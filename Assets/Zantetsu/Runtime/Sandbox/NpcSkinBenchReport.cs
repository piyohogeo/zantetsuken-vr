using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// One frame's row of the benchmark's table (bench-frames.csv). Two kinds of columns stand side by side and are
    /// never to be read as one frame's: the row's own frame (its number, step, clock, the Profiler recorders' values
    /// and the benchmark's counters, all of the frame the row was written in) and FrameTimingManager's result read in
    /// that frame (ft*), which describes an earlier frame -- its start is given as ftFrameStart -- and may be absent.
    /// An absent value is an empty cell, never 0.
    /// </summary>
    public struct NpcSkinBenchRow
    {
        public int frame;
        /// <summary>0 warm-up, 2 settle (after the pose clock's resynchronisation), 1 measurement.</summary>
        public int phase;
        public int step;
        public long wallUtcTicks;
        public double realtimeSeconds, unscaledDeltaSeconds;
        public ulong ftFrameStart;
        public bool hasMain, hasRender, hasGpu, hasPresentWait, hasXrGpu;
        public float ftMainMs, ftRenderMs, ftGpuMs, ftPresentWaitMs, xrAppGpuMs;
        /// <summary>The Profiler recorders of this frame (ns from the recorder, written as ms); NaN when the recorder is not available.</summary>
        public double playerLoopMs, gfxPresentWaitMs;
        /// <summary>
        /// The code's own Stopwatch spans (ms; Development Players and the Editor only, NaN elsewhere; SKIN-BENCH-2 §5): the
        /// pose players applied this frame (evaluate, apply, summed), and the skin display's last UpdateFrame -- which the
        /// drawing runs after this row is written, so these are the frame before's (whole update, palette pass, the bone
        /// matrices gathered within it, upload, the palette SetData calls within it).
        /// </summary>
        public double poseEvaluateMs, poseApplyMs, skinFrameMs, skinPaletteMs, skinGatherMs, skinUploadMs, skinUploadPaletteMs;
        /// <summary>
        /// The worker gather's spans (SKIN-BENCH-3; ms, the frame before's as the skin spans are, NaN without observation): the
        /// inputs prepared and the jobs scheduled (at the end of the late update), the wait in Complete and the verification and
        /// publication (in UpdateFrame); then whether that frame had jobs (0/1), how many individuals they gathered, and how many
        /// were gathered again on the main thread after the schedule.
        /// </summary>
        public double skinPrepareMs, skinScheduleMs, skinCompleteWaitMs, skinPublishMs;
        public int skinJobs, skinJobIndividuals;
        public long skinLateRegathers;
        public long gcAllocatedBytes;
        public int posesApplied;
        public long palettesGathered, paletteWrites, paletteBytes, paletteBytesExtra, skinBodiesIssued, skinCastersIssued, skinMotionIssued, bodiesIssued, castersIssued, motionIssued, selectionsIssued, boundsDispatches;
        /// <summary>SkinnedMeshRenderers Unity found visible this frame (the renderers' route), or -1 (the skin draw: no per-frame figure).</summary>
        public int visibleRenderers;
        public float eyeX, eyeY, eyeZ, eyeYaw, eyePitch;
        /// <summary>The rig's counter-move this frame under the fixed view (metres, degrees); 0 without it.</summary>
        public float rigMove, rigTurn;
    }

    /// <summary>The benchmark's files: the frame table and the result's JSON (pure formatting, for the tests).</summary>
    public static class NpcSkinBenchReport
    {
        public const string Header = "frame,phase,step,wallUtcTicks,realtimeSeconds,unscaledDeltaSeconds,ftFrameStart,ftMainMs,ftRenderMs,ftGpuMs,ftPresentWaitMs,xrAppGpuMs,"
            + "playerLoopMs,gfxPresentWaitMs,poseEvaluateMs,poseApplyMs,skinFrameMs,skinPaletteMs,skinGatherMs,skinUploadMs,skinUploadPaletteMs,"
            + "skinPrepareMs,skinScheduleMs,skinCompleteWaitMs,skinPublishMs,skinJobs,skinJobIndividuals,skinLateRegathers,"
            + "gcAllocatedBytes,posesApplied,palettesGathered,paletteWrites,paletteBytes,paletteBytesExtra,skinBodiesIssued,skinCastersIssued,skinMotionIssued,bodiesIssued,castersIssued,motionIssued,"
            + "selectionsIssued,boundsDispatches,visibleRenderers,eyeX,eyeY,eyeZ,eyeYaw,eyePitch,rigMove,rigTurn";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Csv(in NpcSkinBenchRow r)
        {
            var b = new StringBuilder(400);
            b.Append(r.frame).Append(',').Append(r.phase).Append(',').Append(r.step).Append(',').Append(r.wallUtcTicks).Append(',')
                .Append(r.realtimeSeconds.ToString("R", Inv)).Append(',').Append(r.unscaledDeltaSeconds.ToString("R", Inv)).Append(',').Append(r.ftFrameStart).Append(',');
            Cell(b, r.hasMain, r.ftMainMs); Cell(b, r.hasRender, r.ftRenderMs); Cell(b, r.hasGpu, r.ftGpuMs); Cell(b, r.hasPresentWait, r.ftPresentWaitMs); Cell(b, r.hasXrGpu, r.xrAppGpuMs);
            Cell(b, r.playerLoopMs); Cell(b, r.gfxPresentWaitMs);
            Cell(b, r.poseEvaluateMs); Cell(b, r.poseApplyMs); Cell(b, r.skinFrameMs); Cell(b, r.skinPaletteMs); Cell(b, r.skinGatherMs); Cell(b, r.skinUploadMs); Cell(b, r.skinUploadPaletteMs);
            Cell(b, r.skinPrepareMs); Cell(b, r.skinScheduleMs); Cell(b, r.skinCompleteWaitMs); Cell(b, r.skinPublishMs);
            b.Append(r.skinJobs).Append(',').Append(r.skinJobIndividuals).Append(',').Append(r.skinLateRegathers).Append(',');
            b.Append(r.gcAllocatedBytes).Append(',').Append(r.posesApplied).Append(',').Append(r.palettesGathered).Append(',').Append(r.paletteWrites).Append(',').Append(r.paletteBytes).Append(',').Append(r.paletteBytesExtra).Append(',')
                .Append(r.skinBodiesIssued).Append(',').Append(r.skinCastersIssued).Append(',').Append(r.skinMotionIssued).Append(',')
                .Append(r.bodiesIssued).Append(',').Append(r.castersIssued).Append(',').Append(r.motionIssued).Append(',')
                .Append(r.selectionsIssued).Append(',').Append(r.boundsDispatches).Append(',').Append(r.visibleRenderers).Append(',')
                .Append(r.eyeX.ToString("R", Inv)).Append(',').Append(r.eyeY.ToString("R", Inv)).Append(',').Append(r.eyeZ.ToString("R", Inv)).Append(',')
                .Append(r.eyeYaw.ToString("R", Inv)).Append(',').Append(r.eyePitch.ToString("R", Inv)).Append(',')
                .Append(r.rigMove.ToString("R", Inv)).Append(',').Append(r.rigTurn.ToString("R", Inv));
            return b.ToString();
        }

        private static void Cell(StringBuilder b, bool has, double value)
        {
            if (has) b.Append(value.ToString("R", Inv));
            b.Append(',');
        }

        private static void Cell(StringBuilder b, double value) => Cell(b, !double.IsNaN(value), value);

        /// <summary>The rows' values of one column in one phase (1: measured), valid ones only (an absent value is left out).</summary>
        public static IEnumerable<double> Column(IReadOnlyList<NpcSkinBenchRow> rows, Func<NpcSkinBenchRow, (bool has, double value)> pick, int phase = 1)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].phase != phase) continue;
                (bool has, double value) = pick(rows[i]);
                if (has) yield return value;
            }
        }

        /// <summary>A JSON string literal.</summary>
        public static string Quote(string text)
        {
            if (text == null) return "null";
            var b = new StringBuilder(text.Length + 2).Append('"');
            foreach (char c in text)
            {
                switch (c)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                        else b.Append(c);
                        break;
                }
            }

            return b.Append('"').ToString();
        }

        public static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("R", Inv);

        /// <summary>A JSON object from name/value pairs whose values are already JSON text.</summary>
        public static string Object(IEnumerable<KeyValuePair<string, string>> members, bool pretty = true)
        {
            var b = new StringBuilder("{");
            bool first = true;
            foreach (KeyValuePair<string, string> m in members)
            {
                if (!first) b.Append(',');
                first = false;
                if (pretty) b.Append("\n  ");
                b.Append(Quote(m.Key)).Append(": ").Append(m.Value ?? "null");
            }

            if (pretty) b.Append('\n');
            return b.Append('}').ToString();
        }

        public static string Array(IEnumerable<string> values)
        {
            var b = new StringBuilder("[");
            bool first = true;
            foreach (string v in values)
            {
                if (!first) b.Append(',');
                first = false;
                b.Append(v ?? "null");
            }

            return b.Append(']').ToString();
        }
    }
}
