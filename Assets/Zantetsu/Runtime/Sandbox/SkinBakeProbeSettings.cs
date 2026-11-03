using System;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The GPU bake probe's settings (DESIGN 4.5.14, skin-bake-probe): a diagnosis started by name that shares a drawn
    /// SkinnedMeshRenderer's own mesh GPU buffers (vertices, bone weights, indices), takes one palette on the main
    /// thread, generates the 16-byte render vertices by a compute kernel of its own, compares them with a float-input
    /// CPU reference, and draws the GPU-made vertices through the existing 16-byte route beside the CPU-made ones in the
    /// same frame. Read from a PC Player's arguments, or given by the Quest's entry from its configuration. Nothing of
    /// the product's cut acceptance or publication is touched by it.
    /// </summary>
    public sealed class SkinBakeProbeSettings
    {
        /// <summary>The argument that starts the probe, with the directory its records are written to.</summary>
        public const string Argument = "-zantetsuSkinBakeProbe";
        /// <summary>Which subjects run: <c>small</c> (the known rig), <c>npc</c> (one NPC of the scene's templates) or <c>both</c> (the default).</summary>
        public const string SubjectArgument = "-zantetsuSkinBakeProbeSubject";
        /// <summary>How many frames each drawing window is held (the pictures are taken inside); default 60.</summary>
        public const string DrawFramesArgument = "-zantetsuSkinBakeProbeDrawFrames";
        /// <summary>The numbers only: no drawing through the display, no pictures.</summary>
        public const string NoDrawArgument = "-zantetsuSkinBakeProbeNoDraw";
        public const string NoPictureArgument = "-zantetsuSkinBakeProbeNoPicture";
        public const string KeepOpenArgument = "-zantetsuSkinBakeProbeKeepOpen";
        /// <summary>The performance comparison (SKIN-BAKE-PROBE-2) in place of the cases: <c>burst</c> (the synchronous Burst bake and its upload) or <c>gpu</c> (the compute bake's issue), over the NPC into pre-allocated display regions.</summary>
        public const string BenchArgument = "-zantetsuSkinBakeProbeBench";
        /// <summary>Measured frames (one bake of every body a frame); default 300.</summary>
        public const string BenchFramesArgument = "-zantetsuSkinBakeProbeBenchFrames";
        /// <summary>Warmup frames before the measured ones; default 60.</summary>
        public const string BenchWarmupArgument = "-zantetsuSkinBakeProbeBenchWarmup";
        /// <summary>Bodies (requests with their own output region) a frame, 1..8; default 1.</summary>
        public const string BenchBodiesArgument = "-zantetsuSkinBakeProbeBenchBodies";
        /// <summary>Where the edge windows put the subject along the root's x, in metres (the eye held at the fixed view, the subject 3 m ahead): the picture is one eye's, so its edges differ between the PC Simulator and the Quest. Defaults from the PC pictures: right +0.95 (partly cut), left -4.1.</summary>
        /// <summary>The bench as a series of conditions in one launch (SKIN-BAKE-PROBE-3): tokens route+bodies[xrepeat] separated by commas, route none|product|burst|gpu, bodies 1..8, repeat 1..999 (the GPU bake dispatched that many times a body a frame, for the GPU time's slope); e.g. none1,product1,burst1,gpu1,gpu4,gpu1x4.</summary>
        public const string BenchSeriesArgument = "-zantetsuSkinBakeProbeBenchSeries";
        /// <summary>Extra NPC slots drawn by their SkinnedMeshRenderers as render load during the bench, 0..64 (default 0).</summary>
        public const string BenchLoadArgument = "-zantetsuSkinBakeProbeBenchLoad";
        /// <summary>The cut bench (SKIN-GPU-FIRST-1): this many prepared NPCs (1..8) are cut through the product's own prepared cut in one frame (or staggered), under the launch's skin bake route (-zantetsuSkinBake), and every request's main-thread times, frames to ready and acceptance, the frame's whole times and the switch's equality are recorded.</summary>
        public const string CutBenchArgument = "-zantetsuSkinBakeProbeCutBench";
        /// <summary>Frames between the requests of the cut bench (0: all in one frame; default 0).</summary>
        public const string CutStaggerArgument = "-zantetsuSkinBakeProbeCutStagger";
        /// <summary>Cut bench rounds: the bench is repeated this many times with fresh NPCs (default 1, at most 20).</summary>
        public const string CutRoundsArgument = "-zantetsuSkinBakeProbeCutRounds";
        /// <summary>The cut bench as a performance run: no picture and no verification readback inside the measured frames (TL review 2026-11-01, P2-3).</summary>
        public const string CutNoVerifyArgument = "-zantetsuSkinBakeProbeCutNoVerify";
        /// <summary>Verification runs of the cut bench (SKIN-LINES-1): a picture of every frame from the last warmup frame ("pre") through this many frames after the last request (the per-body request and ready pictures are not taken then); 0, the default: the request and ready pictures only.</summary>
        public const string CutPictureFramesArgument = "-zantetsuSkinBakeProbeCutPictureFrames";
        /// <summary>The shadows the cut bench runs under (SKIN-LINES-1): <c>both</c> (the scene's, the default), <c>nocast</c> (the cut display issues no shadow casters; the renderers and the scene still cast), <c>noreceive</c> (the main light's shadow strength 0: the map is rendered, no surface receives it), <c>none</c> (the main light casts no shadows).</summary>
        public const string ShadowsArgument = "-zantetsuSkinBakeProbeShadows";
        /// <summary>SKIN-LINES-1 step 2: the cut-surface debug colours on (real caps green, temporary faces red) for the whole run.</summary>
        public const string CutSurfaceDebugArgument = "-zantetsuSkinBakeProbeCutSurfaceDebug";
        /// <summary>SKIN-LINES-1 step 2: the stencil cap pass withheld (no temporary cut face drawn) for the whole run.</summary>
        public const string CapsOffArgument = "-zantetsuSkinBakeProbeCapsOff";
        /// <summary>The cut bench's plane (PLANE-1): <c>legacy</c> (the default: <c>float4(0, 1, 0, -localBounds.center.y)</c> in the renderer's frame, which splits this project's Z-up NPC meshes front/back) or <c>horizontal</c> (the world's horizontal plane through the same centre taken to world, the world's up the positive side, converted to the renderer's frame by <see cref="VpCutPlane.TryWorldToGeometryLocal"/>; a refusal is a verification failure, nothing is substituted).</summary>
        public const string CutPlaneArgument = "-zantetsuSkinBakeProbeCutPlane";
        /// <summary>Diagnosis (PLANE-1): the gpu route's CPU bake is kept out of the dispatcher this many frames after its hit, so that the geometry wait is long enough to picture; the acceptance, the physics and the display run as before. Such a run is excluded from the performance summaries.</summary>
        public const string HoldCpuReadyArgument = "-zantetsuSkinBakeProbeHoldCpuReady";
        /// <summary>Diagnosis (PLANE-1): the AppSW motion output (both eyes) read back for every pictured frame of the cut bench and written beside the pictures (Quest: the native route's motion pass; nothing on a platform without it).</summary>
        public const string MotionCaptureArgument = "-zantetsuSkinBakeProbeMotionCapture";
        /// <summary>MOTION-CAPTURE-1: the capture-method check in place of the cases -- two small quads above the horizon (Unity's renderer, the native 16B route), the fixed view moved by the frame number (hold, right a step a frame, hold, left, hold), the AppSW motion output and what the motion pass used captured for the wanted frames.</summary>
        public const string MotionCheckArgument = "-zantetsuSkinBakeProbeMotionCheck";
        /// <summary>The step of the view a frame while moving, in centimetres (default 1).</summary>
        public const string MotionCheckStepArgument = "-zantetsuSkinBakeProbeMotionCheckStepCm";
        /// <summary>The frames of each move (default 6) and of each hold (default 12).</summary>
        public const string MotionCheckFramesArgument = "-zantetsuSkinBakeProbeMotionCheckFrames";
        public const string MotionCheckHoldArgument = "-zantetsuSkinBakeProbeMotionCheckHold";
        /// <summary>MOTION-CAPTURE-1 step 6: in the cut bench (with the motion capture), the fixed eye follows the check's view sequence by the frame's index, the first moving frame the first request's frame.</summary>
        public const string MotionViewArgument = "-zantetsuSkinBakeProbeMotionView";
        /// <summary>SKIN-GPU-FIRST-STARTUP-1: the preparation before the cut bench -- none, or the product's drawing prewarm (QuestDrawPrewarm; the Quest host provides it).</summary>
        public const string WarmArgument = "-zantetsuSkinBakeProbeWarm";
        public static readonly string[] WarmKinds = { "none", "draw", "skin-prepare", "skin-dispatch", "draw+skin-dispatch" };
        public const string EdgeRightArgument = "-zantetsuSkinBakeProbeEdgeRight";
        public const string EdgeLeftArgument = "-zantetsuSkinBakeProbeEdgeLeft";

        public const int DefaultDrawFrames = 60;
        public const int DefaultBenchFrames = 300, DefaultBenchWarmup = 60, DefaultBenchBodies = 1, MaxBenchBodies = 8;
        public const float DefaultEdgeRight = 0.95f, DefaultEdgeLeft = -4.1f, EdgeLimit = 10f;
        public const int MaxBenchRepeat = 999, MaxBenchLoad = 64;
        public const int MaxCutBodies = 8, MaxCutStagger = 60, MaxCutRounds = 20, MaxCutPictureFrames = 60;
        public static readonly string[] ShadowConditions = { "both", "nocast", "noreceive", "none" };
        public static readonly string[] CutPlanes = { SkinBakeProbeCutPlane.Legacy, SkinBakeProbeCutPlane.Horizontal };
        public const int MaxHoldCpuReady = 60;

        /// <summary>One condition of the bench series: the route, the bodies (requests with their own region) a frame, the GPU dispatch repeat.</summary>
        public sealed class BenchCondition
        {
            public string route;
            public int bodies = 1;
            public int repeat = 1;
            public string Name => route + bodies + (repeat > 1 ? "x" + repeat : "");
        }

        /// <summary>Parses a series text; false with the reason for an unknown route, bodies outside 1..8, a repeat outside 1..999 or a repeat on a route other than gpu.</summary>
        public static bool TryParseSeries(string text, out System.Collections.Generic.List<BenchCondition> series, out string failure)
        {
            series = null; failure = null;
            if (string.IsNullOrWhiteSpace(text)) { failure = "a series needs at least one condition"; return false; }
            var list = new System.Collections.Generic.List<BenchCondition>();
            foreach (string raw in text.Split(','))
            {
                string token = raw.Trim().ToLowerInvariant();
                var m = System.Text.RegularExpressions.Regex.Match(token, "^(none|product|burst|gpu)([0-9]+)(?:x([0-9]+))?$");
                if (!m.Success) { failure = "condition '" + raw.Trim() + "' is not route+bodies[xrepeat] (route none, product, burst or gpu)"; return false; }
                var c = new BenchCondition { route = m.Groups[1].Value, bodies = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), repeat = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) : 1 };
                if (c.bodies < 1 || c.bodies > MaxBenchBodies) { failure = "condition '" + raw.Trim() + "': bodies 1.." + MaxBenchBodies; return false; }
                if (c.repeat < 1 || c.repeat > MaxBenchRepeat) { failure = "condition '" + raw.Trim() + "': repeat 1.." + MaxBenchRepeat; return false; }
                if (c.repeat > 1 && c.route != "gpu") { failure = "condition '" + raw.Trim() + "': a repeat is the gpu route's only"; return false; }
                list.Add(c);
            }

            series = list;
            return true;
        }

        public string directory;
        public bool small = true;
        public bool npc = true;
        public bool draw = true;
        public bool picture = true;
        public bool keepOpen;
        public int drawFrames = DefaultDrawFrames;
        /// <summary>null: the cases; "burst" or "gpu": the performance comparison of that route.</summary>
        public string bench;
        public int benchFrames = DefaultBenchFrames;
        public int benchWarmup = DefaultBenchWarmup;
        public int benchBodies = DefaultBenchBodies;
        /// <summary>The bench's conditions in order (SKIN-BAKE-PROBE-3); the one-route bench (<see cref="bench"/>) is a series of one condition.</summary>
        public System.Collections.Generic.List<BenchCondition> benchSeries;
        public int benchLoad;
        /// <summary>The cut bench's bodies (0: no cut bench), the frames between its requests, its rounds (SKIN-GPU-FIRST-1).</summary>
        public int cutBodies;
        public int cutStagger;
        public int cutRounds = 1;
        public bool cutNoVerify;
        /// <summary>SKIN-LINES-1: the frames pictured after the last request (0: the request and ready pictures only) and the shadow condition.</summary>
        public int cutPictureFrames;
        public string shadows = "both";
        public bool cutSurfaceDebug, capsOff, motionCapture;
        /// <summary>MOTION-CAPTURE-1: the capture-method check and its sequence.</summary>
        public bool motionCheck;
        public float motionCheckStepCm = 1f;
        public int motionCheckFrames = 6, motionCheckHold = 12;
        public bool IsMotionCheck => motionCheck;
        /// <summary>MOTION-CAPTURE-1 step 6: the cut bench's eye follows the check's sequence (<see cref="motionCheckStepCm"/>, <see cref="motionCheckFrames"/>, <see cref="motionCheckHold"/>); needs the cut bench and <see cref="motionCapture"/>.</summary>
        public bool motionView;
        /// <summary>SKIN-GPU-FIRST-STARTUP-1: the preparation before the cut bench (<see cref="WarmKinds"/>).</summary>
        public string warm = "none";
        /// <summary>PLANE-1: the cut bench's plane setting and the gpu route's CPU-bake hold (frames; 0: none).</summary>
        public string cutPlane = SkinBakeProbeCutPlane.Legacy;
        public int holdCpuReady;
        public bool IsCutBench => cutBodies > 0;
        public bool IsBench => benchSeries != null && benchSeries.Count > 0;
        public string SeriesText => benchSeries == null ? "" : string.Join(",", benchSeries.ConvertAll(c => c.Name));
        public float edgeRight = DefaultEdgeRight;
        public float edgeLeft = DefaultEdgeLeft;

        /// <summary>
        /// The settings a set of arguments asks. False with the reason when <see cref="Argument"/> is absent or has no
        /// directory, or a value is not understood; nothing is guessed.
        /// </summary>
        public static bool TryRead(string[] arguments, out SkinBakeProbeSettings settings, out string failure)
        {
            settings = null;
            failure = null;
            if (arguments == null) { failure = Argument + " <directory> is needed"; return false; }
            var s = new SkinBakeProbeSettings();
            string Value(int at) => at + 1 < arguments.Length ? arguments[at + 1] : null;
            bool found = false;
            for (int i = 0; i < arguments.Length; i++)
            {
                string a = arguments[i];
                if (string.Equals(a, Argument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (string.IsNullOrEmpty(v) || v.StartsWith("-", StringComparison.Ordinal)) { failure = Argument + " needs a directory"; return false; }
                    s.directory = v; found = true;
                }
                else if (string.Equals(a, SubjectArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (string.Equals(v, "small", StringComparison.OrdinalIgnoreCase)) { s.small = true; s.npc = false; }
                    else if (string.Equals(v, "npc", StringComparison.OrdinalIgnoreCase)) { s.small = false; s.npc = true; }
                    else if (string.Equals(v, "both", StringComparison.OrdinalIgnoreCase)) { s.small = true; s.npc = true; }
                    else { failure = SubjectArgument + " takes small, npc or both, not '" + (v ?? "") + "'"; return false; }
                }
                else if (string.Equals(a, DrawFramesArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 2 || n > 100000) { failure = DrawFramesArgument + " takes a whole number 2..100000, not '" + (v ?? "") + "'"; return false; }
                    s.drawFrames = n;
                }
                else if (string.Equals(a, BenchArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (string.Equals(v, "burst", StringComparison.OrdinalIgnoreCase)) s.bench = "burst";
                    else if (string.Equals(v, "gpu", StringComparison.OrdinalIgnoreCase)) s.bench = "gpu";
                    else { failure = BenchArgument + " takes burst or gpu, not '" + (v ?? "") + "'"; return false; }
                }
                else if (string.Equals(a, BenchFramesArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 10 || n > 100000) { failure = BenchFramesArgument + " takes a whole number 10..100000, not '" + (v ?? "") + "'"; return false; }
                    s.benchFrames = n;
                }
                else if (string.Equals(a, BenchWarmupArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > 10000) { failure = BenchWarmupArgument + " takes a whole number 0..10000, not '" + (v ?? "") + "'"; return false; }
                    s.benchWarmup = n;
                }
                else if (string.Equals(a, BenchBodiesArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > MaxBenchBodies) { failure = BenchBodiesArgument + " takes a whole number 1.." + MaxBenchBodies + ", not '" + (v ?? "") + "'"; return false; }
                    s.benchBodies = n;
                }
                else if (string.Equals(a, BenchSeriesArgument, StringComparison.Ordinal))
                {
                    if (!TryParseSeries(Value(i), out s.benchSeries, out string why)) { failure = BenchSeriesArgument + ": " + why; return false; }
                }
                else if (string.Equals(a, BenchLoadArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > MaxBenchLoad) { failure = BenchLoadArgument + " takes a whole number 0.." + MaxBenchLoad + ", not '" + (v ?? "") + "'"; return false; }
                    s.benchLoad = n;
                }
                else if (string.Equals(a, CutBenchArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > MaxCutBodies) { failure = CutBenchArgument + " takes a whole number 1.." + MaxCutBodies + ", not '" + (v ?? "") + "'"; return false; }
                    s.cutBodies = n;
                }
                else if (string.Equals(a, CutStaggerArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > MaxCutStagger) { failure = CutStaggerArgument + " takes a whole number 0.." + MaxCutStagger + ", not '" + (v ?? "") + "'"; return false; }
                    s.cutStagger = n;
                }
                else if (string.Equals(a, CutRoundsArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > MaxCutRounds) { failure = CutRoundsArgument + " takes a whole number 1.." + MaxCutRounds + ", not '" + (v ?? "") + "'"; return false; }
                    s.cutRounds = n;
                }
                else if (string.Equals(a, EdgeRightArgument, StringComparison.Ordinal) || string.Equals(a, EdgeLeftArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) || float.IsNaN(m) || m < -EdgeLimit || m > EdgeLimit) { failure = a + " takes metres within +-" + EdgeLimit + ", not '" + (v ?? "") + "'"; return false; }
                    if (string.Equals(a, EdgeRightArgument, StringComparison.Ordinal)) s.edgeRight = m; else s.edgeLeft = m;
                }
                else if (string.Equals(a, CutPictureFramesArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > MaxCutPictureFrames) { failure = CutPictureFramesArgument + " takes a whole number 0.." + MaxCutPictureFrames + ", not '" + (v ?? "") + "'"; return false; }
                    s.cutPictureFrames = n;
                }
                else if (string.Equals(a, ShadowsArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (v == null || Array.IndexOf(ShadowConditions, v) < 0) { failure = ShadowsArgument + " takes " + string.Join("|", ShadowConditions) + ", not '" + (v ?? "") + "'"; return false; }
                    s.shadows = v;
                }
                else if (string.Equals(a, CutNoVerifyArgument, StringComparison.Ordinal)) s.cutNoVerify = true;
                else if (string.Equals(a, CutPlaneArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (v == null || Array.IndexOf(CutPlanes, v) < 0) { failure = CutPlaneArgument + " takes " + string.Join("|", CutPlanes) + ", not '" + (v ?? "") + "'"; return false; }
                    s.cutPlane = v;
                }
                else if (string.Equals(a, HoldCpuReadyArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 0 || n > MaxHoldCpuReady) { failure = HoldCpuReadyArgument + " takes a whole number 0.." + MaxHoldCpuReady + ", not '" + (v ?? "") + "'"; return false; }
                    s.holdCpuReady = n;
                }
                else if (string.Equals(a, CutSurfaceDebugArgument, StringComparison.Ordinal)) s.cutSurfaceDebug = true;
                else if (string.Equals(a, MotionCaptureArgument, StringComparison.Ordinal)) s.motionCapture = true;
                else if (string.Equals(a, MotionCheckArgument, StringComparison.Ordinal)) s.motionCheck = true;
                else if (string.Equals(a, MotionViewArgument, StringComparison.Ordinal)) s.motionView = true;
                else if (string.Equals(a, WarmArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (v == null || Array.IndexOf(WarmKinds, v) < 0) { failure = WarmArgument + " takes " + string.Join("|", WarmKinds) + ", not '" + (v ?? "") + "'"; return false; }
                    s.warm = v;
                }
                else if (string.Equals(a, MotionCheckStepArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float cm) || float.IsNaN(cm) || cm <= 0f || cm > 50f) { failure = MotionCheckStepArgument + " takes centimetres in (0, 50], not '" + (v ?? "") + "'"; return false; }
                    s.motionCheckStepCm = cm;
                }
                else if (string.Equals(a, MotionCheckFramesArgument, StringComparison.Ordinal) || string.Equals(a, MotionCheckHoldArgument, StringComparison.Ordinal))
                {
                    string v = Value(i);
                    if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1 || n > 120) { failure = a + " takes a whole number 1..120, not '" + (v ?? "") + "'"; return false; }
                    if (string.Equals(a, MotionCheckFramesArgument, StringComparison.Ordinal)) s.motionCheckFrames = n; else s.motionCheckHold = n;
                }
                else if (string.Equals(a, CapsOffArgument, StringComparison.Ordinal)) s.capsOff = true;
                else if (string.Equals(a, NoDrawArgument, StringComparison.Ordinal)) s.draw = false;
                else if (string.Equals(a, NoPictureArgument, StringComparison.Ordinal)) s.picture = false;
                else if (string.Equals(a, KeepOpenArgument, StringComparison.Ordinal)) s.keepOpen = true;
            }

            if (!found) { failure = Argument + " <directory> is needed"; return false; }
            if (s.bench != null && s.benchSeries != null) { failure = BenchArgument + " and " + BenchSeriesArgument + " are two ways of asking for the bench: give one"; return false; }
            if (s.cutBodies > 0 && (s.bench != null || s.benchSeries != null)) { failure = CutBenchArgument + " and the bake bench are two different benches: give one"; return false; }
            if (s.motionCheck && (s.cutBodies > 0 || s.bench != null || s.benchSeries != null)) { failure = MotionCheckArgument + " runs alone: no cut bench and no bake bench with it"; return false; }
            if (s.motionView && s.cutBodies == 0) { failure = MotionViewArgument + " belongs to the cut bench (" + CutBenchArgument + ")"; return false; }
            if (s.motionView && !s.motionCapture) { failure = MotionViewArgument + " needs " + MotionCaptureArgument + " (the moved view is for the motion output read back)"; return false; }
            // The view sequence starts in the last warmup frame of the frame window (TL review P3, 2026-11-02): a setting without the window is refused, not silently still.
            if (s.motionView && (s.cutPictureFrames <= 0 || s.cutNoVerify || s.benchWarmup < 1)) { failure = MotionViewArgument + " needs the frame window: " + CutPictureFramesArgument + " above 0, no " + CutNoVerifyArgument + ", and a warmup of at least 1 frame"; return false; }
            if (s.bench != null) s.benchSeries = new System.Collections.Generic.List<BenchCondition> { new BenchCondition { route = s.bench, bodies = s.benchBodies } };
            settings = s;
            return true;
        }

        public string Describe()
            => "directory " + directory + "; subjects " + (small && npc ? "small+npc" : small ? "small" : "npc") + "; draw " + draw + " (" + drawFrames + " frames a window); picture " + picture + "; keep open " + keepOpen
               + "; edges right " + edgeRight.ToString("F2", CultureInfo.InvariantCulture) + " m, left " + edgeLeft.ToString("F2", CultureInfo.InvariantCulture) + " m"
               + (IsBench ? "; BENCH series " + SeriesText + " (" + benchFrames + " measured frames after " + benchWarmup + " warmup each; load " + benchLoad + " NPCs)" : "")
               + (IsMotionCheck ? "; MOTION CHECK: step " + motionCheckStepCm.ToString("F2", CultureInfo.InvariantCulture) + " cm a frame, " + motionCheckFrames + " frames a move, " + motionCheckHold + " frames a hold" : "")
               + (motionView ? "; MOTION VIEW: the cut bench's eye follows the check's sequence from the first request (step " + motionCheckStepCm.ToString("F2", CultureInfo.InvariantCulture) + " cm a frame, " + motionCheckFrames + " frames a move, " + motionCheckHold + " frames a hold)" : "")
               + (warm != "none" ? "; WARM before the bench: " + warm : "")
               + (IsCutBench ? "; CUT BENCH " + cutBodies + " bodies, stagger " + cutStagger + " frames, " + cutRounds + " rounds (" + benchFrames + " frames after the requests, " + benchWarmup + " warmup; load " + benchLoad + " NPCs" + (cutNoVerify ? "; PERFORMANCE RUN: no picture, no readback" : "; verification: pictures and readback" + (cutPictureFrames > 0 ? ", a picture of every frame pre..+" + cutPictureFrames : "")) + "; shadows " + shadows + "; cut plane " + cutPlane + (holdCpuReady > 0 ? "; HOLD cpu ready " + holdCpuReady + " frames (diagnosis)" : "") + (cutSurfaceDebug ? "; cut surface DEBUG colours" : "") + (capsOff ? "; stencil caps WITHHELD" : "") + (motionCapture ? "; MOTION capture of the pictured frames" : "") + ")" : "");
    }

    /// <summary>
    /// The capture-method check's view sequence (MOTION-CAPTURE-1): the fixed eye moved along the world's x by the frame's
    /// INDEX in the sequence -- never by a delta time, and never accumulated -- so that LateUpdate and onBeforeRender of one
    /// frame compute the same target and no frame adds the step twice. Index 0 is the sequence's first frame.
    /// hold (the history settles) / right by the step a frame / hold / left by the step a frame / hold; after the sequence, 0.
    /// </summary>
    public static class SkinBakeProbeMotionCheck
    {
        public static int Length(int hold, int frames) => 3 * hold + 2 * frames;

        public static string PhaseOf(int index, int hold, int frames)
        {
            if (index < 0) return "before";
            if (index < hold) return "hold1";
            if (index < hold + frames) return "right";
            if (index < 2 * hold + frames) return "hold2";
            if (index < 2 * hold + 2 * frames) return "left";
            if (index < 3 * hold + 2 * frames) return "hold3";
            return "after";
        }

        /// <summary>The eye's x offset (metres) at the frame of the given index: the move's k-th frame has moved k steps.</summary>
        public static float OffsetX(int index, int hold, int frames, float stepMetres)
        {
            if (index < hold) return 0f;
            if (index < hold + frames) return (index - hold + 1) * stepMetres;
            if (index < 2 * hold + frames) return frames * stepMetres;
            if (index < 2 * hold + 2 * frames) return (frames - (index - 2 * hold - frames + 1)) * stepMetres;
            return 0f;
        }

        /// <summary>Which indices are captured: the last two frames of the first hold, every moving frame, the first two and the last one of the middle hold, the first three of the last hold.</summary>
        public static bool Wanted(int index, int hold, int frames)
        {
            string phase = PhaseOf(index, hold, frames);
            switch (phase)
            {
                case "hold1": return index >= hold - 2;
                case "right": case "left": return true;
                case "hold2": { int k = index - hold - frames; return k < 2 || k == hold - 1; }
                case "hold3": return index - 2 * hold - 2 * frames < 3;
                default: return false;
            }
        }
    }

    /// <summary>
    /// The cut bench's plane (PLANE-1): made once at the hit from the renderer's placement and its local bounds centre, in the
    /// renderer's frame, as <see cref="Zantetsu.PhysicsCut.VpPreparedCharacterCut.TryCut"/> takes it. Legacy is the plane
    /// every earlier cut bench used, unchanged; horizontal is the world's horizontal plane through the centre's world point,
    /// the world's up its positive side, converted to the renderer's frame by <see cref="VpCutPlane.TryWorldToGeometryLocal"/>
    /// (the distance term converted with the normal; a refusal is reported, never replaced by another plane).
    /// </summary>
    public static class SkinBakeProbeCutPlane
    {
        public const string Legacy = "legacy", Horizontal = "horizontal";

        public static bool TryMake(string setting, Matrix4x4 rendererLocalToWorld, Vector3 localCentre, out float4 localPlane, out float4 worldPlane, out float3 anchor, out string failure)
        {
            failure = null;
            localPlane = default;
            worldPlane = default;
            anchor = (float3)rendererLocalToWorld.MultiplyPoint3x4(localCentre);
            if (string.Equals(setting, Horizontal, StringComparison.Ordinal))
            {
                var n = new float3(0f, 1f, 0f);
                worldPlane = new float4(n, -math.dot(n, anchor));
                if (!VpCutPlane.TryWorldToGeometryLocal(worldPlane, rendererLocalToWorld, out localPlane))
                {
                    failure = "VpCutPlane.TryWorldToGeometryLocal refused the renderer's placement for the world plane " + Plane(worldPlane) + " (not finite, not affine, or degenerate in the normal's direction)";
                    localPlane = default;
                    return false;
                }

                return true;
            }

            if (string.IsNullOrEmpty(setting) || string.Equals(setting, Legacy, StringComparison.Ordinal))
            {
                localPlane = new float4(0f, 1f, 0f, -localCentre.y);
                // The same plane in world, for the record only: legacy cuts with the local plane whatever this says.
                if (!VpCutPlane.TryGeometryLocalToWorld(localPlane, rendererLocalToWorld, out worldPlane)) worldPlane = new float4(float.NaN, float.NaN, float.NaN, float.NaN);
                return true;
            }

            failure = "unknown cut plane setting '" + setting + "'";
            return false;
        }

        /// <summary>
        /// Whether the local plane separates the world's up from the world's down through the anchor: the anchor itself on the
        /// plane, a point 0.5 m above it on the positive side, 0.5 m below on the negative side -- each taken to the renderer's
        /// frame by the inverse placement and evaluated there. Decided by these numbers, not by an axis name.
        /// </summary>
        public static bool CheckWorldUpSides(float4 localPlane, Matrix4x4 rendererLocalToWorld, float3 anchor, out string detail)
        {
            Matrix4x4 inverse = rendererLocalToWorld.inverse;
            float Eval(float3 world) { float3 local = (float3)inverse.MultiplyPoint3x4((Vector3)world); return math.dot(localPlane.xyz, local) + localPlane.w; }
            float on = Eval(anchor), up = Eval(anchor + new float3(0f, 0.5f, 0f)), down = Eval(anchor - new float3(0f, 0.5f, 0f));
            float tolerance = 1e-4f * math.max(1f, math.length(anchor));
            bool ok = math.abs(on) <= tolerance && up > tolerance && down < -tolerance && math.all(math.isfinite(new float3(on, up, down)));
            detail = "on the anchor " + on.ToString("G6", CultureInfo.InvariantCulture) + ", 0.5 m above " + up.ToString("G6", CultureInfo.InvariantCulture) + ", 0.5 m below " + down.ToString("G6", CultureInfo.InvariantCulture) + " (tolerance " + tolerance.ToString("G3", CultureInfo.InvariantCulture) + ")";
            return ok;
        }

        public static string Plane(float4 p) => "(" + p.x.ToString("F6", CultureInfo.InvariantCulture) + ", " + p.y.ToString("F6", CultureInfo.InvariantCulture) + ", " + p.z.ToString("F6", CultureInfo.InvariantCulture) + ", " + p.w.ToString("F6", CultureInfo.InvariantCulture) + ")";
        public static string Point(float3 p) => "(" + p.x.ToString("F4", CultureInfo.InvariantCulture) + ", " + p.y.ToString("F4", CultureInfo.InvariantCulture) + ", " + p.z.ToString("F4", CultureInfo.InvariantCulture) + ")";
        public static string Matrix(Matrix4x4 m)
        {
            var t = new System.Text.StringBuilder("[");
            for (int r = 0; r < 4; r++) { if (r > 0) t.Append("; "); for (int c = 0; c < 4; c++) { if (c > 0) t.Append(' '); t.Append(m[r, c].ToString("F5", CultureInfo.InvariantCulture)); } }
            return t.Append(']').ToString();
        }
    }
}
