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
        /// degenerate diagnostic shapes below are made; each mesh is then built from **that range's own vertices**,
        /// because a shape's box is taken from its colliders and a diagnostic shape has to present the degenerate box
        /// it is there to present -- the whole polytope's collider would give it a full one.
        /// </summary>
        private Source NewSource(float4x4 localToOwner, List<ConvexPoly> polys, ConvexBrepRange[] ranges)
        {
            ConvexBrepRange[] givenRanges = ranges;
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
                s.meshes.Add(
                    givenRanges == null
                        ? CookedConvex(polys[math.min(i, polys.Count - 1)], "Source " + i)
                        : ColliderOfRange(s.harness.input.bank, ranges[i], "Source " + i));
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

        /// <summary>
        /// A collider for exactly the vertices one range names. It has no faces: these are the degenerate diagnostic
        /// shapes, which have no volume to triangulate, and what a shape reads from a collider is its bounds.
        /// </summary>
        private static Mesh ColliderOfRange(ConvexBrepBank bank, ConvexBrepRange range, string name)
        {
            var vertices = new Vector3[range.vertexCount];
            for (int v = 0; v < range.vertexCount; v++)
            {
                vertices[v] = bank.vertices[range.vertexBase + v];
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
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
                    float3 v = s.shape.BankOf(c).vertices[range.vertexBase + i];
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

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void AxisCutsAndReversedPlanes_MatchSlabMassAndCentroidNearFaces(int axis)
        {
            Source source = NewSource(float4x4.identity, double3.zero);
            Assert.That(ProvisionalBoxMass.TryBox(source.shape, out float3 lo, out float3 hi), Is.True);
            var cuts = new[] { 0f, lo[axis] + 1e-5f, hi[axis] - 1e-5f };

            foreach (float cut in cuts)
            {
                foreach (float sign in new[] { 1f, -1f })
                {
                    float3 normal = float3.zero;
                    normal[axis] = sign;
                    var plane = new float4(normal, -sign * cut);
                    Assert.That(ProvisionalBoxMass.TryDivide(
                        source.shape, plane, ParentMass, new float3(1f), quaternion.identity,
                        out ProvisionalBoxMass.Side positive, out ProvisionalBoxMass.Side negative, out _), Is.True);

                    // Each side is an axis-aligned slab. Its independent volume fraction is its width divided
                    // by the box width, and its centroid lies halfway between its two faces.
                    double lowerWidth = (double)cut - lo[axis];
                    double upperWidth = (double)hi[axis] - cut;
                    double boxWidth = (double)hi[axis] - lo[axis];
                    double expectedPositive = ParentMass * (sign > 0f ? upperWidth : lowerWidth) / boxWidth;
                    double expectedNegative = ParentMass * (sign > 0f ? lowerWidth : upperWidth) / boxWidth;
                    Assert.That(positive.mass, Is.EqualTo(expectedPositive).Within(1e-9));
                    Assert.That(negative.mass, Is.EqualTo(expectedNegative).Within(1e-9));

                    float3 upperCentre = (lo + hi) * 0.5f;
                    float3 lowerCentre = upperCentre;
                    upperCentre[axis] = (hi[axis] + cut) * 0.5f;
                    lowerCentre[axis] = (lo[axis] + cut) * 0.5f;
                    float3 expectedPositiveCentre = sign > 0f ? upperCentre : lowerCentre;
                    float3 expectedNegativeCentre = sign > 0f ? lowerCentre : upperCentre;
                    Assert.That(math.length(positive.centerOfMass - expectedPositiveCentre), Is.LessThan(3e-5f));
                    Assert.That(math.length(negative.centerOfMass - expectedNegativeCentre), Is.LessThan(3e-5f));
                }
            }
        }
        // ----- the box itself: where it comes from, and when it is settled -----------------------------------------

        /// <summary>
        /// The box the masses are divided from encloses the source, with several convexes and a frame that both turns
        /// and moves. The expected box is worked out here by walking every vertex of every convex through the same
        /// frame -- which is what the builder used to do and no longer does -- and the builder's box must contain it.
        /// <para>
        /// Containing it, not equalling it: the shape's box is the union of its colliders' own boxes, carried over by
        /// its eight corners, so a turned frame leaves it wider than the vertices need. That is allowed, as long as
        /// nothing of the source falls outside.
        /// </para>
        /// </summary>
        [Test]
        public void TheBox_EnclosesEveryVertex_ThroughATurnedAndMovedFrame()
        {
            float4x4 localToOwner = float4x4.TRS(
                new float3(1.5f, -2.25f, 0.75f),
                quaternion.Euler(0.4f, -0.9f, 0.25f),
                new float3(1f));
            Source s = NewSource(localToOwner, new double3(0.0, 0.0, 0.0), new double3(2.5, 0.0, 0.0));

            Assert.That(s.shape.TryLocalBounds(out float3 localLo, out float3 localHi), Is.True, "the shape has a box");
            Assert.That(
                ProvisionalBoxMass.TryBox(s.shape, out float3 lo, out float3 hi), Is.True,
                "and it carries into the actor's frame");

            // Every vertex of every convex, through the same frame: what the old scan would have produced.
            var scanLo = new float3(float.PositiveInfinity);
            var scanHi = new float3(float.NegativeInfinity);
            for (int c = 0; c < s.shape.ConvexCount; c++)
            {
                ConvexBrepRange range = s.shape.Convex(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 at = s.shape.BankOf(c).vertices[range.vertexBase + v];
                    Assert.That(
                        math.all(at >= localLo) && math.all(at <= localHi), Is.True,
                        "convex " + c + " vertex " + v + " is inside the shape's own box, with nothing allowed for");
                    float3 inOwner = math.transform(localToOwner, at);
                    scanLo = math.min(scanLo, inOwner);
                    scanHi = math.max(scanHi, inOwner);
                }
            }

            Assert.That(
                math.all(lo <= scanLo) && math.all(hi >= scanHi), Is.True,
                "the box the builder uses encloses every vertex, with nothing allowed for: it is [" + lo + ", " + hi
                + "] and the vertices reach [" + scanLo + ", " + scanHi + "]");
        }

        /// <summary>
        /// Where the owner is in the world is not part of the box. The same shape is built into two candidates whose
        /// placements differ in position and rotation, and the shape's own box is the same object it was: moving an
        /// owner is not a reason to look at a single vertex again.
        /// </summary>
        [Test]
        public void MovingOrTurningTheOwner_DoesNotChangeTheShapesBox()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            Assert.That(s.shape.TryLocalBounds(out float3 loBefore, out float3 hiBefore), Is.True, "the shape has a box");

            ProvisionalOwnerCandidate atRest = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));
            var moved = new PhysicsOwnerPlacement(
                new float3(12f, -4f, 7f), quaternion.Euler(0.54f, -1.29f, 0.21f));
            ProvisionalOwnerCandidate elsewhere = Build(NewInput(s, plane, moved, default, Anchors()));

            Assert.That(s.shape.TryLocalBounds(out float3 loAfter, out float3 hiAfter), Is.True, "it still has one");
            Assert.That(loAfter, Is.EqualTo(loBefore), "and it is the box it was: the low corner");
            Assert.That(hiAfter, Is.EqualTo(hiBefore), "and the high corner");
            Assert.That(
                elsewhere.Positive.Mass, Is.EqualTo(atRest.Positive.Mass).Within(1e-9),
                "so the mass the box divides into is the same wherever the owner is");
            Assert.That(elsewhere.Negative.Mass, Is.EqualTo(atRest.Negative.Mass).Within(1e-9));
        }

        /// <summary>
        /// A shape made of a different arrangement gets its own box, settled when that shape is made. Two shapes over
        /// the same source convexes -- one of both, one of the first alone -- have boxes of their own, and the smaller
        /// one's is the smaller.
        /// </summary>
        [Test]
        public void AShapeOfADifferentArrangement_HasItsOwnBox()
        {
            Source both = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0), new double3(4.0, 0.0, 0.0));
            Assert.That(both.shape.TryLocalBounds(out float3 bothLo, out float3 bothHi), Is.True, "the pair has a box");

            Source one = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            Assert.That(one.shape.TryLocalBounds(out float3 oneLo, out float3 oneHi), Is.True, "and so has the single");

            Assert.That(bothHi.x, Is.GreaterThan(oneHi.x + 1f), "the pair reaches further along x than the single");
            Assert.That(oneLo.x, Is.EqualTo(bothLo.x).Within(1e-4f), "and they begin at the same place");
            Assert.That(
                oneHi.y, Is.EqualTo(bothHi.y).Within(1e-4f), "with the same reach in the directions they do not differ in");
        }

        /// <summary>
        /// Building a provisional pair works nothing out from vertices for its boxes: the boxes come across from the
        /// source. Every vertex of the source's bank is moved far away **after** the source shape was made, and the
        /// pair is then built: each side's box is the one the source held for the convexes that side was given, not
        /// one drawn from where the vertices now are.
        /// <para>
        /// A build that walked the vertices for its boxes -- which is what an earlier version of this did, once per
        /// side and twice for a convex the plane crosses -- would come back with boxes a thousand units away.
        /// </para>
        /// </summary>
        [Test]
        public void BuildingASide_TakesItsBoxFromTheSource_AndWalksNoVertex()
        {
            Source s = NewSource(
                float4x4.identity, new double3(0.0, 3.0, 0.0), new double3(0.0, -3.0, 0.0), new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);

            // What the source holds for each of its convexes, before anything is disturbed.
            var sourceLo = new float3[s.shape.ConvexCount];
            var sourceHi = new float3[s.shape.ConvexCount];
            for (int c = 0; c < s.shape.ConvexCount; c++)
            {
                s.shape.ConvexBounds(c, out sourceLo[c], out sourceHi[c]);
            }

            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());

            // Not something the product ever does. Every vertex is moved, so anything that reads one to make a box
            // gives itself away.
            for (int c = 0; c < s.shape.ConvexCount; c++)
            {
                ConvexBrepRange range = s.shape.Convex(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    s.shape.BankOf(c).vertices[range.vertexBase + v] += new float3(1000f, 1000f, 1000f);
                }
            }

            ProvisionalOwnerCandidate candidate = Build(in input);

            AssertSideBoxesCameFromTheSource(candidate.PositiveShape, sourceLo, sourceHi, "the positive side");
            AssertSideBoxesCameFromTheSource(candidate.NegativeShape, sourceLo, sourceHi, "the negative side");
        }

        /// <summary>
        /// Every convex box a side holds is one of the source's own, unchanged. Matched by value, because which of the
        /// source's convexes a side was given is the classification's business and not this check's.
        /// </summary>
        private static void AssertSideBoxesCameFromTheSource(
            PhysicsOwnerShape side, float3[] sourceLo, float3[] sourceHi, string what)
        {
            Assert.That(side, Is.Not.Null, what + ": there is a shape");
            for (int c = 0; c < side.ConvexCount; c++)
            {
                side.ConvexBounds(c, out float3 lo, out float3 hi);
                bool matched = false;
                for (int from = 0; from < sourceLo.Length && !matched; from++)
                {
                    matched = lo.Equals(sourceLo[from]) && hi.Equals(sourceHi[from]);
                }

                Assert.That(
                    matched, Is.True,
                    what + ": convex " + c + "'s box [" + lo + ", " + hi
                    + "] is one the source already held, not one drawn from where the vertices are now");
            }
        }

        /// <summary>
        /// A collider whose stored bounds fall slightly **inside** the convex -- which is what the centre-and-size
        /// pair of floats a mesh keeps can do to the numbers that were measured -- is still wholly inside the box the
        /// shape keeps. The overhang is measured first, so that a run where the rounding happened not to bite says so
        /// rather than passing on nothing.
        /// </summary>
        [Test]
        public void AColliderWhoseBoundsFallInsideItsConvex_IsStillWhollyInsideTheShapesBox()
        {
            var harness = new OwnerCutHarness { planeN = new float3(0f, 1f, 0f), planeW = 0f, eps = 1e-5f, parentMass = ParentMass };

            // Values chosen so that the centre-and-size round trip a Mesh does is not exact.
            harness.Add(new ConvexPoly(
                new[]
                {
                    new double3(0.1, 0.1, 0.1), new double3(0.2, 0.1, 0.1), new double3(0.2, 0.2, 0.1), new double3(0.1, 0.2, 0.1),
                    new double3(0.1, 0.1, 0.2), new double3(0.2, 0.1, 0.2), new double3(0.2, 0.2, 0.2), new double3(0.1, 0.2, 0.2),
                },
                new[]
                {
                    new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                    new[] { 1, 2, 6, 5 }, new[] { 2, 3, 7, 6 }, new[] { 3, 0, 4, 7 },
                }));
            harness.Build();
            _disposables.Add(harness);

            ConvexBrepRange range = harness.input.convexes[0];
            var measuredLo = new float3(float.PositiveInfinity);
            var measuredHi = new float3(float.NegativeInfinity);
            for (int v = 0; v < range.vertexCount; v++)
            {
                float3 at = harness.input.bank.vertices[range.vertexBase + v];
                measuredLo = math.min(measuredLo, at);
                measuredHi = math.max(measuredHi, at);
            }

            // A mesh given exactly those numbers the way a cooked collider is given them: as a centre and a size.
            var mesh = new Mesh { name = "Rounded", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[] { (Vector3)measuredLo, (Vector3)measuredHi, (Vector3)((measuredLo + measuredHi) * 0.5f) };
            float3 centre = (measuredLo + measuredHi) * 0.5f;
            mesh.bounds = new Bounds(centre, measuredHi - measuredLo);

            var source = PhysicsShapeSource.External();
            PhysicsOwnerShape shape = null;
            try
            {
                float3 storedLo = mesh.bounds.min;
                float3 storedHi = mesh.bounds.max;
                Assert.That(
                    math.any(storedLo > measuredLo) || math.any(storedHi < measuredHi), Is.True,
                    "the round trip really did fall inside: measured [" + measuredLo + ", " + measuredHi
                    + "] came back as [" + storedLo + ", " + storedHi + "]");

                shape = PhysicsOwnerShape.Authored(
                    harness.input.bank, new[] { range }, new List<Mesh> { mesh }, source, float4x4.identity);
                Assert.That(shape.TryLocalBounds(out float3 lo, out float3 hi), Is.True, "the shape has a box");
                ConvexBrepRange inShape = shape.Convex(0);
                for (int v = 0; v < inShape.vertexCount; v++)
                {
                    float3 at = shape.BankOf(0).vertices[inShape.vertexBase + v];
                    Assert.That(
                        math.all(at >= lo) && math.all(at <= hi), Is.True,
                        "vertex " + v + " at " + at + " is inside [" + lo + ", " + hi + "], with nothing allowed for");
                }
            }
            finally
            {
                shape?.Dispose();
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// A side built from a subset of the source's convexes keeps a box of its own that holds that subset. Both
        /// sides of a real provisional pair are checked, each against the convexes it was actually given -- not
        /// against the source's.
        /// </summary>
        [Test]
        public void EachProvisionalSidesBox_HoldsTheConvexesThatSideWasGiven()
        {
            Source s = NewSource(
                float4x4.identity, new double3(0.0, 3.0, 0.0), new double3(0.0, -3.0, 0.0), new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerCandidate candidate = Build(
                NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors()));

            AssertBoxHoldsItsOwnConvexes(candidate.PositiveShape, "the positive side");
            AssertBoxHoldsItsOwnConvexes(candidate.NegativeShape, "the negative side");
        }

        /// <summary>
        /// **Every produced convex's box is the rule itself, on a real cut's products**: what the job measured for
        /// that convex, widened by its mesh's own bounds, each end judged finite on its own. The mesh is asked once
        /// now instead of twice, and this says the answer did not change with it.
        /// <para>
        /// Beside the boxes it checks that an inherited convex keeps the parent's box and **the parent's mesh**, in
        /// the order the products name them, and that the side's own box is the union of its convexes'. **That is
        /// the whole of what this case checks**: the banks a side addresses and the holds it takes are checked by
        /// <c>PhysicsOwnerShapeBorrowTests</c>, and nothing here repeats them.
        /// </para>
        /// <para>
        /// Run twice: in the identity frame, and in one that is not. The parent is built in **the same frame the
        /// products were cut in**, since both describe the same numerical coordinates; the boxes are in that
        /// numerical local frame and the transform is carried, not applied, so the numbers are the same in both runs
        /// and each shape reports the frame it was given.
        /// </para>
        /// </summary>
        [Test]
        public void EachProducedConvexsBox_IsTheJobsExtremesWidenedByItsMesh()
        {
            foreach (float4x4 localToOwner in new[]
                     {
                         float4x4.identity,
                         float4x4.TRS(new float3(3f, -2f, 5f), quaternion.RotateY(0.7f), new float3(1f, 1f, 1f)),
                     })
            {
                string frame = localToOwner.Equals(float4x4.identity) ? "identity frame" : "a moved frame";
                PhysicsCutProducts products = CutProductsOf(localToOwner, out OwnerCutHarness h, frame);
                PhysicsOwnerShape parent = ParentOf(h, localToOwner);
                var productsSource = PhysicsShapeSource.For(products);
                PhysicsOwnerShape positive = Track(PhysicsOwnerShape.OfSide(parent, products, productsSource, true));
                PhysicsOwnerShape negative = Track(PhysicsOwnerShape.OfSide(parent, products, productsSource, false));

                Assert.That(positive.LocalToOwner, Is.EqualTo(localToOwner), frame + ": the side carries the frame it was cut in");
                Assert.That(negative.LocalToOwner, Is.EqualTo(localToOwner), frame + ": and so does the other side");

                foreach (bool side in new[] { true, false })
                {
                    PhysicsOwnerShape shape = side ? positive : negative;
                    string what = frame + (side ? " positive" : " negative");
                    Assert.That(
                        shape.ConvexCount, Is.EqualTo(products.PartCount(side)),
                        what + ": one convex per part the products name");

                    var unionLo = new float3(float.PositiveInfinity);
                    var unionHi = new float3(float.NegativeInfinity);
                    int produced = 0;
                    for (int i = 0; i < shape.ConvexCount; i++)
                    {
                        PhysicsCutPart part = products.Part(side, i);
                        shape.ConvexBounds(i, out float3 lo, out float3 hi);
                        unionLo = math.min(unionLo, lo);
                        unionHi = math.max(unionHi, hi);

                        if (part.borrowed)
                        {
                            parent.ConvexBounds(part.inputConvex, out float3 pLo, out float3 pHi);
                            Assert.That(lo, Is.EqualTo(pLo).Using(Float3Exactly), what + " " + i + ": inherited low");
                            Assert.That(hi, Is.EqualTo(pHi).Using(Float3Exactly), what + " " + i + ": inherited high");
                            Assert.That(
                                shape.MeshOf(i), Is.SameAs(parent.MeshOf(part.inputConvex)),
                                what + " " + i + ": inherited mesh");
                            continue;
                        }

                        produced++;
                        Assert.That(shape.MeshOf(i), Is.SameAs(part.mesh), what + " " + i + ": the produced mesh");
                        RuleBox(part, out float3 ruleLo, out float3 ruleHi);
                        Assert.That(lo, Is.EqualTo(ruleLo).Using(Float3Exactly), what + " " + i + ": produced low");
                        Assert.That(hi, Is.EqualTo(ruleHi).Using(Float3Exactly), what + " " + i + ": produced high");
                        Assert.That(
                            math.all(lo <= part.bounds.c0), Is.True,
                            what + " " + i + ": the box holds the job's low " + part.bounds.c0 + " (box low " + lo + ")");
                        Assert.That(
                            math.all(hi >= part.bounds.c1), Is.True,
                            what + " " + i + ": the box holds the job's high " + part.bounds.c1 + " (box high " + hi + ")");
                    }

                    Assert.That(produced, Is.GreaterThan(0), what + ": this side really has produced convexes");
                    Assert.That(
                        shape.TryLocalBounds(out float3 shapeLo, out float3 shapeHi), Is.True,
                        what + ": the side has a usable box");
                    Assert.That(shapeLo, Is.EqualTo(unionLo).Using(Float3Exactly), what + ": the side's low is the union");
                    Assert.That(shapeHi, Is.EqualTo(unionHi).Using(Float3Exactly), what + ": the side's high is the union");
                }
            }
        }

        /// <summary>
        /// **The situation the rounding argument is about, made on purpose.** A produced convex's mesh is given a box
        /// that lies **inside** what the job measured for that convex, which is what a centre-and-size pair of floats
        /// can come out as; another is given one that reaches past the job's high end only. The first convex's box
        /// must be the job's extremes untouched -- the inside mesh pulls neither end in -- and the second's must be
        /// widened at the high end and left alone at the low one, which is the two ends being judged apart.
        /// </summary>
        [Test]
        public void AMeshBoxInsideTheJobsExtremes_PullsNeitherEndIn()
        {
            PhysicsCutProducts products = CutProductsOf(float4x4.identity, out OwnerCutHarness h, "shrunk mesh");
            PhysicsOwnerShape parent = ParentOf(h, float4x4.identity);

            // The produced parts of the positive side, and the two this case arranges.
            var produced = new List<int>();
            for (int i = 0; i < products.PartCount(true); i++)
            {
                if (!products.Part(true, i).borrowed && products.Part(true, i).mesh != null)
                {
                    produced.Add(i);
                }
            }

            Assert.That(produced.Count, Is.GreaterThanOrEqualTo(2), "this cut produces at least two convexes on the positive side");

            // **Inside on every axis.** Half the job's box, about its centre.
            PhysicsCutPart inside = products.Part(true, produced[0]);
            float3 insideLo = part_lo(inside), insideHi = part_hi(inside);
            float3 centre = 0.5f * (insideLo + insideHi);
            float3 half = 0.25f * (insideHi - insideLo);
            inside.mesh.bounds = new Bounds(centre, 2f * half);
            Assert.That(
                math.all((float3)inside.mesh.bounds.min > insideLo) && math.all((float3)inside.mesh.bounds.max < insideHi),
                Is.True,
                "the mesh's box really is inside the job's on every axis");

            // **Past the high end only**, on x: the low end stays inside, the high end reaches out.
            PhysicsCutPart outsideHigh = products.Part(true, produced[1]);
            float3 outLo = part_lo(outsideHigh), outHi = part_hi(outsideHigh);
            var reach = new float3(1f, 0f, 0f);
            float3 wantedLo = outLo + (0.25f * (outHi - outLo));
            float3 wantedHi = outHi + reach;
            outsideHigh.mesh.bounds = new Bounds(
                (Vector3)(0.5f * (wantedLo + wantedHi)), (Vector3)(wantedHi - wantedLo));
            Assert.That(
                math.all((float3)outsideHigh.mesh.bounds.min > outLo), Is.True,
                "its low end is inside the job's");
            Assert.That(
                ((float3)outsideHigh.mesh.bounds.max).x > outHi.x, Is.True,
                "and its high end reaches past it");

            var productsSource = PhysicsShapeSource.For(products);
            PhysicsOwnerShape side = Track(PhysicsOwnerShape.OfSide(parent, products, productsSource, true));

            side.ConvexBounds(produced[0], out float3 lo, out float3 hi);
            Assert.That(lo, Is.EqualTo(part_lo(inside)).Using(Float3Exactly), "the inside mesh did not pull the low end in");
            Assert.That(hi, Is.EqualTo(part_hi(inside)).Using(Float3Exactly), "nor the high end");

            side.ConvexBounds(produced[1], out float3 lo2, out float3 hi2);
            Assert.That(lo2, Is.EqualTo(part_lo(outsideHigh)).Using(Float3Exactly), "the low end is the job's, untouched");
            Assert.That(
                hi2, Is.EqualTo(math.max(part_hi(outsideHigh), (float3)outsideHigh.mesh.bounds.max)).Using(Float3Exactly),
                "and the high end is widened to the mesh's");
            Assert.That(hi2.x, Is.GreaterThan(part_hi(outsideHigh).x), "which really is past the job's high end");
        }

        /// <summary>
        /// **The ending, with a cut still out.** A second cut is submitted and its work put in flight, and then the
        /// case simply stops: the fixture's ending is what closes the runner, carries it to drained and stops the
        /// dispatcher. Nothing here asserts about that cut's result -- what is under test is that the ending itself
        /// confirms the stop and the drain before the input and the destinations are released.
        /// </summary>
        [Test]
        public void TheEndingDrainsARunnerThatStillHasACutOut()
        {
            PhysicsCutProducts products = CutProductsOf(float4x4.identity, out OwnerCutHarness h, "with a cut still out");
            Assert.That(products, Is.Not.Null, "the first cut finished");

            // A second cut, left in flight. The ending registered by the helper is what finishes it.
            PhysicsCutRequest outstanding = LastCook.Submit(in h.input, float4x4.identity);
            LastDispatcher.BeginFrame(1);
            LastDispatcher.Dispatch();
            LastCook.Pump();
            Assert.That(outstanding.IsOver, Is.False, "it is still out when this case ends");
        }

        private PhysicsCutCook LastCook { get; set; }

        private SharedWorkDispatcher LastDispatcher { get; set; }

        private static float3 part_lo(PhysicsCutPart part)
        {
            return part.bounds.c0;
        }

        private static float3 part_hi(PhysicsCutPart part)
        {
            return part.bounds.c1;
        }

        /// <summary>The rule a produced convex's box is made by, as this file reads it.</summary>
        private static void RuleBox(PhysicsCutPart part, out float3 lo, out float3 hi)
        {
            lo = part.bounds.c0;
            hi = part.bounds.c1;
            if (part.mesh == null)
            {
                return;
            }

            float3 meshLo = part.mesh.bounds.min;
            if (math.all(math.isfinite(meshLo)))
            {
                lo = math.min(lo, meshLo);
            }

            float3 meshHi = part.mesh.bounds.max;
            if (math.all(math.isfinite(meshHi)))
            {
                hi = math.max(hi, meshHi);
            }
        }

        /// <summary>
        /// One real cut and cook in the given frame. **Everything it makes is registered for release as it is made**,
        /// so a failure anywhere after -- including before any try block of the caller -- still gives it all back.
        /// <para>
        /// The ending is the one the cook's own fixture uses (<c>PhysicsCutCookTests.Fixture.Dispose</c>): the runner
        /// is closed, carried to <see cref="PhysicsCutCook.IsDrained"/>, and the dispatcher is stopped and **asked
        /// what it managed** -- and only then are the input and the destinations let go. A stop that is not confirmed
        /// leaves the input **not** treated as safe to release, so the ending fails there rather than freeing a bank
        /// a worker may still be reading. It is registered **after** them, and the fixture releases in reverse
        /// order, so it runs first.
        /// </para>
        /// </summary>
        private PhysicsCutProducts CutProductsOf(float4x4 localToOwner, out OwnerCutHarness harness, string what)
        {
            OwnerCutHarness h = MixedCompoundForCook();
            _disposables.Add(h);
            harness = h;

            var job = new UnityJobWorkExecutor(4);
            WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(2);
            _disposables.Add(geometry);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(2);
            _disposables.Add(background);
            var dispatcher = new SharedWorkDispatcher(8, 2, 32, job, geometry, background);
            var cook = new PhysicsCutCook(dispatcher, 1);

            // Registered **after** the input and the destinations, so the fixture's reverse order runs it first.
            _disposables.Add(new Ending(() => EndTheCook(cook, dispatcher, what)));
            LastCook = cook;
            LastDispatcher = dispatcher;

            PhysicsCutRequest request = cook.Submit(in h.input, localToOwner);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!request.IsOver && clock.ElapsedMilliseconds < 30000)
            {
                dispatcher.BeginFrame((int)clock.ElapsedMilliseconds + 1);
                dispatcher.Dispatch();
                cook.Pump();
                System.Threading.Thread.Sleep(1);
            }

            Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), what + ": the cut and cook succeeded");
            PhysicsCutProducts products = request.Products;
            _disposables.Add(products);
            return products;
        }

        /// <summary>
        /// A parent to inherit from, in the same frame the products were cut in: one collider per convex, built from
        /// that convex. Registered for release as it is made.
        /// </summary>
        private PhysicsOwnerShape ParentOf(OwnerCutHarness h, float4x4 localToOwner)
        {
            var meshes = new List<Mesh>();
            _disposables.Add(new Ending(() =>
            {
                foreach (Mesh mesh in meshes)
                {
                    if (mesh != null)
                    {
                        UnityEngine.Object.DestroyImmediate(mesh);
                    }
                }
            }));

            var ranges = new ConvexBrepRange[h.input.convexCount];
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = h.input.convexes[c];
                meshes.Add(ColliderOfRange(h.input.bank, ranges[c], "Parent " + c));
            }

            var source = PhysicsShapeSource.External();
            return Track(PhysicsOwnerShape.Authored(h.input.bank, ranges, meshes, source, localToOwner));
        }

        /// <summary>
        /// Closes one runner and its dispatcher and says whether everything came back, before anything the work was
        /// reading is released. Nothing generic: it is this file's two cases' ending, written the way the cook's own
        /// fixture writes it.
        /// </summary>
        private static void EndTheCook(PhysicsCutCook cook, SharedWorkDispatcher dispatcher, string what)
        {
            // Closing ends what was not submitted and marks what was; the caller carries it the rest of the way.
            cook.Dispose();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!cook.IsDrained && clock.ElapsedMilliseconds < 30000)
            {
                dispatcher.BeginFrame((int)clock.ElapsedMilliseconds + 1);
                dispatcher.Dispatch();
                cook.Pump();
            }

            // The stop itself, and what it managed. A run that is already drained still goes through it, because the
            // destinations are what hold the workers.
            DispatchShutdownResult stopped = dispatcher.Shutdown(30000);
            cook.Pump();

            Assert.That(
                stopped.workersStopped, Is.True,
                what + ": every destination confirmed it had stopped, so what the work was reading may be released"
                + " (cancelled " + stopped.cancelled + ", collected " + stopped.collected + ")");
            Assert.That(
                cook.IsDrained, Is.True,
                what + ": the closed runner gave every cut back before the input is released");
            Assert.That(
                cook.Reserving, Is.Zero, what + ": and holds no reservation");
            Assert.That(
                dispatcher.WaitingCount, Is.Zero, what + ": the dispatcher waits for nothing");
            Assert.That(
                dispatcher.SubmittedCount, Is.Zero, what + ": and holds nothing");
        }

        private PhysicsOwnerShape Track(PhysicsOwnerShape shape)
        {
            _disposables.Add(shape);
            return shape;
        }

        /// <summary>Something to run at teardown, in the fixture's own order.</summary>
        private sealed class Ending : IDisposable
        {
            private readonly Action _run;

            internal Ending(Action run)
            {
                _run = run;
            }

            public void Dispose()
            {
                _run();
            }
        }

        /// <summary>Two boxes are the same box when every one of their six numbers is the same number.</summary>
        private static readonly IComparer<float3> Float3Exactly = new ExactFloat3();

        private sealed class ExactFloat3 : IComparer<float3>
        {
            public int Compare(float3 a, float3 b)
            {
                return math.all(a == b) ? 0 : 1;
            }
        }

        /// <summary>
        /// The shapes a real cut and cook produce keep boxes that hold their own convexes. The numbers come back from
        /// the product's own cook -- meshes written by the job and given the bounds it measured, which are floats and
        /// may have been rounded inwards on the way -- so this is where a box drawn from a collider alone would come
        /// up short.
        /// </summary>
        [Test]
        public void EachCutSidesBox_HoldsTheConvexesThatSideWasGiven()
        {
            var job = new UnityJobWorkExecutor(4);
            using (OwnerCutHarness h = MixedCompoundForCook())
            using (WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(2))
            using (WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(2))
            {
                var dispatcher = new SharedWorkDispatcher(8, 2, 32, job, geometry, background);
                using (var cook = new PhysicsCutCook(dispatcher, 1))
                {
                    PhysicsCutRequest request = cook.Submit(in h.input, float4x4.identity);
                    var clock = System.Diagnostics.Stopwatch.StartNew();
                    while (!request.IsOver && clock.ElapsedMilliseconds < 30000)
                    {
                        dispatcher.BeginFrame((int)clock.ElapsedMilliseconds + 1);
                        dispatcher.Dispatch();
                        cook.Pump();
                        System.Threading.Thread.Sleep(1);
                    }

                    Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the cut and cook succeeded");
                    PhysicsCutProducts products = request.Products;
                    var productsSource = PhysicsShapeSource.For(products);

                    // A parent to inherit from, with a collider per convex built from that convex.
                    var parentMeshes = new List<Mesh>();
                    var parentRanges = new ConvexBrepRange[h.input.convexCount];
                    for (int c = 0; c < parentRanges.Length; c++)
                    {
                        parentRanges[c] = h.input.convexes[c];
                        parentMeshes.Add(ColliderOfRange(h.input.bank, parentRanges[c], "Parent " + c));
                    }

                    var parentSource = PhysicsShapeSource.External();
                    PhysicsOwnerShape parent = PhysicsOwnerShape.Authored(
                        h.input.bank, parentRanges, parentMeshes, parentSource, float4x4.identity);
                    PhysicsOwnerShape positive = PhysicsOwnerShape.OfSide(parent, products, productsSource, true);
                    PhysicsOwnerShape negative = PhysicsOwnerShape.OfSide(parent, products, productsSource, false);
                    try
                    {
                        AssertBoxHoldsItsOwnConvexes(positive, "the positive side of a real cut");
                        AssertBoxHoldsItsOwnConvexes(negative, "the negative side of a real cut");
                    }
                    finally
                    {
                        positive.Dispose();
                        negative.Dispose();
                        parent.Dispose();
                        products.Dispose();
                        foreach (Mesh mesh in parentMeshes)
                        {
                            UnityEngine.Object.DestroyImmediate(mesh);
                        }
                    }
                }

                dispatcher.Shutdown(30000);
            }
        }

        /// <summary>
        /// A source whose convexes 2 and 3 are the same box in the same place and share one cooked collider mesh, so
        /// that "which collider carries this mesh" cannot tell the two apart. The plane crosses convex 0 and leaves
        /// convex 1 below it, so the positive side inherits 2 and 3 and has the cut's own convex besides.
        /// </summary>
        private Source NewSourceWhereTwoConvexesShareAMesh()
        {
            var polys = new List<ConvexPoly>
            {
                Translated(CaseGenerator.Box(), new double3(0.0, 0.0, 0.0)),
                Translated(CaseGenerator.Box(), new double3(0.0, -3.0, 0.0)),
                Translated(CaseGenerator.Box(), new double3(0.0, 3.0, 0.0)),
                Translated(CaseGenerator.Box(), new double3(0.0, 3.0, 0.0)),
            };

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

            var ranges = new ConvexBrepRange[s.harness.input.convexCount];
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = s.harness.input.convexes[c];
            }

            // One mesh object for the last two convexes: they stand in the same place, so the same cooked shape
            // covers both and the entry check admits it.
            Mesh shared = CookedConvex(polys[2], "Source 2 and 3");
            s.meshes = new List<Mesh>
            {
                CookedConvex(polys[0], "Source 0"),
                CookedConvex(polys[1], "Source 1"),
                shared,
                shared,
            };

            s.source = PhysicsShapeSource.External();
            s.shape = PhysicsOwnerShape.Authored(s.harness.input.bank, ranges, s.meshes, s.source, float4x4.identity);
            _disposables.Add(s);
            return s;
        }

        /// <summary>The cut and cook of one owner input, run here to the end, so that a real set of products exists.</summary>
        private static PhysicsCutProducts CookProducts(in ConvexCutOwnerInput input, List<IDisposable> disposables)
        {
            var job = new UnityJobWorkExecutor(4);
            WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(2);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(2);
            var dispatcher = new SharedWorkDispatcher(8, 2, 32, job, geometry, background);
            var cook = new PhysicsCutCook(dispatcher, 1);
            try
            {
                PhysicsCutRequest request = cook.Submit(in input, float4x4.identity);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (!request.IsOver && clock.ElapsedMilliseconds < 30000)
                {
                    dispatcher.BeginFrame((int)clock.ElapsedMilliseconds + 1);
                    dispatcher.Dispatch();
                    cook.Pump();
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the cut and cook succeeded");
                PhysicsCutProducts products = request.Products;
                disposables.Add(products);
                return products;
            }
            finally
            {
                cook.Dispose();
                dispatcher.Shutdown(30000);
                geometry.Dispose();
                background.Dispose();
            }
        }

        [Test]
        public void EachInheritedPart_KeepsTheColliderOfItsOwnConvex_WhenTwoConvexesShareAMesh()
        {
            Source s = NewSourceWhereTwoConvexesShareAMesh();
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split), "the plane crosses the first box");
            Assert.That(input.sides[1], Is.EqualTo(ConvexSide.Negative), "the second is below it");
            Assert.That(input.sides[2], Is.EqualTo(ConvexSide.Positive), "and the last two above it");
            Assert.That(input.sides[3], Is.EqualTo(ConvexSide.Positive));

            ProvisionalOwnerCandidate candidate = Build(in input);
            PhysicsOwnerSide side = candidate.Positive;
            PhysicsOwnerShape sideShape = candidate.PositiveShape;
            Assert.That(sideShape.ConvexCount, Is.EqualTo(3), "the crossed convex and the two above");
            Assert.That(side.Colliders.Count, Is.EqualTo(3), "one collider per convex of the side");
            Assert.That(
                new[] { sideShape.InputConvexOf(0), sideShape.InputConvexOf(1), sideShape.InputConvexOf(2) },
                Is.EqualTo(new[] { 0, 2, 3 }),
                "the side knows which convex of the source each of its own is");
            Assert.That(side.Colliders[1].sharedMesh, Is.SameAs(side.Colliders[2].sharedMesh), "and those two carry one mesh");

            var before = new List<MeshCollider>(side.Colliders);
            PhysicsCutProducts products = CookProducts(in s.harness.input, _disposables);
            var parts = new List<PhysicsCutPart>();
            for (int i = 0; i < products.PartCount(true); i++)
            {
                parts.Add(products.Part(true, i));
            }

            Assert.That(parts.Count, Is.EqualTo(3), "the cut's own half of convex 0, and the two inherited ones");
            Assert.That(parts.FindAll(p => p.borrowed).Count, Is.EqualTo(2), "two inherited parts");

            PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                products, s.shape.Meshes, side, sideShape,
                side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
            Assert.That(prepared.Count, Is.EqualTo(parts.Count), "one final collider per part");
            Assert.That(prepared.KeptCount, Is.EqualTo(2), "both inherited parts kept a collider");
            Assert.That(prepared.MadeCount, Is.EqualTo(1), "and only the produced part got a new one");

            prepared.Adopt(side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);

            for (int i = 0; i < parts.Count; i++)
            {
                if (!parts[i].borrowed)
                {
                    Assert.That(
                        before.Contains(side.Colliders[i]), Is.False,
                        "the produced part has a collider of its own, none of the side's old ones");
                    continue;
                }

                // **The correspondence, at the level of the instance.** The side's convex for this part's input
                // convex is where its collider was, and that very object is what the final set holds -- not merely
                // one that carries the same mesh.
                int at = -1;
                for (int j = 0; j < sideShape.ConvexCount; j++)
                {
                    if (sideShape.InputConvexOf(j) == parts[i].inputConvex)
                    {
                        at = j;
                    }
                }

                Assert.That(at, Is.GreaterThanOrEqualTo(0), "the side has a convex for this part");
                Assert.That(side.Colliders[i], Is.SameAs(before[at]), "part " + i + " kept the collider of its own convex");
                Assert.That(side.Colliders[i].enabled, Is.True, "and it never stopped answering");
            }

            Assert.That(side.Colliders[1], Is.Not.SameAs(side.Colliders[2]), "no collider is kept twice");
            Assert.That(before[0] == null, Is.True, "the crossed convex's collider is the one that was replaced and destroyed");
            Assert.That(side.ProducedColliderCount, Is.EqualTo(1), "one convex of this side was produced by the cut");
        }

        /// <summary>
        /// **A side every part of which was produced here keeps nothing, and gets a new collider for each part.**
        /// One box, crossed by the plane: each side is the cut's own half and nothing is inherited, so there is no
        /// part that could keep a collider.
        /// <para>
        /// What the side had is replaced, as it is for any part that cannot be kept: the preparation makes one
        /// collider per part and adopting them destroys what was there. The frame is not moved, and the side's own
        /// colliders are untouched while the preparation is being made.
        /// </para>
        /// </summary>
        [Test]
        public void ASideWhoseEveryPartWasProduced_KeepsNothing()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Split), "the plane crosses the one box");

            ProvisionalOwnerCandidate candidate = Build(in input);
            PhysicsCutProducts products = CookProducts(in s.harness.input, _disposables);

            foreach (bool positive in new[] { true, false })
            {
                PhysicsOwnerSide side = positive ? candidate.Positive : candidate.Negative;
                PhysicsOwnerShape sideShape = positive ? candidate.PositiveShape : candidate.NegativeShape;
                string what = positive ? "the positive side" : "the negative side";

                int count = products.PartCount(positive);
                Assert.That(count, Is.GreaterThan(0), what + ": it has parts");
                for (int i = 0; i < count; i++)
                {
                    Assert.That(
                        products.Part(positive, i).borrowed, Is.False,
                        what + ": part " + i + " was produced here, not inherited");
                }

                var before = new List<MeshCollider>(side.Colliders);
                Vector3 frameBefore = side.ShapeFrame.transform.localPosition;
                Quaternion turnBefore = side.ShapeFrame.transform.localRotation;

                PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                    products, s.shape.Meshes, side, sideShape,
                    side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);

                Assert.That(prepared.Count, Is.EqualTo(count), what + ": one final collider per part");
                Assert.That(prepared.KeptCount, Is.Zero, what + ": nothing of the side was kept");
                Assert.That(prepared.MadeCount, Is.EqualTo(count), what + ": every one was made here");

                // Until they are adopted, the side is what it was.
                Assert.That(side.Colliders.Count, Is.EqualTo(before.Count), what + ": the side still has its own");
                for (int i = 0; i < before.Count; i++)
                {
                    Assert.That(
                        ReferenceEquals(side.Colliders[i], before[i]), Is.True,
                        what + ": collider " + i + " is the very same one");
                    Assert.That(before[i] != null && before[i].enabled, Is.True, what + ": and still answering");
                }

                Assert.That(
                    side.ShapeFrame.transform.localPosition, Is.EqualTo(frameBefore), what + ": the frame is where it was");
                Assert.That(
                    Quaternion.Angle(side.ShapeFrame.transform.localRotation, turnBefore), Is.LessThan(1e-3f), what);

                prepared.Adopt(side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
                Assert.That(side.Colliders.Count, Is.EqualTo(count), what + ": and takes the new set");
                foreach (MeshCollider had in before)
                {
                    Assert.That(had == null, Is.True, what + ": what it had was replaced and destroyed");
                }

                Assert.That(
                    side.ProducedColliderCount, Is.EqualTo(count), what + ": every convex of it came from the cut");
            }
        }

        /// <summary>
        /// **A side whose very first part is a borrowed one.** The convexes are ordered so that the one the plane
        /// leaves wholly on this side comes before the one it crosses, so the preparation is reached on the first
        /// turn of the loop rather than after some colliders have been made. The kept collider is that convex's own.
        /// </summary>
        [Test]
        public void ASideWhoseFirstPartIsBorrowed_PreparesOnThatFirstTurn()
        {
            // Convex 0 is wholly above the plane; convex 1 is crossed by it.
            Source s = NewSource(float4x4.identity, new double3(0.0, 3.0, 0.0), new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            Assert.That(input.sides[0], Is.EqualTo(ConvexSide.Positive), "the first convex is wholly above");
            Assert.That(input.sides[1], Is.EqualTo(ConvexSide.Split), "and the second is crossed");

            ProvisionalOwnerCandidate candidate = Build(in input);
            PhysicsOwnerSide side = candidate.Positive;
            PhysicsOwnerShape sideShape = candidate.PositiveShape;
            var before = new List<MeshCollider>(side.Colliders);
            PhysicsCutProducts products = CookProducts(in s.harness.input, _disposables);

            Assert.That(products.PartCount(true), Is.GreaterThan(1), "the positive side has more than one part");
            Assert.That(
                products.Part(true, 0).borrowed, Is.True,
                "**the very first part is a borrowed one**, which is what this case is about");
            Assert.That(products.Part(true, 0).inputConvex, Is.Zero, "and it is the convex left wholly above");

            PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                products, s.shape.Meshes, side, sideShape,
                side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);

            Assert.That(prepared.Count, Is.EqualTo(products.PartCount(true)), "one final collider per part");
            Assert.That(prepared.KeptCount, Is.EqualTo(1), "the first part kept the collider of its own convex");
            Assert.That(
                prepared.KeepsSideCollider(0), Is.True,
                "and what it kept is the collider the side had for that convex");
            Assert.That(
                prepared.KeepsSideCollider(1), Is.False,
                "while the crossed convex's is not kept");
            Assert.That(prepared.MadeCount, Is.EqualTo(prepared.Count - 1), "the rest were made here");

            prepared.Adopt(side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
            Assert.That(side.Colliders, Has.Member(before[0]), "the kept one is still the side's");
            Assert.That(before[1] == null, Is.True, "and the crossed convex's was replaced and destroyed");
        }

        /// <summary>
        /// **A preparation that throws after it has already made a collider takes that collider off again.** The
        /// entry is given a list of inherited meshes that is too short for the parts it will be asked about, so the
        /// borrowed part -- which comes after a produced one -- cannot be looked up and the call throws. What was
        /// made for the produced part comes off, and the side is exactly as it was.
        /// <para>
        /// **This is the recovery of the entry itself.** The handoff would not reach here with meshes missing: it
        /// asks <see cref="PhysicsOwnerBuilder.FinalShapesArePresent"/> first. And the throw this case makes is the
        /// mesh lookup, not the preparation -- **that the preparation's own throw would be caught by this same
        /// catch is read from the code** (it sits inside that try), not shown here.
        /// </para>
        /// </summary>
        [Test]
        public void APreparationThatThrowsAfterMakingOne_TakesItOffAndLeavesTheSide()
        {
            Source s = NewSourceWhereTwoConvexesShareAMesh();
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            ProvisionalOwnerCandidate candidate = Build(in input);
            PhysicsOwnerSide side = candidate.Positive;
            PhysicsOwnerShape sideShape = candidate.PositiveShape;
            PhysicsCutProducts products = CookProducts(in s.harness.input, _disposables);

            Assert.That(products.Part(true, 0).borrowed, Is.False, "the first part is produced here");
            Assert.That(products.Part(true, 1).borrowed, Is.True, "and a borrowed one comes after it");

            var before = new List<MeshCollider>(side.Colliders);
            int componentsBefore = side.ShapeFrame.GetComponents<MeshCollider>().Length;
            int producedBefore = side.ProducedColliderCount;

            // Too short for the convex the borrowed part names: the lookup for it cannot be made.
            var tooShort = new List<Mesh> { s.shape.Meshes[0] };
            Assert.That(
                () => PhysicsOwnerBuilder.PrepareFinalColliders(
                    products, tooShort, side, sideShape,
                    side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition),
                Throws.InstanceOf<ArgumentOutOfRangeException>(),
                "the call throws once it reaches the borrowed part");

            Assert.That(
                side.ShapeFrame.GetComponents<MeshCollider>().Length, Is.EqualTo(componentsBefore),
                "what it had made for the produced part is off the actor again");
            Assert.That(side.Colliders.Count, Is.EqualTo(before.Count), "the side has the colliders it had");
            for (int i = 0; i < before.Count; i++)
            {
                Assert.That(
                    ReferenceEquals(side.Colliders[i], before[i]), Is.True, "the very same collider " + i);
                Assert.That(before[i] != null && before[i].enabled, Is.True, "still answering");
            }

            Assert.That(side.ProducedColliderCount, Is.EqualTo(producedBefore), "and the count is as it was");

            // The side is still preparable: giving it the meshes it needs makes an ordinary set.
            PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                products, s.shape.Meshes, side, sideShape,
                side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
            Assert.That(prepared.Count, Is.EqualTo(products.PartCount(true)), "one per part, as if nothing had happened");
            prepared.Adopt(side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
        }

        [Test]
        public void AColliderTheConditionsNoLongerAdmit_IsMadeAnew_AndTheOtherIsStillKept()
        {
            Source s = NewSourceWhereTwoConvexesShareAMesh();
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            ProvisionalOwnerCandidate candidate = Build(in input);
            PhysicsOwnerSide side = candidate.Positive;
            PhysicsOwnerShape sideShape = candidate.PositiveShape;
            var before = new List<MeshCollider>(side.Colliders);

            // The collider of the side's convex for source convex 2 stops answering: it is no longer a candidate,
            // and the part that would have kept it must get one of its own.
            before[1].enabled = false;

            PhysicsCutProducts products = CookProducts(in s.harness.input, _disposables);
            PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                products, s.shape.Meshes, side, sideShape,
                side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
            Assert.That(prepared.KeptCount, Is.EqualTo(1), "only the convex whose collider still answers is kept");
            Assert.That(prepared.MadeCount, Is.EqualTo(2), "the produced part and the one that could not be kept");

            prepared.Adopt(side.ShapeFrame.transform.localRotation, side.ShapeFrame.transform.localPosition);
            Assert.That(side.Colliders.Count, Is.EqualTo(3));
            Assert.That(side.Colliders, Has.Member(before[2]), "the other inherited convex kept its own collider");
            Assert.That(before[0] == null && before[1] == null, Is.True, "what was replaced was destroyed");
        }

        [Test]
        public void AFrameThatTheSwitchWouldMove_KeepsNothing_AndEveryPartGetsANewCollider()
        {
            Source s = NewSourceWhereTwoConvexesShareAMesh();
            var plane = new float4(0f, 1f, 0f, 0f);
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            ProvisionalOwnerCandidate candidate = Build(in input);
            PhysicsOwnerSide side = candidate.Positive;
            var before = new List<MeshCollider>(side.Colliders);

            PhysicsCutProducts products = CookProducts(in s.harness.input, _disposables);
            var movedTo = new float3(0f, 0.25f, 0f);
            PreparedSideColliders prepared = PhysicsOwnerBuilder.PrepareFinalColliders(
                products, s.shape.Meshes, side, candidate.PositiveShape,
                side.ShapeFrame.transform.localRotation, movedTo);
            Assert.That(prepared.KeptCount, Is.Zero, "a frame the switch would move keeps nothing");
            Assert.That(prepared.MadeCount, Is.EqualTo(prepared.Count), "every part got a collider of its own");

            prepared.Adopt(side.ShapeFrame.transform.localRotation, movedTo);
            Assert.That(before[0] == null && before[1] == null && before[2] == null, Is.True, "and all three old ones went");
            Vector3 at = side.ShapeFrame.transform.localPosition;
            Assert.That(at.y, Is.EqualTo(movedTo.y).Within(1e-5f), "the frame moved as the switch said");
        }

        /// <summary>A compound the plane really cuts, for the cook to have something to produce.</summary>
        private static OwnerCutHarness MixedCompoundForCook()
        {
            var h = new OwnerCutHarness
            {
                planeN = new float3(0f, 1f, 0f),
                planeW = 0f,
                eps = 1e-5f,
                parentMass = ParentMass,
            };
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 0.0, 0.0)));
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, 0.0, 0.0)));
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 3.0, 0.0)));
            h.Build();
            return h;
        }

        /// <summary>
        /// A shape's own box holds every vertex of every convex that shape is made of, with nothing allowed for. The
        /// slack the authored entry allows is that check's; a box that is kept has to hold what is in it.
        /// </summary>
        private static void AssertBoxHoldsItsOwnConvexes(PhysicsOwnerShape shape, string what)
        {
            Assert.That(shape, Is.Not.Null, what + ": there is a shape");
            Assert.That(shape.TryLocalBounds(out float3 lo, out float3 hi), Is.True, what + ": it has a box");
            for (int c = 0; c < shape.ConvexCount; c++)
            {
                ConvexBrepRange range = shape.Convex(c);
                Assert.That(range.vertexCount, Is.GreaterThan(0), what + ": convex " + c + " has vertices");
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 at = shape.BankOf(c).vertices[range.vertexBase + v];
                    Assert.That(
                        math.all(at >= lo) && math.all(at <= hi), Is.True,
                        what + ": convex " + c + " vertex " + v + " at " + at + " is inside [" + lo + ", " + hi + "]");
                }

                Mesh mesh = shape.MeshOf(c);
                if (mesh != null)
                {
                    Bounds bounds = mesh.bounds;
                    Assert.That(
                        math.all((float3)bounds.min >= lo) && math.all((float3)bounds.max <= hi), Is.True,
                        what + ": the collider of convex " + c + " is inside the box too");
                }
            }
        }

        /// <summary>
        /// An authored collider whose bounds do not cover the convex it is given for is refused where it is taken in,
        /// not carried into a box that would be too small. A mesh in another frame -- here, the same collider moved
        /// away from its convex -- is the same fault and shows up the same way.
        /// </summary>
        [Test]
        public void AnAuthoredColliderThatDoesNotEncloseItsConvex_IsRefusedAtTheEntry()
        {
            var harness = new OwnerCutHarness { planeN = new float3(0f, 1f, 0f), planeW = 0f, eps = 1e-5f, parentMass = ParentMass };
            harness.Add(CaseGenerator.Box());
            harness.Build();
            _disposables.Add(harness);

            // The convex's own collider, moved away from it: the bounds no longer cover the convex.
            Mesh elsewhere = CookedConvex(Translated(CaseGenerator.Box(), new double3(10.0, 0.0, 0.0)), "Elsewhere");
            var ranges = new[] { harness.input.convexes[0] };
            var source = PhysicsShapeSource.External();
            try
            {
                ArgumentException thrown = Assert.Throws<ArgumentException>(
                    () => PhysicsOwnerShape.Authored(harness.input.bank, ranges, new List<Mesh> { elsewhere }, source, float4x4.identity),
                    "a collider that does not enclose its convex is refused");
                Assert.That(thrown.Message, Does.Contain("does not enclose"), "and says so");
                Assert.That(source.Users, Is.Zero, "with no hold taken on whoever owns it");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(elsewhere);
            }
        }

        /// <summary>
        /// Nothing walks the source's vertices to draw the box the masses come from. Every vertex of the source's
        /// bank is moved far away after that shape was made and before the pair is built: a build that drew its box
        /// from them would come back with different masses, and one that uses the box settled when the shape was made
        /// comes back with the same.
        /// <para>
        /// **This is about the box alone.** Building a pair still copies the source's B-rep into each side's own bank,
        /// as every shape's construction does; that copy is unchanged here. Each side's own box is **not** worked out
        /// from what it copied -- it inherits the source's box for each convex it was given, which
        /// <see cref="BuildingASide_TakesItsBoxFromTheSource_AndWalksNoVertex"/> is about. Neither is what the masses
        /// are drawn from: those come from the source shape's own box.
        /// </para>
        /// </summary>
        [Test]
        public void NothingScansTheSourcesVerticesForTheBox_SoMovingThemAfterwardsChangesTheMassesNotAtAll()
        {
            Source s = NewSource(float4x4.identity, new double3(0.0, 0.0, 0.0));
            var plane = new float4(0f, 1f, 0f, 0f);

            // The one input, settled now and used for both builds. Its 7.6 classification is the caller's and is
            // worked out from the vertices here, once, so that moving them below cannot change what is being built --
            // only where the box would come from.
            ProvisionalOwnerBuildInput input = NewInput(s, plane, PhysicsOwnerPlacement.Identity, default, Anchors());
            ProvisionalOwnerCandidate before = Build(in input);

            // Not something the product ever does: the bank is written here to prove where the box did not come from.
            for (int c = 0; c < s.shape.ConvexCount; c++)
            {
                ConvexBrepRange range = s.shape.Convex(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    s.shape.BankOf(c).vertices[range.vertexBase + v] += new float3(1000f, 1000f, 1000f);
                }
            }

            ProvisionalOwnerCandidate after = Build(in input);

            Assert.That(
                after.Positive.Mass, Is.EqualTo(before.Positive.Mass).Within(1e-12),
                "the masses are the ones the collider boxes give, not the ones the vertices would");
            Assert.That(after.Negative.Mass, Is.EqualTo(before.Negative.Mass).Within(1e-12));
            Assert.That(
                after.Positive.CenterOfMass, Is.EqualTo(before.Positive.CenterOfMass),
                "and so is the centre of mass");
            Assert.That(after.Positive.InertiaTensor, Is.EqualTo(before.Positive.InertiaTensor), "and the inertia");
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
