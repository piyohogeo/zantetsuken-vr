using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// NPC-BONE-SPLIT-1 / NPC-BONE-HELD-1: the bone-aware decision over hand-made lineages, the part of the rig each piece keeps
    /// (<see cref="NpcBonePiece"/>, made from the joints and the cut planes as the records make it) and the bones it holds by a convex
    /// or a kept joint -- the three connections (a convex
    /// and a convex of one cluster, a convex and a mesh component weighted to its cluster's bones, two mesh components sharing a
    /// cluster), a mesh component weighted to two clusters joining them, groups of a mesh or convexes alone riding with the others,
    /// everything a decision refuses to guess, the walk from cut vertices back to the base, the bones and segments a plane leaves on
    /// each side, and the records following a commit, a split and a forgotten fragment.
    /// </summary>
    public unsafe class NpcBoneSplitSearchTests
    {
        private const uint BaseStart = 100;
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<NpcBoneLineage> _lineages = new List<NpcBoneLineage>();

        [TearDown]
        public void TearDown()
        {
            foreach (NpcBoneLineage lineage in _lineages)
            {
                while (!lineage.IsReleased) lineage.Release();
            }

            _lineages.Clear();
            for (int i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
            _disposables.Clear();
        }

        private sealed class Disposer : IDisposable
        {
            internal Action action;
            public void Dispose() => action();
        }

        /// <summary>A vertex's bones (palette numbers), as the model's table keeps them.</summary>
        private static ulong W(params int[] bones)
        {
            var b = new ushort[] { NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None };
            for (int i = 0; i < bones.Length; i++) b[i] = (ushort)bones[i];
            return NpcVertexBones.Pack(b[0], b[1], b[2], b[3]);
        }

        /// <summary>A lineage of <paramref name="parent"/>'s bones (all present unless said), the base vertices weighted by <paramref name="weights"/>.</summary>
        private NpcBoneLineage Lineage(int[] parent, ulong[] weights, float3[] joints = null, bool[] present = null)
        {
            NpcVertexBones table = NpcVertexBones.FromEntries(weights, 16);
            _disposables.Add(new Disposer { action = () => { while (!table.IsReleased) table.Release(); } });
            present ??= Present(parent.Length);
            joints ??= new float3[parent.Length];
            var lineage = new NpcBoneLineage(parent.Length, parent, present, joints, table, BaseStart, weights.Length, null);
            lineage.Hold();
            table.Release();   // the lineage's hold is the one left
            _lineages.Add(lineage);
            return lineage;
        }

        private static bool[] Present(int n)
        {
            var present = new bool[n];
            for (int i = 0; i < n; i++) present[i] = true;
            return present;
        }

        /// <summary>The lineage's whole rig, and a piece of it cut by a plane (the side), as the records cut them.</summary>
        private static NpcBonePiece Whole(NpcBoneLineage lineage) => NpcBonePiece.Whole(lineage.Parent, lineage.Present);

        private static NpcBonePiece CutBy(NpcBoneLineage lineage, NpcBonePiece piece, float4 plane, bool positive) =>
            piece.Cut(plane, positive, lineage.Parent, lineage.Present, lineage.Joints);

        /// <summary>Triangles of base vertices (lineage numbers), as global numbers.</summary>
        private static uint[] Triangles(params int[] corners)
        {
            var indices = new uint[corners.Length];
            for (int i = 0; i < corners.Length; i++) indices[i] = BaseStart + (uint)corners[i];
            return indices;
        }

        private NpcBoneSplitRecord Decide(NpcBoneLineage lineage, NpcBonePiece piece, int[] convexBones, uint[] indices, out byte[] sides, string insufficient = null, byte[] boneSides = null)
        {
            var native = new NativeArray<uint>(indices, Allocator.Temp);
            int c = convexBones.Length, t = indices.Length / 3;
            var bytes = new NativeArray<byte>(c + t, Allocator.Temp);
            try
            {
                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(native), indexCount = indices.Length,
                    convexCount = c, convexBones = convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = lineage.NodeCount, parent = lineage.Parent, present = lineage.Present, baseBones = lineage.Vertices.Pointer,
                        baseStart = lineage.BaseStart, baseCount = lineage.BaseCount, chunks = lineage.Chunks, localCount = lineage.LocalCount,
                    },
                    jointKept = piece.JointKept, segmentLo = piece.Lo, segmentHi = piece.Hi, boneSides = boneSides,
                    insufficient = insufficient, maxTriangles = 131072, maxOriginSteps = NpcBoneSplitSearch.DefaultMaxOriginSteps,
                };
                var record = default(NpcBoneSplitRecord);
                NpcBoneSplitSearch.Decide(in input, (byte*)Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.GetUnsafePtr(bytes), bytes.Length, ref record);
                sides = bytes.ToArray();
                TestContext.Out.WriteLine(record.verdict + " (" + record.reason + "): bones held " + record.pieceBones + ", clusters " + record.clusters + ", components " + record.meshComponents
                    + ", groups " + record.groups + " (" + record.fullGroups + " full, " + record.unboundGroups + " mesh alone), weights on bones not held " + record.removedWeights
                    + ", sides " + string.Join("", sides));
                return record;
            }
            finally
            {
                native.Dispose();
                bytes.Dispose();
            }
        }

        /// <summary>Every convex and every triangle on exactly one side, as counted.</summary>
        private static void AssertWhole(in NpcBoneSplitRecord record, byte[] sides, int convexes, int triangles)
        {
            Assert.That(sides.Length, Is.EqualTo(convexes + triangles));
            Assert.That(record.positiveConvexes + record.negativeConvexes, Is.EqualTo(convexes), "no convex lost or counted twice");
            Assert.That(record.positiveTriangles + record.negativeTriangles, Is.EqualTo(triangles), "no triangle lost or counted twice");
            int up = 0;
            for (int i = convexes; i < sides.Length; i++) up += sides[i];
            Assert.That(up, Is.EqualTo(record.positiveTriangles));
        }

        // A bent rig: 0 the waist (y 0), 1 a shoulder above it (y 2), 2 a hand hanging from it back below (y 0), 3 a joint above the
        // waist with no segment of its own (y 1.5). The plane y = 1 leaves below it the waist (its joint and the lower part of its
        // segment to the shoulder) and, apart, the lower part of the shoulder's segment to the hand and the hand: two clusters.
        private static readonly int[] U = { -1, 0, 1, 0 };
        private static readonly float3[] UJoints = { new float3(0f, 0f, 0f), new float3(0f, 2f, 0f), new float3(0.5f, 0f, 0f), new float3(0f, 1.5f, 0f) };
        private static readonly float4 Waist = new float4(0f, 1f, 0f, -1f);

        private NpcBoneLineage ULineage(ulong[] weights) => Lineage(U, weights, UJoints);

        private static NpcBonePiece Below(NpcBoneLineage lineage) => CutBy(lineage, Whole(lineage), Waist, false);

        // ----- the three connections -------------------------------------------------------------------------------------

        /// <summary>
        /// **Convex and mesh.** A convex and a mesh component weighted to its cluster's bones are one group; two clusters apart
        /// are two groups, placed on two sides (the one of more triangles positive) -- and joined again where the rig holds together.
        /// </summary>
        [Test]
        public void AConvexAndAMeshOfItsCluster_AreOneGroup_TwoClustersApart_AreTwoSides()
        {
            // A (vertices 0-5, two triangles) weighted to the waist, B (6-8) to the hand
            NpcBoneLineage lineage = ULineage(new[] { W(0), W(0), W(0), W(0), W(0), W(0), W(2), W(2), W(2) });
            uint[] triangles = Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8);
            NpcBoneSplitRecord apart = Decide(lineage, Below(lineage), new[] { 0, 2 }, triangles, out byte[] sides);
            Assert.That(apart.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(apart.clusters, Is.EqualTo(2), "the waist | the shoulder's lower part and the hand");
            Assert.That(apart.meshComponents, Is.EqualTo(3), "A's two triangles share no vertex here: two components of one cluster");
            Assert.That(sides, Is.EqualTo(new byte[] { 1, 0, 1, 1, 0 }), "convex 0 and A positive (more triangles), convex 2 and B negative");
            AssertWhole(apart, sides, 2, 3);

            NpcBoneSplitRecord joined = Decide(lineage, Whole(lineage), new[] { 0, 2 }, triangles, out _);
            Assert.That(joined.verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable), "the whole rig: one cluster");
            Assert.That(joined.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonOneGroup));
        }

        /// <summary>
        /// **Convex and convex.** Two convexes of one cluster go together (below the waist: the forearm's convex, which holds its bone
        /// though the elbow is above, and the hand's, connected by the forearm's segment part); a convex of a bone none of whose
        /// connections is left (the upper arm's) is a group of its own and rides with the others.
        /// </summary>
        [Test]
        public void TwoConvexesOfOneCluster_GoTogether()
        {
            NpcBoneLineage lineage = Lineage(Body, new[] { W(0), W(0), W(0), W(0), W(0), W(0), W(4), W(4), W(4) }, BodyJoints);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), BodyWaist, false);
            uint[] triangles = Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8);
            NpcBoneSplitRecord kept = Decide(lineage, below, new[] { 0, 3, 4 }, triangles, out byte[] sides);
            Assert.That(kept.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(sides[1], Is.EqualTo(sides[2]), "the forearm's convex with the hand's");
            Assert.That(sides[1], Is.Not.EqualTo(sides[0]));
            AssertWhole(kept, sides, 3, 3);

            // the upper arm's convex: its bone held by it, but its segments have no part below -- no connection, a group of its own
            NpcBoneSplitRecord alone = Decide(lineage, below, new[] { 0, 3, 4, 2 }, triangles, out sides);
            Assert.That(alone.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(alone.groups == 3 && alone.fullGroups == 2, Is.True, "the upper arm's convex alone, with no triangle");
            Assert.That(sides[3], Is.EqualTo(0), "it stays with the others (negative), not dropped");
            AssertWhole(alone, sides, 4, 3);
        }

        /// <summary>
        /// **Mesh and mesh.** Two mesh components weighted to the bones of one cluster are one group, through a bone the piece holds
        /// by its joint with no convex (the finger); a component weighted only to a bone the piece does not hold (the spine) is a group
        /// of mesh alone.
        /// </summary>
        [Test]
        public void TwoMeshComponentsOfOneCluster_AreOneGroup_ThroughABoneHeldByItsJoint()
        {
            // A to the pelvis, B to the hand, D to the finger alone
            NpcBoneLineage lineage = Lineage(Body, new[] { W(0), W(0), W(0), W(0), W(0), W(0), W(4), W(4), W(4), W(5), W(5), W(5) }, BodyJoints);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), BodyWaist, false);
            uint[] triangles = Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            NpcBoneSplitRecord shared = Decide(lineage, below, new[] { 0, 4 }, triangles, out byte[] sides);
            Assert.That(shared.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(sides[2 + 3], Is.EqualTo(sides[2 + 2]), "D (the finger: its joint kept, no convex) with B (the hand)");
            Assert.That(shared.groups == 2 && shared.removedWeights == 0, Is.True);
            AssertWhole(shared, sides, 2, 4);

            // D weighted to the spine, which the piece does not hold (no convex, its joint above): a mesh alone, nothing dropped
            NpcBoneLineage away = Lineage(Body, new[] { W(0), W(0), W(0), W(0), W(0), W(0), W(4), W(4), W(4), W(1), W(1), W(1) }, BodyJoints);
            NpcBoneSplitRecord alone = Decide(away, CutBy(away, Whole(away), BodyWaist, false), new[] { 0, 4 }, triangles, out sides);
            Assert.That(alone.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable), "a known bone not held is not a lack of knowledge");
            Assert.That(alone.groups == 3 && alone.fullGroups == 2 && alone.unboundGroups == 1 && alone.removedWeights == 3, Is.True);
            Assert.That(sides[2 + 3], Is.EqualTo(0));
            AssertWhole(alone, sides, 2, 4);
        }

        /// <summary>A mesh component weighted to two clusters connects them: one component is never divided.</summary>
        [Test]
        public void AMeshComponentWeightedToTwoClusters_ConnectsThem()
        {
            // A's one triangle: vertex 0 to the waist, vertex 1 to the hand (a weight greater than zero is a weight, however small)
            NpcBoneLineage lineage = ULineage(new[] { W(0), W(2), W(0), W(2), W(2), W(2) });
            NpcBoneSplitRecord record = Decide(lineage, Below(lineage), new[] { 0, 2 }, Triangles(0, 1, 2, 3, 4, 5), out _);
            Assert.That(record.verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable));
            Assert.That(record.clusters, Is.EqualTo(2), "two clusters below the plane");
            Assert.That(record.groups, Is.EqualTo(1), "joined by the component weighted to both");
        }

        /// <summary>Two groups that hold triangles, but one of them no convex: no two owners can be made, nothing is dropped.</summary>
        [Test]
        public void FewerThanTwoGroupsWithAConvexAndTriangles_AreNotSplit()
        {
            NpcBoneLineage lineage = ULineage(new[] { W(0), W(0), W(0), W(2), W(2), W(2) });
            NpcBoneSplitRecord record = Decide(lineage, Below(lineage), new[] { 0 }, Triangles(0, 1, 2, 3, 4, 5), out _);
            Assert.That(record.verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable));
            Assert.That(record.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonFewFull));
        }

        // ----- what is not known ----------------------------------------------------------------------------------------

        /// <summary>A convex without its bone, a vertex of no known origin, triangles weighted to nothing, a weight outside the rig, an undecidable lineage.</summary>
        [Test]
        public void WhatIsNotKnown_IsRefused_NeverGuessed()
        {
            NpcBoneLineage lineage = Lineage(new[] { -1, 0, 0 }, new[] { W(0), W(0), W(0), W(1), W(1), W(1), W(), W(), W(), W(2), W(2), W(2) },
                present: new[] { true, true, false });
            NpcBonePiece whole = Whole(lineage);
            uint[] two = Triangles(0, 1, 2, 3, 4, 5);
            Assert.That(Decide(lineage, whole, new[] { 0, -1 }, two, out _).reason, Is.EqualTo(NpcBoneSplitSearch.ReasonConvex), "a convex of no bone");
            Assert.That(Decide(lineage, whole, new[] { 0, 2 }, two, out _).reason, Is.EqualTo(NpcBoneSplitSearch.ReasonConvex), "a convex of a bone outside the rig");

            var outside = new uint[] { BaseStart, BaseStart + 1, BaseStart + 2, BaseStart + 3, BaseStart + 4, BaseStart + 50 };
            Assert.That(Decide(lineage, whole, new[] { 0, 1 }, outside, out _).reason, Is.EqualTo(NpcBoneSplitSearch.ReasonVertex), "a vertex of no known origin");

            NpcBoneSplitRecord unweighted = Decide(lineage, whole, new[] { 0, 1 }, Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8), out _);
            Assert.That(unweighted.verdict, Is.EqualTo(NpcBoneSplitVerdict.Insufficient));
            Assert.That(unweighted.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonUnweighted), "a component weighted to no bone is not dropped");

            Assert.That(Decide(lineage, whole, new[] { 0, 1 }, Triangles(0, 1, 2, 3, 4, 5, 9, 10, 11), out _).reason,
                Is.EqualTo(NpcBoneSplitSearch.ReasonWeight), "a weight on a bone outside the rig");

            NpcBoneSplitRecord rig = Decide(lineage, whole, new[] { 0, 1 }, two, out _, insufficient: "no bone rig");
            Assert.That(rig.verdict, Is.EqualTo(NpcBoneSplitVerdict.Insufficient));
            Assert.That(rig.reason, Does.StartWith(NpcBoneSplitSearch.ReasonRig));
        }

        // ----- cut vertices ---------------------------------------------------------------------------------------------

        private static NativeArray<uint2> Origins(params uint2[] pairs) => new NativeArray<uint2>(pairs, Allocator.Persistent);

        private static uint2 Between(int a, int b) => new uint2(BaseStart + (uint)a, BaseStart + (uint)b);

        private static readonly uint2 Aux = new uint2(MeshCutOutput.NoVertexOrigin, MeshCutOutput.NoVertexOrigin);

        /// <summary>
        /// A cut's new vertex is weighted as the vertices it interpolates: strictly between a vertex of the waist and one of the hand,
        /// it joins the two clusters; at a parameter of 0 it is its one vertex's; a cap's auxiliary vertex is weighted to nothing of
        /// its own, so triangles of auxiliary vertices alone are not known. A second cut's vertex walks back through the first's.
        /// </summary>
        [Test]
        public void ACutsNewVertices_AreWeightedAsWhatTheyInterpolate_WalkedBackToTheBase()
        {
            // base: A (0-2) weighted to the waist, B (3-5) to the hand; convexes of both; the piece below the plane (two clusters)
            ulong[] weights = { W(0), W(0), W(0), W(2), W(2), W(2) };
            NpcBoneLineage between = ULineage(weights);
            const uint first = 1000;
            between.AddChunk(Origins(Between(0, 3), Aux, Aux), first, 3);   // one vertex between A and B, two auxiliary
            var withNew = new List<uint>(Triangles(0, 1, 2, 3, 4, 5)) { first, first + 1, first + 2 };
            Assert.That(Decide(between, Below(between), new[] { 0, 2 }, withNew.ToArray(), out _).verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable),
                "strictly between: weighted to both, a component of its own joining them");

            NpcBoneLineage atOne = ULineage(weights);
            atOne.AddChunk(Origins(Between(0, 0), Aux, Aux), first, 3);   // at a parameter of 0: the one vertex
            NpcBoneSplitRecord one = Decide(atOne, Below(atOne), new[] { 0, 2 }, withNew.ToArray(), out byte[] sides);
            Assert.That(one.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(sides[2 + 2], Is.EqualTo(sides[2 + 0]), "the cap triangle goes with A, whose vertex it lies on");

            NpcBoneLineage auxOnly = ULineage(weights);
            auxOnly.AddChunk(Origins(Aux, Aux, Aux), first, 3);
            Assert.That(Decide(auxOnly, Below(auxOnly), new[] { 0, 2 }, withNew.ToArray(), out _).reason, Is.EqualTo(NpcBoneSplitSearch.ReasonUnweighted));

            // a second cut through the first's vertices: walked back two steps to the base
            NpcBoneLineage twice = ULineage(weights);
            twice.AddChunk(Origins(Between(3, 4), Between(4, 5), Aux), first, 3);   // the first cut's vertices, of B
            const uint second = 2000;
            twice.AddChunk(Origins(new uint2(first, first + 1), new uint2(first + 1, first + 1), Aux), second, 3);
            var twiceIndices = new List<uint>(Triangles(0, 1, 2, 3, 4, 5)) { second, second + 1, second + 2 };
            NpcBoneSplitRecord walked = Decide(twice, Below(twice), new[] { 0, 2 }, twiceIndices.ToArray(), out sides);
            Assert.That(walked.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(sides[2 + 2], Is.EqualTo(sides[2 + 1]), "the second cut's triangle goes with B, through the first cut's vertices");
            Assert.That(walked.originSteps, Is.GreaterThan(walked.referencedVertices), "the walk went past the referenced vertices themselves");
        }

        // ----- the part of the rig a piece holds (NPC-BONE-ACTIVE-2) ----------------------------------------------------

        // A body: 0 pelvis (y 1.0), 1 spine (y 1.3), 2 upper arm from the shoulder (x 0.2, y 1.4), 3 forearm from the elbow
        // (x 0.25, y 1.1), 4 hand from the wrist (x 0.25, y 0.8), 5 a finger (x 0.25, y 0.7). The waist plane y = 1.05 leaves below
        // the pelvis, and apart from it the lower part of the forearm, the hand and the finger: the spine and the upper arm are
        // wholly above.
        private static readonly int[] Body = { -1, 0, 1, 2, 3, 4 };
        private static readonly float3[] BodyJoints =
        {
            new float3(0f, 1.0f, 0f), new float3(0f, 1.3f, 0f), new float3(0.2f, 1.4f, 0f), new float3(0.25f, 1.1f, 0f), new float3(0.25f, 0.8f, 0f), new float3(0.25f, 0.7f, 0f),
        };
        private static readonly float4 BodyWaist = new float4(0f, 1f, 0f, -1.05f);

        /// <summary>
        /// **The waist and the hand are not joined through the trunk and arm bones the cut left on the other side.** Below the waist the
        /// piece holds the pelvis (its joint and convex) and the hand and finger (their joints); the spine, the upper arm and the forearm
        /// (no convex here, joints above) are not held. The waist's skin is weighted to the pelvis and the spine, the hand's to the forearm
        /// and the hand: the spine's and the forearm's weights are left out, and the waist and the hand are two groups.
        /// </summary>
        [Test]
        public void AWaistAndAHand_AreNotJoinedThroughTheTrunkAndArmBonesLeftAbove()
        {
            ulong[] weights = { W(0, 1), W(0), W(0), W(0), W(0), W(0), W(3, 4), W(4), W(3, 4) };
            NpcBoneLineage lineage = Lineage(Body, weights, BodyJoints);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), BodyWaist, false);
            Assert.That(below.IsJointKept(3), Is.False, "the elbow is above");
            var boneSides = new byte[12];
            NpcBoneSplitRecord record = Decide(lineage, below, new[] { 0, 4 }, Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8), out byte[] sides, boneSides: boneSides);
            Assert.That(record.clusters, Is.EqualTo(2), "the pelvis | the hand and the finger");
            Assert.That(record.pieceBones, Is.EqualTo(3));
            Assert.That(record.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(record.removedWeights, Is.EqualTo(3), "the spine's weight on vertex 0, the forearm's on vertices 6 and 8");
            Assert.That(sides, Is.EqualTo(new byte[] { 1, 0, 1, 1, 0 }), "the pelvis and the waist positive, the hand and its skin negative");
            Assert.That(boneSides, Is.EqualTo(new byte[] { 1, 2, 2, 2, 0, 0, 2, 2, 2, 2, 2, 0 }),
                "each held bone on its group's side, each used connection (the hand to the finger) likewise; the rest neither");
            AssertWhole(record, sides, 2, 3);
        }

        /// <summary>
        /// **A convex holds its bone though the bone's joint is on the other side (NPC-BONE-HELD-1).** Below the waist the forearm's convex
        /// is left (the elbow above): the forearm is held, its connection to the wrist (its segment's part below, both ends held) is used,
        /// and the forearm's skin joins the hand's through it. The pelvis -- an ancestor of the arm, held below by its own joint and convex --
        /// is not dropped for it. Without the forearm's convex the forearm is not held and its skin's weights are left out.
        /// </summary>
        [Test]
        public void AConvexHoldsItsBone_WhoseJointIsAcross_AndItsChildConnectionIsUsed()
        {
            // A (0-5) the waist's skin on the pelvis, F (6-8) the forearm's on the forearm, H (9-11) the hand's on the hand
            ulong[] weights = { W(0), W(0), W(0), W(0), W(0), W(0), W(3), W(3), W(3), W(4), W(4), W(4) };
            NpcBoneLineage lineage = Lineage(Body, weights, BodyJoints);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), BodyWaist, false);
            uint[] triangles = Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11);
            var boneSides = new byte[12];
            NpcBoneSplitRecord record = Decide(lineage, below, new[] { 0, 3 }, triangles, out byte[] sides, boneSides: boneSides);
            Assert.That(below.IsJointKept(3), Is.False, "the layout: the elbow above, and the holding does not write the joint's flag");
            Assert.That(record.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(record.pieceBones == 4 && record.clusters == 2 && record.removedWeights == 0, Is.True, "the pelvis | the forearm, the hand, the finger");
            Assert.That(sides[2 + 2], Is.EqualTo(sides[1]), "the forearm's skin with the forearm's convex");
            Assert.That(sides[2 + 3], Is.EqualTo(sides[1]), "the hand's skin with them, through the forearm's connection to the wrist");
            Assert.That(sides[0], Is.Not.EqualTo(sides[1]), "the pelvis's convex and the waist apart");
            Assert.That(boneSides[0], Is.EqualTo(sides[0]), "the pelvis held and placed with its convex: not dropped as an ancestor of the arm");
            Assert.That(boneSides[3] == sides[1] && boneSides[6 + 4] == sides[1] && boneSides[6 + 5] == sides[1], Is.True, "the forearm and its connections on the arm's side");
            AssertWhole(record, sides, 2, 4);

            NpcBoneSplitRecord without = Decide(lineage, below, new[] { 0, 4 }, triangles, out sides);
            Assert.That(without.removedWeights, Is.EqualTo(3), "the forearm neither has its joint nor a convex here: its skin's weights are left out");
            Assert.That(without.groups == 3 && without.unboundGroups == 1, Is.True, "the forearm's skin is a mesh alone");
            Assert.That(sides[2 + 2], Is.EqualTo(0));
            AssertWhole(without, sides, 2, 4);

            // a split keeps on each side what it used, and nothing comes back: in the arm's piece the pelvis is not held
            NpcBonePiece arm = below.Keep(boneSides, sides[1], lineage.Parent, lineage.Present), waist = below.Keep(boneSides, sides[0], lineage.Parent, lineage.Present);
            Assert.That(arm.IsJointKept(0) || waist.IsJointKept(4), Is.False);
            NpcBoneSplitRecord armAgain = Decide(lineage, arm, new[] { 3 }, Triangles(6, 7, 8, 9, 10, 11, 0, 1, 2), out _);
            Assert.That(armAgain.removedWeights, Is.EqualTo(3), "a triangle weighted to the pelvis connects nothing in the arm's piece");
            Assert.That(armAgain.clusters, Is.EqualTo(1));
            NpcBonePiece armRecut = CutBy(lineage, arm, new float4(0f, 1f, 0f, -10f), false);   // a re-cut every joint is on the kept side of
            Assert.That(armRecut.IsJointKept(0) || armRecut.Lo[1] <= armRecut.Hi[1], Is.False, "the pelvis's joint and its segment do not come back");
            NpcBoneSplitRecord waistAgain = Decide(lineage, waist, new[] { 0 }, Triangles(0, 1, 2, 3, 4, 5, 9, 10, 11), out _);
            Assert.That(waistAgain.removedWeights, Is.EqualTo(3), "the hand's skin connects nothing in the waist's piece");
        }

        /// <summary>Two convexes of one bone in a piece are not expected: not known, refused, never guessed.</summary>
        [Test]
        public void TwoConvexesOfOneBone_AreNotKnown()
        {
            NpcBoneLineage lineage = Lineage(Body, new[] { W(0), W(0), W(0), W(4), W(4), W(4) }, BodyJoints);
            NpcBoneSplitRecord record = Decide(lineage, CutBy(lineage, Whole(lineage), BodyWaist, false), new[] { 0, 4, 4 }, Triangles(0, 1, 2, 3, 4, 5), out _);
            Assert.That(record.verdict, Is.EqualTo(NpcBoneSplitVerdict.Insufficient));
            Assert.That(record.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonConvexesOfOneBone));
        }

        /// <summary>
        /// **A bone cut through keeps its connection's part on each side, and the two pieces are not joined across.** The plane through
        /// the forearm (y 0.95) leaves the elbow's part above and the wrist's below; the forearm's convex is cut too, a part on each side,
        /// so the forearm is held on both. Above it is with the trunk, below with the hand; each piece is decided by itself.
        /// </summary>
        [Test]
        public void ABoneCutThrough_IsHeldOnBothSides_AndNotJoinedAcross()
        {
            var plane = new float4(0f, 1f, 0f, -0.95f);
            // vertices 0-2: a triangle of the trunk (pelvis + spine), 3-5: the forearm's skin, 6-8: the hand's
            ulong[] weights = { W(0, 1), W(0), W(1), W(3), W(3), W(3), W(4), W(4), W(4) };
            NpcBoneLineage lineage = Lineage(Body, weights, BodyJoints);
            NpcBonePiece above = CutBy(lineage, Whole(lineage), plane, true);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), plane, false);
            Assert.That(above.Lo[4] == 0f && above.Hi[4] < 1f && below.Lo[4] > 0f && below.Hi[4] == 1f, Is.True, "the forearm's segment (elbow to wrist) shared at the plane");
            Assert.That(above.IsJointKept(3) && !below.IsJointKept(3) && below.IsJointKept(4) && !above.IsJointKept(4), Is.True);

            // above: the trunk and the forearm (its joint and convex part); the hand neither holds its joint nor a convex here
            NpcBoneSplitRecord up = Decide(lineage, above, new[] { 0, 3 }, Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8), out _);
            Assert.That(up.clusters, Is.EqualTo(1), "pelvis to forearm, one cluster above");
            Assert.That(up.groups == 2 && up.unboundGroups == 1 && up.removedWeights == 3, Is.True, "the trunk with the forearm's skin; the hand's skin alone");
            // below: the forearm (its convex part) with the hand and the finger; the trunk's skin weighted to bones not held below
            NpcBoneSplitRecord down = Decide(lineage, below, new[] { 3, 4 }, Triangles(0, 1, 2, 3, 4, 5, 6, 7, 8), out _);
            Assert.That(down.clusters, Is.EqualTo(1), "the forearm, the hand, the finger");
            Assert.That(down.groups == 2 && down.fullGroups == 1 && down.unboundGroups == 1 && down.removedWeights == 4, Is.True,
                "the forearm's and the hand's skin with their convexes; the trunk's skin alone, never with the forearm above");
        }

        /// <summary>
        /// **A re-cut cuts what is left, and nothing taken off comes back.** The piece below the waist is cut again by a plane every
        /// joint lies on the kept side of: the spine and the upper arm stay not held, the elbow not kept, the parts as they were; a
        /// plane through the forearm's part cuts that part further. The first piece is not written. A split keeps each side's bones.
        /// </summary>
        [Test]
        public void ARecut_CutsWhatIsLeft_AndBringsNothingBack()
        {
            NpcBoneLineage lineage = Lineage(Body, new[] { W(0) }, BodyJoints);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), BodyWaist, false);
            float[] lo = (float[])below.Lo.Clone(), hi = (float[])below.Hi.Clone();
            NpcBonePiece again = CutBy(lineage, below, new float4(0f, 1f, 0f, -10f), false);   // y <= 10: every joint on the kept side
            Assert.That(again.Present, Is.EqualTo(below.Present), "no bone comes back");
            Assert.That(again.IsJointKept(1) || again.IsJointKept(2) || again.IsJointKept(3), Is.False, "nor a joint");
            Assert.That(again.Lo, Is.EqualTo(lo));
            Assert.That(again.Hi, Is.EqualTo(hi));

            NpcBonePiece lower = CutBy(lineage, below, new float4(0f, 1f, 0f, -0.9f), false);   // y <= 0.9: through the forearm's part
            Assert.That(lower.Present[3] && lower.Lo[4] > below.Lo[4] && lower.Hi[4] == 1f, Is.True, "the forearm's part cut further");
            Assert.That(lower.Present[0], Is.False, "the pelvis (y 1.0) above this plane: no longer held");
            Assert.That(below.Lo, Is.EqualTo(lo), "the first piece is not written");

            // a split keeps on each side the joints and parts its decision put there, and takes off the rest
            var sides = new byte[] { 1, 2, 2, 2, 0, 0, 2, 1, 2, 2, 0, 0 };
            NpcBonePiece kept = below.Keep(sides, 1, lineage.Parent, lineage.Present), other = below.Keep(sides, 0, lineage.Parent, lineage.Present);
            Assert.That(kept.Present, Is.EqualTo(new[] { true, false, false, false, false, false }));
            Assert.That(other.Present, Is.EqualTo(new[] { false, false, false, true, true, true }));
            Assert.That(CutBy(lineage, kept, new float4(0f, 1f, 0f, -10f), false).Present, Is.EqualTo(kept.Present), "and a cut after it brings none back");
        }

        // A branching bone: 0 the pelvis (y 1.0) with 1 the left hip (x -0.2, y 0.6), 2 the right hip (x 0.2, y 0.6) and 3 the spine
        // (y 1.3). The plane y = 0.9 cuts the pelvis's joint away from below while both its connections to the hips cross it.
        private static readonly int[] Hips = { -1, 0, 0, 0 };
        private static readonly float3[] HipJoints = { new float3(0f, 1.0f, 0f), new float3(-0.2f, 0.6f, 0f), new float3(0.2f, 0.6f, 0f), new float3(0f, 1.3f, 0f) };
        private static readonly float4 BelowThePelvis = new float4(0f, 1f, 0f, -0.9f);

        /// <summary>
        /// **A branching bone whose joint is cut away keeps its branches apart, unless its convex is left.** Below the plane, without the
        /// pelvis's convex, the pelvis is not held: the two legs are two groups, and a weight on the pelvis is left out (not refused). With
        /// the pelvis's convex left below, the pelvis is held by it and the one convex joins the branches (a split does not divide a convex).
        /// Above, the pelvis's joint is kept. A split keeps on each side its own hip.
        /// </summary>
        [Test]
        public void ABranchingBoneWhoseJointIsCutAway_JoinsItsBranchesOnlyThroughItsConvex()
        {
            NpcBoneLineage lineage = Lineage(Hips, new[] { W(1), W(1), W(1), W(2), W(2), W(2) }, HipJoints);
            NpcBonePiece below = CutBy(lineage, Whole(lineage), BelowThePelvis, false);
            Assert.That(below.IsJointKept(0), Is.False, "the pelvis's joint is above");
            Assert.That(below.Lo[1] > 0f && below.Lo[2] > 0f && below.Hi[1] == 1f && below.Hi[2] == 1f, Is.True, "both connections to the hips cross the plane");

            var boneSides = new byte[8];
            NpcBoneSplitRecord legs = Decide(lineage, below, new[] { 1, 2 }, Triangles(0, 1, 2, 3, 4, 5), out byte[] sides, boneSides: boneSides);
            Assert.That(legs.clusters, Is.EqualTo(2), "the left hip | the right hip");
            Assert.That(legs.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable), "the branches are not joined through the pelvis");
            Assert.That(sides[0], Is.Not.EqualTo(sides[1]));
            AssertWhole(legs, sides, 2, 2);
            Assert.That(boneSides[0] == 2 && boneSides[4 + 1] == 2 && boneSides[4 + 2] == 2, Is.True, "the pelvis not held, its connections not used");
            NpcBonePiece left = below.Keep(boneSides, boneSides[1], lineage.Parent, lineage.Present);
            Assert.That(left.IsJointKept(1) && !left.IsJointKept(2), Is.True, "the split keeps each side's hip");

            // a weight on the pelvis, its convex not here: left out, not refused
            NpcBoneLineage weighted = Lineage(Hips, new[] { W(0, 1), W(1), W(1), W(2), W(2), W(2) }, HipJoints);
            NpcBoneSplitRecord byWeight = Decide(weighted, CutBy(weighted, Whole(weighted), BelowThePelvis, false), new[] { 1, 2 }, Triangles(0, 1, 2, 3, 4, 5), out _);
            Assert.That(byWeight.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(byWeight.removedWeights, Is.EqualTo(1));

            // the pelvis's convex left below: the pelvis held by it, the branches joined through that one convex
            NpcBoneSplitRecord joined = Decide(lineage, below, new[] { 0, 1, 2 }, Triangles(0, 1, 2, 3, 4, 5), out _);
            Assert.That(joined.verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable));
            Assert.That(joined.clusters, Is.EqualTo(1), "the pelvis with both hips");
            Assert.That(joined.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonOneGroup));

            // above, the pelvis's joint is kept and it joins the spine
            NpcBoneSplitRecord up = Decide(weighted, CutBy(weighted, Whole(weighted), BelowThePelvis, true), new[] { 0 }, Triangles(0, 1, 2, 3, 4, 5), out _);
            Assert.That(up.clusters, Is.EqualTo(1), "the pelvis and the spine");
            Assert.That(up.verdict, Is.Not.EqualTo(NpcBoneSplitVerdict.Insufficient));
        }

        /// <summary>A joint or a part exactly on the plane stays on both sides (each side is closed).</summary>
        [Test]
        public void WhatLiesOnThePlane_StaysOnBothSides()
        {
            NpcBoneLineage lineage = ULineage(new[] { W(0) });
            NpcBonePiece up = CutBy(lineage, Whole(lineage), new float4(0f, 0f, 1f, 0f), true), down = CutBy(lineage, Whole(lineage), new float4(0f, 0f, 1f, 0f), false);
            Assert.That(up.Present, Is.EqualTo(Whole(lineage).Present));
            Assert.That(down.Present, Is.EqualTo(Whole(lineage).Present));
            Assert.That(up.IsJointKept(1) && down.IsJointKept(1), Is.True);
        }

        // ----- the records follow the geometry ---------------------------------------------------------------------------

        /// <summary>
        /// A commit hands each side its part of the source's rig -- cut by the plane -- with the new vertices' origins kept, and the
        /// source's goes; a split hands each of its two the bones the decision put on its side (or, without the decision's sides, the
        /// source's part to both); a forgotten fragment's goes. The lineage is given back with its last record, and only an NPC's
        /// decidable lineage asks a cut for origins.
        /// </summary>
        [Test]
        public void TheRecords_FollowCommitsSplitsAndForgottenFragments()
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(16));
            var records = new NpcBoneSplitRegistry(ledger);
            NpcVertexBones table = NpcVertexBones.FromEntries(new[] { W(0), W(1) }, 4);
            var lineage = new NpcBoneLineage(2, new[] { -1, 0 }, Present(2), new[] { new float3(0, 1, 0), new float3(0, -1, 0) }, table, BaseStart, 2, null);
            table.Release();   // the lineage's hold is the one left
            LogicalFragmentId root = ledger.AddFragment(), positive = ledger.AddFragment(), negative = ledger.AddFragment(), other = ledger.AddFragment();
            records.RegisterRoot(root, lineage);
            Assert.That(records.WantsVertexOrigins(root) && !records.WantsVertexOrigins(other), Is.True, "an NPC's fragment only");
            Assert.That(records.TryGet(root, out NpcBoneFragment r0) && r0.Piece.PresentCount == 2, Is.True, "the root holds the whole rig");

            var commit = new CutGeometryCommit(default, root, positive, negative, new float4(0, 1, 0, 0), default, default, 1);
            records.Committed(in commit, new float4(0, 1, 0, 0), Origins(Between(0, 1), Aux), 500, 2);
            Assert.That(records.Knows(root), Is.False, "the source's record went");
            Assert.That(records.TryGet(positive, out NpcBoneFragment p), Is.True, "to both sides");
            Assert.That(records.TryGet(negative, out NpcBoneFragment n), Is.True);
            Assert.That(p.Piece.Present, Is.EqualTo(new[] { true, false }), "above: bone 0 (its joint, the upper half of its segment); bone 1 below");
            Assert.That(n.Piece.Present, Is.EqualTo(new[] { true, true }), "below: the lower half of bone 0's segment and bone 1");
            Assert.That(!n.Piece.IsJointKept(0) && n.Piece.IsJointKept(1), Is.True);
            Assert.That(lineage.Chunks.Length == 1 && lineage.Chunks[0].start == 500 && lineage.Chunks[0].count == 2 && lineage.LocalCount == 4, Is.True, "the origins kept");
            Assert.That(records.Commits == 1 && records.OriginsRecorded == 2 && records.CommitsWithoutOrigins == 0, Is.True);

            LogicalFragmentId a = ledger.AddFragment(), b = ledger.AddFragment();
            records.NoteSplitSides(negative, new byte[] { 2, 1, 2, 0 });   // bone 1's joint positive, bone 0's segment part negative
            records.Split(negative, a, b);
            Assert.That(records.Knows(negative), Is.False);
            Assert.That(records.TryGet(a, out NpcBoneFragment ra), Is.True);
            Assert.That(records.TryGet(b, out NpcBoneFragment rb), Is.True);
            Assert.That(ra.Piece.Present, Is.EqualTo(new[] { false, true }), "the positive side: the bone the decision put there");
            Assert.That(rb.Piece.Present, Is.EqualTo(new[] { true, false }), "the negative side: the other");
            Assert.That(ReferenceEquals(ra.Lineage, rb.Lineage), Is.True);

            LogicalFragmentId c = ledger.AddFragment(), d = ledger.AddFragment();
            records.Split(positive, c, d);
            Assert.That(records.TryGet(c, out NpcBoneFragment rc), Is.True);
            Assert.That(records.TryGet(d, out NpcBoneFragment rd), Is.True);
            Assert.That(ReferenceEquals(rc.Piece, rd.Piece) && ReferenceEquals(rc.Piece, p.Piece), Is.True, "without the decision's sides both keep the source's part");
            Assert.That(records.SplitsWithoutSides, Is.EqualTo(1));

            records.Forgotten(c);
            records.Forgotten(d);
            records.Forgotten(a);
            Assert.That(lineage.IsReleased, Is.False, "one record still holds the lineage");
            records.Forgotten(b);
            Assert.That(lineage.IsReleased && table.IsReleased, Is.True, "the last record gave the lineage and the model's table back");
            Assert.That(records.Count, Is.Zero);

            // A lineage that cannot be decided asks for no origins, and a commit of it records none and is not counted as missing them.
            var undecidable = new NpcBoneLineage(0, Array.Empty<int>(), Array.Empty<bool>(), Array.Empty<float3>(), null, BaseStart, 2, "no bone rig");
            LogicalFragmentId u = ledger.AddFragment(), u1 = ledger.AddFragment(), u2 = ledger.AddFragment();
            records.RegisterRoot(u, undecidable);
            Assert.That(records.WantsVertexOrigins(u), Is.False);
            records.Committed(new CutGeometryCommit(default, u, u1, u2, new float4(0, 1, 0, 0), default, default, 1), new float4(0, 1, 0, 0), default, 0, 0);
            Assert.That(records.Knows(u1) && records.Knows(u2) && records.CommitsWithoutOrigins == 0 && records.RootsInsufficient == 1, Is.True);
            records.Dispose();
            Assert.That(undecidable.IsReleased, Is.True, "the world's end gives every lineage back");
        }
    }
}
