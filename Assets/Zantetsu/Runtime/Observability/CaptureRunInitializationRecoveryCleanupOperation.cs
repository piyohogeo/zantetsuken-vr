using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free cleanup operation that correlates one cleanup
    /// step of a recovery action plan to its exact target path under the held
    /// recovery lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation holds exactly three values: the action plan, the marker
    /// path set, and the step index. The target path is derived only from the
    /// authoritative <see cref="CaptureRunMarkerPathSet"/> and
    /// <see cref="CaptureRunRootLayout"/>, never regenerated or normalized. A
    /// temporary-deletion step targets the fixed <c>run.init.tmp</c> or
    /// <c>run.ready.tmp</c> entry; a root-removal step targets the stored Run
    /// root. <see cref="IsValid"/> recomputes every check from the held values
    /// without throwing, including after the lock lease has been released.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryCleanupOperation
    {
        private readonly CaptureRunInitializationRecoveryActionPlan _actionPlan;
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly int _stepIndex;

        internal CaptureRunInitializationRecoveryCleanupOperation(
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

            if (!ReferenceEquals(markerPaths.RootLayout, actionPlan.RootLayout))
            {
                throw new ArgumentException("Marker path set must share the action plan's root layout.", nameof(markerPaths));
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

            if (step.Action != CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary
                && step.Action != CaptureRunInitializationRecoveryAction.RemoveEmptyRoot)
            {
                throw new ArgumentException("Step action must be DeleteMarkerTemporary or RemoveEmptyRoot.", nameof(stepIndex));
            }

            CaptureRunInitializationRecoveryInspectionOperation inspection = actionPlan.Decision.Snapshot.Operation;
            if (inspection == null
                || !inspection.IsValid
                || !ReferenceEquals(inspection.RootLayout, actionPlan.RootLayout))
            {
                throw new ArgumentException("Inspection operation must be valid and share the action plan's root layout.", nameof(actionPlan));
            }

            if (!markerPaths.IsValid)
            {
                throw new ArgumentException("Marker path set paths must match the fixed paths derived from the root layout.", nameof(markerPaths));
            }

            if (string.IsNullOrEmpty(ComputeTargetPath(markerPaths, step)))
            {
                throw new ArgumentException("Target path must match a fixed marker or root path.", nameof(stepIndex));
            }

            _actionPlan = actionPlan;
            _markerPaths = markerPaths;
            _stepIndex = stepIndex;
        }

        internal CaptureRunInitializationRecoveryActionPlan ActionPlan => _actionPlan;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal int StepIndex => _stepIndex;

        internal CaptureRunInitializationRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunInitializationRecoveryAction Action => Step.Action;

        internal CaptureRunRootRole RootRole => Step.RootRole;

        internal CaptureRunMarkerKind MarkerKind => Step.MarkerKind;

        internal string TargetPath => ComputeTargetPath(_markerPaths, Step);

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => InspectionOperation.LockIdentityEvidence;

        internal long TestRunId => _actionPlan.TestRunId;

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || !_actionPlan.IsValid || _markerPaths == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_markerPaths.RootLayout, _actionPlan.RootLayout))
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

                if (step.Action != CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary
                    && step.Action != CaptureRunInitializationRecoveryAction.RemoveEmptyRoot)
                {
                    return false;
                }

                CaptureRunInitializationRecoveryInspectionOperation inspection = InspectionOperation;
                if (inspection == null
                    || !inspection.IsValid
                    || !ReferenceEquals(inspection.RootLayout, _actionPlan.RootLayout))
                {
                    return false;
                }

                return _markerPaths.IsValid
                    && !string.IsNullOrEmpty(ComputeTargetPath(_markerPaths, step));
            }
        }

        private CaptureRunInitializationRecoveryInspectionOperation InspectionOperation =>
            _actionPlan.Decision.Snapshot.Operation;

        private static string ComputeTargetPath(
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunInitializationRecoveryStep step)
        {
            if (step.Action == CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary)
            {
                if (step.RootRole == CaptureRunRootRole.Staging)
                {
                    return step.MarkerKind == CaptureRunMarkerKind.Initialization
                        ? markerPaths.StagingInitializationTemporaryPath
                        : markerPaths.StagingReadyTemporaryPath;
                }

                return step.MarkerKind == CaptureRunMarkerKind.Initialization
                    ? markerPaths.FinalInitializationTemporaryPath
                    : markerPaths.FinalReadyTemporaryPath;
            }

            return step.RootRole == CaptureRunRootRole.Staging
                ? markerPaths.RootLayout.StagingRunRoot
                : markerPaths.RootLayout.FinalRunRoot;
        }
    }
}
