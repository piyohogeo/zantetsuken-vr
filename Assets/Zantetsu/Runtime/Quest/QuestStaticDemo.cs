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
            public string mode = "vp3c";
            public string pathFile;
            public string geometryFile;
            public int warmupLaps = 1;
            public bool appSw = true;
            public bool profiler = false;
            public bool quitWhenDone = false;
            public bool headTracked = true;
        }
        private Config _config;
        private Camera _camera;
        private SandboxDrawComparePlayerCheck.Runner _runner;
        private bool _spaceWarpApplied;
        private float _nextStatus;
        private readonly System.Collections.Generic.List<XRDisplaySubsystem> _displays = new System.Collections.Generic.List<XRDisplaySubsystem>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.SceneManagement.SceneManager.GetActiveScene().name.StartsWith("QuestStaticSeed14")) return;
            // Hold all registration and activity before any Start, even while StreamingAssets are being extracted.
            Debug.Log("QUEST STATIC: " + SandboxDrawComparePlayerCheck.QuietScene());
            new GameObject("Quest static comparison").AddComponent<QuestStaticDemo>();
#endif
        }

        private IEnumerator Start()
        {
            _camera = Camera.main;
            if (_camera == null) throw new InvalidOperationException("Quest static demo requires MainCamera");
            if (GraphicsSettings.currentRenderPipeline == null || GraphicsSettings.currentRenderPipeline.name != "Quest_RPAsset")
                throw new InvalidOperationException("Quest static demo requires Quest_RPAsset; scenario pipeline was not configured");
            string configPath = Path.Combine(Application.persistentDataPath, "quest3s-config.json");
            _config = File.Exists(configPath) ? JsonUtility.FromJson<Config>(File.ReadAllText(configPath)) : new Config();
            if (_config == null || (_config.mode != "unity" && _config.mode != "vp3c")) throw new InvalidOperationException("mode must be unity or vp3c");
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
                mode = _config.mode == "unity" ? SandboxDrawComparePlayerCheck.Mode.Unity : SandboxDrawComparePlayerCheck.Mode.Vp3c,
                warmupLaps = _config.warmupLaps,
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
            if (Time.unscaledTime < _nextStatus) return;
            _nextStatus = Time.unscaledTime + 5f;
            _displays.Clear();
            SubsystemManager.GetSubsystems(_displays);
            foreach (var xr in _displays)
            {
                xr.TryGetDisplayRefreshRate(out float refresh);
                xr.TryGetAppGPUTimeLastFrame(out float gpu);
                QuestDisplayRefreshRateFeature.TryReadReported(out float physicalRefresh);
                Debug.Log("QUEST STATIC: mode=" + _config.mode + " camera=" + (_config.headTracked ? "head" : "path") + " phase=" + (_runner != null ? _runner.Phase.ToString() : "Loading") + " displayHz=" + physicalRefresh + " xrReportedHz=" + refresh + " msaa=" + ((UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline).msaaSampleCount + " AppSWApplied=" + _spaceWarpApplied + " dtMs=" + Time.unscaledDeltaTime * 1000f + " appGpuMs=" + gpu * 1000f + " stereo=" + XRSettings.stereoRenderingMode);
            }
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || !_config.appSw || camera != _camera) return;
            SpaceWarpFeature.SetAppSpacePosition(camera.transform.position);
            SpaceWarpFeature.SetAppSpaceRotation(camera.transform.rotation);
        }
        private void OnDestroy() => RenderPipelineManager.beginCameraRendering -= BeforeCamera;
    }
}
