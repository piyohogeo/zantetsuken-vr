using System;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>Format-neutral immutable artifact identity and publication expectation.</summary>
    internal sealed class CaptureArtifactDescriptor
    {
        internal CaptureArtifactDescriptor(
            string artifactId,
            CaptureArtifactKind artifactKind,
            string formatId,
            int formatVersion,
            string stagingRelativePath,
            string finalRelativePath,
            long byteLength,
            string contentHash)
        {
            RequireText(artifactId, nameof(artifactId));
            RequireText(formatId, nameof(formatId));
            RequireRelativePath(stagingRelativePath, nameof(stagingRelativePath));
            RequireRelativePath(finalRelativePath, nameof(finalRelativePath));
            if (artifactKind == CaptureArtifactKind.None || !Enum.IsDefined(typeof(CaptureArtifactKind), artifactKind))
            {
                throw new ArgumentOutOfRangeException(nameof(artifactKind));
            }

            if (formatVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(formatVersion));
            }

            if (byteLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteLength));
            }

            if (!IsLowerHex(contentHash, 64))
            {
                throw new ArgumentException("Content hash must be 64 lowercase hex characters.", nameof(contentHash));
            }

            if (string.Equals(stagingRelativePath, finalRelativePath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Staging and final paths must differ.", nameof(finalRelativePath));
            }

            ArtifactId = artifactId;
            ArtifactKind = artifactKind;
            FormatId = formatId;
            FormatVersion = formatVersion;
            StagingRelativePath = stagingRelativePath;
            FinalRelativePath = finalRelativePath;
            ByteLength = byteLength;
            ContentHash = contentHash;
        }

        internal string ArtifactId { get; }
        internal CaptureArtifactKind ArtifactKind { get; }
        internal string FormatId { get; }
        internal int FormatVersion { get; }
        internal string StagingRelativePath { get; }
        internal string FinalRelativePath { get; }
        internal long ByteLength { get; }
        internal string ContentHash { get; }

        internal bool IsValid =>
            !string.IsNullOrEmpty(ArtifactId)
            && ArtifactKind != CaptureArtifactKind.None
            && Enum.IsDefined(typeof(CaptureArtifactKind), ArtifactKind)
            && !string.IsNullOrEmpty(FormatId)
            && FormatVersion > 0
            && IsRelativePath(StagingRelativePath)
            && IsRelativePath(FinalRelativePath)
            && !string.Equals(StagingRelativePath, FinalRelativePath, StringComparison.Ordinal)
            && ByteLength > 0
            && IsLowerHex(ContentHash, 64);

        private static void RequireText(string value, string name)
        {
            if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 512)
            {
                throw new ArgumentException("Value must be non-empty and at most 512 UTF-8 bytes.", name);
            }
        }

        private static void RequireRelativePath(string value, string name)
        {
            if (!IsRelativePath(value))
            {
                throw new ArgumentException("Path must be a normalized relative path.", name);
            }
        }

        private static bool IsRelativePath(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value[0] != '/'
                && value.IndexOf('\\') < 0
                && value.IndexOf(":", StringComparison.Ordinal) < 0
                && value.IndexOf("//", StringComparison.Ordinal) < 0
                && value.IndexOf("/../", StringComparison.Ordinal) < 0
                && !value.StartsWith("../", StringComparison.Ordinal)
                && !value.EndsWith("/..", StringComparison.Ordinal)
                && value.IndexOf("/./", StringComparison.Ordinal) < 0
                && !value.StartsWith("./", StringComparison.Ordinal)
                && Encoding.UTF8.GetByteCount(value) <= 512;
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length) return false;
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }
    }
}
