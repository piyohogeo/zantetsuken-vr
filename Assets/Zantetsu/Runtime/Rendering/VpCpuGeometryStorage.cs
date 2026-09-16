using System;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// CPU VP geometry storage made of an append-only <see cref="VpCpuVertexStorage"/> and a leased
    /// <see cref="VpCpuIndexStorage"/> (DESIGN 4.5.3), together with the metadata that belongs to the same append: the
    /// vertex blocks a geometry is made of, the render vertex to topology vertex mapping and the submesh descriptors.
    /// A geometry is appended by writing its vertices into the uncommitted vertex tail and its mapping at the same
    /// positions of a fixed array one to one with the vertex capacity, writing its indices into an index range reserved
    /// at exactly their count and rebased onto the global vertex numbers, writing its block and submesh descriptors
    /// into the uncommitted metadata tails, publishing the index range, and only then committing the vertices and the
    /// metadata. An append that fails after reserving cancels the reservation, so nothing is committed and no index
    /// range stays published; bytes written into the uncommitted or freed space are not cleared, and the descriptor
    /// generation used is not given back. Nothing that can fail follows the publish.
    /// <para>
    /// A geometry's vertices are the ordered union of its blocks rather than one contiguous run, so the two geometries
    /// a cut produces can name their parent's blocks and the block of vertices the cut appended without copying either
    /// (<see cref="TryReserveCutOutput"/>, <see cref="TryCommitCutOutput"/>). The two sides share those vertices, that
    /// mapping and that block list, and own their index ranges separately: retiring or reusing one side's range leaves
    /// the other untouched, and a child stays readable after its parent has been retired.
    /// </para>
    /// <para>
    /// Committed vertices, mapping entries, blocks and submesh descriptors are never moved or overwritten while the
    /// storage lives, and they have no read lease: the metadata is append-only and never reused, so a geometry's
    /// metadata stays readable while its own index range is Published or Retiring, and is refused once that range is
    /// Free or its descriptor has been registered again. Index ranges themselves are read only through read leases,
    /// then retired and reused, under the view contracts of <see cref="VpCpuIndexStorage"/>. Only the main thread calls
    /// the storage. After <see cref="Dispose"/>, the views and every operation throw ObjectDisposedException; disposing
    /// again does nothing.
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
            public int blockStart;
            public int blockCount;
        }

        private readonly VpCpuVertexStorage _vertices;
        private readonly VpCpuIndexStorage _indices;
        private NativeArray<int> _topologyOfVertex;
        private NativeArray<VpGeometrySubmesh> _submeshes;
        private NativeArray<VpGeometryVertexBlock> _vertexBlocks;
        private readonly AppendRecord[] _appendOfDescriptor;

        // At most one cut output reservation is open at a time, because an open one holds the uncommitted tails of the
        // vertices, the mapping, the submeshes and the blocks: a second writer into the same tails would have its work
        // overwritten or would overwrite what the first is about to commit.
        private VpCutOutputReservation _openCutOutput;
        private int _submeshCount;
        private int _vertexBlockCount;
        private bool _disposed;
        private bool _referenceTableClaimed;

        public VpCpuGeometryStorage(
            int vertexCapacity,
            int indexCapacity,
            int indexDescriptorCapacity,
            int submeshCapacity,
            int vertexBlockCapacity,
            Allocator allocator)
        {
            if (submeshCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(submeshCapacity), submeshCapacity, "Must not be negative.");
            }

            if (vertexBlockCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(vertexBlockCapacity), vertexBlockCapacity, "Must not be negative.");
            }

            _vertices = new VpCpuVertexStorage(vertexCapacity, allocator);
            try
            {
                _indices = new VpCpuIndexStorage(indexCapacity, indexDescriptorCapacity, allocator);
                _topologyOfVertex = new NativeArray<int>(vertexCapacity, allocator);
                _submeshes = new NativeArray<VpGeometrySubmesh>(submeshCapacity, allocator);
                _vertexBlocks = new NativeArray<VpGeometryVertexBlock>(vertexBlockCapacity, allocator);

                // One record per index descriptor, taken once here so that publishing never has to allocate.
                _appendOfDescriptor = new AppendRecord[_indices.DescriptorCapacity];
            }
            catch
            {
                if (_vertexBlocks.IsCreated)
                {
                    _vertexBlocks.Dispose();
                }

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

        public int VertexBlockCapacity => _vertexBlocks.Length;

        public int VertexBlockCount => _vertexBlockCount;

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
        /// The topology vertex id of every committed render vertex, at the vertex's own global number. A view into the
        /// storage, not a copy: a geometry's own entries are the ones its blocks name.
        /// </summary>
        public NativeArray<int>.ReadOnly TopologyOfVertex
        {
            get
            {
                ThrowIfDisposed();
                return _topologyOfVertex.GetSubArray(0, _vertices.Count).AsReadOnly();
            }
        }

        /// <summary>
        /// Appends the mesh (see <see cref="VpMeshConverter"/>) and returns its committed vertex range, published index
        /// range and submesh descriptors. The submeshes keep the mesh's order, each covering its part of the published
        /// range with an offset relative to that range's start, and take their submesh ordinal as material index. A
        /// Unity Mesh carries no source topology and none is reconstructed or guessed here, so the result has no
        /// topology mapping. Returns false with a default result, committing nothing and leaving no index range reserved
        /// or published, when, checked in this order: the mesh is null or has more than int.MaxValue indices; the free
        /// vertex tail, submesh tail or block tail is too small; no index range or descriptor can be reserved; the
        /// converter rejects the mesh; or a converted index is not below the mesh's vertex count or its global number
        /// exceeds uint.MaxValue.
        /// </summary>
        public bool TryAppend(Mesh mesh, out VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            geometry = default;
            if (mesh == null || _openCutOutput != null)
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
                int blockStart = _vertexBlockCount;
                if (vertexCount > _vertices.Capacity - vertexStart
                    || submeshCount > _submeshes.Length - submeshStart
                    || 1 > _vertexBlocks.Length - blockStart
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
                        && WriteOwnBlock(blockStart, vertexStart, vertexCount)
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
                _vertexBlockCount = blockStart + 1;
                geometry = new VpStoredGeometry(vertexStart, vertexCount, indexRange, false, 0, submeshStart, submeshCount, blockStart, 1);
                RecordAppend(indexRange, geometry);
                return true;
            }
        }

        /// <summary>
        /// Appends a prepared geometry whose arrays the caller owns: its vertices in the one common 32 byte layout, its
        /// indices in mesh-local vertex numbers, the topology vertex each render vertex belongs to, and its submesh
        /// descriptors. Everything is copied into the storage, which keeps no reference to the arrays and changes none
        /// of them. Indices are rebased onto the global vertex numbers; topology ids stay geometry-local. The result is
        /// one vertex block.
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
            if (vertices == null || localIndices == null || topologyOfVertex == null || submeshes == null || _openCutOutput != null)
            {
                return false;
            }

            int vertexStart = _vertices.Count;
            int vertexCount = vertices.Length;
            int indexCount = localIndices.Length;
            int submeshStart = _submeshCount;
            int submeshCount = submeshes.Length;
            int blockStart = _vertexBlockCount;
            if (topologyVertexCount < 0
                || topologyOfVertex.Length != vertexCount
                || indexCount % 3 != 0
                || !AreTopologyIdsInRange(topologyOfVertex, topologyVertexCount)
                || !AreIndicesInRange(localIndices, vertexStart, vertexCount)
                || !DoSubmeshesCover(submeshes, 0, submeshes.Length, indexCount))
            {
                return false;
            }

            if (vertexCount > _vertices.Capacity - vertexStart
                || submeshCount > _submeshes.Length - submeshStart
                || 1 > _vertexBlocks.Length - blockStart
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

                WriteOwnBlock(blockStart, vertexStart, vertexCount);

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
            _vertexBlockCount = blockStart + 1;
            geometry = new VpStoredGeometry(vertexStart, vertexCount, indexRange, true, topologyVertexCount, submeshStart, submeshCount, blockStart, 1);
            RecordAppend(indexRange, geometry);
            return true;
        }

        /// <summary>
        /// Sets aside room for the output of one cut of <paramref name="parent"/>: the vertices the cut may append and
        /// their mapping entries, one index range for both sides together, and the metadata the two sides may need.
        /// Nothing is visible to a reader until the reservation is committed. Returns false with a null reservation,
        /// having taken nothing, when the parent is not a geometry of this storage whose metadata is readable, when a
        /// capacity is negative, when a free tail is too small, or when no index range or descriptor can be reserved.
        /// </summary>
        /// <param name="submeshCapacity">Descriptors the two sides may use together, normally twice the parent's.</param>
        /// <param name="vertexBlockCapacity">Blocks the children's shared list may use, normally the parent's plus one.</param>
        /// <remarks>
        /// One reservation at a time. While one is open it holds the uncommitted vertex, mapping, submesh and block
        /// tails, so a second reservation and both append paths are refused without changing anything until it is
        /// committed or cancelled. A commit that fails leaves it open, to be cancelled.
        /// </remarks>
        public bool TryReserveCutOutput(
            VpStoredGeometry parent,
            int newVertexCapacity,
            int newIndexCapacity,
            int submeshCapacity,
            int vertexBlockCapacity,
            out VpCutOutputReservation reservation)
        {
            ThrowIfDisposed();
            reservation = null;
            if (_openCutOutput != null
                || !IsMetadataReadable(parent)
                || newVertexCapacity < 0
                || newIndexCapacity < 0
                || submeshCapacity < 0
                || vertexBlockCapacity < 0)
            {
                return false;
            }

            int vertexStart = _vertices.Count;
            int submeshStart = _submeshCount;
            int blockStart = _vertexBlockCount;
            if (newVertexCapacity > _vertices.Capacity - vertexStart
                || submeshCapacity > _submeshes.Length - submeshStart
                || vertexBlockCapacity > _vertexBlocks.Length - blockStart
                || !_indices.TryReserve(newIndexCapacity, out VpIndexRangeHandle indexRange))
            {
                return false;
            }

            try
            {
                if (!_indices.TryGetReservedWriteView(indexRange, out NativeArray<uint> indexView)
                    || !_indices.TryGetState(indexRange, out _, out int indexStart, out _))
                {
                    _indices.TryCancelReservation(indexRange);
                    return false;
                }

                // Last: once this returns, the reservation owns the index range and holds the uncommitted tails.
                reservation = new VpCutOutputReservation(
                    parent,
                    indexRange,
                    indexStart,
                    vertexStart,
                    submeshStart,
                    blockStart,
                    submeshCapacity,
                    vertexBlockCapacity,
                    _vertices.GetUncommittedTail(newVertexCapacity),
                    _topologyOfVertex.GetSubArray(vertexStart, newVertexCapacity),
                    indexView);
            }
            catch
            {
                reservation = null;
                CancelWhileThrowing(indexRange);
                throw;
            }

            _openCutOutput = reservation;
            return true;
        }

        /// <summary>
        /// Gives back an open reservation without committing anything: the index range returns to the allocator and the
        /// vertices, mapping entries and metadata written into the uncommitted tails stay invisible and are simply
        /// overwritten by the next reservation. Returns false for a null, foreign or already closed reservation.
        /// </summary>
        public bool TryCancelCutOutput(VpCutOutputReservation reservation)
        {
            ThrowIfDisposed();
            if (!IsOpenReservation(reservation))
            {
                return false;
            }

            reservation.closed = true;
            _openCutOutput = null;
            return _indices.TryCancelReservation(reservation.indexRange);
        }

        /// <summary>
        /// Publishes the result of one cut written into <paramref name="reservation"/>: the appended vertices and their
        /// mapping entries, which both sides share, the shared block list of the parent's blocks plus the appended one,
        /// and the two sides' own index ranges, the positive side taking the first <paramref name="positiveIndexCount"/>
        /// indices of the reservation and the negative side the <paramref name="negativeIndexCount"/> that follow them
        /// (DESIGN 4.5.6). The unused tail of the reservation is returned. Submesh descriptors come from
        /// <paramref name="submeshes"/>, the positive side's first and then the negative side's, each side's offsets
        /// relative to that side's own published range.
        /// <para>
        /// Returns false, publishing nothing and leaving the reservation open for the caller to cancel, when the
        /// reservation is null, foreign or closed; when a count is negative or exceeds what was reserved; when the two
        /// index counts are both 0; when a side with no index is given submesh descriptors; when a side's descriptors do
        /// not cover its indices once, in order and in whole triangles; when the topology vertex count does not cover
        /// the parent's ids and the ones written for the appended vertices; or when the second side's index descriptor
        /// cannot be registered. That last check is made before anything is published, so a side is never published
        /// alone.
        /// </para>
        /// </summary>
        public bool TryCommitCutOutput(
            VpCutOutputReservation reservation,
            int newVertexCount,
            int topologyVertexCount,
            int positiveIndexCount,
            int negativeIndexCount,
            VpGeometrySubmesh[] submeshes,
            int positiveSubmeshCount,
            int negativeSubmeshCount,
            out VpStoredGeometry positive,
            out VpStoredGeometry negative)
        {
            ThrowIfDisposed();
            positive = default;
            negative = default;
            if (!IsOpenReservation(reservation) || submeshes == null)
            {
                return false;
            }

            VpStoredGeometry parent = reservation.parent;
            int blockCount = parent.blockCount + (newVertexCount > 0 ? 1 : 0);
            if (newVertexCount < 0
                || newVertexCount > reservation.NewVertexCapacity
                || positiveIndexCount < 0
                || negativeIndexCount < 0
                || (long)positiveIndexCount + negativeIndexCount > reservation.NewIndexCapacity
                || positiveIndexCount + negativeIndexCount == 0
                || positiveSubmeshCount < 0
                || negativeSubmeshCount < 0
                || (long)positiveSubmeshCount + negativeSubmeshCount > Math.Min(submeshes.Length, reservation.submeshCapacity)
                || (positiveIndexCount == 0 && positiveSubmeshCount != 0)
                || (negativeIndexCount == 0 && negativeSubmeshCount != 0)
                || blockCount > reservation.vertexBlockCapacity
                || topologyVertexCount < parent.topologyVertexCount
                || !DoSubmeshesCover(submeshes, 0, positiveSubmeshCount, positiveIndexCount)
                || !DoSubmeshesCover(submeshes, positiveSubmeshCount, negativeSubmeshCount, negativeIndexCount))
            {
                return false;
            }

            // The children's shared block list: the parent's blocks, then the block this cut appended. The parent's
            // vertices and mapping entries are named, never copied, and stay valid however the parent's range ends.
            int blockStart = reservation.vertexBlockStart;
            for (int b = 0; b < parent.blockCount; b++)
            {
                _vertexBlocks[blockStart + b] = _vertexBlocks[parent.blockStart + b];
            }

            if (newVertexCount > 0)
            {
                _vertexBlocks[blockStart + parent.blockCount] = new VpGeometryVertexBlock(reservation.vertexStart, newVertexCount);
            }

            int submeshStart = reservation.submeshStart;
            int submeshCount = positiveSubmeshCount + negativeSubmeshCount;
            for (int s = 0; s < submeshCount; s++)
            {
                _submeshes[submeshStart + s] = submeshes[s];
            }

            // The last step that can fail, and it fails before publishing either side.
            if (!_indices.TryPublishSplit(
                    reservation.indexRange,
                    positiveIndexCount,
                    negativeIndexCount,
                    out VpIndexRangeHandle positiveRange,
                    out VpIndexRangeHandle negativeRange))
            {
                return false;
            }

            reservation.closed = true;
            _openCutOutput = null;
            _vertices.Commit(newVertexCount);
            _submeshCount = submeshStart + submeshCount;
            _vertexBlockCount = blockStart + blockCount;
            if (positiveIndexCount > 0)
            {
                positive = new VpStoredGeometry(
                    reservation.vertexStart, newVertexCount, positiveRange, true, topologyVertexCount,
                    submeshStart, positiveSubmeshCount, blockStart, blockCount);
                RecordAppend(positiveRange, positive);
            }

            if (negativeIndexCount > 0)
            {
                negative = new VpStoredGeometry(
                    reservation.vertexStart, newVertexCount, negativeRange, true, topologyVertexCount,
                    submeshStart + positiveSubmeshCount, negativeSubmeshCount, blockStart, blockCount);
                RecordAppend(negativeRange, negative);
            }

            return true;
        }

        /// <summary>
        /// The blocks the geometry is made of, in order, and the number of topology vertices its mapping addresses. A
        /// view into the storage, not a copy; the mapping entries themselves are <see cref="TopologyOfVertex"/> at each
        /// block's own positions. Returns false with defaults for a default, foreign, stale or structurally inconsistent
        /// geometry, for one without a mapping, and once its index range is Free or its descriptor has been registered
        /// again.
        /// </summary>
        public bool TryGetVertexBlocks(VpStoredGeometry geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out int topologyVertexCount)
        {
            ThrowIfDisposed();
            blocks = default;
            topologyVertexCount = 0;
            if (!geometry.hasTopology || !IsMetadataReadable(geometry))
            {
                return false;
            }

            blocks = _vertexBlocks.GetSubArray(geometry.blockStart, geometry.blockCount).AsReadOnly();
            topologyVertexCount = geometry.topologyVertexCount;
            return true;
        }

        /// <summary>
        /// The mapping of a geometry made of one single block, one entry per vertex of that block, and the number of
        /// topology vertices it addresses. A view into the storage, not a copy. Returns false with defaults for a
        /// default, foreign, stale or structurally inconsistent geometry, for one appended without a mapping, for one
        /// made of several blocks — a cut result, whose blocks <see cref="TryGetVertexBlocks"/> gives — and once its
        /// index range is Free or its descriptor has been registered again.
        /// </summary>
        public bool TryGetTopology(VpStoredGeometry geometry, out NativeArray<int>.ReadOnly topologyOfVertex, out int topologyVertexCount)
        {
            ThrowIfDisposed();
            topologyOfVertex = default;
            topologyVertexCount = 0;
            if (!geometry.hasTopology || !IsMetadataReadable(geometry) || geometry.blockCount != 1)
            {
                return false;
            }

            VpGeometryVertexBlock block = _vertexBlocks[geometry.blockStart];
            topologyOfVertex = _topologyOfVertex.GetSubArray(block.vertexStart, block.vertexCount).AsReadOnly();
            topologyVertexCount = geometry.topologyVertexCount;
            return true;
        }

        /// <summary>
        /// The geometry's submesh descriptors in order, each covering its part of the published index range with an
        /// offset relative to that range's start. A view into the storage, not a copy. Returns false with a default
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
            if (_openCutOutput != null)
            {
                // An open reservation dies with the storage: its views point into memory that is about to go.
                _openCutOutput.closed = true;
                _openCutOutput = null;
            }

            _indices.Dispose();
            _vertexBlocks.Dispose();
            _submeshes.Dispose();
            _topologyOfVertex.Dispose();
            _vertices.Dispose();
        }

        /// <inheritdoc cref="VpCpuVertexStorage.TryGetCommittedRange"/>
        internal bool TryGetCommittedVertexRange(int start, int count, out NativeArray<VpRenderVertex> range)
        {
            ThrowIfDisposed();
            return _vertices.TryGetCommittedRange(start, count, out range);
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryGetLeasedSpan(VpIndexReadLease, out NativeArray{uint}, out int, out int)"/>
        internal bool TryGetLeasedIndexSpan(VpIndexReadLease lease, out NativeArray<uint> span, out int indexStart, out int indexCount)
        {
            ThrowIfDisposed();
            return _indices.TryGetLeasedSpan(lease, out span, out indexStart, out indexCount);
        }

        /// <inheritdoc cref="VpCpuIndexStorage.TryGetLeasedSpan(VpIndexReadLease, VpIndexReadLease, out NativeArray{uint}, out int, out int)"/>
        internal bool TryGetLeasedIndexSpan(
            VpIndexReadLease first,
            VpIndexReadLease second,
            out NativeArray<uint> span,
            out int indexStart,
            out int indexCount)
        {
            ThrowIfDisposed();
            return _indices.TryGetLeasedSpan(first, second, out span, out indexStart, out indexCount);
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
        /// storage's own index table, and every range it names is the one that registration was published with. Ranges
        /// that merely fit inside the storage are not enough, so one result's handle cannot be paired with another
        /// result's vertices, blocks, submeshes or topology description. Says nothing about the state of the index range.
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
                && append.submeshCount == geometry.submeshCount
                && append.blockStart == geometry.blockStart
                && append.blockCount == geometry.blockCount;
        }

        /// <summary>Records what a published descriptor registration was published with. Writes into the array taken at construction.</summary>
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
                blockStart = geometry.blockStart,
                blockCount = geometry.blockCount,
            };
        }

        /// <summary>Metadata is readable while the geometry's own index range is Published or Retiring in this storage.</summary>
        private bool IsMetadataReadable(VpStoredGeometry geometry)
        {
            return IsGeometryConsistent(geometry)
                && _indices.TryGetState(geometry.indexRange, out VpIndexRangeState state, out _, out _)
                && (state == VpIndexRangeState.Published || state == VpIndexRangeState.Retiring);
        }

        /// <summary>
        /// Whether the reservation is this storage's own open one: the object it handed out and has not yet committed
        /// or cancelled. A reservation of another storage, or one this storage has already closed, is not it.
        /// </summary>
        private bool IsOpenReservation(VpCutOutputReservation reservation)
        {
            return reservation != null
                && ReferenceEquals(reservation, _openCutOutput)
                && !reservation.closed
                && _indices.TryGetState(reservation.indexRange, out VpIndexRangeState state, out _, out _)
                && state == VpIndexRangeState.Reserved;
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

        /// <summary>Writes the single block of an appended geometry into the uncommitted block tail.</summary>
        private bool WriteOwnBlock(int blockStart, int vertexStart, int vertexCount)
        {
            _vertexBlocks[blockStart] = new VpGeometryVertexBlock(vertexStart, vertexCount);
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
        /// The descriptors [start, start + count) must cover [0, indexCount) once, in order, with no gap and no overlap,
        /// and each must hold whole triangles: a boundary inside a triangle would hand the display side a submesh it
        /// cannot draw. An empty submesh is still allowed, 0 being a multiple of 3.
        /// </summary>
        private static bool DoSubmeshesCover(VpGeometrySubmesh[] submeshes, int start, int count, int indexCount)
        {
            long covered = 0;
            for (int s = 0; s < count; s++)
            {
                VpGeometrySubmesh submesh = submeshes[start + s];
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
