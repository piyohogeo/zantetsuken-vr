using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Zantetsu.Core.Animation;
using Zantetsu.Core.MobPlan;
using Zantetsu.Core.Statistics;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The NPC skin benchmark (DESIGN 3.4, npc-skin-bench): a crowd of uncut skinned individuals, placed in rows before
    /// the eye and posed deterministically, drawn by the skin route the launch names -- the SkinnedMeshRenderers
    /// (<c>-zantetsuSkinUnity</c>) or the 24-byte indirect skin draw (<c>-zantetsuSkinDraw</c>) -- and measured: the
    /// whole frame's GPU time, the main and render threads, the present wait, with their medians, 95th percentiles and
    /// valid sample counts, beside what was drawn (individuals, models, vertices, bones, pose updates, palette transfers,
    /// issues, dispatches). One launch is one condition: count, pose (stopped or updated every frame) and shadows (off
    /// or four cascades); the two routes are two launches of the same build.
    /// <para>
    /// **What does not run.** No cut (the prepared cuts stand ready and are never asked), no crowd plan, no refill, no
    /// input replay, no katana, no bone level of detail (the slots have none here): the individuals' poses are this
    /// runner's, by step number and a fixed step, never by the clock. The physics step runs over a world without bodies.
    /// </para>
    /// <para>
    /// **Preparation and measurement are apart.** The slots are made, prepared and activated, the pipelines drawn once
    /// and the buffers transferred in the warm-up; the measurement begins only then, and a growth, a buffer replacement
    /// or a refusal inside it is recorded and makes the run invalid as a sample. A requested skin draw that fell back
    /// to the renderers, or any registration, selection, transfer or issue refusal, is recorded as NOT a valid
    /// measurement of the skin draw: the run completes and says so (exit code 23).
    /// </para>
    /// <para>
    /// **GPU breakdown.** The whole-frame GPU time comes from FrameTimingManager (and the XR display's app GPU time,
    /// another definition, kept apart). No breakdown by pass is measured here: a CPU recorder's time of a dispatch is
    /// never reported as GPU time. A Profiler binary log (<see cref="NpcSkinBenchSettings.ProfilerArgument"/>) is a
    /// diagnosis run, not a sample.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(-60)]
    public sealed class NpcSkinBenchRunner : MonoBehaviour
    {
        public const string Prefix = "NPC SKIN BENCH: ";
        public const int ExitValid = 0;
        /// <summary>The run completed and was measured, but not as a valid sample of the route asked for (fallback or refusals).</summary>
        public const int ExitInvalidRoute = 23;
        public const int ExitFailed = 14;

        [Tooltip("The scene's cut world: its skin draw is the route measured, its drawing's native route gives the issue counters.")]
        [SerializeField] private CutWorldRoot world;
        [Tooltip("The slot templates, one a model family: an inactive holder with its SandboxNpcCharacter and the character root beneath it.")]
        [SerializeField] private GameObject[] templates = Array.Empty<GameObject>();
        [Tooltip("The crowd's assets: the pose table every slot is configured and posed with.")]
        [SerializeField] private MobPlanAssets assets;
        [SerializeField] private Light mainLight;
        [Tooltip("The XR rig (the XR Origin): placed at the origin facing +Z, so that the eye looks at the rows.")]
        [SerializeField] private Transform rig;
        [SerializeField] private Camera eye;

        private static NpcSkinBenchSettings s_settings;
        private static bool s_armed;

        /// <summary>The PC entry (SandboxScenario): the runner in the scene is armed with the launch's settings; a failure in words.</summary>
        public static string StartForScenario(string scene)
        {
            NpcSkinBenchRunner runner = FindAnyObjectByType<NpcSkinBenchRunner>(FindObjectsInactive.Include);
            if (runner == null) return "the scene " + scene + " has no NPC skin bench runner";
            if (!NpcSkinBenchSettings.TryRead(Environment.GetCommandLineArgs(), out NpcSkinBenchSettings settings, out string failure))
                return failure ?? NpcSkinBenchSettings.Argument + " <directory> is needed";
            Arm(settings);
            return null;
        }

        /// <summary>Arms the scene's runner with settings (the Quest's entry gives its configuration's): it runs from its Start.</summary>
        public static void Arm(NpcSkinBenchSettings settings)
        {
            s_settings = settings ?? throw new ArgumentNullException(nameof(settings));
            s_armed = true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_settings = null;
            s_armed = false;
        }

        /// <summary>For a test: the runner over a world and templates made by the test, with settings given directly.</summary>
        public void ConfigureForTest(CutWorldRoot targetWorld, GameObject[] slotTemplates, TextAsset poseTable, Light light, Transform xrRig, Camera camera, NpcSkinBenchSettings settings)
        {
            world = targetWorld; templates = slotTemplates; _clipForTest = poseTable; mainLight = light; rig = xrRig; eye = camera;
            _settings = settings;
            _armedForTest = true;
        }

        private TextAsset _clipForTest;
        private bool _armedForTest;
        private NpcSkinBenchSettings _settings;

        public enum Phase { Waiting, Preparing, Activating, WarmingUp, Settling, Measuring, Ending, Done, Failed }

        public Phase Current { get; private set; } = Phase.Waiting;
        public string Failure { get; private set; }
        public int ExitCode { get; private set; } = -1;
        /// <summary>The route asked for by the launch: "SMR" or "SKIN24".</summary>
        public string RequestedRoute { get; private set; }
        /// <summary>The world's route: "RENDERERS", "SKIN24-NATIVE" or "SKIN24-UNITY".</summary>
        public string ActualRoute { get; private set; }
        /// <summary>Whether the run is a valid sample of the route asked for (no fallback, no refusal, no growth or replacement in the measurement).</summary>
        public bool Valid { get; private set; }
        public readonly List<string> Invalidities = new List<string>();
        /// <summary>Whether the measurement holds as one side of a comparison (SKIN-BENCH-2 §2): the eye within the tolerances, the rows in view. Apart from <see cref="Valid"/>.</summary>
        public bool ComparisonValid { get; private set; }
        public readonly List<string> ComparisonIssues = new List<string>();
        public int Step { get; private set; }
        /// <summary>The step range of the measurement (the pose clock is resynchronised to 0 after the warm-up).</summary>
        public int MeasureStepStart { get; private set; }
        public int MeasureStepEnd { get; private set; }
        public IReadOnlyList<NpcSkinBenchRow> Rows => _rows;
        public IReadOnlyList<SandboxNpcCharacter> Slots => _slots;
        /// <summary>The slot objects this made (for a test's cleanup).</summary>
        public IReadOnlyList<GameObject> Made => _made;
        public long PalettesGatheredInMeasurement { get; private set; }
        public int PosesAppliedInMeasurement { get; private set; }

        private readonly List<SandboxNpcCharacter> _slots = new List<SandboxNpcCharacter>();
        private readonly List<PoseTablePlayer> _poses = new List<PoseTablePlayer>();
        private readonly List<GameObject> _made = new List<GameObject>();
        private readonly Dictionary<string, SandboxNpcCharacter.SlotShare> _shares = new Dictionary<string, SandboxNpcCharacter.SlotShare>(StringComparer.Ordinal);
        private readonly List<NpcSkinBenchRow> _rows = new List<NpcSkinBenchRow>(8192);
        private NpcSkinBenchPlan.Placement[] _plan;
        private string[] _families;
        private PoseTable _clip;
        private TextAsset _clipAsset;
        private FrameTimeSampler _sampler;
        private ProfilerRecorder _playerLoop, _gfxWait, _gcAlloc;
        private ProfilerRecorder _poseEvaluate, _poseApply, _skinFrame, _skinPalette, _skinGather, _skinUpload, _skinUploadPalette;
        private bool _fixedViewOn;
        private Vector3 _eyeTargetPosition, _rigBefore;
        private Quaternion _eyeTargetRotation, _rigRotationBefore;
        private float _rigMoveThisFrame, _rigTurnThisFrame, _rigMaxMove, _rigMaxTurn;
        private string _captureRecord;
        private CutWorldCameraDrawing _drawing;
        private RenderPipelineAsset _pipelineBefore, _pipelineOwn;
        private LightShadows _lightShadowsBefore;
        private readonly Dictionary<string, string> _drawSettings = new Dictionary<string, string>();
        private int _posesAppliedThisFrame;
        private double _phaseBegan;
        private Counters _last, _measureStart, _measureEnd;
        private int _pipelinesAtMeasureStart = -1, _growthAtMeasureStart, _retiredAtMeasureStart;
        private DateTime _measureWallStart, _measureWallEnd;
        private Vector3 _eyeAtStart;
        private float _eyeMaxMove, _eyeMaxTurn;
        private Quaternion _eyeRotationAtStart;
        /// <summary>The tracked eye's pose at the resynchronisation, before any fixed view (the head's own pose: evidence of where it looked).</summary>
        private Vector3 _headAtSettle;
        private Quaternion _headRotationAtSettle = Quaternion.identity;
        private static readonly Vector3 PlanEye = new Vector3(0f, NpcSkinBenchPlan.EyeHeight, 0f);
        private bool _profiling;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private struct Counters
        {
            public long palettes, paletteBytes, paletteWrites, paletteBytesExtra, skinBodies, skinCasters, skinMotion, bodies, casters, motion, selections, dispatches, selectionsRefused, uploadRefusals, individualsHidden;
            public long jobsScheduled, lateSchedules, lateRegathers, jobFailures, completedEarly, uncollectedJobs, belowThresholdFrames;
            public long refused, ringFull, pipelineFailures, xrNotDrawn;
            public int nativeRefusals, growth, retired, pipelines;
        }

        private Counters Read()
        {
            var c = new Counters();
            VpSkinDisplay skins = world != null ? world.SkinDisplay : null;
            if (skins != null && !skins.IsDisposed)
            {
                c.palettes = skins.PalettesGathered; c.paletteBytes = skins.PaletteBytesWritten; c.paletteWrites = skins.PaletteWrites; c.paletteBytesExtra = skins.PaletteBytesExtra;
                c.selections = skins.SelectionsIssued; c.dispatches = skins.BoundsDispatches;
                c.selectionsRefused = skins.SelectionsRefused; c.uploadRefusals = skins.UploadRefusals; c.individualsHidden = skins.IndividualsHidden;
                c.growth = skins.Geometry.GrowthCount; c.retired = skins.Geometry.RetiredCount;
                c.jobsScheduled = skins.JobsScheduled; c.lateSchedules = skins.LateSchedules; c.lateRegathers = skins.LateRegathers; c.jobFailures = skins.JobFailures; c.completedEarly = skins.CompletedEarly;
                c.uncollectedJobs = skins.UncollectedJobs; c.belowThresholdFrames = skins.BelowThresholdFrames;
            }

            VpNativeDrawRoute route = _drawing != null ? _drawing.NativeRoute : null;
            if (route != null)
            {
                c.skinBodies = route.SkinBodiesIssued; c.skinCasters = route.SkinCastersIssued; c.skinMotion = route.SkinMotionIssued;
                c.bodies = route.BodiesIssued; c.casters = route.CastersIssued; c.motion = route.MotionIssued;
                c.refused = route.Refused; c.ringFull = route.RingFull; c.pipelineFailures = route.PipelineFailures; c.xrNotDrawn = route.XrFramesNotDrawn; c.pipelines = route.PipelineCount;
            }

            if (_drawing != null) c.nativeRefusals = _drawing.NativeRefusals;
            return c;
        }

        private IEnumerator Start()
        {
            if (_armedForTest) { }
            else if (!s_armed) { Debug.Log(Prefix + "not started: the launch's scenario is not npc-skin-bench (the runner stays idle)"); enabled = false; yield break; }
            else _settings = s_settings;
            yield return Run();
        }

        private IEnumerator Run()
        {
            Current = Phase.Preparing;
            string invalid = _settings.Validate();
            if (invalid != null) { Fail(invalid); yield break; }
            if (world == null || eye == null || rig == null || templates == null || templates.Length == 0) { Fail("the scene gives the runner no world, eye, rig or templates"); yield break; }
            VpSkinDrawRequest request = VpSkinDrawSetup.Request;
            if (request == VpSkinDrawRequest.Default)
            {
                Fail("the launch names no skin route: give " + VpSkinDrawSetup.RenderersArgument + " (the SkinnedMeshRenderers) or " + VpSkinDrawSetup.SkinDrawArgument + " (the skin draw); each route is a launch of its own");
                yield break;
            }

            RequestedRoute = request == VpSkinDrawRequest.SkinDraw ? "SKIN24" : "SMR";
            VpSkinDisplay.PaletteJoinGapMatrices = _settings.paletteJoinGap;   // the diagnosis of SKIN-BENCH-2 section 6; 0 is the product's way
            Log("settings: " + _settings.Describe() + "; skin route asked: " + RequestedRoute + " (" + (request == VpSkinDrawRequest.SkinDraw ? VpSkinDrawSetup.SkinDrawArgument : VpSkinDrawSetup.RenderersArgument) + ")");
            if (!string.IsNullOrEmpty(_settings.directory)) Directory.CreateDirectory(_settings.directory);

            // The world, ready: its route lines are written at its build.
            float worldBy = Time.realtimeSinceStartup + 60f;
            while (!world.IsReady && Time.realtimeSinceStartup < worldBy) yield return null;
            if (!world.IsReady) { Fail("the world was not ready within 60 s"); yield break; }
            _drawing = world.GetComponent<CutWorldCameraDrawing>();
            ActualRoute = world.SkinRoute ?? "?";
            Log("world: body route " + world.BodyRoute + "; skin route " + ActualRoute + " -- " + world.SkinRouteReason);

            // The rendering of the condition: the pipeline's shadows, the light's shadows; what is in effect, recorded.
            try { ApplyRendering(); }
            catch (Exception e) { Fail("the rendering could not be set: " + e.Message); yield break; }

            // The plan: the families (the first ones by name), the clip, the rows.
            var available = new List<string>();
            foreach (GameObject t in templates)
            {
                SandboxNpcCharacter npc = t != null ? t.GetComponentInChildren<SandboxNpcCharacter>(true) : null;
                if (npc != null) available.Add(npc.Family);
            }

            _families = NpcSkinBenchPlan.ChooseFamilies(available, _settings.families);
            if (_families.Length == 0) { Fail("the templates name no family"); yield break; }
            if (_families.Length < _settings.families) Log("families: " + _families.Length + " available, " + _settings.families + " asked: the available ones are used");
            _clipAsset = _clipForTest != null ? _clipForTest : assets != null ? assets.initialPose : null;
            string clipError = null;
            if (_clipAsset == null || !PoseTable.TryRead(_clipAsset.bytes, out _clip, out clipError)) { Fail("the pose table could not be read: " + (clipError ?? "no table")); yield break; }
            _plan = NpcSkinBenchPlan.Place(_settings.count, _families.Length, _clip.DurationSeconds);
            float verticalFov = eye.fieldOfView;
            float horizontalFov = 2f * Mathf.Atan(Mathf.Tan(0.5f * verticalFov * Mathf.Deg2Rad) * Mathf.Max(1f, eye.aspect)) * Mathf.Rad2Deg;
            bool inView = NpcSkinBenchPlan.AllInView(_plan, Mathf.Max(horizontalFov, NpcSkinBenchPlan.DefaultHorizontalFovDegrees), verticalFov, NpcSkinBenchPlan.EyeHeight, 1.9f, 2f, out string outside);
            Log("plan: " + _plan.Length + " individuals of " + _families.Length + " families [" + string.Join(" ", _families) + "], clip " + _clipAsset.name + " " + _clip.DurationSeconds.ToString("0.###", Inv)
                + " s, rows to " + NpcSkinBenchPlan.FarthestDistance(_plan).ToString("0.#", Inv) + " m, eye fov " + verticalFov.ToString("0.#", Inv) + " x " + horizontalFov.ToString("0.#", Inv)
                + " deg; all in view by the plan's rule: " + inView + (outside != null ? " (" + outside + ")" : ""));
            if (!inView) ComparisonIssues.Add("placement outside the view rule: " + outside);

            // The rig at the origin, facing +Z: the eye stands where the plan's eye is (its tracked pose is the runtime's).
            rig.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            // The slots, a few a frame.
            var templateOf = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            foreach (GameObject t in templates)
            {
                SandboxNpcCharacter npc = t != null ? t.GetComponentInChildren<SandboxNpcCharacter>(true) : null;
                if (npc != null && !templateOf.ContainsKey(npc.Family)) templateOf[npc.Family] = t;
            }

            for (int i = 0; i < _plan.Length; i++)
            {
                string family = _families[_plan[i].family];
                GameObject holder = Instantiate(templateOf[family]);
                holder.name = "bench slot " + i + " " + family;
                _made.Add(holder);
                SandboxNpcCharacter npc = holder.GetComponentInChildren<SandboxNpcCharacter>(true);
                PoseTablePlayer pose = npc.CharacterRoot.GetComponent<PoseTablePlayer>();
                pose.Configure(_clipAsset, pose.ModelRoot, 0.0, true);
                pose.Managed = true;   // posed by this runner alone: the step's time, never the clock's
                double phase = _plan[i].phase;
                PoseTable clip = _clip;
                pose.PlanSource = (double target, out PoseTable table, out double source) =>
                {
                    table = clip;
                    source = clip.ResolveSourceTime(NpcSkinBenchPlan.PoseTime(Step, _settings.stepSeconds, phase));
                    return true;
                };
                if (!_shares.TryGetValue(family, out SandboxNpcCharacter.SlotShare share)) _shares[family] = share = new SandboxNpcCharacter.SlotShare();
                npc.PrepareAsSlot(share);
                npc.CharacterRoot.SetActive(true);
                holder.SetActive(true);
                npc.enabled = true;
                _slots.Add(npc);
                _poses.Add(pose);
                if ((i & 15) == 15) yield return null;
            }

            float preparedBy = Time.realtimeSinceStartup + 180f;
            while (_slots.Any(s => !s.IsPrepared && s.Failure == null) && Time.realtimeSinceStartup < preparedBy) yield return null;
            var broken = _slots.Where(s => s.Failure != null).ToList();
            if (broken.Count > 0) { Fail(broken.Count + " slots failed to prepare: " + string.Join("; ", broken.Take(5).Select(s => s.Family + ": " + s.Failure))); yield break; }
            if (_slots.Any(s => !s.IsPrepared)) { Fail(_slots.Count(s => !s.IsPrepared) + " slots were not prepared within 180 s"); yield break; }
            Log("prepared: " + _slots.Count + " slots in " + _shares.Count + " shares");

            // Placed, then activated: the pose of step 0 is applied once at the activation (PoseTablePlayer.ApplyNow).
            Current = Phase.Activating;
            Step = 0;
            for (int i = 0; i < _slots.Count; i++)
            {
                _slots[i].CharacterRoot.transform.SetPositionAndRotation(_plan[i].position, Quaternion.Euler(0f, _plan[i].yaw, 0f));
                if (!_slots[i].Activate()) { Fail("slot " + i + " (" + _slots[i].Family + ") could not be activated: " + (_slots[i].Failure ?? "not prepared")); yield break; }
                if ((i & 31) == 31) yield return null;
            }

            yield return null;
            CheckRoute();
            Log("activated: " + _slots.Count + "; route asked " + RequestedRoute + ", actual " + ActualRoute + "; valid so far: " + (Invalidities.Count == 0) + (Invalidities.Count > 0 ? " [" + string.Join("; ", Invalidities) + "]" : ""));

            // The samplers.
            _sampler = new FrameTimeSampler { XrDisplay = XrDisplay() };
            _playerLoop = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "PlayerLoop");
            _gfxWait = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Gfx.WaitForPresentOnGfxThread");
            _gcAlloc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            // The script markers of the pose and the skin draw, every sample of a frame summed (SKIN-BENCH-2 section 5).
            _poseEvaluate = Marker("Zantetsu.PoseTable.Evaluate"); _poseApply = Marker("Zantetsu.PoseTable.Apply");
            _skinFrame = Marker("Zantetsu.SkinDraw.Frame"); _skinPalette = Marker("Zantetsu.SkinDraw.Palette"); _skinGather = Marker("Zantetsu.SkinDraw.Palette.Gather");
            _skinUpload = Marker("Zantetsu.SkinDraw.Upload"); _skinUploadPalette = Marker("Zantetsu.SkinDraw.Upload.Palette");
            _last = Read();

            // The warm-up: the pipelines are made at the first draws, the buffers transferred; the rows are kept but marked.
            Current = Phase.WarmingUp;
            _phaseBegan = Time.realtimeSinceStartupAsDouble;
            while (Time.realtimeSinceStartupAsDouble - _phaseBegan < _settings.warmupSeconds) yield return null;

            // The pose clock resynchronised: step 0 again, every pose applied once, so that every launch measures the
            // same step range whatever its warm-up's frame rate was (SKIN-BENCH-2 section 2); then the settle span, in
            // which the history and the first palettes after the jump settle, not measured. The fixed view holds the
            // PLAN's eye from here on -- not the head's pose of the moment: a Quest series (2026-10-30, s3) held heads that
            // looked down, 0 of 64 visible, and the view rule above was judged for the plan's eye. The head's own pose is
            // kept for the record.
            Current = Phase.Settling;
            Step = 0;
            for (int i = 0; i < _poses.Count; i++) _poses[i].ApplyNow(null, 0);
            _headAtSettle = eye.transform.position; _headRotationAtSettle = eye.transform.rotation;
            if (_settings.fixedView)
            {
                _eyeTargetPosition = PlanEye; _eyeTargetRotation = Quaternion.identity;
                _fixedViewOn = true;
                Application.onBeforeRender += FixView;
                FixView();
                Log("fixed view: the eye held at the plan's eye " + PlanEye.ToString("F3") + " facing +Z (the rig counter-moves every frame); the tracked head stood at "
                    + _headAtSettle.ToString("F3") + " yaw " + YawOf(_headRotationAtSettle).ToString("0.00", Inv) + " pitch " + PitchOf(_headRotationAtSettle).ToString("0.00", Inv) + " deg");
            }

            for (int settled = 0; settled < _settings.settleFrames; settled++) yield return null;

            // The measurement: the same frames and steps in every launch when given as frames.
            _measureStart = Read();
            _pipelinesAtMeasureStart = _measureStart.pipelines; _growthAtMeasureStart = _measureStart.growth; _retiredAtMeasureStart = _measureStart.retired;
            _measureWallStart = DateTime.UtcNow;
            _eyeAtStart = eye.transform.position; _eyeRotationAtStart = eye.transform.rotation; _eyeMaxMove = 0f; _eyeMaxTurn = 0f; _rigMaxMove = 0f; _rigMaxTurn = 0f;
            PalettesGatheredInMeasurement = 0; PosesAppliedInMeasurement = 0;
            MeasureStepStart = Step + 1;
            if (!string.IsNullOrEmpty(_settings.profilerFile)) StartProfiler();
            Current = Phase.Measuring;
            _phaseBegan = Time.realtimeSinceStartupAsDouble;
            if (_settings.measureFrames > 0)
            {
                for (int measured = 0; measured < _settings.measureFrames; measured++) yield return null;
            }
            else
            {
                while (Time.realtimeSinceStartupAsDouble - _phaseBegan < _settings.measureSeconds) yield return null;
            }

            Current = Phase.Ending;
            MeasureStepEnd = Step;
            _measureEnd = Read();
            _measureWallEnd = DateTime.UtcNow;
            if (_profiling) StopProfiler();
            PalettesGatheredInMeasurement = _measureEnd.palettes - _measureStart.palettes;

            // The diagnoses after the measurement: the GPU capture (an external profiler attached), the selection's
            // visible count (a synchronous readback), the picture.
            if (_settings.captureFrames > 0)
            {
                bool attached = UnityEngine.Experimental.Rendering.ExternalGPUProfiler.IsAttached();
                if (attached)
                {
                    UnityEngine.Experimental.Rendering.ExternalGPUProfiler.BeginGPUCapture();
                    for (int f = 0; f < _settings.captureFrames; f++) yield return null;
                    UnityEngine.Experimental.Rendering.ExternalGPUProfiler.EndGPUCapture();
                    yield return null;
                    _captureRecord = "captured " + _settings.captureFrames + " frames after the measurement (steps " + (MeasureStepEnd + 1) + ".." + Step + ") by the attached external GPU profiler; not a performance sample";
                }
                else _captureRecord = "capture asked for " + _settings.captureFrames + " frames but no external GPU profiler is attached: nothing captured";
                Log(_captureRecord);
            }

            int skinVisible = -1;
            if (world.DrawsSkins && world.SkinDisplay.TryReadVisibleInstances(eye, out int visible)) skinVisible = visible;
            int renderersVisible = _slots.Count(s => s.Renderer != null && s.Renderer.enabled && s.Renderer.isVisible);
            if (_settings.picture && !string.IsNullOrEmpty(_settings.directory))
            {
                // On Android ScreenCapture writes under the persistent data path and takes a relative name: the run's
                // directory is given relative to it there (an absolute path wrote nothing on the Quest, 2026-10-30).
                string picture = Path.Combine(_settings.directory, "picture-" + RequestedRoute.ToLowerInvariant() + ".png");
                string persistent = Application.persistentDataPath;
                if (Application.platform == RuntimePlatform.Android && picture.StartsWith(persistent, StringComparison.Ordinal))
                    picture = picture.Substring(persistent.Length).TrimStart('/', '\\');
                ScreenCapture.CaptureScreenshot(picture);
                yield return null;
                yield return null;
                yield return null;
            }

            Judge(skinVisible, renderersVisible);
            string result = null;
            try { result = WriteResults(skinVisible, renderersVisible); }
            catch (Exception e) { Debug.LogError(Prefix + "the results could not be written: " + e); }
            Log("done: " + (result ?? "(no result)"));
            Current = Phase.Done;
            ExitCode = Valid ? ExitValid : ExitInvalidRoute;
            RestoreRendering();
            if (!_settings.keepOpen && !_armedForTest) Application.Quit(ExitCode);
        }

        private void Update()
        {
            if (Current != Phase.WarmingUp && Current != Phase.Settling && Current != Phase.Measuring) return;
            Step++;
            _posesAppliedThisFrame = 0;
            if (_settings.pose == NpcSkinBenchPose.Update)
            {
                // The first so many individuals (SKIN-BENCH-3: the update count; every one by default): the others stand.
                int limit = UpdatedIndividuals;
                for (int i = 0; i < limit; i++) if (_poses[i].ApplyNow(null, 0)) _posesAppliedThisFrame++;
            }

            if (Current == Phase.Measuring) PosesAppliedInMeasurement += _posesAppliedThisFrame;
        }

        private void LateUpdate()
        {
            if (Current != Phase.WarmingUp && Current != Phase.Settling && Current != Phase.Measuring) return;
            FixView();   // the Update-phase tracking countered here too; onBeforeRender counters the late one
            Counters now = Read();
            FrameTimeSample sample = _sampler.Sample();
            Transform e = eye.transform;
            Vector3 forward = e.forward;
            var row = new NpcSkinBenchRow
            {
                frame = Time.frameCount, phase = Current == Phase.Measuring ? 1 : Current == Phase.Settling ? 2 : 0, step = Step, wallUtcTicks = DateTime.UtcNow.Ticks,
                realtimeSeconds = Time.realtimeSinceStartupAsDouble, unscaledDeltaSeconds = Time.unscaledDeltaTime, ftFrameStart = sample.FrameStart,
                hasMain = sample.HasMain, ftMainMs = sample.MainMs, hasRender = sample.HasRender, ftRenderMs = sample.RenderMs, hasGpu = sample.HasGpu, ftGpuMs = sample.GpuMs,
                hasPresentWait = sample.HasPresentWait, ftPresentWaitMs = sample.PresentWaitMs, hasXrGpu = sample.HasGpuXr, xrAppGpuMs = sample.GpuXrMs,
                playerLoopMs = _playerLoop.Valid ? _playerLoop.LastValue / 1e6 : double.NaN, gfxPresentWaitMs = _gfxWait.Valid ? _gfxWait.LastValue / 1e6 : double.NaN,
                poseEvaluateMs = PoseSpanMs(true), poseApplyMs = PoseSpanMs(false), skinFrameMs = SkinSpanMs(0), skinPaletteMs = SkinSpanMs(1), skinGatherMs = SkinSpanMs(2),
                skinUploadMs = SkinSpanMs(3), skinUploadPaletteMs = SkinSpanMs(4),
                skinPrepareMs = SkinSpanMs(5), skinScheduleMs = SkinSpanMs(6), skinCompleteWaitMs = SkinSpanMs(7), skinPublishMs = SkinSpanMs(8),
                skinJobs = SkinJobsLastFrame(out int jobIndividuals), skinJobIndividuals = jobIndividuals, skinLateRegathers = now.lateRegathers - _last.lateRegathers,
                gcAllocatedBytes = _gcAlloc.Valid ? _gcAlloc.LastValue : -1, posesApplied = _posesAppliedThisFrame,
                palettesGathered = now.palettes - _last.palettes, paletteWrites = now.paletteWrites - _last.paletteWrites, paletteBytes = now.paletteBytes - _last.paletteBytes,
                paletteBytesExtra = now.paletteBytesExtra - _last.paletteBytesExtra,
                skinBodiesIssued = now.skinBodies - _last.skinBodies, skinCastersIssued = now.skinCasters - _last.skinCasters, skinMotionIssued = now.skinMotion - _last.skinMotion,
                bodiesIssued = now.bodies - _last.bodies, castersIssued = now.casters - _last.casters, motionIssued = now.motion - _last.motion,
                selectionsIssued = now.selections - _last.selections, boundsDispatches = now.dispatches - _last.dispatches,
                visibleRenderers = RequestedRoute == "SMR" ? _slots.Count(s => s.Renderer != null && s.Renderer.isVisible) : -1,
                eyeX = e.position.x, eyeY = e.position.y, eyeZ = e.position.z, eyeYaw = Yaw(e), eyePitch = Pitch(e), rigMove = _rigMoveThisFrame, rigTurn = _rigTurnThisFrame,
            };
            _rows.Add(row);
            _last = now;
            _rigMoveThisFrame = 0f; _rigTurnThisFrame = 0f;
            if (Current == Phase.Measuring)
            {
                _eyeMaxMove = Mathf.Max(_eyeMaxMove, Vector3.Distance(e.position, _eyeAtStart));
                _eyeMaxTurn = Mathf.Max(_eyeMaxTurn, Quaternion.Angle(e.rotation, _eyeRotationAtStart));
                _rigMaxMove = Mathf.Max(_rigMaxMove, row.rigMove); _rigMaxTurn = Mathf.Max(_rigMaxTurn, row.rigTurn);
            }
        }

        // The marker's own handle, found by name among the available stats (as the city walk's skinning watch does: a
        // recorder made by category and name read a wrong or empty stat in the Player, SKIN-BENCH-2), every sample of the
        // frame summed, started at once. An invalid recorder (the marker not made yet) gives NaN cells.
        private static ProfilerRecorder Marker(string name)
        {
            var handles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
            Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
            foreach (Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle handle in handles)
            {
                if (Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(handle).Name != name) continue;
                return new ProfilerRecorder(handle, 1, ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately);
            }

            Debug.LogWarning(Prefix + "marker not available for recording: " + name);
            return default;
        }

        private static double Ms(ProfilerRecorder r) => r.Valid ? r.LastValue / 1e6 : double.NaN;

        // The pose players' spans of this frame (those applied this frame), summed: the code's own Stopwatch, not a recorder
        // (SKIN-BENCH-2 section 5: a recorder read 0 for the pose markers in the Player). Only in a Development Player.
        private double PoseSpanMs(bool evaluate)
        {
            if (!Debug.isDebugBuild && !Application.isEditor) return double.NaN;
            double seconds = 0.0;
            int frame = Time.frameCount;
            for (int i = 0; i < _poses.Count; i++)
            {
                PoseTablePlayer p = _poses[i];
                if (p.AppliedFrame != frame) continue;
                seconds += evaluate ? p.LastEvaluateSeconds : p.LastApplySeconds;
            }

            return seconds * 1000.0;
        }

        // The skin display's spans of its last UpdateFrame (the drawing runs it after this LateUpdate: the frame before's).
        private double SkinSpanMs(int which)
        {
            if (!Debug.isDebugBuild && !Application.isEditor) return double.NaN;
            VpSkinDisplay skins = world != null ? world.SkinDisplay : null;
            if (skins == null || skins.IsDisposed) return 0.0;
            switch (which)
            {
                case 0: return skins.LastFrameSeconds * 1000.0;
                case 1: return skins.LastPaletteSeconds * 1000.0;
                case 2: return skins.LastGatherSeconds * 1000.0;
                case 3: return skins.LastUploadSeconds * 1000.0;
                case 4: return skins.LastPaletteUploadSeconds * 1000.0;
                case 5: return skins.LastPrepareSeconds * 1000.0;
                case 6: return skins.LastScheduleSeconds * 1000.0;
                case 7: return skins.LastCompleteWaitSeconds * 1000.0;
                default: return skins.LastPublishSeconds * 1000.0;
            }
        }

        // Whether the display's last UpdateFrame published job results, and for how many individuals (SKIN-BENCH-3).
        private int SkinJobsLastFrame(out int individuals)
        {
            individuals = 0;
            VpSkinDisplay skins = world != null ? world.SkinDisplay : null;
            if (skins == null || skins.IsDisposed) return 0;
            individuals = skins.LastJobIndividuals;
            return skins.LastFrameUsedJobs ? 1 : 0;
        }

        /// <summary>How many individuals have their pose applied each frame under the update pose: the settings' update count, or every one.</summary>
        private int UpdatedIndividuals => _settings.updateCount < 0 ? _poses.Count : Math.Min(_settings.updateCount, _poses.Count);

        private static float Yaw(Transform t) { Vector3 f = t.forward; return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }

        private static float Pitch(Transform t) => -Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;

        private static float Roll(Transform t) { Vector3 u = t.up, r = t.right; return Mathf.Atan2(-r.y, u.y) * Mathf.Rad2Deg; }

        private static float YawOf(Quaternion q) { Vector3 f = q * Vector3.forward; return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; }

        private static float PitchOf(Quaternion q) => -Mathf.Asin(Mathf.Clamp((q * Vector3.forward).y, -1f, 1f)) * Mathf.Rad2Deg;

        private static float RollOf(Quaternion q) { Vector3 u = q * Vector3.up, r = q * Vector3.right; return Mathf.Atan2(-r.y, u.y) * Mathf.Rad2Deg; }

        // The fixed view (SKIN-BENCH-2 section 2): the rig placed so that the tracked eye's world pose is the one taken at
        // the resynchronisation. The camera's transform is what every pass reads (bodies, shadows, culling), so all see
        // one view; the AppSW application space (the rig) moves against the head and is recorded (rigMove, rigTurn).
        private void FixView()
        {
            if (!_fixedViewOn || eye == null || rig == null) return;
            Quaternion rigRotation = rig.rotation;
            Vector3 rigPosition = rig.position;
            Vector3 local = Quaternion.Inverse(rigRotation) * (eye.transform.position - rigPosition);
            Quaternion localRotation = Quaternion.Inverse(rigRotation) * eye.transform.rotation;
            Quaternion wanted = _eyeTargetRotation * Quaternion.Inverse(localRotation);
            Vector3 wantedPosition = _eyeTargetPosition - wanted * local;
            if (wanted == rigRotation && wantedPosition == rigPosition) return;
            rig.SetPositionAndRotation(wantedPosition, wanted);
            _rigMoveThisFrame = Mathf.Max(_rigMoveThisFrame, Vector3.Distance(wantedPosition, rigPosition));
            _rigTurnThisFrame = Mathf.Max(_rigTurnThisFrame, Quaternion.Angle(wanted, rigRotation));
        }

        // ---- the route's check and the judgement ---------------------------------------------------------------------

        private void CheckRoute()
        {
            bool skin = RequestedRoute == "SKIN24";
            if (skin)
            {
                if (!world.DrawsSkins || ActualRoute == null || !ActualRoute.StartsWith("SKIN24", StringComparison.Ordinal))
                    Invalidities.Add("the skin draw was asked for but the world draws the skins by " + ActualRoute + " (" + world.SkinRouteReason + ")");
                int refused = 0, drawnByRenderer = 0;
                string firstRefusal = null;
                foreach (SandboxNpcCharacter s in _slots)
                {
                    if (!s.DrawnBySkinDraw) { refused++; firstRefusal ??= s.SkinDrawRefusal; }
                    if (s.Renderer != null && s.Renderer.enabled) drawnByRenderer++;
                }

                if (refused > 0) Invalidities.Add(refused + " of " + _slots.Count + " individuals are not in the skin draw (first: " + (firstRefusal ?? "?") + ")");
                if (drawnByRenderer > 0) Invalidities.Add(drawnByRenderer + " individuals still have their renderer enabled");
            }
            else
            {
                if (world.DrawsSkins) Invalidities.Add("the renderers were asked for but the world made a skin draw (" + ActualRoute + ")");
                int off = _slots.Count(s => s.Renderer == null || !s.Renderer.enabled), inSkin = _slots.Count(s => s.DrawnBySkinDraw);
                if (off > 0) Invalidities.Add(off + " individuals have no enabled renderer");
                if (inSkin > 0) Invalidities.Add(inSkin + " individuals are in a skin draw");
            }
        }

        private void Judge(int skinVisible, int renderersVisible)
        {
            Counters a = _measureStart, b = _measureEnd;
            if (RequestedRoute == "SKIN24")
            {
                if (b.selectionsRefused != a.selectionsRefused) Invalidities.Add("selections refused in the measurement: " + (b.selectionsRefused - a.selectionsRefused));
                if (b.uploadRefusals != a.uploadRefusals) Invalidities.Add("uploads refused in the measurement: " + (b.uploadRefusals - a.uploadRefusals));
                if (b.growth != a.growth || b.retired != a.retired) Invalidities.Add("the skin geometry grew or replaced a buffer in the measurement (growth " + (b.growth - a.growth) + ", retired " + (b.retired - a.retired) + ")");
                if (_settings.pose == NpcSkinBenchPose.Stop && PalettesGatheredInMeasurement != 0) Invalidities.Add("palettes were gathered under the stopped pose: " + PalettesGatheredInMeasurement);
            }

            if (b.refused != a.refused || b.ringFull != a.ringFull || b.pipelineFailures != a.pipelineFailures || b.xrNotDrawn != a.xrNotDrawn || b.nativeRefusals != a.nativeRefusals)
                Invalidities.Add("the native route refused, filled its ring, failed a pipeline or left an XR frame undrawn in the measurement");
            if (b.pipelines != a.pipelines) Invalidities.Add("pipelines were made in the measurement: " + (b.pipelines - a.pipelines) + " (warm-up too short)");
            if (_settings.pose == NpcSkinBenchPose.Update)
            {
                int expected = _rows.Count(r => r.phase == 1) * UpdatedIndividuals;
                if (PosesAppliedInMeasurement != expected) Invalidities.Add("poses applied in the measurement " + PosesAppliedInMeasurement + " of the expected " + expected);
            }

            Valid = Invalidities.Count == 0;
            if (_eyeMaxMove > _settings.toleranceMetres) ComparisonIssues.Add("the eye moved " + _eyeMaxMove.ToString("0.000", Inv) + " m in the measurement (tolerance " + _settings.toleranceMetres.ToString("0.000", Inv) + ")");
            if (_eyeMaxTurn > _settings.toleranceDegrees) ComparisonIssues.Add("the eye turned " + _eyeMaxTurn.ToString("0.00", Inv) + " deg in the measurement (tolerance " + _settings.toleranceDegrees.ToString("0.00", Inv) + ")");
            if (_settings.measureFrames > 0 && _rows.Count(r => r.phase == 1) != _settings.measureFrames) ComparisonIssues.Add("measured frames " + _rows.Count(r => r.phase == 1) + " of the " + _settings.measureFrames + " asked");

            // The view: the eye must have started at the plan's eye (the pose the view rule was judged for; under the fixed
            // view it does, elsewhere the head decides), and every individual must have been visible at the end (Quest
            // series s3, 2026-10-30: heads looking down, 0 of 64 visible, and the comparison still "held"). Not in the
            // tests' cold world, where no camera renders.
            float startOffset = Vector3.Distance(_eyeAtStart, PlanEye), startTurn = Quaternion.Angle(_eyeRotationAtStart, Quaternion.identity);
            if (startOffset > _settings.toleranceMetres || startTurn > _settings.toleranceDegrees)
                ComparisonIssues.Add("the eye started " + startOffset.ToString("0.000", Inv) + " m and " + startTurn.ToString("0.00", Inv) + " deg from the plan's eye " + PlanEye.ToString("F2") + " facing +Z (yaw "
                    + YawOf(_eyeRotationAtStart).ToString("0.0", Inv) + " pitch " + PitchOf(_eyeRotationAtStart).ToString("0.0", Inv) + "; tolerances " + _settings.toleranceMetres.ToString("0.000", Inv) + " m, " + _settings.toleranceDegrees.ToString("0.00", Inv) + " deg)");
            if (_clipForTest == null)
            {
                if (RequestedRoute == "SMR" && renderersVisible != _slots.Count) ComparisonIssues.Add(renderersVisible + " of " + _slots.Count + " renderers visible at the end");
                if (RequestedRoute == "SKIN24" && skinVisible >= 0 && skinVisible != _slots.Count) ComparisonIssues.Add(skinVisible + " of " + _slots.Count + " individuals selected visible at the end");
            }

            ComparisonValid = ComparisonIssues.Count == 0;
        }

        // ---- the rendering of the condition ----------------------------------------------------------------------------

        private void ApplyRendering()
        {
            _pipelineBefore = QualitySettings.renderPipeline;
            RenderPipelineAsset current = GraphicsSettings.currentRenderPipeline;
            if (current == null) throw new InvalidOperationException("no render pipeline asset is in effect");
            // The run's own copy of the pipeline asset in effect: its cascades set, the shared asset untouched.
            _pipelineOwn = Instantiate(current);
            _pipelineOwn.name = current.name + " (npc-skin-bench)";
            int cascades = NpcSkinBenchSettings.CascadesOf(_settings.shadows);
            bool shadows = cascades > 0;
            // The cascade count is the condition's; everything else of the shadows (distance, resolution, soft, bias,
            // splits) stays the product's (SKIN-BENCH-2 section 2) and is recorded below.
            SetField(_pipelineOwn, "m_ShadowCascadeCount", shadows ? cascades : 1);
            SetField(_pipelineOwn, "m_MainLightShadowsSupported", shadows);
            QualitySettings.renderPipeline = _pipelineOwn;
            if (mainLight != null)
            {
                _lightShadowsBefore = mainLight.shadows;
                if (!shadows) mainLight.shadows = LightShadows.None;
                else if (mainLight.shadows == LightShadows.None) mainLight.shadows = LightShadows.Soft;
            }

            RenderPipelineAsset inEffect = GraphicsSettings.currentRenderPipeline;
            _drawSettings["pipeline"] = inEffect != null ? inEffect.name : "none";
            _drawSettings["pipelineSource"] = current.name;
            foreach (string field in new[] { "m_ShadowCascadeCount", "m_ShadowDistance", "m_MainLightShadowsSupported", "m_MainLightShadowmapResolution", "m_SoftShadowsSupported", "m_ShadowDepthBias", "m_ShadowNormalBias", "m_MSAA", "m_RenderScale", "m_SupportsHDR", "m_Cascade2Split", "m_Cascade3Split", "m_Cascade4Split", "m_CascadeBorder" })
                _drawSettings[field] = Convert.ToString(GetField(inEffect, field), Inv) ?? "?";
            _drawSettings["lightShadows"] = mainLight != null ? mainLight.shadows.ToString() : "no light";
            _drawSettings["lightShadowStrength"] = mainLight != null ? mainLight.shadowStrength.ToString("R", Inv) : "no light";
            _drawSettings["lightShadowBias"] = mainLight != null ? mainLight.shadowBias.ToString("R", Inv) + " / normal " + mainLight.shadowNormalBias.ToString("R", Inv) : "no light";
            _drawSettings["lightIntensity"] = mainLight != null ? mainLight.intensity.ToString("R", Inv) : "no light";
            _drawSettings["lightEuler"] = mainLight != null ? mainLight.transform.eulerAngles.ToString("F1") : "no light";
            _drawSettings["cameraFov"] = eye.fieldOfView.ToString("R", Inv);
            _drawSettings["cameraNear"] = eye.nearClipPlane.ToString("R", Inv);
            _drawSettings["cameraFar"] = eye.farClipPlane.ToString("R", Inv);
            _drawSettings["cameraHdr"] = eye.allowHDR.ToString();
            _drawSettings["cameraMsaa"] = eye.allowMSAA.ToString();
            _drawSettings["stereoRenderingMode"] = XRSettings.enabled ? XRSettings.stereoRenderingMode.ToString() : "no XR";
            _drawSettings["eyeTexture"] = XRSettings.enabled ? XRSettings.eyeTextureWidth + "x" + XRSettings.eyeTextureHeight : "no XR";
            _drawSettings["eyeTextureResolutionScale"] = XRSettings.eyeTextureResolutionScale.ToString("R", Inv);
            _drawSettings["screen"] = Screen.width + "x" + Screen.height;
            _drawSettings["vSyncCount"] = QualitySettings.vSyncCount.ToString(Inv);
            _drawSettings["targetFrameRate"] = Application.targetFrameRate.ToString(Inv);
            Log("rendering: " + string.Join(", ", _drawSettings.Select(p => p.Key + "=" + p.Value)));
        }

        private void RestoreRendering()
        {
            if (_pipelineOwn != null)
            {
                QualitySettings.renderPipeline = _pipelineBefore;
                Destroy(_pipelineOwn);
                _pipelineOwn = null;
            }

            if (mainLight != null && _settings != null) mainLight.shadows = _lightShadowsBefore;
        }

        private static void SetField(object owner, string name, object value)
        {
            FieldInfo f = Field(owner, name);
            if (f == null) throw new InvalidOperationException("the pipeline asset has no field " + name);
            object converted = f.FieldType.IsEnum ? Enum.ToObject(f.FieldType, value) : Convert.ChangeType(value, f.FieldType, Inv);
            f.SetValue(owner, converted);
        }

        private static object GetField(object owner, string name) => owner == null ? null : Field(owner, name)?.GetValue(owner);

        private static FieldInfo Field(object owner, string name)
        {
            for (Type t = owner.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }

            return null;
        }

        private static XRDisplaySubsystem XrDisplay()
        {
            var displays = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(displays);
            foreach (XRDisplaySubsystem d in displays) if (d.running) return d;
            return null;
        }

        private void StartProfiler()
        {
            Profiler.logFile = _settings.profilerFile;
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            _profiling = true;
            Log("PROFILER DIAGNOSIS: binary log to " + _settings.profilerFile + " for the measurement (this run is not a performance sample)");
        }

        private void StopProfiler()
        {
            Profiler.enabled = false;
            Profiler.enableBinaryLog = false;
            _profiling = false;
        }

        // ---- the results -----------------------------------------------------------------------------------------------

        private string WriteResults(int skinVisible, int renderersVisible)
        {
            var measured = _rows.Where(r => r.phase == 1).ToList();
            NpcSkinBenchStatistics main = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (r.hasMain, r.ftMainMs)));
            NpcSkinBenchStatistics render = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (r.hasRender, r.ftRenderMs)));
            NpcSkinBenchStatistics gpu = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (r.hasGpu, r.ftGpuMs)));
            NpcSkinBenchStatistics presentWait = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (r.hasPresentWait, r.ftPresentWaitMs)));
            NpcSkinBenchStatistics xrGpu = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (r.hasXrGpu, r.xrAppGpuMs)));
            NpcSkinBenchStatistics playerLoop = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.playerLoopMs), r.playerLoopMs)));
            NpcSkinBenchStatistics gfxWait = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.gfxPresentWaitMs), r.gfxPresentWaitMs)));
            NpcSkinBenchStatistics frameDelta = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (true, r.unscaledDeltaSeconds * 1000.0)));
            NpcSkinBenchStatistics poseEvaluate = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.poseEvaluateMs), r.poseEvaluateMs)), true);
            NpcSkinBenchStatistics poseApply = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.poseApplyMs), r.poseApplyMs)), true);
            NpcSkinBenchStatistics skinFrame = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinFrameMs), r.skinFrameMs)), true);
            NpcSkinBenchStatistics skinPalette = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinPaletteMs), r.skinPaletteMs)), true);
            NpcSkinBenchStatistics skinGather = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinGatherMs), r.skinGatherMs)), true);
            NpcSkinBenchStatistics skinUpload = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinUploadMs), r.skinUploadMs)), true);
            NpcSkinBenchStatistics skinUploadPalette = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinUploadPaletteMs), r.skinUploadPaletteMs)), true);
            NpcSkinBenchStatistics paletteWritesPerFrame = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (true, (double)r.paletteWrites)), true);
            NpcSkinBenchStatistics skinPrepare = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinPrepareMs), r.skinPrepareMs)), true);
            NpcSkinBenchStatistics skinSchedule = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinScheduleMs), r.skinScheduleMs)), true);
            NpcSkinBenchStatistics skinCompleteWait = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinCompleteWaitMs), r.skinCompleteWaitMs)), true);
            NpcSkinBenchStatistics skinPublish = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (!double.IsNaN(r.skinPublishMs), r.skinPublishMs)), true);
            NpcSkinBenchStatistics skinJobIndividuals = NpcSkinBenchStatistics.Of(NpcSkinBenchReport.Column(_rows, r => (true, (double)r.skinJobIndividuals)), true);
            VpSkinDisplay skinsNow = world != null ? world.SkinDisplay : null;
            VpNativeDrawRoute routeNow = _drawing != null ? _drawing.NativeRoute : null;
            string gatherActual = skinsNow != null && !skinsNow.IsDisposed ? skinsNow.GatherModeActual : null;
            long vertices = 0, bones = 0;
            foreach (SandboxNpcCharacter s in _slots)
            {
                if (s.Renderer != null && s.Renderer.sharedMesh != null) vertices += s.Renderer.sharedMesh.vertexCount;
                if (s.Renderer != null) bones += s.Renderer.bones.Length;
            }

            Counters a = _measureStart, b = _measureEnd;
            var longFrames = measured.Where(r => r.unscaledDeltaSeconds > 0.1).Select(r => NpcSkinBenchReport.Object(new[]
            {
                new KeyValuePair<string, string>("frame", r.frame.ToString(Inv)),
                new KeyValuePair<string, string>("unscaledDeltaMs", NpcSkinBenchReport.Number(r.unscaledDeltaSeconds * 1000.0)),
                new KeyValuePair<string, string>("wallUtc", NpcSkinBenchReport.Quote(new DateTime(r.wallUtcTicks, DateTimeKind.Utc).ToString("o"))),
            }, false)).ToList();
            VpNativeDrawRoute route = _drawing != null ? _drawing.NativeRoute : null;
            var members = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("scenario", NpcSkinBenchReport.Quote("npc-skin-bench")),
                new KeyValuePair<string, string>("requestedRoute", NpcSkinBenchReport.Quote(RequestedRoute)),
                new KeyValuePair<string, string>("actualRoute", NpcSkinBenchReport.Quote(ActualRoute)),
                new KeyValuePair<string, string>("actualRouteReason", NpcSkinBenchReport.Quote(world.SkinRouteReason)),
                new KeyValuePair<string, string>("bodyRoute", NpcSkinBenchReport.Quote(world.BodyRoute)),
                new KeyValuePair<string, string>("valid", Valid ? "true" : "false"),
                new KeyValuePair<string, string>("invalidities", NpcSkinBenchReport.Array(Invalidities.Select(NpcSkinBenchReport.Quote))),
                new KeyValuePair<string, string>("count", _settings.count.ToString(Inv)),
                new KeyValuePair<string, string>("pose", NpcSkinBenchReport.Quote(NpcSkinBenchSettings.Name(_settings.pose))),
                new KeyValuePair<string, string>("shadows", NpcSkinBenchReport.Quote(NpcSkinBenchSettings.Name(_settings.shadows))),
                new KeyValuePair<string, string>("families", NpcSkinBenchReport.Array(_families.Select(NpcSkinBenchReport.Quote))),
                new KeyValuePair<string, string>("clip", NpcSkinBenchReport.Quote(_clipAsset != null ? _clipAsset.name : null)),
                new KeyValuePair<string, string>("clipSeconds", NpcSkinBenchReport.Number(_clip != null ? _clip.DurationSeconds : double.NaN)),
                new KeyValuePair<string, string>("stepSeconds", NpcSkinBenchReport.Number(_settings.stepSeconds)),
                new KeyValuePair<string, string>("warmupSeconds", NpcSkinBenchReport.Number(_settings.warmupSeconds)),
                new KeyValuePair<string, string>("measureSeconds", NpcSkinBenchReport.Number(_settings.measureSeconds)),
                new KeyValuePair<string, string>("individuals", _slots.Count.ToString(Inv)),
                new KeyValuePair<string, string>("models", (world.DrawsSkins ? world.SkinDisplay.ModelCount : _shares.Count).ToString(Inv)),
                new KeyValuePair<string, string>("skinDrawIndividuals", (world.DrawsSkins ? world.SkinDisplay.IndividualCount : 0).ToString(Inv)),
                new KeyValuePair<string, string>("vertices", vertices.ToString(Inv)),
                new KeyValuePair<string, string>("bones", bones.ToString(Inv)),
                new KeyValuePair<string, string>("farthestRowMetres", NpcSkinBenchReport.Number(NpcSkinBenchPlan.FarthestDistance(_plan))),
                new KeyValuePair<string, string>("visibleBySkinSelectionAtEnd", skinVisible.ToString(Inv)),
                new KeyValuePair<string, string>("visibleRenderersAtEnd", renderersVisible.ToString(Inv)),
                new KeyValuePair<string, string>("framesWarmup", _rows.Count(r => r.phase == 0).ToString(Inv)),
                new KeyValuePair<string, string>("framesMeasured", measured.Count.ToString(Inv)),
                new KeyValuePair<string, string>("measureWallUtcStart", NpcSkinBenchReport.Quote(_measureWallStart.ToString("o"))),
                new KeyValuePair<string, string>("measureWallUtcEnd", NpcSkinBenchReport.Quote(_measureWallEnd.ToString("o"))),
                new KeyValuePair<string, string>("ftMainMs", main.Json()),
                new KeyValuePair<string, string>("ftRenderMs", render.Json()),
                new KeyValuePair<string, string>("ftGpuMs", gpu.Json()),
                new KeyValuePair<string, string>("ftPresentWaitMs", presentWait.Json()),
                new KeyValuePair<string, string>("xrAppGpuMs", xrGpu.Json()),
                new KeyValuePair<string, string>("playerLoopMs", playerLoop.Json()),
                new KeyValuePair<string, string>("gfxPresentWaitMs", gfxWait.Json()),
                new KeyValuePair<string, string>("unscaledDeltaMs", frameDelta.Json()),
                new KeyValuePair<string, string>("poseEvaluateMs", poseEvaluate.Json()),
                new KeyValuePair<string, string>("poseApplyMs", poseApply.Json()),
                new KeyValuePair<string, string>("skinFrameMs", skinFrame.Json()),
                new KeyValuePair<string, string>("skinPaletteMs", skinPalette.Json()),
                new KeyValuePair<string, string>("skinGatherMs", skinGather.Json()),
                new KeyValuePair<string, string>("skinUploadMs", skinUpload.Json()),
                new KeyValuePair<string, string>("skinUploadPaletteMs", skinUploadPalette.Json()),
                new KeyValuePair<string, string>("skinPrepareMs", skinPrepare.Json()),
                new KeyValuePair<string, string>("skinScheduleMs", skinSchedule.Json()),
                new KeyValuePair<string, string>("skinCompleteWaitMs", skinCompleteWait.Json()),
                new KeyValuePair<string, string>("skinPublishMs", skinPublish.Json()),
                new KeyValuePair<string, string>("skinJobIndividualsPerFrame", skinJobIndividuals.Json()),
                new KeyValuePair<string, string>("paletteWritesPerFrame", paletteWritesPerFrame.Json()),
                new KeyValuePair<string, string>("comparisonValid", ComparisonValid ? "true" : "false"),
                new KeyValuePair<string, string>("comparisonIssues", NpcSkinBenchReport.Array(ComparisonIssues.Select(NpcSkinBenchReport.Quote))),
                new KeyValuePair<string, string>("eyeStart", NpcSkinBenchReport.Object(new[]
                {
                    new KeyValuePair<string, string>("x", NpcSkinBenchReport.Number(_eyeAtStart.x)), new KeyValuePair<string, string>("y", NpcSkinBenchReport.Number(_eyeAtStart.y)), new KeyValuePair<string, string>("z", NpcSkinBenchReport.Number(_eyeAtStart.z)),
                    new KeyValuePair<string, string>("yaw", NpcSkinBenchReport.Number(YawOf(_eyeRotationAtStart))), new KeyValuePair<string, string>("pitch", NpcSkinBenchReport.Number(PitchOf(_eyeRotationAtStart))), new KeyValuePair<string, string>("roll", NpcSkinBenchReport.Number(RollOf(_eyeRotationAtStart))),
                }, false)),
                new KeyValuePair<string, string>("headAtSettle", NpcSkinBenchReport.Object(new[]
                {
                    new KeyValuePair<string, string>("x", NpcSkinBenchReport.Number(_headAtSettle.x)), new KeyValuePair<string, string>("y", NpcSkinBenchReport.Number(_headAtSettle.y)), new KeyValuePair<string, string>("z", NpcSkinBenchReport.Number(_headAtSettle.z)),
                    new KeyValuePair<string, string>("yaw", NpcSkinBenchReport.Number(YawOf(_headRotationAtSettle))), new KeyValuePair<string, string>("pitch", NpcSkinBenchReport.Number(PitchOf(_headRotationAtSettle))), new KeyValuePair<string, string>("roll", NpcSkinBenchReport.Number(RollOf(_headRotationAtSettle))),
                }, false)),
                new KeyValuePair<string, string>("toleranceMetres", NpcSkinBenchReport.Number(_settings.toleranceMetres)),
                new KeyValuePair<string, string>("toleranceDegrees", NpcSkinBenchReport.Number(_settings.toleranceDegrees)),
                new KeyValuePair<string, string>("fixedView", _settings.fixedView ? "true" : "false"),
                new KeyValuePair<string, string>("rigMaxMoveMetres", NpcSkinBenchReport.Number(_rigMaxMove)),
                new KeyValuePair<string, string>("rigMaxTurnDegrees", NpcSkinBenchReport.Number(_rigMaxTurn)),
                new KeyValuePair<string, string>("measureFrames", _settings.measureFrames.ToString(Inv)),
                new KeyValuePair<string, string>("settleFrames", _settings.settleFrames.ToString(Inv)),
                new KeyValuePair<string, string>("measureStepStart", MeasureStepStart.ToString(Inv)),
                new KeyValuePair<string, string>("measureStepEnd", MeasureStepEnd.ToString(Inv)),
                new KeyValuePair<string, string>("paletteJoinGap", _settings.paletteJoinGap.ToString(Inv)),
                new KeyValuePair<string, string>("updateCount", UpdatedIndividuals.ToString(Inv)),
                // SKIN-BENCH-3: the gathering asked and done, the worker's counters over the measurement, and the slice selection in effect.
                new KeyValuePair<string, string>("skinGatherAsked", NpcSkinBenchReport.Quote(VpSkinGatherSetup.Name(VpSkinGatherSetup.Mode))),
                new KeyValuePair<string, string>("skinGatherActual", NpcSkinBenchReport.Quote(gatherActual)),
                new KeyValuePair<string, string>("workerFallbackReason", NpcSkinBenchReport.Quote(skinsNow != null && !skinsNow.IsDisposed ? skinsNow.WorkerFallbackReason : null)),
                new KeyValuePair<string, string>("workerFallbacks", (skinsNow != null && !skinsNow.IsDisposed ? skinsNow.WorkerFallbacks : 0).ToString(Inv)),
                new KeyValuePair<string, string>("syncOnlyIndividuals", (skinsNow != null && !skinsNow.IsDisposed ? skinsNow.SyncOnlyIndividuals : 0).ToString(Inv)),
                new KeyValuePair<string, string>("jobsScheduledInMeasurement", (b.jobsScheduled - a.jobsScheduled).ToString(Inv)),
                new KeyValuePair<string, string>("lateSchedulesInMeasurement", (b.lateSchedules - a.lateSchedules).ToString(Inv)),
                new KeyValuePair<string, string>("lateRegathersInMeasurement", (b.lateRegathers - a.lateRegathers).ToString(Inv)),
                new KeyValuePair<string, string>("jobFailuresInMeasurement", (b.jobFailures - a.jobFailures).ToString(Inv)),
                new KeyValuePair<string, string>("completedEarlyInMeasurement", (b.completedEarly - a.completedEarly).ToString(Inv)),
                new KeyValuePair<string, string>("uncollectedJobsInMeasurement", (b.uncollectedJobs - a.uncollectedJobs).ToString(Inv)),
                new KeyValuePair<string, string>("belowThresholdFramesInMeasurement", (b.belowThresholdFrames - a.belowThresholdFrames).ToString(Inv)),
                new KeyValuePair<string, string>("skinGatherWorkerFrom", (skinsNow != null && !skinsNow.IsDisposed ? skinsNow.WorkerFrom : VpSkinGatherSetup.WorkerFrom).ToString(Inv)),
                new KeyValuePair<string, string>("shadowSliceSelectionAsked", VpGpuCullSetup.ShadowSliceSelection ? "true" : "false"),
                new KeyValuePair<string, string>("skin24SliceSelectionAsked", VpGpuCullSetup.Skin24SliceSelectionOnVulkan ? "true" : "false"),
                new KeyValuePair<string, string>("skinCasterSliceSelection", routeNow != null && routeNow.SkinCasterSliceSelection.HasValue ? (routeNow.SkinCasterSliceSelection.Value ? "true" : "false") : "null"),
                new KeyValuePair<string, string>("ordinaryCasterSliceSelection", routeNow != null && routeNow.OrdinaryCasterSliceSelection.HasValue ? (routeNow.OrdinaryCasterSliceSelection.Value ? "true" : "false") : "null"),
                new KeyValuePair<string, string>("captureRecord", NpcSkinBenchReport.Quote(_captureRecord)),
                new KeyValuePair<string, string>("frameTimingReads", _sampler.Reads.ToString(Inv)),
                new KeyValuePair<string, string>("frameTimingDuplicates", _sampler.Duplicates.ToString(Inv)),
                new KeyValuePair<string, string>("frameTimingAbsent", _sampler.Absent.ToString(Inv)),
                new KeyValuePair<string, string>("frameTimingGpuAbsent", _sampler.GpuAbsent.ToString(Inv)),
                new KeyValuePair<string, string>("frameTimingEnabled", FrameTimeSampler.FrameTimingEnabled ? "true" : "false"),
                new KeyValuePair<string, string>("posesAppliedInMeasurement", PosesAppliedInMeasurement.ToString(Inv)),
                new KeyValuePair<string, string>("palettesGatheredInMeasurement", PalettesGatheredInMeasurement.ToString(Inv)),
                new KeyValuePair<string, string>("paletteBytesInMeasurement", (b.paletteBytes - a.paletteBytes).ToString(Inv)),
                new KeyValuePair<string, string>("paletteWritesInMeasurement", (b.paletteWrites - a.paletteWrites).ToString(Inv)),
                new KeyValuePair<string, string>("paletteBytesExtraInMeasurement", (b.paletteBytesExtra - a.paletteBytesExtra).ToString(Inv)),
                new KeyValuePair<string, string>("skinBodiesIssuedInMeasurement", (b.skinBodies - a.skinBodies).ToString(Inv)),
                new KeyValuePair<string, string>("skinCastersIssuedInMeasurement", (b.skinCasters - a.skinCasters).ToString(Inv)),
                new KeyValuePair<string, string>("skinMotionIssuedInMeasurement", (b.skinMotion - a.skinMotion).ToString(Inv)),
                new KeyValuePair<string, string>("bodiesIssuedInMeasurement", (b.bodies - a.bodies).ToString(Inv)),
                new KeyValuePair<string, string>("castersIssuedInMeasurement", (b.casters - a.casters).ToString(Inv)),
                new KeyValuePair<string, string>("motionIssuedInMeasurement", (b.motion - a.motion).ToString(Inv)),
                new KeyValuePair<string, string>("selectionsIssuedInMeasurement", (b.selections - a.selections).ToString(Inv)),
                new KeyValuePair<string, string>("boundsDispatchesInMeasurement", (b.dispatches - a.dispatches).ToString(Inv)),
                new KeyValuePair<string, string>("pipelinesAtMeasureStart", a.pipelines.ToString(Inv)),
                new KeyValuePair<string, string>("pipelinesAtMeasureEnd", b.pipelines.ToString(Inv)),
                new KeyValuePair<string, string>("skinGeometryGrowthInMeasurement", (b.growth - a.growth).ToString(Inv)),
                new KeyValuePair<string, string>("eyeMaxMoveMetres", NpcSkinBenchReport.Number(_eyeMaxMove)),
                new KeyValuePair<string, string>("eyeMaxTurnDegrees", NpcSkinBenchReport.Number(_eyeMaxTurn)),
                new KeyValuePair<string, string>("longFramesOver100ms", NpcSkinBenchReport.Array(longFrames)),
                new KeyValuePair<string, string>("rendering", NpcSkinBenchReport.Object(_drawSettings.Select(p => new KeyValuePair<string, string>(p.Key, NpcSkinBenchReport.Quote(p.Value))), false)),
                new KeyValuePair<string, string>("nativeRoute", NpcSkinBenchReport.Quote(route != null ? route.Describe() : "none")),
                new KeyValuePair<string, string>("skinDisplay", NpcSkinBenchReport.Quote(world.DrawsSkins ? world.SkinDisplay.Describe() : "none")),
                new KeyValuePair<string, string>("gpuBreakdown", NpcSkinBenchReport.Quote("not measured: whole-frame GPU time from FrameTimingManager (ftGpuMs) and the XR display's app GPU time (xrAppGpuMs) only; no per-pass GPU timing (Unity skinning, bodies, shadows, skin bounds/selection, native constants, AppSW) is taken by this benchmark"
                    + (_settings.profilerFile != null ? "; a Profiler binary log of this diagnosis run is at " + _settings.profilerFile : ""))),
                new KeyValuePair<string, string>("profilerDiagnosis", _settings.profilerFile != null ? "true" : "false"),
                new KeyValuePair<string, string>("platform", NpcSkinBenchReport.Quote(SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName + " / " + SystemInfo.deviceModel + " / unity " + Application.unityVersion + " / build " + Application.buildGUID + (Debug.isDebugBuild ? " (Development)" : ""))),
                new KeyValuePair<string, string>("commandLine", NpcSkinBenchReport.Quote(string.Join(" ", Environment.GetCommandLineArgs()))),
            };
            string json = NpcSkinBenchReport.Object(members);
            string summary = "route " + RequestedRoute + "->" + ActualRoute + " valid " + Valid + "; count " + _slots.Count + " pose " + NpcSkinBenchSettings.Name(_settings.pose) + " shadows " + NpcSkinBenchSettings.Name(_settings.shadows)
                + "; frames " + measured.Count + "; GPU " + gpu.Describe() + "; Main " + main.Describe() + "; Render " + render.Describe() + "; present wait " + presentWait.Describe()
                + "; XR app GPU " + xrGpu.Describe() + "; poses applied " + PosesAppliedInMeasurement + ", palettes gathered " + PalettesGatheredInMeasurement
                + ", skin bodies/casters/motion issued " + (b.skinBodies - a.skinBodies) + "/" + (b.skinCasters - a.skinCasters) + "/" + (b.skinMotion - a.skinMotion)
                + ", bodies/casters/motion issued " + (b.bodies - a.bodies) + "/" + (b.casters - a.casters) + "/" + (b.motion - a.motion)
                + "; visible: skin selection " + skinVisible + ", renderers " + renderersVisible + (Invalidities.Count > 0 ? "; INVALID: " + string.Join("; ", Invalidities) : "")
                + "; gather " + (gatherActual ?? "-") + " (asked " + VpSkinGatherSetup.Name(VpSkinGatherSetup.Mode) + "), jobs " + (b.jobsScheduled - a.jobsScheduled) + ", late regathers " + (b.lateRegathers - a.lateRegathers) + ", job failures " + (b.jobFailures - a.jobFailures)
                + "; skin caster slice selection " + (routeNow != null && routeNow.SkinCasterSliceSelection.HasValue ? (routeNow.SkinCasterSliceSelection.Value ? "on" : "off") : "-")
                + "; update count " + UpdatedIndividuals
                + "; comparison " + (ComparisonValid ? "holds" : "DOES NOT HOLD: " + string.Join("; ", ComparisonIssues)) + "; steps " + MeasureStepStart + ".." + MeasureStepEnd
                + "; markers ms: pose evaluate " + poseEvaluate.Describe("") + "; pose apply " + poseApply.Describe("") + "; skin frame " + skinFrame.Describe("") + "; palette " + skinPalette.Describe("")
                + "; gather " + skinGather.Describe("") + "; upload " + skinUpload.Describe("") + "; upload palette " + skinUploadPalette.Describe("") + "; palette writes " + (b.paletteWrites - a.paletteWrites) + " (" + (b.paletteBytesExtra - a.paletteBytesExtra) + " B extra)";
            if (string.IsNullOrEmpty(_settings.directory)) return summary;
            var csv = new StringBuilder(NpcSkinBenchReport.Header).Append('\n');
            foreach (NpcSkinBenchRow r in _rows) csv.Append(NpcSkinBenchReport.Csv(in r)).Append('\n');
            File.WriteAllText(Path.Combine(_settings.directory, "bench-frames.csv"), csv.ToString());
            File.WriteAllText(Path.Combine(_settings.directory, "bench-result.json"), json);
            File.WriteAllText(Path.Combine(_settings.directory, "bench-result.txt"), summary + "\n");
            return summary;
        }

        private void Fail(string failure)
        {
            Failure = failure;
            Current = Phase.Failed;
            ExitCode = ExitFailed;
            Debug.LogError(Prefix + "FAILED: " + failure);
            if (!string.IsNullOrEmpty(_settings?.directory))
            {
                try { Directory.CreateDirectory(_settings.directory); File.WriteAllText(Path.Combine(_settings.directory, "bench-result.txt"), "FAILED: " + failure + "\n"); }
                catch (Exception e) { Debug.LogError(Prefix + "the failure could not be written: " + e.Message); }
            }

            RestoreRendering();
            if (_settings != null && !_settings.keepOpen && !_armedForTest) Application.Quit(ExitFailed);
        }

        private static void Log(string text) => Debug.Log(Prefix + text);

        private void OnDestroy()
        {
            if (_playerLoop.Valid) _playerLoop.Dispose();
            if (_gfxWait.Valid) _gfxWait.Dispose();
            if (_gcAlloc.Valid) _gcAlloc.Dispose();
            foreach (ProfilerRecorder r in new[] { _poseEvaluate, _poseApply, _skinFrame, _skinPalette, _skinGather, _skinUpload, _skinUploadPalette }) if (r.Valid) r.Dispose();
            if (_fixedViewOn) { Application.onBeforeRender -= FixView; _fixedViewOn = false; }
            VpSkinDisplay.PaletteJoinGapMatrices = 0;
            if (_profiling) StopProfiler();
            RestoreRendering();
        }
    }
}
