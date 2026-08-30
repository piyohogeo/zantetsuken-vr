using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable internal token proving one synchronous publish call fully
    /// succeeded. It holds no bytes, hash copies, file handles, streams, or
    /// path copies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt is not an OS certificate or a filesystem snapshot; it only
    /// records that one synchronous call succeeded. <see cref="IsValid"/> and
    /// <see cref="IsIssuedFor"/> recompute without throwing, so a receipt whose
    /// operation or lease has been corrupted or released becomes invalid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactPublishReceipt
    {
        private readonly ICaptureRunPublicationArtifactPublisher _issuedBy;
        private readonly CaptureRunPublicationArtifactPublishOperation _operation;

        internal CaptureRunPublicationArtifactPublishReceipt(
            ICaptureRunPublicationArtifactPublisher issuedBy,
            CaptureRunPublicationArtifactPublishOperation operation)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunPublicationArtifactPublisher IssuedBy => _issuedBy;

        internal CaptureRunPublicationArtifactPublishOperation Operation => _operation;

        internal int EntryIndex => _operation.EntryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => _operation.ArtifactKind;

        internal long CaptureFrameId => _operation.CaptureFrameId;

        internal string SourcePath => _operation.SourcePath;

        internal string DestinationPath => _operation.DestinationPath;

        internal long ExpectedByteCount => _operation.ExpectedByteCount;

        internal string ExpectedContentSha256 => _operation.ExpectedContentSha256;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                return _issuedBy != null
                    && _operation != null
                    && _operation.IsValid;
            }
        }

        internal bool IsIssuedFor(
            ICaptureRunPublicationArtifactPublisher publisher,
            CaptureRunPublicationArtifactPublishOperation operation)
        {
            return IsValid
                && ReferenceEquals(publisher, _issuedBy)
                && ReferenceEquals(operation, _operation);
        }
    }
}
