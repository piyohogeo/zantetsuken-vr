using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Zantetsu.Rendering;

namespace Zantetsu.Quest
{
    /// <summary>
    /// Prepares the Unity stencil and replay visual pipelines in the real Quest
    /// camera before replay. Uses the actual display/visual materials and draw
    /// paths. Degenerate visual triangles also prepare shadow and XR motion
    /// passes, without rasterizing a sample or creating a cut or AppSW cap.
    /// </summary>
    public sealed class QuestNpcUnityPrewarm : IDisposable
    {
        private static readonly Bounds Everywhere = new Bounds(Vector3.zero, Vector3.one * 1000000f);
        private readonly Camera _camera;
        private readonly Material _init, _volume, _cap;
        private readonly int _layer, _instances;
        private readonly MaterialPropertyBlock _initProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _volumeProperties = new MaterialPropertyBlock();
        private readonly MaterialPropertyBlock _capProperties = new MaterialPropertyBlock();
        private GraphicsBuffer _vertices, _indices, _transforms, _clips, _arguments, _capVertices, _capPlacements;
        private readonly List<MeshRenderer> _replayVisualRenderers = new List<MeshRenderer>();
        private readonly List<Mesh> _replayVisualMeshes = new List<Mesh>();
        private GraphicsFence _fence;
        private int _lastIssueFrame = -1, _fenceFrame = -1;
        private bool _disposed, _retirementRequested;

        /// <summary>Completed camera callbacks for frames in which Issue registered the three draws.</summary>
        public int RenderedWarmFrames { get; private set; }

        public int ReplayVisualRendererCount { get; private set; }
        public int ReplayVisualMaterialCount { get; private set; }

        /// <summary>
        /// Four subsequent frames, including normal camera depth/stencil clears,
        /// and GPU completion of the last warm camera are required before release.
        /// </summary>
        public bool ReadyToDispose => _lastIssueFrame < 0
            || (!_disposed && Time.frameCount >= _lastIssueFrame + 4
                && _fenceFrame == _lastIssueFrame && _fence.passed);

        public static QuestNpcUnityPrewarm Create(Camera camera, VpStencilCapMaterials materials,
            bool singlePassInstanced = false, int layer = 0, IReadOnlyList<MeshRenderer> replayVisualRenderers = null)
        {
            if (camera == null || materials == null || materials.ColorCount < 1)
                throw new ArgumentException("The real camera and display stencil materials are required.");
            if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset pipeline)
                || pipeline.name != "Quest_RPAsset")
                throw new InvalidOperationException("Unity prewarm requires the Quest_RPAsset drawing conditions.");
            if (camera.TryGetComponent<UniversalAdditionalCameraData>(out var data) && data.renderType != CameraRenderType.Base)
                throw new InvalidOperationException("Unity prewarm requires the Base camera's ordinary depth/stencil clear.");
            if (camera.clearFlags == CameraClearFlags.Nothing)
                throw new InvalidOperationException("Unity prewarm requires the ordinary camera clear.");
            if (layer < 0 || layer > 31 || (camera.cullingMask & (1 << layer)) == 0)
                throw new ArgumentException("The warm draw layer must be visible to the real camera.");
            if (!SystemInfo.supportsGraphicsFence)
                throw new InvalidOperationException("GPU completion fences are required for prewarm buffer retirement.");
            return new QuestNpcUnityPrewarm(camera, materials, singlePassInstanced, layer, replayVisualRenderers);
        }

        private QuestNpcUnityPrewarm(Camera camera, VpStencilCapMaterials materials, bool singlePassInstanced,
            int layer, IReadOnlyList<MeshRenderer> replayVisualRenderers)
        {
            _camera = camera;
            _layer = layer;
            _instances = singlePassInstanced ? 2 : 1;
            // Different colours have the same shader and stencil state. Queue and
            // base colour do not change those pipelines, so colour zero suffices.
            _init = materials.Init(0);
            _volume = materials.Volume(0);
            _cap = materials.Cap(0);
            try
            {
                _vertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VpRenderVertex.Stride) { name = "Quest prewarm degenerate vertex" };
                _vertices.SetData(new[] { new VpRenderVertex { position = Vector3.zero, normal = Vector3.up, uv0 = Vector2.zero } });
                _indices = new GraphicsBuffer(GraphicsBuffer.Target.Index, 3, sizeof(uint)) { name = "Quest prewarm degenerate indices" };
                _indices.SetData(new uint[] { 0, 0, 0 });
                _transforms = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VpIndexedIndirectDrawBatch.InstanceStride) { name = "Quest prewarm transform" };
                _transforms.SetData(new[] { Matrix4x4.identity });
                _clips = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, VpIndexedIndirectDrawBatch.InstanceClipStride) { name = "Quest prewarm no clip record" };
                _clips.SetData(new[] { VpInstanceClip.None });
                _arguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size) { name = "Quest prewarm indirect triangle" };
                _arguments.SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = 3, instanceCount = (uint)_instances,
                    startIndex = 0, baseVertexIndex = 0, startInstance = 0,
                } });
                _capVertices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 4) { name = "Quest prewarm degenerate cap vertex" };
                _capVertices.SetData(new[] { Vector4.zero });
                _capPlacements = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, sizeof(float) * 4) { name = "Quest prewarm cap placement" };
                _capPlacements.SetData(new[]
                {
                    new Vector4(1, 0, 0, 0), new Vector4(0, 1, 0, 0),
                    new Vector4(0, 0, 1, 0), new Vector4(0, 1, 0, 0),
                });
                _initProperties.SetInteger("_VpInstanceMultiplier", _instances);
                _volumeProperties.SetInteger("_VpInstanceMultiplier", _instances);
                _volumeProperties.SetBuffer("_VpVertices", _vertices);
                _volumeProperties.SetBuffer("_VpInstanceObjectToWorld", _transforms);
                _volumeProperties.SetBuffer("_VpInstanceClip", _clips);
                _capProperties.SetInteger("_VpInstanceMultiplier", _instances);
                _capProperties.SetBuffer("_VpCapVertices", _capVertices);
                _capProperties.SetBuffer("_VpCapPlacements", _capPlacements);
                // Vulkan requires all reflected buffers, even a branch-disabled
                // one. Index zero of this buffer is also a finite unit normal.
                _capProperties.SetBuffer("_VpCapNormals", _capPlacements);
                PrepareReplayVisuals(replayVisualRenderers);
                RenderPipelineManager.endCameraRendering += AfterCamera;
            }
            catch
            {
                ReleaseBuffers(); // No draw has been issued by the constructor.
                throw;
            }
        }

        private void PrepareReplayVisuals(IReadOnlyList<MeshRenderer> sources)
        {
            if (sources == null) return;
            var seenRenderers = new HashSet<MeshRenderer>();
            var seenMaterials = new HashSet<Material>();
            for (int i = 0; i < sources.Count; i++)
            {
                MeshRenderer source = sources[i];
                if (source == null || !seenRenderers.Add(source)) continue;
                if (!source.TryGetComponent<MeshFilter>(out var sourceFilter)
                    || sourceFilter.sharedMesh == null || !sourceFilter.sharedMesh.isReadable
                    || sourceFilter.sharedMesh.vertexCount == 0)
                    throw new InvalidOperationException("Replay visual prewarm needs its readable, nonempty source mesh: " + source.name);
                if ((_camera.cullingMask & (1 << source.gameObject.layer)) == 0)
                    throw new InvalidOperationException("The replay visual's layer must be visible to the real camera: " + source.name);
                Material[] materials = source.sharedMaterials;
                if (materials.Length == 0)
                    throw new InvalidOperationException("Replay visual prewarm needs the actual source materials: " + source.name);
                for (int slot = 0; slot < materials.Length; slot++)
                {
                    if (materials[slot] == null)
                        throw new InvalidOperationException("A replay visual material is missing: " + source.name);
                    seenMaterials.Add(materials[slot]);
                }

                // Clone the mesh rather than building a new position-only mesh:
                // Vulkan's pipeline key includes the actual vertex stream layout.
                // Replacing only indices retains positions/normals/tangents/UVs
                // and makes every submesh a zero-area triangle in every pass.
                Mesh mesh = UnityEngine.Object.Instantiate(sourceFilter.sharedMesh);
                _replayVisualMeshes.Add(mesh);
                mesh.name = "Quest prewarm degenerate " + sourceFilter.sharedMesh.name;
                mesh.hideFlags = HideFlags.DontSave;
                for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                {
                    if (mesh.GetTopology(submesh) != MeshTopology.Triangles)
                        throw new InvalidOperationException("Replay visual prewarm requires triangle submeshes: " + source.name);
                    mesh.SetIndices(new[] { 0, 0, 0 }, MeshTopology.Triangles, submesh, false);
                }
                // A finite camera-local bound keeps these degenerate draws in
                // the ordinary renderer lists, including shadows and XR motion.
                mesh.bounds = new Bounds(Vector3.zero, Vector3.one);
                var visual = new GameObject("Quest prewarm " + source.name) { hideFlags = HideFlags.DontSave };
                visual.SetActive(false);
                visual.layer = source.gameObject.layer;
                visual.AddComponent<MeshFilter>().sharedMesh = mesh;
                MeshRenderer renderer = visual.AddComponent<MeshRenderer>();
                _replayVisualRenderers.Add(renderer);
                renderer.enabled = false;
                renderer.sharedMaterials = materials;
                renderer.additionalVertexStreams = source.additionalVertexStreams;
                renderer.shadowCastingMode = source.shadowCastingMode;
                renderer.receiveShadows = source.receiveShadows;
                renderer.motionVectorGenerationMode = source.motionVectorGenerationMode;
                renderer.lightProbeUsage = source.lightProbeUsage;
                renderer.reflectionProbeUsage = source.reflectionProbeUsage;
                renderer.probeAnchor = source.probeAnchor;
                renderer.lightProbeProxyVolumeOverride = source.lightProbeProxyVolumeOverride;
                renderer.lightmapIndex = source.lightmapIndex;
                renderer.lightmapScaleOffset = source.lightmapScaleOffset;
                renderer.realtimeLightmapIndex = source.realtimeLightmapIndex;
                renderer.realtimeLightmapScaleOffset = source.realtimeLightmapScaleOffset;
                renderer.renderingLayerMask = source.renderingLayerMask;
                renderer.rendererPriority = source.rendererPriority;
                renderer.sortingLayerID = source.sortingLayerID;
                renderer.sortingOrder = source.sortingOrder;
                renderer.allowOcclusionWhenDynamic = false;
                if (source.HasPropertyBlock())
                {
                    var properties = new MaterialPropertyBlock();
                    source.GetPropertyBlock(properties);
                    if (!properties.isEmpty) renderer.SetPropertyBlock(properties);
                    for (int slot = 0; slot < materials.Length; slot++)
                    {
                        properties.Clear();
                        source.GetPropertyBlock(properties, slot);
                        if (!properties.isEmpty) renderer.SetPropertyBlock(properties, slot);
                    }
                }
                Vector3 scale = source.transform.lossyScale;
                visual.transform.localScale = new Vector3(scale.x < 0 ? -1 : 1,
                    scale.y < 0 ? -1 : 1, scale.z < 0 ? -1 : 1);
                visual.SetActive(true);
            }
            ReplayVisualRendererCount = _replayVisualRenderers.Count;
            ReplayVisualMaterialCount = seenMaterials.Count;
        }

        private void SetReplayVisualsEnabled(bool enabled)
        {
            for (int i = 0; i < _replayVisualRenderers.Count; i++)
            {
                MeshRenderer renderer = _replayVisualRenderers[i];
                if (renderer == null) continue;
                if (enabled)
                {
                    renderer.transform.SetPositionAndRotation(_camera.transform.position
                        + _camera.transform.forward * Mathf.Max(1f, _camera.nearClipPlane + 0.6f), _camera.transform.rotation);
                }
                renderer.enabled = enabled;
            }
        }

        /// <summary>Register one preparation frame's draws before the real camera renders.</summary>
        public void Issue()
        {
            if (_disposed || _retirementRequested) throw new ObjectDisposedException(nameof(QuestNpcUnityPrewarm));
            if (_camera == null || !_camera.isActiveAndEnabled)
                throw new InvalidOperationException("The prewarm camera must still render.");
            if (_lastIssueFrame == Time.frameCount)
                throw new InvalidOperationException("Register prewarm once per frame.");
            _lastIssueFrame = Time.frameCount;
            SetReplayVisualsEnabled(true);
            // All three use the opaque attachment, real URP keywords, XR views,
            // MSAA and their existing queues. Init writes only stencil (128).
            // Volume/cap indices are 0,0,0: no sample is rasterized, so they
            // cannot change colour/depth/stencil or add a visible cap.
            Graphics.RenderPrimitives(Parameters(_init, _initProperties, false), MeshTopology.Triangles, 3, _instances);
            Graphics.RenderPrimitivesIndexedIndirect(Parameters(_volume, _volumeProperties, true),
                MeshTopology.Triangles, _indices, _arguments, 1, 0);
            Graphics.RenderPrimitivesIndexed(Parameters(_cap, _capProperties, false),
                MeshTopology.Triangles, _indices, 3, 0, _instances);
        }

        private RenderParams Parameters(Material material, MaterialPropertyBlock properties, bool receiveShadows) => new RenderParams(material)
        {
            camera = _camera, layer = _layer, matProps = properties, worldBounds = Everywhere,
            shadowCastingMode = ShadowCastingMode.Off, receiveShadows = receiveShadows,
            lightProbeUsage = LightProbeUsage.Off, reflectionProbeUsage = ReflectionProbeUsage.Off,
        };

        private void AfterCamera(ScriptableRenderContext context, Camera camera)
        {
            if (_disposed || camera != _camera || _lastIssueFrame != Time.frameCount || _fenceFrame == _lastIssueFrame) return;
            SetReplayVisualsEnabled(false);
            // URP submits this camera before its endCameraRendering callback.
            // An immediate graphics fence therefore follows the actual draws,
            // rather than an Update-time fence before the registered draws ran.
            // CPU completion polling must not require asynchronous compute support.
            _fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.PixelProcessing);
            _fenceFrame = _lastIssueFrame;
            RenderedWarmFrames++;
        }

        /// <summary>
        /// Relinquish an abandoned preparation without freeing buffers still in
        /// use by the GPU. The rendering event keeps this helper alive even if
        /// its owning demo is destroyed. Further Issue calls are prohibited.
        /// </summary>
        public void RetireWhenReady()
        {
            if (_disposed || _retirementRequested) return;
            if (ReadyToDispose)
            {
                Dispose();
                return;
            }
            _retirementRequested = true;
            SetReplayVisualsEnabled(false);
            RenderPipelineManager.endContextRendering += RetireAfterContext;
        }

        private void RetireAfterContext(ScriptableRenderContext context, List<Camera> cameras)
        {
            if (_disposed) return;
            // If the owner disabled/destroyed the camera before it rendered,
            // the previous frame's registrations have now expired. Fence the
            // submitted context anyway; do not count this as a warmed frame.
            if (_fenceFrame != _lastIssueFrame && Time.frameCount > _lastIssueFrame)
            {
                _fence = Graphics.CreateGraphicsFence(GraphicsFenceType.CPUSynchronisation, SynchronisationStageFlags.PixelProcessing);
                _fenceFrame = _lastIssueFrame;
            }
            if (ReadyToDispose) Dispose();
        }

        public void Dispose()
        {
            if (_disposed) return;
            if (!ReadyToDispose)
                throw new InvalidOperationException("Keep prewarm buffers through four normal frames and the completed GPU fence before disposal.");
            _disposed = true;
            RenderPipelineManager.endCameraRendering -= AfterCamera;
            RenderPipelineManager.endContextRendering -= RetireAfterContext;
            ReleaseBuffers();
            // The borrowed display materials are never changed or destroyed.
        }

        private void ReleaseBuffers()
        {
            _vertices?.Dispose(); _indices?.Dispose(); _transforms?.Dispose(); _clips?.Dispose();
            _arguments?.Dispose(); _capVertices?.Dispose(); _capPlacements?.Dispose();
            _vertices = _indices = _transforms = _clips = _arguments = _capVertices = _capPlacements = null;
            for (int i = 0; i < _replayVisualRenderers.Count; i++)
                if (_replayVisualRenderers[i] != null) UnityEngine.Object.Destroy(_replayVisualRenderers[i].gameObject);
            for (int i = 0; i < _replayVisualMeshes.Count; i++)
                if (_replayVisualMeshes[i] != null) UnityEngine.Object.Destroy(_replayVisualMeshes[i]);
            _replayVisualRenderers.Clear();
            _replayVisualMeshes.Clear();
        }
    }
}
