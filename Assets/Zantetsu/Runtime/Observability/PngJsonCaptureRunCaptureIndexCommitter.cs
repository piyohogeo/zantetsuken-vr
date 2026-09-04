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

            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture index commit requires no-follow open and directory flush support on this platform.");
            }

            byte[] canonicalBytes = operation.GetCanonicalBytes();
            if (canonicalBytes == null || canonicalBytes.Length == 0)
            {
                throw new InvalidOperationException("Operation canonical bytes must be non-empty.");
            }

            switch (operation.Mode)
            {
                case CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit:
                    CreateTemporary(canonicalBytes, finalRunRoot, temporaryPath);
                    break;

                case CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit:
                    ReuseCanonicalTemporary(canonicalBytes, finalRunRoot, temporaryPath);
                    break;

                case CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit:
                    ReplaceInvalidTemporary(operation, canonicalBytes, finalRunRoot, temporaryPath);
                    break;

                default:
                    throw new ArgumentException("Commit mode must be defined.", nameof(operation));
            }

            CommitTemporaryToFinal(canonicalBytes, finalRunRoot, temporaryPath, finalPath);

            return PngJsonCaptureRunCaptureIndexCommitReceipt.Create(this, operation, token);
        }

        private void CreateTemporary(byte[] canonicalBytes, string finalRunRoot, string temporaryPath)
        {
            RequireAbsent(finalRunRoot, CaptureIndexName);
            RequireAbsent(finalRunRoot, CaptureIndexTemporaryName);

            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(canonicalBytes, 0, canonicalBytes.Length);
                stream.Flush(true);
                stream.Position = 0;
                RequireContentMatches(stream, canonicalBytes);
            }
        }

        private void ReuseCanonicalTemporary(byte[] canonicalBytes, string finalRunRoot, string temporaryPath)
        {
            RequireAbsent(finalRunRoot, CaptureIndexName);

            CaptureArtifactNoFollowOpenResult opened = OpenRegularFile(finalRunRoot, CaptureIndexTemporaryName);
            try
            {
                RequireContentMatches(opened.Stream, canonicalBytes);
            }
            finally
            {
                opened.Close();
            }

            // Durably flush the existing temporary data without rewriting or
            // deleting it.
            using (FileStream stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Flush(true);
            }
        }

        private void ReplaceInvalidTemporary(
            PngJsonCaptureRunCaptureIndexCommitOperation operation,
            byte[] canonicalBytes,
            string finalRunRoot,
            string temporaryPath)
        {
            RequireAbsent(finalRunRoot, CaptureIndexName);

            (int maximumPlanBytes, int maximumEntryCount, int maximumPathBytes) = GetInspectionLimits(operation);

            CaptureArtifactNoFollowOpenResult opened = OpenRegularFile(finalRunRoot, CaptureIndexTemporaryName);
            byte[] observed;
            try
            {
                observed = ReadBounded(opened.Stream, maximumPlanBytes);
            }
            finally
            {
                opened.Close();
            }

            // Confirm the temporary is still invalid under the exact recovery
            // inspection limits. A canonical temporary, whether it matches the
            // authoritative plan or another plan, is a hard failure and is never
            // deleted or replaced.
            bool canonical;
            try
            {
                PngJsonCapturePublicationPlanCodec.DeserializeCanonical(
                    observed, maximumPlanBytes, maximumEntryCount, maximumPathBytes);
                canonical = true;
            }
            catch (InvalidDataException)
            {
                canonical = false;
            }

            if (canonical)
            {
                throw new InvalidDataException("capture.index.tmp is canonical; refusing to replace it.");
            }

            // Delete only the confirmed fixed temporary, then durably flush the
            // final run root directory metadata.
            File.Delete(temporaryPath);
            _fileSystem.FlushDirectory(finalRunRoot);

            using (FileStream stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                stream.Write(canonicalBytes, 0, canonicalBytes.Length);
                stream.Flush(true);
                stream.Position = 0;
                RequireContentMatches(stream, canonicalBytes);
            }
        }

        private void CommitTemporaryToFinal(
            byte[] canonicalBytes,
            string finalRunRoot,
            string temporaryPath,
            string finalPath)
        {
            // Re-confirm the final is still absent immediately before the
            // non-overwriting atomic rename.
            RequireAbsent(finalRunRoot, CaptureIndexName);

            File.Move(temporaryPath, finalPath);

            _fileSystem.FlushDirectory(finalRunRoot);

            RequireAbsent(finalRunRoot, CaptureIndexTemporaryName);

            CaptureArtifactNoFollowOpenResult opened = OpenRegularFile(finalRunRoot, CaptureIndexName);
            try
            {
                RequireContentMatches(opened.Stream, canonicalBytes);
            }
            finally
            {
                opened.Close();
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

        private void RequireAbsent(string root, string relativeName)
        {
            CaptureArtifactNoFollowOpenResult result = _fileSystem.TryOpen(root, relativeName);
            switch (result.Status)
            {
                case CaptureArtifactNoFollowOpenStatus.Absent:
                    return;

                case CaptureArtifactNoFollowOpenStatus.Opened:
                    result.Close();
                    throw new IOException(relativeName + " already exists.");

                case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                    throw new IOException(relativeName + " is a reparse point or directory.");

                case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                    throw new IOException(relativeName + " escapes the run root.");

                case CaptureArtifactNoFollowOpenStatus.IoFailure:
                    throw new IOException("Failed to observe " + relativeName + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow observation is not supported on this platform.");
            }
        }

        private CaptureArtifactNoFollowOpenResult OpenRegularFile(string root, string relativeName)
        {
            CaptureArtifactNoFollowOpenResult result = _fileSystem.TryOpen(root, relativeName);
            switch (result.Status)
            {
                case CaptureArtifactNoFollowOpenStatus.Opened:
                    return result;

                case CaptureArtifactNoFollowOpenStatus.Absent:
                    throw new IOException(relativeName + " is absent.");

                case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                    throw new IOException(relativeName + " is a reparse point or directory.");

                case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                    throw new IOException(relativeName + " escapes the run root.");

                case CaptureArtifactNoFollowOpenStatus.IoFailure:
                    throw new IOException("Failed to open " + relativeName + ".");

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
