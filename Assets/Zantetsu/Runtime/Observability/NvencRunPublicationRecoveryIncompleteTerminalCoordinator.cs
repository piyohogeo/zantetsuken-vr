using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Brings one incomplete Run to the common terminal value: it runs the
    /// orphan cleanup once, issues the release retention coordinator from that
    /// cleanup's terminal result, and carries the resulting receipt as a
    /// <see cref="NvencRunPublicationRecoveryTerminalResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cleanup happens at most once. Once its release retention
    /// coordinator exists, every later call goes through that one coordinator
    /// alone - the cleanup orchestration is never entered again - so a release
    /// that has to be retried never re-runs a cleanup that already discarded
    /// part of the Run. If the cleanup orchestration itself throws, the
    /// exception propagates and this coordinator refuses to start a second
    /// cleanup: a later call stops with an
    /// <see cref="InvalidOperationException"/> instead of repeating work whose
    /// effect on disk is unknown. The flag behind that refusal records only
    /// that a cleanup was started; it is evidence of neither success nor any
    /// filesystem progress, and is not published as such.
    /// </para>
    /// <para>
    /// The held terminal result's own validity is the completion latch: no
    /// separate completion flag or status, and no copy of the cleanup result,
    /// the receipt, the operation, or the lease. It is assigned only after the
    /// terminal factory has returned, and the two ways a release can fail are
    /// not the same. A partial release - the disposal threw with the lease
    /// still releasable - leaves this coordinator unfinished, and the next call
    /// is another release attempt on that same retained operation. A call that
    /// fully released the ownership lease but produced an unusable receipt also
    /// leaves it unfinished, but there is nothing left to retry: the exception
    /// propagates, nothing is retained, and every later call stops at the
    /// retention coordinator's own admission, because neither the lease's state
    /// nor the decision is read as success.
    /// </para>
    /// <para>
    /// A cleaned and a failed cleanup lead to the same release: the status is
    /// carried by the cleanup result the release operation holds and is never
    /// converted into a success or branched on here.
    /// </para>
    /// <para>
    /// What this boundary handles is its own terminal result's validity, the
    /// cleanup orchestration's result, and the release coordinator's result.
    /// It performs no inspection, classification, or reclassification, selects
    /// no disposition, touches no file itself, runs no Capture Index or
    /// CaptureComplete work, changes no process state, Poison, Registry,
    /// disposition, or Service, owns no thread, queue, task, wait, or deadline,
    /// never disposes the lease itself, and adds no receipt, attempt result,
    /// status, proof, token, nonce, or generation. It is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteTerminalCoordinator
    {
        private readonly NvencRunPublicationRecoveryDecision _decision;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private readonly NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator
            _cleanup;

        private readonly NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
            _releaseExecution;

        private bool _cleanupStarted;

        private NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator
            _releaseCoordinator;

        private NvencRunPublicationRecoveryTerminalResult _terminalResult;

        internal NvencRunPublicationRecoveryIncompleteTerminalCoordinator(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator cleanup,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
                releaseExecution)
        {
            // The incomplete disposition, the decision graph, and the lease
            // correlation are the cleanup orchestration's and the operation
            // factories' to judge, at the moment they are used.
            _decision = decision ?? throw new ArgumentNullException(nameof(decision));
            _ownershipLease = ownershipLease
                ?? throw new ArgumentNullException(nameof(ownershipLease));
            _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
            _releaseExecution = releaseExecution
                ?? throw new ArgumentNullException(nameof(releaseExecution));
        }

        internal NvencRunPublicationRecoveryDecision Decision => _decision;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator
            ReleaseCoordinator => _releaseCoordinator;

        /// <summary>
        /// The cleanup attempt this Run's release was issued from, read back
        /// through that release operation rather than kept in a field of its
        /// own. It is the uninitialized default until the release coordinator
        /// exists.
        /// </summary>
        internal NvencRunPublicationRecoveryIncompleteCleanupAttemptResult CleanupResult =>
            _releaseCoordinator != null
                ? _releaseCoordinator.Operation.CleanupResult
                : default;

        internal NvencRunPublicationRecoveryTerminalResult TerminalResult => _terminalResult;

        /// <summary>
        /// Whether the cleanup has run and its release coordinator exists. This
        /// says nothing about whether that cleanup cleaned or failed.
        /// </summary>
        internal bool IsCleanupPrepared => _releaseCoordinator != null;

        /// <summary>
        /// Whether this Run has reached its terminal value, derived from that
        /// value's own validity rather than from a separate flag.
        /// </summary>
        internal bool IsComplete => _terminalResult.IsValid;

        internal NvencRunPublicationRecoveryTerminalResult Execute()
        {
            if (_terminalResult.IsValid)
            {
                // Already terminal: the same value, without entering the
                // cleanup orchestration or the release coordinator again.
                return _terminalResult;
            }

            if (_releaseCoordinator == null)
            {
                if (_cleanupStarted)
                {
                    // A cleanup was started and did not hand back a result, so
                    // what it did on disk is unknown; running it again here is
                    // exactly what must not happen.
                    throw new InvalidOperationException(
                        "The orphan cleanup was already started and did not complete; it is not run again here.");
                }

                // Set before the call, so an exception cannot be followed by a
                // second cleanup.
                _cleanupStarted = true;

                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanupResult =
                    _cleanup.Execute(_decision, _ownershipLease);

                NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator
                    releaseCoordinator =
                        new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                            _releaseExecution, cleanupResult, _ownershipLease);

                // Retained only after a constructed coordinator, so a refused
                // release operation leaves nothing half-prepared.
                _releaseCoordinator = releaseCoordinator;
            }

            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                _releaseCoordinator.Release();

            NvencRunPublicationRecoveryTerminalResult terminal =
                NvencRunPublicationRecoveryTerminalResult.IncompleteReleased(receipt);

            // Assigned only after a returned factory. A partial release leaves
            // the retry to the next call through that same release
            // coordinator; an unusable receipt after a completed release leaves
            // this unfinished with nothing left to retry. Neither re-runs the
            // cleanup.
            _terminalResult = terminal;
            return _terminalResult;
        }
    }
}
