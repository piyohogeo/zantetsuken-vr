using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    public sealed partial class VpLogicalCutDisplay
    {
        // The regions of the vertex buffer whose contents are a GPU bake's and not yet the storage's (DESIGN 4.5.15):
        // each with the producer that bakes it, so that a replaced buffer gets them baked again after the storage's
        // re-transfer. A region leaves when its CPU bake completed (the storage then holds the vertices and the ordinary
        // re-transfer suffices) or when its request ended.
        private sealed class GpuRegion
        {
            public VpStoredGeometry geometry;
            public IVpSkinBakeDispatch producer;
        }

        private readonly List<GpuRegion> _gpuRegions = new List<GpuRegion>(4);
        private bool _showingDeferred;

        /// <summary>How many shown regions are the GPU bake's now.</summary>
        public int GpuRegionCount => _gpuRegions.Count;

        /// <summary>How many geometries the reference table holds live now (a dropped body's leaves once its range is retired).</summary>
        public int LiveGeometryCount => _table.LiveGeometryCount;

        /// <summary>How many first dispatches, re-dispatches into a replaced buffer, and regions forgotten because their producer could not bake.</summary>
        public int GpuRegionDispatches { get; private set; }
        public int GpuRegionRebakes { get; private set; }
        public int GpuRegionsLost { get; private set; }
        public int MaxGpuRegions { get; private set; }

        /// <summary>How many regions left with their geometry's retirement (the body dropped before its CPU bake completed, or after it failed).</summary>
        public int GpuRegionsRetired { get; private set; }

        /// <summary>Whether the geometry's region is the GPU bake's now (its producer is still needed for a re-bake).</summary>
        public bool HasGpuRegion(VpStoredGeometry geometry)
        {
            for (int i = 0; i < _gpuRegions.Count; i++)
            {
                if (_gpuRegions[i].geometry.indexRange.Equals(geometry.indexRange) && _gpuRegions[i].geometry.vertexStart == geometry.vertexStart) return true;
            }

            return false;
        }

        /// <summary>
        /// Shows a prepared root whose vertices the GPU bakes (DESIGN 4.5.15): the geometry a storage reserved for the
        /// deferred skin (<see cref="VpCpuGeometryStorage.TryReserveDeferredSkin"/>), registered as a prepared root is
        /// -- its indices transferred, its command made with the conservative bounds -- with no vertex transfer, and
        /// then baked into its region by <paramref name="producer"/> in this same update, before the cameras. The
        /// region is kept as the GPU's until <see cref="CompleteGpuRegion"/> or <see cref="ForgetGpuRegion"/>. False,
        /// taking nothing in, where the show is refused or the geometry is not a pending deferred one; false with the
        /// root taken in where the first dispatch is refused (the caller then forgets the region and cancels).
        /// </summary>
        public bool TryShowPreparedRootDeferred(PreparedRoot slot, VpStoredGeometry geometry, LogicalFragmentId fragment,
            Matrix4x4 objectToWorld, IVpSkinBakeDispatch producer, out bool shown)
        {
            ThrowIfDisposed(); ThrowIfBroken(); ThrowIfPreparing();
            shown = false;
            if (slot == null || !ReferenceEquals(slot.owner, this) || producer == null || !_storage.IsDeferredPending(geometry)) return false;
            if (RefusePreparedRootShowForTest != null && RefusePreparedRootShowForTest()) return false;
            _showingDeferred = true;
            try
            {
                shown = TryShowCore(fragment, geometry, objectToWorld, Matrix4x4.identity, Array.Empty<VpClipBoundary>(), slot);
            }
            finally
            {
                _showingDeferred = false;
            }

            if (!shown) return false;
            var region = new GpuRegion { geometry = geometry, producer = producer };
            _gpuRegions.Add(region);
            MaxGpuRegions = Math.Max(MaxGpuRegions, _gpuRegions.Count);
            if (!producer.TryDispatch(_buffers.VertexBuffer, geometry.vertexStart, geometry.vertexCount))
            {
                _gpuRegions.Remove(region);
                GpuRegionsLost++;
                return false;
            }

            GpuRegionDispatches++;
            return true;
        }

        /// <summary>
        /// The same, for a geometry already completed by its producer on the CPU (the synchronous float route): a prepared
        /// root shown from the storage's vertices, without the producer identity the direct skin output carries.
        /// </summary>
        public bool TryShowPreparedRootGeometry(PreparedRoot slot, VpStoredGeometry geometry, LogicalFragmentId fragment, Matrix4x4 objectToWorld)
        {
            ThrowIfDisposed(); ThrowIfBroken(); ThrowIfPreparing();
            if (slot == null || !ReferenceEquals(slot.owner, this) || _storage.IsDeferredPending(geometry)) return false;
            if (RefusePreparedRootShowForTest != null && RefusePreparedRootShowForTest()) return false;
            return TryShowCore(fragment, geometry, objectToWorld, Matrix4x4.identity, Array.Empty<VpClipBoundary>(), slot);
        }

        /// <summary>The CPU bake of the region completed and the storage holds its vertices: the region is the storage's again. False when it was not a GPU region.</summary>
        public bool CompleteGpuRegion(VpStoredGeometry geometry) => RemoveGpuRegion(geometry);

        /// <summary>The request of the region ended without completing: the region is forgotten (what is drawn of it leaves with its fragment). False when it was not a GPU region.</summary>
        public bool ForgetGpuRegion(VpStoredGeometry geometry) => RemoveGpuRegion(geometry);

        private bool RemoveGpuRegion(VpStoredGeometry geometry)
        {
            for (int i = 0; i < _gpuRegions.Count; i++)
            {
                if (_gpuRegions[i].geometry.indexRange.Equals(geometry.indexRange) && _gpuRegions[i].geometry.vertexStart == geometry.vertexStart)
                {
                    _gpuRegions.RemoveAt(i);
                    return true;
                }
            }

            return false;
        }

        // After a replaced vertex buffer was filled again from the storage: every GPU region baked again by its producer.
        // A producer that cannot (its request ended between) loses its region, which is then whatever the storage held.
        private bool RebakeGpuRegions()
        {
            for (int i = _gpuRegions.Count - 1; i >= 0; i--)
            {
                GpuRegion region = _gpuRegions[i];
                if ((long)region.geometry.vertexStart + region.geometry.vertexCount > _buffers.VertexCapacity
                    || !region.producer.TryDispatch(_buffers.VertexBuffer, region.geometry.vertexStart, region.geometry.vertexCount))
                {
                    _gpuRegions.RemoveAt(i);
                    GpuRegionsLost++;
                    continue;
                }

                GpuRegionRebakes++;
            }

            return true;
        }
    }
}
