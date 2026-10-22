using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>The tiled-seed14 static comparison: the PC runner, Android inputs, 90 Hz display and 45 fps AppSW.</summary>
    public sealed class QuestStaticDemo : MonoBehaviour
    {
        [Serializable] private sealed class Config
        {
            public string scenario = "";               // DESIGN 3.4: read by SandboxScenario (play without it); kept here so that the run's record of its configuration carries it
            public string mode = "vp3c";
            public string pathFile;
            public string geometryFile;
            public int warmupLaps = 1;
            public int sceneObjectLimit = 0;
            public bool appSw = true;
            public bool nativeColourPreparation = true;
            public bool directClipProbe = false;
            public bool nativeNoClipColour = true;
            public bool nativeNoClipMotion = true;
            public bool nativeNoClipShadows = true;
            public bool nativeShareGpuConstants = true;
            public bool nativeIndirectCount = true;    // DESIGN 4.5.10 (D-217): the command compaction is asked for by default; false keeps the fixed draw
            public bool nativeIndirectCountSelfTest = false; // diagnosis: the wave kernel's self-test with a readback, before the run; a failure keeps the fixed draw
            public int gpuStopStage = 0;               // diagnosis (unit 8-C): VpGpuStop stage 0..4, applied before the scene loads; never a product option
            public bool nativeIndirectCountVerify = false;   // the experiment's GPU check of every compaction, read back for the log (a check run, not a measurement)
            public bool nativeIndirectCountDrill = false;    // the experiment's rest-and-resume drill: the camera, the shadows and the motion pass stopped for a few frames in turn (a check run)
            public int nativeIndirectCountFirstSlot = 0;     // diagnosis (2026-10-10): the count slot a pass's first run takes; 1 draws the scene's one run from countBufferOffset 4 (a check run)
            public bool profiler = false;
            public bool quitWhenDone = false;
            public bool headTracked = true;
        }
        private Config _config;
        private Camera _camera;
        private Transform _appSpaceOrigin;
        private SandboxDrawComparePlayerCheck.Runner _runner;
        private bool _spaceWarpApplied;
        private float _nextStatus;
        private readonly System.Collections.Generic.List<XRDisplaySubsystem> _displays = new System.Collections.Generic.List<XRDisplaySubsystem>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SelectNativeRoute()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Zantetsu.Core.PlayerPlatform.IsAndroidFlat) return;
            // The static scene's own route, where the launch's scenario is the static one (DESIGN 3.4): the other
            // scenarios read theirs in QuestNpcSlashDemo. It was read here whatever ran while each had its own package.
            SandboxScenarioChoice choice = SandboxScenario.Current;
            if (choice.Failure != null || choice.Kind != SandboxScenarioKind.StaticWait) return;
            string file = Path.Combine(Application.persistentDataPath, "quest3s-config.json");
            if (!File.Exists(file)) return;
            Config early = JsonUtility.FromJson<Config>(File.ReadAllText(file));
            Zantetsu.Rendering.VpNativeDrawSetup.Requested = early?.mode == "vp3c-native";
            // The stop stage (unit 8-C) must be in force before CutWorldRoot.Build makes the display at scene load.
            if (early != null && early.gpuStopStage != 0)
            {
                Zantetsu.Rendering.VpGpuStop.Set(early.gpuStopStage);
                Debug.Log("QUEST STATIC: " + Zantetsu.Rendering.VpGpuStop.Describe());
                // Stage 1: the scene's own renderers are held off when the scene has loaded, before its first frame (the
                // inventory still lists them). sceneLoaded comes after the scene objects' Awake and before any render.
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += HoldSceneRenderersForStop;
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void HoldSceneRenderersForStop(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            int held = Zantetsu.Rendering.VpGpuStop.SuppressSceneRenderers();
            Debug.Log("QUEST STATIC: stop stage " + Zantetsu.Rendering.VpGpuStop.Stage + ": " + held + " renderers of scene '" + scene.name + "' held off before its first frame");
        }
#endif

        /// <summary>
        /// The Quest's entry for the static scene (DESIGN 3.4), called by <see cref="SandboxScenario"/> once the
        /// scenario's scene is loaded and before anything of it starts. It no longer starts by itself on the scene's
        /// name: the name is checked against the scenario, and another scene is a failure, answered in words. Null
        /// when the demo was made.
        /// </summary>
        internal static string StartForScenario()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (!scene.StartsWith("QuestStaticSeed14")) return "static-wait needs a scene named QuestStaticSeed14...; this is " + scene;
            // Hold all registration and activity before any Start, even while StreamingAssets are being extracted.
            Debug.Log("QUEST STATIC: " + SandboxDrawComparePlayerCheck.QuietScene());
            new GameObject("Quest static comparison").AddComponent<QuestStaticDemo>();
            return null;
        }

        private IEnumerator Start()
        {
            _camera = Camera.main;
            if (_camera == null) throw new InvalidOperationException("Quest static demo requires MainCamera");
            _appSpaceOrigin = _camera.transform.parent;
            if (_appSpaceOrigin == null) throw new InvalidOperationException("Quest static demo requires the tracked camera parent");
            if (GraphicsSettings.currentRenderPipeline == null || GraphicsSettings.currentRenderPipeline.name != "Quest_RPAsset")
                throw new InvalidOperationException("Quest static demo requires Quest_RPAsset; scenario pipeline was not configured");
            string configPath = Path.Combine(Application.persistentDataPath, "quest3s-config.json");
            _config = File.Exists(configPath) ? JsonUtility.FromJson<Config>(File.ReadAllText(configPath)) : new Config();
            if (_config == null || (_config.mode != "unity" && _config.mode != "vp3c" && _config.mode != "vp3c-native")) throw new InvalidOperationException("mode must be unity, vp3c or vp3c-native");
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanColourPreparation = _config.nativeColourPreparation;
            Debug.Log("QUEST STATIC: nativeColourPreparation=" + _config.nativeColourPreparation);
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanDirectClipProbe = _config.directClipProbe;
            Debug.Log("QUEST STATIC: directClipProbe=" + _config.directClipProbe);
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanNoClipColourShaders = _config.nativeNoClipColour;
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanNoClipMotionShaders = _config.nativeNoClipMotion;
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanNoClipShadowShaders = _config.nativeNoClipShadows;
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanShareGpuConstants = _config.nativeShareGpuConstants;
            Debug.Log("QUEST STATIC: noClipColour=" + _config.nativeNoClipColour + " noClipMotion=" + _config.nativeNoClipMotion
                + " noClipShadows=" + _config.nativeNoClipShadows + " shareGpuConstants=" + _config.nativeShareGpuConstants);
            Zantetsu.Rendering.VpNativeDrawPlugin.VulkanIndirectCount = _config.nativeIndirectCount;
            Zantetsu.Rendering.VpCommandCompaction.VerificationEnabled = _config.nativeIndirectCount && _config.nativeIndirectCountVerify;
            Zantetsu.Rendering.VpCommandCompaction.FirstSlot = _config.nativeIndirectCount ? _config.nativeIndirectCountFirstSlot : 0;
            Debug.Log("QUEST STATIC: " + Zantetsu.Rendering.VpGpuStop.Describe() + " (configured gpuStopStage=" + _config.gpuStopStage + ")");
            Debug.Log("QUEST STATIC: indirectCount=" + _config.nativeIndirectCount + " verify=" + Zantetsu.Rendering.VpCommandCompaction.VerificationEnabled
                + " firstSlot=" + Zantetsu.Rendering.VpCommandCompaction.FirstSlot);
            if (_config.nativeIndirectCount && _config.nativeIndirectCountSelfTest)
            {
                // Diagnosis only (a readback): does the wave kernel run on this device as written? It decides no draw.
                bool compacts = Zantetsu.Rendering.VpCommandCompaction.SelfTest(out string selfTest);
                Debug.Log("QUEST STATIC: " + selfTest);
                bool ready = Zantetsu.Rendering.VpNativeDrawPlugin.TryLoad(out string pluginFailure) && Zantetsu.Rendering.VpNativeDrawPlugin.IndirectCountReady
                    && Zantetsu.Rendering.VpNativeDrawPlugin.ZeroEventId != 0;
                if (!compacts || !ready)
                {
                    // Not chosen without its checks: the fixed draw instead, and the run says so (its result is the fixed draw's).
                    Zantetsu.Rendering.VpNativeDrawPlugin.VulkanIndirectCount = false;
                    Zantetsu.Rendering.VpCommandCompaction.VerificationEnabled = false;
                    Debug.LogError("QUEST STATIC: indirectCount asked but NOT taken: self-test " + (compacts ? "passed" : "FAILED") + ", plugin count draw ready " + ready
                        + (pluginFailure != null ? " (" + pluginFailure + ")" : "") + "; the fixed draw is used");
                }
            }
            string pathText = !string.IsNullOrEmpty(_config.pathFile) ? File.ReadAllText(_config.pathFile)
                : Resources.Load<TextAsset>("Quest3sStaticDemoPath").text;
            if (!SceneDrawComparePath.TryParse(pathText, out var path, out string failure)) throw new InvalidOperationException(failure);
            string geometryPath = _config.geometryFile;
            if (string.IsNullOrEmpty(geometryPath))
            {
                geometryPath = Path.Combine(Application.persistentDataPath, "scene-geometry.vpdc");
                // Extract on each launch so an APK update cannot leave an old bank in persistent storage.
                using (var request = UnityWebRequest.Get(Application.streamingAssetsPath + "/Quest3s/scene-geometry.vpdc"))
                {
                    request.downloadHandler = new DownloadHandlerFile(geometryPath);
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success) throw new IOException(request.error);
                }
            }
            Application.targetFrameRate = -1; // OpenXR AppSW controls cadence; 45 targetFrameRate alone is insufficient.
            string directory = Path.Combine(Application.persistentDataPath, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + _config.mode + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(directory);
            File.WriteAllLines(Path.Combine(directory, "platform.txt"), new[] { "buildGUID=" + Application.buildGUID, "unity=" + Application.unityVersion, "device=" + SystemInfo.deviceModel, "graphics=" + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName, "MSAA requested=" + ((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline).msaaSampleCount, "AppSW requested=" + _config.appSw + "; display requested=90 Hz; verify QUEST STATIC and VrApi logcat for actual cadence" });
            File.WriteAllText(Path.Combine(directory, "quest-config.json"), JsonUtility.ToJson(_config, true));
            File.WriteAllText(Path.Combine(directory, "path.txt"), pathText);
            // The shared runner requires its own initially empty output directory.
            var settings = new SandboxDrawComparePlayerCheck.Settings
            {
                directory = Path.Combine(directory, "comparison"), path = path, geometryFile = geometryPath,
                mode = _config.mode == "unity" ? SandboxDrawComparePlayerCheck.Mode.Unity : _config.mode == "vp3c-native" ? SandboxDrawComparePlayerCheck.Mode.Vp3cNative : SandboxDrawComparePlayerCheck.Mode.Vp3c,
                warmupLaps = _config.warmupLaps,
                sceneObjectLimit = Math.Max(0, _config.sceneObjectLimit),
                headTracked = _config.headTracked, retainSceneAfterMeasurement = !_config.quitWhenDone,
                profilerFile = _config.profiler ? Path.Combine(directory, "profile.raw") : null,
            };
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
            Debug.Log("QUEST STATIC: mode=" + _config.mode + " AppSW=" + _config.appSw + " output=" + directory + " geometry=" + geometryPath);
            _runner = SandboxDrawComparePlayerCheck.StartPrepared(settings, _config.quitWhenDone);
        }

        private void Update()
        {
            if (_config == null) return;
            if (!_spaceWarpApplied) _spaceWarpApplied = SpaceWarpFeature.SetSpaceWarp(_config.appSw);
            if (_config.nativeIndirectCountDrill) StepDrill();
            if (Zantetsu.Core.GamePause.UnscaledTime < _nextStatus) return;
            _nextStatus = Zantetsu.Core.GamePause.UnscaledTime + 5f;
            _displays.Clear();
            SubsystemManager.GetSubsystems(_displays);
            foreach (var xr in _displays)
            {
                xr.TryGetDisplayRefreshRate(out float refresh);
                xr.TryGetAppGPUTimeLastFrame(out float gpu);
                QuestDisplayRefreshRateFeature.TryReadReported(out float physicalRefresh);
                if (_config.mode == "vp3c-native") Debug.Log("QUEST STATIC COMPACTION: " + Zantetsu.Rendering.VpCommandCompaction.DescribeRun());
                if (Zantetsu.Rendering.VpCommandCompaction.VerificationEnabled) Debug.Log("QUEST STATIC: " + Zantetsu.Rendering.VpCommandCompaction.ReadVerification());
                if (_config.nativeIndirectCountDrill) Debug.Log("QUEST STATIC: " + DescribeDrill());
                if (_config.mode == "vp3c-native" && Zantetsu.Rendering.VpNativeDrawPlugin.TryLoad(out _)) Debug.Log("QUEST NATIVE: " + Zantetsu.Rendering.VpNativeDrawPlugin.ReadCounters() + "; " + Zantetsu.Rendering.VpNativeDrawPlugin.DescribeState());
                Debug.Log("QUEST STATIC: mode=" + _config.mode + " camera=" + (_config.headTracked ? "head" : "path") + " phase=" + (_runner != null ? _runner.Phase.ToString() : "Loading") + " displayHz=" + physicalRefresh + " xrReportedHz=" + refresh + " msaa=" + ((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline).msaaSampleCount + " AppSWRequested=" + _config.appSw + " AppSWConfigApplied=" + _spaceWarpApplied + " dtMs=" + Zantetsu.Core.GamePause.UnscaledDeltaTime * 1000f + " appGpuMs=" + gpu * 1000f + " stereo=" + XRSettings.stereoRenderingMode);
            }
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || !_config.appSw || camera != _camera) return;
            // The parent includes scripted rig locomotion and excludes the tracked HMD local pose.
            SpaceWarpFeature.SetAppSpacePosition(_appSpaceOrigin.position);
            SpaceWarpFeature.SetAppSpaceRotation(_appSpaceOrigin.rotation);
        }
        // ---- the rest-and-resume drill of the indirect-count experiment (2026-10-10) --------------------------------
        // In the existing scene and path: every 13 drawn frames one thing is stopped for 1, 2, 3, 4, 5, 6, 7, 9 or 13
        // frames and started again -- the camera (no pass of the route runs), then the main light's shadows (no caster
        // pass), then AppSW (no motion pass) -- so that a compaction set of the four comes back after every length of
        // rest, with and without a zeroing in between. What it proves is read from the GPU check's tallies and the
        // route's counters; the picture blinks on purpose.
        private static readonly int[] k_drillLengths = { 1, 2, 3, 4, 5, 6, 7, 9, 13 };
        private static readonly string[] k_drillKinds = { "camera", "shadows", "motion" };
        private readonly int[] _drillPauses = new int[3];
        private readonly int[] _drillPausedFrames = new int[3];
        private int _drillStep, _drillFramesLeft, _drillKind, _drillLength;
        private bool _drillPaused;
        private Light _drillSun;
        private LightShadows _drillSunShadows;

        private void StepDrill()
        {
            bool drawing = _runner != null && (_runner.Phase == SandboxDrawComparePlayerCheck.Phase.WarmingUp || _runner.Phase == SandboxDrawComparePlayerCheck.Phase.Measuring);
            if (!drawing)
            {
                if (_drillPaused) EndDrillPause();
                return;
            }

            if (_drillFramesLeft > 0)
            {
                _drillFramesLeft--;
                return;
            }

            if (_drillPaused)
            {
                EndDrillPause();
                _drillFramesLeft = 12;
                return;
            }

            _drillKind = (_drillStep / k_drillLengths.Length) % 3;
            _drillLength = k_drillLengths[_drillStep % k_drillLengths.Length];
            _drillStep++;
            switch (_drillKind)
            {
                case 0:
                    _camera.enabled = false;
                    break;
                case 1:
                    if (_drillSun == null)
                    {
                        _drillSun = RenderSettings.sun;
                        if (_drillSun == null)
                            foreach (Light light in FindObjectsByType<Light>(FindObjectsSortMode.None))
                                if (light.type == LightType.Directional && light.shadows != LightShadows.None) { _drillSun = light; break; }
                        if (_drillSun != null) _drillSunShadows = _drillSun.shadows;
                    }

                    if (_drillSun != null) _drillSun.shadows = LightShadows.None;
                    break;
                default:
                    SpaceWarpFeature.SetSpaceWarp(false);
                    break;
            }

            _drillPaused = true;
            _drillFramesLeft = _drillLength - 1;
        }

        private void EndDrillPause()
        {
            switch (_drillKind)
            {
                case 0: _camera.enabled = true; break;
                case 1: if (_drillSun != null) _drillSun.shadows = _drillSunShadows; break;
                default: SpaceWarpFeature.SetSpaceWarp(_config.appSw); break;
            }

            _drillPauses[_drillKind]++;
            _drillPausedFrames[_drillKind] += _drillLength;
            _drillPaused = false;
        }

        private string DescribeDrill()
        {
            return "drill: pauses done camera " + _drillPauses[0] + " (" + _drillPausedFrames[0] + " frames), shadows " + _drillPauses[1] + " (" + _drillPausedFrames[1]
                + " frames, light " + (_drillSun != null ? _drillSun.name : "not found yet") + "), motion " + _drillPauses[2] + " (" + _drillPausedFrames[2]
                + " frames); lengths 1,2,3,4,5,6,7,9,13 frames in turn, 13 frames drawn between; frame " + Time.frameCount;
        }

        private void OnDestroy()
        {
            if (_config != null && _config.nativeIndirectCountDrill) Debug.Log("QUEST STATIC: at the end, " + DescribeDrill());
            if (_config != null && _config.mode == "vp3c-native") Debug.Log("QUEST STATIC COMPACTION: at the end, " + Zantetsu.Rendering.VpCommandCompaction.DescribeRun());
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            if (Zantetsu.Rendering.VpCommandCompaction.VerificationEnabled) Debug.Log("QUEST STATIC: at the end, " + Zantetsu.Rendering.VpCommandCompaction.ReadVerification());
        }
    }
}
