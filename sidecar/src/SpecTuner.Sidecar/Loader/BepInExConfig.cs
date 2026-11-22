using System.Text.RegularExpressions;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Loader;

/// <summary>
/// The one BepInEx setting SpecTuner changes, and only in a BepInEx it installed itself.
///
/// BepInEx 5 starts plugins from UnityEngine.Application's static constructor by default.
/// On the Unity 6000.6 test game that is too early: adding any plugin component there
/// crashes the game with an access violation inside GameObject.AddComponent (seen with
/// a minimal plugin built by .NET Framework's csc as well as by our compiler). Hooking
/// Camera's static constructor instead, a documented BepInEx entrypoint option, starts
/// plugins once the first scene is set up, and the same plugin then loads fine.
/// Older Unity versions keep BepInEx's default.
/// </summary>
public static partial class BepInExConfig
{
    public const string ConfigFile = "BepInEx/config/BepInEx.cfg";
    public const string LateEntrypointType = "Camera";

    [GeneratedRegex(@"(\[Preloader\.Entrypoint\][^\[]*?^Type\s*=\s*)(\S+)", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex EntrypointType();

    /// <summary>The entrypoint type to use for this Unity version, or null for BepInEx's default.</summary>
    public static string? EntrypointFor(string? unityVersion)
    {
        var major = unityVersion?.Split('.')[0];
        return int.TryParse(major, out var number) && number >= 6000 ? LateEntrypointType : null;
    }

    /// <summary>Makes BepInEx.cfg use <paramref name="type"/> as the entrypoint. Returns true if the file was written.</summary>
    public static bool EnsureEntrypoint(string gameRoot, string type)
    {
        var path = GameTree.Full(gameRoot, ConfigFile);
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, MinimalConfig(type));
            return true;
        }

        var text = File.ReadAllText(path);
        var match = EntrypointType().Match(text);
        if (!match.Success)
        {
            File.WriteAllText(path, text.TrimEnd() + Environment.NewLine + Environment.NewLine + MinimalConfig(type));
            return true;
        }
        if (match.Groups[2].Value == type)
            return false;
        File.WriteAllText(path, EntrypointType().Replace(text, m => m.Groups[1].Value + type, 1));
        return true;
    }

    // BepInEx fills in every other setting with its defaults on first launch.
    private static string MinimalConfig(string type) => $"""
        [Preloader.Entrypoint]

        ## The local filename of the assembly to target.
        # Setting type: String
        # Default value: UnityEngine.CoreModule.dll
        Assembly = UnityEngine.CoreModule.dll

        ## The name of the type in the entrypoint assembly to search for the entrypoint method.
        # Setting type: String
        # Default value: Application
        Type = {type}

        ## The name of the method in the specified entrypoint assembly and type to hook and load Chainloader from.
        # Setting type: String
        # Default value: .cctor
        Method = .cctor

        """;
}
