using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable internal token proving one synchronous PngJson publish call
    /// fully succeeded. It holds only the exact issuer, the exact operation,
    /// and the exact validation token used for issuance; no bytes, hash copies,
    /// streams, handles, leases, or path copies are held or exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt is not an OS certificate or a filesystem snapshot; it only
    /// records that one synchronous call succeeded. It holds exactly three
    /// readonly references — the issuer, the operation, and the token it was
    /// issued under. What was published is read from <see cref="Operation"/>:
    /// the receipt restates none of it and duplicates no path, hash, byte
    /// count, or Run identity as a field of its own. <see cref="IsValid"/> and
    /// <see cref="IsIssuedFor"/> recompute without throwing, so a receipt whose
    /// operation, token, or owner has been corrupted or released becomes
    /// invalid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactPublishReceipt
    {
        private readonly IPngJsonCapturePublicationArtifactPublisher _issuedBy;
        private readonly PngJsonCapturePublicationArtifactPublishOperation _operation;
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken _token;

        private PngJsonCapturePublicationArtifactPublishReceipt(
            IPngJsonCapturePublicationArtifactPublisher issuedBy,
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            _issuedBy = issuedBy;
            _operation = operation;
            _token = token;
        }

        /// <summary>
        /// Atomic issuance factory: null-checks every input, then requires the
        /// operation to be index-locally valid for the exact supplied token
        /// (no whole-plan re-validation, no token re-issuance) before assigning
        /// the three references.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactPublishReceipt Create(
            IPngJsonCapturePublicationArtifactPublisher issuedBy,
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!operation.IsValidIndexLocal(token))
            {
                throw new ArgumentException("Operation must be index-locally valid for the issued token.", nameof(operation));
            }

            return new PngJsonCapturePublicationArtifactPublishReceipt(issuedBy, operation, token);
        }

        internal IPngJsonCapturePublicationArtifactPublisher IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationArtifactPublishOperation Operation => _operation;

        /// <summary>
        /// O(1), exception-safe validity: the three references are present and
        /// the operation is still index-locally valid for the held token.
        /// Never throws and never re-issues a token or re-validates the plan.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return _issuedBy != null
                    && _operation != null
                    && _token != null
                    && _operation.IsValidIndexLocal(_token);
            }
        }

        /// <summary>
        /// Exact issuance identity: requires <see cref="IsValid"/> and a
        /// <see cref="object.ReferenceEquals"/> on the issuer, operation, and
        /// token, so a foreign backend, an equal-valued operation, or a
        /// separately re-issued token for the same plan is rejected. Never
        /// throws.
        /// </summary>
        internal bool IsIssuedFor(
            IPngJsonCapturePublicationArtifactPublisher issuedBy,
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return IsValid
                && ReferenceEquals(issuedBy, _issuedBy)
                && ReferenceEquals(operation, _operation)
                && ReferenceEquals(token, _token);
        }
    }
}
