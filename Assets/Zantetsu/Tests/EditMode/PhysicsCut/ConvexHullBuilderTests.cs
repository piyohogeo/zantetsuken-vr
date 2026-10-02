using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The convex hull builder of the building hull trial: polygon faces, interior and edge points dropped, refusals, the
    /// B-rep it fills, and -- apart from the approximation it makes -- the error of its result **in metres**, checked
    /// independently of the builder's own report: every input point inside every face (an outer envelope that trims by
    /// numeric noise only), every vertex convex, every face planar, every edge two-faced, at sizes of 2, 20 and 200 m, in
    /// several poses and input orders.
    /// </summary>
    public class ConvexHullBuilderTests
    {
        private static List<float3> Box(float3 centre, float3 half)
        {
            var pts = new List<float3>();
            for (int i = 0; i < 8; i++) pts.Add(centre + new float3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z));
            return pts;
        }

        /// <summary>A box cut at x = 0.3: the left half as it stands, the right half moved down and turned (a fallen side); extent about 2 m.</summary>
        private static List<float3> HalvesApart(int trial)
        {
            var pts = new List<float3>();
            const float x = 0.3f;
            foreach (float3 c in Box(0f, 1f)) if (c.x < x) pts.Add(c);
            var section = new[] { new float3(x, -1f, -1f), new float3(x, -1f, 1f), new float3(x, 1f, -1f), new float3(x, 1f, 1f) };
            pts.AddRange(section);
            float4x4 moved = float4x4.TRS(new float3(0.02f * trial, -0.6f - 0.01f * trial, 0.03f * trial), quaternion.EulerXYZ(0.05f * trial, 0.1f * trial, 0.02f * trial), 1f);
            foreach (float3 c in Box(0f, 1f)) if (c.x > x) pts.Add(math.transform(moved, c));
            foreach (float3 c in section) pts.Add(math.transform(moved, c));
            return pts;
        }

        /// <summary>The shared measure, and every face polygon convex within its own plane (no reflex corner past the numeric tolerance, one full turn) asserted on the way (2026-09-30).</summary>
        private static void Measure(IReadOnlyList<float3> pts, float3[] v, int[] offsets, int[] indices, out double outside, out double nonConvex, out double nonPlanar)
        {
            HullChecks.Measure(pts, v, offsets, indices, out outside, out nonConvex, out nonPlanar, out double reflex, out double turning);
            double numeric = ConvexHullBuilder.NumericTolerance * HullChecks.Extent(pts);
            Assert.That(reflex, Is.LessThanOrEqualTo(numeric), "a face polygon has a reflex corner of " + reflex + " m within its own plane");
            Assert.That(turning, Is.LessThanOrEqualTo(1e-6), "a face polygon does not turn once (" + turning + " rad from 2 pi)");
        }

        private static double Extent(IReadOnlyList<float3> pts) => HullChecks.Extent(pts);

        [Test]
        public void ABoxWithInteriorAndEdgePoints_IsSixQuadsOnEightCorners()
        {
            List<float3> pts = Box(0f, 1f);
            pts.Add(new float3(0.2f, -0.1f, 0.3f));   // interior
            pts.Add(new float3(0f, -1f, -1f));   // on an edge
            pts.Add(new float3(0.3f, 1f, 0.2f));   // on a face
            Assert.That(ConvexHullBuilder.TryBuild(pts, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report), Is.True, report.reason);
            Assert.That(v.Length, Is.EqualTo(8), "eight corners");
            Assert.That(offsets.Length - 1, Is.EqualTo(6), "six faces");
            for (int f = 0; f < 6; f++) Assert.That(offsets[f + 1] - offsets[f], Is.EqualTo(4), "each a quad");
            Assert.That(report.shrink, Is.LessThanOrEqualTo(1e-6), "no input point outside (" + report.shrink + " m)");
            Assert.That(report.expansion, Is.LessThanOrEqualTo(1e-6), "nothing filled (" + report.expansion + " m)");
            using (HullBrep brep = HullBrep.FromArrays(v, offsets, indices))
            {
                Assert.That(brep.Range.edgeCount, Is.EqualTo(12));
                MassProperties m = brep.Mass();
                Assert.That(m.volume, Is.EqualTo(8.0).Within(1e-6), "the volume of the box (outward winding)");
                Assert.That(math.length(m.CenterOfMass), Is.LessThan(1e-6));
            }
        }

        [Test]
        public void TwoBoxesApart_GiveOneHullFillingTheGap_WithinTheVertexLimit()
        {
            List<float3> pts = Box(new float3(-1.5f, 0f, 0f), new float3(1f, 1f, 1f));
            pts.AddRange(Box(new float3(2f, 0.4f, 0.2f), new float3(1f, 0.8f, 1f)));
            Assert.That(ConvexHullBuilder.TryBuild(pts, out float3[] v, out int[] offsets, out int[] indices, out string reason), Is.True, reason);
            Assert.That(v.Length, Is.LessThanOrEqualTo(16));
            using (HullBrep brep = HullBrep.FromArrays(v, offsets, indices))
            {
                double volume = brep.Mass().volume;
                Assert.That(volume, Is.GreaterThan(8.0 + 6.4), "more than the two boxes: the gap is filled");
                Assert.That(brep.Range.edgeCount, Is.EqualTo(brep.VertexCount + brep.FaceCount - 2), "Euler: a closed convex");
            }
        }

        /// <summary>
        /// **The shape error by size, pose and input order, in metres.** The two halves apart (twenty placements) at 2, 20
        /// and 200 m, turned and moved, the points shuffled: measured here, no input point stands outside a face by more
        /// than the numeric tolerance of the extent (36 µm at 3.6 m, 3.6 mm at 360 m), the vertices are convex, the faces
        /// planar and the hull closed; the builder's own shrink agrees; the expansion (what a merge filled) is recorded and
        /// stays under the merge tolerance of the extent. Volume alone would not see a lost corner: the containment does.
        /// </summary>
        [Test]
        public void ShapeError_AtSizesPosesAndOrders_TrimsByNumericNoiseOnly()
        {
            var rng = new Unity.Mathematics.Random(777);
            var poses = new[]
            {
                (quaternion.identity, float3.zero),
                (quaternion.EulerXYZ(0.3f, 1.1f, -0.4f), new float3(3f, -2f, 5f)),
                (quaternion.EulerXYZ(-1.2f, 0.2f, 2.5f), new float3(-6f, 4f, -1.5f)),
            };
            double worstShrinkRatio = 0.0, worstExpansionRatio = 0.0;
            var table = new List<string>();
            foreach (float scale in new[] { 1f, 10f, 100f })
            {
                for (int trial = 0; trial < 20; trial++)
                {
                    for (int pose = 0; pose < poses.Length; pose++)
                    {
                        for (int order = 0; order < 2; order++)
                        {
                            List<float3> pts = HalvesApart(trial);
                            for (int i = 0; i < pts.Count; i++) pts[i] = math.rotate(poses[pose].Item1, pts[i] * scale) + poses[pose].Item2 * scale;
                            if (order == 1) for (int i = pts.Count - 1; i > 0; i--) { int j = rng.NextInt(i + 1); (pts[i], pts[j]) = (pts[j], pts[i]); }
                            string label = "scale " + scale + " trial " + trial + " pose " + pose + " order " + order;
                            Assert.That(ConvexHullBuilder.TryBuild(pts, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report), Is.True, label + ": " + report.reason);
                            double extent = Extent(pts);
                            double numeric = ConvexHullBuilder.NumericTolerance * extent;
                            Measure(pts, v, offsets, indices, out double outside, out double nonConvex, out double nonPlanar);
                            Assert.That(outside, Is.LessThanOrEqualTo(numeric), label + ": an input point stands " + outside + " m outside a face (allowed " + numeric + ")");
                            Assert.That(nonConvex, Is.LessThanOrEqualTo(numeric), label + ": a vertex stands " + nonConvex + " m outside a face");
                            Assert.That(nonPlanar, Is.LessThanOrEqualTo(numeric), label + ": a face's vertices leave its plane by " + nonPlanar + " m");
                            Assert.That(report.shrink, Is.LessThanOrEqualTo(numeric), label + ": the builder's shrink " + report.shrink);
                            Assert.That(report.expansion, Is.LessThanOrEqualTo(ConvexHullBuilder.PlaneTolerance * extent), label + ": the expansion " + report.expansion + " m is past the merge tolerance");
                            worstShrinkRatio = math.max(worstShrinkRatio, outside / extent);
                            worstExpansionRatio = math.max(worstExpansionRatio, report.expansion / extent);
                            if (trial % 5 == 0 && order == 0) table.Add(label + ": extent " + extent.ToString("F2") + " m, " + v.Length + " v " + (offsets.Length - 1) + " f, shrink " + outside.ToString("E2") + " m, expansion " + report.expansion.ToString("E2") + " m, merges " + report.merges);
                        }
                    }
                }
            }

            foreach (string line in table) TestContext.Out.WriteLine(line);
            TestContext.Out.WriteLine("worst shrink / extent " + worstShrinkRatio.ToString("E2") + ", worst expansion / extent " + worstExpansionRatio.ToString("E2"));
        }

        /// <summary>Random clouds of 6 to 45 points at 2 and 200 m: closed, convex, every point inside by the numeric tolerance (metres), never smaller than what it holds.</summary>
        [Test]
        public void RandomClouds_AtTwoSizes_HoldEveryPointWithinNumericNoise()
        {
            var rng = new Unity.Mathematics.Random(12345);
            foreach (float scale in new[] { 1f, 100f })
            {
                for (int trial = 0; trial < 40; trial++)
                {
                    var pts = new List<float3>();
                    int count = 6 + trial;
                    for (int i = 0; i < count; i++) pts.Add(rng.NextFloat3(-2f, 2f) * new float3(1f, 0.5f + 0.5f * (trial % 3), 1f) * scale);
                    string label = "scale " + scale + " trial " + trial;
                    Assert.That(ConvexHullBuilder.TryBuild(pts, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report), Is.True, label + ": " + report.reason);
                    double numeric = ConvexHullBuilder.NumericTolerance * Extent(pts);
                    Measure(pts, v, offsets, indices, out double outside, out double nonConvex, out double nonPlanar);
                    Assert.That(outside, Is.LessThanOrEqualTo(numeric), label + ": a point stands " + outside + " m outside");
                    Assert.That(nonConvex, Is.LessThanOrEqualTo(numeric), label + ": not convex by " + nonConvex + " m");
                    Assert.That(nonPlanar, Is.LessThanOrEqualTo(numeric), label + ": not planar by " + nonPlanar + " m");
                    using (HullBrep brep = HullBrep.FromArrays(v, offsets, indices)) Assert.That(brep.Mass().volume, Is.GreaterThan(0.0), label + ": a positive volume");
                }
            }
        }

        /// <summary>Two flush halves a millimetre apart (the sixteen vertices the clip kernel once failed to walk): merged to one box of eight vertices whose expansion is under the merge tolerance, with no point outside.</summary>
        [Test]
        public void FlushHalves_MergeToOneBox_FilledWithinTheMergeTolerance()
        {
            var pts = new List<float3>
            {
                new float3(-1f, -1f, 1f), new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, -1f, 1f), new float3(1.00000179f, -0.150071219f, -1.00000465f), new float3(1f, -0.149999976f, 1f),
                new float3(-1f, -0.149999976f, 1f), new float3(0.999780834f, 0.150743648f, -1.00023913f), new float3(0.9991587f, 1.001487f, 0.999096f), new float3(0.9997829f, 0.150743484f, 0.9997617f),
                new float3(-1.00084078f, 1.0000174f, 0.999098837f), new float3(-1.0002172f, 0.1507428f, 0.999764562f), new float3(-1.00084245f, 0.998459637f, -1.00090051f), new float3(0.9995152f, 0.1507448f, -1.0005213f),
                new float3(0.999157f, 0.999929249f, -1.00090337f), new float3(-1.00048208f, 0.150742769f, 0.9994814f),
            };
            Assert.That(ConvexHullBuilder.TryBuild(pts, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report), Is.True, report.reason);
            Measure(pts, v, offsets, indices, out double outside, out _, out _);
            double extent = Extent(pts);
            TestContext.Out.WriteLine("flush halves: " + v.Length + " vertices " + (offsets.Length - 1) + " faces, shrink " + outside.ToString("E2") + " m, expansion " + report.expansion.ToString("E2") + " m");
            Assert.That(v.Length, Is.EqualTo(8), "one box");
            Assert.That(outside, Is.LessThanOrEqualTo(ConvexHullBuilder.NumericTolerance * extent));
            Assert.That(report.expansion, Is.LessThanOrEqualTo(ConvexHullBuilder.PlaneTolerance * extent), "the millimetre step is filled, not more");
        }

        /// <summary>Two halves of a 25 m box, the second turned by a hair (1e-5 rad) and lifted by a millimetre -- the faces of both nearly one plane, as a resting side against the standing one: built, the faces folded, no point outside, the expansion within the merge tolerance.</summary>
        [Test]
        public void NearlyParallelFacesOfTwoHalves_FoldIntoOne()
        {
            for (int trial = 0; trial < 6; trial++)
            {
                var pts = new List<float3>();
                foreach (float3 c in Box(new float3(-6f, 10f, 0f), new float3(6f, 10f, 4f))) pts.Add(c);
                float4x4 moved = float4x4.TRS(new float3(0f, 0.001f * trial, 0f), quaternion.EulerXYZ(0f, 0f, 1e-5f * trial), 1f);
                foreach (float3 c in Box(new float3(6f, 10f, 0f), new float3(6f, 10f, 4f))) pts.Add(math.transform(moved, c));
                Assert.That(ConvexHullBuilder.TryBuild(pts, out float3[] v, out int[] offsets, out int[] indices, out HullBuildReport report), Is.True, "trial " + trial + ": " + report.reason);
                double extent = Extent(pts);
                Measure(pts, v, offsets, indices, out double outside, out double nonConvex, out double nonPlanar);
                TestContext.Out.WriteLine("trial " + trial + ": " + v.Length + " v " + (offsets.Length - 1) + " f, shrink " + outside.ToString("E2") + " m, expansion " + report.expansion.ToString("E2") + " m");
                Assert.That(outside, Is.LessThanOrEqualTo(ConvexHullBuilder.NumericTolerance * extent), "trial " + trial + ": a point stands " + outside + " m outside");
                Assert.That(nonPlanar, Is.LessThanOrEqualTo(ConvexHullBuilder.NumericTolerance * extent), "trial " + trial + ": not planar by " + nonPlanar + " m");
                Assert.That(report.expansion, Is.LessThanOrEqualTo(ConvexHullBuilder.PlaneTolerance * extent), "trial " + trial + ": expansion " + report.expansion + " m");
                Assert.That(v.Length, Is.LessThanOrEqualTo(12), "trial " + trial + ": the halves' faces folded (" + v.Length + " vertices)");
            }
        }

        [Test]
        public void FlatOrFewPoints_AreRefused()
        {
            Assert.That(ConvexHullBuilder.TryBuild(new List<float3> { 0f, new float3(1f, 0f, 0f), new float3(0f, 1f, 0f) }, out _, out _, out _, out string reason), Is.False);
            StringAssert.Contains("four", reason);
            var flat = new List<float3>();
            for (int i = 0; i < 12; i++) flat.Add(new float3(i, i * i % 5, 0f));
            Assert.That(ConvexHullBuilder.TryBuild(flat, out _, out _, out _, out reason), Is.False);
            Assert.That(reason, Is.EqualTo("coplanar"));
        }
    }
}
