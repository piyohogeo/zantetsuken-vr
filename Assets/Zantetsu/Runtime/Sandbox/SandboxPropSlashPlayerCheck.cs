using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Observability;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Trace;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The Player check of the Prop path in Sandbox.unity (Phase 4.51), on when the Player is started with
    /// <see cref="Argument"/> and a directory. It replays recorded device grip poses and views through the sandbox
    /// Recorder into the katana -- the product core's entrance -- and only watches: the katana's waves hit the box
    /// through the world's hit detector, and every cut, publication and commit is the product's own. Nothing here asks
    /// for a cut, publishes anything or says that something was hit.
    /// <para>
    /// **Time.** One recorded row is fed per frame (the Recorder's own replay), so the waves move on the recorded
    /// clock: a row's replay time is the clock start plus its recorded offset. The frame rate is asked to be the
    /// recording's (<see cref="FrameRateArgument"/>, 90 by default) so that the recorded clock and the real one -- which
    /// the physics steps on -- stay close; how far apart they actually were is written at every hit and at the end, and
    /// is what says whether the run was at ordinary speed.
    /// </para>
    /// <para>
    /// It writes every hit (Slash, fragment, what the acceptance said, the input row and the three clocks), the frame
    /// each accepted cut was Provisional, published and committed, takes pictures of those frames from a camera of its
    /// own, and ends the world the ordinary way before quitting with 0 when the expected path held.
    /// </para>
    /// <para>
    /// **The pieces' physics.** The cuts are complete when every accepted cut's geometry has committed; that is not a
    /// physical rest. After it the ordinary Steps go on for a fixed observation period, apart from the cuts' times, and
    /// every live piece is written frame by frame from the frame its owner is first seen (pieces.csv: StepId, the
    /// unsimulated time, position, velocity, its own lowest vertex, its colliders, the floor's footprint and contact).
    /// What is judged is that the Steps went on, that every piece's colliders stayed on its body, and -- a check limited
    /// to this scenario, not a guarantee to catch every way through a floor -- where each piece's own lowest vertex was
    /// in the first row it was under the floor's underside: inside the floor's footprint fails, outside it is written as
    /// a fall past the floor's edge. Where a piece's own
    /// B-rep stands at one moment is written only as a diagnostic: the cooked collider and the discrete Steps may leave
    /// it a little into the floor (DESIGN 7.3).
    /// </para>
    /// <para>
    /// **A character** (<see cref="SandboxNpcCharacter"/>, the private NPC scene): the check waits for its preparation to
    /// finish before the replay begins -- a fixed step of the start, not a wait for anything late -- restarts its Pose
    /// Table with the replay, and then only watches: which cuts are of the character's lineage and which of the box's,
    /// the Source Time of the pose each hit met and of the pose at that Slash's latch, and the frame the character's root
    /// was withdrawn.
    /// </para>
    /// <para>
    /// **Measurement** (for performance observation; the Gameplay, input, drawing, Pose and physics are the same). Every
    /// run keeps a frame timeline in memory -- real and recorded time, StepId, the unsimulated time, sweeps and hits,
    /// and the frame's product, engine and check markers -- and writes it (frames.csv) after the world has ended.
    /// <see cref="LightArgument"/> leaves out what only observes: the check's own view camera and its pictures, the
    /// pieces' rows and the observation period, the per-frame fast-piece scan; its log lines are kept in memory and
    /// written after the run. <see cref="IterationsArgument"/> runs the scenario that many times in one process, each on
    /// the scene loaded anew (a new, uncut character and world), into iter-NN directories; the process ends with the
    /// worst code.
    /// </para>
    /// <para>
    /// **Trace** (<see cref="TraceArgument"/>): this check composes one trace run of its own -- one main-thread lane
    /// carrying <see cref="TraceEventType.SlashHitConfirmed"/>, a paged history, drained every frame -- and gives the
    /// lane to the hit detector. At the end the lane is taken back, the run is finished and saved, read back, and every
    /// record is compared with the hits this check saw. Without the argument nothing of it exists, and the hits, cuts
    /// and pictures are the same path.
    /// </para>
    /// </summary>
    public static partial class SandboxPropSlashPlayerCheck
    {
        public const string Argument = "-zantetsuPropSlash";
        public const string DebugColoursArgument = "-zantetsuPropDebugColours";
        public const string FrameRateArgument = "-zantetsuPropFrameRate";
        public const string TraceArgument = "-zantetsuPropTrace";
        public const string LightArgument = "-zantetsuPropLight";
        public const string IterationsArgument = "-zantetsuPropIterations";
        public const string MarkersArgument = "-zantetsuPropMarkers";
        public const string UiArgument = "-zantetsuPropUi";

        /// <summary>
        /// Measurement only: the character's withdrawal per iteration, a string of A (the whole root) and B (its parts)
        /// used in turn (iteration i takes character i mod its length). Without it, the registration's own choice holds.
        /// </summary>
        public const string WithdrawalArgument = "-zantetsuPropWithdrawal";

        // Measurement (XR Simulator city): list the available frame / render / XR / wait recorders once.
        public const string ListMarkersArgument = "-zantetsuPropListMarkers";

        /// <summary>Measurement: raised on the frame the replay and the target's Pose Table start, for scene-side walkers.</summary>
        public static event Action ReplayBegun;

        /// <summary>Image evidence: raised where the check logs a hit, with its lineage ("npc", "box", ...) and frame.</summary>
        public static event Action<string, int> HitLogged;

        /// <summary>Image evidence (2026-10-01): raised on the frame the required re-cut during a drop begins, before the script (a movie can start there).</summary>
        public static event Action MidDropSectionBegun;

        internal static void RaiseMidDropSectionBegun() => MidDropSectionBegun?.Invoke();

        // Image evidence (XR Temporary): hold the geometry pool's finished work for N frames after the first hit.
        public const string HoldGeometryArgument = "-zantetsuPropHoldGeometry";

        // Measurement (re-cut miss): pieces and sweeps recorded in a light run too.
        public const string TraceMissArgument = "-zantetsuPropTraceMiss";

        // Measurement (the moving VRS): after the preparation, the replay begins when the XR head leaves its pose.
        public const string StartOnHeadMoveArgument = "-zantetsuPropStartOnHeadMove";

        // Measurement (image runs): a longer head wait, a hold before the ending, and the updated characters per frame.
        public const string HeadWaitSecondsArgument = "-zantetsuPropHeadWaitSeconds";
        public const string EndHoldSecondsArgument = "-zantetsuPropEndHoldSeconds";

        // How many input rows are replayed from the start row (at most the recorder's capacity, which is also the default).
        public const string RowsArgument = "-zantetsuSlashRows";

        /// <summary>The hull scenario (2026-09-30): after the replay, the recovery to one hull is waited for (10 s), held (3 s), the current hulls are re-cut by synthetic Slashes through the ordinary detection path, and the recovery waited for again.</summary>
        public const string HullScenarioArgument = "-zantetsuHullScenario";
        public const string LodPhasesArgument = "-zantetsuPropLodPhases";
        public const string ViewTurnArgument = "-zantetsuPropViewTurn";

        // Measurement (the cost of the added shadows): the characters' and the cut pieces' shadows off, the city's kept.
        public const string AddedShadowsOffArgument = "-zantetsuAddedShadowsOff";
        private static HeldGeometryPool s_heldGeometry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallGeometryHold()
        {
            if (Value(HoldGeometryArgument) == null)
            {
                return;
            }

            CutWorldRoot.nextWorldExecutors = destination => destination == WorkDestination.GeometryPool
                ? s_heldGeometry = new HeldGeometryPool(WorkerPoolExecutor.GeometryPool(4))
                : null;
        }

        // CutWorldRootPlayModeTests.HoldingExecutor's hold, in the Player: finished work is taken from the destination
        // and kept from the collection while HoldEverything, then handed on first, as it came back.
        private sealed class HeldGeometryPool : IWorkExecutor, IDisposable
        {
            private readonly IWorkExecutor _inner;
            private readonly List<(IDispatchWork work, WorkCompletion completion)> _held = new List<(IDispatchWork, WorkCompletion)>();

            internal HeldGeometryPool(IWorkExecutor inner) { _inner = inner; }

            internal bool HoldEverything { get; set; } = true;
            internal int HoldingCount => _held.Count;
            public WorkDestination Destination => _inner.Destination;
            public int Capacity => _inner.Capacity;
            public int Held => _inner.Held;
            public bool CanAccept => _inner.CanAccept;
            public bool TryAccept(IDispatchWork work) => _inner.TryAccept(work);
            public void BeginAccepted(IDispatchWork work) => _inner.BeginAccepted(work);

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (!HoldEverything && _held.Count > 0)
                {
                    (work, completion) = _held[0];
                    _held.RemoveAt(0);
                    return true;
                }

                if (!_inner.TryTakeFinished(out work, out completion))
                {
                    return false;
                }

                if (!HoldEverything)
                {
                    return true;
                }

                _held.Add((work, completion));
                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork() => _inner.CloseForNewWork();
            public bool StopAndConfirm(int timeoutMilliseconds) => _inner.StopAndConfirm(timeoutMilliseconds) && _held.Count == 0;
            public void Dispose() => (_inner as IDisposable)?.Dispose();
        }
        public const string Prefix = "PROP SLASH: ";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            string directory = Value(Argument);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            // A check Player keeps running when its window is not in focus (the project setting stays as it is).
            Application.runInBackground = true;
            var host = new GameObject("Prop slash player check");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Runner runner = host.AddComponent<Runner>();
            runner.directory = directory;
            runner.iterations = int.TryParse(Value(IterationsArgument), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int n) && n > 1 ? n : 1;
        }

        // Runs the check once, or the given number of times in this process, each time on the scene loaded anew.
        private sealed class Runner : MonoBehaviour
        {
            internal string directory;
            internal int iterations;

            private IEnumerator Start()
            {
                int worst = 0;
                string scene = SceneManager.GetActiveScene().path;
                for (int i = 0; i < iterations; i++)
                {
                    if (i > 0)
                    {
                        InstallGeometryHold();
                        SceneManager.LoadScene(scene, LoadSceneMode.Single);
                        yield return null;
                    }

                    string order = Value(WithdrawalArgument);
                    string mode = string.IsNullOrEmpty(order) ? "registration" : order[i % order.Length].ToString();
                    PhysicsCut.PreparedCharacterWithdrawal.CompareWholeRoot = mode == "A";
                    Walk walk = gameObject.AddComponent<Walk>();
                    walk.withdrawalMode = mode;
                    walk.directory = iterations > 1 ? Path.Combine(directory, "iter-" + i.ToString("D2", CultureInfo.InvariantCulture)) : directory;
                    walk.iteration = i;
                    walk.input = Value(SandboxSlashReplayPlayerCheck.InputArgument);
                    walk.start = int.TryParse(Value(SandboxSlashReplayPlayerCheck.StartArgument), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int s) ? s : 1336;
                    walk.debugColours = Has(DebugColoursArgument);
                    walk.trace = Has(TraceArgument);
                    walk.light = Has(LightArgument);
                    walk.extraMarkers = Value(MarkersArgument);
                    walk.showUi = Has(UiArgument);
                    walk.listMarkers = Has(ListMarkersArgument) && i == 0;
                    walk.traceMiss = Has(TraceMissArgument);
                    walk.startOnHeadMove = float.TryParse(Value(StartOnHeadMoveArgument), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float move) ? move : 0f;
                    walk.headWaitSeconds = double.TryParse(Value(HeadWaitSecondsArgument), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double wait) ? wait : 60.0;
                    walk.endHoldSeconds = float.TryParse(Value(EndHoldSecondsArgument), NumberStyles.Float, CultureInfo.InvariantCulture,
                        out float hold) ? hold : 0f;
                    walk.lodPhases = Has(LodPhasesArgument);
                    walk.multiNpc = Has(MultiNpcArgument);
                    walk.building = Has(BuildingArgument);
                    walk.mobPlan = Value(MobPlanArgument);
                    walk.rows = int.TryParse(Value(RowsArgument), NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowLimit) && rowLimit > 0
                        ? Math.Min(rowLimit, SandboxSlashPoseRecorder.Capacity) : SandboxSlashPoseRecorder.Capacity;
                    walk.hullScenario = Has(HullScenarioArgument);
                    string[] turn = (Value(ViewTurnArgument) ?? "").Split(',');
                    if (turn.Length == 4 && int.TryParse(turn[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int turnIteration) && turnIteration == i)
                    {
                        walk.viewTurnStart = double.Parse(turn[1], CultureInfo.InvariantCulture);
                        walk.viewTurnSeconds = double.Parse(turn[2], CultureInfo.InvariantCulture);
                        walk.viewTurnDegrees = float.Parse(turn[3], CultureInfo.InvariantCulture);
                    }
                    walk.ParseCaptureCamera();   // image runs only (2026-10-01): a capture camera of its own, the XR camera untouched
                    walk.frameRate = int.TryParse(Value(FrameRateArgument), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int rate) ? rate : 90;
                    while (!walk.Done)
                    {
                        yield return null;
                    }

                    worst = Math.Max(worst, walk.Code);
                    Log("iteration " + i + " of " + iterations + " finished with code " + walk.Code);
                    Destroy(walk);
                    yield return null;
                    if (walk.Code == 13 || walk.Code == 15)
                    {
                        break;
                    }
                }

                Log("all iterations finished with code " + worst);
                yield return null;
                Application.Quit(worst);
            }
        }

        private static string Value(string name)
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i + 1];
                }
            }

            return null;
        }

        private static bool Has(string name)
        {
            foreach (string argument in Environment.GetCommandLineArgs())
            {
                if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Kept in memory while a light run is being measured, and written after it.
        private static List<string> s_heldLog;

        /// <summary>Holds the log in memory from here until <see cref="ReleaseLog"/> (a light run's measurement; a test of the ending).</summary>
        internal static void HoldLog()
        {
            s_heldLog = new List<string>(512);
        }

        internal static void Log(string line)
        {
            if (s_heldLog != null)
            {
                s_heldLog.Add(line);
                return;
            }

            Debug.Log(Prefix + line);
        }

        /// <summary>Writes the held log out, in order, and stops holding. Nothing held: nothing to write.</summary>
        internal static void ReleaseLog()
        {
            List<string> held = s_heldLog;
            s_heldLog = null;
            if (held != null)
            {
                foreach (string line in held)
                {
                    Debug.Log(Prefix + line);
                }
            }
        }

        // After the driver's late update (-100), so what it reads is what this frame will draw: nothing of a cut's
        // state changes after the late updates.
        [DefaultExecutionOrder(400)]
        private sealed partial class Walk : MonoBehaviour
        {
            internal string directory;
            internal string input;
            internal int start;
            internal int rows;
            internal bool hullScenario;
            internal bool debugColours;
            internal bool trace;
            internal bool light;
            internal string extraMarkers;
            internal bool showUi;
            internal bool listMarkers;
            internal bool traceMiss;
            internal float startOnHeadMove;
            internal double headWaitSeconds = 60.0;
            internal float endHoldSeconds;
            internal bool lodPhases;
            internal double viewTurnStart = -1.0, viewTurnSeconds;
            internal float viewTurnDegrees;
            private Transform _viewTurn;
            private bool _viewTurned;
            private StreamWriter _sweepRows;
            private StreamWriter _pairRows, _pairVertices;
            private readonly List<CurrentShape> _pairShapes = new List<CurrentShape>();
            private float3[] _pairSection = new float3[64];
            internal string withdrawalMode;
            internal int iteration;
            internal int frameRate;

            internal bool Done { get; private set; }

            internal int Code { get; private set; }

            private static readonly ProfilerMarker s_checkLate = new ProfilerMarker("Zantetsu.Check.LateUpdate");
            private static readonly ProfilerMarker s_checkPicture = new ProfilerMarker("Zantetsu.Check.Picture");

            // The frame timeline: one row per frame from the replay's start to the end of the run, its markers filled in
            // the frame after (a recorder's last value is the frame before the one it is read in).
            private static readonly (ProfilerCategory category, string name)[] TimelineMarkers =
            {
                (ProfilerCategory.Internal, "PlayerLoop"),
                (ProfilerCategory.Internal, "WaitForTargetFPS"),
                (ProfilerCategory.Internal, "Gfx.WaitForPresentOnGfxThread"),
                (ProfilerCategory.Internal, "Main Thread"),
                (ProfilerCategory.Memory, "GC Allocated In Frame"),
                (ProfilerCategory.Scripts, "Zantetsu.PoseTable.Evaluate"),
                (ProfilerCategory.Scripts, "Zantetsu.PoseTable.Apply"),
                (ProfilerCategory.Scripts, "Zantetsu.SlashHit.Evaluate"),
                (ProfilerCategory.Scripts, "Zantetsu.Driver.Update"),
                (ProfilerCategory.Scripts, "Zantetsu.Driver.LateUpdate"),
                (ProfilerCategory.Scripts, "Zantetsu.Driver.AfterRendering"),
                (ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot"),
                (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Validate"),
                (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Collect"),
                (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Group"),
                (ProfilerCategory.Scripts, "Zantetsu.Snapshot.Place"),
                (ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Simulate"),
                (ProfilerCategory.Scripts, "Zantetsu.CutPhysicsStep.Collect"),
                (ProfilerCategory.Scripts, "Zantetsu.Check.LateUpdate"),
                (ProfilerCategory.Scripts, "Zantetsu.Check.Picture"),
            };

            private (ProfilerCategory category, string name)[] _timelineMarkers;
            private ProfilerRecorder[] _timelineRecorders;
            private readonly List<FrameRow> _timeline = new List<FrameRow>(1024);
            private readonly Dictionary<int, int> _timelineRowOf = new Dictionary<int, int>();
            private bool _timing;
            private string _phase = "replay";
            private int _picturesAsked;

            private sealed class FrameRow
            {
                public int frame;
                public string phase;
                public double real;
                public float delta;
                public int fed;
                public double recorded;
                public long stepId;
                public double unsimulated;
                public int sweeps;
                public int waves;
                public int hits;
                public int pictures;
                public long[] markers;
                public double ftCpu = double.NaN, ftMain = double.NaN, ftMainPresentWait = double.NaN, ftRender = double.NaN, ftGpu = double.NaN;
                public double xrGpu = double.NaN, xrCompositorGpu = double.NaN;
                public int xrDropped = -1;
                public Vector3 viewPosition;
                public float viewYaw, viewPitch;
                public int decisionFrame = -1;
                public double lastSimulateMs = double.NaN, expectedMs = double.NaN, remainingMs = double.NaN;
                public int stepped = -1;
                public int lodL0 = -1, lodL1 = -1, lodL2 = -1, lodL3 = -1, lodUpdated = -1, lodForced = -1, lodTarget = -1;
                public string lodPhases;
                // The display's collections of this very frame, stamped where they ran (-1: not known).
                public long displayCollections = -1, displayBuilds = -1, displayValidations = -1, displayPlacements = -1, displayRoomGrowths = -1, displaySnapshotRegrowths = -1;
                public VpValidateCounts validate;   // the validations' parts of this frame (a copy), null when not known
                public VpPlaceCounts placeStructural, placePlacementOnly;   // the Place passes of this frame, structural and placement-only (copies), null when not known
            }

            private PoseLodDirector _lodDirector;
            private bool _lodLooked;

            private readonly FrameTiming[] _frameTimings = new FrameTiming[1];
            private int _firstHitFrame = -1;
            private bool _holdReleased;
            private UnityEngine.XR.XRDisplaySubsystem _xrDisplay;
            private readonly List<SandboxNpcCharacter> _npcs = new List<SandboxNpcCharacter>();

            private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
            private readonly List<double> _rowTimes = new List<double>();
            private readonly List<int> _rowOf = new List<int>();
            private readonly List<int> _refusedRows = new List<int>();
            private readonly HashSet<int> _fastSeen = new HashSet<int>();

            // The pieces' physics, frame by frame, from the frame each owner is first seen to the end of the observation
            // period (pieces.csv): read in this LateUpdate, which is before the frame's Step, so a row carries the state
            // the last Step (its StepId) and this frame's publications left.
            private const float ObservationPhysicsSeconds = 3f;
            private StreamWriter _pieceRows;
            private Collider _floor;
            private bool _observing;
            private readonly Dictionary<int, PieceTrack> _tracks = new Dictionary<int, PieceTrack>();

            private sealed class PieceTrack
            {
                public int firstFrame;
                public long firstStep;
                public float startLowest = float.NaN;
                public long startStep = -1;
                public float endLowest;
                public long endStep;
                public string end;
                public bool endTouch;
                public bool endOverFloor;
                public string underFloor;      // the first row its lowest own vertex was under the floor's underside
                public bool underWhileOver;    // ... and that vertex was over the floor's footprint then
                public string collidersOff;    // the first row a collider was off or not on the piece's body
            }

            private long _observedSteps = -1;
            private long _askedSteps;
            private readonly List<Accepted> _accepted = new List<Accepted>();
            private readonly List<(long slash, LogicalFragmentId fragment)> _confirmed = new List<(long, LogicalFragmentId)>();
            private readonly List<string> _pictures = new List<string>();
            private readonly List<SlashHitConfirmed> _seenHits = new List<SlashHitConfirmed>();
            // The input time of the last hit list read, and the frames that still showed an evaluation already read.
            private double _lastHitListAt = double.NaN;
            private readonly List<(int frame, double at, int count, int fed)> _hitListRereads = new List<(int, double, int, int)>();
            private TraceLaneSet _traceLanes;
            private TracePagedHistory _traceHistory;
            private TraceLaneDrainer _traceDrainer;
            private const int TraceMaxPayload = 32;
            private CutWorldRoot _world;
            private SandboxRightHandKatana _katana;
            private SandboxSlashPoseRecorder _recorder;
            private SlashHitDetector _detector;
            private SandboxCutWorldProbe _probe;
            private Camera _view;
            private RenderTexture _target;
            private bool _replaying;
            private double _clockStart;
            private int _loaded;
            private int _failures;
            private int _hitsLogged;
            private int _rootCuts;
            private int _childCuts;
            private SandboxNpcCharacter _npc;
            private PoseTablePlayer _npcPose;
            private readonly Dictionary<long, (int frame, double source)> _latchPose = new Dictionary<long, (int, double)>();
            private long _lastLatchSeen;
            private int _npcRootCuts, _npcChildCuts, _boxRootCuts, _boxChildCuts;
            private int _npcRootAcceptedFrame = -1;
            private int _npcWithdrawnFrame = -1;
            private int _npcPoseFrameAtWithdrawal = -1;

            private bool NpcDrawn() => _npc.Renderer != null && _npc.Renderer.enabled && _npc.Renderer.gameObject.activeInHierarchy;

            private bool MotionInScene() => _npc.MotionBody != null && _npc.MotionBody.gameObject.activeInHierarchy;

            // Left: not drawn, and not a target of the detector (its handle ends with its cut, or says it is none).
            private bool NpcLeft() => !NpcDrawn() && (_npc.Handle == null || _npc.Handle.IsDisposed || !_npc.Handle.IsHitTarget);

            // Observation only: the Pose Table's and the hit detector's markers, frame by frame (a recorder's last value
            // is the frame before the one it is read in).
            private ProfilerRecorder _poseEvaluateNs, _poseApplyNs, _hitEvaluateNs;
            private readonly Dictionary<int, (long evaluate, long apply, long hit)> _markerNs = new Dictionary<int, (long, long, long)>();
            private readonly List<(int frame, string what)> _hitFrames = new List<(int, string)>();

            private sealed class Accepted
            {
                public long slash;
                public LogicalFragmentId fragment;
                public CutOperationId operation;
                public bool child;
                public int acceptedFrame;
                public int publishedFrame = -1;
                public int committedFrame = -1;
                public string name;

                // The multi-NPC mode: whether it was accepted as Pending, the frame its Provisional pair was published
                // (the acceptance's own frame when Published), the stages' real times, and how a Pending one ended if
                // it did without being published.
                public bool pending;

                // The hull trial: accepted as Pending by a hull group, which issues no operation to this check (its cut's outcome is the trial's own record).
                public bool hull;
                public int provisionalFrame = -1;
                public double acceptedTime = double.NaN, provisionalTime = double.NaN, finalTime = double.NaN, committedTime = double.NaN;
                public string pendingEnd;

                // A hit on a fused building member (Pending, with no operation of its own): the fusion answers it, and
                // its success is followed from the fusion's hit record to its members' operations (never committedFrame).
                public bool fusion;
            }

            private IEnumerator Start()
            {
                Directory.CreateDirectory(directory);
                Application.quitting += MultiQuitting;
                yield return null;

                foreach (CutWorldRoot candidate in FindObjectsByType<CutWorldRoot>(FindObjectsSortMode.None))
                {
                    if (candidate.name == "Prop Cut World") _world = candidate;
                }

                _katana = FindAnyObjectByType<SandboxRightHandKatana>();
                _recorder = FindAnyObjectByType<SandboxSlashPoseRecorder>();
                SandboxSlashPropHit hit = FindAnyObjectByType<SandboxSlashPropHit>();
                _detector = hit != null ? hit.Detector : null;
                _probe = _world != null ? _world.GetComponent<SandboxCutWorldProbe>() : null;
                GameObject viewObject = GameObject.Find("Prop Check View");
                _view = viewObject != null ? viewObject.GetComponent<Camera>() : null;
                if (_world == null || !_world.IsReady || _katana == null || _recorder == null || _detector == null
                    || (!multiNpc && !building && !MobPlanMode && (_probe == null || !_probe.Body.IsSet)) || _view == null || (!MobPlanLive && (string.IsNullOrEmpty(input) || !File.Exists(input))))
                {
                    Log("FAILED: the scene is not wired (world, katana, recorder, detector, box, view) or no input: " + input);
                    Finish(13);
                    yield break;
                }

                // The fixed preparation: the frame rate asked for, the colours, the check's own view. No waiting.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = frameRate;
                VpCutSurfaceColour.SetDebugEnabled(debugColours);
                if (!light)
                {
                    _target = new RenderTexture(960, 600, 24);
                    _target.Create();
                    _view.targetTexture = _target;
                    _view.enabled = true;
                }

                // The sandbox's adjustment and diagnostics IMGUI is not drawn unless asked for (UiArgument); the
                // recorder, capture, gesture and waves go on as they are, and so does everything else drawn.
                foreach (SandboxSlashPoseRecorder recorderUi in FindObjectsByType<SandboxSlashPoseRecorder>(FindObjectsSortMode.None))
                {
                    recorderUi.ShowControls = showUi;
                }

                foreach (SandboxSlashDiagnosticsOverlay overlay in FindObjectsByType<SandboxSlashDiagnosticsOverlay>(FindObjectsSortMode.None))
                {
                    overlay.Visible = showUi;
                }

                WriteEnvironment();
                Log("sandbox IMGUI (recorder controls, diagnostics overlay): " + (showUi ? "shown" : "hidden"));
                Log("separation impulse given to every accepted cut (box and character): "
                    + (hit != null ? hit.SeparationImpulse.ToString("R", Inv) : "none") + " N·s");
                if (Has(AddedShadowsOffArgument))
                {
                    TurnAddedShadowsOff();
                }

                Log("added shadows: characters' surface renderers casting=" + CountSurfaceCasters()
                    + " cut display casters=" + DisplayCasters());
                Log("separation strength from the world's profile: byMass=" + _world.Profile.SeparationImpulseByMass
                    + " k=" + _world.Profile.SeparationImpulsePerKg.ToString("R", Inv)
                    + " N·s per kg of each free child's mass (byMass off: the fixed value above is used); strength connected="
                    + (_world.Driver.SeparationStrength != null));
                // The MobPlan city: the script and the crowd first; its NPCs are enabled when its plan is loaded.
                if (MobPlanMode)
                {
                    yield return MobPlanWaitReady();
                    if (_crowd == null || !_crowd.IsReady)
                    {
                        Log("FAILED: the MobPlan crowd did not become ready");
                        Finish(15);
                        yield break;
                    }
                }

                // Several NPCs (the XR Simulator city): the target is "NPC Casual"; every active one is waited for.
                foreach (SandboxNpcCharacter found in FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None))
                {
                    if (!_npcs.Contains(found)) _npcs.Add(found);
                }

                _npcs.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
                _npc = _npcs.Find(c => c.CharacterRoot != null && c.CharacterRoot.name == "NPC Casual") ?? (_npcs.Count > 0 ? _npcs[0] : null);
                if (_npc != null)
                {
                    // The characters' loading frames: each cold preparation finishes, then it is a target. Bounded; a
                    // target character that does not become one ends the check.
                    int until = Time.frameCount + 1200;
                    // A dormant prepared slot (the MobPlan crowd's spares) is ready without being a target.
                    while (_npcs.Exists(c => !c.IsTarget && !c.IsPrepared && c.Failure == null) && Time.frameCount < until)
                    {
                        yield return null;
                    }

                    var prepFrames = new List<int>();
                    var confirmMs = new List<double>();
                    int prepared = 0, failed = 0;
                    foreach (SandboxNpcCharacter c in _npcs)
                    {
                        if (c.IsTarget) { prepared++; prepFrames.Add(c.PreparationFrames); confirmMs.Add(c.ConfirmSeconds * 1000.0); }
                        if (c.Failure != null) failed++;
                    }

                    prepFrames.Sort();
                    confirmMs.Sort();
                    Log("npcs: active=" + _npcs.Count + " prepared=" + prepared + " failed=" + failed + " target=" + (_npc.CharacterRoot != null ? _npc.CharacterRoot.name : "none")
                        + (prepared > 0 ? " preparationFrames median=" + prepFrames[prepFrames.Count / 2] + " max=" + prepFrames[prepFrames.Count - 1]
                            + " partsCheck ms median=" + confirmMs[confirmMs.Count / 2].ToString("F3", Inv) + " max=" + confirmMs[confirmMs.Count - 1].ToString("F3", Inv) : "")
                        + " prepared by frame " + Time.frameCount);

                    _npcPose = _npc.CharacterRoot != null ? _npc.CharacterRoot.GetComponent<PoseTablePlayer>() : null;
                    PoseTable table = _npcPose != null ? _npcPose.Table : null;
                    Log("npc: target=" + _npc.IsTarget + " failure=" + (_npc.Failure ?? "none") + " preparationFrames=" + _npc.PreparationFrames
                        + " hulls=" + _npc.HullCount + " renderer=" + (_npc.Renderer != null ? _npc.Renderer.name : "none")
                        + " table=" + (table != null ? table.ClipName + " (" + table.ClipId + ") duration=" + table.DurationSeconds.ToString("R", Inv)
                            + " rate=" + table.SampleRate.ToString("R", Inv) + " samples=" + table.SampleCount + " looping=" + table.IsLooping
                            + " bones=" + table.BoneCount + " resolved=" + _npcPose.ResolvedBoneCount + " unresolved=" + _npcPose.UnresolvedBoneCount
                            + " requiresBones=" + _npcPose.RequiresBones + " bonesConfirmed=" + _npcPose.BonesConfirmed : "none (" + (_npcPose != null ? _npcPose.BindError : "no player") + ")")
                        + " root=" + (_npc.CharacterRoot != null ? _npc.CharacterRoot.transform.position.ToString("F4") : "none"));
                    LogCharacterInventory();
                    Log("npc withdrawal: registration asks for " + (_npc.WithdrawsParts ? "its parts" : "the whole root (" + _npc.WholeRootReason + ")")
                        + "; this run: " + withdrawalMode + (withdrawalMode == "A" ? " (whole root, for comparison)" : "")
                        + "; the parts' check at preparation took " + (_npc.ConfirmSeconds * 1000.0).ToString("F3", Inv) + " ms");
                    if (!_npc.IsTarget || table == null)
                    {
                        Log("FAILED: the character did not become a hit target with a Pose Table");
                        Finish(15);
                        yield break;
                    }
                }

                // The building E2E: the scenario registers the building once the world is ready. Bounded; a building
                // that is not registered ends the check.
                if (building)
                {
                    _building = FindAnyObjectByType<BuildingSlashE2E>();
                    if (_building != null)
                    {
                        BuildingPlaceViews();
                    }

                    int until = Time.frameCount + 600;
                    while (_building != null && !_building.IsRegistered && Time.frameCount < until)
                    {
                        yield return null;
                    }

                    if (!BuildingReady())
                    {
                        Log("FAILED: the building was not registered: " + (_building != null ? _building.Registration : "no scenario"));
                        Finish(15);
                        yield break;
                    }
                }

                if (trace)
                {
                    BeginTrace();
                }

                string[] lines = MobPlanMode ? new string[0] : File.ReadAllLines(input);
                if (!MobPlanMode) _recorder.BeginRecording();
                for (int i = start + 1; i < lines.Length && _loaded < rows; i++, _loaded++)
                {
                    string[] c = lines[i].Split(',');
                    float F(int k) => float.Parse(c[k], Inv);
                    double t = double.Parse(c[2], Inv);
                    var sample = new BladePoseSample(long.Parse(c[1], Inv), t, new Vector3(F(3), F(4), F(5)),
                        new Quaternion(F(6), F(7), F(8), F(9)), (BladeTrackingState)int.Parse(c[10], Inv));
                    // The recorder refuses a row whose time does not advance (the saved run repeats a timestamp now and
                    // then); such a row is not replayed, by the recorder's own rule, and is written down here.
                    if (!_recorder.TryAppendRecordedSample(sample, new Vector3(F(13), F(14), F(15))))
                    {
                        _refusedRows.Add(start + _loaded);
                        continue;
                    }

                    _rowTimes.Add(t);
                    _rowOf.Add(start + _loaded);
                }

                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                CookingAuditBegin();   // PhysX cooking errors from here on, from any thread
                if (light)
                {
                    HoldLog();
                }

                CaptureBegin();   // the game's view recorded as it stands; image runs only: the capture camera made beside it
                HullRequiredDecide();   // the required sections, decided here from the scenario and the profile, before anything is found or run
                if (startOnHeadMove > 0f)
                {
                    Camera head = Camera.main;
                    if (head == null)
                    {
                        Log("FAILED: no XR head camera to wait on");
                        Finish(16);
                        yield break;
                    }

                    int readyFrame = Time.frameCount;
                    double readyTime = Time.unscaledTimeAsDouble;
                    Vector3 readyPosition = head.transform.position;
                    Quaternion readyRotation = head.transform.rotation;
                    Log("head wait: ready frame=" + readyFrame + " time=" + readyTime.ToString("F4", Inv) + " head=" + readyPosition.ToString("F4")
                        + " yaw=" + readyRotation.eulerAngles.y.ToString("F2", Inv) + " pitch=" + readyRotation.eulerAngles.x.ToString("F2", Inv)
                        + " unsimulated=" + (clock != null ? clock.UnsimulatedSeconds.ToString("F6", Inv) : "none")
                        + " threshold=" + startOnHeadMove.ToString("R", Inv) + " m or 0.5 deg");
                    // The cut limit on (2026-10-01): the required re-cut during a drop runs here, to its end, before the head-wait loop
                    // lets the script begin (an external replay seen meanwhile breaks its condition: a failure, recorded).
                    yield return HullMidDropBeforeScript(true, head, readyPosition, readyRotation);

                    var wait = new StringBuilder("frame,time,x,y,z,yaw,pitch,unsimulated,stepId\n");
                    bool moved = false;
                    int looks = 0;
                    while (Time.unscaledTimeAsDouble < readyTime + headWaitSeconds)
                    {
                        Vector3 at = head.transform.position;
                        Vector3 e = head.transform.eulerAngles;
                        wait.Append(Time.frameCount).Append(',').Append(Time.unscaledTimeAsDouble.ToString("R", Inv)).Append(',')
                            .Append(at.x.ToString("R", Inv)).Append(',').Append(at.y.ToString("R", Inv)).Append(',').Append(at.z.ToString("R", Inv)).Append(',')
                            .Append(e.y.ToString("R", Inv)).Append(',').Append(e.x.ToString("R", Inv)).Append(',')
                            .Append(clock != null ? clock.UnsimulatedSeconds.ToString("R", Inv) : "").Append(',').Append(clock != null ? clock.StepId : -1).Append('\n');
                        looks++;
                        if (Vector3.Distance(at, readyPosition) > startOnHeadMove || Quaternion.Angle(head.transform.rotation, readyRotation) > 0.5f)
                        {
                            moved = true;
                            break;
                        }

                        yield return null;
                    }

                    File.WriteAllText(Path.Combine(directory, "head-wait.csv"), wait.ToString());
                    // Moved already at the second look (one frame after ready): the replay was under way before the check was
                    // ready, so the run is not under the comparison's condition. It goes on, and is written down as such.
                    _replayBeforeReady = moved && looks <= 2;
                    if (_replayBeforeReady)
                    {
                        Log("[condition] head wait: the head was already moving " + looks + " look(s) after ready: the VRS replay began before the check was ready");
                    }
                    Log("head wait: " + (moved ? "moved" : "did not move") + " at frame=" + Time.frameCount + " time=" + Time.unscaledTimeAsDouble.ToString("F4", Inv)
                        + " (" + (Time.unscaledTimeAsDouble - readyTime).ToString("F4", Inv) + " s after ready) head=" + head.transform.position.ToString("F4")
                        + " unsimulated=" + (clock != null ? clock.UnsimulatedSeconds.ToString("F6", Inv) : "none"));
                    if (!moved)
                    {
                        Log("FAILED: the XR head did not move within " + headWaitSeconds.ToString("R", Inv) + " s of the preparation");
                        Finish(16);
                        yield break;
                    }
                }

                // Without a head wait the required re-cut during a drop still runs, here, before the script (never left out in silence).
                if (!(startOnHeadMove > 0f))
                {
                    yield return HullMidDropBeforeScript(false, null, Vector3.zero, Quaternion.identity);
                }

                StartTimeline();
                _clockStart = Time.unscaledTimeAsDouble;
                _replaying = MobPlanMode || _recorder.TryBeginReplay(_clockStart);
                if (!MobPlanMode) _npcPose?.Restart();
                if (MobPlanMode)
                {
                    MobPlanBegin();
                }

                if (multiNpc)
                {
                    MultiBegin();
                }

                if (building)
                {
                    BuildingBegin();
                }

                ReplayBegun?.Invoke();
                _poseEvaluateNs = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.PoseTable.Evaluate");
                _poseApplyNs = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.PoseTable.Apply");
                _hitEvaluateNs = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.SlashHit.Evaluate");
                Log("start: frame=" + Time.frameCount + " rows " + start + ".." + (start + _loaded - 1) + " replayed=" + _rowTimes.Count
                    + " refusedByRecorder=[" + string.Join(",", _refusedRows) + "]"
                    + " replay begun=" + _replaying + " clock start=" + _clockStart.ToString("F4", Inv)
                    + " (a row's replay time = clock start + its recorded time - " + (_rowTimes.Count > 0 ? _rowTimes[0].ToString("R", Inv) : "none") + ")"
                    + " physicsHz=" + (clock != null ? clock.FrequencyHz : 0)
                    + " unsimulatedAtStart=" + (clock != null ? clock.UnsimulatedSeconds.ToString("F6", Inv) : "none")
                    + " stepId=" + (clock != null ? clock.StepId : -1)
                    + " box=" + (_probe != null && _probe.Actor != null ? _probe.Actor.transform.position.ToString("F4") : "none") + " targetFrameRate=" + frameRate
                    + " debugColours=" + debugColours + " light=" + light + " iteration=" + iteration);
                RequestPicture("0-start");

                float deadline = Time.realtimeSinceStartup + (MobPlanMode ? (float)_mpEnd + 30f : 120f);
                bool cutsComplete = false;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    if (MobPlanMode)
                    {
                        if (MobPlanFinished()) { cutsComplete = true; break; }
                        continue;
                    }

                    bool fedThrough = !_recorder.IsReplaying && _recorder.ReplayIndex >= _rowTimes.Count;
                    // Every accepted cut's geometry committed: the end of the cuts, not a physical rest of the pieces.
                    if (fedThrough && _katana.WaveCount == 0 && CutsSettled() && _accepted.Count > 0)
                    {
                        cutsComplete = true;
                        break;
                    }
                }

                _replaying = false;
                ManualPhysicsClock stepClock = CutPhysicsStep.Clock;
                if (cutsComplete)
                {
                    Log("cuts complete (every accepted cut's geometry committed, the fusion settled, replay fed through, no wave flying): frame=" + Time.frameCount
                        + " realSinceStart=" + (Time.unscaledTimeAsDouble - _clockStart).ToString("F4", Inv)
                        + " stepId=" + (stepClock != null ? stepClock.StepId : -1));
                }
                else
                {
                    // The wait ended at its deadline (TL, 2026-09-30: it used to say "cuts complete" whatever it met): what
                    // is still waiting, and why. Nothing waiting is read as done later -- an ending that abandons it is
                    // not a completion.
                    Log("cuts NOT complete at the waiting deadline: frame=" + Time.frameCount + " realSinceStart=" + (Time.unscaledTimeAsDouble - _clockStart).ToString("F4", Inv)
                        + " stepId=" + (stepClock != null ? stepClock.StepId : -1) + "; " + DescribeWaiting());
                    Expect(false, "[scenario] the cuts completed before the waiting deadline (" + DescribeWaiting() + ")");
                }
                if (building)
                {
                    BuildingStepBack();
                }

                RequestPicture("8-cuts-complete");
                WriteTimeline(false);

                // The observation period, apart from the cuts' completion: the ordinary Steps go on for a fixed physics
                // time, nothing is simulated, synchronised or placed by the check, and every piece is followed. A light
                // run has none: it goes on to the ending.
                _phase = "observe";
                _observing = !light;
                long observeFrom = stepClock != null ? stepClock.StepId : 0;
                float observeSeconds = building ? BuildingObserveSeconds : ObservationPhysicsSeconds;
                float observeCap = building ? Mathf.Max(BuildingObservationRealCap, 2f * BuildingObserveSeconds + 10f) : 10f;
                long observeSteps = stepClock != null ? (long)Math.Round(observeSeconds * stepClock.FrequencyHz) : 0;
                int observeFrame = Time.frameCount;
                float observeBy = Time.realtimeSinceStartup + observeCap;
                while (!light && stepClock != null && stepClock.StepId < observeFrom + observeSteps && Time.realtimeSinceStartup < observeBy)
                {
                    yield return null;
                }

                _observedSteps = (stepClock != null ? stepClock.StepId : 0) - observeFrom;
                _askedSteps = observeSteps;
                Log("observation: frames " + observeFrame + ".." + Time.frameCount + " steps " + observeFrom + ".." + (stepClock != null ? stepClock.StepId : -1)
                    + " (asked " + observeSteps + " steps = " + observeSeconds.ToString("R", Inv) + " s of physics; real cap " + observeCap.ToString("R", Inv) + " s)");
                yield return null;
                _observing = false;
                if (endHoldSeconds > 0f)
                {
                    _phase = "hold";
                    float holdUntil = Time.realtimeSinceStartup + endHoldSeconds;
                    Log("hold: " + endHoldSeconds.ToString("R", Inv) + " s before the ending, from frame " + Time.frameCount);
                    while (Time.realtimeSinceStartup < holdUntil)
                    {
                        yield return null;
                    }
                }

                if (hullScenario && _world != null && _world.Hulls != null)
                {
                    yield return HullScenario();
                }

                // The coexistence run's building in the always-kinematic hull trial: the re-cut during a drop, a section of its own after the script.
                if (_hullMidDrop.Required)
                {
                    // With the cut limit on (required, decided before the run), the re-cut during a drop ran before the script; here a
                    // building past N is shown cut no more. Not run (the world ending or without its trial) is judged at the summary.
                    if (_world != null && _world.Hulls != null && !_world.IsEnding) yield return HullLimitCheck();
                }
                else if (MobPlanMode && PlayableCity && _world != null && _world.Hulls != null && _world.Hulls.Settings.kinematicDisplay && !_world.IsEnding)
                {
                    yield return HullMidDropRecut();
                }

                // The MobPlan crowd's reused slots: one live individual on a reused slot, hit once through the ordinary detector by a synthetic Slash at its current shape (a section of its own after the script).
                if (MobPlanMode && !MobPlanLive && _crowd != null && _world != null && !_world.IsEnding)
                {
                    yield return MobPlanReuseCheck();
                }

                yield return CaptureDrain();   // image runs: the capture camera given back and its sink finished (bounded by the sink's deadline)
                RequestPicture("9-end");
                yield return null;
                yield return null;
                // The ending (CheckEnding): a failure inside one part of the summary must not stop the later parts, the
                // world's reclaim, the log's release: each part is guarded, and the whole is guarded once more.
                var parts = new List<CheckEnding.Part> { new CheckEnding.Part("summary", Summarise) };
                if (trace)
                {
                    parts.Add(new CheckEnding.Part("trace", FinishTrace));
                }

                parts.Add(new CheckEnding.Part("end shadows", () =>
                {
                    Log("added shadows at the end: frame=" + Time.frameCount + " " + DisplayCasters());
                    _phase = "ending";
                }));
                var ending = new CheckEnding(Log);
                yield return ending.Run(_world, parts, 15f);   // the ordinary ending, carried by the ordinary frames
                _failures += ending.Failures;
                yield return null;
                Finish(ending, null);
            }

            /// <summary>An early end with a fixed code (nothing of the run to summarise): the same completion.</summary>
            private void Finish(int code) => Finish(new CheckEnding(Log), code);

            // The end of this run (CheckEnding.Complete): the records' close, the timeline, the view target, each guarded;
            // the code (the fixed one of an early end, unless a closing step failed); the held log's release; Done.
            private void Finish(CheckEnding ending, int? fixedCode)
            {
                try
                {
                    Application.quitting -= MultiQuitting;
                    _timing = false;
                    var steps = new List<CheckEnding.Part>
                    {
                        new CheckEnding.Part("capture sinks cancelled if still live", CaptureCancel),
                        new CheckEnding.Part("cooking audit after the reclaim", CookingAuditFinal),
                        new CheckEnding.Part("records close", MultiClose),
                        new CheckEnding.Part("timeline", () => WriteTimeline(true)),
                        new CheckEnding.Part("view target", ReleaseTarget),
                    };
                    ending.Complete(steps, ReleaseLog,
                        closingFailures => fixedCode.HasValue && closingFailures == 0 ? fixedCode.Value : CheckEnding.CodeOf(_failures + closingFailures, _replayBeforeReady),
                        code => { Code = code; Done = true; });
                }
                finally
                {
                    // Every ending comes here -- the ordinary one, an early one, one whose summary threw -- and the
                    // cooking audit's subscription to the log goes with it, whatever came before.
                    _cookingAudit.End();
                }
            }

            private void ReleaseTarget()
            {
                if (_target != null)
                {
                    if (_view != null) _view.targetTexture = null;
                    _target.Release();
                    Destroy(_target);
                    _target = null;
                }
            }

            // The fixed markers, and any the run names after MarkersArgument (separated by ';', each "Category:Name" or
            // a name in Internal), so that what a
            // measurement looks at is chosen without building again.
            private void StartTimeline()
            {
                var markers = new List<(ProfilerCategory, string)>(TimelineMarkers);
                foreach (string entry in (extraMarkers ?? string.Empty).Split(';'))
                {
                    // "Category:Name" (a ProfilerCategory property, such as Render or Scripts), or a name in Internal.
                    string text = entry.Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    int colon = text.IndexOf(':');
                    ProfilerCategory category = ProfilerCategory.Internal;
                    if (colon > 0)
                    {
                        System.Reflection.PropertyInfo named = typeof(ProfilerCategory).GetProperty(text.Substring(0, colon),
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                        if (named != null && named.PropertyType == typeof(ProfilerCategory))
                        {
                            category = (ProfilerCategory)named.GetValue(null);
                            text = text.Substring(colon + 1);
                        }
                    }

                    markers.Add((category, text));
                }

                _timelineMarkers = markers.ToArray();
                var displays = new List<UnityEngine.XR.XRDisplaySubsystem>();
                SubsystemManager.GetSubsystems(displays);
                _xrDisplay = displays.Find(d => d.running);
                Log("frame timing: FrameTimingManager enabled=" + FrameTimingManager.IsFeatureEnabled()
                    + " xr active=" + UnityEngine.XR.XRSettings.isDeviceActive + " device=" + UnityEngine.XR.XRSettings.loadedDeviceName
                    + " stereo=" + UnityEngine.XR.XRSettings.stereoRenderingMode + " eyeTexture=" + UnityEngine.XR.XRSettings.eyeTextureWidth + "x" + UnityEngine.XR.XRSettings.eyeTextureHeight
                    + " renderScale=" + UnityEngine.XR.XRSettings.eyeTextureResolutionScale.ToString("R", Inv)
                    + " refreshRate=" + (_xrDisplay != null && _xrDisplay.TryGetDisplayRefreshRate(out float hz) ? hz.ToString("R", Inv) : "n/a")
                    + " xrAppGpuTime=" + (_xrDisplay != null && _xrDisplay.TryGetAppGPUTimeLastFrame(out float g) ? "answers" : "no answer")
                    + " xrCompositorGpuTime=" + (_xrDisplay != null && _xrDisplay.TryGetCompositorGPUTimeLastFrame(out float cg) ? "answers" : "no answer")
                    + " xrDroppedFrames=" + (_xrDisplay != null && _xrDisplay.TryGetDroppedFrameCount(out int dr) ? "answers" : "no answer")
                    + " graphics=" + SystemInfo.graphicsDeviceType + " gpu=" + SystemInfo.graphicsDeviceName + " cpu=" + SystemInfo.processorType
                    + " screen=" + Screen.width + "x" + Screen.height + " quality=" + QualitySettings.names[QualitySettings.GetQualityLevel()]);
                if (_xrDisplay != null)
                {
                    int passes = _xrDisplay.GetRenderPassCount();
                    var views = new StringBuilder();
                    for (int k = 0; k < passes; k++)
                    {
                        _xrDisplay.GetRenderPass(k, out UnityEngine.XR.XRDisplaySubsystem.XRRenderPass pass);
                        views.Append(k == 0 ? "" : ",").Append(pass.GetRenderParameterCount());
                    }

                    Camera eyes = Camera.main;
                    string separation = "n/a";
                    if (eyes != null && eyes.stereoEnabled)
                    {
                        Vector3 left = eyes.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3);
                        Vector3 right = eyes.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3);
                        separation = (Vector3.Distance(left, right) * 1000f).ToString("F1", Inv) + " mm";
                    }

                    Log("stereo: xr render passes=" + passes + " views per pass=[" + views + "] camera stereoEnabled="
                        + (eyes != null && eyes.stereoEnabled) + " eye separation=" + separation + " camera=" + (eyes != null ? eyes.name : "none"));
                }

                if (listMarkers)
                {
                    var handles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
                    Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
                    var names = new List<string>();
                    foreach (Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle h in handles)
                    {
                        Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderDescription d = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(h);
                        string n = d.Name;
                        if (System.Text.RegularExpressions.Regex.IsMatch(n, "XR|Wait|Present|Gfx|GPU|Render Thread|Main Thread|FrameTime|Frame Time|Skin|Semaphore|Vsync|VSync"))
                        {
                            names.Add(d.Category.Name + ":" + n);
                        }
                    }

                    names.Sort(string.CompareOrdinal);
                    Log("available recorders (" + names.Count + " of " + handles.Count + "): " + string.Join(" | ", names));
                }

                _timelineRecorders = new ProfilerRecorder[_timelineMarkers.Length];
                for (int i = 0; i < _timelineMarkers.Length; i++)
                {
                    _timelineRecorders[i] = ProfilerRecorder.StartNew(_timelineMarkers[i].category, _timelineMarkers[i].name);
                }

                _timing = true;
            }

            // One row for this frame, and the markers of the frame before into that frame's row.
            private void RecordFrame()
            {
                int frame = Time.frameCount;
                if (_timelineRowOf.TryGetValue(frame - 1, out int before))
                {
                    FrameRow previous = _timeline[before];
                    previous.markers = new long[_timelineRecorders.Length];
                    for (int i = 0; i < _timelineRecorders.Length; i++)
                    {
                        previous.markers[i] = _timelineRecorders[i].Valid ? _timelineRecorders[i].LastValue : -1;
                    }

                    // The display's counts of that same frame, as it stamped them (not the counters' difference between two reads).
                    if (_world != null && _world.Display != null && _world.Display.TryGetFrameCounts(frame - 1, out VpLogicalCutDisplay.FrameCounts counts))
                    {
                        previous.displayCollections = counts.collections;
                        previous.displayBuilds = counts.structureBuilds;
                        previous.displayValidations = counts.structureValidations;
                        previous.displayPlacements = counts.placementPasses;
                        previous.displayRoomGrowths = counts.roomGrowths;
                        previous.displaySnapshotRegrowths = counts.snapshotRegrowths;
                        if (counts.validate != null) { previous.validate = new VpValidateCounts(); previous.validate.CopyFrom(counts.validate); }
                        if (counts.placeStructural != null) { previous.placeStructural = new VpPlaceCounts(); previous.placeStructural.CopyFrom(counts.placeStructural); }
                        if (counts.placePlacementOnly != null) { previous.placePlacementOnly = new VpPlaceCounts(); previous.placePlacementOnly.CopyFrom(counts.placePlacementOnly); }
                    }
                }

                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                int fed = _recorder != null ? _recorder.ReplayIndex : 0;
                SlashWaveCore core = _katana != null ? _katana.Core : null;
                _timelineRowOf[frame] = _timeline.Count;
                _timeline.Add(new FrameRow
                {
                    frame = frame,
                    phase = _phase,
                    real = Time.unscaledTimeAsDouble - _clockStart,
                    delta = Time.unscaledDeltaTime,
                    fed = fed,
                    recorded = fed > 0 && fed <= _rowTimes.Count ? _rowTimes[fed - 1] - _rowTimes[0] : double.NaN,
                    stepId = clock != null ? clock.StepId : -1,
                    unsimulated = clock != null ? clock.UnsimulatedSeconds : double.NaN,
                    sweeps = core != null ? core.SweepCount : -1,
                    waves = _katana != null ? _katana.WaveCount : -1,
                    hits = _replaying && HitListIsNew() ? _detector.HitCount : 0,
                    pictures = _picturesAsked,
                });
                _picturesAsked = 0;
                FillExtras(_timeline[_timeline.Count - 1]);
                ReleaseGeometryHoldWhenDue();
            }

            // The hold ends N frames after the first hit; the held work is then collected as it would have been.
            private void ReleaseGeometryHoldWhenDue()
            {
                if (s_heldGeometry == null || _holdReleased || _firstHitFrame < 0
                    || !int.TryParse(Value(HoldGeometryArgument), NumberStyles.Integer, Inv, out int frames)
                    || Time.frameCount < _firstHitFrame + frames)
                {
                    return;
                }

                _holdReleased = true;
                Log("geometry hold: released at frame " + Time.frameCount + " (" + frames + " frames after the first hit at "
                    + _firstHitFrame + "), " + s_heldGeometry.HoldingCount + " finished works were held");
                s_heldGeometry.HoldEverything = false;
            }

            // Measurement (XR Simulator city): the frame timing, the XR display's statistics and the view drawn.
            private void FillExtras(FrameRow row)
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _frameTimings) > 0)
                {
                    row.ftCpu = _frameTimings[0].cpuFrameTime;
                    row.ftMain = _frameTimings[0].cpuMainThreadFrameTime;
                    row.ftMainPresentWait = _frameTimings[0].cpuMainThreadPresentWaitTime;
                    row.ftRender = _frameTimings[0].cpuRenderThreadFrameTime;
                    row.ftGpu = _frameTimings[0].gpuFrameTime;
                }

                if (_xrDisplay != null)
                {
                    if (_xrDisplay.TryGetAppGPUTimeLastFrame(out float xrGpu)) row.xrGpu = xrGpu;
                    if (_xrDisplay.TryGetCompositorGPUTimeLastFrame(out float xrComp)) row.xrCompositorGpu = xrComp;
                    if (_xrDisplay.TryGetDroppedFrameCount(out int dropped)) row.xrDropped = dropped;
                }

                row.decisionFrame = CutPhysicsStep.LastDecidedFrame;
                row.lastSimulateMs = CutPhysicsStep.LastSimulateSeconds * 1000.0;
                row.expectedMs = CutPhysicsStep.LastDecisionExpectedSeconds * 1000.0;
                row.remainingMs = CutPhysicsStep.LastDecisionRemainingSeconds * 1000.0;
                row.stepped = CutPhysicsStep.LastDecisionStepped ? 1 : 0;
                if (!_lodLooked)
                {
                    _lodDirector = FindAnyObjectByType<PoseLodDirector>(FindObjectsInactive.Include);
                    _lodLooked = true;
                }

                if (_lodDirector != null && _lodDirector.isActiveAndEnabled)
                {
                    row.lodL0 = row.lodL1 = row.lodL2 = row.lodL3 = row.lodForced = 0;
                    row.lodUpdated = _lodDirector.UpdatedLastFrame;
                    foreach (PoseLodCharacter c in _lodDirector.Characters)
                    {
                        row.lodForced += c.ForcedCount;
                        if (_npcPose != null && c.Player == _npcPose) row.lodTarget = c.IsLive ? c.Level : -2;
                        if (lodPhases && c.LastUpdateFrame == Time.frameCount) row.lodPhases = (row.lodPhases == null ? "" : row.lodPhases + ";") + c.Phase;
                        if (!c.IsLive) continue;
                        if (c.Level == 0) row.lodL0++; else if (c.Level == 1) row.lodL1++; else if (c.Level == 2) row.lodL2++; else row.lodL3++;
                    }
                }
                Camera main = Camera.main;
                if (main != null)
                {
                    row.viewPosition = main.transform.position;
                    Vector3 e = main.transform.eulerAngles;
                    row.viewYaw = e.y;
                    row.viewPitch = e.x;
                }
            }

            // Written at the cuts' completion (so far) and at the end (all of it, and the recorders go back): at a
            // boundary of the run, never within the replay.
            private void WriteTimeline(bool final)
            {
                if (_timelineRecorders == null)
                {
                    return;
                }

                var text = new StringBuilder(_timeline.Count * 160);
                text.Append("frame,phase,real,delta,fed,recorded,realMinusRecorded,stepId,unsimulated,sweeps,waves,hits,picturesAsked");
                text.Append(",ftCpuMs,ftMainMs,ftMainPresentWaitMs,ftRenderMs,ftGpuMs,xrAppGpuMs,xrCompositorGpuMs,xrDropped,viewX,viewY,viewZ,viewYaw,viewPitch,decisionFrame,lastSimulateMs,expectedMs,remainingMs,stepped,lodL0,lodL1,lodL2,lodL3,lodUpdated,lodForced,lodTarget,lodPhases,displayCollections,displayBuilds,displayValidations,displayPlacements,displayRoomGrowths,displaySnapshotRegrowths,vStructural,vPlacementOnly,vRegistrations,vPlacementRegistrations,vIndexMs,vInputMs,vContractMs,vAncestorsMs,vOperationsMs,vPlacementInputMs,vPlacementContractMs,vAncestorSteps,vAncestorLookups,vPlaneChecks,vOperations,vOwnerLookups,vOwnerSteps,vOwnerCacheHits,vUnreflectedSteps,vIndexesBuilt,vIndexesReused,vAncestorReads,cMs,cChainsMs,cSelectMs,cCapMs,cBranches,cCollections,cChainSteps,cOperationReads,cCandidates,cCapIdentities,vAncestorHits,cVisits,cReads,cHits,sMs,vSegEntries,cSplices,cSegBoundaries,cLookups,psPasses,psMs,psRenderFragments,psQueries,psChecks,psPlaneTransforms,psSectionsFound,psSectionsReused,psSectionsBuilt,psSectionCompared,psCapClips,ppPasses,ppMs,ppRenderFragments,ppQueries,ppChecks,ppPlaneTransforms,ppSectionsFound,ppSectionsReused,ppSectionsBuilt,ppSectionCompared,ppCapClips,psProviderMs,psCheckMs,psRestMs,ppProviderMs,ppCheckMs,ppRestMs");
                foreach ((ProfilerCategory _, string name) in _timelineMarkers)
                {
                    text.Append(',').Append(name);
                }

                text.Append('\n');
                foreach (FrameRow r in _timeline)
                {
                    text.Append(r.frame).Append(',').Append(r.phase).Append(',').Append(r.real.ToString("R", Inv)).Append(',')
                        .Append(r.delta.ToString("R", Inv)).Append(',').Append(r.fed).Append(',').Append(r.recorded.ToString("R", Inv)).Append(',')
                        .Append((r.real - r.recorded).ToString("R", Inv)).Append(',').Append(r.stepId).Append(',')
                        .Append(r.unsimulated.ToString("R", Inv)).Append(',').Append(r.sweeps).Append(',').Append(r.waves).Append(',')
                        .Append(r.hits).Append(',').Append(r.pictures)
                        .Append(',').Append(r.ftCpu.ToString("R", Inv)).Append(',').Append(r.ftMain.ToString("R", Inv))
                        .Append(',').Append(r.ftMainPresentWait.ToString("R", Inv)).Append(',').Append(r.ftRender.ToString("R", Inv))
                        .Append(',').Append(r.ftGpu.ToString("R", Inv)).Append(',').Append(r.xrGpu.ToString("R", Inv)).Append(',').Append(r.xrCompositorGpu.ToString("R", Inv)).Append(',').Append(r.xrDropped)
                        .Append(',').Append(r.viewPosition.x.ToString("R", Inv)).Append(',').Append(r.viewPosition.y.ToString("R", Inv))
                        .Append(',').Append(r.viewPosition.z.ToString("R", Inv)).Append(',').Append(r.viewYaw.ToString("R", Inv))
                        .Append(',').Append(r.viewPitch.ToString("R", Inv))
                        .Append(',').Append(r.decisionFrame).Append(',').Append(r.lastSimulateMs.ToString("R", Inv))
                        .Append(',').Append(r.expectedMs.ToString("R", Inv)).Append(',').Append(r.remainingMs.ToString("R", Inv))
                        .Append(',').Append(r.stepped)
                        .Append(',').Append(r.lodL0).Append(',').Append(r.lodL1).Append(',').Append(r.lodL2).Append(',').Append(r.lodL3)
                        .Append(',').Append(r.lodUpdated).Append(',').Append(r.lodForced).Append(',').Append(r.lodTarget)
                        .Append(',').Append(r.lodPhases ?? "")
                        .Append(',').Append(r.displayCollections).Append(',').Append(r.displayBuilds).Append(',').Append(r.displayValidations)
                        .Append(',').Append(r.displayPlacements).Append(',').Append(r.displayRoomGrowths).Append(',').Append(r.displaySnapshotRegrowths);
                    VpValidateCounts v = r.validate;
                    if (v != null)
                    {
                        text.Append(',').Append(v.structural).Append(',').Append(v.placementOnly).Append(',').Append(v.registrations).Append(',').Append(v.placementRegistrations)
                            .Append(',').Append((v.indexSeconds * 1000).ToString("R", Inv)).Append(',').Append((v.inputSeconds * 1000).ToString("R", Inv)).Append(',').Append((v.contractSeconds * 1000).ToString("R", Inv))
                            .Append(',').Append((v.ancestorSeconds * 1000).ToString("R", Inv)).Append(',').Append((v.operationsSeconds * 1000).ToString("R", Inv)).Append(',').Append((v.placementInputSeconds * 1000).ToString("R", Inv))
                            .Append(',').Append((v.placementContractSeconds * 1000).ToString("R", Inv)).Append(',').Append(v.ancestorSteps).Append(',').Append(v.ancestorLookups).Append(',').Append(v.planeChecks)
                            .Append(',').Append(v.operations).Append(',').Append(v.ownerLookups).Append(',').Append(v.ownerSteps).Append(',').Append(v.ownerCacheHits).Append(',').Append(v.unreflectedSteps)
                            .Append(',').Append(v.indexesBuilt).Append(',').Append(v.indexesReused).Append(',').Append(v.ancestorReads)
                            .Append(',').Append((v.collectSeconds * 1000).ToString("R", Inv)).Append(',').Append((v.collectIntoSeconds * 1000).ToString("R", Inv)).Append(',').Append((v.selectSeconds * 1000).ToString("R", Inv))
                            .Append(',').Append((v.capIdentitySeconds * 1000).ToString("R", Inv)).Append(',').Append(v.branches).Append(',').Append(v.collectCalls).Append(',').Append(v.chainSteps)
                            .Append(',').Append(v.operationReads).Append(',').Append(v.candidatesMade).Append(',').Append(v.capIdentities)
                            .Append(',').Append(v.ancestorHits).Append(',').Append(v.collectVisits).Append(',').Append(v.collectReads).Append(',').Append(v.collectHits)
                            .Append(',').Append((v.structureSeconds * 1000).ToString("R", Inv))
                            .Append(',').Append(v.segmentEntries).Append(',').Append(v.collectSplices).Append(',').Append(v.collectSegmentBoundaries).Append(',').Append(v.collectLookups);
                    }
                    else
                    {
                        text.Append(",,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,");
                    }

                    // The Place passes of this frame, structural (ps) and placement-only (pp) apart (2026-10-01).
                    foreach (VpPlaceCounts p in new[] { r.placeStructural, r.placePlacementOnly })
                    {
                        if (p == null) { text.Append(",,,,,,,,,,,"); continue; }
                        text.Append(',').Append(p.passes).Append(',').Append((p.seconds * 1000).ToString("R", Inv)).Append(',').Append(p.renderFragments).Append(',').Append(p.queries)
                            .Append(',').Append(p.placementChecks).Append(',').Append(p.planeTransforms).Append(',').Append(p.sectionsFoundHere).Append(',').Append(p.sectionsReused)
                            .Append(',').Append(p.sectionsBuilt).Append(',').Append(p.sectionEntriesCompared).Append(',').Append(p.capClips);
                    }

                    // The diagnosis's blocks of the Place passes (0 unless -zantetsuPlacePhased), structural then placement-only.
                    foreach (VpPlaceCounts p in new[] { r.placeStructural, r.placePlacementOnly })
                    {
                        if (p == null) { text.Append(",,,"); continue; }
                        text.Append(',').Append((p.providerSeconds * 1000).ToString("R", Inv)).Append(',').Append((p.checkSeconds * 1000).ToString("R", Inv)).Append(',').Append((p.restSeconds * 1000).ToString("R", Inv));
                    }
                    for (int i = 0; i < _timelineMarkers.Length; i++)
                    {
                        text.Append(',').Append(r.markers != null ? r.markers[i] : -1);
                    }

                    text.Append('\n');
                }

                File.WriteAllText(Path.Combine(directory, final ? "frames.csv" : "frames-at-cuts-complete.csv"), text.ToString());
                if (!final)
                {
                    return;
                }

                var valid = new StringBuilder();
                for (int i = 0; i < _timelineMarkers.Length; i++)
                {
                    valid.Append(_timelineMarkers[i].name).Append('=').Append(_timelineRecorders[i].Valid).Append(' ');
                    _timelineRecorders[i].Dispose();
                }

                _timelineRecorders = null;
                Log("timeline: " + _timeline.Count + " frames written to frames.csv; recorders " + valid);
            }

            // A run left behind by a check that ended early goes back with it.
            private void OnDestroy()
            {
                if (_timelineRecorders != null)
                {
                    foreach (ProfilerRecorder recorder in _timelineRecorders) recorder.Dispose();
                    _timelineRecorders = null;
                }

                _pieceRows?.Dispose();
                _sweepRows?.Dispose();
                _pairRows?.Dispose();
                _pairVertices?.Dispose();
                _poseEvaluateNs.Dispose();
                _poseApplyNs.Dispose();
                _hitEvaluateNs.Dispose();
                _traceHistory?.Dispose();
                _traceLanes?.Dispose();
            }

            // The view turned by viewTurnDegrees about the vertical through the head while the replay time is inside the
            // turn's window, straight otherwise; logged when it turns and when it comes back.
            private void TurnView(double replayTime)
            {
                Camera eyes = Camera.main;
                if (eyes == null || eyes.transform.parent == null)
                {
                    return;
                }

                if (_viewTurn == null)
                {
                    Transform offset = eyes.transform.parent;
                    _viewTurn = new GameObject("Measurement View Turn").transform;
                    _viewTurn.SetParent(offset, false);
                    eyes.transform.SetParent(_viewTurn, false);
                }

                bool turned = replayTime >= viewTurnStart && replayTime < viewTurnStart + viewTurnSeconds;
                Vector3 head = eyes.transform.localPosition;
                Quaternion yaw = turned ? Quaternion.Euler(0f, viewTurnDegrees, 0f) : Quaternion.identity;
                _viewTurn.localRotation = yaw;
                _viewTurn.localPosition = head - yaw * head;
                if (turned != _viewTurned)
                {
                    _viewTurned = turned;
                    Log("view turn: " + (turned ? "turned " + viewTurnDegrees.ToString("R", Inv) + " deg" : "back") + " at frame " + Time.frameCount
                        + " replay time " + replayTime.ToString("F4", Inv));
                }
            }

            private void LateUpdate()
            {
                using (s_checkLate.Auto())
                {
                    LateUpdateObserved();
                }
            }

            private void LateUpdateObserved()
            {
                GameViewFrame();   // every frame: the game's view references still the XR camera's
                if (viewTurnStart >= 0.0 && _replaying)
                {
                    TurnView(Time.unscaledTimeAsDouble - _clockStart);
                }

                if (_timing)
                {
                    RecordFrame();
                }

                if ((!light || traceMiss) && (_replaying || _observing) && _world != null && !_world.IsEnding)
                {
                    TrackPieces();
                }

                if (building && _world != null && _world.Hulls != null && !_world.IsEnding)
                {
                    BuildingHullFrame(Time.frameCount);   // every frame of the run, observed or not: the costs' frame sums over the whole run
                }

                if (building && _observing)
                {
                    BuildingFrame(Time.frameCount);
                }

                if (traceMiss && _replaying)
                {
                    RecordSweeps();
                    RecordHitPairs(3);
                }

                if (!_replaying || _world == null || _world.IsEnding)
                {
                    return;
                }

                int frame = Time.frameCount;
                int update = _recorder.ReplayIndex - 1;
                if (_hitEvaluateNs.Valid)
                {
                    _markerNs[frame - 1] = (_poseEvaluateNs.LastValue, _poseApplyNs.LastValue, _hitEvaluateNs.LastValue);
                }

                // The pose the character stands in when each Slash latches, to set beside the pose its hit meets.
                for (int w = 0; w < _katana.WaveCount; w++)
                {
                    long id = _katana.SlashIdAt(w);
                    if (id > _lastLatchSeen)
                    {
                        _lastLatchSeen = id;
                        if (_npcPose != null)
                        {
                            _latchPose[id] = (_npcPose.AppliedFrame, _npcPose.AppliedSourceTime);
                        }
                    }
                }

                // Any piece moving faster than 20 m/s, the first time it does: which, how heavy, where (not in a light run).
                for (int id = 1; !light && id < 256; id++)
                {
                    var fragment = new LogicalFragmentId(id);
                    if (!_world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
                    {
                        break;
                    }

                    if (state == LogicalFragmentState.Live && !_fastSeen.Contains(id) && _world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner)
                        && !owner.IsWithdrawn && owner.Body != null && owner.Body.linearVelocity.magnitude > 20f)
                    {
                        _fastSeen.Add(id);
                        Log("fast piece: fragment=" + id + " of=" + LineageOf(fragment) + " frame=" + frame
                            + " stepId=" + (CutPhysicsStep.Clock != null ? CutPhysicsStep.Clock.StepId : -1) + " mass=" + owner.Body.mass.ToString("R", Inv)
                            + " speed=" + owner.Body.linearVelocity.magnitude.ToString("F2", Inv) + " position=" + owner.Root.transform.position.ToString("F3")
                            + " inertia=" + owner.Body.inertiaTensor.ToString("G4"));
                    }
                }

                // The character's withdrawal, as seen from outside: it is no longer drawn and no longer a hit target (the
                // same frame), its pose is not applied again, and its motion body takes no part in the physics.
                if (_npc != null && _npcWithdrawnFrame < 0 && NpcLeft())
                {
                    _npcWithdrawnFrame = frame;
                    _npcPoseFrameAtWithdrawal = _npcPose != null ? _npcPose.AppliedFrame : -1;
                    Log("npc withdrawn: frame=" + frame + " renderer drawn=" + NpcDrawn() + " hit target=" + (_npc.Handle != null && !_npc.Handle.IsDisposed && _npc.Handle.IsHitTarget)
                        + " root active=" + _npc.CharacterRoot.activeInHierarchy + " motion body in the scene=" + MotionInScene()
                        + " pose last applied at frame " + _npcPoseFrameAtWithdrawal);
                }

                if (HitListIsNew())
                {
                    _lastHitListAt = _detector.HitAt(0).At;
                    for (int i = 0; i < _detector.HitCount; i++)
                    {
                        SlashHitConfirmed hit = _detector.HitAt(i);
                        _seenHits.Add(hit);
                        LogHit(hit, frame, update);
                    }
                }
                else if (_detector.HitCount > 0)
                {
                    _hitListRereads.Add((frame, _detector.HitAt(0).At, _detector.HitCount, _recorder.ReplayIndex));
                    Log("hit list not new: frame=" + frame + " the detector still holds the " + _detector.HitCount
                        + " hit(s) of the evaluation at input time " + _detector.HitAt(0).At.ToString("R", Inv) + " (fed "
                        + _recorder.ReplayIndex + "); not read again");
                }

                // The MobPlan refill unit's burst of simultaneous retirements (check only, off unless asked).
                if (MobPlanMode)
                {
                    MobPlanBurst(frame, update);
                }

                // The run's ordinary drain, once a frame, as the lane's consumer.
                if (_traceDrainer != null)
                {
                    _traceDrainer.Drain(_traceHistory);
                }

                string picture = null;
                foreach (Accepted a in _accepted)
                {
                    if (!_world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op))
                    {
                        continue;
                    }

                    if (a.publishedFrame < 0 && op.state != LogicalCutOperationState.Admitted)
                    {
                        a.publishedFrame = frame;
                        Log(a.name + " Final/Logical published: state=" + op.state + " frame=" + frame
                            + " children=" + op.positive.value + "," + op.negative.value + Clocks(update));
                        picture ??= a.name + "-2-final";
                    }

                    if (a.committedFrame < 0 && _world.Geometry.StageOf(a.operation) == CutGeometryStage.Committed)
                    {
                        a.committedFrame = frame;
                        Log(a.name + " geometry committed: frame=" + frame + " ledger=" + op.state + Clocks(update));
                        picture ??= a.name + "-3-geometry";
                    }
                }

                if (multiNpc || MobPlanMode)
                {
                    MultiFrame(frame);
                }

                if (MobPlanMode)
                {
                    MobPlanFrame(frame);
                }

                // The building E2E follows Slashes and cuts as the multi-NPC mode does, then reads every piece.
                if (building)
                {
                    MultiFrame(frame);
                    BuildingFrame(frame);
                }

                if (picture != null)
                {
                    RequestPicture(picture);
                }
            }

            // Whether the detector's hits are a list not read before. The detector keeps the last Evaluate's hits until it
            // evaluates again, and it evaluates only when the recorder gives the core a sample: a replayed row, or a tick
            // once the replay has been fed through. A frame with neither (the frame after the last row, before the ticks
            // begin) still shows the list already read. Every hit of one Evaluate carries the input time of that core
            // update (SlashHitConfirmed.At), and each update's time is later than the one before: the recorder refuses a
            // row whose time does not advance, and a tick adds the frame's unscaled delta, which the ending judges was
            // positive on every replay frame. So the input time names the evaluation, and a list with the time of the
            // list last read is the same evaluation. Nothing is dropped per operation or per fragment: hits of another
            // evaluation are read whatever they repeat.
            private bool HitListIsNew()
            {
                return _detector != null && _detector.HitCount > 0 && _detector.HitAt(0).At != _lastHitListAt;
            }

            private void LogHit(in SlashHitConfirmed hit, int frame, int update)
            {
                _hitsLogged++;
                LogicalCutLedger ledger = _world.Ledger;
                bool hasOrigin = ledger.TryGetOrigin(hit.Fragment, out CutOperationId origin, out float originSide);
                string lineage = "root";
                bool childOfPublished = false;
                if (hasOrigin && ledger.TryGetOperation(origin, out LogicalCutOperation made))
                {
                    CutGeometryStage stage = _world.Geometry.StageOf(origin);
                    childOfPublished = made.state != LogicalCutOperationState.Admitted;
                    lineage = "child of op " + origin.value + " (side " + originSide + ", op " + made.state
                        + ", its geometry " + stage + ", source fragment " + made.source.value + ")";
                }

                // The same Slash must not have confirmed this fragment or any ancestor of it before.
                bool again = false;
                for (LogicalFragmentId at = hit.Fragment; at.IsSet;)
                {
                    foreach ((long slash, LogicalFragmentId fragment) in _confirmed)
                    {
                        again |= slash == hit.SlashId && fragment == at;
                    }

                    if (!ledger.TryGetOrigin(at, out CutOperationId up, out _) || !ledger.TryGetOperation(up, out LogicalCutOperation upOp))
                    {
                        break;
                    }

                    at = upOp.source;
                }

                Expect(!again, "slash " + hit.SlashId + " confirmed fragment " + hit.Fragment.value
                    + " whose lineage it had not confirmed before");
                _confirmed.Add((hit.SlashId, hit.Fragment));

                string of = LineageOf(hit.Fragment);
                _hitFrames.Add((Time.frameCount, of + " slash " + hit.SlashId + " " + hit.Acceptance));
                string pose = "";
                if (of == "npc" && _npcPose != null)
                {
                    bool latched = _latchPose.TryGetValue(hit.SlashId, out (int frame, double source) atLatch);
                    pose = " npcPose=(frame " + _npcPose.AppliedFrame + ", source " + _npcPose.AppliedSourceTime.ToString("F4", Inv)
                        + ") atLatch=(" + (latched ? "frame " + atLatch.frame + ", source " + atLatch.source.ToString("F4", Inv) : "not seen") + ")";
                }

                // The MobPlan mode pictures only chosen root hits (the first few and the reused slots').
                if (!MobPlanMode || MobPlanWantsPicture(hit))
                {
                    HitLogged?.Invoke(of, frame);
                }
                if (_firstHitFrame < 0)
                {
                    _firstHitFrame = frame;
                }

                Log("hit of=" + of + " slashId=" + hit.SlashId + " fragment=" + hit.Fragment.value + " side=" + hit.Side + pose
                    + " atLatch=" + hit.AtLatch + " acceptance=" + hit.Acceptance + " admission=" + hit.Admission
                    + " operation=" + hit.Operation.value + " lineage=" + lineage + " frame=" + frame
                    + " sweepAt=" + hit.At.ToString("F6", Inv) + Clocks(update));

                bool bad = hit.Acceptance == ProvisionalCutAcceptance.InvalidRequest
                    || hit.Acceptance == ProvisionalCutAcceptance.Aborted || hit.Acceptance == ProvisionalCutAcceptance.Stale;
                Expect(!bad, "hit " + _hitsLogged + " is an ordinary outcome of acceptance (" + hit.Acceptance + ")");
                if (multiNpc || building || MobPlanMode)
                {
                    MultiOnHit(hit, frame, hasOrigin, update);
                }

                if (MobPlanMode)
                {
                    MobPlanOnHit(hit, frame);
                }

                if (building)
                {
                    BuildingOnHit(hit);
                }

                // The multi-NPC mode and the building E2E follow a Pending cut as they follow a Published one.
                bool pendingCut = (multiNpc || building || MobPlanMode) && hit.Acceptance == ProvisionalCutAcceptance.Pending;
                if (hit.Acceptance != ProvisionalCutAcceptance.Published && !pendingCut)
                {
                    return;
                }

                bool child = hasOrigin;
                if (child)
                {
                    _childCuts++;
                    Expect(childOfPublished, "the child cut by slash " + hit.SlashId + " had been Final/Logical published");
                }
                else
                {
                    _rootCuts++;
                }

                if (of == "npc")
                {
                    if (child) _npcChildCuts++; else _npcRootCuts++;
                    if (!child && _npcRootAcceptedFrame < 0) _npcRootAcceptedFrame = frame;
                }
                else if (of == "box")
                {
                    if (child) _boxChildCuts++; else _boxRootCuts++;
                }

                var accepted = new Accepted
                {
                    slash = hit.SlashId,
                    fragment = hit.Fragment,
                    operation = hit.Operation,
                    child = child,
                    acceptedFrame = frame,
                    name = "op" + hit.Operation.value + "-" + of + (child ? "-child" : "-root"),
                    pending = pendingCut,
                    provisionalFrame = pendingCut ? -1 : frame,
                    fusion = pendingCut && _world.Fusion != null && _world.Owners.TryGet(hit.Fragment, out PhysicsFragmentOwner fusedOwner) && fusedOwner.IsFused,
                    hull = pendingCut && _world.Hulls != null && hit.Operation.value == 0 && hit.Admission == LogicalCutAdmission.NoOp,
                };
                _accepted.Add(accepted);
                if (_world.Ledger.TryGetOperation(hit.Operation, out LogicalCutOperation cut))
                {
                    // The adopted plane in its source's frame, as the ledger holds it: enough to repeat this cut.
                    Log(accepted.name + " adopted plane=(" + cut.plane.x.ToString("R", Inv) + "," + cut.plane.y.ToString("R", Inv) + ","
                        + cut.plane.z.ToString("R", Inv) + "," + cut.plane.w.ToString("R", Inv) + ") source=" + cut.source.value);
                }

                RequestPicture(accepted.name + "-1-provisional");
            }

            /// <summary>Every accepted cut outside the fusion has its geometry committed, and the fusion (when there is one) has nothing waiting.</summary>
            private bool CutsSettled()
            {
                return _accepted.TrueForAll(a => a.fusion || a.hull || a.committedFrame >= 0) && (_world == null || _world.Fusion == null || _world.Fusion.IsSettled) && (_world == null || _world.Hulls == null || _world.Hulls.IsSettled);
            }

            /// <summary>What a wait for the cuts is still waiting for: the accepted cuts outside the fusion not committed (the first few, by their stage), the fusion's own, the replay and the waves.</summary>
            private string DescribeWaiting()
            {
                var open = _accepted.Where(a => !a.fusion && !a.hull && a.committedFrame < 0).ToList();
                var line = new StringBuilder();
                line.Append("accepted cuts outside the fusion not committed ").Append(open.Count);
                foreach (Accepted a in open.Take(5))
                {
                    string state = _world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op) ? op.state.ToString() : "no operation";
                    line.Append(" [").Append(a.name).Append(": ledger ").Append(state).Append(", geometry ").Append(_world.Geometry != null ? _world.Geometry.StageOf(a.operation).ToString() : "none").Append(']');
                }

                line.Append("; fusion: ").Append(_world.Fusion != null ? _world.Fusion.DescribeUnsettled() : "off");
                line.Append("; hulls: ").Append(_world.Hulls != null ? _world.Hulls.DescribeUnsettled() : "off");
                line.Append("; replay ").Append(_recorder.IsReplaying ? "going" : "ended").Append(", waves flying ").Append(_katana.WaveCount);
                return line.ToString();
            }

            // The three clocks of this frame's update: the input row and its recorded time, the replay time the core
            // was given, the real time since the replay began, and the physics clock.
            private string Clocks(int update)
            {
                if (update < 0 || update >= _rowTimes.Count)
                {
                    return " update=" + update;
                }

                double recorded = _rowTimes[update] - _rowTimes[0];
                double real = Time.unscaledTimeAsDouble - _clockStart;
                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                return " update=" + update + " row=" + _rowOf[update] + " recordedOffset=" + recorded.ToString("F4", Inv)
                    + " replayTime=" + (_clockStart + recorded).ToString("F4", Inv) + " realOffset=" + real.ToString("F4", Inv)
                    + " realMinusRecorded=" + (real - recorded).ToString("F4", Inv)
                    + " physicsSeconds=" + (clock != null ? clock.PhysicsSeconds.ToString("F4", Inv) : "none")
                    + " unsimulated=" + (clock != null ? clock.UnsimulatedSeconds.ToString("F4", Inv) : "none")
                    + " stepId=" + (clock != null ? clock.StepId : -1);
            }

            private void BeginTrace()
            {
                var profile = new TraceLaneSetProfile(
                    TraceMaxPayload, 64, 1024, 16,
                    new[]
                    {
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(TraceEventType.SlashHitConfirmed), 4096, 64),
                    });
                _traceLanes = new TraceLaneSet(profile);
                _traceHistory = new TracePagedHistory(profile);
                _traceDrainer = new TraceLaneDrainer(_traceLanes);
                _detector.AttachTrace(_traceLanes.CreateWriter(0));
                Log("trace: one main-thread lane (SlashHitConfirmed), history 16 x 1024 bytes, drained every frame");
            }

            // The detector lets go of the lane first, then the run is sealed, saved and read back against what was seen.
            private void FinishTrace()
            {
                _detector.AttachTrace(default);
                string path = Path.Combine(directory, "slash-hits.ztrace");
                long laneDrops = _traceLanes.DropCountOf(0);
                TracePagedRunResult result = TracePagedRunResult.Finish(_traceDrainer, _traceHistory);
                long bytes = TracePagedHistoryFileStore.SaveAtomic(path, result);
                _traceHistory.Dispose();
                _traceLanes.Dispose();
                _traceHistory = null;
                _traceLanes = null;
                _traceDrainer = null;

                var read = new ReadBack();
                TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(path, TraceMaxPayload, read);
                Log("trace saved: " + path + " bytes=" + bytes + " integrity=" + summary.Integrity
                    + " committed=" + summary.CommittedRecordCount + " laneDrops=" + summary.LaneDropCount
                    + " (lane counted " + laneDrops + ") historyDrops=" + summary.HistoryDropCount
                    + " records=" + read.Hits.Count + " undecodable=" + read.Undecodable + " otherKinds=" + read.OtherKinds);
                for (int i = 0; i < read.Hits.Count; i++)
                {
                    SlashHitConfirmed r = read.Hits[i];
                    Log("trace record " + i + ": slashId=" + r.SlashId + " fragment=" + r.Fragment.value + " side=" + r.Side
                        + " at=" + r.At.ToString("F6", Inv) + " latch=" + r.AtLatch + " acceptance=" + r.Acceptance
                        + " admission=" + r.Admission + " operation=" + r.Operation.value);
                }

                Expect(summary.Integrity == TraceIntegrityState.Complete && summary.LaneDropCount == 0 && summary.HistoryDropCount == 0,
                    "the trace is complete: nothing dropped by the lane or the history");
                bool same = read.Undecodable == 0 && read.OtherKinds == 0 && read.Hits.Count == _seenHits.Count;
                for (int i = 0; same && i < _seenHits.Count; i++)
                {
                    SlashHitConfirmed a = _seenHits[i];
                    SlashHitConfirmed b = read.Hits[i];
                    same = a.SlashId == b.SlashId && a.Fragment == b.Fragment && a.Operation.Equals(b.Operation)
                        && a.At.Equals(b.At) && a.Acceptance == b.Acceptance && a.Admission == b.Admission
                        && a.AtLatch == b.AtLatch && a.Side == b.Side;
                }

                Expect(same, "every record read back is a hit this check saw, in order: SlashId, fragment, operation, time, acceptance ("
                    + read.Hits.Count + " records, " + _seenHits.Count + " hits)");
            }

            private sealed unsafe class ReadBack : ITraceRecordDestination
            {
                internal readonly List<SlashHitConfirmed> Hits = new List<SlashHitConfirmed>();
                internal int Undecodable;
                internal int OtherKinds;

                public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
                {
                    if (recordKind != TraceEventType.SlashHitConfirmed)
                    {
                        OtherKinds++;
                        return;
                    }

                    if (SlashHitConfirmedTraceRecord.TryRead(new ReadOnlySpan<byte>(payload, payloadLength), out SlashHitConfirmed hit))
                    {
                        Hits.Add(hit);
                    }
                    else
                    {
                        Undecodable++;
                    }
                }
            }

            // The markers' frames: the Pose Table's while the character played, and the hit detector's on the frames a
            // hit was passed on against those it was not.
            private void SummariseMarkers()
            {
                var evaluate = new List<long>();
                var apply = new List<long>();
                var quiet = new List<long>();
                var hitFrames = new HashSet<int>();
                foreach ((int frame, string _) in _hitFrames) hitFrames.Add(frame);
                foreach (KeyValuePair<int, (long evaluate, long apply, long hit)> at in _markerNs)
                {
                    if (at.Value.evaluate > 0) evaluate.Add(at.Value.evaluate);
                    if (at.Value.apply > 0) apply.Add(at.Value.apply);
                    if (!hitFrames.Contains(at.Key)) quiet.Add(at.Value.hit);
                }

                PoseTable table = _npcPose != null ? _npcPose.Table : null;
                Log("profiler PoseTable: bindSeconds=" + (_npcPose != null ? _npcPose.BindSeconds.ToString("F6", Inv) : "none")
                    + " tableBytes=" + (table != null ? (long)table.SampleCount * table.BoneCount * 28 : 0)
                    + " (samples " + (table != null ? table.SampleCount : 0) + " x bones " + (table != null ? table.BoneCount : 0) + " x 28 B)"
                    + " evaluateUs " + Spread(evaluate) + " applyUs " + Spread(apply));
                Log("profiler SlashHit.Evaluate: framesWithoutHit " + Spread(quiet));
                foreach ((int frame, string what) in _hitFrames)
                {
                    Log("profiler SlashHit.Evaluate on hit frame " + frame + " (" + what + "): "
                        + (_markerNs.TryGetValue(frame, out (long evaluate, long apply, long hit) v) ? (v.hit / 1000.0).ToString("F1", Inv) + " us" : "not recorded"));
                }
            }

            private static string Spread(List<long> ns)
            {
                if (ns.Count == 0) return "n=0";
                ns.Sort();
                double sum = 0;
                foreach (long v in ns) sum += v;
                return "n=" + ns.Count + " median=" + (ns[ns.Count / 2] / 1000.0).ToString("F1", Inv) + " p95=" + (ns[(int)(ns.Count * 0.95)] / 1000.0).ToString("F1", Inv)
                    + " max=" + (ns[ns.Count - 1] / 1000.0).ToString("F1", Inv) + " mean=" + (sum / ns.Count / 1000.0).ToString("F1", Inv);
            }

            // Which lineage a fragment is of: the character's (its identified fragment) or the box's, by its origins.
            private string LineageOf(LogicalFragmentId fragment)
            {
                LogicalFragmentId at = fragment;
                while (_world.Ledger.TryGetOrigin(at, out CutOperationId up, out _) && _world.Ledger.TryGetOperation(up, out LogicalCutOperation made))
                {
                    at = made.source;
                }

                if (multiNpc)
                {
                    return MultiLineageOf(at);
                }

                if (MobPlanMode)
                {
                    return MobPlanLineageOf(at);
                }

                if (building)
                {
                    return BuildingLineageOf(at);
                }

                if (_npc != null && _npc.Handle != null && _npc.Handle.Source.IsSet && at == _npc.Handle.Source)
                {
                    return "npc";
                }

                foreach (SandboxNpcCharacter c in _npcs)
                {
                    if (c != _npc && c.Handle != null && c.Handle.Source.IsSet && at == c.Handle.Source)
                    {
                        return "crowd";
                    }
                }

                return at == _probe.Body ? "box" : "other";
            }

            // What the character's hierarchy holds when it becomes a target: what a withdrawal of it has to stop.
            private void LogCharacterInventory()
            {
                if (_npc == null || _npc.CharacterRoot == null)
                {
                    return;
                }

                var byType = new SortedDictionary<string, int>();
                var live = new List<string>();
                var onDisable = new SortedSet<string>();
                foreach (Component component in _npc.CharacterRoot.GetComponentsInChildren<Component>(true))
                {
                    if (component == null)
                    {
                        continue;
                    }

                    string type = component.GetType().Name;
                    byType.TryGetValue(type, out int n);
                    byType[type] = n + 1;
                    bool active = component.gameObject.activeInHierarchy;
                    if (component is Renderer r && r.enabled && active)
                    {
                        live.Add("renderer " + r.name + " (" + type + ")");
                    }
                    else if (component is Behaviour b && b.enabled && active && !(component is Renderer))
                    {
                        live.Add("behaviour " + b.name + " (" + type + ")");
                    }
                    else if (component is Rigidbody body)
                    {
                        live.Add("rigidbody " + body.name + " kinematic=" + body.isKinematic + " detectCollisions=" + body.detectCollisions
                            + " colliders=" + body.GetComponentsInChildren<Collider>(true).Length + " active=" + active);
                    }
                    else if (component is Collider c)
                    {
                        live.Add("collider " + c.name + " (" + type + ") enabled=" + c.enabled + " active=" + active);
                    }
                    else if (component is Joint j)
                    {
                        live.Add("joint " + j.name + " (" + type + ")");
                    }

                    if (component is MonoBehaviour && component.GetType().GetMethod("OnDisable",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) != null)
                    {
                        onDisable.Add(type);
                    }
                }

                var text = new StringBuilder("npc hierarchy: ");
                foreach (KeyValuePair<string, int> entry in byType)
                {
                    text.Append(entry.Key).Append('=').Append(entry.Value).Append(' ');
                }

                Log(text.ToString());
                Log("npc hierarchy live parts: " + string.Join("; ", live));
                Log("npc hierarchy MonoBehaviours with OnDisable: " + (onDisable.Count > 0 ? string.Join(", ", onDisable) : "none"));
            }

            private void WriteEnvironment()
            {
                Transform space = GameObject.Find("XR Origin")?.transform.Find("Camera Offset");
                int katanaColliders = _katana.Katana != null ? _katana.Katana.GetComponentsInChildren<Collider>(true).Length : -1;
                GameObject origin = GameObject.Find("XR Origin");
                int originColliders = origin != null ? origin.GetComponentsInChildren<Collider>(true).Length : -1;
                int player = LayerMask.NameToLayer("Player");
                bool ignores = player >= 0 && Physics.GetIgnoreLayerCollision(player, 0);
                Log("environment: xr deviceActive=" + UnityEngine.XR.XRSettings.isDeviceActive
                    + " trackingSpace=" + (space != null ? space.position.ToString("F4") + " " + space.rotation.eulerAngles.ToString("F2") : "none")
                    + " katanaColliders=" + katanaColliders + " xrOriginColliders=" + originColliders
                    + " playerLayer=" + player + " playerIgnoresDefault=" + ignores
                    + " simulationMode=" + Physics.simulationMode);
                Expect(katanaColliders == 0, "the katana carries no collider");
                Expect(ignores, "the Player layer does not collide with the default layer the box and the floor are on");
            }

            /// <summary>
            /// Runs one part of the check's summaries so that its failure is logged and counted as a failure of the
            /// check, but stops nothing else: the later parts, the saving of what is already gathered, the ending and
            /// the reclaim go on. Used by the ending and by the quitting path alike.
            /// </summary>
            private void Guarded(string what, Action part)
            {
                if (!CheckEnding.TryRun(what, part, Log))
                {
                    _failures++;
                }
            }

            private void Summarise()
            {
                double real = Time.unscaledTimeAsDouble - _clockStart;
                double recorded = _rowTimes.Count > 0 ? _rowTimes[_rowTimes.Count - 1] - _rowTimes[0] : 0.0;
                Log("summary: fed=" + _recorder.ReplayIndex + "/" + _loaded + " recordedSpan=" + recorded.ToString("F4", Inv)
                    + " realSinceStart=" + real.ToString("F4", Inv) + " hits=" + _hitsLogged + " rootCuts=" + _rootCuts
                    + " childCuts=" + _childCuts + " geometryFaults=" + _world.GeometryFaults
                    + " terminationRequested=" + _world.TerminationRequested + " pictures=[" + string.Join(",", _pictures) + "]");
                foreach (Accepted a in _accepted)
                {
                    Log(a.name + ": slash=" + a.slash + " fragment=" + a.fragment.value + " accepted@" + a.acceptedFrame
                        + " final@" + a.publishedFrame + " geometry@" + a.committedFrame);
                }

                Guarded("markers", SummariseMarkers);
                Log("summary by lineage: boxRoot=" + _boxRootCuts + " boxChild=" + _boxChildCuts + " npcRoot=" + _npcRootCuts
                    + " npcChild=" + _npcChildCuts + " npcWithdrawnFrame=" + _npcWithdrawnFrame + " npcRootAcceptedFrame=" + _npcRootAcceptedFrame);
                if (multiNpc)
                {
                    Guarded("multi summary", MultiSummarise);
                }

                if (building)
                {
                    Guarded("building summary", BuildingSummarise);
                }

                if (MobPlanMode)
                {
                    Guarded("mobplan summary", MobPlanSummarise);
                }

                if (!multiNpc && !building && !MobPlanMode)
                {
                    Expect(_boxRootCuts >= 1, "the box was cut by a real hit");
                    Expect(_boxChildCuts >= 1, "a surviving child of the box was cut by a real hit of another Slash");
                }

                if (_npc != null && !multiNpc && !MobPlanMode)
                {
                    Expect(_npcRootCuts == 1, "the walking character was cut once, from the pose its hit met");
                    Expect(_npcChildCuts >= 1, "a surviving child of the character was cut by a real hit of another Slash");
                    Expect(_npcWithdrawnFrame == _npcRootAcceptedFrame,
                        "the character stopped being drawn and being a hit target in the frame its cut was published");
                    Expect(_npcPose == null || (_npcPoseFrameAtWithdrawal >= 0 && _npcPose.AppliedFrame == _npcPoseFrameAtWithdrawal),
                        "its pose was not applied again after it left (last at frame " + (_npcPose != null ? _npcPose.AppliedFrame : -1) + ")");
                    Expect(!MotionInScene(), "its motion body takes no part in the physics after it left");
                }
                if (_world.Fusion == null && _world.Hulls == null)
                {
                    Expect(_accepted.TrueForAll(a => a.publishedFrame >= 0 && a.committedFrame >= 0),
                        "every accepted cut was published and its geometry committed");
                }
                else if (_world.Hulls != null)
                {
                    Log("the hull trial is on: a hull cut publishes no operation to this check, so the accepted cuts' publication and commit are judged in the hull summary");
                }
                else
                {
                    Log("the fusion is on: a group cut publishes no operation to this check (its members' operations are the fusion's), so the accepted cuts' publication and commit are not judged here; the fusion summary counts its Finals");
                }

                Expect(_world.GeometryFaults == 0 && !_world.TerminationRequested, "no geometry fault and no termination");
                Guarded("cooking audit", CookingAuditSummary);

                // Where the pieces rest: every live fragment's owner, above the floor.
                for (int id = 1; id < 256; id++)
                {
                    var fragment = new LogicalFragmentId(id);
                    if (!_world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
                    {
                        break;
                    }

                    if (state != LogicalFragmentState.Live || !_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner)
                        || owner.IsWithdrawn)
                    {
                        continue;
                    }

                    // The lowest vertex of the convexes the piece is made of, where it stands now: its own points, not
                    // a box around them, which a tilted piece would push below the floor.
                    float lowest = LowestVertexY(owner);
                    // Where the piece is in the check's pictures: the view's screen point of its convexes' centre (y up from
                    // the picture's bottom), so a shape in a picture can be told apart by lineage.
                    Vector3 centre = PieceCentre(owner);
                    Vector3 screen = _view.WorldToScreenPoint(centre);
                    Log("piece fragment=" + id + " of=" + LineageOf(fragment) + " convexes=" + ConvexCountOf(owner) + (owner.IsDisplayOnly ? " (display-only)" : "")
                        + " centre=" + centre.ToString("F3") + " pictureXY=(" + screen.x.ToString("F0", Inv) + "," + screen.y.ToString("F0", Inv)
                        + ") depth=" + screen.z.ToString("F2", Inv) + " mass=" + owner.Mass.ToString("R", Inv)
                        + " position=" + owner.Root.transform.position.ToString("F4")
                        + " lowestVertexY=" + lowest.ToString("F4", Inv) + " velocity=" + (owner.Body != null ? owner.Body.linearVelocity.ToString("F4") : "fused")
                        + " sleeping=" + (owner.Body != null ? owner.Body.IsSleeping().ToString() : owner.IsFused && owner.Group != null ? "group kinematic " + owner.Group.Kinematic : "none"));
                    // A diagnostic, not a judgement: the piece's own B-rep at one moment, which the cooked collider and the
                    // discrete Steps may leave a little into the floor (DESIGN 7.3).
                    Log("piece " + id + " lowest own vertex at this moment " + (lowest > -0.02f ? "at or above" : "BELOW") + " floor -0.02 (diagnostic)");
                }

                if (!light)
                {
                    SummariseObservation();
                }
            }

            // One row per live piece per frame, and where each stands when the observation starts and ends.
            // Every sweep of this update, as the hit detector was given it (SlashWaveCore.SweepAt).
            private void RecordSweeps()
            {
                SlashWaveCore core = _katana != null ? _katana.Core : null;
                if (core == null)
                {
                    return;
                }

                if (_sweepRows == null)
                {
                    _sweepRows = new StreamWriter(Path.Combine(directory, "sweeps.csv"));
                    _sweepRows.WriteLine("frame,slashId,at,latch,prevAX,prevAY,prevAZ,prevBX,prevBY,prevBZ,curAX,curAY,curAZ,curBX,curBY,curBZ,travelX,travelY,travelZ,planeNX,planeNY,planeNZ,planeD");
                }

                for (int i = 0; i < core.SweepCount; i++)
                {
                    SlashSweep w = core.SweepAt(i);
                    _sweepRows.WriteLine(string.Join(",", Time.frameCount, w.SlashId, w.At.ToString("R", Inv), w.IsLatch,
                        F(w.PreviousA.x), F(w.PreviousA.y), F(w.PreviousA.z), F(w.PreviousB.x), F(w.PreviousB.y), F(w.PreviousB.z),
                        F(w.CurrentA.x), F(w.CurrentA.y), F(w.CurrentA.z), F(w.CurrentB.x), F(w.CurrentB.y), F(w.CurrentB.z),
                        F(w.TravelAxis.x), F(w.TravelAxis.y), F(w.TravelAxis.z),
                        F(w.SourceSlashPlane.normal.x), F(w.SourceSlashPlane.normal.y), F(w.SourceSlashPlane.normal.z), F(w.SourceSlashPlane.distance)));
                }
            }

            // The detector's two stages for Slash `slashId`'s sweeps against the NPC lineage's current shapes.
            private unsafe void RecordHitPairs(long slashId)
            {
                SlashWaveCore core = _katana != null ? _katana.Core : null;
                if (core == null || _world == null || _detector == null)
                {
                    return;
                }

                if (_pairRows == null)
                {
                    _pairRows = new StreamWriter(Path.Combine(directory, "hitpair.csv"));
                    _pairRows.WriteLine("frame,slashId,sweepIndex,fragment,side,convex,consumed,candidate,intersects,vertexCount");
                    _pairVertices = new StreamWriter(Path.Combine(directory, "hitpair-vertices.csv"));
                    _pairVertices.WriteLine("frame,sweepIndex,fragment,side,convex,vertex,x,y,z");
                }

                _world.Owners.CollectCurrentShapes(_pairShapes);
                for (int i = 0; i < core.SweepCount; i++)
                {
                    SlashSweep sweep = core.SweepAt(i);
                    if (sweep.SlashId != slashId)
                    {
                        continue;
                    }

                    float3 n = sweep.SourceSlashPlane.normal;
                    var worldPlane = new float4(n, sweep.SourceSlashPlane.distance);
                    foreach (CurrentShape current in _pairShapes)
                    {
                        if (current.Owner == null || current.Shape.IsFreed || LineageOf(current.Fragment) != "npc")
                        {
                            continue;
                        }

                        PhysicsOwnerShape shape = current.Shape;
                        float4x4 shapeToWorld = math.mul((float4x4)current.Owner.localToWorldMatrix, shape.LocalToOwner);
                        float4x4 worldToShape = math.inverse(shapeToWorld);
                        float4 plane = math.mul(math.transpose(shapeToWorld), worldPlane);
                        plane /= math.length(plane.xyz);
                        float3 la0 = math.transform(worldToShape, (float3)sweep.PreviousA);
                        float3 lb0 = math.transform(worldToShape, (float3)sweep.PreviousB);
                        float3 la1 = math.transform(worldToShape, (float3)sweep.CurrentA);
                        float3 lb1 = math.transform(worldToShape, (float3)sweep.CurrentB);
                        float3 qlo = math.min(math.min(la0, lb0), math.min(la1, lb1));
                        float3 qhi = math.max(math.max(la0, lb0), math.max(la1, lb1));
                        bool consumed = _detector.Consumption.IsConsumed(slashId, current.Fragment);
                        for (int k = 0; k < shape.ConvexCount; k++)
                        {
                            shape.ConvexBounds(k, out float3 lo, out float3 hi);
                            bool candidate = !(math.any(qhi < lo) || math.any(hi < qlo));
                            bool intersects = candidate && SlashSweepConvexQuery.Intersects(
                                plane, la0, lb0, la1, lb1, shape.BankOf(k), shape.Convex(k), ref _pairSection);
                            ConvexBrepRange range = shape.Convex(k);
                            _pairRows.WriteLine(string.Join(",", Time.frameCount, slashId, i, current.Fragment.value, current.Side.ToString("R", Inv), k,
                                consumed ? 1 : 0, candidate ? 1 : 0, intersects ? 1 : 0, range.vertexCount));
                            if (!candidate)
                            {
                                continue;
                            }

                            ConvexBrepBank bank = shape.BankOf(k);
                            for (int v = 0; v < range.vertexCount; v++)
                            {
                                float3 w = math.transform(shapeToWorld, bank.vertices[range.vertexBase + v]);
                                _pairVertices.WriteLine(string.Join(",", Time.frameCount, i, current.Fragment.value, current.Side.ToString("R", Inv), k, v,
                                    w.x.ToString("R", Inv), w.y.ToString("R", Inv), w.z.ToString("R", Inv)));
                            }
                        }
                    }
                }
            }

            private void TrackPieces()
            {
                if (_pieceRows == null)
                {
                    GameObject floorObject = GameObject.Find(building ? "Building Slash Floor" : "Prop Floor");
                    _floor = floorObject != null ? floorObject.GetComponent<Collider>() : null;
                    Log("floor: " + (_floor != null ? _floor.GetType().Name + " bounds min=" + _floor.bounds.min.ToString("F4") + " max=" + _floor.bounds.max.ToString("F4")
                            + " layer=" + _floor.gameObject.layer + " contactOffset=" + _floor.contactOffset.ToString("R", Inv) : "none")
                        + " defaultContactOffset=" + Physics.defaultContactOffset.ToString("R", Inv) + " solverIterations=" + Physics.defaultSolverIterations
                        + " bounceThreshold=" + Physics.bounceThreshold.ToString("R", Inv) + " gravity=" + Physics.gravity.ToString("F3"));
                    _pieceRows = new StreamWriter(Path.Combine(directory, "pieces.csv"));
                    _pieceRows.WriteLine("frame,phase,stepId,unsimulated,fragment,of,mass,posX,posY,posZ,velX,velY,velZ,speed,sleeping,kinematic,detection,"
                        + "lowestVertexY,colliders,enabledColliders,attached,colliderMinY,colliderMaxY,overFloor,floorPenetration,floorTouch,"
                        + "lowestX,lowestZ,centreX,centreY,centreZ,originOverFloor,boundsMinX,boundsMinY,boundsMinZ,boundsMaxX,boundsMaxY,boundsMaxZ");
                }

                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                long step = clock != null ? clock.StepId : -1;
                int frame = Time.frameCount;
                string phase = _observing ? "observe" : "cuts";
                for (int id = 1; id < 256; id++)
                {
                    var fragment = new LogicalFragmentId(id);
                    if (!_world.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
                    {
                        break;
                    }

                    if (state != LogicalFragmentState.Live || !_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner)
                        || owner.IsWithdrawn || owner.Body == null)
                    {
                        continue;
                    }

                    Rigidbody body = owner.Body;
                    Vector3 lowestPoint = LowestVertex(owner);
                    float lowest = lowestPoint.y;
                    Vector3 centre = PieceCentre(owner);
                    int colliders = 0, enabled = 0, attached = 0;
                    float minY = float.PositiveInfinity, maxY = float.NegativeInfinity, penetration = 0f;
                    Bounds all = default;
                    bool anyBounds = false;
                    foreach (Collider collider in owner.Root.GetComponentsInChildren<Collider>(true))
                    {
                        colliders++;
                        if (!collider.enabled || !collider.gameObject.activeInHierarchy)
                        {
                            continue;
                        }

                        enabled++;
                        attached += collider.attachedRigidbody == body ? 1 : 0;
                        Bounds b = collider.bounds;
                        if (anyBounds) all.Encapsulate(b); else { all = b; anyBounds = true; }
                        minY = Math.Min(minY, b.min.y);
                        maxY = Math.Max(maxY, b.max.y);
                        if (_floor != null && Physics.ComputePenetration(collider, collider.transform.position, collider.transform.rotation,
                                _floor, _floor.transform.position, _floor.transform.rotation, out Vector3 _, out float depth))
                        {
                            penetration = Math.Max(penetration, depth);
                        }
                    }

                    Vector3 position = body.position;
                    // Over the floor: the piece's own lowest vertex inside the floor's footprint (the body's origin is its
                    // source's, not the piece's, so it is kept only as a column).
                    bool overFloor = OverFloor(lowestPoint);
                    bool originOverFloor = OverFloor(position);
                    bool touch = _floor != null && enabled > 0 && (penetration > 0f
                        || (overFloor && minY <= _floor.bounds.max.y + 2f * Physics.defaultContactOffset));
                    Vector3 v = body.linearVelocity;
                    _pieceRows.WriteLine(string.Join(",", frame, phase, step, clock != null ? clock.UnsimulatedSeconds.ToString("F6", Inv) : "",
                        id, LineageOf(fragment), body.mass.ToString("R", Inv), F(position.x), F(position.y), F(position.z), F(v.x), F(v.y), F(v.z),
                        F(v.magnitude), body.IsSleeping(), body.isKinematic, body.collisionDetectionMode, F(lowest), colliders, enabled, attached,
                        F(minY), F(maxY), overFloor, F(penetration), touch, F(lowestPoint.x), F(lowestPoint.z), F(centre.x), F(centre.y), F(centre.z),
                        originOverFloor, F(all.min.x), F(all.min.y), F(all.min.z), F(all.max.x), F(all.max.y), F(all.max.z)));

                    if (!_tracks.TryGetValue(id, out PieceTrack track))
                    {
                        _tracks[id] = track = new PieceTrack { firstFrame = frame, firstStep = step };
                    }

                    if (_observing && track.startStep < 0)
                    {
                        track.startStep = step;
                        track.startLowest = lowest;
                    }

                    track.endStep = step;
                    track.endLowest = lowest;
                    track.endTouch = touch;
                    track.endOverFloor = overFloor;
                    if (track.underFloor == null && _floor != null && lowest < _floor.bounds.min.y)
                    {
                        track.underFloor = "frame " + frame + " step " + step + " lowestVertex=" + lowestPoint.ToString("F4");
                        track.underWhileOver = overFloor;
                    }

                    if (track.collidersOff == null && (enabled < colliders || attached < enabled || enabled == 0))
                    {
                        track.collidersOff = "frame " + frame + " step " + step + " enabled " + enabled + "/" + colliders + " attached " + attached;
                    }

                    track.end = "centre=" + centre.ToString("F4") + " lowestVertex=" + lowestPoint.ToString("F4") + " speed=" + F(v.magnitude) + " velY=" + F(v.y) + " overFloor=" + overFloor
                        + " floorPenetration=" + F(penetration) + " floorTouch=" + touch + " colliders=" + enabled + "/" + colliders + " attached=" + attached
                        + " colliderMinY=" + F(minY) + " colliderMaxY=" + F(maxY) + " sleeping=" + body.IsSleeping();
                }
            }

            private static string F(float value) => value.ToString("R", Inv);

            // What each piece did, judged on its physics over the whole run and the observation period -- not on where its
            // own B-rep stands at one moment, which the cooked collider and the discrete Steps may leave a little into the
            // floor (DESIGN 7.3). The floor is a finite slab (its footprint and its underside). The floor check is limited
            // to this scenario: it reads only the first row a piece's own lowest vertex was under the underside, and fails
            // when that vertex was inside the footprint then; outside it, the piece is written as fallen past the edge. It
            // is not a guarantee to catch every way through a floor (a piece through and back out between rows, a
            // collider apart from its own vertices), and no general contact check is made here.
            private void SummariseObservation()
            {
                _pieceRows?.Flush();
                Expect(_observedSteps >= _askedSteps && _askedSteps > 0,
                    "the ordinary Steps went on through the observation (" + _observedSteps + " of " + _askedSteps + " steps)");
                foreach (KeyValuePair<int, PieceTrack> at in _tracks)
                {
                    PieceTrack t = at.Value;
                    string outcome = t.startStep < 0 ? "cut again before the observation"
                        : t.underFloor != null ? (t.underWhileOver ? "UNDER THE FLOOR WHILE OVER IT" : "fell past the floor's edge")
                        : t.endTouch ? "on the floor"
                        : t.endOverFloor ? "above the floor, on other pieces"
                        : "off the floor's footprint, above its underside";
                    Log("observed piece " + at.Key + " (" + outcome + "): first seen frame " + t.firstFrame + " step " + t.firstStep
                        + "; observation steps " + t.startStep + ".." + t.endStep + " lowest own vertex " + F(t.startLowest) + " -> " + F(t.endLowest)
                        + (t.underFloor != null ? "; under the floor's underside first at " + t.underFloor : "")
                        + "; at the end " + t.end);
                    Expect(t.collidersOff == null, "piece " + at.Key + "'s colliders stayed on and on its body" + (t.collidersOff != null ? " (" + t.collidersOff + ")" : ""));
                    Expect(!(t.underFloor != null && t.underWhileOver), "piece " + at.Key + "'s own lowest vertex was not inside the floor's footprint when it first went under the floor (limited check)");
                }
            }

            private static Vector3 PieceCentre(PhysicsFragmentOwner owner) => PieceCentreOf(owner);   // safe for a display-only owner

            private bool OverFloor(Vector3 point)
            {
                return _floor != null && point.x >= _floor.bounds.min.x && point.x <= _floor.bounds.max.x
                    && point.z >= _floor.bounds.min.z && point.z <= _floor.bounds.max.z;
            }

            private static Vector3 LowestVertex(PhysicsFragmentOwner owner) => LowestVertexOf(owner);

            private static float LowestVertexY(PhysicsFragmentOwner owner) => LowestVertexYOf(owner);

            private void RequestPicture(string name)
            {
                if (light)
                {
                    return;
                }

                _picturesAsked++;
                _pictures.Add(name + "@" + Time.frameCount);
                StartCoroutine(Picture(name));
            }

            private IEnumerator Picture(string name)
            {
                int frame = Time.frameCount;
                yield return new WaitForEndOfFrame();
                using ProfilerMarker.AutoScope scope = s_checkPicture.Auto();
                string file = Path.Combine(directory, name + "-f" + frame + ".png");
                var picture = new Texture2D(_target.width, _target.height, TextureFormat.RGB24, false);
                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _target;
                picture.ReadPixels(new Rect(0f, 0f, _target.width, _target.height), 0, 0);
                RenderTexture.active = previous;
                picture.Apply(false);
                File.WriteAllBytes(file, picture.EncodeToPNG());
                Destroy(picture);
                Log("picture " + name + " -> " + file + " frame=" + frame);
            }

            // The characters' shadows are those of the renderers drawn with the product mesh surface; the cut pieces'
            // are the display's two casters. Only these are turned off: the city's renderers keep theirs.
            private const string SurfaceShader = "Zantetsu/VP Mesh Surface";

            private void TurnAddedShadowsOff()
            {
                int off = 0;
                foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name == SurfaceShader
                        && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    {
                        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        off++;
                    }
                }

                string how = "none";
                Zantetsu.MeshCut.VpLogicalCutDisplay display = _world != null ? _world.Display : null;
                if (display != null)
                {
                    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                    System.Reflection.FieldInfo one = typeof(Zantetsu.MeshCut.VpLogicalCutDisplay).GetField("_shadowMaterial", flags);
                    System.Reflection.FieldInfo two = typeof(Zantetsu.MeshCut.VpLogicalCutDisplay).GetField("_provisionalShadowMaterial", flags);
                    Material oneMaterial = one?.GetValue(display) as Material;
                    Material twoMaterial = two?.GetValue(display) as Material;
                    try
                    {
                        one?.SetValue(display, null);
                        two?.SetValue(display, null);
                    }
                    catch (Exception)
                    {
                    }

                    if (one != null && one.GetValue(display) == null && two != null && two.GetValue(display) == null)
                    {
                        how = "casters cleared";
                    }
                    else
                    {
                        // Where a field cannot be written, the casters' pass is switched off instead: the calls remain.
                        oneMaterial?.SetShaderPassEnabled("ShadowCaster", false);
                        twoMaterial?.SetShaderPassEnabled("ShadowCaster", false);
                        how = "caster pass disabled (calls still issued)";
                    }
                }

                Log("added shadows OFF: " + off + " surface renderers set to cast no shadow; cut display: " + how);
            }

            private int CountSurfaceCasters()
            {
                int casting = 0;
                foreach (Renderer r in FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (r.sharedMaterial != null && r.sharedMaterial.shader != null && r.sharedMaterial.shader.name == SurfaceShader
                        && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off)
                    {
                        casting++;
                    }
                }

                return casting;
            }

            private string DisplayCasters()
            {
                Zantetsu.MeshCut.VpLogicalCutDisplay display = _world != null ? _world.Display : null;
                if (display == null)
                {
                    return "no display";
                }

                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                object one = typeof(Zantetsu.MeshCut.VpLogicalCutDisplay).GetField("_shadowMaterial", flags)?.GetValue(display);
                object two = typeof(Zantetsu.MeshCut.VpLogicalCutDisplay).GetField("_provisionalShadowMaterial", flags)?.GetValue(display);
                return (one != null ? "stable set" : "stable none") + ", " + (two != null ? "immediate set" : "immediate none")
                    + "; shadow calls so far one-sided=" + display.OneSidedShadowIssues + " two-sided=" + display.TwoSidedShadowIssues;
            }

            private void Expect(bool held, string what)
            {
                if (!held) _failures++;
                Log((held ? "ok: " : "FAILED: ") + what);
            }
        }
    }
}
