#if ZANTETSU_STATS_VERIFY || UNITY_EDITOR
using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// A fixed-size tally of one measured value, for the VERIFICATION build of the play statistics alone (DESIGN 21.18,
    /// "検証専用の構成"): how many, the sum, the sum of squares, the least and the most, and counts by fixed upper edges
    /// with one overflow. Nothing of the samples is kept and nothing is allocated after construction. Compiled in the
    /// Editor (for its test) and under <c>ZANTETSU_STATS_VERIFY</c>; no product Player, Development or Release, holds it.
    /// </summary>
    public sealed class PlayVerifyTally
    {
        /// <summary>Edges for a cost in microseconds: from a twentieth of one to ten milliseconds.</summary>
        public static readonly double[] MicrosecondEdges =
        {
            0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000,
        };

        /// <summary>Edges for a frame's time in milliseconds: quarter steps to 20 ms, then coarser to 100 ms.</summary>
        public static readonly double[] MillisecondEdges = MakeMillisecondEdges();

        private static double[] MakeMillisecondEdges()
        {
            double[] coarse = { 22, 25, 28, 32, 36, 40, 45, 50, 60, 70, 85, 100 };
            var edges = new double[80 + coarse.Length];
            for (int i = 0; i < 80; i++) edges[i] = 0.25 * (i + 1);
            Array.Copy(coarse, 0, edges, 80, coarse.Length);
            return edges;
        }

        private readonly double[] _edges;
        private readonly ulong[] _counts;

        public PlayVerifyTally(double[] upperEdges)
        {
            if (upperEdges == null || upperEdges.Length == 0) throw new ArgumentException("edges are needed", nameof(upperEdges));
            for (int i = 1; i < upperEdges.Length; i++)
            {
                if (!(upperEdges[i] > upperEdges[i - 1])) throw new ArgumentException("edges must rise", nameof(upperEdges));
            }

            _edges = upperEdges;
            _counts = new ulong[upperEdges.Length];
        }

        public ulong Count { get; private set; }

        /// <summary>Values that were not finite or were negative: counted here, in no bucket and no sum.</summary>
        public ulong Refused { get; private set; }

        /// <summary>Values at or past the last edge.</summary>
        public ulong Overflow { get; private set; }

        public double Sum { get; private set; }
        public double SumOfSquares { get; private set; }
        public double Min { get; private set; }
        public double Max { get; private set; }
        public int BucketCount => _edges.Length;
        public ulong this[int bucket] => _counts[bucket];
        public double UpperEdge(int bucket) => _edges[bucket];

        public bool Add(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0.0)
            {
                Refused++;
                return false;
            }

            int bucket = -1;
            for (int i = 0; i < _edges.Length; i++)
            {
                if (value < _edges[i])
                {
                    bucket = i;
                    break;
                }
            }

            if (bucket < 0) Overflow++;
            else _counts[bucket]++;
            if (Count == 0 || value < Min) Min = value;
            if (Count == 0 || value > Max) Max = value;
            Count++;
            Sum += value;
            SumOfSquares += value * value;
            return true;
        }

        public double Mean => Count > 0 ? Sum / Count : 0.0;

        /// <summary>The sample standard deviation (n - 1); 0 with fewer than two values.</summary>
        public double StandardDeviation
        {
            get
            {
                if (Count < 2) return 0.0;
                double n = Count, variance = (SumOfSquares - Sum * Sum / n) / (n - 1.0);
                return variance > 0.0 ? Math.Sqrt(variance) : 0.0;
            }
        }

        /// <summary>
        /// The upper edge of the bucket that holds the value of that rank (fraction of the count, rounded up): the value
        /// is under it, and at or over the edge before. Positive infinity where the rank is in the overflow; 0 with no value.
        /// </summary>
        public double UnderEdgeAt(double fraction)
        {
            if (Count == 0) return 0.0;
            ulong rank = (ulong)Math.Ceiling(fraction * Count);
            if (rank < 1) rank = 1;
            if (rank > Count) rank = Count;
            ulong seen = 0;
            for (int i = 0; i < _counts.Length; i++)
            {
                seen += _counts[i];
                if (seen >= rank) return _edges[i];
            }

            return double.PositiveInfinity;
        }

        /// <summary>One line: the count, mean, standard deviation, the bucket edges of the median, 95th and 99th, the least and the most.</summary>
        public string Describe(string unit, string format = "F3")
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            if (Count == 0) return "n 0" + (Refused > 0 ? " (refused " + Refused + ")" : "");
            var b = new StringBuilder(160);
            b.Append("n ").Append(Count).Append(", mean ").Append(Mean.ToString(format, c)).Append(' ').Append(unit)
                .Append(", sd ").Append(StandardDeviation.ToString(format, c))
                .Append(", p50 <").Append(Edge(UnderEdgeAt(0.5), c)).Append(", p95 <").Append(Edge(UnderEdgeAt(0.95), c)).Append(", p99 <").Append(Edge(UnderEdgeAt(0.99), c))
                .Append(", min ").Append(Min.ToString(format, c)).Append(", max ").Append(Max.ToString(format, c));
            if (Overflow > 0) b.Append(", over the last edge ").Append(Overflow);
            if (Refused > 0) b.Append(", refused ").Append(Refused);
            return b.ToString();
        }

        /// <summary>The buckets that hold anything, as "under edge: count".</summary>
        public string DescribeBuckets()
        {
            CultureInfo c = CultureInfo.InvariantCulture;
            var b = new StringBuilder(160);
            for (int i = 0; i < _counts.Length; i++)
            {
                if (_counts[i] == 0) continue;
                if (b.Length > 0) b.Append(", ");
                b.Append('<').Append(Edge(_edges[i], c)).Append(": ").Append(_counts[i]);
            }

            if (Overflow > 0)
            {
                if (b.Length > 0) b.Append(", ");
                b.Append(">=").Append(Edge(_edges[_edges.Length - 1], c)).Append(": ").Append(Overflow);
            }

            return b.Length > 0 ? b.ToString() : "(none)";
        }

        private static string Edge(double edge, CultureInfo c) => double.IsPositiveInfinity(edge) ? "inf" : edge.ToString("0.###", c);
    }
}
#endif
