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
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private StreamWriter _mpEyeRows;
            private readonly List<(double t, float eye, float pitch, float roll)> _mpEyeAtRender = new List<(double, float, float, float)>();
            private double _mpEyeStart;
            private UnityEngine.Events.UnityAction _mpEyeCallback;
            private Transform _mpEyePivot;

            private static float Signed(float degrees) => degrees > 180f ? degrees - 360f : degrees;

            private void MobPlanEyeViewBegin()
            {
                if (_mpDetail)
                {
                    _mpEyeRows = new StreamWriter(Path.Combine(directory, "mobplan-eye.csv"));
                    _mpEyeRows.WriteLine("frame,t,eyeAtRender,cameraY,floorY,pitch,roll,yaw");
                }

                _mpEyeStart = Time.unscaledTimeAsDouble;
                if (System.Environment.GetCommandLineArgs().Contains("-zantetsuEyeLevel"))
                {
                    Camera camera = Camera.main;
                    Transform space = GameObject.Find("XR Origin")?.transform.Find("Camera Offset");
                    string line;
                    if (camera == null || space == null || camera.transform.parent != space)
                    {
                        line = "mobplan eye level: not set -- the main camera is not directly under the XR Origin's Camera Offset ("
                               + (camera != null && camera.transform.parent != null ? camera.transform.parent.name : "none") + ")";
                    }
                    else
                    {
                        _mpEyePivot = new GameObject("Check View Level").transform;
                        _mpEyePivot.SetParent(space, false);
                        camera.transform.SetParent(_mpEyePivot, false);
                        MobPlanLevelEye();
                        line = "mobplan eye level: the view is held level (pitch and roll taken away, yaw and position kept) every frame; "
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
                double t = Time.unscaledTimeAsDouble - _mpEyeStart;
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
                if (_mpEyeCallback != null) Application.onBeforeRender -= _mpEyeCallback;
                _mpEyeCallback = null;
                _mpEyeRows?.Dispose();
                _mpEyeRows = null;
            }
        }
    }
}
