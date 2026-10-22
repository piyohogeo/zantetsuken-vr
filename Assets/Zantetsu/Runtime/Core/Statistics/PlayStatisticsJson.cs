using System.Globalization;
using System.Text;

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// Writes a <see cref="PlayStatistics"/> as the one JSON body a send carries (DESIGN 21.18): the receiver's
    /// envelope -- schema_version, session_id, install_id, revision, tester_role -- and <c>stats</c>, this record. Written
    /// into one reused <see cref="StringBuilder"/>, at a snapshot only, never every frame. Counts are integers;
    /// milliseconds carry one decimal; nothing is written as 0 for "not obtained" -- the missing counts say that, and a name or
    /// version not obtained is null.
    /// </summary>
    public static class PlayStatisticsJson
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The receiver's envelope version (the Worker's contract), apart from the record's own <see cref="PlayStatistics.FormatVersion"/>.</summary>
        public const int SchemaVersion = 1;

        public static void Write(StringBuilder b, PlayStatistics s, string sessionId, string installId, int revision, string testerRole)
        {
            b.Clear();
            b.Append("{\"schema_version\":").Append(SchemaVersion);
            Str(b, ",\"session_id\":", sessionId);
            Str(b, ",\"install_id\":", installId);
            b.Append(",\"revision\":").Append(revision);
            Str(b, ",\"tester_role\":", testerRole);
            b.Append(",\"stats\":{");
            b.Append("\"format\":").Append(PlayStatistics.FormatVersion);
            b.Append(",\"histogram\":").Append(PlayTimeHistogram.Version);
            Str(b, ",\"build\":", s.BuildId);
            Str(b, ",\"timeSource\":", s.TimeSource);
            b.Append(",\"mainIncludesPresentWait\":true");
            b.Append(",\"env\":{");
            Str(b, "\"platform\":", s.Platform);
            Str(b, ",\"os\":", s.OperatingSystem);
            StrOrNull(b, ",\"deviceModel\":", s.DeviceModel);
            Str(b, ",\"cpu\":", s.Cpu);
            Str(b, ",\"gpu\":", s.Gpu);
            Str(b, ",\"graphicsApi\":", s.GraphicsApi);
            StrOrNull(b, ",\"gpuDriver\":", s.GpuDriver);
            StrOrNull(b, ",\"xrDevice\":", s.XrDevice);
            StrOrNull(b, ",\"xrRuntime\":", s.XrRuntime);
            StrOrNull(b, ",\"xrRuntimeVersion\":", s.XrRuntimeVersion);
            b.Append('}');
            b.Append(",\"attributionLagFrames\":").Append(PlayStatisticsCollector.AttributionLagFrames);
            PlayUiSettings u = s.Settings;
            b.Append(",\"settings\":");
            if (!u.Known) b.Append("null");
            else
            {
                b.Append("{\"volume\":").Append(u.Volume.ToString("0.##", Inv)).Append(",\"moveSpeed\":").Append(u.MoveSpeed.ToString("0.##", Inv))
                    .Append(",\"turn\":").Append(u.TurnDegrees.ToString("0.#", Inv)).Append(",\"turnContinuous\":").Append(u.TurnIsContinuous ? "true" : "false")
                    .Append(",\"turnOnRightStick\":").Append(u.TurnOnRightStick ? "true" : "false")
                    .Append(",\"bladeAngle\":").Append(u.BladeAngleDegrees.ToString("0.#", Inv)).Append('}');
            }

            b.Append(",\"load\":{\"maxLiveFragments\":").Append(s.MaxLiveFragments)
                .Append(",\"acceptedCuts\":").Append(s.AcceptedCuts)
                .Append(",\"collectedFragments\":").Append(s.CollectedFragments).Append('}');
            b.Append(",\"capacities\":{");
            for (int i = 0; i < (int)PlayCapacityKind.Count; i++)
            {
                var kind = (PlayCapacityKind)i;
                PlayCapacity c = s.Capacity(kind);
                if (i > 0) b.Append(',');
                b.Append('"').Append(PlayStatistics.CapacityName(kind)).Append("\":{\"unit\":\"").Append(PlayStatistics.CapacityUnit(kind))
                    .Append("\",\"capacity\":").Append(c.Capacity).Append(",\"limit\":");
                if (c.Limit > 0) b.Append(c.Limit); else b.Append("null");
                b.Append(",\"maxEnd\":").Append(c.MaxEnd).Append(",\"minHeadroomToCapacity\":");
                if (c.Read) b.Append(c.MinHeadroomToCapacity); else b.Append("null");
                b.Append(",\"minHeadroomToLimit\":");
                if (c.Read && c.Limit > 0) b.Append(c.MinHeadroomToLimit); else b.Append("null");
                b.Append(",\"growths\":").Append(c.Growths).Append(",\"shortfalls\":").Append(c.Shortfalls).Append('}');
            }

            b.Append("},\"failures\":{");
            for (int i = 0; i < (int)PlayFailureKind.Count; i++)
            {
                if (i > 0) b.Append(',');
                b.Append('"').Append(PlayStatistics.FailureName((PlayFailureKind)i)).Append("\":").Append(s.Failures((PlayFailureKind)i));
            }

            b.Append("},\"epochsDropped\":").Append(s.EpochChangesDropped)
                .Append(",\"framesOutsideEpochs\":").Append(s.FramesOutsideEpochs);
            b.Append(",\"epochs\":[");
            for (int i = 0; i < s.EpochCount; i++)
            {
                if (i > 0) b.Append(',');
                WriteEpoch(b, s.EpochAt(i));
            }

            b.Append("]}}");
        }

        private static void WriteEpoch(StringBuilder b, PlayEpoch e)
        {
            PlayConditions c = e.Conditions;
            b.Append("{\"conditions\":{");
            Str(b, "\"scene\":", c.Scene);
            Str(b, ",\"mode\":", c.Mode);
            Str(b, ",\"route\":", c.DrawRoute);
            StrOrNull(b, ",\"xrRuntime\":", c.XrRuntime);
            b.Append(",\"refreshHz\":").Append(c.RefreshRateHz.ToString("0.#", Inv));
            b.Append(",\"renderScale\":").Append(c.RenderScale.ToString("0.###", Inv));
            b.Append(",\"eye\":[").Append(c.EyeTextureWidth).Append(',').Append(c.EyeTextureHeight).Append(']');
            b.Append(",\"screen\":[").Append(c.ScreenWidth).Append(',').Append(c.ScreenHeight).Append(']');
            b.Append(",\"fsrEnabled\":").Append(c.FsrEnabled ? "true" : "false");
            b.Append(",\"multiDrawIndirect\":").Append(c.MultiDrawIndirect ? "true" : "false");
            b.Append(",\"drawIndirectCount\":").Append(c.DrawIndirectCount ? "true" : "false");
            b.Append(",\"msaa\":").Append(c.MsaaSamples);
            b.Append(",\"cutSurfaceDebug\":").Append(c.CutSurfaceDebug ? "true" : "false");
            b.Append("},\"frames\":").Append(e.Frames)
                .Append(",\"unattributed\":").Append(e.Unattributed)
                .Append(",\"structureChangedFrames\":").Append(e.StructureChangedFrames)
                .Append(",\"playSeconds\":").Append(e.PlaySeconds.ToString("0.#", Inv));
            Histogram(b, ",\"main\":", e.Main, e.MainMissing);
            Histogram(b, ",\"render\":", e.Render, e.RenderMissing);
            Histogram(b, ",\"gpu\":", e.Gpu, e.GpuMissing);
            Histogram(b, ",\"gpuXr\":", e.GpuXr, e.GpuXrMissing);
            Histogram(b, ",\"interval\":", e.Interval, 0);
            b.Append('}');
        }

        private static void Histogram(StringBuilder b, string key, PlayTimeHistogram h, ulong missing)
        {
            b.Append(key).Append("{\"samples\":").Append(h.Samples).Append(",\"missing\":").Append(missing)
                .Append(",\"maxMs\":").Append(h.MaxMs.ToString("0.#", Inv)).Append(",\"overflow\":").Append(h.Overflow).Append(",\"counts\":[");
            for (int i = 0; i < PlayTimeHistogram.BucketCount; i++)
            {
                if (i > 0) b.Append(',');
                b.Append(h[i]);
            }

            b.Append("]}");
        }

        // A value that may be "not obtained": null then, never an empty string or a stand-in.
        private static void StrOrNull(StringBuilder b, string key, string value)
        {
            if (string.IsNullOrEmpty(value)) b.Append(key).Append("null");
            else Str(b, key, value);
        }

        private static void Str(StringBuilder b, string key, string value)
        {
            b.Append(key).Append('"');
            if (value != null)
            {
                foreach (char ch in value)
                {
                    switch (ch)
                    {
                        case '"': b.Append("\\\""); break;
                        case '\\': b.Append("\\\\"); break;
                        case '\n': b.Append("\\n"); break;
                        case '\r': b.Append("\\r"); break;
                        case '\t': b.Append("\\t"); break;
                        default:
                            if (ch < 0x20) b.Append("\\u").Append(((int)ch).ToString("x4", Inv));
                            else b.Append(ch);
                            break;
                    }
                }
            }

            b.Append('"');
        }
    }
}
