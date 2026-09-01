using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Owns a Run's two acquired lock handles for the full duration of the Run
    /// and correlates them with the ready evidence of a fully completed
    /// initialization, whether it came from the fresh path or the recovery
    /// path. Disposal releases the locks in reverse acquisition order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lock handles are never exposed; only the path set and the ready
    /// evidence are visible. On successful construction the lease ownership
    /// transfers here and nothing else may dispose it. <see cref="IsCreated"/>
    /// reflects this session's own disposal state, not the lease's transient
    /// state. Disposal is idempotent once fully successful and may be retried
    /// after a partial failure; it never disposes or mutates the evidence, the
    /// execution receipt, or the recovery result.
    /// </para>
    /// <para>
    /// Forwarding properties read straight from the evidence and hold no
    /// copied value. This type performs no filesystem work.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSession : IDisposable
    {
        private readonly CaptureRunLockLease _lockLease;
        private readonly CaptureRunInitializationReadyEvidence _readyEvidence;
        private bool _disposed;

        // Compatibility path: a fresh execution receipt becomes ready evidence.
        internal CaptureRunInitializationSession(
            CaptureRunLockLease lockLease,
            CaptureRunInitializationExecutionReceipt executionReceipt)
            : this(lockLease, ToFreshEvidence(lockLease, executionReceipt))
        {
        }

        internal CaptureRunInitializationSession(
            CaptureRunLockLease lockLease,
            CaptureRunInitializationReadyEvidence readyEvidence)
        {
            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            if (readyEvidence == null)
            {
                throw new ArgumentNullException(nameof(readyEvidence));
            }

            if (!lockLease.IsCreated)
            {
                throw new ArgumentException("Lock lease must be created.", nameof(lockLease));
            }

            if (!readyEvidence.IsValid)
            {
                throw new ArgumentException("Ready evidence must be valid.", nameof(readyEvidence));
            }

            CaptureRunLockPathSet pathSet = lockLease.PathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock lease must hold a path set.", nameof(lockLease));
            }

            if (!ReferenceEquals(pathSet.RootLayout, readyEvidence.RootLayout))
            {
                throw new ArgumentException("Lock path set and ready evidence must share the same root layout.", nameof(readyEvidence));
            }

            if (pathSet.RootLayout.TestRunId != readyEvidence.TestRunId)
            {
                throw new ArgumentException("Lock path set and ready evidence must share the same test run ID.", nameof(readyEvidence));
            }

            if (readyEvidence.IsRecovery)
            {
                CaptureRunLockLease evidenceLease = readyEvidence.RecoveryOrchestrationResult.LockLease;
                if (!ReferenceEquals(evidenceLease, lockLease))
                {
                    throw new ArgumentException("Recovery ready evidence must share the session lock lease.", nameof(lockLease));
                }
            }

            _lockLease = lockLease;
            _readyEvidence = readyEvidence;
        }

        internal CaptureRunInitializationReadyEvidence ReadyEvidence => _readyEvidence;

        internal CaptureRunInitializationExecutionReceipt ExecutionReceipt => _readyEvidence.FreshExecutionReceipt;

        internal CaptureRunInitializationRecoveryOrchestrationResult RecoveryOrchestrationResult => _readyEvidence.RecoveryOrchestrationResult;

        internal CaptureRunRootLayout RootLayout => _readyEvidence.RootLayout;

        internal long TestRunId => _readyEvidence.TestRunId;

        internal string RunInitializationId => _readyEvidence.RunInitializationId;

        internal CaptureRunLockPathSet LockPathSet => _lockLease.PathSet;

        internal bool IsCreated => !_disposed;

        /// <summary>
        /// Exception-safe ownership check: reports whether this live session
        /// still owns exactly the given lock lease, without exposing the lease
        /// itself. Never throws.
        /// </summary>
        internal bool OwnsLockLease(CaptureRunLockLease lockLease)
        {
            if (_disposed || _lockLease == null || lockLease == null)
            {
                return false;
            }

            if (!ReferenceEquals(_lockLease, lockLease))
            {
                return false;
            }

            if (!lockLease.IsCreated)
            {
                return false;
            }

            CaptureRunLockPathSet leasePathSet = lockLease.PathSet;
            CaptureRunLockPathSet sessionPathSet = _lockLease.PathSet;
            return leasePathSet != null
                && sessionPathSet != null
                && ReferenceEquals(leasePathSet, sessionPathSet);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _lockLease.Dispose();
            _disposed = true;
        }

        private static CaptureRunInitializationReadyEvidence ToFreshEvidence(
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

            return CaptureRunInitializationReadyEvidence.FromFresh(executionReceipt);
        }
    }
}
