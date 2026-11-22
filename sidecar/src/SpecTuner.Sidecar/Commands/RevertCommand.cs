using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>Undo one change, or with <c>all: true</c> "Restore game".</summary>
public sealed class RevertCommand : ICommand
{
    public string Name => "revert";

    public CommandResult Execute(CommandRequest request)
    {
        var all = request.OptionalBool("all");
        var changeId = request.OptionalString("changeId");
        if (all == (changeId is not null))
            throw new SidecarException(ErrorCodes.BadRequest, "The request needs either \"changeId\" or \"all\": true (not both).");

        var facts = GameCommands.RequireInScope(request, allowIfRecorded: true);
        var state = GameState.For(facts.FullPath);
        using var _ = state.Lock();
        GameProcesses.EnsureNotRunning(facts);

        var record = state.LoadRecord();
        var baseline = state.LoadBaseline();
        if (record is null || baseline is null)
        {
            if (!all)
                throw new SidecarException(ErrorCodes.ChangeNotFound, "SpecTuner couldn't find that change.", "No state for this game.");
            return CommandResult.Success("SpecTuner hasn't changed this game, so there is nothing to restore.",
                new { removed = 0, nothingToRestore = true });
        }

        var ledger = Ledger.Load(state, baseline);
        if (!all)
        {
            var entry = ledger.Find(changeId!);
            // The loader alone, with nothing else in place, is the same as restoring the game.
            var loaderOnly = entry is { Kind: ChangeKinds.Loader, IsLive: true } && ledger.Live.All(e => e == entry);
            if (!loaderOnly)
            {
                var reverted = ledger.Revert(changeId!);
                return CommandResult.Success($"Undone: {reverted.Title}.", new { reverted, fullRestore = false });
            }
        }

        var report = GameRestorer.RestoreAll(state, record, baseline, ledger);
        return CommandResult.Success(RestoreSummary(report), new
        {
            fullRestore = true,
            report.Removed,
            report.MatchesBaseline,
            report.LeftInPlace,
            report.LeftInPlaceCount,
            report.ChangedOriginals,
            report.ChangedOriginalsCount,
            report.MissingOriginals,
            report.MissingOriginalsCount,
            report.Truncated,
            report.RevertedChanges,
        });
    }

    private static string RestoreSummary(RestoreReport report)
    {
        if (report.MatchesBaseline)
            return "Your game is back exactly the way SpecTuner first found it.";
        var notes = new List<string>();
        if (report.LeftInPlaceCount > 0)
            notes.Add($"{report.LeftInPlaceCount} file(s) that SpecTuner did not add (probably written by the game itself) were left alone");
        if (report.ChangedOriginalsCount > 0)
            notes.Add($"{report.ChangedOriginalsCount} original game file(s) are different from when SpecTuner first looked, but SpecTuner never edits them (a game update can do this)");
        if (report.MissingOriginalsCount > 0)
            notes.Add($"{report.MissingOriginalsCount} original game file(s) are missing; SpecTuner never deletes them, so a game update or a file check in your game launcher may help");
        return "SpecTuner removed everything it added. " + string.Join(". ", notes) + ".";
    }
}
