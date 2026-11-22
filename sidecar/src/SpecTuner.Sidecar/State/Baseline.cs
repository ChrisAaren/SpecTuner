using System.Text.Json.Serialization;

namespace SpecTuner.Sidecar.State;

public sealed class BaselineFile
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>Manifest of every file in the game folder as first found. Written once, before anything is added.</summary>
public sealed class Baseline
{
    public DateTime CreatedAt { get; set; }
    public string GamePath { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    public List<string> Directories { get; set; } = [];
    public List<BaselineFile> Files { get; set; } = [];

    private Dictionary<string, BaselineFile>? _byPath;
    private HashSet<string>? _directories;

    [JsonIgnore]
    public IReadOnlyDictionary<string, BaselineFile> ByPath =>
        _byPath ??= Files.ToDictionary(f => f.Path, GameTree.PathComparer);

    public bool Contains(string relativePath) => ByPath.ContainsKey(relativePath);

    public bool ContainsDirectory(string relativePath) =>
        (_directories ??= new HashSet<string>(Directories, GameTree.PathComparer)).Contains(relativePath);

    public bool AnyUnder(string relativeDirectory) =>
        ContainsDirectory(relativeDirectory) || Files.Any(f => GameTree.IsUnder(f.Path, relativeDirectory));

    public static Baseline Capture(string root)
    {
        var entries = GameTree.Entries(root).ToList();
        var files = entries.Where(e => !e.IsDirectory).Select(e => e.Relative).ToArray();
        var results = new BaselineFile[files.Length];

        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, i =>
        {
            var full = GameTree.Full(root, files[i]);
            var info = new FileInfo(full);
            results[i] = new BaselineFile
            {
                Path = files[i],
                Size = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc,
                Sha256 = GameTree.Sha256(full),
            };
        });

        return new Baseline
        {
            CreatedAt = DateTime.UtcNow,
            GamePath = root,
            FileCount = results.Length,
            TotalBytes = results.Sum(f => f.Size),
            Directories = entries.Where(e => e.IsDirectory).Select(e => e.Relative)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            Files = results.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }
}
