using System.Collections.Generic;
using NUnit.Framework;
using Unity.Burst;
using Unity.Jobs;
using Unity.Mathematics;
using Zantetsu.Editor.Sandbox;
using Zantetsu.PhysicsCut;
using static Zantetsu.Core.Tests.SpatialSplitReferenceDeciderTests;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The native port of the graph candidate's AB (N0 as managed code, N1 through Burst) against the managed candidate
    /// (M0): the same verdict, placement, counters and stop reason on every fixture; the limits; an invalid input; the
    /// proof that N1 ran through Burst (when Burst is enabled in this Editor) and N0 did not.
    /// </summary>
    public class SpatialSplitGraphCandidateNativeTests
    {
        private const float M = 1f / 4096f;
        private const float Hair = 1f / (1 << 20);
        private const string FarExactly = "boxes at far exactly";
        private static readonly (float3, float3) Unit = Box(0f, 0f, 0f, 1f, 1f, 1f);
        private static float3[] At(float x) => Tri(x, 0.2f, 0.2f, x, 0.8f, 0.2f, x, 0.5f, 0.8f);

        private static IEnumerable<SpatialSplitSample> Fixtures()
        {
            foreach (SpatialSplitSample s in CrossCheckFixtures()) yield return s;
            float3[] inB = Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f);
            (float3[] p1, int[] i1) = Mesh(Tri(-1f, -1f, -1f, 5f, -1f, 5f, 5f, 5f, 5f), inB);
            yield return Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p1, i1, name: "slanted");
            var several = new List<float3[]>();
            foreach (float lift in new[] { -0.5f, 0f, 0.5f }) several.Add(Tri(-1f, -1f, -1f + lift, 5f, -1f, 5f + lift, 5f, 5f, 5f + lift));
            several.Add(inB);
            (float3[] p2, int[] i2) = Mesh(several.ToArray());
            yield return Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p2, i2, name: "several");
            foreach ((string name, float x) in new[] { ("under 2m", 1f + (2f * M) - Hair), ("at 2m", 1f + (2f * M)), ("over 2m", 1f + (2f * M) + Hair) })
            {
                (float3[] p, int[] i) = Mesh(At(x), FarTri);
                yield return Fixture(new[] { Unit, FarBox }, p, i, name: name);
            }

            (float3[] p3, int[] i3) = Mesh(Tri(2f, 0f, 0f, 3f, 0f, 0f, 2f, 1f, 0f), Tri(3f, 0f, 0f, 4f, 0f, 0f, 4f, 1f, 0f), FarTri);
            yield return Fixture(new[] { Unit, FarBox }, p3, i3, name: "touching");
            const float L = 1f / (1 << 24);
            (float3[] p4, int[] i4) = Mesh(new[] { new float3(L / 4f, L / 4f, L), new float3(3f * L / 4f, L / 4f, L), new float3(L / 2f, 3f * L / 4f, L) }, inB);
            yield return Fixture(new[] { Box(0f, 0f, 0f, L, L, L), Box(2f, 0f, 0f, 3f, 1f, 1f) }, p4, i4, margin: 1f / (1L << 35), name: "tiny box");
            (float3[] p5, int[] i5) = Mesh(Tri(2f, 0.5f, -1f, 2f, 0.5f, 2f, 2.2f, 0.5f, 2f), Tri(1.5f, 0f, 0.5f, 2.5f, 0f, 0.5f, 2f, 1f, 0.5f));
            yield return Fixture(new[] { Unit, Box(3f, 0f, 0f, 4f, 1f, 1f) }, p5, i5, name: "crossing");
            yield return NineSeeds();
            // Two boxes exactly far apart along x (far = 2m + 4 E1 = 2^-11 + 21 * 2^-42 for M = 2^-12 and the bound 21 of FarBox):
            // hi.x = -2^-11 (= -2M) and lo.x = 21 * 2^-42 are floats whose double difference is far itself (TL review 2026-10-23: the
            // earlier -2^-12 was half of far); a far box and a far triangle make the deal. The pair is a candidate at the boundary,
            // kept by every pair and by the sweep alike; the fixture test asserts its gap is the job's own far, bit for bit.
            const float Step = 21f / (1L << 42);
            (float3[] p6, int[] i6) = Mesh(FarTri, Tri(-0.8f, 0.2f, 0.2f, -0.2f, 0.2f, 0.2f, -0.5f, 0.8f, 0.2f));
            yield return Fixture(new[] { Box(-1f, 0f, 0f, -(2f * M), 1f, 1f), Box(Step, 0f, 0f, 1f, 1f, 1f), FarBox }, p6, i6, name: FarExactly);
        }

        // Three convex-only and three triangle-only components, every pair a candidate by the boxes (the planes z = x + c
        // pass over the three boxes along x, their boxes hold them): the seeds are the most the search generates, 3 x 3 pairs
        // over 6 components (TL review 2026-10-22: the seeds' area must hold them without reaching the next area).
        private static SpatialSplitSample NineSeeds()
        {
            var triangles = new List<float3[]>();
            foreach (float c in new[] { 1.5f, 1.75f, 2f }) triangles.Add(Tri(-1f, -1f, -1f + c, 7f, -1f, 7f + c, 7f, 5f, 7f + c));   // the lowest point z = c - 1 <= 1: every box overlaps every triangle
            (float3[] p, int[] i) = Mesh(triangles.ToArray());
            return Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f), Box(4f, 0f, 0f, 5f, 1f, 1f) }, p, i, name: "nine seeds");
        }

        [Test]
        public void NineSeeds_AreGeneratedAndTheSearchStillAgrees()
        {
            SpatialSplitSample sample = NineSeeds();
            var settings = new CandidateSettings { stages = CandidateStages.AB };
            CandidateResult m0 = SpatialSplitGraphCandidate.Decide(sample, settings);
            Assert.That(m0.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(m0.stage, Is.EqualTo("B"), "the boxes alone cannot deal: every pair is a candidate");
            Assert.That(m0.candidatesA, Is.EqualTo(12), "3 x 3 box-triangle pairs and the three triangle pairs");
            foreach (bool burst in new[] { false, true })
            {
                CandidateResult x = SpatialSplitGraphCandidateNative.Decide(sample, settings, burst, out _);
                AssertSame("nine seeds", m0, x, burst ? "N1" : "N0");
            }
        }

        [Test]
        public void AnAllocationFailure_AtAnyPoint_LeavesNoNativeAreaBehind()
        {
            (float3[] p, int[] i) = Mesh(Tri(-1f, -1f, -1f, 5f, -1f, 5f, 5f, 5f, 5f), Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f));
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, name: "slanted, failing allocations");
            long before = SpatialSplitGraphCandidateNative.LiveNativeAllocations;
            // the sweep's two added areas are released on any failure as well
            for (int failAt = 0; failAt < 64; failAt++)
            {
                CandidateResult s = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, pairsSweep = true, testFailNativeAllocation = failAt }, failAt % 2 == 0, out _);
                Assert.That(SpatialSplitGraphCandidateNative.LiveNativeAllocations, Is.EqualTo(before), "sweep: after the allocation " + (failAt + 1) + " failed, nothing is left behind");
                if (s.verdict == CandidateVerdict.Separable) break;
                Assert.That(s.stop, Is.EqualTo("allocation"));
            }

            CandidateResult whole = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB }, false, out _);
            Assert.That(whole.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(SpatialSplitGraphCandidateNative.LiveNativeAllocations, Is.EqualTo(before), "a whole decision releases everything");
            int failures = 0;
            for (int failAt = 0; failAt < 64; failAt++)
            {
                CandidateResult x = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, testFailNativeAllocation = failAt }, failAt % 2 == 1, out _);
                Assert.That(SpatialSplitGraphCandidateNative.LiveNativeAllocations, Is.EqualTo(before), "after the allocation " + (failAt + 1) + " failed, nothing is left behind");
                if (x.verdict == CandidateVerdict.Separable) break;   // more allocations than the decision makes: the hook did not fire
                Assert.That(x.verdict, Is.EqualTo(CandidateVerdict.Undecided), "a failed allocation says nothing: " + x.reason);
                Assert.That(x.stop, Is.EqualTo("allocation"));
                Assert.That(x.reason, Does.Contain("allocation " + (failAt + 1) + " failed"));
                failures++;
            }

            TestContext.Out.WriteLine(failures + " allocations in a decision, each made to fail once");
            Assert.That(failures, Is.GreaterThanOrEqualTo(20));

            // Memory running out (OutOfMemoryException, not the allocator's refusal) at any allocation: the same failure result,
            // nothing left behind, nothing escaping to the caller.
            for (int failAt = 0; failAt < failures; failAt += 5)
            {
                CandidateResult x = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, testFailNativeAllocation = failAt, testFailWithOutOfMemory = true }, failAt % 2 == 0, out _);
                Assert.That(x.verdict, Is.EqualTo(CandidateVerdict.Undecided), "out of memory says nothing: " + x.reason);
                Assert.That(x.stop, Is.EqualTo("allocation"));
                Assert.That(x.reason, Does.StartWith("out of memory"));
                Assert.That(SpatialSplitGraphCandidateNative.LiveNativeAllocations, Is.EqualTo(before), "after memory ran out at the allocation " + (failAt + 1) + ", nothing is left behind");
            }
            // The input refused after the first allocations (a bad index is found inside the preparation job).
            sample.indices[1] = 99;
            CandidateResult refused = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB }, false, out _);
            Assert.That(refused.verdict, Is.EqualTo(CandidateVerdict.InvalidInput));
            Assert.That(SpatialSplitGraphCandidateNative.LiveNativeAllocations, Is.EqualTo(before), "an input refused inside releases everything");
        }

        private static void AssertSame(string name, CandidateResult expected, CandidateResult actual, string engine)
        {
            Assert.That(actual.verdict, Is.EqualTo(expected.verdict), name + " [" + engine + "]: the verdict (" + actual.reason + ")");
            Assert.That(actual.stage, Is.EqualTo(expected.stage), name + " [" + engine + "]: the stage");
            Assert.That(actual.stop, Is.EqualTo(expected.stop), name + " [" + engine + "]: the stop reason");
            Assert.That(actual.convexPositive, Is.EqualTo(expected.convexPositive), name + " [" + engine + "]: the convexes' sides");
            Assert.That(actual.trianglePositive, Is.EqualTo(expected.trianglePositive), name + " [" + engine + "]: the triangles' sides");
            Assert.That(actual.meshComponents, Is.EqualTo(expected.meshComponents), name + " [" + engine + "]: mesh components");
            Assert.That(actual.candidatesA, Is.EqualTo(expected.candidatesA), name + " [" + engine + "]: candidates A");
            Assert.That(actual.pairsApartByBoxes, Is.EqualTo(expected.pairsApartByBoxes), name + " [" + engine + "]: pairs apart by boxes");
            Assert.That(actual.triedB, Is.EqualTo(expected.triedB), name + " [" + engine + "]: tried B");
            Assert.That(actual.refutedB, Is.EqualTo(expected.refutedB), name + " [" + engine + "]: refuted B");
            Assert.That(actual.ambiguousB, Is.EqualTo(expected.ambiguousB), name + " [" + engine + "]: ambiguous B");
            Assert.That(actual.cutAttempts, Is.EqualTo(expected.cutAttempts), name + " [" + engine + "]: cut attempts");
            Assert.That(actual.gjkIterations, Is.EqualTo(expected.gjkIterations), name + " [" + engine + "]: GJK iterations");
            Assert.That(actual.supportQueries, Is.EqualTo(expected.supportQueries), name + " [" + engine + "]: support queries");
            Assert.That(actual.vertexScans, Is.EqualTo(expected.vertexScans), name + " [" + engine + "]: vertex scans");
        }

        [Test]
        public void N0AndN1_AgreeWithM0_OnEveryFixture()
        {
            SpatialSplitGraphCandidateNative.Warmup();
            int fixtures = 0, separable = 0;
            foreach (SpatialSplitSample sample in Fixtures())
            {
                fixtures++;
                var settings = new CandidateSettings { stages = CandidateStages.AB, secondsLimit = 120.0 };
                CandidateResult m0 = SpatialSplitGraphCandidate.Decide(sample, settings);
                CandidateResult n0 = SpatialSplitGraphCandidateNative.Decide(sample, settings, false, out SpatialSplitGraphCandidateNative.NativeRun run0);
                CandidateResult n1 = SpatialSplitGraphCandidateNative.Decide(sample, settings, true, out SpatialSplitGraphCandidateNative.NativeRun run1);
                TestContext.Out.WriteLine(sample.objectName + ": M0 " + m0.verdict + " at " + m0.stage + " (" + m0.stop + "; A " + m0.candidatesA + ", B " + m0.triedB + "/" + m0.refutedB + ", cuts " + m0.cutAttempts
                    + "); N0 " + n0.verdict + " (" + (run0.secondsTotal * 1000.0).ToString("F2") + " ms, native " + run0.nativeBytes + " B); N1 " + n1.verdict + " (" + (run1.secondsTotal * 1000.0).ToString("F2")
                    + " ms, Burst ran " + run1.burstRan + ")");
                Assert.That(m0.verdict, Is.Not.EqualTo(CandidateVerdict.InvalidInput), sample.objectName + ": M0 " + m0.reason);
                AssertSame(sample.objectName, m0, n0, "N0");
                AssertSame(sample.objectName, m0, n1, "N1");
                Assert.That(run0.burstRan, Is.False, sample.objectName + ": N0 ran as managed code");
                Assert.That(run1.burstRan, Is.EqualTo(BurstCompiler.IsEnabled), sample.objectName + ": N1 ran through Burst iff Burst is enabled");
                if (m0.verdict == CandidateVerdict.Separable) separable++;
            }

            TestContext.Out.WriteLine(fixtures + " fixtures, " + separable + " separable; Burst enabled: " + BurstCompiler.IsEnabled);
            Assert.That(fixtures, Is.GreaterThanOrEqualTo(20));
            Assert.That(separable, Is.GreaterThanOrEqualTo(8));
        }

        [Test]
        public void TheLimits_AreTheSame_AndTheDeadlineIsAppliedAtTheReturn()
        {
            float3[] inB = Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f);
            var several = new List<float3[]>();
            foreach (float lift in new[] { -0.5f, 0f, 0.5f }) several.Add(Tri(-1f, -1f, -1f + lift, 5f, -1f, 5f + lift, 5f, 5f, 5f + lift));
            several.Add(inB);
            (float3[] p, int[] i) = Mesh(several.ToArray());
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, name: "several, limited");
            foreach (bool burst in new[] { false, true })
            {
                var noCuts = new CandidateSettings { stages = CandidateStages.AB, maxCutAttempts = 0 };
                CandidateResult m = SpatialSplitGraphCandidate.Decide(sample, noCuts);
                CandidateResult x = SpatialSplitGraphCandidateNative.Decide(sample, noCuts, burst, out _);
                Assert.That(x.verdict, Is.EqualTo(CandidateVerdict.NotFound));
                Assert.That(x.stop, Is.EqualTo("cut-limit"));
                AssertSame("no cuts", m, x, burst ? "N1" : "N0");
                var oneIteration = new CandidateSettings { stages = CandidateStages.AB, gjkMaxIterations = 1 };
                m = SpatialSplitGraphCandidate.Decide(sample, oneIteration);
                x = SpatialSplitGraphCandidateNative.Decide(sample, oneIteration, burst, out _);
                AssertSame("one GJK iteration", m, x, burst ? "N1" : "N0");
                var noTime = new CandidateSettings { stages = CandidateStages.AB, secondsLimit = 1e-9 };
                x = SpatialSplitGraphCandidateNative.Decide(sample, noTime, burst, out _);
                Assert.That(x.verdict, Is.EqualTo(CandidateVerdict.Undecided), "the deadline, applied at the return");
                Assert.That(x.stop, Is.EqualTo("time"));
                Assert.That(x.convexPositive, Is.Null);
            }
        }

        [Test]
        public void TheMeasurement_ChangesNoResult_RunsThroughBurst_AndItsClockIsSane()
        {
            // The clock: monotonic, and its nanosecond conversion agrees with the Stopwatch over a busy wait.
            long a = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.Timestamp;
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            long until = begin + (System.Diagnostics.Stopwatch.Frequency / 50);   // 20 ms
            while (System.Diagnostics.Stopwatch.GetTimestamp() < until) { }
            long b = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.Timestamp;
            double stopwatchMs = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            var ratio = Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.TimestampToNanosecondsConversionRatio;
            double clockMs = (double)(b - a) * ratio.Numerator / ratio.Denominator / 1e6;
            TestContext.Out.WriteLine(SpatialSplitGraphCandidateNative.ClockDescription() + "; a 20 ms busy wait: Stopwatch " + stopwatchMs.ToString("F3") + " ms, the clock " + clockMs.ToString("F3") + " ms");
            Assert.That(b, Is.GreaterThan(a), "monotonic");
            Assert.That(clockMs, Is.InRange(stopwatchMs * 0.7, stopwatchMs * 1.3), "the conversion to nanoseconds agrees roughly with the Stopwatch");

            // The same results with the measurement off, the section times on, and the counts on; Burst ran in every mode.
            float3[] inB = Tri(2.2f, 0.2f, 0.5f, 2.8f, 0.2f, 0.5f, 2.5f, 0.8f, 0.5f);
            var several = new List<float3[]>();
            foreach (float lift in new[] { -0.5f, 0f, 0.5f }) several.Add(Tri(-1f, -1f, -1f + lift, 5f, -1f, 5f + lift, 5f, 5f, 5f + lift));
            several.Add(inB);
            (float3[] p, int[] i) = Mesh(several.ToArray());
            SpatialSplitSample sample = Fixture(new[] { Unit, Box(2f, 0f, 0f, 3f, 1f, 1f) }, p, i, name: "several, measured");
            foreach (int mode in new[] { 0, 1, 2 }) SpatialSplitGraphCandidateNative.Warmup(mode);
            CandidateResult off = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, measure = 0 }, true, out SpatialSplitGraphCandidateNative.NativeRun runOff);
            CandidateResult timed = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, measure = 1 }, true, out SpatialSplitGraphCandidateNative.NativeRun runTimed);
            CandidateResult counted = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, measure = 2 }, true, out SpatialSplitGraphCandidateNative.NativeRun runCounted);
            Assert.That(off.verdict, Is.EqualTo(CandidateVerdict.Separable));
            Assert.That(off.stage, Is.EqualTo("B"), "the fixture reaches B: GJK and projections run");
            AssertSame("measured", off, timed, "N1 measure 1");
            AssertSame("measured", off, counted, "N1 measure 2");
            Assert.That(runOff.burstRan && runTimed.burstRan && runCounted.burstRan, Is.EqualTo(BurstCompiler.IsEnabled), "Burst ran in every mode iff enabled");
            Assert.That(runOff.measure, Is.Zero);
            Assert.That(runTimed.measure, Is.EqualTo(1));
            Assert.That(runCounted.measure, Is.EqualTo(2));
            double sum = 0.0;
            for (int s = 0; s < SpatialSplitGraphCandidateNative.SectionTimes; s++)
            {
                Assert.That(runTimed.sectionMs[s], Is.GreaterThanOrEqualTo(0.0), SpatialSplitGraphCandidateNative.SectionNames[s]);
                sum += runTimed.sectionMs[s];
            }

            TestContext.Out.WriteLine("sections (ms): " + string.Join(", ", System.Array.ConvertAll(runTimed.sectionMs, v => v.ToString("F4"))) + "; sum " + sum.ToString("F4") + ", search " + (runTimed.secondsSearch * 1000.0).ToString("F4")
                + ", residual " + runTimed.residualMs.ToString("F4") + "; projection calls " + runTimed.counters[SpatialSplitGraphCandidateNative.CntProjectionCalls - SpatialSplitGraphCandidateNative.SectionTimes]);
            Assert.That(sum, Is.LessThanOrEqualTo(runTimed.secondsSearch * 1000.0 * 1.05 + 0.01), "the sections do not exceed the search's own time");
            Assert.That(runTimed.sectionMs[SpatialSplitGraphCandidateNative.SecGjk], Is.GreaterThan(0.0), "GJK was timed");
            Assert.That(runTimed.counters[SpatialSplitGraphCandidateNative.CntProjectionCalls - SpatialSplitGraphCandidateNative.SectionTimes], Is.EqualTo(off.refutedB).Or.GreaterThan(off.refutedB), "a projection per refutation at least");
            Assert.That(runCounted.counters[SpatialSplitGraphCandidateNative.CntGjkCalls - SpatialSplitGraphCandidateNative.SectionTimes], Is.EqualTo(off.triedB), "a GJK call per B trial");
            Assert.That(runCounted.counters[SpatialSplitGraphCandidateNative.CntMinCutCalls - SpatialSplitGraphCandidateNative.SectionTimes], Is.EqualTo(off.cutAttempts), "a minimum cut per cut attempt");
            TestContext.Out.WriteLine("counts: " + string.Join(", ", System.Array.ConvertAll(runCounted.counters, v => v.ToString())));
        }

        private static void AssertTop3(string name, double[] gaps, int[] reps)
        {
            int[] sorted = SpatialSplitGraphCandidateNative.SortAllReference(gaps, reps);
            int[] top = SpatialSplitGraphCandidateNative.SelectTop3Reference(gaps, reps, out long compares, out long moves);
            int want = System.Math.Min(3, gaps.Length);
            Assert.That(top.Length, Is.EqualTo(want), name + ": count");
            for (int i = 0; i < want; i++) Assert.That(top[i], Is.EqualTo(sorted[i]), name + ": item " + i + " (the order of the three)");
            Assert.That(compares, Is.LessThanOrEqualTo(3L * gaps.Length), name + ": at most three compares an item");
            Assert.That(moves, Is.LessThanOrEqualTo(2L * gaps.Length), name + ": at most two moves an item");
        }

        [Test]
        public void TheTop3Selection_GivesTheSortsFirstThree_InOrder_OnEveryCase()
        {
            // 0 / 1 / 2 / 3 / 4+ items; ascending, descending, the least last, many ties; ties whose representative order differs
            // from the input order.
            AssertTop3("none", new double[0], new int[0]);
            AssertTop3("one", new[] { 2.0 }, new[] { 7 });
            AssertTop3("two", new[] { 2.0, 1.0 }, new[] { 7, 3 });
            AssertTop3("three", new[] { 2.0, 1.0, 3.0 }, new[] { 7, 3, 5 });
            AssertTop3("four", new[] { 2.0, 1.0, 3.0, 0.5 }, new[] { 7, 3, 5, 9 });
            AssertTop3("ascending", new[] { 0.0, 1.0, 2.0, 3.0, 4.0, 5.0 }, new[] { 0, 1, 2, 3, 4, 5 });
            AssertTop3("descending", new[] { 5.0, 4.0, 3.0, 2.0, 1.0, 0.0 }, new[] { 0, 1, 2, 3, 4, 5 });
            AssertTop3("least last", new[] { 3.0, 3.0, 3.0, 3.0, 0.0 }, new[] { 0, 1, 2, 3, 4 });
            AssertTop3("all ties, reps reversed", new[] { 1.0, 1.0, 1.0, 1.0, 1.0 }, new[] { 9, 7, 5, 3, 1 });
            AssertTop3("ties and reps out of input order", new[] { 2.0, 1.0, 1.0, 2.0, 1.0, 0.0, 0.0 }, new[] { 4, 12, 3, 1, 8, 20, 6 });
            // Many deterministic inputs: gaps from a small set (many ties), representatives a permutation.
            uint state = 12345u;
            uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
            int cases = 0;
            for (int n = 0; n <= 40; n++)
            {
                for (int rep = 0; rep < 60; rep++)
                {
                    var gaps = new double[n];
                    var reps = new int[n];
                    for (int i = 0; i < n; i++) { gaps[i] = (Next() % 5u) * 0.25; reps[i] = i; }
                    for (int i = n - 1; i > 0; i--) { int j = (int)(Next() % (uint)(i + 1)); (reps[i], reps[j]) = (reps[j], reps[i]); }
                    AssertTop3("random n=" + n + " #" + rep, gaps, reps);
                    cases++;
                }
            }

            TestContext.Out.WriteLine(cases + " deterministic cases agreed with the full sort's first three, in order");
        }

        [Test]
        public void TheSelection_AndTheSort_GiveTheSameSearch_OnEveryFixture_WithLinearNearestWork()
        {
            SpatialSplitGraphCandidateNative.Warmup(0, true);
            SpatialSplitGraphCandidateNative.Warmup(0, false);
            int fixtures = 0, withSeeds = 0;
            foreach (SpatialSplitSample sample in Fixtures())
            {
                fixtures++;
                var sort = new CandidateSettings { stages = CandidateStages.AB, nearestSelection = false };
                var select = new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true };
                CandidateResult a = SpatialSplitGraphCandidateNative.Decide(sample, sort, true, out SpatialSplitGraphCandidateNative.NativeRun ra);
                CandidateResult b = SpatialSplitGraphCandidateNative.Decide(sample, select, true, out SpatialSplitGraphCandidateNative.NativeRun rb);
                AssertSame(sample.objectName, a, b, "N1 select vs sort");
                Assert.That(ra.nearestSelection, Is.False);
                Assert.That(rb.nearestSelection, Is.True);
                // The diagnostic counts: the selection's gaps are one per item, its compares at most three per item.
                var diagSort = new CandidateSettings { stages = CandidateStages.AB, nearestSelection = false, measure = 2 };
                var diagSelect = new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, measure = 2 };
                SpatialSplitGraphCandidateNative.Decide(sample, diagSort, true, out SpatialSplitGraphCandidateNative.NativeRun da);
                SpatialSplitGraphCandidateNative.Decide(sample, diagSelect, true, out SpatialSplitGraphCandidateNative.NativeRun db);
                int gi = SpatialSplitGraphCandidateNative.CntNearestGaps - SpatialSplitGraphCandidateNative.SectionTimes;
                int ci = SpatialSplitGraphCandidateNative.CntNearestCompares - SpatialSplitGraphCandidateNative.SectionTimes;
                if (db.counters[gi] > 0)
                {
                    withSeeds++;
                    Assert.That(db.counters[ci], Is.LessThanOrEqualTo(3 * db.counters[gi]), sample.objectName + ": the selection compares at most three times an item");
                    // The sort computes no gap for a lone candidate (no outer iteration) where the selection computes one: at most one
                    // extra gap a convex-only side (<= 3) an iteration; otherwise the selection's count gaps are never more than
                    // the sort's 2 (count - 1) or more.
                    Assert.That(db.counters[gi], Is.LessThanOrEqualTo(da.counters[gi] + (3L * (b.cutAttempts + 1))), sample.objectName + ": the selection computes no more gaps than the sort, but for lone candidates");
                }
            }

            TestContext.Out.WriteLine(fixtures + " fixtures, " + withSeeds + " with triangle-only seeds to choose from");
            Assert.That(withSeeds, Is.GreaterThanOrEqualTo(2));
        }

        // ---- the flow kernel: Edmonds-Karp against Dinic against an exhaustive check on small graphs --------------------------------

        private const int Inf = SpatialSplitGraphCandidateNative.FlowKernel.Inf;

        private sealed class SmallGraph
        {
            public int m;                                   // the component nodes 0..m-1; src = m, dst = m + 1
            public List<(int a, int b, bool blocked)> groups = new List<(int, int, bool)>();
            public int[] source, sink;                      // the seeds' members (1 or 2 each)
        }

        // The arcs as the search job builds them: groups in order (a->b, b->a with 1 or Inf), then the seeds' arcs (src->k Inf
        // / k->src 0; k->dst Inf / dst->k 0); the CSR order of each node's list is the order of insertion.
        private static (Unity.Collections.NativeArray<int> work, int adjStart, int arcTo, int arcRev, int arcCap, int queue, int parent, int level, int cur, int pathNode, int pathArc, int nodes)
            Build(SmallGraph g)
        {
            int nodes = g.m + 2, src = g.m, dst = g.m + 1;
            var lists = new List<(int to, int cap)>[nodes];
            for (int v = 0; v < nodes; v++) lists[v] = new List<(int, int)>();
            var pairs = new List<(int u, int iu, int v, int iv)>();
            void Add(int a, int b, int capAb, int capBa)
            {
                lists[a].Add((b, capAb));
                lists[b].Add((a, capBa));
                pairs.Add((a, lists[a].Count - 1, b, lists[b].Count - 1));
            }

            foreach ((int a, int b, bool blocked) in g.groups) { int c = blocked ? Inf : 1; Add(a, b, c, c); }
            foreach (int k in g.source) Add(src, k, Inf, 0);
            foreach (int k in g.sink) Add(k, dst, Inf, 0);
            int arcs = pairs.Count * 2;
            int adjStart = 0, arcTo = nodes + 1, arcRev = arcTo + arcs, arcCap = arcRev + arcs, queue = arcCap + arcs, parent = queue + nodes + 1, level = parent + nodes + 1, cur = level + nodes + 1, pathNode = cur + nodes + 1, pathArc = pathNode + nodes + 1;
            var w = new Unity.Collections.NativeArray<int>(pathArc + nodes + 1, Unity.Collections.Allocator.Temp);
            int at = 0;
            var start = new int[nodes];
            for (int v = 0; v < nodes; v++) { w[adjStart + v] = at; start[v] = at; at += lists[v].Count; }
            w[adjStart + nodes] = at;
            for (int v = 0; v < nodes; v++) for (int i = 0; i < lists[v].Count; i++) { w[arcTo + start[v] + i] = lists[v][i].to; w[arcCap + start[v] + i] = lists[v][i].cap; }
            foreach ((int u, int iu, int v, int iv) in pairs) { w[arcRev + start[u] + iu] = start[v] + iv; w[arcRev + start[v] + iv] = start[u] + iu; }
            return (w, adjStart, arcTo, arcRev, arcCap, queue, parent, level, cur, pathNode, pathArc, nodes);
        }

        // Exhaustive: every source side S (src in S, dst not), its capacity (Inf when an Inf arc crosses); the least, and the
        // intersection of all least sides.
        private static (long least, bool[] minimalSide, bool finite) Exhaustive(SmallGraph g)
        {
            int src = g.m, dst = g.m + 1;
            long least = long.MaxValue;
            bool[] minimal = null;
            for (int mask = 0; mask < (1 << g.m); mask++)
            {
                var inS = new bool[g.m + 2];
                for (int v = 0; v < g.m; v++) inS[v] = (mask & (1 << v)) != 0;
                inS[src] = true;
                long cap = 0;
                bool infinite = false;
                foreach ((int a, int b, bool blocked) in g.groups)
                {
                    if (inS[a] != inS[b]) { if (blocked) infinite = true; else cap++; }
                }

                foreach (int k in g.source) if (!inS[k]) infinite = true;
                foreach (int k in g.sink) if (inS[k]) infinite = true;
                if (infinite) continue;
                if (cap < least) { least = cap; minimal = (bool[])inS.Clone(); }
                else if (cap == least) for (int v = 0; v < inS.Length; v++) minimal[v] &= inS[v];
            }

            return (least, minimal, minimal != null);
        }

        private static void CheckGraph(string name, SmallGraph g)
        {
            (long least, bool[] minimalSide, bool finite) = Exhaustive(g);
            var results = new List<(int flow, bool[] side, List<int> cut, SpatialSplitGraphCandidateNative.FlowCounts counts)>();
            foreach (bool dinic in new[] { false, true })
            {
                var b = Build(g);
                var counts = new SpatialSplitGraphCandidateNative.FlowCounts();
                int flow = dinic
                    ? SpatialSplitGraphCandidateNative.FlowKernel.Dinic(b.work, b.adjStart, b.arcTo, b.arcRev, b.arcCap, b.queue, b.level, b.cur, b.pathNode, b.pathArc, b.nodes, g.m, g.m + 1, true, ref counts)
                    : SpatialSplitGraphCandidateNative.FlowKernel.EdmondsKarp(b.work, b.adjStart, b.arcTo, b.arcRev, b.arcCap, b.queue, b.parent, b.nodes, g.m, g.m + 1, true, ref counts);
                bool[] side = null;
                var cut = new List<int>();
                if (flow >= 0)
                {
                    var reach = new Unity.Collections.NativeArray<byte>(b.nodes, Unity.Collections.Allocator.Temp);
                    SpatialSplitGraphCandidateNative.FlowKernel.ReachFromSource(b.work, b.adjStart, b.arcTo, b.arcCap, b.queue, b.nodes, g.m, reach, 0);
                    side = new bool[b.nodes];
                    for (int v = 0; v < b.nodes; v++) side[v] = reach[v] != 0;
                    for (int i = 0; i < g.groups.Count; i++) if (side[g.groups[i].a] != side[g.groups[i].b]) cut.Add(i);
                    reach.Dispose();
                }

                b.work.Dispose();
                results.Add((flow, side, cut, counts));
            }

            foreach ((int flow, bool[] side, List<int> cut, SpatialSplitGraphCandidateNative.FlowCounts counts) in results)
            {
                if (!finite) { Assert.That(flow, Is.EqualTo(-1), name + ": no finite cut"); continue; }
                Assert.That(flow, Is.EqualTo((int)least), name + ": the least capacity");
                Assert.That(side, Is.EqualTo(minimalSide), name + ": the source side is the intersection of the least cuts' sides");
            }

            Assert.That(results[1].flow, Is.EqualTo(results[0].flow), name + ": Dinic's flow equals Edmonds-Karp's");
            Assert.That(results[1].cut, Is.EqualTo(results[0].cut), name + ": the same cut, in the groups' order");
        }

        [Test]
        public void TheFlowKernel_EdmondsKarpAndDinic_AgreeWithTheExhaustiveCheck_OnTheListedShapes()
        {
            // cut 0 (nothing between the seeds)
            CheckGraph("cut 0", new SmallGraph { m = 2, source = new[] { 0 }, sink = new[] { 1 } });
            // already cut: a chain a-b and c-d, the seeds on a and d, no path
            CheckGraph("already cut", new SmallGraph { m = 4, groups = { (0, 1, false), (2, 3, false) }, source = new[] { 0 }, sink = new[] { 3 } });
            // several least cuts: a path a-b-c-d with unit arcs: three cuts of 1; the minimal side is {a}
            CheckGraph("several least cuts", new SmallGraph { m = 4, groups = { (0, 1, false), (1, 2, false), (2, 3, false) }, source = new[] { 0 }, sink = new[] { 3 } });
            // finite and Inf mixed: a-b blocked, b-c unit, c-d blocked: the cut is b-c
            CheckGraph("mixed", new SmallGraph { m = 4, groups = { (0, 1, true), (1, 2, false), (2, 3, true) }, source = new[] { 0 }, sink = new[] { 3 } });
            // Inf alone joins the seeds: no finite cut
            CheckGraph("Inf only", new SmallGraph { m = 3, groups = { (0, 1, true), (1, 2, true) }, source = new[] { 0 }, sink = new[] { 2 } });
            // seeds of two members
            CheckGraph("two-member seeds", new SmallGraph { m = 6, groups = { (0, 2, false), (1, 2, false), (2, 3, false), (3, 4, false), (3, 5, false), (0, 5, false) }, source = new[] { 0, 1 }, sink = new[] { 4, 5 } });
            // a first path that must be rerouted through a reverse arc (the TL's fixture, 2026-10-23): edges (0,1) (0,2) (1,3)
            // (1,4) (2,3) (3,5) (4,5), source 0, sink 5. The first path is 0-1-3-5; the second 0-2-3-1-4-5 undoes the flow on
            // 1-3 through its reverse arc: the flow is 2, and the residual capacities of 1-3 are back to 1 both ways.
            var reroute = new SmallGraph { m = 6, groups = { (0, 1, false), (0, 2, false), (1, 3, false), (1, 4, false), (2, 3, false), (3, 5, false), (4, 5, false) }, source = new[] { 0 }, sink = new[] { 5 } };
            CheckGraph("reroute", reroute);
            foreach (bool dinic in new[] { false, true })
            {
                var b = Build(reroute);
                var counts = new SpatialSplitGraphCandidateNative.FlowCounts();
                int flow = dinic
                    ? SpatialSplitGraphCandidateNative.FlowKernel.Dinic(b.work, b.adjStart, b.arcTo, b.arcRev, b.arcCap, b.queue, b.level, b.cur, b.pathNode, b.pathArc, b.nodes, 6, 7, true, ref counts)
                    : SpatialSplitGraphCandidateNative.FlowKernel.EdmondsKarp(b.work, b.adjStart, b.arcTo, b.arcRev, b.arcCap, b.queue, b.parent, b.nodes, 6, 7, true, ref counts);
                Assert.That(flow, Is.EqualTo(2), (dinic ? "Dinic" : "Edmonds-Karp") + ": the flow of the reroute fixture");
                Assert.That(counts.pushes, Is.EqualTo(2), (dinic ? "Dinic" : "Edmonds-Karp") + ": two unit pushes");
                int e13 = -1, e31 = -1;
                for (int e = b.work[b.adjStart + 1]; e < b.work[b.adjStart + 2]; e++) if (b.work[b.arcTo + e] == 3) e13 = e;
                Assert.That(e13, Is.GreaterThanOrEqualTo(0));
                e31 = b.work[b.arcRev + e13];
                Assert.That(b.work[b.arcTo + e31], Is.EqualTo(1));
                Assert.That(b.work[b.arcCap + e13], Is.EqualTo(1), (dinic ? "Dinic" : "Edmonds-Karp") + ": 1->3 undone: its residual capacity is 1 again");
                Assert.That(b.work[b.arcCap + e31], Is.EqualTo(1), (dinic ? "Dinic" : "Edmonds-Karp") + ": 3->1 back to 1 as well");
                // and the arcs that carry the two units are saturated: 0->1, 0->2, 3->5, 4->5
                int Arc(int u, int v) { for (int e = b.work[b.adjStart + u]; e < b.work[b.adjStart + u + 1]; e++) if (b.work[b.arcTo + e] == v) return e; return -1; }
                Assert.That(b.work[b.arcCap + Arc(0, 1)], Is.Zero);
                Assert.That(b.work[b.arcCap + Arc(0, 2)], Is.Zero);
                Assert.That(b.work[b.arcCap + Arc(3, 5)], Is.Zero);
                Assert.That(b.work[b.arcCap + Arc(4, 5)], Is.Zero);
                b.work.Dispose();
            }
            // a long path and a branch
            var longPath = new SmallGraph { m = 8, source = new[] { 0 }, sink = new[] { 7 } };
            for (int i = 0; i < 7; i++) longPath.groups.Add((i, i + 1, false));
            longPath.groups.Add((1, 5, false));
            longPath.groups.Add((2, 6, true));
            CheckGraph("long path and branch", longPath);
            // deterministic random graphs
            uint state = 987654321u;
            uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
            int cases = 0;
            for (int i = 0; i < 400; i++)
            {
                int m = 3 + (int)(Next() % 5u);
                var g = new SmallGraph { m = m };
                int edges = (int)(Next() % (uint)(m * 2)) + 1;
                var seen = new HashSet<(int, int)>();
                for (int e = 0; e < edges; e++)
                {
                    int a = (int)(Next() % (uint)m), b = (int)(Next() % (uint)m);
                    if (a == b) continue;
                    var key = (System.Math.Min(a, b), System.Math.Max(a, b));
                    if (!seen.Add(key)) continue;
                    g.groups.Add((key.Item1, key.Item2, Next() % 5u == 0));
                }

                int s0 = (int)(Next() % (uint)m), t0 = (int)(Next() % (uint)m);
                if (s0 == t0) continue;
                var src = new List<int> { s0 };
                var dst = new List<int> { t0 };
                if (m >= 4 && Next() % 2u == 0) { int s1 = (int)(Next() % (uint)m); if (s1 != s0 && s1 != t0) src.Add(s1); }
                if (m >= 4 && Next() % 2u == 0) { int t1 = (int)(Next() % (uint)m); if (t1 != t0 && !src.Contains(t1)) dst.Add(t1); }
                g.source = src.ToArray();
                g.sink = dst.ToArray();
                CheckGraph("random #" + i, g);
                cases++;
            }

            TestContext.Out.WriteLine(cases + " random graphs agreed (the flow, the source side, the cut and its order) with the exhaustive check");
            Assert.That(cases, Is.GreaterThanOrEqualTo(300));
        }

        [Test]
        public void Dinic_AndEdmondsKarp_GiveTheSameSearch_OnEveryFixture()
        {
            SpatialSplitGraphCandidateNative.Warmup(0, true, false);
            SpatialSplitGraphCandidateNative.Warmup(0, true, true);
            int fixtures = 0, withCuts = 0;
            foreach (SpatialSplitSample sample in Fixtures())
            {
                fixtures++;
                var ek = new CandidateSettings { stages = CandidateStages.AB, flowDinic = false };
                var dinic = new CandidateSettings { stages = CandidateStages.AB, flowDinic = true };
                CandidateResult a = SpatialSplitGraphCandidateNative.Decide(sample, ek, true, out SpatialSplitGraphCandidateNative.NativeRun ra);
                CandidateResult b = SpatialSplitGraphCandidateNative.Decide(sample, dinic, true, out SpatialSplitGraphCandidateNative.NativeRun rb);
                AssertSame(sample.objectName, a, b, "N1 dinic vs ek");
                Assert.That(ra.flowDinic, Is.False);
                Assert.That(rb.flowDinic, Is.True);
                Assert.That(rb.burstRan, Is.EqualTo(BurstCompiler.IsEnabled));
                if (a.cutAttempts > 0) withCuts++;
                var diag = new CandidateSettings { stages = CandidateStages.AB, flowDinic = true, measure = 2 };
                SpatialSplitGraphCandidateNative.Decide(sample, diag, true, out SpatialSplitGraphCandidateNative.NativeRun rd);
                int li = SpatialSplitGraphCandidateNative.CntLevelBuilds - SpatialSplitGraphCandidateNative.SectionTimes;
                int mi = SpatialSplitGraphCandidateNative.CntMinCutCalls - SpatialSplitGraphCandidateNative.SectionTimes;
                if (a.cutAttempts > 0) Assert.That(rd.counters[li], Is.GreaterThanOrEqualTo(rd.counters[mi]), sample.objectName + ": at least one level graph a minimum cut");
            }

            TestContext.Out.WriteLine(fixtures + " fixtures, " + withCuts + " with cuts");
            Assert.That(withCuts, Is.GreaterThanOrEqualTo(4));
        }

        private static void AssertBits(string name, double3[] a, double3[] b)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length), name + ": length");
            for (int i = 0; i < a.Length; i++)
            {
                Assert.That(System.BitConverter.DoubleToInt64Bits(b[i].x), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a[i].x)), name + "[" + i + "].x bits");
                Assert.That(System.BitConverter.DoubleToInt64Bits(b[i].y), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a[i].y)), name + "[" + i + "].y bits");
                Assert.That(System.BitConverter.DoubleToInt64Bits(b[i].z), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a[i].z)), name + "[" + i + "].z bits");
            }
        }

        private static void AssertBits(string name, double[] a, double[] b)
        {
            Assert.That(b.Length, Is.EqualTo(a.Length), name + ": length");
            for (int i = 0; i < a.Length; i++) Assert.That(System.BitConverter.DoubleToInt64Bits(b[i]), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(a[i])), name + "[" + i + "] bits");
        }

        private static void AssertSnapshot(string name, SpatialSplitGraphCandidateNative.PrepareSnapshot a, SpatialSplitGraphCandidateNative.PrepareSnapshot b)
        {
            Assert.That(b.nodes, Is.EqualTo(a.nodes), name + ": nodes");
            AssertBits(name + ": owner", a.owner, b.owner);
            Assert.That(b.cornerSlot, Is.EqualTo(a.cornerSlot), name + ": corner slots");
            Assert.That(b.triComponent, Is.EqualTo(a.triComponent), name + ": component numbers, in order");
            AssertBits(name + ": node lo", a.nodeLo, b.nodeLo);
            AssertBits(name + ": node hi", a.nodeHi, b.nodeHi);
            Assert.That(b.nodeHullStart, Is.EqualTo(a.nodeHullStart), name + ": hull starts");
            Assert.That(b.nodeTriangleCount, Is.EqualTo(a.nodeTriangleCount), name + ": triangle counts");
            AssertBits(name + ": hull (the vertex lists, in order)", a.hull, b.hull);
            AssertBits(name + ": bounds (2m, E1, far, near)", a.bounds, b.bounds);
            Assert.That(b.candidates, Is.EqualTo(a.candidates), name + ": candidate pairs, in order");
            AssertBits(name + ": gaps", a.gaps, b.gaps);
        }

        [Test]
        public void ThePreparationsOutputs_AreBitIdentical_AcrossTheMeasureModes_AndBurstAgainstManaged()
        {
            foreach (int mode in new[] { 0, 1, 2 }) SpatialSplitGraphCandidateNative.Warmup(mode, true, true);
            int fixtures = 0;
            foreach (SpatialSplitSample sample in Fixtures())
            {
                fixtures++;
                var snaps = new List<SpatialSplitGraphCandidateNative.PrepareSnapshot>();
                var runs = new List<SpatialSplitGraphCandidateNative.NativeRun>();
                var results = new List<CandidateResult>();
                foreach ((bool burst, int measure) in new[] { (true, 0), (true, 1), (true, 2), (false, 0) })
                {
                    var snap = new SpatialSplitGraphCandidateNative.PrepareSnapshot();
                    CandidateResult c = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, measure = measure }, burst, out SpatialSplitGraphCandidateNative.NativeRun run, snap);
                    Assert.That(c.verdict, Is.Not.EqualTo(CandidateVerdict.InvalidInput), sample.objectName + ": " + c.reason);
                    snaps.Add(snap); runs.Add(run); results.Add(c);
                }

                AssertSnapshot(sample.objectName + " [measure 1 vs 0]", snaps[0], snaps[1]);
                AssertSnapshot(sample.objectName + " [measure 2 vs 0]", snaps[0], snaps[2]);
                AssertSnapshot(sample.objectName + " [N0 vs N1]", snaps[0], snaps[3]);
                AssertSame(sample.objectName, results[0], results[1], "N1 measure 1");
                AssertSame(sample.objectName, results[0], results[2], "N1 measure 2");
                AssertSame(sample.objectName, results[0], results[3], "N0");
                // measure 0 reads no clock: its prepare sections stay zero; measure 1's sections sum to no more than the prepare job's time
                foreach (double s in runs[0].prepareSectionMs) Assert.That(s, Is.Zero, sample.objectName + ": no section time with the measurement off");
                double sum = 0.0;
                foreach (double s in runs[1].prepareSectionMs) { Assert.That(s, Is.GreaterThanOrEqualTo(0.0)); sum += s; }
                Assert.That(sum, Is.LessThanOrEqualTo(runs[1].secondsPrepare * 1000.0 * 1.05 + 0.01), sample.objectName + ": the prepare sections do not exceed the job's time");
                // the counts: the named vertices, the triangles, the components, the nodes and the pairs match the snapshot and the result
                long[] pc = runs[2].prepareCounters;
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcTriangles - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(results[0].triangleCount), sample.objectName + ": triangles counted");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcComponents - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(results[0].meshComponents), sample.objectName + ": components counted");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcNodes - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(snaps[0].nodes), sample.objectName + ": nodes counted");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcPairsTotal - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(results[0].nodePairs), sample.objectName + ": the theoretical pairs");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcGapEvals - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(results[0].nodePairs), sample.objectName + ": every pair's gap evaluated once (no sweep yet)");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcCandidates - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(results[0].candidatesA), sample.objectName + ": candidates counted");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcApart - SpatialSplitGraphCandidateNative.PcNamed] + pc[SpatialSplitGraphCandidateNative.PcCandidates - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(results[0].nodePairs), sample.objectName + ": apart + candidates = pairs");
                Assert.That(pc[SpatialSplitGraphCandidateNative.PcHullStored - SpatialSplitGraphCandidateNative.PcNamed], Is.EqualTo(snaps[0].hull.Length), sample.objectName + ": stored hull vertices");
            }

            TestContext.Out.WriteLine(fixtures + " fixtures: the preparation's outputs bit-identical across measure 0/1/2 and N0/N1");
        }

        // ---- the candidate enumeration: every pair against the sweep on box sets ----------------------------------------------

        private static (int2[] pairs, double[] gaps, SpatialSplitGraphCandidateNative.EnumerateCounts counts) Enumerate(double3[] lo, double3[] hi, double far, bool sweep)
        {
            int n = lo.Length;
            var nlo = new Unity.Collections.NativeArray<double3>(System.Math.Max(1, n), Unity.Collections.Allocator.Temp);
            var nhi = new Unity.Collections.NativeArray<double3>(System.Math.Max(1, n), Unity.Collections.Allocator.Temp);
            for (int i = 0; i < n; i++) { nlo[i] = lo[i]; nhi[i] = hi[i]; }
            var candidates = new Unity.Collections.NativeList<int2>(4, Unity.Collections.Allocator.Temp);
            var gaps = new Unity.Collections.NativeList<double>(4, Unity.Collections.Allocator.Temp);
            var counts = new SpatialSplitGraphCandidateNative.EnumerateCounts();
            if (sweep)
            {
                var order = new Unity.Collections.NativeArray<SpatialSplitGraphCandidateNative.NodeKey>(System.Math.Max(1, n), Unity.Collections.Allocator.Temp);
                var keyGaps = new Unity.Collections.NativeList<SpatialSplitGraphCandidateNative.KeyGap>(4, Unity.Collections.Allocator.Temp);
                SpatialSplitGraphCandidateNative.PairKernel.Sweep(nlo, nhi, n, far, order, keyGaps, candidates, gaps, true, ref counts);
                order.Dispose(); keyGaps.Dispose();
            }
            else
            {
                SpatialSplitGraphCandidateNative.PairKernel.AllPairs(nlo, nhi, n, far, candidates, gaps, true, ref counts);
            }

            var pairs = candidates.AsArray().ToArray();
            var g = gaps.AsArray().ToArray();
            nlo.Dispose(); nhi.Dispose(); candidates.Dispose(); gaps.Dispose();
            return (pairs, g, counts);
        }

        private static void AssertSweepAgrees(string name, double3[] lo, double3[] hi, double far)
        {
            (int2[] p0, double[] g0, SpatialSplitGraphCandidateNative.EnumerateCounts c0) = Enumerate(lo, hi, far, false);
            (int2[] p1, double[] g1, SpatialSplitGraphCandidateNative.EnumerateCounts c1) = Enumerate(lo, hi, far, true);
            Assert.That(p1, Is.EqualTo(p0), name + ": the candidate pairs, in the (i, j) order");
            for (int k = 1; k < p1.Length; k++) Assert.That(((long)p1[k].x * lo.Length) + p1[k].y, Is.GreaterThan(((long)p1[k - 1].x * lo.Length) + p1[k - 1].y), name + ": strictly increasing keys (no duplicate)");
            AssertBits(name + ": gaps", g0, g1);
            long n = lo.Length;
            Assert.That(c1.gapEvals + c1.sweepSkipped, Is.EqualTo(n * (n - 1) / 2), name + ": evaluated + skipped = every pair");
            Assert.That(c1.gapEvals, Is.LessThanOrEqualTo(c0.gapEvals), name + ": the sweep evaluates no more pairs");
            Assert.That(c1.excluded + p1.Length, Is.EqualTo(c1.gapEvals), name + ": evaluated = excluded + candidates");
            Assert.That(c1.keysSorted, Is.EqualTo(p1.Length));
        }

        private static (double3[] lo, double3[] hi) Boxes(params (double x0, double y0, double z0, double x1, double y1, double z1)[] b)
        {
            var lo = new double3[b.Length];
            var hi = new double3[b.Length];
            for (int i = 0; i < b.Length; i++) { lo[i] = new double3(b[i].x0, b[i].y0, b[i].z0); hi[i] = new double3(b[i].x1, b[i].y1, b[i].z1); }
            return (lo, hi);
        }

        [Test]
        public void TheSweep_GivesEveryPairsCandidates_InOrder_WithTheSameGaps_OnTheListedShapes()
        {
            double far = (2.0 * M) + (4.0 * 21.0 * System.Math.Pow(2.0, -44));
            (double3[] lo, double3[] hi) = Boxes();
            AssertSweepAgrees("no node", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1));
            AssertSweepAgrees("one node", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1), (2, 0, 0, 3, 1, 1));
            AssertSweepAgrees("two nodes apart", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1), (0, 0, 0, 1, 1, 1), (0, 0, 0, 1, 1, 1));
            AssertSweepAgrees("the same box thrice", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 10, 10, 10), (4, 4, 4, 5, 5, 5), (1, 1, 1, 2, 2, 2));
            AssertSweepAgrees("containment", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 100, 100, 100), (0.5, 0.5, 0.5, 0.6, 0.6, 0.6), (150, 0, 0, 151, 1, 1), (99, 99, 99, 200, 200, 200));
            AssertSweepAgrees("mixed sizes", lo, hi, far);
            double under = 1.0 + far - (far / 8.0), at = 1.0 + far, over = 1.0 + far + (far / 8.0);
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1), (under, 0, 0, under + 1, 1, 1), (at, 5, 0, at + 1, 6, 1), (over, 10, 0, over + 1, 11, 1), (1, 20, 0, 2, 21, 1));
            AssertSweepAgrees("under, at, over far along x; the same end point", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1), (0, 5, 0, 1, 6, 1), (0, 5 + under, 0, 1, 6 + under, 1), (0, 0, at + 0.0, 1, 1, at + 1.0), (0, 30, 0, 1, 31, 1));
            AssertSweepAgrees("under, at, over far along y and z (all overlapping on x)", lo, hi, far);
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1), (0, 10, 0, 1, 11, 1), (0, 20, 0, 1, 21, 1), (0, 30, 0, 1, 31, 1), (0, 40, 0, 1, 41, 1), (0, 50, 0, 1, 51, 1));
            AssertSweepAgrees("every box overlaps on x and is apart on y", lo, hi, far);
            double big = 4096.0;
            (lo, hi) = Boxes((big, big, big, big + 1, big + 1, big + 1), (big + 1 + far - (far / 8.0), big, big, big + 2, big + 1, big + 1), (big + 1 + far, big, big, big + 2, big + 1, big + 1), (big + 1 + far + (far / 8.0), big, big, big + 2, big + 1, big + 1));
            AssertSweepAgrees("large coordinates with small separations", lo, hi, far);
            var many = new List<(double, double, double, double, double, double)> { (0, 0, 0, 100, 100, 100) };
            for (int i = 0; i < 40; i++) many.Add((2.0 * i, 3.0 * (i % 7), 1.0 * (i % 5), (2.0 * i) + 0.5, (3.0 * (i % 7)) + 0.5, (1.0 * (i % 5)) + 0.5));
            (lo, hi) = Boxes(many.ToArray());
            AssertSweepAgrees("a big box over many small ones", lo, hi, far);
            // the same set in another input order
            var permuted = new List<(double, double, double, double, double, double)>(many);
            permuted.Reverse();
            (lo, hi) = Boxes(permuted.ToArray());
            AssertSweepAgrees("the same set reversed", lo, hi, far);
            // ties in the sort key: equal least x with different indices
            (lo, hi) = Boxes((0, 0, 0, 1, 1, 1), (0, 2, 0, 1, 3, 1), (0, 0, 2, 1, 1, 3), (0, 2, 2, 2, 3, 3));
            AssertSweepAgrees("ties of the least x", lo, hi, far);
            // deterministic random sets
            uint state = 2024u;
            uint Next() { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return state; }
            double R() => (Next() % 100000u) / 1000.0;
            int cases = 0;
            for (int r = 0; r < 300; r++)
            {
                int n = (int)(Next() % 30u);
                var set = new List<(double, double, double, double, double, double)>();
                for (int i = 0; i < n; i++)
                {
                    double x = R(), y = R(), z = R(), w = (Next() % 4u == 0) ? R() * 0.5 : 0.01 + ((Next() % 100u) / 100.0);
                    set.Add((x, y, z, x + w, y + w * ((Next() % 3u) + 1), z + w));
                }

                (lo, hi) = Boxes(set.ToArray());
                AssertSweepAgrees("random #" + r, lo, hi, far * (1 + (Next() % 50u)));
                cases++;
            }

            TestContext.Out.WriteLine(cases + " random box sets: the sweep agreed with every-pair on the candidates, their order and their gaps");
        }

        // A Burst-compiled job around the kernel, so that the boundary cases run through the Burst path as well.
        [BurstCompile(FloatMode = Unity.Burst.FloatMode.Strict, FloatPrecision = Unity.Burst.FloatPrecision.Standard, CompileSynchronously = true)]
        private struct PairKernelJob : Unity.Jobs.IJob
        {
            [Unity.Collections.ReadOnly] public Unity.Collections.NativeArray<double3> lo, hi;
            public int n;
            public double far;
            public int sweep;
            public Unity.Collections.NativeArray<SpatialSplitGraphCandidateNative.NodeKey> order;
            public Unity.Collections.NativeList<SpatialSplitGraphCandidateNative.KeyGap> keyGaps;
            public Unity.Collections.NativeList<int2> candidates;
            public Unity.Collections.NativeList<double> gaps;
            public Unity.Collections.NativeArray<long> marker;

            public void Execute()
            {
                Managed(marker);
                var c = new SpatialSplitGraphCandidateNative.EnumerateCounts();
                if (sweep != 0) SpatialSplitGraphCandidateNative.PairKernel.Sweep(lo, hi, n, far, order, keyGaps, candidates, gaps, false, ref c);
                else SpatialSplitGraphCandidateNative.PairKernel.AllPairs(lo, hi, n, far, candidates, gaps, false, ref c);
            }

            [BurstDiscard]
            private static void Managed(Unity.Collections.NativeArray<long> marker) => marker[0] = 1;
        }

        private static (int2[] pairs, double[] gaps, bool burst) EnumerateThroughBurst(double3[] lo, double3[] hi, double far, bool sweep)
        {
            int n = lo.Length;
            var job = new PairKernelJob
            {
                lo = new Unity.Collections.NativeArray<double3>(System.Math.Max(1, n), Unity.Collections.Allocator.TempJob), hi = new Unity.Collections.NativeArray<double3>(System.Math.Max(1, n), Unity.Collections.Allocator.TempJob),
                n = n, far = far, sweep = sweep ? 1 : 0,
                order = new Unity.Collections.NativeArray<SpatialSplitGraphCandidateNative.NodeKey>(System.Math.Max(1, n), Unity.Collections.Allocator.TempJob),
                keyGaps = new Unity.Collections.NativeList<SpatialSplitGraphCandidateNative.KeyGap>(4, Unity.Collections.Allocator.TempJob),
                candidates = new Unity.Collections.NativeList<int2>(4, Unity.Collections.Allocator.TempJob), gaps = new Unity.Collections.NativeList<double>(4, Unity.Collections.Allocator.TempJob),
                marker = new Unity.Collections.NativeArray<long>(1, Unity.Collections.Allocator.TempJob),
            };
            for (int i = 0; i < n; i++) { job.lo[i] = lo[i]; job.hi[i] = hi[i]; }
            job.Run();
            var pairs = job.candidates.AsArray().ToArray();
            var g = job.gaps.AsArray().ToArray();
            bool burst = job.marker[0] == 0;
            job.lo.Dispose(); job.hi.Dispose(); job.order.Dispose(); job.keyGaps.Dispose(); job.candidates.Dispose(); job.gaps.Dispose(); job.marker.Dispose();
            return (pairs, g, burst);
        }

        [Test]
        public void TheSweep_KeepsThePairAtFarExactly_AndDecidesItsRepresentableNeighbours_DirectlyAndThroughBurst()
        {
            // far as the job computes it for a fixture of coordinates up to 21 (U = 21): 2m + 4 E1 = 2^-11 + 21 * 2^-42, exact in double.
            double far = (2.0 / 4096.0) + (21.0 * System.Math.Pow(2.0, -42));
            double farUp = Increment(far), farDown = Decrement(far);
            // Boxes aligned on y and z (so that no other axis excludes the pair): A = [-1, 0] on x; B's lo.x = far exactly, its neighbours.
            (double3[] lo, double3[] hi) = Boxes((-1, 0, 0, 0, 1, 1), (far, 0, 0, far + 1, 1, 1), (farDown, 5, 0, farDown + 1, 6, 1), (farUp, 10, 0, farUp + 1, 11, 1), (-1, 5, 0, 0, 6, 1), (-1, 10, 0, 0, 11, 1));
            // Expected by the rule gap > far excludes: (A, at) kept with gap == far; (A-at-y5, down) kept; (A-at-y10, up) excluded.
            foreach (bool sweep in new[] { false, true })
            {
                (int2[] p, double[] g, SpatialSplitGraphCandidateNative.EnumerateCounts c) = Enumerate(lo, hi, far, sweep);
                (int2[] pb, double[] gb, bool burst) = EnumerateThroughBurst(lo, hi, far, sweep);
                string name = sweep ? "sweep" : "all";
                Assert.That(burst, Is.EqualTo(BurstCompiler.IsEnabled), name + ": the kernel ran through Burst iff enabled");
                Assert.That(pb, Is.EqualTo(p), name + ": Burst and direct give the same pairs");
                AssertBits(name + ": Burst and direct gaps", g, gb);
                var set = new HashSet<(int, int)>();
                foreach (int2 q in p) set.Add((q.x, q.y));
                Assert.That(set.Contains((0, 1)), Is.True, name + ": the pair at far exactly is a candidate");
                int at = System.Array.FindIndex(p, q => q.x == 0 && q.y == 1);
                Assert.That(System.BitConverter.DoubleToInt64Bits(g[at]), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(far)), name + ": its gap is far, bit for bit");
                Assert.That(set.Contains((2, 4)), Is.True, name + ": the pair a representable step under far is a candidate");
                Assert.That(set.Contains((3, 5)), Is.False, name + ": the pair a representable step over far is excluded");
            }

            AssertSweepAgrees("at far and its neighbours", lo, hi, far);
            // The same boundary along y (every box overlapping on x, the sweep cannot skip): at, under, over.
            (lo, hi) = Boxes((0, -1, 0, 1, 0, 1), (0, far, 0, 1, far + 1, 1), (0, farDown, 5, 1, farDown + 1, 6), (0, farUp, 10, 1, farUp + 1, 11), (0, -1, 5, 1, 0, 6), (0, -1, 10, 1, 0, 11));
            AssertSweepAgrees("at far and its neighbours along y", lo, hi, far);
            (int2[] py, double[] gy, SpatialSplitGraphCandidateNative.EnumerateCounts cy) = Enumerate(lo, hi, far, true);
            var sy = new HashSet<(int, int)>();
            foreach (int2 q in py) sy.Add((q.x, q.y));
            Assert.That(sy.Contains((0, 1)) && sy.Contains((2, 4)) && !sy.Contains((3, 5)), Is.True, "along y: at and under kept, over excluded");
        }

        private static double Increment(double x) => System.BitConverter.Int64BitsToDouble(System.BitConverter.DoubleToInt64Bits(x) + 1);
        private static double Decrement(double x) => System.BitConverter.Int64BitsToDouble(System.BitConverter.DoubleToInt64Bits(x) - 1);

        [Test]
        public void TheSweep_AndEveryPair_GiveTheSamePreparationAndSearch_OnEveryFixture()
        {
            foreach (int mode in new[] { 0, 1, 2 }) { SpatialSplitGraphCandidateNative.Warmup(mode, true, true, false); SpatialSplitGraphCandidateNative.Warmup(mode, true, true, true); }
            int fixtures = 0;
            foreach (SpatialSplitSample sample in Fixtures())
            {
                fixtures++;
                var snapAll = new SpatialSplitGraphCandidateNative.PrepareSnapshot();
                var snapSweep = new SpatialSplitGraphCandidateNative.PrepareSnapshot();
                var snapSweepN0 = new SpatialSplitGraphCandidateNative.PrepareSnapshot();
                CandidateResult a = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, pairsSweep = false }, true, out SpatialSplitGraphCandidateNative.NativeRun ra, snapAll);
                CandidateResult b = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, pairsSweep = true }, true, out SpatialSplitGraphCandidateNative.NativeRun rb, snapSweep);
                CandidateResult b0 = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, pairsSweep = true }, false, out SpatialSplitGraphCandidateNative.NativeRun rb0, snapSweepN0);
                AssertSnapshot(sample.objectName + " [sweep vs all]", snapAll, snapSweep);
                AssertSnapshot(sample.objectName + " [sweep N0 vs N1]", snapSweep, snapSweepN0);
                if (sample.objectName == FarExactly)
                {
                    // the whole job (Burst): the fixture's pair (0, 1) is at the job's own far, bit for bit, and a candidate under both enumerations
                    double farStated = (2.0 / 4096.0) + (21.0 * System.Math.Pow(2.0, -42));
                    Assert.That(System.BitConverter.DoubleToInt64Bits(snapSweep.bounds[2]), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(farStated)), FarExactly + ": the job's far is the stated one");
                    int at = System.Array.IndexOf(snapSweep.candidates, new int2(0, 1));
                    Assert.That(at, Is.GreaterThanOrEqualTo(0), FarExactly + ": the pair (0, 1) is a candidate");
                    Assert.That(System.BitConverter.DoubleToInt64Bits(snapSweep.gaps[at]), Is.EqualTo(System.BitConverter.DoubleToInt64Bits(snapSweep.bounds[2])), FarExactly + ": its gap is far exactly");
                    Assert.That(System.Array.IndexOf(snapAll.candidates, new int2(0, 1)), Is.EqualTo(at), FarExactly + ": at the same place under every pair");
                    Assert.That(System.Array.IndexOf(snapSweep.candidates, new int2(0, 2)), Is.LessThan(0), FarExactly + ": the far box is apart");
                }
                AssertSame(sample.objectName, a, b, "N1 sweep");
                AssertSame(sample.objectName, a, b0, "N0 sweep");
                Assert.That(ra.pairsSweep, Is.False);
                Assert.That(rb.pairsSweep, Is.True);
                Assert.That(rb.burstRan, Is.EqualTo(BurstCompiler.IsEnabled));
                Assert.That(rb.sweepBytes, Is.GreaterThan(0), "the sweep's added areas are accounted");
                Assert.That(ra.sweepBytes, Is.Zero);
                // the verification hash of the candidate list and gaps agrees between all and sweep
                SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, pairsSweep = false, hashPrepare = true }, true, out SpatialSplitGraphCandidateNative.NativeRun ha);
                SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, pairsSweep = true, hashPrepare = true }, true, out SpatialSplitGraphCandidateNative.NativeRun hb);
                Assert.That(ha.prepareHash.Length, Is.EqualTo(16));
                Assert.That(hb.prepareHash, Is.EqualTo(ha.prepareHash), sample.objectName + ": the prepare hash agrees");
                Assert.That(ra.prepareHash, Is.Empty, "no hash in a run that did not ask for it");
                foreach (int mode in new[] { 1, 2 })
                {
                    var snapMode = new SpatialSplitGraphCandidateNative.PrepareSnapshot();
                    CandidateResult m = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB, nearestSelection = true, flowDinic = true, pairsSweep = true, measure = mode }, true, out SpatialSplitGraphCandidateNative.NativeRun rm, snapMode);
                    AssertSnapshot(sample.objectName + " [sweep measure " + mode + "]", snapSweep, snapMode);
                    AssertSame(sample.objectName, a, m, "N1 sweep measure " + mode);
                    if (mode == 2)
                    {
                        long[] pc = rm.prepareCounters;
                        int at(int c) => c - SpatialSplitGraphCandidateNative.PcNamed;
                        Assert.That(pc[at(SpatialSplitGraphCandidateNative.PcGapEvals)] + pc[at(SpatialSplitGraphCandidateNative.PcSweepSkipped)], Is.EqualTo(a.nodePairs), sample.objectName + ": evaluated + skipped = every pair");
                        Assert.That(pc[at(SpatialSplitGraphCandidateNative.PcApart)], Is.EqualTo(a.pairsApartByBoxes), sample.objectName + ": apart counted as before");
                        Assert.That(pc[at(SpatialSplitGraphCandidateNative.PcKeysSorted)], Is.EqualTo(a.candidatesA), sample.objectName + ": keys sorted = candidates");
                    }
                }
            }

            TestContext.Out.WriteLine(fixtures + " fixtures: the sweep's preparation and search identical to every-pair's");
        }

        [Test]
        public void AnIndexNamingAMissingVertex_IsAnInvalidInput_AndABCIsRefused()
        {
            (float3[] p, int[] i) = Mesh(At(1.5f), FarTri);
            SpatialSplitSample sample = Fixture(new[] { Unit, FarBox }, p, i, name: "bad index");
            sample.indices[4] = 99;
            foreach (bool burst in new[] { false, true })
            {
                CandidateResult x = SpatialSplitGraphCandidateNative.Decide(sample, new CandidateSettings { stages = CandidateStages.AB }, burst, out _);
                Assert.That(x.verdict, Is.EqualTo(CandidateVerdict.InvalidInput), x.reason);
                Assert.That(x.reason, Does.Contain("does not hold"));
            }

            CandidateResult m = SpatialSplitGraphCandidate.Decide(sample, new CandidateSettings { stages = CandidateStages.AB });
            Assert.That(m.verdict, Is.EqualTo(CandidateVerdict.InvalidInput));
            (float3[] p2, int[] i2) = Mesh(At(1.5f), FarTri);
            CandidateResult abc = SpatialSplitGraphCandidateNative.Decide(Fixture(new[] { Unit, FarBox }, p2, i2, name: "abc"), new CandidateSettings { stages = CandidateStages.ABC }, false, out _);
            Assert.That(abc.verdict, Is.EqualTo(CandidateVerdict.InvalidInput));
            Assert.That(abc.reason, Does.Contain("AB only"));
        }
    }
}
