using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity GPU copy of a VP CPU pool (DESIGN 4.5.4): a structured buffer of <see cref="VpRenderVertex"/> and
    /// one of uint indices, both read by the vertex-pulling shader rather than bound as hardware vertex or index
    /// buffers. There is no growth or buffer replacement. After <see cref="Dispose"/>, uploading and the buffers throw;
    /// disposing again does nothing.
    /// </summary>
    public sealed class VpGpuGeometryBuffers : IDisposable
    {
        public const int IndexStride = sizeof(uint);

        private readonly GraphicsBuffer _vertexBuffer;
        private readonly GraphicsBuffer _indexBuffer;
        private bool _disposed;

        public VpGpuGeometryBuffers(int vertexCapacity, int indexCapacity)
        {
            _vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertexCapacity, VpRenderVertex.Stride)
            {
                name = "VP Vertices",
            };
            try
            {
                _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, indexCapacity, IndexStride)
                {
                    name = "VP Indices",
                };
            }
            catch
            {
                _vertexBuffer.Dispose();
                throw;
            }

            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
        }

        public int VertexCapacity { get; }

        public int IndexCapacity { get; }

        /// <summary>The vertex buffer, for binding. It stays owned and released by this object.</summary>
        public GraphicsBuffer VertexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _vertexBuffer;
            }
        }

        /// <summary>The index buffer, for binding. It stays owned and released by this object.</summary>
        public GraphicsBuffer IndexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _indexBuffer;
            }
        }

        /// <summary>
        /// Writes the pool's appended vertices and indices from the start of the buffers. An empty pool succeeds without
        /// writing. Returns false, writing nothing and keeping the buffers, when either count exceeds its capacity.
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

            if (vertices.Length > 0)
            {
                _vertexBuffer.SetData(vertices);
            }

            if (indices.Length > 0)
            {
                _indexBuffer.SetData(indices);
            }

            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _vertexBuffer.Dispose();
            _indexBuffer.Dispose();
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
