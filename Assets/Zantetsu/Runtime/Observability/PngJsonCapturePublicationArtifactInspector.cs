using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Read-only, no-follow, bounded concrete inspector that observes the
    /// trace manifest and every plan entry's four artifacts — staging PNG,
    /// staging sidecar, final PNG, and final sidecar — and issues one
    /// <see cref="PngJsonCapturePublicationArtifactInspectionSnapshot"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inspector is bound to one immutable trace bundle directory, one
    /// immutable no-follow opener, and one immutable verification buffer pool.
    /// It holds no operation, snapshot, stream, handle, lease, or byte array
    /// after a call returns, and performs no create, write, delete, rename,
    /// flush, repair, or retry.
    /// </para>
    /// <para>
    /// <see cref="Inspect"/> rejects a null operation and an invalid operation
    /// before any filesystem contact, then confirms the opener capability and
    /// rents the buffer once for the whole inspection. The trace manifest is
    /// observed once, then every entry is observed in ascending index order
    /// with the four artifacts in the fixed staging PNG, staging sidecar,
    /// final PNG, final sidecar order. Every open result is closed on every
    /// path and the buffer lease is returned exactly once. On any exception no
    /// snapshot is returned.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactInspector : IPngJsonCapturePublicationArtifactInspector
    {
        private readonly string _traceBundleDirectory;
        private readonly ICaptureArtifactNoFollowOpener _opener;
        private readonly CaptureArtifactVerificationBufferPool _bufferPool;

        internal PngJsonCapturePublicationArtifactInspector(string traceBundleDirectory)
            : this(
                traceBundleDirectory,
                CaptureArtifactNoFollowOpen.Create(),
                new CaptureArtifactVerificationBufferPool(CaptureArtifactFileStore.VerificationBufferLength))
        {
        }

        internal PngJsonCapturePublicationArtifactInspector(
            string traceBundleDirectory,
            ICaptureArtifactNoFollowOpener opener,
            CaptureArtifactVerificationBufferPool bufferPool)
        {
            _traceBundleDirectory = NormalizeTraceBundleDirectory(traceBundleDirectory);
            _opener = opener ?? throw new ArgumentNullException(nameof(opener));
            _bufferPool = bufferPool ?? throw new ArgumentNullException(nameof(bufferPool));
        }

        public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(
            PngJsonCapturePublicationArtifactInspectionOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token))
            {
                throw new ArgumentException("Operation must be fully valid.", nameof(operation));
            }

            if (!_opener.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "No-follow open is not supported on this platform.");
            }

            CaptureArtifactVerificationBufferPool.Lease lease = _bufferPool.TryRent();
            if (lease == null)
            {
                throw new CaptureArtifactVerificationDeferredException(
                    "Verification buffer unavailable; no filesystem change.");
            }

            try
            {
                byte[] buffer = lease.Buffer;

                TraceManifestEvidence manifestEvidence = ObserveTraceManifest(operation, buffer);

                PngJsonCapturePublicationArtifactEntryObservation[] entries =
                    new PngJsonCapturePublicationArtifactEntryObservation[operation.EntryCount];
                for (int i = 0; i < operation.EntryCount; i++)
                {
                    entries[i] = ObserveEntry(operation, token, i, manifestEvidence.Manifest, buffer);
                }

                return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                    this,
                    operation,
                    manifestEvidence.Status,
                    manifestEvidence.ProbedByteCount,
                    entries);
            }
            finally
            {
                _bufferPool.Return(lease);
            }
        }

        private readonly struct TraceManifestEvidence
        {
            internal readonly CaptureRunPublicationEvidenceStatus Status;
            internal readonly long ProbedByteCount;
            internal readonly TraceRunManifest Manifest;

            internal TraceManifestEvidence(
                CaptureRunPublicationEvidenceStatus status,
                long probedByteCount,
                TraceRunManifest manifest)
            {
                Status = status;
                ProbedByteCount = probedByteCount;
                Manifest = manifest;
            }
        }

        private readonly struct ReadResult
        {
            internal readonly byte[] Bytes;
            internal readonly int Count;
            internal readonly bool Exceeded;

            internal ReadResult(byte[] bytes, int count, bool exceeded)
            {
                Bytes = bytes;
                Count = count;
                Exceeded = exceeded;
            }
        }

        private TraceManifestEvidence ObserveTraceManifest(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            byte[] buffer)
        {
            CaptureArtifactNoFollowOpenResult result = _opener.TryOpen(
                _traceBundleDirectory, TraceRunBundleFormat.ManifestFileName);
            if (result.Status != CaptureArtifactNoFollowOpenStatus.Opened)
            {
                return new TraceManifestEvidence(MapOpenStatus(result.Status), 0, null);
            }

            try
            {
                int limit = operation.MaximumTraceManifestByteCount;

                ReadResult readResult;
                try
                {
                    readResult = ReadBounded(result.Stream, limit, buffer);
                }
                catch (Exception)
                {
                    return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.Invalid, 0, null);
                }

                if (readResult.Exceeded)
                {
                    return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.LimitExceeded, checked(limit + 1), null);
                }

                if (readResult.Count == 0)
                {
                    return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.Invalid, 0, null);
                }

                TraceRunManifest manifest;
                try
                {
                    manifest = TraceRunManifestCodec.DeserializeCanonical(readResult.Bytes);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException)
                {
                    return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count, null);
                }

                if (manifest.TestRunId != operation.TestRunId)
                {
                    return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count, null);
                }

                if (!string.Equals(
                    TraceRunManifestCodec.ComputeContentSha256(manifest),
                    operation.RunManifestContentSha256,
                    StringComparison.Ordinal))
                {
                    return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count, null);
                }

                return new TraceManifestEvidence(CaptureRunPublicationEvidenceStatus.MatchesExpected, readResult.Count, manifest);
            }
            finally
            {
                result.Close();
            }
        }

        private PngJsonCapturePublicationArtifactEntryObservation ObserveEntry(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token,
            int index,
            TraceRunManifest manifest,
            byte[] buffer)
        {
            PngJsonCapturePublicationArtifactInspectionPathSet pathSet = operation.GetArtifactPaths(index);
            PngJsonCapturePublicationPlanEntry entry = pathSet.Entry;
            CaptureRunRootLayout layout = operation.RootLayout;
            CaptureRunPublicationPathSet publicationPaths = operation.PublicationPaths;

            (CaptureRunPublicationEvidenceStatus stagingPngStatus, long stagingPngCount) =
                ObservePng(operation, entry, entry.PngStagingRelativePath, layout.StagingRunRoot, buffer);

            (CaptureRunPublicationEvidenceStatus stagingSidecarStatus, long stagingSidecarCount) =
                ObserveSidecar(operation, entry, entry.SidecarStagingRelativePath, layout.StagingRunRoot, publicationPaths.StagingFramesRoot, manifest, buffer);

            (CaptureRunPublicationEvidenceStatus finalPngStatus, long finalPngCount) =
                ObservePng(operation, entry, entry.PngFinalRelativePath, layout.FinalRunRoot, buffer);

            (CaptureRunPublicationEvidenceStatus finalSidecarStatus, long finalSidecarCount) =
                ObserveSidecar(operation, entry, entry.SidecarFinalRelativePath, layout.FinalRunRoot, publicationPaths.FinalFramesRoot, manifest, buffer);

            return PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                token,
                operation,
                pathSet,
                stagingPngStatus,
                stagingPngCount,
                stagingSidecarStatus,
                stagingSidecarCount,
                finalPngStatus,
                finalPngCount,
                finalSidecarStatus,
                finalSidecarCount);
        }

        private (CaptureRunPublicationEvidenceStatus, long) ObservePng(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationPlanEntry entry,
            string relativePath,
            string root,
            byte[] buffer)
        {
            CaptureArtifactNoFollowOpenResult result = _opener.TryOpen(root, relativePath);
            if (result.Status != CaptureArtifactNoFollowOpenStatus.Opened)
            {
                return (MapOpenStatus(result.Status), 0);
            }

            try
            {
                long expectedLength = entry.PngByteLength;
                long limit = Min(expectedLength, operation.MaximumPngByteCount);

                long maximumProbe = checked(limit + 1);

                long observed = 0;
                string hash;
                using (SHA256 sha = SHA256.Create())
                {
                    while (observed < maximumProbe)
                    {
                        int request = (int)Math.Min(buffer.Length, maximumProbe - observed);
                        int read;
                        try
                        {
                            read = result.Stream.Read(buffer, 0, request);
                        }
                        catch (Exception)
                        {
                            return (CaptureRunPublicationEvidenceStatus.Invalid, observed);
                        }

                        if (read == 0)
                        {
                            break;
                        }

                        long next;
                        try
                        {
                            next = checked(observed + read);
                        }
                        catch (OverflowException)
                        {
                            return (CaptureRunPublicationEvidenceStatus.Invalid, observed);
                        }

                        observed = next;

                        if (observed > limit)
                        {
                            return (CaptureRunPublicationEvidenceStatus.LimitExceeded, observed);
                        }

                        sha.TransformBlock(buffer, 0, read, null, 0);
                    }

                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    hash = ToLowerHex(sha.Hash);
                }

                if (observed == 0)
                {
                    return (CaptureRunPublicationEvidenceStatus.Invalid, 0);
                }

                if (observed != expectedLength)
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, observed);
                }

                if (!string.Equals(hash, entry.PngContentSha256, StringComparison.Ordinal))
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, observed);
                }

                return (CaptureRunPublicationEvidenceStatus.MatchesExpected, observed);
            }
            finally
            {
                result.Close();
            }
        }

        private (CaptureRunPublicationEvidenceStatus, long) ObserveSidecar(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationPlanEntry entry,
            string relativePath,
            string root,
            string pngDirectory,
            TraceRunManifest manifest,
            byte[] buffer)
        {
            CaptureArtifactNoFollowOpenResult result = _opener.TryOpen(root, relativePath);
            if (result.Status != CaptureArtifactNoFollowOpenStatus.Opened)
            {
                return (MapOpenStatus(result.Status), 0);
            }

            try
            {
                int limit = (int)Min(entry.SidecarByteLength, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

                ReadResult readResult;
                try
                {
                    readResult = ReadBounded(result.Stream, limit, buffer);
                }
                catch (Exception)
                {
                    return (CaptureRunPublicationEvidenceStatus.Invalid, 0);
                }

                if (readResult.Exceeded)
                {
                    return (CaptureRunPublicationEvidenceStatus.LimitExceeded, checked(limit + 1));
                }

                if (readResult.Count == 0)
                {
                    return (CaptureRunPublicationEvidenceStatus.Invalid, 0);
                }

                if (readResult.Count != entry.SidecarByteLength)
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                string sidecarHash;
                using (SHA256 sha = SHA256.Create())
                {
                    sidecarHash = ToLowerHex(sha.ComputeHash(readResult.Bytes));
                }

                if (!string.Equals(sidecarHash, entry.SidecarContentSha256, StringComparison.Ordinal))
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                if (manifest == null)
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                CaptureFramePngArtifact artifact;
                try
                {
                    artifact = CaptureFramePngArtifactCodec.DeserializeCanonical(readResult.Bytes, manifest, pngDirectory);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is ArgumentException || ex is InvalidOperationException)
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                if (artifact.CaptureFrameId != entry.CaptureFrameId)
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                if (!string.Equals(artifact.PngContentSha256, entry.PngContentSha256, StringComparison.Ordinal))
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                if (artifact.PngByteCount != entry.PngByteLength)
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                if (!string.Equals(artifact.FrameRecord.RunManifestContentSha256, operation.RunManifestContentSha256, StringComparison.Ordinal))
                {
                    return (CaptureRunPublicationEvidenceStatus.Mismatch, readResult.Count);
                }

                return (CaptureRunPublicationEvidenceStatus.MatchesExpected, readResult.Count);
            }
            finally
            {
                result.Close();
            }
        }

        private static ReadResult ReadBounded(Stream stream, int limit, byte[] buffer)
        {
            byte[] bytes = new byte[limit];
            int total = 0;
            while (total < limit)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, limit - total));
                if (read == 0)
                {
                    break;
                }

                Array.Copy(buffer, 0, bytes, total, read);
                total += read;
            }

            if (total < limit)
            {
                byte[] exact = new byte[total];
                Array.Copy(bytes, 0, exact, 0, total);
                return new ReadResult(exact, total, false);
            }

            int extra = stream.Read(buffer, 0, 1);
            if (extra > 0)
            {
                return new ReadResult(null, limit + 1, true);
            }

            return new ReadResult(bytes, limit, false);
        }

        private static CaptureRunPublicationEvidenceStatus MapOpenStatus(CaptureArtifactNoFollowOpenStatus status)
        {
            switch (status)
            {
                case CaptureArtifactNoFollowOpenStatus.Absent:
                    return CaptureRunPublicationEvidenceStatus.Absent;

                default:
                    return CaptureRunPublicationEvidenceStatus.Invalid;
            }
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
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

        private static string NormalizeTraceBundleDirectory(string traceBundleDirectory)
        {
            if (traceBundleDirectory == null)
            {
                throw new ArgumentNullException(nameof(traceBundleDirectory));
            }

            if (string.IsNullOrWhiteSpace(traceBundleDirectory))
            {
                throw new ArgumentException("Trace bundle directory must not be empty or whitespace.", nameof(traceBundleDirectory));
            }

            if (!Path.IsPathFullyQualified(traceBundleDirectory))
            {
                throw new ArgumentException("Trace bundle directory must be fully qualified.", nameof(traceBundleDirectory));
            }

            string fullPath = Path.GetFullPath(traceBundleDirectory);
            string root = Path.GetPathRoot(fullPath);

            int end = fullPath.Length;
            while (end > 0 && (fullPath[end - 1] == Path.DirectorySeparatorChar || fullPath[end - 1] == Path.AltDirectorySeparatorChar))
            {
                end--;
            }

            if (end == fullPath.Length)
            {
                return fullPath;
            }

            string trimmed = fullPath.Substring(0, end);
            if (root != null && root.Length > 0
                && (root[root.Length - 1] == Path.DirectorySeparatorChar || root[root.Length - 1] == Path.AltDirectorySeparatorChar)
                && string.Equals(trimmed, root.Substring(0, root.Length - 1), StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return trimmed;
        }
    }
}
