using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production Phase 0.11 NVENC Capture Index
    /// recovery committer. It serializes the authoritative plan once and, under
    /// one no-follow-verified handle to the exact final Run root, makes those
    /// canonical bytes the final <c>capture.index</c> by a single
    /// handle-bound, non-overwriting rename, in the way the operation's commit
    /// mode prescribes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The committer is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// existing <see cref="ICaptureIndexCommitFileSystem"/>; no new filesystem
    /// interface, Win32, or NT backend is introduced here. It holds no
    /// operation, receipt, canonical byte copy, stream, or handle in fields and
    /// is not an <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// Validation comes before any filesystem contact: a null operation, an
    /// invalid one, and one whose root layout is not this committer's exact
    /// root layout are refused untouched, and a platform without no-follow
    /// support raises
    /// <see cref="CaptureArtifactNoFollowUnavailableException"/>. Phase 0.11
    /// asks for no durability, so directory-flush capability is not part of
    /// admission and neither file nor directory flush is ever called; only the
    /// managed <see cref="Stream.Flush()"/> runs, to put the written bytes in
    /// the file before the rename.
    /// </para>
    /// <para>
    /// Only two fixed basenames under the exact final Run root are ever
    /// touched, <c>capture.index.tmp</c> and <c>capture.index</c>. The staging
    /// Run root, the publication plan, the chunk, <c>run.init</c>,
    /// <c>run.ready</c>, the legacy plan temporary, and every other Run root
    /// are outside this type entirely. The final name is observed first, and
    /// unless it is confirmed absent no temporary is created, written,
    /// deleted, or renamed. An open that is not Absent - Opened,
    /// InvalidFileKind, EscapesRoot, IoFailure, or Unsupported - is never
    /// folded into absence.
    /// </para>
    /// <para>
    /// Delete and rename are bound to the handle opened in this very attempt,
    /// so an entry that cannot be established right now as an exact ordinary
    /// file under the verified directory is left alone; no file identity is
    /// compared against a previous inspection and no such proof is invented.
    /// The temporary may be deleted only under
    /// <see cref="CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit"/>,
    /// and only after this attempt re-confirmed that exact handle holds an
    /// unusable document.
    /// </para>
    /// <para>
    /// A receipt is minted only after the rename has returned and the handles
    /// it needed have been released; a failure anywhere, the rename included,
    /// propagates by the same reference and is never converted into a status or
    /// a success. Nothing is retried, re-renamed, existence-checked after the
    /// rename, rolled back, or cleaned up: the caller decides again from a
    /// fresh inspection under the lock it still holds.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryCommitter
        : INvencRunCaptureIndexRecoveryCommitter
    {
        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string CaptureIndexName = "capture.index";

        private const int InitialReadByteCount = 64 * 1024;

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly ICaptureIndexCommitFileSystem _fileSystem;

        internal NvencRunCaptureIndexRecoveryCommitter(CaptureRunRootLayout rootLayout)
            : this(rootLayout, CaptureIndexCommitFileSystem.Create())
        {
        }

        internal NvencRunCaptureIndexRecoveryCommitter(
            CaptureRunRootLayout rootLayout,
            ICaptureIndexCommitFileSystem fileSystem)
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

        public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
            NvencRunCaptureIndexRecoveryCommitOperation operation)
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
                    "Operation root layout must match the committer's root layout.",
                    nameof(operation));
            }

            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "NVENC Capture Index recovery commit requires no-follow file support on this platform.");
            }

            // The authoritative plan is serialized exactly once, and the bytes
            // live only for this call.
            byte[] canonicalBytes = CapturePublicationPlanCodec.SerializeCanonical(
                operation.AuthoritativePlan);

            CaptureIndexCommitDirectory directory =
                _fileSystem.OpenDirectory(_rootLayout.FinalRunRoot);
            try
            {
                // The final name decides whether this attempt may touch the
                // temporary at all.
                RequireAbsent(directory, CaptureIndexName);

                CaptureIndexCommitFile file;
                switch (operation.CommitMode)
                {
                    case CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit:
                        file = CreateTemporary(directory, canonicalBytes);
                        break;

                    case CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit:
                        file = ReuseCanonicalTemporary(directory, canonicalBytes);
                        break;

                    case CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit:
                        file = ReplaceInvalidTemporary(directory, canonicalBytes);
                        break;

                    default:
                        throw new ArgumentException(
                            "Commit mode must be defined.", nameof(operation));
                }

                try
                {
                    // One handle-bound, non-overwriting rename. Its outcome is
                    // never re-derived, re-read, retried, or undone.
                    _fileSystem.Rename(file, directory, CaptureIndexName);
                }
                finally
                {
                    file.Dispose();
                }
            }
            finally
            {
                directory.Dispose();
            }

            return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
        }

        private CaptureIndexCommitFile CreateTemporary(
            CaptureIndexCommitDirectory directory,
            byte[] canonicalBytes)
        {
            RequireAbsent(directory, CaptureIndexTemporaryName);

            CaptureIndexCommitFile file = _fileSystem.CreateNew(directory, CaptureIndexTemporaryName);
            try
            {
                Write(file, canonicalBytes);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private CaptureIndexCommitFile ReuseCanonicalTemporary(
            CaptureIndexCommitDirectory directory,
            byte[] canonicalBytes)
        {
            CaptureIndexCommitFile file = OpenRegularFile(directory, CaptureIndexTemporaryName);
            try
            {
                // The existing temporary is committed unchanged only while it
                // still holds exactly these bytes.
                byte[] observed = ReadBounded(file.Stream);
                if (!HasSameBytes(observed, canonicalBytes))
                {
                    throw new InvalidDataException(
                        "capture.index.tmp no longer holds the authoritative canonical bytes.");
                }

                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private CaptureIndexCommitFile ReplaceInvalidTemporary(
            CaptureIndexCommitDirectory directory,
            byte[] canonicalBytes)
        {
            CaptureIndexCommitFile existing = OpenRegularFile(directory, CaptureIndexTemporaryName);

            bool unusable;
            try
            {
                byte[] observed = ReadBounded(existing.Stream);

                // A read failure is a failure to observe and never becomes
                // unusable content; only the codec's verdict on bytes that were
                // fully read decides that.
                unusable = !IsCanonicalDocument(observed);
            }
            catch
            {
                existing.Dispose();
                throw;
            }

            if (!unusable)
            {
                // A canonical document is another writer's evidence whether or
                // not it is this Run's, and is never deleted.
                existing.Dispose();
                throw new InvalidDataException(
                    "capture.index.tmp is a canonical document; refusing to replace it.");
            }

            try
            {
                // Only this attempt's own verified handle is deleted.
                _fileSystem.Delete(existing);
            }
            finally
            {
                existing.Dispose();
            }

            CaptureIndexCommitFile file = _fileSystem.CreateNew(directory, CaptureIndexTemporaryName);
            try
            {
                Write(file, canonicalBytes);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private static void Write(CaptureIndexCommitFile file, byte[] canonicalBytes)
        {
            file.Stream.Write(canonicalBytes, 0, canonicalBytes.Length);

            // Managed buffer flush only, so the bytes reach the file before the
            // rename. This claims no crash or power-loss durability, and no
            // file or directory flush is performed.
            file.Stream.Flush();
        }

        /// <summary>
        /// Reads at most one byte past the canonical limit, so a document too
        /// large to be canonical is observed as such instead of being read
        /// without bound. The buffer grows only as far as the bytes actually
        /// observed.
        /// </summary>
        private static byte[] ReadBounded(Stream stream)
        {
            const int Limit = CapturePublicationPlanCodec.MaximumCanonicalByteCount;
            byte[] buffer = new byte[InitialReadByteCount];
            int count = 0;
            while (count <= Limit)
            {
                if (count == buffer.Length)
                {
                    Array.Resize(
                        ref buffer,
                        buffer.Length <= (Limit + 1) / 2 ? buffer.Length * 2 : Limit + 1);
                }

                int read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0)
                {
                    break;
                }

                count = checked(count + read);
            }

            if (count > Limit)
            {
                throw new InvalidDataException(
                    "capture.index.tmp exceeds the canonical byte limit.");
            }

            byte[] observed = new byte[count];
            Array.Copy(buffer, observed, count);
            return observed;
        }

        private static bool IsCanonicalDocument(byte[] observed)
        {
            try
            {
                CapturePublicationPlanCodec.DeserializeCanonical(observed);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static bool HasSameBytes(byte[] observed, byte[] expected)
        {
            if (observed.Length != expected.Length)
            {
                return false;
            }

            for (int i = 0; i < observed.Length; i++)
            {
                if (observed[i] != expected[i])
                {
                    return false;
                }
            }

            return true;
        }

        private CaptureIndexCommitFile OpenRegularFile(
            CaptureIndexCommitDirectory directory,
            string name)
        {
            CaptureIndexFileOpen opened = _fileSystem.TryOpen(directory, name);
            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Opened:
                    return opened.File;

                case CaptureIndexFileOpenStatus.Absent:
                    throw new IOException(name + " is absent.");

                case CaptureIndexFileOpenStatus.InvalidFileKind:
                    throw new IOException(name + " is a reparse point or directory.");

                case CaptureIndexFileOpenStatus.EscapesRoot:
                    throw new IOException(name + " escapes the Run root.");

                case CaptureIndexFileOpenStatus.IoFailure:
                    throw new IOException("Failed to open " + name + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow open is not supported on this platform.");
            }
        }

        private void RequireAbsent(CaptureIndexCommitDirectory directory, string name)
        {
            CaptureIndexFileOpen opened = _fileSystem.TryOpen(directory, name);
            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Absent:
                    return;

                case CaptureIndexFileOpenStatus.Opened:
                    opened.File.Dispose();
                    throw new IOException(name + " already exists.");

                case CaptureIndexFileOpenStatus.InvalidFileKind:
                    throw new IOException(name + " is a reparse point or directory.");

                case CaptureIndexFileOpenStatus.EscapesRoot:
                    throw new IOException(name + " escapes the Run root.");

                case CaptureIndexFileOpenStatus.IoFailure:
                    throw new IOException("Failed to observe " + name + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow observation is not supported on this platform.");
            }
        }
    }
}
