using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using SpecTuner.Sidecar.Loader;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class InstallRestoreTests : SidecarTest
{
    [TestMethod]
    public void InstallThenRestore_LeavesExactlyTheOriginalFiles()
    {
        var game = CreateGame();
        var original = game.Hashes();
        var originalDirs = GameFixture.Directories(game.Root);

        var install = Run("install", new { gamePath = game.Root });

        AssertOk(install);
        var data = install.GetProperty("data");
        Assert.IsTrue(data.GetProperty("baseline").GetProperty("created").GetBoolean());
        Assert.AreEqual(original.Count, data.GetProperty("baseline").GetProperty("files").GetInt32());
        Assert.IsTrue(data.GetProperty("loader").GetProperty("ours").GetBoolean());
        Assert.IsTrue(game.Exists("BepInEx/core/BepInEx.dll"));
        Assert.IsTrue(game.Exists("BepInEx/core/0Harmony.dll"));
        Assert.IsTrue(game.Exists(OurFiles.LoaderRootFiles[0]));
        Assert.IsTrue(game.Exists(OurFiles.LoaderRootFiles[1]));
        Assert.IsTrue(game.Exists(OurFiles.LoaderRootFiles[2]));
        Assert.IsFalse(game.Exists("changelog.txt"), "changelog.txt must not be extracted");
        foreach (var (path, hash) in original)
            Assert.AreEqual(hash, game.Hashes()[path], $"original file changed by install: {path}");

        // What BepInEx itself creates on first launch is removed with it.
        game.Write("BepInEx/LogOutput.log", "[Message:   BepInEx] Chainloader startup complete");
        game.Write("BepInEx/config/BepInEx.cfg", "[Logging]");
        game.Mkdir("BepInEx/plugins");

        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        var report = restore.GetProperty("data");
        Assert.IsTrue(report.GetProperty("matchesBaseline").GetBoolean(), restore.ToString());
        Assert.AreEqual(0, report.GetProperty("leftInPlaceCount").GetInt32());
        Assert.AreEqual(0, report.GetProperty("changedOriginalsCount").GetInt32());
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
        CollectionAssert.AreEqual(originalDirs, GameFixture.Directories(game.Root));

        // State is cleared except the record that it was restored.
        var state = GameState.For(game.Root);
        Assert.IsFalse(File.Exists(state.BaselinePath));
        Assert.IsFalse(File.Exists(state.ChangesPath));
        Assert.HasCount(1, state.LoadRecord()!.Restores);
        AssertOk(Run("detect", new { gamePath = game.Root }));
    }

    [TestMethod]
    public void Install_IsIdempotent()
    {
        var game = CreateGame();
        AssertOk(Run("install", new { gamePath = game.Root }));
        var afterFirst = game.Hashes();

        var second = Run("install", new { gamePath = game.Root });

        AssertOk(second);
        Assert.IsTrue(second.GetProperty("data").GetProperty("loader").GetProperty("alreadyInstalled").GetBoolean());
        Assert.IsTrue(second.GetProperty("data").GetProperty("probe").GetProperty("alreadyInstalled").GetBoolean());
        Assert.IsFalse(second.GetProperty("data").GetProperty("baseline").GetProperty("created").GetBoolean());
        CollectionAssert.AreEqual(afterFirst.ToList(), game.Hashes().ToList());
        // Milestone 3: install records the loader and then the probe.
        var changes = Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("changes");
        Assert.AreEqual(2, changes.GetArrayLength());
        Assert.AreEqual("probe", changes[0].GetProperty("kind").GetString());
        Assert.AreEqual("loader", changes[1].GetProperty("kind").GetString());
        Assert.AreEqual("active", changes[0].GetProperty("status").GetString());
        Assert.AreEqual("active", changes[1].GetProperty("status").GetString());
    }

    [TestMethod]
    public void Install_PutsBackMissingLoaderFiles()
    {
        var game = CreateGame();
        AssertOk(Run("install", new { gamePath = game.Root }));
        File.Delete(game.Full(OurFiles.LoaderRootFiles[0]));

        var again = Run("install", new { gamePath = game.Root });

        AssertOk(again);
        Assert.AreEqual(1, again.GetProperty("data").GetProperty("loader").GetProperty("filesAdded").GetInt32());
        Assert.IsTrue(game.Exists(OurFiles.LoaderRootFiles[0]));
    }

    [TestMethod]
    public void PlayersOwnBepInEx_IsUsedAndKeptOnRestore()
    {
        var game = CreateGame();
        using (var archive = BepInExPackage.Open())
        {
            foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0 && e.FullName != "changelog.txt"))
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                game.Write(entry.FullName, buffer.ToArray());
            }
        }
        game.Write("BepInEx/plugins/PlayersMod.dll", GameFixture.ManagedAssembly());
        game.Write("BepInEx/config/BepInEx.cfg", "[Logging]");
        var original = game.Hashes();

        var install = Run("install", new { gamePath = game.Root });

        AssertOk(install);
        var loader = install.GetProperty("data").GetProperty("loader");
        Assert.IsFalse(loader.GetProperty("ours").GetBoolean());
        Assert.AreEqual(BepInExPackage.Version, loader.GetProperty("version").GetString());
        // Only the probe is added; the player's loader files are untouched and no loader change is recorded.
        var afterInstall = game.Hashes();
        Assert.IsTrue(afterInstall.Remove("BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll"));
        CollectionAssert.AreEqual(original.ToList(), afterInstall.ToList());
        var installed = Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("changes");
        Assert.AreEqual(1, installed.GetArrayLength());
        Assert.AreEqual("probe", installed[0].GetProperty("kind").GetString());

        // Something of ours inside the player's BepInEx (as milestone 3/4 will add).
        AddFakeChange(game, ChangeKinds.Mod, "BepInEx/plugins/SpecTuner/Fake.dll", "x");
        game.Write("BepInEx/config/spectuner.quality.json", "{}");
        game.Write("BepInEx/LogOutput.log", "log written by the player's BepInEx");

        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        Assert.IsTrue(game.Exists("BepInEx/core/BepInEx.dll"));
        Assert.IsTrue(game.Exists(OurFiles.LoaderRootFiles[0]));
        Assert.IsTrue(game.Exists("BepInEx/plugins/PlayersMod.dll"));
        Assert.IsFalse(game.Exists("BepInEx/plugins/SpecTuner"));
        Assert.IsFalse(game.Exists("BepInEx/config/spectuner.quality.json"));
        var report = restore.GetProperty("data");
        Assert.AreEqual(1, report.GetProperty("leftInPlaceCount").GetInt32(), restore.ToString());
        Assert.AreEqual("BepInEx/LogOutput.log", report.GetProperty("leftInPlace")[0].GetString());
        var after = game.Hashes();
        after.Remove("BepInEx/LogOutput.log");
        CollectionAssert.AreEqual(original.ToList(), after.ToList());
    }

    [TestMethod]
    public void PlayersBepInExThatIsNotVersion5_IsUnsupportedLoader()
    {
        var game = CreateGame();
        game.Write("BepInEx/core/BepInEx.dll", GameFixture.ManagedAssembly()); // version 1.0.0.0
        var before = game.Hashes();

        AssertRefused(Run("install", new { gamePath = game.Root }), "UNSUPPORTED_LOADER");
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    [DataRow("winhttp.dll")]
    [DataRow("doorstop_config.ini")]
    [DataRow(".doorstop_version")]
    [DataRow("BepInEx/config/BepInEx.cfg")]
    public void LeftoverLoaderFiles_AreLoaderConflict(string relative)
    {
        var game = CreateGame();
        game.Write(relative, "someone else's");
        var before = game.Hashes();

        var envelope = Run("install", new { gamePath = game.Root });

        AssertRefused(envelope, "LOADER_CONFLICT");
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void FileWrittenByGameAfterBaseline_IsLeftInPlaceAndReported()
    {
        var game = CreateGame();
        AssertOk(Run("install", new { gamePath = game.Root }));
        game.Write(game.Native("Fixture Game_Data/savegame.dat"), "progress");

        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        var report = restore.GetProperty("data");
        Assert.IsFalse(report.GetProperty("matchesBaseline").GetBoolean());
        Assert.AreEqual(1, report.GetProperty("leftInPlaceCount").GetInt32());
        Assert.AreEqual(game.Native("Fixture Game_Data/savegame.dat"), report.GetProperty("leftInPlace")[0].GetString());
        Assert.IsTrue(game.Exists(game.Native("Fixture Game_Data/savegame.dat")), "files we did not add are never deleted");
        Assert.IsFalse(game.Exists("BepInEx"));
    }

    [TestMethod]
    public void ModifiedOriginal_IsReportedAndNotTouched()
    {
        var game = CreateGame();
        AssertOk(Run("install", new { gamePath = game.Root }));
        game.Write(game.Native("Fixture Game_Data/level0"), "patched by a game update");
        File.Delete(game.Full("MonoBleedingEdge/etc/mono/config"));

        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        var report = restore.GetProperty("data");
        Assert.IsFalse(report.GetProperty("matchesBaseline").GetBoolean());
        Assert.AreEqual(game.Native("Fixture Game_Data/level0"), report.GetProperty("changedOriginals")[0].GetString());
        Assert.AreEqual("MonoBleedingEdge/etc/mono/config", report.GetProperty("missingOriginals")[0].GetString());
        Assert.AreEqual("patched by a game update", File.ReadAllText(game.Full(game.Native("Fixture Game_Data/level0"))));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "Uses Windows' PING.EXE as a stand-in; RunningFakeGame_IsRefused covers every system.")]
    public void RunningGame_IsRefused()
    {
        var game = CreateGame();
        // A harmless stand-in process started from the fixture's exe path (not a game).
        var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE");
        File.Copy(ping, game.ExePath, overwrite: true);
        var before = game.Hashes();
        using var process = Process.Start(new ProcessStartInfo(game.ExePath, "-n 60 127.0.0.1")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
        })!;
        try
        {
            AssertRefused(Run("install", new { gamePath = game.Root }), "GAME_RUNNING");
            AssertRefused(Run("revert", new { gamePath = game.Root, all = true }), "GAME_RUNNING");
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }

        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
        AssertOk(Run("install", new { gamePath = game.Root }));
    }

    [TestMethod]
    public void RunningFakeGame_IsRefused()
    {
        var game = CreateGame().UseFakeGameExe();
        var before = game.Hashes();
        var start = new ProcessStartInfo(game.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["SPECTUNER_FAKE_GAME"] = "hang";
        using var process = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!SpecTuner.Sidecar.Games.GameProcesses.IsRunning(game.ExePath) && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            AssertRefused(Run("install", new { gamePath = game.Root }), "GAME_RUNNING");
            AssertRefused(Run("revert", new { gamePath = game.Root, all = true }), "GAME_RUNNING");
            AssertRefused(Run("play", new { gamePath = game.Root }), "GAME_RUNNING");
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }

        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
        AssertOk(Run("install", new { gamePath = game.Root }));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX, IgnoreMessage = "Linked folders as Linux and macOS have them: the kernel reports a running program's path behind the links.")]
    public void RunningGame_ChosenThroughALinkedFolder_IsRefused()
    {
        // A game library on another drive is often reached through a link; the player picks the linked path.
        var game = CreateGame().UseFakeGameExe();
        var before = game.Hashes();
        var linkedRoot = Path.Combine(TempDir, "linked library");
        Directory.CreateSymbolicLink(linkedRoot, game.Root);
        var linkedExe = Path.Combine(linkedRoot, Path.GetRelativePath(game.Root, game.ExePath));
        var start = new ProcessStartInfo(game.ExePath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["SPECTUNER_FAKE_GAME"] = "hang";
        using var process = Process.Start(start)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!SpecTuner.Sidecar.Games.GameProcesses.IsRunning(linkedExe) && DateTime.UtcNow < deadline)
                Thread.Sleep(100);
            Assert.IsTrue(SpecTuner.Sidecar.Games.GameProcesses.IsRunning(linkedExe), "the game is found through the linked path");
            AssertRefused(Run("install", new { gamePath = linkedRoot }), "GAME_RUNNING");
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }

        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void RevertLoaderAlone_IsBlockedWhileOtherChangesAreActive_ThenRestoresWhenAlone()
    {
        var game = CreateGame();
        var original = game.Hashes();
        var install = Run("install", new { gamePath = game.Root });
        var loaderId = install.GetProperty("data").GetProperty("loader").GetProperty("changeId").GetString();
        var modId = AddFakeChange(game, ChangeKinds.Mod, "BepInEx/plugins/SpecTuner/Fake.dll", "x");

        AssertRefused(Run("revert", new { gamePath = game.Root, changeId = loaderId }), "REVERT_BLOCKED");

        AssertOk(Run("revert", new { gamePath = game.Root, changeId = modId }));
        Assert.IsFalse(game.Exists("BepInEx/plugins/SpecTuner/Fake.dll"));
        var changes = Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("changes");
        Assert.AreEqual(modId, changes[0].GetProperty("id").GetString(), "newest first");
        Assert.AreEqual("reverted", changes[0].GetProperty("status").GetString());

        // The probe is still live, so the loader is still blocked; the probe itself can go alone.
        AssertRefused(Run("revert", new { gamePath = game.Root, changeId = loaderId }), "REVERT_BLOCKED");
        var probeId = install.GetProperty("data").GetProperty("probe").GetProperty("changeId").GetString();
        AssertOk(Run("revert", new { gamePath = game.Root, changeId = probeId }));
        Assert.IsFalse(game.Exists("BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll"));

        var loaderRevert = Run("revert", new { gamePath = game.Root, changeId = loaderId });
        AssertOk(loaderRevert);
        Assert.IsTrue(loaderRevert.GetProperty("data").GetProperty("fullRestore").GetBoolean());
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void Revert_NeedsChangeIdOrAll()
    {
        var game = CreateGame();
        AssertRefused(Run("revert", new { gamePath = game.Root }), "BAD_REQUEST");
        AssertRefused(Run("revert", new { gamePath = game.Root, changeId = "c0001", all = true }), "BAD_REQUEST");
        AssertRefused(Run("revert", new { gamePath = game.Root, changeId = "c0042" }), "CHANGE_NOT_FOUND");
    }

    [TestMethod]
    public void RestoreWithNothingRecorded_SaysNothingToRestore()
    {
        var game = CreateGame();
        var before = game.Hashes();

        var envelope = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(envelope);
        Assert.IsTrue(envelope.GetProperty("data").GetProperty("nothingToRestore").GetBoolean());
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void UndoStillWorks_WhenGameLaterFallsOutOfScope()
    {
        var game = CreateGame();
        var original = game.Hashes();
        AssertOk(Run("install", new { gamePath = game.Root }));
        game.Write(game.Native("Fixture Game_Data/Managed/Mirror.dll"), GameFixture.ManagedAssembly()); // e.g. a game update

        AssertRefused(Run("install", new { gamePath = game.Root }), "ONLINE_MULTIPLAYER");
        AssertOk(Run("changes", new { gamePath = game.Root }));
        var restore = Run("revert", new { gamePath = game.Root, all = true });

        AssertOk(restore);
        Assert.IsFalse(game.Exists("BepInEx"));
        Assert.AreEqual(game.Native("Fixture Game_Data/Managed/Mirror.dll"), restore.GetProperty("data").GetProperty("leftInPlace")[0].GetString());
        File.Delete(game.Full(game.Native("Fixture Game_Data/Managed/Mirror.dll")));
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    /// <summary>Records a change through the ledger API, as later commands will.</summary>
    private static string AddFakeChange(GameFixture game, string kind, string relative, string content)
    {
        var state = GameState.For(game.Root);
        var ledger = Ledger.Load(state, state.LoadBaseline()!);
        var change = ledger.Begin(kind, "Fake change", "For a test.");
        game.Write(relative, content);
        ledger.Commit(change, [relative]);
        return change.Id;
    }
}
