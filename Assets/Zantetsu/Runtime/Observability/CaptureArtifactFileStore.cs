using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>Format-neutral file store rooted in one staging and one final run.</summary>
    internal sealed class CaptureArtifactFileStore : ICaptureArtifactStore, ICapturePublicationPlanStore, ICaptureArtifactReservationStore
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly long _testRunId;
        private readonly string _stagingRunRoot;
        private readonly string _finalRunRoot;
        private readonly string _publicationPlanTemporaryPath;
        private readonly string _publicationPlanPath;
        private readonly CaptureArtifactVerificationBufferPool _verificationBufferPool;

        internal const int VerificationBufferLength = 64 * 1024;

        internal CaptureArtifactFileStore(CaptureRunRootLayout rootLayout)
            : this(rootLayout, new CaptureArtifactVerificationBufferPool(VerificationBufferLength))
        {
        }

        internal CaptureArtifactFileStore(
            CaptureRunRootLayout rootLayout,
            CaptureArtifactVerificationBufferPool verificationBufferPool)
        {
            if (rootLayout == null) throw new ArgumentNullException(nameof(rootLayout));
            if (!rootLayout.IsValid) throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));
            if (verificationBufferPool == null) throw new ArgumentNullException(nameof(verificationBufferPool));
            _rootLayout = rootLayout;
            _testRunId = rootLayout.TestRunId;
            _stagingRunRoot = rootLayout.StagingRunRoot;
            _finalRunRoot = rootLayout.FinalRunRoot;
            _publicationPlanTemporaryPath = Path.Combine(_stagingRunRoot, "publication.plan.tmp");
            _publicationPlanPath = Path.Combine(_stagingRunRoot, "publication.plan");
            _verificationBufferPool = verificationBufferPool;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal CaptureArtifactVerificationBufferPool VerificationBufferPool => _verificationBufferPool;

        internal string PublicationPlanPath => _publicationPlanPath;

        public CapturePublicationPlanWriteReceipt WritePlan(CapturePublicationPlan plan)
        {
            if (plan == null || !plan.IsValid || plan.TestRunId != _testRunId)
                throw new ArgumentException("Plan must be valid for this run.", nameof(plan));
            byte[] bytes = CapturePublicationPlanCodec.SerializeCanonical(plan);
            Directory.CreateDirectory(_stagingRunRoot);
            using (FileStream stream = new FileStream(_publicationPlanTemporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
            try
            {
                File.Move(_publicationPlanTemporaryPath, _publicationPlanPath);
            }
            catch
            {
                if (File.Exists(_publicationPlanTemporaryPath)) File.Delete(_publicationPlanTemporaryPath);
                throw;
            }
            return new CapturePublicationPlanWriteReceipt(this, plan, _publicationPlanPath, bytes.Length);
        }

        public CapturePublicationPlan ReadPlan(int maximumCanonicalByteCount)
        {
            using (FileStream stream = new FileStream(_publicationPlanPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                CapturePublicationPlan plan = CapturePublicationPlanCodec.DeserializeCanonical(stream, maximumCanonicalByteCount);
                if (plan.TestRunId != _testRunId) throw new InvalidDataException("Publication plan belongs to another run.");
                return plan;
            }
        }

        public CapturePublicationPlan ReadOrRecoverPlan(int maximumCanonicalByteCount)
        {
            bool finalExists = File.Exists(_publicationPlanPath);
            bool temporaryExists = File.Exists(_publicationPlanTemporaryPath);
            if (finalExists && temporaryExists)
                throw new InvalidDataException("Both canonical and temporary publication plans exist.");
            if (finalExists) return ReadPlan(maximumCanonicalByteCount);
            if (!temporaryExists) throw new FileNotFoundException("No publication plan is available for recovery.", _publicationPlanPath);

            CapturePublicationPlan plan;
            using (FileStream stream = new FileStream(_publicationPlanTemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                plan = CapturePublicationPlanCodec.DeserializeCanonical(stream, maximumCanonicalByteCount);
            }
            if (plan.TestRunId != _testRunId) throw new InvalidDataException("Temporary publication plan belongs to another run.");

            // The canonical temporary document is the durable pre-rename
            // authority. Promote only after complete bounded validation.
            File.Move(_publicationPlanTemporaryPath, _publicationPlanPath);
            return plan;
        }

        public bool DiscardInvalidTemporaryPlan(int maximumCanonicalByteCount)
        {
            if (maximumCanonicalByteCount < 1
                || maximumCanonicalByteCount > CapturePublicationPlanCodec.MaximumCanonicalByteCount)
                throw new ArgumentOutOfRangeException(nameof(maximumCanonicalByteCount));
            if (File.Exists(_publicationPlanPath))
                throw new InvalidOperationException("A canonical publication plan exists; temporary cleanup requires collision inspection.");
            if (!File.Exists(_publicationPlanTemporaryPath)) return false;

            try
            {
                using (FileStream stream = new FileStream(_publicationPlanTemporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    CapturePublicationPlan plan = CapturePublicationPlanCodec.DeserializeCanonical(stream, maximumCanonicalByteCount);
                    if (plan.TestRunId != _testRunId)
                        throw new InvalidDataException("Temporary publication plan belongs to another run.");
                }
            }
            catch (ArgumentException)
            {
                File.Delete(_publicationPlanTemporaryPath);
                return true;
            }

            throw new InvalidOperationException("A canonical temporary plan must be promoted, not discarded.");
        }

        public CaptureArtifactWriteReceipt WriteStaging(CaptureArtifactWriteRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            CaptureArtifactDescriptor descriptor = request.Descriptor;
            string target = Resolve(_stagingRunRoot, descriptor.StagingRelativePath);
            string temp = target + ".tmp";
            byte[] payload = request.GetPayload();
            if (!string.Equals(Hash(payload), descriptor.ContentHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Payload hash does not match descriptor.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(payload, 0, payload.Length);
                stream.Flush(true);
            }

            try
            {
                File.Move(temp, target);
            }
            catch
            {
                if (File.Exists(temp)) File.Delete(temp);
                throw;
            }

            return new CaptureArtifactWriteReceipt(this, descriptor, target);
        }

        public CaptureArtifactPublishReceipt Publish(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));

            // Reserve the verification buffer before any filesystem change so
            // buffer exhaustion is never discovered only after the move.
            CaptureArtifactPublishReservation reservation = TryReservePublish();
            if (reservation == null)
                throw new CaptureArtifactVerificationDeferredException("Verification buffer unavailable; no filesystem change.");

            try
            {
                return PublishReserved(descriptor, reservation);
            }
            finally
            {
                ReleasePublishReservation(reservation);
            }
        }

        public CaptureArtifactPublishReservation TryReservePublish()
        {
            CaptureArtifactVerificationBufferPool.Lease lease = _verificationBufferPool.TryRent();
            return lease == null ? null : new CaptureArtifactPublishReservation(this, lease);
        }

        public void ReleasePublishReservation(CaptureArtifactPublishReservation reservation)
        {
            if (reservation == null) return;
            if (!ReferenceEquals(reservation.Store, this)) return;
            _verificationBufferPool.Return(reservation.Lease);
        }

        public CaptureArtifactPublishReceipt PublishReserved(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactPublishReservation reservation)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            if (reservation == null) throw new ArgumentNullException(nameof(reservation));

            // The reservation must be minted by this exact store and must still
            // be the current, outstanding lease of this store's pool. A foreign
            // store, foreign pool, returned, or stale reservation is rejected
            // before any filesystem change.
            if (!ReferenceEquals(reservation.Store, this))
                throw new ArgumentException("Publication reservation belongs to another store.", nameof(reservation));
            CaptureArtifactVerificationBufferPool.Lease lease = reservation.Lease;
            if (lease == null || !_verificationBufferPool.IsActive(lease))
                throw new InvalidOperationException("Publication reservation is not active.");

            string source = Resolve(_stagingRunRoot, descriptor.StagingRelativePath);
            string target = Resolve(_finalRunRoot, descriptor.FinalRelativePath);

            CaptureArtifactVerificationResult staging = VerifyAtReserved(descriptor, _stagingRunRoot, descriptor.StagingRelativePath, lease);
            if (staging.Status != CaptureArtifactVerificationStatus.MatchesExpected)
                throw new InvalidDataException("Staging artifact does not match descriptor.");

            if (File.Exists(target)) throw new IOException("Final artifact already exists.");

            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Move(source, target);

            CaptureArtifactVerificationResult final = VerifyAtReserved(descriptor, _finalRunRoot, descriptor.FinalRelativePath, lease);
            if (final.Status != CaptureArtifactVerificationStatus.MatchesExpected)
                throw new IOException("Published artifact verification failed.");

            return new CaptureArtifactPublishReceipt(this, descriptor, target);
        }

        public CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            return VerifyAt(descriptor, _finalRunRoot, descriptor.FinalRelativePath);
        }

        public CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            return VerifyAt(descriptor, _stagingRunRoot, descriptor.StagingRelativePath);
        }

        private CaptureArtifactVerificationResult VerifyAt(CaptureArtifactDescriptor descriptor, string root, string relativePath)
        {
            CaptureArtifactVerificationBufferPool.Lease lease = _verificationBufferPool.TryRent();
            if (lease == null)
            {
                return new CaptureArtifactVerificationResult(
                    descriptor,
                    CaptureArtifactVerificationExecutionDisposition.Deferred,
                    CaptureArtifactVerificationStatus.None,
                    CaptureArtifactVerificationFailureReason.BufferUnavailable,
                    0);
            }

            try
            {
                return VerifyAtReserved(descriptor, root, relativePath, lease);
            }
            finally
            {
                _verificationBufferPool.Return(lease);
            }
        }

        private static CaptureArtifactVerificationResult VerifyAtReserved(
            CaptureArtifactDescriptor descriptor,
            string root,
            string relativePath,
            CaptureArtifactVerificationBufferPool.Lease lease)
        {
            CaptureArtifactNoFollowOpenResult opened = CaptureArtifactNoFollowOpen.TryOpen(root, relativePath);
            switch (opened.Status)
            {
                case CaptureArtifactNoFollowOpenStatus.Opened:
                    try
                    {
                        return CaptureArtifactStreamingVerifier.Verify(descriptor, opened.Stream, lease.Buffer);
                    }
                    finally
                    {
                        opened.Close();
                    }
                case CaptureArtifactNoFollowOpenStatus.Absent:
                    return Absent(descriptor);
                case CaptureArtifactNoFollowOpenStatus.InvalidFileKind:
                    return new CaptureArtifactVerificationResult(
                        descriptor,
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.Invalid,
                        CaptureArtifactVerificationFailureReason.ReparsePointOrInvalidFileKind,
                        0);
                case CaptureArtifactNoFollowOpenStatus.EscapesRoot:
                    return new CaptureArtifactVerificationResult(
                        descriptor,
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.Invalid,
                        CaptureArtifactVerificationFailureReason.PathOrRunCorrelationMismatch,
                        0);
                case CaptureArtifactNoFollowOpenStatus.IoFailure:
                    return InvalidRead(descriptor, 0);
                case CaptureArtifactNoFollowOpenStatus.Unsupported:
                default:
                    return new CaptureArtifactVerificationResult(
                        descriptor,
                        CaptureArtifactVerificationExecutionDisposition.Completed,
                        CaptureArtifactVerificationStatus.Invalid,
                        CaptureArtifactVerificationFailureReason.NoFollowUnavailable,
                        0);
            }
        }

        private static CaptureArtifactVerificationResult Absent(CaptureArtifactDescriptor descriptor)
        {
            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Completed,
                CaptureArtifactVerificationStatus.Absent,
                CaptureArtifactVerificationFailureReason.FileAbsent,
                0);
        }

        private static CaptureArtifactVerificationResult InvalidRead(CaptureArtifactDescriptor descriptor, long observedByteLength)
        {
            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Completed,
                CaptureArtifactVerificationStatus.Invalid,
                CaptureArtifactVerificationFailureReason.ReadIoFailure,
                observedByteLength);
        }

        private static string Resolve(string root, string relative)
        {
            string combined = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            string prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!combined.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Artifact path escapes run root.");
            return combined;
        }

        private static string Hash(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(bytes);
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
