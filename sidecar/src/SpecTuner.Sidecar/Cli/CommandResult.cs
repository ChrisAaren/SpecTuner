using System.Text.Json.Serialization;

namespace SpecTuner.Sidecar.Cli;

public sealed record ErrorInfo(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Detail);

public sealed class CommandResult
{
    public bool Ok { get; private init; }
    public string? Summary { get; private init; }
    public ErrorInfo? Error { get; private init; }
    public object Data { get; private init; } = new { };

    public static CommandResult Success(string summary, object data) =>
        new() { Ok = true, Summary = summary, Data = data };

    public static CommandResult Failure(string code, string message, string? detail = null, object? data = null) =>
        new() { Ok = false, Error = new ErrorInfo(code, message, detail), Data = data ?? new { } };
}

/// <summary>The one JSON object written to stdout.</summary>
public sealed class Envelope
{
    public bool Ok { get; init; }
    public string Command { get; init; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Summary { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorInfo? Error { get; init; }

    public object Data { get; init; } = new { };
}

/// <summary>A refusal or handled failure, thrown from anywhere and turned into an ok:false envelope.</summary>
public sealed class SidecarException(string code, string message, string? detail = null, object? data = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public string? Detail { get; } = detail;
    public object? ResultData { get; } = data;
}
