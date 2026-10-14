using System;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Zantetsu.Core.Statistics;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The statistics' frame path allocates nothing once it runs (DESIGN 21.18): the sampler's read and the collector's
    /// frame, counters, capacities and settings, called as the service calls them every frame. Measured here, in the
    /// Editor, by the engine's record of managed allocations (the test framework's constraint), first shown to see a
    /// known allocation. (This thread's allocated-bytes counter is not used: it stays at 0 for a known allocation both
    /// in the Editor and in the IL2CPP Player, where the verification build found it inert.) What the service itself
    /// reads from the engine and the world each frame is not in this test.
    /// </summary>
    public sealed class PlayStatisticsAllocationTests
    {
        [Test]
        public void TheFramePath_OfTheSamplerAndTheCollector_AllocatesNothing_OnceItRuns()
        {
            // The control: the record sees a known allocation here.
            Assert.That(() => { byte[] probe = new byte[4096]; GC.KeepAlive(probe); }, Is.AllocatingGCMemory());

            var statistics = new PlayStatistics();
            var collector = new PlayStatisticsCollector(statistics);
            var sampler = new FrameTimeSampler();
            collector.Agree();
            var conditions = new PlayConditions { Scene = "s", Mode = "play", DrawRoute = "VP3C-NATIVE", XrRuntime = "test", RefreshRateHz = 72f, RenderScale = 1f, EyeTextureWidth = 1680, EyeTextureHeight = 1760, MsaaSamples = 4 };
            var settings = new PlayUiSettings { Known = true, Volume = 0.5f, MoveSpeed = 2f, TurnDegrees = 30f, TurnIsContinuous = false, BladeAngleDegrees = 30f };

            // Before the measure: the epoch begins, and whatever the first calls make is made.
            for (int i = 0; i < 64; i++)
            {
                One(collector, sampler, in conditions, in settings, i);
            }

            ulong framesBefore = collector.Gate.Counted;
            Assert.That(() =>
            {
                for (int i = 64; i < 2064; i++)
                {
                    One(collector, sampler, in conditions, in settings, i);
                }
            }, Is.Not.AllocatingGCMemory(), "2,000 frames of the sampler and the collector");
            Assert.That(collector.Gate.Counted - framesBefore, NUnit.Framework.Is.EqualTo(2000UL), "every one of the frames was counted");
        }

        private static void One(PlayStatisticsCollector collector, FrameTimeSampler sampler, in PlayConditions conditions, in PlayUiSettings settings, int i)
        {
            sampler.Sample();   // the engine's read, whatever it gives here
            var sample = new FrameTimeSample { HasMain = true, MainMs = 2f + i % 5, HasRender = true, RenderMs = 3f, HasGpu = i % 40 != 0, GpuMs = 2.5f, HasGpuXr = false };
            collector.Frame(true, false, true, in conditions, 13.9f + i % 3, sample, i / 50);
            collector.Live(100 + i % 9);
            collector.Total((PlayTotalKind)0, 0, i);
            collector.Failure((PlayFailureKind)0, 0, 0, i / 100);
            collector.Capacity((PlayCapacityKind)0, 0, 100 + i, 4096, 65536, i / 500, 0);
            collector.Settings(in settings);
        }
    }
}
