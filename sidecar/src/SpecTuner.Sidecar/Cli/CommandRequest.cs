using System.Text.Json;

namespace SpecTuner.Sidecar.Cli;

public sealed class CommandRequest(JsonElement root)
{
    public JsonElement Root { get; } = root;

    public string RequireString(string name)
    {
        var value = OptionalString(name);
        if (string.IsNullOrWhiteSpace(value))
            throw new SidecarException(ErrorCodes.BadRequest, $"The request is missing \"{name}\".");
        return value;
    }

    public string? OptionalString(string name)
    {
        if (!Root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new SidecarException(ErrorCodes.BadRequest, $"\"{name}\" must be a string.");
        return value.GetString();
    }

    public double OptionalNumber(string name, double fallback, double min, double max)
    {
        if (!Root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || number < min || number > max)
            throw new SidecarException(ErrorCodes.BadRequest, $"\"{name}\" must be a number from {min} to {max}.");
        return number;
    }

    public bool OptionalBool(string name, bool fallback = false)
    {
        if (!Root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new SidecarException(ErrorCodes.BadRequest, $"\"{name}\" must be true or false."),
        };
    }
}
