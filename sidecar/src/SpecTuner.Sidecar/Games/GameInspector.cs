using System.Diagnostics;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Games;

/// <summary>Reads a game folder and decides whether it is in scope. Never writes anything.</summary>
public static class GameInspector
{
    private const string NothingChanged = "Nothing was changed.";

    public static ScopeVerdict Inspect(string gamePath)
    {
        var facts = new GameFacts { FullPath = SidecarHome.NormalizeGamePath(gamePath) };
        var root = facts.FullPath;

        if (!Directory.Exists(root))
            return Decline(facts, ErrorCodes.GameNotFound,
                $"SpecTuner couldn't find that folder. Please pick the folder that contains the game. {NothingChanged}",
                $"Folder does not exist: {root}");

        var exes = Directory.GetFiles(root, "*.exe")
            .Where(p => !Path.GetFileName(p).StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (exes.Count == 0)
            return Decline(facts, ErrorCodes.GameNotFound,
                $"SpecTuner couldn't find a game in that folder. Please pick the folder that contains the game's program (.exe) file. {NothingChanged}",
                "No .exe file in the folder root.");

        var exe = exes.FirstOrDefault(p => Directory.Exists(DataDirFor(p)));
        if (exe is null)
        {
            facts.Exe = Path.GetFileName(exes[0]);
            return Decline(facts, ErrorCodes.NotUnity, NotUnityMessage, "No <Name>_Data folder next to any .exe.");
        }

        facts.ExePath = exe;
        facts.Exe = Path.GetFileName(exe);
        facts.Name = Path.GetFileNameWithoutExtension(exe);
        facts.DataDir = DataDirFor(exe);

        var unityPlayer = Path.Combine(root, "UnityPlayer.dll");
        // Unity before 2017.2 had no UnityPlayer.dll; its data folder still has these files.
        var hasUnityData = new[] { "globalgamemanagers", "mainData", "data.unity3d" }
            .Any(f => File.Exists(Path.Combine(facts.DataDir, f)));
        if (!File.Exists(unityPlayer) && !hasUnityData)
            return Decline(facts, ErrorCodes.NotUnity, NotUnityMessage, "No UnityPlayer.dll and no Unity data files.");

        facts.UnityVersion = ReadUnityVersion(unityPlayer) ?? ReadUnityVersion(exe);
        ReadAppInfo(facts);

        var exeHeader = PeFile.TryRead(exe);
        facts.Arch = exeHeader?.Arch ?? "unknown";

        var isIl2Cpp = File.Exists(Path.Combine(root, "GameAssembly.dll"))
                       || Directory.Exists(Path.Combine(facts.DataDir, "il2cpp_data"));
        facts.Backend = isIl2Cpp ? "il2cpp" : Directory.Exists(facts.ManagedDir) ? "mono" : "unknown";

        if (isIl2Cpp)
            return Decline(facts, ErrorCodes.Il2CppNotSupported,
                "This game is built with a Unity option called IL2CPP, and SpecTuner does not support that kind of game yet. " +
                NothingChanged,
                "GameAssembly.dll or <Name>_Data/il2cpp_data is present.");

        if (facts.Backend != "mono")
            return Decline(facts, ErrorCodes.NotUnity,
                $"SpecTuner couldn't find this game's code, so it doesn't look like a complete Unity game. {NothingChanged}",
                "No <Name>_Data/Managed folder.");

        if (facts.Arch != "x64")
            return Decline(facts, ErrorCodes.NotWindowsX64,
                $"This isn't a 64-bit Windows game, and SpecTuner only works with 64-bit Windows games for now. {NothingChanged}",
                $"Game exe machine type: {facts.Arch}.");

        var antiCheat = FindAntiCheat(root);
        if (antiCheat is not null)
            return Decline(facts, ErrorCodes.AntiCheat,
                $"This game uses anti-cheat protection ({antiCheat.Value.Product}). SpecTuner never changes games with anti-cheat, " +
                $"because that breaks the game's rules and could get your account banned. {NothingChanged}",
                $"Found: {antiCheat.Value.Path}");

        if (exeHeader!.SectionNames.Contains(".bind"))
            return Decline(facts, ErrorCodes.ProtectedCode,
                $"This game is protected by copy protection (DRM), so SpecTuner won't change it. {NothingChanged}",
                "The game exe has a .bind section (Steam DRM wrapper).");

        var assemblyCSharp = Path.Combine(facts.ManagedDir!, "Assembly-CSharp.dll");
        if (File.Exists(assemblyCSharp) && PeFile.TryRead(assemblyCSharp) is not { HasValidMetadata: true })
            return Decline(facts, ErrorCodes.ProtectedCode,
                $"This game's code is encrypted or protected, so SpecTuner can't read it and won't change it. {NothingChanged}",
                "Managed/Assembly-CSharp.dll has no valid .NET metadata header.");

        var network = FindOnlineMultiplayer(facts.ManagedDir!);
        if (network is not null)
            return Decline(facts, ErrorCodes.OnlineMultiplayer,
                $"This game has online multiplayer (it includes {network.Value.Product}). SpecTuner only changes single-player games, " +
                $"so other players are never affected. {NothingChanged}",
                $"Found: {network.Value.Path}");

        return new ScopeVerdict { Facts = facts };
    }

    private const string NotUnityMessage =
        "This doesn't look like a game made with Unity. SpecTuner only works with Unity games. " + NothingChanged;

    private static string DataDirFor(string exe) =>
        Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "_Data");

    private static ScopeVerdict Decline(GameFacts facts, string code, string message, string detail) =>
        new() { Facts = facts, Code = code, Message = message, Detail = detail };

    private static string? ReadUnityVersion(string path)
    {
        if (!File.Exists(path))
            return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        // ProductVersion looks like "6000.6.9f1 (8c45277fb9c5)".
        var version = info.ProductVersion?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(version) ? info.FileVersion : version;
    }

    private static void ReadAppInfo(GameFacts facts)
    {
        var appInfo = Path.Combine(facts.DataDir!, "app.info");
        if (!File.Exists(appInfo))
            return;
        var lines = File.ReadAllLines(appInfo);
        facts.Company = lines.Length > 0 && lines[0].Length > 0 ? lines[0].Trim() : null;
        facts.Product = lines.Length > 1 && lines[1].Length > 0 ? lines[1].Trim() : null;
    }

    private static (string Product, string Path)? FindAntiCheat(string root)
    {
        foreach (var (relative, isDirectory) in GameTree.Entries(root))
        {
            var name = relative[(relative.LastIndexOf('/') + 1)..];
            var match = Signatures.AntiCheat.FirstOrDefault(s => s.Matches(name, isDirectory));
            if (match is not null)
                return (match.Product, relative);
        }
        return null;
    }

    private static (string Product, string Path)? FindOnlineMultiplayer(string managedDir)
    {
        foreach (var file in Directory.EnumerateFiles(managedDir))
        {
            var name = Path.GetFileName(file);
            var match = Signatures.OnlineMultiplayer.FirstOrDefault(s => s.Matches(name, isDirectory: false));
            if (match is not null)
                return (match.Product, "Managed/" + name);
        }
        return null;
    }
}
