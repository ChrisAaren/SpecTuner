using System.Text.Encodings.Web;
using System.Text.Json;

namespace SpecTuner.Sidecar.Cli;

public static class Json
{
    // Web defaults: camelCase names, case-insensitive reads. Relaxed escaping keeps
    // apostrophes and non-ASCII game paths readable; output never goes into HTML.
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
}
