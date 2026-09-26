using System;
using System.Threading;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity table of index range descriptors, their states and their read leases (DESIGN 4.5.3). It only
    /// records ranges: it owns no index storage, searches no free space and does not compare ranges with each other.
    /// States move Free → Reserved → Published → Retiring → Free; a reservation can be cancelled back to Free, and a
    /// published range retired without readers becomes Free at once. Any operation given a default, foreign or stale
    /// handle or lease, or called in another state, returns false and changes nothing. The main thread alone uses
    /// the table; leases lent to jobs are returned after the jobs are collected.
    /// <para>
    /// Identities never wrap, so an old handle or lease cannot match again. Table ids are never reused; creating a
    /// table after the last id throws. A descriptor whose generation reached the last value is not registered again
    /// once Free, which shrinks the usable capacity; registration fails when no usable descriptor is free. A lease
    /// slot whose generation reached the last value is not reused once returned. Acquiring a lease fails, changing
    /// nothing, when the range's reader count is at its limit or no lease slot can be taken.
    /// </para>
    /// </summary>
    public sealed class VpIndexRangeLifecycleTable
    {
        private struct Descriptor
        {
            public VpIndexRangeState state;
            public uint generation;
            public int indexStart;
            public int indexCount;
            public int readerCount;
        }

        private struct LeaseSlot
        {
            public bool held;
            public uint generation;
            public int descriptor;
            public uint descriptorGeneration;
        }

        private static int s_lastTableId;

        private readonly int _tableId;
        private readonly Descriptor[] _descriptors;
        private readonly uint _lastGeneration;
        private readonly int _maxReaderCount;
        private readonly int _maxLeaseSlotCount;

        // Lease slots grow on demand up to the int index range. Returned slots are taken again first.
        private LeaseSlot[] _leaseSlots = Array.Empty<LeaseSlot>();
        private int _usedLeaseSlotCount;
        private int[] _returnedLeaseSlots = Array.Empty<int>();
        private int _returnedLeaseSlotCount;

        public VpIndexRangeLifecycleTable(int descriptorCapacity)
            : this(descriptorCapacity, uint.MaxValue, int.MaxValue, int.MaxValue)
        {
        }

        /// <summary>A table whose identity limits are lowered, so tests can reach them.</summary>
        internal VpIndexRangeLifecycleTable(int descriptorCapacity, uint lastGeneration, int maxReaderCount, int maxLeaseSlotCount)
        {
            if (descriptorCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(descriptorCapacity), descriptorCapacity, "Must not be negative.");
            }

            if (lastGeneration < 1 || maxReaderCount < 1 || maxLeaseSlotCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(lastGeneration), "Identity limits must be at least 1.");
            }

            _lastGeneration = lastGeneration;
            _maxReaderCount = maxReaderCount;
            _maxLeaseSlotCount = maxLeaseSlotCount;
            _descriptors = new Descriptor[descriptorCapacity];
            _tableId = NextTableId(ref s_lastTableId);
        }

        public int DescriptorCapacity => _descriptors.Length;

        /// <summary>
        /// Registers [indexStart, indexStart + indexCount) as Reserved in a free descriptor that can take another
        /// generation. An empty range is accepted. Returns false with a default handle when either value is negative,
        /// the end exceeds int.MaxValue, or no such descriptor is free.
        /// </summary>
        public bool TryReserve(int indexStart, int indexCount, out VpIndexRangeHandle handle)
        {
            handle = default;
            if (indexStart < 0 || indexCount < 0 || (long)indexStart + indexCount > int.MaxValue)
            {
                return false;
            }

            for (int d = 0; d < _descriptors.Length; d++)
            {
                ref Descriptor descriptor = ref _descriptors[d];
                if (descriptor.state != VpIndexRangeState.Free || descriptor.generation == _lastGeneration)
                {
                    continue;
                }

                descriptor.state = VpIndexRangeState.Reserved;
                descriptor.generation = checked(descriptor.generation + 1);
                descriptor.indexStart = indexStart;
                descriptor.indexCount = indexCount;
                descriptor.readerCount = 0;
                handle = new VpIndexRangeHandle(_tableId, d, descriptor.generation);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gives a Reserved range with no index -- a descriptor its owner holds in reserve -- the range
        /// [indexStart, indexStart + indexCount), still Reserved and under the same handle. It is how a publish in two
        /// parts uses a descriptor that was reserved beforehand instead of looking for a free one. Fails, changing
        /// nothing, for a handle that is not Reserved or already has indices, or a range that is negative or ends past
        /// int.MaxValue.
        /// </summary>
        internal bool TryPlaceHeld(VpIndexRangeHandle handle, int indexStart, int indexCount)
        {
            if (!TryFind(handle, out int d)
                || _descriptors[d].state != VpIndexRangeState.Reserved
                || _descriptors[d].indexCount != 0
                || indexStart < 0
                || indexCount < 0
                || (long)indexStart + indexCount > int.MaxValue)
            {
                return false;
            }

            _descriptors[d].indexStart = indexStart;
            _descriptors[d].indexCount = indexCount;
            return true;
        }

        /// <summary>Reserved → Published.</summary>
        public bool TryPublish(VpIndexRangeHandle handle)
        {
            if (!TryFind(handle, out int d) || _descriptors[d].state != VpIndexRangeState.Reserved)
            {
                return false;
            }

            _descriptors[d].state = VpIndexRangeState.Published;
            return true;
        }

        /// <summary>
        /// Reserved → Published with the range cut to its first <paramref name="publishedCount"/> indices, for the
        /// allocator to return the unused tail. A range cut to nothing starts at 0. Fails when the count is negative or
        /// exceeds the reserved count.
        /// </summary>
        internal bool TryPublish(VpIndexRangeHandle handle, int publishedCount)
        {
            if (!TryFind(handle, out int d)
                || _descriptors[d].state != VpIndexRangeState.Reserved
                || publishedCount < 0
                || publishedCount > _descriptors[d].indexCount)
            {
                return false;
            }

            ref Descriptor descriptor = ref _descriptors[d];
            descriptor.state = VpIndexRangeState.Published;
            descriptor.indexCount = publishedCount;
            if (publishedCount == 0)
            {
                descriptor.indexStart = 0;
            }

            return true;
        }

        /// <summary>Reserved → Free, for an unused or failed reservation.</summary>
        public bool TryCancelReservation(VpIndexRangeHandle handle)
        {
            if (!TryFind(handle, out int d) || _descriptors[d].state != VpIndexRangeState.Reserved)
            {
                return false;
            }

            _descriptors[d].state = VpIndexRangeState.Free;
            return true;
        }

        /// <summary>
        /// Lends a new read lease on a Published range. Returns false with a default lease in any other state, when the
        /// range's reader count is at its limit, or when no lease slot can be taken.
        /// </summary>
        public bool TryAcquireReadLease(VpIndexRangeHandle handle, out VpIndexReadLease lease)
        {
            lease = default;
            if (!TryFind(handle, out int d)
                || _descriptors[d].state != VpIndexRangeState.Published
                || _descriptors[d].readerCount == _maxReaderCount
                || !TryTakeLeaseSlot(out int slot))
            {
                return false;
            }

            ref LeaseSlot leaseSlot = ref _leaseSlots[slot];
            leaseSlot.held = true;
            leaseSlot.generation = checked(leaseSlot.generation + 1);
            leaseSlot.descriptor = d;
            leaseSlot.descriptorGeneration = handle.generation;
            _descriptors[d].readerCount++;
            lease = new VpIndexReadLease(handle, slot, leaseSlot.generation);
            return true;
        }

        /// <summary>Published → Free when no lease is held, otherwise Published → Retiring.</summary>
        public bool TryRetire(VpIndexRangeHandle handle)
        {
            if (!TryFind(handle, out int d) || _descriptors[d].state != VpIndexRangeState.Published)
            {
                return false;
            }

            ref Descriptor descriptor = ref _descriptors[d];
            descriptor.state = descriptor.readerCount == 0 ? VpIndexRangeState.Free : VpIndexRangeState.Retiring;
            return true;
        }

        /// <summary>
        /// Returns a held lease. Returning the last lease of a Retiring range frees it. A lease already returned, or one
        /// of an earlier registration of the descriptor, is rejected.
        /// </summary>
        public bool TryReleaseReadLease(VpIndexReadLease lease)
        {
            if (!IsHeld(lease))
            {
                return false;
            }

            int d = lease.range.descriptor;
            ref LeaseSlot leaseSlot = ref _leaseSlots[lease.slot];
            leaseSlot.held = false;
            if (leaseSlot.generation != _lastGeneration)
            {
                _returnedLeaseSlots[_returnedLeaseSlotCount++] = lease.slot;
            }

            ref Descriptor descriptor = ref _descriptors[d];
            descriptor.readerCount--;
            if (descriptor.readerCount == 0 && descriptor.state == VpIndexRangeState.Retiring)
            {
                descriptor.state = VpIndexRangeState.Free;
            }

            return true;
        }

        /// <summary>
        /// The current state and the registered range of the handle's registration, which a Free descriptor keeps until
        /// it is registered again. Returns false with Free and an empty range for a rejected handle.
        /// </summary>
        public bool TryGetState(VpIndexRangeHandle handle, out VpIndexRangeState state, out int indexStart, out int indexCount)
        {
            if (!TryFind(handle, out int d))
            {
                state = VpIndexRangeState.Free;
                indexStart = 0;
                indexCount = 0;
                return false;
            }

            state = _descriptors[d].state;
            indexStart = _descriptors[d].indexStart;
            indexCount = _descriptors[d].indexCount;
            return true;
        }

        /// <summary>
        /// Whether the lease is held: lent by this table on the current registration of its descriptor and not yet
        /// returned. A held lease's range is Published or Retiring.
        /// </summary>
        internal bool IsHeld(VpIndexReadLease lease)
        {
            if (!TryFind(lease.range, out int d) || (uint)lease.slot >= (uint)_usedLeaseSlotCount)
            {
                return false;
            }

            ref LeaseSlot leaseSlot = ref _leaseSlots[lease.slot];
            return leaseSlot.held
                && leaseSlot.generation == lease.slotGeneration
                && leaseSlot.descriptor == d
                && leaseSlot.descriptorGeneration == lease.range.generation;
        }

        /// <summary>The number of leases currently held on the handle's registration.</summary>
        internal bool TryGetReaderCount(VpIndexRangeHandle handle, out int readerCount)
        {
            if (!TryFind(handle, out int d))
            {
                readerCount = 0;
                return false;
            }

            readerCount = _descriptors[d].readerCount;
            return true;
        }

        /// <summary>
        /// Advances <paramref name="lastTableId"/> atomically and returns the new id. Ids start at 1 and never wrap: at
        /// int.MaxValue it throws and leaves the value unchanged.
        /// </summary>
        internal static int NextTableId(ref int lastTableId)
        {
            while (true)
            {
                int last = Volatile.Read(ref lastTableId);
                if (last == int.MaxValue)
                {
                    throw new InvalidOperationException("No index range lifecycle table ids remain.");
                }

                if (Interlocked.CompareExchange(ref lastTableId, last + 1, last) == last)
                {
                    return last + 1;
                }
            }
        }

        private bool TryFind(VpIndexRangeHandle handle, out int descriptor)
        {
            descriptor = handle.descriptor;
            return handle.tableId == _tableId
                && (uint)descriptor < (uint)_descriptors.Length
                && _descriptors[descriptor].generation == handle.generation;
        }

        /// <summary>A returned slot, else a new one. Fails without any change when every slot is used.</summary>
        private bool TryTakeLeaseSlot(out int slot)
        {
            if (_returnedLeaseSlotCount > 0)
            {
                slot = _returnedLeaseSlots[--_returnedLeaseSlotCount];
                return true;
            }

            if (_usedLeaseSlotCount == _maxLeaseSlotCount)
            {
                slot = -1;
                return false;
            }

            if (_usedLeaseSlotCount == _leaseSlots.Length)
            {
                int grown = (int)Math.Min(_maxLeaseSlotCount, Math.Max(4L, 2L * _leaseSlots.Length));
                Array.Resize(ref _leaseSlots, grown);
                Array.Resize(ref _returnedLeaseSlots, grown);
            }

            slot = _usedLeaseSlotCount++;
            return true;
        }
    }
}
