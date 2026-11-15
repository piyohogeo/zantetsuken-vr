using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// NPC-PAIR-REUSE-1: what one decision of the graph engine leaves for a later decision of the same input
    /// (<see cref="SpatialSplitGraph.Kept"/>) and the links' check before that later decision
    /// (<see cref="SpatialSplitGraph.ConvexesHeld"/>), over hand-made span inputs. The first decision is the combination's shape of
    /// the engine (the mesh components by topology: a seamed surface is one node); the later one the graph stage's (the components
    /// by render vertex). A later decision on the kept set ends exactly as one from the start -- the verdict, the placement, every
    /// count, the work and the bytes -- with the compaction and the first preparation not computed again and a hull judgment of two
    /// convexes carried out once; where the convexes are all held together no graph places and the check says so; a pair at the
    /// iteration limit holds nothing; a budget stops both at the same place; another input reuses nothing; every area is given back.
    /// </summary>
    public unsafe class SpatialSplitPairReuseTests
    {
        private const float Margin = 1f / 4096f;
        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private long _live;

        [SetUp]
        public void SetUp()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            _live = SpatialSplitGraph.LiveNativeAllocations;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--) _disposables[i].Dispose();
            _disposables.Clear();
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(_live), "every area of the engine and of the kept sets was given back");
        }

        /// <summary>A span input: a vertex view, indices into it, the convexes as arrays, and the render vertices' topology (a seam: two render vertices of one topology vertex).</summary>
        private sealed class Piece : IDisposable
        {
            internal NativeArray<VpRenderVertex> view;
            internal NativeArray<uint> indices;
            internal NativeArray<int> topology;
            internal NativeArray<RenderTopologyRange> ranges;
            internal float3[][] convexes;
            internal RenderCutTopologyMap map;
            internal int maxConvexVertices;

            internal SpatialSplitGraph.SpanInput Span(float margin = Margin) => new SpatialSplitGraph.SpanInput
            {
                vertices = (VpRenderVertex*)view.GetUnsafePtr(), vertexViewLength = view.Length, indices = (uint*)indices.GetUnsafePtr(), indexCount = indices.Length,
                convexVertices = convexes, geometryToOwner = float4x4.identity, shapeToOwner = float4x4.identity, margin = margin,
            };

            public void Dispose()
            {
                if (view.IsCreated) view.Dispose();
                if (indices.IsCreated) indices.Dispose();
                if (topology.IsCreated) topology.Dispose();
                if (ranges.IsCreated) ranges.Dispose();
            }
        }

        private Piece Make(float3[] positions, int[] corners, int[] topologyOfVertex, int topologyCount, params float3[][] convexes)
        {
            var p = new Piece
            {
                view = new NativeArray<VpRenderVertex>(positions.Length, Allocator.Persistent), indices = new NativeArray<uint>(corners.Length, Allocator.Persistent),
                topology = new NativeArray<int>(positions.Length, Allocator.Persistent), ranges = new NativeArray<RenderTopologyRange>(1, Allocator.Persistent), convexes = convexes,
            };
            _disposables.Add(p);
            for (int i = 0; i < positions.Length; i++)
            {
                p.view[i] = new VpRenderVertex { position = new Vector3(positions[i].x, positions[i].y, positions[i].z) };
                p.topology[i] = topologyOfVertex[i];
            }

            for (int i = 0; i < corners.Length; i++) p.indices[i] = (uint)corners[i];
            p.ranges[0] = new RenderTopologyRange { vertexBase = 0, count = positions.Length, topologyVertex = (int*)p.topology.GetUnsafePtr() };
            p.map = new RenderCutTopologyMap { ranges = (RenderTopologyRange*)p.ranges.GetUnsafePtr(), rangeCount = 1, topologyVertexCount = topologyCount };
            foreach (float3[] c in convexes) p.maxConvexVertices = Math.Max(p.maxConvexVertices, c.Length);
            return p;
        }

        // A slab of the plane z in [0, 1] between the lines y = x + lift and y = x + lift + 0.1, x in [x0, x0 + 1].
        private static float3[] Slab(float x0, float lift)
        {
            var v = new float3[8];
            for (int i = 0; i < 8; i++)
            {
                float x = x0 + ((i & 1) != 0 ? 1f : 0f);
                v[i] = new float3(x, x + lift + ((i & 2) != 0 ? 0.1f : 0f), (i & 4) != 0 ? 1f : 0f);
            }

            return v;
        }

        // Two slabs whose boxes overlap and whose hulls are 0.35 apart, a triangle inside each; the two triangles are one surface by
        // topology (their first vertices are one topology vertex) and two by render vertex.
        private Piece TwoSlabs() => Make(
            new[]
            {
                new float3(0.3f, 0.33f, 0.5f), new float3(0.5f, 0.53f, 0.5f), new float3(0.4f, 0.45f, 0.6f),
                new float3(1.0f, 0.43f, 0.5f), new float3(1.2f, 0.63f, 0.5f), new float3(1.1f, 0.55f, 0.6f),
            },
            new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 1, 2, 0, 3, 4 }, 5, Slab(0f, 0f), Slab(0.6f, -0.6f));

        // Two boxes that overlap (x 0.9 .. 1), a triangle inside each, the triangles one surface by topology.
        private Piece TwoBoxesThatMeet() => Make(
            new[]
            {
                new float3(0.2f, 0.2f, 0.5f), new float3(0.4f, 0.2f, 0.5f), new float3(0.3f, 0.4f, 0.5f),
                new float3(1.5f, 0.2f, 0.5f), new float3(1.8f, 0.2f, 0.5f), new float3(1.6f, 0.4f, 0.5f),
            },
            new[] { 0, 1, 2, 3, 4, 5 }, new[] { 0, 1, 2, 0, 3, 4 }, 5,
            SpatialSplitGraph.WarmupBox(new float3(0f), new float3(1f)), SpatialSplitGraph.WarmupBox(new float3(0.9f, 0f, 0f), new float3(2f, 1f, 1f)));

        // The two slabs with many small triangles in each (each its own component by render vertex, all one surface by topology):
        // a preparation and a stage A of many thousand units, so that a budget's check points fall inside them.
        private Piece ManyInTwoSlabs(int each)
        {
            var positions = new List<float3>();
            var corners = new List<int>();
            var topology = new List<int>();
            int next = 1;
            for (int side = 0; side < 2; side++)
            {
                for (int i = 0; i < each; i++)
                {
                    float x = (side == 0 ? 0.05f : 0.65f) + (0.9f * i / each), y = x + (side == 0 ? 0.05f : -0.55f), z = 0.2f + (0.6f * ((i * 37) % 101) / 101f);
                    int first = positions.Count;
                    positions.Add(new float3(x, y, z)); positions.Add(new float3(x + 0.001f, y + 0.001f, z)); positions.Add(new float3(x, y + 0.0005f, z + 0.001f));
                    corners.Add(first); corners.Add(first + 1); corners.Add(first + 2);
                    topology.Add(0); topology.Add(next++); topology.Add(next++);
                }
            }

            return Make(positions.ToArray(), corners.ToArray(), topology.ToArray(), next, Slab(0f, 0f), Slab(0.6f, -0.6f));
        }

        private static SpatialSplitGraph.GraphSettings Settings(long workBudget = 0, int gjkMaxIterations = 64) => new SpatialSplitGraph.GraphSettings
        {
            secondsLimit = 0.0, measure = 0, nearestSelection = true, flowDinic = true, workBudget = workBudget, gjkMaxIterations = gjkMaxIterations,
        };

        /// <summary>The combination's decision of the piece (the components by topology, no rows: the geometry alone), leaving a kept set.</summary>
        private SpatialSplitGraph.Kept Combination(Piece p, out SpatialSplitGraph.GraphOutcome outcome, long workBudget = 0, int gjkMaxIterations = 64)
        {
            var kept = new SpatialSplitGraph.Kept();
            _disposables.Add(kept);
            SpatialSplitGraph.GraphSettings settings = Settings(workBudget, gjkMaxIterations);
            settings.pair = new SpatialSplitGraph.PairInput { useTopology = true, topology = p.map, protect = true };
            settings.keep = kept;
            outcome = SpatialSplitGraph.DecideSpan(p.Span(), settings, true, out SpatialSplitGraph.NativeRun _);
            return kept;
        }

        private static void AssertSame(SpatialSplitGraph.GraphOutcome a, SpatialSplitGraph.NativeRun ra, SpatialSplitGraph.GraphOutcome b, SpatialSplitGraph.NativeRun rb, string what)
        {
            Assert.That(b.verdict, Is.EqualTo(a.verdict), what + ": verdict (" + a.reason + " / " + b.reason + ")");
            Assert.That(b.reason, Is.EqualTo(a.reason), what + ": reason");
            Assert.That(b.stop, Is.EqualTo(a.stop), what + ": stop");
            Assert.That(b.unfinished, Is.EqualTo(a.unfinished), what + ": unfinished");
            Assert.That(new[] { b.convexCount, b.triangleCount, b.meshComponents, b.cutAttempts, b.vertexSpan, b.referencedVertices, b.positiveConvexes, b.negativeConvexes, b.positiveTriangles, b.negativeTriangles },
                Is.EqualTo(new[] { a.convexCount, a.triangleCount, a.meshComponents, a.cutAttempts, a.vertexSpan, a.referencedVertices, a.positiveConvexes, a.negativeConvexes, a.positiveTriangles, a.negativeTriangles }),
                what + ": convexes, triangles, components, cut attempts, span, referenced, the sides' counts");
            Assert.That(new[] { b.nodePairs, b.pairsApartByBoxes, b.candidatesA, b.triedB, b.refutedB, b.ambiguousB, b.gjkLimitHit, b.work, b.workPrepare, b.workSearch },
                Is.EqualTo(new[] { a.nodePairs, a.pairsApartByBoxes, a.candidatesA, a.triedB, a.refutedB, a.ambiguousB, a.gjkLimitHit, a.work, a.workPrepare, a.workSearch }),
                what + ": pairs, apart by the boxes, candidates, tried, refuted, ambiguous, at the limit, the work (all, preparation, search)");
            Assert.That(b.cutLimitHit, Is.EqualTo(a.cutLimitHit), what + ": cut limit");
            Assert.That(b.convexPositive, Is.EqualTo(a.convexPositive), what + ": the convexes' sides");
            Assert.That(b.trianglePositive, Is.EqualTo(a.trianglePositive), what + ": the triangles' sides");
            Assert.That(new[] { rb.nativeBytes, rb.allocations, rb.listGrowths, rb.candidateCapacity }, Is.EqualTo(new[] { ra.nativeBytes, ra.allocations, ra.listGrowths, ra.candidateCapacity }),
                what + ": the bytes and the allocations counted against the cap, the lists' growths and capacity");
        }

        /// <summary>
        /// **A graph stage on the kept set ends as one from the start, and a hull judgment of two convexes is carried out once.** The
        /// combination holds the seamed surface as one node: not separable, and no hull is read. The links' check judges the two
        /// convexes (their boxes overlap, their hulls are apart): two groups, not held. The stage then runs on the kept compaction and
        /// first preparation, reads that judgment instead of making it, and places the two slabs apart -- every count, the work and
        /// the bytes those of a stage from the start.
        /// </summary>
        [Test]
        public void AStageOnTheKeptSet_EndsAsOneFromTheStart_AndAJudgmentOfTwoConvexesIsCarriedOutOnce()
        {
            Piece p = TwoSlabs();
            SpatialSplitGraph.GraphOutcome fresh = SpatialSplitGraph.DecideSpan(p.Span(), Settings(), true, out SpatialSplitGraph.NativeRun freshRun);
            Assert.That(fresh.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable), "the layout: the graph stage alone places the slabs apart (" + fresh.reason + ")");
            Assert.That(fresh.meshComponents, Is.EqualTo(2), "the layout: two components by render vertex");
            Assert.That(freshRun.reusedInput, Is.False);

            SpatialSplitGraph.Kept kept = Combination(p, out SpatialSplitGraph.GraphOutcome combination);
            Assert.That(combination.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.NotSeparable), "the layout: by topology the surface is one node (" + combination.reason + ")");
            Assert.That(combination.meshComponents, Is.EqualTo(1));
            Assert.That(kept.Usable, Is.True, "the combination's decision left its compaction, first preparation and the judgments' table");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.GreaterThan(_live), "held by the kept set");
            Assert.That(kept.OwnBytes, Is.LessThanOrEqualTo(SpatialSplitGraph.Kept.OwnBytesAtMost(2, p.maxConvexVertices)), "its own tables are within what is counted before the issue");
            Assert.That(kept.judged[0] & SpatialSplitGraph.JudgedStateMask, Is.EqualTo(SpatialSplitGraph.JudgedNotRun), "no hull was read by the combination: nothing is kept as judged");

            SpatialSplitGraph.LinkRun link = SpatialSplitGraph.ConvexesHeld(kept, Settings(), true);
            Assert.That(link.ran && !link.held && link.groups == 2, Is.True, link.reason);
            Assert.That(link.judged == 1 && link.reused == 0 && link.apartByHulls == 1 && link.boxApart == 0 && link.atLimit == 0, Is.True, "one judgment carried out: apart by the hulls");
            Assert.That(kept.judged[0], Is.EqualTo((byte)(SpatialSplitGraph.JudgedApartByHulls | SpatialSplitGraph.JudgedByLinks)), "kept with what it came to and who carried it out");

            SpatialSplitGraph.GraphSettings again = Settings();
            again.reuse = kept;
            SpatialSplitGraph.GraphOutcome reused = SpatialSplitGraph.DecideSpan(p.Span(), again, true, out SpatialSplitGraph.NativeRun reusedRun);
            Assert.That(reusedRun.reusedInput, Is.True, "the compaction and the first preparation were the kept ones");
            Assert.That(reusedRun.triedConvexB == 1 && reusedRun.reusedB == 1 && reusedRun.judgedB == 0, Is.True,
                "the stage asked for the two convexes' hulls once and read the kept judgment (asked " + reusedRun.triedConvexB + ", read " + reusedRun.reusedB + ", made " + reusedRun.judgedB + ")");
            AssertSame(fresh, freshRun, reused, reusedRun, "the stage on the kept set");

            // and the same through the managed bodies
            SpatialSplitGraph.GraphOutcome managed = SpatialSplitGraph.DecideSpan(p.Span(), again, false, out SpatialSplitGraph.NativeRun managedRun);
            AssertSame(fresh, freshRun, managed, managedRun, "the stage on the kept set, managed");

            kept.Dispose();
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(_live), "the kept set's areas were given back");
            Assert.That(kept.Usable, Is.False);
            SpatialSplitGraph.GraphOutcome after = SpatialSplitGraph.DecideSpan(p.Span(), again, true, out SpatialSplitGraph.NativeRun afterRun);
            Assert.That(afterRun.reusedInput, Is.False, "a disposed set is not read");
            AssertSame(fresh, freshRun, after, afterRun, "the stage after the set was disposed");
        }

        /// <summary>
        /// **Where the convexes are all held together, no graph places, and the check says so.** Two boxes that overlap: the hulls meet,
        /// the pair can never be certified apart, and a placement would have it across its sides. The check holds; the stage from the
        /// start indeed ends without a placement.
        /// </summary>
        [Test]
        public void WhereTheConvexesAreHeldTogether_TheCheckSaysSo_AndTheStageFromTheStartPlacesNothing()
        {
            Piece p = TwoBoxesThatMeet();
            SpatialSplitGraph.GraphOutcome fresh = SpatialSplitGraph.DecideSpan(p.Span(), Settings(), true, out SpatialSplitGraph.NativeRun _);
            Assert.That(fresh.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.NotFound), "the stage from the start places nothing (" + fresh.reason + ")");

            SpatialSplitGraph.Kept kept = Combination(p, out SpatialSplitGraph.GraphOutcome combination);
            Assert.That(combination.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.Separable));
            SpatialSplitGraph.LinkRun link = SpatialSplitGraph.ConvexesHeld(kept, Settings(), true);
            Assert.That(link.ran && link.held && link.groups == 1 && link.holding == 1, Is.True, link.reason);
            Assert.That(kept.judged[0] & SpatialSplitGraph.JudgedStateMask, Is.EqualTo(SpatialSplitGraph.JudgedNotApart));

            // asked again: nothing is judged twice
            SpatialSplitGraph.LinkRun second = SpatialSplitGraph.ConvexesHeld(kept, Settings(), false);
            Assert.That(second.held && second.judged == 0 && second.reused == 1, Is.True, second.reason);

            // nothing to check with
            Assert.That(SpatialSplitGraph.ConvexesHeld(null, Settings(), true).held, Is.False);
            kept.Dispose();
            Assert.That(SpatialSplitGraph.ConvexesHeld(kept, Settings(), true).held, Is.False, "a disposed set proves nothing");
        }

        /// <summary>
        /// **A pair at the iteration limit holds nothing, and the stage counts it as before.** With one GJK iteration the two slabs'
        /// pair is not settled: not a conclusion, so the check does not hold by it, and the stage on the kept set reads it as at the
        /// limit -- the same count of pairs at the limit as a stage from the start.
        /// </summary>
        [Test]
        public void APairAtTheIterationLimit_HoldsNothing_AndIsCountedAsBefore()
        {
            Piece p = TwoBoxesThatMeet();
            SpatialSplitGraph.GraphOutcome fresh = SpatialSplitGraph.DecideSpan(p.Span(), Settings(0, 1), true, out SpatialSplitGraph.NativeRun freshRun);
            Assert.That(fresh.gjkLimitHit, Is.GreaterThan(0), "the layout: pairs at the limit (" + fresh.reason + ")");

            SpatialSplitGraph.Kept kept = Combination(p, out SpatialSplitGraph.GraphOutcome _, 0, 1);
            SpatialSplitGraph.LinkRun link = SpatialSplitGraph.ConvexesHeld(kept, Settings(0, 1), true);
            Assert.That(link.ran && !link.held && link.atLimit == 1 && link.holding == 0, Is.True, "at the limit: " + link.reason);
            Assert.That(kept.judged[0] & SpatialSplitGraph.JudgedStateMask, Is.EqualTo(SpatialSplitGraph.JudgedAtLimit));
            Assert.That(SpatialSplitGraph.ConvexesHeld(kept, Settings(), true).ran, Is.False, "another iteration limit: the kept judgments are not read");

            SpatialSplitGraph.GraphSettings again = Settings(0, 1);
            again.reuse = kept;
            SpatialSplitGraph.GraphOutcome reused = SpatialSplitGraph.DecideSpan(p.Span(), again, true, out SpatialSplitGraph.NativeRun reusedRun);
            Assert.That(reusedRun.reusedInput && reusedRun.reusedB >= 1, Is.True);
            AssertSame(fresh, freshRun, reused, reusedRun, "the stage on the kept set, one iteration");

            // another iteration limit is another judgment: the set is not this decision's
            SpatialSplitGraph.GraphSettings other = Settings();
            other.reuse = kept;
            SpatialSplitGraph.DecideSpan(p.Span(), other, true, out SpatialSplitGraph.NativeRun otherRun);
            Assert.That(otherRun.reusedInput, Is.False);
        }

        /// <summary>
        /// **A budget stops the stage on the kept set where it stops one from the start.** Over a piece whose preparation and stage A
        /// take many thousand units, for each budget: the stage from the start, and the stage after a combination's decision under the
        /// same budget (on its kept set where that decision completed its preparation). The two end the same -- unfinished by the work
        /// at the same count, or with the same verdict.
        /// </summary>
        [Test]
        public void ABudget_StopsTheStageOnTheKeptSet_WhereItStopsOneFromTheStart()
        {
            Piece p = ManyInTwoSlabs(400);
            int reusedAndStopped = 0, reusedAndEnded = 0;
            foreach (long budget in new long[] { 20000, 60000, 100000, 200000, 400000, 3000000, 0 })
            {
                SpatialSplitGraph.GraphOutcome fresh = SpatialSplitGraph.DecideSpan(p.Span(), Settings(budget), true, out SpatialSplitGraph.NativeRun freshRun);
                SpatialSplitGraph.Kept kept = Combination(p, out SpatialSplitGraph.GraphOutcome combination, budget);
                SpatialSplitGraph.LinkRun link = SpatialSplitGraph.ConvexesHeld(kept, Settings(budget), true);
                Assert.That(link.held, Is.False);
                SpatialSplitGraph.GraphSettings again = Settings(budget);
                again.reuse = kept;
                SpatialSplitGraph.GraphOutcome reused = SpatialSplitGraph.DecideSpan(p.Span(), again, true, out SpatialSplitGraph.NativeRun reusedRun);
                TestContext.Out.WriteLine("budget " + budget + ": from the start " + fresh.verdict + " " + fresh.stop + " work " + fresh.work + "; the combination " + combination.verdict + " " + combination.stop
                    + " (kept " + kept.Usable + "); on the kept set " + reused.verdict + " " + reused.stop + " work " + reused.work + ", reused input " + reusedRun.reusedInput);
                Assert.That(reusedRun.reusedInput, Is.EqualTo(kept.Usable), "budget " + budget);
                AssertSame(fresh, freshRun, reused, reusedRun, "budget " + budget);
                if (reusedRun.reusedInput && reused.unfinished) reusedAndStopped++;
                if (reusedRun.reusedInput && !reused.unfinished) reusedAndEnded++;
                kept.Dispose();
            }

            Assert.That(reusedAndStopped, Is.GreaterThanOrEqualTo(1), "the layout: a budget the combination's decision passes and the stage does not");
            Assert.That(reusedAndEnded, Is.GreaterThanOrEqualTo(1), "the layout: a budget both pass");
        }

        /// <summary>
        /// **Another input reuses nothing.** A set kept of one piece is not read for another margin, other indices or another piece;
        /// each decision then runs from the start and ends as it does without the set.
        /// </summary>
        [Test]
        public void AnotherInput_ReusesNothing()
        {
            Piece p = TwoSlabs(), q = TwoSlabs();
            SpatialSplitGraph.Kept kept = Combination(p, out SpatialSplitGraph.GraphOutcome _);
            Assert.That(kept.Usable, Is.True);

            SpatialSplitGraph.GraphSettings again = Settings();
            again.reuse = kept;
            SpatialSplitGraph.GraphOutcome freshWide = SpatialSplitGraph.DecideSpan(p.Span(Margin * 2f), Settings(), true, out SpatialSplitGraph.NativeRun freshWideRun);
            SpatialSplitGraph.GraphOutcome wide = SpatialSplitGraph.DecideSpan(p.Span(Margin * 2f), again, true, out SpatialSplitGraph.NativeRun wideRun);
            Assert.That(wideRun.reusedInput, Is.False, "another margin");
            AssertSame(freshWide, freshWideRun, wide, wideRun, "another margin");

            SpatialSplitGraph.DecideSpan(q.Span(), again, true, out SpatialSplitGraph.NativeRun otherRun);
            Assert.That(otherRun.reusedInput, Is.False, "another piece (other memory), though its numbers are the same");

            again.workBudget = 5000000;
            SpatialSplitGraph.DecideSpan(p.Span(), again, true, out SpatialSplitGraph.NativeRun budgetRun);
            Assert.That(budgetRun.reusedInput, Is.False, "another budget");

            SpatialSplitGraph.GraphSettings measured = Settings();
            measured.reuse = kept;
            measured.measure = 2;
            SpatialSplitGraph.DecideSpan(p.Span(), measured, true, out SpatialSplitGraph.NativeRun measuredRun);
            Assert.That(measuredRun.reusedInput, Is.False, "a counting run is a full run");
        }
    }
}
