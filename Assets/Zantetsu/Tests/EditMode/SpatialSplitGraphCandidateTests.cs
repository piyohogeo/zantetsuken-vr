using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.Editor.Sandbox;
using Zantetsu.PhysicsCut;
using static Zantetsu.Core.Tests.SpatialSplitReferenceDeciderTests;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The graph candidate (TL 2026-10-22): sound by construction -- every placement it finds is verified here by the
    /// reference decider's geometric check of the pairs across the sides, and its verdict is compared with the
    /// reference's. The fixtures are built with the reference tests' helpers (axis-aligned boxes, triangles, identity
    /// frames unless said). A mesh component is a set of triangles sharing vertex indices; <see cref="SharedMesh"/>
    /// shares the indices of equal positions, <see cref="Mesh"/> never does.
    /// </summary>
    public class SpatialSplitGraphCandidateTests
    {
        private const float M = 1f / 4096f;
        private const float Hair = 1f / (1 << 20);

        private static CandidateResult Candidate(SpatialSplitSample sample, CandidateStages stages = CandidateStages.ABC, CandidateSettings settings = null)
        {
            settings = settings ?? new CandidateSettings();
            settings.stages = stages;
            CandidateResult c = SpatialSplitGraphCandidate.Decide(sample, settings);
            TestContext.Out.WriteLine(sample.objectName + " [" + stages + "]: " + c.verdict + " at " + c.stage + " (" + c.reason + "); mesh components " + c.meshComponents
                + ", candidates A " + c.candidatesA + ", B tried/refuted/ambiguous " + c.triedB + "/" + c.refutedB + "/" + c.ambiguousB + ", C tried/refuted/confirmed/ambiguous "
                + c.triedC + "/" + c.refutedC + "/" + c.confirmedC + "/" + c.ambiguousC + ", leaf tests " + c.leafTests + ", cuts " + c.cutAttempts + ", " + (c.seconds * 1000.0).ToString("F1") + " ms");
            Assert.That(c.verdict, Is.Not.EqualTo(CandidateVerdict.InvalidInput), c.reason);
            ReferenceDecision reference = SpatialSplitReferenceDecider.Decide(sample, 120.0);
            Assert.That(reference.verdict, Is.Not.EqualTo(ReferenceVerdict.Undecided).And.Not.EqualTo(ReferenceVerdict.InvalidInput), "the reference: " + reference.reason);
            if (c.verdict == CandidateVerdict.Separable)
            {
                // Every element placed once, both sides full, and the reference's check of the pairs across the sides.
                Assert.That(c.convexPositive.Length, Is.EqualTo(c.convexCount));
                Assert.That(c.trianglePositive.Length, Is.EqualTo(c.triangleCount));
                Assert.That(c.positiveConvexes + c.negativeConvexes, Is.EqualTo(c.convexCount), "every convex placed once");
                Assert.That(c.positiveTriangles + c.negativeTriangles, Is.EqualTo(c.triangleCount), "every triangle placed once");
                Assert.That(c.positiveConvexes > 0 && c.positiveTriangles > 0 && c.negativeConvexes > 0 && c.negativeTriangles > 0, Is.True, "both sides with a convex and a triangle");
                SpatialSplitReferenceDecider.PlacementCheck check = SpatialSplitReferenceDecider.VerifyPlacement(sample, c.convexPositive, c.trianglePositive, 120.0);
                TestContext.Out.WriteLine("  verified by the reference: " + check.valid + " (" + check.reason + "; pairs across " + check.pairsAcross + ", exact " + check.pairsJudgedExactly + ")");
                Assert.That(check.valid, Is.True, sample.objectName + ": the placement, by the reference: " + check.reason);
                SpatialSplitReferenceDecider.PlacementCheck exact = SpatialSplitReferenceDecider.VerifyPlacement(sample, c.convexPositive, c.trianglePositive, 600.0, filters: false);
                TestContext.Out.WriteLine("  verified exactly: " + exact.valid + " (" + exact.reason + "; pairs across " + exact.pairsAcross + ", far by exact boxes " + exact.pairsFarByExactBoxes
                    + ", judged exactly " + exact.pairsJudgedExactly + ", " + (exact.seconds * 1000.0).ToString("F1") + " ms)");
                Assert.That(exact.unfinished, Is.False, sample.objectName + ": the exact verification finished");
                Assert.That(exact.valid, Is.True, sample.objectName + ": the placement, exactly: " + exact.reason);
                Assert.That(exact.pairsFarByExactBoxes + exact.pairsJudgedExactly, Is.EqualTo(exact.pairsAcross), sample.objectName + ": every pair across the sides was settled exactly");
                Assert.That(reference.verdict, Is.EqualTo(ReferenceVerdict.Separable), sample.objectName + ": the reference agrees a placement exists");
            }
            else if (c.verdict == CandidateVerdict.NotSeparable)
            {
                Assert.That(reference.verdict, Is.EqualTo(ReferenceVerdict.NotSeparable), sample.objectName + ": the candidate said NotSeparable; the reference says " + reference.verdict);
            }

            return c;
        }

        private static void SameSide(CandidateResult c, string what, params (bool convex, int index)[] elements)
        {
            bool first = elements[0].convex ? c.convexPositive[elements[0].index] : c.trianglePositive[elements[0].index];
            foreach ((bool convex, int index) in elements)
            {
                Assert.That(convex ? c.convexPositive[index] : c.trianglePositive[index], Is.EqualTo(first), what + ": " + (convex ? "convex " : "triangle ") + index + " on the same side");
            }
        }

        // Triangles with the indices of equal positions shared (one mesh component where they touch at a vertex).
        private static (float3[] positions, int[] indices) SharedMesh(params float3[][] triangles)
        {
            var positions = new List<float3>();
            var indices = new List<int>();
            foreach (float3[] t in triangles)
            {
                foreach (float3 v in t)
                {
                    int at = positions.FindIndex(p => math.all(p == v));
                    if (at < 0) { at = positions.Count; positions.Add(v); }
                    indices.Add(at);
                }
            }

            return (positions.ToArray(), indices.ToArray());
        }

        private static SpatialSplitSample WithFarSide(string name, (float3, float3)[] boxes, params float3[][] triangles)
        {
            var all = new List<float3[]>(triangles) { FarTri };
            var allBoxes = new List<(float3, float3)>(boxes) { FarBox };
            (float3[] positions, int[] indices) = Mesh(all.ToArray());
            return Fixture(allBoxes.ToArray(), positions, indices, name: name);
        }

        private static float3[] At(float x) => Tri(x, 0.2f, 0.2f, x, 0.8f, 0.2f, x, 0.5f, 0.8f);

        private static readonly (float3, float3) Unit = Box(0f, 0f, 0f, 1f, 1f, 1f);

        // A U of three triangles sharing vertices (one mesh component) around the unit box, 0.5 away on three sides: the
        // planes x = -0.5, x = 1.5 and y = -0.5, reaching to y = 4 and z = 4. Its hull holds the box B2 = [0,1]x[2,3]x[0,1]
        // in part (and the triangle inside B2 wholly), while every triangle of the U is 0.5 or more from B2.
        private static readonly float3[] ULeft = { new float3(-0.5f, -0.5f, -1f), new float3(-0.5f, 4f, -1f), new float3(-0.5f, -0.5f, 4f) };
        private static readonly float3[] URight = { new float3(1.5f, -0.5f, -1f), new float3(1.5f, 4f, -1f), new float3(1.5f, -0.5f, 4f) };
        private static readonly float3[] UBottom = { new float3(-0.5f, -0.5f, -1f), new float3(1.5f, -0.5f, -1f), new float3(0.5f, -0.5f, 4f) };
        private static readonly (float3, float3) B2 = Box(0f, 2f, 0f, 1f, 3f, 1f);
        private static readonly float3[] TriInB2 = Tri(0.2f, 2.2f, 0.5f, 0.8f, 2.2f, 0.5f, 0.5f, 2.8f, 0.5f);

        private static SpatialSplitSample UFixture(string name)
        {
            (float3[] p, int[] i) = SharedMesh(ULeft, URight, UBottom, TriInB2);
            return Fixture(new[] { Unit, B2 }, p, i, name: name);
        }

        // ---- components -----------------------------------------------------------------------------------------------------

        [Test]
        public void MeshComponents_FollowTheSharedIndices_NotThePositions()
        {
            // Two triangles sharing an index: one component (with the far triangle, two). The same two with their own
            // indices: two components (three), joined by the geometry, never by the indices.
            float3[] a = Tri(0f, 0f, 2f, 1f, 0f, 2f, 0f, 1f, 2f);
            float3[] b = Tri(1f, 0f, 2f, 1f, 1f, 2f, 0f, 1f, 2f);
            var positions = new List<float3>(a) { b[1] };
            positions.AddRange(FarTri);
            SpatialSplitSample shared = Fixture(new[] { Unit, FarBox }, positions.ToArray(), new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6 }, name: "shared index");
            CandidateResult s = Candidate(shared);
            Assert.That(s.meshComponents, Is.EqualTo(2));
            Assert.That(s.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(s, "shared index", (false, 0), (false, 1));
            (float3[] p, int[] i) = Mesh(a, b, FarTri);
            CandidateResult c = Candidate(Fixture(new[] { Unit, FarBox }, p, i, name: "own indices"));
            Assert.That(c.meshComponents, Is.EqualTo(3));
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(c, "own indices", (false, 0), (false, 1));
        }

        [Test]
        public void TwoComponents_Crossing_AreNeverSeparated()
        {
            // T0 pierces T1 (different indices); the two boxes are apart from both. No placement exists; the candidate must
            // not find one, and with the leaves it confirms the contact and says NotSeparable (every triangle one component).
            float3[] t0 = Tri(2f, 0.5f, -1f, 2f, 0.5f, 2f, 2.2f, 0.5f, 2f);
            float3[] t1 = Tri(1.5f, 0f, 0.5f, 2.5f, 0f, 0.5f, 2f, 1f, 0.5f);
            (float3[] p, int[] i) = Mesh(t0, t1);
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(3f, 0f, 0f, 4f, 1f, 1f) }, p, i, name: "crossing");
            Assert.That(Candidate(sample, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotFound), "boxes alone cannot tell");
            Assert.That(Candidate(sample, CandidateStages.AB).verdict, Is.EqualTo(CandidateVerdict.NotFound), "hulls alone cannot tell");
            CandidateResult c = Candidate(sample);
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.NotSeparable), "the leaves confirm the contact; every triangle in one component");
            Assert.That(c.confirmedC, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public void TwoComponents_TouchingAtAPosition_AreJoined()
        {
            float3[] t0 = Tri(2f, 0f, 0f, 3f, 0f, 0f, 2f, 1f, 0f);
            float3[] t1 = Tri(3f, 0f, 0f, 4f, 0f, 0f, 4f, 1f, 0f);   // shares the position (3,0,0) with its own index
            CandidateResult c = Candidate(WithFarSide("touching", new[] { Unit }, t0, t1));
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(c, "touching", (false, 0), (false, 1));
        }

        [Test]
        public void TwoComponents_ApartButWithin2m_AreJoined()
        {
            float3[] t0 = Tri(2f, 0f, 0f, 3f, 0f, 0f, 2f, 1f, 0f);
            float3[] t1 = Tri(3f + M, 0f, 0f, 4f, 0f, 0f, 4f, 1f, 0f);   // m from t0's vertex: under 2m
            CandidateResult c = Candidate(WithFarSide("within 2m", new[] { Unit }, t0, t1));
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(c, "within 2m", (false, 0), (false, 1));
        }

        // ---- the 2m boundary -------------------------------------------------------------------------------------------------------

        [Test]
        public void TheBoundary_UnderAtAndOver2m()
        {
            CandidateResult under = Candidate(WithFarSide("under 2m", new[] { Unit }, At(1f + (2f * M) - Hair)));
            Assert.That(under.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(under, "under 2m", (true, 0), (false, 0));

            // Exactly 2m: neither apart nor joined can be certified in double; the pair is never put apart.
            CandidateResult at = Candidate(WithFarSide("at 2m", new[] { Unit }, At(1f + (2f * M))));
            Assert.That(at.verdict, Is.EqualTo(CandidateVerdict.NotFound).Or.EqualTo(CandidateVerdict.Separable), "at 2m: no false contact");
            if (at.verdict == CandidateVerdict.Separable) SameSide(at, "at 2m", (true, 0), (false, 0));

            CandidateResult over = Candidate(WithFarSide("over 2m", new[] { Unit }, At(1f + (2f * M) + Hair)));
            Assert.That(over.verdict, Is.EqualTo(CandidateVerdict.Separable));
        }

        [Test]
        public void ABoxAtExactly2m_IsNotPutApart_AndAHairOver_Is()
        {
            float3[] inside = Tri(0.2f, 0.2f, 0.5f, 0.8f, 0.2f, 0.5f, 0.5f, 0.8f, 0.5f);   // inside the unit box
            (float3[] p, int[] i) = Mesh(inside, FarTri);
            CandidateResult at = Candidate(Fixture(new[] { Unit, Box(1f + (2f * M), 0f, 0f, 2f, 1f, 1f), FarBox }, p, i, name: "boxes at 2m"));
            if (at.verdict == CandidateVerdict.Separable) SameSide(at, "boxes at 2m", (true, 0), (true, 1));
            CandidateResult over = Candidate(Fixture(new[] { Unit, Box(1f + (2f * M) + Hair, 0f, 0f, 2f, 1f, 1f), FarBox }, p, i, name: "boxes over 2m"));
            Assert.That(over.verdict, Is.EqualTo(CandidateVerdict.Separable));
        }

        // ---- the stages ----------------------------------------------------------------------------------------------------------

        [Test]
        public void ASlantedTriangle_OverABox_IsRefutedByTheHulls()
        {
            // The plane z = x through the unit box (joined) and over the box [2,3]x[0,1]x[0,1] (1/sqrt(2) apart): the boxes
            // overlap, so the boxes alone find nothing; the hulls refute the candidate.
            float3[] slanted = Tri(-1f, -1f, -1f, 5f, -1f, 5f, 5f, 5f, 5f);
            float3[] inB = Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f);
            (float3[] p, int[] i) = Mesh(slanted, inB);
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, name: "slanted");
            Assert.That(Candidate(sample, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotFound), "the boxes overlap");
            CandidateResult ab = Candidate(sample, CandidateStages.AB);
            Assert.That(ab.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(ab.stage, Is.EqualTo("B"));
            Assert.That(ab.refutedB, Is.GreaterThanOrEqualTo(1));
            Assert.That(ab.convexPositive[0], Is.Not.EqualTo(ab.convexPositive[1]));
        }

        [Test]
        public void AUShapedMesh_WhoseHullHoldsTheOtherSide_IsSeparatedOnlyWithTheLeaves()
        {
            SpatialSplitSample sample = UFixture("U shape");
            Assert.That(Candidate(sample, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotFound));
            Assert.That(Candidate(sample, CandidateStages.AB).verdict, Is.EqualTo(CandidateVerdict.NotFound), "the U's hull holds the other side's triangle");
            CandidateResult abc = Candidate(sample);
            Assert.That(abc.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(abc.stage, Is.EqualTo("C"));
            Assert.That(abc.refutedC, Is.GreaterThanOrEqualTo(1));
            Assert.That(abc.convexPositive[0], Is.Not.EqualTo(abc.convexPositive[1]), "the unit box and B2 on different sides");
        }

        [Test]
        public void ABoxInsideAHollowShell_IsSeparatedOnlyWithTheLeaves()
        {
            // The inner box with a triangle inside it; the closed shell (one component) touched from outside by the box B.
            var shell = new List<float3[]>(ClosedBoxMesh(new float3(0f), new float3(1f)));
            shell.Add(Tri(0.45f, 0.5f, 0.5f, 0.55f, 0.5f, 0.5f, 0.5f, 0.52f, 0.5f));
            (float3[] p, int[] i) = SharedMesh(shell.ToArray());
            SpatialSplitSample sample = Fixture(new[] { Box(0.4f, 0.45f, 0.45f, 0.6f, 0.55f, 0.55f), Box(1f, 0f, 0f, 2f, 1f, 1f) }, p, i, name: "nested");
            Assert.That(Candidate(sample, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotFound));
            Assert.That(Candidate(sample, CandidateStages.AB).verdict, Is.EqualTo(CandidateVerdict.NotFound), "the shell's hull holds the inner box");
            CandidateResult abc = Candidate(sample);
            Assert.That(abc.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(abc.stage, Is.EqualTo("C"));
            Assert.That(abc.refutedC, Is.GreaterThanOrEqualTo(1));
            Assert.That(abc.confirmedC, Is.GreaterThanOrEqualTo(1), "the triangle inside the inner box, or the shell's touch with B");
            Assert.That(abc.convexPositive[0], Is.Not.EqualTo(abc.convexPositive[1]), "the inner box and the outer box on different sides");
        }

        [Test]
        public void ADefiniteChain_BetweenTwoBoxes_IsNotSeparable()
        {
            // A strip of triangles sharing indices from the unit box (m/2 away) to the box at x = 4 + m/2 (touched), and a
            // loose triangle apart from everything (so that the triangles are not one component by the indices alone).
            var positions = new List<float3>();
            var indices = new List<int>();
            for (int k = 0; k <= 6; k++)
            {
                float x = 1f + (M / 2f) + (k * 0.5f);
                positions.Add(new float3(x, 0.2f, 0.5f));
                positions.Add(new float3(x, 0.8f, 0.5f));
                if (k > 0) indices.AddRange(new[] { (2 * k) - 2, (2 * k) - 1, 2 * k, (2 * k) - 1, (2 * k) + 1, 2 * k });
            }

            int loose = positions.Count;
            positions.AddRange(Tri(2f, 3f, 0f, 3f, 3f, 0f, 2.5f, 4f, 0f));
            indices.AddRange(new[] { loose, loose + 1, loose + 2 });
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(4f + (M / 2f), 0f, 0f, 5f, 1f, 1f) }, positions.ToArray(), indices.ToArray(), name: "chain");
            Assert.That(Candidate(sample, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotFound));
            Assert.That(Candidate(sample, CandidateStages.AB).verdict, Is.EqualTo(CandidateVerdict.NotFound), "the hulls confirm nothing");
            CandidateResult c = Candidate(sample);
            Assert.That(c.meshComponents, Is.EqualTo(2));
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.NotSeparable), "both contacts confirmed by the leaves: every convex one component");
            Assert.That(c.confirmedC, Is.EqualTo(2));

            // The strip alone: every triangle is one component by the indices, so the boxes alone settle it.
            SpatialSplitSample alone = Fixture(new[] { Unit, Box(4f + (M / 2f), 0f, 0f, 5f, 1f, 1f) }, positions.GetRange(0, loose).ToArray(), indices.GetRange(0, indices.Count - 3).ToArray(), name: "chain alone");
            Assert.That(Candidate(alone, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotSeparable));
        }

        [Test]
        public void SeveralCandidates_OnTheCut_AreAllRefuted()
        {
            // Three parallel slanted planes z = x + c through the unit box and over the box B (1.5 to 3.5 above its top),
            // a triangle inside B: the cut between {unit, the three} and {B, its triangle} holds six candidates, all
            // refuted by the hulls.
            var triangles = new List<float3[]>();
            foreach (float lift in new[] { -0.5f, 0f, 0.5f }) triangles.Add(Tri(-1f, -1f, -1f + lift, 5f, -1f, 5f + lift, 5f, 5f, 5f + lift));
            triangles.Add(Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f));
            (float3[] p, int[] i) = Mesh(triangles.ToArray());
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, name: "several");
            Assert.That(Candidate(sample, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.NotFound));
            CandidateResult c = Candidate(sample, CandidateStages.AB);
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(c.candidatesA, Is.GreaterThanOrEqualTo(6));
            Assert.That(c.refutedB, Is.GreaterThanOrEqualTo(6));
            Assert.That(c.cutAttempts, Is.LessThanOrEqualTo(8));
            SameSide(c, "the three with the unit box", (true, 0), (false, 0), (false, 1), (false, 2));
            Assert.That(c.convexPositive[0], Is.Not.EqualTo(c.convexPositive[1]));
        }

        // ---- degenerate, slivers, scale and translation ------------------------------------------------------------------------------

        [Test]
        public void Slivers_LargeTranslation_AndATinyScale_KeepTheBoundary()
        {
            CandidateResult sliverAt = Candidate(WithFarSide("sliver at 2m", new[] { Unit },
                Tri(1f + (2f * M), 0.5f, 0.1f, 1f + (2f * M), 0.5f + (1f / 1024f), 0.9f, 1f + (2f * M), 0.5f, 0.9f)));
            if (sliverAt.verdict == CandidateVerdict.Separable) SameSide(sliverAt, "sliver at 2m", (true, 0), (false, 0));
            CandidateResult sliverUnder = Candidate(WithFarSide("sliver under 2m", new[] { Unit },
                Tri(1f + (2f * M) - Hair, 0.5f, 0.1f, 1f + (2f * M) - Hair, 0.5f + (1f / 1024f), 0.9f, 1f + (2f * M) - Hair, 0.5f, 0.9f)));
            Assert.That(sliverUnder.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(sliverUnder, "sliver under", (true, 0), (false, 0));

            // A collinear (degenerate) triangle touching the box: joined through its edges.
            CandidateResult collinear = Candidate(WithFarSide("collinear", new[] { Unit }, Tri(1f, 0.5f, 0.5f, 1.5f, 0.5f, 0.5f, 2f, 0.5f, 0.5f)));
            Assert.That(collinear.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(collinear, "collinear", (true, 0), (false, 0));

            float4x4 moved = new float4x4(new float4(1f, 0f, 0f, 0f), new float4(0f, 1f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(4096f, -4096f, 4096f, 1f));
            (float3[] p1, int[] i1) = Mesh(At(1f + (2f * M)), FarTri);
            CandidateResult movedAt = Candidate(Fixture(new[] { Unit, FarBox }, p1, i1, geometryToOwner: moved, shapeToOwner: moved, name: "moved, at 2m"));
            if (movedAt.verdict == CandidateVerdict.Separable) SameSide(movedAt, "moved, at 2m", (true, 0), (false, 0));
            (float3[] p2, int[] i2) = Mesh(At(1f + (2f * M) - Hair), FarTri);
            CandidateResult movedUnder = Candidate(Fixture(new[] { Unit, FarBox }, p2, i2, geometryToOwner: moved, shapeToOwner: moved, name: "moved, under 2m"));
            Assert.That(movedUnder.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(movedUnder, "moved under", (true, 0), (false, 0));

            // The TL's tiny triangle (scale 1e-8, moved to (1,1,1)) touched at a vertex by a point element, margin 1e-10,
            // with a box apart from it, the far box and the far triangle (in the mesh's frame): the tiny pair stays together.
            float s = 1e-8f;
            float4x4 geometry = new float4x4(new float4(s, 0f, 0f, 0f), new float4(0f, s, 0f, 0f), new float4(0f, 0f, s, 0f), new float4(1f, 1f, 1f, 1f));
            var tiny = new List<float3> { new float3(-3f, 3f, -2f), new float3(2f, -2f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f) };
            foreach (float3 v in FarTri) tiny.Add((v - new float3(1f)) / s);
            CandidateResult tinyResult = Candidate(Fixture(new[] { Box(-1f, -1f, -1f, 0f, 0f, 0f), FarBox }, tiny.ToArray(), new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8 }, margin: 1e-10f, geometryToOwner: geometry, name: "tiny"));
            Assert.That(tinyResult.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(tinyResult, "tiny", (false, 0), (false, 1));
        }

        [Test]
        public void ATinyCoplanarTriangle_InsideAnother_IsNeverPutApart_TheTLsSecondCounterexample()
        {
            // L = 2^-24, margin 2^-35: the outer triangle (L,0,0),(0,L,0),(0,0,L) and the inner (L/2,L/4,L/4),(L/4,L/2,L/4),
            // (L/4,L/4,L/2) in the same plane, the inner wholly inside the outer. Their normals are too short to certify in
            // double; the candidate must keep them as triangles (no certificate of "apart" by their edges) and leave the
            // pair a candidate. The reference joins them. Two boxes apart from everything: no placement exists.
            const float L = 1f / (1 << 24);
            const float Margin = 1f / (1L << 35);
            float3[] outer = { new float3(L, 0f, 0f), new float3(0f, L, 0f), new float3(0f, 0f, L) };
            float3[] inner = { new float3(L / 2f, L / 4f, L / 4f), new float3(L / 4f, L / 2f, L / 4f), new float3(L / 4f, L / 4f, L / 2f) };
            (float3[] p, int[] i) = Mesh(outer, inner);
            SpatialSplitSample sample = Fixture(new[] { Box(-2f, -2f, -2f, -1f, -1f, -1f), Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, margin: Margin, name: "coplanar, tiny");
            ReferenceDecision reference = SpatialSplitReferenceDecider.Decide(sample, 120.0);
            Assert.That(reference.verdict, Is.EqualTo(ReferenceVerdict.NotSeparable), "the reference joins the two triangles: " + reference.reason);
            Assert.That(reference.componentCount, Is.EqualTo(3), "the two boxes and the joined pair");
            // The contract under test: two triangles that meet are never put apart. Confirming the contact (the inner vertex
            // is at distance 0 from the outer face, whose polygon is kept even though its normal is not certified) is the
            // better outcome: NotSeparable; leaving the pair ambiguous (NotFound) is also allowed. Refuting it is not.
            CandidateResult c = Candidate(sample);
            Assert.That(c.verdict, Is.Not.EqualTo(CandidateVerdict.Separable), "the pair is never put apart");
            Assert.That(c.refutedC, Is.Zero, "no leaf judgement refuted the pair");
            if (c.verdict == CandidateVerdict.NotSeparable) Assert.That(c.confirmedC, Is.GreaterThanOrEqualTo(1), "the contact was confirmed at the leaves");
            else
            {
                Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.NotFound));
                Assert.That(c.ambiguousC, Is.GreaterThanOrEqualTo(1), "the pair stayed ambiguous at the leaves");
                Assert.That(c.stop, Is.EqualTo("ambiguous"));
            }

            // With a triangle in each box, a placement exists (the pair on either side together); the candidate's, if any, keeps them together.
            (float3[] p2, int[] i2) = Mesh(outer, inner, Tri(-1.8f, -1.8f, -1.5f, -1.2f, -1.8f, -1.5f, -1.5f, -1.2f, -1.5f), Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f));
            CandidateResult placed = Candidate(Fixture(new[] { Box(-2f, -2f, -2f, -1f, -1f, -1f), Box(2f, 0f, 0f, 3f, 1f, 1f) }, p2, i2, margin: Margin, name: "coplanar, tiny, placeable"));
            Assert.That(placed.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(placed, "coplanar pair", (false, 0), (false, 1));
        }

        [Test]
        public void ATinyBox_WhoseFaceNormalsAreNotCertifiable_IsAValidInput_NotInvalid()
        {
            // A box of edge 2^-24 (TL review 3, 2026-10-22) with a tiny triangle on its top face, and a box with a triangle
            // inside it apart: a placement exists; the candidate must not call the input invalid, and keeps the touching pair
            // together. Margin 2^-35.
            const float L = 1f / (1 << 24);
            const float Margin = 1f / (1L << 35);
            float3[] onTop = { new float3(L / 4f, L / 4f, L), new float3(3f * L / 4f, L / 4f, L), new float3(L / 2f, 3f * L / 4f, L) };
            float3[] inB = Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f);
            (float3[] p, int[] i) = Mesh(onTop, inB);
            SpatialSplitSample sample = Fixture(new[] { Box(0f, 0f, 0f, L, L, L), Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, margin: Margin, name: "tiny box");
            CandidateResult c = Candidate(sample);
            Assert.That(c.verdict, Is.EqualTo(CandidateVerdict.Separable));
            SameSide(c, "tiny box and its triangle", (true, 0), (false, 0));
            Assert.That(c.convexPositive[0], Is.Not.EqualTo(c.convexPositive[1]));

            // The same tiny box against a tiny triangle just above it (apart by 4m): the boxes overlap, the hulls refute it.
            float3[] above = { new float3(L / 4f, L / 4f, L + (4f * Margin)), new float3(3f * L / 4f, L / 4f, L + (4f * Margin)), new float3(L / 2f, 3f * L / 4f, L + (4f * Margin)) };
            (float3[] p2, int[] i2) = Mesh(above, inB, onTop);
            CandidateResult apart = Candidate(Fixture(new[] { Box(0f, 0f, 0f, L, L, L), Box(2f, 0f, 0f, 3f, 1f, 1f) }, p2, i2, margin: Margin, name: "tiny box, triangle above"));
            Assert.That(apart.verdict, Is.Not.EqualTo(CandidateVerdict.InvalidInput));
            if (apart.verdict == CandidateVerdict.Separable) SameSide(apart, "tiny box and the triangle on it", (true, 0), (false, 2));
        }

        // ---- limits ---------------------------------------------------------------------------------------------------------------------

        [Test]
        public void TheLimits_NeverMakeAPlacement()
        {
            SpatialSplitSample sample = UFixture("U shape, limited");
            CandidateResult noCuts = Candidate(sample, CandidateStages.ABC, new CandidateSettings { maxCutAttempts = 0 });
            Assert.That(noCuts.verdict, Is.EqualTo(CandidateVerdict.NotFound));
            Assert.That(noCuts.stop, Is.EqualTo("cut-limit"));
            // The leaf budget: at most one leaf judgement a candidate; a refutation by the BVH's boxes alone is still a
            // certificate (the U's walls are axis-aligned: their boxes settle it), so the verdict may be Separable, verified.
            CandidateResult noLeaves = Candidate(sample, CandidateStages.ABC, new CandidateSettings { maxLeafTests = 0 });
            Assert.That(noLeaves.verdict, Is.Not.EqualTo(CandidateVerdict.NotSeparable));
            Assert.That(noLeaves.leafTests, Is.LessThanOrEqualTo(noLeaves.triedC));
            // The slanted triangle over a box: only a leaf judgement could refute it at stage C; none is allowed.
            (float3[] p, int[] i) = Mesh(Tri(-1f, -1f, -1f, 5f, -1f, 5f, 5f, 5f, 5f), Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f));
            SpatialSplitSample slanted = Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, name: "slanted, limited");
            CandidateResult noLeavesSlanted = Candidate(slanted, CandidateStages.ABC, new CandidateSettings { maxLeafTests = 0, gjkMaxIterations = 1 });
            Assert.That(noLeavesSlanted.verdict, Is.EqualTo(CandidateVerdict.NotFound).Or.EqualTo(CandidateVerdict.Separable));
            if (noLeavesSlanted.verdict == CandidateVerdict.Separable) Assert.That(noLeavesSlanted.stage, Is.EqualTo("B"), "found by the hulls, not by a leaf judgement that was not allowed");
            else
            {
                Assert.That(noLeavesSlanted.stop, Is.EqualTo("leaf-limit"), "a limit, told apart from an ambiguity");
                Assert.That(noLeavesSlanted.leafTests, Is.Zero, "the budget is checked before the judgement");
                Assert.That(noLeavesSlanted.limitC, Is.GreaterThanOrEqualTo(1));
            }
            CandidateResult oneIteration = Candidate(sample, CandidateStages.AB, new CandidateSettings { gjkMaxIterations = 1 });
            Assert.That(oneIteration.verdict, Is.EqualTo(CandidateVerdict.NotFound));
            CandidateResult noTime = Candidate(sample, CandidateStages.ABC, new CandidateSettings { secondsLimit = 1e-9 });
            Assert.That(noTime.verdict, Is.EqualTo(CandidateVerdict.Undecided));
            Assert.That(noTime.stop, Is.EqualTo("time"));
            Assert.That(noTime.timedOut, Is.True);
            Assert.That(noTime.convexPositive, Is.Null);

            // The deadline passing WHILE the conclusion is computed (after its entry check): a fixture that deals at stage A,
            // a hold inside the conclusion longer than the limit. The placement is computed, then discarded at the return.
            float3[] t0 = Tri(2f, 0f, 0f, 3f, 0f, 0f, 2f, 1f, 0f);
            SpatialSplitSample dealing = WithFarSide("deals at A, late", new[] { Unit }, t0);
            Assert.That(Candidate(dealing, CandidateStages.A).verdict, Is.EqualTo(CandidateVerdict.Separable), "without the hold it deals at A");
            CandidateResult late = Candidate(dealing, CandidateStages.A, new CandidateSettings { secondsLimit = 0.05, testHoldSecondsInConclude = 0.2 });
            Assert.That(late.verdict, Is.EqualTo(CandidateVerdict.Undecided), "a conclusion computed after the deadline is never returned");
            Assert.That(late.stop, Is.EqualTo("time"));
            Assert.That(late.timedOut, Is.True);
            Assert.That(late.convexPositive, Is.Null, "the placement is discarded");
            Assert.That(late.trianglePositive, Is.Null);
            Assert.That(late.seconds, Is.GreaterThan(0.05));
        }

        // ---- against the reference on every fixture -------------------------------------------------------------------------------------

        [Test]
        public void EveryReferenceFixture_IsPlacedValidly_OrLeftAlone()
        {
            int fixtures = 0, separable = 0, notFound = 0;
            foreach (SpatialSplitSample sample in CrossCheckFixtures())
            {
                fixtures++;
                CandidateResult c = Candidate(sample);   // verifies a placement, and NotSeparable, against the reference
                if (c.verdict == CandidateVerdict.Separable) separable++;
                if (c.verdict == CandidateVerdict.NotFound) notFound++;
                Assert.That(c.verdict, Is.Not.EqualTo(CandidateVerdict.Undecided), sample.objectName);
            }

            TestContext.Out.WriteLine(fixtures + " fixtures: " + separable + " placed, " + notFound + " not found");
            Assert.That(fixtures, Is.GreaterThanOrEqualTo(15));
        }
    }
}
