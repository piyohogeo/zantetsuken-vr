using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// IMAGE RUNS ONLY (TL, 2026-10-01/02): a camera of the check's own for pictures, beside the game's XR camera, which it
    /// never touches (its parent, transform, tag, tracking, stereo settings, and whatever reads it). Mono, untagged; either
    /// it follows the head (set back, up, pitched up), or it stands at one of a few poses fixed in the world (shots), switched
    /// by its caller -- never moved between switches. It renders into its own texture each frame the pipeline renders,
    /// registered with the world's camera drawing and display like any camera there (nothing of the world is updated or built
    /// again for it).
    /// <para>
    /// It only views (docs/diagnostics/check-capture-2026-10-02.md): each render's texture, frame and real time are offered
    /// to a <see cref="CheckFrameSink"/>, which decides and saves. <see cref="Dispose"/> unregisters and destroys the camera
    /// at once and hands its texture to the sink, released there once no readback of it is in flight.
    /// </para>
    /// </summary>
    public sealed class CheckCaptureCamera : IDisposable
    {
        public sealed class Shot
        {
            public string name;
            public Vector3 position, lookAt;
            public float fieldOfView;
        }

        public readonly float back, up, pitch, fieldOfView;
        public readonly int width, height;
        public readonly IReadOnlyList<Shot> shots;   // empty: it follows the head
        public readonly CheckFrameSink sink;         // null: it only views

        public Camera Camera { get; private set; }
        public RenderTexture Target { get; private set; }
        public int Rendered { get; private set; }
        public bool ListedWithDrawing { get; private set; }
        /// <summary>Whether the display holds this camera now (taken here, or by the camera drawing when it registers its list).</summary>
        public bool TakenByDisplay => Camera != null && _world != null && _world.Display != null && !_world.Display.IsDisposed && _world.Display.TryGetCameraStencil(Camera, out _, out _);
        public int ShotIndex { get; private set; } = -1;
        public string ShotName => ShotIndex >= 0 ? shots[ShotIndex].name : "head";
        public readonly List<string> Switches = new List<string>();
        /// <summary>Fixed shots: the farthest the camera stood from its shot's pose at any render (m, degrees).</summary>
        public double MaxFixedOffset { get; private set; }
        public double MaxFixedTurn { get; private set; }

        private readonly CutWorldRoot _world;
        private readonly Camera _game;
        private readonly CutWorldCameraDrawing _drawing;
        private readonly Camera[] _drawingCamerasBefore;
        private bool _registeredHere;
        private readonly Action<ScriptableRenderContext, Camera> _endCamera;
        private readonly UnityEngine.Events.UnityAction _beforeRender;

        private static readonly System.Reflection.FieldInfo s_drawingCameras = typeof(CutWorldCameraDrawing).GetField("cameras", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        private static readonly System.Reflection.FieldInfo s_drawingRegistered = typeof(CutWorldCameraDrawing).GetField("_registered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        public CheckCaptureCamera(CutWorldRoot world, Camera game, CheckFrameSink sink, float back, float up, float pitch, float fieldOfView, int width, int height,
            IReadOnlyList<Shot> shots = null)
        {
            if (sink != null && (sink.width != width || sink.height != height)) throw new ArgumentException("the sink saves " + sink.width + "x" + sink.height + ", the camera renders " + width + "x" + height);
            _world = world;
            _game = game;
            this.sink = sink;
            this.back = back; this.up = up; this.pitch = pitch; this.fieldOfView = fieldOfView;
            this.width = width; this.height = height;
            this.shots = shots ?? Array.Empty<Shot>();

            var go = new GameObject("Check Capture Camera");   // untagged: Camera.main stays the game's
            Camera = go.AddComponent<Camera>();
            Camera.stereoTargetEye = StereoTargetEyeMask.None;
            Camera.fieldOfView = fieldOfView;
            Camera.nearClipPlane = 0.05f;
            Camera.farClipPlane = game != null ? game.farClipPlane : 1000f;
            Camera.cullingMask = game != null ? game.cullingMask : ~0;
            Camera.depth = game != null ? game.depth - 1 : -1;
            Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "Check Capture Target" };
            Camera.targetTexture = Target;
            if (this.shots.Count > 0) SetShot(0, "made"); else Place();

            // The cut display draws for the cameras its camera drawing lists and the display took: this one is added to both.
            _drawing = UnityEngine.Object.FindFirstObjectByType<CutWorldCameraDrawing>();
            if (_drawing != null && s_drawingCameras != null)
            {
                _drawingCamerasBefore = (Camera[])s_drawingCameras.GetValue(_drawing);
                s_drawingCameras.SetValue(_drawing, _drawingCamerasBefore.Concat(new[] { Camera }).ToArray());
                ListedWithDrawing = true;
            }

            // The drawing registers its list once, when the world is first ready: if it has, this camera is registered here;
            // if not yet, the drawing registers it with the rest (registering it twice would be refused).
            bool drawingRegistered = _drawing != null && s_drawingRegistered != null && (bool)s_drawingRegistered.GetValue(_drawing);
            if (!ListedWithDrawing || drawingRegistered) _registeredHere = world != null && world.Display != null && world.Display.TryRegisterCamera(Camera);
            _beforeRender = Place;
            Application.onBeforeRender += _beforeRender;
            _endCamera = OnEndCamera;
            RenderPipelineManager.endCameraRendering += _endCamera;
        }

        /// <summary>A fixed shot: placed now, and never moved until the next switch.</summary>
        public void SetShot(int index, string why)
        {
            if (Camera == null || index < 0 || index >= shots.Count || index == ShotIndex) return;
            Shot s = shots[index];
            Camera.transform.SetPositionAndRotation(s.position, Quaternion.LookRotation(s.lookAt - s.position, Vector3.up));
            Camera.fieldOfView = s.fieldOfView;
            Switches.Add("frame " + Time.frameCount + " real " + Time.realtimeSinceStartupAsDouble.ToString("R") + ": " + (ShotIndex >= 0 ? shots[ShotIndex].name : "none") + " -> " + s.name + " (" + why + ")");
            ShotIndex = index;
        }

        /// <summary>Following the head: its position and yaw as the game's camera stands now, set back, up, pitched up (the game's camera only read). A fixed shot is not moved here.</summary>
        public void Place()
        {
            if (Camera == null || shots.Count > 0 || _game == null) return;
            Transform head = _game.transform;
            Vector3 f = head.forward;
            f.y = 0f;
            f = f.sqrMagnitude > 1e-6f ? f.normalized : Vector3.forward;
            Camera.transform.SetPositionAndRotation(head.position - f * back + Vector3.up * up, Quaternion.LookRotation(f, Vector3.up) * Quaternion.Euler(-pitch, 0f, 0f));
        }

        private void OnEndCamera(ScriptableRenderContext context, Camera camera)
        {
            if (!ReferenceEquals(camera, Camera)) return;
            Rendered++;
            if (ShotIndex >= 0)
            {
                Shot s = shots[ShotIndex];
                MaxFixedOffset = Math.Max(MaxFixedOffset, (Camera.transform.position - s.position).magnitude);
                MaxFixedTurn = Math.Max(MaxFixedTurn, Quaternion.Angle(Camera.transform.rotation, Quaternion.LookRotation(s.lookAt - s.position, Vector3.up)));
            }

            sink?.Offer(Target, Time.frameCount, Time.realtimeSinceStartupAsDouble, ShotName);
        }

        public string Describe() =>
            (shots.Count > 0 ? "fixed shots [" + string.Join("; ", shots.Select(s => s.name + " at " + s.position.ToString("F1") + " -> " + s.lookAt.ToString("F1") + " fov " + s.fieldOfView)) + "], now " + (ShotIndex >= 0 ? shots[ShotIndex].name : "none")
                + " (moved within a shot at most " + MaxFixedOffset.ToString("R") + " m, turned " + MaxFixedTurn.ToString("R") + " deg); switches " + Switches.Count + "; " : "following the head; ")
            + "capture camera " + width + "x" + height + ": listed with the camera drawing " + ListedWithDrawing + ", taken by the display " + TakenByDisplay + (_registeredHere ? " (registered here)" : " (by the drawing)")
            + "; rendered " + Rendered + (sink != null ? "; saving: " + sink.Describe() : "; no sink");

        /// <summary>Unregistered and destroyed at once (never a wait); the switches written beside the pictures; the texture to the sink.</summary>
        public void Dispose()
        {
            if (Camera == null) return;
            Application.onBeforeRender -= _beforeRender;
            RenderPipelineManager.endCameraRendering -= _endCamera;
            if (_drawing != null && s_drawingCameras != null && ListedWithDrawing) s_drawingCameras.SetValue(_drawing, _drawingCamerasBefore);
            if (_world != null && _world.Display != null && !_world.Display.IsDisposed && _world.Display.TryGetCameraStencil(Camera, out _, out _)) _world.Display.TryUnregisterCamera(Camera);
            Camera.targetTexture = null;
            UnityEngine.Object.Destroy(Camera.gameObject);
            Camera = null;
            if (sink != null)
            {
                File.WriteAllLines(Path.Combine(sink.directory, "switches.txt"), Switches);
                sink.RetireSource(Target);
            }
            else if (Target != null)
            {
                Target.Release();
                UnityEngine.Object.Destroy(Target);
            }

            Target = null;
        }
    }
}
