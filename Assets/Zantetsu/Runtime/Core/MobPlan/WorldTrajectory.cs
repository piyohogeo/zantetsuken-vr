using System;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// Uniformly sampled absolute-time world trajectory (XZ + yaw). All candidate trajectories of one planning cycle
    /// share the same StartTime and Dt so pairwise checks are index-aligned. Piecewise linear between samples.
    /// </summary>
    public sealed class WorldTrajectory
    {
        public float StartTime;
        public float Dt;
        public float[] X = Array.Empty<float>();
        public float[] Z = Array.Empty<float>();
        public float[] Yaw = Array.Empty<float>();
        public float MinX, MaxX, MinZ, MaxZ;
        /// <summary>Per whole-second bucket AABB (from StartTime) for cheap prefiltering.</summary>
        public float[] BucketMinX = Array.Empty<float>();
        public float[] BucketMaxX = Array.Empty<float>();
        public float[] BucketMinZ = Array.Empty<float>();
        public float[] BucketMaxZ = Array.Empty<float>();

        public int Count => X.Length;
        public float EndTime => StartTime + (Count - 1) * Dt;

        public void ComputeBounds()
        {
            MinX = float.MaxValue; MaxX = float.MinValue; MinZ = float.MaxValue; MaxZ = float.MinValue;
            var buckets = Count == 0 ? 0 : (int)Math.Floor((Count - 1) * Dt) + 1;
            BucketMinX = new float[buckets]; BucketMaxX = new float[buckets]; BucketMinZ = new float[buckets]; BucketMaxZ = new float[buckets];
            for (var b = 0; b < buckets; b++) { BucketMinX[b] = float.MaxValue; BucketMaxX[b] = float.MinValue; BucketMinZ[b] = float.MaxValue; BucketMaxZ[b] = float.MinValue; }
            for (var i = 0; i < Count; i++)
            {
                if (X[i] < MinX) MinX = X[i];
                if (X[i] > MaxX) MaxX = X[i];
                if (Z[i] < MinZ) MinZ = Z[i];
                if (Z[i] > MaxZ) MaxZ = Z[i];
                // A sample belongs to the bucket of its own time and (as segment end) to the previous one.
                var b = Math.Min(buckets - 1, (int)Math.Floor(i * Dt));
                Extend(b, X[i], Z[i]);
                if (i > 0)
                {
                    var previous = Math.Min(buckets - 1, (int)Math.Floor((i - 1) * Dt));
                    if (previous != b) Extend(previous, X[i], Z[i]);
                }
            }
        }

        private void Extend(int b, float x, float z)
        {
            if (x < BucketMinX[b]) BucketMinX[b] = x;
            if (x > BucketMaxX[b]) BucketMaxX[b] = x;
            if (z < BucketMinZ[b]) BucketMinZ[b] = z;
            if (z > BucketMaxZ[b]) BucketMaxZ[b] = z;
        }

        public bool BoundsOverlap(WorldTrajectory other, float inflate)
        {
            return !(MaxX + inflate < other.MinX || other.MaxX + inflate < MinX || MaxZ + inflate < other.MinZ || other.MaxZ + inflate < MinZ);
        }

        /// <summary>Does the [t0, t1] portion of this trajectory's bucket AABBs overlap the given box (inflated)?</summary>
        public bool BucketsOverlap(float t0, float t1, float minX, float maxX, float minZ, float maxZ, float inflate)
        {
            if (BucketMinX.Length == 0) return false;
            var b0 = Math.Max(0, (int)Math.Floor(t0 - StartTime));
            var b1 = Math.Min(BucketMinX.Length - 1, (int)Math.Floor(t1 - StartTime));
            for (var b = b0; b <= b1; b++)
            {
                if (BucketMaxX[b] + inflate < minX || maxX + inflate < BucketMinX[b] || BucketMaxZ[b] + inflate < minZ || maxZ + inflate < BucketMinZ[b]) continue;
                return true;
            }

            return false;
        }

        /// <summary>Linear interpolation at an absolute time (clamped to the sampled range).</summary>
        public void PositionAt(float time, out float x, out float z)
        {
            if (Count == 0) { x = 0f; z = 0f; return; }
            var s = (time - StartTime) / Dt;
            if (s <= 0f) { x = X[0]; z = Z[0]; return; }
            var i = (int)Math.Floor(s);
            if (i >= Count - 1) { x = X[Count - 1]; z = Z[Count - 1]; return; }
            var w = s - i;
            x = X[i] + (X[i + 1] - X[i]) * w;
            z = Z[i] + (Z[i + 1] - Z[i]) * w;
        }
    }

    /// <summary>Continuous circle-circle collision between index-aligned trajectories (Section 9.3).</summary>
    public static class CircleSweep
    {
        /// <summary>
        /// Minimum separation of two aligned trajectories and the first time the separation drops below the threshold
        /// (NaN when never). Both must share StartTime and Dt. The shorter trajectory is held at its last sample until
        /// the longer one ends: every stored plan ends in a wait state, so its final position persists.
        /// </summary>
        public static float MinSeparation(WorldTrajectory a, WorldTrajectory b, float threshold, out float firstConflictTime)
        {
            firstConflictTime = float.NaN;
            var count = Math.Max(a.Count, b.Count);
            if (a.Count == 0 || b.Count == 0) return float.PositiveInfinity;
            var min = float.PositiveInfinity;
            var thresholdSq = threshold * threshold;
            var lastA = a.Count - 1;
            var lastB = b.Count - 1;
            for (var i = 0; i + 1 < count; i++)
            {
                var ia0 = Math.Min(i, lastA); var ia1 = Math.Min(i + 1, lastA);
                var ib0 = Math.Min(i, lastB); var ib1 = Math.Min(i + 1, lastB);
                var d0x = a.X[ia0] - b.X[ib0];
                var d0z = a.Z[ia0] - b.Z[ib0];
                var ux = (a.X[ia1] - b.X[ib1]) - d0x;
                var uz = (a.Z[ia1] - b.Z[ib1]) - d0z;
                var uu = ux * ux + uz * uz;
                var s = 0f;
                if (uu > 1e-12f)
                {
                    s = -(d0x * ux + d0z * uz) / uu;
                    if (s < 0f) s = 0f; else if (s > 1f) s = 1f;
                }

                var dx = d0x + s * ux;
                var dz = d0z + s * uz;
                var distSq = dx * dx + dz * dz;
                if (distSq < min * min) min = (float)Math.Sqrt(distSq);
                if (float.IsNaN(firstConflictTime) && distSq < thresholdSq)
                {
                    // Earliest s in [0, 1] with |d0 + s u|^2 < threshold^2: solve the quadratic, take the smaller root clamped.
                    var first = s;
                    if (uu > 1e-12f)
                    {
                        var bq = 2f * (d0x * ux + d0z * uz);
                        var cq = d0x * d0x + d0z * d0z - thresholdSq;
                        var disc = bq * bq - 4f * uu * cq;
                        if (disc >= 0f)
                        {
                            var root = (-bq - (float)Math.Sqrt(disc)) / (2f * uu);
                            first = Math.Max(0f, Math.Min(1f, root));
                        }
                    }

                    firstConflictTime = a.StartTime + (i + first) * a.Dt;
                }
            }

            if (count == 1)
            {
                var dx = a.X[0] - b.X[0];
                var dz = a.Z[0] - b.Z[0];
                min = (float)Math.Sqrt(dx * dx + dz * dz);
                if (min < threshold) firstConflictTime = a.StartTime;
            }

            return min;
        }
    }
}
