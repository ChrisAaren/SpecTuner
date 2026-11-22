using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>The list of changes, newest first, for the UI's change list and Undo buttons, and the past changes that can be applied again.</summary>
public sealed class ChangesCommand : ICommand
{
    private const int ListCap = 100;

    public string Name => "changes";

    public CommandResult Execute(CommandRequest request)
    {
        var facts = GameCommands.RequireInScope(request, allowIfRecorded: true);
        var state = GameState.For(facts.FullPath);
        var baseline = state.LoadBaseline();
        var entries = baseline is null ? [] : Ledger.Load(state, baseline).Entries;

        var newestFirst = entries.Reverse().ToList();
        var live = entries.Count(e => e.IsLive);
        var past = PastChanges.List(state);
        // Which build of which mod each listed mod change is, so the UI can tell two builds of one mod apart.
        var all = PastChanges.AllEntries(state, out _);
        var mods = new Dictionary<string, object>();
        foreach (var entry in newestFirst.Take(ListCap))
        {
            var (name, build) = PastChanges.ModOf(all, entry);
            if (name is not null)
                mods[entry.Id] = new { name, build };
        }
        return CommandResult.Success(
            live == 0 ? "SpecTuner has no changes in place for this game." : $"SpecTuner has {live} change{(live == 1 ? "" : "s")} in place for this game.",
            new
            {
                changes = newestFirst.Take(ListCap),
                total = newestFirst.Count,
                truncated = newestFirst.Count > ListCap,
                canRestore = live > 0,
                mods,
                past = past.Take(PastChanges.ListCap).Select(p => p.ToJson()),
                pastTotal = past.Count,
                pastTruncated = past.Count > PastChanges.ListCap,
            });
    }
}
