using System.Globalization;
using System.Text.Json;
using SpecTuner.Sidecar.Benchmark;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Quality;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>
/// Starts the game, lets the probe measure frame times, closes the game and returns the numbers.
/// A successful start verifies every live change; a failed one reverts every unverified change.
/// </summary>
public sealed class LaunchAndBenchmarkCommand : ICommand
{
    public string Name => "launch-and-benchmark";

    public CommandResult Execute(CommandRequest request)
    {
        var label = request.OptionalString("label") ?? "run";
        var warmup = request.OptionalNumber("warmupSeconds", 10, 0, 120);
        var measure = request.OptionalNumber("measureSeconds", 20, 1, 300);
        var startup = request.OptionalNumber("startupTimeoutSeconds", 90, 5, 600);
        var trial = ReadTrial(request);

        var facts = GameCommands.RequireInScope(request);
        var state = GameState.For(facts.FullPath);
        using var _ = state.Lock();
        GameProcesses.EnsureNotRunning(facts);

        var game = GameSetup.EnsureInstalled(facts, state);
        var history = BenchmarkHistory.Load(state);
        var runId = history.NextRunId();
        var startedAt = DateTime.UtcNow;

        var outcome = GameLauncher.Run(facts, new BenchmarkRequest(runId, warmup, measure, startup, trial));
        var (plugins, logErrors, logLines) = GameRun.ReadThisRunsLog(facts.FullPath, startedAt);
        var (mods, pluginErrors) = GameRun.ModReport(game, plugins, logLines, GameRun.ReadThisRunsPlayerLog(facts, startedAt));

        if (outcome.Result is not { } result)
            return Failed(game, history, runId, label, outcome, logErrors, pluginErrors);

        var frameMs = result.GetProperty("frameMs");
        var run = new BenchmarkRun
        {
            RunId = runId,
            Label = label,
            At = startedAt,
            Ok = true,
            AvgFps = Round(result.GetProperty("avgFps").GetDouble()),
            Low1PctFps = Round(result.GetProperty("low1PctFps").GetDouble()),
            P95Ms = Round(frameMs.GetProperty("p95").GetDouble()),
            Frames = result.GetProperty("frames").GetInt32(),
            Resolution = result.GetProperty("resolution").GetString(),
            Trial = trial is not null,
        };
        var previous = history.LastSuccessfulOfKind(run.Trial);
        var baseline = history.BaselineOfKind(run.Trial);
        history.Runs.Add(run);
        history.Save(state);
        game.Ledger.MarkAllLiveVerified();

        var overrides = result.GetProperty("overrides");
        var toPrevious = previous is null ? null : BenchmarkHistory.Compare(run, previous);
        var toBaseline = baseline is null ? null : BenchmarkHistory.Compare(run, baseline);

        // An older probe that does not know trials returns no trial block; then only A numbers are reported.
        var trialResult = trial is not null && result.TryGetProperty("trial", out var trialJson) && trialJson.ValueKind == JsonValueKind.Object
            ? TrialResult(trialJson)
            : null;
        var windowSpreadPct = trialResult is not null
            ? trialResult.ASpreadPct
            : result.TryGetProperty("subWindowFps", out var sub) && sub.ValueKind == JsonValueKind.Array
                ? Verdict.SpreadPct(sub.EnumerateArray().Select(v => v.GetDouble()).ToList())
                : null;

        var seconds = Round(result.GetProperty("seconds").GetDouble());
        var summary = $"Measured {run.AvgFps.ToString("0.0", CultureInfo.InvariantCulture)} frames per second on average " +
                      $"(slowest 1%: {run.Low1PctFps.ToString("0.0", CultureInfo.InvariantCulture)}) over {seconds.ToString("0.#", CultureInfo.InvariantCulture)} seconds" +
                      (trialResult is null ? "." : " of normal-settings windows.");
        if (trialResult is not null)
            summary += $" Trying {string.Join(", ", trial!.Settings.Keys)}: {trialResult.Reason}";
        else if (toPrevious is not null)
            summary += " " + Verdict.DescribeLaunchChange(toPrevious.AvgFpsDeltaPct, "the previous test");

        return CommandResult.Success(summary, new
        {
            label,
            runId,
            avgFps = run.AvgFps,
            low1PctFps = run.Low1PctFps,
            frameMs,
            frames = run.Frames,
            seconds,
            windowSpreadPct,
            resolution = run.Resolution,
            scene = result.TryGetProperty("scene", out var scene) ? scene.GetString() : null,
            settings = result.GetProperty("settings"),
            hardware = result.GetProperty("hardware"),
            overrides = new
            {
                requested = overrides.GetProperty("requested"),
                inEffect = overrides.GetProperty("inEffect"),
                notInEffect = overrides.GetProperty("inEffect").EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.False).Select(p => p.Name).ToList(),
                reapplied = overrides.GetProperty("reapplied"),
                suspendedForMeasurement = overrides.GetProperty("suspendedForMeasurement"),
            },
            frameCap = result.GetProperty("frameCap"),
            loadedPlugins = plugins,
            logErrors,
            mods,
            pluginErrors,
            trial = trialResult?.Data,
            comparedToPrevious = toPrevious,
            comparedToBaseline = toBaseline,
            durationSeconds = Math.Round(outcome.DurationSeconds, 1),
        });
    }

    private const int MaxPairs = 10;

    /// <summary>Reads and validates trySettings, pairs, windowSeconds and settleSeconds. Null when there is no trial.</summary>
    public static TrialRequest? ReadTrial(CommandRequest request)
    {
        if (!request.Root.TryGetProperty("trySettings", out var settings) || settings.ValueKind == JsonValueKind.Null)
            return null;
        return new TrialRequest(
            QualitySettingsFile.ValidateTrial(settings, null),
            (int)request.OptionalNumber("pairs", 4, 2, MaxPairs),
            request.OptionalNumber("windowSeconds", 5, 2, 60),
            request.OptionalNumber("settleSeconds", 2, 0.5, 30));
    }

    private sealed record TrialOutcome(object Data, string Reason, double? ASpreadPct);

    /// <summary>Turns the probe's per-window numbers into paired deltas and a verdict.</summary>
    private static TrialOutcome TrialResult(JsonElement trial)
    {
        var windows = trial.GetProperty("windows").EnumerateArray().ToList();
        var aFps = windows.Where(w => w.GetProperty("state").GetString() == "A").Select(w => w.GetProperty("avgFps").GetDouble()).ToList();
        var bFps = windows.Where(w => w.GetProperty("state").GetString() == "B").Select(w => w.GetProperty("avgFps").GetDouble()).ToList();
        var deltas = Verdict.PairDeltasPct(aFps, bFps);
        var inEffect = trial.GetProperty("inEffect").EnumerateObject().ToList();
        var confirmed = inEffect.Where(p => p.Value.ValueKind == JsonValueKind.True).Select(p => p.Name).ToList();
        var notConfirmed = inEffect.Where(p => p.Value.ValueKind != JsonValueKind.True).Select(p => p.Name).ToList();
        var verdict = Verdict.Decide(deltas, confirmed.Count, inEffect.Count);

        var data = new
        {
            settings = trial.GetProperty("settings"),
            pairs = deltas.Count,
            windowSeconds = trial.GetProperty("windowSeconds").GetDouble(),
            settleSeconds = trial.GetProperty("settleSeconds").GetDouble(),
            a = trial.GetProperty("a"),
            b = trial.GetProperty("b"),
            pairResults = deltas.Select((d, i) => new { aFps = Round(aFps[i]), bFps = Round(bFps[i]), deltaPct = d }).ToList(),
            meanDeltaPct = deltas.Count == 0 ? 0 : Math.Round(deltas.Average(), 2),
            confirmed,
            notConfirmed,
            verdict = verdict.Verdict,
            reason = verdict.Reason,
        };
        return new TrialOutcome(data, verdict.Reason, Verdict.SpreadPct(aFps));
    }

    private static CommandResult Failed(InstalledGame game, BenchmarkHistory history, string runId, string label, LaunchOutcome outcome, List<string> logErrors,
        Dictionary<string, List<string>> pluginErrors)
    {
        history.Runs.Add(new BenchmarkRun { RunId = runId, Label = label, At = DateTime.UtcNow, Ok = false, Failure = outcome.Failure });
        history.Save(game.State);
        return GameRun.FailedStart(game, runId, outcome, logErrors, pluginErrors);
    }

    private static double Round(double value) => Math.Round(value, 2);
}
