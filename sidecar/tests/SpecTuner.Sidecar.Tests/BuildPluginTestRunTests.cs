using System.Text.Json;
using SpecTuner.Sidecar.Commands;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>build-plugin's own test launch, against SpecTuner.FakeGame (never a real game).</summary>
[TestClass]
public sealed class BuildPluginTestRunTests : SidecarTest
{
    private const string Dll = "BepInEx/plugins/SpecTuner/TestMod.dll";

    [TestInitialize]
    public void DefaultFakeGame() => FakeGame("result", mods: "load");

    [TestCleanup]
    public void ClearFakeGame()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", null);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", null);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_MODS", null);
    }

    private static void FakeGame(string mode, string mods = "load", double fps = 100)
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", mode);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_MODS", mods);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>build-plugin without "test": the default, which starts the game.</summary>
    private static JsonElement Build(GameFixture game, string source, string title = "Test mod") =>
        Run("build-plugin", new { gamePath = game.Root, name = "TestMod", title, source });

    private static object Benchmark(GameFixture game, string label) =>
        new { gamePath = game.Root, label, warmupSeconds = 0, measureSeconds = 1, startupTimeoutSeconds = 30 };

    private static List<ChangeEntry> Changes(GameFixture game)
    {
        var state = GameState.For(game.Root);
        return Ledger.Load(state, state.LoadBaseline()!).Entries.ToList();
    }

    [TestMethod]
    public void ByDefault_StartsTheGameOnce_AndReportsTheModsOwnLogLines()
    {
        var game = CreateGame().UseFakeGameExe();

        var envelope = Build(game, ModSources.Plugin());

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        var test = data.GetProperty("test");
        Assert.IsTrue(test.GetProperty("ran").GetBoolean());
        Assert.IsTrue(test.GetProperty("started").GetBoolean());
        Assert.IsTrue(test.GetProperty("loaded").GetBoolean());
        Assert.AreEqual(0, test.GetProperty("errorCount").GetInt32());
        Assert.AreEqual(0, test.GetProperty("errors").GetArrayLength());
        CollectionAssert.AreEqual(new[] { "[Info   :  Test Mod] loaded" }, test.GetProperty("logLines").EnumerateArray().Select(l => l.GetString()).ToArray());
        Assert.IsGreaterThan(0.0, test.GetProperty("seconds").GetDouble());
        StringAssert.Contains(envelope.GetProperty("summary").GetString()!, "started the game once");
        Assert.AreEqual("Test Mod 1.0.0", data.GetProperty("lookFor").GetString(), "existing fields stay");

        Assert.IsTrue(game.Exists(Dll));
        Assert.IsTrue(Changes(game).All(c => c.Verified && c.IsLive), "the game started with every change in place");
        Assert.IsFalse(game.Exists("BepInEx/config/spectuner.benchmark.json"));
        Assert.IsEmpty(Directory.GetFiles(game.Full("BepInEx/spectuner"), "benchmark-*.json"), "the test run leaves no result file");
    }

    [TestMethod]
    public void ModThatThrowsOnLoad_IsRemoved_AndTheEarlierBuildComesBack()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Build(game, ModSources.Plugin(version: "1.0.0")));
        var firstDll = File.ReadAllBytes(game.Full(Dll));

        FakeGame("result", mods: "throw");
        var envelope = Build(game, ModSources.Plugin(version: "1.0.1"), title: "Test mod v2");

        AssertRefused(envelope, "MOD_FAILED_TEST");
        var message = envelope.GetProperty("error").GetProperty("message").GetString()!;
        StringAssert.Contains(message, "logged 1 error(s)");
        StringAssert.Contains(message, "First error:");
        StringAssert.Contains(message, "fake failure in Awake");
        StringAssert.Contains(message, "previous version");
        var data = envelope.GetProperty("data");
        Assert.IsTrue(data.GetProperty("loaded").GetBoolean());
        Assert.AreEqual(1, data.GetProperty("errorCount").GetInt32());
        StringAssert.Contains(data.GetProperty("errors")[0].GetString()!, "TestMod.Plugin.Awake");
        Assert.IsTrue(data.GetProperty("removed").GetBoolean());
        Assert.IsTrue(data.GetProperty("restoredPrevious").GetBoolean());

        CollectionAssert.AreEqual(firstDll, File.ReadAllBytes(game.Full(Dll)), "the earlier build is back");
        var changes = Changes(game);
        var second = changes.Single(c => c.Id == data.GetProperty("changeId").GetString());
        Assert.AreEqual(ChangeStatus.Reverted, second.Status);
        Assert.IsTrue(changes.Where(c => c.IsLive).All(c => c.Verified));
        Assert.AreEqual("1.0.0", ModRecord.Load(GameState.For(game.Root), "TestMod")!.Version, "the mod record describes the build that is back");

        // The earlier build is recognised again in a later launch.
        FakeGame("result", mods: "load");
        var mods = Run("launch-and-benchmark", Benchmark(game, "after")).GetProperty("data").GetProperty("mods");
        Assert.IsTrue(mods[0].GetProperty("loaded").GetBoolean(), mods.ToString());
    }

    [TestMethod]
    public void ModThatDoesNotLoad_IsRemoved_AndTheRestIsVerified()
    {
        var game = CreateGame().UseFakeGameExe();
        FakeGame("result", mods: "none");

        var envelope = Build(game, ModSources.Plugin());

        AssertRefused(envelope, "MOD_FAILED_TEST");
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "did not load it");
        var data = envelope.GetProperty("data");
        Assert.IsFalse(data.GetProperty("loaded").GetBoolean());
        Assert.AreEqual(0, data.GetProperty("errorCount").GetInt32());
        Assert.IsFalse(data.GetProperty("restoredPrevious").GetBoolean());
        Assert.IsFalse(game.Exists(Dll));
        var live = Changes(game).Where(c => c.IsLive).ToList();
        CollectionAssert.AreEquivalent(new[] { ChangeKinds.Loader, ChangeKinds.Probe }, live.Select(c => c.Kind).ToArray());
        Assert.IsTrue(live.All(c => c.Verified), "the game did start with the loader and probe");
    }

    [TestMethod]
    public void FailedStart_RevertsEveryUnverifiedChange_LikeLaunchAndBenchmark()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("launch-and-benchmark", Benchmark(game, "before")));
        AssertOk(Run("set-quality", new { gamePath = game.Root, title = "Lower shadows", settings = new { shadowDistance = 40 } }));

        FakeGame("crash");
        var envelope = Build(game, ModSources.Plugin());

        AssertRefused(envelope, "GAME_FAILED_TO_START");
        var data = envelope.GetProperty("data");
        Assert.AreEqual("crashed", data.GetProperty("reason").GetString());
        Assert.IsFalse(data.GetProperty("fullRestore").GetBoolean());
        CollectionAssert.AreEquivalent(new[] { "Lower shadows", "Test mod" },
            data.GetProperty("reverted").EnumerateArray().Select(r => r.GetProperty("title").GetString()).ToArray());
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "undid the changes made since the game last worked");
        Assert.IsFalse(game.Exists(Dll));
        Assert.IsFalse(game.Exists("BepInEx/config/spectuner.quality.json"));
        Assert.IsNull(ModRecord.Load(GameState.For(game.Root), "TestMod"));
    }

    [TestMethod]
    public void FailedFirstStart_RemovesEverything()
    {
        var game = CreateGame().UseFakeGameExe();
        var original = game.Hashes();
        FakeGame("crash");

        var envelope = Build(game, ModSources.Plugin());

        AssertRefused(envelope, "GAME_FAILED_TO_START");
        Assert.IsTrue(envelope.GetProperty("data").GetProperty("fullRestore").GetBoolean());
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void TestFalse_DoesNotStartTheGame()
    {
        var game = CreateGame().UseFakeGameExe();
        FakeGame("crash"); // a launch would fail and revert

        var envelope = Run("build-plugin", new { gamePath = game.Root, name = "TestMod", title = "Test mod", source = ModSources.Plugin(), test = false });

        AssertOk(envelope);
        var test = envelope.GetProperty("data").GetProperty("test");
        Assert.IsFalse(test.GetProperty("ran").GetBoolean());
        Assert.AreEqual(1, test.EnumerateObject().Count());
        StringAssert.Contains(envelope.GetProperty("summary").GetString()!, "loads the next time the game starts");
        Assert.IsTrue(game.Exists(Dll));
        Assert.IsFalse(game.Exists("BepInEx/LogOutput.log"), "the game never ran");
        Assert.IsFalse(Changes(game).Single(c => c.Kind == ChangeKinds.Mod).Verified);
    }

    [TestMethod]
    public void PlayerLogExceptions_AreBlamedOnTheModTheirStackTraceNames()
    {
        // Unity's Player.log as written by the test game: BepInEx's lines, and Unity's exceptions
        // right after whatever line came before (here another mod's info lines).
        string[] playerLog =
        [
            "[Info   :   BepInEx] Loading [Fix Check 1.0.0]",
            "[Info   : Fix Check] loaded",
            "[Info   :   BepInEx] Loading [Thrower 1.0.0]",
            "[Info   :   Thrower] about to fail",
            "InvalidOperationException: deliberate failure in Awake",
            "  at Thrower.Plugin.Awake () [0x00010] in <1ea7b0f36bd645d8beef50f40ff1769d>:0 ",
            "UnityEngine.GameObject:AddComponent(Type)",
            "[Info   : Fix Check] Update ran for 2 seconds",
            "NullReferenceException: Object reference not set to an instance of an object",
            "  at Thrower.Plugin.Update () [0x00001] in <1ea7b0f36bd645d8beef50f40ff1769d>:0 ",
        ];
        var loaded = new List<string> { "Fix Check 1.0.0", "Thrower 1.0.0" };
        ModRecord Record(string name, string plugin, string ns) => new()
        {
            Name = name, PluginName = plugin, Version = "1.0.0", AssemblyName = "SpecTuner.Mod." + name, Types = [ns + ".Plugin"],
        };

        var thrower = GameRun.Status("Thrower", Record("Thrower", "Thrower", "Thrower"), loaded, [], [.. playerLog]);
        var fixCheck = GameRun.Status("FixCheck", Record("FixCheck", "Fix Check", "FixCheck"), loaded, [], [.. playerLog]);

        Assert.HasCount(2, thrower.Errors);
        // The line the exception follows in Player.log is left out: only the exception and the frame naming the mod.
        Assert.AreEqual("InvalidOperationException: deliberate failure in Awake | at Thrower.Plugin.Awake () [0x00010] in <1ea7b0f36bd645d8beef50f40ff1769d>:0",
            thrower.Errors[0]);
        StringAssert.Contains(thrower.Errors[1], "at Thrower.Plugin.Update");
        Assert.IsEmpty(fixCheck.Errors, "an exception after Fix Check's info line belongs to the mod its stack trace names");
    }

    [TestMethod]
    public void TestRun_IsNotAPreviousBenchmark()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("launch-and-benchmark", Benchmark(game, "before")));
        FakeGame("result", mods: "load", fps: 50);
        AssertOk(Build(game, ModSources.Plugin()));

        FakeGame("result", mods: "load", fps: 120);
        var after = Run("launch-and-benchmark", Benchmark(game, "after"));

        AssertOk(after);
        var data = after.GetProperty("data");
        Assert.AreEqual("b0002", data.GetProperty("runId").GetString());
        Assert.AreEqual("b0001", data.GetProperty("comparedToPrevious").GetProperty("runId").GetString());
        Assert.AreEqual(20, data.GetProperty("comparedToPrevious").GetProperty("avgFpsDeltaPct").GetDouble());
    }
}
