using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// CPU VP geometry storage made of an append-only <see cref="VpCpuVertexStorage"/> and a leased
    /// <see cref="VpCpuIndexStorage"/> (DESIGN 4.5.3). A mesh is appended by converting its vertices into the
    /// uncommitted vertex tail and its indices into an index range reserved at exactly their count, rebasing the indices
    /// onto the global vertex numbers, publishing the whole index range, and only then committing the vertices. An
    /// append that fails after reserving cancels the reservation, so no vertices are committed and no index range stays
    /// published; bytes written into the uncommitted or freed space are not cleared, and the descriptor generation used
    /// is not given back.
    /// <para>
    /// Committed vertices are never moved or overwritten while the storage lives and have no read lease in Phase 0.93.
    /// Index ranges are read only through read leases, then retired and reused, under the view contracts of
    /// <see cref="VpCpuIndexStorage"/>. Only the main thread calls the storage. After <see cref="Dispose"/>, the vertex
    /// view and every operation throw ObjectDisposedException; disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpCpuGeometryStorage : IDisposable
    {
        private readonly VpCpuVertexStorage _vertices;
        private readonly VpCpuIndexStorage _indices;
        private bool _disposed;
        private bool _referenceTableClaimed;

        public VpCpuGeometryStorage(int vertexCapacity, int indexCapacity, int indexDescriptorCapacity, Allocator allocator)
        {
            _vertices = new VpCpuVertexStorage(vertexCapacity, allocator);
            try
            {
                _indices = new VpCpuIndexStorage(indexCapacity, indexDescriptorCapacity, allocator);
            }
            catch
            {
                _vertices.Dispose();
                throw;
            }
        }

        public int VertexCapacity => _vertices.Capacity;

        public int VertexCount => _vertices.Count;

        public int IndexCapacity => _indices.IndexCapacity;

        public int IndexDescriptorCapacity => _indices.DescriptorCapacity;

        /// <summary>The committed vertices, [0, VertexCount), never moved or overwritten. A view into the storage, not a copy.</summary>
        public NativeArray<VpRenderVertex>.ReadOnly Vertices
        {
            get
            {
                ThrowIfDisposed();
                return _vertices.Vertices;
            }
        }

        /// <summary>
        /// Appends the mesh (see <see cref="VpMeshConverter"/>) and returns its committed vertex range and published index
        /// range. Returns false with a default result, committing no vertices and leaving no index range reserved or
        /// published, when, checked in this order: the mesh is null or has more than int.MaxValue indices; the free
        /// vertex tail is too small; no index range or descriptor can be reserved; the converter rejects the mesh; or a
        /// converted index is not below the mesh's vertex count or its global number exceeds uint.MaxValue.
        /// </summary>
        public bool TryAppend(Mesh mesh, out VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            geometry = default;
            if (mesh == null)
            {
                return false;
            }

            using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                Mesh.MeshData data = dataArray[0];
                long totalIndexCount = 0;
                for (int s = 0; s < data.subMeshCount; s++)
                {
                    totalIndexCount += data.GetSubMesh(s).indexCount;
                }

                if (totalIndexCount > int.MaxValue)
                {
                    return false;
                }

                int vertexStart = _vertices.Count;
                int vertexCount = data.vertexCount;
                if (vertexCount > _vertices.Capacity - vertexStart
                    || !_indices.TryReserve((int)totalIndexCount, out VpIndexRangeHandle indexRange))
                {
                    return false;
                }

                bool published;
                try
                {
                    published = _indices.TryGetReservedWriteView(indexRange, out NativeArray<uint> indices)
                        && VpMeshConverter.TryConvert(data, _vertices.GetUncommittedTail(vertexCount), indices, out _, out _)
                        && TryRebase(indices, vertexStart, vertexCount)
                        && _indices.TryPublish(indexRange);
                }
                catch
                {
                    CancelWhileThrowing(indexRange);
                    throw;
                }

                if (!published)
                {
                    _indices.TryCancelReservation(indexRange);
                    return false;
                }

                _vertices.Commit(vertexCount);
                geometry = new VpStoredGeometry(vertexStart, vertexCount, indexRange);
                return true;
            }
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryAcquireReadLease"/>
        public bool TryAcquireIndexReadLease(VpIndexRangeHandle indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view)
        {
            ThrowIfDisposed();
            return _indices.TryAcquireReadLease(indexRange, out lease, out view);
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryGetReadView"/>
        public bool TryGetIndexReadView(VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view)
        {
            ThrowIfDisposed();
            return _indices.TryGetReadView(lease, out view);
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryRetire"/>
        public bool TryRetireIndices(VpIndexRangeHandle indexRange)
        {
            ThrowIfDisposed();
            return _indices.TryRetire(indexRange);
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryReleaseReadLease"/>
        public bool TryReleaseIndexReadLease(VpIndexReadLease lease)
        {
            ThrowIfDisposed();
            return _indices.TryReleaseReadLease(lease);
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryGetState"/>
        public bool TryGetIndexState(VpIndexRangeHandle indexRange, out VpIndexRangeState state, out int indexStart, out int indexCount)
        {
            ThrowIfDisposed();
            return _indices.TryGetState(indexRange, out state, out indexStart, out indexCount);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _indices.Dispose();
            _vertices.Dispose();
        }

        /// <summary>
        /// Claims the storage for its one <see cref="VpGeometryReferenceTable"/> for the rest of the storage's lifetime,
        /// so that a geometry's index range has a single owner that retires it. Returns false when already claimed; the
        /// claim is never released. Main thread only.
        /// </summary>
        internal bool TryClaimReferenceTable()
        {
            ThrowIfDisposed();
            if (_referenceTableClaimed)
            {
                return false;
            }

            _referenceTableClaimed = true;
            return true;
        }

        /// <summary>
        /// Rewrites mesh-local indices as global vertex numbers. Returns false, possibly after rewriting some, when an
        /// index is not below <paramref name="vertexCount"/> or the global number exceeds uint.MaxValue.
        /// </summary>
        private static bool TryRebase(NativeArray<uint> indices, int vertexStart, int vertexCount)
        {
            for (int i = 0; i < indices.Length; i++)
            {
                uint local = indices[i];
                ulong global = (ulong)vertexStart + local;
                if (local >= (uint)vertexCount || global > uint.MaxValue)
                {
                    return false;
                }

                indices[i] = (uint)global;
            }

            return true;
        }

        /// <summary>Cancels the reservation while an exception propagates, without letting a failure here replace it.</summary>
        private void CancelWhileThrowing(VpIndexRangeHandle indexRange)
        {
            try
            {
                _indices.TryCancelReservation(indexRange);
            }
            catch (Exception)
            {
                // Best effort: the original exception is the one to report.
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCpuGeometryStorage));
            }
        }
    }
}
