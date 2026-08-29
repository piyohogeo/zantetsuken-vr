using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable preflight batch that materializes every step of a recovery
    /// action plan into a concrete prepared step before any filesystem change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor is the sole owner of the prepared-step array: it
    /// allocates the exact-length array once and fills it directly. The array
    /// is never exposed. <see cref="IsValid"/> recomputes the full sequence
    /// from the held values without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, calls no backend, provisioner,
    /// or writer, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryExecutionBatch
    {
        private readonly CaptureRunInitializationRecoveryActionPlan _actionPlan;
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly CaptureRunInitializationRecoveryPreparedStep[] _steps;

        internal CaptureRunInitializationRecoveryExecutionBatch(
            CaptureRunInitializationRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.IsValid)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan));
            }

            CaptureRunInitializationRecoveryInspectionOperation inspection = actionPlan.Decision.Snapshot.Operation;
            if (inspection == null
                || !inspection.IsValid
                || !ReferenceEquals(inspection.RootLayout, actionPlan.RootLayout))
            {
                throw new ArgumentException("Inspection operation must be valid and share the action plan's root layout.", nameof(actionPlan));
            }

            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(actionPlan.RootLayout);

            if (!markerPaths.IsValid)
            {
                throw new InvalidOperationException("Marker path set must be valid.");
            }

            int count = actionPlan.Count;
            CaptureRunInitializationRecoveryPreparedStep[] steps = new CaptureRunInitializationRecoveryPreparedStep[count];
            for (int i = 0; i < count; i++)
            {
                steps[i] = new CaptureRunInitializationRecoveryPreparedStep(actionPlan, markerPaths, i);
            }

            _actionPlan = actionPlan;
            _markerPaths = markerPaths;
            _steps = steps;
        }

        internal CaptureRunInitializationRecoveryActionPlan ActionPlan => _actionPlan;

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal int Count => _steps.Length;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockLease LockLease => _actionPlan.Decision.Snapshot.Operation.LockLease;

        internal long TestRunId => _actionPlan.TestRunId;

        internal CaptureRunMarkerBinding ExpectedBinding => _actionPlan.Decision.ExpectedBinding;

        internal CaptureRunInitializationRecoveryPreparedStep GetPreparedStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Prepared step index out of range.");
            }

            return _steps[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || !_actionPlan.IsValid || _markerPaths == null || _steps == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_markerPaths.RootLayout, _actionPlan.RootLayout) || !_markerPaths.IsValid)
                {
                    return false;
                }

                if (_steps.Length != _actionPlan.Count)
                {
                    return false;
                }

                for (int i = 0; i < _steps.Length; i++)
                {
                    CaptureRunInitializationRecoveryPreparedStep prepared = _steps[i];
                    if (prepared == null || !prepared.IsValid)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(prepared.ActionPlan, _actionPlan)
                        || !ReferenceEquals(prepared.MarkerPaths, _markerPaths)
                        || prepared.StepIndex != i)
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
