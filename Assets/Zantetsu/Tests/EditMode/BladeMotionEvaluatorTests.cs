using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class BladeMotionEvaluatorTests
    {
        private const float Tolerance = 1e-4f;

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

        private static void AssertVector(Vector3 actual, Vector3 expected, float tolerance)
        {
            Assert.That(Vector3.Distance(actual, expected), Is.LessThan(tolerance));
        }

        [Test]
        public void BladeMotionSample_HasNoReferenceTypeFields()
        {
            FieldInfo[] fields = typeof(BladeMotionSample).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " is a reference type");
            }
        }

        [Test]
        public void OneMeterInHalfSecond_GivesVelocityTwo()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 0.5, new Vector3(1, 0, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.DeltaTimeSeconds, Is.EqualTo(0.5));
            Assert.That(result.Speed, Is.EqualTo(2f).Within(Tolerance));
            AssertVector(result.CutSampleVelocity, new Vector3(2, 0, 0), Tolerance);
        }

        [Test]
        public void EdgeDirectionMovement_GivesScorePlusOne()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 1.0, new Vector3(0, 1, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.HasLateralMotion, Is.True);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void ReverseEdgeDirectionMovement_GivesScoreMinusOne()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 1.0, new Vector3(0, -1, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.HasLateralMotion, Is.True);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(-1f).Within(Tolerance));
        }

        [Test]
        public void PerpendicularMovement_GivesScoreZero()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 1.0, new Vector3(0, 0, 1));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.HasLateralMotion, Is.True);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(0f).Within(Tolerance));
        }

        [Test]
        public void ThrustComponent_IsRemoved()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 1.0, new Vector3(3, 0, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            AssertVector(result.LateralVelocity, Vector3.zero, Tolerance);
            Assert.That(result.LateralSpeed, Is.EqualTo(0f).Within(Tolerance));
            Assert.That(result.HasLateralMotion, Is.False);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(0f));
        }

        [Test]
        public void MixedThrustAndEdgeMovement_GivesCorrectLateralAndScore()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 1.0, new Vector3(1, 1, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            AssertVector(result.LateralVelocity, new Vector3(0, 1, 0), Tolerance);
            Assert.That(result.LateralSpeed, Is.EqualTo(1f).Within(Tolerance));
            Assert.That(result.HasLateralMotion, Is.True);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void Stationary_IsSuccessWithZeroMotion()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 0.1, Vector3.zero);

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.Speed, Is.EqualTo(0f));
            Assert.That(result.HasLateralMotion, Is.False);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(0f));
        }

        [Test]
        public void UsesCurrentPoseAxes()
        {
            EvaluatedBladePose previous = new EvaluatedBladePose(1, 0.0, new Pose(Vector3.zero, Quaternion.identity), Vector3.right, Vector3.up, Vector3.forward, Vector3.zero);
            EvaluatedBladePose current = new EvaluatedBladePose(2, 1.0, new Pose(Vector3.zero, Quaternion.identity), Vector3.up, Vector3.right, Vector3.forward, new Vector3(1, 0, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            // Movement +X is edge-leading against the current axes (edge = right).
            Assert.That(ok, Is.True);
            Assert.That(result.EdgeLeadScore, Is.EqualTo(1f).Within(Tolerance));
        }

        [Test]
        public void SameFrameId_IncreasingTimestamp_IsSuccess()
        {
            EvaluatedBladePose previous = PoseAt(5, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(5, 0.1, new Vector3(0, 1, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.True);
            Assert.That(result.FromFrameId, Is.EqualTo(5));
            Assert.That(result.ToFrameId, Is.EqualTo(5));
        }

        [Test]
        public void TimestampNotIncreasing_IsRejected()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.5, Vector3.zero);
            EvaluatedBladePose equal = PoseAt(2, 0.5, new Vector3(0, 1, 0));
            EvaluatedBladePose earlier = PoseAt(2, 0.4, new Vector3(0, 1, 0));

            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, equal, out _), Is.False);
            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, earlier, out _), Is.False);
        }

        [Test]
        public void NonFiniteTimestamp_IsRejected()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose nanCurrent = PoseAt(2, double.NaN, new Vector3(0, 1, 0));
            EvaluatedBladePose infCurrent = PoseAt(2, double.PositiveInfinity, new Vector3(0, 1, 0));

            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, nanCurrent, out _), Is.False);
            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, infCurrent, out _), Is.False);
        }

        [Test]
        public void NonFinitePosition_IsRejected()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, new Vector3(float.NaN, 0, 0));
            EvaluatedBladePose current = PoseAt(2, 0.1, new Vector3(float.PositiveInfinity, 0, 0));

            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, current, out _), Is.False);
        }

        [Test]
        public void InvalidAxes_AreRejected()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);

            EvaluatedBladePose zeroAxis = new EvaluatedBladePose(2, 0.1, new Pose(Vector3.zero, Quaternion.identity), Vector3.zero, Vector3.up, Vector3.forward, new Vector3(0, 1, 0));
            EvaluatedBladePose nonUnitAxis = new EvaluatedBladePose(2, 0.1, new Pose(Vector3.zero, Quaternion.identity), new Vector3(2, 0, 0), Vector3.up, Vector3.forward, new Vector3(0, 1, 0));
            EvaluatedBladePose nonOrthogonal = new EvaluatedBladePose(2, 0.1, new Pose(Vector3.zero, Quaternion.identity), Vector3.right, Vector3.right, Vector3.forward, new Vector3(0, 1, 0));

            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, zeroAxis, out _), Is.False);
            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, nonUnitAxis, out _), Is.False);
            Assert.That(BladeMotionEvaluator.TryEvaluate(previous, nonOrthogonal, out _), Is.False);
        }

        [Test]
        public void TinyDeltaTime_VelocityOverflow_IsRejected()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 1e-40, new Vector3(1, 0, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void FiniteInputs_DifferenceOverflow_IsRejected()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, new Vector3(-float.MaxValue, 0, 0));
            EvaluatedBladePose current = PoseAt(2, 1.0, new Vector3(float.MaxValue, 0, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out _);

            Assert.That(ok, Is.False);
        }

        [Test]
        public void Failure_ResultsInDefault()
        {
            EvaluatedBladePose previous = PoseAt(1, 0.0, Vector3.zero);
            EvaluatedBladePose current = PoseAt(2, 0.0, new Vector3(1, 0, 0));

            bool ok = BladeMotionEvaluator.TryEvaluate(previous, current, out BladeMotionSample result);

            Assert.That(ok, Is.False);
            Assert.That(result.FromFrameId, Is.EqualTo(0));
            Assert.That(result.ToFrameId, Is.EqualTo(0));
            Assert.That(result.DeltaTimeSeconds, Is.EqualTo(0.0));
            Assert.That(result.Speed, Is.EqualTo(0f));
            Assert.That(result.LateralSpeed, Is.EqualTo(0f));
            Assert.That(result.EdgeLeadScore, Is.EqualTo(0f));
            Assert.That(result.HasLateralMotion, Is.False);
        }
    }
}
