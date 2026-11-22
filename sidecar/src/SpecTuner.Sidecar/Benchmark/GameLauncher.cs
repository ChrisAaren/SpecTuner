using System.Diagnostics;
using System.Text.Json;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Benchmark;

public sealed record TrialRequest(SortedDictionary<string, object> Settings, int Pairs, double WindowSeconds, double SettleSeconds)
{
    /// <summary>Time the alternating windows take, settle periods included.</summary>
    public double TotalSeconds => 2 * Pairs * (WindowSeconds + SettleSeconds);
}

/// <summary>With <paramref name="Trial"/> set, MeasureSeconds is not used: the probe measures alternating A/B windows instead.</summary>
public sealed record BenchmarkRequest(string RunId, double WarmupSeconds, double MeasureSeconds, double StartupSeconds, TrialRequest? Trial = null);

public sealed class LaunchOutcome
{
    /// <summary>The probe's result file, parsed. Null when the run failed.</summary>
    public JsonElement? Result { get; init; }
    /// <summary>"exited", "crashed", "timeout" or "start-failed" when the run failed.</summary>
    public string? Failure { get; init; }
    public int? ExitCode { get; init; }
    public double DurationSeconds { get; init; }
}

/// <summary>Starts the game for one benchmark run and waits for the probe's result.</summary>
public static class GameLauncher
{
    private static readonly TimeSpan QuitGrace = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static string ResultPath(string gameRoot, string runId) =>
        GameTree.Full(gameRoot, $"{OurFiles.ProbeOutputDir}/benchmark-{runId}.json");

    public static LaunchOutcome Run(GameFacts facts, BenchmarkRequest request)
    {
        var root = facts.FullPath;
        var requestPath = GameTree.Full(root, OurFiles.BenchmarkRequestFile);
        var resultPath = ResultPath(root, request.RunId);
        GameTree.DeleteFile(resultPath);

        var clock = Stopwatch.StartNew();
        try
        {
            AtomicFile.WriteJson(requestPath, new
            {
                runId = request.RunId,
                warmupSeconds = request.WarmupSeconds,
                measureSeconds = request.MeasureSeconds,
                quitWhenDone = true,
                createdAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                trial = request.Trial is null ? null : new
                {
                    settings = request.Trial.Settings,
                    pairs = request.Trial.Pairs,
                    windowSeconds = request.Trial.WindowSeconds,
                    settleSeconds = request.Trial.SettleSeconds,
                },
            });

            var platform = Platforms.Current;
            // Windows: a kill-on-close job object. Linux/macOS: signal handlers plus a guard file.
            using var job = platform.Processes.CreateGuard(GameState.For(root).RunningGamePath);
            // Windows: the plain program (winhttp.dll loads the loader). Linux/macOS: what run_bepinex.sh would run.
            var spec = platform.Loader.WithLoader(facts, Environment.GetEnvironmentVariable);
            var start = new ProcessStartInfo(spec.FileName)
            {
                WorkingDirectory = root,
                UseShellExecute = false,
                // Keep the game off our stdout, which carries the JSON envelope.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
            };
            foreach (var argument in spec.Arguments)
                start.ArgumentList.Add(argument);
            foreach (var (key, value) in spec.Environment)
                start.Environment[key] = value;
            Process process;
            try
            {
                process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                Console.Error.WriteLine($"Could not start the game: {ex.Message}");
                return new LaunchOutcome { Failure = "start-failed", DurationSeconds = clock.Elapsed.TotalSeconds };
            }

            using (process)
            {
                job.Add(process);
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var measuring = request.Trial?.TotalSeconds ?? request.MeasureSeconds;
                var deadline = TimeSpan.FromSeconds(request.StartupSeconds + request.WarmupSeconds + measuring);
                JsonElement? result = null;
                while (true)
                {
                    result = TryReadResult(resultPath);
                    if (result is not null)
                        break;
                    if (process.HasExited)
                    {
                        result = TryReadResult(resultPath);
                        if (result is not null)
                            break;
                        var code = process.ExitCode;
                        return new LaunchOutcome
                        {
                            Failure = code == 0 ? "exited" : "crashed", ExitCode = code, DurationSeconds = clock.Elapsed.TotalSeconds,
                        };
                    }
                    if (clock.Elapsed > deadline)
                    {
                        Stop(process);
                        return new LaunchOutcome { Failure = "timeout", DurationSeconds = clock.Elapsed.TotalSeconds };
                    }
                    Thread.Sleep(PollInterval);
                }

                // The probe asks the game to quit after writing the result; give it a moment, then close it.
                if (!process.WaitForExit(QuitGrace))
                    Stop(process);
                return new LaunchOutcome
                {
                    Result = result,
                    ExitCode = process.HasExited ? process.ExitCode : null,
                    DurationSeconds = clock.Elapsed.TotalSeconds,
                };
            }
        }
        finally
        {
            GameTree.DeleteFile(requestPath);
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static JsonElement? TryReadResult(string path)
    {
        if (!File.Exists(path))
            return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null; // still being written
        }
    }
}
