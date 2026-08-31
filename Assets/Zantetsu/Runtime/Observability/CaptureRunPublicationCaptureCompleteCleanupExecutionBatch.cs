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
        /// <c>CaptureCompleteReady</c> position. The returned token is non-null
        /// only when every check succeeds and is bound to this exact batch and
        /// its prepared-step array, so the execution coordinator cannot reuse a
        /// token minted for one batch as proof of another.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        /// <summary>
        /// Proof that this exact execution batch and its prepared-step array
        /// were fully validated at a single point in time. The token is bound
        /// to the batch by reference, carries the action plan token, binds to
        /// the exact prepared-step array, and holds a defensive per-step proof
        /// snapshot, so both whole-array replacement and in-place element
        /// substitution after issuance fail closed.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly CaptureRunPublicationCaptureCompleteCleanupExecutionBatch _batch;
            private readonly CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken _actionPlanToken;
            private readonly CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] _issuedStepsArray;
            private readonly PreparedStepProof[] _issuedStepProofs;

            private ValidationToken(
                CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
                CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken actionPlanToken,
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] issuedStepsArray,
                PreparedStepProof[] issuedStepProofs)
            {
                _batch = batch;
                _actionPlanToken = actionPlanToken;
                _issuedStepsArray = issuedStepsArray;
                _issuedStepProofs = issuedStepProofs;
            }

            internal CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken ActionPlanToken => _actionPlanToken;

            /// <summary>
            /// Single validated mint: re-runs the batch's full sequence
            /// validation exactly once and only then captures the defensive
            /// proof snapshot. The private constructor keeps the token
            /// unfabricable by callers.
            /// </summary>
            internal static bool TryAcquire(
                CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch,
                out ValidationToken token)
            {
                token = null;
                if (batch == null || batch._actionPlan == null || batch._steps == null)
                {
                    return false;
                }

                if (!batch._actionPlan.TryValidate(out CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken actionPlanToken))
                {
                    return false;
                }

                if (!batch.IsValidatedSequence(actionPlanToken))
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] steps = batch._steps;
                PreparedStepProof[] proofs = new PreparedStepProof[steps.Length];
                for (int i = 0; i < proofs.Length; i++)
                {
                    proofs[i] = new PreparedStepProof(steps[i]);
                }

                token = new ValidationToken(batch, actionPlanToken, steps, proofs);
                return true;
            }

            /// <summary>
            /// Exception-safe check that this token was minted for the given
            /// batch, that the batch still holds the exact prepared-step array,
            /// and that each prepared step is still the same instance with the
            /// same step index, action, and cleanup operation as at mint time.
            /// Never throws and never exposes the prepared-step array.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch)
            {
                if (batch == null || !ReferenceEquals(_batch, batch) || batch._steps == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_issuedStepsArray, batch._steps))
                {
                    return false;
                }

                PreparedStepProof[] proofs = _issuedStepProofs;
                CaptureRunPublicationCaptureCompleteCleanupPreparedStep[] current = batch._steps;
                if (proofs == null || proofs.Length != current.Length)
                {
                    return false;
                }

                for (int i = 0; i < proofs.Length; i++)
                {
                    CaptureRunPublicationCaptureCompleteCleanupPreparedStep step = current[i];
                    PreparedStepProof proof = proofs[i];
                    if (step == null || !ReferenceEquals(step, proof.Step))
                    {
                        return false;
                    }

                    if (!step.MatchesIssuedProof(proof.StepIndex, proof.Action, proof.Operation))
                    {
                        return false;
                    }
                }

                return true;
            }

            private readonly struct PreparedStepProof
            {
                internal readonly CaptureRunPublicationCaptureCompleteCleanupPreparedStep Step;
                internal readonly int StepIndex;
                internal readonly CaptureRunPublicationCaptureCompleteCleanupAction Action;
                internal readonly CaptureRunPublicationCaptureCompleteCleanupOperation Operation;

                internal PreparedStepProof(CaptureRunPublicationCaptureCompleteCleanupPreparedStep step)
                {
                    Step = step;
                    if (step != null
                        && step.TryGetIssuedIdentity(
                            out int stepIndex,
                            out CaptureRunPublicationCaptureCompleteCleanupAction action,
                            out CaptureRunPublicationCaptureCompleteCleanupOperation operation))
                    {
                        StepIndex = stepIndex;
                        Action = action;
                        Operation = operation;
                    }
                    else
                    {
                        StepIndex = -1;
                        Action = default(CaptureRunPublicationCaptureCompleteCleanupAction);
                        Operation = null;
                    }
                }
            }
        }

        private bool IsValidatedSequence(CaptureRunPublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
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
