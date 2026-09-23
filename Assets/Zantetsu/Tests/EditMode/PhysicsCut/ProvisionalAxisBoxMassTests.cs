using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    public unsafe class ProvisionalAxisBoxMassTests
    {
        [TestCase(0, 1f)] [TestCase(0, -1f)]
        [TestCase(1, 1f)] [TestCase(1, -1f)]
        [TestCase(2, 1f)] [TestCase(2, -1f)]
        public void InteriorAxisCutsMatchIndependentSlabMassAndCentroid(int axis, float sign)
        {
            var half = new float3(2, 4, 8);
            using (var source = new Source(half, float4x4.identity))
            {
                float3 normal = float3.zero;
                normal[axis] = sign;
                float cut = half[axis] * 0.25f;
                Assert.That(Divide(source, new float4(normal, -sign * cut), 12, out var positive, out var negative), Is.True);
                Assert.That(positive.mass, Is.EqualTo(sign > 0 ? 4.5 : 7.5).Within(1e-12));
                Assert.That(negative.mass, Is.EqualTo(12 - positive.mass));
                float3 positiveCenter = float3.zero, negativeCenter = float3.zero;
                positiveCenter[axis] = half[axis] * (sign > 0 ? .625f : -.375f);
                negativeCenter[axis] = half[axis] * (sign > 0 ? -.375f : .625f);
                Assert.That(math.length(positive.centerOfMass - positiveCenter), Is.LessThan(1e-6f));
                Assert.That(math.length(negative.centerOfMass - negativeCenter), Is.LessThan(1e-6f));
                Assert.That(positive.inertiaRotation, Is.EqualTo(quaternion.identity));
                Assert.That(negative.inertiaRotation, Is.EqualTo(quaternion.identity));
            }
        }

        [TestCase(-2f)] [TestCase(-1f)] [TestCase(1f)] [TestCase(2f)]
        public void FacesAndPlanesOutsideTheBoxCannotProduceTwoPositiveMasses(float cut)
        {
            using (var source = new Source(new float3(1), float4x4.identity))
            {
                foreach (float sign in new[] { 1f, -1f })
                    Assert.That(Divide(source, new float4(sign, 0, 0, -sign * cut), 12, out _, out _), Is.False);
            }
        }

        [Test]
        public void ARepresentableThinSlabIsNotClampedAway()
        {
            using (var source = new Source(new float3(1), float4x4.identity))
            {
                float cut = math.asfloat(math.asuint(1f) - 1u);
                Assert.That(Divide(source, new float4(1, 0, 0, -cut), 12, out var positive, out var negative), Is.True);
                double expected = 12 * ((1.0 - cut) / 2.0);
                Assert.That(positive.mass, Is.EqualTo(expected).Within(expected * 1e-12));
                Assert.That((float)positive.mass, Is.GreaterThan(0f));
                Assert.That(negative.mass, Is.EqualTo(12 - positive.mass));
                Assert.That(positive.centerOfMass.x, Is.EqualTo((float)((1.0 + cut) * .5)));
            }
        }

        [Test]
        public void ASlabBelowTheSubtractiveMassResolutionKeepsTheExistingRefusal()
        {
            using (var source = new Source(new float3(1), float4x4.identity, new float3(1, 0, 0)))
            {
                // The box spans x=[0,2]. The positive fraction rounds to one in double; parent-positive is zero.
                Assert.That(Divide(source, new float4(1, 0, 0, -1e-20f), 12, out _, out _), Is.False);
            }
        }

        [Test]
        public void APlaneWithANonzeroSmallSecondComponentStillUsesItsObliqueCentroid()
        {
            using (var source = new Source(new float3(1), float4x4.identity))
            {
                const float slope = 1e-5f;
                const float cut = .25f;
                Assert.That(Divide(source, new float4(1, slope, 0, -cut), 12, out var positive, out _), Is.True);
                // Integrate the slab width 1-cut+slope*y over y in [-1,1]. Its y first moment is 2*slope/3.
                double expectedY = slope / (3.0 * (1.0 - cut));
                Assert.That(positive.centerOfMass.y, Is.EqualTo(expectedY).Within(1e-7));
                Assert.That(positive.centerOfMass.y, Is.GreaterThan(1e-6f), "an axis approximation would return zero");
            }
        }

        [Test]
        public void AnAxisPlaneInATurnedShapeIsObliqueInTheOwnerFrame()
        {
            var frame = float4x4.RotateZ(math.PI * .25f);
            using (var source = new Source(new float3(1), frame))
            {
                Assert.That(ProvisionalBoxMass.TryBox(source.Shape, out _, out float3 hi), Is.True);
                Assert.That(Divide(source, new float4(1, 0, 0, 0), 12, out var positive, out var negative), Is.True);
                // x+y>0 halves an owner-frame square into a triangle, whose centroid is (extent/3, extent/3).
                Assert.That(positive.mass, Is.EqualTo(6).Within(1e-5));
                Assert.That(positive.centerOfMass.x, Is.EqualTo(hi.x / 3).Within(2e-5));
                Assert.That(positive.centerOfMass.y, Is.EqualTo(hi.y / 3).Within(2e-5));
                Assert.That(math.length(positive.centerOfMass + negative.centerOfMass), Is.LessThan(2e-5f));
            }
        }

        [TestCase(1e20f)] [TestCase(1e-20f)]
        [TestCase(1e13f)] [TestCase(1e-16f)]
        public void VolumeProductsUseDoubleBeforeMultiplication(float halfExtent)
        {
            using (var source = new Source(new float3(halfExtent), float4x4.identity))
            {
                // Keep the tiny box's inertia in the normal float range, so this tests volume arithmetic without
                // depending on the runtime's treatment of subnormal inertia values.
                double parentMass = halfExtent < 1f ? 1e20 : 12;
                Assert.That(Divide(source, new float4(0, 1, 0, -halfExtent * .25f), parentMass, out var positive, out var negative), Is.True);
                Assert.That(positive.mass, Is.EqualTo(parentMass * .375).Within(parentMass * 1e-7));
                Assert.That(negative.mass, Is.EqualTo(parentMass * .625).Within(parentMass * 1e-7));
                Assert.That(positive.centerOfMass.y / halfExtent, Is.EqualTo(.625f).Within(1e-6f));
                Assert.That(negative.centerOfMass.y / halfExtent, Is.EqualTo(-.375f).Within(1e-6f));
                Assert.That(math.all(positive.inertia > 0f), Is.True);
            }
        }

        [Test]
        public void ZeroVolumeKeepsTheEqualMassAndCenterFallback()
        {
            using (var source = new Source(new float3(1, 0, 1), float4x4.identity))
            {
                Assert.That(Divide(source, new float4(1, 0, 0, -.25f), 12, out var positive, out var negative), Is.True);
                Assert.That(positive.mass, Is.EqualTo(6));
                Assert.That(negative.mass, Is.EqualTo(6));
                Assert.That(positive.centerOfMass, Is.EqualTo(float3.zero));
                Assert.That(negative.centerOfMass, Is.EqualTo(float3.zero));
            }
        }

        [TestCase(1e40)] [TestCase(1e-60)]
        public void MassMustRemainPositiveAndFiniteAsFloat(double parentMass)
        {
            using (var source = new Source(new float3(1), float4x4.identity))
                Assert.That(Divide(source, new float4(1, 0, 0, -.25f), parentMass, out _, out _), Is.False);
        }

        [TestCase(5.444517708475739e38)] [TestCase(1e-31)]
        public void ExtremeParentMassKeepsTheLegacyRatioRounding(double parentMass)
        {
            using (var source = new Source(new float3(1), float4x4.identity))
            {
                // This fixed box and plane integrated to this ratio before the fast path. The high-mass case's
                // negative child becomes float.MaxValue with that rounding, but infinity with exact 3/8.
                const double legacyRatio = .37500000000000006;
                double expectedPositive = parentMass * legacyRatio;
                Assert.That(Divide(source, new float4(0, 0, 1, -.25f), parentMass,
                    out var positive, out var negative), Is.True);
                Assert.That(positive.mass, Is.EqualTo(expectedPositive));
                Assert.That(negative.mass, Is.EqualTo(parentMass - expectedPositive));
                Assert.That(math.isfinite((float)negative.mass), Is.True);
                if (parentMass > float.MaxValue)
                    Assert.That((float)negative.mass, Is.EqualTo(float.MaxValue));
            }
        }

        [Test]
        public void OverflowingBoxInertiaKeepsTheSourceInertiaAndItsOrientation()
        {
            using (var source = new Source(new float3(1e20f), float4x4.identity))
            {
                quaternion rotation = quaternion.RotateZ(.7f);
                var inertia = new float3(4, 8, 12);
                Assert.That(ProvisionalBoxMass.TryDivide(source.Shape, new float4(1, 0, 0, -.25e20f),
                    12, inertia, rotation, out var positive, out var negative, out _), Is.True);
                Assert.That(math.length(positive.inertia - inertia * (float)(positive.mass / 12)), Is.LessThan(1e-6f));
                Assert.That(math.length(negative.inertia - inertia * (float)(negative.mass / 12)), Is.LessThan(1e-6f));
                Assert.That(math.abs(math.dot(positive.inertiaRotation.value, rotation.value)), Is.GreaterThan(.99999f));
            }
        }

        [Test]
        public void InertiaAtTheFloatOverflowBoundaryKeepsTheLegacyFallback()
        {
            const float halfExtent = 18446744073709551616f; // 2^64, exactly representable.
            const double parentMass = 3.99999988079071;
            using (var source = new Source(new float3(halfExtent), float4x4.identity))
            {
                quaternion rotation = quaternion.RotateZ(.7f);
                Assert.That(ProvisionalBoxMass.TryDivide(source.Shape,
                    new float4(0, 0, 1, -4611686018427387904f), parentMass, new float3(4), rotation,
                    out var positive, out var negative, out _), Is.True);
                // Legacy 0.37500000000000006 overflows the box inertia; exact 3/8 rounds to float.MaxValue.
                // Both masses themselves are ordinary. The existing source-inertia fallback must still apply.
                Assert.That(positive.inertia, Is.EqualTo(new float3(1.5f)));
                Assert.That(negative.inertia, Is.EqualTo(new float3(2.5f)));
                Assert.That(math.abs(math.dot(positive.inertiaRotation.value, rotation.value)), Is.GreaterThan(.99999f));
                Assert.That(math.abs(math.dot(negative.inertiaRotation.value, rotation.value)), Is.GreaterThan(.99999f));
            }
        }

        private static bool Divide(Source source, float4 plane, double mass,
            out ProvisionalBoxMass.Side positive, out ProvisionalBoxMass.Side negative)
            => ProvisionalBoxMass.TryDivide(source.Shape, plane, mass, new float3(4), quaternion.identity,
                out positive, out negative, out _);

        // Only the shape's box is read by this path. Use a real B-rep's tables, with its vertices scaled into the
        // diagnostic box, and an enclosing mesh; no collider cooking or actor creation is needed for mass tests.
        private sealed class Source : IDisposable
        {
            internal PhysicsOwnerShape Shape;
            private OwnerCutHarness _harness;
            private Mesh _mesh;

            internal Source(float3 half, float4x4 frame, float3 center = default)
            {
                try
                {
                    _harness = new OwnerCutHarness();
                    _harness.Add(CaseGenerator.Box());
                    _harness.Build();
                    var range = _harness.input.convexes[0];
                    float3 max = float3.zero;
                    for (int i = 0; i < range.vertexCount; i++)
                        max = math.max(max, math.abs(_harness.input.bank.vertices[range.vertexBase + i]));
                    var vertices = new Vector3[range.vertexCount];
                    for (int i = 0; i < range.vertexCount; i++)
                    {
                        float3 at = _harness.input.bank.vertices[range.vertexBase + i] / max * half + center;
                        _harness.input.bank.vertices[range.vertexBase + i] = at;
                        vertices[i] = at;
                    }
                    _mesh = new Mesh { vertices = vertices, bounds = new Bounds((Vector3)center, (Vector3)(half * 2)) };
                    Shape = PhysicsOwnerShape.Authored(_harness.input.bank, new[] { range }, new[] { _mesh },
                        PhysicsShapeSource.External(), frame);
                }
                catch { Dispose(); throw; }
            }

            public void Dispose()
            {
                Shape?.Dispose();
                if (_mesh != null) UnityEngine.Object.DestroyImmediate(_mesh);
                _harness?.Dispose();
            }
        }
    }
}
