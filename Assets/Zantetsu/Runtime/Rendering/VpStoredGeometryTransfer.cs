using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Transfers of a <see cref="VpCpuGeometryStorage"/>'s own memory into GPU buffers that mirror it: what is at a
    /// position in the storage goes to the same position in the buffer, so a stored index addresses the right vertex
    /// with no renumbering and a published range is drawn where it already lies (DESIGN 4.5.4, "CPU／GPU offset対応を
    /// 保つ").
    /// <para>
    /// Nothing here copies the data into an array of its own first: the storage's memory is the transfer source, read
    /// where it is. That is what "no intermediate copy" means here — it says nothing about copies Unity or the driver
    /// make inside <c>SetData</c>, which are theirs.
    /// </para>
    /// <para>
    /// The index transfers take the read leases they need themselves and give them back in a <c>finally</c>, including
    /// when the second of two cannot be taken. A lease protects the reading of the transfer source and nothing else:
    /// <c>SetData</c> having returned is not a statement about GPU work, here or anywhere.
    /// </para>
    /// <para>
    /// Every range is checked before any transfer is issued — order, adjacency, published counts and the destination's
    /// stride and capacity — so a refusal never leaves a partly written buffer. Each method reports how many elements it
    /// transferred, and 0 means no <c>SetData</c> was issued at all: that is the count a caller records.
    /// </para>
    /// </summary>
    public static class VpStoredGeometryTransfer
    {
        /// <summary>
        /// Writes committed vertices [start, start + count) into <paramref name="destination"/> at the same positions,
        /// which is what lets a draw leave baseVertexIndex at 0. A count of 0 succeeds without issuing a transfer.
        /// Returns false, transferring nothing, when an argument is null, the range is negative or reaches past the
        /// committed vertices, or the destination's stride or capacity cannot hold it.
        /// </summary>
        public static bool TryUploadCommittedVertices(
            VpCpuGeometryStorage storage,
            GraphicsBuffer destination,
            int start,
            int count,
            out int transferred)
        {
            transferred = 0;
            if (storage == null || destination == null || start < 0 || count < 0)
            {
                return false;
            }

            if (!storage.TryGetCommittedVertexRange(start, count, out NativeArray<VpRenderVertex> range))
            {
                return false;
            }

            if (!CanHold(destination, VpRenderVertex.Stride, start, count))
            {
                return false;
            }

            if (count == 0)
            {
                return true;
            }

            // The window begins at the range's own start, so the source position inside it is 0 and the storage's
            // position is the destination's. Adding the start to both would put the vertices twice as far along.
            destination.SetData(range, 0, start, count);
            transferred = count;
            return true;
        }

        /// <summary>
        /// Writes one published range's indices into <paramref name="destination"/> at the positions they occupy in the
        /// storage. The lease is taken here and given back here. An empty range succeeds without issuing a transfer.
        /// Returns false, transferring nothing, when an argument is null, the range is not published, or the
        /// destination's stride or capacity cannot hold it.
        /// </summary>
        public static bool TryUploadPublishedIndices(
            VpCpuGeometryStorage storage,
            GraphicsBuffer destination,
            VpIndexRangeHandle range,
            out int transferred)
        {
            transferred = 0;
            if (storage == null || destination == null)
            {
                return false;
            }

            if (!storage.TryAcquireIndexReadLease(range, out VpIndexReadLease lease, out _))
            {
                return false;
            }

            try
            {
                if (!storage.TryGetLeasedIndexSpan(lease, out NativeArray<uint> span, out int indexStart, out int indexCount))
                {
                    return false;
                }

                return TryWriteIndices(destination, span, indexStart, indexCount, out transferred);
            }
            finally
            {
                storage.TryReleaseIndexReadLease(lease);
            }
        }

        /// <summary>
        /// Writes the one contiguous run that two adjacent published ranges make together — <paramref name="first"/>
        /// then <paramref name="second"/> — in a single transfer, at the positions they occupy in the storage. This is
        /// the cut's two sides: they are laid out as one run on purpose, so they go across as one transfer and never as
        /// two (DESIGN 4.5.6). One empty side transfers the other alone; two empty sides transfer nothing.
        /// <para>
        /// Both leases are taken here and given back here, the first one included when the second cannot be taken.
        /// </para>
        /// Returns false, transferring nothing, when an argument is null, either range is not published, the second does
        /// not begin exactly where the first ends, or the destination's stride or capacity cannot hold the run.
        /// </summary>
        public static bool TryUploadPublishedIndexRun(
            VpCpuGeometryStorage storage,
            GraphicsBuffer destination,
            VpIndexRangeHandle first,
            VpIndexRangeHandle second,
            out int transferred)
        {
            transferred = 0;
            if (storage == null || destination == null)
            {
                return false;
            }

            if (!storage.TryAcquireIndexReadLease(first, out VpIndexReadLease firstLease, out _))
            {
                return false;
            }

            try
            {
                if (!storage.TryAcquireIndexReadLease(second, out VpIndexReadLease secondLease, out _))
                {
                    // The first lease goes back through this method's own finally; nothing is left held.
                    return false;
                }

                try
                {
                    if (!storage.TryGetLeasedIndexSpan(firstLease, secondLease, out NativeArray<uint> span, out int indexStart, out int indexCount))
                    {
                        return false;
                    }

                    return TryWriteIndices(destination, span, indexStart, indexCount, out transferred);
                }
                finally
                {
                    storage.TryReleaseIndexReadLease(secondLease);
                }
            }
            finally
            {
                storage.TryReleaseIndexReadLease(firstLease);
            }
        }

        private static bool TryWriteIndices(
            GraphicsBuffer destination,
            NativeArray<uint> span,
            int indexStart,
            int indexCount,
            out int transferred)
        {
            transferred = 0;
            if (!CanHold(destination, VpGpuIndexedGeometryBuffers.IndexStride, indexStart, indexCount))
            {
                return false;
            }

            if (indexCount == 0)
            {
                return true;
            }

            // Source position 0: the window already starts at indexStart, which is the destination position.
            destination.SetData(span, 0, indexStart, indexCount);
            transferred = indexCount;
            return true;
        }

        private static bool CanHold(GraphicsBuffer destination, int stride, int start, int count)
        {
            return destination.stride == stride && start >= 0 && count >= 0 && start <= destination.count - count;
        }
    }
}
