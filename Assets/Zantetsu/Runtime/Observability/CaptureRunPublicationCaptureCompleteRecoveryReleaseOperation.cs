using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free release operation that targets the exact
    /// ownership lease held by a valid capture-complete lifecycle evidence,
    /// for the final owner release boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is minted only by <see cref="From"/> from a valid
    /// <see cref="CaptureRunPublicationCaptureCompleteLifecycleEvidence"/> whose
    /// <see cref="CaptureRunPublicationCaptureCompleteLifecycleOwnerKind"/> is
    /// <see cref="CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.RecoveryOpenOutcome"/>.
    /// It holds and forwards the evidence, the exact notification result, the
    /// exact open outcome, and the exact ownership lease, and never owns or
    /// disposes the outcome or the ownership lease.
    /// </para>
    /// <para>
    /// Construction validates in a fixed order: null evidence, valid evidence,
    /// recovery owner kind, absent fresh receipt/session/draft/artifact
    /// references, a live open outcome, publication-recovery-required status,
    /// no session, the exact provenance open outcome, root layout / ownership
    /// lease / lock identity evidence / test run id / run initialization id
    /// correlation, a live ownership lease, and a shared lock path set. Fields
    /// are stored only after every check succeeds.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the full issuance correlation without
    /// throwing, so it becomes <c>false</c> once the ownership lease is even
    /// partially released. The separate <see cref="CanRelease"/> predicate
    /// distinguishes the post-issuance retryable condition: the issuance proof
    /// must still bind and the ownership lease must still be retryable via
    /// <see cref="CaptureRunInitializationSessionOwnershipLease.CanRelease"/>
    /// even after a partial release failure. There is no mutable completion
    /// flag; state is derived from the current owner.
    /// </para>
    /// <para>
    /// The nested <see cref="IssuanceProof"/> is an opaque correlation proof
    /// minted only inside <see cref="From"/>. Its constructor is private and
    /// it binds to this exact operation's private issuance nonce and to the
    /// issuance-time evidence, notification result, open outcome, and lock
    /// lease, so a proof cannot be reused for a different operation — even one
    /// built from the same evidence — and cannot be minted for arbitrary
    /// references.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, owns and disposes nothing, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation
    {
        /// <summary>
        /// Opaque proof minted only inside the atomic nested factory
        /// <see cref="Mint"/> after the full issuance validation. It binds to
        /// this exact operation's private issuance nonce and to the
        /// issuance-time evidence, notification result, open outcome, and lock
        /// lease, so it cannot be reused for a different operation — even one
        /// built from the same evidence — and it is never returned to callers.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly object _nonce;
            private readonly CaptureRunPublicationCaptureCompleteLifecycleEvidence _evidence;
            private readonly CaptureRunPublicationCaptureCompleteNotificationResult _notificationResult;
            private readonly CaptureRunInitializationOpenOutcome _openOutcome;
            private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;
            private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;

            private IssuanceProof(
                object nonce,
                CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence,
                CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunInitializationSessionOwnershipLease ownershipLease,
                CaptureRunLockIdentityEvidence lockIdentityEvidence)
            {
                _nonce = nonce;
                _evidence = evidence;
                _notificationResult = notificationResult;
                _openOutcome = openOutcome;
                _ownershipLease = ownershipLease;
                _lockIdentityEvidence = lockIdentityEvidence;
            }

            /// <summary>
            /// Atomic validated mint: fully validates the lifecycle evidence,
            /// generates the private per-operation nonce, mints the proof, and
            /// constructs the operation — returning only the operation and
            /// never exposing the proof. A proof can therefore only exist for
            /// an operation issued from a fully valid evidence.
            /// </summary>
            internal static CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation Mint(
                CaptureRunPublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
            {
                if (lifecycleEvidence == null)
                {
                    throw new ArgumentNullException(nameof(lifecycleEvidence));
                }

                if (!CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation.IsCorrelated(lifecycleEvidence))
                {
                    throw new ArgumentException(
                        "Lifecycle evidence must be recovery-owner correlated and releasable.",
                        nameof(lifecycleEvidence));
                }

                object nonce = new object();
                IssuanceProof proof = new IssuanceProof(
                    nonce,
                    lifecycleEvidence,
                    lifecycleEvidence.NotificationResult,
                    lifecycleEvidence.OpenOutcome,
                    lifecycleEvidence.OwnershipLease,
                    lifecycleEvidence.LockIdentityEvidence);

                return new CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation(lifecycleEvidence, nonce, proof);
            }

            internal bool IsIssuedFor(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                return operation != null
                    && ReferenceEquals(operation._issuanceNonce, _nonce)
                    && ReferenceEquals(operation._lifecycleEvidence, _evidence)
                    && ReferenceEquals(operation._notificationResult, _notificationResult)
                    && ReferenceEquals(operation._openOutcome, _openOutcome)
                    && ReferenceEquals(operation._ownershipLease, _ownershipLease)
                    && ReferenceEquals(operation._lockIdentityEvidence, _lockIdentityEvidence);
            }
        }

        private readonly CaptureRunPublicationCaptureCompleteLifecycleEvidence _lifecycleEvidence;
        private readonly CaptureRunPublicationCaptureCompleteNotificationResult _notificationResult;
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;
        private readonly object _issuanceNonce;
        private readonly IssuanceProof _issuanceProof;

        private CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation(
            CaptureRunPublicationCaptureCompleteLifecycleEvidence lifecycleEvidence,
            object issuanceNonce,
            IssuanceProof issuanceProof)
        {
            _lifecycleEvidence = lifecycleEvidence;
            _notificationResult = lifecycleEvidence.NotificationResult;
            _openOutcome = lifecycleEvidence.OpenOutcome;
            _ownershipLease = lifecycleEvidence.OwnershipLease;
            _lockIdentityEvidence = lifecycleEvidence.LockIdentityEvidence;
            _issuanceNonce = issuanceNonce;
            _issuanceProof = issuanceProof;
        }

        internal static CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation From(
            CaptureRunPublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            return IssuanceProof.Mint(lifecycleEvidence);
        }

        internal CaptureRunPublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _lifecycleEvidence;

        internal CaptureRunPublicationCaptureCompleteNotificationResult NotificationResult => _notificationResult;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        internal CaptureRunRootLayout RootLayout => _notificationResult.RootLayout;

        internal long TestRunId => _notificationResult.TestRunId;

        internal string RunInitializationId => _notificationResult.RunInitializationId;

        internal string RunManifestContentSha256 => _notificationResult.RunManifestContentSha256;

        internal string CaptureIndexPath => _notificationResult.CaptureIndexPath;

        /// <summary>
        /// Exception-safe predicate shared by <see cref="IsValid"/>,
        /// <see cref="CanRelease"/>, and the release receipt: the issuance
        /// proof must still bind to this exact operation and to its
        /// issuance-time evidence, notification result, open outcome, and lock
        /// lease. <c>false</c> when any of those references was swapped or
        /// forged after issuance.
        /// </summary>
        internal bool IsIssuanceProofIntact => _issuanceProof != null && _issuanceProof.IsIssuedFor(this);

        /// <summary>
        /// Exception-safe recomputation of the full issuance correlation.
        /// <c>false</c> once the lease is released or when the proof, evidence,
        /// outcome, lease, or notification result is forged, replaced, or
        /// corrupted.
        /// </summary>
        internal bool IsValid => IsIssuanceProofIntact && IsCorrelated(_lifecycleEvidence);

        /// <summary>
        /// Exception-safe retryable condition after issuance: the issuance proof
        /// must still bind to this exact operation and the ownership lease must
        /// still be retryable (not yet fully released). It intentionally does
        /// not require the lock lease to be fully created, so a partially
        /// released lease can be retried.
        /// </summary>
        internal bool CanRelease
        {
            get
            {
                if (!IsIssuanceProofIntact)
                {
                    return false;
                }

                CaptureRunInitializationSessionOwnershipLease ownershipLease = _ownershipLease;
                return ownershipLease != null && ownershipLease.CanRelease;
            }
        }

        private static bool IsCorrelated(
            CaptureRunPublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            if (lifecycleEvidence == null)
            {
                return false;
            }

            if (!lifecycleEvidence.IsValid)
            {
                return false;
            }

            if (lifecycleEvidence.Kind != CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.RecoveryOpenOutcome)
            {
                return false;
            }

            if (lifecycleEvidence.FreezeReceipt != null
                || lifecycleEvidence.RunSession != null
                || lifecycleEvidence.Drafts != null
                || lifecycleEvidence.Artifacts != null)
            {
                return false;
            }

            CaptureRunInitializationOpenOutcome openOutcome = lifecycleEvidence.OpenOutcome;
            if (openOutcome == null || !openOutcome.IsValid)
            {
                return false;
            }

            if (openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
            {
                return false;
            }

            if (openOutcome.Session != null)
            {
                return false;
            }

            CaptureRunInitializationSessionOwnershipLease ownershipLease = lifecycleEvidence.OwnershipLease;
            if (ownershipLease == null || !ownershipLease.IsCreated)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult = lifecycleEvidence.NotificationResult;
            if (notificationResult == null)
            {
                return false;
            }

            if (!ReferenceEquals(GetProvenanceOpenOutcome(notificationResult), openOutcome))
            {
                return false;
            }

            if (!ReferenceEquals(openOutcome.RootLayout, notificationResult.RootLayout))
            {
                return false;
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = notificationResult.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                return false;
            }

            if (!lockIdentityEvidence.IsIssuedFor(ownershipLease))
            {
                return false;
            }

            if (!ReferenceEquals(lockIdentityEvidence, lifecycleEvidence.LockIdentityEvidence))
            {
                return false;
            }

            if (openOutcome.TestRunId != notificationResult.TestRunId)
            {
                return false;
            }

            if (!string.Equals(openOutcome.RunInitializationId, notificationResult.RunInitializationId, StringComparison.Ordinal))
            {
                return false;
            }

            if (lockIdentityEvidence.LockPathSet == null
                || openOutcome.LockPathSet == null
                || !ReferenceEquals(lockIdentityEvidence.LockPathSet, openOutcome.LockPathSet)
                || !ReferenceEquals(ownershipLease.LockPathSet, openOutcome.LockPathSet))
            {
                return false;
            }

            return true;
        }

        private static CaptureRunInitializationOpenOutcome GetProvenanceOpenOutcome(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult)
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanupResult = notificationResult.CleanupResult;
            if (cleanupResult == null)
            {
                return null;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult recoveryResult = cleanupResult.OrchestrationResult;
            if (recoveryResult == null)
            {
                return null;
            }

            CaptureRunPublicationArtifactInspectionSnapshot artifactSnapshot = recoveryResult.InspectionSnapshot;
            if (artifactSnapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryDecision decision = artifactSnapshot.Decision;
            if (decision == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = decision.Snapshot;
            if (recoverySnapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = recoverySnapshot.Operation;
            if (operation == null)
            {
                return null;
            }

            return operation.OpenOutcome;
        }
    }
}
