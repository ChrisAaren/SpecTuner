using System.Diagnostics;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;

namespace SpecTuner.Sidecar.Platform;

/// <summary>
/// A Windows Unity game: <c>&lt;Name&gt;.exe</c> with a sibling <c>&lt;Name&gt;_Data</c> folder,
/// <c>UnityPlayer.dll</c>, IL2CPP's <c>GameAssembly.dll</c>. Behaviour, messages and codes are the
/// sidecar's original Windows ones.
/// </summary>
public sealed class WindowsLayout : IGameLayout
{
    public string GameRoot(string pickedPath) => pickedPath;

    public LayoutDecline? Locate(GameFacts facts)
    {
        var root = facts.FullPath;
        var exes = FindExes(root);
        if (exes.Count == 0)
            return new LayoutDecline(ErrorCodes.GameNotFound,
                $"SpecTuner couldn't find a game in that folder. Please pick the folder that contains the game's program (.exe) file. {LayoutText.NothingChanged}",
                "No .exe file in the folder root.");

        var exe = exes.FirstOrDefault(p => Directory.Exists(DataDirFor(p)));
        if (exe is null)
        {
            facts.Exe = Path.GetFileName(exes[0]);
            return new LayoutDecline(ErrorCodes.NotUnity, LayoutText.NotUnity, "No <Name>_Data folder next to any .exe.");
        }

        facts.ExePath = exe;
        facts.Exe = Path.GetFileName(exe);
        facts.Name = Path.GetFileNameWithoutExtension(exe);
        facts.DataDir = DataDirFor(exe);
        return null;
    }

    /// <summary>True when the folder holds a Windows Unity game (used to recognise a Windows build on Linux or macOS).</summary>
    public static bool HasWindowsGame(string root) =>
        Directory.Exists(root) && FindExes(root).Any(p => Directory.Exists(DataDirFor(p)));

    private static List<string> FindExes(string root) =>
        Directory.GetFiles(root, "*.exe")
            .Where(p => !Path.GetFileName(p).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string DataDirFor(string exe) =>
        Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "_Data");

    public string UnityPlayerPath(GameFacts facts) => Path.Combine(facts.FullPath, "UnityPlayer.dll");

    public string? ReadUnityVersion(GameFacts facts) =>
        ReadVersionResource(UnityPlayerPath(facts)) ?? ReadVersionResource(facts.ExePath!);

    private static string? ReadVersionResource(string path)
    {
        if (!File.Exists(path))
            return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        // ProductVersion looks like "6000.6.9f1 (8c45277fb9c5)".
        var version = info.ProductVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(version) ? info.FileVersion : version;
    }

    public string GameAssemblyPath(GameFacts facts) => Path.Combine(facts.FullPath, "GameAssembly.dll");

    public string Il2CppDetail => "GameAssembly.dll or <Name>_Data/il2cpp_data is present.";

    public string ReadArch(GameFacts facts) => PeFile.TryRead(facts.ExePath!)?.Arch ?? "unknown";

    public LayoutDecline? CheckArch(GameFacts facts) =>
        facts.Arch == "x64"
            ? null
            : new LayoutDecline(ErrorCodes.NotWindowsX64,
                $"This isn't a 64-bit Windows game, and SpecTuner only works with 64-bit Windows games for now. {LayoutText.NothingChanged}",
                $"Game exe machine type: {facts.Arch}.");

    public LayoutDecline? CheckProgram(GameFacts facts) =>
        PeFile.TryRead(facts.ExePath!)!.SectionNames.Contains(".bind")
            ? new LayoutDecline(ErrorCodes.ProtectedCode,
                $"This game is protected by copy protection (DRM), so SpecTuner won't change it. {LayoutText.NothingChanged}",
                "The game exe has a .bind section (Steam DRM wrapper).")
            : null;
}
