using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free release operation that targets the exact
    /// recovery open outcome held by a valid capture-complete lifecycle
    /// evidence, for the final owner release boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is minted only by <see cref="From"/> from a valid
    /// <see cref="CaptureRunPublicationCaptureCompleteLifecycleEvidence"/> whose
    /// <see cref="CaptureRunPublicationCaptureCompleteLifecycleOwnerKind"/> is
    /// <see cref="CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.RecoveryOpenOutcome"/>.
    /// It holds and forwards the evidence, the exact notification result, the
    /// exact open outcome, and the exact lock lease, and never owns or
    /// disposes the outcome or the lease.
    /// </para>
    /// <para>
    /// Construction validates in a fixed order: null evidence, valid evidence,
    /// recovery owner kind, absent fresh receipt/session/draft/artifact
    /// references, a created outcome, publication-recovery-required status, no
    /// session, the exact provenance open outcome, root layout / lock lease /
    /// test run id / run initialization id correlation, a created lock lease,
    /// and a shared lock path set. Fields are stored only after every check
    /// succeeds.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the full issuance correlation without
    /// throwing, so it becomes <c>false</c> once the lease is released. The
    /// separate <see cref="CanRelease"/> predicate distinguishes the
    /// post-issuance retryable condition: the exact open outcome must still be
    /// created even when the lease is no longer created after a partial release
    /// failure. There is no mutable completion flag; state is derived from the
    /// current owner.
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
        /// Opaque proof minted only inside <see cref="From"/> after the full
        /// issuance validation. It binds to this exact operation's private
        /// issuance nonce and to the issuance-time evidence, notification
        /// result, open outcome, and lock lease, so it cannot be reused for a
        /// different operation — even one built from the same evidence — and
        /// cannot be minted for arbitrary references.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly object _nonce;
            private readonly CaptureRunPublicationCaptureCompleteLifecycleEvidence _evidence;
            private readonly CaptureRunPublicationCaptureCompleteNotificationResult _notificationResult;
            private readonly CaptureRunInitializationOpenOutcome _openOutcome;
            private readonly CaptureRunLockLease _lockLease;

            private IssuanceProof(
                object nonce,
                CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence,
                CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
                CaptureRunInitializationOpenOutcome openOutcome,
                CaptureRunLockLease lockLease)
            {
                _nonce = nonce;
                _evidence = evidence;
                _notificationResult = notificationResult;
                _openOutcome = openOutcome;
                _lockLease = lockLease;
            }

            internal static IssuanceProof Acquire(
                CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                return new IssuanceProof(
                    operation._issuanceNonce,
                    operation._lifecycleEvidence,
                    operation._notificationResult,
                    operation._openOutcome,
                    operation._lockLease);
            }

            internal bool IsIssuedFor(CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
            {
                return operation != null
                    && ReferenceEquals(operation._issuanceNonce, _nonce)
                    && ReferenceEquals(operation._lifecycleEvidence, _evidence)
                    && ReferenceEquals(operation._notificationResult, _notificationResult)
                    && ReferenceEquals(operation._openOutcome, _openOutcome)
                    && ReferenceEquals(operation._lockLease, _lockLease);
            }
        }

        private readonly CaptureRunPublicationCaptureCompleteLifecycleEvidence _lifecycleEvidence;
        private readonly CaptureRunPublicationCaptureCompleteNotificationResult _notificationResult;
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunLockLease _lockLease;
        private readonly object _issuanceNonce;
        private readonly IssuanceProof _issuanceProof;

        private CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation(
            CaptureRunPublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            _lifecycleEvidence = lifecycleEvidence;
            _notificationResult = lifecycleEvidence.NotificationResult;
            _openOutcome = lifecycleEvidence.OpenOutcome;
            _lockLease = lifecycleEvidence.LockLease;
            _issuanceNonce = new object();
            _issuanceProof = IssuanceProof.Acquire(this);
        }

        internal static CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation From(
            CaptureRunPublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            if (lifecycleEvidence == null)
            {
                throw new ArgumentNullException(nameof(lifecycleEvidence));
            }

            if (!IsCorrelated(lifecycleEvidence))
            {
                throw new ArgumentException(
                    "Lifecycle evidence must be recovery-owner correlated and releasable.",
                    nameof(lifecycleEvidence));
            }

            return new CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation(lifecycleEvidence);
        }

        internal CaptureRunPublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _lifecycleEvidence;

        internal CaptureRunPublicationCaptureCompleteNotificationResult NotificationResult => _notificationResult;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;

        internal CaptureRunLockLease LockLease => _lockLease;

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
        /// must still bind to this exact operation and the exact open outcome
        /// must still be created. It intentionally does not require the lock
        /// lease to be created, so a partially released lease can be retried.
        /// </summary>
        internal bool CanRelease
        {
            get
            {
                if (!IsIssuanceProofIntact)
                {
                    return false;
                }

                CaptureRunInitializationOpenOutcome openOutcome = _openOutcome;
                return openOutcome != null && openOutcome.IsCreated;
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
            if (openOutcome == null || !openOutcome.IsCreated)
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

            if (!ReferenceEquals(notificationResult.LockLease, lifecycleEvidence.LockLease))
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

            CaptureRunLockLease lockLease = notificationResult.LockLease;
            if (lockLease == null || !lockLease.IsCreated)
            {
                return false;
            }

            if (lockLease.PathSet == null
                || openOutcome.LockPathSet == null
                || !ReferenceEquals(lockLease.PathSet, openOutcome.LockPathSet))
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
