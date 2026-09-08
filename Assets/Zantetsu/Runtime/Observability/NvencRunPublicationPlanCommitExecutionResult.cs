using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning proof that one NVENC Run publication plan commit
    /// attempt was executed exactly once and its returned attempt result was
    /// verified: the exact execution coordinator, the coordinator-minted
    /// issuance proof, the exact operation, and the exact attempt result. The
    /// status, receipt, plan, finalization result, trace freeze receipt, run
    /// identity, and root layout are forwarded from the held graph and never
    /// duplicated as fields.
    /// </summary>
    /// <remarks>
    /// <see cref="Create"/> verifies only the proof's exact binding and never
    /// re-runs the committer. <see cref="IsValid"/> re-verifies the proof
    /// binding and the attempt result's current exact issuance. A Committed
    /// result stays valid even after the Registry Slot advances to
    /// <c>Committed</c>, while plan, receipt, or Session Issue corruption or a
    /// released ownership lease fails closed. This type owns, mutates, and
    /// disposes nothing and is not an <see cref="IDisposable"/>.
    /// </remarks>
    internal sealed class NvencRunPublicationPlanCommitExecutionResult
    {
        private readonly NvencRunPublicationPlanCommitExecutionCoordinator _issuedBy;
        private readonly NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof _proof;
        private readonly NvencRunPublicationPlanCommitOperation _operation;
        private readonly NvencRunPublicationPlanCommitAttemptResult _attempt;

        private NvencRunPublicationPlanCommitExecutionResult(
            NvencRunPublicationPlanCommitExecutionCoordinator issuedBy,
            NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof proof,
            NvencRunPublicationPlanCommitOperation operation,
            NvencRunPublicationPlanCommitAttemptResult attempt)
        {
            _issuedBy = issuedBy;
            _proof = proof;
            _operation = operation;
            _attempt = attempt;
        }

        internal static NvencRunPublicationPlanCommitExecutionResult Create(
            NvencRunPublicationPlanCommitExecutionCoordinator issuedBy,
            NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof proof,
            NvencRunPublicationPlanCommitOperation operation,
            NvencRunPublicationPlanCommitAttemptResult attempt)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (proof == null)
            {
                throw new ArgumentNullException(nameof(proof));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!proof.IsMintedFor(issuedBy, issuedBy.Committer, operation, attempt))
            {
                throw new ArgumentException(
                    "Commit execution proof is not minted by this coordinator for this attempt.",
                    nameof(proof));
            }

            return new NvencRunPublicationPlanCommitExecutionResult(
                issuedBy, proof, operation, attempt);
        }

        internal NvencRunPublicationPlanCommitExecutionCoordinator IssuedBy => _issuedBy;

        internal NvencRunPublicationPlanCommitAttemptResult Attempt => _attempt;

        internal NvencRunPublicationPlanCommitStatus Status => _attempt.Status;

        internal NvencRunPublicationPlanCommitReceipt Receipt => _attempt.Receipt;

        internal CapturePublicationPlan Plan => _operation.Plan;

        internal NvencChunkFinalizationResult FinalizationResult => _operation.FinalizationResult;

        internal NvencTraceFreezeReceipt TraceFreezeReceipt => _operation.TraceFreezeReceipt;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentHash => _operation.RunManifestContentHash;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal bool IsValid => IsCorrelated(_issuedBy, _proof, _operation, _attempt);

        private static bool IsCorrelated(
            NvencRunPublicationPlanCommitExecutionCoordinator issuedBy,
            NvencRunPublicationPlanCommitExecutionCoordinator.IssuanceProof proof,
            NvencRunPublicationPlanCommitOperation operation,
            NvencRunPublicationPlanCommitAttemptResult attempt)
        {
            try
            {
                if (issuedBy == null || proof == null || operation == null)
                {
                    return false;
                }

                if (!proof.IsMintedFor(issuedBy, issuedBy.Committer, operation, attempt))
                {
                    return false;
                }

                return attempt.IsIssuedFor(issuedBy.Committer, operation);
            }
            catch (Exception ex) when (ex is ArgumentException
                || ex is ArgumentOutOfRangeException
                || ex is InvalidOperationException)
            {
                return false;
            }
        }
    }
}
