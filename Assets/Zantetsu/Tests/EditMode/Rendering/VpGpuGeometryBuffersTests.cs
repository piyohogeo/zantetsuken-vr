using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// GPU copy of the VP CPU pool (DESIGN 4.5.4): structured buffers with the VP strides, uploads read back with
    /// GraphicsBuffer.GetData to match the pool, unchanged buffers on too little capacity, and disposal.
    /// </summary>
    public class VpGpuGeometryBuffersTests
    {
        // Built-in mesh sizes: Quad 4 vertices / 6 indices, Cube 24 / 36.
        private const int QuadVertices = 4;
        private const int QuadIndices = 6;
        private const int CubeVertices = 24;
        private const int CubeIndices = 36;

        private static Mesh BuiltIn(string name)
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>(name);
            Assert.That(mesh, Is.Not.Null, name);
            return mesh;
        }

        private static void Append(VpCpuGeometryPool pool, string builtIn)
        {
            Assert.That(pool.TryAppend(BuiltIn(builtIn), out _), Is.True, builtIn);
        }

        private static VpRenderVertex[] ReadVertices(VpGpuGeometryBuffers buffers, int count)
        {
            var vertices = new VpRenderVertex[count];
            buffers.VertexBuffer.GetData(vertices, 0, 0, count);
            return vertices;
        }

        private static uint[] ReadIndices(VpGpuGeometryBuffers buffers, int count)
        {
            var indices = new uint[count];
            buffers.IndexBuffer.GetData(indices, 0, 0, count);
            return indices;
        }

        private static void AssertBuffersMatch(VpGpuGeometryBuffers buffers, VpCpuGeometryPool pool)
        {
            Assert.That(ReadVertices(buffers, pool.VertexCount), Is.EqualTo(pool.Vertices.ToArray()));
            Assert.That(ReadIndices(buffers, pool.IndexCount), Is.EqualTo(pool.Indices.ToArray()));
        }

        [Test]
        public void TheBuffers_AreStructuredWithTheVpStridesAndTheGivenCapacities()
        {
            using (var buffers = new VpGpuGeometryBuffers(100, 200))
            {
                Assert.That(buffers.VertexCapacity, Is.EqualTo(100));
                Assert.That(buffers.IndexCapacity, Is.EqualTo(200));
                Assert.That(buffers.VertexBuffer.target, Is.EqualTo(GraphicsBuffer.Target.Structured));
                Assert.That(buffers.IndexBuffer.target, Is.EqualTo(GraphicsBuffer.Target.Structured));
                Assert.That(buffers.VertexBuffer.stride, Is.EqualTo(VpRenderVertex.Stride));
                Assert.That(buffers.IndexBuffer.stride, Is.EqualTo(sizeof(uint)));
                Assert.That(buffers.VertexBuffer.count, Is.EqualTo(100));
                Assert.That(buffers.IndexBuffer.count, Is.EqualTo(200));
            }
        }

        [Test]
        public void UploadingAnEmptyPool_Succeeds()
        {
            using (var pool = new VpCpuGeometryPool(10, 10, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(10, 10))
            {
                Assert.That(buffers.TryUpload(pool), Is.True);
            }
        }

        [Test]
        public void UploadingOneMesh_ReadsBackThePoolContents()
        {
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(100, 200))
            {
                Append(pool, "Cube.fbx");

                Assert.That(buffers.TryUpload(pool), Is.True);

                AssertBuffersMatch(buffers, pool);
            }
        }

        [Test]
        public void UploadingSeveralMeshes_ReadsBackTheirRebasedIndices()
        {
            Mesh cube = BuiltIn("Cube.fbx");
            using (var pool = new VpCpuGeometryPool(2000, 5000, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(2000, 5000))
            {
                Append(pool, "Quad.fbx");
                Append(pool, "Cube.fbx");
                Append(pool, "Sphere.fbx");

                Assert.That(buffers.TryUpload(pool), Is.True);

                AssertBuffersMatch(buffers, pool);
                uint[] cubeIndices = new uint[CubeIndices];
                buffers.IndexBuffer.GetData(cubeIndices, 0, QuadIndices, CubeIndices);
                int[] triangles = cube.triangles;
                for (int i = 0; i < CubeIndices; i++)
                {
                    Assert.That(cubeIndices[i], Is.EqualTo((uint)(QuadVertices + triangles[i])), "cube index " + i);
                }
            }
        }

        [TestCase(QuadVertices + CubeVertices - 1, QuadIndices + CubeIndices)]
        [TestCase(QuadVertices + CubeVertices, QuadIndices + CubeIndices - 1)]
        public void TooLittleCapacity_ReturnsFalseAndKeepsTheBuffersAndTheirContents(int vertexCapacity, int indexCapacity)
        {
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(vertexCapacity, indexCapacity))
            {
                Append(pool, "Quad.fbx");
                Assert.That(buffers.TryUpload(pool), Is.True);
                VpRenderVertex[] quadVertices = pool.Vertices.ToArray();
                uint[] quadIndices = pool.Indices.ToArray();
                GraphicsBuffer vertexBuffer = buffers.VertexBuffer;
                GraphicsBuffer indexBuffer = buffers.IndexBuffer;
                Append(pool, "Cube.fbx");

                Assert.That(buffers.TryUpload(pool), Is.False);

                Assert.That(buffers.VertexBuffer, Is.SameAs(vertexBuffer));
                Assert.That(buffers.IndexBuffer, Is.SameAs(indexBuffer));
                Assert.That(buffers.VertexBuffer.count, Is.EqualTo(vertexCapacity));
                Assert.That(buffers.IndexBuffer.count, Is.EqualTo(indexCapacity));
                Assert.That(ReadVertices(buffers, QuadVertices), Is.EqualTo(quadVertices));
                Assert.That(ReadIndices(buffers, QuadIndices), Is.EqualTo(quadIndices));
            }
        }

        [Test]
        public void AfterDispose_UploadingAndTheBuffersThrowAndTheBuffersAreReleased()
        {
            using (var pool = new VpCpuGeometryPool(10, 10, Allocator.Persistent))
            {
                var buffers = new VpGpuGeometryBuffers(10, 10);
                GraphicsBuffer vertexBuffer = buffers.VertexBuffer;
                GraphicsBuffer indexBuffer = buffers.IndexBuffer;
                buffers.Dispose();

                Assert.That(vertexBuffer.IsValid(), Is.False);
                Assert.That(indexBuffer.IsValid(), Is.False);
                Assert.Throws<ObjectDisposedException>(() => buffers.TryUpload(pool));
                Assert.Throws<ObjectDisposedException>(() => _ = buffers.VertexBuffer);
                Assert.Throws<ObjectDisposedException>(() => _ = buffers.IndexBuffer);
            }
        }

        [Test]
        public void DisposingTwice_DoesNothing()
        {
            var buffers = new VpGpuGeometryBuffers(10, 10);
            buffers.Dispose();

            Assert.DoesNotThrow(buffers.Dispose);
        }
    }
}
