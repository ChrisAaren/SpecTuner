using System.Security.Cryptography;
using System.Text;

namespace SpecTuner.Sidecar.Tests.Support;

/// <summary>A synthetic Unity game folder in a temp directory.</summary>
public sealed class GameFixture
{
    public string Root { get; }
    public string Name { get; }
    public string DataDir => Path.Combine(Root, Name + "_Data");
    public string ManagedDir => Path.Combine(DataDir, "Managed");
    public string ExePath => Path.Combine(Root, Name + ".exe");

    private GameFixture(string root, string name)
    {
        Root = root;
        Name = name;
    }

    /// <summary>The bytes of a real managed assembly (this test assembly).</summary>
    public static byte[] ManagedAssembly() => File.ReadAllBytes(typeof(GameFixture).Assembly.Location);

    /// <summary>A supported Mono / x64 layout.</summary>
    public static GameFixture CreateMono(string parentDir, string name = "Fixture Game")
    {
        var fixture = new GameFixture(Path.Combine(parentDir, "game"), name);
        Directory.CreateDirectory(fixture.Root);
        fixture
            .Write(name + ".exe", PeBuilder.Build())
            .Write("UnityPlayer.dll", PeBuilder.Build())
            .Write("UnityCrashHandler64.exe", PeBuilder.Build())
            .Write("MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll", PeBuilder.Build())
            .Write("MonoBleedingEdge/etc/mono/config", "<configuration />")
            .Write(name + "_Data/app.info", "TestCompany\nFixture Product")
            .Write(name + "_Data/globalgamemanagers", RandomBytes(4096, seed: 1))
            .Write(name + "_Data/level0", RandomBytes(2048, seed: 2))
            .Write(name + "_Data/Managed/Assembly-CSharp.dll", ManagedAssembly())
            .Write(name + "_Data/Managed/UnityEngine.dll", UnityStub.Assembly);
        // Enough of a core library for the probe to compile (see UnityStub).
        foreach (var file in UnityStub.RuntimeFiles)
            File.Copy(Path.Combine(UnityStub.RuntimeDirectory, file), Path.Combine(fixture.ManagedDir, file));
        return fixture;
    }

    /// <summary>Replaces the game exe with the stand-in from SpecTuner.FakeGame (see its Program.cs).</summary>
    public GameFixture UseFakeGameExe()
    {
        var output = AppContext.BaseDirectory;
        File.Copy(Path.Combine(output, "fake-game.exe"), ExePath, overwrite: true);
        foreach (var file in new[] { "fake-game.dll", "fake-game.runtimeconfig.json", "fake-game.deps.json" })
            File.Copy(Path.Combine(output, file), Full(file), overwrite: true);
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
