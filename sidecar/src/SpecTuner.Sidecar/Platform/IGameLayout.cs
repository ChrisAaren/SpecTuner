using SpecTuner.Sidecar.Games;

namespace SpecTuner.Sidecar.Platform;

/// <summary>A reason the scope gate declines, before it becomes a <see cref="ScopeVerdict"/>.</summary>
public sealed record LayoutDecline(string Code, string Message, string Detail);

/// <summary>
/// Where things are in a game folder on one platform, and the platform-specific steps of the scope
/// gate. Works from names and file bytes only, so every platform can be tested on any host.
/// <see cref="GameInspector"/> runs the steps in the same order on every platform.
/// </summary>
public interface IGameLayout
{
    /// <summary>The folder SpecTuner works in, from the normalized folder the player picked (macOS: the folder holding the .app).</summary>
    string GameRoot(string pickedPath);

    /// <summary>Finds the game program and its data folder and fills in Exe, ExePath, Name, DataDir; or declines.</summary>
    LayoutDecline? Locate(GameFacts facts);

    /// <summary>The engine library whose presence says "Unity" (UnityPlayer.dll / .so / .dylib).</summary>
    string UnityPlayerPath(GameFacts facts);

    string? ReadUnityVersion(GameFacts facts);

    /// <summary>IL2CPP's native game code library (GameAssembly.dll / .so / .dylib).</summary>
    string GameAssemblyPath(GameFacts facts);

    string Il2CppDetail { get; }

    /// <summary>"x64", "x86", "arm64", "arm64+x64" (macOS universal) or "unknown".</summary>
    string ReadArch(GameFacts facts);

    LayoutDecline? CheckArch(GameFacts facts);

    /// <summary>Protection found in the game program itself (Windows: Steam DRM wrapper; macOS: a signature that blocks the loader).</summary>
    LayoutDecline? CheckProgram(GameFacts facts);
}

internal static class LayoutText
{
    public const string NothingChanged = "Nothing was changed.";

    public const string NotUnity =
        "This doesn't look like a game made with Unity. SpecTuner only works with Unity games. " + NothingChanged;

    public static LayoutDecline CompatibilityLayer(string system) => new(
        Cli.ErrorCodes.CompatibilityLayerNotSupported,
        $"This folder holds the Windows version of the game, which runs on {system} through a compatibility layer such as Proton or Wine. " +
        $"SpecTuner doesn't support that yet; it only works with games made for {system}. {NothingChanged}",
        "Found a Windows game (.exe with a _Data folder) and no native game.");

    /// <summary>The first line in the data files that looks like a Unity version ("2021.3.16f1", "6000.0.23f1").</summary>
    public static string? UnityVersionFromDataFiles(string dataDir)
    {
        foreach (var name in new[] { "globalgamemanagers", "data.unity3d", "mainData", "level0" })
        {
            var path = Path.Combine(dataDir, name);
            if (!File.Exists(path))
                continue;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var buffer = new byte[(int)Math.Min(8192, stream.Length)];
                stream.ReadExactly(buffer);
                var text = System.Text.Encoding.ASCII.GetString(buffer);
                var match = System.Text.RegularExpressions.Regex.Match(text, @"(?<![\d.])(\d{1,4}\.\d{1,2}\.\d{1,3}[abfpx]\d{1,3})(?![\d.])");
                if (match.Success)
                    return match.Groups[1].Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }
}
