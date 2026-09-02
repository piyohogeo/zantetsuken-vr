using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The regular entry point for opening a Capture Run: acquire the two OS
    /// locks, inspect from scratch, route the recovery result into a session or
    /// a caller-held outcome, and return a non-owning open outcome plus the
    /// ownership lease that owns the acquired lock via separate out
    /// parameters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Holds exactly three readonly dependencies — the lock acquisition
    /// coordinator, the recovery orchestration coordinator, and the recovery
    /// session routing coordinator — and is not an <see cref="IDisposable"/>.
    /// It never calls a bootstrap coordinator, issues an initialization ID
    /// itself, touches a frame ID, draft, or publication recovery body, and
    /// performs no filesystem work.
    /// </para>
    /// <para>
    /// Lock contention returns false with no outcome, no owner, and no
    /// inspection. After the locks are acquired, any failure before both out
    /// parameters are assigned releases the ownership lease in reverse order,
    /// rethrows the original exception with its original stack, and reports a
    /// cleanup failure as an <see cref="AggregateException"/> with the
    /// original exception first. The outcome and ownership lease out
    /// parameters are assigned only after every validation succeeds, so a
    /// thrown or false call never exposes a partially built owner.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationEntryCoordinator
    {
        private readonly CaptureRunLockAcquisitionCoordinator _lockCoordinator;
        private readonly CaptureRunInitializationRecoveryOrchestrationCoordinator _orchestrationCoordinator;
        private readonly CaptureRunInitializationRecoverySessionRoutingCoordinator _sessionRoutingCoordinator;

        internal CaptureRunInitializationEntryCoordinator(
            CaptureRunLockAcquisitionCoordinator lockCoordinator,
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrationCoordinator,
            CaptureRunInitializationRecoverySessionRoutingCoordinator sessionRoutingCoordinator)
        {
            if (lockCoordinator == null)
            {
                throw new ArgumentNullException(nameof(lockCoordinator));
            }

            if (orchestrationCoordinator == null)
            {
                throw new ArgumentNullException(nameof(orchestrationCoordinator));
            }

            if (sessionRoutingCoordinator == null)
            {
                throw new ArgumentNullException(nameof(sessionRoutingCoordinator));
            }

            _lockCoordinator = lockCoordinator;
            _orchestrationCoordinator = orchestrationCoordinator;
            _sessionRoutingCoordinator = sessionRoutingCoordinator;
        }

        internal CaptureRunLockAcquisitionCoordinator LockCoordinator => _lockCoordinator;

        internal CaptureRunInitializationRecoveryOrchestrationCoordinator OrchestrationCoordinator => _orchestrationCoordinator;

        internal CaptureRunInitializationRecoverySessionRoutingCoordinator SessionRoutingCoordinator => _sessionRoutingCoordinator;

        internal bool TryOpen(
            CaptureRunRootLayout rootLayout,
            int maximumRootEntryCount,
            out CaptureRunInitializationOpenOutcome outcome,
            out CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            outcome = null;
            ownershipLease = null;

            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            if (maximumRootEntryCount < 1
                || maximumRootEntryCount > CaptureRunInitializationRecoveryInspectionOperation.MaximumAllowedRootEntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRootEntryCount), maximumRootEntryCount,
                    "Maximum root entry count must be between 1 and " + CaptureRunInitializationRecoveryInspectionOperation.MaximumAllowedRootEntryCount + ".");
            }

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(rootLayout);

            CaptureRunLockLease lease;
            bool acquired = _lockCoordinator.TryAcquire(pathSet, out lease);

            if (!acquired)
            {
                return false;
            }

            CaptureRunInitializationSessionOwnershipLease heldOwnershipLease = null;

            try
            {
                if (lease == null)
                {
                    throw new InvalidOperationException("Lock acquisition returned true with a null lease.");
                }

                if (!lease.IsCreated)
                {
                    throw new InvalidOperationException("Lock acquisition returned an uncreated lease.");
                }

                if (!ReferenceEquals(lease.PathSet, pathSet))
                {
                    throw new InvalidOperationException("Lock lease path set does not match the requested path set.");
                }

                heldOwnershipLease = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
                CaptureRunLockIdentityEvidence lockIdentityEvidence =
                    CaptureRunLockIdentityEvidence.Create(heldOwnershipLease, heldOwnershipLease.LockPathSet);

                CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(rootLayout, lockIdentityEvidence, maximumRootEntryCount);
                CaptureRunInitializationRecoveryOrchestrationResult result = _orchestrationCoordinator.Execute(operation);

                if (result == null)
                {
                    throw new InvalidOperationException("Orchestration returned no result.");
                }

                if (!result.IsValid)
                {
                    throw new InvalidOperationException("Orchestration returned an invalid result.");
                }

                if (!ReferenceEquals(result.Snapshot.Operation, operation))
                {
                    throw new InvalidOperationException("Orchestration result operation does not match the inspection.");
                }

                if (!ReferenceEquals(result.LockIdentityEvidence, lockIdentityEvidence))
                {
                    throw new InvalidOperationException("Orchestration result identity evidence does not match the held evidence.");
                }

                if (!ReferenceEquals(result.RootLayout, rootLayout))
                {
                    throw new InvalidOperationException("Orchestration result root layout does not match the input.");
                }

                CaptureRunInitializationSessionIssue issue;
                bool sessionReady = _sessionRoutingCoordinator.TryContinueToSession(result, heldOwnershipLease, lockIdentityEvidence, out issue);

                CaptureRunInitializationOpenOutcome created = new CaptureRunInitializationOpenOutcome(
                    result,
                    sessionReady ? issue : null,
                    lockIdentityEvidence);

                outcome = created;
                ownershipLease = heldOwnershipLease;
                heldOwnershipLease = null;
                return true;
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(ex);

                if (heldOwnershipLease != null)
                {
                    try
                    {
                        heldOwnershipLease.Dispose();
                    }
                    catch (Exception cleanupEx)
                    {
                        throw new AggregateException(
                            "Opening the Capture Run failed and lock ownership release also failed.",
                            new Exception[] { ex, cleanupEx });
                    }
                }

                captured.Throw();
                throw;
            }
        }
    }
}
