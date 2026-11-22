using System.Security.Cryptography;
using System.Text;
using SpecTuner.Sidecar.Platform;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>A synthetic Unity game folder in a temp directory, shaped like a Windows, Linux or macOS game.</summary>
public sealed class GameFixture
{
    public string Root { get; }
    public string Name { get; }
    public GamePlatform Platform { get; }

    /// <summary>The data folder relative to the root, forward slashes: <c>Name_Data</c>, or <c>Name.app/Contents/Resources/Data</c> on macOS.</summary>
    public string DataRelative => Platform == GamePlatform.MacOS ? $"{Name}.app/Contents/Resources/Data" : Name + "_Data";
    public string DataDir => Full(DataRelative);
    public string ManagedDir => Path.Combine(DataDir, "Managed");

    /// <summary>The program that is started, relative to the root.</summary>
    public string ExeRelative => Platform switch
    {
        GamePlatform.Windows => Name + ".exe",
        GamePlatform.Linux => Name + ".x86_64",
        _ => $"{Name}.app/Contents/MacOS/{Name}",
    };
    public string ExePath => Full(ExeRelative);
    public string UnityPlayerRelative => Platform switch
    {
        GamePlatform.Windows => "UnityPlayer.dll",
        GamePlatform.Linux => "UnityPlayer.so",
        _ => $"{Name}.app/Contents/Frameworks/UnityPlayer.dylib",
    };

    public string GameAssemblyRelative => Platform switch
    {
        GamePlatform.Windows => "GameAssembly.dll",
        GamePlatform.Linux => "GameAssembly.so",
        _ => $"{Name}.app/Contents/Frameworks/GameAssembly.dylib",
    };

    /// <summary>
    /// A path written for the Windows layout, as it is in this fixture's layout: <c>Fixture Game_Data/…</c>
    /// becomes the data folder, <c>UnityPlayer.dll</c> and <c>GameAssembly.dll</c> the platform's libraries.
    /// Unchanged on Windows.
    /// </summary>
    public string Native(string windowsRelative)
    {
        if (windowsRelative == "UnityPlayer.dll")
            return UnityPlayerRelative;
        if (windowsRelative == "GameAssembly.dll")
            return GameAssemblyRelative;
        var data = Name + "_Data";
        return windowsRelative == data || windowsRelative.StartsWith(data + "/", StringComparison.Ordinal)
            ? DataRelative + windowsRelative[data.Length..]
            : windowsRelative;
    }

    /// <summary>macOS: the .app bundle folder; null elsewhere.</summary>
    public string? AppDir => Platform == GamePlatform.MacOS ? Full(Name + ".app") : null;

    private GameFixture(string root, string name, GamePlatform platform)
    {
        Root = root;
        Name = name;
        Platform = platform;
    }

    /// <summary>The bytes of a real managed assembly (this test assembly).</summary>
    public static byte[] ManagedAssembly() => File.ReadAllBytes(typeof(GameFixture).Assembly.Location);

    /// <summary>A supported Mono layout for the current platform profile (the host's, unless SPECTUNER_PLATFORM says otherwise).</summary>
    public static GameFixture CreateMono(string parentDir, string name = "Fixture Game") => Create(parentDir, Platforms.CurrentId, name);

    /// <summary>A supported Mono layout for <paramref name="platform"/> (x64; on macOS universal arm64 + x64, ad-hoc signed like a Unity build).</summary>
    public static GameFixture Create(string parentDir, GamePlatform platform, string name = "Fixture Game")
    {
        var fixture = new GameFixture(Path.Combine(parentDir, "game"), name, platform);
        Directory.CreateDirectory(fixture.Root);
        switch (platform)
        {
            case GamePlatform.Windows:
                fixture
                    .Write(name + ".exe", PeBuilder.Build())
                    .Write("UnityPlayer.dll", PeBuilder.Build())
                    .Write("UnityCrashHandler64.exe", PeBuilder.Build())
                    .Write("MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", PeBuilder.Build());
                break;
            case GamePlatform.Linux:
                fixture
                    .Write(name + ".x86_64", ElfBuilder.Build())
                    .Write("UnityPlayer.so", ElfBuilder.Build())
                    .Write(name + "_Data/MonoBleedingEdge/x86_64/libmonobdwgc-2.0.so", ElfBuilder.Build());
                break;
            default:
                var adHoc = new MachOSignature(HardenedRuntime: false);
                fixture
                    .Write(fixture.ExeRelative, MachOBuilder.Universal(MachOBuilder.Thin(MachOBuilder.Arm64, adHoc), MachOBuilder.Thin(MachOBuilder.X64)))
                    .Write($"{name}.app/Contents/Info.plist", InfoPlist(name))
                    .Write($"{name}.app/Contents/Frameworks/UnityPlayer.dylib", MachOBuilder.Thin(MachOBuilder.Arm64))
                    .Write($"{name}.app/Contents/Frameworks/libmonobdwgc-2.0.dylib", MachOBuilder.Thin(MachOBuilder.Arm64));
                break;
        }
        var data = fixture.DataRelative;
        fixture
            .Write("MonoBleedingEdge/etc/mono/config", "<configuration />")
            .Write(data + "/app.info", "TestCompany\nFixture Product")
            .Write(data + "/globalgamemanagers", RandomBytes(4096, seed: 1))
            .Write(data + "/level0", RandomBytes(2048, seed: 2))
            .Write(data + "/Managed/Assembly-CSharp.dll", ManagedAssembly())
            .Write(data + "/Managed/UnityEngine.dll", UnityStub.Assembly);
        // Enough of a core library for the probe to compile (see UnityStub).
        foreach (var file in UnityStub.RuntimeFiles)
            File.Copy(Path.Combine(UnityStub.RuntimeDirectory, file), Path.Combine(fixture.ManagedDir, file));
        return fixture;
    }

    public static string InfoPlist(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>CFBundleExecutable</key>
        	<string>{executable}</string>
        	<key>CFBundleIdentifier</key>
        	<string>com.example.fixture</string>
        </dict>
        </plist>
        """;

    /// <summary>
    /// Replaces the game program with the stand-in from SpecTuner.FakeGame (see its Program.cs): the
    /// host's apphost (<c>fake-game.exe</c> on Windows, <c>fake-game</c> elsewhere) plus its dll, copied
    /// next to the program. Works only for a fixture shaped like the host's own games.
    /// </summary>
    public GameFixture UseFakeGameExe()
    {
        var output = AppContext.BaseDirectory;
        var apphost = OperatingSystem.IsWindows() ? "fake-game.exe" : "fake-game";
        File.Copy(Path.Combine(output, apphost), ExePath, overwrite: true);
        HostFiles.MakeExecutable(ExePath);
        var programDir = Path.GetDirectoryName(ExePath)!;
        foreach (var file in new[] { "fake-game.dll", "fake-game.runtimeconfig.json", "fake-game.deps.json" })
            File.Copy(Path.Combine(output, file), Path.Combine(programDir, file), overwrite: true);
        return this;
    }

    public GameFixture Write(string relative, byte[] content)
    {
        var path = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, content);
        return this;
    }

    public GameFixture Write(string relative, string content) => Write(relative, Encoding.UTF8.GetBytes(content));

    public GameFixture Mkdir(string relative)
    {
        Directory.CreateDirectory(Full(relative));
        return this;
    }

    public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public bool Exists(string relative) => File.Exists(Full(relative)) || Directory.Exists(Full(relative));

    /// <summary>(relative path, sha256) for every file, sorted. Used to prove restore leaves the folder as found.</summary>
    public SortedDictionary<string, string> Hashes() => Hashes(Root);

    public static SortedDictionary<string, string> Hashes(string root)
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            result[Path.GetRelativePath(root, file).Replace('\\', '/')] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        return result;
    }

    public static List<string> Directories(string root) =>
        Directory.EnumerateDirectories(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Select(d => Path.GetRelativePath(root, d).Replace('\\', '/'))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }
}
