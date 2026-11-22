using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Platform;

/// <summary>
/// Linux and macOS. There are no job objects, so a measured run is guarded twice: signal handlers
/// close the game when the sidecar is stopped (SIGTERM, SIGINT, SIGHUP, SIGQUIT), and a guard file
/// records the game so the next command closes it if the sidecar was killed outright (SIGKILL).
/// </summary>
public sealed class UnixGameProcesses : IGameProcesses
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    public bool IsRunning(string exePath)
    {
        var wanted = Candidates(exePath);
        return OperatingSystem.IsLinux() ? RunningOnLinux(wanted) : RunningOnMac(wanted);
    }

    /// <summary>
    /// The program path as given, and with every symbolic link in it followed, because the kernel
    /// reports the path behind the links, parent folders included. On macOS names are compared
    /// without regard to case: its volumes ignore case by default, and missing a running game is the
    /// worse mistake.
    /// </summary>
    private static HashSet<string> Candidates(string exePath)
    {
        var full = Path.GetFullPath(exePath);
        return new HashSet<string>(OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
        {
            full,
            HostFiles.RealPath(full),
        };
    }

    /// <summary>Every <c>/proc/&lt;pid&gt;/exe</c> link names the program a process runs. Names are not used: Linux cuts them at 15 characters.</summary>
    private static bool RunningOnLinux(HashSet<string> wanted)
    {
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out _))
                continue;
            try
            {
                var target = new FileInfo(Path.Combine(dir, "exe")).LinkTarget;
                if (target is null)
                    continue;
                if (target.EndsWith(" (deleted)", StringComparison.Ordinal))
                    target = target[..^" (deleted)".Length];
                if (wanted.Contains(target))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Another user's process, or it just ended.
            }
        }
        return false;
    }

    private static bool RunningOnMac(HashSet<string> wanted)
    {
        var buffer = new byte[4096];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                int length;
                try
                {
                    length = proc_pidpath(process.Id, buffer, (uint)buffer.Length);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    return false;
                }
                if (length > 0 && wanted.Contains(Encoding.UTF8.GetString(buffer, 0, length)))
                    return true;
            }
        }
        return false;
    }

    [DllImport("libproc", SetLastError = true)]
    private static extern int proc_pidpath(int pid, byte[] buffer, uint size);

    public void ReapLeftover(string guardFile)
    {
        var record = Read(guardFile);
        if (record is null)
            return;
        try
        {
            using var process = Process.GetProcessById(record.Pid);
            if (!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime() - record.StartedAt).TotalSeconds) <= StartTimeTolerance.TotalSeconds)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone.
        }
        TryDelete(guardFile);
    }

    public IProcessGuard CreateGuard(string guardFile) => new SignalGuard(guardFile);

    /// <summary>
    /// Through <c>/bin/sh</c>, which points the game's standard input and output at /dev/null before
    /// it execs the game (same process), so the game holds no pipe of the sidecar's. LD_PRELOAD is
    /// handed over under another name and set only for the exec, so the shell itself never loads
    /// Doorstop (UnityDoorstop issue 88).
    /// </summary>
    public Process? StartDetached(LaunchSpec spec, string workingDirectory)
    {
        const string script =
            "if [ -n \"${SPECTUNER_LD_PRELOAD+set}\" ]; then LD_PRELOAD=\"$SPECTUNER_LD_PRELOAD\"; export LD_PRELOAD; unset SPECTUNER_LD_PRELOAD; fi; " +
            "exec \"$0\" \"$@\" </dev/null >/dev/null 2>&1";
        var start = new ProcessStartInfo("/bin/sh") { UseShellExecute = false, WorkingDirectory = workingDirectory };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(spec.FileName);
        foreach (var argument in spec.Arguments)
            start.ArgumentList.Add(argument);
        foreach (var (key, value) in spec.Environment)
            start.Environment[key == "LD_PRELOAD" ? "SPECTUNER_LD_PRELOAD" : key] = value;

        var process = Process.Start(start);
        // 126/127: the shell could not run the program (not executable, not found).
        if (process is not null && process.WaitForExit(500) && process.ExitCode is 126 or 127)
            throw new Win32Exception($"The game program could not be run (shell exit code {process.ExitCode}).");
        return process;
    }

    private sealed record GuardRecord(int Pid, DateTime StartedAt);

    private static GuardRecord? Read(string path)
    {
        try
        {
            return AtomicFile.ReadJson<GuardRecord>(path);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class SignalGuard(string guardFile) : IProcessGuard
    {
        private readonly List<PosixSignalRegistration> _registrations = [];
        private Process? _process;

        public void Add(Process process)
        {
            _process = process;
            AtomicFile.WriteJson(guardFile, new GuardRecord(process.Id, process.StartTime.ToUniversalTime()));
            foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP, PosixSignal.SIGQUIT })
                _registrations.Add(PosixSignalRegistration.Create(signal, _ => Close()));
            AppDomain.CurrentDomain.ProcessExit += OnExit;
        }

        private void OnExit(object? sender, EventArgs e) => Close();

        /// <summary>Like the job object: the game goes when the guard goes.</summary>
        private void Close()
        {
            try
            {
                if (_process is { HasExited: false })
                    _process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
            }
        }

        public void Dispose()
        {
            Close();
            foreach (var registration in _registrations)
                registration.Dispose();
            AppDomain.CurrentDomain.ProcessExit -= OnExit;
            TryDelete(guardFile);
        }
    }
}
