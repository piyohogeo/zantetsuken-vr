using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Tests
{
    /// <summary>
    /// The fixed view of a measurement (-zantetsuEyeFixed): with the pivot's pose <see cref="SandboxEyeView.FixedPivot"/>
    /// gives, a camera whose local pose is any head pose stands at the fixed eye of the tracking space, unrotated in
    /// it -- wherever the tracking space itself is.
    /// </summary>
    public class SandboxEyeViewTests
    {
        private static readonly (Vector3 position, Vector3 euler)[] Heads =
        {
            (new Vector3(0f, 1.7f, 0.0237f), Vector3.zero),
            (new Vector3(0.43f, 0.72f, -0.31f), new Vector3(-22f, 135f, 8f)),      // lying on a desk, turned away
            (new Vector3(-1.2f, 1.55f, 0.9f), new Vector3(35f, -80f, -15f)),
            (Vector3.zero, new Vector3(0f, 180f, 0f)),
        };

        [Test]
        public void TheCamera_StandsAtTheFixedEyeOfTheTrackingSpace_WhateverTheHeadDoes()
        {
            var space = new GameObject("tracking space").transform;
            var pivot = new GameObject("pivot").transform;
            var camera = new GameObject("camera").transform;
            try
            {
                // The tracking space as a route may have put it: moved and turned.
                space.SetPositionAndRotation(new Vector3(12f, 0.35f, -40f), Quaternion.Euler(0f, 67f, 0f));
                pivot.SetParent(space, false);
                camera.SetParent(pivot, false);
                foreach ((Vector3 position, Vector3 euler) head in Heads)
                {
                    camera.localPosition = head.position;
                    camera.localRotation = Quaternion.Euler(head.euler);
                    SandboxEyeView.FixedPivot(camera.localRotation, camera.localPosition, out Quaternion rotation, out Vector3 position);
                    pivot.localRotation = rotation;
                    pivot.localPosition = position;

                    Vector3 inSpace = space.InverseTransformPoint(camera.position);
                    Assert.That(Vector3.Distance(inSpace, SandboxEyeView.FixedEyePosition), Is.LessThan(1e-4f), "the eye's place in the tracking space, head " + head);
                    Assert.That(Quaternion.Angle(camera.rotation, space.rotation), Is.LessThan(0.01f), "the eye faces the tracking space's forward, level, head " + head);
                }
            }
            finally
            {
                Object.DestroyImmediate(space.gameObject);
            }
        }

        // The walk's order in a frame: the head's newest pose is on the camera, the eye height hold runs, and the pivot
        // is set again later (the walk's end of frame, and before rendering). The hold must not see the head.
        [Test]
        public void TheEyeHeightHold_UnderTheFixedView_IsNotMovedByTheHead()
        {
            const float floor = 0.1f, wanted = 1.6f;
            var space = new GameObject("tracking space").transform;
            var pivot = new GameObject("pivot").transform;
            var camera = new GameObject("camera").transform;
            try
            {
                space.SetPositionAndRotation(new Vector3(96.5f, 0.52f, -38.3f), Quaternion.Euler(0f, 195f, 0f));
                pivot.SetParent(space, false);
                camera.SetParent(pivot, false);

                // The first frame: the head at 1.6 m, the hold puts the fixed eye at the wanted height.
                camera.localPosition = new Vector3(0.2f, 1.6f, -0.1f);
                camera.localRotation = Quaternion.Euler(5f, 20f, 0f);
                SandboxEyeView.HoldEyeHeight(space, pivot, camera, floor, wanted);
                Assert.That(camera.position.y - floor, Is.EqualTo(wanted).Within(1e-4f), "the first hold sets the fixed eye's height");
                Vector3 held = space.position;

                // The next frames: the head rises 0.1 m, turns and drops, the hold running BEFORE the walk's own pivot update.
                foreach ((float y, Vector3 euler) head in new[] { (1.7f, new Vector3(-10f, 40f, 3f)), (1.2f, new Vector3(30f, -60f, -8f)), (1.65f, Vector3.zero) })
                {
                    camera.localPosition = new Vector3(0.25f, head.y, -0.05f);
                    camera.localRotation = Quaternion.Euler(head.euler);
                    float read = SandboxEyeView.HoldEyeHeight(space, pivot, camera, floor, wanted);
                    Assert.That(read, Is.EqualTo(wanted).Within(1e-4f), "the hold reads the fixed eye, head at " + head.y);
                    Assert.That(Vector3.Distance(space.position, held), Is.LessThan(1e-4f), "the tracking space (the katana's and the hands') did not move, head at " + head.y);

                    // The walk's later pivot update of the same frame changes nothing more.
                    SandboxEyeView.FixedPivot(camera.localRotation, camera.localPosition, out Quaternion rotation, out Vector3 position);
                    pivot.localRotation = rotation;
                    pivot.localPosition = position;
                    Assert.That(camera.position.y - floor, Is.EqualTo(wanted).Within(1e-4f), "the frame's final eye height, head at " + head.y);
                }
            }
            finally
            {
                Object.DestroyImmediate(space.gameObject);
            }
        }

        [Test]
        public void TheEyeHeightHold_WithoutTheFixedView_FollowsTheHeadAsBefore()
        {
            const float floor = 0f, wanted = 1.6f;
            var space = new GameObject("tracking space").transform;
            var camera = new GameObject("camera").transform;
            try
            {
                camera.SetParent(space, false);
                camera.localPosition = new Vector3(0f, 1.7f, 0f);
                float read = SandboxEyeView.HoldEyeHeight(space, null, camera, floor, wanted);
                Assert.That(read, Is.EqualTo(1.7f).Within(1e-4f));
                Assert.That(space.position.y, Is.EqualTo(-0.1f).Within(1e-4f), "the tracking space is moved against the head's height");
                Assert.That(camera.position.y, Is.EqualTo(wanted).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(space.gameObject);
            }
        }

        [Test]
        public void TheFixedEye_IsOverTheTrackingSpacesOrigin_AtTheMeasurementsEyeHeight()
        {
            Assert.That(SandboxEyeView.FixedEyePosition, Is.EqualTo(new Vector3(0f, 1.6f, 0f)));
            Assert.That(SandboxEyeView.FixedArgument, Is.EqualTo("-zantetsuEyeFixed"));
        }
    }
}
