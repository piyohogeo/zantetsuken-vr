using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON serializer for <see cref="CapturePublicationPlan"/>.
    /// The canonical form is byte-for-byte deterministic: no BOM, no trailing
    /// newline, no extra whitespace, PascalCase property order per the Schema
    /// v1 contract, invariant shortest decimal integers, and literal ASCII
    /// strings without escape representations.
    /// </summary>
    /// <remarks>
    /// This codec performs no file I/O, hash recomputation, clock, random, or
    /// Unity static API access, re-sorts no entry, and mutates no input. The
    /// 16 MiB output limit is monitored while entries are appended so an
    /// oversized plan fails before the whole document is materialized.
    /// </remarks>
    internal static class CapturePublicationPlanCodec
    {
        internal const int MaximumCanonicalByteCount = 16 * 1024 * 1024;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CapturePublicationPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"SchemaVersion\":");
            AppendLong(sb, plan.SchemaVersion);
            sb.Append(",\"TestRunId\":");
            AppendLong(sb, plan.TestRunId);
            sb.Append(",\"RunInitializationId\":");
            AppendLiteral(sb, plan.RunInitializationId);
            sb.Append(",\"RunManifestContentSha256\":");
            AppendLiteral(sb, plan.RunManifestContentSha256);
            sb.Append(",\"EntryCount\":");
            AppendLong(sb, plan.EntryCount);
            sb.Append(",\"Entries\":[");

            for (int i = 0; i < plan.EntryCount; i++)
            {
                if (i > 0)
                {
                    sb.Append(',');
                }

                AppendEntry(sb, plan.GetEntry(i));

                // Every serialized value is validated literal ASCII, so the
                // UTF-16 length equals the UTF-8 byte length. Monitor the limit
                // while writing instead of only after the document is built.
                if (sb.Length > MaximumCanonicalByteCount)
                {
                    throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
                }
            }

            sb.Append("]}");

            if (sb.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return Utf8NoBom.GetBytes(sb.ToString());
        }

        private static void AppendEntry(StringBuilder sb, CapturePublicationPlanEntry entry)
        {
            sb.Append("{\"CaptureFrameId\":");
            AppendLong(sb, entry.CaptureFrameId);
            sb.Append(",\"PngStagingRelativePath\":");
            AppendLiteral(sb, entry.PngStagingRelativePath);
            sb.Append(",\"SidecarStagingRelativePath\":");
            AppendLiteral(sb, entry.SidecarStagingRelativePath);
            sb.Append(",\"PngFinalRelativePath\":");
            AppendLiteral(sb, entry.PngFinalRelativePath);
            sb.Append(",\"SidecarFinalRelativePath\":");
            AppendLiteral(sb, entry.SidecarFinalRelativePath);
            sb.Append(",\"PngByteLength\":");
            AppendLong(sb, entry.PngByteLength);
            sb.Append(",\"SidecarByteLength\":");
            AppendLong(sb, entry.SidecarByteLength);
            sb.Append(",\"PngContentSha256\":");
            AppendLiteral(sb, entry.PngContentSha256);
            sb.Append(",\"SidecarContentSha256\":");
            AppendLiteral(sb, entry.SidecarContentSha256);
            sb.Append('}');
        }

        private static void AppendLong(StringBuilder sb, long value)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendLiteral(StringBuilder sb, string value)
        {
            // Values are validated literal ASCII (fixed paths, lowercase hex
            // hashes and initialization ID), so no JSON escaping is generated.
            sb.Append('"');
            sb.Append(value);
            sb.Append('"');
        }
    }
}
