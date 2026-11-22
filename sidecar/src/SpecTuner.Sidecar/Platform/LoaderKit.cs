using SpecTuner.Sidecar.Games;

namespace SpecTuner.Sidecar.Platform;

/// <summary>How to start a program: what to run, its arguments, and environment variables to set on top of the inherited ones.</summary>
public sealed record LaunchSpec(string FileName, IReadOnlyList<string> Arguments, IReadOnlyDictionary<string, string> Environment)
{
    public static LaunchSpec Plain(string fileName) => new(fileName, [], new Dictionary<string, string>());
}

/// <summary>
/// One platform's BepInEx 5.4.23.6 package (unmodified upstream release, embedded in the sidecar) and
/// how the game starts with or without it. On Windows Doorstop's <c>winhttp.dll</c> loads itself;
/// on Linux and macOS the loader is injected through environment variables, exactly as the
/// package's own <c>run_bepinex.sh</c> does.
/// </summary>
public abstract class LoaderKit
{
    public const string Version = "5.4.23.6";
    public const string PreloaderDll = "BepInEx/core/BepInEx.Preloader.dll";
    public const string StartScript = "run_bepinex.sh";

    public abstract GamePlatform Platform { get; }
    public abstract string ResourceName { get; }
    public abstract string Sha256 { get; }
    /// <summary>The loader's own files at the game root (besides BepInEx/).</summary>
    public abstract IReadOnlyList<string> RootFiles { get; }
    /// <summary>Files that, found at baseline or on disk without BepInEx, mean another loader or a leftover one.</summary>
    public abstract IReadOnlyList<string> ConflictFiles { get; }
    /// <summary>Files that must be executable after extraction (zip archives made on Windows carry no Unix modes).</summary>
    public virtual IReadOnlyList<string> ExecutableFiles => [];
    /// <summary>The Doorstop library, relative to the game root.</summary>
    public abstract string DoorstopLibrary { get; }

    /// <summary>Start the game with the loader active.</summary>
    public abstract LaunchSpec WithLoader(GameFacts facts, Func<string, string?> inherited);

    /// <summary>Start the game with the loader switched off for this run only; no file changes.</summary>
    public abstract LaunchSpec WithoutLoader(GameFacts facts);

    /// <summary>The Steam launch option that makes a start from Steam load the changes (BepInEx docs, "Running native Unix games through Steam"); null where not needed.</summary>
    public virtual string? SteamLaunchOption(string gameRoot) => null;

    /// <summary>
    /// Added to a failed start's message when the run left no BepInEx log, i.e. the loader never got
    /// into the game. Null on Windows, whose messages stay as they were.
    /// </summary>
    public virtual string? LoaderDidNotStartNote => null;

    public static LoaderKit For(GamePlatform platform) => platform switch
    {
        GamePlatform.Windows => new WindowsLoaderKit(),
        GamePlatform.Linux => new LinuxLoaderKit(),
        _ => new MacLoaderKit(),
    };

    /// <summary>Everything run_bepinex.sh exports (its defaults), for both Unix systems.</summary>
    protected static Dictionary<string, string> DoorstopVariables(string gameRoot, string libraryExtension) => new()
    {
        ["DOORSTOP_ENABLED"] = "1",
        ["DOORSTOP_TARGET_ASSEMBLY"] = gameRoot + "/" + PreloaderDll,
        ["DOORSTOP_BOOT_CONFIG_OVERRIDE"] = "",
        ["DOORSTOP_IGNORE_DISABLED_ENV"] = "0",
        ["DOORSTOP_MONO_DLL_SEARCH_PATH_OVERRIDE"] = "",
        ["DOORSTOP_MONO_DEBUG_ENABLED"] = "0",
        ["DOORSTOP_MONO_DEBUG_ADDRESS"] = "127.0.0.1:10000",
        ["DOORSTOP_MONO_DEBUG_SUSPEND"] = "0",
        ["DOORSTOP_CLR_RUNTIME_CORECLR_PATH"] = "." + libraryExtension,
        ["DOORSTOP_CLR_CORLIB_DIR"] = "",
    };
}

public sealed class WindowsLoaderKit : LoaderKit
{
    public override GamePlatform Platform => GamePlatform.Windows;
    public override string ResourceName => "BepInEx_win_x64_5.4.23.6.zip";
    public override string Sha256 => "6c57a714a892efb4e9e1fef63d608a3391f30ec984031c465404e4ee381a0755";
    public override IReadOnlyList<string> RootFiles => ["winhttp.dll", "doorstop_config.ini", ".doorstop_version"];
    public override IReadOnlyList<string> ConflictFiles => RootFiles;
    public override string DoorstopLibrary => "winhttp.dll";

    // The game loads winhttp.dll from its own folder on every start: a plain start is a start with the loader.
    public override LaunchSpec WithLoader(GameFacts facts, Func<string, string?> inherited) => LaunchSpec.Plain(facts.ExePath!);

    /// <summary>Doorstop's documented command-line switch (README, "Doorstop arguments"): <c>--doorstop-enabled false</c>.</summary>
    public override LaunchSpec WithoutLoader(GameFacts facts) =>
        new(facts.ExePath!, ["--doorstop-enabled", "false"], new Dictionary<string, string>());
}

public sealed class LinuxLoaderKit : LoaderKit
{
    public override GamePlatform Platform => GamePlatform.Linux;
    public override string ResourceName => "BepInEx_linux_x64_5.4.23.6.zip";
    public override string Sha256 => "f8ac87a97fb1ef85451c04ace1fddb7dd7eb0b079c179b989d6bc462c98bd365";
    public override IReadOnlyList<string> RootFiles => ["libdoorstop.so", StartScript, ".doorstop_version"];
    public override IReadOnlyList<string> ConflictFiles => [.. RootFiles, "winhttp.dll", "doorstop_config.ini"];
    public override IReadOnlyList<string> ExecutableFiles => [StartScript];
    public override string DoorstopLibrary => "libdoorstop.so";

    /// <summary>run_bepinex.sh on Linux: the DOORSTOP_* variables, LD_LIBRARY_PATH and LD_PRELOAD, then exec of the game itself.</summary>
    public override LaunchSpec WithLoader(GameFacts facts, Func<string, string?> inherited)
    {
        var root = facts.FullPath;
        var environment = DoorstopVariables(root, "so");
        environment["LD_LIBRARY_PATH"] = $"{root}/::{inherited("LD_LIBRARY_PATH")}";
        var preload = inherited("LD_PRELOAD");
        environment["LD_PRELOAD"] = string.IsNullOrEmpty(preload) ? DoorstopLibrary : $"{DoorstopLibrary}:{preload}";
        return new LaunchSpec(facts.ExePath!, [], environment);
    }

    public override LaunchSpec WithoutLoader(GameFacts facts) => LaunchSpec.Plain(facts.ExePath!);

    public override string? SteamLaunchOption(string gameRoot) => $"./{StartScript} %command%";

    public override string? LoaderDidNotStartNote => "SpecTuner's mod loader didn't start inside the game.";
}

public sealed class MacLoaderKit : LoaderKit
{
    /// <summary>run_bepinex.sh's default: native arm64 first, then x86_64 under Rosetta.</summary>
    public const string ArchPreference = "arm64,x86_64";
    public const string Arch = "/usr/bin/arch";

    public override GamePlatform Platform => GamePlatform.MacOS;
    public override string ResourceName => "BepInEx_macos_universal_5.4.23.6.zip";
    public override string Sha256 => "2e711e21f6570da964fce4654b1976500dbe043f61778bce0008500078564638";
    public override IReadOnlyList<string> RootFiles => ["libdoorstop.dylib", StartScript, ".doorstop_version"];
    public override IReadOnlyList<string> ConflictFiles => [.. RootFiles, "winhttp.dll", "doorstop_config.ini"];
    public override IReadOnlyList<string> ExecutableFiles => [StartScript];
    public override string DoorstopLibrary => "libdoorstop.dylib";

    /// <summary>
    /// run_bepinex.sh on macOS: the DOORSTOP_* variables and ARCHPREFERENCE, then
    /// <c>arch -e DYLD_LIBRARY_PATH=… -e DYLD_INSERT_LIBRARIES=… &lt;program&gt;</c>. The DYLD variables go
    /// on arch's final exec because dyld strips them from platform binaries such as arch itself.
    /// </summary>
    public override LaunchSpec WithLoader(GameFacts facts, Func<string, string?> inherited)
    {
        var root = facts.FullPath;
        var environment = DoorstopVariables(root, "dylib");
        environment["ARCHPREFERENCE"] = ArchPreference;
        var inheritedLibraries = inherited("DYLD_INSERT_LIBRARIES");
        if (string.IsNullOrEmpty(inheritedLibraries))
            inheritedLibraries = inherited("STEAM_DYLD_INSERT_LIBRARIES");
        var insert = string.IsNullOrEmpty(inheritedLibraries) ? DoorstopLibrary : $"{DoorstopLibrary}:{inheritedLibraries}";
        return new LaunchSpec(Arch,
            ["-e", $"DYLD_LIBRARY_PATH={root}/:{inherited("DYLD_LIBRARY_PATH")}", "-e", $"DYLD_INSERT_LIBRARIES={insert}", facts.ExePath!],
            environment);
    }

    public override LaunchSpec WithoutLoader(GameFacts facts) => LaunchSpec.Plain(facts.ExePath!);

    public override string? SteamLaunchOption(string gameRoot) => $"\"{gameRoot}/{StartScript}\" %command%";

    public override string? LoaderDidNotStartNote =>
        "SpecTuner's mod loader didn't start inside the game. macOS may be keeping add-ons out of this game's app; " +
        "SpecTuner never changes a game's app to get around that, so it may not be able to work with this game.";
}
