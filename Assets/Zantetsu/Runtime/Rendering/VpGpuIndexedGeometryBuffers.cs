using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// VP Stage 3 GPU copy of a VP CPU pool (DESIGN 4.5.4) for indexed indirect draws: a structured buffer of
    /// <see cref="VpRenderVertex"/>, read by the vertex-pulling shader, and a hardware index buffer
    /// (GraphicsBuffer.Target.Index, uint) holding the pool's indices, which already address global vertex numbers.
    /// D3D11 cannot use one buffer as both an index and a structured buffer, so this owns an index buffer of its own and
    /// <see cref="VpGpuGeometryBuffers"/> keeps Stage 2's structured index buffer unchanged. The capacity is fixed. After
    /// <see cref="Dispose"/>, uploading and the buffers throw; disposing again does nothing.
    /// </summary>
    public sealed class VpGpuIndexedGeometryBuffers : IDisposable
    {
        public const int IndexStride = sizeof(uint);

        private readonly GraphicsBuffer _vertexBuffer;
        private readonly GraphicsBuffer _indexBuffer;
        private bool _disposed;

        public VpGpuIndexedGeometryBuffers(int vertexCapacity, int indexCapacity)
        {
            _vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, vertexCapacity, VpRenderVertex.Stride)
            {
                name = "VP Indexed Vertices",
            };
            try
            {
                _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, indexCapacity, IndexStride)
                {
                    name = "VP Indexed Indices",
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

        /// <summary>The structured vertex buffer, for binding. It stays owned and released by this object.</summary>
        public GraphicsBuffer VertexBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _vertexBuffer;
            }
        }

        /// <summary>The hardware index buffer, for indexed draws. It stays owned and released by this object.</summary>
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
        /// writing. Returns false, writing nothing, when either count exceeds its capacity.
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
                throw new ObjectDisposedException(nameof(VpGpuIndexedGeometryBuffers));
            }
        }
    }
}
