using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;

namespace Zantetsu.Core.Tests
{
    public class BladeFrameTests
    {
        [Test]
        public void TrackingState_BitsAreIndependent()
        {
            Assert.That((int)BladeTrackingState.None, Is.EqualTo(0));
            Assert.That(((int)BladeTrackingState.Position & (int)BladeTrackingState.Rotation), Is.EqualTo(0));
            Assert.That((int)BladeTrackingState.Position, Is.Not.EqualTo((int)BladeTrackingState.Rotation));
        }

        [Test]
        public void IsFullyTracked_RequiresPositionAndRotation()
        {
            BladePoseSample positionOnly = new BladePoseSample(0, 0, default, default, BladeTrackingState.Position);
            BladePoseSample rotationOnly = new BladePoseSample(0, 0, default, default, BladeTrackingState.Rotation);
            BladePoseSample both = new BladePoseSample(0, 0, default, default, BladeTrackingState.Position | BladeTrackingState.Rotation);

            Assert.That(positionOnly.IsPositionTracked, Is.True);
            Assert.That(positionOnly.IsRotationTracked, Is.False);
            Assert.That(positionOnly.IsFullyTracked, Is.False);

            Assert.That(rotationOnly.IsPositionTracked, Is.False);
            Assert.That(rotationOnly.IsRotationTracked, Is.True);
            Assert.That(rotationOnly.IsFullyTracked, Is.False);

            Assert.That(both.IsFullyTracked, Is.True);
        }

        [Test]
        public void BladePoseSample_HoldsFields()
        {
            Vector3 position = new Vector3(1, 2, 3);
            Quaternion rotation = Quaternion.Euler(10, 20, 30);
            BladePoseSample sample = new BladePoseSample(7, 1.5, position, rotation, BladeTrackingState.Position | BladeTrackingState.Rotation);

            Assert.That(sample.FrameId, Is.EqualTo(7));
            Assert.That(sample.TimestampSeconds, Is.EqualTo(1.5));
            Assert.That(Vector3.Distance(sample.GripPosition, position), Is.LessThan(1e-6f));
            Assert.That(Quaternion.Angle(sample.GripRotation, rotation), Is.LessThan(1e-4f));
            Assert.That(sample.TrackingState, Is.EqualTo(BladeTrackingState.Position | BladeTrackingState.Rotation));
        }

        [Test]
        public void BladePoseSample_HasNoReferenceTypeFields()
        {
            AssertNoReferenceFields(typeof(BladePoseSample));
        }

        [Test]
        public void EvaluatedBladePose_HasNoReferenceTypeFields()
        {
            AssertNoReferenceFields(typeof(EvaluatedBladePose));
        }

        [Test]
        public void BladeFrame_NormalizesAxes()
        {
            BladeFrame frame = new BladeFrame(Vector3.right * 2f, Vector3.up * 3f, Vector3.forward * 4f, Vector3.one);

            Assert.That(frame.BladeAxis.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(frame.EdgeDirection.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(frame.SideNormal.magnitude, Is.EqualTo(1f).Within(1e-5f));
            Assert.That(Vector3.Dot(frame.BladeAxis, Vector3.right), Is.EqualTo(1f).Within(1e-5f));
            Assert.That(frame.IsValid, Is.True);
        }

        [Test]
        public void BladeFrame_RejectsZeroAxis()
        {
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.zero, Vector3.up, Vector3.forward, Vector3.zero));
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.right, Vector3.zero, Vector3.forward, Vector3.zero));
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.right, Vector3.up, Vector3.zero, Vector3.zero));
        }

        [Test]
        public void BladeFrame_RejectsNonOrthogonalAxes()
        {
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.right, Vector3.right, Vector3.up, Vector3.zero));
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.right, Vector3.up, Vector3.up, Vector3.zero));
        }

        [Test]
        public void BladeFrame_RejectsNaNOrInfinity()
        {
            Assert.Throws<ArgumentException>(() => new BladeFrame(new Vector3(float.NaN, 0, 0), Vector3.up, Vector3.forward, Vector3.zero));
            Assert.Throws<ArgumentException>(() => new BladeFrame(new Vector3(float.PositiveInfinity, 0, 0), Vector3.up, Vector3.forward, Vector3.zero));
        }

        [Test]
        public void DefaultBladeFrame_IsInvalid()
        {
            Assert.That(default(BladeFrame).IsValid, Is.False);
        }

        [Test]
        public void BladeFrame_RejectsNonFiniteCutSamplePoint()
        {
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.right, Vector3.up, Vector3.forward, new Vector3(float.NaN, 0, 0)));
            Assert.Throws<ArgumentException>(() => new BladeFrame(Vector3.right, Vector3.up, Vector3.forward, new Vector3(float.PositiveInfinity, 0, 0)));
        }

        [Test]
        public void VeryShortAxis_IsNormalizedOrRejected()
        {
            // Above the threshold: becomes a valid unit axis.
            BladeFrame shortAxis = new BladeFrame(new Vector3(2e-6f, 0, 0), Vector3.up, Vector3.forward, Vector3.zero);
            Assert.That(shortAxis.IsValid, Is.True);
            Assert.That(shortAxis.BladeAxis.magnitude, Is.EqualTo(1f).Within(1e-4f));
            Assert.That(Vector3.Dot(shortAxis.BladeAxis, Vector3.right), Is.EqualTo(1f).Within(1e-4f));

            // Below the threshold: rejected.
            Assert.Throws<ArgumentException>(() => new BladeFrame(new Vector3(1e-7f, 0, 0), Vector3.up, Vector3.forward, Vector3.zero));
        }

        [Test]
        public void HugeAxisMagnitudeOverflow_IsRejected()
        {
            // A huge finite axis whose squared magnitude overflows to Infinity.
            Assert.Throws<ArgumentException>(() => new BladeFrame(new Vector3(2e19f, 0, 0), Vector3.up, Vector3.forward, Vector3.zero));
        }

        [Test]
        public void ConstructorSuccess_ImpliesIsValid()
        {
            BladeFrame frame = new BladeFrame(Vector3.right, Vector3.up, Vector3.forward, Vector3.zero);
            Assert.That(frame.IsValid, Is.True);

            BladeFrame scaled = new BladeFrame(Vector3.right * 100f, Vector3.up, Vector3.forward, new Vector3(1, 2, 3));
            Assert.That(scaled.IsValid, Is.True);
        }

        private static void AssertNoReferenceFields(Type type)
        {
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " is a reference type");
            }
        }
    }
}
