using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable PngJson execution batch that materializes every step of an
    /// artifact recovery action plan into a prepared step before any side
    /// effect runs. The prepared step array is allocated exactly once at its
    /// exact length and is never exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The atomic factory validates the whole plan once and issues a single
    /// plan-bound validation token, then materializes each step in ascending
    /// index order using the index-local factory paths, keeping the whole
    /// construction O(n). <see cref="IsValid"/> recomputes the same
    /// correlations without throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — neither the action plan,
    /// the lease, the operations, nor the canonical bytes — and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryExecutionBatch
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly PngJsonCapturePublicationArtifactRecoveryPreparedStep[] _preparedSteps;

        private PngJsonCapturePublicationArtifactRecoveryExecutionBatch(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] preparedSteps)
        {
            _actionPlan = actionPlan;
            _preparedSteps = preparedSteps;
        }

        /// <summary>
        /// Atomic validated factory: validates the whole plan once through a
        /// non-throwing token issuance, allocates the prepared step array once
        /// at its exact length, and materializes each step in ascending index
        /// order with the same token before issuing the batch.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryExecutionBatch Create(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
            {
                throw new ArgumentException("Action plan must be fully valid.", nameof(actionPlan));
            }

            int count = actionPlan.Count;

            PngJsonCapturePublicationArtifactRecoveryPreparedStep[] preparedSteps =
                new PngJsonCapturePublicationArtifactRecoveryPreparedStep[count];

            for (int i = 0; i < count; i++)
            {
                preparedSteps[i] = PngJsonCapturePublicationArtifactRecoveryPreparedStep.CreateIndexLocal(
                    actionPlan, token, i);
            }

            return new PngJsonCapturePublicationArtifactRecoveryExecutionBatch(actionPlan, preparedSteps);
        }

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _actionPlan.Disposition;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _actionPlan.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _actionPlan.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _actionPlan.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _actionPlan.LockIdentityEvidence;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal string RunManifestContentSha256 => _actionPlan.RunManifestContentSha256;

        internal int Count => _preparedSteps.Length;

        internal PngJsonCapturePublicationArtifactRecoveryPreparedStep GetStep(int index)
        {
            if (index < 0 || index >= _preparedSteps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the step count.");
            }

            return _preparedSteps[index];
        }

        internal bool IsValid => TryValidate(out _);

        /// <summary>
        /// Fully validates this batch once and returns the action plan
        /// validation token acquired during that validation, so a caller can
        /// perform the single full validation and reuse the token for
        /// index-local checks without re-validating the plan a second time.
        /// A commit step's canonical bytes are re-verified through
        /// <see cref="PngJsonCapturePublicationArtifactRecoveryPreparedStep.IsValidWithToken"/>.
        /// </summary>
        internal bool TryValidate(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            token = null;

            if (_actionPlan == null || _preparedSteps == null)
            {
                return false;
            }

            if (!_actionPlan.TryAcquireValidationToken(out token))
            {
                return false;
            }

            if (_preparedSteps.Length != _actionPlan.Count)
            {
                token = null;
                return false;
            }

            for (int i = 0; i < _preparedSteps.Length; i++)
            {
                PngJsonCapturePublicationArtifactRecoveryPreparedStep preparedStep = _preparedSteps[i];
                if (preparedStep == null
                    || preparedStep.StepIndex != i
                    || !ReferenceEquals(preparedStep.ActionPlan, _actionPlan)
                    || !preparedStep.IsValidWithToken(token))
                {
                    token = null;
                    return false;
                }
            }

            return true;
        }
    }
}
