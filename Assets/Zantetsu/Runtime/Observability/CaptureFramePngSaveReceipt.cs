using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable proof of an atomically published PNG save: the fully-qualified
    /// destination path, the byte count, and the content SHA-256.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A receipt is produced only after the atomic rename has succeeded. It is
    /// a proof of path, byte count, and content hash, not a container for the
    /// file contents; it owns no file handle and no PNG data.
    /// </para>
    /// <para>
    /// The receipt never touches the filesystem after construction and never
    /// re-verifies or auto-updates if the destination is later changed
    /// externally. It does not require disposal.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngSaveReceipt
    {
        internal CaptureFramePngSaveReceipt(string destinationPath, int byteCount, string contentSha256)
        {
            if (destinationPath == null)
            {
                throw new ArgumentNullException(nameof(destinationPath));
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

            if (byteCount <= 8)
            {
                throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Byte count must be greater than 8.");
            }

            if (contentSha256 == null)
            {
                throw new ArgumentNullException(nameof(contentSha256));
            }

            if (!IsLowercaseHex(contentSha256))
            {
                throw new ArgumentException("Content SHA-256 must be 64 lowercase hexadecimal characters.", nameof(contentSha256));
            }

            DestinationPath = fullPath;
            ByteCount = byteCount;
            ContentSha256 = contentSha256;
        }

        public string DestinationPath { get; }

        public int ByteCount { get; }

        public string ContentSha256 { get; }

        internal static string ToLowerHex(byte[] bytes)
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

        private static bool IsLowercaseHex(string value)
        {
            if (value.Length != 64)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
