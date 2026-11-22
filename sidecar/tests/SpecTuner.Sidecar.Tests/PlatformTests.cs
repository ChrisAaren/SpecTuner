using System.Runtime.InteropServices;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>The platform layer's pure parts: profile selection, program headers, user folders, loader kits. Runs on every system.</summary>
[TestClass]
public sealed class PlatformTests : SidecarTest
{
    private static string Slashes(string path) => path.Replace('\\', '/');

    [TestMethod]
    public void Profile_ComesFromTheHost_OrFromTheOverride()
    {
        Assert.AreEqual(Platforms.Host, Platforms.CurrentId);
        Assert.IsFalse(Platforms.IsOverridden);
        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            UsePlatform(platform);
            Assert.AreEqual(platform, Platforms.Current.Id);
            Assert.AreEqual(platform, Platforms.Current.Loader.Platform);
            Assert.AreEqual(platform, Platforms.Current.Folders.Platform);
        }
        Environment.SetEnvironmentVariable(Platforms.OverrideVariable, "beos");
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = Platforms.CurrentId);
    }

    [TestMethod]
    public void ProcessesAreAlwaysTheHosts()
    {
        UsePlatform(GamePlatform.Linux);
        var linux = Platforms.Current.Processes;
        UsePlatform(GamePlatform.Windows);
        Assert.AreSame(linux, Platforms.Current.Processes);
        Assert.AreEqual(OperatingSystem.IsWindows(), linux is WindowsGameProcesses);
    }

    [TestMethod]
    public void ElfHeaders_GiveTheArchitecture()
    {
        var dir = Path.Combine(TempDir, "elf");
        Directory.CreateDirectory(dir);
        foreach (var (machine, arch) in new[] { (ElfBuilder.MachineX64, "x64"), (ElfBuilder.MachineX86, "x86"), (ElfBuilder.MachineArm64, "arm64") })
        {
            var path = Path.Combine(dir, arch);
            File.WriteAllBytes(path, ElfBuilder.Build(machine));
            var header = ExecutableHeader.Read(path)!;
            Assert.AreEqual(ExecutableHeader.Elf, header.Format);
            CollectionAssert.AreEqual(new[] { arch }, header.Arches.ToArray());
        }
        File.WriteAllBytes(Path.Combine(dir, "pe"), PeBuilder.Build());
        Assert.AreEqual(ExecutableHeader.Pe, ExecutableHeader.Read(Path.Combine(dir, "pe"))!.Format);
        File.WriteAllText(Path.Combine(dir, "text"), "#!/bin/sh\necho hi\n");
        Assert.IsNull(ExecutableHeader.Read(Path.Combine(dir, "text")));
        Assert.IsNull(ExecutableHeader.Read(Path.Combine(dir, "missing")));
    }

    [TestMethod]
    public void MachOHeaders_GiveSlicesAndWhatTheirSignatureSays()
    {
        var hardened = new MachOSignature(true, MachOBuilder.DisableLibraryValidation);
        var file = MachOBuilder.Universal(MachOBuilder.Thin(MachOBuilder.Arm64, hardened), MachOBuilder.Thin(MachOBuilder.X64));
        using var stream = new MemoryStream(file);

        var header = ExecutableHeader.Read(stream)!;

        Assert.AreEqual(ExecutableHeader.MachOUniversal, header.Format);
        CollectionAssert.AreEqual(new[] { "arm64", "x64" }, header.Arches.ToArray());
        var arm = header.Slices[0];
        Assert.IsTrue(arm.Signed);
        Assert.IsTrue(arm.HardenedRuntime);
        Assert.IsTrue(arm.DisablesLibraryValidation);
        Assert.IsFalse(arm.AllowsDyldEnvironment);
        Assert.IsFalse(header.Slices[1].Signed);

        using var thin = new MemoryStream(MachOBuilder.Thin(MachOBuilder.X64, new MachOSignature(false)));
        var thinHeader = ExecutableHeader.Read(thin)!;
        Assert.AreEqual(ExecutableHeader.MachO, thinHeader.Format);
        Assert.IsFalse(thinHeader.Slices[0].HardenedRuntime);

        // A Java class file shares the universal magic; its "slice count" is a version number.
        using var java = new MemoryStream([0xCA, 0xFE, 0xBA, 0xBE, 0x00, 0x00, 0x00, 0x34, 0, 0, 0, 0]);
        Assert.IsNull(ExecutableHeader.Read(java));
    }

    [TestMethod]
    public void UserFolders_FollowEachSystemsConventions()
    {
        var linux = new UserFolders(GamePlatform.Linux, "/home/player", _ => null);
        Assert.AreEqual("/home/player/.local/share/SpecTuner", Slashes(linux.StateRoot));
        Assert.AreEqual("/home/player/.config/unity3d/Co/Game/Player.log", Slashes(linux.PlayerLogPath("Co", "Game")));
        var xdg = new UserFolders(GamePlatform.Linux, "/home/player", name => name == "XDG_DATA_HOME" ? "/data/share" : null);
        Assert.AreEqual("/data/share/SpecTuner", Slashes(xdg.StateRoot));
        var relativeXdg = new UserFolders(GamePlatform.Linux, "/home/player", name => name == "XDG_DATA_HOME" ? "data" : null);
        Assert.AreEqual("/home/player/.local/share/SpecTuner", Slashes(relativeXdg.StateRoot), "a relative XDG_DATA_HOME is ignored");

        var mac = new UserFolders(GamePlatform.MacOS, "/Users/player");
        Assert.AreEqual("/Users/player/Library/Application Support/SpecTuner", Slashes(mac.StateRoot));
        Assert.AreEqual("/Users/player/Library/Logs/Co/Game/Player.log", Slashes(mac.PlayerLogPath("Co", "Game")));

        var windows = new UserFolders(GamePlatform.Windows, @"C:\Users\Player");
        Assert.AreEqual(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpecTuner"), windows.StateRoot);
        Assert.AreEqual(@"C:/Users/Player/AppData/LocalLow/Co/Game/Player.log", Slashes(windows.PlayerLogPath("Co", "Game")));
    }

    [TestMethod]
    public void StateRoot_UsesTheProfile_WhenSpectunerHomeIsUnset()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_HOME", null);
        UsePlatform(GamePlatform.MacOS);
        StringAssert.EndsWith(Slashes(SidecarHome.Root), "/Library/Application Support/SpecTuner");
        UsePlatform(GamePlatform.Linux);
        StringAssert.EndsWith(Slashes(SidecarHome.Root), "/SpecTuner");
    }

    [TestMethod]
    public void GameId_IgnoresCase_ExceptOnLinux()
    {
        var upper = Path.Combine(TempDir, "Games", "Sample Game");
        var lower = Path.Combine(TempDir, "games", "sample game");
        UsePlatform(GamePlatform.Windows);
        Assert.AreEqual(SidecarHome.GameId(upper), SidecarHome.GameId(lower));
        UsePlatform(GamePlatform.MacOS);
        Assert.AreEqual(SidecarHome.GameId(upper), SidecarHome.GameId(lower));
        UsePlatform(GamePlatform.Linux);
        Assert.AreNotEqual(SidecarHome.GameId(upper), SidecarHome.GameId(lower));
    }

    [TestMethod]
    public void CaseSensitiveBaseline_KeepsNamesThatDifferOnlyByCase()
    {
        var baseline = new Baseline
        {
            CaseSensitive = true,
            Files = [new BaselineFile { Path = "Data/Foo.txt" }, new BaselineFile { Path = "Data/foo.txt" }],
            Directories = ["Data"],
        };
        Assert.HasCount(2, baseline.ByPath);
        Assert.IsTrue(baseline.Contains("Data/foo.txt"));
        Assert.IsFalse(baseline.Contains("Data/FOO.txt"));
        Assert.IsFalse(baseline.ContainsDirectory("data"));

        var insensitive = new Baseline { Files = [new BaselineFile { Path = "Data/Foo.txt" }] };
        Assert.IsTrue(insensitive.Contains("data/FOO.TXT"), "Windows and old baselines compare without case");
        Assert.DoesNotContain("caseSensitive", Cli.Json.Serialize(insensitive), "absent from Windows baselines");
        StringAssert.Contains(Cli.Json.Serialize(baseline), "\"caseSensitive\": true");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "NTFS folders ignore case by default; the Linux variant runs on Linux.")]
    public void HostCaseProbe_SaysWindowsFoldersIgnoreCase()
    {
        var game = CreateGame();
        Assert.IsFalse(HostFiles.IsCaseSensitive(game.Root, fallback: true));
        Assert.IsFalse(Platforms.For(GamePlatform.Windows).PathsAreCaseSensitive(game.Root), "Windows never asks");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux, IgnoreMessage = "Linux file systems tell names apart by case; checked on Linux only.")]
    public void HostCaseProbe_SaysLinuxFoldersTellCaseApart()
    {
        var game = CreateGame();
        Assert.IsTrue(HostFiles.IsCaseSensitive(game.Root, fallback: false));
        game.Write("Readme.txt", "a").Write("readme.txt", "b");
        AssertOk(Run("install", new { gamePath = game.Root }));
        AssertOk(Run("revert", new { gamePath = game.Root, all = true }));
        Assert.AreEqual("a", File.ReadAllText(game.Full("Readme.txt")));
        Assert.AreEqual("b", File.ReadAllText(game.Full("readme.txt")));
    }

    [TestMethod]
    public void RealPath_OfAFile_NamesThatFile()
    {
        var file = Path.Combine(TempDir, "plain", "program");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        var real = HostFiles.RealPath(file);

        Assert.AreEqual("x", File.ReadAllText(real));
        Assert.AreEqual("program", Path.GetFileName(real));
        Assert.AreEqual(real, HostFiles.RealPath(real), "nothing left to follow");
        Assert.AreEqual(Path.Combine(HostFiles.RealPath(TempDir), "plain", "missing"), HostFiles.RealPath(Path.Combine(TempDir, "plain", "missing")),
            "a missing file keeps its name below the folder's real path");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX, IgnoreMessage = "Linked folders as Linux and macOS have them; the kernel reports a program's path behind the links there.")]
    public void RealPath_FollowsLinkedFoldersAndLinkedFiles()
    {
        var library = Path.Combine(HostFiles.RealPath(TempDir), "library");
        var program = Path.Combine(library, "Sample Game", "game.x86_64");
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, "x");
        // A folder link with a relative target, a folder link to that link, and a file link.
        File.CreateSymbolicLink(Path.Combine(TempDir, "games"), "library");
        File.CreateSymbolicLink(Path.Combine(TempDir, "shortcut"), Path.Combine(TempDir, "games"));
        File.CreateSymbolicLink(Path.Combine(library, "start"), Path.Combine("Sample Game", "game.x86_64"));

        Assert.AreEqual(program, HostFiles.RealPath(Path.Combine(TempDir, "games", "Sample Game", "game.x86_64")));
        Assert.AreEqual(program, HostFiles.RealPath(Path.Combine(TempDir, "shortcut", "Sample Game", "game.x86_64")));
        Assert.AreEqual(program, HostFiles.RealPath(Path.Combine(TempDir, "shortcut", "start")));
    }

    [TestMethod]
    public void EveryLoaderKit_OpensItsEmbeddedPackage_WithTheSameCore()
    {
        var cores = new List<string>();
        foreach (var platform in Enum.GetValues<GamePlatform>())
        {
            var kit = LoaderKit.For(platform);
            using var archive = BepInExPackage.Open(kit);
            var files = BepInExPackage.FilesToExtract(archive);
            foreach (var root in kit.RootFiles)
                CollectionAssert.Contains(files, root, $"{platform}: {root}");
            CollectionAssert.Contains(files, LoaderKit.PreloaderDll);
            CollectionAssert.Contains(files, kit.DoorstopLibrary);
            cores.Add(string.Join(",", archive.Entries.Where(e => e.FullName.StartsWith("BepInEx/core/", StringComparison.Ordinal))
                .OrderBy(e => e.FullName, StringComparer.Ordinal).Select(e => $"{e.FullName}:{e.Length}:{e.Crc32}")));
        }
        Assert.AreEqual(1, cores.Distinct().Count(), "BepInEx/core is the same in every package");
        CollectionAssert.AreEqual(new[] { "run_bepinex.sh" }, LoaderKit.For(GamePlatform.Linux).ExecutableFiles.ToArray());
        CollectionAssert.AreEqual(new[] { "run_bepinex.sh" }, LoaderKit.For(GamePlatform.MacOS).ExecutableFiles.ToArray());
        Assert.IsEmpty(LoaderKit.For(GamePlatform.Windows).ExecutableFiles);
    }

    private static GameFacts Facts(string root, string exe) => new() { FullPath = root, ExePath = exe, Name = "Sample Game" };

    [TestMethod]
    public void WindowsLaunch_IsPlain_AndSwitchesDoorstopOffWithItsArgument()
    {
        var kit = LoaderKit.For(GamePlatform.Windows);
        var facts = Facts(@"C:\Games\Sample Game", @"C:\Games\Sample Game\Sample Game.exe");
        var with = kit.WithLoader(facts, _ => null);
        Assert.AreEqual(facts.ExePath, with.FileName);
        Assert.IsEmpty(with.Arguments);
        Assert.IsEmpty(with.Environment);
        var without = kit.WithoutLoader(facts);
        CollectionAssert.AreEqual(new[] { "--doorstop-enabled", "false" }, without.Arguments.ToArray());
        Assert.IsEmpty(without.Environment);
        Assert.IsNull(kit.SteamLaunchOption(facts.FullPath));
    }

    [TestMethod]
    public void LinuxLaunch_SetsWhatRunBepInExShExports()
    {
        var kit = LoaderKit.For(GamePlatform.Linux);
        var facts = Facts("/home/player/Games/Sample Game", "/home/player/Games/Sample Game/Sample Game.x86_64");
        var inherited = new Dictionary<string, string> { ["LD_PRELOAD"] = "gameoverlayrenderer.so", ["LD_LIBRARY_PATH"] = "/usr/lib/steam" };

        var spec = kit.WithLoader(facts, name => inherited.GetValueOrDefault(name));

        Assert.AreEqual(facts.ExePath, spec.FileName);
        Assert.IsEmpty(spec.Arguments);
        var env = spec.Environment;
        Assert.AreEqual("libdoorstop.so:gameoverlayrenderer.so", env["LD_PRELOAD"]);
        Assert.AreEqual("/home/player/Games/Sample Game/::/usr/lib/steam", env["LD_LIBRARY_PATH"]);
        Assert.AreEqual("1", env["DOORSTOP_ENABLED"]);
        Assert.AreEqual("/home/player/Games/Sample Game/BepInEx/core/BepInEx.Preloader.dll", env["DOORSTOP_TARGET_ASSEMBLY"]);
        Assert.AreEqual("0", env["DOORSTOP_IGNORE_DISABLED_ENV"]);
        Assert.AreEqual("127.0.0.1:10000", env["DOORSTOP_MONO_DEBUG_ADDRESS"]);
        Assert.AreEqual(".so", env["DOORSTOP_CLR_RUNTIME_CORECLR_PATH"]);
        Assert.AreEqual("libdoorstop.so", kit.WithLoader(facts, _ => null).Environment["LD_PRELOAD"]);

        var plain = kit.WithoutLoader(facts);
        Assert.AreEqual(facts.ExePath, plain.FileName);
        Assert.IsEmpty(plain.Arguments);
        Assert.IsEmpty(plain.Environment);
        Assert.AreEqual("./run_bepinex.sh %command%", kit.SteamLaunchOption(facts.FullPath));
    }

    [TestMethod]
    public void MacLaunch_GoesThroughArch_WithTheDyldVariablesOnItsCommandLine()
    {
        var kit = LoaderKit.For(GamePlatform.MacOS);
        var facts = Facts("/Users/player/Games/Sample Game", "/Users/player/Games/Sample Game/Sample Game.app/Contents/MacOS/Sample Game");

        var spec = kit.WithLoader(facts, name => name == "STEAM_DYLD_INSERT_LIBRARIES" ? "steamloader.dylib" : null);

        Assert.AreEqual("/usr/bin/arch", spec.FileName);
        CollectionAssert.AreEqual(new[]
        {
            "-e", "DYLD_LIBRARY_PATH=/Users/player/Games/Sample Game/:",
            "-e", "DYLD_INSERT_LIBRARIES=libdoorstop.dylib:steamloader.dylib",
            facts.ExePath,
        }, spec.Arguments.ToArray());
        Assert.IsFalse(spec.Environment.Keys.Any(k => k.StartsWith("DYLD_", StringComparison.Ordinal)), "dyld strips DYLD_* from arch: never in the environment");
        Assert.AreEqual("arm64,x86_64", spec.Environment["ARCHPREFERENCE"]);
        Assert.AreEqual("1", spec.Environment["DOORSTOP_ENABLED"]);
        Assert.AreEqual(".dylib", spec.Environment["DOORSTOP_CLR_RUNTIME_CORECLR_PATH"]);
        Assert.AreEqual("/Users/player/Games/Sample Game/BepInEx/core/BepInEx.Preloader.dll", spec.Environment["DOORSTOP_TARGET_ASSEMBLY"]);

        Assert.AreEqual(facts.ExePath, kit.WithoutLoader(facts).FileName);
        Assert.AreEqual("\"/Users/player/Games/Sample Game/run_bepinex.sh\" %command%", kit.SteamLaunchOption(facts.FullPath));
    }

    [TestMethod]
    public void MacLayout_RunsArm64First_OnAppleSilicon_AndOnlyX64OnIntel()
    {
        CollectionAssert.AreEqual(new[] { "arm64", "x64" }, new MacLayout(Architecture.Arm64).RunnableArches.ToArray());
        CollectionAssert.AreEqual(new[] { "x64" }, new MacLayout(Architecture.X64).RunnableArches.ToArray());
    }

    [TestMethod]
    public void SteamGames_AreRecognisedByTheirFolder()
    {
        Assert.IsTrue(Commands.PlayCommand.LooksLikeSteamGame("/home/player/.local/share/Steam/steamapps/common/Sample Game"));
        Assert.IsTrue(Commands.PlayCommand.LooksLikeSteamGame(@"D:\SteamLibrary\steamapps\common\Sample Game"));
        Assert.IsFalse(Commands.PlayCommand.LooksLikeSteamGame("/home/player/Games/Sample Game"));
    }

    [TestMethod]
    public void LogLines_LoseLinuxAndMacHomeFolders()
    {
        var user = Environment.UserName;
        if (user.Length <= 2)
            Assert.Inconclusive("The user name is too short to be redacted by pattern.");
        var cleaned = Logs.LogReader.Clean($"at /home/{user}/game/x and /Users/{user}/y");
        Assert.DoesNotContain(user, cleaned, StringComparison.OrdinalIgnoreCase);
    }
}
