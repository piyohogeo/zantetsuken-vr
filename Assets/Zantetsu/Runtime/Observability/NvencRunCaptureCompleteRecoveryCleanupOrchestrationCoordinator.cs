using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Runs the Phase 0.11 NVENC recovery CaptureComplete cleanup exactly once
    /// for one accepted CaptureComplete: it issues the cleanup operation from
    /// that receipt and returns the attempt result the configured execution
    /// coordinator produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds exactly one readonly dependency, the cleanup execution
    /// coordinator, and is not an <see cref="IDisposable"/>. It owns no thread,
    /// queue, task, lease, filesystem, or buffer, keeps no operation, receipt,
    /// or result in fields, and adds no result wrapper, status, receipt,
    /// issuer, proof, token, nonce, generation, or latch of its own.
    /// </para>
    /// <para>
    /// Only a null receipt is rejected here. The admission of the receipt and
    /// every correlation of operation, decision, snapshot, plan, root layout,
    /// Run identity, and optional commit receipt belong to the existing
    /// operation factory and execution coordinator, and are not restated.
    /// </para>
    /// <para>
    /// Cleaned and Failed are both ordinary terminal results and are returned
    /// as they are: a failure is never turned into an exception or another
    /// status, and a success is never re-wrapped in a new receipt. Exceptions
    /// from the operation issuance, the cleaner, and the execution coordinator
    /// propagate by the same reference, and nothing is retried, rolled back,
    /// re-inspected, re-committed, re-completed, cleaned up again, or guessed.
    /// </para>
    /// <para>
    /// The boundary stops at that result. It changes no process state, Poison,
    /// Registry, or disposition, releases no OS lock or ownership lease,
    /// re-checks no file, re-runs no earlier recovery stage, and publishes no
    /// Run completion.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator
    {
        private readonly NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator
            _cleanupExecution;

        internal NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
            NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator cleanupExecution)
        {
            _cleanupExecution = cleanupExecution
                ?? throw new ArgumentNullException(nameof(cleanupExecution));
        }

        internal NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator CleanupExecution =>
            _cleanupExecution;

        internal NvencRunCaptureCompleteRecoveryCleanupAttemptResult Execute(
            NvencRunCaptureCompleteRecoveryReceipt receipt)
        {
            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            // Issuance is the whole admission check, and it has no side effect.
            NvencRunCaptureCompleteRecoveryCleanupOperation operation =
                NvencRunCaptureCompleteRecoveryCleanupOperation.Create(receipt);

            // Exactly one cleanup attempt, and its result as it stands.
            return _cleanupExecution.Execute(operation);
        }
    }
}
