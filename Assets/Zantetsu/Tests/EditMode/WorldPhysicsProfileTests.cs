using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.Core.Tests
{
    public class WorldPhysicsProfileTests
    {
        private static readonly Vector3 ExpectedDefaultGravity = new Vector3(0f, -4.9f, 0f);
        private const float GravityTolerance = 1e-4f;

        [Test]
        public void DefaultGravity_IsPoCValue()
        {
            WorldPhysicsProfile profile = ScriptableObject.CreateInstance<WorldPhysicsProfile>();
            try
            {
                Assert.That(profile.Gravity.x, Is.EqualTo(ExpectedDefaultGravity.x).Within(GravityTolerance));
                Assert.That(profile.Gravity.y, Is.EqualTo(ExpectedDefaultGravity.y).Within(GravityTolerance));
                Assert.That(profile.Gravity.z, Is.EqualTo(ExpectedDefaultGravity.z).Within(GravityTolerance));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void DefaultProfileVersion_IsOne()
        {
            WorldPhysicsProfile profile = ScriptableObject.CreateInstance<WorldPhysicsProfile>();
            try
            {
                Assert.That(profile.ProfileVersion, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Apply_SetsPhysicsGravityToProfileValue()
        {
            WorldPhysicsProfile profile = ScriptableObject.CreateInstance<WorldPhysicsProfile>();
            Vector3 originalGravity = Physics.gravity;
            try
            {
                profile.Apply();

                Assert.That(Physics.gravity.x, Is.EqualTo(profile.Gravity.x).Within(GravityTolerance));
                Assert.That(Physics.gravity.y, Is.EqualTo(profile.Gravity.y).Within(GravityTolerance));
                Assert.That(Physics.gravity.z, Is.EqualTo(profile.Gravity.z).Within(GravityTolerance));
            }
            finally
            {
                Physics.gravity = originalGravity;
                Object.DestroyImmediate(profile);
            }
        }
    }
}
