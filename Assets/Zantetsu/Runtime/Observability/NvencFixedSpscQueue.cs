using System;
using System.Threading;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, single-producer/single-consumer FIFO for Phase 0.11
    /// NVENC-internal records. Capacity is fixed at
    /// <see cref="NvencBringUpProfileV1.SubmissionQueueCapacity"/> (eight) and
    /// the backing array is allocated exactly once in the constructor. Enqueue
    /// and dequeue perform no managed allocation, no waiting, and no I/O; a
    /// full queue fails enqueue and an empty queue fails dequeue immediately.
    /// </summary>
    /// <typeparam name="T">A Phase 0.11-internal record value type. The struct
    /// constraint avoids boxing and enumeration-based helpers.</typeparam>
    /// <remarks>
    /// <para>
    /// The contract is strictly one producer and one consumer. The producer is
    /// the only writer of the write position, and the consumer is the only
    /// writer of the read position. The producer writes a payload and then
    /// publishes the advanced write position with a volatile store; the
    /// consumer reads the published write position with a volatile load and
    /// only then observes the payload. After a dequeue the backing slot is
    /// reset to <c>default</c>, so no reference is retained.
    /// </para>
    /// <para>
    /// Logical positions grow monotonically and are never reset; only the
    /// backing index wraps with <c>position % capacity</c>. A distance of zero
    /// means empty, a distance of the capacity means full. When the write
    /// position reaches its maximum value, enqueue fails closed instead of
    /// wrapping. There is no reset, resize, drain, peek, or join API.
    /// </para>
    /// <para>
    /// This type is not synchronized against multiple producers; those callers
    /// are serialized by the later admission linearization boundary. No lock,
    /// wait handle, busy loop, atomic read-modify-write, or pause is used.
    /// </para>
    /// </remarks>
    internal sealed class NvencFixedSpscQueue<T>
        where T : struct
    {
        private const long MaxPosition = long.MaxValue;

        private readonly T[] _items;

        private long _readPosition;
        private long _writePosition;

        internal NvencFixedSpscQueue()
        {
            _items = new T[NvencBringUpProfileV1.SubmissionQueueCapacity];
            _readPosition = 0;
            _writePosition = 0;
        }

        internal int Capacity => _items.Length;

        /// <summary>
        /// An approximate, diagnostic-only snapshot of the number of stored
        /// items. It is not a linearization point and must not be used for
        /// admission decisions.
        /// </summary>
        internal int Count
        {
            get
            {
                long write = Volatile.Read(ref _writePosition);
                long read = Volatile.Read(ref _readPosition);
                long distance = write - read;
                if (distance < 0)
                {
                    distance = 0;
                }
                else if (distance > Capacity)
                {
                    distance = Capacity;
                }

                return (int)distance;
            }
        }

        internal bool TryEnqueue(in T item)
        {
            long write = Volatile.Read(ref _writePosition);
            long read = Volatile.Read(ref _readPosition);

            if (!CanEnqueueWith(write, read))
            {
                return false;
            }

            _items[BackingIndex(write)] = item;
            Volatile.Write(ref _writePosition, write + 1);
            return true;
        }

        /// <summary>
        /// Producer-side O(1) capacity query. True when the next producer-side
        /// <see cref="TryEnqueue"/> is guaranteed to succeed under the strict
        /// single-producer contract, because the consumer only ever frees
        /// capacity. It has no side effect, performs no wait and no allocation,
        /// and shares the exact full/position-limit predicate with
        /// <see cref="TryEnqueue"/>.
        /// </summary>
        internal bool CanEnqueue
        {
            get
            {
                long write = Volatile.Read(ref _writePosition);
                long read = Volatile.Read(ref _readPosition);
                return CanEnqueueWith(write, read);
            }
        }

        internal bool TryDequeue(out T item)
        {
            long read = Volatile.Read(ref _readPosition);
            long write = Volatile.Read(ref _writePosition);

            if (read == write)
            {
                item = default;
                return false;
            }

            int index = BackingIndex(read);
            item = _items[index];
            _items[index] = default;

            Volatile.Write(ref _readPosition, read + 1);
            return true;
        }

        private bool CanEnqueueWith(long write, long read)
        {
            return write - read < Capacity && write < MaxPosition;
        }

        private int BackingIndex(long position)
        {
            return (int)(position % _items.Length);
        }
    }
}
