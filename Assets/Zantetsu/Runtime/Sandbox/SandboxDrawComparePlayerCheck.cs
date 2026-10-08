using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Profiling;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The scene draw comparison (TL, 2026-10-07): one scene's placed cuttables drawn either by their own MeshRenderers
    /// (<see cref="Mode.Unity"/>) or by the cut world's display in their place (<see cref="Mode.Vp3c"/>; the display's
    /// body route is the world's own -- VP3C unless the Player was started with <c>-zantetsuVp3</c>), along one camera
    /// path, so that the Main thread, the render thread and the GPU can be set side by side. An experiment of its own,
    /// started by its own argument: it is not the city walk, changes nothing of it, and does not run beside it.
    /// <para>
    /// **What does not run.** No crowd -- its planning, its slots, their refill and their animation never start (the
    /// crowd component is switched off before its first update) -- no Slash input, no cut target and no cut: the
    /// placed cuttables are held, the katana and the walking inputs are switched off. Both ways alike.
    /// </para>
    /// <para>
    /// **Preparation and measurement are apart.** Everything that makes the scene ready is preparation, written to a
    /// record of its own and never to the frame table: reading the scene and the geometry made beforehand, the room,
    /// the conversion's registration and first transfers (<see cref="SceneDrawVpDisplay"/>), the frames in which the
    /// scene is first drawn, and a lap of the whole path that is not recorded. Only then, with the eye back at the
    /// path's start, do the Profiler's recording (when asked for) and the frame table begin, in the same frame, for
    /// one lap of the path; they end together. Room that grows, a GPU buffer that is replaced or a geometry that is
    /// transferred between those two frames is counted, and the run then says it did not hold the comparison's
    /// conditions.
    /// </para>
    /// <para>
    /// **The eye is the path's.** Each frame the rig is placed so that the camera stands where the path says and
    /// heads where it says, whatever the head pose is; the height is the path's own.
    /// </para>
    /// The run's completion and what it measured are written apart from any judgement: nothing here is a check of
    /// the product's behaviour, and a completed run is not a functional pass.
    /// </summary>
    public static class SandboxDrawComparePlayerCheck
    {
        public const string Argument = "-zantetsuDrawCompare";
        public const string ModeArgument = "-zantetsuDrawCompareMode";
        public const string PathArgument = "-zantetsuDrawComparePath";
        public const string GeometryArgument = "-zantetsuDrawCompareGeometry";
        public const string ProfilerArgument = "-zantetsuDrawCompareProfiler";
        public const string WarmupLapsArgument = "-zantetsuDrawCompareWarmupLaps";
        public const string AllowRefusedArgument = "-zantetsuDrawCompareAllowRefused";
        public const string ShotsArgument = "-zantetsuDrawCompareShots";
        public const string MarkersArgument = "-zantetsuDrawCompareMarkers";

        /// <summary>
        /// Path times, separated by ';', at which one frame is captured by an attached external GPU profiler
        /// (RenderDoc), after the measurement has ended. A run under such a profiler is not a performance sample.
        /// </summary>
        public const string CaptureArgument = "-zantetsuDrawCompareCapture";

        /// <summary>
        /// After the measurement (mode vp3c-native only): the lifetime drill of the plugin route's event data and
        /// resources, with the plugin's consumption held so that events are submitted and unconsumed while the route
        /// is disposed, made anew and the display ends; the keeper's journal is written (native-lifetime-journal.txt).
        /// Not a performance sample.
        /// </summary>
        public const string LifetimeDrillArgument = "-zantetsuDrawCompareLifetimeDrill";
        public const string Prefix = "DRAW COMPARE: ";

        public enum Mode { Unity, Vp3c, Vp3cNative }

        /// <summary>The mode's name as the argument gives it.</summary>
        public static string ModeName(Mode mode)
        {
            return mode == Mode.Unity ? "unity" : mode == Mode.Vp3c ? "vp3c" : "vp3c-native";
        }

        public enum Phase { Waiting, Preparing, WarmingUp, Measuring, Ending, Done }

        public sealed class Settings
        {
            public string directory, pathFile, geometryFile, profilerFile, markers;
            public Mode mode;
            public int warmupLaps = 1;
            public bool allowRefused, shots, lifetimeDrill;
            public double[] captureTimes = Array.Empty<double>();
            public SceneDrawComparePath path;
        }

        /// <summary>
        /// The run's settings from a Player's arguments. False with no failure when the comparison was not asked for;
        /// false with the failure when it was and cannot be started as asked.
        /// </summary>
        public static bool TryRead(string[] arguments, out Settings settings, out string failure)
        {
            settings = null;
            failure = null;
            string directory = Value(arguments, Argument);
            if (directory == null)
            {
                return false;
            }

            var made = new Settings { directory = directory };
            string mode = Value(arguments, ModeArgument);
            if (string.Equals(mode, "unity", StringComparison.OrdinalIgnoreCase)) made.mode = Mode.Unity;
            else if (string.Equals(mode, "vp3c", StringComparison.OrdinalIgnoreCase)) made.mode = Mode.Vp3c;
            else if (string.Equals(mode, "vp3c-native", StringComparison.OrdinalIgnoreCase)) made.mode = Mode.Vp3cNative;
            else
            {
                failure = ModeArgument + " must be 'unity', 'vp3c' or 'vp3c-native' (given: " + (mode ?? "nothing") + ")";
                return false;
            }

            // The plugin's route is the world's at start-up (VpNativeDrawSetup.Argument): a run asking to compare it
            // must have been launched with that argument, and one asking for Unity's route must not have been.
            bool nativeLaunched = Zantetsu.Rendering.VpNativeDrawSetup.Read(arguments);
            if (made.mode == Mode.Vp3cNative && !nativeLaunched)
            {
                failure = "mode vp3c-native needs the Player launched with " + Zantetsu.Rendering.VpNativeDrawSetup.Argument;
                return false;
            }

            if (made.mode != Mode.Vp3cNative && nativeLaunched)
            {
                failure = "the Player was launched with " + Zantetsu.Rendering.VpNativeDrawSetup.Argument + ", which only mode vp3c-native compares";
                return false;
            }

            if (Has(arguments, SandboxPropSlashPlayerCheck.Argument))
            {
                failure = "it does not run beside the Prop Slash check (" + SandboxPropSlashPlayerCheck.Argument + ")";
                return false;
            }

            made.pathFile = Value(arguments, PathArgument);
            if (made.pathFile == null)
            {
                failure = PathArgument + " <file> is required";
                return false;
            }

            made.geometryFile = Value(arguments, GeometryArgument);
            made.profilerFile = Value(arguments, ProfilerArgument);
            made.markers = Value(arguments, MarkersArgument);
            made.allowRefused = Has(arguments, AllowRefusedArgument);
            made.shots = Has(arguments, ShotsArgument);
            made.lifetimeDrill = Has(arguments, LifetimeDrillArgument);
            if (made.lifetimeDrill && made.mode != Mode.Vp3cNative)
            {
                failure = LifetimeDrillArgument + " drills the plugin's route: mode vp3c-native only";
                return false;
            }

            string captures = Value(arguments, CaptureArgument);
            if (captures != null)
            {
                string[] parts = captures.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                made.captureTimes = new double[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                {
                    if (!double.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out made.captureTimes[i]) || !(made.captureTimes[i] >= 0))
                    {
                        failure = CaptureArgument + " takes path times separated by ';' (given: " + captures + ")";
                        return false;
                    }
                }

                if (parts.Length == 0 || parts.Length > 8)
                {
                    failure = CaptureArgument + " takes one to eight path times (given: " + captures + ")";
                    return false;
                }
            }
            string laps = Value(arguments, WarmupLapsArgument);
            if (laps != null && (!int.TryParse(laps, NumberStyles.Integer, CultureInfo.InvariantCulture, out made.warmupLaps) || made.warmupLaps < 0 || made.warmupLaps > 8))
            {
                failure = WarmupLapsArgument + " must be 0..8 (given: " + laps + ")";
                return false;
            }

            settings = made;
            return true;
        }

        private static string Value(string[] arguments, string name)
        {
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase)) return arguments[i + 1];
            }

            return null;
        }

        private static bool Has(string[] arguments, string name)
        {
            foreach (string argument in arguments)
            {
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        private static void Log(string line) => Debug.Log(Prefix + line);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            if (Application.isEditor)
            {
                return;
            }

            if (!TryRead(Environment.GetCommandLineArgs(), out Settings settings, out string failure))
            {
                if (failure != null)
                {
                    Log("NOT started: " + failure);
                    Application.Quit(20);
                }

                return;
            }

            Application.runInBackground = true;
            // After the scene's load and before any Start or Update of it: nothing of the crowd, the cuts or the walking begins.
            Log("scene: " + QuietScene());
            string text = null;
            try
            {
                text = File.ReadAllText(settings.pathFile);
            }
            catch (Exception e)
            {
                failure = "the path could not be read: " + e.Message;
            }

            if (text != null && !SceneDrawComparePath.TryParse(text, out settings.path, out failure))
            {
                failure = "the path " + settings.pathFile + ": " + failure;
            }

            if (failure != null)
            {
                Log("NOT started: " + failure);
                Application.Quit(20);
                return;
            }

            CutWorldRoot world = UnityEngine.Object.FindAnyObjectByType<CutWorldRoot>();
            PlayableCityCuttable[] cuttables = UnityEngine.Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var instances = new List<KeyValuePair<string, Renderer[]>>(cuttables.Length);
            foreach (PlayableCityCuttable c in cuttables)
            {
                instances.Add(new KeyValuePair<string, Renderer[]>(c.name, c.instanceRenderers));
            }

            instances.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            var roots = new List<Transform>(cuttables.Length);
            foreach (PlayableCityCuttable c in cuttables)
            {
                if (c.target != null) roots.Add(c.target);
            }

            Runner runner = Begin(settings, world, instances, Camera.main, null);
            runner.targetRoots = roots;
            runner.endWorld = true;
            runner.quit = Application.Quit;
        }

        /// <summary>
        /// Switches off what the comparison does not run, in the scene as loaded: every crowd (before its first
        /// update: no pool, no plan, no slot), the placed cuttables' registration, the katana and the walking inputs;
        /// and hides the sandbox's settings and diagnostics IMGUI. What was found and switched off, in words.
        /// </summary>
        public static string QuietScene()
        {
            PlayableCityCuttable.Held = true;
            int crowds = 0, katanas = 0, inputs = 0;
            foreach (MobPlanCrowd crowd in UnityEngine.Object.FindObjectsByType<MobPlanCrowd>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                crowd.enabled = false;
                crowds++;
            }

            foreach (SandboxRightHandKatana katana in UnityEngine.Object.FindObjectsByType<SandboxRightHandKatana>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                katana.enabled = false;
                katanas++;
            }

            foreach (SandboxLocomotionInput input in UnityEngine.Object.FindObjectsByType<SandboxLocomotionInput>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                input.enabled = false;
                inputs++;
            }

            foreach (MobPlanPlayerInput input in UnityEngine.Object.FindObjectsByType<MobPlanPlayerInput>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                input.enabled = false;
                inputs++;
            }

            // The sandbox's adjustment and diagnostics IMGUI is not drawn, as in the city walk's runs: only its drawing
            // goes; what the components do otherwise is as the scene has it.
            int panels = 0;
            foreach (SandboxSlashPoseRecorder recorder in UnityEngine.Object.FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                recorder.ShowControls = false;
                panels++;
            }

            foreach (SandboxSlashDiagnosticsOverlay overlay in UnityEngine.Object.FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                overlay.Visible = false;
                panels++;
            }

            return "placed cuttables held (no cut target is made); switched off before their first update: crowds " + crowds + ", katanas " + katanas + ", walking inputs " + inputs
                + "; sandbox IMGUI (recorder controls, diagnostics overlay) hidden: " + panels;
        }

        /// <summary>
        /// Starts a run on what is given: the world, the instances (each with its own renderers), the camera whose eye
        /// follows the path, and the transform moved for it (null: the camera's root, or the camera itself when it
        /// has no parent). The settings' path must be set.
        /// </summary>
        public static Runner Begin(Settings settings, CutWorldRoot world, IReadOnlyList<KeyValuePair<string, Renderer[]>> instances, Camera camera, Transform rig)
        {
            if (settings == null || settings.path == null) throw new ArgumentException("settings with a path", nameof(settings));
            var host = new GameObject("Scene draw comparison");
            Runner runner = host.AddComponent<Runner>();
            runner.settings = settings;
            runner.world = world;
            runner.instances = instances;
            runner.eye = camera;
            runner.rig = rig != null ? rig : camera != null ? camera.transform.root : null;
            return runner;
        }

        // The markers of every row: times in nanoseconds, the Render category's in counts.
        private static readonly (ProfilerCategory category, string name)[] FixedMarkers =
        {
            (ProfilerCategory.Internal, "PlayerLoop"),
            (ProfilerCategory.Internal, "Main Thread"),
            (ProfilerCategory.Internal, "WaitForTargetFPS"),
            (ProfilerCategory.Internal, "Gfx.WaitForPresentOnGfxThread"),
            (ProfilerCategory.Memory, "GC Allocated In Frame"),
            (ProfilerCategory.Render, "Draw Calls Count"),
            (ProfilerCategory.Render, "Batches Count"),
            (ProfilerCategory.Render, "SetPass Calls Count"),
            (ProfilerCategory.Render, "Triangles Count"),
            (ProfilerCategory.Render, "Vertices Count"),
            (ProfilerCategory.Scripts, "Zantetsu.Driver.Update"),
            (ProfilerCategory.Scripts, "Zantetsu.Driver.LateUpdate"),
            (ProfilerCategory.Scripts, "Zantetsu.Driver.AfterRendering"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.0Room"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.1Read"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.3Draw"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.4Instances"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.5Candidate"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.6Stencil"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.7Upload"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.8Adopt"),
            (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.9Release"),
            (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Validate"),
            (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Collect"),
            (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Group"),
            (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Place"),
            (ProfilerCategory.Scripts, "Zantetsu.GpuCull.Conditions"),
            (ProfilerCategory.Scripts, "Zantetsu.GpuCull.Issue"),
            (ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Simulate"),
            (ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Collect"),
            (ProfilerCategory.Scripts, "Zantetsu.MobPlan.Update"),
            (ProfilerCategory.Scripts, "Zantetsu.SlashHit.Evaluate"),
            (ProfilerCategory.Scripts, "Zantetsu.PoseTable.Apply"),
        };

        // What may not change between the measurement's first and last frame, and what the preparation is counted by.
        private struct Standing
        {
            public int roomGrowths, snapshotRegrowths, gpuReplacements, gpuGrowths, tableGrowths, vertexTransfers, indexTransfers, shown;
            public int cpuVertices, cpuVerticesCommitted, cpuIndicesCommitted, gpuVertexCapacity, gpuIndexCapacity, commandCapacity, instanceCapacity, commandEnd, instanceEnd;
            public long gpuRoomBytes, nativeCommitted, managedHeap, allocated;

            public static Standing Of(CutWorldRoot world)
            {
                var s = new Standing { managedHeap = GC.GetTotalMemory(false), allocated = Profiler.GetTotalAllocatedMemoryLong() };
                if (world == null || world.Display == null || world.Display.IsDisposed)
                {
                    return s;
                }

                VpLogicalCutDisplay d = world.Display;
                s.roomGrowths = d.RoomGrowths;
                s.snapshotRegrowths = d.SnapshotRegrowths;
                s.gpuReplacements = d.GpuReplacements;
                s.gpuGrowths = d.GpuGrowthCount;
                s.tableGrowths = world.References.GrowthCount;
                s.vertexTransfers = d.VertexTransfers;
                s.indexTransfers = d.IndexTransfers;
                s.shown = d.ShownCount;
                s.cpuVertices = world.Storage.VertexCount;
                s.cpuVerticesCommitted = world.Storage.CommittedVertexCapacity;
                s.cpuIndicesCommitted = world.Storage.CommittedIndexCapacity;
                s.gpuVertexCapacity = d.GpuVertexCapacity;
                s.gpuIndexCapacity = d.GpuIndexCapacity;
                s.commandCapacity = d.CommandCapacity;
                s.instanceCapacity = d.InstanceCapacity;
                s.commandEnd = d.DrawCommandEnd;
                s.instanceEnd = d.DrawInstanceEnd;
                s.gpuRoomBytes = d.RoomGpuBytes + d.GpuVertexBytes + d.GpuIndexBytes;
                s.nativeCommitted = d.RoomBytes().nativeCommitted;
                return s;
            }

            // Room made larger, a GPU object replaced, a geometry shown or transferred: none belongs between the measurement's ends.
            public string ChangesSince(in Standing before)
            {
                var text = new StringBuilder();
                void One(string what, long was, long now)
                {
                    if (was != now) text.Append(text.Length > 0 ? ", " : "").Append(what).Append(' ').Append(was).Append(" -> ").Append(now);
                }

                One("display room growths", before.roomGrowths, roomGrowths);
                One("snapshot regrowths", before.snapshotRegrowths, snapshotRegrowths);
                One("GPU objects replaced", before.gpuReplacements, gpuReplacements);
                One("GPU geometry buffer growths", before.gpuGrowths, gpuGrowths);
                One("reference table growths", before.tableGrowths, tableGrowths);
                One("vertex transfers", before.vertexTransfers, vertexTransfers);
                One("index transfers", before.indexTransfers, indexTransfers);
                One("bodies shown", before.shown, shown);
                One("CPU vertices", before.cpuVertices, cpuVertices);
                One("CPU vertices committed", before.cpuVerticesCommitted, cpuVerticesCommitted);
                One("CPU indices committed", before.cpuIndicesCommitted, cpuIndicesCommitted);
                One("GPU vertex capacity", before.gpuVertexCapacity, gpuVertexCapacity);
                One("GPU index capacity", before.gpuIndexCapacity, gpuIndexCapacity);
                One("draw command capacity", before.commandCapacity, commandCapacity);
                One("draw instance capacity", before.instanceCapacity, instanceCapacity);
                One("draw command slots taken", before.commandEnd, commandEnd);
                One("draw instance records taken", before.instanceEnd, instanceEnd);
                return text.Length > 0 ? text.ToString() : null;
            }

            public string Describe()
            {
                return "bodies shown " + shown + ", draw command slots " + commandEnd + " of " + commandCapacity + ", instance records " + instanceEnd + " of " + instanceCapacity
                    + "; CPU vertices " + cpuVertices + " (committed " + cpuVerticesCommitted + "), CPU indices committed " + cpuIndicesCommitted
                    + "; GPU vertex capacity " + gpuVertexCapacity + ", GPU index capacity " + gpuIndexCapacity
                    + "; growths: display room " + roomGrowths + ", snapshot " + snapshotRegrowths + ", GPU objects replaced " + gpuReplacements + ", GPU geometry buffers " + gpuGrowths
                    + ", reference table " + tableGrowths + "; transfers: vertex " + vertexTransfers + ", index " + indexTransfers
                    + "; bytes: GPU buffers " + gpuRoomBytes + ", display native committed " + nativeCommitted + ", managed heap " + managedHeap + ", Unity allocated " + allocated;
            }
        }

        /// <summary>The run, carried by the ordinary frames.</summary>
        public sealed class Runner : MonoBehaviour
        {
            internal Settings settings;
            internal CutWorldRoot world;
            internal IReadOnlyList<KeyValuePair<string, Renderer[]>> instances;
            internal Camera eye;
            internal Transform rig;
            internal bool endWorld;
            internal Action<int> quit;

            /// <summary>For tests: geometry the run starts with (a Player reads its file instead).</summary>
            internal SceneDrawMeshBank bank;

            /// <summary>
            /// The instances' own roots: the colliders and bodies under them are the targets' physics, which the
            /// comparison leaves as it is. Null: the roots of the instances' renderers.
            /// </summary>
            internal IReadOnlyList<Transform> targetRoots;

            /// <summary>The largest distance, in metres, and heading difference, in degrees, between the path's eye and the camera as it stood through a row's frame.</summary>
            public double LargestEyeDistance { get; private set; }
            public double LargestEyeHeadingDifference { get; private set; }

            public Phase Phase { get; private set; } = Phase.Waiting;
            public bool Done { get; private set; }
            public int Code { get; private set; } = -1;
            public bool Completed { get; private set; }
            public bool ConditionsHeld { get; private set; }
            public string ConditionsNote { get; private set; }
            public SceneDrawInventory Inventory { get; private set; }
            public SceneDrawVpDisplay VpDisplay { get; private set; }
            public int RowCount => _rows;
            public int RowsPastRoom => _rowsPastRoom;
            public int MeasureStartFrame { get; private set; } = -1;
            public int MeasureEndFrame { get; private set; } = -1;
            public int WarmupFrames { get; private set; }
            public bool ProfilerStartedHere { get; private set; }

            /// <summary>For tests: whether the Profiler was recording, and how many rows stood, in any frame before the measurement began.</summary>
            public bool ProfilerSeenBeforeMeasurement { get; private set; }
            public int RowsSeenBeforeMeasurement { get; private set; }

            private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
            private readonly StringBuilder _preparation = new StringBuilder();
            private (ProfilerCategory category, string name)[] _markers;
            private ProfilerRecorder[] _recorders;
            private UnityEngine.XR.XRDisplaySubsystem _xr;
            private readonly FrameTiming[] _timings = new FrameTiming[1];

            // The frame table's room, made while preparing: a row a frame, nothing made in a measured frame.
            private int _rows, _room, _rowsPastRoom;
            private int[] _frame, _segment, _gc, _xrDropped, _collections, _roomGrowths, _snapshotRegrowths, _renderFragments, _gpuReplacements, _commandsLive, _instancesLive;
            private double[] _t, _real, _deltaMs, _ftCpu, _ftMain, _ftWait, _ftRender, _ftGpu, _xrGpu, _xrCompositor, _x, _y, _z, _yaw, _collectMs;
            private double[] _camX, _camY, _camZ, _camYaw;
            private long[] _builds, _placementPasses, _queries, _static, _argumentElements, _instanceElements, _setDataCalls, _wholeCalls;
            private long[][] _values;
            private bool _ownsDirectory;

            private void Start()
            {
                StartCoroutine(Run());
            }

            private void Update()
            {
                if (Phase == Phase.Preparing || Phase == Phase.WarmingUp || Phase == Phase.Waiting)
                {
                    ProfilerSeenBeforeMeasurement |= ProfilerStartedHere || RecordsIntoOurFile();
                    RowsSeenBeforeMeasurement = Math.Max(RowsSeenBeforeMeasurement, _rows);
                }
            }

            // Whether the Profiler is writing the file this run was given: only from the measurement's first frame on.
            private bool RecordsIntoOurFile()
            {
                return settings != null && settings.profilerFile != null && Profiler.enabled && Profiler.enableBinaryLog
                    && !string.IsNullOrEmpty(Profiler.logFile) && Profiler.logFile.StartsWith(settings.profilerFile, StringComparison.OrdinalIgnoreCase);
            }

            private void Note(string line)
            {
                _preparation.Append(line).Append('\n');
                Log(line);
            }

            private IEnumerator Run()
            {
                // ----- before anything: the output folder is new, the world is there ---------------------------------
                string setup = null;
                try
                {
                    if (Directory.Exists(settings.directory) && Directory.GetFileSystemEntries(settings.directory).Length > 0) setup = "the output folder is not empty: " + settings.directory;
                    else
                    {
                        Directory.CreateDirectory(settings.directory);
                        _ownsDirectory = true;   // only then is anything written into it
                    }
                    if (settings.profilerFile != null && File.Exists(settings.profilerFile)) setup = "the Profiler's file exists: " + settings.profilerFile;
                }
                catch (Exception e)
                {
                    setup = "the output folder: " + e.Message;
                }

                float waitUntil = Time.realtimeSinceStartup + 60f;
                while (setup == null && (world == null || !world.IsReady) && Time.realtimeSinceStartup < waitUntil) yield return null;
                if (setup == null && (world == null || !world.IsReady)) setup = "no ready cut world";
                if (setup == null && (eye == null || rig == null)) setup = "no camera to carry along the path";
                if (setup != null)
                {
                    Log("NOT started: " + setup);
                    Finish(20, false, false, setup);
                    yield break;
                }

                // ----- preparation --------------------------------------------------------------------------------------
                Phase = Phase.Preparing;
                long prepBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                int prepBeganFrame = Time.frameCount;
                Note("mode: " + (settings.mode != Mode.Unity
                    ? ModeName(settings.mode) + " (the cut world's display draws the targets; "
                      + (world.Display.NativeArguments ? "VP3C-NATIVE, GPU selection drawn by the Direct3D 12 plugin"
                          : world.Display.CullsOnGpu ? "VP3C, GPU selection"
                          : "VP3, no GPU selection" + (world.GpuCullFallback != null ? ": FALLBACK " + world.GpuCullFallback : "")) + ")"
                    : "unity (the targets' own MeshRenderers)"));
                Note("launch: " + SandboxLaunchRecord.Describe());
                bool profilerAlready = Profiler.enabled && Profiler.enableBinaryLog;
                if (profilerAlready) Note("the Profiler was ALREADY recording when the preparation began (" + Profiler.logFile + "): the preparation is in that recording");
                Note("path: " + (settings.pathFile ?? "(given)") + ": " + settings.path.Describe() + "; warm-up laps " + settings.warmupLaps);
                if (targetRoots == null)
                {
                    var found = new List<Transform>();
                    foreach (KeyValuePair<string, Renderer[]> instance in instances)
                    {
                        foreach (Renderer listed in instance.Value ?? Array.Empty<Renderer>())
                        {
                            if (listed != null && !found.Contains(listed.transform.root)) found.Add(listed.transform.root);
                        }
                    }

                    targetRoots = found;
                }

                Note("physics as loaded: " + PhysicsState());
                Dictionary<int, string> collidersAsLoaded = SceneColliders();
                string targetsBefore = TargetPhysics();
                Note("the targets' own physics as loaded: " + targetsBefore);
                Standing asLoaded = Standing.Of(world);
                Note("world as loaded: " + asLoaded.Describe());

                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                string failure = null;
                SceneDrawMeshBank geometry = bank;
                if (geometry == null && settings.geometryFile != null)
                {
                    try
                    {
                        using (FileStream stream = File.OpenRead(settings.geometryFile))
                        {
                            if (!SceneDrawMeshBank.TryRead(stream, out geometry, out failure)) failure = "the geometry " + settings.geometryFile + ": " + failure;
                        }
                    }
                    catch (Exception e)
                    {
                        failure = "the geometry " + settings.geometryFile + ": " + e.Message;
                    }
                }

                if (geometry == null && failure == null) geometry = new SceneDrawMeshBank();
                double readMs = Ms(t0);
                if (failure != null)
                {
                    Note("NOT started: " + failure);
                    WritePreparation();
                    Finish(20, false, false, failure);
                    yield break;
                }

                Note("geometry made beforehand: " + (settings.geometryFile ?? "(none)") + ": " + geometry.Count + " meshes, " + geometry.VertexCount + " vertices, " + geometry.IndexCount + " indices, read in " + readMs.ToString("F1", Inv) + " ms");
                yield return null;

                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    Inventory = SceneDrawInventory.Collect(instances, world.MaterialBindings, geometry);
                }
                catch (Exception e)
                {
                    failure = "the scene could not be read: " + e;
                }

                if (failure != null)
                {
                    Note("NOT started: " + failure);
                    WritePreparation();
                    Finish(23, false, false, failure);
                    yield break;
                }

                Note("targets: " + Inventory.Describe() + "; read in " + Ms(t0).ToString("F1", Inv) + " ms");
                foreach (SceneDrawRefusal refusal in Inventory.Refusals) _preparation.Append("refused: ").Append(refusal).Append('\n');
                for (int i = 0; i < Math.Min(10, Inventory.Refusals.Count); i++) Log("refused: " + Inventory.Refusals[i] + (i == 9 && Inventory.Refusals.Count > 10 ? " (and " + (Inventory.Refusals.Count - 10) + " more, in the preparation record)" : ""));
                bool roomOk = SceneDrawVpDisplay.CheckRoom(world, Inventory, out string room);
                Note("room for the targets (needed of what the world's profile made): " + room);
                int refused = Inventory.Refusals.Count;
                if ((refused > 0 && !settings.allowRefused) || (!roomOk && settings.mode != Mode.Unity))
                {
                    string why = !roomOk ? "the world has no room for the targets" : refused + " renderers drawn by Unity cannot be drawn by the display (the comparison's condition is not met; " + AllowRefusedArgument + " leaves them to Unity in both ways)";
                    Note("NOT started: " + why);
                    WritePreparation();
                    Finish(22, false, false, why);
                    yield break;
                }

                // The registration: only in the display's way. Spread over frames; each frame's share is bounded.
                int showRefused = 0, registrationFrames = 0;
                double registrationMs = 0;
                if (settings.mode != Mode.Unity)
                {
                    VpDisplay = new SceneDrawVpDisplay(world);
                    int next = 0;
                    while (next < Inventory.Targets.Count)
                    {
                        long frameBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                        while (next < Inventory.Targets.Count && Ms(frameBegan) < 12.0)
                        {
                            SceneDrawTarget target = Inventory.Targets[next++];
                            string refusedWhy = null;
                            try
                            {
                                if (!VpDisplay.TryShow(target, out refusedWhy)) refusedWhy = refusedWhy ?? "refused";
                                else refusedWhy = null;
                            }
                            catch (Exception e)
                            {
                                refusedWhy = e.GetType().Name + ": " + e.Message;
                            }

                            if (refusedWhy != null)
                            {
                                showRefused++;
                                _preparation.Append("refused when shown: ").Append(target.owner).Append(" / ").Append(target.path).Append(": ").Append(refusedWhy).Append('\n');
                                if (showRefused <= 5) Log("refused when shown: " + target.owner + " / " + target.path + ": " + refusedWhy);
                            }
                        }

                        registrationMs += Ms(frameBegan);
                        registrationFrames++;
                        yield return null;
                    }

                    VpDisplay.ReleaseScaledCopies();
                    Note("registration: shown " + VpDisplay.ShownCount + " of " + Inventory.Targets.Count + " (draw commands " + VpDisplay.Commands + ", vertices " + VpDisplay.Vertices + ", indices " + VpDisplay.Indices
                        + "), refused when shown " + showRefused + "; " + registrationFrames + " frames, " + registrationMs.ToString("F1", Inv) + " ms in them (append " + (VpDisplay.AppendSeconds * 1000).ToString("F1", Inv)
                        + " ms, show and first transfer " + (VpDisplay.ShowSeconds * 1000).ToString("F1", Inv) + " ms); the targets' renderers are off");
                    if (showRefused > 0 && !settings.allowRefused)
                    {
                        string why = showRefused + " targets were refused when shown";
                        Note("NOT started: " + why);
                        VpDisplay.Dispose();
                        WritePreparation();
                        Finish(22, false, false, why);
                        yield break;
                    }
                }
                else
                {
                    Note("registration: none (the targets stay their own MeshRenderers'); nothing converted, appended or transferred");
                }

                Standing registered = Standing.Of(world);
                Note("world after the registration: " + registered.Describe());
                string registrationChanges = registered.ChangesSince(asLoaded);
                Note("changed by the registration: " + (registrationChanges ?? "nothing"));

                // The scene's first frames at the start, then the path once (or as many laps as asked), unrecorded.
                Phase = Phase.WarmingUp;
                ApplyPose(settings.path.At(0));
                for (int i = 0; i < 30; i++) yield return null;
                long warmBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int lap = 0; lap < settings.warmupLaps; lap++)
                {
                    double lapStart = Time.unscaledTimeAsDouble;
                    while (true)
                    {
                        double t = Time.unscaledTimeAsDouble - lapStart;
                        if (t >= settings.path.Seconds) break;
                        ApplyPose(settings.path.At(t));
                        WarmupFrames++;
                        yield return null;
                    }
                }

                double warmMs = Ms(warmBegan);
                ApplyPose(settings.path.At(0));
                for (int i = 0; i < 30; i++) yield return null;

                // The frame table's room and the recorders, then a collection, all before the first measured frame.
                t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                long heapBefore = GC.GetTotalMemory(false);
                MakeRoom();
                double roomMs = Ms(t0);
                long roomBytes = GC.GetTotalMemory(false) - heapBefore;
                GC.Collect();
                for (int i = 0; i < 10; i++) yield return null;
                ApplyPose(settings.path.At(0));
                Standing atStart = Standing.Of(world);
                string warmChanges = atStart.ChangesSince(registered);
                Note("warm-up: " + settings.warmupLaps + " laps of the path, " + WarmupFrames + " frames, " + (warmMs / 1000).ToString("F2", Inv) + " s, not recorded; changed by it: " + (warmChanges ?? "nothing"));
                Note("frame table room: " + _room + " rows with " + _markers.Length + " markers each, made in " + roomMs.ToString("F1", Inv) + " ms, managed heap +" + roomBytes + " B; then one full collection");
                Note("world at the measurement's start: " + atStart.Describe());
                string physicsAtStart = PhysicsState();
                Dictionary<int, string> collidersAtStart = SceneColliders();
                string changedWhilePreparing = ColliderChanges(collidersAsLoaded, collidersAtStart);
                Note("physics at the measurement's start: " + physicsAtStart);
                Note("scene colliders changed while preparing, by name (the comparison's own condition is the targets' physics, below): " + (changedWhilePreparing ?? "none"));
                Note("the targets' own physics at the measurement's start: " + TargetPhysics());
                Note("activity at the measurement's start: " + Activity(out _));
                Note("frame timing: FrameTimingManager enabled=" + FrameTimingManager.IsFeatureEnabled() + " (its values reach a row a few frames late)"
                    + "; xr active=" + UnityEngine.XR.XRSettings.isDeviceActive + " device=" + UnityEngine.XR.XRSettings.loadedDeviceName + " stereo=" + UnityEngine.XR.XRSettings.stereoRenderingMode
                    + " eyeTexture=" + UnityEngine.XR.XRSettings.eyeTextureWidth + "x" + UnityEngine.XR.XRSettings.eyeTextureHeight + " renderScale=" + UnityEngine.XR.XRSettings.eyeTextureResolutionScale.ToString("R", Inv)
                    + " refreshRate=" + (_xr != null && _xr.TryGetDisplayRefreshRate(out float hz) ? hz.ToString("R", Inv) : "n/a")
                    + " xrAppGpuTime=" + (_xr != null && _xr.TryGetAppGPUTimeLastFrame(out float _) ? "answers" : "no answer")
                    + "; graphics=" + SystemInfo.graphicsDeviceType + " gpu=" + SystemInfo.graphicsDeviceName + " cpu=" + SystemInfo.processorType
                    + " screen=" + Screen.width + "x" + Screen.height + " quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]
                    + " shadows=" + QualitySettings.shadows + " pipeline=" + (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline != null ? UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline.name : "built-in"));
                double prepSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - prepBegan) / (double)System.Diagnostics.Stopwatch.Frequency;
                Note("preparation: " + prepSeconds.ToString("F2", Inv) + " s, frames " + prepBeganFrame + ".." + Time.frameCount + "; none of it is in the frame table");
                WritePreparation();
                yield return null;

                // ----- measurement: the Profiler's recording and the frame table begin in this frame ---------------------
                ApplyPose(settings.path.At(0));
                if (settings.profilerFile != null)
                {
                    Profiler.logFile = settings.profilerFile;
                    Profiler.enableBinaryLog = true;
                    Profiler.maxUsedMemory = 1 << 30;
                    Profiler.enabled = true;
                    ProfilerStartedHere = true;
                }

                Phase = Phase.Measuring;
                MeasureStartFrame = Time.frameCount;
                double start = Time.unscaledTimeAsDouble;
                Log("measurement begins at frame " + MeasureStartFrame + (ProfilerStartedHere ? "; the Profiler records from here into " + settings.profilerFile : "; no Profiler recording was asked for"));
                // A shot run also takes pictures WHILE MOVING, at the middle of each moving or turning segment (the
                // screen and both eyes; no other pass of the frame): the frames the standing pictures after the
                // measurement cannot stand for (2026-10-08: the TL saw the plugin route without its own shadows while
                // walking, while the standing pictures matched Unity's). A shot run is not a performance sample.
                int movingShotSegment = settings.shots ? NextMovingSegment(-1) : -1;
                int movingShotsTaken = 0;
                while (true)
                {
                    double t = Time.unscaledTimeAsDouble - start;
                    bool last = t >= settings.path.Seconds;
                    SceneDrawComparePath.Pose pose = settings.path.At(t);
                    // The camera as it stood through the frame before, read before the rig is placed for this one.
                    Vector3 seen = eye.transform.position;
                    float seenYaw = eye.transform.eulerAngles.y;
                    ApplyPose(pose);
                    Record(t, pose, !last, seen, seenYaw);
                    if (movingShotSegment >= 0 && t >= settings.path.BeginOf(movingShotSegment) + 0.5 * settings.path.SecondsOf(movingShotSegment))
                    {
                        string stem = "moving-" + movingShotsTaken.ToString("D2", Inv) + "-segment-" + movingShotSegment.ToString("D2", Inv);
                        ScreenCapture.CaptureScreenshot(Path.Combine(settings.directory, stem + ".png"));
                        if (!Zantetsu.Rendering.Urp.VpEyeShot.TryRequest(eye, Path.Combine(settings.directory, stem), out string movingFailure))
                        {
                            Log("moving eye shot not taken: " + movingFailure);
                        }

                        Log("moving pictures at path time " + t.ToString("F2", Inv) + " s, segment " + movingShotSegment + " (" + stem + ")");
                        movingShotsTaken++;
                        movingShotSegment = NextMovingSegment(movingShotSegment);
                    }

                    if (last) break;
                    yield return null;
                }

                MeasureEndFrame = Time.frameCount - 1;   // the last frame with a row of its own
                if (ProfilerStartedHere)
                {
                    Profiler.enabled = false;
                    Profiler.enableBinaryLog = false;
                    Profiler.logFile = "";
                }

                Phase = Phase.Ending;
                Standing atEnd = Standing.Of(world);
                string measuredChanges = atEnd.ChangesSince(atStart);
                Log("measurement ended: frames " + MeasureStartFrame + ".." + MeasureEndFrame + ", " + _rows + " rows");
                yield return null;

                // ----- after the measurement: nothing below is in a row -------------------------------------------------
                if (settings.shots)
                {
                    yield return Shots();
                }

                if (settings.captureTimes.Length > 0)
                {
                    yield return Captures();
                }

                string activity = Activity(out bool quiet);
                string physicsAfter = PhysicsState();
                string targetsAfter = TargetPhysics();
                string changedWhileMeasuring = ColliderChanges(collidersAtStart, SceneColliders());

                // The drill ends the world itself (its third step is the display's end): after everything read above.
                var drillNotes = new List<string>();
                if (settings.lifetimeDrill)
                {
                    yield return LifetimeDrill(drillNotes);
                }

                var notes = new List<string>(drillNotes);
                if (measuredChanges != null) notes.Add("changed between the measurement's first and last frame: " + measuredChanges);
                if (_rowsPastRoom > 0) notes.Add(_rowsPastRoom + " frames past the frame table's room were not recorded");
                if (refused > 0 || showRefused > 0) notes.Add((refused + showRefused) + " renderers were left to Unity in both ways (" + AllowRefusedArgument + ")");
                if (!quiet) notes.Add("a crowd, a cut, a cut target or the sandbox's IMGUI was active");
                if (profilerAlready) notes.Add("the Profiler was already recording during the preparation");
                if (targetsAfter != targetsBefore) notes.Add("the targets' own colliders or bodies changed: " + targetsBefore + " -> " + targetsAfter);
                if (changedWhileMeasuring != null || physicsAfter != physicsAtStart) notes.Add("the scene's bodies and colliders changed during the measurement: " + physicsAtStart + " -> " + physicsAfter + (changedWhileMeasuring != null ? " (" + changedWhileMeasuring + ")" : ""));
                if (settings.mode == Mode.Vp3c && !world.Display.CullsOnGpu) notes.Add("the display drew as VP3, not VP3C");
                if (settings.mode == Mode.Vp3c && world.Display.NativeArguments) notes.Add("the display drew through the plugin, not by Unity's calls");
                if (settings.mode == Mode.Vp3cNative && !world.Display.NativeArguments) notes.Add("the display did NOT draw through the plugin");
                if (settings.mode == Mode.Vp3cNative)
                {
                    foreach (CutWorldCameraDrawing drawing in UnityEngine.Object.FindObjectsByType<CutWorldCameraDrawing>(FindObjectsSortMode.None))
                    {
                        string route = drawing.DescribeNativeRoute(out bool drewEverything);
                        if (route == null) continue;
                        if (!drewEverything)
                        {
                            notes.Add("the plugin's route did not draw every issue: " + route);
                        }
                        else
                        {
                            Note("native route: " + route);
                        }
                    }
                }
                string placement = VpDisplay != null ? "; the display asked where a target stands " + VpDisplay.PlacementAnswers + " times in all" : "";
                bool wrote = true;
                try
                {
                    WriteFrames();
                    WriteResult(true, notes, atStart, atEnd, activity + placement,
                        physicsAfter + "; the targets' own: " + targetsAfter + " (as loaded: " + (targetsAfter == targetsBefore ? "the same" : targetsBefore) + "); scene colliders changed while preparing: " + (changedWhilePreparing ?? "none"));
                }
                catch (Exception e)
                {
                    wrote = false;
                    Debug.LogException(e);
                    notes.Add("the records could not be written: " + e.Message);
                }

                DisposeRecorders();
                if (endWorld && world != null)
                {
                    // The world ends as it stands: what the display holds is given back by its own ending.
                    var ending = new CheckEnding(Log);
                    yield return ending.Run(world, new List<CheckEnding.Part>(), 15f);
                    Log("world released " + ending.WorldReleased + ", ending failures " + ending.Failures);
                }

                if (VpDisplay != null)
                {
                    VpDisplay.Dispose();
                }

                Finish(!wrote ? 23 : notes.Count == 0 ? 0 : 21, wrote, notes.Count == 0, notes.Count == 0 ? null : string.Join("; ", notes));
            }

            private static double Ms(long since) => (System.Diagnostics.Stopwatch.GetTimestamp() - since) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            private void Finish(int code, bool completed, bool conditionsHeld, string note)
            {
                Phase = Phase.Done;
                Completed = completed;
                ConditionsHeld = conditionsHeld;
                ConditionsNote = note;
                Code = code;
                DisposeRecorders();
                if (!completed)
                {
                    try
                    {
                        if (_ownsDirectory) WriteResult(false, new List<string> { note }, default, default, null, null);
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                Log("finished with code " + code + ": run " + (completed ? "completed" : "NOT completed") + "; the comparison's conditions " + (conditionsHeld ? "held" : "NOT held" + (note != null ? " (" + note + ")" : "")));
                Done = true;
                quit?.Invoke(code);
            }

            // The rig placed so that the camera's eye is the pose's, whatever the head's own pose under the rig is.
            private void ApplyPose(in SceneDrawComparePath.Pose pose)
            {
                Transform camera = eye.transform;
                if (rig == camera)
                {
                    camera.SetPositionAndRotation(pose.eye, Quaternion.Euler(camera.eulerAngles.x, pose.yaw, 0f));
                    return;
                }

                Vector3 local = rig.InverseTransformPoint(camera.position);
                float localYaw = (Quaternion.Inverse(rig.rotation) * camera.rotation).eulerAngles.y;
                Quaternion rotation = Quaternion.Euler(0f, pose.yaw - localYaw, 0f);
                rig.SetPositionAndRotation(pose.eye - rotation * Vector3.Scale(local, rig.lossyScale), rotation);
            }

            private void MakeRoom()
            {
                var markers = new List<(ProfilerCategory, string)>(FixedMarkers);
                foreach (string entry in (settings.markers ?? string.Empty).Split(';'))
                {
                    string text = entry.Trim();
                    if (text.Length == 0) continue;
                    ProfilerCategory category = ProfilerCategory.Internal;
                    int colon = text.IndexOf(':');
                    if (colon > 0)
                    {
                        System.Reflection.PropertyInfo named = typeof(ProfilerCategory).GetProperty(text.Substring(0, colon), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (named != null && named.PropertyType == typeof(ProfilerCategory))
                        {
                            category = (ProfilerCategory)named.GetValue(null);
                            text = text.Substring(colon + 1);
                        }
                    }

                    markers.Add((category, text));
                }

                _markers = markers.ToArray();
                _recorders = new ProfilerRecorder[_markers.Length];
                for (int i = 0; i < _markers.Length; i++) _recorders[i] = ProfilerRecorder.StartNew(_markers[i].category, _markers[i].name);
                var displays = new List<UnityEngine.XR.XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(displays);
                _xr = displays.Find(d => d.running);

                // A row a frame at twice the display's rate (144 a second where there is none) and a fifth more.
                float rate = _xr != null && _xr.TryGetDisplayRefreshRate(out float hz) && hz > 0f ? hz * 2f : 144f;
                _room = (int)Math.Ceiling(settings.path.Seconds * rate * 1.2) + 512;
                _frame = new int[_room]; _segment = new int[_room]; _gc = new int[_room]; _xrDropped = new int[_room];
                _collections = new int[_room]; _roomGrowths = new int[_room]; _snapshotRegrowths = new int[_room]; _renderFragments = new int[_room];
                _gpuReplacements = new int[_room]; _commandsLive = new int[_room]; _instancesLive = new int[_room];
                _t = new double[_room]; _real = new double[_room]; _deltaMs = new double[_room]; _ftCpu = new double[_room]; _ftMain = new double[_room];
                _ftWait = new double[_room]; _ftRender = new double[_room]; _ftGpu = new double[_room]; _xrGpu = new double[_room]; _xrCompositor = new double[_room];
                _x = new double[_room]; _y = new double[_room]; _z = new double[_room]; _yaw = new double[_room]; _collectMs = new double[_room];
                _camX = new double[_room]; _camY = new double[_room]; _camZ = new double[_room]; _camYaw = new double[_room];
                _builds = new long[_room]; _placementPasses = new long[_room]; _queries = new long[_room]; _static = new long[_room];
                _argumentElements = new long[_room]; _instanceElements = new long[_room]; _setDataCalls = new long[_room]; _wholeCalls = new long[_room];
                _values = new long[_markers.Length][];
                for (int i = 0; i < _markers.Length; i++) _values[i] = new long[_room];
            }

            private void DisposeRecorders()
            {
                if (_recorders == null) return;
                foreach (ProfilerRecorder recorder in _recorders) recorder.Dispose();
                _recorders = null;
            }

            // The frame before's markers and display counts into its row, then (unless this frame is past the path's
            // end) a row for this frame. Nothing is made here: the rows are the room's.
            private void Record(double t, in SceneDrawComparePath.Pose pose, bool take, Vector3 seen, float seenYaw)
            {
                int frame = Time.frameCount, before = _rows - 1;
                if (before >= 0 && _frame[before] == frame - 1)
                {
                    _camX[before] = seen.x; _camY[before] = seen.y; _camZ[before] = seen.z; _camYaw[before] = seenYaw;
                    double apart = Math.Sqrt((seen.x - _x[before]) * (seen.x - _x[before]) + (seen.y - _y[before]) * (seen.y - _y[before]) + (seen.z - _z[before]) * (seen.z - _z[before]));
                    LargestEyeDistance = Math.Max(LargestEyeDistance, apart);
                    LargestEyeHeadingDifference = Math.Max(LargestEyeHeadingDifference, Math.Abs(Mathf.DeltaAngle(seenYaw, (float)_yaw[before])));
                    for (int i = 0; i < _recorders.Length; i++) _values[i][before] = _recorders[i].Valid ? _recorders[i].LastValue : -1;
                    if (world.Display != null && !world.Display.IsDisposed && world.Display.TryGetFrameCounts(frame - 1, out VpLogicalCutDisplay.FrameCounts counts))
                    {
                        _collections[before] = counts.collections;
                        _roomGrowths[before] = counts.roomGrowths;
                        _snapshotRegrowths[before] = counts.snapshotRegrowths;
                        _builds[before] = counts.structureBuilds;
                        _placementPasses[before] = counts.placementPasses;
                        _queries[before] = (counts.placeStructural != null ? counts.placeStructural.queries : 0) + (counts.placePlacementOnly != null ? counts.placePlacementOnly.queries : 0);
                        _static[before] = (counts.placeStructural != null ? counts.placeStructural.staticPlacements : 0) + (counts.placePlacementOnly != null ? counts.placePlacementOnly.staticPlacements : 0);
                        _commandsLive[before] = counts.draw.commandsLive;
                        _instancesLive[before] = counts.draw.instancesLive;
                        _argumentElements[before] = counts.draw.argumentElements;
                        _instanceElements[before] = counts.draw.instanceElements;
                        _setDataCalls[before] = counts.draw.argumentCalls + counts.draw.instanceCalls + counts.draw.transformCalls + counts.draw.clipCalls;
                        _wholeCalls[before] = counts.draw.wholeCalls;
                        _collectMs[before] = counts.times.collectSeconds * 1000.0;
                    }
                }

                if (!take)
                {
                    return;
                }

                if (_rows >= _room)
                {
                    _rowsPastRoom++;
                    return;
                }

                int r = _rows++;
                _frame[r] = frame;
                _segment[r] = pose.segment;
                _t[r] = t;
                _real[r] = Time.realtimeSinceStartupAsDouble;
                _deltaMs[r] = Time.unscaledDeltaTime * 1000.0;
                _gc[r] = GC.CollectionCount(0);
                _x[r] = pose.eye.x; _y[r] = pose.eye.y; _z[r] = pose.eye.z; _yaw[r] = pose.yaw;
                _ftCpu[r] = _ftMain[r] = _ftWait[r] = _ftRender[r] = _ftGpu[r] = _xrGpu[r] = _xrCompositor[r] = double.NaN;
                _camX[r] = _camY[r] = _camZ[r] = _camYaw[r] = double.NaN;
                _xrDropped[r] = -1;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
                {
                    _ftCpu[r] = _timings[0].cpuFrameTime;
                    _ftMain[r] = _timings[0].cpuMainThreadFrameTime;
                    _ftWait[r] = _timings[0].cpuMainThreadPresentWaitTime;
                    _ftRender[r] = _timings[0].cpuRenderThreadFrameTime;
                    _ftGpu[r] = _timings[0].gpuFrameTime;
                }

                if (_xr != null)
                {
                    if (_xr.TryGetAppGPUTimeLastFrame(out float gpu)) _xrGpu[r] = gpu * 1000.0;
                    if (_xr.TryGetCompositorGPUTimeLastFrame(out float compositor)) _xrCompositor[r] = compositor * 1000.0;
                    if (_xr.TryGetDroppedFrameCount(out int dropped)) _xrDropped[r] = dropped;
                }

                if (world.Display != null && !world.Display.IsDisposed)
                {
                    _renderFragments[r] = world.Display.RenderFragmentCount;
                    _gpuReplacements[r] = world.Display.GpuReplacements;
                }
            }

            /// <summary>What the capture phase did, in words; null when no capture was asked for.</summary>
            public string CaptureRecord { get; private set; }

            public int CapturesTaken { get; private set; }

            // After the measurement: one frame at each path time asked for, handed to the external GPU profiler that
            // launched this Player (RenderDoc). The eye stands still at the path's pose for the frames before and
            // through the capture. With no profiler attached nothing is captured, and that is said.
            private IEnumerator Captures()
            {
                bool attached = UnityEngine.Experimental.Rendering.ExternalGPUProfiler.IsAttached();
                var text = new StringBuilder();
                text.Append("asked ").Append(settings.captureTimes.Length).Append("; external GPU profiler attached ").Append(attached);
                int calls = Array.FindIndex(_markers, m => m.name == "Draw Calls Count"), batches = Array.FindIndex(_markers, m => m.name == "Batches Count"),
                    setPass = Array.FindIndex(_markers, m => m.name == "SetPass Calls Count");
                for (int i = 0; i < settings.captureTimes.Length; i++)
                {
                    SceneDrawComparePath.Pose pose = settings.path.At(settings.captureTimes[i]);
                    for (int f = 0; f < 30; f++)
                    {
                        ApplyPose(pose);
                        yield return null;
                    }

                    ApplyPose(pose);
                    int frame = Time.frameCount;
                    if (attached)
                    {
                        UnityEngine.Experimental.Rendering.ExternalGPUProfiler.BeginGPUCapture();
                    }

                    yield return null;
                    if (attached)
                    {
                        UnityEngine.Experimental.Rendering.ExternalGPUProfiler.EndGPUCapture();
                        CapturesTaken++;
                    }

                    // The counters of the frame just captured, as Unity counted them, to set beside the capture's own calls.
                    long unityCalls = calls >= 0 && _recorders[calls].Valid ? _recorders[calls].LastValue : -1;
                    long unityBatches = batches >= 0 && _recorders[batches].Valid ? _recorders[batches].LastValue : -1;
                    long unitySetPass = setPass >= 0 && _recorders[setPass].Valid ? _recorders[setPass].LastValue : -1;
                    VpLogicalCutDisplay d = world.Display;
                    string line = "capture " + i + ": path time " + settings.captureTimes[i].ToString("R", Inv) + " s, eye (" + pose.eye.x.ToString("F2", Inv) + ", " + pose.eye.y.ToString("F2", Inv) + ", "
                        + pose.eye.z.ToString("F2", Inv) + ") heading " + pose.yaw.ToString("F1", Inv) + ", frame " + frame + (attached ? ", captured" : ", NOT captured (no external GPU profiler attached)")
                        + "; Unity's counters of that frame: draw calls " + unityCalls + ", batches " + unityBatches + ", SetPass " + unitySetPass
                        + "; display: bodies " + d.ShownCount + ", commands that draw " + d.CommandCount + ", command slots " + d.DrawCommandEnd + ", instances " + d.DrawInstanceCount
                        + ", one-sided caster issues so far " + d.OneSidedShadowIssues + ", two-sided " + d.TwoSidedShadowIssues;
                    Log(line);
                    text.Append("; ").Append(line);
                    for (int f = 0; f < 15; f++) yield return null;
                }

                text.Append("; taken ").Append(CapturesTaken).Append(" (a run under an external GPU profiler is not a performance sample)");
                CaptureRecord = text.ToString();
            }

            /// <summary>The next moving or turning segment after <paramref name="after"/>, or -1.</summary>
            private int NextMovingSegment(int after)
            {
                for (int s = after + 1; s < settings.path.SegmentCount; s++)
                {
                    SceneDrawComparePath.Kind kind = settings.path.KindOf(s);
                    if (kind == SceneDrawComparePath.Kind.Goto || kind == SceneDrawComparePath.Kind.Turn) return s;
                }

                return -1;
            }

            /// <summary>
            /// The lifetime drill (TL, 2026-10-08): on a Player whose rendering is threaded, the plugin's events of a
            /// frame are unconsumed while the main thread goes on; the drill widens that window by holding the plugin's
            /// consumed marks and, within it, (1) disposes the route with submitted, unconsumed events (the drawing
            /// components disabled), (2) makes a new route (enabled again) and draws through it, (3) ends the display
            /// -- its buffers were named by both routes' events -- and asserts, by the keeper's counts, that nothing
            /// is released while the marks are held and everything is once they are given, in order. The journal is
            /// written as the order evidence (native-lifetime-journal.txt). Failures are notes of the run.
            /// </summary>
            private IEnumerator LifetimeDrill(List<string> notes)
            {
                Log("lifetime drill begins at frame " + Time.frameCount);
                Zantetsu.Rendering.VpNativeDrawRelease.JournalOn = true;
                Zantetsu.Rendering.VpNativeDrawRelease.Note("lifetime drill begins");
                var drawings = new List<CutWorldCameraDrawing>(UnityEngine.Object.FindObjectsByType<CutWorldCameraDrawing>(FindObjectsSortMode.None));
                drawings.RemoveAll(d => d == null || !d.enabled || d.NativeRoute == null);
                if (drawings.Count == 0)
                {
                    notes.Add("lifetime drill: no enabled camera drawing with a plugin route");
                    yield break;
                }

                int pendingBefore = Zantetsu.Rendering.VpNativeDrawRelease.Pending;
                long releasedBefore = Zantetsu.Rendering.VpNativeDrawRelease.Released;
                var oldRoutes = new List<Zantetsu.Rendering.Urp.VpNativeDrawRoute>();
                foreach (CutWorldCameraDrawing drawing in drawings) oldRoutes.Add(drawing.NativeRoute);

                // 0. Hold: from here every event runs but keeps its block unconsumed.
                Zantetsu.Rendering.VpNativeDrawPlugin.HoldConsumptionForTest(true);
                for (int i = 0; i < 3; i++) yield return null;
                int inFlight = 0;
                foreach (Zantetsu.Rendering.Urp.VpNativeDrawRoute route in oldRoutes) inFlight += route.BlocksInFlight;
                int heldAtDispose = Zantetsu.Rendering.VpNativeDrawPlugin.HeldCountForTest();
                Log("drill 0: consumption held for 3 frames; blocks in flight " + inFlight + ", events held by the plugin " + heldAtDispose);
                if (inFlight == 0 || heldAtDispose == 0) notes.Add("lifetime drill: no unconsumed event was made (in flight " + inFlight + ", held " + heldAtDispose + "): the drill did not pass through the state");

                // 1. The old routes disposed with unconsumed events: entries pending, nothing released.
                foreach (CutWorldCameraDrawing drawing in drawings) drawing.enabled = false;
                Zantetsu.Rendering.VpNativeDrawRelease.Note("drill 1: drawing components disabled (routes disposed with unconsumed events)");
                yield return null;
                int pendingAfterDispose = Zantetsu.Rendering.VpNativeDrawRelease.Pending;
                Log("drill 1: routes disposed; " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                if (pendingAfterDispose <= pendingBefore) notes.Add("lifetime drill: the disposed route's entry is not pending (" + pendingAfterDispose + " <= " + pendingBefore + ")");
                if (Zantetsu.Rendering.VpNativeDrawRelease.Released != releasedBefore) notes.Add("lifetime drill: something was released while the marks were held (after the dispose)");

                // 2. New routes (enabled again) draw the same display; their events are held too.
                foreach (CutWorldCameraDrawing drawing in drawings) drawing.enabled = true;
                Zantetsu.Rendering.VpNativeDrawRelease.Note("drill 2: drawing components enabled (new routes)");
                for (int i = 0; i < 3; i++) yield return null;
                int newInFlight = 0, newRoutes = 0;
                foreach (CutWorldCameraDrawing drawing in drawings)
                {
                    if (drawing.NativeRoute != null && !oldRoutes.Contains(drawing.NativeRoute)) { newRoutes++; newInFlight += drawing.NativeRoute.BlocksInFlight; }
                }

                Log("drill 2: new routes " + newRoutes + ", their blocks in flight " + newInFlight + "; display sinks remembered " + world.Display.SinksUsed.Count + "; " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                if (newRoutes == 0) notes.Add("lifetime drill: no new route was made after enabling");
                if (world.Display.SinksUsed.Count < 2) notes.Add("lifetime drill: the display remembers " + world.Display.SinksUsed.Count + " sinks, not both routes");
                if (Zantetsu.Rendering.VpNativeDrawRelease.Released != releasedBefore) notes.Add("lifetime drill: something was released while the marks were held (after the new route)");

                // 3. The display ends while both routes' events are unconsumed: its buffers wait on both; nothing is freed.
                GraphicsBuffer vertices = world.Display.GpuVertexBufferForDiagnosis;
                long acrossBefore = world.Display.BuffersRetiredAcrossRoutes;
                Zantetsu.Rendering.VpNativeDrawRelease.Note("drill 3: the display ends (buffers named by both routes' events)");
                var ending = new CheckEnding(Log);
                yield return ending.Run(world, new List<CheckEnding.Part>(), 15f);
                Log("drill 3: world released " + ending.WorldReleased + ", ending failures " + ending.Failures + "; " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                int pendingAtEnd = Zantetsu.Rendering.VpNativeDrawRelease.Pending;
                if (!ending.WorldReleased) notes.Add("lifetime drill: the world did not end");
                if (vertices == null || !vertices.IsValid()) notes.Add("lifetime drill: the display's vertex buffer was disposed while the marks were held");
                if (pendingAtEnd <= pendingAfterDispose) notes.Add("lifetime drill: the display's buffers are not pending (" + pendingAtEnd + " <= " + pendingAfterDispose + ")");
                if (Zantetsu.Rendering.VpNativeDrawRelease.Released != releasedBefore) notes.Add("lifetime drill: something was released while the marks were held (after the display's end)");
                Log("drill 3: pending entries:\n" + Zantetsu.Rendering.VpNativeDrawRelease.DescribePending());

                // 4. The marks given in the order the events ran: the old routes' first (the display's buffers still wait
                // on the new routes'), then all. Each Tick is the keeper's own, at the end of the frame's rendering.
                int given = Zantetsu.Rendering.VpNativeDrawPlugin.ReleaseHeldForTest(heldAtDispose);
                Zantetsu.Rendering.VpNativeDrawRelease.Note("drill 4a: " + given + " held marks given (the events run before the dispose)");
                yield return null;
                Log("drill 4a: " + given + " marks given; " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                if (vertices != null && !vertices.IsValid()) notes.Add("lifetime drill: the display's vertex buffer was disposed while the new routes' events were unconsumed");
                int givenAll = Zantetsu.Rendering.VpNativeDrawPlugin.ReleaseHeldForTest(-1);
                Zantetsu.Rendering.VpNativeDrawPlugin.HoldConsumptionForTest(false);
                Zantetsu.Rendering.VpNativeDrawRelease.Note("drill 4b: " + givenAll + " held marks given (all); hold off");
                for (int i = 0; i < 4 && Zantetsu.Rendering.VpNativeDrawRelease.Pending > pendingBefore; i++) yield return null;
                Log("drill 4b: " + givenAll + " marks given; " + Zantetsu.Rendering.VpNativeDrawRelease.Describe());
                if (Zantetsu.Rendering.VpNativeDrawRelease.Pending > pendingBefore) notes.Add("lifetime drill: " + (Zantetsu.Rendering.VpNativeDrawRelease.Pending - pendingBefore) + " entries still pending after every mark was given:\n" + Zantetsu.Rendering.VpNativeDrawRelease.DescribePending());
                if (vertices != null && vertices.IsValid()) notes.Add("lifetime drill: the display's vertex buffer is still valid after every mark was given");
                foreach (CutWorldCameraDrawing drawing in drawings)
                {
                    if (drawing != null && drawing.NativeRoute != null && drawing.NativeRoute.Refused > 0) notes.Add("lifetime drill: the new route had refusals: " + drawing.NativeRoute.LastFailure);
                }

                try
                {
                    File.WriteAllText(Path.Combine(settings.directory, "native-lifetime-journal.txt"), Zantetsu.Rendering.VpNativeDrawRelease.JournalText());
                    Log("lifetime journal written (native-lifetime-journal.txt, " + Zantetsu.Rendering.VpNativeDrawRelease.Journal.Count + " lines)");
                }
                catch (Exception e)
                {
                    notes.Add("lifetime drill: the journal could not be written: " + e.Message);
                }

                Log("lifetime drill ended at frame " + Time.frameCount + "; the world was ended by it");
                endWorld = false;   // ended above
            }

            private IEnumerator Shots()
            {
                // After the measurement and the Profiler's recording: the screen as drawn, at the middle of each standing
                // segment -- twice: a first frame with nothing but the screen and both eyes taken (as the frames of the
                // path are), and, four frames later, a second frame that also takes the shadow map (shot-b / eyes-b /
                // shadow-map). The two frames of a standing pose tell whether the shadow map's picture pass changes
                // the frame it is in (2026-10-08: the standing pictures with it matched Unity's; the TL's view of the
                // path frames without it did not).
                int taken = 0;
                for (int s = 0; s < settings.path.SegmentCount; s++)
                {
                    if (settings.path.KindOf(s) != SceneDrawComparePath.Kind.Hold) continue;
                    ApplyPose(settings.path.At(settings.path.BeginOf(s) + 0.5 * settings.path.SecondsOf(s)));
                    for (int i = 0; i < 4; i++) yield return null;
                    string suffix = taken.ToString("D2", Inv) + "-segment-" + s.ToString("D2", Inv);
                    ScreenCapture.CaptureScreenshot(Path.Combine(settings.directory, "shot-" + suffix + ".png"));

                    // Each eye's own texture, as it leaves URP (the window shows one eye): eyes-NN-segment-SS-eye0/1.png.
                    if (!Zantetsu.Rendering.Urp.VpEyeShot.TryRequest(eye, Path.Combine(settings.directory, "eyes-" + suffix), out string eyeFailure))
                    {
                        Log("eye shot not taken: " + eyeFailure);
                    }

                    for (int i = 0; i < 4; i++) yield return null;
                    ScreenCapture.CaptureScreenshot(Path.Combine(settings.directory, "shot-b-" + suffix + ".png"));
                    if (!Zantetsu.Rendering.Urp.VpEyeShot.TryRequest(eye, Path.Combine(settings.directory, "eyes-b-" + suffix), out string eyeFailureB))
                    {
                        Log("eye shot (b) not taken: " + eyeFailureB);
                    }

                    // And the main light's shadow map of that second frame (every cascade's slice): whether the route's
                    // casters are in it, by route -- shadow-map-NN-segment-SS.png.
                    if (!Zantetsu.Rendering.Urp.VpEyeShot.TryRequestShadowMap(eye, Path.Combine(settings.directory, "shadow-map-" + suffix + ".png"), out string mapFailure))
                    {
                        Log("shadow map shot not taken: " + mapFailure);
                    }

                    taken++;
                    for (int i = 0; i < 3; i++) yield return null;
                }

                // Diagnosis of the plugin route's inputs at the last standing pose: the GPU-assembled constant blocks
                // of the last body issue (camera matrices and positions of both eyes, the shadow cascades).
                if (settings.mode == Mode.Vp3cNative)
                {
                    foreach (CutWorldCameraDrawing drawing in UnityEngine.Object.FindObjectsByType<CutWorldCameraDrawing>(FindObjectsSortMode.None))
                    {
                        string blocks = drawing.DescribeNativeGpuBlocksForDiagnosis(eye);
                        if (blocks != null)
                        {
                            File.WriteAllText(Path.Combine(settings.directory, "native-gpu-blocks.txt"), blocks);
                            Log("native GPU blocks written (native-gpu-blocks.txt)");
                        }
                    }
                }

                for (int i = 0; i < 30 && Zantetsu.Rendering.Urp.VpEyeShot.Pending > 0; i++) yield return null;
                Log("eye shots written " + Zantetsu.Rendering.Urp.VpEyeShot.Written + ", pending " + Zantetsu.Rendering.Urp.VpEyeShot.Pending
                    + (Zantetsu.Rendering.Urp.VpEyeShot.LastFailure != null ? ", last failure: " + Zantetsu.Rendering.Urp.VpEyeShot.LastFailure : ""));

                Log("pictures after the measurement: " + taken + " (the screen as drawn, at the standing segments)");
            }

            // The scene's bodies and colliders, counted: the comparison changes none of them.
            // The targets' own physics: every collider and body under the instances' roots, counted and summed up, so
            // that one switched off, made, destroyed or changed shows.
            private string TargetPhysics()
            {
                int colliders = 0, enabled = 0, bodies = 0, kinematic = 0;
                uint sum = 2166136261;
                void Mix(int value)
                {
                    unchecked { sum = (sum ^ (uint)value) * 16777619; }
                }

                var seen = new HashSet<int>();
                foreach (Transform root in targetRoots)
                {
                    if (root == null)
                    {
                        Mix(-1);
                        continue;
                    }

                    foreach (Collider collider in root.GetComponentsInChildren<Collider>(true))
                    {
                        if (!seen.Add(collider.GetInstanceID())) continue;
                        colliders++;
                        if (collider.enabled) enabled++;
                        Mix(collider.GetInstanceID());
                        Mix((collider.enabled ? 1 : 0) | (collider.gameObject.activeInHierarchy ? 2 : 0) | (collider.isTrigger ? 4 : 0));
                    }

                    foreach (Rigidbody body in root.GetComponentsInChildren<Rigidbody>(true))
                    {
                        if (!seen.Add(body.GetInstanceID())) continue;
                        bodies++;
                        if (body.isKinematic) kinematic++;
                        Mix(body.GetInstanceID());
                        Mix((body.isKinematic ? 1 : 0) | (body.useGravity ? 2 : 0) | (body.detectCollisions ? 4 : 0));
                    }
                }

                return "roots " + targetRoots.Count + ", colliders " + colliders + " (enabled " + enabled + "), bodies " + bodies + " (kinematic " + kinematic + "), sum " + sum.ToString("x8");
            }

            // Every collider of the scene by its instance, with where it is and whether it is on.
            private static Dictionary<int, string> SceneColliders()
            {
                var all = new Dictionary<int, string>();
                foreach (Collider collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    all[collider.GetInstanceID()] = SceneDrawInventory.PathOf(collider.transform) + " (" + collider.GetType().Name + (collider.enabled ? ", on)" : ", off)");
                }

                return all;
            }

            // What differs between two readings, by name (the first twelve); null when nothing does.
            private static string ColliderChanges(Dictionary<int, string> before, Dictionary<int, string> now)
            {
                var lines = new List<string>();
                int gone = 0, made = 0, switched = 0;
                foreach (KeyValuePair<int, string> c in before)
                {
                    if (!now.TryGetValue(c.Key, out string after))
                    {
                        gone++;
                        if (lines.Count < 12) lines.Add("gone: " + c.Value);
                    }
                    else if (after != c.Value)
                    {
                        switched++;
                        if (lines.Count < 12) lines.Add("changed: " + c.Value + " -> " + after);
                    }
                }

                foreach (KeyValuePair<int, string> c in now)
                {
                    if (!before.ContainsKey(c.Key))
                    {
                        made++;
                        if (lines.Count < 12) lines.Add("new: " + c.Value);
                    }
                }

                return gone + made + switched == 0 ? null : "gone " + gone + ", new " + made + ", changed " + switched + ": " + string.Join("; ", lines);
            }

            private static string PhysicsState()
            {
                int bodies = 0, kinematic = 0, colliders = 0, enabled = 0;
                foreach (Rigidbody body in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    bodies++;
                    if (body.isKinematic) kinematic++;
                }

                foreach (Collider collider in UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    colliders++;
                    if (collider.enabled) enabled++;
                }

                return "bodies " + bodies + " (kinematic " + kinematic + "), colliders " + colliders + " (enabled " + enabled + ")";
            }

            // What of the crowd and the cuts is there: nothing, in a run that holds its conditions.
            private string Activity(out bool quiet)
            {
                int crowds = 0, crowdsOn = 0, slots = 0, live = 0, cycles = 0;
                foreach (MobPlanCrowd crowd in UnityEngine.Object.FindObjectsByType<MobPlanCrowd>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    crowds++;
                    if (crowd.enabled || crowd.IsReady) crowdsOn++;
                    slots += crowd.SlotCount;
                    live += crowd.LiveCount;
                    cycles += crowd.PublishedCycles;
                }

                int cuttables = 0, cutTargets = 0;
                foreach (PlayableCityCuttable cuttable in UnityEngine.Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    cuttables++;
                    if (cuttable.IsCutTarget) cutTargets++;
                }

                int skinned = 0;
                foreach (SkinnedMeshRenderer renderer in UnityEngine.Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (renderer.enabled) skinned++;
                }

                int panelsShown = 0;
                foreach (SandboxSlashPoseRecorder recorder in UnityEngine.Object.FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (recorder.enabled && recorder.ShowControls) panelsShown++;
                }

                foreach (SandboxSlashDiagnosticsOverlay overlay in UnityEngine.Object.FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (overlay.enabled && overlay.Visible) panelsShown++;
                }

                int owners = world.Owners != null ? world.Owners.Count : 0;
                int transactions = world.Driver != null ? world.Driver.Transactions.Count : 0;
                quiet = crowdsOn == 0 && slots == 0 && live == 0 && cycles == 0 && cutTargets == 0 && owners == 0 && transactions == 0 && skinned == 0 && panelsShown == 0;
                return "crowds " + crowds + " (running " + crowdsOn + ", slots " + slots + ", live characters " + live + ", published plans " + cycles + "); placed cuttables " + cuttables
                    + " (cut targets made " + cutTargets + "); physics owners of the cut world " + owners + ", cut transactions " + transactions + "; skinned renderers drawn " + skinned
                    + "; sandbox IMGUI panels drawn " + panelsShown;
            }

            private void WritePreparation()
            {
                File.WriteAllText(Path.Combine(settings.directory, "draw-compare-preparation.txt"), _preparation.ToString());
            }

            private void WriteFrames()
            {
                var text = new StringBuilder(_rows * 320);
                text.Append("frame,t,segment,real,deltaMs,ftCpuMs,ftMainMs,ftMainPresentWaitMs,ftRenderMs,ftGpuMs,xrAppGpuMs,xrCompositorGpuMs,xrDropped,eyeX,eyeY,eyeZ,yaw,camX,camY,camZ,camYaw,gcCount"
                    + ",displayCollections,displayRoomGrowths,displaySnapshotRegrowths,displayBuilds,displayPlacementPasses,placeQueries,placeStatic,displayRenderFragments,displayGpuReplacements"
                    + ",drawCommandsLive,drawInstancesLive,argumentElements,instanceElements,setDataCalls,wholeCalls,collectMs");
                foreach ((ProfilerCategory _, string name) in _markers) text.Append(',').Append(name);
                text.Append('\n');
                for (int r = 0; r < _rows; r++)
                {
                    text.Append(_frame[r]).Append(',').Append(_t[r].ToString("R", Inv)).Append(',').Append(_segment[r]).Append(',').Append(_real[r].ToString("R", Inv)).Append(',').Append(_deltaMs[r].ToString("R", Inv))
                        .Append(',').Append(_ftCpu[r].ToString("R", Inv)).Append(',').Append(_ftMain[r].ToString("R", Inv)).Append(',').Append(_ftWait[r].ToString("R", Inv))
                        .Append(',').Append(_ftRender[r].ToString("R", Inv)).Append(',').Append(_ftGpu[r].ToString("R", Inv)).Append(',').Append(_xrGpu[r].ToString("R", Inv))
                        .Append(',').Append(_xrCompositor[r].ToString("R", Inv)).Append(',').Append(_xrDropped[r])
                        .Append(',').Append(_x[r].ToString("R", Inv)).Append(',').Append(_y[r].ToString("R", Inv)).Append(',').Append(_z[r].ToString("R", Inv)).Append(',').Append(_yaw[r].ToString("R", Inv))
                        .Append(',').Append(_camX[r].ToString("R", Inv)).Append(',').Append(_camY[r].ToString("R", Inv)).Append(',').Append(_camZ[r].ToString("R", Inv)).Append(',').Append(_camYaw[r].ToString("R", Inv))
                        .Append(',').Append(_gc[r]).Append(',').Append(_collections[r]).Append(',').Append(_roomGrowths[r]).Append(',').Append(_snapshotRegrowths[r]).Append(',').Append(_builds[r])
                        .Append(',').Append(_placementPasses[r]).Append(',').Append(_queries[r]).Append(',').Append(_static[r]).Append(',').Append(_renderFragments[r]).Append(',').Append(_gpuReplacements[r])
                        .Append(',').Append(_commandsLive[r]).Append(',').Append(_instancesLive[r]).Append(',').Append(_argumentElements[r]).Append(',').Append(_instanceElements[r])
                        .Append(',').Append(_setDataCalls[r]).Append(',').Append(_wholeCalls[r]).Append(',').Append(_collectMs[r].ToString("R", Inv));
                    for (int i = 0; i < _markers.Length; i++) text.Append(',').Append(_values[i][r]);
                    text.Append('\n');
                }

                File.WriteAllText(Path.Combine(settings.directory, "frames.csv"), text.ToString());
            }

            // The run and what it holds, apart: a completed run is not a judgement of anything.
            private void WriteResult(bool completed, List<string> notes, in Standing atStart, in Standing atEnd, string activity, string physics)
            {
                bool held = completed && notes.Count == 0;
                var text = new StringBuilder();
                text.Append("run: ").Append(completed ? "completed" : "NOT completed").Append('\n');
                text.Append("comparison conditions: ").Append(held ? "held" : "NOT held").Append('\n');
                foreach (string note in notes) text.Append("note: ").Append(note).Append('\n');
                text.Append("checks: not run (this run measures; it judges nothing of the product)\n");
                text.Append("mode: ").Append(ModeName(settings.mode)).Append('\n');
                text.Append("launch: ").Append(SandboxLaunchRecord.Describe()).Append('\n');
                text.Append("path: ").Append(settings.path.Describe()).Append('\n');
                if (Inventory != null) text.Append("targets: ").Append(Inventory.Describe()).Append('\n');
                if (completed)
                {
                    text.Append("measurement frames: ").Append(MeasureStartFrame).Append("..").Append(MeasureEndFrame).Append("; rows ").Append(_rows).Append(" of a room of ").Append(_room)
                        .Append("; rows past the room ").Append(_rowsPastRoom).Append('\n');
                    text.Append("profiler: ").Append(ProfilerStartedHere ? "recorded from the measurement's first frame to its last into " + settings.profilerFile : "no recording asked for").Append('\n');
                    text.Append("world at the measurement's start: ").Append(atStart.Describe()).Append('\n');
                    text.Append("world at the measurement's end: ").Append(atEnd.Describe()).Append('\n');
                    text.Append("activity: ").Append(activity).Append('\n');
                    text.Append("physics: ").Append(physics).Append('\n');
                    text.Append("GPU time: ").Append(GpuTimeNote()).Append('\n');
                    if (CaptureRecord != null) text.Append("captures: ").Append(CaptureRecord).Append('\n');
                    text.Append("eye against the path: largest distance ").Append(LargestEyeDistance.ToString("F4", Inv)).Append(" m, largest heading difference ")
                        .Append(LargestEyeHeadingDifference.ToString("F3", Inv)).Append(" degrees (the camera as it stood through each row's frame, read at the next frame's start)\n");
                }

                File.WriteAllText(Path.Combine(settings.directory, "draw-compare-result.txt"), text.ToString());
            }

            // Whether a GPU time was obtained at all: a wait for the present or a frame's length is never put in its place.
            private string GpuTimeNote()
            {
                int frameTiming = 0, xr = 0;
                for (int r = 0; r < _rows; r++)
                {
                    if (_ftGpu[r] > 0) frameTiming++;
                    if (_xrGpu[r] > 0) xr++;
                }

                return "FrameTimingManager's GPU frame time answered in " + frameTiming + " of " + _rows + " rows" + (frameTiming == 0 ? " (NOT OBTAINED)" : "")
                    + "; the XR display's application GPU time in " + xr + " of " + _rows + " rows" + (xr == 0 ? " (NOT OBTAINED)" : "");
            }
        }
    }
}
