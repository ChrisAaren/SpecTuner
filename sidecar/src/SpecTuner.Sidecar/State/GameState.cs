using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.State;

public sealed class LoaderRecord
{
    /// <summary>True when SpecTuner installed BepInEx; false when the player already had it (then it is never removed).</summary>
    public bool Ours { get; set; }
    public string? Version { get; set; }
}

public sealed class ProbeRecord
{
    /// <summary>Hash of the embedded probe source it was built from; a different hash means rebuild.</summary>
    public string SourceHash { get; set; } = "";
    public DateTime BuiltAt { get; set; }
}

public sealed class RestoreRecord
{
    public DateTime At { get; set; }
    public int Removed { get; set; }
    public bool MatchesBaseline { get; set; }
}

/// <summary>Contents of <c>game.json</c>.</summary>
public sealed class GameRecord
{
    public string GamePath { get; set; } = "";
    public string? Name { get; set; }
    public DateTime FirstSeen { get; set; }
    public LoaderRecord? Loader { get; set; }
    public ProbeRecord? Probe { get; set; }
    public List<RestoreRecord> Restores { get; set; } = [];
}

/// <summary>The shape of changes.json, for reading it outside the Ledger.</summary>
internal sealed class LedgerFileShape
{
    public List<ChangeEntry> Changes { get; set; } = [];
}

/// <summary>Paths and files of one game's state folder, <c>&lt;SPECTUNER_HOME&gt;/games/&lt;id&gt;/</c>.</summary>
public sealed class GameState
{
    public string GamePath { get; }
    public string Id { get; }
    public string Dir { get; }
    public string GameJsonPath => Path.Combine(Dir, "game.json");
    public string BaselinePath => Path.Combine(Dir, "baseline.json");
    public string ChangesPath => Path.Combine(Dir, "changes.json");
    public string SnapshotsDir => Path.Combine(Dir, "snapshots");
    public string BenchmarksPath => Path.Combine(Dir, "benchmarks.json");
    public string HistoryPath => Path.Combine(Dir, "history.json");
    /// <summary>Linux and macOS: the game a measured run started, so a killed sidecar's game can be closed by the next command.</summary>
    public string RunningGamePath => Path.Combine(Dir, "running.json");

    private GameState(string gamePath)
    {
        GamePath = SidecarHome.NormalizeGamePath(gamePath);
        Id = SidecarHome.GameId(GamePath);
        Dir = Path.Combine(SidecarHome.Root, "games", Id);
    }

    public static GameState For(string gamePath) => new(gamePath);

    public bool HasRecord => File.Exists(GameJsonPath);

    public GameRecord? LoadRecord() => AtomicFile.ReadJson<GameRecord>(GameJsonPath);

    public GameRecord LoadOrCreateRecord(string? name)
    {
        var record = LoadRecord();
        if (record is not null)
            return record;
        record = new GameRecord { GamePath = GamePath, Name = name, FirstSeen = DateTime.UtcNow };
        SaveRecord(record);
        return record;
    }

    public void SaveRecord(GameRecord record) => AtomicFile.WriteJson(GameJsonPath, record);

    public Baseline? LoadBaseline() => AtomicFile.ReadJson<Baseline>(BaselinePath);

    public void SaveBaseline(Baseline baseline) => AtomicFile.WriteJson(BaselinePath, baseline);

    /// <summary>Exclusive lock so two sidecar processes never change the same game at once.</summary>
    public IDisposable Lock()
    {
        Directory.CreateDirectory(Dir);
        try
        {
            return new FileStream(Path.Combine(Dir, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.DeleteOnClose);
        }
        catch (IOException)
        {
            throw new SidecarException(ErrorCodes.Busy, "SpecTuner is already working on this game. Please wait a moment and try again.");
        }
    }

    /// <summary>
    /// After a full restore: forget the baseline, ledger, snapshots and benchmark history, keep game.json as the record that it was restored.
    /// The ledger's entries (all reverted by now) move to history.json, and mods/ (each build's source) stays, so past changes can be applied again.
    /// </summary>
    public void ClearAfterRestore(GameRecord record, RestoreRecord restore)
    {
        record.Loader = null;
        record.Probe = null;
        record.Restores.Add(restore);
        SaveRecord(record);
        var entries = AtomicFile.ReadJson<LedgerFileShape>(ChangesPath)?.Changes;
        if (entries is { Count: > 0 })
        {
            var history = ChangeHistory.Load(this);
            history.Changes.AddRange(entries);
            history.Save(this);
        }
        foreach (var file in new[] { BaselinePath, ChangesPath, BenchmarksPath })
            if (File.Exists(file))
                File.Delete(file);
        if (Directory.Exists(SnapshotsDir))
            Directory.Delete(SnapshotsDir, recursive: true);
    }
}
