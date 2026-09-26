using System;
using System.Collections.Generic;
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
    public sealed partial class VpCpuGeometryStorage : IDisposable
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
            public bool cutInputAccepted;

            /// <summary>
            /// Recorded with the geometry by its producer: the span of vertex indices its indices name and the
            /// bounds of those vertices. Every producer records them (the appends measure once at the append, a cut
            /// takes them from the kernel); a geometry without a record is refused by the draw, never measured there.
            /// </summary>
            public bool hasExtent;
            public int referencedStart;
            public int referencedCount;
            public Vector3 boundsMin;
            public Vector3 boundsMax;
        }

        private readonly VpCpuVertexStorage _vertices;
        private readonly VpCpuIndexStorage _indices;
        private NativeArray<int> _topologyOfVertex;
        private NativeArray<VpGeometrySubmesh> _submeshes;

        /// <summary>Beside each submesh, the bounds of the vertices its indices name; valid where the geometry's record says so.</summary>
        private NativeArray<VpGeometryBounds> _submeshBounds;
        private NativeArray<VpGeometryVertexBlock> _vertexBlocks;
        private readonly AppendRecord[] _appendOfDescriptor;

        // At most one cut output reservation is open at a time, because an open one holds the uncommitted tails of the
        // vertices, the mapping, the submeshes and the blocks: a second writer into the same tails would have its work
        // overwritten or would overwrite what the first is about to commit.
        // Room is given out as spans, so several cuts may hold room in the same arrays at once and write only their
        // own. The index side already had an allocator; these are its counterparts for the parts addressed by
        // position.
        private readonly VpSpanAllocator _vertexSpans;
        private readonly VpSpanAllocator _submeshSpans;
        private readonly VpSpanAllocator _vertexBlockSpans;
        private readonly List<VpCutOutputReservation> _openCutOutputs = new List<VpCutOutputReservation>();
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
                // Inside the try: these can throw on a capacity the vertex storage accepted, and the vertices are
                // already native memory by then.
                _vertexSpans = new VpSpanAllocator(vertexCapacity);
                _submeshSpans = new VpSpanAllocator(submeshCapacity);
                _vertexBlockSpans = new VpSpanAllocator(vertexBlockCapacity);
                _indices = new VpCpuIndexStorage(indexCapacity, indexDescriptorCapacity, allocator);
                _topologyOfVertex = new NativeArray<int>(vertexCapacity, allocator);
                _submeshes = new NativeArray<VpGeometrySubmesh>(submeshCapacity, allocator);
                _submeshBounds = new NativeArray<VpGeometryBounds>(submeshCapacity, allocator);
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

                if (_submeshBounds.IsCreated)
                {
                    _submeshBounds.Dispose();
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

        /// <summary>
        /// How many vertex slots are free: neither held by an open reservation nor taken by something published. It
        /// is what a further reservation may be given, and it comes back when a reservation is cancelled or commits
        /// less than it took. Retiring a geometry does not return its vertices, which is unchanged by this.
        /// </summary>
        public int FreeVertexRoom => _vertexSpans.Capacity - _vertexSpans.Used;

        /// <summary>The largest single vertex span that could be reserved right now, which fragmentation lowers.</summary>
        public int LargestFreeVertexSpan => _vertexSpans.LargestFreeSpan;

        /// <summary>How many separate free vertex spans there are: one when the free room is in one piece.</summary>
        public int FreeVertexSpanCount => _vertexSpans.FreeSpanCount;

        /// <summary>How many submesh descriptor slots are free, by the same rule as the vertices.</summary>
        public int FreeSubmeshRoom => _submeshSpans.Capacity - _submeshSpans.Used;

        /// <summary>How many vertex block slots are free, by the same rule as the vertices.</summary>
        public int FreeVertexBlockRoom => _vertexBlockSpans.Capacity - _vertexBlockSpans.Used;

        public int VertexCount => _vertices.Count;

        public int IndexCapacity => _indices.IndexCapacity;

        public int IndexDescriptorCapacity => _indices.DescriptorCapacity;

        /// <summary>How many indices are free for a reservation, the way <see cref="FreeVertexRoom"/> is.</summary>
        public int FreeIndexRoom => _indices.FreeIndexRoom;

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
            if (mesh == null || _openCutOutputs.Count > 0)
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

                int vertexCount = data.vertexCount;
                int submeshCount = data.subMeshCount;
                if (!TryTakeSpans(vertexCount, submeshCount, 1, out int vertexStart, out int submeshStart, out int blockStart))
                {
                    return false;
                }

                if (!_indices.TryReserve((int)totalIndexCount, out VpIndexRangeHandle indexRange))
                {
                    GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                    return false;
                }

                bool published;
                try
                {
                    published = _indices.TryGetReservedWriteView(indexRange, out NativeArray<uint> indices)
                        && VpMeshConverter.TryConvert(data, _vertices.GetSpan(vertexStart, vertexCount), indices, out _, out _)
                        && TryRebase(indices, vertexStart, vertexCount)
                        && WriteMeshSubmeshes(data, submeshStart)
                        && WriteOwnBlock(blockStart, vertexStart, vertexCount)
                        && _indices.TryPublish(indexRange);
                }
                catch
                {
                    GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                    CancelWhileThrowing(indexRange);
                    throw;
                }

                if (!published)
                {
                    _indices.TryCancelReservation(indexRange);
                    GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                    return false;
                }

                PublishSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                geometry = new VpStoredGeometry(vertexStart, vertexCount, indexRange, false, 0, submeshStart, submeshCount, blockStart, 1);
                RecordAppend(indexRange, geometry);
                RecordExtentByMeasuring(geometry);
                return true;
            }
        }

        /// <summary>
        /// Appends a prepared geometry whose arrays the caller owns: its vertices in the common 16 byte layout, its
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
        /// reserved. Manifoldness, winding and finiteness are not checked here.
        /// </para>
        /// <para>
        /// This is the ordinary append: the geometry keeps its topology mapping and can be displayed, but it is not a
        /// cut input — <see cref="VpStoredGeometry.cutInputAccepted"/> stays false, whatever its topology. An open mesh
        /// may be appended and shown this way. A geometry that is to be cut goes through <see cref="TryAppendCuttable"/>.
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
            return AppendPrepared(vertices, localIndices, topologyOfVertex, topologyVertexCount, submeshes, false, out geometry);
        }

        /// <summary>
        /// Appends a prepared geometry as a cut input: the arrays are first put through <see cref="VpCutInputGate"/>,
        /// DESIGN 6.2's input contract, and only a geometry that passes is appended, exactly as
        /// <see cref="TryAppendPrepared"/> would append it, and recorded as <see cref="VpStoredGeometry.cutInputAccepted"/>.
        /// This is the one place a geometry is judged: nothing re-checks it per frame or per draw, and what a cut of it
        /// produces inherits the acceptance through <see cref="TryCommitCutOutput"/> without being judged again.
        /// <para>
        /// Returns false with a default geometry when the gate refuses — <paramref name="verdict"/> says why — or when
        /// the append itself fails for any reason <see cref="TryAppendPrepared"/> gives. The gate runs before anything
        /// is reserved, so a refusal takes no vertex, index, submesh or block capacity and changes no geometry already
        /// stored.
        /// </para>
        /// </summary>
        public bool TryAppendCuttable(
            VpRenderVertex[] vertices,
            uint[] localIndices,
            int[] topologyOfVertex,
            int topologyVertexCount,
            VpGeometrySubmesh[] submeshes,
            out VpStoredGeometry geometry,
            out VpCutInputVerdict verdict)
        {
            ThrowIfDisposed();
            geometry = default;
            verdict = VpCutInputGate.Check(vertices, localIndices, topologyOfVertex, topologyVertexCount, submeshes);
            if (!verdict.Accepted)
            {
                return false;
            }

            return AppendPrepared(vertices, localIndices, topologyOfVertex, topologyVertexCount, submeshes, true, out geometry);
        }

        private bool AppendPrepared(
            VpRenderVertex[] vertices,
            uint[] localIndices,
            int[] topologyOfVertex,
            int topologyVertexCount,
            VpGeometrySubmesh[] submeshes,
            bool cutInputAccepted,
            out VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            geometry = default;
            if (vertices == null || localIndices == null || topologyOfVertex == null || submeshes == null || _openCutOutputs.Count > 0)
            {
                return false;
            }

            int vertexCount = vertices.Length;
            int indexCount = localIndices.Length;
            int submeshCount = submeshes.Length;
            if (topologyVertexCount < 0
                || topologyOfVertex.Length != vertexCount
                || indexCount % 3 != 0
                || !AreTopologyIdsInRange(topologyOfVertex, topologyVertexCount)
                || !DoSubmeshesCover(submeshes, 0, submeshes.Length, indexCount))
            {
                return false;
            }

            // Prepared input is already compact: invalid encodings must not reach GPU readers, even for display-only
            // geometry. Validate before taking any spans; no full float vertex copy is needed.
            for (int v = 0; v < vertexCount; v++)
            {
                if (!vertices[v].HasValidAttributes
                    || !Unity.Mathematics.math.all(Unity.Mathematics.math.isfinite((Unity.Mathematics.float3)vertices[v].position))) return false;
            }

            // Where the vertices go is the allocator's answer now, and the indices are checked against it, so the room
            // is taken before that check and given back if it fails.
            if (!TryTakeSpans(vertexCount, submeshCount, 1, out int vertexStart, out int submeshStart, out int blockStart))
            {
                return false;
            }

            if (!AreIndicesInRange(localIndices, vertexStart, vertexCount))
            {
                GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                return false;
            }

            if (!_indices.TryReserve(indexCount, out VpIndexRangeHandle indexRange))
            {
                GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                return false;
            }

            bool published;
            try
            {
                if (!_indices.TryGetReservedWriteView(indexRange, out NativeArray<uint> indexView))
                {
                    _indices.TryCancelReservation(indexRange);
                    GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                    return false;
                }

                NativeArray<VpRenderVertex> vertexSpan = _vertices.GetSpan(vertexStart, vertexCount);
                for (int v = 0; v < vertexCount; v++)
                {
                    vertexSpan[v] = vertices[v];
                    _topologyOfVertex[vertexStart + v] = topologyOfVertex[v];
                }

                for (int i = 0; i < indexCount; i++)
                {
                    indexView[i] = (uint)(vertexStart + localIndices[i]);
                }

                for (int s = 0; s < submeshCount; s++)
                {
                    _submeshes[submeshStart + s] = submeshes[s];
                    _submeshBounds[submeshStart + s] = MeasureSubmesh(vertices, localIndices, submeshes[s]);
                }

                WriteOwnBlock(blockStart, vertexStart, vertexCount);

                // The last step that can fail: what follows only publishes the spans already written.
                published = _indices.TryPublish(indexRange);
            }
            catch
            {
                GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                CancelWhileThrowing(indexRange);
                throw;
            }

            if (!published)
            {
                _indices.TryCancelReservation(indexRange);
                GiveBackSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
                return false;
            }

            PublishSpans(vertexStart, vertexCount, submeshStart, submeshCount, blockStart, 1);
            geometry = new VpStoredGeometry(
                vertexStart, vertexCount, indexRange, true, topologyVertexCount, submeshStart, submeshCount, blockStart, 1, cutInputAccepted);
            RecordAppend(indexRange, geometry);
            RecordExtentFromSubmeshes(indexRange, submeshStart, submeshCount, vertexStart, localIndices, 0, indexCount);
            return true;
        }

        /// <summary>
        /// Records the extent of a geometry just published from a Mesh by measuring its published indices once, here
        /// at the append -- the one producer that has no arrays of its own to measure from.
        /// </summary>
        private void RecordExtentByMeasuring(VpStoredGeometry geometry)
        {
            if (!TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view))
            {
                return;
            }

            try
            {
                NativeArray<VpRenderVertex>.ReadOnly vertices = Vertices;
                uint lo = uint.MaxValue, hi = 0;
                for (int s = 0; s < geometry.submeshCount; s++)
                {
                    VpGeometrySubmesh submesh = _submeshes[geometry.submeshStart + s];
                    var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
                    var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
                    for (int i = submesh.indexOffset; i < submesh.indexOffset + submesh.indexCount; i++)
                    {
                        uint g = view[i];
                        if (g >= (uint)vertices.Length)
                        {
                            return;
                        }

                        Vector3 p = vertices[(int)g].position;
                        min = Vector3.Min(min, p);
                        max = Vector3.Max(max, p);
                        lo = Math.Min(lo, g);
                        hi = Math.Max(hi, g);
                    }

                    _submeshBounds[geometry.submeshStart + s] = new VpGeometryBounds(min, max);
                }

                if (view.Length > 0)
                {
                    RecordExtent(geometry.indexRange, geometry.submeshStart, geometry.submeshCount, (int)lo, (int)(hi - lo + 1));
                }
            }
            finally
            {
                TryReleaseIndexReadLease(lease);
            }
        }

        /// <summary>The bounds of the vertices one submesh's local indices name.</summary>
        private static VpGeometryBounds MeasureSubmesh(VpRenderVertex[] vertices, uint[] localIndices, VpGeometrySubmesh submesh)
        {
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            for (int i = submesh.indexOffset; i < submesh.indexOffset + submesh.indexCount; i++)
            {
                Vector3 p = vertices[localIndices[i]].position;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            return new VpGeometryBounds(min, max);
        }

        /// <summary>
        /// Records an appended geometry's extent from the submesh bounds just written and its local indices: the
        /// referenced span is the lowest to the highest index named, offset to the storage's vertex space.
        /// </summary>
        private void RecordExtentFromSubmeshes(
            VpIndexRangeHandle indexRange, int submeshStart, int submeshCount, int vertexStart, uint[] localIndices,
            int indexOffset, int indexCount)
        {
            if (indexCount <= 0)
            {
                return;
            }

            uint lo = uint.MaxValue, hi = 0;
            for (int i = indexOffset; i < indexOffset + indexCount; i++)
            {
                lo = Math.Min(lo, localIndices[i]);
                hi = Math.Max(hi, localIndices[i]);
            }

            RecordExtent(indexRange, submeshStart, submeshCount, vertexStart + (int)lo, (int)(hi - lo + 1));
        }

        /// <summary>
        /// Records the extent of the geometry published under <paramref name="indexRange"/>: the union of its
        /// submeshes' bounds (those with indices) and the referenced vertex span its producer reported.
        /// </summary>
        private void RecordExtent(VpIndexRangeHandle indexRange, int submeshStart, int submeshCount, int referencedStart, int referencedCount)
        {
            var min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            var max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            bool any = false;
            for (int s = 0; s < submeshCount; s++)
            {
                if (_submeshes[submeshStart + s].indexCount <= 0)
                {
                    continue;
                }

                VpGeometryBounds b = _submeshBounds[submeshStart + s];
                min = Vector3.Min(min, b.min);
                max = Vector3.Max(max, b.max);
                any = true;
            }

            if (!any || referencedCount <= 0)
            {
                return;
            }

            AppendRecord record = _appendOfDescriptor[indexRange.descriptor];
            record.hasExtent = true;
            record.referencedStart = referencedStart;
            record.referencedCount = referencedCount;
            record.boundsMin = min;
            record.boundsMax = max;
            _appendOfDescriptor[indexRange.descriptor] = record;
        }

        /// <summary>
        /// The extent recorded for a published geometry: the span of storage vertex indices its indices name and
        /// the bounds of those vertices, in the geometry's own coordinates. False for a geometry without a record
        /// (an append whose indices could not be read), which the draw then refuses rather than measures.
        /// </summary>
        public bool TryGetPublishedExtent(VpStoredGeometry geometry, out int referencedStart, out int referencedCount, out Bounds localBounds)
        {
            ThrowIfDisposed();
            referencedStart = 0;
            referencedCount = 0;
            localBounds = default;
            if (!IsMetadataReadable(geometry))
            {
                return false;
            }

            AppendRecord record = _appendOfDescriptor[geometry.indexRange.descriptor];
            if (record.generation != geometry.indexRange.generation || !record.hasExtent)
            {
                return false;
            }

            referencedStart = record.referencedStart;
            referencedCount = record.referencedCount;
            localBounds = new Bounds((record.boundsMin + record.boundsMax) * 0.5f, record.boundsMax - record.boundsMin);
            return true;
        }

        /// <summary>The recorded bounds beside each of the geometry's submeshes; only meaningful where <see cref="TryGetPublishedExtent"/> is true.</summary>
        public bool TryGetSubmeshBounds(VpStoredGeometry geometry, out NativeArray<VpGeometryBounds>.ReadOnly bounds)
        {
            ThrowIfDisposed();
            bounds = default;
            if (!IsMetadataReadable(geometry))
            {
                return false;
            }

            bounds = _submeshBounds.GetSubArray(geometry.submeshStart, geometry.submeshCount).AsReadOnly();
            return true;
        }

        /// <summary>
        /// Sets aside room for the output of one cut of <paramref name="parent"/>: the vertices the cut may append and
        /// their mapping entries, one index range for both sides together, the descriptor the second side's range will
        /// take, and the metadata the two sides may need. Nothing is visible to a reader until the reservation is
        /// committed. Returns false with a null reservation, having taken nothing, when the parent is not a geometry of
        /// this storage whose metadata is readable, when a capacity is negative, when a free tail is too small, or when
        /// the index range or either of its two descriptors cannot be reserved.
        /// </summary>
        /// <param name="submeshCapacity">Descriptors the two sides may use together, normally twice the parent's.</param>
        /// <param name="vertexBlockCapacity">Blocks the children's shared list may use, normally the parent's plus one.</param>
        /// <remarks>
        /// **Several reservations may be open at once.** Each holds spans of its own -- vertices with their mapping
        /// entries, submesh descriptors, vertex blocks, and an index range with the descriptor its split will use --
        /// so two cuts of this storage write in different places and their kernels may run together, and nothing taken
        /// after a reservation, by another reservation or an append, can leave its commit short of a descriptor. A span
        /// is never moved, so taking one while a worker writes another leaves that worker's views valid. What is
        /// refused while any reservation is open is a mesh or prepared append, which is unchanged; a direct skin
        /// append takes room of its own beside the open ones. A commit that fails leaves its reservation open, to be
        /// cancelled.
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
            if (!IsMetadataReadable(parent)
                || newVertexCapacity < 0
                || newIndexCapacity < 0
                || submeshCapacity < 0
                || vertexBlockCapacity < 0)
            {
                return false;
            }

            if (!TryTakeSpans(newVertexCapacity, submeshCapacity, vertexBlockCapacity, out int vertexStart, out int submeshStart, out int blockStart))
            {
                return false;
            }

            if (!_indices.TryReserve(newIndexCapacity, out VpIndexRangeHandle indexRange))
            {
                GiveBackSpans(vertexStart, newVertexCapacity, submeshStart, submeshCapacity, blockStart, vertexBlockCapacity);
                return false;
            }

            // The second side's descriptor, taken now as an empty range and owned until the commit or the cancel.
            if (!_indices.TryReserve(0, out VpIndexRangeHandle splitDescriptor))
            {
                _indices.TryCancelReservation(indexRange);
                GiveBackSpans(vertexStart, newVertexCapacity, submeshStart, submeshCapacity, blockStart, vertexBlockCapacity);
                return false;
            }

            try
            {
                if (!_indices.TryGetReservedWriteView(indexRange, out NativeArray<uint> indexView)
                    || !_indices.TryGetState(indexRange, out _, out int indexStart, out _))
                {
                    _indices.TryCancelReservation(splitDescriptor);
                    _indices.TryCancelReservation(indexRange);
                    GiveBackSpans(vertexStart, newVertexCapacity, submeshStart, submeshCapacity, blockStart, vertexBlockCapacity);
                    return false;
                }

                // Last: once this returns, the reservation owns the index range and holds the uncommitted tails.
                reservation = new VpCutOutputReservation(
                    parent,
                    indexRange,
                    splitDescriptor,
                    indexStart,
                    vertexStart,
                    submeshStart,
                    blockStart,
                    submeshCapacity,
                    vertexBlockCapacity,
                    _vertices.GetSpan(vertexStart, newVertexCapacity),
                    _topologyOfVertex.GetSubArray(vertexStart, newVertexCapacity),
                    indexView);
            }
            catch
            {
                reservation = null;
                GiveBackSpans(vertexStart, newVertexCapacity, submeshStart, submeshCapacity, blockStart, vertexBlockCapacity);
                CancelWhileThrowing(splitDescriptor);
                CancelWhileThrowing(indexRange);
                throw;
            }

            _openCutOutputs.Add(reservation);
            return true;
        }

        /// <summary>
        /// Gives back an open reservation without committing anything: the index range returns to its allocator and
        /// **every span goes back whole**, to be handed out again. What was written into them stays where it is and is
        /// simply overwritten by whoever takes those slots next; nothing of it was ever visible. No other
        /// reservation's spans are touched. Returns false for a null, foreign or already closed reservation.
        /// </summary>
        public bool TryCancelCutOutput(VpCutOutputReservation reservation)
        {
            ThrowIfDisposed();
            if (!IsOpenReservation(reservation))
            {
                return false;
            }

            reservation.closed = true;
            _openCutOutputs.Remove(reservation);
            GiveBackSpans(
                reservation.vertexStart, reservation.NewVertexCapacity,
                reservation.submeshStart, reservation.submeshCapacity,
                reservation.vertexBlockStart, reservation.vertexBlockCapacity);
            _indices.TryCancelReservation(reservation.splitDescriptor);
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
        /// the parent's ids and the ones written for the appended vertices. The second side's index descriptor is the
        /// reservation's own, taken when it was reserved, so a commit is never short of one; a side is never published
        /// alone.
        /// </para>
        /// <para>
        /// The extent comes with the sides and is required: the bounds beside each described submesh
        /// (<paramref name="submeshBounds"/>, in the submeshes' order, at least as many as are described) and, for
        /// each produced side, its lowest and highest referenced storage vertex index. A commit without them is
        /// refused rather than published without a record, because the draw refuses a geometry without one
        /// (<see cref="TryGetPublishedExtent"/>) and measures nothing itself.
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
            VpGeometryBounds[] submeshBounds,
            uint positiveReferencedLo,
            uint positiveReferencedHi,
            uint negativeReferencedLo,
            uint negativeReferencedHi,
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
                || !DoSubmeshesCover(submeshes, positiveSubmeshCount, negativeSubmeshCount, negativeIndexCount)
                || submeshBounds == null
                || submeshBounds.Length < positiveSubmeshCount + negativeSubmeshCount
                || (positiveIndexCount > 0 && positiveReferencedHi < positiveReferencedLo)
                || (negativeIndexCount > 0 && negativeReferencedHi < negativeReferencedLo))
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
                _submeshBounds[submeshStart + s] = submeshBounds[s];
            }

            // The last step that can fail, and it fails before publishing either side.
            if (!_indices.TryPublishSplit(
                    reservation.indexRange,
                    positiveIndexCount,
                    negativeIndexCount,
                    reservation.splitDescriptor,
                    out VpIndexRangeHandle positiveRange,
                    out VpIndexRangeHandle negativeRange))
            {
                return false;
            }

            reservation.closed = true;
            _openCutOutputs.Remove(reservation);

            // Only what was used is published, and the rest of each span goes back at once, so a run that reserved
            // generously and wrote little leaves the room it did not need to the next cut.
            PublishSpans(
                reservation.vertexStart, newVertexCount,
                submeshStart, submeshCount,
                blockStart, blockCount);
            GiveBackSpans(
                reservation.vertexStart + newVertexCount, reservation.NewVertexCapacity - newVertexCount,
                submeshStart + submeshCount, reservation.submeshCapacity - submeshCount,
                blockStart + blockCount, reservation.vertexBlockCapacity - blockCount);

            // What the cut produces inherits the parent's acceptance as a cut input, without being judged again
            // (DESIGN 6.2: the cut side inherits the invariants of an accepted input). The parent is the one the
            // reservation was taken for, checked against this storage's own record when it was taken; the flag cannot
            // be chosen here or by any ordinary append.
            bool inheritedAcceptance = parent.cutInputAccepted;
            if (positiveIndexCount > 0)
            {
                positive = new VpStoredGeometry(
                    reservation.vertexStart, newVertexCount, positiveRange, true, topologyVertexCount,
                    submeshStart, positiveSubmeshCount, blockStart, blockCount, inheritedAcceptance);
                RecordAppend(positiveRange, positive);
                RecordExtent(positiveRange, submeshStart, positiveSubmeshCount, (int)positiveReferencedLo, (int)(positiveReferencedHi - positiveReferencedLo + 1));
            }

            if (negativeIndexCount > 0)
            {
                negative = new VpStoredGeometry(
                    reservation.vertexStart, newVertexCount, negativeRange, true, topologyVertexCount,
                    submeshStart + positiveSubmeshCount, negativeSubmeshCount, blockStart, blockCount, inheritedAcceptance);
                RecordAppend(negativeRange, negative);
                RecordExtent(negativeRange, submeshStart + positiveSubmeshCount, negativeSubmeshCount, (int)negativeReferencedLo, (int)(negativeReferencedHi - negativeReferencedLo + 1));
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

            // Every open reservation dies with the storage: their views point into memory that is about to go, and
            // there may be several of them.
            for (int i = 0; i < _openCutOutputs.Count; i++)
            {
                _openCutOutputs[i].closed = true;
            }

            _openCutOutputs.Clear();

            _indices.Dispose();
            _vertexBlocks.Dispose();
            _submeshBounds.Dispose();
            _submeshes.Dispose();
            _topologyOfVertex.Dispose();
            _vertices.Dispose();
        }

        /// <summary>
        /// Whether every vertex of <paramref name="count"/> from <paramref name="start"/> is published: taken, and
        /// held by no open reservation. **The high-water is not the answer.** Spans publish in whatever order their
        /// cuts finish, so below it there may be a reservation still being written into, or room that has come back
        /// free -- and neither may be read or transferred as though it were a published vertex.
        /// </summary>
        internal bool ArePublishedVertices(int start, int count)
        {
            if (count == 0)
            {
                return start >= 0 && start <= _vertices.Capacity;
            }

            if (!_vertexSpans.IsWhollyTaken(start, count))
            {
                return false;
            }

            int end = start + count;
            for (int i = 0; i < _openCutOutputs.Count; i++)
            {
                VpCutOutputReservation open = _openCutOutputs[i];
                int openStart = open.VertexStart;
                int openEnd = openStart + open.NewVertexCapacity;
                if (openStart < end && start < openEnd)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A window on published vertices, for a reader that takes them where they are. False when any slot of the
        /// range is not published: open, or free. **Not "below the high-water"** -- see
        /// <see cref="ArePublishedVertices"/>.
        /// </summary>
        public bool TryGetCommittedVertices(int start, int count, out NativeArray<VpRenderVertex> range)
        {
            return TryGetCommittedVertexRange(start, count, out range);
        }

        /// <inheritdoc cref="VpCpuVertexStorage.TryGetCommittedRange"/>
        internal bool TryGetCommittedVertexRange(int start, int count, out NativeArray<VpRenderVertex> range)
        {
            ThrowIfDisposed();
            if (!ArePublishedVertices(start, count))
            {
                range = default;
                return false;
            }

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
                && append.blockCount == geometry.blockCount
                && append.cutInputAccepted == geometry.cutInputAccepted;
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
                cutInputAccepted = geometry.cutInputAccepted,
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
        /// Takes one span of each array, or none at all: a request that cannot be met in full gives back whatever it
        /// had taken before answering. Room refused here is room this storage does not have free right now, which is
        /// an ordinary outcome and not a failure of the cut.
        /// </summary>
        private bool TryTakeSpans(
            int vertexCount, int submeshCount, int blockCount,
            out int vertexStart, out int submeshStart, out int blockStart)
        {
            submeshStart = 0;
            blockStart = 0;
            if (!_vertexSpans.TryTake(vertexCount, out vertexStart))
            {
                return false;
            }

            if (!_submeshSpans.TryTake(submeshCount, out submeshStart))
            {
                _vertexSpans.GiveBack(vertexStart, vertexCount);
                return false;
            }

            if (!_vertexBlockSpans.TryTake(blockCount, out blockStart))
            {
                _submeshSpans.GiveBack(submeshStart, submeshCount);
                _vertexSpans.GiveBack(vertexStart, vertexCount);
                return false;
            }

            return true;
        }

        /// <summary>Gives three spans back, each merging with its free neighbours. An empty span gives back nothing.</summary>
        private void GiveBackSpans(
            int vertexStart, int vertexCount, int submeshStart, int submeshCount, int blockStart, int blockCount)
        {
            _vertexSpans.GiveBack(vertexStart, vertexCount);
            _submeshSpans.GiveBack(submeshStart, submeshCount);
            _vertexBlockSpans.GiveBack(blockStart, blockCount);
        }

        /// <summary>
        /// Makes three written spans visible. Each count is how far publishing has reached, not how many slots are
        /// live: spans publish in whatever order their cuts finish, so a slot below one of these may belong to a span
        /// still open or to one given back, and no published geometry names it.
        /// </summary>
        private void PublishSpans(
            int vertexStart, int vertexCount, int submeshStart, int submeshCount, int blockStart, int blockCount)
        {
            _vertices.Publish(vertexStart, vertexCount);
            _submeshCount = Math.Max(_submeshCount, submeshStart + submeshCount);
            _vertexBlockCount = Math.Max(_vertexBlockCount, blockStart + blockCount);
        }

        /// <summary>
        /// Whether the reservation is one of this storage's open ones: an object it handed out and has not yet
        /// committed or cancelled. **There may be several**; a reservation of another storage, or one this storage has
        /// already closed, is not among them.
        /// </summary>
        private bool IsOpenReservation(VpCutOutputReservation reservation)
        {
            return reservation != null
                && _openCutOutputs.Contains(reservation)
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

        internal static bool AreTopologyIdsInRange(int[] topologyOfVertex, int topologyVertexCount)
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

        internal static bool AreIndicesInRange(uint[] localIndices, int vertexStart, int vertexCount)
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
        internal static bool DoSubmeshesCover(VpGeometrySubmesh[] submeshes, int start, int count, int indexCount)
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
