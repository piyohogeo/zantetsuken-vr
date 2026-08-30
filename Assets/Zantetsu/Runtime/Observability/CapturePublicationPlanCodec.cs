using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>Canonical UTF-8 serializer for generic artifact publication plans.</summary>
    internal static class CapturePublicationPlanCodec
    {
        internal const int MaximumCanonicalByteCount = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(CapturePublicationPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (!plan.IsValid) throw new ArgumentException("Plan must be valid.", nameof(plan));
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"schemaVersion\":2,\"testRunId\":").Append(plan.TestRunId.ToString(CultureInfo.InvariantCulture));
            AppendString(sb, "runInitializationId", plan.RunInitializationId);
            AppendString(sb, "runManifestContentHash", plan.RunManifestContentHash);
            sb.Append(",\"artifactDescriptors\":[");
            for (int i = 0; i < plan.ArtifactCount; i++)
            {
                if (i != 0) sb.Append(',');
                CaptureArtifactDescriptor d = plan.GetArtifact(i);
                sb.Append('{');
                AppendFirstString(sb, "artifactId", d.ArtifactId);
                sb.Append(",\"artifactKind\":").Append(((int)d.ArtifactKind).ToString(CultureInfo.InvariantCulture));
                AppendString(sb, "formatId", d.FormatId);
                sb.Append(",\"formatVersion\":").Append(d.FormatVersion.ToString(CultureInfo.InvariantCulture));
                AppendString(sb, "stagingRelativePath", d.StagingRelativePath);
                AppendString(sb, "finalRelativePath", d.FinalRelativePath);
                sb.Append(",\"byteLength\":").Append(d.ByteLength.ToString(CultureInfo.InvariantCulture));
                AppendString(sb, "contentHash", d.ContentHash);
                sb.Append('}');
            }
            sb.Append("],\"captureFrameEvidenceEntries\":[");
            for (int i = 0; i < plan.CaptureFrameEvidenceCount; i++)
            {
                if (i != 0) sb.Append(',');
                CaptureFrameEvidenceEntry e = plan.GetCaptureFrameEvidence(i);
                sb.Append("{\"captureFrameId\":").Append(e.CaptureFrameId.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"artifactIds\":[");
                for (int j = 0; j < e.ArtifactCount; j++)
                {
                    if (j != 0) sb.Append(',');
                    AppendQuoted(sb, e.GetArtifactId(j));
                }
                sb.Append("]}");
            }
            sb.Append("]}");
            byte[] bytes = Utf8.GetBytes(sb.ToString());
            if (bytes.Length > MaximumCanonicalByteCount) throw new InvalidOperationException("Canonical plan exceeds size limit.");
            return bytes;
        }

        private static void AppendFirstString(StringBuilder sb, string name, string value)
        {
            sb.Append('\"').Append(name).Append("\":");
            AppendQuoted(sb, value);
        }

        private static void AppendString(StringBuilder sb, string name, string value)
        {
            sb.Append(",\"").Append(name).Append("\":");
            AppendQuoted(sb, value);
        }

        private static void AppendQuoted(StringBuilder sb, string value)
        {
            sb.Append('\"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '\"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('\"');
        }
    }
}
