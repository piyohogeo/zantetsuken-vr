using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Zantetsu.Core;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>
    /// DIAGNOSIS ONLY (unit 22, 2026-10-17): the empty XR scene's entry on the Quest. The scene holds the project's XR
    /// rig and a directional light and nothing of the game; this keeps the XR and URP conditions the static scene runs
    /// under -- AppSW on and the app space set from the tracked camera's parent before each camera render, as
    /// <see cref="QuestStaticDemo"/> does, the frame cadence left to OpenXR -- and says every 5 s and at each session
    /// change what the effective conditions are (pipeline, MSAA, eye texture, stereo mode, display Hz, AppSW applied,
    /// GPU time) and what of the game's session installers stood down. Nothing else is made.
    /// </summary>
    public sealed class QuestEmptyXrDiagnosis : MonoBehaviour
    {
        public const string Prefix = "QUEST EMPTY XR: ";
        public const string SceneName = "QuestEmptyXr";

        [Serializable]
        private sealed class Config
        {
            public string scenario = "";
            public bool appSw = true;   // the static scene's default: AppSW requested
        }

        private Config _config;
        private Camera _camera;
        private Transform _appSpaceOrigin;
        private bool _spaceWarpApplied;
        private float _nextStatus;
        private int _sessionState = -1;
        private readonly System.Collections.Generic.List<XRDisplaySubsystem> _displays = new System.Collections.Generic.List<XRDisplaySubsystem>();

        /// <summary>
        /// Before anything is made (SubsystemRegistration): when the launch's configuration names the empty XR scene, the
        /// whole Player is told, so that the game's session installers stand down (<see cref="LaunchDiagnosis"/>).
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void MarkLaunch()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Zantetsu.Core.PlayerPlatform.IsAndroidFlat) return;
            SandboxScenarioChoice choice = SandboxScenario.Current;
            if (choice.Failure == null && choice.Kind == SandboxScenarioKind.EmptyXr)
            {
                LaunchDiagnosis.MarkEmptyXrScene("quest3s-config.json scenario empty-xr");
                Debug.Log(Prefix + LaunchDiagnosis.Describe());
            }
#endif
        }

        /// <summary>The scenario's entry (<see cref="SandboxScenario"/>): the scene must be the empty one; a failure in words, or null.</summary>
        internal static string StartForScenario()
        {
            string scene = SceneManager.GetActiveScene().name;
            if (scene != SceneName) return "empty-xr needs the scene " + SceneName + "; this is " + scene;
            if (!LaunchDiagnosis.EmptyXrScene) return "the empty XR scene was not marked before the scene loaded (the configuration was read late)";
            new GameObject("Quest empty XR diagnosis").AddComponent<QuestEmptyXrDiagnosis>();
            return null;
        }

        private void Start()
        {
            _camera = Camera.main;
            if (_camera == null) throw new InvalidOperationException("the empty XR scene requires MainCamera");
            _appSpaceOrigin = _camera.transform.parent;
            if (_appSpaceOrigin == null) throw new InvalidOperationException("the empty XR scene requires the tracked camera's parent (Camera Offset)");
            string configPath = Path.Combine(Application.persistentDataPath, "quest3s-config.json");
            _config = File.Exists(configPath) ? JsonUtility.FromJson<Config>(File.ReadAllText(configPath)) ?? new Config() : new Config();
            // As the static scene: the cadence is OpenXR's (AppSW), the app space is the rig's offset before each render.
            Application.targetFrameRate = -1;
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
            Debug.Log(Prefix + "started in " + SceneManager.GetActiveScene().name + ": appSw requested=" + _config.appSw + "; " + LaunchDiagnosis.Describe());
            Debug.Log(Prefix + DescribeScene());
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
        }

        private void Update()
        {
            if (_config == null) return;
            if (!_spaceWarpApplied && _config.appSw) _spaceWarpApplied = SpaceWarpFeature.SetSpaceWarp(true);
            int state = XrSessionStateReport.State;
            bool changed = state != _sessionState;
            if (changed) _sessionState = state;
            if (!changed && Time.realtimeSinceStartup < _nextStatus) return;
            _nextStatus = Time.realtimeSinceStartup + 5f;
            Debug.Log(Prefix + (changed ? "session " + XrSessionStateReport.Name + " " : "status ") + DescribeConditions());
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || !_config.appSw || camera != _camera) return;
            SpaceWarpFeature.SetAppSpacePosition(_appSpaceOrigin.position);
            SpaceWarpFeature.SetAppSpaceRotation(_appSpaceOrigin.rotation);
        }

        private static string F(float value) => value.ToString("F2", CultureInfo.InvariantCulture);

        /// <summary>The scene's content, once: what the diagnosis runs with.</summary>
        private static string DescribeScene()
        {
            Scene scene = SceneManager.GetActiveScene();
            int renderers = FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int lights = FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            int behaviours = FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None).Length;
            var roots = new System.Text.StringBuilder();
            foreach (GameObject root in scene.GetRootGameObjects()) roots.Append(root.name).Append(", ");
            return "scene " + scene.name + ": roots [" + roots + "] renderers=" + renderers + " lights=" + lights + " behaviours=" + behaviours
                + " skybox=" + (RenderSettings.skybox != null ? RenderSettings.skybox.name : "none") + " fog=" + RenderSettings.fog
                + " ambient=" + RenderSettings.ambientMode + " skyFog=" + (Zantetsu.Rendering.VpSkyFog.Enabled ? "on" : "off (" + Zantetsu.Rendering.VpSkyFog.OffReason + ")");
        }

        /// <summary>The effective XR and URP conditions now (requested values are the configuration's; these are read back).</summary>
        private string DescribeConditions()
        {
            var pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
            float refresh = 0f, gpu = 0f;
            _displays.Clear();
            SubsystemManager.GetSubsystems(_displays);
            foreach (XRDisplaySubsystem xr in _displays)
            {
                xr.TryGetDisplayRefreshRate(out refresh);
                xr.TryGetAppGPUTimeLastFrame(out gpu);
            }

            QuestDisplayRefreshRateFeature.TryReadReported(out float physicalRefresh);
            return "pipeline=" + (pipeline != null ? pipeline.name : GraphicsSettings.currentRenderPipeline != null ? GraphicsSettings.currentRenderPipeline.name : "built-in")
                + " msaaAsset=" + (pipeline != null ? pipeline.msaaSampleCount : -1) + " msaaQuality=" + QualitySettings.antiAliasing
                + " renderScale=" + (pipeline != null ? F(pipeline.renderScale) : "-") + " hdr=" + (pipeline != null && pipeline.supportsHDR)
                + " eye=" + XRSettings.eyeTextureWidth + "x" + XRSettings.eyeTextureHeight + " stereo=" + XRSettings.stereoRenderingMode
                + " graphics=" + SystemInfo.graphicsDeviceType + " displayHz=" + F(physicalRefresh) + " xrReportedHz=" + F(refresh)
                + " appGpuMs=" + F(gpu * 1000f) + " dtMs=" + F(Time.unscaledDeltaTime * 1000f)
                + " appSwRequested=" + _config.appSw + " appSwApplied=" + _spaceWarpApplied + " targetFrameRate=" + Application.targetFrameRate
                + " xrState=" + XrSessionStateReport.Name + " restarts=" + XrSessionStateReport.Restarts
                + " | metaQuest: " + DescribeMetaQuestFeature()
                + " | " + LaunchDiagnosis.Describe();
        }

        /// <summary>The Meta Quest feature's build-time switches as this Player carries them (read by reflection from its serialized fields; a reading only).</summary>
        private static string DescribeMetaQuestFeature()
        {
            try
            {
                OpenXRSettings settings = OpenXRSettings.Instance;
                var quest = settings != null ? settings.GetFeature<UnityEngine.XR.OpenXR.Features.MetaQuestSupport.MetaQuestFeature>() : null;
                if (quest == null) return "feature not found";
                var text = new System.Text.StringBuilder("enabled=" + quest.enabled);
                foreach (string field in new[] { "m_optimizeBufferDiscards", "m_symmetricProjection", "lateLatchingMode", "spacewarpMotionVectorTextureFormat", "m_multiviewRenderRegionsOptimizationMode" })
                {
                    System.Reflection.FieldInfo info = quest.GetType().GetField(field, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    text.Append(' ').Append(field.TrimStart('m', '_')).Append('=').Append(info != null ? (info.GetValue(quest) ?? "null").ToString() : "?");
                }

                return text.ToString();
            }
            catch (Exception e)
            {
                return "unknown (" + e.GetType().Name + ")";
            }
        }
    }
}
