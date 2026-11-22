using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>One change that was once in place and is not any more, as offered for "Apply again".</summary>
public sealed record PastChange(
    ChangeEntry Entry, string Reason, DateTime EndedAt, bool CanReapply, string? WhyNot, string? ModName, int? ModBuild)
{
    /// <summary>The JSON item of <c>changes</c>' <c>past</c> list.</summary>
    public Dictionary<string, object?> ToJson()
    {
        var item = new Dictionary<string, object?>
        {
            ["id"] = Entry.Id,
            ["kind"] = Entry.Kind,
            ["title"] = Entry.Title,
            ["description"] = Entry.Description,
            ["createdAt"] = Entry.CreatedAt,
            ["endedAt"] = EndedAt,
            ["reason"] = Reason,
            ["canReapply"] = CanReapply,
        };
        if (!CanReapply)
            item["whyNot"] = WhyNot;
        if (ModName is not null)
            item["mod"] = new { name = ModName, build = ModBuild };
        if (Entry.ReappliedFrom is not null)
            item["reappliedFrom"] = Entry.ReappliedFrom;
        return item;
    }
}

/// <summary>
/// Past changes from the ledger (undone, removed automatically, or replaced by a newer build of
/// the same mod) and from history.json (earlier rounds, archived by "Restore game").
/// Only the kinds a player asks for (quality, mod); the loader and helper are set up automatically.
/// </summary>
public static class PastChanges
{
    public const int ListCap = 50;

    private static readonly string[] PlayerKinds = [ChangeKinds.Quality, ChangeKinds.Mod];

    /// <summary>Every entry SpecTuner has a record of, current ledger and history, oldest first.</summary>
    public static List<ChangeEntry> AllEntries(GameState state, out List<ChangeEntry> ledgerEntries)
    {
        ledgerEntries = AtomicFile.ReadJson<LedgerFileShape>(state.ChangesPath)?.Changes ?? [];
        return ChangeHistory.Load(state).Changes.Concat(ledgerEntries)
            .OrderBy(e => ChangeHistory.IdNumber(e.Id))
            .ToList();
    }

    /// <summary>
    /// Newest first. A past change that was applied again is left out while that newer copy is in
    /// place or is itself offered: the newest copy stands for it, so the same change is never offered twice.
    /// </summary>
    public static List<PastChange> List(GameState state)
    {
        var all = AllEntries(state, out var ledger);
        var result = new List<PastChange>();
        var represented = new HashSet<string>();
        foreach (var entry in Enumerable.Reverse(all))
        {
            var past = Describe(state, all, ledger, entry);
            var omitted = represented.Contains(entry.Id);
            var inPlace = entry.IsLive && past is null;
            if (entry.ReappliedFrom is not null && (inPlace || omitted || past is { CanReapply: true }))
                represented.Add(entry.ReappliedFrom);
            if (past is not null && !omitted)
                result.Add(past);
        }
        return result;
    }

    /// <summary>A change in place that was applied again from this one (directly or through a chain of reapplies), or null.</summary>
    public static ChangeEntry? InPlaceCopyOf(GameState state, string id)
    {
        var all = AllEntries(state, out var ledger);
        var ids = new HashSet<string> { id };
        ChangeEntry? copy = null;
        foreach (var entry in all.Where(e => e.ReappliedFrom is not null && ChangeHistory.IdNumber(e.Id) > ChangeHistory.IdNumber(id)))
        {
            if (!ids.Contains(entry.ReappliedFrom!))
                continue;
            ids.Add(entry.Id);
            if (entry.IsLive && Describe(state, all, ledger, entry) is null)
                copy = entry;
        }
        return copy;
    }

    /// <summary>The entry with this id, current ledger or history, or null.</summary>
    public static ChangeEntry? Find(GameState state, string id) => AllEntries(state, out _).LastOrDefault(e => e.Id == id);

    /// <summary>The entry as a past change, or null when it is in place (or not a player-facing kind).</summary>
    public static PastChange? Describe(GameState state, string id)
    {
        var all = AllEntries(state, out var ledger);
        var entry = all.LastOrDefault(e => e.Id == id);
        return entry is null ? null : Describe(state, all, ledger, entry);
    }

    private static PastChange? Describe(GameState state, List<ChangeEntry> all, List<ChangeEntry> ledger, ChangeEntry entry)
    {
        if (!PlayerKinds.Contains(entry.Kind) || entry.Status == ChangeStatus.Pending)
            return null;

        string reason;
        DateTime endedAt;
        if (entry.IsLive)
        {
            var successor = ledger.Contains(entry) ? Ledger.ReplacedBy(ledger, entry) : null;
            if (successor is null)
                return null; // in place
            reason = ChangeEndReasons.Replaced;
            endedAt = successor.CreatedAt;
        }
        else
        {
            reason = entry.EndReason ?? ChangeEndReasons.Undone; // entries from before reasons were recorded were undone or restored
            endedAt = entry.RevertedAt ?? entry.CreatedAt;
        }

        var (modName, build) = ModOf(all, entry);

        var whyNot = WhyNot(state, entry, reason, modName);
        return new PastChange(entry, reason, endedAt, whyNot is null, whyNot, modName, build);
    }

    /// <summary>For a mod change: the mod's name and which build of that mod this is (1 for the first), counted over the whole record.</summary>
    public static (string? Name, int? Build) ModOf(List<ChangeEntry> all, ChangeEntry entry)
    {
        if (entry.Kind != ChangeKinds.Mod)
            return (null, null);
        var name = entry.Files.Select(ModRecord.NameFromFile).FirstOrDefault(n => n is not null);
        if (name is null)
            return (null, null);
        var build = all.Count(e => e.Kind == ChangeKinds.Mod && ChangeHistory.IdNumber(e.Id) <= ChangeHistory.IdNumber(entry.Id)
                                   && e.Files.Select(ModRecord.NameFromFile).Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)));
        return (name, build);
    }

    private static string? WhyNot(GameState state, ChangeEntry entry, string reason, string? modName)
    {
        if (reason == ChangeEndReasons.FailedStart)
            return "The game didn't start with this change, so SpecTuner won't put it back.";
        if (reason == ChangeEndReasons.FailedTest)
            return "This mod didn't load cleanly when SpecTuner tested it, so it can't be put back. Ask for the change again to get a fixed version.";
        if (entry.Kind == ChangeKinds.Quality && entry.Settings is null)
            return "This change was made by an older version of SpecTuner that didn't record its settings, so it can't be put back. You can ask for the same settings again.";
        if (entry.Kind == ChangeKinds.Mod && (modName is null || BuildPluginCommand.SavedSources(state, modName, entry.Id) is null))
            return "SpecTuner no longer has the code for this mod, so it can't build it again. You can ask for the change again.";
        return null;
    }
}
