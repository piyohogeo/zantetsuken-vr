using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class BladePoseAdapterTests
    {
        private const float PositionTolerance = 1e-4f;
        private const float AngleTolerance = 1e-3f;

        private static BladePoseSample FullyTracked(Vector3 position, Quaternion rotation)
        {
            return new BladePoseSample(1, 0.5, position, rotation, BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static BladeFrame StandardFrame()
        {
            return new BladeFrame(Vector3.right, Vector3.up, Vector3.forward, Vector3.right * 0.7f);
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, float tolerance)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tolerance));
        }

        [Test]
        public void IdentityOffset_MatchesGripPose()
        {
            Vector3 gripPosition = new Vector3(1, 2, 3);
            Quaternion gripRotation = Quaternion.Euler(10, 20, 30);
            BladePoseSample sample = FullyTracked(gripPosition, gripRotation);
            Pose identity = new Pose(Vector3.zero, Quaternion.identity);

            bool ok = BladePoseAdapter.TryEvaluate(sample, identity, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            AssertVector(result.KatanaPose.position, gripPosition, PositionTolerance);
            Assert.That(Quaternion.Angle(result.KatanaPose.rotation, gripRotation), Is.LessThan(AngleTolerance));
            Assert.That(result.FrameId, Is.EqualTo(1));
            Assert.That(result.TimestampSeconds, Is.EqualTo(0.5));
        }

        [Test]
        public void PositionOffset_IsRotatedByGripRotation()
        {
            Quaternion gripRotation = Quaternion.Euler(0, 90, 0);
            BladePoseSample sample = FullyTracked(Vector3.zero, gripRotation);
            Pose offset = new Pose(Vector3.forward, Quaternion.identity);

            bool ok = BladePoseAdapter.TryEvaluate(sample, offset, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            AssertVector(result.KatanaPose.position, Vector3.right, PositionTolerance);
        }

        [Test]
        public void RotationOffset_CompositionOrderIsCorrect()
        {
            Quaternion gripRotation = Quaternion.Euler(0, 30, 0);
            Quaternion offsetRotation = Quaternion.Euler(0, 0, 45);
            BladePoseSample sample = FullyTracked(Vector3.zero, gripRotation);
            Pose offset = new Pose(Vector3.zero, offsetRotation);

            bool ok = BladePoseAdapter.TryEvaluate(sample, offset, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            Quaternion expected = gripRotation * offsetRotation;
            Assert.That(Quaternion.Angle(result.KatanaPose.rotation, expected), Is.LessThan(AngleTolerance));
        }

        [Test]
        public void BladeAxes_AreTransformedToWorld()
        {
            Quaternion gripRotation = Quaternion.Euler(0, 90, 0);
            BladePoseSample sample = FullyTracked(Vector3.zero, gripRotation);
            Pose identity = new Pose(Vector3.zero, Quaternion.identity);

            bool ok = BladePoseAdapter.TryEvaluate(sample, identity, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            AssertVector(result.BladeAxis, Vector3.back, PositionTolerance);
            AssertVector(result.EdgeDirection, Vector3.up, PositionTolerance);
            AssertVector(result.SideNormal, Vector3.right, PositionTolerance);
        }

        [Test]
        public void CutSamplePoint_IsTransformedToWorld()
        {
            Quaternion gripRotation = Quaternion.Euler(0, 90, 0);
            BladePoseSample sample = FullyTracked(new Vector3(1, 2, 3), gripRotation);
            Pose identity = new Pose(Vector3.zero, Quaternion.identity);
            Vector3 expected = new Vector3(1, 2, 3) + Vector3.back * 0.7f;

            bool ok = BladePoseAdapter.TryEvaluate(sample, identity, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            AssertVector(result.CutSamplePosition, expected, PositionTolerance);
        }

        [Test]
        public void OutputAxes_AreUnitLengthAndOrthogonal()
        {
            Quaternion gripRotation = Quaternion.Euler(12, 34, 56);
            BladePoseSample sample = FullyTracked(new Vector3(4, 5, 6), gripRotation);
            Pose offset = new Pose(new Vector3(0.5f, -0.25f, 0.75f), Quaternion.Euler(-10, 15, -20));

            bool ok = BladePoseAdapter.TryEvaluate(sample, offset, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            Assert.That(result.BladeAxis.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(result.EdgeDirection.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(result.SideNormal.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(Mathf.Abs(Vector3.Dot(result.BladeAxis, result.EdgeDirection)), Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(Vector3.Dot(result.EdgeDirection, result.SideNormal)), Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(Vector3.Dot(result.SideNormal, result.BladeAxis)), Is.LessThan(1e-4f));
        }

        [Test]
        public void BladeFrame_PreservesInputAxisOrientation()
        {
            // right/up/back are mutually orthogonal but not right-handed; the
            // adapter must preserve them as-is instead of reconstructing handedness.
            BladeFrame frame = new BladeFrame(Vector3.right, Vector3.up, Vector3.back, Vector3.zero);
            BladePoseSample sample = FullyTracked(Vector3.zero, Quaternion.identity);
            Pose identity = new Pose(Vector3.zero, Quaternion.identity);

            bool ok = BladePoseAdapter.TryEvaluate(sample, identity, frame, out EvaluatedBladePose result);

            Assert.That(ok, Is.True);
            AssertVector(result.BladeAxis, Vector3.right, PositionTolerance);
            AssertVector(result.EdgeDirection, Vector3.up, PositionTolerance);
            AssertVector(result.SideNormal, Vector3.back, PositionTolerance);
        }

        [Test]
        public void MissingPositionTracking_IsRejected()
        {
            BladePoseSample sample = new BladePoseSample(1, 0, new Vector3(1, 2, 3), Quaternion.identity, BladeTrackingState.Rotation);
            bool ok = BladePoseAdapter.TryEvaluate(sample, new Pose(Vector3.zero, Quaternion.identity), StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.False);
            Assert.That(result.FrameId, Is.EqualTo(0));
        }

        [Test]
        public void MissingRotationTracking_IsRejected()
        {
            BladePoseSample sample = new BladePoseSample(1, 0, new Vector3(1, 2, 3), Quaternion.identity, BladeTrackingState.Position);
            bool ok = BladePoseAdapter.TryEvaluate(sample, new Pose(Vector3.zero, Quaternion.identity), StandardFrame(), out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void NaNOrInfinity_IsRejected()
        {
            Pose identity = new Pose(Vector3.zero, Quaternion.identity);

            BladePoseSample nanPosition = FullyTracked(new Vector3(float.NaN, 0, 0), Quaternion.identity);
            Assert.That(BladePoseAdapter.TryEvaluate(nanPosition, identity, StandardFrame(), out _), Is.False);

            BladePoseSample nanRotation = FullyTracked(Vector3.zero, new Quaternion(float.NaN, 0, 0, 1));
            Assert.That(BladePoseAdapter.TryEvaluate(nanRotation, identity, StandardFrame(), out _), Is.False);

            Pose infOffset = new Pose(new Vector3(float.PositiveInfinity, 0, 0), Quaternion.identity);
            Assert.That(BladePoseAdapter.TryEvaluate(FullyTracked(Vector3.zero, Quaternion.identity), infOffset, StandardFrame(), out _), Is.False);
        }

        [Test]
        public void ZeroQuaternion_IsRejected()
        {
            BladePoseSample sample = FullyTracked(Vector3.zero, new Quaternion(0, 0, 0, 0));
            bool ok = BladePoseAdapter.TryEvaluate(sample, new Pose(Vector3.zero, Quaternion.identity), StandardFrame(), out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void ZeroOffsetRotation_IsRejected()
        {
            Pose offset = new Pose(Vector3.zero, new Quaternion(0, 0, 0, 0));
            bool ok = BladePoseAdapter.TryEvaluate(FullyTracked(Vector3.zero, Quaternion.identity), offset, StandardFrame(), out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void DefaultBladeFrame_IsRejectedByTryEvaluate()
        {
            bool ok = BladePoseAdapter.TryEvaluate(
                FullyTracked(Vector3.zero, Quaternion.identity),
                new Pose(Vector3.zero, Quaternion.identity),
                default,
                out EvaluatedBladePose result);

            Assert.That(ok, Is.False);
            Assert.That(result.FrameId, Is.EqualTo(0));
        }

        [Test]
        public void NonFiniteTimestamp_IsRejected()
        {
            Pose identity = new Pose(Vector3.zero, Quaternion.identity);

            BladePoseSample nanTime = new BladePoseSample(1, double.NaN, Vector3.zero, Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
            Assert.That(BladePoseAdapter.TryEvaluate(nanTime, identity, StandardFrame(), out _), Is.False);

            BladePoseSample infTime = new BladePoseSample(1, double.PositiveInfinity, Vector3.zero, Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
            Assert.That(BladePoseAdapter.TryEvaluate(infTime, identity, StandardFrame(), out _), Is.False);
        }

        [Test]
        public void QuaternionSquaredLengthOverflow_IsRejected()
        {
            // Finite components whose squared length overflows to Infinity.
            BladePoseSample sample = FullyTracked(Vector3.zero, new Quaternion(1e19f, 1e19f, 1e19f, 1e19f));
            bool ok = BladePoseAdapter.TryEvaluate(sample, new Pose(Vector3.zero, Quaternion.identity), StandardFrame(), out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void FiniteInputs_ThatOverflow_AreRejected()
        {
            // Finite inputs whose sum overflows to Infinity during the transform.
            BladePoseSample sample = FullyTracked(new Vector3(float.MaxValue, 0, 0), Quaternion.identity);
            Pose offset = new Pose(new Vector3(float.MaxValue, 0, 0), Quaternion.identity);

            bool ok = BladePoseAdapter.TryEvaluate(sample, offset, StandardFrame(), out EvaluatedBladePose result);

            Assert.That(ok, Is.False);
            Assert.That(result.FrameId, Is.EqualTo(0));
        }

        [Test]
        public void FakeSource_ReturnsSample()
        {
            BladePoseSample expected = new BladePoseSample(42, 2.0, new Vector3(3, 4, 5), Quaternion.Euler(5, 6, 7), BladeTrackingState.Position | BladeTrackingState.Rotation);
            IBladePoseSource source = new FakeBladePoseSource(expected, true);

            Assert.That(source.TryGetLatestSample(out BladePoseSample sample), Is.True);
            Assert.That(sample.FrameId, Is.EqualTo(42));
            Assert.That(sample.IsFullyTracked, Is.True);
        }

        [Test]
        public void FakeSource_ReturnsDefaultWhenUnavailable()
        {
            IBladePoseSource source = new FakeBladePoseSource(default, false);

            Assert.That(source.TryGetLatestSample(out BladePoseSample sample), Is.False);
            Assert.That(sample.FrameId, Is.EqualTo(0));
            Assert.That(sample.IsFullyTracked, Is.False);
        }

        private sealed class FakeBladePoseSource : IBladePoseSource
        {
            private readonly BladePoseSample _sample;
            private readonly bool _hasSample;

            public FakeBladePoseSource(BladePoseSample sample, bool hasSample)
            {
                _sample = sample;
                _hasSample = hasSample;
            }

            public bool TryGetLatestSample(out BladePoseSample sample)
            {
                sample = _hasSample ? _sample : default;
                return _hasSample;
            }
        }
    }
}
