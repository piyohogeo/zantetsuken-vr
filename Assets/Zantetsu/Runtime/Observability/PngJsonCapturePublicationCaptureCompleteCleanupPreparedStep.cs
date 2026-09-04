using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable prepared step of a PngJson capture-complete cleanup execution
    /// batch: one action plan step materialized into exactly one concrete
    /// cleanup operation, or no operation for the
    /// <c>CaptureCompleteReady</c> routing step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly three values: the exact action plan, the step
    /// index, and the optional cleanup operation. Construction is token-gated:
    /// the single factory first fetches the exact issued step through the
    /// token's O(1) index-local accessor and then branches on the action, so a
    /// corrupted step array never leaks an exception before the action is
    /// known. Each of the eight side-effecting actions materializes exactly
    /// one cleanup operation through the existing operation factory's trusted
    /// path; <c>CaptureCompleteReady</c> carries none.
    /// <see cref="IsValid"/> performs the full plan validation and token
    /// acquisition exactly once, then delegates to
    /// <see cref="IsValidIndexLocal"/>.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupOperation _cleanupOperation;

        private PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            int stepIndex,
            PngJsonCapturePublicationCaptureCompleteCleanupOperation cleanupOperation)
        {
            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _cleanupOperation = cleanupOperation;
        }

        /// <summary>
        /// Token-gated atomic factory: the single construction path. It first
        /// fetches the exact issued step through the token's O(1) index-local
        /// accessor, then branches on the action. Side-effecting actions
        /// materialize one cleanup operation through the operation factory's
        /// trusted path with the same plan, token, and step index;
        /// <c>CaptureCompleteReady</c> is a routing step and carries no
        /// operation. <c>None</c> and undefined actions are rejected.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep CreateIndexLocal(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token,
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

            if (!token.TryGetIssuedCleanupInputs(
                    actionPlan,
                    stepIndex,
                    out CaptureRunPublicationCaptureCompleteCleanupStep step,
                    out _,
                    out _))
            {
                throw new ArgumentException(
                    "Validation token must bind to the exact validated step.",
                    nameof(token));
            }

            CaptureRunPublicationCaptureCompleteCleanupAction action = step.Action;

            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation;
            switch (action)
            {
                case CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady:
                    operation = null;
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                    operation = PngJsonCapturePublicationCaptureCompleteCleanupOperationFactory.CreateIndexLocal(
                        token,
                        actionPlan,
                        DerivePublicationPaths(actionPlan),
                        new CaptureRunMarkerPathSet(actionPlan.RootLayout),
                        stepIndex);
                    break;

                default:
                    throw new ArgumentException(
                        "Step action must be a defined cleanup action.",
                        nameof(stepIndex));
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep(actionPlan, stepIndex, operation);
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

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationCaptureCompleteCleanupStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationCaptureCompleteCleanupAction Action => Step.Action;

        internal PngJsonCapturePublicationCaptureCompleteCleanupOperation CleanupOperation => _cleanupOperation;

        /// <summary>
        /// Performs the full plan validation and token acquisition exactly once,
        /// then delegates to the index-local predicate. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null)
                {
                    return false;
                }

                if (!_actionPlan.TryValidate(out PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
                {
                    return false;
                }

                return IsValidIndexLocal(token);
            }
        }

        /// <summary>
        /// O(1), exception-safe index-local check: the token must bind to the
        /// plan's exact step, the plan's nested structure must be intact, and a
        /// side-effecting step must hold exactly one cleanup operation whose
        /// plan, index, and action correlation plus full token-gated index-local
        /// correlation are re-verified. <c>CaptureCompleteReady</c> holds none.
        /// It never re-validates the whole plan, re-issues a token, scans an
        /// entry, or touches a filesystem.
        /// </summary>
        internal bool IsValidIndexLocal(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            try
            {
                if (token == null || _actionPlan == null)
                {
                    return false;
                }

                if (!_actionPlan.IsValidIndexLocal(token, _stepIndex))
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupStep step = _actionPlan.GetStep(_stepIndex);
                if (step == null || !step.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupAction action = step.Action;

                if (action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    return _cleanupOperation == null;
                }

                if (_cleanupOperation == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_cleanupOperation.ActionPlan, _actionPlan)
                    || _cleanupOperation.StepIndex != _stepIndex
                    || _cleanupOperation.Action != action)
                {
                    return false;
                }

                return _cleanupOperation.IsValidIndexLocal(token);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static CaptureRunPublicationPathSet DerivePublicationPaths(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            try
            {
                PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result = actionPlan.OrchestrationResult;
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = result == null ? null : result.InspectionSnapshot;
                PngJsonCapturePublicationArtifactInspectionOperation operation = snapshot == null ? null : snapshot.Operation;
                return operation == null ? null : operation.PublicationPaths;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
