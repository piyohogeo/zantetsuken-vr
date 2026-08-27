using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, main-thread-only FIFO queue of encoded capture PNGs.
    /// Owns the <see cref="NativeArray{T}"/> values it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Main-thread only. No locks, concurrent collections, or tasks are used.
    /// </para>
    /// <para>
    /// Ownership transfers only on successful enqueue (caller to queue) and on
    /// successful dequeue (queue to caller). On a rejected or throwing enqueue
    /// the caller keeps ownership and must dispose the PNG itself.
    /// <see cref="Clear"/> and <see cref="Dispose"/> dispose any PNGs still held
    /// by the queue; a dequeued PNG is the caller's responsibility.
    /// </para>
    /// <para>
    /// Performs no file I/O and records no trace events.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngQueue : IDisposable
    {
        private readonly CaptureFrameRequest[] _requests;
        private readonly NativeArray<byte>[] _pngs;
        private int _head;
        private int _count;
        private long _totalAccepted;
        private long _totalRejected;
        private bool _disposed;

        /// <summary>
        /// Test-only seam: when set, the next enqueue attempt throws instead of
        /// enqueuing. Intended exclusively for EditMode tests that exercise
        /// exception-cleanup paths without inducing a real queue failure.
        /// Cleared after one use.
        /// </summary>
        private bool _forceNextEnqueueError;

        public CaptureFramePngQueue(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            _requests = new CaptureFrameRequest[capacity];
            _pngs = new NativeArray<byte>[capacity];
            _head = 0;
            _count = 0;
            _totalAccepted = 0;
            _totalRejected = 0;
            _disposed = false;
        }

        public bool IsCreated => !_disposed;

        public int Capacity
        {
            get
            {
                ThrowIfDisposed();
                return _requests.Length;
            }
        }

        public int Count
        {
            get
            {
                ThrowIfDisposed();
                return _count;
            }
        }

        public long TotalAccepted
        {
            get
            {
                ThrowIfDisposed();
                return _totalAccepted;
            }
        }

        public long TotalRejected
        {
            get
            {
                ThrowIfDisposed();
                return _totalRejected;
            }
        }

        /// <summary>
        /// Enqueues a valid request and its PNG at the FIFO tail. On success the
        /// PNG's ownership transfers to the queue and the caller must no longer
        /// reference or dispose the original <see cref="NativeArray{T}"/>. On
        /// failure the caller keeps ownership.
        /// </summary>
        public bool TryEnqueue(in CaptureFrameRequest frameRequest, NativeArray<byte> pngBytes)
        {
            ThrowIfDisposed();

            if (_forceNextEnqueueError)
            {
                _forceNextEnqueueError = false;
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            if (!frameRequest.IsValid)
            {
                throw new ArgumentException("Frame request must be valid.", nameof(frameRequest));
            }

            if (!pngBytes.IsCreated || pngBytes.Length == 0)
            {
                throw new ArgumentException("PNG buffer must be created and non-empty.", nameof(pngBytes));
            }

            for (int i = 0; i < _count; i++)
            {
                int index = _head + i;
                if (index >= _pngs.Length)
                {
                    index -= _pngs.Length;
                }

                if (_pngs[index].Equals(pngBytes))
                {
                    throw new ArgumentException("The same PNG allocation is already enqueued.", nameof(pngBytes));
                }
            }

            if (_count == _pngs.Length)
            {
                _totalRejected++;
                return false;
            }

            int tail = _head + _count;
            if (tail >= _pngs.Length)
            {
                tail -= _pngs.Length;
            }

            _requests[tail] = frameRequest;
            _pngs[tail] = pngBytes;
            _count++;
            _totalAccepted++;
            return true;
        }

        /// <summary>
        /// Dequeues the FIFO head. On success the PNG's ownership transfers to
        /// the caller, who must dispose it. Counters are unchanged.
        /// </summary>
        public bool TryDequeue(out CaptureFrameRequest frameRequest, out NativeArray<byte> pngBytes)
        {
            ThrowIfDisposed();

            if (_count == 0)
            {
                frameRequest = default;
                pngBytes = default;
                return false;
            }

            frameRequest = _requests[_head];
            pngBytes = _pngs[_head];

            _requests[_head] = default;
            _pngs[_head] = default;

            _head++;
            if (_head == _pngs.Length)
            {
                _head = 0;
            }

            _count--;
            return true;
        }

        /// <summary>
        /// Internal, non-owning peek of the FIFO head. Returns the head request
        /// and a non-owning view of its PNG without modifying queue state. The
        /// returned <see cref="NativeArray{T}"/> is owned by the queue and must
        /// only be used as a read-only save input; the caller must not dispose,
        /// modify, or retain it.
        /// </summary>
        internal bool TryPeek(out CaptureFrameRequest frameRequest, out NativeArray<byte> pngBytes)
        {
            ThrowIfDisposed();

            if (_count == 0)
            {
                frameRequest = default;
                pngBytes = default;
                return false;
            }

            frameRequest = _requests[_head];
            pngBytes = _pngs[_head];
            return true;
        }

        /// <summary>
        /// Disposes every PNG still held by the queue, resets all used slots,
        /// and clears the count. The allocated arrays are reused and the
        /// accepted/rejected counters are preserved.
        /// </summary>
        public void Clear()
        {
            ThrowIfDisposed();

            for (int i = 0; i < _count; i++)
            {
                int index = _head + i;
                if (index >= _pngs.Length)
                {
                    index -= _pngs.Length;
                }

                if (_pngs[index].IsCreated)
                {
                    _pngs[index].Dispose();
                }

                _requests[index] = default;
                _pngs[index] = default;
            }

            _head = 0;
            _count = 0;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Clear();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }
        }
    }
}
