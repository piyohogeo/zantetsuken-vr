using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The entry point of a Phase 0.11 NVENC publication recovery: it inspects
    /// and classifies one recovered Run's open outcome once, and hands back the
    /// terminal routing coordinator that the branch chosen from that
    /// classification will be driven through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Begin"/> stops at that routing coordinator. Nothing terminal
    /// happens here: no Capture Index commit, no CaptureComplete, no cleanup,
    /// and no release of the Session Ownership Lease. Whoever called it holds
    /// the returned coordinator and drives it, which is what lets a partial
    /// release be retried later through the same branch.
    /// </para>
    /// <para>
    /// What this entry checks itself is only that the open outcome is currently
    /// valid and that its own lock identity evidence was issued for the exact
    /// lease it was handed. That correlation is settled before the inspection,
    /// so a foreign lease never reaches the filesystem. The evidence's
    /// <c>IsIssuedFor</c> is the authority for it: the root layout, Run
    /// identity, and lock path set are not re-derived, the disposition is left
    /// to the routing coordinator, and no new admission type is introduced.
    /// </para>
    /// <para>
    /// The inspection does read files, through the publication recovery
    /// orchestration it was configured with; it writes none here. That
    /// orchestration runs exactly once per call, and an exception from it
    /// propagates by the same reference - not caught, wrapped, or retried, and
    /// never followed by a second inspection in the same call.
    /// </para>
    /// <para>
    /// The type holds only its configured collaborators, keeps no state across
    /// calls - no started latch, history, or cache of an earlier
    /// classification - and adds no dependency bundle, factory interface,
    /// result wrapper, receipt, status, proof, token, nonce, or generation. It
    /// owns no thread, queue, or task, never disposes the lease, and is not an
    /// <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryEntryCoordinator
    {
        private readonly NvencRunPublicationRecoveryOrchestrationCoordinator _publicationRecovery;

        private readonly NvencRunCaptureIndexRecoveryOrchestrationCoordinator _captureIndexRecovery;

        private readonly NvencRunCaptureCompleteRecoveryOrchestrationCoordinator _captureComplete;

        private readonly NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator
            _captureCompleteCleanup;

        private readonly NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
            _captureCompleteReleaseExecution;

        private readonly NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator
            _incompleteCleanup;

        private readonly NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
            _incompleteReleaseExecution;

        private readonly NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator
            _stopReleaseExecution;

        internal NvencRunPublicationRecoveryEntryCoordinator(
            NvencRunPublicationRecoveryOrchestrationCoordinator publicationRecovery,
            NvencRunCaptureIndexRecoveryOrchestrationCoordinator captureIndexRecovery,
            NvencRunCaptureCompleteRecoveryOrchestrationCoordinator captureComplete,
            NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator captureCompleteCleanup,
            NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator
                captureCompleteReleaseExecution,
            NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator incompleteCleanup,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator
                incompleteReleaseExecution,
            NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator stopReleaseExecution)
        {
            _publicationRecovery = publicationRecovery
                ?? throw new ArgumentNullException(nameof(publicationRecovery));
            _captureIndexRecovery = captureIndexRecovery
                ?? throw new ArgumentNullException(nameof(captureIndexRecovery));
            _captureComplete = captureComplete
                ?? throw new ArgumentNullException(nameof(captureComplete));
            _captureCompleteCleanup = captureCompleteCleanup
                ?? throw new ArgumentNullException(nameof(captureCompleteCleanup));
            _captureCompleteReleaseExecution = captureCompleteReleaseExecution
                ?? throw new ArgumentNullException(nameof(captureCompleteReleaseExecution));
            _incompleteCleanup = incompleteCleanup
                ?? throw new ArgumentNullException(nameof(incompleteCleanup));
            _incompleteReleaseExecution = incompleteReleaseExecution
                ?? throw new ArgumentNullException(nameof(incompleteReleaseExecution));
            _stopReleaseExecution = stopReleaseExecution
                ?? throw new ArgumentNullException(nameof(stopReleaseExecution));
        }

        internal NvencRunPublicationRecoveryOrchestrationCoordinator PublicationRecovery =>
            _publicationRecovery;

        /// <summary>
        /// Inspects and classifies this Run once, and returns the routing
        /// coordinator for the branch that classification selects. Nothing
        /// terminal is executed.
        /// </summary>
        internal NvencRunPublicationRecoveryTerminalRoutingCoordinator Begin(
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (openOutcome == null)
            {
                throw new ArgumentNullException(nameof(openOutcome));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (!openOutcome.IsValid)
            {
                throw new ArgumentException(
                    "Initialization open outcome must be valid.", nameof(openOutcome));
            }

            // Settled before the inspection: the lock identity evidence this
            // outcome carries answers whether that exact lease is this Run's
            // current lock holder, and nothing of it is re-derived here.
            if (openOutcome.LockIdentityEvidence?.IsIssuedFor(ownershipLease) != true)
            {
                throw new ArgumentException(
                    "The ownership lease must be the one this Run's lock identity evidence was issued for.",
                    nameof(ownershipLease));
            }

            // One inspection and one classification, whose exceptions belong to
            // the caller exactly as they were thrown.
            NvencRunPublicationRecoveryDecision decision =
                _publicationRecovery.Execute(openOutcome);

            // The disposition is the routing coordinator's to read.
            return new NvencRunPublicationRecoveryTerminalRoutingCoordinator(
                decision,
                ownershipLease,
                _captureIndexRecovery,
                _captureComplete,
                _captureCompleteCleanup,
                _captureCompleteReleaseExecution,
                _incompleteCleanup,
                _incompleteReleaseExecution,
                _stopReleaseExecution);
        }
    }
}
