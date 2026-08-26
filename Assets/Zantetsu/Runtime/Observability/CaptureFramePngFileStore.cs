using System;
using System.IO;
using System.Security.Cryptography;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Atomically saves a caller-owned PNG <see cref="NativeArray{T}"/> to a
    /// file. The PNG is written to a unique temp file in the destination
    /// directory and renamed into place, never overwriting an existing
    /// destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Saving is synchronous I/O and must not be called directly from a
    /// frame-update hot path. A single instance is not safe for concurrent use.
    /// </para>
    /// <para>
    /// The input PNG is not owned: the caller must dispose it after both
    /// successful and failed saves. The managed chunk buffer is reused across
    /// calls and never becomes a full PNG-sized managed copy.
    /// </para>
    /// <para>
    /// The temp file is created in the same directory as the destination and
    /// renamed into place, so publication is atomic and never overwrites an
    /// existing destination. Capture Record metadata and trace bundles are
    /// outside this type's responsibility.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngFileStore
    {
        private readonly byte[] _copyBuffer;

        public CaptureFramePngFileStore(int copyBufferSize = 65536)
        {
            if (copyBufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(copyBufferSize), copyBufferSize, "Copy buffer size must be greater than zero.");
            }

            _copyBuffer = new byte[copyBufferSize];
        }

        public int CopyBufferSize => _copyBuffer.Length;

        /// <summary>
        /// Atomically saves the PNG and returns an immutable receipt proving the
        /// published destination path, byte count, and content SHA-256.
        /// Synchronous I/O; the input is not owned. The content hash is computed
        /// incrementally from the same chunks that are written to the file, and
        /// the receipt is returned only after the rename has succeeded. Linking
        /// the receipt to a Capture Record is the caller's responsibility.
        /// </summary>
        public CaptureFramePngSaveReceipt SaveAtomicWithReceipt(string destinationPath, NativeArray<byte> pngBytes)
        {
            return SaveAtomicCore(destinationPath, pngBytes);
        }

        public void SaveAtomic(string destinationPath, NativeArray<byte> pngBytes)
        {
            SaveAtomicCore(destinationPath, pngBytes);
        }

        private CaptureFramePngSaveReceipt SaveAtomicCore(string destinationPath, NativeArray<byte> pngBytes)
        {
            string fullPath = Validate(destinationPath, pngBytes);

            string directory = Path.GetDirectoryName(fullPath);
            string fileName = Path.GetFileName(fullPath);
            string tempPath = Path.Combine(directory, fileName + "." + Guid.NewGuid().ToString("N") + ".tmp");

            bool tempCreated = false;
            FileStream stream = null;
            IncrementalHash hasher = null;
            try
            {
                stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                tempCreated = true;

                hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                int offset = 0;
                while (offset < pngBytes.Length)
                {
                    int chunkLength = pngBytes.Length - offset;
                    if (chunkLength > _copyBuffer.Length)
                    {
                        chunkLength = _copyBuffer.Length;
                    }

                    NativeArray<byte>.Copy(pngBytes, offset, _copyBuffer, 0, chunkLength);
                    stream.Write(_copyBuffer, 0, chunkLength);
                    hasher.AppendData(_copyBuffer, 0, chunkLength);
                    offset += chunkLength;
                }

                byte[] hashBytes = hasher.GetHashAndReset();
                string contentSha256 = CaptureFramePngSaveReceipt.ToLowerHex(hashBytes);

                stream.Flush(flushToDisk: true);
                stream.Dispose();
                stream = null;

                hasher.Dispose();
                hasher = null;

                CaptureFramePngSaveReceipt receipt =
                    new CaptureFramePngSaveReceipt(fullPath, pngBytes.Length, contentSha256);

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

                if (hasher != null)
                {
                    try
                    {
                        hasher.Dispose();
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

        private static string Validate(string destinationPath, NativeArray<byte> pngBytes)
        {
            if (destinationPath == null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
            }

            if (string.IsNullOrWhiteSpace(destinationPath))
            {
                throw new ArgumentException("Destination path must not be empty or whitespace.", nameof(destinationPath));
            }

            if (!Path.IsPathFullyQualified(destinationPath))
            {
                throw new ArgumentException("Destination path must be fully qualified.", nameof(destinationPath));
            }

            string fullPath = Path.GetFullPath(destinationPath);

            if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Destination path must end with '.png'.", nameof(destinationPath));
            }

            if (!pngBytes.IsCreated)
            {
                throw new ArgumentException("PNG buffer is not created.", nameof(pngBytes));
            }

            if (pngBytes.Length <= 8)
            {
                throw new ArgumentException("PNG buffer is too short.", nameof(pngBytes));
            }

            if (pngBytes[0] != 0x89 || pngBytes[1] != 0x50 || pngBytes[2] != 0x4E || pngBytes[3] != 0x47
                || pngBytes[4] != 0x0D || pngBytes[5] != 0x0A || pngBytes[6] != 0x1A || pngBytes[7] != 0x0A)
            {
                throw new ArgumentException("PNG buffer has an invalid signature.", nameof(pngBytes));
            }

            string directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("Destination directory does not exist: " + directory);
            }

            if (File.Exists(fullPath) || Directory.Exists(fullPath))
            {
                throw new IOException("Destination already exists: " + fullPath);
            }

            return fullPath;
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
