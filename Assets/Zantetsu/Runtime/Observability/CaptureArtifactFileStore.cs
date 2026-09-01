using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>Format-neutral file store rooted in one staging and one final run.</summary>
    internal sealed class CaptureArtifactFileStore : ICaptureArtifactStore, ICapturePublicationPlanStore
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly long _testRunId;
        private readonly string _stagingRunRoot;
        private readonly string _finalRunRoot;
        private readonly string _publicationPlanTemporaryPath;
        private readonly string _publicationPlanPath;

        internal CaptureArtifactFileStore(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null) throw new ArgumentNullException(nameof(rootLayout));
            if (!rootLayout.IsValid) throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));
            _rootLayout = rootLayout;
            _testRunId = rootLayout.TestRunId;
            _stagingRunRoot = rootLayout.StagingRunRoot;
            _finalRunRoot = rootLayout.FinalRunRoot;
            _publicationPlanTemporaryPath = Path.Combine(_stagingRunRoot, "publication.plan.tmp");
            _publicationPlanPath = Path.Combine(_stagingRunRoot, "publication.plan");
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

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
            string source = Resolve(_stagingRunRoot, descriptor.StagingRelativePath);
            string target = Resolve(_finalRunRoot, descriptor.FinalRelativePath);
            CaptureArtifactVerificationResult staging = VerifyAt(descriptor, source);
            if (staging.Status != CaptureArtifactVerificationStatus.MatchesExpected) throw new InvalidDataException("Staging artifact does not match descriptor.");
            if (File.Exists(target)) throw new IOException("Final artifact already exists.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Move(source, target);
            CaptureArtifactVerificationResult final = VerifyAt(descriptor, target);
            if (final.Status != CaptureArtifactVerificationStatus.MatchesExpected) throw new IOException("Published artifact verification failed.");
            return new CaptureArtifactPublishReceipt(this, descriptor, target);
        }

        public CaptureArtifactVerificationResult Verify(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            return VerifyAt(descriptor, Resolve(_finalRunRoot, descriptor.FinalRelativePath));
        }

        public CaptureArtifactVerificationResult VerifyStaging(CaptureArtifactDescriptor descriptor)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            return VerifyAt(descriptor, Resolve(_stagingRunRoot, descriptor.StagingRelativePath));
        }

        private static CaptureArtifactVerificationResult VerifyAt(CaptureArtifactDescriptor descriptor, string path)
        {
            if (!File.Exists(path)) return new CaptureArtifactVerificationResult(descriptor, CaptureArtifactVerificationStatus.Absent, 0);
            FileInfo info = new FileInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return new CaptureArtifactVerificationResult(descriptor, CaptureArtifactVerificationStatus.Invalid, info.Length);
            if (info.Length != descriptor.ByteLength) return new CaptureArtifactVerificationResult(descriptor, CaptureArtifactVerificationStatus.Mismatch, info.Length);
            byte[] bytes = File.ReadAllBytes(path);
            CaptureArtifactVerificationStatus status = string.Equals(Hash(bytes), descriptor.ContentHash, StringComparison.Ordinal)
                ? CaptureArtifactVerificationStatus.MatchesExpected
                : CaptureArtifactVerificationStatus.Mismatch;
            return new CaptureArtifactVerificationResult(descriptor, status, info.Length);
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
