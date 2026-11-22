using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace SpecTuner.Sidecar.Games;

/// <summary>The few facts we read from a Windows PE file header.</summary>
public sealed record PeFile(string Arch, IReadOnlyList<string> SectionNames, bool HasValidMetadata)
{
    /// <summary>Returns null when the file is missing or is not a readable PE file.</summary>
    public static PeFile? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            var arch = headers.CoffHeader.Machine switch
            {
                Machine.Amd64 => "x64",
                Machine.I386 => "x86",
                Machine.Arm64 => "arm64",
                var other => other.ToString().ToLowerInvariant(),
            };
            var sections = headers.SectionHeaders.Select(s => s.Name).ToList();

            var hasMetadata = false;
            if (reader.HasMetadata)
            {
                try
                {
                    hasMetadata = reader.GetMetadataReader().IsAssembly;
                }
                catch (BadImageFormatException)
                {
                }
            }

            return new PeFile(arch, sections, hasMetadata);
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
