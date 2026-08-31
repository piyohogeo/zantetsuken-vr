using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable preflight batch that materializes every step of a
    /// capture-complete cleanup action plan into a concrete prepared step
    /// before any filesystem change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor validates the plan once, reuses the single validation
    /// token to construct every prepared step in fixed ascending order, and
    /// shares one publication path set and one marker path set across all
    /// steps. It allocates the exact-length prepared-step array once and never
    /// exposes it. <see cref="IsValid"/> and <see cref="TryValidate"/> re-run
    /// the full plan validation exactly once and then delegate to per-step
    /// index-local predicates, keeping the whole batch O(n) in the step count.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, calls no cleanup backend, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupExecutionBatch
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupActionPlan _actionPlan;
        private readonly CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] _steps;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionBatch(
            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token))
            {
                throw new ArgumentException("Action plan must be a valid capture-complete cleanup plan.", nameof(actionPlan));
            }

            if (!token.IsIssuedFor(actionPlan))
            {
                throw new ArgumentException("Validation token must be issued for the action plan.", nameof(actionPlan));
            }

            if (actionPlan.LockLease == null || !actionPlan.LockLease.IsCreated)
            {
                throw new ArgumentException("Action plan lock lease must be live.", nameof(actionPlan));
            }

            CaptureRunPublicationPathSet publicationPaths =
                actionPlan.OrchestrationResult.InspectionSnapshot.Decision.Snapshot.Operation.PublicationPaths;
            if (publicationPaths == null
                || !ReferenceEquals(publicationPaths.RootLayout, actionPlan.RootLayout)
                || !publicationPaths.IsValid)
            {
                throw new ArgumentException("Publication path set must be valid and share the action plan's root layout.", nameof(actionPlan));
            }

            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(actionPlan.RootLayout);
            if (!markerPaths.IsValid)
            {
                throw new InvalidOperationException("Marker path set must be valid.");
            }

            int count = checked(actionPlan.Count);
            CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps =
                new CaptureRunPublicationCaptureCompleteCleanupPreparedStep[count];
            for (int i = 0; i < count; i++)
            {
                steps[i] = new CaptureRunPublicationCaptureCompleteCleanupPreparedStep(
                    actionPlan, publicationPaths, markerPaths, i, token);
            }

            _actionPlan = actionPlan;
            _steps = steps;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupActionPlan ActionPlan => _actionPlan;

        internal CaptureRunPublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _actionPlan.OrchestrationResult;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _actionPlan.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal CaptureRunLockLease LockLease => _actionPlan.LockLease;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal int Count => _steps.Length;

        internal CaptureRunPublicationCaptureCompleteCleanupPreparedStep GetStep(int index)
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
        /// Performs the full plan validation and token acquisition exactly once,
        /// then verifies the prepared-step array length, per-step correlation,
        /// shared path set identity, step order, and terminal
        /// <c>CaptureCompleteReady</c> position. The returned token can be
        /// reused by the execution coordinator.
        /// </summary>
        internal bool TryValidate(out CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            token = null;

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
                return false;
            }

            if (_steps.Length == 0)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupPreparedStep first = _steps[0];
            if (first == null)
            {
                return false;
            }

            CaptureRunPublicationPathSet publicationPaths = first.PublicationPaths;
            CaptureRunMarkerPathSet markerPaths = first.MarkerPaths;
            if (publicationPaths == null || markerPaths == null)
            {
                return false;
            }

            if (!ReferenceEquals(publicationPaths.RootLayout, _actionPlan.RootLayout)
                || !publicationPaths.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(markerPaths.RootLayout, _actionPlan.RootLayout)
                || !markerPaths.IsValid)
            {
                return false;
            }

            int last = _steps.Length - 1;
            for (int i = 0; i < _steps.Length; i++)
            {
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep prepared = _steps[i];
                if (prepared == null || !prepared.IsValidIndexLocal(token))
                {
                    return false;
                }

                if (prepared.StepIndex != i)
                {
                    return false;
                }

                if (!ReferenceEquals(prepared.PublicationPaths, publicationPaths)
                    || !ReferenceEquals(prepared.MarkerPaths, markerPaths))
                {
                    return false;
                }

                if (i < last)
                {
                    if (prepared.Action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                    {
                        return false;
                    }
                }
                else if (prepared.Action != CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// O(1), exception-safe check that the batch's core structure and the
        /// plan's nested inspection structure are intact, so a coordinator can
        /// safely confirm a possibly-stale token still maps to a readable batch.
        /// </summary>
        internal bool IsIndexLocalStructureIntact()
        {
            if (_actionPlan == null || _steps == null)
            {
                return false;
            }

            return _actionPlan.IsIndexLocalStructureIntact();
        }
    }
}
