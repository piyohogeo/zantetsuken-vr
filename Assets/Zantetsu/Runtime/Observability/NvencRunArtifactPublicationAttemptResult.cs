using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Readonly value result of one NVENC Run artifact publication attempt:
    /// the exact publisher, the exact operation, and a status with a receipt
    /// held only for the <see cref="NvencRunArtifactPublicationStatus.Published"/>
    /// shape. The two terminal shapes are mutually exclusive;
    /// <see cref="NvencRunArtifactPublicationStatus.None"/> is the
    /// uninitialized default and is never visible as a terminal shape.
    /// </summary>
    /// <remarks>
    /// Descriptor, path, hash, and run identity are forwarded from the held
    /// operation and never duplicated as fields, and no new proof, token, or
    /// nonce is introduced.
    /// </remarks>
    internal readonly struct NvencRunArtifactPublicationAttemptResult
    {
        private readonly INvencRunArtifactPublisher _publisher;
        private readonly NvencRunArtifactPublicationOperation _operation;
        private readonly NvencRunArtifactPublicationReceipt _receipt;
        private readonly NvencRunArtifactPublicationStatus _status;

        private NvencRunArtifactPublicationAttemptResult(
            INvencRunArtifactPublisher publisher,
            NvencRunArtifactPublicationOperation operation,
            NvencRunArtifactPublicationReceipt receipt,
            NvencRunArtifactPublicationStatus status)
        {
            _publisher = publisher;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal INvencRunArtifactPublisher Publisher => _publisher;

        internal NvencRunArtifactPublicationOperation Operation => _operation;

        internal NvencRunArtifactPublicationReceipt Receipt => _receipt;

        internal NvencRunArtifactPublicationStatus Status => _status;

        internal bool IsNone => _status == NvencRunArtifactPublicationStatus.None;

        internal bool IsPublished =>
            _status == NvencRunArtifactPublicationStatus.Published
            && _publisher != null
            && _operation != null
            && _receipt != null
            && _receipt.IsIssuedFor(_publisher, _operation);

        internal bool IsFailed =>
            _status == NvencRunArtifactPublicationStatus.Failed
            && _publisher != null
            && _operation != null
            && _operation.IsValid
            && _receipt == null;

        internal bool IsValid => IsPublished || IsFailed;

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

        internal static NvencRunArtifactPublicationAttemptResult Published(
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

            NvencRunArtifactPublicationReceipt receipt =
                NvencRunArtifactPublicationReceipt.Create(publisher, operation);

            return new NvencRunArtifactPublicationAttemptResult(
                publisher, operation, receipt, NvencRunArtifactPublicationStatus.Published);
        }

        internal static NvencRunArtifactPublicationAttemptResult Failed(
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

            return new NvencRunArtifactPublicationAttemptResult(
                publisher, operation, null, NvencRunArtifactPublicationStatus.Failed);
        }
    }
}
