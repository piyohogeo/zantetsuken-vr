using System;
using System.ComponentModel;
using System.IO;
using System.Security;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete Fresh NVENC CaptureComplete cleaner: it
    /// removes the staging side of one successfully completed Run in a fixed
    /// order, each target exactly once, through handle-bound no-follow deletes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cleaner is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// immutable filesystem collaborator, and holds no operation, result,
    /// stream, or handle in fields. It is not an <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// The published side is never touched: the final Run root, the final
    /// chunk, and the capture index are left alone, and so are the Registry,
    /// the disposition, the Session Ownership Lease, the Publication Service,
    /// the legacy <c>publication.plan.tmp</c>, and any Recovery classification.
    /// The staging chunk itself is not deleted either: the artifact
    /// publication already moved it to the final root, so this cleaner only
    /// confirms that the staging <c>chunks</c> directory is empty before
    /// removing it.
    /// </para>
    /// <para>
    /// <see cref="Clean"/> validates before any filesystem contact: a
    /// <c>null</c> operation throws <see cref="ArgumentNullException"/>, an
    /// invalid operation and a foreign root layout throw
    /// <see cref="ArgumentException"/>, and both filesystem capabilities are
    /// preflighted together and raised as
    /// <see cref="CaptureArtifactNoFollowUnavailableException"/> before the
    /// first deletion, so a contract violation or an unsupported platform is
    /// never reported as an ordinary Failed cleanup.
    /// </para>
    /// <para>
    /// A known cleanup failure - a content mismatch, a missing target, a
    /// non-empty directory, a refused reparse point, or an ordinary filesystem
    /// error - is published as
    /// <see cref="NvencRunCaptureCompleteCleanupStatus.Failed"/>. Unexpected
    /// programming or invariant failures are deliberately not caught. There is
    /// no retry, no alternate path, no rollback, no restoration of an
    /// already-deleted target, and no second run inside one call, so a failure
    /// part way through leaves the earlier deletions done; the result carries
    /// no progress position, and a later step re-inspects the actual file set
    /// under the still-held Run lock. Handles and streams are released on every
    /// path.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteCleaner : INvencRunCaptureCompleteCleaner
    {
        private const string PublicationPlanName = "publication.plan";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const int VerificationBufferLength = 64 * 1024;

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly ICaptureCompleteCleanupFileSystem _fileSystem;

        internal NvencRunCaptureCompleteCleaner(CaptureRunRootLayout rootLayout)
            : this(rootLayout, CaptureIndexCommitFileSystem.Create())
        {
        }

        internal NvencRunCaptureCompleteCleaner(
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

        public NvencRunCaptureCompleteCleanupAttemptResult Clean(
            NvencRunCaptureCompleteCleanupOperation operation)
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
                    "Operation root layout must match the cleaner's root layout.", nameof(operation));
            }

            // Preflight both capabilities together, before the first deletion.
            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "CaptureComplete cleanup requires no-follow open support on this platform.");
            }

            if (!_fileSystem.IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "CaptureComplete cleanup requires directory metadata flush support on this platform.");
            }

            try
            {
                DeleteStagingPublicationPlan(operation);
                RemoveStagingChunksDirectory();
                DeleteStagingReadyMarker(operation);
                DeleteStagingInitializationMarker(operation);
                RemoveStagingRunRoot();
            }
            catch (Exception ex) when (IsKnownCleanupFailure(ex))
            {
                return NvencRunCaptureCompleteCleanupAttemptResult.Failed(this, operation);
            }

            return NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(this, operation);
        }

        /// <summary>
        /// Step 1: the staging publication plan must still carry the exact
        /// canonical bytes of the operation's plan before it is deleted.
        /// </summary>
        private void DeleteStagingPublicationPlan(NvencRunCaptureCompleteCleanupOperation operation)
        {
            byte[] canonical = CapturePublicationPlanCodec.SerializeCanonical(operation.Plan);

            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                CaptureIndexCommitFile plan = OpenRegularFile(staging, PublicationPlanName);
                try
                {
                    if (!StreamMatches(plan.Stream, canonical))
                    {
                        throw new InvalidDataException(
                            "The staging publication plan does not match the operation's canonical plan bytes.");
                    }

                    _fileSystem.Delete(plan);
                }
                finally
                {
                    plan.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        /// <summary>
        /// Step 2: the staging chunk was already moved to the final root by the
        /// artifact publication, so the chunks directory must be empty. It is
        /// removed non-recursively and nothing inside it is ever deleted.
        /// </summary>
        private void RemoveStagingChunksDirectory()
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                using (CaptureIndexCommitDirectory chunks = _fileSystem.OpenDirectory(
                    Path.Combine(_rootLayout.StagingRunRoot, ChunksDirectoryName)))
                {
                    if (!_fileSystem.IsDirectoryEmpty(chunks))
                    {
                        throw new IOException("The staging chunks directory is not empty.");
                    }

                    _fileSystem.DeleteDirectory(chunks);
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        /// <summary>
        /// Step 3: the staging ready marker must correlate to the Run identity
        /// and to its staging peer, the staging initialization marker whose
        /// content hash it records, before it is deleted.
        /// </summary>
        private void DeleteStagingReadyMarker(NvencRunCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                CaptureIndexCommitFile readyFile = OpenRegularFile(staging, RunReadyMarkerName);
                try
                {
                    CaptureRunReadyMarker ready = CaptureRunReadyMarkerCodec.DeserializeCanonical(
                        readyFile.Stream, CaptureRunReadyMarkerCodec.MaximumCanonicalByteCount);

                    if (ready.TestRunId != operation.TestRunId)
                    {
                        throw new InvalidDataException(
                            "The ready marker TestRunId does not match the operation.");
                    }

                    if (!string.Equals(
                            ready.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The ready marker RunInitializationId does not match the operation.");
                    }

                    RequireReadyBindsStagingInitialization(staging, ready, operation);

                    _fileSystem.Delete(readyFile);
                }
                finally
                {
                    readyFile.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        /// <summary>
        /// Step 4: the staging initialization marker must correlate to the root
        /// layout and the Run identity, and the ready marker must already be
        /// gone, before it is deleted.
        /// </summary>
        private void DeleteStagingInitializationMarker(NvencRunCaptureCompleteCleanupOperation operation)
        {
            using (CaptureIndexCommitDirectory staging = _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
            {
                RequireAbsent(staging, RunReadyMarkerName);

                CaptureIndexCommitFile initFile = OpenRegularFile(staging, RunInitializationMarkerName);
                try
                {
                    CaptureRunInitializationMarker init = CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                        initFile.Stream, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);

                    RequireInitializationMarkerCorrelates(init, operation);

                    _fileSystem.Delete(initFile);
                }
                finally
                {
                    initFile.Dispose();
                }

                _fileSystem.FlushDirectory(staging);
            }
        }

        /// <summary>
        /// Step 5: with every staging target gone the staging Run root must be
        /// empty, and is removed non-recursively. Its own parent directory - the
        /// directory whose entry the deletion changes - is opened first so a
        /// flush capability failure is observed before the Run root is deleted.
        /// </summary>
        private void RemoveStagingRunRoot()
        {
            using (CaptureIndexCommitDirectory baseDirectory =
                _fileSystem.OpenDirectory(StagingRunRootParent()))
            {
                using (CaptureIndexCommitDirectory staging =
                    _fileSystem.OpenDirectory(_rootLayout.StagingRunRoot))
                {
                    RequireAbsent(staging, PublicationPlanName);
                    RequireAbsent(staging, ChunksDirectoryName);
                    RequireAbsent(staging, RunReadyMarkerName);
                    RequireAbsent(staging, RunInitializationMarkerName);

                    if (!_fileSystem.IsDirectoryEmpty(staging))
                    {
                        throw new IOException("The staging Run root is not empty.");
                    }

                    _fileSystem.DeleteDirectory(staging);
                }

                _fileSystem.FlushDirectory(baseDirectory);
            }
        }

        /// <summary>
        /// The directory that holds the staging Run root's own entry. The Run
        /// root sits at a fixed relative path under the trusted base root, so
        /// this is always a directory strictly inside that base root.
        /// </summary>
        private string StagingRunRootParent()
        {
            string parent = Path.GetDirectoryName(_rootLayout.StagingRunRoot);
            if (string.IsNullOrEmpty(parent)
                || parent.Length <= _rootLayout.StagingTrustedBaseRoot.Length
                || !parent.StartsWith(_rootLayout.StagingTrustedBaseRoot, StringComparison.Ordinal))
            {
                throw new IOException("The staging Run root has no parent inside the staging trusted base root.");
            }

            return parent;
        }

        private void RequireReadyBindsStagingInitialization(
            CaptureIndexCommitDirectory staging,
            CaptureRunReadyMarker ready,
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            CaptureIndexCommitFile initFile = OpenRegularFile(staging, RunInitializationMarkerName);
            try
            {
                CaptureRunInitializationMarker init = CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                    initFile.Stream, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);

                RequireInitializationMarkerCorrelates(init, operation);

                if (!string.Equals(
                        ready.StagingInitSha256,
                        CaptureRunInitializationMarkerCodec.ComputeContentSha256(init),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "The ready marker StagingInitSha256 does not match the staging initialization marker.");
                }
            }
            finally
            {
                initFile.Dispose();
            }
        }

        private void RequireInitializationMarkerCorrelates(
            CaptureRunInitializationMarker init,
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            if (init.TestRunId != operation.TestRunId)
            {
                throw new InvalidDataException(
                    "The initialization marker TestRunId does not match the operation.");
            }

            if (!string.Equals(
                    init.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker RunInitializationId does not match the operation.");
            }

            if (init.RootRole != CaptureRunRootRole.Staging)
            {
                throw new InvalidDataException(
                    "The staging initialization marker does not carry the staging root role.");
            }

            if (!string.Equals(
                    init.StagingRunRootSha256, _rootLayout.StagingRunRootSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker StagingRunRootSha256 does not match the root layout.");
            }

            if (!string.Equals(
                    init.FinalRunRootSha256, _rootLayout.FinalRunRootSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker FinalRunRootSha256 does not match the root layout.");
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

        /// <summary>
        /// The ordinary cleanup failures this boundary converts into a Failed
        /// result: content mismatches, missing or wrong-kind targets, non-empty
        /// directories, access denials, and plain I/O errors. Unexpected
        /// programming and invariant failures, and the capability signal, are
        /// deliberately absent so they propagate.
        /// </summary>
        private static bool IsKnownCleanupFailure(Exception ex)
        {
            return ex is IOException
                || ex is InvalidDataException
                || ex is UnauthorizedAccessException
                || ex is SecurityException
                || ex is Win32Exception
                || ex is NotSupportedException;
        }
    }
}
