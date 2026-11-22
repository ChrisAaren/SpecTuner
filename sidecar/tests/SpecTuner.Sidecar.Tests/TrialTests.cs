using System.Text.Json;
using SpecTuner.Probe;
using SpecTuner.Sidecar.Benchmark;
using SpecTuner.Sidecar.Tests.Support;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class VerdictTests
{
    [TestMethod]
    public void ClearWin_IsBetter()
    {
        var v = Verdict.Decide([4.1, 5.0, 3.8, 4.6], keysApplied: 1, keysTried: 1);
        Assert.AreEqual(Verdict.Better, v.Verdict);
        StringAssert.Contains(v.Reason, "4.4% higher");
    }

    [TestMethod]
    public void ClearLoss_IsWorse()
    {
        Assert.AreEqual(Verdict.Worse, Verdict.Decide([-6, -4, -5, -7], 1, 1).Verdict);
    }

    [TestMethod]
    public void OneContraryPair_IsAllowedFromFourPairs_ButNotFromThree()
    {
        Assert.AreEqual(Verdict.Better, Verdict.Decide([8, 6, -1, 7], 1, 1).Verdict);
        Assert.AreEqual(Verdict.NoClearDifference, Verdict.Decide([8, -1, 7], 1, 1).Verdict);
        Assert.AreEqual(Verdict.NoClearDifference, Verdict.Decide([8, 6, -1, -2, 9], 1, 1).Verdict, "two contrary pairs");
        Assert.AreEqual(Verdict.Worse, Verdict.Decide([-8, -6, 1, -7], 1, 1).Verdict);
    }

    [TestMethod]
    public void MixedPairs_AreNoClearDifference()
    {
        var v = Verdict.Decide([10, -6, 9, -5], 1, 1);
        Assert.AreEqual(Verdict.NoClearDifference, v.Verdict);
        StringAssert.Contains(v.Reason, "not consistently");
    }

    [TestMethod]
    public void TinyConsistentDelta_IsNoClearDifference()
    {
        var v = Verdict.Decide([1.0, 2.0, 1.5, 2.5], 1, 1);
        Assert.AreEqual(Verdict.NoClearDifference, v.Verdict);
        StringAssert.Contains(v.Reason, "too little");
        Assert.AreEqual(Verdict.Better, Verdict.Decide([3.0, 3.0, 3.0, 3.0], 1, 1).Verdict, "exactly +3% counts");
    }

    [TestMethod]
    public void NothingApplied_IsNotApplied_EvenWithBigDeltas()
    {
        Assert.AreEqual(Verdict.NotApplied, Verdict.Decide([20, 20, 20, 20], keysApplied: 0, keysTried: 2).Verdict);
    }

    [TestMethod]
    public void PartlyApplied_SaysSo()
    {
        StringAssert.Contains(Verdict.Decide([5, 5, 5, 5], 1, 2).Reason, "only some");
    }

    [TestMethod]
    public void PairDeltas_AreBMinusAOverA()
    {
        CollectionAssert.AreEqual(new[] { 10.0, -5.0 }, Verdict.PairDeltasPct([100, 200], [110, 190]));
    }

    [TestMethod]
    public void Spread_IsMaxMinusMinOverMean()
    {
        Assert.AreEqual(4.0, Verdict.SpreadPct([98, 102, 100, 100]));
        Assert.IsNull(Verdict.SpreadPct([100]));
    }

    [TestMethod]
    public void SmallLaunchDifferences_AreNotPresentedAsChanges()
    {
        StringAssert.Contains(Verdict.DescribeLaunchChange(9.9, "the previous test"), "within normal run-to-run variation");
        StringAssert.Contains(Verdict.DescribeLaunchChange(-9.9, "the previous test"), "within normal run-to-run variation");
        Assert.AreEqual("That is +10.0% compared with the previous test.", Verdict.DescribeLaunchChange(10.0, "the previous test"));
        Assert.IsTrue(BenchmarkHistory.Compare(new BenchmarkRun { AvgFps = 109 }, new BenchmarkRun { AvgFps = 100 }).WithinNoise);
        Assert.IsFalse(BenchmarkHistory.Compare(new BenchmarkRun { AvgFps = 111 }, new BenchmarkRun { AvgFps = 100 }).WithinNoise);
    }

    [TestMethod]
    public void WindowFps_SplitsByTime()
    {
        // First half of the time at 100 fps (10 ms frames), second half at 50 fps (20 ms).
        var frames = Enumerable.Repeat(0.010f, 100).Concat(Enumerable.Repeat(0.020f, 50)).ToList();

        var fps = FrameStats.WindowFps(frames, 4);

        Assert.AreEqual(100, fps[0], 0.5);
        Assert.AreEqual(100, fps[1], 0.5);
        Assert.AreEqual(50, fps[2], 0.5);
        Assert.AreEqual(50, fps[3], 0.5);
    }
}

[TestClass]
public sealed class TrialTests : SidecarTest
{
    private const string QualityFile = "BepInEx/config/spectuner.quality.json";

    [TestInitialize]
    public void FakeGameDefaults()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", "result");
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", "100");
    }

    [TestCleanup]
    public void ClearFakeGame()
    {
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME", null);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", null);
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_TRIAL_FPS", null);
    }

    private static object Trial(GameFixture game, object trySettings, int pairs = 4) =>
        new { gamePath = game.Root, label = "trial", warmupSeconds = 0, startupTimeoutSeconds = 30, trySettings, pairs, windowSeconds = 2, settleSeconds = 1 };

    [TestMethod]
    [DataRow("{\"bloom\": true}", "INVALID_SETTING")]
    [DataRow("{\"qualityLevel\": 1}", "INVALID_SETTING")]
    [DataRow("{\"fullscreenMode\": \"Windowed\"}", "INVALID_SETTING")]
    [DataRow("{\"resolutionWidth\": 1280, \"resolutionHeight\": 720}", "INVALID_SETTING")]
    [DataRow("{\"vSyncCount\": 0}", "INVALID_SETTING")]
    [DataRow("{\"renderScale\": null}", "INVALID_SETTING")]
    [DataRow("{\"renderScale\": 7}", "INVALID_SETTING")]
    [DataRow("{}", "BAD_REQUEST")]
    public void BadTrySettings_AreRefused_BeforeAnythingHappens(string trySettings, string code)
    {
        var game = CreateGame().UseFakeGameExe();
        var before = game.Hashes();
        var request = $"{{\"gamePath\": {JsonSerializer.Serialize(game.Root)}, \"trySettings\": {trySettings}}}";

        AssertRefused(RunRaw(["launch-and-benchmark"], request).Envelope, code);
        CollectionAssert.AreEqual(before.ToList(), game.Hashes().ToList());
    }

    [TestMethod]
    public void QualityLevelRefusal_SaysWhatToDoInstead()
    {
        var game = CreateGame().UseFakeGameExe();
        var envelope = Run("launch-and-benchmark", Trial(game, new { qualityLevel = 1 }));
        StringAssert.Contains(envelope.GetProperty("error").GetProperty("message").GetString()!, "set-quality");
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(11)]
    public void PairsOutOfRange_AreRefused(int pairs)
    {
        var game = CreateGame().UseFakeGameExe();
        AssertRefused(Run("launch-and-benchmark", Trial(game, new { lodBias = 0.5 }, pairs)), "BAD_REQUEST");
    }

    [TestMethod]
    public void TrialRun_GivesAVerdict_AndPersistsNothing()
    {
        var game = CreateGame().UseFakeGameExe();
        Run("set-quality", new { gamePath = game.Root, settings = new { shadowDistance = 40 } });
        var savedFile = File.ReadAllText(game.Full(QualityFile));
        var changesBefore = Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("total").GetInt32();
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_TRIAL_FPS", "110,108,99,112");

        var envelope = Run("launch-and-benchmark", Trial(game, new { lodBias = 0.5 }));

        AssertOk(envelope);
        var data = envelope.GetProperty("data");
        Assert.AreEqual(100, data.GetProperty("avgFps").GetDouble(), "top-level numbers are the A windows");
        var trial = data.GetProperty("trial");
        Assert.AreEqual("better", trial.GetProperty("verdict").GetString(), trial.ToString());
        Assert.AreEqual(4, trial.GetProperty("pairResults").GetArrayLength());
        Assert.AreEqual(-1.0, trial.GetProperty("pairResults")[2].GetProperty("deltaPct").GetDouble());
        Assert.AreEqual(7.25, trial.GetProperty("meanDeltaPct").GetDouble());
        Assert.AreEqual("lodBias", trial.GetProperty("confirmed")[0].GetString());
        Assert.AreEqual(0.0, data.GetProperty("windowSpreadPct").GetDouble(), "A windows all at 100 fps");
        StringAssert.Contains(envelope.GetProperty("summary").GetString()!, "Trying lodBias");

        Assert.AreEqual(savedFile, File.ReadAllText(game.Full(QualityFile)), "the overrides file is not touched");
        Assert.AreEqual(changesBefore, Run("changes", new { gamePath = game.Root }).GetProperty("data").GetProperty("total").GetInt32());
        Assert.IsFalse(game.Exists("BepInEx/config/spectuner.benchmark.json"));
    }

    [TestMethod]
    public void TrialThatDidNotTakeEffect_IsNotApplied()
    {
        var game = CreateGame().UseFakeGameExe();
        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_TRIAL_FPS", "130,130,130,130");

        var trial = Run("launch-and-benchmark", Trial(game, new { renderScale = 0.5 })).GetProperty("data").GetProperty("trial");

        Assert.AreEqual("not_applied", trial.GetProperty("verdict").GetString());
        Assert.AreEqual("renderScale", trial.GetProperty("notConfirmed")[0].GetString());
    }

    [TestMethod]
    public void TrialAndOrdinaryRuns_AreOnlyComparedWithTheirOwnKind()
    {
        var game = CreateGame().UseFakeGameExe();
        AssertOk(Run("launch-and-benchmark", Trial(game, new { lodBias = 0.5 })));

        var ordinary = Run("launch-and-benchmark", new { gamePath = game.Root, warmupSeconds = 0, measureSeconds = 1 });
        Assert.AreEqual(JsonValueKind.Null, ordinary.GetProperty("data").GetProperty("comparedToPrevious").ValueKind);

        var secondTrial = Run("launch-and-benchmark", Trial(game, new { lodBias = 0.5 }));
        Assert.AreEqual("b0001", secondTrial.GetProperty("data").GetProperty("comparedToPrevious").GetProperty("runId").GetString());
    }

    [TestMethod]
    public void OrdinaryRun_ReportsSpread_AndCallsSmallDifferencesNoise()
    {
        var game = CreateGame().UseFakeGameExe();
        var first = Run("launch-and-benchmark", new { gamePath = game.Root, warmupSeconds = 0, measureSeconds = 1 });
        Assert.AreEqual(4.0, first.GetProperty("data").GetProperty("windowSpreadPct").GetDouble());

        Environment.SetEnvironmentVariable("SPECTUNER_FAKE_GAME_FPS", "103");
        var second = Run("launch-and-benchmark", new { gamePath = game.Root, warmupSeconds = 0, measureSeconds = 1 });

        Assert.IsTrue(second.GetProperty("data").GetProperty("comparedToPrevious").GetProperty("withinNoise").GetBoolean());
        StringAssert.Contains(second.GetProperty("summary").GetString()!, "within normal run-to-run variation");
    }
}
