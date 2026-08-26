using System;

namespace Zantetsu.Trace
{
    /// <summary>
    /// Fixed-capacity ring buffer of <see cref="TraceEvent"/> values.
    /// Intended for the main thread only: it is not thread-safe and performs
    /// no locking, allocation, LINQ, iteration, string formatting, or logging
    /// on the write/read/copy paths.
    /// </summary>
    public sealed class TraceRingBuffer
    {
        private readonly TraceEvent[] _buffer;
        private int _head;   // Index where the next event will be written.
        private int _count;  // Number of valid entries, in [0, Capacity].
        private long _totalWritten;
        private long _overwrittenCount;

        public TraceRingBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            _buffer = new TraceEvent[capacity];
            _head = 0;
            _count = 0;
            _totalWritten = 0;
            _overwrittenCount = 0;
        }

        /// <summary>Fixed number of events this buffer can hold.</summary>
        public int Capacity => _buffer.Length;

        /// <summary>Number of valid events currently stored, in [0, Capacity].</summary>
        public int Count => _count;

        /// <summary>Total number of events ever written, including overwritten ones.</summary>
        public long TotalWritten => _totalWritten;

        /// <summary>Number of oldest events discarded due to capacity overflow.</summary>
        public long OverwrittenCount => _overwrittenCount;

        public void Write(in TraceEvent traceEvent)
        {
            _buffer[_head] = traceEvent;

            _head++;
            if (_head == _buffer.Length)
            {
                _head = 0;
            }

            if (_count < _buffer.Length)
            {
                _count++;
            }
            else
            {
                _overwrittenCount++;
            }

            _totalWritten++;
        }

        /// <summary>
        /// Returns the event at the given logical position, where 0 is the
        /// oldest stored event.
        /// </summary>
        public TraceEvent this[int index]
        {
            get
            {
                if (index < 0 || index >= _count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), index, "Index is out of range.");
                }

                return _buffer[PhysicalIndex(index)];
            }
        }

        /// <summary>
        /// Copies the stored events, oldest first, into
        /// <paramref name="destination"/> starting at
        /// <paramref name="destinationIndex"/>. Does not allocate.
        /// </summary>
        public void CopyTo(TraceEvent[] destination, int destinationIndex)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destinationIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationIndex), destinationIndex, "Destination index must not be negative.");
            }

            if (destination.Length - destinationIndex < _count)
            {
                throw new ArgumentException("Destination array does not have enough space for all events.", nameof(destination));
            }

            for (int i = 0; i < _count; i++)
            {
                destination[destinationIndex + i] = _buffer[PhysicalIndex(i)];
            }
        }

        /// <summary>
        /// Resets the buffer to an empty run, clearing contents and counters.
        /// </summary>
        public void Clear()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _head = 0;
            _count = 0;
            _totalWritten = 0;
            _overwrittenCount = 0;
        }

        private int PhysicalIndex(int logicalIndex)
        {
            int start = (_count == _buffer.Length) ? _head : 0;
            return (start + logicalIndex) % _buffer.Length;
        }
    }
}
