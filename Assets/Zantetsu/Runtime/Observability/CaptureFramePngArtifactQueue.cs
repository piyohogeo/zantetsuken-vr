using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity FIFO of prepared <see cref="CaptureFramePngArtifact"/>
    /// instances that have been saved but whose sidecars have not yet been
    /// published. Used to decouple PNG save from sidecar publication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. The backing array is
    /// allocated exactly once in the constructor and reused by
    /// <see cref="Clear"/>. Enqueue, peek, dequeue, and clear perform no
    /// allocation, no LINQ, no enumeration, no boxing, no string formatting,
    /// no reflection, and no logging.
    /// </para>
    /// <para>
    /// Holds only references to artifacts. It never owns, mutates, deletes, or
    /// disposes the artifacts, their frame records, receipts, or PNG files, and
    /// does not implement <see cref="IDisposable"/>. It performs no file I/O and
    /// uses no Unity static API.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactQueue
    {
        private readonly CaptureFramePngArtifact[] _buffer;
        private int _head;
        private int _count;
        private long _totalAccepted;
        private long _totalRejected;

        public CaptureFramePngArtifactQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            _buffer = new CaptureFramePngArtifact[capacity];
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
        /// Enqueues an artifact at the FIFO tail. When the queue is full this
        /// returns false without changing the existing contents and increments
        /// only <see cref="TotalRejected"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> when the artifact was enqueued; <c>false</c> when the
        /// queue was full, in which case only <see cref="TotalRejected"/>
        /// increments and the existing contents are untouched.
        /// </returns>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="artifact"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// An artifact with the same capture frame ID is already enqueued; the
        /// queue contents and counters are unchanged.
        /// </exception>
        public bool TryEnqueue(CaptureFramePngArtifact artifact)
        {
            if (artifact == null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            for (int i = 0; i < _count; i++)
            {
                int index = _head + i;
                if (index >= _buffer.Length)
                {
                    index -= _buffer.Length;
                }

                if (_buffer[index] != null && _buffer[index].CaptureFrameId == artifact.CaptureFrameId)
                {
                    throw new ArgumentException("An artifact with the same capture frame ID is already enqueued.", nameof(artifact));
                }
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

            _buffer[tail] = artifact;
            _count++;
            _totalAccepted++;
            return true;
        }

        /// <summary>
        /// Returns the FIFO head without modifying queue state or counters.
        /// Returns false with <paramref name="artifact"/> set to null when empty.
        /// </summary>
        public bool TryPeek(out CaptureFramePngArtifact artifact)
        {
            if (_count == 0)
            {
                artifact = null;
                return false;
            }

            artifact = _buffer[_head];
            return true;
        }

        /// <summary>
        /// Returns the FIFO head and nulls its slot. Cumulative counters are
        /// unchanged. Returns false with <paramref name="artifact"/> set to null
        /// when empty.
        /// </summary>
        public bool TryDequeue(out CaptureFramePngArtifact artifact)
        {
            if (_count == 0)
            {
                artifact = null;
                return false;
            }

            artifact = _buffer[_head];
            _buffer[_head] = null;

            _head++;
            if (_head == _buffer.Length)
            {
                _head = 0;
            }

            _count--;
            return true;
        }

        /// <summary>
        /// Nulls every held reference and resets <see cref="Count"/> to zero,
        /// reusing the allocated array. <see cref="TotalAccepted"/> and
        /// <see cref="TotalRejected"/> are preserved. Artifacts are not disposed.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _buffer.Length; i++)
            {
                _buffer[i] = null;
            }

            _head = 0;
            _count = 0;
        }
    }
}
