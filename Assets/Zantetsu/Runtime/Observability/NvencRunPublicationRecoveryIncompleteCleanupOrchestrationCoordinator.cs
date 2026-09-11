using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Runs the Phase 0.11 NVENC publication recovery orphan cleanup exactly
    /// once for one Run classified incomplete: it issues the cleanup operation
    /// from that classification and the Run's Session Ownership Lease, and
    /// returns the attempt result the configured execution coordinator
    /// produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds exactly one readonly dependency, the cleanup execution
    /// coordinator, and is not an <see cref="IDisposable"/>. It owns no thread,
    /// queue, task, wait primitive, lease, filesystem, buffer, or handle, keeps
    /// no operation, decision, lease, result, or receipt in fields, and adds no
    /// result wrapper, status, receipt, issuer, proof, token, nonce,
    /// generation, or retry latch of its own.
    /// </para>
    /// <para>
    /// Only the two null arguments are rejected here. That the disposition is
    /// incomplete, that the decision is currently valid, and every correlation
    /// of open outcome, lock identity evidence, root layout, Run identity, and
    /// the lease's own retention belong to the existing operation factory,
    /// which is the sole authority and is not restated. The result's cleaner,
    /// operation, receipt, and status shape belong to the existing execution
    /// coordinator in the same way.
    /// </para>
    /// <para>
    /// Cleaned and Failed are both ordinary terminal results and are returned
    /// exactly as they stand: a failure is never turned into an exception or
    /// another status, a success is never re-wrapped, and neither is branched
    /// on here. Exceptions from the operation issuance, the cleaner, and the
    /// execution coordinator propagate by the same reference, and nothing is
    /// retried, re-issued, rolled back, re-classified, or guessed.
    /// </para>
    /// <para>
    /// This coordinator performs no filesystem operation of its own: it
    /// delegates the whole cleanup once to the configured cleanup execution,
    /// and performs no further inspection of the filesystem after it returns.
    /// It changes no process state, Poison, Registry, disposition, or Service,
    /// publishes no Run completion, and releases neither the OS lock nor the
    /// Session Ownership Lease - the release that follows a finished cleanup is
    /// a separate authority.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator
    {
        private readonly NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator
            _cleanupExecution;

        internal NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
            NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator cleanupExecution)
        {
            _cleanupExecution = cleanupExecution
                ?? throw new ArgumentNullException(nameof(cleanupExecution));
        }

        internal NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator
            CleanupExecution => _cleanupExecution;

        internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Execute(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            // Issuance is the whole admission check, and it has no side effect.
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation =
                NvencRunPublicationRecoveryIncompleteCleanupOperation.Create(
                    decision, ownershipLease);

            // Exactly one cleanup attempt, and its result as it stands.
            return _cleanupExecution.Execute(operation);
        }
    }
}
