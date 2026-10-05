using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
// Aliases, not a plain "using UnityEngine.InputSystem": that makes InputDevice and CommonUsages ambiguous
// with the UnityEngine.XR types this harness reads the head pose from.
using InputSystemRuntime = UnityEngine.InputSystem.InputSystem;
using InputSystemSettings = UnityEngine.InputSystem.InputSettings;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;
using Debug = UnityEngine.Debug;
using RG = UnityEngine.Rendering.RenderGraphModule;

// Tools/XrSim harness for Meta XR Simulator VRS fixtures. Copied to Assets/_XrSimHarness/ by
// Tools/XrSim/Build-XrSimHarnessPlayer.ps1 only for a Player build and removed afterwards; never product code.
//
// Active only with -xrsimMode hold|replay|noreplay. It builds the G40N960 VP3 / VP3C test scene from the Sandbox grid
// meshes, fixes the XR Origin at the "along" or "across" view, draws the display set through VP3 or the VP3C route and
// records, per render of the main camera, the XR head pose (UnityEngine.XR centre eye), the camera's world and local pose,
// the camera-follow error (camera local pose vs head pose; the scene's TrackedPoseDriver should make it zero) and both
// eyes' view / projection matrices. It reads the in-process Simulator runtime log for its version, device profile,
// session-capture transitions and the documented automated replay ("Opened '<file>.vrs'", "Playback is complete").
//
//   hold     : waits at READY until the stop file exists (a recording is made meanwhile by the tools).
//   replay   : captures each pose target of -xrsimTargets in order while the replay is in progress: a target is
//              captured once the head has stayed within its tolerance for the stationary frame count; a later target
//              reached first marks the earlier ones skipped. Each capture saves both eyes, the URP main light shadow
//              atlas and the VP3C selection counts, and is marked invalid with its reasons (not in replay, camera not
//              following, copy missing).
//   noreplay : the control without a recording; captures at fixed seconds after READY.
// Outputs in -xrsimOut: environment.json, ready.json, frames.csv, events.jsonl, result.json and captures/.
public sealed class XrSimHarness : MonoBehaviour
{
    private const string Prefix = "XrSimHarness: ";
    private const int Geometries = 40;
    private const int Instances = 960;
    private const float Spacing = 1.5f;
    private const float LayoutCenterZ = 9.75f;
    private const float XrTimeoutSeconds = 60f;
    private const int SettleFrames = 90;
    private const float FollowPositionTolerance = 0.002f;
    private const float FollowAngleTolerance = 0.2f;

    private static readonly string[] Categories = { "Character", "Vehicle", "Building", "Prop" };
    private static readonly float[] CategoryScale = { 0.67331535f, 0.25973427f, 0.13995336f, 0.5259023f };
    private static readonly float[] CategoryHeight = { 0.00019448317f, 0.0013148637f, 0.000013346992f, 0.005706391f };
    private static readonly string[] Phase092Displays =
    {
        "VP Display Probe",
        "Unity Mesh Display/VP Adopted Subset",
        "Unity Mesh Display/VP Shared Geometry Probe",
        "Unity Mesh Display/VP Multi Geometry Probe",
    };

    private static readonly Regex SimLine = new Regex(@"^\[Meta XR Simulator\]\[(\d+\.\d+)\]");
    private static readonly Regex SimTransition = new Regex(@"Transitioning from state (\w+) to (\w+)");
    private static readonly Regex SimVersion = new Regex(@"Version (\d+\.\d+\.\d+\.\d+\.\d+)");
    private static readonly Regex SimProfile = new Regex(@"Simulated Device Profile: (.+)$");

    private static string s_mode;
    private static string s_out;
    private static string s_view;
    private static string s_path;
    private static string s_run;
    private static string s_targetsPath;
    private static string s_stopFile;
    private static bool s_ignoreFocus;
    private static float[] s_noReplaySeconds = { 1.5f, 5f, 8.5f };

    [Serializable]
    public sealed class PoseTarget
    {
        public string name;
        public float px, py, pz, qx, qy, qz, qw;
    }

    [Serializable]
    public sealed class TargetsFile
    {
        public PoseTarget[] poses;
        public float positionTolerance = 0.01f;
        public float angleToleranceDeg = 0.5f;
        public int stationaryFrames = 8;
        public float timeoutSeconds = 90f;
    }

    private sealed class Capture
    {
        public string name;
        public bool captured;
        public int frame = -1;
        public double replayTime = -1;
        public string simState = "";
        public Vector3 head, cameraWorld, cameraLocal;
        public Quaternion headRotation = Quaternion.identity, cameraWorldRotation = Quaternion.identity, cameraLocalRotation = Quaternion.identity;
        public float followErrorM = -1, followErrorDeg = -1;
        public readonly Matrix4x4[] views = new Matrix4x4[2];
        public readonly Matrix4x4[] projections = new Matrix4x4[2];
        public int forwardSelected = -1;
        public string cascadeSelected = "";
        public string files = "";
        public readonly List<string> reasons = new List<string>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CloneVertex
    {
        public Vector3 position;
        public Vector3 normal;
        public Vector2 uv0;
    }

    private readonly Mesh[] _sourceMeshes = new Mesh[Categories.Length];
    private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();
    private readonly List<string> _runReasons = new List<string>();
    private readonly List<string> _failures = new List<string>();
    private readonly List<Capture> _captures = new List<Capture>();
    private readonly Matrix4x4[] _views = new Matrix4x4[2];
    private readonly Matrix4x4[] _projections = new Matrix4x4[2];

    private TargetsFile _targets;
    private StreamWriter _events;
    private StreamWriter _frames;
    private Transform _xrOrigin;
    private Transform _ground;
    private UniversalRenderPipelineAsset _pipeline;
    private Material _indexedMaterial, _indexedShadowMaterial, _culledMaterial, _culledShadowMaterial;
    private MaterialPropertyBlock _properties;
    private VpCpuGeometryPool _pool;
    private VpGpuIndexedGeometryBuffers _buffers;
    private VpIndexedIndirectDrawBatch _batch;
    private VpCulledInstanceSet _set;
    private VpCulledCameraRoute _route;
    private AtlasCopyPass _atlasPass;
    private EyeCopyPass _eyePass;
    private Camera _camera;
    private Vector3 _layoutCenter;
    private int _columns, _rows;
    private bool _singlePassInstanced;
    private bool _issuing;
    private string _simLogPath;
    private long _simLogOffset;
    private string _simState = "UNKNOWN";
    private string _simVersion = "";
    private string _simProfile = "";
    private int _readyFrame = -1, _openedFrame = -1, _completeFrame = -1;
    private double _openedTime = -1;
    private int _captureFrame = -1;
    private Capture _pendingCapture;
    private int _recordTransitions;
    private Vector3 _lastHead;
    private Quaternion _lastHeadRotation = Quaternion.identity;
    private float _lastFollowM, _lastFollowDeg;
    private Vector3 _lastCameraWorld, _lastCameraLocal;
    private Quaternion _lastCameraWorldRotation = Quaternion.identity, _lastCameraLocalRotation = Quaternion.identity;
    // Temporary measurement state for the Operator check (right-hand controller).
    private bool _rhValid, _rhPrimary, _rhSecondary;
    private float _rhTrigger;
    private bool _rhPrimaryPrevious, _rhSecondaryPrevious, _rhSeen;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        s_mode = Arg("-xrsimMode");
        if (s_mode == null)
        {
            return;
        }

        string error = null;
        s_out = Arg("-xrsimOut");
        s_view = Arg("-xrsimView") ?? "along";
        s_path = Arg("-xrsimPath") ?? "VP3";
        s_run = Arg("-xrsimRun") ?? "-";
        s_targetsPath = Arg("-xrsimTargets");
        if (s_mode != "hold" && s_mode != "replay" && s_mode != "noreplay")
        {
            error = "-xrsimMode must be hold, replay or noreplay, was " + s_mode;
        }
        else if (string.IsNullOrEmpty(s_out))
        {
            error = "-xrsimOut is required";
        }
        else if (s_view != "along" && s_view != "across")
        {
            error = "-xrsimView must be along or across, was " + s_view;
        }
        else if (s_path != "VP3" && s_path != "VP3C")
        {
            error = "-xrsimPath must be VP3 or VP3C, was " + s_path;
        }
        else if (s_mode == "replay" && string.IsNullOrEmpty(s_targetsPath))
        {
            error = "-xrsimTargets is required for replay";
        }

        if (error != null)
        {
            Debug.LogError(Prefix + "FAILED " + error);
            Application.Quit(2);
            return;
        }

        s_stopFile = Arg("-xrsimStopFile") ?? Path.Combine(s_out, "stop");
        if (Arg("-xrsimNoReplaySeconds") != null)
        {
            s_noReplaySeconds = Array.ConvertAll(Arg("-xrsimNoReplaySeconds").Split(','), value => float.Parse(value, CultureInfo.InvariantCulture));
        }

        // Test-only focus experiment, opt-in with -xrsimIgnoreFocus 1 and off by default. The recording keys
        // need the Simulator window to hold the OS focus, which freezes the Player's TrackedPoseDriver.
        // IgnoreFocus only stops devices being reset and disabled on focus loss (it needs runInBackground to
        // take effect); it does not by itself guarantee that the XR runtime keeps tracking. Measured, not assumed.
        s_ignoreFocus = Arg("-xrsimIgnoreFocus") == "1";
        if (s_ignoreFocus)
        {
            Application.runInBackground = true;
            InputSystemRuntime.settings.backgroundBehavior = InputSystemSettings.BackgroundBehavior.IgnoreFocus;
        }

        Directory.CreateDirectory(Path.Combine(s_out, "captures"));
        new GameObject("XrSimHarness").AddComponent<XrSimHarness>();
    }

    private static string Arg(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static string F(double value, string format = "R")
    {
        return value.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string J(string text)
    {
        var builder = new StringBuilder("\"");
        foreach (char c in text ?? "")
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private static string JV(Vector3 v) => "[" + F(v.x) + "," + F(v.y) + "," + F(v.z) + "]";

    private static string JQ(Quaternion q) => "[" + F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w) + "]";

    private static string JM(Matrix4x4 m)
    {
        var parts = new string[16];
        for (int i = 0; i < 16; i++)
        {
            parts[i] = F(m[i / 4, i % 4]);
        }

        return "[" + string.Join(",", parts) + "]";
    }

    private static string JList(IEnumerable<string> items)
    {
        var parts = new List<string>();
        foreach (string item in items)
        {
            parts.Add(J(item));
        }

        return "[" + string.Join(",", parts) + "]";
    }

    private void Event(string type, string detail)
    {
        string line = "{\"frame\":" + Time.frameCount + ",\"realtime\":" + F(Time.realtimeSinceStartupAsDouble) + ",\"type\":" + J(type) + ",\"detail\":" + J(detail) + "}";
        _events?.WriteLine(line);
        Debug.Log(Prefix + type + " " + detail);
    }

    private void Fail(string reason)
    {
        _failures.Add(reason);
        Debug.LogError(Prefix + "FAILED " + reason);
        _events?.WriteLine("{\"frame\":" + Time.frameCount + ",\"realtime\":" + F(Time.realtimeSinceStartupAsDouble) + ",\"type\":\"failure\",\"detail\":" + J(reason) + "}");
    }

    private IEnumerator Start()
    {
        _events = new StreamWriter(Path.Combine(s_out, "events.jsonl")) { AutoFlush = true };
        Event("start", "mode=" + s_mode + " view=" + s_view + " path=" + s_path + " run=" + s_run + " pid=" + Process.GetCurrentProcess().Id
            + " xr_runtime_json=" + Environment.GetEnvironmentVariable("XR_RUNTIME_JSON") + " unity=" + Application.unityVersion);
        if (s_mode == "replay" && !LoadTargets())
        {
            Finish();
            yield break;
        }

        SceneSetUp();
        if (_failures.Count > 0)
        {
            Finish();
            yield break;
        }

        float waited = 0f;
        while (!XrRunning() && waited < XrTimeoutSeconds)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        int pid = Process.GetCurrentProcess().Id;
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MetaXR", "MetaXrSimulator", "logs");
        for (int i = 0; i < 120 && _simLogPath == null; i++)
        {
            if (Directory.Exists(logs))
            {
                string[] found = Directory.GetFiles(logs, "meta_xrsim_*_" + pid + ".log");
                _simLogPath = found.Length > 0 ? found[0] : null;
            }

            yield return null;
        }

        PollSimLog();
        _singlePassInstanced = XRSettings.isDeviceActive && XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced;
        WriteEnvironment(waited);
        if (!XrRunning())
        {
            Fail("xr_not_running: XR did not start within " + F(waited, "F1") + " s");
            Finish();
            yield break;
        }

        if (!_singlePassInstanced || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
        {
            // The VP3C route's guards are not relaxed for other modes or APIs.
            Fail("unsupported_player_condition: stereo " + XRSettings.stereoRenderingMode + ", graphics " + SystemInfo.graphicsDeviceType + " (needs Single Pass Instanced on Direct3D 11)");
            Finish();
            yield break;
        }

        for (int i = 0; i < 30; i++)
        {
            PollSimLog();
            yield return null;
        }

        Prepare();
        if (_failures.Count > 0)
        {
            Finish();
            yield break;
        }

        _camera = Camera.main;
        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        for (int pass = 0; pass < 2; pass++)
        {
            AimView();
            for (int i = 0; i < 10; i++)
            {
                PollSimLog();
                yield return null;
            }
        }

        if (!Activate())
        {
            Finish();
            yield break;
        }

        _frames = new StreamWriter(Path.Combine(s_out, "frames.csv")) { AutoFlush = true };
        _frames.WriteLine("frame,realtime_s,sim_state,replay_time_s,head_px,head_py,head_pz,head_qx,head_qy,head_qz,head_qw,"
            + "camera_px,camera_py,camera_pz,camera_qx,camera_qy,camera_qz,camera_qw,camera_local_px,camera_local_py,camera_local_pz,"
            + "camera_local_qx,camera_local_qy,camera_local_qz,camera_local_qw,follow_error_m,follow_error_deg,"
            + MatrixHeader("left_view") + "," + MatrixHeader("left_proj") + "," + MatrixHeader("right_view") + "," + MatrixHeader("right_proj")
            + ",rh_valid,rh_primary,rh_secondary,rh_trigger,event");
        for (int i = 0; i < SettleFrames; i++)
        {
            PollSimLog();
            yield return null;
        }

        _readyFrame = Time.frameCount;
        WriteReady();
        switch (s_mode)
        {
            case "hold":
                yield return HoldFlow();
                break;
            case "replay":
                yield return ReplayFlow();
                break;
            default:
                yield return NoReplayFlow();
                break;
        }

        Finish();
    }

    private bool LoadTargets()
    {
        try
        {
            _targets = JsonUtility.FromJson<TargetsFile>(File.ReadAllText(s_targetsPath));
        }
        catch (Exception exception)
        {
            Fail("targets_unreadable: " + exception.Message);
            return false;
        }

        if (_targets == null || _targets.poses == null || _targets.poses.Length == 0)
        {
            Fail("targets_empty: " + s_targetsPath);
            return false;
        }

        Event("targets", _targets.poses.Length + " poses, tolerance " + F(_targets.positionTolerance) + " m / " + F(_targets.angleToleranceDeg) + " deg, stationary frames " + _targets.stationaryFrames);
        return true;
    }

    private void SceneSetUp()
    {
        foreach (string path in Phase092Displays)
        {
            GameObject display = FindPath(path);
            if (display != null)
            {
                display.SetActive(false);
            }
        }

        GameObject origin = FindPath("XR Origin");
        GameObject grid = FindPath("Unity Mesh Display/Adopted Grid");
        GameObject ground = FindPath("Unity Mesh Display/Adopted Grid/Grid Ground");
        if (origin == null || grid == null || ground == null)
        {
            Fail("scene_mismatch: XR Origin, Adopted Grid or Grid Ground missing");
            return;
        }

        _xrOrigin = origin.transform;
        _ground = ground.transform;
        foreach (Transform child in grid.transform)
        {
            if (child == _ground)
            {
                continue;
            }

            int category = Array.FindIndex(Categories, name => child.name.StartsWith(name + " ", StringComparison.Ordinal));
            MeshFilter filter = child.GetComponent<MeshFilter>();
            if (category >= 0 && filter != null && _sourceMeshes[category] == null)
            {
                _sourceMeshes[category] = filter.sharedMesh;
            }
        }

        for (int c = 0; c < Categories.Length; c++)
        {
            if (_sourceMeshes[c] == null || !_sourceMeshes[c].isReadable)
            {
                Fail("scene_mismatch: grid mesh " + Categories[c] + " missing or not readable (licensed display meshes are required)");
                return;
            }
        }

        foreach (Renderer sceneRenderer in FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (sceneRenderer.transform != _ground)
            {
                sceneRenderer.enabled = false;
            }
        }

        _pipeline = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (_pipeline == null)
        {
            Fail("scene_mismatch: current render pipeline is not URP");
            return;
        }

        Material indexed = Resources.Load<Material>("XrSimHarnessIndexed");
        Material indexedShadow = Resources.Load<Material>("XrSimHarnessIndexedShadow");
        Material culled = Resources.Load<Material>("XrSimHarnessCulled");
        Material culledShadow = Resources.Load<Material>("XrSimHarnessCulledShadow");
        if (indexed == null || indexedShadow == null || culled == null || culledShadow == null)
        {
            Fail("build_mismatch: harness materials are not in the build");
            return;
        }

        _indexedMaterial = new Material(indexed);
        _indexedShadowMaterial = new Material(indexedShadow);
        _culledMaterial = new Material(culled);
        _culledShadowMaterial = new Material(culledShadow);
        _indexedMaterial.SetColor("_BaseColor", Color.white);
        _culledMaterial.SetColor("_BaseColor", Color.white);
        _properties = new MaterialPropertyBlock();
    }

    private void WriteEnvironment(float xrWaitSeconds)
    {
        foreach (XRDisplaySubsystem display in _displays)
        {
            display.TryGetDisplayRefreshRate(out float rate);
            _refreshRate = rate;
        }

        string json = "{\"unity_version\":" + J(Application.unityVersion) + ",\"xr_running\":" + (XrRunning() ? "true" : "false")
            + ",\"xr_wait_s\":" + F(xrWaitSeconds) + ",\"xr_device\":" + J(XRSettings.loadedDeviceName)
            + ",\"stereo_mode\":" + J(XRSettings.stereoRenderingMode.ToString()) + ",\"single_pass_instanced\":" + (_singlePassInstanced ? "true" : "false")
            + ",\"graphics_api\":" + J(SystemInfo.graphicsDeviceType.ToString()) + ",\"refresh_rate\":" + F(_refreshRate)
            + ",\"eye_texture\":[" + XRSettings.eyeTextureWidth + "," + XRSettings.eyeTextureHeight + "]"
            + ",\"development_build\":" + (Debug.isDebugBuild ? "true" : "false")
            + ",\"ignore_focus\":" + (s_ignoreFocus ? "true" : "false")
            + ",\"run_in_background\":" + (Application.runInBackground ? "true" : "false")
            + ",\"input_background_behavior\":" + J(InputSystemRuntime.settings.backgroundBehavior.ToString())
            + ",\"xr_runtime_json\":" + J(Environment.GetEnvironmentVariable("XR_RUNTIME_JSON"))
            + ",\"simulator_log\":" + J(_simLogPath) + ",\"simulator_version\":" + J(_simVersion) + ",\"simulator_device_profile\":" + J(_simProfile)
            + ",\"view\":" + J(s_view) + ",\"path\":" + J(s_path) + ",\"mode\":" + J(s_mode) + ",\"run\":" + J(s_run) + "}";
        File.WriteAllText(Path.Combine(s_out, "environment.json"), json);
        Event("environment", json);
    }

    private float _refreshRate;

    private void Prepare()
    {
        int variants = Geometries / Categories.Length;
        var clones = new Mesh[Geometries];
        var cloneBounds = new Bounds[Geometries];
        var cloneCategory = new int[Geometries];
        long totalVertices = 0, totalIndices = 0;
        for (int v = 0; v < variants; v++)
        {
            for (int c = 0; c < Categories.Length; c++)
            {
                int g = v * Categories.Length + c;
                clones[g] = CloneVariant(_sourceMeshes[c], c, v, variants, out int vertexCount, out int indexCount);
                cloneBounds[g] = clones[g].bounds;
                cloneCategory[g] = c;
                totalVertices += vertexCount;
                totalIndices += indexCount;
            }
        }

        _columns = Mathf.CeilToInt(Mathf.Sqrt(1.5f * Instances));
        while (Instances % _columns != 0)
        {
            _columns++;
        }

        _rows = Instances / _columns;
        var perGeometry = new int[Geometries];
        for (int i = 0; i < Geometries; i++)
        {
            perGeometry[i] = Instances / Geometries + (i < Instances % Geometries ? 1 : 0);
        }

        var slotGeometry = new int[Instances];
        var assigned = new int[Geometries];
        for (int slot = 0; slot < Instances;)
        {
            for (int i = 0; i < Geometries && slot < Instances; i++)
            {
                if (assigned[i] < perGeometry[i])
                {
                    slotGeometry[slot++] = i;
                    assigned[i]++;
                }
            }
        }

        var slotMatrix = new Matrix4x4[Instances];
        for (int s = 0; s < Instances; s++)
        {
            int category = cloneCategory[slotGeometry[s]];
            uint hash = (uint)s * 2654435761u;
            hash ^= hash >> 15;
            var position = new Vector3((s % _columns - (_columns - 1) * 0.5f) * Spacing, CategoryHeight[category], LayoutCenterZ + (s / _columns - (_rows - 1) * 0.5f) * Spacing);
            slotMatrix[s] = Matrix4x4.TRS(position, Quaternion.Euler(0f, (hash % 3600u) * 0.1f, 0f), Vector3.one * CategoryScale[category]);
        }

        var objectToWorlds = new Matrix4x4[Instances];
        int k = 0;
        for (int g = 0; g < Geometries; g++)
        {
            for (int s = 0; s < Instances; s++)
            {
                if (slotGeometry[s] == g)
                {
                    objectToWorlds[k++] = slotMatrix[s];
                }
            }
        }

        _layoutCenter = new Vector3(0f, 0f, LayoutCenterZ);
        _ground.position = new Vector3(0f, -0.05f, LayoutCenterZ);
        _ground.localScale = new Vector3(_columns * Spacing + 3f, 0.1f, _rows * Spacing + 3f);

        _pool = new VpCpuGeometryPool((int)totalVertices, (int)totalIndices, Allocator.Persistent);
        var commands = new VpIndirectCommand[Geometries];
        for (int g = 0; g < Geometries; g++)
        {
            if (!_pool.TryAppend(clones[g], out VpGeometryRange range))
            {
                Fail("prepare: VP pool append " + g);
                return;
            }

            commands[g] = new VpIndirectCommand(range, cloneBounds[g], perGeometry[g]);
        }

        foreach (Mesh clone in clones)
        {
            Destroy(clone);
        }

        _buffers = new VpGpuIndexedGeometryBuffers((int)totalVertices, (int)totalIndices);
        _batch = new VpIndexedIndirectDrawBatch(Geometries, Instances);
        _set = new VpCulledInstanceSet(Geometries, Instances);
        if (!_buffers.TryUpload(_pool) || !_batch.TryUpload(commands, objectToWorlds, _singlePassInstanced) || !_set.TryUpload(commands, objectToWorlds, _singlePassInstanced))
        {
            Fail("prepare: VP3 / VP3C upload");
            return;
        }

        _route = new VpCulledCameraRoute(_batch, _set, _buffers, _indexedMaterial, _indexedShadowMaterial, _culledMaterial, _culledShadowMaterial, 0);
        int cascades = _pipeline.shadowCascadeCount;
        int size = _pipeline.mainLightShadowmapResolution;
        _atlasPass = new AtlasCopyPass(size, cascades == 2 ? size >> 1 : size);
        _eyePass = new EyeCopyPass();
        Event("prepare", "grid " + _columns + "x" + _rows + ", geometries " + Geometries + ", instances " + Instances + ", vertices " + totalVertices + ", indices " + totalIndices
            + ", shadow cascades " + cascades + ", atlas " + size);
    }

    private static Mesh CloneVariant(Mesh source, int category, int variant, int variants, out int vertexCount, out int indexCount)
    {
        using (Mesh.MeshDataArray dataArray = Mesh.AcquireReadOnlyMeshData(source))
        {
            Mesh.MeshData data = dataArray[0];
            vertexCount = data.vertexCount;
            indexCount = 0;
            for (int s = 0; s < data.subMeshCount; s++)
            {
                indexCount += data.GetSubMesh(s).indexCount;
            }

            var positions = new NativeArray<Vector3>(vertexCount, Allocator.TempJob);
            var normals = new NativeArray<Vector3>(vertexCount, Allocator.TempJob);
            var uvs = new NativeArray<Vector2>(vertexCount, Allocator.TempJob);
            var vertices = new NativeArray<CloneVertex>(vertexCount, Allocator.TempJob);
            var indices = new NativeArray<uint>(indexCount, Allocator.TempJob);
            try
            {
                data.GetVertices(positions);
                data.GetNormals(normals);
                if (data.HasVertexAttribute(VertexAttribute.TexCoord0))
                {
                    data.GetUVs(0, uvs);
                }

                Quaternion turn = Quaternion.Euler(0f, 360f * variant / variants, 0f);
                for (int v = 0; v < vertexCount; v++)
                {
                    vertices[v] = new CloneVertex { position = turn * positions[v], normal = turn * normals[v], uv0 = uvs[v] };
                }

                int written = 0;
                for (int s = 0; s < data.subMeshCount; s++)
                {
                    int count = data.GetSubMesh(s).indexCount;
                    using (var subMeshIndices = new NativeArray<int>(count, Allocator.TempJob))
                    {
                        data.GetIndices(subMeshIndices, s, true);
                        for (int i = 0; i < count; i++)
                        {
                            indices[written + i] = (uint)subMeshIndices[i];
                        }
                    }

                    written += count;
                }

                const MeshUpdateFlags flags = MeshUpdateFlags.DontValidateIndices | MeshUpdateFlags.DontNotifyMeshUsers | MeshUpdateFlags.DontRecalculateBounds;
                var mesh = new Mesh { name = "XrSimHarness " + Categories[category] + " v" + variant };
                mesh.SetVertexBufferParams(vertexCount,
                    new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2));
                mesh.SetVertexBufferData(vertices, 0, 0, vertexCount, 0, flags);
                mesh.SetIndexBufferParams(indexCount, IndexFormat.UInt32);
                mesh.SetIndexBufferData(indices, 0, 0, indexCount, flags);
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new SubMeshDescriptor(0, written, MeshTopology.Triangles), flags);
                mesh.RecalculateBounds();
                return mesh;
            }
            finally
            {
                positions.Dispose();
                normals.Dispose();
                uvs.Dispose();
                vertices.Dispose();
                indices.Dispose();
            }
        }
    }

    // Moves and turns the XR Origin so the tracked camera sits at the view's world pose (as the benchmark views).
    private void AimView()
    {
        Vector3 shadow = Vector3.forward;
        foreach (Light sceneLight in FindObjectsByType<Light>(FindObjectsSortMode.None))
        {
            if (sceneLight.type == LightType.Directional && sceneLight.shadows != LightShadows.None)
            {
                Vector3 forward = sceneLight.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude > 1e-6f)
                {
                    shadow = forward.normalized;
                    break;
                }
            }
        }

        Vector3 side = Vector3.Cross(Vector3.up, shadow);
        Vector3 axis = s_view == "along" ? shadow : side;
        float extent = (s_view == "along" ? _rows : _columns) * Spacing * 0.2f;
        Vector3 position = _layoutCenter - axis * extent + Vector3.up * 2.5f;
        Quaternion rotation = Quaternion.LookRotation(axis * Mathf.Cos(30f * Mathf.Deg2Rad) + Vector3.down * Mathf.Sin(30f * Mathf.Deg2Rad), Vector3.up);
        Transform camera = _camera.transform;
        Quaternion originInverse = Quaternion.Inverse(_xrOrigin.rotation);
        Quaternion localRotation = originInverse * camera.rotation;
        Vector3 localPosition = originInverse * (camera.position - _xrOrigin.position);
        _xrOrigin.rotation = rotation * Quaternion.Inverse(localRotation);
        _xrOrigin.position = position - _xrOrigin.rotation * localPosition;
    }

    private bool Activate()
    {
        if (s_path == "VP3C")
        {
            bool first = _route.TryEnable(_camera, out string refusal);
            bool second = _route.TryEnable(_camera, out string secondRefusal);
            Event("route", "enable first=" + first + " second=" + second + " refusal=" + (refusal ?? secondRefusal ?? "none"));
            if (!first || !second)
            {
                Fail("vp3c_refused: " + (refusal ?? secondRefusal));
                return false;
            }
        }

        _issuing = true;
        return true;
    }

    private void LateUpdate()
    {
        if (!_issuing)
        {
            return;
        }

        if (s_path == "VP3C")
        {
            _route.Issue();
        }
        else
        {
            _batch.Render(_indexedMaterial, _indexedShadowMaterial, _properties, _buffers, 0);
        }
    }

    private void OnBeginCamera(ScriptableRenderContext context, Camera camera)
    {
        if (camera != _camera)
        {
            return;
        }

        RecordFrame(camera);
        if (_captureFrame == Time.frameCount && _pendingCapture != null)
        {
            ScriptableRenderer renderer = camera.GetUniversalAdditionalCameraData().scriptableRenderer;
            renderer.EnqueuePass(_atlasPass);
            renderer.EnqueuePass(_eyePass);
            Capture capture = _pendingCapture;
            capture.frame = Time.frameCount;
            capture.replayTime = _openedTime >= 0 ? Time.realtimeSinceStartupAsDouble - _openedTime : -1;
            capture.simState = _simState;
            capture.head = _lastHead;
            capture.headRotation = _lastHeadRotation;
            capture.cameraWorld = _lastCameraWorld;
            capture.cameraWorldRotation = _lastCameraWorldRotation;
            capture.cameraLocal = _lastCameraLocal;
            capture.cameraLocalRotation = _lastCameraLocalRotation;
            capture.followErrorM = _lastFollowM;
            capture.followErrorDeg = _lastFollowDeg;
            for (int eye = 0; eye < 2; eye++)
            {
                capture.views[eye] = _views[eye];
                capture.projections[eye] = _projections[eye];
            }
        }
    }

    private void RecordFrame(Camera camera)
    {
        for (int eye = 0; eye < 2; eye++)
        {
            var stereoEye = eye == 0 ? Camera.StereoscopicEye.Left : Camera.StereoscopicEye.Right;
            _views[eye] = camera.stereoEnabled ? camera.GetStereoViewMatrix(stereoEye) : camera.worldToCameraMatrix;
            _projections[eye] = camera.stereoEnabled ? camera.GetStereoProjectionMatrix(stereoEye) : camera.projectionMatrix;
        }

        ReadHead(out _lastHead, out _lastHeadRotation);
        ReadRightButtons(out _rhValid, out _rhPrimary, out _rhSecondary, out _rhTrigger);
        if (!_rhSeen || _rhPrimary != _rhPrimaryPrevious || _rhSecondary != _rhSecondaryPrevious)
        {
            Event("controller_button", "right primary=" + (_rhPrimary ? "on" : "off") + " secondary=" + (_rhSecondary ? "on" : "off")
                + " trigger=" + F(_rhTrigger) + " device_valid=" + (_rhValid ? "true" : "false"));
            _rhPrimaryPrevious = _rhPrimary;
            _rhSecondaryPrevious = _rhSecondary;
            _rhSeen = true;
        }

        Transform t = camera.transform;
        _lastCameraWorld = t.position;
        _lastCameraWorldRotation = t.rotation;
        _lastCameraLocal = t.localPosition;
        _lastCameraLocalRotation = t.localRotation;
        _lastFollowM = Vector3.Distance(_lastCameraLocal, _lastHead);
        _lastFollowDeg = Quaternion.Angle(_lastCameraLocalRotation, _lastHeadRotation);
        if (_frames == null)
        {
            return;
        }

        double replayTime = _openedTime >= 0 ? Time.realtimeSinceStartupAsDouble - _openedTime : -1;
        string label = _captureFrame == Time.frameCount && _pendingCapture != null ? _pendingCapture.name : "";
        var row = new StringBuilder(1600);
        row.Append(Time.frameCount).Append(',').Append(F(Time.realtimeSinceStartupAsDouble)).Append(',').Append(_simState).Append(',').Append(F(replayTime)).Append(',')
            .Append(Csv(_lastHead)).Append(',').Append(Csv(_lastHeadRotation)).Append(',')
            .Append(Csv(_lastCameraWorld)).Append(',').Append(Csv(_lastCameraWorldRotation)).Append(',')
            .Append(Csv(_lastCameraLocal)).Append(',').Append(Csv(_lastCameraLocalRotation)).Append(',')
            .Append(F(_lastFollowM)).Append(',').Append(F(_lastFollowDeg)).Append(',')
            .Append(Csv(_views[0])).Append(',').Append(Csv(_projections[0])).Append(',').Append(Csv(_views[1])).Append(',').Append(Csv(_projections[1])).Append(',')
            .Append(_rhValid ? 1 : 0).Append(',').Append(_rhPrimary ? 1 : 0).Append(',').Append(_rhSecondary ? 1 : 0).Append(',').Append(F(_rhTrigger)).Append(',')
            .Append(label);
        _frames.WriteLine(row.ToString());
    }

    private static string Csv(Vector3 v) => F(v.x) + "," + F(v.y) + "," + F(v.z);

    private static string Csv(Quaternion q) => F(q.x) + "," + F(q.y) + "," + F(q.z) + "," + F(q.w);

    private static string Csv(Matrix4x4 m)
    {
        var parts = new string[16];
        for (int i = 0; i < 16; i++)
        {
            parts[i] = F(m[i / 4, i % 4]);
        }

        return string.Join(",", parts);
    }

    private static string MatrixHeader(string name)
    {
        var parts = new string[16];
        for (int i = 0; i < 16; i++)
        {
            parts[i] = name + "_m" + (i / 4) + (i % 4);
        }

        return string.Join(",", parts);
    }

    private static void ReadHead(out Vector3 position, out Quaternion rotation)
    {
        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.CenterEye);
        if (!head.TryGetFeatureValue(CommonUsages.centerEyePosition, out position))
        {
            position = Vector3.zero;
        }

        if (!head.TryGetFeatureValue(CommonUsages.centerEyeRotation, out rotation))
        {
            rotation = Quaternion.identity;
        }
    }

    // Temporary measurement log for the Meta XR Operator check: the harness otherwise reads no controller
    // state, so there would be no app-side evidence that a button reached the application. Right-hand
    // primaryButton is the A button that Operator's openxr_set_controller_input drives.
    private static void ReadRightButtons(out bool valid, out bool primary, out bool secondary, out float trigger)
    {
        InputDevice hand = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
        valid = hand.isValid;
        if (!hand.TryGetFeatureValue(CommonUsages.primaryButton, out primary))
        {
            primary = false;
        }

        if (!hand.TryGetFeatureValue(CommonUsages.secondaryButton, out secondary))
        {
            secondary = false;
        }

        if (!hand.TryGetFeatureValue(CommonUsages.trigger, out trigger))
        {
            trigger = 0f;
        }
    }

    private void WriteReady()
    {
        bool following = _lastFollowM <= FollowPositionTolerance && _lastFollowDeg <= FollowAngleTolerance;
        string json = "{\"frame\":" + _readyFrame + ",\"realtime\":" + F(Time.realtimeSinceStartupAsDouble) + ",\"sim_state\":" + J(_simState)
            + ",\"replay_opened_frame\":" + _openedFrame + ",\"head_position\":" + JV(_lastHead) + ",\"head_rotation\":" + JQ(_lastHeadRotation)
            + ",\"camera_world_position\":" + JV(_lastCameraWorld) + ",\"camera_world_rotation\":" + JQ(_lastCameraWorldRotation)
            + ",\"camera_local_position\":" + JV(_lastCameraLocal) + ",\"camera_local_rotation\":" + JQ(_lastCameraLocalRotation)
            + ",\"follow_error_m\":" + F(_lastFollowM) + ",\"follow_error_deg\":" + F(_lastFollowDeg) + ",\"camera_following\":" + (following ? "true" : "false")
            + ",\"simulator_version\":" + J(_simVersion) + ",\"simulator_device_profile\":" + J(_simProfile)
            + ",\"stereo_mode\":" + J(XRSettings.stereoRenderingMode.ToString()) + ",\"graphics_api\":" + J(SystemInfo.graphicsDeviceType.ToString())
            + ",\"vp3c_route_enabled\":" + (_route != null && _route.IsEnabled ? "true" : "false") + "}";
        File.WriteAllText(Path.Combine(s_out, "ready.json"), json);
        Event("ready", json);
        if (!following)
        {
            _runReasons.Add("camera_not_following_at_ready: follow error " + F(_lastFollowM, "F4") + " m / " + F(_lastFollowDeg, "F3") + " deg");
        }
    }

    private IEnumerator HoldFlow()
    {
        float held = 0f;
        while (!File.Exists(s_stopFile) && held < 900f && XrRunning())
        {
            PollSimLog();
            held += Time.unscaledDeltaTime;
            yield return null;
        }

        PollSimLog();
        Event("hold_end", "held " + F(held, "F1") + " s, stop file " + File.Exists(s_stopFile) + ", xr " + XrRunning() + ", record transitions " + _recordTransitions);
    }

    private IEnumerator ReplayFlow()
    {
        if (_openedFrame >= 0)
        {
            _runReasons.Add("replay_opened_before_ready: opened at frame " + _openedFrame + ", ready at frame " + _readyFrame + " (the view was fixed while the recorded head pose was already applied)");
        }

        float waited = 0f;
        while (_openedFrame < 0 && waited < 120f && XrRunning())
        {
            PollSimLog();
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (_openedFrame < 0)
        {
            Fail("replay_not_opened: no \"Opened '<file>.vrs'\" in the Simulator log within " + F(waited, "F1") + " s");
            yield break;
        }

        foreach (PoseTarget target in _targets.poses)
        {
            _captures.Add(new Capture { name = target.name });
        }

        int next = 0;
        int stationary = 0;
        Vector3 previous = new Vector3(float.NaN, 0f, 0f);
        Quaternion previousRotation = Quaternion.identity;
        float elapsed = 0f;
        while (next < _captures.Count && XrRunning() && elapsed < _targets.timeoutSeconds)
        {
            PollSimLog();
            ReadHead(out Vector3 head, out Quaternion rotation);
            bool still = !float.IsNaN(previous.x) && Vector3.Distance(head, previous) < 0.001f && Quaternion.Angle(rotation, previousRotation) < 0.05f;
            previous = head;
            previousRotation = rotation;
            int match = MatchTarget(head, rotation, next);
            if (match > next)
            {
                for (int skipped = next; skipped < match; skipped++)
                {
                    _captures[skipped].reasons.Add("skipped: the head reached " + _targets.poses[match].name + " before " + _targets.poses[skipped].name + " was captured");
                    Event("target_skipped", _targets.poses[skipped].name + " (head at " + _targets.poses[match].name + ")");
                }

                next = match;
                stationary = 0;
            }

            stationary = match == next && still ? stationary + 1 : 0;
            if (match == next && stationary >= _targets.stationaryFrames)
            {
                Capture capture = _captures[next];
                _pendingCapture = capture;
                _captureFrame = Time.frameCount;
                yield return null;
                SaveCapture(capture, next);
                _pendingCapture = null;
                next++;
                stationary = 0;
                continue;
            }

            elapsed += Time.unscaledDeltaTime;
            yield return null;
        }

        for (int i = next; i < _captures.Count; i++)
        {
            _captures[i].reasons.Add(!XrRunning() ? "not captured: the XR session ended" : _completeFrame >= 0 ? "not captured: the replay completed first" : "not captured: timeout");
        }

        waited = 0f;
        while (XrRunning() && waited < 20f)
        {
            PollSimLog();
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        PollSimLog();
        Event("replay_end", "opened frame " + _openedFrame + ", complete frame " + _completeFrame + ", xr " + XrRunning());
    }

    private IEnumerator NoReplayFlow()
    {
        double start = Time.realtimeSinceStartupAsDouble;
        for (int i = 0; i < s_noReplaySeconds.Length; i++)
        {
            var capture = new Capture { name = "N" + (i + 1) };
            _captures.Add(capture);
            while (Time.realtimeSinceStartupAsDouble - start < s_noReplaySeconds[i] && XrRunning())
            {
                PollSimLog();
                yield return null;
            }

            if (!XrRunning())
            {
                capture.reasons.Add("not captured: the XR session ended");
                continue;
            }

            _pendingCapture = capture;
            _captureFrame = Time.frameCount;
            yield return null;
            SaveCapture(capture, i);
            _pendingCapture = null;
        }

        if (_openedFrame >= 0)
        {
            _runReasons.Add("replay_opened_in_noreplay: a recording was replayed in the control run");
        }
    }

    private int MatchTarget(Vector3 head, Quaternion rotation, int from)
    {
        for (int i = from; i < _targets.poses.Length; i++)
        {
            PoseTarget t = _targets.poses[i];
            var position = new Vector3(t.px, t.py, t.pz);
            var targetRotation = new Quaternion(t.qx, t.qy, t.qz, t.qw);
            if (Vector3.Distance(head, position) <= _targets.positionTolerance && Quaternion.Angle(rotation, targetRotation) <= _targets.angleToleranceDeg)
            {
                return i;
            }
        }

        return -1;
    }

    private void SaveCapture(Capture capture, int index)
    {
        capture.captured = capture.frame == _captureFrame;
        if (!capture.captured)
        {
            capture.reasons.Add("not captured: the main camera did not render in the capture frame");
            return;
        }

        if (s_mode == "replay")
        {
            if (capture.simState != "REPLAY")
            {
                capture.reasons.Add("replay not in progress at capture (state " + capture.simState + ")");
            }

            if (_completeFrame >= 0 && _completeFrame <= capture.frame)
            {
                capture.reasons.Add("the replay had completed at frame " + _completeFrame);
            }
        }

        if (capture.followErrorM > FollowPositionTolerance || capture.followErrorDeg > FollowAngleTolerance)
        {
            capture.reasons.Add("camera not following the head: " + F(capture.followErrorM, "F4") + " m / " + F(capture.followErrorDeg, "F3") + " deg");
        }

        string stem = Path.Combine(s_out, "captures", capture.name);
        string eyes = _eyePass.LastFrame == capture.frame ? _eyePass.Save(stem) : null;
        string atlas = _atlasPass.LastFrame == capture.frame ? _atlasPass.Save(stem + "_atlas.bin") : null;
        if (eyes == null)
        {
            capture.reasons.Add("eye images not copied in the capture frame");
        }

        if (atlas == null)
        {
            capture.reasons.Add("shadow atlas not copied in the capture frame");
        }

        if (s_path == "VP3C")
        {
            capture.forwardSelected = _set.ForwardSelectedInstances;
            capture.cascadeSelected = _set.ShadowSelectedInstances(0) + "/" + _set.ShadowSelectedInstances(1) + "/" + _set.ShadowSelectedInstances(2) + "/" + _set.ShadowSelectedInstances(3);
        }

        capture.files = (eyes ?? "") + ";" + (atlas ?? "");
        Event("capture", capture.name + " frame " + capture.frame + " state " + capture.simState + " follow " + F(capture.followErrorM, "F4") + " m reasons " + capture.reasons.Count);
    }

    private void PollSimLog()
    {
        if (_simLogPath == null)
        {
            return;
        }

        try
        {
            using (var stream = new FileStream(_simLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length <= _simLogOffset)
                {
                    return;
                }

                stream.Seek(_simLogOffset, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - _simLogOffset];
                int read = stream.Read(buffer, 0, buffer.Length);
                int end = read > 0 ? Array.LastIndexOf(buffer, (byte)'\n', read - 1) : -1;
                if (end < 0)
                {
                    return;
                }

                _simLogOffset += end + 1;
                foreach (string raw in Encoding.UTF8.GetString(buffer, 0, end + 1).Split('\n'))
                {
                    HandleSimLine(raw.TrimEnd('\r'));
                }
            }
        }
        catch (IOException exception)
        {
            Event("simulator_log_read_failed", exception.Message);
        }
    }

    private void HandleSimLine(string line)
    {
        Match stamp = SimLine.Match(line);
        if (!stamp.Success)
        {
            return;
        }

        string simTime = stamp.Groups[1].Value;
        Match transition = SimTransition.Match(line);
        if (transition.Success)
        {
            _simState = transition.Groups[2].Value;
            _recordTransitions += transition.Groups[2].Value == "RECORD" ? 1 : 0;
            Event("simulator_transition", transition.Groups[1].Value + "->" + transition.Groups[2].Value + " sim_time " + simTime);
            return;
        }

        if (line.IndexOf("] Opened '", StringComparison.Ordinal) >= 0 && line.EndsWith(".vrs'", StringComparison.Ordinal))
        {
            if (_openedFrame < 0)
            {
                _openedFrame = Time.frameCount;
                _openedTime = Time.realtimeSinceStartupAsDouble;
                _simState = "REPLAY";
            }

            Event("replay_opened", "sim_time " + simTime + " " + line.Substring(line.IndexOf("Opened", StringComparison.Ordinal)));
            return;
        }

        if (line.IndexOf("Playback is complete", StringComparison.Ordinal) >= 0)
        {
            if (_completeFrame < 0)
            {
                _completeFrame = Time.frameCount;
            }

            _simState = "REPLAY_COMPLETE";
            Event("replay_complete", "sim_time " + simTime + " since opened " + (_openedTime >= 0 ? F(Time.realtimeSinceStartupAsDouble - _openedTime, "F3") : "na") + " s");
            return;
        }

        Match version = SimVersion.Match(line);
        if (version.Success && line.IndexOf("Set up XrApiLayers", StringComparison.Ordinal) >= 0)
        {
            _simVersion = version.Groups[1].Value;
            return;
        }

        Match profile = SimProfile.Match(line);
        if (profile.Success)
        {
            _simProfile = profile.Groups[1].Value.Trim();
            return;
        }

        if (line.Contains("][E]") || line.IndexOf("session_capture", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            Event("simulator_log", line.Length > 400 ? line.Substring(0, 400) : line);
        }
    }

    private void Finish()
    {
        PollSimLog();
        var captures = new List<string>();
        foreach (Capture c in _captures)
        {
            captures.Add("{\"name\":" + J(c.name) + ",\"captured\":" + (c.captured ? "true" : "false") + ",\"valid\":" + (c.captured && c.reasons.Count == 0 ? "true" : "false")
                + ",\"frame\":" + c.frame + ",\"replay_time_s\":" + F(c.replayTime) + ",\"sim_state\":" + J(c.simState)
                + ",\"head_position\":" + JV(c.head) + ",\"head_rotation\":" + JQ(c.headRotation)
                + ",\"camera_world_position\":" + JV(c.cameraWorld) + ",\"camera_world_rotation\":" + JQ(c.cameraWorldRotation)
                + ",\"camera_local_position\":" + JV(c.cameraLocal) + ",\"camera_local_rotation\":" + JQ(c.cameraLocalRotation)
                + ",\"follow_error_m\":" + F(c.followErrorM) + ",\"follow_error_deg\":" + F(c.followErrorDeg)
                + ",\"left_view\":" + JM(c.views[0]) + ",\"left_projection\":" + JM(c.projections[0])
                + ",\"right_view\":" + JM(c.views[1]) + ",\"right_projection\":" + JM(c.projections[1])
                + ",\"forward_selected\":" + c.forwardSelected + ",\"cascade_selected\":" + J(c.cascadeSelected)
                + ",\"files\":" + J(c.files) + ",\"reasons\":" + JList(c.reasons) + "}");
        }

        string json = "{\"mode\":" + J(s_mode) + ",\"view\":" + J(s_view) + ",\"path\":" + J(s_path) + ",\"run\":" + J(s_run)
            + ",\"failed\":" + (_failures.Count > 0 ? "true" : "false") + ",\"failures\":" + JList(_failures)
            + ",\"run_reasons\":" + JList(_runReasons)
            + ",\"ready_frame\":" + _readyFrame + ",\"replay_opened_frame\":" + _openedFrame + ",\"replay_complete_frame\":" + _completeFrame
            + ",\"record_transitions\":" + _recordTransitions + ",\"simulator_version\":" + J(_simVersion) + ",\"simulator_device_profile\":" + J(_simProfile)
            + ",\"captures\":[" + string.Join(",", captures) + "]}";
        File.WriteAllText(Path.Combine(s_out, "result.json"), json);
        Event("finish", "failures " + _failures.Count + ", run reasons " + _runReasons.Count + ", captures " + _captures.Count);
        _frames?.Dispose();
        _frames = null;
        _events?.Dispose();
        _events = null;
        Application.Quit(_failures.Count > 0 ? 2 : 0);
    }

    private void OnDestroy()
    {
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        _route?.Dispose();
        _set?.Dispose();
        _batch?.Dispose();
        _buffers?.Dispose();
        _pool?.Dispose();
        _atlasPass?.Release();
        _eyePass?.Release();
        _frames?.Dispose();
        _events?.Dispose();
    }

    private bool XrRunning()
    {
        _displays.Clear();
        SubsystemManager.GetSubsystems(_displays);
        return XRSettings.isDeviceActive && _displays.Exists(display => display.running);
    }

    private static GameObject FindPath(string path)
    {
        string[] parts = path.Split('/');
        foreach (GameObject root in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
        {
            if (root.name != parts[0])
            {
                continue;
            }

            Transform current = root.transform;
            for (int i = 1; i < parts.Length && current != null; i++)
            {
                current = current.Find(parts[i]);
            }

            if (current != null)
            {
                return current.gameObject;
            }
        }

        return null;
    }

    // Copies URP's main light shadow atlas (after the VP3C cascade pass) into an RFloat texture in the capture frame.
    private sealed class AtlasCopyPass : ScriptableRenderPass
    {
        private readonly RTHandle _copy;

        private class CopyData
        {
            internal RG.TextureHandle source;
        }

        public AtlasCopyPass(int width, int height)
        {
            renderPassEvent = RenderPassEvent.AfterRenderingShadows + 1;
            _copy = RTHandles.Alloc(new RenderTextureDescriptor(width, height, GraphicsFormat.R32_SFloat, GraphicsFormat.None), FilterMode.Point, TextureWrapMode.Clamp, name: "XrSimHarness Atlas Copy");
        }

        public int LastFrame { get; private set; } = -1;

        public override void RecordRenderGraph(RG.RenderGraph renderGraph, ContextContainer frameData)
        {
            RG.TextureHandle shadows = frameData.Get<UniversalResourceData>().mainShadowsTexture;
            if (!shadows.IsValid())
            {
                return;
            }

            using (RG.IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass("XrSimHarness Atlas Copy", out CopyData data))
            {
                data.source = shadows;
                builder.UseTexture(shadows);
                builder.SetRenderAttachment(renderGraph.ImportTexture(_copy), 0);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (CopyData copyData, RG.RasterGraphContext context) => Blitter.BlitTexture(context.cmd, copyData.source, new Vector4(1f, 1f, 0f, 0f), 0f, false));
            }

            LastFrame = Time.frameCount;
        }

        public string Save(string path)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(_copy.rt, 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            if (request.hasError)
            {
                return null;
            }

            NativeArray<float> data = request.GetData<float>();
            var bytes = new byte[data.Length * sizeof(float)];
            Buffer.BlockCopy(data.ToArray(), 0, bytes, 0, bytes.Length);
            File.WriteAllBytes(path, bytes);
            return Path.GetFileName(path) + " " + _copy.rt.width + "x" + _copy.rt.height;
        }

        public void Release()
        {
            _copy.Release();
        }
    }

    // Copies each eye of the camera colour (a texture array under Single Pass Instanced) in the capture frame.
    private sealed class EyeCopyPass : ScriptableRenderPass
    {
        private readonly RenderTexture[] _targets = new RenderTexture[2];

        private class PassData
        {
            internal RG.TextureHandle source;
            internal RenderTexture[] targets;
            internal int eyes;
        }

        public EyeCopyPass()
        {
            renderPassEvent = RenderPassEvent.AfterRendering;
        }

        public int LastFrame { get; private set; } = -1;

        public override void RecordRenderGraph(RG.RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            RG.TextureHandle source = resources.cameraColor.IsValid() ? resources.cameraColor : resources.activeColorTexture;
            if (!source.IsValid())
            {
                return;
            }

            RG.TextureDesc desc = renderGraph.GetTextureDesc(source);
            RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
            GraphicsFormat format = desc.format != GraphicsFormat.None ? desc.format : target.graphicsFormat;
            int eyes = desc.dimension == TextureDimension.Tex2DArray ? Mathf.Min(2, desc.slices) : 1;
            for (int eye = 0; eye < 2; eye++)
            {
                if (_targets[eye] == null || _targets[eye].width != target.width || _targets[eye].height != target.height || _targets[eye].graphicsFormat != format)
                {
                    if (_targets[eye] != null)
                    {
                        _targets[eye].Release();
                    }

                    _targets[eye] = new RenderTexture(target.width, target.height, format, GraphicsFormat.None) { name = "XrSimHarness Eye Copy " + eye };
                    _targets[eye].Create();
                }
            }

            using (RG.IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("XrSimHarness Eye Copy", out PassData data))
            {
                data.source = source;
                data.targets = _targets;
                data.eyes = eyes;
                builder.UseTexture(source, RG.AccessFlags.Read);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData passData, RG.UnsafeGraphContext context) =>
                {
                    CommandBuffer native = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                    for (int eye = 0; eye < passData.eyes; eye++)
                    {
                        native.CopyTexture(passData.source, eye, 0, passData.targets[eye], 0, 0);
                    }
                });
            }

            LastFrame = eyes == 2 ? Time.frameCount : -1;
        }

        public string Save(string stem)
        {
            var saved = new List<string>();
            for (int eye = 0; eye < 2; eye++)
            {
                RenderTexture texture = _targets[eye];
                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(texture, 0, TextureFormat.RGBA32);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    return null;
                }

                string path = stem + (eye == 0 ? "_left.png" : "_right.png");
                NativeArray<byte> png = ImageConversion.EncodeNativeArrayToPNG(request.GetData<byte>(), GraphicsFormat.R8G8B8A8_UNorm, (uint)texture.width, (uint)texture.height);
                File.WriteAllBytes(path, png.ToArray());
                png.Dispose();
                saved.Add(Path.GetFileName(path));
            }

            return string.Join(",", saved);
        }

        public void Release()
        {
            foreach (RenderTexture texture in _targets)
            {
                if (texture != null)
                {
                    texture.Release();
                }
            }
        }
    }
}
