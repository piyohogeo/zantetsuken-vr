using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one PngJson capture-complete cleanup
    /// operation: which backend issued it, which operation it executed, and
    /// the exact action plan token it was validated with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type holds exactly three readonly reference fields — the issuer, the
    /// operation, and the token it was issued under — and has no public
    /// constructor. What was cleaned up is read from <see cref="Operation"/>:
    /// the receipt restates none of it and holds no path, hash, byte count,
    /// authority, handle, lease, proof array, or duplicate path set of its
    /// own.
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks with O(1) index-local correlation and never throw, including
    /// after the lease has been released or a nested value was forged.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupReceipt
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteCleanupBackend _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupOperation _operation;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken _token;

        private PngJsonCapturePublicationCaptureCompleteCleanupReceipt(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            _issuedBy = issuedBy;
            _operation = operation;
            _token = token;
        }

        /// <summary>
        /// Single atomic issuance path: null-checks every input, then requires
        /// the operation to be index-locally valid for the token before
        /// binding the receipt. No plan re-validation and no token re-issuance
        /// happen.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupReceipt Create(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
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
                throw new ArgumentException(
                    "Cleanup operation must be index-locally valid for the token.",
                    nameof(operation));
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupReceipt(issuedBy, operation, token);
        }

        internal IPngJsonCapturePublicationCaptureCompleteCleanupBackend IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationCaptureCompleteCleanupOperation Operation => _operation;

        /// <summary>
        /// Exception-safe validity: the three held references must be present
        /// and the held operation must remain index-locally valid for the held
        /// token. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_issuedBy == null || _operation == null || _token == null)
                {
                    return false;
                }

                return _operation.IsValidIndexLocal(_token);
            }
        }

        /// <summary>
        /// Exception-safe issuance check: requires this receipt to still be
        /// valid and the issuer, operation, and token to be reference-identical
        /// to the supplied values. A re-issued token for the same plan, an
        /// equivalent but different operation, or a foreign issuer fails.
        /// Never throws.
        /// </summary>
        internal bool IsIssuedFor(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend issuedBy,
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (issuedBy == null || operation == null || token == null)
            {
                return false;
            }

            if (!ReferenceEquals(_issuedBy, issuedBy)
                || !ReferenceEquals(_operation, operation)
                || !ReferenceEquals(_token, token))
            {
                return false;
            }

            return IsValid;
        }
    }
}
