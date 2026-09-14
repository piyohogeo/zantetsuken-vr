using System;
using System.Collections;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// GPU copy of the VP CPU pool (DESIGN 4.5.4): structured buffers with the VP strides, uploads read back with
    /// GraphicsBuffer.GetData to match the pool, unchanged buffers on too little capacity, growth into larger buffers
    /// that hold the whole pool, rejected growth that keeps the active buffers, one replaced pair at a time released
    /// once, only after the async GPU readbacks of both of its buffers have completed, and disposal.
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

        // Growth scenarios start from buffers that hold exactly the quad, with the cube then appended to the pool.
        private const int GrownVertices = 64;
        private const int GrownIndices = 128;

        private static VpGpuGeometryBuffers QuadBuffersWithCubeAppended(VpCpuGeometryPool pool)
        {
            var buffers = new VpGpuGeometryBuffers(QuadVertices, QuadIndices);
            Append(pool, "Quad.fbx");
            Assert.That(buffers.TryUpload(pool), Is.True, "the quad fits");
            Append(pool, "Cube.fbx");
            Assert.That(buffers.TryUpload(pool), Is.False, "quad and cube do not fit");
            return buffers;
        }

        [Test]
        public void TheGraphicsDevice_SupportsAsyncGpuReadback()
        {
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True, SystemInfo.graphicsDeviceType.ToString());
        }

        [Test]
        public void Growing_UploadsTheWholePoolIntoLargerBuffersAndSwitchesToThem()
        {
            Mesh cube = BuiltIn("Cube.fbx");
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            using (VpGpuGeometryBuffers buffers = QuadBuffersWithCubeAppended(pool))
            {
                GraphicsBuffer smallVertices = buffers.VertexBuffer;
                GraphicsBuffer smallIndices = buffers.IndexBuffer;

                Assert.That(buffers.TryGrow(pool, GrownVertices, GrownIndices), Is.True);

                Assert.That(buffers.VertexBuffer, Is.Not.SameAs(smallVertices), "active vertex buffer switched");
                Assert.That(buffers.IndexBuffer, Is.Not.SameAs(smallIndices), "active index buffer switched");
                Assert.That(new[] { buffers.VertexCapacity, buffers.IndexCapacity }, Is.EqualTo(new[] { GrownVertices, GrownIndices }));
                Assert.That(new[] { buffers.VertexBuffer.count, buffers.IndexBuffer.count }, Is.EqualTo(new[] { GrownVertices, GrownIndices }));
                AssertBuffersMatch(buffers, pool);

                // The cube's range still starts after the quad and its indices still address the quad-offset vertices.
                var cubeIndices = new uint[CubeIndices];
                buffers.IndexBuffer.GetData(cubeIndices, 0, QuadIndices, CubeIndices);
                int[] triangles = cube.triangles;
                for (int i = 0; i < CubeIndices; i++)
                {
                    Assert.That(cubeIndices[i], Is.EqualTo((uint)(QuadVertices + triangles[i])), "cube index " + i);
                }

                Assert.That(smallVertices.IsValid() && smallIndices.IsValid(), Is.True, "the replaced pair stays valid until released");
            }
        }

        [TestCase(QuadVertices, GrownIndices)]
        [TestCase(GrownVertices, QuadIndices)]
        [TestCase(QuadVertices + CubeVertices - 1, GrownIndices)]
        [TestCase(GrownVertices, QuadIndices + CubeIndices - 1)]
        public void GrowingToCapacitiesNotLargerOrTooSmallForThePool_FailsAndKeepsTheActiveBuffers(int vertexCapacity, int indexCapacity)
        {
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            using (VpGpuGeometryBuffers buffers = QuadBuffersWithCubeAppended(pool))
            {
                VpRenderVertex[] quadVertices = pool.Vertices.ToArray().AsSpan(0, QuadVertices).ToArray();
                uint[] quadIndices = pool.Indices.ToArray().AsSpan(0, QuadIndices).ToArray();
                GraphicsBuffer vertexBuffer = buffers.VertexBuffer;
                GraphicsBuffer indexBuffer = buffers.IndexBuffer;

                Assert.That(buffers.TryGrow(pool, vertexCapacity, indexCapacity), Is.False);

                Assert.That(buffers.VertexBuffer, Is.SameAs(vertexBuffer));
                Assert.That(buffers.IndexBuffer, Is.SameAs(indexBuffer));
                Assert.That(new[] { buffers.VertexCapacity, buffers.IndexCapacity }, Is.EqualTo(new[] { QuadVertices, QuadIndices }));
                Assert.That(vertexBuffer.IsValid() && indexBuffer.IsValid(), Is.True);
                Assert.That(ReadVertices(buffers, QuadVertices), Is.EqualTo(quadVertices));
                Assert.That(ReadIndices(buffers, QuadIndices), Is.EqualTo(quadIndices));
                Assert.That(buffers.TryReleaseRetired(), Is.False, "a failed growth replaces nothing");
                Assert.That(buffers.TryGrow(pool, GrownVertices, GrownIndices), Is.True, "a valid growth still succeeds");
            }
        }

        [Test]
        public void GrowingAgainWhileTheReplacedPairIsHeld_IsRejected()
        {
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            using (VpGpuGeometryBuffers buffers = QuadBuffersWithCubeAppended(pool))
            {
                Assert.That(buffers.TryGrow(pool, GrownVertices, GrownIndices), Is.True);
                GraphicsBuffer grownVertices = buffers.VertexBuffer;

                Assert.That(buffers.TryGrow(pool, GrownVertices * 2, GrownIndices * 2), Is.False);

                Assert.That(buffers.VertexBuffer, Is.SameAs(grownVertices));
                Assert.That(new[] { buffers.VertexCapacity, buffers.IndexCapacity }, Is.EqualTo(new[] { GrownVertices, GrownIndices }));
                AssertBuffersMatch(buffers, pool);
            }
        }

        [UnityTest]
        public IEnumerator TheReplacedPair_IsReleasedOnceAfterTheReadbacksOfBothOfItsBuffersComplete()
        {
            Assert.That(SystemInfo.supportsAsyncGPUReadback, Is.True, SystemInfo.graphicsDeviceType.ToString());
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            using (VpGpuGeometryBuffers buffers = QuadBuffersWithCubeAppended(pool))
            {
                GraphicsBuffer smallVertices = buffers.VertexBuffer;
                GraphicsBuffer smallIndices = buffers.IndexBuffer;
                Assert.That(buffers.TryGrow(pool, GrownVertices, GrownIndices), Is.True);
                Assert.That(smallVertices.IsValid() && smallIndices.IsValid(), Is.True, "the replaced pair is valid right after the switch");

                // The readbacks may already be complete. Only while they are pending must the pair be kept; the release
                // decision is theirs, not a frame count, so every frame simply asks again.
                bool released = buffers.TryReleaseRetired();
                bool releasedAtOnce = released;
                int framesWaited = 0;
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                while (!released)
                {
                    Assert.That(smallVertices.IsValid() && smallIndices.IsValid(), Is.True, "kept while its readbacks are pending");
                    Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(10.0), "the readbacks complete within ten seconds");
                    yield return null;
                    framesWaited++;
                    released = buffers.TryReleaseRetired();
                }

                TestContext.Out.WriteLine("replaced pair released " + (releasedAtOnce ? "at once" : "after " + framesWaited + " editor frames"));
                Assert.That(smallVertices.IsValid(), Is.False, "replaced vertex buffer released");
                Assert.That(smallIndices.IsValid(), Is.False, "replaced index buffer released");
                Assert.That(buffers.VertexBuffer.IsValid() && buffers.IndexBuffer.IsValid(), Is.True, "the active pair stays valid");
                Assert.That(buffers.TryReleaseRetired(), Is.False, "releasing again does nothing");
                AssertBuffersMatch(buffers, pool);
                Assert.That(buffers.TryGrow(pool, GrownVertices * 2, GrownIndices * 2), Is.True, "growth is possible again");
            }
        }

        [Test]
        public void DisposingWhileTheReplacedPairIsHeld_ReleasesEveryBufferOnce()
        {
            using (var pool = new VpCpuGeometryPool(100, 200, Allocator.Persistent))
            {
                VpGpuGeometryBuffers buffers = QuadBuffersWithCubeAppended(pool);
                GraphicsBuffer smallVertices = buffers.VertexBuffer;
                GraphicsBuffer smallIndices = buffers.IndexBuffer;
                Assert.That(buffers.TryGrow(pool, GrownVertices, GrownIndices), Is.True);
                GraphicsBuffer grownVertices = buffers.VertexBuffer;
                GraphicsBuffer grownIndices = buffers.IndexBuffer;

                buffers.Dispose();

                Assert.That(new[] { smallVertices.IsValid(), smallIndices.IsValid(), grownVertices.IsValid(), grownIndices.IsValid() }, Is.All.False);
                Assert.DoesNotThrow(buffers.Dispose, "disposing again does nothing");
                Assert.Throws<ObjectDisposedException>(() => buffers.TryReleaseRetired());
                Assert.Throws<ObjectDisposedException>(() => buffers.TryGrow(pool, 256, 512));
            }
        }
    }
}
