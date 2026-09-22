using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The Provisional to Final handoff **while the game is playing**, where destruction is deferred (DESIGN 7.2).
    /// <para>
    /// The edit-mode tests of this path destroy immediately, so they cannot say anything about what the scene holds
    /// between the switch and the end of the frame. What is watched here is only that: at the moment the handoff
    /// returns, the old colliders are still there — <c>Destroy</c> has only been asked for — and **none of them is
    /// answering any more**, while the final ones are; and once the frame is over the actors are still standing, with
    /// the old colliders and the sibling constraint really gone.
    /// </para>
    /// <para>
    /// **How the frame is driven.** The driver's own <c>Update</c> is switched off and the test carries the frame
    /// through <see cref="ProvisionalCutDriver.Advance"/> — the entrance that update calls — so that the handoff
    /// happens inside a call the test can look at the other side of, within the same frame.
    /// </para>
    /// <para>
    /// **What this does not say.** The simulation is not stepped, nothing is measured, and the handoff's own rules —
    /// what is kept, what is published, what goes back — are the edit-mode tests'.
    /// </para>
    /// </summary>
    public unsafe class FinalHandoffPlayModeTests
    {
        private const double ParentMass = 12.0;
        private const float DeadlineSeconds = 30f;

        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private NativeArray<float3> _vertices;
        private NativeArray<int> _faceOffsets;
        private NativeArray<int> _faceIndices;
        private NativeArray<int> _faceEdges;
        private NativeArray<BrepEdge> _edges;
        private PhysicsOwnerRegistry _registry;
        private PhysicsShapeSource _meshSource;
        private PhysicsCutCook _cook;
        private SharedWorkDispatcher _dispatcher;
        private WorkerPoolExecutor _geometry;
        private WorkerPoolExecutor _background;

        [TearDown]
        public void Cleanup()
        {
            _registry?.Dispose();
            _registry = null;
            _cook?.Dispose();
            _dispatcher?.Shutdown(5000);
            _cook?.Pump();
            _cook = null;
            _dispatcher = null;
            _geometry?.Dispose();
            _background?.Dispose();
            _geometry = null;
            _background = null;
            foreach (GameObject go in _objects)
            {
                if (go != null)
                {
                    Object.Destroy(go);
                }
            }

            _objects.Clear();
            foreach (Mesh mesh in _meshes)
            {
                if (mesh != null)
                {
                    Object.Destroy(mesh);
                }
            }

            _meshes.Clear();
            Free(ref _vertices);
            Free(ref _faceOffsets);
            Free(ref _faceIndices);
            Free(ref _faceEdges);
            Free(ref _edges);
        }

        /// <summary>
        /// At the switch: only the final colliders answer, although the old ones are still waiting to be destroyed.
        /// After the frame: the same actors are standing, with nothing of the Provisional configuration left on them.
        /// </summary>
        [UnityTest]
        public IEnumerator TheHandoffInPlayMode_StopsTheOldShapeAtOnce_AndLeavesTheActorsStanding()
        {
            _registry = new PhysicsOwnerRegistry();
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));

            PhysicsOwnerShape shape = NewAuthoredShape(out Mesh _);
            GameObject sourceRoot = NewSourceObject(shape);
            LogicalFragmentId source = ledger.AddFragment();
            _registry.RegisterAuthored(
                source, sourceRoot, sourceRoot.GetComponent<Rigidbody>(), shape, false, Matrix4x4.identity);

            _geometry = WorkerPoolExecutor.GeometryPool(2);
            _background = WorkerPoolExecutor.BackgroundPool(2);
            _dispatcher = new SharedWorkDispatcher(8, 2, 32, new UnityJobWorkExecutor(4), _geometry, _background);
            _cook = new PhysicsCutCook(_dispatcher, 1);
            var frame = new SharedWorkFrame(_dispatcher);
            frame.Add(_cook);

            var driverObject = new GameObject("Provisional Cut Driver");
            _objects.Add(driverObject);
            ProvisionalCutDriver driver = driverObject.AddComponent<ProvisionalCutDriver>();
            driver.Bind(ledger, _registry, _cook, frame, null, 1e-4f, 1e-5f, 4096);

            // The frame is the test's to carry, so that the handoff happens inside a call it can look at.
            driver.enabled = false;

            var ask = new ProvisionalCutAsk
            {
                source = source,
                plane = new float4(0f, 1f, 0f, 0f),
                renderAnchor = float3.zero,
            };
            Assert.That(
                driver.RequestCut(in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission _),
                Is.EqualTo(ProvisionalCutAcceptance.Published),
                "the pair was published in the update the cut was asked in");

            PhysicsOwnerSide positive = transaction.Pair.Positive;
            PhysicsOwnerSide negative = transaction.Pair.Negative;
            GameObject positiveRoot = positive.Root;
            GameObject negativeRoot = negative.Root;
            Rigidbody positiveBody = positive.Body;
            Rigidbody negativeBody = negative.Body;
            ConfigurableJoint joint = transaction.Pair.Separation;
            var oldColliders = new List<MeshCollider>(positive.Colliders);
            oldColliders.AddRange(negative.Colliders);
            Assert.That(oldColliders.Count, Is.GreaterThan(0), "the pair stands on colliders of its own");

            float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
            while (transaction.Phase != ProvisionalCutPhase.HandedOff && Time.realtimeSinceStartup < deadline)
            {
                driver.Advance(Time.frameCount);
                if (transaction.Phase == ProvisionalCutPhase.HandedOff)
                {
                    break;
                }

                yield return null;
            }

            Assert.That(
                transaction.Phase, Is.EqualTo(ProvisionalCutPhase.HandedOff),
                "the handoff happened, in a call this test made");

            // Still inside that frame: the old colliders have only been asked to go, and not one of them answers.
            foreach (MeshCollider old in oldColliders)
            {
                Assert.That(
                    old == null || !old.enabled, Is.True,
                    "an old collider was left answering after the switch: " + old);
            }

            foreach (PhysicsOwnerSide side in new[] { positive, negative })
            {
                Assert.That(side.Colliders.Count, Is.GreaterThan(0), "the actor has its final colliders");
                foreach (MeshCollider collider in side.Colliders)
                {
                    Assert.That(collider != null && collider.enabled, Is.True, "and they are the ones answering");
                    Assert.That(oldColliders.Contains(collider), Is.False, "none of them is one of the old ones");
                }

                Assert.That(side.Root.activeInHierarchy, Is.True, "the actor is in the scene throughout");

                // The Provisional publication declared these explicit before the actor entered the scene; the
                // handoff writes the final mass and motion onto the same actor and must not hand it back to
                // automatic ones.
                Assert.That(
                    side.Body.automaticCenterOfMass, Is.False,
                    "the actor's centre of mass is still given, not computed, after the handoff");
                Assert.That(
                    side.Body.automaticInertiaTensor, Is.False, "and so is its inertia");
                Assert.That(
                    side.Body.mass, Is.EqualTo((float)side.Mass).Within(1e-3f),
                    "and it holds the mass the final shape decided");

                // The inertia as a tensor, so the quaternion's sign and the order of its axes cannot hide a
                // difference between what the final shape decided and what the body ended up with.
                float3x3 rotation = new float3x3(side.Body.inertiaTensorRotation);
                var held = float3x3.zero;
                held.c0.x = side.Body.inertiaTensor.x;
                held.c1.y = side.Body.inertiaTensor.y;
                held.c2.z = side.Body.inertiaTensor.z;
                float3x3 heldTensor = math.mul(math.mul(rotation, held), math.transpose(rotation));
                float3x3 wanted = new float3x3(side.InertiaRotation);
                var diagonal = float3x3.zero;
                diagonal.c0.x = side.InertiaTensor.x;
                diagonal.c1.y = side.InertiaTensor.y;
                diagonal.c2.z = side.InertiaTensor.z;
                float3x3 wantedTensor = math.mul(math.mul(wanted, diagonal), math.transpose(wanted));
                Assert.That(
                    math.max(
                        math.max(math.length(heldTensor.c0 - wantedTensor.c0), math.length(heldTensor.c1 - wantedTensor.c1)),
                        math.length(heldTensor.c2 - wantedTensor.c2)),
                    Is.LessThan(1e-2f),
                    "and the inertia the final shape decided, principal frame included");
            }

            yield return null;

            Assert.That(positiveRoot != null && positiveRoot.activeInHierarchy, Is.True, "the actors are still standing");
            Assert.That(negativeRoot != null && negativeRoot.activeInHierarchy, Is.True);
            foreach (MeshCollider old in oldColliders)
            {
                Assert.That(old == null, Is.True, "the old colliders were destroyed once the frame was over");
            }

            Assert.That(joint == null, Is.True, "and so was the sibling constraint");
            Assert.That(
                positive.ShapeFrame.GetComponents<MeshCollider>().Length, Is.EqualTo(positive.Colliders.Count),
                "nothing of the Provisional configuration is left on the actor");

            Assert.That(ledger.TryGetOperation(transaction.Operation, out LogicalCutOperation published), Is.True);
            Assert.That(
                _registry.TryResolveFragment(positiveBody, out LogicalFragmentId positiveChild, out float side0),
                Is.True,
                "each actor resolves to the child it became");
            Assert.That(positiveChild, Is.EqualTo(published.positive));
            Assert.That(side0, Is.EqualTo(0f));
            Assert.That(
                _registry.TryResolveFragment(negativeBody, out LogicalFragmentId negativeChild, out float _), Is.True);
            Assert.That(negativeChild, Is.EqualTo(published.negative));
            Assert.That(sourceRoot == null, Is.True, "and the source's own object went with its retirement");
        }

        // ----- one authored box, made without the editor-only harness -------------------------------------------------

        private PhysicsOwnerShape NewAuthoredShape(out Mesh mesh)
        {
            var corners = new[]
            {
                new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, 1f, -1f), new float3(-1f, 1f, -1f),
                new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
            };

            var faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var faceIndices = new[]
            {
                0, 3, 2, 1, 4, 5, 6, 7, 0, 1, 5, 4,
                2, 3, 7, 6, 1, 2, 6, 5, 0, 4, 7, 3,
            };

            // The edge table and each face's edges, from the loops themselves: this box is really cut here, and a
            // kernel given no adjacency produces nothing at all.
            BuildEdges(faceOffsets, faceIndices, out int[] faceEdges, out BrepEdge[] edges);

            _vertices = new NativeArray<float3>(corners, Allocator.Persistent);
            _faceOffsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
            _faceIndices = new NativeArray<int>(faceIndices, Allocator.Persistent);
            _faceEdges = new NativeArray<int>(faceEdges, Allocator.Persistent);
            _edges = new NativeArray<BrepEdge>(edges, Allocator.Persistent);

            var bank = new ConvexBrepBank
            {
                vertices = (float3*)_vertices.GetUnsafePtr(),
                faceOffsets = (int*)_faceOffsets.GetUnsafePtr(),
                faceIndices = (int*)_faceIndices.GetUnsafePtr(),
                faceEdges = (int*)_faceEdges.GetUnsafePtr(),
                edges = (BrepEdge*)_edges.GetUnsafePtr(),
            };

            var range = new ConvexBrepRange
            {
                vertexBase = 0, vertexCount = 8,
                faceBase = 0, faceCount = 6,
                faceIndexBase = 0, faceIndexCount = faceIndices.Length,
                edgeBase = 0, edgeCount = edges.Length,
                maxFaceLoop = 4,
            };

            mesh = NewBoxMesh();
            _meshSource = PhysicsShapeSource.External();
            return PhysicsOwnerShape.Authored(
                bank, new[] { range }, new List<Mesh> { mesh }, _meshSource, float4x4.identity);
        }

        private Mesh NewBoxMesh()
        {
            var mesh = new Mesh { name = "Authored", hideFlags = HideFlags.HideAndDontSave };
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
            _meshes.Add(mesh);
            return mesh;
        }

        private GameObject NewSourceObject(PhysicsOwnerShape shape)
        {
            var root = new GameObject("Authored Source");
            _objects.Add(root);
            var body = root.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = root.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            return root;
        }

        /// <summary>
        /// The undirected edges of one closed convex, and the edge each corner of each face loop belongs to, by the
        /// convention the rest of the code uses: <c>v0</c> is the lower vertex index, <c>f0</c> the face that
        /// traverses the edge from it and <c>f1</c> the face that traverses it the other way.
        /// </summary>
        private static void BuildEdges(int[] faceOffsets, int[] faceIndices, out int[] faceEdges, out BrepEdge[] edges)
        {
            var found = new Dictionary<long, int>();
            var table = new List<BrepEdge>();
            faceEdges = new int[faceIndices.Length];
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f];
                int count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = faceIndices[from + k];
                    int b = faceIndices[from + ((k + 1) % count)];
                    int lo = math.min(a, b);
                    int hi = math.max(a, b);
                    long key = ((long)lo << 32) | (uint)hi;
                    if (!found.TryGetValue(key, out int at))
                    {
                        at = table.Count;
                        found.Add(key, at);
                        table.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 });
                    }

                    BrepEdge edge = table[at];
                    if (a == lo)
                    {
                        if (edge.f0 < 0)
                        {
                            edge.f0 = f;
                        }
                    }
                    else if (edge.f1 < 0)
                    {
                        edge.f1 = f;
                    }

                    table[at] = edge;
                    faceEdges[from + k] = at;
                }
            }

            edges = table.ToArray();
            foreach (BrepEdge edge in edges)
            {
                Assert.That(edge.f0, Is.GreaterThanOrEqualTo(0), "the box is closed: every edge has two faces");
                Assert.That(edge.f1, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static void Free<T>(ref NativeArray<T> array)
            where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }
        }
    }
}
