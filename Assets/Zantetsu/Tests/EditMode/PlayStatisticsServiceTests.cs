using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The play statistics in the game (DESIGN 21.18): which frames count as ordinary play, and the service's consent
    /// handling -- nothing collected before an agreement, everything cleared and the sender withdrawn after a decline.
    /// </summary>
    public sealed class PlayStatisticsServiceTests
    {
        [Test]
        public void TheGate_CountsOnlyAgreedReadyUnpausedFocusedFrames_AndNeverTheFirstAfterAnInterruption()
        {
            var g = new PlayFrameGate();
            Assert.That(g.Admit(agreed: true, worldReady: true, paused: false, focused: true), Is.False, "the first frame of all: an interruption's successor");
            Assert.That(g.Admit(true, true, false, true), Is.True);
            Assert.That(g.Admit(true, true, false, true), Is.True);
            Assert.That(g.Admit(false, true, false, true), Is.False, "no agreement");
            Assert.That(g.Admit(true, true, false, true), Is.False, "the first frame after it");
            Assert.That(g.Admit(true, true, false, true), Is.True);
            Assert.That(g.Admit(true, false, false, true), Is.False, "the world not ready (loading, start-up)");
            Assert.That(g.Admit(true, true, true, true), Is.False, "paused");
            Assert.That(g.Admit(true, true, false, false), Is.False, "out of focus");
            Assert.That(g.Admit(true, true, false, true), Is.False, "the first frame back");
            Assert.That(g.Admit(true, true, false, true), Is.True);
            g.Interrupt();
            Assert.That(g.Admit(true, true, false, true), Is.False, "the first frame after a change of conditions");
            Assert.That(g.Admit(true, true, false, true), Is.True);
            Assert.That(g.Counted, Is.EqualTo(5));
            Assert.That(g.Skipped, Is.EqualTo(8));
        }

        [Test]
        public void TheService_CollectsNothingBeforeTheAgreement_AndClearsAndWithdrawsOnADecline()
        {
            var host = new GameObject("statistics under test");
            try
            {
                PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
                var store = new MemoryPlayStatisticsStore();
                int driverReads = GraphicsDriverVersion.Reads;
                service.Setup(SandboxScenarioKind.Play, "TestScene", store, null);
                Assert.That(service.Consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
                // Before the agreement nothing of the machine is read for the statistics: no name, no version of a driver.
                Assert.That(GraphicsDriverVersion.Reads, Is.EqualTo(driverReads), "the driver entries are not read before the agreement");
                Assert.That(service.Statistics.Platform + service.Statistics.OperatingSystem + service.Statistics.Cpu + service.Statistics.Gpu + service.Statistics.GraphicsApi, Is.Empty);
                Assert.That(service.Statistics.GpuDriver, Is.Null);
                Assert.That(service.Statistics.XrRuntime, Is.Null);
                Assert.That(service.Sender.OnFocusLost(), Does.StartWith("not agreed"), "a focus change before the answer sends nothing");
                Assert.That(service.Statistics.HasPlay, Is.False);
                Assert.That(service.Statistics.SessionId, Is.EqualTo(service.Sender.SessionId));

                Assert.That(service.Collector.Agreed, Is.False, "the collector takes nothing before the agreement");

                service.Agree();
                Assert.That(service.Consent.Agreed, Is.True);
                Assert.That(service.Collector.Agreed, Is.True);
                Assert.That(GraphicsDriverVersion.Reads, Is.EqualTo(driverReads + 1), "read once, at the agreement");
                Assert.That(service.Statistics.Platform, Is.Not.Empty, "the environment is read from the agreement on");
                Assert.That(service.Statistics.GraphicsApi, Is.EqualTo(SystemInfo.graphicsDeviceVersion));
                Assert.That(service.Statistics.TimeSource, Does.StartWith("FrameTimingManager"));
                Assert.That(store.TryGet(PlayStatisticsConsent.InstallIdKey, out string id) && System.Guid.TryParse(id, out _), Is.True);
                // Frames as the service would hand them to the collector (no world in this test: the frames of a ready one are
                // given directly), then a decline: all of it gone, nothing sent afterwards.
                var conditions = new PlayConditions { Scene = "TestScene", Mode = "play" };
                for (int i = 0; i < 4; i++) service.Collector.Frame(true, false, true, conditions, 14f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
                service.Collector.Live(3);
                Assert.That(service.Statistics.HasPlay, Is.True);
                Assert.That(service.Statistics.EpochCount, Is.EqualTo(1), "the epoch begun by the agreement and the first frame, not by a call of its own");
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(2));
                Assert.That(service.Sender.OnFocusLost(), Does.StartWith("no transport"), "no config: nothing is sent, and it is said");

                service.Withdraw();
                Assert.That(service.Consent.Agreed, Is.False);
                Assert.That(service.Collector.Agreed, Is.False);
                Assert.That(service.Statistics.HasPlay, Is.False, "cleared");
                Assert.That(service.Statistics.MaxLiveFragments, Is.Zero);
                Assert.That(service.Statistics.Platform + service.Statistics.Gpu + service.Statistics.GraphicsApi, Is.Empty, "withdrawn: nothing of the environment is kept");
                Assert.That(service.Statistics.GpuDriver, Is.Null);
                Assert.That(service.Sender.OnFocusLost(), Does.StartWith("not agreed"));
                Assert.That(new PlayStatisticsConsent(store).Decision, Is.EqualTo(PlayStatisticsDecision.Declined), "kept: not asked again");
                for (int i = 0; i < 4; i++) service.Collector.Frame(true, false, true, conditions, 14f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
                Assert.That(service.Statistics.HasPlay, Is.False, "after the withdrawal, frames count for nothing");

                // Agreed again: the collection starts from nothing, the epoch again from the first counted frame.
                service.Agree();
                Assert.That(GraphicsDriverVersion.Reads, Is.EqualTo(driverReads + 2), "and once more at the agreement given again");
                Assert.That(service.Statistics.Platform, Is.Not.Empty);
                for (int i = 0; i < 5; i++) service.Collector.Frame(true, false, true, conditions, 14f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
                Assert.That(service.Statistics.EpochCount, Is.EqualTo(1));
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(3));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void TheConsentText_NamesWhatIsSent_WhatIsNot_TheUse_TheReceiver_AndTheRetention()
        {
            string body = PlayStatisticsService.ConsentBody;
            Assert.That(body, Does.Contain("frame time").And.Contain("Cloudflare").And.Contain("Retention").And.Contain("Never:").And.Contain("hardware IDs").And.Contain("improving"));
            Assert.That(body, Does.Not.Contain("completely anonymous").And.Not.Contain("fully anonymous"));
            // Unit 9-B: the retention is the receiver's 90-day rule, said as eligibility (the deletion runs after the period), not as a guarantee of deletion within it;
            // withdrawal stops future collection and sending and does not delete what was sent; a killed or crashed app sends nothing; the version is 2.
            Assert.That(body, Does.Contain("90 days").And.Contain("eligible for automatic deletion").And.Contain("may take some time"));
            Assert.That(body, Does.Not.Contain("to be decided").And.Not.Contain("within 90 days").And.Not.Contain("deleted within"));
            Assert.That(body, Does.Contain("Withdraw").And.Contain("stop from then on").And.Contain("already sent stay"));
            Assert.That(body, Does.Contain("killed or crashes"));
            Assert.That(body, Does.Contain("CPU").And.Contain("driver version"), "format 2's environment fields are named");
            Assert.That(PlayStatisticsConsent.CurrentTextVersion, Is.EqualTo(2));
            Assert.That(PlayStatisticsService.MenuExplanationFor(false), Does.Contain("90 days").And.Contain("loses focus").And.Not.Contain("closed normally"));
            Assert.That(PlayStatisticsService.MenuExplanationFor(true), Does.Contain("closed normally"));
            Assert.That(PlayStatisticsService.ConsentAgree, Is.EqualTo("Agree"));
            Assert.That(PlayStatisticsService.ConsentDecline, Is.EqualTo("Don't send"));
        }
    }
}
