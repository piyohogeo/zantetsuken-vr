using System;
using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>A world-space capsule: the segment between two centres, swept by a radius.</summary>
    public readonly struct LocomotionCapsule
    {
        public readonly Vector3 a;
        public readonly Vector3 b;
        public readonly float radius;

        public LocomotionCapsule(Vector3 a, Vector3 b, float radius)
        {
            this.a = a;
            this.b = b;
            this.radius = radius;
        }

        /// <summary>
        /// A capsule of <paramref name="height"/> end to end along the local Y axis of <paramref name="rotation"/>,
        /// centred on <paramref name="center"/>. A height below two radii is a sphere.
        /// </summary>
        public static LocomotionCapsule Upright(Vector3 center, Quaternion rotation, float radius, float height)
        {
            float half = Mathf.Max(0f, (height * 0.5f) - radius);
            Vector3 axis = rotation * Vector3.up;
            return new LocomotionCapsule(center - (axis * half), center + (axis * half), radius);
        }
    }

    /// <summary>A world-space oriented box: centre, rotation, half extents.</summary>
    public readonly struct LocomotionBox
    {
        public readonly Vector3 center;
        public readonly Quaternion rotation;
        public readonly Vector3 halfExtents;

        public LocomotionBox(Vector3 center, Quaternion rotation, Vector3 halfExtents)
        {
            this.center = center;
            this.rotation = rotation;
            this.halfExtents = halfExtents;
        }
    }

    /// <summary>
    /// The two overlap tests the fixed locomotion occupancy needs (DESIGN 7.2.3): a capsule against an oriented box,
    /// and against another capsule. Touching counts as overlapping. Nothing here is a general query system.
    /// </summary>
    public static class LocomotionOverlap
    {
        /// <summary>Whether the capsule and the box share a point.</summary>
        public static bool Overlaps(in LocomotionCapsule capsule, in LocomotionBox box)
        {
            // In the box's frame the box is axis-aligned; the distance from the segment to it is convex along the
            // segment, so a bracketing search finds its minimum.
            Quaternion inverse = Quaternion.Inverse(box.rotation);
            Vector3 a = inverse * (capsule.a - box.center);
            Vector3 b = inverse * (capsule.b - box.center);
            float r2 = capsule.radius * capsule.radius;
            float lo = 0f;
            float hi = 1f;
            for (int i = 0; i < 48; i++)
            {
                float m1 = lo + ((hi - lo) / 3f);
                float m2 = hi - ((hi - lo) / 3f);
                if (DistanceSqToBox(Vector3.LerpUnclamped(a, b, m1), box.halfExtents)
                    <= DistanceSqToBox(Vector3.LerpUnclamped(a, b, m2), box.halfExtents))
                {
                    hi = m2;
                }
                else
                {
                    lo = m1;
                }
            }

            float best = Math.Min(
                DistanceSqToBox(Vector3.LerpUnclamped(a, b, (lo + hi) * 0.5f), box.halfExtents),
                Math.Min(DistanceSqToBox(a, box.halfExtents), DistanceSqToBox(b, box.halfExtents)));
            return best <= r2;
        }

        /// <summary>Whether the two capsules share a point.</summary>
        public static bool Overlaps(in LocomotionCapsule first, in LocomotionCapsule second)
        {
            float reach = first.radius + second.radius;
            return SegmentDistanceSq(first.a, first.b, second.a, second.b) <= reach * reach;
        }

        private static float DistanceSqToBox(Vector3 p, Vector3 half)
        {
            float dx = Math.Max(Math.Abs(p.x) - half.x, 0f);
            float dy = Math.Max(Math.Abs(p.y) - half.y, 0f);
            float dz = Math.Max(Math.Abs(p.z) - half.z, 0f);
            return (dx * dx) + (dy * dy) + (dz * dz);
        }

        /// <summary>The squared distance between two segments (the closest points, clamped to both).</summary>
        private static float SegmentDistanceSq(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2)
        {
            Vector3 d1 = q1 - p1;
            Vector3 d2 = q2 - p2;
            Vector3 r = p1 - p2;
            float a = Vector3.Dot(d1, d1);
            float e = Vector3.Dot(d2, d2);
            float f = Vector3.Dot(d2, r);
            float s;
            float t;
            const float Epsilon = 1e-12f;
            if (a <= Epsilon && e <= Epsilon)
            {
                return r.sqrMagnitude;
            }

            if (a <= Epsilon)
            {
                s = 0f;
                t = Mathf.Clamp01(f / e);
            }
            else
            {
                float c = Vector3.Dot(d1, r);
                if (e <= Epsilon)
                {
                    t = 0f;
                    s = Mathf.Clamp01(-c / a);
                }
                else
                {
                    float bb = Vector3.Dot(d1, d2);
                    float denom = (a * e) - (bb * bb);
                    s = denom > Epsilon ? Mathf.Clamp01(((bb * f) - (c * e)) / denom) : 0f;
                    t = ((bb * s) + f) / e;
                    if (t < 0f)
                    {
                        t = 0f;
                        s = Mathf.Clamp01(-c / a);
                    }
                    else if (t > 1f)
                    {
                        t = 1f;
                        s = Mathf.Clamp01((bb - c) / a);
                    }
                }
            }

            return ((p1 + (d1 * s)) - (p2 + (d2 * t))).sqrMagnitude;
        }
    }
}
