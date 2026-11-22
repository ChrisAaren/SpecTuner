using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;

namespace SpecTuner.Sidecar.Platform;

/// <summary>
/// A macOS Unity game: <c>&lt;Name&gt;.app</c> with the program in <c>Contents/MacOS/</c> (named by
/// <c>CFBundleExecutable</c> in <c>Contents/Info.plist</c>), data in <c>Contents/Resources/Data/</c>,
/// <c>Contents/Frameworks/UnityPlayer.dylib</c>, and for IL2CPP <c>Contents/Frameworks/GameAssembly.dylib</c>.
/// SpecTuner works in the folder that holds the .app, where BepInEx expects its files: nothing is
/// ever written inside the app.
/// </summary>
public sealed class MacLayout(Architecture hostArchitecture) : IGameLayout
{
    /// <summary>Easy Anti-Cheat's bootstrapper apps: not the game, and the anti-cheat check reports them.</summary>
    private static readonly string[] HelperApps = ["start_protected_game.app", "start_protected_game_x86-64.app"];

    /// <summary>Architectures this Mac can run, most preferred first (the order run_bepinex.sh asks arch for).</summary>
    public IReadOnlyList<string> RunnableArches => hostArchitecture == Architecture.Arm64 ? ["arm64", "x64"] : ["x64"];

    public string GameRoot(string pickedPath) =>
        pickedPath.EndsWith(".app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(pickedPath)
            ? Path.GetDirectoryName(pickedPath) ?? pickedPath
            : pickedPath;

    public static string? AppPath(GameFacts facts) =>
        facts.Exe is null ? null : Path.Combine(facts.FullPath, facts.Exe);

    public LayoutDecline? Locate(GameFacts facts)
    {
        var root = facts.FullPath;
        var apps = Directory.GetDirectories(root, "*.app")
            .Where(p => !HelperApps.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (apps.Count == 0)
        {
            if (WindowsLayout.HasWindowsGame(root))
                return LayoutText.CompatibilityLayer("macOS");
            return new LayoutDecline(ErrorCodes.GameNotFound,
                $"SpecTuner couldn't find a game in that folder. Please pick the folder that contains the game's app. {LayoutText.NothingChanged}",
                "No .app in the folder root.");
        }
        if (apps.Count > 1)
            return new LayoutDecline(ErrorCodes.GameNotFound,
                "This folder holds more than one app. SpecTuner adds its files next to the game's app, so the game needs a folder " +
                $"of its own. Please move the game into its own folder and pick that folder. {LayoutText.NothingChanged}",
                "Apps in the folder: " + string.Join(", ", apps.Select(Path.GetFileName)));

        var app = apps[0];
        facts.Exe = Path.GetFileName(app);
        facts.Name = Path.GetFileNameWithoutExtension(app);
        var dataDir = Path.Combine(app, "Contents", "Resources", "Data");
        if (!Directory.Exists(dataDir))
            return new LayoutDecline(ErrorCodes.NotUnity, LayoutText.NotUnity, "No Contents/Resources/Data folder in the app.");

        var program = ProgramPath(app);
        if (program is null)
            return new LayoutDecline(ErrorCodes.GameNotFound,
                $"SpecTuner couldn't find the game's program inside its app. {LayoutText.NothingChanged}",
                "No program in Contents/MacOS.");
        facts.ExePath = program;
        facts.DataDir = dataDir;
        return null;
    }

    /// <summary>Contents/MacOS/&lt;CFBundleExecutable&gt;; when Info.plist is not XML, the only file in Contents/MacOS, or one named like the app.</summary>
    private static string? ProgramPath(string app)
    {
        var macOs = Path.Combine(app, "Contents", "MacOS");
        if (!Directory.Exists(macOs))
            return null;
        var plist = Path.Combine(app, "Contents", "Info.plist");
        if (File.Exists(plist))
        {
            var match = Regex.Match(File.ReadAllText(plist), @"<key>\s*CFBundleExecutable\s*</key>\s*<string>([^<]+)</string>");
            if (match.Success)
            {
                var named = Path.Combine(macOs, match.Groups[1].Value.Trim());
                if (File.Exists(named))
                    return named;
            }
        }
        var files = Directory.GetFiles(macOs);
        if (files.Length == 1)
            return files[0];
        var sameName = Path.Combine(macOs, Path.GetFileNameWithoutExtension(app));
        return File.Exists(sameName) ? sameName : null;
    }

    private static string Frameworks(GameFacts facts) => Path.Combine(AppPath(facts)!, "Contents", "Frameworks");

    public string UnityPlayerPath(GameFacts facts) => Path.Combine(Frameworks(facts), "UnityPlayer.dylib");

    public string? ReadUnityVersion(GameFacts facts) => LayoutText.UnityVersionFromDataFiles(facts.DataDir!);

    public string GameAssemblyPath(GameFacts facts) => Path.Combine(Frameworks(facts), "GameAssembly.dylib");

    public string Il2CppDetail => "Contents/Frameworks/GameAssembly.dylib or Contents/Resources/Data/il2cpp_data is present.";

    public string ReadArch(GameFacts facts)
    {
        var header = ExecutableHeader.Read(facts.ExePath!);
        return header is { Format: ExecutableHeader.MachO or ExecutableHeader.MachOUniversal }
            ? string.Join("+", header.Arches.Distinct())
            : "unknown";
    }

    /// <summary>The slice macOS will run here, or null when none can run.</summary>
    public MachOSlice? RunningSlice(GameFacts facts)
    {
        var header = ExecutableHeader.Read(facts.ExePath!);
        if (header is null)
            return null;
        foreach (var arch in RunnableArches)
        {
            var slice = header.Slices.FirstOrDefault(s => s.Arch == arch);
            if (slice is not null)
                return slice;
        }
        return null;
    }

    public LayoutDecline? CheckArch(GameFacts facts) =>
        RunningSlice(facts) is not null
            ? null
            : new LayoutDecline(ErrorCodes.UnsupportedArchitecture,
                $"This game isn't built for this Mac's processor, so SpecTuner can't work with it. {LayoutText.NothingChanged}",
                $"Game program architecture: {facts.Arch}; this Mac runs: {string.Join(", ", RunnableArches)}.");

    /// <summary>
    /// Apple's Hardened Runtime ignores DYLD_* variables unless the app has the
    /// allow-dyld-environment-variables entitlement, and refuses libraries signed by someone else
    /// unless it has disable-library-validation. Without both, the loader can't get in, and
    /// SpecTuner never re-signs or edits a game to change that.
    /// </summary>
    public LayoutDecline? CheckProgram(GameFacts facts)
    {
        var slice = RunningSlice(facts);
        if (slice is not { HardenedRuntime: true } || (slice.AllowsDyldEnvironment && slice.DisablesLibraryValidation))
            return null;
        return new LayoutDecline(ErrorCodes.LoaderBlocked,
            "macOS protects this game from add-ons: its app is signed in a way that stops SpecTuner's mod loader from running inside it. " +
            "Getting around that would mean changing the game's app, which SpecTuner never does, so it can't work with this game. " +
            LayoutText.NothingChanged,
            $"Hardened runtime on the {slice.Arch} slice; allow-dyld-environment-variables: {slice.AllowsDyldEnvironment}; " +
            $"disable-library-validation: {slice.DisablesLibraryValidation}.");
    }
}
