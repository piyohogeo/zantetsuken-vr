using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The quit of a PC Player and the last statistics (DESIGN 21.18): no wait where there is nothing to wait for (no
    /// agreement, no transport, nothing of play, the latest accepted already); the wait for an accepted, a failed and a
    /// late send, never past two seconds from the request; a quit while a send is under way (the last one pending, both
    /// inside the one deadline); a quit right after a loss of focus (one send, one revision); requests repeated while
    /// waiting and after the end; and the wait being no part of the statistics.
    /// </summary>
    public sealed class PlayStatisticsQuitTests
    {
        private sealed class FakeTransport : IPlayStatisticsTransport
        {
            public sealed class Request : IPlayStatisticsRequest
            {
                public byte[] Body;
                public Action<PlayStatisticsSendResult> Done;
                public bool AbortAsked;
                public void Abort() => AbortAsked = true;
                public void Succeed() => Done(new PlayStatisticsSendResult { Success = true, StatusCode = 201 });
                public void Fail() => Done(new PlayStatisticsSendResult { Success = false, StatusCode = 0, Error = "error" });
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

        private static PlayConditions Conditions() => new PlayConditions { Scene = "WalkCity", Mode = "play", DrawRoute = "VP3C-NATIVE", XrRuntime = "test", RefreshRateHz = 72f };

        private sealed class Rig
        {
            public PlayStatistics Stats = new PlayStatistics();
            public PlayStatisticsCollector Collector;
            public PlayStatisticsConsent Consent = new PlayStatisticsConsent(new MemoryPlayStatisticsStore());
            public FakeTransport Transport = new FakeTransport();
            public PlayStatisticsSender Sender;
            public PlayStatisticsQuit Quit;

            public Rig(bool agreed = true, bool transport = true)
            {
                Collector = new PlayStatisticsCollector(Stats);
                if (agreed) { Consent.Agree(); Collector.Agree(); }
                Sender = new PlayStatisticsSender(Stats, Consent, transport ? Transport : null, "developer");
                Quit = new PlayStatisticsQuit(Sender);
            }

            public void Play(int frames = 12)
            {
                for (int i = 0; i < frames; i++) Collector.Frame(true, false, true, Conditions(), 13.9f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
            }

            // What the service does at the first request: the collection stops, then the quit asks.
            public bool Ask(double now)
            {
                if (!Quit.Requested) Collector.Stop();
                return Quit.Request(now);
            }
        }

        [Test]
        public void NothingToWaitFor_NoAgreement_NoTransport_NoPlay_OrTheLatestAccepted_QuitsAtOnce()
        {
            var notAgreed = new Rig(agreed: false);
            notAgreed.Play();
            Assert.That(notAgreed.Ask(10.0), Is.True);
            Assert.That(notAgreed.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.NotAgreed));
            Assert.That(notAgreed.Transport.Requests, Is.Empty);

            var noTransport = new Rig(transport: false);
            noTransport.Play();
            Assert.That(noTransport.Ask(10.0), Is.True);
            Assert.That(noTransport.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.NoTransport));
            Assert.That(noTransport.Stats.HasPlay, Is.True, "the agreed statistics stand, to be read locally, though nothing is sent");

            var noPlay = new Rig();
            Assert.That(noPlay.Ask(10.0), Is.True);
            Assert.That(noPlay.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.NoPlay));
            Assert.That(noPlay.Transport.Requests, Is.Empty);

            // The latest content accepted at a loss of focus, nothing changed since: no send, no wait.
            var accepted = new Rig();
            accepted.Play();
            accepted.Collector.Stop();   // (nothing more is counted between the focus loss and the quit)
            accepted.Sender.OnFocusLost();
            accepted.Transport.Last.Succeed();
            Assert.That(accepted.Ask(10.0), Is.True);
            Assert.That(accepted.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.AlreadyAccepted));
            Assert.That(accepted.Transport.Requests.Count, Is.EqualTo(1), "nothing sent again");
            Assert.That(accepted.Quit.WaitedSeconds, Is.Zero);
        }

        [Test]
        public void TheLastSend_Accepted_Failed_OrOutOfTime_EndsTheWait_NeverPastTwoSeconds()
        {
            Assert.That(PlayStatisticsQuit.WaitSeconds, Is.EqualTo(2.0));

            var ok = new Rig();
            ok.Play();
            Assert.That(ok.Ask(100.0), Is.False, "a send began: the quit is held");
            Assert.That(ok.Quit.Waiting, Is.True);
            Assert.That(ok.Transport.Requests.Count, Is.EqualTo(1));
            Assert.That(ok.Quit.Tick(100.5), Is.False);
            ok.Transport.Last.Succeed();
            Assert.That(ok.Quit.Tick(100.6), Is.True);
            Assert.That(ok.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.Accepted));
            Assert.That(ok.Quit.WaitedSeconds, Is.EqualTo(0.6).Within(1e-9));
            Assert.That(ok.Quit.Finished, Is.True);

            var failed = new Rig();
            failed.Play();
            Assert.That(failed.Ask(100.0), Is.False);
            failed.Transport.Last.Fail();
            Assert.That(failed.Quit.Tick(100.2), Is.True, "a failure ends the wait at once");
            Assert.That(failed.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.Failed));
            Assert.That(failed.Transport.Requests.Count, Is.EqualTo(1), "not tried again");
            Assert.That(failed.Sender.Busy, Is.False);

            var late = new Rig();
            late.Play();
            Assert.That(late.Ask(100.0), Is.False);
            Assert.That(late.Quit.Tick(101.99), Is.False, "still inside the two seconds");
            Assert.That(late.Quit.Tick(102.0), Is.True, "the time is up");
            Assert.That(late.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.TimedOut));
            Assert.That(late.Transport.Last.AbortAsked, Is.True, "the send under way is abandoned");
            Assert.That(late.Sender.Busy, Is.False);
            late.Transport.Last.Succeed();   // its late word
            Assert.That(late.Sender.Succeeded, Is.Zero, "the late word of an abandoned send is not taken");
            Assert.That(late.Transport.Requests.Count, Is.EqualTo(1), "and nothing is sent after the time is up");
        }

        [Test]
        public void AQuitWhileASendIsUnderWay_HoldsTheLastOnePending_BothInsideTheOneDeadline()
        {
            // A loss of focus sent revision 0; more was played; the quit comes while that send is still under way.
            var r = new Rig();
            r.Play();
            r.Sender.OnFocusLost();
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(1));
            r.Play(5);
            Assert.That(r.Ask(50.0), Is.False);
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(1), "no second send beside the one under way");
            Assert.That(r.Sender.HasPending, Is.True, "the last statistics wait for it");
            Assert.That(r.Quit.FinalRevision, Is.EqualTo(1));

            // The earlier one is accepted at 1.2 s: the last one begins; accepted at 1.7 s: the quit goes on.
            r.Transport.Requests[0].Succeed();
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(2), "the pending one, now");
            Assert.That(r.Quit.Tick(51.2), Is.False);
            r.Transport.Last.Succeed();
            Assert.That(r.Quit.Tick(51.7), Is.True);
            Assert.That(r.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.Accepted));

            // The same, the earlier one slow: the deadline is counted from the request and not lengthened by the second send.
            var slow = new Rig();
            slow.Play();
            slow.Sender.OnFocusLost();
            slow.Play(5);
            Assert.That(slow.Ask(50.0), Is.False);
            Assert.That(slow.Quit.Tick(51.8), Is.False);
            slow.Transport.Requests[0].Succeed();   // at 1.8 s: the last one begins with 0.2 s left
            Assert.That(slow.Transport.Requests.Count, Is.EqualTo(2));
            Assert.That(slow.Quit.Tick(51.9), Is.False);
            Assert.That(slow.Quit.Tick(52.0), Is.True, "two seconds from the request, whatever began in between");
            Assert.That(slow.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.TimedOut));
            Assert.That(slow.Transport.Last.AbortAsked, Is.True);

            // The earlier one fails: the last one is another snapshot, not a retry -- it is sent, inside the deadline.
            var failedFirst = new Rig();
            failedFirst.Play();
            failedFirst.Sender.OnFocusLost();
            failedFirst.Play(5);
            Assert.That(failedFirst.Ask(50.0), Is.False);
            failedFirst.Transport.Requests[0].Fail();
            Assert.That(failedFirst.Transport.Requests.Count, Is.EqualTo(2));
            Assert.That(failedFirst.Transport.Last.Body, Is.Not.SameAs(failedFirst.Transport.Requests[0].Body));
            failedFirst.Transport.Last.Fail();
            Assert.That(failedFirst.Quit.Tick(50.4), Is.True);
            Assert.That(failedFirst.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.Failed));
            Assert.That(failedFirst.Transport.Requests.Count, Is.EqualTo(2), "no retry loop");
        }

        [Test]
        public void AQuitRightAfterALossOfFocus_SendsNothingTwice_TheSameRevisionIsWaitedFor()
        {
            var r = new Rig();
            r.Play();
            r.Collector.Stop();   // (the focus is lost and the window closed with nothing counted in between)
            Assert.That(r.Sender.OnFocusLost(), Does.StartWith("sending revision 0"));
            byte[] body = r.Transport.Last.Body;
            Assert.That(r.Ask(20.0), Is.False, "that very send is waited for");
            Assert.That(r.Quit.FinalRevision, Is.EqualTo(0), "the same session and revision: the content did not change");
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(1), "one send for one content");
            r.Transport.Last.Succeed();
            Assert.That(r.Quit.Tick(20.3), Is.True);
            Assert.That(r.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.Accepted));
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(1), "its end begins nothing again");
            Assert.That(r.Transport.Requests[0].Body, Is.SameAs(body));

            // The same with that send failing: the quit is not a retry of it.
            var f = new Rig();
            f.Play();
            f.Collector.Stop();
            f.Sender.OnFocusLost();
            Assert.That(f.Ask(20.0), Is.False);
            f.Transport.Last.Fail();
            Assert.That(f.Quit.Tick(20.1), Is.True);
            Assert.That(f.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.Failed));
            Assert.That(f.Transport.Requests.Count, Is.EqualTo(1));
        }

        [Test]
        public void RequestsRepeated_WhileWaitingStartNothing_AndAfterTheEndPass()
        {
            var r = new Rig();
            r.Play();
            Assert.That(r.Ask(5.0), Is.False);
            Assert.That(r.Ask(5.1), Is.False, "asked again while held: still held");
            Assert.That(r.Ask(5.2), Is.False);
            Assert.That(r.Quit.RepeatedRequests, Is.EqualTo(2));
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(1), "no second send, no second wait");
            Assert.That(r.Quit.Tick(6.9), Is.False, "the deadline is the first request's: 7.0");
            Assert.That(r.Quit.Tick(7.0), Is.True);
            Assert.That(r.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.TimedOut));

            // The quit the caller now makes comes as a request again: it passes, and nothing is begun by it.
            Assert.That(r.Ask(7.01), Is.True);
            Assert.That(r.Ask(7.02), Is.True);
            Assert.That(r.Quit.Tick(7.03), Is.True);
            Assert.That(r.Transport.Requests.Count, Is.EqualTo(1));
            Assert.That(r.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.TimedOut), "the outcome stands");
        }

        [Test]
        public void TheWait_IsNoPartOfTheStatistics_NothingIsCountedAfterTheRequest()
        {
            var r = new Rig();
            r.Play();
            Assert.That(r.Ask(0.0), Is.False);
            long changes = r.Stats.ChangeCount;
            ulong frames = r.Stats.Current.Frames;
            double seconds = r.Stats.Current.PlaySeconds;

            // Frames go on while the quit is held (the game still runs): none is counted, no counter is taken.
            for (int i = 0; i < 100; i++)
            {
                Assert.That(r.Collector.Frame(true, false, true, Conditions(), 13.9f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 2 + i), Is.False);
                r.Collector.Live(1000 + i);
                r.Collector.Total(PlayTotalKind.AcceptedCuts, 1, 500 + i);
                r.Collector.Failure(PlayFailureKind.RingFull, 1, 50 + i);
                r.Collector.Capacity(PlayCapacityKind.CpuVertices, 1, 9999, 10000, 20000, 9, 9);
            }

            Assert.That(r.Collector.Stopped, Is.True);
            Assert.That(r.Stats.ChangeCount, Is.EqualTo(changes), "the statistics are final at the request");
            Assert.That(r.Stats.Current.Frames, Is.EqualTo(frames));
            Assert.That(r.Stats.Current.PlaySeconds, Is.EqualTo(seconds));
            Assert.That(r.Stats.MaxLiveFragments, Is.Zero);
            Assert.That(r.Stats.AcceptedCuts, Is.Zero);
        }

        [Test]
        public void TheService_AtTheQuit_StopsTheCollection_WritesItsRecord_AndAllowsTheQuitWhereNothingIsSent()
        {
            var host = new GameObject("statistics under test");
            try
            {
                PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
                service.Setup(SandboxScenarioKind.Play, "TestScene", new MemoryPlayStatisticsStore(), null);
                service.Agree();
                var conditions = new PlayConditions { Scene = "TestScene", Mode = "play" };
                for (int i = 0; i < 6; i++) service.Collector.Frame(true, false, true, conditions, 14f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(4));

                // No send configuration: the record is written to the log, and the quit goes on at once.
                UnityEngine.TestTools.LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("^PLAY STATISTICS: record at the quit: counted: epochs 1 .*frames 4"));
                UnityEngine.TestTools.LogAssert.Expect(LogType.Log, new System.Text.RegularExpressions.Regex("^PLAY STATISTICS: quit asked: NoTransport"));
                Assert.That(service.RequestQuit(1.0), Is.True);
                Assert.That(service.Collector.Stopped, Is.True);
                Assert.That(service.Quit.Outcome, Is.EqualTo(PlayQuitOutcome.NoTransport));
                Assert.That(service.TickQuit(1.1), Is.False, "nothing was held: nothing ends later");
                Assert.That(service.RequestQuit(1.2), Is.True, "and asked again it passes");
                Assert.That(service.Collector.Frame(true, false, true, conditions, 14f, new FrameTimeSample(), 1), Is.False);
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(4));
                Assert.That(PlayStatisticsService.ConsentBodyFor(true), Does.Contain("loses focus").And.Contain("closed normally"), "a desktop Player sends at its ordinary quit too");
                Assert.That(PlayStatisticsService.ConsentBodyFor(false), Does.Contain("loses focus").And.Not.Contain("closed normally"), "the Quest sends only at a loss of focus");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(host);
            }
        }
    }
}
