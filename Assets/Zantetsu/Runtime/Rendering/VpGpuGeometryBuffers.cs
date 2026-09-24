using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// GPU copy of a VP CPU pool (DESIGN 4.5.4): a structured buffer of <see cref="VpRenderVertex"/> and one of uint
    /// indices, both read by the vertex-pulling shader rather than bound as hardware vertex or index buffers. The
    /// capacity can grow, one replacement at a time: at a draw boundary, <see cref="TryGrow"/> uploads the whole pool
    /// into larger buffers and switches to them, keeping the replaced pair until the GPU has finished with it. That is
    /// observed through an asynchronous readback of the first element of each replaced buffer, requested at the switch;
    /// <see cref="TryReleaseRetired"/> releases the pair once both have completed. Normal updates never wait for the
    /// GPU. After <see cref="Dispose"/>, every operation and the buffers throw; disposing again does nothing.
    /// </summary>
    public sealed class VpGpuGeometryBuffers : IDisposable
    {
        public const int IndexStride = sizeof(uint);

        private GraphicsBuffer _vertexBuffer;
        private GraphicsBuffer _indexBuffer;
        private GraphicsBuffer _retiredVertexBuffer;
        private GraphicsBuffer _retiredIndexBuffer;
        private AsyncGPUReadbackRequest _retiredVertexReadback;
        private AsyncGPUReadbackRequest _retiredIndexReadback;
        private RetiredCompletion _retiredCompletion;
        private bool _retiredReadbackErrorLogged;
        private bool _disposed;

        // A completed request is disposed by Unity on a later frame, when hasError becomes true even after success.
        // Latch each outcome while the callback's request is valid. This object belongs to one retired generation;
        // a late callback cannot change a later generation, and callback completion publishes its preceding outcome.
        private sealed class RetiredCompletion
        {
            public bool vertexError, indexError;
            public volatile bool vertexDone, indexDone;
            public void VertexCompleted(AsyncGPUReadbackRequest request) { vertexError = request.hasError; vertexDone = true; }
            public void IndexCompleted(AsyncGPUReadbackRequest request) { indexError = request.hasError; indexDone = true; }
        }

        public VpGpuGeometryBuffers(int vertexCapacity, int indexCapacity)
        {
            (_vertexBuffer, _indexBuffer) = CreateBuffers(vertexCapacity, indexCapacity);
            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
        }

        public int VertexCapacity { get; private set; }

        public int IndexCapacity { get; private set; }

        /// <summary>The active vertex buffer, for binding. It stays owned and released by this object.</summary>
        public GraphicsBuffer VertexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _vertexBuffer;
            }
        }

        /// <summary>The active index buffer, for binding. It stays owned and released by this object.</summary>
        public GraphicsBuffer IndexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _indexBuffer;
            }
        }

        /// <summary>
        /// Writes the pool's appended vertices and indices from the start of the active buffers. An empty pool succeeds
        /// without writing. Returns false, writing nothing and keeping the buffers, when either count exceeds its
        /// capacity.
        /// </summary>
        public bool TryUpload(VpCpuGeometryPool pool)
        {
            ThrowIfDisposed();
            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            NativeArray<VpRenderVertex> vertices = pool.AppendedVertices;
            NativeArray<uint> indices = pool.AppendedIndices;
            if (vertices.Length > VertexCapacity || indices.Length > IndexCapacity)
            {
                return false;
            }

            Upload(_vertexBuffer, _indexBuffer, vertices, indices);
            return true;
        }

        /// <summary>
        /// Moves to larger buffers. Call it at a draw boundary, after submitting the last draws that use the active
        /// buffers. It creates buffers of the given capacities and uploads all of the pool's appended vertices and
        /// indices into them; only then does it request a readback of the first element of each active buffer and make
        /// the new pair the active <see cref="VertexBuffer"/> and <see cref="IndexBuffer"/>. Geometry ranges keep their
        /// offsets. The replaced pair stays valid until <see cref="TryReleaseRetired"/> releases it. Returns false,
        /// changing nothing, when async GPU readback is unsupported, while a replaced pair is still held, or when either
        /// capacity is not larger than the current one or cannot hold the pool's contents. Buffers created by a call that
        /// then throws are released before the exception propagates.
        /// </summary>
        public bool TryGrow(VpCpuGeometryPool pool, int vertexCapacity, int indexCapacity)
        {
            ThrowIfDisposed();
            if (pool == null)
            {
                throw new ArgumentNullException(nameof(pool));
            }

            NativeArray<VpRenderVertex> vertices = pool.AppendedVertices;
            NativeArray<uint> indices = pool.AppendedIndices;
            if (!SystemInfo.supportsAsyncGPUReadback
                || _retiredVertexBuffer != null
                || vertexCapacity <= VertexCapacity
                || indexCapacity <= IndexCapacity
                || vertices.Length > vertexCapacity
                || indices.Length > indexCapacity)
            {
                return false;
            }

            GraphicsBuffer grownVertexBuffer = null;
            GraphicsBuffer grownIndexBuffer = null;
            AsyncGPUReadbackRequest vertexReadback;
            AsyncGPUReadbackRequest indexReadback;
            var completion = new RetiredCompletion();
            try
            {
                (grownVertexBuffer, grownIndexBuffer) = CreateBuffers(vertexCapacity, indexCapacity);
                Upload(grownVertexBuffer, grownIndexBuffer, vertices, indices);

                // A readback completes only after the GPU has processed the commands submitted before it, including
                // the last draws that used the buffer it reads.
                vertexReadback = AsyncGPUReadback.Request(_vertexBuffer, VpRenderVertex.Stride, 0, completion.VertexCompleted);
                indexReadback = AsyncGPUReadback.Request(_indexBuffer, IndexStride, 0, completion.IndexCompleted);
            }
            catch
            {
                grownVertexBuffer?.Dispose();
                grownIndexBuffer?.Dispose();
                throw;
            }

            _retiredVertexBuffer = _vertexBuffer;
            _retiredIndexBuffer = _indexBuffer;
            _retiredVertexReadback = vertexReadback;
            _retiredIndexReadback = indexReadback;
            _retiredCompletion = completion;
            _retiredReadbackErrorLogged = false;
            _vertexBuffer = grownVertexBuffer;
            _indexBuffer = grownIndexBuffer;
            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
            return true;
        }

        /// <summary>
        /// Releases the pair replaced by <see cref="TryGrow"/> once the readbacks of both of its buffers have completed,
        /// without waiting for them. True when this call released the pair; false when no pair is held or a readback is
        /// still pending. A readback that completed with an error is not taken as the GPU having finished with the pair:
        /// the pair is kept until <see cref="Dispose"/>, so growth stays blocked, false is returned, and the error is
        /// logged once.
        /// </summary>
        public bool TryReleaseRetired()
        {
            ThrowIfDisposed();
            if (_retiredVertexBuffer == null || !_retiredCompletion.vertexDone || !_retiredCompletion.indexDone)
            {
                return false;
            }

            if (_retiredCompletion.vertexError || _retiredCompletion.indexError)
            {
                if (!_retiredReadbackErrorLogged)
                {
                    _retiredReadbackErrorLogged = true;
                    Debug.LogError("VpGpuGeometryBuffers: a readback of the replaced buffers failed; they are kept until Dispose and growth stays blocked.");
                }

                return false;
            }

            ReleaseRetired();
            return true;
        }

        /// <summary>
        /// Releases the active buffers and any replaced pair still held. Only here, at teardown, does it wait: for the
        /// held pair's two readbacks to complete before releasing the pair.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_retiredVertexBuffer != null)
            {
                if (!_retiredCompletion.vertexDone) _retiredVertexReadback.WaitForCompletion();
                if (!_retiredCompletion.indexDone) _retiredIndexReadback.WaitForCompletion();
            }

            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
            ReleaseRetired();
        }

        private static (GraphicsBuffer vertexBuffer, GraphicsBuffer indexBuffer) CreateBuffers(int vertexCapacity, int indexCapacity)
        {
            var vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertexCapacity, VpRenderVertex.Stride)
            {
                name = "VP Vertices",
            };
            try
            {
                var indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, indexCapacity, IndexStride)
                {
                    name = "VP Indices",
                };
                return (vertexBuffer, indexBuffer);
            }
            catch
            {
                vertexBuffer.Dispose();
                throw;
            }
        }

        private static void Upload(
            GraphicsBuffer vertexBuffer,
            GraphicsBuffer indexBuffer,
            NativeArray<VpRenderVertex> vertices,
            NativeArray<uint> indices)
        {
            if (vertices.Length > 0)
            {
                vertexBuffer.SetData(vertices);
            }

            if (indices.Length > 0)
            {
                indexBuffer.SetData(indices);
            }
        }

        private void ReleaseRetired()
        {
            _retiredVertexBuffer?.Dispose();
            _retiredVertexBuffer = null;
            _retiredIndexBuffer?.Dispose();
            _retiredIndexBuffer = null;
            _retiredVertexReadback = default;
            _retiredIndexReadback = default;
            _retiredCompletion = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpGpuGeometryBuffers));
            }
        }
    }
}
