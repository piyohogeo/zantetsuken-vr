using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
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
    /// The product entry of the Provisional path (DESIGN 7.1.1, 14 T-091): an asked-for cut is classified, accepted,
    /// built and published in the update it was asked in, and the cut that follows it is the frame's business and not
    /// the publication's.
    /// <para>
    /// **Everything after the ask goes through the product.** The ask itself comes from here, which is what a weapon
    /// or a tool would do; the classification, the acceptance, the build, the publication, the submission of the final
    /// cut and the recovery are all <see cref="ProvisionalCutDriver"/>'s. Nothing here reaches around it.
    /// </para>
    /// <para>
    /// **What is watched is the destination and the scene**: which work an executor accepted and was told to begin,
    /// which objects are in the scene, what the correspondence answers, and what a collection built from the ledger
    /// and the placement lookup puts where. A stage or a queue is not a submission.
    /// </para>
    /// <para>
    /// **What these tests do not say.** No hit detection, no handoff to a Final publication, no building constraint,
    /// no character, no XR and nothing measured. The display's own component is not driven: the collection is built
    /// from the same snapshot and lookup a display collects through.
    /// </para>
    /// </summary>
    public unsafe class ProvisionalCutDriverTests
    {
        private const double ParentMass = 12.0;
        private const float SupportEpsilon = 1e-5f;
        private const float AnchorEpsilon = 0.01f;
        private const int VertexLimit = 128;
        private const int DeadlineMilliseconds = 30000;

        private readonly List<IDisposable> _disposables = new List<IDisposable>();
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<Material> _materials = new List<Material>();

        [TearDown]
        public void Cleanup()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }

            _objects.Clear();
            foreach (Mesh mesh in _meshes)
            {
                if (mesh != null)
                {
                    UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            _meshes.Clear();
            foreach (Material material in _materials)
            {
                if (material != null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                }
            }

            _materials.Clear();
        }

        // ----- a destination whose collection the test controls --------------------------------------------------------

        /// <summary>
        /// A destination that runs work for real and hands it back only once the test has let it go. Holding something
        /// back delays a collection that would otherwise happen; it never makes a work finish.
        /// </summary>
        private sealed class HoldingExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _held = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _released = new List<IDispatchWork>();
            private bool _closed;

            internal HoldingExecutor(WorkDestination destination, int capacity)
            {
                Destination = destination;
                Capacity = capacity;
            }

            internal List<IDispatchWork> Accepted { get; } = new List<IDispatchWork>();

            internal List<IDispatchWork> Begun { get; } = new List<IDispatchWork>();

            internal bool HoldEverything { get; set; }

            public WorkDestination Destination { get; }

            public int Capacity { get; }

            public int Held => _held.Count;

            public bool CanAccept => !_closed && _held.Count < Capacity;

            internal void ReleaseEverything()
            {
                HoldEverything = false;
                for (int i = 0; i < Accepted.Count; i++)
                {
                    if (!_released.Contains(Accepted[i]))
                    {
                        _released.Add(Accepted[i]);
                    }
                }
            }

            private bool Lets(IDispatchWork work)
            {
                return !HoldEverything || _released.Contains(work);
            }

            public bool TryAccept(IDispatchWork work)
            {
                if (!CanAccept)
                {
                    return false;
                }

                _held.Add(work);
                Accepted.Add(work);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                Begun.Add(work);
                work.Begin();
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    IDispatchWork candidate = _held[i];
                    if (!candidate.IsComplete || !Lets(candidate))
                    {
                        continue;
                    }

                    _held.RemoveAt(i);
                    work = candidate;
                    completion = WorkCompletion.Finished;
                    return true;
                }

                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                return _held.Count == 0;
            }
        }

        // ----- one authored source, and the services the driver is bound to --------------------------------------------

        private sealed class World : IDisposable
        {
            internal OwnerCutHarness harness;
            internal PhysicsShapeSource meshSource;
            internal PhysicsOwnerShape shape;
            internal PhysicsOwnerRegistry registry;
            internal LogicalCutLedger ledger;
            internal PhysicsOwnerPlacementLookup lookup;
            internal SharedWorkDispatcher dispatcher;
            internal HoldingExecutor job;
            internal WorkerPoolExecutor geometry;
            internal WorkerPoolExecutor background;
            internal PhysicsCutCook cook;
            internal SharedWorkFrame frame;
            internal ProvisionalCutDriver driver;
            internal LogicalFragmentId source;
            internal GameObject root;

            internal PhysicsFragmentOwner SourceOwner
            {
                get
                {
                    registry.TryGet(source, out PhysicsFragmentOwner owner);
                    return owner;
                }
            }

            public void Dispose()
            {
                if (driver != null)
                {
                    UnityEngine.Object.DestroyImmediate(driver.gameObject);
                }

                registry?.Dispose();
                cook?.Dispose();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (cook != null && !cook.IsDrained && clock.ElapsedMilliseconds < DeadlineMilliseconds)
                {
                    job.ReleaseEverything();
                    cook.Pump();
                    dispatcher.BeginFrame(clock.ElapsedMilliseconds > 0 ? (int)clock.ElapsedMilliseconds : 1);
                    dispatcher.Dispatch();
                }

                dispatcher?.Shutdown(DeadlineMilliseconds);
                cook?.Pump();
                geometry?.Dispose();
                background?.Dispose();
                harness?.Dispose();
            }
        }

        private static readonly Matrix4x4 k_geometryLocalToOwner =
            Matrix4x4.TRS(new Vector3(0f, 0.25f, 0f), Quaternion.identity, Vector3.one);

        private World NewWorld(
            int frameBudget = 32,
            int concurrentReservations = 1,
            float supportEpsilon = SupportEpsilon,
            double3[] convexOffsets = null,
            float3[] anchors = null,
            Func<int> frameSource = null)
        {
            var w = new World
            {
                harness = new OwnerCutHarness(),
                registry = new PhysicsOwnerRegistry(),
                ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8)),
                job = new HoldingExecutor(WorkDestination.UnityJob, 4),
                geometry = WorkerPoolExecutor.GeometryPool(2),
                background = WorkerPoolExecutor.BackgroundPool(2),
            };
            _disposables.Add(w);

            w.dispatcher = new SharedWorkDispatcher(8, 2, frameBudget, w.job, w.geometry, w.background);

            // The cook's concurrent reservation count is supplied here, by the caller. It is a test value and says
            // nothing about what the product should use.
            w.cook = new PhysicsCutCook(w.dispatcher, concurrentReservations);
            w.frame = new SharedWorkFrame(w.dispatcher);
            w.frame.Add(w.cook);

            w.harness.planeN = new float3(0f, 1f, 0f);
            w.harness.planeW = 0f;
            w.harness.eps = supportEpsilon;
            w.harness.parentMass = ParentMass;
            if (convexOffsets == null)
            {
                w.harness.Add(CaseGenerator.Box());
            }
            else
            {
                foreach (double3 offset in convexOffsets)
                {
                    w.harness.Add(Translated(CaseGenerator.Box(), offset));
                }
            }

            w.harness.Build();

            var ranges = new ConvexBrepRange[w.harness.input.convexCount];
            for (int c = 0; c < ranges.Length; c++)
            {
                ranges[c] = w.harness.input.convexes[c];
            }

            var meshes = new List<Mesh>();
            for (int c = 0; c < ranges.Length; c++)
            {
                meshes.Add(BoxColliderOf(w.harness.input.bank, ranges[c], "Authored " + c));
            }

            w.meshSource = PhysicsShapeSource.External();
            w.shape = PhysicsOwnerShape.Authored(w.harness.input.bank, ranges, meshes, w.meshSource, float4x4.identity);

            w.root = new GameObject("Authored Source");
            _objects.Add(w.root);
            var body = w.root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < w.shape.ConvexCount; c++)
            {
                MeshCollider collider = w.root.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = w.shape.MeshOf(c);
            }

            w.source = w.ledger.AddFragment(anchors);
            w.registry.RegisterAuthored(w.source, w.root, body, w.shape, false, k_geometryLocalToOwner);
            w.lookup = new PhysicsOwnerPlacementLookup(w.registry);

            var driverObject = new GameObject("Provisional Cut Driver");
            _objects.Add(driverObject);
            w.driver = driverObject.AddComponent<ProvisionalCutDriver>();
            w.driver.Bind(
                w.ledger, w.registry, w.cook, w.frame, null, supportEpsilon, AnchorEpsilon, VertexLimit, frameSource);
            return w;
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

        private Mesh BoxColliderOf(ConvexBrepBank bank, ConvexBrepRange range, string name)
        {
            var lo = new float3(float.PositiveInfinity);
            var hi = new float3(float.NegativeInfinity);
            for (int v = 0; v < range.vertexCount; v++)
            {
                float3 at = bank.vertices[range.vertexBase + v];
                lo = math.min(lo, at);
                hi = math.max(hi, at);
            }

            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
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
            _meshes.Add(mesh);
            return mesh;
        }

        private static ProvisionalCutAsk Ask(World w, float positiveImpulse = 0f, float negativeImpulse = 0f)
        {
            return new ProvisionalCutAsk
            {
                source = w.source,
                plane = new float4(w.harness.planeN, w.harness.planeW),
                positiveSeparationImpulse = positiveImpulse,
                negativeSeparationImpulse = negativeImpulse,
                renderAnchor = float3.zero,
            };
        }

        private static ProvisionalCutTransaction Publish(World w, float positiveImpulse = 0f, float negativeImpulse = 0f)
        {
            ProvisionalCutAsk ask = Ask(w, positiveImpulse, negativeImpulse);
            Assert.That(
                w.driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission),
                Is.EqualTo(ProvisionalCutAcceptance.Published),
                "the cut was accepted and published");
            Assert.That(admission, Is.EqualTo(LogicalCutAdmission.Admitted));
            return transaction;
        }

        /// <summary>What a display collects: the snapshot built from the ledger and the placement lookup.</summary>
        private static VpMultiCutSnapshot Collect(World w, out VpMultiCutRegistration registration)
        {
            registration = new VpMultiCutRegistration(
                w.source,
                new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f)),
                w.root != null ? w.root.transform.localToWorldMatrix * k_geometryLocalToOwner : k_geometryLocalToOwner,
                Matrix4x4.identity,
                Array.Empty<VpClipBoundary>(),
                1e-4f);
            var snapshot = new VpMultiCutSnapshot(new VpMultiCutCapacities(16, 128, 16, 64, 16));
            Assert.That(
                snapshot.TryBuild(w.ledger, new List<VpMultiCutRegistration> { registration }, w.lookup),
                Is.EqualTo(VpMultiCutBuildOutcome.Built),
                "the collection built");
            return snapshot;
        }

        private static void RunUntil(World w, int frameId, Func<bool> condition, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; clock.ElapsedMilliseconds < DeadlineMilliseconds; f++)
            {
                w.job.ReleaseEverything();
                w.driver.Advance(frameId + f);
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(1);
            }

            Assert.Fail(what + ": it had not happened within the deadline");
        }

        // ----- 1. the acceptance frame (T-091) -------------------------------------------------------------------------

        /// <summary>
        /// A cut with everything it needs is **published in the update it was asked in**, and the collection that
        /// follows in that same frame draws each side where its own actor stands. The final cut is still running at
        /// that moment: taking its reservation, its numbers, its bake and its display geometry are none of them
        /// conditions of the publication.
        /// </summary>
        [Test]
        public void AnAskWithWhatItNeeds_IsPublishedInThatUpdate_WithTheCutStillRunning()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                int fragmentsBefore = w.ledger.FragmentCount;

                ProvisionalCutTransaction transaction = Publish(w);

                // Published already, in the call that asked.
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1), "the pair is in the correspondence");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.True, "and the source has left the scene");
                Assert.That(transaction.Pair.IsStanding(true) && transaction.Pair.IsStanding(false), Is.True);
                Assert.That(w.ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no logical child was made");

                // And the cut of it is not finished: it has not even been offered yet, which is the frame's business.
                Assert.That(transaction.Cut, Is.Not.Null, "the final cut was submitted");
                Assert.That(transaction.Cut.IsOver, Is.False, "and is not finished");
                Assert.That(transaction.CutOutcome, Is.EqualTo(PhysicsCutOutcomeKind.Pending));

                // The same frame carries the cut as far as the budget allows -- and the publication did not wait for
                // any of it.
                w.driver.Advance(9);
                Assert.That(w.job.Accepted.Count, Is.GreaterThan(0), "the cut's work reached its destination");
                Assert.That(w.job.Begun.Count, Is.EqualTo(w.job.Accepted.Count), "and was begun there");
                Assert.That(transaction.Cut.IsOver, Is.False, "while the collection of it is still held back");

                // The collection of this same frame: each side is drawn where its own actor stands.
                VpMultiCutSnapshot snapshot = Collect(w, out VpMultiCutRegistration _);
                var places = new List<Vector3>();
                for (int r = 0; r < snapshot.RenderFragmentCount; r++)
                {
                    Assert.That(snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                    places.Add(rf.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero));
                }

                Assert.That(places.Count, Is.EqualTo(2), "the two sides of the accepted cut were collected");
                Vector3 positive = transaction.Pair.Positive.Root.transform.localToWorldMatrix
                    .MultiplyPoint3x4(k_geometryLocalToOwner.MultiplyPoint3x4(Vector3.zero));
                Assert.That(
                    places[0], Is.EqualTo(positive).Using(Vector3Within(1e-4f)),
                    "drawn where the positive actor is");
            }
        }

        /// <summary>
        /// Moving the real actors moves what the next collection draws, each side following its own. Nothing here
        /// assigns a placement: the actors are moved and the collection is built again.
        /// </summary>
        [Test]
        public void MovingTheRealActors_MovesEachSidesOwnDrawing()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);

                VpMultiCutSnapshot before = Collect(w, out VpMultiCutRegistration _);
                Assert.That(before.TryGetRenderFragment(0, out VpMultiCutRenderFragment firstBefore), Is.True);
                Assert.That(before.TryGetRenderFragment(1, out VpMultiCutRenderFragment secondBefore), Is.True);

                transaction.Pair.Positive.Root.transform.position = new Vector3(5f, 1f, -2f);

                VpMultiCutSnapshot after = Collect(w, out VpMultiCutRegistration _);
                Assert.That(after.TryGetRenderFragment(0, out VpMultiCutRenderFragment firstAfter), Is.True);
                Assert.That(after.TryGetRenderFragment(1, out VpMultiCutRenderFragment secondAfter), Is.True);

                Assert.That(
                    firstAfter.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero),
                    Is.Not.EqualTo(firstBefore.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero))
                        .Using(Vector3Within(1e-4f)),
                    "the side whose actor moved is drawn somewhere else");
                Assert.That(
                    secondAfter.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero),
                    Is.EqualTo(secondBefore.geometryLocalToWorld.MultiplyPoint3x4(Vector3.zero)).Using(Vector3Within(1e-4f)),
                    "and the other side did not move with it");
            }
        }

        // ----- 2. one classification, and the empty side ---------------------------------------------------------------

        /// <summary>
        /// The pair and the final cut are given the **same** classification: the sides the pair was allocated by are
        /// the very bytes the kernel reads, from the one scan the driver made.
        /// </summary>
        [Test]
        public void ThePairAndTheCut_ReadTheOneClassification()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);

                PhysicsCutClassification classification = transaction.Classification;
                Assert.That(classification, Is.Not.Null, "the scan is held while the cut is running");
                ConvexCutOwnerInput input = classification.Input;
                Assert.That(
                    input.convexCount, Is.EqualTo(w.shape.ConvexCount), "the kernel's input covers the whole shape");

                for (int c = 0; c < classification.ConvexCount; c++)
                {
                    Assert.That(
                        (ConvexSide)input.sides[c], Is.EqualTo(classification.Sides[c]),
                        "convex " + c + ": the kernel's byte and the pair's disposition are the same value");
                }

                // And the plane and the vertex limit the caller supplied are what the kernel was given.
                Assert.That((float3)input.plane.xyz, Is.EqualTo(w.harness.planeN).Using(Float3Within(1e-6f)));
                Assert.That(input.vertexLimit, Is.EqualTo(VertexLimit));
                Assert.That(input.parentMass, Is.EqualTo(ParentMass).Within(1e-9));
            }
        }

        /// <summary>
        /// A plane that leaves one side without a convex is the existing no-op: nothing is accepted, no pair is made,
        /// and the ledger is untouched -- it is not an abort and the source is still live.
        /// </summary>
        [Test]
        public void APlaneThatLeavesOneSideEmpty_IsANoOpAndAcceptsNothing()
        {
            using (World w = NewWorld())
            {
                // Far above the box: every convex has support on one side only.
                ProvisionalCutAsk ask = Ask(w);
                ask.plane = new float4(0f, 1f, 0f, -100f);

                Assert.That(
                    w.driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission),
                    Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
                Assert.That(transaction, Is.Null, "nothing is held for it");
                Assert.That(admission, Is.Not.EqualTo(LogicalCutAdmission.Admitted), "and nothing was accepted");
                Assert.That(w.ledger.OperationCount, Is.Zero, "no operation was issued");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "the source keeps its own physics");
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state)
                        && state == LogicalFragmentState.Live,
                    Is.True);
            }
        }

        // ----- 3. the input outlives the source and the readers of what the cut made -----------------------------------

        /// <summary>
        /// A shape with a wholly negative convex beside one whose every vertex is within the epsilon of the plane has
        /// **no positive support at all**, so there is nothing on that side to cut off: the ask is the existing no-op.
        /// <para>
        /// The allocation rule would put the near-plane convex on the positive side -- that rule is unchanged -- so a
        /// question asked of the allocation would have accepted this. What decides is the support of the set.
        /// </para>
        /// </summary>
        [Test]
        public void AShapeWithNoSupportOnOneSide_IsANoOp_EvenThoughAConvexWouldBeAllocatedThere()
        {
            // A wide epsilon makes the box at the origin all near-plane; the far one is wholly below.
            using (World w = NewWorld(
                supportEpsilon: 10f,
                convexOffsets: new[] { new double3(0.0, 0.0, 0.0), new double3(0.0, -30.0, 0.0) }))
            {
                // What the classification itself says, before the ask: one side has support and the other has none,
                // while both sides would be allocated a convex.
                Assert.That(
                    PhysicsCutClassification.TryClassify(
                        w.shape, new float4(0f, 1f, 0f, 0f), 10f, ParentMass, VertexLimit,
                        out PhysicsCutClassification classification),
                    Is.True);
                using (classification)
                {
                    Assert.That(classification.SupportsNegative, Is.True, "the far convex is support below the plane");
                    Assert.That(classification.SupportsPositive, Is.False, "and nothing at all is support above it");
                    Assert.That(classification.SplitsBothSides, Is.False, "so this plane cuts nothing off");

                    bool aConvexOnEachSide = false;
                    for (int c = 0; c < classification.ConvexCount; c++)
                    {
                        aConvexOnEachSide |= classification.Sides[c] == ConvexSide.NearPlaneToPositive;
                    }

                    Assert.That(
                        aConvexOnEachSide, Is.True,
                        "and the allocation does put a convex on the positive side, which is not the question");
                }

                ProvisionalCutAsk ask = Ask(w);
                Assert.That(
                    w.driver.RequestCut(in ask, out ProvisionalCutTransaction none, out LogicalCutAdmission _),
                    Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
                Assert.That(none, Is.Null);
                Assert.That(w.ledger.OperationCount, Is.Zero, "nothing was accepted");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False);
            }
        }

        /// <summary>
        /// The ordinary case of the same shape: with support above the plane in one convex and below it in another, the
        /// ask is accepted and published, even though no single convex is crossed.
        /// </summary>
        [Test]
        public void SupportOnBothSidesInDifferentConvexes_IsAcceptedAndPublished()
        {
            using (World w = NewWorld(convexOffsets: new[] { new double3(0.0, 3.0, 0.0), new double3(0.0, -3.0, 0.0) }))
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
                Assert.That(
                    transaction.Classification.SupportsPositive && transaction.Classification.SupportsNegative, Is.True,
                    "support on both sides, in different convexes");
                for (int c = 0; c < transaction.Classification.ConvexCount; c++)
                {
                    Assert.That(
                        transaction.Classification.Sides[c], Is.Not.EqualTo(ConvexSide.Split),
                        "and no convex is crossed");
                }
            }
        }

        /// <summary>
        /// The hold the driver takes on the input shape keeps its bank and its meshes alive **past the source's own
        /// retirement** and past the end of the cut: the products' borrowed parts are ranges in that bank, and
        /// whoever builds a final side later reads them from there. It goes back when the record stops holding the
        /// products, and only then, and only once.
        /// </summary>
        [Test]
        public void TheInputOutlivesTheSourceAndTheCut_AndGoesBackOnceWithTheProducts()
        {
            using (World w = NewWorld())
            {
                ProvisionalCutTransaction transaction = Publish(w);
                Assert.That(transaction.HoldsInput, Is.True, "the hold was taken when the cut was submitted");
                Assert.That(w.shape.WorkUsers, Is.EqualTo(1));

                // The source's own owner is retired while the cut is still running. Its shape is given up, but the
                // bank is not freed: this hold is what keeps it.
                RunUntil(w, 20, () => w.job.Accepted.Count > 0, "the cut reached its destination");
                Assert.That(w.registry.EndProvisional(transaction.Operation), Is.True);
                Assert.That(w.registry.Retire(w.source), Is.True);
                Assert.That(w.shape.IsFreed, Is.False, "the input's bank is still there");

                RunUntil(w, 30, () => transaction.Products != null || transaction.Phase == ProvisionalCutPhase.Recovered,
                    "the cut ends and its products are taken in");

                Assert.That(
                    transaction.Phase, Is.EqualTo(ProvisionalCutPhase.FinalHeld),
                    "the products are held for a handoff: " + transaction.CutOutcome);
                Assert.That(transaction.Products, Is.Not.Null);
                Assert.That(transaction.HoldsInput, Is.True, "and the input is still held for their borrowed parts");
                Assert.That(w.shape.IsFreed, Is.False);

                // A final side really can be built from them, which is the read the hold is for.
                PhysicsShapeSource productsSource = PhysicsShapeSource.For(transaction.Products);
                productsSource.Acquire();
                PhysicsOwnerShape side = PhysicsOwnerShape.OfSide(w.shape, transaction.Products, productsSource, true);
                Assert.That(side.ConvexCount, Is.GreaterThan(0), "a final side was built from the products");
                side.Dispose();
                productsSource.Release();

                // Only now, when the record lets the products go, does the hold go back -- once.
                Assert.That(w.driver.EndCut(transaction.Operation), Is.True);
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Recovered));
                Assert.That(transaction.HoldsInput, Is.False);
                Assert.That(transaction.Products, Is.Null, "the products went back with it");
                Assert.That(w.shape.WorkUsers, Is.Zero);
                Assert.That(w.driver.EndCut(transaction.Operation), Is.False, "ending it again does nothing");
                Assert.That(w.shape.WorkUsers, Is.Zero, "and nothing went back twice");
            }
        }

        /// <summary>
        /// Abandoning a cut whose work is already with the dispatcher is not the same as collecting it: the record
        /// keeps its input until that work comes back, and then everything goes back once.
        /// </summary>
        [Test]
        public void ACutAbandonedAfterItWasSubmitted_KeepsItsInputUntilTheWorkComesBack()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);
                w.driver.Advance(41);
                Assert.That(w.job.Accepted.Count, Is.GreaterThan(0), "the work is with the destination");

                Assert.That(w.driver.EndCut(transaction.Operation), Is.True, "the cut is ended while its work runs");
                Assert.That(
                    transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.Recovered),
                    "nothing was given back yet: the work has not come back");
                Assert.That(transaction.HoldsInput, Is.True, "the input is still held");
                Assert.That(w.shape.WorkUsers, Is.EqualTo(1));

                RunUntil(w, 42, () => transaction.Phase == ProvisionalCutPhase.Recovered, "the work comes back");
                Assert.That(transaction.HoldsInput, Is.False, "and then the input goes back");
                Assert.That(w.shape.WorkUsers, Is.Zero);
                Assert.That(w.driver.TransactionOf(transaction.Operation), Is.Null, "the record is done with");
            }
        }

        // ----- 4. refusals and failures -------------------------------------------------------------------------------

        /// <summary>
        /// A second ask for a fragment that already has an accepted cut is refused by the ledger, and nothing of the
        /// physics changes: the pair that is standing stays, no second pair is made, and the same ask can be made
        /// again once the first cut is out of the way.
        /// </summary>
        [Test]
        public void AnAskTheLedgerRefuses_ChangesNothing_AndCanBeAskedAgainLater()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction first = Publish(w);

                ProvisionalCutAsk again = Ask(w);
                ProvisionalCutAcceptance second = w.driver.RequestCut(
                    in again, out ProvisionalCutTransaction none, out LogicalCutAdmission admission);

                Assert.That(second, Is.EqualTo(ProvisionalCutAcceptance.InvalidRequest).Or.EqualTo(ProvisionalCutAcceptance.NotAccepted));
                Assert.That(none, Is.Null, "nothing is held for the refused ask");
                Assert.That(admission, Is.Not.EqualTo(LogicalCutAdmission.Admitted));
                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1), "the standing pair is untouched");
                Assert.That(first.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
                Assert.That(w.driver.Transactions.Count, Is.EqualTo(1), "and no second record was made");
            }
        }

        /// <summary>
        /// A stale cut -- its source's ownership authority moved after acceptance -- is refused by the publication, so
        /// no pair is published, nothing of the physics changed, and the driver holds nothing for it.
        /// </summary>
        [Test]
        public void ACutMadeStaleBeforePublication_PublishesNothing_AndHoldsNothing()
        {
            using (World w = NewWorld())
            {
                // The ask is made through the driver, and the authority moves inside it: the only seam is the
                // classification's own -- so instead the cut is made stale by asking for a second cut of a fragment
                // whose authority changed, which the publication refuses as Stale.
                w.ledger.NoteOwnershipChanged(w.source);
                ProvisionalCutAsk ask = Ask(w);
                ProvisionalCutAcceptance acceptance = w.driver.RequestCut(
                    in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission _);

                // An ownership note before acceptance is not staleness: the cut is admitted with the new authority and
                // publishes normally. This is the control for the case below.
                Assert.That(acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published));

                // Now it moves again, after this cut was accepted and published: the next ask of the same fragment is
                // refused, and the standing pair is not disturbed.
                w.ledger.NoteOwnershipChanged(w.source);
                ProvisionalCutAsk second = Ask(w);
                Assert.That(
                    w.driver.RequestCut(in second, out ProvisionalCutTransaction none, out LogicalCutAdmission _),
                    Is.Not.EqualTo(ProvisionalCutAcceptance.Published));
                Assert.That(none, Is.Null);
                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1));
            }
        }

        // ----- 5. teardown, endings, and exceptions -------------------------------------------------------------------

        /// <summary>
        /// Destroying the driver while a cut's work is still out **does not** release what that work is reading: the
        /// classification the kernel points at and the hold on the input stay, the record stays, and everything goes
        /// back once -- when the work comes back, which is whoever owns the cook and the frame's to carry.
        /// </summary>
        [Test]
        public void DestroyingTheDriverWithWorkOutstanding_ReleasesNothingUntilTheWorkComesBack()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);
                w.driver.Advance(101);
                Assert.That(w.job.Accepted.Count, Is.GreaterThan(0), "the work is with the destination");
                Assert.That(w.shape.WorkUsers, Is.EqualTo(1));

                // What the driver does when it goes away, which is what its OnDestroy calls. (In edit mode Unity does
                // not send that message, so the test calls the same entrance.)
                ProvisionalCutDriver driver = w.driver;
                ProvisionalCutRecovery recovery = driver.Recovery;
                w.driver = null;
                driver.EndEveryCut();
                UnityEngine.Object.DestroyImmediate(driver.gameObject);

                Assert.That(transaction.IsEnding, Is.True, "every cut was asked to end");
                Assert.That(
                    w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation ended)
                        && ended.state != LogicalCutOperationState.Admitted,
                    Is.True,
                    "and the ledger ended the cut, as an ordinary ending does: " + ended.state);
                Assert.That(
                    transaction.Phase, Is.Not.EqualTo(ProvisionalCutPhase.Recovered),
                    "but nothing was given back: its work has not come back");
                Assert.That(transaction.HoldsInput, Is.True, "the input the kernel borrows is still held");
                Assert.That(transaction.Classification, Is.Not.Null, "and so are the arrays it points at");
                Assert.That(w.shape.WorkUsers, Is.EqualTo(1));
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "while the published pair did leave the scene");

                Assert.That(
                    recovery.Count, Is.EqualTo(1),
                    "the record is with the recovery the frame carries, not with the driver that has gone");

                // Nothing of this test finishes the record: **the frame does**, because the recovery is one of its
                // participants and the frame is what whoever bound this driver goes on pumping.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds
                    && transaction.Phase != ProvisionalCutPhase.Recovered)
                {
                    w.job.ReleaseEverything();
                    w.frame.Update((int)clock.ElapsedMilliseconds + 200);
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(
                    transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Recovered),
                    "the record ended once its work came back");
                Assert.That(transaction.HoldsInput, Is.False, "and the hold went back");
                Assert.That(w.shape.WorkUsers, Is.Zero);
                Assert.That(recovery.Count, Is.Zero, "the recovery let it go");
                w.frame.Update(9999);
                Assert.That(w.shape.WorkUsers, Is.Zero, "and nothing went back twice");
            }
        }

        /// <summary>
        /// A cut whose final work produced nothing cannot be established, so it ends the whole way: the pair leaves the
        /// scene, the ledger ends the cut, the source is retired, and the record goes back -- not merely forgotten by
        /// the driver with the pair and the acceptance left standing.
        /// </summary>
        [Test]
        public void ACutWhoseWorkProducedNothing_EndsThePairTheLedgerAndTheSource()
        {
            using (World w = NewWorld())
            {
                ProvisionalCutTransaction transaction = Publish(w);

                // The work is taken out from under the cut: it comes back having made nothing.
                RunUntil(w, 120, () => transaction.Cut != null && transaction.Cut.Stage != PhysicsCutStage.Waiting,
                    "the cut is under way");
                Assert.That(w.cook.Abandon(transaction.Cut), Is.True, "the cut is given up");
                RunUntil(w, 130, () => transaction.Phase == ProvisionalCutPhase.Recovered, "it comes back with nothing");

                Assert.That(transaction.Products, Is.Null, "nothing was produced");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "the pair left the scene");
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state)
                        && state == LogicalFragmentState.Live,
                    Is.False,
                    "the ledger ended the cut and retired the source");
                Assert.That(w.registry.TryGet(w.source, out PhysicsFragmentOwner _), Is.False, "its owner with it");
                Assert.That(transaction.HoldsInput, Is.False, "and the hold went back");
                Assert.That(w.driver.TransactionOf(transaction.Operation), Is.Null);
            }
        }

        /// <summary>
        /// A cut whose source's authority moves **after it was accepted** is stale: the publication refuses it, nothing
        /// of the physics changed, the record ends, and **no fragment is retired** -- which is what tells a stale
        /// result apart from a physics failure.
        /// </summary>
        [Test]
        public void ACutMadeStaleAfterAcceptance_IsRefused_AndRetiresNothing()
        {
            using (World w = NewWorld())
            {
                // The authority moves between the acceptance and the publication, which is what staleness is. The
                // acceptance is reached through the driver, and the seam is the builder's own hook.
                ProvisionalOwnerBuilder.sideBuiltHook = positive =>
                {
                    if (!positive)
                    {
                        w.ledger.NoteOwnershipChanged(w.source);
                    }
                };

                try
                {
                    ProvisionalCutAsk ask = Ask(w);
                    Assert.That(
                        w.driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission),
                        Is.EqualTo(ProvisionalCutAcceptance.Stale),
                        "the ledger reclaimed it as stale");
                    Assert.That(admission, Is.EqualTo(LogicalCutAdmission.Admitted), "it had been accepted");
                    Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Recovered), "and its record ended");
                    Assert.That(transaction.HoldsInput, Is.False, "holding nothing");
                }
                finally
                {
                    ProvisionalOwnerBuilder.sideBuiltHook = null;
                }

                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "nothing was published");
                Assert.That(
                    w.ledger.TryGetFragmentState(w.source, out LogicalFragmentState state)
                        && state == LogicalFragmentState.Live,
                    Is.True,
                    "and the source is still live: a stale cut retires nothing");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "with its own physics untouched");
                Assert.That(w.shape.WorkUsers, Is.Zero, "and no hold was left behind");
            }
        }

        /// <summary>
        /// An exception raised while the pair is being built -- before anything of it is in the scene -- leaves the
        /// source where it was, publishes nothing, holds nothing, and is passed on.
        /// </summary>
        [Test]
        public void AnExceptionBeforeTheSwitch_GivesEverythingBackAndIsPassedOn()
        {
            using (World w = NewWorld())
            {
                ProvisionalOwnerBuilder.sideBuiltHook = positive =>
                {
                    if (positive)
                    {
                        throw new InvalidOperationException("while building the pair");
                    }
                };

                try
                {
                    ProvisionalCutAsk ask = Ask(w);
                    Assert.That(
                        () => w.driver.RequestCut(in ask, out ProvisionalCutTransaction _, out LogicalCutAdmission _),
                        Throws.InvalidOperationException);
                }
                finally
                {
                    ProvisionalOwnerBuilder.sideBuiltHook = null;
                }

                // 1. The source is untouched and nothing of the pair is anywhere.
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "nothing was published");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "the source never left the scene");
                Assert.That(w.shape.WorkUsers, Is.Zero, "nothing holds the input");

                // 3. The acceptance is still the ledger's, so the record that can end it is still the driver's. An
                // exception is not an infeasibility to declare, so the cut was not aborted for one.
                Assert.That(w.driver.Transactions.Count, Is.EqualTo(1), "the record is kept, for the ending");
                ProvisionalCutTransaction transaction = w.driver.Transactions[0];
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Unestablished));
                Assert.That(transaction.Classification, Is.Null, "with its scan given back");
                Assert.That(transaction.Candidate, Is.Null, "and the half-built pair too");
                Assert.That(
                    w.ledger.TryGetActiveOperation(w.source, out CutOperationId still) && still.Equals(transaction.Operation),
                    Is.True,
                    "the ledger still has it as the source's active operation");
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "holding a budget unit");

                // 2. And nothing retries it.
                w.driver.Advance(220);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "still nothing published");
                Assert.That(w.ledger.OperationCount, Is.EqualTo(1), "and no second cut was accepted");

                Assert.That(w.driver.EndCut(transaction.Operation), Is.True, "the ending entrance closes it");
                Assert.That(w.ledger.TryGetActiveOperation(w.source, out CutOperationId _), Is.False);
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "the budget unit came back");

                // 4. Twice is once.
                Assert.That(w.driver.EndCut(transaction.Operation), Is.False);
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero);
            }
        }

        /// <summary>
        /// An exception raised **after** the switch is not read as an unpublished failure: the pair stays in the scene
        /// and stays this driver's record, and the error is passed on as it is.
        /// </summary>
        [Test]
        public void AnExceptionAfterTheSwitch_KeepsThePublishedPairAndItsRecord_AndIsPassedOn()
        {
            using (World w = NewWorld())
            {
                w.job.HoldEverything = true;
                ProvisionalPhysicsPublication.publishedHook = () => throw new InvalidOperationException("after the switch");
                try
                {
                    ProvisionalCutAsk ask = Ask(w);
                    Assert.That(
                        () => w.driver.RequestCut(in ask, out ProvisionalCutTransaction _, out LogicalCutAdmission _),
                        Throws.InvalidOperationException);
                }
                finally
                {
                    ProvisionalPhysicsPublication.publishedHook = null;
                }

                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1), "the pair is in the scene");
                Assert.That(w.SourceOwner.IsWithdrawn, Is.True, "and the source has left it");

                // The record is still the driver's, and it is the pair's: that is what makes the pair endable.
                Assert.That(w.driver.Transactions.Count, Is.EqualTo(1), "the driver still holds a record for it");
                ProvisionalCutTransaction transaction = w.driver.Transactions[0];
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
                Assert.That(transaction.Pair, Is.Not.Null, "which names the published pair");
                Assert.That(
                    w.registry.TryGetProvisional(transaction.Operation, out ProvisionalOwnerPair standing), Is.True);
                Assert.That(ReferenceEquals(transaction.Pair, standing), Is.True, "the very pair in the scene");
                Assert.That(standing.IsStanding(true) && standing.IsStanding(false), Is.True, "both actors standing");

                // And the product's ending entrance can end it afterwards: the pair, the ledger and the resources.
                Assert.That(w.driver.EndCut(transaction.Operation), Is.True);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "the pair left the scene");
                Assert.That(
                    w.ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation ended)
                        && ended.state != LogicalCutOperationState.Admitted,
                    Is.True,
                    "the ledger ended the cut: " + ended.state);

                RunUntil(w, 300, () => transaction.Phase == ProvisionalCutPhase.Recovered, "its work comes back");
                Assert.That(transaction.HoldsInput, Is.False, "and everything it held went back");
                Assert.That(w.shape.WorkUsers, Is.Zero);
            }
        }

        /// <summary>
        /// A distribution the ledger **refuses** is not a wait: the same epsilon over the same anchors would be refused
        /// again, so nothing is kept back to retry. What this driver held goes back, no pair is published, and the
        /// refusal is reported as its own reason -- not as an abort, which DESIGN reserves for infeasibility.
        /// </summary>
        [Test]
        public void ADistributionTheLedgerRefuses_IsItsOwnReason_AndIsNotRetried()
        {
            // A plane steep enough that a far anchor's own distance overflows, while the shape's own vertices stay
            // finite and have support on both sides. Every value handed in is finite; the distribution the ledger
            // computes from them is not, and it refuses.
            using (World w = NewWorld(anchors: new[] { new float3(1e30f, 0f, 0f) }))
            {
                ProvisionalCutAsk ask = Ask(w);
                ask.plane = new float4(1e30f, 1f, 0f, 0f);
                Assert.That(
                    w.driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission),
                    Is.EqualTo(ProvisionalCutAcceptance.AnchorsRefused));
                Assert.That(admission, Is.EqualTo(LogicalCutAdmission.Admitted), "it had been accepted");

                // 1. The source is untouched and the resources this record made are back.
                Assert.That(w.SourceOwner.IsWithdrawn, Is.False, "the source keeps its own physics");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "nothing was published");
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Unestablished));
                Assert.That(transaction.Classification, Is.Null, "the scan went back");
                Assert.That(transaction.Candidate, Is.Null, "and there is no pair to give back");
                Assert.That(w.shape.WorkUsers, Is.Zero, "no hold was taken or left behind");

                // 2. Nothing retries it, however far the frame is carried.
                w.driver.Advance(210);
                w.driver.Advance(211);
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "still nothing published");
                Assert.That(w.ledger.OperationCount, Is.EqualTo(1), "and no second cut was accepted for it");

                // 3. The record is still the driver's, and it is what can end what the ledger still holds: the cut is
                // its source's active operation and it holds a unit of the incomplete budget.
                Assert.That(w.driver.Transactions.Count, Is.EqualTo(1), "the record is kept, for the ending");
                Assert.That(
                    ReferenceEquals(w.driver.TransactionOf(transaction.Operation), transaction), Is.True,
                    "and it is findable by its cut");
                Assert.That(
                    w.ledger.TryGetActiveOperation(w.source, out CutOperationId still) && still.Equals(transaction.Operation),
                    Is.True,
                    "the ledger still has it as the source's active operation");
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "holding a budget unit");

                Assert.That(w.driver.EndCut(transaction.Operation), Is.True, "the ending entrance closes it");
                Assert.That(
                    w.ledger.TryGetActiveOperation(w.source, out CutOperationId _), Is.False,
                    "the source has no active operation any more");
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the budget unit came back");
                Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Recovered));

                // 4. And a second ending does nothing at all.
                Assert.That(w.driver.EndCut(transaction.Operation), Is.False, "ending it again does nothing");
                Assert.That(w.ledger.Budget.IncompleteCutOperationCount, Is.Zero, "nothing came back twice");
                Assert.That(w.shape.WorkUsers, Is.Zero);
            }
        }

        // ----- 6. the frame's budget ----------------------------------------------------------------------------------

        /// <summary>
        /// Going round again inside the same frame does not refill the frame's budget, and no update waits for a work
        /// that has not finished: it comes back with the collection still outstanding.
        /// </summary>
        [Test]
        public void AdvancingTwiceInOneFrame_DoesNotRefillTheBudget_AndWaitsForNothing()
        {
            using (World w = NewWorld(frameBudget: 1))
            {
                w.job.HoldEverything = true;
                ProvisionalCutTransaction transaction = Publish(w);

                w.driver.Advance(77);
                int acceptedAfterFirst = w.job.Accepted.Count;
                Assert.That(acceptedAfterFirst, Is.EqualTo(1), "one unit of budget went on one submission");
                Assert.That(w.dispatcher.RemainingBudget, Is.Zero, "the frame's budget is spent");

                w.driver.Advance(77);
                Assert.That(
                    w.job.Accepted.Count, Is.EqualTo(acceptedAfterFirst),
                    "going round again in the same frame buys nothing");
                Assert.That(w.dispatcher.RemainingBudget, Is.Zero);
                Assert.That(transaction.Cut.IsOver, Is.False, "and nothing waited for the work that is still held");

                w.driver.Advance(78);
                Assert.That(w.dispatcher.RemainingBudget, Is.GreaterThanOrEqualTo(0), "a new frame refills it");
            }
        }

        // ----- 7. the update route, with the real display entrance ----------------------------------------------------

        /// <summary>
        /// **T-091 through the product's own phases.** An ask made outside the update is taken up by what
        /// <c>Update</c> calls, and in that one call the cut is classified, accepted, built and published; what
        /// <c>LateUpdate</c> calls then collects through the **real display**, in the same frame, with the final cut
        /// still unfinished.
        /// <para>
        /// The two entrances are the ones Unity calls (<c>Update</c> and <c>LateUpdate</c> call nothing else), and the
        /// frame is the display's own counter, so "the same frame" is the display's own answer and not this test's.
        /// </para>
        /// </summary>
        [Test]
        public void AnAskTakenUpInTheUpdate_IsPublishedAndCollectedInThatFrame()
        {
            using (World w = NewWorld())
            using (var storage = new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent))
            {
                w.job.HoldEverything = true;

                // The real display, shown the source's geometry and following the physics owners.
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry geometry = AppendCube(storage);
                Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
                var material = new Material(shader) { name = "provisional body", hideFlags = HideFlags.HideAndDontSave };
                _materials.Add(material);
                int frame = 7;
                int FrameSource() => frame;
                Assert.That(
                    VpLogicalCutDisplay.TryCreate(
                        storage, table, w.ledger, new Dictionary<int, Material> { { 0, material } }, null, null, 16, 16,
                        VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates,
                        VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(), FrameSource,
                        out VpLogicalCutDisplay display),
                    Is.True,
                    "the display was created");
                using (display)
                {
                    display.Placement = w.lookup;
                    Assert.That(
                        display.TryShow(w.source, geometry, w.root.transform.localToWorldMatrix * k_geometryLocalToOwner),
                        Is.True,
                        "the source's geometry is shown");
                    // **The same frame source as the display's.** Otherwise "the same frame" would be two numbers:
                    // the display's own counter and the engine's.
                    w.driver.Bind(
                        w.ledger, w.registry, w.cook, w.frame, display, SupportEpsilon, AnchorEpsilon, VertexLimit,
                        FrameSource);

                    // The ask is made outside the update, as a weapon in its own phase would.
                    ProvisionalCutAsk ask = Ask(w);
                    w.driver.Ask(in ask);
                    Assert.That(w.registry.ProvisionalPairCount, Is.Zero, "and nothing has happened yet");

                    // What Update calls. Everything from the classification to the publication is in here.
                    int frameAtUpdate = FrameSource();
                    w.driver.DriveUpdate();

                    Assert.That(
                        w.driver.LastTaken, Is.EqualTo(new[] { ProvisionalCutAcceptance.Published }),
                        "the ask was taken up and published in that update");
                    Assert.That(w.driver.Transactions.Count, Is.EqualTo(1));
                    ProvisionalCutTransaction transaction = w.driver.Transactions[0];
                    Assert.That(
                        transaction.PublishedFrame, Is.EqualTo(frameAtUpdate),
                        "in the very frame the update ran in, counted by the display's own source");
                    Assert.That(transaction.Cut, Is.Not.Null, "the final cut was submitted");
                    Assert.That(transaction.Cut.IsOver, Is.False, "and is not finished: nothing waited for it");
                    Assert.That(transaction.Products, Is.Null);

                    // What LateUpdate calls: the display collects, after the publication, in the same frame.
                    Assert.That(w.driver.DriveLateUpdate(), Is.True, "the display opened this frame");
                    Assert.That(
                        display.SideCount, Is.EqualTo(2),
                        "and it collected the two sides of the accepted cut: " + display.SideCount);

                    var sides = new List<float>();
                    for (int i = 0; i < display.SideCount; i++)
                    {
                        Assert.That(display.TryGetSide(i, out LogicalCutDisplaySide side), Is.True);
                        Assert.That(side.operation, Is.EqualTo(transaction.Operation), "each is a side of this cut");
                        Assert.That(side.published, Is.False, "which has published no child");
                        sides.Add(side.side);
                    }

                    Assert.That(sides, Is.EquivalentTo(new[] { 1f, -1f }), "one side each way");
                }
            }
        }

        /// <summary>One cube in the storage, for the display to be shown. Wound outward.</summary>
        private static VpStoredGeometry AppendCube(VpCpuGeometryStorage storage)
        {
            var corners = new[]
            {
                new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, 1f, -1f), new float3(-1f, 1f, -1f),
                new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
            };
            var faces = new[]
            {
                new[] { 0, 3, 2, 1 }, new[] { 4, 5, 6, 7 }, new[] { 0, 1, 5, 4 },
                new[] { 2, 3, 7, 6 }, new[] { 1, 2, 6, 5 }, new[] { 0, 4, 7, 3 },
            };

            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            foreach (int[] face in faces)
            {
                float3 normal = math.normalize(math.cross(
                    corners[face[1]] - corners[face[0]], corners[face[2]] - corners[face[0]]));
                uint b = (uint)vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new VpRenderVertex
                    {
                        position = corners[face[k]], normal = normal, uv0 = new float2(0.5f, 0.5f),
                    });
                    topology.Add(face[k]);
                }

                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), corners.Length,
                    new[] { new VpGeometrySubmesh(0, indices.Count, 0) }, out VpStoredGeometry geometry),
                Is.True,
                "append the cube");
            return geometry;
        }

        // ----- helpers -------------------------------------------------------------------------------------------------

        private static IComparer<Vector3> Vector3Within(float tolerance)
        {
            return new Vector3Comparer(tolerance);
        }

        private sealed class Vector3Comparer : IComparer<Vector3>
        {
            private readonly float _tolerance;

            internal Vector3Comparer(float tolerance)
            {
                _tolerance = tolerance;
            }

            public int Compare(Vector3 x, Vector3 y)
            {
                return (x - y).magnitude <= _tolerance ? 0 : 1;
            }
        }

        private static IComparer<float3> Float3Within(float tolerance)
        {
            return new Float3Comparer(tolerance);
        }

        private sealed class Float3Comparer : IComparer<float3>
        {
            private readonly float _tolerance;

            internal Float3Comparer(float tolerance)
            {
                _tolerance = tolerance;
            }

            public int Compare(float3 x, float3 y)
            {
                return math.all(math.abs(x - y) <= _tolerance) ? 0 : 1;
            }
        }
    }
}
