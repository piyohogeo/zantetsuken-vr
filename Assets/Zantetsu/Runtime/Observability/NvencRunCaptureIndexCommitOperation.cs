using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run capture index commit operation: the
    /// exact Run Coordinator and the exact Published artifact publication
    /// receipt, fixed together for the future capture index committer. The
    /// publication operation, plan, root layout, and run identity are
    /// forwarded from the held receipt's graph and are never duplicated as
    /// fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type performs no file, hash, or serialization operation, holds no
    /// canonical byte copy or resolved path, never advances the registry, the
    /// disposition, or the evidence state, and owns no lease, handle, or
    /// buffer. It introduces no new issuance credential or state marker and
    /// performs no cleanup, retry, or recovery classification.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> reuse the Run
    /// Coordinator's existing retained-state correlation for the collected
    /// Published artifact publication result; the already-validated plan,
    /// descriptor, registry entry, run identity, and session graph are never
    /// re-verified by a separate mechanism. A process-wide Poison invalidates
    /// an already-issued operation, so no later capture index work can start
    /// from it. Validity is deliberately not tied
    /// to whether the Publication Service has been released, so a later phase
    /// that keeps the same Service alive through CaptureComplete does not
    /// change this contract.
    /// </para>
    /// <para>
    /// The forwarding surface is intentionally narrow. Values reachable
    /// through <see cref="ArtifactPublicationReceipt"/> or
    /// <see cref="ArtifactPublicationOperation"/>, such as the descriptor and
    /// the finalization result, are not duplicated as properties here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexCommitOperation
    {
        private readonly NvencCaptureRunCoordinator _coordinator;
        private readonly NvencRunArtifactPublicationReceipt _publicationReceipt;

        internal NvencRunCaptureIndexCommitOperation(
            NvencCaptureRunCoordinator coordinator,
            NvencRunArtifactPublicationReceipt publicationReceipt)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _publicationReceipt = publicationReceipt
                ?? throw new ArgumentNullException(nameof(publicationReceipt));
        }

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt => _publicationReceipt;

        internal NvencRunArtifactPublicationOperation ArtifactPublicationOperation => _publicationReceipt.Operation;

        internal CapturePublicationPlan Plan => _publicationReceipt.Plan;

        internal CaptureRunRootLayout RootLayout => _publicationReceipt.RootLayout;

        internal long TestRunId => _publicationReceipt.TestRunId;

        internal string RunInitializationId => _publicationReceipt.RunInitializationId;

        internal bool IsValid =>
            _coordinator != null
            && _publicationReceipt != null
            && _coordinator.IsCaptureIndexCommitReceiptCorrelated(_publicationReceipt);

        /// <summary>
        /// Exception-safe post-commit issuance binding: the same exact
        /// correlation as <see cref="IsValid"/> on an unpoisoned process,
        /// except that the disposition may be either <c>Committed</c> (before
        /// the commit is reflected) or
        /// <see cref="NvencRunEvidenceDisposition.PublicationRecoveryRequired"/>
        /// (after a Failed commit is reflected). Used by the issued attempt
        /// result and receipt so that reflecting a Failed capture index commit
        /// does not invalidate them. It re-inspects no file and introduces no
        /// second authority.
        /// </summary>
        internal bool IsBindingIntact =>
            _coordinator != null
            && _publicationReceipt != null
            && _coordinator.IsCaptureIndexCommitBindingIntact(_publicationReceipt);

        /// <summary>
        /// Minimal O(1) exact-process-state correlation used by the Publication
        /// Service: true only while this exact operation is the exact retained
        /// capture index commit operation of its Run Coordinator and that Run
        /// Coordinator is bound to the exact supplied process state. It
        /// delegates only ReferenceEquals checks to the Run Coordinator and
        /// never exposes the Coordinator or the process state as a property.
        /// </summary>
        internal bool IsBoundToProcessState(NvencCaptureProcessState processState)
        {
            return processState != null
                && _coordinator != null
                && _coordinator.IsCaptureIndexCommitOperationBoundTo(this, processState);
        }

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }
    }
}
