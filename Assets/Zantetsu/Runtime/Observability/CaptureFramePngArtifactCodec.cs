using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Canonical UTF-8 JSON codec and content hash for
    /// <see cref="CaptureFramePngArtifact"/>. The canonical form is
    /// byte-for-byte deterministic: no BOM, no whitespace, fixed property order,
    /// invariant number formatting. Only serialization and the content hash are
    /// implemented; deserialization and file storage live in later phases.
    /// </summary>
    public static class CaptureFramePngArtifactCodec
    {
        public const int CurrentSchemaVersion = 1;

        public const int MaximumCanonicalByteCount = 65536;

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        private static readonly UTF8Encoding Utf8Strict = new UTF8Encoding(false, true);

        /// <summary>
        /// Serializes the artifact into its canonical UTF-8 JSON byte sequence.
        /// </summary>
        public static byte[] SerializeCanonical(CaptureFramePngArtifact artifact)
        {
            if (artifact == null)
            {
                throw new ArgumentNullException(nameof(artifact));
            }

            string pngFileName = Path.GetFileName(artifact.DestinationPath);
            if (string.IsNullOrEmpty(pngFileName))
            {
                throw new InvalidOperationException("PNG destination path must include a file name.");
            }

            CaptureFrameRecord record = artifact.FrameRecord;
            CaptureFrameTraceContext context = record.Request.TraceContext;

            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"schemaVersion\":");
            sb.Append(CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture));

            sb.Append(",\"captureFrameId\":");
            sb.Append(context.CaptureFrameId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"unityFrameId\":");
            sb.Append(context.UnityFrameId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"openXRFrameId\":");
            sb.Append(context.OpenXRFrameId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"timestamp\":");
            sb.Append(context.Timestamp.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"fixedStepId\":");
            sb.Append(context.FixedStepId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"threadId\":");
            sb.Append(context.ThreadId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"testRunId\":");
            sb.Append(context.TestRunId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"testCaseId\":");
            sb.Append(record.TestCaseId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"buildId\":");
            AppendJsonString(sb, record.BuildId);
            sb.Append(",\"sceneId\":");
            AppendJsonString(sb, record.SceneId);
            sb.Append(",\"randomSeed\":");
            sb.Append(record.RandomSeed.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"slashId\":");
            sb.Append(context.SlashId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"frontEdgeId\":");
            sb.Append(context.FrontEdgeId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"objectId\":");
            sb.Append(context.ObjectId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"objectGeneration\":");
            sb.Append(context.ObjectGeneration.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"taskId\":");
            sb.Append(context.TaskId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"commitPathId\":");
            sb.Append(record.CommitPathId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"captureSource\":");
            sb.Append(((int)record.Source).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"eye\":");
            sb.Append(((int)record.Eye).ToString(CultureInfo.InvariantCulture));

            sb.Append(",\"imageRect\":{\"x\":");
            sb.Append(record.ImageRect.X.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"y\":");
            sb.Append(record.ImageRect.Y.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"width\":");
            sb.Append(record.ImageRect.Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"height\":");
            sb.Append(record.ImageRect.Height.ToString(CultureInfo.InvariantCulture));
            sb.Append("}");

            sb.Append(",\"arrayIndex\":");
            sb.Append(record.ArrayIndex.ToString(CultureInfo.InvariantCulture));

            CaptureFramePixelLayout pixelLayout = record.Request.PixelLayout;
            sb.Append(",\"pixelLayout\":{\"format\":");
            sb.Append(((int)pixelLayout.Format).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"width\":");
            sb.Append(pixelLayout.Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"height\":");
            sb.Append(pixelLayout.Height.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"bytesPerPixel\":");
            sb.Append(pixelLayout.BytesPerPixel.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"rowStrideBytes\":");
            sb.Append(pixelLayout.RowStrideBytes.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"byteCount\":");
            sb.Append(pixelLayout.ByteCount.ToString(CultureInfo.InvariantCulture));
            sb.Append("}");

            AppendTiming(sb, record.Timing);

            AppendPose(sb, "headPose", record.HeadPose);
            AppendPose(sb, "leftControllerPose", record.LeftControllerPose);
            AppendPose(sb, "rightControllerPose", record.RightControllerPose);

            sb.Append(",\"captureProfileId\":");
            sb.Append(record.CaptureProfileId.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"runManifestContentSha256\":");
            AppendJsonString(sb, record.RunManifestContentSha256);

            sb.Append(",\"pngFileName\":");
            AppendJsonString(sb, pngFileName);
            sb.Append(",\"pngByteCount\":");
            sb.Append(artifact.PngByteCount.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"pngContentSha256\":");
            AppendJsonString(sb, artifact.PngContentSha256);
            sb.Append("}");

            byte[] bytes = Utf8NoBom.GetBytes(sb.ToString());

            if (bytes.Length > MaximumCanonicalByteCount)
            {
                throw new InvalidOperationException("Canonical JSON exceeds the maximum allowed byte count.");
            }

            return bytes;
        }

        /// <summary>
        /// Computes the SHA-256 of the artifact's canonical byte sequence. This
        /// is the artifact sidecar content hash, distinct from the PNG content
        /// hash and the run manifest content hash.
        /// </summary>
        public static string ComputeContentSha256(CaptureFramePngArtifact artifact)
        {
            byte[] canonical = SerializeCanonical(artifact);

            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(canonical);
            }

            return ToLowerHex(hash);
        }

        /// <summary>
        /// Parses canonical UTF-8 JSON and restores a
        /// <see cref="CaptureFramePngArtifact"/>, validating every value against
        /// the provided run manifest and the strict canonical byte form.
        /// </summary>
        /// <remarks>
        /// The run manifest is the source of truth for test run, build, scene,
        /// random seed, and manifest content hash; JSON whose run fields do not
        /// match is rejected. The PNG destination path is rebuilt from the
        /// validated directory and the JSON basename only, so absolute paths and
        /// path traversal are rejected. No PNG, sidecar, or hash is read; the
        /// result only reconstructs the save-time record.
        /// </remarks>
        public static CaptureFramePngArtifact DeserializeCanonical(
            byte[] utf8Json,
            TraceRunManifest runManifest,
            string pngDirectory)
        {
            if (utf8Json == null)
            {
                throw new ArgumentNullException(nameof(utf8Json));
            }

            if (runManifest == null)
            {
                throw new ArgumentNullException(nameof(runManifest));
            }

            if (pngDirectory == null)
            {
                throw new ArgumentNullException(nameof(pngDirectory));
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

            string normalizedDirectory = NormalizePngDirectory(pngDirectory);

            string json;
            try
            {
                json = Utf8Strict.GetString(utf8Json);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Canonical JSON is not valid UTF-8.", ex);
            }

            ArtifactRootDto dto;
            try
            {
                dto = JsonUtility.FromJson<ArtifactRootDto>(json);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Canonical JSON is malformed.", ex);
            }

            if (dto == null)
            {
                throw new InvalidDataException("Canonical JSON root must be an object.");
            }

            if (dto.schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException("Unsupported artifact schema version.");
            }

            ValidateRunManifestMatch(dto, runManifest);

            CaptureFramePngArtifact artifact = BuildArtifact(dto, runManifest, normalizedDirectory);

            byte[] canonical;
            try
            {
                canonical = SerializeCanonical(artifact);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidDataException("Decoded artifact cannot be represented within the canonical size limit.", ex);
            }

            if (!BytesEqual(canonical, utf8Json))
            {
                throw new InvalidDataException("Canonical JSON is not in canonical form.");
            }

            return artifact;
        }

        private static string NormalizePngDirectory(string pngDirectory)
        {
            if (string.IsNullOrWhiteSpace(pngDirectory))
            {
                throw new ArgumentException("PNG directory must not be empty or whitespace.", nameof(pngDirectory));
            }

            if (!Path.IsPathFullyQualified(pngDirectory))
            {
                throw new ArgumentException("PNG directory must be fully qualified.", nameof(pngDirectory));
            }

            string fullPath = Path.GetFullPath(pngDirectory);
            string root = Path.GetPathRoot(fullPath);

            int end = fullPath.Length;
            while (end > 0 && IsDirectorySeparator(fullPath[end - 1]))
            {
                end--;
            }

            if (end == fullPath.Length)
            {
                return fullPath;
            }

            string trimmed = fullPath.Substring(0, end);
            if (root != null && root.Length > 0 && IsDirectorySeparator(root[root.Length - 1])
                && string.Equals(trimmed, root.Substring(0, root.Length - 1), StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            return trimmed;
        }

        private static bool IsDirectorySeparator(char c)
        {
            return c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
        }

        private static void ValidateRunManifestMatch(ArtifactRootDto dto, TraceRunManifest runManifest)
        {
            if (dto.testRunId != runManifest.TestRunId)
            {
                throw new InvalidDataException("Test run ID does not match the run manifest.");
            }

            if (!string.Equals(dto.buildId, runManifest.BuildId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Build ID does not match the run manifest.");
            }

            if (!string.Equals(dto.sceneId, runManifest.SceneId, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Scene ID does not match the run manifest.");
            }

            if (dto.randomSeed != runManifest.RandomSeed)
            {
                throw new InvalidDataException("Random seed does not match the run manifest.");
            }

            string computedHash = TraceRunManifestCodec.ComputeContentSha256(runManifest);
            if (!string.Equals(dto.runManifestContentSha256, computedHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Run manifest content hash does not match the run manifest.");
            }
        }

        private static CaptureFramePngArtifact BuildArtifact(ArtifactRootDto dto, TraceRunManifest runManifest, string normalizedDirectory)
        {
            try
            {
                CaptureRunReference run = new CaptureRunReference(runManifest, dto.testCaseId, dto.captureProfileId, dto.runManifestContentSha256);

                if (dto.objectGeneration < 0 || dto.objectGeneration > uint.MaxValue)
                {
                    throw new ArgumentException("Object generation is out of range.", nameof(dto.objectGeneration));
                }

                CaptureFrameTraceContext traceContext = new CaptureFrameTraceContext(
                    dto.timestamp,
                    dto.unityFrameId,
                    dto.fixedStepId,
                    dto.threadId,
                    dto.captureFrameId,
                    dto.openXRFrameId,
                    dto.testRunId,
                    dto.slashId,
                    dto.frontEdgeId,
                    dto.objectId,
                    (uint)dto.objectGeneration,
                    dto.taskId);

                CaptureImageRect imageRect = BuildImageRect(dto.imageRect);

                if (dto.pixelLayout == null)
                {
                    throw new InvalidDataException("Canonical JSON is missing the pixel layout.");
                }

                CaptureFrameRequest request = new CaptureFrameRequest(
                    traceContext,
                    (CaptureSource)dto.captureSource,
                    (CaptureEye)dto.eye,
                    imageRect,
                    dto.arrayIndex,
                    (CapturePixelFormat)dto.pixelLayout.format);

                CaptureFrameTiming timing = BuildTiming(dto.timing);

                CapturePoseSample head = BuildPose(dto.headPose);
                CapturePoseSample left = BuildPose(dto.leftControllerPose);
                CapturePoseSample right = BuildPose(dto.rightControllerPose);

                CaptureFrameRecord record = new CaptureFrameRecord(run, request, timing, head, left, right, dto.commitPathId);

                string destinationPath = BuildPngPath(normalizedDirectory, dto.pngFileName);
                CaptureFramePngSaveReceipt receipt = new CaptureFramePngSaveReceipt(destinationPath, dto.pngByteCount, dto.pngContentSha256);

                return new CaptureFramePngArtifact(record, request, receipt);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Canonical JSON contains invalid values.", ex);
            }
        }

        private static CaptureImageRect BuildImageRect(ArtifactImageRectDto dto)
        {
            if (dto == null)
            {
                throw new InvalidDataException("Canonical JSON is missing the image rect.");
            }

            return new CaptureImageRect(dto.x, dto.y, dto.width, dto.height);
        }

        private static CaptureFrameTiming BuildTiming(ArtifactTimingDto dto)
        {
            if (dto == null)
            {
                throw new InvalidDataException("Canonical JSON is missing the timing.");
            }

            return new CaptureFrameTiming(
                dto.predictedDisplayTimeSeconds,
                dto.predictedDisplayPeriodSeconds,
                dto.shouldRender,
                dto.appGpuTimeMilliseconds,
                dto.compositorGpuTimeMilliseconds,
                dto.droppedFrameCount);
        }

        private static CapturePoseSample BuildPose(ArtifactPoseDto dto)
        {
            if (dto == null)
            {
                throw new InvalidDataException("Canonical JSON is missing a pose.");
            }

            if (dto.position == null || dto.rotation == null)
            {
                throw new InvalidDataException("Canonical JSON pose must include position and rotation.");
            }

            if (!dto.available)
            {
                if (dto.position.x != 0f || dto.position.y != 0f || dto.position.z != 0f
                    || dto.rotation.x != 0f || dto.rotation.y != 0f || dto.rotation.z != 0f || dto.rotation.w != 0f)
                {
                    throw new InvalidDataException("Unavailable pose must use canonical default values.");
                }

                return CapturePoseSample.Unavailable;
            }

            return CapturePoseSample.RestoreCanonical(
                new Vector3(dto.position.x, dto.position.y, dto.position.z),
                new Quaternion(dto.rotation.x, dto.rotation.y, dto.rotation.z, dto.rotation.w));
        }

        private static string BuildPngPath(string normalizedDirectory, string pngFileName)
        {
            if (string.IsNullOrWhiteSpace(pngFileName))
            {
                throw new InvalidDataException("PNG file name must not be empty or whitespace.");
            }

            if (Path.IsPathRooted(pngFileName))
            {
                throw new InvalidDataException("PNG file name must be a basename.");
            }

            if (pngFileName.IndexOf('/') >= 0 || pngFileName.IndexOf('\\') >= 0)
            {
                throw new InvalidDataException("PNG file name must not contain directory separators.");
            }

            if (pngFileName == "." || pngFileName == "..")
            {
                throw new InvalidDataException("PNG file name must not be a relative directory reference.");
            }

            if (Path.GetFileName(pngFileName) != pngFileName)
            {
                throw new InvalidDataException("PNG file name must be a basename.");
            }

            if (!string.Equals(Path.GetExtension(pngFileName), ".png", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PNG file name must end with '.png'.");
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            for (int i = 0; i < pngFileName.Length; i++)
            {
                for (int j = 0; j < invalidChars.Length; j++)
                {
                    if (pngFileName[i] == invalidChars[j])
                    {
                        throw new InvalidDataException("PNG file name contains invalid path characters.");
                    }
                }
            }

            string fullPath = Path.GetFullPath(Path.Combine(normalizedDirectory, pngFileName));
            if (!string.Equals(Path.GetDirectoryName(fullPath), normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PNG file name escapes the destination directory.");
            }

            return fullPath;
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

        private static void AppendTiming(StringBuilder sb, CaptureFrameTiming timing)
        {
            sb.Append(",\"timing\":{\"predictedDisplayTimeSeconds\":");
            AppendDouble(sb, timing.PredictedDisplayTimeSeconds);
            sb.Append(",\"predictedDisplayPeriodSeconds\":");
            AppendDouble(sb, timing.PredictedDisplayPeriodSeconds);
            sb.Append(",\"shouldRender\":");
            sb.Append(timing.ShouldRender ? "true" : "false");
            sb.Append(",\"appGpuTimeMilliseconds\":");
            AppendDouble(sb, timing.AppGpuTimeMilliseconds);
            sb.Append(",\"compositorGpuTimeMilliseconds\":");
            AppendDouble(sb, timing.CompositorGpuTimeMilliseconds);
            sb.Append(",\"droppedFrameCount\":");
            sb.Append(timing.DroppedFrameCount.ToString(CultureInfo.InvariantCulture));
            sb.Append("}");
        }

        private static void AppendPose(StringBuilder sb, string propertyName, CapturePoseSample pose)
        {
            sb.Append(",\"").Append(propertyName).Append("\":{\"available\":");
            sb.Append(pose.IsAvailable ? "true" : "false");
            sb.Append(",\"position\":{\"x\":");
            AppendFloat(sb, pose.Position.x);
            sb.Append(",\"y\":");
            AppendFloat(sb, pose.Position.y);
            sb.Append(",\"z\":");
            AppendFloat(sb, pose.Position.z);
            sb.Append("},\"rotation\":{\"x\":");
            AppendFloat(sb, pose.Rotation.x);
            sb.Append(",\"y\":");
            AppendFloat(sb, pose.Rotation.y);
            sb.Append(",\"z\":");
            AppendFloat(sb, pose.Rotation.z);
            sb.Append(",\"w\":");
            AppendFloat(sb, pose.Rotation.w);
            sb.Append("}}");
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
    internal sealed class ArtifactVector3Dto
    {
        public float x;
        public float y;
        public float z;
    }

    [Serializable]
    internal sealed class ArtifactQuaternionDto
    {
        public float x;
        public float y;
        public float z;
        public float w;
    }

    [Serializable]
    internal sealed class ArtifactPoseDto
    {
        public bool available;
        public ArtifactVector3Dto position;
        public ArtifactQuaternionDto rotation;
    }

    [Serializable]
    internal sealed class ArtifactImageRectDto
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    [Serializable]
    internal sealed class ArtifactPixelLayoutDto
    {
        public int format;
        public int width;
        public int height;
        public int bytesPerPixel;
        public int rowStrideBytes;
        public int byteCount;
    }

    [Serializable]
    internal sealed class ArtifactTimingDto
    {
        public double predictedDisplayTimeSeconds;
        public double predictedDisplayPeriodSeconds;
        public bool shouldRender;
        public double appGpuTimeMilliseconds;
        public double compositorGpuTimeMilliseconds;
        public long droppedFrameCount;
    }

    [Serializable]
    internal sealed class ArtifactRootDto
    {
        public int schemaVersion;
        public long captureFrameId;
        public long unityFrameId;
        public long openXRFrameId;
        public long timestamp;
        public long fixedStepId;
        public int threadId;
        public long testRunId;
        public long testCaseId;
        public string buildId;
        public string sceneId;
        public long randomSeed;
        public long slashId;
        public long frontEdgeId;
        public long objectId;
        public long objectGeneration;
        public long taskId;
        public int commitPathId;
        public int captureSource;
        public int eye;
        public ArtifactImageRectDto imageRect;
        public int arrayIndex;
        public ArtifactPixelLayoutDto pixelLayout;
        public ArtifactTimingDto timing;
        public ArtifactPoseDto headPose;
        public ArtifactPoseDto leftControllerPose;
        public ArtifactPoseDto rightControllerPose;
        public int captureProfileId;
        public string runManifestContentSha256;
        public string pngFileName;
        public int pngByteCount;
        public string pngContentSha256;
    }
}
