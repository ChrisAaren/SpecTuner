// Shared file: compiled into the sidecar (for tests) and embedded into the
// probe plugin source. Must stay C# 9 and use only APIs that exist in
// Unity's Mono mscorlib.
#nullable disable
using System;
using System.Collections.Generic;

namespace SpecTuner.Probe
{
    /// <summary>Frame-time statistics from per-frame delta times in seconds.</summary>
    public sealed class FrameStats
    {
        public int Frames;
        public double Seconds;
        public double AvgFps;
        /// <summary>FPS over the slowest 1% of frames: 1000 / mean of the slowest max(1, n/100) frame times in ms.</summary>
        public double Low1PctFps;
        public double AvgMs;
        public double P50Ms;
        public double P95Ms;
        public double P99Ms;
        public double MaxMs;

        public static FrameStats From(IList<float> deltaSeconds)
        {
            var stats = new FrameStats();
            int n = deltaSeconds.Count;
            if (n == 0)
                return stats;

            var ms = new double[n];
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                ms[i] = deltaSeconds[i] * 1000.0;
                total += ms[i];
            }
            Array.Sort(ms);

            stats.Frames = n;
            stats.Seconds = total / 1000.0;
            stats.AvgMs = total / n;
            stats.AvgFps = total > 0 ? n / stats.Seconds : 0;
            stats.P50Ms = Percentile(ms, 50);
            stats.P95Ms = Percentile(ms, 95);
            stats.P99Ms = Percentile(ms, 99);
            stats.MaxMs = ms[n - 1];

            int worst = Math.Max(1, n / 100);
            double worstTotal = 0;
            for (int i = n - worst; i < n; i++)
                worstTotal += ms[i];
            stats.Low1PctFps = worstTotal > 0 ? 1000.0 / (worstTotal / worst) : 0;
            return stats;
        }

        /// <summary>
        /// Average FPS of <paramref name="count"/> consecutive sub-windows of equal duration.
        /// A frame belongs to the sub-window in which it ends.
        /// </summary>
        public static double[] WindowFps(IList<float> deltaSeconds, int count)
        {
            var result = new double[count];
            double total = 0;
            for (int i = 0; i < deltaSeconds.Count; i++)
                total += deltaSeconds[i];
            if (total <= 0 || count <= 0)
                return result;

            var frames = new int[count];
            var seconds = new double[count];
            double elapsed = 0;
            for (int i = 0; i < deltaSeconds.Count; i++)
            {
                elapsed += deltaSeconds[i];
                int w = Math.Min(count - 1, (int)(elapsed / total * count - 1e-9));
                frames[w]++;
                seconds[w] += deltaSeconds[i];
            }
            for (int w = 0; w < count; w++)
                result[w] = seconds[w] > 0 ? frames[w] / seconds[w] : 0;
            return result;
        }

        /// <summary>Nearest-rank percentile of an ascending array.</summary>
        public static double Percentile(double[] sorted, double percent)
        {
            if (sorted.Length == 0)
                return 0;
            int rank = (int)Math.Ceiling(percent / 100.0 * sorted.Length);
            if (rank < 1)
                rank = 1;
            if (rank > sorted.Length)
                rank = sorted.Length;
            return sorted[rank - 1];
        }
    }
}
