using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete capture-complete cleanup backend that
    /// applies one validated cleanup operation to the actual filesystem,
    /// deleting exactly the fixed target of a single cleanup step under the
    /// held recovery lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The backend is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// immutable filesystem collaborator. It holds no operation, token,
    /// receipt, owner, handle, or canonical byte copy after the call returns.
    /// Only a success receipt may hold the operation and token references.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> rejects null arguments, requires the operation to
    /// be index-locally valid for the token exactly once before any side
    /// effect, requires the operation's root layout to be this backend's exact
    /// root layout, preflights the no-follow and directory-flush capabilities,
    /// performs the action-specific re-verification and one side effect, and
    /// flushes the parent directory metadata before issuing the receipt. There
    /// is no retry, rollback, journal, or re-inspection: a missing target is a
    /// hard failure, and a flush failure returns no receipt without rolling
    /// back an already completed deletion.
    /// </para>
    /// <para>
    /// Every deletion is handle-bound: the target is opened no-follow, its
    /// identity and content re-verified through that exact handle, and only
    /// that handle is deleted. A path or parent-directory swap after
    /// verification cannot redirect a delete to a different file or outside
    /// the run root.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupBackend : IPngJsonCapturePublicationCaptureCompleteCleanupBackend
    {
        private const string CaptureIndexName = "capture.index";
        private const string CaptureIndexTemporaryName = "capture.index.tmp";
        private const string PublicationPlanName = "publication.plan";
        private const string PublicationPlanTemporaryName = "publication.plan.tmp";
        private const string FramesDirectoryName = "frames";
        private const string RunInitializationMarkerName = "run.init";
        private const string RunReadyMarkerName = "run.ready";

        private const int VerificationBufferLength = 64 * 1024;

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly ICaptureCompleteCleanupFileSystem _fileSystem;

        internal PngJsonCapturePublicationCaptureCompleteCleanupBackend(CaptureRunRootLayout rootLayout)
            : this(rootLayout, CaptureIndexCommitFileSystem.Create())
        {
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupBackend(
            CaptureRunRootLayout rootLayout,
            ICaptureCompleteCleanupFileSystem fileSystem)
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

        public PngJsonCapturePublicationCaptureCompleteCleanupReceipt Execute(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!operation.IsValidIndexLocal(token))
            {
                throw new ArgumentException(
                    "Cleanup operation must be index-locally valid for the supplied token.",
                    nameof(operation));
            }

            if (!ReferenceEquals(operation.RootLayout, _rootLayout))
            {
                throw new ArgumentException(
                    "Cleanup operation root layout must match the backend's root layout.",
                    nameof(operation));
            }

            // Preflight both capabilities before the first filesystem contact.
            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture-complete cleanup requires no-follow open support on this platform.");
            }

            if (!_fileSystem.IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Capture-complete cleanup requires directory metadata flush support on this platform.");
            }

            switch (operation.Action)
            {
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                    DeletePublicationPlanTemporary(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                    DeleteCaptureIndexTemporary(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact:
                    DeleteStagingArtifact(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                    RemoveStagingFramesRoot(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                    DeletePublicationPlan(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                    DeleteStagingReadyMarker(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                    DeleteStagingInitializationMarker(operation);
                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                    RemoveStagingRunRoot(operation);
                    break;

                default:
                    throw new ArgumentException(
                        "Cleanup action must be a side-effecting cleanup action.",
                        nameof(operation));
            }

            return PngJsonCapturePublicationCaptureCompleteCleanupReceipt.Create(this, operation, token);
        }

        private void DeletePublicationPlanTemporary(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                CaptureIndexCommitFile file = OpenRegularFile(staging, PublicationPlanTemporaryName);
                try
                {
                    RequirePlanMatches(file, operation.AuthoritativePlan);
                    _fileSystem.Delete(file);
                }
                finally
                {
                    file.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        private void DeleteCaptureIndexTemporary(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory final = _fileSystem.OpenDirectory(_rootLayout.FinalRunRoot))
            {
                CaptureIndexCommitFile index = OpenRegularFile(final, CaptureIndexName);
                try
                {
                    RequirePlanMatches(index, operation.AuthoritativePlan);
                }
                finally
                {
                    index.Dispose();
                }

                CaptureIndexCommitFile temporary = OpenRegularFile(final, CaptureIndexTemporaryName);
                try
                {
                    RequirePlanMatches(temporary, operation.AuthoritativePlan);
                    _fileSystem.Delete(temporary);
                }
                finally
                {
                    temporary.Dispose();
                }

                _fileSystem.FlushDirectory(final);
            }
        }

        private void DeleteStagingArtifact(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            string targetPath = operation.TargetPath;
            string parentPath = Path.GetDirectoryName(targetPath);
            string name = Path.GetFileName(targetPath);

            if (!string.Equals(parentPath, operation.PublicationPaths.StagingFramesRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Staging artifact target must be directly under the staging frames root.");
            }

            using (CaptureIndexCommitDirectory frames = _fileSystem.OpenDirectory(parentPath))
            {
                CaptureIndexCommitFile file = OpenRegularFile(frames, name);
                try
                {
                    RequireArtifactMatches(file, operation.ExpectedByteCount, operation.ExpectedContentSha256);
                    _fileSystem.Delete(file);
                }
                finally
                {
                    file.Dispose();
                }

                _fileSystem.FlushDirectory(frames);
            }
        }

        private void RemoveStagingFramesRoot(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                using (CaptureIndexCommitDirectory frames = _fileSystem.OpenDirectory(operation.PublicationPaths.StagingFramesRoot))
                {
                    if (!_fileSystem.IsDirectoryEmpty(frames))
                    {
                        throw new IOException("Staging frames directory is not empty.");
                    }

                    _fileSystem.DeleteDirectory(frames);
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        private void DeletePublicationPlan(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory final = _fileSystem.OpenDirectory(_rootLayout.FinalRunRoot))
            {
                CaptureIndexCommitFile index = OpenRegularFile(final, CaptureIndexName);
                try
                {
                    RequirePlanMatches(index, operation.AuthoritativePlan);
                }
                finally
                {
                    index.Dispose();
                }
            }

            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                CaptureIndexCommitFile plan = OpenRegularFile(staging, PublicationPlanName);
                try
                {
                    RequirePlanMatches(plan, operation.AuthoritativePlan);
                    _fileSystem.Delete(plan);
                }
                finally
                {
                    plan.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        private void DeleteStagingReadyMarker(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                CaptureIndexCommitFile readyFile = OpenRegularFile(staging, RunReadyMarkerName);
                try
                {
                    CaptureRunReadyMarker ready = CaptureRunReadyMarkerCodec.DeserializeCanonical(
                        readyFile.Stream, CaptureRunReadyMarkerCodec.MaximumCanonicalByteCount);

                    RequireReadyMarkerCorrelates(ready, operation, staging);

                    _fileSystem.Delete(readyFile);
                }
                finally
                {
                    readyFile.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        private void RequireReadyMarkerCorrelates(
            CaptureRunReadyMarker ready,
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            CaptureIndexCommitDirectory staging)
        {
            if (ready.TestRunId != operation.TestRunId)
            {
                throw new InvalidDataException("Ready marker TestRunId does not match the operation.");
            }

            if (!string.Equals(ready.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Ready marker RunInitializationId does not match the operation.");
            }

            using (CaptureIndexCommitDirectory final = _fileSystem.OpenDirectory(_rootLayout.FinalRunRoot))
            {
                using (CaptureIndexCommitFile stagingInitFile = OpenRegularFile(staging, RunInitializationMarkerName))
                using (CaptureIndexCommitFile finalInitFile = OpenRegularFile(final, RunInitializationMarkerName))
                {
                    CaptureRunInitializationMarker stagingInit = CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                        stagingInitFile.Stream, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);
                    CaptureRunInitializationMarker finalInit = CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                        finalInitFile.Stream, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);

                    RequireInitMarkerCorrelates(stagingInit, operation, CaptureRunRootRole.Staging);
                    RequireInitMarkerCorrelates(finalInit, operation, CaptureRunRootRole.Final);

                    if (!string.Equals(
                        ready.StagingInitSha256,
                        CaptureRunInitializationMarkerCodec.ComputeContentSha256(stagingInit),
                        StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Ready marker StagingInitSha256 does not match the staging init marker.");
                    }

                    if (!string.Equals(
                        ready.FinalInitSha256,
                        CaptureRunInitializationMarkerCodec.ComputeContentSha256(finalInit),
                        StringComparison.Ordinal))
                    {
                        throw new InvalidDataException("Ready marker FinalInitSha256 does not match the final init marker.");
                    }
                }
            }
        }

        private void DeleteStagingInitializationMarker(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                RequireAbsent(staging, RunReadyMarkerName);

                CaptureIndexCommitFile initFile = OpenRegularFile(staging, RunInitializationMarkerName);
                try
                {
                    CaptureRunInitializationMarker init = CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                        initFile.Stream, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);

                    RequireInitMarkerCorrelates(init, operation, CaptureRunRootRole.Staging);

                    _fileSystem.Delete(initFile);
                }
                finally
                {
                    initFile.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        private void RemoveStagingRunRoot(
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation)
        {
            // Open (and probe flush capability on) the real parent directory
            // that holds the staging run root's own entry BEFORE the run root
            // is deleted, so a flush capability or open failure rejects before
            // any side effect. Flushing the trusted staging base root would
            // flush a grandparent that does not hold the deleted entry.
            using (CaptureIndexCommitDirectory parent = _fileSystem.OpenDirectory(StagingRunRootParent()))
            {
                using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
                {
                    RequireAbsent(staging, RunInitializationMarkerName);
                    RequireAbsent(staging, RunReadyMarkerName);
                    RequireAbsent(staging, PublicationPlanName);
                    RequireAbsent(staging, FramesDirectoryName);

                    if (!_fileSystem.IsDirectoryEmpty(staging))
                    {
                        throw new IOException("Staging run root is not empty.");
                    }

                    _fileSystem.DeleteDirectory(staging);
                }

                // The staging handle is released by the inner using before the
                // parent metadata is flushed.
                _fileSystem.FlushDirectory(parent);
            }
        }

        /// <summary>
        /// The directory that holds the staging run root's own entry. The run
        /// root sits at a fixed relative path under the already-validated
        /// trusted base root, so this derives no new containment authority: it
        /// only names the parent of a path the layout already verified.
        /// </summary>
        private string StagingRunRootParent()
        {
            string parent = Path.GetDirectoryName(_rootLayout.StagingRunRoot);
            if (string.IsNullOrEmpty(parent)
                || parent.Length <= _rootLayout.StagingTrustedBaseRoot.Length
                || !parent.StartsWith(_rootLayout.StagingTrustedBaseRoot, StringComparison.Ordinal))
            {
                throw new IOException("Staging run root has no parent inside the staging trusted base root.");
            }

            return parent;
        }

        private void RequireInitMarkerCorrelates(
            CaptureRunInitializationMarker init,
            PngJsonCapturePublicationCaptureCompleteCleanupOperation operation,
            CaptureRunRootRole expectedRole)
        {
            if (init.TestRunId != operation.TestRunId)
            {
                throw new InvalidDataException("Initialization marker TestRunId does not match the operation.");
            }

            if (!string.Equals(init.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Initialization marker RunInitializationId does not match the operation.");
            }

            if (init.RootRole != expectedRole)
            {
                throw new InvalidDataException("Initialization marker RootRole does not match the expected role.");
            }

            if (!string.Equals(init.StagingRunRootSha256, _rootLayout.StagingRunRootSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Initialization marker StagingRunRootSha256 does not match the root layout.");
            }

            if (!string.Equals(init.FinalRunRootSha256, _rootLayout.FinalRunRootSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Initialization marker FinalRunRootSha256 does not match the root layout.");
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

        private void RequireAbsent(CaptureIndexCommitDirectory directory, string name)
        {
            CaptureIndexFileOpen opened = _fileSystem.TryOpen(directory, name);
            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Absent:
                    return;

                case CaptureIndexFileOpenStatus.Opened:
                    opened.File.Dispose();
                    throw new IOException(name + " still exists.");

                case CaptureIndexFileOpenStatus.InvalidFileKind:
                    throw new IOException(name + " still exists as a reparse point or directory.");

                case CaptureIndexFileOpenStatus.EscapesRoot:
                    throw new IOException(name + " escapes the run root.");

                case CaptureIndexFileOpenStatus.IoFailure:
                    throw new IOException("Failed to observe " + name + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow observation is not supported on this platform.");
            }
        }

        private static void RequirePlanMatches(CaptureIndexCommitFile file, PngJsonCapturePublicationPlan plan)
        {
            byte[] canonical = PngJsonCapturePublicationPlanCodec.SerializeCanonical(plan);
            if (!StreamMatches(file.Stream, canonical))
            {
                throw new InvalidDataException("File content does not match the authoritative plan canonical bytes.");
            }
        }

        private static bool StreamMatches(Stream stream, byte[] expected)
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

            if (length != expected.LongLength)
            {
                return false;
            }

            stream.Position = 0;

            byte[] buffer = new byte[VerificationBufferLength];
            int offset = 0;
            while (offset < expected.Length)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, expected.Length - offset));
                if (read == 0)
                {
                    return false;
                }

                for (int i = 0; i < read; i++)
                {
                    if (buffer[i] != expected[offset + i])
                    {
                        return false;
                    }
                }

                offset += read;
            }

            return true;
        }

        private static void RequireArtifactMatches(
            CaptureIndexCommitFile file,
            long expectedByteCount,
            string expectedContentSha256)
        {
            Stream stream = file.Stream;

            long length;
            try
            {
                length = stream.Length;
            }
            catch (Exception ex)
            {
                throw new IOException("Failed to read the file length.", ex);
            }

            if (length != expectedByteCount)
            {
                throw new InvalidDataException("Artifact byte length does not match the expected byte count.");
            }

            stream.Position = 0;

            long observed = 0;
            string actualHash;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[VerificationBufferLength];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    observed = checked(observed + read);
                    if (observed > expectedByteCount)
                    {
                        throw new InvalidDataException("Artifact byte length exceeds the expected byte count.");
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);
                actualHash = ToLowerHex(sha.Hash);
            }

            if (observed != expectedByteCount)
            {
                throw new InvalidDataException("Artifact byte length does not match the expected byte count.");
            }

            if (!string.Equals(actualHash, expectedContentSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Artifact SHA-256 does not match the expected content hash.");
            }
        }

        private static string ToLowerHex(byte[] hash)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = hex[hash[i] >> 4];
                chars[i * 2 + 1] = hex[hash[i] & 15];
            }

            return new string(chars);
        }
    }
}
