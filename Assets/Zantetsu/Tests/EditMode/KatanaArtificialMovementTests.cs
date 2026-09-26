using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The katana's gesture leaves the player's artificial movement out (U4 / T-088): it is judged in tracking space,
    /// a latch is settled in the world by the placement at that moment, and an open span keeps taking its guide from
    /// the katana as it is shown now (DESIGN 19.1.5.1). Each update goes the live way round: the movement through the
    /// Sandbox adapter's own entry first, then the device grip pose through the katana's Update path.
    /// </summary>
    public class KatanaArtificialMovementTests
    {
        private const double Interval = 0.011;
        private const float BladeLength = 0.9f;
        private const float Tolerance = 1e-4f;
        private static readonly Vector3 EdgeStep = new Vector3(0f, -0.12f, 0f);

        private readonly List<GameObject> _made = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject made in _made)
            {
                if (made != null)
                {
                    Object.DestroyImmediate(made);
                }
            }

            _made.Clear();
        }

        private sealed class Rig
        {
            public Transform Origin;
            public Transform Offset;
            public SandboxLocomotionInput Input;
            public SandboxRightHandKatana Katana;
            public Transform Shown;
        }

        private Rig MakeRig(Vector3 origin = default, float yaw = 0f)
        {
            var root = new GameObject("XR Origin");
            _made.Add(root);
            root.transform.SetPositionAndRotation(origin, Quaternion.Euler(0f, yaw, 0f));
            Transform offset = new GameObject("Camera Offset").transform;
            offset.SetParent(root.transform, false);
            Transform head = new GameObject("Main Camera").transform;
            head.SetParent(offset, false);
            head.localPosition = new Vector3(0f, 1.6f, 0f);

            PlayerLocomotion locomotion = root.AddComponent<PlayerLocomotion>();
            locomotion.Configure(
                null,
                head,
                new PlayerCapsuleAuthoring(new Vector3(0f, 0.9f, 0f), Vector3.zero, 0.25f, 1.6f),
                new PlayerCapsuleAuthoring(Vector3.zero, Vector3.zero, 0.15f, 0.3f));
            SandboxLocomotionInput input = root.AddComponent<SandboxLocomotionInput>();
            input.Configure(locomotion, head);
            input.LiveInputEnabled = false;

            var rigObject = new GameObject("Sandbox Katana Rig");
            _made.Add(rigObject);
            var shown = new GameObject("Katana");
            _made.Add(shown);
            SandboxRightHandKatana katana = rigObject.AddComponent<SandboxRightHandKatana>();
            katana.Katana = shown.transform;
            var serialized = new SerializedObject(katana);
            serialized.FindProperty("trackingSpace").objectReferenceValue = offset;
            serialized.FindProperty("viewForwardReference").objectReferenceValue = head;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            return new Rig { Origin = root.transform, Offset = offset, Input = input, Katana = katana, Shown = shown.transform };
        }

        // One update, the live way round: movement, then the gesture.
        private static void Step(Rig rig, in BladePoseSample device, SandboxLocomotionCommand command)
        {
            rig.Input.Submit(command, (float)Interval);
            rig.Katana.TryRecordSample(device);
        }

        private static Quaternion Upright => Quaternion.Inverse(Quaternion.Euler(-15f, 0f, 0f));

        private static BladePoseSample Device(int update, Vector3 position, Quaternion rotation)
        {
            return new BladePoseSample(update + 1, Interval * (update + 1), position, rotation,
                BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static SandboxLocomotionCommand None => new SandboxLocomotionCommand(Vector2.zero, 0);

        private static SandboxLocomotionCommand Turn(int steps) => new SandboxLocomotionCommand(Vector2.zero, steps);

        private static SandboxLocomotionCommand Move(float x, float y) => new SandboxLocomotionCommand(new Vector2(x, y), 0);

        private delegate SandboxLocomotionCommand Pattern(int update);

        // The existing normal sweep: 10 still updates, 8 of 0.12 m along the edge in 11 ms, then still.
        private const int SweepBefore = 10;
        private const int SweepLength = 8;
        private const int SweepUpdates = 58;

        private static Vector3 SweepPosition(int update)
        {
            int steps = Mathf.Clamp(update - SweepBefore + 1, 0, SweepLength);
            return new Vector3(0f, 1.4f, 0.3f) + (EdgeStep * steps);
        }

        private sealed class LatchState
        {
            public int Update = -1;
            public Pose Space;
            public Plane Plane;
            public Vector3 Origin, Travel, SpanAxis, SegmentStart, SegmentEnd;
            public float Span;
        }

        private sealed class SweepResult
        {
            public readonly List<int> Accepted = new List<int>();
            public readonly LatchState Latch = new LatchState();
            public int WavesAtEnd;
        }

        private SweepResult Sweep(Pattern pattern, Vector3 origin = default, float yaw = 0f)
        {
            Rig rig = MakeRig(origin, yaw);
            var result = new SweepResult();
            for (int u = 0; u < SweepUpdates; u++)
            {
                Step(rig, Device(u, SweepPosition(u), Upright), pattern(u));
                result.Accepted.Add(rig.Katana.AcceptedSampleCount);
                if (result.Latch.Update < 0 && rig.Katana.WaveCount > 0)
                {
                    Assert.That(rig.Katana.TryGetWave(0, out _, out Plane plane, out Vector3 waveOrigin, out Vector3 travel,
                        out Vector3 spanAxis, out float span, out _, out _, out Vector3 start, out Vector3 end), Is.True);
                    result.Latch.Update = u;
                    result.Latch.Space = rig.Katana.TrackingSpacePose;
                    result.Latch.Plane = plane;
                    result.Latch.Origin = waveOrigin;
                    result.Latch.Travel = travel;
                    result.Latch.SpanAxis = spanAxis;
                    result.Latch.Span = span;
                    result.Latch.SegmentStart = start;
                    result.Latch.SegmentEnd = end;
                }
            }

            result.WavesAtEnd = rig.Katana.WaveCount;
            return result;
        }

        private static IEnumerable<(string name, Pattern pattern)> MovementDuringTheSweep()
        {
            for (int k = SweepBefore - 1; k < SweepBefore + SweepLength; k++)
            {
                int at = k;
                yield return ("snap right at update " + at, u => u == at ? Turn(1) : None);
                yield return ("snap left at update " + at, u => u == at ? Turn(-1) : None);
            }

            yield return ("forward throughout", u => Move(0f, 1f));
            yield return ("strafe right throughout", u => Move(1f, 0f));
            yield return ("back throughout", u => Move(0f, -1f));
            yield return ("snaps and strafing", u => u % 3 == 0 ? new SandboxLocomotionCommand(new Vector2(-1f, 0f), u % 2 == 0 ? 1 : -1) : Move(-1f, 0f));
        }

        [Test]
        public void AStillHand_UnderRepeatedSnapTurnsAndMovement_IsNeitherAcceptedNorLatched()
        {
            var grips = new (string name, Quaternion rotation)[]
            {
                ("edge left", Quaternion.Euler(0f, 0f, -90f) * Upright),
                ("edge right", Quaternion.Euler(0f, 0f, 90f) * Upright),
                ("upright", Upright),
            };
            var patterns = new (string name, Pattern pattern)[]
            {
                ("snap left every 15 updates", u => u % 15 == 5 ? Turn(-1) : None),
                ("snap right every 15 updates", u => u % 15 == 5 ? Turn(1) : None),
                // Every 11, not 10: twelve 30 degree turns would bring the player back to where they started.
                ("strafing with a snap every 11 updates", u => u % 11 == 5 ? new SandboxLocomotionCommand(new Vector2(1f, 0f), 1) : Move(1f, 0f)),
            };

            foreach (var grip in grips)
            {
                foreach (var pattern in patterns)
                {
                    Rig rig = MakeRig();
                    Vector3 shownBefore = default;
                    for (int u = 0; u < 120; u++)
                    {
                        Step(rig, Device(u, new Vector3(0.25f, 1.2f, 0.35f), grip.rotation), pattern.pattern(u));
                        Assert.That(rig.Katana.AcceptedSampleCount, Is.EqualTo(0), grip.name + ", " + pattern.name + ", update " + u);
                        Assert.That(rig.Katana.WaveCount, Is.EqualTo(0), grip.name + ", " + pattern.name + ", update " + u);
                        if (u == 0)
                        {
                            shownBefore = rig.Shown.position;
                        }
                    }

                    // Not vacuous: the player did turn, and the katana shown went with them.
                    Assert.That(Quaternion.Angle(rig.Origin.rotation, Quaternion.identity), Is.GreaterThan(1f), pattern.name);
                    Assert.That(Vector3.Distance(rig.Shown.position, shownBefore), Is.GreaterThan(0.1f), pattern.name);
                }
            }
        }

        [Test]
        public void TheSameSwing_WithMovementOrSnapTurns_IsAcceptedAndLatchedAsWithout()
        {
            SweepResult baseline = Sweep(u => None);
            Assert.That(baseline.Latch.Update, Is.GreaterThanOrEqualTo(0), "the normal sweep latches");
            Assert.That(baseline.WavesAtEnd, Is.EqualTo(1));

            foreach (var movement in MovementDuringTheSweep())
            {
                SweepResult moved = Sweep(movement.pattern);
                Assert.That(moved.Accepted, Is.EqualTo(baseline.Accepted), movement.name + ": accepted samples, update by update");
                Assert.That(moved.Latch.Update, Is.EqualTo(baseline.Latch.Update), movement.name + ": latch update");
                Assert.That(moved.WavesAtEnd, Is.EqualTo(baseline.WavesAtEnd), movement.name);
            }
        }

        [Test]
        public void TheLatchPlacementTakenAway_LeavesThePlaneOriginAxesAndInitialSpanOfTheSwingWithout()
        {
            SweepResult baseline = Sweep(u => None);
            foreach (var movement in MovementDuringTheSweep())
            {
                SweepResult moved = Sweep(movement.pattern, new Vector3(3f, 0f, -2f), 40f);
                Pose space = moved.Latch.Space;
                Quaternion back = Quaternion.Inverse(space.rotation);
                Vector3 Point(Vector3 world) => back * (world - space.position);

                Assert.That(Vector3.Angle(back * moved.Latch.Plane.normal, baseline.Latch.Plane.normal), Is.LessThan(0.01f), movement.name + ": plane normal");
                // The published plane's own position: its point -normal * distance, taken back into tracking space,
                // lies on the plane of the swing without movement.
                Vector3 publishedPoint = Point(-moved.Latch.Plane.normal * moved.Latch.Plane.distance);
                Assert.That(Mathf.Abs(baseline.Latch.Plane.GetDistanceToPoint(publishedPoint)),
                    Is.LessThan(Tolerance), movement.name + ": plane position");
                AssertNear(Point(moved.Latch.Origin), baseline.Latch.Origin, movement.name + ": origin");
                AssertNear(back * moved.Latch.Travel, baseline.Latch.Travel, movement.name + ": travel axis");
                AssertNear(back * moved.Latch.SpanAxis, baseline.Latch.SpanAxis, movement.name + ": span axis");
                Assert.That(moved.Latch.Span, Is.EqualTo(baseline.Latch.Span).Within(Tolerance), movement.name + ": initial span");
                AssertNear(Point(moved.Latch.SegmentStart), baseline.Latch.SegmentStart, movement.name + ": initial segment start");
                AssertNear(Point(moved.Latch.SegmentEnd), baseline.Latch.SegmentEnd, movement.name + ": initial segment end");
            }
        }

        [Test]
        public void AFiredWave_KeepsItsSettledValues_AndItsOpenSpanIsGuidedByTheKatanaAsShown()
        {
            SweepResult baseline = Sweep(u => None);
            int latch = baseline.Latch.Update;
            Rig rig = MakeRig();
            Rig still = MakeRig();
            bool sawOpenGuide = false;
            bool sawClosedGuide = false;
            float largestGuideShift = 0f;
            for (int u = 0; u < SweepUpdates; u++)
            {
                SandboxLocomotionCommand command = u == latch + 1 ? Turn(1) : (u > latch + 1 && u <= latch + 12 ? Move(1f, 0f) : None);
                Step(rig, Device(u, SweepPosition(u), Upright), command);
                Step(still, Device(u, SweepPosition(u), Upright), None);
                if (u < latch)
                {
                    continue;
                }

                Assert.That(rig.Katana.TryGetWave(0, out _, out Plane plane, out Vector3 origin, out Vector3 travel,
                    out Vector3 spanAxis, out _, out _, out _, out _, out _), Is.True);
                Assert.That(plane.normal, Is.EqualTo(baseline.Latch.Plane.normal), "update " + u + ": plane");
                Assert.That(plane.distance, Is.EqualTo(baseline.Latch.Plane.distance), "update " + u + ": plane");
                Assert.That(origin, Is.EqualTo(baseline.Latch.Origin), "update " + u + ": origin");
                Assert.That(travel, Is.EqualTo(baseline.Latch.Travel), "update " + u + ": travel axis");
                Assert.That(spanAxis, Is.EqualTo(baseline.Latch.SpanAxis), "update " + u + ": span axis");

                if (u == latch
                    || !rig.Katana.TryGetWaveCandidate(0, out _, out bool evaluated, out bool fromFrozen, out Vector3 guideOrigin,
                        out Vector3 guideDirection, out _, out _, out _, out _, out _, out _)
                    || !evaluated)
                {
                    continue;
                }

                if (fromFrozen)
                {
                    sawClosedGuide = true;
                    continue;
                }

                // While open, the guide is the katana shown now: its emission point and blade axis, onto the plane.
                sawOpenGuide = true;
                Vector3 emitter = rig.Shown.position + (rig.Shown.forward * (BladeLength * 0.5f));
                AssertNear(guideOrigin, plane.ClosestPointOnPlane(emitter), "update " + u + ": open guide origin");
                Vector3 axisOnPlane = Vector3.ProjectOnPlane(rig.Shown.forward, plane.normal).normalized;
                AssertNear(guideDirection, axisOnPlane, "update " + u + ": open guide direction");

                Assert.That(still.Katana.TryGetWaveCandidate(0, out _, out _, out _, out Vector3 stillOrigin,
                    out _, out _, out _, out _, out _, out _, out _), Is.True);
                largestGuideShift = Mathf.Max(largestGuideShift, Vector3.Distance(guideOrigin, stillOrigin));
            }

            Assert.That(sawOpenGuide, Is.True, "an open span was evaluated after the movement");
            Assert.That(sawClosedGuide, Is.True, "and the span closed within the run");
            Assert.That(largestGuideShift, Is.GreaterThan(0.05f), "the movement moved the open guide with the katana shown");
        }

        [Test]
        public void ARecordingReplayedAtAnotherPlacement_IsJudgedAndLatchedTheSame()
        {
            // Recorded the live way while the player moves and turns; replayed into a katana standing elsewhere.
            Rig live = MakeRig(new Vector3(-1f, 0f, 2f), 15f);
            var recorderObject = new GameObject("Slash Diagnostics");
            _made.Add(recorderObject);
            SandboxSlashPoseRecorder recorder = recorderObject.AddComponent<SandboxSlashPoseRecorder>();
            recorder.BeginRecording();
            var liveAccepted = new List<int>();
            int liveLatch = -1;
            Pose liveSpace = default;
            Plane livePlane = default;
            Vector3 liveOrigin = default;
            Vector3 liveTravel = default;
            for (int u = 0; u < SweepUpdates; u++)
            {
                BladePoseSample device = Device(u, SweepPosition(u), Upright);
                SandboxLocomotionCommand command = u % 4 == 1 ? Turn(u % 8 == 1 ? 1 : -1) : Move(0.5f, 1f);
                live.Input.Submit(command, (float)Interval);
                Assert.That(recorder.TryAppendRecordedSample(device, live.Katana.CurrentViewForward), Is.True);
                live.Katana.TryRecordSample(device);
                liveAccepted.Add(live.Katana.AcceptedSampleCount);
                if (liveLatch < 0 && live.Katana.WaveCount > 0)
                {
                    liveLatch = u;
                    liveSpace = live.Katana.TrackingSpacePose;
                    live.Katana.TryGetWave(0, out _, out livePlane, out liveOrigin, out liveTravel, out _, out _, out _, out _, out _, out _);
                }
            }

            Rig replay = MakeRig(new Vector3(4f, 0f, 1f), -70f);
            Assert.That(recorder.TryBeginReplay(0.0), Is.True);
            var replayAccepted = new List<int>();
            int replayLatch = -1;
            Pose replaySpace = default;
            Plane replayPlane = default;
            Vector3 replayOrigin = default;
            Vector3 replayTravel = default;
            for (int u = 0; u < SweepUpdates; u++)
            {
                Assert.That(recorder.TryTakeNextReplaySample(u + 1, out BladePoseSample sample, out Vector3 view), Is.True);
                replay.Katana.TryRecordSample(sample, view);
                replayAccepted.Add(replay.Katana.AcceptedSampleCount);
                if (replayLatch < 0 && replay.Katana.WaveCount > 0)
                {
                    replayLatch = u;
                    replaySpace = replay.Katana.TrackingSpacePose;
                    replay.Katana.TryGetWave(0, out _, out replayPlane, out replayOrigin, out replayTravel, out _, out _, out _, out _, out _, out _);
                }
            }

            Assert.That(liveLatch, Is.GreaterThanOrEqualTo(0), "the recorded swing latched");
            Assert.That(replayAccepted, Is.EqualTo(liveAccepted));
            Assert.That(replayLatch, Is.EqualTo(liveLatch));
            Quaternion liveBack = Quaternion.Inverse(liveSpace.rotation);
            Quaternion replayBack = Quaternion.Inverse(replaySpace.rotation);
            AssertNear(replayBack * replayPlane.normal, liveBack * livePlane.normal, "plane normal in tracking space");
            AssertNear(replayBack * (replayOrigin - replaySpace.position), liveBack * (liveOrigin - liveSpace.position), "origin in tracking space");
            AssertNear(replayBack * replayTravel, liveBack * liveTravel, "travel in tracking space");
        }

        [Test]
        public void TheKatana_IsShownWhereTheTrackingSpacePlacesTheGesturePose()
        {
            Rig rig = MakeRig(new Vector3(2f, 0f, -1f), 90f);
            var device = Device(0, new Vector3(0.1f, 1.2f, 0.3f), Quaternion.Euler(10f, 20f, 0f));

            Assert.That(rig.Katana.TryApplySample(device), Is.True);

            Pose offset = rig.Katana.GripToKatanaOffset;
            Vector3 gesturePosition = device.GripPosition + (device.GripRotation * offset.position);
            Quaternion gestureRotation = device.GripRotation * offset.rotation;
            AssertNear(rig.Shown.position, rig.Offset.TransformPoint(gesturePosition), "shown position");
            Assert.That(Quaternion.Angle(rig.Shown.rotation, rig.Offset.rotation * gestureRotation), Is.LessThan(1e-3f));

            // The view the gesture is judged with is in the same tracking space.
            AssertNear(rig.Katana.CurrentViewForward, Vector3.forward, "view in tracking space");
        }

        [Test]
        public void TheMovement_IsAppliedBeforeTheKatanaJudgesTheFrame()
        {
            int Order(System.Type type) => type.GetCustomAttribute<DefaultExecutionOrder>()?.order ?? 0;
            Assert.That(Order(typeof(SandboxLocomotionInput)), Is.LessThan(Order(typeof(SandboxRightHandKatana))));
        }

        private static void AssertNear(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance),
                what + ": expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
        }
    }
}
