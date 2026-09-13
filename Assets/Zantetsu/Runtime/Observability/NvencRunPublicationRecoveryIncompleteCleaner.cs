using System;
using System.ComponentModel;
using System.IO;
using System.Security;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous production Phase 0.11 NVENC publication recovery
    /// orphan cleaner: it discards a Run whose publication never finished -
    /// both of its Run roots - in a fixed order, each target at most once,
    /// through handle-bound no-follow deletes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cleaner is bound to one <see cref="CaptureRunRootLayout"/> and one
    /// existing filesystem collaborator; no new interface, Win32 or NT backend,
    /// status, proof, token, nonce, generation, or retry latch is introduced.
    /// It holds no operation, result, receipt, stream, handle, path, or byte
    /// buffer in fields and is not an <see cref="IDisposable"/>.
    /// </para>
    /// <para>
    /// Only the fixed, uncommitted Phase 0.11 NVENC targets are ever deleted:
    /// the NVENC precommit temporary, the fixed pre-final <c>.partial</c> and
    /// finalized staging chunk, the emptied staging <c>chunks</c> directory,
    /// each root's <c>run.ready</c> and <c>run.init</c>, and the two emptied
    /// Run roots. A finished <c>publication.plan</c>, the legacy
    /// <c>publication.plan.tmp</c>, the final <c>capture.index</c> and its
    /// temporary, a published final chunk, and any unknown entry are never
    /// deleted or modified - finding one is a failure, never a licence to guess
    /// that it is an orphan.
    /// </para>
    /// <para>
    /// The NVENC temporary and the chunks are discarded whole, as uncommitted
    /// artifacts at fixed paths. Their content is never read, parsed,
    /// truncated, partially hashed, promoted, or recovered.
    /// </para>
    /// <para>
    /// Because a failed cleanup leaves the earlier deletions done, every step
    /// accepts a confirmed absent target as already processed, so a later
    /// attempt can resume. That tolerance is for absence alone: an unobservable
    /// target, a reparse point, a path escaping its root, a different file
    /// kind, and an access or I/O failure are never folded into absence, and a
    /// residual shape that cannot be true - a ready marker still present with
    /// no initialization marker to bind it, a non-empty <c>chunks</c>
    /// directory, an unknown entry in a Run root, or a marker that does not
    /// carry this Run's identity and both peer bindings - is a failure.
    /// </para>
    /// <para>
    /// While the directory that holds a step's entry is there, the step ends
    /// with a flush of it - the Run root for a root entry, the staging
    /// <c>chunks</c> directory for a chunk file, and
    /// <c>&lt;trusted base&gt;/runs</c> for a Run root itself - whether this
    /// attempt deleted the entry or found it already gone. An earlier attempt
    /// may have deleted an entry and then failed to flush, so such a step is
    /// only "processed" once its own flush has succeeded here. If that
    /// directory is itself already absent there is nothing to flush and
    /// nothing left in it to delete, and the step that removes that directory
    /// flushes its parent instead.
    /// </para>
    /// <para>
    /// One call is one attempt. There is no retry, rollback, re-creation,
    /// alternate-path cleanup, or recursive delete, and a failure stops the
    /// sequence rather than continuing to the next target. A known cleanup
    /// failure is published as Failed; a programming error, a broken contract,
    /// and a missing capability propagate instead. Failed is not a statement of
    /// progress: the next attempt begins from a new recovery inspection and a
    /// newly acquired lock, re-observing the real filesystem. Handles and
    /// streams are released on every path, and a release failure never replaces
    /// the failure that was already on its way out.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryIncompleteCleaner
        : INvencRunPublicationRecoveryIncompleteCleaner
    {
        private const string PublicationPlanName = "publication.plan";

        private const string PrecommitTemporaryName =
            NvencRunPublicationPlanCommitOperation.PreCommitBasename;

        private const string ChunksDirectoryName = "chunks";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly ICaptureCompleteCleanupFileSystem _fileSystem;

        internal NvencRunPublicationRecoveryIncompleteCleaner(CaptureRunRootLayout rootLayout)
            : this(rootLayout, CaptureIndexCommitFileSystem.Create())
        {
        }

        internal NvencRunPublicationRecoveryIncompleteCleaner(
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

        public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
            NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            // The operation is the authority on the decision graph, the
            // incomplete disposition, and the lease correlation; none of that
            // is revalidated here.
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
                    "Recovery orphan cleanup requires no-follow open support on this platform.");
            }

            if (!_fileSystem.IsDirectoryFlushSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Recovery orphan cleanup requires directory metadata flush support on this platform.");
            }

            try
            {
                CaptureRunMarkerBinding expected = new CaptureRunMarkerBinding(
                    operation.TestRunId,
                    operation.RunInitializationId,
                    _rootLayout.StagingRunRootSha256,
                    _rootLayout.FinalRunRootSha256);

                RequirePublicationPlanStillAbsent();
                DeleteStagingRootEntry(PrecommitTemporaryName);
                DeleteStagingChunk(PendingChunkName());
                DeleteStagingChunk(FinalizedChunkName());
                RemoveStagingChunksDirectory();
                RequireResidualMarkersBind(expected);
                RequireFinalArtifactsAbsent();
                DeleteFinalRootEntry(RunReadyMarkerName);
                DeleteFinalRootEntry(RunInitializationMarkerName);
                RemoveRunRoot(_rootLayout.FinalRunRoot, _rootLayout.FinalTrustedBaseRoot);
                DeleteStagingRootEntry(RunReadyMarkerName);
                DeleteStagingRootEntry(RunInitializationMarkerName);
                RemoveRunRoot(_rootLayout.StagingRunRoot, _rootLayout.StagingTrustedBaseRoot);
            }
            catch (Exception ex) when (IsKnownCleanupFailure(ex))
            {
                return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                    this, operation);
            }

            return NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                this, operation);
        }

        /// <summary>
        /// Step 1: this Run was classified incomplete because no finished plan
        /// existed. If one is there now it is not this cleanup's to interpret:
        /// nothing has been deleted yet, and nothing will be.
        /// </summary>
        private void RequirePublicationPlanStillAbsent()
        {
            CaptureIndexCommitDirectory staging = TryOpenDirectoryOrNull(_rootLayout.StagingRunRoot);
            if (staging == null)
            {
                return;
            }

            try
            {
                RequireAbsent(staging, PublicationPlanName);
            }
            catch
            {
                ReleaseQuietly(staging);
                throw;
            }

            staging.Dispose();
        }

        /// <summary>
        /// Steps 2, 9 and 10: one fixed entry directly under the staging Run
        /// root, deleted through the handle this attempt opened and followed by
        /// that root's own flush.
        /// </summary>
        private void DeleteStagingRootEntry(string name)
        {
            DeleteEntry(_rootLayout.StagingRunRoot, name);
        }

        /// <summary>Steps 6 and 7: the same shape under the final Run root.</summary>
        private void DeleteFinalRootEntry(string name)
        {
            DeleteEntry(_rootLayout.FinalRunRoot, name);
        }

        /// <summary>
        /// Step 3: one fixed chunk file. The parent whose entry changes is the
        /// staging <c>chunks</c> directory, so that is what the step flushes.
        /// The chunk is discarded whole and its content is never read.
        /// </summary>
        private void DeleteStagingChunk(string name)
        {
            DeleteEntry(StagingChunksPath(), name);
        }

        private void DeleteEntry(string directoryPath, string name)
        {
            CaptureIndexCommitDirectory directory = TryOpenDirectoryOrNull(directoryPath);
            if (directory == null)
            {
                // The directory that held this entry is gone, so the entry is
                // gone with it and there is nothing left to flush.
                return;
            }

            CaptureIndexCommitFile file = null;
            try
            {
                CaptureIndexFileOpen opened = _fileSystem.TryOpen(directory, name);
                switch (opened.Status)
                {
                    case CaptureIndexFileOpenStatus.Absent:
                        // Already gone: never written, or removed by an earlier
                        // attempt.
                        break;

                    case CaptureIndexFileOpenStatus.Opened:
                        file = opened.File;
                        break;

                    default:
                        throw ObservationFailure(name, opened.Status);
                }

                if (file != null)
                {
                    _fileSystem.Delete(file);

                    file.Dispose();
                    file = null;
                }

                // The flush belongs to the step, not to the deletion: an entry
                // that is already gone may have been removed by an attempt
                // whose flush then failed, so this attempt must flush too
                // before calling the step processed.
                _fileSystem.FlushDirectory(directory);
            }
            catch
            {
                ReleaseQuietly(file);
                ReleaseQuietly(directory);
                throw;
            }

            directory.Dispose();
        }

        /// <summary>
        /// Step 4: with both fixed chunk entries gone the staging
        /// <c>chunks</c> directory must be empty, and is removed
        /// non-recursively. Nothing inside it is ever deleted here.
        /// </summary>
        private void RemoveStagingChunksDirectory()
        {
            CaptureIndexCommitDirectory staging = TryOpenDirectoryOrNull(_rootLayout.StagingRunRoot);
            if (staging == null)
            {
                return;
            }

            CaptureIndexCommitDirectory chunks = null;
            try
            {
                chunks = TryOpenDirectoryOrNull(StagingChunksPath());

                if (chunks != null)
                {
                    if (!_fileSystem.IsDirectoryEmpty(chunks))
                    {
                        // An unknown entry is left exactly where it is.
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
        /// Step 5: whatever markers are still in either root must be this Run's
        /// own, by the existing codec and the existing binding rules. A ready
        /// marker without the initialization marker it binds cannot be a state
        /// this cleanup produced, so it is a failure rather than a target.
        /// </summary>
        private void RequireResidualMarkersBind(CaptureRunMarkerBinding expected)
        {
            RequireRootMarkersBind(
                _rootLayout.FinalRunRoot, CaptureRunRootRole.Final, expected.FinalInitialization,
                expected.FinalReady);
            RequireRootMarkersBind(
                _rootLayout.StagingRunRoot, CaptureRunRootRole.Staging,
                expected.StagingInitialization, expected.StagingReady);
        }

        private void RequireRootMarkersBind(
            string rootPath,
            CaptureRunRootRole role,
            CaptureRunInitializationMarker expectedInit,
            CaptureRunReadyMarker expectedReady)
        {
            CaptureIndexCommitDirectory root = TryOpenDirectoryOrNull(rootPath);
            if (root == null)
            {
                return;
            }

            try
            {
                bool readyPresent = ReadMarker(
                    root,
                    RunReadyMarkerName,
                    stream => RequireReadyMarkerBinds(
                        CaptureRunReadyMarkerCodec.DeserializeCanonical(
                            stream, CaptureRunReadyMarkerCodec.MaximumCanonicalByteCount),
                        expectedReady));

                bool initPresent = ReadMarker(
                    root,
                    RunInitializationMarkerName,
                    stream => RequireInitializationMarkerBinds(
                        CaptureRunInitializationMarkerCodec.DeserializeCanonical(
                            stream,
                            CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount),
                        expectedInit,
                        role));

                if (readyPresent && !initPresent)
                {
                    throw new InvalidDataException(
                        "A ready marker remains with no initialization marker to bind it.");
                }
            }
            catch
            {
                ReleaseQuietly(root);
                throw;
            }

            root.Dispose();
        }

        /// <summary>
        /// Opens one marker for reading, verifies it, and releases it. A
        /// confirmed absent marker is an earlier attempt's work, not a failure.
        /// </summary>
        private bool ReadMarker(
            CaptureIndexCommitDirectory root, string name, Action<Stream> verify)
        {
            CaptureIndexFileOpen opened = _fileSystem.TryOpen(root, name);

            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Absent:
                    return false;

                case CaptureIndexFileOpenStatus.Opened:
                    break;

                default:
                    throw ObservationFailure(name, opened.Status);
            }

            CaptureIndexCommitFile file = opened.File;
            try
            {
                verify(file.Stream);
            }
            catch
            {
                // A verification failure is the outcome, so the release must
                // not replace it.
                ReleaseQuietly(file);
                throw;
            }

            // The verification passed, so this handle's own release is part of
            // the result: a failure here stops the cleanup instead of leading
            // to a deletion.
            file.Dispose();
            return true;
        }

        private static void RequireReadyMarkerBinds(
            CaptureRunReadyMarker observed, CaptureRunReadyMarker expected)
        {
            if (observed.TestRunId != expected.TestRunId)
            {
                throw new InvalidDataException(
                    "The ready marker TestRunId does not match this Run.");
            }

            if (!string.Equals(
                    observed.RunInitializationId,
                    expected.RunInitializationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The ready marker RunInitializationId does not match this Run.");
            }

            if (!string.Equals(
                    observed.StagingInitSha256, expected.StagingInitSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The ready marker StagingInitSha256 does not match this Run's binding.");
            }

            if (!string.Equals(
                    observed.FinalInitSha256, expected.FinalInitSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The ready marker FinalInitSha256 does not match this Run's binding.");
            }
        }

        private static void RequireInitializationMarkerBinds(
            CaptureRunInitializationMarker observed,
            CaptureRunInitializationMarker expected,
            CaptureRunRootRole role)
        {
            if (observed.TestRunId != expected.TestRunId)
            {
                throw new InvalidDataException(
                    "The initialization marker TestRunId does not match this Run.");
            }

            if (!string.Equals(
                    observed.RunInitializationId,
                    expected.RunInitializationId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker RunInitializationId does not match this Run.");
            }

            if (observed.RootRole != role)
            {
                throw new InvalidDataException(
                    "The initialization marker does not carry this root's role.");
            }

            if (!string.Equals(
                    observed.StagingRunRootSha256,
                    expected.StagingRunRootSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker StagingRunRootSha256 does not match the root layout.");
            }

            if (!string.Equals(
                    observed.FinalRunRootSha256,
                    expected.FinalRunRootSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The initialization marker FinalRunRootSha256 does not match the root layout.");
            }
        }

        /// <summary>
        /// Before any final marker is deleted: a Run that never finished its
        /// publication has no committed final artifact. If one is there it
        /// belongs to something this cleanup does not own, so the final root is
        /// left exactly as it is.
        /// </summary>
        private void RequireFinalArtifactsAbsent()
        {
            CaptureIndexCommitDirectory finalRoot = TryOpenDirectoryOrNull(_rootLayout.FinalRunRoot);
            if (finalRoot == null)
            {
                return;
            }

            try
            {
                RequireAbsent(finalRoot, CaptureIndexName);
                RequireAbsent(finalRoot, CaptureIndexTemporaryName);
                RequireDirectoryAbsent(
                    Path.Combine(_rootLayout.FinalRunRoot, ChunksDirectoryName),
                    ChunksDirectoryName);
            }
            catch
            {
                ReleaseQuietly(finalRoot);
                throw;
            }

            finalRoot.Dispose();
        }

        /// <summary>
        /// Steps 8 and 11: with every target of that root gone it must be
        /// empty, and is removed non-recursively. Its own parent - the
        /// directory whose entry the deletion changes - is opened first, so a
        /// flush capability failure is observed before the Run root is deleted,
        /// and that parent is flushed even when the root was already gone.
        /// </summary>
        private void RemoveRunRoot(string runRootPath, string trustedBaseRoot)
        {
            CaptureIndexCommitDirectory baseDirectory =
                RequireDirectory(RunRootParent(runRootPath, trustedBaseRoot));
            CaptureIndexCommitDirectory root = null;
            try
            {
                root = TryOpenDirectoryOrNull(runRootPath);
                if (root != null)
                {
                    if (!_fileSystem.IsDirectoryEmpty(root))
                    {
                        // An unknown entry is left exactly where it is.
                        throw new IOException(runRootPath + " is not empty.");
                    }

                    _fileSystem.DeleteDirectory(root);

                    root.Dispose();
                    root = null;
                }

                _fileSystem.FlushDirectory(baseDirectory);
            }
            catch
            {
                ReleaseQuietly(root);
                ReleaseQuietly(baseDirectory);
                throw;
            }

            baseDirectory.Dispose();
        }

        private string StagingChunksPath()
        {
            return Path.Combine(_rootLayout.StagingRunRoot, ChunksDirectoryName);
        }

        private static string PendingChunkName()
        {
            return NameOf(NvencRunChunkArtifactDescriptorFactory.PendingRelativePath);
        }

        private static string FinalizedChunkName()
        {
            return NameOf(NvencRunChunkArtifactDescriptorFactory.StagingRelativePath);
        }

        private static string NameOf(string relativePath)
        {
            int separator = relativePath.LastIndexOf('/');
            return separator < 0 ? relativePath : relativePath.Substring(separator + 1);
        }

        /// <summary>
        /// The directory that holds one Run root's own entry. A Run root sits
        /// at a fixed relative path under its trusted base root, so this is
        /// always a directory strictly inside that base root.
        /// </summary>
        private static string RunRootParent(string runRootPath, string trustedBaseRoot)
        {
            string parent = Path.GetDirectoryName(runRootPath);
            if (string.IsNullOrEmpty(parent)
                || parent.Length <= trustedBaseRoot.Length
                || !parent.StartsWith(trustedBaseRoot, StringComparison.Ordinal))
            {
                throw new IOException(
                    runRootPath + " has no parent inside its trusted base root.");
            }

            return parent;
        }

        /// <summary>
        /// One directory, or <c>null</c> when it is confirmed gone - which an
        /// earlier attempt may well have done. Anything else is a failure to
        /// observe.
        /// </summary>
        private CaptureIndexCommitDirectory TryOpenDirectoryOrNull(string absolutePath)
        {
            CaptureIndexDirectoryOpen opened = _fileSystem.TryOpenDirectory(absolutePath);

            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Opened:
                    return opened.Directory;

                case CaptureIndexFileOpenStatus.Absent:
                    return null;

                default:
                    throw ObservationFailure(absolutePath, opened.Status);
            }
        }

        private CaptureIndexCommitDirectory RequireDirectory(string absolutePath)
        {
            CaptureIndexCommitDirectory directory = TryOpenDirectoryOrNull(absolutePath);
            if (directory == null)
            {
                throw new IOException(absolutePath + " is absent.");
            }

            return directory;
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

        private void RequireAbsent(CaptureIndexCommitDirectory directory, string name)
        {
            CaptureIndexFileOpen opened = _fileSystem.TryOpen(directory, name);

            switch (opened.Status)
            {
                case CaptureIndexFileOpenStatus.Absent:
                    return;

                case CaptureIndexFileOpenStatus.Opened:
                    // Left exactly where it is, and never read.
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
        /// result: content mismatches, residual shapes, non-empty directories,
        /// access denials, and plain I/O errors. Unexpected programming and
        /// invariant failures, and the capability signal, are deliberately
        /// absent so they propagate.
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
