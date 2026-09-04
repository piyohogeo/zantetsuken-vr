using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete Capture Index committer that durably
    /// commits a validated PngJson Capture Index commit operation to its final
    /// path under the exact root layout it was constructed with, and must never
    /// overwrite an existing destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The committer is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// immutable filesystem collaborator. It holds no active operation,
    /// receipt, or canonical byte copy in fields, caches, or queues; canonical
    /// bytes are obtained transiently from the operation and discarded after
    /// the call.
    /// </para>
    /// <para>
    /// <see cref="Commit"/> validates the operation and token before any
    /// filesystem contact, requires the operation's root layout to be this
    /// committer's exact root layout, requires the fixed
    /// <c>capture.index.tmp</c> and <c>capture.index</c> names directly under
    /// the final run root, and rejects an unsupported no-follow platform before
    /// the first side effect. A foreign run, a foreign or stale token, or a
    /// released owner is therefore always filesystem-free.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, holds no lease directly,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. Calls are synchronous and single-attempt: no retry, no
    /// rollback, no journal, and no re-inspection.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCaptureRunCaptureIndexCommitter : IPngJsonCaptureRunCaptureIndexCommitter
    {
        private const string CaptureIndexTemporaryName = "capture.index.tmp";
        private const string CaptureIndexName = "capture.index";

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly ICaptureIndexCommitFileSystem _fileSystem;

        internal PngJsonCaptureRunCaptureIndexCommitter(CaptureRunRootLayout rootLayout)
            : this(rootLayout, CaptureIndexCommitFileSystem.Create())
        {
        }

        internal PngJsonCaptureRunCaptureIndexCommitter(
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

        public PngJsonCaptureRunCaptureIndexCommitReceipt Commit(
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!operation.IsValidWithToken(token))
            {
                throw new ArgumentException("Operation must be valid for the supplied token.", nameof(operation));
            }

            if (!ReferenceEquals(operation.RootLayout, _rootLayout))
            {
                throw new ArgumentException("Operation root layout must match the committer's root layout.", nameof(operation));
            }

            string finalRunRoot = _rootLayout.FinalRunRoot;
            string temporaryPath = Path.GetFullPath(Path.Combine(finalRunRoot, CaptureIndexTemporaryName));
            string finalPath = Path.GetFullPath(Path.Combine(finalRunRoot, CaptureIndexName));

            if (!string.Equals(operation.TemporaryPath, temporaryPath, StringComparison.Ordinal)
                || !string.Equals(operation.FinalPath, finalPath, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "Operation must target the fixed capture.index paths directly under the final run root.",
                    nameof(operation));
            }

            // Preflight both capabilities before the first filesystem contact
            // so a platform that cannot flush directory metadata never reaches
            // temporary creation or rename.
            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture index commit requires no-follow open support on this platform.");
            }

            if (!_fileSystem.IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture index commit requires directory metadata flush support on this platform.");
            }

            byte[] canonicalBytes = operation.GetCanonicalBytes();
            if (canonicalBytes == null || canonicalBytes.Length == 0)
            {
                throw new InvalidOperationException("Operation canonical bytes must be non-empty.");
            }

            using (CaptureIndexCommitDirectory directory = _fileSystem.OpenDirectory(finalRunRoot))
            {
                CaptureIndexCommitFile file;
                switch (operation.Mode)
                {
                    case CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit:
                        file = CreateTemporary(directory, canonicalBytes);
                        break;

                    case CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit:
                        file = ReuseCanonicalTemporary(directory, canonicalBytes);
                        break;

                    case CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit:
                        file = ReplaceInvalidTemporary(directory, operation, canonicalBytes);
                        break;

                    default:
                        throw new ArgumentException("Commit mode must be defined.", nameof(operation));
                }

                try
                {
                    CommitTemporaryToFinal(directory, file, canonicalBytes);
                }
                finally
                {
                    file.Dispose();
                }
            }

            return PngJsonCaptureRunCaptureIndexCommitReceipt.Create(this, operation, token);
        }

        private CaptureIndexCommitFile CreateTemporary(CaptureIndexCommitDirectory directory, byte[] canonicalBytes)
        {
            RequireAbsent(directory, CaptureIndexName);
            RequireAbsent(directory, CaptureIndexTemporaryName);

            CaptureIndexCommitFile file = _fileSystem.CreateNew(directory, CaptureIndexTemporaryName);
            try
            {
                WriteAndVerify(file, canonicalBytes);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private CaptureIndexCommitFile ReuseCanonicalTemporary(CaptureIndexCommitDirectory directory, byte[] canonicalBytes)
        {
            RequireAbsent(directory, CaptureIndexName);

            CaptureIndexCommitFile file = OpenRegularFile(directory, CaptureIndexTemporaryName);
            try
            {
                RequireContentMatches(file.Stream, canonicalBytes);
                _fileSystem.FlushFileData(file);
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
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            byte[] canonicalBytes)
        {
            RequireAbsent(directory, CaptureIndexName);

            (int maximumPlanBytes, int maximumEntryCount, int maximumPathBytes) = GetInspectionLimits(operation);

            CaptureIndexCommitFile existing = OpenRegularFile(directory, CaptureIndexTemporaryName);

            bool canonical;
            try
            {
                byte[] observed = ReadBounded(existing.Stream, maximumPlanBytes);
                canonical = TryDecodeCanonical(observed, maximumPlanBytes, maximumEntryCount, maximumPathBytes);
            }
            catch
            {
                existing.Dispose();
                throw;
            }

            if (canonical)
            {
                existing.Dispose();
                throw new InvalidDataException("capture.index.tmp is canonical; refusing to replace it.");
            }

            // Delete only the exact verified temporary through its handle, then
            // durably flush the final run root directory metadata.
            try
            {
                _fileSystem.Delete(existing);
            }
            finally
            {
                existing.Dispose();
            }

            _fileSystem.FlushDirectory(directory);

            CaptureIndexCommitFile file = _fileSystem.CreateNew(directory, CaptureIndexTemporaryName);
            try
            {
                WriteAndVerify(file, canonicalBytes);
                return file;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        private void CommitTemporaryToFinal(
            CaptureIndexCommitDirectory directory,
            CaptureIndexCommitFile file,
            byte[] canonicalBytes)
        {
            // Re-confirm the final is still absent immediately before the
            // handle-bound non-overwriting rename.
            RequireAbsent(directory, CaptureIndexName);

            _fileSystem.Rename(file, directory, CaptureIndexName);

            _fileSystem.FlushDirectory(directory);

            RequireAbsent(directory, CaptureIndexTemporaryName);

            // Verify the renamed final through the same file identity handle.
            file.Stream.Position = 0;
            RequireContentMatches(file.Stream, canonicalBytes);
        }

        private void WriteAndVerify(CaptureIndexCommitFile file, byte[] canonicalBytes)
        {
            file.Stream.Write(canonicalBytes, 0, canonicalBytes.Length);
            _fileSystem.FlushFileData(file);
            file.Stream.Position = 0;
            RequireContentMatches(file.Stream, canonicalBytes);
        }

        private static bool TryDecodeCanonical(byte[] observed, int maximumPlanBytes, int maximumEntryCount, int maximumPathBytes)
        {
            try
            {
                PngJsonCapturePublicationPlanCodec.DeserializeCanonical(
                    observed, maximumPlanBytes, maximumEntryCount, maximumPathBytes);
                return true;
            }
            catch (InvalidDataException)
            {
                return false;
            }
        }

        private static (int maximumPlanBytes, int maximumEntryCount, int maximumPathBytes) GetInspectionLimits(
            PngJsonCaptureRunCaptureIndexCommitOperation operation)
        {
            CaptureRunPublicationRecoveryDecision recoveryDecision = operation.Authority.RecoveryDecision;
            if (recoveryDecision == null)
            {
                throw new InvalidOperationException(
                    "ReplaceInvalidTemporaryAndCommit requires a recovery decision authority.");
            }

            CaptureRunPublicationRecoveryInspectionOperation inspection = recoveryDecision.Snapshot.Operation;
            return (inspection.MaximumPlanBytes, inspection.MaximumEntryCount, inspection.MaximumPathBytes);
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
                    throw new IOException(name + " escapes the run root.");

                case CaptureIndexFileOpenStatus.IoFailure:
                    throw new IOException("Failed to observe " + name + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow observation is not supported on this platform.");
            }
        }

        private CaptureIndexCommitFile OpenRegularFile(CaptureIndexCommitDirectory directory, string name)
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
                    throw new IOException(name + " escapes the run root.");

                case CaptureIndexFileOpenStatus.IoFailure:
                    throw new IOException("Failed to open " + name + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow open is not supported on this platform.");
            }
        }

        private static void RequireContentMatches(Stream stream, byte[] canonicalBytes)
        {
            long length;
            try
            {
                length = stream.Length;
            }
            catch (Exception ex)
            {
                throw new IOException("Failed to read the file length.", ex);
            }

            if (length != canonicalBytes.LongLength)
            {
                throw new InvalidDataException("File byte length does not match the canonical bytes.");
            }

            stream.Position = 0;

            byte[] buffer = new byte[CaptureArtifactFileStore.VerificationBufferLength];
            int offset = 0;
            while (offset < canonicalBytes.Length)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, canonicalBytes.Length - offset));
                if (read == 0)
                {
                    throw new InvalidDataException("File content is shorter than the canonical bytes.");
                }

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != canonicalBytes[offset + i])
                    {
                        throw new InvalidDataException("File content does not match the canonical bytes.");
                    }
                }

                offset += read;
            }
        }

        private static byte[] ReadBounded(Stream stream, int maximumByteCount)
        {
            byte[] buffer = new byte[checked(maximumByteCount + 1)];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = stream.Read(buffer, count, buffer.Length - count);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > maximumByteCount)
            {
                throw new InvalidDataException("capture.index.tmp exceeds the inspection byte limit.");
            }

            if (count == 0)
            {
                throw new InvalidDataException("capture.index.tmp is empty.");
            }

            byte[] result = new byte[count];
            Array.Copy(buffer, result, count);
            return result;
        }
    }
}
