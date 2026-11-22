using System.IO.Compression;
using System.Security.Cryptography;

namespace SpecTuner.Sidecar.Loader;

/// <summary>The BepInEx 5 zip embedded in the sidecar exe.</summary>
public static class BepInExPackage
{
    public const string Version = "5.4.23.6";
    public const string ResourceName = "BepInEx_win_x64_5.4.23.6.zip";
    public const string Sha256 = "6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755";

    /// <summary>Entries we never extract into a game folder.</summary>
    public static readonly string[] SkippedEntries = ["changelog.txt"];

    public static ZipArchive Open()
    {
        using var resource = typeof(BepInExPackage).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var actual = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        if (actual != Sha256)
            throw new InvalidOperationException($"Embedded BepInEx package is corrupt (sha256 {actual}).");
        buffer.Position = 0;
        return new ZipArchive(buffer, ZipArchiveMode.Read);
    }

    /// <summary>The dlls in BepInEx/core/, as compile references before BepInEx is on disk.</summary>
    public static List<(string Name, byte[] Image)> CoreImages()
    {
        using var archive = Open();
        var result = new List<(string, byte[])>();
        foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("BepInEx/core/", StringComparison.Ordinal)
                                                        && e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            result.Add((entry.FullName, buffer.ToArray()));
        }
        return result;
    }

    /// <summary>Relative paths (forward slashes) of the files we extract.</summary>
    public static List<string> FilesToExtract(ZipArchive archive) =>
        archive.Entries
            .Where(e => !e.FullName.EndsWith('/') && e.Name.Length > 0)
            .Select(e => e.FullName.Replace('\\', '/'))
            .Where(p => !SkippedEntries.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
}
