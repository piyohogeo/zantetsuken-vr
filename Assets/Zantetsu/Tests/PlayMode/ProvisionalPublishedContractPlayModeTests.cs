// What a published Provisional pair's bodies hold: the pair goes through
// `ProvisionalPhysicsPublication.TryPublish`, the way the driver publishes one, and each body is read
// afterwards -- both automatic mass properties off, the fixing the anchors decided, the build's mass, centre
// of mass, inertia and principal frame, and the motion, which a free side carries and a fixed one is not
// given.
//
// Nothing here reads a private member or activates a side by hand: the ledger, the registry,
// `ProvisionalOwnerBuilder.TryBuild` and `TryPublish` are the ordinary ones, and the fixture is the one
// `ProvisionalPairPlayModeTests` uses -- an authored source with its own colliders, admitted and prepared in
// the ledger. The state *before* the publication writes anything is
// `ProvisionalMassFlagActivationPlayModeTests`'s subject.
//
// **What it does not say**: nothing about time, and it is not a proof that no internal recomputation happens.
// It reads the values back and compares them with the ones the build decided.
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
    public unsafe class ProvisionalPublishedContractPlayModeTests
    {
        private const double ParentMass = 12.0;

        private readonly List<GameObject> _objects = new List<GameObject>();

        /// <summary>
        /// The candidate this test built, **held by the fixture**: an assertion that ends the test part way through
        /// must not leave two actors and two shape holds behind. Disposing it is safe either way -- it is refused
        /// twice over, and a candidate the publication took (`Detach`) destroys nothing when it is disposed.
        /// </summary>
        private ProvisionalOwnerCandidate _candidate;

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
            _candidate?.Dispose();
            _candidate = null;
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

        [UnityTest]
        public IEnumerator PublishedPair_HoldsTheBuildsContract_BothSidesFree()
        {
            yield return null;
            RunCheck(false, 1);
        }

        [UnityTest]
        public IEnumerator PublishedPair_HoldsTheBuildsContract_OneSideFixedByAnchors()
        {
            yield return null;
            RunCheck(true, 1);
        }

        [UnityTest]
        public IEnumerator PublishedPair_HoldsTheBuildsContract_BothSidesFree_ThreeColliders()
        {
            yield return null;
            RunCheck(false, 3);
        }

        [UnityTest]
        public IEnumerator PublishedPair_HoldsTheBuildsContract_OneSideFixedByAnchors_ThreeColliders()
        {
            yield return null;
            RunCheck(true, 3);
        }

        /// <summary>
        /// One real publication, and what the two bodies hold afterwards: both automatic mass properties still off,
        /// the fixing the anchors decided, the build's mass, centre of mass, inertia and principal frame, and the
        /// motion -- which a free side carries and a fixed one is not given.
        /// </summary>
        private void RunCheck(bool withAnchor, int convexCount)
        {
            _registry = new PhysicsOwnerRegistry();
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));

            PhysicsOwnerShape shape = NewAuthoredShape(convexCount);
            GameObject sourceRoot = NewSourceObject(shape, convexCount);
            float3[] anchors = withAnchor ? new[] { new float3(0f, -0.5f, 0f) } : new float3[0];
            LogicalFragmentId source = ledger.AddFragment(anchors);
            _registry.RegisterAuthored(
                source, sourceRoot, sourceRoot.GetComponent<Rigidbody>(), shape, false, Matrix4x4.identity);

            // The ledger's own plane, and the anchor distribution it prepares for this operation: the fixing is the
            // product's decision here, not the test's.
            var plane = new float4(0f, 1f, 0f, 0f);
            Assert.That(
                ledger.Admit(source, plane, true, out CutOperationId operation),
                Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(
                ledger.PrepareAnchorDistribution(operation, 1e-5f, out AnchorDistributionResult distribution),
                Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(
                withAnchor
                    ? (distribution.IsNegativeFixed || distribution.IsPositiveFixed)
                    : (!distribution.IsNegativeFixed && !distribution.IsPositiveFixed),
                Is.True,
                withAnchor ? "the anchored cut fixes a side" : "the free cut fixes neither");

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

                // The source inertia and its frame, which `ProvisionalBoxMass` uses **only as a fallback** when the
                // box form is not finite: on this input the side inertia comes from the source box extents and the
                // side mass, with the identity frame. They are given here so the build has them, and nothing is
                // concluded from them.
                sourceInertia = new float3(2f, 5f, 9f),
                sourceInertiaRotation = quaternion.AxisAngle(math.normalize(new float3(1f, 2f, 3f)), 0.7f),
                cooking = PhysicsCutCook.DefaultCooking,
                name = "Provisional",
            };
            Assert.That(
                ProvisionalOwnerBuilder.TryBuild(
                    in build, out ProvisionalOwnerCandidate candidate, out PhysicsOwnerBuildOutcome built),
                Is.True, "the pair was built: " + built);
            _candidate = candidate;

            // ---- the input, before any publication: is it one a turned principal frame would show up in? --------
            // The side's inertia is the **source box's** extents with that side's mass (`ProvisionalBoxMass`), and
            // its principal frame is the identity; the source inertia given to the build is only a fallback for a
            // non-finite box. So this is a statement about the test's own input: with three different extents the
            // three principal moments differ, and a tensor expressed in a different frame is a different tensor,
            // which the comparison below can see. **It does not promise that an automatically recomputed inertia
            // would be caught** -- nothing here says what such a value would be.
            foreach (PhysicsOwnerSide side in new[] { candidate.Negative, candidate.Positive })
            {
                string whichBuilt = side.positive ? "the positive side" : "the negative side";
                Debug.Log(
                    "[input] " + whichBuilt + ": convexes=" + convexCount
                    + " box half-extents=" + HalfExtents + " spacing=" + Spacing
                    + " mass=" + side.Mass + " inertia=" + side.InertiaTensor
                    + " inertiaRotation=" + side.InertiaRotation.value
                    + " centreOfMass=" + side.CenterOfMass + " fixed=" + side.FixedByAnchors);
                AssertInputWouldShowATurnedFrame(side, whichBuilt);
            }

            // The publication reads the motion from the source body itself, so that is what has to be moving.
            Rigidbody sourceBody = sourceRoot.GetComponent<Rigidbody>();
            sourceBody.linearVelocity = new Vector3(0.4f, -0.2f, 0.9f);
            sourceBody.angularVelocity = new Vector3(0.3f, 1.1f, -0.5f);

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

            foreach (PhysicsOwnerSide side in new[] { pair.Negative, pair.Positive })
            {
                string which = side.positive ? "the positive side" : "the negative side";
                Assert.That(side.Root.activeInHierarchy, Is.True, which + " is in the scene");
                Assert.That(
                    side.Colliders.Count, Is.EqualTo(convexCount), which + " has one collider per convex it keeps");

                Assert.That(
                    side.Body.automaticCenterOfMass, Is.False, which + "'s centre of mass is given, not computed");
                Assert.That(side.Body.automaticInertiaTensor, Is.False, which + "'s inertia is given, not computed");
                Assert.That(
                    side.Body.isKinematic, Is.EqualTo(side.FixedByAnchors), which + "'s fixing follows its anchors");
                Assert.That(
                    side.Body.mass, Is.EqualTo((float)side.Mass).Within(1e-4f), which + " holds the build's mass");
                Assert.That(
                    (float3)(Vector3)side.Body.centerOfMass, Is.EqualTo(side.CenterOfMass).Using(Float3Within(1e-4f)),
                    which + " holds the build's centre of mass");
                Assert.That(
                    (float3)(Vector3)side.Body.inertiaTensor, Is.EqualTo(side.InertiaTensor).Using(Float3Within(1e-3f)),
                    which + " holds the build's inertia");
                AssertSameInertiaTensor(side, which + ", published");

                if (side.FixedByAnchors)
                {
                    Assert.That(
                        (float3)(Vector3)side.Body.linearVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-4f)),
                        which + " is fixed, so it takes no velocity");
                    Assert.That(
                        (float3)(Vector3)side.Body.angularVelocity, Is.EqualTo(float3.zero).Using(Float3Within(1e-4f)),
                        which + " is fixed, so it takes no spin");
                    continue;
                }

                Assert.That(
                    (float3)(Vector3)side.Body.linearVelocity, Is.EqualTo(side.LinearVelocity).Using(Float3Within(1e-3f)),
                    which + " carries the first split's velocity");
                Assert.That(
                    (float3)(Vector3)side.Body.angularVelocity, Is.EqualTo(side.AngularVelocity).Using(Float3Within(1e-3f)),
                    which + " carries the spin");
                Assert.That(
                    math.length(side.LinearVelocity), Is.GreaterThan(0.1f),
                    which + " was given a velocity to carry, so reading zero back would be a difference");
                Assert.That(
                    math.length(side.AngularVelocity), Is.GreaterThan(0.1f), which + " was given a spin to carry");
            }

            // The fixture gives the candidate back in its teardown, whether this line is reached or not.
            _registry.Retire(source);
            _registry.EndProvisional(operation);
        }

        /// <summary>
        /// The input is one in which a difference in the principal frame would be visible: the three principal
        /// moments differ from one another, so the same numbers read in a turned frame give a different tensor.
        /// **This is a property of the test's input, not a guarantee about recomputation.**
        /// </summary>
        private static void AssertInputWouldShowATurnedFrame(PhysicsOwnerSide side, string which)
        {
            float3 inertia = side.InertiaTensor;
            Assert.That(
                math.abs(inertia.x - inertia.y), Is.GreaterThan(0.1f),
                which + "'s first two principal moments differ, so a turned frame would show");
            Assert.That(
                math.abs(inertia.y - inertia.z), Is.GreaterThan(0.1f),
                which + "'s last two principal moments differ, so a turned frame would show");
            Assert.That(
                math.abs(inertia.x - inertia.z), Is.GreaterThan(0.1f),
                which + "'s outer two principal moments differ, so a turned frame would show");
        }

        /// <summary>The inertia the body really holds, as a tensor in its own frame: R diag(I) R^T.</summary>
        private static float3x3 TensorOf(Rigidbody body)
        {
            float3x3 rotation = new float3x3(body.inertiaTensorRotation);
            var diagonal = float3x3.zero;
            diagonal.c0.x = body.inertiaTensor.x;
            diagonal.c1.y = body.inertiaTensor.y;
            diagonal.c2.z = body.inertiaTensor.z;
            return math.mul(math.mul(rotation, diagonal), math.transpose(rotation));
        }

        /// <summary>
        /// The body's inertia is the one decided for it, **compared as a tensor** so that the quaternion's sign and
        /// the order of its axes cannot hide a difference.
        /// </summary>
        private static void AssertSameInertiaTensor(PhysicsOwnerSide side, string which)
        {
            float3x3 held = TensorOf(side.Body);
            float3x3 rotation = new float3x3(side.InertiaRotation);
            var diagonal = float3x3.zero;
            diagonal.c0.x = side.InertiaTensor.x;
            diagonal.c1.y = side.InertiaTensor.y;
            diagonal.c2.z = side.InertiaTensor.z;
            float3x3 expected = math.mul(math.mul(rotation, diagonal), math.transpose(rotation));
            float difference = math.max(
                math.max(math.length(held.c0 - expected.c0), math.length(held.c1 - expected.c1)),
                math.length(held.c2 - expected.c2));
            Assert.That(
                difference, Is.LessThan(1e-2f),
                which + " holds the inertia it was given, principal frame included");
        }

        private static IEqualityComparer<float3> Float3Within(float tolerance)
        {
            return new Float3Comparer(tolerance);
        }

        private sealed class Float3Comparer : IEqualityComparer<float3>
        {
            private readonly float _tolerance;

            public Float3Comparer(float tolerance)
            {
                _tolerance = tolerance;
            }

            public bool Equals(float3 a, float3 b)
            {
                return math.length(a - b) <= _tolerance;
            }

            public int GetHashCode(float3 value)
            {
                return value.GetHashCode();
            }
        }

        // ----- authored boxes, made without the editor-only harness --------------------------------------------------

        /// <summary>The half-extents of one box: three different edge lengths, so no two moments are equal.</summary>
        private static readonly float3 HalfExtents = new float3(0.7f, 0.5f, 1.1f);

        /// <summary>How far apart the boxes stand along x, which keeps them from meeting.</summary>
        private const float Spacing = 3f;

        /// <summary>
        /// <paramref name="convexCount"/> boxes side by side along x, each of them crossed by the plane y = 0, in one
        /// bank, so that a published side has more than one collider. **Each box has three different edge lengths**
        /// (<see cref="HalfExtents"/>), and the B-rep and the mesh are the same shape.
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
                float x = Spacing * c;
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
            return PhysicsOwnerShape.Authored(bank, ranges, meshes, _meshSource, float4x4.identity);
        }

        /// <summary>
        /// The eight corners of one box, centred on x and on the plane y = 0, with the three different edge lengths
        /// of <see cref="HalfExtents"/>. **The mesh below is built from these same corners**, so the B-rep and the
        /// collider are one shape.
        /// </summary>
        private static float3[] BoxCorners(float x)
        {
            float ex = HalfExtents.x, ey = HalfExtents.y, ez = HalfExtents.z;
            return new[]
            {
                new float3(x - ex, -ey, -ez), new float3(x + ex, -ey, -ez),
                new float3(x + ex, ey, -ez), new float3(x - ex, ey, -ez),
                new float3(x - ex, -ey, ez), new float3(x + ex, -ey, ez),
                new float3(x + ex, ey, ez), new float3(x - ex, ey, ez),
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

        private GameObject NewSourceObject(PhysicsOwnerShape shape, int convexCount)
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
            for (int c = 0; c < convexCount; c++)
            {
                MeshCollider collider = root.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = shape.MeshOf(c);
            }

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
