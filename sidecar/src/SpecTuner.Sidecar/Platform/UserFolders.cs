namespace SpecTuner.Sidecar.Platform;

/// <summary>
/// Folders in the user's home that SpecTuner reads or writes: its own state root (when
/// SPECTUNER_HOME is not set) and Unity's Player.log. Built from a home folder and an
/// environment lookup, so tests can give made-up ones.
/// </summary>
public sealed class UserFolders
{
    private readonly Func<string, string?> _environment;

    public GamePlatform Platform { get; }
    /// <summary>The user's home folder (Windows: the user profile).</summary>
    public string Home { get; }

    public UserFolders(GamePlatform platform, string home, Func<string, string?>? environment = null)
    {
        Platform = platform;
        Home = home;
        _environment = environment ?? Environment.GetEnvironmentVariable;
    }

    public static UserFolders ForHost(GamePlatform platform) =>
        new(platform, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Windows: <c>%LOCALAPPDATA%\SpecTuner</c> (unchanged). macOS: <c>~/Library/Application Support/SpecTuner</c>.
    /// Linux: <c>${XDG_DATA_HOME:-~/.local/share}/SpecTuner</c>.
    /// </summary>
    public string StateRoot => Platform switch
    {
        GamePlatform.Windows => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpecTuner"),
        GamePlatform.MacOS => Path.Combine(Home, "Library", "Application Support", "SpecTuner"),
        _ => Path.Combine(XdgDataHome, "SpecTuner"),
    };

    private string XdgDataHome
    {
        get
        {
            // The XDG spec says a relative value is invalid and must be ignored.
            var value = _environment("XDG_DATA_HOME");
            return !string.IsNullOrEmpty(value) && value.StartsWith('/') ? value : Path.Combine(Home, ".local", "share");
        }
    }

    /// <summary>
    /// Where Unity writes Player.log (Unity manual, "Log files"). Windows:
    /// <c>%USERPROFILE%\AppData\LocalLow\&lt;company&gt;\&lt;product&gt;</c>; Linux:
    /// <c>~/.config/unity3d/&lt;company&gt;/&lt;product&gt;</c>; macOS: <c>~/Library/Logs/&lt;company&gt;/&lt;product&gt;</c>.
    /// </summary>
    public string PlayerLogPath(string company, string product) => Platform switch
    {
        GamePlatform.Windows => Path.Combine(Home, "AppData", "LocalLow", company, product, "Player.log"),
        GamePlatform.MacOS => Path.Combine(Home, "Library", "Logs", company, product, "Player.log"),
        _ => Path.Combine(Home, ".config", "unity3d", company, product, "Player.log"),
    };
}
