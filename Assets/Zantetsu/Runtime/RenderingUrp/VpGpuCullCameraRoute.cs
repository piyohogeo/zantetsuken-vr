using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using XRPass = UnityEngine.Experimental.Rendering.XRPass;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>
    /// Puts the GPU selection of a display's instances (VP Stage 3C, DESIGN 4.5.7) inside URP's own frame for a camera:
    /// a pass enqueued for that camera as it begins to render, recorded before everything else of that camera
    /// (<see cref="RenderPassEvent.BeforeRendering"/>), which asks the target to issue its selection into the pass's
    /// command buffer. The camera's shadow maps and its colour passes are recorded after it, so the draws registered
    /// for that camera read what this camera's selection wrote. Nothing is read back, nothing waits, and no frame is
    /// counted: the order is the pipeline's own.
    /// <para>
    /// **The conditions are the frame's own.** They are taken while the pass is recorded, from what URP will render
    /// with: each view's view and projection matrices as the camera data gives them -- under Single Pass Instanced the
    /// two eyes', so an instance only one eye sees is kept -- and, for the casters, the culling planes of every split
    /// of the main light's shadow map, recomputed with the public <c>ShadowUtils.ExtractDirectionalLightMatrix</c> from
    /// the inputs URP uses. The plane of a split that faces the light is not used: URP clamps a caster nearer the
    /// light than the slice's near plane onto it, so such a caster still casts. A caster is kept when any split's
    /// volume may hold it, whether the camera sees it or not.
    /// </para>
    /// <para>
    /// **What is not known keeps everything.** With an XR pass that is not single pass, the body keeps every instance.
    /// With more splits than <see cref="VpCullConditions.SplitCapacity"/>, or any other visible light that casts
    /// shadows while the pipeline renders additional light shadows, the casters keep every instance. They keep every
    /// instance too in a frame for which URP draws no main light shadow map from them at all -- no main light, one
    /// that is not directional or casts no shadow, or a split that cannot be computed, which is how a view with no
    /// caster or receiver in its shadow range comes out -- where there is nothing to select for. Each is counted, the
    /// two kinds apart, so a run can tell a selection from a view that kept everything.
    /// </para>
    /// </summary>
    public sealed class VpGpuCullCameraRoute
    {
        private const string NotUrp = "the active render pipeline is not URP";
        private const string CompatibilityMode = "URP compatibility mode is on; the selection is recorded only through the render graph";
        private const string NoRenderer = "the camera has no URP renderer to enqueue a pass on";

        // The CPU side of the selection, for the Profiler: the conditions taken while the pass is recorded, and the
        // dispatch issued when it is executed.
        private static readonly ProfilerMarker RecordMarker = new ProfilerMarker("Zantetsu.GpuCull.Conditions");
        private static readonly ProfilerMarker IssueMarker = new ProfilerMarker("Zantetsu.GpuCull.Issue");

        private readonly List<Pass> _passes = new List<Pass>(2);
        private bool _environmentChecked;

        /// <summary>How many times a selection was enqueued for a camera.</summary>
        public long Enqueued { get; private set; }

        /// <summary>How many times a pass was recorded and asked its target for a selection.</summary>
        public long Recorded { get; private set; }

        /// <summary>How many recordings the target refused: it had registered no draw for that camera this frame.</summary>
        public long Refused { get; private set; }

        /// <summary>How many recordings kept every instance for the body: an XR pass that is not single pass.</summary>
        public long BodyKeptEverything { get; private set; }

        /// <summary>
        /// How many recordings kept every instance as a caster although shadow maps may be drawn from them -- another
        /// light casting shadows, more splits than there is room for -- and why the last one did.
        /// </summary>
        public long CastersKeptEverything { get; private set; }
        public string LastCastersKeptEverythingReason { get; private set; }

        /// <summary>
        /// How many recordings found no main light shadow map to select for (see the class notes), and why the last
        /// one did. The casters keep everything then too; URP draws no main light shadow from them in such a frame.
        /// </summary>
        public long FramesWithoutMainShadowMap { get; private set; }
        public string LastWithoutMainShadowMapReason { get; private set; }

        /// <summary>The last recording's conditions: how many eyes and how many splits it selected by.</summary>
        public int LastEyeCount { get; private set; }
        public int LastSplitCount { get; private set; }

        /// <summary>
        /// The largest difference, over every element of every eye's view and projection, between what the last
        /// recording selected by and what the camera reported once it had rendered (<see cref="NoteRendered"/>). For a
        /// check that the selection's view is the rendered one.
        /// </summary>
        public float LastViewDifference { get; private set; }
        public float LargestViewDifference { get; private set; }

        /// <summary>
        /// Whether the selection can be put inside this project's pipeline at all: URP, recording through the render
        /// graph. Null when it can; otherwise why not.
        /// </summary>
        public static string CheckEnvironment()
        {
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset))
            {
                return NotUrp;
            }

            if (GraphicsSettings.TryGetRenderPipelineSettings(out RenderGraphSettings renderGraphSettings) && renderGraphSettings.enableRenderCompatibilityMode)
            {
                return CompatibilityMode;
            }

            return null;
        }

        /// <summary>
        /// Enqueues, for <paramref name="camera"/>'s rendering that is about to begin, the pass that asks
        /// <paramref name="target"/> for its selection. To be called from <c>RenderPipelineManager.beginCameraRendering</c>
        /// for that camera, before the target registers the draws that read the selection. False, with the reason and
        /// enqueuing nothing, when the pipeline or the camera cannot take the pass.
        /// </summary>
        public bool TryEnqueue(Camera camera, IVpGpuCullTarget target, out string failure)
        {
            // The pipeline is asked once: it is the project's, fixed while a Player runs.
            failure = _environmentChecked ? null : CheckEnvironment();
            if (failure != null)
            {
                return false;
            }

            _environmentChecked = true;
            if (camera == null || target == null)
            {
                failure = "no camera or no target";
                return false;
            }

            camera.TryGetComponent(out UniversalAdditionalCameraData cameraData);
            ScriptableRenderer renderer = cameraData != null ? cameraData.scriptableRenderer : null;
            if (renderer == null)
            {
                renderer = (GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset)?.scriptableRenderer;
            }

            if (renderer == null)
            {
                failure = NoRenderer;
                return false;
            }

            Pass pass = null;
            for (int i = 0; i < _passes.Count; i++)
            {
                if (ReferenceEquals(_passes[i].camera, camera))
                {
                    pass = _passes[i];
                    break;
                }
            }

            if (pass == null)
            {
                pass = new Pass(this, camera);
                _passes.Add(pass);
            }

            pass.target = target;
            renderer.EnqueuePass(pass);
            Enqueued++;
            return true;
        }

        /// <summary>
        /// Compares what the last recording for <paramref name="camera"/> selected by with the views the camera reports
        /// now. To be called once the camera has rendered (<c>RenderPipelineManager.endCameraRendering</c>), when a
        /// stereo camera's matrices are the ones URP rendered with. Diagnostic: it changes nothing that is drawn.
        /// </summary>
        public void NoteRendered(Camera camera)
        {
            for (int i = 0; i < _passes.Count; i++)
            {
                Pass pass = _passes[i];
                if (!ReferenceEquals(pass.camera, camera) || pass.recordedEyes == 0)
                {
                    continue;
                }

                float difference = 0f;
                if (pass.recordedEyes == 2)
                {
                    difference = Mathf.Max(difference, Difference(pass.views[0], camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left)));
                    difference = Mathf.Max(difference, Difference(pass.projections[0], camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left)));
                    difference = Mathf.Max(difference, Difference(pass.views[1], camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right)));
                    difference = Mathf.Max(difference, Difference(pass.projections[1], camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right)));
                }
                else
                {
                    difference = Mathf.Max(difference, Difference(pass.views[0], camera.worldToCameraMatrix));
                    difference = Mathf.Max(difference, Difference(pass.projections[0], camera.projectionMatrix));
                }

                LastViewDifference = difference;
                LargestViewDifference = Mathf.Max(LargestViewDifference, difference);
                return;
            }
        }

        /// <summary>Forgets a camera that is no longer drawn for.</summary>
        public void Forget(Camera camera)
        {
            for (int i = _passes.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_passes[i].camera, camera))
                {
                    _passes.RemoveAt(i);
                }
            }
        }

        private static float Difference(Matrix4x4 a, Matrix4x4 b)
        {
            float largest = 0f;
            for (int i = 0; i < 16; i++)
            {
                largest = Mathf.Max(largest, Mathf.Abs(a[i] - b[i]));
            }

            return largest;
        }

        private sealed class PassData
        {
            public IVpGpuCullTarget target;
            public Camera camera;
            public VpCullConditions conditions;
            public VpGpuCullCameraRoute route;
        }

        private sealed class Pass : ScriptableRenderPass
        {
            public readonly Camera camera;
            public readonly Matrix4x4[] views = new Matrix4x4[VpCullConditions.EyeCapacity];
            public readonly Matrix4x4[] projections = new Matrix4x4[VpCullConditions.EyeCapacity];
            public IVpGpuCullTarget target;
            public int recordedEyes;

            private readonly VpGpuCullCameraRoute _route;
            private readonly VpCullConditions _conditions = new VpCullConditions();

            public Pass(VpGpuCullCameraRoute route, Camera camera)
            {
                _route = route;
                this.camera = camera;
                renderPassEvent = RenderPassEvent.BeforeRendering;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(cameraData.camera, camera) || target == null)
                {
                    return;
                }

                _route.Recorded++;
                using (RecordMarker.Auto())
                {
                    _conditions.KeepEverything();
                    SetEyes(cameraData);
                    SetSplits(frameData, cameraData);
                }

                _route.LastEyeCount = _conditions.eyeCount;
                _route.LastSplitCount = _conditions.shadowSplitCount;

                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass("VP GPU Cull", out PassData data, profilingSampler))
                {
                    data.target = target;
                    data.camera = camera;
                    data.conditions = _conditions;
                    data.route = _route;
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (PassData passData, UnsafeGraphContext context) =>
                    {
                        CommandBuffer commands = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        using (IssueMarker.Auto())
                        {
                            if (!passData.target.IssueCull(commands, passData.camera, passData.conditions))
                            {
                                passData.route.Refused++;
                            }
                        }
                    });
                }
            }

            // The eyes URP renders this pass with. A camera with no XR pass has one; a single pass XR pass has two;
            // an XR pass of one view (multi pass) is one eye of two this pass is not told the other of, so nothing is
            // removed for it.
            private void SetEyes(UniversalCameraData cameraData)
            {
                recordedEyes = 0;
                XRPass xr = cameraData.xr;
                bool xrEnabled = xr != null && xr.enabled;
                if (xrEnabled && !xr.singlePassEnabled)
                {
                    _route.BodyKeptEverything++;
                    return;
                }

                int eyes = xrEnabled ? Mathf.Min(xr.viewCount, VpCullConditions.EyeCapacity) : 1;
                for (int eye = 0; eye < eyes; eye++)
                {
                    views[eye] = cameraData.GetViewMatrix(eye);
                    projections[eye] = cameraData.GetProjectionMatrix(eye);
                    _conditions.SetEye(eye, views[eye], projections[eye]);
                }

                _conditions.eyeCount = eyes;
                recordedEyes = eyes;
            }

            private void SetSplits(ContextContainer frameData, UniversalCameraData cameraData)
            {
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
                string reason = TrySetSplits(renderingData, lightData, shadowData, out bool noShadowMap);
                if (reason != null)
                {
                    _conditions.shadowSplitCount = 0;
                    _conditions.shadowPlaneCounts = Vector4.zero;
                    if (noShadowMap)
                    {
                        _route.FramesWithoutMainShadowMap++;
                        _route.LastWithoutMainShadowMapReason = reason;
                    }
                    else
                    {
                        _route.CastersKeptEverything++;
                        _route.LastCastersKeptEverythingReason = reason;
                    }
                }
            }

            // Null when every split's planes were set; otherwise why the casters keep everything, and whether that is
            // because URP draws no main light shadow map in this frame.
            private string TrySetSplits(
                UniversalRenderingData renderingData, UniversalLightData lightData, UniversalShadowData shadowData, out bool noShadowMap)
            {
                noShadowMap = false;

                // Another light's shadow map is drawn from the same casters, and its volume is not one of these splits.
                // Asked first: it holds whatever the main light is.
                int lightIndex = lightData.mainLightIndex;
                if (shadowData.supportsAdditionalLightShadows)
                {
                    for (int i = 0; i < lightData.visibleLights.Length; i++)
                    {
                        if (i == lightIndex)
                        {
                            continue;
                        }

                        Light other = lightData.visibleLights[i].light;
                        if (other != null && other.shadows != LightShadows.None)
                        {
                            return "another visible light casts shadows";
                        }
                    }
                }

                if (lightIndex < 0)
                {
                    noShadowMap = true;
                    return "no main light";
                }

                VisibleLight mainLight = lightData.visibleLights[lightIndex];
                if (mainLight.lightType != LightType.Directional || mainLight.light == null || mainLight.light.shadows == LightShadows.None
                    || !shadowData.supportsMainLightShadows)
                {
                    noShadowMap = true;
                    return "the main light is not a directional light casting shadows";
                }

                int splits = shadowData.mainLightShadowCascadesCount;
                if (splits <= 0 || splits > VpCullConditions.SplitCapacity)
                {
                    return "the main light's shadow map has " + splits + " splits";
                }

                int width = shadowData.mainLightShadowmapWidth;
                int height = shadowData.mainLightShadowmapHeight;
                int renderTargetHeight = splits == 2 ? height >> 1 : height;
                int resolution = ShadowUtils.GetMaxTileResolutionInAtlas(width, height, splits);
                Vector3 lightForward = mainLight.localToWorldMatrix.GetColumn(2);
                Vector4 counts = Vector4.zero;
                for (int split = 0; split < splits; split++)
                {
                    if (!ShadowUtils.ExtractDirectionalLightMatrix(
                            ref renderingData.cullResults, shadowData, lightIndex, split, width, renderTargetHeight, resolution,
                            mainLight.light.shadowNearPlane, out Vector4 _, out ShadowSliceData slice))
                    {
                        noShadowMap = true;
                        return "split " + split + " could not be computed";
                    }

                    int kept = 0;
                    int planes = slice.splitData.cullingPlaneCount;
                    for (int i = 0; i < planes && kept < VpCullConditions.SplitPlaneCapacity; i++)
                    {
                        Plane plane = slice.splitData.GetCullingPlane(i);
                        if (Vector3.Dot(plane.normal, lightForward) > 0.9f)
                        {
                            continue;
                        }

                        _conditions.SetShadowPlane(split, kept++, plane);
                    }

                    counts[split] = kept;
                }

                _conditions.shadowPlaneCounts = counts;
                _conditions.shadowSplitCount = splits;
                return null;
            }
        }
    }
}
