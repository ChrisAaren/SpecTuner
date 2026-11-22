using System.Diagnostics;

namespace SpecTuner.Sidecar.Platform;

/// <summary>Makes sure a game the sidecar started does not outlive the sidecar.</summary>
public interface IProcessGuard : IDisposable
{
    void Add(Process process);
}

/// <summary>
/// The game process, on the machine the sidecar runs on. Always the host's implementation, also
/// when SPECTUNER_PLATFORM simulates another system, so tests can still start the fake game.
/// </summary>
public interface IGameProcesses
{
    /// <summary>True when a running process was started from this exact program file.</summary>
    bool IsRunning(string exePath);

    /// <summary>
    /// Closes a game left running by a sidecar that was killed while measuring (recorded in
    /// <paramref name="guardFile"/>), then forgets it. Windows needs nothing: its job object did it.
    /// </summary>
    void ReapLeftover(string guardFile);

    /// <summary>A guard for one measured run; the game is closed when the sidecar ends or dies.</summary>
    IProcessGuard CreateGuard(string guardFile);

    /// <summary>
    /// Starts the game for the player and lets it go: no handles or pipes are shared with the
    /// sidecar, so the sidecar can exit at once and the game keeps running. Throws
    /// <see cref="System.ComponentModel.Win32Exception"/> or <see cref="InvalidOperationException"/> when it can't start.
    /// </summary>
    Process? StartDetached(LaunchSpec spec, string workingDirectory);
}
