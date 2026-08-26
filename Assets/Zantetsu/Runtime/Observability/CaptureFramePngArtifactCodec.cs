using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

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
}
