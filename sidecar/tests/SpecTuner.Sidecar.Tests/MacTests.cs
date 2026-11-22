using System.Runtime.InteropServices;
using System.Text;
using SpecTuner.Sidecar.Games;
using SpecTuner.Sidecar.Platform;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>The scope gate on macOS .app games, through the macOS profile. Runs on every system.</summary>
[TestClass]
public sealed class MacScopeTests : SidecarTest
{
    [TestMethod]
    public void MacMonoApp_IsSupported_FromItsFolderOrFromTheAppItself()
    {
        var game = CreateGame(GamePlatform.MacOS);
        game.Write(game.DataRelative + "/globalgamemanagers", Encoding.ASCII.GetBytes("\0\06000.0.23f1\0"));

        foreach (var picked in new[] { game.Root, game.AppDir! })
        {
            var envelope = Run("detect", new { gamePath = picked });

            AssertOk(envelope);
            var facts = envelope.GetProperty("data").GetProperty("game");
            Assert.AreEqual("Fixture Game", facts.GetProperty("name").GetString());
            Assert.AreEqual("Fixture Game.app", facts.GetProperty("exe").GetString());
            Assert.AreEqual("arm64+x64", facts.GetProperty("arch").GetString());
            Assert.AreEqual("6000.0.23f1", facts.GetProperty("unityVersion").GetString());
            Assert.AreEqual("macos", facts.GetProperty("platform").GetString());
        }
        Assert.AreEqual(game.Root, GameInspector.Inspect(game.AppDir!).Facts.FullPath, "SpecTuner works in the folder holding the app");
        Assert.AreEqual(game.ExePath, GameInspector.Inspect(game.Root).Facts.ExePath, "the program named by CFBundleExecutable");
    }

    [TestMethod]
    public void ProgramName_ComesFromInfoPlist()
    {
        var game = CreateGame(GamePlatform.MacOS);
        File.Move(game.ExePath, game.Full("Fixture Game.app/Contents/MacOS/RealName"));
        game.Write("Fixture Game.app/Contents/MacOS/helper.txt", "not the program");
        game.Write("Fixture Game.app/Contents/Info.plist", GameFixture.InfoPlist("RealName"));

        var facts = GameInspector.Inspect(game.Root).Facts;

        Assert.AreEqual(game.Full("Fixture Game.app/Contents/MacOS/RealName"), facts.ExePath);
    }

    [TestMethod]
    public void Arm64OnlyApp_RunsOnAppleSilicon_ButNotOnIntel()
    {
        var game = CreateGame(GamePlatform.MacOS);
        game.Write(game.ExeRelative, MachOBuilder.Thin(MachOBuilder.Arm64, new MachOSignature(false)));
        var facts = GameInspector.Inspect(game.Root).Facts;

        Assert.IsNull(new MacLayout(Architecture.Arm64).CheckArch(facts));
        Assert.AreEqual("UNSUPPORTED_ARCHITECTURE", new MacLayout(Architecture.X64).CheckArch(facts)!.Code);
    }

    [TestMethod]
    public void X64OnlyApp_RunsEverywhere_UnderRosettaOnAppleSilicon()
    {
        var game = CreateGame(GamePlatform.MacOS);
        game.Write(game.ExeRelative, MachOBuilder.Thin(MachOBuilder.X64));
        var facts = GameInspector.Inspect(game.Root).Facts;

        Assert.IsNull(new MacLayout(Architecture.Arm64).CheckArch(facts));
        Assert.IsNull(new MacLayout(Architecture.X64).CheckArch(facts));
    }

    [TestMethod]
    public void HardenedRuntimeWithoutTheEntitlements_IsLoaderBlocked()
    {
        var game = CreateGame(GamePlatform.MacOS);
        // Unity's documented minimum hardened-runtime entitlements: no allow-dyld-environment-variables.
        var unityMinimum = new MachOSignature(true, MachOBuilder.DisableLibraryValidation, "com.apple.security.cs.disable-executable-page-protection");
        game.Write(game.ExeRelative, MachOBuilder.Universal(MachOBuilder.Thin(MachOBuilder.Arm64, unityMinimum), MachOBuilder.Thin(MachOBuilder.X64, unityMinimum)));
        var before = game.Hashes();

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "LOADER_BLOCKED");
        var message = envelope.GetProperty("error").GetProperty("message").GetString()!;
        StringAssert.Contains(message, "macOS protects this game");
        StringAssert.Contains(message, "never does");
        StringAssert.Contains(message, "Nothing was changed");
        AssertRefused(Run("install", new { gamePath = game.Root }), "LOADER_BLOCKED");
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void HardenedRuntimeThatAllowsTheLoader_IsSupported()
    {
        var game = CreateGame(GamePlatform.MacOS);
        var open = new MachOSignature(true, MachOBuilder.AllowDyld, MachOBuilder.DisableLibraryValidation);
        game.Write(game.ExeRelative, MachOBuilder.Universal(MachOBuilder.Thin(MachOBuilder.Arm64, open), MachOBuilder.Thin(MachOBuilder.X64, open)));

        AssertOk(Run("detect", new { gamePath = game.Root }));
    }

    [TestMethod]
    public void WindowsBuild_OnMac_IsDeclinedAsACompatibilityLayer()
    {
        UsePlatform(GamePlatform.MacOS);
        var windowsBuild = GameFixture.Create(TempDir, GamePlatform.Windows);

        var envelope = Run("detect", new { gamePath = windowsBuild.Root });

        AssertRefused(envelope, "COMPATIBILITY_LAYER_NOT_SUPPORTED");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString(), "macOS");
    }

    [TestMethod]
    public void FolderWithSeveralApps_AsksForAFolderOfItsOwn()
    {
        var game = CreateGame(GamePlatform.MacOS);
        game.Mkdir("Other Tool.app/Contents/MacOS");

        var envelope = Run("detect", new { gamePath = game.Root });

        AssertRefused(envelope, "GAME_NOT_FOUND");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString(), "folder of its own");
    }

    [TestMethod]
    public void AppWithoutData_IsNotUnity_AndNoApp_IsGameNotFound()
    {
        var game = CreateGame(GamePlatform.MacOS);
        Directory.Delete(game.DataDir, recursive: true);
        AssertRefused(Run("detect", new { gamePath = game.Root }), "NOT_UNITY");

        var empty = Path.Combine(TempDir, "empty");
        Directory.CreateDirectory(empty);
        AssertRefused(Run("detect", new { gamePath = empty }), "GAME_NOT_FOUND");
    }

    [TestMethod]
    [DataRow("Fixture Game.app/Contents/Frameworks/GameAssembly.dylib", false)]
    [DataRow("Fixture Game.app/Contents/Resources/Data/il2cpp_data", true)]
    public void Il2Cpp_IsDeclined(string marker, bool isDirectory)
    {
        var game = CreateGame(GamePlatform.MacOS);
        Directory.Delete(game.ManagedDir, recursive: true);
        if (isDirectory)
            game.Mkdir(marker + "/Metadata");
        else
            game.Write(marker, MachOBuilder.Thin(MachOBuilder.Arm64));

        AssertRefused(Run("detect", new { gamePath = game.Root }), "IL2CPP_NOT_SUPPORTED");
    }

    [TestMethod]
    [DataRow("start_protected_game.app", true)]
    [DataRow("start_protected_game_x86-64.app", true)]
    [DataRow("Fixture Game.app.eac", false)]
    [DataRow("EasyAntiCheat", true)]
    [DataRow("Fixture Game.app/Contents/Frameworks/BEClient_x64.dylib", false)]
    public void MacAntiCheat_IsDeclined(string relative, bool isDirectory)
    {
        var game = CreateGame(GamePlatform.MacOS);
        if (isDirectory)
            game.Mkdir(relative + (relative.EndsWith(".app", StringComparison.Ordinal) ? "/Contents/MacOS" : ""));
        else
            game.Write(relative, "x");

        AssertRefused(Run("detect", new { gamePath = game.Root }), "ANTI_CHEAT");
    }
}

/// <summary>Install, undo and restore next to a macOS .app, through the macOS profile. Runs on every system.</summary>
[TestClass]
public sealed class MacInstallTests : SidecarTest
{
    private static SortedDictionary<string, string> AppHashes(GameFixture game) =>
        new(game.Hashes().Where(p => p.Key.StartsWith("Fixture Game.app/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value),
            StringComparer.OrdinalIgnoreCase);

    [TestMethod]
    public void Install_PutsTheLoaderNextToTheApp_NeverInsideIt()
    {
        var game = CreateGame(GamePlatform.MacOS);
        var original = game.Hashes();
        var app = AppHashes(game);
        var appDirs = GameFixture.Directories(game.AppDir!);

        AssertOk(Run("install", new { gamePath = game.AppDir! }));

        foreach (var file in new[] { "libdoorstop.dylib", "run_bepinex.sh", ".doorstop_version", "BepInEx/core/BepInEx.Preloader.dll",
                     "BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll" })
            Assert.IsTrue(game.Exists(file), file);
        Assert.IsFalse(game.Exists("winhttp.dll"));
        CollectionAssert.AreEqual(app.ToList(), AppHashes(game).ToList(), "nothing inside the app is added or changed");
        CollectionAssert.AreEqual(appDirs, GameFixture.Directories(game.AppDir!));

        AssertOk(Run("set-quality", new { gamePath = game.Root, settings = new { shadowDistance = 30 } }));
        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        Assert.IsTrue(restore.GetProperty("data").GetProperty("matchesBaseline").GetBoolean(), restore.ToString());
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void PlayerLog_IsReadFromUnitysMacFolder()
    {
        var game = CreateGame(GamePlatform.MacOS);
        var path = Commands.ReadLogsCommand.PlayerLogPath(GameInspector.Inspect(game.Root).Facts)!;
        StringAssert.EndsWith(path.Replace('\\', '/'), "/Library/Logs/TestCompany/Fixture Product/Player.log");
    }

    [TestMethod]
    public void FailedStartWithoutTheLoader_SaysMacOSMayBeKeepingItOut()
    {
        UsePlatform(GamePlatform.MacOS);
        var note = Platforms.Current.Loader.LoaderDidNotStartNote!;
        StringAssert.Contains(note, "mod loader didn't start");
        StringAssert.Contains(note, "never changes a game's app");
        Assert.IsNull(LoaderKit.For(GamePlatform.Windows).LoaderDidNotStartNote, "Windows messages stay as they were");
    }
}
