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
    /// <summary>Quest NPC Slash and city-walk scenarios, with real-time recorded input and live camera-only AppSW.</summary>
    public sealed class QuestNpcSlashDemo : MonoBehaviour
    {
        public const string SceneName = "QuestNpcSlash10";
        public const string PackageName = "com.zantetsuken.quest.npcslash";
        public const string CityWalkSceneName = "QuestCityWalk";
        public const string CityWalkPackageName = "com.zantetsuken.quest.citywalk";

        [Serializable] internal sealed class Config
        {
            public string scenario = "";                         // DESIGN 3.4: read by SandboxScenario (play without it); kept here so that the run's record of its configuration carries it
            public string mode = "vp3c-native";
            public bool appSw = true;
            public string appSpacePoseMode = "tracking-origin"; // Camera pose is retained only for the controlled comparison.
            public bool appSwDistanceExperiment = true;
            public float appSwFarMeters = 30f;
            public string appSwFixedView = "off";
            public bool profiler = true;
            public bool prewarm = true;
            public string inputFile;
            public string cityWalkScriptFile;
            public int fragmentGroupsMax = -1; // DESIGN 7.10: 0 or more switches the fragment group collection on above that many live cut fragments; -1 leaves the world's profile as it is.
            public bool spatialSplit = false; // DESIGN 7.9.2: true switches the spatial split on for the scene's cut worlds; false leaves the world's profile as it is.
            public bool spatialSplitBent = false; // DESIGN 7.9.2, the bent partition: true switches it on for the scene's cut worlds' split (the split itself as the profile or spatialSplit has it); false leaves the profile as it is.
            public float cityWalkBudgetSeconds = 565f;
            public int inputStart = 2227;
            public int inputRows = 512;
            public float startDelaySeconds = 5f;
            public bool keepOpen = true;
            public bool nativeColourPreparation = true;
            public bool nativeNoClipColour = true;
            public bool nativeNoClipMotion = true;
            public bool nativeNoClipShadows = true;
            public bool nativeShareGpuConstants = true;
            public bool nativeIndirectCount = true;        // DESIGN 4.5.10 (D-217): the command compaction is asked for by default; false keeps the fixed draw
            public bool nativeIndirectCountVerify = false; // diagnosis: the GPU check of every compaction, read back for the log (not in a measurement)
            public bool nativeIndirectCountSnapshot = false; // diagnosis (2026-10-10): the count of every count draw copied and read back, tallied a column (a check run)
            public bool nativeIndirectCountCastersFixed = false; // diagnosis: the caster column drawn fixed while bodies and motion compact
            public bool nativeIndirectCountDirect = false;  // comparison: the count draws read the compute-written counts directly (the earlier way, with the one-frame shadow loss)
            public bool nativeIndirectCountPoison = false;  // diagnosis: the compacted entries filled with zeroes each frame, so that a stale read of them shows (a check run)
            public bool propChecks = true;                 // D-212: the city walk's checks part; false leaves it out (the standard performance configuration)
            public bool propDetail = true;                 // D-212: the city walk's detailed diagnostics part; false leaves it out
        }

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private Config _config;
        private bool _cityWalk;
        private bool _play;
        private static SandboxScenarioKind s_scenario;
        private string LogPrefix => _play ? "QUEST PLAY: " : _cityWalk ? "QUEST CITY WALK: " : "QUEST NPC: ";
        private Camera _camera;
        private Transform _appSpaceOrigin;
        private Quaternion _lastHeadRotation, _lastAppSpaceRotation;
        private bool _appSpacePoseSampled;
        private float _headAngleMax, _appSpaceAngleMax;
        private QuestAppSwDistanceSetup _distanceSetup;
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

        private static string ReadConfigText()
        {
            string file = Path.Combine(Application.persistentDataPath, "quest3s-config.json");
            return File.Exists(file) ? File.ReadAllText(file) : null;
        }

        /// <summary>
        /// The configuration a scenario runs with, from the launch's file (null: no file, every default). A scripted
        /// scenario takes the file as it is. **Play takes the common settings alone** (DESIGN 3.4): the draw route's
        /// mode and its documented way back (the fixed draw), AppSW and the application space's pose. Everything of a
        /// scripted run or an experiment is left at what play needs whatever the file says -- no distance experiment
        /// (the class's default has it on, for the scripted runs), no profile, no diagnosis of the compaction, no
        /// comparison switch of the native route -- so that a file left by an earlier experiment, or no file at all,
        /// starts the same play.
        /// </summary>
        private static Config ConfigFor(SandboxScenarioKind kind, string json)
        {
            Config launch = string.IsNullOrEmpty(json) ? new Config() : JsonUtility.FromJson<Config>(json) ?? new Config();
            if (kind != SandboxScenarioKind.Play)
            {
                return launch;
            }

            return new Config
            {
                scenario = launch.scenario,
                mode = launch.mode,
                appSw = launch.appSw,
                appSpacePoseMode = launch.appSpacePoseMode,
                nativeIndirectCount = launch.nativeIndirectCount,
                // The bent partition's verification switch (DESIGN 7.9.2) is carried into play too: its default, false,
                // leaves play as it is, and the Quest check of the bent partition runs as play (qBent2 of 2026-10-15 asked
                // for it and got the profile's off, because this copy left it out).
                spatialSplitBent = launch.spatialSplitBent,
                appSwDistanceExperiment = false,
                profiler = false,
            };
        }

        /// <summary>
        /// What is wrong with a scenario's configuration, or null: the checks made before anything of the scene is
        /// touched. Play is asked only what it uses.
        /// </summary>
        private static string Validate(Config config, SandboxScenarioKind kind)
        {
            if (config == null || (config.mode != "vp3c" && config.mode != "vp3c-native")) return "mode must be vp3c or vp3c-native";
            if (config.appSpacePoseMode != "camera" && config.appSpacePoseMode != "tracking-origin") return "appSpacePoseMode must be camera or tracking-origin";
            if (kind == SandboxScenarioKind.Play) return null;
            if (config.inputStart < 0 || config.inputRows < 1 || config.inputRows > 4096
                || !float.IsFinite(config.startDelaySeconds) || config.startDelaySeconds < 0f) return "invalid input range or start delay";
            if (kind == SandboxScenarioKind.CityWalk && (!float.IsFinite(config.cityWalkBudgetSeconds) || config.cityWalkBudgetSeconds <= 0f))
                return "invalid city walk budget";
            if (config.appSw && config.appSwDistanceExperiment && config.mode != "vp3c-native") return "distance experiment requires AppSW and vp3c-native";
            return null;
        }

        /// <summary>
        /// For tests and the record: a launch's configuration text read for a scenario -- its failure (null: none),
        /// its mode, AppSW and whether the AppSW distance experiment is set up. Reads and decides only.
        /// </summary>
        /// <summary>For tests: the configuration a scenario would run with from this launch text (null: no file). Reads only.</summary>
        internal static Config ReadLaunch(string json, SandboxScenarioKind kind) => ConfigFor(kind, json);

        internal static string CheckLaunch(string json, SandboxScenarioKind kind, out string mode, out bool appSw, out bool distanceExperiment)
        {
            Config config = ConfigFor(kind, json);
            mode = config.mode;
            appSw = config.appSw;
            distanceExperiment = config.appSw && config.appSwDistanceExperiment;
            return Validate(config, kind);
        }

        /// <summary>
        /// The draw route and the native route's options for a launch, set before any scene's world is made: for play,
        /// the city walk and the NPC slash (the static scene keeps its own, QuestStaticDemo), from the configuration as
        /// <see cref="ConfigFor"/> gives it for the scenario.
        /// </summary>
        internal static void SelectRouteFor(SandboxScenarioChoice choice, string json)
        {
            if (choice.Failure != null || choice.Kind == SandboxScenarioKind.StaticWait) return;
            Config config = ConfigFor(choice.Kind, json);
            VpNativeDrawSetup.Requested = config.mode == "vp3c-native";
            ApplyNativeOptions(config);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void SelectNativeRoute()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // The launch's scenario selects the initial route before any scene's world is made (DESIGN 3.4): play, the city
            // walk and the NPC slash read it here; the static scene keeps its own (QuestStaticDemo). It was the package
            // that selected it while each scenario had a package of its own.
            if (Application.CanStreamedLevelBeLoaded(BunnyDemo.SceneName)) { VpNativeDrawSetup.Requested = false; return; }
            SelectRouteFor(SandboxScenario.Current, ReadConfigText());
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
            VpNativeDrawPlugin.VulkanIndirectCount = config.nativeIndirectCount;
            VpCommandCompaction.VerificationEnabled = config.nativeIndirectCount && config.nativeIndirectCountVerify;
            VpCommandCompaction.SnapshotEnabled = config.nativeIndirectCount && config.nativeIndirectCountSnapshot;
            VpCommandCompaction.CastersFixed = config.nativeIndirectCount && config.nativeIndirectCountCastersFixed;
            VpCommandCompaction.DrawFromCopy = !config.nativeIndirectCountDirect;
            VpCommandCompaction.PoisonEntries = config.nativeIndirectCount && config.nativeIndirectCountPoison;
        }

        /// <summary>
        /// The Quest's entry for play, the city walk and the NPC slash (DESIGN 3.4), called by <see cref="SandboxScenario"/>
        /// once the scenario's scene is loaded and before anything of it starts. It no longer starts by itself on the
        /// scene's name: the name is checked against the scenario instead, and a scene that is not the scenario's is a
        /// failure, answered in words. Null when the demo was made.
        /// <para>
        /// **Play** makes the same demo for what a Quest scene needs whatever runs in it -- the configuration's draw
        /// route options, AppSW with the tracking origin's pose, the display's cadence -- and nothing of a run: the
        /// crowd, the katana and the walking input are left as the scene has them, no input is replayed, nothing is
        /// measured, recorded or ended.
        /// </para>
        /// </summary>
        internal static string StartForScenario(SandboxScenarioKind kind)
        {
            string scene = SceneManager.GetActiveScene().name;
            bool cityWalk = kind == SandboxScenarioKind.CityWalk;
            bool play = kind == SandboxScenarioKind.Play;
            if (cityWalk && scene != CityWalkSceneName) return "city-walk needs the scene " + CityWalkSceneName + "; this is " + scene;
            if (kind == SandboxScenarioKind.NpcSlash && scene != SceneName) return "npc-slash needs the scene " + SceneName + "; this is " + scene;
            if (play && UnityEngine.Object.FindAnyObjectByType<CutWorldRoot>() == null) return "play needs a scene with a cut world; " + scene + " has none";
            s_scenario = kind;
            // The direct skin input requires the same four-weight contract as the authored NPC cut data.
            foreach (var npc in UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None))
                if (npc.CharacterRoot != null)
                    foreach (var skin in npc.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true)) skin.quality = SkinQuality.Bone4;
            if (play)
            {
                // (The recorder's own controls and the diagnostic overlay are quieted for play by SandboxScenario, on the device as on a PC.)
                new GameObject("Quest play").AddComponent<QuestNpcSlashDemo>();
                return null;
            }

            if (cityWalk)
            {
                // Extraction takes several frames. Keep admission and crowd activation for StartPrepared's observers.
                PlayableCityCuttable.Held = true;
                foreach (var crowd in UnityEngine.Object.FindObjectsByType<MobPlanCrowd>(FindObjectsSortMode.None)) crowd.enabled = false;
            }
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
            new GameObject(cityWalk ? "Quest city walk demo" : "Quest ten NPC Slash demo").AddComponent<QuestNpcSlashDemo>();
            return null;
        }

        private IEnumerator Start()
        {
            _cityWalk = s_scenario == SandboxScenarioKind.CityWalk;
            _play = s_scenario == SandboxScenarioKind.Play;
            // Play's configuration is the common settings alone (ConfigFor): no experiment of a scripted run reaches it.
            _config = ConfigFor(s_scenario, ReadConfigText());
            string invalid = Validate(_config, s_scenario);
            if (invalid != null) { Fail(invalid); yield break; }
            _camera = Camera.main;
            if (_camera == null) { Fail("MainCamera is required"); yield break; }
            _appSpaceOrigin = _camera.transform.parent;
            if (_config.appSpacePoseMode == "tracking-origin" && _appSpaceOrigin == null)
            { Fail("tracking-origin requires the tracked camera parent"); yield break; }
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline) || pipeline.name != "Quest_RPAsset")
            { Fail("Quest_RPAsset is required"); yield break; }
            ApplyNativeOptions(_config);
            if (_config.appSw && _config.appSwDistanceExperiment)
            {
                // (Never in play: its configuration has the experiment off. The mode was checked with the rest.)
                _distanceSetup = new QuestAppSwDistanceSetup(_camera, _config.appSwFarMeters, _config.appSwFixedView, Application.persistentDataPath);
            }
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1; // The 90 Hz OpenXR display and AppSW select the application cadence.
            Application.runInBackground = true;
            if (_play)
            {
                // Play (DESIGN 3.4): the scene runs as it is, with a person's own input. No directory, no replay, no
                // profile, no result and no ending: only what every frame of a Quest scene needs goes on from here
                // (AppSW's switch in Update, the application space's pose before the camera).
                RenderPipelineManager.beginCameraRendering += BeforeCamera;
                _phase = "Play";
                Debug.Log(LogPrefix + "mode=" + _config.mode + " AppSW=" + _config.appSw + " appSpace=" + _config.appSpacePoseMode
                    + " scene=" + SceneManager.GetActiveScene().name + "; no script, no measurement, no ending");
                // The bent partition's verification switch is the one switch of the configuration play applies to the
                // scene's cut worlds (DESIGN 3.4, 7.9.2): the worlds exist with the scene, before any cut. (qBent3 of
                // 2026-10-16 carried the switch into play's configuration and still ran with the profile's off, because
                // play left here before the scripted path's block below.)
                string playBent = BentPartition(out string playBentFailure);
                if (playBentFailure != null) { Fail(playBentFailure); yield break; }
                Debug.Log(LogPrefix + playBent);
                yield break;
            }

            _directory = Path.Combine(Application.persistentDataPath, "runs", DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ")
                + (_cityWalk ? "-citywalk-" : "-npc10-") + _config.mode + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
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
                "profile scope=" + (_cityWalk ? "crowd and city registration, pipeline prewarm, route and cuts" : "ten NPCs ready, check preparation and start delay pre-roll, replay, cut completion; replay CSV scope unchanged"),
            });
            if (_distanceSetup != null) File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { _distanceSetup.Describe });
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

            string scriptFile = _config.cityWalkScriptFile;
            if (_cityWalk && string.IsNullOrEmpty(scriptFile))
            {
                scriptFile = Path.Combine(Application.persistentDataPath, "CityWalkScript.txt");
                using (var request = UnityWebRequest.Get(Application.streamingAssetsPath + "/Quest3s/CityWalkScript.txt"))
                {
                    request.downloadHandler = new DownloadHandlerFile(scriptFile);
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success) { Fail("city walk script extraction: " + request.error); yield break; }
                }
            }
            if (_cityWalk && !File.Exists(scriptFile)) { Fail("city walk script does not exist: " + scriptFile); yield break; }

            _phase = _cityWalk ? "Preparing world" : "Preparing NPCs";
            var world = _world = UnityEngine.Object.FindAnyObjectByType<CutWorldRoot>();
            _drawing = world != null ? world.GetComponent<CutWorldCameraDrawing>() : null;
            var npcs = UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None);
            if (!_cityWalk && npcs.Length != 10) { Fail("expected ten NPCs, found " + npcs.Length); yield break; }
            double readyBy = Zantetsu.Core.GamePause.UnscaledTimeAsDouble + 120.0;
            while (Zantetsu.Core.GamePause.UnscaledTimeAsDouble < readyBy)
            {
                bool ready = world != null && world.IsReady;
                foreach (var npc in _cityWalk ? Array.Empty<SandboxNpcCharacter>() : npcs)
                {
                    if (npc.Failure != null) { Fail("NPC preparation: " + npc.name + ": " + npc.Failure); yield break; }
                    ready &= npc.IsTarget;
                }
                if (ready) break;
                { yield return null; while (Zantetsu.Core.GamePause.IsPaused) yield return null; }
            }
            bool allReady = world != null && world.IsReady;
            if (!_cityWalk) foreach (var npc in npcs) allReady &= npc.IsTarget;
            if (!allReady) { Fail(_cityWalk ? "world did not prepare within 120 s" : "ten NPCs did not prepare within 120 s"); yield break; }
            if (_distanceSetup != null) File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { _distanceSetup.Describe });

            string[] arguments =
            {
                (_cityWalk ? "QuestCityWalkDemo" : "QuestNpcSlashDemo"), SandboxPropSlashPlayerCheck.Argument, Path.Combine(_directory, "comparison"),
                SandboxSlashReplayPlayerCheck.InputArgument, inputFile,
                SandboxSlashReplayPlayerCheck.StartArgument, _config.inputStart.ToString(Inv),
                SandboxPropSlashPlayerCheck.RowsArgument, _config.inputRows.ToString(Inv),
                SandboxPropSlashPlayerCheck.FrameRateArgument, "-1", SandboxPropSlashPlayerCheck.IterationsArgument, "1",
                SandboxPropSlashPlayerCheck.LightArgument,
                SandboxPropSlashPlayerCheck.MarkersArgument, "FrameEvents.XRBeginFrame;FrameEvents.OnBeforeRenderCallback;Scripts:Zantetsu.PoseLod.Decide;Scripts:Zantetsu.PoseLod.Force",
            };
            var scenarioArguments = new List<string>(arguments);
            if (_cityWalk)
            {
                scenarioArguments.AddRange(new[] { SandboxPropSlashPlayerCheck.CityWalkArgument,
                    SandboxPropSlashPlayerCheck.MobPlanArgument, scriptFile,
                    SandboxPropSlashPlayerCheck.CityWalkBudgetArgument, _config.cityWalkBudgetSeconds.ToString(Inv) });
            }
            else scenarioArguments.Add(SandboxPropSlashPlayerCheck.MultiNpcArgument);
            // D-212: the parts a city walk leaves out, from the launch's configuration (a Player on the device has no command line of its own).
            if (_cityWalk && !_config.propChecks) scenarioArguments.AddRange(new[] { SandboxPropSlashPlayerCheck.ChecksArgument, "off" });
            if (_cityWalk && !_config.propDetail) scenarioArguments.AddRange(new[] { SandboxPropSlashPlayerCheck.DetailArgument, "off" });
            arguments = scenarioArguments.ToArray();
            File.WriteAllLines(Path.Combine(_directory, "run-arguments.txt"), arguments);
            // Diagnosis: every count snapshot of the run, for a reading of one-frame falls (once the scene is retained the file goes on: pull it again).
            if (VpCommandCompaction.SnapshotEnabled) VpCommandCompaction.SnapshotFile = Path.Combine(_directory, "count-snapshots.csv");
#if DEBUG
            _loggerBeginAdmission = DevelopmentLogger.Instance.write_log("QuestNpcSlashDemo", "npc_run_begin", _directory).ToString();
#endif
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { "development logger begin enqueue=" + _loggerBeginAdmission });
            foreach (var npc in npcs)
                File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { "prepared NPC skin=" + npc.name + "; quality=" + (npc.Renderer != null ? npc.Renderer.quality.ToString() : "none") });
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), ReplayVisualShaderLines());
            Debug.Log(LogPrefix + (_cityWalk ? "world ready; city route prepared; inputStart=" : "ready ten NPCs; inputStart=") + _config.inputStart + " rows=" + _config.inputRows
                + " delay=" + _config.startDelaySeconds + " mode=" + _config.mode + " AppSW=" + _config.appSw + " output=" + _directory);
            RecordPhase("ready");
            _phase = "Pre-roll";
            StartProfiler();
            if (_config.prewarm && !_cityWalk)
            {
                yield return PrewarmBeforeReplay();
                if (_warmFailure != null) { Fail("pipeline prewarm: " + _warmFailure); yield break; }
            }
            else if (!_config.prewarm)
            {
                RecordPhase("warm-disabled");
                File.WriteAllText(Path.Combine(_directory, "prewarm.txt"), "prewarm disabled by config\n");
            }
            // The fragment group collection (DESIGN 7.10), asked for by the configuration: on for every cut world of the scene, before any cut.
            string groups = "fragment group collection=the world's profile (fragmentGroupsMax " + _config.fragmentGroupsMax + ")";
            if (_config.fragmentGroupsMax >= 0)
            {
                int switched = 0, worlds = 0;
                foreach (CutWorldRoot cutWorld in UnityEngine.Object.FindObjectsByType<CutWorldRoot>(FindObjectsSortMode.None))
                {
                    worlds++;
                    if (cutWorld.EnableFragmentGroups(_config.fragmentGroupsMax, "the Quest configuration's fragmentGroupsMax")) switched++;
                }

                groups = "fragment group collection=on above " + _config.fragmentGroupsMax + " live cut fragments in " + switched + " of " + worlds + " cut worlds";
                if (switched == 0) { Fail("fragmentGroupsMax was asked but no cut world took it"); yield break; }
            }

            Debug.Log(LogPrefix + groups);
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { groups });
            // The spatial split (DESIGN 7.9.2), asked for by the configuration: on for every cut world of the scene, before any cut.
            string split = "spatial split=the world's profile (spatialSplit false)";
            if (_config.spatialSplit)
            {
                int switched = 0, worlds = 0;
                foreach (CutWorldRoot cutWorld in UnityEngine.Object.FindObjectsByType<CutWorldRoot>(FindObjectsSortMode.None))
                {
                    worlds++;
                    if (cutWorld.EnableSpatialSplit("the Quest configuration's spatialSplit")) switched++;
                }

                split = "spatial split=on in " + switched + " of " + worlds + " cut worlds";
                if (switched == 0) { Fail("spatialSplit was asked but no cut world took it"); yield break; }
            }

            Debug.Log(LogPrefix + split);
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { split });

            // The bent partition of the split (DESIGN 7.9.2), asked for by the configuration: on for every cut world, before any cut.
            string bent = BentPartition(out string bentFailure);
            if (bentFailure != null) { Fail(bentFailure); yield break; }
            Debug.Log(LogPrefix + bent);
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { bent });
            _run = SandboxPropSlashPlayerCheck.StartPrepared(arguments, _config.keepOpen, true, _config.startDelaySeconds,
                _cityWalk ? PrepareCityReplay : null);
            if (_cityWalk)
                foreach (var crowd in UnityEngine.Object.FindObjectsByType<MobPlanCrowd>(FindObjectsSortMode.None)) crowd.enabled = true;
            // (Watching the run end, not a stage of a script: not held by a pause, DESIGN 4.6.)
            while (!_run.Done) yield return null;
            StopProfiler();
            _phase = _run.Phase;
            RecordDone();
        }

        private IEnumerator PrepareCityReplay()
        {
            RecordPhase("city-registered");
            if (_config.prewarm) yield return PrewarmBeforeReplay();
            _run.PreparationFailure = _warmFailure;
            if (_warmFailure == null) RecordPhase("city-ready");
        }

        private IEnumerator PrewarmBeforeReplay()
        {
            RecordPhase("warm-begin");
            double deadline = Zantetsu.Core.GamePause.UnscaledTimeAsDouble + 30.0;
            var report = new List<string> { "native requested=" + (_config.mode == "vp3c-native"), "Unity expected rendered warm frames=3" };
            var nativeRoute = _drawing != null ? _drawing.NativeRoute : null;
            if (_config.mode == "vp3c-native")
            {
                if (_drawing == null) _warmFailure = "the prepared world has no camera drawing component";
                while (_warmFailure == null && nativeRoute == null && Zantetsu.Core.GamePause.UnscaledTimeAsDouble < deadline)
                {
                    { yield return null; while (Zantetsu.Core.GamePause.IsPaused) yield return null; }
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
                    { yield return null; while (Zantetsu.Core.GamePause.IsPaused) yield return null; }
                }
                // Allow real camera clears and the last warm frame's GPU work to finish.
                for (int frame = 0; frame < 4; frame++) { yield return null; while (Zantetsu.Core.GamePause.IsPaused) yield return null; }
                bool readyKnown = TryUnityPrewarmReady(out bool ready);
                while (readyKnown && !ready && Zantetsu.Core.GamePause.UnscaledTimeAsDouble < deadline)
                {
                    { yield return null; while (Zantetsu.Core.GamePause.IsPaused) yield return null; }
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
                    if (_warmFailure == null && !complete && Zantetsu.Core.GamePause.UnscaledTimeAsDouble >= deadline) _warmFailure = "native prewarm did not complete within 30 s";
                    if (_warmFailure == null && !complete) { yield return null; while (Zantetsu.Core.GamePause.IsPaused) yield return null; }
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
            string line = "phase=" + phase + "; frame=" + Time.frameCount + "; unscaledSeconds=" + Zantetsu.Core.GamePause.UnscaledTimeAsDouble.ToString("R", Inv)
                + "; realtimeSeconds=" + Zantetsu.Core.GamePause.RealtimeSinceStartupAsDouble.ToString("R", Inv) + "; utc=" + DateTime.UtcNow.ToString("O", Inv);
            File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { line });
            Debug.Log((_cityWalk ? "QUEST CITY WALK PHASE: " : "QUEST NPC PHASE: ") + line);
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
            Debug.Log(LogPrefix + "Profiler recording begins " + (_cityWalk ? "before crowd and city preparation" : "after ten NPCs are ready") + " at frame " + Time.frameCount + " into " + Profiler.logFile);
        }

        private void StopProfiler()
        {
            if (!_profileStarted) return;
            Profiler.enabled = false;
            Profiler.enableBinaryLog = _binaryBefore;
            Profiler.logFile = _profileFileBefore;
            Profiler.enabled = _profilerBefore;
            _profileStarted = false;
            Debug.Log(LogPrefix + "Profiler recording ended at frame " + Time.frameCount);
        }

        private void RecordDone()
        {
            if (_doneRecorded || _run == null || !_run.Done) return;
            _doneRecorded = true;
            // Moving the tracked-camera parent also moves recorded blade poses into world space.
            // Keep it intact during the cut replay; freeze only the retained scene for numeric captures.
            _distanceSetup?.BeginPoseLock();
            if (_distanceSetup != null) File.AppendAllLines(Path.Combine(_directory, "platform.txt"), new[] { _distanceSetup.Describe });
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
            Debug.Log(LogPrefix + "measurement done code=" + _run.Code + " sceneRetained=" + _run.SceneRetained + " output=" + _directory);
        }

        /// <summary>
        /// The bent partition of the split (DESIGN 7.9.2) switched on for every cut world of the scene when the
        /// configuration asks for it; the line that says what was done, and the failure (null: none) where it was asked
        /// and no cut world took it. Before any cut; play and the scripted scenarios alike.
        /// </summary>
        private string BentPartition(out string failure)
        {
            failure = null;
            if (!_config.spatialSplitBent) return "bent partition=the world's profile (spatialSplitBent false)";
            int switched = 0, worlds = 0;
            foreach (CutWorldRoot cutWorld in UnityEngine.Object.FindObjectsByType<CutWorldRoot>(FindObjectsSortMode.None))
            {
                worlds++;
                if (cutWorld.EnableSpatialSplitBent("the Quest configuration's spatialSplitBent")) switched++;
            }

            if (switched == 0) failure = "spatialSplitBent was asked but no cut world took it";
            return "bent partition=on in " + switched + " of " + worlds + " cut worlds";
        }

        private void Fail(string failure)
        {
            _phase = "Failed";
            Debug.LogError(LogPrefix + failure);
            StopProfiler();
            if (!string.IsNullOrEmpty(_directory)) File.WriteAllText(Path.Combine(_directory, "demo-result.txt"), "NOT started: " + failure + "\n");
            if (_config != null && !_config.keepOpen) Application.Quit(13);
        }

        private void Update()
        {
            if (_config == null || _camera == null) return;
            if (!_spaceWarpApplied) _spaceWarpApplied = SpaceWarpFeature.SetSpaceWarp(_config.appSw);
            if (Zantetsu.Core.GamePause.UnscaledTime < _nextStatus) return;
            _nextStatus = Zantetsu.Core.GamePause.UnscaledTime + 5f;
            Debug.Log("QUEST APPSPACE: mode=" + _config.appSpacePoseMode + " parent=" + (_appSpaceOrigin != null ? _appSpaceOrigin.name : "none")
                + " maxHeadDeltaDeg=" + _headAngleMax.ToString("F4", Inv) + " maxSentDeltaDeg=" + _appSpaceAngleMax.ToString("F4", Inv)
                + " near=" + _camera.nearClipPlane + " far=" + _camera.farClipPlane);
            _headAngleMax = _appSpaceAngleMax = 0f;
            if (_distanceSetup != null) Debug.Log("QUEST APPSW DISTANCE: " + _distanceSetup.Describe);
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
                if (_config.mode == "vp3c-native") Debug.Log("QUEST NPC COMPACTION: " + VpCommandCompaction.DescribeRun());
                if (VpCommandCompaction.VerificationEnabled) Debug.Log("QUEST NPC COMPACTION: " + VpCommandCompaction.ReadVerification());
                if (VpCommandCompaction.SnapshotEnabled || VpCommandCompaction.CastersFixed) Debug.Log("QUEST NPC SNAPSHOTS: castersFixed=" + VpCommandCompaction.CastersFixed + "; " + VpCommandCompaction.DescribeSnapshots());
                // Under frame synthesis the application renders at the reported rate, a part of the display's: the fragment
                // groups' collection takes that frame for its budget (TL, 2026-10-14); 0 otherwise.
                double groupsFrame = _spaceWarpApplied && _config.appSw && refresh > 0f && physicalRefresh > 0f && refresh < 0.75f * physicalRefresh
                    ? 1.0 / Math.Round(refresh) : 0.0;
                foreach (CutWorldRoot cutWorld in UnityEngine.Object.FindObjectsByType<CutWorldRoot>(FindObjectsSortMode.None))
                {
                    if (cutWorld.Groups != null) cutWorld.Groups.FrameSeconds = groupsFrame;
                }

                Debug.Log(LogPrefix + "mode=" + _config.mode + " phase=" + (_run != null ? _run.Phase : _phase)
                    + " displayHz=" + physicalRefresh + " xrReportedHz=" + refresh + " AppSWRequested=" + _config.appSw + " AppSWConfigApplied=" + _spaceWarpApplied
                    + " groupsFrameMs=" + (groupsFrame * 1000.0).ToString("F1", Inv)
                    + " dtMs=" + Zantetsu.Core.GamePause.UnscaledDeltaTime * 1000f + " appGpuMs=" + gpu * 1000f + " stereo=" + XRSettings.stereoRenderingMode);
            }
        }

        private void BeforeCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!_spaceWarpApplied || !_config.appSw || camera != _camera) return;
            _distanceSetup?.BeforeCamera(context);
            // The camera local pose includes physical HMD tracking. The parent maps tracking space into the scene.
            // XR_FB_space_warp appSpaceDeltaPose describes application locomotion, which includes the city route's artificial translation and rotation.
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
            _distanceSetup?.Dispose();
            RenderPipelineManager.beginCameraRendering -= BeforeCamera;
            SandboxPropSlashPlayerCheck.ReplayBegun -= OnReplayBegun;
            SandboxPropSlashPlayerCheck.ReplayPrepared -= OnReplayPrepared;
            Application.quitting -= StopProfiler;
            StopProfiler();
        }
    }
}
