using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON serializer for
    /// <see cref="CaptureRunInitializationMarker"/>. The canonical form is
    /// byte-for-byte deterministic: no BOM, no trailing newline, no extra
    /// whitespace, fixed PascalCase property order, invariant shortest decimal
    /// integers, and literal ASCII strings without escape representations. The
    /// root role is serialized as the exact string <c>"Staging"</c> or
    /// <c>"Final"</c>, never as a number.
    /// </summary>
    /// <remarks>
    /// This codec performs no file I/O, no role derivation, no root hash
    /// computation, no clock, no random, and no Unity static API access.
    /// </remarks>
    internal static class CaptureRunInitializationMarkerCodec
    {
        internal const int MaximumCanonicalByteCount = 4 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CaptureRunInitializationMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            StringBuilder sb = new StringBuilder(256);
            sb.Append("{\"SchemaVersion\":");
            AppendLong(sb, marker.SchemaVersion);
            sb.Append(",\"TestRunId\":");
            AppendLong(sb, marker.TestRunId);
            sb.Append(",\"RunInitializationId\":");
            AppendLiteral(sb, marker.RunInitializationId);
            sb.Append(",\"RootRole\":");
            AppendLiteral(sb, RootRoleLiteral(marker.RootRole));
            sb.Append(",\"StagingRunRootSha256\":");
            AppendLiteral(sb, marker.StagingRunRootSha256);
            sb.Append(",\"FinalRunRootSha256\":");
            AppendLiteral(sb, marker.FinalRunRootSha256);
            sb.Append('}');

            if (sb.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        /// <summary>
        /// Returns the lowercase 64-character SHA-256 of the marker's canonical
        /// bytes. Nothing is cached on the marker; the hash is recomputed on
        /// every call.
        /// </summary>
        internal static string ComputeContentSha256(CaptureRunInitializationMarker marker)
        {
            if (marker == null)
            {
                throw new ArgumentNullException(nameof(marker));
            }

            byte[] canonical = SerializeCanonical(marker);
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(canonical));
            }
        }

        private static string RootRoleLiteral(CaptureRunRootRole role)
        {
            switch (role)
            {
                case CaptureRunRootRole.Staging:
                    return "Staging";
                case CaptureRunRootRole.Final:
                    return "Final";
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Root role must be Staging or Final.");
            }
        }

        private static void AppendLong(StringBuilder sb, long value)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendLiteral(StringBuilder sb, string value)
        {
            // Values are validated literal ASCII (lowercase hex and the fixed
            // role name), so no JSON escaping is generated.
            sb.Append('"');
            sb.Append(value);
            sb.Append('"');
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                int b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0xF];
            }

            return new string(chars);
        }
    }
}
