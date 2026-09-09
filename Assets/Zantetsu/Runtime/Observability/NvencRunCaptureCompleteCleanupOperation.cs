using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run CaptureComplete cleanup operation: the
    /// exact Run Coordinator and the exact Completed CaptureComplete receipt,
    /// fixed together for the future cleanup boundary. The CaptureComplete
    /// operation, the capture index and artifact publication receipts, the
    /// plan, the root layout, and the run identity are forwarded from the held
    /// receipt's graph and are never duplicated as fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type performs no file, hash, or serialization operation, deletes
    /// nothing, releases no lease, holds no canonical byte copy, resolved path,
    /// or content hash, never advances the registry, the disposition, or the
    /// evidence state, and owns no service, lease, handle, or buffer. It
    /// introduces no new issuance credential or state marker.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> reuse the Run
    /// Coordinator's existing retained-state correlation for the collected
    /// Completed CaptureComplete result, which in turn carries the capture
    /// index commit, the Published artifact publication, the committed plan
    /// commit, the Committed Registry entry, the Finalized context, and the
    /// live Session Ownership Lease. The already-validated graph is never
    /// re-verified by a separate mechanism and no file is re-inspected.
    /// </para>
    /// <para>
    /// The forwarding surface is intentionally narrow: only what a later
    /// cleanup needs. Values reachable through the forwarded receipts, such as
    /// the descriptor, the finalization result, the frame relation, and the
    /// artifact's path, length, and hash, are not duplicated as properties
    /// here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteCleanupOperation
    {
        private readonly NvencCaptureRunCoordinator _coordinator;
        private readonly NvencRunCaptureCompleteReceipt _captureCompleteReceipt;

        internal NvencRunCaptureCompleteCleanupOperation(
            NvencCaptureRunCoordinator coordinator,
            NvencRunCaptureCompleteReceipt captureCompleteReceipt)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _captureCompleteReceipt = captureCompleteReceipt
                ?? throw new ArgumentNullException(nameof(captureCompleteReceipt));
        }

        internal NvencRunCaptureCompleteReceipt CaptureCompleteReceipt => _captureCompleteReceipt;

        internal NvencRunCaptureCompleteOperation CaptureCompleteOperation =>
            _captureCompleteReceipt.Operation;

        internal NvencRunCaptureIndexCommitReceipt CaptureIndexCommitReceipt =>
            _captureCompleteReceipt.CaptureIndexCommitReceipt;

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt =>
            _captureCompleteReceipt.ArtifactPublicationReceipt;

        internal CapturePublicationPlan Plan => _captureCompleteReceipt.Plan;

        internal CaptureRunRootLayout RootLayout => _captureCompleteReceipt.RootLayout;

        internal long TestRunId => _captureCompleteReceipt.TestRunId;

        internal string RunInitializationId => _captureCompleteReceipt.RunInitializationId;

        internal bool IsValid =>
            _coordinator != null
            && _captureCompleteReceipt != null
            && _coordinator.IsCaptureCompleteCleanupReceiptCorrelated(_captureCompleteReceipt);

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }
    }
}
