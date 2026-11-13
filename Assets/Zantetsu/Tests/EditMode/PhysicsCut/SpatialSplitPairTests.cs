using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// NPC-PAIR-EVAL-1: the pairwise combination of the bones and the geometry over hand-made pieces -- the bone-aware decision's
    /// attributes of the nodes (<see cref="NpcBonePairAttributes"/>) handed to the graph engine (<see cref="SpatialSplitGraph.PairInput"/>):
    /// a pair only the bones leave out, a pair only the boxes leave out, a pair both keep; the bones first (no box is read for a
    /// pair they leave out) and nothing left out where they are not known or the hulls do not settle; a piece each method alone
    /// holds together and the pairs divide; components by topology, each on one side, each child's surface closed, nothing missing
    /// or twice; the main supporting mesh's protection; and the bones each child would keep.
    /// </summary>
    public unsafe class SpatialSplitPairTests
    {
        private const uint BaseStart = 100;
        private const float Margin = 1f / 4096f;
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
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(0), "every area of the engine was given back");
        }

        private sealed class Disposer : IDisposable
        {
            internal Action action;
            public void Dispose() => action();
        }

        private static ulong W(params int[] bones)
        {
            var b = new ushort[] { NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None };
            for (int i = 0; i < bones.Length; i++) b[i] = (ushort)bones[i];
            return NpcVertexBones.Pack(b[0], b[1], b[2], b[3]);
        }

        /// <summary>A piece: bones that are each a root (a cluster of its own), all held whole; the vertices' weights; the convexes' bones and boxes; triangles of lineage numbers.</summary>
        private sealed class Piece
        {
            internal int bones;
            internal ulong[] weights;
            internal float3[] positions;
            internal int[] convexBones;
            internal float3[][] convexes;
            internal int[] corners;
            internal int[] topologyOfLocal;
            internal int topologyCount;
        }

        private sealed class Outcome
        {
            internal NpcBoneSplitRecord bone;
            internal SpatialSplitGraph.GraphOutcome graph;
            internal SpatialSplitGraph.NativeRun run;
            internal SpatialSplitGraph.PairInput counts;
            internal int protectedPairsOfBones;
            internal byte[] boneSides;
            internal int ownMainPairs, ownProtectedAdded, ownUnderRatio;
        }

        private static float3[] Box(float x0, float x1) => SpatialSplitGraph.WarmupBox(new float3(x0, 0f, 0f), new float3(x1, 1f, 1f));

        /// <summary>
        /// mode 0: the geometry alone (no rows); 1: the pairs; 2: the pairs with the protection; 3: 2 with A (each component's own main
        /// bone protected too); 4: 2 with B (an isolated mesh node moved to its own main convexes' side). bonesKnown false: the rows are withheld.
        /// </summary>
        private Outcome Decide(Piece p, int mode, bool bonesKnown = true, int gjkMaxIterations = 64, int measure = 0, string insufficient = null, bool noTopology = false)
        {
            int n = p.bones;
            var parent = new int[n];
            var present = new bool[n];
            for (int i = 0; i < n; i++) { parent[i] = -1; present[i] = true; }
            NpcVertexBones table = NpcVertexBones.FromEntries(p.weights, 16);
            _disposables.Add(new Disposer { action = () => { while (!table.IsReleased) table.Release(); } });
            var lineage = new NpcBoneLineage(n, parent, present, new float3[n], table, BaseStart, p.weights.Length, null);
            lineage.Hold();
            table.Release();
            _lineages.Add(lineage);
            NpcBonePiece piece = NpcBonePiece.Whole(lineage.Parent, lineage.Present);

            var indices = new uint[p.corners.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = BaseStart + (uint)p.corners[i];
            int c = p.convexBones.Length, t = indices.Length / 3;
            var native = new NativeArray<uint>(indices, Allocator.Temp);
            var bytes = new NativeArray<byte>(c + t, Allocator.Temp);
            var attributes = new NpcBonePairAttributes();
            var o = new Outcome();
            try
            {
                var input = new NpcBoneSplitSearch.Input
                {
                    indices = (uint*)native.GetUnsafePtr(), indexCount = indices.Length, convexCount = c, convexBones = p.convexBones,
                    lineage = new NpcBoneSplitSearch.Lineage
                    {
                        nodeCount = lineage.NodeCount, parent = lineage.Parent, present = lineage.Present, baseBones = lineage.Vertices.Pointer,
                        baseStart = lineage.BaseStart, baseCount = lineage.BaseCount, chunks = lineage.Chunks, localCount = lineage.LocalCount,
                    },
                    jointKept = piece.JointKept, segmentLo = piece.Lo, segmentHi = piece.Hi,
                    maxTriangles = 131072, maxOriginSteps = NpcBoneSplitSearch.DefaultMaxOriginSteps, pairs = attributes, insufficient = insufficient,
                };
                using (NpcBoneTopology topology = NpcBoneTopology.OfLocal(in input.lineage, p.topologyOfLocal, p.topologyCount))
                {
                    if (!noTopology) input.topology = topology.Map;
                    NpcBoneSplitSearch.Decide(in input, (byte*)bytes.GetUnsafePtr(), bytes.Length, ref o.bone);
                    o.protectedPairsOfBones = attributes.protectedPairs;
                    o.boneSides = bytes.ToArray();

                    // the engine's compact input: the named vertices in ascending id order
                    var named = new SortedSet<int>(p.corners);
                    var local = new List<float3>();
                    var ids = new List<uint>();
                    foreach (int v in named) { local.Add(p.positions[v]); ids.Add(BaseStart + (uint)v); }
                    var compact = new SpatialSplitGraph.CompactInput
                    {
                        local = local.ToArray(), ids = ids.ToArray(), indices = indices, convexes = p.convexes, vertexViewLength = (int)BaseStart + p.positions.Length,
                        geometryToOwner = float4x4.identity, shapeToOwner = float4x4.identity, margin = Margin,
                    };
                    var settings = new SpatialSplitGraph.GraphSettings { secondsLimit = 0.0, measure = measure, flowDinic = true, gjkMaxIterations = gjkMaxIterations };
                    if (mode != 0)
                    {
                        o.counts = new SpatialSplitGraph.PairInput { useTopology = true, topology = noTopology ? default : topology.Map, protect = mode >= 2, relocate = mode == 4 };
                        o.ownMainPairs = attributes.ownMainPairs;
                        if (bonesKnown && mode == 3) attributes.ApplyOwnMain(out o.ownProtectedAdded, out o.ownUnderRatio);
                        if (bonesKnown)
                        {
                            Assert.That(attributes.filled, Is.True, "the bones' attributes were worked out");
                            o.counts.rows = attributes.Rows;
                            o.counts.nodes = attributes.Nodes;
                            o.counts.clusterWords = attributes.clusterWords;
                            o.counts.rowWords = attributes.rowWords;
                            o.counts.convexWords = attributes.convexWords;
                            o.counts.triComponent = attributes.TriComponent;
                        }

                        settings.pair = o.counts;
                    }

                    o.graph = SpatialSplitGraph.DecideCompact(compact, settings, true, out o.run);
                }

                TestContext.Out.WriteLine("mode " + mode + ": bones " + o.bone.verdict + " (" + o.bone.reason + "), engine " + o.graph.verdict + " (" + o.graph.reason + "), components " + o.graph.meshComponents
                    + ", candidates " + o.graph.candidatesA + (o.counts != null ? ", left out by the bones " + string.Join("/", o.counts.boneApart) + ", by the boxes " + string.Join("/", o.counts.boxApart)
                        + ", candidates " + string.Join("/", o.counts.candidates) + ", protected " + o.counts.protectedPairs + " (kept though apart " + o.counts.protectedKeptThoughApart + ", across " + o.counts.protectedAcross + ")" : ""));
                return o;
            }
            finally
            {
                attributes.Dispose();
                native.Dispose();
                bytes.Dispose();
            }
        }

        private static string Sides(SpatialSplitGraph.GraphOutcome g)
        {
            if (g.convexPositive == null) return "";
            var s = new System.Text.StringBuilder();
            foreach (bool b in g.convexPositive) s.Append(b ? '1' : '0');
            s.Append('|');
            foreach (bool b in g.trianglePositive) s.Append(b ? '1' : '0');
            return s.ToString();
        }

        // Two bones, a convex each (boxes that overlap at x 0.9..1), a triangle inside each box far from the other box.
        private static Piece TwoArms(ulong[] weights) => new Piece
        {
            bones = 2, weights = weights, convexBones = new[] { 0, 1 }, convexes = new[] { Box(0f, 1f), Box(0.9f, 2f) },
            positions = new[] { new float3(0.2f, 0.2f, 0.5f), new float3(0.4f, 0.2f, 0.5f), new float3(0.3f, 0.4f, 0.5f), new float3(1.5f, 0.2f, 0.5f), new float3(1.8f, 0.2f, 0.5f), new float3(1.6f, 0.4f, 0.5f) },
            corners = new[] { 0, 1, 2, 3, 4, 5 },
        };

        /// <summary>
        /// **The three kinds of pair.** Two convexes whose boxes overlap, of two clusters: left out by the bones alone. A convex and the
        /// other bone's mesh: left out by the bones (their boxes are apart too, and are never read). A convex and its own bone's mesh,
        /// inside its box: kept by both. What is left is two groups: a placement -- which neither the geometry alone (the convexes
        /// overlap) gives, nor is it the bones' alone to give without the geometry's nodes.
        /// </summary>
        [Test]
        public void APairTheBonesLeaveOut_IsNotRead_APairBothKeep_Stays_AndTheRestIsTwoGroups()
        {
            Piece p = TwoArms(new[] { W(0), W(0), W(0), W(1), W(1), W(1) });
            Outcome geometry = Decide(p, 0);
            Assert.That(geometry.graph.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "the geometry alone: the convexes overlap");

            Outcome pairs = Decide(p, 1, measure: 2);
            Assert.That(pairs.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(pairs.counts.pairsByKind, Is.EqualTo(new long[] { 1, 4, 1 }));
            Assert.That(pairs.counts.boneApart, Is.EqualTo(new long[] { 1, 2, 1 }), "the convexes of two clusters; each convex and the other's mesh; the two meshes");
            Assert.That(pairs.counts.boxApart, Is.EqualTo(new long[] { 0, 0, 0 }));
            Assert.That(pairs.counts.candidates, Is.EqualTo(new long[] { 0, 2, 0 }), "each convex and its own mesh");
            long gapEvals = pairs.run.prepareCounters[SpatialSplitGraph.PcGapEvals - SpatialSplitGraph.PcNamed];
            Assert.That(gapEvals, Is.EqualTo(2), "a box is read only for a pair the bones keep");
            Assert.That(pairs.graph.convexPositive[0], Is.Not.EqualTo(pairs.graph.convexPositive[1]));
            Assert.That(pairs.graph.trianglePositive[0], Is.EqualTo(pairs.graph.convexPositive[0]), "each mesh with its convex");
            Assert.That(pairs.graph.trianglePositive[1], Is.EqualTo(pairs.graph.convexPositive[1]));
            Assert.That(pairs.graph.positiveConvexes + pairs.graph.negativeConvexes, Is.EqualTo(2));
            Assert.That(pairs.graph.positiveTriangles + pairs.graph.negativeTriangles, Is.EqualTo(2), "every triangle on one side, once");
        }

        /// <summary>
        /// **A pair only the geometry leaves out.** The bones connect everything (every vertex weighted to both bones, one cluster's
        /// mesh the other's too); the boxes leave out each convex and the far mesh, and the two meshes -- counted by kind.
        /// </summary>
        [Test]
        public void APairTheBonesKeepAndTheBoxesLeaveOut_IsCountedAsTheBoxes()
        {
            Piece p = TwoArms(new[] { W(0, 1), W(0, 1), W(0, 1), W(0, 1), W(0, 1), W(0, 1) });
            Outcome pairs = Decide(p, 1);
            Assert.That(pairs.counts.boneApart, Is.EqualTo(new long[] { 1, 0, 0 }), "only the two convexes are of two clusters");
            Assert.That(pairs.counts.boxApart, Is.EqualTo(new long[] { 0, 2, 1 }), "each convex and the far mesh; the two meshes");
            Assert.That(pairs.counts.candidates, Is.EqualTo(new long[] { 0, 2, 0 }));
            Assert.That(pairs.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
        }

        /// <summary>
        /// **Each alone holds it together; the pairs divide it.** The bones: mesh 0 is weighted to both bones, so everything is one
        /// group. The geometry: the convexes overlap, so everything is one component. Pair by pair: the convexes are left out by the
        /// bones, mesh 0 and the far convex and the two meshes by the boxes -- two groups.
        /// </summary>
        [Test]
        public void WhatTheBonesAloneAndTheGeometryAloneHoldTogether_ThePairsDivide()
        {
            Piece p = TwoArms(new[] { W(0), W(1), W(0, 1), W(1), W(1), W(1) });
            Outcome geometry = Decide(p, 0);
            Assert.That(geometry.bone.verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable), "the bones alone: one group");
            Assert.That(geometry.graph.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "the geometry alone: one component");
            Outcome pairs = Decide(p, 1);
            Assert.That(pairs.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(pairs.counts.boneApart, Is.EqualTo(new long[] { 1, 1, 0 }), "the convexes; convex 0 and mesh 1");
            Assert.That(pairs.counts.boxApart, Is.EqualTo(new long[] { 0, 1, 1 }), "convex 1 and mesh 0; the meshes");
            Assert.That(Sides(pairs.graph), Is.EqualTo("10|10").Or.EqualTo("01|01"));
        }

        /// <summary>
        /// **Not known is not apart.** Without the bones' rows no pair is left out by them: the engine's own result. And a pair the
        /// hulls do not settle within their iterations stays connected: no placement is made of it.
        /// </summary>
        [Test]
        public void WithoutTheBones_NoPairIsLeftOutByThem_AndAPairTheHullsDoNotSettle_StaysConnected()
        {
            Piece p = TwoArms(new[] { W(0), W(0), W(0), W(1), W(1), W(1) });
            Outcome unknown = Decide(p, 1, bonesKnown: false);
            Assert.That(unknown.counts.boneApart, Is.EqualTo(new long[] { 0, 0, 0 }));
            Assert.That(unknown.graph.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "as the geometry alone: the convexes overlap");
            Assert.That(unknown.counts.boxApart, Is.EqualTo(new long[] { 0, 2, 1 }));

            // the warm-up's input: a placement that needs the hulls (the boxes overlap, the hulls are apart)
            SpatialSplitGraph.CompactInput warm = SpatialSplitGraph.WarmupInput();
            var settled = new SpatialSplitGraph.PairInput();
            SpatialSplitGraph.GraphOutcome a = SpatialSplitGraph.DecideCompact(warm, new SpatialSplitGraph.GraphSettings { secondsLimit = 0.0, pair = settled }, true, out _);
            Assert.That(a.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(settled.gjkTried[0] + settled.gjkTried[1] + settled.gjkTried[2], Is.GreaterThan(0), "pairs were tried by the hulls");
            Assert.That(settled.gjkRefuted[0] + settled.gjkRefuted[1] + settled.gjkRefuted[2], Is.GreaterThan(0), "and taken off by them");
            var short1 = new SpatialSplitGraph.PairInput();
            SpatialSplitGraph.GraphOutcome b = SpatialSplitGraph.DecideCompact(warm, new SpatialSplitGraph.GraphSettings { secondsLimit = 0.0, pair = short1, gjkMaxIterations = 1 }, true, out _);
            TestContext.Out.WriteLine("one iteration: " + b.verdict + ", limit hits " + b.gjkLimitHit + ", unsettled " + string.Join("/", short1.gjkUnsettled));
            if (b.gjkLimitHit > 0)
            {
                Assert.That(b.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "a pair not settled is not apart");
                Assert.That(short1.gjkUnsettled[0] + short1.gjkUnsettled[1] + short1.gjkUnsettled[2], Is.GreaterThan(0));
            }
            else
            {
                Assert.That(b.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "settled within one iteration: the same result");
            }
        }

        /// <summary>
        /// **The placement among the groups is the bone-aware split's.** Three bones, a convex and a mesh each, of two, one and three
        /// triangles: the group of fewest triangles goes to the positive side alone (the smallest independent group first). With two
        /// groups, the one of more triangles.
        /// </summary>
        [Test]
        public void WithThreeGroups_TheSmallestGoesAlone_WithTwo_TheLarger()
        {
            var positions = new List<float3>();
            var corners = new List<int>();
            var weights = new List<ulong>();
            int[] trianglesOf = { 2, 1, 3 };
            for (int g = 0; g < 3; g++)
            {
                for (int k = 0; k < trianglesOf[g]; k++)
                {
                    float x = (2f * g) + 0.2f, y = 0.1f + (0.25f * k);
                    int first = positions.Count;
                    positions.Add(new float3(x, y, 0.5f)); positions.Add(new float3(x + 0.3f, y, 0.5f)); positions.Add(new float3(x + 0.1f, y + 0.2f, 0.5f));
                    corners.AddRange(new[] { first, first + 1, first + 2 });
                    for (int v = 0; v < 3; v++) weights.Add(W(g));
                }
            }

            var three = new Piece
            {
                bones = 3, weights = weights.ToArray(), convexBones = new[] { 0, 1, 2 }, convexes = new[] { Box(0f, 1f), Box(2f, 3f), Box(4f, 5f) },
                positions = positions.ToArray(), corners = corners.ToArray(),
            };
            Outcome a = Decide(three, 1);
            Assert.That(a.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(Sides(a.graph), Is.EqualTo("010|001000"), "the group of one triangle alone");
            Assert.That(a.bone.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
            Assert.That(a.bone.positiveTriangles, Is.EqualTo(1), "as the bones alone place it");

            var two = new Piece
            {
                bones = 2, weights = weights.GetRange(0, 9).ToArray(), convexBones = new[] { 0, 1 }, convexes = new[] { Box(0f, 1f), Box(2f, 3f) },
                positions = positions.GetRange(0, 9).ToArray(), corners = corners.GetRange(0, 9).ToArray(),
            };
            Outcome b = Decide(two, 1);
            Assert.That(Sides(b.graph), Is.EqualTo("10|110"), "the group of two triangles, not the one of one");
        }

        // Groups equal in triangles and convexes: a bone, a convex and one triangle each, at x = 2 g; the convexes given in this order.
        private static Piece EqualGroups(int groups, int[] convexOrder)
        {
            var positions = new List<float3>();
            var corners = new List<int>();
            var weights = new List<ulong>();
            for (int g = 0; g < groups; g++)
            {
                float x = (2f * g) + 0.2f;
                positions.Add(new float3(x, 0.2f, 0.5f)); positions.Add(new float3(x + 0.3f, 0.2f, 0.5f)); positions.Add(new float3(x + 0.1f, 0.4f, 0.5f));
                corners.AddRange(new[] { 3 * g, (3 * g) + 1, (3 * g) + 2 });
                for (int v = 0; v < 3; v++) weights.Add(W(g));
            }

            var convexes = new float3[groups][];
            for (int k = 0; k < groups; k++) convexes[k] = Box(2f * convexOrder[k], (2f * convexOrder[k]) + 1f);
            return new Piece { bones = groups, weights = weights.ToArray(), convexBones = convexOrder, convexes = convexes, positions = positions.ToArray(), corners = corners.ToArray() };
        }

        /// <summary>
        /// **Of equal groups, the one the bones alone choose -- whatever the order of the convexes** (review 1). Groups of one convex
        /// and one triangle each: the bones alone meet their groups in the order of each group's lowest bone and keep the first of
        /// equals, so the group of bone 0 is the one placed apart, with two groups and with three. The combination chooses the same
        /// group, and still does with the convexes given in the reverse order.
        /// </summary>
        [Test]
        public void OfEqualGroups_TheOneTheBonesAloneChoose_IsChosen_WhateverTheOrderOfTheConvexes()
        {
            foreach (int groups in new[] { 2, 3 })
            {
                var forward = new int[groups];
                var backward = new int[groups];
                for (int k = 0; k < groups; k++) { forward[k] = k; backward[k] = groups - 1 - k; }
                foreach (int[] order in new[] { forward, backward })
                {
                    Outcome o = Decide(EqualGroups(groups, order), 1);
                    Assert.That(o.bone.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable));
                    Assert.That(o.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
                    string what = groups + " groups, convexes " + string.Join(",", order);
                    for (int k = 0; k < groups; k++)
                    {
                        Assert.That(o.graph.convexPositive[k], Is.EqualTo(order[k] == 0), what + ": the convex of bone 0 alone on the positive side");
                        Assert.That(o.graph.convexPositive[k], Is.EqualTo(o.boneSides[k] != 0), what + ": as the bones alone place convex " + k);
                    }

                    for (int g = 0; g < groups; g++)
                    {
                        Assert.That(o.graph.trianglePositive[g], Is.EqualTo(g == 0), what + ": the triangle of bone 0 with it");
                        Assert.That(o.graph.trianglePositive[g], Is.EqualTo(o.boneSides[groups + g] != 0), what + ": as the bones alone place triangle " + g);
                    }
                }
            }
        }

        /// <summary>
        /// **A malformed input is refused; what is only not known goes on by the geometry alone** (review 1). A convex's bone number
        /// or a weight's bone number outside the rig's range: the bone-aware decision says malformed, and the combination's reading of
        /// it is Invalid. Triangles weighted to no bone at all: not known of a well-formed input -- the geometry alone decides (no
        /// pair is left out by the bones). A decision that worked out its connections: known.
        /// </summary>
        [Test]
        public void AMalformedInput_IsInvalid_AndWhatIsOnlyNotKnown_GoesOnByTheGeometryAlone()
        {
            Piece good = TwoArms(new[] { W(0), W(0), W(0), W(1), W(1), W(1) });
            Outcome known = Decide(good, 0);
            Assert.That(known.bone.malformed, Is.False);
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in known.bone, true), Is.EqualTo(SpatialSplitPairBones.Known));
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in known.bone, false), Is.EqualTo(SpatialSplitPairBones.NotKnown), "without its rows nothing is read of it");

            Piece convexOutside = TwoArms(new[] { W(0), W(0), W(0), W(1), W(1), W(1) });
            convexOutside.convexBones = new[] { 0, 5 };
            Outcome a = Decide(convexOutside, 0);
            Assert.That(a.bone.verdict == NpcBoneSplitVerdict.Insufficient && a.bone.reason == NpcBoneSplitSearch.ReasonConvex && a.bone.malformed, Is.True, "a convex's bone outside the rig's range");
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in a.bone, false), Is.EqualTo(SpatialSplitPairBones.Invalid));

            Outcome b = Decide(TwoArms(new[] { W(0), W(0), W(7), W(1), W(1), W(1) }), 0);
            Assert.That(b.bone.verdict == NpcBoneSplitVerdict.Insufficient && b.bone.reason == NpcBoneSplitSearch.ReasonWeight && b.bone.malformed, Is.True, "a weight's bone outside the rig's range");
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in b.bone, false), Is.EqualTo(SpatialSplitPairBones.Invalid));

            Piece unweighted = TwoArms(new[] { W(0), W(0), W(0), W(), W(), W() });
            Outcome c = Decide(unweighted, 1, bonesKnown: false);
            Assert.That(c.bone.verdict == NpcBoneSplitVerdict.Insufficient && c.bone.reason == NpcBoneSplitSearch.ReasonUnweighted && !c.bone.malformed, Is.True, "triangles weighted to no bone: not known, not malformed");
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in c.bone, true), Is.EqualTo(SpatialSplitPairBones.NotKnown));
            Assert.That(c.counts.boneApart, Is.EqualTo(new long[] { 0, 0, 0 }), "the geometry alone: no pair left out by the bones");
            Assert.That(c.graph.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput));
        }

        /// <summary>
        /// **The topology is required whatever the bones say** (review 2). The bone-aware decision ends at the first thing it does not
        /// know: with the rig not known it never looks at the topology, and says so of a well-formed input. The combination's own
        /// check does not depend on that: the rig not known and the topology there -- the geometry alone, by the topology's nodes; the
        /// rig not known and the topology missing -- Invalid, as with the rig known; and a malformed input stays Invalid with the
        /// topology there. The engine asked for the topology's nodes without a topology refuses the input too (never render-vertex nodes).
        /// </summary>
        [Test]
        public void WithoutTheTopology_TheInputIsInvalid_WhateverTheBonesDecisionEndedAt()
        {
            ulong[] weights = { W(0), W(0), W(0), W(1), W(1), W(1) };
            // the rig not known, the topology there: not known, the geometry alone over the topology's nodes
            Outcome a = Decide(TwoArms(weights), 1, bonesKnown: false, insufficient: "the pose was not read");
            Assert.That(a.bone.verdict == NpcBoneSplitVerdict.Insufficient && a.bone.reason.StartsWith(NpcBoneSplitSearch.ReasonRig) && !a.bone.malformed, Is.True);
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in a.bone, false, true), Is.EqualTo(SpatialSplitPairBones.NotKnown));
            Assert.That(a.graph.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput));
            Assert.That(a.counts.boneApart, Is.EqualTo(new long[] { 0, 0, 0 }));

            // the rig not known AND the topology missing: the bones' record is the same (it ended before the topology), the input is Invalid
            Outcome b = Decide(TwoArms(weights), 1, bonesKnown: false, insufficient: "the pose was not read", noTopology: true);
            Assert.That(b.bone.reason.StartsWith(NpcBoneSplitSearch.ReasonRig) && !b.bone.malformed, Is.True, "the bones' decision did not reach its topology check");
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in b.bone, false, false), Is.EqualTo(SpatialSplitPairBones.Invalid));
            Assert.That(b.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput), "the engine: no topology, no nodes");
            StringAssert.Contains("topology", b.graph.reason);

            // the rig known and the topology missing: the bones say so themselves (malformed), and the check agrees
            Outcome c = Decide(TwoArms(weights), 1, bonesKnown: false, noTopology: true);
            Assert.That(c.bone.reason == NpcBoneSplitSearch.ReasonTopology && c.bone.malformed, Is.True);
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in c.bone, false, false), Is.EqualTo(SpatialSplitPairBones.Invalid));
            Assert.That(c.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput));

            // the triangles over the bones' limit is the other ending before the topology: the same rule (a record made by hand)
            var over = new NpcBoneSplitRecord { verdict = NpcBoneSplitVerdict.OverTheLimits, reason = NpcBoneSplitSearch.ReasonTriangles };
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in over, false, true), Is.EqualTo(SpatialSplitPairBones.NotKnown));
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in over, false, false), Is.EqualTo(SpatialSplitPairBones.Invalid));

            // a malformed input with the topology there stays Invalid; a decided one with the topology is known
            Piece outside = TwoArms(weights);
            outside.convexBones = new[] { 0, 5 };
            Outcome d = Decide(outside, 0);
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in d.bone, false, true), Is.EqualTo(SpatialSplitPairBones.Invalid));
            Outcome e = Decide(TwoArms(weights), 1);
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in e.bone, true, true), Is.EqualTo(SpatialSplitPairBones.Known));
            Assert.That(CutSpatialSplit.Attempt.BonesOf(in e.bone, true, false), Is.EqualTo(SpatialSplitPairBones.Invalid), "decided or not, no topology is no input");
        }

        // Bone 0 with its convex at x 0..1 and a mesh of two triangles (four vertices) in it; bone 1 with its convex at x 5..6 and a
        // mesh of three triangles (five vertices) in it (the larger group: the positive side of two). And islands far from both convexes (x 3 and on), each a
        // triangle with the weights given: the boxes leave every pair of an island and a convex out.
        private static Piece Islands(int bones, params ulong[][] islands)
        {
            var positions = new List<float3> { new float3(0.2f, 0.2f, 0.5f), new float3(0.5f, 0.2f, 0.5f), new float3(0.3f, 0.5f, 0.5f), new float3(0.6f, 0.5f, 0.5f) };
            var weights = new List<ulong> { W(0), W(0), W(0), W(0) };
            var corners = new List<int> { 0, 1, 2, 1, 3, 2 };
            positions.AddRange(new[] { new float3(5.2f, 0.2f, 0.5f), new float3(5.5f, 0.2f, 0.5f), new float3(5.3f, 0.5f, 0.5f), new float3(5.6f, 0.5f, 0.5f), new float3(5.4f, 0.8f, 0.5f) });
            weights.AddRange(new[] { W(1), W(1), W(1), W(1), W(1) });
            corners.AddRange(new[] { 4, 5, 6, 5, 7, 6, 6, 7, 8 });
            for (int k = 0; k < islands.Length; k++)
            {
                int first = positions.Count;
                float x = 3f + (0.02f * k);   // islands next to one another: their own boxes overlap
                positions.AddRange(new[] { new float3(x, 0.2f + (0.01f * k), 0.5f), new float3(x + 0.3f, 0.2f, 0.5f), new float3(x + 0.1f, 0.5f, 0.5f) });
                weights.AddRange(islands[k]);
                corners.AddRange(new[] { first, first + 1, first + 2 });
            }

            var convexBones = new[] { 0, 1 };
            return new Piece { bones = bones, weights = weights.ToArray(), convexBones = convexBones, convexes = new[] { Box(0f, 1f), Box(5f, 6f) }, positions = positions.ToArray(), corners = corners.ToArray() };
        }

        /// <summary>
        /// **A: a component's connection to the convex of the bone it supports most is kept; B: an isolated mesh node is moved to that
        /// convex's side.** An island far from both convexes, three vertices on bone 1 and one of them on bone 0 too. The protected
        /// pairs leave it with no edge: it lies on the negative side by the deal's default, with bone 0's convex -- away from the
        /// bone it supports. With A the island's pair with bone 1's convex is protected though their boxes are apart, and it goes
        /// with that convex; no isolated mesh is left. With B the placement is made as before and the island alone is moved.
        /// </summary>
        [Test]
        public void AnIslandAwayFromItsBonesConvex_IsKeptWithItByA_AndMovedToItsSideByB()
        {
            Piece p = Islands(2, new[] { W(1), W(1), W(0, 1) });
            Outcome basis = Decide(p, 2);
            Assert.That(basis.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(basis.ownMainPairs, Is.EqualTo(3), "each mesh's own main convex: the two meshes at their convexes, the island's bone 1");
            Assert.That(basis.counts.isolatedMesh, Is.EqualTo(1));
            Assert.That(basis.graph.convexPositive[1], Is.True, "the group of three triangles positive");
            Assert.That(basis.graph.trianglePositive[5], Is.False, "the island on the negative side by default: with bone 0's convex");

            Outcome a = Decide(p, 3);
            Assert.That(a.ownProtectedAdded, Is.EqualTo(1), "the island and bone 1's convex (the other two were protected already, seen from the bones)");
            Assert.That(a.ownUnderRatio, Is.EqualTo(0), "the ratio had kept that cluster");
            Assert.That(a.counts.protectedBonesApart, Is.EqualTo(0));
            Assert.That(a.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(a.counts.protectedKeptThoughApart, Is.EqualTo(1));
            Assert.That(a.counts.isolatedMesh, Is.EqualTo(0), "nothing is left for B");
            Assert.That(a.graph.trianglePositive[5], Is.EqualTo(a.graph.convexPositive[1]), "the island with bone 1's convex");
            Assert.That(a.graph.positiveTriangles + a.graph.negativeTriangles, Is.EqualTo(6));

            Outcome b = Decide(p, 4);
            Assert.That(b.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(b.counts.isolatedMesh == 1 && b.counts.relocated == 1 && b.counts.relocateAmbiguous == 0 && b.counts.relocateNoSupport == 0 && !b.counts.relocationFailed, Is.True);
            Assert.That(b.graph.trianglePositive[5], Is.EqualTo(b.graph.convexPositive[1]), "the island moved to bone 1's convex's side");
            Assert.That(b.graph.convexPositive, Is.EqualTo(basis.graph.convexPositive), "the convexes where the placement had them");
            for (int k = 0; k < 5; k++) Assert.That(b.graph.trianglePositive[k], Is.EqualTo(basis.graph.trianglePositive[k]), "the other triangles where they were");
            Assert.That(b.graph.positiveTriangles == 4 && b.graph.negativeTriangles == 2, Is.True, "every triangle on one side, once");
        }

        /// <summary>
        /// **A's ties, a component that supports no bone with a convex, and the two protections together.** An island with as many
        /// vertices on bone 0 as on bone 1 is protected with both convexes: the two groups are one again, and no placement is made
        /// (the separation is given up; said by the verdict). An island weighted only to a bone without a convex has no main convex:
        /// A adds nothing for it. And A never takes away what the first protection keeps: a pair protected from the bone's side
        /// stays protected.
        /// </summary>
        [Test]
        public void WithA_ATieProtectsBoth_AComponentOfNoConvexBoneGetsNothing_AndTheBonesProtectionStays()
        {
            Outcome tie = Decide(Islands(2, new[] { W(0, 1), W(0, 1), W(0, 1) }), 3);
            Assert.That(tie.ownProtectedAdded, Is.EqualTo(2), "the island with both convexes");
            Assert.That(tie.counts.protectedKeptThoughApart, Is.EqualTo(2));
            Assert.That(tie.graph.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "the two groups are held together through the island: no placement");
            Outcome tieBefore = Decide(Islands(2, new[] { W(0, 1), W(0, 1), W(0, 1) }), 2);
            Assert.That(tieBefore.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "without A the island is left out and the two groups part");

            Outcome none = Decide(Islands(3, new[] { W(2), W(2), W(2) }), 3);
            Assert.That(none.ownMainPairs, Is.EqualTo(2), "the two meshes at their convexes; the island supports no bone with a convex");
            Assert.That(none.ownProtectedAdded, Is.EqualTo(0));
            Assert.That(none.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(none.counts.isolatedMesh, Is.EqualTo(1), "it stays without an edge");

            // the sleeve's piece: the first protection's pair (the sleeve and the arm's convex) is kept with A as without
            Outcome sleeve = Decide(Sleeve(), 3);
            Assert.That(sleeve.counts.protectedKeptThoughApart, Is.GreaterThanOrEqualTo(1));
            Assert.That(sleeve.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(sleeve.graph.convexPositive[1], Is.EqualTo(sleeve.graph.convexPositive[0]), "the arm's convex with the trunk and its sleeve, as with the first protection alone");
            Assert.That(sleeve.counts.protectedAcross, Is.EqualTo(0));
        }

        /// <summary>
        /// **B moves a mesh node with no edge at all, and only that.** Two islands next to each other, both on bone 1: their own pair
        /// is left (the bones connect them, their boxes overlap), so they are a group of meshes alone, not isolated nodes -- counted
        /// apart and left where the placement put them. An isolated island with as many vertices on each bone has its main convexes
        /// on both sides: left, counted as not unique. One weighted only to a bone without a convex: left, counted as without a
        /// support.
        /// </summary>
        [Test]
        public void WithB_MeshesHeldByMeshesAlone_ATie_AndNoSupport_AreLeftAndCounted()
        {
            Outcome two = Decide(Islands(2, new[] { W(1), W(1), W(1) }, new[] { W(1), W(1), W(1) }), 4);
            Assert.That(two.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(two.counts.isolatedMesh, Is.EqualTo(0), "each island has the other");
            Assert.That(two.counts.meshOnlyNodes, Is.EqualTo(2), "a group of meshes alone");
            Assert.That(two.counts.relocated + two.counts.relocateAmbiguous + two.counts.relocateNoSupport, Is.EqualTo(0), "not B's to move");
            Assert.That(two.graph.trianglePositive[5] == two.graph.trianglePositive[6], Is.True);

            Outcome tie = Decide(Islands(2, new[] { W(0, 1), W(0, 1), W(0, 1) }), 4);
            Assert.That(tie.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(tie.counts.isolatedMesh == 1 && tie.counts.relocateAmbiguous == 1 && tie.counts.relocated == 0, Is.True, "its main convexes are on both sides: left as placed");
            Outcome tieBefore = Decide(Islands(2, new[] { W(0, 1), W(0, 1), W(0, 1) }), 2);
            Assert.That(tie.graph.trianglePositive, Is.EqualTo(tieBefore.graph.trianglePositive));

            Outcome none = Decide(Islands(3, new[] { W(2), W(2), W(2) }), 4);
            Assert.That(none.counts.isolatedMesh == 1 && none.counts.relocateNoSupport == 1 && none.counts.relocated == 0, Is.True, "no bone with a convex to follow");
        }

        /// <summary>
        /// **After B's moves the placement must still stand.** Bone 0's convex with its mesh, bone 1's convex with no mesh, and an
        /// island on bone 0 far from everything: the engine deals the one full group to one side and the lone convex with the lone
        /// mesh to the other. B moves the island to bone 0's convex's side -- and the other side is left with a convex and no
        /// triangle: the placement is given up (not found, said), for the method as before to go on.
        /// </summary>
        [Test]
        public void WithB_APlacementThatNoLongerStands_IsGivenUp()
        {
            var p = new Piece
            {
                bones = 2, weights = new[] { W(0), W(0), W(0), W(0), W(0), W(0), W(0) }, convexBones = new[] { 0, 1 }, convexes = new[] { Box(0f, 1f), Box(5f, 6f) },
                positions = new[]
                {
                    new float3(0.2f, 0.2f, 0.5f), new float3(0.5f, 0.2f, 0.5f), new float3(0.3f, 0.5f, 0.5f), new float3(0.6f, 0.5f, 0.5f),
                    new float3(3f, 0.2f, 0.5f), new float3(3.3f, 0.2f, 0.5f), new float3(3.1f, 0.5f, 0.5f),
                },
                corners = new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6 },   // bone 0's mesh of four vertices (its main supporting mesh), the island of three
            };
            Outcome basis = Decide(p, 2);
            Assert.That(basis.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "one full group, a convex alone, a mesh alone: the engine's deal");
            Assert.That(basis.graph.trianglePositive[2], Is.EqualTo(basis.graph.convexPositive[1]), "the island with the lone convex");
            Outcome b = Decide(p, 4);
            Assert.That(b.counts.relocated, Is.EqualTo(1), "the island to bone 0's convex's side");
            Assert.That(b.counts.relocationFailed, Is.True);
            Assert.That(b.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.NotFound), "the lone convex's side has no triangle left: no placement");
            Assert.That(b.graph.convexPositive, Is.Null);
        }

        /// <summary>
        /// **A protects the pair, not the cluster** (review 1). A strip of forty triangles next to bone 1's own mesh: forty-one of its
        /// vertices on bone 2 (no convex) and one on bone 1 -- the support ratio leaves bone 1's cluster out of the strip's clusters
        /// (1 of 41), and bone 1's main mesh is the other one. The strip's own main convex is still bone 1's. With A that one pair
        /// is kept, though the rows share no cluster and the boxes are apart; the strip's clusters are as the rule made them, so the
        /// strip and bone 1's mesh, whose boxes overlap, are still not connected by the bones -- the pair of the two meshes is left
        /// out by the bones with A exactly as without.
        /// </summary>
        [Test]
        public void WithA_ThePairIsKept_AndTheComponentsClustersAreNotWidened()
        {
            var positions = new List<float3> { new float3(0.2f, 0.2f, 0.5f), new float3(0.5f, 0.2f, 0.5f), new float3(0.3f, 0.5f, 0.5f), new float3(0.6f, 0.5f, 0.5f) };
            var weights = new List<ulong> { W(0), W(0), W(0), W(0) };
            var corners = new List<int> { 0, 1, 2, 1, 3, 2 };
            // bone 1's mesh: three triangles in its convex (x 5..6), reaching up to y 0.9
            positions.AddRange(new[] { new float3(5.2f, 0.2f, 0.5f), new float3(5.5f, 0.2f, 0.5f), new float3(5.3f, 0.5f, 0.5f), new float3(5.6f, 0.5f, 0.5f), new float3(5.4f, 0.9f, 0.5f) });
            weights.AddRange(new[] { W(1), W(1), W(1), W(1), W(1) });
            corners.AddRange(new[] { 4, 5, 6, 5, 7, 6, 6, 7, 8 });
            // the strip: above the convex (y 1.5..1.7: its box apart from the convex's by 0.5) and over bone 1's mesh in x and z;
            // 42 vertices, 40 triangles. Its box does not reach bone 1's mesh either (y), so that pair is the bones' or the boxes' to leave out.
            int first = positions.Count;
            for (int k = 0; k < 21; k++)
            {
                positions.Add(new float3(5.0f + (0.05f * k), 1.5f, 0.5f));
                positions.Add(new float3(5.0f + (0.05f * k), 1.7f, 0.5f));
                weights.Add(k == 0 ? W(1, 2) : W(2));
                weights.Add(W(2));
            }

            for (int k = 0; k < 20; k++)
            {
                int a0 = first + (2 * k);
                corners.AddRange(new[] { a0, a0 + 2, a0 + 1, a0 + 1, a0 + 2, a0 + 3 });
            }

            var p = new Piece { bones = 3, weights = weights.ToArray(), convexBones = new[] { 0, 1 }, convexes = new[] { Box(0f, 1f), Box(5f, 6f) }, positions = positions.ToArray(), corners = corners.ToArray() };
            Outcome basis = Decide(p, 2);
            Outcome a = Decide(p, 3);
            Assert.That(a.bone.connectionsDropped, Is.GreaterThanOrEqualTo(1), "the layout: the ratio leaves bone 1's cluster out of the strip (1 vertex of 42)");
            Assert.That(a.ownMainPairs, Is.EqualTo(3), "each mesh's own main convex; the strip's is bone 1's, by its one vertex");
            Assert.That(a.ownProtectedAdded, Is.EqualTo(1));
            Assert.That(a.ownUnderRatio, Is.EqualTo(1), "its cluster is not among the strip's clusters");
            Assert.That(a.counts.protectedBonesApart, Is.EqualTo(1), "kept as a pair though the rows share no cluster");
            Assert.That(a.counts.protectedKeptThoughApart, Is.EqualTo(1), "and though the boxes are apart");
            Assert.That(basis.counts.protectedBonesApart, Is.EqualTo(0));
            Assert.That(a.counts.boneApart[2], Is.EqualTo(basis.counts.boneApart[2]), "the meshes' pairs left out by the bones are the same: the strip is not connected to bone 1's mesh through a widened cluster");
            Assert.That(a.counts.boneApart[0], Is.EqualTo(basis.counts.boneApart[0]));
            Assert.That(a.counts.boneApart[1], Is.EqualTo(basis.counts.boneApart[1] - 1), "one convex-mesh pair fewer left out by the bones: the protected one");
            Assert.That(a.counts.candidates[1], Is.EqualTo(basis.counts.candidates[1] + 1));
            Assert.That(a.counts.candidates[2], Is.EqualTo(basis.counts.candidates[2]));
            Assert.That(a.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(a.graph.trianglePositive[5], Is.EqualTo(a.graph.convexPositive[1]), "the strip with bone 1's convex");
            Assert.That(a.counts.isolatedMesh, Is.EqualTo(0));
            Assert.That(basis.counts.isolatedMesh, Is.EqualTo(1), "without A the strip has no edge");
        }

        // A closed tetrahedron at x0 whose last face has render vertices of its own (an attribute seam): 7 render vertices, 4 topology vertices.
        private static void Tetrahedron(float x0, int firstLocal, int firstTopology, List<float3> positions, List<int> corners, List<int> topology)
        {
            var q = new[] { new float3(x0 + 0.2f, 0.2f, 0.2f), new float3(x0 + 0.6f, 0.2f, 0.2f), new float3(x0 + 0.4f, 0.6f, 0.2f), new float3(x0 + 0.4f, 0.3f, 0.6f) };
            for (int i = 0; i < 4; i++) { positions.Add(q[i]); topology.Add(firstTopology + i); }
            for (int i = 0; i < 3; i++) { positions.Add(q[i]); topology.Add(firstTopology + i); }   // the seam's other sides: the same points
            int a = firstLocal, b = firstLocal + 1, c = firstLocal + 2, d = firstLocal + 3;
            corners.AddRange(new[] { a, b, d, b, c, d, c, a, d });
            corners.AddRange(new[] { firstLocal + 4, firstLocal + 6, firstLocal + 5 });   // the base (a, c, b) by its own render vertices
        }

        /// <summary>
        /// **Components by topology, each whole on one side, each child closed.** Two closed tetrahedra, each with a face of render
        /// vertices of its own: two mesh nodes (not four), each with its bone's convex; every triangle on one side once; and each
        /// side's surface closed by the topology.
        /// </summary>
        [Test]
        public void ASeamedSurface_IsOneNode_GoesWholeToOneSide_AndEachChildIsClosed()
        {
            var positions = new List<float3>();
            var corners = new List<int>();
            var topology = new List<int>();
            Tetrahedron(0f, 0, 0, positions, corners, topology);
            Tetrahedron(1.2f, 7, 4, positions, corners, topology);
            var weights = new ulong[14];
            for (int i = 0; i < 14; i++) weights[i] = W(i < 7 ? 0 : 1);
            var p = new Piece
            {
                bones = 2, weights = weights, convexBones = new[] { 0, 1 }, convexes = new[] { Box(0f, 1f), Box(0.9f, 2f) },
                positions = positions.ToArray(), corners = corners.ToArray(), topologyOfLocal = topology.ToArray(), topologyCount = 8,
            };
            Outcome pairs = Decide(p, 1);
            Assert.That(pairs.graph.meshComponents, Is.EqualTo(2), "by topology: a tetrahedron is one component across its seam");
            Assert.That(pairs.bone.meshComponents, Is.EqualTo(2));
            Assert.That(pairs.counts.seamSlots, Is.EqualTo(6));
            Assert.That(pairs.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            bool[] side = pairs.graph.trianglePositive;
            for (int k = 1; k < 4; k++) Assert.That(side[k], Is.EqualTo(side[0]), "the first tetrahedron whole");
            for (int k = 5; k < 8; k++) Assert.That(side[k], Is.EqualTo(side[4]), "the second whole");
            Assert.That(side[0], Is.Not.EqualTo(side[4]));
            Assert.That(pairs.graph.positiveTriangles, Is.EqualTo(4));
            Assert.That(pairs.graph.negativeTriangles, Is.EqualTo(4));
            foreach (bool which in new[] { true, false })
            {
                var uses = new Dictionary<(int, int), int>();
                for (int k = 0; k < 8; k++)
                {
                    if (side[k] != which) continue;
                    for (int e = 0; e < 3; e++)
                    {
                        int u = topology[p.corners[(3 * k) + e]], v = topology[p.corners[(3 * k) + ((e + 1) % 3)]];
                        (int, int) key = (Math.Min(u, v), Math.Max(u, v));
                        uses[key] = uses.TryGetValue(key, out int had) ? had + 1 : 1;
                    }
                }

                foreach (KeyValuePair<(int, int), int> e in uses) Assert.That(e.Value, Is.EqualTo(2), "an edge of the child's surface is shared by two triangles");
            }

            // by render vertex (no topology) the same surface is four components: the engine as it was
            p.topologyOfLocal = null;
            p.topologyCount = 0;
            Outcome before = Decide(p, 0);
            Assert.That(before.graph.meshComponents, Is.EqualTo(4));
        }

        // The trunk (bone 0), the upper arm (bone 1) and the hand (bone 2), a convex each: the trunk's at the origin, the arm's at
        // x 5, the hand's at x 7. The sleeve (one component with the trunk's mesh, at the trunk) has three vertices on the arm's
        // bone; the hand's mesh (at the hand) one.
        private static Piece Sleeve() => new Piece
        {
            bones = 3,
            weights = new[] { W(0), W(0), W(0, 1), W(0, 1), W(0, 1), W(2), W(2), W(1, 2) },
            convexBones = new[] { 0, 1, 2 },
            convexes = new[] { Box(0f, 1f), Box(5f, 6f), Box(7f, 8f) },
            positions = new[]
            {
                new float3(0.2f, 0.2f, 0.5f), new float3(0.4f, 0.2f, 0.5f), new float3(0.3f, 0.4f, 0.5f), new float3(0.6f, 0.3f, 0.5f), new float3(0.5f, 0.6f, 0.5f),
                new float3(7.2f, 0.2f, 0.5f), new float3(7.5f, 0.2f, 0.5f), new float3(7.3f, 0.4f, 0.5f),
            },
            corners = new[] { 0, 1, 2, 1, 3, 2, 2, 3, 4, 5, 6, 7 },
        };

        /// <summary>
        /// **The main supporting mesh's protection.** The sleeve is the mesh that supports the upper arm's bone by the most vertices,
        /// and the arm's convex is far from it. The plain pairs: the boxes leave the sleeve and the arm's convex out, and the arm's
        /// convex goes away from the sleeve (counted: a protected pair left out by the boxes, and across the two sides). With the
        /// protection that pair stays whatever its boxes say: the arm's convex goes with the sleeve and the trunk, the hand alone.
        /// </summary>
        [Test]
        public void TheSleeveAndTheArmsConvex_AreDividedByThePlainPairs_AndKeptTogetherByTheProtection()
        {
            Piece p = Sleeve();
            Outcome plain = Decide(p, 1);
            Assert.That(plain.bone.verdict, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable), "the bones alone: the sleeve and the hand's mesh both support the arm");
            Assert.That(plain.protectedPairsOfBones, Is.EqualTo(3), "the trunk's mesh and the trunk's convex, the sleeve and the arm's, the hand's mesh and the hand's");
            Assert.That(plain.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(plain.counts.protectedPairs, Is.EqualTo(3));
            Assert.That(plain.counts.protectedApartByBoxes, Is.EqualTo(1), "the sleeve and the arm's convex");
            Assert.That(plain.counts.protectedAcross, Is.EqualTo(1));
            Assert.That(plain.graph.convexPositive[1], Is.Not.EqualTo(plain.graph.convexPositive[0]), "the arm's convex away from the trunk and its sleeve");

            Outcome kept = Decide(p, 2);
            Assert.That(kept.graph.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            Assert.That(kept.counts.protectedKeptThoughApart, Is.EqualTo(1));
            Assert.That(kept.counts.protectedAcross, Is.EqualTo(0));
            Assert.That(kept.graph.convexPositive[1], Is.EqualTo(kept.graph.convexPositive[0]), "the arm's convex with the trunk");
            Assert.That(kept.graph.trianglePositive[0], Is.EqualTo(kept.graph.convexPositive[0]), "and with the sleeve");
            Assert.That(kept.graph.convexPositive[2], Is.Not.EqualTo(kept.graph.convexPositive[0]), "the hand apart");
            Assert.That(kept.graph.trianglePositive[3], Is.EqualTo(kept.graph.convexPositive[2]), "with its mesh");
        }

        /// <summary>
        /// **The bones each child would keep.** From a placement of the sleeve's piece (the trunk and the sleeve positive, the arm's
        /// convex and the hand negative -- what the plain pairs give): the positive child needs the trunk's bone and, for its sleeve's
        /// vertices, the arm's; the negative child the arm's (its convex) and the hand's. The arm's bone is needed by both, by a
        /// convex on one side and supporting vertices on the other; the pointed triangle (the hand's) is apart from the trunk.
        /// </summary>
        [Test]
        public void TheBonesEachChildNeeds_FollowThePlacement_AndABoneBothNeedIsToldWithItsBasis()
        {
            Piece p = Sleeve();
            int stride = UnsafeUtility.SizeOf<VpRenderVertex>(), at0 = UnsafeUtility.GetFieldOffset(typeof(VpRenderVertex).GetField(nameof(VpRenderVertex.position)));
            var vertexBytes = new byte[p.positions.Length * stride];
            var ids = new uint[p.positions.Length];
            for (int i = 0; i < p.positions.Length; i++)
            {
                ids[i] = BaseStart + (uint)i;
                Buffer.BlockCopy(BitConverter.GetBytes(p.positions[i].x), 0, vertexBytes, (i * stride) + at0, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(p.positions[i].y), 0, vertexBytes, (i * stride) + at0 + 4, 4);
                Buffer.BlockCopy(BitConverter.GetBytes(p.positions[i].z), 0, vertexBytes, (i * stride) + at0 + 8, 4);
            }

            var indices = new uint[p.corners.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = BaseStart + (uint)p.corners[i];
            var sample = new SpatialSplitSample { vertexStructBytes = stride, vertexIds = ids, vertexBytes = vertexBytes, indices = indices };
            var bones = new SpatialSplitBoneSample
            {
                nodeCount = 3, parent = new[] { -1, 0, 1 }, present = new[] { true, true, true }, piecePresent = new[] { true, true, true }, jointKept = new[] { 7UL },
                segmentLo = new[] { 1f, 0f, 0f }, segmentHi = new[] { 0f, 1f, 1f }, convexBones = p.convexBones, baseStart = BaseStart, baseCount = p.weights.Length, baseBones = p.weights,
                localCount = p.weights.Length, chunkStarts = new uint[0], chunkLocalBases = new int[0], chunkOrigins = new uint2[0][],
            };
            SpatialSplitPairAnalysis a = SpatialSplitPairAnalysis.Of(sample, bones, null, new float3(7.3f, 0.25f, 0.5f), new byte[] { 1, 0, 0 }, new byte[] { 1, 1, 1, 0 });
            TestContext.Out.WriteLine(a.Json());
            Assert.That(a.hitTriangle, Is.EqualTo(3));
            Assert.That(a.trunkConvex, Is.EqualTo(0), "the convex of the bone nearest the root");
            Assert.That(a.hitSeparated, Is.True);
            Assert.That(a.positiveBones, Is.EqualTo(new[] { 0, 1 }), "the trunk's, and the arm's for the sleeve's vertices");
            Assert.That(a.negativeBones, Is.EqualTo(new[] { 1, 2 }), "the arm's for its convex, the hand's");
            Assert.That(a.bothBones, Is.EqualTo(new[] { 1 }));
            Assert.That(a.bothBasis[0], Is.EqualTo(new int4(1, 0, 3, 1)), "bone 1: its convex negative; three supporting vertices positive, one negative");
            Assert.That(a.positiveSegments, Is.EqualTo(1), "the trunk to the arm");
            Assert.That(a.negativeSegments, Is.EqualTo(1), "the arm to the hand");
            Assert.That(a.convexesWithoutMesh, Is.Empty, "the arm's convex still has the hand's vertex on its side");
            Assert.That(a.verticesWithoutSupport, Is.EqualTo(0));
            Assert.That(a.componentsDivided, Is.EqualTo(0));

            // the hand's convex taken from its mesh: a convex without its mesh, and vertices without their convexes
            SpatialSplitPairAnalysis lost = SpatialSplitPairAnalysis.Of(sample, bones, null, new float3(7.3f, 0.25f, 0.5f), new byte[] { 1, 1, 1 }, new byte[] { 1, 1, 1, 0 });
            Assert.That(lost.convexesWithoutMesh, Is.EqualTo(new[] { 2 }));
            Assert.That(lost.verticesWithoutSupport, Is.EqualTo(3));
        }
    }
}
