using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a completed PngJson publication artifact recovery
    /// execution: the coordinator that issued it, the batch it executed, the
    /// completed steps in index order, and the exact action plan validation
    /// token used for validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The completed-step array is defensively copied at construction and never
    /// exposed; only <see cref="Count"/> and a range-checked
    /// <see cref="GetCompletedStep"/> access it. <see cref="Status"/> is
    /// derived from the validated disposition and never injected as a field.
    /// <see cref="IsValid"/> recomputes the full correlation from the held
    /// token without re-issuing it, re-validating the plan, or scanning an
    /// entry.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryExecutionResult
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationArtifactRecoveryExecutionBatch _batch;
        private readonly PngJsonCapturePublicationArtifactRecoveryCompletedStep[] _completedSteps;
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken _token;

        private PngJsonCapturePublicationArtifactRecoveryExecutionResult(
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator issuedBy,
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] completedSteps,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            _issuedBy = issuedBy;
            _batch = batch;
            _completedSteps = completedSteps;
            _token = token;
        }

        /// <summary>
        /// Atomic validated factory: null-checks every input, allocates an
        /// exact-length copy array once, and reads each input element once per
        /// iteration — validating it and storing that same reference immediately
        /// in the copy, so validation and copying never split into two loops
        /// that would open a time-of-check/time-of-use window.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryExecutionResult Create(
            PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator issuedBy,
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] completedSteps,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (completedSteps == null)
            {
                throw new ArgumentNullException(nameof(completedSteps));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            int count = batch.Count;

            if (completedSteps.Length != count)
            {
                throw new ArgumentException("Completed step count must equal the batch step count.", nameof(completedSteps));
            }

            PngJsonCapturePublicationArtifactRecoveryCompletedStep[] copy =
                new PngJsonCapturePublicationArtifactRecoveryCompletedStep[count];

            for (int i = 0; i < count; i++)
            {
                PngJsonCapturePublicationArtifactRecoveryCompletedStep completedStep = completedSteps[i];
                PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep = batch.GetStep(i);

                if (completedStep == null
                    || completedStep.StepIndex != i
                    || !ReferenceEquals(completedStep.PreparedStep, preparedStep)
                    || !completedStep.IsValidIndexLocal(token))
                {
                    throw new ArgumentException("Completed steps must be index-ordered and correlated with the batch.", nameof(completedSteps));
                }

                copy[i] = completedStep;
            }

            return new PngJsonCapturePublicationArtifactRecoveryExecutionResult(issuedBy, batch, copy, token);
        }

        internal PngJsonCapturePublicationArtifactRecoveryExecutionCoordinator IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationArtifactRecoveryExecutionBatch Batch => _batch;

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _batch.ActionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _batch.Decision;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _batch.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _batch.AuthorityKind;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _batch.Disposition;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _batch.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _batch.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _batch.LockIdentityEvidence;

        internal long TestRunId => _batch.TestRunId;

        internal string RunInitializationId => _batch.RunInitializationId;

        internal string RunManifestContentSha256 => _batch.RunManifestContentSha256;

        internal int Count => _completedSteps.Length;

        /// <summary>
        /// Execution status derived from the batch's validated disposition; it
        /// is never injected as a field. An undefined or <c>None</c>
        /// disposition maps to <see cref="CaptureRunPublicationArtifactRecoveryExecutionStatus.None"/>.
        /// </summary>
        internal CaptureRunPublicationArtifactRecoveryExecutionStatus Status
        {
            get
            {
                try
                {
                    return _batch == null
                        ? CaptureRunPublicationArtifactRecoveryExecutionStatus.None
                        : DeriveStatus(_batch.Disposition);
                }
                catch (Exception)
                {
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.None;
                }
            }
        }

        internal PngJsonCapturePublicationArtifactRecoveryCompletedStep GetCompletedStep(int index)
        {
            if (index < 0 || index >= _completedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Completed step index out of range.");
            }

            return _completedSteps[index];
        }

        /// <summary>
        /// Exception-safe full validity recomputed from the held action plan
        /// token: the exact coordinator/batch/token correlation, the completed
        /// step count and order, each prepared step's exact reference, the
        /// action-exclusive receipt shape, each receipt's issuer/operation/token
        /// correlation, the owner liveness, and the status mapping. It never
        /// re-issues a token, re-validates the plan, or scans an entry.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_issuedBy == null || _batch == null || _completedSteps == null || _token == null)
                    {
                        return false;
                    }

                    if (Status == CaptureRunPublicationArtifactRecoveryExecutionStatus.None)
                    {
                        return false;
                    }

                    int count = _batch.Count;
                    if (_completedSteps.Length != count)
                    {
                        return false;
                    }

                    for (int i = 0; i < count; i++)
                    {
                        PngJsonCapturePublicationArtifactRecoveryCompletedStep completedStep = _completedSteps[i];
                        PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep = _batch.GetStep(i);

                        if (completedStep == null
                            || preparedStep == null
                            || !ReferenceEquals(completedStep.PreparedStep, preparedStep)
                            || completedStep.StepIndex != i
                            || !ReferenceEquals(preparedStep.ActionPlan, _batch.ActionPlan)
                            || !completedStep.IsValidIndexLocal(_token))
                        {
                            return false;
                        }

                        switch (preparedStep.Action)
                        {
                            case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                            {
                                PngJsonCapturePublicationArtifactPublishReceipt receipt = completedStep.PublishReceipt;
                                if (receipt == null
                                    || !ReferenceEquals(receipt.IssuedBy, _issuedBy.Publisher)
                                    || !receipt.IsIssuedFor(_issuedBy.Publisher, preparedStep.PublishOperation, _token))
                                {
                                    return false;
                                }

                                break;
                            }

                            case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                            {
                                PngJsonCaptureRunCaptureIndexCommitReceipt receipt = completedStep.CommitReceipt;
                                if (receipt == null
                                    || !ReferenceEquals(receipt.IssuedBy, _issuedBy.Committer)
                                    || !receipt.IsIssuedFor(_issuedBy.Committer, preparedStep.CaptureIndexCommitOperation, _token))
                                {
                                    return false;
                                }

                                break;
                            }

                            case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                            case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                            case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                            case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                            case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                            case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                                if (completedStep.PublishReceipt != null || completedStep.CommitReceipt != null)
                                {
                                    return false;
                                }

                                break;

                            default:
                                return false;
                        }
                    }

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static CaptureRunPublicationArtifactRecoveryExecutionStatus DeriveStatus(
            CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            switch (disposition)
            {
                case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired;

                case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired;

                case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace;

                case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing;

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing;

                case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision;

                default:
                    return CaptureRunPublicationArtifactRecoveryExecutionStatus.None;
            }
        }
    }
}
