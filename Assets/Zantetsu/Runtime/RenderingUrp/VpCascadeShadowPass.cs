using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>Which faces the cascade shadow pass culls.</summary>
    public enum VpShadowCullMode
    {
        /// <summary>Back faces: the pass starts a separate native render pass targeting the shadow texture.</summary>
        Auto,
        Back,
        Front,
    }

    /// <summary>
    /// Stage 3 probe: draws a <see cref="VpCulledInstanceSet"/>'s instances into URP's main light shadow map, selected per
    /// cascade. Enqueue it for a camera from RenderPipelineManager.beginCameraRendering; it records after URP's main light
    /// shadow pass. For each cascade it recomputes the slice with the public ShadowUtils.ExtractDirectionalLightMatrix and
    /// the inputs URP uses (render target size, per-cascade resolution and the light's shadow near plane), selects the
    /// instances against that slice's ShadowSplitData culling planes, uploads the selections, and draws each selected
    /// command with CommandBuffer.DrawProceduralIndirect over the hardware index buffer in the slice's viewport, with
    /// ShadowUtils.GetShadowBias and the depth bias URP's RenderShadowSlice sets.
    /// <para>
    /// The pass does not set the view and projection matrices: URP restores the camera's matrices (and the XR stereo
    /// constants) after its own shadow passes but before AfterRenderingShadows passes, only through internal code. The
    /// slice's GPU view-projection, with the render-texture Y flip, goes to the shader as _VpShadowSliceViewProjection
    /// instead. An empty unsafe pass before the raster pass breaks native render pass merging, so the shadow texture
    /// is bound in a new native render pass after URP's camera-properties update. <see cref="CullMode"/> Auto then culls
    /// back faces for the texture's GPU projection, independently of the camera target and XR state.
    /// </para>
    /// <para>
    /// Without the boundary, URP's attachmentless camera-properties pass can merge between its shadow pass and this
    /// pass. On D3D11 that makes the correct cull depend on the camera target even though the shadow texture's public
    /// UV origin is unchanged. AllowGlobalStateModification alone does not prevent native pass merging. The unsafe
    /// boundary changes no matrices or culling state; the raster pass retains the atlas with ReadWrite access. It can
    /// add a shadow-atlas store/load, so its performance should be measured on the deployment GPU. The pass leaves
    /// behind only what URP's main light shadow pass leaves (the
    /// last cascade's _ShadowBias, _LightDirection, _LightPosition, _CASTING_PUNCTUAL_LIGHT_SHADOW off, depth bias 0,
    /// the full-target viewport) and its own VP globals.
    /// </para>
    /// <para>
    /// The culling plane that faces the light is not used: URP clamps shadow casters closer to the light than the near
    /// plane onto it (ApplyShadowClamping), so such casters still write depth. With <see cref="CullSplits"/> false every
    /// instance is drawn into every cascade. With <see cref="DiagnosticTarget"/> set, the pass draws into that depth
    /// texture (cleared to far first) instead of URP's shadow map, so its result can be compared with URP's. The pass draws
    /// nothing, and reports why in <see cref="LastSkipReason"/>, when there is no main light, shadows are off or
    /// unsupported, the light has no shadow caster bounds, URP bound an empty placeholder instead of its shadow atlas
    /// (no caster draws into a shadow map this frame), or a slice is invalid. Only the last leaves URP's rendered atlas
    /// without the set's casters; <see cref="LastSkipLosesShadows"/> reports it. It never owns the set, the geometry
    /// buffers, the material or the diagnostic target.
    /// </para>
    /// </summary>
    public sealed class VpCascadeShadowPass : ScriptableRenderPass
    {
        private static readonly ProfilerMarker SelectMarker = new ProfilerMarker("VpCascadeShadows.Select");
        private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("VpCascadeShadows.Upload");
        private static readonly ProfilerMarker IssueMarker = new ProfilerMarker("VpCascadeShadows.Issue");
        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");
        private static readonly int VisibleInstancesId = Shader.PropertyToID("_VpVisibleInstances");
        private static readonly int VisibleOffsetId = Shader.PropertyToID("_VpVisibleOffset");
        private static readonly int SliceViewProjectionId = Shader.PropertyToID("_VpShadowSliceViewProjection");
        private static readonly int ShadowBiasId = Shader.PropertyToID("_ShadowBias");
        private static readonly int LightDirectionId = Shader.PropertyToID("_LightDirection");
        private static readonly int LightPositionId = Shader.PropertyToID("_LightPosition");
        private static readonly GlobalKeyword CastingPunctualLightShadow = GlobalKeyword.Create("_CASTING_PUNCTUAL_LIGHT_SHADOW");
        private const int CullBackPass = 0;
        private const int CullFrontPass = 1;

        private readonly VpCulledInstanceSet _set;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly Material _material;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private readonly Plane[] _planes = new Plane[16];
        private readonly Matrix4x4[] _viewProjections = new Matrix4x4[VpCulledInstanceSet.MaxSplits];
        private readonly Vector4[] _biases = new Vector4[VpCulledInstanceSet.MaxSplits];
        private readonly Rect[] _viewports = new Rect[VpCulledInstanceSet.MaxSplits];
        private readonly int[] _splitPlaneCounts = new int[VpCulledInstanceSet.MaxSplits];

        private sealed class BoundaryPassData { }

        private class PassData
        {
            internal VpCascadeShadowPass pass;
            internal int splitCount;
            internal Vector4 lightDirection;
            internal Vector4 lightPosition;
            internal TextureHandle shadowTexture;
            internal TextureHandle cameraTarget;
            internal Rect fullViewport;
        }

        public VpCascadeShadowPass(VpCulledInstanceSet set, VpGpuIndexedGeometryBuffers buffers, Material shadowCasterMaterial)
        {
            _set = set;
            _buffers = buffers;
            _material = shadowCasterMaterial;
            renderPassEvent = RenderPassEvent.AfterRenderingShadows;
            profilingSampler = new ProfilingSampler("VP Cascade Shadows");
        }

        /// <summary>Whether each cascade draws only the instances inside its culling planes; otherwise every instance.</summary>
        public bool CullSplits { get; set; } = true;

        public VpShadowCullMode CullMode { get; set; } = VpShadowCullMode.Auto;

        /// <summary>
        /// Diagnostics: a depth texture of the shadow map's size and format to draw into instead of URP's shadow map. The
        /// texture is cleared before the draws. Null draws into URP's shadow map.
        /// </summary>
        public RTHandle DiagnosticTarget { get; set; }

        /// <summary>Diagnostics: the render graph handle of the texture drawn in the last recorded frame; invalid when skipped.</summary>
        public TextureHandle LastTargetHandle { get; private set; }

        /// <summary>The cascades drawn in the last recorded frame; 0 when skipped.</summary>
        public int LastSplitCount { get; private set; }

        /// <summary>Why the last recorded frame drew nothing, or null when it drew.</summary>
        public string LastSkipReason { get; private set; }

        /// <summary>
        /// Whether the last recorded frame skipped although URP rendered its main light shadow atlas, so the set's
        /// instances cast no shadow in that frame.
        /// </summary>
        public bool LastSkipLosesShadows { get; private set; }

        /// <summary>The number of times the pass has been recorded, drawn or skipped; for telling a fresh frame's state from a stale one.</summary>
        public int RecordCount { get; private set; }

        /// <summary>Whether the last executed frame culled front faces.</summary>
        public bool LastCullFront { get; private set; }

        /// <summary>The UV origin of URP's main light shadow map in the last executed frame.</summary>
        public TextureUVOrigin LastShadowOrigin { get; private set; }

        /// <summary>The UV origin of the camera target (URP's active colour or depth texture) in the last executed frame.</summary>
        public TextureUVOrigin LastCameraTargetOrigin { get; private set; }

        /// <summary>Camera facts of the last recorded frame, for the diagnostics log.</summary>
        public bool LastXrEnabled { get; private set; }

        public bool LastActiveTargetBackBuffer { get; private set; }

        public bool LastPostProcessEnabled { get; private set; }

        public CameraType LastCameraType { get; private set; }

        /// <summary>The main light's shadow caster bounds (Unity's culling result) in the last recorded frame that drew.</summary>
        public Bounds LastCasterBounds { get; private set; }

        /// <summary>The number of culling planes of a cascade used in the last recorded frame.</summary>
        public int SplitPlaneCount(int split)
        {
            return _splitPlaneCounts[split];
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            RecordCount++;
            LastSplitCount = 0;
            LastSkipLosesShadows = false;
            LastTargetHandle = TextureHandle.nullHandle;
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>();
            UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            LastXrEnabled = cameraData.xr != null && cameraData.xr.enabled;
            LastActiveTargetBackBuffer = resourceData.isActiveTargetBackBuffer;
            LastPostProcessEnabled = cameraData.postProcessEnabled;
            LastCameraType = cameraData.cameraType;

            int lightIndex = lightData.mainLightIndex;
            if (lightIndex < 0)
            {
                LastSkipReason = "no main light";
                return;
            }

            VisibleLight visibleLight = lightData.visibleLights[lightIndex];
            if (!shadowData.supportsMainLightShadows || visibleLight.light == null || visibleLight.light.shadows == LightShadows.None)
            {
                LastSkipReason = "main light shadows off or unsupported";
                return;
            }

            CullingResults cullResults = renderingData.cullResults;
            if (!cullResults.GetShadowCasterBounds(lightIndex, out Bounds casterBounds))
            {
                LastSkipReason = "no shadow caster bounds: URP binds an empty shadow map";
                return;
            }

            LastCasterBounds = casterBounds;

            TextureHandle shadowTexture = resourceData.mainShadowsTexture;
            if (!shadowTexture.IsValid())
            {
                LastSkipReason = "no main light shadow texture";
                return;
            }

            int cascades = Mathf.Min(shadowData.mainLightShadowCascadesCount, VpCulledInstanceSet.MaxSplits);
            int width = shadowData.mainLightShadowmapWidth;
            int height = shadowData.mainLightShadowmapHeight;
            int renderTargetHeight = cascades == 2 ? height >> 1 : height;
            TextureDesc shadowDescription = renderGraph.GetTextureDesc(shadowTexture);
            if (shadowDescription.width != width || shadowDescription.height != renderTargetHeight)
            {
                LastSkipReason = "URP bound an empty main light shadow map";
                return;
            }

            int resolution = ShadowUtils.GetMaxTileResolutionInAtlas(width, height, cascades);
            Vector3 lightForward = visibleLight.localToWorldMatrix.GetColumn(2);

            using (SelectMarker.Auto())
            {
                _set.BeginShadowFrame();
                for (int cascade = 0; cascade < cascades; cascade++)
                {
                    if (!ShadowUtils.ExtractDirectionalLightMatrix(ref cullResults, shadowData, lightIndex, cascade, width, renderTargetHeight, resolution,
                            visibleLight.light.shadowNearPlane, out Vector4 _, out ShadowSliceData slice))
                    {
                        LastSkipReason = "invalid cascade slice " + cascade;
                        LastSkipLosesShadows = true;
                        return;
                    }

                    // The shadow map is a render texture: the same GPU projection SetViewProjectionMatrices would derive.
                    _viewProjections[cascade] = GL.GetGPUProjectionMatrix(slice.projectionMatrix, true) * slice.viewMatrix;
                    _viewports[cascade] = new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution);
                    _biases[cascade] = ShadowUtils.GetShadowBias(ref visibleLight, lightIndex, shadowData, slice.projectionMatrix, slice.resolution);

                    int planeCount = 0;
                    if (CullSplits)
                    {
                        int count = Mathf.Min(slice.splitData.cullingPlaneCount, _planes.Length);
                        for (int i = 0; i < count; i++)
                        {
                            Plane plane = slice.splitData.GetCullingPlane(i);
                            if (Vector3.Dot(plane.normal, lightForward) > 0.9f)
                            {
                                continue;
                            }

                            _planes[planeCount++] = plane;
                        }
                    }

                    _splitPlaneCounts[cascade] = planeCount;
                    _set.SelectSplit(cascade, _planes, planeCount);
                }
            }

            using (UploadMarker.Auto())
            {
                _set.UploadShadowSelections();
            }

            _properties.SetBuffer(VerticesId, _buffers.VertexBuffer);
            _properties.SetBuffer(InstanceObjectToWorldId, _set.InstanceBuffer);
            _properties.SetBuffer(VisibleInstancesId, _set.VisibleBuffer);

            TextureHandle target = shadowTexture;
            if (DiagnosticTarget != null)
            {
                // The same UV origin URP's own shadow map (a render graph texture) gets, so the two rasterize alike.
                target = renderGraph.ImportTexture(DiagnosticTarget, new ImportResourceParams
                {
                    clearOnFirstUse = true, clearColor = Color.clear, discardOnLastUse = false, textureUVOrigin = TextureUVOrigin.BottomLeft,
                });
            }

            // A non-raster pass ends the preceding native render pass. In particular, URP's attachmentless
            // SetupCameraProperties must not run inside the same native pass as these draws: it changes the native
            // projection/winding state even though this shader uses its own GPU VP. The empty pass needs no resources;
            // its global-state sync point preserves ordering and prevents it from being culled.
            using (IUnsafeRenderGraphBuilder boundary = renderGraph.AddUnsafePass<BoundaryPassData>("VP Shadow Native Pass Boundary", out _))
            {
                boundary.AllowGlobalStateModification(true);
                boundary.SetRenderFunc(static (BoundaryPassData data, UnsafeGraphContext context) => { });
            }

            using (IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass(passName, out PassData data, profilingSampler))
            {
                data.pass = this;
                data.splitCount = cascades;
                Vector3 lightDirection = -visibleLight.localToWorldMatrix.GetColumn(2);
                Vector3 lightPosition = visibleLight.localToWorldMatrix.GetColumn(3);
                data.lightDirection = new Vector4(lightDirection.x, lightDirection.y, lightDirection.z, 0f);
                data.lightPosition = new Vector4(lightPosition.x, lightPosition.y, lightPosition.z, 1f);
                data.shadowTexture = shadowTexture;
                data.cameraTarget = resourceData.activeColorTexture.IsValid() ? resourceData.activeColorTexture : resourceData.activeDepthTexture;
                data.fullViewport = new Rect(0f, 0f, width, renderTargetHeight);
                builder.SetRenderAttachmentDepth(target, AccessFlags.ReadWrite);
                builder.AllowGlobalStateModification(true);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData passData, RasterGraphContext context) => passData.pass.Execute(passData, context));
            }

            LastTargetHandle = target;
            LastSplitCount = cascades;
            LastSkipReason = null;
        }

        private void Execute(PassData data, RasterGraphContext context)
        {
            RasterCommandBuffer cmd = context.cmd;
            LastShadowOrigin = context.GetTextureUVOrigin(data.shadowTexture);
            LastCameraTargetOrigin = context.GetTextureUVOrigin(data.cameraTarget);
            switch (CullMode)
            {
                case VpShadowCullMode.Back:
                    LastCullFront = false;
                    break;
                case VpShadowCullMode.Front:
                    LastCullFront = true;
                    break;
                default:
                    LastCullFront = false;
                    break;
            }

            int shaderPass = LastCullFront ? CullFrontPass : CullBackPass;
            using (IssueMarker.Auto())
            {
                cmd.SetKeyword(CastingPunctualLightShadow, false);
                cmd.SetGlobalVector(LightDirectionId, data.lightDirection);
                cmd.SetGlobalVector(LightPositionId, data.lightPosition);
                for (int split = 0; split < data.splitCount; split++)
                {
                    cmd.SetGlobalVector(ShadowBiasId, _biases[split]);
                    cmd.SetGlobalDepthBias(1.0f, 2.5f);
                    cmd.SetViewport(_viewports[split]);
                    cmd.SetGlobalMatrix(SliceViewProjectionId, _viewProjections[split]);
                    int draws = _set.ShadowDrawCount(split);
                    for (int draw = 0; draw < draws; draw++)
                    {
                        _set.GetShadowDraw(split, draw, out int argumentsOffset, out int visibleOffset);
                        cmd.SetGlobalInteger(VisibleOffsetId, visibleOffset);
                        cmd.DrawProceduralIndirect(_buffers.IndexBuffer, Matrix4x4.identity, _material, shaderPass, MeshTopology.Triangles, _set.ShadowArgumentBuffer, argumentsOffset, _properties);
                    }

                    cmd.DisableScissorRect();
                    cmd.SetGlobalDepthBias(0.0f, 0.0f);
                }

                // The last cascade's viewport must not leak into the next pass on the same target.
                cmd.SetViewport(data.fullViewport);
            }
        }
    }
}
