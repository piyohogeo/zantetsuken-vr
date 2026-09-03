using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one orchestrated PngJson capture publication
    /// artifact recovery pass: the coordinator that issued it, the execution
    /// result it produced, and the opaque proof from that result's single full
    /// validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly three read-only reference fields and has no public
    /// constructor; the only way to build one is through the atomic factory
    /// called by
    /// <see cref="PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator.Execute"/>.
    /// Every accessor forwards a value from the correlated execution result
    /// graph, and the held proof is never exposed. <see cref="IsValid"/>
    /// recomputes the full correlation without throwing, so a result whose
    /// nested values were forged, whose lease was released, or whose held
    /// values became otherwise invalid reports <c>false</c> instead of
    /// throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — no array, lease, stream,
    /// or byte sequence — and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryOrchestrationResult
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationArtifactRecoveryExecutionResult _executionResult;
        private readonly PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken _token;

        private PngJsonCapturePublicationArtifactRecoveryOrchestrationResult(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator issuedBy,
            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult,
            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken token)
        {
            _issuedBy = issuedBy;
            _executionResult = executionResult;
            _token = token;
        }

        /// <summary>
        /// Atomic issuance gate: null-checks every input and confirms the
        /// already-issued proof's O(1) exact binding plus the coordinator
        /// correlation only. It never re-validates the execution result,
        /// re-issues a token, re-serializes canonical bytes, or scans an entry.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryOrchestrationResult Create(
            PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator issuedBy,
            PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult,
            PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken token)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (executionResult == null)
            {
                throw new ArgumentNullException(nameof(executionResult));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!token.IsIssuedFor(executionResult)
                || !ReferenceEquals(executionResult.IssuedBy, issuedBy.ExecutionCoordinator))
            {
                throw new ArgumentException(
                    "Execution result must be correlated with the issuing orchestration coordinator.",
                    nameof(executionResult));
            }

            return new PngJsonCapturePublicationArtifactRecoveryOrchestrationResult(issuedBy, executionResult, token);
        }

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationArtifactRecoveryExecutionResult ExecutionResult => _executionResult;

        internal PngJsonCapturePublicationArtifactInspectionSnapshot InspectionSnapshot =>
            _executionResult.Batch.ActionPlan.Decision.Snapshot;

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _executionResult.Decision;

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _executionResult.ActionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryExecutionBatch Batch => _executionResult.Batch;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _executionResult.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _executionResult.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _executionResult.AuthoritativePlan;

        internal CaptureRunPublicationArtifactRecoveryExecutionStatus Status => _executionResult.Status;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _executionResult.Disposition;

        internal CaptureRunRootLayout RootLayout => _executionResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _executionResult.LockIdentityEvidence;

        internal long TestRunId => _executionResult.TestRunId;

        internal string RunInitializationId => _executionResult.RunInitializationId;

        internal string RunManifestContentSha256 => _executionResult.RunManifestContentSha256;

        /// <summary>
        /// Full validation plus token issuance: delegates to the shared
        /// validated mint, so the only way to obtain a proof is through the
        /// exact full-validation predicate. A stale or corrupted result never
        /// produces a token.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        /// <summary>
        /// Exception-safe recomputation delegated to
        /// <see cref="TryValidate"/>.
        /// </summary>
        internal bool IsValid => TryValidate(out _);

        /// <summary>
        /// Exception-safe full re-validation with an already-issued proof:
        /// confirms the proof's O(1) exact binding to this instance and then
        /// re-runs the shared full-validation predicate, without issuing a new
        /// token. A foreign or null proof reports false without throwing.
        /// </summary>
        internal bool IsValidWithToken(ValidationToken token)
        {
            if (token == null || !token.IsIssuedFor(this))
            {
                return false;
            }

            return IsFullyValid();
        }

        /// <summary>
        /// Single shared full-validation predicate: the exact coordinator and
        /// execution coordinator, the exact execution result, batch, action
        /// plan, decision, and snapshot, the snapshot's exact inspector, the
        /// proof's exact binding, the authority/plan/root layout/identity
        /// evidence reference correlation, the owner liveness, and the
        /// status/disposition mapping. It never re-issues a token, re-validates
        /// the plan, or scans an entry.
        /// </summary>
        private bool IsFullyValid()
        {
            try
            {
                if (_issuedBy == null || _executionResult == null || _token == null)
                {
                    return false;
                }

                // Re-validate the current execution result with the already
                // held proof: the proof's O(1) exact binding plus the shared
                // full-validation predicate, without issuing a new token.
                if (!_executionResult.IsValidWithToken(_token))
                {
                    return false;
                }

                if (!ReferenceEquals(_executionResult.IssuedBy, _issuedBy.ExecutionCoordinator))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = _executionResult.Batch;
                if (batch == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactRecoveryActionPlan plan = batch.ActionPlan;
                if (plan == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactRecoveryDecision decision = plan.Decision;
                if (decision == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = decision.Snapshot;
                if (snapshot == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionOperation operation = snapshot.Operation;
                if (operation == null)
                {
                    return false;
                }

                if (!ReferenceEquals(snapshot.IssuedBy, _issuedBy.Inspector))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = operation.Authority;
                if (authority == null
                    || !ReferenceEquals(authority, plan.Authority)
                    || !ReferenceEquals(authority, batch.Authority)
                    || !ReferenceEquals(authority, _executionResult.Authority))
                {
                    return false;
                }

                PngJsonCapturePublicationPlan authoritativePlan = operation.Plan;
                if (authoritativePlan == null
                    || !ReferenceEquals(authoritativePlan, plan.AuthoritativePlan)
                    || !ReferenceEquals(authoritativePlan, batch.AuthoritativePlan)
                    || !ReferenceEquals(authoritativePlan, _executionResult.AuthoritativePlan))
                {
                    return false;
                }

                CaptureRunRootLayout rootLayout = operation.RootLayout;
                if (rootLayout == null
                    || !ReferenceEquals(rootLayout, decision.RootLayout)
                    || !ReferenceEquals(rootLayout, plan.RootLayout)
                    || !ReferenceEquals(rootLayout, batch.RootLayout)
                    || !ReferenceEquals(rootLayout, _executionResult.RootLayout))
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = operation.LockIdentityEvidence;
                if (lockIdentityEvidence == null
                    || !lockIdentityEvidence.IsValid
                    || !ReferenceEquals(lockIdentityEvidence, batch.LockIdentityEvidence)
                    || !ReferenceEquals(lockIdentityEvidence, _executionResult.LockIdentityEvidence))
                {
                    return false;
                }

                if (operation.TestRunId != _executionResult.TestRunId
                    || !string.Equals(
                        operation.RunInitializationId,
                        _executionResult.RunInitializationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return StatusMatchesDisposition(_executionResult.Status, decision.Disposition);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Opaque proof minted only after this exact orchestration result
        /// validates once through the shared predicate. It snapshots the exact
        /// coordinator, execution result, execution result proof, and snapshot
        /// references at issuance, and O(1) re-confirms each against the
        /// current result. It exposes no proof array or internal token getter.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationArtifactRecoveryOrchestrationResult _result;
            private readonly PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator _issuedBy;
            private readonly PngJsonCapturePublicationArtifactRecoveryExecutionResult _executionResult;
            private readonly PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken _executionResultToken;
            private readonly PngJsonCapturePublicationArtifactInspectionSnapshot _snapshot;

            private ValidationToken(
                PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result,
                PngJsonCapturePublicationArtifactRecoveryOrchestrationCoordinator issuedBy,
                PngJsonCapturePublicationArtifactRecoveryExecutionResult executionResult,
                PngJsonCapturePublicationArtifactRecoveryExecutionResult.ValidationToken executionResultToken,
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
            {
                _result = result;
                _issuedBy = issuedBy;
                _executionResult = executionResult;
                _executionResultToken = executionResultToken;
                _snapshot = snapshot;
            }

            /// <summary>
            /// Validated mint: runs the exact result's single shared full
            /// validation predicate and issues a token only on success. The
            /// private constructor keeps the proof unfabricable by callers
            /// outside this token.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result,
                out ValidationToken token)
            {
                token = null;

                if (result == null || !result.IsFullyValid())
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot;
                try
                {
                    snapshot = result.InspectionSnapshot;
                }
                catch (Exception)
                {
                    return false;
                }

                if (snapshot == null)
                {
                    return false;
                }

                token = new ValidationToken(
                    result, result._issuedBy, result._executionResult, result._token, snapshot);
                return true;
            }

            /// <summary>
            /// O(1), exception-safe exact binding: re-confirms the exact result,
            /// coordinator, execution result, execution result proof, and
            /// snapshot references captured at issuance against the current
            /// result. It never re-validates the result, re-issues a token, or
            /// scans an entry.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationArtifactRecoveryOrchestrationResult result)
            {
                try
                {
                    return result != null
                        && ReferenceEquals(_result, result)
                        && ReferenceEquals(_issuedBy, result._issuedBy)
                        && ReferenceEquals(_executionResult, result._executionResult)
                        && ReferenceEquals(_executionResultToken, result._token)
                        && ReferenceEquals(_snapshot, result.InspectionSnapshot);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static bool StatusMatchesDisposition(
            CaptureRunPublicationArtifactRecoveryExecutionStatus status,
            CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            CaptureRunPublicationArtifactRecoveryExecutionStatus expected;
            switch (disposition)
            {
                case CaptureRunPublicationArtifactRecoveryDisposition.PublishMissingArtifacts:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.ReinspectionRequired;
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex:
                case CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.CaptureCompleteCleanupRequired;
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.OrphanedPreTrace:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.OrphanedPreTrace;
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.ArtifactSourceMissing:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.ArtifactSourceMissing;
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.PublishedArtifactMissing:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.PublishedArtifactMissing;
                    break;

                case CaptureRunPublicationArtifactRecoveryDisposition.RunRootCollision:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.RunRootCollision;
                    break;

                default:
                    expected = CaptureRunPublicationArtifactRecoveryExecutionStatus.None;
                    break;
            }

            return status == expected;
        }
    }
}
