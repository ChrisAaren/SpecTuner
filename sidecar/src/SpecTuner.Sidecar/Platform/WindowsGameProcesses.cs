using System.Diagnostics;

namespace SpecTuner.Sidecar.Platform;

/// <summary>Windows: process lookup by name and path, and a kill-on-close job object for measured runs.</summary>
public sealed class WindowsGameProcesses : IGameProcesses
{
    public bool IsRunning(string exePath)
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

    public void ReapLeftover(string guardFile)
    {
    }

    public IProcessGuard CreateGuard(string guardFile) => new JobGuard();

    /// <summary>
    /// CreateProcess inherits every inheritable handle, and the sidecar's own standard handles are
    /// the caller's pipes: a game holding them would keep the caller waiting until the game exits.
    /// So those three are made non-inheritable first, and the game gets pipes of its own that close
    /// when the sidecar exits (Windows has no SIGPIPE; a write to them just fails). CreateNoWindow
    /// only affects console programs; a game's window is not touched.
    /// </summary>
    public Process? StartDetached(LaunchSpec spec, string workingDirectory)
    {
        if (spec.Environment.Count > 0)
            throw new InvalidOperationException("Windows launches take no extra environment.");
        foreach (var std in new[] { StdInput, StdOutput, StdError })
        {
            var handle = GetStdHandle(std);
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                SetHandleInformation(handle, HandleFlagInherit, 0);
        }
        var start = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in spec.Arguments)
            start.ArgumentList.Add(argument);
        return Process.Start(start);
    }

    private const int StdInput = -10;
    private const int StdOutput = -11;
    private const int StdError = -12;
    private const uint HandleFlagInherit = 1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    private sealed class JobGuard : IProcessGuard
    {
        private readonly JobObject _job = new();

        public void Add(Process process) => _job.Add(process);

        public void Dispose() => _job.Dispose();
    }
}
