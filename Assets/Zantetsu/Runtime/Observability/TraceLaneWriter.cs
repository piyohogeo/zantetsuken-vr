using System.Threading;
using Unity.Collections.LowLevel.Unsafe;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The producer side of one trace lane: a value type with no managed
    /// reference in it, so it can be copied into a Burst-compiled job and
    /// written from there. It owns nothing and allocates nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TryWrite"/> never waits. It copies the caller's payload into
    /// the lane's own ring before it returns, so the caller may reuse or
    /// overwrite that memory immediately afterwards, and it publishes the index
    /// entry last - that publication is the record's only commit point, and
    /// until it happens a consumer sees nothing of the record.
    /// </para>
    /// <para>
    /// A disabled event is not a drop: nothing is written and the lane's drop
    /// count does not move, because the lane was told not to carry that event
    /// at all. Everything else that cannot be written - no index slot, no
    /// payload room once the padding a contiguous record needs is counted, a
    /// payload longer than the ring, a null payload for a non-empty record -
    /// returns false and adds one to the lane's drop count, saturating rather
    /// than wrapping. There is no exception for an ordinary full lane, no other
    /// lane to try, no growth, no sampling, and no retry.
    /// </para>
    /// <para>
    /// Only one producer may hold a writer for a lane at a time. The cursors
    /// this side owns are read and written plainly; the two the consumer owns
    /// are read through <see cref="Interlocked"/>, and the index publication is
    /// written through it, which is what orders the payload copy and the entry
    /// before the record becomes visible.
    /// </para>
    /// </remarks>
    internal readonly unsafe struct TraceLaneWriter
    {
        // The lane owns these regions and outlives the writer by contract, so
        // the job safety system is told the pointers are deliberate rather
        // than a container that should have been tracked.
        [NativeDisableUnsafePtrRestriction]
        private readonly byte* _payload;

        [NativeDisableUnsafePtrRestriction]
        private readonly TraceLaneIndexEntry* _index;

        [NativeDisableUnsafePtrRestriction]
        private readonly long* _cursors;
        private readonly int _payloadCapacity;
        private readonly int _maxPayloadLength;
        private readonly int _indexCapacity;
        private readonly TraceLaneEventMask _enabled;

        internal TraceLaneWriter(
            byte* payload,
            TraceLaneIndexEntry* index,
            long* cursors,
            int payloadCapacity,
            int maxPayloadLength,
            int indexCapacity,
            TraceLaneEventMask enabled)
        {
            _payload = payload;
            _index = index;
            _cursors = cursors;
            _payloadCapacity = payloadCapacity;
            _maxPayloadLength = maxPayloadLength;
            _indexCapacity = indexCapacity;
            _enabled = enabled;
        }

        /// <summary>
        /// The longest payload one record of this lane can carry. It is a
        /// limit of its own, not the size of the ring: a bigger ring holds
        /// more records, it does not make one record longer.
        /// </summary>
        internal int MaxPayloadLength => _maxPayloadLength;

        /// <summary>
        /// True only for an event this lane accepts, so a caller can skip
        /// building a payload it would not be allowed to write.
        /// </summary>
        internal bool IsEnabled(TraceEventType recordKind)
        {
            return _enabled.IsEnabled(recordKind);
        }

        /// <summary>
        /// Copies one record into the lane and commits it, or returns false
        /// having changed nothing a consumer can see.
        /// </summary>
        internal bool TryWrite(TraceEventType recordKind, byte* payload, int payloadLength)
        {
            // An event this lane does not carry is not a loss of data: it was
            // never this lane's to hold.
            if (!_enabled.IsEnabled(recordKind))
            {
                return false;
            }

            // Longer than one record of this lane may be, whatever room
            // the ring happens to have.
            if (payloadLength < 0 || payloadLength > _maxPayloadLength)
            {
                CountDrop();
                return false;
            }

            if (payloadLength > 0 && payload == null)
            {
                CountDrop();
                return false;
            }

            long indexWrite = _cursors[TraceLaneCursors.IndexWrite];
            long indexRead = Interlocked.Read(ref _cursors[TraceLaneCursors.IndexRead]);
            if (indexWrite - indexRead >= _indexCapacity)
            {
                CountDrop();
                return false;
            }

            long payloadWrite = _cursors[TraceLaneCursors.PayloadWrite];
            long payloadRead = Interlocked.Read(ref _cursors[TraceLaneCursors.PayloadRead]);

            // A record is always one contiguous run of bytes. If it will not
            // fit before the end of the ring, the rest of the tail is spent as
            // padding and the record starts again at the beginning - and that
            // padding counts against the room the lane has left, so a record
            // that only fits by ignoring it is refused.
            int offset = (int)(payloadWrite % _payloadCapacity);
            int tail = _payloadCapacity - offset;
            long start = payloadWrite;
            int needed = payloadLength;
            if (payloadLength > tail)
            {
                start = payloadWrite + tail;
                needed = tail + payloadLength;
            }

            if (needed > _payloadCapacity - (payloadWrite - payloadRead))
            {
                CountDrop();
                return false;
            }

            if (payloadLength > 0)
            {
                UnsafeUtility.MemCpy(
                    _payload + (int)(start % _payloadCapacity), payload, payloadLength);
            }

            _index[(int)(indexWrite % _indexCapacity)] =
                new TraceLaneIndexEntry(recordKind, start, payloadLength);

            _cursors[TraceLaneCursors.PayloadWrite] = payloadWrite + needed;

            // The one commit point. Everything above is invisible until this
            // store, and this store also orders the copy and the entry before
            // anything a consumer can observe.
            Interlocked.Exchange(ref _cursors[TraceLaneCursors.IndexWrite], indexWrite + 1);
            return true;
        }

        /// <summary>
        /// Adds one to the lane's drop count, and stops at the largest value
        /// rather than wrapping back to nothing.
        /// </summary>
        private void CountDrop()
        {
            long current = _cursors[TraceLaneCursors.DropCount];
            if (current == long.MaxValue)
            {
                return;
            }

            Interlocked.Exchange(ref _cursors[TraceLaneCursors.DropCount], current + 1);
        }
    }
}
