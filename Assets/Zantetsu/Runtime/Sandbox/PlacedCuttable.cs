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
    /// topology, its convexes (a building has one; a prop one or more), anchors and flags, as the scenario exporters write
    /// it. Not a product importer.
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
        public Vector3[] anchors = new Vector3[0];   // none is a normal input (2026-10-03, TL): an input without the field has none

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
        private readonly List<Mesh> _colliderMeshes = new List<Mesh>();

        public LogicalFragmentId Fragment { get; private set; }
        public GameObject Actor { get; private set; }
        public int AnchorCount { get; private set; }
        public bool IsBuilding { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }

        public static PlacedCuttableRegistration Register(CutWorldRoot world, PlacedCuttableInput data, Transform target,
            Renderer[] instanceRenderers, Collider[] instanceColliders, float mass, bool expectBuilding, bool withoutBuildingWorld = false)
        {
            // A prop may be an authored compound of several convexes (2026-10-03: the walk city's tree_012, six); a building
            // registered here as an ordinary body keeps one.
            // Anchors as authored, none included (2026-10-03, TL): what fixes a piece is the anchors it holds.
            if (!data.isCuttable || data.isBuilding != expectBuilding || data.hulls.Length == 0 || (data.isBuilding && data.hulls.Length != 1))
            {
                throw new InvalidOperationException(data.name + ": not a cuttable " + (expectBuilding ? "building with one convex" : "prop with its convexes"));
            }

            if ((target.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                throw new InvalidOperationException(data.name + ": the placed instance is scaled: " + target.lossyScale);
            }

            var made = new PlacedCuttableRegistration { IsBuilding = data.isBuilding, Name = data.name };
            VpStoredGeometry geometry = default;
            bool stored = false;
            LogicalFragmentId fragment;
            Rigidbody body;
            try
            {
                made._shape = made.NewShape(data.hulls);
                bool appended = world.Storage.TryAppendCuttable(data.Vertices(), data.indices, data.topology, data.topologyCount,
                    new[] { new VpGeometrySubmesh(0, data.indices.Length, 0) }, out geometry, out VpCutInputVerdict verdict);
                if (!appended)
                {
                    throw new InvalidOperationException(data.name + ": the geometry was refused: " + verdict);
                }

                stored = true;
                made.Actor = new GameObject((data.isBuilding ? "Building " : "Prop ") + data.name);
                made.Actor.transform.SetPositionAndRotation(target.position, target.rotation);
                body = made.Actor.AddComponent<Rigidbody>();
                body.useGravity = true;
                body.mass = mass;
                body.isKinematic = true;   // standing as placed until it is cut, anchored or not
                // One convex collider a convex, all on the actor (as a cut's side carries its convexes).
                foreach (Mesh mesh in made._colliderMeshes)
                {
                    var collider = made.Actor.AddComponent<MeshCollider>();
                    collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                    collider.convex = true;
                    collider.sharedMesh = mesh;
                }
                // The world refuses only at its display, before anything of the body is its own (CutWorldRoot.TryAddBody):
                // a refused geometry was never shown, and is given back below.
                bool added = world.TryAddBody(made.Actor, made._shape, geometry, Matrix4x4.identity, Matrix4x4.identity,
                    data.anchors.Length > 0 ? data.anchors.Select(v => (float3)v).ToArray() : null, data.isBuilding && !withoutBuildingWorld, out fragment);
                if (!added)
                {
                    throw new InvalidOperationException(data.name + ": refused by the world");
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(e.Message + made.Abandon(world, stored ? geometry : (VpStoredGeometry?)null), e);
            }

            // The instance's own drawing and colliders go only now, the world having taken the actor; the registered actor
            // stands exactly where the instance stood.
            foreach (Renderer r in instanceRenderers)
            {
                if (r != null) r.enabled = false;
            }

            foreach (Collider c in instanceColliders)
            {
                if (c != null) c.enabled = false;
            }

            made.Fragment = fragment;
            made.AnchorCount = data.anchors.Length;
            bool fixedByAnchors = world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.FixedByAnchors && body.isKinematic;
            made.Description = "name=" + data.name + " fragment=" + fragment + " anchors=" + data.anchors.Length
                + " fixedByAnchors=" + fixedByAnchors + " building=" + (owner != null && owner.Building.IsBuildingDerived)
                + " depth=" + (owner != null ? owner.Building.SplitDepth : -1)
                + " position=" + made.Actor.transform.position.ToString("F3") + " rotation=" + made.Actor.transform.rotation.eulerAngles.ToString("F2")
                + " mass=" + mass + " triangles=" + data.indices.Length / 3 + " convexes=" + data.hulls.Length + " hullVertices=" + string.Join("+", data.hulls.Select(h => h.vertices.Length))
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
            Renderer[] instanceRenderers, Collider[] instanceColliders, float mass, int materialIndex = 0, LogicalFragmentId issued = default)
        {
            if (!data.isCuttable || !data.isBuilding || data.hulls.Length != 1)
            {
                throw new InvalidOperationException(data.name + ": not a cuttable building with one convex");
            }

            if ((target.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                throw new InvalidOperationException(data.name + ": the placed instance is scaled: " + target.lossyScale);
            }

            var made = new PlacedCuttableRegistration { IsBuilding = true, Name = data.name };
            PlacedCuttableInput.Hull hull = data.hulls[0];
            VpStoredGeometry geometry = default;
            bool stored = false, displayed = false;
            LogicalFragmentId fragment;
            HullGroup group;
            try
            {
                made._shape = made.NewShape(data.hulls);
                bool appended = world.Storage.TryAppendCuttable(data.Vertices(), data.indices, data.topology, data.topologyCount,
                    new[] { new VpGeometrySubmesh(0, data.indices.Length, materialIndex) }, out geometry, out VpCutInputVerdict verdict);
                if (!appended)
                {
                    throw new InvalidOperationException(data.name + ": the geometry was refused: " + verdict);
                }

                stored = true;
                made.Actor = new GameObject("Building " + data.name);
                made.Actor.transform.SetPositionAndRotation(target.position, target.rotation);
                var body = made.Actor.AddComponent<Rigidbody>();
                body.useGravity = true;
                body.mass = mass;
                body.isKinematic = true;
                // The world says whether its display took the geometry, however the call ends (CutWorldRoot.TryAddBuildingHull):
                // one it never showed is still ours to give back -- a refusal before the display, the display's own refusal, or
                // an exception before it -- and one it showed is the world's, retired with the fragment and taken back by it.
                bool added = world.TryAddBuildingHull(made.Actor, made._shape, geometry, Matrix4x4.identity, data.anchors.Select(v => (float3)v).ToArray(), mass, issued, ref displayed, out fragment, out group);
                if (!added)
                {
                    throw new InvalidOperationException(data.name + ": refused by the hull trial");
                }
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(e.Message + made.Abandon(world, stored && !displayed ? geometry : (VpStoredGeometry?)null)
                    + (displayed ? "; its stored geometry left to the world (shown, then refused: the world takes it back)" : ""), e);
            }

            made.Dispose();   // the convex was only read
            // The instance's own drawing and colliders go only now, the world having taken the building.
            foreach (Renderer r in instanceRenderers)
            {
                if (r != null) r.enabled = false;
            }

            foreach (Collider c in instanceColliders)
            {
                if (c != null) c.enabled = false;
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

        /// <summary>
        /// A building into the hull trial at its first hit, drawn by its own renderers until that cut takes the drawing over
        /// (TL, 2026-10-03; <see cref="CutWorldRoot.TryAddBuildingHullDeferred"/>): the same input, display geometry, actor
        /// and group as <see cref="RegisterHull"/>, but nothing shown and the instance not touched here --
        /// <paramref name="handedOver"/> is told when the first cut's display has the building (the caller switches the
        /// instance off there), <paramref name="takenBack"/> when that cut ended without a publication and the world took
        /// it back out. Refused here (the storage, the trial): the actor and the convex given back, the geometry too (it was
        /// never shown), the instance as it was.
        /// </summary>
        public static PlacedCuttableRegistration RegisterHullDeferred(CutWorldRoot world, PlacedCuttableInput data, Transform target, float mass, int materialIndex,
            LogicalFragmentId issued, Action handedOver, Action<string> takenBack)
        {
            if (!data.isCuttable || !data.isBuilding || data.hulls.Length != 1)
            {
                throw new InvalidOperationException(data.name + ": not a cuttable building with one convex");
            }

            if ((target.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                throw new InvalidOperationException(data.name + ": the placed instance is scaled: " + target.lossyScale);
            }

            var made = new PlacedCuttableRegistration { IsBuilding = true, Name = data.name };
            VpStoredGeometry geometry = default;
            bool stored = false, taken = false;
            LogicalFragmentId fragment;
            HullGroup group;
            try
            {
                made._shape = made.NewShape(data.hulls);
                VpRenderVertex[] vertices;
                VpGeometrySubmesh[] submeshes;
                vertices = data.Vertices();
                submeshes = new[] { new VpGeometrySubmesh(0, data.indices.Length, materialIndex) };

                bool appended = world.Storage.TryAppendCuttable(vertices, data.indices, data.topology, data.topologyCount, submeshes, out geometry, out VpCutInputVerdict verdict);
                if (!appended)
                {
                    throw new InvalidOperationException(data.name + ": the geometry was refused: " + verdict);
                }

                stored = true;
                Rigidbody body;
                made.Actor = new GameObject("Building " + data.name);
                made.Actor.transform.SetPositionAndRotation(target.position, target.rotation);
                body = made.Actor.AddComponent<Rigidbody>();
                body.useGravity = true;
                body.mass = mass;
                body.isKinematic = true;

                bool added = world.TryAddBuildingHullDeferred(made.Actor, made._shape, geometry, Matrix4x4.identity, data.anchors.Select(v => (float3)v).ToArray(), mass,
                    issued, handedOver, takenBack, ref taken, out fragment, out group, out string refusal);
                if (!added)
                {
                    throw new InvalidOperationException(data.name + ": refused by the hull trial: " + refusal);
                }
            }
            catch (Exception e)
            {
                // The geometry is given back here only while it is still ours: once the world took it (its base geometry
                // registered) the world gives it back.
                throw new InvalidOperationException(e.Message + made.Abandon(world, stored && !taken ? geometry : (VpStoredGeometry?)null), e);
            }

            made.Dispose();   // the convex was only read
            made.Fragment = fragment;
            made.Group = group;
            made.AnchorCount = data.anchors.Length;
            made.Description = "name=" + data.name + " fragment=" + fragment + " hull group=" + group.Id + " anchors=" + data.anchors.Length
                + " anchored=" + group.Anchored + " hull vertices=" + group.VertexCount + " faces=" + group.FaceCount + " (input convex " + data.hulls[0].vertices.Length + ")"
                + " position=" + made.Actor.transform.position.ToString("F3") + " rotation=" + made.Actor.transform.rotation.eulerAngles.ToString("F2")
                + " mass=" + mass + " triangles=" + data.indices.Length / 3 + " (shown at its first cut's publication) sourceSha256=" + data.sha256;
            return made;
        }

        /// <summary>
        /// A registration that failed leaves the scene as it found it (2026-10-03): the actor goes at once -- deactivated, so
        /// no body or collider of it stays in the physics scene for a step, and destroyed -- the convex's shape, arrays and
        /// mesh are given back, and a geometry the world never showed is given back to the storage (its index range
        /// retired, its vertex group released). The instance was never touched: its renderers and colliders are switched
        /// off only after the world has taken the actor. Returns what was given back, for the refusal's message.
        /// </summary>
        private string Abandon(CutWorldRoot world, VpStoredGeometry? unshown)
        {
            string what = "; given back: ";
            if (Actor != null)
            {
                Actor.SetActive(false);
                UnityEngine.Object.Destroy(Actor);
                Actor = null;
                what += "the actor, ";
            }

            Dispose();
            what += "the convex";
            if (unshown.HasValue && world != null && world.Storage != null)
            {
                VpCpuGeometryStorage storage = world.Storage;
                // The group is read before the retirement: a Free range's metadata is refused.
                bool grouped = storage.TryGetVertexGroup(unshown.Value, out int vertexGroup);
                bool retired = storage.TryRetireIndices(unshown.Value.indexRange);
                bool released = grouped && retired && storage.TryReleaseVertexGroup(vertexGroup);
                what += ", the stored geometry (indices retired " + retired + ", vertex room released " + released + ")";
            }

            return what;
        }

        /// <summary>Gives back the shape, its arrays and the collider mesh; only once the world no longer uses them (released or gone).</summary>
        public void Dispose()
        {
            _shape?.Dispose();
            _shape = null;
            foreach (IDisposable array in _native) array.Dispose();
            _native.Clear();
            foreach (Mesh mesh in _colliderMeshes) if (mesh != null) UnityEngine.Object.Destroy(mesh);
            _colliderMeshes.Clear();
        }

        // The input's convexes as one authored shape (2026-10-03: one or more): one bank holding them one after another
        // (BuildBank), and one cooked collider mesh a convex.
        private unsafe PhysicsOwnerShape NewShape(PlacedCuttableInput.Hull[] hulls)
        {
            ConvexBrepBank bank = BuildBank(hulls, _native, out List<ConvexBrepRange> ranges);
            foreach (PlacedCuttableInput.Hull hull in hulls)
            {
                var colliderMesh = new Mesh { name = "Placed cuttable convex " + ranges.Count, hideFlags = HideFlags.HideAndDontSave };
                colliderMesh.vertices = hull.vertices;
                var triangles = new List<int>();
                for (int f = 0; f + 1 < hull.faceOffsets.Length; f++)
                {
                    for (int k = hull.faceOffsets[f] + 1; k + 1 < hull.faceOffsets[f + 1]; k++)
                    {
                        triangles.Add(hull.faceIndices[hull.faceOffsets[f]]);
                        triangles.Add(hull.faceIndices[k + 1]);
                        triangles.Add(hull.faceIndices[k]);
                    }
                }

                colliderMesh.triangles = triangles.ToArray();
                UnityEngine.Physics.BakeMesh(colliderMesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                _colliderMeshes.Add(colliderMesh);
            }

            return PhysicsOwnerShape.Authored(bank, ranges, new List<Mesh>(_colliderMeshes), PhysicsShapeSource.External(), float4x4.identity);
        }

        /// <summary>
        /// The input's convexes as one B-rep bank: them one after another, one range a convex with its own data local to it
        /// (vertex numbers, face offsets and edges counted from its own start). The bank's arrays are added to
        /// <paramref name="natives"/>, the caller's to give back once nothing reads the bank (a shape or a hit shape made from
        /// it copies what it keeps).
        /// </summary>
        internal static unsafe ConvexBrepBank BuildBank(PlacedCuttableInput.Hull[] hulls, List<IDisposable> natives, out List<ConvexBrepRange> ranges)
        {
            var corners = new List<float3>();
            var faceOffsets = new List<int>();
            var faceIndices = new List<int>();
            var faceEdges = new List<int>();
            var edges = new List<BrepEdge>();
            ranges = new List<ConvexBrepRange>(hulls.Length);
            foreach (PlacedCuttableInput.Hull hull in hulls)
            {
                BuildEdges(hull.faceOffsets, hull.faceIndices, out int[] localFaceEdges, out BrepEdge[] localEdges);
                ranges.Add(new ConvexBrepRange
                {
                    vertexBase = corners.Count, vertexCount = hull.vertices.Length,
                    faceBase = faceOffsets.Count, faceCount = hull.faceOffsets.Length - 1,
                    faceIndexBase = faceIndices.Count, faceIndexCount = hull.faceIndices.Length,
                    edgeBase = edges.Count, edgeCount = localEdges.Length,
                    maxFaceLoop = Enumerable.Range(0, hull.faceOffsets.Length - 1).Max(i => hull.faceOffsets[i + 1] - hull.faceOffsets[i]),
                });
                corners.AddRange(hull.vertices.Select(v => (float3)v));
                faceOffsets.AddRange(hull.faceOffsets);
                faceIndices.AddRange(hull.faceIndices);
                faceEdges.AddRange(localFaceEdges);
                edges.AddRange(localEdges);
            }

            var vertices = new NativeArray<float3>(corners.ToArray(), Allocator.Persistent);
            var offsets = new NativeArray<int>(faceOffsets.ToArray(), Allocator.Persistent);
            var indices = new NativeArray<int>(faceIndices.ToArray(), Allocator.Persistent);
            var edgesOfFaces = new NativeArray<int>(faceEdges.ToArray(), Allocator.Persistent);
            var edgeTable = new NativeArray<BrepEdge>(edges.ToArray(), Allocator.Persistent);
            natives.Add(vertices);
            natives.Add(offsets);
            natives.Add(indices);
            natives.Add(edgesOfFaces);
            natives.Add(edgeTable);

            return new ConvexBrepBank
            {
                vertices = (float3*)vertices.GetUnsafePtr(),
                faceOffsets = (int*)offsets.GetUnsafePtr(),
                faceIndices = (int*)indices.GetUnsafePtr(),
                faceEdges = (int*)edgesOfFaces.GetUnsafePtr(),
                edges = (BrepEdge*)edgeTable.GetUnsafePtr(),
            };
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
