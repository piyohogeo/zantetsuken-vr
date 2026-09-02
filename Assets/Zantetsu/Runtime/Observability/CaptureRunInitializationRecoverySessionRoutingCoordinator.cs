using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Routes a completed recovery orchestration result by its terminal status
    /// into either a Run session (start-fresh or initialization-ready) or a
    /// caller-held false outcome (publication recovery or collision),
    /// referencing the caller's exact ownership lease and identity evidence
    /// without transferring or releasing them.
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
    /// session issuance. On the start-fresh and initialization-ready paths the
    /// issued issue references the caller's exact ownership lease and identity
    /// evidence; a false result leaves them untouched and they are never
    /// released here. Exceptions propagate unchanged.
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
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            out CaptureRunInitializationSessionIssue issue)
        {
            issue = null;

            if (recoveryResult == null)
            {
                throw new ArgumentNullException(nameof(recoveryResult));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (lockIdentityEvidence == null)
            {
                throw new ArgumentNullException(nameof(lockIdentityEvidence));
            }

            if (!recoveryResult.IsValid)
            {
                throw new ArgumentException("Recovery orchestration result must be valid.", nameof(recoveryResult));
            }

            if (!ownershipLease.IsCreated)
            {
                throw new ArgumentException("Ownership lease must be live.", nameof(ownershipLease));
            }

            if (!lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Lock identity evidence must be valid.", nameof(lockIdentityEvidence));
            }

            if (!lockIdentityEvidence.IsIssuedFor(ownershipLease))
            {
                throw new ArgumentException("Lock identity evidence must be issued for the exact ownership lease.", nameof(lockIdentityEvidence));
            }

            if (!ReferenceEquals(recoveryResult.LockIdentityEvidence, lockIdentityEvidence))
            {
                throw new ArgumentException("Recovery result identity evidence must be the evidence being routed.", nameof(lockIdentityEvidence));
            }

            CaptureRunLockPathSet pathSet = lockIdentityEvidence.LockPathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock identity evidence must hold a path set.", nameof(lockIdentityEvidence));
            }

            if (!ReferenceEquals(pathSet.RootLayout, recoveryResult.RootLayout))
            {
                throw new ArgumentException("Lock identity evidence and recovery result must share the same root layout.", nameof(recoveryResult));
            }

            if (pathSet.RootLayout.TestRunId != recoveryResult.TestRunId)
            {
                throw new ArgumentException("Lock identity evidence and recovery result must share the same test run ID.", nameof(recoveryResult));
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

                    issue = _startFreshCoordinator.Continue(recoveryResult, ownershipLease, lockIdentityEvidence);
                    return true;

                case CaptureRunInitializationRecoveryExecutionStatus.InitializationReady:
                    if (recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization
                        && recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers
                        && recoveryResult.Disposition != CaptureRunInitializationRecoveryDisposition.AlreadyInitialized)
                    {
                        throw new ArgumentException("InitializationReady status must carry an initialization-ready disposition.", nameof(recoveryResult));
                    }

                    CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromRecovery(recoveryResult);
                    issue = CaptureRunInitializationSessionFactory.Create(ownershipLease, lockIdentityEvidence, evidence);
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
