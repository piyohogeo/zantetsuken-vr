using System;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Core;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Rendering.Urp;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Draws what a <see cref="CutWorldRoot"/>'s display settled this frame, for the cameras it is given (DESIGN 4.5.6,
    /// 5.6). It is the last step of an ordinary frame: the driver's update carries the cuts, the driver's late update
    /// settles the collection, and this — as each of its cameras begins to render, after every late update — prepares
    /// that camera's stencil arrangement from the settled collection and registers the draw.
    /// <para>
    /// **Where the draw is registered, and what was measured.** The display names the camera it draws for, because
    /// the stencil work is that camera's own. On this project (Unity 6000.3.22f1, URP 17.3, D3D11), with this
    /// display's own call naming the camera, the same commands registered from an update or from a late update were
    /// measured to put **no pixel at all** on that camera while every record said they had been drawn, and the same
    /// commands registered from the pipeline's start of that camera were measured to appear. So this registers the
    /// draw there. That measurement is of this call on this setup: it is **not** a statement about
    /// <see cref="Graphics"/> draw registration in general, about other Unity or pipeline versions, or about calls
    /// that name no camera. It is not a pass, a renderer feature or a camera manager -- it calls the same display
    /// entry, from the moment that was measured to work.
    /// </para>
    /// <para>
    /// **It only draws.** No cut is accepted here, nothing is committed, and no snapshot is settled or reconsidered: a
    /// frame that was not opened for drawing has nothing to draw, which is an ordinary state. Each camera is drawn
    /// once per frame, because this is the one place that draws.
    /// </para>
    /// <para>
    /// **The cameras are named, not found.** What is given is registered with the display while this is enabled and
    /// unregistered when it is not, so a camera that is destroyed or taken away stops being drawn for. A display that
    /// has been given up, or a world that has ended, is not touched: this stops with it.
    /// </para>
    /// <para>
    /// The materials, the stencil settings and the shadow arrangement are the display's own, from the world's profile.
    /// Nothing here is a render pipeline feature, a camera manager or a pass.
    /// </para>
    /// <para>
    /// **The cameras given send no <c>OnMouse*</c> messages.** This is where a world's cameras are named, so it is where
    /// each is told so, once, as it is registered (<see cref="CameraMouseEvents"/>): nothing in the product answers those
    /// messages, and a camera left to send them costs a physics raycast every frame. Nothing else of the camera is set.
    /// </para>
    /// <para>
    /// **A display that selects on the GPU (DESIGN 4.5.7).** Its draws read a selection that must be written first, in
    /// the same camera's frame. So for such a display this also enqueues, for the camera about to render, the pass
    /// that issues the selection inside the pipeline (<see cref="VpGpuCullCameraRoute"/>) -- before it registers the
    /// draws. A camera the pass cannot be enqueued for is not drawn for in that frame, rather than drawn from an
    /// earlier frame's selection. Nothing is read back and nothing waits.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CutWorldCameraDrawing : MonoBehaviour
    {
        [Tooltip("The world whose display is drawn. Required.")]
        [SerializeField]
        private CutWorldRoot world;

        [Tooltip("The cameras it is drawn for. Each is registered with the display while this is enabled.")]
        [SerializeField]
        private Camera[] cameras = Array.Empty<Camera>();

        [Tooltip("The layer the draws are registered on. It is the layer a camera must see for them to appear.")]
        [SerializeField]
        private int layer;

        private bool _registered;
        private int _drawnFrame;
        private int _emptyFrame;
        private VpGpuCullCameraRoute _cullRoute;
        private VpLogicalCutDisplay _cullDisplay;
        private bool _cullRefusalLogged;
        private VpNativeDrawRoute _nativeRoute;
        private bool _nativeRefusalLogged;

        // The skin draw (DESIGN 4.5.12): its own selection route (a pass of its own a camera), the frame it was last
        // updated in, and -- drawn by Unity's calls -- the skin variants of the world's materials.
        private VpGpuCullCameraRoute _skinCullRoute;
        private int _skinUpdatedFrame;
        private Material _skinForwardUnity, _skinShadowUnity;
        private bool _skinRefusalLogged;

        /// <summary>How many times a camera was not drawn the skins for because their selection could not be enqueued, or the plugin's pass was not.</summary>
        public int SkinRefusals { get; private set; }

        /// <summary>How many frames the skin draw was updated in by this drawing.</summary>
        public int SkinFramesUpdated { get; private set; }

        /// <summary>The route the GPU selection is put into the pipeline by, once a display that selects has been drawn; else null.</summary>
        public VpGpuCullCameraRoute CullRoute => _cullRoute;

        /// <summary>The route the Direct3D 12 plugin draws by (DESIGN 4.5.8), once a display drawn by it has been drawn; else null.</summary>
        public VpNativeDrawRoute NativeRoute => _nativeRoute;

        /// <summary>How many times a camera was not drawn for because its selection could not be enqueued.</summary>
        public int CullRefusals { get; private set; }

        /// <summary>How many times a camera was not drawn for because the plugin's pass could not be enqueued.</summary>
        public int NativeRefusals { get; private set; }

        /// <summary>Diagnosis only (a shot run): the plugin route's GPU-assembled constants of the last body issue for the camera; null without the route.</summary>
        public string DescribeNativeGpuBlocksForDiagnosis(Camera camera)
        {
            return _nativeRoute?.DescribeGpuBlocksForDiagnosis(camera);
        }

        /// <summary>
        /// The plugin's route in words, with whether it drew every issue it was given (no refusal, no full ring, no
        /// pipeline failure, no frame left undrawn); null when this has not drawn through the plugin.
        /// </summary>
        public string DescribeNativeRoute(out bool drewEverything)
        {
            drewEverything = false;
            if (_nativeRoute == null)
            {
                return null;
            }

            // Like with like: the skin's issues are taken and issued under their own counters (review 3).
            drewEverything = _nativeRoute.Refused == 0 && _nativeRoute.RingFull == 0 && _nativeRoute.PipelineFailures == 0
                && _nativeRoute.XrFramesNotDrawn == 0 && NativeRefusals == 0 && _nativeRoute.BodiesIssued == _nativeRoute.BodiesTaken
                && _nativeRoute.MotionIssued == _nativeRoute.MotionTaken
                && _nativeRoute.SkinBodiesIssued == _nativeRoute.SkinBodiesTaken && _nativeRoute.SkinMotionIssued == _nativeRoute.SkinMotionTaken;
            return _nativeRoute.Describe() + "; cameras not drawn for want of the plugin's pass " + NativeRefusals;
        }

        /// <summary>How many frames this has registered a draw in. It counts the drawing, not what was drawn.</summary>
        public int DrawnFrames { get; private set; }

        /// <summary>How many frames had nothing to draw: no collection had settled for them.</summary>
        public int FramesWithNothingToDraw { get; private set; }

        /// <summary>The last frame a camera could not be prepared in, or zero. A refusal draws nothing that frame.</summary>
        public int LastRefusedFrame { get; private set; }

        public void Configure(CutWorldRoot targetWorld, params Camera[] targetCameras)
        {
            if (_registered) throw new System.InvalidOperationException("Configure drawing before registration.");
            world = targetWorld; cameras = targetCameras; TryRegister();
        }

        private void OnEnable()
        {
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            TryRegister();
        }

        private void OnDisable()
        {
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            ReportCullRoute();
            ReleaseSkinUnityMaterials();
            Unregister();
        }

        // What the GPU selection did while this drew, once, for the log: a run reads it to know that it selected.
        private void ReportCullRoute()
        {
            if (_skinCullRoute != null && world != null && world.SkinDisplay != null && !world.SkinDisplay.IsDisposed)
            {
                Debug.Log("VP SKIN ROUTE (drawing): " + name + " " + world.SkinDisplay.Describe() + "; skin selection enqueued " + _skinCullRoute.Enqueued
                    + ", recorded " + _skinCullRoute.Recorded + ", refused by the display " + _skinCullRoute.Refused + ", cameras not drawn the skins for " + SkinRefusals
                    + ", frames updated " + SkinFramesUpdated, this);
                _skinCullRoute = null;
            }

            if (_nativeRoute != null)
            {
                Debug.Log("VP BODY ROUTE (end): " + name + " " + _nativeRoute.Describe() + "; cameras not drawn for want of the plugin's pass " + NativeRefusals, this);
                // The command compaction's GPU check, where the launch asked for it: its tallies read back here, once.
                string compactionCheck = _nativeRoute.ReadCompactionCheck();
                if (compactionCheck != null) Debug.Log("VP COMMAND COMPACTION (check): " + name + " " + compactionCheck, this);
                _nativeRoute.Dispose();
                _nativeRoute = null;
            }

            if (_cullRoute == null)
            {
                return;
            }

            VpLogicalCutDisplay display = _cullDisplay;
            Debug.Log(
                "VP BODY ROUTE (end): " + name + " GPU selection enqueued " + _cullRoute.Enqueued + ", recorded " + _cullRoute.Recorded
                + ", refused by the display " + _cullRoute.Refused + ", cameras not drawn for want of it " + CullRefusals
                + "; last conditions eyes " + _cullRoute.LastEyeCount + ", splits " + _cullRoute.LastSplitCount
                + "; body kept everything " + _cullRoute.BodyKeptEverything + ", casters kept everything " + _cullRoute.CastersKeptEverything
                + (_cullRoute.LastCastersKeptEverythingReason != null ? " (last: " + _cullRoute.LastCastersKeptEverythingReason + ")" : "")
                + ", frames without a main light shadow map " + _cullRoute.FramesWithoutMainShadowMap
                + (_cullRoute.LastWithoutMainShadowMapReason != null ? " (last: " + _cullRoute.LastWithoutMainShadowMapReason + ")" : "")
                + "; largest view difference from the rendered one " + _cullRoute.LargestViewDifference.ToString("G6")
                + (display != null
                    ? "; display selections issued " + display.CullSelectionsIssued + ", missed " + display.CullSelectionsMissed
                      + ", dispatches " + display.CullDispatches + ", selection bytes on the GPU " + display.CullViewBytes
                      + "; clipped instances " + (display.SeparatesClipDraws ? "apart" : "with the selection")
                      + ": in the list now " + display.ClipDrawCount + ", clip list body issues " + display.ClipBodyIssues
                      + ", clip list caster issues " + display.ClipCasterIssues + ", clip list uploads " + display.ClipListUploads
                      + "; clip route by command: commands now " + display.ClipRouteCommandCount + ", instances now " + display.ClipDrawCount
                      + ", of them with no plane " + display.ClipRoutePlanelessCount + "; most commands " + display.MostClipRouteCommands
                      + ", most instances " + display.MostClipRouteInstances + ", most with no plane " + display.MostClipRoutePlaneless
                      + "; clip selection runs " + display.ClipCullRuns + ", entries " + display.ClipCullEntries
                      + ", kept for the body " + display.ClipCullBodyKept + ", kept as casters " + display.ClipCullCasterKept
                      + ", time " + (display.ClipCullTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ms"
                      + ", clip list writes " + display.ClipArgumentWrites + " (" + display.ClipArgumentBytes + " bytes; by a selection " + display.ClipSelectionBytes + " bytes, put back whole before a frame's selection " + display.ClipRestoreBytes + " bytes)"
                    : "; no display"),
                this);
            _cullRoute = null;
            _cullDisplay = null;
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (!IsOneOfOurs(camera))
            {
                return;
            }

            if (_cullRoute != null)
            {
                _cullRoute.NoteRendered(camera);
            }

            // The view this camera was just rendered with, for the display's held placements (DESIGN 5.6, D-210): a
            // held piece near this camera is asked where it stands only if it is also in this view -- either eye's.
            // Read here, at the end of the camera's rendering, because the camera's matrices are then the ones the
            // pipeline rendered with (measured on this project: the difference the GPU selection's route reports
            // between the two is 0); the collection of the next frame runs before that frame's are made, so a
            // placement pass of that frame reads these. A camera not rendered in the frame before is asked for by
            // nearness alone. No frame is promised for a piece that comes into view: it is asked when a placement
            // pass next runs (none runs without a new step result, a change told or a structure change).
            if (_registered && world != null && world.Display != null && !world.Display.IsDisposed)
            {
                if (camera.stereoEnabled)
                {
                    world.Display.NoteCameraView(
                        camera, 2,
                        camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left),
                        camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right));
                }
                else
                {
                    Matrix4x4 worldToClip = camera.projectionMatrix * camera.worldToCameraMatrix;
                    world.Display.NoteCameraView(camera, 1, worldToClip, worldToClip);
                }
            }
        }

        /// <summary>
        /// One of this world's cameras is about to render: prepare it from the collection this frame settled and
        /// register its draw. Every other camera the pipeline renders -- another world's, a scene view, a preview --
        /// passes through untouched.
        /// </summary>
        private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
#if VP_DIAGNOSTIC_SCENE_AB
            using var measured = SceneAbCounters.Measure(2);
#endif
            if (!IsOneOfOurs(camera) || !TryRegister())
            {
                return;
            }

            VpLogicalCutDisplay display = world.Display;

            // Nothing was settled for this frame -- the world may be ending, or nothing is shown yet. It is not an
            // error and nothing is drawn.
            if (!display.IsFrameOpen)
            {
                if (_emptyFrame != Time.frameCount)
                {
                    _emptyFrame = Time.frameCount;
                    FramesWithNothingToDraw++;
                }

                return;
            }

            // The stencil arrangement of this camera's own view, from the collection that has already settled.
            // A refusal leaves that camera unprepared, and an unprepared camera is not drawn for.
            if (!display.TryPrepareCamera(camera))
            {
                LastRefusedFrame = Time.frameCount;
                return;
            }

            // The selection first: it is enqueued for this camera's frame before the draws that read it are
            // registered, and a camera it cannot be enqueued for is not drawn for.
            if (display.CullsOnGpu)
            {
                if (_cullRoute == null)
                {
                    _cullRoute = new VpGpuCullCameraRoute();
                }

                _cullDisplay = display;
                if (!_cullRoute.TryEnqueue(camera, display, out string failure))
                {
                    CullRefusals++;
                    LastRefusedFrame = Time.frameCount;
                    if (!_cullRefusalLogged)
                    {
                        _cullRefusalLogged = true;
                        Debug.LogError(
                            name + ": the GPU selection could not be put in " + camera.name + "'s frame (" + failure
                            + "); that camera is not drawn for. Told once.", this);
                    }

                    return;
                }

                // The plugin's pass (DESIGN 4.5.8) after the selection it reads, and before the draws it takes: a
                // camera it cannot be enqueued for is not drawn for either, and never by Unity's calls instead.
                if (display.NativeArguments)
                {
                    if (_nativeRoute == null)
                    {
                        _nativeRoute = new VpNativeDrawRoute();
                    }

                    if (!_nativeRoute.TryEnqueue(camera, display, out string nativeFailure))
                    {
                        NativeRefusals++;
                        LastRefusedFrame = Time.frameCount;
                        if (!_nativeRefusalLogged)
                        {
                            _nativeRefusalLogged = true;
                            Debug.LogError(
                                name + ": the plugin's draw pass could not be put in " + camera.name + "'s frame (" + nativeFailure
                                + "); that camera is not drawn for. Told once.", this);
                        }

                        return;
                    }
                }
            }

            display.Render(layer, camera);
            DrawSkins(camera, display);
            if (_drawnFrame != Time.frameCount)
            {
                _drawnFrame = Time.frameCount;
                DrawnFrames++;
            }
        }

        // The skin draw for this camera (DESIGN 4.5.12): the display is brought current once a frame (the first camera's
        // beginning: after every late update, before any camera renders), its selection is put in the camera's frame
        // by a route of its own, and its draws are registered -- with the plugin's route the display's bodies go
        // through, or by Unity's calls with the skin variants.
        private void DrawSkins(Camera camera, VpLogicalCutDisplay display)
        {
            VpSkinDisplay skins = world.SkinDisplay;
            if (skins == null || skins.IsDisposed)
            {
                return;
            }

            if (_skinUpdatedFrame != Time.frameCount)
            {
                _skinUpdatedFrame = Time.frameCount;
                SkinFramesUpdated++;
                skins.UpdateFrame(display.SinglePassInstanced);
            }

            if (skins.IndividualCount == 0)
            {
                return;
            }

            _skinCullRoute ??= new VpGpuCullCameraRoute();
            if (skins.NativeArguments)
            {
                if (_nativeRoute == null)
                {
                    RefuseSkins("the plugin's route is not drawing for this camera");
                    return;
                }

                skins.BarrierSink = _nativeRoute;
                if (!_skinCullRoute.TryEnqueue(camera, skins, out string failure))
                {
                    RefuseSkins(failure);
                    return;
                }

                skins.Render(layer, camera, _nativeRoute);
            }
            else
            {
                skins.BarrierSink = null;
                if (!_skinCullRoute.TryEnqueue(camera, skins, out string failure))
                {
                    RefuseSkins(failure);
                    return;
                }

                EnsureSkinUnityMaterials();
                skins.RenderUnity(layer, camera, _skinForwardUnity, _skinShadowUnity);
            }
        }

        private void RefuseSkins(string failure)
        {
            SkinRefusals++;
            if (!_skinRefusalLogged)
            {
                _skinRefusalLogged = true;
                Debug.LogError(name + ": the skin draw could not be put in a camera's frame (" + failure + "); the skins are not drawn for it. Told once.", this);
            }
        }

        // Unity's calls draw the skin draw by the skin variants of the world's materials: copies with VP_SKIN24, the
        // selection's and the no-clip keywords on (the Editor's checks; the plugin's route needs none).
        private void EnsureSkinUnityMaterials()
        {
            if (_skinForwardUnity == null && world.SkinDrawMaterial != null)
            {
                _skinForwardUnity = new Material(world.SkinDrawMaterial) { name = world.SkinDrawMaterial.name + " (skin draw)" };
                EnableSkinKeywords(_skinForwardUnity);
            }

            if (_skinShadowUnity == null && world.ShadowMaterial != null)
            {
                _skinShadowUnity = new Material(world.ShadowMaterial) { name = world.ShadowMaterial.name + " (skin draw)" };
                EnableSkinKeywords(_skinShadowUnity);
            }
        }

        /// <summary>The keywords of a material that draws the skin draw by Unity's calls (DESIGN 4.5.12).</summary>
        public static void EnableSkinKeywords(Material material)
        {
            material.EnableKeyword(VpGpuCullSetup.Keyword);
            material.EnableKeyword(VpGpuCullSetup.NoClipKeyword);
            material.EnableKeyword("VP_SKIN24");
        }

        private void ReleaseSkinUnityMaterials()
        {
            if (_skinForwardUnity != null) { if (Application.isPlaying) Destroy(_skinForwardUnity); else DestroyImmediate(_skinForwardUnity); }
            if (_skinShadowUnity != null) { if (Application.isPlaying) Destroy(_skinShadowUnity); else DestroyImmediate(_skinShadowUnity); }
            _skinForwardUnity = null;
            _skinShadowUnity = null;
        }

        private bool IsOneOfOurs(Camera camera)
        {
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] == camera)
                {
                    return camera != null;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether there is a world with a display to draw, registering the cameras the first time there is. A world
        /// that has ended, or a display that has been given up, is not touched again.
        /// </summary>
        private bool TryRegister()
        {
            if (world == null || world.Display == null || world.Display.IsDisposed || !world.IsReady)
            {
                _registered = false;
                return false;
            }

            if (_registered)
            {
                return true;
            }

            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] == null)
                {
                    continue;
                }

                // Once, with the registration: the camera sends no OnMouse* messages (no raycast for them each frame).
                CameraMouseEvents.TurnOff(cameras[i]);
                if (!world.Display.TryRegisterCamera(cameras[i]))
                {
                    Debug.LogError(
                        name + ": the display would not take " + cameras[i].name
                        + " -- its camera capacity is what the world's profile says.", this);
                }
            }

            // The cameras named here are the reference points of the display's held placements (DESIGN 5.6, D-205,
            // D-210): a held piece is asked where it stands again when one of them comes near it and has it in view.
            _proximity ??= AddCameraReferences;
            world.Display.PlacementProximityViews = _proximity;
            _registered = true;
            return true;
        }

        private VpLogicalCutDisplay.PlacementProximityViewSource _proximity;

        private void AddCameraReferences(System.Collections.Generic.List<Vector3> points, System.Collections.Generic.List<Camera> of)
        {
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null)
                {
                    points.Add(cameras[i].transform.position);
                    of.Add(cameras[i]);
                }
            }
        }

        private void Unregister()
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
            if (world == null || world.Display == null || world.Display.IsDisposed)
            {
                return;
            }

            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i] != null)
                {
                    world.Display.TryUnregisterCamera(cameras[i]);
                    world.SkinDisplay?.ForgetCamera(cameras[i]);
                }
            }

            if (world.Display.PlacementProximityViews == _proximity)
            {
                world.Display.PlacementProximityViews = null;   // no camera named any more: every placement is asked
            }
        }
    }
}
