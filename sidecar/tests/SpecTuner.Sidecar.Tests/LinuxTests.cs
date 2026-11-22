using System.Text;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>The scope gate on Linux game folders, through the Linux profile. Runs on every system.</summary>
[TestClass]
public sealed class LinuxScopeTests : SidecarTest
{
    [TestMethod]
    public void LinuxMonoGame_IsSupported_WithGameFacts()
    {
        var game = CreateGame(GamePlatform.Linux);
        game.Write(game.DataRelative + "/globalgamemanagers", Encoding.ASCII.GetBytes("\0\0\0\u0016\0\02022.3.21f1\0more"));
        var before = game.Hashes();

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertOk(envelope);
        var facts = envelope.GetProperty("data").GetProperty("game");
        Assert.AreEqual("Fixture Game", facts.GetProperty("name").GetString());
        Assert.AreEqual("Fixture Game.x86_64", facts.GetProperty("exe").GetString());
        Assert.AreEqual("x64", facts.GetProperty("arch").GetString());
        Assert.AreEqual("mono", facts.GetProperty("backend").GetString());
        Assert.AreEqual("2022.3.21f1", facts.GetProperty("unityVersion").GetString());
        Assert.AreEqual("linux", facts.GetProperty("platform").GetString());
        Assert.AreEqual("TestCompany", facts.GetProperty("company").GetString());
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void OlderX86NameAndNoExtension_AreFound_ButOnly64BitIsSupported()
    {
        var game = CreateGame(GamePlatform.Linux);
        File.Move(game.ExePath, game.Full("Fixture Game"));
        AssertOk(Run("detect", new { gamePath = game.Root }));

        File.Move(game.Full("Fixture Game"), game.Full("Fixture Game.x86"));
        game.Write("Fixture Game.x86", ElfBuilder.Build(ElfBuilder.MachineX86));
        var envelope = Run("detect", new { gamePath = game.Root });
        AssertRefused(envelope, "UNSUPPORTED_ARCHITECTURE");
        Assert.AreEqual("x86", envelope.GetProperty("data").GetProperty("game").GetProperty("arch").GetString());
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString(), "Nothing was changed");
    }

    [TestMethod]
    public void Arm64Program_IsUnsupportedArchitecture()
    {
        var game = CreateGame(GamePlatform.Linux);
        game.Write(game.ExeRelative, ElfBuilder.Build(ElfBuilder.MachineArm64));
        AssertRefused(Run("detect", new { gamePath = game.Root }), "UNSUPPORTED_ARCHITECTURE");
    }

    [TestMethod]
    public void WindowsBuild_OnLinux_IsDeclinedAsACompatibilityLayer()
    {
        UsePlatform(GamePlatform.Linux);
        var windowsBuild = GameFixture.Create(TempDir, GamePlatform.Windows);
        var before = windowsBuild.Hashes();

        var envelope = Run("detect", new { gamePath = windowsBuild.Root });

        AssertRefused(envelope, "COMPATIBILITY_LAYER_NOT_SUPPORTED");
        var message = envelope.GetProperty("error").GetProperty("message").GetString()!;
        StringAssert.Contains(message, "compatibility layer");
        StringAssert.Contains(message, "Proton");
        StringAssert.Contains(message, "Nothing was changed");
        AssertRefused(Run("install", new { gamePath = windowsBuild.Root }), "COMPATIBILITY_LAYER_NOT_SUPPORTED");
        CollectionAssert.AreEqual(before.ToList(), windowsBuild.Hashes().ToList());
    }

    [TestMethod]
    public void NoGame_ProgramWithoutData_AndNoUnityPlayer_AreDeclined()
    {
        UsePlatform(GamePlatform.Linux);
        var empty = Path.Combine(TempDir, "empty");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, "readme.txt"), "hi");
        AssertRefused(Run("detect", new { gamePath = empty }), "GAME_NOT_FOUND");

        File.WriteAllBytes(Path.Combine(empty, "Game.x86_64"), ElfBuilder.Build());
        AssertRefused(Run("detect", new { gamePath = empty }), "NOT_UNITY");

        var game = CreateGame(GamePlatform.Linux);
        File.Delete(game.Full(game.UnityPlayerRelative));
        File.Delete(game.Full(game.DataRelative + "/globalgamemanagers"));
        AssertRefused(Run("detect", new { gamePath = game.Root }), "NOT_UNITY");
    }

    [TestMethod]
    [DataRow("GameAssembly.so", false)]
    [DataRow("Fixture Game_Data/il2cpp_data", true)]
    public void Il2Cpp_IsDeclined(string marker, bool isDirectory)
    {
        var game = CreateGame(GamePlatform.Linux);
        Directory.Delete(game.ManagedDir, recursive: true);
        if (isDirectory)
            game.Mkdir(marker + "/Metadata");
        else
            game.Write(marker, ElfBuilder.Build());

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "IL2CPP_NOT_SUPPORTED");
        Assert.AreEqual("il2cpp", envelope.GetProperty("data").GetProperty("game").GetProperty("backend").GetString());
    }

    [TestMethod]
    [DataRow("start_protected_game", false, "Easy Anti-Cheat")]
    [DataRow("EasyAntiCheat", true, "Easy Anti-Cheat")]
    [DataRow("easyanticheat_x64.so", false, "Easy Anti-Cheat")]
    [DataRow("Fixture Game_Data/Plugins/BEClient_x64.so", false, "BattlEye")]
    [DataRow("BattlEye", true, "BattlEye")]
    [DataRow("Fixture Game_Data/Plugins/libanticheat.so", false, "anti-cheat")]
    public void LinuxAntiCheat_IsDeclined(string relative, bool isDirectory, string product)
    {
        var game = CreateGame(GamePlatform.Linux);
        if (isDirectory)
            game.Mkdir(relative);
        else
            game.Write(relative, ElfBuilder.Build());

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "ANTI_CHEAT");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString(), product);
    }

    [TestMethod]
    public void MacOnlySignatures_DoNotApplyOnLinux()
    {
        var game = CreateGame(GamePlatform.Linux);
        game.Write("libanticheat.dylib", "not for Linux");
        AssertOk(Run("detect", new { gamePath = game.Root }));
    }

    [TestMethod]
    public void EncryptedCodeAndNetworking_AreDeclined()
    {
        var game = CreateGame(GamePlatform.Linux);
        game.Write(game.DataRelative + "/Managed/Mirror.dll", GameFixture.ManagedAssembly());
        AssertRefused(Run("detect", new { gamePath = game.Root }), "ONLINE_MULTIPLAYER");
        game.Write(game.DataRelative + "/Managed/Assembly-CSharp.dll", PeBuilder.Build());
        AssertRefused(Run("detect", new { gamePath = game.Root }), "PROTECTED_CODE");
    }
}

/// <summary>Install, undo and restore on Linux game folders, through the Linux profile. Runs on every system.</summary>
[TestClass]
public sealed class LinuxInstallTests : SidecarTest
{
    private static readonly string[] LinuxLoaderFiles = ["libdoorstop.so", "run_bepinex.sh", ".doorstop_version"];

    [TestMethod]
    public void Install_AddsTheLinuxLoader_AndRestoreRemovesEverything()
    {
        var game = CreateGame(GamePlatform.Linux);
        game.Write(game.DataRelative + "/globalgamemanagers", Encoding.ASCII.GetBytes("\0\06000.0.23f1\0"));
        var original = game.Hashes();
        var originalDirs = GameFixture.Directories(game.Root);

        var install = Run("install", new { gamePath = game.Root });

        AssertOk(install);
        Assert.IsTrue(install.GetProperty("data").GetProperty("loader").GetProperty("ours").GetBoolean());
        foreach (var file in LinuxLoaderFiles)
            Assert.IsTrue(game.Exists(file), file);
        Assert.IsFalse(game.Exists("winhttp.dll"));
        Assert.IsFalse(game.Exists("doorstop_config.ini"));
        Assert.IsFalse(game.Exists("changelog.txt"));
        Assert.IsTrue(game.Exists("BepInEx/core/BepInEx.Preloader.dll"));
        Assert.IsTrue(game.Exists("BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll"));
        StringAssert.Contains(File.ReadAllText(game.Full("BepInEx/config/BepInEx.cfg")), "Type = Camera", "Unity 6000 entrypoint");
        using (var archive = BepInExPackage.Open(LoaderKit.For(GamePlatform.Linux)))
        using (var script = archive.GetEntry("run_bepinex.sh")!.Open())
        using (var copy = new MemoryStream())
        {
            script.CopyTo(copy);
            CollectionAssert.AreEqual(copy.ToArray(), File.ReadAllBytes(game.Full("run_bepinex.sh")), "the upstream script, unedited");
        }
        foreach (var (path, hash) in original)
            Assert.AreEqual(hash, game.Hashes()[path], path);

        AssertOk(Run("set-quality", new { gamePath = game.Root, settings = new { shadowDistance = 30 } }));
        game.Write("BepInEx/LogOutput.log", "written by BepInEx at run time");

        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        Assert.IsTrue(restore.GetProperty("data").GetProperty("matchesBaseline").GetBoolean(), restore.ToString());
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
        CollectionAssert.AreEqual(originalDirs, GameFixture.Directories(game.Root));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX, IgnoreMessage = "Execute bits exist only on Linux and macOS file systems.")]
    public void RunBepInExSh_IsExecutable()
    {
        var game = CreateGame(Platforms.Host);
        AssertOk(Run("install", new { gamePath = game.Root }));
        Assert.IsTrue(HostFiles.IsExecutable(game.Full("run_bepinex.sh")));
        Assert.IsFalse(HostFiles.IsExecutable(game.Full(".doorstop_version")));
    }

    [TestMethod]
    [DataRow("run_bepinex.sh")]
    [DataRow("libdoorstop.so")]
    [DataRow("winhttp.dll")]
    [DataRow("doorstop_config.ini")]
    public void LeftoverLoaderFiles_AreLoaderConflict(string relative)
    {
        var game = CreateGame(GamePlatform.Linux);
        game.Write(relative, "someone else's");
        var before = game.Hashes();

        AssertRefused(Run("install", new { gamePath = game.Root }), "LOADER_CONFLICT");
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void PlayersOwnLinuxBepInEx_IsUsedAndKept()
    {
        var game = CreateGame(GamePlatform.Linux);
        using (var archive = BepInExPackage.Open(LoaderKit.For(GamePlatform.Linux)))
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0 && e.FullName != "changelog.txt"))
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                game.Write(entry.FullName, buffer.ToArray());
            }
        var original = game.Hashes();

        var install = Run("install", new { gamePath = game.Root });

        AssertOk(install);
        Assert.IsFalse(install.GetProperty("data").GetProperty("loader").GetProperty("ours").GetBoolean());
        AssertOk(Run("revert", new { gamePath = game.Root, all = true }));
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void PlayerLog_IsReadFromUnitysLinuxFolder()
    {
        var game = CreateGame(GamePlatform.Linux);
        var log = Platforms.Current.Folders.PlayerLogPath("TestCompany", "Fixture Product");
        StringAssert.EndsWith(log.Replace('\\', '/'), "/.config/unity3d/TestCompany/Fixture Product/Player.log");
        Assert.AreEqual(log, Commands.ReadLogsCommand.PlayerLogPath(Games.GameInspector.Inspect(game.Root).Facts));
    }
}
