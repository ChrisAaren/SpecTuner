using System.Globalization;

namespace SpecTuner.Sidecar.Benchmark;

public sealed record TrialVerdict(string Verdict, string Reason);

/// <summary>The rules that turn measurements into a plain judgement. Kept together so they are easy to find and test.</summary>
public static class Verdict
{
    public const string Better = "better";
    public const string Worse = "worse";
    public const string NoClearDifference = "no_clear_difference";
    public const string NotApplied = "not_applied";

    /// <summary>Minimum mean paired change in average FPS for a trial to count as better or worse.</summary>
    public const double MinTrialDeltaPct = 3.0;

    /// <summary>Differences between separate launches below this are within normal run-to-run variation.</summary>
    public const double LaunchNoisePct = 10.0;

    /// <summary>
    /// better: mean paired avg-FPS change ≥ +3% and every pair positive, allowing one
    /// exception when there are more than 3 pairs. worse: the mirror image.
    /// not_applied: none of the tried settings took effect. Otherwise no_clear_difference.
    /// </summary>
    public static TrialVerdict Decide(IReadOnlyList<double> pairDeltasPct, int keysApplied, int keysTried)
    {
        if (keysApplied == 0)
            return new(NotApplied, "None of the tried settings took effect in this game, so there was nothing to compare.");
        if (pairDeltasPct.Count == 0)
            return new(NoClearDifference, "No measurements were taken.");

        var mean = pairDeltasPct.Average();
        var allowedMisses = pairDeltasPct.Count <= 3 ? 0 : 1;
        var up = pairDeltasPct.Count(d => d > 0);
        var down = pairDeltasPct.Count(d => d < 0);
        var meanText = Pct(mean);
        var partly = keysApplied < keysTried ? " (only some of the tried settings took effect)" : "";

        if (mean >= MinTrialDeltaPct && pairDeltasPct.Count - up <= allowedMisses)
            return new(Better, $"Frame rate was {Plain(mean)} higher with the change, consistently across {up} of {pairDeltasPct.Count} comparisons{partly}.");
        if (mean <= -MinTrialDeltaPct && pairDeltasPct.Count - down <= allowedMisses)
            return new(Worse, $"Frame rate was {Plain(mean)} lower with the change, consistently across {down} of {pairDeltasPct.Count} comparisons{partly}.");
        var inconsistent = up > allowedMisses && down > allowedMisses;
        return inconsistent || Math.Abs(mean) >= MinTrialDeltaPct
            ? new(NoClearDifference, $"The frame rate moved {meanText} on average, but not consistently across the comparisons{partly}.")
            : new(NoClearDifference, $"The change moved the frame rate by only {meanText} on average, too little to count{partly}.");
    }

    /// <summary>Per pair: (B - A) / A in percent.</summary>
    public static List<double> PairDeltasPct(IReadOnlyList<double> aFps, IReadOnlyList<double> bFps) =>
        aFps.Zip(bFps, (a, b) => a > 0 ? Math.Round((b - a) / a * 100.0, 2) : 0).ToList();

    /// <summary>(max - min) / mean of sub-window average FPS, in percent. Null when there are fewer than two windows.</summary>
    public static double? SpreadPct(IReadOnlyList<double> windowFps)
    {
        var values = windowFps.Where(v => v > 0).ToList();
        if (values.Count < 2)
            return null;
        return Math.Round((values.Max() - values.Min()) / values.Average() * 100.0, 1);
    }

    public static bool WithinLaunchNoise(double deltaPct) => Math.Abs(deltaPct) < LaunchNoisePct;

    /// <summary>How a difference between two separate launches is put into words.</summary>
    public static string DescribeLaunchChange(double deltaPct, string comparedWith) =>
        WithinLaunchNoise(deltaPct)
            ? $"The difference from {comparedWith} ({Pct(deltaPct)}) is within normal run-to-run variation, so it is not a real change."
            : $"That is {Pct(deltaPct)} compared with {comparedWith}.";

    private static string Plain(double value) => Math.Abs(value).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Pct(double value) => value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture) + "%";
}
