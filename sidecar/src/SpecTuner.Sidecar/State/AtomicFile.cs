using System.Text.Json;
using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.State;

public static class AtomicFile
{
    /// <summary>Writes JSON to a temp file next to the target, flushes it to disk, then moves it over the target.</summary>
    public static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, Json.Options);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }

    public static T? ReadJson<T>(string path) where T : class
    {
        if (!File.Exists(path))
            return null;
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, Json.Options);
    }
}
