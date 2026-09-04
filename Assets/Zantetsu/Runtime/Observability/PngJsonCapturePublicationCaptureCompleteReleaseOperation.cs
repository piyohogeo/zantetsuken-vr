using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free release operation that targets the exact
    /// ownership lease held by a valid PngJson capture-complete lifecycle
    /// evidence, shared by the Fresh and Recovery provenance paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type is minted only by <see cref="Create"/> from a valid
    /// <see cref="PngJsonCapturePublicationCaptureCompleteLifecycleEvidence"/>
    /// whose owner kind is <c>FreshSession</c> or <c>RecoveryOpenOutcome</c>.
    /// It holds and forwards the exact lifecycle evidence, notification result,
    /// ownership lease, and lock identity evidence, and never owns or disposes
    /// the lease.
    /// </para>
    /// <para>
    /// Construction validates in a fixed order: null evidence, valid evidence,
    /// a defined Fresh or Recovery owner kind, a present notification result,
    /// lock identity evidence, and ownership lease, and a retryable,
    /// not-yet-released ownership lease. Fields are stored only after every
    /// check succeeds.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the full issuance correlation without
    /// throwing, so it becomes <c>false</c> once the ownership lease is even
    /// partially released. The separate <see cref="CanRelease"/> predicate
    /// distinguishes the post-issuance retryable condition: the issuance
    /// references must still bind exactly and the ownership lease must still
    /// be retryable via
    /// <see cref="CaptureRunInitializationSessionOwnershipLease.CanRelease"/>
    /// even after a partial release failure. <see cref="IsReleaseComplete"/>
    /// forwards the exact ownership lease's
    /// <see cref="CaptureRunInitializationSessionOwnershipLease.IsReleaseComplete"/>.
    /// There is no mutable completion flag and no per-operation nonce; binding
    /// is derived from the exact held references, so any reference swapped
    /// after issuance fails closed.
    /// </para>
    /// <para>
    /// This type performs no filesystem, registry, cleanup backend, or
    /// notifier work, owns and disposes nothing, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteReleaseOperation
    {
        private readonly PngJsonCapturePublicationCaptureCompleteLifecycleEvidence _lifecycleEvidence;
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationResult _notificationResult;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;

        private PngJsonCapturePublicationCaptureCompleteReleaseOperation(
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence lifecycleEvidence,
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            _lifecycleEvidence = lifecycleEvidence;
            _notificationResult = notificationResult;
            _ownershipLease = ownershipLease;
            _lockIdentityEvidence = lockIdentityEvidence;
        }

        /// <summary>
        /// Atomic validated factory: the single issuance site. It null-checks
        /// the lifecycle evidence, verifies the issuance correlation, and then
        /// stores the exact references through the private assignment
        /// constructor.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteReleaseOperation Create(
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            if (lifecycleEvidence == null)
            {
                throw new ArgumentNullException(nameof(lifecycleEvidence));
            }

            if (!IsCorrelated(lifecycleEvidence))
            {
                throw new ArgumentException(
                    "Lifecycle evidence must be correlated and releasable.",
                    nameof(lifecycleEvidence));
            }

            return new PngJsonCapturePublicationCaptureCompleteReleaseOperation(
                lifecycleEvidence,
                lifecycleEvidence.NotificationResult,
                lifecycleEvidence.OwnershipLease,
                lifecycleEvidence.LockIdentityEvidence);
        }

        internal PngJsonCapturePublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _lifecycleEvidence;

        internal PngJsonCapturePublicationCaptureCompleteNotificationResult NotificationResult => _notificationResult;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        /// <summary>
        /// Exception-safe exact-binding check shared by <see cref="IsValid"/>,
        /// <see cref="CanRelease"/>, and the release receipt: the held
        /// notification result, ownership lease, and lock identity evidence
        /// must still be the exact references the held lifecycle evidence
        /// forwards. <c>false</c> when any of those references was swapped,
        /// nulled, or forged after issuance.
        /// </summary>
        internal bool IsIssuanceBindingIntact
        {
            get
            {
                if (_lifecycleEvidence == null || _notificationResult == null
                    || _ownershipLease == null || _lockIdentityEvidence == null)
                {
                    return false;
                }

                return ReferenceEquals(_lifecycleEvidence.NotificationResult, _notificationResult)
                    && ReferenceEquals(_lifecycleEvidence.OwnershipLease, _ownershipLease)
                    && ReferenceEquals(_lifecycleEvidence.LockIdentityEvidence, _lockIdentityEvidence);
            }
        }

        /// <summary>
        /// Exception-safe recomputation of the full pre-release issuance
        /// correlation. <c>false</c> once the ownership lease is even partially
        /// released, or when any held reference is forged, replaced, or
        /// corrupted.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    return IsIssuanceBindingIntact && IsCorrelated(_lifecycleEvidence);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Exception-safe retryable condition after issuance: the exact binding
        /// must still hold and the exact ownership lease must still be
        /// retryable (not yet fully released). It intentionally does not depend
        /// on the lifecycle evidence's or lock identity evidence's current
        /// liveness, so a partially released ownership lease can be retried
        /// with the same operation.
        /// </summary>
        internal bool CanRelease
        {
            get
            {
                try
                {
                    return IsIssuanceBindingIntact
                        && _ownershipLease != null
                        && _ownershipLease.CanRelease;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Forwards the exact ownership lease's completed-release state. Never
        /// throws.
        /// </summary>
        internal bool IsReleaseComplete
        {
            get
            {
                try
                {
                    return _ownershipLease != null && _ownershipLease.IsReleaseComplete;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        private static bool IsCorrelated(
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            try
            {
                if (lifecycleEvidence == null || !lifecycleEvidence.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationCaptureCompleteLifecycleOwnerKind kind = lifecycleEvidence.Kind;
                if (kind != CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.FreshSession
                    && kind != CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.RecoveryOpenOutcome)
                {
                    return false;
                }

                if (lifecycleEvidence.NotificationResult == null
                    || lifecycleEvidence.LockIdentityEvidence == null)
                {
                    return false;
                }

                CaptureRunInitializationSessionOwnershipLease ownershipLease = lifecycleEvidence.OwnershipLease;
                if (ownershipLease == null
                    || !ownershipLease.CanRelease
                    || ownershipLease.IsReleaseComplete)
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
    }
}
