using System.Runtime.InteropServices;

namespace SpecTuner.Sidecar.Platform;

/// <summary>The operating system a game was built for. SpecTuner works on native games of the system it runs on.</summary>
public enum GamePlatform { Windows, Linux, MacOS }

/// <summary>
/// Everything that differs between Windows, Linux and macOS, in one object. Code outside
/// <c>Platform/</c> asks the profile and never checks the operating system itself.
/// </summary>
public sealed class PlatformProfile
{
    public required GamePlatform Id { get; init; }
    /// <summary>Where things are inside a game folder, and what the scope gate reads from them.</summary>
    public required IGameLayout Layout { get; init; }
    /// <summary>Which BepInEx package, which files it adds, and how the game is started with or without it.</summary>
    public required LoaderKit Loader { get; init; }
    /// <summary>SpecTuner's state folder, Unity's Player.log, the user's home folder.</summary>
    public required UserFolders Folders { get; init; }
    /// <summary>Is the game running, starting it, closing it when the sidecar dies. Always the host's.</summary>
    public required IGameProcesses Processes { get; init; }

    /// <summary>"windows", "linux" or "macos".</summary>
    public string Name => Platforms.NameOf(Id);

    /// <summary>The game id lower-cases the path where file systems ignore case (Windows, macOS by default).</summary>
    public bool LowerCaseGameId => Id != GamePlatform.Linux;

    /// <summary>
    /// Whether the game folder's file system tells names apart by case. Windows: never asked,
    /// always false (as before). Linux and macOS: probed on the folder itself, read-only.
    /// </summary>
    public bool PathsAreCaseSensitive(string gameRoot) =>
        Id != GamePlatform.Windows && HostFiles.IsCaseSensitive(gameRoot, fallback: Id == GamePlatform.Linux);
}

public static class Platforms
{
    /// <summary>Test switch: <c>windows</c>, <c>linux</c> or <c>macos</c> selects that profile on any host. Processes stay the host's.</summary>
    public const string OverrideVariable = "SPECTUNER_PLATFORM";

    private static readonly Lazy<IGameProcesses> HostProcesses = new(() =>
        OperatingSystem.IsWindows() ? new WindowsGameProcesses() : new UnixGameProcesses());

    public static GamePlatform Host =>
        OperatingSystem.IsWindows() ? GamePlatform.Windows : OperatingSystem.IsMacOS() ? GamePlatform.MacOS : GamePlatform.Linux;

    /// <summary>True when <see cref="OverrideVariable"/> is set.</summary>
    public static bool IsOverridden => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(OverrideVariable));

    /// <summary>The override if set, otherwise the host. Read on every call, like SPECTUNER_HOME, so tests can switch it.</summary>
    public static GamePlatform CurrentId
    {
        get
        {
            var value = Environment.GetEnvironmentVariable(OverrideVariable);
            if (string.IsNullOrWhiteSpace(value))
                return Host;
            return Parse(value) ?? throw new InvalidOperationException(
                $"{OverrideVariable} must be windows, linux or macos, not \"{value}\".");
        }
    }

    public static PlatformProfile Current => For(CurrentId);

    public static PlatformProfile For(GamePlatform id) => new()
    {
        Id = id,
        Layout = id switch
        {
            GamePlatform.Windows => new WindowsLayout(),
            GamePlatform.Linux => new LinuxLayout(),
            _ => new MacLayout(RuntimeInformation.OSArchitecture),
        },
        Loader = LoaderKit.For(id),
        Folders = UserFolders.ForHost(id),
        Processes = HostProcesses.Value,
    };

    public static string NameOf(GamePlatform id) => id switch
    {
        GamePlatform.Windows => "windows",
        GamePlatform.Linux => "linux",
        _ => "macos",
    };

    public static GamePlatform? Parse(string value) => value.Trim().ToLowerInvariant() switch
    {
        "windows" => GamePlatform.Windows,
        "linux" => GamePlatform.Linux,
        "macos" or "osx" => GamePlatform.MacOS,
        _ => null,
    };
}
