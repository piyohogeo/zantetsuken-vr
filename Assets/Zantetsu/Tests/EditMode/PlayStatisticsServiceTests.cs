using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

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
                Assert.That(service.Statistics.DeviceModel, Is.Null, "the phone model is not kept before consent");
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
                Assert.That(service.Statistics.DeviceModel,
                    Is.EqualTo(PlayerPlatform.IsAndroidFlat ? PlayEnvironment.OrNull(SystemInfo.deviceModel) : null),
                    "the phone model is included only in the non-VR Android build after consent");
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
                Assert.That(service.Statistics.DeviceModel, Is.Null, "withdrawal forgets the phone model as well");
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

        private sealed class FlatSource : IPlayStatisticsFlatSource
        {
            public bool Ready;
            public int ReadyReads, ConditionReads;
            public bool StatisticsReady { get { ReadyReads++; return Ready; } }
            public void ReadStatisticsConditions(ref PlayConditions conditions)
            {
                ConditionReads++;
                conditions.ScreenWidth = 1440;
                conditions.ScreenHeight = 3120;
                conditions.RenderScale = 0.65f;
                conditions.RefreshRateHz = 60f;
                conditions.MsaaSamples = 4;
                conditions.FsrEnabled = true;
                conditions.MultiDrawIndirect = true;
                conditions.DrawIndirectCount = false;
            }
        }

        private static object Call(PlayStatisticsService service, string method, params object[] arguments)
            => typeof(PlayStatisticsService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, arguments);

        [Test]
        public void TheFlatSource_IsNotReadBeforeConsent_AndSuppliesEffectiveConditionsOnlyWithoutXr()
        {
            var host = new GameObject("flat statistics source under test");
            try
            {
                PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
                var source = new FlatSource();
                service.FlatSource = source;
                // No configuration or transport: these tests cannot send anything to a network destination.
                service.Setup(SandboxScenarioKind.Play, "PhoneScene", new MemoryPlayStatisticsStore(), null);
                Call(service, "UpdateCore");
                Assert.That(source.ReadyReads + source.ConditionReads, Is.Zero, "no host state is queried before the agreement");
                Assert.That(service.Sampler.Reads, Is.Zero, "nor are frame timings sampled");

                service.Agree();
                Call(service, "UpdateCore");
                Assert.That(source.ConditionReads, Is.EqualTo(1), "the agreed non-XR update asks for the host's conditions");
                PlayConditions c = (PlayConditions)Call(service, "ReadConditions", false);
                Assert.That(c.Scene, Is.EqualTo("PhoneScene"), "the source extends the service's own labels");
                Assert.That(c.ScreenWidth, Is.EqualTo(1440));
                Assert.That(c.ScreenHeight, Is.EqualTo(3120));
                Assert.That(c.RenderScale, Is.EqualTo(0.65f));
                Assert.That(c.RefreshRateHz, Is.EqualTo(60f));
                Assert.That(c.MsaaSamples, Is.EqualTo(4));
                Assert.That(c.FsrEnabled && c.MultiDrawIndirect && !c.DrawIndirectCount, Is.True);
                int reads = source.ConditionReads;
                PlayConditions xr = (PlayConditions)Call(service, "ReadConditions", true);
                Assert.That(source.ConditionReads, Is.EqualTo(reads), "XR reads only its runtime, even with a source attached");
                Assert.That(xr.ScreenWidth + xr.ScreenHeight, Is.Zero);
                Assert.That(xr.FsrEnabled || xr.MultiDrawIndirect || xr.DrawIndirectCount, Is.False);

                service.Withdraw();
                Call(service, "UpdateCore");
                Assert.That(source.ConditionReads, Is.EqualTo(reads), "withdrawal stops source reads again");
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void AFlatHostsPreparation_ExcludesStartupAndResetFrames_EvenWhenTheWorldIsReady()
        {
            var host = new GameObject("flat preparation statistics under test");
            var worldHost = new GameObject("statistics world shell");
            worldHost.SetActive(false);
            CutWorldRoot world = worldHost.AddComponent<CutWorldRoot>();
            const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                GamePause.ResetForTest();
                PlayPreparation.ResetForSession();
                // No GPU or physics world is constructed. Only the state and ledger read by the service are supplied.
                typeof(CutWorldRoot).GetField("<IsReady>k__BackingField", Fields).SetValue(world, true);
                typeof(CutWorldRoot).GetField("<Ledger>k__BackingField", Fields)
                    .SetValue(world, new LogicalCutLedger(new LogicalCutIncompleteBudget(1)));
                PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
                var source = new FlatSource();
                service.FlatSource = source;
                service.Setup(SandboxScenarioKind.Play, "PhoneScene", new MemoryPlayStatisticsStore(), null);
                typeof(PlayStatisticsService).GetField("_world", Fields).SetValue(service, world);
                typeof(PlayStatisticsService).GetField("_nextLookup", Fields).SetValue(service, float.PositiveInfinity);
                service.Agree();
                for (int i = 0; i < 4; i++) Call(service, "UpdateCore");
                Assert.That(source.ReadyReads, Is.EqualTo(4));
                Assert.That(service.PlayStart.Began, Is.False, "the host's initial prewarming is not play");
                Assert.That(service.Statistics.HasPlay, Is.False);

                source.Ready = true;
                for (int i = 0; i < 8; i++) Call(service, "UpdateCore");
                Assert.That(service.PlayStart.Began, Is.True);
                Assert.That(service.Statistics.HasPlay, Is.True, "ordinary ready play is collected");
                ulong frames = service.Statistics.Current.Frames;
                source.Ready = false;
                for (int i = 0; i < 4; i++) Call(service, "UpdateCore");
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(frames), "reset/prewarming after play also counts no frames");
                source.Ready = true;
                Call(service, "UpdateCore");
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(frames), "the first frame after preparation is interrupted");
                Call(service, "UpdateCore");
                Assert.That(service.Statistics.Current.Frames, Is.EqualTo(frames + 1));
            }
            finally
            {
                // The shell owns no world resources; do not start a real world shutdown from its destruction.
                typeof(CutWorldRoot).GetField("_released", Fields).SetValue(world, true);
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(worldHost);
                GamePause.ResetForTest();
                PlayPreparation.ResetForSession();
            }
        }

        [Test]
        public void TheAndroidConsentText_DescribesScreenSettingsAndTouchControls_AndBackgroundOrFocusLossSending()
        {
            string body = PlayStatisticsService.ConsentBodyForAndroid;
            Assert.That(body, Does.Contain("screen dimensions").And.Contain("resolution scale").And.Contain("upscaling").And.Contain("indirect draw settings"));
            Assert.That(body, Does.Contain("device/OS/CPU/GPU model").And.Contain("hardware IDs").And.Contain("Never:"));
            Assert.That(body, Does.Contain("Cloudflare").And.Contain("90 days").And.Contain("eligible for automatic deletion").And.Contain("may take some time"));
            Assert.That(body, Does.Contain("background or loses focus").And.Contain("Menu > Statistics").And.Contain("agree again"));
            Assert.That(body, Does.Not.Contain("headset").And.Not.Contain("left menu button").And.Not.Contain("closed normally"));
            Assert.That(body, Does.Contain("already sent stay").And.Contain("killed or crashes"));
            Assert.That(PlayStatisticsService.MenuExplanationForAndroid,
                Does.Contain("screen dimensions").And.Contain("background or loses focus").And.Contain("OFF stops collecting and sending").And.Contain("agree again"));
        }

        private sealed class FakeRequest : IPlayStatisticsRequest
        {
            public bool Aborted;
            public Action<PlayStatisticsSendResult> Done;
            public void Abort() => Aborted = true;
        }

        private sealed class FakeTransport : IPlayStatisticsTransport
        {
            public readonly List<FakeRequest> Requests = new List<FakeRequest>();
            public IPlayStatisticsRequest Send(byte[] body, Action<PlayStatisticsSendResult> done)
            {
                var request = new FakeRequest { Done = done };
                Requests.Add(request);
                return request;
            }
            public void Accept(int index = 0)
                => Requests[index].Done(new PlayStatisticsSendResult { Success = true, StatusCode = 201 });
        }

        private static PlayStatisticsService ServiceWithFakeTransport(GameObject host, FakeTransport transport)
        {
            PlayStatisticsService service = host.AddComponent<PlayStatisticsService>();
            service.Setup(SandboxScenarioKind.Play, "PhoneScene", new MemoryPlayStatisticsStore(), null);
            var sender = new PlayStatisticsSender(service.Statistics, service.Consent, transport, "developer");
            const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(PlayStatisticsService).GetField("<Sender>k__BackingField", Fields).SetValue(service, sender);
            typeof(PlayStatisticsService).GetField("<Quit>k__BackingField", Fields).SetValue(service, new PlayStatisticsQuit(sender));
            return service;
        }

        private static void CountSomePlay(PlayStatisticsService service)
        {
            var conditions = new PlayConditions { Scene = "PhoneScene", Mode = "play" };
            for (int i = 0; i < 5; i++)
                service.Collector.Frame(true, false, true, conditions, 16f, new FrameTimeSample { HasMain = true, MainMs = 8f }, 1);
            Assert.That(service.Statistics.HasPlay, Is.True);
        }

        [TestCase(true, false)]
        [TestCase(false, false)]
        [TestCase(true, true)]
        [TestCase(false, true)]
        public void AndroidPauseAndFocusLoss_SendOneRevision_WhicheverArrivesFirst(bool pauseFirst, bool acceptedBeforeSecond)
        {
            var host = new GameObject("Android statistics lifecycle under test");
            try
            {
                var transport = new FakeTransport();
                PlayStatisticsService service = ServiceWithFakeTransport(host, transport);
                service.Agree();
                CountSomePlay(service);
                // Unity also reports paused=false at startup and on resuming: neither offers a snapshot.
                Call(service, "ApplicationPause", false, true);
                Assert.That(transport.Requests.Count, Is.Zero);
                if (pauseFirst) Call(service, "ApplicationPause", true, true);
                else Call(service, "OnApplicationFocus", false);
                Assert.That(transport.Requests.Count, Is.EqualTo(1), "the first lifecycle notification offers the play once");
                if (acceptedBeforeSecond) transport.Accept();
                if (pauseFirst) Call(service, "OnApplicationFocus", false);
                else Call(service, "ApplicationPause", true, true);
                Assert.That(transport.Requests.Count, Is.EqualTo(1), "the second notification cannot begin a duplicate send");
                if (!acceptedBeforeSecond) transport.Accept();
                Assert.That(service.Sender.Busy || service.Sender.HasPending, Is.False, "the duplicate in-flight offer is not resent at completion");
                Assert.That(service.Sender.Succeeded, Is.EqualTo(1));
                Assert.That(service.Sender.LastRevisionSent, Is.Zero);
                Call(service, "ApplicationPause", false, true);
                Call(service, "OnApplicationFocus", true);
                Call(service, "ApplicationPause", true, true);
                Assert.That(transport.Requests.Count, Is.EqualTo(1), "resuming sends nothing, and already accepted play is not resent");
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void AndroidPause_SendsNothingBeforeConsentOrAfterDecline_AndWithdrawalAbortsTheSend()
        {
            var host = new GameObject("Android statistics withdrawal under test");
            try
            {
                var transport = new FakeTransport();
                PlayStatisticsService service = ServiceWithFakeTransport(host, transport);
                Call(service, "ApplicationPause", true, true);
                Call(service, "OnApplicationFocus", false);
                Assert.That(transport.Requests.Count, Is.Zero, "an undecided launch sends nothing");
                service.Withdraw();
                Call(service, "ApplicationPause", true, true);
                Assert.That(transport.Requests.Count, Is.Zero, "declining also sends nothing");
                service.Agree();
                CountSomePlay(service);
                Call(service, "ApplicationPause", true, true);
                Assert.That(transport.Requests.Count, Is.EqualTo(1));
                service.Withdraw();
                Assert.That(transport.Requests[0].Aborted, Is.True);
                Assert.That(service.Sender.Busy || service.Sender.HasPending, Is.False);
                Call(service, "ApplicationPause", true, true);
                Call(service, "OnApplicationFocus", false);
                Assert.That(transport.Requests.Count, Is.EqualTo(1), "withdrawal prevents both lifecycle notifications sending again");
                Assert.That(service.Statistics.HasPlay, Is.False);
                Assert.That(service.Statistics.DeviceModel, Is.Null);
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void NonAndroidFlatPause_KeepsQuestAndDesktopBehavior_AndTheUnityCallbackUsesBuildIdentity()
        {
            var host = new GameObject("non-Android statistics pause under test");
            try
            {
                var transport = new FakeTransport();
                PlayStatisticsService service = ServiceWithFakeTransport(host, transport);
                service.Agree();
                CountSomePlay(service);
                Call(service, "ApplicationPause", false, false);
                Call(service, "ApplicationPause", true, false);
                Assert.That(transport.Requests.Count, Is.Zero, "Quest and PC pause callbacks continue to interrupt without sending");
                Call(service, "OnApplicationPause", true);
                Assert.That(transport.Requests.Count, Is.EqualTo(PlayerPlatform.IsAndroidFlat ? 1 : 0), "the Unity callback uses AndroidFlat identity, not Android alone");
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void AndroidPauseAndFocusLoss_AfterQuitRequest_DoNotOfferAnotherSnapshot()
        {
            var host = new GameObject("statistics quit lifecycle under test");
            try
            {
                var transport = new FakeTransport();
                PlayStatisticsService service = ServiceWithFakeTransport(host, transport);
                service.Agree();
                CountSomePlay(service);
                Assert.That(service.RequestQuit(1.0), Is.False, "the quit offers its final snapshot and waits");
                Assert.That(transport.Requests.Count, Is.EqualTo(1));
                Call(service, "ApplicationPause", true, true);
                Call(service, "OnApplicationFocus", false);
                Assert.That(service.Sender.HasPending, Is.False, "neither event offers even an in-flight duplicate after quit was requested");
                transport.Accept();
                Call(service, "ApplicationPause", true, true);
                Assert.That(transport.Requests.Count, Is.EqualTo(1));
            }
            finally { Object.DestroyImmediate(host); }
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
