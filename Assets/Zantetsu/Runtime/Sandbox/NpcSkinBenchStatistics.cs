using System;
using System.Collections.Generic;
using System.Globalization;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The benchmark's summary of one series of values (ms): the valid samples alone -- an absent or invalid value is
    /// left out, never counted as 0 -- with their count, median, 95th percentile (nearest rank), mean, least and most.
    /// </summary>
    public readonly struct NpcSkinBenchStatistics
    {
        public readonly int count;
        public readonly double median, p95, mean, least, most;

        public NpcSkinBenchStatistics(int count, double median, double p95, double mean, double least, double most)
        {
            this.count = count; this.median = median; this.p95 = p95; this.mean = mean; this.least = least; this.most = most;
        }

        /// <summary>Whether any valid value was seen.</summary>
        public bool Any => count > 0;

        /// <summary>
        /// The statistics of the finite, positive values in <paramref name="values"/> (NaN, infinities and non-positive values
        /// are left out: a frame time of 0 is a missing reading). With <paramref name="zeroIsValue"/>, 0 counts (a marker
        /// that did no work this frame, a count of 0 writes: SKIN-BENCH-2 section 5).
        /// </summary>
        public static NpcSkinBenchStatistics Of(IEnumerable<double> values, bool zeroIsValue = false)
        {
            var valid = new List<double>();
            foreach (double v in values) if (IsValid(v) || (zeroIsValue && v == 0.0)) valid.Add(v);
            if (valid.Count == 0) return new NpcSkinBenchStatistics(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
            valid.Sort();
            double sum = 0;
            for (int i = 0; i < valid.Count; i++) sum += valid[i];
            return new NpcSkinBenchStatistics(valid.Count, Median(valid), Percentile(valid, 0.95), sum / valid.Count, valid[0], valid[valid.Count - 1]);
        }

        /// <summary>Whether a value is a sample at all: finite and above zero (a time of 0 ms is a missing reading, not a frame).</summary>
        public static bool IsValid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0.0;

        private static double Median(List<double> sorted)
        {
            int n = sorted.Count;
            return n % 2 == 1 ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        /// <summary>The nearest-rank percentile of a sorted list: the value at rank ceil(p * n), 1-based.</summary>
        private static double Percentile(List<double> sorted, double p)
        {
            int rank = (int)Math.Ceiling(p * sorted.Count);
            rank = Math.Min(sorted.Count, Math.Max(1, rank));
            return sorted[rank - 1];
        }

        public string Describe(string unit = " ms")
        {
            if (count == 0) return "no valid sample";
            return "n " + count + ", median " + median.ToString("0.000", CultureInfo.InvariantCulture) + unit + ", p95 " + p95.ToString("0.000", CultureInfo.InvariantCulture) + unit
                + ", mean " + mean.ToString("0.000", CultureInfo.InvariantCulture) + unit + ", min " + least.ToString("0.000", CultureInfo.InvariantCulture) + unit
                + ", max " + most.ToString("0.000", CultureInfo.InvariantCulture) + unit;
        }

        /// <summary>The JSON object of these statistics (null values for an empty series).</summary>
        public string Json()
        {
            if (count == 0) return "{\"count\":0,\"median\":null,\"p95\":null,\"mean\":null,\"min\":null,\"max\":null}";
            string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            return "{\"count\":" + count + ",\"median\":" + N(median) + ",\"p95\":" + N(p95) + ",\"mean\":" + N(mean) + ",\"min\":" + N(least) + ",\"max\":" + N(most) + "}";
        }
    }
}
