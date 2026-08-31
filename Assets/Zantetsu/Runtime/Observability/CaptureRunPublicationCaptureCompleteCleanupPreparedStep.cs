using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable prepared step of a capture-complete cleanup execution batch:
    /// one action plan step materialized into exactly one concrete cleanup
    /// operation, or no operation for the <c>CaptureCompleteReady</c> routing
    /// step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction is token-gated: it performs only O(1) index-local checks
    /// against the plan's validation token and delegates to the existing
    /// cleanup operation's token-gated constructor, so materializing every step
    /// of a large plan stays linear in the total step count. Exactly one of the
    /// eight side-effecting actions carries a cleanup operation;
    /// <c>CaptureCompleteReady</c> carries none. <see cref="IsValid"/> performs
    /// the full plan validation and token acquisition exactly once, then
    /// delegates to <see cref="IsValidIndexLocal"/>.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupPreparedStep
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupActionPlan _actionPlan;
        private readonly CaptureRunPublicationPathSet _publicationPaths;
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly int _stepIndex;
        private readonly CaptureRunPublicationCaptureCompleteCleanupOperation _cleanupOperation;

        internal CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (publicationPaths == null)
            {
                throw new ArgumentNullException(nameof(publicationPaths));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!actionPlan.IsTokenBound(token))
            {
                throw new ArgumentException("Validation token must be issued for the action plan.", nameof(token));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index out of range.");
            }

            if (!actionPlan.IsStepIdentityAt(token, stepIndex))
            {
                throw new ArgumentException("Step must be the exact validated step.", nameof(stepIndex));
            }

            CaptureRunPublicationCaptureCompleteCleanupStep step = actionPlan.GetStep(stepIndex);
            CaptureRunPublicationCaptureCompleteCleanupAction action = step.Action;

            CaptureRunPublicationCaptureCompleteCleanupOperation operation = null;
            if (action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
            {
                ValidateRoutingStep(actionPlan, publicationPaths, markerPaths, token);
            }
            else
            {
                operation = new CaptureRunPublicationCaptureCompleteCleanupOperation(
                    actionPlan, publicationPaths, markerPaths, stepIndex, token);
            }

            _actionPlan = actionPlan;
            _publicationPaths = publicationPaths;
            _markerPaths = markerPaths;
            _stepIndex = stepIndex;
            _cleanupOperation = operation;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan ActionPlan => _actionPlan;

        internal CaptureRunPublicationPathSet PublicationPaths => _publicationPaths;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationCaptureCompleteCleanupStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationCaptureCompleteCleanupAction Action => Step.Action;

        internal CaptureRunPublicationCaptureCompleteCleanupOperation CleanupOperation => _cleanupOperation;

        /// <summary>
        /// Performs the full plan validation and token acquisition exactly once,
        /// then delegates to the index-local predicate.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || _publicationPaths == null || _markerPaths == null)
                {
                    return false;
                }

                if (!_actionPlan.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
                {
                    return false;
                }

                return IsValidIndexLocal(token);
            }
        }

        /// <summary>
        /// O(1), exception-safe index-local check: the token must bind to the
        /// plan's exact step array, the nested structure must be intact, and a
        /// side-effecting step must hold exactly one cleanup operation whose
        /// full token-gated correlation is re-verified, while
        /// <c>CaptureCompleteReady</c> holds none but still re-confirms the
        /// exact publication path set instance, both path sets' validity and
        /// root layout correlation, and lease liveness.
        /// </summary>
        internal bool IsValidIndexLocal(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (token == null || _actionPlan == null || _publicationPaths == null || _markerPaths == null)
            {
                return false;
            }

            if (!_actionPlan.IsTokenBound(token))
            {
                return false;
            }

            if (_stepIndex < 0 || _stepIndex >= _actionPlan.Count)
            {
                return false;
            }

            if (!_actionPlan.IsStepIdentityAt(token, _stepIndex))
            {
                return false;
            }

            if (!_actionPlan.IsIndexLocalStructureIntact())
            {
                return false;
            }

            if (!_actionPlan.IsExecutionResultIntact(token))
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
                if (_cleanupOperation != null)
                {
                    return false;
                }

                return ReferenceEquals(_publicationPaths, PublicationInspectionPaths(_actionPlan))
                    && ReferenceEquals(_publicationPaths.RootLayout, _actionPlan.RootLayout)
                    && _publicationPaths.IsValid
                    && ReferenceEquals(_markerPaths.RootLayout, _actionPlan.RootLayout)
                    && _markerPaths.IsValid;
            }

            if (_cleanupOperation == null)
            {
                return false;
            }

            if (!ReferenceEquals(_cleanupOperation.ActionPlan, _actionPlan)
                || !ReferenceEquals(_cleanupOperation.PublicationPaths, _publicationPaths)
                || !ReferenceEquals(_cleanupOperation.MarkerPaths, _markerPaths)
                || _cleanupOperation.StepIndex != _stepIndex
                || _cleanupOperation.Action != action
                || _cleanupOperation.EntryIndex != step.EntryIndex
                || _cleanupOperation.ArtifactKind != step.ArtifactKind)
            {
                return false;
            }

            // Re-verify the operation's full token-gated correlation, including
            // the exact path set instance and validity, lease liveness,
            // inspection correlation, artifact path set and observation
            // index-local validity, evidence status, and plan entry correlation.
            return _cleanupOperation.IsValidIndexLocal(token);
        }

        /// <summary>
        /// Exception-safe read of this prepared step's identity triple: its
        /// step index, the action of the plan step it materializes, and its
        /// cleanup operation. The action is read through the plan only after
        /// the plan and its step array are structurally guarded, so a forged
        /// null plan or step array fails closed instead of throwing.
        /// </summary>
        internal bool TryGetIssuedIdentity(
            out int stepIndex,
            out CaptureRunPublicationCaptureCompleteCleanupAction action,
            out CaptureRunPublicationCaptureCompleteCleanupOperation operation)
        {
            stepIndex = _stepIndex;
            action = default(CaptureRunPublicationCaptureCompleteCleanupAction);
            operation = _cleanupOperation;

            if (_actionPlan == null)
            {
                return false;
            }

            if (!_actionPlan.TryGetStep(_stepIndex, out CaptureRunPublicationCaptureCompleteCleanupStep step))
            {
                return false;
            }

            action = step.Action;
            return true;
        }

        /// <summary>
        /// Exception-safe check that this prepared step still carries the given
        /// identity triple. Shared with the batch token so its per-step proof
        /// comparison never reads the plan action without a structural guard.
        /// </summary>
        internal bool MatchesIssuedProof(
            int stepIndex,
            CaptureRunPublicationCaptureCompleteCleanupAction action,
            CaptureRunPublicationCaptureCompleteCleanupOperation operation)
        {
            if (!TryGetIssuedIdentity(
                    out int currentStepIndex,
                    out CaptureRunPublicationCaptureCompleteCleanupAction currentAction,
                    out CaptureRunPublicationCaptureCompleteCleanupOperation currentOperation))
            {
                return false;
            }

            return currentStepIndex == stepIndex
                && currentAction == action
                && ReferenceEquals(currentOperation, operation);
        }

        private static void ValidateRoutingStep(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (!actionPlan.IsIndexLocalStructureIntact())
            {
                throw new ArgumentException(
                    "Action plan, inspection operation, and lease must be valid and correlated.",
                    nameof(actionPlan));
            }

            if (!ReferenceEquals(publicationPaths, PublicationInspectionPaths(actionPlan)))
            {
                throw new ArgumentException(
                    "Publication path set must be the publication inspection operation's exact path set.",
                    nameof(publicationPaths));
            }

            if (!ReferenceEquals(publicationPaths.RootLayout, actionPlan.RootLayout)
                || !publicationPaths.IsValid)
            {
                throw new ArgumentException(
                    "Publication path set must be valid and share the action plan's root layout.",
                    nameof(publicationPaths));
            }

            if (!ReferenceEquals(markerPaths.RootLayout, actionPlan.RootLayout)
                || !markerPaths.IsValid)
            {
                throw new ArgumentException(
                    "Marker path set must be valid and share the action plan's root layout.",
                    nameof(markerPaths));
            }

            if (!actionPlan.IsExecutionResultIntact(token))
            {
                throw new ArgumentException(
                    "Action plan execution result must still be proven valid by its minted token.",
                    nameof(actionPlan));
            }
        }

        private static CaptureRunPublicationPathSet PublicationInspectionPaths(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            CaptureRunPublicationArtifactRecoveryOrchestrationResult result =
                actionPlan == null ? null : actionPlan.OrchestrationResult;
            if (result == null)
            {
                return null;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = result.InspectionSnapshot;
            if (snapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryDecision decision = snapshot.Decision;
            if (decision == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = decision.Snapshot;
            if (publicationSnapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionOperation publicationInspection = publicationSnapshot.Operation;
            if (publicationInspection == null)
            {
                return null;
            }

            return publicationInspection.PublicationPaths;
        }
    }
}
