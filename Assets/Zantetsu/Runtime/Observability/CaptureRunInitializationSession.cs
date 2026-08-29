using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Owns a Run's two acquired lock handles for the full duration of the Run
    /// and correlates them with the execution receipt of a fully completed
    /// initialization. Disposal releases the locks in reverse acquisition
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock handles are never exposed; only the path set and the execution
    /// receipt are visible. On successful construction the lease ownership
    /// transfers here and nothing else may dispose it. <see cref="IsCreated"/>
    /// reflects this session's own disposal state, not the lease's transient
    /// state. Disposal is idempotent once fully successful and may be retried
    /// after a partial failure; it never disposes or mutates the execution
    /// receipt.
    /// </para>
    /// <para>
    /// Forwarding properties read straight from the receipt and hold no copied
    /// value. This type performs no filesystem work.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSession : IDisposable
    {
        private readonly CaptureRunLockLease _lockLease;
        private readonly CaptureRunInitializationExecutionReceipt _executionReceipt;
        private bool _disposed;

        internal CaptureRunInitializationSession(
            CaptureRunLockLease lockLease,
            CaptureRunInitializationExecutionReceipt executionReceipt)
        {
            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            if (executionReceipt == null)
            {
                throw new ArgumentNullException(nameof(executionReceipt));
            }

            if (!lockLease.IsCreated)
            {
                throw new ArgumentException("Lock lease must be created.", nameof(lockLease));
            }

            if (!executionReceipt.IsValid)
            {
                throw new ArgumentException("Execution receipt must be valid.", nameof(executionReceipt));
            }

            CaptureRunLockPathSet pathSet = lockLease.PathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock lease must hold a path set.", nameof(lockLease));
            }

            if (!ReferenceEquals(pathSet.RootLayout, executionReceipt.RootLayout))
            {
                throw new ArgumentException("Lock path set and execution receipt must share the same root layout.", nameof(executionReceipt));
            }

            if (pathSet.RootLayout.TestRunId != executionReceipt.TestRunId)
            {
                throw new ArgumentException("Lock path set and execution receipt must share the same test run ID.", nameof(executionReceipt));
            }

            _lockLease = lockLease;
            _executionReceipt = executionReceipt;
        }

        internal CaptureRunInitializationExecutionReceipt ExecutionReceipt => _executionReceipt;

        internal CaptureRunRootLayout RootLayout => _executionReceipt.RootLayout;

        internal long TestRunId => _executionReceipt.TestRunId;

        internal string RunInitializationId => _executionReceipt.RunInitializationId;

        internal CaptureRunLockPathSet LockPathSet => _lockLease.PathSet;

        internal bool IsCreated => !_disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _lockLease.Dispose();
            _disposed = true;
        }
    }
}
