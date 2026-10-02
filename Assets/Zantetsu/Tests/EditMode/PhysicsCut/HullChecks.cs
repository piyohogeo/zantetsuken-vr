using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The independent verification of a hull the builder gave (its final float output, never the builder's own report):
    /// every input point inside every face, every vertex inside every face, every face planar, every face polygon convex
    /// within its own plane (no reflex corner), every edge two-faced and the surface closed -- measured here in metres.
    /// </summary>
    internal static class HullChecks
    {
        public static void Measure(IReadOnlyList<float3> pts, float3[] v, int[] offsets, int[] indices, out double outside, out double nonConvex, out double nonPlanar)
        {
            Measure(pts, v, offsets, indices, out outside, out nonConvex, out nonPlanar, out _);
        }

        /// <summary>As above, and the worst reflex corner of a face polygon within its own plane (metres: how far a corner stands inside the chord of its two neighbours, against the face's Newell normal; positive is reflex).</summary>
        public static void Measure(IReadOnlyList<float3> pts, float3[] v, int[] offsets, int[] indices, out double outside, out double nonConvex, out double nonPlanar, out double reflex)
        {
            Measure(pts, v, offsets, indices, out outside, out nonConvex, out nonPlanar, out reflex, out _);
        }

        /// <summary>
        /// As above, and the worst departure of a face polygon's turning from one full turn (radians): the signed exterior
        /// angles of a convex polygon, walked once, add up to 2 pi. A hairpin -- the polygon walking out along a line and
        /// back, which the reflex distance cannot see when its chord is short -- adds about pi.
        /// </summary>
        public static void Measure(IReadOnlyList<float3> pts, float3[] v, int[] offsets, int[] indices, out double outside, out double nonConvex, out double nonPlanar, out double reflex, out double turning)
        {
            outside = nonConvex = nonPlanar = 0.0;
            reflex = double.NegativeInfinity;
            turning = 0.0;
            int faces = offsets.Length - 1;
            for (int f = 0; f < faces; f++)
            {
                int from = offsets[f], count = offsets[f + 1] - from;
                // Newell's normal over the polygon, the plane through its centroid.
                double3 n = 0, c = 0;
                for (int k = 0; k < count; k++)
                {
                    double3 a = v[indices[from + k]], b = v[indices[from + (k + 1) % count]];
                    n += math.cross(a, b);
                    c += a;
                }

                double area = math.length(n) * 0.5;
                n = math.normalize(n);
                c /= count;
                double d = math.dot(n, c);
                double faceOutside = 0.0;
                for (int k = 0; k < count; k++) nonPlanar = math.max(nonPlanar, math.abs(math.dot(n, (double3)v[indices[from + k]]) - d));
                foreach (float3 p in pts) faceOutside = math.max(faceOutside, math.dot(n, (double3)p) - d);
                foreach (float3 p in v) nonConvex = math.max(nonConvex, math.dot(n, (double3)p) - d);
                // The exterior angles of the polygon projected onto its plane: a closed plane polygon turns a whole number of times exactly.
                double turn = 0.0;
                for (int k = 0; k < count; k++)
                {
                    double3 e0 = (double3)v[indices[from + (k + 1) % count]] - (double3)v[indices[from + k]], e1 = (double3)v[indices[from + (k + 2) % count]] - (double3)v[indices[from + (k + 1) % count]];
                    e0 -= math.dot(e0, n) * n;
                    e1 -= math.dot(e1, n) * n;
                    turn += math.atan2(math.dot(math.cross(e0, e1), n), math.dot(e0, e1));
                }

                double off = math.abs(turn - 2.0 * math.PI_DBL);
                if (off > turning)
                {
                    turning = off;
                    if (off > 1e-6) TestContext.Out.WriteLine("face " + f + " (" + count + " vertices) turns " + turn.ToString("R") + " rad, not one full turn");
                }
                for (int k = 0; k < count; k++)
                {
                    double3 p0 = v[indices[from + k]], p1 = v[indices[from + (k + 1) % count]], p2 = v[indices[from + (k + 2) % count]];
                    double chord = math.length(p2 - p0);
                    double r = chord > 0.0 ? -math.dot(math.cross(p1 - p0, p2 - p1), n) / chord : 0.0;
                    if (r > reflex)
                    {
                        reflex = r;
                        if (r > 1e-3) TestContext.Out.WriteLine("reflex corner " + r.ToString("E2") + " m at face " + f + " corner " + k + " (" + count + " vertices): " + (Vector3)v[indices[from + k]] + " " + (Vector3)v[indices[from + (k + 1) % count]] + " " + (Vector3)v[indices[from + (k + 2) % count]]);
                    }
                }
                if (faceOutside > outside)
                {
                    outside = faceOutside;
                    if (faceOutside > 1e-3)
                    {
                        var text = new System.Text.StringBuilder("face " + f + " (" + count + " vertices, area " + area.ToString("E2") + ", normal " + n + "):");
                        for (int k = 0; k < count; k++) text.Append(' ').Append(indices[from + k]).Append('=').Append((Vector3)v[indices[from + k]]);
                        TestContext.Out.WriteLine(text.ToString());
                    }
                }
            }

            var seen = new HashSet<long>();
            int edges = 0;
            for (int f = 0; f < faces; f++)
            {
                int from = offsets[f], count = offsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = indices[from + k], b = indices[from + (k + 1) % count];
                    Assert.That(seen.Add(((long)a << 32) | (uint)b), Is.True, "an edge runs the same way twice");
                    edges++;
                }
            }

            foreach (long key in seen) Assert.That(seen.Contains(((key & 0xFFFFFFFFL) << 32) | (uint)(key >> 32)), Is.True, "an edge has one face");
            Assert.That(v.Length - edges / 2 + faces, Is.EqualTo(2), "Euler: a closed convex");
        }

        public static double Extent(IReadOnlyList<float3> pts)
        {
            float3 lo = float.MaxValue, hi = float.MinValue;
            foreach (float3 p in pts) { lo = math.min(lo, p); hi = math.max(hi, p); }
            return math.cmax(hi - lo);
        }

        /// <summary>The whole verification of one build against its input: the builder accepted it, and the output holds every point within the numeric tolerance of the extent, is convex, planar and closed; the expansion within the merge tolerance.</summary>
        public static void AssertHull(IReadOnlyList<float3> pts, float3[] v, int[] offsets, int[] indices, HullBuildReport report, string label, int vertexLimit = 0)
        {
            double extent = Extent(pts);
            double numeric = ConvexHullBuilder.NumericTolerance * extent;
            Measure(pts, v, offsets, indices, out double outside, out double nonConvex, out double nonPlanar, out double reflex, out double turning);
            Assert.That(reflex, Is.LessThanOrEqualTo(numeric), label + ": a face polygon has a reflex corner of " + reflex + " m within its own plane");
            Assert.That(turning, Is.LessThanOrEqualTo(1e-6), label + ": a face polygon does not turn once (" + turning + " rad from 2 pi): a hairpin or a fold within its plane");
            if (vertexLimit > 0) Assert.That(v.Length, Is.LessThanOrEqualTo(vertexLimit), label + ": " + v.Length + " vertices, past the limit " + vertexLimit);
            Assert.That(outside, Is.LessThanOrEqualTo(numeric), label + ": an input point stands " + outside + " m outside a face (allowed " + numeric + ")");
            Assert.That(nonConvex, Is.LessThanOrEqualTo(numeric), label + ": a vertex stands " + nonConvex + " m outside a face");
            Assert.That(nonPlanar, Is.LessThanOrEqualTo(numeric), label + ": a face's vertices leave its plane by " + nonPlanar + " m");
            Assert.That(report.shrink, Is.LessThanOrEqualTo(numeric), label + ": the builder's own shrink " + report.shrink);
            Assert.That(report.expansion, Is.LessThanOrEqualTo(ConvexHullBuilder.PlaneTolerance * extent), label + ": the expansion " + report.expansion + " m is past the merge tolerance");
        }
    }
}
