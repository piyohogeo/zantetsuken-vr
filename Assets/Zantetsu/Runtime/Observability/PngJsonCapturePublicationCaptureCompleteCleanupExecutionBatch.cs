using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable preflight batch that materializes every step of a PngJson
    /// capture-complete cleanup action plan into a concrete prepared step
    /// before any filesystem change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The static factory validates the plan once, reuses the single
    /// validation token to materialize every prepared step in fixed ascending
    /// order, and allocates the exact-length prepared-step array once. It
    /// publishes the batch only after every step succeeds and never returns a
    /// partial batch. <see cref="IsValid"/> and <see cref="TryValidate"/> re-run
    /// the full plan validation exactly once and then verify the prepared-step
    /// array in a single O(n) pass, reusing the action plan token rather than
    /// minting a separate batch proof.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, calls no cleanup backend, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan _actionPlan;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[] _steps;

        private PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[] steps)
        {
            _actionPlan = actionPlan;
            _steps = steps;
        }

        /// <summary>
        /// Single atomic factory: validates the plan exactly once through its
        /// validation token, allocates the exact-length prepared-step array
        /// once, materializes every step in ascending order with that same
        /// token, and publishes the batch only after every step succeeds. A
        /// failure discards the unpublished temporary array and never returns a
        /// partial batch.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch Create(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
            {
                throw new ArgumentException(
                    "Action plan must be a valid capture-complete cleanup plan.",
                    nameof(actionPlan));
            }

            int count = actionPlan.Count;
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[] steps =
                new PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep[count];
            for (int i = 0; i < count; i++)
            {
                steps[i] = PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep.CreateIndexLocal(
                    actionPlan, token, i);
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch(actionPlan, steps);
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupActionPlan ActionPlan => _actionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _actionPlan.OrchestrationResult;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _actionPlan.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _actionPlan.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _actionPlan.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _actionPlan.LockIdentityEvidence;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal int Count => _steps.Length;

        internal PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep GetStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Prepared step index out of range.");
            }

            return _steps[index];
        }

        /// <summary>
        /// Re-runs the full plan validation exactly once and then verifies the
        /// whole prepared-step sequence with O(1) index-local checks.
        /// </summary>
        internal bool IsValid => TryValidate(out _);

        /// <summary>
        /// Issues the plan token exactly once, then verifies the prepared-step
        /// array length, ascending index, exact plan reference, and each
        /// prepared step's index-local validity in a single loop. The returned
        /// plan token is non-null only on success. A nulled, shortened, or
        /// element-swapped prepared-step array converges to false without
        /// throwing.
        /// </summary>
        internal bool TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            token = null;

            try
            {
                if (_actionPlan == null || _steps == null)
                {
                    return false;
                }

                if (!_actionPlan.TryValidate(out token))
                {
                    return false;
                }

                if (_steps.Length != _actionPlan.Count)
                {
                    token = null;
                    return false;
                }

                for (int i = 0; i < _steps.Length; i++)
                {
                    PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep prepared = _steps[i];
                    if (prepared == null
                        || !ReferenceEquals(prepared.ActionPlan, _actionPlan)
                        || prepared.StepIndex != i
                        || !prepared.IsValidIndexLocal(token))
                    {
                        token = null;
                        return false;
                    }
                }

                return true;
            }
            catch (Exception)
            {
                token = null;
                return false;
            }
        }
    }
}
