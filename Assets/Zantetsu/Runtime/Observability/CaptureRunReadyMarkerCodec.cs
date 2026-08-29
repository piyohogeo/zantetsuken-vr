using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON serializer for <see cref="CaptureRunReadyMarker"/>.
    /// The canonical form is byte-for-byte deterministic: no BOM, no trailing
    /// newline, no extra whitespace, fixed PascalCase property order, invariant
    /// shortest decimal integers, and literal ASCII strings without escape
    /// representations.
    /// </summary>
    /// <remarks>
    /// This codec performs no file I/O, no hash computation, no clock, no
    /// random, and no Unity static API access.
    /// </remarks>
    internal static class CaptureRunReadyMarkerCodec
    {
        internal const int MaximumCanonicalByteCount = 4 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CaptureRunReadyMarker marker)
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
            sb.Append(",\"StagingInitSha256\":");
            AppendLiteral(sb, marker.StagingInitSha256);
            sb.Append(",\"FinalInitSha256\":");
            AppendLiteral(sb, marker.FinalInitSha256);
            sb.Append('}');

            if (sb.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        private static void AppendLong(StringBuilder sb, long value)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendLiteral(StringBuilder sb, string value)
        {
            // Values are validated literal ASCII (lowercase hex), so no JSON
            // escaping is generated.
            sb.Append('"');
            sb.Append(value);
            sb.Append('"');
        }
    }
}
