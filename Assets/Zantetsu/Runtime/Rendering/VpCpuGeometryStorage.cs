using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// CPU VP geometry storage made of an append-only <see cref="VpCpuVertexStorage"/> and a leased
    /// <see cref="VpCpuIndexStorage"/> (DESIGN 4.5.3), together with the metadata that belongs to the same append: the
    /// render vertex to topology vertex mapping and the submesh descriptors. A geometry is appended by writing its
    /// vertices into the uncommitted vertex tail and its mapping at the same positions of a fixed array one to one with
    /// the vertex capacity, writing its indices into an index range reserved at exactly their count and rebased onto the
    /// global vertex numbers, writing its submesh descriptors into the uncommitted submesh tail, publishing the whole
    /// index range, and only then committing the vertices and the submeshes. An append that fails after reserving
    /// cancels the reservation, so no vertex, mapping entry or submesh descriptor is committed and no index range stays
    /// published; bytes written into the uncommitted or freed space are not cleared, and the descriptor generation used
    /// is not given back. Nothing that can fail follows the publish.
    /// <para>
    /// Committed vertices, mapping entries and submesh descriptors are never moved or overwritten while the storage
    /// lives, and they have no read lease: the metadata is append-only and never reused, so a geometry's metadata stays
    /// readable while its index range is Published or Retiring, and is refused once that range is Free or its descriptor
    /// has been registered again. Index ranges themselves are read only through read leases, then retired and reused,
    /// under the view contracts of <see cref="VpCpuIndexStorage"/>. Only the main thread calls the storage. After
    /// <see cref="Dispose"/>, the views and every operation throw ObjectDisposedException; disposing again does nothing.
    /// </para>
    /// </summary>
    public sealed class VpCpuGeometryStorage : IDisposable
    {
        /// <summary>
        /// What one index descriptor's current registration was appended with. A stored geometry is one of this
        /// storage's own only if it matches the record of its descriptor's registration, so a handle of one append
        /// cannot be paired with the ranges of another, and a descriptor's earlier registration is not accepted once
        /// the descriptor has been registered again.
        /// </summary>
        private struct AppendRecord
        {
            public uint generation;
            public int vertexStart;
            public int vertexCount;
            public bool hasTopology;
            public int topologyVertexCount;
            public int submeshStart;
            public int submeshCount;
        }

        private readonly VpCpuVertexStorage _vertices;
        private readonly VpCpuIndexStorage _indices;
        private NativeArray<int> _topologyOfVertex;
        private NativeArray<VpGeometrySubmesh> _submeshes;
        private readonly AppendRecord[] _appendOfDescriptor;
        private int _submeshCount;
        private bool _disposed;
        private bool _referenceTableClaimed;

        public VpCpuGeometryStorage(
            int vertexCapacity,
            int indexCapacity,
            int indexDescriptorCapacity,
            int submeshCapacity,
            Allocator allocator)
        {
            if (submeshCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(submeshCapacity), submeshCapacity, "Must not be negative.");
            }

            _vertices = new VpCpuVertexStorage(vertexCapacity, allocator);
            try
            {
                _indices = new VpCpuIndexStorage(indexCapacity, indexDescriptorCapacity, allocator);
                _topologyOfVertex = new NativeArray<int>(vertexCapacity, allocator);
                _submeshes = new NativeArray<VpGeometrySubmesh>(submeshCapacity, allocator);

                // One record per index descriptor, taken once here so that publishing never has to allocate.
                _appendOfDescriptor = new AppendRecord[_indices.DescriptorCapacity];
            }
            catch
            {
                if (_submeshes.IsCreated)
                {
                    _submeshes.Dispose();
                }

                if (_topologyOfVertex.IsCreated)
                {
                    _topologyOfVertex.Dispose();
                }

                _indices?.Dispose();
                _vertices.Dispose();
                throw;
            }
        }

        public int VertexCapacity => _vertices.Capacity;

        public int VertexCount => _vertices.Count;

        public int IndexCapacity => _indices.IndexCapacity;

        public int IndexDescriptorCapacity => _indices.DescriptorCapacity;

        public int SubmeshCapacity => _submeshes.Length;

        public int SubmeshCount => _submeshCount;

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
        /// Appends the mesh (see <see cref="VpMeshConverter"/>) and returns its committed vertex range, published index
        /// range and submesh descriptors. The submeshes keep the mesh's order, each covering its part of the published
        /// range with an offset relative to that range's start, and take their submesh ordinal as material index. A
        /// Unity Mesh carries no source topology and none is reconstructed or guessed here, so the result has no
        /// topology mapping. Returns false with a default result, committing nothing and leaving no index range reserved
        /// or published, when, checked in this order: the mesh is null or has more than int.MaxValue indices; the free
        /// vertex tail or submesh tail is too small; no index range or descriptor can be reserved; the converter rejects
        /// the mesh; or a converted index is not below the mesh's vertex count or its global number exceeds uint.MaxValue.
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
                int submeshStart = _submeshCount;
                int submeshCount = data.subMeshCount;
                if (vertexCount > _vertices.Capacity - vertexStart
                    || submeshCount > _submeshes.Length - submeshStart
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
                        && WriteMeshSubmeshes(data, submeshStart)
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
                _submeshCount += submeshCount;
                geometry = new VpStoredGeometry(vertexStart, vertexCount, indexRange, false, 0, submeshStart, submeshCount);
                RecordAppend(indexRange, geometry);
                return true;
            }
        }

        /// <summary>
        /// Appends a prepared geometry whose arrays the caller owns: its vertices in the one common 32 byte layout, its
        /// indices in mesh-local vertex numbers, the topology vertex each render vertex belongs to, and its submesh
        /// descriptors. Everything is copied into the storage, which keeps no reference to the arrays and changes none
        /// of them. Indices are rebased onto the global vertex numbers; topology ids stay geometry-local.
        /// <para>
        /// Returns false with a default result, committing nothing and leaving no index range reserved or published,
        /// when an array is null, the topology map length is not the vertex count, the topology vertex count is
        /// negative, a topology id is outside [0, topologyVertexCount), an index is not below the vertex count or its
        /// global number would exceed uint.MaxValue, a submesh index count or material index is negative, the submeshes
        /// do not cover every index once in order, a capacity is too small, or no index range or descriptor can be
        /// reserved. Conditions the input gate already decided — manifoldness, winding, finiteness — are not checked
        /// again here.
        /// </para>
        /// </summary>
        public bool TryAppendPrepared(
            VpRenderVertex[] vertices,
            uint[] localIndices,
            int[] topologyOfVertex,
            int topologyVertexCount,
            VpGeometrySubmesh[] submeshes,
            out VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            geometry = default;
            if (vertices == null || localIndices == null || topologyOfVertex == null || submeshes == null)
            {
                return false;
            }

            int vertexStart = _vertices.Count;
            int vertexCount = vertices.Length;
            int indexCount = localIndices.Length;
            int submeshStart = _submeshCount;
            int submeshCount = submeshes.Length;
            if (topologyVertexCount < 0
                || topologyOfVertex.Length != vertexCount
                || indexCount % 3 != 0
                || !AreTopologyIdsInRange(topologyOfVertex, topologyVertexCount)
                || !AreIndicesInRange(localIndices, vertexStart, vertexCount)
                || !DoSubmeshesCover(submeshes, indexCount))
            {
                return false;
            }

            if (vertexCount > _vertices.Capacity - vertexStart
                || submeshCount > _submeshes.Length - submeshStart
                || !_indices.TryReserve(indexCount, out VpIndexRangeHandle indexRange))
            {
                return false;
            }

            bool published;
            try
            {
                if (!_indices.TryGetReservedWriteView(indexRange, out NativeArray<uint> indexView))
                {
                    _indices.TryCancelReservation(indexRange);
                    return false;
                }

                NativeArray<VpRenderVertex> vertexTail = _vertices.GetUncommittedTail(vertexCount);
                for (int v = 0; v < vertexCount; v++)
                {
                    vertexTail[v] = vertices[v];
                    _topologyOfVertex[vertexStart + v] = topologyOfVertex[v];
                }

                for (int i = 0; i < indexCount; i++)
                {
                    indexView[i] = (uint)(vertexStart + localIndices[i]);
                }

                for (int s = 0; s < submeshCount; s++)
                {
                    _submeshes[submeshStart + s] = submeshes[s];
                }

                // The last step that can fail: what follows only advances the committed counts.
                published = _indices.TryPublish(indexRange);
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
            _submeshCount += submeshCount;
            geometry = new VpStoredGeometry(vertexStart, vertexCount, indexRange, true, topologyVertexCount, submeshStart, submeshCount);
            RecordAppend(indexRange, geometry);
            return true;
        }

        /// <summary>
        /// The geometry's render vertex to topology vertex mapping, one entry per committed vertex, and the number of
        /// topology vertices it addresses. A view into the storage, not a copy. Returns false with defaults for a
        /// default, foreign, stale or structurally inconsistent geometry, for one appended without a mapping, and once
        /// its index range is Free or its descriptor has been registered again.
        /// </summary>
        public bool TryGetTopology(VpStoredGeometry geometry, out NativeArray<int>.ReadOnly topologyOfVertex, out int topologyVertexCount)
        {
            ThrowIfDisposed();
            topologyOfVertex = default;
            topologyVertexCount = 0;
            if (!geometry.hasTopology || !IsMetadataReadable(geometry))
            {
                return false;
            }

            topologyOfVertex = _topologyOfVertex.GetSubArray(geometry.vertexStart, geometry.vertexCount).AsReadOnly();
            topologyVertexCount = geometry.topologyVertexCount;
            return true;
        }

        /// <summary>
        /// The geometry's submesh descriptors in append order, each covering its part of the published index range with
        /// an offset relative to that range's start. A view into the storage, not a copy. Returns false with a default
        /// view for a default, foreign, stale or structurally inconsistent geometry, and once its index range is Free or
        /// its descriptor has been registered again.
        /// </summary>
        public bool TryGetSubmeshes(VpStoredGeometry geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes)
        {
            ThrowIfDisposed();
            submeshes = default;
            if (!IsMetadataReadable(geometry))
            {
                return false;
            }

            submeshes = _submeshes.GetSubArray(geometry.submeshStart, geometry.submeshCount).AsReadOnly();
            return true;
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
            _submeshes.Dispose();
            _topologyOfVertex.Dispose();
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
        /// Whether the geometry is one this storage returned: its index range handle is a current registration of this
        /// storage's own index table, and every range it names is the one that registration was appended with. Ranges
        /// that merely fit inside the storage are not enough, so one append's handle cannot be paired with another
        /// append's vertices, submeshes or topology description. Says nothing about the state of the index range.
        /// </summary>
        internal bool IsGeometryConsistent(VpStoredGeometry geometry)
        {
            // A current registration of this storage's table: this rejects the default, foreign and superseded handles,
            // and bounds the descriptor index.
            if (!_indices.TryGetState(geometry.indexRange, out _, out _, out _))
            {
                return false;
            }

            AppendRecord append = _appendOfDescriptor[geometry.indexRange.descriptor];
            return append.generation == geometry.indexRange.generation
                && append.vertexStart == geometry.vertexStart
                && append.vertexCount == geometry.vertexCount
                && append.hasTopology == geometry.hasTopology
                && append.topologyVertexCount == geometry.topologyVertexCount
                && append.submeshStart == geometry.submeshStart
                && append.submeshCount == geometry.submeshCount;
        }

        /// <summary>Records what a published descriptor registration was appended with. Writes into the array taken at construction.</summary>
        private void RecordAppend(VpIndexRangeHandle indexRange, VpStoredGeometry geometry)
        {
            _appendOfDescriptor[indexRange.descriptor] = new AppendRecord
            {
                generation = indexRange.generation,
                vertexStart = geometry.vertexStart,
                vertexCount = geometry.vertexCount,
                hasTopology = geometry.hasTopology,
                topologyVertexCount = geometry.topologyVertexCount,
                submeshStart = geometry.submeshStart,
                submeshCount = geometry.submeshCount,
            };
        }

        /// <summary>Metadata is readable while the geometry's own index range is Published or Retiring in this storage.</summary>
        private bool IsMetadataReadable(VpStoredGeometry geometry)
        {
            return IsGeometryConsistent(geometry)
                && _indices.TryGetState(geometry.indexRange, out VpIndexRangeState state, out _, out _)
                && (state == VpIndexRangeState.Published || state == VpIndexRangeState.Retiring);
        }

        /// <summary>Writes one descriptor per mesh submesh into the uncommitted submesh tail, whose room was checked before reserving.</summary>
        private bool WriteMeshSubmeshes(Mesh.MeshData data, int submeshStart)
        {
            int offset = 0;
            for (int s = 0; s < data.subMeshCount; s++)
            {
                int count = data.GetSubMesh(s).indexCount;
                _submeshes[submeshStart + s] = new VpGeometrySubmesh(offset, count, s);
                offset += count;
            }

            return true;
        }

        private static bool AreTopologyIdsInRange(int[] topologyOfVertex, int topologyVertexCount)
        {
            for (int v = 0; v < topologyOfVertex.Length; v++)
            {
                if (topologyOfVertex[v] < 0 || topologyOfVertex[v] >= topologyVertexCount)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool AreIndicesInRange(uint[] localIndices, int vertexStart, int vertexCount)
        {
            for (int i = 0; i < localIndices.Length; i++)
            {
                uint local = localIndices[i];
                if (local >= (uint)vertexCount || (ulong)vertexStart + local > uint.MaxValue)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// The submeshes must cover [0, indexCount) once, in order, with no gap and no overlap, and each must hold whole
        /// triangles: a boundary inside a triangle would hand the display side a submesh it cannot draw. An empty
        /// submesh is still allowed, 0 being a multiple of 3.
        /// </summary>
        private static bool DoSubmeshesCover(VpGeometrySubmesh[] submeshes, int indexCount)
        {
            long covered = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                VpGeometrySubmesh submesh = submeshes[s];
                if (submesh.materialIndex < 0
                    || submesh.indexCount < 0
                    || submesh.indexCount % 3 != 0
                    || submesh.indexOffset != covered)
                {
                    return false;
                }

                covered += submesh.indexCount;
            }

            return covered == indexCount;
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
