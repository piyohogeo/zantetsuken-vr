using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Editor.Sandbox;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using static Zantetsu.Core.Tests.SpatialSplitReferenceDeciderTests;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// GRAPH-CONNECT-2, unit 1 (the Runtime engine): the span input against the sample input, the caps and the work budget
    /// (Unfinished, never Invalid; the counted loops stop within the check interval), the metered union-find, the flow fixture
    /// that pushes many times in one phase, and the engine on a real Background worker with the main thread's preparation.
    /// </summary>
    public unsafe partial class SpatialSplitGraphCandidateNativeTests
    {
        private static SpatialSplitGraph.GraphSettings Graph(long workBudget = 0, int maxCandidates = 0, int maxNodes = 0, long maxGraphBytes = 0, int maxVertexSpan = 0, int maxTriangles = 0, int maxConvexes = 0, int guard = 0)
            => new SpatialSplitGraph.GraphSettings { secondsLimit = 0.0, nearestSelection = true, flowDinic = true, workBudget = workBudget, maxCandidates = maxCandidates, maxNodes = maxNodes, maxGraphBytes = maxGraphBytes, maxVertexSpan = maxVertexSpan, maxTriangles = maxTriangles, maxConvexes = maxConvexes, hashPrepare = true, testGuardBytes = guard };

        // The sample with its vertices spread over a wider view: id -> 3 + 7 * id, the indices mapped, the view as long as the greatest id + 1.
        private static SpatialSplitSample Spread(SpatialSplitSample s)
        {
            var ids = new uint[s.vertexIds.Length];
            uint greatest = 0;
            for (int i = 0; i < ids.Length; i++) { ids[i] = 3u + (7u * s.vertexIds[i]); greatest = System.Math.Max(greatest, ids[i]); }
            var map = new Dictionary<uint, uint>();
            for (int i = 0; i < ids.Length; i++) map[s.vertexIds[i]] = ids[i];
            var indices = new uint[s.indices.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = map[s.indices[i]];
            return new SpatialSplitSample
            {
                formatVersion = s.formatVersion, vertexStructBytes = s.vertexStructBytes, codeVersion = s.codeVersion, sessionId = s.sessionId, objectName = s.objectName + " (spread)", sha256 = s.sha256,
                world = s.world, attempt = s.attempt, margin = s.margin, supportEpsilon = s.supportEpsilon, anchorEpsilon = s.anchorEpsilon, vertexLimit = s.vertexLimit, parentMass = s.parentMass,
                limits = s.limits, cutConvexes = s.cutConvexes, bentSearch = s.bentSearch, bentLimits = s.bentLimits, maxConvexVertices = s.maxConvexVertices,
                geometryToOwner = s.geometryToOwner, shapeToOwner = s.shapeToOwner, submeshOffsets = s.submeshOffsets, submeshCounts = s.submeshCounts, materials = s.materials, anchors = s.anchors,
                indices = indices, vertexIds = ids, vertexBytes = s.vertexBytes, vertexViewLength = (int)greatest + 1, convexes = s.convexes,
            };
        }

        // Many nodes and candidates: t tiny triangles scattered inside two overlapping boxes, each triangle its own component; every
        // pair of nodes is a candidate by the boxes (the search then refutes or not: the verdict is not the point of these tests).
        private static SpatialSplitSample ManyComponents(int t)
        {
            var triangles = new List<float3[]>();
            for (int i = 0; i < t; i++)
            {
                float x = 0.1f + (0.8f * ((i * 37) % 101) / 101f), y = 0.1f + (0.8f * ((i * 53) % 97) / 97f), z = 0.1f + (0.8f * ((i * 71) % 89) / 89f);
                triangles.Add(Tri(x, y, z, x + 0.01f, y, z, x, y + 0.01f, z + 0.002f));
            }

            (float3[] p, int[] idx) = Mesh(triangles.ToArray());
            return Fixture(new[] { Unit, Box(0.5f, 0.5f, 0.5f, 1.5f, 1.5f, 1.5f) }, p, idx, name: "many components " + t);
        }

        // t tiny triangles, each its own component, all within a cube smaller than far (= 2m + 4E1, m = 2^-12): every pair of nodes is a
        // candidate, so the candidates (t (t - 1) / 2) exceed the lists' first capacity (4 x the nodes, rounded up) from t = 10 on.
        private static SpatialSplitSample DenseComponents(int t)
        {
            var triangles = new List<float3[]>();
            for (int i = 0; i < t; i++)
            {
                float x = 0.5f + (0.0003f * ((i * 37) % 101) / 101f), y = 0.5f + (0.0003f * ((i * 53) % 97) / 97f), z = 0.5f + (0.0003f * ((i * 71) % 89) / 89f);
                triangles.Add(Tri(x, y, z, x + 0.00001f, y, z, x, y + 0.00001f, z + 0.000002f));
            }

            (float3[] p, int[] idx) = Mesh(triangles.ToArray());
            return Fixture(new[] { Unit, Box(0.5f, 0.5f, 0.5f, 1.5f, 1.5f, 1.5f) }, p, idx, name: "dense components " + t);
        }

        [Test]
        public void TheSpanInput_GivesTheSamePreparationHashAndResult_AsTheSample_WhateverTheUnreferencedSlotsHold()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            int fixtures = 0;
            foreach (SpatialSplitSample sample in Fixtures())
            {
                fixtures++;
                var snapSample = new SpatialSplitGraph.PrepareSnapshot();
                SpatialSplitGraph.GraphOutcome a = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun ra, snapSample);
                Assert.That(a.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput), sample.objectName + ": " + a.reason);
                // the same vertices spread over a wider view; the gaps between them hold NaN, then huge values
                SpatialSplitSample spread = Spread(sample);
                foreach (float3 garbage in new[] { new float3(float.NaN), new float3(1e30f, -1e30f, 1e30f), new float3(float.PositiveInfinity) })
                {
                    var snapSpan = new SpatialSplitGraph.PrepareSnapshot();
                    SpatialSplitGraph.GraphOutcome b = SpatialSplitGraph.DecideAsSpan(spread, Graph(), true, out SpatialSplitGraph.NativeRun rb, snapSpan, garbage);
                    Assert.That(b.verdict, Is.EqualTo(a.verdict), sample.objectName + " (span): " + b.reason);
                    Assert.That(rb.prepareHash, Is.EqualTo(ra.prepareHash), sample.objectName + ": the candidate list and gaps are the same through the span input");
                    Assert.That(rb.referencedVertices, Is.EqualTo(sample.vertexIds.Length), sample.objectName + ": the referenced vertices are the named ones");
                    Assert.That(rb.vertexSpan, Is.GreaterThanOrEqualTo(sample.vertexIds.Length), sample.objectName + ": the span covers the ids");
                    // the preparation's outputs, apart from the corner slots and the owner coordinates' indexing, which follow the (same) compact order
                    Assert.That(snapSpan.nodes, Is.EqualTo(snapSample.nodes));
                    AssertBits(sample.objectName + ": owner", snapSample.owner, snapSpan.owner);
                    Assert.That(snapSpan.cornerSlot, Is.EqualTo(snapSample.cornerSlot), sample.objectName + ": corner slots");
                    Assert.That(snapSpan.triComponent, Is.EqualTo(snapSample.triComponent));
                    AssertBits(sample.objectName + ": bounds (the error bound reads the referenced vertices only)", snapSample.bounds, snapSpan.bounds);
                    AssertBits(sample.objectName + ": gaps", snapSample.gaps, snapSpan.gaps);
                    Assert.That(snapSpan.candidates, Is.EqualTo(snapSample.candidates));
                    if (a.verdict == SpatialSplitGraph.GraphVerdict.Separable)
                    {
                        Assert.That(b.convexPositive, Is.EqualTo(a.convexPositive), sample.objectName + ": the convex sides");
                        Assert.That(b.trianglePositive, Is.EqualTo(a.trianglePositive), sample.objectName + ": the triangle sides");
                    }

                    // the span's compaction is on the meter (work 3): an index three units over its scans and copy, a slot of the span one
                    Assert.That(b.work, Is.EqualTo(a.work + (3L * sample.indices.Length) + rb.vertexSpan), sample.objectName + ": the same work plus the compaction's (3 I + S)");
                }
            }

            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
            TestContext.Out.WriteLine(fixtures + " fixtures: the span input agrees with the sample input, with NaN, huge and infinite unreferenced slots");
        }

        [Test]
        public void TheSpanInput_TellsAMalformedInput_FromACapExceeded()
        {
            SpatialSplitSample sample = Spread(SpatialSplitGraph.WarmupSample());
            // malformed: an index outside the view -> InvalidInput, nothing allocated (a span input built by hand: the sample adapter
            // would size its view by the greatest index and so never see one outside it)
            long live = SpatialSplitGraph.LiveNativeAllocations;
            SpatialSplitGraph.CompactInput w = SpatialSplitGraph.WarmupInput();
            var view = new VpRenderVertex[w.local.Length];
            var badIndices = new uint[] { 0, 1, 2, 3, 4, 99 };
            SpatialSplitGraph.GraphOutcome x;
            SpatialSplitGraph.NativeRun rx;
            fixed (VpRenderVertex* vp = view)
            fixed (uint* ip = badIndices)
            {
                var span = new SpatialSplitGraph.SpanInput { vertices = vp, vertexViewLength = view.Length, indices = ip, indexCount = badIndices.Length, convexVertices = w.convexes, geometryToOwner = w.geometryToOwner, shapeToOwner = w.shapeToOwner, margin = w.margin };
                x = SpatialSplitGraph.DecideSpan(span, Graph(), false, out rx);
            }

            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput), x.reason);
            Assert.That(x.reason, Does.Contain("outside the view"));
            Assert.That(x.unfinished, Is.False);
            Assert.That(rx.nativeBytes, Is.Zero, "a malformed input allocates nothing");
            // malformed: an index count that is not a multiple of three
            SpatialSplitSample odd = Spread(SpatialSplitGraph.WarmupSample());
            odd.indices = new uint[] { odd.indices[0], odd.indices[1] };
            x = SpatialSplitGraph.DecideAsSpan(odd, Graph(), false, out _);
            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput));
            // over a cap: the span, the triangles, the convexes -> Undecided, unfinished, stop capacity, nothing allocated for the span cap
            x = SpatialSplitGraph.DecideAsSpan(sample, Graph(maxVertexSpan: 4), false, out rx);
            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided), x.reason);
            Assert.That(x.unfinished, Is.True);
            Assert.That(x.stop, Is.EqualTo("capacity"));
            Assert.That(x.reason, Does.Contain("span"));
            Assert.That(rx.nativeBytes, Is.Zero, "the span cap is checked before the marks are allocated");
            x = SpatialSplitGraph.DecideAsSpan(sample, Graph(maxTriangles: 1), false, out _);
            Assert.That(x.unfinished && x.stop == "capacity" && x.reason.Contains("triangle"), Is.True, x.reason);
            x = SpatialSplitGraph.DecideAsSpan(sample, Graph(maxConvexes: 1), false, out _);
            Assert.That(x.unfinished && x.stop == "capacity" && x.reason.Contains("convex"), Is.True, x.reason);
            x = SpatialSplitGraph.Decide(SpatialSplitGraph.WarmupSample(), Graph(maxTriangles: 1), false, out _);
            Assert.That(x.unfinished && x.stop == "capacity", Is.True, "the sample input applies the same caps");
            // a negative cap is a malformed setting
            x = SpatialSplitGraph.Decide(SpatialSplitGraph.WarmupSample(), new SpatialSplitGraph.GraphSettings { maxNodes = -1 }, false, out _);
            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
        }

        [Test]
        public void TheSpanInputsMainThreadAllocation_FailsInItsOwnBoundary_AsUnfinishedAllocation()
        {
            SpatialSplitSample spread = Spread(SpatialSplitGraph.WarmupSample());
            long live = SpatialSplitGraph.LiveNativeAllocations;
            // the first allocation is the marks: its failure is Unfinished(allocation), the marks are gone, no other area exists
            SpatialSplitGraph.GraphSettings s = Graph();
            s.testFailNativeAllocation = 0;
            SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.DecideAsSpan(spread, s, false, out SpatialSplitGraph.NativeRun rx);
            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided));
            Assert.That(x.unfinished, Is.True);
            Assert.That(x.stop, Is.EqualTo("allocation"));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "the marks were released");
            // every later allocation ordinal, failing in turn, is the same kind of ending and leaves nothing behind
            for (int failAt = 1; failAt < 40; failAt++)
            {
                s = Graph();
                s.testFailNativeAllocation = failAt;
                x = SpatialSplitGraph.DecideAsSpan(spread, s, failAt % 2 == 0, out _);
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "after the allocation " + (failAt + 1) + " failed, nothing is left behind");
                if (x.verdict != SpatialSplitGraph.GraphVerdict.Undecided) { Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Separable)); break; }
                Assert.That(x.stop, Is.EqualTo("allocation"));
            }
        }

        [Test]
        public void TheCaps_StopTheBody_BeforeAGrowthOrAnAllocationPastThem_AndAreNeverAMark()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            SpatialSplitSample sample = ManyComponents(40);
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun rf);
            Assert.That(free.verdict, Is.Not.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput), free.reason);
            Assert.That(free.unfinished, Is.False);
            Assert.That(free.candidatesA, Is.GreaterThan(16), "the fixture has more candidates than the smallest cap");
            // maxCandidates below the candidates: the lists never grow past the cap (the capacity recorded stays within it)
            SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(sample, Graph(maxCandidates: 16), true, out SpatialSplitGraph.NativeRun rx);
            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided), x.reason);
            Assert.That(x.unfinished && x.stop == "capacity", Is.True, x.reason);
            Assert.That(x.reason, Does.Contain("growth"));
            Assert.That(rx.candidateCapacity, Is.LessThanOrEqualTo(16), "no growth past the cap happened");
            Assert.That(rx.burstRan, Is.EqualTo(BurstCompiler.IsEnabled));
            // maxNodes below the nodes: stopped right after the components, before the node areas are written
            x = SpatialSplitGraph.Decide(sample, Graph(maxNodes: 2), true, out _);
            Assert.That(x.unfinished && x.stop == "capacity" && x.reason.Contains("node count"), Is.True, x.reason);
            Assert.That(x.meshComponents, Is.GreaterThan(0), "the components were counted before the stop");
            // maxGraphBytes below the fixed areas: nothing allocated; just below the whole: stopped at the lists or the search areas
            x = SpatialSplitGraph.Decide(sample, Graph(maxGraphBytes: 64), true, out rx);
            Assert.That(x.unfinished && x.stop == "capacity", Is.True, x.reason);
            Assert.That(rx.nativeBytes, Is.Zero, "the fixed areas were not allocated");
            x = SpatialSplitGraph.Decide(sample, Graph(maxGraphBytes: rf.nativeBytes - 1), true, out rx);
            Assert.That(x.unfinished && x.stop == "capacity", Is.True, x.reason);
            Assert.That(rx.nativeBytes, Is.LessThan(rf.nativeBytes));
            // enough room: the same result and hash as without caps
            x = SpatialSplitGraph.Decide(sample, Graph(maxGraphBytes: rf.nativeBytes * 2, maxCandidates: (int)rf.candidateCapacity, maxNodes: 64), true, out rx);
            Assert.That(x.verdict, Is.EqualTo(free.verdict), x.reason);
            Assert.That(x.unfinished, Is.False);
            Assert.That(rx.prepareHash, Is.EqualTo(rf.prepareHash));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }

        [Test]
        public void TheWorkBudget_StopsWithinTheCheckInterval_InEveryStage_AndWithoutABudgetTheCountsDoNotChangeTheResult()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            var samples = new List<SpatialSplitSample> { NineSeeds(), SpatialSplitGraph.WarmupSample(), ManyComponents(40), ManyComponents(120) };
            foreach (SpatialSplitSample s in Fixtures()) samples.Add(s);
            int stoppedInPrepare = 0, stoppedInSearch = 0, finished = 0;
            foreach (SpatialSplitSample sample in samples)
            {
                SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun rf);
                if (free.verdict == SpatialSplitGraph.GraphVerdict.InvalidInput) continue;
                Assert.That(free.work, Is.EqualTo(free.workPrepare + free.workSearch), sample.objectName + ": the work is the two bodies'");
                Assert.That(free.work, Is.GreaterThan(0));
                SpatialSplitGraph.GraphOutcome managed = SpatialSplitGraph.Decide(sample, Graph(), false, out _);
                Assert.That(managed.work, Is.EqualTo(free.work), sample.objectName + ": the same count through Burst and as managed code");
                foreach (long budget in new long[] { 1, 10, 100, 500, 2000, 5000, 20000, 100000 })
                {
                    SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(sample, Graph(workBudget: budget, guard: 256), true, out SpatialSplitGraph.NativeRun rx);
                    // the budget is compared at the check points (multiples of CheckEvery): a decision whose work never reaches the first check
                    // point past the budget completes as without one
                    long firstCheckPastBudget = ((budget / SpatialSplitGraph.WorkMeter.CheckEvery) + 1) * SpatialSplitGraph.WorkMeter.CheckEvery;
                    if (free.work < firstCheckPastBudget)
                    {
                        Assert.That(x.verdict, Is.EqualTo(free.verdict), sample.objectName + " budget " + budget + ": a budget the work fits in changes nothing");
                        Assert.That(rx.prepareHash, Is.EqualTo(rf.prepareHash));
                        Assert.That(x.work, Is.EqualTo(free.work));
                        finished++;
                        continue;
                    }

                    // the budget passed: Undecided, unfinished, stop work; the counted work past the budget is at most the check interval and one unit
                    Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided), sample.objectName + " budget " + budget + ": " + x.reason);
                    Assert.That(x.unfinished && x.stop == "work", Is.True, x.reason);
                    Assert.That(x.work, Is.GreaterThan(budget), sample.objectName + " budget " + budget + ": it stopped because the budget passed");
                    Assert.That(x.work, Is.LessThanOrEqualTo(firstCheckPastBudget + 1), sample.objectName + " budget " + budget + ": the work past the budget is bounded by the check interval (" + firstCheckPastBudget + " + 1)");
                    Assert.That(x.work, Is.EqualTo(x.workPrepare + x.workSearch));
                    if (x.workSearch == 0) stoppedInPrepare++; else stoppedInSearch++;
                }
            }

            Assert.That(stoppedInPrepare, Is.GreaterThan(0), "some budgets stop inside the preparation");
            Assert.That(stoppedInSearch, Is.GreaterThan(0), "some budgets stop inside the search");
            Assert.That(finished, Is.GreaterThan(0));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
            TestContext.Out.WriteLine("budgets: stopped in the preparation " + stoppedInPrepare + ", in the search " + stoppedInSearch + ", finished " + finished);
        }

        [Test]
        public void TheMeteredUnionFind_CountsEveryParentReference_OnADeepChain()
        {
            // a chain 0 <- 1 <- 2 <- ... <- depth: the root of the deepest node costs as many units as the loop iterates, plus one
            foreach (int depth in new[] { 1, 2, 7, 64, 1000 })
            {
                var a = new NativeArray<int>(depth + 1, Allocator.Temp);
                var expected = new int[depth + 1];
                for (int i = 0; i <= depth; i++) { a[i] = i == 0 ? 0 : i - 1; expected[i] = a[i]; }
                // the same loop, counted independently
                int iterations = 0, k = depth;
                while (expected[k] != k) { expected[k] = expected[expected[k]]; k = expected[k]; iterations++; }
                var meter = SpatialSplitGraph.WorkMeter.Of(0);
                int root = SpatialSplitGraph.FindRoot((int*)a.GetUnsafePtr(), 0, depth, ref meter);
                Assert.That(root, Is.Zero);
                Assert.That(meter.work, Is.EqualTo(iterations + 1), "depth " + depth + ": a unit a parent reference");
                Assert.That(iterations, Is.GreaterThanOrEqualTo(depth / 2), "path halving still walks about half the chain");
                a.Dispose();
            }
        }

        [Test]
        public void TheFlowKernel_StopsInsideAPhase_WhereManyUnitBranchesPushAlongOneLongPath()
        {
            // src -> a chain of L nodes -> a hub -> B unit branches -> dst: one Dinic phase pushes B times, each over L + 2 edges, each
            // push walking the path twice (the least capacity, the residual update): about 2 B (L + 2) units in one phase
            const int L = 200, B = 100;
            int hub = L, first = L + 1, m = L + 1 + B;
            var g = new SmallGraph { m = m, source = new[] { 0 }, sink = new int[B] };
            for (int i = 0; i < L; i++) g.groups.Add((i, i + 1, true));          // the chain 0 - 1 - ... - hub, blocked (Inf): the cut is at the branches
            for (int b = 0; b < B; b++) { g.groups.Add((hub, first + b, false)); g.sink[b] = first + b; }
            var bd = Build(g);
            var full = SpatialSplitGraph.WorkMeter.Of(0);
            var counts = new SpatialSplitGraph.FlowCounts();
            int flow = SpatialSplitGraph.FlowKernel.Dinic((int*)bd.work.GetUnsafePtr(), bd.adjStart, bd.arcTo, bd.arcRev, bd.arcCap, bd.queue, bd.level, bd.cur, bd.pathNode, bd.pathArc, bd.nodes, g.m, g.m + 1, true, ref counts, ref full);
            Assert.That(flow, Is.EqualTo(B), "every branch carries one unit");
            Assert.That(counts.levelBuilds, Is.EqualTo(2), "all pushes in one phase (the second level build finds no path)");
            Assert.That(counts.pushes, Is.EqualTo(B));
            Assert.That(full.work, Is.GreaterThan(2L * B * (L + 2)), "the path updates alone cost more than a phase boundary would have bounded");
            bd.work.Dispose();
            // the same network with a budget inside that phase: stopped mid-phase, within the interval, fewer pushes than branches
            bd = Build(g);
            long budget = SpatialSplitGraph.WorkMeter.CheckEvery;
            var metered = SpatialSplitGraph.WorkMeter.Of(budget);
            counts = new SpatialSplitGraph.FlowCounts();
            SpatialSplitGraph.FlowKernel.Dinic((int*)bd.work.GetUnsafePtr(), bd.adjStart, bd.arcTo, bd.arcRev, bd.arcCap, bd.queue, bd.level, bd.cur, bd.pathNode, bd.pathArc, bd.nodes, g.m, g.m + 1, true, ref counts, ref metered);
            Assert.That(metered.Stopped, Is.True);
            Assert.That(metered.reason, Is.EqualTo(SpatialSplitGraph.StopWork));
            Assert.That(metered.work, Is.GreaterThan(budget));
            Assert.That(metered.work, Is.LessThanOrEqualTo(budget + SpatialSplitGraph.WorkMeter.CheckEvery + 1));
            Assert.That(counts.levelBuilds, Is.EqualTo(1), "stopped inside the first phase");
            Assert.That(counts.pushes, Is.LessThan(B), "stopped before every branch was pushed");
            bd.work.Dispose();
        }

        [Test]
        public void AStopAnywhere_LeavesEveryAreaWithinItsBounds_AndNothingHalfBuiltIsUsed()
        {
            // Every budget from 1 to the whole work, in steps, on an input that builds and cuts many groups (the minimum cut's
            // construction included): each stopped run ends Unfinished(work) within the check interval, writes nothing past any
            // search area (the guard bytes after each area stay intact: a write through a half-initialised index would hit them or
            // crash), and releases everything. Burst and managed alike.
            SpatialSplitGraph.Warmup(0, true, true, false);
            foreach (SpatialSplitSample sample in new[] { ManyComponents(60), ManyComponents(200), NineSeeds() })
            {
                SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.Decide(sample, Graph(guard: 1024), true, out _);
                Assert.That(free.unfinished, Is.False, free.reason);
                long step = System.Math.Max(1, free.work / 97);
                int stopped = 0;
                for (long budget = 1; budget < free.work; budget += step)
                {
                    foreach (bool burst in new[] { true, false })
                    {
                        long live = SpatialSplitGraph.LiveNativeAllocations;
                        SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(sample, Graph(workBudget: budget, guard: 1024), burst, out _);
                        Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), sample.objectName + " budget " + budget + ": everything released");
                        long firstCheck = ((budget / SpatialSplitGraph.WorkMeter.CheckEvery) + 1) * SpatialSplitGraph.WorkMeter.CheckEvery;
                        if (free.work < firstCheck)
                        {
                            Assert.That(x.verdict, Is.EqualTo(free.verdict), sample.objectName + " budget " + budget);
                            continue;
                        }

                        Assert.That(x.unfinished && x.stop == "work", Is.True, sample.objectName + " budget " + budget + " (" + (burst ? "Burst" : "managed") + "): " + x.reason);
                        Assert.That(x.work, Is.LessThanOrEqualTo(firstCheck + 1), sample.objectName + " budget " + budget + ": the work past the budget is bounded");
                        stopped++;
                    }
                }

                if (free.work >= SpatialSplitGraph.WorkMeter.CheckEvery) Assert.That(stopped, Is.GreaterThan(0), sample.objectName + ": the sweep stopped somewhere");
                else Assert.That(stopped, Is.Zero, sample.objectName + ": under the first check point nothing stops");
                TestContext.Out.WriteLine(sample.objectName + ": " + stopped + " stopped runs over budgets 1.." + free.work + " by " + step + ", every guard intact");
            }
        }

        [Test]
        public void TheMeteredUnionFind_LeavesTheChain_WhenTheBudgetStops()
        {
            // a chain of depth 20,000 under a budget of 1: the walk ends at the first check point past the budget, not at the root
            const int depth = 20000;
            var a = new NativeArray<int>(depth + 1, Allocator.Temp);
            for (int i = 0; i <= depth; i++) a[i] = i == 0 ? 0 : i - 1;
            var meter = SpatialSplitGraph.WorkMeter.Of(1);
            int root = SpatialSplitGraph.FindRoot((int*)a.GetUnsafePtr(), 0, depth, ref meter);
            Assert.That(meter.Stopped, Is.True);
            Assert.That(meter.work, Is.LessThanOrEqualTo(SpatialSplitGraph.WorkMeter.CheckEvery + 1), "the walk stopped within the check interval");
            Assert.That(root, Is.InRange(0, depth), "what it returns is a node of the chain, inside the area");
            a.Dispose();
            // the same chain inside a preparation: the union-find of the triangles over a strip of shared vertices under a small budget
            SpatialSplitSample strip = Strip(3000);
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.Decide(strip, Graph(guard: 1024), true, out _);
            Assert.That(free.unfinished, Is.False, free.reason);
            SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(strip, Graph(workBudget: 1, guard: 1024), true, out _);
            Assert.That(x.unfinished && x.stop == "work", Is.True, x.reason);
            Assert.That(x.workSearch, Is.Zero, "stopped in the preparation");
            Assert.That(x.work, Is.LessThanOrEqualTo(SpatialSplitGraph.WorkMeter.CheckEvery + 1));
        }

        // A strip of t triangles sharing vertices in a chain (each triangle shares an edge with the next): one mesh component, a deep union-find.
        private static SpatialSplitSample Strip(int t)
        {
            var positions = new List<float3>();
            var indices = new List<int>();
            for (int i = 0; i <= t + 1; i++) positions.Add(new float3(0.1f + (0.8f * i / (t + 1)), (i % 2) * 0.01f + 0.5f, 0.5f));
            for (int i = 0; i < t; i++) { indices.Add(i); indices.Add(i + 1); indices.Add(i + 2); }
            return Fixture(new[] { Unit, Box(0.5f, 0.5f, 0.5f, 1.5f, 1.5f, 1.5f) }, positions.ToArray(), indices.ToArray(), name: "strip " + t);
        }

        [Test]
        public void TheCandidateCap_IsHonoured_AtTheBoundary_WithNoRoom_AndForANonPowerOfTwo()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            SpatialSplitSample sample = ManyComponents(40);
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun rf);
            Assert.That(free.unfinished, Is.False, free.reason);
            Assert.That(free.candidatesA, Is.GreaterThan(16));
            // exactly the capacity the lists end with: no growth is needed, the result is the same
            SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(sample, Graph(maxCandidates: (int)rf.candidateCapacity), true, out SpatialSplitGraph.NativeRun rx);
            Assert.That(x.unfinished, Is.False, x.reason);
            Assert.That(rx.prepareHash, Is.EqualTo(rf.prepareHash));
            Assert.That(rx.candidateCapacity, Is.LessThanOrEqualTo(rf.candidateCapacity));
            // a non-power-of-two cap under the candidates: the first capacity is the largest power of two within it, the growth past it is refused
            x = SpatialSplitGraph.Decide(sample, Graph(maxCandidates: 17), true, out rx);
            Assert.That(x.unfinished && x.stop == "capacity", Is.True, x.reason);
            Assert.That(rx.candidateCapacityInitial, Is.EqualTo(16), "the first capacity stays within the cap (NativeList would have rounded 17 up to 32)");
            Assert.That(rx.candidateCapacity, Is.EqualTo(16), "no growth past the cap");
            // a cap too small for any list: refused before the lists exist
            x = SpatialSplitGraph.Decide(sample, Graph(maxCandidates: 3), true, out rx);
            Assert.That(x.unfinished && x.stop == "capacity", Is.True, x.reason);
            Assert.That(rx.candidateCapacity, Is.Zero, "no list was created");
            // no room left by the byte cap for the lists (the fixed areas just fit): a computed room of 0 is 'no room', not 'no cap'
            long fixedOnly = rf.nativeBytes;   // the whole run's bytes; find the fixed areas' size by a cap that refuses the lists
            SpatialSplitGraph.GraphOutcome probe = SpatialSplitGraph.Decide(sample, Graph(maxGraphBytes: 1), true, out SpatialSplitGraph.NativeRun rp);
            Assert.That(probe.reason, Does.Contain("would exceed maxGraphBytes"), "the first area refused (the compact arrays, since the compaction allocates them)");
            // step the byte cap up until the fixed areas fit: the next refusal must be the lists (room 0..7 pairs), never a run without a cap
            long bytesCap = 1;
            string lastReason = probe.reason;
            for (int i = 0; i < 64 && lastReason.Contains("fixed areas"); i++)
            {
                bytesCap *= 2;
                probe = SpatialSplitGraph.Decide(sample, Graph(maxGraphBytes: bytesCap), true, out rp);
                lastReason = probe.reason;
            }

            Assert.That(probe.unfinished && probe.stop == "capacity", Is.True, "the byte cap bites somewhere after the fixed areas: " + probe.reason);
            Assert.That(lastReason, Does.Not.Contain("fixed areas"));
            // the whole decision fits in twice its bytes
            x = SpatialSplitGraph.Decide(sample, Graph(maxGraphBytes: fixedOnly * 2), true, out rx);
            Assert.That(x.unfinished, Is.False, x.reason);
            Assert.That(rx.prepareHash, Is.EqualTo(rf.prepareHash));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }

        [Test]
        public void TheCountCaps_RefuseBeforeAnyAllocation_EvenWhenEveryAllocationWouldFail()
        {
            SpatialSplitSample spread = Spread(SpatialSplitGraph.WarmupSample());
            foreach (bool asSpan in new[] { false, true })
            {
                SpatialSplitGraph.GraphSettings s = Graph(maxTriangles: 1);
                s.testFailNativeAllocation = 0;   // the very first allocation would fail: it is never reached
                SpatialSplitGraph.GraphOutcome x = asSpan ? SpatialSplitGraph.DecideAsSpan(spread, s, false, out SpatialSplitGraph.NativeRun rx) : SpatialSplitGraph.Decide(spread, s, false, out rx);
                Assert.That(x.unfinished, Is.True);
                Assert.That(x.stop, Is.EqualTo("capacity"), (asSpan ? "span" : "sample") + ": " + x.reason);
                Assert.That(rx.nativeBytes, Is.Zero);
                Assert.That(rx.vertexSpan, Is.Zero, "the span was not even scanned");
                s = Graph(maxConvexes: 1);
                s.testFailNativeAllocation = 0;
                x = asSpan ? SpatialSplitGraph.DecideAsSpan(spread, s, false, out rx) : SpatialSplitGraph.Decide(spread, s, false, out rx);
                Assert.That(x.stop, Is.EqualTo("capacity"), x.reason);
                Assert.That(rx.nativeBytes, Is.Zero);
            }
        }

        [Test]
        public void TheTimeLimit_EndsAsUnfinishedTime_AndOtherExceptionsAreNotAllocations()
        {
            SpatialSplitGraph.GraphSettings s = Graph();
            s.secondsLimit = 1e-12;
            SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(SpatialSplitGraph.WarmupSample(), s, false, out SpatialSplitGraph.NativeRun rx);
            Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided));
            Assert.That(x.unfinished && x.stop == "time" && x.timedOut, Is.True, x.reason);
            Assert.That(rx.unfinished, Is.True);
            // a guard violation is an implementation error and propagates as such (nothing turns it into Unfinished)
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }

        [Test]
        public void AListThatFills_IsGrownByTheDriver_AndItsFailedGrowth_IsUnfinishedAllocation_WithEverythingReleased()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            // 200 dense components + the 2 boxes = 202 nodes: 20,301 candidate pairs, far over the first capacity (4 x the nodes = 808,
            // rounded to 1,024): the lists grow
            SpatialSplitSample sample = DenseComponents(200);
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun rf);
            Assert.That(free.unfinished, Is.False, free.reason);
            Assert.That(free.candidatesA, Is.EqualTo(202 * 201 / 2), "every pair of the 202 nodes is a candidate");
            Assert.That(rf.listGrowths, Is.GreaterThan(0), "the lists were grown by the driver");
            Assert.That(rf.candidateCapacity, Is.GreaterThan(rf.candidateCapacityInitial));
            Assert.That(free.candidatesA, Is.LessThanOrEqualTo(rf.candidateCapacity));
            // the same decision as managed code: the same growths, the same result
            SpatialSplitGraph.GraphOutcome managed = SpatialSplitGraph.Decide(sample, Graph(), false, out SpatialSplitGraph.NativeRun rm);
            Assert.That(rm.listGrowths, Is.EqualTo(rf.listGrowths));
            Assert.That(rm.prepareHash, Is.EqualTo(rf.prepareHash));
            Assert.That(managed.work, Is.EqualTo(free.work), "the work counts the re-runs the same way");
            // every allocation ordinal failing in turn -- the grown lists among them -- ends as Unfinished(allocation) with nothing left behind
            long live = SpatialSplitGraph.LiveNativeAllocations;
            int growthFailures = 0;
            long workAtFirstGrowth = -1, workAtSecondGrowth = -1;
            for (int failAt = 0; failAt < rf.allocations; failAt++)
            {
                SpatialSplitGraph.GraphSettings s = Graph();
                s.testFailNativeAllocation = failAt;
                SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.Decide(sample, s, failAt % 2 == 0, out SpatialSplitGraph.NativeRun rx);
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "after the allocation " + (failAt + 1) + " failed, nothing is left behind");
                Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided), "allocation " + (failAt + 1) + ": " + x.reason);
                Assert.That(x.unfinished && x.stop == "allocation", Is.True, x.reason);
                Assert.That(rx.work, Is.EqualTo(x.work));
                if (rx.listGrowths > 0)
                {
                    // the failure landed on a growth's allocation: the preparation had run that many times, and its work and time are returned
                    growthFailures++;
                    // ... and so is the execution state, recorded right after the body ran and before the growth: ran, through Burst in the
                    // Burst runs (when Burst is enabled) and as managed code in the others (GRAPH-CONNECT-7 review 2)
                    Assert.That(rx.bodiesRan, Is.True, "allocation " + (failAt + 1) + ": the body had run before the failed growth");
                    Assert.That(rx.burstRan, Is.EqualTo(failAt % 2 == 0 && BurstCompiler.IsEnabled), "allocation " + (failAt + 1) + ": the mode the body ran in");
                    Assert.That(x.workPrepare, Is.GreaterThan(0), "allocation " + (failAt + 1) + ": the work of the runs before the failed growth");
                    Assert.That(x.work, Is.EqualTo(x.workPrepare));
                    Assert.That(x.work, Is.LessThan(free.work));
                    Assert.That(rx.secondsPrepare, Is.GreaterThan(0.0));
                    if (rx.listGrowths == 1 && workAtFirstGrowth < 0) workAtFirstGrowth = x.workPrepare;
                    if (rx.listGrowths == 2 && workAtSecondGrowth < 0) workAtSecondGrowth = x.workPrepare;
                }
                else
                {
                    Assert.That(x.work, Is.Zero, "allocation " + (failAt + 1) + ": before the first run, no work");
                    Assert.That(rx.bodiesRan, Is.False, "allocation " + (failAt + 1) + ": no body had run");
                    Assert.That(rx.burstRan, Is.False);
                }
            }

            Assert.That(growthFailures, Is.GreaterThan(0), "some failures landed on the grown lists");
            Assert.That(workAtFirstGrowth, Is.GreaterThan(0));
            Assert.That(workAtSecondGrowth, Is.GreaterThan(workAtFirstGrowth), "the second growth comes after more work than the first");
            // a real out-of-memory at a growth is the same ending
            SpatialSplitGraph.GraphSettings oom = Graph();
            oom.testFailNativeAllocation = rf.allocations - 1; oom.testFailWithOutOfMemory = true;
            SpatialSplitGraph.GraphOutcome y = SpatialSplitGraph.Decide(sample, oom, true, out _);
            Assert.That(y.unfinished && y.stop == "allocation", Is.True, y.reason);
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
            // a cap met at a growth is capacity, not allocation, and the lists never grew past it
            SpatialSplitGraph.GraphOutcome z = SpatialSplitGraph.Decide(sample, Graph(maxCandidates: (int)rf.candidateCapacityInitial), true, out SpatialSplitGraph.NativeRun rz);
            Assert.That(z.unfinished && z.stop == "capacity", Is.True, z.reason);
            Assert.That(rz.candidateCapacity, Is.EqualTo(rf.candidateCapacityInitial));
            Assert.That(rz.listGrowths, Is.Zero);
            Assert.That(rz.bodiesRan && rz.burstRan == BurstCompiler.IsEnabled, Is.True, "the body ran up to the cap; its mode is recorded");
            // the body stopped at the same point for the cap as for the first growth: the same work, now returned by both endings
            Assert.That(z.workPrepare, Is.EqualTo(workAtFirstGrowth), "the capacity stop and the failed first growth report the same work");
        }

        [Test]
        public void AGrowingInput_KeepsItsResultCounts_WhateverTheMeasure_AndThePairMode()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            SpatialSplitGraph.Warmup(0, true, true, true);
            SpatialSplitSample sample = DenseComponents(200);
            foreach (bool sweep in new[] { false, true })
            {
                var outcomes = new List<SpatialSplitGraph.GraphOutcome>();
                var runs = new List<SpatialSplitGraph.NativeRun>();
                foreach ((bool burst, int measure) in new[] { (true, 0), (true, 1), (true, 2), (false, 0), (false, 2) })
                {
                    SpatialSplitGraph.GraphSettings s = Graph();
                    s.pairsSweep = sweep; s.measure = measure;
                    outcomes.Add(SpatialSplitGraph.Decide(sample, s, burst, out SpatialSplitGraph.NativeRun run));
                    runs.Add(run);
                }

                string mode = sweep ? "sweep" : "all pairs";
                SpatialSplitGraph.GraphOutcome o0 = outcomes[0];
                Assert.That(o0.unfinished, Is.False, o0.reason);
                Assert.That(o0.nodePairs, Is.EqualTo(202 * 201 / 2), mode + ": the 202 nodes' pairs");
                Assert.That(o0.candidatesA, Is.EqualTo(o0.nodePairs), mode + ": every pair a candidate");
                Assert.That(runs[0].listGrowths, Is.GreaterThan(0), mode + ": the lists grew");
                for (int k = 1; k < outcomes.Count; k++)
                {
                    string which = mode + " [" + (runs[k].burstRan ? "N1" : "N0") + " measure " + runs[k].measure + "]";
                    // the result counts and the hash are those of the final run, the same whatever the measure and the engine
                    Assert.That(outcomes[k].verdict, Is.EqualTo(o0.verdict), which);
                    Assert.That(outcomes[k].candidatesA, Is.EqualTo(o0.candidatesA), which + ": candidates");
                    Assert.That(outcomes[k].nodePairs, Is.EqualTo(o0.nodePairs), which + ": pairs");
                    Assert.That(outcomes[k].meshComponents, Is.EqualTo(o0.meshComponents), which + ": components");
                    Assert.That(runs[k].prepareHash, Is.EqualTo(runs[0].prepareHash), which + ": the candidate list and gaps");
                    Assert.That(runs[k].listGrowths, Is.EqualTo(runs[0].listGrowths), which + ": the same growths");
                    Assert.That(runs[k].candidateCapacity, Is.EqualTo(runs[0].candidateCapacity), which);
                    // the work counts the re-runs the same way whatever the measure: cumulative over the growths
                    Assert.That(outcomes[k].workPrepare, Is.EqualTo(o0.workPrepare), which + ": the preparation's work");
                    Assert.That(outcomes[k].work, Is.EqualTo(o0.work), which + ": the work");
                }

                // measure 0 reads no clock; measure 1's sections (cumulative over the re-runs, like the time) stay within the preparation's time
                foreach (double ms in runs[0].prepareSectionMs) Assert.That(ms, Is.Zero, mode + ": no section time with the measurement off");
                double sum = 0.0;
                foreach (double ms in runs[1].prepareSectionMs) { Assert.That(ms, Is.GreaterThanOrEqualTo(0.0)); sum += ms; }
                Assert.That(sum, Is.LessThanOrEqualTo((runs[1].secondsPrepare * 1000.0 * 1.05) + 0.01), mode + ": the prepare sections do not exceed the preparation's time");
                // measure 2's counts describe the final run only (not the re-runs): the identities the Editor engine's tests require
                foreach (int k in new[] { 2, 4 })
                {
                    long[] pc = runs[k].prepareCounters;
                    long Pc(int slot) => pc[slot - SpatialSplitGraph.PcNamed];
                    string which = mode + " counts [" + (runs[k].burstRan ? "N1" : "N0") + "]";
                    Assert.That(Pc(SpatialSplitGraph.PcTriangles), Is.EqualTo(200), which + ": triangles counted once");
                    Assert.That(Pc(SpatialSplitGraph.PcComponents), Is.EqualTo(o0.meshComponents), which + ": components counted once");
                    Assert.That(Pc(SpatialSplitGraph.PcNodes), Is.EqualTo(202), which + ": nodes counted once");
                    Assert.That(Pc(SpatialSplitGraph.PcPairsTotal), Is.EqualTo(o0.nodePairs), which + ": the theoretical pairs, once");
                    Assert.That(Pc(SpatialSplitGraph.PcCandidates), Is.EqualTo(o0.candidatesA), which + ": candidates counted once");
                    Assert.That(Pc(SpatialSplitGraph.PcApart) + Pc(SpatialSplitGraph.PcCandidates), Is.EqualTo(o0.nodePairs), which + ": apart + candidates = pairs");
                    Assert.That(Pc(SpatialSplitGraph.PcGapEvals) + Pc(SpatialSplitGraph.PcSweepSkipped), Is.EqualTo(o0.nodePairs), which + ": evaluated + skipped = every pair");
                    Assert.That(Pc(SpatialSplitGraph.PcGapExcluded) + Pc(SpatialSplitGraph.PcCandidates), Is.EqualTo(Pc(SpatialSplitGraph.PcGapEvals)), which + ": evaluated = excluded + candidates");
                    Assert.That(Pc(SpatialSplitGraph.PcKeysSorted), Is.EqualTo(sweep ? o0.candidatesA : 0), which + ": keys sorted");
                    Assert.That(Pc(SpatialSplitGraph.PcCandGrowths), Is.EqualTo(runs[0].listGrowths), which + ": the growths are the driver's");
                    Assert.That(Pc(SpatialSplitGraph.PcGapGrowths), Is.EqualTo(runs[0].listGrowths), which);
                    Assert.That(Pc(SpatialSplitGraph.PcCandGrowthOld), Is.GreaterThanOrEqualTo(runs[0].candidateCapacityInitial), which + ": the capacities released");
                }
            }

            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }

        [Test]
        public void APreparationThatFails_LeavesTheEngineUnprepared_AndTheWorkerRefusesWithoutAllocating()
        {
            try
            {
                // the preparation's own allocation fails: not prepared, and the flag says so (from an unprepared process)
                SpatialSplitGraph.ResetPreparedForTests();
                Assert.That(SpatialSplitGraph.PrepareForTests(2), Is.False, "a preparation whose allocation failed is not a preparation");
                Assert.That(SpatialSplitGraph.IsPrepared, Is.False);
                using (var pool = new WorkerPoolExecutor(WorkDestination.BackgroundPool, 1, ThreadPriority.Lowest, 1))
                {
                    var refused = new GraphWork { sample = NineSeeds(), settings = Graph() };
                    refused.settings.requirePrepared = true;
                    Assert.That(pool.TryAccept(refused), Is.True);
                    Assert.That(refused.done.Wait(20000), Is.True);
                    Assert.That(refused.outcome.unfinished && refused.outcome.stop == "notPrepared", Is.True, refused.outcome.reason);
                    Assert.That(refused.run.nativeBytes, Is.Zero, "nothing was allocated on the worker");
                    Assert.That(refused.run.allocations, Is.Zero);
                    int taken = 0;
                    for (int i = 0; i < 200 && taken < 1; i++) { if (pool.TryTakeFinished(out _, out _)) taken++; else Thread.Sleep(10); }
                    Assert.That(taken, Is.EqualTo(1));
                    Assert.That(pool.StopAndConfirm(10000), Is.True);
                }

                // a preparation that succeeds: both bodies reached the verdict
                Assert.That(SpatialSplitGraph.Prepare(), Is.True);
                Assert.That(SpatialSplitGraph.IsPrepared, Is.True);
                // a later failure (another world's preparation) does not revoke it (GRAPH-CONNECT-4 §3-10)
                Assert.That(SpatialSplitGraph.PrepareForTests(2), Is.False);
                Assert.That(SpatialSplitGraph.IsPrepared, Is.True, "a preparation that succeeded stands");
            }
            finally
            {
                SpatialSplitGraph.Prepare();
            }

            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }

        private sealed class GraphWork : IDispatchWork
        {
            public SpatialSplitSample sample;
            public SpatialSplitGraph.GraphSettings settings;
            public SpatialSplitGraph.GraphOutcome outcome;
            public SpatialSplitGraph.NativeRun run;
            public int threadId;
            public readonly ManualResetEventSlim done = new ManualResetEventSlim();
            public void Begin() { try { threadId = Thread.CurrentThread.ManagedThreadId; outcome = SpatialSplitGraph.Decide(sample, settings, true, out run); } finally { done.Set(); } }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { }
        }

        [Test]
        public void TheEngine_RunsOnARealBackgroundWorker_ThroughBurst_OnlyAfterTheMainThreadPrepared()
        {
            Assert.That(SpatialSplitGraph.Prepare(), Is.True);
            Assert.That(SpatialSplitGraph.IsPrepared, Is.True);
            int main = Thread.CurrentThread.ManagedThreadId;
            SpatialSplitSample sample = NineSeeds();
            SpatialSplitGraph.GraphOutcome onMain = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun rm);
            using (var pool = new WorkerPoolExecutor(WorkDestination.BackgroundPool, 1, ThreadPriority.Lowest, 2))
            {
                // prepared: the bodies run on the worker, through Burst when it is enabled, with the main thread's result
                var work = new GraphWork { sample = sample, settings = Graph() };
                work.settings.requirePrepared = true;
                Assert.That(pool.TryAccept(work), Is.True);
                Assert.That(work.done.Wait(20000), Is.True, "the worker ran the work");
                Assert.That(work.threadId, Is.Not.EqualTo(main), "it ran off the main thread");
                Assert.That(work.outcome.verdict, Is.EqualTo(onMain.verdict), work.outcome.reason);
                Assert.That(work.run.prepareHash, Is.EqualTo(rm.prepareHash));
                Assert.That(work.run.burstRan, Is.EqualTo(BurstCompiler.IsEnabled), "the Burst direct call ran on the worker iff Burst is enabled");
                Assert.That(work.outcome.work, Is.EqualTo(onMain.work));
                // not prepared: the contract refuses before any area is touched; the bodies did not run
                SpatialSplitGraph.ResetPreparedForTests();
                try
                {
                    var refused = new GraphWork { sample = sample, settings = Graph() };
                    refused.settings.requirePrepared = true;
                    Assert.That(pool.TryAccept(refused), Is.True);
                    Assert.That(refused.done.Wait(20000), Is.True);
                    Assert.That(refused.outcome.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.Undecided));
                    Assert.That(refused.outcome.unfinished, Is.True);
                    Assert.That(refused.outcome.stop, Is.EqualTo("notPrepared"));
                    Assert.That(refused.outcome.work, Is.Zero, "the bodies did not run");
                    Assert.That(refused.run.nativeBytes, Is.Zero, "nothing was allocated");
                }
                finally
                {
                    SpatialSplitGraph.Prepare();
                }

                // collected on the main thread (the pool's contract), once each
                int taken = 0;
                for (int i = 0; i < 200 && taken < 2; i++)
                {
                    if (pool.TryTakeFinished(out _, out WorkCompletion completion)) { taken++; Assert.That(completion.outcome, Is.EqualTo(WorkOutcome.Finished)); }
                    else Thread.Sleep(10);
                }

                Assert.That(taken, Is.EqualTo(2));
                Assert.That(pool.StopAndConfirm(10000), Is.True);
            }

            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }
    }
}
