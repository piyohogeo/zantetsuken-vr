using System;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity index range allocator over a <see cref="VpIndexRangeLifecycleTable"/> (DESIGN 4.5.3). The free
    /// index ranges are kept in address order and a reservation takes the start of the first one that fits, leaving
    /// the rest free. Space returns when its range becomes Free in the table (a cancelled reservation, a retirement
    /// without readers, or the last lease of a Retiring range) and when a partial publish drops the unused tail, and
    /// it merges with free neighbours. An empty range takes a descriptor but no index space and starts at 0. A failed
    /// operation returns false and changes neither the free ranges nor the table; there is no waiting, growth or
    /// compaction. The main thread alone uses the allocator.
    /// </summary>
    public sealed class VpIndexRangeAllocator
    {
        private readonly VpIndexRangeLifecycleTable _table;

        // Free ranges by ascending start, none empty and none adjacent. Every gap between two of them holds a live
        // non-empty range, so there are at most descriptorCapacity + 1 and (indexCapacity + 1) / 2 of them.
        private readonly int[] _freeStarts;
        private readonly int[] _freeCounts;
        private int _freeRangeCount;

        public VpIndexRangeAllocator(int indexCapacity, int descriptorCapacity)
            : this(indexCapacity, new VpIndexRangeLifecycleTable(descriptorCapacity))
        {
        }

        /// <summary>An allocator over a new, unused table, so tests can lower the table's identity limits.</summary>
        internal VpIndexRangeAllocator(int indexCapacity, VpIndexRangeLifecycleTable table)
        {
            if (indexCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(indexCapacity), indexCapacity, "Must not be negative.");
            }

            _table = table;
            IndexCapacity = indexCapacity;
            int maxFreeRangeCount = (int)Math.Min((long)table.DescriptorCapacity + 1, ((long)indexCapacity + 1) / 2);
            _freeStarts = new int[maxFreeRangeCount];
            _freeCounts = new int[maxFreeRangeCount];
            if (indexCapacity > 0)
            {
                _freeCounts[0] = indexCapacity;
                _freeRangeCount = 1;
            }
        }

        public int IndexCapacity { get; }

        public int DescriptorCapacity => _table.DescriptorCapacity;

        /// <summary>
        /// Reserves <paramref name="indexCount"/> indices at the start of the first free range that holds them, or an
        /// empty range at 0. Returns false with a default handle when the count is negative, no single free range is
        /// large enough, or the table has no usable descriptor.
        /// </summary>
        public bool TryReserve(int indexCount, out VpIndexRangeHandle handle)
        {
            handle = default;
            if (indexCount < 0)
            {
                return false;
            }

            if (indexCount == 0)
            {
                return _table.TryReserve(0, 0, out handle);
            }

            for (int i = 0; i < _freeRangeCount; i++)
            {
                if (_freeCounts[i] < indexCount)
                {
                    continue;
                }

                if (!_table.TryReserve(_freeStarts[i], indexCount, out handle))
                {
                    return false;
                }

                if (_freeCounts[i] == indexCount)
                {
                    RemoveFreeRange(i);
                }
                else
                {
                    _freeStarts[i] += indexCount;
                    _freeCounts[i] -= indexCount;
                }

                return true;
            }

            return false;
        }

        /// <summary>Reserved → Published with the whole reservation.</summary>
        public bool TryPublish(VpIndexRangeHandle handle)
        {
            return _table.TryPublish(handle);
        }

        /// <summary>
        /// Reserved → Published with the range cut to its first <paramref name="publishedCount"/> indices; the unused
        /// tail is freed at once. A range cut to nothing starts at 0. Fails, changing nothing, when the count is
        /// negative or exceeds the reservation.
        /// </summary>
        public bool TryPublish(VpIndexRangeHandle handle, int publishedCount)
        {
            if (!_table.TryGetState(handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
                || state != VpIndexRangeState.Reserved
                || !_table.TryPublish(handle, publishedCount))
            {
                return false;
            }

            AddFreeRange(indexStart + publishedCount, indexCount - publishedCount);
            return true;
        }

        /// <summary>
        /// Reserved → Published as the two ranges of one reservation: its first <paramref name="firstCount"/> indices
        /// and the <paramref name="secondCount"/> that follow them, for a cut that wrote both sides into one run
        /// (DESIGN 4.5.6). The unused tail is freed at once, and each published part is an ordinary range with its own
        /// descriptor, lease counter and retirement. A part with no index gets neither range nor descriptor, so an
        /// empty side is not given an owner; the handle passed in carries the first non-empty part. The second part's
        /// descriptor is registered before anything is published, so either both parts are published or nothing is and
        /// the reservation is left Reserved for the caller to cancel. Fails, changing nothing, when the handle's range
        /// is not Reserved, a count is negative, or the two together exceed the reservation. When both counts are 0 the
        /// reservation is simply cancelled and both handles come back default.
        /// </summary>
        public bool TryPublishSplit(
            VpIndexRangeHandle handle,
            int firstCount,
            int secondCount,
            out VpIndexRangeHandle first,
            out VpIndexRangeHandle second)
        {
            first = default;
            second = default;
            if (!_table.TryGetState(handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
                || state != VpIndexRangeState.Reserved
                || firstCount < 0
                || secondCount < 0
                || (long)firstCount + secondCount > indexCount)
            {
                return false;
            }

            if (firstCount == 0 && secondCount == 0)
            {
                return TryCancelReservation(handle);
            }

            // The one step that can fail comes before every publish: once past it, both parts are published.
            bool bothUsed = firstCount > 0 && secondCount > 0;
            if (bothUsed && !_table.TryReserve(indexStart + firstCount, secondCount, out second))
            {
                return false;
            }

            if (bothUsed)
            {
                _table.TryPublish(handle, firstCount);
                _table.TryPublish(second);
                first = handle;
            }
            else
            {
                // One side only: it owns the used prefix through the descriptor already registered.
                _table.TryPublish(handle, firstCount + secondCount);
                if (firstCount > 0)
                {
                    first = handle;
                }
                else
                {
                    second = handle;
                }
            }

            AddFreeRange(indexStart + firstCount + secondCount, indexCount - firstCount - secondCount);
            return true;
        }

        /// <summary>Reserved → Free; the space is reusable at once.</summary>
        public bool TryCancelReservation(VpIndexRangeHandle handle)
        {
            if (!_table.TryCancelReservation(handle))
            {
                return false;
            }

            FreeSpaceIfFree(handle);
            return true;
        }

        /// <summary>Published → Free with the space reusable at once when no lease is held, otherwise Published → Retiring.</summary>
        public bool TryRetire(VpIndexRangeHandle handle)
        {
            if (!_table.TryRetire(handle))
            {
                return false;
            }

            FreeSpaceIfFree(handle);
            return true;
        }

        public bool TryAcquireReadLease(VpIndexRangeHandle handle, out VpIndexReadLease lease)
        {
            return _table.TryAcquireReadLease(handle, out lease);
        }

        /// <summary>Returns a held lease; the last lease of a Retiring range makes its space reusable.</summary>
        public bool TryReleaseReadLease(VpIndexReadLease lease)
        {
            if (!_table.TryReleaseReadLease(lease))
            {
                return false;
            }

            FreeSpaceIfFree(lease.range);
            return true;
        }

        /// <inheritdoc cref="VpIndexRangeLifecycleTable.TryGetState"/>
        public bool TryGetState(VpIndexRangeHandle handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
        {
            return _table.TryGetState(handle, out state, out indexStart, out indexCount);
        }

        /// <inheritdoc cref="VpIndexRangeLifecycleTable.IsHeld"/>
        internal bool IsLeaseHeld(VpIndexReadLease lease)
        {
            return _table.IsHeld(lease);
        }

        internal bool TryGetReaderCount(VpIndexRangeHandle handle, out int readerCount)
        {
            return _table.TryGetReaderCount(handle, out readerCount);
        }

        /// <summary>A copy of the free ranges in address order.</summary>
        internal (int start, int count)[] CopyFreeRanges()
        {
            var ranges = new (int start, int count)[_freeRangeCount];
            for (int i = 0; i < _freeRangeCount; i++)
            {
                ranges[i] = (_freeStarts[i], _freeCounts[i]);
            }

            return ranges;
        }

        /// <summary>Frees the space of a registration that has just become Free. Each registration becomes Free once.</summary>
        private void FreeSpaceIfFree(VpIndexRangeHandle handle)
        {
            if (_table.TryGetState(handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
                && state == VpIndexRangeState.Free)
            {
                AddFreeRange(indexStart, indexCount);
            }
        }

        private void AddFreeRange(int start, int count)
        {
            if (count == 0)
            {
                return;
            }

            int i = 0;
            while (i < _freeRangeCount && _freeStarts[i] < start)
            {
                i++;
            }

            bool joinsLeft = i > 0 && _freeStarts[i - 1] + _freeCounts[i - 1] == start;
            bool joinsRight = i < _freeRangeCount && start + count == _freeStarts[i];
            if (joinsLeft && joinsRight)
            {
                _freeCounts[i - 1] += count + _freeCounts[i];
                RemoveFreeRange(i);
            }
            else if (joinsLeft)
            {
                _freeCounts[i - 1] += count;
            }
            else if (joinsRight)
            {
                _freeStarts[i] = start;
                _freeCounts[i] += count;
            }
            else
            {
                Array.Copy(_freeStarts, i, _freeStarts, i + 1, _freeRangeCount - i);
                Array.Copy(_freeCounts, i, _freeCounts, i + 1, _freeRangeCount - i);
                _freeStarts[i] = start;
                _freeCounts[i] = count;
                _freeRangeCount++;
            }
        }

        private void RemoveFreeRange(int i)
        {
            _freeRangeCount--;
            Array.Copy(_freeStarts, i + 1, _freeStarts, i, _freeRangeCount - i);
            Array.Copy(_freeCounts, i + 1, _freeCounts, i, _freeRangeCount - i);
        }
    }
}
