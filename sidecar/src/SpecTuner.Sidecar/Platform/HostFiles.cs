namespace SpecTuner.Sidecar.Platform;

/// <summary>File-system facts that depend on the machine the sidecar runs on, not on the simulated platform.</summary>
public static class HostFiles
{
    private const UnixFileMode Executable = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Adds the execute bits (like <c>chmod +x</c>). Windows has no such bits: nothing to do.</summary>
    public static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserRead | Executable);
    }

    /// <summary>True when the owner may execute the file; null on Windows, which has no such bit.</summary>
    public static bool? IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return null;
        return (File.GetUnixFileMode(path) & UnixFileMode.UserExecute) != 0;
    }

    /// <summary>The permission bits as an octal string ("755"), or null on Windows.</summary>
    public static string? ModeString(string path)
    {
        if (OperatingSystem.IsWindows() || (!File.Exists(path) && !Directory.Exists(path)))
            return null;
        return Convert.ToString((int)File.GetUnixFileMode(path) & 0x1FF, 8);
    }

    /// <summary>
    /// The path with every symbolic link in it followed, parent folders included. Linux and macOS
    /// report a running program's path in this form, whatever path it was started through. (macOS
    /// reaches its temporary folders through the link <c>/var</c>; a Steam library is often reached
    /// through a linked folder.)
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        // Each pass follows the first link in the path. 40 is the kernels' own limit on links in one path.
        for (var pass = 0; pass < 40; pass++)
        {
            if (FollowFirstLink(full) is not { } next)
                break;
            full = next;
        }
        return full;
    }

    private static string? FollowFirstLink(string full)
    {
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return null;
        var parts = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            string? target;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            if (target is not null)
            {
                // A relative target is relative to the folder that holds the link.
                var resolved = Path.GetFullPath(target, current);
                foreach (var rest in parts.Skip(i + 1))
                    resolved = Path.Combine(resolved, rest);
                return Path.GetFullPath(resolved);
            }
            current = next;
        }
        return null;
    }

    /// <summary>
    /// Whether names in <paramref name="root"/> differ by case, found without writing: take an entry
    /// whose name has letters, flip their case, and see whether the flipped name opens the same entry.
    /// <paramref name="fallback"/> when no entry has letters to flip.
    /// </summary>
    public static bool IsCaseSensitive(string root, bool fallback)
    {
        if (!Directory.Exists(root))
            return fallback;
        var names = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().ToList();
        foreach (var name in names)
        {
            var flipped = new string(name.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
            if (flipped == name)
                continue;
            if (names.Contains(flipped, StringComparer.Ordinal))
                return true; // two entries that differ only by case
            var path = Path.Combine(root, flipped);
            return !(File.Exists(path) || Directory.Exists(path));
        }
        return fallback;
    }
}
