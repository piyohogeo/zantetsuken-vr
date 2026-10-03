using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Where a cut side's body is given the mass properties computed for it (<see cref="MassPropertiesBoundary"/>, TL
    /// 2026-10-03): below the engine's mass floor (1e-7 kg) the inertia follows the mass the body holds; at and above it
    /// the body is given exactly what the direct writes gave it. Anisotropic principal moments on rotated axes, a centre off
    /// the origin, read back at once and after a physics step; applied again without compounding; through the side's own
    /// application (built out of the scene, published in it, handed off). And the refusals: every check that writes
    /// nothing, the two that come after the mass write, the side's flags and motion put back, the begun application
    /// reverted -- each leaving the body exactly as it was.
    /// </summary>
    public class MassPropertiesBoundaryPlayModeTests
    {
        private const float Floor = 1e-7f;
        private const double BelowFloor = 6.3923e-11;   // w5 piece 231's side mass (inferred from its inertia)
        private static readonly Vector3 Centre = new Vector3(0.012f, -0.034f, 0.0056f);
        private static readonly Quaternion Axes = Quaternion.Euler(23f, -61f, 137f);
        private static readonly Vector3 Moments = new Vector3(1.3f, 7.9f, 4.4f);   // per kg m^2 of mass; scaled by the mass asked

        private SimulationMode _mode;
        private Vector3 _gravity;
        private readonly List<GameObject> _objects = new List<GameObject>();

        [SetUp]
        public void SetUp()
        {
            _mode = Physics.simulationMode;
            _gravity = Physics.gravity;
            Physics.simulationMode = SimulationMode.Script;
        }

        [TearDown]
        public void TearDown()
        {
            MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
            foreach (GameObject o in _objects) Object.Destroy(o);
            _objects.Clear();
            Physics.simulationMode = _mode;
            Physics.gravity = _gravity;
        }

        private Rigidbody NewBody(string name, Vector3 position, bool active = true)
        {
            var root = new GameObject(name);
            root.SetActive(active);
            root.transform.SetPositionAndRotation(position, Quaternion.Euler(10f, 20f, 30f));
            _objects.Add(root);
            var body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.isKinematic = false;
            return body;
        }

        // What PhysicsOwnerBuild wrote before the boundary, in its order.
        private static void WriteDirectly(Rigidbody body, double mass, Vector3 centre, Vector3 inertia, Quaternion rotation)
        {
            body.mass = (float)mass;
            body.centerOfMass = centre;
            body.inertiaTensor = inertia;
            body.inertiaTensorRotation = rotation;
        }

        private static MassPropertiesBoundary.Applied Apply(Rigidbody body, double mass, Vector3 centre, Vector3 inertia, Quaternion axes)
        {
            Assert.That(MassPropertiesBoundary.TryApply(body, mass, centre, inertia, axes, out MassPropertiesBoundary.Applied applied, out MassPropertiesRefusal refusal), Is.True, "applied: " + refusal);
            Assert.That(refusal, Is.EqualTo(MassPropertiesRefusal.None));
            return applied;
        }

        private static void AssertRelative(Vector3 actual, Vector3 expected, double tolerance, string what)
        {
            for (int i = 0; i < 3; i++)
                Assert.That(Mathf.Abs(actual[i] - expected[i]), Is.LessThanOrEqualTo(tolerance * Mathf.Abs(expected[i])), what + " [" + i + "]: " + actual.ToString("E6") + " vs " + expected.ToString("E6"));
        }

        private static void AssertHolds(Rigidbody body, float mass, Vector3 inertia, string what)
        {
            Assert.That(body.mass, Is.EqualTo(mass), what + ": mass");
            AssertRelative(body.inertiaTensor, inertia, 1e-6, what + ": inertia");
            Assert.That(Quaternion.Angle(body.inertiaTensorRotation, Axes), Is.LessThan(0.01f), what + ": principal axes");
            Assert.That(body.centerOfMass, Is.EqualTo(Centre), what + ": centre of mass");
        }

        /// <summary>Everything of a body a refused application must leave as it was, read exactly.</summary>
        private readonly struct BodyState
        {
            private readonly float _mass;
            private readonly Vector3 _centre, _inertia, _linear, _angular;
            private readonly Quaternion _axes;
            private readonly bool _automaticCentre, _automaticInertia, _kinematic;

            internal BodyState(Rigidbody b)
            {
                _mass = b.mass; _centre = b.centerOfMass; _inertia = b.inertiaTensor; _axes = b.inertiaTensorRotation;
                _linear = b.linearVelocity; _angular = b.angularVelocity;
                _automaticCentre = b.automaticCenterOfMass; _automaticInertia = b.automaticInertiaTensor; _kinematic = b.isKinematic;
            }

            internal void AssertSame(Rigidbody b, string what)
            {
                Assert.That(b.mass, Is.EqualTo(_mass), what + ": mass");
                Assert.That(b.centerOfMass, Is.EqualTo(_centre), what + ": centre of mass");
                Assert.That(b.inertiaTensor, Is.EqualTo(_inertia), what + ": inertia");
                Assert.That(b.inertiaTensorRotation, Is.EqualTo(_axes), what + ": principal axes");
                Assert.That(b.automaticCenterOfMass, Is.EqualTo(_automaticCentre), what + ": automatic centre flag");
                Assert.That(b.automaticInertiaTensor, Is.EqualTo(_automaticInertia), what + ": automatic inertia flag");
                Assert.That(b.isKinematic, Is.EqualTo(_kinematic), what + ": kinematic flag");
                Assert.That(b.linearVelocity, Is.EqualTo(_linear), what + ": velocity");
                Assert.That(b.angularVelocity, Is.EqualTo(_angular), what + ": angular velocity");
            }
        }

        // A body holding known values, moving and turning, in the scene.
        private Rigidbody NewMovingBody(string name, Vector3 position)
        {
            Rigidbody body = NewBody(name, position);
            WriteDirectly(body, 3.0, new Vector3(0.1f, -0.2f, 0.05f), new Vector3(0.7f, 0.4f, 0.9f), Quaternion.Euler(5f, 15f, -25f));
            body.linearVelocity = new Vector3(1.5f, -0.5f, 0.25f);
            body.angularVelocity = new Vector3(0.3f, -0.7f, 0.2f);
            return body;
        }

        [UnityTest]
        public IEnumerator BelowTheFloor_TheInertiaFollowsTheMassTheBodyHolds_AtOnceAndAfterAStep()
        {
            yield return null;
            Rigidbody body = NewBody("below", new Vector3(0f, 100f, 0f));
            Rigidbody direct = NewBody("below, direct", new Vector3(10f, 100f, 0f));
            Vector3 asked = Moments * (float)BelowFloor;
            MassPropertiesBoundary.Applied applied = Apply(body, BelowFloor, Centre, asked, Axes);
            WriteDirectly(direct, BelowFloor, Centre, asked, Axes);

            double scale = Floor / BelowFloor;
            Assert.That(applied.effectiveMass, Is.EqualTo(Floor), "the engine keeps its floor");
            Assert.That(applied.inertiaScale, Is.EqualTo(scale), "effective / asked (a record)");
            Assert.That(applied.Adjusted, Is.True);
            Assert.That(applied.askedMass, Is.EqualTo(BelowFloor));
            Assert.That(applied.askedInertia, Is.EqualTo(asked));
            AssertRelative(applied.appliedInertia, Moments * Floor, 1e-6, "the inertia written is the shape's at the floor");
            AssertHolds(body, Floor, Moments * Floor, "at once");
            // The direct writes, for the record: the floor's mass with the inertia of the side's own mass.
            Assert.That(direct.mass, Is.EqualTo(Floor));
            AssertRelative(direct.inertiaTensor, asked, 1e-6, "direct: the inertia as asked");

            Physics.Simulate(1f / 45f);
            AssertHolds(body, Floor, Moments * Floor, "after a step");
            AssertRelative(body.inertiaTensor / body.mass, asked / (float)BelowFloor, 1e-5, "inertia / mass");
        }

        [UnityTest]
        public IEnumerator AtAndAboveTheFloor_TheBodyIsGivenExactlyWhatTheDirectWritesGave_AtOnceAndAfterAStep()
        {
            yield return null;
            int n = 0;
            foreach (double mass in new[] { 1e-7, 2e-7, 0.0203, 50.0 })
            {
                n++;
                Rigidbody body = NewBody("mass " + mass, new Vector3(0f, 100f, n * 10f));
                Rigidbody direct = NewBody("mass " + mass + ", direct", new Vector3(10f, 100f, n * 10f));
                Vector3 asked = Moments * (float)mass;
                MassPropertiesBoundary.Applied applied = Apply(body, mass, Centre, asked, Axes);
                WriteDirectly(direct, mass, Centre, asked, Axes);
                Assert.That(applied.inertiaScale, Is.EqualTo(1.0), mass + ": no scale");
                Assert.That(applied.Adjusted, Is.False, mass + "");
                Assert.That(applied.effectiveMass, Is.EqualTo((float)mass), mass + ": held as asked");
                Assert.That(applied.appliedInertia, Is.EqualTo(asked), mass + ": written as asked");
                for (int pass = 0; pass < 2; pass++)
                {
                    string when = mass + (pass == 0 ? " at once" : " after a step");
                    Assert.That(body.mass, Is.EqualTo(direct.mass), when + ": mass");
                    Assert.That(body.inertiaTensor, Is.EqualTo(direct.inertiaTensor), when + ": inertia");
                    Assert.That(body.inertiaTensorRotation, Is.EqualTo(direct.inertiaTensorRotation), when + ": axes");
                    Assert.That(body.centerOfMass, Is.EqualTo(direct.centerOfMass), when + ": centre");
                    AssertHolds(body, (float)mass, asked, when);
                    Physics.Simulate(1f / 45f);
                }
            }
        }

        [UnityTest]
        public IEnumerator AboveTheFloor_TheBodyTurnsExactlyAsWithTheDirectWrites()
        {
            yield return null;
            const double mass = 0.0203;
            Rigidbody body = NewBody("turning", new Vector3(0f, 100f, 0f));
            Rigidbody direct = NewBody("turning, direct", new Vector3(10f, 100f, 0f));
            Apply(body, mass, Centre, Moments * (float)mass, Axes);
            WriteDirectly(direct, mass, Centre, Moments * (float)mass, Axes);
            var w = new Vector3(3f, -5f, 7f);
            body.angularVelocity = w;
            direct.angularVelocity = w;
            for (int i = 0; i < 30; i++)
            {
                Physics.Simulate(1f / 45f);
                Assert.That(body.angularVelocity, Is.EqualTo(direct.angularVelocity), "step " + i + ": angular velocity");
                Assert.That(body.rotation, Is.EqualTo(direct.rotation), "step " + i + ": rotation");
            }
        }

        [UnityTest]
        public IEnumerator AppliedAgain_TheScaleIsWorkedOutFromTheValuesAsked_NeverCompounded_NoResidue()
        {
            yield return null;
            Rigidbody body = NewBody("again", new Vector3(0f, 100f, 0f));
            Vector3 belowAsked = Moments * (float)BelowFloor;
            MassPropertiesBoundary.Applied first = Apply(body, BelowFloor, Centre, belowAsked, Axes);
            Vector3 firstRead = body.inertiaTensor;
            for (int i = 2; i <= 3; i++)
            {
                MassPropertiesBoundary.Applied again = Apply(body, BelowFloor, Centre, belowAsked, Axes);
                Assert.That(again.inertiaScale, Is.EqualTo(first.inertiaScale), "application " + i + ": the same scale");
                Assert.That(again.appliedInertia, Is.EqualTo(first.appliedInertia), "application " + i);
                Assert.That(body.inertiaTensor, Is.EqualTo(firstRead), "application " + i + ": the body reads the same");
                Physics.Simulate(1f / 45f);
                Assert.That(body.inertiaTensor, Is.EqualTo(firstRead), "application " + i + ", after a step");
            }

            const double above = 0.0203;
            var aboveAsked = new Vector3(2.1e-4f, 3.7e-5f, 1.9e-4f);
            MassPropertiesBoundary.Applied heavy = Apply(body, above, Centre, aboveAsked, Axes);
            Assert.That(heavy.inertiaScale, Is.EqualTo(1.0), "a mass the body holds after one it did not: no scale left over");
            Assert.That(body.mass, Is.EqualTo((float)above));
            Assert.That(body.inertiaTensor, Is.EqualTo(aboveAsked), "the inertia exactly as asked");

            Apply(body, BelowFloor, Centre, belowAsked, Axes);
            Assert.That(body.mass, Is.EqualTo(Floor));
            Assert.That(body.inertiaTensor, Is.EqualTo(firstRead), "below again: as the first time");
        }

        [UnityTest]
        public IEnumerator ThroughTheSide_BuiltOutOfTheScene_PublishedInIt_HandedOff_TheSidesValuesStayItsOwn()
        {
            yield return null;
            Rigidbody body = NewBody("side", new Vector3(0f, 100f, 0f), active: false);
            var frame = new GameObject("shape frame");
            frame.transform.SetParent(body.transform, false);
            var side = new PhysicsOwnerSide(true, body.gameObject, frame, body)
            {
                Mass = BelowFloor,
                CenterOfMass = Centre,
                InertiaTensor = Moments * (float)BelowFloor,
                InertiaRotation = Axes,
            };
            float3 askedInertia = side.InertiaTensor;
            Assert.That(side.EffectiveMass, Is.EqualTo(0f), "before any application");
            Assert.That(side.InertiaScale, Is.EqualTo(1.0));

            // The separation impulse: J = k x the side's Mass (the publications), a first velocity of J / Mass (Reposition).
            const float k = 1.5f;
            side.Reposition(PhysicsOwnerPlacement.Identity, PhysicsOwnerMotion.AtRest, new float3(0f, 1f, 0f), (float)(k * side.Mass));
            Assert.That(side.LinearVelocity.y, Is.EqualTo(k).Within(1e-5f), "the first velocity is k, the side's mass asked on both sides of it");

            Assert.That(side.TryApplyToBody(out MassPropertiesRefusal _), Is.True, "the build's, out of the scene");
            TestContext.Out.WriteLine("out of the scene: mass " + body.mass.ToString("E4") + ", inertia " + body.inertiaTensor.ToString("E3") + ", side effective " + side.EffectiveMass.ToString("E4") + " scale " + side.InertiaScale.ToString("E6"));

            body.gameObject.SetActive(true);
            Assert.That(side.TryApplyToBody(out MassPropertiesRefusal _), Is.True, "the publication's, in the scene");
            for (int pass = 0; pass < 2; pass++)
            {
                string when = pass == 0 ? "published" : "applied again";
                Assert.That(side.Mass, Is.EqualTo(BelowFloor), when + ": the side's own mass");
                Assert.That(side.InertiaTensor, Is.EqualTo(askedInertia), when + ": the side's own inertia");
                Assert.That(side.EffectiveMass, Is.EqualTo(Floor), when + ": what the body holds");
                Assert.That(side.InertiaScale, Is.EqualTo(Floor / BelowFloor), when + ": the scale, not compounded");
                AssertRelative(side.AppliedInertiaTensor, Moments * Floor, 1e-6, when + ": the inertia given");
                AssertHolds(body, Floor, Moments * Floor, when);
                Assert.That(body.linearVelocity.y, Is.EqualTo(k).Within(1e-5f), when + ": the first velocity");
                Assert.That(side.LastRefusal, Is.EqualTo(MassPropertiesRefusal.None));
                Assert.That(side.TryApplyMassAndMotionToBody(out MassPropertiesRefusal _), Is.True);
            }

            // The handoff's two halves on the same body: begin (the mass only), then commit.
            float3 handedInertia = Moments * (float)(2.0 * BelowFloor);
            Assert.That(side.TryBeginMassProperties(2.0 * BelowFloor, Centre, handedInertia, Axes, out MassPropertiesBoundary.Pending pending, out MassPropertiesRefusal _), Is.True);
            Assert.That(side.Mass, Is.EqualTo(BelowFloor), "the side's records are not changed by the begin");
            side.CommitMassAndMotion(ref pending, 2.0 * BelowFloor, Centre, handedInertia, Axes, new float3(0f, 2f, 0f), float3.zero);
            Assert.That(side.Mass, Is.EqualTo(2.0 * BelowFloor), "the side takes the final values");
            AssertHolds(body, Floor, Moments * Floor, "handed off: the inertia scaled from the new values, not compounded");
            Assert.That(body.linearVelocity.y, Is.EqualTo(2f), "the motion written back");

            Physics.Simulate(1f / 45f);
            AssertHolds(body, Floor, Moments * Floor, "after a step");
        }

        [UnityTest]
        public IEnumerator RefusedBeforeAnyWrite_EachCheck_LeavesTheBodyExactlyAsItWas()
        {
            yield return null;
            Vector3 inertia = Moments * 0.02f;
            var cases = new List<(string name, double mass, Vector3 centre, Vector3 inertia, Quaternion axes, MassPropertiesRefusal expected)>
            {
                ("mass NaN", double.NaN, Centre, inertia, Axes, MassPropertiesRefusal.MassNotPositiveFinite),
                ("mass +inf", double.PositiveInfinity, Centre, inertia, Axes, MassPropertiesRefusal.MassNotPositiveFinite),
                ("mass 0", 0.0, Centre, inertia, Axes, MassPropertiesRefusal.MassNotPositiveFinite),
                ("mass negative", -1.0, Centre, inertia, Axes, MassPropertiesRefusal.MassNotPositiveFinite),
                ("mass 1e-50 (float 0)", 1e-50, Centre, inertia, Axes, MassPropertiesRefusal.MassNotRepresentable),
                ("mass 1e39 (float inf)", 1e39, Centre, inertia, Axes, MassPropertiesRefusal.MassNotRepresentable),
                ("inertia 0", 0.02, Centre, new Vector3(0f, 1f, 1f), Axes, MassPropertiesRefusal.InertiaNotPositiveFinite),
                ("inertia negative", 0.02, Centre, new Vector3(1f, -1f, 1f), Axes, MassPropertiesRefusal.InertiaNotPositiveFinite),
                ("inertia NaN", 0.02, Centre, new Vector3(1f, 1f, float.NaN), Axes, MassPropertiesRefusal.InertiaNotPositiveFinite),
                ("inertia inf", 0.02, Centre, new Vector3(float.PositiveInfinity, 1f, 1f), Axes, MassPropertiesRefusal.InertiaNotPositiveFinite),
                ("centre NaN", 0.02, new Vector3(float.NaN, 0f, 0f), inertia, Axes, MassPropertiesRefusal.CentreNotFinite),
                ("axes zero quaternion", 0.02, Centre, inertia, new Quaternion(0f, 0f, 0f, 0f), MassPropertiesRefusal.AxesNotRotation),
                ("axes NaN", 0.02, Centre, inertia, new Quaternion(float.NaN, 0f, 0f, 1f), MassPropertiesRefusal.AxesNotRotation),
                ("axes not unit", 0.02, Centre, inertia, new Quaternion(0f, 0f, 0f, 2f), MassPropertiesRefusal.AxesNotRotation),
            };
            int n = 0;
            foreach (var c in cases)
            {
                Rigidbody body = NewMovingBody(c.name, new Vector3(0f, 100f, 10f * ++n));
                var before = new BodyState(body);
                Assert.That(MassPropertiesBoundary.TryApply(body, c.mass, c.centre, c.inertia, c.axes, out _, out MassPropertiesRefusal refusal), Is.False, c.name);
                Assert.That(refusal, Is.EqualTo(c.expected), c.name);
                before.AssertSame(body, c.name);
            }

            Assert.That(MassPropertiesBoundary.TryApply(null, 0.02, Centre, inertia, Axes, out _, out MassPropertiesRefusal none), Is.False);
            Assert.That(none, Is.EqualTo(MassPropertiesRefusal.NoBody));

            Rigidbody automatic = NewMovingBody("automatic flags on", new Vector3(0f, 100f, 300f));
            automatic.automaticInertiaTensor = true;
            var automaticBefore = new BodyState(automatic);
            Assert.That(MassPropertiesBoundary.TryApply(automatic, 0.02, Centre, inertia, Axes, out _, out MassPropertiesRefusal auto), Is.False);
            Assert.That(auto, Is.EqualTo(MassPropertiesRefusal.AutomaticMassProperties));
            automaticBefore.AssertSame(automatic, "automatic flags on");
        }

        [UnityTest]
        public IEnumerator RefusedAfterTheMassWrite_OnlyTheMassWasWritten_AndWritingItBackRestoresTheBody()
        {
            yield return null;
            // Moments that cannot be scaled to the floor: 3e38 x (1e-7 / 1e-12) is beyond the float range.
            Rigidbody overflow = NewMovingBody("scaled moment beyond float", new Vector3(0f, 100f, 0f));
            var overflowBefore = new BodyState(overflow);
            Assert.That(MassPropertiesBoundary.TryApply(overflow, 1e-12, Centre, new Vector3(3e38f, 1e-20f, 1e-20f), Axes, out _, out MassPropertiesRefusal refusal), Is.False);
            Assert.That(refusal, Is.EqualTo(MassPropertiesRefusal.AppliedInertiaNotRepresentable));
            overflowBefore.AssertSame(overflow, "after the overflow refusal");

            // A moment that is representable after scaling is not refused for its intermediate size: 1e33 x 1e5 = 1e38.
            Rigidbody large = NewMovingBody("scaled moment within float", new Vector3(0f, 100f, 10f));
            Assert.That(MassPropertiesBoundary.TryApply(large, 1e-12, Centre, new Vector3(1e33f, 1e-20f, 1e-20f), Axes, out MassPropertiesBoundary.Applied applied, out _), Is.True);
            AssertRelative(applied.appliedInertia, new Vector3(1e38f, 1e-15f, 1e-15f), 1e-5, "scaled in double, judged as the float written");

            // The read-back refused (as an unusable engine value would be): the mass was written and is written back.
            Rigidbody hooked = NewMovingBody("refused after the write", new Vector3(0f, 100f, 20f));
            var hookedBefore = new BodyState(hooked);
            float seenMass = 0f;
            MassPropertiesBoundary.refuseAfterMassWriteForTest = b => { seenMass = b.mass; return true; };
            Assert.That(MassPropertiesBoundary.TryApply(hooked, BelowFloor, Centre, Moments * (float)BelowFloor, Axes, out _, out MassPropertiesRefusal hookRefusal), Is.False);
            MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
            Assert.That(hookRefusal, Is.EqualTo(MassPropertiesRefusal.RefusedForTest));
            Assert.That(seenMass, Is.EqualTo(Floor), "the mass really was written before the refusal");
            hookedBefore.AssertSame(hooked, "after the refusal following the write");

            // And the body goes on as an untouched twin does.
            Rigidbody twin = NewMovingBody("twin", new Vector3(0f, 100f, 30f));
            for (int i = 0; i < 5; i++)
            {
                Physics.Simulate(1f / 45f);
                Assert.That(hooked.angularVelocity, Is.EqualTo(twin.angularVelocity), "step " + i + ": it turns as the twin");
                Assert.That(hooked.linearVelocity, Is.EqualTo(twin.linearVelocity), "step " + i + ": it moves as the twin");
            }
        }

        [UnityTest]
        public IEnumerator BeginCommitRevert_TheBegunMassIsTheOnlyWrite_RevertWritesItBack_EachEndsOnce()
        {
            yield return null;
            Rigidbody body = NewMovingBody("begun", new Vector3(0f, 100f, 0f));
            var before = new BodyState(body);
            Assert.That(MassPropertiesBoundary.TryBegin(body, BelowFloor, Centre, Moments * (float)BelowFloor, Axes, out MassPropertiesBoundary.Pending pending, out _), Is.True);
            Assert.That(pending.IsOpen, Is.True);
            Assert.That(body.mass, Is.EqualTo(Floor), "the mass is written at the begin");
            Assert.That(body.centerOfMass, Is.EqualTo(new Vector3(0.1f, -0.2f, 0.05f)), "nothing else is");
            Assert.That(body.inertiaTensor, Is.EqualTo(new Vector3(0.7f, 0.4f, 0.9f)));
            MassPropertiesBoundary.Revert(ref pending);
            Assert.That(pending.IsOpen, Is.False);
            before.AssertSame(body, "reverted");
            MassPropertiesBoundary.Revert(ref pending);   // a second revert does nothing
            before.AssertSame(body, "reverted twice");
            Assert.That(() => MassPropertiesBoundary.Commit(ref pending), Throws.InvalidOperationException, "a reverted application is not committed");

            Assert.That(MassPropertiesBoundary.TryBegin(body, BelowFloor, Centre, Moments * (float)BelowFloor, Axes, out pending, out _), Is.True);
            MassPropertiesBoundary.Commit(ref pending);
            AssertHolds(body, Floor, Moments * Floor, "committed");
            MassPropertiesBoundary.Revert(ref pending);   // nothing to revert once committed
            AssertHolds(body, Floor, Moments * Floor, "a revert after the commit changes nothing");
        }

        [UnityTest]
        public IEnumerator TheSide_RefusedAfterItsFlags_PutsTheFlagsAndTheMotionBack_RefusedBeforeThem_TouchesNothing()
        {
            yield return null;
            // A body as one that has not been given explicit mass properties: automatic flags on, a collider to compute them from, moving.
            Rigidbody body = NewBody("side refused", new Vector3(0f, 100f, 0f));
            var box = body.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(0.5f, 2f, 1f);
            body.automaticCenterOfMass = true;
            body.automaticInertiaTensor = true;
            body.mass = 4f;
            body.linearVelocity = new Vector3(0.5f, 1f, -2f);
            body.angularVelocity = new Vector3(0.1f, 0.2f, 0.3f);
            var frame = new GameObject("shape frame");
            frame.transform.SetParent(body.transform, false);
            var side = new PhysicsOwnerSide(true, body.gameObject, frame, body)
            {
                Mass = BelowFloor,
                CenterOfMass = Centre,
                InertiaTensor = Moments * (float)BelowFloor,
                InertiaRotation = Axes,
                FixedByAnchors = true,   // the flags would be changed: kinematic
            };
            var before = new BodyState(body);

            MassPropertiesBoundary.refuseAfterMassWriteForTest = b => true;
            Assert.That(side.TryApplyToBody(out MassPropertiesRefusal refusal), Is.False);
            MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
            Assert.That(refusal, Is.EqualTo(MassPropertiesRefusal.RefusedForTest));
            Assert.That(side.LastRefusal, Is.EqualTo(MassPropertiesRefusal.RefusedForTest));
            Assert.That(side.EffectiveMass, Is.EqualTo(0f), "the side's records of an application are not changed");
            before.AssertSame(body, "refused after the flags (flags, automatic centre and inertia, mass, motion)");

            side.InertiaRotation = new quaternion(0f, 0f, 0f, 0f);
            Assert.That(side.TryApplyToBody(out MassPropertiesRefusal early), Is.False);
            Assert.That(early, Is.EqualTo(MassPropertiesRefusal.AxesNotRotation));
            before.AssertSame(body, "refused before the flags");
        }

        [UnityTest]
        public IEnumerator AnExceptionAfterTheMassWrite_IsPassedOn_AfterTheMassIsWrittenBack_AndTheSidePutsItsFlagsBack()
        {
            yield return null;
            Rigidbody body = NewMovingBody("exception after the write", new Vector3(0f, 100f, 0f));
            var before = new BodyState(body);
            float seen = 0f;
            MassPropertiesBoundary.refuseAfterMassWriteForTest = b => { seen = b.mass; throw new System.InvalidOperationException("after the mass write"); };
            Assert.That(() => MassPropertiesBoundary.TryBegin(body, BelowFloor, Centre, Moments * (float)BelowFloor, Axes, out _, out _), Throws.InvalidOperationException);
            Assert.That(seen, Is.EqualTo(Floor), "the mass really was written before the exception");
            before.AssertSame(body, "after the exception in the begin");

            // The side: its flags (changed before the boundary) and its motion are put back too.
            Rigidbody sideBody = NewBody("side, exception", new Vector3(0f, 100f, 10f));
            var box = sideBody.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(0.5f, 2f, 1f);
            sideBody.automaticCenterOfMass = true;
            sideBody.automaticInertiaTensor = true;
            sideBody.mass = 4f;
            sideBody.linearVelocity = new Vector3(0.5f, 1f, -2f);
            sideBody.angularVelocity = new Vector3(0.1f, 0.2f, 0.3f);
            var frame = new GameObject("shape frame");
            frame.transform.SetParent(sideBody.transform, false);
            var side = new PhysicsOwnerSide(true, sideBody.gameObject, frame, sideBody)
            {
                Mass = BelowFloor,
                CenterOfMass = Centre,
                InertiaTensor = Moments * (float)BelowFloor,
                InertiaRotation = Axes,
                FixedByAnchors = true,
            };
            var sideBefore = new BodyState(sideBody);
            Assert.That(() => side.TryApplyToBody(out _), Throws.InvalidOperationException);
            MassPropertiesBoundary.refuseAfterMassWriteForTest = null;
            sideBefore.AssertSame(sideBody, "the side after the exception (flags, automatic centre and inertia, mass, motion)");
            Assert.That(side.EffectiveMass, Is.EqualTo(0f), "the side's records of an application are not changed");
        }
    }
}
