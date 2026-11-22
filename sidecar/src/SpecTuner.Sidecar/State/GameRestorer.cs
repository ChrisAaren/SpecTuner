namespace SpecTuner.Sidecar.State;

public sealed class VerifyReport
{
    public const int ListCap = 50;

    public bool MatchesBaseline { get; init; }
    public List<string> LeftInPlace { get; init; } = [];
    public int LeftInPlaceCount { get; init; }
    public List<string> ChangedOriginals { get; init; } = [];
    public int ChangedOriginalsCount { get; init; }
    public List<string> MissingOriginals { get; init; } = [];
    public int MissingOriginalsCount { get; init; }
    public bool Truncated { get; init; }
}

public sealed class RestoreReport
{
    public int Removed { get; init; }
    public bool MatchesBaseline { get; init; }
    public List<string> LeftInPlace { get; init; } = [];
    public int LeftInPlaceCount { get; init; }
    public List<string> ChangedOriginals { get; init; } = [];
    public int ChangedOriginalsCount { get; init; }
    public List<string> MissingOriginals { get; init; } = [];
    public int MissingOriginalsCount { get; init; }
    public bool Truncated { get; init; }
    public List<string> RevertedChanges { get; init; } = [];
}

/// <summary>"Restore game": removes everything SpecTuner added, then checks the folder against the baseline.</summary>
public static class GameRestorer
{
    public static RestoreReport RestoreAll(GameState state, GameRecord record, Baseline baseline, Ledger ledger, string endReason = ChangeEndReasons.Restored)
    {
        var root = state.GamePath;
        var loaderIsOurs = record.Loader?.Ours == true || ledger.Live.Any(e => e.Kind == ChangeKinds.Loader);
        var recordedFiles = new HashSet<string>(ledger.Live.SelectMany(e => e.Files), GameTree.PathComparer);

        var entries = GameTree.Entries(root).ToList();
        var removed = 0;
        foreach (var (relative, _) in entries.Where(e => !e.IsDirectory))
        {
            if (baseline.Contains(relative))
                continue;
            if (IsOurs(relative, loaderIsOurs) || recordedFiles.Contains(relative))
            {
                GameTree.DeleteFile(GameTree.Full(root, relative));
                removed++;
            }
        }

        // Deepest first, so parents are empty by the time we reach them.
        foreach (var (relative, _) in entries.Where(e => e.IsDirectory).OrderByDescending(e => e.Relative.Length))
        {
            if (baseline.ContainsDirectory(relative) || !IsOurDirectory(relative, loaderIsOurs))
                continue;
            var full = GameTree.Full(root, relative);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }

        var reverted = ledger.MarkAllReverted(endReason).Select(e => e.Id).ToList();
        var verify = Verify(root, baseline);
        state.ClearAfterRestore(record, new RestoreRecord { At = DateTime.UtcNow, Removed = removed, MatchesBaseline = verify.MatchesBaseline });

        return new RestoreReport
        {
            Removed = removed,
            MatchesBaseline = verify.MatchesBaseline,
            LeftInPlace = verify.LeftInPlace,
            LeftInPlaceCount = verify.LeftInPlaceCount,
            ChangedOriginals = verify.ChangedOriginals,
            ChangedOriginalsCount = verify.ChangedOriginalsCount,
            MissingOriginals = verify.MissingOriginals,
            MissingOriginalsCount = verify.MissingOriginalsCount,
            Truncated = verify.Truncated,
            RevertedChanges = reverted,
        };
    }

    /// <summary>
    /// After a failed launch: undo every change made since the last successful start, newest first.
    /// If the loader itself was never verified, that means a full restore.
    /// </summary>
    public static IReadOnlyList<string> RevertAllUnverified(GameState state, GameRecord record, Baseline baseline, Ledger ledger)
    {
        var unverified = ledger.Live.Where(e => !e.Verified).ToList();
        if (unverified.Any(e => e.Kind == ChangeKinds.Loader))
            return RestoreAll(state, record, baseline, ledger, ChangeEndReasons.FailedStart).RevertedChanges;

        var reverted = new List<string>();
        foreach (var entry in Enumerable.Reverse(unverified))
        {
            ledger.Revert(entry.Id, ChangeEndReasons.FailedStart);
            reverted.Add(entry.Id);
        }
        return reverted;
    }

    /// <summary>
    /// Compares the folder with the baseline. A file whose size and last-write time
    /// both match the baseline is taken as unchanged; any other baseline file is re-hashed.
    /// </summary>
    public static VerifyReport Verify(string root, Baseline baseline)
    {
        var current = GameTree.Files(root).ToList();
        var currentSet = new HashSet<string>(current, GameTree.PathComparer);

        var leftInPlace = current.Where(p => !baseline.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = baseline.Files.Where(f => !currentSet.Contains(f.Path)).Select(f => f.Path).ToList();

        var candidates = current.Where(baseline.Contains).ToList();
        var changed = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(candidates, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, relative =>
        {
            var expected = baseline.ByPath[relative];
            var full = GameTree.Full(root, relative);
            var info = new FileInfo(full);
            if (info.Length == expected.Size && info.LastWriteTimeUtc == expected.LastWriteUtc)
                return;
            if (info.Length != expected.Size || GameTree.Sha256(full) != expected.Sha256)
                changed.Add(relative);
        });
        var changedList = changed.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();

        var cap = VerifyReport.ListCap;
        return new VerifyReport
        {
            MatchesBaseline = leftInPlace.Count == 0 && missing.Count == 0 && changedList.Count == 0,
            LeftInPlace = leftInPlace.Take(cap).ToList(),
            LeftInPlaceCount = leftInPlace.Count,
            ChangedOriginals = changedList.Take(cap).ToList(),
            ChangedOriginalsCount = changedList.Count,
            MissingOriginals = missing.Take(cap).ToList(),
            MissingOriginalsCount = missing.Count,
            Truncated = leftInPlace.Count > cap || changedList.Count > cap || missing.Count > cap,
        };
    }

    private static bool IsOurs(string relative, bool loaderIsOurs) =>
        (loaderIsOurs && (GameTree.IsUnder(relative, OurFiles.BepInExDir)
                          || OurFiles.LoaderRootFiles.Contains(relative, GameTree.PathComparer)))
        || GameTree.IsUnder(relative, OurFiles.PluginDir)
        || GameTree.IsUnder(relative, OurFiles.ProbeOutputDir)
        || OurFiles.IsSpecTunerConfig(relative);

    private static bool IsOurDirectory(string relative, bool loaderIsOurs) =>
        (loaderIsOurs && (relative.Equals(OurFiles.BepInExDir, StringComparison.OrdinalIgnoreCase)
                          || GameTree.IsUnder(relative, OurFiles.BepInExDir)))
        || relative.Equals(OurFiles.PluginDir, StringComparison.OrdinalIgnoreCase)
        || GameTree.IsUnder(relative, OurFiles.PluginDir)
        || relative.Equals(OurFiles.ProbeOutputDir, StringComparison.OrdinalIgnoreCase)
        || GameTree.IsUnder(relative, OurFiles.ProbeOutputDir);
}
