using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>Canonical UTF-8 serializer for generic artifact publication plans.</summary>
    internal static class CapturePublicationPlanCodec
    {
        internal const int MaximumCanonicalByteCount = 16 * 1024 * 1024;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        [Serializable]
        private sealed class PlanDto
        {
            public int schemaVersion;
            public long testRunId;
            public string runInitializationId;
            public string runManifestContentHash;
            public ArtifactDto[] artifactDescriptors;
            public EvidenceDto[] captureFrameEvidenceEntries;
        }

        [Serializable]
        private sealed class ArtifactDto
        {
            public string artifactId;
            public int artifactKind;
            public string formatId;
            public int formatVersion;
            public string stagingRelativePath;
            public string finalRelativePath;
            public long byteLength;
            public string contentHash;
        }

        [Serializable]
        private sealed class EvidenceDto
        {
            public long captureFrameId;
            public string[] artifactIds;
        }

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

        internal static CapturePublicationPlan DeserializeCanonical(Stream source, int maximumByteCount)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (!source.CanRead) throw new ArgumentException("Source must be readable.", nameof(source));
            if (maximumByteCount < 1 || maximumByteCount > MaximumCanonicalByteCount)
                throw new ArgumentOutOfRangeException(nameof(maximumByteCount));

            byte[] buffer = new byte[checked(maximumByteCount + 1)];
            int count = 0;
            while (count < buffer.Length)
            {
                int read = source.Read(buffer, count, buffer.Length - count);
                if (read == 0) break;
                count = checked(count + read);
            }
            if (count == 0) throw new ArgumentException("Canonical plan must not be empty.", nameof(source));
            if (count > maximumByteCount) throw new ArgumentException("Canonical plan exceeds the byte limit.", nameof(source));
            byte[] bytes = new byte[count];
            Array.Copy(buffer, bytes, count);
            return DeserializeCanonical(bytes);
        }

        internal static CapturePublicationPlan DeserializeCanonical(byte[] canonicalBytes)
        {
            if (canonicalBytes == null) throw new ArgumentNullException(nameof(canonicalBytes));
            if (canonicalBytes.Length < 1 || canonicalBytes.Length > MaximumCanonicalByteCount)
                throw new ArgumentException("Canonical plan byte count is outside the supported range.", nameof(canonicalBytes));

            string json;
            try
            {
                json = StrictUtf8.GetString(canonicalBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new ArgumentException("Canonical plan must be strict UTF-8.", nameof(canonicalBytes), ex);
            }

            PlanDto dto;
            try
            {
                dto = JsonUtility.FromJson<PlanDto>(json);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("Canonical plan JSON is invalid.", nameof(canonicalBytes), ex);
            }
            if (dto == null || dto.schemaVersion != 2 || dto.artifactDescriptors == null || dto.captureFrameEvidenceEntries == null)
                throw new ArgumentException("Canonical plan root is incomplete.", nameof(canonicalBytes));

            CaptureArtifactDescriptor[] descriptors = new CaptureArtifactDescriptor[dto.artifactDescriptors.Length];
            for (int i = 0; i < descriptors.Length; i++)
            {
                ArtifactDto value = dto.artifactDescriptors[i];
                if (value == null) throw new ArgumentException("Canonical plan contains a null artifact.", nameof(canonicalBytes));
                descriptors[i] = new CaptureArtifactDescriptor(
                    value.artifactId,
                    (CaptureArtifactKind)value.artifactKind,
                    value.formatId,
                    value.formatVersion,
                    value.stagingRelativePath,
                    value.finalRelativePath,
                    value.byteLength,
                    value.contentHash);
            }

            CaptureFrameEvidenceEntry[] evidence = new CaptureFrameEvidenceEntry[dto.captureFrameEvidenceEntries.Length];
            for (int i = 0; i < evidence.Length; i++)
            {
                EvidenceDto value = dto.captureFrameEvidenceEntries[i];
                if (value == null || value.artifactIds == null)
                    throw new ArgumentException("Canonical plan contains incomplete frame evidence.", nameof(canonicalBytes));
                evidence[i] = new CaptureFrameEvidenceEntry(value.captureFrameId, value.artifactIds);
            }

            CapturePublicationPlan plan = new CapturePublicationPlan(
                dto.testRunId,
                dto.runInitializationId,
                dto.runManifestContentHash,
                descriptors,
                evidence);
            byte[] expected = SerializeCanonical(plan);
            if (!BytesEqual(expected, canonicalBytes))
                throw new ArgumentException("Plan bytes are valid JSON but not canonical.", nameof(canonicalBytes));
            return plan;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
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
