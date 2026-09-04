using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one accepted PngJson capture-complete owner
    /// release: the coordinator that issued it, the releaser it was routed
    /// through, the release operation it completed, and the receipt the
    /// releaser returned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the issuing
    /// coordinator, the coordinator-bound issuance proof, the release
    /// operation, and the release receipt — and has no public constructor.
    /// It duplicates no ownership lease, lock identity evidence, lifecycle
    /// evidence, or run identity as a field; every accessor forwards a value
    /// from the held operation graph. The status is derived from
    /// <see cref="IsValid"/>, never stored.
    /// </para>
    /// <para>
    /// The atomic factory <see cref="Create"/> performs only the O(1)
    /// exact-binding predicate after the coordinator already fully verified
    /// the receipt: null-checks every input, requires the proof to be minted
    /// by this exact coordinator for the exact releaser, operation, and
    /// receipt, requires the receipt issuer and operation to be the exact
    /// references, and requires the operation to be in the fully released
    /// state. It never re-runs <c>receipt.IsIssuedFor</c> on the success path.
    /// <see cref="IsValid"/> re-runs that same binding as its first stage and
    /// then adds the single full post-release verification through
    /// <c>receipt.IsIssuedFor</c>, together with the released and
    /// non-retryable terminal state. Because the release makes the upstream
    /// evidence invalid by design, <see cref="IsValid"/> intentionally never
    /// requires <c>operation.IsValid</c>, the lifecycle evidence's validity,
    /// or the lock identity evidence's validity.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteReleaseResult
    {
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseCoordinator _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof _proof;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseOperation _operation;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseReceipt _receipt;

        private PngJsonCapturePublicationCaptureCompleteReleaseResult(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            _issuedBy = issuedBy;
            _proof = proof;
            _operation = operation;
            _receipt = receipt;
        }

        /// <summary>
        /// Atomic issuance gate used only by the coordinator after it already
        /// fully verified the receipt: null-checks every input and then runs
        /// the O(1) exact-binding predicate plus the fully-released terminal
        /// check. It never re-runs <c>receipt.IsIssuedFor</c> on the success
        /// path.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteReleaseResult Create(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (proof == null)
            {
                throw new ArgumentNullException(nameof(proof));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (!IsBoundO1(issuedBy, proof, operation, receipt) || !operation.IsReleaseComplete)
            {
                throw new InvalidOperationException(
                    "Release receipt must be correlated with the issuing coordinator and operation.");
            }

            return new PngJsonCapturePublicationCaptureCompleteReleaseResult(issuedBy, proof, operation, receipt);
        }

        internal PngJsonCapturePublicationCaptureCompleteReleaseCoordinator IssuedBy => _issuedBy;

        internal IPngJsonCapturePublicationCaptureCompleteReleaser Releaser => _issuedBy.Releaser;

        internal PngJsonCapturePublicationCaptureCompleteReleaseOperation Operation => _operation;

        internal PngJsonCapturePublicationCaptureCompleteReleaseReceipt Receipt => _receipt;

        internal PngJsonCapturePublicationCaptureCompleteLifecycleEvidence LifecycleEvidence => _operation.LifecycleEvidence;

        internal PngJsonCapturePublicationCaptureCompleteNotificationResult NotificationResult => _operation.NotificationResult;

        internal CaptureRunPublicationCaptureCompleteLifecycleOwnerKind Kind => _operation.Kind;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _operation.LockIdentityEvidence;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal string CaptureIndexPath => _operation.CaptureIndexPath;

        internal bool IsReleaseComplete => _operation.IsReleaseComplete;

        internal PngJsonCapturePublicationCaptureCompleteReleaseStatus Status
            => IsValid
                ? PngJsonCapturePublicationCaptureCompleteReleaseStatus.OwnerReleased
                : PngJsonCapturePublicationCaptureCompleteReleaseStatus.None;

        /// <summary>
        /// Exception-safe recomputation of the full post-release correlation
        /// from the currently held graph, without throwing. Any corrupted or
        /// replaced value converges to <c>false</c>.
        /// </summary>
        internal bool IsValid => IsFullyValid(_issuedBy, _proof, _operation, _receipt);

        /// <summary>
        /// O(1), exception-safe exact-binding predicate used by the issuance
        /// factory: non-null checks, proof binding, and the receipt issuer and
        /// operation reference identity only. It never calls
        /// <c>receipt.IsIssuedFor</c>, so the coordinator's immediate full
        /// receipt verification is not repeated on the success path.
        /// </summary>
        private static bool IsBoundO1(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            try
            {
                if (issuedBy == null || proof == null || operation == null || receipt == null)
                {
                    return false;
                }

                if (!issuedBy.IsMintedByThis(proof, operation, receipt))
                {
                    return false;
                }

                IPngJsonCapturePublicationCaptureCompleteReleaser releaser = issuedBy.Releaser;
                if (releaser == null)
                {
                    return false;
                }

                return ReferenceEquals(receipt.IssuedBy, releaser)
                    && ReferenceEquals(receipt.Operation, operation);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Exception-safe full post-release validation predicate: runs the O(1)
        /// exact-binding predicate, then calls <c>receipt.IsIssuedFor</c>
        /// exactly once for the receipt's own post-release verification, and
        /// finally requires the released and non-retryable terminal state. It
        /// never re-validates the operation, lifecycle evidence, or lock
        /// identity evidence.
        /// </summary>
        private static bool IsFullyValid(
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseCoordinator.IssuanceProof proof,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation,
            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt)
        {
            try
            {
                if (!IsBoundO1(issuedBy, proof, operation, receipt))
                {
                    return false;
                }

                IPngJsonCapturePublicationCaptureCompleteReleaser releaser = issuedBy.Releaser;
                if (!receipt.IsIssuedFor(releaser, operation))
                {
                    return false;
                }

                if (!operation.IsReleaseComplete)
                {
                    return false;
                }

                if (operation.CanRelease)
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
