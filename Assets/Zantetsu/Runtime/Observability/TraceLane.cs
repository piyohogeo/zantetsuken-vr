using System;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed cursor slots inside one lane's cursor block. Five longs, and no
    /// state beyond them.
    /// </summary>
    internal static class TraceLaneCursors
    {
        /// <summary>Published by the producer; the record commit point.</summary>
        internal const int IndexWrite = 0;

        /// <summary>Published by the consumer once a record is finished with.</summary>
        internal const int IndexRead = 1;

        /// <summary>The producer's own position in the payload ring.</summary>
        internal const int PayloadWrite = 2;

        /// <summary>Published by the consumer; payload room the lane may reuse.</summary>
        internal const int PayloadRead = 3;

        /// <summary>Records this lane could not take, saturating.</summary>
        internal const int DropCount = 4;

        internal const int Count = 5;
    }

    /// <summary>
    /// One variable-length trace lane: a fixed payload ring, a fixed index
    /// ring, the few cursors they need, and a drop count. One producer and one
    /// consumer at a time, neither of which ever waits for the other.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything the lane holds is allocated once, in one block, when the lane
    /// is constructed: the cursor block first, then the index ring, then the
    /// payload ring. Nothing is resized, no second lane is kept in reserve, and
    /// no record is ever split - a payload that will not fit before the end of
    /// the ring spends the rest of the tail as padding and starts again at the
    /// beginning, so a consumer always sees one contiguous run of bytes.
    /// </para>
    /// <para>
    /// Publishing an index entry is the only commit point. The producer copies
    /// the payload and fills its private index slot first, and only then makes
    /// the new write position visible; the consumer reads that position before
    /// it looks at either the entry or the payload, so it can never see a
    /// half-written record or an entry that has not been committed.
    /// </para>
    /// <para>
    /// A peeked record's payload stays untouched until it is consumed: the
    /// producer only reuses payload room the consumer has published as
    /// finished. This type holds no registry, lease, generation, receipt, or
    /// status enum, and it is not a general-purpose ring buffer - it carries
    /// trace records for one producer and one consumer and nothing else.
    /// </para>
    /// </remarks>
    internal sealed unsafe class TraceLane : IDisposable
    {
        private readonly int _payloadCapacity;
        private readonly int _indexCapacity;
        private readonly TraceLaneEventMask _enabled;
        private readonly long _blockBytes;

        private byte* _block;
        private long* _cursors;
        private TraceLaneIndexEntry* _index;
        private byte* _payload;

        /// <summary>
        /// Validates the capacities and takes the lane's whole allocation in
        /// one call. A lane that cannot be built allocates nothing.
        /// </summary>
        internal TraceLane(int payloadCapacity, int indexCapacity, TraceLaneEventMask enabled)
        {
            if (payloadCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payloadCapacity), payloadCapacity, "Payload capacity must be positive.");
            }

            if (indexCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(indexCapacity), indexCapacity, "Index capacity must be positive.");
            }

            _payloadCapacity = payloadCapacity;
            _indexCapacity = indexCapacity;
            _enabled = enabled;

            int entrySize = UnsafeUtility.SizeOf<TraceLaneIndexEntry>();
            long cursorBytes = CursorBlockBytes;
            long indexBytes = (long)entrySize * indexCapacity;
            _blockBytes = cursorBytes + indexBytes + payloadCapacity;

            _block = (byte*)UnsafeUtility.Malloc(_blockBytes, CursorBlockBytes, Allocator.Persistent);
            if (_block == null)
            {
                throw new OutOfMemoryException("The trace lane's region could not be allocated.");
            }

            UnsafeUtility.MemClear(_block, _blockBytes);
            _cursors = (long*)_block;
            _index = (TraceLaneIndexEntry*)(_block + cursorBytes);
            _payload = _block + cursorBytes + indexBytes;
        }

        /// <summary>
        /// The cursor block's size, which is also the block's alignment: one
        /// cache line, so the five cursors share no line with the index ring.
        /// </summary>
        private const int CursorBlockBytes = 64;

        internal int PayloadCapacity => _payloadCapacity;

        internal int IndexCapacity => _indexCapacity;

        /// <summary>The longest payload one record of this lane can carry.</summary>
        internal int MaxPayloadLength => _payloadCapacity;

        /// <summary>Bytes this lane holds, for a test to say it was taken and given back.</summary>
        internal long AllocatedBytes => _block == null ? 0L : _blockBytes;

        /// <summary>Records this lane could not take. Saturating, never wrapping.</summary>
        internal long DropCount
        {
            get
            {
                RequireLive();
                return Interlocked.Read(ref _cursors[TraceLaneCursors.DropCount]);
            }
        }

        /// <summary>
        /// The one producer's writer. A value type with no managed reference,
        /// so it can be copied into a Burst-compiled job.
        /// </summary>
        internal TraceLaneWriter CreateWriter()
        {
            RequireLive();
            return new TraceLaneWriter(
                _payload, _index, _cursors, _payloadCapacity, _indexCapacity, _enabled);
        }

        /// <summary>
        /// The next committed record, if there is one: its kind, where its
        /// payload is and how long it is. The payload stays valid and
        /// unchanged until <see cref="Consume"/> is called for it.
        /// </summary>
        /// <remarks>
        /// The write position is read first, and only a record behind it is
        /// looked at, so a record still being written is never read. For one
        /// consumer only; nothing is leased, acknowledged, or allocated here.
        /// </remarks>
        internal bool TryPeek(out TraceLaneIndexEntry entry, out byte* payload)
        {
            RequireLive();

            long read = _cursors[TraceLaneCursors.IndexRead];
            long write = Interlocked.Read(ref _cursors[TraceLaneCursors.IndexWrite]);
            if (read == write)
            {
                entry = default;
                payload = null;
                return false;
            }

            entry = _index[(int)(read % _indexCapacity)];
            payload = _payload + (int)(entry.PayloadStart % _payloadCapacity);
            return true;
        }

        /// <summary>
        /// Releases the record <see cref="TryPeek"/> returned, giving its
        /// payload room - and any padding skipped before it - back to the
        /// producer.
        /// </summary>
        /// <remarks>
        /// The payload room is published before the index slot, so the producer
        /// never learns a slot is free while the bytes behind it are still
        /// being read. Because the payload position only grows, releasing to
        /// the end of this record also releases whatever padding preceded it.
        /// </remarks>
        internal void Consume(in TraceLaneIndexEntry entry)
        {
            RequireLive();

            Interlocked.Exchange(
                ref _cursors[TraceLaneCursors.PayloadRead],
                entry.PayloadStart + entry.PayloadLength);
            Interlocked.Exchange(
                ref _cursors[TraceLaneCursors.IndexRead],
                _cursors[TraceLaneCursors.IndexRead] + 1);
        }

        /// <summary>
        /// Releases the lane's region. Only valid once the producer and the
        /// consumer have both stopped; this type has no way to stop them and
        /// does not try. Calling it twice does nothing the second time.
        /// </summary>
        public void Dispose()
        {
            if (_block == null)
            {
                return;
            }

            UnsafeUtility.Free(_block, Allocator.Persistent);
            _block = null;
            _cursors = null;
            _index = null;
            _payload = null;
        }

        private void RequireLive()
        {
            if (_block == null)
            {
                throw new ObjectDisposedException(nameof(TraceLane));
            }
        }
    }
}
