using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.ConvexCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using static Zantetsu.Core.Tests.SpatialSplitReferenceDeciderTests;

namespace Zantetsu.Core.Tests
{
    // GRAPH-CONNECT-4 unit 2, the engine's additions for the product path (§3-8): the convexes by bank and range pointers, the
    // sides written to the caller's bytes, the span compaction in native areas with its scans on the meter (work 3), and the
    // GJK iteration-limit count. The numbers are the sample input's: the same hash, verdict and sides.
    public unsafe partial class SpatialSplitGraphCandidateNativeTests
    {
        private static int IndexCountOf(SpatialSplitSample s) => s.indices.Length;

        [Test]
        public void TheProductInput_GivesTheSameHashVerdictAndSides_AsTheSampleAndTheSpan_AndMetersItsCompaction()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            var samples = new List<SpatialSplitSample> { NineSeeds(), SpatialSplitGraph.WarmupSample(), ManyComponents(40), DenseComponents(30), Spread(NineSeeds()) };
            int separable = 0;
            foreach (SpatialSplitSample sample in samples)
            {
                SpatialSplitGraph.GraphOutcome a = SpatialSplitGraph.Decide(sample, Graph(), true, out SpatialSplitGraph.NativeRun ra);
                SpatialSplitGraph.GraphOutcome b = SpatialSplitGraph.DecideAsSpan(sample, Graph(), true, out SpatialSplitGraph.NativeRun rb);
                SpatialSplitGraph.GraphOutcome c = SpatialSplitGraph.DecideAsProduct(sample, Graph(), true, out SpatialSplitGraph.NativeRun rc);
                SpatialSplitGraph.GraphOutcome d = SpatialSplitGraph.DecideAsProduct(sample, Graph(), false, out SpatialSplitGraph.NativeRun rd);
                string name = sample.objectName;
                Assert.That(a.unfinished || b.unfinished || c.unfinished || d.unfinished, Is.False, name);
                Assert.That(rb.prepareHash, Is.EqualTo(ra.prepareHash), name + ": span vs sample");
                Assert.That(rc.prepareHash, Is.EqualTo(ra.prepareHash), name + ": product (banks, sides to bytes, native compaction) vs sample");
                Assert.That(rd.prepareHash, Is.EqualTo(ra.prepareHash), name + ": product as managed code vs sample");
                Assert.That(rc.burstRan, Is.True, name); Assert.That(rd.burstRan, Is.False, name);
                Assert.That(c.verdict, Is.EqualTo(a.verdict), name); Assert.That(d.verdict, Is.EqualTo(a.verdict), name);
                Assert.That(c.candidatesA, Is.EqualTo(a.candidatesA), name);
                Assert.That(c.positiveConvexes == a.positiveConvexes && c.negativeConvexes == a.negativeConvexes && c.positiveTriangles == a.positiveTriangles && c.negativeTriangles == a.negativeTriangles, Is.True, name + ": the side counts");
                if (a.verdict == SpatialSplitGraph.GraphVerdict.Separable)
                {
                    separable++;
                    Assert.That(c.sidesWritten, Is.True, name + ": the sides went to the bytes");
                    Assert.That(c.convexPositive, Is.EqualTo(a.convexPositive), name + ": the convex sides, read back from the bytes");
                    Assert.That(c.trianglePositive, Is.EqualTo(a.trianglePositive), name + ": the triangle sides, read back from the bytes");
                    Assert.That(d.trianglePositive, Is.EqualTo(a.trianglePositive), name);
                }
                else
                {
                    Assert.That(c.sidesWritten, Is.False, name + ": no placement, no sides");
                }

                // the search's work is the same; the preparation's carries the compaction's scans: an index three units, a slot of the span one
                Assert.That(c.workSearch, Is.EqualTo(a.workSearch), name);
                Assert.That(b.workPrepare, Is.EqualTo(a.workPrepare + (3L * IndexCountOf(sample)) + b.vertexSpan), name + ": the span input's compaction is on the meter: work 3 = work 2 + 3 I + S");
                Assert.That(c.workPrepare, Is.EqualTo(b.workPrepare), name + ": the product input's compaction is the span's");
                Assert.That(d.workPrepare, Is.EqualTo(c.workPrepare), name);
                Assert.That(c.vertexSpan, Is.EqualTo(b.vertexSpan), name); Assert.That(c.referencedVertices, Is.EqualTo(b.referencedVertices), name);
                Assert.That(rc.nativeBytes, Is.GreaterThan(0), name);
            }

            Assert.That(separable, Is.GreaterThan(0), "some fixture is separable (the sides are compared)");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
        }

        [Test]
        public void TheProductCompaction_StopsOnTheBudget_WithinTheInterval_AndReleasesEverything()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            SpatialSplitSample sample = ManyComponents(2000);   // 6,000 indices: the compaction alone spends over 4,096 units
            SpatialSplitGraph.GraphSettings s = Graph(workBudget: 1);
            SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.DecideAsProduct(sample, s, true, out SpatialSplitGraph.NativeRun rx);
            Assert.That(x.unfinished && x.stop == "work", Is.True, x.reason);
            Assert.That(x.reason, Does.Contain("span compaction"), "the stop is the compaction's");
            Assert.That(x.work, Is.GreaterThanOrEqualTo(4096).And.LessThanOrEqualTo(4097), "stopped at the first check point past the budget");
            Assert.That(x.workPrepare, Is.EqualTo(x.work));
            Assert.That(rx.work, Is.EqualTo(x.work));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "the marks and the compact arrays are released");
            // a budget the compaction fits in but the preparation does not: the body's stop, with the compaction's work counted in
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.DecideAsProduct(sample, Graph(), true, out _);
            Assert.That(free.unfinished, Is.False, free.reason);
            long compaction = (3L * IndexCountOf(sample)) + free.vertexSpan;
            SpatialSplitGraph.GraphOutcome y = SpatialSplitGraph.DecideAsProduct(sample, Graph(workBudget: compaction + 8192), true, out _);
            Assert.That(y.unfinished && y.stop == "work", Is.True, y.reason);
            Assert.That(y.reason, Does.Not.Contain("span compaction"));
            Assert.That(y.work, Is.GreaterThan(compaction));
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
        }

        [Test]
        public void TheProductInput_RefusesAShortSidesBuffer_AndASmallConvex_AsInvalid_BeforeAnyAllocation()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            SpatialSplitGraph.CompactInput w = SpatialSplitGraph.WarmupInput();
            var view = new VpRenderVertex[w.local.Length];
            var indices = new uint[w.indices.Length];
            for (int i = 0; i < indices.Length; i++) indices[i] = w.indices[i];
            var sides = new byte[3];   // one short of the convexes and triangles (2 + 2)
            fixed (VpRenderVertex* vp = view)
            fixed (uint* ip = indices)
            fixed (byte* sp = sides)
            {
                var span = new SpatialSplitGraph.SpanInput
                {
                    vertices = vp, vertexViewLength = view.Length, indices = ip, indexCount = indices.Length, convexVertices = w.convexes,
                    geometryToOwner = w.geometryToOwner, shapeToOwner = w.shapeToOwner, margin = w.margin, sidesOut = sp, sidesOutLength = sides.Length,
                };
                SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.DecideSpan(span, Graph(), true, out SpatialSplitGraph.NativeRun rx);
                Assert.That(x.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput), x.reason);
                Assert.That(x.reason, Does.Contain("sides buffer"));
                Assert.That(rx.allocations, Is.Zero, "refused before any allocation");
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));

                // the product's convexes by pointer: one with three vertices is invalid, before anything is scanned
                var all = new float3[8];
                var ranges = new[] { new ConvexBrepRange { vertexBase = 0, vertexCount = 4 }, new ConvexBrepRange { vertexBase = 4, vertexCount = 3 } };
                var banks = new ConvexBrepBank[2];
                fixed (float3* ap = all)
                fixed (ConvexBrepRange* rp = ranges)
                fixed (ConvexBrepBank* bp = banks)
                {
                    bp[0] = new ConvexBrepBank { vertices = ap }; bp[1] = new ConvexBrepBank { vertices = ap };
                    var byPointer = new SpatialSplitGraph.SpanInput
                    {
                        vertices = vp, vertexViewLength = view.Length, indices = ip, indexCount = indices.Length, convexVertices = null, banks = bp, ranges = rp, convexCount = 2,
                        geometryToOwner = w.geometryToOwner, shapeToOwner = w.shapeToOwner, margin = w.margin,
                    };
                    SpatialSplitGraph.GraphOutcome y = SpatialSplitGraph.DecideSpan(byPointer, Graph(), true, out SpatialSplitGraph.NativeRun ry);
                    Assert.That(y.verdict, Is.EqualTo(SpatialSplitGraph.GraphVerdict.InvalidInput), y.reason);
                    Assert.That(y.reason, Does.Contain("fewer than four vertices"));
                    Assert.That(ry.allocations, Is.Zero);
                }
            }

            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live));
        }

        [Test]
        public void TheProductInput_FailedAllocations_AreUnfinishedAllocation_WithEverythingReleased()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            SpatialSplitSample sample = ManyComponents(40);
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.DecideAsProduct(sample, Graph(), true, out SpatialSplitGraph.NativeRun rf);
            Assert.That(free.unfinished, Is.False, free.reason);
            Assert.That(rf.allocations, Is.GreaterThan(4), "the marks, the compact arrays and the engine's areas");
            for (int failAt = 0; failAt < rf.allocations; failAt++)
            {
                SpatialSplitGraph.GraphSettings s = Graph();
                s.testFailNativeAllocation = failAt;
                SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.DecideAsProduct(sample, s, failAt % 2 == 0, out _);
                Assert.That(x.unfinished && x.stop == "allocation", Is.True, "allocation " + (failAt + 1) + ": " + x.reason);
                Assert.That(x.sidesWritten, Is.False);
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), "after the allocation " + (failAt + 1) + " failed, nothing is left behind");
            }
        }

        [Test]
        public void TheProductCompaction_ReturnsTheWorkSpent_AtEveryFailureBoundary()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            long live = SpatialSplitGraph.LiveNativeAllocations;
            SpatialSplitSample sample = ManyComponents(40);
            SpatialSplitGraph.GraphOutcome free = SpatialSplitGraph.DecideAsProduct(sample, Graph(), true, out SpatialSplitGraph.NativeRun rf);
            Assert.That(free.unfinished, Is.False, free.reason);
            long I = IndexCountOf(sample), S = free.vertexSpan, V = free.referencedVertices;
            long afterScan = I, afterMarks = 2 * I, compaction = (3 * I) + S;
            void Expect(SpatialSplitGraph.GraphSettings s, string stop, long work, string what)
            {
                SpatialSplitGraph.GraphOutcome x = SpatialSplitGraph.DecideAsProduct(sample, s, true, out SpatialSplitGraph.NativeRun rx);
                Assert.That(x.unfinished && x.stop == stop, Is.True, what + ": " + x.reason);
                Assert.That(x.workPrepare, Is.EqualTo(work), what + ": the work spent before the stop");
                Assert.That(x.work, Is.EqualTo(work), what);
                Assert.That(rx.work, Is.EqualTo(work), what);
                Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.EqualTo(live), what + ": everything released");
            }

            // the caps met inside the compaction: after the first scan (the span), after the marks (the compact arrays' bytes), after it (the fixed areas)
            Expect(Graph(maxVertexSpan: 1), "capacity", afterScan, "the span cap");
            Expect(Graph(maxGraphBytes: 1), "capacity", afterScan, "the marks' byte cap");
            Expect(Graph(maxGraphBytes: S), "capacity", afterMarks, "the compact arrays' byte cap (the marks just fit)");
            Expect(Graph(maxGraphBytes: S + (16 * V) + (4 * I)), "capacity", compaction, "the fixed areas' byte cap (the compaction just fits)");
            // the allocations failing: the marks (the first), the three compact arrays, then the engine's first fixed area
            for (int failAt = 0; failAt < 5; failAt++)
            {
                SpatialSplitGraph.GraphSettings s = Graph();
                s.testFailNativeAllocation = failAt;
                long expected = failAt == 0 ? afterScan : failAt <= 3 ? afterMarks : compaction;
                Expect(s, "allocation", expected, "allocation " + (failAt + 1) + " failing");
            }

            Assert.That(rf.allocations, Is.GreaterThan(5));
        }

        [Test]
        public void TheGjkIterationLimit_IsCountedApart_AndTheDefaultLimitIsNotReachedOnTheFixtures()
        {
            SpatialSplitGraph.Warmup(0, true, true, false);
            long hits = 0, hitsAtDefault = 0;
            foreach (SpatialSplitSample sample in new[] { NineSeeds(), ManyComponents(40), ManyComponents(120), DenseComponents(30) })
            {
                SpatialSplitGraph.GraphOutcome a = SpatialSplitGraph.Decide(sample, Graph(), true, out _);
                hitsAtDefault += a.gjkLimitHit;
                SpatialSplitGraph.GraphSettings one = Graph();
                one.gjkMaxIterations = 1;
                SpatialSplitGraph.GraphOutcome b = SpatialSplitGraph.Decide(sample, one, true, out _);
                hits += b.gjkLimitHit;
                Assert.That(b.gjkLimitHit, Is.LessThanOrEqualTo(b.triedB), sample.objectName + ": a hit is a tried pair");
                SpatialSplitGraph.GraphOutcome c = SpatialSplitGraph.Decide(sample, one, false, out _);
                Assert.That(c.gjkLimitHit, Is.EqualTo(b.gjkLimitHit), sample.objectName + ": the count is the same as managed code");
                TestContext.Out.WriteLine(sample.objectName + ": GJK limit 1 -> " + b.verdict + ", hits " + b.gjkLimitHit + " of " + b.triedB + " tried; default -> " + a.verdict + ", hits " + a.gjkLimitHit);
            }

            Assert.That(hits, Is.GreaterThan(0), "one iteration is not enough for some pair");
            Assert.That(hitsAtDefault, Is.Zero, "the default limit of 64 is not reached on these fixtures");
            Assert.That(SpatialSplitGraph.LiveNativeAllocations, Is.Zero);
        }
    }
}
