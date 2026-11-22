using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Games;

public static class GameProcesses
{
    /// <summary>True when a running process was started from this exact program file.</summary>
    public static bool IsRunning(string exePath) => Platforms.Current.Processes.IsRunning(exePath);

    /// <summary>
    /// Refuses with GAME_RUNNING while the game runs. First closes a game that a killed sidecar
    /// left running during a measurement (Linux and macOS; Windows' job object already did).
    /// </summary>
    public static void EnsureNotRunning(GameFacts facts)
    {
        var processes = Platforms.Current.Processes;
        processes.ReapLeftover(GameState.For(facts.FullPath).RunningGamePath);
        if (facts.ExePath is not null && processes.IsRunning(facts.ExePath))
            throw new SidecarException(ErrorCodes.GameRunning,
                $"{facts.Name ?? "The game"} is running. Please close it first, then try again. Nothing was changed.");
    }
}
