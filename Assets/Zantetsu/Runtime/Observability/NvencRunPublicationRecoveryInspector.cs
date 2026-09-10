using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous, read-only production Phase 0.11 NVENC
    /// publication recovery inspector. It observes the finished
    /// <c>publication.plan</c> and the NVENC precommit temporary under the
    /// staging Run root, verifies the one final chunk a canonical fixed plan
    /// declares, and returns exactly one snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every path is fixed: the plan and the temporary are basenames under the
    /// staging Run root and the chunk is the plan descriptor's own relative
    /// path under the final Run root, all opened through the existing no-follow
    /// opener. Nothing re-resolves a path with <c>File.Exists</c>,
    /// <c>File.Open</c>, or a directory listing, no Win32 or NT entry point is
    /// duplicated here, and the legacy <c>publication.plan.tmp</c> is never
    /// touched. The store's plan read-or-recover path is deliberately not used:
    /// it can promote that legacy temporary, which a read-only recovery
    /// boundary must never do.
    /// </para>
    /// <para>
    /// The temporary is observed by opening it and closing it again without a
    /// single read; only Opened means present and only Absent means absent, so
    /// a reparse point, a root escape, an I/O failure, or a missing capability
    /// is never guessed to be absence. A plan read failure likewise propagates
    /// rather than being recorded as an absent plan; only the file's own
    /// content decides between canonical, invalid, and over the limit.
    /// </para>
    /// <para>
    /// The verification buffer is reserved only when the observation actually
    /// turns on the chunk - a canonical plan of this Run's fixed graph with no
    /// competing temporary - because every other observation is already decided
    /// without it. The chunk is opened once, streamed once through the existing
    /// verifier with that one buffer, and never re-opened, re-hashed, read into
    /// a length-proportional array, or parsed as H.264. Streams, handles, and
    /// the buffer lease are released exactly once on every path.
    /// </para>
    /// <para>
    /// This type renames, deletes, promotes, and cleans up nothing, releases no
    /// lock, holds no state between calls, owns no thread, queue, or task, and
    /// is not an <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryInspector : INvencRunPublicationRecoveryInspector
    {
        private const int InitialPlanReadByteCount = 64 * 1024;

        private readonly ICaptureArtifactNoFollowOpener _opener;
        private readonly CaptureArtifactVerificationBufferPool _bufferPool;

        internal NvencRunPublicationRecoveryInspector(
            CaptureArtifactVerificationBufferPool bufferPool)
            : this(CaptureArtifactNoFollowOpen.Create(), bufferPool)
        {
        }

        internal NvencRunPublicationRecoveryInspector(
            ICaptureArtifactNoFollowOpener opener,
            CaptureArtifactVerificationBufferPool bufferPool)
        {
            _opener = opener ?? throw new ArgumentNullException(nameof(opener));
            _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        }

        public NvencRunPublicationRecoveryInspectionSnapshot Inspect(
            NvencRunPublicationRecoveryInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!_opener.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "Phase 0.11 NVENC publication recovery inspection requires no-follow file support on this platform.");
            }

            CaptureRunRootLayout rootLayout = operation.RootLayout;

            bool temporaryPresent = ObservePresence(
                rootLayout.StagingRunRoot, NvencRunPublicationPlanCommitOperation.PreCommitBasename);

            CaptureRunPublicationDocumentObservationStatus planStatus = ObservePlan(
                rootLayout.StagingRunRoot, out CapturePublicationPlan plan);

            // Every observation that is already decided without the chunk
            // returns here, so the chunk and the buffer pool are never touched
            // for it.
            if (planStatus != CaptureRunPublicationDocumentObservationStatus.Canonical
                || temporaryPresent
                || !NvencRunPublicationRecoveryPlanShape.IsFixedPhase011Plan(
                    plan, operation.TestRunId, operation.RunInitializationId))
            {
                return new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation, planStatus, plan, temporaryPresent, default);
            }

            CaptureArtifactVerificationResult verification = VerifyChunk(
                rootLayout.FinalRunRoot, plan.GetArtifact(0));

            return new NvencRunPublicationRecoveryInspectionSnapshot(
                operation, planStatus, plan, temporaryPresent, verification);
        }

        /// <summary>
        /// Opens the fixed name and closes it again without reading a byte.
        /// Only Opened is presence and only Absent is absence; every other
        /// status is a failure to observe and is never guessed either way.
        /// </summary>
        private bool ObservePresence(string root, string basename)
        {
            CaptureArtifactNoFollowOpenResult opened = _opener.TryOpen(root, basename);
            switch (opened.Status)
            {
                case CaptureArtifactNoFollowOpenStatus.Opened:
                    opened.Close();
                    return true;

                case CaptureArtifactNoFollowOpenStatus.Absent:
                    return false;

                case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                    throw new IOException(basename + " is a reparse point or directory.");

                case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                    throw new IOException(basename + " escapes the Run root.");

                case CaptureArtifactNoFollowOpenStatus.IoFailure:
                    throw new IOException("Failed to observe " + basename + ".");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow observation is not supported on this platform.");
            }
        }

        private CaptureRunPublicationDocumentObservationStatus ObservePlan(
            string stagingRunRoot,
            out CapturePublicationPlan plan)
        {
            plan = null;

            CaptureArtifactNoFollowOpenResult opened = _opener.TryOpen(
                stagingRunRoot, NvencRunPublicationPlanCommitOperation.FinalBasename);

            switch (opened.Status)
            {
                case CaptureArtifactNoFollowOpenStatus.Opened:
                    break;

                case CaptureArtifactNoFollowOpenStatus.Absent:
                    return CaptureRunPublicationDocumentObservationStatus.Absent;

                case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                    // A reparse point or directory where the plan should be is
                    // an unreadable finished document, never an absent one.
                    return CaptureRunPublicationDocumentObservationStatus.Invalid;

                case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                    return CaptureRunPublicationDocumentObservationStatus.Invalid;

                case CaptureArtifactNoFollowOpenStatus.IoFailure:
                    throw new IOException("Failed to open the finished publication plan.");

                default:
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow open is not supported on this platform.");
            }

            try
            {
                // One byte past the limit is read so the limit itself can be
                // observed; nothing is read after that. The buffer grows only
                // as far as the bytes actually observed, so an ordinary plan
                // never reserves the whole limit.
                const int Limit = CapturePublicationPlanCodec.MaximumCanonicalByteCount;
                byte[] buffer = new byte[InitialPlanReadByteCount];
                int count = 0;
                while (count <= Limit)
                {
                    if (count == buffer.Length)
                    {
                        Array.Resize(
                            ref buffer,
                            buffer.Length <= (Limit + 1) / 2 ? buffer.Length * 2 : Limit + 1);
                    }

                    int read = opened.Stream.Read(buffer, count, buffer.Length - count);
                    if (read == 0)
                    {
                        break;
                    }

                    count = checked(count + read);
                }

                if (count > Limit)
                {
                    return CaptureRunPublicationDocumentObservationStatus.LimitExceeded;
                }

                if (count == 0)
                {
                    return CaptureRunPublicationDocumentObservationStatus.Invalid;
                }

                byte[] canonical = new byte[count];
                Array.Copy(buffer, canonical, count);

                try
                {
                    // The codec's own canonical decision is the authority; no
                    // second serializer runs here.
                    plan = CapturePublicationPlanCodec.DeserializeCanonical(canonical);
                }
                catch (ArgumentException)
                {
                    return CaptureRunPublicationDocumentObservationStatus.Invalid;
                }
                catch (InvalidOperationException)
                {
                    return CaptureRunPublicationDocumentObservationStatus.Invalid;
                }

                if (plan == null || !plan.IsValid)
                {
                    plan = null;
                    return CaptureRunPublicationDocumentObservationStatus.Invalid;
                }

                return CaptureRunPublicationDocumentObservationStatus.Canonical;
            }
            finally
            {
                opened.Close();
            }
        }

        private CaptureArtifactVerificationResult VerifyChunk(
            string finalRunRoot,
            CaptureArtifactDescriptor descriptor)
        {
            CaptureArtifactVerificationBufferPool.Lease lease = _bufferPool.TryRent();
            if (lease == null)
            {
                // Nothing on the filesystem is touched when the one buffer is
                // already in use.
                return Deferred(descriptor);
            }

            try
            {
                CaptureArtifactNoFollowOpenResult opened = _opener.TryOpen(
                    finalRunRoot, descriptor.FinalRelativePath);

                switch (opened.Status)
                {
                    case CaptureArtifactNoFollowOpenStatus.Opened:
                        break;

                    case CaptureArtifactNoFollowOpenStatus.Absent:
                        return Completed(
                            descriptor,
                            CaptureArtifactVerificationStatus.Absent,
                            CaptureArtifactVerificationFailureReason.FileAbsent,
                            0);

                    case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                        return Invalid(
                            descriptor,
                            CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind);

                    case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                        return Invalid(
                            descriptor,
                            CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch);

                    case CaptureArtifactNoFollowOpenStatus.IoFailure:
                        return Invalid(
                            descriptor, CaptureArtifactVerificationFailureReason.ReadIoFailure);

                    default:
                        throw new CaptureArtifactNoFollowUnavailableException(
                            "No-follow open is not supported on this platform.");
                }

                try
                {
                    return CaptureArtifactStreamingVerifier.Verify(
                        descriptor, opened.Stream, lease.Buffer);
                }
                finally
                {
                    opened.Close();
                }
            }
            finally
            {
                _bufferPool.Return(lease);
            }
        }

        private static CaptureArtifactVerificationResult Deferred(CaptureArtifactDescriptor descriptor)
        {
            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Deferred,
                CaptureArtifactVerificationStatus.None,
                CaptureArtifactVerificationFailureReason.BufferUnavailable,
                0);
        }

        private static CaptureArtifactVerificationResult Invalid(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationFailureReason reason)
        {
            return Completed(descriptor, CaptureArtifactVerificationStatus.Invalid, reason, 0);
        }

        private static CaptureArtifactVerificationResult Completed(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationStatus status,
            CaptureArtifactVerificationFailureReason reason,
            long observedByteLength)
        {
            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Completed,
                status,
                reason,
                observedByteLength);
        }
    }
}
