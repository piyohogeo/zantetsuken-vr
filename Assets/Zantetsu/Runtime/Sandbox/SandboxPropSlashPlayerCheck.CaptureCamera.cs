using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    // The game's view and the pictures apart (TL, 2026-10-01/02). Every MobPlan run records the game's camera at the walk's
    // start and reads every frame after that the game still reads it, untouched (GameViewGuard): Camera.main, the katana's
    // view reference, the pose level of detail's cameras and the pieces' lifetime camera; its tag, stereo target and
    // target; its parent the original Camera Offset until the check's own eye level hangs it under its pivot (the one
    // sanctioned re-parenting, noted where it is made). Image runs only ("-zantetsuCaptureCamera <dir>", with
    // "-zantetsuCaptureView back,up,pitch,fov" and "-zantetsuCaptureFrames n"): a capture camera of the check's own beside
    // it (CheckCaptureCamera, viewing only) and the sink that saves it (CheckFrameSink), saving from the required section's
    // start; drained before the ending without waiting on the Main thread (docs/diagnostics/check-capture-2026-10-02.md).
    // Without the argument nothing is made -- no camera, no sink, no thread -- and that is checked too.
    public static partial class SandboxPropSlashPlayerCheck
    {
        public const string CaptureCameraArgument = "-zantetsuCaptureCamera";
        public const string CaptureViewArgument = "-zantetsuCaptureView";
        public const string CaptureFramesArgument = "-zantetsuCaptureFrames";
        public const string CaptureShotsArgument = "-zantetsuCaptureShots";     // name:x,y,z,lookX,lookY,lookZ,fov;... poses fixed in the world
        public const string CaptureScheduleArgument = "-zantetsuCaptureSchedule";   // section=i;replay=i;t<seconds>=i;... (t: the script's time); a switch waits for no drop running
        public const string CaptureSizeArgument = "-zantetsuCaptureSize";     // width,height

        private sealed partial class Walk
        {
            internal string captureDirectory;
            internal float captureBack = 6f, captureUp = 4f, capturePitch = 10f, captureFov = 70f;
            internal int captureFrames = 4000;
            internal int captureWidth = 1280, captureHeight = 720;
            internal List<CheckCaptureCamera.Shot> captureShots;
            internal readonly List<(string when, double at, int shot)> captureSchedule = new List<(string, double, int)>();
            private int _captureNext;   // the schedule's next entry
            private int _captureSwitchesDelayed;

            private CheckCaptureCamera _capture;
            private CheckFrameSink _captureSink;
            private string _captureAtDrain;   // the camera as it stood when the drain began
            private bool _captureTaken, _captureDrained;
            private GameViewGuard _gameView;
            private int _camerasAtBegin = -1;
            private PoseLodDirector[] _poseLods = Array.Empty<PoseLodDirector>();
            private readonly List<Camera> _poseLodCameras = new List<Camera>();
            private readonly Plane[] _gvPlanes = new Plane[VpInstanceCulling.EyePlaneCount * 2];
            private Action _captureOnSection, _captureOnReplay;

            private static readonly System.Reflection.FieldInfo s_katanaView = typeof(SandboxRightHandKatana).GetField("viewForwardReference", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            private static readonly System.Reflection.FieldInfo s_poseLodView = typeof(PoseLodDirector).GetField("viewCamera", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            internal void ParseCaptureCamera()
            {
                string[] a = Environment.GetCommandLineArgs();
                int at = Array.IndexOf(a, CaptureCameraArgument);
                if (at < 0 || at + 1 >= a.Length) return;
                captureDirectory = a[at + 1];
                int v = Array.IndexOf(a, CaptureViewArgument);
                if (v >= 0 && v + 1 < a.Length)
                {
                    string[] p = a[v + 1].Split(',');
                    if (p.Length == 4)
                    {
                        captureBack = float.Parse(p[0], CultureInfo.InvariantCulture); captureUp = float.Parse(p[1], CultureInfo.InvariantCulture);
                        capturePitch = float.Parse(p[2], CultureInfo.InvariantCulture); captureFov = float.Parse(p[3], CultureInfo.InvariantCulture);
                    }
                }

                int x = Array.IndexOf(a, CaptureShotsArgument);
                if (x >= 0 && x + 1 < a.Length)
                {
                    captureShots = new List<CheckCaptureCamera.Shot>();
                    foreach (string shot in a[x + 1].Split(';'))
                    {
                        string[] nv = shot.Split(':');
                        string[] p = nv[1].Split(',');
                        float F(int i) => float.Parse(p[i], CultureInfo.InvariantCulture);
                        captureShots.Add(new CheckCaptureCamera.Shot { name = nv[0], position = new Vector3(F(0), F(1), F(2)), lookAt = new Vector3(F(3), F(4), F(5)), fieldOfView = F(6) });
                    }
                }

                int y = Array.IndexOf(a, CaptureScheduleArgument);
                if (y >= 0 && y + 1 < a.Length)
                {
                    foreach (string entry in a[y + 1].Split(';'))
                    {
                        string[] kv = entry.Split('=');
                        int shot = int.Parse(kv[1], CultureInfo.InvariantCulture);
                        if (kv[0].StartsWith("t")) captureSchedule.Add(("t", double.Parse(kv[0].Substring(1), CultureInfo.InvariantCulture), shot));
                        else captureSchedule.Add((kv[0], 0.0, shot));
                    }
                }

                int z = Array.IndexOf(a, CaptureSizeArgument);
                if (z >= 0 && z + 1 < a.Length)
                {
                    string[] p = a[z + 1].Split(',');
                    if (p.Length == 2) { captureWidth = int.Parse(p[0], CultureInfo.InvariantCulture); captureHeight = int.Parse(p[1], CultureInfo.InvariantCulture); }
                }

                int f = Array.IndexOf(a, CaptureFramesArgument);
                if (f >= 0 && f + 1 < a.Length && int.TryParse(a[f + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0) captureFrames = n;
            }

            private Transform KatanaView() => _katana != null && s_katanaView != null ? (Transform)s_katanaView.GetValue(_katana) : null;

            /// <summary>The walk's start, before the head wait: the game's view recorded; in an image run, the capture camera made beside it.</summary>
            private void CaptureBegin()
            {
                if (!MobPlanMode) return;
                Camera game = Camera.main;
                _gameView = new GameViewGuard(game);
                _camerasAtBegin = Camera.allCamerasCount;
                _poseLods = UnityEngine.Object.FindObjectsByType<PoseLodDirector>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                Transform katanaView = KatanaView();
                Log("game view at the walk's start: camera " + (game != null ? game.name : "NONE") + " (parent " + (_gameView.OriginalParent != null ? _gameView.OriginalParent.name : "none") + ", tag " + (game != null ? game.tag : "-") + ", stereo " + (game != null ? game.stereoTargetEye.ToString() : "-")
                    + "); cameras " + _camerasAtBegin + "; pose level-of-detail directors " + _poseLods.Length + "; the katana's view reference " + (katanaView != null ? katanaView.name : "none") + "; the lifetime's camera " + (_world != null && _world.Lifetime != null && _world.Lifetime.ViewCamera != null ? _world.Lifetime.ViewCamera.name : "none (Camera.main)"));
                if (captureDirectory == null)
                {
                    Log("capture camera: off (no " + CaptureCameraArgument + "): nothing made");
                    return;
                }

                if (_world == null || game == null) { Log("capture camera: NOT made (no world or no game camera)"); return; }
                _captureSink = new CheckFrameSink("capture camera", captureDirectory, captureWidth, captureHeight, captureFrames, new CheckJpegFrameWriter(85));
                _capture = new CheckCaptureCamera(_world, game, _captureSink, captureBack, captureUp, capturePitch, captureFov, captureWidth, captureHeight, captureShots);
                _captureOnSection = () => { _captureSink?.StartSaving(); CaptureEvent("section"); };
                _captureOnReplay = () => { _captureSink?.StartSaving(); CaptureEvent("replay"); };
                Log("capture camera schedule: " + string.Join("; ", captureSchedule.Select(e => (e.when == "t" ? "the script's t " + e.at.ToString("R", Inv) + " s" : e.when) + " -> " + (captureShots != null && e.shot < captureShots.Count ? captureShots[e.shot].name : "#" + e.shot)))
                    + " (a switch at a time waits until no drop runs)");
                MidDropSectionBegun += _captureOnSection;
                ReplayBegun += _captureOnReplay;
                Log("capture camera (image run): made beside the game's camera, untagged, mono, back " + captureBack.ToString("R", Inv) + " m, up " + captureUp.ToString("R", Inv) + " m, pitched up " + capturePitch.ToString("R", Inv) + " deg, fov " + captureFov.ToString("R", Inv)
                    + ", at most " + captureFrames + " frames to " + captureDirectory + " from the required section's start (or the replay's); " + _capture.Describe() + "; Camera.main still " + (Camera.main != null ? Camera.main.name : "NONE"));
            }

            // An event's switch (the section's start, the replay's): at once -- no drop runs then.
            private void CaptureEvent(string when)
            {
                if (_capture == null) return;
                while (_captureNext < captureSchedule.Count && captureSchedule[_captureNext].when == when)
                {
                    _capture.SetShot(captureSchedule[_captureNext].shot, when);
                    _captureNext++;
                }
            }

            // Each frame: a switch at the script's time, when its time has come and no drop of the building is running.
            private void CaptureScheduleFrame()
            {
                if (_capture == null || !_replaying || _captureNext >= captureSchedule.Count || captureSchedule[_captureNext].when != "t") return;
                double t = Time.unscaledTimeAsDouble - _clockStart;
                if (t < captureSchedule[_captureNext].at) return;
                if (_world != null && _world.Hulls != null && _world.Hulls.AnimationsRunning > 0) { _captureSwitchesDelayed++; return; }   // never in the middle of a drop
                _capture.SetShot(captureSchedule[_captureNext].shot, "the script's t " + t.ToString("F3", Inv) + " s (due " + captureSchedule[_captureNext].at.ToString("R", Inv) + " s; frames waited for a drop " + _captureSwitchesDelayed + ")");
                _captureSwitchesDelayed = 0;
                _captureNext++;
            }

            /// <summary>The check's own eye level hung the game camera under its pivot: the one sanctioned re-parenting.</summary>
            private void GameViewEyeLevel(Transform pivot)
            {
                if (_gameView == null) return;
                _gameView.NoteEyeLevelReparent(pivot);
                Log("game view: " + _gameView.Reparents[_gameView.Reparents.Count - 1] + " -- the eye level takes pitch and roll away (the view's orientation changes by design): a viewing condition the measurement and the pictures share, not a capture camera's change");
            }

            /// <summary>Every frame: what the game reads, and its camera as recorded.</summary>
            private void GameViewFrame()
            {
                if (_gameView == null || _gameView.Game == null) return;
                _poseLodCameras.Clear();
                foreach (PoseLodDirector d in _poseLods)
                {
                    if (d == null) continue;
                    var own = s_poseLodView != null ? (Camera)s_poseLodView.GetValue(d) : null;
                    _poseLodCameras.Add(own != null ? own : Camera.main);
                }

                Camera lifetime = _world != null && _world.Lifetime != null ? (_world.Lifetime.ViewCamera != null ? _world.Lifetime.ViewCamera : Camera.main) : _gameView.Game;
                _gameView.Frame(KatanaView(), _poseLodCameras, lifetime);
                CaptureScheduleFrame();
                // Samples of what they read: the camera's position and its eyes' planes (the lifetime's frustum), now and then.
                if (_gameView.Frames == 1 || _gameView.Frames % 2000 == 0)
                {
                    Camera game = _gameView.Game;
                    int planes = VpInstanceCulling.GetEyePlanes(game, _gvPlanes);
                    Log("game view frame " + Time.frameCount + ": camera " + game.name + " at " + game.transform.position.ToString("F3") + " facing " + game.transform.forward.ToString("F3") + ", stereo enabled " + game.stereoEnabled + ", the eyes' planes " + planes
                        + (_capture != null ? "; " + _capture.Describe() : "; no capture camera"));
                }
            }

            /// <summary>
            /// Before the ending (the ordinary one): the capture camera given back at once, and every sink still live finished --
            /// frames are let run (the Main thread never waits inside one) until each has settled or its own deadline passed.
            /// </summary>
            private IEnumerator CaptureDrain()
            {
                if (_capture != null)
                {
                    _captureAtDrain = _capture.Describe();
                    _captureTaken = _capture.TakenByDisplay;
                    _capture.Dispose();
                }

                var sinks = new List<CheckFrameSink>(CheckFrameSink.Live);
                if (sinks.Count == 0) yield break;
                int from = Time.frameCount;
                foreach (CheckFrameSink s in sinks) s.BeginFinish("the run's ending");
                while (sinks.Any(s => !s.Settled && !s.DeadlineExceeded)) yield return null;
                _captureDrained = true;
                foreach (CheckFrameSink s in sinks) Log("capture sink drained over " + (Time.frameCount - from) + " frames: " + s.Describe());
            }

            /// <summary>An early ending: every sink still live cancelled, the capture camera given back; nothing waited for.</summary>
            private void CaptureCancel()
            {
                _capture?.Dispose();
                foreach (CheckFrameSink s in new List<CheckFrameSink>(CheckFrameSink.Live))
                {
                    s.Cancel("the run ended early");
                    Log("capture sink cancelled at an early ending (not waited for): " + s.Describe());
                }
            }

            /// <summary>The run's summary: the game's view untouched, nothing made without the argument, the capture camera given back and its sink settled.</summary>
            private void CaptureClose()
            {
                if (!MobPlanMode) return;
                if (_captureOnSection != null) MidDropSectionBegun -= _captureOnSection;
                if (_captureOnReplay != null) ReplayBegun -= _captureOnReplay;
                if (_capture != null)
                {
                    CheckFrameSink s = _captureSink;
                    CheckFrameLedger l = s.ledger;
                    string before = _captureAtDrain ?? _capture.Describe();
                    Log("capture camera at the drain: " + before);
                    Log("capture camera at the end: " + _capture.Describe());
                    foreach (string sw in _capture.Switches) Log("capture camera switch: " + sw);
                    if (_capture.shots.Count > 0)
                        Expect(_capture.Rendered > 0 && _capture.MaxFixedOffset < 1e-4 && _capture.MaxFixedTurn < 1e-3, "[capture] the capture camera stayed where each shot fixed it, whatever the head did (" + before + ")");
                    Expect(_capture.ListedWithDrawing && _captureTaken, "[capture] the capture camera was drawn for by the display (" + before + ")");
                    Expect(_captureDrained && s.Now == CheckFrameSink.State.Finished && !s.DeadlineExceeded && s.Released && l.Requested > 0 && l.Written > 0 && l.Errors == 0 && l.Cancelled == 0 && l.AllAccounted,
                        "[capture] the sink finished within its deadline and released everything; every save asked ended written or skipped by its bounds, none in error or cancelled (" + s.Describe() + ")");
                    _capture = null;
                    _captureSink = null;
                }
                else
                {
                    bool none = GameObject.Find("Check Capture Camera") == null;
                    Expect(none && Camera.allCamerasCount == _camerasAtBegin && CheckFrameSink.LiveSinks == 0 && CheckFrameSink.LiveWorkers == 0,
                        "[capture] without " + CaptureCameraArgument + " no capture camera, sink or thread was made (cameras " + _camerasAtBegin + " -> " + Camera.allCamerasCount + ", sinks " + CheckFrameSink.LiveSinks + ", writer threads " + CheckFrameSink.LiveWorkers + ")");
                }

                if (_gameView == null) return;
                Log("game view " + _gameView.Describe());
                Expect(_gameView.Passed, "[game view] at every frame the game read its own XR camera, untouched by the pictures: Camera.main, the katana's view, the pose level of detail and the pieces' lifetime; its tag, stereo target and target; its parent the Camera Offset until the eye level's own pivot (" + _gameView.Frames + " frames)");
            }
        }
    }
}
