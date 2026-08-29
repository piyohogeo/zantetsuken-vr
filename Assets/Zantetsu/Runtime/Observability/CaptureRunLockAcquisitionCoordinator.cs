using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Acquires the two Run locks in their fixed order and rolls back
    /// previously acquired handles in reverse order on any failure.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This coordinator is main-thread only and not thread-safe in this stage.
    /// It holds only the backend reference and is not disposable; it never
    /// disposes the backend. It performs no directory or root creation, no
    /// no-follow implementation, no file, file stream, or P/Invoke access, no
    /// wait or sleep, no initialization ID generation, no marker generation, no
    /// run root mutation, no trace recording, and no event generation.
    /// </para>
    /// </remarks>
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

            ICaptureRunLockHandle firstHandle;
            bool firstAcquired = _backend.TryAcquire(pathSet.FirstLockPath, out firstHandle);

            if (!firstAcquired)
            {
                if (firstHandle != null)
                {
                    ThrowContractViolation("Lock backend returned false with a non-null handle.", firstHandle);
                }

                return false;
            }

            if (firstHandle == null)
            {
                throw new InvalidOperationException("Lock backend returned true with a null handle.");
            }

            ICaptureRunLockHandle secondHandle;
            bool secondAcquired;
            try
            {
                secondAcquired = _backend.TryAcquire(pathSet.SecondLockPath, out secondHandle);
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(ex);
                try
                {
                    firstHandle.Dispose();
                }
                catch (Exception cleanupEx)
                {
                    throw new AggregateException(
                        "Second lock acquisition failed and first handle cleanup also failed.",
                        new Exception[] { ex, cleanupEx });
                }

                captured.Throw();
                throw;
            }

            if (!secondAcquired)
            {
                if (secondHandle != null)
                {
                    ThrowContractViolation("Lock backend returned false with a non-null handle for the second lock.", secondHandle, firstHandle);
                }

                firstHandle.Dispose();
                return false;
            }

            if (secondHandle == null)
            {
                ThrowContractViolation("Lock backend returned true with a null handle for the second lock.", firstHandle);
            }

            CaptureRunLockLease createdLease;
            try
            {
                createdLease = new CaptureRunLockLease(pathSet, firstHandle, secondHandle);
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(ex);
                List<Exception> cleanupFailures = new List<Exception>();
                DisposeHandles(cleanupFailures, secondHandle, firstHandle);

                if (cleanupFailures.Count > 0)
                {
                    List<Exception> all = new List<Exception> { ex };
                    all.AddRange(cleanupFailures);
                    throw new AggregateException("Lease construction failed and handle cleanup also failed.", all);
                }

                captured.Throw();
                throw;
            }

            lease = createdLease;
            return true;
        }

        private static void ThrowContractViolation(string message, params ICaptureRunLockHandle[] handles)
        {
            InvalidOperationException violation = new InvalidOperationException(message);
            List<Exception> failures = new List<Exception> { violation };
            DisposeHandles(failures, handles);

            if (failures.Count > 1)
            {
                throw new AggregateException(message + " and cleanup failed.", failures);
            }

            throw violation;
        }

        private static void DisposeHandles(List<Exception> failures, params ICaptureRunLockHandle[] handles)
        {
            for (int i = 0; i < handles.Length; i++)
            {
                ICaptureRunLockHandle handle = handles[i];
                if (handle == null)
                {
                    continue;
                }

                bool alreadyHandled = false;
                for (int j = 0; j < i; j++)
                {
                    if (ReferenceEquals(handles[j], handle))
                    {
                        alreadyHandled = true;
                        break;
                    }
                }

                if (alreadyHandled)
                {
                    continue;
                }

                try
                {
                    handle.Dispose();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }
        }
    }
}
