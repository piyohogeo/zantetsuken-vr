using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;

[assembly: InternalsVisibleTo("Zantetsu.Core.EditModeTests")]

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity, append-only CPU VP vertex / index pool (DESIGN 4.5.3). Meshes are converted straight into the
    /// free tail and their indices rebased onto the pool's global vertex numbers. There is no growth, reuse or
    /// release of ranges. After <see cref="Dispose"/>, appending and the views throw; disposing again does nothing.
    /// </summary>
    public sealed class VpCpuGeometryPool : IDisposable
    {
        private NativeArray<VpRenderVertex> _vertices;
        private NativeArray<uint> _indices;
        private bool _disposed;

        public VpCpuGeometryPool(int vertexCapacity, int indexCapacity, Allocator allocator)
        {
            _vertices = new NativeArray<VpRenderVertex>(vertexCapacity, allocator);
            try
            {
                _indices = new NativeArray<uint>(indexCapacity, allocator);
            }
            catch
            {
                _vertices.Dispose();
                throw;
            }

            VertexCapacity = vertexCapacity;
            IndexCapacity = indexCapacity;
        }

        public int VertexCapacity { get; }

        public int IndexCapacity { get; }

        public int VertexCount { get; private set; }

        public int IndexCount { get; private set; }

        /// <summary>The appended vertices, [0, VertexCount). A view into the pool, not a copy.</summary>
        public NativeArray<VpRenderVertex>.ReadOnly Vertices
        {
            get
            {
                ThrowIfDisposed();
                return _vertices.GetSubArray(0, VertexCount).AsReadOnly();
            }
        }

        /// <summary>The appended indices, [0, IndexCount), in global vertex numbers. A view into the pool, not a copy.</summary>
        public NativeArray<uint>.ReadOnly Indices
        {
            get
            {
                ThrowIfDisposed();
                return _indices.GetSubArray(0, IndexCount).AsReadOnly();
            }
        }

        /// <summary>The appended vertices as a NativeArray view for GPU uploads inside this assembly. Not to be written.</summary>
        internal NativeArray<VpRenderVertex> AppendedVertices
        {
            get
            {
                ThrowIfDisposed();
                return _vertices.GetSubArray(0, VertexCount);
            }
        }

        /// <summary>The appended indices as a NativeArray view for GPU uploads inside this assembly. Not to be written.</summary>
        internal NativeArray<uint> AppendedIndices
        {
            get
            {
                ThrowIfDisposed();
                return _indices.GetSubArray(0, IndexCount);
            }
        }

        /// <summary>
        /// Appends the mesh after the current contents (see <see cref="VpMeshConverter.TryConvert"/>). Returns false
        /// with a default range, leaving the counts and all pool data unchanged, when the mesh cannot be converted or
        /// either free tail is too small.
        /// </summary>
        public bool TryAppend(Mesh mesh, out VpGeometryRange range)
        {
            ThrowIfDisposed();
            int vertexStart = VertexCount;
            int indexStart = IndexCount;
            if (!VpMeshConverter.TryConvert(
                    mesh,
                    _vertices.GetSubArray(vertexStart, VertexCapacity - vertexStart),
                    _indices.GetSubArray(indexStart, IndexCapacity - indexStart),
                    out int vertexCount,
                    out int indexCount))
            {
                range = default;
                return false;
            }

            for (int i = indexStart; i < indexStart + indexCount; i++)
            {
                _indices[i] += (uint)vertexStart;
            }

            VertexCount = vertexStart + vertexCount;
            IndexCount = indexStart + indexCount;
            range = new VpGeometryRange(vertexStart, vertexCount, indexStart, indexCount);
            return true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _vertices.Dispose();
            _indices.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCpuGeometryPool));
            }
        }
    }
}
