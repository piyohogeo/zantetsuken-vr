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
            if (action != CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
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
        /// side-effecting step must hold exactly one correlated cleanup
        /// operation while <c>CaptureCompleteReady</c> holds none.
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
                || !ReferenceEquals(_cleanupOperation.PublicationPaths, _publicationPaths)
                || !ReferenceEquals(_cleanupOperation.MarkerPaths, _markerPaths))
            {
                return false;
            }

            if (_cleanupOperation.StepIndex != _stepIndex)
            {
                return false;
            }

            if (_cleanupOperation.Action != action
                || _cleanupOperation.EntryIndex != step.EntryIndex
                || _cleanupOperation.ArtifactKind != step.ArtifactKind)
            {
                return false;
            }

            if (string.IsNullOrEmpty(_cleanupOperation.TargetPath))
            {
                return false;
            }

            return true;
        }
    }
}
