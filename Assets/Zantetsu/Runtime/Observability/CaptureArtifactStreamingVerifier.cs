using System;
using System.IO;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single-pass, fixed-memory artifact verification. Streams the artifact
    /// through one caller-owned open handle into one fixed-size buffer,
    /// updating a checked accumulated length and an incremental SHA-256 per
    /// read, and returns a terminal <see cref="CaptureArtifactVerificationResult"/>
    /// without ever allocating an artifact-length array or re-opening the file.
    /// </summary>
    internal static class CaptureArtifactStreamingVerifier
    {
        private static readonly byte[] Empty = new byte[0];

        internal static CaptureArtifactVerificationResult Verify(
            CaptureArtifactDescriptor descriptor,
            Stream stream,
            byte[] buffer)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (!stream.CanRead) throw new ArgumentException("Stream must be readable.", nameof(stream));
            if (buffer == null || buffer.Length < 1) throw new ArgumentException("Buffer must be non-empty.", nameof(buffer));

            long startLength;
            try
            {
                startLength = stream.Length;
            }
            catch (Exception)
            {
                return Invalid(descriptor, CaptureArtifactVerificationFailureReason.ReadIoFailure, 0);
            }

            long observed = 0;
            string actualHash;
            using (SHA256 sha = SHA256.Create())
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = stream.Read(buffer, 0, buffer.Length);
                    }
                    catch (Exception)
                    {
                        return Invalid(descriptor, CaptureArtifactVerificationFailureReason.ReadIoFailure, observed);
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    try
                    {
                        observed = checked(observed + read);
                    }
                    catch (OverflowException)
                    {
                        return Invalid(descriptor, CaptureArtifactVerificationFailureReason.CheckedLengthOverflow, observed);
                    }

                    sha.TransformBlock(buffer, 0, read, null, 0);
                }

                sha.TransformFinalBlock(Empty, 0, 0);
                actualHash = ToHex(sha.Hash);
            }

            long endLength;
            try
            {
                endLength = stream.Length;
            }
            catch (Exception)
            {
                return Invalid(descriptor, CaptureArtifactVerificationFailureReason.ReadIoFailure, observed);
            }

            if (startLength != endLength)
            {
                return Invalid(descriptor, CaptureArtifactVerificationFailureReason.FileChangedDuringRead, observed);
            }

            if (observed < descriptor.ByteLength)
            {
                return new CaptureArtifactVerificationResult(
                    descriptor,
                    CaptureArtifactVerificationExecutionDisposition.Completed,
                    CaptureArtifactVerificationStatus.Mismatch,
                    CaptureArtifactVerificationFailureReason.ShorterThanDeclared,
                    observed);
            }

            if (observed > descriptor.ByteLength)
            {
                return new CaptureArtifactVerificationResult(
                    descriptor,
                    CaptureArtifactVerificationExecutionDisposition.Completed,
                    CaptureArtifactVerificationStatus.Mismatch,
                    CaptureArtifactVerificationFailureReason.LongerThanDeclared,
                    observed);
            }

            if (!string.Equals(actualHash, descriptor.ContentHash, StringComparison.Ordinal))
            {
                return new CaptureArtifactVerificationResult(
                    descriptor,
                    CaptureArtifactVerificationExecutionDisposition.Completed,
                    CaptureArtifactVerificationStatus.Mismatch,
                    CaptureArtifactVerificationFailureReason.HashMismatch,
                    observed);
            }

            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Completed,
                CaptureArtifactVerificationStatus.MatchesExpected,
                CaptureArtifactVerificationFailureReason.None,
                observed);
        }

        private static CaptureArtifactVerificationResult Invalid(
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactVerificationFailureReason reason,
            long observed)
        {
            return new CaptureArtifactVerificationResult(
                descriptor,
                CaptureArtifactVerificationExecutionDisposition.Completed,
                CaptureArtifactVerificationStatus.Invalid,
                reason,
                observed);
        }

        private static string ToHex(byte[] hash)
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
    }
}
