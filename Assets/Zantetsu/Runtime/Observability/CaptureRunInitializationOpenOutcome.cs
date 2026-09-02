using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable terminal outcome of an opened Capture Run. The outcome holds
    /// the recovery orchestration result plus, on the ready path, the session
    /// and its lock identity evidence. It holds no lock and cannot release
    /// one; lock ownership lives in the issuing coordinator's ownership lease.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction validates the exact identity evidence, orchestration
    /// result, and session issue before assigning fields, so a forged or
    /// cross-substituted issue never reaches the held graph.
    /// <see cref="IsValid"/> recomputes the per-status invariants from the
    /// held values without throwing. Status, root layout, test run ID, and
    /// run initialization ID remain readable.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationOpenOutcome
    {
        private readonly CaptureRunInitializationRecoveryOrchestrationResult _orchestrationResult;
        private readonly CaptureRunInitializationSessionIssue _sessionIssue;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;

        internal CaptureRunInitializationOpenOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult,
            CaptureRunInitializationSessionIssue sessionIssue,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            if (orchestrationResult == null)
            {
                throw new ArgumentNullException(nameof(orchestrationResult));
            }

            if (!orchestrationResult.IsValid)
            {
                throw new ArgumentException("Orchestration result must be valid.", nameof(orchestrationResult));
            }

            if (lockIdentityEvidence == null)
            {
                throw new ArgumentNullException(nameof(lockIdentityEvidence));
            }

            if (!lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Lock identity evidence must be valid.", nameof(lockIdentityEvidence));
            }

            if (!ReferenceEquals(orchestrationResult.LockIdentityEvidence, lockIdentityEvidence))
            {
                throw new ArgumentException("Open outcome must hold the exact lock identity evidence of its orchestration result.", nameof(lockIdentityEvidence));
            }

            CaptureRunInitializationRecoveryExecutionStatus status = orchestrationResult.Status;

            switch (status)
            {
                case CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired:
                    RequireSessionIssue(sessionIssue, lockIdentityEvidence);
                    RequireFreshSession(sessionIssue.Session);
                    break;

                case CaptureRunInitializationRecoveryExecutionStatus.InitializationReady:
                    RequireSessionIssue(sessionIssue, lockIdentityEvidence);
                    RequireRecoverySession(sessionIssue.Session, orchestrationResult);
                    break;

                case CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired:
                    RequireNoSessionIssue(sessionIssue);
                    if (orchestrationResult.Disposition != CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery)
                    {
                        throw new ArgumentException("Publication-recovery outcome must carry the publication recovery disposition.", nameof(orchestrationResult));
                    }
                    break;

                case CaptureRunInitializationRecoveryExecutionStatus.RunRootCollision:
                    RequireNoSessionIssue(sessionIssue);
                    if (orchestrationResult.Disposition != CaptureRunInitializationRecoveryDisposition.RunRootCollision)
                    {
                        throw new ArgumentException("Collision outcome must carry the collision disposition.", nameof(orchestrationResult));
                    }
                    break;

                default:
                    throw new ArgumentException("Open outcome must carry a defined terminal status.", nameof(orchestrationResult));
            }

            _orchestrationResult = orchestrationResult;
            _lockIdentityEvidence = lockIdentityEvidence;
            _sessionIssue = sessionIssue;
        }

        private static void RequireSessionIssue(
            CaptureRunInitializationSessionIssue sessionIssue,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            if (sessionIssue == null)
            {
                throw new ArgumentException("Ready outcome must hold a session issue.", nameof(sessionIssue));
            }

            if (!sessionIssue.IsValid)
            {
                throw new ArgumentException("Session issue must be valid.", nameof(sessionIssue));
            }

            if (!ReferenceEquals(sessionIssue.LockIdentityEvidence, lockIdentityEvidence))
            {
                throw new ArgumentException("Session issue must be issued for the exact lock identity evidence.", nameof(sessionIssue));
            }
        }

        private static void RequireNoSessionIssue(CaptureRunInitializationSessionIssue sessionIssue)
        {
            if (sessionIssue != null)
            {
                throw new ArgumentException("Recovery-only outcome must not hold a session issue.", nameof(sessionIssue));
            }
        }

        private static void RequireFreshSession(CaptureRunInitializationSession session)
        {
            if (session == null || session.RecoveryOrchestrationResult != null || session.ExecutionReceipt == null)
            {
                throw new ArgumentException("Start-fresh outcome must hold a fresh session with no recovery result.", nameof(session));
            }
        }

        private static void RequireRecoverySession(
            CaptureRunInitializationSession session,
            CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult)
        {
            if (session == null || session.ExecutionReceipt != null || !ReferenceEquals(session.RecoveryOrchestrationResult, orchestrationResult))
            {
                throw new ArgumentException("Initialization-ready outcome must hold the exact recovery session.", nameof(session));
            }
        }

        internal CaptureRunInitializationOpenStatus Status
        {
            get
            {
                if (_sessionIssue != null)
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

        internal CaptureRunInitializationSession Session =>
            _sessionIssue != null ? _sessionIssue.Session : null;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        internal CaptureRunLockPathSet LockPathSet =>
            _lockIdentityEvidence != null ? _lockIdentityEvidence.LockPathSet : null;

        internal CaptureRunRootLayout RootLayout =>
            _sessionIssue != null ? _sessionIssue.Session.RootLayout
            : _orchestrationResult != null ? _orchestrationResult.RootLayout
            : null;

        internal long TestRunId =>
            _sessionIssue != null ? _sessionIssue.Session.TestRunId
            : _orchestrationResult != null ? _orchestrationResult.TestRunId
            : 0;

        internal string RunInitializationId =>
            _sessionIssue != null ? _sessionIssue.Session.RunInitializationId
            : _orchestrationResult != null ? _orchestrationResult.RunInitializationId
            : null;

        internal bool IsValid
        {
            get
            {
                if (_orchestrationResult == null || !_orchestrationResult.IsValid)
                {
                    return false;
                }

                if (_lockIdentityEvidence == null || !_lockIdentityEvidence.IsValid)
                {
                    return false;
                }

                if (!ReferenceEquals(_orchestrationResult.LockIdentityEvidence, _lockIdentityEvidence))
                {
                    return false;
                }

                if (_sessionIssue != null)
                {
                    if (!_sessionIssue.IsValid)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(_sessionIssue.LockIdentityEvidence, _lockIdentityEvidence))
                    {
                        return false;
                    }

                    CaptureRunInitializationSession session = _sessionIssue.Session;
                    if (session == null)
                    {
                        return false;
                    }

                    CaptureRunInitializationRecoveryExecutionStatus status = _orchestrationResult.Status;
                    if (status != CaptureRunInitializationRecoveryExecutionStatus.StartFreshRequired
                        && status != CaptureRunInitializationRecoveryExecutionStatus.InitializationReady)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(session.RootLayout, _orchestrationResult.RootLayout)
                        || session.TestRunId != _orchestrationResult.TestRunId)
                    {
                        return false;
                    }

                    if (status == CaptureRunInitializationRecoveryExecutionStatus.InitializationReady)
                    {
                        return session.ExecutionReceipt == null
                            && ReferenceEquals(session.RecoveryOrchestrationResult, _orchestrationResult);
                    }

                    return session.RecoveryOrchestrationResult == null
                        && session.ExecutionReceipt != null;
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
    }
}
