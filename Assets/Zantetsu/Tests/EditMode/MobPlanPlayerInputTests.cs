using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The city's stick move follows the head's horizontal facing (D-225), never the rig's forward and never up or down:
    /// a head turned in the room, a head looking down, a head looking straight up (the fallback), and the move's speed
    /// forward and back.
    /// </summary>
    public sealed class MobPlanPlayerInputTests
    {
        [Test]
        public void TheFacing_IsTheHeadsForwardLaidFlat_NotTheRigs()
        {
            // The rig faces +z; the player has turned 90 degrees to the right in the room and looks a little down.
            Vector3 headForward = Quaternion.Euler(20f, 90f, 0f) * Vector3.forward;
            Vector3 facing = MobPlanPlayerInput.HorizontalFacing(headForward, Vector3.forward);
            Assert.That(facing.y, Is.Zero);
            Assert.That(facing.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(Vector3.Angle(facing, Vector3.right), Is.LessThan(1e-3f), "the head's facing, flat: +x, not the rig's +z");
        }

        [Test]
        public void AHeadLookingStraightDownOrUp_FallsBackToTheRigsForward_Flat()
        {
            Vector3 rigForward = Quaternion.Euler(10f, 30f, 0f) * Vector3.forward;   // a rig pitched a little, turned 30 degrees
            Vector3 down = MobPlanPlayerInput.HorizontalFacing(Vector3.down, rigForward);
            Vector3 up = MobPlanPlayerInput.HorizontalFacing(Vector3.up, rigForward);
            Vector3 expected = new Vector3(rigForward.x, 0f, rigForward.z).normalized;
            Assert.That(Vector3.Angle(down, expected), Is.LessThan(1e-3f));
            Assert.That(Vector3.Angle(up, expected), Is.LessThan(1e-3f));
            Assert.That(down.y, Is.Zero);
            Assert.That(MobPlanPlayerInput.HorizontalFacing(Vector3.down, Vector3.up), Is.EqualTo(Vector3.forward), "both vertical: the world's forward");
        }

        [Test]
        public void TheMove_IsAlongTheFacing_ForwardAtTheSpeed_BackAtTheReverseFactor_AndNeverVertical()
        {
            Vector3 facing = MobPlanPlayerInput.HorizontalFacing(Quaternion.Euler(-40f, 45f, 0f) * Vector3.forward, Vector3.forward);
            Vector3 forward = MobPlanPlayerInput.Move(facing, 1f, 1.49f, 0.5f, 0.5f);
            Vector3 back = MobPlanPlayerInput.Move(facing, -1f, 1.49f, 0.5f, 0.5f);
            Vector3 half = MobPlanPlayerInput.Move(facing, 0.5f, 1.49f, 0.5f, 0.5f);
            Assert.That(forward.y, Is.Zero, "looking up moves nobody up");
            Assert.That(forward.magnitude, Is.EqualTo(1.49f * 0.5f).Within(1e-5f));
            Assert.That(Vector3.Angle(forward, facing), Is.LessThan(1e-3f));
            Assert.That(back.magnitude, Is.EqualTo(1.49f * 0.5f * 0.5f).Within(1e-5f), "back at the reverse factor");
            Assert.That(Vector3.Angle(back, -facing), Is.LessThan(1e-3f));
            Assert.That(half.magnitude, Is.EqualTo(1.49f * 0.5f * 0.5f).Within(1e-5f), "half the stick, half the speed");
            Assert.That(MobPlanPlayerInput.Move(facing, 3f, 1.49f, 0.5f, 0.5f).magnitude, Is.EqualTo(1.49f * 0.5f).Within(1e-5f), "clamped to the stick's range");
        }

        [Test]
        public void TheHead_IsTheOneAssigned_ElseTheMainCamera()
        {
            var host = new GameObject("city input");
            var assigned = new GameObject("head");
            var camera = new GameObject("Main Camera", typeof(Camera)) { tag = "MainCamera" };
            try
            {
                MobPlanPlayerInput input = host.AddComponent<MobPlanPlayerInput>();
                // Unassigned: the main camera -- the Editor's open scene may hold one of its own, so the one found is
                // asked for its tag, not its identity.
                Transform found = input.Head;
                Assert.That(found, Is.Not.Null, "unassigned: the main camera");
                Assert.That(found.CompareTag("MainCamera"), Is.True);
                Assert.That(found.GetComponent<Camera>(), Is.Not.Null);
                input.head = assigned.transform;
                Assert.That(input.Head, Is.SameAs(assigned.transform), "assigned: that one, whatever the cameras");
            }
            finally
            {
                Object.DestroyImmediate(host);
                Object.DestroyImmediate(assigned);
                Object.DestroyImmediate(camera);
            }
        }
    }
}
