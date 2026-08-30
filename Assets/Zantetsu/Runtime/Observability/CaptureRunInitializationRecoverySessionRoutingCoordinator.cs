using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Routes a completed recovery orchestration result by its terminal status
    /// into either a Run session (start-fresh or initialization-ready) or a
    /// caller-held false outcome (publication recovery or collision), keeping
    /// lease ownership with the caller until a session is actually produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holds exactly one readonly dependency — the start-fresh continuation
    /// coordinator — and is not an <see cref="IDisposable"/>. It never holds a
    /// lock acquisition coordinator, a bootstrap coordinator, an inspector, or
    /// a publication recovery dependency, and it performs no filesystem work.
    /// </para>
    /// <para>
    /// All pre-validation happens before any ID issuance, evidence creation, or
    /// session construction. On the start-fresh and initialization-ready paths
    /// the ownership transfer into the returned session is the linearization
    /// point; a false result leaves the caller's lease reference and ownership
    /// untouched. Exceptions propagate unchanged and the lease is never
    /// released here.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoverySessionRoutingCoordinator
    {
        private readonly CaptureRunInitializationRecoveryStartFreshCoordinator _startFreshCoordinator;

        internal CaptureRunInitializationRecoverySessionRoutingCoordinator(
            CaptureRunInitializationRecoveryStartFreshCoordinator startFreshCoordinator)
        {
            if (startFreshCoordinator == null)
            {
                throw new ArgumentNullException(nameof(startFreshCoordinator));
            }

            _startFreshCoordinator = startFreshCoordinator;
        }

        internal CaptureRunInitializationRecoveryStartFreshCoordinator StartFreshCoordinator => _startFreshCoordinator;

        internal bool TryContinueToSession(
            CaptureRunInitializationRecoveryOrchestrationResult recoveryResult,
            ref CaptureRunLockLease lockLease,
            out CaptureRunInitializationSession session)
        {
            session = null;

            if (recoveryResult == null)
            {
                throw new ArgumentNullException(nameof(recoveryResult));
            }

            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            if (!recoveryResult.IsValid)
            {
                throw new ArgumentException("Recovery orchestration result must be valid.", nameof(recoveryResult));
            }

            if (!lockLease.IsCreated)
            {
                throw new ArgumentException("Lock lease must be created.", nameof(lockLease));
            }

            if (!ReferenceEquals(recoveryResult.LockLease, lockLease))
            {
                throw new ArgumentException("Recovery result lease must be the lease being routed.", nameof(lockLease));
            }

            CaptureRunLockPathSet pathSet = lockLease.PathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock lease must hold a path set.", nameof(lockLease));
            }

            if (!ReferenceEquals(pathSet.RootLayout, recoveryResult.RootLayout))
            {
                throw new ArgumentException("Lock lease and recovery result must share the same root layout.", nameof(recoveryResult));
            }

            if (pathSet.RootLayout.TestRunId != recoveryResult.TestRunId)
            {
                throw new ArgumentException("Lock lease and recovery result must share the same test run ID.", nameof(recoveryResult));
            }

            if (recoveryResult.Status != CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired
                && recoveryResult.Status != CaptureRunInitializationRecoveryExecutionStatus.InitializationReady
                && recoveryResult.Status != CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired
                && recoveryResult.Status != CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision)
            {
                throw new ArgumentException("Recovery orchestration result must carry a defined terminal status.", nameof(recoveryResult));
            }

            switch (recoveryResult.Status)
            {
                case CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired:
                    if (recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.StartFresh
                        && recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh)
                    {
                        throw new ArgumentException("StartFreshRequired status must carry a start-fresh disposition.", nameof(recoveryResult));
                    }

                    session = _startFreshCoordinator.Continue(recoveryResult, ref lockLease);
                    return true;

                case CaptureRunInitializationRecoveryExecutionStatus.InitializationReady:
                    if (recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization
                        && recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers
                        && recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.AlreadyInitialized)
                    {
                        throw new ArgumentException("InitializationReady status must carry an initialization-ready disposition.", nameof(recoveryResult));
                    }

                    CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(recoveryResult);
                    session = CaptureRunInitializationSessionFactory.Create(ref lockLease, evidence);
                    return true;

                case CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired:
                    if (recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery)
                    {
                        throw new ArgumentException("PublicationRecoveryRequired status must carry the publication recovery disposition.", nameof(recoveryResult));
                    }

                    return false;

                case CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision:
                    if (recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.RunRootCollision)
                    {
                        throw new ArgumentException("RunRootCollision status must carry the collision disposition.", nameof(recoveryResult));
                    }

                    return false;

                default:
                    throw new InvalidOperationException("Unreachable terminal status.");
            }
        }
    }
}
