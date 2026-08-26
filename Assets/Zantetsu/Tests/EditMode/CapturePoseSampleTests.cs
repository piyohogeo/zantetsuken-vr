using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CapturePoseSampleTests
    {
        private const float Tolerance = 1e-5f;

        private static void AssertNoReferenceFields(Type type)
        {
            Assert.That(type.IsValueType, Is.True);
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "Reference-type field: " + field.Name);
            }
        }

        private static void AssertRejectedForPosition(float x, float y, float z)
        {
            Assert.Throws<ArgumentException>(() => new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity));
        }

        private static void AssertRejectedForRotation(float x, float y, float z, float w)
        {
            Assert.Throws<ArgumentException>(() => new CapturePoseSample(Vector3.zero, new Quaternion(x, y, z, w)));
        }

        [Test]
        public void Default_And_Unavailable_AreNotAvailable()
        {
            Assert.That(default(CapturePoseSample).IsAvailable, Is.False);
            Assert.That(CapturePoseSample.Unavailable.IsAvailable, Is.False);
        }

        [Test]
        public void Unavailable_IsDefaultPose_NotIdentity()
        {
            CapturePoseSample unavailable = CapturePoseSample.Unavailable;

            Assert.That(unavailable.Pose.position, Is.EqualTo(Vector3.zero));
            Assert.That(unavailable.Pose.rotation, Is.EqualTo(default(Quaternion)));
            Assert.That(unavailable.Rotation, Is.Not.EqualTo(Quaternion.identity));
        }

        [Test]
        public void FinitePosition_IdentityRotation_Succeeds()
        {
            Vector3 position = new Vector3(1.0f, 2.0f, -3.5f);

            CapturePoseSample sample = new CapturePoseSample(position, Quaternion.identity);

            Assert.That(sample.IsAvailable, Is.True);
            Assert.That(sample.Position, Is.EqualTo(position));
            Assert.That(sample.Rotation, Is.EqualTo(Quaternion.identity));
        }

        [Test]
        public void NonUnitQuaternion_IsNormalized()
        {
            CapturePoseSample sample = new CapturePoseSample(Vector3.zero, new Quaternion(2.0f, 0.0f, 0.0f, 0.0f));

            Assert.That(sample.IsAvailable, Is.True);
            Assert.That(sample.Rotation.x, Is.EqualTo(1.0f).Within(Tolerance));
            Assert.That(sample.Rotation.y, Is.EqualTo(0.0f).Within(Tolerance));
            Assert.That(sample.Rotation.z, Is.EqualTo(0.0f).Within(Tolerance));
            Assert.That(sample.Rotation.w, Is.EqualTo(0.0f).Within(Tolerance));
        }

        [Test]
        public void NormalizedRotation_HasUnitLength()
        {
            Quaternion input = new Quaternion(3.0f, -4.0f, 5.0f, 6.0f);

            CapturePoseSample sample = new CapturePoseSample(new Vector3(1.0f, 2.0f, 3.0f), input);

            float lengthSq = Quaternion.Dot(sample.Rotation, sample.Rotation);
            Assert.That(lengthSq, Is.EqualTo(1.0f).Within(Tolerance));
        }

        [Test]
        public void Position_NaN_Rejected_PerComponent()
        {
            AssertRejectedForPosition(float.NaN, 0.0f, 0.0f);
            AssertRejectedForPosition(0.0f, float.NaN, 0.0f);
            AssertRejectedForPosition(0.0f, 0.0f, float.NaN);
        }

        [Test]
        public void Position_Infinity_Rejected_PerComponent()
        {
            AssertRejectedForPosition(float.PositiveInfinity, 0.0f, 0.0f);
            AssertRejectedForPosition(0.0f, float.NegativeInfinity, 0.0f);
            AssertRejectedForPosition(0.0f, 0.0f, float.PositiveInfinity);
        }

        [Test]
        public void Rotation_NaN_Rejected_PerComponent()
        {
            AssertRejectedForRotation(float.NaN, 0.0f, 0.0f, 1.0f);
            AssertRejectedForRotation(0.0f, float.NaN, 0.0f, 1.0f);
            AssertRejectedForRotation(0.0f, 0.0f, float.NaN, 1.0f);
            AssertRejectedForRotation(0.0f, 0.0f, 0.0f, float.NaN);
        }

        [Test]
        public void Rotation_Infinity_Rejected_PerComponent()
        {
            AssertRejectedForRotation(float.PositiveInfinity, 0.0f, 0.0f, 1.0f);
            AssertRejectedForRotation(0.0f, float.NegativeInfinity, 0.0f, 1.0f);
            AssertRejectedForRotation(0.0f, 0.0f, float.PositiveInfinity, 1.0f);
            AssertRejectedForRotation(0.0f, 0.0f, 0.0f, float.NegativeInfinity);
        }

        [Test]
        public void ZeroQuaternion_Rejected()
        {
            AssertRejectedForRotation(0.0f, 0.0f, 0.0f, 0.0f);
        }

        [Test]
        public void TinyQuaternion_Rejected()
        {
            AssertRejectedForRotation(1e-20f, 1e-20f, 1e-20f, 1e-20f);
        }

        [Test]
        public void HugeFiniteComponents_Overflow_Rejected()
        {
            AssertRejectedForRotation(float.MaxValue, float.MaxValue, float.MaxValue, float.MaxValue);
        }

        [Test]
        public void ConstructorSuccess_IsAvailable()
        {
            CapturePoseSample sample = new CapturePoseSample(new Vector3(1.0f, 2.0f, 3.0f), new Quaternion(0.0f, 0.0f, 0.0f, 1.0f));

            Assert.That(sample.IsAvailable, Is.True);
        }

        [Test]
        public void ReadonlyStruct_NoReferenceFields()
        {
            AssertNoReferenceFields(typeof(CapturePoseSample));
        }

        [Test]
        public void PositionRotation_MatchPose()
        {
            Vector3 position = new Vector3(-4.0f, 0.5f, 9.0f);
            Quaternion rotation = new Quaternion(0.0f, 1.0f, 0.0f, 0.0f);

            CapturePoseSample sample = new CapturePoseSample(position, rotation);

            Assert.That(sample.Position, Is.EqualTo(sample.Pose.position));
            Assert.That(sample.Rotation, Is.EqualTo(sample.Pose.rotation));
        }
    }
}
