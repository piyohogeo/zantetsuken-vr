using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Prepares the cut kernel's input from one geometry a <see cref="VpCpuGeometryStorage"/> owns, without copying the
    /// geometry: the kernel reads the storage's own vertex, index and topology views for as long as this adapter is
    /// held. Acquiring takes the read lease on the geometry's published index range; disposing returns it exactly once.
    /// <para>
    /// The two index numberings stay apart. The values inside the index view are global vertex numbers into the
    /// storage's committed vertices, so the vertex view is the whole committed array and the topology map is one range
    /// per vertex block of the geometry — for a geometry a cut produced, its parent's blocks and the block the cut
    /// appended, each at its own <see cref="RenderTopologyRange.vertexBase"/>, so a child can be cut again without any
    /// vertex or mapping entry being copied. A <see cref="MeshCutIndexRange.indexStart"/>, by contrast, is a position
    /// inside the index view handed to the kernel, which is exactly the leased range: a submesh therefore starts at its
    /// own <see cref="VpGeometrySubmesh.indexOffset"/>, and the physical position of the range inside the storage's
    /// index buffer is never added to it. The submesh order is kept, so the kernel's output ranges line up with the
    /// source material mapping one for one; the material index itself is not part of the kernel's input.
    /// </para>
    /// <para>
    /// The kernel's result is read in those same two numberings. When a side reports reusesInput, the geometry was
    /// not cut and its ranges come back exactly as they went in, in the input view's numbering; when the geometry was
    /// cut, the produced ranges are based at the caller's newIndexBase in the output reservation, and nothing is
    /// written into that reservation in the reuse case.
    /// </para>
    /// <para>
    /// Main thread only, and the storage must outlive both this adapter and any reader still using an input built from
    /// it. The adapter owns only the small range arrays it allocates; the geometry data stays the storage's. The kernel
    /// does not modify the input and keeps no pointer after it returns, so a synchronous call is complete when it
    /// returns. Disposing twice does nothing, and an input cannot be built after disposal.
    /// </para>
    /// </summary>
    public sealed class VpStorageCutInput : IDisposable
    {
        private readonly VpCpuGeometryStorage _storage;
        private readonly VpStoredGeometry _geometry;
        private readonly VpIndexReadLease _lease;
        private readonly NativeArray<uint>.ReadOnly _indices;
        private readonly NativeArray<VpGeometryVertexBlock>.ReadOnly _blocks;
        private readonly int _topologyVertexCount;
        private NativeArray<MeshCutIndexRange> _ranges;
        private NativeArray<RenderTopologyRange> _topologyRanges;
        private bool _disposed;

        private VpStorageCutInput(
            VpCpuGeometryStorage storage,
            VpStoredGeometry geometry,
            VpIndexReadLease lease,
            NativeArray<uint>.ReadOnly indices,
            NativeArray<VpGeometryVertexBlock>.ReadOnly blocks,
            int topologyVertexCount,
            NativeArray<MeshCutIndexRange> ranges,
            NativeArray<RenderTopologyRange> topologyRanges)
        {
            _storage = storage;
            _geometry = geometry;
            _lease = lease;
            _indices = indices;
            _blocks = blocks;
            _topologyVertexCount = topologyVertexCount;
            _ranges = ranges;
            _topologyRanges = topologyRanges;
        }

        /// <summary>The geometry this input reads, as the storage returned it.</summary>
        public VpStoredGeometry Geometry => _geometry;

        /// <summary>One kernel range per submesh of the geometry, in the geometry's submesh order.</summary>
        public int RangeCount => _ranges.IsCreated ? _ranges.Length : 0;

        /// <summary>The number of vertex blocks the topology map is made of: the geometry's own and every inherited one.</summary>
        public int TopologyRangeCount => _topologyRanges.IsCreated ? _topologyRanges.Length : 0;

        /// <summary>The length of the leased index view the ranges address.</summary>
        public int IndexViewLength => _indices.Length;

        /// <summary>The topology vertex id space of the geometry; ids a cut creates start here.</summary>
        public int TopologyVertexCount => _topologyVertexCount;

        public bool IsDisposed => _disposed;

        /// <summary>
        /// Takes the geometry's index read lease and prepares its kernel ranges, one per submesh in submesh order, and
        /// its topology ranges, one per vertex block. Returns false with a null adapter, holding no lease and having
        /// allocated nothing, when the storage is null, the geometry is not one the storage owns (default, foreign,
        /// stale, or a description that does not match its own append), it has no topology mapping, it was not accepted
        /// as a cut input (<see cref="VpStoredGeometry.cutInputAccepted"/>: appended through the input gate, or cut from
        /// a geometry that was), it has no submesh or no index, or its index range is not Published — which covers a
        /// range that is Retiring or Free. A topology mapping alone does not make a geometry cuttable: an ordinary
        /// prepared append is refused here, before any lease.
        /// </summary>
        public static bool TryAcquire(VpCpuGeometryStorage storage, VpStoredGeometry geometry, out VpStorageCutInput input)
        {
            input = null;
            if (storage == null
                || !geometry.cutInputAccepted
                || !storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out int topologyVertexCount)
                || !storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes)
                || !storage.TryGetIndexState(geometry.indexRange, out _, out _, out int publishedIndexCount))
            {
                return false;
            }

            // Nothing here invents a range: one kernel range per submesh, so a geometry with no submesh or no index is
            // refused rather than given a range of its own. Refused before the lease, so none is taken.
            if (submeshes.Length == 0 || publishedIndexCount == 0 || blocks.Length == 0)
            {
                return false;
            }

            // From here on the lease is held, and everything allocated below must be given back until the adapter owns it.
            if (!storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices))
            {
                return false;
            }

            var ranges = default(NativeArray<MeshCutIndexRange>);
            var topologyRanges = default(NativeArray<RenderTopologyRange>);
            try
            {
                ranges = new NativeArray<MeshCutIndexRange>(submeshes.Length, Allocator.Persistent);
                topologyRanges = new NativeArray<RenderTopologyRange>(blocks.Length, Allocator.Persistent);
                for (int s = 0; s < submeshes.Length; s++)
                {
                    VpGeometrySubmesh submesh = submeshes[s];
                    // indexOffset is relative to the geometry's own range, which is exactly the leased view
                    ranges[s] = new MeshCutIndexRange { indexStart = (uint)submesh.indexOffset, indexCount = submesh.indexCount };
                }

                // Last: once this returns, the adapter owns the lease and both arrays.
                input = new VpStorageCutInput(storage, geometry, lease, indices, blocks, topologyVertexCount, ranges, topologyRanges);
                return true;
            }
            catch
            {
                if (topologyRanges.IsCreated)
                {
                    topologyRanges.Dispose();
                }

                if (ranges.IsCreated)
                {
                    ranges.Dispose();
                }

                storage.TryReleaseIndexReadLease(lease);
                throw;
            }
        }

        /// <summary>
        /// The kernel input over the storage's views for one plane. Returns false with a default input after disposal.
        /// The vertex view is the storage's committed vertices as they stand now, so a geometry appended after this
        /// adapter was acquired only makes the view longer and moves nothing. The returned input borrows the storage's
        /// memory and this adapter's ranges: it may be used only while the adapter is held.
        /// </summary>
        public unsafe bool TryGetInput(float4 plane, out MeshCutInput input)
        {
            input = default;
            if (_disposed)
            {
                return false;
            }

            NativeArray<VpRenderVertex>.ReadOnly vertices = _storage.Vertices;
            NativeArray<int>.ReadOnly topologyOfVertex = _storage.TopologyOfVertex;
            int* topologyBase = (int*)topologyOfVertex.GetUnsafeReadOnlyPtr();
            MeshCutIndexRange* ranges = (MeshCutIndexRange*)_ranges.GetUnsafePtr();
            RenderTopologyRange* topologyRanges = (RenderTopologyRange*)_topologyRanges.GetUnsafePtr();
            for (int b = 0; b < _blocks.Length; b++)
            {
                VpGeometryVertexBlock block = _blocks[b];
                topologyRanges[b] = new RenderTopologyRange
                {
                    vertexBase = (uint)block.vertexStart,
                    count = block.vertexCount,
                    topologyVertex = topologyBase + block.vertexStart,
                };
            }

            input = new MeshCutInput
            {
                vertices = (VpRenderVertex*)vertices.GetUnsafeReadOnlyPtr(),
                vertexViewLength = vertices.Length,
                indices = (uint*)_indices.GetUnsafeReadOnlyPtr(),
                indexViewLength = _indices.Length,
                ranges = ranges,
                rangeCount = _ranges.Length,
                topology = new RenderCutTopologyMap
                {
                    ranges = topologyRanges,
                    rangeCount = _topologyRanges.Length,
                    topologyVertexCount = _topologyVertexCount,
                },
                plane = plane,
            };
            return true;
        }

        /// <summary>Returns the read lease once and frees the adapter's own ranges. Disposing again does nothing.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_topologyRanges.IsCreated)
            {
                _topologyRanges.Dispose();
            }

            if (_ranges.IsCreated)
            {
                _ranges.Dispose();
            }

            _storage.TryReleaseIndexReadLease(_lease);
        }
    }
}
