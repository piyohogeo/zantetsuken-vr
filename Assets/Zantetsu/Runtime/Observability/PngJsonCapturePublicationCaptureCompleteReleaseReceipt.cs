using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one PngJson capture-complete owner release:
    /// which releaser issued it and which release operation it completed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the issuing
    /// releaser and the release operation — and has no public constructor. It
    /// can be constructed only after the release succeeded: the constructor
    /// rejects a null issuer, a null operation, and any operation whose exact
    /// issuance binding does not hold or whose ownership lease is not fully
    /// released.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="IsIssuedFor"/> recompute the held
    /// checks without throwing. They intentionally do not require the
    /// lifecycle evidence or the notification result to remain valid, because
    /// the ownership lease release makes them invalid by design; instead they
    /// verify the exact issuance binding and the current completed-release
    /// terminal state. The receipt never exposes the ownership lease, the raw
    /// lock lease, or any token.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteReleaseReceipt
    {
        private readonly IPngJsonCapturePublicationCaptureCompleteReleaser _issuedBy;
        private readonly PngJsonCapturePublicationCaptureCompleteReleaseOperation _operation;

        internal PngJsonCapturePublicationCaptureCompleteReleaseReceipt(
            IPngJsonCapturePublicationCaptureCompleteReleaser issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation)
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
                    "Release operation must be fully released with an intact issuance binding.",
                    nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal IPngJsonCapturePublicationCaptureCompleteReleaser IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationCaptureCompleteReleaseOperation Operation => _operation;

        internal bool IsValid => IsCorrelated(_issuedBy, _operation);

        internal bool IsIssuedFor(
            IPngJsonCapturePublicationCaptureCompleteReleaser releaser,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation)
        {
            return releaser != null
                && operation != null
                && ReferenceEquals(_issuedBy, releaser)
                && ReferenceEquals(_operation, operation)
                && IsCorrelated(_issuedBy, _operation);
        }

        private static bool IsCorrelated(
            IPngJsonCapturePublicationCaptureCompleteReleaser issuedBy,
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation)
        {
            if (issuedBy == null || operation == null)
            {
                return false;
            }

            if (!operation.IsIssuanceBindingIntact)
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
    }
}
