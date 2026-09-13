using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable internal token proving one synchronous PngJson Capture Index
    /// commit call fully succeeded. It holds only the exact issuer, the exact
    /// operation, and the exact validation token used for issuance; no bytes,
    /// canonical copies, streams, handles, leases, or path copies are held or
    /// exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt is not an OS certificate or a filesystem snapshot; it only
    /// records that one synchronous call succeeded. It holds exactly three
    /// readonly references — the committer, the operation, and the token it was
    /// issued under. What was committed is read from <see cref="Operation"/>:
    /// the receipt restates none of it and keeps no path, byte count, mode, or
    /// Run identity of its own. The canonical bytes are checked at issuance and
    /// neither retained nor returned. <see cref="IsValid"/> and
    /// <see cref="IsIssuedFor"/> recompute without throwing, so a receipt whose
    /// operation, token, or owner has been corrupted or released becomes
    /// invalid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCaptureRunCaptureIndexCommitReceipt
    {
        private readonly IPngJsonCaptureRunCaptureIndexCommitter _issuedBy;
        private readonly PngJsonCaptureRunCaptureIndexCommitOperation _operation;
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken _token;

        private PngJsonCaptureRunCaptureIndexCommitReceipt(
            IPngJsonCaptureRunCaptureIndexCommitter issuedBy,
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            _issuedBy = issuedBy;
            _operation = operation;
            _token = token;
        }

        /// <summary>
        /// Atomic issuance factory: null-checks every input, then requires the
        /// operation to be fully valid for the exact supplied token, including
        /// the canonical byte re-verification (no whole-plan re-validation, no
        /// token re-issuance) before assigning the three references.
        /// </summary>
        internal static PngJsonCaptureRunCaptureIndexCommitReceipt Create(
            IPngJsonCaptureRunCaptureIndexCommitter issuedBy,
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
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

            if (!operation.IsValidWithToken(token))
            {
                throw new ArgumentException("Operation must be fully valid for the issued token, including its canonical bytes.", nameof(operation));
            }

            return new PngJsonCaptureRunCaptureIndexCommitReceipt(issuedBy, operation, token);
        }

        internal IPngJsonCaptureRunCaptureIndexCommitter IssuedBy => _issuedBy;

        internal PngJsonCaptureRunCaptureIndexCommitOperation Operation => _operation;

        /// <summary>
        /// Exception-safe validity: the three references are present and the
        /// operation is still fully valid for the held token, including the
        /// canonical byte re-verification. Never throws and never re-issues a
        /// token or re-validates the plan.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return _issuedBy != null
                    && _operation != null
                    && _token != null
                    && _operation.IsValidWithToken(_token);
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
            IPngJsonCaptureRunCaptureIndexCommitter issuedBy,
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return IsValid
                && ReferenceEquals(issuedBy, _issuedBy)
                && ReferenceEquals(operation, _operation)
                && ReferenceEquals(token, _token);
        }

        /// <summary>
        /// O(1), exception-safe index-local issuance identity: requires the
        /// three exact reference identities and the operation's index-local
        /// validity only, without re-serializing the canonical bytes. Full
        /// byte-content verification remains the responsibility of
        /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/>. Never throws.
        /// </summary>
        internal bool IsIssuedForIndexLocal(
            IPngJsonCaptureRunCaptureIndexCommitter issuedBy,
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return _issuedBy != null
                && _operation != null
                && _token != null
                && ReferenceEquals(issuedBy, _issuedBy)
                && ReferenceEquals(operation, _operation)
                && ReferenceEquals(token, _token)
                && _operation.IsValidIndexLocal(token);
        }
    }
}
