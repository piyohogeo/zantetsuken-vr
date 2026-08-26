using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity registry that keeps <see cref="CaptureFrameRecord"/>
    /// instances accepted at capture-request time and lets callers retrieve or
    /// reclaim them later by full <see cref="CaptureFrameRequest"/> match, even
    /// when GPU readback and PNG save complete out of order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backing array is allocated exactly once in the constructor and is
    /// reused by <see cref="Clear"/>. Lookup is a linear scan keyed on
    /// <see cref="CaptureFrameTraceContext.CaptureFrameId"/>; no dictionary,
    /// list, LINQ, enumerator, string allocation, or logging is used.
    /// </para>
    /// <para>
    /// Records are kept as immutable references only; the registry never
    /// disposes them and does not implement <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// This type is intended for the main thread only and is <b>not</b>
    /// thread-safe. Callers must guarantee exclusive access.
    /// </para>
    /// </remarks>
    public sealed class CaptureFrameRecordRegistry
    {
        private readonly CaptureFrameRecord[] _slots;

        private int _count;
        private long _totalAccepted;
        private long _totalRejected;

        /// <summary>
        /// Creates a registry with the given fixed capacity. The backing array
        /// is allocated exactly once and is reused for the lifetime of the
        /// instance.
        /// </summary>
        public CaptureFrameRecordRegistry(int capacity)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be greater than zero.");
            }

            _slots = new CaptureFrameRecord[capacity];
        }

        /// <summary>
        /// Maximum number of records this registry can hold simultaneously.
        /// </summary>
        public int Capacity => _slots.Length;

        /// <summary>
        /// Number of records currently held.
        /// </summary>
        public int Count => _count;

        /// <summary>
        /// Total number of records successfully registered since construction,
        /// including any that were later removed or cleared.
        /// </summary>
        public long TotalAccepted => _totalAccepted;

        /// <summary>
        /// Total number of registration attempts rejected because the registry
        /// was full. Invalid inputs and duplicate registrations are thrown and
        /// do not count here.
        /// </summary>
        public long TotalRejected => _totalRejected;

        /// <summary>
        /// Registers a record if a slot is free and no record with the same
        /// capture frame ID is already held.
        /// </summary>
        /// <returns>
        /// <c>true</c> if the record was stored; <c>false</c> if the registry
        /// was full (in which case only <see cref="TotalRejected"/> increments
        /// and existing records are untouched).
        /// </returns>
        public bool TryRegister(CaptureFrameRecord record)
        {
            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (!record.Request.IsValid)
            {
                throw new ArgumentException("Record request must be valid.", nameof(record));
            }

            if (record.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(record), record.CaptureFrameId, "Capture frame ID must be greater than zero.");
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] != null && _slots[i].CaptureFrameId == record.CaptureFrameId)
                {
                    throw new ArgumentException("A record with the same capture frame ID is already registered.", nameof(record));
                }
            }

            if (_count >= _slots.Length)
            {
                _totalRejected++;
                return false;
            }

            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] == null)
                {
                    _slots[i] = record;
                    _count++;
                    _totalAccepted++;
                    return true;
                }
            }

            // Unreachable: the capacity check above guarantees an empty slot.
            _totalRejected++;
            return false;
        }

        /// <summary>
        /// Retrieves, without removing, the record whose capture frame ID
        /// matches <paramref name="request"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> and the stored record reference on success;
        /// <c>false</c> and <c>null</c> when no matching capture frame ID is
        /// held. Counters and <see cref="Count"/> are never changed.
        /// </returns>
        public bool TryGet(in CaptureFrameRequest request, out CaptureFrameRecord record)
        {
            ValidateLookupRequest(request);

            int index = FindIndex(request.TraceContext.CaptureFrameId);
            if (index < 0)
            {
                record = null;
                return false;
            }

            if (!_slots[index].Request.IdenticalTo(request))
            {
                throw new InvalidOperationException("A record with the same capture frame ID exists but its request does not match.");
            }

            record = _slots[index];
            return true;
        }

        /// <summary>
        /// Removes and returns the record whose capture frame ID matches
        /// <paramref name="request"/>, nulling its slot.
        /// </summary>
        /// <returns>
        /// <c>true</c> and the removed record reference on success, with
        /// <see cref="Count"/> decremented; <c>false</c> and <c>null</c> when no
        /// matching capture frame ID is held. Accepted/rejected counters are
        /// never changed.
        /// </returns>
        public bool TryRemove(in CaptureFrameRequest request, out CaptureFrameRecord record)
        {
            ValidateLookupRequest(request);

            int index = FindIndex(request.TraceContext.CaptureFrameId);
            if (index < 0)
            {
                record = null;
                return false;
            }

            if (!_slots[index].Request.IdenticalTo(request))
            {
                throw new InvalidOperationException("A record with the same capture frame ID exists but its request does not match.");
            }

            record = _slots[index];
            _slots[index] = null;
            _count--;
            return true;
        }

        /// <summary>
        /// Nulls every occupied slot and resets <see cref="Count"/> to zero,
        /// reusing the fixed backing array. <see cref="TotalAccepted"/> and
        /// <see cref="TotalRejected"/> are retained. Records and run references
        /// are not disposed or otherwise touched.
        /// </summary>
        public void Clear()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = null;
            }

            _count = 0;
        }

        private static void ValidateLookupRequest(in CaptureFrameRequest request)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (request.TraceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentException("Capture frame ID must be greater than zero.", nameof(request));
            }
        }

        private int FindIndex(long captureFrameId)
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                if (_slots[i] != null && _slots[i].CaptureFrameId == captureFrameId)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
