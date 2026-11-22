using System.Runtime.InteropServices;

namespace SpecTuner.Sidecar.Platform;

/// <summary>Facts about the machine the sidecar runs on, for <c>diagnose</c>. Nothing identifying: no names, no paths.</summary>
public static class HostFacts
{
    public static string OsVersion => Environment.OSVersion.Version.ToString();

    /// <summary>Linux: ID and VERSION_ID from /etc/os-release ("ubuntu", "24.04"); null elsewhere or when unreadable.</summary>
    public static object? LinuxDistribution()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/etc/os-release"))
            return null;
        try
        {
            string? id = null, version = null;
            foreach (var line in File.ReadLines("/etc/os-release"))
            {
                if (line.StartsWith("ID=", StringComparison.Ordinal))
                    id = line[3..].Trim('"');
                else if (line.StartsWith("VERSION_ID=", StringComparison.Ordinal))
                    version = line[11..].Trim('"');
            }
            return new { id, versionId = version };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>macOS: true when the sidecar itself runs translated by Rosetta 2; null elsewhere.</summary>
    public static bool? RunsUnderRosetta()
    {
        if (!OperatingSystem.IsMacOS())
            return null;
        try
        {
            var value = 0;
            var size = (nint)sizeof(int);
            return sysctlbyname("sysctl.proc_translated", ref value, ref size, IntPtr.Zero, 0) == 0 ? value == 1 : false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int sysctlbyname(string name, ref int value, ref nint size, IntPtr newValue, nint newSize);

    public static string OsArchitecture => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string RuntimeIdentifier => RuntimeInformation.RuntimeIdentifier;
}
