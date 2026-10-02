using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR;
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
    /// driver's application, and the frame's Update and BeforeRender, focus, pause, background and XR display.
    /// <para>
    /// Off unless the Player is started with <c>-zantetsuViewDiag &lt;new directory&gt;</c>: then nothing is created at all.
    /// When on it enables no action, writes no transform, moves no focus and starts no check, synthetic input or capture.
    /// Samples go to preallocated buffers on the main thread; a writer thread formats and appends them, a buffer a
    /// second, to new files (an existing file is refused).
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
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, Argument);
            if (at < 0 || at + 1 >= args.Length || string.IsNullOrEmpty(args[at + 1]))
            {
                return;
            }

            StartedFromArgument = true;
            Start(args[at + 1]);
        }

        /// <summary>
        /// Starts recording into <paramref name="directory"/> (created; its files must not exist). Tests call it directly.
        /// The camera observed is <see cref="Camera.main"/> at the start, or <paramref name="observe"/> when a test gives one
        /// (a fixture of its own, independent of what else the session holds); the Player's start always uses Camera.main.
        /// </summary>
        public static Recorder Start(string directory, Camera observe = null)
        {
            if (Active != null)
            {
                throw new InvalidOperationException("the view diagnosis is already recording");
            }

            var host = new GameObject("View diagnosis");
            UnityEngine.Object.DontDestroyOnLoad(host);
            Created++;
            Recorder r = host.AddComponent<Recorder>();
            try
            {
                r.Open(directory, observe);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(host);   // an existing file refused: nothing is left running
                throw;
            }

            Active = r;
            return r;
        }

        /// <summary>How many Input System actions are enabled now (read only; for checking that the diagnosis enables none).</summary>
        public static int EnabledActionCount() => InputSystem.ListEnabledActions().Count;

        /// <summary>Stops and flushes the running recorder, if any.</summary>
        public static void Stop()
        {
            if (Active != null)
            {
                UnityEngine.Object.DestroyImmediate(Active.gameObject);
            }
        }

        private struct Sample
        {
            public char kind;            // I = input after-update, U = Update, B = Application.onBeforeRender
            public int frame;
            public long ticks;
            public int seq;
            public byte updateType;      // InputUpdateType for kind I
            public bool focused;
            public bool hmdFound, hmdEnabled;
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
            public Quaternion camRot;
            public Vector3 camPos;
        }

        public sealed class Recorder : MonoBehaviour
        {
            private const int BufferSize = 8192;
            private Sample[] _buffer = new Sample[BufferSize];
            private int _count;
            private int _seq;
            private readonly ConcurrentQueue<Sample[]> _full = new ConcurrentQueue<Sample[]>();
            private readonly ConcurrentQueue<int> _fullCounts = new ConcurrentQueue<int>();
            private readonly ConcurrentQueue<string> _events = new ConcurrentQueue<string>();
            private readonly ConcurrentBag<Sample[]> _spare = new ConcurrentBag<Sample[]>();
            private readonly AutoResetEvent _wake = new AutoResetEvent(false);
            private Thread _writer;
            private volatile bool _closing;
            private FileStream _samples, _state;
            private long _start;
            private double _nextState;
            private int _dropped;

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

            public string Directory { get; private set; }

            /// <summary>The camera this recorder reads (null when there was none): the one its "found" line names, with its instance id.</summary>
            public Camera ObservedCamera => _camera;

            internal void Open(string directory, Camera observe)
            {
                Directory = directory;
                System.IO.Directory.CreateDirectory(directory);
                _samples = new FileStream(Path.Combine(directory, "view-samples.csv"), FileMode.CreateNew, FileAccess.Write);
                _state = new FileStream(Path.Combine(directory, "view-state.txt"), FileMode.CreateNew, FileAccess.Write);
                Write(_samples, "kind,frame,ms,seq,updateType,focused,hmdFound,hmdEnabled,hmdRotX,hmdRotY,hmdRotZ,hmdRotW,hmdPosX,hmdPosY,hmdPosZ,hmdTracking,hmdLastUpdate,"
                    + "rotActEnabled,rotActControls,rotActX,rotActY,rotActZ,rotActW,posActEnabled,posActX,posActY,posActZ,tsActEnabled,tsActControls,tsActValue,"
                    + "tpdEnabled,tpdTracking,tpdRotX,tpdRotY,tpdRotZ,tpdRotW,tpdPosX,tpdPosY,tpdPosZ,legacyValid,legacyRotX,legacyRotY,legacyRotZ,legacyRotW,legacyPosX,legacyPosY,legacyPosZ,legacyTracking,"
                    + "camFound,camRotX,camRotY,camRotZ,camRotW,camPosX,camPosY,camPosZ,camToTpdDeg,camToHmdDeg\n");
                _start = Stopwatch.GetTimestamp();
                for (int i = 0; i < 3; i++) _spare.Add(new Sample[BufferSize]);
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "View diagnosis writer" };
                _writer.Start();
                Event("start: directory " + directory + ", Application.runInBackground " + Application.runInBackground + ", isFocused " + Application.isFocused
                      + ", Input System backgroundBehavior " + InputSystem.settings.backgroundBehavior + ", Unity " + Application.unityVersion);
                Debug.Log("[view diagnosis] on: " + directory);

                // After the scene's objects: the driver has subscribed already (its OnEnable). Only reading from here.
                InputSystem.onAfterUpdate += AfterInputUpdate;
                InputSystem.onDeviceChange += DeviceChange;
                Application.onBeforeRender += BeforeRender;
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
                var b = new StringBuilder("found: ").Append(observe != null ? "the camera given" : "Camera.main").Append(' ')
                    .Append(_camera != null ? _camera.name + " (instance " + _camera.GetInstanceID().ToString(CultureInfo.InvariantCulture) + ")" : "none")
                    .Append(", TrackedPoseDriver ").Append(_tpd != null ? "yes" : "no");
                if (_tpd != null)
                {
                    b.Append(" (enabled ").Append(_tpd.enabled).Append(", trackingType ").Append(_tpd.trackingType).Append(", updateType ").Append(_tpd.updateType)
                        .Append(", ignoreTrackingState ").Append(_tpd.ignoreTrackingState).Append(")");
                    Describe(b, "position", _tpd.positionInput);
                    Describe(b, "rotation", _tpd.rotationInput);
                    Describe(b, "trackingState", _tpd.trackingStateInput);
                }

                b.Append("; driver fields read: tracking ").Append(_fTracking != null).Append(", rotation ").Append(_fRot != null).Append(", position ").Append(_fPos != null);
                Event(b.ToString());
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
                Event("device " + change + ": " + device.name + " id " + device.deviceId + " (" + device.layout + ") enabled " + device.enabled + " canRunInBackground " + device.canRunInBackground);
                if (device is UnityEngine.InputSystem.XR.XRHMD || ReferenceEquals(device, _hmd)) FindHmd();
            }

            private void OnApplicationFocus(bool focus) => Event("OnApplicationFocus " + focus + " (isFocused " + Application.isFocused + ")");

            private void OnApplicationPause(bool pause)
            {
                _paused = pause;
                Event("OnApplicationPause " + pause);
            }

            private void AfterInputUpdate()
            {
                _inputUpdates++;
                Take('I', (byte)InputState.currentUpdateType);
            }

            private void BeforeRender()
            {
                _beforeRenders++;
                Take('B', 0);
            }

            private void Update()
            {
                _updates++;
                Take('U', 0);
                Mark();
                double now = Seconds();
                if (now >= _nextState)
                {
                    _nextState = now + 1.0;
                    StateLine(now);
                    Hand();
                }
            }

            private void Take(char kind, byte updateType)
            {
                if (_buffer == null) return;
                if (_count == _buffer.Length) Hand();
                ref Sample s = ref _buffer[_count++];
                s = default;
                s.kind = kind;
                s.frame = Time.frameCount;
                s.ticks = Stopwatch.GetTimestamp() - _start;
                s.seq = ++_seq;
                s.updateType = updateType;
                s.focused = Application.isFocused;
                if (_hmd != null)
                {
                    s.hmdFound = true;
                    s.hmdEnabled = _hmd.enabled;
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

                if (kind == 'U')
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
                    Transform c = _camera.transform;
                    s.camRot = c.localRotation;
                    s.camPos = c.localPosition;
                }
            }

            // The person's own marks of what they meant to do (TL 2026-10-02): each press of the left controller's X
            // (primary) button, read by the legacy XR API -- which goes on while the window is out of focus (lv113411) and
            // which the game does not use -- is one numbered "mark" line with its time and frame. What the window's focus
            // really did is the separate OnApplicationFocus line; the two are compared afterwards, not merged.
            private bool _markDown;
            private int _marks;

            private void Mark()
            {
                XRInputDevice left = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
                bool down = left.isValid && left.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool pressed) && pressed;
                if (down && !_markDown)
                {
                    _marks++;
                    Event("mark " + _marks + ": left X pressed (isFocused " + Application.isFocused + ")");
                }

                _markDown = down;
            }

            private void StateLine(double now)
            {
                _displays.Clear();
                SubsystemManager.GetSubsystems(_displays);
                var b = new StringBuilder();
                b.Append("state t ").Append(now.ToString("F3", CultureInfo.InvariantCulture)).Append(" frame ").Append(Time.frameCount)
                    .Append(" counts update ").Append(_updates).Append(" beforeRender ").Append(_beforeRenders).Append(" inputAfterUpdate ").Append(_inputUpdates)
                    .Append(" | focused ").Append(Application.isFocused).Append(" paused ").Append(_paused).Append(" runInBackground ").Append(Application.runInBackground)
                    .Append(" | XR displays");
                b.Append(' ').Append(_displays.Count);
                foreach (XRDisplaySubsystem d in _displays) b.Append(" (running ").Append(d.running).Append(')');
                b.Append(" XRSettings.isDeviceActive ").Append(XRSettings.isDeviceActive).Append(" loadedDevice ").Append(XRSettings.loadedDeviceName);
                b.Append(" | Camera.main ").Append(Camera.main != null ? Camera.main.name : "none").Append(" | cameras");
                foreach (Camera c in Camera.allCameras)
                {
                    b.Append(' ').Append(c.name).Append(" (enabled ").Append(c.enabled).Append(" stereo ").Append(c.stereoEnabled).Append(" target ").Append(c.stereoTargetEye)
                        .Append(" texture ").Append(c.targetTexture != null ? c.targetTexture.name : "none").Append(" depth ").Append(c.depth.ToString(CultureInfo.InvariantCulture)).Append(')');
                }

                _legacy.Clear();
                InputDevices.GetDevicesAtXRNode(XRNode.Head, _legacy);
                b.Append(" | legacy head devices ").Append(_legacy.Count);
                foreach (XRInputDevice d in _legacy) b.Append(' ').Append(d.name).Append(" valid ").Append(d.isValid);
                b.Append(" | input HMD ").Append(_hmd != null ? _hmd.name + " id " + _hmd.deviceId + " enabled " + _hmd.enabled + " canRunInBackground " + _hmd.canRunInBackground : "none");
                if (_tpd != null)
                {
                    b.Append(" | driver enabled ").Append(_tpd.isActiveAndEnabled);
                    InputAction rot = _tpd.rotationInput.action;
                    if (rot != null) { b.Append(" rotation action enabled ").Append(rot.enabled).Append(" phase ").Append(rot.phase).Append(" controls ").Append(rot.controls.Count); foreach (InputControl c in rot.controls) b.Append(' ').Append(c.path).Append(" device enabled ").Append(c.device.enabled); }
                }

                if (_dropped > 0) b.Append(" | samples dropped ").Append(_dropped);
                Event(b.ToString());
            }

            private void Hand()
            {
                if (_count == 0) return;
                if (!_spare.TryTake(out Sample[] next))
                {
                    // The writer is behind: this buffer's samples are dropped (counted) rather than waiting for it.
                    _dropped += _count;
                    _count = 0;
                    Event("writer behind: " + BufferSize + " samples' buffer dropped");
                    return;
                }

                _full.Enqueue(_buffer);
                _fullCounts.Enqueue(_count);
                _buffer = next;
                _count = 0;
                _wake.Set();
            }

            private void Event(string line) => _events.Enqueue("t " + Seconds().ToString("F4", CultureInfo.InvariantCulture) + " frame " + Time.frameCount + " " + line);

            private double Seconds() => (double)(Stopwatch.GetTimestamp() - _start) / Stopwatch.Frequency;

            private void WriterLoop()
            {
                var b = new StringBuilder(1 << 20);
                while (true)
                {
                    _wake.WaitOne(500);
                    while (_events.TryDequeue(out string e)) Write(_state, e + "\n");
                    while (_full.TryDequeue(out Sample[] buf) && _fullCounts.TryDequeue(out int n))
                    {
                        b.Clear();
                        for (int i = 0; i < n; i++) Row(b, ref buf[i]);
                        Write(_samples, b.ToString());
                        _spare.Add(buf);
                    }

                    _samples.Flush();
                    _state.Flush();
                    if (_closing && _full.IsEmpty && _events.IsEmpty) return;
                }
            }

            private static void Row(StringBuilder b, ref Sample s)
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                void F(float v) => b.Append(',').Append(v.ToString("R", inv));
                void Q(Quaternion q) { F(q.x); F(q.y); F(q.z); F(q.w); }
                void V(Vector3 v) { F(v.x); F(v.y); F(v.z); }
                void B(bool v) => b.Append(',').Append(v ? '1' : '0');
                void I(int v) => b.Append(',').Append(v.ToString(inv));
                b.Append(s.kind).Append(',').Append(s.frame.ToString(inv)).Append(',').Append((s.ticks * 1000.0 / Stopwatch.Frequency).ToString("F3", inv)).Append(',').Append(s.seq.ToString(inv));
                I(s.updateType); B(s.focused); B(s.hmdFound); B(s.hmdEnabled); Q(s.hmdRot); V(s.hmdPos); I(s.hmdTracking); b.Append(',').Append(s.hmdLastUpdate.ToString("F4", inv));
                B(s.rotActEnabled); I(s.rotActControls); Q(s.rotActValue); B(s.posActEnabled); V(s.posActValue); B(s.tsActEnabled); I(s.tsActControls); I(s.tsActValue);
                B(s.tpdEnabled); I(s.tpdTracking); Q(s.tpdRot); V(s.tpdPos);
                B(s.legacyValid); Q(s.legacyRot); V(s.legacyPos); I(s.legacyTracking);
                B(s.camFound); Q(s.camRot); V(s.camPos);
                F(s.camFound ? Quaternion.Angle(s.camRot, s.tpdRot) : float.NaN);
                F(s.camFound && s.hmdFound ? Quaternion.Angle(s.camRot, s.hmdRot) : float.NaN);
                b.Append('\n');
            }

            private static void Write(FileStream f, string text)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(text);
                f.Write(bytes, 0, bytes.Length);
            }

            private void OnDestroy()
            {
                InputSystem.onAfterUpdate -= AfterInputUpdate;
                InputSystem.onDeviceChange -= DeviceChange;
                Application.onBeforeRender -= BeforeRender;
                if (_writer != null)
                {
                    StateLine(Seconds());
                    Event("stop: updates " + _updates + ", beforeRenders " + _beforeRenders + ", input after-updates " + _inputUpdates + ", samples dropped " + _dropped);
                    Hand();
                    _closing = true;
                    _wake.Set();
                    if (!_writer.Join(3000)) Debug.LogWarning("[view diagnosis] the writer did not finish within 3 s");
                    _writer = null;
                }

                _samples?.Dispose();
                _state?.Dispose();
                _samples = null;
                _state = null;
                if (ReferenceEquals(Active, this)) Active = null;
            }
        }
    }
}
