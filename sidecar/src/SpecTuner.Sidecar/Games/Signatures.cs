using System.IO.Enumeration;

namespace SpecTuner.Sidecar.Games;

public enum EntryKind { File, Directory }

/// <summary>A file or folder name pattern (<c>*</c> and <c>?</c> wildcards, case-insensitive) and the product it means.</summary>
public sealed record Signature(string Pattern, EntryKind Kind, string Product)
{
    public bool Matches(string name, bool isDirectory) =>
        isDirectory == (Kind == EntryKind.Directory) && FileSystemName.MatchesSimpleExpression(Pattern, name, ignoreCase: true);
}

/// <summary>
/// Data tables for the scope gate. Extend by adding rows. These are used only to
/// detect and decline; nothing here is ever disabled, removed, or worked around.
/// </summary>
public static class Signatures
{
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
