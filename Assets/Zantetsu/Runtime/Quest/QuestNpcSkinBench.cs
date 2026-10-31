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
    /// The Quest's entry for the NPC skin benchmark (DESIGN 3.4, npc-skin-bench): the benchmark's settings from
    /// <c>quest3s-config.json</c> (its own fields, beside the common ones the other scenarios read: the draw route's
    /// mode is selected by QuestNpcSlashDemo before the scene's world is made, as for every scripted scenario), the
    /// results under the persistent data's runs, and what a Quest scene needs whatever runs in it: AppSW with the
    /// tracking origin's pose, the display's cadence. The skin route is the launch's (the intent's unity extra:
    /// <c>-zantetsuSkinUnity</c> or <c>-zantetsuSkinDraw</c>); the configuration may name it too (<c>skinRoute</c>),
    /// which selects the same before the world is made.
    /// </summary>
    public sealed class QuestNpcSkinBench : MonoBehaviour
    {
        public const string SceneName = "QuestNpcSkinBench";
        public const string PackageName = "com.zantetsuken.quest.npcskinbench";
        private const string LogPrefix = "QUEST NPC SKIN BENCH: ";

        [Serializable]
        internal sealed class Config
        {
            public string scenario = "";
            public string mode = "vp3c-native";
            public bool appSw = true;
            public string appSpacePoseMode = "tracking-origin";
            public bool keepOpen = true;
            public string skinRoute = "";                  // "", "smr" or "skin24": the intent's arguments choose when empty
            public string skinGather = "";                 // "", "sync" or "worker" (SKIN-BENCH-3): the palette gathering; the intent's -zantetsuSkinGather when empty
            public int skinGatherWorkerFrom = 0;           // SKIN-BENCH-3 (TL review): dirty individuals from which the worker gathers (0: always)
            public bool skin24SliceSelection = false;      // SKIN-BENCH-3: the skin draw's casters select by slice on Vulkan (-zantetsuSkin24SliceSelection)
            public int npcSkinBenchUpdateCount = -1;       // SKIN-BENCH-3: individuals posed each frame under update (-1 every one)
            public int npcSkinBenchCount = NpcSkinBenchSettings.DefaultCount;
            public string npcSkinBenchPose = "update";
            public string npcSkinBenchShadows = "cascade4";
            public int npcSkinBenchFamilies = NpcSkinBenchSettings.DefaultFamilies;
            public float npcSkinBenchWarmupSeconds = NpcSkinBenchSettings.DefaultWarmupSeconds;
            public float npcSkinBenchMeasureSeconds = NpcSkinBenchSettings.DefaultMeasureSeconds;
            public int npcSkinBenchMeasureFrames = 900;      // SKIN-BENCH-2: 20 s of 45 Hz frames, the same frames and steps in every launch (0: by seconds)
            public int npcSkinBenchSettleFrames = NpcSkinBenchSettings.DefaultSettleFrames;
            public double npcSkinBenchStepSeconds = NpcSkinBenchSettings.DefaultStepSeconds;
            public float npcSkinBenchPoseToleranceMetres = NpcSkinBenchSettings.DefaultToleranceMetres;
            public float npcSkinBenchPoseToleranceDegrees = NpcSkinBenchSettings.DefaultToleranceDegrees;
            public bool npcSkinBenchFixedView = false;       // the rig counter-moves the head: one view for every pass
            public int npcSkinBenchPaletteJoinGap = 0;       // diagnosis (SKIN-BENCH-2 section 6)
            public bool npcSkinBenchProfiler = false;      // diagnosis: the Profiler's binary log of the measurement (not a sample)
            public bool npcSkinBenchPicture = true;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private Config _config;
        private NpcSkinBenchSettings _settings;
        private NpcSkinBenchRunner _runner;
        private Camera _camera;
        private Transform _appSpaceOrigin;
        private bool _spaceWarpApplied, _recorded;
        private float _nextStatus;
        private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();

        /// <summary>For the tests: the settings a configuration text gives (null: no file), or the failure.</summary>
        internal static NpcSkinBenchSettings SettingsOf(string json, string directory, out string failure)
        {
            Config config = string.IsNullOrEmpty(json) ? new Config() : JsonUtility.FromJson<Config>(json) ?? new Config();
            return SettingsOf(config, directory, out failure);
        }

        private static NpcSkinBenchSettings SettingsOf(Config config, string directory, out string failure)
        {
            failure = null;
            var settings = new NpcSkinBenchSettings
            {
                directory = directory, count = config.npcSkinBenchCount, families = config.npcSkinBenchFamilies,
                warmupSeconds = config.npcSkinBenchWarmupSeconds, measureSeconds = config.npcSkinBenchMeasureSeconds, stepSeconds = config.npcSkinBenchStepSeconds,
                measureFrames = config.npcSkinBenchMeasureFrames, settleFrames = config.npcSkinBenchSettleFrames,
                toleranceMetres = config.npcSkinBenchPoseToleranceMetres, toleranceDegrees = config.npcSkinBenchPoseToleranceDegrees,
                fixedView = config.npcSkinBenchFixedView, paletteJoinGap = config.npcSkinBenchPaletteJoinGap, updateCount = config.npcSkinBenchUpdateCount,
                keepOpen = config.keepOpen, picture = config.npcSkinBenchPicture,
                profilerFile = config.npcSkinBenchProfiler && directory != null ? Path.Combine(directory, "profile.raw") : null,
            };
            if (!NpcSkinBenchSettings.TryParsePose(config.npcSkinBenchPose, out settings.pose)) { failure = "npcSkinBenchPose must be stop or update"; return null; }
            if (!NpcSkinBenchSettings.TryParseShadows(config.npcSkinBenchShadows, out settings.shadows)) { failure = "npcSkinBenchShadows must be off, cascade1 or cascade4"; return null; }
            if (config.mode != "vp3c" && config.mode != "vp3c-native") { failure = "mode must be vp3c or vp3c-native"; return null; }
            if (config.appSpacePoseMode != "camera" && config.appSpacePoseMode != "tracking-origin") { failure = "appSpacePoseMode must be camera or tracking-origin"; return null; }
            if (config.skinRoute != "" && config.skinRoute != "smr" && config.skinRoute != "skin24") { failure = "skinRoute must be empty, smr or skin24"; return null; }
            if (config.skinGather != "" && !Zantetsu.Rendering.VpSkinGatherSetup.TryParse(config.skinGather, out _)) { failure = "skinGather must be empty, sync or worker"; return null; }
            failure = settings.Validate();
            return failure == null ? settings : null;
        }

        /// <summary>The Quest's entry (SandboxScenario, after the scene is loaded and before anything of it starts).</summary>
        internal static string StartForScenario()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != SceneName) return "npc-skin-bench needs the scene " + SceneName + "; this is " + scene;
            string file = Path.Combine(Application.persistentDataPath, SandboxScenario.ConfigFile);
            string json = File.Exists(file) ? File.ReadAllText(file) : null;
            Config config = string.IsNullOrEmpty(json) ? new Config() : JsonUtility.FromJson<Config>(json) ?? new Config();
            // The configuration may select the skin route where the intent's arguments did not (the same choice).
            if (config.skinRoute == "smr") Zantetsu.Rendering.VpSkinDrawSetup.Request = Zantetsu.Rendering.VpSkinDrawRequest.Renderers;
            else if (config.skinRoute == "skin24") Zantetsu.Rendering.VpSkinDrawSetup.Request = Zantetsu.Rendering.VpSkinDrawRequest.SkinDraw;
            // SKIN-BENCH-3: the gathering and the skin casters' slice selection, before the world is made (both are read at its making).
            if (config.skinGather != "" && Zantetsu.Rendering.VpSkinGatherSetup.TryParse(config.skinGather, out Zantetsu.Rendering.VpSkinGatherMode gatherMode)) Zantetsu.Rendering.VpSkinGatherSetup.Mode = gatherMode;
            if (config.skinGatherWorkerFrom > 0) Zantetsu.Rendering.VpSkinGatherSetup.WorkerFrom = config.skinGatherWorkerFrom;
            if (config.skin24SliceSelection) Zantetsu.Rendering.VpGpuCullSetup.Skin24SliceSelectionOnVulkan = true;
            string route = Zantetsu.Rendering.VpSkinDrawSetup.Request == Zantetsu.Rendering.VpSkinDrawRequest.SkinDraw ? "skin24"
                : Zantetsu.Rendering.VpSkinDrawSetup.Request == Zantetsu.Rendering.VpSkinDrawRequest.Renderers ? "smr" : "noroute";
            string directory = Path.Combine(Application.persistentDataPath, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-skinbench-" + route + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            NpcSkinBenchSettings settings = SettingsOf(config, directory, out string failure);
            if (settings == null) return "the configuration is not usable: " + failure;
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "quest-config.json"), json ?? "(no " + SandboxScenario.ConfigFile + ": every default)");
            NpcSkinBenchRunner.Arm(settings);
            var entry = new GameObject("Quest NPC skin bench").AddComponent<QuestNpcSkinBench>();
            entry._config = config;
            entry._settings = settings;
            Debug.Log(LogPrefix + "armed: " + settings.Describe() + "; skin route " + route + " (" + (config.skinRoute != "" ? "configuration skinRoute" : "the intent's arguments") + "); mode " + config.mode + "; AppSW " + config.appSw);
            return null;
        }

        private void Start()
        {
            _camera = Camera.main;
            _appSpaceOrigin = _camera != null ? _camera.transform.parent : null;
            _runner = FindAnyObjectByType<NpcSkinBenchRunner>(FindObjectsInactive.Include);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;   // the OpenXR display and AppSW select the cadence
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
        }

        private void Update()
        {
            if (_config == null) return;
            if (!_spaceWarpApplied) _spaceWarpApplied = SpaceWarpFeature.SetSpaceWarp(_config.appSw);
            if (_runner != null && !_recorded && (_runner.Current == NpcSkinBenchRunner.Phase.Done || _runner.Current == NpcSkinBenchRunner.Phase.Failed)) RecordPlatform();
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
            QuestDisplayRefreshRateFeature.TryReadReported(out float physicalRefresh);
            return "phase=" + (_runner != null ? _runner.Current.ToString() : "no runner") + " step=" + (_runner != null ? _runner.Step : 0)
                + " AppSWRequested=" + _config.appSw + " AppSWConfigApplied=" + _spaceWarpApplied + " appSpace=" + _config.appSpacePoseMode
                + " displayHz=" + physicalRefresh + " xrReportedHz=" + refresh + " appGpuMs=" + (gpu * 1000f).ToString("F2", Inv) + " stereo=" + XRSettings.stereoRenderingMode;
        }

        // The AppSW state beside the benchmark's results, written once at its end.
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
                    Status(), "runnerExitCode=" + (_runner != null ? _runner.ExitCode : -1), "runnerValid=" + (_runner != null && _runner.Valid),
                });
            }
            catch (Exception e) { Debug.LogError(LogPrefix + "the platform record could not be written: " + e.Message); }
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || _config == null || !_config.appSw || camera != _camera) return;
            // The application space's pose for XR_FB_space_warp: the tracking origin's (the rig's), or the camera's.
            Transform appSpace = _config.appSpacePoseMode == "tracking-origin" && _appSpaceOrigin != null ? _appSpaceOrigin : camera.transform;
            SpaceWarpFeature.SetAppSpacePosition(appSpace.position);
            SpaceWarpFeature.SetAppSpaceRotation(appSpace.rotation);
        }
    }
}
