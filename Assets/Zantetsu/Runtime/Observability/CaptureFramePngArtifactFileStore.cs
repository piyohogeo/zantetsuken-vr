using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Atomically saves a <see cref="CaptureFramePngArtifact"/> as a canonical
    /// JSON sidecar next to its PNG, and loads a sidecar back into an artifact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving is synchronous I/O and a single instance must not be used
    /// concurrently. The sidecar must live in the same directory as the PNG; the
    /// JSON <c>pngFileName</c> is the source of truth for that relationship. The
    /// PNG file itself is never read, hashed, or verified.
    /// </para>
    /// <para>
    /// The receipt hash is the SHA-256 of the canonical sidecar bytes, distinct
    /// from both the PNG content hash and the run manifest content hash.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactFileStore
    {
        public CaptureFramePngArtifactSaveReceipt SaveAtomic(string destinationPath, CaptureFramePngArtifact artifact)
        {
            string fullPath = ValidateJsonPath(destinationPath, nameof(destinationPath));

            if (artifact == null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            string sidecarDirectory = Path.GetDirectoryName(fullPath);
            string pngDirectory = Path.GetDirectoryName(Path.GetFullPath(artifact.DestinationPath));
            if (!string.Equals(sidecarDirectory, pngDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Sidecar and PNG must be in the same directory.", nameof(destinationPath));
            }

            byte[] canonical = CaptureFramePngArtifactCodec.SerializeCanonical(artifact);

            if (canonical.Length < 1 || canonical.Length > CaptureFramePngArtifactCodec.MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical sidecar byte count is out of range.");
            }

            string contentSha256 = ComputeSha256(canonical);

            CaptureFramePngArtifactSaveReceipt receipt = new CaptureFramePngArtifactSaveReceipt(fullPath, canonical.Length, contentSha256);

            if (!Directory.Exists(sidecarDirectory))
            {
                throw new DirectoryNotFoundException("Destination directory does not exist: " + sidecarDirectory);
            }

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new IOException("Destination already exists: " + fullPath);
            }

            string fileName = Path.GetFileName(fullPath);
            string tempPath = Path.Combine(sidecarDirectory, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

            bool tempCreated = false;
            FileStream stream = null;
            try
            {
                stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                tempCreated = true;

                stream.Write(canonical, 0, canonical.Length);
                stream.Flush(flushToDisk: true);
                stream.Dispose();
                stream = null;

                File.Move(tempPath, fullPath);

                return receipt;
            }
            catch
            {
                if (stream != null)
                {
                    try
                    {
                        stream.Dispose();
                    }
                    catch
                    {
                        // Best-effort dispose; never replace the original exception.
                    }
                }

                if (tempCreated)
                {
                    TryDeleteTempFile(tempPath);
                }

                throw;
            }
        }

        public CaptureFramePngArtifact Load(string sourcePath, TraceRunManifest runManifest)
        {
            string fullPath = ValidateJsonPath(sourcePath, nameof(sourcePath));

            if (runManifest == null)
            {
                throw new ArgumentNullException(nameof(runManifest));
            }

            byte[] bytes;
            using (FileStream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long length = stream.Length;
                if (length < 1 || length > CaptureFramePngArtifactCodec.MaximumCanonicalByteCount)
                {
                    throw new InvalidDataException("Sidecar byte count is out of range.");
                }

                bytes = new byte[(int)length];

                int totalRead = 0;
                while (totalRead < bytes.Length)
                {
                    int read = stream.Read(bytes, totalRead, bytes.Length - totalRead);
                    if (read == 0)
                    {
                        throw new InvalidDataException("Sidecar file ended before the expected byte count.");
                    }

                    totalRead += read;
                }
            }

            string pngDirectory = Path.GetDirectoryName(fullPath);
            return CaptureFramePngArtifactCodec.DeserializeCanonical(bytes, runManifest, pngDirectory);
        }

        private static string ValidateJsonPath(string path, string paramName)
        {
            if (path == null)
            {
                throw new ArgumentNullException(paramName);
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Path must not be empty or whitespace.", paramName);
            }

            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException("Path must be fully qualified.", paramName);
            }

            string fullPath = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Path must end with '.json'.", paramName);
            }

            return fullPath;
        }

        private static string ComputeSha256(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            return CaptureFramePngArtifactSaveReceipt.ToLowerHex(hash);
        }

        private static void TryDeleteTempFile(string tempPath)
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup; never replace the original exception.
            }
        }
    }
}
