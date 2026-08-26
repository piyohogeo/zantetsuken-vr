using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Verifies that the PNG file referenced by a
    /// <see cref="CaptureFramePngArtifact"/> matches the byte count and content
    /// SHA-256 recorded in its receipt.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Verification is synchronous I/O. A single instance is not thread-safe
    /// and must not be used concurrently. The managed read buffer is allocated
    /// once in the constructor and reused across verifications; a full
    /// PNG-sized array is never allocated.
    /// </para>
    /// <para>
    /// The PNG is read incrementally and its content is hashed in chunks. The
    /// byte count is compared before any hashing. The artifact, frame record,
    /// receipt, PNG, and any sidecar are never modified, and no stream is owned.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactVerifier
    {
        private readonly byte[] _readBuffer;

        public CaptureFramePngArtifactVerifier(int readBufferSize = 65536)
        {
            if (readBufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(readBufferSize), readBufferSize, "Read buffer size must be greater than zero.");
            }

            _readBuffer = new byte[readBufferSize];
        }

        public int ReadBufferSize => _readBuffer.Length;

        public void Verify(CaptureFramePngArtifact artifact)
        {
            if (artifact == null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            string destinationPath = artifact.DestinationPath;
            int expectedByteCount = artifact.PngByteCount;
            string expectedHash = artifact.PngContentSha256;

            string actualHash = null;
            using (FileStream stream = new FileStream(destinationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long actualLength = stream.Length;
                if (actualLength != expectedByteCount)
                {
                    throw new InvalidDataException("PNG byte count does not match the receipt.");
                }

                using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    long totalRead = 0;
                    while (totalRead < actualLength)
                    {
                        int toRead = (int)Math.Min((long)_readBuffer.Length, actualLength - totalRead);
                        int read = stream.Read(_readBuffer, 0, toRead);
                        if (read == 0)
                        {
                            throw new InvalidDataException("PNG file ended before the expected byte count.");
                        }

                        hasher.AppendData(_readBuffer, 0, read);
                        totalRead += read;
                    }

                    if (totalRead != actualLength)
                    {
                        throw new InvalidDataException("PNG read count does not match the file length.");
                    }

                    actualHash = ToLowerHex(hasher.GetHashAndReset());
                }
            }

            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("PNG content SHA-256 does not match the receipt.");
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0x0F];
            }

            return new string(chars);
        }
    }
}
