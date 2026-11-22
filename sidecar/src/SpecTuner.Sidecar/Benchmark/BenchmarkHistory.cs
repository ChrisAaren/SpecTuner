using SpecTuner.Sidecar.State;

namespace SpecTuner.Sidecar.Benchmark;

public sealed class BenchmarkRun
{
    public string RunId { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime At { get; set; }
    public bool Ok { get; set; }
    public double AvgFps { get; set; }
    public double Low1PctFps { get; set; }
    public double P95Ms { get; set; }
    public int Frames { get; set; }
    public string? Resolution { get; set; }
    public string? Failure { get; set; }
    /// <summary>True when the numbers are the A windows of a same-launch trial rather than one continuous measurement.</summary>
    public bool Trial { get; set; }
}

public sealed record BenchmarkComparison(
    string RunId, string Label,
    double AvgFpsDelta, double AvgFpsDeltaPct,
    double Low1PctFpsDelta, double Low1PctFpsDeltaPct,
    double P95MsDelta,
    bool WithinNoise);

/// <summary><c>benchmarks.json</c>: every run since the last full restore. The first successful run is the baseline.</summary>
public sealed class BenchmarkHistory
{
    public List<BenchmarkRun> Runs { get; set; } = [];

    public static BenchmarkHistory Load(GameState state) => AtomicFile.ReadJson<BenchmarkHistory>(state.BenchmarksPath) ?? new BenchmarkHistory();

    public void Save(GameState state) => AtomicFile.WriteJson(state.BenchmarksPath, this);

    public string NextRunId() => $"b{Runs.Count + 1:D4}";

    public BenchmarkRun? LastSuccessful => Runs.LastOrDefault(r => r.Ok);

    public BenchmarkRun? Baseline => Runs.FirstOrDefault(r => r.Ok);

    // Comparisons are made only between runs of the same kind. On the test game the frame rate
    // follows the title scene's own timeline, so trial A windows (spread over the run) and one
    // continuous measurement window cover different moments and are not comparable.
    public BenchmarkRun? LastSuccessfulOfKind(bool trial) => Runs.LastOrDefault(r => r.Ok && r.Trial == trial);

    public BenchmarkRun? BaselineOfKind(bool trial) => Runs.FirstOrDefault(r => r.Ok && r.Trial == trial);

    public static BenchmarkComparison Compare(BenchmarkRun current, BenchmarkRun earlier) => new(
        earlier.RunId,
        earlier.Label,
        Math.Round(current.AvgFps - earlier.AvgFps, 2),
        Percent(current.AvgFps, earlier.AvgFps),
        Math.Round(current.Low1PctFps - earlier.Low1PctFps, 2),
        Percent(current.Low1PctFps, earlier.Low1PctFps),
        Math.Round(current.P95Ms - earlier.P95Ms, 2),
        Verdict.WithinLaunchNoise(Percent(current.AvgFps, earlier.AvgFps)));

    private static double Percent(double now, double before) =>
        before == 0 ? 0 : Math.Round((now - before) / before * 100.0, 1);
}
