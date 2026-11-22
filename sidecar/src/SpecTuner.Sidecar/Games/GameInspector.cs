using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Games;

/// <summary>
/// Reads a game folder and decides whether it is in scope. Never writes anything. The steps and
/// their order are the same on every platform; where files are and what a program header says
/// come from the platform's <see cref="IGameLayout"/>.
/// </summary>
public static class GameInspector
{
    private const string NothingChanged = LayoutText.NothingChanged;

    public static ScopeVerdict Inspect(string gamePath) => Inspect(gamePath, Platforms.Current);

    public static ScopeVerdict Inspect(string gamePath, PlatformProfile platform)
    {
        var layout = platform.Layout;
        var facts = new GameFacts { FullPath = layout.GameRoot(SidecarHome.NormalizeGamePath(gamePath)), Platform = platform.Name };
        var root = facts.FullPath;

        if (!Directory.Exists(root))
            return Decline(facts, ErrorCodes.GameNotFound,
                $"SpecTuner couldn't find that folder. Please pick the folder that contains the game. {NothingChanged}",
                $"Folder does not exist: {root}");

        if (layout.Locate(facts) is { } notFound)
            return Decline(facts, notFound);

        // Unity before 2017.2 had no UnityPlayer library; its data folder still has these files.
        var hasUnityData = new[] { "globalgamemanagers", "mainData", "data.unity3d" }
            .Any(f => File.Exists(Path.Combine(facts.DataDir!, f)));
        if (!File.Exists(layout.UnityPlayerPath(facts)) && !hasUnityData)
            return Decline(facts, ErrorCodes.NotUnity, LayoutText.NotUnity,
                $"No {Path.GetFileName(layout.UnityPlayerPath(facts))} and no Unity data files.");

        facts.UnityVersion = layout.ReadUnityVersion(facts);
        ReadAppInfo(facts);

        facts.Arch = layout.ReadArch(facts);

        var isIl2Cpp = File.Exists(layout.GameAssemblyPath(facts))
                       || Directory.Exists(Path.Combine(facts.DataDir!, "il2cpp_data"));
        facts.Backend = isIl2Cpp ? "il2cpp" : Directory.Exists(facts.ManagedDir) ? "mono" : "unknown";

        if (isIl2Cpp)
            return Decline(facts, ErrorCodes.Il2CppNotSupported,
                "This game is built with a Unity option called IL2CPP, and SpecTuner does not support that kind of game yet. " +
                NothingChanged,
                layout.Il2CppDetail);

        if (facts.Backend != "mono")
            return Decline(facts, ErrorCodes.NotUnity,
                $"SpecTuner couldn't find this game's code, so it doesn't look like a complete Unity game. {NothingChanged}",
                "No <Name>_Data/Managed folder.");

        if (layout.CheckArch(facts) is { } wrongArch)
            return Decline(facts, wrongArch);

        var antiCheat = FindAntiCheat(root, platform.Id);
        if (antiCheat is not null)
            return Decline(facts, ErrorCodes.AntiCheat,
                $"This game uses anti-cheat protection ({antiCheat.Value.Product}). SpecTuner never changes games with anti-cheat, " +
                $"because that breaks the game's rules and could get your account banned. {NothingChanged}",
                $"Found: {antiCheat.Value.Path}");

        if (layout.CheckProgram(facts) is { } protectedProgram)
            return Decline(facts, protectedProgram);

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

    private static ScopeVerdict Decline(GameFacts facts, LayoutDecline decline) => Decline(facts, decline.Code, decline.Message, decline.Detail);

    private static ScopeVerdict Decline(GameFacts facts, string code, string message, string detail) =>
        new() { Facts = facts, Code = code, Message = message, Detail = detail };

    private static void ReadAppInfo(GameFacts facts)
    {
        var appInfo = Path.Combine(facts.DataDir!, "app.info");
        if (!File.Exists(appInfo))
            return;
        var lines = File.ReadAllLines(appInfo);
        facts.Company = lines.Length > 0 && lines[0].Length > 0 ? lines[0].Trim() : null;
        facts.Product = lines.Length > 1 && lines[1].Length > 0 ? lines[1].Trim() : null;
    }

    private static (string Product, string Path)? FindAntiCheat(string root, GamePlatform platform)
    {
        foreach (var (relative, isDirectory) in GameTree.Entries(root))
        {
            var name = relative[(relative.LastIndexOf('/') + 1)..];
            var match = Signatures.AntiCheat.FirstOrDefault(s => s.Matches(name, isDirectory, platform));
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
