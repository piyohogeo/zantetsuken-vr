using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// A world cut plane read in a geometry's own coordinates (DESIGN 19.5.1). What is checked is what the conversion
    /// promises: a point on the plane is still on it afterwards, each point keeps the side it was on, and the normal
    /// and d come out normalized together. The signed distance itself is **not** required to survive — it comes out
    /// divided by the transformed normal's length — so nothing here compares distances between the two spaces.
    /// </summary>
    public class VpCutPlaneTests
    {
        private const float OnPlaneTolerance = 1e-4f;

        /// <summary>The transforms a geometry may plausibly be placed by, each with a name for the failure message.</summary>
        private static readonly (string name, Matrix4x4 localToWorld)[] k_placements =
        {
            ("identity", Matrix4x4.identity),
            ("translation", Matrix4x4.Translate(new Vector3(3.5f, -2.25f, 7f))),
            ("rotation", Matrix4x4.Rotate(Quaternion.Euler(23f, -47f, 61f))),
            ("uniform scale", Matrix4x4.Scale(new Vector3(2.5f, 2.5f, 2.5f))),
            ("non-uniform scale", Matrix4x4.Scale(new Vector3(2f, 0.5f, 1.75f))),
            ("moved, turned and scaled", Matrix4x4.TRS(
                new Vector3(-1.5f, 4f, 0.75f),
                Quaternion.Euler(15f, 80f, -35f),
                new Vector3(1.25f, 3f, 0.4f))),
        };

        /// <summary>A plane that is not axis-aligned and does not pass through the origin, as (n, d) with |n| = 1.</summary>
        private static float4 WorldPlane()
        {
            float3 n = math.normalize(new float3(0.37f, 0.61f, -0.7f));
            return new float4(n, -math.dot(n, new float3(0.9f, -0.4f, 1.3f)));
        }

        /// <summary>Points that lie exactly on <paramref name="plane"/>, spread over it.</summary>
        private static float3[] PointsOn(float4 plane)
        {
            float3 n = math.normalize(plane.xyz);
            float3 origin = -n * (plane.w / math.length(plane.xyz));
            float3 u = math.normalize(math.cross(n, math.abs(n.x) < 0.9f ? new float3(1, 0, 0) : new float3(0, 1, 0)));
            float3 v = math.cross(n, u);
            return new[]
            {
                origin,
                origin + u * 2.3f,
                origin - u * 0.75f + v * 1.4f,
                origin + v * 3.1f,
                origin - u * 2f - v * 2.6f,
            };
        }

        private static float SignedDistance(float4 plane, float3 point)
        {
            return math.dot(plane.xyz, point) + plane.w;
        }

        private static float3 ToLocal(Matrix4x4 localToWorld, float3 worldPoint)
        {
            Vector3 local = localToWorld.inverse.MultiplyPoint3x4(new Vector3(worldPoint.x, worldPoint.y, worldPoint.z));
            return new float3(local.x, local.y, local.z);
        }

        [Test]
        public void APointOnTheWorldPlane_IsStillOnTheLocalPlane([ValueSource(nameof(k_placements))] (string name, Matrix4x4 localToWorld) placement)
        {
            float4 world = WorldPlane();
            Assert.That(
                VpCutPlane.TryWorldToGeometryLocal(world, placement.localToWorld, out float4 local),
                Is.True,
                placement.name + ": convert");

            foreach (float3 onPlane in PointsOn(world))
            {
                float3 localPoint = ToLocal(placement.localToWorld, onPlane);
                Assert.That(
                    math.abs(SignedDistance(local, localPoint)),
                    Is.LessThan(OnPlaneTolerance),
                    placement.name + ": a point on the world plane is on the local plane");
            }
        }

        [Test]
        public void EachSideOfThePlane_KeepsItsSign([ValueSource(nameof(k_placements))] (string name, Matrix4x4 localToWorld) placement)
        {
            float4 world = WorldPlane();
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, placement.localToWorld, out float4 local), Is.True, placement.name + ": convert");

            float3 n = math.normalize(world.xyz);
            foreach (float3 onPlane in PointsOn(world))
            {
                foreach (float offset in new[] { 0.35f, 1.9f, -0.35f, -1.9f })
                {
                    float3 worldPoint = onPlane + n * offset;
                    float worldSide = SignedDistance(world, worldPoint);
                    float localSide = SignedDistance(local, ToLocal(placement.localToWorld, worldPoint));
                    Assert.That(
                        math.sign(localSide),
                        Is.EqualTo(math.sign(worldSide)),
                        placement.name + ": the point " + offset + " from the plane keeps its side");
                    Assert.That(math.abs(localSide), Is.GreaterThan(OnPlaneTolerance), placement.name + ": and is not on the plane");
                }
            }
        }

        /// <summary>
        /// The normal comes out unit length and d is divided by the same value, which is what keeps the plane where it
        /// is. A world plane scaled by any positive factor is the same plane, and converts to the same one.
        /// </summary>
        [Test]
        public void TheNormalAndD_AreNormalizedTogether([ValueSource(nameof(k_placements))] (string name, Matrix4x4 localToWorld) placement)
        {
            float4 world = WorldPlane();
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, placement.localToWorld, out float4 local), Is.True, placement.name + ": convert");
            Assert.That(math.length(local.xyz), Is.EqualTo(1f).Within(1e-5f), placement.name + ": the normal is unit length");

            // The same plane, written with a longer normal: the conversion must not move it.
            float4 scaled = world * 7.5f;
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(scaled, placement.localToWorld, out float4 fromScaled), Is.True, placement.name + ": convert the scaled plane");
            Assert.That(math.length(fromScaled.xyz), Is.EqualTo(1f).Within(1e-5f), placement.name + ": still unit length");
            Assert.That(math.distance(fromScaled.xyz, local.xyz), Is.LessThan(1e-4f), placement.name + ": the same normal");
            Assert.That(math.abs(fromScaled.w - local.w), Is.LessThan(1e-4f), placement.name + ": and the same d");
        }

        /// <summary>Two geometries placed differently read the same world plane as two different local planes, each right for its own.</summary>
        [Test]
        public void TwoPlacements_EachGetTheirOwnLocalPlane()
        {
            float4 world = WorldPlane();
            Matrix4x4 first = Matrix4x4.TRS(new Vector3(2f, 0f, 0f), Quaternion.Euler(0f, 30f, 0f), Vector3.one);
            Matrix4x4 second = Matrix4x4.TRS(new Vector3(-3f, 1f, 2f), Quaternion.Euler(45f, 0f, 10f), new Vector3(2f, 1f, 1f));

            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, first, out float4 firstLocal), Is.True, "the first");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, second, out float4 secondLocal), Is.True, "the second");
            Assert.That(math.distance(firstLocal, secondLocal), Is.GreaterThan(1e-3f), "two placements, two local planes");

            foreach (float3 onPlane in PointsOn(world))
            {
                Assert.That(math.abs(SignedDistance(firstLocal, ToLocal(first, onPlane))), Is.LessThan(OnPlaneTolerance), "the first is right for its own placement");
                Assert.That(math.abs(SignedDistance(secondLocal, ToLocal(second, onPlane))), Is.LessThan(OnPlaneTolerance), "and the second for its own");
            }
        }

        [Test]
        public void AnInvalidPlane_IsRefused()
        {
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), Vector3.one);
            var zeroNormal = new float4(0f, 0f, 0f, 1.5f);
            var notFinite = new float4(float.NaN, 1f, 0f, 0f);
            var infinite = new float4(1f, 0f, 0f, float.PositiveInfinity);

            Assert.That(VpCutPlane.TryWorldToGeometryLocal(zeroNormal, placement, out float4 a), Is.False, "a zero normal");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(notFinite, placement, out float4 b), Is.False, "a normal that is not finite");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(infinite, placement, out float4 c), Is.False, "a d that is not finite");
            Assert.That(new[] { a, b, c }, Is.All.EqualTo(default(float4)), "and nothing is handed back");

            // the arguments are untouched
            Assert.That(zeroNormal, Is.EqualTo(new float4(0f, 0f, 0f, 1.5f)));
            Assert.That(placement, Is.EqualTo(Matrix4x4.TRS(new Vector3(1f, 2f, 3f), Quaternion.Euler(10f, 20f, 30f), Vector3.one)));
        }

        [Test]
        public void AnInvalidTransform_IsRefused()
        {
            float4 world = WorldPlane();

            var notFinite = Matrix4x4.identity;
            notFinite[0, 0] = float.NaN;

            // A projective matrix: this form of plane transform does not describe it, so it is refused, not approximated.
            var projective = Matrix4x4.identity;
            projective[3, 2] = 0.5f;

            // Flattened onto the xz plane. What this refuses is decided by the normal, not by the transform alone: a
            // normal along the axis being flattened transposes to nothing, and there is no plane left to hand back.
            Matrix4x4 flattening = Matrix4x4.Scale(new Vector3(1f, 0f, 1f));
            var alongTheFlattenedAxis = new float4(0f, 1f, 0f, -2f);

            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, notFinite, out float4 a), Is.False, "a matrix that is not finite");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, projective, out float4 b), Is.False, "a projective matrix");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(alongTheFlattenedAxis, flattening, out float4 c), Is.False, "a normal flattened to nothing");
            Assert.That(new[] { a, b, c }, Is.All.EqualTo(default(float4)), "and nothing is handed back");

            // "Exactly (0, 0, 0, 1)" means exactly. Vector4's own equality is approximate and would take this row for
            // the identity's, so the check compares value by value and this is refused like any other projective one.
            var nearlyAffine = Matrix4x4.identity;
            nearlyAffine.m32 = 1e-6f;
            nearlyAffine.m33 = 1f + 1e-6f;
            Assert.That(nearlyAffine.GetRow(3) == new Vector4(0f, 0f, 0f, 1f), Is.True, "Vector4's == cannot tell this row from the identity's");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, nearlyAffine, out float4 d), Is.False, "a last row that is only nearly (0, 0, 0, 1)");
            Assert.That(d, Is.EqualTo(default(float4)), "and nothing is handed back for it either");

            // The same flattening transform refuses only where it must: a normal it does not flatten still describes a
            // plane in the geometry's own coordinates, and that plane is handed back.
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, flattening, out float4 survives), Is.True, "a normal it does not flatten");
            Assert.That(math.length(survives.xyz), Is.EqualTo(1f).Within(1e-5f));
        }

        [Test]
        public void TheIdentityPlacement_LeavesAnAlreadyNormalizedPlaneAlone()
        {
            float4 world = WorldPlane();
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, Matrix4x4.identity, out float4 local), Is.True, "convert");
            Assert.That(math.distance(local.xyz, world.xyz), Is.LessThan(1e-6f), "the same normal");
            Assert.That(math.abs(local.w - world.w), Is.LessThan(1e-6f), "and the same d");
        }

        // ----- the other direction: a plane given in the geometry's frame, as a plane in world -------------------

        /// <summary>
        /// A point's side of the plane is what has to survive the conversion. This asks it directly: a point taken in
        /// the geometry's own frame, and the same point placed in world, must fall on the same side of the two planes.
        /// </summary>
        private static void AssertSidesSurvive(Matrix4x4 placement, float4 localPlane, float3 localPoint, string what)
        {
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(localPlane, placement, out float4 worldPlane), Is.True,
                what + ": the plane converts");

            float localSide = math.dot(localPlane.xyz, localPoint) + localPlane.w;
            float3 worldPoint = (float3)placement.MultiplyPoint3x4((Vector3)localPoint);
            float worldSide = math.dot(worldPlane.xyz, worldPoint) + worldPlane.w;

            Assert.That(math.abs(localSide), Is.GreaterThan(1e-4f), what + ": the point is not on the plane");
            Assert.That(
                math.sign(worldSide), Is.EqualTo(math.sign(localSide)),
                what + ": the point stays on the side it was on (local " + localSide + ", world " + worldSide + ")");
            Assert.That(math.length(worldPlane.xyz), Is.EqualTo(1f).Within(1e-5f), what + ": the normal is normalized");
        }

        /// <summary>
        /// The sign is never flipped, whatever the placement does to lengths or to handedness: a non-uniform scale and
        /// a mirroring one both keep each point on the side it was on.
        /// </summary>
        [Test]
        public void TheOtherDirection_KeepsBothSides_UnderNonUniformScaleAndMirroring()
        {
            var plane = new float4(0f, 1f, 0f, -1f);
            var above = new float3(0.3f, 1.7f, -0.2f);
            var below = new float3(-0.4f, 0.25f, 0.6f);

            Matrix4x4 nonUniform = Matrix4x4.TRS(
                new Vector3(2f, -3f, 0.5f), Quaternion.Euler(15f, -40f, 75f), new Vector3(0.25f, 3.5f, 1.75f));
            AssertSidesSurvive(nonUniform, plane, above, "non-uniform scale, above");
            AssertSidesSurvive(nonUniform, plane, below, "non-uniform scale, below");

            // A mirroring placement: its determinant is negative, and the sides still do not change hands.
            Matrix4x4 mirrored = Matrix4x4.TRS(
                new Vector3(-1f, 2f, 3f), Quaternion.Euler(0f, 30f, 0f), new Vector3(1f, -2f, 1f));
            Assert.That(mirrored.determinant, Is.LessThan(0f), "the placement really does mirror");
            AssertSidesSurvive(mirrored, plane, above, "mirrored, above");
            AssertSidesSurvive(mirrored, plane, below, "mirrored, below");

            // A plane whose normal is not an axis, through a placement that scales that direction unevenly.
            var slanted = new float4(math.normalize(new float3(0.4f, -0.7f, 0.59f)), 0.23f);
            AssertSidesSurvive(nonUniform, slanted, above, "slanted plane, above");
            AssertSidesSurvive(nonUniform, slanted, below, "slanted plane, below");
        }

        /// <summary>
        /// A placement that cannot be inverted has no world plane to give back, and neither has a projective one or a
        /// plane with no normal. Nothing is invented for any of them.
        /// </summary>
        [Test]
        public void TheOtherDirection_RefusesASingularOrProjectivePlacement()
        {
            var plane = new float4(0f, 1f, 0f, -1f);

            // Flattened onto a plane: the placement is singular, and there is no way back from it.
            Matrix4x4 singular = Matrix4x4.Scale(new Vector3(1f, 0f, 1f));
            Assert.That(singular.determinant, Is.EqualTo(0f), "the placement is singular");
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(plane, singular, out float4 fromSingular), Is.False,
                "a singular placement is refused");
            Assert.That(fromSingular, Is.EqualTo(default(float4)), "and nothing is handed back");

            // Wholly degenerate, for the same reason.
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(plane, Matrix4x4.zero, out float4 fromZero), Is.False,
                "a zero placement is refused");
            Assert.That(fromZero, Is.EqualTo(default(float4)));

            // Projective placements are not what this API is for, in either direction.
            var projective = Matrix4x4.identity;
            projective.m30 = 0.5f;
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(plane, projective, out float4 fromProjective), Is.False,
                "a projective placement is refused");
            Assert.That(fromProjective, Is.EqualTo(default(float4)));

            // And a plane with no normal is no plane.
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(new float4(0f, 0f, 0f, 1f), Matrix4x4.identity, out float4 fromNoNormal),
                Is.False,
                "a plane with no normal is refused");
            Assert.That(fromNoNormal, Is.EqualTo(default(float4)));

            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(new float4(0f, float.NaN, 0f, 1f), Matrix4x4.identity, out _), Is.False,
                "and so is one that is not finite");
        }

        /// <summary>The two directions are each other's inverse: a plane converted and converted back is the same one.</summary>
        [Test]
        public void TheTwoDirections_AreEachOthersInverse()
        {
            var local = new float4(math.normalize(new float3(0.2f, 0.9f, -0.35f)), -0.75f);
            Matrix4x4 placement = Matrix4x4.TRS(
                new Vector3(1.5f, 0.25f, -2f), Quaternion.Euler(-20f, 65f, 10f), new Vector3(2f, 0.5f, 1.25f));

            Assert.That(VpCutPlane.TryGeometryLocalToWorld(local, placement, out float4 world), Is.True, "out to world");
            Assert.That(VpCutPlane.TryWorldToGeometryLocal(world, placement, out float4 back), Is.True, "and back again");

            Assert.That(math.distance(back.xyz, local.xyz), Is.LessThan(1e-5f), "the same normal");
            Assert.That(math.abs(back.w - local.w), Is.LessThan(1e-5f), "and the same d");
        }
    }
}
