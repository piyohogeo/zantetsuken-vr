using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using Zantetsu.Core;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>Ten prepared NPCs cut by the recorded Slash input, with a live Quest head and camera-only AppSW motion.</summary>
    public sealed class QuestNpcSlashDemo : MonoBehaviour
    {
        public const string SceneName = "QuestNpcSlash10";
        public const string PackageName = "com.junic.zantetsu.questnpcslash";

        [Serializable] private sealed class Config
        {
            public string mode = "vp3c-native";
            public bool appSw = true;
            public string appSpacePoseMode = "tracking-origin"; // Camera pose is retained only for the controlled comparison.
            public bool profiler = true;
            public bool prewarm = true;
            public string inputFile;
            public int inputStart = 2227;
            public int inputRows = 512;
            public float startDelaySeconds = 5f;
            public bool keepOpen = true;
            public bool nativeColourPreparation = true;
            public bool nativeNoClipColour = true;
            public bool nativeNoClipMotion = true;
            public bool nativeNoClipShadows = true;
            public bool nativeShareGpuConstants = true;
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private Config _config;
        private Camera _camera;
        private Transform _appSpaceOrigin;
        private Quaternion _lastHeadRotation, _lastAppSpaceRotation;
        private bool _appSpacePoseSampled;
        private float _headAngleMax, _appSpaceAngleMax;
        private CutWorldRoot _world;
        private CutWorldCameraDrawing _drawing;
        private SandboxPropSlashPlayerCheck.PreparedRun _run;
        private string _directory, _phase = "Loading";
        private bool _spaceWarpApplied, _profileStarted, _doneRecorded;
        private bool _profilerBefore, _binaryBefore;
        private string _profileFileBefore;
        private string _loggerBeginAdmission = "Disabled";
        private string _warmFailure;
        private QuestNpcUnityPrewarm _unityPrewarm;
        private bool _unityPrewarmRetireFailureLogged;
        private float _nextStatus;
        private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();

        private static Config ReadConfig()
        {
            string file = Path.Combine(Application.persistentDataPath, "quest3s-config.json");
            return File.Exists(file) ? JsonUtility.FromJson<Config>(File.ReadAllText(file)) : new Config();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SelectNativeRoute()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // The package selects the initial route before the scene's world is made; other Quest demos keep theirs.
            if (Application.identifier != PackageName) return;
            Config config = ReadConfig();
            VpNativeDrawSetup.Requested = (config ?? new Config()).mode == "vp3c-native";
            ApplyNativeOptions(config ?? new Config());
#endif
        }

        private static void ApplyNativeOptions(Config config)
        {
            VpNativeDrawPlugin.VulkanColourPreparation = config.nativeColourPreparation;
            VpNativeDrawPlugin.VulkanDirectClipProbe = false;
            VpNativeDrawPlugin.VulkanNoClipColourShaders = config.nativeNoClipColour;
            VpNativeDrawPlugin.VulkanNoClipMotionShaders = config.nativeNoClipMotion;
            VpNativeDrawPlugin.VulkanNoClipShadowShaders = config.nativeNoClipShadows;
            VpNativeDrawPlugin.VulkanShareGpuConstants = config.nativeShareGpuConstants;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (SceneManager.GetActiveScene().name != SceneName) return;
            // The direct skin input requires the same four-weight contract as the authored NPC cut data.
            foreach (var npc in UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None))
                if (npc.CharacterRoot != null)
                    foreach (var skin in npc.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skin.quality = SkinQuality.Bone4;
            // No controller input or diagnostic UI while the input asset is extracted and the NPCs prepare.
            foreach (var katana in UnityEngine.Object.FindObjectsByType<SandboxRightHandKatana>(FindObjectsSortMode.None)) katana.enabled = false;
            foreach (var input in UnityEngine.Object.FindObjectsByType<SandboxLocomotionInput>(FindObjectsSortMode.None)) input.enabled = false;
            foreach (var input in UnityEngine.Object.FindObjectsByType<MobPlanPlayerInput>(FindObjectsSortMode.None)) input.enabled = false;
            foreach (var recorder in UnityEngine.Object.FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsSortMode.None)) recorder.ShowControls = false;
            foreach (var overlay in UnityEngine.Object.FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsSortMode.None)) overlay.Visible = false;
            new GameObject("Quest ten NPC Slash demo").AddComponent<QuestNpcSlashDemo>();
#endif
        }

        private IEnumerator Start()
        {
            _config = ReadConfig();
            if (_config == null || (_config.mode != "vp3c" && _config.mode != "vp3c-native")) { Fail("mode must be vp3c or vp3c-native"); yield break; }
            if (_config.inputStart < 0 || _config.inputRows < 1 || _config.inputRows > 4096
                || !float.IsFinite(_config.startDelaySeconds) || _config.startDelaySeconds < 0f) { Fail("invalid input range or start delay"); yield break; }
            _camera = Camera.main;
            if (_camera == null) { Fail("MainCamera is required"); yield break; }
            if (_config.appSpacePoseMode != "camera" && _config.appSpacePoseMode != "tracking-origin")
            { Fail("appSpacePoseMode must be camera or tracking-origin"); yield break; }
            _appSpaceOrigin = _camera.transform.parent;
            if (_config.appSpacePoseMode == "tracking-origin" && _appSpaceOrigin == null)
            { Fail("tracking-origin requires the tracked camera parent"); yield break; }
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline) || pipeline.name != "Quest_RPAsset")
            { Fail("Quest_RPAsset is required"); yield break; }
            ApplyNativeOptions(_config);
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1; // The 90 Hz OpenXR display and AppSW select the application cadence.
            Application.runInBackground = true;
            _directory = Path.Combine(Application.persistentDataPath, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")
                + "-npc10-" + _config.mode + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(_directory);
            File.WriteAllText(Path.Combine(_directory, "quest-config.json"), JsonUtility.ToJson(_config, true));
            File.WriteAllLines(Path.Combine(_directory, "platform.txt"), new[]
            {
                "buildGUID=" + Application.buildGUID, "unity=" + Application.unityVersion, "device=" + SystemInfo.deviceModel,
                "graphics=" + SystemInfo.graphicsDeviceType + " / " + SystemInfo.graphicsDeviceName,
                "global skinWeights=" + QualitySettings.skinWeights,
                "supportsGraphicsFence=" + SystemInfo.supportsGraphicsFence + "; supportsAsyncCompute=" + SystemInfo.supportsAsyncCompute + "; warm fence=CPUSynchronisation",
                "development logger directory=" + Path.Combine(Application.persistentDataPath, "logger"),
                "MSAA requested=" + pipeline.msaaSampleCount, "AppSW requested=" + _config.appSw + "; display requested=90 Hz; application cadence verified by QUEST NPC and VrApi logs",
                "replay=recorded timestamps in real time; head tracking live; camera-only AppSW motion; object motion ignored",
                "AppSpace pose=" + _config.appSpacePoseMode + "; camera parent=" + (_appSpaceOrigin != null ? _appSpaceOrigin.name : "none"),
                "checks=on; metrics=on; light run, no picture capture; scene retained=" + _config.keepOpen,
                "profile scope=ten NPCs ready, check preparation and start delay pre-roll, replay, cut completion; replay CSV scope unchanged",
            });
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), NpcSkinQualityLines());
            RenderPipelineManager.beginCameraRendering += BeforeCamera;
            SandboxPropSlashPlayerCheck.ReplayBegun += OnReplayBegun;
            SandboxPropSlashPlayerCheck.ReplayPrepared += OnReplayPrepared;
            Application.quitting += StopProfiler;

            string inputFile = _config.inputFile;
            if (string.IsNullOrEmpty(inputFile))
            {
                inputFile = Path.Combine(Application.persistentDataPath, "NpcSlashInput.csv");
                // Refresh each launch so the persistent copy always belongs to this APK.
                using (var request = UnityWebRequest.Get(Application.streamingAssetsPath + "/Quest3s/NpcSlashInput.csv"))
                {
                    request.downloadHandler = new DownloadHandlerFile(inputFile);
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success) { Fail("input extraction: " + request.error); yield break; }
                }
            }
            if (!File.Exists(inputFile)) { Fail("input file does not exist: " + inputFile); yield break; }

            _phase = "Preparing NPCs";
            var world = _world = UnityEngine.Object.FindAnyObjectByType<CutWorldRoot>();
            _drawing = world != null ? world.GetComponent<CutWorldCameraDrawing>() : null;
            var npcs = UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None);
            if (npcs.Length != 10) { Fail("expected ten NPCs, found " + npcs.Length); yield break; }
            double readyBy = Time.unscaledTimeAsDouble + 120.0;
            while (Time.unscaledTimeAsDouble < readyBy)
            {
                bool ready = world != null && world.IsReady;
                foreach (var npc in npcs)
                {
                    if (npc.Failure != null) { Fail("NPC preparation: " + npc.name + ": " + npc.Failure); yield break; }
                    ready &= npc.IsTarget;
                }
                if (ready) break;
                yield return null;
            }
            bool allReady = world != null && world.IsReady;
            foreach (var npc in npcs) allReady &= npc.IsTarget;
            if (!allReady) { Fail("ten NPCs did not prepare within 120 s"); yield break; }

            string[] arguments =
            {
                "QuestNpcSlashDemo", SandboxPropSlashPlayerCheck.Argument, Path.Combine(_directory, "comparison"),
                SandboxSlashReplayPlayerCheck.InputArgument, inputFile,
                SandboxSlashReplayPlayerCheck.StartArgument, _config.inputStart.ToString(Inv),
                SandboxPropSlashPlayerCheck.RowsArgument, _config.inputRows.ToString(Inv),
                SandboxPropSlashPlayerCheck.FrameRateArgument, "-1", SandboxPropSlashPlayerCheck.IterationsArgument, "1",
                SandboxPropSlashPlayerCheck.MultiNpcArgument, SandboxPropSlashPlayerCheck.LightArgument,
                SandboxPropSlashPlayerCheck.MarkersArgument, "FrameEvents.XRBeginFrame;FrameEvents.OnBeforeRenderCallback;Scripts:Zantetsu.PoseLod.Decide;Scripts:Zantetsu.PoseLod.Force",
            };
            File.WriteAllLines(Path.Combine(_directory, "run-arguments.txt"), arguments);
#if DEBUG
            _loggerBeginAdmission = DevelopmentLogger.Instance.write_log("QuestNpcSlashDemo", "npc_run_begin", _directory).ToString();
#endif
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { "development logger begin enqueue=" + _loggerBeginAdmission });
            foreach (var npc in npcs)
                File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { "prepared NPC skin=" + npc.name + "; quality=" + (npc.Renderer != null ? npc.Renderer.quality.ToString() : "none") });
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), ReplayVisualShaderLines());
            Debug.Log("QUEST NPC: ready ten NPCs; inputStart=" + _config.inputStart + " rows=" + _config.inputRows
                + " delay=" + _config.startDelaySeconds + " mode=" + _config.mode + " AppSW=" + _config.appSw + " output=" + _directory);
            RecordPhase("ready");
            _phase = "Pre-roll";
            StartProfiler();
            if (_config.prewarm)
            {
                yield return PrewarmBeforeReplay();
                if (_warmFailure != null) { Fail("pipeline prewarm: " + _warmFailure); yield break; }
            }
            else
            {
                RecordPhase("warm-disabled");
                File.WriteAllText(Path.Combine(_directory, "prewarm.txt"), "prewarm disabled by config\n");
            }
            _run = SandboxPropSlashPlayerCheck.StartPrepared(arguments, _config.keepOpen, true, _config.startDelaySeconds);
            while (!_run.Done) yield return null;
            StopProfiler();
            _phase = _run.Phase;
            RecordDone();
        }

        private IEnumerator PrewarmBeforeReplay()
        {
            RecordPhase("warm-begin");
            double deadline = Time.unscaledTimeAsDouble + 30.0;
            var report = new List<string> { "native requested=" + (_config.mode == "vp3c-native"), "Unity expected rendered warm frames=3" };
            var nativeRoute = _drawing != null ? _drawing.NativeRoute : null;
            if (_config.mode == "vp3c-native")
            {
                if (_drawing == null) _warmFailure = "the prepared world has no camera drawing component";
                while (_warmFailure == null && nativeRoute == null && Time.unscaledTimeAsDouble < deadline)
                {
                    yield return null;
                    nativeRoute = _drawing.NativeRoute;
                }
                if (_warmFailure == null && nativeRoute == null) _warmFailure = "the native route did not become available within 30 s";
                if (_warmFailure == null)
                {
                    try
                    {
                        if (!nativeRoute.RequestPrewarm(_camera, _world.Display, out string failure, _config.appSw)) _warmFailure = failure ?? "native prewarm was not accepted";
                    }
                    catch (Exception e) { _warmFailure = "native prewarm request: " + e.Message; }
                }
            }

            if (_warmFailure == null)
            {
                try { _unityPrewarm = QuestNpcUnityPrewarm.Create(_camera, _world.Display.PrewarmStencilMaterials, _world.Display.SinglePassInstanced, _world.gameObject.layer, SandboxPropSlashPlayerCheck.ReplayVisualRenderers()); }
                catch (Exception e) { _warmFailure = "Unity prewarm creation: " + e.Message; }
            }
            if (_unityPrewarm != null)
            {
                for (int frame = 0; frame < 3 && _warmFailure == null; frame++)
                {
                    try { _unityPrewarm.Issue(); }
                    catch (Exception e) { _warmFailure = "Unity prewarm issue: " + e.Message; }
                    yield return null;
                }
                // Allow real camera clears and the last warm frame's GPU work to finish.
                for (int frame = 0; frame < 4; frame++) yield return null;
                bool readyKnown = TryUnityPrewarmReady(out bool ready);
                while (readyKnown && !ready && Time.unscaledTimeAsDouble < deadline)
                {
                    yield return null;
                    readyKnown = TryUnityPrewarmReady(out ready);
                }
                report.Add("Unity rendered warm frames=" + _unityPrewarm.RenderedWarmFrames);
                report.Add("Unity replay visual renderers=" + _unityPrewarm.ReplayVisualRendererCount + "; material slots=" + _unityPrewarm.ReplayVisualMaterialCount);
                report.Add("Unity buffer retirement query succeeded=" + readyKnown + "; ready=" + ready);
                if (_unityPrewarm.RenderedWarmFrames != 3 && _warmFailure == null) _warmFailure = "Unity prewarm did not render all three preparation frames";
                if (!ready && _warmFailure == null) _warmFailure = "Unity prewarm GPU completion was not observed within 30 s";
                if (ready)
                {
                    try { _unityPrewarm.Dispose(); _unityPrewarm = null; }
                    catch (Exception e) { if (_warmFailure == null) _warmFailure = "Unity prewarm disposal: " + e.Message; }
                }
                RetireUnityPrewarmSafely(); // Never free an in-flight or unconfirmed buffer on failure.
            }

            if (_config.mode == "vp3c-native" && nativeRoute != null)
            {
                bool complete = false;
                // Preserve native completion evidence even when Unity's fence query failed.
                try
                {
                    bool requested = nativeRoute.GetPrewarmStatus(_camera, out complete, out string failure);
                    if (_warmFailure == null && (!requested || failure != null)) _warmFailure = failure ?? "native prewarm request disappeared";
                }
                catch (Exception e) { if (_warmFailure == null) _warmFailure = "native prewarm status: " + e.Message; }
                while (_warmFailure == null && !complete)
                {
                    try
                    {
                        if (!nativeRoute.GetPrewarmStatus(_camera, out complete, out string failure)) _warmFailure = "native prewarm request disappeared";
                        else if (failure != null) _warmFailure = failure;
                    }
                    catch (Exception e) { _warmFailure = "native prewarm status: " + e.Message; }
                    if (_warmFailure == null && !complete && Time.unscaledTimeAsDouble >= deadline) _warmFailure = "native prewarm did not complete within 30 s";
                    if (_warmFailure == null && !complete) yield return null;
                }
                report.Add("native complete=" + complete);
                report.Add(nativeRoute.DescribePrewarm(_camera));
            }
            report.Add("result=" + (_warmFailure ?? "completed"));
            File.WriteAllLines(Path.Combine(_directory, "prewarm.txt"), report);
            RecordPhase(_warmFailure == null ? "warm-end" : "warm-failed");
        }

        private bool TryUnityPrewarmReady(out bool ready)
        {
            ready = false;
            try { ready = _unityPrewarm.ReadyToDispose; return true; }
            catch (Exception e)
            {
                if (_warmFailure == null) _warmFailure = "Unity prewarm GPU completion query: " + e.Message;
                return false;
            }
        }

        private void RetireUnityPrewarmSafely()
        {
            if (_unityPrewarm == null) return;
            try { _unityPrewarm.RetireWhenReady(); _unityPrewarm = null; }
            catch (Exception e)
            {
                // Retain the helper if the driver cannot prove completion. Optional diagnostics
                // never force-release buffers that the GPU may still read or abort the coroutine.
                if (!_unityPrewarmRetireFailureLogged)
                {
                    _unityPrewarmRetireFailureLogged = true;
                    Debug.LogWarning("QUEST NPC: prewarm buffers retained; retirement failed: " + e.Message);
                }
            }
        }

        private static string[] NpcSkinQualityLines()
        {
            var lines = new List<string>();
            foreach (var npc in UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None))
                if (npc.CharacterRoot != null)
                    foreach (var skin in npc.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                        lines.Add("source NPC skin=" + npc.name + "/" + skin.name + "; quality=" + skin.quality);
            return lines.ToArray();
        }

        private static string[] ReplayVisualShaderLines()
        {
            MeshRenderer[] renderers = SandboxPropSlashPlayerCheck.ReplayVisualRenderers();
            var materials = new HashSet<Material>();
            var lines = new List<string>();
            int slots = 0;
            foreach (MeshRenderer renderer in renderers)
            {
                lines.Add("replay visual renderer=" + renderer.name + "; active=" + renderer.gameObject.activeInHierarchy
                    + "; enabled=" + renderer.enabled + "; motionMode=" + renderer.motionVectorGenerationMode);
                Material[] shared = renderer.sharedMaterials;
                for (int slot = 0; slot < shared.Length; slot++)
                {
                    Material material = shared[slot];
                    if (material == null) continue;
                    slots++;
                    materials.Add(material);
                    lines.Add("replay visual material renderer=" + renderer.name + "; slot=" + slot + "; material=" + material.name
                        + "; shader=" + (material.shader != null ? material.shader.name : "none")
                        + "; XRMotionVectors pass=" + material.FindPass("XRMotionVectors")
                        + "; enabled=" + material.GetShaderPassEnabled("XRMotionVectors"));
                }
            }
            lines.Insert(0, "replay visual metadata: renderers=" + renderers.Length + "; unique materials=" + materials.Count + "; material slots=" + slots);
            return lines.ToArray();
        }

        private void OnReplayPrepared() => RecordPhase("check-ready");

        private void OnReplayBegun()
        {
            RecordPhase("replay-begin");
            _phase = "Replaying";
        }

        private void RecordPhase(string phase)
        {
            _phase = phase;
            string line = "phase=" + phase + "; frame=" + Time.frameCount + "; unscaledSeconds=" + Time.unscaledTimeAsDouble.ToString("R", Inv)
                + "; realtimeSeconds=" + Time.realtimeSinceStartupAsDouble.ToString("R", Inv) + "; utc=" + DateTime.UtcNow.ToString("O", Inv);
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { line });
            Debug.Log("QUEST NPC PHASE: " + line);
        }

        private void StartProfiler()
        {
            if (!_config.profiler || _profileStarted) return;
            _profilerBefore = Profiler.enabled;
            _binaryBefore = Profiler.enableBinaryLog;
            _profileFileBefore = Profiler.logFile;
            Profiler.logFile = Path.Combine(_directory, "profile.raw");
            Profiler.enableBinaryLog = true;
            Profiler.enabled = true;
            _profileStarted = true;
            Debug.Log("QUEST NPC: Profiler pre-roll recording begins after ten NPCs are ready at frame " + Time.frameCount + " into " + Profiler.logFile);
        }

        private void StopProfiler()
        {
            if (!_profileStarted) return;
            Profiler.enabled = false;
            Profiler.enableBinaryLog = _binaryBefore;
            Profiler.logFile = _profileFileBefore;
            Profiler.enabled = _profilerBefore;
            _profileStarted = false;
            Debug.Log("QUEST NPC: Profiler recording ended at frame " + Time.frameCount);
        }

        private void RecordDone()
        {
            if (_doneRecorded || _run == null || !_run.Done) return;
            _doneRecorded = true;
            if (_drawing != null) File.WriteAllText(Path.Combine(_directory, "native-route.txt"), _drawing.DescribeNativeRoute(out _) ?? "no native route");
            if (_config.prewarm && _config.mode == "vp3c-native" && _drawing != null && _drawing.NativeRoute != null)
                File.WriteAllText(Path.Combine(_directory, "prewarm-after-replay.txt"), _drawing.NativeRoute.DescribePrewarm(_camera));
            string loggerEndAdmission = "Disabled";
#if DEBUG
            loggerEndAdmission = DevelopmentLogger.Instance.write_log("QuestNpcSlashDemo", "npc_run_end", _directory).ToString();
#endif
            File.WriteAllText(Path.Combine(_directory, "demo-result.txt"), "run code=" + _run.Code + "\nscene retained=" + _run.SceneRetained
                + "\nprofile requested=" + _config.profiler + "\ncomparison=" + _run.Directory
                + "\ndevelopment logger directory=" + Path.Combine(Application.persistentDataPath, "logger")
                + "\ndevelopment logger begin enqueue=" + _loggerBeginAdmission + "\ndevelopment logger end enqueue=" + loggerEndAdmission
                + "\nlogger enqueue accepted does not guarantee persistence; verify npc_run_end in the saved JSONL\n");
            Debug.Log("QUEST NPC: measurement done code=" + _run.Code + " sceneRetained=" + _run.SceneRetained + " output=" + _directory);
        }

        private void Fail(string failure)
        {
            _phase = "Failed";
            Debug.LogError("QUEST NPC: " + failure);
            StopProfiler();
            if (!string.IsNullOrEmpty(_directory)) File.WriteAllText(Path.Combine(_directory, "demo-result.txt"), "NOT started: " + failure + "\n");
            if (_config != null && !_config.keepOpen) Application.Quit(13);
        }

        private void Update()
        {
            if (_config == null || _camera == null) return;
            if (!_spaceWarpApplied) _spaceWarpApplied = SpaceWarpFeature.SetSpaceWarp(_config.appSw);
            if (Time.unscaledTime < _nextStatus) return;
            _nextStatus = Time.unscaledTime + 5f;
            Debug.Log("QUEST APPSPACE: mode=" + _config.appSpacePoseMode + " parent=" + (_appSpaceOrigin != null ? _appSpaceOrigin.name : "none")
                + " maxHeadDeltaDeg=" + _headAngleMax.ToString("F4", Inv) + " maxSentDeltaDeg=" + _appSpaceAngleMax.ToString("F4", Inv)
                + " near=" + _camera.nearClipPlane + " far=" + _camera.farClipPlane);
            _headAngleMax = _appSpaceAngleMax = 0f;
            _displays.Clear();
            SubsystemManager.GetSubsystems(_displays);
            foreach (var xr in _displays)
            {
                xr.TryGetDisplayRefreshRate(out float refresh);
                xr.TryGetAppGPUTimeLastFrame(out float gpu);
                QuestDisplayRefreshRateFeature.TryReadReported(out float physicalRefresh);
                if (_drawing != null && _config.mode == "vp3c-native") Debug.Log("QUEST NPC ROUTE: " + _drawing.DescribeNativeRoute(out _));
                if (_config.mode == "vp3c-native" && VpNativeDrawPlugin.TryLoad(out _))
                    Debug.Log("QUEST NPC NATIVE: " + VpNativeDrawPlugin.ReadCounters() + "; " + VpNativeDrawPlugin.DescribeState());
                Debug.Log("QUEST NPC: mode=" + _config.mode + " phase=" + (_run != null ? _run.Phase : _phase)
                    + " displayHz=" + physicalRefresh + " xrReportedHz=" + refresh + " AppSWRequested=" + _config.appSw + " AppSWConfigApplied=" + _spaceWarpApplied
                    + " dtMs=" + Time.unscaledDeltaTime * 1000f + " appGpuMs=" + gpu * 1000f + " stereo=" + XRSettings.stereoRenderingMode);
            }
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || !_config.appSw || camera != _camera) return;
            // The camera local pose includes physical HMD tracking. The parent maps tracking space into the scene.
            // XR_FB_space_warp appSpaceDeltaPose describes application locomotion, which is constant in this demo.
            Transform appSpace = _config.appSpacePoseMode == "tracking-origin" ? _appSpaceOrigin : camera.transform;
            if (_appSpacePoseSampled)
            {
                _headAngleMax = Mathf.Max(_headAngleMax, Quaternion.Angle(_lastHeadRotation, camera.transform.rotation));
                _appSpaceAngleMax = Mathf.Max(_appSpaceAngleMax, Quaternion.Angle(_lastAppSpaceRotation, appSpace.rotation));
            }
            _lastHeadRotation = camera.transform.rotation; _lastAppSpaceRotation = appSpace.rotation; _appSpacePoseSampled = true;
            SpaceWarpFeature.SetAppSpacePosition(appSpace.position);
            SpaceWarpFeature.SetAppSpaceRotation(appSpace.rotation);
        }

        private void OnDestroy()
        {
            RetireUnityPrewarmSafely();
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            SandboxPropSlashPlayerCheck.ReplayBegun -= OnReplayBegun;
            SandboxPropSlashPlayerCheck.ReplayPrepared -= OnReplayPrepared;
            Application.quitting -= StopProfiler;
            StopProfiler();
        }
    }
}
