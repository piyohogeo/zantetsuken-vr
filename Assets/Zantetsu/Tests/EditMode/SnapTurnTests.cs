using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Core.MobPlan;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The crowd's input's snap turn (2026-10-20): the trigger (one turn a deflection, two thresholds, the centre
    /// first after the input comes back), the turn about the head taken whole, the continuous turn and the move as
    /// they were, and the katana: a snap is no swing, and a swing with a snap in it is judged and latched as without.
    /// </summary>
    public sealed class SnapTurnTests
    {
        private const float Tolerance = 1e-4f;
        private readonly List<GameObject> _made = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject made in _made) if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
            GamePause.ResetForTest();
        }

        // ---- the trigger -------------------------------------------------------------------------------------------

        private static List<int> Fire(SnapTurnTrigger trigger, params float[] axes)
        {
            var fired = new List<int>();
            foreach (float axis in axes) fired.Add(trigger.Step(axis));
            return fired;
        }

        [Test]
        public void TheTrigger_FiresOnceADeflection_NotWhileHeld_AndAgainAfterTheCentre()
        {
            var trigger = new SnapTurnTrigger();
            Assert.That(Fire(trigger, 0f, 1f, 1f, 1f, 1f, 1f), Is.EqualTo(new[] { 0, 1, 0, 0, 0, 0 }), "held over: one turn, no repeat");
            Assert.That(Fire(trigger, 0.2f, -0.9f, -0.9f), Is.EqualTo(new[] { 0, -1, 0 }), "back to the centre, then left: one turn left");
            Assert.That(Fire(trigger, 0f, 0.7f), Is.EqualTo(new[] { 0, 1 }), "the fire threshold itself fires");
        }

        [Test]
        public void TheTrigger_HeldAtTheStart_TurnsNothingUntilTheCentre()
        {
            var trigger = new SnapTurnTrigger();
            Assert.That(Fire(trigger, 1f, 1f, 1f), Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(Fire(trigger, 0.3f, 1f), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void TheTrigger_WaveringAboutEitherThreshold_FiresOnce()
        {
            var trigger = new SnapTurnTrigger();
            // About the fire threshold, never back to the release one: one turn.
            Assert.That(Fire(trigger, 0f, 0.69f, 0.71f, 0.69f, 0.71f, 0.72f, 0.68f, 0.75f), Is.EqualTo(new[] { 0, 0, 1, 0, 0, 0, 0, 0 }));
            // After a turn, wavering above the release threshold re-arms nothing.
            Assert.That(Fire(trigger, 0.36f, 0.9f, 0.36f, 0.95f, 0.4f, 0.8f), Is.EqualTo(new[] { 0, 0, 0, 0, 0, 0 }));
            // Below it, armed again.
            Assert.That(Fire(trigger, 0.34f, 0.8f), Is.EqualTo(new[] { 0, 1 }));
        }

        [Test]
        public void TheTrigger_AfterRequireCentre_OrAnAxisNotRead_WaitsForTheCentre()
        {
            var trigger = new SnapTurnTrigger();
            Fire(trigger, 0f);
            Assert.That(trigger.Armed, Is.True);
            trigger.RequireCentre();
            Assert.That(Fire(trigger, 1f, 1f), Is.EqualTo(new[] { 0, 0 }));
            Assert.That(Fire(trigger, 0f, 1f), Is.EqualTo(new[] { 0, 1 }));
            Fire(trigger, 0f);
            Assert.That(Fire(trigger, float.NaN, 1f, float.PositiveInfinity, 1f), Is.EqualTo(new[] { 0, 0, 0, 0 }), "not read: no turn, and no centre");
            Assert.That(Fire(trigger, 0f, -1f), Is.EqualTo(new[] { 0, -1 }));
        }

        // ---- the crowd's input -------------------------------------------------------------------------------------

        private sealed class Rig
        {
            public Transform Origin, Offset, Head;
            public PlayerLocomotion Locomotion;
            public MobPlanPlayerInput Input;
        }

        // A map free where x <= the edge, blocked beyond it (clearance 10 / 0).
        private sealed class EdgeMap : WalkableMap
        {
            public float Edge = 1e6f;
            public override float ClearanceAt(float x, float z) => x <= Edge ? 10f : 0f;
            public override bool IsWalkableAt(float x, float z) => x <= Edge;
        }

        // The XR rig as the play scene has it, the head off the rig's origin (leaning, or standing aside in the room), the crowd ready.
        private Rig MakeRig(Vector3 origin = default, float yaw = 0f, Vector3? headLocal = null)
        {
            var root = new GameObject("XR Origin");
            _made.Add(root);
            root.transform.SetPositionAndRotation(origin, Quaternion.Euler(0f, yaw, 0f));
            Transform offset = new GameObject("Camera Offset").transform;
            offset.SetParent(root.transform, false);
            Transform head = new GameObject("Main Camera").transform;
            head.SetParent(offset, false);
            head.localPosition = headLocal ?? new Vector3(0.5f, 1.6f, 0.3f);
            head.localRotation = Quaternion.Euler(10f, 20f, 0f);
            PlayerLocomotion locomotion = root.AddComponent<PlayerLocomotion>();
            locomotion.Configure(null, head,
                new PlayerCapsuleAuthoring(new Vector3(0f, 0.9f, 0f), Vector3.zero, 0.25f, 1.6f),
                new PlayerCapsuleAuthoring(Vector3.zero, Vector3.zero, 0.15f, 0.3f));

            var crowdObject = new GameObject("Crowd");
            _made.Add(crowdObject);
            MobPlanCrowd crowd = crowdObject.AddComponent<MobPlanCrowd>();
            typeof(MobPlanCrowd).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(crowd, true);
            Assume.That(crowd.IsReady, Is.True);
            MobPlanPlayerInput input = crowdObject.AddComponent<MobPlanPlayerInput>();
            input.crowd = crowd;
            input.player = locomotion;
            input.head = head;
            return new Rig { Origin = root.transform, Offset = offset, Head = head, Locomotion = locomotion, Input = input };
        }

        private static float Yaw(Rig rig) => rig.Origin.eulerAngles.y;

        private static void AssertNear(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(Tolerance), what + ": expected " + expected.ToString("F5") + " but was " + actual.ToString("F5"));
        }

        [Test]
        public void ASnap_Of15_30_And45Degrees_LeftAndRight_TurnsAtOnceAboutTheHead_TheHeadStayingWhereItIs()
        {
            foreach (float angle in new[] { 15f, 30f, 45f })
                foreach (int side in new[] { 1, -1 })
                {
                    Rig rig = MakeRig(new Vector3(3f, 0f, -2f), 40f);
                    rig.Input.SnapTurn = true;
                    rig.Input.SnapTurnDegrees = angle;
                    Vector3 head = rig.Head.position;
                    Vector3 headLocal = rig.Head.localPosition;
                    float before = Yaw(rig);
                    rig.Input.Drive(Vector2.zero, 0.011f);
                    rig.Input.Drive(new Vector2(side, 0f), 0.011f);
                    string what = angle + " degrees " + (side > 0 ? "right" : "left");
                    Assert.That(Mathf.DeltaAngle(before, Yaw(rig)), Is.EqualTo(side * angle).Within(1e-3f), what + ": the whole angle, in one frame");
                    AssertNear(rig.Head.position, head, what + ": the head where it was");
                    AssertNear(rig.Head.localPosition, headLocal, what + ": the tracked pose untouched");
                    Assert.That(rig.Origin.position, Is.Not.EqualTo(new Vector3(3f, 0f, -2f)), what + ": the rig moved round the head (not vacuous)");
                    Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1));
                    Assert.That(rig.Input.SnapTurnsInPlace, Is.Zero, what + ": about the head, not in place");
                }
        }

        [Test]
        public void HeldOver_ASnapTurnsOnce_AndAgainOnlyAfterTheStickCameBack()
        {
            Rig rig = MakeRig();
            rig.Input.SnapTurn = true;
            float start = Yaw(rig);
            rig.Input.Drive(Vector2.zero, 0.011f);
            for (int i = 0; i < 60; i++) rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), "held for 60 frames: one turn");
            Assert.That(Mathf.DeltaAngle(start, Yaw(rig)), Is.EqualTo(30f).Within(1e-3f), "the default 30 degrees, and no continuous turn on top");
            rig.Input.Drive(new Vector2(0.1f, 0f), 0.011f);
            rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(2));
            Assert.That(Mathf.DeltaAngle(start, Yaw(rig)), Is.EqualTo(60f).Within(1e-3f));
        }

        [Test]
        public void GivenBackAfterTheMenu_TheFocusBack_TheWayChanged_OrAfterThePause_AStickStillDeflectedTurnsNothing()
        {
            void CentreThenCount(Rig rig, string what, int expected)
            {
                rig.Input.Drive(Vector2.zero, 0.011f);
                rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
                Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(expected), what + ": after the centre, it turns");
            }

            // The menu takes the live input and gives it back (PlayMenu's liveInput off and on).
            Rig menu = MakeRig();
            menu.Input.SnapTurn = true;
            menu.Input.Drive(Vector2.zero, 0.011f);
            menu.Input.liveInput = false;
            menu.Input.Drive(new Vector2(1f, 0f), 0.011f);
            menu.Input.liveInput = true;
            for (int i = 0; i < 5; i++) menu.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(menu.Input.SnapTurnsMade, Is.Zero, "the menu: given back with the stick deflected");
            CentreThenCount(menu, "the menu", 1);

            // The application's focus back, and its pause's end.
            foreach (string message in new[] { "OnApplicationFocus", "OnApplicationPause" })
            {
                Rig focus = MakeRig();
                focus.Input.SnapTurn = true;
                focus.Input.Drive(Vector2.zero, 0.011f);
                typeof(MobPlanPlayerInput).GetMethod(message, BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(focus.Input, new object[] { message == "OnApplicationFocus" });
                for (int i = 0; i < 5; i++) focus.Input.Drive(new Vector2(1f, 0f), 0.011f);
                Assert.That(focus.Input.SnapTurnsMade, Is.Zero, message);
                CentreThenCount(focus, message, 1);
            }

            // The way changed from continuous to snap while the stick is deflected.
            Rig changed = MakeRig();
            changed.Input.Drive(Vector2.zero, 0.011f);
            changed.Input.Drive(new Vector2(1f, 0f), 0.011f);
            changed.Input.SnapTurn = true;
            float yaw = Yaw(changed);
            for (int i = 0; i < 5; i++) changed.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(changed.Input.SnapTurnsMade, Is.Zero, "changed while deflected");
            Assert.That(Mathf.DeltaAngle(yaw, Yaw(changed)), Is.EqualTo(0f).Within(1e-3f), "and no continuous turn either");
            CentreThenCount(changed, "changed", 1);

            // The game paused while the stick is deflected, then going on.
            Rig paused = MakeRig();
            paused.Input.SnapTurn = true;
            paused.Input.Drive(Vector2.zero, 0.011f);
            GamePause.SetManual(true);
            paused.Input.Drive(new Vector2(1f, 0f), 0.011f);
            GamePause.SetManual(false);
            for (int i = 0; i < 5; i++) paused.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(paused.Input.SnapTurnsMade, Is.Zero, "paused while deflected");
            CentreThenCount(paused, "paused", 1);
        }

        [Test]
        public void AStickHeldOverAFrameNotRead_TurnsNoMore_UntilACentreIsRead_ThenTurnsAgain()
        {
            // TL review 2026-10-20: deflect -> a frame the controller is not read -> read again, still deflected -> centre -> deflect.
            Rig rig = MakeRig();
            rig.Input.SnapTurn = true;
            rig.Input.Drive(Vector2.zero, 0.011f);
            rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), "the deflection's turn");
            float yaw = Yaw(rig);
            Vector3 position = rig.Origin.position;
            rig.Input.Drive(Vector2.zero, 0.011f, read: false);
            Assert.That(Yaw(rig), Is.EqualTo(yaw).Within(1e-3f), "not read: nothing turns");
            AssertNear(rig.Origin.position, position, "and nothing moves");
            for (int i = 0; i < 10; i++) rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), "read again, still deflected: no second turn");
            rig.Input.Drive(new Vector2(0.9f, 0f), 0.011f, read: false);
            rig.Input.Drive(new Vector2(0.2f, 0f), 0.011f, read: false);
            rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), "a centre not read is no centre");
            rig.Input.Drive(new Vector2(0.1f, 0f), 0.011f);
            rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(2), "a centre read, then the deflection: the next turn");
            Assert.That(Mathf.DeltaAngle(0f, Yaw(rig)), Is.EqualTo(60f).Within(1e-3f));
        }

        [Test]
        public void TheStick_IsReadOnlyWhereItsValueCame_AndANonFiniteValueIsNotRead_EachWithItsSource()
        {
            const MobPlanPlayerInput.StickSource Controller = MobPlanPlayerInput.StickSource.Controller,
                Gamepad = MobPlanPlayerInput.StickSource.Gamepad, Keyboard = MobPlanPlayerInput.StickSource.Keyboard, None = MobPlanPlayerInput.StickSource.None;
            Vector2 stick = new Vector2(0.8f, 0.1f), pad = new Vector2(-0.6f, 0.4f), zero = Vector2.zero;
            Assert.That(MobPlanPlayerInput.ReadStick(true, true, stick, true, pad, true, zero, out Vector2 c, out var s), Is.True);
            Assert.That((c, s), Is.EqualTo((stick, Controller)), "the controller's stick");
            Assert.That(MobPlanPlayerInput.ReadStick(true, false, zero, true, pad, true, zero, out c, out s), Is.False, "a controller whose stick did not come: not read (not the gamepad's, not the centre)");
            Assert.That((c, s), Is.EqualTo((zero, None)));
            Assert.That(MobPlanPlayerInput.ReadStick(true, true, new Vector2(float.NaN, 0f), false, zero, false, zero, out _, out _), Is.False, "NaN: not read");
            Assert.That(MobPlanPlayerInput.ReadStick(true, true, new Vector2(0f, float.PositiveInfinity), false, zero, false, zero, out _, out _), Is.False, "infinite: not read");
            Assert.That(MobPlanPlayerInput.ReadStick(false, false, zero, true, pad, true, zero, out c, out s), Is.True);
            Assert.That((c, s), Is.EqualTo((pad, Gamepad)), "no controller: the gamepad's");
            Assert.That(MobPlanPlayerInput.ReadStick(false, false, zero, false, zero, true, zero, out c, out s), Is.True, "a keyboard alone, nothing pressed: the centre");
            Assert.That((c, s), Is.EqualTo((zero, Keyboard)));
            Assert.That(MobPlanPlayerInput.ReadStick(false, false, zero, false, zero, false, zero, out _, out s), Is.False, "nothing to read");
            Assert.That(s, Is.EqualTo(None));
            Assert.That(MobPlanPlayerInput.ReadStick(true, true, zero, false, zero, true, new Vector2(1f, 0f), out c, out s), Is.True, "keys pressed are read");
            Assert.That((c, s), Is.EqualTo((new Vector2(1f, 0f), Keyboard)), "from the keyboard, though a controller is there");
        }

        [Test]
        public void TheControllerLostAMoment_AnotherSourcesCentreInBetween_AndTheControllerBackStillDeflected_TurnsNoMore()
        {
            // TL review 2026-10-20: deflect -> the controller gone -> the keyboard's (or the gamepad's) centre -> the
            // controller back, still deflected -> its own centre -> deflect.
            foreach (MobPlanPlayerInput.StickSource other in new[] { MobPlanPlayerInput.StickSource.Keyboard, MobPlanPlayerInput.StickSource.Gamepad })
            {
                Rig rig = MakeRig();
                rig.Input.SnapTurn = true;
                var controller = MobPlanPlayerInput.StickSource.Controller;
                rig.Input.Drive(Vector2.zero, 0.011f, true, controller);
                rig.Input.Drive(new Vector2(1f, 0f), 0.011f, true, controller);
                Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), other + ": the deflection's turn");
                for (int i = 0; i < 5; i++) rig.Input.Drive(Vector2.zero, 0.011f, true, other);
                for (int i = 0; i < 10; i++) rig.Input.Drive(new Vector2(1f, 0f), 0.011f, true, controller);
                Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), other + ": back still deflected: no second turn");
                rig.Input.Drive(new Vector2(0.1f, 0f), 0.011f, true, controller);
                rig.Input.Drive(new Vector2(1f, 0f), 0.011f, true, controller);
                Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(2), other + ": the controller's own centre, then the deflection");
                Assert.That(Mathf.DeltaAngle(0f, Yaw(rig)), Is.EqualTo(60f).Within(1e-3f), other.ToString());
            }

            // One source alone keeps its snaps: a keyboard without a controller (keys centre, a key, its release, the key again).
            Rig keys = MakeRig();
            keys.Input.SnapTurn = true;
            var keyboard = MobPlanPlayerInput.StickSource.Keyboard;
            keys.Input.Drive(Vector2.zero, 0.011f, true, keyboard);
            keys.Input.Drive(new Vector2(1f, 0f), 0.011f, true, keyboard);
            keys.Input.Drive(Vector2.zero, 0.011f, true, keyboard);
            keys.Input.Drive(new Vector2(-1f, 0f), 0.011f, true, keyboard);
            Assert.That(keys.Input.SnapTurnsMade, Is.EqualTo(2), "the keyboard alone snap-turns");
        }

        [Test]
        public void Continuous_TheTurnAndTheMove_AreAsBefore_AndAScriptsStickTurnsContinuouslyEitherWay()
        {
            Rig rig = MakeRig();
            Vector3 origin = rig.Origin.position;
            rig.Input.Drive(new Vector2(1f, 0f), 0.1f);
            Assert.That(Mathf.DeltaAngle(0f, Yaw(rig)), Is.EqualTo(12f).Within(1e-3f), "120 degrees a second for 0.1 s");
            AssertNear(rig.Origin.position, origin, "turned about the rig's own origin, as before");

            Vector3 facing = MobPlanPlayerInput.HorizontalFacing(rig.Head.forward, rig.Origin.forward);
            rig.Input.Drive(new Vector2(0f, 1f), 0.1f);
            AssertNear(rig.Origin.position, origin + facing * (rig.Input.speed * 0.1f), "the move along the head's facing");

            Rig snapping = MakeRig();
            snapping.Input.SnapTurn = true;
            snapping.Input.SubmitAlongRig(0f, 1f, 0.1f);
            Assert.That(Mathf.DeltaAngle(0f, Yaw(snapping)), Is.EqualTo(12f).Within(1e-3f), "a script's stick: continuous whatever the live way");
            Assert.That(snapping.Input.SnapTurnsMade, Is.Zero);
        }

        [Test]
        public void ASnapWithAMove_TurnsFirstAboutTheHead_ThenMovesAlongTheHeadsFacingAsItNowStands()
        {
            Rig rig = MakeRig();
            rig.Input.SnapTurn = true;
            rig.Input.Drive(Vector2.zero, 0.011f);
            Vector3 head = rig.Head.position;
            Vector3 turnedFacing = MobPlanPlayerInput.HorizontalFacing(Quaternion.Euler(0f, 30f, 0f) * rig.Head.forward, Vector3.forward);
            rig.Input.Drive(new Vector2(1f, 1f), 0.1f);
            Assert.That(Mathf.DeltaAngle(0f, Yaw(rig)), Is.EqualTo(30f).Within(1e-3f));
            AssertNear(rig.Head.position, head + turnedFacing * (rig.Input.speed * 0.1f), "turned about the head, then moved along the new facing");
        }

        [Test]
        public void OnTheWalkableMap_ASnapAboutTheHeadTheMapWouldStopShort_IsMadeInPlace_AboutTheRigsOrigin()
        {
            // The plain request turns the rig even where the map stops its position: the turn about the head would carry the head.
            Rig plain = MakeRig(Vector3.zero, 0f, new Vector3(0.5f, 1.6f, 0f));
            var map = new EdgeMap { Edge = 0.05f };
            plain.Locomotion.ConfigureMap(map, Vector3.zero);
            Pose candidate = PlayerLocomotion.Candidate(plain.Locomotion.RootPose, plain.Head.position, Vector3.zero, 45f);
            Assume.That(candidate.position.x, Is.GreaterThan(map.Edge), "the turn about the head takes the rig over the edge");
            Vector3 head = plain.Head.position;
            plain.Locomotion.TryRequest(candidate);
            Assert.That(Vector3.Distance(plain.Head.position, head), Is.GreaterThan(0.1f), "the plain request: turned in place of the rig, the head carried");

            // The snap: the turn about the head is refused whole, and the turn is made in place, about the rig's
            // origin, as the continuous turn is (TL 2026-10-20 after the device: at the wall it must still turn).
            Rig rig = MakeRig(Vector3.zero, 0f, new Vector3(0.5f, 1.6f, 0f));
            rig.Locomotion.ConfigureMap(new EdgeMap { Edge = 0.05f }, Vector3.zero);
            rig.Input.SnapTurn = true;
            rig.Input.SnapTurnDegrees = 45f;
            head = rig.Head.position;
            Vector3 origin = rig.Origin.position;
            rig.Input.Drive(Vector2.zero, 0.011f);
            rig.Input.Drive(new Vector2(1f, 0f), 0.011f);
            Assert.That(rig.Input.SnapTurnsRefused, Is.EqualTo(1), "about the head: refused");
            Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), "made all the same");
            Assert.That(rig.Input.SnapTurnsInPlace, Is.EqualTo(1), "in place");
            Assert.That(Mathf.DeltaAngle(0f, Yaw(rig)), Is.EqualTo(45f).Within(1e-3f), "the whole angle");
            AssertNear(rig.Origin.position, origin, "the rig's origin where it was");
            Vector3 expectedHead = origin + Quaternion.Euler(0f, 45f, 0f) * (head - origin);
            AssertNear(rig.Head.position, expectedHead, "the head carried round the origin, that time only");
            Assert.That(Vector3.Distance(rig.Head.position, head), Is.GreaterThan(0.3f), "by as much as it stands off the origin (not vacuous)");

            // Where the map lets the rig round the head, it turns and the head stays.
            Rig free = MakeRig(Vector3.zero, 0f, new Vector3(0.5f, 1.6f, 0f));
            free.Locomotion.ConfigureMap(new EdgeMap(), Vector3.zero);
            free.Input.SnapTurn = true;
            head = free.Head.position;
            free.Input.Drive(Vector2.zero, 0.011f);
            free.Input.Drive(new Vector2(-1f, 0f), 0.011f);
            Assert.That(Mathf.DeltaAngle(0f, Yaw(free)), Is.EqualTo(-30f).Within(1e-3f));
            AssertNear(free.Head.position, head, "on the map: the head where it was");
        }

        // ---- the katana --------------------------------------------------------------------------------------------

        private const double Interval = 0.011;
        private static readonly Vector3 EdgeStep = new Vector3(0f, -0.12f, 0f);
        private const int SweepBefore = 10, SweepLength = 8, SweepUpdates = 58;

        private static Quaternion Upright => Quaternion.Inverse(Quaternion.Euler(-15f, 0f, 0f));

        private static BladePoseSample Device(int update, Vector3 position, Quaternion rotation) =>
            new BladePoseSample(update + 1, Interval * (update + 1), position, rotation, BladeTrackingState.Position | BladeTrackingState.Rotation);

        private static Vector3 SweepPosition(int update)
        {
            int steps = Mathf.Clamp(update - SweepBefore + 1, 0, SweepLength);
            return new Vector3(0f, 1.4f, 0.3f) + (EdgeStep * steps);
        }

        private (Rig rig, SandboxRightHandKatana katana, Transform shown) KatanaRig()
        {
            Rig rig = MakeRig(new Vector3(2f, 0f, -1f), 25f);
            rig.Input.SnapTurn = true;
            var katanaObject = new GameObject("Sandbox Katana Rig");
            _made.Add(katanaObject);
            var shown = new GameObject("Katana");
            _made.Add(shown);
            SandboxRightHandKatana katana = katanaObject.AddComponent<SandboxRightHandKatana>();
            katana.Katana = shown.transform;
            var serialized = new SerializedObject(katana);
            serialized.FindProperty("trackingSpace").objectReferenceValue = rig.Offset;
            serialized.FindProperty("viewForwardReference").objectReferenceValue = rig.Head;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return (rig, katana, shown.transform);
        }

        // The live order: the crowd's input (-100) moves and turns the rig, then the katana judges the frame's pose.
        private static void Step(Rig rig, SandboxRightHandKatana katana, in BladePoseSample device, float stickX)
        {
            rig.Input.Drive(new Vector2(stickX, 0f), (float)Interval);
            katana.TryRecordSample(device);
        }

        [Test]
        public void AStillHand_UnderSnapTurns_IsNeitherAcceptedNorLatched_ThoughTheKatanaShownJumps()
        {
            var grips = new[] { Quaternion.Euler(0f, 0f, -90f) * Upright, Quaternion.Euler(0f, 0f, 90f) * Upright, Upright };
            foreach (float angle in new[] { 15f, 30f, 45f })
                foreach (Quaternion grip in grips)
                {
                    var (rig, katana, shown) = KatanaRig();
                    rig.Input.SnapTurnDegrees = angle;
                    float largestJump = 0f;
                    Vector3 last = default;
                    for (int u = 0; u < 120; u++)
                    {
                        // A deflection every 10 updates, right then left in turn, back to the centre between.
                        float x = u % 10 == 5 ? (u % 20 == 5 ? 1f : -1f) : 0f;
                        Step(rig, katana, Device(u, new Vector3(0.25f, 1.2f, 0.35f), grip), x);
                        Assert.That(katana.AcceptedSampleCount, Is.EqualTo(0), angle + " degrees, update " + u + ": no sample taken");
                        Assert.That(katana.WaveCount, Is.EqualTo(0), angle + " degrees, update " + u + ": nothing latched");
                        if (u > 0) largestJump = Mathf.Max(largestJump, Vector3.Distance(shown.position, last));
                        last = shown.position;
                    }

                    Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(12), "the turns were made");
                    // The katana stands some 0.27 m off the head here: 0.07 m at 15 degrees, 0.2 m at 45.
                    Assert.That(largestJump, Is.GreaterThan(0.05f * angle / 15f), angle + " degrees: the katana shown jumped with the turn (not vacuous)");
                }
        }

        [Test]
        public void TheSameSwing_WithASnapTurnInIt_IsAcceptedAndLatchedAsWithout_AtTheSamePlaceInTrackingSpace()
        {
            (List<int> accepted, int latch, Pose space, Plane plane, Vector3 origin, int waves) Sweep(int snapAt)
            {
                var (rig, katana, _) = KatanaRig();
                var accepted = new List<int>();
                int latch = -1;
                Pose space = default;
                Plane plane = default;
                Vector3 origin = default;
                for (int u = 0; u < SweepUpdates; u++)
                {
                    Step(rig, katana, Device(u, SweepPosition(u), Upright), u == snapAt ? 1f : 0f);
                    accepted.Add(katana.AcceptedSampleCount);
                    if (latch < 0 && katana.WaveCount > 0)
                    {
                        latch = u;
                        space = katana.TrackingSpacePose;
                        katana.TryGetWave(0, out _, out plane, out origin, out _, out _, out _, out _, out _, out _, out _);
                    }
                }

                if (snapAt >= 0) Assert.That(rig.Input.SnapTurnsMade, Is.EqualTo(1), "snap at " + snapAt + ": turned");
                return (accepted, latch, space, plane, origin, katana.WaveCount);
            }

            var baseline = Sweep(-1);
            Assert.That(baseline.latch, Is.GreaterThanOrEqualTo(0), "the swing latches");
            for (int at = SweepBefore - 1; at < SweepBefore + SweepLength; at++)
            {
                var snapped = Sweep(at);
                Assert.That(snapped.accepted, Is.EqualTo(baseline.accepted), "snap at " + at + ": accepted samples, update by update");
                Assert.That(snapped.latch, Is.EqualTo(baseline.latch), "snap at " + at + ": latch update");
                Assert.That(snapped.waves, Is.EqualTo(baseline.waves), "snap at " + at);
                Quaternion back = Quaternion.Inverse(snapped.space.rotation), baseBack = Quaternion.Inverse(baseline.space.rotation);
                Assert.That(Vector3.Angle(back * snapped.plane.normal, baseBack * baseline.plane.normal), Is.LessThan(0.01f), "snap at " + at + ": plane in tracking space");
                AssertNear(back * (snapped.origin - snapped.space.position), baseBack * (baseline.origin - baseline.space.position), "snap at " + at + ": origin in tracking space");
            }
        }
    }
}
