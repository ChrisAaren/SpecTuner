using System.Text.Json;
using System.Text.Json.Serialization;
using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.State;

public static class ChangeKinds
{
    public const string Loader = "loader";
    public const string Probe = "probe";
    public const string Quality = "quality";
    public const string Mod = "mod";
}

public static class ChangeStatus
{
    /// <summary>Begun but not yet committed (e.g. the sidecar stopped halfway). Treated as live.</summary>
    public const string Pending = "pending";
    public const string Active = "active";
    public const string Reverted = "reverted";
}

public sealed class ChangeEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = ChangeStatus.Pending;
    public bool Verified { get; set; }
    public List<string> Files { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? RevertedAt { get; set; }

    /// <summary>Why it stopped being in place (see <see cref="ChangeEndReasons"/>). Null while live, and on entries from before this was recorded.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EndReason { get; set; }

    /// <summary>For a quality change: the "settings" object it applied, as set-quality received it, so it can be applied again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Settings { get; set; }

    /// <summary>The past change this one applied again (reapply).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ReappliedFrom { get; set; }

    [JsonIgnore] public bool IsLive => Status != ChangeStatus.Reverted;
}

/// <summary>Why a change stopped being in place.</summary>
public static class ChangeEndReasons
{
    /// <summary>The player (or the agent) undid it.</summary>
    public const string Undone = "undone";
    /// <summary>"Restore game" removed it with everything else.</summary>
    public const string Restored = "restored";
    /// <summary>The game failed to start with it, so it was reverted automatically.</summary>
    public const string FailedStart = "failed_start";
    /// <summary>A mod that did not load or logged errors in build-plugin's test launch.</summary>
    public const string FailedTest = "failed_test";
    /// <summary>A mod build replaced by a newer build of the same mod.</summary>
    public const string Replaced = "replaced";
}

/// <summary>
/// <c>history.json</c>: the ledger entries of earlier rounds, archived by "Restore game" (which
/// clears the ledger itself). Kept so past changes can be shown and applied again, and so change
/// ids are never reused.
/// </summary>
public sealed class ChangeHistory
{
    public const int Cap = 1000;

    public List<ChangeEntry> Changes { get; set; } = [];

    public static ChangeHistory Load(GameState state) => AtomicFile.ReadJson<ChangeHistory>(state.HistoryPath) ?? new ChangeHistory();

    public void Save(GameState state)
    {
        if (Changes.Count > Cap)
            Changes.RemoveRange(0, Changes.Count - Cap);
        AtomicFile.WriteJson(state.HistoryPath, this);
    }

    public static int IdNumber(string id) => int.TryParse(id.AsSpan(1), out var n) ? n : 0;
}

/// <summary>
/// The ordered list of changes in <c>changes.json</c>. Every change goes
/// Begin (snapshot) -> apply (caller writes files) -> Commit. Undo is
/// <see cref="Revert"/>. Original game files are never touched: any attempt
/// to overwrite or delete a baseline file throws.
/// The loader change is removed only by a full restore (see GameRestorer).
/// </summary>
public sealed class Ledger
{
    private sealed class LedgerFile
    {
        public List<ChangeEntry> Changes { get; set; } = [];
    }

    private readonly GameState _state;
    private readonly Baseline _baseline;
    private readonly List<ChangeEntry> _entries;

    private Ledger(GameState state, Baseline baseline, List<ChangeEntry> entries)
    {
        _state = state;
        _baseline = baseline;
        _entries = entries;
    }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<ChangeEntry> Entries => _entries;

    public IEnumerable<ChangeEntry> Live => _entries.Where(e => e.IsLive);

    public static Ledger Load(GameState state, Baseline baseline) =>
        new(state, baseline, AtomicFile.ReadJson<LedgerFile>(state.ChangesPath)?.Changes ?? []);

    /// <summary>
    /// The later live mod change that replaced this mod build (same dll), or null. The replaced
    /// build stays live in the ledger, because undoing the newer build brings it back.
    /// </summary>
    public ChangeEntry? ReplacedBy(ChangeEntry entry) => ReplacedBy(_entries, entry);

    public static ChangeEntry? ReplacedBy(IReadOnlyList<ChangeEntry> entries, ChangeEntry entry)
    {
        if (entry.Kind != ChangeKinds.Mod || !entry.IsLive)
            return null;
        var index = -1;
        for (var i = 0; i < entries.Count; i++)
            if (ReferenceEquals(entries[i], entry))
                index = i;
        return entries.Skip(index + 1).FirstOrDefault(e => e.IsLive && e.Kind == ChangeKinds.Mod && e.Files.Intersect(entry.Files, GameTree.PathComparer).Any());
    }

    public ChangeEntry? Find(string id) => _entries.FirstOrDefault(e => e.Id == id);

    public string SnapshotDir(string changeId) => Path.Combine(_state.SnapshotsDir, changeId);

    public ChangeEntry Begin(string kind, string title, string description)
    {
        var entry = new ChangeEntry
        {
            Id = NextId(),
            Kind = kind,
            Title = title,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            Status = ChangeStatus.Pending,
        };
        Snapshot.Take(_state.GamePath, SnapshotDir(entry.Id));
        _entries.Add(entry);
        Save();
        return entry;
    }

    public void Commit(ChangeEntry entry, IEnumerable<string> files)
    {
        entry.Files = files.Distinct(_baseline.Comparer).ToList();
        entry.Status = ChangeStatus.Active;
        Save();
    }

    /// <summary>Apply failed: put back the files the change touched and drop it from the ledger.</summary>
    public void Abandon(ChangeEntry entry, IEnumerable<string> touchedFiles)
    {
        RestoreFiles(entry, touchedFiles.ToList());
        _entries.Remove(entry);
        var snapshot = SnapshotDir(entry.Id);
        if (Directory.Exists(snapshot))
            Directory.Delete(snapshot, recursive: true);
        Save();
    }

    /// <summary>Undo one change: put the files it touched back as they were before it.</summary>
    public ChangeEntry Revert(string changeId, string reason = ChangeEndReasons.Undone)
    {
        var entry = Find(changeId)
            ?? throw new SidecarException(ErrorCodes.ChangeNotFound, "SpecTuner couldn't find that change.", $"No change with id {changeId}.");
        if (!entry.IsLive)
            throw new SidecarException(ErrorCodes.ChangeNotActive, $"\"{entry.Title}\" was already undone.");

        if (entry.Kind == ChangeKinds.Loader)
            throw new SidecarException(ErrorCodes.RevertBlocked,
                "The mod loader can only be removed with \"Restore game\", which removes everything SpecTuner added.");

        if (entry.Kind == ChangeKinds.Probe)
        {
            var dependents = Live.Where(e => e.Kind is not (ChangeKinds.Loader or ChangeKinds.Probe)).ToList();
            if (dependents.Count > 0)
                throw new SidecarException(ErrorCodes.RevertBlocked,
                    $"\"{entry.Title}\" is needed by your other changes. Undo those first, or use \"Restore game\".",
                    "Depends on it: " + string.Join(", ", dependents.Select(e => e.Id)));
        }

        var index = _entries.IndexOf(entry);
        var later = Live
            .Where(e => _entries.IndexOf(e) > index && e.Files.Intersect(entry.Files, _baseline.Comparer).Any())
            .ToList();
        if (later.Count > 0)
            throw new SidecarException(ErrorCodes.RevertBlocked,
                $"Please undo {string.Join(" and ", later.Select(e => $"\"{e.Title}\""))} first, because it changed the same files.",
                "Later changes touching the same files: " + string.Join(", ", later.Select(e => e.Id)));

        RestoreFiles(entry, entry.Files);
        entry.Status = ChangeStatus.Reverted;
        entry.RevertedAt = DateTime.UtcNow;
        entry.EndReason = reason;
        Save();
        return entry;
    }

    /// <summary>The game started successfully with this change in place.</summary>
    public void MarkVerified(string changeId)
    {
        var entry = Find(changeId)
            ?? throw new SidecarException(ErrorCodes.ChangeNotFound, "SpecTuner couldn't find that change.", $"No change with id {changeId}.");
        entry.Verified = true;
        Save();
    }

    public void MarkAllLiveVerified()
    {
        foreach (var entry in Live)
            entry.Verified = true;
        Save();
    }

    /// <summary>Marks every live change reverted without touching files. Used by the full restore, which removes the files itself.</summary>
    public IReadOnlyList<ChangeEntry> MarkAllReverted(string reason = ChangeEndReasons.Restored)
    {
        var live = Live.ToList();
        var replacedBy = live.ToDictionary(e => e, e => ReplacedBy(e));
        foreach (var entry in live)
        {
            // A mod build that a newer build had already replaced ended then, not now.
            var successor = replacedBy[entry];
            entry.Status = ChangeStatus.Reverted;
            entry.RevertedAt = successor?.CreatedAt ?? DateTime.UtcNow;
            entry.EndReason = successor is null ? reason : ChangeEndReasons.Replaced;
        }
        Save();
        return live;
    }

    private void RestoreFiles(ChangeEntry entry, IReadOnlyCollection<string> files)
    {
        var originals = files.Where(_baseline.Contains).ToList();
        if (originals.Count > 0)
            throw new InvalidOperationException($"Refusing to touch original game files: {string.Join(", ", originals)}");

        var snapshotDir = SnapshotDir(entry.Id);
        var snapshot = Snapshot.Load(snapshotDir);
        var root = _state.GamePath;
        foreach (var relative in files)
        {
            var target = GameTree.Full(root, relative);
            if (snapshot.Has(relative, _baseline.Comparer))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Snapshot.StoredFile(snapshotDir, relative), target, overwrite: true);
            }
            else
            {
                GameTree.DeleteFile(target);
                GameTree.RemoveEmptyParents(root, relative, _baseline);
            }
        }
    }

    private string NextId()
    {
        // Ids continue across "Restore game": archived entries keep theirs (see ChangeHistory).
        var max = _entries.Select(e => ChangeHistory.IdNumber(e.Id))
            .Concat(ChangeHistory.Load(_state).Changes.Select(e => ChangeHistory.IdNumber(e.Id)))
            .DefaultIfEmpty(0).Max();
        return $"c{max + 1:D4}";
    }

    private void Save() => AtomicFile.WriteJson(_state.ChangesPath, new LedgerFile { Changes = _entries });
}
