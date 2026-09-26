using System;
using Unity.Collections;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity CPU VP index storage whose ranges are reserved, written, published, leased and reused through its
    /// own <see cref="VpIndexRangeAllocator"/> (DESIGN 4.5.3). Stored values are indices already resolved to the global
    /// CPU VP vertex numbers; the storage neither rebases, interprets nor checks them, and it does not clear reused
    /// space. It does not grow.
    /// <para>
    /// Views are windows into the one index array and cannot be revoked once handed out, so their use is bounded by
    /// contract, not detected. A write view may be used only while its range is Reserved; a read view only while its
    /// lease is held. Using a view after the range is published or cancelled, or after the lease is returned, breaks
    /// the contract. Only the main thread calls the storage: workers given a view never change the allocator or any
    /// range state, and leases lent to jobs are returned after the jobs are collected.
    /// </para>
    /// After <see cref="Dispose"/>, every operation throws ObjectDisposedException; disposing again does nothing.
    /// </summary>
    public sealed class VpCpuIndexStorage : IDisposable
    {
        private readonly VpIndexRangeAllocator _allocator;
        private NativeArray<uint> _indices;
        private bool _disposed;

        public VpCpuIndexStorage(int indexCapacity, int descriptorCapacity, Allocator allocator)
        {
            _allocator = new VpIndexRangeAllocator(indexCapacity, descriptorCapacity);
            _indices = new NativeArray<uint>(indexCapacity, allocator);
        }

        public int IndexCapacity => _allocator.IndexCapacity;

        public int DescriptorCapacity => _allocator.DescriptorCapacity;

        /// <summary>How many indices are free for a reservation, over all free ranges.</summary>
        public int FreeIndexRoom => _allocator.FreeIndexRoom;

        /// <inheritdoc cref="VpIndexRangeAllocator.TryReserve"/>
        public bool TryReserve(int indexCount, out VpIndexRangeHandle handle)
        {
            ThrowIfDisposed();
            return _allocator.TryReserve(indexCount, out handle);
        }

        /// <summary>
        /// A writable view of exactly the reserved range, to be filled from its start. Returns false with a default view
        /// unless the handle's range is Reserved. Reused space still holds earlier values. The view may be used only
        /// while the range stays Reserved.
        /// </summary>
        public bool TryGetReservedWriteView(VpIndexRangeHandle handle, out NativeArray<uint> view)
        {
            ThrowIfDisposed();
            if (!_allocator.TryGetState(handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
                || state != VpIndexRangeState.Reserved)
            {
                view = default;
                return false;
            }

            view = _indices.GetSubArray(indexStart, indexCount);
            return true;
        }

        /// <summary>Reserved → Published with the whole reservation, all of which the caller must have written.</summary>
        public bool TryPublish(VpIndexRangeHandle handle)
        {
            ThrowIfDisposed();
            return _allocator.TryPublish(handle);
        }

        /// <summary>
        /// Reserved → Published with the first <paramref name="publishedCount"/> indices of the reservation, which the
        /// caller must have written in order from its start; the storage does not inspect the data, and publishing
        /// unwritten indices breaks the contract. The unused tail returns to the allocator at once, and publishing
        /// nothing leaves an empty range at 0. Fails, changing nothing, when the count is negative or exceeds the
        /// reservation.
        /// </summary>
        public bool TryPublish(VpIndexRangeHandle handle, int publishedCount)
        {
            ThrowIfDisposed();
            return _allocator.TryPublish(handle, publishedCount);
        }

        /// <inheritdoc cref="VpIndexRangeAllocator.TryPublishSplit"/>
        public bool TryPublishSplit(
            VpIndexRangeHandle handle,
            int firstCount,
            int secondCount,
            VpIndexRangeHandle held,
            out VpIndexRangeHandle first,
            out VpIndexRangeHandle second)
        {
            ThrowIfDisposed();
            return _allocator.TryPublishSplit(handle, firstCount, secondCount, held, out first, out second);
        }

        /// <summary>Reserved → Free; the space is reusable at once and write views of it must no longer be used.</summary>
        public bool TryCancelReservation(VpIndexRangeHandle handle)
        {
            ThrowIfDisposed();
            return _allocator.TryCancelReservation(handle);
        }

        /// <summary>
        /// Lends a read lease on a Published range with a read-only view of its published indices. Returns false with a
        /// default lease and view in any other state. The view may be used only while the lease is held.
        /// </summary>
        public bool TryAcquireReadLease(VpIndexRangeHandle handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view)
        {
            ThrowIfDisposed();
            if (!_allocator.TryAcquireReadLease(handle, out lease))
            {
                view = default;
                return false;
            }

            view = PublishedView(lease.range);
            return true;
        }

        /// <summary>
        /// The read-only view of a held lease's range, while that range is Published or Retiring. Returns false with a
        /// default view for a returned, stale, foreign or default lease. The view may be used only while the lease is held.
        /// </summary>
        public bool TryGetReadView(VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view)
        {
            ThrowIfDisposed();
            if (!_allocator.IsLeaseHeld(lease))
            {
                view = default;
                return false;
            }

            view = PublishedView(lease.range);
            return true;
        }

        /// <summary>
        /// A window on the indices of a **held lease's** range, for a transfer that reads them where they are instead of
        /// copying them out, together with where they sit in the index array. The window is answered only for a lease
        /// this storage still holds, so a caller cannot ask for an arbitrary start and count. The array is **borrowed**:
        /// it is read, never written, never kept past the call and never disposed, and it may be used only while the
        /// lease is held. False with a default window for a returned, stale, foreign or default lease.
        /// </summary>
        internal bool TryGetLeasedSpan(VpIndexReadLease lease, out NativeArray<uint> span, out int indexStart, out int indexCount)
        {
            ThrowIfDisposed();
            span = default;
            indexStart = 0;
            indexCount = 0;
            if (!_allocator.IsLeaseHeld(lease)
                || !_allocator.TryGetState(lease.range, out _, out indexStart, out indexCount))
            {
                return false;
            }

            span = _indices.GetSubArray(indexStart, indexCount);
            return true;
        }

        /// <summary>
        /// The same window over **two held leases whose ranges are adjacent**, in order: the second range must begin
        /// exactly where the first ends, so the window is the one contiguous run they make together and holds nothing
        /// else — no gap, no unused reservation tail, no unrelated range. An empty range on either side answers the
        /// other side's window alone, and two empty ranges answer an empty one. False, with a default window, for a
        /// lease this storage does not hold or for ranges that are not adjacent in that order.
        /// </summary>
        internal bool TryGetLeasedSpan(
            VpIndexReadLease first,
            VpIndexReadLease second,
            out NativeArray<uint> span,
            out int indexStart,
            out int indexCount)
        {
            ThrowIfDisposed();
            span = default;
            indexStart = 0;
            indexCount = 0;
            if (!_allocator.IsLeaseHeld(first)
                || !_allocator.IsLeaseHeld(second)
                || !_allocator.TryGetState(first.range, out _, out int firstStart, out int firstCount)
                || !_allocator.TryGetState(second.range, out _, out int secondStart, out int secondCount))
            {
                return false;
            }

            if (firstCount > 0 && secondCount > 0)
            {
                if (firstStart + firstCount != secondStart)
                {
                    return false;
                }

                indexStart = firstStart;
                indexCount = firstCount + secondCount;
            }
            else if (firstCount > 0)
            {
                indexStart = firstStart;
                indexCount = firstCount;
            }
            else
            {
                indexStart = secondStart;
                indexCount = secondCount;
            }

            span = _indices.GetSubArray(indexStart, indexCount);
            return true;
        }

        /// <inheritdoc cref="VpIndexRangeAllocator.TryRetire"/>
        public bool TryRetire(VpIndexRangeHandle handle)
        {
            ThrowIfDisposed();
            return _allocator.TryRetire(handle);
        }

        /// <summary>Returns a held lease; its read views must no longer be used. The last lease of a Retiring range frees its space.</summary>
        public bool TryReleaseReadLease(VpIndexReadLease lease)
        {
            ThrowIfDisposed();
            return _allocator.TryReleaseReadLease(lease);
        }

        /// <inheritdoc cref="VpIndexRangeLifecycleTable.TryGetState"/>
        public bool TryGetState(VpIndexRangeHandle handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
        {
            ThrowIfDisposed();
            return _allocator.TryGetState(handle, out state, out indexStart, out indexCount);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _indices.Dispose();
        }

        private NativeArray<uint>.ReadOnly PublishedView(VpIndexRangeHandle handle)
        {
            _allocator.TryGetState(handle, out _, out int indexStart, out int indexCount);
            return _indices.GetSubArray(indexStart, indexCount).AsReadOnly();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpCpuIndexStorage));
            }
        }
    }
}
