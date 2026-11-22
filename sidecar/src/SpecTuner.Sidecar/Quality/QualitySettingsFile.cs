using System.Globalization;
using System.Text.Json;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Quality;

public enum SettingKind { Int, Number, Bool, Choice }

/// <summary>One supported quality override and its valid values.</summary>
public sealed record SettingRule(string Key, SettingKind Kind, double Min = 0, double Max = 0, int[]? Allowed = null, string[]? Choices = null)
{
    public string Describe() => Kind switch
    {
        SettingKind.Bool => "true or false",
        SettingKind.Choice => "one of " + string.Join(", ", Choices!),
        _ when Allowed is not null && Min < Max => $"{string.Join(" or ", Allowed)}, or {Kind.ToString().ToLowerInvariant()} from {Min.ToString(CultureInfo.InvariantCulture)} to {Max.ToString(CultureInfo.InvariantCulture)}",
        _ when Allowed is not null => "one of " + string.Join(", ", Allowed),
        SettingKind.Int => $"a whole number from {Min.ToString(CultureInfo.InvariantCulture)} to {Max.ToString(CultureInfo.InvariantCulture)}",
        _ => $"a number from {Min.ToString(CultureInfo.InvariantCulture)} to {Max.ToString(CultureInfo.InvariantCulture)}",
    };
}

/// <summary>
/// The overrides file <c>BepInEx/config/spectuner.quality.json</c>: a flat JSON object
/// the probe applies at every launch. Frame-cap lifting and runInBackground used during
/// benchmarks are not settings and never appear here.
/// </summary>
public static class QualitySettingsFile
{
    public static readonly SettingRule[] Rules =
    [
        new("qualityLevel", SettingKind.Int, 0, 31),
        new("vSyncCount", SettingKind.Int, 0, 4),
        new("targetFrameRate", SettingKind.Int, 10, 1000, Allowed: [-1]),
        new("resolutionWidth", SettingKind.Int, 320, 16384),
        new("resolutionHeight", SettingKind.Int, 200, 16384),
        new("fullscreenMode", SettingKind.Choice, Choices: ["ExclusiveFullScreen", "FullScreenWindow", "MaximizedWindow", "Windowed"]),
        new("renderScale", SettingKind.Number, 0.1, 2.0),
        new("shadows", SettingKind.Choice, Choices: ["Disable", "HardOnly", "All"]),
        new("shadowDistance", SettingKind.Number, 0, 10000),
        new("shadowResolution", SettingKind.Int, Allowed: [256, 512, 1024, 2048, 4096, 8192]),
        new("shadowCascades", SettingKind.Int, 1, 4),
        new("antiAliasing", SettingKind.Int, Allowed: [0, 2, 4, 8]),
        new("anisotropicFiltering", SettingKind.Choice, Choices: ["Disable", "Enable", "ForceEnable"]),
        new("textureMipmapLimit", SettingKind.Int, 0, 3),
        new("lodBias", SettingKind.Number, 0.01, 10),
        new("maximumLODLevel", SettingKind.Int, 0, 7),
        new("pixelLightCount", SettingKind.Int, 0, 64),
        new("softParticles", SettingKind.Bool),
        new("realtimeReflectionProbes", SettingKind.Bool),
    ];

    public static string ValidKeys => string.Join(", ", Rules.Select(r => r.Key));

    /// <summary>Checks one value and returns it normalized (long, double, bool or string); null means "remove".</summary>
    public static object? Validate(string key, JsonElement value, int? qualityLevelCount = null)
    {
        var rule = Rules.FirstOrDefault(r => r.Key == key)
            ?? throw new SidecarException(ErrorCodes.InvalidSetting, $"\"{key}\" is not a setting SpecTuner can change.",
                "Valid settings: " + ValidKeys);
        if (value.ValueKind == JsonValueKind.Null)
            return null;

        var max = rule.Key == "qualityLevel" && qualityLevelCount is > 0 ? qualityLevelCount.Value - 1 : rule.Max;
        SidecarException Invalid() => new(ErrorCodes.InvalidSetting,
            $"\"{key}\" must be {(max != rule.Max ? rule with { Max = max } : rule).Describe()}.", $"Got: {value.GetRawText()}");

        switch (rule.Kind)
        {
            case SettingKind.Bool:
                return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw Invalid() };
            case SettingKind.Choice:
            {
                var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                return rule.Choices!.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase)) ?? throw Invalid();
            }
            default:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw Invalid();
                if (rule.Kind == SettingKind.Int && number != Math.Floor(number))
                    throw Invalid();
                var allowed = rule.Allowed?.Contains((int)number) == true && number == Math.Floor(number);
                var inRange = rule.Min < max && number >= rule.Min && number <= max;
                if (!allowed && !inRange)
                    throw Invalid();
                return rule.Kind == SettingKind.Int ? (object)(long)number : number;
            }
        }
    }

    /// <summary>The overrides in the game folder now (empty when there is no file).</summary>
    public static SortedDictionary<string, object> Read(string gameRoot)
    {
        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var path = GameTree.Full(gameRoot, OurFiles.QualityFile);
        if (!File.Exists(path))
            return result;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = Validate(property.Name, property.Value);
            if (value is not null)
                result[property.Name] = value;
        }
        return result;
    }

    /// <summary>Applies changes (null removes a key). Throws INVALID_SETTING before anything is written.</summary>
    public static SortedDictionary<string, object> Merge(SortedDictionary<string, object> current, JsonElement settings, int? qualityLevelCount)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.EnumerateObject().Any())
            throw new SidecarException(ErrorCodes.BadRequest, "The request needs \"settings\" with at least one setting.", "Valid settings: " + ValidKeys);

        var merged = new SortedDictionary<string, object>(current, StringComparer.Ordinal);
        foreach (var property in settings.EnumerateObject())
        {
            var value = Validate(property.Name, property.Value, qualityLevelCount);
            if (value is null)
                merged.Remove(property.Name);
            else
                merged[property.Name] = value;
        }

        if (merged.ContainsKey("resolutionWidth") != merged.ContainsKey("resolutionHeight"))
            throw new SidecarException(ErrorCodes.InvalidSetting, "Set \"resolutionWidth\" and \"resolutionHeight\" together.");
        return merged;
    }

    /// <summary>
    /// Settings that a same-launch A/B trial refuses, with the reason. They cannot be switched
    /// back and forth cleanly while the game runs, or are not settings during a benchmark.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> NotTryable = new Dictionary<string, string>
    {
        ["qualityLevel"] = "switching the quality level replaces the game's whole set of graphics settings, so the two halves of the test would not differ by just this one setting",
        ["resolutionWidth"] = "changing the resolution resizes the game window, which takes a moment and disturbs the measurement",
        ["resolutionHeight"] = "changing the resolution resizes the game window, which takes a moment and disturbs the measurement",
        ["fullscreenMode"] = "switching between window and fullscreen takes a moment and disturbs the measurement",
        ["vSyncCount"] = "the frame-rate cap is always lifted while measuring",
        ["targetFrameRate"] = "the frame-rate cap is always lifted while measuring",
    };

    /// <summary>Validates <c>trySettings</c> for launch-and-benchmark: the same keys and ranges as set-quality, minus <see cref="NotTryable"/>, no nulls.</summary>
    public static SortedDictionary<string, object> ValidateTrial(JsonElement settings, int? qualityLevelCount)
    {
        if (settings.ValueKind != JsonValueKind.Object || !settings.EnumerateObject().Any())
            throw new SidecarException(ErrorCodes.BadRequest, "\"trySettings\" needs at least one setting.", "Valid settings: " + ValidKeys);

        var result = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in settings.EnumerateObject())
        {
            if (NotTryable.TryGetValue(property.Name, out var why))
                throw new SidecarException(ErrorCodes.InvalidSetting,
                    $"\"{property.Name}\" can't be tried within one test because {why}. Save it with set-quality and run a normal benchmark instead.");
            if (property.Value.ValueKind == JsonValueKind.Null)
                throw new SidecarException(ErrorCodes.InvalidSetting, $"\"{property.Name}\" needs a value to try; null is not allowed in trySettings.");
            result[property.Name] = Validate(property.Name, property.Value, qualityLevelCount)!;
        }

        if (result.ContainsKey("resolutionWidth") != result.ContainsKey("resolutionHeight"))
            throw new SidecarException(ErrorCodes.InvalidSetting, "Set \"resolutionWidth\" and \"resolutionHeight\" together.");
        return result;
    }

    /// <summary>Writes the file atomically, or deletes it when no overrides are left.</summary>
    public static void Write(string gameRoot, SortedDictionary<string, object> overrides)
    {
        var path = GameTree.Full(gameRoot, OurFiles.QualityFile);
        if (overrides.Count == 0)
            GameTree.DeleteFile(path);
        else
            AtomicFile.WriteJson(path, overrides);
    }

    public static bool SameAs(SortedDictionary<string, object> a, SortedDictionary<string, object> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && Equals(Convert.ToString(kv.Value, CultureInfo.InvariantCulture), Convert.ToString(other, CultureInfo.InvariantCulture)));
}
