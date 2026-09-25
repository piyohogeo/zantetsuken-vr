// The play-mode half of `ProvisionalMassFlagActivationTests`: the two automatic mass properties are declared
// while each Provisional side is out of the scene, and are read back straight after that side is activated,
// before anything writes them again. The negative side is read once more after the side carrying the sibling
// joint has entered.
//
// It runs where the game runs, which is the same ground the published contract is checked on
// (`ProvisionalPublishedContractPlayModeTests`). It says nothing about time, and nothing about whether an
// inertia is recomputed internally.
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public unsafe class ProvisionalMassFlagActivationPlayModeTests
    {
        private const double ParentMass = 12.0;

        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<System.IDisposable> _disposables = new List<System.IDisposable>();
        private NativeArray<float3> _vertices;
        private NativeArray<int> _faceOffsets;
        private NativeArray<int> _faceIndices;
        private NativeArray<int> _faceEdges;
        private NativeArray<BrepEdge> _edges;
        private PhysicsShapeSource _meshSource;

        [UnityTest]
        public IEnumerator D5_ColdPreparation_InactiveBeforeReturn_HoldsMeshUntilDeferredObjectsDisappear()
        {
            var source=NewAuthoredShape(1);
            var input=new VpPreparedPhysicsInput(source.BankOf(0),new[]{source.Convex(0)});_disposables.Add(input);
            var warm=new VpPhysicsColdPreparation();warm.Prepare(input);
            var roots=Resources.FindObjectsOfTypeAll<GameObject>().Where(g=>g.name.StartsWith("Cold physics preparation ")).ToArray();
            Assert.That(roots.Length,Is.EqualTo(2));
            var colliders=roots.SelectMany(r=>r.GetComponentsInChildren<MeshCollider>(true)).ToArray();
            Assert.That(colliders.Length,Is.EqualTo(1));
            foreach(var root in roots)Assert.That(root.activeInHierarchy,Is.False);
            var borrowedMesh=colliders[0].sharedMesh;Assert.That(borrowedMesh,Is.Not.Null);
            Assert.That(warm.IsPrepared,Is.False);Assert.That(warm.TryFinish(),Is.False);
            Assert.Throws<System.InvalidOperationException>(()=>warm.Dispose());
            Assert.That(colliders[0].gameObject.activeInHierarchy,Is.False);
            Physics.SyncTransforms();
            Assert.That(Physics.OverlapBox(Vector3.zero,Vector3.one*20).Any(c=>colliders.Contains(c)),Is.False);
            warm.Prepare(input);Assert.That(input.IsPosed,Is.False);Assert.That(input.ColdCookCalls,Is.EqualTo(1));
            Assert.That(input.TryPose(new[]{float4x4.identity},out _),Is.True);
            input.Dispose();
            Assert.That(borrowedMesh!=null,Is.True,"temporary Collider must retain its Mesh independently of the input");
            yield return null;
            foreach(var root in roots)Assert.That(root==null,Is.True);
            Assert.That(colliders[0]==null,Is.True);Assert.That(borrowedMesh!=null,Is.True);
            Assert.That(warm.TryFinish(),Is.True);Assert.That(warm.IsPrepared,Is.True);warm.Dispose();
            yield return null;
            Assert.That(borrowedMesh==null,Is.True,"last cold hold releases the unregistered input Mesh");
        }

        [TearDown]
        public void Cleanup()
        {
            for (int i = _disposables.Count - 1; i >= 0; i--)
            {
                _disposables[i].Dispose();
            }

            _disposables.Clear();
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
        /// The two flags are set while both sides are out of the scene, and are read back **straight after each side
        /// is activated**, in the product's order, before anything re-sets them. The negative side is read again once
        /// the positive side (the one carrying the joint) has entered.
        /// </summary>
        [UnityTest]
        public IEnumerator MassFlagsSetWhileInactive_AreStillSetStraightAfterActivation_BothSidesFree()
        {
            yield return null;
            RunCheck(false, 1);
        }

        [UnityTest]
        public IEnumerator MassFlagsSetWhileInactive_AreStillSetStraightAfterActivation_OneSideFixed()
        {
            yield return null;
            RunCheck(true, 1);
        }

        [UnityTest]
        public IEnumerator MassFlagsSetWhileInactive_AreStillSetStraightAfterActivation_BothSidesFree_ThreeColliders()
        {
            yield return null;
            RunCheck(false, 3);
        }

        [UnityTest]
        public IEnumerator MassFlagsSetWhileInactive_AreStillSetStraightAfterActivation_OneSideFixed_ThreeColliders()
        {
            yield return null;
            RunCheck(true, 3);
        }

        private void RunCheck(bool withAnchor, int convexCount)
        {
            ProvisionalOwnerCandidate candidate =
                NewCandidate(withAnchor, convexCount, out bool negativeFixed, out bool positiveFixed);
            PhysicsOwnerSide negative = candidate.Negative;
            PhysicsOwnerSide positive = candidate.Positive;

            Assert.That(withAnchor ? (negativeFixed || positiveFixed) : (!negativeFixed && !positiveFixed), Is.True,
                withAnchor ? "the anchored configuration fixes a side" : "the free configuration fixes neither");
            Assert.That(
                negative.Colliders.Count, Is.EqualTo(convexCount),
                "the negative side has one collider per convex it keeps");
            Assert.That(
                positive.Colliders.Count, Is.EqualTo(convexCount),
                "the positive side has one collider per convex it keeps");

            // ---- 1. out of the scene, and told that the mass properties are given -------------------------------
            Assert.That(negative.Root.activeInHierarchy, Is.False, "the negative side starts out of the scene");
            Assert.That(positive.Root.activeInHierarchy, Is.False, "and so does the positive one");
            negative.DeclareMassPropertiesExplicit();
            positive.DeclareMassPropertiesExplicit();

            // ---- 2. both flags read false while still out of the scene ------------------------------------------
            AssertFlags(negative, "the negative side, still out of the scene");
            AssertFlags(positive, "the positive side, still out of the scene");

            // ---- 3 & 4. the product's order: the negative side first, read back at once --------------------------
            negative.Root.SetActive(true);
            Assert.That(negative.Root.activeInHierarchy, Is.True, "the negative side is in the scene");
            AssertFlags(negative, "the negative side, straight after its activation and before anything re-sets them");

            positive.Root.SetActive(true);
            Assert.That(positive.Root.activeInHierarchy, Is.True, "the positive side is in the scene");
            AssertFlags(positive, "the positive side, straight after its activation and before anything re-sets them");

            // ---- 5. the negative side again, now that the joint's own side has entered ---------------------------
            AssertFlags(negative, "the negative side again, after the side carrying the joint entered");

            // the joint really is on the positive side and names the other body: the configuration under test
            var joint = positive.Root.GetComponent<ConfigurableJoint>();
            Assert.That(joint, Is.Not.Null, "the sibling joint is on the positive side");
            Assert.That(joint.connectedBody, Is.SameAs(negative.Body), "and it names the negative side's body");
            Assert.That(
                positive.Root.GetComponent<ConfigurableJoint>() != null && negative.Root.GetComponent<ConfigurableJoint>() == null,
                Is.True, "only the positive side carries one");
        }

        private static void AssertFlags(PhysicsOwnerSide side, string when)
        {
            Assert.That(side.Body, Is.Not.Null, when + ": there is a body to read");
            Assert.That(side.Body.automaticCenterOfMass, Is.False, when + ": the centre of mass is still given, not computed");
            Assert.That(side.Body.automaticInertiaTensor, Is.False, when + ": the inertia is still given, not computed");
        }

        // ----- the real Provisional composition -------------------------------------------------------------------

        /// <summary>
        /// Builds the pair the publication would be handed: <paramref name="convexCount"/> boxes, every one of them
        /// crossed by the plane, so each side ends with that many colliders.
        /// </summary>
        private ProvisionalOwnerCandidate NewCandidate(
            bool withAnchor, int convexCount, out bool negativeFixed, out bool positiveFixed)
        {
            PhysicsOwnerShape shape = NewAuthoredShape(convexCount);
            var plane = new float4(0f, 1f, 0f, 0f);
            float3[] anchors = withAnchor ? new[] { new float3(0f, -0.5f, 0f) } : new float3[0];
            Assert.That(
                FixedSupportAnchors.TryDistribute(
                    anchors, plane, 1e-5f, new List<float3>(), new List<float3>(),
                    out AnchorDistributionResult distribution),
                Is.True, "the anchors were distributed");
            negativeFixed = distribution.IsNegativeFixed;
            positiveFixed = distribution.IsPositiveFixed;

            var sides = new ConvexSide[convexCount];
            for (int i = 0; i < convexCount; i++)
            {
                sides[i] = ConvexSide.Split;
            }

            var build = new ProvisionalOwnerBuildInput
            {
                sourceShape = shape,
                sides = sides,
                planeLocal = plane,
                placement = PhysicsOwnerPlacement.Identity,
                sourceMotion = default,
                anchors = distribution,
                parentMass = ParentMass,
                sourceInertia = new float3(2f, 5f, 9f),
                sourceInertiaRotation = quaternion.AxisAngle(math.normalize(new float3(1f, 2f, 3f)), 0.7f),
                cooking = PhysicsCutCook.DefaultCooking,
                name = "Provisional",
            };

            Assert.That(
                ProvisionalOwnerBuilder.TryBuild(
                    in build, out ProvisionalOwnerCandidate candidate, out PhysicsOwnerBuildOutcome built),
                Is.True, "the pair was built: " + built);
            _disposables.Add(candidate);
            return candidate;
        }

        /// <summary>
        /// <paramref name="convexCount"/> boxes side by side along x, each of them crossed by the plane y = 0.
        /// </summary>
        private PhysicsOwnerShape NewAuthoredShape(int convexCount)
        {
            var corners = new List<float3>();
            var faceOffsets = new List<int> { 0 };
            var faceIndices = new List<int>();
            var ranges = new ConvexBrepRange[convexCount];
            var meshes = new List<Mesh>();

            for (int c = 0; c < convexCount; c++)
            {
                float x = 3f * c;
                foreach (float3 corner in BoxCorners(x))
                {
                    corners.Add(corner);
                }

                for (int f = 0; f < 6; f++)
                {
                    faceOffsets.Add(4 * (6 * c + f + 1));
                }

                foreach (int index in BoxFaceIndices())
                {
                    faceIndices.Add(index + 8 * c);
                }

                ranges[c] = new ConvexBrepRange
                {
                    vertexBase = 8 * c, vertexCount = 8,
                    faceBase = 6 * c, faceCount = 6,
                    faceIndexBase = 24 * c, faceIndexCount = 24,
                    edgeBase = 12 * c, edgeCount = 12,
                    maxFaceLoop = 4,
                };

                meshes.Add(NewBoxMesh(x));
            }

            _vertices = new NativeArray<float3>(corners.ToArray(), Allocator.Persistent);
            _faceOffsets = new NativeArray<int>(faceOffsets.ToArray(), Allocator.Persistent);
            _faceIndices = new NativeArray<int>(faceIndices.ToArray(), Allocator.Persistent);
            _faceEdges = new NativeArray<int>(24 * convexCount, Allocator.Persistent);
            _edges = new NativeArray<BrepEdge>(12 * convexCount, Allocator.Persistent);

            var bank = new ConvexBrepBank
            {
                vertices = (float3*)_vertices.GetUnsafePtr(),
                faceOffsets = (int*)_faceOffsets.GetUnsafePtr(),
                faceIndices = (int*)_faceIndices.GetUnsafePtr(),
                faceEdges = (int*)_faceEdges.GetUnsafePtr(),
                edges = (BrepEdge*)_edges.GetUnsafePtr(),
            };

            _meshSource = PhysicsShapeSource.External();
            PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(
                bank, ranges, meshes, _meshSource, float4x4.identity);
            _disposables.Add(shape);
            return shape;
        }

        private static float3[] BoxCorners(float x)
        {
            return new[]
            {
                new float3(x - 1f, -1f, -1f), new float3(x + 1f, -1f, -1f),
                new float3(x + 1f, 1f, -1f), new float3(x - 1f, 1f, -1f),
                new float3(x - 1f, -1f, 1f), new float3(x + 1f, -1f, 1f),
                new float3(x + 1f, 1f, 1f), new float3(x - 1f, 1f, 1f),
            };
        }

        private static int[] BoxFaceIndices()
        {
            return new[]
            {
                0, 3, 2, 1, 4, 5, 6, 7, 0, 1, 5, 4,
                2, 3, 7, 6, 1, 2, 6, 5, 0, 4, 7, 3,
            };
        }

        private Mesh NewBoxMesh(float x)
        {
            var mesh = new Mesh { name = "Authored", hideFlags = HideFlags.HideAndDontSave };
            var vertices = new Vector3[8];
            float3[] corners = BoxCorners(x);
            for (int i = 0; i < 8; i++)
            {
                vertices[i] = corners[i];
            }

            mesh.vertices = vertices;
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            mesh.RecalculateNormals();
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            _meshes.Add(mesh);
            return mesh;
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
