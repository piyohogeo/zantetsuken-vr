using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one orchestrated PngJson capture-complete cleanup
    /// pass: the coordinator that issued it, the exact cleanup execution result
    /// it produced, and the opaque proof from that execution result's single
    /// full validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly three read-only reference fields and has no public
    /// constructor; the only way to build one is through the atomic factory
    /// called by
    /// <see cref="PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator.Execute"/>.
    /// It holds no plan, batch, recovery result, authority, root layout, or run
    /// identity as a duplicate field — every accessor forwards a value from the
    /// correlated execution result graph, and the held proof is never exposed.
    /// <see cref="IsValid"/> recomputes the full correlation without throwing,
    /// so a result whose nested values were forged, whose lease was released,
    /// or whose held values became otherwise invalid reports <c>false</c>
    /// instead of throwing.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing — no array, receipt,
    /// ownership lease, raw lease, stream, or byte sequence — and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult _executionResult;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken _token;

        private PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult executionResult,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token)
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
        internal static PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult Create(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult executionResult,
            PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken token)
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

            return new PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult(issuedBy, executionResult, token);
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _executionResult;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch Batch => _executionResult.Batch;

        internal PngJsonCapturePublicationCaptureCompleteCleanupActionPlan ActionPlan => _executionResult.ActionPlan;

        internal PngJsonCapturePublicationArtifactRecoveryOrchestrationResult OrchestrationResult => _executionResult.OrchestrationResult;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _executionResult.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _executionResult.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _executionResult.AuthoritativePlan;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _executionResult.Status;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _executionResult.OrchestrationResult.Disposition;

        internal CaptureRunRootLayout RootLayout => _executionResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _executionResult.LockIdentityEvidence;

        internal long TestRunId => _executionResult.TestRunId;

        internal string RunInitializationId => _executionResult.RunInitializationId;

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
        /// plan, and recovery result reference chain, the proof's exact binding,
        /// the capture-complete-ready status, the allowed disposition, and the
        /// authority/plan/root layout/identity evidence/run identity forwarding
        /// correlation. It never re-issues a token, re-validates the plan, or
        /// scans an entry.
        /// </summary>
        private bool IsFullyValid()
        {
            try
            {
                if (_issuedBy == null || _executionResult == null || _token == null)
                {
                    return false;
                }

                if (!_executionResult.IsValidWithToken(_token))
                {
                    return false;
                }

                if (!ReferenceEquals(_executionResult.IssuedBy, _issuedBy.ExecutionCoordinator))
                {
                    return false;
                }

                PngJsonCapturePublicationCaptureCompleteCleanupExecutionBatch batch = _executionResult.Batch;
                if (batch == null)
                {
                    return false;
                }

                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan plan = batch.ActionPlan;
                if (plan == null || !ReferenceEquals(plan, _executionResult.ActionPlan))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactRecoveryOrchestrationResult recovery = plan.OrchestrationResult;
                if (recovery == null || !ReferenceEquals(recovery, _executionResult.OrchestrationResult))
                {
                    return false;
                }

                if (_executionResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
                {
                    return false;
                }

                if (!IsAllowedDisposition(recovery.Disposition))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = plan.Authority;
                if (authority == null || !ReferenceEquals(authority, _executionResult.Authority))
                {
                    return false;
                }

                PngJsonCapturePublicationPlan authoritativePlan = plan.AuthoritativePlan;
                if (authoritativePlan == null || !ReferenceEquals(authoritativePlan, _executionResult.AuthoritativePlan))
                {
                    return false;
                }

                CaptureRunRootLayout rootLayout = plan.RootLayout;
                if (rootLayout == null || !ReferenceEquals(rootLayout, _executionResult.RootLayout))
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = plan.LockIdentityEvidence;
                if (lockIdentityEvidence == null
                    || !lockIdentityEvidence.IsValid
                    || !ReferenceEquals(lockIdentityEvidence, _executionResult.LockIdentityEvidence))
                {
                    return false;
                }

                if (plan.TestRunId != _executionResult.TestRunId
                    || !string.Equals(
                        plan.RunInitializationId,
                        _executionResult.RunInitializationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAllowedDisposition(CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            return disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex
                || disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
        }

        /// <summary>
        /// Opaque proof minted only after this exact orchestration result
        /// validates once through the shared predicate. It snapshots the exact
        /// result, coordinator, execution result, and execution result proof
        /// references at issuance, and O(1) re-confirms each against the
        /// current result. It exposes no proof array or internal token getter.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult _result;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator _issuedBy;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult _executionResult;
            private readonly PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken _executionResultToken;

            private ValidationToken(
                PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult result,
                PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationCoordinator issuedBy,
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult executionResult,
                PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult.ValidationToken executionResultToken)
            {
                _result = result;
                _issuedBy = issuedBy;
                _executionResult = executionResult;
                _executionResultToken = executionResultToken;
            }

            /// <summary>
            /// Validated mint: runs the exact result's single shared full
            /// validation predicate and issues a token only on success. The
            /// private constructor keeps the proof unfabricable by callers
            /// outside this token.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult result,
                out ValidationToken token)
            {
                token = null;

                if (result == null || !result.IsFullyValid())
                {
                    return false;
                }

                token = new ValidationToken(
                    result, result._issuedBy, result._executionResult, result._token);
                return true;
            }

            /// <summary>
            /// O(1), exception-safe exact binding: re-confirms the exact result,
            /// coordinator, execution result, and execution result proof
            /// references captured at issuance against the current result. It
            /// never re-validates the result, re-issues a token, or scans an
            /// entry.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult result)
            {
                try
                {
                    return result != null
                        && ReferenceEquals(_result, result)
                        && ReferenceEquals(_issuedBy, result._issuedBy)
                        && ReferenceEquals(_executionResult, result._executionResult)
                        && ReferenceEquals(_executionResultToken, result._token);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }
    }
}
