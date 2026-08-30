using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable terminal outcome of an opened Capture Run. The outcome always
    /// owns whatever the lock acquisition produced: a session on the ready
    /// path, or the raw lease on the publication and collision paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction performs the single session routing step and then only
    /// assigns fields; no validation that could fail after the lease transfer
    /// is performed. The outcome never holds both a session and a lease.
    /// <see cref="IsValid"/> recomputes the per-status invariants from the held
    /// values without throwing, so a forged nested value yields <c>false</c>.
    /// </para>
    /// <para>
    /// Disposal releases the owned object exactly once: the session on the
    /// ready path, or the held lease on the publication and collision paths.
    /// The orchestration result is never disposed or mutated. Status, root
    /// layout, test run ID, and run initialization ID remain readable after
    /// disposal.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationOpenOutcome : IDisposable
    {
        private readonly CaptureRunInitializationRecoveryOrchestrationResult _orchestrationResult;
        private readonly CaptureRunInitializationSession _session;
        private readonly CaptureRunLockLease _lockLease;
        private bool _disposed;

        internal CaptureRunInitializationOpenOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult,
            ref CaptureRunLockLease lockLease,
            CaptureRunInitializationRecoverySessionRoutingCoordinator sessionRoutingCoordinator)
        {
            if (orchestrationResult == null)
            {
                throw new ArgumentNullException(nameof(orchestrationResult));
            }

            if (sessionRoutingCoordinator == null)
            {
                throw new ArgumentNullException(nameof(sessionRoutingCoordinator));
            }

            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            CaptureRunInitializationSession session;
            bool sessionReady = sessionRoutingCoordinator.TryContinueToSession(orchestrationResult, ref lockLease, out session);

            _orchestrationResult = orchestrationResult;

            if (sessionReady)
            {
                _session = session;
                _lockLease = null;
            }
            else
            {
                _session = null;
                _lockLease = lockLease;
                lockLease = null;
            }
        }

        internal CaptureRunInitializationOpenStatus Status
        {
            get
            {
                if (_session != null)
                {
                    return CaptureRunInitializationOpenStatus.SessionReady;
                }

                if (_orchestrationResult == null)
                {
                    return CaptureRunInitializationOpenStatus.None;
                }

                switch (_orchestrationResult.Status)
                {
                    case CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired:
                        return CaptureRunInitializationOpenStatus.PublicationRecoveryRequired;

                    case CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision:
                        return CaptureRunInitializationOpenStatus.RunRootCollision;

                    default:
                        return CaptureRunInitializationOpenStatus.None;
                }
            }
        }

        internal CaptureRunInitializationRecoveryOrchestrationResult OrchestrationResult => _orchestrationResult;

        internal CaptureRunInitializationSession Session => _session;

        internal CaptureRunRootLayout RootLayout =>
            _session != null ? _session.RootLayout
            : _orchestrationResult != null ? _orchestrationResult.RootLayout
            : null;

        internal long TestRunId =>
            _session != null ? _session.TestRunId
            : _orchestrationResult != null ? _orchestrationResult.TestRunId
            : 0;

        internal string RunInitializationId =>
            _session != null ? _session.RunInitializationId
            : _orchestrationResult != null ? _orchestrationResult.RunInitializationId
            : null;

        internal CaptureRunLockPathSet LockPathSet =>
            _session != null ? _session.LockPathSet
            : _lockLease != null ? _lockLease.PathSet
            : null;

        internal bool IsCreated => !_disposed;

        internal bool IsValid
        {
            get
            {
                if (_disposed || _orchestrationResult == null || !_orchestrationResult.IsValid)
                {
                    return false;
                }

                if (_session != null)
                {
                    if (_lockLease != null || !_session.IsCreated)
                    {
                        return false;
                    }

                    CaptureRunInitializationRecoveryExecutionStatus status = _orchestrationResult.Status;
                    if (status != CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired
                        && status != CaptureRunInitializationRecoveryExecutionStatus.InitializationReady)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(_session.RootLayout, _orchestrationResult.RootLayout)
                        || _session.TestRunId != _orchestrationResult.TestRunId)
                    {
                        return false;
                    }

                    if (status == CaptureRunInitializationRecoveryExecutionStatus.InitializationReady)
                    {
                        return _session.ExecutionReceipt == null
                            && ReferenceEquals(_session.RecoveryOrchestrationResult, _orchestrationResult);
                    }

                    return _session.RecoveryOrchestrationResult == null
                        && _session.ExecutionReceipt != null;
                }

                if (_lockLease == null || !_lockLease.IsCreated)
                {
                    return false;
                }

                if (!ReferenceEquals(_lockLease, _orchestrationResult.LockLease))
                {
                    return false;
                }

                if (_orchestrationResult.Status == CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired)
                {
                    return _orchestrationResult.Disposition == CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery;
                }

                if (_orchestrationResult.Status == CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision)
                {
                    return _orchestrationResult.Disposition == CaptureRunInitializationRecoveryDisposition.RunRootCollision;
                }

                return false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_session != null)
            {
                _session.Dispose();
                _disposed = true;
                return;
            }

            if (_lockLease != null)
            {
                _lockLease.Dispose();
                _disposed = true;
                return;
            }

            _disposed = true;
        }
    }
}
