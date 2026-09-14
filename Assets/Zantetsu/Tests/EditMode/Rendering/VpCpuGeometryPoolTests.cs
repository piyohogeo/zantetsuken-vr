using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Fixed-capacity append-only CPU VP pool (DESIGN 4.5.3): ranges and index rebasing of appended meshes, views
    /// limited to the appended counts, unchanged pool on too small capacity or a rejected mesh, and disposal.
    /// </summary>
    public class VpCpuGeometryPoolTests
    {
        // Built-in mesh sizes: Quad 4 vertices / 6 indices, Cube 24 / 36.
        private const int QuadVertices = 4;
        private const int QuadIndices = 6;
        private const int CubeVertices = 24;
        private const int CubeIndices = 36;

        private readonly List<Mesh> _meshes = new List<Mesh>();

        [TearDown]
        public void DestroyMeshes()
        {
            foreach (Mesh mesh in _meshes)
            {
                Object.DestroyImmediate(mesh);
            }

            _meshes.Clear();
        }

        private static Mesh BuiltIn(string name)
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>(name);
            Assert.That(mesh, Is.Not.Null, name);
            return mesh;
        }

        private static void AssertRange(VpGeometryRange range, int vertexStart, int vertexCount, int indexStart, int indexCount)
        {
            Assert.That(
                new[] { range.vertexStart, range.vertexCount, range.indexStart, range.indexCount },
                Is.EqualTo(new[] { vertexStart, vertexCount, indexStart, indexCount }));
        }

        /// <summary>The pool contents of a range reproduce the mesh: attributes, indices less the base, triangle corners.</summary>
        private static void AssertRangeHoldsMesh(VpCpuGeometryPool pool, VpGeometryRange range, Mesh mesh)
        {
            NativeArray<VpRenderVertex>.ReadOnly vertices = pool.Vertices;
            NativeArray<uint>.ReadOnly indices = pool.Indices;
            Vector3[] positions = mesh.vertices;
            Vector3[] normals = mesh.normals;
            Vector2[] uvs = mesh.uv;
            int[] triangles = mesh.triangles;

            for (int v = 0; v < range.vertexCount; v++)
            {
                VpRenderVertex vertex = vertices[range.vertexStart + v];
                Assert.That(vertex.position, Is.EqualTo(positions[v]), "position " + v);
                Assert.That(vertex.normal, Is.EqualTo(normals[v]), "normal " + v);
                Assert.That(vertex.uv0, Is.EqualTo(uvs[v]), "uv0 " + v);
            }

            Assert.That(range.indexCount, Is.EqualTo(triangles.Length));
            for (int i = 0; i < range.indexCount; i++)
            {
                uint index = indices[range.indexStart + i];
                Assert.That(index, Is.EqualTo((uint)(range.vertexStart + triangles[i])), "index " + i);
                Assert.That(vertices[(int)index].position, Is.EqualTo(positions[triangles[i]]), "corner " + i);
            }
        }

        private static void AssertUnchanged(VpCpuGeometryPool pool, VpRenderVertex[] vertices, uint[] indices)
        {
            Assert.That(pool.VertexCount, Is.EqualTo(vertices.Length));
            Assert.That(pool.IndexCount, Is.EqualTo(indices.Length));
            Assert.That(pool.Vertices.ToArray(), Is.EqualTo(vertices));
            Assert.That(pool.Indices.ToArray(), Is.EqualTo(indices));
        }

        [Test]
        public void TheConstructor_CreatesAnEmptyPoolWithTheGivenCapacities()
        {
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            {
                Assert.That(pool.VertexCapacity, Is.EqualTo(100));
                Assert.That(pool.IndexCapacity, Is.EqualTo(200));
                Assert.That(pool.VertexCount, Is.Zero);
                Assert.That(pool.IndexCount, Is.Zero);
                Assert.That(pool.Vertices.Length, Is.Zero);
                Assert.That(pool.Indices.Length, Is.Zero);
            }
        }

        [Test]
        public void AppendingOneMesh_ReturnsItsRangeAndOnlyThatIsVisible()
        {
            Mesh cube = BuiltIn("Cube.fbx");
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            {
                Assert.That(pool.TryAppend(cube, out VpGeometryRange range), Is.True);

                AssertRange(range, 0, CubeVertices, 0, CubeIndices);
                Assert.That(pool.VertexCount, Is.EqualTo(CubeVertices));
                Assert.That(pool.IndexCount, Is.EqualTo(CubeIndices));
                Assert.That(pool.Vertices.Length, Is.EqualTo(CubeVertices));
                Assert.That(pool.Indices.Length, Is.EqualTo(CubeIndices));
                AssertRangeHoldsMesh(pool, range, cube);
            }
        }

        [Test]
        public void AppendingMoreMeshes_RebasesTheirIndicesAndKeepsEarlierRanges()
        {
            Mesh quad = BuiltIn("Quad.fbx");
            Mesh cube = BuiltIn("Cube.fbx");
            Mesh sphere = BuiltIn("Sphere.fbx");
            int sphereIndices = sphere.triangles.Length;
            using (var pool = new VpCpuGeometryPool(
                       QuadVertices + CubeVertices + sphere.vertexCount,
                       QuadIndices + CubeIndices + sphereIndices,
                       Allocator.Persistent))
            {
                Assert.That(pool.TryAppend(quad, out VpGeometryRange quadRange), Is.True);
                Assert.That(pool.TryAppend(cube, out VpGeometryRange cubeRange), Is.True);
                Assert.That(pool.TryAppend(sphere, out VpGeometryRange sphereRange), Is.True);

                AssertRange(quadRange, 0, QuadVertices, 0, QuadIndices);
                AssertRange(cubeRange, QuadVertices, CubeVertices, QuadIndices, CubeIndices);
                AssertRange(sphereRange, QuadVertices + CubeVertices, sphere.vertexCount, QuadIndices + CubeIndices, sphereIndices);
                Assert.That(pool.VertexCount, Is.EqualTo(pool.VertexCapacity));
                Assert.That(pool.IndexCount, Is.EqualTo(pool.IndexCapacity));
                AssertRangeHoldsMesh(pool, quadRange, quad);
                AssertRangeHoldsMesh(pool, cubeRange, cube);
                AssertRangeHoldsMesh(pool, sphereRange, sphere);
            }
        }

        [TestCase(QuadVertices + CubeVertices - 1, QuadIndices + CubeIndices)]
        [TestCase(QuadVertices + CubeVertices, QuadIndices + CubeIndices - 1)]
        public void TooLittleCapacity_IsRejectedWithoutChangingThePool(int vertexCapacity, int indexCapacity)
        {
            using (var pool = new VpCpuGeometryPool(vertexCapacity, indexCapacity, Allocator.Persistent))
            {
                Assert.That(pool.TryAppend(BuiltIn("Quad.fbx"), out _), Is.True);
                VpRenderVertex[] vertices = pool.Vertices.ToArray();
                uint[] indices = pool.Indices.ToArray();

                Assert.That(pool.TryAppend(BuiltIn("Cube.fbx"), out VpGeometryRange range), Is.False);

                Assert.That(range, Is.EqualTo(default(VpGeometryRange)));
                AssertUnchanged(pool, vertices, indices);
            }
        }

        [TestCase("null")]
        [TestCase("no normals")]
        [TestCase("lines")]
        public void MeshesTheConverterRejects_LeaveThePoolUnchanged(string kind)
        {
            Mesh mesh = null;
            if (kind != "null")
            {
                mesh = new Mesh();
                _meshes.Add(mesh);
                mesh.SetVertices(new[] { Vector3.zero, Vector3.right, Vector3.up });
                if (kind == "lines")
                {
                    mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
                    mesh.SetIndices(new[] { 0, 1, 1, 2 }, MeshTopology.Lines, 0);
                }
                else
                {
                    mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
                }
            }

            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            {
                Assert.That(pool.TryAppend(BuiltIn("Quad.fbx"), out _), Is.True);
                VpRenderVertex[] vertices = pool.Vertices.ToArray();
                uint[] indices = pool.Indices.ToArray();

                Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.False);

                Assert.That(range, Is.EqualTo(default(VpGeometryRange)));
                AssertUnchanged(pool, vertices, indices);
            }
        }

        [Test]
        public void AfterDispose_AppendingAndTheViewsThrowObjectDisposedException()
        {
            var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent);
            pool.Dispose();

            Assert.Throws<ObjectDisposedException>(() => pool.TryAppend(BuiltIn("Quad.fbx"), out _));
            Assert.Throws<ObjectDisposedException>(() => _ = pool.Vertices);
            Assert.Throws<ObjectDisposedException>(() => _ = pool.Indices);
            Assert.Throws<ObjectDisposedException>(() => _ = pool.AppendedVertices);
            Assert.Throws<ObjectDisposedException>(() => _ = pool.AppendedIndices);
        }

        [Test]
        public void DisposingTwice_DoesNothing()
        {
            var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent);
            pool.Dispose();

            Assert.DoesNotThrow(pool.Dispose);
        }
    }
}
