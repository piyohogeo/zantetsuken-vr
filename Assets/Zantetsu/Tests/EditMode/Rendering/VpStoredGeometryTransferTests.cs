using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Transfers that read the storage's own memory and put it at the same position in the GPU buffer: a vertex range
    /// at its own global numbers, one published index range where it lies, and the two adjacent ranges of a cut as one
    /// run. What is checked here is that nothing is copied to the wrong place — a source offset added twice would put a
    /// range twice as far along — that a refusal transfers nothing at all, and that the leases a transfer takes are
    /// given back, the first one included when the second cannot be taken.
    /// </summary>
    public class VpStoredGeometryTransferTests
    {
        // Built-in Quad: 4 vertices / 6 indices.
        private const int QuadVertices = 4;
        private const int QuadIndices = 6;
        private const uint IndexSentinel = 0xDEADBEEF;

        private readonly List<Mesh> _meshes = new List<Mesh>();
        private readonly List<GraphicsBuffer> _buffers = new List<GraphicsBuffer>();

        [TearDown]
        public void DestroyCreated()
        {
            foreach (GraphicsBuffer buffer in _buffers)
            {
                buffer?.Dispose();
            }

            _buffers.Clear();
            foreach (Mesh mesh in _meshes)
            {
                Object.DestroyImmediate(mesh);
            }

            _meshes.Clear();
        }

        private static Mesh Quad()
        {
            Mesh mesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");
            Assert.That(mesh, Is.Not.Null);
            return mesh;
        }

        /// <summary>Three vertices with normals and a triangle submesh without indices: a published but empty range.</summary>
        private Mesh EmptyTriangles()
        {
            var mesh = new Mesh();
            _meshes.Add(mesh);
            mesh.SetVertices(new[] { Vector3.zero, Vector3.right, Vector3.up });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(Array.Empty<int>(), 0);
            return mesh;
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(64, 64, 8, 16, 16, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Mesh mesh)
        {
            Assert.That(storage.TryAppend(mesh, out VpStoredGeometry geometry), Is.True, "append");
            return geometry;
        }

        private GraphicsBuffer VertexBuffer(int count)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, VpRenderVertex.Stride);
            _buffers.Add(buffer);
            var blank = new VpRenderVertex[count];
            for (int i = 0; i < count; i++)
            {
                blank[i] = new VpRenderVertex { position = new Unity.Mathematics.float3(-99f, -99f, -99f) };
            }

            buffer.SetData(blank);
            return buffer;
        }

        private GraphicsBuffer IndexBuffer(int count)
        {
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, count, VpGpuIndexedGeometryBuffers.IndexStride);
            _buffers.Add(buffer);
            var blank = new uint[count];
            for (int i = 0; i < count; i++)
            {
                blank[i] = IndexSentinel;
            }

            buffer.SetData(blank);
            return buffer;
        }

        private static uint[] ReadBack(GraphicsBuffer buffer)
        {
            var data = new uint[buffer.count];
            buffer.GetData(data);
            return data;
        }

        private static (int start, int count) IndexPlace(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out _, out int start, out int count), Is.True, "index state");
            return (start, count);
        }

        [Test]
        public void TheVertexTransfer_PutsTheRangeAtItsOwnPosition()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, Quad());
                VpStoredGeometry second = Append(storage, Quad());
                Assert.That(second.vertexStart, Is.EqualTo(QuadVertices), "the second append starts past the first");

                GraphicsBuffer destination = VertexBuffer(storage.VertexCapacity);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, destination, second.vertexStart, second.vertexCount, out int transferred),
                    Is.True,
                    "transfer");
                Assert.That(transferred, Is.EqualTo(QuadVertices), "one transfer of the range");

                var read = new VpRenderVertex[destination.count];
                destination.GetData(read);
                NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                for (int i = 0; i < second.vertexCount; i++)
                {
                    // At its own global number, not at 0 and not twice as far along.
                    Assert.That(
                        (Vector3)read[second.vertexStart + i].position,
                        Is.EqualTo((Vector3)committed[second.vertexStart + i].position),
                        "vertex " + i + " at its global number");
                }

                for (int i = 0; i < second.vertexStart; i++)
                {
                    Assert.That(read[i].position.x, Is.EqualTo(-99f), "position " + i + " before the range is untouched");
                }
            }
        }

        [Test]
        public void AnEmptyVertexRange_IssuesNoTransfer()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, Quad());
                GraphicsBuffer destination = VertexBuffer(storage.VertexCapacity);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, destination, storage.VertexCount, 0, out int transferred),
                    Is.True,
                    "an empty range succeeds");
                Assert.That(transferred, Is.Zero, "and transfers nothing");
            }
        }

        [Test]
        public void TheIndexTransfer_PutsARangeWhereItLies()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, Quad());
                VpStoredGeometry second = Append(storage, Quad());
                (int start, int count) = IndexPlace(storage, second);
                Assert.That(start, Is.EqualTo(QuadIndices), "the second range lies past the first");

                GraphicsBuffer destination = IndexBuffer(storage.IndexCapacity);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadPublishedIndices(storage, destination, second.indexRange, out int transferred),
                    Is.True,
                    "transfer");
                Assert.That(transferred, Is.EqualTo(count), "one transfer of the range");

                uint[] read = ReadBack(destination);
                Assert.That(storage.TryAcquireIndexReadLease(second.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True);
                for (int i = 0; i < count; i++)
                {
                    Assert.That(read[start + i], Is.EqualTo(view[i]), "index " + i + " at its own position");
                }

                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                for (int i = 0; i < start; i++)
                {
                    Assert.That(read[i], Is.EqualTo(IndexSentinel), "position " + i + " before the range is untouched");
                }

                Assert.That(read[start + count], Is.EqualTo(IndexSentinel), "and the position after it");
            }
        }

        [Test]
        public void TwoAdjacentRanges_GoAcrossAsOneRun()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, Quad());
                VpStoredGeometry second = Append(storage, Quad());
                (int firstStart, int firstCount) = IndexPlace(storage, first);
                (int secondStart, int secondCount) = IndexPlace(storage, second);
                Assert.That(secondStart, Is.EqualTo(firstStart + firstCount), "they are adjacent, as a cut's two sides are");

                GraphicsBuffer destination = IndexBuffer(storage.IndexCapacity);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadPublishedIndexRun(storage, destination, first.indexRange, second.indexRange, out int transferred),
                    Is.True,
                    "transfer");
                Assert.That(transferred, Is.EqualTo(firstCount + secondCount), "one transfer covering both");

                uint[] read = ReadBack(destination);
                for (int i = 0; i < firstCount + secondCount; i++)
                {
                    Assert.That(read[firstStart + i], Is.Not.EqualTo(IndexSentinel), "position " + (firstStart + i) + " was written");
                }

                Assert.That(read[firstStart + firstCount + secondCount], Is.EqualTo(IndexSentinel), "and nothing past the run was");
            }
        }

        [Test]
        public void RangesOutOfOrder_TransferNothing()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, Quad());
                VpStoredGeometry second = Append(storage, Quad());
                GraphicsBuffer destination = IndexBuffer(storage.IndexCapacity);

                // The later range given first: the second does not begin where the first ends.
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadPublishedIndexRun(storage, destination, second.indexRange, first.indexRange, out int transferred),
                    Is.False,
                    "refused");
                Assert.That(transferred, Is.Zero);
                Assert.That(ReadBack(destination), Is.All.EqualTo(IndexSentinel), "the buffer is untouched");
            }
        }

        [Test]
        public void ADestinationTooSmall_TransfersNothing()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                Append(storage, Quad());
                VpStoredGeometry second = Append(storage, Quad());
                (int start, int count) = IndexPlace(storage, second);

                // Room for the range's length but not at its position.
                GraphicsBuffer destination = IndexBuffer(start + count - 1);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadPublishedIndices(storage, destination, second.indexRange, out int transferred),
                    Is.False,
                    "refused");
                Assert.That(transferred, Is.Zero);
                Assert.That(ReadBack(destination), Is.All.EqualTo(IndexSentinel), "the buffer is untouched");

                GraphicsBuffer vertices = VertexBuffer(storage.VertexCount - 1);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, vertices, 0, storage.VertexCount, out int vertexTransferred),
                    Is.False,
                    "the vertices too");
                Assert.That(vertexTransferred, Is.Zero);
            }
        }

        [Test]
        public void WhenTheSecondRangeCannotBeLeased_TheFirstLeaseIsGivenBack()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, Quad());
                VpStoredGeometry second = Append(storage, Quad());
                Assert.That(storage.TryRetireIndices(second.indexRange), Is.True, "the second range is retired, so it cannot be leased");

                GraphicsBuffer destination = IndexBuffer(storage.IndexCapacity);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadPublishedIndexRun(storage, destination, first.indexRange, second.indexRange, out int transferred),
                    Is.False,
                    "refused");
                Assert.That(transferred, Is.Zero);
                Assert.That(ReadBack(destination), Is.All.EqualTo(IndexSentinel), "the buffer is untouched");

                // If the first lease were still held, retiring would leave the range Retiring instead of freeing it.
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True, "retire the first range");
                Assert.That(storage.TryGetIndexState(first.indexRange, out VpIndexRangeState state, out _, out _), Is.True);
                Assert.That(state, Is.EqualTo(VpIndexRangeState.Free), "no lease was left holding it");
            }
        }

        [Test]
        public void AnEmptySide_TransfersTheOtherAlone()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry empty = Append(storage, EmptyTriangles());
                VpStoredGeometry filled = Append(storage, Quad());
                (int emptyStart, int emptyCount) = IndexPlace(storage, empty);
                (int filledStart, int filledCount) = IndexPlace(storage, filled);
                Assert.That(emptyCount, Is.Zero, "the empty side holds no index");

                GraphicsBuffer destination = IndexBuffer(storage.IndexCapacity);
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadPublishedIndexRun(storage, destination, empty.indexRange, filled.indexRange, out int transferred),
                    Is.True,
                    "transfer");
                Assert.That(transferred, Is.EqualTo(filledCount), "only the side that has indices");

                uint[] read = ReadBack(destination);
                for (int i = 0; i < filledCount; i++)
                {
                    Assert.That(read[filledStart + i], Is.Not.EqualTo(IndexSentinel), "position " + (filledStart + i) + " was written");
                }

                Assert.That(emptyStart, Is.LessThanOrEqualTo(filledStart), "the empty range sits at or before the filled one");
            }
        }

        [Test]
        public void ARefusedArgument_TransfersNothing()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry geometry = Append(storage, Quad());
                GraphicsBuffer destination = IndexBuffer(storage.IndexCapacity);

                Assert.That(VpStoredGeometryTransfer.TryUploadPublishedIndices(null, destination, geometry.indexRange, out int a), Is.False, "no storage");
                Assert.That(VpStoredGeometryTransfer.TryUploadPublishedIndices(storage, null, geometry.indexRange, out int b), Is.False, "no destination");
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, null, 0, 1, out int c), Is.False, "no vertex destination");
                Assert.That(
                    VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, VertexBuffer(storage.VertexCapacity), 0, storage.VertexCount + 1, out int d),
                    Is.False,
                    "past the committed vertices");
                Assert.That(new[] { a, b, c, d }, Is.All.Zero, "nothing was transferred");
                Assert.That(ReadBack(destination), Is.All.EqualTo(IndexSentinel), "the buffer is untouched");
            }
        }
    }
}
