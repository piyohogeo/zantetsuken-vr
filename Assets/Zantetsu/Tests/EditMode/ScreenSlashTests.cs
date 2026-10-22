using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Slash;

namespace Zantetsu.Core.Tests
{
    public class ScreenSlashTests
    {
        private static readonly Pose IdentityPose = new Pose(Vector3.zero, Quaternion.identity);

        [TestCase(0.5f, 1f, 0f)]
        [TestCase(2f, 1f, 0f)]
        [TestCase(0.5f, 0.7f, -0.35f)]
        [TestCase(2f, 0f, -1f)]
        public void ScreenLine_UsesFlickPositionAndViewportEdges_RegardlessOfStrokeLength(float aspect, float x, float y)
        {
            Vector2 position = new Vector2(0.28f, 0.67f);
            Vector2 direction = new Vector2(x, y);
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, aspect, 0.05f, position, direction, out ScreenSlashFrame frame), Is.True);
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, aspect, 0.05f, position, direction * 100f, out ScreenSlashFrame longerStroke), Is.True);
            AssertVector(longerStroke.InitialA, frame.InitialA);
            AssertVector(longerStroke.InitialB, frame.InitialB);
            AssertVector(longerStroke.StartTravelPerMetre, frame.StartTravelPerMetre);
            AssertVector(longerStroke.EndTravelPerMetre, frame.EndTravelPerMetre);

            Vector2 a = Viewport(frame.InitialA, aspect);
            Vector2 b = Viewport(frame.InitialB, aspect);
            AssertOnEdge(a);
            AssertOnEdge(b);
            Assert.That(Cross(a - position, direction.normalized), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(Cross(b - position, direction.normalized), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(Vector2.Dot(b - a, direction), Is.GreaterThan(0f), "endpoint order follows flick direction");
            Assert.That(frame.SourceSlashPlane.GetDistanceToPoint(frame.InitialA), Is.EqualTo(0f).Within(1e-5f));
            Assert.That(frame.SourceSlashPlane.GetDistanceToPoint(frame.InitialB), Is.EqualTo(0f).Within(1e-5f));
        }

        [Test]
        public void PixelGeometry_CapturesCameraPoseProjectionAndPixelAspectOnce()
        {
            var cameraObject = new GameObject("Screen slash snapshot test");
            try
            {
                Camera camera = cameraObject.AddComponent<Camera>();
                camera.fieldOfView = 60f;
                camera.nearClipPlane = 0.05f;
                camera.pixelRect = new Rect(21f, 37f, 960f, 540f);
                camera.transform.SetPositionAndRotation(new Vector3(2f, 1.5f, -3f), Quaternion.Euler(-12f, 35f, 0f));
                Vector2 position = new Vector2(320f, 330f);
                Vector2 direction = new Vector2(100f, -30f);
                Assert.That(ScreenSlashGeometry.TryCreate(camera, position, direction, out ScreenSlashFrame frame), Is.True);
                Vector3 screenA = camera.WorldToScreenPoint(frame.InitialA);
                Vector3 screenB = camera.WorldToScreenPoint(frame.InitialB);
                Assert.That(Cross(new Vector2(screenA.x, screenA.y) - position, direction.normalized), Is.EqualTo(0f).Within(0.01f));
                Assert.That(Cross(new Vector2(screenB.x, screenB.y) - position, direction.normalized), Is.EqualTo(0f).Within(0.01f));

                var core = new SlashWaveCore(default);
                core.Tick(10.0);
                Assert.That(core.TryLatchScreenSlash(10.0, frame, out _), Is.True);
                camera.transform.SetPositionAndRotation(new Vector3(-10f, 7f, 2f), Quaternion.Euler(0f, 170f, 0f));
                camera.fieldOfView = 100f;
                camera.pixelRect = new Rect(0f, 0f, 540f, 960f);
                core.Tick(10.25);
                SlashSweep sweep = core.SweepAt(0);
                float rayDistance = frame.InitialTravelDistance + 3f;
                AssertVector(sweep.CurrentA, frame.Origin + frame.StartTravelPerMetre * rayDistance);
                AssertVector(sweep.CurrentB, frame.Origin + frame.EndTravelPerMetre * rayDistance);
                Assert.That(sweep.SourceSlashPlane.GetDistanceToPoint(sweep.CurrentA), Is.EqualTo(0f).Within(1e-4f));
                Assert.That(sweep.SourceSlashPlane.GetDistanceToPoint(sweep.CurrentB), Is.EqualTo(0f).Within(1e-4f));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }

        [TestCase(0.5f)]
        [TestCase(2f)]
        public void LatchedScreenWave_ExpandsAcrossFrozenViewport_WithAdoptedFlightAndActualEndpointSweeps(float aspect)
        {
            Vector2 position = new Vector2(0.2f, 0.8f);
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, aspect, 0.05f, position, Vector2.right, out ScreenSlashFrame frame), Is.True);
            var core = new SlashWaveCore(default);
            core.Tick(10.0);
            Assert.That(core.TryLatchScreenSlash(10.0, frame, out long id), Is.True);
            Assert.That(core.SweepCount, Is.EqualTo(1));
            SlashSweep latch = core.SweepAt(0);
            Assert.That(latch.IsLatch, Is.True);
            AssertVector(latch.PreviousA, latch.CurrentA);
            AssertVector(latch.PreviousB, latch.CurrentB);
            Assert.That(core.TryGetWaveSpanClose(0, out double closedAt, out _, out _), Is.True);
            Assert.That(closedAt, Is.EqualTo(10.0), "the screen frame is closed at recognition");

            // Changing the current flight reaches later waves only.
            core.Flight = new SlashWaveFlight(1f, 0.01f);
            core.Tick(10.25);
            Assert.That(core.WaveCount, Is.EqualTo(1));
            Assert.That(core.RecordedPoseCount, Is.Zero, "flying needs no VR blade pose");
            Assert.That(core.SweepCount, Is.EqualTo(1));
            SlashSweep moved = core.SweepAt(0);
            Assert.That(moved.SlashId, Is.EqualTo(id));
            Assert.That(moved.IsLatch, Is.False);
            AssertVector(moved.PreviousA, latch.CurrentA);
            AssertVector(moved.PreviousB, latch.CurrentB);
            AssertVector(moved.CurrentA, frame.InitialA + frame.StartTravelPerMetre * 3f);
            AssertVector(moved.CurrentB, frame.InitialB + frame.EndTravelPerMetre * 3f);
            Assert.That((moved.CurrentB - moved.CurrentA).magnitude, Is.GreaterThan((latch.CurrentB - latch.CurrentA).magnitude));
            Vector2 a = Viewport(moved.CurrentA, aspect);
            Vector2 b = Viewport(moved.CurrentB, aspect);
            Assert.That(a.x, Is.EqualTo(0f).Within(1e-5f));
            Assert.That(b.x, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(a.y, Is.EqualTo(position.y).Within(1e-5f));
            Assert.That(b.y, Is.EqualTo(position.y).Within(1e-5f));
            AssertVector(frame.TravelAxis, new Vector3((2f * position.x - 1f) * Mathf.Tan(30f * Mathf.Deg2Rad) * aspect,
                (2f * position.y - 1f) * Mathf.Tan(30f * Mathf.Deg2Rad), 1f).normalized);
            core.Tick(11.5);
            Assert.That(core.WaveCount, Is.Zero, "adopted lifetime remains 1.5 seconds");
            Assert.That(core.SweepCount, Is.Zero);
        }

        [Test]
        public void MobileCapacity_IsSixteen_AndExpiryFreesSlotsBeforeLatching()
        {
            Assert.That(new SlashWaveCore(default).WaveCapacity, Is.EqualTo(4), "VR default is preserved");
            var core = new SlashWaveCore(default, SlashWaveCore.MaximumCapacity);
            Assert.That(core.WaveCapacity, Is.EqualTo(16));
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, 1f, 0.05f, Vector2.one * 0.5f, Vector2.right, out ScreenSlashFrame frame), Is.True);
            core.Tick(1.0);
            long lastId = 0;
            for (int i = 0; i < core.WaveCapacity; i++)
            {
                Assert.That(core.TryLatchScreenSlash(1.0, frame, out long id), Is.True);
                Assert.That(id, Is.GreaterThan(lastId));
                lastId = id;
            }

            Assert.That(core.WaveCount, Is.EqualTo(16));
            Assert.That(core.SweepCount, Is.EqualTo(16), "every simultaneous latch has its sweep");
            Assert.That(core.TryLatchScreenSlash(1.0, frame, out _), Is.False);
            Assert.That(core.LastUpdate.Latch, Is.EqualTo(SlashLatchAttempt.NoSlot));
            core.Tick(1.1);
            Assert.That(core.SweepCount, Is.EqualTo(16), "every live wave can feed hit queries");
            core.Tick(2.5);
            Assert.That(core.TryLatchScreenSlash(2.5, frame, out long replacement), Is.True);
            Assert.That(replacement, Is.GreaterThan(lastId));
            Assert.That(core.WaveCount, Is.EqualTo(1));
            Assert.That(core.SweepCount, Is.EqualTo(1));
        }

        [Test]
        public void Tick_RejectsUnusableTimeAndNeverRewindsFlyingEndpoints()
        {
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, 1f, 0.05f, Vector2.one * 0.5f, Vector2.right, out ScreenSlashFrame frame), Is.True);
            var core = new SlashWaveCore(default);
            core.Tick(1.0);
            Assert.That(core.TryLatchScreenSlash(double.NaN, frame, out _), Is.False);
            Assert.That(core.TryLatchScreenSlash(1.0, default, out _), Is.False);
            Assert.That(core.TryLatchScreenSlash(1.0, frame, out _), Is.True);
            core.Tick(1.2);
            SlashSweep moved = core.SweepAt(0);
            core.Tick(double.NaN);
            Assert.That(core.SweepCount, Is.Zero);
            core.Tick(1.1);
            Assert.That(core.SweepCount, Is.Zero);
            core.Tick(1.3);
            SlashSweep next = core.SweepAt(0);
            AssertVector(next.PreviousA, moved.CurrentA);
            AssertVector(next.PreviousB, moved.CurrentB);
            Assert.Throws<ArgumentOutOfRangeException>(() => new SlashWaveCore(default, SlashWaveCore.MaximumCapacity + 1));
        }

        [Test]
        public void Geometry_RefusesEmptyDirectionAndPositionsOutsideTheViewport()
        {
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, 1f, 0.05f, Vector2.one * 0.5f, Vector2.zero, out _), Is.False);
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, 1f, 0.05f, new Vector2(-0.1f, 0.5f), Vector2.right, out _), Is.False);
            Assert.That(ScreenSlashGeometry.TryCreate(IdentityPose, 60f, 1f, 0f, Vector2.one * 0.5f, Vector2.right, out _), Is.False);
        }

        private static Vector2 Viewport(Vector3 worldPoint, float aspect)
        {
            float height = worldPoint.z * Mathf.Tan(30f * Mathf.Deg2Rad);
            return new Vector2(0.5f + worldPoint.x / (2f * height * aspect), 0.5f + worldPoint.y / (2f * height));
        }

        private static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        private static void AssertOnEdge(Vector2 point)
        {
            Assert.That(point.x, Is.InRange(-1e-5f, 1.00001f));
            Assert.That(point.y, Is.InRange(-1e-5f, 1.00001f));
            float distance = Mathf.Min(Mathf.Min(Mathf.Abs(point.x), Mathf.Abs(1f - point.x)),
                Mathf.Min(Mathf.Abs(point.y), Mathf.Abs(1f - point.y)));
            Assert.That(distance, Is.LessThan(1e-5f));
        }

        private static void AssertVector(Vector3 actual, Vector3 expected)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(1e-4f));
        }
    }
}
