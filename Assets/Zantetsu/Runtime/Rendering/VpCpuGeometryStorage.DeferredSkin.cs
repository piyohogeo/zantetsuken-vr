using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.Rendering
{
    public sealed partial class VpCpuGeometryStorage
    {
        // The geometries whose vertices are still to be written by their deferred CPU bake (DESIGN 4.5.15): the
        // descriptor and generation of each. A pending geometry is shown by the display (its vertices come from the GPU
        // bake) but is no cut input until it is completed; CutDag.RegisterBaseGeometry asks IsDeferredPending.
        private readonly Dictionary<int, uint> _deferredPending = new Dictionary<int, uint>();

        /// <summary>How many deferred geometries await their CPU bake now.</summary>
        public int DeferredPendingCount => _deferredPending.Count;

        /// <summary>How many deferred geometries were reserved, completed and abandoned here, in all.</summary>
        public int DeferredReserved { get; private set; }
        public int DeferredCompleted { get; private set; }
        public int DeferredAbandoned { get; private set; }

        /// <summary>
        /// Reserves a geometry for a GPU-first cut of <paramref name="model"/> (DESIGN 4.5.15): the vertex, topology,
        /// submesh and block spans and the index range are taken as the direct skin append takes them; the indices are
        /// written now from the model (the display needs them in the hit's frame) and published; the submesh bounds and
        /// the extent take <paramref name="conservative"/>, the producer's box of the pose. The vertices and the topology
        /// are **not written**: the geometry is pending until <see cref="TryCompleteDeferredSkin"/> writes them, and until
        /// then it is no cut input. Refused, taking nothing, for the room reasons of the ordinary append (the refusal
        /// counts and <see cref="LastRefusalIsTemporary"/> say which) or an unusable model or box.
        /// </summary>
        public unsafe bool TryReserveDeferredSkin(VpSkinModel model, Bounds conservative, out VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            geometry = default;
            if (model == null || model.IsDisposed) return false;
            Vector3 cmin = conservative.min, cmax = conservative.max;
            if (!(cmin.x <= cmax.x && cmin.y <= cmax.y && cmin.z <= cmax.z) || float.IsInfinity(cmin.x) || float.IsInfinity(cmax.x)
                || float.IsInfinity(cmin.y) || float.IsInfinity(cmax.y) || float.IsInfinity(cmin.z) || float.IsInfinity(cmax.z)) return false;
            int vertexCount = model.VertexCount, indexCount = model.IndexCount;
            if (!TryTakeSpans(vertexCount, 1, 1, out int vertex, out int submesh, out int block)) return false;
            if (!TryReserveIndices(indexCount, out VpIndexRangeHandle range))
            {
                GiveBackSpans(vertex, vertexCount, submesh, 1, block, 1);
                return false;
            }

            bool published = false;
            try
            {
                if (!_indices.TryGetReservedWriteView(range, out NativeArray<uint> indices)) return false;
                VpDirectSkinKernel.CopyIndices(model.IndexPointer, (uint*)indices.GetUnsafePtr(), indexCount, (uint)vertex);
                _submeshes[submesh] = new VpGeometrySubmesh(0, indexCount, 0);
                _submeshBounds[submesh] = new VpGeometryBounds(cmin, cmax);
                WriteOwnBlock(block, vertex, vertexCount);
                if (!_indices.TryPublish(range)) return false;
                published = true;
                PublishSpans(vertex, vertexCount, submesh, 1, block, 1);
                OpenVertexGroup(vertex, vertexCount, submesh, 1, block, 1);
                // The model passed the cut input gate at its cold making (VpSkinModel.TryCreate), as the direct skin
                // input's geometry does: the geometry is a cut input once its vertices are written, and not before.
                geometry = new VpStoredGeometry(vertex, vertexCount, range, true, model.TopologyCount, submesh, 1, block, 1, true);
                RecordAppend(range, geometry);
                RecordExtent(range, submesh, 1, vertex, vertexCount);
                AppendRecord record = _appendOfDescriptor[range.descriptor];
                record.boundsMin = cmin;
                record.boundsMax = cmax;
                _appendOfDescriptor[range.descriptor] = record;
                _deferredPending[range.descriptor] = range.generation;
                DeferredReserved++;
                return true;
            }
            finally
            {
                if (!published)
                {
                    _indices.TryCancelReservation(range);
                    GiveBackSpans(vertex, vertexCount, submesh, 1, block, 1);
                }
            }
        }

        /// <summary>Whether <paramref name="geometry"/> is a deferred geometry whose CPU vertices are still to come.</summary>
        public bool IsDeferredPending(VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            return _deferredPending.TryGetValue(geometry.indexRange.descriptor, out uint generation) && generation == geometry.indexRange.generation;
        }

        /// <summary>
        /// Completes a pending deferred geometry: <paramref name="vertices"/> and <paramref name="topology"/> (at least
        /// the geometry's count each) are copied into its spans and <paramref name="measured"/> replaces the conservative
        /// box as its submesh bounds and extent. The geometry is a cut input from here. False, changing nothing, when it
        /// is not pending (completed or abandoned already, or never reserved here), the arrays are short, or the box is
        /// not a finite ordered box.
        /// </summary>
        public unsafe bool TryCompleteDeferredSkin(VpStoredGeometry geometry, NativeArray<VpRenderVertex> vertices, NativeArray<int> topology, Bounds measured)
        {
            ThrowIfDisposed();
            if (!IsDeferredPending(geometry) || !IsMetadataReadable(geometry)) return false;
            if (!vertices.IsCreated || !topology.IsCreated || vertices.Length < geometry.vertexCount || topology.Length < geometry.vertexCount) return false;
            Vector3 min = measured.min, max = measured.max;
            if (!(min.x <= max.x && min.y <= max.y && min.z <= max.z) || float.IsInfinity(min.x) || float.IsInfinity(max.x)
                || float.IsInfinity(min.y) || float.IsInfinity(max.y) || float.IsInfinity(min.z) || float.IsInfinity(max.z)) return false;
            NativeArray<VpRenderVertex> span = _vertices.GetSpan(geometry.vertexStart, geometry.vertexCount);
            UnsafeUtility.MemCpy(span.GetUnsafePtr(), vertices.GetUnsafeReadOnlyPtr(), (long)geometry.vertexCount * VpRenderVertex.Stride);
            NativeArray<int> topologySpan = _topologyOfVertex.GetSubArray(geometry.vertexStart, geometry.vertexCount);
            UnsafeUtility.MemCpy(topologySpan.GetUnsafePtr(), topology.GetUnsafeReadOnlyPtr(), (long)geometry.vertexCount * sizeof(int));
            var box = new VpGeometryBounds(min, max);
            for (int s = 0; s < geometry.submeshCount; s++) _submeshBounds[geometry.submeshStart + s] = box;
            AppendRecord record = _appendOfDescriptor[geometry.indexRange.descriptor];
            record.boundsMin = min;
            record.boundsMax = max;
            _appendOfDescriptor[geometry.indexRange.descriptor] = record;
            _deferredPending.Remove(geometry.indexRange.descriptor);
            DeferredCompleted++;
            return true;
        }

        /// <summary>
        /// Abandons a pending deferred geometry (the request was cancelled before its CPU bake completed): it is no
        /// longer pending. With <paramref name="retireIndices"/> -- a geometry the display never took in -- its index range
        /// is retired here and, once that range is free and nothing else is on its vertex group, the group's spans are
        /// given back. A geometry the display shows is **not** retired here: the display retires it once, when the body
        /// it shows drops with its fragment's retirement (the reference table's retirement of the index range); retiring it
        /// here first would make that retirement fail and leave the registration live (TL review 2026-11-01, P2-1). False
        /// when the geometry is not pending.
        /// </summary>
        public bool TryAbandonDeferredSkin(VpStoredGeometry geometry, bool retireIndices)
        {
            ThrowIfDisposed();
            if (!IsDeferredPending(geometry)) return false;
            _deferredPending.Remove(geometry.indexRange.descriptor);
            DeferredAbandoned++;
            if (!retireIndices) return true;
            TryRetireIndices(geometry.indexRange);
            if (TryGetVertexGroup(geometry, out int group) && IsVertexGroupQuiet(group)) TryReleaseVertexGroup(group);
            return true;
        }
    }
}
