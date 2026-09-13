using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable prepared step of a recovery execution batch: the action plan
    /// step materialized into exactly one concrete operation, or no operation
    /// for a routing step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one of the three operation fields is non-null for a cleanup,
    /// provision, or write step; a routing step holds none. Construction
    /// delegates to the existing cleanup and root provision operations, and
    /// builds the marker write itself: it picks the marker and the two paths
    /// the step names, serializes that one marker once, and hands the bytes to
    /// the write operation. <see cref="IsValid"/> recomputes the exclusivity
    /// and correlation checks from the held values without throwing, and never
    /// re-serializes a marker.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryPreparedStep
    {
        private readonly CaptureRunInitializationRecoveryActionPlan _actionPlan;
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly int _stepIndex;
        private readonly CaptureRunInitializationRecoveryCleanupOperation _cleanupOperation;
        private readonly CaptureRunRootProvisionOperation _provisionOperation;
        private readonly CaptureRunMarkerWriteOperation _markerWriteOperation;

        internal CaptureRunInitializationRecoveryPreparedStep(
            CaptureRunInitializationRecoveryActionPlan actionPlan,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (!actionPlan.IsValid)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan));
            }

            if (!ReferenceEquals(markerPaths.RootLayout, actionPlan.RootLayout) || !markerPaths.IsValid)
            {
                throw new ArgumentException("Marker path set must be valid and share the action plan's root layout.", nameof(markerPaths));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index out of range.");
            }

            CaptureRunInitializationRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid)
            {
                throw new ArgumentException("Step must be valid.", nameof(stepIndex));
            }

            CaptureRunInitializationRecoveryCleanupOperation cleanup = null;
            CaptureRunRootProvisionOperation provision = null;
            CaptureRunMarkerWriteOperation write = null;

            switch (step.Action)
            {
                case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                    cleanup = new CaptureRunInitializationRecoveryCleanupOperation(actionPlan, markerPaths, stepIndex);
                    break;

                case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                    provision = new CaptureRunRootProvisionOperation(actionPlan.RootLayout, step.RootRole);
                    break;

                case CaptureRunInitializationRecoveryAction.WriteMarker:
                    write = CreateMarkerWrite(actionPlan, markerPaths, step);
                    break;
            }

            _actionPlan = actionPlan;
            _markerPaths = markerPaths;
            _stepIndex = stepIndex;
            _cleanupOperation = cleanup;
            _provisionOperation = provision;
            _markerWriteOperation = write;
        }

        internal CaptureRunInitializationRecoveryActionPlan ActionPlan => _actionPlan;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal int StepIndex => _stepIndex;

        internal CaptureRunInitializationRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunInitializationRecoveryAction Action => Step.Action;

        internal CaptureRunRootRole RootRole => Step.RootRole;

        internal CaptureRunMarkerKind MarkerKind => Step.MarkerKind;

        internal CaptureRunInitializationRecoveryCleanupOperation CleanupOperation => _cleanupOperation;

        internal CaptureRunRootProvisionOperation ProvisionOperation => _provisionOperation;

        internal CaptureRunMarkerWriteOperation MarkerWriteOperation => _markerWriteOperation;

        internal bool IsRouting
        {
            get
            {
                CaptureRunInitializationRecoveryAction action = Action;
                return action == CaptureRunInitializationRecoveryAction.StartFreshInitialization
                    || action == CaptureRunInitializationRecoveryAction.InitializationReady
                    || action == CaptureRunInitializationRecoveryAction.ContinuePublicationRecovery
                    || action == CaptureRunInitializationRecoveryAction.StopRunRootCollision;
            }
        }

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || !_actionPlan.IsValid || _markerPaths == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_markerPaths.RootLayout, _actionPlan.RootLayout) || !_markerPaths.IsValid)
                {
                    return false;
                }

                if (_stepIndex < 0 || _stepIndex >= _actionPlan.Count)
                {
                    return false;
                }

                CaptureRunInitializationRecoveryStep step = _actionPlan.GetStep(_stepIndex);
                if (step == null || !step.IsValid)
                {
                    return false;
                }

                switch (step.Action)
                {
                    case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                    case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                        return _cleanupOperation != null
                            && _provisionOperation == null
                            && _markerWriteOperation == null
                            && _cleanupOperation.IsValid
                            && ReferenceEquals(_cleanupOperation.ActionPlan, _actionPlan)
                            && ReferenceEquals(_cleanupOperation.MarkerPaths, _markerPaths)
                            && _cleanupOperation.StepIndex == _stepIndex;

                    case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                    {
                        if (_cleanupOperation != null || _provisionOperation == null || _markerWriteOperation != null)
                        {
                            return false;
                        }

                        if (!ReferenceEquals(_provisionOperation.RootLayout, _actionPlan.RootLayout)
                            || _provisionOperation.RootRole != step.RootRole
                            || _provisionOperation.TestRunId != _actionPlan.TestRunId)
                        {
                            return false;
                        }

                        string expectedTrustedBase = step.RootRole == CaptureRunRootRole.Staging
                            ? _actionPlan.RootLayout.StagingTrustedBaseRoot
                            : _actionPlan.RootLayout.FinalTrustedBaseRoot;
                        string expectedRunRoot = step.RootRole == CaptureRunRootRole.Staging
                            ? _actionPlan.RootLayout.StagingRunRoot
                            : _actionPlan.RootLayout.FinalRunRoot;

                        return string.Equals(_provisionOperation.TrustedBaseRoot, expectedTrustedBase, StringComparison.Ordinal)
                            && string.Equals(_provisionOperation.RunRoot, expectedRunRoot, StringComparison.Ordinal);
                    }

                    case CaptureRunInitializationRecoveryAction.WriteMarker:
                    {
                        if (_cleanupOperation != null || _provisionOperation != null || _markerWriteOperation == null)
                        {
                            return false;
                        }

                        if (!_markerWriteOperation.IsValid
                            || _markerWriteOperation.RootRole != step.RootRole
                            || _markerWriteOperation.MarkerKind != step.MarkerKind)
                        {
                            return false;
                        }

                        SelectPaths(step, _markerPaths, out string temporaryPath, out string finalPath);

                        return string.Equals(_markerWriteOperation.TemporaryPath, temporaryPath, StringComparison.Ordinal)
                            && string.Equals(_markerWriteOperation.FinalPath, finalPath, StringComparison.Ordinal);
                    }

                    default:
                        return _cleanupOperation == null
                            && _provisionOperation == null
                            && _markerWriteOperation == null;
                }
            }
        }

        private static CaptureRunMarkerWriteOperation CreateMarkerWrite(
            CaptureRunInitializationRecoveryActionPlan actionPlan,
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunInitializationRecoveryStep step)
        {
            CaptureRunMarkerBinding expectedBinding = actionPlan.Decision.ExpectedBinding;

            SelectPaths(step, markerPaths, out string temporaryPath, out string finalPath);
            byte[] canonicalBytes = SerializeMarker(step, expectedBinding);

            return new CaptureRunMarkerWriteOperation(
                step.RootRole,
                step.MarkerKind,
                temporaryPath,
                finalPath,
                canonicalBytes);
        }

        private static void SelectPaths(
            CaptureRunInitializationRecoveryStep step,
            CaptureRunMarkerPathSet markerPaths,
            out string temporaryPath,
            out string finalPath)
        {
            if (step.RootRole == CaptureRunRootRole.Staging)
            {
                if (step.MarkerKind == CaptureRunMarkerKind.Initialization)
                {
                    temporaryPath = markerPaths.StagingInitializationTemporaryPath;
                    finalPath = markerPaths.StagingInitializationPath;
                }
                else
                {
                    temporaryPath = markerPaths.StagingReadyTemporaryPath;
                    finalPath = markerPaths.StagingReadyPath;
                }
            }
            else if (step.MarkerKind == CaptureRunMarkerKind.Initialization)
            {
                temporaryPath = markerPaths.FinalInitializationTemporaryPath;
                finalPath = markerPaths.FinalInitializationPath;
            }
            else
            {
                temporaryPath = markerPaths.FinalReadyTemporaryPath;
                finalPath = markerPaths.FinalReadyPath;
            }
        }

        private static byte[] SerializeMarker(
            CaptureRunInitializationRecoveryStep step,
            CaptureRunMarkerBinding binding)
        {
            if (step.RootRole == CaptureRunRootRole.Staging)
            {
                return step.MarkerKind == CaptureRunMarkerKind.Initialization
                    ? CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization)
                    : CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
            }

            return step.MarkerKind == CaptureRunMarkerKind.Initialization
                ? CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization)
                : CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady);
        }
    }
}
