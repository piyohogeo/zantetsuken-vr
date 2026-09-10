using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable record of what one Phase 0.11 NVENC publication recovery
    /// inspection observed: the finished <c>publication.plan</c>, whether the
    /// NVENC precommit temporary exists, and, when the plan was canonical, the
    /// verification of the final chunk it declares.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds observed facts only. The temporary is observed by existence
    /// alone: its content, canonical form, Run correlation, and corresponding
    /// chunk are neither held nor analysed here. The Fresh path's receipts,
    /// writer hash, and finalization result are not inputs; the previous
    /// process's <see cref="NvencRunEvidenceDisposition"/> is never restored or
    /// inferred.
    /// </para>
    /// <para>
    /// A canonical observation must carry the exact plan that was read, and any
    /// other status must carry none. A chunk verification is meaningful only
    /// under a canonical plan, so a non-canonical observation must carry the
    /// default, unexecuted result. Conversely, an observation that reaches this
    /// Run's own chunk - a canonical plan of the fixed graph with no competing
    /// temporary - must carry that chunk's verification: without it the
    /// inspection simply did not finish, which is not an observed fact and must
    /// never be read as one. This type performs no filesystem, codec, or hash
    /// work, owns and disposes nothing, and never throws from
    /// <see cref="IsValid"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryInspectionSnapshot
    {
        private readonly NvencRunPublicationRecoveryInspectionOperation _operation;
        private readonly CaptureRunPublicationDocumentObservationStatus _publicationPlanStatus;
        private readonly CapturePublicationPlan _publicationPlan;
        private readonly bool _precommitTemporaryPresent;
        private readonly CaptureArtifactVerificationResult _chunkVerification;

        internal NvencRunPublicationRecoveryInspectionSnapshot(
            NvencRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservationStatus publicationPlanStatus,
            CapturePublicationPlan publicationPlan,
            bool precommitTemporaryPresent,
            CaptureArtifactVerificationResult chunkVerification)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!IsDefinedStatus(publicationPlanStatus))
            {
                throw new ArgumentException(
                    "Publication plan status must be a defined observation status.",
                    nameof(publicationPlanStatus));
            }

            if (publicationPlanStatus == CaptureRunPublicationDocumentObservationStatus.Canonical)
            {
                if (publicationPlan == null || !publicationPlan.IsValid)
                {
                    throw new ArgumentException(
                        "A canonical observation must carry the plan that was read.",
                        nameof(publicationPlan));
                }
            }
            else if (publicationPlan != null)
            {
                throw new ArgumentException(
                    "Only a canonical observation may carry a plan.", nameof(publicationPlan));
            }

            if (HasChunkVerification(chunkVerification))
            {
                if (publicationPlanStatus != CaptureRunPublicationDocumentObservationStatus.Canonical)
                {
                    throw new ArgumentException(
                        "A chunk verification is meaningful only under a canonical plan.",
                        nameof(chunkVerification));
                }

                if (!chunkVerification.IsValid)
                {
                    throw new ArgumentException(
                        "The chunk verification result must be valid.", nameof(chunkVerification));
                }
            }
            else if (RequiresChunkVerification(
                operation, publicationPlanStatus, publicationPlan, precommitTemporaryPresent))
            {
                throw new ArgumentException(
                    "An observation that turns on this Run's own chunk must carry its verification.",
                    nameof(chunkVerification));
            }

            _operation = operation;
            _publicationPlanStatus = publicationPlanStatus;
            _publicationPlan = publicationPlan;
            _precommitTemporaryPresent = precommitTemporaryPresent;
            _chunkVerification = chunkVerification;
        }

        internal NvencRunPublicationRecoveryInspectionOperation Operation => _operation;

        internal CaptureRunPublicationDocumentObservationStatus PublicationPlanStatus =>
            _publicationPlanStatus;

        /// <summary>
        /// The exact plan that was read, held by reference and never copied or
        /// re-serialized. Null unless the observation was canonical.
        /// </summary>
        internal CapturePublicationPlan PublicationPlan => _publicationPlan;

        /// <summary>
        /// Whether <c>publication.plan.nvenc-precommit.tmp</c> exists. Existence
        /// is the whole observation: nothing about its content is held.
        /// </summary>
        internal bool PrecommitTemporaryPresent => _precommitTemporaryPresent;

        internal CaptureArtifactVerificationResult ChunkVerification => _chunkVerification;

        internal bool HasChunkVerificationResult => HasChunkVerification(_chunkVerification);

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_operation == null || !_operation.IsValid
                        || !IsDefinedStatus(_publicationPlanStatus))
                    {
                        return false;
                    }

                    if (_publicationPlanStatus
                        == CaptureRunPublicationDocumentObservationStatus.Canonical)
                    {
                        if (_publicationPlan == null || !_publicationPlan.IsValid)
                        {
                            return false;
                        }
                    }
                    else if (_publicationPlan != null)
                    {
                        return false;
                    }

                    if (HasChunkVerification(_chunkVerification))
                    {
                        return _publicationPlanStatus
                                == CaptureRunPublicationDocumentObservationStatus.Canonical
                            && _chunkVerification.IsValid;
                    }

                    return !RequiresChunkVerification(
                        _operation,
                        _publicationPlanStatus,
                        _publicationPlan,
                        _precommitTemporaryPresent);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// True when the observation reaches this Run's own chunk, so a missing
        /// verification means the inspection did not finish rather than that
        /// anything was found on disk. A competing temporary or a plan outside
        /// the fixed graph is already decided without the chunk, so neither
        /// needs one.
        /// </summary>
        private static bool RequiresChunkVerification(
            NvencRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservationStatus status,
            CapturePublicationPlan plan,
            bool precommitTemporaryPresent)
        {
            return status == CaptureRunPublicationDocumentObservationStatus.Canonical
                && !precommitTemporaryPresent
                && NvencRunPublicationRecoveryPlanShape.IsFixedPhase011Plan(
                    plan, operation.TestRunId, operation.RunInitializationId);
        }

        private static bool HasChunkVerification(CaptureArtifactVerificationResult result)
        {
            return result.ExecutionDisposition
                != CaptureArtifactVerificationExecutionDisposition.None;
        }

        private static bool IsDefinedStatus(CaptureRunPublicationDocumentObservationStatus status)
        {
            switch (status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                case CaptureRunPublicationDocumentObservationStatus.Invalid:
                case CaptureRunPublicationDocumentObservationStatus.LimitExceeded:
                    return true;

                default:
                    return false;
            }
        }
    }
}
