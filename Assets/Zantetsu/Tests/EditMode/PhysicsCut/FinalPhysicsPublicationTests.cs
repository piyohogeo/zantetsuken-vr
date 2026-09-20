using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The final physics and the logical publication of one direct final split, in one main-thread update
    /// (DESIGN 7.1.2): the source's owner ends, the two owners enter the scene with the values they were given, the
    /// ledger publishes its two children, and the correspondence becomes the children's.
    /// <para>
    /// Everything here is synthetic. The display is not part of it: until a geometry commit, what would be drawn is
    /// still the source's registration, so these tests say nothing about the whole of DESIGN 7.1.2.
    /// </para>
    /// </summary>
    public unsafe class FinalPhysicsPublicationTests
    {
        private const int DeadlineMilliseconds = 30000;

        // ----- the world one cut happens in ---------------------------------------------------------------------------

        private sealed class World : IDisposable
        {
            public UnityJobWorkExecutor job;
            public WorkerPoolExecutor geometry;
            public WorkerPoolExecutor background;
            public SharedWorkDispatcher dispatcher;
            public PhysicsCutCook cook;
            public LogicalCutLedger ledger;
            public PhysicsOwnerRegistry registry;
            public OwnerCutHarness harness;
            public List<Mesh> authoredMeshes;
            public PhysicsShapeSource authoredSource;
            public LogicalFragmentId source;
            public readonly List<IDisposable> extras = new List<IDisposable>();
            public readonly List<GameObject> objects = new List<GameObject>();
            private int _frame;

            public PhysicsFragmentOwner SourceOwner => registry.TryGet(source, out PhysicsFragmentOwner o) ? o : null;

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
                registry?.Dispose();
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
                for (int i = extras.Count - 1; i >= 0; i--)
                {
                    extras[i].Dispose();
                }

                foreach (GameObject go in objects)
                {
                    if (go != null)
                    {
                        UnityEngine.Object.DestroyImmediate(go);
                    }
                }

                if (authoredMeshes != null)
                {
                    foreach (Mesh mesh in authoredMeshes)
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
        /// Two boxes the plane crosses and two it misses, one on each side: every side ends up with produced convexes
        /// and an inherited one.
        /// </summary>
        private static OwnerCutHarness MixedCompound()
        {
            var h = new OwnerCutHarness();
            h.planeN = new float3(0f, 1f, 0f);
            h.planeW = 0f;
            h.eps = 1e-5f;
            h.parentMass = 12.0;
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 0.0, 0.0)));
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, 0.0, 0.0)));
            h.Add(Translated(CaseGenerator.Box(), new double3(0.0, 3.0, 0.0)));
            h.Add(Translated(CaseGenerator.Box(), new double3(2.5, -3.0, 0.0)));
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

        private static List<Mesh> CookedCubes(int count)
        {
            var meshes = new List<Mesh>(count);
            for (int i = 0; i < count; i++)
            {
                var mesh = new Mesh { name = "Authored " + i, hideFlags = HideFlags.HideAndDontSave };
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

        /// <summary>
        /// One authored compound in the scene, its ledger fragment, and the correspondence between them: the state a
        /// first cut starts from.
        /// </summary>
        private static World NewWorld(float3[] anchors, PhysicsOwnerPlacement placement)
        {
            var w = new World
            {
                job = new UnityJobWorkExecutor(4),
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(2),
                registry = new PhysicsOwnerRegistry(),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4)),
                harness = MixedCompound(),
            };
            w.dispatcher = new SharedWorkDispatcher(8, 2, 32, w.job, w.geometry, w.background);
            w.cook = new PhysicsCutCook(w.dispatcher, 1);
            w.authoredMeshes = CookedCubes(w.harness.input.convexCount);
            w.authoredSource = PhysicsShapeSource.External();

            var ranges = new ConvexBrepRange[w.harness.input.convexCount];
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = w.harness.input.convexes[c];
            }

            PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(
                w.harness.input.bank, ranges, w.authoredMeshes, w.authoredSource, float4x4.identity);

            var root = new GameObject("Authored Source");
            w.objects.Add(root);
            root.transform.SetPositionAndRotation(placement.position, placement.rotation);
            var body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)w.harness.parentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < shape.ConvexCount; c++)
            {
                MeshCollider collider = root.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = shape.MeshOf(c);
            }

            w.source = w.ledger.AddFragment(anchors);
            w.registry.RegisterAuthored(w.source, root, body, shape);
            return w;
        }

        /// <summary>Admits the cut and prepares its anchor distribution, which is what publication needs beforehand.</summary>
        private static CutOperationId Admit(World w, bool prepareAnchors = true)
        {
            Assert.That(
                w.ledger.Admit(w.source, new float4(w.harness.planeN, w.harness.planeW), true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted));
            if (prepareAnchors)
            {
                Assert.That(
                    w.ledger.PrepareAnchorDistribution(operation, w.harness.eps, out AnchorDistributionResult _),
                    Is.EqualTo(AnchorPreparationOutcome.Prepared));
            }

            return operation;
        }

        /// <summary>Runs the cut and cook of the source's current shape, and builds the two unpublished owners.</summary>
        private static PhysicsOwnerCandidate Build(
            World w,
            CutOperationId operation,
            out PhysicsCutProducts products,
            in ConvexCutOwnerInput input,
            AnchorDistributionResult? distribution = null)
        {
            PhysicsCutRequest request = w.cook.Submit(in input, float4x4.identity);
            w.RunUntil(() => request.IsOver, "the cut and cook end");
            Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the cut produced something to build from");
            products = request.Products;

            PhysicsFragmentOwner owner = w.SourceOwner;
            AnchorDistributionResult anchors;
            if (distribution.HasValue)
            {
                anchors = distribution.Value;
            }
            else
            {
                Assert.That(
                    w.ledger.TryGetSettledAnchorDistribution(operation, out anchors), Is.True,
                    "the distribution the publication will use");
            }
            var buildInput = new PhysicsOwnerBuildInput
            {
                products = products,
                placement = owner.ReadPlacement(),
                sourceMotion = owner.ReadMotion(float3.zero),
                anchors = anchors,
                parentMass = owner.Mass,
                inheritedMeshes = owner.Shape.Meshes,
                name = "Child",
            };
            Assert.That(
                PhysicsOwnerBuilder.TryBuild(in buildInput, out PhysicsOwnerCandidate candidate, out PhysicsOwnerBuildOutcome outcome),
                Is.True,
                "the two owners were built: " + outcome);
            return candidate;
        }

        private static PhysicsPublicationOutcome Publish(
            World w,
            CutOperationId operation,
            PhysicsOwnerCandidate candidate,
            PhysicsCutProducts products,
            out LogicalFragmentId positive,
            out LogicalFragmentId negative,
            out LogicalCutResultOutcome ledgerOutcome,
            float3 renderAnchor = default,
            float separationImpulse = 0f,
            PhysicsOwnerShape cutFrom = null,
            LogicalFragmentId? source = null)
        {
            var input = new FinalPhysicsPublicationInput
            {
                ledger = w.ledger,
                registry = w.registry,
                operation = operation,
                source = source ?? w.source,
                candidate = candidate,
                products = products,
                cutFrom = cutFrom ?? w.SourceOwner?.Shape,
                renderAnchor = renderAnchor,
                separationImpulse = separationImpulse,
            };
            return FinalPhysicsPublication.TryPublish(in input, out positive, out negative, out ledgerOutcome);
        }

        // ----- the switch ---------------------------------------------------------------------------------------------

        /// <summary>
        /// One direct final split all the way: the ledger has two live children and a replaced source, each child is
        /// the owner that was built for it, both bodies are in the scene with the values they were given, and the
        /// source's own body is gone.
        /// </summary>
        [Test]
        public void PublishingACut_SwitchesTheOwnersAndEndsTheSource()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                GameObject sourceRoot = w.SourceOwner.Root;
                PhysicsOwnerSide builtPositive = candidate.Positive;

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome ledger),
                    Is.EqualTo(PhysicsPublicationOutcome.Published),
                    "the publication went through: " + ledger);

                Assert.That(w.ledger.IsCurrentTarget(positive), Is.True, "the positive child is live");
                Assert.That(w.ledger.IsCurrentTarget(negative), Is.True, "and the negative one");
                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.False, "the source is not a target any more");

                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False, "the source has no owner now");
                Assert.That(sourceRoot == null, Is.True, "its body left the scene");

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner owner), Is.True, "the child has one");
                Assert.That(owner.Body, Is.SameAs(builtPositive.Body), "which is the owner that was built for it");
                Assert.That(owner.Root.activeInHierarchy, Is.True, "in the scene");
                Assert.That(owner.Body.mass, Is.EqualTo((float)builtPositive.Mass).Within(1e-4f), "with its mass");
                Assert.That(
                    ((float3)owner.Body.centerOfMass).x, Is.EqualTo(builtPositive.CenterOfMass.x).Within(1e-3f),
                    "and the centre of mass a body only holds once it is in the scene");
                Assert.That(
                    ((float3)owner.Body.centerOfMass).y, Is.EqualTo(builtPositive.CenterOfMass.y).Within(1e-3f));
                Assert.That(
                    ((float3)owner.Body.inertiaTensor).x, Is.EqualTo(builtPositive.InertiaTensor.x).Within(1e-2f),
                    "and its inertia");
                Assert.That(candidate.IsDetached, Is.True, "the candidate gave the owners up");
                Assert.That(
                    w.registry.TryGet(negative, out PhysicsFragmentOwner other) && other.Root != owner.Root, Is.True,
                    "the two sides are two owners");

                candidate.Dispose();
                Assert.That(owner.Root == null, Is.False, "disposing the candidate afterwards destroys nothing");
            }
        }

        /// <summary>
        /// A source that moved and turned between the build and the publication is published where it is then, with
        /// the motion it has then. Ordinary movement is not staleness, and nothing goes back to what the build saw.
        /// </summary>
        [Test]
        public void ASourceThatMovedSinceTheBuild_IsPublishedWhereItIsNow()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);

                // It moves, turns and picks up motion after the candidate was built.
                var moved = new PhysicsOwnerPlacement(new float3(4f, -1f, 2f), quaternion.EulerXYZ(0.2f, 0.9f, -0.4f));
                PhysicsFragmentOwner sourceOwner = w.SourceOwner;
                sourceOwner.Root.transform.SetPositionAndRotation(moved.position, moved.rotation);
                sourceOwner.Body.linearVelocity = new Vector3(0.5f, 0f, -0.25f);
                sourceOwner.Body.angularVelocity = new Vector3(0f, 0f, 1f);
                float3 anchor = moved.position + new float3(1f, 0f, 0f);
                PhysicsOwnerMotion motion = sourceOwner.ReadMotion(anchor);
                float3 sourceCentre = motion.centerOfMass;

                PhysicsOwnerSide freeSide = candidate.Negative;
                float3 centreInOwner = freeSide.CenterOfMass;

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId negative, out LogicalCutResultOutcome _, anchor),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.TryGet(negative, out PhysicsFragmentOwner owner), Is.True);
                Assert.That(
                    (float3)owner.Root.transform.position, Is.EqualTo(moved.position).Using(Float3Within(1e-3f)),
                    "it stands where the source stood at publication, not where it stood at the build");
                Assert.That(
                    math.abs(math.dot(((quaternion)owner.Root.transform.rotation).value, moved.rotation.value)),
                    Is.EqualTo(1f).Within(1e-4f),
                    "turned with it");

                // v_anchor = v_source + omega x (anchor - COM_source); v_child = v_anchor + omega x (COM_child - anchor).
                float3 centreWorld = moved.ToWorld(centreInOwner);
                float3 atAnchor = motion.linearVelocity + math.cross(motion.angularVelocity, anchor - sourceCentre);
                float3 expected = atAnchor + math.cross(motion.angularVelocity, centreWorld - anchor);
                Assert.That(
                    (float3)owner.Body.linearVelocity, Is.EqualTo(expected).Using(Float3Within(1e-3f)),
                    "with the first-split velocity of the motion it had at publication");
                Assert.That(
                    (float3)owner.Body.angularVelocity, Is.EqualTo(motion.angularVelocity).Using(Float3Within(1e-3f)));
            }
        }

        /// <summary>
        /// The anchored side is fixed and takes no separation; the free side takes it once, away from the plane. The
        /// impulse here is this test's own value and says nothing about what a product should use.
        /// </summary>
        [Test]
        public void TheSeparationImpulse_MovesOnlyTheSideNoAnchorFixes()
        {
            const float impulse = 3f;
            using (World w = NewWorld(new[] { new float3(0f, 0.5f, 0f) }, PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(candidate.Positive.FixedByAnchors, Is.True, "the anchor above the plane fixes that side");
                Assert.That(candidate.Negative.FixedByAnchors, Is.False);
                var freeMass = (float)candidate.Negative.Mass;

                Assert.That(
                    Publish(
                        w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative,
                        out LogicalCutResultOutcome _, float3.zero, impulse),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner fixedOwner), Is.True);
                Assert.That(fixedOwner.Body.isKinematic, Is.True, "the anchored side is fixed");
                Assert.That(fixedOwner.FixedByAnchors, Is.True);

                Assert.That(w.registry.TryGet(negative, out PhysicsFragmentOwner freeOwner), Is.True);
                Assert.That(freeOwner.Body.isKinematic, Is.False);
                Assert.That(
                    (float3)freeOwner.Body.linearVelocity,
                    Is.EqualTo(new float3(0f, -impulse / freeMass, 0f)).Using(Float3Within(1e-3f)),
                    "the free side takes the impulse once, away from the plane");
            }
        }

        // ----- what a refusal leaves behind ---------------------------------------------------------------------------

        /// <summary>
        /// A distribution that was never prepared is one of the ledger's ordinary refusals. It is found before
        /// anything of the physics is touched: the source still owns its body, and the owners never entered the scene.
        /// </summary>
        [Test]
        public void AnUnpreparedDistribution_RefusesBeforeThePhysicsIsTouched()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                // Admitted, but the ledger's distribution is never prepared. The candidate is built from a
                // distribution worked out directly, so that the only thing missing is the ledger's own preparation.
                CutOperationId operation = Admit(w, prepareAnchors: false);
                var positiveAnchors = new List<float3>();
                var negativeAnchors = new List<float3>();
                FixedSupportAnchors.TryDistribute(
                    Array.Empty<float3>(), new float4(w.harness.planeN, w.harness.planeW), w.harness.eps,
                    positiveAnchors, negativeAnchors, out AnchorDistributionResult direct);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input, direct);
                GameObject sourceRoot = w.SourceOwner.Root;

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome ledger),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(ledger, Is.EqualTo(LogicalCutResultOutcome.AnchorsNotPrepared), "the ledger's own reason");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner still), Is.True, "the source still has its owner");
                Assert.That(still.Root, Is.SameAs(sourceRoot), "which is the same body it had");
                Assert.That(sourceRoot.activeInHierarchy, Is.True, "still in the scene");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "and the owners never entered it");
                Assert.That(candidate.Negative.Root.activeInHierarchy, Is.False);
                Assert.That(candidate.IsDetached, Is.False, "the candidate is still the caller's");
                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.True, "nothing of the logical state moved either");

                candidate.Dispose();
                products.Dispose();
            }
        }

        /// <summary>
        /// A source whose ownership changed elsewhere makes the result stale. The stale work does not retire the
        /// source or touch its physics: it gives its own candidate up and leaves everything else where it was.
        /// </summary>
        [Test]
        public void AStaleOperation_LeavesTheCurrentSourceAlone()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                GameObject sourceRoot = w.SourceOwner.Root;

                w.ledger.NoteOwnershipChanged(w.source);

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome ledger),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(ledger, Is.EqualTo(LogicalCutResultOutcome.Stale), "the ledger's own reason");

                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.True, "the source is still live");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner still), Is.True);
                Assert.That(still.Root, Is.SameAs(sourceRoot), "with the body it had");
                Assert.That(sourceRoot.activeInHierarchy, Is.True);
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "the owners are out of the scene again");

                candidate.Dispose();
                products.Dispose();
            }
        }

        /// <summary>
        /// Physics that cannot be established publishes nothing, and the product itself takes that to the ordinary
        /// abort: the operation ends and the source is retired through the ledger and the correspondence. The candidate
        /// and the products stay the caller's, and giving them back twice is not a second release.
        /// </summary>
        [Test]
        public void PhysicsThatCannotBeEstablished_IsTakenToTheAbortAndLeavesTheProducts()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                GameObject sourceRoot = w.SourceOwner.Root;

                // One side of the pair is no longer there to be established.
                UnityEngine.Object.DestroyImmediate(candidate.Negative.Root);

                var producedMeshes = new List<Mesh>();
                for (int i = 0; i < products.PartCount(true); i++)
                {
                    PhysicsCutPart part = products.Part(true, i);
                    if (!part.borrowed)
                    {
                        producedMeshes.Add(part.mesh);
                    }
                }

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome ledger),
                    Is.EqualTo(PhysicsPublicationOutcome.PhysicsNotEstablished));

                // The product takes it to the ordinary abort itself: the operation ends and the source is retired,
                // through the ledger and the correspondence.
                Assert.That(ledger, Is.EqualTo(LogicalCutResultOutcome.Applied), "the abort applied");
                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.False, "the source is retired");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False, "and its physics ended with it");
                Assert.That(sourceRoot == null, Is.True, "its body left the scene");

                // The products never became the publication's, so they are still the caller's to use and to give back.
                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.False, "a failed call leaves the products alone");
                }

                candidate.Dispose();
                products.Dispose();
                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.True, "the caller gives them back itself");
                }

                products.Dispose();
                candidate.Dispose();
            }
        }

        /// <summary>
        /// An operation is published for the fragment its own record names, and a call that says another one is
        /// refused outright. Otherwise one fragment's owner could be retired for another fragment's cut.
        /// </summary>
        [Test]
        public void ACallThatNamesAnotherFragment_IsRefused()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                GameObject sourceRoot = w.SourceOwner.Root;

                // Another live fragment of the same ledger, with an owner of its own.
                LogicalFragmentId other = w.ledger.AddFragment();

                Assert.That(
                    Publish(
                        w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _,
                        out LogicalCutResultOutcome _, float3.zero, 0f, null, other),
                    Is.EqualTo(PhysicsPublicationOutcome.InvalidInput),
                    "the fragment named is not the one this operation is of");

                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.True, "the source is untouched");
                Assert.That(w.ledger.IsCurrentTarget(other), Is.True, "and so is the other fragment");
                Assert.That(sourceRoot.activeInHierarchy, Is.True, "nothing left the scene");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "and nothing entered it");

                candidate.Dispose();
                products.Dispose();
            }
        }

        /// <summary>
        /// A result of a cut of one owner is not applied to a different owner the fragment was given in the meantime.
        /// This is the correspondence between what was cut and what the fragment is made of now; the ledger's own
        /// authority catches the same replacement as staleness when whoever replaced the owner says so, and ordinary
        /// movement changes neither.
        /// </summary>
        [Test]
        public void AnOwnerReplacedAfterAdmission_IsNotGivenTheOldResult()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                PhysicsOwnerShape cutFrom = w.SourceOwner.Shape;

                // Something else gives the fragment a different owner, and does not tell the ledger. The ledger has
                // nothing to object to, so this call's own comparison is what catches it.
                PhysicsOwnerShape replacementShape = ReplaceOwner(w, out GameObject root);

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome _, float3.zero, 0f, cutFrom),
                    Is.EqualTo(PhysicsPublicationOutcome.InvalidInput),
                    "what was cut is not what the fragment is made of now");
                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.True, "the fragment keeps the owner it has");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner now), Is.True);
                Assert.That(now.Shape, Is.SameAs(replacementShape), "which is the replacement");
                Assert.That(root.activeInHierarchy, Is.True, "still in the scene");
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "and the old result never entered it");

                candidate.Dispose();
                products.Dispose();
            }
        }

        /// <summary>
        /// An owner that was replaced, by something that told the ledger so, reaches the ledger and is told its result
        /// is stale: the operation ends once with its budget returned, the owner the fragment has now is left alone,
        /// and the old candidate is not published. A comparison of this call's own must not turn such a case away
        /// before the ledger has seen it.
        /// </summary>
        [Test]
        public void AReplacedOwnerThatToldTheLedger_EndsTheOperationAsStale()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                PhysicsOwnerShape cutFrom = w.SourceOwner.Shape;
                int taken = w.ledger.Budget.IncompleteCutOperationCount;
                Assert.That(taken, Is.EqualTo(1), "the admitted operation holds one budget unit");

                PhysicsOwnerShape replacementShape = ReplaceOwner(w, out GameObject replacementRoot);
                w.ledger.NoteOwnershipChanged(w.source);

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome ledger, float3.zero, 0f, cutFrom),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(ledger, Is.EqualTo(LogicalCutResultOutcome.Stale), "the ledger's own reason");

                Assert.That(
                    w.ledger.TryGetOperation(operation, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Stale,
                    Is.True,
                    "the operation ended as stale");
                Assert.That(record.positive.IsSet, Is.False, "with no children");
                Assert.That(positive.IsSet, Is.False);
                Assert.That(negative.IsSet, Is.False);
                Assert.That(
                    w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(taken - 1), "and its budget unit came back");

                Assert.That(w.ledger.IsCurrentTarget(w.source), Is.True, "the fragment is still live");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner now), Is.True);
                Assert.That(now.Shape, Is.SameAs(replacementShape), "with the owner it was given");
                Assert.That(now.IsWithdrawn, Is.False, "which was neither changed nor retired");
                Assert.That(replacementRoot.activeInHierarchy, Is.True);
                Assert.That(candidate.Positive.Root.activeInHierarchy, Is.False, "and the old candidate never entered the scene");

                // Ended once: a second attempt finds nothing active, and the budget does not move again.
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome again, float3.zero, 0f, cutFrom),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(again, Is.EqualTo(LogicalCutResultOutcome.NotActive));
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(taken - 1), "returned once, not twice");

                candidate.Dispose();
                products.Dispose();
            }
        }

        /// <summary>
        /// Gives the source fragment a different owner, made of a different shape, the way something else in the world
        /// would. The old owner is retired; the meshes it had are the authored ones and stay.
        /// </summary>
        private static PhysicsOwnerShape ReplaceOwner(World w, out GameObject root)
        {
            w.registry.Retire(w.source);
            List<Mesh> meshes = CookedCubes(1);
            w.authoredMeshes.AddRange(meshes);
            var ranges = new[] { w.harness.input.convexes[0] };
            PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(
                w.harness.input.bank, ranges, meshes, PhysicsShapeSource.External(), float4x4.identity);
            root = new GameObject("Replacement");
            w.objects.Add(root);
            var body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.mass = (float)w.harness.parentMass;
            MeshCollider collider = root.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            w.registry.RegisterAuthored(w.source, root, body, shape);
            return shape;
        }

        /// <summary>
        /// An exception after the publication is not an ordinary outcome. The two children are published and their
        /// owners registered, and the error comes out of the call as itself: it is not reported as physics that could
        /// not be established, the operation is not aborted, the source is not brought back, and nothing of the
        /// children's is given back.
        /// </summary>
        [Test]
        public void AnExceptionAfterThePublication_IsNotAnOrdinaryFailure()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                PhysicsOwnerShape cutFrom = w.SourceOwner.Shape;
                var input = new FinalPhysicsPublicationInput
                {
                    ledger = w.ledger,
                    registry = w.registry,
                    operation = operation,
                    source = w.source,
                    candidate = candidate,
                    products = products,
                    cutFrom = cutFrom,
                };

                FinalPhysicsPublication.publishedHook = () => throw new InvalidOperationException("after the publication");
                try
                {
                    Assert.Throws<InvalidOperationException>(
                        () => FinalPhysicsPublication.TryPublish(
                            in input, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome _),
                        "it comes out of the call as itself");
                }
                finally
                {
                    FinalPhysicsPublication.publishedHook = null;
                }

                Assert.That(w.ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
                Assert.That(
                    record.state, Is.EqualTo(LogicalCutOperationState.Published),
                    "the publication stands: it was not aborted and not made stale");
                Assert.That(w.ledger.IsCurrentTarget(record.positive), Is.True, "both children are live");
                Assert.That(w.ledger.IsCurrentTarget(record.negative), Is.True);
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state) && state == LogicalFragmentState.Replaced,
                    Is.True,
                    "the source was replaced, not retired");

                Assert.That(w.registry.TryGet(record.positive, out PhysicsFragmentOwner keptPositive), Is.True, "the owners are registered");
                Assert.That(w.registry.TryGet(record.negative, out PhysicsFragmentOwner keptNegative), Is.True);
                Assert.That(keptPositive.IsWithdrawn, Is.False, "and were not taken back out of the scene");
                Assert.That(keptNegative.IsWithdrawn, Is.False);
                Assert.That(keptPositive.Shape.IsFreed, Is.False, "nor was anything of theirs given back");
                Assert.That(keptPositive.Root.activeInHierarchy, Is.True);
            }
        }

        // ----- lifetimes ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Withdrawing an owner takes it out of the physics scene at once, before anything of it is destroyed: a query
        /// no longer finds it although the object is still there. Destruction is not immediate in every mode, so the
        /// two have to be separate steps.
        /// </summary>
        [Test]
        public void WithdrawingAnOwner_TakesItOutOfQueriesBeforeAnythingIsDestroyed()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                PhysicsFragmentOwner owner = w.SourceOwner;
                GameObject root = owner.Root;
                UnityEngine.Physics.SyncTransforms();
                Assert.That(Found(root), Is.True, "a query finds it while it is in the scene");

                Assert.That(w.registry.Withdraw(w.source), Is.True);
                UnityEngine.Physics.SyncTransforms();
                Assert.That(owner.IsWithdrawn, Is.True);
                Assert.That(root == null, Is.False, "nothing has been destroyed yet");
                Assert.That(owner.IsReleased, Is.False, "and nothing has been given back");
                Assert.That(Found(root), Is.False, "but a query does not find it any more");

                Assert.That(w.registry.Retire(w.source), Is.True, "and then it is destroyed");
                Assert.That(root == null, Is.True);
            }
        }

        private static bool Found(GameObject root)
        {
            Collider[] found = UnityEngine.Physics.OverlapBox(Vector3.zero, new Vector3(8f, 8f, 8f));
            foreach (Collider collider in found)
            {
                if (collider.gameObject == root
                    || (collider.attachedRigidbody != null && collider.attachedRigidbody.gameObject == root))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Work that is reading an owner's bank keeps it: retiring that owner takes it out of the scene and destroys
        /// its body, but the convexes the cut is reading stay where they are until the work has been collected and its
        /// hold let go. Then they go back, once.
        /// </summary>
        [Test]
        public void AnInFlightCutOfAnOwner_KeepsItsBankUntilTheWorkIsCollected()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                PhysicsOwnerShape shape = w.SourceOwner.Shape;
                var plane = new float4(1f, 0f, 0f, -1.25f);
                using (var next = new SecondCutInput(w.SourceOwner, plane.xyz, plane.w, w.harness.eps))
                {
                    // The hold whoever submits the work takes, for as long as the work may read the bank.
                    shape.AcquireForWork();
                    PhysicsCutRequest request = w.cook.Submit(in next.input, shape.LocalToOwner);

                    // The owner is retired while that work is out.
                    Assert.That(w.registry.Retire(w.source), Is.True);
                    Assert.That(shape.IsFreed, Is.False, "the bank the work reads is still there");
                    Assert.That(shape.WorkUsers, Is.EqualTo(1));
                    Assert.That(shape.Bank.vertices != null, Is.True, "and still readable");

                    w.RunUntil(() => request.IsOver, "the cut ends");
                    Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the work ran on the retired owner's shape");
                    Assert.That(shape.IsFreed, Is.False, "and the bank was held until it was collected");

                    shape.ReleaseFromWork();
                    Assert.That(shape.IsFreed, Is.True, "then it goes back, once");
                    request.Products?.Dispose();
                }
            }
        }

        /// <summary>
        /// A mesh a child inherited outlives the source it came from and the sibling that was retired first. The
        /// products of the cut are given back only when no owner is using them any more.
        /// </summary>
        [Test]
        public void AnInheritedShape_OutlivesTheSourceAndARetiredSibling()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
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

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive2, out LogicalFragmentId negative2, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                // The source has already been ended by the publication; its shapes were inherited, not destroyed.
                foreach (Mesh mesh in w.authoredMeshes)
                {
                    Assert.That(mesh == null, Is.False, "an inherited mesh outlives the source it came from");
                }

                Assert.That(w.registry.TryGet(positive2, out PhysicsFragmentOwner keptSide), Is.True);
                MeshCollider[] colliders = keptSide.Root.GetComponentsInChildren<MeshCollider>(true);
                Assert.That(colliders.Length, Is.GreaterThan(0));

                Assert.That(w.registry.Retire(negative2), Is.True, "one side can be retired on its own");
                foreach (Mesh mesh in w.authoredMeshes)
                {
                    Assert.That(mesh == null, Is.False, "and that does not take the other side's shapes");
                }

                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.False, "nor the ones this cut produced, which the kept side uses");
                }

                foreach (MeshCollider collider in colliders)
                {
                    Assert.That(collider.sharedMesh == null, Is.False, "the kept side's colliders still have their shapes");
                }

                Assert.That(w.registry.Retire(positive2), Is.True, "when the last side goes");
                foreach (Mesh mesh in producedMeshes)
                {
                    Assert.That(mesh == null, Is.True, "the cut's products are given back, once");
                }

                foreach (Mesh mesh in w.authoredMeshes)
                {
                    Assert.That(mesh == null, Is.False, "the authored shapes were never this cut's to give back");
                }
            }
        }

        // ----- the next cut -------------------------------------------------------------------------------------------

        /// <summary>
        /// A published child can be cut again straight away, with no geometry anywhere: the ledger admits the cut, and
        /// the child's own physics — its convexes in its own bank, with its current mass — is what that cut reads.
        /// </summary>
        [Test]
        public void APublishedChild_CanBeCutAgainFromItsOwnPhysics()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId _, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner child), Is.True);
                Assert.That(child.Shape.ConvexCount, Is.EqualTo(3), "two produced and one inherited, in one bank of its own");

                // The ledger takes the cut although no geometry exists anywhere.
                var plane = new float4(1f, 0f, 0f, -1.25f);
                Assert.That(
                    w.ledger.Admit(positive, plane, true, out CutOperationId second), Is.EqualTo(LogicalCutAdmission.Admitted),
                    "a published child is a target at once");

                // And its own physics is what the next cut reads: one bank, its convexes, its current mass.
                using (var next = new SecondCutInput(child, plane.xyz, plane.w, w.harness.eps))
                {
                    PhysicsCutRequest request = w.cook.Submit(in next.input, child.Shape.LocalToOwner);
                    w.RunUntil(() => request.IsOver, "the second cut ends");
                    Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the child's shape cut again");
                    using (PhysicsCutProducts again = request.Products)
                    {
                        Assert.That(
                            again.Result.positiveMass + again.Result.negativeMass,
                            Is.EqualTo((double)child.Mass).Within(1e-4),
                            "with the child's own mass as the parent mass of that cut");
                        Assert.That(again.PartCount(true), Is.GreaterThan(0));
                        Assert.That(again.PartCount(false), Is.GreaterThan(0));
                    }
                }

                Assert.That(w.ledger.Abort(second), Is.EqualTo(LogicalCutResultOutcome.Applied), "the second cut is not published here");
            }
        }

        /// <summary>
        /// The kernel input of a cut of one owner's current shape: its own bank and convexes, the support
        /// classification of DESIGN 7.6, and its mass as the parent mass (DESIGN 7.2).
        /// </summary>
        private sealed unsafe class SecondCutInput : IDisposable
        {
            internal ConvexCutOwnerInput input;
            private NativeArray<ConvexBrepRange> _ranges;
            private NativeArray<byte> _sides;
            private NativeArray<float> _distance;
            private NativeArray<sbyte> _class;
            private NativeArray<int> _bases;

            internal SecondCutInput(PhysicsFragmentOwner owner, float3 normal, float w, float eps)
            {
                PhysicsOwnerShape shape = owner.Shape;
                int count = shape.ConvexCount;
                int vertices = 0;
                for (int c = 0; c < count; c++)
                {
                    vertices += shape.Convex(c).vertexCount;
                }

                _ranges = new NativeArray<ConvexBrepRange>(count, Allocator.Persistent);
                _sides = new NativeArray<byte>(count, Allocator.Persistent);
                _distance = new NativeArray<float>(vertices, Allocator.Persistent);
                _class = new NativeArray<sbyte>(vertices, Allocator.Persistent);
                _bases = new NativeArray<int>(count, Allocator.Persistent);

                var ranges = (ConvexBrepRange*)_ranges.GetUnsafePtr();
                var sides = (byte*)_sides.GetUnsafePtr();
                var distance = (float*)_distance.GetUnsafePtr();
                var classes = (sbyte*)_class.GetUnsafePtr();
                var bases = (int*)_bases.GetUnsafePtr();

                int at = 0;
                for (int c = 0; c < count; c++)
                {
                    ConvexBrepRange range = shape.Convex(c);
                    ranges[c] = range;
                    bases[c] = at;
                    int positive = 0, negative = 0;
                    for (int i = 0; i < range.vertexCount; i++)
                    {
                        float3 v = shape.Bank.vertices[range.vertexBase + i];
                        float s = math.dot(normal, v) + w;
                        distance[at + i] = s;
                        classes[at + i] = (sbyte)(s > 0f ? 1 : s < 0f ? -1 : 0);
                        if (s > eps)
                        {
                            positive++;
                        }
                        else if (s < -eps)
                        {
                            negative++;
                        }
                    }

                    sides[c] = (byte)(positive > 0 && negative > 0 ? ConvexSide.Split
                        : negative > 0 ? ConvexSide.Negative
                        : positive > 0 ? ConvexSide.Positive
                        : ConvexSide.NearPlaneToPositive);
                    at += range.vertexCount;
                }

                input = new ConvexCutOwnerInput
                {
                    bank = shape.Bank,
                    convexes = ranges,
                    convexCount = count,
                    sides = sides,
                    signedDistance = distance,
                    signClass = classes,
                    distanceBases = bases,
                    plane = new float4(normal, w),
                    parentMass = owner.Mass,
                    vertexLimit = 128,
                };
            }

            public void Dispose()
            {
                Free(ref _ranges);
                Free(ref _sides);
                Free(ref _distance);
                Free(ref _class);
                Free(ref _bases);
            }

            private static void Free<T>(ref NativeArray<T> array)
                where T : struct
            {
                if (array.IsCreated)
                {
                    array.Dispose();
                }

                array = default;
            }
        }

        // ----- the physics scene itself -------------------------------------------------------------------------------

        /// <summary>
        /// After the switch, the two owners are what a query finds and the source is not, and one step of the
        /// simulation moves the free side while the anchored one stays where it is. This is the only place that steps
        /// anything.
        /// </summary>
        [Test]
        public void AfterTheSwitch_TheOwnersAreWhatThePhysicsSceneHas()
        {
            SimulationMode mode = UnityEngine.Physics.simulationMode;
            using (World w = NewWorld(new[] { new float3(0f, 0.5f, 0f) }, PhysicsOwnerPlacement.Identity))
            {
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(
                        w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative,
                        out LogicalCutResultOutcome _, float3.zero, 3f),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                w.registry.TryGet(positive, out PhysicsFragmentOwner fixedOwner);
                w.registry.TryGet(negative, out PhysicsFragmentOwner freeOwner);
                freeOwner.Body.useGravity = false;

                UnityEngine.Physics.SyncTransforms();
                Collider[] found = UnityEngine.Physics.OverlapBox(Vector3.zero, new Vector3(4f, 4f, 4f));
                var roots = new HashSet<GameObject>();
                foreach (Collider collider in found)
                {
                    roots.Add(collider.attachedRigidbody != null ? collider.attachedRigidbody.gameObject : collider.gameObject);
                }

                Assert.That(roots.Contains(fixedOwner.Root), Is.True, "a query finds the positive owner");
                Assert.That(roots.Contains(freeOwner.Root), Is.True, "and the negative one");
                Assert.That(
                    roots.Count, Is.EqualTo(2),
                    "and nothing else of this cut: the source's body is not in the scene any more");

                try
                {
                    UnityEngine.Physics.simulationMode = SimulationMode.Script;
                    Vector3 fixedBefore = fixedOwner.Root.transform.position;
                    Vector3 freeBefore = freeOwner.Root.transform.position;
                    UnityEngine.Physics.Simulate(0.02f);
                    Assert.That(
                        fixedOwner.Root.transform.position, Is.EqualTo(fixedBefore).Using(Vector3Within(1e-5f)),
                        "the anchored side does not move");
                    Assert.That(
                        (freeOwner.Root.transform.position - freeBefore).magnitude, Is.GreaterThan(1e-4f),
                        "the free side moves with the velocity it was given");
                }
                finally
                {
                    UnityEngine.Physics.simulationMode = mode;
                }
            }
        }

        // ----- helpers ---------------------------------------------------------------------------------------------------

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

        private static IEqualityComparer<Vector3> Vector3Within(float tolerance)
        {
            return new Vector3Comparer(tolerance);
        }

        private sealed class Vector3Comparer : IEqualityComparer<Vector3>
        {
            private readonly float _tolerance;

            internal Vector3Comparer(float tolerance)
            {
                _tolerance = tolerance;
            }

            public bool Equals(Vector3 a, Vector3 b)
            {
                return (a - b).sqrMagnitude <= _tolerance * _tolerance;
            }

            public int GetHashCode(Vector3 value)
            {
                return value.GetHashCode();
            }
        }
    }
}
