using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>Format-neutral file store rooted in one staging and one final run.</summary>
    internal sealed class CaptureArtifactFileStore : ICaptureArtifactStore
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly long _testRunId;
        private readonly string _stagingRunRoot;
        private readonly CaptureArtifactVerificationBufferPool _verificationBufferPool;
        private readonly ICaptureArtifactNoFollowOpener _noFollowOpener;

        internal const int VerificationBufferLength = 64 * 1024;

        internal CaptureArtifactFileStore(CaptureRunRootLayout rootLayout)
            : this(rootLayout, new CaptureArtifactVerificationBufferPool(VerificationBufferLength), CaptureArtifactNoFollowOpen.Create())
        {
        }

        internal CaptureArtifactFileStore(
            CaptureRunRootLayout rootLayout,
            CaptureArtifactVerificationBufferPool verificationBufferPool)
            : this(rootLayout, verificationBufferPool, CaptureArtifactNoFollowOpen.Create())
        {
        }

        internal CaptureArtifactFileStore(
            CaptureRunRootLayout rootLayout,
            CaptureArtifactVerificationBufferPool verificationBufferPool,
            ICaptureArtifactNoFollowOpener noFollowOpener)
        {
            if (rootLayout == null) throw new ArgumentNullException(nameof(rootLayout));
            if (!rootLayout.IsValid) throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));
            if (verificationBufferPool == null) throw new ArgumentNullException(nameof(verificationBufferPool));
            if (noFollowOpener == null) throw new ArgumentNullException(nameof(noFollowOpener));

            // Capability insufficiency is not a content mismatch. Refuse to
            // build the store before any Run root, Plan, or chunk exists when
            // the opener cannot open artifact paths without following reparse
            // points, so a normal artifact can never be misclassified as a
            // collision and no filesystem change is made. The opener is held
            // immutable, so a store can never be downgraded to unsupported
            // after construction.
            if (!noFollowOpener.IsSupported)
            {
                throw new CaptureArtifactNoFollowUnavailableException(
                    "No-follow artifact open is not supported on this platform.");
            }

            _rootLayout = rootLayout;
            _testRunId = rootLayout.TestRunId;
            _stagingRunRoot = rootLayout.RunRoot;
            _verificationBufferPool = verificationBufferPool;
            _noFollowOpener = noFollowOpener;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal CaptureArtifactVerificationBufferPool VerificationBufferPool => _verificationBufferPool;

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

        private CaptureArtifactVerificationResult VerifyAtReserved(
            CaptureArtifactDescriptor descriptor,
            string root,
            string relativePath,
            CaptureArtifactVerificationBufferPool.Lease lease)
        {
            CaptureArtifactNoFollowOpenResult opened = _noFollowOpener.TryOpen(root, relativePath);
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
                    // Unreachable after the constructor capability check; kept
                    // as a fail-closed fallback so an unsupported platform can
                    // never produce a content classification.
                    throw new CaptureArtifactNoFollowUnavailableException(
                        "No-follow artifact open is not supported on this platform.");
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
