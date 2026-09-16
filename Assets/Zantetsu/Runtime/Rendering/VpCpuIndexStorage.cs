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
            out VpIndexRangeHandle first,
            out VpIndexRangeHandle second)
        {
            ThrowIfDisposed();
            return _allocator.TryPublishSplit(handle, firstCount, secondCount, out first, out second);
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
