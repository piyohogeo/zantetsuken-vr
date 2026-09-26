using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Unity.Mathematics;
using UnityEngine;
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
                    if (!_recorder.TryAppendRecordedSample(sample, new Vector3(F(13), F(14), F(15))))
                    {
                        Expect(false, "the recorder took row " + (start + _loaded));
                    }

                    _rowTimes.Add(t);
                }

                ManualPhysicsClock clock = CutPhysicsStep.Clock;
                _clockStart = Time.unscaledTimeAsDouble;
                _replaying = _recorder.TryBeginReplay(_clockStart);
                Log("start: frame=" + Time.frameCount + " rows " + start + ".." + (start + _loaded - 1)
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
                    bool fedThrough = !_recorder.IsReplaying && _recorder.ReplayIndex >= _loaded;
                    bool settled = _accepted.TrueForAll(a => a.committedFrame >= 0);
                    if (fedThrough && _katana.WaveCount == 0 && settled && _accepted.Count > 0)
                    {
                        break;
                    }
                }

                _replaying = false;
                yield return null;
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
                _traceHistory?.Dispose();
                _traceLanes?.Dispose();
            }

            private void LateUpdate()
            {
                if (!_replaying || _world == null || _world.IsEnding)
                {
                    return;
                }

                int frame = Time.frameCount;
                int update = _recorder.ReplayIndex - 1;
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

                Log("hit slashId=" + hit.SlashId + " fragment=" + hit.Fragment.value + " side=" + hit.Side
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

                var accepted = new Accepted
                {
                    slash = hit.SlashId,
                    fragment = hit.Fragment,
                    operation = hit.Operation,
                    child = child,
                    acceptedFrame = frame,
                    name = "op" + hit.Operation.value + (child ? "-child" : "-root"),
                };
                _accepted.Add(accepted);
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
                return " update=" + update + " row=" + (start + update) + " recordedOffset=" + recorded.ToString("F4", Inv)
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

                Expect(_rootCuts >= 1, "the box was cut by a real hit");
                Expect(_childCuts >= 1, "a surviving child was cut by a real hit of another Slash");
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
                    Log("piece fragment=" + id + " position=" + owner.Root.transform.position.ToString("F4")
                        + " lowestVertexY=" + lowest.ToString("F4", Inv) + " velocity=" + owner.Body.linearVelocity.ToString("F4")
                        + " sleeping=" + owner.Body.IsSleeping());
                    Expect(lowest > -0.02f, "piece " + id + " rests on or above the floor");
                }
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
