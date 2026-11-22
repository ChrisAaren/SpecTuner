using System.IO.Enumeration;

namespace SpecTuner.Sidecar.State;

/// <summary>The only places inside a game folder that SpecTuner writes to (see docs/sidecar-contract.md).</summary>
public static class OurFiles
{
    public const string BepInExDir = "BepInEx";
    public static readonly string[] LoaderRootFiles = ["winhttp.dll", "doorstop_config.ini", ".doorstop_version"];
    public const string BepInExCoreDll = "BepInEx/core/BepInEx.dll";

    /// <summary>Probe plugin and generated mods.</summary>
    public const string PluginDir = "BepInEx/plugins/SpecTuner";
    /// <summary>Probe output (hardware, benchmark results).</summary>
    public const string ProbeOutputDir = "BepInEx/spectuner";
    public const string ConfigDir = "BepInEx/config";
    public const string ConfigPattern = "spectuner.*.json";

    public const string ProbeDll = PluginDir + "/SpecTuner.Probe.dll";
    public const string QualityFile = ConfigDir + "/spectuner.quality.json";
    /// <summary>A one-shot request for the probe. Transient: never snapshotted, never part of a change.</summary>
    public const string BenchmarkRequestFile = ConfigDir + "/spectuner.benchmark.json";

    public static bool IsSpecTunerConfig(string relative)
    {
        var slash = relative.LastIndexOf('/');
        return slash > 0
               && relative.AsSpan(0, slash).Equals(ConfigDir, StringComparison.OrdinalIgnoreCase)
               && FileSystemName.MatchesSimpleExpression(ConfigPattern, relative.AsSpan(slash + 1), ignoreCase: true);
    }

    /// <summary>Our mutable files, which are snapshotted before every change.</summary>
    public static IEnumerable<string> SnapshottedFiles(string gameRoot)
    {
        var pluginDir = GameTree.Full(gameRoot, PluginDir);
        if (Directory.Exists(pluginDir))
            foreach (var file in GameTree.Files(pluginDir))
                yield return PluginDir + "/" + file;

        var configDir = GameTree.Full(gameRoot, ConfigDir);
        if (Directory.Exists(configDir))
            foreach (var file in Directory.EnumerateFiles(configDir, ConfigPattern))
            {
                var relative = ConfigDir + "/" + Path.GetFileName(file);
                if (!relative.Equals(BenchmarkRequestFile, StringComparison.OrdinalIgnoreCase))
                    yield return relative;
            }
    }
}
