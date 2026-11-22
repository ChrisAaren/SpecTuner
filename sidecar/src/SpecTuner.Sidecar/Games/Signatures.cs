using System.IO.Enumeration;
using SpecTuner.Sidecar.Platform;

namespace SpecTuner.Sidecar.Games;

public enum EntryKind { File, Directory }

/// <summary>
/// A file or folder name pattern (<c>*</c> and <c>?</c> wildcards, case-insensitive) and the product it means.
/// <paramref name="Platforms"/> limits a row to some platforms; null means every platform.
/// </summary>
public sealed record Signature(string Pattern, EntryKind Kind, string Product, GamePlatform[]? Platforms = null)
{
    public bool Matches(string name, bool isDirectory) =>
        isDirectory == (Kind == EntryKind.Directory) && FileSystemName.MatchesSimpleExpression(Pattern, name, ignoreCase: true);

    public bool Matches(string name, bool isDirectory, GamePlatform platform) =>
        (Platforms is null || Platforms.Contains(platform)) && Matches(name, isDirectory);
}

/// <summary>
/// Data tables for the scope gate. Extend by adding rows. These are used only to
/// detect and decline; nothing here is ever disabled, removed, or worked around.
/// </summary>
public static class Signatures
{
    private static readonly GamePlatform[] Linux = [GamePlatform.Linux];
    private static readonly GamePlatform[] MacOS = [GamePlatform.MacOS];

    /// <summary>Matched against every file and folder name anywhere in the game folder.</summary>
    public static readonly Signature[] AntiCheat =
    [
        new("EasyAntiCheat", EntryKind.Directory, "Easy Anti-Cheat"),
        new("EasyAntiCheat*.dll", EntryKind.File, "Easy Anti-Cheat"),
        new("EasyAntiCheat*.exe", EntryKind.File, "Easy Anti-Cheat"),
        new("start_protected_game.exe", EntryKind.File, "Easy Anti-Cheat"),
        new("BattlEye", EntryKind.Directory, "BattlEye"),
        new("BEClient*.dll", EntryKind.File, "BattlEye"),
        new("BEService*.exe", EntryKind.File, "BattlEye"),
        new("GameGuard", EntryKind.Directory, "nProtect GameGuard"),
        new("GameGuard.des", EntryKind.File, "nProtect GameGuard"),
        new("xigncode", EntryKind.Directory, "XIGNCODE3"),
        new("x3.xem", EntryKind.File, "XIGNCODE3"),
        new("vgk.sys", EntryKind.File, "Riot Vanguard"),
        new("vgc.exe", EntryKind.File, "Riot Vanguard"),
        new("*anticheat*.dll", EntryKind.File, "anti-cheat software"),

        // Linux. Easy Anti-Cheat (EOS) bootstrapper and the renamed SDK library (Epic and Valve docs).
        new("start_protected_game", EntryKind.File, "Easy Anti-Cheat", Linux),
        new("EasyAntiCheat*.so", EntryKind.File, "Easy Anti-Cheat", Linux),
        // BattlEye's client library name comes from community reports, not from BattlEye.
        new("BEClient*.so", EntryKind.File, "BattlEye", Linux),
        new("*anticheat*.so", EntryKind.File, "anti-cheat software", Linux),

        // macOS. Easy Anti-Cheat (EOS) bootstrapper apps and per-app settings file (Epic docs).
        new("start_protected_game.app", EntryKind.Directory, "Easy Anti-Cheat", MacOS),
        new("start_protected_game_x86-64.app", EntryKind.Directory, "Easy Anti-Cheat", MacOS),
        new("*.app.eac", EntryKind.File, "Easy Anti-Cheat", MacOS),
        new("EasyAntiCheat*.dylib", EntryKind.File, "Easy Anti-Cheat", MacOS),
        new("BEClient*.dylib", EntryKind.File, "BattlEye", MacOS),
        new("*anticheat*.dylib", EntryKind.File, "anti-cheat software", MacOS),
    ];

    /// <summary>
    /// Matched against file names in <c>&lt;Name&gt;_Data/Managed/</c>. Sign-in, cloud save,
    /// analytics (Unity.Services.Authentication/CloudSave/Core*) and Steamworks.NET are
    /// deliberately absent: single-player games ship them too.
    /// </summary>
    public static readonly Signature[] OnlineMultiplayer =
    [
        new("Unity.Netcode.Runtime.dll", EntryKind.File, "Unity Netcode"),
        new("Unity.Networking.Transport.dll", EntryKind.File, "Unity Transport"),
        new("Unity.Services.Multiplayer*.dll", EntryKind.File, "Unity Multiplayer Services"),
        new("Unity.Services.Relay.dll", EntryKind.File, "Unity Relay"),
        new("Unity.Services.Lobbies.dll", EntryKind.File, "Unity Lobby"),
        new("Unity.Services.Matchmaker.dll", EntryKind.File, "Unity Matchmaker"),
        new("Unity.Multiplayer*.dll", EntryKind.File, "Unity Multiplayer"),
        new("Mirror.dll", EntryKind.File, "Mirror"),
        new("Mirror.*.dll", EntryKind.File, "Mirror"),
        new("PhotonUnityNetworking.dll", EntryKind.File, "Photon"),
        new("PhotonRealtime.dll", EntryKind.File, "Photon"),
        new("Photon3Unity3D.dll", EntryKind.File, "Photon"),
        new("Fusion.Runtime.dll", EntryKind.File, "Photon Fusion"),
        new("FishNet.Runtime.dll", EntryKind.File, "FishNet"),
        new("LiteNetLib.dll", EntryKind.File, "LiteNetLib"),
        new("DarkRift*.dll", EntryKind.File, "DarkRift"),
        new("RiptideNetworking.dll", EntryKind.File, "Riptide"),
        new("Nakama.dll", EntryKind.File, "Nakama"),
    ];
}
