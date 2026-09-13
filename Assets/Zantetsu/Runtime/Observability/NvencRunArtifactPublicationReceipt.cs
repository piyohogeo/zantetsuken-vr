using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run artifact publication receipt: the exact
    /// publisher and the exact operation whose final-name placement and full
    /// final length-and-hash verification are known to have succeeded in one
    /// synchronous publish call. It is issued only from the
    /// <see cref="NvencRunArtifactPublicationAttemptResult.Published"/> path
    /// and has no public constructor.
    /// </summary>
    /// <remarks>
    /// This type holds only the exact publisher and the exact operation,
    /// performs no filesystem or hash work, and is not an
    /// <see cref="IDisposable"/>. Descriptor, path, hash, and run identity are
    /// read from <see cref="Operation"/>; the receipt does not restate them,
    /// and no new proof, token, or nonce is introduced. A receipt means one synchronous
    /// publish call completed its post-placement length-and-hash verification;
    /// it is not a crash-durability or persistent filesystem snapshot.
    /// </remarks>
    internal sealed class NvencRunArtifactPublicationReceipt
    {
        private readonly INvencRunArtifactPublisher _publisher;
        private readonly NvencRunArtifactPublicationOperation _operation;

        private NvencRunArtifactPublicationReceipt(
            INvencRunArtifactPublisher publisher,
            NvencRunArtifactPublicationOperation operation)
        {
            _publisher = publisher;
            _operation = operation;
        }

        internal static NvencRunArtifactPublicationReceipt Create(
            INvencRunArtifactPublisher publisher,
            NvencRunArtifactPublicationOperation operation)
        {
            if (publisher == null)
            {
                throw new ArgumentNullException(nameof(publisher));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException(
                    "The operation is no longer valid.", nameof(operation));
            }

            return new NvencRunArtifactPublicationReceipt(publisher, operation);
        }

        internal INvencRunArtifactPublisher Publisher => _publisher;

        internal NvencRunArtifactPublicationOperation Operation => _operation;

        internal bool IsValid =>
            _publisher != null
            && _operation != null
            && _operation.IsBindingIntact;

        internal bool IsIssuedFor(
            INvencRunArtifactPublisher publisher,
            NvencRunArtifactPublicationOperation operation)
        {
            return publisher != null
                && operation != null
                && ReferenceEquals(_publisher, publisher)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }
    }
}
