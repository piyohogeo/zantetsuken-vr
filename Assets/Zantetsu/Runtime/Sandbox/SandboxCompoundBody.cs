using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Builds one owner out of several boxes, so that a measurement can ask for cuts of owners that differ in how
    /// much there is to do (DESIGN 7.5): how many convexes an owner holds, and how many of them a plane really
    /// splits.
    /// <para>
    /// **It is the sandbox's own box, repeated.** Each convex is the same eight-corner, six-face box the sandbox
    /// probe builds, at its own place; the compound is one bank holding them one after another, one range per box,
    /// and one collider mesh per box. Each convex's own data is local to it, which is what a range means. Nothing
    /// here is a new input format and nothing is read from disk.
    /// </para>
    /// <para>
    /// **The plane is the caller's.** A box placed across y = 0 is split by a plane through it; a box placed clear of
    /// it is inherited whole. That is how an owner with both kinds in it is made.
    /// </para>
    /// </summary>
    public sealed unsafe class SandboxCompoundBody : IDisposable
    {
        /// <summary>The corner cycles of one box, in the order the B-rep face wants them, in local indices.</summary>
        private static readonly int[][] FaceCycles =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
            new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

        private static readonly int[] ColliderTriangles =
        {
            0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
            2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
        };

        private readonly List<IDisposable> _native = new List<IDisposable>(8);
        private readonly List<Mesh> _meshes = new List<Mesh>(8);
        private bool _disposed;

        private SandboxCompoundBody(int convexCount, int crossingCount)
        {
            ConvexCount = convexCount;
            CrossingCount = crossingCount;
        }

        /// <summary>The shape to register the body with. The world owns it once the body is taken.</summary>
        public PhysicsOwnerShape Shape { get; private set; }

        /// <summary>The display geometry of the same boxes, already appended to the world's storage.</summary>
        public VpStoredGeometry Geometry { get; private set; }

        /// <summary>How many convexes this owner holds.</summary>
        public int ConvexCount { get; }

        /// <summary>How many of them lie across y = 0, which are the ones a plane through it splits.</summary>
        public int CrossingCount { get; }

        /// <summary>The collider mesh of the first convex, for the actor's own collider.</summary>
        public Mesh FirstColliderMesh => _meshes.Count > 0 ? _meshes[0] : null;

        /// <summary>
        /// Builds a compound of <paramref name="convexCount"/> boxes, of which <paramref name="crossingCount"/> lie
        /// across y = 0 and the rest sit clear of it, and appends its display geometry to
        /// <paramref name="storage"/>. Returns null, holding nothing, when the storage will not take the geometry.
        /// </summary>
        public static SandboxCompoundBody TryBuild(
            VpCpuGeometryStorage storage, int convexCount, int crossingCount, float3 halfExtents, int sourceIndex)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            if (convexCount <= 0 || crossingCount < 0 || crossingCount > convexCount)
            {
                throw new ArgumentOutOfRangeException(nameof(crossingCount), crossingCount, "0 to convexCount.");
            }

            var built = new SandboxCompoundBody(convexCount, crossingCount);
            var corners = new List<float3>(convexCount * 8);
            var faceOffsets = new List<int>(convexCount * 7);
            var faceIndices = new List<int>(convexCount * 24);
            var faceEdges = new List<int>(convexCount * 24);
            var edges = new List<BrepEdge>(convexCount * 12);
            var ranges = new List<ConvexBrepRange>(convexCount);

            float pitch = (halfExtents.x * 2f) + 0.2f;
            float stack = (halfExtents.y * 2f) + 0.2f;
            for (int c = 0; c < convexCount; c++)
            {
                bool crossing = c < crossingCount;

                // A crossing box sits centred on y = 0; the rest are stacked clear above it and set back, so that no
                // two boxes touch and only the crossing ones meet a plane through y = 0.
                float3 at = crossing
                    ? new float3((c - ((crossingCount - 1) * 0.5f)) * pitch, 0f, 0f)
                    : new float3(
                        (c - crossingCount) % 4 * pitch,
                        halfExtents.y + 0.4f + ((c - crossingCount) / 4 * stack),
                        (halfExtents.z * 2f) + 0.5f);

                int vertexBase = corners.Count;
                int faceBase = faceOffsets.Count;
                int faceIndexBase = faceIndices.Count;
                int edgeBase = edges.Count;

                AppendBoxCorners(corners, at, halfExtents);

                // One box's own face table, in **local** indices: the offsets are relative to this box's first face
                // index, and the vertex numbers are 0..7 of this box.
                var localOffsets = new int[FaceCycles.Length + 1];
                var localIndices = new List<int>(24);
                for (int f = 0; f < FaceCycles.Length; f++)
                {
                    localOffsets[f] = localIndices.Count;
                    localIndices.AddRange(FaceCycles[f]);
                }

                localOffsets[FaceCycles.Length] = localIndices.Count;
                BuildEdges(localOffsets, localIndices.ToArray(), out int[] localFaceEdges, out BrepEdge[] localEdges);

                faceOffsets.AddRange(localOffsets);
                faceIndices.AddRange(localIndices);
                faceEdges.AddRange(localFaceEdges);
                edges.AddRange(localEdges);

                ranges.Add(new ConvexBrepRange
                {
                    vertexBase = vertexBase,
                    vertexCount = 8,
                    faceBase = faceBase,
                    faceCount = FaceCycles.Length,
                    faceIndexBase = faceIndexBase,
                    faceIndexCount = localIndices.Count,
                    edgeBase = edgeBase,
                    edgeCount = localEdges.Length,
                    maxFaceLoop = 4,
                });
            }

            built.Shape = built.NewShape(
                corners.ToArray(), faceOffsets.ToArray(), faceIndices.ToArray(), faceEdges.ToArray(), edges.ToArray(),
                ranges);

            if (!TryAppendGeometry(storage, corners, sourceIndex, out VpStoredGeometry geometry))
            {
                built.Dispose();
                return null;
            }

            built.Geometry = geometry;
            return built;
        }

        private static void AppendBoxCorners(List<float3> corners, float3 at, float3 e)
        {
            corners.Add(at + new float3(-e.x, -e.y, -e.z));
            corners.Add(at + new float3(e.x, -e.y, -e.z));
            corners.Add(at + new float3(e.x, -e.y, e.z));
            corners.Add(at + new float3(-e.x, -e.y, e.z));
            corners.Add(at + new float3(-e.x, e.y, -e.z));
            corners.Add(at + new float3(e.x, e.y, -e.z));
            corners.Add(at + new float3(e.x, e.y, e.z));
            corners.Add(at + new float3(-e.x, e.y, e.z));
        }

        private PhysicsOwnerShape NewShape(
            float3[] corners,
            int[] faceOffsets,
            int[] faceIndices,
            int[] faceEdges,
            BrepEdge[] edges,
            List<ConvexBrepRange> ranges)
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

            for (int c = 0; c < ranges.Count; c++)
            {
                var mesh = new Mesh { name = "Sandbox compound collider " + c, hideFlags = HideFlags.HideAndDontSave };
                var meshVertices = new Vector3[8];
                for (int i = 0; i < 8; i++)
                {
                    meshVertices[i] = corners[ranges[c].vertexBase + i];
                }

                mesh.vertices = meshVertices;
                mesh.triangles = ColliderTriangles;
                UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                _meshes.Add(mesh);
            }

            return PhysicsOwnerShape.Authored(bank, ranges, _meshes, PhysicsShapeSource.External(), float4x4.identity);
        }

        /// <summary>The same boxes as one display geometry, appended to the world's storage as a cut input.</summary>
        private static bool TryAppendGeometry(
            VpCpuGeometryStorage storage, List<float3> corners, int sourceIndex, out VpStoredGeometry geometry)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            int boxes = corners.Count / 8;
            for (int box = 0; box < boxes; box++)
            {
                int corner0 = box * 8;
                for (int f = 0; f < FaceCycles.Length; f++)
                {
                    int[] cycle = FaceCycles[f];
                    float3 normal = math.normalize(math.cross(
                        corners[corner0 + cycle[1]] - corners[corner0 + cycle[0]],
                        corners[corner0 + cycle[2]] - corners[corner0 + cycle[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[]
                    {
                        new float2(0.05f, 0.1f), new float2(0.95f, 0.1f),
                        new float2(0.95f, 0.9f), new float2(0.05f, 0.9f),
                    };

                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = corners[corner0 + cycle[k]],
                            normal = normal,
                            uv0 = uv[k],
                        });
                        topology.Add(corner0 + cycle[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }
            }

            // **Appended as a cuttable geometry**, which is what the display's own cut needs: an ordinary append
            // stores the same triangles without that acceptance, and a cut of it is refused as invalid input.
            var submeshes = new[] { new VpGeometrySubmesh(0, indices.Count, sourceIndex) };
            bool taken = storage.TryAppendCuttable(
                vertices.ToArray(), indices.ToArray(), topology.ToArray(), corners.Count, submeshes, out geometry,
                out VpCutInputVerdict verdict);
            if (!taken)
            {
                Debug.LogWarning("Sandbox compound: the storage would not take the geometry as cuttable: " + verdict);
            }

            return taken;
        }

        /// <summary>The edge table of one convex's face loops, built the way the sandbox probe's box is.</summary>
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
        }

        /// <summary>
        /// Gives back what this still holds. The shape goes back only while this still owns it: once a world has
        /// taken the body, the world owns the shape and this lets go of it through <see cref="Taken"/>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Shape?.Dispose();
            Shape = null;
            foreach (Mesh mesh in _meshes)
            {
                if (mesh != null)
                {
                    // At once outside play (a test's teardown), after the update loop while playing.
                    if (UnityEngine.Application.isPlaying) UnityEngine.Object.Destroy(mesh);
                    else UnityEngine.Object.DestroyImmediate(mesh);
                }
            }

            _meshes.Clear();
            foreach (IDisposable held in _native)
            {
                held.Dispose();
            }

            _native.Clear();
        }

        /// <summary>The world has taken the body: the shape is the world's from here, and this stops owning it.</summary>
        public void Taken()
        {
            Shape = null;
        }
    }
}
