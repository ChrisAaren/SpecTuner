using System.Security.Cryptography;
using System.Text;
using SpecTuner.Sidecar.Platform;

namespace SpecTuner.Sidecar.State;

public static class SidecarHome
{
    /// <summary>
    /// <c>SPECTUNER_HOME</c> if set, otherwise the platform's default (Windows <c>%LOCALAPPDATA%\SpecTuner</c>,
    /// macOS <c>~/Library/Application Support/SpecTuner</c>, Linux <c>${XDG_DATA_HOME:-~/.local/share}/SpecTuner</c>).
    /// Read on every call so tests can switch it.
    /// </summary>
    public static string Root
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("SPECTUNER_HOME");
            return !string.IsNullOrWhiteSpace(fromEnv)
                ? Path.GetFullPath(fromEnv)
                : Platforms.Current.Folders.StateRoot;
        }
    }

    public static string NormalizeGamePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// First 16 hex chars of SHA-256 of the normalized full game path, lower-cased on Windows and
    /// macOS (file systems that ignore case) and as it is on Linux.
    /// </summary>
    public static string GameId(string gamePath)
    {
        var path = NormalizeGamePath(gamePath);
        if (Platforms.Current.LowerCaseGameId)
            path = path.ToLowerInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..16];
    }
}
