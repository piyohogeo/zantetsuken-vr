using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A private real-asset input for a placed cuttable (a Megacity building or prop): the author's drawn geometry,
    /// topology, one convex, anchors and flags, as the scenario exporters write it. Not a product importer.
    /// </summary>
    [Serializable] public sealed class PlacedCuttableInput
    {
        public int schemaVersion, topologyCount;
        public string name, source, sha256;
        public bool isBuilding, isCuttable;
        public Vertex[] render;
        public uint[] indices;
        public int[] topology;
        public Hull[] hulls;
        public Vector3[] anchors;

        public VpRenderVertex[] Vertices() => render.Select(v => new VpRenderVertex
            { position = v.position, normal = v.normal, uv0 = v.uv }).ToArray();

        [Serializable] public sealed class Vertex { public Vector3 position, normal; public Vector2 uv; }
        [Serializable] public sealed class Hull { public Vector3[] vertices; public int[] faceOffsets, faceIndices; }
    }

    /// <summary>
    /// One placed cuttable taken into a cut world where its instance stands, from the author's own geometry, convex and
    /// anchors: the geometry into the world's storage, an actor with the convex at the instance's pose, anchored as the
    /// author placed it, a building or not as its input says (and as the caller expects). The instance's own renderers and
    /// colliders are switched off, so nothing of it is there twice. Nothing is cut here. The shape and its arrays are kept
    /// until the world is released (<see cref="Dispose"/>). A diagnosis may register a building as an ordinary body
    /// (<c>withoutBuildingWorld</c>): its pieces then carry no building World D6.
    /// </summary>
    public sealed class PlacedCuttableRegistration : IDisposable
    {
        private readonly List<IDisposable> _native = new List<IDisposable>();
        private PhysicsOwnerShape _shape;
        private Mesh _colliderMesh;

        public LogicalFragmentId Fragment { get; private set; }
        public GameObject Actor { get; private set; }
        public int AnchorCount { get; private set; }
        public bool IsBuilding { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }

        public static PlacedCuttableRegistration Register(CutWorldRoot world, PlacedCuttableInput data, Transform target,
            Renderer[] instanceRenderers, Collider[] instanceColliders, float mass, bool expectBuilding, bool withoutBuildingWorld = false)
        {
            if (!data.isCuttable || data.isBuilding != expectBuilding || data.hulls.Length != 1 || data.anchors.Length == 0)
            {
                throw new InvalidOperationException(data.name + ": not a cuttable " + (expectBuilding ? "building" : "prop") + " with one convex and anchors");
            }

            if ((target.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                throw new InvalidOperationException(data.name + ": the placed instance is scaled: " + target.lossyScale);
            }

            var made = new PlacedCuttableRegistration { IsBuilding = data.isBuilding, Name = data.name };
            PlacedCuttableInput.Hull hull = data.hulls[0];
            BuildEdges(hull.faceOffsets, hull.faceIndices, out int[] faceEdges, out BrepEdge[] edges);
            made._shape = made.NewShape(hull.vertices.Select(v => (float3)v).ToArray(), hull.faceOffsets, hull.faceIndices, faceEdges, edges, out made._colliderMesh);
            if (!world.Storage.TryAppendCuttable(data.Vertices(), data.indices, data.topology, data.topologyCount,
                    new[] { new VpGeometrySubmesh(0, data.indices.Length, 0) }, out VpStoredGeometry geometry, out VpCutInputVerdict verdict))
            {
                throw new InvalidOperationException(data.name + ": the geometry was refused: " + verdict);
            }

            // The instance's own drawing and colliders go; the registered actor stands exactly where the instance stood.
            foreach (Renderer r in instanceRenderers)
            {
                if (r != null) r.enabled = false;
            }

            foreach (Collider c in instanceColliders)
            {
                if (c != null) c.enabled = false;
            }

            made.Actor = new GameObject((data.isBuilding ? "Building " : "Prop ") + data.name);
            made.Actor.transform.SetPositionAndRotation(target.position, target.rotation);
            var body = made.Actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.mass = mass;
            body.isKinematic = true;
            var collider = made.Actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = made._colliderMesh;
            if (!world.TryAddBody(made.Actor, made._shape, geometry, Matrix4x4.identity, Matrix4x4.identity,
                    data.anchors.Select(v => (float3)v).ToArray(), data.isBuilding && !withoutBuildingWorld, out LogicalFragmentId fragment))
            {
                throw new InvalidOperationException(data.name + ": refused by the world");
            }

            made.Fragment = fragment;
            made.AnchorCount = data.anchors.Length;
            bool fixedByAnchors = world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.FixedByAnchors && body.isKinematic;
            made.Description = "name=" + data.name + " fragment=" + fragment + " anchors=" + data.anchors.Length
                + " fixedByAnchors=" + fixedByAnchors + " building=" + (owner != null && owner.Building.IsBuildingDerived)
                + " depth=" + (owner != null ? owner.Building.SplitDepth : -1)
                + " position=" + made.Actor.transform.position.ToString("F3") + " rotation=" + made.Actor.transform.rotation.eulerAngles.ToString("F2")
                + " mass=" + mass + " triangles=" + data.indices.Length / 3 + " hullVertices=" + hull.vertices.Length
                + " instanceRenderersOff=" + instanceRenderers.Count(r => r != null && !r.enabled)
                + " instanceCollidersOff=" + instanceColliders.Count(c => c != null && !c.enabled)
                + " sourceSha256=" + data.sha256;
            return made;
        }

        /// <summary>The hull group the building became (<see cref="RegisterHull"/>), or null.</summary>
        public HullGroup Group { get; private set; }

        /// <summary>
        /// A building into the hull trial (2026-09-30): the same input, display geometry and instance handling as
        /// <see cref="Register"/>, but the actor carries only a Rigidbody -- the trial makes the group's one collider from
        /// the convex hull of the input's convex -- and the world takes it through <see cref="CutWorldRoot.TryAddBuildingHull"/>.
        /// The input's convex is only read: its shape and mesh are given back here.
        /// </summary>
        public static PlacedCuttableRegistration RegisterHull(CutWorldRoot world, PlacedCuttableInput data, Transform target,
            Renderer[] instanceRenderers, Collider[] instanceColliders, float mass, int materialIndex = 0)
        {
            if (!data.isCuttable || !data.isBuilding || data.hulls.Length != 1 || data.anchors.Length == 0)
            {
                throw new InvalidOperationException(data.name + ": not a cuttable building with one convex and anchors");
            }

            if ((target.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                throw new InvalidOperationException(data.name + ": the placed instance is scaled: " + target.lossyScale);
            }

            var made = new PlacedCuttableRegistration { IsBuilding = true, Name = data.name };
            PlacedCuttableInput.Hull hull = data.hulls[0];
            BuildEdges(hull.faceOffsets, hull.faceIndices, out int[] faceEdges, out BrepEdge[] edges);
            made._shape = made.NewShape(hull.vertices.Select(v => (float3)v).ToArray(), hull.faceOffsets, hull.faceIndices, faceEdges, edges, out made._colliderMesh);
            if (!world.Storage.TryAppendCuttable(data.Vertices(), data.indices, data.topology, data.topologyCount,
                    new[] { new VpGeometrySubmesh(0, data.indices.Length, materialIndex) }, out VpStoredGeometry geometry, out VpCutInputVerdict verdict))
            {
                made.Dispose();
                throw new InvalidOperationException(data.name + ": the geometry was refused: " + verdict);
            }

            foreach (Renderer r in instanceRenderers)
            {
                if (r != null) r.enabled = false;
            }

            foreach (Collider c in instanceColliders)
            {
                if (c != null) c.enabled = false;
            }

            made.Actor = new GameObject("Building " + data.name);
            made.Actor.transform.SetPositionAndRotation(target.position, target.rotation);
            var body = made.Actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.mass = mass;
            body.isKinematic = true;
            bool added = world.TryAddBuildingHull(made.Actor, made._shape, geometry, Matrix4x4.identity, data.anchors.Select(v => (float3)v).ToArray(), mass, out LogicalFragmentId fragment, out HullGroup group);
            made.Dispose();   // the convex was only read
            if (!added)
            {
                throw new InvalidOperationException(data.name + ": refused by the hull trial");
            }

            made.Fragment = fragment;
            made.Group = group;
            made.AnchorCount = data.anchors.Length;
            made.Description = "name=" + data.name + " fragment=" + fragment + " hull group=" + group.Id + " anchors=" + data.anchors.Length
                + " anchored=" + group.Anchored + " hull vertices=" + group.VertexCount + " faces=" + group.FaceCount + " (input convex " + hull.vertices.Length + ")"
                + " position=" + made.Actor.transform.position.ToString("F3") + " rotation=" + made.Actor.transform.rotation.eulerAngles.ToString("F2")
                + " mass=" + mass + " triangles=" + data.indices.Length / 3
                + " instanceRenderersOff=" + instanceRenderers.Count(r => r != null && !r.enabled)
                + " instanceCollidersOff=" + instanceColliders.Count(c => c != null && !c.enabled)
                + " sourceSha256=" + data.sha256;
            return made;
        }

        /// <summary>Gives back the shape, its arrays and the collider mesh; only once the world no longer uses them (released or gone).</summary>
        public void Dispose()
        {
            _shape?.Dispose();
            _shape = null;
            foreach (IDisposable array in _native) array.Dispose();
            _native.Clear();
            if (_colliderMesh != null) UnityEngine.Object.Destroy(_colliderMesh);
            _colliderMesh = null;
        }

        private unsafe PhysicsOwnerShape NewShape(
            float3[] corners, int[] faceOffsets, int[] faceIndices, int[] faceEdges, BrepEdge[] edges, out Mesh colliderMesh)
        {
            var vertices = new NativeArray<float3>(corners, Allocator.Persistent);
            var offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
            var indices = new NativeArray<int>(faceIndices, Allocator.Persistent);
            var edgesOfFaces = new NativeArray<int>(faceEdges, Allocator.Persistent);
            var edgeTable = new NativeArray<BrepEdge>(edges, Allocator.Persistent);
            _native.Add(vertices);
            _native.Add(offsets);
            _native.Add(indices);
            _native.Add(edgesOfFaces);
            _native.Add(edgeTable);

            var bank = new ConvexBrepBank
            {
                vertices = (float3*)vertices.GetUnsafePtr(),
                faceOffsets = (int*)offsets.GetUnsafePtr(),
                faceIndices = (int*)indices.GetUnsafePtr(),
                faceEdges = (int*)edgesOfFaces.GetUnsafePtr(),
                edges = (BrepEdge*)edgeTable.GetUnsafePtr(),
            };

            var range = new ConvexBrepRange
            {
                vertexBase = 0, vertexCount = corners.Length,
                faceBase = 0, faceCount = faceOffsets.Length - 1,
                faceIndexBase = 0, faceIndexCount = faceIndices.Length,
                edgeBase = 0, edgeCount = edges.Length,
                maxFaceLoop = Enumerable.Range(0, faceOffsets.Length - 1).Max(i => faceOffsets[i + 1] - faceOffsets[i]),
            };

            colliderMesh = new Mesh { name = "Placed cuttable convex", hideFlags = HideFlags.HideAndDontSave };
            colliderMesh.vertices = corners.Select(c => (Vector3)c).ToArray();
            var triangles = new List<int>();
            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                for (int k = faceOffsets[f] + 1; k + 1 < faceOffsets[f + 1]; k++)
                {
                    triangles.Add(faceIndices[faceOffsets[f]]);
                    triangles.Add(faceIndices[k + 1]);
                    triangles.Add(faceIndices[k]);
                }
            }

            colliderMesh.triangles = triangles.ToArray();
            UnityEngine.Physics.BakeMesh(colliderMesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            return PhysicsOwnerShape.Authored(
                bank, new[] { range }, new List<Mesh> { colliderMesh }, PhysicsShapeSource.External(), float4x4.identity);
        }

        // One edge per unordered vertex pair: f0 is the face that runs it from its lower vertex to its higher one, f1 the
        // face that runs it the other way (the B-rep convention the kernel reads).
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
                        if (edge.f0 < 0) edge.f0 = f;
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
        }
    }
}
