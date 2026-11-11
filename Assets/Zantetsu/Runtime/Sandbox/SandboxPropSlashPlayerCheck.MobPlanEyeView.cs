using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    // Measurement (the MobPlanSlash unit, view height and direction): the eye as it is rendered, not as the check's
    // LateUpdate sees it. From the replay's start, at every Application.onBeforeRender -- after the tracked pose driver has
    // put the newest head pose on the camera -- the main camera's height above the floor under the player and its pitch
    // and roll are written (mobplan-eye.csv), with or without the view conditions, and summed up at the end.
    //
    // "-zantetsuEyeLevel": a view condition of a measurement series, for the measurement replay only. The replayed head of
    // the moving VRS looks some 22 degrees up for most of a run; with this the view is held level: a pivot is put between
    // the tracking space (the Camera Offset) and the camera, and turned every frame (in the check's LateUpdate and again
    // before rendering) so the camera's pitch and roll are taken away, its yaw and its position kept. Only the camera
    // hangs under the pivot: the katana and the hands stay in the tracking space as they are, and the eye height hold
    // (-zantetsuEyeHeight), which moves the tracking space, still reads the same camera position.
    //
    // "-zantetsuEyeFixed": the view condition of a measurement that must not depend on the head at all (a headset worn
    // by a person, or lying on a desk: 2026-11-11, for runs on the Quest that are compared with each other). The same
    // pivot takes the whole of the head's pose away: the camera stands at SandboxEyeView.FixedEyePosition of the
    // tracking space, level, facing the tracking space's forward, every frame. What moves the view is then the
    // script alone -- the city route moves the tracking space, and the eye height hold still sets the height (from the
    // fixed eye: the hold sets the pivot for the head's newest pose before it reads the height, SandboxEyeView.HoldEyeHeight). The
    // katana and the hands stay in the tracking space, so the replayed cuts are where they were. It includes the
    // level view (pitch and roll are gone with the rest) and wins when both are given.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private StreamWriter _mpEyeRows;
            private readonly List<(double t, float eye, float pitch, float roll)> _mpEyeAtRender = new List<(double, float, float, float)>();
            private double _mpEyeStart;
            private UnityEngine.Events.UnityAction _mpEyeCallback;
            private Transform _mpEyePivot;
            private bool _mpEyeFixed;
            private bool _mpEyeFixedSeen, _mpEyeFixedSaid;
            private Vector3 _mpEyeFixedFirst;
            private Quaternion _mpEyeFixedFirstRotation;
            private float _mpEyeFixedMoveMax, _mpEyeFixedTurnMax;

            private static float Signed(float degrees) => degrees > 180f ? degrees - 360f : degrees;

            private void MobPlanEyeViewBegin()
            {
                if (_mpDetail)
                {
                    _mpEyeRows = new StreamWriter(Path.Combine(directory, "mobplan-eye.csv"));
                    _mpEyeRows.WriteLine("frame,t,eyeAtRender,cameraY,floorY,pitch,roll,yaw");
                }

                _mpEyeStart = Zantetsu.Core.GamePause.UnscaledTimeAsDouble;
                bool fixedEye = System.Environment.GetCommandLineArgs().Contains(SandboxEyeView.FixedArgument);
                if (fixedEye || System.Environment.GetCommandLineArgs().Contains("-zantetsuEyeLevel"))
                {
                    Camera camera = Camera.main;
                    Transform space = GameObject.Find("XR Origin")?.transform.Find("Camera Offset");
                    string line;
                    if (camera == null || space == null || camera.transform.parent != space)
                    {
                        line = (fixedEye ? "mobplan eye fixed" : "mobplan eye level") + ": not set -- the main camera is not directly under the XR Origin's Camera Offset ("
                               + (camera != null && camera.transform.parent != null ? camera.transform.parent.name : "none") + ")"
                               + (fixedEye ? "; THE VIEW FOLLOWS THE HEAD: this run is not one of a fixed view" : "");
                        if (fixedEye) Debug.LogError("PROP SLASH: " + line);
                    }
                    else
                    {
                        _mpEyePivot = new GameObject("Check View Level").transform;
                        _mpEyePivot.SetParent(space, false);
                        camera.transform.SetParent(_mpEyePivot, false);
                        GameViewEyeLevel(_mpEyePivot);   // the one sanctioned re-parenting of the game camera, noted where it is made
                        _mpEyeFixed = fixedEye;
                        MobPlanLevelEye();
                        // The eye height hold set the tracking space from the head's height just before this (MobPlanLifetimeBegin):
                        // under the fixed view it is set again here, from the fixed eye, before the first frame is drawn.
                        if (fixedEye) MobPlanHoldEyeHeight();
                        line = fixedEye
                            ? "mobplan eye fixed: the head's pose is taken away every frame; the eye stands at " + SandboxEyeView.FixedEyePosition.ToString("F3")
                              + " of the tracking space, level, facing its forward (the route and the eye height hold move the view, the head does not); "
                              + "the camera alone hangs under the pivot"
                            : "mobplan eye level: the view is held level (pitch and roll taken away, yaw and position kept) every frame; "
                              + "the camera alone hangs under the pivot";
                    }

                    Log(line);
                    MobPlanRecord(line);
                }

                _mpEyeCallback = MobPlanEyeAtRender;
                Application.onBeforeRender += _mpEyeCallback;
            }

            // The pivot's pose in the tracking space so the camera (its local pose the head's) looks level from where the
            // head is: rotation P^-1 * yaw(P * L) * L^-1, position L.p - R * L.p.
            private void MobPlanLevelEye()
            {
                if (_mpEyePivot == null) return;
                Camera camera = Camera.main;
                if (camera == null || camera.transform.parent != _mpEyePivot) return;
                Transform space = _mpEyePivot.parent;
                Quaternion head = camera.transform.localRotation;
                Vector3 at = camera.transform.localPosition;
                if (_mpEyeFixed)
                {
                    // What the head did, for the record: none of it reaches the view.
                    if (!_mpEyeFixedSeen) { _mpEyeFixedSeen = true; _mpEyeFixedFirst = at; _mpEyeFixedFirstRotation = head; }
                    _mpEyeFixedMoveMax = Mathf.Max(_mpEyeFixedMoveMax, Vector3.Distance(at, _mpEyeFixedFirst));
                    _mpEyeFixedTurnMax = Mathf.Max(_mpEyeFixedTurnMax, Quaternion.Angle(head, _mpEyeFixedFirstRotation));
                    SandboxEyeView.FixedPivot(head, at, out Quaternion fixedRotation, out Vector3 fixedPosition);
                    _mpEyePivot.localRotation = fixedRotation;
                    _mpEyePivot.localPosition = fixedPosition;
                    return;
                }

                Quaternion world = space.rotation * head;
                Vector3 forward = world * Vector3.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-8f) return;
                Quaternion level = Quaternion.LookRotation(forward.normalized, Vector3.up);
                Quaternion pivot = Quaternion.Inverse(space.rotation) * level * Quaternion.Inverse(head);
                _mpEyePivot.localRotation = pivot;
                _mpEyePivot.localPosition = at - pivot * at;
            }

            private void MobPlanEyeAtRender()
            {
                MobPlanLevelEye();
                Camera camera = Camera.main;
                if (_mpEyeCallback == null || camera == null || _mpInput == null) return;
                float floor = _mpInput.player.transform.position.y;
                float y = camera.transform.position.y;
                Vector3 e = camera.transform.eulerAngles;
                double t = Zantetsu.Core.GamePause.UnscaledTimeAsDouble - _mpEyeStart;
                _mpEyeAtRender.Add((t, y - floor, Signed(e.x), Signed(e.z)));
                _mpEyeRows?.WriteLine(string.Join(",", Time.frameCount, t.ToString("F4", Inv), (y - floor).ToString("F4", Inv),
                    y.ToString("F4", Inv), floor.ToString("F4", Inv), Signed(e.x).ToString("F2", Inv), Signed(e.z).ToString("F2", Inv),
                    e.y.ToString("F2", Inv)));
            }

            private void MobPlanEyeViewEnd()
            {
                if (_mpEyeAtRender.Count == 0) return;
                string Stats(IEnumerable<float> source)
                {
                    List<float> v = source.OrderBy(x => x).ToList();
                    return v.Count == 0 ? "none" : "n=" + v.Count + " min " + v[0].ToString("F3", Inv) + " median " + v[v.Count / 2].ToString("F3", Inv)
                                                   + " max " + v[v.Count - 1].ToString("F3", Inv);
                }

                string line = "mobplan eye at rendering (camera above the floor under the player; pitch, negative up; roll): first 5 s height "
                              + Stats(_mpEyeAtRender.Where(e => e.t < 5.0).Select(e => e.eye)) + "; after 5 s height "
                              + Stats(_mpEyeAtRender.Where(e => e.t >= 5.0).Select(e => e.eye)) + " pitch "
                              + Stats(_mpEyeAtRender.Where(e => e.t >= 5.0).Select(e => e.pitch)) + " roll "
                              + Stats(_mpEyeAtRender.Where(e => e.t >= 5.0).Select(e => e.roll));
                Log(line);
                MobPlanRecord(line);
            }

            private void MobPlanEyeViewClose()
            {
                // Said here, not in the summary of a run with its checks: every run closes, and the record is still open.
                if (_mpEyeFixed && !_mpEyeFixedSaid)
                {
                    _mpEyeFixedSaid = true;
                    string head = "mobplan eye fixed: the tracked head moved " + _mpEyeFixedMoveMax.ToString("F3", Inv) + " m and turned "
                                  + _mpEyeFixedTurnMax.ToString("F1", Inv) + " degrees at most from where it first stood; none of it reached the view";
                    Log(head);
                    MobPlanRecord(head);
                }

                if (_mpEyeCallback != null) Application.onBeforeRender -= _mpEyeCallback;
                _mpEyeCallback = null;
                _mpEyeRows?.Dispose();
                _mpEyeRows = null;
            }
        }
    }

    /// <summary>
    /// The fixed view of a measurement (<see cref="FixedArgument"/>): where the eye stands in the tracking space, and
    /// the pose of the pivot between the tracking space and the camera that puts it there whatever the head's pose is.
    /// </summary>
    public static class SandboxEyeView
    {
        /// <summary>The Player argument that takes the head's pose out of the view for the replay (the city walk's measurement on a headset).</summary>
        public const string FixedArgument = "-zantetsuEyeFixed";

        /// <summary>Where the fixed eye stands in the tracking space: over its origin, at the height the measurements hold the eye at.</summary>
        public static readonly Vector3 FixedEyePosition = new Vector3(0f, 1.6f, 0f);

        /// <summary>
        /// The pivot's local pose for a camera whose local pose under it is the head's: the camera then stands at
        /// <see cref="FixedEyePosition"/> of the pivot's parent with no rotation of its own (rotation L^-1, position
        /// fixed - L^-1 * L.p).
        /// </summary>
        public static void FixedPivot(Quaternion headRotation, Vector3 headPosition, out Quaternion pivotRotation, out Vector3 pivotPosition)
        {
            pivotRotation = Quaternion.Inverse(headRotation);
            pivotPosition = FixedEyePosition - pivotRotation * headPosition;
        }

        /// <summary>
        /// The eye height hold of one frame (-zantetsuEyeHeight): the tracking space is moved up or down so that the
        /// eye stands <paramref name="wanted"/> above the floor. Under the fixed view (<paramref name="fixedPivot"/>
        /// given, the camera hanging under it) the pivot is set for the head's newest pose FIRST, so the height read
        /// is the fixed eye's and nothing the head does moves the tracking space -- or the katana and the hands in
        /// it. (Review, 2026-11-11: the hold ran before the pivot's update, read the old pivot with the new head and
        /// moved the space against the head's own rise: a head raised 0.1 m left the eye 0.1 m low that frame.)
        /// Returns the eye's height above the floor before the move.
        /// </summary>
        public static float HoldEyeHeight(Transform space, Transform fixedPivot, Transform camera, float floorY, float wanted)
        {
            if (fixedPivot != null && camera.parent == fixedPivot)
            {
                FixedPivot(camera.localRotation, camera.localPosition, out Quaternion rotation, out Vector3 position);
                fixedPivot.localRotation = rotation;
                fixedPivot.localPosition = position;
            }

            float eye = camera.position.y - floorY;
            float delta = wanted - eye;
            if (Mathf.Abs(delta) > 0.0005f)
            {
                space.position += Vector3.up * delta;
            }

            return eye;
        }
    }
}
