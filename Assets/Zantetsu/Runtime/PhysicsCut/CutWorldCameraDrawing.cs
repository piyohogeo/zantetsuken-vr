using System;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.MeshCut;
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

        /// <summary>The route the GPU selection is put into the pipeline by, once a display that selects has been drawn; else null.</summary>
        public VpGpuCullCameraRoute CullRoute => _cullRoute;

        /// <summary>How many times a camera was not drawn for because its selection could not be enqueued.</summary>
        public int CullRefusals { get; private set; }

        /// <summary>How many frames this has registered a draw in. It counts the drawing, not what was drawn.</summary>
        public int DrawnFrames { get; private set; }

        /// <summary>How many frames had nothing to draw: no collection had settled for them.</summary>
        public int FramesWithNothingToDraw { get; private set; }

        /// <summary>The last frame a camera could not be prepared in, or zero. A refusal draws nothing that frame.</summary>
        public int LastRefusedFrame { get; private set; }

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
            Unregister();
        }

        // What the GPU selection did while this drew, once, for the log: a run reads it to know that it selected.
        private void ReportCullRoute()
        {
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
                    : "; no display"),
                this);
            _cullRoute = null;
            _cullDisplay = null;
        }

        private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (_cullRoute != null && IsOneOfOurs(camera))
            {
                _cullRoute.NoteRendered(camera);
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
            }

            display.Render(layer, camera);
            if (_drawnFrame != Time.frameCount)
            {
                _drawnFrame = Time.frameCount;
                DrawnFrames++;
            }
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

                if (!world.Display.TryRegisterCamera(cameras[i]))
                {
                    Debug.LogError(
                        name + ": the display would not take " + cameras[i].name
                        + " -- its camera capacity is what the world's profile says.", this);
                }
            }

            _registered = true;
            return true;
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
                }
            }
        }
    }
}
