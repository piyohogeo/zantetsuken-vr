using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run artifact publication operation: the
    /// exact Run Coordinator and the exact verified Committed publication plan
    /// commit Execution Result, fixed together for the future Publication
    /// Service. The plan, chunk finalization result, descriptor, frame
    /// relation, root layout, run identity, and expected byte length and
    /// content hash are forwarded from the held graph and are never duplicated
    /// as fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type performs no file or hash operation, does not re-read the plan
    /// or the chunk, never advances the registry or the evidence state, and
    /// owns no lease, handle, or buffer. It introduces no new issuance
    /// credential or state marker and performs no cleanup, retry, or recovery
    /// classification.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> reuse the existing
    /// commit Execution Result and the Run Coordinator's current retained-state
    /// correlation; the already-validated plan, descriptor, relation, run
    /// identity, and session graph are never re-verified by a separate
    /// mechanism. Absolute paths are not derived here; safe resolution under a
    /// fixed root is the later filesystem publisher's duty.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunArtifactPublicationOperation
    {
        private readonly NvencCaptureRunCoordinator _coordinator;
        private readonly NvencRunPublicationPlanCommitExecutionResult _planCommitResult;

        internal NvencRunArtifactPublicationOperation(
            NvencCaptureRunCoordinator coordinator,
            NvencRunPublicationPlanCommitExecutionResult planCommitResult)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _planCommitResult = planCommitResult ?? throw new ArgumentNullException(nameof(planCommitResult));
        }

        internal NvencRunPublicationPlanCommitExecutionResult PlanCommitResult => _planCommitResult;

        internal CapturePublicationPlan Plan => _planCommitResult.Plan;

        internal NvencChunkFinalizationResult FinalizationResult => _planCommitResult.FinalizationResult;

        internal CaptureArtifactDescriptor Descriptor => _planCommitResult.FinalizationResult?.Descriptor;

        internal CaptureArtifactFrameRelation FrameRelation => _planCommitResult.FinalizationResult?.FrameRelation;

        internal CaptureRunRootLayout RootLayout => _planCommitResult.RootLayout;

        internal long TestRunId => _planCommitResult.TestRunId;

        internal string RunInitializationId => _planCommitResult.RunInitializationId;

        internal string StagingRelativePath => Descriptor?.StagingRelativePath;

        internal string FinalRelativePath => Descriptor?.FinalRelativePath;

        internal long ExpectedByteLength => Descriptor != null ? Descriptor.ByteLength : 0L;

        internal string ExpectedContentHash => Descriptor?.ContentHash;

        internal bool IsValid =>
            _coordinator != null
            && _planCommitResult != null
            && _coordinator.IsArtifactPublicationOperationCorrelated(_planCommitResult);

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }
    }
}
