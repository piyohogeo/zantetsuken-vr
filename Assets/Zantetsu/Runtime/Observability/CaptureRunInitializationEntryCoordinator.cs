using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The regular entry point for opening a Capture Run: acquire the two OS
    /// locks, inspect from scratch, route the recovery result into a session or
    /// a caller-held outcome, and hand back an outcome that always owns the
    /// acquired lease.
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
    /// Lock contention returns false with no outcome and no inspection. After
    /// the locks are acquired, any failure before the outcome is complete
    /// releases the lease in reverse order, rethrows the original exception
    /// with its original stack, and reports a cleanup failure as an
    /// <see cref="AggregateException"/> with the original exception first. The
    /// outcome construction is the last success operation; no validation
    /// follows the ownership transfer.
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
            out CaptureRunInitializationOpenOutcome outcome)
        {
            outcome = null;

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

                CaptureRunInitializationRecoveryInspectionOperation operation = new CaptureRunInitializationRecoveryInspectionOperation(rootLayout, lease, maximumRootEntryCount);
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

                if (!ReferenceEquals(result.LockLease, lease))
                {
                    throw new InvalidOperationException("Orchestration result lease does not match the held lease.");
                }

                if (!ReferenceEquals(result.RootLayout, rootLayout))
                {
                    throw new InvalidOperationException("Orchestration result root layout does not match the input.");
                }

                CaptureRunInitializationOpenOutcome created = new CaptureRunInitializationOpenOutcome(result, ref lease, _sessionRoutingCoordinator);

                lease = null;
                outcome = created;
                return true;
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo captured = ExceptionDispatchInfo.Capture(ex);

                if (lease != null)
                {
                    try
                    {
                        lease.Dispose();
                    }
                    catch (Exception cleanupEx)
                    {
                        throw new AggregateException(
                            "Opening the Capture Run failed and lock lease cleanup also failed.",
                            new Exception[] { ex, cleanupEx });
                    }
                }

                captured.Throw();
                throw;
            }
        }
    }
}
