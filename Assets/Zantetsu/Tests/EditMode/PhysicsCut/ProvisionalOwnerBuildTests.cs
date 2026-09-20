using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The unpublished Provisional pair of one accepted cut (DESIGN 7.1.1, 7.2, 7.6): the source's own cooked
    /// convexes allocated to two sides and shared, temporary masses from a conservative box, the first split's
    /// motion, and the sibling constraint — all of it inactive, with the source untouched.
    /// <para>
    /// The expected numbers are worked out here from the source geometry, not read back from the builder. The
    /// ordinary source is the 4-gon prism of <see cref="CaseGenerator.Box"/> — half-extents <see cref="Radius"/> in x
    /// and z, 1 in y — and each convex's collider mesh is built from that same polytope, so the B-rep the mass is
    /// taken over, the placement, and the collider all describe one shape. The degenerate sources further down are
    /// diagnostic inputs made on purpose, and are kept apart from the ordinary ones.
    /// </para>
    /// <para>
    /// **What these tests do not say.** Nothing here enters the physics scene, so nothing is stepped: the sibling
    /// constraint is checked as the values it was configured with, not as an effect on any solver, and DESIGN 7.1.1
    /// asks for no such guarantee. An inactive Rigidbody keeps none of the mass properties or velocities given to it,
    /// so those are checked on the candidate's own side records — which is not the same as checking a body that has
    /// them. The lease that has to outlive the physics steps a published pair needs is not exercised at all: what is
    /// checked is that an unpublished pair takes its mesh holds and gives them back.
    /// </para>
    /// </summary>
    public unsafe class ProvisionalOwnerBuildTests
    {
        /// <summary>The prism's half-extent in x and z (<see cref="CaseGenerator.Prism"/> at radius 1).</summary>
        private const float Radius = 0.70710678f;

        private const double ParentMass = 12.0;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void Cleanup()
        {
            ProvisionalOwnerBuilder.sideBuiltHook = null;
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
        }

        // ----- a source to cut ------------------------------------------------------------------------------------

        /// <summary>One authored source owner's shape: its convexes, a cooked mesh each, and its own frame.</summary>
        private sealed class Source : IDisposable
        {
            internal OwnerCutHarness harness;
            internal PhysicsOwnerShape shape;
            internal List<Mesh> meshes;
            internal PhysicsShapeSource source;

            public void Dispose()
            {
                shape?.Dispose();
                if (meshes != null)
                {
                    foreach (Mesh mesh in meshes)
                    {
                        if (mesh != null)
                        {
                            UnityEngine.Object.DestroyImmediate(mesh);
                        }
                    }
                }

                harness?.Dispose();
            }
        }

        /// <summary>
        /// A source of one prism per offset, at the frame given. Each convex's collider mesh is cooked from **that
        /// convex's own polytope**, so the B-rep and the collider describe the same shape in the same place.
        /// </summary>
        private Source NewSource(float4x4 localToOwner, params double3[] offsets)
        {
            var polys = new List<ConvexPoly>();
            foreach (double3 offset in offsets)
            {
                polys.Add(Translated(CaseGenerator.Box(), offset));
            }

            return NewSource(localToOwner, polys, null);
        }

        /// <summary>
        /// The general form. <paramref name="ranges"/> replaces the convexes the shape is made of, which is how the
        /// degenerate diagnostic shapes below are made; the meshes then still come from the whole polytopes, since
        /// what those cases are about is the box, not the collider.
        /// </summary>
        private Source NewSource(float4x4 localToOwner, List<ConvexPoly> polys, ConvexBrepRange[] ranges)
        {
            var s = new Source { harness = new OwnerCutHarness() };
            s.harness.planeN = new float3(0f, 1f, 0f);
            s.harness.planeW = 0f;
            s.harness.eps = 1e-5f;
            s.harness.parentMass = ParentMass;
            foreach (ConvexPoly poly in polys)
            {
                s.harness.Add(poly);
            }

            s.harness.Build();

            if (ranges == null)
            {
                ranges = new ConvexBrepRange[s.harness.input.convexCount];
                for (int c = 0; c < ranges.Length; c++)
                {
                    ranges[c] = s.harness.input.convexes[c];
                }
            }

            s.meshes = new List<Mesh>();
            for (int i = 0; i < ranges.Length; i++)
            {
                s.meshes.Add(CookedConvex(polys[math.min(i, polys.Count - 1)], "Source " + i));
            }

            s.source = PhysicsShapeSource.External();
            s.shape = PhysicsOwnerShape.Authored(s.harness.input.bank, ranges, s.meshes, s.source, localToOwner);
            _disposables.Add(s);
            return s;
        }

        /// <summary>The convex collider mesh of one polytope: its own vertices, its own faces, fanned and baked.</summary>
        private static Mesh CookedConvex(ConvexPoly poly, string name)
        {
            var vertices = new Vector3[poly.V.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                vertices[i] = (float3)poly.V[i];
            }

            var triangles = new List<int>();
            foreach (int[] face in poly.F)
            {
                for (int i = 2; i < face.Length; i++)
                {
                    triangles.Add(face[0]);
                    triangles.Add(face[i - 1]);
                    triangles.Add(face[i]);
                }
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.triangles = triangles.ToArray();
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            return mesh;
        }

        private static ConvexPoly Translated(ConvexPoly poly, double3 by)
        {
            var moved = new double3[poly.V.Length];
            for (int i = 0; i < poly.V.Length; i++)
            {
                moved[i] = poly.V[i] + by;
            }

            return new ConvexPoly(moved, poly.F);
        }

        /// <summary>
        /// The 7.6 disposition of each convex of a shape against a plane in its own frame, the way the admitting side
        /// settles it: support on both sides is Split, on one side only is that side, on neither is near-plane.
        /// </summary>
        private static ConvexSide[] Classify(Source s, float4 planeLocal, float epsilon)
        {
            var sides = new ConvexSide[s.shape.ConvexCount];
            for (int c = 0; c < sides.Length; c++)
            {
                ConvexBrepRange range = s.shape.Convex(c);
                int positive = 0, negative = 0;
                for (int i = 0; i < range.vertexCount; i++)
                {
                    float3 v = s.shape.Bank.vertices[range.vertexBase + i];
                    float d = math.dot(planeLocal.xyz, v) + planeLocal.w;
                    if (d > epsilon)
                    {
                        positive++;
                    }
                    else if (d < -epsilon)
                    {
                        negative++;
                    }
                }

                sides[c] = positive > 0 && negative > 0 ? ConvexSide.Split
                    : negative > 0 ? ConvexSide.Negative
                    : positive > 0 ? ConvexSide.Positive
                    : ConvexSide.NearPlaneToPositive;
            }

            return sides;
        }

        private static AnchorDistributionResult Anchors(params float3[] anchors)
        {
            FixedSupportAnchors.TryDistribute(
                anchors, new float4(0f, 1f, 0f, 0f), 1e-5f, new List<float3>(), new List<float3>(),
                out AnchorDistributionResult result);
            return result;
        }

        private static ProvisionalOwnerBuildInput NewInput(
            Source s, float4 planeLocal, PhysicsOwnerPlacement placement, PhysicsOwnerMotion motion,
            AnchorDistributionResult anchors)
        {
            return new ProvisionalOwnerBuildInput
            {
                sourceShape = s.shape,
                sides = Classify(s, planeLocal, s.harness.eps),
                planeLocal = planeLocal,
                placement = placement,
                sourceMotion = motion,
                anchors = anchors,
                parentMass = ParentMass,
                sourceInertia = new float3(4f, 4f, 4f),
                sourceInertiaRotation = quaternion.identity,
                cooking = PhysicsCutCook.DefaultCooking,
                name = "Provisional",
            };
        }

        private ProvisionalOwnerCandidate Build(in ProvisionalOwnerBuildInput input)
        {
            bool built = ProvisionalOwnerBuilder.TryBuild(
                in input, out ProvisionalOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome);
            Assert.That(built, Is.True, "the pair was built, not " + outcome);
            Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.Ok));
            _disposables.Add(candidate);
            return candidate;
        }

        private static PhysicsOwnerBuildOutcome Refused(in ProvisionalOwnerBuildInput input)
        {
            bool built = ProvisionalOwnerBuilder.TryBuild(
                in input, out ProvisionalOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome);
            Assert.That(built, Is.False, "nothing should have been built");
            Assert.That(candidate, Is.Null);
            return outcome;
        }

        // ----- 1. the shapes are the source's own, shared -----------------------------------------------------------

        /// <summary>
        /// The 7.6 classification decides which of the source's own convexes each side names, a convex the plane
        /// crosses is named by **both** sides uncut, and the colliders use the very meshes the source already has.
        /// No cut was made, no mesh was copied and no mesh was baked here.
        /// </summary>
        [Test]
        public void TheSidesShareTheSourcesOwnCookedConvexes()
        {
            // Two prisms: the plane crosses the first and leaves the second wholly above it.
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(0.0, 3.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split), "the plane crosses the first prism");
            Assert.That(input.sides[1], Is.EqualTo(ConvexSide.Positive), "and passes below the second");

            ProvisionalOwnerCandidate candidate = Build(in input);

            Assert.That(candidate.PositiveShape.ConvexCount, Is.EqualTo(2), "the crossed one and the one above it");
            Assert.That(candidate.NegativeShape.ConvexCount, Is.EqualTo(1), "the crossed one alone");

            // The meshes are the source's own objects, on both sides.
            Assert.That(candidate.PositiveShape.MeshOf(0), Is.SameAs(s.shape.MeshOf(0)), "the same mesh object");
            Assert.That(candidate.PositiveShape.MeshOf(1), Is.SameAs(s.shape.MeshOf(1)));
            Assert.That(candidate.NegativeShape.MeshOf(0), Is.SameAs(s.shape.MeshOf(0)), "shared with the other side");

            // And so are the colliders', with the source's own cooking profile set before the mesh.
            Assert.That(candidate.Positive.Colliders.Count, Is.EqualTo(2), "one collider per convex of that side");
            Assert.That(candidate.Negative.Colliders.Count, Is.EqualTo(1));
            foreach (bool positive in new[] { true, false })
            {
                PhysicsOwnerSide owner = candidate.Side(positive);
                PhysicsOwnerShape shape = positive ? candidate.PositiveShape : candidate.NegativeShape;
                for (int i = 0; i < owner.Colliders.Count; i++)
                {
                    MeshCollider collider = owner.Colliders[i];
                    Assert.That(collider.convex, Is.True);
                    Assert.That(collider.cookingOptions, Is.EqualTo(PhysicsCutCook.DefaultCooking), "the source's profile");
                    Assert.That(collider.sharedMesh, Is.SameAs(shape.MeshOf(i)), "the mesh the source already had");
                }

                Assert.That(owner.ProducedColliderCount, Is.Zero, "this pair produced no collider of its own");
            }

            // The collider of a side is the same shape the B-rep and the mass were taken over: the second prism's
            // mesh sits three metres up, where its convex does.
            Bounds far = candidate.PositiveShape.MeshOf(1).bounds;
            Assert.That(far.center.y, Is.EqualTo(3f).Within(1e-3f), "the mesh stands where its convex does");
            Assert.That(candidate.PositiveShape.MeshOf(0).bounds.center.y, Is.EqualTo(0f).Within(1e-3f));
        }

        // ----- 2. the temporary mass, and the two separate fallbacks -------------------------------------------------

        /// <summary>
        /// The ordinary calculation: the temporary masses come from the conservative box divided by this cut's plane.
        /// A plane through the middle of one centred prism halves the parent mass, the centres of mass are the two
        /// parts' centroids, and the inertia is the box inertia of the **whole** source box at that side's mass, in
        /// the actor's own axes.
        /// </summary>
        [Test]
        public void TheTemporaryMassesComeFromTheBoxDividedByThePlane()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            // The box is x,z in [-Radius, Radius] and y in [-1, 1]; the plane y = 0 halves it.
            Assert.That(candidate.Positive.Mass, Is.EqualTo(6.0).Within(1e-6), "half of the parent mass");
            Assert.That(candidate.Negative.Mass, Is.EqualTo(6.0).Within(1e-6));

            Assert.That(candidate.Positive.CenterOfMass.y, Is.EqualTo(0.5f).Within(1e-4f), "the upper part's centroid");
            Assert.That(candidate.Positive.CenterOfMass.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(candidate.Negative.CenterOfMass.y, Is.EqualTo(-0.5f).Within(1e-4f));

            // (m/12)(ey^2 + ez^2) over the whole box's extents: 2 in y, 2*Radius in x and z.
            float ex = 2f * Radius, ey = 2f, ez = 2f * Radius;
            Assert.That(
                candidate.Positive.InertiaTensor.x, Is.EqualTo((float)(6.0 * ((ey * ey) + (ez * ez)) / 12.0)).Within(1e-3f));
            Assert.That(
                candidate.Positive.InertiaTensor.y, Is.EqualTo((float)(6.0 * ((ex * ex) + (ez * ez)) / 12.0)).Within(1e-3f));
            Assert.That(
                candidate.Positive.InertiaRotation, Is.EqualTo(quaternion.identity),
                "the box is axis-aligned in the actor's frame, so its principal axes are the actor's");
            Assert.That(
                candidate.Negative.InertiaTensor.x, Is.EqualTo(candidate.Positive.InertiaTensor.x).Within(1e-6f),
                "the whole box at the same mass gives the same inertia");
        }

        /// <summary>
        /// An asymmetric, axis-aligned cut, with each side's mass and centre of mass worked out here independently —
        /// not from the other side, and not from the two adding up.
        /// </summary>
        [Test]
        public void AnAsymmetricAxisAlignedCut_MatchesTheClosedFormOfEachSide()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));

            // The box is y in [-1, 1]; cut at y = 0.25. The upper part is 0.75 tall of a total of 2.
            var plane = new float4(0f, 1f, 0f, -0.25f);
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            Assert.That(candidate.Positive.Mass, Is.EqualTo(ParentMass * (0.75 / 2.0)).Within(1e-4), "4.5");
            Assert.That(candidate.Negative.Mass, Is.EqualTo(ParentMass * (1.25 / 2.0)).Within(1e-4), "7.5");
            Assert.That(candidate.Positive.CenterOfMass.y, Is.EqualTo(0.625f).Within(1e-4f), "(0.25 + 1) / 2");
            Assert.That(candidate.Negative.CenterOfMass.y, Is.EqualTo(-0.375f).Within(1e-4f), "(-1 + 0.25) / 2");
            Assert.That(candidate.Positive.CenterOfMass.x, Is.EqualTo(0f).Within(1e-4f));
            Assert.That(candidate.Negative.CenterOfMass.z, Is.EqualTo(0f).Within(1e-4f));
        }

        /// <summary>
        /// An asymmetric cut that is not axis-aligned: a plane through three faces cuts one corner off the box as a
        /// tetrahedron, whose volume and centroid have a closed form of their own. The positive side is compared
        /// against that tetrahedron, and the negative side against the whole box less it — each computed here.
        /// </summary>
        [Test]
        public void ACornerCut_MatchesTheTetrahedronsOwnClosedForm()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));

            // Through (0, 1, Radius), (Radius, 0, Radius) and (Radius, 1, 0): the corner at (Radius, 1, Radius) is
            // cut off with legs u = Radius along x, v = 1 along y, w = Radius along z.
            var raw = new float4(1f / Radius, 1f, 1f / Radius, -2f);
            float4 plane = raw / math.length(raw.xyz);
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            // The tetrahedron is u*v*w/6 of a box of 2u * 2v * 2w, so one forty-eighth of it.
            const double CornerShare = 1.0 / 48.0;
            double expectedPositive = ParentMass * CornerShare;
            Assert.That(candidate.Positive.Mass, Is.EqualTo(expectedPositive).Within(1e-4), "0.25");
            Assert.That(candidate.Negative.Mass, Is.EqualTo(ParentMass * (1.0 - CornerShare)).Within(1e-4), "11.75");

            // A tetrahedron's centroid is the mean of its four corners, so each coordinate is a quarter of the leg in
            // from the box corner.
            var expectedCentre = new float3(0.75f * Radius, 0.75f, 0.75f * Radius);
            Assert.That(
                math.length(candidate.Positive.CenterOfMass - expectedCentre), Is.LessThan(2e-3f),
                "the corner tetrahedron's own centroid");

            // The rest of the box, from the whole box's centroid at the origin less that tetrahedron's.
            float3 expectedRest = -expectedCentre * (float)(CornerShare / (1.0 - CornerShare));
            Assert.That(
                math.length(candidate.Negative.CenterOfMass - expectedRest), Is.LessThan(2e-3f),
                "the box less the corner");
        }

        /// <summary>
        /// The two temporary masses always add back to the parent mass, and the two centroids weighted by them
        /// recombine to the centre of the box.
        /// <para>
        /// **This is a necessary condition, not a proof of the division.** The sum holds by construction, since the
        /// negative mass is the parent mass less the positive one; the recombination would also hold for some wrong
        /// pairs of parts. What the closed-form tests above compare is each side on its own.
        /// </para>
        /// </summary>
        [Test]
        public void TheTwoPartsAlwaysRecombine_WhateverThePlane()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var planes = new[]
            {
                new float4(0f, 1f, 0f, 0f),
                new float4(1f, 0f, 0f, -0.25f),
                new float4(math.normalize(new float3(1f, 1f, 0f)), 0f),
                new float4(math.normalize(new float3(1f, 2f, 3f)), 0.1f),
                new float4(math.normalize(new float3(-2f, 0.5f, 1f)), -0.4f),
                new float4(math.normalize(new float3(0.3f, -1f, 0.2f)), 0.45f),
            };

            foreach (float4 plane in planes)
            {
                ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
                Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split), "every one of these crosses the prism");
                ProvisionalOwnerCandidate candidate = Build(in input);

                Assert.That(
                    candidate.Positive.Mass + candidate.Negative.Mass, Is.EqualTo(ParentMass).Within(1e-9),
                    "the two masses add back to the parent for " + plane);
                Assert.That(candidate.Positive.Mass, Is.GreaterThan(0.0), "and neither part is empty for " + plane);
                Assert.That(candidate.Negative.Mass, Is.GreaterThan(0.0));

                double3 combined =
                    ((candidate.Positive.Mass * (double3)candidate.Positive.CenterOfMass)
                     + (candidate.Negative.Mass * (double3)candidate.Negative.CenterOfMass)) / ParentMass;
                Assert.That(
                    math.length(combined), Is.LessThan(1e-4),
                    "the two parts recombine to the centre of the box for " + plane);
            }
        }

        // ----- the two fallbacks, apart from each other --------------------------------------------------------------

        /// <summary>
        /// **The volume fallback of DESIGN 7.2, alone.** A diagnostic source whose box has no volume — all of it in
        /// one plane — cannot be divided by volume, so the parent mass is halved and the centre of the box stands in
        /// for the centroids. The inertia here is **not** a fallback: that flat box still has a finite, positive box
        /// inertia, and the actor's own axes go with it.
        /// </summary>
        [Test]
        public void ABoxWithNoVolume_DividesTheParentMassEqually()
        {
            // The prism's top cap alone: four vertices at y = 1, spanning x and z.
            Source s = NewFlatSource();

            var plane = new float4(1f, 0f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split), "two corners each side of the plane");

            ProvisionalOwnerCandidate candidate = Build(in input);

            Assert.That(candidate.Positive.Mass, Is.EqualTo(6.0).Within(1e-9), "no volume to divide: halves");
            Assert.That(candidate.Negative.Mass, Is.EqualTo(6.0).Within(1e-9));

            // The centre of that flat box, on both sides, because neither part has a centroid of its own.
            var centre = new float3(0f, 1f, 0f);
            Assert.That(math.length(candidate.Positive.CenterOfMass - centre), Is.LessThan(1e-4f));
            Assert.That(
                math.all(candidate.Negative.CenterOfMass == candidate.Positive.CenterOfMass), Is.True,
                "the same stand-in on both sides");

            // The inertia is the ordinary one: the flat box's extents are 2*Radius, 0, 2*Radius.
            float e = 2f * Radius;
            Assert.That(
                candidate.Positive.InertiaTensor.x, Is.EqualTo((float)(6.0 * (e * e) / 12.0)).Within(1e-3f),
                "the box inertia, not the source's");
            Assert.That(candidate.Positive.InertiaRotation, Is.EqualTo(quaternion.identity));
        }

        /// <summary>
        /// A mass the body could not be given is refused, not built with. A parent mass of 1e40 divides into two
        /// finite, positive doubles, but each becomes an infinity in the float a Rigidbody takes, so there is no pair
        /// to make: no candidate, and no hold taken on the way.
        /// <para>
        /// The refusal is decided on the masses, before any inertia is worked out. Nothing is clamped here and no
        /// smallest or largest mass is introduced.
        /// </para>
        /// </summary>
        [Test]
        public void AMassThatCannotReachTheBody_IsRefused()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            int held = s.source.Users;
            int bodies = LiveBodies();

            ProvisionalOwnerBuildInput input = NewInput(
                s, new float4(0f, 1f, 0f, -0.25f), PhysicsOwnerPlacement.Identity, default, Anchors());
            input.parentMass = 1e40;
            Assert.That(
                (float)(input.parentMass * 0.375), Is.EqualTo(float.PositiveInfinity),
                "the division is finite as a double and an infinity as a float");

            Assert.That(Refused(in input), Is.EqualTo(PhysicsOwnerBuildOutcome.MassNotUsable));
            Assert.That(s.source.Users, Is.EqualTo(held), "no hold was taken");
            Assert.That(LiveBodies(), Is.EqualTo(bodies), "and no actor was left behind");
        }

        /// <summary>
        /// **The inertia fallback of DESIGN 7.2, alone, with its principal axes.** The masses here are ordinary ones
        /// a body could be given; what is past what a float holds is the **box inertia**, because the diagnostic
        /// source is 1e6 units in radius and half-height. So the source actor's own inertia at this side's share of
        /// the mass is used —
        /// and since that inertia is anisotropic and its principal axes are not the actor's, the orientation comes
        /// with it, or the pair would be a differently oriented body. The mass is still divided **by volume**, not
        /// halved, so neither of the other fallbacks is involved.
        /// </summary>
        [Test]
        public void AnInertiaThatIsNotFinite_FallsBackToTheSourceInertiaWithItsAxes()
        {
            // The same prism at radius 1e6 and half-height 1e6, so the box around it is about 1.41e6 in x and z and
            // 2e6 in y: a scale whose second moments leave the float range at these masses.
            var polys = new List<ConvexPoly> { CaseGenerator.Prism(4, 1e6, 1e6) };
            Source s = NewSource(float4x4.identity, polys, null);

            quaternion principal = math.normalize(quaternion.AxisAngle(math.normalize(new float3(1f, 1f, 0f)), 0.7f));
            var anisotropic = new float3(6f, 3f, 1f);
            const double LargeMass = 1e30;

            // The box is y in [-1e6, 1e6], cut at a quarter up: shares of 0.375 and 0.625 by volume.
            ProvisionalOwnerBuildInput input = NewInput(
                s, new float4(0f, 1f, 0f, -0.25e6f), PhysicsOwnerPlacement.Identity, default, Anchors());
            input.parentMass = LargeMass;
            input.sourceInertia = anisotropic;
            input.sourceInertiaRotation = principal;
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split));

            ProvisionalOwnerCandidate candidate = Build(in input);

            Assert.That(candidate.Positive.Mass, Is.EqualTo(LargeMass * 0.375).Within(LargeMass * 1e-6));
            Assert.That(
                candidate.Negative.Mass, Is.EqualTo(LargeMass * 0.625).Within(LargeMass * 1e-6),
                "divided by volume, not halved: this is the inertia fallback alone");
            Assert.That(
                math.isfinite((float)candidate.Positive.Mass) && (float)candidate.Positive.Mass > 0f, Is.True,
                "and both masses are ones a body could be given");
            Assert.That(math.isfinite((float)candidate.Negative.Mass), Is.True);

            foreach (bool positive in new[] { true, false })
            {
                PhysicsOwnerSide side = candidate.Side(positive);
                var share = (float)(side.Mass / LargeMass);
                Assert.That(
                    math.length(side.InertiaTensor - (anisotropic * share)), Is.LessThan(1e-3f),
                    "the source's three numbers at this side's share of the mass");
                Assert.That(
                    math.abs(math.dot(side.InertiaRotation.value, principal.value)), Is.GreaterThan(1f - 1e-5f),
                    "and the orientation they were taken in, not the identity");
            }
        }

        /// <summary>
        /// A box inertia that is finite but zero is **not** rescued by the fallback. A diagnostic source with all its
        /// vertices on one line has a box with two extents of zero, so one principal value is zero, and the pair is
        /// refused for the same reason any unusable mass is refused.
        /// </summary>
        [Test]
        public void AFiniteZeroInertia_IsRefusedRatherThanReplaced()
        {
            Source s = NewNeedleSource();
            int held = s.source.Users;

            ProvisionalOwnerBuildInput input = NewInput(
                s, new float4(1f, 0f, 0f, 0f), PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split), "one end each side of the plane");

            Assert.That(Refused(in input), Is.EqualTo(PhysicsOwnerBuildOutcome.MassNotUsable));
            Assert.That(s.source.Users, Is.EqualTo(held), "and nothing was taken");
        }

        /// <summary>
        /// A source whose shape is one flat quadrilateral: the prism's top cap alone. Diagnostic input, never a cook
        /// product.
        /// </summary>
        private Source NewFlatSource()
        {
            var polys = new List<ConvexPoly> { CaseGenerator.Box() };
            var flat = new[] { new ConvexBrepRange { vertexBase = 0, vertexCount = 4 } };
            return NewSource(float4x4.identity, polys, flat);
        }

        /// <summary>
        /// A source whose shape is one segment: the two corners of the top cap that differ in x alone. Diagnostic
        /// input, never a cook product.
        /// </summary>
        private Source NewNeedleSource()
        {
            var polys = new List<ConvexPoly> { CaseGenerator.Box() };
            var needle = new[] { new ConvexBrepRange { vertexBase = 0, vertexCount = 2 } };
            return NewSource(float4x4.identity, polys, needle);
        }

        // ----- 3. the frames --------------------------------------------------------------------------------------

        /// <summary>
        /// The shape's numerical frame is not the actor's. The box, the plane, the centres of mass and the joint axis
        /// all belong to the actor's frame, so a turned and moved shape frame carries them with it, and each side's
        /// own shape frame carries the same mapping.
        /// </summary>
        [Test]
        public void ATurnedShapeFrame_IsCarriedIntoTheActorsFrame()
        {
            // A quarter turn about z and a lift: local +y becomes actor -x, local +x becomes actor +y.
            quaternion turn = quaternion.AxisAngle(new float3(0f, 0f, 1f), math.PI * 0.5f);
            var lift = new float3(0f, 0.4f, 0f);
            float4x4 localToOwner = float4x4.TRS(lift, turn, new float3(1f, 1f, 1f));

            Source s = NewSource(localToOwner, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            // The plane's local +y normal is actor -x, so the positive part of the box is the one at smaller x, and
            // its centroid is half the box's x-extent that way from the box centre, which sits at the lift.
            Assert.That(
                candidate.Positive.CenterOfMass.x, Is.EqualTo(-0.5f).Within(1e-3f),
                "the positive part is the one the turned normal points at");
            Assert.That(candidate.Positive.CenterOfMass.y, Is.EqualTo(lift.y).Within(1e-3f), "lifted with the frame");
            Assert.That(candidate.Negative.CenterOfMass.x, Is.EqualTo(0.5f).Within(1e-3f));
            Assert.That(candidate.Negative.CenterOfMass.y, Is.EqualTo(lift.y).Within(1e-3f));

            // The inertia follows the turned box: 2 along x now, 2*Radius along y and z.
            float ex = 2f, ey = 2f * Radius, ez = 2f * Radius;
            Assert.That(
                candidate.Positive.InertiaTensor.x, Is.EqualTo((float)(6.0 * ((ey * ey) + (ez * ez)) / 12.0)).Within(1e-3f));
            Assert.That(
                candidate.Positive.InertiaTensor.z, Is.EqualTo((float)(6.0 * ((ex * ex) + (ey * ey)) / 12.0)).Within(1e-3f));

            // The joint's axis is that same normal in the actor's frame.
            float3 axis = candidate.Separation.axis;
            Assert.That(axis.x, Is.EqualTo(-1f).Within(1e-4f));
            Assert.That(axis.y, Is.EqualTo(0f).Within(1e-4f));

            // And each side's shape frame carries the mapping, so its colliders sit where the source's do.
            foreach (bool positive in new[] { true, false })
            {
                Transform frame = candidate.Side(positive).ShapeFrame.transform;
                Assert.That(((float3)frame.localPosition).y, Is.EqualTo(lift.y).Within(1e-4f));
                float3 turned = math.mul((quaternion)frame.localRotation, new float3(0f, 1f, 0f));
                Assert.That(turned.x, Is.EqualTo(-1f).Within(1e-3f), "the frame turns local +y onto actor -x");
            }
        }

        // ----- 4. the placement, the motion and the fixed side -------------------------------------------------------

        /// <summary>
        /// Both sides stand where the source stands and take the first split's motion from it, by the formula of
        /// DESIGN 7.2, with no separation impulse. A side the ledger's anchors fix takes neither velocity.
        /// </summary>
        [Test]
        public void BothSidesStandWhereTheSourceDoes_AndTakeTheFirstSplitMotion()
        {
            var placement = new PhysicsOwnerPlacement(
                new float3(2f, -1f, 0.5f), quaternion.AxisAngle(new float3(0f, 0f, 1f), math.PI * 0.25f));
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            var motion = new PhysicsOwnerMotion(
                float3.zero, new float3(1f, 0f, 0f), new float3(0f, 0f, 2f), float3.zero);

            ProvisionalOwnerCandidate free = Build(NewInput(s, plane, placement, motion, Anchors()));

            Assert.That(
                math.length((float3)free.Positive.Root.transform.position - placement.position),
                Is.LessThan(1e-4f), "both sides stand where the source stands");
            Assert.That(
                math.length((float3)free.Negative.Root.transform.position - placement.position),
                Is.LessThan(1e-4f));

            // v = v_anchor + omega x (com_world - anchor), with the anchor and the source's centre at the origin.
            foreach (bool positive in new[] { true, false })
            {
                PhysicsOwnerSide side = free.Side(positive);
                float3 world = placement.ToWorld(side.CenterOfMass);
                float3 expected = new float3(1f, 0f, 0f) + math.cross(new float3(0f, 0f, 2f), world);
                Assert.That(
                    math.length(side.LinearVelocity - expected), Is.LessThan(1e-3f),
                    "the first split's velocity at that side's centre of mass, and no impulse on top of it");
                Assert.That(
                    math.length(side.AngularVelocity - new float3(0f, 0f, 2f)), Is.LessThan(1e-4f),
                    "the source's angular velocity");
            }

            Assert.That(
                math.length(free.Positive.LinearVelocity - free.Negative.LinearVelocity), Is.GreaterThan(1e-3f),
                "the two differ only because their centres of mass do");

            // An anchor above the plane fixes the positive side and nothing else.
            Source anchored = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            ProvisionalOwnerCandidate held = Build(
                NewInput(anchored, plane, placement, motion, Anchors(new float3(0f, 0.5f, 0f))));
            Assert.That(held.Positive.FixedByAnchors, Is.True, "the anchor went to the positive side");
            Assert.That(math.all(held.Positive.LinearVelocity == float3.zero), Is.True, "so that side takes no motion");
            Assert.That(math.all(held.Positive.AngularVelocity == float3.zero), Is.True);
            Assert.That(held.Negative.FixedByAnchors, Is.False, "and the other side is free");
            Assert.That(math.lengthsq(held.Negative.LinearVelocity), Is.GreaterThan(0f));
        }

        // ----- 5. the sibling constraint, as configured --------------------------------------------------------------

        /// <summary>
        /// The sibling separation constraint carries the values DESIGN 7.1.1 fixes. **These are the settings it was
        /// given, not a statement about any solver**: nothing here is stepped, and DESIGN 7.1.1 asks for no guarantee
        /// about the displacement that follows.
        /// </summary>
        [Test]
        public void TheSiblingConstraintCarriesTheValuesTheDesignFixes()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            ConfigurableJoint joint = candidate.Separation;
            Assert.That(joint, Is.Not.Null, "one constraint for the pair");
            Assert.That(
                candidate.Positive.Root.GetComponents<ConfigurableJoint>().Length
                + candidate.Negative.Root.GetComponents<ConfigurableJoint>().Length, Is.EqualTo(1), "exactly one");
            Assert.That(joint.connectedBody, Is.SameAs(candidate.Negative.Body), "between the two siblings");
            Assert.That(joint.autoConfigureConnectedAnchor, Is.False);

            float3 axis = joint.axis;
            Assert.That(axis.y, Is.EqualTo(1f).Within(1e-4f), "the adopted plane's normal");
            float3 secondary = joint.secondaryAxis;
            Assert.That(math.abs(math.dot(secondary, axis)), Is.LessThan(1e-4f), "orthogonal to it");
            Assert.That(math.length(secondary), Is.EqualTo(1f).Within(1e-4f), "and a direction");

            Assert.That(joint.xMotion, Is.EqualTo(ConfigurableJointMotion.Limited), "only along the normal");
            Assert.That(joint.yMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.zMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.angularXMotion, Is.EqualTo(ConfigurableJointMotion.Locked), "and nothing turns");
            Assert.That(joint.angularYMotion, Is.EqualTo(ConfigurableJointMotion.Locked));
            Assert.That(joint.angularZMotion, Is.EqualTo(ConfigurableJointMotion.Locked));

            Assert.That(joint.linearLimit.limit, Is.EqualTo(1f).Within(1e-6f), "a symmetric metre");
            Assert.That(math.length((float3)joint.anchor), Is.EqualTo(0f).Within(1e-6f));
            Assert.That(
                math.length((float3)joint.connectedAnchor), Is.EqualTo(1f).Within(1e-4f),
                "with the anchors a metre apart along the normal");

            Assert.That(joint.projectionMode, Is.EqualTo(JointProjectionMode.None), "nothing is projected");
            Assert.That(joint.breakForce, Is.EqualTo(float.PositiveInfinity), "and nothing breaks it");
            Assert.That(joint.breakTorque, Is.EqualTo(float.PositiveInfinity));

            // The second axis is settled by the first: the same normal gives the same answer.
            Source again = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            ProvisionalOwnerCandidate twice = Build(
                NewInput(again, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));
            Assert.That(
                math.all(math.abs((float3)twice.Separation.secondaryAxis - secondary) < 1e-6f), Is.True,
                "the same input gives the same second axis");
        }

        // ----- 6. unpublished, and given back -----------------------------------------------------------------------

        /// <summary>
        /// Nothing of the pair is in the physics scene and nothing of the source changed. Disposing gives back what
        /// this build made — its objects and the mesh holds its shapes took — and leaves the source's own meshes and
        /// shape as they were. This is the unpublished candidate's whole lifetime; the lease a published pair needs
        /// across physics steps is not this, and is not exercised here.
        /// </summary>
        [Test]
        public void ThePairIsUnpublished_AndDisposingGivesBackWhatTheBuildTook()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(0.0, 3.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);

            int held = s.source.Users;
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "inactive, so in no physics scene");
            Assert.That(candidate.Negative.Root.activeInHierarchy, Is.False);
            Assert.That(candidate.Positive.Body.isKinematic, Is.False, "and nothing was written onto the bodies");
            Assert.That(s.source.Users, Is.EqualTo(held + 2), "one hold per side on the source's meshes");

            // The source is untouched: its shape, its meshes and its convexes are as they were.
            Assert.That(s.shape.ConvexCount, Is.EqualTo(2), "the source still has both convexes");
            Assert.That(s.shape.MeshOf(0) == null, Is.False, "and its meshes");
            Assert.That(s.shape.IsFreed, Is.False);

            GameObject positiveRoot = candidate.Positive.Root;
            GameObject negativeRoot = candidate.Negative.Root;
            candidate.Dispose();

            Assert.That(candidate.IsDisposed, Is.True);
            Assert.That(positiveRoot == null, Is.True, "the objects this build made are gone");
            Assert.That(negativeRoot == null, Is.True);
            Assert.That(s.source.Users, Is.EqualTo(held), "and the holds went back");
            Assert.That(s.shape.MeshOf(0) == null, Is.False, "the source's meshes are still the source's");
            Assert.That(s.shape.IsFreed, Is.False, "and so is its shape");

            candidate.Dispose();
            Assert.That(s.source.Users, Is.EqualTo(held), "disposing twice gives nothing back twice");
        }

        /// <summary>
        /// A build that throws **after** the holds are taken and one side's actor is standing: everything this call
        /// made goes back, the source is left as it was, and the exception reaches the caller rather than being
        /// turned into a result.
        /// </summary>
        [Test]
        public void AFailurePartWayThrough_GivesBackWhatWasMadeAndLetsTheExceptionOut()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(0.0, 3.0, 0.0));
            int held = s.source.Users;
            int bodies = LiveBodies();

            var seen = new List<bool>();
            ProvisionalOwnerBuilder.sideBuiltHook = positive =>
            {
                seen.Add(positive);
                if (!positive)
                {
                    throw new InvalidOperationException("the second side failed");
                }
            };

            ProvisionalOwnerBuildInput input = NewInput(
                s, new float4(0f, 1f, 0f, 0f), PhysicsOwnerPlacement.Identity, default, Anchors());

            Assert.That(
                () =>
                {
                    ProvisionalOwnerBuilder.TryBuild(
                        in input, out ProvisionalOwnerCandidate _, out PhysicsOwnerBuildOutcome _);
                },
                Throws.TypeOf<InvalidOperationException>(), "the exception is not turned into a result");
            Assert.That(seen, Is.EqualTo(new[] { true, false }), "it failed with the first side already standing");

            Assert.That(s.source.Users, Is.EqualTo(held), "both sides' holds went back");
            Assert.That(LiveBodies(), Is.EqualTo(bodies), "and neither actor was left behind");
            Assert.That(s.shape.IsFreed, Is.False, "the source's shape is still the source's");
            Assert.That(s.shape.MeshOf(0) == null, Is.False, "and so are its meshes");
        }

        /// <summary>
        /// An input the build cannot use makes nothing and takes nothing, and says so with the reason a final split
        /// would give. No new reason is introduced for this path.
        /// </summary>
        [Test]
        public void AnInputTheBuildCannotUse_MakesNothingAndTakesNothing()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(0.0, 3.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            int held = s.source.Users;
            int bodies = LiveBodies();

            ProvisionalOwnerBuildInput noMass = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            noMass.parentMass = 0.0;
            Assert.That(Refused(in noMass), Is.EqualTo(PhysicsOwnerBuildOutcome.InvalidInput));

            ProvisionalOwnerBuildInput noNormal = NewInput(
                s, new float4(0f, 0f, 0f, 1f), PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(
                Refused(in noNormal), Is.EqualTo(PhysicsOwnerBuildOutcome.InvalidInput), "a plane with no normal");

            ProvisionalOwnerBuildInput noInertiaAxes = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            noInertiaAxes.sourceInertiaRotation = new quaternion(0f, 0f, 0f, 0f);
            Assert.That(
                Refused(in noInertiaAxes), Is.EqualTo(PhysicsOwnerBuildOutcome.InvalidInput),
                "an inertia with no orientation to go with it");

            // A disposition this call was not given: not a fourth known value, so not a default either.
            ProvisionalOwnerBuildInput unknownSide = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            var sides = new ConvexSide[] { ConvexSide.Split, (ConvexSide)7 };
            unknownSide.sides = sides;
            Assert.That(
                Refused(in unknownSide), Is.EqualTo(PhysicsOwnerBuildOutcome.InvalidInput),
                "an unknown 7.6 disposition is not quietly made positive");

            // A plane that leaves every convex on one side: by 7.6 there is no pair to build at all.
            ProvisionalOwnerBuildInput oneSided = NewInput(
                s, new float4(0f, 1f, 0f, -50f), PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(oneSided.sides[0], Is.EqualTo(ConvexSide.Negative));
            Assert.That(oneSided.sides[1], Is.EqualTo(ConvexSide.Negative));
            Assert.That(Refused(in oneSided), Is.EqualTo(PhysicsOwnerBuildOutcome.SideEmpty));

            Assert.That(s.source.Users, Is.EqualTo(held), "nothing was taken");
            Assert.That(LiveBodies(), Is.EqualTo(bodies), "and nothing was left behind");
            Assert.That(s.shape.IsFreed, Is.False, "the source is as it was");
        }

        /// <summary>
        /// A source that is not there to be read any more, and one whose cooked mesh has been destroyed: neither
        /// reaches a built pair. The first is refused before its bank is scanned; the second before any hold is
        /// taken, rather than becoming a collider with no shape.
        /// </summary>
        [Test]
        public void AMissingShapeOrMesh_IsNeverBuiltInto()
        {
            var plane = new float4(0f, 1f, 0f, 0f);

            Source destroyed = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(0.0, 3.0, 0.0));
            int held = destroyed.source.Users;
            ProvisionalOwnerBuildInput missingMesh = NewInput(
                destroyed, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            UnityEngine.Object.DestroyImmediate(destroyed.shape.MeshOf(1));
            Assert.That(destroyed.shape.MeshOf(1) == null, Is.True, "the source has lost one of its meshes");
            Assert.That(Refused(in missingMesh), Is.EqualTo(PhysicsOwnerBuildOutcome.ShapeMissing));
            Assert.That(destroyed.source.Users, Is.EqualTo(held), "and no hold was taken on the way");

            Source released = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(0.0, 3.0, 0.0));
            ProvisionalOwnerBuildInput freedShape = NewInput(
                released, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            released.shape.Dispose();
            Assert.That(released.shape.IsFreed, Is.True, "the source's shape has been given back");
            Assert.That(
                Refused(in freedShape), Is.EqualTo(PhysicsOwnerBuildOutcome.InvalidInput),
                "a released shape is refused, not scanned");
        }

        private static int LiveBodies()
        {
            return UnityEngine.Object
                .FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
        }
    }
}
