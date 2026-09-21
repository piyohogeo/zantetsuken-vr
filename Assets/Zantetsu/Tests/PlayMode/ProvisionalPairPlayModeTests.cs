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
using Zantetsu.PhysicsCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Ending a published Provisional pair **while the game is playing**, where destruction is deferred
    /// (DESIGN 7.1.1).
    /// <para>
    /// The edit-mode tests of this path destroy immediately, so they cannot say anything about the deferred order.
    /// What is watched here is only that: the objects are out of the scene and out of the correspondence at the moment
    /// the pair ends, they are really gone once the frame that ended them is over, and the meshes the pair shared with
    /// its source outlive the source's own retirement.
    /// </para>
    /// <para>
    /// **What this does not say.** No cut is run and nothing is cooked: the source's collider meshes come from outside
    /// (<see cref="PhysicsShapeSource.External"/>), so they are nobody's here to give back and their destruction is
    /// not part of this. There is no product caller for this path, so nothing here says anything about frame timing
    /// (T-091), and the simulation is not stepped.
    /// </para>
    /// </summary>
    public unsafe class ProvisionalPairPlayModeTests
    {
        private const double ParentMass = 12.0;

        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Mesh> _meshes = new List<Mesh>();
        private NativeArray<float3> _vertices;
        private NativeArray<int> _faceOffsets;
        private NativeArray<int> _faceIndices;
        private NativeArray<int> _faceEdges;
        private NativeArray<BrepEdge> _edges;
        private PhysicsOwnerRegistry _registry;
        private PhysicsShapeSource _meshSource;

        [TearDown]
        public void Cleanup()
        {
            _registry?.Dispose();
            _registry = null;
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
        /// A published pair, its source retired first, then ended: nothing of it is in the scene or in the
        /// correspondence at that moment, and once the frame is over the objects are really gone. The meshes the pair
        /// shared with the source are alive throughout — retiring the source does not take away what the pair is
        /// using.
        /// </summary>
        [UnityTest]
        public IEnumerator EndingAPublishedPairInPlayMode_LeavesNothingBehind()
        {
            _registry = new PhysicsOwnerRegistry();
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));

            PhysicsOwnerShape shape = NewAuthoredShape(out Mesh authoredMesh);
            GameObject sourceRoot = NewSourceObject(shape);
            LogicalFragmentId source = ledger.AddFragment();
            _registry.RegisterAuthored(source, sourceRoot, sourceRoot.GetComponent<Rigidbody>(), shape, false, Matrix4x4.identity);

            var plane = new float4(0f, 1f, 0f, 0f);
            Assert.That(ledger.Admit(source, plane, true, out CutOperationId operation), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(
                ledger.PrepareAnchorDistribution(operation, 1e-5f, out AnchorDistributionResult anchors),
                Is.EqualTo(AnchorPreparationOutcome.Prepared));

            var build = new ProvisionalOwnerBuildInput
            {
                sourceShape = shape,
                sides = new[] { ConvexSide.Split },
                planeLocal = plane,
                placement = PhysicsOwnerPlacement.Identity,
                sourceMotion = default,
                anchors = anchors,
                parentMass = ParentMass,
                sourceInertia = new float3(4f, 4f, 4f),
                sourceInertiaRotation = quaternion.identity,
                cooking = PhysicsCutCook.DefaultCooking,
                name = "Provisional",
            };
            Assert.That(
                ProvisionalOwnerBuilder.TryBuild(
                    in build, out ProvisionalOwnerCandidate candidate, out PhysicsOwnerBuildOutcome built),
                Is.True,
                "the pair was built: " + built);

            var publication = new ProvisionalPhysicsPublicationInput
            {
                ledger = ledger,
                registry = _registry,
                operation = operation,
                source = source,
                candidate = candidate,
                builtFrom = shape,
                renderAnchor = float3.zero,
            };
            Assert.That(
                ProvisionalPhysicsPublication.TryPublish(
                    in publication, out ProvisionalOwnerPair pair, out LogicalCutResultOutcome _),
                Is.EqualTo(PhysicsPublicationOutcome.Published));

            Rigidbody positiveBody = pair.Positive.Body;
            Rigidbody negativeBody = pair.Negative.Body;
            GameObject positiveRoot = pair.Positive.Root;
            GameObject negativeRoot = pair.Negative.Root;
            int heldWhilePublished = _meshSource.Users;

            // The source is retired first, while the pair is standing. Its own object goes at the end of this frame;
            // what the pair is using does not go with it.
            Assert.That(_registry.Retire(source), Is.True);
            yield return null;

            Assert.That(sourceRoot == null, Is.True, "the source's object was destroyed once the frame was over");
            Assert.That(authoredMesh != null, Is.True, "the meshes the pair shares are still there");
            Assert.That(
                _meshSource.Users, Is.LessThan(heldWhilePublished), "the source's own hold went back with it");
            Assert.That(_meshSource.Users, Is.GreaterThan(0), "and the pair still holds what it is using");
            Assert.That(pair.IsStanding(true) && pair.IsStanding(false), Is.True, "the pair is still in the scene");

            // Now the pair. At this moment it is out of the scene and out of the correspondence; the objects
            // themselves are still there, because destruction in play mode happens once the frame is over.
            Assert.That(_registry.EndProvisional(operation), Is.True);
            Assert.That(pair.IsEnded, Is.True);
            Assert.That(_registry.ProvisionalPairCount, Is.Zero, "the cut has no pair");
            Assert.That(_registry.TryGetProvisionalOf(source, out ProvisionalOwnerPair _), Is.False, "nor the source");
            Assert.That(
                _registry.TryResolveSource(positiveBody, out LogicalFragmentId _, out float _), Is.False,
                "and neither body resolves to anything any more");
            Assert.That(_registry.TryResolveSource(negativeBody, out LogicalFragmentId _, out float _), Is.False);
            Assert.That(positiveRoot.activeInHierarchy, Is.False, "both objects have left the scene");
            Assert.That(negativeRoot.activeInHierarchy, Is.False);
            Assert.That(_meshSource.Users, Is.Zero, "and the pair's holds went back");

            yield return null;

            Assert.That(positiveRoot == null, Is.True, "once the frame was over, the positive actor was destroyed");
            Assert.That(negativeRoot == null, Is.True, "and the negative one");
            Assert.That(
                authoredMesh != null, Is.True,
                "the meshes came from outside and are nobody's here to give back");

            Assert.That(_registry.EndProvisional(operation), Is.False, "ending it again does nothing");
            candidate.Dispose();
            yield return null;
            Assert.That(authoredMesh != null, Is.True, "and nothing of the meshes went back twice");
        }

        // ----- one authored box, made without the editor-only harness -------------------------------------------------

        private PhysicsOwnerShape NewAuthoredShape(out Mesh mesh)
        {
            var corners = new[]
            {
                new float3(-1f, -1f, -1f), new float3(1f, -1f, -1f), new float3(1f, 1f, -1f), new float3(-1f, 1f, -1f),
                new float3(-1f, -1f, 1f), new float3(1f, -1f, 1f), new float3(1f, 1f, 1f), new float3(-1f, 1f, 1f),
            };

            _vertices = new NativeArray<float3>(corners, Allocator.Persistent);
            _faceOffsets = new NativeArray<int>(new[] { 0, 4, 8, 12, 16, 20, 24 }, Allocator.Persistent);
            _faceIndices = new NativeArray<int>(
                new[]
                {
                    0, 3, 2, 1, 4, 5, 6, 7, 0, 1, 5, 4,
                    2, 3, 7, 6, 1, 2, 6, 5, 0, 4, 7, 3,
                },
                Allocator.Persistent);
            _faceEdges = new NativeArray<int>(24, Allocator.Persistent);
            _edges = new NativeArray<BrepEdge>(12, Allocator.Persistent);

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
                faceIndexBase = 0, faceIndexCount = 24,
                edgeBase = 0, edgeCount = 12,
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
