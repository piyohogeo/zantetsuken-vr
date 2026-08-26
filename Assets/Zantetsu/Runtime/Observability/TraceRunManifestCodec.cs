using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON codec and content hash for <see cref="TraceRunManifest"/>.
    /// The canonical form is byte-for-byte deterministic: no BOM, no whitespace,
    /// fixed property order, invariant number formatting.
    /// </summary>
    public static class TraceRunManifestCodec
    {
        public const int MaximumCanonicalByteCount = 65536;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        /// <summary>
        /// Serializes the manifest into its canonical UTF-8 JSON byte sequence.
        /// </summary>
        public static byte[] SerializeCanonical(TraceRunManifest manifest)
        {
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            StringBuilder sb = new StringBuilder(512);
            sb.Append("{\"schemaVersion\":");
            sb.Append(manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"testRunId\":");
            sb.Append(manifest.TestRunId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"capturedUtcUnixMilliseconds\":");
            sb.Append(manifest.CapturedUtcUnixMilliseconds.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"buildId\":");
            AppendJsonString(sb, manifest.BuildId);
            sb.Append(",\"unityVersion\":");
            AppendJsonString(sb, manifest.UnityVersion);
            sb.Append(",\"packageLockSha256\":");
            AppendJsonString(sb, manifest.PackageLockSha256);
            sb.Append(",\"sceneId\":");
            AppendJsonString(sb, manifest.SceneId);
            sb.Append(",\"randomSeed\":");
            sb.Append(manifest.RandomSeed.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"fixedDeltaTimeSeconds\":");
            AppendDouble(sb, manifest.FixedDeltaTimeSeconds);
            sb.Append(",\"qualityLevel\":");
            sb.Append(manifest.QualityLevel.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"qualityName\":");
            AppendJsonString(sb, manifest.QualityName);
            sb.Append(",\"worldPhysicsProfileVersion\":");
            sb.Append(manifest.WorldPhysicsProfileVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"gravity\":{\"x\":");
            AppendFloat(sb, manifest.Gravity.x);
            sb.Append(",\"y\":");
            AppendFloat(sb, manifest.Gravity.y);
            sb.Append(",\"z\":");
            AppendFloat(sb, manifest.Gravity.z);
            sb.Append("},\"traceFormat\":{\"major\":");
            sb.Append(manifest.TraceFormatMajorVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"minor\":");
            sb.Append(manifest.TraceFormatMinorVersion.ToString(CultureInfo.InvariantCulture));
            sb.Append("},\"trace\":{\"eventCount\":");
            sb.Append(manifest.EventCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"triggerHistoryCount\":");
            sb.Append(manifest.TriggerHistoryCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"capturedPostRollCount\":");
            sb.Append(manifest.CapturedPostRollCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"wasHistoryOverwrittenAtTrigger\":");
            sb.Append(manifest.WasHistoryOverwrittenAtTrigger ? "true" : "false");
            sb.Append("}}");

            byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());

            if (bytes.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return bytes;
        }

        /// <summary>
        /// Parses a canonical JSON byte sequence back into a manifest. Only
        /// byte-for-byte canonical input is accepted.
        /// </summary>
        public static TraceRunManifest DeserializeCanonical(byte[] utf8Json)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException(nameof(utf8Json));
            }

            if (utf8Json.Length == 0)
            {
                throw new InvalidDataException("Canonical JSON must not be empty.");
            }

            if (utf8Json.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidDataException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            if (HasUtf8Bom(utf8Json))
            {
                throw new InvalidDataException("Canonical JSON must not have a UTF-8 BOM.");
            }

            string json;
            try
            {
                json = Utf8Strict.GetString(utf8Json);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Canonical JSON is not valid UTF-8.", ex);
            }

            InternalManifestDto dto;
            try
            {
                dto = JsonUtility.FromJson<InternalManifestDto>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException("Canonical JSON is malformed.", ex);
            }

            if (dto == null)
            {
                throw new InvalidDataException("Canonical JSON root must be an object.");
            }

            TraceRunManifest manifest = BuildManifest(dto);

            // Strict canonical check: the input must re-serialize byte-for-byte.
            byte[] canonical;
            try
            {
                canonical = SerializeCanonical(manifest);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException(
                    "Decoded manifest cannot be represented within the canonical size limit.",
                    ex);
            }

            if (!BytesEqual(canonical, utf8Json))
            {
                throw new InvalidDataException("Canonical JSON is not in canonical form.");
            }

            return manifest;
        }

        /// <summary>
        /// Computes the SHA-256 of the manifest's canonical byte sequence. This
        /// is the full-manifest content hash used for content identification and
        /// integrity, not an environment-comparison fingerprint.
        /// </summary>
        public static string ComputeContentSha256(TraceRunManifest manifest)
        {
            byte[] canonical = SerializeCanonical(manifest);

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(canonical);
            }

            return ToLowerHex(hash);
        }

        private static TraceRunManifest BuildManifest(InternalManifestDto dto)
        {
            if (dto.gravity == null)
            {
                throw new InvalidDataException("Canonical JSON is missing the gravity object.");
            }

            if (dto.traceFormat == null)
            {
                throw new InvalidDataException("Canonical JSON is missing the traceFormat object.");
            }

            if (dto.trace == null)
            {
                throw new InvalidDataException("Canonical JSON is missing the trace object.");
            }

            TraceRunContext context;
            try
            {
                context = new TraceRunContext(
                    dto.testRunId,
                    dto.capturedUtcUnixMilliseconds,
                    dto.buildId,
                    dto.unityVersion,
                    dto.packageLockSha256,
                    dto.sceneId,
                    dto.randomSeed,
                    dto.fixedDeltaTimeSeconds,
                    dto.qualityLevel,
                    dto.qualityName,
                    dto.worldPhysicsProfileVersion,
                    new Vector3(dto.gravity.x, dto.gravity.y, dto.gravity.z));
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Canonical JSON contains invalid run context values.", ex);
            }

            return TraceRunManifest.Restore(
                context,
                dto.schemaVersion,
                (ushort)dto.traceFormat.major,
                (ushort)dto.traceFormat.minor,
                dto.trace.eventCount,
                dto.trace.triggerHistoryCount,
                dto.trace.capturedPostRollCount,
                dto.trace.wasHistoryOverwrittenAtTrigger);
        }

        private static void AppendJsonString(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append(c);
                        }

                        break;
                }
            }

            sb.Append('"');
        }

        private static void AppendDouble(StringBuilder sb, double value)
        {
            if (value == 0.0)
            {
                sb.Append('0');
            }
            else
            {
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private static void AppendFloat(StringBuilder sb, float value)
        {
            if (value == 0f)
            {
                sb.Append('0');
            }
            else
            {
                sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            }
        }

        private static bool HasUtf8Bom(byte[] bytes)
        {
            return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length)
            {
                return false;
            }

            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string ToLowerHex(byte[] bytes)
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
    }

    [Serializable]
    internal sealed class InternalGravityDto
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    internal sealed class InternalTraceFormatDto
    {
        public int major;
        public int minor;
    }

    [Serializable]
    internal sealed class InternalTraceDto
    {
        public int eventCount;
        public int triggerHistoryCount;
        public int capturedPostRollCount;
        public bool wasHistoryOverwrittenAtTrigger;
    }

    [Serializable]
    internal sealed class InternalManifestDto
    {
        public int schemaVersion;
        public long testRunId;
        public long capturedUtcUnixMilliseconds;
        public string buildId;
        public string unityVersion;
        public string packageLockSha256;
        public string sceneId;
        public long randomSeed;
        public double fixedDeltaTimeSeconds;
        public int qualityLevel;
        public string qualityName;
        public int worldPhysicsProfileVersion;
        public InternalGravityDto gravity;
        public InternalTraceFormatDto traceFormat;
        public InternalTraceDto trace;
    }
}
