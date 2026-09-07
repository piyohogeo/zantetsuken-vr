using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Phase 0.11 Run chunk finalization receipt: the exact issuer,
    /// the exact operation, and the exact artifact descriptor of a finalized
    /// chunk. It owns no writer, lease, buffer, stream, hash state, token, or
    /// byte array, performs no file, hash, thread, or task operation, and is
    /// not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// The only normal issue path is <see cref="Create"/>, which requires a
    /// valid operation and a valid descriptor that matches it.
    /// <see cref="IsValid"/> re-checks the same correlation without throwing.
    /// </remarks>
    internal sealed class NvencRunChunkFinalizationReceipt
    {
        private readonly INvencRunChunkFinalizer _issuedBy;
        private readonly NvencRunChunkFinalizationOperation _operation;
        private readonly CaptureArtifactDescriptor _descriptor;

        private NvencRunChunkFinalizationReceipt(
            INvencRunChunkFinalizer issuedBy,
            NvencRunChunkFinalizationOperation operation,
            CaptureArtifactDescriptor descriptor)
        {
            _issuedBy = issuedBy;
            _operation = operation;
            _descriptor = descriptor;
        }

        internal static NvencRunChunkFinalizationReceipt Create(
            INvencRunChunkFinalizer issuedBy,
            NvencRunChunkFinalizationOperation operation,
            CaptureArtifactDescriptor descriptor)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!descriptor.IsValid)
            {
                throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            }

            if (!Correlates(operation, descriptor))
            {
                throw new ArgumentException(
                    "Descriptor must match the operation.", nameof(descriptor));
            }

            return new NvencRunChunkFinalizationReceipt(issuedBy, operation, descriptor);
        }

        internal INvencRunChunkFinalizer IssuedBy => _issuedBy;

        internal NvencRunChunkFinalizationOperation Operation => _operation;

        internal CaptureArtifactDescriptor Descriptor => _descriptor;

        internal NvencRunChunkSink Sink => _operation.Sink;

        internal NvencRunChunkSinkFinalizationEvidence Evidence => _operation.Evidence;

        internal CaptureArtifactFrameRelation FrameRelation => _operation.FrameRelation;

        internal string ArtifactId => _descriptor.ArtifactId;

        internal CaptureArtifactKind ArtifactKind => _descriptor.ArtifactKind;

        internal string FormatId => _descriptor.FormatId;

        internal int FormatVersion => _descriptor.FormatVersion;

        internal string StagingRelativePath => _descriptor.StagingRelativePath;

        internal string FinalRelativePath => _descriptor.FinalRelativePath;

        internal long ByteLength => _descriptor.ByteLength;

        internal string ContentHash => _descriptor.ContentHash;

        internal long AppendedCount => _operation.AppendedCount;

        internal long LastFrameId => _operation.LastFrameId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _issuedBy != null
                        && _operation != null
                        && _descriptor != null
                        && _operation.IsIssuanceBindingIntact
                        && _descriptor.IsValid
                        && Correlates(_operation, _descriptor);
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is ArgumentOutOfRangeException
                    || ex is InvalidOperationException)
                {
                    return false;
                }
            }
        }

        internal bool IsIssuedFor(
            INvencRunChunkFinalizer finalizer,
            NvencRunChunkFinalizationOperation operation)
        {
            return finalizer != null
                && operation != null
                && ReferenceEquals(_issuedBy, finalizer)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        private static bool Correlates(
            NvencRunChunkFinalizationOperation operation,
            CaptureArtifactDescriptor descriptor)
        {
            return string.Equals(descriptor.ArtifactId, operation.ArtifactId, StringComparison.Ordinal)
                && descriptor.ArtifactKind == operation.ArtifactKind
                && string.Equals(descriptor.FormatId, operation.FormatId, StringComparison.Ordinal)
                && descriptor.FormatVersion == operation.FormatVersion
                && string.Equals(descriptor.StagingRelativePath, operation.StagingRelativePath, StringComparison.Ordinal)
                && string.Equals(descriptor.FinalRelativePath, operation.FinalRelativePath, StringComparison.Ordinal)
                && descriptor.ByteLength == operation.AccumulatedByteLength
                && !ContainsPendingPath(descriptor.StagingRelativePath)
                && !ContainsPendingPath(descriptor.FinalRelativePath)
                && IsLowerHex(descriptor.ContentHash, 64);
        }

        private static bool ContainsPendingPath(string relativePath)
        {
            return relativePath != null
                && relativePath.IndexOf(".partial", StringComparison.Ordinal) >= 0;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
