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
        internal const int MaximumArtifactCount = 200000;
        internal const int MaximumCaptureFrameEvidenceCount = 100000;
        internal const int MaximumArtifactReferencesPerFrame = 200000;
        private const int MaximumTextByteCount = 512;
        private const int MaximumEncodedTextByteCount = MaximumTextByteCount * 6;
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

            // Validate the fixed schema and all structural counts before
            // JsonUtility is allowed to allocate a DTO graph.
            ValidateStructureBeforeObjectification(canonicalBytes);

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

        private static void ValidateStructureBeforeObjectification(byte[] document)
        {
            PreflightReader reader = new PreflightReader(document);
            reader.Expect((byte)'{');
            reader.Expect("\"schemaVersion\":");
            if (reader.ReadInteger() != 2) throw new ArgumentException("Unsupported publication plan schema.", nameof(document));
            reader.Expect(",\"testRunId\":");
            reader.ReadInteger();
            reader.Expect(",\"runInitializationId\":");
            reader.SkipString(32);
            reader.Expect(",\"runManifestContentHash\":");
            reader.SkipString(64);
            reader.Expect(",\"artifactDescriptors\":[");

            int artifactCount = 0;
            if (!reader.TryConsume((byte)']'))
            {
                do
                {
                    artifactCount = checked(artifactCount + 1);
                    if (artifactCount > MaximumArtifactCount)
                        throw new ArgumentException("Artifact count exceeds the structural limit.", nameof(document));
                    ValidateArtifactStructure(reader);
                }
                while (reader.TryConsume((byte)','));
                reader.Expect((byte)']');
            }

            reader.Expect(",\"captureFrameEvidenceEntries\":[");
            int frameCount = 0;
            if (!reader.TryConsume((byte)']'))
            {
                do
                {
                    frameCount = checked(frameCount + 1);
                    if (frameCount > MaximumCaptureFrameEvidenceCount)
                        throw new ArgumentException("Frame evidence count exceeds the structural limit.", nameof(document));
                    ValidateEvidenceStructure(reader);
                }
                while (reader.TryConsume((byte)','));
                reader.Expect((byte)']');
            }
            reader.Expect((byte)'}');
            reader.ExpectEnd();
        }

        private static void ValidateArtifactStructure(PreflightReader reader)
        {
            reader.Expect((byte)'{');
            reader.Expect("\"artifactId\":"); reader.SkipString(MaximumEncodedTextByteCount);
            reader.Expect(",\"artifactKind\":"); reader.ReadInteger();
            reader.Expect(",\"formatId\":"); reader.SkipString(MaximumEncodedTextByteCount);
            reader.Expect(",\"formatVersion\":"); reader.ReadInteger();
            reader.Expect(",\"stagingRelativePath\":"); reader.SkipString(MaximumEncodedTextByteCount);
            reader.Expect(",\"finalRelativePath\":"); reader.SkipString(MaximumEncodedTextByteCount);
            reader.Expect(",\"byteLength\":"); reader.ReadInteger();
            reader.Expect(",\"contentHash\":"); reader.SkipString(64);
            reader.Expect((byte)'}');
        }

        private static void ValidateEvidenceStructure(PreflightReader reader)
        {
            reader.Expect((byte)'{');
            reader.Expect("\"captureFrameId\":"); reader.ReadInteger();
            reader.Expect(",\"artifactIds\":[");
            int referenceCount = 0;
            if (!reader.TryConsume((byte)']'))
            {
                do
                {
                    referenceCount = checked(referenceCount + 1);
                    if (referenceCount > MaximumArtifactReferencesPerFrame)
                        throw new ArgumentException("Artifact reference count exceeds the structural limit.", "document");
                    reader.SkipString(MaximumEncodedTextByteCount);
                }
                while (reader.TryConsume((byte)','));
                reader.Expect((byte)']');
            }
            reader.Expect((byte)'}');
        }

        /// <summary>
        /// Allocation-free token scanner used only for structural preflight.
        /// String contents are not materialized; canonical reserialization
        /// after DTO construction remains the value-level authority.
        /// </summary>
        private sealed class PreflightReader
        {
            private readonly byte[] _bytes;
            private int _position;

            internal PreflightReader(byte[] bytes) { _bytes = bytes; }

            internal void Expect(byte expected)
            {
                if (_position >= _bytes.Length || _bytes[_position] != expected)
                    throw new ArgumentException("Publication plan structure is not canonical.", "document");
                _position++;
            }

            internal void Expect(string ascii)
            {
                for (int i = 0; i < ascii.Length; i++) Expect((byte)ascii[i]);
            }

            internal bool TryConsume(byte value)
            {
                if (_position >= _bytes.Length || _bytes[_position] != value) return false;
                _position++;
                return true;
            }

            internal long ReadInteger()
            {
                if (_position >= _bytes.Length || _bytes[_position] < '0' || _bytes[_position] > '9')
                    throw new ArgumentException("Publication plan integer is invalid.", "document");
                if (_bytes[_position] == '0')
                {
                    _position++;
                    if (_position < _bytes.Length && _bytes[_position] >= '0' && _bytes[_position] <= '9')
                        throw new ArgumentException("Publication plan integer has a leading zero.", "document");
                    return 0;
                }
                long value = 0;
                while (_position < _bytes.Length && _bytes[_position] >= '0' && _bytes[_position] <= '9')
                {
                    int digit = _bytes[_position++] - '0';
                    if (value > (long.MaxValue - digit) / 10)
                        throw new ArgumentException("Publication plan integer overflows Int64.", "document");
                    value = value * 10 + digit;
                }
                return value;
            }

            internal void SkipString(int maximumEncodedByteCount)
            {
                Expect((byte)'\"');
                int encodedCount = 0;
                while (true)
                {
                    if (_position >= _bytes.Length)
                        throw new ArgumentException("Publication plan string is unterminated.", "document");
                    byte value = _bytes[_position++];
                    if (value == '"') break;
                    encodedCount++;
                    if (encodedCount > maximumEncodedByteCount)
                        throw new ArgumentException("Publication plan string exceeds its structural limit.", "document");
                    if (value < 0x20)
                        throw new ArgumentException("Publication plan string contains a control byte.", "document");
                    if (value == '\\')
                    {
                        if (_position >= _bytes.Length)
                            throw new ArgumentException("Publication plan escape is incomplete.", "document");
                        byte escaped = _bytes[_position++];
                        encodedCount++;
                        if (escaped == 'u')
                        {
                            for (int i = 0; i < 4; i++)
                            {
                                if (_position >= _bytes.Length || !IsHex(_bytes[_position++]))
                                    throw new ArgumentException("Publication plan Unicode escape is invalid.", "document");
                                encodedCount++;
                            }
                        }
                        else if (escaped != '"' && escaped != '\\' && escaped != 'n' && escaped != 'r' && escaped != 't')
                        {
                            throw new ArgumentException("Publication plan escape is invalid.", "document");
                        }
                        if (encodedCount > maximumEncodedByteCount)
                            throw new ArgumentException("Publication plan string exceeds its structural limit.", "document");
                    }
                }
            }

            internal void ExpectEnd()
            {
                if (_position != _bytes.Length)
                    throw new ArgumentException("Publication plan has trailing bytes.", "document");
            }

            private static bool IsHex(byte value) =>
                (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
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
