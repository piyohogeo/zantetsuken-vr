using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The trial rest of building pieces (<see cref="BuildingRest"/>, 2026-09-29) on the product's physics and cut
    /// path: a building registered as one, cut by the driver, its pieces published Provisional then Final, the
    /// manual physics stepping, and the root's turn each frame. The floor is a static collider. Every case switches the
    /// trial on through the profile (it is off by default) and reads the rest's own record; what it observes about
    /// the engine's own waking is written out, not asserted.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const float RestSettleSeconds = 20f;

        private CutWorldRoot NewRestWorld()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile => SetPrivate(profile, "buildingRestEnabled", true));
            Assert.That(root.Rest, Is.Not.Null, "the root made the trial rest");
            Assert.That(root.Rest.Enabled, Is.True, "and the profile switched it on");
            root.Driver.RemainingMainSeconds = () => 1.0;

            // The floor: a static collider whose top is at y = -1, so a box (half size 1) at y = 0 stands on it.
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(60f, 1f, 60f);
            return root;
        }

        /// <summary>A building box at a place, under gravity (the fixture's bodies are made without it).</summary>
        private LogicalFragmentId AddBuilding(CutWorldRoot root, Vector3 at)
        {
            LogicalFragmentId fragment = AddBody(root, at, building: true);
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True);
            owner.Body.useGravity = true;
            return fragment;
        }

        private static Rigidbody BodyOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True, "piece " + fragment.value + " has an owner");
            return owner.Body;
        }

        private static void WriteRecord(CutWorldRoot root, string what)
        {
            BuildingRest rest = root.Rest;
            TestContext.Out.WriteLine(what + ": tracked " + rest.TrackedDynamic + "+" + rest.TrackedAnchored + ", attempted " + rest.SleepAttempted + ", succeeded " + rest.SleepSucceeded + ", asleep now " + rest.AsleepNow
                + ", auto wakes " + rest.AutoWakes + ", explicit wakes " + rest.ExplicitWakes + ", unsupported past timeout " + rest.UnsupportedPastTimeout + ", asleep without support " + rest.AsleepWithoutSupport
                + ", notes " + rest.ContactNotes + ", cost contacts/support/sleep ms " + (rest.ContactSeconds * 1000).ToString("F2") + "/" + (rest.SupportSeconds * 1000).ToString("F2") + "/" + (rest.SleepSeconds * 1000).ToString("F2") + " over " + rest.Steps + " turns");
            foreach (string e in rest.Events)
            {
                TestContext.Out.WriteLine("  " + e);
            }
        }

        /// <summary>
        /// **A group of debris on the floor rests after the timeout.** A building box on the floor is cut in two
        /// horizontally: the lower half stands on the floor, the upper on the lower. After the timeout and the support
        /// steps both are put to sleep as one group, and none is asleep without a chain to the ground.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingRest_ADebrisGroupOnTheFloor_RestsAfterTheTimeout()
        {
            CutWorldRoot root = NewRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            Assert.That(root.Rest.TrackedDynamic, Is.EqualTo(2), "both halves are tracked (the source left)");

            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AsleepNow >= 2 || Time.realtimeSinceStartup > deadline, "the group's rest");
            WriteRecord(root, "after the settle");
            Assert.That(root.Rest.AsleepNow, Is.EqualTo(2), "both halves asleep by the trial");
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero, "none asleep without a chain to the ground");
            Assert.That(BodyOf(root, sides[0]).IsSleeping() && BodyOf(root, sides[1]).IsSleeping(), Is.True, "the engine reports both asleep");
            Assert.That(root.Rest.SleepAttempted, Is.EqualTo(2), "one attempt per piece");

            yield return PhysicsSeconds(1.0);
            WriteRecord(root, "one second later");
            Assert.That(root.Rest.AsleepNow, Is.EqualTo(2), "held for a second (no auto wake)");
            yield return EndWorld(root);
            Assert.That(root.Rest, Is.Null, "the ending released the trial: its subscription and its records are gone");
        }

        /// <summary>
        /// **Pieces in the air keep falling, and pieces touching each other in the air are not put to rest.** A building
        /// box high above the floor is cut in two horizontally while it falls: past the timeout both are still awake and
        /// counted as past the timeout without support. Once they have landed they rest.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingRest_AirbornePiecesKeepFalling_AndRestOnlyOnceLanded()
        {
            CutWorldRoot root = NewRestWorld();
            // The floor far below instead of the box far above: the fixture's cut planes and geometry are at the origin.
            GameObject.Find("Floor").transform.position = new Vector3(0f, -11f, 0f);   // its top at y = -12
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            double published = CutPhysicsStep.Clock.PhysicsSeconds;
            yield return Until(() => CutPhysicsStep.Clock.PhysicsSeconds >= published + 1.15, "past the timeout");
            float y0 = BodyOf(root, sides[0]).worldCenterOfMass.y, y1 = BodyOf(root, sides[1]).worldCenterOfMass.y;
            WriteRecord(root, "past the timeout, in the air (centres y " + y0.ToString("F2") + ", " + y1.ToString("F2") + ")");
            Assert.That(Mathf.Min(y0, y1), Is.GreaterThan(-9f), "the layout: still in the air (the floor's top is at -12)");
            Assert.That(root.Rest.AsleepNow, Is.Zero, "nothing asleep in the air");
            Assert.That(root.Rest.SleepAttempted, Is.Zero, "no attempt either");
            Assert.That(root.Rest.UnsupportedPastTimeout, Is.EqualTo(2), "both are past the timeout without support");

            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AsleepNow >= 1 || Time.realtimeSinceStartup > deadline, "rest after landing");
            WriteRecord(root, "after landing");
            Assert.That(root.Rest.AsleepNow, Is.GreaterThanOrEqualTo(1), "at least the piece on the floor rests once landed and supported");
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A sleeping piece can be re-cut, and its group is woken explicitly at the publication.** After the group of
        /// two rests, the upper half is cut vertically: the trial wakes the group when the Provisional pair is published
        /// (the source leaves the scene), the new children start their own judgement, and later the three pieces rest.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingRest_ASleepingPieceIsReCut_TheGroupIsWokenAndTheChildrenRestAgain()
        {
            CutWorldRoot root = NewRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AsleepNow >= 2 || Time.realtimeSinceStartup > deadline, "the group's rest");
            Assert.That(root.Rest.AsleepNow, Is.EqualTo(2), "the layout: both asleep");
            LogicalFragmentId upper = BodyOf(root, sides[0]).worldCenterOfMass.y > BodyOf(root, sides[1]).worldCenterOfMass.y ? sides[0] : sides[1];
            LogicalFragmentId lower = upper.Equals(sides[0]) ? sides[1] : sides[0];

            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(1f, 0f, 0f, 0f), children);
            WriteRecord(root, "after the re-cut of the upper (sleeping) piece");
            Assert.That(root.Rest.ExplicitWakes, Is.GreaterThanOrEqualTo(1), "the group was woken at the publication");
            Assert.That(root.Rest.TrackedDynamic, Is.EqualTo(3), "the lower half and the two children");
            Assert.That(BodyOf(root, children[0]).IsSleeping() || BodyOf(root, children[1]).IsSleeping(), Is.False, "the children start awake");

            deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AsleepNow >= 3 || Time.realtimeSinceStartup > deadline, "the three pieces' rest");
            WriteRecord(root, "after the settle");
            Assert.That(root.Rest.AsleepNow, Is.EqualTo(3), "the lower half (woken and judged again) and both children rest");
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero);
            Assert.That(BodyOf(root, lower).IsSleeping(), Is.True);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Re-cutting the supporter wakes what stood on it, and that piece moves.** With the group of two at rest,
        /// the lower half is cut vertically: the upper half is woken explicitly at the publication (it stood on the
        /// source), it is awake right after, and within the next second it has moved or turned as the new children under
        /// it take its weight. Whether it rests again afterwards is written out.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingRest_ReCuttingTheSupporter_WakesThePieceItHeld()
        {
            CutWorldRoot root = NewRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AsleepNow >= 2 || Time.realtimeSinceStartup > deadline, "the group's rest");
            Assert.That(root.Rest.AsleepNow, Is.EqualTo(2), "the layout: both asleep");
            LogicalFragmentId upper = BodyOf(root, sides[0]).worldCenterOfMass.y > BodyOf(root, sides[1]).worldCenterOfMass.y ? sides[0] : sides[1];
            LogicalFragmentId lower = upper.Equals(sides[0]) ? sides[1] : sides[0];
            Rigidbody upperBody = BodyOf(root, upper);
            Vector3 before = upperBody.worldCenterOfMass;
            Quaternion beforeRotation = upperBody.rotation;

            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(1f, 0f, 0f, 0f), children);
            WriteRecord(root, "after the re-cut of the lower (supporting) piece");
            Assert.That(root.Rest.ExplicitWakes, Is.GreaterThanOrEqualTo(1), "the upper half was woken explicitly as its supporter left");
            Assert.That(upperBody.IsSleeping(), Is.False, "the upper half is awake right after");

            yield return PhysicsSeconds(1.0);
            float moved = (upperBody.worldCenterOfMass - before).magnitude, turned = Quaternion.Angle(upperBody.rotation, beforeRotation);
            WriteRecord(root, "one second later: the upper half moved " + moved.ToString("F4") + " m, turned " + turned.ToString("F2") + " deg, asleep " + upperBody.IsSleeping());
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero, "nothing asleep without support");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A contact from an outside dynamic body wakes a resting piece, and the trial records it and leaves it awake.**
        /// A plain sphere is dropped onto the resting upper half: the engine wakes the piece; the rest counts an automatic
        /// wake with the partner it touched, and does not put the piece to sleep again.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingRest_AnOutsideBodyWakesARestingPiece_AndItStaysAwake()
        {
            CutWorldRoot root = NewRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AsleepNow >= 2 || Time.realtimeSinceStartup > deadline, "the group's rest");
            Assert.That(root.Rest.AsleepNow, Is.EqualTo(2), "the layout: both asleep");

            GameObject sphere = Track(new GameObject("Outside Sphere"));
            sphere.transform.position = new Vector3(0.3f, 5f, 0.2f);
            sphere.AddComponent<SphereCollider>().radius = 0.25f;
            var ball = sphere.AddComponent<Rigidbody>();
            ball.mass = 2f;
            ball.useGravity = true;

            deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Rest.AutoWakes >= 1 || Time.realtimeSinceStartup > deadline, "the engine's wake");
            WriteRecord(root, "after the sphere landed");
            Assert.That(root.Rest.AutoWakes, Is.GreaterThanOrEqualTo(1), "the engine woke a resting piece and the trial recorded it");
            int asleepAfterWake = root.Rest.AsleepNow;
            int attempted = root.Rest.SleepAttempted;
            yield return PhysicsSeconds(2.0);
            WriteRecord(root, "two seconds later");
            Assert.That(root.Rest.SleepAttempted, Is.EqualTo(attempted), "the woken piece is not put to sleep again by the trial");
            Assert.That(root.Rest.AsleepNow, Is.LessThanOrEqualTo(asleepAfterWake), "and stays awake");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Off by default: nothing is tracked and nothing is put to sleep.** The same debris on the floor with the
        /// profile's default; after the same time the rest holds no record, and the pieces' sleep is the engine's own.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingRest_OffByDefault_TracksNothing()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            Assert.That(root.Rest, Is.Not.Null);
            Assert.That(root.Rest.Enabled, Is.False, "the profile's default is off");
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(60f, 1f, 60f);
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            yield return PhysicsSeconds(1.5);
            Assert.That(root.Rest.TrackedDynamic + root.Rest.TrackedAnchored, Is.Zero, "nothing tracked");
            Assert.That(root.Rest.SleepAttempted + root.Rest.Steps, Is.Zero, "no attempt, no turn");
            TestContext.Out.WriteLine("off: the engine's own sleep after 1.5 s: " + BodyOf(root, sides[0]).IsSleeping() + ", " + BodyOf(root, sides[1]).IsSleeping());
            yield return EndWorld(root);
        }
    }
}
