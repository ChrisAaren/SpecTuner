using System.Security.Cryptography;
using System.Text;

namespace SpecTuner.Sidecar.State;

public static class SidecarHome
{
    /// <summary><c>SPECTUNER_HOME</c> if set, otherwise <c>%LOCALAPPDATA%\SpecTuner</c>. Read on every call so tests can switch it.</summary>
    public static string Root
    {
        get
        {
            var fromEnv = Environment.GetEnvironmentVariable("SPECTUNER_HOME");
            return !string.IsNullOrWhiteSpace(fromEnv)
                ? Path.GetFullPath(fromEnv)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpecTuner");
        }
    }

    public static string NormalizeGamePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>First 16 hex chars of SHA-256 of the lower-cased, normalized full game path.</summary>
    public static string GameId(string gamePath)
    {
        var bytes = Encoding.UTF8.GetBytes(NormalizeGamePath(gamePath).ToLowerInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }
}
