using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>Format-neutral file store rooted in one staging and one final run.</summary>
    internal sealed class CaptureArtifactFileStore : ICaptureArtifactStore
    {
        private readonly string _stagingRunRoot;
        private readonly string _finalRunRoot;

        internal CaptureArtifactFileStore(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null) throw new ArgumentNullException(nameof(rootLayout));
            if (!rootLayout.IsValid) throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));
            _stagingRunRoot = rootLayout.StagingRunRoot;
            _finalRunRoot = rootLayout.FinalRunRoot;
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
