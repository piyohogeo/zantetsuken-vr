using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable prepared PngJson artifact recovery step: the action plan, the
    /// step index, and at most one of a publish operation or a capture index
    /// commit operation, according to the step's fixed action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action-to-operation correlation is exclusive:
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.PublishArtifact"/>
    /// holds a publish operation only,
    /// <see cref="CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex"/>
    /// holds a capture index commit operation only, and every routing or stop
    /// action holds neither.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — neither the action plan,
    /// the decision, the snapshot, the lease, nor the canonical bytes — and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryPreparedStep
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly PngJsonCapturePublicationArtifactPublishOperation _publishOperation;
        private readonly PngJsonCaptureRunCaptureIndexCommitOperation _captureIndexCommitOperation;

        private PngJsonCapturePublicationArtifactRecoveryPreparedStep(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            PngJsonCapturePublicationArtifactPublishOperation publishOperation,
            PngJsonCaptureRunCaptureIndexCommitOperation captureIndexCommitOperation)
        {
            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _publishOperation = publishOperation;
            _captureIndexCommitOperation = captureIndexCommitOperation;
        }

        /// <summary>
        /// O(1) token-gated atomic factory: re-verifies only the targeted step
        /// through the token's index-local step accessor, then builds exactly
        /// the one operation required by the step's action through the
        /// operation's index-local factory path. It never re-validates the
        /// plan, re-issues a token, or scans an entry.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryPreparedStep CreateIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!token.TryGetIssuedStep(actionPlan, stepIndex, out CaptureRunPublicationArtifactRecoveryStep step))
            {
                // Diagnose an out-of-range index without leaking an exception
                // from a nulled or shortened step array.
                int count;
                try
                {
                    count = actionPlan.Count;
                }
                catch (Exception)
                {
                    throw new ArgumentException("Action plan step array must remain intact.", nameof(stepIndex));
                }

                if (stepIndex < 0 || stepIndex >= count)
                {
                    throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index must be within the step count.");
                }

                throw new ArgumentException("Step must be bound by the issued token.", nameof(stepIndex));
            }

            PngJsonCapturePublicationArtifactPublishOperation publishOperation = null;
            PngJsonCaptureRunCaptureIndexCommitOperation captureIndexCommitOperation = null;

            switch (step.Action)
            {
                case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    publishOperation = PngJsonCapturePublicationArtifactPublishOperationFactory.CreateIndexLocal(
                        actionPlan, token, stepIndex);
                    break;

                case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    captureIndexCommitOperation = PngJsonCaptureRunCaptureIndexCommitOperationFactory.CreateIndexLocal(
                        actionPlan, token, stepIndex);
                    break;

                case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                    break;

                default:
                    throw new ArgumentException("Step action must be a defined artifact recovery action.", nameof(stepIndex));
            }

            return new PngJsonCapturePublicationArtifactRecoveryPreparedStep(
                actionPlan, stepIndex, publishOperation, captureIndexCommitOperation);
        }

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _actionPlan.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _actionPlan.AuthorityKind;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationArtifactRecoveryAction Action => Step.Action;

        internal PngJsonCapturePublicationArtifactPublishOperation PublishOperation => _publishOperation;

        internal PngJsonCaptureRunCaptureIndexCommitOperation CaptureIndexCommitOperation => _captureIndexCommitOperation;

        /// <summary>
        /// Full validity: validates the whole plan once through a non-throwing
        /// token issuance and delegates to <see cref="IsValidWithToken"/> with
        /// the same token. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null)
                {
                    return false;
                }

                if (!_actionPlan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
                {
                    return false;
                }

                return IsValidWithToken(token);
            }
        }

        /// <summary>
        /// O(1), exception-safe index-local validity: re-verifies only the
        /// targeted step and its exclusive operation correlation. It never
        /// re-serializes a commit operation's canonical bytes.
        /// </summary>
        internal bool IsValidIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return IsPreparedStepValid(_actionPlan, token, _stepIndex, _publishOperation, _captureIndexCommitOperation, fullCommitValidation: false);
        }

        /// <summary>
        /// Token-gated full validity: re-verifies the targeted step and its
        /// exclusive operation correlation, and for a commit step re-serializes
        /// the canonical bytes through
        /// <see cref="PngJsonCaptureRunCaptureIndexCommitOperation.IsValidWithToken"/>.
        /// Never throws.
        /// </summary>
        internal bool IsValidWithToken(
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return IsPreparedStepValid(_actionPlan, token, _stepIndex, _publishOperation, _captureIndexCommitOperation, fullCommitValidation: true);
        }

        private static bool IsPreparedStepValid(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex,
            PngJsonCapturePublicationArtifactPublishOperation publishOperation,
            PngJsonCaptureRunCaptureIndexCommitOperation captureIndexCommitOperation,
            bool fullCommitValidation)
        {
            if (actionPlan == null || token == null)
            {
                return false;
            }

            if (!token.TryGetIssuedStep(actionPlan, stepIndex, out CaptureRunPublicationArtifactRecoveryStep step))
            {
                return false;
            }

            switch (step.Action)
            {
                case CaptureRunPublicationArtifactRecoveryAction.PublishArtifact:
                    return publishOperation != null
                        && captureIndexCommitOperation == null
                        && ReferenceEquals(publishOperation.ActionPlan, actionPlan)
                        && publishOperation.StepIndex == stepIndex
                        && publishOperation.IsValidIndexLocal(token);

                case CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex:
                    return publishOperation == null
                        && captureIndexCommitOperation != null
                        && ReferenceEquals(captureIndexCommitOperation.ActionPlan, actionPlan)
                        && captureIndexCommitOperation.StepIndex == stepIndex
                        && (fullCommitValidation
                            ? captureIndexCommitOperation.IsValidWithToken(token)
                            : captureIndexCommitOperation.IsValidIndexLocal(token));

                case CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts:
                case CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup:
                case CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace:
                case CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing:
                case CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision:
                    return publishOperation == null && captureIndexCommitOperation == null;

                default:
                    return false;
            }
        }
    }
}
