using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, fixed PngJson publication artifact recovery action plan
    /// derived from a shared PngJson artifact recovery decision before any side
    /// effect runs. It owns exactly one step array computed by its static
    /// atomic factory and the exact decision reference it was built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The static factory validates the decision exactly once through
    /// <see cref="PngJsonCapturePublicationArtifactRecoveryDecision.TryValidate"/>
    /// and builds the step sequence with the same issued token, so no caller can
    /// hand in a disposition or a step array and no double validation of the
    /// snapshot occurs. The step array is allocated exactly once at its exact
    /// length and is never exposed; <see cref="Count"/> and a range-checked
    /// <see cref="GetStep"/> are the only accessors.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> re-derives the expected step sequence from the held
    /// decision in one linear pass and compares the held steps, so structure
    /// corruption, entry substitution, decision substitution, step-array
    /// substitution or shortening, and a released owner all converge to
    /// <c>false</c> without throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, holds no lease, and is not
    /// an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryActionPlan
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryDecision _decision;
        private readonly CaptureRunPublicationArtifactRecoveryStep[] _steps;

        private PngJsonCapturePublicationArtifactRecoveryActionPlan(
            PngJsonCapturePublicationArtifactRecoveryDecision decision,
            CaptureRunPublicationArtifactRecoveryStep[] steps)
        {
            _decision = decision;
            _steps = steps;
        }

        /// <summary>
        /// Atomic validated factory: validates the decision once through its
        /// token, builds the exact-length step array with the same token, and
        /// assigns both fields only after every step is computed. The private
        /// constructor keeps the decision and step array unfabricable by
        /// callers.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryActionPlan Create(
            PngJsonCapturePublicationArtifactRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.TryValidate(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token))
            {
                throw new ArgumentException("Decision must be fully valid.", nameof(decision));
            }

            CaptureRunPublicationArtifactRecoveryStep[] steps = BuildSteps(decision, token);
            return new PngJsonCapturePublicationArtifactRecoveryActionPlan(decision, steps);
        }

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _decision;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _decision.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _decision.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _decision.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _decision.LockIdentityEvidence;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        internal string RunManifestContentSha256 => _decision.RunManifestContentSha256;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _decision.Disposition;

        internal int Count => _steps.Length;

        internal CaptureRunPublicationArtifactRecoveryStep GetStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the step count.");
            }

            return _steps[index];
        }

        internal ValidationToken AcquireValidationToken()
        {
            return ValidationToken.Acquire(this);
        }

        internal bool TryAcquireValidationToken(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        /// <summary>
        /// O(1), exception-safe check that the held decision graph still exposes
        /// a live lock identity evidence, so a released owner is detected
        /// without a full validation pass.
        /// </summary>
        internal bool IsDecisionLeaseLive()
        {
            try
            {
                if (_decision == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = _decision.Snapshot;
                if (snapshot == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionOperation operation = snapshot.Operation;
                if (operation == null)
                {
                    return false;
                }

                // Read the authority through the field-only accessor before any
                // forwarding getter, so a nulled authority converges to false
                // instead of a NullReferenceException.
                PngJsonCapturePublicationArtifactInspectionAuthority authority = operation.Authority;
                if (authority == null)
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence evidence = authority.LockIdentityEvidence;
                return evidence != null && evidence.IsValid;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Exception-safe recomputation with token issuance: validates the held
        /// decision once through <see cref="PngJsonCapturePublicationArtifactRecoveryDecision.TryValidate"/>,
        /// re-derives the expected step sequence with the same token, and
        /// returns the issued snapshot token only when the held steps match.
        /// The snapshot is never fully validated twice.
        /// </summary>
        private bool TryValidateCore(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token)
        {
            token = null;

            try
            {
                if (_decision == null || _steps == null)
                {
                    return false;
                }

                if (!_decision.TryValidate(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken issued))
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryDisposition disposition = _decision.Disposition;
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = _decision.Snapshot;
                int entryCount = snapshot.Count;

                bool matches;
                switch (disposition)
                {
                    case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                        matches = _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace, -1, CaptureRunPublicationArtifactKind.None);
                        break;

                    case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                        matches = MatchesPublishSteps(snapshot, issued, entryCount);
                        break;

                    case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                        matches = _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex, -1, CaptureRunPublicationArtifactKind.None);
                        break;

                    case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                        matches = _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup, -1, CaptureRunPublicationArtifactKind.None);
                        break;

                    case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                        matches = _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing, -1, CaptureRunPublicationArtifactKind.None);
                        break;

                    case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                        matches = _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing, -1, CaptureRunPublicationArtifactKind.None);
                        break;

                    case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                        matches = _steps.Length == 1
                            && StepMatches(_steps[0], CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision, -1, CaptureRunPublicationArtifactKind.None);
                        break;

                    default:
                        matches = false;
                        break;
                }

                if (!matches)
                {
                    return false;
                }

                token = issued;
                return true;
            }
            catch (Exception)
            {
                token = null;
                return false;
            }
        }

        internal bool IsValid
        {
            get
            {
                return TryValidateCore(out _);
            }
        }

        private static CaptureRunPublicationArtifactRecoveryStep[] BuildSteps(
            PngJsonCapturePublicationArtifactRecoveryDecision decision,
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token)
        {
            CaptureRunPublicationArtifactRecoveryDisposition disposition = decision.Disposition;

            switch (disposition)
            {
                case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                    return Single(CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace);

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                    return BuildPublishSteps(decision.Snapshot, token);

                case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                    return Single(CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex);

                case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                    return Single(CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup);

                case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                    return Single(CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing);

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                    return Single(CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing);

                case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                    return Single(CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision);

                default:
                    throw new ArgumentException("Disposition must be a defined artifact recovery disposition.", nameof(decision));
            }
        }

        private static CaptureRunPublicationArtifactRecoveryStep[] Single(
            CaptureRunPublicationArtifactRecoveryAction action)
        {
            return new[]
            {
                new CaptureRunPublicationArtifactRecoveryStep(action, -1, CaptureRunPublicationArtifactKind.None)
            };
        }

        private static CaptureRunPublicationArtifactRecoveryStep[] BuildPublishSteps(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token)
        {
            int entryCount = snapshot.Count;

            int publishableCount = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                {
                    throw new ArgumentException("Snapshot entry proof is no longer intact.", nameof(token));
                }

                if (IsPublishable(observation.FinalPngStatus, observation.StagingPngStatus))
                {
                    publishableCount++;
                }

                if (IsPublishable(observation.FinalSidecarStatus, observation.StagingSidecarStatus))
                {
                    publishableCount++;
                }
            }

            if (publishableCount == 0)
            {
                throw new ArgumentException(
                    "PublishMissingArtifacts must require at least one publishable artifact.",
                    nameof(snapshot));
            }

            CaptureRunPublicationArtifactRecoveryStep[] steps =
                new CaptureRunPublicationArtifactRecoveryStep[checked(publishableCount + 1)];

            int stepIndex = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                {
                    throw new ArgumentException("Snapshot entry proof is no longer intact.", nameof(token));
                }

                if (IsPublishable(observation.FinalPngStatus, observation.StagingPngStatus))
                {
                    steps[stepIndex] = new CaptureRunPublicationArtifactRecoveryStep(
                        CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Png);
                    stepIndex++;
                }

                if (IsPublishable(observation.FinalSidecarStatus, observation.StagingSidecarStatus))
                {
                    steps[stepIndex] = new CaptureRunPublicationArtifactRecoveryStep(
                        CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Sidecar);
                    stepIndex++;
                }
            }

            steps[stepIndex] = new CaptureRunPublicationArtifactRecoveryStep(
                CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts, -1, CaptureRunPublicationArtifactKind.None);

            return steps;
        }

        private bool MatchesPublishSteps(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token,
            int entryCount)
        {
            int stepIndex = 0;

            for (int i = 0; i < entryCount; i++)
            {
                if (!token.TryGetIssuedEntry(snapshot, i, out PngJsonCapturePublicationArtifactEntryObservation observation))
                {
                    return false;
                }

                if (IsPublishable(observation.FinalPngStatus, observation.StagingPngStatus))
                {
                    if (stepIndex >= _steps.Length
                        || !StepMatches(_steps[stepIndex], CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Png))
                    {
                        return false;
                    }

                    stepIndex++;
                }

                if (IsPublishable(observation.FinalSidecarStatus, observation.StagingSidecarStatus))
                {
                    if (stepIndex >= _steps.Length
                        || !StepMatches(_steps[stepIndex], CaptureRunPublicationArtifactRecoveryAction.PublishArtifact, i, CaptureRunPublicationArtifactKind.Sidecar))
                    {
                        return false;
                    }

                    stepIndex++;
                }
            }

            if (stepIndex >= _steps.Length
                || !StepMatches(_steps[stepIndex], CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts, -1, CaptureRunPublicationArtifactKind.None))
            {
                return false;
            }

            stepIndex++;

            return stepIndex == _steps.Length;
        }

        private static bool IsPublishable(
            CaptureRunPublicationEvidenceStatus finalStatus,
            CaptureRunPublicationEvidenceStatus stagingStatus)
        {
            return finalStatus == CaptureRunPublicationEvidenceStatus.Absent
                && stagingStatus == CaptureRunPublicationEvidenceStatus.MatchesExpected;
        }

        private static bool StepMatches(
            CaptureRunPublicationArtifactRecoveryStep step,
            CaptureRunPublicationArtifactRecoveryAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind artifactKind)
        {
            return step != null && step.Matches(action, entryIndex, artifactKind);
        }

        /// <summary>
        /// Validation proof minted only after the whole action plan validates
        /// once. It binds to the exact plan, decision, step array, and each
        /// step's reference and values, and exposes no proof array or lease.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan _plan;
            private readonly PngJsonCapturePublicationArtifactRecoveryDecision _decision;
            private readonly PngJsonCapturePublicationArtifactInspectionSnapshot _snapshot;
            private readonly PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken _snapshotToken;
            private readonly CaptureRunPublicationArtifactRecoveryDisposition _disposition;
            private readonly CaptureRunPublicationArtifactRecoveryStep[] _steps;
            private readonly StepProof[] _proof;

            private ValidationToken(
                PngJsonCapturePublicationArtifactRecoveryActionPlan plan,
                PngJsonCapturePublicationArtifactRecoveryDecision decision,
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
                PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken snapshotToken,
                CaptureRunPublicationArtifactRecoveryDisposition disposition,
                CaptureRunPublicationArtifactRecoveryStep[] steps,
                StepProof[] proof)
            {
                _plan = plan;
                _decision = decision;
                _snapshot = snapshot;
                _snapshotToken = snapshotToken;
                _disposition = disposition;
                _steps = steps;
                _proof = proof;
            }

            internal static bool TryAcquire(
                PngJsonCapturePublicationArtifactRecoveryActionPlan plan,
                out ValidationToken token)
            {
                token = null;

                if (plan == null)
                {
                    return false;
                }

                if (!plan.TryValidateCore(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken snapshotToken))
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryStep[] steps = plan._steps;
                StepProof[] proof = new StepProof[steps.Length];
                for (int i = 0; i < steps.Length; i++)
                {
                    proof[i] = new StepProof(steps[i]);
                }

                token = new ValidationToken(
                    plan,
                    plan._decision,
                    plan._decision.Snapshot,
                    snapshotToken,
                    plan._decision.Disposition,
                    steps,
                    proof);
                return true;
            }

            internal static ValidationToken Acquire(PngJsonCapturePublicationArtifactRecoveryActionPlan plan)
            {
                if (plan == null)
                {
                    throw new ArgumentNullException(nameof(plan));
                }

                if (!TryAcquire(plan, out ValidationToken token))
                {
                    throw new InvalidOperationException("Action plan must be fully valid before issuing a validation token.");
                }

                return token;
            }

            /// <summary>
            /// O(1), exception-safe shared binding predicate: confirms the
            /// exact plan, decision, snapshot, snapshot validation token, and
            /// step array references, the held disposition value, and that the
            /// owner lease is still live, without touching any step element.
            /// </summary>
            private bool IsBindingIntact(PngJsonCapturePublicationArtifactRecoveryActionPlan plan)
            {
                try
                {
                    if (plan == null || !ReferenceEquals(_plan, plan))
                    {
                        return false;
                    }

                    if (!plan.IsDecisionLeaseLive())
                    {
                        return false;
                    }

                    if (_decision == null || _snapshot == null || _snapshotToken == null)
                    {
                        return false;
                    }

                    PngJsonCapturePublicationArtifactRecoveryDecision decision = plan._decision;
                    if (!ReferenceEquals(_decision, decision))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(_snapshot, decision.Snapshot))
                    {
                        return false;
                    }

                    if (decision.Disposition != _disposition)
                    {
                        return false;
                    }

                    if (!_snapshotToken.IsIssuedForExactBindings(_snapshot))
                    {
                        return false;
                    }

                    CaptureRunPublicationArtifactRecoveryStep[] steps = plan._steps;
                    if (_steps == null || _proof == null || steps == null)
                    {
                        return false;
                    }

                    return ReferenceEquals(_steps, steps) && _steps.Length == _proof.Length;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>
            /// O(n), exception-safe whole-token check: confirms the shared
            /// bindings and then re-checks every step's reference and values.
            /// Never throws and never exposes a proof array or a lease.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationArtifactRecoveryActionPlan plan)
            {
                if (!IsBindingIntact(plan))
                {
                    return false;
                }

                try
                {
                    CaptureRunPublicationArtifactRecoveryStep[] steps = plan._steps;
                    for (int i = 0; i < steps.Length; i++)
                    {
                        if (!_proof[i].Matches(steps[i]))
                        {
                            return false;
                        }
                    }

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>
            /// O(1), exception-safe index-local publish input access: confirms
            /// the shared bindings, then re-verifies the target step's
            /// reference and Action/EntryIndex/ArtifactKind against the
            /// issuance proof, requires
            /// <see cref="CaptureRunPublicationArtifactRecoveryAction.PublishArtifact"/>,
            /// and obtains the exact entry observation through the captured
            /// snapshot token's index-local API. The snapshot is never fully
            /// validated, no other step is visited, and no token is re-issued.
            /// Both out parameters are assigned only on success.
            /// </summary>
            internal bool TryGetIssuedPublishInputs(
                PngJsonCapturePublicationArtifactRecoveryActionPlan plan,
                int stepIndex,
                out CaptureRunPublicationArtifactRecoveryStep step,
                out PngJsonCapturePublicationArtifactEntryObservation observation)
            {
                step = null;
                observation = null;

                if (!IsBindingIntact(plan))
                {
                    return false;
                }

                try
                {
                    CaptureRunPublicationArtifactRecoveryStep[] steps = plan._steps;
                    if (stepIndex < 0 || stepIndex >= steps.Length)
                    {
                        return false;
                    }

                    CaptureRunPublicationArtifactRecoveryStep issuedStep = steps[stepIndex];
                    if (issuedStep == null || !_proof[stepIndex].Matches(issuedStep))
                    {
                        return false;
                    }

                    if (issuedStep.Action != CaptureRunPublicationArtifactRecoveryAction.PublishArtifact)
                    {
                        return false;
                    }

                    int entryIndex = issuedStep.EntryIndex;
                    if (entryIndex < 0)
                    {
                        return false;
                    }

                    if (!_snapshotToken.TryGetIssuedEntry(_snapshot, entryIndex, out PngJsonCapturePublicationArtifactEntryObservation issuedObservation))
                    {
                        return false;
                    }

                    if (issuedObservation.EntryIndex != entryIndex)
                    {
                        return false;
                    }

                    step = issuedStep;
                    observation = issuedObservation;
                    return true;
                }
                catch (Exception)
                {
                    step = null;
                    observation = null;
                    return false;
                }
            }
        }

        /// <summary>
        /// Immutable snapshot of one step's exact reference and every value
        /// field, captured once at token issuance so a later reference or value
        /// substitution of a step is detected in O(1) per step.
        /// </summary>
        private readonly struct StepProof
        {
            private readonly CaptureRunPublicationArtifactRecoveryStep _step;
            private readonly CaptureRunPublicationArtifactRecoveryAction _action;
            private readonly int _entryIndex;
            private readonly CaptureRunPublicationArtifactKind _artifactKind;

            internal StepProof(CaptureRunPublicationArtifactRecoveryStep step)
            {
                _step = step;
                _action = step.Action;
                _entryIndex = step.EntryIndex;
                _artifactKind = step.ArtifactKind;
            }

            internal bool Matches(CaptureRunPublicationArtifactRecoveryStep step)
            {
                return step != null
                    && ReferenceEquals(_step, step)
                    && _action == step.Action
                    && _entryIndex == step.EntryIndex
                    && _artifactKind == step.ArtifactKind;
            }
        }
    }
}
