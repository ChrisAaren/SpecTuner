using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Commands;

/// <summary>A game with the baseline taken and the loader and probe in place.</summary>
public sealed class InstalledGame
{
    public required GameFacts Facts { get; init; }
    public required GameState State { get; init; }
    public required GameRecord Record { get; init; }
    public required Baseline Baseline { get; init; }
    public required bool BaselineCreated { get; init; }
    public required Ledger Ledger { get; init; }
    public required LoaderInstallResult Loader { get; init; }
    public required ProbeInstallResult Probe { get; init; }
}

public static class GameSetup
{
    /// <summary>
    /// What <c>install</c> does; other commands that need the loader and probe call this.
    /// The caller holds the game lock and has checked the game is not running.
    /// </summary>
    public static InstalledGame EnsureInstalled(GameFacts facts, GameState state)
    {
        var record = state.LoadOrCreateRecord(facts.Name);
        var baseline = state.LoadBaseline();
        var baselineCreated = baseline is null;
        if (baseline is null)
        {
            baseline = Baseline.Capture(facts.FullPath, Platform.Platforms.Current.PathsAreCaseSensitive(facts.FullPath));
            state.SaveBaseline(baseline);
        }

        var ledger = Ledger.Load(state, baseline);

        // Check the loader and build the probe before touching the game folder: if either fails, nothing changes.
        LoaderInstaller.Check(state, record, baseline, ledger);
        var probeBytes = ProbeInstaller.IsUpToDate(state, record, ledger) ? null : ProbeInstaller.Build(facts);
        var loader = LoaderInstaller.Install(state, record, baseline, ledger, facts.UnityVersion);
        var probe = ProbeInstaller.Install(state, record, ledger, probeBytes);

        return new InstalledGame
        {
            Facts = facts, State = state, Record = record, Baseline = baseline, BaselineCreated = baselineCreated,
            Ledger = ledger, Loader = loader, Probe = probe,
        };
    }
}
