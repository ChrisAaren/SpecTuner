using System.Globalization;
using SpecTuner.Sidecar.Benchmark;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Logs;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>One live mod as seen in one run's BepInEx log.</summary>
public sealed record ModRunStatus(string Name, ModRecord Record, bool Loaded, List<string> Errors)
{
    public string Plugin => $"{Record.PluginName} {Record.Version}";
}

/// <summary>
/// What every command that starts the game shares (launch-and-benchmark and build-plugin's test run):
/// reading this run's log, telling which mods loaded and which errors are theirs, and reverting after a failed start.
/// </summary>
public static class GameRun
{
    private const int MaxLogErrors = 10;

    /// <summary>BepInEx log lines, if the log was written during this run; all lines, its error lines (last 10, cleaned) and the loaded plugins.</summary>
    public static (List<string> Plugins, List<string> Errors, List<string> Lines) ReadThisRunsLog(string gameRoot, DateTime startedAt)
    {
        var path = GameTree.Full(gameRoot, "BepInEx/LogOutput.log");
        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedAt.AddSeconds(-2))
            return ([], [], []);
        var lines = LogReader.ReadLines(path);
        var errors = lines.Where(LogReader.IsError).TakeLast(MaxLogErrors).Select(LogReader.Clean).ToList();
        return (LogReader.LoadedPlugins(lines), errors, lines);
    }

    /// <summary>
    /// Unity's Player.log, if written during this run. BepInEx 5 does not copy Unity's own log
    /// (exceptions thrown in a plugin's Awake or Update, with their stack traces) into
    /// LogOutput.log by default (WriteUnityLog = false), but Player.log has them.
    /// </summary>
    public static List<string> ReadThisRunsPlayerLog(Games.GameFacts facts, DateTime startedAt)
    {
        var path = ReadLogsCommand.PlayerLogPath(facts);
        if (path is null || !File.Exists(path) || File.GetLastWriteTimeUtc(path) < startedAt.AddSeconds(-2))
            return [];
        try
        {
            return LogReader.ReadLines(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Did BepInEx load this mod, and every error entry in this run's logs (BepInEx's, then Unity's
    /// Player.log) that belongs to it: an error it logged itself, an error whose entry names its
    /// assembly or one of its types (stack traces), or an error line naming "[Name Version]"
    /// (BepInEx's "Error loading [Name Version]"). Errors are oldest first, without repeats.
    /// </summary>
    public static ModRunStatus Status(string name, ModRecord record, List<string> loadedPlugins, List<string> lines, List<string> playerLines)
    {
        var plugin = $"{record.PluginName} {record.Version}";
        var markers = new List<string> { record.AssemblyName };
        markers.AddRange(record.Types);
        var errors = new List<string>();
        foreach (var log in new[] { lines, playerLines })
        {
            errors.AddRange(LogReader.ErrorsMentioning(log, record.PluginName, markers, int.MaxValue));
            errors.AddRange(log.Where(l => LogReader.IsError(l) && l.Contains($"[{plugin}]", StringComparison.Ordinal)).Select(LogReader.Clean));
        }
        return new ModRunStatus(name, record, loadedPlugins.Contains(plugin), errors.Distinct().ToList());
    }

    /// <summary>
    /// For every live mod: did BepInEx load it, and which errors in this run's log belong to it (last 5).
    /// BepInEx catches exceptions thrown while a plugin loads or patches, so the game still
    /// starts; these lists are how a broken mod becomes visible.
    /// </summary>
    public static (List<object> Mods, Dictionary<string, List<string>> PluginErrors) ModReport(InstalledGame game, List<string> loadedPlugins, List<string> lines,
        List<string> playerLines)
    {
        var mods = new List<object>();
        var errors = new Dictionary<string, List<string>>();
        var names = game.Ledger.Live.Where(e => e.Kind == ChangeKinds.Mod)
            .SelectMany(e => e.Files.Select(ModRecord.NameFromFile))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var record = ModRecord.Load(game.State, name);
            if (record is null)
                continue;
            var status = Status(name, record, loadedPlugins, lines, playerLines);
            var found = status.Errors.TakeLast(5).ToList();
            if (found.Count > 0)
                errors[record.PluginName] = found;
            mods.Add(new { name, plugin = status.Plugin, loaded = status.Loaded, errorCount = found.Count });
        }
        return (mods, errors);
    }

    /// <summary>
    /// The game did not start, crashed, exited or timed out: undo every change made since the
    /// game last started (a full restore if the loader itself was never verified) and say so plainly.
    /// </summary>
    public static CommandResult FailedStart(InstalledGame game, string runId, LaunchOutcome outcome, List<string> logErrors,
        Dictionary<string, List<string>> pluginErrors)
    {
        var unverified = game.Ledger.Live.Where(e => !e.Verified).Select(e => new { id = e.Id, kind = e.Kind, title = e.Title }).ToList();
        var fullRestore = unverified.Any(e => e.kind == ChangeKinds.Loader);
        if (unverified.Count > 0)
            GameRestorer.RevertAllUnverified(game.State, game.Record, game.Baseline, game.Ledger);

        var what = outcome.Failure switch
        {
            "crashed" => "The game closed with an error",
            "exited" => "The game closed by itself before the test finished",
            "timeout" => "The game didn't finish the test in time",
            _ => "The game couldn't be started",
        };
        var message = unverified.Count == 0
            ? $"{what}. SpecTuner didn't undo anything, because all of its changes have worked before."
            : fullRestore
                ? $"{what}. SpecTuner removed everything it had added, so your game is back the way it was."
                : $"{what}. SpecTuner undid the changes made since the game last worked: {string.Join(", ", unverified.Select(e => e.title))}.";

        return CommandResult.Failure(ErrorCodes.GameFailedToStart, message,
            $"failure={outcome.Failure}; exitCode={outcome.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"}",
            new
            {
                runId,
                reason = outcome.Failure,
                exitCode = outcome.ExitCode,
                reverted = unverified,
                fullRestore,
                logErrors,
                pluginErrors,
                durationSeconds = Math.Round(outcome.DurationSeconds, 1),
            });
    }
}
