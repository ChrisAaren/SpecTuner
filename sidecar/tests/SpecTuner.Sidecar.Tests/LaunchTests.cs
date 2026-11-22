using System.Diagnostics;
using System.Text.Json;
using SpecTuner.Sidecar.State;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

/// <summary>launch-and-benchmark against SpecTuner.FakeGame, a stand-in exe (never a real game).</summary>
[TestClass]
public sealed class LaunchTests : SidecarTest
{
    private const string QualityFile = "BepInEx/config/spectuner.quality.json";

    [TestInitialize]
    public void DefaultFakeGame() => FakeGame("result", fps: 100);

    [TestCleanup]
    public void ClearFakeGame()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", null);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", null);
    }

    private static void FakeGame(string mode, double fps = 100)
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", mode);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", fps.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static object Benchmark(GameFixture game, string label = "test", double startup = 30) =>
        new { gamePath = game.Root, label, warmupSeconds = 0, measureSeconds = 1, startupTimeoutSeconds = startup };

    private static JsonElement Changes(GameFixture game) =>
        Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("changes");

    [TestMethod]
    public void SuccessfulRun_ReturnsNumbers_VerifiesChanges_AndCompares()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("set-quality", new { gamePath = game.Root, settings = new { renderScale = 0.8, shadowDistance = 40 } }));

        var first = Run("launch-and-benchmark", Benchmark(game, "before"));

        AssertOk(first);
        var data = first.GetProperty("data");
        Assert.AreEqual("b0001", data.GetProperty("runId").GetString());
        Assert.AreEqual(100, data.GetProperty("avgFps").GetDouble());
        Assert.AreEqual(50, data.GetProperty("low1PctFps").GetDouble());
        Assert.AreEqual("SpecTuner Probe 1.0.0", data.GetProperty("loadedPlugins")[0].GetString());
        StringAssert.Contains(data.GetProperty("logErrors")[0].GetString()!, "NullReferenceException");
        Assert.AreEqual("renderScale", data.GetProperty("overrides").GetProperty("notInEffect")[0].GetString());
        Assert.AreEqual(JsonValueKind.Null, data.GetProperty("comparedToPrevious").ValueKind);
        Assert.IsFalse(game.Exists("BepInEx/config/spectuner.benchmark.json"), "the request never outlives the run");
        foreach (var change in Changes(game).EnumerateArray())
            Assert.IsTrue(change.GetProperty("verified").GetBoolean(), change.ToString());

        FakeGame("result", fps: 120);
        var second = Run("launch-and-benchmark", Benchmark(game, "after"));

        AssertOk(second);
        var compared = second.GetProperty("data").GetProperty("comparedToPrevious");
        Assert.AreEqual("b0001", compared.GetProperty("runId").GetString());
        Assert.AreEqual(20, compared.GetProperty("avgFpsDelta").GetDouble());
        Assert.AreEqual(20, compared.GetProperty("avgFpsDeltaPct").GetDouble());
        Assert.AreEqual("b0001", second.GetProperty("data").GetProperty("comparedToBaseline").GetProperty("runId").GetString());
    }

    [TestMethod]
    public void FailedStartAfterAGoodRun_RevertsOnlyTheNewChanges()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("set-quality", new { gamePath = game.Root, title = "Lower shadows", settings = new { shadowDistance = 40 } }));
        AssertOk(Run("launch-and-benchmark", Benchmark(game)));
        var goodContent = File.ReadAllText(game.Full(QualityFile));
        AssertOk(Run("set-quality", new { gamePath = game.Root, title = "Lower render scale", settings = new { renderScale = 0.5 } }));

        FakeGame("crash");
        var failed = Run("launch-and-benchmark", Benchmark(game));

        AssertRefused(failed, "GAME_FAILED_TO_START");
        var data = failed.GetProperty("data");
        Assert.AreEqual("crashed", data.GetProperty("reason").GetString());
        Assert.AreEqual(3, data.GetProperty("exitCode").GetInt32());
        Assert.IsFalse(data.GetProperty("fullRestore").GetBoolean());
        Assert.AreEqual(1, data.GetProperty("reverted").GetArrayLength());
        Assert.AreEqual("Lower render scale", data.GetProperty("reverted")[0].GetProperty("title").GetString());
        StringAssert.Contains(failed.GetProperty("error").GetProperty("message").GetString()!, "Lower render scale");

        Assert.AreEqual(goodContent, File.ReadAllText(game.Full(QualityFile)));
        var changes = Changes(game);
        Assert.AreEqual("reverted", changes[0].GetProperty("status").GetString());
        Assert.IsTrue(changes.EnumerateArray().Skip(1).All(c => c.GetProperty("status").GetString() == "active"));
        Assert.IsTrue(game.Exists("BepInEx/plugins/SpecTuner/SpecTuner.Probe.dll"));
    }

    [TestMethod]
    public void FailedFirstStart_RemovesEverything()
    {
        var game = CreateGame().UseFakeGameExe();
        var original = game.Hashes();
        FakeGame("crash");

        var failed = Run("launch-and-benchmark", Benchmark(game));

        AssertRefused(failed, "GAME_FAILED_TO_START");
        Assert.IsTrue(failed.GetProperty("data").GetProperty("fullRestore").GetBoolean());
        Assert.AreEqual(2, failed.GetProperty("data").GetProperty("reverted").GetArrayLength(), "loader and probe");
        CollectionAssert.AreEqual(original.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void GameThatNeverFinishes_TimesOut_AndIsClosed()
    {
        var game = CreateGame().UseFakeGameExe();
        FakeGame("hang");

        var failed = Run("launch-and-benchmark", Benchmark(game, startup: 5));

        AssertRefused(failed, "GAME_FAILED_TO_START");
        Assert.AreEqual("timeout", failed.GetProperty("data").GetProperty("reason").GetString());
        Assert.IsFalse(Running(game), "the game process must be closed");
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Windows, IgnoreMessage = "Windows job object; Linux and macOS have KilledSidecar_GameIsClosedByTheNextCommand and StoppedSidecar_ClosesTheGame.")]
    public void KillingTheSidecar_KillsTheGameToo()
    {
        var game = CreateGame().UseFakeGameExe();
        FakeGame("hang");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "spectuner-sidecar.dll"));
        start.ArgumentList.Add("launch-and-benchmark");
        using var sidecar = Process.Start(start)!;
        sidecar.StandardInput.Write(JsonSerializer.Serialize(Benchmark(game, startup: 120)));
        sidecar.StandardInput.Close();

        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!Running(game) && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
        Assert.IsTrue(Running(game), "the fake game should have started");

        sidecar.Kill(); // not the tree: the job object must do it
        sidecar.WaitForExit();
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (Running(game) && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
        Assert.IsFalse(Running(game), "the job object should have closed the game with the sidecar");
    }

    [TestMethod]
    public void ReadLogs_ReturnsThisRunsLines()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertRefused(Run("read-logs", new { gamePath = game.Root }), "LOG_NOT_FOUND");
        AssertOk(Run("launch-and-benchmark", Benchmark(game)));

        var logs = Run("read-logs", new { gamePath = game.Root, source = "bepinex", filter = "SpecTuner", tail = 5 });

        AssertOk(logs);
        var data = logs.GetProperty("data");
        Assert.AreEqual("LogOutput.log", data.GetProperty("file").GetString());
        Assert.AreEqual(1, data.GetProperty("lines").GetArrayLength());
        Assert.AreEqual(1, data.GetProperty("errorCount").GetInt32());
        AssertRefused(Run("read-logs", new { gamePath = game.Root, source = "steam" }), "BAD_REQUEST");
    }

    /// <summary>Starts the real sidecar on a hanging fake game and waits until the game runs.</summary>
    private static Process StartSidecarOnHangingGame(GameFixture game)
    {
        FakeGame("hang");
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "spectuner-sidecar.dll"));
        start.ArgumentList.Add("launch-and-benchmark");
        var sidecar = Process.Start(start)!;
        sidecar.StandardInput.Write(JsonSerializer.Serialize(Benchmark(game, startup: 120)));
        sidecar.StandardInput.Close();
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!Running(game) && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
        Assert.IsTrue(Running(game), "the fake game should have started");
        return sidecar;
    }

    private static void WaitUntilStopped(GameFixture game, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (Running(game) && DateTime.UtcNow < deadline)
            Thread.Sleep(200);
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX, IgnoreMessage = "No job objects on Linux and macOS; Windows has KillingTheSidecar_KillsTheGameToo.")]
    public void KilledSidecar_GameIsClosedByTheNextCommand()
    {
        var game = CreateGame().UseFakeGameExe();
        using var sidecar = StartSidecarOnHangingGame(game);
        var guard = SpecTuner.Sidecar.State.GameState.For(game.Root).RunningGamePath;
        Assert.IsTrue(File.Exists(guard), "the measured game is recorded");

        sidecar.Kill(); // SIGKILL: no handler runs
        sidecar.WaitForExit();
        FakeGame("result");

        // The next command that needs the game closed closes the leftover one instead of refusing.
        AssertOk(Run("launch-and-benchmark", Benchmark(game)));
        Assert.IsFalse(File.Exists(guard));
        Assert.IsFalse(Running(game));
    }

    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX, IgnoreMessage = "Signal handlers are the Linux and macOS guard; Windows uses a job object.")]
    public void StoppedSidecar_ClosesTheGame()
    {
        var game = CreateGame().UseFakeGameExe();
        using var sidecar = StartSidecarOnHangingGame(game);

        using (var kill = Process.Start("kill", ["-TERM", sidecar.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            kill.WaitForExit();
        sidecar.WaitForExit(10000);
        WaitUntilStopped(game, 10);
        Assert.IsFalse(Running(game), "SIGTERM to the sidecar must close the game");
    }

    private static bool Running(GameFixture game) => SpecTuner.Sidecar.Games.GameProcesses.IsRunning(game.ExePath);
}
