using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Logs;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Matching lines from the last run's BepInEx log or Unity Player.log, errors always included.</summary>
public sealed class ReadLogsCommand : ICommand
{
    public string Name => "read-logs";

    public CommandResult Execute(CommandRequest request)
    {
        var source = request.OptionalString("source") ?? "bepinex";
        if (source is not ("bepinex" or "player"))
            throw new SidecarException(ErrorCodes.BadRequest, "\"source\" must be \"bepinex\" or \"player\".");
        var filter = request.OptionalString("filter");
        var tail = (int)request.OptionalNumber("tail", 60, 1, LogReader.MaxTail);

        var facts = GameCommands.RequireInScope(request, allowIfRecorded: true);
        var path = source == "bepinex" ? GameTree.Full(facts.FullPath, "BepInEx/LogOutput.log") : PlayerLogPath(facts);
        if (path is null || !File.Exists(path))
            throw new SidecarException(ErrorCodes.LogNotFound,
                source == "bepinex"
                    ? "There is no mod loader log yet. It appears after the game has been started with SpecTuner installed."
                    : "There is no game log yet. It appears after the game has been started at least once.");

        var excerpt = LogReader.Filter(LogReader.ReadLines(path), filter, tail);
        return CommandResult.Success(
            $"{excerpt.Lines.Count} matching line(s) and {excerpt.ErrorCount} error line(s) in the last {(source == "bepinex" ? "mod loader" : "game")} log.",
            new
            {
                source,
                file = Path.GetFileName(path),
                lastWritten = File.GetLastWriteTimeUtc(path),
                excerpt.TotalLines,
                excerpt.Lines,
                excerpt.Errors,
                excerpt.ErrorCount,
                excerpt.Truncated,
            });
    }

    /// <summary>Where Unity writes Player.log on this platform (Windows: %USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;\; see <see cref="Platform.UserFolders.PlayerLogPath"/>).</summary>
    public static string? PlayerLogPath(GameFacts facts)
    {
        var product = facts.Product ?? facts.Name;
        if (facts.Company is null || product is null)
            return null;
        return Platform.Platforms.Current.Folders.PlayerLogPath(facts.Company, product);
    }
}
