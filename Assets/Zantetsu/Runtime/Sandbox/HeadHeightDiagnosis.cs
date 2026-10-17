using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using Zantetsu.Core;
using Debug = UnityEngine.Debug;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// DIAGNOSIS ONLY (DESIGN 4.6, unit 6-A, 2026-10-17: the view 30-50 cm higher after the headset is put on again).
    /// Records, without changing anything, where the head's height comes from along its whole way, at each transition
    /// and for a short window after it (<see cref="HeadHeightTrace"/>): the XR session's state and focus, the
    /// application's own pause and focus, the pause's reasons; the tracking origin as the input subsystem reports it
    /// and as the XR Origin holds it, with its camera Y offset and every trackingOriginUpdated; the head as the XR
    /// input device gives it (valid, tracked, tracking flags, the raw position and whether it was read), as the
    /// camera's TrackedPoseDriver holds it, and as the XR node states give the head and the eyes; the transforms of
    /// the XR Origin, the Camera Offset and the camera (local and world); and the eyes' world positions from the
    /// stereo view matrices of the last render. One log line a record; the clock is the Stopwatch's, which goes on
    /// while the game is paused. Nothing is written to any transform, origin or subsystem.
    /// <para>
    /// On only with the Player argument <see cref="Argument"/> (a normal launch, the distribution's, creates nothing).
    /// The 6-B comparison's treatments (a Floor re-application, the plugin's RefreshRecenterSpace) were tried from
    /// here on 2026-10-17 and removed again: neither restored the height, and the Refresh turned the space (record
    /// perf/height/HEIGHT-1.md §5.1). The ALPHA takes the shift as an accepted observation (D-242): no correction, no
    /// guidance to the player; what the manual recentre did on one device is in the record only.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrder)]
    public sealed class HeadHeightDiagnosis : MonoBehaviour
    {
        public const string Prefix = "HEAD HEIGHT: ";
        public const int ExecutionOrder = 32000;   // after the pose driver's Update and the locomotion: the frame's final transforms

        /// <summary>The Player argument that turns the diagnosis on; without it nothing is created (a normal launch records nothing of this).</summary>
        public const string Argument = "-zantetsuHeadHeightDiag";

        public static HeadHeightDiagnosis Instance { get; private set; }

        /// <summary>Whether the arguments ask for the diagnosis.</summary>
        public static bool AskedFor(string[] arguments) => arguments != null && System.Array.IndexOf(arguments, Argument) >= 0;

        /// <summary>Makes the diagnosis once, and only when the Player was started with <see cref="Argument"/>; null otherwise.</summary>
        public static HeadHeightDiagnosis InstallIfAsked()
        {
            return AskedFor(System.Environment.GetCommandLineArgs()) ? Install() : null;
        }

        public static HeadHeightDiagnosis Install()
        {
            if (Instance != null) return Instance;
            var host = new GameObject("Zantetsu head height diagnosis");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            Instance = host.AddComponent<HeadHeightDiagnosis>();
            return Instance;
        }

        private struct Sample
        {
            public int frame;
            public double seconds;
            public int session;
            public bool focused, xrActive, appPaused, appFocus;
            public GamePauseReason pause;
            public TrackingOriginModeFlags subMode;
            public bool originFound;
            public TrackingOriginModeFlags xroMode;
            public XROrigin.TrackingOriginMode xroRequested;
            public float yOffset;
            public int originUpdates;
            public bool headValid, headTracked, headPosRead, centerEyeRead;
            public int headFlags;
            public Vector3 headPos, centerEye;
            public bool tpdFound, tpdEnabled;
            public int tpdTracking;
            public Vector3 tpdPos;
            public bool nodeHead, nodeCenter, nodeLeft, nodeRight;           // tracked
            public float nodeHeadY, nodeCenterY, nodeLeftY, nodeRightY;
            public bool cameraFound, parentIsOffset;
            public Vector3 originPos, offsetLocal, cameraLocal, cameraWorld, cameraEuler;
            public float originYaw, originScaleY, offsetWorldY;
            public bool stereo;
            public float eyeLeftY, eyeRightY;
        }

        private readonly HeadHeightTrace _trace = new HeadHeightTrace();
        private readonly Sample[] _before = new Sample[HeadHeightTrace.BeforeFrames];
        private int _beforeNext;
        private readonly StringBuilder _b = new StringBuilder(640);
        private readonly List<XRInputSubsystem> _inputs = new List<XRInputSubsystem>();
        private readonly HashSet<XRInputSubsystem> _subscribed = new HashSet<XRInputSubsystem>();
        private readonly List<XRNodeState> _nodes = new List<XRNodeState>();
        private long _start;
        private Camera _camera;
        private XROrigin _origin;
        private TrackedPoseDriver _tpd;
        private FieldInfo _fTracking, _fPos;
        private bool _started;
        private int _originUpdates;
        private bool _appPaused, _appFocus = true;
        private Sample _last;
        private bool _hasLast;

        private double Seconds => (double)(Stopwatch.GetTimestamp() - _start) / Stopwatch.Frequency;

        private void Awake()
        {
            _start = Stopwatch.GetTimestamp();
            InputTracking.trackingAcquired += TrackingAcquired;
            InputTracking.trackingLost += TrackingLost;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            _fTracking = typeof(TrackedPoseDriver).GetField("m_CurrentTrackingState", flags);
            _fPos = typeof(TrackedPoseDriver).GetField("m_CurrentPosition", flags);
        }

        private void OnDestroy()
        {
            InputTracking.trackingAcquired -= TrackingAcquired;
            InputTracking.trackingLost -= TrackingLost;
            foreach (XRInputSubsystem s in _subscribed) s.trackingOriginUpdated -= TrackingOriginUpdated;
            _subscribed.Clear();
            if (Instance == this) Instance = null;
        }

        private void OnApplicationPause(bool pause)
        {
            _appPaused = pause;
            Event("OnApplicationPause " + pause);
        }

        private void OnApplicationFocus(bool focus)
        {
            _appFocus = focus;
            Event("OnApplicationFocus " + focus);
        }

        private void TrackingAcquired(XRNodeState node) => Event("trackingAcquired " + node.nodeType);

        private void TrackingLost(XRNodeState node) => Event("trackingLost " + node.nodeType);

        private void TrackingOriginUpdated(XRInputSubsystem subsystem)
        {
            _originUpdates++;
            Transform offset = _origin != null && _origin.CameraFloorOffsetObject != null ? _origin.CameraFloorOffsetObject.transform : null;
            Event("trackingOriginUpdated #" + _originUpdates + " subsystem=" + subsystem.GetTrackingOriginMode()
                + " xrOrigin=" + (_origin != null ? _origin.CurrentTrackingOriginMode.ToString() : "none")
                + " offsetLocalY=" + (offset != null ? F(offset.localPosition.y) : "-"));
        }

        private void Find()
        {
            if (_camera == null)
            {
                _camera = Camera.main;
                _tpd = _camera != null ? _camera.GetComponent<TrackedPoseDriver>() : null;
            }

            if (_origin == null)
            {
                _origin = FindFirstObjectByType<XROrigin>();
            }

            SubsystemManager.GetSubsystems(_inputs);
            foreach (XRInputSubsystem s in _inputs)
            {
                if (_subscribed.Add(s))
                {
                    s.trackingOriginUpdated += TrackingOriginUpdated;
                }
            }
        }

        private void LateUpdate()
        {
            Find();
            Sample s = Take();
            if (!_started)
            {
                _started = true;
                Debug.Log(Prefix + "listening; camera=" + (_camera != null ? _camera.name : "none") + " driver=" + (_tpd != null ? "yes" : "no")
                    + " xrOrigin=" + (_origin != null ? _origin.name + " requested=" + _origin.RequestedTrackingOriginMode + " yOffset=" + F(_origin.CameraYOffset) : "none")
                    + " inputSubsystems=" + _inputs.Count + " (window " + HeadHeightTrace.WindowFrames + " frames, jump " + F(HeadHeightTrace.JumpMeters) + " m, steady " + HeadHeightTrace.SteadySeconds + " s; the clock goes on while paused)"
                    + "; AllowRecentering=" + OpenXRSettings.AllowRecentering + " FloorOffset=" + F(OpenXRSettings.FloorOffset) + " (read only: nothing of the origin is touched)");
            }

            // Transitions read from the frame itself (the callbacks above are the others).
            if (_hasLast)
            {
                if (s.session != _last.session) Event("session " + XrSessionStateReport.NameOf(_last.session) + "->" + XrSessionStateReport.NameOf(s.session));
                if (s.focused != _last.focused) Event("sessionFocused " + s.focused);
                if (s.xrActive != _last.xrActive) Event("xrActive " + s.xrActive);
                if (s.subMode != _last.subMode) Event("subsystem origin mode " + _last.subMode + "->" + s.subMode);
                if (s.xroMode != _last.xroMode) Event("XR Origin current mode " + _last.xroMode + "->" + s.xroMode);
                if (s.yOffset != _last.yOffset) Event("XR Origin camera Y offset " + F(_last.yOffset) + "->" + F(s.yOffset));
                if ((s.offsetLocal - _last.offsetLocal).sqrMagnitude > 1e-6f) Event("Camera Offset local " + V(_last.offsetLocal) + "->" + V(s.offsetLocal));
                if (s.headTracked != _last.headTracked) Event("head isTracked " + s.headTracked);
                if (s.headFlags != _last.headFlags) Event("head tracking flags " + _last.headFlags + "->" + s.headFlags);
                if (s.headValid != _last.headValid) Event("head device valid " + s.headValid);
                if (s.pause != _last.pause) Event("pause reasons " + _last.pause + "->" + s.pause);
                if (s.parentIsOffset != _last.parentIsOffset) Event("camera parent is the Camera Offset: " + s.parentIsOffset);
            }

            _last = s;
            _hasLast = true;

            if (_trace.Sample(s.frame, s.seconds, s.headTracked, s.cameraLocal.y, out string why))
            {
                if (why == "jump")
                {
                    Debug.Log(Prefix + "event jump: camera local y " + F(_before[(_beforeNext + HeadHeightTrace.BeforeFrames - 1) % HeadHeightTrace.BeforeFrames].cameraLocal.y) + "->" + F(s.cameraLocal.y) + " (frame " + s.frame + ")");
                    FlushBefore();
                }

                Debug.Log(Format(why, ref s));
            }
            else
            {
                _before[_beforeNext] = s;
                _beforeNext = (_beforeNext + 1) % HeadHeightTrace.BeforeFrames;
            }

            if (_trace.Settled(s.frame, out float mean, out int count))
            {
                Debug.Log(Prefix + "settled: camera local y mean " + (count > 0 ? F(mean) + " over " + count + " tracked frames" : "no tracked frame in the window")
                    + "; now " + F(s.cameraLocal.y) + " world " + F(s.cameraWorld.y) + " offsetLocalY " + F(s.offsetLocal.y) + " originY " + F(s.originPos.y)
                    + " subsystem=" + s.subMode + " xrOrigin=" + s.xroMode + " session=" + XrSessionStateReport.NameOf(s.session) + " (frame " + s.frame + ")");
            }
        }

        private void Event(string what)
        {
            int frame = Time.frameCount;
            bool opened = _trace.Trigger(frame);
            Debug.Log(Prefix + "event " + what + " (frame " + frame + ", t " + Seconds.ToString("F3", CultureInfo.InvariantCulture) + ")");
            if (opened) FlushBefore();
        }

        // The last frames before a transition, kept but not recorded until one comes.
        private void FlushBefore()
        {
            for (int i = 0; i < HeadHeightTrace.BeforeFrames; i++)
            {
                Sample s = _before[(_beforeNext + i) % HeadHeightTrace.BeforeFrames];
                if (s.frame > 0) Debug.Log(Format("before", ref s));
                _before[(_beforeNext + i) % HeadHeightTrace.BeforeFrames] = default;
            }
        }

        private Sample Take()
        {
            var s = new Sample
            {
                frame = Time.frameCount,
                seconds = Seconds,
                session = XrSessionStateReport.State,
                xrActive = XRSettings.isDeviceActive,
                appPaused = _appPaused,
                appFocus = _appFocus,
                pause = GamePause.Requested,
                originUpdates = _originUpdates,
                headFlags = -1,
                tpdTracking = -1,
            };
            string loader = s.xrActive ? XRSettings.loadedDeviceName ?? "" : "";
            bool openXr = s.xrActive && loader.IndexOf("OpenXR", System.StringComparison.OrdinalIgnoreCase) >= 0;
            s.focused = openXr && OpenXRUtility.IsSessionFocused;
            s.subMode = _inputs.Count > 0 ? _inputs[0].GetTrackingOriginMode() : TrackingOriginModeFlags.Unknown;

            if (_origin != null)
            {
                s.originFound = true;
                s.xroMode = _origin.CurrentTrackingOriginMode;
                s.xroRequested = _origin.RequestedTrackingOriginMode;
                s.yOffset = _origin.CameraYOffset;
                Transform o = _origin.Origin != null ? _origin.Origin.transform : _origin.transform;
                s.originPos = o.position;
                s.originYaw = o.eulerAngles.y;
                s.originScaleY = o.lossyScale.y;
                if (_origin.CameraFloorOffsetObject != null)
                {
                    Transform offset = _origin.CameraFloorOffsetObject.transform;
                    s.offsetLocal = offset.localPosition;
                    s.offsetWorldY = offset.position.y;
                    s.parentIsOffset = _camera != null && _camera.transform.parent == offset;
                }
            }

            InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (head.isValid)
            {
                s.headValid = true;
                s.headTracked = head.TryGetFeatureValue(CommonUsages.isTracked, out bool tracked) && tracked;
                s.headFlags = head.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState flags) ? (int)flags : -1;
                s.headPosRead = head.TryGetFeatureValue(CommonUsages.devicePosition, out s.headPos);
                s.centerEyeRead = head.TryGetFeatureValue(CommonUsages.centerEyePosition, out s.centerEye);
            }

            if (_tpd != null)
            {
                s.tpdFound = true;
                s.tpdEnabled = _tpd.isActiveAndEnabled;
                if (_fTracking != null) s.tpdTracking = System.Convert.ToInt32(_fTracking.GetValue(_tpd), CultureInfo.InvariantCulture);
                if (_fPos != null) s.tpdPos = (Vector3)_fPos.GetValue(_tpd);
            }

            _nodes.Clear();
            InputTracking.GetNodeStates(_nodes);
            foreach (XRNodeState n in _nodes)
            {
                if (!n.TryGetPosition(out Vector3 p)) continue;
                switch (n.nodeType)
                {
                    case XRNode.Head: s.nodeHead = n.tracked; s.nodeHeadY = p.y; break;
                    case XRNode.CenterEye: s.nodeCenter = n.tracked; s.nodeCenterY = p.y; break;
                    case XRNode.LeftEye: s.nodeLeft = n.tracked; s.nodeLeftY = p.y; break;
                    case XRNode.RightEye: s.nodeRight = n.tracked; s.nodeRightY = p.y; break;
                }
            }

            if (_camera != null)
            {
                s.cameraFound = true;
                Transform c = _camera.transform;
                s.cameraLocal = c.localPosition;
                s.cameraWorld = c.position;
                s.cameraEuler = c.eulerAngles;
                s.stereo = _camera.stereoEnabled;
                if (s.stereo)
                {
                    // The eyes as the last render saw them: the inverse view's translation is the eye in world space.
                    s.eyeLeftY = _camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left).inverse.GetColumn(3).y;
                    s.eyeRightY = _camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right).inverse.GetColumn(3).y;
                }
            }

            return s;
        }

        private string Format(string why, ref Sample s)
        {
            StringBuilder b = _b;
            b.Clear();
            b.Append(Prefix).Append(why).Append(" f=").Append(s.frame).Append(" t=").Append(s.seconds.ToString("F3", CultureInfo.InvariantCulture));
            b.Append(" | session=").Append(XrSessionStateReport.NameOf(s.session)).Append(" focused=").Append(B(s.focused)).Append(" appPaused=").Append(B(s.appPaused)).Append(" appFocus=").Append(B(s.appFocus))
                .Append(" xr=").Append(B(s.xrActive)).Append(" pause=").Append(s.pause);
            b.Append(" | origin: subsystem=").Append(s.subMode);
            if (s.originFound) b.Append(" xrOrigin=").Append(s.xroMode).Append('/').Append(s.xroRequested).Append(" yOffset=").Append(F(s.yOffset)).Append(" updates=").Append(s.originUpdates);
            else b.Append(" xrOrigin=none");
            b.Append(" | head: valid=").Append(B(s.headValid)).Append(" tracked=").Append(B(s.headTracked)).Append(" flags=").Append(s.headFlags)
                .Append(" pos=").Append(s.headPosRead ? V(s.headPos) : "unread").Append(" centerEyeY=").Append(s.centerEyeRead ? F(s.centerEye.y) : "unread");
            b.Append(" | tpd: ").Append(s.tpdFound ? (s.tpdEnabled ? "on" : "off") + " ts=" + s.tpdTracking + " pos=" + V(s.tpdPos) : "none");
            b.Append(" | nodes y: head=").Append(F(s.nodeHeadY)).Append(T(s.nodeHead)).Append(" center=").Append(F(s.nodeCenterY)).Append(T(s.nodeCenter))
                .Append(" L=").Append(F(s.nodeLeftY)).Append(T(s.nodeLeft)).Append(" R=").Append(F(s.nodeRightY)).Append(T(s.nodeRight));
            b.Append(" | rig: origin=").Append(V(s.originPos)).Append(" yaw=").Append(s.originYaw.ToString("F1", CultureInfo.InvariantCulture)).Append(" scaleY=").Append(F(s.originScaleY))
                .Append(" offsetLocal=").Append(V(s.offsetLocal)).Append(" offsetWorldY=").Append(F(s.offsetWorldY)).Append(" parentIsOffset=").Append(B(s.parentIsOffset));
            b.Append(" | camera: ").Append(s.cameraFound ? "local=" + V(s.cameraLocal) + " world=" + V(s.cameraWorld) + " euler=" + V(s.cameraEuler) : "none");
            b.Append(" | eyes(last render) y: ").Append(s.stereo ? "L=" + F(s.eyeLeftY) + " R=" + F(s.eyeRightY) : "no stereo");
            return b.ToString();
        }

        private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
        private static string V(Vector3 v) => "(" + F(v.x) + "," + F(v.y) + "," + F(v.z) + ")";
        private static string B(bool v) => v ? "1" : "0";
        private static string T(bool tracked) => tracked ? "" : "(untracked)";
    }
}
