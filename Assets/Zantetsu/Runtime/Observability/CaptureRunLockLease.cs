using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Sole owner of a Capture Run's acquired OS lock handle. Disposal
    /// releases the handle exactly once and is safe to repeat.
    /// </summary>
    internal sealed class CaptureRunLockLease : IDisposable
    {
        private readonly CaptureRunLockPathSet _pathSet;
        private readonly ICaptureRunLockHandle _handle;
        private bool _released;

        internal CaptureRunLockLease(CaptureRunLockPathSet pathSet, ICaptureRunLockHandle handle)
        {
            if (pathSet == null)
            {
                throw new ArgumentNullException(nameof(pathSet));
            }

            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (!handle.IsCreated)
            {
                throw new ArgumentException("Lock handle must be created.", nameof(handle));
            }

            if (!string.Equals(handle.LockPath, pathSet.LockPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Lock handle path does not match the path set.", nameof(handle));
            }

            _pathSet = pathSet;
            _handle = handle;
        }

        internal CaptureRunLockPathSet PathSet => _pathSet;

        internal bool IsCreated => !_released;

        internal bool IsFullyReleased => _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _handle.Dispose();
            _released = true;
        }
    }
}
