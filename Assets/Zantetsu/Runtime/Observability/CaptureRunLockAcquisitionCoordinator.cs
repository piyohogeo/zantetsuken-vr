using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Acquires a Capture Run's single OS lock through the given backend and
    /// hands the caller the lease that owns it. Ordinary contention returns
    /// false without a handle; a backend that breaks its own contract is
    /// reported rather than papered over.
    /// </summary>
    internal sealed class CaptureRunLockAcquisitionCoordinator
    {
        private readonly ICaptureRunLockBackend _backend;

        internal CaptureRunLockAcquisitionCoordinator(ICaptureRunLockBackend backend)
        {
            if (backend == null)
            {
                throw new ArgumentNullException(nameof(backend));
            }

            _backend = backend;
        }

        internal bool TryAcquire(CaptureRunLockPathSet pathSet, out CaptureRunLockLease lease)
        {
            lease = null;
            if (pathSet == null)
            {
                throw new ArgumentNullException(nameof(pathSet));
            }

            ICaptureRunLockHandle handle;
            if (!_backend.TryAcquire(pathSet.LockPath, out handle))
            {
                if (handle != null)
                {
                    handle.Dispose();
                    throw new InvalidOperationException("Lock backend returned false with a non-null handle.");
                }

                return false;
            }

            if (handle == null)
            {
                throw new InvalidOperationException("Lock backend returned true with a null handle.");
            }

            try
            {
                lease = new CaptureRunLockLease(pathSet, handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }

            return true;
        }
    }
}
