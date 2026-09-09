using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete Fresh NVENC Run chunk artifact
    /// publisher: it moves the exact finalized staging chunk of one issued
    /// <see cref="NvencRunArtifactPublicationOperation"/> to its fixed final
    /// name and verifies the placed final file exactly once, all bound to the
    /// exact <see cref="CaptureArtifactFileStore"/> it was constructed with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The publisher holds only its exact store and its exact filesystem
    /// backend. Operations, descriptors, results, and buffer leases are never
    /// retained in fields or collections.
    /// </para>
    /// <para>
    /// <see cref="Publish"/> validates before any side effect: a <c>null</c>
    /// operation throws <see cref="ArgumentNullException"/>, and an invalid
    /// operation, a foreign Run, or a descriptor that is not the fixed
    /// <c>NvencH264IdrChunk</c> version 1 frame-sequence chunk at its fixed
    /// staging and final relative paths throws
    /// <see cref="ArgumentException"/> before a buffer is reserved and before
    /// the filesystem is touched. An unsupported no-follow capability is a
    /// configuration error that belongs before the Run starts and is raised as
    /// <see cref="CaptureArtifactNoFollowUnavailableException"/>, never
    /// disguised as a publication content failure.
    /// </para>
    /// <para>
    /// The store's single fixed verification buffer is reserved exactly once,
    /// before the first filesystem contact, and returned on the success, the
    /// failure, and the exception path. No second reservation and no fallback
    /// allocation exists, and no array proportional to the chunk length is
    /// ever allocated. The store's general
    /// <see cref="CaptureArtifactFileStore.Publish"/> and
    /// <see cref="CaptureArtifactFileStore.PublishReserved"/> are deliberately
    /// not used: they also re-hash the staging file, which the Fresh contract
    /// replaces with the streaming hash already confirmed at Context
    /// finalization.
    /// </para>
    /// <para>
    /// Each call is exactly one attempt: the backend is invoked at most once,
    /// no retry, rollback, cleanup, second rename, or post-failure
    /// re-inspection is performed, and the plan, registry, disposition,
    /// session ownership lease, service, and coordinator are never touched. A
    /// receipt means only that the rename and the post-placement verification
    /// both succeeded in this synchronous call; it is not a crash-durability
    /// claim.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunArtifactPublisher : INvencRunArtifactPublisher
    {
        private readonly CaptureArtifactFileStore _store;
        private readonly INvencRunArtifactPublicationFileSystem _fileSystem;

        internal NvencRunArtifactPublisher(CaptureArtifactFileStore store)
            : this(store, NvencRunArtifactPublicationFileSystem.Create())
        {
        }

        internal NvencRunArtifactPublisher(
            CaptureArtifactFileStore store,
            INvencRunArtifactPublicationFileSystem fileSystem)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        public NvencRunArtifactPublicationAttemptResult Publish(
            NvencRunArtifactPublicationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!ReferenceEquals(operation.RootLayout, _store.RootLayout))
            {
                throw new ArgumentException(
                    "Operation root layout must match the store's root layout.", nameof(operation));
            }

            CaptureArtifactDescriptor descriptor = operation.Descriptor;
            RequireFixedChunkDescriptor(descriptor);

            // Capability insufficiency is a configuration error to be detected
            // before a Run starts, not a content failure of this publish.
            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Fresh NVENC Run artifact publication requires no-follow file support on this platform.");
            }

            // The single fixed verification buffer is reserved before the first
            // filesystem contact, so buffer exhaustion is never discovered only
            // after the final placement.
            CaptureArtifactPublishReservation reservation = _store.TryReservePublish();
            if (reservation == null)
            {
                return NvencRunArtifactPublicationAttemptResult.Failed(this, operation);
            }

            bool placed;
            try
            {
                placed = _fileSystem.TryPublishFresh(
                    operation.RootLayout, descriptor, reservation.Lease.Buffer);
            }
            finally
            {
                _store.ReleasePublishReservation(reservation);
            }

            return placed
                ? NvencRunArtifactPublicationAttemptResult.Published(this, operation)
                : NvencRunArtifactPublicationAttemptResult.Failed(this, operation);
        }

        private static void RequireFixedChunkDescriptor(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid)
            {
                throw new ArgumentException("Operation descriptor must be valid.", "operation");
            }

            if (descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence)
            {
                throw new ArgumentException(
                    "Operation descriptor must describe a frame sequence artifact.", "operation");
            }

            if (!string.Equals(
                    descriptor.FormatId,
                    NvencRunChunkArtifactDescriptorFactory.FormatId,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Operation descriptor must use the NVENC H.264 IDR chunk format.", "operation");
            }

            if (descriptor.FormatVersion != NvencRunChunkArtifactDescriptorFactory.FormatVersion)
            {
                throw new ArgumentException(
                    "Operation descriptor must use the supported chunk format version.", "operation");
            }

            // A pending path is a partially written chunk and must never be
            // published, even if it were to reach the fixed-path comparison.
            if (IsPendingPath(descriptor.StagingRelativePath)
                || IsPendingPath(descriptor.FinalRelativePath))
            {
                throw new ArgumentException(
                    "Operation descriptor must not name a pending chunk path.", "operation");
            }

            if (!string.Equals(
                    descriptor.StagingRelativePath,
                    NvencRunChunkArtifactDescriptorFactory.StagingRelativePath,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Operation descriptor must use the fixed staging chunk path.", "operation");
            }

            if (!string.Equals(
                    descriptor.FinalRelativePath,
                    NvencRunChunkArtifactDescriptorFactory.FinalRelativePath,
                    StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Operation descriptor must use the fixed final chunk path.", "operation");
            }

            if (descriptor.ByteLength <= 0)
            {
                throw new ArgumentException(
                    "Operation descriptor must declare a positive byte length.", "operation");
            }

            if (!IsLowerHex(descriptor.ContentHash, 64))
            {
                throw new ArgumentException(
                    "Operation descriptor must declare a lowercase hex SHA-256 content hash.", "operation");
            }
        }

        private static bool IsPendingPath(string relativePath)
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
