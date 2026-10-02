using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR;
using Zantetsu.Core;
using Debug = UnityEngine.Debug;
using InputDevice = UnityEngine.InputSystem.InputDevice;
using XRInputDevice = UnityEngine.XR.InputDevice;
using TrackedPoseDriver = UnityEngine.InputSystem.XR.TrackedPoseDriver;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// DIAGNOSIS ONLY (2026-10-02, the Link view that did not follow the head, ln105635): records, without changing anything,
    /// the head pose along its whole way -- the runtime's two input paths (the Input System's XR HMD and the legacy XR
    /// API), the actions the camera's <see cref="TrackedPoseDriver"/> really reads (enabled, resolved control and device,
    /// value, tracking state) and the driver's own current pose and tracking state, the camera's local pose read after the
    /// driver's application, and the frame's Update and BeforeRender, focus, pause, device changes and XR display.
    /// <para>
    /// Off unless the Player is started with <c>-zantetsuViewDiag</c>: then nothing is created at all. When on it enables
    /// no action, writes no transform, moves no focus and starts no check, synthetic input or capture. Every sample and
    /// event is one record of the development logger (DESIGN 21.17, writer "ViewDiagnosis"), its value the field names and
    /// values in turn; the tag tells the stage: inputAfterUpdate, update, beforeRender (samples), start, found, focus,
    /// pause, device, mark, state, and the summary that ends the record.
    /// </para>
    /// <para>
    /// The record has an explicit end (<see cref="Recorder.End"/>: on <see cref="Stop"/>, at the recorder's destruction, or
    /// at the application's quit request, before the logger's own session stops at quitting): from the start to it every
    /// record tried has its recording (the session's count of recorders) and seq (1..N, one per record tried), the attempts, acceptances and refusals by reason are counted,
    /// the summary is the last record, and nothing is recorded after it. The logger is best effort (accepted is not saved):
    /// a record is complete only if the saved file holds every seq from 1 to the summary, checked afterwards against the
    /// "[view diagnosis] ended" line of the log, which does not depend on the logger.
    /// </para>
    /// <para>
    /// seconds: the Stopwatch (QueryPerformanceCounter) clock from the recorder's start. A sample's seconds and eventFrame
    /// are read when the sample is taken, before its values; an event's when it is made.
    /// </para>
    /// <para>
    /// "After the driver's application": the driver applies its pose in <see cref="InputSystem.onAfterUpdate"/>. This
    /// records in the same callback, subscribed after the driver's own (the driver subscribes in its OnEnable, at the
    /// scene's load; this subscribes after the scene is loaded), and each sample keeps both the camera's local pose and
    /// the driver's current pose, so whether the camera holds the driver's pose at that point is read from the record
    /// itself, not assumed from the order.
    /// </para>
    /// </summary>
    public static class ViewDiagnosis
    {
        public const string Argument = "-zantetsuViewDiag";
        internal const string Writer = "ViewDiagnosis";

        /// <summary>Hosts made in this session (0 while off).</summary>
        public static int Created { get; private set; }

        /// <summary>The running recorder, or null.</summary>
        public static Recorder Active { get; private set; }

        /// <summary>Whether this session's start found the argument and began recording (false while off).</summary>
        public static bool StartedFromArgument { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            Created = 0;
            Active = null;
            StartedFromArgument = false;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            if (Array.IndexOf(Environment.GetCommandLineArgs(), Argument) < 0)
            {
                return;
            }

            StartedFromArgument = true;
            Start();
        }

        /// <summary>
        /// Starts recording. Tests call it directly. The camera observed is <see cref="Camera.main"/> at the start, or
        /// <paramref name="observe"/> when a test gives one (a fixture of its own, independent of what else the session
        /// holds); the Player's start always uses Camera.main.
        /// </summary>
        public static Recorder Start(Camera observe = null)
        {
            if (Active != null)
            {
                throw new InvalidOperationException("the view diagnosis is already recording");
            }

            var host = new GameObject("View diagnosis");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Created++;
            Recorder r = host.AddComponent<Recorder>();
            r.Open(Created, observe);
            Active = r;
            return r;
        }

        /// <summary>How many Input System actions are enabled now (read only; for checking that the diagnosis enables none).</summary>
        public static int EnabledActionCount() => InputSystem.ListEnabledActions().Count;

        /// <summary>Ends the running recorder's record and destroys it, if any.</summary>
        public static void Stop()
        {
            if (Active != null)
            {
                UnityEngine.Object.DestroyImmediate(Active.gameObject);
            }
        }

        private struct Sample
        {
            public string tag;           // inputAfterUpdate, update, beforeRender (Application.onBeforeRender)
            public int frame;
            public long ticks;
            public int sample;           // the samples' own count, 1.. (the record's seq counts every record)
            public byte updateType;      // InputUpdateType for inputAfterUpdate
            public bool focused;
            public bool hmdFound, hmdEnabled;
            public int hmdDeviceId;
            public Quaternion hmdRot;
            public Vector3 hmdPos;
            public int hmdTracking;
            public double hmdLastUpdate;
            public bool rotActEnabled, posActEnabled, tsActEnabled;
            public int rotActControls, tsActControls;
            public Quaternion rotActValue;
            public Vector3 posActValue;
            public int tsActValue;
            public bool tpdEnabled;
            public int tpdTracking;
            public Quaternion tpdRot;
            public Vector3 tpdPos;
            public bool legacyValid;
            public Quaternion legacyRot;
            public Vector3 legacyPos;
            public int legacyTracking;
            public bool camFound;
            public int camInstance;
            public Quaternion camRot;
            public Vector3 camPos;
        }

        public sealed class Recorder : MonoBehaviour
        {
            private int _recording;   // which recorder of the session (ViewDiagnosis.Created): each has its own seq 1..N
            private long _start;
            private double _nextState;
            private int _sampleCount;

            private Camera _camera;
            private TrackedPoseDriver _tpd;
            private FieldInfo _fTracking, _fRot, _fPos;
            private InputDevice _hmd;
            private QuaternionControl _hmdRotControl;
            private Vector3Control _hmdPosControl;
            private IntegerControl _hmdTrackingControl;
            private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>();
            private readonly List<XRInputDevice> _legacy = new List<XRInputDevice>();
            private bool _paused;
            private int _updates, _beforeRenders, _inputUpdates;

            // The record's own bookkeeping: seq of the last record tried, what the logger accepted and refused, the end.
            private long _seq, _accepted, _queueFull, _unavailable, _invalid, _disabled;
            private bool _ended;
            // The record's own cost on the main thread (Stopwatch ticks): every write_log call, and every sample from its first
            // read to its record's return; and the bytes the thread allocated in the write_log calls, where the runtime counts
            // them (GC.GetAllocatedBytesForCurrentThread; 0 where it does not, so 0 is not read as "nothing allocated").
            private long _writeTicks, _writeMax, _sampleTicks, _sampleMax, _writeAllocated;
            private readonly object[] _sampleValues = new object[Prefix + 2 * SampleFieldCount];

            /// <summary>The camera this recorder reads (null when there was none): the one its "found" record names, with its instance id.</summary>
            public Camera ObservedCamera => _camera;

            /// <summary>Records tried: the seq of the last one.</summary>
            public long Attempted => _seq;

            /// <summary>Records the logger accepted (queued -- not yet known to be saved).</summary>
            public long Accepted => _accepted;

            /// <summary>Whether the record was ended: nothing is recorded after it.</summary>
            public bool Ended => _ended;

            /// <summary>Samples taken, by stage (the summary's counts).</summary>
            public int Updates => _updates;
            public int BeforeRenders => _beforeRenders;
            public int InputAfterUpdates => _inputUpdates;

            internal void Open(int recording, Camera observe)
            {
                _recording = recording;
                _start = Stopwatch.GetTimestamp();
                Event("start", new object[]
                {
                    "runInBackground", B(Application.runInBackground), "isFocused", B(Application.isFocused),
                    "backgroundBehavior", InputSystem.settings.backgroundBehavior.ToString(), "unityVersion", Application.unityVersion,
                });
                Debug.Log("[view diagnosis] on: recording " + _recording + ", records of writer " + Writer + " to the development logger");

                // After the scene's objects: the driver has subscribed already (its OnEnable). Only reading from here.
                InputSystem.onAfterUpdate += AfterInputUpdate;
                InputSystem.onDeviceChange += DeviceChange;
                Application.onBeforeRender += BeforeRender;
                Application.wantsToQuit += EndOnQuitRequest;
                Find(observe);
            }

            private void Find(Camera observe)
            {
                _camera = observe != null ? observe : Camera.main;
                _tpd = _camera != null ? _camera.GetComponent<TrackedPoseDriver>() : null;
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                _fTracking = typeof(TrackedPoseDriver).GetField("m_CurrentTrackingState", flags);
                _fRot = typeof(TrackedPoseDriver).GetField("m_CurrentRotation", flags);
                _fPos = typeof(TrackedPoseDriver).GetField("m_CurrentPosition", flags);
                FindHmd();
                var b = new StringBuilder("TrackedPoseDriver ").Append(_tpd != null ? "yes" : "no");
                if (_tpd != null)
                {
                    b.Append(" (enabled ").Append(_tpd.enabled).Append(", trackingType ").Append(_tpd.trackingType).Append(", updateType ").Append(_tpd.updateType)
                        .Append(", ignoreTrackingState ").Append(_tpd.ignoreTrackingState).Append(")");
                    Describe(b, "position", _tpd.positionInput);
                    Describe(b, "rotation", _tpd.rotationInput);
                    Describe(b, "trackingState", _tpd.trackingStateInput);
                }

                b.Append("; driver fields read: tracking ").Append(_fTracking != null).Append(", rotation ").Append(_fRot != null).Append(", position ").Append(_fPos != null);
                Event("found", new object[]
                {
                    "source", observe != null ? "the camera given" : "Camera.main",
                    "camera", _camera != null ? _camera.name : null, "cameraInstance", _camera != null ? (object)_camera.GetInstanceID() : null,
                    "hmd", _hmd != null ? _hmd.name : null, "hmdDeviceId", _hmd != null ? (object)_hmd.deviceId : null,
                    "driver", b.ToString(),
                });
            }

            private static void Describe(StringBuilder b, string what, InputActionProperty p)
            {
                InputAction a = p.action;
                b.Append("; ").Append(what).Append(" action ");
                if (a == null) { b.Append("none"); return; }
                b.Append('"').Append(a.name).Append("\" reference ").Append(p.reference != null).Append(" enabled ").Append(a.enabled).Append(" bindings ");
                foreach (InputBinding x in a.bindings) b.Append(x.effectivePath).Append(' ');
                b.Append("controls ").Append(a.controls.Count);
                foreach (InputControl c in a.controls) b.Append(' ').Append(c.path).Append(" (device ").Append(c.device.name).Append(" id ").Append(c.device.deviceId).Append(" enabled ").Append(c.device.enabled).Append(')');
            }

            private void FindHmd()
            {
                _hmd = null;
                _hmdRotControl = null;
                _hmdPosControl = null;
                _hmdTrackingControl = null;
                foreach (InputDevice d in InputSystem.devices)
                {
                    if (d is UnityEngine.InputSystem.XR.XRHMD)
                    {
                        _hmd = d;
                        _hmdRotControl = d.TryGetChildControl<QuaternionControl>("centerEyeRotation");
                        _hmdPosControl = d.TryGetChildControl<Vector3Control>("centerEyePosition");
                        _hmdTrackingControl = d.TryGetChildControl<IntegerControl>("trackingState");
                        break;
                    }
                }
            }

            private void DeviceChange(InputDevice device, InputDeviceChange change)
            {
                Event("device", new object[]
                {
                    "change", change.ToString(), "device", device.name, "deviceId", device.deviceId, "layout", device.layout,
                    "enabled", B(device.enabled), "canRunInBackground", B(device.canRunInBackground),
                });
                if (device is UnityEngine.InputSystem.XR.XRHMD || ReferenceEquals(device, _hmd)) FindHmd();
            }

            private void OnApplicationFocus(bool focus) => Event("focus", new object[] { "focus", B(focus), "isFocused", B(Application.isFocused) });

            private void OnApplicationPause(bool pause)
            {
                _paused = pause;
                Event("pause", new object[] { "pause", B(pause) });
            }

            private void AfterInputUpdate()
            {
                _inputUpdates++;
                Take("inputAfterUpdate", (byte)InputState.currentUpdateType);
            }

            private void BeforeRender()
            {
                _beforeRenders++;
                Take("beforeRender", 0);
            }

            private void Update()
            {
                _updates++;
                Take("update", 0);
                Mark();
                double now = Seconds();
                if (now >= _nextState)
                {
                    _nextState = now + 1.0;
                    StateLine(Stopwatch.GetTimestamp() - _start);
                }
            }

            private void Take(string tag, byte updateType)
            {
                if (_ended) return;
                long begin = Stopwatch.GetTimestamp();
                var s = new Sample
                {
                    tag = tag,
                    frame = Time.frameCount,
                    ticks = Stopwatch.GetTimestamp() - _start,
                    sample = ++_sampleCount,
                    updateType = updateType,
                    focused = Application.isFocused,
                    hmdDeviceId = -1,
                    camInstance = 0,
                };
                if (_hmd != null)
                {
                    s.hmdFound = true;
                    s.hmdEnabled = _hmd.enabled;
                    s.hmdDeviceId = _hmd.deviceId;
                    s.hmdLastUpdate = _hmd.lastUpdateTime;
                    if (_hmdRotControl != null) s.hmdRot = _hmdRotControl.ReadValue();
                    if (_hmdPosControl != null) s.hmdPos = _hmdPosControl.ReadValue();
                    s.hmdTracking = _hmdTrackingControl != null ? _hmdTrackingControl.ReadValue() : -1;
                }

                if (_tpd != null)
                {
                    s.tpdEnabled = _tpd.isActiveAndEnabled;
                    InputAction rot = _tpd.rotationInput.action, pos = _tpd.positionInput.action, ts = _tpd.trackingStateInput.action;
                    if (rot != null) { s.rotActEnabled = rot.enabled; s.rotActControls = rot.controls.Count; s.rotActValue = rot.ReadValue<Quaternion>(); }
                    if (pos != null) { s.posActEnabled = pos.enabled; s.posActValue = pos.ReadValue<Vector3>(); }
                    if (ts != null) { s.tsActEnabled = ts.enabled; s.tsActControls = ts.controls.Count; s.tsActValue = ts.ReadValue<int>(); }
                    s.tpdTracking = _fTracking != null ? Convert.ToInt32(_fTracking.GetValue(_tpd), CultureInfo.InvariantCulture) : -1;
                    if (_fRot != null) s.tpdRot = (Quaternion)_fRot.GetValue(_tpd);
                    if (_fPos != null) s.tpdPos = (Vector3)_fPos.GetValue(_tpd);
                }

                if (tag == "update")
                {
                    XRInputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
                    if (head.isValid)
                    {
                        s.legacyValid = true;
                        if (!head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyeRotation, out s.legacyRot)) head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.deviceRotation, out s.legacyRot);
                        if (!head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.centerEyePosition, out s.legacyPos)) head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.devicePosition, out s.legacyPos);
                        s.legacyTracking = head.TryGetFeatureValue(UnityEngine.XR.CommonUsages.trackingState, out InputTrackingState t) ? (int)t : -1;
                    }
                }

                if (_camera != null)
                {
                    s.camFound = true;
                    s.camInstance = _camera.GetInstanceID();
                    Transform c = _camera.transform;
                    s.camRot = c.localRotation;
                    s.camPos = c.localPosition;
                }

                WriteSample(ref s);
                long took = Stopwatch.GetTimestamp() - begin;
                _sampleTicks += took;
                if (took > _sampleMax) _sampleMax = took;
            }

            // The fields of a sample record after seq, eventFrame and seconds, in this order.
            private const int SampleFieldCount = 57;

            private void WriteSample(ref Sample s)
            {
                object[] v = _sampleValues;
                int i = Prefix;
                void P(string key, object value) { v[i++] = key; v[i++] = value; }
                void Q(string key, Quaternion q) { P(key + "X", q.x); P(key + "Y", q.y); P(key + "Z", q.z); P(key + "W", q.w); }
                void V(string key, Vector3 p) { P(key + "X", p.x); P(key + "Y", p.y); P(key + "Z", p.z); }
                P("sample", s.sample); P("updateType", (int)s.updateType); P("focused", B(s.focused));
                P("hmdFound", B(s.hmdFound)); P("hmdEnabled", B(s.hmdEnabled)); P("hmdDeviceId", s.hmdDeviceId); Q("hmdRot", s.hmdRot); V("hmdPos", s.hmdPos);
                P("hmdTracking", s.hmdTracking); P("hmdLastUpdate", s.hmdLastUpdate);
                P("rotActEnabled", B(s.rotActEnabled)); P("rotActControls", s.rotActControls); Q("rotAct", s.rotActValue);
                P("posActEnabled", B(s.posActEnabled)); V("posAct", s.posActValue);
                P("tsActEnabled", B(s.tsActEnabled)); P("tsActControls", s.tsActControls); P("tsActValue", s.tsActValue);
                P("tpdEnabled", B(s.tpdEnabled)); P("tpdTracking", s.tpdTracking); Q("tpdRot", s.tpdRot); V("tpdPos", s.tpdPos);
                P("legacyValid", B(s.legacyValid)); Q("legacyRot", s.legacyRot); V("legacyPos", s.legacyPos); P("legacyTracking", s.legacyTracking);
                P("camFound", B(s.camFound)); P("camInstance", s.camInstance); Q("camRot", s.camRot); V("camPos", s.camPos);
                P("camToTpdDeg", s.camFound ? (object)Quaternion.Angle(s.camRot, s.tpdRot) : null);
                P("camToHmdDeg", s.camFound && s.hmdFound ? (object)Quaternion.Angle(s.camRot, s.hmdRot) : null);
                if (i != v.Length) throw new InvalidOperationException("the sample's field count is " + (i - Prefix) / 2 + ", not " + SampleFieldCount);
                Write(s.tag, s.frame, s.ticks, v);
            }

            // The person's own marks of what they meant to do (TL 2026-10-02): each press of the left controller's X
            // (primary) button, read by the legacy XR API -- which goes on while the window is out of focus (lv113411) and
            // which the game does not use -- is one numbered "mark" record with its time and frame. What the window's focus
            // really did is the separate focus record; the two are compared afterwards, not merged.
            private bool _markDown;
            private int _marks;

            private void Mark()
            {
                XRInputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                MarkEdge(left.isValid && left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool pressed) && pressed);
            }

            /// <summary>One reading of the X button: a press (up to down) is a mark. Tests give the reading directly.</summary>
            internal void MarkEdge(bool down)
            {
                if (down && !_markDown)
                {
                    _marks++;
                    Event("mark", new object[] { "mark", _marks, "button", "left X", "isFocused", B(Application.isFocused) });
                }

                _markDown = down;
            }

            private void StateLine(long ticks)
            {
                _displays.Clear();
                SubsystemManager.GetSubsystems(_displays);
                var running = new StringBuilder();
                foreach (XRDisplaySubsystem d in _displays) running.Append(running.Length > 0 ? " " : "").Append(d.running);
                var cameras = new StringBuilder();
                foreach (Camera c in Camera.allCameras)
                {
                    cameras.Append(cameras.Length > 0 ? " " : "").Append(c.name).Append(" (instance ").Append(c.GetInstanceID()).Append(" enabled ").Append(c.enabled).Append(" stereo ").Append(c.stereoEnabled)
                        .Append(" target ").Append(c.stereoTargetEye).Append(" texture ").Append(c.targetTexture != null ? c.targetTexture.name : "none").Append(" depth ").Append(c.depth.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                _legacy.Clear();
                InputDevices.GetDevicesAtXRNode(XRNode.Head, _legacy);
                var legacy = new StringBuilder();
                foreach (XRInputDevice d in _legacy) legacy.Append(legacy.Length > 0 ? " " : "").Append(d.name).Append(" valid ").Append(d.isValid);
                string driver = null;
                if (_tpd != null)
                {
                    var b = new StringBuilder("enabled ").Append(_tpd.isActiveAndEnabled);
                    InputAction rot = _tpd.rotationInput.action;
                    if (rot != null) { b.Append(" rotation action enabled ").Append(rot.enabled).Append(" phase ").Append(rot.phase).Append(" controls ").Append(rot.controls.Count); foreach (InputControl c in rot.controls) b.Append(' ').Append(c.path).Append(" device enabled ").Append(c.device.enabled); }
                    driver = b.ToString();
                }

                Camera main = Camera.main;
                Event("state", ticks, new object[]
                {
                    "updates", _updates, "beforeRenders", _beforeRenders, "inputAfterUpdates", _inputUpdates,
                    "focused", B(Application.isFocused), "paused", B(_paused), "runInBackground", B(Application.runInBackground),
                    "xrDisplays", _displays.Count, "xrDisplaysRunning", running.ToString(),
                    "isDeviceActive", B(XRSettings.isDeviceActive), "loadedDevice", XRSettings.loadedDeviceName,
                    "cameraMain", main != null ? main.name : null, "cameraMainInstance", main != null ? (object)main.GetInstanceID() : null, "cameras", cameras.ToString(),
                    "legacyHeadDevices", _legacy.Count, "legacyHeads", legacy.ToString(),
                    "hmd", _hmd != null ? _hmd.name : null, "hmdDeviceId", _hmd != null ? (object)_hmd.deviceId : null,
                    "hmdEnabled", _hmd != null ? (object)B(_hmd.enabled) : null, "hmdCanRunInBackground", _hmd != null ? (object)B(_hmd.canRunInBackground) : null,
                    "driver", driver, "refusedSoFar", _queueFull + _unavailable + _invalid + _disabled,
                });
            }

            /// <summary>
            /// Ends the record (once): a last state record, then the summary as the last record; nothing is recorded after it,
            /// though the recorder may still be there. The summary record's counts are of the records before it (seq 1..N-1);
            /// the summary itself is seq N. The same counts, the summary included (attempted N, accepted and refused over
            /// seq 1..N), go to the log, which does not depend on the logger: what the saved file is checked against.
            /// </summary>
            public void End()
            {
                if (_ended) return;
                StateLine(Stopwatch.GetTimestamp() - _start);
                // The cost of the records before the summary, the same in the summary record and in the log line.
                long writes = _seq;
                string cost = "write_log " + writes + " calls " + R(Sec(_writeTicks)) + " s (max " + R(Sec(_writeMax)) + " s), samples " + R(Sec(_sampleTicks))
                    + " s (max " + R(Sec(_sampleMax)) + " s), write_log allocated bytes " + _writeAllocated + " (0 = not counted by this runtime)";
                Event("summary", new object[]
                {
                    "updates", _updates, "beforeRenders", _beforeRenders, "inputAfterUpdates", _inputUpdates, "marks", _marks,
                    "writeSeconds", Sec(_writeTicks), "writeMaxSeconds", Sec(_writeMax), "sampleSeconds", Sec(_sampleTicks), "sampleMaxSeconds", Sec(_sampleMax),
                    "writeAllocatedBytes", _writeAllocated,
                    "attemptedBefore", _seq, "acceptedBefore", _accepted, "queueFull", _queueFull, "unavailable", _unavailable, "invalidValue", _invalid, "disabled", _disabled,
                });
                _ended = true;
                Debug.Log("[view diagnosis] ended: recording " + _recording + ", attempted " + _seq + " (seq 1.." + _seq + ", the summary last), accepted " + _accepted
                    + ", refused: queue full " + _queueFull + ", unavailable " + _unavailable + ", invalid value " + _invalid + ", disabled " + _disabled
                    + "; samples update " + _updates + ", beforeRender " + _beforeRenders + ", inputAfterUpdate " + _inputUpdates + "; cost before the summary: " + cost);
            }

            private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);

            // At the quit request, before Application.quitting, where the logger's session stops: the record is ended while
            // the logger still accepts. The quit itself is not held or changed.
            internal bool EndOnQuitRequest()
            {
                End();
                return true;
            }

            private void Event(string tag, object[] fields) => Event(tag, Stopwatch.GetTimestamp() - _start, fields);

            private void Event(string tag, long ticks, object[] fields)
            {
                if (_ended) return;
                var values = new object[Prefix + fields.Length];
                Array.Copy(fields, 0, values, Prefix, fields.Length);
                Write(tag, Time.frameCount, ticks, values);
            }

            // One record: the recording, its seq, the frame and seconds of the moment given, then the fields already in values[Prefix..].
            private const int Prefix = 8;

            private void Write(string tag, int frame, long ticks, object[] values)
            {
                values[0] = "recording";
                values[1] = _recording;
                values[2] = "seq";
                values[3] = ++_seq;
                values[4] = "eventFrame";
                values[5] = frame;
                values[6] = "seconds";
                values[7] = (double)ticks / Stopwatch.Frequency;
                long begin = Stopwatch.GetTimestamp();
                long allocated = GC.GetAllocatedBytesForCurrentThread();
#if DEBUG
                switch (DevelopmentLogger.Instance.write_log(Writer, tag, values))
                {
                    case DevelopmentLogResult.Accepted: _accepted++; break;
                    case DevelopmentLogResult.QueueFull: _queueFull++; break;
                    case DevelopmentLogResult.Unavailable: _unavailable++; break;
                    case DevelopmentLogResult.InvalidValue: _invalid++; break;
                    default: _disabled++; break;
                }
#else
                _disabled++;
#endif
                _writeAllocated += GC.GetAllocatedBytesForCurrentThread() - allocated;
                long took = Stopwatch.GetTimestamp() - begin;
                _writeTicks += took;
                if (took > _writeMax) _writeMax = took;
            }

            private static double Sec(long ticks) => (double)ticks / Stopwatch.Frequency;

            private static int B(bool value) => value ? 1 : 0;

            private double Seconds() => (double)(Stopwatch.GetTimestamp() - _start) / Stopwatch.Frequency;

            private void OnDestroy()
            {
                InputSystem.onAfterUpdate -= AfterInputUpdate;
                InputSystem.onDeviceChange -= DeviceChange;
                Application.onBeforeRender -= BeforeRender;
                Application.wantsToQuit -= EndOnQuitRequest;
                End();
                if (ReferenceEquals(Active, this)) Active = null;
            }
        }
    }
}
