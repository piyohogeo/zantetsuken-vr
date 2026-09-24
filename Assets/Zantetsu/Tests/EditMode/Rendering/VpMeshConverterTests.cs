using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Mesh to VP vertex / index conversion (DESIGN 4.5.2): built-in meshes, submeshes appended in order with their
    /// base vertex applied, uint indices beyond 16 bits, ignored tangents, zero uv0, and rejection without writing
    /// for a null mesh, missing normals, non-triangle submeshes and too small arrays.
    /// </summary>
    public class VpMeshConverterTests
    {
        private static readonly Vector3[] Positions =
        {
            new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
            new Vector3(2f, 0f, 1f), new Vector3(3f, 0f, 1f), new Vector3(2f, 1f, 1f),
        };

        private static readonly Vector3[] Normals =
        {
            Vector3.back, Vector3.back, Vector3.back,
            Vector3.up, Vector3.up, Vector3.up,
        };

        private static readonly Vector2[] Uvs =
        {
            new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f),
            new Vector2(0.25f, 0.5f), new Vector2(0.75f, 0.5f), new Vector2(0.25f, 1f),
        };

        private static readonly VpRenderVertex Sentinel = new VpRenderVertex { position = new Vector3(99f, 99f, 99f) };
        private const uint SentinelIndex = 0xDEADBEEF;

        private readonly List<Mesh> _meshes = new List<Mesh>();

        [Test]
        public void BuiltInSphere_SourceDomainDiagnostic()
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>("Sphere.fbx");
            Debug.Log($"Compact16uv sphere source: u={mesh.uv.Min(x=>x.x):R}..{mesh.uv.Max(x=>x.x):R}, v={mesh.uv.Min(x=>x.y):R}..{mesh.uv.Max(x=>x.y):R}, zeroNormals={mesh.normals.Count(x=>x.sqrMagnitude<=0)}");
        }

        [TearDown]
        public void DestroyMeshes()
        {
            foreach (Mesh mesh in _meshes)
            {
                Object.DestroyImmediate(mesh);
            }

            _meshes.Clear();
        }

        /// <summary>Two submeshes: one triangle at base vertex 0, then two triangles written at base vertex 3.</summary>
        private Mesh TwoSubMeshMesh(bool normals = true, bool uvs = true, bool tangents = false)
        {
            var mesh = new Mesh();
            _meshes.Add(mesh);
            mesh.SetVertices(Positions);
            if (normals)
            {
                mesh.SetNormals(Normals);
            }

            if (uvs)
            {
                mesh.SetUVs(0, Uvs);
            }

            if (tangents)
            {
                mesh.SetTangents(Enumerable.Repeat(new Vector4(1f, 0f, 0f, -1f), Positions.Length).ToArray());
            }

            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0, true, 0);
            mesh.SetTriangles(new[] { 0, 1, 2, 2, 1, 0 }, 1, true, 3);
            return mesh;
        }

        private static int IndexCount(Mesh mesh)
        {
            int count = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                count += (int)mesh.GetIndexCount(s);
            }

            return count;
        }

        private static NativeArray<VpRenderVertex> SentinelVertices(int length)
        {
            var vertices = new NativeArray<VpRenderVertex>(length, Allocator.Persistent);
            for (int i = 0; i < length; i++)
            {
                vertices[i] = Sentinel;
            }

            return vertices;
        }

        private static NativeArray<uint> SentinelIndices(int length)
        {
            var indices = new NativeArray<uint>(length, Allocator.Persistent);
            for (int i = 0; i < length; i++)
            {
                indices[i] = SentinelIndex;
            }

            return indices;
        }

        private static void ConvertExactly(Mesh mesh, out VpRenderVertex[] vertices, out uint[] indices)
        {
            using (var vertexPool = new NativeArray<VpRenderVertex>(mesh.vertexCount, Allocator.Persistent))
            using (var indexPool = new NativeArray<uint>(IndexCount(mesh), Allocator.Persistent))
            {
                Assert.That(VpMeshConverter.TryConvert(mesh, vertexPool, indexPool, out int vertexCount, out int indexCount), Is.True);
                Assert.That(vertexCount, Is.EqualTo(mesh.vertexCount));
                Assert.That(indexCount, Is.EqualTo(IndexCount(mesh)));
                vertices = vertexPool.ToArray();
                indices = indexPool.ToArray();
            }
        }

        private static void AssertAttributes(VpRenderVertex[] vertices, Vector3[] positions, Vector3[] normals, Vector2[] uvs)
        {
            Assert.That(vertices.Select(v => v.position), Is.EqualTo(positions));
            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(Vector3.Angle(vertices[i].normal, normals[i]), Is.LessThan(1f));
                Assert.That(Mathf.Abs(vertices[i].uv0.x - uvs[i].x), Is.LessThanOrEqualTo(1f / 512f + VpRenderVertex.UvEndpointTolerance));
                Assert.That(Mathf.Abs(vertices[i].uv0.y - uvs[i].y), Is.LessThanOrEqualTo(1f / 512f + VpRenderVertex.UvEndpointTolerance));
            }
        }

        [TestCase("Quad.fbx")]
        [TestCase("Cube.fbx")]
        [TestCase("Sphere.fbx")]
        public void BuiltInMeshes_ConvertAttributesAndRestoreTheirTriangles(string builtIn)
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>(builtIn);
            Assert.That(mesh, Is.Not.Null, builtIn);

            ConvertExactly(mesh, out VpRenderVertex[] vertices, out uint[] indices);

            AssertAttributes(vertices, mesh.vertices, mesh.normals, mesh.uv);
            int[] triangles = mesh.triangles;
            Assert.That(indices, Is.EqualTo(triangles.Select(i => (uint)i)));
            Vector3[] sourcePositions = mesh.vertices;
            for (int i = 0; i < triangles.Length; i++)
            {
                Assert.That(vertices[indices[i]].position, Is.EqualTo(sourcePositions[triangles[i]]), "corner " + i);
            }
        }

        [TestCase("Cube.fbx")]
        [TestCase("Sphere.fbx")]
        public void ConvertingAcquiredMeshData_MatchesConvertingTheMeshAndLeavesTheDataToTheCaller(string builtIn)
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>(builtIn);
            Assert.That(mesh, Is.Not.Null, builtIn);
            ConvertExactly(mesh, out VpRenderVertex[] expectedVertices, out uint[] expectedIndices);

            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            using (var vertexPool = new NativeArray<VpRenderVertex>(mesh.vertexCount, Allocator.Persistent))
            using (var indexPool = new NativeArray<uint>(IndexCount(mesh), Allocator.Persistent))
            {
                Assert.That(VpMeshConverter.TryConvert(dataArray[0], vertexPool, indexPool, out int vertexCount, out int indexCount), Is.True);

                Assert.That(vertexCount, Is.EqualTo(mesh.vertexCount));
                Assert.That(indexCount, Is.EqualTo(IndexCount(mesh)));
                Assert.That(vertexPool.ToArray(), Is.EqualTo(expectedVertices));
                Assert.That(indexPool.ToArray(), Is.EqualTo(expectedIndices));
                Assert.That(dataArray[0].vertexCount, Is.EqualTo(mesh.vertexCount), "the acquired data stays usable after the conversion");
            }
        }

        [Test]
        public void SubMeshes_AreAppendedInOrderWithTheirBaseVertexApplied()
        {
            Mesh mesh = TwoSubMeshMesh();

            using (NativeArray<VpRenderVertex> vertexPool = SentinelVertices(Positions.Length + 2))
            using (NativeArray<uint> indexPool = SentinelIndices(9 + 2))
            {
                Assert.That(VpMeshConverter.TryConvert(mesh, vertexPool, indexPool, out int vertexCount, out int indexCount), Is.True);

                Assert.That(vertexCount, Is.EqualTo(6));
                Assert.That(indexCount, Is.EqualTo(9));
                Assert.That(indexPool.ToArray(), Is.EqualTo(new uint[] { 0, 1, 2, 3, 4, 5, 5, 4, 3, SentinelIndex, SentinelIndex }));
                AssertAttributes(vertexPool.GetSubArray(0, vertexCount).ToArray(), Positions, Normals, Uvs);
                Assert.That(vertexPool.GetSubArray(vertexCount, 2).ToArray(), Is.EqualTo(new[] { Sentinel, Sentinel }));
            }
        }

        [Test]
        public void Indices_AboveSixteenBits_ArePreservedAsUint()
        {
            const int vertexCount = 65540;
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            _meshes.Add(mesh);
            mesh.SetVertices(Enumerable.Range(0, vertexCount).Select(i => new Vector3(i, 0f, 0f)).ToArray());
            mesh.SetNormals(Enumerable.Repeat(Vector3.up, vertexCount).ToArray());
            mesh.SetTriangles(new[] { 0, 1, 2, 65537, 65539, 65538 }, 0);

            ConvertExactly(mesh, out VpRenderVertex[] vertices, out uint[] indices);

            Assert.That(indices, Is.EqualTo(new uint[] { 0, 1, 2, 65537, 65539, 65538 }));
            Assert.That(vertices[indices[4]].position, Is.EqualTo(new Vector3(65539f, 0f, 0f)));
        }

        [Test]
        public void Tangents_AreIgnored()
        {
            Mesh mesh = TwoSubMeshMesh(tangents: true);
            Assert.That(mesh.HasVertexAttribute(VertexAttribute.Tangent), Is.True);

            ConvertExactly(mesh, out VpRenderVertex[] vertices, out uint[] indices);

            AssertAttributes(vertices, Positions, Normals, Uvs);
            Assert.That(indices, Is.EqualTo(new uint[] { 0, 1, 2, 3, 4, 5, 5, 4, 3 }));
        }

        [Test]
        public void MissingUv0_IsWrittenAsTheFirstByteCentre()
        {
            Mesh mesh = TwoSubMeshMesh(uvs: false);
            Assert.That(mesh.HasVertexAttribute(VertexAttribute.TexCoord0), Is.False);

            ConvertExactly(mesh, out VpRenderVertex[] vertices, out _);

            AssertAttributes(vertices, Positions, Normals, Enumerable.Repeat(Vector2.zero, Positions.Length).ToArray());
        }

        [Test]
        public void MissingNormals_AreRejectedWithoutWritingOrRepairingTheMesh()
        {
            Mesh mesh = TwoSubMeshMesh(normals: false);

            AssertRejectedWithoutWriting(mesh, Positions.Length, 9, 6, 9);

            Assert.That(mesh.HasVertexAttribute(VertexAttribute.Normal), Is.False);
        }

        [TestCase(-0.001f)]
        [TestCase(1.001f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void UnsupportedUv_IsRejectedBeforeAnyDestinationWrite(float component)
        {
            Mesh mesh = TwoSubMeshMesh();
            Vector2[] uv = mesh.uv; uv[uv.Length - 1] = new Vector2(component, .5f); mesh.uv = uv;
            AssertRejectedWithoutWriting(mesh, Positions.Length, 9, 6, 9);
        }

        [Test]
        public void ZeroNormal_IsRejectedBeforeAnyDestinationWrite()
        {
            Mesh mesh = TwoSubMeshMesh();
            Vector3[] normals = mesh.normals; normals[normals.Length - 1] = Vector3.zero; mesh.normals = normals;
            AssertRejectedWithoutWriting(mesh, Positions.Length, 9, 6, 9);
        }

        [Test]
        public void ANullMesh_IsRejectedWithoutWriting()
        {
            AssertRejectedWithoutWriting(null, 1, 3, 0, 0);
        }

        [TestCase(5, 9)]
        [TestCase(6, 8)]
        public void TooSmallArrays_AreRejectedWithoutWriting(int vertexCapacity, int indexCapacity)
        {
            AssertRejectedWithoutWriting(TwoSubMeshMesh(), vertexCapacity, indexCapacity, 6, 9);
        }

        [Test]
        public void NonTriangleSubMeshes_AreRejectedWithoutWriting()
        {
            Mesh mesh = TwoSubMeshMesh();
            mesh.SetIndices(new[] { 0, 1, 1, 2 }, MeshTopology.Lines, 1, true, 3);

            AssertRejectedWithoutWriting(mesh, Positions.Length, 16, 6, 7);
        }

        private static void AssertRejectedWithoutWriting(
            Mesh mesh,
            int vertexCapacity,
            int indexCapacity,
            int requiredVertices,
            int requiredIndices)
        {
            using (NativeArray<VpRenderVertex> vertexPool = SentinelVertices(vertexCapacity))
            using (NativeArray<uint> indexPool = SentinelIndices(indexCapacity))
            {
                Assert.That(VpMeshConverter.TryConvert(mesh, vertexPool, indexPool, out int vertexCount, out int indexCount), Is.False);

                Assert.That(vertexCount, Is.EqualTo(requiredVertices));
                Assert.That(indexCount, Is.EqualTo(requiredIndices));
                Assert.That(vertexPool.ToArray(), Is.All.EqualTo(Sentinel));
                Assert.That(indexPool.ToArray(), Is.All.EqualTo(SentinelIndex));
            }
        }
    }
}
