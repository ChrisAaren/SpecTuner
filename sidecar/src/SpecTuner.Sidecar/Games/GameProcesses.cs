using System.Diagnostics;
using SpecTuner.Sidecar.Cli;

namespace SpecTuner.Sidecar.Games;

public static class GameProcesses
{
    /// <summary>True when a running process was started from this exact exe.</summary>
    public static bool IsRunning(string exePath)
    {
        var fullExe = Path.GetFullPath(exePath);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullExe)))
        {
            using (process)
            {
                try
                {
                    var module = process.MainModule?.FileName;
                    if (module is not null && string.Equals(Path.GetFullPath(module), fullExe, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Same name but we can't read its path (e.g. it runs elevated). Err on the safe side.
                    if (!process.HasExited)
                        return true;
                }
            }
        }
        return false;
    }

    public static void EnsureNotRunning(GameFacts facts)
    {
        if (facts.ExePath is not null && IsRunning(facts.ExePath))
            throw new SidecarException(ErrorCodes.GameRunning,
                $"{facts.Name ?? "The game"} is running. Please close it first, then try again. Nothing was changed.");
    }
}
