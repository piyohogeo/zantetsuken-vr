using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one capture-complete recovery owner
    /// release: which releaser issued it and which release operation it
    /// completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the issuing
    /// releaser and the release operation — and has no public constructor. It
    /// can be constructed only after the release succeeded: the constructor
    /// rejects a null issuer, a null operation, and any operation whose
    /// issuance proof, terminal state, or owner correlation does not hold.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks without throwing. They do not require the lifecycle evidence or
    /// the notification result to remain valid, because the lease release makes
    /// them invalid by design; instead they verify the opaque issuance proof
    /// and the current release terminal state. Every other accessor forwards a
    /// value from the held operation.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt
    {
        private readonly ICaptureRunPublicationCaptureCompleteRecoveryReleaser _issuedBy;
        private readonly CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation _operation;

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser issuedBy,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!IsCorrelated(issuedBy, operation))
            {
                throw new ArgumentException(
                    "Release operation must be fully released and owner correlated.",
                    nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunPublicationCaptureCompleteRecoveryReleaser IssuedBy => _issuedBy;

        internal CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation Operation => _operation;

        internal CaptureRunPublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _operation.LifecycleEvidence;

        internal CaptureRunPublicationCaptureCompleteNotificationResult NotificationResult => _operation.NotificationResult;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _operation.OpenOutcome;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _operation.OwnershipLease;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal bool IsValid => IsCorrelated(_issuedBy, _operation);

        internal bool IsIssuedFor(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser releaser,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            return releaser != null
                && operation != null
                && ReferenceEquals(_issuedBy, releaser)
                && ReferenceEquals(_operation, operation)
                && IsCorrelated(_issuedBy, _operation);
        }

        private static bool IsCorrelated(
            ICaptureRunPublicationCaptureCompleteRecoveryReleaser issuedBy,
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation)
        {
            if (issuedBy == null || operation == null)
            {
                return false;
            }

            if (!operation.IsIssuanceProofIntact)
            {
                return false;
            }

            CaptureRunInitializationOpenOutcome openOutcome = operation.OpenOutcome;
            CaptureRunInitializationSessionOwnershipLease ownershipLease = operation.OwnershipLease;
            if (openOutcome == null || ownershipLease == null)
            {
                return false;
            }

            if (!ownershipLease.IsReleaseComplete)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteLifecycleEvidence evidence = operation.LifecycleEvidence;
            if (evidence == null || !ReferenceEquals(openOutcome, evidence.OpenOutcome))
            {
                return false;
            }

            return true;
        }
    }
}
