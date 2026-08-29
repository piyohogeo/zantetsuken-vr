using System;
using Microsoft.Win32.SafeHandles;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Windows lock handle owning an acquired OS file handle. The OS handle,
    /// not the existence of the lock file, is the source of truth for lock
    /// ownership.
    /// </summary>
    /// <remarks>
    /// Disposal is idempotent and releases the OS handle exactly once.
    /// </remarks>
    internal sealed class CaptureRunWindowsLockHandle : ICaptureRunLockHandle
    {
        private SafeFileHandle _handle;
        private bool _disposed;

        internal CaptureRunWindowsLockHandle(string lockPath, SafeFileHandle handle)
        {
            if (lockPath == null)
            {
                throw new ArgumentNullException(nameof(lockPath));
            }

            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (handle.IsInvalid)
            {
                throw new ArgumentException("Lock handle must be a valid OS handle.", nameof(handle));
            }

            if (handle.IsClosed)
            {
                throw new ArgumentException("Lock handle must not be closed.", nameof(handle));
            }

            LockPath = lockPath;
            _handle = handle;
        }

        public string LockPath { get; }

        public bool IsCreated => !_disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _handle?.Dispose();
            _handle = null;
            _disposed = true;
        }
    }
}
