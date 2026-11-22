using System.Security.Cryptography;
using System.Text;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Compilation;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Loader;

public sealed record ProbeInstallResult(bool Installed, string? ChangeId, bool AlreadyInstalled);

/// <summary>
/// Builds the probe plugin from its embedded source against the game's own
/// assemblies and installs it as BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll
/// (the "probe" change).
/// </summary>
public static class ProbeInstaller
{
    public const string AssemblyName = "SpecTuner.Probe";

    private static readonly Lazy<IReadOnlyList<SourceFile>> LazySources = new(() =>
    {
        var assembly = typeof(ProbeInstaller).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("probe/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return new SourceFile(n["probe/".Length..], reader.ReadToEnd());
            })
            .ToList();
    });

    public static IReadOnlyList<SourceFile> Sources => LazySources.Value;

    public static string SourceHash
    {
        get
        {
            var all = string.Join("\n", Sources.Select(s => s.Path + "\n" + s.Text));
            return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(all)))[..16];
        }
    }

    public static bool IsInstalled(Ledger? ledger) =>
        ledger?.Live.Any(e => e.Kind == ChangeKinds.Probe && e.Status == ChangeStatus.Active) == true;

    /// <summary>True when the installed probe is current and nothing needs building.</summary>
    public static bool IsUpToDate(GameState state, GameRecord record, Ledger ledger) =>
        IsInstalled(ledger)
        && record.Probe?.SourceHash == SourceHash
        && File.Exists(GameTree.Full(state.GamePath, OurFiles.ProbeDll));

    /// <summary>
    /// Compiles the probe for this game. Runs before anything is written to the game
    /// folder, so a failure leaves the game untouched. BepInEx core comes from the
    /// game folder when present, otherwise from the embedded package.
    /// </summary>
    public static byte[] Build(GameFacts facts)
    {
        var onDisk = File.Exists(GameTree.Full(facts.FullPath, OurFiles.BepInExCoreDll));
        var references = PluginCompiler.GameReferences(facts, onDisk ? null : BepInExPackage.CoreImages());
        var result = PluginCompiler.Compile(AssemblyName, Sources, references);
        if (result.Success)
            return result.Assembly!;

        var first = result.Errors.Take(5).Select(e => $"{e.File}({e.Line},{e.Column}): {e.Id} {e.Message}");
        throw new SidecarException(ErrorCodes.ProbeBuildFailed,
            "SpecTuner couldn't prepare its helper for this game, so nothing was changed. This game may use a Unity version SpecTuner doesn't handle yet.",
            $"{result.ErrorCount} compile error(s): " + string.Join(" | ", first),
            new { errors = result.Errors, errorCount = result.ErrorCount });
    }

    /// <summary>Writes the built probe as a "probe" change. With <paramref name="built"/> null the current probe is kept.</summary>
    public static ProbeInstallResult Install(GameState state, GameRecord record, Ledger ledger, byte[]? built)
    {
        if (built is null)
        {
            var existing = ledger.Live.LastOrDefault(e => e.Kind == ChangeKinds.Probe);
            return new ProbeInstallResult(true, existing?.Id, AlreadyInstalled: true);
        }

        var change = ledger.Begin(ChangeKinds.Probe, "Install the SpecTuner helper",
            "Adds a small SpecTuner helper inside the game that reports your hardware, measures the frame rate during tests, " +
            "and applies the graphics settings SpecTuner chooses. No original game file is touched.");
        try
        {
            var target = GameTree.Full(state.GamePath, OurFiles.ProbeDll);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, built);
            ledger.Commit(change, [OurFiles.ProbeDll]);
        }
        catch
        {
            ledger.Abandon(change, [OurFiles.ProbeDll]);
            throw;
        }

        record.Probe = new ProbeRecord { SourceHash = SourceHash, BuiltAt = DateTime.UtcNow };
        state.SaveRecord(record);
        return new ProbeInstallResult(true, change.Id, AlreadyInstalled: false);
    }
}
