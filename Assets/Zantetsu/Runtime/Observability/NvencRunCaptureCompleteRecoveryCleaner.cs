using System;
using System.ComponentModel;
using System.IO;
using System.Security;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production Phase 0.11 NVENC recovery
    /// CaptureComplete cleaner: it removes what a recovered Run leaves behind -
    /// the final Capture Index temporary and the staging side - in a fixed
    /// order, each target at most once, through handle-bound no-follow
    /// deletes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cleaner is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// existing filesystem collaborator; no new Win32 or NT backend is
    /// introduced. It holds no operation, result, receipt, canonical byte copy,
    /// stream, or handle in fields and is not an <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// The published side is authoritative and untouched: the final chunk and
    /// <c>capture.index</c> are never opened, read, deleted, renamed, or
    /// written, and the Capture Index authority the operation's graph already
    /// carries is reused rather than re-derived. Only
    /// <c>capture.index.tmp</c> under the final Run root, and the staging
    /// <c>chunks</c> directory, <c>publication.plan</c>, <c>run.ready</c>,
    /// <c>run.init</c>, and the empty staging Run root, are ever targets.
    /// </para>
    /// <para>
    /// Because a failed cleanup leaves the earlier deletions done, every step
    /// accepts a confirmed absent target as already processed, so a later
    /// attempt can resume. That tolerance is for absence alone: an
    /// unobservable target, a reparse point, a path escaping its root, a
    /// different file kind, and an access or I/O failure are never folded into
    /// absence, and a residual shape that cannot be true - a ready marker
    /// still present with no initialization marker to bind it - is a failure.
    /// </para>
    /// <para>
    /// Each step ends with a flush of the directory that actually holds its
    /// entry - <c>&lt;staging base&gt;/runs</c> for the staging Run root
    /// itself - and that flush happens whether this attempt deleted the entry
    /// or found it already gone. An earlier attempt may have deleted an entry
    /// and then failed to flush, so a step is only "processed" once its own
    /// flush has succeeded here; nothing relies on some other call flushing
    /// that directory as a side effect. The one case with nothing to flush is
    /// a staging Run root that is itself already gone, since the directory
    /// those entries lived in no longer exists.
    /// </para>
    /// <para>
    /// One call is one attempt. There is no retry, rollback, re-creation,
    /// alternate-path cleanup, or recursive delete, and a failure stops the
    /// sequence rather than continuing to the next target. A known cleanup
    /// failure is published as Failed; a programming error, a broken contract,
    /// and a missing capability propagate instead. Handles and streams are
    /// released on every path, and a release failure never replaces the
    /// failure that was already on its way out.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryCleaner
        : INvencRunCaptureCompleteRecoveryCleaner
    {
        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const int InitialReadByteCount = 64 * 1024;

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly ICaptureCompleteCleanupFileSystem _fileSystem;

        internal NvencRunCaptureCompleteRecoveryCleaner(CaptureRunRootLayout rootLayout)
            : this(rootLayout, CaptureIndexCommitFileSystem.Create())
        {
        }

        internal NvencRunCaptureCompleteRecoveryCleaner(
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

        public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
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
                    "Operation root layout must match the cleaner's root layout.",
                    nameof(operation));
            }

            // Preflight both capabilities together, before the first deletion.
            if (!_fileSystem.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Recovery CaptureComplete cleanup requires no-follow open support on this platform.");
            }

            if (!_fileSystem.IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Recovery CaptureComplete cleanup requires directory metadata flush support on this platform.");
            }

            try
            {
                // The authoritative plan is serialized exactly once and used
                // for both the temporary and the staging plan comparison.
                byte[] canonical = CapturePublicationPlanCodec.SerializeCanonical(
                    operation.AuthoritativePlan);

                RemoveFinalCaptureIndexTemporary(operation, canonical);
                RemoveStagingChunksDirectory();
                DeleteStagingPublicationPlan(canonical);
                DeleteStagingReadyMarker(operation);
                DeleteStagingInitializationMarker(operation);
                RemoveStagingRunRoot();
            }
            catch (Exception ex) when (IsKnownCleanupFailure(ex))
            {
                return NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(this, operation);
            }

            return NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(this, operation);
        }

        /// <summary>
        /// Step 1: the final Capture Index temporary. A Run that committed its
        /// Capture Index consumed the temporary in that commit, so only its
        /// absence is acceptable there. A Run whose final index was already
        /// authoritative may still have one, and it is deleted only when what
        /// is on disk right now agrees with what the inspection safely
        /// classified.
        /// </summary>
        private void RemoveFinalCaptureIndexTemporary(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation,
            byte[] canonical)
        {
            CaptureIndexCommitDirectory finalRoot = RequireDirectory(_rootLayout.FinalRunRoot);
            CaptureIndexCommitFile temporary = null;
            try
            {
                CaptureIndexFileOpen opened = _fileSystem.TryOpen(
                    finalRoot, CaptureIndexTemporaryName);
                switch (opened.Status)
                {
                    case CaptureIndexFileOpenStatus.Absent:
                        // Already gone: either never written, consumed by the
                        // commit, or removed by an earlier attempt.
                        break;

                    case CaptureIndexFileOpenStatus.Opened:
                        temporary = opened.File;
                        break;

                    default:
                        throw ObservationFailure(CaptureIndexTemporaryName, opened.Status);
                }

                if (temporary != null)
                {
                    if (operation.HasCommitReceipt)
                    {
                        // The commit renamed this exact name to the final
                        // index, so anything standing here now is another
                        // writer's, whatever it contains.
                        throw new IOException(
                            CaptureIndexTemporaryName
                            + " still exists after a Capture Index recovery commit.");
                    }

                    RequireTemporaryStillSafeToDelete(operation, temporary.Stream, canonical);

                    _fileSystem.Delete(temporary);

                    temporary.Dispose();
                    temporary = null;
                }

                // The flush belongs to the step, not to the deletion: an entry
                // that is already gone may have been removed by an attempt
                // whose flush then failed, so this attempt must flush too
                // before calling the step processed.
                _fileSystem.FlushDirectory(finalRoot);
            }
            catch
            {
                ReleaseQuietly(temporary);
                ReleaseQuietly(finalRoot);
                throw;
            }

            finalRoot.Dispose();
        }

        /// <summary>
        /// The temporary is deleted only under the classification the
        /// inspection reached, re-confirmed against the current bytes: the
        /// authoritative document, or an unusable one. A read failure stays a
        /// read failure and never becomes "unusable content".
        /// </summary>
        private static void RequireTemporaryStillSafeToDelete(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation,
            Stream stream,
            byte[] canonical)
        {
            NvencRunCaptureIndexObservationStatus observed =
                operation.CaptureIndexRecoverySnapshot.TemporaryIndexStatus;

            switch (observed)
            {
                case NvencRunCaptureIndexObservationStatus.MatchesAuthoritative:
                    if (!HasSameBytes(ReadBounded(stream), canonical))
                    {
                        throw new InvalidDataException(
                            CaptureIndexTemporaryName
                            + " no longer holds the authoritative canonical bytes.");
                    }

                    return;

                case NvencRunCaptureIndexObservationStatus.Invalid:
                    if (IsCanonicalDocument(ReadBounded(stream)))
                    {
                        throw new InvalidDataException(
                            CaptureIndexTemporaryName + " is now a canonical document.");
                    }

                    return;

                default:
                    // The inspection saw no temporary, a foreign canonical
                    // document, or one past the limit: none of those authorizes
                    // a deletion now.
                    throw new InvalidDataException(
                        CaptureIndexTemporaryName
                        + " exists but was not classified as safe to remove.");
            }
        }

        /// <summary>
        /// Step 2: the staging chunk was moved to the final root by the
        /// publication, so the chunks directory must be empty. It is removed
        /// non-recursively and nothing inside it is ever deleted.
        /// </summary>
        private void RemoveStagingChunksDirectory()
        {
            CaptureIndexCommitDirectory staging = TryOpenStagingRunRoot();
            if (staging == null)
            {
                return;
            }

            CaptureIndexCommitDirectory chunks = null;
            try
            {
                CaptureIndexDirectoryOpen opened = _fileSystem.TryOpenDirectory(
                    Path.Combine(_rootLayout.StagingRunRoot, ChunksDirectoryName));
                switch (opened.Status)
                {
                    case CaptureIndexFileOpenStatus.Absent:
                        break;

                    case CaptureIndexFileOpenStatus.Opened:
                        chunks = opened.Directory;
                        break;

                    default:
                        throw ObservationFailure(ChunksDirectoryName, opened.Status);
                }

                if (chunks != null)
                {
                    if (!_fileSystem.IsDirectoryEmpty(chunks))
                    {
                        throw new IOException("The staging chunks directory is not empty.");
                    }

                    _fileSystem.DeleteDirectory(chunks);

                    chunks.Dispose();
                    chunks = null;
                }

                _fileSystem.FlushDirectory(staging);
            }
            catch
            {
                ReleaseQuietly(chunks);
                ReleaseQuietly(staging);
                throw;
            }

            staging.Dispose();
        }

        /// <summary>
        /// Step 3: the staging publication plan must still carry the exact
        /// canonical bytes of the authoritative plan before it is deleted.
        /// </summary>
        private void DeleteStagingPublicationPlan(byte[] canonical)
        {
            DeleteStagingFile(
                PublicationPlanName,
                (staging, plan) =>
                {
                    if (!HasSameBytes(ReadBounded(plan.Stream), canonical))
                    {
                        throw new InvalidDataException(
                            "The staging publication plan does not match the authoritative canonical plan bytes.");
                    }
                });
        }

        /// <summary>
        /// Step 4: the staging ready marker must correlate to the Run identity
        /// and to both init hashes it binds before it is deleted.
        /// </summary>
        private void DeleteStagingReadyMarker(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            DeleteStagingFile(
                RunReadyMarkerName,
                (staging, readyFile) =>
                {
                    CaptureRunReadyMarker ready = CaptureRunReadyMarkerCodec.DeserializeCanonical(
                        readyFile.Stream, CaptureRunReadyMarkerCodec.MaximumCanonicalByteCount);

                    if (ready.TestRunId != operation.TestRunId)
                    {
                        throw new InvalidDataException(
                            "The ready marker TestRunId does not match the operation.");
                    }

                    if (!string.Equals(
                            ready.RunInitializationId,
                            operation.RunInitializationId,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "The ready marker RunInitializationId does not match the operation.");
                    }

                    RequireReadyBindsBothInitializations(staging, ready, operation);
                });
        }

        /// <summary>
        /// Step 5: the staging initialization marker must correlate to the root
        /// layout and the Run identity, and the ready marker must already be
        /// gone, before it is deleted.
        /// </summary>
        private void DeleteStagingInitializationMarker(
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            DeleteStagingFile(
                RunInitializationMarkerName,
                (staging, initFile) =>
                {
                    RequireAbsent(staging, RunReadyMarkerName);

                    CaptureRunInitializationMarker init =
                        CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                            initFile.Stream,
                            CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);

                    RequireInitializationMarkerCorrelates(init, operation);
                });
        }

        /// <summary>
        /// Step 6: with every staging target gone the staging Run root must be
        /// empty, and is removed non-recursively. Its own parent - the
        /// directory whose entry the deletion changes - is opened first, so a
        /// flush capability failure is observed before the Run root is deleted.
        /// </summary>
        private void RemoveStagingRunRoot()
        {
            CaptureIndexCommitDirectory baseDirectory = RequireDirectory(StagingRunRootParent());
            CaptureIndexCommitDirectory staging = null;
            try
            {
                staging = TryOpenStagingRunRoot();
                if (staging != null)
                {
                    RequireAbsent(staging, PublicationPlanName);
                    RequireAbsent(staging, RunReadyMarkerName);
                    RequireAbsent(staging, RunInitializationMarkerName);
                    RequireDirectoryAbsent(
                        Path.Combine(_rootLayout.StagingRunRoot, ChunksDirectoryName),
                        ChunksDirectoryName);

                    if (!_fileSystem.IsDirectoryEmpty(staging))
                    {
                        // An unknown entry is left exactly where it is.
                        throw new IOException("The staging Run root is not empty.");
                    }

                    _fileSystem.DeleteDirectory(staging);

                    staging.Dispose();
                    staging = null;
                }

                _fileSystem.FlushDirectory(baseDirectory);
            }
            catch
            {
                ReleaseQuietly(staging);
                ReleaseQuietly(baseDirectory);
                throw;
            }

            baseDirectory.Dispose();
        }

        /// <summary>
        /// The shared shape of steps 3 to 5: open the staging Run root, accept
        /// a confirmed absent target as processed, verify the exact opened file,
        /// delete it through that handle, and flush the directory that held it.
        /// </summary>
        private void DeleteStagingFile(
            string name,
            Action<CaptureIndexCommitDirectory, CaptureIndexCommitFile> verify)
        {
            CaptureIndexCommitDirectory staging = TryOpenStagingRunRoot();
            if (staging == null)
            {
                return;
            }

            CaptureIndexCommitFile file = null;
            try
            {
                CaptureIndexFileOpen opened = _fileSystem.TryOpen(staging, name);
                switch (opened.Status)
                {
                    case CaptureIndexFileOpenStatus.Absent:
                        break;

                    case CaptureIndexFileOpenStatus.Opened:
                        file = opened.File;
                        break;

                    default:
                        throw ObservationFailure(name, opened.Status);
                }

                if (file != null)
                {
                    verify(staging, file);

                    _fileSystem.Delete(file);

                    file.Dispose();
                    file = null;
                }

                _fileSystem.FlushDirectory(staging);
            }
            catch
            {
                ReleaseQuietly(file);
                ReleaseQuietly(staging);
                throw;
            }

            staging.Dispose();
        }

        /// <summary>
        /// The ready marker binds both initialization markers, so both of its
        /// hashes are verified before it is deleted. The staging hash is
        /// checked against the actual staging marker - which must therefore
        /// still be there - and the final hash against the value derived in
        /// memory from the Run identity and this exact root layout, so the
        /// final Run root's markers are never opened or existence-checked.
        /// </summary>
        private void RequireReadyBindsBothInitializations(
            CaptureIndexCommitDirectory staging,
            CaptureRunReadyMarker ready,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            CaptureIndexCommitFile initFile = OpenRegularFile(staging, RunInitializationMarkerName);
            try
            {
                CaptureRunInitializationMarker init =
                    CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                        initFile.Stream,
                        CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount);

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
            catch
            {
                // A verification failure is the outcome, so the release must
                // not replace it.
                ReleaseQuietly(initFile);
                throw;
            }

            // The verification passed, so this handle's own release is part of
            // the result: a failure here stops the cleanup instead of leading
            // to a deletion.
            initFile.Dispose();

            CaptureRunMarkerBinding expected = new CaptureRunMarkerBinding(
                operation.TestRunId,
                operation.RunInitializationId,
                _rootLayout.StagingRunRootSha256,
                _rootLayout.FinalRunRootSha256);

            if (!string.Equals(
                    ready.FinalInitSha256,
                    expected.FinalReady.FinalInitSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The ready marker FinalInitSha256 does not match the expected final initialization marker.");
            }
        }

        private void RequireInitializationMarkerCorrelates(
            CaptureRunInitializationMarker init,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            if (init.TestRunId != operation.TestRunId)
            {
                throw new InvalidDataException(
                    "The initialization marker TestRunId does not match the operation.");
            }

            if (!string.Equals(
                    init.RunInitializationId,
                    operation.RunInitializationId,
                    StringComparison.Ordinal))
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
                    init.StagingRunRootSha256,
                    _rootLayout.StagingRunRootSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker StagingRunRootSha256 does not match the root layout.");
            }

            if (!string.Equals(
                    init.FinalRunRootSha256,
                    _rootLayout.FinalRunRootSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker FinalRunRootSha256 does not match the root layout.");
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
                throw new IOException(
                    "The staging Run root has no parent inside the staging trusted base root.");
            }

            return parent;
        }

        /// <summary>
        /// The staging Run root, or <c>null</c> when it is confirmed gone -
        /// which an earlier attempt may well have done. Anything else is a
        /// failure to observe.
        /// </summary>
        private CaptureIndexCommitDirectory TryOpenStagingRunRoot()
        {
            CaptureIndexDirectoryOpen opened =
                _fileSystem.TryOpenDirectory(_rootLayout.StagingRunRoot);

            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Opened:
                    return opened.Directory;

                case CaptureIndexFileOpenStatus.Absent:
                    return null;

                default:
                    throw ObservationFailure("the staging Run root", opened.Status);
            }
        }

        private CaptureIndexCommitDirectory RequireDirectory(string absolutePath)
        {
            CaptureIndexDirectoryOpen opened = _fileSystem.TryOpenDirectory(absolutePath);

            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Opened:
                    return opened.Directory;

                case CaptureIndexFileOpenStatus.Absent:
                    throw new IOException(absolutePath + " is absent.");

                default:
                    throw ObservationFailure(absolutePath, opened.Status);
            }
        }

        private void RequireDirectoryAbsent(string absolutePath, string name)
        {
            CaptureIndexDirectoryOpen opened = _fileSystem.TryOpenDirectory(absolutePath);

            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Absent:
                    return;

                case CaptureIndexFileOpenStatus.Opened:
                    ReleaseQuietly(opened.Directory);
                    throw new IOException(name + " still exists.");

                default:
                    throw ObservationFailure(name, opened.Status);
            }
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

                default:
                    throw ObservationFailure(name, opened.Status);
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
                    ReleaseQuietly(opened.File);
                    throw new IOException(name + " still exists.");

                default:
                    throw ObservationFailure(name, opened.Status);
            }
        }

        /// <summary>
        /// Every non-absent, non-opened observation. None of these is absence,
        /// and the platform signal is not an ordinary cleanup failure.
        /// </summary>
        private static Exception ObservationFailure(string name, CaptureIndexFileOpenStatus status)
        {
            switch (status)
            {
                case CaptureIndexFileOpenStatus.InvalidFileKind:
                    return new IOException(name + " is a reparse point or the wrong file kind.");

                case CaptureIndexFileOpenStatus.EscapesRoot:
                    return new IOException(name + " escapes its Run root.");

                case CaptureIndexFileOpenStatus.IoFailure:
                    return new IOException("Failed to observe " + name + ".");

                default:
                    return new CaptureArtifactNoFollowUnavailableException(
                        "No-follow observation is not supported on this platform.");
            }
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
                throw new InvalidDataException("The document exceeds the canonical byte limit.");
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

        /// <summary>
        /// Releases a handle while a failure is already propagating, so the
        /// original exception reaches the caller instead of a release failure.
        /// </summary>
        private static void ReleaseQuietly(IDisposable resource)
        {
            try
            {
                resource?.Dispose();
            }
            catch
            {
            }
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
