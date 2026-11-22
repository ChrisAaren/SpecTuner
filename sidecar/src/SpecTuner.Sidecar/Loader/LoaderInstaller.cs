using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Loader;

public sealed record LoaderStatus(bool Installed, bool Ours, string? Version);

public sealed class LoaderInstallResult
{
    public bool Ours { get; init; }
    public string? Version { get; init; }
    public bool AlreadyInstalled { get; init; }
    public string? ChangeId { get; init; }
    public int FilesAdded { get; init; }
    /// <summary>Our loader was already recorded and missing files were put back.</summary>
    public bool Repaired { get; init; }
}

/// <summary>Installs BepInEx 5 into a game folder without overwriting any original file.</summary>
public static class LoaderInstaller
{
    private const string BepInEx6CoreDll = "BepInEx/core/BepInEx.Core.dll";

    public static LoaderStatus Inspect(string gameRoot, GameRecord? record)
    {
        var dll = GameTree.Full(gameRoot, OurFiles.BepInExCoreDll);
        var installed = File.Exists(dll);
        return new LoaderStatus(installed, installed && record?.Loader?.Ours == true, installed ? ReadVersion(dll) : null);
    }

    public static string? ReadVersion(string dllPath)
    {
        var fileVersion = FileVersionInfo.GetVersionInfo(dllPath).FileVersion;
        if (!string.IsNullOrWhiteSpace(fileVersion))
            return fileVersion.Split(' ')[0];
        try
        {
            return AssemblyName.GetAssemblyName(dllPath).Version?.ToString();
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Throws UNSUPPORTED_LOADER or LOADER_CONFLICT when the loader can't be installed or used.
    /// Writes nothing, so callers can run it before doing any other work.
    /// </summary>
    public static void Check(GameState state, GameRecord record, Baseline baseline, Ledger ledger)
    {
        if (record.Loader is not null || ledger.Live.Any(e => e.Kind == ChangeKinds.Loader))
            return;

        var root = state.GamePath;
        if (baseline.Contains(OurFiles.BepInExCoreDll))
        {
            var dll = GameTree.Full(root, OurFiles.BepInExCoreDll);
            var version = File.Exists(dll) ? ReadVersion(dll) : null;
            if (!System.Version.TryParse(version, out var parsed) || parsed.Major != 5)
                throw new SidecarException(ErrorCodes.UnsupportedLoader,
                    "This game already has a version of the BepInEx mod loader that SpecTuner doesn't support. Nothing was changed.",
                    $"BepInEx/core/BepInEx.dll version: {version ?? "unknown"}; SpecTuner needs 5.x.");
            return;
        }

        if (baseline.Contains(BepInEx6CoreDll))
            throw new SidecarException(ErrorCodes.UnsupportedLoader,
                "This game already has a newer BepInEx (version 6) that SpecTuner doesn't support yet. Nothing was changed.",
                "Found BepInEx/core/BepInEx.Core.dll.");

        var conflicts = Platforms.Current.Loader.ConflictFiles.Append(OurFiles.BepInExCoreDll)
            .Where(f => baseline.Contains(f) || File.Exists(GameTree.Full(root, f)))
            .ToList();
        if (baseline.AnyUnder(OurFiles.BepInExDir))
            conflicts.Add("BepInEx/");
        if (baseline.AnyUnder("MelonLoader"))
            conflicts.Add("MelonLoader/");
        if (conflicts.Count > 0)
            throw new SidecarException(ErrorCodes.LoaderConflict,
                "This game already has another mod loader, or parts of one. SpecTuner won't install its own on top of it, " +
                "so nothing was changed.",
                "Already present: " + string.Join(", ", conflicts.Distinct()));
    }

    public static LoaderInstallResult Install(GameState state, GameRecord record, Baseline baseline, Ledger ledger, string? unityVersion)
    {
        if (record.Loader is { Ours: false })
            return new LoaderInstallResult { Ours = false, Version = record.Loader.Version, AlreadyInstalled = true };

        var existingChange = ledger.Live.LastOrDefault(e => e.Kind == ChangeKinds.Loader);
        if (existingChange is not null)
            return Repair(state, record, baseline, ledger, existingChange, unityVersion);

        Check(state, record, baseline, ledger);
        if (baseline.Contains(OurFiles.BepInExCoreDll))
            return AdoptPlayersLoader(state, record);
        return InstallOurs(state, record, baseline, ledger, unityVersion);
    }

    private static LoaderInstallResult AdoptPlayersLoader(GameState state, GameRecord record)
    {
        var version = ReadVersion(GameTree.Full(state.GamePath, OurFiles.BepInExCoreDll));
        record.Loader = new LoaderRecord { Ours = false, Version = version };
        state.SaveRecord(record);
        return new LoaderInstallResult { Ours = false, Version = version, AlreadyInstalled = true };
    }

    private static LoaderInstallResult InstallOurs(GameState state, GameRecord record, Baseline baseline, Ledger ledger, string? unityVersion)
    {
        using var archive = BepInExPackage.Open();
        var files = BepInExPackage.FilesToExtract(archive);
        var entrypoint = BepInExConfig.EntrypointFor(unityVersion);
        if (entrypoint is not null)
            files.Add(BepInExConfig.ConfigFile);
        var originals = files.Where(baseline.Contains).ToList();
        if (originals.Count > 0)
            throw new SidecarException(ErrorCodes.LoaderConflict,
                "This game already has files that the mod loader would replace, so nothing was changed.",
                "Already present: " + string.Join(", ", originals));

        var change = ledger.Begin(ChangeKinds.Loader, "Install the BepInEx mod loader",
            $"Adds BepInEx {BepInExPackage.Version}, the standard loader for Unity mods, so SpecTuner's changes can run inside the game. " +
            "No original game file is touched.");
        record.Loader = new LoaderRecord { Ours = true, Version = BepInExPackage.Version };
        state.SaveRecord(record);
        try
        {
            Extract(archive, state.GamePath, files);
            if (entrypoint is not null)
                BepInExConfig.EnsureEntrypoint(state.GamePath, entrypoint);
            ledger.Commit(change, files);
        }
        catch
        {
            ledger.Abandon(change, files);
            record.Loader = null;
            state.SaveRecord(record);
            throw;
        }

        return new LoaderInstallResult
        {
            Ours = true, Version = BepInExPackage.Version, ChangeId = change.Id, FilesAdded = files.Count,
        };
    }

    /// <summary>Our loader is recorded: put back any of its files that went missing and finish a half-done install.</summary>
    private static LoaderInstallResult Repair(GameState state, GameRecord record, Baseline baseline, Ledger ledger, ChangeEntry change, string? unityVersion)
    {
        using var archive = BepInExPackage.Open();
        var files = BepInExPackage.FilesToExtract(archive);
        var missing = files.Where(f => !File.Exists(GameTree.Full(state.GamePath, f))).ToList();
        if (missing.Any(baseline.Contains))
            throw new InvalidOperationException("Loader files overlap the baseline.");

        var wasActive = change.Status == ChangeStatus.Active;
        Extract(archive, state.GamePath, missing);
        var entrypoint = BepInExConfig.EntrypointFor(unityVersion);
        var configWritten = entrypoint is not null && BepInExConfig.EnsureEntrypoint(state.GamePath, entrypoint);
        if (entrypoint is not null)
            files.Add(BepInExConfig.ConfigFile);
        if (!wasActive || missing.Count > 0 || configWritten)
            ledger.Commit(change, files);

        if (record.Loader is not { Ours: true })
        {
            record.Loader = new LoaderRecord { Ours = true, Version = BepInExPackage.Version };
            state.SaveRecord(record);
        }

        return new LoaderInstallResult
        {
            Ours = true,
            Version = BepInExPackage.Version,
            AlreadyInstalled = wasActive && missing.Count == 0 && !configWritten,
            ChangeId = change.Id,
            FilesAdded = missing.Count,
            Repaired = missing.Count > 0 || configWritten,
        };
    }

    private static void Extract(ZipArchive archive, string root, IReadOnlyCollection<string> files)
    {
        var wanted = new HashSet<string>(files, GameTree.PathComparer);
        var executable = new HashSet<string>(Platforms.Current.Loader.ExecutableFiles, StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (!wanted.Contains(relative))
                continue;
            var target = GameTree.Full(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
            // The zips are made on Windows and carry no Unix modes: run_bepinex.sh needs its execute bit.
            if (executable.Contains(relative))
                HostFiles.MakeExecutable(target);
        }
    }
}
