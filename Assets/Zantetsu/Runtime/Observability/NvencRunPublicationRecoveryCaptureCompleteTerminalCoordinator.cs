using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Brings one recoverable Run to the common terminal value: it takes the
    /// Capture Index recovery and the CaptureComplete once, the staging cleanup
    /// once, and then carries the ownership release receipt as a
    /// <see cref="NvencRunPublicationRecoveryTerminalResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each stage with a side effect happens at most once. The CaptureComplete
    /// stage - the Capture Index recovery inspection, its commit when one is
    /// required, and the CaptureComplete itself - runs while no receipt is
    /// retained; the cleanup runs while no release coordinator exists; and from
    /// then on every call goes through that one release coordinator alone, so a
    /// release retried after a partial failure never re-commits an index or
    /// re-runs a cleanup that has already deleted something.
    /// </para>
    /// <para>
    /// The two "started" flags exist only to refuse a blind retry of a stage
    /// that threw. An exception can arrive after a committed index or after a
    /// partial cleanup, so what such a stage left on disk is unknown, and a
    /// later call stops with an <see cref="InvalidOperationException"/> rather
    /// than repeating it or inferring an outcome from the decision. Those flags
    /// record that a stage was entered - never success, a commit, filesystem
    /// state, or cleanup progress - and are not published as any of those.
    /// </para>
    /// <para>
    /// Completion is the held terminal result's own validity, assigned only
    /// after the terminal factory has returned. A partial release - the
    /// disposal threw with the lease still releasable - leaves this coordinator
    /// unfinished, and the next call is another release attempt on that same
    /// retained operation. A call that fully released the ownership lease but
    /// produced an unusable receipt also leaves it unfinished, with nothing
    /// left to retry: the exception propagates, nothing is retained, and later
    /// calls stop at the retention coordinator's own admission, because
    /// neither the lease's state nor the decision is read as success.
    /// </para>
    /// <para>
    /// A cleaned and a failed cleanup both lead to the same release, with the
    /// status carried by the cleanup result the release operation holds rather
    /// than converted into a success or an exception. Whether the Capture Index
    /// had to be committed or was already authoritative is likewise not decided
    /// here: that branch belongs to the existing CaptureComplete recovery
    /// orchestration and is not restated.
    /// </para>
    /// <para>
    /// What this boundary handles is its own terminal result's validity and the
    /// results of the orchestrations it was configured with. It performs no
    /// publication recovery inspection or classification, selects no
    /// disposition, touches no file itself, re-inspects and re-commits nothing,
    /// changes no process state, Poison, Registry, disposition, or Service,
    /// owns no thread, queue, task, wait, or deadline, never disposes the lease
    /// itself, and adds no receipt, attempt result, status, proof, token,
    /// nonce, or generation. It is not an <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator
    {
        private readonly NvencRunPublicationRecoveryDecision _decision;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private readonly NvencRunCaptureIndexRecoveryOrchestrationCoordinator _captureIndexRecovery;

        private readonly NvencRunCaptureCompleteRecoveryOrchestrationCoordinator _captureComplete;

        private readonly NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator _cleanup;

        private readonly NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
            _releaseExecution;

        private bool _captureCompleteStarted;

        private NvencRunCaptureCompleteRecoveryReceipt _captureCompleteReceipt;

        private bool _cleanupStarted;

        private NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator _releaseCoordinator;

        private NvencRunPublicationRecoveryTerminalResult _terminalResult;

        internal NvencRunPublicationRecoveryCaptureCompleteTerminalCoordinator(
            NvencRunPublicationRecoveryDecision decision,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            NvencRunCaptureIndexRecoveryOrchestrationCoordinator captureIndexRecovery,
            NvencRunCaptureCompleteRecoveryOrchestrationCoordinator captureComplete,
            NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator cleanup,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator releaseExecution)
        {
            // The recoverable disposition, the authoritative plan, the decision
            // graph, and the lease correlation are each stage's own authority,
            // at the moment that stage runs.
            _decision = decision ?? throw new ArgumentNullException(nameof(decision));
            _ownershipLease = ownershipLease
                ?? throw new ArgumentNullException(nameof(ownershipLease));
            _captureIndexRecovery = captureIndexRecovery
                ?? throw new ArgumentNullException(nameof(captureIndexRecovery));
            _captureComplete = captureComplete
                ?? throw new ArgumentNullException(nameof(captureComplete));
            _cleanup = cleanup ?? throw new ArgumentNullException(nameof(cleanup));
            _releaseExecution = releaseExecution
                ?? throw new ArgumentNullException(nameof(releaseExecution));
        }

        internal NvencRunPublicationRecoveryDecision Decision => _decision;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal NvencRunCaptureCompleteRecoveryReceipt CaptureCompleteReceipt =>
            _captureCompleteReceipt;

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator ReleaseCoordinator =>
            _releaseCoordinator;

        /// <summary>
        /// The cleanup attempt this Run's release was issued from, read back
        /// through that release operation rather than kept in a field of its
        /// own. It is the uninitialized default until the release coordinator
        /// exists.
        /// </summary>
        internal NvencRunCaptureCompleteRecoveryCleanupAttemptResult CleanupResult =>
            _releaseCoordinator != null
                ? _releaseCoordinator.Operation.CleanupResult
                : default;

        internal NvencRunPublicationRecoveryTerminalResult TerminalResult => _terminalResult;

        /// <summary>
        /// Whether the CaptureComplete has been accepted and its receipt is
        /// retained.
        /// </summary>
        internal bool IsCaptureCompletePrepared => _captureCompleteReceipt != null;

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
                // Already terminal: the same value, without entering any stage
                // again.
                return _terminalResult;
            }

            if (_captureCompleteReceipt == null)
            {
                if (_captureCompleteStarted)
                {
                    // That stage may have committed the Capture Index before it
                    // threw, so what is on disk now is unknown; running it
                    // again here is exactly what must not happen.
                    throw new InvalidOperationException(
                        "The CaptureComplete recovery was already started and did not complete; it is not run again here.");
                }

                // Set before the call, so an exception cannot be followed by a
                // second attempt.
                _captureCompleteStarted = true;

                // The Capture Index recovery decides what the CaptureComplete
                // needs; whether that means a commit first is the existing
                // orchestration's branch, not one repeated here.
                NvencRunCaptureIndexRecoveryDecision captureIndexRecovery =
                    _captureIndexRecovery.Execute(_decision);

                _captureCompleteReceipt = _captureComplete.Execute(captureIndexRecovery);
            }

            if (_releaseCoordinator == null)
            {
                if (_cleanupStarted)
                {
                    // A cleanup was started and did not hand back a result, so
                    // what it deleted is unknown.
                    throw new InvalidOperationException(
                        "The CaptureComplete cleanup was already started and did not complete; it is not run again here.");
                }

                _cleanupStarted = true;

                NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanupResult =
                    _cleanup.Execute(_captureCompleteReceipt);

                NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator releaseCoordinator =
                    new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                        cleanupResult, _ownershipLease, _releaseExecution);

                // Retained only after a constructed coordinator, so a refused
                // release operation leaves nothing half-prepared.
                _releaseCoordinator = releaseCoordinator;
            }

            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                _releaseCoordinator.Release();

            NvencRunPublicationRecoveryTerminalResult terminal =
                NvencRunPublicationRecoveryTerminalResult.CaptureCompleted(receipt);

            // Assigned only after a returned factory. A partial release leaves
            // the retry to the next call through that same release
            // coordinator; an unusable receipt after a completed release leaves
            // this unfinished with nothing left to retry. Neither re-runs the
            // CaptureComplete or the cleanup.
            _terminalResult = terminal;
            return _terminalResult;
        }
    }
}
