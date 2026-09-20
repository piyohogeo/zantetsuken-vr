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
using Zantetsu.MeshCut.Tests;
using Zantetsu.Rendering;

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

        /// <summary>
        /// One collider mesh per convex: the box that convex lies in, in the convex's own frame. A cube at the origin
        /// would do for a test that only needs a mesh to exist, but an authored mesh is taken as the collider of its
        /// own convex -- its bounds are what the provisional mass is drawn from -- so it has to be where the convex is.
        /// </summary>
        private static unsafe List<Mesh> CookedConvexBoxes(
            ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> ranges)
        {
            var meshes = new List<Mesh>(ranges.Count);
            for (int i = 0; i < ranges.Count; i++)
            {
                ConvexBrepRange range = ranges[i];
                var lo = new float3(float.PositiveInfinity);
                var hi = new float3(float.NegativeInfinity);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 at = bank.vertices[range.vertexBase + v];
                    lo = math.min(lo, at);
                    hi = math.max(hi, at);
                }

                var mesh = new Mesh { name = "Authored " + i, hideFlags = HideFlags.HideAndDontSave };
                mesh.vertices = new[]
                {
                    new Vector3(lo.x, lo.y, lo.z), new Vector3(hi.x, lo.y, lo.z),
                    new Vector3(hi.x, hi.y, lo.z), new Vector3(lo.x, hi.y, lo.z),
                    new Vector3(lo.x, lo.y, hi.z), new Vector3(hi.x, lo.y, hi.z),
                    new Vector3(hi.x, hi.y, hi.z), new Vector3(lo.x, hi.y, hi.z),
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
        private static World NewWorld(
            float3[] anchors, PhysicsOwnerPlacement placement, Matrix4x4? geometryLocalToOwner = null,
            int incompleteBudget = 4)
        {
            var w = new World
            {
                job = new UnityJobWorkExecutor(4),
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(2),
                registry = new PhysicsOwnerRegistry(),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(incompleteBudget)),
                harness = MixedCompound(),
            };
            w.dispatcher = new SharedWorkDispatcher(8, 2, 32, w.job, w.geometry, w.background);
            w.cook = new PhysicsCutCook(w.dispatcher, 1);
            w.authoredSource = PhysicsShapeSource.External();

            var ranges = new ConvexBrepRange[w.harness.input.convexCount];
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = w.harness.input.convexes[c];
            }

            w.authoredMeshes = CookedConvexBoxes(w.harness.input.bank, ranges);

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
            w.registry.RegisterAuthored(w.source, root, body, shape, false, geometryLocalToOwner);
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
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));
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

                // The display ends with it: asking where it stands is refused, and the next collection draws nothing
                // from that registration rather than putting it back where it was registered.
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(w.source, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "nothing says where a retired source stands");
                VpMultiCutSnapshot ended = NewSnapshot();
                Assert.That(
                    Build(ended, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built),
                    "a retired root is an ordinary collection, not a failure");
                Assert.That(ended.RenderFragmentCount, Is.Zero, "with nothing drawn from it");
                Assert.That(Has(ended, w.source), Is.False, "the retired source least of all");

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
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                PhysicsOwnerShape cutFrom = w.SourceOwner.Shape;
                int taken = w.ledger.Budget.IncompleteCutOperationCount;
                Assert.That(taken, Is.EqualTo(1), "the admitted operation holds one budget unit");

                // While it is still admitted, the source is drawn with that boundary as a temporary one.
                VpMultiCutSnapshot admitted = NewSnapshot();
                Assert.That(Build(admitted, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Assert.That(Of(admitted, w.source).capCount, Is.GreaterThan(0), "the admitted boundary is drawn");

                PhysicsOwnerShape replacementShape = ReplaceOwner(
                    w, out GameObject replacementRoot, k_geometryLocalToOwner);
                replacementRoot.transform.SetPositionAndRotation(
                    new Vector3(2f, -1f, 0.5f), Quaternion.AngleAxis(20f, Vector3.forward));
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

                // What the display sees: the fragment still follows, and it follows the owner it has now, which is
                // the replacement where the test put it. The boundary that ended as stale is no longer drawn.
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(w.source, out Matrix4x4 followed),
                    Is.EqualTo(VpFragmentPlacementKind.Following),
                    "the live source still follows an owner");
                Same(
                    Expected(new Vector3(2f, -1f, 0.5f), Quaternion.AngleAxis(20f, Vector3.forward)), followed,
                    "and it is the owner it has now");

                VpMultiCutSnapshot stale = NewSnapshot();
                Assert.That(Build(stale, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                VpMultiCutRenderFragment rf = Of(stale, w.source);
                Assert.That(rf.capCount, Is.Zero, "the stale boundary is not drawn any more");
                Assert.That(rf.clip.PlaneCount, Is.Zero, "and nothing is clipped by it");
                Same(
                    Expected(new Vector3(2f, -1f, 0.5f), Quaternion.AngleAxis(20f, Vector3.forward)),
                    rf.geometryLocalToWorld,
                    "the body is drawn at the owner it kept");

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
        private static PhysicsOwnerShape ReplaceOwner(
            World w, out GameObject root, Matrix4x4? geometryLocalToOwner = null)
        {
            w.registry.Retire(w.source);
            var ranges = new[] { w.harness.input.convexes[0] };
            List<Mesh> meshes = CookedConvexBoxes(w.harness.input.bank, ranges);
            w.authoredMeshes.AddRange(meshes);
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
            w.registry.RegisterAuthored(w.source, root, body, shape, false, geometryLocalToOwner);
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

        // ----- the display following the owners -----------------------------------------------------------------------

        /// <summary>Where the display geometry of this lineage sits in its owner's coordinates: never the identity.</summary>
        private static readonly Matrix4x4 k_geometryLocalToOwner = Matrix4x4.Translate(new Vector3(0f, 0.4f, 0f));

        /// <summary>The other way round, for the registration: the plane the ledger holds is in the owner's frame.</summary>
        private static readonly Matrix4x4 k_lineageToGeometryLocal = Matrix4x4.Translate(new Vector3(0f, -0.4f, 0f));

        private static readonly Bounds k_box = new Bounds(Vector3.zero, Vector3.one * 2f);
        /// <summary>A length to judge "far enough to tell two places apart" by. Not a separation: nothing separates the display.</summary>
        private const float Apart = 0.5f;

        private static VpMultiCutSnapshot NewSnapshot()
        {
            return new VpMultiCutSnapshot(new VpMultiCutCapacities(64, 1024, 64, 512, 64));
        }

        /// <summary>
        /// The placement a test works out for itself: the owner's transform as the test set it, with the
        /// correspondence the test gave. Nothing of the lookup's answer is used to build it.
        /// </summary>
        private static Matrix4x4 Expected(Vector3 position, Quaternion rotation)
        {
            return Matrix4x4.TRS(position, rotation, Vector3.one) * k_geometryLocalToOwner;
        }

        private static void Same(Matrix4x4 expected, Matrix4x4 actual, string what)
        {
            for (int i = 0; i < 16; i++)
            {
                Assert.That(actual[i], Is.EqualTo(expected[i]).Within(2e-3f), what + " [" + i + "]");
            }
        }

        private static void Same(Vector3 expected, Vector3 actual, string what)
        {
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(2e-3f), what + ".x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(2e-3f), what + ".y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(2e-3f), what + ".z");
        }

        private static void Move(PhysicsFragmentOwner owner, Vector3 position, float degreesAboutZ)
        {
            owner.Root.transform.SetPositionAndRotation(position, Quaternion.AngleAxis(degreesAboutZ, Vector3.forward));
        }

        /// <summary>The registration this lineage is shown with: where its owner stood when it was registered.</summary>
        private static VpMultiCutRegistration Registered(World w, Matrix4x4 geometryLocalToWorld)
        {
            return new VpMultiCutRegistration(
                w.source, k_box, geometryLocalToWorld, k_lineageToGeometryLocal, Array.Empty<VpClipBoundary>(), 1e-4f);
        }

        private static VpMultiCutRenderFragment Of(VpMultiCutSnapshot snapshot, LogicalFragmentId fragment)
        {
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment found), Is.True);
                if (found.root == fragment)
                {
                    return found;
                }
            }

            Assert.Fail("nothing is drawn for that fragment");
            return default;
        }

        private static bool Has(VpMultiCutSnapshot snapshot, LogicalFragmentId fragment)
        {
            for (int r = 0; r < snapshot.RenderFragmentCount; r++)
            {
                Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment found), Is.True);
                if (found.root == fragment)
                {
                    return true;
                }
            }

            return false;
        }

        private static VpMultiCutBuildOutcome Build(
            VpMultiCutSnapshot snapshot, World w, VpMultiCutRegistration registration, IVpFragmentPlacement lookup)
        {
            return snapshot.TryBuild(
                w.ledger, new[] { registration }, lookup);
        }

        /// <summary>A real display over the same ledger, with one cube in its storage to be cut and committed.</summary>
        private sealed class DisplayScene : IDisposable
        {
            internal VpCpuGeometryStorage storage;
            internal VpGeometryReferenceTable table;
            internal VpLogicalCutDisplay display;
            internal VpStoredGeometry geometry;
            internal Material material;
            internal int frame = 1;

            public void Dispose()
            {
                display?.Dispose();
                storage?.Dispose();
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            internal void Collect()
            {
                frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "the display collects");
            }

            internal Vector3 Drawn(LogicalFragmentId fragment, Vector3 local)
            {
                for (int r = 0; r < display.RenderFragmentCount; r++)
                {
                    Assert.That(display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    if (rf.root == fragment)
                    {
                        return rf.geometryLocalToWorld.MultiplyPoint3x4(local);
                    }
                }

                Assert.Fail("nothing is drawn for that fragment");
                return default;
            }
        }

        private static DisplayScene NewDisplayScene(World w)
        {
            var scene = new DisplayScene
            {
                storage = new VpCpuGeometryStorage(8192, 32768, 64, 256, 256, Allocator.Persistent),
            };
            scene.table = new VpGeometryReferenceTable(scene.storage, 16, 32);
            Shader shader = Shader.Find("Zantetsu/VP Indexed Indirect Unlit");
            Assert.That(shader, Is.Not.Null, "the VP unlit shader");
            scene.material = new Material(shader) { name = "body" };
            var materials = new Dictionary<int, Material> { { 0, scene.material } };
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    scene.storage, scene.table, w.ledger, materials, null, null, 16, 16,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates,
                    VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(4), () => scene.frame,
                    out scene.display),
                Is.True,
                "create the display");
            scene.geometry = AppendCube(scene.storage);
            return scene;
        }

        private static readonly float3[] k_cubeCorners =
        {
            new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, 1f, -1f), new float3(-1f, 1f, -1f),
            new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
        };

        private static readonly int[][] k_cubeFaces =
        {
            new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
            new[] { 2, 3, 7, 6 }, new[] { 1, 2, 6, 5 }, new[] { 0, 4, 7, 3 },
        };

        private static VpStoredGeometry AppendCube(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            foreach (int[] c in k_cubeFaces)
            {
                float3 n = math.normalize(math.cross(
                    k_cubeCorners[c[1]] - k_cubeCorners[c[0]], k_cubeCorners[c[2]] - k_cubeCorners[c[0]]));
                uint b = (uint)vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new VpRenderVertex
                    {
                        position = k_cubeCorners[c[k]], normal = n, uv0 = new float2(0.5f, 0.5f),
                    });
                    topology.Add(c[k]);
                }

                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_cubeCorners.Length,
                    new[] { new VpGeometrySubmesh(0, indices.Count, 0) }, out VpStoredGeometry geometry, out _),
                Is.True,
                "append the cube as something that may be cut");
            return geometry;
        }

        /// <summary>
        /// A real geometry commit of one operation through the product's own entry: the shown geometry is cut at the
        /// adopted plane carried into its own coordinates, and both sides are handed to the display.
        /// </summary>
        private static bool CommitGeometry(
            DisplayScene scene, LogicalFragmentId source, CutOperationId cut, float4 plane,
            LogicalFragmentId positive, LogicalFragmentId negative)
        {
            Assert.That(
                VpCutPlane.TryGeometryLocalToWorld(plane, k_lineageToGeometryLocal, out float4 geometryLocalPlane),
                Is.True,
                "the adopted plane, in the geometry's own coordinates");
            Assert.That(
                VpStorageCutInput.TryAcquire(scene.storage, scene.geometry, out VpStorageCutInput input), Is.True,
                "read the geometry");
            using (input)
            {
                Assert.That(
                    VpStorageCut.TryExecute(scene.storage, input, geometryLocalPlane, out VpStorageCutResult result),
                    Is.True,
                    "cut it");
                Assert.That(result.positive.IsProduced && result.negative.IsProduced, Is.True, "both sides produced");
                return scene.display.TryCommitCut(
                    source, cut, new Vector4(plane.x, plane.y, plane.z, plane.w), positive, in result.positive,
                    negative, in result.negative, result.kernel.capTriangles);
            }
        }

        /// <summary>
        /// The cut is admitted and its owners are built, and only then does the source move and turn. While it is
        /// pending it is drawn where it stands; once it is published both children are drawn there too, because the
        /// publication puts them at the placement the source has by then rather than at the one the build read.
        /// The drawn position — the placement itself, with nothing on it — never goes back to the registration.
        /// </summary>
        [Test]
        public void ASourceThatMovedAfterTheBuild_IsNeverDrawnWhereItWasRegistered()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));
                Vector3 registeredOrigin = registration.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero);

                // Admitted, and the two owners built from where it was then.
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);

                // It moves and turns after that, and before anything is published.
                var moved = new Vector3(2.5f, -1.25f, 0.75f);
                Quaternion turned = Quaternion.AngleAxis(35f, Vector3.forward);
                Move(w.SourceOwner, moved, 35f);
                Matrix4x4 movedPlacement = Expected(moved, turned);
                Vector3 movedOrigin = movedPlacement.MultiplyPoint3x4(Vector3.zero);
                Assert.That(
                    (movedOrigin - registeredOrigin).magnitude, Is.GreaterThan(4f * Apart),
                    "the move is far enough that a drawn position could not be mistaken for the registration's");

                VpMultiCutSnapshot before = NewSnapshot();
                Assert.That(Build(before, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                VpMultiCutRenderFragment pending = Of(before, w.source);
                Same(movedPlacement, pending.geometryLocalToWorld, "the pending source follows its owner");
                Vector3 pendingDrawn = pending.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero);
                Assert.That(
                    (pendingDrawn - movedOrigin).magnitude, Is.LessThanOrEqualTo(2e-3f),
                    "and is drawn exactly there: the display adds nothing");
                Assert.That(
                    (pendingDrawn - registeredOrigin).magnitude, Is.GreaterThan(2f * Apart),
                    "not back where it was registered");

                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                VpMultiCutSnapshot after = NewSnapshot();
                Assert.That(Build(after, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                foreach (LogicalFragmentId child in new[] { positive, negative })
                {
                    VpMultiCutRenderFragment rf = Of(after, child);
                    Same(movedPlacement, rf.geometryLocalToWorld, "the child stands where the source did");
                    Vector3 drawn = rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero);
                    Assert.That(
                        (drawn - movedOrigin).magnitude, Is.LessThanOrEqualTo(2e-3f),
                        "and is drawn exactly there: nothing is added for the display");
                    Assert.That(
                        (drawn - registeredOrigin).magnitude, Is.GreaterThan(2f * Apart),
                        "not back where it was registered");
                }
            }
        }

        /// <summary>
        /// The two children move and turn independently afterwards. Each one's body, its clip half-space, its cap
        /// plane, its cap's outward normal and every vertex of its cap polygon come from its own owner, and from
        /// nothing else: a cap sits on the plane its own side carried, where that side stands.
        /// </summary>
        [Test]
        public void TheTwoChildren_FollowTheirOwnOwners_InBodyClipAndCap()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));

                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                // A quarter turn for one, a plain move for the other.
                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner positiveOwner), Is.True);
                Assert.That(w.registry.TryGet(negative, out PhysicsFragmentOwner negativeOwner), Is.True);
                Move(positiveOwner, new Vector3(3f, 0f, 0f), 90f);
                Move(negativeOwner, new Vector3(-2f, 1f, 0f), 0f);
                Matrix4x4 expectedPositive = Expected(new Vector3(3f, 0f, 0f), Quaternion.AngleAxis(90f, Vector3.forward));
                Matrix4x4 expectedNegative = Expected(new Vector3(-2f, 1f, 0f), Quaternion.identity);

                VpMultiCutSnapshot snapshot = NewSnapshot();
                Assert.That(Build(snapshot, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                VpMultiCutRenderFragment rfPositive = Of(snapshot, positive);
                VpMultiCutRenderFragment rfNegative = Of(snapshot, negative);

                Same(expectedPositive, rfPositive.geometryLocalToWorld, "the positive body");
                Same(expectedNegative, rfNegative.geometryLocalToWorld, "the negative body");


                Assert.That(rfPositive.clip.PlaneCount, Is.EqualTo(1), "the positive side is clipped by its one boundary");
                Vector4 positiveClip = rfPositive.clip.SignedPlane(0);
                Same(new Vector3(-1f, 0f, 0f), new Vector3(positiveClip.x, positiveClip.y, positiveClip.z), "the positive clip normal");
                Assert.That(positiveClip.w, Is.EqualTo(3f).Within(2e-3f), "the positive clip is at its own owner");
                Vector4 negativeClip = rfNegative.clip.SignedPlane(0);
                Same(new Vector3(0f, -1f, 0f), new Vector3(negativeClip.x, negativeClip.y, negativeClip.z), "the negative clip normal");
                Assert.That(negativeClip.w, Is.EqualTo(1f).Within(2e-3f), "the negative clip is at its own owner");

                Assert.That(rfPositive.capCount, Is.GreaterThan(0), "the positive side has a cap");
                Assert.That(snapshot.TryGetCap(rfPositive.capStart, out VpMultiCutCap capPositive), Is.True);
                Same(new Vector3(1f, 0f, 0f), capPositive.outwardNormal, "the positive cap points out of the side that is kept");
                Assert.That(capPositive.worldPlane.w, Is.EqualTo(3f).Within(2e-3f), "the positive cap plane stands at its owner");
                for (int v = 0; v < capPositive.vertexCount; v++)
                {
                    Assert.That(snapshot.TryGetCapVertex(rfPositive.capStart, v, out Vector3 world), Is.True);
                    Assert.That(world.x, Is.EqualTo(3f).Within(2e-3f), "positive cap vertex " + v + " is on its own side's plane");
                }

                Assert.That(snapshot.TryGetCap(rfNegative.capStart, out VpMultiCutCap capNegative), Is.True);
                Same(new Vector3(0f, 1f, 0f), capNegative.outwardNormal, "the negative cap outward normal");
                for (int v = 0; v < capNegative.vertexCount; v++)
                {
                    Assert.That(snapshot.TryGetCapVertex(rfNegative.capStart, v, out Vector3 world), Is.True);
                    Assert.That(world.y, Is.EqualTo(1f).Within(2e-3f), "negative cap vertex " + v + " is on its own side's plane");
                }
            }
        }

        /// <summary>
        /// An anchor fixes one side. Being fixed says nothing about where it is drawn: it follows its own owner like
        /// any other side, and the free one follows its own -- here an owner that has not been moved, so that side is
        /// still exactly at the placement it was published at.
        /// </summary>
        [Test]
        public void TheAnchoredSide_StillFollowsItsOwnOwner()
        {
            using (World w = NewWorld(new[] { new float3(0f, 0.5f, 0f) }, PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));

                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner anchored), Is.True);
                Assert.That(anchored.FixedByAnchors, Is.True, "the anchor is on the positive side");
                Move(anchored, new Vector3(1.5f, 2f, 0f), 45f);
                Matrix4x4 expected = Expected(new Vector3(1.5f, 2f, 0f), Quaternion.AngleAxis(45f, Vector3.forward));

                VpMultiCutSnapshot snapshot = NewSnapshot();
                Assert.That(Build(snapshot, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                VpMultiCutRenderFragment rf = Of(snapshot, positive);
                Same(expected, rf.geometryLocalToWorld, "the anchored side follows its owner all the same");
                Same(
                    Expected(Vector3.zero, Quaternion.identity), Of(snapshot, negative).geometryLocalToWorld,
                    "while the free side is at its own owner, which nothing has moved");
            }
        }

        /// <summary>
        /// The display frame is not the owner's frame. A point of the geometry is drawn at the owner's transform with
        /// that correspondence applied exactly once -- it is neither dropped nor counted twice -- and with nothing else
        /// applied after it. This correspondence is the geometry's own frame against its owner's, which stays; what
        /// the drawn point no longer carries is any displacement of the side for the display.
        /// </summary>
        [Test]
        public void ADisplayFrameOffsetFromTheOwner_IsCarriedExactlyOnce()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));

                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId _, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner owner), Is.True);
                var at = new Vector3(4f, -1f, 2f);
                Move(owner, at, 0f);

                VpMultiCutSnapshot snapshot = NewSnapshot();
                Assert.That(Build(snapshot, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                VpMultiCutRenderFragment rf = Of(snapshot, positive);

                // Worked out here: the owner is at `at` and the geometry sits 0.4 above it in its own coordinates.
                // That 0.4 is the whole of the difference; being the positive side of y = 0 adds nothing to it.
                foreach (Vector3 local in new[] { Vector3.zero, new Vector3(1f, 1f, 1f), new Vector3(-1f, 0.5f, -1f) })
                {
                    Vector3 expected = at + new Vector3(0f, 0.4f, 0f) + local;
                    Same(expected, rf.geometryLocalToWorld.MultiplyPoint3x4(local), "a drawn point");
                }

                // The other side of the same correspondence: dropping it would put the origin at the owner itself.
                Assert.That(
                    rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero).y, Is.Not.EqualTo(at.y).Within(2e-3f),
                    "and the correspondence is carried, not lost");
            }
        }

        /// <summary>
        /// A second cut of one child, published the same way, and then the ancestor's geometry committed through the
        /// display's own commit. The fragment between them is replaced and its owner is gone — nothing keeps it alive
        /// to be drawn from — and the two living descendants each follow an owner of their own: their drawn positions
        /// do not move across the real commit, and they still follow a turn made afterwards.
        /// </summary>
        [Test]
        public void AfterASecondCutAndARealCommit_TheLivingDescendantsFollowWithoutTheReplacedOwner()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            using (DisplayScene scene = NewDisplayScene(w))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                scene.display.Placement = lookup;
                Assert.That(
                    scene.display.TryShow(
                        w.source, scene.geometry, Expected(Vector3.zero, Quaternion.identity),
                        k_lineageToGeometryLocal, Array.Empty<VpClipBoundary>()),
                    Is.True,
                    "the source is shown");

                var adopted = new float4(w.harness.planeN, w.harness.planeW);
                CutOperationId first = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, first, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, first, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner middle), Is.True);
                Move(middle, new Vector3(1f, 1f, 0f), 30f);

                // B, on the positive child, from its own physics.
                var plane = new float4(1f, 0f, 0f, 0f);
                Assert.That(
                    w.ledger.Admit(positive, plane, true, out CutOperationId second), Is.EqualTo(LogicalCutAdmission.Admitted));
                Assert.That(
                    w.ledger.PrepareAnchorDistribution(second, w.harness.eps, out AnchorDistributionResult distribution),
                    Is.EqualTo(AnchorPreparationOutcome.Prepared));

                LogicalFragmentId grandPositive;
                LogicalFragmentId grandNegative;
                using (var next = new SecondCutInput(middle, plane.xyz, plane.w, w.harness.eps))
                {
                    PhysicsOwnerShape cutFrom = middle.Shape;
                    PhysicsCutRequest request = w.cook.Submit(in next.input, float4x4.identity);
                    w.RunUntil(() => request.IsOver, "the second cut and cook end");
                    Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the second cut produced something");

                    var buildInput = new PhysicsOwnerBuildInput
                    {
                        products = request.Products,
                        placement = middle.ReadPlacement(),
                        sourceMotion = middle.ReadMotion(float3.zero),
                        anchors = distribution,
                        parentMass = middle.Mass,
                        inheritedMeshes = middle.Shape.Meshes,
                        name = "Grandchild",
                    };
                    Assert.That(
                        PhysicsOwnerBuilder.TryBuild(in buildInput, out PhysicsOwnerCandidate grandCandidate, out PhysicsOwnerBuildOutcome outcome),
                        Is.True,
                        "the two owners of the second cut were built: " + outcome);
                    Assert.That(
                        Publish(w, second, grandCandidate, request.Products, out grandPositive, out grandNegative, out LogicalCutResultOutcome _, float3.zero, 0f, cutFrom, positive),
                        Is.EqualTo(PhysicsPublicationOutcome.Published),
                        "the second publication went through");
                }

                Assert.That(
                    w.registry.TryGet(positive, out PhysicsFragmentOwner _), Is.False,
                    "the replaced fragment has no owner kept for the display");
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(positive, out Matrix4x4 _),
                    Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "and asking about it is refused rather than answered with where it was");

                // The two descendants, each somewhere of its own.
                Assert.That(w.registry.TryGet(grandPositive, out PhysicsFragmentOwner gp), Is.True);
                Assert.That(w.registry.TryGet(grandNegative, out PhysicsFragmentOwner gn), Is.True);
                Move(gp, new Vector3(4f, 0f, 0f), 0f);
                Move(gn, new Vector3(-4f, 0f, 0f), 90f);

                scene.Collect();
                var beforePositive = new Vector3[k_points.Length];
                var beforeNegative = new Vector3[k_points.Length];
                for (int i = 0; i < k_points.Length; i++)
                {
                    beforePositive[i] = scene.Drawn(grandPositive, k_points[i]);
                    beforeNegative[i] = scene.Drawn(grandNegative, k_points[i]);
                }

                // The ancestor's geometry, committed through the display's own entry with no intermediate owner
                // anywhere: the first boundary stops being temporary and its share goes into each side's geometry.
                Assert.That(
                    CommitGeometry(scene, w.source, first, adopted, positive, negative), Is.True,
                    "the geometry commit is established");
                scene.Collect();
                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(beforePositive[i], scene.Drawn(grandPositive, k_points[i]), "the positive descendant's point " + i + " does not move");
                    Same(beforeNegative[i], scene.Drawn(grandNegative, k_points[i]), "the negative descendant's point " + i + " does not move");
                }

                // And it still follows: a turn of that owner moves everything drawn from it by exactly that turn.
                Matrix4x4 was = Matrix4x4.TRS(new Vector3(4f, 0f, 0f), Quaternion.identity, Vector3.one);
                Move(gp, new Vector3(4f, 0.5f, 0f), 55f);
                Matrix4x4 now = Matrix4x4.TRS(new Vector3(4f, 0.5f, 0f), Quaternion.AngleAxis(55f, Vector3.forward), Vector3.one);
                Matrix4x4 moved = now * was.inverse;
                scene.Collect();
                for (int i = 0; i < k_points.Length; i++)
                {
                    Same(
                        moved.MultiplyPoint3x4(beforePositive[i]), scene.Drawn(grandPositive, k_points[i]),
                        "the committed descendant's point " + i + " turns with its owner");
                    Same(
                        beforeNegative[i], scene.Drawn(grandNegative, k_points[i]),
                        "while the other descendant does not move with it");
                }
            }
        }

        private static readonly Vector3[] k_points =
        {
            Vector3.zero, new Vector3(1f, 1f, 1f), new Vector3(-1f, 0.5f, -0.25f), new Vector3(0.75f, -1f, 1f),
        };

        /// <summary>
        /// A publication the ledger refuses changes no correspondence at all, and retiring one published side leaves
        /// the other exactly as it is. The stale ending and the abort after a physics failure are covered where those
        /// paths are tested, not here.
        /// </summary>
        [Test]
        public void ALedgerRefusalAndAOneSidedRetirement_LeaveTheOtherCorrespondenceAlone()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);

                // A cut the ledger refuses: nothing of the correspondence moves and the source keeps following.
                CutOperationId unprepared = Admit(w, prepareAnchors: false);
                FixedSupportAnchors.TryDistribute(
                    Array.Empty<float3>(), new float4(w.harness.planeN, w.harness.planeW), w.harness.eps,
                    new List<float3>(), new List<float3>(), out AnchorDistributionResult direct);
                PhysicsOwnerCandidate refusedCandidate = Build(
                    w, unprepared, out PhysicsCutProducts refusedProducts, in w.harness.input, direct);
                int owners = w.registry.Count;
                Assert.That(
                    Publish(w, unprepared, refusedCandidate, refusedProducts, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.LedgerRefused));
                Assert.That(w.registry.Count, Is.EqualTo(owners), "no correspondence was added or taken away");
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(w.source, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Following),
                    "and the source still follows its own owner");
                refusedCandidate.Dispose();
                refusedProducts.Dispose();
                Assert.That(w.ledger.Abort(unprepared), Is.EqualTo(LogicalCutResultOutcome.Applied));

                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.True, "the source was not retired for it");
            }

            // A published cut, then one side retired: the other is untouched.
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId negative, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                Assert.That(w.registry.Retire(negative), Is.True, "one side is retired");
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(negative, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "the retired side is refused, not drawn where it was");
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(positive, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Following),
                    "the living side is not ended with it");
                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner living), Is.True);
                Assert.That(living.Root != null && living.Root.activeInHierarchy, Is.True, "and its body is still in the scene");
            }
        }

        /// <summary>
        /// An owner **in the scene** whose display is arranged some other way says so. A fragment with no physics at
        /// all, and one whose owner has left the scene, are both refused — with or without a correspondence — so that
        /// a gap is never answered with the registration's placement.
        /// </summary>
        [Test]
        public void AnArrangementIsAnAnswerAndAGapIsNot()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(w.source, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Static),
                    "an owner that says nothing about its display is an arrangement");

                LogicalFragmentId stranger = w.ledger.AddFragment();
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(stranger, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "a fragment with no owner is refused");

                Assert.That(w.registry.Withdraw(w.source), Is.True);
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(w.source, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "an owner that has left the scene is a gap, whether or not it ever said anything");
            }

            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                Assert.That(w.registry.Withdraw(w.source), Is.True, "its owner leaves the scene");
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(w.source, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "a following fragment whose owner has left is refused, not drawn where it was");
            }
        }

        /// <summary>
        /// A snapshot is settled once: an owner that moves afterwards does not move what has already been built, and
        /// the next collection is where the move appears.
        /// </summary>
        [Test]
        public void AnOwnerThatMovesAfterTheCollection_ChangesNothingUntilTheNextOne()
        {
            using (World w = NewWorld(Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                VpMultiCutRegistration registration = Registered(w, Expected(Vector3.zero, Quaternion.identity));

                CutOperationId operation = Admit(w);
                PhysicsOwnerCandidate candidate = Build(w, operation, out PhysicsCutProducts products, in w.harness.input);
                Assert.That(
                    Publish(w, operation, candidate, products, out LogicalFragmentId positive, out LogicalFragmentId _, out LogicalCutResultOutcome _),
                    Is.EqualTo(PhysicsPublicationOutcome.Published));

                VpMultiCutSnapshot settled = NewSnapshot();
                Assert.That(Build(settled, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Matrix4x4 asSettled = Of(settled, positive).geometryLocalToWorld;

                Assert.That(w.registry.TryGet(positive, out PhysicsFragmentOwner owner), Is.True);
                Move(owner, new Vector3(9f, -9f, 3f), 120f);
                Same(asSettled, Of(settled, positive).geometryLocalToWorld, "what was settled did not move with the owner");

                VpMultiCutSnapshot next = NewSnapshot();
                Assert.That(Build(next, w, registration, lookup), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Same(
                    Expected(new Vector3(9f, -9f, 3f), Quaternion.AngleAxis(120f, Vector3.forward)),
                    Of(next, positive).geometryLocalToWorld,
                    "and the next collection is where it appears");
            }
        }

        // ----- an aggregate drawn past the clip capacity ---------------------------------------------------------------

        /// <summary>
        /// Publishes one more cut of <paramref name="fragment"/> for real: the convexes are cut and cooked, the two
        /// owners are built from where that owner is now, and the publication switches the correspondence. The
        /// positive child comes back.
        /// </summary>
        private static LogicalFragmentId PublishOneMore(
            World w, LogicalFragmentId fragment, float3 normal, float offset, out LogicalFragmentId other)
        {
            Assert.That(w.registry.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True, "it has an owner to cut");
            var plane = new float4(normal, -offset);
            Assert.That(
                w.ledger.Admit(fragment, plane, true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted), "the cut is admitted");
            Assert.That(
                w.ledger.PrepareAnchorDistribution(operation, w.harness.eps, out AnchorDistributionResult anchors),
                Is.EqualTo(AnchorPreparationOutcome.Prepared), "its anchors are distributed");

            LogicalFragmentId positive;
            using (var next = new SecondCutInput(owner, normal, -offset, w.harness.eps))
            {
                PhysicsOwnerShape cutFrom = owner.Shape;
                PhysicsCutRequest request = w.cook.Submit(in next.input, float4x4.identity);
                w.RunUntil(() => request.IsOver, "the cut and cook end");
                Assert.That(request.Outcome, Is.EqualTo(PhysicsCutOutcomeKind.Ok), "the convex cut produced sides");

                var buildInput = new PhysicsOwnerBuildInput
                {
                    products = request.Products,
                    placement = owner.ReadPlacement(),
                    sourceMotion = owner.ReadMotion(float3.zero),
                    anchors = anchors,
                    parentMass = owner.Mass,
                    inheritedMeshes = owner.Shape.Meshes,
                    name = "Deep",
                };
                Assert.That(
                    PhysicsOwnerBuilder.TryBuild(in buildInput, out PhysicsOwnerCandidate candidate, out PhysicsOwnerBuildOutcome built),
                    Is.True,
                    "the two owners were built: " + built);
                Assert.That(
                    Publish(w, operation, candidate, request.Products, out positive, out other, out LogicalCutResultOutcome _, float3.zero, 0f, cutFrom, fragment),
                    Is.EqualTo(PhysicsPublicationOutcome.Published),
                    "the publication went through");
            }

            return positive;
        }

        /// <summary>
        /// Nine boundaries published for real on one chain. The ninth is past the clip capacity, so what is behind it
        /// is drawn once, and the fragment that cut is aggregated at has been replaced by that very publication and
        /// has no owner left. The display collects all the same -- this is the route that used to stop it for good --
        /// and the one shape it draws stands where the first living branch of the aggregate stands and follows it.
        /// </summary>
        [Test]
        public void AnAggregateWhoseRootWasPublishedAway_IsCollectedAndFollowsItsFirstBranch()
        {
            // Nothing commits any geometry here, so every one of these cuts holds its budget unit at once.
            using (World w = NewWorld(
                Array.Empty<float3>(), PhysicsOwnerPlacement.Identity, k_geometryLocalToOwner,
                VpClipCandidates.Capacity + 4))
            using (DisplayScene scene = NewDisplayScene(w))
            {
                var lookup = new PhysicsOwnerPlacementLookup(w.registry);
                scene.display.Placement = lookup;
                Assert.That(
                    scene.display.TryShow(
                        w.source, scene.geometry, Expected(Vector3.zero, Quaternion.identity),
                        k_lineageToGeometryLocal, Array.Empty<VpClipBoundary>()),
                    Is.True,
                    "the source is shown");

                // Down the positive side, one more cut than the clip capacity: the last one cannot be selected and
                // is what the aggregate forms behind. Every plane is along the prisms' own axis, so each piece stays
                // a prism of the same few faces however many times it is cut, and both sides always have material:
                // the compound has a box above all of these planes and one below the first.
                var up = new float3(0f, 1f, 0f);
                var offsets = new[] { 0f, 0.2f, 0.4f, 0.6f, 0.7f, 0.8f, 0.85f, 0.9f, 0.95f };
                Assert.That(
                    offsets.Length, Is.EqualTo(VpClipCandidates.Capacity + 1), "one more than can be selected");
                LogicalFragmentId at = w.source;
                LogicalFragmentId lastSource = default;
                LogicalFragmentId first = default;
                LogicalFragmentId second = default;
                for (int i = 0; i < offsets.Length; i++)
                {
                    lastSource = at;
                    at = PublishOneMore(w, at, up, offsets[i], out LogicalFragmentId other);
                    first = at;
                    second = other;
                }

                // The ninth cut's source is the one the aggregate is rooted at, and the publication ended its owner.
                Assert.That(
                    w.ledger.TryGetFragmentState(lastSource, out LogicalFragmentState state)
                    && state == LogicalFragmentState.Replaced,
                    Is.True,
                    "the aggregation root has been replaced");
                Assert.That(
                    w.registry.TryGet(lastSource, out PhysicsFragmentOwner _), Is.False, "and has no owner left");
                Assert.That(
                    lookup.TryGetGeometryLocalToWorld(lastSource, out Matrix4x4 _),
                    Is.EqualTo(VpFragmentPlacementKind.Missing),
                    "so nothing can say where it stands");

                // The collection that used to stop the display for good.
                scene.Collect();
                Assert.That(scene.display.IsHalted, Is.False, "the display is still running");

                VpMultiCutRenderFragment aggregate = default;
                int aggregates = 0;
                for (int r = 0; r < scene.display.RenderFragmentCount; r++)
                {
                    Assert.That(scene.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    if (rf.aggregated)
                    {
                        aggregate = rf;
                        aggregates++;
                    }
                }

                Assert.That(aggregates, Is.EqualTo(1), "one shape is drawn for what is behind the ignored boundary");
                Assert.That(aggregate.root, Is.EqualTo(lastSource), "rooted at the replaced fragment");
                Assert.That(
                    w.registry.TryGet(first, out PhysicsFragmentOwner firstOwner), Is.True,
                    "its first living branch has an owner");
                Assert.That(w.registry.TryGet(second, out PhysicsFragmentOwner _), Is.True, "and so has the other");

                Vector3 drawnBefore = aggregate.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero);

                // It follows that branch: moving its owner moves the one shape by the same rigid motion.
                Matrix4x4 was = firstOwner.Root.transform.localToWorldMatrix;
                Move(firstOwner, new Vector3(6f, -2f, 1f), 70f);
                Matrix4x4 now = firstOwner.Root.transform.localToWorldMatrix;
                scene.Collect();

                VpMultiCutRenderFragment moved = default;
                for (int r = 0; r < scene.display.RenderFragmentCount; r++)
                {
                    Assert.That(scene.display.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    if (rf.aggregated)
                    {
                        moved = rf;
                    }
                }

                Same(
                    (now * was.inverse).MultiplyPoint3x4(drawnBefore),
                    moved.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero),
                    "the aggregate moved exactly as its first branch's owner did");
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
