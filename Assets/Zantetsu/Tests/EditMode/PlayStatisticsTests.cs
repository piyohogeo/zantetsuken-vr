using System;
using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The lightweight play statistics (DESIGN 21.18): the histogram's buckets, overflow and sum; the epochs by
    /// conditions, bounded, with frames outside them counted; the missing values counted apart from the taken ones; the
    /// maxima and totals kept where they rose; the JSON body's shape and size; the consent; and the sender's one send at
    /// a time, its pending one, the same bytes for the same content, nothing resent once accepted, nothing sent without
    /// an agreement or without play, the failure left for the next focus loss, and the withdrawal's discard and abort.
    /// </summary>
    public sealed class PlayStatisticsTests
    {
        // ---- the histogram --------------------------------------------------------------------------------------

        [Test]
        public void TheHistogram_PutsEachSampleInOneBucket_TheLongOnesInTheOverflow_AndSumsToItsSamples()
        {
            var h = new PlayTimeHistogram();
            Assert.That(PlayTimeHistogram.BucketCount, Is.EqualTo(30));
            Assert.That(h.Add(0f), Is.True);
            Assert.That(h.Add(0.99f), Is.True);
            Assert.That(h[0], Is.EqualTo(2), "under 1 ms: the first bucket");
            Assert.That(h.Add(1f), Is.True);
            Assert.That(h[1], Is.EqualTo(1), "1 ms is the second bucket (edges are upper, exclusive)");
            Assert.That(h.Add(16.5f), Is.True);
            Assert.That(h[16], Is.EqualTo(1), "16.5 falls under the 18 ms edge");
            Assert.That(h.Add(99.9f), Is.True);
            Assert.That(h[29], Is.EqualTo(1));
            Assert.That(h.Add(100f), Is.True);
            Assert.That(h.Add(5000f), Is.True);
            Assert.That(h.Overflow, Is.EqualTo(2), "at and past the last edge: the overflow, never dropped");
            Assert.That(h.MaxMs, Is.EqualTo(5000f));
            Assert.That(h.Samples, Is.EqualTo(7));
            Assert.That(h.Sum(), Is.EqualTo(h.Samples));

            Assert.That(h.Add(float.NaN), Is.False, "not a number: refused");
            Assert.That(h.Add(-1f), Is.False, "negative: refused");
            Assert.That(h.Add(float.PositiveInfinity), Is.False);
            Assert.That(h.Samples, Is.EqualTo(7), "a refused sample is not counted here");
            h.Clear();
            Assert.That(h.Samples + h.Overflow + h[0], Is.Zero);
            Assert.That(h.MaxMs, Is.Zero);
        }

        [Test]
        public void TheHistogram_BucketOf_FollowsTheEdges()
        {
            for (int i = 0; i < PlayTimeHistogram.BucketCount; i++)
            {
                float edge = PlayTimeHistogram.UpperEdgesMs[i];
                Assert.That(PlayTimeHistogram.BucketOf(edge - 0.001f), Is.EqualTo(i), "just under edge " + i);
                Assert.That(PlayTimeHistogram.BucketOf(edge), Is.EqualTo(i + 1 < PlayTimeHistogram.BucketCount ? i + 1 : -1), "at edge " + i);
                if (i > 0) Assert.That(edge, Is.GreaterThan(PlayTimeHistogram.UpperEdgesMs[i - 1]), "edges rise");
            }
        }

        // ---- the statistics -------------------------------------------------------------------------------------

        private static PlayConditions Conditions(string scene, float scale = 1f, bool debug = false)
        {
            return new PlayConditions { Scene = scene, Mode = "play", DrawRoute = "VP3C-NATIVE", XrRuntime = "test", RefreshRateHz = 72f, RenderScale = scale, EyeTextureWidth = 1680, EyeTextureHeight = 1760, MsaaSamples = 4, CutSurfaceDebug = debug };
        }

        private static FrameTimeSample Sample(float main, float render, float gpu, bool hasGpu = true)
        {
            return new FrameTimeSample { HasMain = true, MainMs = main, HasRender = true, RenderMs = render, HasGpu = hasGpu, GpuMs = gpu };
        }

        [Test]
        public void Frames_GoToTheEpochOfTheirConditions_AndTheMissingValuesAreCountedApart()
        {
            var s = new PlayStatistics();
            Assert.That(s.HasPlay, Is.False);
            s.BeginEpoch(Conditions("WalkCity"));
            s.NoteFrame(13.9f, Sample(8.2f, 3.1f, 6.0f), structureChanged: false);
            s.NoteFrame(13.8f, Sample(9.0f, 3.0f, 0f, hasGpu: false), structureChanged: true);
            s.NoteFrame(14.1f, new FrameTimeSample(), structureChanged: true);   // no result this frame: nothing taken, everything missing
            PlayEpoch e = s.Current;
            Assert.That(s.EpochCount, Is.EqualTo(1));
            Assert.That(e.Frames, Is.EqualTo(3));
            Assert.That(e.StructureChangedFrames, Is.EqualTo(2), "a frame with a change counts once");
            Assert.That(e.Main.Samples, Is.EqualTo(2));
            Assert.That(e.MainMissing, Is.EqualTo(1));
            Assert.That(e.Gpu.Samples, Is.EqualTo(1), "an absent GPU time is not 0 ms");
            Assert.That(e.GpuMissing, Is.EqualTo(2));
            Assert.That(e.GpuXrMissing, Is.EqualTo(3));
            Assert.That(e.Interval.Samples, Is.EqualTo(3));
            Assert.That(e.PlaySeconds, Is.EqualTo(0.0418).Within(1e-6));
            Assert.That(e.Main.Samples + e.MainMissing, Is.EqualTo(e.Frames), "taken and missing make the frames");
            Assert.That(s.HasPlay, Is.True);
        }

        [Test]
        public void EpochsAreBounded_AChangeBeyondThemCountsItsFramesOutside_AndAReturnToSeenConditionsGoesOn()
        {
            var s = new PlayStatistics();
            Assert.That(PlayStatistics.MaxEpochs, Is.EqualTo(4));
            for (int i = 0; i < PlayStatistics.MaxEpochs; i++)
            {
                s.BeginEpoch(Conditions("scene" + i));
                s.NoteFrame(14f, Sample(8f, 3f, 6f), false);
            }

            Assert.That(s.EpochCount, Is.EqualTo(4));
            s.BeginEpoch(Conditions("scene-too-many"));
            Assert.That(s.Current, Is.Null);
            Assert.That(s.EpochChangesDropped, Is.EqualTo(1));
            s.NoteFrame(14f, Sample(8f, 3f, 6f), false);
            s.NoteFrame(14f, Sample(8f, 3f, 6f), false);
            Assert.That(s.FramesOutsideEpochs, Is.EqualTo(2), "counted, not put under any conditions");
            Assert.That(s.EpochCount, Is.EqualTo(4), "no epoch was dropped for it");

            s.BeginEpoch(Conditions("scene1"));
            Assert.That(s.Current, Is.SameAs(s.EpochAt(1)), "conditions seen before: their epoch again");
            s.NoteFrame(14f, Sample(8f, 3f, 6f), false);
            Assert.That(s.EpochAt(1).Frames, Is.EqualTo(2));

            // The same conditions again: no new epoch, no change counted.
            long changes = s.ChangeCount;
            s.BeginEpoch(Conditions("scene1"));
            Assert.That(s.ChangeCount, Is.EqualTo(changes));
            // A rendering setting is a condition: the debug colour on is another epoch.
            s.BeginEpoch(Conditions("scene1", debug: true));
            Assert.That(s.Current, Is.Null, "a fifth set of conditions: no room");
            Assert.That(s.EpochChangesDropped, Is.EqualTo(2));
        }

        [Test]
        public void MaximaAndTotals_AreKeptWhereTheyRose_AndCapacitiesKeepUseAndCapacityApart()
        {
            var s = new PlayStatistics();
            long c0 = s.ChangeCount;
            s.NoteLiveFragments(10);
            s.NoteLiveFragments(7);
            s.NoteLiveFragments(12);
            Assert.That(s.MaxLiveFragments, Is.EqualTo(12));
            s.NoteTotals(3, 20);
            s.NoteTotals(3, 20);
            Assert.That(s.AcceptedCuts, Is.EqualTo(3));
            Assert.That(s.CollectedFragments, Is.EqualTo(20));
            Assert.That(s.Capacity(PlayCapacityKind.CpuVertices).Read, Is.False, "no reading yet: no headroom");
            s.NoteCapacity(PlayCapacityKind.CpuVertices, end: 1000, capacity: 4096, growths: 0, shortfalls: 0);
            s.NoteCapacity(PlayCapacityKind.CpuVertices, end: 500, capacity: 8192, growths: 1, shortfalls: 0);
            PlayCapacity v = s.Capacity(PlayCapacityKind.CpuVertices);
            Assert.That(v.MaxEnd, Is.EqualTo(1000), "the highest end in use (holes included)");
            Assert.That(v.Capacity, Is.EqualTo(8192), "the latest capacity");
            Assert.That(v.Limit, Is.Zero, "no limit given");
            Assert.That(v.MinHeadroomToCapacity, Is.EqualTo(3096), "the least headroom to the capacity: at the first reading, 4096 - 1000, not the latest 8192 - 500");
            Assert.That(v.Growths, Is.EqualTo(1));
            // A buffer that grows toward a fixed limit: both headrooms, apart.
            s.NoteCapacity(PlayCapacityKind.DrawCommandSlots, end: 900, capacity: 1024, limit: 4096, growths: 0, shortfalls: 0);
            s.NoteCapacity(PlayCapacityKind.DrawCommandSlots, end: 1500, capacity: 2048, limit: 4096, growths: 1, shortfalls: 0);
            PlayCapacity d = s.Capacity(PlayCapacityKind.DrawCommandSlots);
            Assert.That(d.Limit, Is.EqualTo(4096));
            Assert.That(d.MinHeadroomToCapacity, Is.EqualTo(124), "the room before the next growth, at its least (1024 - 900)");
            Assert.That(d.MinHeadroomToLimit, Is.EqualTo(2596), "the room before a refusal, at its least (4096 - 1500)");
            Assert.That(PlayStatistics.CapacityUnit(PlayCapacityKind.CpuVertices), Is.EqualTo("vertices"));
            s.NoteFailures(PlayFailureKind.RingFull, 2);
            s.NoteFailures(PlayFailureKind.RingFull, 1);
            Assert.That(s.Failures(PlayFailureKind.RingFull), Is.EqualTo(2));
            Assert.That(s.ChangeCount, Is.GreaterThan(c0));
            long c1 = s.ChangeCount;
            s.NoteLiveFragments(12);
            s.NoteTotals(3, 20);
            s.NoteFailures(PlayFailureKind.RingFull, 2);
            s.NoteCapacity(PlayCapacityKind.CpuVertices, 500, 8192, 1, 0);
            Assert.That(s.ChangeCount, Is.EqualTo(c1), "nothing new: no change counted");
            s.Clear();
            Assert.That(s.MaxLiveFragments + s.AcceptedCuts + s.Failures(PlayFailureKind.RingFull) + s.Capacity(PlayCapacityKind.CpuVertices).MaxEnd, Is.Zero);
            Assert.That(s.Capacity(PlayCapacityKind.CpuVertices).Read, Is.False);
            Assert.That(s.HasPlay, Is.False);
        }

        // ---- the JSON -------------------------------------------------------------------------------------------

        private static PlayStatistics Full()
        {
            var s = new PlayStatistics { BuildId = "build-1", Platform = "WindowsPlayer", OperatingSystem = "Windows 11", Cpu = "cpu", Gpu = "gpu \"x\"", GraphicsApi = "Direct3D 12 [level 12.2]", GpuDriver = "32.0.15.9159", XrDevice = null, XrRuntime = "Oculus", XrRuntimeVersion = "1.115.0", TimeSource = "FrameTimingManager+XRDisplaySubsystem" };
            var random = new Random(7);
            for (int e = 0; e < PlayStatistics.MaxEpochs; e++)
            {
                s.BeginEpoch(Conditions("scene" + e, 1f + e * 0.1f));
                for (int i = 0; i < 100000; i++)
                {
                    s.NoteFrame(13f + (float)random.NextDouble() * 200f, Sample(5f + (float)random.NextDouble() * 120f, 2f + (float)random.NextDouble() * 50f, 4f + (float)random.NextDouble() * 150f), i % 7 == 0);
                }
            }

            s.NoteLiveFragments(123456);
            s.NoteTotals(98765, 87654);
            for (int k = 0; k < (int)PlayCapacityKind.Count; k++) s.NoteCapacity((PlayCapacityKind)k, 123456789, 987654321, 1000000000, 12, 34);
            for (int k = 0; k < (int)PlayFailureKind.Count; k++) s.NoteFailures((PlayFailureKind)k, 1000000 + k);
            s.NoteSettings(new PlayUiSettings { Known = true, Volume = 0.8f, MoveSpeed = 1.5f, TurnDegrees = 90f, TurnIsContinuous = true, BladeAngleDegrees = 30f });
            return s;
        }

        [Test]
        public void TheJson_CarriesTheEnvelopeAndEveryPart_AndStaysWellUnderTheReceiversLimit_WhenFull()
        {
            PlayStatistics s = Full();
            var b = new StringBuilder();
            PlayStatisticsJson.Write(b, s, "11111111-2222-3333-4444-555555555555", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", 5, "external");
            string json = b.ToString();
            int bytes = Encoding.UTF8.GetByteCount(json);
            TestContext.Out.WriteLine("full snapshot: " + bytes + " bytes");
            Assert.That(bytes, Is.LessThan(PlayStatisticsSender.MaxBodyBytes * 3 / 4), "well under 16 KiB with four epochs full");
            Assert.That(json, Does.StartWith("{\"schema_version\":1,\"session_id\":\"11111111-2222-3333-4444-555555555555\",\"install_id\":\"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee\",\"revision\":5,\"tester_role\":\"external\",\"stats\":{"));
            Assert.That(PlayStatistics.FormatVersion, Is.EqualTo(2), "the counted frames and the environment fields changed their meaning: a new version");
            Assert.That(json, Does.Contain("\"format\":2").And.Contain("\"histogram\":1").And.Contain("\"mainIncludesPresentWait\":true"));
            // The environment: the graphics API apart from the driver's version; the runtime's own name and version; a device not obtained is null.
            Assert.That(json, Does.Contain("\"graphicsApi\":\"Direct3D 12 [level 12.2]\",\"gpuDriver\":\"32.0.15.9159\",\"xrDevice\":null,\"xrRuntime\":\"Oculus\",\"xrRuntimeVersion\":\"1.115.0\""));
            Assert.That(json, Does.Contain("\"gpu\":\"gpu \\\"x\\\"\""), "a quote in a string is escaped");
            Assert.That(json, Does.Contain("\"cpuVertices\":{\"unit\":\"vertices\",\"capacity\":987654321,\"limit\":1000000000,\"maxEnd\":123456789,\"minHeadroomToCapacity\":864197532,\"minHeadroomToLimit\":876543211,\"growths\":12,\"shortfalls\":34}"));
            Assert.That(json, Does.Contain("\"ringFull\":1000001"));
            Assert.That(json, Does.Contain("\"maxLiveFragments\":123456"));
            Assert.That(json, Does.Contain("\"gpuVertices\":{\"unit\":\"vertices\"").And.Contain("\"renderFragments\":{\"unit\":\"fragments\""));
            Assert.That(json, Does.Contain("\"settings\":{\"volume\":0.8,\"moveSpeed\":1.5,\"turn\":90,\"turnContinuous\":true,\"bladeAngle\":30}"));
            Assert.That(json, Does.Contain("\"unattributed\":0"));
            Assert.That(CountOf(json, "\"conditions\":"), Is.EqualTo(4));
            Assert.That(CountOf(json, "\"counts\":["), Is.EqualTo(20), "five histograms in each of four epochs");
            Assert.That(Balanced(json), Is.True, "brackets balance");

            // The counts written are the histogram's, overflow included: the sum of one epoch's main counts is its samples.
            PlayEpoch e0 = s.EpochAt(0);
            Assert.That(e0.Main.Sum(), Is.EqualTo(e0.Main.Samples));
            Assert.That(e0.Main.Overflow, Is.GreaterThan(0), "the layout: samples past 100 ms went to the overflow");
            Assert.That(json, Does.Contain("\"overflow\":" + e0.Main.Overflow));
        }

        [Test]
        public void TheJson_OfAnEmptyRecord_IsSmall_AndWritesNoEpoch()
        {
            var b = new StringBuilder();
            PlayStatisticsJson.Write(b, new PlayStatistics(), "s", "i", 0, "developer");
            string json = b.ToString();
            TestContext.Out.WriteLine("empty snapshot: " + Encoding.UTF8.GetByteCount(json) + " bytes");
            Assert.That(json, Does.Contain("\"epochs\":[]").And.Contain("\"settings\":null").And.Contain("\"limit\":null,\"maxEnd\":0,\"minHeadroomToCapacity\":null,\"minHeadroomToLimit\":null").And.Contain("\"gpuDriver\":null,\"xrDevice\":null").And.Contain("\"xrRuntimeVersion\":null").And.Contain("\"attributionLagFrames\":" + PlayStatisticsCollector.AttributionLagFrames));
            Assert.That(Balanced(json), Is.True);
            Assert.That(Encoding.UTF8.GetByteCount(json), Is.LessThan(2048), "an empty record: the envelope, the environment and seven capacities' fields");
        }

        private static int CountOf(string text, string part)
        {
            int n = 0, i = 0;
            while ((i = text.IndexOf(part, i, StringComparison.Ordinal)) >= 0) { n++; i += part.Length; }
            return n;
        }

        private static bool Balanced(string json)
        {
            int braces = 0, brackets = 0;
            bool inString = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inString) { if (c == '\\') i++; else if (c == '"') inString = false; continue; }
                if (c == '"') inString = true;
                else if (c == '{') braces++;
                else if (c == '}') braces--;
                else if (c == '[') brackets++;
                else if (c == ']') brackets--;
                if (braces < 0 || brackets < 0) return false;
            }

            return braces == 0 && brackets == 0 && !inString;
        }

        // ---- the consent ----------------------------------------------------------------------------------------

        [Test]
        public void TheConsent_StartsUndecided_KeepsTheAnswer_MakesARandomIdOnAgreement_AndForgetsItOnDecline()
        {
            var store = new MemoryPlayStatisticsStore();
            var c = new PlayStatisticsConsent(store);
            Assert.That(c.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
            Assert.That(c.Agreed, Is.False);
            Assert.That(c.InstallId, Is.Null);
            var changes = new List<PlayStatisticsDecision>();
            c.Changed += changes.Add;
            c.Agree();
            Assert.That(c.Agreed, Is.True);
            Assert.That(Guid.TryParse(c.InstallId, out _), Is.True, "a random GUID, not a device id");
            Assert.That(store.Saves, Is.EqualTo(1));
            string first = c.InstallId;

            var again = new PlayStatisticsConsent(store);
            Assert.That(again.Decision, Is.EqualTo(PlayStatisticsDecision.Agreed), "kept: not asked again");
            Assert.That(again.InstallId, Is.EqualTo(first), "the same install id");

            c.Decline();
            Assert.That(c.Agreed, Is.False);
            Assert.That(c.InstallId, Is.Null, "forgotten");
            Assert.That(new PlayStatisticsConsent(store).Decision, Is.EqualTo(PlayStatisticsDecision.Declined));
            Assert.That(store.TryGet(PlayStatisticsConsent.InstallIdKey, out _), Is.False);
            c.Agree();
            Assert.That(c.InstallId, Is.Not.EqualTo(first), "a new agreement, a new id");
            Assert.That(changes, Is.EqualTo(new[] { PlayStatisticsDecision.Agreed, PlayStatisticsDecision.Declined, PlayStatisticsDecision.Agreed }));
        }

        // ---- the sender -----------------------------------------------------------------------------------------

        private sealed class FakeTransport : IPlayStatisticsTransport
        {
            public sealed class Request : IPlayStatisticsRequest
            {
                public byte[] Body;
                public Action<PlayStatisticsSendResult> Done;
                public bool AbortAsked;
                public void Abort() => AbortAsked = true;
                public void Succeed() => Done(new PlayStatisticsSendResult { Success = true, StatusCode = 201 });
                public void Fail(long status = 0, bool timedOut = false) => Done(new PlayStatisticsSendResult { Success = false, StatusCode = status, TimedOut = timedOut, Error = timedOut ? "timeout" : "error" });
            }

            public readonly List<Request> Requests = new List<Request>();
            public Request Last => Requests[Requests.Count - 1];

            public IPlayStatisticsRequest Send(byte[] body, Action<PlayStatisticsSendResult> done)
            {
                var r = new Request { Body = body, Done = done };
                Requests.Add(r);
                return r;
            }
        }

        private static (PlayStatistics stats, PlayStatisticsConsent consent, FakeTransport transport, PlayStatisticsSender sender) Rig(bool agreed = true)
        {
            var stats = new PlayStatistics();
            var consent = new PlayStatisticsConsent(new MemoryPlayStatisticsStore());
            if (agreed) consent.Agree();
            var transport = new FakeTransport();
            var sender = new PlayStatisticsSender(stats, consent, transport, "developer");
            return (stats, consent, transport, sender);
        }

        private static void Play(PlayStatistics s, int frames = 1)
        {
            s.BeginEpoch(Conditions("WalkCity"));
            for (int i = 0; i < frames; i++) s.NoteFrame(14f, Sample(8f, 3f, 6f), false);
        }

        [Test]
        public void NothingIsSent_WithoutAnAgreement_WithoutPlay_OrWithoutATransport()
        {
            var (stats, consent, transport, sender) = Rig(agreed: false);
            Play(stats);
            Assert.That(sender.OnFocusLost(), Does.StartWith("not agreed"));
            Assert.That(transport.Requests, Is.Empty);
            consent.Agree();
            var fresh = new PlayStatisticsSender(new PlayStatistics(), consent, transport, "developer");
            Assert.That(fresh.OnFocusLost(), Does.StartWith("nothing of play"), "an empty record is not sent: the first consent screen's focus change sends nothing");
            Assert.That(transport.Requests, Is.Empty);
            var noTransport = new PlayStatisticsSender(stats, consent, null, "developer");
            Assert.That(noTransport.OnFocusLost(), Does.StartWith("no transport"));
        }

        [Test]
        public void OneSendAtATime_TheNewestKeptPending_AndTheSameContentReusesItsRevisionAndBytes()
        {
            var (stats, _, transport, sender) = Rig();
            Play(stats);
            Assert.That(sender.OnFocusLost(), Does.StartWith("sending revision 0"));
            Assert.That(sender.Busy, Is.True);
            Assert.That(transport.Requests.Count, Is.EqualTo(1));
            byte[] first = transport.Last.Body;

            // Focus lost again while the send is under way, the statistics unchanged: the same snapshot, held pending.
            Assert.That(sender.OnFocusLost(), Does.Contain("held as the one pending"));
            Assert.That(sender.HasPending, Is.True);
            Assert.That(transport.Requests.Count, Is.EqualTo(1), "one in flight");
            // ... and changed: a new revision supersedes the pending one.
            Play(stats);
            Assert.That(sender.OnFocusLost(), Does.Contain("revision 1 held"));
            Play(stats);
            Assert.That(sender.OnFocusLost(), Does.Contain("revision 2 held"));
            Assert.That(sender.Superseded, Is.EqualTo(2), "revision 0 (pending) by 1, then 1 by 2");

            // The first ends well: the pending (2) goes, alone.
            transport.Requests[0].Succeed();
            Assert.That(sender.Succeeded, Is.EqualTo(1));
            Assert.That(transport.Requests.Count, Is.EqualTo(2));
            Assert.That(sender.Busy, Is.True);
            Assert.That(sender.HasPending, Is.False);
            Assert.That(sender.LastRevisionSent, Is.EqualTo(2));
            Assert.That(transport.Last.Body, Is.Not.EqualTo(first));
            Assert.That(Encoding.UTF8.GetString(transport.Last.Body), Does.Contain("\"revision\":2"));
            transport.Last.Succeed();
            Assert.That(sender.Busy, Is.False);

            // Unchanged, accepted: nothing more.
            Assert.That(sender.OnFocusLost(), Does.Contain("accepted: nothing sent"));
            Assert.That(sender.SkippedUnchanged, Is.EqualTo(1));
            Assert.That(transport.Requests.Count, Is.EqualTo(2));
        }

        [Test]
        public void AFailure_IsNotRetriedAtOnce_TheNextFocusLossResendsTheSameBytes_AndAChangeMakesANewRevision()
        {
            var (stats, _, transport, sender) = Rig();
            Play(stats);
            sender.OnFocusLost();
            transport.Last.Fail(status: 0);
            Assert.That(sender.Failed, Is.EqualTo(1));
            Assert.That(sender.Busy, Is.False);
            Assert.That(transport.Requests.Count, Is.EqualTo(1), "no retry by itself");
            byte[] bytes = transport.Last.Body;

            Assert.That(sender.OnFocusLost(), Does.StartWith("sending revision 0"), "the same revision again");
            Assert.That(transport.Last.Body, Is.SameAs(bytes), "the same bytes");
            transport.Last.Fail(status: 0, timedOut: true);
            Assert.That(sender.LastResult.TimedOut, Is.True);

            Play(stats);
            Assert.That(sender.OnFocusLost(), Does.StartWith("sending revision 1"), "changed: the latest cumulative values, a new revision");
            Assert.That(Encoding.UTF8.GetString(transport.Last.Body), Does.Contain("\"revision\":1").And.Contain("\"frames\":2"));
            transport.Last.Succeed();
            Assert.That(sender.OnFocusLost(), Does.Contain("accepted: nothing sent"));
        }

        [Test]
        public void AWithdrawal_DropsThePending_AbandonsTheSendUnderWay_IgnoresItsLateWord_AndSendsNothingMore()
        {
            var (stats, consent, transport, sender) = Rig();
            Play(stats);
            sender.OnFocusLost();
            Play(stats);
            sender.OnFocusLost();
            Assert.That(sender.HasPending, Is.True);
            FakeTransport.Request inFlight = transport.Requests[0];

            consent.Decline();
            sender.Withdraw();
            stats.Clear();
            Assert.That(inFlight.AbortAsked, Is.True, "the send under way was asked to stop");
            Assert.That(sender.Busy, Is.False);
            Assert.That(sender.HasPending, Is.False);
            Assert.That(sender.Aborted, Is.EqualTo(1));
            inFlight.Succeed();   // the transport's late word, after the abort
            Assert.That(sender.Succeeded, Is.Zero, "nothing of it is taken");
            Assert.That(transport.Requests.Count, Is.EqualTo(1), "the pending one did not go");

            Play(stats);
            Assert.That(sender.OnFocusLost(), Does.StartWith("not agreed"));
            Assert.That(transport.Requests.Count, Is.EqualTo(1));

            // Agreed again: a new install id, the statistics from nothing, the session the same process's.
            consent.Agree();
            Assert.That(sender.OnFocusLost(), Does.StartWith("sending revision 2"), "the revision goes on counting within the session");
            Assert.That(Encoding.UTF8.GetString(transport.Last.Body), Does.Contain("\"install_id\":\"" + consent.InstallId + "\"").And.Contain("\"session_id\":\"" + sender.SessionId + "\""));
        }

        [Test]
        public void ManyFocusChanges_MakeNoUnboundedPile()
        {
            var (stats, _, transport, sender) = Rig();
            for (int i = 0; i < 1000; i++)
            {
                Play(stats);
                sender.OnFocusLost();
            }

            Assert.That(transport.Requests.Count, Is.EqualTo(1), "one in flight, the rest superseded");
            Assert.That(sender.HasPending, Is.True);
            Assert.That(sender.Superseded, Is.EqualTo(998));
            transport.Last.Succeed();
            Assert.That(transport.Requests.Count, Is.EqualTo(2), "then the one pending");
            Assert.That(sender.LastRevisionSent, Is.EqualTo(999));
        }

        [Test]
        public void TheSessionId_IsAGuid_AndTheTesterRoleDefaultsToExternal()
        {
            var sender = new PlayStatisticsSender(new PlayStatistics(), new PlayStatisticsConsent(new MemoryPlayStatisticsStore()), null, null);
            Assert.That(Guid.TryParse(sender.SessionId, out _), Is.True);
            Assert.That(sender.TesterRole, Is.EqualTo("external"));
        }
    }
}
