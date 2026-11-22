using System.IO.Compression;
using System.Security.Cryptography;
using SpecTuner.Sidecar.Platform;

namespace SpecTuner.Sidecar.Loader;

/// <summary>
/// The BepInEx 5 zips embedded in the sidecar: one per platform (Windows x64, Linux x64, macOS
/// universal), unmodified upstream release assets checked against their SHA-256. All three are in
/// every build, so any platform's install can be tested on any host. <c>BepInEx/core</c> is
/// byte-identical in all of them.
/// </summary>
public static class BepInExPackage
{
    public const string Version = LoaderKit.Version;

    /// <summary>Entries we never extract into a game folder.</summary>
    public static readonly string[] SkippedEntries = ["changelog.txt"];

    /// <summary>The current platform's package.</summary>
    public static ZipArchive Open() => Open(Platforms.Current.Loader);

    public static ZipArchive Open(LoaderKit kit)
    {
        using var resource = typeof(BepInExPackage).Assembly.GetManifestResourceStream(kit.ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {kit.ResourceName} is missing.");
        var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var actual = Convert.ToHexStringLower(SHA256.HashData(buffer.ToArray()));
        if (actual != kit.Sha256)
            throw new InvalidOperationException($"Embedded BepInEx package {kit.ResourceName} is corrupt (sha256 {actual}).");
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
