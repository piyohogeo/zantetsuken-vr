using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Copying a geometry a <see cref="VpCpuGeometryStorage"/> owns into a Unity Mesh: the attributes and the winding
    /// come across untouched, coincident vertices that differ in an attribute stay apart, the submeshes keep their
    /// order and their source material mapping, and the Mesh owes nothing to the storage once the copy is done. The
    /// cut results used here are produced by the ordinary product path, so a child's several vertex blocks and a
    /// reused index range are exercised as they really occur.
    /// </summary>
    public class VpStoredGeometryMeshTests
    {
        // An asymmetric closed hexahedron: 8 control points, one set of render vertices per face, so every control
        // point carries three coincident render vertices with different normals and uvs.
        private const int ControlPoints = 8;
        private const int RenderVertices = 24;
        private const int IndexCount = 36;
        private const int SideIndices = 24;
        private const int EndIndices = 12;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;
        private const int QuadIndexCount = 6;

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.2f, 0.0f, -1.0f), new float3(1.1f, 0.0f, 0.9f), new float3(-0.8f, 0.0f, 1.0f),
            new float3(-0.5f, 1.3f, -0.4f), new float3(0.7f, 1.3f, -0.6f), new float3(0.6f, 1.3f, 0.5f), new float3(-0.3f, 1.3f, 0.6f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private sealed class Prepared
        {
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] TopologyOfVertex;
            public VpGeometrySubmesh[] Submeshes;
            public int TopologyVertexCount;
        }

        private static Prepared BuildPrepared()
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_controlPoints[c[k]],
                            normal = n,
                            uv0 = uv[k] + new float2(f * 0.013f, f * 0.021f),
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            return new Prepared
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                TopologyOfVertex = topology.ToArray(),
                Submeshes = submeshes.ToArray(),
                TopologyVertexCount = ControlPoints,
            };
        }

        /// <summary>A small separate geometry, to push the subject off zero and to take freed space.</summary>
        private static Prepared BuildQuad(float3 offset)
        {
            var vertices = new VpRenderVertex[4];
            var topology = new int[4];
            for (int k = 0; k < 4; k++)
            {
                vertices[k] = new VpRenderVertex
                {
                    position = offset + new float3(k == 1 || k == 2 ? 1f : 0f, 0f, k >= 2 ? 1f : 0f),
                    normal = new float3(0, 1, 0),
                    uv0 = new float2(k == 1 || k == 2 ? 1f : 0f, k >= 2 ? 1f : 0f),
                };
                topology[k] = k;
            }

            return new Prepared
            {
                Vertices = vertices,
                Indices = new uint[] { 0, 1, 2, 0, 2, 3 },
                TopologyOfVertex = topology,
                Submeshes = new[] { new VpGeometrySubmesh(0, QuadIndexCount, 3) },
                TopologyVertexCount = 4,
            };
        }

        private static VpCpuGeometryStorage NewStorage(
            int vertexCapacity = 2048,
            int indexCapacity = 8192,
            int descriptorCapacity = 32,
            int submeshCapacity = 128,
            int vertexBlockCapacity = 128)
        {
            return new VpCpuGeometryStorage(vertexCapacity, indexCapacity, descriptorCapacity, submeshCapacity, vertexBlockCapacity, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendPrepared(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        /// <summary>For the subjects these display tests cut: appended as a cut input, through the gate.</summary>
        private static VpStoredGeometry AppendCuttable(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendCuttable(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry, out _),
                Is.True,
                "append as a cut input");
            return geometry;
        }

        private static float4 TiltedPlane()
        {
            float3 centre = float3.zero;
            foreach (float3 p in k_controlPoints)
            {
                centre += p;
            }

            centre /= k_controlPoints.Length;
            return SyntheticPlane(new float3(0.37f, 0.61f, -0.7f), centre + new float3(0.0071f, -0.0233f, 0.0119f));
        }

        private static float4 SecondPlane()
        {
            return SyntheticPlane(new float3(0.81f, -0.23f, 0.54f), new float3(0.0313f, 0.6217f, -0.0119f));
        }

        /// <summary>The plane through a point with a normal, in the kernel's own form.</summary>
        private static float4 SyntheticPlane(float3 normal, float3 point)
        {
            float3 n = math.normalize(normal);
            return new float4(n, -math.dot(n, point));
        }

        private static VpStorageCutResult Cut(VpCpuGeometryStorage storage, VpStoredGeometry geometry, float4 plane)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, "acquire input");
            using (input)
            {
                Assert.That(VpStorageCut.TryExecute(storage, input, plane, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                return result;
            }
        }

        private static uint[] ReadIndices(VpCpuGeometryStorage storage, VpIndexRangeHandle handle)
        {
            Assert.That(storage.TryAcquireIndexReadLease(handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, "read lease");
            uint[] indices = view.ToArray();
            Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True, "release lease");
            return indices;
        }

        /// <summary>Every triangle corner of the Mesh, in submesh order, as positions.</summary>
        private static List<Vector3> CornerPositions(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            var corners = new List<Vector3>();
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                foreach (int corner in mesh.GetTriangles(s))
                {
                    corners.Add(vertices[corner]);
                }
            }

            return corners;
        }

        /// <summary>The same corners as the storage holds them, read through the geometry's own index range.</summary>
        private static List<Vector3> StoredCornerPositions(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            uint[] indices = ReadIndices(storage, geometry.indexRange);
            NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
            var corners = new List<Vector3>();
            foreach (uint index in indices)
            {
                corners.Add(committed[(int)index].position);
            }

            return corners;
        }

        private static void DestroyMesh(Mesh mesh)
        {
            if (mesh != null)
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>No reader is left on the range: a retirement with one would leave it Retiring instead of Free.</summary>
        private static void AssertNoLeaseRemains(VpCpuGeometryStorage storage, VpStoredGeometry geometry, string label)
        {
            Assert.That(storage.TryRetireIndices(geometry.indexRange), Is.True, label + ": retire");
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _), Is.True, label + ": state");
            Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), label + ": no reader was left behind");
        }

        [Test]
        public void AGeometryAtNonZeroStarts_KeepsItsAttributesWindingAndSubmeshes()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(subject.vertexStart, Is.Not.Zero, "the vertices do not start at 0");
                Assert.That(storage.TryGetIndexState(subject.indexRange, out _, out int indexStart, out _), Is.True);
                Assert.That(indexStart, Is.Not.Zero, "the indices do not start at 0");

                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, subject, out Mesh mesh), Is.True, "create mesh");
                try
                {
                    Assert.That(mesh.vertexCount, Is.EqualTo(RenderVertices), "every render vertex is referenced and kept");
                    Assert.That(mesh.subMeshCount, Is.EqualTo(2), "the submeshes are the geometry's own");
                    Assert.That(mesh.GetTriangles(0).Length, Is.EqualTo(SideIndices), "submesh 0 keeps its index count");
                    Assert.That(mesh.GetTriangles(1).Length, Is.EqualTo(EndIndices), "submesh 1 keeps its index count");

                    // corner for corner, in order: the winding of every triangle is the stored one
                    Assert.That(CornerPositions(mesh), Is.EqualTo(StoredCornerPositions(storage, subject)), "corners in order");

                    // the attributes are the stored ones, matched through the mesh's own triangles
                    NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                    uint[] indices = ReadIndices(storage, subject.indexRange);
                    Vector3[] normals = mesh.normals;
                    Vector2[] uvs = mesh.uv;
                    int[] meshTriangles = mesh.GetTriangles(0).Concat(mesh.GetTriangles(1)).ToArray();
                    for (int i = 0; i < indices.Length; i++)
                    {
                        VpRenderVertex stored = committed[(int)indices[i]];
                        int local = meshTriangles[i];
                        Assert.That(normals[local], Is.EqualTo(stored.normal), "normal at corner " + i);
                        Assert.That(uvs[local], Is.EqualTo(stored.uv0), "uv0 at corner " + i);
                    }
                }
                finally
                {
                    DestroyMesh(mesh);
                }
            }
        }

        [Test]
        public void CoincidentVerticesThatDifferInAnAttribute_AreNotWelded()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, subject, out Mesh mesh), Is.True, "create mesh");
                try
                {
                    Vector3[] positions = mesh.vertices;
                    Vector3[] normals = mesh.normals;
                    Vector2[] uvs = mesh.uv;
                    Assert.That(positions.Length, Is.EqualTo(RenderVertices), "no vertex was merged away");
                    Assert.That(positions.Distinct().Count(), Is.EqualTo(ControlPoints), "they really are coincident");

                    // every group of coincident vertices differs in normal or uv0, which is what makes it a seam
                    foreach (IGrouping<Vector3, int> group in Enumerable.Range(0, positions.Length).GroupBy(v => positions[v]))
                    {
                        int[] members = group.ToArray();
                        Assert.That(members.Length, Is.GreaterThan(1), "each control point carries several render vertices");
                        for (int a = 0; a < members.Length; a++)
                        {
                            for (int b = a + 1; b < members.Length; b++)
                            {
                                bool differs = normals[members[a]] != normals[members[b]] || uvs[members[a]] != uvs[members[b]];
                                Assert.That(differs, Is.True, "coincident vertices " + members[a] + " and " + members[b] + " differ in an attribute");
                            }
                        }
                    }
                }
                finally
                {
                    DestroyMesh(mesh);
                }
            }
        }

        [Test]
        public void Materials_ResolveByTheStoredSourceIndexAndNeverByOrdinal()
        {
            Prepared prepared = BuildPrepared();
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Unlit/Color");
            var side = new Material(shader) { name = "side" };
            var end = new Material(shader) { name = "end" };
            try
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    VpStoredGeometry subject = Append(storage, prepared);

                    var bySource = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, end } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, bySource, out Material[] materials), Is.True, "resolve");
                    Assert.That(materials, Is.EqualTo(new[] { side, end }), "in submesh order, by stored material index");

                    // ordinals are not material indices: a map keyed by 0 and 1 resolves nothing here
                    var byOrdinal = new Dictionary<int, Material> { { 0, side }, { 1, end } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, byOrdinal, out Material[] none), Is.False, "ordinals are refused");
                    Assert.That(none, Is.Null);

                    // a missing entry fails outright rather than borrowing the first material
                    var missing = new Dictionary<int, Material> { { SideMaterial, side } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, missing, out Material[] partial), Is.False, "a missing material fails");
                    Assert.That(partial, Is.Null);

                    var withNull = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, null } };
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, withNull, out _), Is.False, "a null material fails");
                    Assert.That(VpStoredGeometryMesh.TryResolveMaterials(storage, subject, null, out _), Is.False, "no map fails");
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(side);
                UnityEngine.Object.DestroyImmediate(end);
            }
        }

        [Test]
        public void BothSidesOfACut_AndAReCutChild_Convert()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, BuildQuad(new float3(5, 5, 5)));
                VpStoredGeometry subject = AppendCuttable(storage, prepared);
                VpStorageCutResult cut = Cut(storage, subject, TiltedPlane());
                Assert.That(cut.positive.IsProduced && cut.negative.IsProduced, Is.True, "both sides");

                foreach ((VpStorageCutSide side, string label) in new[] { (cut.positive, "positive"), (cut.negative, "negative") })
                {
                    Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, side.geometry, out Mesh mesh), Is.True, label + ": create mesh");
                    try
                    {
                        Assert.That(mesh.subMeshCount, Is.EqualTo(2), label + ": submeshes kept");
                        Assert.That(CornerPositions(mesh), Is.EqualTo(StoredCornerPositions(storage, side.geometry)), label + ": corners in order");
                        Assert.That(mesh.vertexCount, Is.GreaterThan(0), label + ": vertices");
                        Assert.That(
                            VpStoredGeometryMesh.TryResolveMaterials(storage, side.geometry, new Dictionary<int, Material> { { SideMaterial, null } }, out _),
                            Is.False,
                            label + ": a null material is still refused");
                    }
                    finally
                    {
                        DestroyMesh(mesh);
                    }
                }

                // the child has the parent's block and the cut's own, and cutting it again adds a third
                VpStoredGeometry child = cut.positive.geometry;
                Assert.That(child.blockCount, Is.EqualTo(2), "the child is already multi-block");
                VpStorageCutResult second = Cut(storage, child, SecondPlane());
                VpStoredGeometry grandchild = second.positive.IsProduced ? second.positive.geometry : second.negative.geometry;
                Assert.That(grandchild.blockCount, Is.GreaterThanOrEqualTo(2), "the grandchild names several blocks");

                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, grandchild, out Mesh grandchildMesh), Is.True, "create the grandchild mesh");
                try
                {
                    Assert.That(CornerPositions(grandchildMesh), Is.EqualTo(StoredCornerPositions(storage, grandchild)), "grandchild corners in order");

                    // vertices really come from more than one block: the mesh holds positions from the original
                    // geometry as well as ones the cuts created
                    Assert.That(storage.TryGetVertexBlocks(grandchild, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True);
                    NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                    var meshPositions = new HashSet<Vector3>(grandchildMesh.vertices);
                    int blocksSeen = 0;
                    foreach (VpGeometryVertexBlock block in blocks)
                    {
                        for (int v = 0; v < block.vertexCount; v++)
                        {
                            if (meshPositions.Contains(committed[block.vertexStart + v].position))
                            {
                                blocksSeen++;
                                break;
                            }
                        }
                    }

                    Assert.That(blocksSeen, Is.GreaterThan(1), "the mesh draws on more than one block");
                }
                finally
                {
                    DestroyMesh(grandchildMesh);
                }
            }
        }

        [Test]
        public void AfterAnIndexRangeIsReused_TheCopyReadsTheGeometryThatOwnsItNow()
        {
            Prepared prepared = BuildPrepared();
            Prepared quad = BuildQuad(new float3(9, 0, 9));
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, prepared);
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True, "retire the first");

                // the quad's indices land in the space the hexahedron gave back
                VpStoredGeometry reuser = Append(storage, quad);
                Assert.That(storage.TryGetIndexState(reuser.indexRange, out _, out int start, out _), Is.True);
                Assert.That(start, Is.Zero, "the freed space is reused");

                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, reuser, out Mesh mesh), Is.True, "create mesh");
                try
                {
                    Assert.That(mesh.vertexCount, Is.EqualTo(4), "the quad's vertices, not the hexahedron's");
                    Assert.That(mesh.subMeshCount, Is.EqualTo(1));
                    Assert.That(CornerPositions(mesh), Is.EqualTo(StoredCornerPositions(storage, reuser)), "corners in order");
                }
                finally
                {
                    DestroyMesh(mesh);
                }

                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, first, out Mesh gone), Is.False, "the retired geometry is refused");
                Assert.That(gone, Is.Null);
            }
        }

        [Test]
        public void RetiringTheGeometryAfterTheCopy_LeavesTheMeshAsItWas()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, subject, out Mesh mesh), Is.True, "create mesh");
                try
                {
                    Vector3[] positionsBefore = mesh.vertices;
                    Vector3[] normalsBefore = mesh.normals;
                    Vector2[] uvsBefore = mesh.uv;
                    int[] submesh0Before = mesh.GetTriangles(0);
                    int[] submesh1Before = mesh.GetTriangles(1);
                    Bounds boundsBefore = mesh.bounds;

                    // the storage moves on: the geometry is retired and its index space handed to someone else
                    Assert.That(storage.TryRetireIndices(subject.indexRange), Is.True, "retire");
                    Append(storage, BuildQuad(new float3(2, 2, 2)));

                    Assert.That(mesh.vertices, Is.EqualTo(positionsBefore), "positions unchanged");
                    Assert.That(mesh.normals, Is.EqualTo(normalsBefore), "normals unchanged");
                    Assert.That(mesh.uv, Is.EqualTo(uvsBefore), "uvs unchanged");
                    Assert.That(mesh.GetTriangles(0), Is.EqualTo(submesh0Before), "submesh 0 unchanged");
                    Assert.That(mesh.GetTriangles(1), Is.EqualTo(submesh1Before), "submesh 1 unchanged");
                    Assert.That(mesh.bounds, Is.EqualTo(boundsBefore), "bounds unchanged");
                }
                finally
                {
                    DestroyMesh(mesh);
                }
            }
        }

        [Test]
        public void SuccessAndRefusal_LeaveNoLeaseAndChangeNeitherStorageNorInput()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                VpStoredGeometry foreign = Append(other, prepared);
                int vertexCount = storage.VertexCount;
                int submeshCount = storage.SubmeshCount;
                int blockCount = storage.VertexBlockCount;
                uint[] indicesBefore = ReadIndices(storage, subject.indexRange);

                // a successful copy leaves nothing behind
                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, subject, out Mesh mesh), Is.True, "create mesh");
                DestroyMesh(mesh);
                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "no vertex was committed");
                Assert.That(storage.SubmeshCount, Is.EqualTo(submeshCount), "no submesh was committed");
                Assert.That(storage.VertexBlockCount, Is.EqualTo(blockCount), "no block was committed");
                Assert.That(ReadIndices(storage, subject.indexRange), Is.EqualTo(indicesBefore), "the indices are unchanged");

                // and so do the refusals
                Assert.That(VpStoredGeometryMesh.TryCreateMesh(null, subject, out Mesh noStorage), Is.False, "a null storage");
                Assert.That(noStorage, Is.Null);
                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, default, out Mesh defaultGeometry), Is.False, "a default geometry");
                Assert.That(defaultGeometry, Is.Null);
                Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, foreign, out Mesh foreignMesh), Is.False, "another storage's geometry");
                Assert.That(foreignMesh, Is.Null);
                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "the refusals committed nothing");
                Assert.That(ReadIndices(storage, subject.indexRange), Is.EqualTo(indicesBefore), "and changed no index");

                // no reader is left on either storage: both ranges retire straight to Free
                AssertNoLeaseRemains(storage, subject, "after a copy and refusals");
                AssertNoLeaseRemains(other, foreign, "the untouched foreign geometry");
            }
        }

        [Test]
        public void AGeometryWithoutATopologyMapping_IsRefusedRatherThanGuessedAt()
        {
            // A Unity Mesh appended straight into the storage carries no topology mapping, and the block list is read
            // through that mapping, so such a geometry is outside this conversion. The intake path this display serves
            // always has one. Recorded here so the limit is a decision and not an accident.
            var mesh = new Mesh();
            mesh.SetVertices(new List<Vector3> { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(1, 0, 1) });
            mesh.SetNormals(new List<Vector3> { Vector3.up, Vector3.up, Vector3.up });
            mesh.SetUVs(0, new List<Vector2> { Vector2.zero, Vector2.right, Vector2.one });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            try
            {
                using (VpCpuGeometryStorage storage = NewStorage())
                {
                    Assert.That(storage.TryAppend(mesh, out VpStoredGeometry appended), Is.True, "append the mesh");
                    Assert.That(appended.hasTopology, Is.False, "a Unity Mesh brings no topology mapping");
                    Assert.That(VpStoredGeometryMesh.TryCreateMesh(storage, appended, out Mesh copy), Is.False, "and is refused");
                    Assert.That(copy, Is.Null);
                    AssertNoLeaseRemains(storage, appended, "after the refusal");
                }
            }
            finally
            {
                DestroyMesh(mesh);
            }
        }
    }
}
