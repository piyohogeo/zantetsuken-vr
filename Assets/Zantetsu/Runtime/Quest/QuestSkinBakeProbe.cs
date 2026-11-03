using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR.Features;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>
    /// The Quest's entry for the GPU bake probe (DESIGN 4.5.14, skin-bake-probe): the probe's settings from
    /// <c>quest3s-config.json</c> (its own fields beside the common ones), the record under the persistent data's runs,
    /// and what a Quest scene needs whatever runs in it: AppSW with the tracking origin's pose, the display's cadence.
    /// The world's draw route is the configuration's mode, selected by QuestNpcSlashDemo before the scene's world is
    /// made, as for every scripted scenario.
    /// </summary>
    public sealed class QuestSkinBakeProbe : MonoBehaviour
    {
        public const string SceneName = "QuestSkinBakeProbe";
        public const string PackageName = "com.zantetsuken.quest.skinbakeprobe";
        private const string LogPrefix = "QUEST SKIN BAKE PROBE: ";

        [Serializable]
        internal sealed class Config
        {
            public string scenario = "";
            public string mode = "vp3c-native";
            public bool appSw = true;
            public string appSpacePoseMode = "tracking-origin";
            public bool keepOpen = true;
            public string skinBakeProbeSubject = "both";    // small, npc or both
            public int skinBakeProbeDrawFrames = SkinBakeProbeSettings.DefaultDrawFrames;
            public bool skinBakeProbeDraw = true;
            public bool skinBakeProbePicture = true;
            public string skinBakeProbeBench = "";          // "" (the cases), burst or gpu (SKIN-BAKE-PROBE-2)
            public int skinBakeProbeBenchFrames = SkinBakeProbeSettings.DefaultBenchFrames;
            public int skinBakeProbeBenchWarmup = SkinBakeProbeSettings.DefaultBenchWarmup;
            public int skinBakeProbeBenchBodies = SkinBakeProbeSettings.DefaultBenchBodies;
            public string skinBakeProbeBenchSeries = "";   // SKIN-BAKE-PROBE-3: the conditions in one launch (route+bodies[xrepeat], comma separated); empty: skinBakeProbeBench alone
            public int skinBakeProbeBenchLoad = 0;
            public int skinBakeProbeCutBench = 0;          // SKIN-GPU-FIRST-1: the cut bench's bodies (0: none); the skin bake route comes from the launch's -zantetsuSkinBake (the intent's unity extra)
            public int skinBakeProbeCutStagger = 0;
            public int skinBakeProbeCutRounds = 1;
            public bool skinBakeProbeCutNoVerify = false;   // the performance run: no picture, no verification readback
            public string skinBakeProbeCutPlane = "legacy";   // PLANE-1: legacy (the renderer-frame +y plane) or horizontal (the world's horizontal plane through the bounds centre)
            public int skinBakeProbeCutPictureFrames = 0;      // SKIN-LINES-1: verification runs picture every frame pre..+N after the last request
            public int skinBakeProbeHoldCpuReady = 0;          // PLANE-1 diagnosis: the gpu route's CPU bake kept out of the dispatcher N frames
            public string skinBakeProbeShadows = "both";       // SKIN-LINES-1: both, nocast, noreceive or none
            public bool skinBakeProbeMotionCapture = false;    // PLANE-1: the AppSW motion output of the pictured frames read back (diagnosis)
            public bool skinBakeProbeMotionCheck = false;      // MOTION-CAPTURE-1: the capture-method check in place of the cases
            public float skinBakeProbeMotionCheckStepCm = 1f;
            public int skinBakeProbeMotionCheckFrames = 6;
            public int skinBakeProbeMotionCheckHold = 12;
            public bool skinBakeProbeMotionView = false;       // MOTION-CAPTURE-1 step 6: the cut bench's eye follows the check's sequence (with skinBakeProbeMotionCapture)
            public string skinBakeProbeWarm = "none";          // SKIN-GPU-FIRST-STARTUP-1: none | draw (the product's QuestDrawPrewarm before the bench)
            public float skinBakeProbeEdgeRight = 0.6f;    // the Quest's one-eye picture: its right edge is nearer than the PC's (run 3's 'both' picture)
            public float skinBakeProbeEdgeLeft = -2.8f;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private Config _config;
        private SkinBakeProbeSettings _settings;
        private SkinBakeProbeRunner _runner;
        private Camera _camera;
        private Transform _appSpaceOrigin;
        private bool _spaceWarpApplied, _recorded;
        private float _nextStatus;
        private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();

        /// <summary>For the tests: the settings a configuration text gives (null: no file), or the failure.</summary>
        internal static SkinBakeProbeSettings SettingsOf(string json, string directory, out string failure)
        {
            Config config = string.IsNullOrEmpty(json) ? new Config() : JsonUtility.FromJson<Config>(json) ?? new Config();
            return SettingsOf(config, directory, out failure);
        }

        private static SkinBakeProbeSettings SettingsOf(Config config, string directory, out string failure)
        {
            failure = null;
            var settings = new SkinBakeProbeSettings { directory = directory, drawFrames = config.skinBakeProbeDrawFrames, draw = config.skinBakeProbeDraw, picture = config.skinBakeProbePicture, keepOpen = config.keepOpen };
            switch ((config.skinBakeProbeSubject ?? "").ToLowerInvariant())
            {
                case "small": settings.small = true; settings.npc = false; break;
                case "npc": settings.small = false; settings.npc = true; break;
                case "both": case "": settings.small = true; settings.npc = true; break;
                default: failure = "skinBakeProbeSubject must be small, npc or both"; return null;
            }

            if (config.skinBakeProbeDrawFrames < 2 || config.skinBakeProbeDrawFrames > 100000) { failure = "skinBakeProbeDrawFrames must be 2..100000"; return null; }
            switch ((config.skinBakeProbeBench ?? "").ToLowerInvariant())
            {
                case "": settings.bench = null; break;
                case "burst": settings.bench = "burst"; break;
                case "gpu": settings.bench = "gpu"; break;
                default: failure = "skinBakeProbeBench must be empty, burst or gpu"; return null;
            }
            if (config.skinBakeProbeBenchFrames < 10 || config.skinBakeProbeBenchFrames > 100000) { failure = "skinBakeProbeBenchFrames must be 10..100000"; return null; }
            if (config.skinBakeProbeBenchWarmup < 0 || config.skinBakeProbeBenchWarmup > 10000) { failure = "skinBakeProbeBenchWarmup must be 0..10000"; return null; }
            if (config.skinBakeProbeBenchBodies < 1 || config.skinBakeProbeBenchBodies > SkinBakeProbeSettings.MaxBenchBodies) { failure = "skinBakeProbeBenchBodies must be 1.." + SkinBakeProbeSettings.MaxBenchBodies; return null; }
            settings.benchFrames = config.skinBakeProbeBenchFrames; settings.benchWarmup = config.skinBakeProbeBenchWarmup; settings.benchBodies = config.skinBakeProbeBenchBodies;
            if (!string.IsNullOrWhiteSpace(config.skinBakeProbeBenchSeries))
            {
                if (settings.bench != null) { failure = "skinBakeProbeBench and skinBakeProbeBenchSeries are two ways of asking for the bench: give one"; return null; }
                if (!SkinBakeProbeSettings.TryParseSeries(config.skinBakeProbeBenchSeries, out settings.benchSeries, out string why)) { failure = "skinBakeProbeBenchSeries: " + why; return null; }
            }
            else if (settings.bench != null) settings.benchSeries = new System.Collections.Generic.List<SkinBakeProbeSettings.BenchCondition> { new SkinBakeProbeSettings.BenchCondition { route = settings.bench, bodies = settings.benchBodies } };
            if (config.skinBakeProbeBenchLoad < 0 || config.skinBakeProbeBenchLoad > SkinBakeProbeSettings.MaxBenchLoad) { failure = "skinBakeProbeBenchLoad must be 0.." + SkinBakeProbeSettings.MaxBenchLoad; return null; }
            settings.benchLoad = config.skinBakeProbeBenchLoad;
            if (config.skinBakeProbeCutBench < 0 || config.skinBakeProbeCutBench > SkinBakeProbeSettings.MaxCutBodies) { failure = "skinBakeProbeCutBench must be 0.." + SkinBakeProbeSettings.MaxCutBodies; return null; }
            if (config.skinBakeProbeCutStagger < 0 || config.skinBakeProbeCutStagger > SkinBakeProbeSettings.MaxCutStagger) { failure = "skinBakeProbeCutStagger must be 0.." + SkinBakeProbeSettings.MaxCutStagger; return null; }
            if (config.skinBakeProbeCutRounds < 1 || config.skinBakeProbeCutRounds > SkinBakeProbeSettings.MaxCutRounds) { failure = "skinBakeProbeCutRounds must be 1.." + SkinBakeProbeSettings.MaxCutRounds; return null; }
            if (config.skinBakeProbeCutBench > 0 && settings.IsBench) { failure = "skinBakeProbeCutBench and the bake bench are two different benches: give one"; return null; }
            settings.cutBodies = config.skinBakeProbeCutBench; settings.cutStagger = config.skinBakeProbeCutStagger; settings.cutRounds = config.skinBakeProbeCutRounds;
            settings.cutNoVerify = config.skinBakeProbeCutNoVerify;
            string plane = (config.skinBakeProbeCutPlane ?? "legacy").ToLowerInvariant();
            if (Array.IndexOf(SkinBakeProbeSettings.CutPlanes, plane) < 0) { failure = "skinBakeProbeCutPlane must be legacy or horizontal"; return null; }
            settings.cutPlane = plane;
            if (config.skinBakeProbeCutPictureFrames < 0 || config.skinBakeProbeCutPictureFrames > SkinBakeProbeSettings.MaxCutPictureFrames) { failure = "skinBakeProbeCutPictureFrames must be 0.." + SkinBakeProbeSettings.MaxCutPictureFrames; return null; }
            settings.cutPictureFrames = config.skinBakeProbeCutPictureFrames;
            if (config.skinBakeProbeHoldCpuReady < 0 || config.skinBakeProbeHoldCpuReady > SkinBakeProbeSettings.MaxHoldCpuReady) { failure = "skinBakeProbeHoldCpuReady must be 0.." + SkinBakeProbeSettings.MaxHoldCpuReady; return null; }
            settings.holdCpuReady = config.skinBakeProbeHoldCpuReady;
            string shadows = (config.skinBakeProbeShadows ?? "both").ToLowerInvariant();
            if (Array.IndexOf(SkinBakeProbeSettings.ShadowConditions, shadows) < 0) { failure = "skinBakeProbeShadows must be both, nocast, noreceive or none"; return null; }
            settings.shadows = shadows;
            settings.motionCapture = config.skinBakeProbeMotionCapture;
            settings.motionCheck = config.skinBakeProbeMotionCheck;
            if (float.IsNaN(config.skinBakeProbeMotionCheckStepCm) || config.skinBakeProbeMotionCheckStepCm <= 0f || config.skinBakeProbeMotionCheckStepCm > 50f) { failure = "skinBakeProbeMotionCheckStepCm must be in (0, 50]"; return null; }
            if (config.skinBakeProbeMotionCheckFrames < 1 || config.skinBakeProbeMotionCheckFrames > 120 || config.skinBakeProbeMotionCheckHold < 1 || config.skinBakeProbeMotionCheckHold > 120) { failure = "skinBakeProbeMotionCheckFrames/Hold must be 1..120"; return null; }
            settings.motionCheckStepCm = config.skinBakeProbeMotionCheckStepCm; settings.motionCheckFrames = config.skinBakeProbeMotionCheckFrames; settings.motionCheckHold = config.skinBakeProbeMotionCheckHold;
            if (settings.motionCheck && (settings.cutBodies > 0 || settings.IsBench)) { failure = "skinBakeProbeMotionCheck runs alone"; return null; }
            settings.motionView = config.skinBakeProbeMotionView;
            if (Array.IndexOf(SkinBakeProbeSettings.WarmKinds, config.skinBakeProbeWarm ?? "") < 0) { failure = "skinBakeProbeWarm must be " + string.Join("|", SkinBakeProbeSettings.WarmKinds); return null; }
            settings.warm = config.skinBakeProbeWarm;
            if (settings.motionView && settings.cutBodies == 0) { failure = "skinBakeProbeMotionView belongs to the cut bench (skinBakeProbeCutBench)"; return null; }
            if (settings.motionView && !settings.motionCapture) { failure = "skinBakeProbeMotionView needs skinBakeProbeMotionCapture"; return null; }
            if (settings.motionView && (settings.cutPictureFrames <= 0 || settings.cutNoVerify || settings.benchWarmup < 1)) { failure = "skinBakeProbeMotionView needs the frame window: skinBakeProbeCutPictureFrames above 0, no skinBakeProbeCutNoVerify, warmup at least 1"; return null; }
            if (float.IsNaN(config.skinBakeProbeEdgeRight) || float.IsNaN(config.skinBakeProbeEdgeLeft) || Mathf.Abs(config.skinBakeProbeEdgeRight) > SkinBakeProbeSettings.EdgeLimit || Mathf.Abs(config.skinBakeProbeEdgeLeft) > SkinBakeProbeSettings.EdgeLimit) { failure = "skinBakeProbeEdgeRight/Left must be metres within +-" + SkinBakeProbeSettings.EdgeLimit; return null; }
            settings.edgeRight = config.skinBakeProbeEdgeRight; settings.edgeLeft = config.skinBakeProbeEdgeLeft;
            if (config.mode != "vp3c" && config.mode != "vp3c-native") { failure = "mode must be vp3c or vp3c-native"; return null; }
            if (config.appSpacePoseMode != "camera" && config.appSpacePoseMode != "tracking-origin") { failure = "appSpacePoseMode must be camera or tracking-origin"; return null; }
            return settings;
        }

        /// <summary>The Quest's entry (SandboxScenario, after the scene is loaded and before anything of it starts).</summary>
        internal static string StartForScenario()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != SceneName) return "skin-bake-probe needs the scene " + SceneName + "; this is " + scene;
            string file = Path.Combine(Application.persistentDataPath, SandboxScenario.ConfigFile);
            string json = File.Exists(file) ? File.ReadAllText(file) : null;
            Config config = string.IsNullOrEmpty(json) ? new Config() : JsonUtility.FromJson<Config>(json) ?? new Config();
            string directory = Path.Combine(Application.persistentDataPath, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-skinbakeprobe-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            SkinBakeProbeSettings settings = SettingsOf(config, directory, out string failure);
            if (settings == null) return "the configuration is not usable: " + failure;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "quest-config.json"), json ?? "(no " + SandboxScenario.ConfigFile + ": every default)");
            SkinBakeProbeRunner.Arm(settings);
            var entry = new GameObject("Quest skin bake probe").AddComponent<QuestSkinBakeProbe>();
            entry._config = config;
            entry._settings = settings;
            // SKIN-GPU-FIRST-STARTUP-1: the product's drawing preparation before the bench, when asked (the probe's own assembly cannot reach it).
            if (settings.warm == "draw" || settings.warm == "draw+skin-dispatch") SkinBakeProbeRunner.WarmBeforeBench = entry.WarmDraw;
            Debug.Log(LogPrefix + "armed: " + settings.Describe() + "; mode " + config.mode + "; AppSW " + config.appSw + "; directory " + directory);
            return null;
        }

        private void Start()
        {
            _camera = Camera.main;
            _appSpaceOrigin = _camera != null ? _camera.transform.parent : null;
            _runner = FindAnyObjectByType<SkinBakeProbeRunner>(FindObjectsInactive.Include);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
        }

        private void OnDestroy() { RenderPipelineManager.beginCameraRendering -= BeforeCamera; SkinBakeProbeRunner.WarmBeforeBench = null; }

        /// <summary>
        /// The product's drawing preparation (QuestDrawPrewarm: the native route's warm request and Unity's stencil draws) run
        /// before the cut bench, as play runs it at its start; its report goes to the probe (a Fact) and to prewarm.txt. It does
        /// not warm the GPU bake (Bake16) nor the snapshot/palette allocation: those are separate candidates of the unit.
        /// </summary>
        private System.Collections.IEnumerator WarmDraw()
        {
            var report = new List<string>();
            SkinBakeProbeRunner.WarmReport = report;
            Zantetsu.PhysicsCut.CutWorldRoot world = FindAnyObjectByType<Zantetsu.PhysicsCut.CutWorldRoot>();
            if (_camera == null) _camera = Camera.main;
            double deadline = Zantetsu.Core.GamePause.UnscaledTimeAsDouble + 30.0;
            while ((world == null || !world.IsReady || world.Display == null || world.Display.PrewarmStencilMaterials == null) && Zantetsu.Core.GamePause.UnscaledTimeAsDouble < deadline)
            {
                yield return null;
                if (world == null) world = FindAnyObjectByType<Zantetsu.PhysicsCut.CutWorldRoot>();
            }
            if (world == null || !world.IsReady || world.Display == null || world.Display.PrewarmStencilMaterials == null) { report.Add("FAILED: the cut world or the display's materials were not ready within 30 s"); yield break; }
            var drawing = world.GetComponent<Zantetsu.PhysicsCut.CutWorldCameraDrawing>();
            // The display must be running before the drawing is warmed (series 1, p2: started on a ~3 fps session right after a relaunch,
            // Unity's warm frames were never rendered and the preparation ran 458 s): 30 consecutive frames under 50 ms, at most 60 s.
            int steady = 0, settleFrames = 0;
            double settleDeadline = Zantetsu.Core.GamePause.UnscaledTimeAsDouble + 60.0;
            while (steady < 30 && Zantetsu.Core.GamePause.UnscaledTimeAsDouble < settleDeadline)
            {
                yield return null;
                settleFrames++;
                steady = Time.unscaledDeltaTime < 0.05f ? steady + 1 : 0;
            }
            report.Add("settle before the warm: " + settleFrames + " frames waited, steady " + steady + " (30 wanted)" + (steady < 30 ? " TIMED OUT at 60 s" : ""));
            int began = Time.frameCount;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var prewarm = new QuestDrawPrewarm(_camera, world, drawing, _config.mode == "vp3c-native", _config.appSw, false, QuestDrawPrewarm.DefaultTimeoutSeconds, () =>
            {
                foreach (var npc in FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None)) if (npc != null && npc.Handle != null && npc.Handle.SkinModelForWarm != null) return npc.Handle.SkinModelForWarm;
                return null;   // before the bench makes its bodies: the probe's own skin-dispatch warm covers the bake (-Warm draw+skin-dispatch)
            });
            yield return prewarm.Run();
            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) / (double)System.Diagnostics.Stopwatch.Frequency;
            report.Add((prewarm.Completed ? "completed" : "FAILED: " + prewarm.Failure) + "; frames " + began + ".." + Time.frameCount + " (" + (Time.frameCount - began) + "), " + seconds.ToString("F3", Inv) + " s; native " + (_config.mode == "vp3c-native") + ", motion " + _config.appSw);
            report.AddRange(prewarm.Report);
            try { File.WriteAllLines(Path.Combine(_settings.directory, "prewarm.txt"), report); } catch (Exception e) { report.Add("prewarm.txt NOT WRITTEN: " + e.Message); }
        }

        private void Update()
        {
            if (_config == null) return;
            if (!_spaceWarpApplied) _spaceWarpApplied = SpaceWarpFeature.SetSpaceWarp(_config.appSw);
            if (_runner != null && !_recorded && (_runner.Current == SkinBakeProbeRunner.Phase.Done || _runner.Current == SkinBakeProbeRunner.Phase.Failed)) RecordPlatform();
            if (Zantetsu.Core.GamePause.UnscaledTime < _nextStatus) return;
            _nextStatus = Zantetsu.Core.GamePause.UnscaledTime + 5f;
            Debug.Log(LogPrefix + Status());
        }

        private string Status()
        {
            _displays.Clear();
            SubsystemManager.GetSubsystems(_displays);
            float refresh = 0f, gpu = 0f;
            foreach (XRDisplaySubsystem xr in _displays) { xr.TryGetDisplayRefreshRate(out refresh); xr.TryGetAppGPUTimeLastFrame(out gpu); }
            return "phase=" + (_runner != null ? _runner.Current.ToString() : "no runner") + " AppSWRequested=" + _config.appSw + " AppSWConfigApplied=" + _spaceWarpApplied
                + " xrReportedHz=" + refresh + " appGpuMs=" + (gpu * 1000f).ToString("F2", Inv) + " stereo=" + XRSettings.stereoRenderingMode;
        }

        private void RecordPlatform()
        {
            _recorded = true;
            try
            {
                File.WriteAllLines(Path.Combine(_settings.directory, "quest-platform.txt"), new[]
                {
                    "device=" + SystemInfo.deviceModel, "graphics=" + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName,
                    "buildGUID=" + Application.buildGUID, "unity=" + Application.unityVersion, "package=" + Application.identifier,
                    "mode=" + _config.mode, "appSwRequested=" + _config.appSw, "appSwConfigApplied=" + _spaceWarpApplied, "appSpacePoseMode=" + _config.appSpacePoseMode,
                    Status(), "runnerExitCode=" + (_runner != null ? _runner.ExitCode : -1),
                });
            }
            catch (Exception e) { Debug.LogError(LogPrefix + "the platform record could not be written: " + e.Message); }
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || _config == null || !_config.appSw || camera != _camera) return;
            Transform appSpace = _config.appSpacePoseMode == "tracking-origin" && _appSpaceOrigin != null ? _appSpaceOrigin : camera.transform;
            SpaceWarpFeature.SetAppSpacePosition(appSpace.position);
            SpaceWarpFeature.SetAppSpaceRotation(appSpace.rotation);
        }
    }
}
