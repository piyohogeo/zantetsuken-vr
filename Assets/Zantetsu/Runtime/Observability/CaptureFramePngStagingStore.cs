using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed-capacity, bounded owner of encoded PNG staging entries. The store
    /// takes ownership of each entry on a successful registration and releases
    /// every held entry exactly once on <see cref="Dispose"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The entry array is allocated exactly once in the constructor and is never
    /// reallocated; no <see cref="System.Collections.Generic.List{T}"/>,
    /// <see cref="System.Collections.Generic.Dictionary{TKey, TValue}"/>, LINQ,
    /// or enumeration is used. A <c>null</c> slot is a free slot. The store is
    /// for the main thread only and is not thread-safe.
    /// </para>
    /// <para>
    /// On a successful <see cref="TryRegister"/> the store takes logical
    /// ownership of the entry and the caller must not dispose it. On
    /// <c>false</c> or an exception the entry stays caller-owned and the store
    /// never disposes it.
    /// </para>
    /// <para>
    /// <see cref="RollbackRegistration"/> is an internal, stage-publication-only
    /// rollback seam: it returns ownership of exactly the expected entry to the
    /// caller without disposing it, and only the future terminal coordinator may
    /// call it after a failed pre-<c>Staged</c> publication. It must never be
    /// used for arbitrary entry removal or for collecting entries after their
    /// stage has been published.
    /// </para>
    /// <para>
    /// <see cref="Dispose"/> disposes every held entry individually, never
    /// aborting on a single failure, aggregates any cleanup exceptions, and
    /// transitions to the disposed state only when every held entry has been
    /// disposed. Failed entries stay held and are retried on the next
    /// <see cref="Dispose"/> call. After disposal <see cref="IsCreated"/> is
    /// <c>false</c> and every other API or state property throws
    /// <see cref="ObjectDisposedException"/>.
    /// </para>
    /// <para>
    /// This store owns the entries it holds but performs no file I/O, trace
    /// recording, ID issuance, or queue work, and is not a MonoBehaviour or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFramePngStagingStore : IDisposable
    {
        private readonly CaptureDraftRunContext _run;
        private readonly CaptureFramePngStagingEntry[] _entries;
        private readonly long _maximumTotalByteCount;
        private int _count;
        private long _totalByteCount;
        private long _totalAccepted;
        private long _totalRejected;
        private bool _disposed;

        internal CaptureFramePngStagingStore(
            CaptureDraftRunContext run,
            int maximumEntryCount,
            long maximumTotalByteCount)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (maximumEntryCount < 1 || maximumEntryCount > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEntryCount), maximumEntryCount, "Maximum entry count must be between 1 and 100000.");
            }

            if (maximumTotalByteCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTotalByteCount), maximumTotalByteCount, "Maximum total byte count must be greater than zero.");
            }

            _run = run;
            _maximumTotalByteCount = maximumTotalByteCount;
            _entries = new CaptureFramePngStagingEntry[maximumEntryCount];
            _count = 0;
            _totalByteCount = 0;
            _totalAccepted = 0;
            _totalRejected = 0;
            _disposed = false;
        }

        public CaptureDraftRunContext Run
        {
            get
            {
                ThrowIfDisposed();
                return _run;
            }
        }

        public bool IsCreated => !_disposed;

        public int MaximumEntryCount
        {
            get
            {
                ThrowIfDisposed();
                return _entries.Length;
            }
        }

        public long MaximumTotalByteCount
        {
            get
            {
                ThrowIfDisposed();
                return _maximumTotalByteCount;
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

        public long TotalByteCount
        {
            get
            {
                ThrowIfDisposed();
                return _totalByteCount;
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
        /// Registers a caller-owned entry, transferring its ownership to this
        /// store only on success. Returns <c>false</c>, leaving the store
        /// unchanged and incrementing only <see cref="TotalRejected"/>, when the
        /// entry count or the total byte capacity would be exceeded.
        /// </summary>
        internal bool TryRegister(CaptureFramePngStagingEntry entry)
        {
            ThrowIfDisposed();

            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (!entry.IsCreated)
            {
                throw new ArgumentException("Entry must not be disposed.", nameof(entry));
            }

            if (entry.TestRunId != _run.TestRunId)
            {
                throw new ArgumentException("Entry test run ID must match the store run.", nameof(entry));
            }

            // 5. Same capture frame ID.
            int idIndex = FindByCaptureFrameId(entry.CaptureFrameId);
            if (idIndex >= 0)
            {
                if (ReferenceEquals(_entries[idIndex], entry))
                {
                    throw new ArgumentException("The entry is already registered.", nameof(entry));
                }

                throw new InvalidOperationException("A different entry with the same capture frame ID is already registered.");
            }

            // 6. Same entry instance (defensive duplicate search).
            if (FindByReference(entry) >= 0)
            {
                throw new ArgumentException("The entry is already registered.", nameof(entry));
            }

            // 7. Entry count capacity.
            if (_count >= _entries.Length)
            {
                _totalRejected++;
                return false;
            }

            // 8. Byte capacity, checked without an addition overflow.
            if (entry.ByteCount > _maximumTotalByteCount - _totalByteCount)
            {
                _totalRejected++;
                return false;
            }

            // 9. Register into a free slot.
            int free = FindFreeSlot();
            if (free < 0)
            {
                throw new InvalidOperationException("No free slot is available despite entry capacity.");
            }

            _entries[free] = entry;
            _count++;
            _totalByteCount += entry.ByteCount;
            _totalAccepted++;
            return true;
        }

        /// <summary>
        /// Returns a non-owning reference to the entry registered for the given
        /// capture frame ID, without changing any state or counter.
        /// </summary>
        internal bool TryGet(long captureFrameId, out CaptureFramePngStagingEntry entry)
        {
            ThrowIfDisposed();

            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId), captureFrameId, "Capture frame ID must be greater than zero.");
            }

            int index = FindByCaptureFrameId(captureFrameId);
            if (index < 0)
            {
                entry = null;
                return false;
            }

            entry = _entries[index];
            return true;
        }

        /// <summary>
        /// Internal pre-publication rollback: removes exactly the expected entry
        /// and returns its ownership to the caller without disposing it. The
        /// slot is freed, <see cref="Count"/> and <see cref="TotalByteCount"/>
        /// are restored, and the cumulative acceptance/rejection counters are
        /// never changed.
        /// </summary>
        /// <remarks>
        /// Only the future terminal coordinator may call this after a failed
        /// pre-<c>Staged</c> publication. It must never be used for arbitrary
        /// entry removal or for collecting entries after publication.
        /// </remarks>
        internal CaptureFramePngStagingEntry RollbackRegistration(
            long captureFrameId,
            CaptureFramePngStagingEntry expectedEntry)
        {
            ThrowIfDisposed();

            int index = FindByCaptureFrameId(captureFrameId);
            if (index < 0)
            {
                throw new InvalidOperationException("No entry is registered with the capture frame ID.");
            }

            if (expectedEntry == null)
            {
                throw new ArgumentNullException(nameof(expectedEntry));
            }

            if (!ReferenceEquals(_entries[index], expectedEntry))
            {
                throw new InvalidOperationException("The registered entry is not the expected entry.");
            }

            _entries[index] = null;
            _count--;
            _totalByteCount -= expectedEntry.ByteCount;
            return expectedEntry;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Exception failure = null;
            for (int i = 0; i < _entries.Length; i++)
            {
                CaptureFramePngStagingEntry entry = _entries[i];
                if (entry != null)
                {
                    try
                    {
                        entry.Dispose();
                    }
                    catch (Exception ex)
                    {
                        failure = failure == null
                            ? ex
                            : new AggregateException(failure, ex);
                    }
                }
            }

            if (failure != null)
            {
                // Failed entries stay held and are retried on the next call.
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            _disposed = true;
        }

        private int FindByCaptureFrameId(long captureFrameId)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i] != null && _entries[i].CaptureFrameId == captureFrameId)
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindByReference(CaptureFramePngStagingEntry entry)
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i] != null && ReferenceEquals(_entries[i], entry))
                {
                    return i;
                }
            }

            return -1;
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < _entries.Length; i++)
            {
                if (_entries[i] == null)
                {
                    return i;
                }
            }

            return -1;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }
    }
}
