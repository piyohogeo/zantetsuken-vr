using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Input;
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
    /// **Trace** (<see cref="TraceArgument"/>): this check composes one trace run of its own -- one main-thread lane
    /// carrying <see cref="TraceEventType.SlashHitConfirmed"/>, a paged history, drained every frame -- and gives the
    /// lane to the hit detector. At the end the lane is taken back, the run is finished and saved, read back, and every
    /// record is compared with the hits this check saw. Without the argument nothing of it exists, and the hits, cuts
    /// and pictures are the same path.
    /// </para>
    /// </summary>
    public static class SandboxPropSlashPlayerCheck
    {
        public const string Argument = "-zantetsuPropSlash";
        public const string DebugColoursArgument = "-zantetsuPropDebugColours";
        public const string FrameRateArgument = "-zantetsuPropFrameRate";
        public const string TraceArgument = "-zantetsuPropTrace";
        public const string Prefix = "PROP SLASH: ";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            string directory = Value(Argument);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var host = new GameObject("Prop slash player check");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Walk walk = host.AddComponent<Walk>();
            walk.directory = directory;
            walk.input = Value(SandboxSlashReplayPlayerCheck.InputArgument);
            walk.start = int.TryParse(Value(SandboxSlashReplayPlayerCheck.StartArgument), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int s) ? s : 1336;
            walk.debugColours = Has(DebugColoursArgument);
            walk.trace = Has(TraceArgument);
            walk.frameRate = int.TryParse(Value(FrameRateArgument), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out int rate) ? rate : 90;
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

        private static void Log(string line)
        {
            Debug.Log(Prefix + line);
        }

        // After the driver's late update (-100), so what it reads is what this frame will draw: nothing of a cut's
        // state changes after the late updates.
        [DefaultExecutionOrder(400)]
        private sealed class Walk : MonoBehaviour
        {
            internal string directory;
            internal string input;
            internal int start;
            internal bool debugColours;
            internal bool trace;
            internal int frameRate;

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
            }

            private IEnumerator Start()
            {
                Directory.CreateDirectory(directory);
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
                    || _probe == null || !_probe.Body.IsSet || _view == null || string.IsNullOrEmpty(input) || !File.Exists(input))
                {
                    Log("FAILED: the scene is not wired (world, katana, recorder, detector, box, view) or no input: " + input);
                    Application.Quit(13);
                    yield break;
                }

                // The fixed preparation: the frame rate asked for, the colours, the check's own view. No waiting.
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = frameRate;
                VpCutSurfaceColour.SetDebugEnabled(debugColours);
                _target = new RenderTexture(960, 600, 24);
                _target.Create();
                _view.targetTexture = _target;
                _view.enabled = true;

                WriteEnvironment();
                Log("separation impulse given to every accepted cut (box and character): "
                    + (hit != null ? hit.SeparationImpulse.ToString("R", Inv) : "none") + " N·s");
                _npc = FindAnyObjectByType<SandboxNpcCharacter>();
                if (_npc != null)
                {
                    // The character's loading frames: its cold preparation finishes, then it is a target. Bounded; a
                    // character that does not become one ends the check.
                    int until = Time.frameCount + 600;
                    while (!_npc.IsTarget && _npc.Failure == null && Time.frameCount < until)
                    {
                        yield return null;
                    }

                    _npcPose = _npc.CharacterRoot != null ? _npc.CharacterRoot.GetComponent<PoseTablePlayer>() : null;
                    PoseTable table = _npcPose != null ? _npcPose.Table : null;
                    Log("npc: target=" + _npc.IsTarget + " failure=" + (_npc.Failure ?? "none") + " preparationFrames=" + _npc.PreparationFrames
                        + " hulls=" + _npc.HullCount + " renderer=" + (_npc.Renderer != null ? _npc.Renderer.name : "none")
                        + " table=" + (table != null ? table.ClipName + " (" + table.ClipId + ") duration=" + table.DurationSeconds.ToString("R", Inv)
                            + " rate=" + table.SampleRate.ToString("R", Inv) + " samples=" + table.SampleCount + " looping=" + table.IsLooping
                            + " bones=" + table.BoneCount + " resolved=" + _npcPose.ResolvedBoneCount + " unresolved=" + _npcPose.UnresolvedBoneCount
                            + " requiresBones=" + _npcPose.RequiresBones + " bonesConfirmed=" + _npcPose.BonesConfirmed : "none (" + (_npcPose != null ? _npcPose.BindError : "no player") + ")")
                        + " root=" + (_npc.CharacterRoot != null ? _npc.CharacterRoot.transform.position.ToString("F4") : "none"));
                    if (!_npc.IsTarget || table == null)
                    {
                        Log("FAILED: the character did not become a hit target with a Pose Table");
                        Application.Quit(15);
                        yield break;
                    }
                }

                if (trace)
                {
                    BeginTrace();
                }

                string[] lines = File.ReadAllLines(input);
                _recorder.BeginRecording();
                for (int i = start + 1; i < lines.Length && _loaded < SandboxSlashPoseRecorder.Capacity; i++, _loaded++)
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
                _clockStart = Time.unscaledTimeAsDouble;
                _replaying = _recorder.TryBeginReplay(_clockStart);
                _npcPose?.Restart();
                _poseEvaluateNs = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.PoseTable.Evaluate");
                _poseApplyNs = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.PoseTable.Apply");
                _hitEvaluateNs = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.SlashHit.Evaluate");
                Log("start: frame=" + Time.frameCount + " rows " + start + ".." + (start + _loaded - 1) + " replayed=" + _rowTimes.Count
                    + " refusedByRecorder=[" + string.Join(",", _refusedRows) + "]"
                    + " replay begun=" + _replaying + " clock start=" + _clockStart.ToString("F4", Inv)
                    + " (a row's replay time = clock start + its recorded time - " + _rowTimes[0].ToString("R", Inv) + ")"
                    + " physicsHz=" + (clock != null ? clock.FrequencyHz : 0)
                    + " unsimulatedAtStart=" + (clock != null ? clock.UnsimulatedSeconds.ToString("F6", Inv) : "none")
                    + " stepId=" + (clock != null ? clock.StepId : -1)
                    + " box=" + _probe.Actor.transform.position.ToString("F4") + " targetFrameRate=" + frameRate
                    + " debugColours=" + debugColours);
                RequestPicture("0-start");

                float deadline = Time.realtimeSinceStartup + 120f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                    bool fedThrough = !_recorder.IsReplaying && _recorder.ReplayIndex >= _rowTimes.Count;
                    // Every accepted cut's geometry committed: the end of the cuts, not a physical rest of the pieces.
                    bool allGeometryCommitted = _accepted.TrueForAll(a => a.committedFrame >= 0);
                    if (fedThrough && _katana.WaveCount == 0 && allGeometryCommitted && _accepted.Count > 0)
                    {
                        break;
                    }
                }

                _replaying = false;
                ManualPhysicsClock stepClock = CutPhysicsStep.Clock;
                Log("cuts complete (every accepted cut's geometry committed, replay fed through, no wave flying): frame=" + Time.frameCount
                    + " realSinceStart=" + (Time.unscaledTimeAsDouble - _clockStart).ToString("F4", Inv)
                    + " stepId=" + (stepClock != null ? stepClock.StepId : -1));
                RequestPicture("8-cuts-complete");

                // The observation period, apart from the cuts' completion: the ordinary Steps go on for a fixed physics
                // time, nothing is simulated, synchronised or placed by the check, and every piece is followed.
                _observing = true;
                long observeFrom = stepClock != null ? stepClock.StepId : 0;
                long observeSteps = stepClock != null ? (long)Math.Round(ObservationPhysicsSeconds * stepClock.FrequencyHz) : 0;
                int observeFrame = Time.frameCount;
                float observeBy = Time.realtimeSinceStartup + 10f;
                while (stepClock != null && stepClock.StepId < observeFrom + observeSteps && Time.realtimeSinceStartup < observeBy)
                {
                    yield return null;
                }

                _observedSteps = (stepClock != null ? stepClock.StepId : 0) - observeFrom;
                _askedSteps = observeSteps;
                Log("observation: frames " + observeFrame + ".." + Time.frameCount + " steps " + observeFrom + ".." + (stepClock != null ? stepClock.StepId : -1)
                    + " (asked " + observeSteps + " steps = " + ObservationPhysicsSeconds.ToString("R", Inv) + " s of physics; real cap 10 s)");
                yield return null;
                _observing = false;
                RequestPicture("9-end");
                yield return null;
                yield return null;
                Summarise();
                if (trace)
                {
                    FinishTrace();
                }

                // The ordinary ending, carried by the ordinary frames.
                float endBy = Time.realtimeSinceStartup + 15f;
                _world.Shutdown();
                while (!_world.IsReleased && Time.realtimeSinceStartup < endBy)
                {
                    yield return null;
                    _world.Shutdown();
                }

                Expect(_world.IsReleased, "the world ended the ordinary way and gave everything back");
                int code = _failures == 0 ? 0 : 14;
                Log("finished with code " + code);
                yield return null;
                Application.Quit(code);
            }

            // A run left behind by a check that ended early goes back with it.
            private void OnDestroy()
            {
                _pieceRows?.Dispose();
                _poseEvaluateNs.Dispose();
                _poseApplyNs.Dispose();
                _hitEvaluateNs.Dispose();
                _traceHistory?.Dispose();
                _traceLanes?.Dispose();
            }

            private void LateUpdate()
            {
                if ((_replaying || _observing) && _world != null && !_world.IsEnding)
                {
                    TrackPieces();
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

                // Any piece moving faster than 20 m/s, the first time it does: which, how heavy, where.
                for (int id = 1; id < 256; id++)
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

                if (_npc != null && _npcWithdrawnFrame < 0 && _npc.CharacterRoot != null && !_npc.CharacterRoot.activeInHierarchy)
                {
                    _npcWithdrawnFrame = frame;
                    Log("npc root withdrawn: frame=" + frame + " (its first cut accepted at frame " + _npcRootAcceptedFrame + ")");
                }

                for (int i = 0; i < _detector.HitCount; i++)
                {
                    SlashHitConfirmed hit = _detector.HitAt(i);
                    _seenHits.Add(hit);
                    LogHit(hit, frame, update);
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

                if (picture != null)
                {
                    RequestPicture(picture);
                }
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

                Log("hit of=" + of + " slashId=" + hit.SlashId + " fragment=" + hit.Fragment.value + " side=" + hit.Side + pose
                    + " atLatch=" + hit.AtLatch + " acceptance=" + hit.Acceptance + " admission=" + hit.Admission
                    + " operation=" + hit.Operation.value + " lineage=" + lineage + " frame=" + frame
                    + " sweepAt=" + hit.At.ToString("F6", Inv) + Clocks(update));

                bool bad = hit.Acceptance == ProvisionalCutAcceptance.InvalidRequest
                    || hit.Acceptance == ProvisionalCutAcceptance.Aborted || hit.Acceptance == ProvisionalCutAcceptance.Stale;
                Expect(!bad, "hit " + _hitsLogged + " is an ordinary outcome of acceptance (" + hit.Acceptance + ")");
                if (hit.Acceptance != ProvisionalCutAcceptance.Published)
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

                if (_npc != null && _npc.Handle != null && _npc.Handle.Source.IsSet && at == _npc.Handle.Source)
                {
                    return "npc";
                }

                return at == _probe.Body ? "box" : "other";
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

                SummariseMarkers();
                Log("summary by lineage: boxRoot=" + _boxRootCuts + " boxChild=" + _boxChildCuts + " npcRoot=" + _npcRootCuts
                    + " npcChild=" + _npcChildCuts + " npcWithdrawnFrame=" + _npcWithdrawnFrame + " npcRootAcceptedFrame=" + _npcRootAcceptedFrame);
                Expect(_boxRootCuts >= 1, "the box was cut by a real hit");
                Expect(_boxChildCuts >= 1, "a surviving child of the box was cut by a real hit of another Slash");
                if (_npc != null)
                {
                    Expect(_npcRootCuts == 1, "the walking character was cut once, from the pose its hit met");
                    Expect(_npcChildCuts >= 1, "a surviving child of the character was cut by a real hit of another Slash");
                    Expect(_npcWithdrawnFrame == _npcRootAcceptedFrame,
                        "the character's root (its pose playback and skinned drawing) left in the frame its cut was published");
                }
                Expect(_accepted.TrueForAll(a => a.publishedFrame >= 0 && a.committedFrame >= 0),
                    "every accepted cut was published and its geometry committed");
                Expect(_world.GeometryFaults == 0 && !_world.TerminationRequested, "no geometry fault and no termination");

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
                    Log("piece fragment=" + id + " of=" + LineageOf(fragment) + " convexes=" + owner.Shape.ConvexCount
                        + " centre=" + centre.ToString("F3") + " pictureXY=(" + screen.x.ToString("F0", Inv) + "," + screen.y.ToString("F0", Inv)
                        + ") depth=" + screen.z.ToString("F2", Inv) + " mass=" + owner.Body.mass.ToString("R", Inv)
                        + " position=" + owner.Root.transform.position.ToString("F4")
                        + " lowestVertexY=" + lowest.ToString("F4", Inv) + " velocity=" + owner.Body.linearVelocity.ToString("F4")
                        + " sleeping=" + owner.Body.IsSleeping());
                    // A diagnostic, not a judgement: the piece's own B-rep at one moment, which the cooked collider and the
                    // discrete Steps may leave a little into the floor (DESIGN 7.3).
                    Log("piece " + id + " lowest own vertex at this moment " + (lowest > -0.02f ? "at or above" : "BELOW") + " floor -0.02 (diagnostic)");
                }

                SummariseObservation();
            }

            // One row per live piece per frame, and where each stands when the observation starts and ends.
            private void TrackPieces()
            {
                if (_pieceRows == null)
                {
                    GameObject floorObject = GameObject.Find("Prop Floor");
                    _floor = floorObject != null ? floorObject.GetComponent<Collider>() : null;
                    Log("floor: " + (_floor != null ? _floor.GetType().Name + " bounds min=" + _floor.bounds.min.ToString("F4") + " max=" + _floor.bounds.max.ToString("F4")
                            + " layer=" + _floor.gameObject.layer + " contactOffset=" + _floor.contactOffset.ToString("R", Inv) : "none")
                        + " defaultContactOffset=" + Physics.defaultContactOffset.ToString("R", Inv) + " solverIterations=" + Physics.defaultSolverIterations
                        + " bounceThreshold=" + Physics.bounceThreshold.ToString("R", Inv) + " gravity=" + Physics.gravity.ToString("F3"));
                    _pieceRows = new StreamWriter(Path.Combine(directory, "pieces.csv"));
                    _pieceRows.WriteLine("frame,phase,stepId,unsimulated,fragment,of,mass,posX,posY,posZ,velX,velY,velZ,speed,sleeping,kinematic,detection,"
                        + "lowestVertexY,colliders,enabledColliders,attached,colliderMinY,colliderMaxY,overFloor,floorPenetration,floorTouch,"
                        + "lowestX,lowestZ,centreX,centreY,centreZ,originOverFloor");
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
                        originOverFloor));

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

            private static unsafe Vector3 PieceCentre(PhysicsFragmentOwner owner)
            {
                float4x4 toWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
                float3 sum = float3.zero;
                int count = 0;
                for (int c = 0; c < owner.Shape.ConvexCount; c++)
                {
                    Zantetsu.ConvexCut.ConvexBrepRange range = owner.Shape.Convex(c);
                    Zantetsu.ConvexCut.ConvexBrepBank bank = owner.Shape.BankOf(c);
                    for (int v = 0; v < range.vertexCount; v++)
                    {
                        sum += math.transform(toWorld, bank.vertices[range.vertexBase + v]);
                        count++;
                    }
                }

                return count > 0 ? (Vector3)(sum / count) : owner.Root.transform.position;
            }

            private bool OverFloor(Vector3 point)
            {
                return _floor != null && point.x >= _floor.bounds.min.x && point.x <= _floor.bounds.max.x
                    && point.z >= _floor.bounds.min.z && point.z <= _floor.bounds.max.z;
            }

            private static unsafe Vector3 LowestVertex(PhysicsFragmentOwner owner)
            {
                float4x4 toWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
                float3 lowest = new float3(0f, float.PositiveInfinity, 0f);
                for (int c = 0; c < owner.Shape.ConvexCount; c++)
                {
                    Zantetsu.ConvexCut.ConvexBrepRange range = owner.Shape.Convex(c);
                    Zantetsu.ConvexCut.ConvexBrepBank bank = owner.Shape.BankOf(c);
                    for (int v = 0; v < range.vertexCount; v++)
                    {
                        float3 point = math.transform(toWorld, bank.vertices[range.vertexBase + v]);
                        if (point.y < lowest.y) lowest = point;
                    }
                }

                return lowest;
            }

            private static unsafe float LowestVertexY(PhysicsFragmentOwner owner)
            {
                float4x4 toWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
                float lowest = float.PositiveInfinity;
                for (int c = 0; c < owner.Shape.ConvexCount; c++)
                {
                    Zantetsu.ConvexCut.ConvexBrepRange range = owner.Shape.Convex(c);
                    Zantetsu.ConvexCut.ConvexBrepBank bank = owner.Shape.BankOf(c);
                    for (int v = 0; v < range.vertexCount; v++)
                    {
                        lowest = math.min(lowest, math.transform(toWorld, bank.vertices[range.vertexBase + v]).y);
                    }
                }

                return lowest;
            }

            private void RequestPicture(string name)
            {
                _pictures.Add(name + "@" + Time.frameCount);
                StartCoroutine(Picture(name));
            }

            private IEnumerator Picture(string name)
            {
                int frame = Time.frameCount;
                yield return new WaitForEndOfFrame();
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

            private void Expect(bool held, string what)
            {
                if (!held) _failures++;
                Log((held ? "ok: " : "FAILED: ") + what);
            }
        }
    }
}
