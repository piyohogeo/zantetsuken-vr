using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete Fresh NVENC capture index committer: it
    /// serializes the operation's plan to canonical bytes, writes them to the
    /// dedicated temporary file <c>capture.index.tmp</c> directly under the
    /// final Run root, and non-overwriting renames it to <c>capture.index</c>
    /// exactly once, all bound to the exact root layout it was constructed
    /// with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The committer is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// immutable filesystem collaborator. It holds no operation, receipt,
    /// canonical byte copy, stream, or handle in fields; canonical bytes are
    /// produced once per call and discarded after it. It is not an
    /// <see cref="IDisposable"/> and owns no thread, queue, task, wait, or
    /// lease.
    /// </para>
    /// <para>
    /// The capture index content is the canonical serialization of the same
    /// <see cref="CapturePublicationPlan"/> that the publication plan commit
    /// used, taken from the operation's graph rather than rebuilt here.
    /// </para>
    /// <para>
    /// <see cref="Commit"/> validates before any filesystem contact: a
    /// <c>null</c> operation throws <see cref="ArgumentNullException"/>, an
    /// invalid operation throws <see cref="ArgumentException"/>, and a foreign
    /// operation whose root layout is not this committer's exact root layout
    /// throws <see cref="ArgumentException"/> without touching the filesystem.
    /// The no-follow capability is preflighted before the first side effect and
    /// raised as <see cref="CaptureArtifactNoFollowUnavailableException"/>, so
    /// a contract violation or an unsupported platform is never reported as an
    /// ordinary Failed commit.
    /// </para>
    /// <para>
    /// A failure to serialize, open, create, write, or rename is published as
    /// <see cref="NvencRunCaptureIndexCommitStatus.Failed"/> without a receipt;
    /// a returned rename is published as
    /// <see cref="NvencRunCaptureIndexCommitStatus.Committed"/> with an exact
    /// receipt. An exception raised by the rename call itself is also Failed
    /// and is never re-derived, re-read, existence-checked, renamed again,
    /// cleaned up, or rolled back; the Run proceeds to the existing
    /// <c>PublicationRecoveryRequired</c> handoff. Each call performs at most
    /// one create, one write, and one rename, never overwrites the
    /// destination, touches no file other than the two fixed names, deletes
    /// nothing, and never touches the staging Run root, the plan, the chunk,
    /// the Registry, the disposition, or the Service. Handles and streams are
    /// released on every path, after the outcome has been classified, so a
    /// release failure can never rewrite a known success into a Failed. This
    /// type performs no durability flush and claims no crash or power-loss
    /// durability.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexCommitter : INvencRunCaptureIndexCommitter
    {
        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string CaptureIndexName = "capture.index";

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly INvencPublicationPlanCommitFileSystem _fileSystem;

        internal NvencRunCaptureIndexCommitter(CaptureRunRootLayout rootLayout)
            : this(rootLayout, NvencPublicationPlanCommitFileSystem.Create())
        {
        }

        internal NvencRunCaptureIndexCommitter(
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

        public NvencRunCaptureIndexCommitAttemptResult Commit(
            NvencRunCaptureIndexCommitOperation operation)
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
                    "NVENC capture index commit requires no-follow file support on this platform.");
            }

            // Pre-rename phase: every failure before the rename call is an
            // ordinary Failed commit. The temporary file is never deleted.
            NvencPublicationPlanCommitDirectory directory = null;
            NvencPublicationPlanCommitFile file = null;
            try
            {
                byte[] canonicalBytes = CapturePublicationPlanCodec.SerializeCanonical(operation.Plan);

                directory = _fileSystem.OpenDirectory(_rootLayout.FinalRunRoot);
                file = _fileSystem.CreateNew(directory, CaptureIndexTemporaryName);
                file.Stream.Write(canonicalBytes, 0, canonicalBytes.Length);

                // Managed buffer flush only, so the bytes reach the file before
                // the rename. This is not a flush to disk and claims no
                // crash or power-loss durability.
                file.Stream.Flush();
            }
            catch (Exception)
            {
                Release(directory, file);
                return NvencRunCaptureIndexCommitAttemptResult.Failed(this, operation);
            }

            // Rename boundary: once the rename is invoked its outcome is never
            // re-derived, re-read, existence-checked, retried, or cleaned up.
            try
            {
                _fileSystem.Rename(file, directory, CaptureIndexName);
            }
            catch (Exception)
            {
                Release(directory, file);
                return NvencRunCaptureIndexCommitAttemptResult.Failed(this, operation);
            }

            // The non-overwriting rename returned: known success. The release
            // below is best-effort and never rewrites this classification.
            Release(directory, file);
            return NvencRunCaptureIndexCommitAttemptResult.Committed(this, operation);
        }

        private static void Release(
            NvencPublicationPlanCommitDirectory directory,
            NvencPublicationPlanCommitFile file)
        {
            // Best-effort cleanup: both handle types release their kernel
            // handle in a finally, and a release failure must not change the
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
