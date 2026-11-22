using System.IO.Enumeration;
using System.Security.Cryptography;

namespace SpecTuner.Sidecar.State;

/// <summary>
/// Walks a game folder. Relative paths use forward slashes and are compared
/// case-insensitively (Windows). Hidden and system files are included; folder
/// links (junctions, symlinks) are not followed.
/// </summary>
public static class GameTree
{
    public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    public static IEnumerable<(string Relative, bool IsDirectory)> Entries(string root)
    {
        if (!Directory.Exists(root))
            return [];
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = true };
        return new FileSystemEnumerable<(string, bool)>(root,
            (ref FileSystemEntry entry) => (Relative(root, entry.ToFullPath()), entry.IsDirectory), options)
        {
            ShouldRecursePredicate = (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };
    }

    public static IEnumerable<string> Files(string root) => Entries(root).Where(e => !e.IsDirectory).Select(e => e.Relative);

    public static IEnumerable<string> Directories(string root) => Entries(root).Where(e => e.IsDirectory).Select(e => e.Relative);

    public static string Relative(string root, string fullPath) =>
        Path.GetRelativePath(root, fullPath).Replace('\\', '/');

    /// <summary>Full path for a relative path, refusing anything that would land outside <paramref name="root"/>.</summary>
    public static string Full(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSep = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Path escapes the game folder: {relative}");
        return full;
    }

    public static bool IsUnder(string relative, string directory) =>
        relative.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase);

    public static string Sha256(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static void DeleteFile(string fullPath)
    {
        if (!File.Exists(fullPath))
            return;
        File.SetAttributes(fullPath, FileAttributes.Normal);
        File.Delete(fullPath);
    }

    /// <summary>Removes now-empty parent folders of a deleted file, stopping at the root or at any folder that existed at baseline.</summary>
    public static void RemoveEmptyParents(string root, string relativeFile, Baseline baseline)
    {
        var dir = Path.GetDirectoryName(relativeFile.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
        while (!string.IsNullOrEmpty(dir) && !baseline.ContainsDirectory(dir))
        {
            var full = Full(root, dir);
            if (!Directory.Exists(full) || Directory.EnumerateFileSystemEntries(full).Any())
                return;
            Directory.Delete(full);
            dir = Path.GetDirectoryName(dir.Replace('/', Path.DirectorySeparatorChar))?.Replace('\\', '/');
        }
    }
}
