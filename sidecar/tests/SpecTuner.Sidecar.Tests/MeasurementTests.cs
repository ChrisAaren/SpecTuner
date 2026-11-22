using SpecTuner.Probe;
using SpecTuner.Sidecar.Benchmark;
using SpecTuner.Sidecar.Logs;

namespace SpecTuner.Sidecar.Tests;

[TestClass]
public sealed class MeasurementTests
{
    [TestMethod]
    public void FrameStats_OneSlowFrameInAHundred()
    {
        var frames = Enumerable.Repeat(0.010f, 99).Append(0.050f).ToList();

        var stats = FrameStats.From(frames);

        Assert.AreEqual(100, stats.Frames);
        Assert.AreEqual(1.04, stats.Seconds, 1e-4);
        Assert.AreEqual(10.4, stats.AvgMs, 1e-3);
        Assert.AreEqual(96.154, stats.AvgFps, 1e-2);
        Assert.AreEqual(10.0, stats.P50Ms, 1e-3);
        Assert.AreEqual(10.0, stats.P99Ms, 1e-3);
        Assert.AreEqual(50.0, stats.MaxMs, 1e-3);
        Assert.AreEqual(20.0, stats.Low1PctFps, 1e-2, "1% of 100 frames is the single 50 ms frame");
    }

    [TestMethod]
    public void FrameStats_PercentilesAreNearestRank()
    {
        // 1 ms .. 200 ms, shuffled.
        var rng = new Random(1);
        var frames = Enumerable.Range(1, 200).Select(i => i / 1000f).OrderBy(_ => rng.Next()).ToList();

        var stats = FrameStats.From(frames);

        Assert.AreEqual(100.0, stats.P50Ms, 1e-3);
        Assert.AreEqual(190.0, stats.P95Ms, 1e-3);
        Assert.AreEqual(198.0, stats.P99Ms, 1e-3);
        Assert.AreEqual(200.0, stats.MaxMs, 1e-3);
        Assert.AreEqual(1000.0 / 199.5, stats.Low1PctFps, 1e-3, "slowest 2 of 200 frames: 199 and 200 ms");
    }

    [TestMethod]
    public void Percentile_RoundsTheRankUp()
    {
        double[] sorted = [1, 2, 3, 4, 5, 6, 7];
        Assert.AreEqual(4, FrameStats.Percentile(sorted, 50)); // rank ceil(3.5) = 4
        Assert.AreEqual(7, FrameStats.Percentile(sorted, 95)); // rank ceil(6.65) = 7
        Assert.AreEqual(1, FrameStats.Percentile(sorted, 0));
        double[] ten = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
        Assert.AreEqual(3, FrameStats.Percentile(ten, 21)); // rank ceil(2.1) = 3, not 2
    }

    [TestMethod]
    public void FrameStats_NoFrames_IsAllZero()
    {
        var stats = FrameStats.From(new List<float>());
        Assert.AreEqual(0, stats.Frames);
        Assert.AreEqual(0, stats.AvgFps);
    }

    [TestMethod]
    public void Comparison_Maths()
    {
        var before = new BenchmarkRun { RunId = "b0001", Label = "before", AvgFps = 100, Low1PctFps = 50, P95Ms = 12.5 };
        var after = new BenchmarkRun { RunId = "b0002", Label = "after", AvgFps = 112.346, Low1PctFps = 45, P95Ms = 11.0 };

        var c = BenchmarkHistory.Compare(after, before);

        Assert.AreEqual("b0001", c.RunId);
        Assert.AreEqual(12.35, c.AvgFpsDelta);
        Assert.AreEqual(12.3, c.AvgFpsDeltaPct);
        Assert.AreEqual(-5.0, c.Low1PctFpsDelta);
        Assert.AreEqual(-10.0, c.Low1PctFpsDeltaPct);
        Assert.AreEqual(-1.5, c.P95MsDelta);
    }

    [TestMethod]
    public void History_BaselineIsTheFirstSuccess_PreviousIsTheLastSuccess()
    {
        var history = new BenchmarkHistory();
        history.Runs.Add(new BenchmarkRun { RunId = "b0001", Ok = false });
        history.Runs.Add(new BenchmarkRun { RunId = "b0002", Ok = true });
        history.Runs.Add(new BenchmarkRun { RunId = "b0003", Ok = true });
        history.Runs.Add(new BenchmarkRun { RunId = "b0004", Ok = false });

        Assert.AreEqual("b0002", history.Baseline!.RunId);
        Assert.AreEqual("b0003", history.LastSuccessful!.RunId);
        Assert.AreEqual("b0005", history.NextRunId());
    }

    [TestMethod]
    public void MiniJson_RoundTrips()
    {
        var obj = new JObj { { "a", 1.5 }, { "b", "x\"y" }, { "c", new[] { 1, 2 } }, { "d", null }, { "e", true }, { "f", new JObj { { "g", 2 } } } };

        var parsed = (Dictionary<string, object>)MiniJson.Parse(MiniJson.Write(obj));

        Assert.AreEqual(1.5, parsed["a"]);
        Assert.AreEqual("x\"y", parsed["b"]);
        Assert.HasCount(2, (List<object>)parsed["c"]);
        Assert.IsNull(parsed["d"]);
        Assert.IsTrue((bool)parsed["e"]);
        Assert.AreEqual(2.0, ((Dictionary<string, object>)parsed["f"])["g"]);
    }

    [TestMethod]
    public void LogFilter_KeepsMatchesAndAlwaysTheErrors()
    {
        var lines = new List<string>();
        for (var i = 0; i < 100; i++)
            lines.Add($"[Info   :   BepInEx] line {i}");
        lines.Insert(10, "[Error  : Unity Log] NullReferenceException: Object reference not set");
        lines.Insert(20, "[Info   :DoubleJump] jump 1");
        lines.Add("[Info   :DoubleJump] jump 2");
        lines.Add("[Info   :DoubleJump] jump 3");

        var excerpt = LogReader.Filter(lines, "doublejump", tail: 2);

        CollectionAssert.AreEqual(new[] { "[Info   :DoubleJump] jump 2", "[Info   :DoubleJump] jump 3" }, excerpt.Lines);
        Assert.IsTrue(excerpt.Truncated);
        Assert.AreEqual(1, excerpt.ErrorCount);
        StringAssert.Contains(excerpt.Errors[0], "NullReferenceException");
        Assert.AreEqual(104, excerpt.TotalLines);
    }

    [TestMethod]
    public void LogFilter_HidesTheUserProfile_AndCapsLineLength()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var line = $"Loading {profile}\\AppData\\LocalLow\\Co\\Game\\Player.log " + new string('x', 500);

        var cleaned = LogReader.Clean(line);

        Assert.IsFalse(cleaned.Contains(profile, StringComparison.OrdinalIgnoreCase));
        StringAssert.StartsWith(cleaned, "Loading %USERPROFILE%\\AppData");
        Assert.AreEqual(LogReader.MaxLineLength + 3, cleaned.Length);
    }

    [TestMethod]
    public void LoadedPlugins_AreReadFromTheBepInExLog()
    {
        var plugins = LogReader.LoadedPlugins(
        [
            "[Info   :   BepInEx] 2 plugins to load",
            "[Info   :   BepInEx] Loading [SpecTuner Probe 1.0.0]",
            "[Info   :   BepInEx] Loading [Double Jump 1.0.0]",
            "[Message:   BepInEx] Chainloader startup complete",
        ]);

        CollectionAssert.AreEqual(new[] { "SpecTuner Probe 1.0.0", "Double Jump 1.0.0" }, plugins);
    }
}
