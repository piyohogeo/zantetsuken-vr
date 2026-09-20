using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The two Final Physics Owners built from a finished cut, unpublished (DESIGN 7.1.2, 7.2): one Rigidbody per
    /// side with that side's convexes as its collider group, the produced shapes and the inherited ones together, the
    /// frames made to correspond, the kernel's mass properties on the body, the ledger's anchors deciding which side
    /// is fixed, and the first-split velocity about the render anchor.
    /// <para>
    /// The expected numbers are box formulas worked out here, not values read back from the builder. Nothing in these
    /// tests publishes anything: the owners never enter the physics scene, and every one of them is destroyed again.
    /// </para>
    /// </summary>
    public unsafe class PhysicsOwnerBuildTests
    {
        private const int DeadlineMilliseconds = 30000;

        /// <summary>Half the side of <see cref="CaseGenerator.Box"/>: its corners are at exactly this.</summary>
        private const double BoxHalf = 0.70710678118654752440;

        // ----- running a real cut and cook, because its products are what is built from -------------------------------

        private sealed class Fixture : IDisposable
        {
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public PhysicsCutCook cook;
            private int _frame;

            public void Pump()
            {
                dispatcher.BeginFrame(++_frame);
                dispatcher.Dispatch();
                cook.Pump();
            }

            public void RunUntil(Func<bool> condition, string what)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Pump();
                    if (condition())
                    {
                        return;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                Assert.Fail(what + ": it had not happened when the deadline passed");
            }

            public void Dispose()
            {
                cook?.Dispose();
                var clock = Stopwatch.StartNew();
                while (cook != null && !cook.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    Pump();
                }

                dispatcher?.Shutdown(DeadlineMilliseconds);
                cook?.Pump();
                geometry?.Dispose();
                background?.Dispose();
            }
        }

        private static Fixture NewFixture()
        {
            var f = new Fixture
            {
                job = new UnityJobWorkExecutor(4),
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(2),
            };
            f.dispatcher = new SharedWorkDispatcher(8, 2, 32, f.job, f.geometry, f.background);
            f.cook = new PhysicsCutCook(f.dispatcher, 1);
            return f;
        }

        private static PhysicsCutProducts Cut(Fixture f, OwnerCutHarness h, float4x4 localToOwner)
        {
            PhysicsCutRequest request = f.cook.Submit(in h.input, localToOwner);
            f.RunUntil(() => request.IsOver, "the cut and cook end");
            Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the cut produced something to build from");
            Assert.That(request.Products, Is.Not.Null);
            return request.Products;
        }

        // ----- the inputs ---------------------------------------------------------------------------------------------

        /// <summary>
        /// One box the plane crosses at its middle, stretched along z so that the three principal moments of each half
        /// are different from one another. Cut at y = 0, each half is a box of √2 by 1 by 2√2.
        /// </summary>
        private static OwnerCutHarness OneStretchedBox(double parentMass = 10.0)
        {
            var h = new OwnerCutHarness();
            h.planeN = new float3(0f, 1f, 0f);
            h.planeW = 0f;
            h.eps = 1e-5f;
            h.parentMass = parentMass;
            h.Add(Scaled(CaseGenerator.Box(), new double3(1.0, 1.0, 2.0)));
            h.Build();
            return h;
        }

        /// <summary>Two boxes the plane crosses and two it misses, one on each side.</summary>
        private static OwnerCutHarness MixedCompound()
        {
            var h = new OwnerCutHarness();
            h.planeN = new float3(0f, 1f, 0f);
            h.planeW = 0f;
            h.eps = 1e-5f;
            h.parentMass = 12.0;
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 0.0, 0.0)));      // crosses y = 0
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, 0.0, 0.0)));      // crosses y = 0
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 3.0, 0.0)));      // wholly above
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, -3.0, 0.0)));     // wholly below
            h.Build();
            return h;
        }

        private static ConvexPoly Translated(ConvexPoly poly, double3 by)
        {
            var moved = new double3[poly.V.Length];
            for (int i = 0; i < poly.V.Length; i++)
            {
                moved[i] = poly.V[i] + by;
            }

            return new ConvexPoly { V = moved, F = poly.F };
        }

        private static ConvexPoly Scaled(ConvexPoly poly, double3 by)
        {
            var scaled = new double3[poly.V.Length];
            for (int i = 0; i < poly.V.Length; i++)
            {
                scaled[i] = poly.V[i] * by;
            }

            return new ConvexPoly { V = scaled, F = poly.F };
        }

        /// <summary>
        /// Stands in for the source owner's existing cooked collider shapes, one per input convex: a real cube mesh,
        /// baked once with the same profile. The build borrows these — what the tests watch is that they are used as
        /// they are, and that nothing here destroys or re-bakes them.
        /// </summary>
        private static List<Mesh> InheritedMeshes(int count)
        {
            var meshes = new List<Mesh>(count);
            for (int i = 0; i < count; i++)
            {
                var mesh = new Mesh { name = "Inherited " + i, hideFlags = HideFlags.HideAndDontSave };
                mesh.vertices = new[]
                {
                    new Vector3(-1f, -1f, -1f), new Vector3(1f, -1f, -1f), new Vector3(1f, 1f, -1f), new Vector3(-1f, 1f, -1f),
                    new Vector3(-1f, -1f, 1f), new Vector3(1f, -1f, 1f), new Vector3(1f, 1f, 1f), new Vector3(-1f, 1f, 1f),
                };
                mesh.triangles = new[]
                {
                    0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                    2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
                };
                UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                meshes.Add(mesh);
            }

            return meshes;
        }

        private static void DestroyMeshes(List<Mesh> meshes)
        {
            foreach (Mesh mesh in meshes)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }
        }

        /// <summary>
        /// The ledger's own distribution of a set of anchors across the cut plane. It is the product code that
        /// classifies them; the build is given the result and does not classify anything again.
        /// </summary>
        private static AnchorDistributionResult Distribute(OwnerCutHarness h, params float3[] anchors)
        {
            var positive = new List<float3>();
            var negative = new List<float3>();
            FixedSupportAnchors.TryDistribute(
                anchors, new float4(h.planeN, h.planeW), h.eps, positive, negative, out AnchorDistributionResult result);
            Assert.That(result.status, Is.EqualTo(AnchorDistributionStatus.Ok), "the distribution the ledger would have made");
            return result;
        }

        private static PhysicsOwnerBuildInput Input(
            PhysicsCutProducts products,
            OwnerCutHarness h,
            List<Mesh> inherited,
            AnchorDistributionResult anchors,
            PhysicsOwnerPlacement placement = default,
            PhysicsOwnerMotion motion = default)
        {
            return new PhysicsOwnerBuildInput
            {
                products = products,
                placement = placement.IsFinite ? placement : PhysicsOwnerPlacement.Identity,
                sourceMotion = motion,
                anchors = anchors,
                parentMass = h.parentMass,
                inheritedMeshes = inherited,
                name = "Owner",
            };
        }

        private static int SceneObjectCount()
        {
            return SceneManager.GetActiveScene().rootCount;
        }

        // ----- one owner per side, out of what the cut made and what it inherited ------------------------------------

        /// <summary>
        /// A compound whose convexes are partly split and partly inherited becomes exactly two owners: every convex of
        /// a side is a collider under that side's one Rigidbody, a split one using the mesh this cut produced and an
        /// inherited one the existing cooked mesh of its input convex. Neither owner is in the scene.
        /// </summary>
        [Test]
        public void MixedConvexes_BecomeOneOwnerPerSide_WithProducedAndBorrowedShapes()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    Assert.That(products.PartCount(true), Is.EqualTo(3), "two produced and one inherited above the plane");
                    Assert.That(products.PartCount(false), Is.EqualTo(3), "and the same below it");

                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate,
                            out PhysicsOwnerBuildOutcome outcome),
                        Is.True,
                        "both sides were built");
                    Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.Ok));
                    using (candidate)
                    {
                        foreach (bool positive in new[] { true, false })
                        {
                            PhysicsOwnerSide side = candidate.Side(positive);
                            string what = positive ? "the positive side" : "the negative side";
                            Assert.That(side, Is.Not.Null, what);
                            Assert.That(side.Root.activeSelf, Is.False, what + ": it is not active");
                            Assert.That(side.Root.activeInHierarchy, Is.False, what + ": and nothing of it is");
                            Assert.That(
                                side.Root.GetComponentsInChildren<Rigidbody>(true).Length, Is.EqualTo(1),
                                what + ": one Rigidbody, not one per convex or per island");
                            Assert.That(side.Colliders.Count, Is.EqualTo(products.PartCount(positive)), what + ": every convex");
                            Assert.That(side.ProducedColliderCount, Is.EqualTo(2), what + ": two of them were produced here");

                            for (int i = 0; i < side.Colliders.Count; i++)
                            {
                                MeshCollider collider = side.Colliders[i];
                                PhysicsCutPart part = products.Part(positive, i);
                                Assert.That(
                                    collider.GetComponentInParent<Rigidbody>(true), Is.SameAs(side.Body),
                                    what + ": the collider belongs to that one body");
                                Assert.That(collider.convex, Is.True, what + ": the shape is convex");
                                Assert.That(
                                    collider.cookingOptions, Is.EqualTo(products.Cooking),
                                    what + ": with the profile its mesh was cooked with");
                                Assert.That(
                                    collider.sharedMesh,
                                    Is.SameAs(part.borrowed ? inherited[part.inputConvex] : part.mesh),
                                    what + ": a produced part uses its own mesh and an inherited one the input's");
                            }
                        }

                        Assert.That(
                            candidate.Positive.Root, Is.Not.SameAs(candidate.Negative.Root), "the two sides are two owners");
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        // ----- the numbers, against the box formulas -------------------------------------------------------------------

        /// <summary>
        /// One box cut in half: each side's mass, centre of mass and principal moments are the box's own, worked out
        /// here from its dimensions, and the two masses add up to the parent's snapshot.
        /// </summary>
        [Test]
        public void HalfOfABox_HasTheMassCentreAndInertiaOfThatHalf()
        {
            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate, out _),
                        Is.True);
                    using (candidate)
                    {
                        // Each half is a box of x = 2 * BoxHalf, y = 1, z = 4 * BoxHalf, and the halves have equal
                        // volume, so each carries half the parent's mass.
                        double x = 2.0 * BoxHalf, y = 1.0, z = 4.0 * BoxHalf;
                        double m = h.parentMass / 2.0;
                        var expected = new double3(
                            m * ((y * y) + (z * z)) / 12.0,
                            m * ((x * x) + (z * z)) / 12.0,
                            m * ((x * x) + (y * y)) / 12.0);

                        Assert.That(
                            candidate.Positive.Mass + candidate.Negative.Mass, Is.EqualTo(h.parentMass).Within(1e-9),
                            "the two sides carry the parent's mass");
                        foreach (bool positive in new[] { true, false })
                        {
                            PhysicsOwnerSide side = candidate.Side(positive);
                            string what = positive ? "above the plane" : "below it";
                            Assert.That(side.Mass, Is.EqualTo(m).Within(1e-9), what + ": half the mass");
                            Assert.That(side.Body.mass, Is.EqualTo((float)m).Within(1e-5f), what + ": and the body has it");

                            float3 centre = side.CenterOfMass;
                            Assert.That(centre.x, Is.EqualTo(0f).Within(1e-5f), what + ": centred in x");
                            Assert.That(centre.z, Is.EqualTo(0f).Within(1e-5f), what + ": centred in z");
                            Assert.That(centre.y, Is.EqualTo(positive ? 0.5f : -0.5f).Within(1e-5f), what + ": half way up its own half");
                            // The half is axis aligned, so the principal axes are the frame's own and the moments come
                            // back in that order.
                            Assert.That(side.InertiaTensor.x, Is.EqualTo((float)expected.x).Within(1e-4f), what + ": Ixx");
                            Assert.That(side.InertiaTensor.y, Is.EqualTo((float)expected.y).Within(1e-4f), what + ": Iyy");
                            Assert.That(side.InertiaTensor.z, Is.EqualTo((float)expected.z).Within(1e-4f), what + ": Izz");
                            Assert.That(
                                math.abs(math.dot(side.InertiaRotation.value, quaternion.identity.value)),
                                Is.EqualTo(1f).Within(1e-4f),
                                what + ": about the frame's own axes");
                        }
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>
        /// The same cut with the numerical frame turned and moved inside a turned and moved owner: the collider
        /// vertices land where the two transforms put them, the centre of mass is the same point of the same body, and
        /// the inertia the solver is given is the kernel's inertia turned by the same rotation. An identity placement
        /// would not show any of this.
        /// </summary>
        [Test]
        public void ARotatedFrame_KeepsShapeCentreAndInertiaTogether()
        {
            var localRotation = quaternion.EulerXYZ(0.3f, -0.7f, 1.1f);
            var localOffset = new float3(3f, -2f, 0.5f);
            float4x4 localToOwner = float4x4.TRS(localOffset, localRotation, new float3(1f));
            var placement = new PhysicsOwnerPlacement(new float3(-8f, 4f, 11f), quaternion.EulerXYZ(-1.2f, 0.4f, 0.9f));

            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, localToOwner))
                {
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h), placement), out PhysicsOwnerCandidate candidate,
                            out PhysicsOwnerBuildOutcome outcome),
                        Is.True,
                        "a rigid frame is accepted");
                    Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.Ok));
                    using (candidate)
                    {
                        float4x4 ownerToWorld = float4x4.TRS(placement.position, placement.rotation, new float3(1f));
                        float4x4 localToWorld = math.mul(ownerToWorld, localToOwner);

                        PhysicsOwnerSide side = candidate.Positive;
                        Assert.That(
                            (float3)side.Root.transform.position, Is.EqualTo(placement.position).Using(Float3Within(1e-4f)),
                            "the owner is where it was placed");

                        // Where a vertex of a produced collider really is, against the two transforms applied by hand.
                        PhysicsCutPart part = products.Part(true, 0);
                        Assert.That(part.borrowed, Is.False, "the first part of this cut is a produced one");
                        Vector3[] vertices = part.mesh.vertices;
                        Assert.That(vertices.Length, Is.GreaterThan(0), "it has vertices to check");
                        for (int i = 0; i < vertices.Length; i++)
                        {
                            float3 byHand = math.transform(localToWorld, vertices[i]);
                            float3 byUnity = side.ShapeFrame.transform.TransformPoint(vertices[i]);
                            Assert.That(byUnity, Is.EqualTo(byHand).Using(Float3Within(1e-3f)), "collider vertex " + i);
                        }

                        // The centre of mass, as the same point of the numerical frame.
                        float3 centreByHand = math.transform(localToWorld, (float3)products.Result.positiveCenterOfMass);
                        Assert.That(
                            placement.ToWorld(side.CenterOfMass), Is.EqualTo(centreByHand).Using(Float3Within(1e-3f)),
                            "the centre of mass is that point of the shape");

                        // The inertia, rebuilt out of what the solver was given, against the kernel's own matrix turned
                        // into the owner frame.
                        double3x3 kernelInertia = products.Result.positiveInertia.ToMatrix();
                        float3x3 r = new float3x3(localRotation);
                        float3x3 expected = math.mul(math.mul(r, (float3x3)kernelInertia), math.transpose(r));
                        float3x3 q = new float3x3(side.InertiaRotation);
                        float3x3 rebuilt = math.mul(math.mul(q, Diagonal(side.InertiaTensor)), math.transpose(q));
                        for (int c = 0; c < 3; c++)
                        {
                            for (int rw = 0; rw < 3; rw++)
                            {
                                Assert.That(
                                    rebuilt[c][rw], Is.EqualTo(expected[c][rw]).Within(1e-3f),
                                    "inertia entry " + c + "," + rw);
                            }
                        }
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>
        /// A placement rotation of any length is the same orientation, and gives the same owner: the transform it is
        /// put at, the centre of mass in the world and the velocity that follows from it do not change when the same
        /// rotation is given three times as long. The placement makes it a rotation once, and everything reads that
        /// one value.
        /// </summary>
        [Test]
        public void APlacementRotationOfAnyLength_BuildsTheSameOwner()
        {
            var orientation = quaternion.EulerXYZ(-1.2f, 0.4f, 0.9f);
            var position = new float3(-8f, 4f, 11f);
            var unit = new PhysicsOwnerPlacement(position, orientation);
            var longer = new PhysicsOwnerPlacement(position, new quaternion(orientation.value * 3f));
            var motion = new PhysicsOwnerMotion(
                centerOfMass: new float3(-8f, 4f, 11f),
                linearVelocity: new float3(0.25f, -0.5f, 0.75f),
                angularVelocity: new float3(0.3f, -0.2f, 1.1f),
                renderAnchor: new float3(-7f, 4.5f, 11.5f));

            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h), unit, motion), out PhysicsOwnerCandidate one, out _),
                        Is.True);
                    using (one)
                    {
                        Assert.That(
                            PhysicsOwnerBuilder.TryBuild(
                                Input(products, h, inherited, Distribute(h), longer, motion), out PhysicsOwnerCandidate other,
                                out PhysicsOwnerBuildOutcome outcome),
                            Is.True,
                            "a rotation of another length is not a different kind of input");
                        Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.Ok));
                        using (other)
                        {
                            foreach (bool positive in new[] { true, false })
                            {
                                PhysicsOwnerSide a = one.Side(positive);
                                PhysicsOwnerSide b = other.Side(positive);
                                string what = positive ? "the positive side" : "the negative side";
                                Assert.That(
                                    math.abs(math.dot(((quaternion)a.Root.transform.rotation).value, ((quaternion)b.Root.transform.rotation).value)),
                                    Is.EqualTo(1f).Within(1e-5f),
                                    what + ": put at the same orientation");
                                Assert.That(
                                    (float3)b.Root.transform.position, Is.EqualTo((float3)a.Root.transform.position).Using(Float3Within(1e-4f)),
                                    what + ": and the same place");
                                Assert.That(
                                    b.CenterOfMass, Is.EqualTo(a.CenterOfMass).Using(Float3Within(1e-6f)),
                                    what + ": the same centre of mass");
                                Assert.That(
                                    b.LinearVelocity, Is.EqualTo(a.LinearVelocity).Using(Float3Within(1e-4f)),
                                    what + ": and the same first velocity");
                            }

                            // And that velocity is the one the normalized rotation gives, not one scaled by the
                            // quaternion's length.
                            PhysicsOwnerSide side = one.Positive;
                            float3 centreWorld = position + math.mul(math.normalize(orientation), side.CenterOfMass);
                            float3 atAnchor = motion.linearVelocity
                                              + math.cross(motion.angularVelocity, motion.renderAnchor - motion.centerOfMass);
                            float3 expected = atAnchor + math.cross(motion.angularVelocity, centreWorld - motion.renderAnchor);
                            Assert.That(
                                side.LinearVelocity, Is.EqualTo(expected).Using(Float3Within(1e-4f)),
                                "the centre of mass was carried into the world by a rotation");
                        }
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>A frame that scales is refused outright: it would leave the colliders and the mass properties
        /// describing different shapes.</summary>
        [Test]
        public void AFrameThatIsNotRigid_IsRefusedAndNothingIsBuilt()
        {
            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.Scale(2f)))
                {
                    int before = SceneObjectCount();
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate,
                            out PhysicsOwnerBuildOutcome outcome),
                        Is.False);
                    Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.FrameNotRigid));
                    Assert.That(candidate, Is.Null, "one side is never handed over either");
                    Assert.That(SceneObjectCount(), Is.EqualTo(before), "and nothing was made");
                }

                DestroyMeshes(inherited);
            }
        }

        // ----- the anchors and the first velocity ----------------------------------------------------------------------

        /// <summary>
        /// The side that received an anchor is fixed and still; the other keeps the source's angular velocity and the
        /// linear velocity the render anchor gives it. The distribution is the ledger's and is not made again here.
        /// </summary>
        [Test]
        public void TheAnchoredSideIsFixed_AndTheFreeSideInheritsTheFirstSplitVelocity()
        {
            var motion = new PhysicsOwnerMotion(
                centerOfMass: new float3(0f, 0f, 0f),
                linearVelocity: new float3(0f, 0f, 0f),
                angularVelocity: new float3(0f, 0f, 1f),
                renderAnchor: new float3(1f, 0f, 0f));

            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    // One anchor above the plane: by DESIGN 7.1 that fixes the positive side and leaves the other free.
                    AnchorDistributionResult anchors = Distribute(h, new float3(0f, 0.5f, 0f));
                    Assert.That(anchors.IsPositiveFixed, Is.True, "the ledger put it on the positive side");
                    Assert.That(anchors.IsNegativeFixed, Is.False);

                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, anchors, PhysicsOwnerPlacement.Identity, motion),
                            out PhysicsOwnerCandidate candidate, out _),
                        Is.True);
                    using (candidate)
                    {
                        PhysicsOwnerSide fixedSide = candidate.Positive;
                        Assert.That(fixedSide.FixedByAnchors, Is.True, "the anchored side is fixed");
                        Assert.That(fixedSide.Body.isKinematic, Is.True, "and the body with it");
                        Assert.That(fixedSide.LinearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(0f)), "it takes no motion");
                        Assert.That(fixedSide.AngularVelocity, Is.EqualTo(float3.zero).Using(Float3Within(0f)));

                        PhysicsOwnerSide freeSide = candidate.Negative;
                        Assert.That(freeSide.FixedByAnchors, Is.False, "the other side is dynamic");
                        Assert.That(freeSide.Body.isKinematic, Is.False);

                        // v_anchor = v_source + omega x (anchor - COM_source) = (0,0,1) x (1,0,0) = (0,1,0).
                        // The lower half has its centre of mass at (0, -0.5, 0), so
                        // v_child = (0,1,0) + (0,0,1) x ((0,-0.5,0) - (1,0,0)) = (0,1,0) + (0.5,-1,0) = (0.5,0,0).
                        Assert.That(
                            freeSide.LinearVelocity, Is.EqualTo(new float3(0.5f, 0f, 0f)).Using(Float3Within(1e-4f)),
                            "the velocity the render anchor gives its centre of mass");
                        Assert.That(
                            freeSide.AngularVelocity, Is.EqualTo(motion.angularVelocity).Using(Float3Within(0f)),
                            "the angular velocity is carried over as it is");
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>
        /// What DESIGN 7.2 asks of the mass properties is that the solver can be given them. A body that is not in
        /// the scene has nowhere to keep a centre of mass or an inertia, so this is the one place that puts an owner
        /// into the scene — deliberately, to write the values and read them back — and takes it out again. It is the
        /// publication step that will do this for real; the build only records them and writes them where it can.
        /// </summary>
        [Test]
        public void TheMassProperties_AreOnesTheSolverTakes()
        {
            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate, out _),
                        Is.True);
                    using (candidate)
                    {
                        // While the owner is inactive the body has nowhere to keep these: it answers with the
                        // automatic centre of mass and inertia, whatever it was given. That is not asserted here,
                        // being the engine's behaviour rather than this build's, but it is why the values are
                        // recorded on the side and written again at publication.
                        PhysicsOwnerSide side = candidate.Positive;
                        side.Root.SetActive(true);
                        try
                        {
                            side.ApplyToBody();
                            Assert.That(
                                side.Body.mass, Is.EqualTo((float)side.Mass).Within(1e-5f), "the mass the solver took");
                            Assert.That(
                                (float3)side.Body.centerOfMass, Is.EqualTo(side.CenterOfMass).Using(Float3Within(1e-4f)),
                                "the centre of mass the solver took");
                            Assert.That(
                                (float3)side.Body.inertiaTensor, Is.EqualTo(side.InertiaTensor).Using(Float3Within(1e-3f)),
                                "the principal moments the solver took");
                            Assert.That(
                                math.abs(math.dot(((quaternion)side.Body.inertiaTensorRotation).value, side.InertiaRotation.value)),
                                Is.EqualTo(1f).Within(1e-4f),
                                "about the axes it was given");
                        }
                        finally
                        {
                            side.Root.SetActive(false);
                        }
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        // ----- what a refusal and a discard leave behind ----------------------------------------------------------------

        /// <summary>
        /// An inherited convex whose existing shape was not given is a refusal, and a refusal builds nothing: the
        /// products, their meshes and the borrowed ones are exactly as they were.
        /// </summary>
        [Test]
        public void AMissingInheritedShape_IsRefusedWithTheSourceUntouched()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    // The convex the plane missed above the plane has no shape to inherit.
                    int missing = -1;
                    for (int i = 0; i < products.PartCount(true); i++)
                    {
                        if (products.Part(true, i).borrowed)
                        {
                            missing = products.Part(true, i).inputConvex;
                        }
                    }

                    Assert.That(missing, Is.GreaterThanOrEqualTo(0), "there is an inherited convex in this cut");
                    Mesh withheld = inherited[missing];
                    inherited[missing] = null;

                    int before = SceneObjectCount();
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(
                            Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate,
                            out PhysicsOwnerBuildOutcome outcome),
                        Is.False);
                    Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.ShapeMissing));
                    Assert.That(candidate, Is.Null, "nothing is handed over");
                    Assert.That(SceneObjectCount(), Is.EqualTo(before), "and nothing was made to have to take back");

                    inherited[missing] = withheld;
                    for (int i = 0; i < inherited.Count; i++)
                    {
                        Assert.That(inherited[i], Is.Not.Null, "the borrowed shapes are all still there");
                        Assert.That(inherited[i].vertexCount, Is.EqualTo(8), "and unchanged");
                    }

                    for (int i = 0; i < products.PartCount(true); i++)
                    {
                        PhysicsCutPart part = products.Part(true, i);
                        Assert.That(part.borrowed || part.mesh != null, Is.True, "the products still have their meshes");
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>
        /// A mass snapshot the two sides do not add up to is refused. The kernel's own numbers are not scaled to fit
        /// it and nothing is moved from one side to the other.
        /// </summary>
        [Test]
        public void MassesThatDoNotAddUpToTheParentSnapshot_AreRefused()
        {
            using (OwnerCutHarness h = OneStretchedBox())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    PhysicsOwnerBuildInput input = Input(products, h, inherited, Distribute(h));
                    input.parentMass = h.parentMass + 1.0;

                    int before = SceneObjectCount();
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(in input, out PhysicsOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome),
                        Is.False);
                    Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.MassNotUsable));
                    Assert.That(candidate, Is.Null);
                    Assert.That(SceneObjectCount(), Is.EqualTo(before), "nothing was built");
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>
        /// Giving up a candidate destroys the two owners and nothing else: the meshes this cut produced are still the
        /// products', to be given back when the products are, and the borrowed ones are still the caller's.
        /// </summary>
        [Test]
        public void DiscardingACandidate_TakesBackItsOwnersAndLeavesEveryMesh()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                PhysicsCutProducts products = Cut(f, h, float4x4.identity);
                var producedMeshes = new List<Mesh>();
                foreach (bool positive in new[] { true, false })
                {
                    for (int i = 0; i < products.PartCount(positive); i++)
                    {
                        PhysicsCutPart part = products.Part(positive, i);
                        if (!part.borrowed)
                        {
                            producedMeshes.Add(part.mesh);
                        }
                    }
                }

                int before = SceneObjectCount();
                Assert.That(
                    PhysicsOwnerBuilder.TryBuild(
                        Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate, out _),
                    Is.True);
                GameObject positiveRoot = candidate.Positive.Root;
                GameObject negativeRoot = candidate.Negative.Root;

                candidate.Dispose();
                Assert.That(candidate.IsDisposed, Is.True);
                Assert.That(positiveRoot == null, Is.True, "the positive owner is gone");
                Assert.That(negativeRoot == null, Is.True, "and the negative one");
                Assert.That(SceneObjectCount(), Is.EqualTo(before), "the scene is as it was");
                candidate.Dispose();

                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.False, "a produced mesh belongs to the products, not to the owners");
                }

                foreach (Mesh mesh in inherited)
                {
                    Assert.That(mesh == null, Is.False, "and a borrowed one to the caller");
                }

                // The order at the end: the owners first, the products after them.
                products.Dispose();
                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.True, "the products give their meshes back");
                }

                foreach (Mesh mesh in inherited)
                {
                    Assert.That(mesh == null, Is.False, "the borrowed ones are still the caller's");
                }

                DestroyMeshes(inherited);
            }
        }

        /// <summary>
        /// A build that fails once the first side is already made hands over nothing and leaves nothing behind: the
        /// side it had built is destroyed, and the meshes it was using — produced and borrowed alike — are untouched.
        /// One side alone is never a result.
        /// </summary>
        [Test]
        public void AFailurePartWayThrough_TakesBackWhatItMadeAndKeepsTheShapes()
        {
            using (OwnerCutHarness h = MixedCompound())
            using (Fixture f = NewFixture())
            {
                List<Mesh> inherited = InheritedMeshes(h.input.convexCount);
                using (PhysicsCutProducts products = Cut(f, h, float4x4.identity))
                {
                    int before = SceneObjectCount();
                    var built = new List<bool>();
                    PhysicsOwnerBuilder.sideBuiltHook = positive =>
                    {
                        built.Add(positive);
                        if (!positive)
                        {
                            throw new InvalidOperationException("the second side fails");
                        }
                    };

                    try
                    {
                        Assert.That(
                            PhysicsOwnerBuilder.TryBuild(
                                Input(products, h, inherited, Distribute(h)), out PhysicsOwnerCandidate candidate,
                                out PhysicsOwnerBuildOutcome outcome),
                            Is.False);
                        Assert.That(outcome, Is.EqualTo(PhysicsOwnerBuildOutcome.BuildFailed));
                        Assert.That(candidate, Is.Null, "the side that was built is not handed over on its own");
                    }
                    finally
                    {
                        PhysicsOwnerBuilder.sideBuiltHook = null;
                    }

                    Assert.That(built, Is.EqualTo(new List<bool> { true, false }), "it had got as far as the second side");
                    Assert.That(SceneObjectCount(), Is.EqualTo(before), "and both owners were taken back");

                    for (int i = 0; i < products.PartCount(true); i++)
                    {
                        PhysicsCutPart part = products.Part(true, i);
                        Assert.That(part.borrowed || part.mesh != null, Is.True, "the produced meshes are still the products'");
                    }

                    foreach (Mesh mesh in inherited)
                    {
                        Assert.That(mesh == null, Is.False, "and the borrowed ones are still the caller's");
                    }
                }

                DestroyMeshes(inherited);
            }
        }

        // ----- helpers ---------------------------------------------------------------------------------------------------

        private static float3x3 Diagonal(float3 d)
        {
            return new float3x3(d.x, 0f, 0f, 0f, d.y, 0f, 0f, 0f, d.z);
        }

        private static IEqualityComparer<float3> Float3Within(float tolerance)
        {
            return new Float3Comparer(tolerance);
        }

        private sealed class Float3Comparer : IEqualityComparer<float3>
        {
            private readonly float _tolerance;

            internal Float3Comparer(float tolerance)
            {
                _tolerance = tolerance;
            }

            public bool Equals(float3 a, float3 b)
            {
                return math.all(math.abs(a - b) <= _tolerance);
            }

            public int GetHashCode(float3 value)
            {
                return value.GetHashCode();
            }
        }
    }
}
