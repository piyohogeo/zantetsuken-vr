using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Where the counted play begins and by whose focus (DESIGN 21.18, format 2): no frame before the world is ready,
    /// the scene's start-up preparation over and, in XR, the head tracked -- a saved agreement or not -- and none of
    /// the preparation asked again once play has begun; in XR the session's focus and not the desktop window's; the
    /// driver's version only for the very device that draws; a generic XR device name not taken for a device's.
    /// </summary>
    public sealed class PlayStatisticsStartTests
    {
        [SetUp]
        public void Reset() => PlayPreparation.ResetForSession();

        [TearDown]
        public void ResetAfter() => PlayPreparation.ResetForSession();

        [Test]
        public void PlayBegins_WhenTheWorldIsReady_ThePreparationOver_AndTheHeadTracked_AndIsNotAskedAgain()
        {
            var start = new PlayStartConditions();
            // The world not ready, 3 parts preparing, the head not tracked.
            Assert.That(start.Ask(false, 3, true, false, 395, 10.0, out bool now), Is.False);
            Assert.That(now, Is.False);
            // The world ready, the preparation still going on (the crowd's pool: a long frame here is not play).
            Assert.That(start.Ask(true, 3, true, true, 397, 10.1, out now), Is.False);
            Assert.That(start.Ask(true, 1, true, true, 398, 12.3, out now), Is.False);
            // The preparation over, the head not yet tracked.
            Assert.That(start.Ask(true, 0, true, false, 520, 13.6, out now), Is.False);
            Assert.That(start.Began, Is.False);
            // All three at once: play begins in this frame, once.
            Assert.That(start.Ask(true, 0, true, true, 521, 13.7, out now), Is.True);
            Assert.That(now, Is.True);
            Assert.That(start.BeganFrame, Is.EqualTo(521));
            Assert.That(start.WorldReadyFrame, Is.EqualTo(397));
            Assert.That(start.PreparedFrame, Is.EqualTo(520));
            Assert.That(start.TrackedFrame, Is.EqualTo(521), "tracked at 397, lost by 520: tracked without a break since 521");
            Assert.That(start.MostPending, Is.EqualTo(3));
            Assert.That(start.Describe(), Does.Contain("play began at frame 521").And.Contain("3.70 s after frame 395"));

            // Begun: a preparation that comes later (a part enabled mid-play), or tracking lost, takes no frame out of play.
            Assert.That(start.Ask(true, 5, true, false, 600, 14.5, out now), Is.True);
            Assert.That(now, Is.False, "play begins once");
            Assert.That(start.BeganFrame, Is.EqualTo(521));

            // Without XR there is no head to wait for.
            var flat = new PlayStartConditions();
            Assert.That(flat.Ask(true, 0, false, false, 3, 0.1, out now), Is.True);

            // Another world: not begun for it.
            start.Reset();
            Assert.That(start.Began, Is.False);
            Assert.That(start.Ask(true, 2, true, true, 900, 30.0, out now), Is.False);
        }

        [Test]
        public void TheHeadsTracking_IsItsStateInThatFrame_TrackedOnceAndLostSinceDoesNotBeginThePlay()
        {
            // The order the review named: tracked while the scene still prepares, lost, and only then the preparation over.
            var start = new PlayStartConditions();
            Assert.That(start.Ask(true, 2, true, headTracked: true, 400, 1.0, out bool now), Is.False, "tracked, still preparing");
            Assert.That(start.TrackedFrame, Is.EqualTo(400));
            Assert.That(start.Ask(true, 2, true, headTracked: false, 401, 1.1, out now), Is.False, "the tracking lost");
            Assert.That(start.TrackedFrame, Is.EqualTo(-1), "no longer tracked: not kept from before");
            Assert.That(start.Ask(true, 0, true, headTracked: false, 500, 2.2, out now), Is.False, "prepared, but the head is not tracked now");
            Assert.That(start.Began, Is.False, "a head tracked a moment ago does not begin the play");
            Assert.That(start.Ask(true, 0, true, headTracked: false, 501, 2.3, out now), Is.False);
            Assert.That(start.Ask(true, 0, true, headTracked: true, 502, 2.4, out now), Is.True, "tracked in this frame, with the rest: now");
            Assert.That(start.BeganFrame, Is.EqualTo(502));
            Assert.That(start.TrackedFrame, Is.EqualTo(502));

            // The same for the other two: a world that stopped being ready, a preparation that began again -- before play began.
            var again = new PlayStartConditions();
            Assert.That(again.Ask(true, 0, true, false, 10, 0.1, out now), Is.False);
            Assert.That(again.PreparedFrame, Is.EqualTo(10));
            Assert.That(again.Ask(true, 1, true, true, 11, 0.2, out now), Is.False, "a part began to prepare again");
            Assert.That(again.PreparedFrame, Is.EqualTo(-1));
            Assert.That(again.Ask(false, 0, true, true, 12, 0.3, out now), Is.False, "the world not ready");
            Assert.That(again.WorldReadyFrame, Is.EqualTo(-1));
            Assert.That(again.Ask(true, 0, true, true, 13, 0.4, out now), Is.True);
            Assert.That(again.WorldReadyFrame, Is.EqualTo(13));
            Assert.That(again.PreparedFrame, Is.EqualTo(13));
            Assert.That(again.TrackedFrame, Is.EqualTo(11));
        }

        [Test]
        public void NoFrameIsCounted_BeforePlayBegins_WithASavedAgreement_AndALongFrameAfterItStays()
        {
            // As the service joins them: the collector is told the world is ready only from where play has begun.
            var stats = new PlayStatistics();
            var collector = new PlayStatisticsCollector(stats);
            collector.Agree();   // a saved agreement: agreed from the launch
            var start = new PlayStartConditions();
            var conditions = new PlayConditions { Scene = "WalkCity", Mode = "play", DrawRoute = "VP3C-NATIVE", XrRuntime = "Oculus", RefreshRateHz = 90f };
            PlayPreparation.Begin();   // the crowd
            PlayPreparation.Begin();   // a placed cuttable
            int frame = 395;
            void Frame(float intervalMs, bool worldReady = true, bool tracked = true)
            {
                start.Ask(worldReady, PlayPreparation.Pending, true, tracked, frame, frame * 0.011, out _);
                collector.Frame(worldReady && start.Began, false, true, in conditions, intervalMs, new FrameTimeSample { HasMain = true, MainMs = intervalMs }, 1);
                frame++;
            }

            Frame(11f);
            Frame(2167f);          // the crowd's pool is made in this frame: 2.2 s, and it is not play
            PlayPreparation.End();
            for (int i = 0; i < 30; i++) Frame(11f);   // the placed cuttables still register
            Assert.That(stats.HasPlay, Is.False, "nothing counted while the preparation goes on");
            Assert.That(stats.EpochCount, Is.Zero);
            PlayPreparation.End();
            Assert.That(PlayPreparation.Pending, Is.Zero);
            for (int i = 0; i < 12; i++) Frame(11f);
            Assert.That(start.Began, Is.True);
            Assert.That(stats.Current.Frames, Is.EqualTo(10), "from where play began (less the gate's first frame and the epoch's)");
            Assert.That(stats.Current.Interval.MaxMs, Is.EqualTo(11f), "the 2.2 s frame of the preparation is in no histogram");

            // A long frame of ordinary play is play: it stays in the record.
            Frame(450f);
            Assert.That(stats.Current.Frames, Is.EqualTo(11));
            Assert.That(stats.Current.Interval.MaxMs, Is.EqualTo(450f));
            Assert.That(stats.Current.Interval.Overflow, Is.EqualTo(1));
        }

        [Test]
        public void ThePreparationCount_RisesAndFallsByItsParts_AndNeverGoesBelowNothing()
        {
            Assert.That(PlayPreparation.Pending, Is.Zero);
            PlayPreparation.Begin();
            PlayPreparation.Begin();
            Assert.That(PlayPreparation.Pending, Is.EqualTo(2));
            PlayPreparation.End();
            PlayPreparation.End();
            PlayPreparation.End();   // one End too many is no negative count
            Assert.That(PlayPreparation.Pending, Is.Zero);
            Assert.That(PlayPreparation.Begun, Is.EqualTo(2));
            Assert.That(PlayPreparation.Ended, Is.EqualTo(3));
        }

        [Test]
        public void APlacedCuttable_IsPreparing_FromItsEnabling_UntilItCanNoLongerRegister()
        {
            // (The engine sends no OnEnable/Update to a component in an EditMode test: the messages are given by hand, in the
            // engine's order.)
            var host = new GameObject("placed cuttable under test");
            try
            {
                PlayableCityCuttable placed = host.AddComponent<PlayableCityCuttable>();
                void Send(string message) => typeof(PlayableCityCuttable).GetMethod(message, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(placed, null);
                Assert.That(PlayPreparation.Pending, Is.Zero, "not enabled: not preparing");
                Send("OnEnable");
                Assert.That(PlayPreparation.Pending, Is.EqualTo(1), "enabled and not yet a cut target");
                Send("OnEnable");
                Assert.That(PlayPreparation.Pending, Is.EqualTo(1), "told once");
                Send("OnDisable");
                Assert.That(PlayPreparation.Pending, Is.Zero, "disabled: it prepares nothing");
                Send("OnDisable");
                Assert.That(PlayPreparation.Pending, Is.Zero);
                Send("OnEnable");
                Assert.That(PlayPreparation.Pending, Is.EqualTo(1));

                // No world to register into: it can never become a cut target, and stops holding play back at its first update.
                Assert.That(placed.world, Is.Null);
                Send("Update");
                Assert.That(PlayPreparation.Pending, Is.Zero, "nothing to wait for");
                Send("Update");
                Assert.That(PlayPreparation.Ended, Is.EqualTo(2), "told once more only (the disabling and this)");
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void InXr_TheSessionsFocusDecides_NotTheDesktopWindows()
        {
            // XR by OpenXR: the session's focus alone.
            Assert.That(PlayFocus.ForPlay(xrActive: true, openXr: true, xrSessionFocused: true, desktopFocused: false), Is.True, "another window clicked, the play goes on in the headset");
            Assert.That(PlayFocus.ForPlay(true, true, false, true), Is.False, "the system menu over the game, the window still in focus");
            // Without XR: the window's.
            Assert.That(PlayFocus.ForPlay(false, false, false, true), Is.True);
            Assert.That(PlayFocus.ForPlay(false, false, true, false), Is.False);
            // XR by a loader whose session state is not known: the window's.
            Assert.That(PlayFocus.ForPlay(true, false, false, true), Is.True);

            // Joined to the collector: frames with the desktop window out of focus are counted while the session is focused.
            var stats = new PlayStatistics();
            var collector = new PlayStatisticsCollector(stats);
            collector.Agree();
            var conditions = new PlayConditions { Scene = "WalkCity", Mode = "play" };
            for (int i = 0; i < 12; i++)
            {
                collector.Frame(true, false, PlayFocus.ForPlay(true, true, true, desktopFocused: i < 4), in conditions, 11f, new FrameTimeSample(), 1);
            }

            Assert.That(stats.Current.Frames, Is.EqualTo(10), "the window lost its focus at the fifth frame: no frame was left out for it");
            for (int i = 0; i < 5; i++) collector.Frame(true, false, PlayFocus.ForPlay(true, true, false, true), in conditions, 11f, new FrameTimeSample(), 1);
            Assert.That(stats.Current.Frames, Is.EqualTo(10), "the session out of focus (the system menu): not counted");
        }

        [Test]
        public void TheDriversVersion_IsTheDrawingDevicesOwn_OrNotObtained()
        {
            var entries = new List<GraphicsDriverVersion.Entry>
            {
                new GraphicsDriverVersion.Entry { MatchingDeviceId = @"Root\MetaVirtualScreenDriver", DriverVersion = "15.52.32.370" },
                new GraphicsDriverVersion.Entry { MatchingDeviceId = @"pci\ven_10de&dev_2204", DriverVersion = "32.0.15.9159" },
                new GraphicsDriverVersion.Entry { MatchingDeviceId = @"PCI\VEN_10DE&DEV_1F02&SUBSYS_12345678", DriverVersion = "31.0.15.5000" },
            };
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x2204, entries), Is.EqualTo("32.0.15.9159"));
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x1F02, entries), Is.EqualTo("31.0.15.5000"), "the other GPU has its own, whatever the case of the id");
            Assert.That(GraphicsDriverVersion.Choose(0x1002, 0x2204, entries), Is.Null, "another vendor with the same device number: not that device");
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x220, entries), Is.Null, "a prefix of a device number is not that device");
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x9999, entries), Is.Null, "no entry of that device: not obtained, never another GPU's");
            Assert.That(GraphicsDriverVersion.Choose(0, 0, entries), Is.Null);
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x2204, null), Is.Null);

            // Two entries of the same device: the version they agree on, or none where they do not.
            entries.Add(new GraphicsDriverVersion.Entry { MatchingDeviceId = @"pci\ven_10de&dev_2204", DriverVersion = "32.0.15.9159" });
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x2204, entries), Is.EqualTo("32.0.15.9159"));
            entries.Add(new GraphicsDriverVersion.Entry { MatchingDeviceId = @"pci\ven_10de&dev_2204", DriverVersion = "31.0.15.1111" });
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x2204, entries), Is.Null, "they disagree: not obtained");
            // An entry of that device with no version.
            Assert.That(GraphicsDriverVersion.Choose(0x10DE, 0x2204, new[] { new GraphicsDriverVersion.Entry { MatchingDeviceId = @"pci\ven_10de&dev_2204", DriverVersion = null } }), Is.Null);

            // This machine: whatever is read is a version of dotted numbers or nothing -- never the graphics API's text.
            string here = GraphicsDriverVersion.Read(SystemInfo.graphicsDeviceVendorID, SystemInfo.graphicsDeviceID);
            TestContext.Out.WriteLine("this machine: vendor 0x" + SystemInfo.graphicsDeviceVendorID.ToString("x4") + " device 0x" + SystemInfo.graphicsDeviceID.ToString("x4")
                + " (" + SystemInfo.graphicsDeviceName + "), graphics API '" + SystemInfo.graphicsDeviceVersion + "', driver " + (here ?? "not obtained"));
            if (here != null) Assert.That(here, Does.Match(@"^\d+(\.\d+)+$"));
        }

        [Test]
        public void AGenericXrDeviceName_IsNotADevicesName()
        {
            Assert.That(PlayEnvironment.XrDeviceName("Head Tracking - OpenXR"), Is.Null, "the engine's generic name: not obtained");
            Assert.That(PlayEnvironment.XrDeviceName("  "), Is.Null);
            Assert.That(PlayEnvironment.XrDeviceName(null), Is.Null);
            Assert.That(PlayEnvironment.XrDeviceName("none"), Is.Null);
            Assert.That(PlayEnvironment.XrDeviceName("Something - OpenXR"), Is.Null);
            Assert.That(PlayEnvironment.XrDeviceName(" Meta Quest 3S "), Is.EqualTo("Meta Quest 3S"), "a device's own name is kept as given");
            Assert.That(PlayEnvironment.OrNull(""), Is.Null);
            Assert.That(PlayEnvironment.OrNull("Oculus"), Is.EqualTo("Oculus"));

            // The runtime's name: its own, or null where it gave none -- never a stand-in, so that the two are told apart.
            Assert.That(PlayEnvironment.XrRuntimeName(xrActive: true, runtimeOwnName: "Oculus"), Is.EqualTo("Oculus"));
            Assert.That(PlayEnvironment.XrRuntimeName(true, ""), Is.Null, "XR runs and the runtime gave no name: not obtained");
            Assert.That(PlayEnvironment.XrRuntimeName(true, null), Is.Null);
            Assert.That(PlayEnvironment.XrRuntimeName(false, "Oculus"), Is.EqualTo(PlayEnvironment.NoXr), "no XR is a state, not a missing name");
            var stats = new PlayStatistics { XrRuntime = PlayEnvironment.XrRuntimeName(true, null) };
            var json = new System.Text.StringBuilder();
            PlayStatisticsJson.Write(json, stats, "s", "i", 0, "developer");
            Assert.That(json.ToString(), Does.Contain("\"xrRuntime\":null,\"xrRuntimeVersion\":null"), "written as null, not as a name of the loader or the display");
            stats.XrRuntime = PlayEnvironment.XrRuntimeName(false, null);
            PlayStatisticsJson.Write(json, stats, "s", "i", 0, "developer");
            Assert.That(json.ToString(), Does.Contain("\"xrRuntime\":\"none\""));
        }
    }
}
