using System;
using System.Collections.Generic;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Owns the two acquired lock handles of a Run in their fixed acquisition
    /// order and releases them in reverse order on disposal.
    /// </summary>
    /// <remarks>
    /// The handles are never exposed; only <see cref="PathSet"/> and
    /// <see cref="IsCreated"/> are visible. Disposal releases the second handle
    /// then the first, tries each independently so one failure never skips the
    /// other, retries only the failed handles on the next disposal, never
    /// double-disposes a released handle, and reports multiple failures as an
    /// <see cref="AggregateException"/> in reverse acquisition order. A fully
    /// successful disposal is idempotent. <see cref="PathSet"/> remains
    /// readable after disposal.
    /// </remarks>
    internal sealed class CaptureRunLockLease : IDisposable
    {
        private readonly CaptureRunLockPathSet _pathSet;
        private readonly ICaptureRunLockHandle _firstHandle;
        private readonly ICaptureRunLockHandle _secondHandle;
        private bool _disposed;
        private bool _firstReleased;
        private bool _secondReleased;

        internal CaptureRunLockLease(
            CaptureRunLockPathSet pathSet,
            ICaptureRunLockHandle firstHandle,
            ICaptureRunLockHandle secondHandle)
        {
            if (pathSet == null)
            {
                throw new ArgumentNullException(nameof(pathSet));
            }

            if (firstHandle == null)
            {
                throw new ArgumentNullException(nameof(firstHandle));
            }

            if (secondHandle == null)
            {
                throw new ArgumentNullException(nameof(secondHandle));
            }

            if (ReferenceEquals(firstHandle, secondHandle))
            {
                throw new ArgumentException("First and second lock handles must be distinct instances.", nameof(secondHandle));
            }

            if (!firstHandle.IsCreated)
            {
                throw new ArgumentException("First lock handle must be created.", nameof(firstHandle));
            }

            if (!secondHandle.IsCreated)
            {
                throw new ArgumentException("Second lock handle must be created.", nameof(secondHandle));
            }

            if (!string.Equals(firstHandle.LockPath, pathSet.FirstLockPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("First lock handle path does not match the path set.", nameof(firstHandle));
            }

            if (!string.Equals(secondHandle.LockPath, pathSet.SecondLockPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Second lock handle path does not match the path set.", nameof(secondHandle));
            }

            _pathSet = pathSet;
            _firstHandle = firstHandle;
            _secondHandle = secondHandle;
        }

        internal CaptureRunLockPathSet PathSet => _pathSet;

        internal bool IsCreated => !_disposed;

        public void Dispose()
        {
            if (_disposed && _firstReleased && _secondReleased)
            {
                return;
            }

            _disposed = true;

            List<Exception> failures = new List<Exception>();

            if (!_secondReleased)
            {
                try
                {
                    _secondHandle.Dispose();
                    _secondReleased = true;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (!_firstReleased)
            {
                try
                {
                    _firstHandle.Dispose();
                    _firstReleased = true;
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Failed to release lock handles.", failures);
            }
        }
    }
}
