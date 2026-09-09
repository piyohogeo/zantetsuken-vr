using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete NVENC publication plan committer: it
    /// writes the canonical plan bytes to the dedicated temporary file
    /// <c>publication.plan.nvenc-precommit.tmp</c> directly under the staging
    /// Run root and non-overwriting renames it to <c>publication.plan</c>
    /// exactly once, all bound to the exact root layout it was constructed
    /// with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The committer is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// immutable filesystem collaborator. It holds no active operation,
    /// receipt, or canonical byte copy in fields; canonical bytes are obtained
    /// transiently from the operation and discarded after the call.
    /// </para>
    /// <para>
    /// <see cref="Commit"/> validates the operation before any filesystem
    /// contact: a <c>null</c> operation throws
    /// <see cref="ArgumentNullException"/>, an invalid operation throws
    /// <see cref="ArgumentException"/>, and a foreign operation whose root
    /// layout is not this committer's exact root layout throws
    /// <see cref="ArgumentException"/> without touching the filesystem. The
    /// no-follow capability is preflighted before the first side effect.
    /// </para>
    /// <para>
    /// A failure before the rename call is published as
    /// <see cref="NvencRunPublicationPlanCommitStatus.FailedBeforeRename"/>; a
    /// successful rename is published as
    /// <see cref="NvencRunPublicationPlanCommitStatus.Committed"/> with an
    /// exact receipt; an exception raised by the rename call itself is
    /// published as
    /// <see cref="NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown"/>
    /// and is never re-derived, re-read, cleaned up, or retried. Each call is
    /// exactly one attempt: no retry, no rollback, no second rename, and no
    /// temporary-file deletion. Handles and streams are released on every
    /// path. This type performs no durability flush and claims no
    /// crash/power-loss durability.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationPlanCommitter : INvencRunPublicationPlanCommitter
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly INvencPublicationPlanCommitFileSystem _fileSystem;

        internal NvencRunPublicationPlanCommitter(CaptureRunRootLayout rootLayout)
            : this(rootLayout, NvencPublicationPlanCommitFileSystem.Create())
        {
        }

        internal NvencRunPublicationPlanCommitter(
            CaptureRunRootLayout rootLayout,
            INvencPublicationPlanCommitFileSystem fileSystem)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            if (!rootLayout.IsValid)
            {
                throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));
            }

            _rootLayout = rootLayout;
            _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        }

        public NvencRunPublicationPlanCommitAttemptResult Commit(
            NvencRunPublicationPlanCommitOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!ReferenceEquals(operation.RootLayout, _rootLayout))
            {
                throw new ArgumentException(
                    "Operation root layout must match the committer's root layout.", nameof(operation));
            }

            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "NVENC publication plan commit requires no-follow file support on this platform.");
            }

            // Pre-rename phase: every failure before the rename call is a
            // known pre-rename failure. The temporary file is never deleted.
            NvencPublicationPlanCommitDirectory directory = null;
            NvencPublicationPlanCommitFile file = null;
            try
            {
                byte[] canonicalBytes = CapturePublicationPlanCodec.SerializeCanonical(operation.Plan);

                directory = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot);
                file = _fileSystem.CreateNew(
                    directory, NvencRunPublicationPlanCommitOperation.PreCommitBasename);
                file.Stream.Write(canonicalBytes, 0, canonicalBytes.Length);
                file.Stream.Flush();
            }
            catch (Exception)
            {
                Release(directory, file);
                return NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(this, operation);
            }

            // Rename boundary: once the rename is invoked its outcome is never
            // re-derived, re-read, cleaned up, or retried.
            try
            {
                _fileSystem.Rename(
                    file, directory, NvencRunPublicationPlanCommitOperation.FinalBasename);
            }
            catch (Exception)
            {
                Release(directory, file);
                return NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(this, operation);
            }

            // The non-overwriting rename returned: known success.
            Release(directory, file);
            return NvencRunPublicationPlanCommitAttemptResult.Committed(this, operation);
        }

        private static void Release(
            NvencPublicationPlanCommitDirectory directory,
            NvencPublicationPlanCommitFile file)
        {
            // Best-effort cleanup: both handle types release their kernel
            // handle in a finally, and a cleanup exception must not change the
            // already-determined attempt classification.
            try
            {
                file?.Dispose();
            }
            catch
            {
            }

            try
            {
                directory?.Dispose();
            }
            catch
            {
            }
        }
    }
}
