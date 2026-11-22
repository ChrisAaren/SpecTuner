using SpecTuner.Sidecar.Cli;
using SpecTuner.Sidecar.Games;

namespace SpecTuner.Sidecar.Platform;

/// <summary>
/// A native Linux Unity game (Unity manual, "Build a Linux application"): <c>&lt;Name&gt;.x86_64</c>
/// (older games: <c>.x86</c>, or no extension) next to <c>&lt;Name&gt;_Data/</c> and
/// <c>UnityPlayer.so</c>; IL2CPP adds <c>GameAssembly.so</c>.
/// </summary>
public sealed class LinuxLayout : IGameLayout
{
    private static readonly string[] ProgramSuffixes = [".x86_64", ".x86", ""];

    public string GameRoot(string pickedPath) => pickedPath;

    public LayoutDecline? Locate(GameFacts facts)
    {
        var root = facts.FullPath;
        var dataDirs = Directory.GetDirectories(root, "*_Data").Order(StringComparer.Ordinal).ToList();
        foreach (var dataDir in dataDirs)
        {
            var stem = Path.GetFileName(dataDir)[..^"_Data".Length];
            foreach (var suffix in ProgramSuffixes)
            {
                var program = Path.Combine(root, stem + suffix);
                if (!File.Exists(program) || (suffix.Length == 0 && ExecutableHeader.Read(program)?.Format != ExecutableHeader.Elf))
                    continue;
                facts.ExePath = program;
                facts.Exe = Path.GetFileName(program);
                facts.Name = stem;
                facts.DataDir = dataDir;
                return null;
            }
        }

        if (WindowsLayout.HasWindowsGame(root))
            return LayoutText.CompatibilityLayer("Linux");

        var loose = Directory.GetFiles(root, "*.x86_64").Concat(Directory.GetFiles(root, "*.x86")).Order(StringComparer.Ordinal).FirstOrDefault();
        if (loose is not null)
        {
            facts.Exe = Path.GetFileName(loose);
            return new LayoutDecline(ErrorCodes.NotUnity, LayoutText.NotUnity, "No <Name>_Data folder next to any .x86_64 or .x86 program.");
        }
        return new LayoutDecline(ErrorCodes.GameNotFound,
            $"SpecTuner couldn't find a game in that folder. Please pick the folder that contains the game's program file. {LayoutText.NothingChanged}",
            "No <Name>.x86_64, <Name>.x86 or <Name> program with a <Name>_Data folder in the folder root.");
    }

    public string UnityPlayerPath(GameFacts facts) => Path.Combine(facts.FullPath, "UnityPlayer.so");

    public string? ReadUnityVersion(GameFacts facts) => LayoutText.UnityVersionFromDataFiles(facts.DataDir!);

    public string GameAssemblyPath(GameFacts facts) => Path.Combine(facts.FullPath, "GameAssembly.so");

    public string Il2CppDetail => "GameAssembly.so or <Name>_Data/il2cpp_data is present.";

    public string ReadArch(GameFacts facts)
    {
        var header = ExecutableHeader.Read(facts.ExePath!);
        return header is { Format: ExecutableHeader.Elf } ? header.Arches[0] : "unknown";
    }

    public LayoutDecline? CheckArch(GameFacts facts) =>
        facts.Arch == "x64"
            ? null
            : new LayoutDecline(ErrorCodes.UnsupportedArchitecture,
                "This game is built for a kind of processor SpecTuner doesn't support. On Linux, SpecTuner works with 64-bit games " +
                $"for Intel and AMD processors (x86-64). {LayoutText.NothingChanged}",
                $"Game program architecture: {facts.Arch}.");

    // No Linux equivalent of the Windows Steam DRM wrapper is known; the managed-code check still runs.
    public LayoutDecline? CheckProgram(GameFacts facts) => null;
}
