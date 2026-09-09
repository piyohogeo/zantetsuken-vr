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

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }
    }
}
