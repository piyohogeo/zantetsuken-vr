using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, main-thread-only FIFO queue of capture frame requests.
    /// Allocates once in the constructor and performs no allocation, LINQ,
    /// enumeration, logging, or string formatting on the hot path. The queue
    /// does not record trace events.
    /// </summary>
    public sealed class CaptureFrameRequestQueue
    {
        private readonly CaptureFrameRequest[] _buffer;
        private int _head;
        private int _count;
        private long _totalAccepted;
        private long _totalRejected;

        public CaptureFrameRequestQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            _buffer = new CaptureFrameRequest[capacity];
            _head = 0;
            _count = 0;
            _totalAccepted = 0;
            _totalRejected = 0;
        }

        public int Capacity => _buffer.Length;

        public int Count => _count;

        public long TotalAccepted => _totalAccepted;

        public long TotalRejected => _totalRejected;

        /// <summary>
        /// Enqueues a valid request in FIFO order. Returns false, without
        /// overwriting existing requests, when the queue is full.
        /// </summary>
        public bool TryEnqueue(in CaptureFrameRequest request)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (_count == _buffer.Length)
            {
                _totalRejected++;
                return false;
            }

            int tail = _head + _count;
            if (tail >= _buffer.Length)
            {
                tail -= _buffer.Length;
            }

            _buffer[tail] = request;
            _count++;
            _totalAccepted++;
            return true;
        }

        public bool TryDequeue(out CaptureFrameRequest request)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _buffer[_head];
            _buffer[_head] = default;

            _head++;
            if (_head == _buffer.Length)
            {
                _head = 0;
            }

            _count--;
            return true;
        }

        /// <summary>
        /// Returns the FIFO head without modifying queue state. Returns false
        /// with <paramref name="request"/> set to default when empty.
        /// </summary>
        public bool TryPeek(out CaptureFrameRequest request)
        {
            if (_count == 0)
            {
                request = default;
                return false;
            }

            request = _buffer[_head];
            return true;
        }

        /// <summary>
        /// Empties the queue while reusing the allocated buffer. Cumulative
        /// accepted/rejected counters are preserved.
        /// </summary>
        public void Clear()
        {
            _head = 0;
            _count = 0;
        }
    }
}
