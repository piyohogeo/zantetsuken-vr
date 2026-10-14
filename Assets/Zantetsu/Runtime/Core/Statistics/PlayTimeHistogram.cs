using System;

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// A fixed-length histogram of a frame time, in milliseconds (DESIGN 21.18). The buckets are fixed by
    /// <see cref="UpperEdgesMs"/> -- a sample falls into the first bucket whose upper edge is above it -- and one more
    /// bucket, <see cref="Overflow"/>, takes every sample at or past the last edge, so that a long frame is never
    /// dropped. Nothing of the samples themselves is kept: the counts, the overflow, the most, and how many. The sum
    /// of the buckets and the overflow is always <see cref="Samples"/>. Allocates once, at construction.
    /// </summary>
    public sealed class PlayTimeHistogram
    {
        /// <summary>The bucket edges, in milliseconds: 1 ms steps to 16 ms, then coarser to 100 ms. Changing them is a new <see cref="Version"/>.</summary>
        public static readonly float[] UpperEdgesMs =
        {
            1f, 2f, 3f, 4f, 5f, 6f, 7f, 8f, 9f, 10f, 11f, 12f, 13f, 14f, 15f, 16f,
            18f, 20f, 22f, 25f, 28f, 32f, 36f, 40f, 45f, 50f, 60f, 70f, 85f, 100f,
        };

        /// <summary>The version of the bucket definition above (the receiver reads the counts by it).</summary>
        public const int Version = 1;

        public static int BucketCount => UpperEdgesMs.Length;

        private readonly uint[] _counts = new uint[UpperEdgesMs.Length];

        /// <summary>Samples at or past the last edge.</summary>
        public uint Overflow { get; private set; }

        /// <summary>Every sample taken, the overflow included.</summary>
        public ulong Samples { get; private set; }

        /// <summary>The longest sample, ms; 0 before any.</summary>
        public float MaxMs { get; private set; }

        /// <summary>The count of one bucket.</summary>
        public uint this[int bucket] => _counts[bucket];

        /// <summary>
        /// Takes one sample. A sample that is not a finite, non-negative number is refused and counted by nobody here:
        /// the caller counts what it could not get apart from what it got.
        /// </summary>
        public bool Add(float ms)
        {
            if (!float.IsFinite(ms) || ms < 0f)
            {
                return false;
            }

            int bucket = BucketOf(ms);
            if (bucket < 0)
            {
                Overflow++;
            }
            else
            {
                _counts[bucket]++;
            }

            Samples++;
            if (ms > MaxMs)
            {
                MaxMs = ms;
            }

            return true;
        }

        /// <summary>The bucket a sample falls into, or -1 for the overflow.</summary>
        public static int BucketOf(float ms)
        {
            for (int i = 0; i < UpperEdgesMs.Length; i++)
            {
                if (ms < UpperEdgesMs[i])
                {
                    return i;
                }
            }

            return -1;
        }

        public void Clear()
        {
            Array.Clear(_counts, 0, _counts.Length);
            Overflow = 0;
            Samples = 0;
            MaxMs = 0f;
        }

        /// <summary>The sum of the buckets and the overflow: equals <see cref="Samples"/> by construction (a test's check).</summary>
        public ulong Sum()
        {
            ulong sum = Overflow;
            for (int i = 0; i < _counts.Length; i++)
            {
                sum += _counts[i];
            }

            return sum;
        }
    }
}
