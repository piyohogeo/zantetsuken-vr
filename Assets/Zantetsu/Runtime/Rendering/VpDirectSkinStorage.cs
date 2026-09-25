using Unity.Collections;
using Unity.Mathematics;

namespace Zantetsu.Rendering
{
    public sealed partial class VpCpuGeometryStorage
    {
        // Synchronous Main-only producer. No reservation, writable view, or "valid" flag escapes this call.
        // Gather has already completed. There are no callbacks, awaits, or Unity object operations in this scope.
        internal bool TryAppendDirectSkin(VpDirectSkinInput input, out VpStoredGeometry geometry)
        {
            ThrowIfDisposed();
            geometry = default;
            if (!input.IsAlive || _openCutOutputs.Count != 0) return false;
            if (!TryTakeSpans(input.VertexCount, 1, 1, out int vertex, out int submesh, out int block)) return false;
            if (!_indices.TryReserve(input.IndexCount, out var range))
            {
                GiveBackSpans(vertex, input.VertexCount, submesh, 1, block, 1);
                return false;
            }
            bool published = false;
            try
            {
                if (!_indices.TryGetReservedWriteView(range, out NativeArray<uint> indices)) return false;
                if (!input.Write(_vertices.GetSpan(vertex, input.VertexCount),
                        _topologyOfVertex.GetSubArray(vertex, input.VertexCount), indices, vertex,
                        out float3 min, out float3 max)) return false;
                _submeshes[submesh] = new VpGeometrySubmesh(0, input.IndexCount, 0);
                _submeshBounds[submesh] = new VpGeometryBounds(min, max);
                WriteOwnBlock(block, vertex, input.VertexCount);
                if (!_indices.TryPublish(range)) return false;
                published = true;
                PublishSpans(vertex, input.VertexCount, submesh, 1, block, 1);
                geometry = new VpStoredGeometry(vertex, input.VertexCount, range, true,
                    input.TopologyCount, submesh, 1, block, 1, true);
                RecordAppend(range, geometry);
                // Cold input proves one submesh references every vertex. No index or bounds rescan.
                RecordExtent(range, submesh, 1, vertex, input.VertexCount);
                return true;
            }
            finally
            {
                if (!published)
                {
                    _indices.TryCancelReservation(range);
                    GiveBackSpans(vertex, input.VertexCount, submesh, 1, block, 1);
                }
            }
        }
    }
}
