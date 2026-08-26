using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class BladeEdgeGateTests
    {
        private const float Tolerance = 1e-4f;

        private static BladeEdgeGateSettings DefaultSettings()
        {
            return new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, 0.15f);
        }

        private static EvaluatedBladePose PoseAt(long frameId, double timestamp, Vector3 cutSamplePosition)
        {
            return new EvaluatedBladePose(
                frameId,
                timestamp,
                new Pose(Vector3.zero, Quaternion.identity),
                Vector3.right,
                Vector3.up,
                Vector3.forward,
                cutSamplePosition);
        }

        private static BladeMotionSample MakeValidSample()
        {
            // 0.2 m edge-leading movement over 0.05 s: speed 4, displacement 0.2, score 1.
            BladeMotionEvaluator.TryEvaluate(
                PoseAt(1, 0.0, Vector3.zero),
                PoseAt(2, 0.05, new Vector3(0, 0.2f, 0)),
                out BladeMotionSample sample);
            return sample;
        }

        [Test]
        public void Settings_HoldValues()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, 0.15f);

            Assert.That(settings.MinimumWindowSeconds, Is.EqualTo(0.030));
            Assert.That(settings.MaximumWindowSeconds, Is.EqualTo(0.060));
            Assert.That(settings.MinimumSpeed, Is.EqualTo(1.5f));
            Assert.That(settings.MinimumDisplacement, Is.EqualTo(0.15f));
            Assert.That(settings.MinimumEdgeLeadScore, Is.EqualTo(0.15f));
        }

        [Test]
        public void Settings_RejectInvalidValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(double.NaN, 0.060, 1.5f, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, double.PositiveInfinity, 1.5f, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.0, 0.060, 1.5f, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(-0.01, 0.060, 1.5f, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.060, 0.030, 1.5f, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, -1.5f, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, 1.5f, -0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, float.NaN, 0.15f, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, 1.5f, float.PositiveInfinity, 0.15f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, -1.1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, 1.1f));
        }

        [Test]
        public void ValidEdgeMovement_IsAccepted()
        {
            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(MakeValidSample(), DefaultSettings());

            Assert.That(decision.IsAccepted, Is.True);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.None));
        }

        [Test]
        public void WindowBoundaries_AreAccepted()
        {
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.030, new Vector3(0, 0.2f, 0)), out BladeMotionSample minSample);
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.060, new Vector3(0, 0.2f, 0)), out BladeMotionSample maxSample);

            Assert.That(BladeEdgeGate.Evaluate(minSample, DefaultSettings()).IsAccepted, Is.True);
            Assert.That(BladeEdgeGate.Evaluate(maxSample, DefaultSettings()).IsAccepted, Is.True);
        }

        [Test]
        public void WindowTooShort_IsRejected()
        {
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.029, new Vector3(0, 0.2f, 0)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, DefaultSettings());

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.WindowTooShort));
        }

        [Test]
        public void WindowTooLong_IsRejected()
        {
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.061, new Vector3(0, 0.2f, 0)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, DefaultSettings());

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.WindowTooLong));
        }

        [Test]
        public void SpeedBelowMinimum_IsRejected()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.01, 1.0, 2.0f, 0f, 0.15f);
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 1.0, new Vector3(0, 1f, 0)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, settings);

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.SpeedBelowMinimum));
        }

        [Test]
        public void DisplacementBelowMinimum_IsRejected()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.01, 1.0, 0f, 2.0f, 0.15f);
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 1.0, new Vector3(0, 1f, 0)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, settings);

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.DisplacementBelowMinimum));
        }

        [Test]
        public void NoLateralMotion_IsRejected()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.01, 1.0, 0f, 0f, 0.15f);
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 1.0, new Vector3(1f, 0, 0)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, settings);

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.NoLateralMotion));
        }

        [Test]
        public void EdgeScoreBelowThreshold_IsRejected()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.01, 1.0, 0f, 0f, 0.5f);
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 1.0, new Vector3(0, 0, 1f)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, settings);

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.EdgeLeadBelowThreshold));
        }

        [Test]
        public void EdgeScoreEqualToThreshold_IsRejected()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.01, 1.0, 0f, 0f, 0f);
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 1.0, new Vector3(0, 0, 1f)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, settings);

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.EdgeLeadBelowThreshold));
        }

        [Test]
        public void SpineSideMovement_IsRejected()
        {
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.05, new Vector3(0, -0.2f, 0)), out BladeMotionSample sample);

            BladeEdgeGateDecision decision = BladeEdgeGate.Evaluate(sample, DefaultSettings());

            Assert.That(decision.IsAccepted, Is.False);
            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.EdgeLeadBelowThreshold));
        }

        [Test]
        public void NonFiniteSample_IsInvalidInput()
        {
            BladeMotionSample nanDeltaTime = new BladeMotionSample(1, 2, 0.0, double.NaN, double.NaN, Vector3.zero, Vector3.zero, Vector3.zero, 0f, 0f, 0f, false);
            Assert.That(BladeEdgeGate.Evaluate(nanDeltaTime, DefaultSettings()).Reason, Is.EqualTo(BladeEdgeGateReason.InvalidInput));

            BladeMotionSample nanSpeed = new BladeMotionSample(1, 2, 0.0, 0.05, 0.05, new Vector3(0, 0.2f, 0), new Vector3(0, 4, 0), new Vector3(0, 4, 0), float.NaN, 4f, 1f, true);
            Assert.That(BladeEdgeGate.Evaluate(nanSpeed, DefaultSettings()).Reason, Is.EqualTo(BladeEdgeGateReason.InvalidInput));

            BladeMotionSample infDisplacement = new BladeMotionSample(1, 2, 0.0, 0.05, 0.05, new Vector3(float.PositiveInfinity, 0, 0), Vector3.zero, Vector3.zero, 0f, 0f, 0f, false);
            Assert.That(BladeEdgeGate.Evaluate(infDisplacement, DefaultSettings()).Reason, Is.EqualTo(BladeEdgeGateReason.InvalidInput));

            BladeMotionSample badScore = new BladeMotionSample(1, 2, 0.0, 0.05, 0.05, new Vector3(0, 0.2f, 0), new Vector3(0, 4, 0), new Vector3(0, 4, 0), 4f, 4f, 2f, true);
            Assert.That(BladeEdgeGate.Evaluate(badScore, DefaultSettings()).Reason, Is.EqualTo(BladeEdgeGateReason.InvalidInput));
        }

        [Test]
        public void MultipleFailures_ReturnHighestPriorityReason()
        {
            // Fails window (too short), speed, and displacement -> WindowTooShort.
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.01, new Vector3(0, 0.001f, 0)), out BladeMotionSample shortAndSlow);
            Assert.That(BladeEdgeGate.Evaluate(shortAndSlow, DefaultSettings()).Reason, Is.EqualTo(BladeEdgeGateReason.WindowTooShort));

            // Fails speed, displacement, lateral motion, and score -> SpeedBelowMinimum.
            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.05, new Vector3(0.001f, 0, 0)), out BladeMotionSample slowThrust);
            Assert.That(BladeEdgeGate.Evaluate(slowThrust, DefaultSettings()).Reason, Is.EqualTo(BladeEdgeGateReason.SpeedBelowMinimum));
        }

        [Test]
        public void Decision_Invariant_IsEnforced()
        {
            BladeEdgeGateDecision accepted = BladeEdgeGate.Evaluate(MakeValidSample(), DefaultSettings());
            Assert.That(accepted.IsAccepted, Is.True);
            Assert.That(accepted.Reason, Is.EqualTo(BladeEdgeGateReason.None));

            BladeMotionEvaluator.TryEvaluate(PoseAt(1, 0.0, Vector3.zero), PoseAt(2, 0.029, new Vector3(0, 0.2f, 0)), out BladeMotionSample rejectedSample);
            BladeEdgeGateDecision rejected = BladeEdgeGate.Evaluate(rejectedSample, DefaultSettings());
            Assert.That(rejected.IsAccepted, Is.False);
            Assert.That(rejected.Reason, Is.Not.EqualTo(BladeEdgeGateReason.None));
        }

        [Test]
        public void Decision_Invariant_HoldsForAllReasons()
        {
            Array reasons = Enum.GetValues(typeof(BladeEdgeGateReason));
            foreach (BladeEdgeGateReason reason in reasons)
            {
                BladeEdgeGateDecision decision = new BladeEdgeGateDecision(reason);
                Assert.That(decision.Reason, Is.EqualTo(reason));
                Assert.That(decision.IsAccepted, Is.EqualTo(reason == BladeEdgeGateReason.None));
            }
        }

        [Test]
        public void DefaultDecision_SatisfiesInvariant()
        {
            BladeEdgeGateDecision decision = default;

            Assert.That(decision.Reason, Is.EqualTo(BladeEdgeGateReason.None));
            Assert.That(decision.IsAccepted, Is.True);
            Assert.That(decision.IsAccepted, Is.EqualTo(decision.Reason == BladeEdgeGateReason.None));
        }

        [Test]
        public void Decision_HasNoIndependentAcceptedField()
        {
            FieldInfo[] fields = typeof(BladeEdgeGateDecision).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.Name, Is.Not.EqualTo("IsAccepted"), "IsAccepted must not be an independent field");
            }

            Assert.That(typeof(BladeEdgeGateDecision).GetProperty("IsAccepted"), Is.Not.Null);
        }

        [Test]
        public void PublicValueTypes_HaveNoReferenceFields()
        {
            AssertNoReferenceFields(typeof(BladeEdgeGateSettings));
            AssertNoReferenceFields(typeof(BladeEdgeGateDecision));
        }

        [Test]
        public void GateInputs_AreImmutable()
        {
            AssertAllFieldsReadonly(typeof(BladeMotionSample));
            AssertAllFieldsReadonly(typeof(BladeEdgeGateSettings));
            AssertAllFieldsReadonly(typeof(BladeEdgeGateDecision));
        }

        private static void AssertNoReferenceFields(Type type)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, type.Name + "." + field.Name + " is a reference type");
            }
        }

        private static void AssertAllFieldsReadonly(Type type)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " is not readonly");
            }
        }
    }
}
