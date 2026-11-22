using System.Text.Json.Serialization;
using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.Games;

/// <summary>What detection found out about a game folder. Serialized as <c>data.game</c>.</summary>
public sealed class GameFacts
{
    public string? Name { get; set; }
    /// <summary>The game's program as the player sees it: <c>Game.exe</c>, <c>Game.x86_64</c>, <c>Game.app</c>.</summary>
    public string? Exe { get; set; }
    public string? UnityVersion { get; set; }
    public string? Backend { get; set; }
    public string? Arch { get; set; }
    public string? Company { get; set; }
    public string? Product { get; set; }
    /// <summary>"windows", "linux" or "macos": the profile the folder was inspected with.</summary>
    public string? Platform { get; set; }

    [JsonIgnore] public string FullPath { get; set; } = "";
    /// <summary>The program file that is started (on macOS the one inside <c>Contents/MacOS</c>).</summary>
    [JsonIgnore] public string? ExePath { get; set; }
    [JsonIgnore] public string? DataDir { get; set; }
    [JsonIgnore] public string? ManagedDir => DataDir is null ? null : Path.Combine(DataDir, "Managed");
}

public sealed class ScopeVerdict
{
    public required GameFacts Facts { get; init; }
    public string? Code { get; init; }
    public string? Message { get; init; }
    public string? Detail { get; init; }
    public bool Supported => Code is null;

    public SidecarException ToException() =>
        new(Code!, Message!, Detail, new { supported = false, game = Facts });
}
