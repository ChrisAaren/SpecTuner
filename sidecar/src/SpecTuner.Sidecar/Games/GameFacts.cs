using System.Text.Json.Serialization;
using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.Games;

/// <summary>What detection found out about a game folder. Serialized as <c>data.game</c>.</summary>
public sealed class GameFacts
{
    public string? Name { get; set; }
    public string? Exe { get; set; }
    public string? UnityVersion { get; set; }
    public string? Backend { get; set; }
    public string? Arch { get; set; }
    public string? Company { get; set; }
    public string? Product { get; set; }

    [JsonIgnore] public string FullPath { get; set; } = "";
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
