using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.Core;
using Zantetsu.Core.Input;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// A Player check of artificial movement in Sandbox.unity (Phase 4.2 / T-088), on when the Player is started with
    /// <see cref="Argument"/> and a directory for its pictures. It replays fixed input through the Sandbox adapter's
    /// own entry -- input, request, allowed or rejected, the Root's shown position -- and a fixed HMD pose through the
    /// locomotion's replay entry, writes each outcome, and ends the Player with 0 when every expectation held.
    /// <para>
    /// It never writes the tracked HMD or controllers. Its pictures come from a camera of its own looking down on the
    /// walls, with two markers under the Root and the HMD so their shown positions can be seen.
    /// </para>
    /// </summary>
    public static class SandboxLocomotionPlayerCheck
    {
        public const string Argument = "-zantetsuLocomotionCheck";
        public const string Prefix = "LOCOMOTION CHECK: ";

        private const float StepSeconds = 1f / 90f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    var host = new GameObject("Locomotion player check");
                    UnityEngine.Object.DontDestroyOnLoad(host);
                    host.AddComponent<Walk>().directory = arguments[i + 1];
                    return;
                }
            }
        }

        /// <summary>
        /// The check's scripted movement, submitted through the Sandbox adapter's entry before anything else in the
        /// frame -- before the recorder feeds the katana -- as the live adapter's own order does.
        /// </summary>
        [DefaultExecutionOrder(-200)]
        private sealed class Mover : MonoBehaviour
        {
            internal SandboxLocomotionInput input;
            internal int Allowed;
            internal int Rejected;
            private int _start = -1;
            private int _count;
            private Func<int, SandboxLocomotionCommand> _script;

            internal void Begin(int startFrame, int count, Func<int, SandboxLocomotionCommand> script)
            {
                _start = startFrame;
                _count = count;
                _script = script;
                Allowed = 0;
                Rejected = 0;
            }

            private void Update()
            {
                int index = Time.frameCount - _start;
                if (_script == null || _start < 0 || index < 0 || index >= _count)
                {
                    return;
                }

                LocomotionVerdict? verdict = input.Submit(_script(index), StepSeconds);
                if (verdict == LocomotionVerdict.Allowed) Allowed++;
                else if (verdict.HasValue) Rejected++;
            }
        }

        private static void Log(string line)
        {
            Debug.Log(Prefix + line);
        }

        private static string F(Vector3 v)
        {
            return "(" + v.x.ToString("F3", CultureInfo.InvariantCulture) + ", " + v.y.ToString("F3", CultureInfo.InvariantCulture)
                + ", " + v.z.ToString("F3", CultureInfo.InvariantCulture) + ")";
        }

        [DefaultExecutionOrder(400)]
        private sealed class Walk : MonoBehaviour
        {
            internal string directory;

            private SandboxLocomotionInput _input;
            private PlayerLocomotion _locomotion;
            private Transform _hmd;
            private int _failures;

            private IEnumerator Start()
            {
                Directory.CreateDirectory(directory);
                _input = FindAnyObjectByType<SandboxLocomotionInput>();
                _locomotion = _input != null ? _input.Locomotion : null;
                if (_locomotion == null)
                {
                    Log("FAILED: no SandboxLocomotionInput with a PlayerLocomotion in the scene");
                    Application.Quit(11);
                    yield break;
                }

                _input.LiveInputEnabled = false;
                _hmd = Camera.main != null ? Camera.main.transform : null;
                Log("xr deviceActive=" + XRSettings.isDeviceActive + " loadedDevice='" + XRSettings.loadedDeviceName + "'"
                    + " hmdInRoot=" + F(_locomotion.CurrentHmdInRoot.position)
                    + " occupancy boxes=" + _locomotion.Occupancy.BoxCount + " capsules=" + _locomotion.Occupancy.CapsuleCount
                    + " layer=" + LayerMask.LayerToName(_locomotion.gameObject.layer));
                SetUpView();
                yield return null;
                yield return Picture("0-start");

                // A. Into the left wall with the left stick: some steps allowed, then every step rejected, and the Root
                //    never enters the wall and does not slide.
                yield return Drive("A-left", new SandboxLocomotionCommand(new Vector2(-1f, 0f), 0), 150);
                float stoppedX = _locomotion.RootPose.position.x;
                Expect(stoppedX > -2.1f + 0.25f - 1e-3f && stoppedX < -1.5f, "A: stopped short of the wall, x=" + stoppedX);
                yield return Picture("A-left-wall");

                // B. The wall's source object is taken away: the settled occupancy still rejects.
                GameObject walls = GameObject.Find("Locomotion Walls");
                Transform leftWall = walls != null ? walls.transform.Find("Wall Left") : null;
                Transform leftVolume = walls != null ? walls.transform.Find("Wall Left Occupancy") : null;
                bool bothFound = leftWall != null && leftVolume != null;
                if (leftWall != null) Destroy(leftWall.gameObject);
                if (leftVolume != null) Destroy(leftVolume.gameObject);
                yield return null;
                Log("B: left wall display and authoring found and destroyed=" + bothFound
                    + " stillThere=" + (GameObject.Find("Locomotion Walls/Wall Left Occupancy") != null));
                Expect(bothFound, "B: the left wall's display and authoring were there to destroy");
                int rejectedBefore = _locomotion.RejectedCount;
                Vector3 before = _locomotion.RootPose.position;
                yield return Drive("B-left-again", new SandboxLocomotionCommand(new Vector2(-1f, 0f), 0), 30);
                Expect(_locomotion.RejectedCount - rejectedBefore == 30 && _locomotion.RootPose.position == before,
                    "B: every request still rejected after the source was destroyed");
                yield return Picture("B-wall-source-gone");

                // C. A replayed HMD pose half a metre to the left of the Root: the Root capsule is clear but the HMD's
                //    is in the wall, so the whole request is rejected; farther right both are clear.
                Pose root = _locomotion.RootPose;
                var leaning = new Pose(new Vector3(-0.5f, 1.6f, 0f), Quaternion.identity);
                LocomotionVerdict hmdOnly = _locomotion.TryRequest(new Pose(root.position + new Vector3(0.05f, 0f, 0f), root.rotation), leaning);
                Log("C: step right 0.05 m with the HMD 0.5 m left -> " + hmdOnly + " root=" + F(_locomotion.RootPose.position));
                Expect(hmdOnly == LocomotionVerdict.RejectedHmd && _locomotion.RootPose.position == root.position, "C: HMD-only overlap rejects");
                LocomotionVerdict clear = _locomotion.TryRequest(new Pose(root.position + new Vector3(0.6f, 0f, 0f), root.rotation), leaning);
                Log("C: step right 0.6 m with the HMD 0.5 m left -> " + clear + " root=" + F(_locomotion.RootPose.position));
                Expect(clear == LocomotionVerdict.Allowed, "C: clear of the wall allowed");
                yield return Picture("C-hmd-replay");

                // D. A snap turn to the left with the right stick, then backwards into the back wall.
                float yawBefore = _locomotion.RootPose.rotation.eulerAngles.y;
                yield return Drive("D-turn", new SandboxLocomotionCommand(Vector2.zero, -1), 1, true);
                float yawAfter = _locomotion.RootPose.rotation.eulerAngles.y;
                Expect(Mathf.Abs(Mathf.DeltaAngle(yawBefore - 30f, yawAfter)) < 0.01f, "D: turned 30 degrees left, yaw=" + yawAfter);
                yield return Drive("D-back", new SandboxLocomotionCommand(new Vector2(0f, -1f), 0), 200);
                float stoppedZ = _locomotion.RootPose.position.z;
                Expect(stoppedZ > -2.1f + 0.25f - 1e-3f, "D: stopped short of the back wall, z=" + stoppedZ);
                yield return Picture("D-back-wall");

                // E. Forward again, away from every wall: allowed.
                yield return Drive("E-forward", new SandboxLocomotionCommand(new Vector2(0f, 1f), 0), 60, true);
                yield return Picture("E-forward");

                yield return KatanaPhases();

                Log("totals allowed=" + _locomotion.AllowedCount + " rejected=" + _locomotion.RejectedCount + " failures=" + _failures);
                int code = _failures == 0 ? 0 : 11;
                Log("finished with code " + code);
                yield return null;
                Application.Quit(code);
            }

            // ---- The katana under artificial movement (U4): fixed device poses through the recorder's own replay entry,
            //      one per frame into the katana's Update path, with the movement applied first in the frame.

            private SandboxRightHandKatana _katana;
            private SandboxSlashPoseRecorder _recorder;
            private Mover _mover;

            private sealed class ReplayResult
            {
                public readonly List<int> Accepted = new List<int>();
                public int LatchIndex = -1;
                public Pose Space;
                public Plane Plane;
                public Vector3 Origin, Travel, SpanAxis, SegmentStart, SegmentEnd;
                public float Span;
                public int WavesSeen;
                public float WorstShownError;
                public int ShownFrames;
            }

            private IEnumerator KatanaPhases()
            {
                _katana = FindAnyObjectByType<SandboxRightHandKatana>();
                _recorder = FindAnyObjectByType<SandboxSlashPoseRecorder>();
                if (_katana == null || _recorder == null)
                {
                    Expect(false, "F/G: the scene has the katana and the recorder");
                    yield break;
                }

                _mover = gameObject.AddComponent<Mover>();
                _mover.input = _input;
                Log("F/G: katana tracking space=" + F(_katana.TrackingSpacePose.position) + " live input=" + _katana.LiveInputEnabled);

                // From a known clear placement, through the same request entry.
                LocomotionVerdict home = _locomotion.TryRequest(new Pose(Vector3.zero, Quaternion.identity));
                Expect(home == LocomotionVerdict.Allowed, "F/G: back to the origin through a request");
                yield return null;

                Quaternion upright = Quaternion.Inverse(_katana.GripToKatanaOffset.rotation);

                // F. The hand still, its edge to one side; three snap turns that way.
                foreach (int side in new[] { -1, 1 })
                {
                    string name = side < 0 ? "F-still-edge-left-snap-left" : "F-still-edge-right-snap-right";
                    Quaternion grip = Quaternion.Euler(0f, 0f, 90f * side) * upright;
                    var still = new List<BladePoseSample>();
                    for (int i = 0; i < 60; i++)
                    {
                        still.Add(Device(i, new Vector3(0.25f, 1.2f, 0.35f), grip));
                    }

                    float yawBefore = _locomotion.RootPose.rotation.eulerAngles.y;
                    var result = new ReplayResult();
                    int turn = side;
                    yield return Replay(name, still, i => i == 10 || i == 25 || i == 40 ? new SandboxLocomotionCommand(Vector2.zero, turn) : default, result);
                    float turned = Mathf.DeltaAngle(yawBefore, _locomotion.RootPose.rotation.eulerAngles.y);
                    int maxAccepted = 0;
                    foreach (int a in result.Accepted) maxAccepted = Mathf.Max(maxAccepted, a);
                    Expect(maxAccepted == 0 && result.WavesSeen == 0, name + ": no acceptance and no wave (max accepted " + maxAccepted + ", waves " + result.WavesSeen + ")");
                    Expect(Mathf.Abs(Mathf.Abs(turned) - 90f) < 0.01f, name + ": the player turned 90 degrees, turned=" + turned);
                    Expect(result.WorstShownError < 1e-4f && result.ShownFrames == still.Count,
                        name + ": the katana was shown where the tracking space places it every frame (" + result.ShownFrames + " frames, worst "
                        + result.WorstShownError.ToString("G3", CultureInfo.InvariantCulture) + " m)");
                    yield return Picture(name);
                }

                // G. The normal sweep, first without movement, then strafing with a snap turn part way.
                home = _locomotion.TryRequest(new Pose(Vector3.zero, Quaternion.identity));
                Expect(home == LocomotionVerdict.Allowed, "G: back to the origin through a request");
                var sweep = new List<BladePoseSample>();
                for (int i = 0; i < 58; i++)
                {
                    int steps = Mathf.Clamp(i - 9, 0, 8);
                    sweep.Add(Device(i, new Vector3(0f, 1.4f, 0.3f) + (new Vector3(0f, -0.12f, 0f) * steps), upright));
                }

                var reference = new ReplayResult();
                yield return Replay("G-sweep-still", sweep, i => default, reference);
                Expect(reference.LatchIndex >= 0 && reference.WavesSeen == 1, "G-sweep-still: the sweep latched once, at sample " + reference.LatchIndex);
                yield return Picture("G-sweep-still");

                home = _locomotion.TryRequest(new Pose(new Vector3(0.3f, 0f, 0.2f), Quaternion.Euler(0f, 20f, 0f)));
                Expect(home == LocomotionVerdict.Allowed, "G: another clear placement through a request");
                var moved = new ReplayResult();
                yield return Replay("G-sweep-moving", sweep,
                    i => i == 12 ? new SandboxLocomotionCommand(new Vector2(1f, 0f), 1) : new SandboxLocomotionCommand(new Vector2(1f, 0f), 0), moved);
                Expect(moved.Accepted.Count == reference.Accepted.Count && SameSequence(moved.Accepted, reference.Accepted),
                    "G-sweep-moving: the same acceptance, sample by sample, as without movement");
                Expect(moved.LatchIndex == reference.LatchIndex && moved.WavesSeen == 1,
                    "G-sweep-moving: latched once at the same sample (" + moved.LatchIndex + " vs " + reference.LatchIndex + ")");
                Expect(moved.WorstShownError < 1e-4f && moved.ShownFrames == sweep.Count,
                    "G-sweep-moving: the katana was shown where the tracking space places it every frame (" + moved.ShownFrames + " frames, worst "
                    + moved.WorstShownError.ToString("G3", CultureInfo.InvariantCulture) + " m)");
                if (moved.LatchIndex >= 0 && reference.LatchIndex >= 0)
                {
                    // The wave is where the still sweep's wave would be, carried by the difference of the placements at
                    // the two latches.
                    Quaternion turn = moved.Space.rotation * Quaternion.Inverse(reference.Space.rotation);
                    Vector3 Carry(Vector3 p) => moved.Space.position + (turn * (p - reference.Space.position));
                    // The plane's position: the reference plane's own point -normal * distance, carried, lies on the
                    // published plane (the carried point is on it, not its closest point to the world origin).
                    var errors = new (string what, float error)[]
                    {
                        ("normal", Vector3.Distance(moved.Plane.normal, turn * reference.Plane.normal)),
                        ("plane position", Mathf.Abs(moved.Plane.GetDistanceToPoint(Carry(-reference.Plane.normal * reference.Plane.distance)))),
                        ("origin", Vector3.Distance(moved.Origin, Carry(reference.Origin))),
                        ("travel", Vector3.Distance(moved.Travel, turn * reference.Travel)),
                        ("span axis", Vector3.Distance(moved.SpanAxis, turn * reference.SpanAxis)),
                        ("segment start", Vector3.Distance(moved.SegmentStart, Carry(reference.SegmentStart))),
                        ("segment end", Vector3.Distance(moved.SegmentEnd, Carry(reference.SegmentEnd))),
                        ("initial span", Mathf.Abs(moved.Span - reference.Span)),
                    };
                    float worst = 0f;
                    var each = new System.Text.StringBuilder();
                    foreach (var e in errors)
                    {
                        worst = Mathf.Max(worst, e.error);
                        each.Append(' ').Append(e.what).Append('=').Append(e.error.ToString("G3", CultureInfo.InvariantCulture));
                    }

                    Log("G: wave against the still sweep's, carried to the latch placement:" + each);
                    Log("G: latch placements still=" + F(reference.Space.position) + " yaw " + reference.Space.rotation.eulerAngles.y.ToString("F2", CultureInfo.InvariantCulture)
                        + " moving=" + F(moved.Space.position) + " yaw " + moved.Space.rotation.eulerAngles.y.ToString("F2", CultureInfo.InvariantCulture)
                        + "; moving wave origin=" + F(moved.Origin) + " travel=" + F(moved.Travel) + " span=" + moved.Span.ToString("F4", CultureInfo.InvariantCulture));
                    Expect(worst < 1e-4f, "G-sweep-moving: plane, origin, axes, initial span and segment are the still sweep's, placed at the latch (worst "
                        + worst.ToString("G3", CultureInfo.InvariantCulture) + ")");
                }

                yield return Picture("G-sweep-moving");
                Destroy(_mover);
            }

            private static bool SameSequence(List<int> a, List<int> b)
            {
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] != b[i]) return false;
                }

                return true;
            }

            private static BladePoseSample Device(int index, Vector3 position, Quaternion rotation)
            {
                return new BladePoseSample(index + 1, 0.011 * (index + 1), position, rotation,
                    BladeTrackingState.Position | BladeTrackingState.Rotation);
            }

            // Loads the poses into the recorder and replays them: one per frame, as the recorder's Update does, while the
            // mover submits this frame's movement first. Read after each frame's updates.
            private IEnumerator Replay(string name, List<BladePoseSample> device, Func<int, SandboxLocomotionCommand> script, ReplayResult result)
            {
                _katana.ResetToKnownState();
                _recorder.BeginRecording();
                foreach (BladePoseSample sample in device)
                {
                    // The head looks straight ahead in tracking space.
                    _recorder.TryAppendRecordedSample(sample, Vector3.forward);
                }

                bool begun = _recorder.TryBeginReplay(Time.unscaledTimeAsDouble);
                _mover.Begin(Time.frameCount + 1, device.Count, script);
                Expect(begun, name + ": the replay began with " + device.Count + " poses");
                Pose offset = _katana.GripToKatanaOffset;
                for (int i = 0; i < device.Count; i++)
                {
                    yield return null;
                    result.Accepted.Add(_katana.AcceptedSampleCount);
                    Transform shown = _katana.Katana;
                    if (shown != null && shown.gameObject.activeSelf)
                    {
                        Pose space = _katana.TrackingSpacePose;
                        Vector3 expected = space.position + (space.rotation * (device[i].GripPosition + (device[i].GripRotation * offset.position)));
                        result.WorstShownError = Mathf.Max(result.WorstShownError, Vector3.Distance(shown.position, expected));
                        result.ShownFrames++;
                    }

                    result.WavesSeen = Mathf.Max(result.WavesSeen, _katana.WaveCount);
                    if (result.LatchIndex < 0 && _katana.WaveCount > 0
                        && _katana.TryGetWave(0, out _, out Plane plane, out Vector3 origin, out Vector3 travel, out Vector3 spanAxis,
                            out float span, out _, out _, out Vector3 start, out Vector3 end))
                    {
                        result.LatchIndex = i;
                        result.Space = _katana.TrackingSpacePose;
                        result.Plane = plane;
                        result.Origin = origin;
                        result.Travel = travel;
                        result.SpanAxis = spanAxis;
                        result.Span = span;
                        result.SegmentStart = start;
                        result.SegmentEnd = end;
                    }
                }

                Log(name + ": replayed " + _recorder.ReplayIndex + "/" + device.Count + " moves allowed=" + _mover.Allowed + " rejected=" + _mover.Rejected
                    + " latch at sample " + result.LatchIndex + " waves " + result.WavesSeen + " accepted=[" + string.Join(",", result.Accepted) + "]"
                    + " root=" + F(_locomotion.RootPose.position) + " yaw=" + _locomotion.RootPose.rotation.eulerAngles.y.ToString("F2", CultureInfo.InvariantCulture));
                Expect(_recorder.ReplayIndex == device.Count && _mover.Rejected == 0, name + ": every pose replayed and every move allowed");
            }

            private IEnumerator Drive(string name, SandboxLocomotionCommand command, int frames, bool allAllowed = false)
            {
                int allowed = 0;
                int rejected = 0;
                int firstRejected = -1;
                bool movedAfterRejection = false;
                Vector3 atFirstRejection = default;
                for (int i = 0; i < frames; i++)
                {
                    LocomotionVerdict? verdict = _input.Submit(command, StepSeconds);
                    if (verdict == LocomotionVerdict.Allowed)
                    {
                        allowed++;
                        if (firstRejected >= 0 && _locomotion.RootPose.position != atFirstRejection) movedAfterRejection = true;
                    }
                    else if (verdict.HasValue)
                    {
                        rejected++;
                        if (firstRejected < 0)
                        {
                            firstRejected = i;
                            atFirstRejection = _locomotion.RootPose.position;
                            Log(name + ": first rejection at step " + i + " (" + verdict.Value + ") root=" + F(atFirstRejection));
                        }
                    }

                    // The pose shown is the one the request left: nothing overlaps there.
                    Pose now = _locomotion.RootPose;
                    if (PlayerLocomotion.Judge(_locomotion.Occupancy, _locomotion.Body, now, _locomotion.CurrentHmdInRoot) != LocomotionVerdict.Allowed)
                    {
                        Expect(false, name + ": the shown pose overlaps at step " + i);
                    }

                    yield return null;
                }

                Expect(!movedAfterRejection, name + ": nothing moved after the first rejection (no slide)");
                if (allAllowed)
                {
                    Expect(allowed == frames, name + ": every request allowed");
                }

                Log(name + ": steps=" + frames + " allowed=" + allowed + " rejected=" + rejected + " movedAfterRejection=" + movedAfterRejection
                    + " root=" + F(_locomotion.RootPose.position) + " yaw=" + _locomotion.RootPose.rotation.eulerAngles.y.ToString("F2", CultureInfo.InvariantCulture)
                    + " hmd=" + (_hmd != null ? F(_hmd.position) : "none"));
            }

            private void Expect(bool held, string what)
            {
                if (!held)
                {
                    _failures++;
                }

                Log((held ? "ok: " : "FAILED: ") + what);
            }

            // A camera of the check's own above the walls, drawn over the scene's, and two markers without colliders.
            private void SetUpView()
            {
                var view = new GameObject("Locomotion check view").AddComponent<Camera>();
                view.transform.SetPositionAndRotation(new Vector3(0f, 7f, -0.5f), Quaternion.Euler(90f, 0f, 0f));
                view.depth = 100f;
                view.stereoTargetEye = StereoTargetEyeMask.None;
                Marker(PrimitiveType.Capsule, _locomotion.transform, new Vector3(0f, 0.9f, 0f), new Vector3(0.5f, 0.8f, 0.5f), Color.green);
                Marker(PrimitiveType.Cube, _locomotion.transform, new Vector3(0f, 1.9f, 0.35f), new Vector3(0.1f, 0.1f, 0.5f), Color.green);
                if (_hmd != null)
                {
                    Marker(PrimitiveType.Sphere, _hmd, Vector3.zero, Vector3.one * 0.3f, Color.cyan);
                }
            }

            private static void Marker(PrimitiveType type, Transform parent, Vector3 local, Vector3 scale, Color color)
            {
                GameObject marker = GameObject.CreatePrimitive(type);
                Destroy(marker.GetComponent<Collider>());
                marker.layer = parent.gameObject.layer;
                marker.transform.SetParent(parent, false);
                marker.transform.localPosition = local;
                marker.transform.localScale = scale;
                Material material = marker.GetComponent<Renderer>().material;
                material.color = color;
                if (material.HasProperty("_BaseColor"))
                {
                    material.SetColor("_BaseColor", color);
                }
            }

            private IEnumerator Picture(string name)
            {
                string file = Path.Combine(directory, name + ".png");
                yield return new WaitForEndOfFrame();
                var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
                picture.Apply(false);
                File.WriteAllBytes(file, picture.EncodeToPNG());
                Destroy(picture);
                Log("picture " + name + " -> " + file + " root=" + F(_locomotion.RootPose.position));
                yield return null;
            }
        }
    }
}
