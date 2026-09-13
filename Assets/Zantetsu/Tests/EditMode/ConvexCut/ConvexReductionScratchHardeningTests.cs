using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.ConvexCut.Tests.ProbeBaseline;

namespace Zantetsu.ConvexCut.Tests
{
    /// <summary>
    /// Documents a hardening found while porting: the probe reduction kernel never initialised the ring-walk
    /// successor array, so its first call on a zeroed scratch rejected every candidate once and could fall into the R1
    /// mode spuriously (a different removal sequence than on reused scratch). The ported kernel initialises it in Init;
    /// <see cref="ConvexReductionKernelTests.ScratchContents_DoNotChangeTheReduction"/> covers the ported side.
    /// </summary>
    public class ConvexReductionScratchHardeningTests
    {
        const int L = 128;

        static unsafe (int status, ReductionStats stats, ConvexBrepData output) BaselineReduce(SentinelArena arena, ConvexBrepData input, byte fill)
        {
            arena.Reset(fill);
            int V = input.V, E = input.E, F = input.F, I = input.I;
            var cap = new ClipCapacity { vOut = V, fOut = F + 2 * E + 4, iOut = I + 3 * (2 * E + 4), eOut = 3 * V + 2 * E + 8 };
            var io = input.Upload(arena.Alloc<float3>("io.v", cap.vOut), arena.Alloc<int>("io.faceOff", cap.fOut + 1), arena.Alloc<int>("io.faceIdx", cap.iOut), arena.Alloc<int>("io.faceEdge", cap.iOut), arena.Alloc<BrepEdge>("io.edges", cap.eOut));
            io.vCap = cap.vOut; io.fCap = cap.fOut; io.iCap = cap.iOut; io.eCap = cap.eOut;
            BaselineReductionScratch.Layout(null, V, E, F, I, out int bytes);
            var s = BaselineReductionScratch.Layout(arena.AllocPtr<byte>("reduction.scratch", bytes), V, E, F, I, out _);
            var stats = new ReductionStats();
            int status = BaselineReductionKernel.Reduce(ref io, L, ref s, ref stats);
            return (status, stats, status == (int)ReductionStatus.Ok ? ConvexBrepData.FromBuffer(in io) : null);
        }

        [Test]
        public void ProbeBaseline_FirstCallOnZeroedScratch_DiffersFromReusedScratch_PortedKernelDoesNot()
        {
            var corpus = ConvexReductionKernelTests.ExcessCorpus();
            int baselineDiffers = 0, portedDiffers = 0, compared = 0;
            using (var arena = new SentinelArena(16 << 20))
            {
                for (int k = 0; k < corpus.Count; k += 4)
                {
                    var data = ConvexBrepData.FromPoly(corpus[k].input);
                    var bZero = BaselineReduce(arena, data, 0x00);
                    var bFilled = BaselineReduce(arena, data, 0xA5);
                    if (bZero.status != bFilled.status || bZero.stats.r1Invoked != bFilled.stats.r1Invoked || bZero.stats.removedR0 != bFilled.stats.removedR0 || bZero.stats.evaluations != bFilled.stats.evaluations) baselineDiffers++;
                    var pZero = ReductionHarness.Reduce(arena, data, L, 0x00);
                    var pFilled = ReductionHarness.Reduce(arena, data, L, 0xA5);
                    if (pZero.status != pFilled.status || pZero.stats.r1Invoked != pFilled.stats.r1Invoked || pZero.stats.removedR0 != pFilled.stats.removedR0 || pZero.stats.evaluations != pFilled.stats.evaluations) portedDiffers++;
                    compared++;
                }
            }
            TestContext.Out.WriteLine("compared=" + compared + " probe baseline result depends on scratch contents in " + baselineDiffers + " cases; ported kernel in " + portedDiffers);
            Assert.That(portedDiffers, Is.EqualTo(0));
            Assert.That(baselineDiffers, Is.GreaterThan(0), "the probe baseline was expected to depend on zeroed scratch (nextOf uninitialised); if this no longer holds the hardening note in the port is stale");
        }
    }
}
