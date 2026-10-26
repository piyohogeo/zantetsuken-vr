using NUnit.Framework;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The connections of the play statistics (DESIGN 21.18), through the collector alone: the agreement begins the epoch
    /// of the first counted frame by itself (and a re-agreement after a withdrawal does so again); a change of
    /// conditions begins its epoch at once; the frame times that arrive late are set aside after every boundary until
    /// the lag has passed; the game's cumulative counters are counted from the agreement on and across a replacement
    /// of what keeps them.
    /// </summary>
    public sealed class PlayStatisticsCollectorTests
    {
        private static PlayConditions Conditions(string scene, bool debug = false)
        {
            return new PlayConditions { Scene = scene, Mode = "play", DrawRoute = "VP3C-NATIVE", XrRuntime = "test", RefreshRateHz = 72f, RenderScale = 1f, EyeTextureWidth = 1680, EyeTextureHeight = 1760, MsaaSamples = 4, CutSurfaceDebug = debug };
        }

        private static FrameTimeSample Full() => new FrameTimeSample { HasMain = true, MainMs = 8f, HasRender = true, RenderMs = 3f, HasGpu = true, GpuMs = 6f, HasGpuXr = true, GpuXrMs = 6.5f };

        // Ordinary frames under the same conditions, with a revision that does not change.
        private static int Play(PlayStatisticsCollector c, int frames, string scene = "WalkCity", long revision = 1, bool paused = false, bool focused = true, bool ready = true)
        {
            int counted = 0;
            for (int i = 0; i < frames; i++)
            {
                if (c.Frame(ready, paused, focused, Conditions(scene), 13.9f, Full(), revision)) counted++;
            }

            return counted;
        }

        [Test]
        public void TheAgreement_BeginsTheEpochOfTheFirstCountedFrame_ByItself_AndAReAgreementAfterAWithdrawalDoesSoAgain()
        {
            var s = new PlayStatistics();
            var c = new PlayStatisticsCollector(s);
            Assert.That(Play(c, 20), Is.Zero, "nothing before the agreement");
            Assert.That(s.HasPlay, Is.False);
            Assert.That(s.EpochCount, Is.Zero);

            c.Agree();
            int counted = Play(c, 20);
            // The first frame after the agreement is the gate's resume; the second begins the epoch (not counted); the rest count.
            Assert.That(counted, Is.EqualTo(18));
            Assert.That(s.EpochCount, Is.EqualTo(1), "the epoch of the frames' own conditions, begun without any other call");
            Assert.That(s.Current.Conditions, Is.EqualTo(Conditions("WalkCity")));
            Assert.That(s.Current.Frames, Is.EqualTo(18));
            Assert.That(s.FramesOutsideEpochs, Is.Zero, "no frame went outside an epoch");
            Assert.That(s.Current.PlaySeconds, Is.EqualTo(18 * 0.0139).Within(1e-6));

            c.Withdraw();
            Assert.That(s.HasPlay, Is.False, "cleared");
            Assert.That(s.EpochCount, Is.Zero);
            Assert.That(Play(c, 10), Is.Zero, "nothing after the withdrawal");
            Assert.That(s.HasPlay, Is.False);

            c.Agree();
            Assert.That(Play(c, 12), Is.EqualTo(10), "a re-agreement under the same conditions counts again");
            Assert.That(s.EpochCount, Is.EqualTo(1));
            Assert.That(s.Current.Frames, Is.EqualTo(10), "from nothing: what was counted before the withdrawal is not back");
        }

        [Test]
        public void AChangeOfConditions_BeginsItsEpochAtOnce_TheFrameOfTheChangeInNeither()
        {
            var s = new PlayStatistics();
            var c = new PlayStatisticsCollector(s);
            c.Agree();
            Play(c, 12, "WalkCity");
            Assert.That(s.EpochAt(0).Frames, Is.EqualTo(10));
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity", debug: true), 13.9f, Full(), 1), Is.False, "the frame of the change is not counted");
            Assert.That(s.EpochCount, Is.EqualTo(2), "the new conditions' epoch, begun in that frame");
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity", debug: true), 13.9f, Full(), 1), Is.True, "the next frame is the new epoch's (its interval is a frame's own)");
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity", debug: true), 13.9f, Full(), 1), Is.True);
            Assert.That(s.EpochAt(1).Frames, Is.EqualTo(2));
            Assert.That(s.EpochAt(1).Unattributed, Is.EqualTo(2), "their late times set aside: the lag starts over at the change");
            Assert.That(s.EpochAt(0).Frames, Is.EqualTo(10), "the old epoch took none of them");
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity"), 13.9f, Full(), 1), Is.False, "back to the first conditions: that frame of neither");
            Assert.That(s.EpochCount, Is.EqualTo(2), "conditions seen before: their epoch again, no new one");
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity"), 13.9f, Full(), 1), Is.True);
            Assert.That(s.EpochAt(0).Frames, Is.EqualTo(11));
        }

        [TestCase("screenWidth")]
        [TestCase("screenHeight")]
        [TestCase("orientation")]
        [TestCase("fsr")]
        [TestCase("renderScale")]
        [TestCase("multiDrawIndirect")]
        [TestCase("drawIndirectCount")]
        public void AndroidDrawingChanges_SeparateEpochs_AndSetAsideLateTimes(string change)
        {
            var s = new PlayStatistics();
            var c = new PlayStatisticsCollector(s);
            c.Agree();
            var original = new PlayConditions
            {
                Scene = "WalkCity", Mode = "play", DrawRoute = "VP3C-NATIVE", XrRuntime = PlayEnvironment.NoXr,
                ScreenWidth = 1080, ScreenHeight = 2400, FsrEnabled = true,
                RenderScale = change == "fsr" ? 1f : .75f, MultiDrawIndirect = true,
                DrawIndirectCount = change != "multiDrawIndirect", MsaaSamples = 4
            };
            int lag = PlayStatisticsCollector.AttributionLagFrames;
            for (int i = 0; i < lag + 4; i++) c.Frame(true, false, true, in original, 16.7f, Full(), 1);
            PlayEpoch first = s.Current;
            ulong firstFrames = first.Frames;
            Assert.That(first.Main.Samples, Is.EqualTo(2), "the old conditions already have attributed timing samples");

            PlayConditions changed = original;
            switch (change)
            {
                case "screenWidth": changed.ScreenWidth = 1440; break;
                case "screenHeight": changed.ScreenHeight = 2560; break;
                case "orientation": changed.ScreenWidth = original.ScreenHeight; changed.ScreenHeight = original.ScreenWidth; break;
                case "fsr": changed.FsrEnabled = false; break;
                case "renderScale": changed.RenderScale = .85f; break;
                case "multiDrawIndirect": changed.MultiDrawIndirect = false; break;
                case "drawIndirectCount": changed.DrawIndirectCount = false; break;
            }
            Assert.That(c.Frame(true, false, true, in changed, 16.7f, Full(), 1), Is.False, "the change frame belongs to neither distribution");
            Assert.That(s.EpochCount, Is.EqualTo(2), "this condition alone separates epochs even when the route label is unchanged");
            PlayEpoch second = s.Current;
            for (int i = 0; i < lag; i++)
                Assert.That(c.Frame(true, false, true, in changed, 16.7f, Full(), 1), Is.True);
            Assert.That(first.Frames, Is.EqualTo(firstFrames));
            Assert.That(first.Main.Samples, Is.EqualTo(2));
            Assert.That(second.Frames, Is.EqualTo(lag));
            Assert.That(second.Unattributed, Is.EqualTo(lag));
            Assert.That(second.Main.Samples, Is.Zero);
            Assert.That(second.Render.Samples, Is.Zero);
            Assert.That(second.Gpu.Samples, Is.Zero);
            Assert.That(second.MainMissing, Is.EqualTo(lag));
            Assert.That(second.RenderMissing, Is.EqualTo(lag));
            Assert.That(second.GpuMissing, Is.EqualTo(lag));
            Assert.That(second.Interval.Samples, Is.EqualTo(lag), "current frame intervals remain usable");
            Assert.That(c.Frame(true, false, true, in changed, 16.7f, Full(), 1), Is.True);
            Assert.That(second.Main.Samples, Is.EqualTo(1));
            Assert.That(second.Render.Samples, Is.EqualTo(1));
            Assert.That(second.Gpu.Samples, Is.EqualTo(1));

            Assert.That(c.Frame(true, false, true, in original, 16.7f, Full(), 1), Is.False);
            Assert.That(s.Current, Is.SameAs(first), "returning to an earlier drawing setup reuses its epoch");
            Assert.That(s.EpochCount, Is.EqualTo(2));
        }

        [Test]
        public void TheLateTimes_AreSetAsideAfterEveryBoundary_UntilTheLagHasPassed_AndAStructureThatDiffersAcrossAGapIsNoChange()
        {
            var s = new PlayStatistics();
            var c = new PlayStatisticsCollector(s);
            c.Agree();
            int lag = PlayStatisticsCollector.AttributionLagFrames;
            Assert.That(lag, Is.EqualTo(8));
            Play(c, 2);   // the resume and the epoch's beginning
            Play(c, lag);
            PlayEpoch e = s.Current;
            Assert.That(e.Frames, Is.EqualTo(lag));
            Assert.That(e.Unattributed, Is.EqualTo(lag), "every frame within the lag: its FrameTimingManager values set aside");
            Assert.That(e.MainMissing, Is.EqualTo(lag));
            Assert.That(e.Main.Samples, Is.Zero);
            Assert.That(e.GpuXrMissing, Is.EqualTo(1), "the XR display's time is the last frame's: one frame set aside");
            Assert.That(e.GpuXr.Samples, Is.EqualTo(lag - 1));
            Assert.That(e.Interval.Samples, Is.EqualTo(lag), "the interval is this frame's own: always taken");

            Play(c, 5);
            Assert.That(e.Frames, Is.EqualTo(lag + 5));
            Assert.That(e.Unattributed, Is.EqualTo(lag), "past the lag: taken");
            Assert.That(e.Main.Samples, Is.EqualTo(5));
            Assert.That(c.UnattributedFrames, Is.EqualTo(lag));

            // A pause: a boundary. The structure's revision differs after it -- not a change of the frame that resumes.
            Assert.That(Play(c, 3, paused: true), Is.Zero);
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity"), 13.9f, Full(), 2), Is.False, "the first frame back");
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity"), 13.9f, Full(), 2), Is.True);
            Assert.That(e.StructureChangedFrames, Is.Zero, "the revision 1 -> 2 across the gap is not counted as a change");
            Assert.That(c.Frame(true, false, true, Conditions("WalkCity"), 13.9f, Full(), 3), Is.True);
            Assert.That(e.StructureChangedFrames, Is.EqualTo(1), "a change between two counted frames is");
            Assert.That(e.Unattributed, Is.EqualTo(lag + 2), "the lag starts over after the boundary");
            Play(c, lag);
            Assert.That(e.Unattributed, Is.EqualTo(2 * lag));
            Assert.That(e.Main.Samples, Is.EqualTo(5 + 2), "the two frames past the second lag");
            Assert.That(s.EpochCount, Is.EqualTo(1), "the same conditions all along: one epoch");

            // The focus lost, the world not ready: boundaries too.
            Assert.That(Play(c, 1, focused: false), Is.Zero);
            Assert.That(Play(c, 1), Is.Zero);
            Assert.That(Play(c, 1, ready: false), Is.Zero);
            Assert.That(Play(c, 2), Is.EqualTo(1));
            Assert.That(e.Unattributed, Is.EqualTo(2 * lag + 1));
        }

        [Test]
        public void TheCounters_StartFromTheAgreement_AndCarryOverAReplacementOfWhatKeepsThem()
        {
            var s = new PlayStatistics();
            var c = new PlayStatisticsCollector(s);
            // Before the agreement: not read.
            c.Total(PlayTotalKind.AcceptedCuts, 1, 50);
            c.Failure(PlayFailureKind.RingFull, 1, 9);
            Assert.That(s.AcceptedCuts, Is.Zero);
            Assert.That(s.Failures(PlayFailureKind.RingFull), Is.Zero);

            c.Agree();
            c.Total(PlayTotalKind.AcceptedCuts, 1, 50);   // the first reading after the agreement: the base
            Assert.That(s.AcceptedCuts, Is.Zero, "the 50 cuts before the agreement are not counted");
            c.Total(PlayTotalKind.AcceptedCuts, 1, 53);
            Assert.That(s.AcceptedCuts, Is.EqualTo(3));
            c.Total(PlayTotalKind.AcceptedCuts, 2, 2);    // the world replaced: its counter starts over
            Assert.That(s.AcceptedCuts, Is.EqualTo(5), "the 3 carried, the new world's 2 added");
            c.Total(PlayTotalKind.AcceptedCuts, 2, 1);    // a counter that fell within its generation: a new source
            Assert.That(s.AcceptedCuts, Is.EqualTo(6));
            c.Total(PlayTotalKind.AcceptedCuts, 2, 1);
            Assert.That(s.AcceptedCuts, Is.EqualTo(6), "the same reading twice adds nothing");

            c.Failure(PlayFailureKind.RingFull, 1, 9);
            c.Failure(PlayFailureKind.RingFull, 1, 11);
            c.Failure(PlayFailureKind.RingFull, 2, 0);
            c.Failure(PlayFailureKind.RingFull, 2, 4);
            Assert.That(s.Failures(PlayFailureKind.RingFull), Is.EqualTo(6));

            c.Capacity(PlayCapacityKind.CpuVertices, 1, end: 1000, capacity: 4096, growthsTotal: 7, shortfallsTotal: 2);
            c.Capacity(PlayCapacityKind.CpuVertices, 1, end: 900, capacity: 4096, growthsTotal: 8, shortfallsTotal: 2);
            PlayCapacity v = s.Capacity(PlayCapacityKind.CpuVertices);
            Assert.That(v.MaxEnd, Is.EqualTo(1000), "the highest end in use");
            Assert.That(v.MinHeadroomToCapacity, Is.EqualTo(3096), "the least headroom to the capacity");
            Assert.That(v.Growths, Is.EqualTo(1), "growths from the agreement: 8 - 7");
            Assert.That(v.Shortfalls, Is.Zero);

            // A failure kept by several sources (the camera drawings' routes): each tracked apart, the statistic their sum.
            c.Failure(PlayFailureKind.DrawRefused, source: 2, generation: 1, total: 2);
            c.Failure(PlayFailureKind.DrawRefused, source: 3, generation: 1, total: 3);
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.Zero, "the first readings are the bases");
            c.Failure(PlayFailureKind.DrawRefused, source: 2, generation: 1, total: 4);
            c.Failure(PlayFailureKind.DrawRefused, source: 3, generation: 1, total: 6);
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.EqualTo(5), "2 + 3 since the agreement");
            c.Failure(PlayFailureKind.DrawRefused, source: 2, generation: 2, total: 0);   // that route remade: its count starts over
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.EqualTo(5), "no failure happened: nothing added, nothing lost");
            c.Failure(PlayFailureKind.DrawRefused, source: 2, generation: 2, total: 1);
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.EqualTo(6));
            c.Failure(PlayFailureKind.DrawRefused, source: PlayStatisticsCollector.MaxSources, generation: 1, total: 100);
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.EqualTo(6), "a source past the limit is not counted");

            // Withdrawn and agreed again with several sources: no source read before the withdrawal comes back into the sum
            // (TL review 3: the first source's new base must not be summed with the second's old value).
            c.Withdraw();
            c.Agree();
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.Zero);
            c.Failure(PlayFailureKind.DrawRefused, source: 2, generation: 2, total: 1);   // the first source read: its base
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.Zero, "the second source, not yet read again, adds nothing of its old count");
            c.Failure(PlayFailureKind.DrawRefused, source: 3, generation: 1, total: 6);   // the second source read: its base
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.Zero);
            c.Failure(PlayFailureKind.DrawRefused, source: 3, generation: 1, total: 7);
            Assert.That(s.Failures(PlayFailureKind.DrawRefused), Is.EqualTo(1), "only what happened after the re-agreement");

            c.Settings(new PlayUiSettings { Known = true, Volume = 0.8f, MoveSpeed = 1.5f, TurnDegrees = 90f, TurnIsContinuous = true, BladeAngleDegrees = 30f });
            Assert.That(s.Settings.Known, Is.True);
            Assert.That(s.Settings.BladeAngleDegrees, Is.EqualTo(30f));

            c.Withdraw();
            Assert.That(s.AcceptedCuts, Is.Zero);
            Assert.That(s.Settings.Known, Is.False);
            c.Agree();
            c.Total(PlayTotalKind.AcceptedCuts, 2, 40);
            c.Total(PlayTotalKind.AcceptedCuts, 2, 41);
            Assert.That(s.AcceptedCuts, Is.EqualTo(1), "a re-agreement takes a new base");
        }

        // The refresh rates a Quest 3S reported within a minute of one play under frame synthesis (run qStats2).
        private static readonly float[] ReportedHz = { 45f, 45.00002f, 44.99998f, 44.99996f, 45.00004f };

        [Test]
        public void TheRefreshRatesJitter_IsNoChangeOfConditions_TheEpochStaysOne_AndItsFrameTimesAreAttributed()
        {
            Assert.That(PlayEnvironment.RefreshRateHz(45.00002f), Is.EqualTo(45f));
            Assert.That(PlayEnvironment.RefreshRateHz(44.99996f), Is.EqualTo(45f));
            Assert.That(PlayEnvironment.RefreshRateHz(90.00008f), Is.EqualTo(90f));
            Assert.That(PlayEnvironment.RefreshRateHz(72f), Is.EqualTo(72f));
            Assert.That(PlayEnvironment.RefreshRateHz(0f), Is.EqualTo(0f), "no rate");
            Assert.That(PlayEnvironment.RefreshRateHz(-1f), Is.EqualTo(0f));
            Assert.That(PlayEnvironment.RefreshRateHz(float.NaN), Is.EqualTo(0f));
            Assert.That(PlayEnvironment.RefreshRateHz(float.PositiveInfinity), Is.EqualTo(0f));
            Assert.That(PlayEnvironment.RefreshRateHz(90f), Is.Not.EqualTo(PlayEnvironment.RefreshRateHz(45f)), "a rate and its half are two conditions");

            // As the service reads it: the reported rate made whole hertz before it is a condition.
            var statistics = new PlayStatistics();
            var collector = new PlayStatisticsCollector(statistics);
            collector.Agree();
            const int frames = 200;
            for (int i = 0; i < frames; i++)
            {
                PlayConditions c = Conditions("QuestCityWalk");
                c.RefreshRateHz = PlayEnvironment.RefreshRateHz(ReportedHz[i % ReportedHz.Length]);
                collector.Frame(true, false, true, in c, 22.2f, Full(), 1);
            }

            Assert.That(statistics.EpochCount, Is.EqualTo(1));
            Assert.That(statistics.EpochChangesDropped, Is.Zero);
            Assert.That(statistics.FramesOutsideEpochs, Is.Zero);
            PlayEpoch epoch = statistics.EpochAt(0);
            Assert.That(epoch.Frames, Is.EqualTo((ulong)(frames - 2)), "all but the first frame after the agreement and the frame the conditions were first known in");
            Assert.That(epoch.Unattributed, Is.EqualTo((ulong)PlayStatisticsCollector.AttributionLagFrames), "the lag once, after the one boundary");
            Assert.That(epoch.Main.Samples, Is.EqualTo((ulong)(frames - 2 - PlayStatisticsCollector.AttributionLagFrames)));
        }

        [Test]
        public void ConditionsComparedByTheReportedRate_AsTheyWere_ChangeEveryFrame_HardlyAFrameIsCounted()
        {
            // What the Quest run showed, kept as the contrast: without the whole hertz the same frames give four epochs,
            // changes beyond them, and not one frame time attributed.
            var statistics = new PlayStatistics();
            var collector = new PlayStatisticsCollector(statistics);
            collector.Agree();
            for (int i = 0; i < 200; i++)
            {
                PlayConditions c = Conditions("QuestCityWalk");
                c.RefreshRateHz = ReportedHz[i % ReportedHz.Length];
                collector.Frame(true, false, true, in c, 22.2f, Full(), 1);
            }

            Assert.That(statistics.EpochCount, Is.EqualTo(PlayStatistics.MaxEpochs));
            Assert.That(statistics.EpochChangesDropped, Is.GreaterThan(0));
            ulong counted = 0, attributed = 0;
            for (int i = 0; i < statistics.EpochCount; i++)
            {
                counted += statistics.EpochAt(i).Frames;
                attributed += statistics.EpochAt(i).Main.Samples;
            }

            Assert.That(counted, Is.Zero, "every frame is the frame of a change: of neither epoch");
            Assert.That(attributed, Is.Zero);
        }
    }
}
