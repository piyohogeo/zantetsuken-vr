using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run CaptureComplete operation: the exact Run
    /// Coordinator and the exact Committed capture index commit receipt, fixed
    /// together for the future CaptureComplete boundary. The capture index
    /// operation, the artifact publication receipt and operation, the plan, the
    /// root layout, and the run identity are forwarded from the held receipt's
    /// graph and are never duplicated as fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type performs no file, hash, or serialization operation, holds no
    /// canonical byte copy, resolved path, or content hash, never advances the
    /// registry, the disposition, or the evidence state, and owns no service,
    /// lease, handle, or buffer. It introduces no new issuance credential or
    /// state marker and performs no cleanup, retry, or recovery classification.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> reuse the Run
    /// Coordinator's existing retained-state correlation for the collected
    /// Committed capture index commit result, which in turn carries the
    /// Published artifact publication result and receipt, the committed plan
    /// commit, the Committed Registry entry, the Finalized context, and the
    /// live Session Ownership Lease. The already-validated graph is never
    /// re-verified by a separate mechanism, no file is re-inspected, and the
    /// published artifact is never re-hashed: the artifact publication receipt
    /// CaptureComplete will use is the same exact reference that publication
    /// issued.
    /// </para>
    /// <para>
    /// The forwarding surface is intentionally narrow. Values reachable through
    /// the forwarded receipts and operations, such as the descriptor, the
    /// finalization result, the frame relation, and the artifact's path,
    /// length, and hash, are not duplicated as properties here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteOperation
    {
        private readonly NvencCaptureRunCoordinator _coordinator;
        private readonly NvencRunCaptureIndexCommitReceipt _captureIndexCommitReceipt;

        internal NvencRunCaptureCompleteOperation(
            NvencCaptureRunCoordinator coordinator,
            NvencRunCaptureIndexCommitReceipt captureIndexCommitReceipt)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _captureIndexCommitReceipt = captureIndexCommitReceipt
                ?? throw new ArgumentNullException(nameof(captureIndexCommitReceipt));
        }

        internal NvencRunCaptureIndexCommitReceipt CaptureIndexCommitReceipt => _captureIndexCommitReceipt;

        internal NvencRunCaptureIndexCommitOperation CaptureIndexCommitOperation =>
            _captureIndexCommitReceipt.Operation;

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt =>
            _captureIndexCommitReceipt.ArtifactPublicationReceipt;

        internal NvencRunArtifactPublicationOperation ArtifactPublicationOperation =>
            _captureIndexCommitReceipt.ArtifactPublicationOperation;

        internal CapturePublicationPlan Plan => _captureIndexCommitReceipt.Plan;

        internal CaptureRunRootLayout RootLayout => _captureIndexCommitReceipt.RootLayout;

        internal long TestRunId => _captureIndexCommitReceipt.TestRunId;

        internal string RunInitializationId => _captureIndexCommitReceipt.RunInitializationId;

        internal bool IsValid =>
            _coordinator != null
            && _captureIndexCommitReceipt != null
            && _coordinator.IsCaptureCompleteReceiptCorrelated(_captureIndexCommitReceipt);

        /// <summary>
        /// Exception-safe post-CaptureComplete issuance binding: the same exact
        /// correlation as <see cref="IsValid"/>, except that the disposition
        /// may be <c>Committed</c> (before the result is reflected),
        /// <see cref="NvencRunEvidenceDisposition.CaptureComplete"/> (after a
        /// Completed result), or
        /// <see cref="NvencRunEvidenceDisposition.PublicationRecoveryRequired"/>
        /// (after a Failed one), and a Poison does not by itself revoke it.
        /// Used by the issued attempt result and receipt so that reflecting the
        /// outcome, or a later Poison, does not stop the same result from being
        /// re-collected. It re-inspects no file, never re-hashes the published
        /// artifact, and introduces no second authority.
        /// </summary>
        internal bool IsBindingIntact =>
            _coordinator != null
            && _captureIndexCommitReceipt != null
            && _coordinator.IsCaptureCompleteBindingIntact(_captureIndexCommitReceipt);

        /// <summary>
        /// Minimal O(1) exact-process-state correlation used by the Publication
        /// Service: true only while this exact operation is the exact retained
        /// CaptureComplete operation of its Run Coordinator and that Run
        /// Coordinator is bound to the exact supplied process state. It
        /// delegates only ReferenceEquals checks to the Run Coordinator and
        /// never exposes the Coordinator or the process state as a property.
        /// </summary>
        internal bool IsBoundToProcessState(NvencCaptureProcessState processState)
        {
            return processState != null
                && _coordinator != null
                && _coordinator.IsCaptureCompleteOperationBoundTo(this, processState);
        }

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }
    }
}
