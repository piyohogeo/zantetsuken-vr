using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class BladeInputProcessorTests
    {
        private const float Tolerance = 1e-4f;

        private static BladeEdgeGateSettings GateSettings()
        {
            return new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, 0.15f);
        }

        private static BladeFrame Frame()
        {
            return new BladeFrame(Vector3.right, Vector3.up, Vector3.forward, Vector3.right * 0.7f);
        }

        private static Pose IdentityOffset()
        {
            return new Pose(Vector3.zero, Quaternion.identity);
        }

        private static BladePoseSample FullyTracked(double timestamp, Vector3 gripPosition)
        {
            return FullyTrackedAt(1, timestamp, gripPosition);
        }

        private static BladePoseSample FullyTrackedAt(long frameId, double timestamp, Vector3 gripPosition)
        {
            return new BladePoseSample(frameId, timestamp, gripPosition, Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        [Test]
        public void Constructor_RejectsInvalidCapacity()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeInputProcessor(0, GateSettings()));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeInputProcessor(1, GateSettings()));
            Assert.That(new BladeInputProcessor(2, GateSettings()).WindowCount, Is.EqualTo(0));
        }

        [Test]
        public void Constructor_RejectsInvalidSettings()
        {
            Assert.Throws<ArgumentException>(() => new BladeInputProcessor(2, default));

            BladeInputProcessor processor = new BladeInputProcessor(2, GateSettings());
            Assert.That(processor.WindowCount, Is.EqualTo(0));
            Assert.That(processor.HasUsableTracking, Is.False);
        }

        [Test]
        public void FirstValidSample_WindowAccumulating_NoTransition()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.WindowAccumulating));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
            Assert.That(result.HasEvaluatedPose, Is.True);
            Assert.That(result.HasGateDecision, Is.False);
            Assert.That(processor.WindowCount, Is.EqualTo(1));
            Assert.That(processor.HasUsableTracking, Is.True);
        }

        [Test]
        public void FirstIncompleteSample_WaitingForTracking_NoTransition()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            BladePoseSample incomplete = new BladePoseSample(1, 0.0, Vector3.zero, Quaternion.identity, BladeTrackingState.Position);

            BladeInputProcessingResult result = processor.Process(incomplete, IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.WaitingForTracking));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
            Assert.That(processor.HasUsableTracking, Is.False);
        }

        [Test]
        public void FirstInvalidSample_InvalidSample_NoTransition()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            BladePoseSample invalid = new BladePoseSample(1, 0.0, new Vector3(float.NaN, 0, 0), Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);

            BladeInputProcessingResult result = processor.Process(invalid, IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.InvalidSample));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
            Assert.That(processor.HasUsableTracking, Is.False);
        }

        [Test]
        public void ValidConsecutiveSamples_GateAccepted()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.050, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.GateAccepted));
            Assert.That(result.IsGateAccepted, Is.True);
            Assert.That(result.HasGateDecision, Is.True);
            Assert.That(result.HasMotion, Is.True);
            Assert.That(result.GateDecision.Reason, Is.EqualTo(BladeEdgeGateReason.None));
        }

        [Test]
        public void SpineSideMovement_GateRejected_WithReason()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.050, new Vector3(0, -0.2f, 0)), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.GateRejected));
            Assert.That(result.IsGateAccepted, Is.False);
            Assert.That(result.HasGateDecision, Is.True);
            Assert.That(result.GateDecision.Reason, Is.EqualTo(BladeEdgeGateReason.EdgeLeadBelowThreshold));
        }

        [Test]
        public void WindowTooShort_WindowAccumulating()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.010, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.WindowAccumulating));
            Assert.That(result.HasGateDecision, Is.False);
        }

        [Test]
        public void PositionOnlyRotationOnlyNone_AreWaitingForTracking()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

            BladePoseSample positionOnly = new BladePoseSample(1, 0.0, Vector3.zero, Quaternion.identity, BladeTrackingState.Position);
            BladePoseSample rotationOnly = new BladePoseSample(1, 0.0, Vector3.zero, Quaternion.identity, BladeTrackingState.Rotation);
            BladePoseSample none = new BladePoseSample(1, 0.0, Vector3.zero, Quaternion.identity, BladeTrackingState.None);

            Assert.That(processor.Process(positionOnly, IdentityOffset(), Frame()).Status, Is.EqualTo(BladeInputProcessingStatus.WaitingForTracking));
            Assert.That(processor.Process(rotationOnly, IdentityOffset(), Frame()).Status, Is.EqualTo(BladeInputProcessingStatus.WaitingForTracking));
            Assert.That(processor.Process(none, IdentityOffset(), Frame()).Status, Is.EqualTo(BladeInputProcessingStatus.WaitingForTracking));
        }

        [Test]
        public void ValidToTrackingLost_LostOnce_WindowCleared()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            processor.Process(FullyTracked(0.030, new Vector3(0, 0.1f, 0)), IdentityOffset(), Frame());
            Assert.That(processor.WindowCount, Is.EqualTo(2));

            BladePoseSample lost = new BladePoseSample(1, 0.060, Vector3.zero, Quaternion.identity, BladeTrackingState.Position);
            BladeInputProcessingResult result = processor.Process(lost, IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.WaitingForTracking));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.Lost));
            Assert.That(processor.WindowCount, Is.EqualTo(0));
            Assert.That(processor.HasUsableTracking, Is.False);
        }

        [Test]
        public void ConsecutiveLost_DoesNotRepeat()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());

            BladePoseSample lost1 = new BladePoseSample(1, 0.010, Vector3.zero, Quaternion.identity, BladeTrackingState.Position);
            Assert.That(processor.Process(lost1, IdentityOffset(), Frame()).TrackingTransition, Is.EqualTo(BladeTrackingTransition.Lost));

            BladePoseSample lost2 = new BladePoseSample(1, 0.020, Vector3.zero, Quaternion.identity, BladeTrackingState.Rotation);
            Assert.That(processor.Process(lost2, IdentityOffset(), Frame()).TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
        }

        [Test]
        public void Restore_RestoredOnce()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            processor.Process(new BladePoseSample(1, 0.010, Vector3.zero, Quaternion.identity, BladeTrackingState.Position), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.030, new Vector3(0, 0.1f, 0)), IdentityOffset(), Frame());

            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.Restored));
        }

        [Test]
        public void AfterRestore_NoGateDecisionUntilWindowReaccumulates()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            processor.Process(new BladePoseSample(1, 0.010, Vector3.zero, Quaternion.identity, BladeTrackingState.Position), IdentityOffset(), Frame());

            BladeInputProcessingResult restored = processor.Process(FullyTracked(0.030, new Vector3(0, 0.1f, 0)), IdentityOffset(), Frame());

            Assert.That(restored.TrackingTransition, Is.EqualTo(BladeTrackingTransition.Restored));
            Assert.That(restored.Status, Is.EqualTo(BladeInputProcessingStatus.WindowAccumulating));
            Assert.That(restored.HasGateDecision, Is.False);
            Assert.That(restored.HasMotion, Is.False);
        }

        [Test]
        public void NoMotionAcrossLoss()
        {
            BladeInputProcessor processor = new BladeInputProcessor(4, GateSettings());
            processor.Process(FullyTrackedAt(1, 0.0, new Vector3(0, 0, 0)), IdentityOffset(), Frame());
            processor.Process(FullyTrackedAt(2, 0.030, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());
            processor.Process(new BladePoseSample(3, 0.060, Vector3.zero, Quaternion.identity, BladeTrackingState.Position), IdentityOffset(), Frame()); // loss

            processor.Process(FullyTrackedAt(4, 0.090, new Vector3(0, 0.4f, 0)), IdentityOffset(), Frame()); // restore (window accumulating)
            BladeInputProcessingResult result = processor.Process(FullyTrackedAt(5, 0.150, new Vector3(0, 0.7f, 0)), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.GateAccepted));
            // Motion spans only the post-loss poses (frame 4 -> 5), never across the loss.
            Assert.That(result.Motion.FromFrameId, Is.EqualTo(4));
            Assert.That(result.Motion.ToFrameId, Is.EqualTo(5));
        }

        [Test]
        public void ValidToAdapterFailure_LostAndInvalidSample()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());

            BladePoseSample bad = new BladePoseSample(2, 0.030, new Vector3(float.NaN, 0, 0), Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
            BladeInputProcessingResult result = processor.Process(bad, IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.InvalidSample));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.Lost));
            Assert.That(processor.HasUsableTracking, Is.False);
            Assert.That(processor.WindowCount, Is.EqualTo(0));
        }

        [Test]
        public void ValidToTimestampRegression_LostAndInvalidSample()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.050, Vector3.zero), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.040, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.InvalidSample));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.Lost));
            Assert.That(processor.WindowCount, Is.EqualTo(0));
        }

        [Test]
        public void InvalidSampleThenValid_Restored()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            processor.Process(new BladePoseSample(2, 0.030, new Vector3(float.NaN, 0, 0), Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.060, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());

            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.Restored));
        }

        [Test]
        public void FrameIdRegressionOrEqual_StillAccepted()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTrackedAt(10, 0.0, Vector3.zero), IdentityOffset(), Frame());

            BladeInputProcessingResult result = processor.Process(FullyTrackedAt(5, 0.050, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.GateAccepted));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
        }

        [Test]
        public void Reset_InitializesWindowAndTracking()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());
            processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            processor.Process(FullyTracked(0.030, new Vector3(0, 0.1f, 0)), IdentityOffset(), Frame());
            Assert.That(processor.WindowCount, Is.EqualTo(2));
            Assert.That(processor.HasUsableTracking, Is.True);

            processor.Reset();
            Assert.That(processor.WindowCount, Is.EqualTo(0));
            Assert.That(processor.HasUsableTracking, Is.False);

            BladeInputProcessingResult result = processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.WindowAccumulating));
        }

        [Test]
        public void ResultProperties_SatisfyInvariants()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

            BladeInputProcessingResult waiting = processor.Process(new BladePoseSample(1, 0.0, Vector3.zero, Quaternion.identity, BladeTrackingState.Position), IdentityOffset(), Frame());
            AssertFlags(waiting, false, false, false, false);

            BladeInputProcessingResult invalid = processor.Process(new BladePoseSample(2, 0.0, new Vector3(float.NaN, 0, 0), Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation), IdentityOffset(), Frame());
            AssertFlags(invalid, false, false, false, false);

            BladeInputProcessingResult accumulating = processor.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            AssertFlags(accumulating, true, false, false, false);

            BladeInputProcessingResult accepted = processor.Process(FullyTracked(0.050, new Vector3(0, 0.2f, 0)), IdentityOffset(), Frame());
            AssertFlags(accepted, true, true, true, true);

            BladeInputProcessor other = new BladeInputProcessor(3, GateSettings());
            other.Process(FullyTracked(0.0, Vector3.zero), IdentityOffset(), Frame());
            BladeInputProcessingResult rejected = other.Process(FullyTracked(0.050, new Vector3(0, -0.2f, 0)), IdentityOffset(), Frame());
            AssertFlags(rejected, true, true, true, false);
        }

        [Test]
        public void DefaultResult_IsSafe()
        {
            BladeInputProcessingResult result = default;

            Assert.That(result.Status, Is.EqualTo(BladeInputProcessingStatus.None));
            Assert.That(result.TrackingTransition, Is.EqualTo(BladeTrackingTransition.None));
            Assert.That(result.HasEvaluatedPose, Is.False);
            Assert.That(result.HasMotion, Is.False);
            Assert.That(result.HasGateDecision, Is.False);
            Assert.That(result.IsGateAccepted, Is.False);
        }

        [Test]
        public void ResultValueTypes_HaveNoReferenceFields()
        {
            FieldInfo[] fields = typeof(BladeInputProcessingResult).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " is a reference type");
            }
        }

        [Test]
        public void Process_DoesNotMutateInputs()
        {
            BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

            BladePoseSample sample = FullyTracked(0.050, new Vector3(0, 0.2f, 0));
            Pose offset = IdentityOffset();
            BladeFrame frame = Frame();
            BladeEdgeGateSettings settings = GateSettings();

            long frameId = sample.FrameId;
            double timestamp = sample.TimestampSeconds;
            Vector3 gripPosition = sample.GripPosition;
            Vector3 offsetPosition = offset.position;
            Vector3 frameAxis = frame.BladeAxis;
            float minimumSpeed = settings.MinimumSpeed;

            processor.Process(sample, offset, frame);

            Assert.That(sample.FrameId, Is.EqualTo(frameId));
            Assert.That(sample.TimestampSeconds, Is.EqualTo(timestamp));
            Assert.That(sample.GripPosition, Is.EqualTo(gripPosition));
            Assert.That(offset.position, Is.EqualTo(offsetPosition));
            Assert.That(frame.BladeAxis, Is.EqualTo(frameAxis));
            Assert.That(settings.MinimumSpeed, Is.EqualTo(minimumSpeed));
        }

        private static void AssertFlags(BladeInputProcessingResult result, bool hasPose, bool hasMotion, bool hasGate, bool accepted)
        {
            Assert.That(result.HasEvaluatedPose, Is.EqualTo(hasPose));
            Assert.That(result.HasMotion, Is.EqualTo(hasMotion));
            Assert.That(result.HasGateDecision, Is.EqualTo(hasGate));
            Assert.That(result.IsGateAccepted, Is.EqualTo(accepted));
        }
    }
}
