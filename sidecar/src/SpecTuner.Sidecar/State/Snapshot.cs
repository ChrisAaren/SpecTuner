namespace SpecTuner.Sidecar.State;

/// <summary>A copy of our own mutable files, taken before a change. Stored as <c>snapshots/&lt;changeId&gt;/</c>.</summary>
public sealed class Snapshot
{
    public DateTime TakenAt { get; set; }
    public List<string> Files { get; set; } = [];

    private const string ManifestName = "snapshot.json";
    private const string FilesDir = "files";

    public bool Has(string relative) => Files.Contains(relative, GameTree.PathComparer);

    public static void Take(string gameRoot, string snapshotDir)
    {
        var files = OurFiles.SnapshottedFiles(gameRoot).ToList();
        foreach (var relative in files)
        {
            var target = GameTree.Full(Path.Combine(snapshotDir, FilesDir), relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(GameTree.Full(gameRoot, relative), target, overwrite: true);
        }
        AtomicFile.WriteJson(Path.Combine(snapshotDir, ManifestName), new Snapshot { TakenAt = DateTime.UtcNow, Files = files });
    }

    public static Snapshot Load(string snapshotDir) =>
        AtomicFile.ReadJson<Snapshot>(Path.Combine(snapshotDir, ManifestName))
        ?? throw new InvalidOperationException($"Snapshot is missing: {snapshotDir}");

    public static string StoredFile(string snapshotDir, string relative) =>
        GameTree.Full(Path.Combine(snapshotDir, FilesDir), relative);
}
