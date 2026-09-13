using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>Format-neutral file store rooted in one Run root.</summary>
    internal sealed class CaptureArtifactFileStore : ICaptureArtifactStore
    {
        private readonly CaptureRunRootLayout _rootLayout;
        private readonly long _testRunId;
        private readonly string _stagingRunRoot;

        internal CaptureArtifactFileStore(CaptureRunRootLayout rootLayout)
        {
            if (rootLayout == null) throw new ArgumentNullException(nameof(rootLayout));
            if (!rootLayout.IsValid) throw new ArgumentException("Root layout must be valid.", nameof(rootLayout));

            _rootLayout = rootLayout;
            _testRunId = rootLayout.TestRunId;
            _stagingRunRoot = rootLayout.RunRoot;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

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
