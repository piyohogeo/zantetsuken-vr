using System;
using System.Globalization;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>Canonical Phase 0 JSON metadata owned only by the PNG+JSON backend.</summary>
    internal static class PngJsonFrameMetadataCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        internal static byte[] SerializeCanonical(
            CaptureFrameEnvelope frame,
            CaptureArtifactDescriptor image)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (image == null || !image.IsValid || image.ArtifactKind != CaptureArtifactKind.FrameImage)
            {
                throw new ArgumentException("Image descriptor must be valid.", nameof(image));
            }

            CaptureFrameTraceContext c = frame.TraceContext;
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\"schemaVersion\":2");
            AppendLong(sb, "captureFrameId", frame.CaptureFrameId);
            AppendLong(sb, "unityFrameId", frame.UnityFrameId);
            AppendLong(sb, "openXRFrameId", frame.OpenXRFrameId);
            AppendLong(sb, "timestamp", c.Timestamp);
            AppendLong(sb, "fixedStepId", c.FixedStepId);
            AppendLong(sb, "threadId", c.ThreadId);
            AppendLong(sb, "testRunId", frame.TestRunId);
            AppendLong(sb, "testCaseId", frame.TestCaseId);
            AppendString(sb, "buildId", frame.BuildId);
            AppendString(sb, "sceneId", frame.SceneId);
            AppendLong(sb, "randomSeed", frame.RandomSeed);
            AppendLong(sb, "slashId", frame.SlashId);
            AppendLong(sb, "frontEdgeId", c.FrontEdgeId);
            AppendLong(sb, "objectId", frame.ObjectId);
            AppendLong(sb, "objectGeneration", frame.ObjectGeneration);
            AppendLong(sb, "taskId", frame.TaskId);
            AppendLong(sb, "commitPathId", frame.CommitPathId);
            AppendLong(sb, "captureProfileId", frame.CaptureProfileId);
            AppendLong(sb, "captureSource", (int)frame.CaptureSource);
            AppendLong(sb, "eye", (int)frame.Eye);
            AppendLong(sb, "colorSpace", (int)frame.ColorSpace);
            sb.Append(",\"imageRect\":{\"x\":").Append(frame.ImageRect.X.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"y\":").Append(frame.ImageRect.Y.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"width\":").Append(frame.ImageRect.Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"height\":").Append(frame.ImageRect.Height.ToString(CultureInfo.InvariantCulture)).Append('}');
            sb.Append(",\"pixelLayout\":{\"format\":").Append(((int)frame.PixelLayout.Format).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"width\":").Append(frame.PixelLayout.Width.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"height\":").Append(frame.PixelLayout.Height.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"bytesPerPixel\":").Append(frame.PixelLayout.BytesPerPixel.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"rowStrideBytes\":").Append(frame.PixelLayout.RowStrideBytes.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"byteCount\":").Append(frame.PixelLayout.ByteCount.ToString(CultureInfo.InvariantCulture)).Append('}');
            AppendDouble(sb, "predictedDisplayTimeSeconds", frame.Timing.PredictedDisplayTimeSeconds);
            AppendDouble(sb, "predictedDisplayPeriodSeconds", frame.Timing.PredictedDisplayPeriodSeconds);
            sb.Append(",\"shouldRender\":").Append(frame.Timing.ShouldRender ? "true" : "false");
            AppendDouble(sb, "appGpuTimeMilliseconds", frame.Timing.AppGpuTimeMilliseconds);
            AppendDouble(sb, "compositorGpuTimeMilliseconds", frame.Timing.CompositorGpuTimeMilliseconds);
            AppendLong(sb, "droppedFrameCount", frame.Timing.DroppedFrameCount);
            AppendPose(sb, "headPose", frame.HeadPose);
            AppendPose(sb, "leftControllerPose", frame.LeftControllerPose);
            AppendPose(sb, "rightControllerPose", frame.RightControllerPose);
            AppendString(sb, "frameImageArtifactId", image.ArtifactId);
            AppendString(sb, "frameImageFormatId", image.FormatId);
            AppendLong(sb, "frameImageByteLength", image.ByteLength);
            AppendString(sb, "frameImageContentHash", image.ContentHash);
            sb.Append('}');
            return Utf8.GetBytes(sb.ToString());
        }

        private static void AppendLong(StringBuilder sb, string name, long value) =>
            sb.Append(",\"").Append(name).Append("\":").Append(value.ToString(CultureInfo.InvariantCulture));

        private static void AppendDouble(StringBuilder sb, string name, double value) =>
            sb.Append(",\"").Append(name).Append("\":").Append(value == 0.0 ? "0" : value.ToString("R", CultureInfo.InvariantCulture));

        private static void AppendString(StringBuilder sb, string name, string value)
        {
            sb.Append(",\"").Append(name).Append("\":\"");
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

        private static void AppendPose(StringBuilder sb, string name, CapturePoseSample sample)
        {
            sb.Append(",\"").Append(name).Append("\":{\"available\":")
                .Append(sample.IsAvailable ? "true" : "false");
            if (sample.IsAvailable)
            {
                sb.Append(",\"position\":{");
                AppendFloatMember(sb, "x", sample.Position.x, true);
                AppendFloatMember(sb, "y", sample.Position.y, false);
                AppendFloatMember(sb, "z", sample.Position.z, false);
                sb.Append("},\"rotation\":{");
                AppendFloatMember(sb, "x", sample.Rotation.x, true);
                AppendFloatMember(sb, "y", sample.Rotation.y, false);
                AppendFloatMember(sb, "z", sample.Rotation.z, false);
                AppendFloatMember(sb, "w", sample.Rotation.w, false);
                sb.Append('}');
            }
            sb.Append('}');
        }

        private static void AppendFloatMember(StringBuilder sb, string name, float value, bool first)
        {
            if (!first) sb.Append(',');
            sb.Append('\"').Append(name).Append("\":")
                .Append(value == 0f ? "0" : value.ToString("R", CultureInfo.InvariantCulture));
        }
    }
}
