using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Plugin = Zantetsu.Rendering.VpNativeDrawPlugin;

namespace Zantetsu.Rendering.Urp
{
    /// <summary>
    /// Draws a display's bodies and casters through the Direct3D 12 plugin inside URP's frame for a camera
    /// (DESIGN 4.5.8): the display hands this sink its runs of commands (<see cref="IVpNativeDrawSink"/>) while the
    /// camera begins to render, and two passes enqueued for that camera issue each run as plugin events -- one
    /// ExecuteIndirect over the run's commands, from the entries this camera's GPU selection wrote. The body pass,
    /// recorded after URP's opaques, issues one event per run; the caster pass, recorded after URP's main light shadow
    /// pass, issues one event per run per cascade into URP's own shadow map, with the slice's viewport, matrices and
    /// bias recomputed from the public ShadowUtils as URP computes them. Nothing is set per command on the CPU.
    /// <para>
    /// **What the passes bind.** The pipeline state is the plugin's, built once per material, variant and target from
    /// the variant the Editor compiled (<see cref="VpNativeShaderBinary"/>); the body's variant follows the main
    /// light's shadow keywords URP sets for the frame. The constants a stage reads from <c>$Globals</c> and
    /// <c>LightShadows</c> -- the main light, the shadow cascades, the VP surface constants, the camera's -- are
    /// assembled on the GPU in this frame's command stream from the globals Unity set (VpNativeConstantCopy.compute),
    /// with the view-projection as URP derives it from the public camera data; the per-material and per-draw
    /// constants (the material's values, the ambient probe, the default reflection) are written here. The vertex,
    /// instance, clip, selection and argument buffers are the batch's own; the shadow map is URP's.
    /// </para>
    /// <para>
    /// **What it does not do yet.** An XR pass (both eyes at once) is not drawn, and counted as such. The plugin not
    /// being available, a shader binary not being in the build, or a pipeline the plugin refuses are failures this
    /// route records; it never draws through Unity's calls instead.
    /// </para>
    /// </summary>
    public sealed class VpNativeDrawRoute : IVpNativeDrawSink, IDisposable
    {
        public const string CasterVariant = "caster-plugin";
        public const string CopyShaderResource = "VpNativeConstantCopy";

        // The main thread's work of this route, apart: not nested in one another, so that each is its own time. The
        // render thread's part (the plugin's event) is not here; it stands under the passes' own profiling samplers.
        private static readonly ProfilerMarker PointersMarker = new ProfilerMarker("Zantetsu.NativeDraw.Pointers");          // native pointers asked of Unity or taken from the cache
        private static readonly ProfilerMarker CpuConstantsMarker = new ProfilerMarker("Zantetsu.NativeDraw.CpuConstants");  // the per-material and per-draw constants, written and copied
        private static readonly ProfilerMarker GpuConstantsMarker = new ProfilerMarker("Zantetsu.NativeDraw.GpuConstants");  // the commands that assemble the stage constants on the GPU
        private static readonly ProfilerMarker EventDataMarker = new ProfilerMarker("Zantetsu.NativeDraw.EventData");        // the event data block and the plugin event's reservation
        private static readonly ProfilerMarker BodyPrepareMarker = new ProfilerMarker("Zantetsu.NativeDraw.BodyPrepare");    // the camera's matrices, the target, the variant
        private static readonly ProfilerMarker CasterSlicesMarker = new ProfilerMarker("Zantetsu.NativeDraw.CasterSlices");  // the cascades' matrices, viewports and biases
        private static readonly ProfilerMarker DeclareMarker = new ProfilerMarker("Zantetsu.NativeDraw.DeclareCasters");     // the caster bounds told to Unity's culling
        private static readonly int OffsetsId = Shader.PropertyToID("_VpNativeOffsets");
        private static readonly int DestinationId = Shader.PropertyToID("_VpNativeDestination");
        private static readonly int ConstantsId = Shader.PropertyToID("_VpNativeConstants");
        private static readonly int MatrixVpId = Shader.PropertyToID("_VpNativeMatrixVP");
        private static readonly int MatrixVId = Shader.PropertyToID("_VpNativeMatrixV");
        private static readonly int ProjectionId = Shader.PropertyToID("_VpNativeProjection");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpNativeInstanceMultiplier");
        private static readonly int ShadowBiasId = Shader.PropertyToID("_VpNativeShadowBias");
        private static readonly int LightDirectionId = Shader.PropertyToID("_VpNativeLightDirection");
        private static readonly int ShadowSliceSelectionId = Shader.PropertyToID("_VpNativeShadowSliceSelection");
        private static readonly int BaseMapId = Shader.PropertyToID("_BaseMap");
        private static readonly int BaseMapStId = Shader.PropertyToID("_BaseMap_ST");
        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int UsePaletteAtlasId = Shader.PropertyToID("_VpUsePaletteAtlas");
        private static readonly int PaletteSurfaceId = Shader.PropertyToID("_VpPaletteSurface");
        private static readonly int PaletteCapsId = Shader.PropertyToID("_VpPaletteCaps");
        private static readonly int CullId = Shader.PropertyToID("_Cull");

        private const int SlotCount = 48;
        private const uint Unused = 0xFFFFFFFFu;
        private const int ConstantAlignment = 256;
        private const int GpuConstantBytesPerPass = 256 * 1024;
        private const int DrawBlocks = 64;
        private const int MaxSplits = VpCullConditions.SplitCapacity;

        /// <summary>The slots of VpNativeConstantCopy.compute, by the name of the constant each fills.</summary>
        private static readonly (string name, int slot)[] Slots =
        {
            ("_MainLightPosition", 0), ("_MainLightColor", 1), ("_WorldSpaceCameraPos", 2), ("_GlobalMipBias", 3), ("unity_OrthoParams", 4),
            ("_VpSurfaceColorScale", 5), ("_VpSurfaceMaterial", 6), ("_VpPaletteAtlasEnabled", 7), ("_VpCutSurfaceColor", 8),
            ("_VpCutSurfaceDebugColor", 9), ("_VpCutSurfaceDebug", 10), ("_MainLightWorldToShadow", 11),
            ("_CascadeShadowSplitSpheres0", 12), ("_CascadeShadowSplitSpheres1", 13), ("_CascadeShadowSplitSpheres2", 14), ("_CascadeShadowSplitSpheres3", 15),
            ("_CascadeShadowSplitSphereRadii", 16), ("_MainLightShadowOffset0", 17), ("_MainLightShadowOffset1", 18), ("_MainLightShadowParams", 19),
            ("_MainLightShadowmapSize", 20), ("unity_MatrixVP", 21), ("unity_MatrixV", 22), ("glstate_matrix_projection", 23),
            ("_VpInstanceMultiplier", 24), ("_ShadowBias", 25), ("_LightDirection", 26), ("_VpShadowSliceSelection", 27),
            ("unity_StereoMatrixVP", 28), ("unity_StereoMatrixV", 29), ("unity_StereoWorldSpaceCameraPos", 30), ("unity_StereoEyeIndices", 31),
            ("unity_StereoMatrixP", 32), ("unity_StereoMatrixInvV", 33), ("unity_StereoCameraProjection", 34), ("unity_StereoCameraInvProjection", 35),
            ("unity_StereoMatrixInvVP", 36), ("unity_StereoMatrixInvP", 37),
        };

        private static readonly int StereoMatrixVpId = Shader.PropertyToID("_VpNativeStereoMatrixVP");
        private static readonly int StereoMatrixVId = Shader.PropertyToID("_VpNativeStereoMatrixV");
        private static readonly int StereoMatrixPId = Shader.PropertyToID("_VpNativeStereoMatrixP");
        private static readonly int StereoCameraProjectionId = Shader.PropertyToID("_VpNativeStereoCameraProjection");
        private static readonly int StereoCameraPosId = Shader.PropertyToID("_VpNativeStereoCameraPos");
        private readonly Matrix4x4[] _stereoVP = new Matrix4x4[2], _stereoV = new Matrix4x4[2], _stereoP = new Matrix4x4[2], _stereoCameraProjection = new Matrix4x4[2];
        private readonly Vector4[] _stereoPos = new Vector4[2];

        private readonly List<CameraPasses> _cameras = new List<CameraPasses>(2);
        private readonly Dictionary<PipelineKey, PipelineEntry> _pipelines = new Dictionary<PipelineKey, PipelineEntry>();
        private readonly int[] _gpuOffsets = new int[8];
        private readonly VpNativePointerCache _pointers = new VpNativePointerCache();
        private int _pointersSweptFrame = -1;
        private VpNativeDrawData _ringAfterDispose;
        private readonly Vector4[] _ambient = new Vector4[7];
        private int _ambientFrame = -1;
        private readonly MaterialPropertyBlock _declareProperties = new MaterialPropertyBlock();
        private GraphicsBuffer _zeroArguments;
        private int _declaredFrame = -1;
        private Camera _declaredCamera;
        private readonly List<Block> _blocks = new List<Block>(64);
        private ComputeShader _copy;
        private int _copyKernel;
        private VpNativeDrawData _drawData;
        private string _environmentFailure;
        private bool _environmentChecked;
        private bool _disposed;

        // A pipeline is one of a material, a variant and a draw state: the key allocates nothing per frame.
        private readonly struct PipelineKey : IEquatable<PipelineKey>
        {
            private readonly int _material;
            private readonly string _variant;
            private readonly VpNativeRenderState _state;

            public PipelineKey(Material material, string variant, in VpNativeRenderState state)
            {
                _material = material.GetInstanceID();
                _variant = variant;
                _state = state;
            }

            public bool Equals(PipelineKey other)
            {
                return _material == other._material && string.Equals(_variant, other._variant, StringComparison.Ordinal) && _state.Equals(other._state);
            }

            public override bool Equals(object obj) => obj is PipelineKey other && Equals(other);

            public override int GetHashCode() => (_material * 397) ^ (_variant != null ? _variant.GetHashCode() : 0) ^ _state.GetHashCode();
        }

        // One pipeline of this route: the plugin's, with the GPU-assembled constant blocks it needs and the CPU ones.
        private sealed class PipelineEntry
        {
            public VpNativePipeline pipeline;
            public readonly List<GpuBlock> gpuBlocks = new List<GpuBlock>(3);
            public readonly List<CpuBlock> cpuBlocks = new List<CpuBlock>(3);
            public int vertexArguments = -1, vertexVertices = -1, vertexInstances = -1, vertexClips = -1, vertexVisible = -1, vertexCullCommands = -1;
            public int pixelSpecCube = -1, pixelShadowMap = -1, pixelPaletteSurface = -1, pixelPaletteCaps = -1, pixelBaseMap = -1;
        }

        private sealed class GpuBlock
        {
            public int binding;
            public int size;
            public GraphicsBuffer offsets;
        }

        private sealed class CpuBlock
        {
            public int binding;
            public VpNativeConstantBlock block;
            public string name;
        }

        private struct Issue
        {
            public VpIndexedIndirectDrawBatch batch;
            public VpGpuIndexedGeometryBuffers buffers;
            public Material material;
            public int startCommand;
            public int commandCount;
            public int view;
        }

        private struct Block
        {
            public IntPtr data;
            public int frame;
            public uint serial;
        }

        // What the GPU-assembled stage constants are given beside the frame's globals. For a Single Pass Instanced
        // draw the two eyes' matrices stand in the route's stereo arrays (set before the issue) and `stereo` says so.
        private struct GlobalsInputs
        {
            public Matrix4x4 matrixVP, matrixV, projection;
            public int instanceMultiplier;
            public Vector4 shadowBias, lightDirection;
            public float sliceSelection;
            public bool stereo;
        }

        // What one issue binds beside the material's own: the selection's entries and list, the geometry, the shadow map.
        private struct Bound
        {
            public GraphicsBuffer arguments, visible, instances, clips, cullCommands, indexBuffer, vertexBuffer;
            public uint argumentsGeneration, visibleGeneration, instancesGeneration, clipsGeneration, cullCommandsGeneration, indexGeneration, vertexGeneration;
            public RenderTexture shadowMap;

            // The owners' CPU-write generations of the buffers: taken with the buffers, so that a pointer kept across
            // a write is asked again (see VpNativePointerCache.Buffer).
            public void TakeGenerations(VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers)
            {
                argumentsGeneration = batch.CullViewGeneration;
                visibleGeneration = batch.CullViewGeneration;
                instancesGeneration = batch.InstanceGeneration;
                clipsGeneration = batch.ClipGeneration;
                cullCommandsGeneration = batch.CullCommandGeneration;
                vertexGeneration = buffers.VertexGeneration;
                indexGeneration = buffers.IndexGeneration;
            }
        }

        private sealed class CameraPasses
        {
            public Camera camera;
            public BodyPass body;
            public CasterPass casters;
            public readonly List<Issue> bodies = new List<Issue>(8);
            public readonly List<Issue> casterRuns = new List<Issue>(8);
            public int frame;
        }

        /// <summary>Null: the body's variant follows the frame's main light shadow keywords. A name forces one, for a test.</summary>
        public string BodyVariantOverride { get; set; }

        /// <summary>The comparison sampler the shadow map is read with. Decided by the picture comparison of the shadow stage.</summary>
        public Plugin.SamplerKind ShadowCompareSampler { get; set; } = DefaultShadowCompareSampler ?? (SystemInfo.usesReversedZBuffer ? Plugin.SamplerKind.CompareLinearClampGreaterEqual : Plugin.SamplerKind.CompareLinearClampLessEqual);

        /// <summary>
        /// What a new route reads the shadow map with when set; null (the product) follows the depth convention: with a
        /// reversed Z buffer the shadow transform and the map both hold reversed depth, so a fragment is lit where its
        /// depth is GREATER or equal to the stored one (play-40, 2026-10-08: LESS_EQUAL never shadowed a display surface;
        /// GREATER_EQUAL shadows the box top the renderers shadow). A test may set it to compare the two by picture.
        /// </summary>
        public static Plugin.SamplerKind? DefaultShadowCompareSampler;

        /// <summary>
        /// Null: the front face is counter-clockwise exactly when URP renders the target with a flipped projection
        /// (what Unity's own draws do). A value forces it, for the picture comparison that establishes the rule.
        /// </summary>
        public bool? FrontCounterClockwise { get; set; }

        /// <summary>
        /// The rasterizer bias of the caster draws, beside the vertex-stage bias URP computes. URP's own casters are
        /// drawn with SetGlobalDepthBias(1, 2.5); what Unity turns the constant 1 into for a Direct3D 12 rasterizer
        /// is not public. Measured against URP's soft-shadow picture on a D16 map (2026-10-07, play-20/21): the
        /// difference falls from a depth bias of 0 (mean 4.7) to a plateau from about 160 on (0.9, the same up to
        /// 65536), and the slope term 2.5 is needed (0: 2.2). 256 is taken, inside the plateau; the city's picture is
        /// to confirm it.
        /// </summary>
        public int CasterDepthBias { get; set; } = 256;

        /// <summary>
        /// The rasterizer bias as the pipeline takes it: away from the light. With a reversed Z buffer the depth grows
        /// toward the light, so the bias is negated there (play-40: +256 under reversed Z shadowed every lit face of the
        /// boxes against itself).
        /// </summary>
        public int CasterDepthBiasSigned => SystemInfo.usesReversedZBuffer ? -CasterDepthBias : CasterDepthBias;

        public float CasterSlopeScaledDepthBiasSigned => SystemInfo.usesReversedZBuffer ? -CasterSlopeScaledDepthBias : CasterSlopeScaledDepthBias;

        public float CasterSlopeScaledDepthBias { get; set; } = 2.5f;

        // Counters, for the record.
        public long Enqueued { get; private set; }
        public long Recorded { get; private set; }
        public long BodiesTaken { get; private set; }
        public long CastersTaken { get; private set; }
        public long BodiesIssued { get; private set; }
        public long CastersIssued { get; private set; }
        public long CasterRunsNotDrawn { get; private set; }

        /// <summary>Runs taken for a frame whose render graph was never executed (see <see cref="TryEnqueue"/>).</summary>
        public long BodiesDropped { get; private set; }

        public long CasterRunsDropped { get; private set; }

        /// <summary>How many frames the display's caster bounds were told to Unity's culling (see <see cref="DeclareCasterBounds"/>).</summary>
        public long CasterBoundsDeclared { get; private set; }

        /// <summary>How many issues were refused here for a shadow map of a format the plugin has no sampled format for.</summary>
        public long ShadowMapFormatRefusals { get; private set; }

        /// <summary>The native pointers: how many were asked of Unity, how many a kept one answered.</summary>
        public long PointerAcquisitions => _pointers.Acquisitions;

        public long PointerHits => _pointers.Hits;
        public string LastCasterSkipReason { get; private set; }
        public int LastSplitCount { get; private set; }
        public long XrFramesNotDrawn { get; private set; }
        public long Refused { get; private set; }
        public long RingFull { get; private set; }
        public long PipelineFailures { get; private set; }
        public string LastFailure { get; private set; }
        public int LastFrameIssues { get; private set; }
        public string LastBodyVariant { get; private set; }

        /// <summary>The results the plugin answered for the blocks issued, by result, once read back.</summary>
        public readonly long[] ResultsByKind = new long[8];

        /// <summary>
        /// Diagnosis: the GPU-assembled constant blocks of the last body issue -- the constant buffer's name and stage,
        /// its byte offset in the body pass's GPU constant buffer, and its size -- for a test that reads the buffer
        /// back (<see cref="TryReadBodyGpuConstantsForDiagnosis"/>).
        /// </summary>
        public readonly List<(VpNativePipeline.BindingInfo binding, int offset, int size)> LastBodyGpuBlocks = new List<(VpNativePipeline.BindingInfo, int, int)>();

        /// <summary>
        /// Diagnosis: the GPU-assembled constant blocks of the last caster run's issues -- one entry a block a split
        /// -- for a shot run that reads the caster pass's buffer back (<see cref="TryReadCasterGpuConstantsForDiagnosis"/>).
        /// </summary>
        public readonly List<(VpNativePipeline.BindingInfo binding, int offset, int size, int split)> LastCasterGpuBlocks = new List<(VpNativePipeline.BindingInfo, int, int, int)>();

        /// <summary>
        /// Diagnosis only -- a shot run or a test: the GPU-assembled constant blocks of the last body issue and of the
        /// last caster run (every split) for <paramref name="camera"/>, read back (**waiting for the GPU**) and
        /// written constant by constant with the layout of each variant's shader binary, one line a block. Null when
        /// the camera has not been drawn for. The product never calls this.
        /// </summary>
        public string DescribeGpuBlocksForDiagnosis(Camera camera)
        {
            if (LastBodyVariant == null)
            {
                return null;
            }

            var words = new uint[GpuConstantBytesPerPass / 4];
            if (!TryReadBodyGpuConstantsForDiagnosis(camera, words))
            {
                return null;
            }

            var text = new StringBuilder();
            text.Append("body variant ").Append(LastBodyVariant).Append('\n');
            if (!AppendBlocks(text, LastBodyVariant, words, LastBodyGpuBlocks.ConvertAll(b => (b.binding, b.offset, b.size, -1))))
            {
                return text.ToString();
            }

            if (TryReadCasterGpuConstantsForDiagnosis(camera, words))
            {
                text.Append("caster variant ").Append(CasterVariant).Append('\n');
                AppendBlocks(text, CasterVariant, words, LastCasterGpuBlocks);
            }

            return text.ToString();
        }

        private static bool AppendBlocks(StringBuilder text, string variant, uint[] words, List<(VpNativePipeline.BindingInfo binding, int offset, int size, int split)> blocks)
        {
            var asset = Resources.Load<TextAsset>(VpNativeShaderBinary.ResourceFolder + "/" + variant);
            if (asset == null || !VpNativeShaderBinary.TryRead(asset.bytes, out VpNativeShaderBinary binary, out string _))
            {
                text.Append("the variant's binary could not be read: ").Append(variant).Append('\n');
                return false;
            }

            foreach ((VpNativePipeline.BindingInfo binding, int offset, int size, int split) block in blocks)
            {
                VpNativeShaderBinary.Stage stage = binary.FindStage(block.binding.Stage == Plugin.Stage.Vertex ? VpNativeShaderBinary.StageKind.Vertex : VpNativeShaderBinary.StageKind.Pixel);
                VpNativeShaderBinary.ConstantBuffer layout = stage?.FindConstantBuffer(block.binding.Name);
                if (layout == null) continue;
                if (block.split >= 0) text.Append("split ").Append(block.split).Append(' ');
                text.Append(block.binding.Stage).Append(' ').Append(block.binding.Name).Append(" @").Append(block.offset).Append(" (").Append(block.size).Append(" B):");
                foreach (VpNativeShaderBinary.Constant constant in layout.Constants)
                {
                    int at = (block.offset + constant.Offset) / 4;
                    int n = Mathf.Min(constant.Bytes / 4, 128);
                    text.Append(' ').Append(constant.Name).Append("=(");
                    for (int i = 0; i < n && at + i < words.Length; i++)
                    {
                        if (i > 0) text.Append(',');
                        text.Append(BitConverter.ToSingle(BitConverter.GetBytes(words[at + i]), 0).ToString("G5"));
                    }

                    text.Append(n < constant.Bytes / 4 ? ",...)" : ")");
                }

                text.Append('\n');
            }

            return true;
        }

        /// <summary>Diagnosis only: as <see cref="TryReadBodyGpuConstantsForDiagnosis"/>, for the caster pass's buffer.</summary>
        public bool TryReadCasterGpuConstantsForDiagnosis(Camera camera, uint[] into)
        {
            CameraPasses passes = PassesOf(camera, false);
            GraphicsBuffer buffer = passes?.casters.GpuConstantsForDiagnosis;
            if (buffer == null || into == null)
            {
                return false;
            }

            buffer.GetData(into, 0, 0, Mathf.Min(into.Length, buffer.count));
            return true;
        }

        /// <summary>
        /// Diagnosis only -- tests: reads back, **waiting for the GPU**, the body pass's GPU constant buffer for
        /// <paramref name="camera"/> (the blocks of <see cref="LastBodyGpuBlocks"/> lie in it). False when the camera
        /// has not been drawn for. The product never calls this.
        /// </summary>
        public bool TryReadBodyGpuConstantsForDiagnosis(Camera camera, uint[] into)
        {
            CameraPasses passes = PassesOf(camera, false);
            GraphicsBuffer buffer = passes?.body.GpuConstantsForDiagnosis;
            if (buffer == null || into == null)
            {
                return false;
            }

            buffer.GetData(into, 0, 0, Mathf.Min(into.Length, buffer.count));
            return true;
        }

        /// <summary>
        /// Whether the route can draw at all: URP recording through the render graph, Direct3D 12, the plugin
        /// loaded and of this contract, its compute shader in the build. Null when it can; otherwise why not.
        /// </summary>
        public static string CheckEnvironment()
        {
            string failure = VpGpuCullCameraRoute.CheckEnvironment();
            if (failure != null)
            {
                return failure;
            }

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                return "the graphics device is " + SystemInfo.graphicsDeviceType + ", not Direct3D 12";
            }

            if (!Plugin.TryLoad(out failure))
            {
                return failure;
            }

            if (Resources.Load<ComputeShader>(CopyShaderResource) == null)
            {
                return "the compute shader '" + CopyShaderResource + "' is not in the build";
            }

            return null;
        }

        /// <summary>
        /// Enqueues the passes for <paramref name="camera"/>'s rendering that is about to begin, and makes this the
        /// display's sink. To be called from RenderPipelineManager.beginCameraRendering, after the GPU selection was
        /// enqueued and before the display registers its draws. False, with the reason, when it cannot.
        /// </summary>
        public bool TryEnqueue(Camera camera, IVpNativeDrawHost display, out string failure)
        {
            ThrowIfDisposed();
            if (!_environmentChecked)
            {
                _environmentChecked = true;
                _environmentFailure = CheckEnvironment();
                if (_environmentFailure == null)
                {
                    _copy = Resources.Load<ComputeShader>(CopyShaderResource);
                    _copyKernel = _copy.FindKernel("AssembleGlobals");
                    _drawData = new VpNativeDrawData(DrawBlocks, 16, 8, 8192) { Name = "route ring " + (++s_ringsMade) };
                    VpNativeDrawRelease.Register(_drawData);
                }
            }

            failure = _environmentFailure;
            if (failure != null)
            {
                return false;
            }

            if (camera == null || display == null)
            {
                failure = "no camera or no display";
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
                failure = "the camera has no URP renderer to enqueue a pass on";
                return false;
            }

            ForgetDeadCameras();
            CameraPasses passes = PassesOf(camera, true);
            if (passes.bodies.Count > 0 || passes.casterRuns.Count > 0)
            {
                // Runs taken for an earlier frame that was never recorded: the frame's render graph was not executed
                // (reset by an error of another pass, or the camera did not render). Counted, so that "taken" and
                // "issued" differing has a name.
                BodiesDropped += passes.bodies.Count;
                CasterRunsDropped += passes.casterRuns.Count;
                LastFailure = "a frame's runs were never recorded (the camera's frame was not executed)";
            }

            passes.bodies.Clear();
            passes.casterRuns.Clear();
            passes.frame = Time.frameCount;
            if (_pointersSweptFrame != passes.frame)
            {
                // Once a frame: pointers of buffers disposed and textures destroyed since are dropped, so that a new
                // object is asked for and a dead one is never bound (a pass over the kept entries, about 15).
                _pointersSweptFrame = passes.frame;
                _pointers.DropDead();
            }

            display.NativeDrawSink = this;
            renderer.EnqueuePass(passes.casters);
            renderer.EnqueuePass(passes.body);
            Enqueued++;
            return true;
        }

        /// <summary>
        /// Forgets a camera that is no longer drawn for: its passes go, and the GPU constant buffers its issued
        /// events may still read are released by <see cref="VpNativeDrawRelease"/> once the route's blocks are all
        /// consumed or passed (the camera's events are blocks of this route's ring, which stays with the route).
        /// </summary>
        public void Forget(Camera camera)
        {
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_cameras[i].camera, camera))
                {
                    CameraPasses passes = _cameras[i];
                    _cameras.RemoveAt(i);
                    DeferCameraBuffers(passes);
                }
            }
        }

        // A camera's GPU constant buffers, released once no event of this route can still read them: the camera's
        // events are blocks of the route's ring, so the condition is the ring's blocks at this moment all released --
        // which the keeper judges by a ring of its own; here the buffers wait on the route's own ring through a
        // holder that answers for it.
        private void DeferCameraBuffers(CameraPasses passes)
        {
            VpNativeDrawRelease.Defer("VP native draw route camera", IssuedEventsWait(),
                new IDisposable[] { passes.body.TakeGpuConstants(), passes.casters.TakeGpuConstants() });
        }

        public void AddBody(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer)
        {
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null)
            {
                return;
            }

            BodiesTaken++;
            passes.bodies.Add(new Issue { batch = batch, buffers = buffers, material = material, startCommand = startCommand, commandCount = commandCount, view = view });
        }

        public void AddCasters(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer)
        {
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null)
            {
                return;
            }

            CastersTaken++;
            passes.casterRuns.Add(new Issue { batch = batch, buffers = buffers, material = material, startCommand = startCommand, commandCount = commandCount, view = view });
            DeclareCasterBounds(camera, batch, buffers, material, layer);
        }

        /// <summary>
        /// Tells Unity's culling, for this camera's frame, where the display's casters are. URP renders its main light
        /// shadow map -- and computes the cascades, and sets the receiver constants -- only when the camera's culling
        /// found a shadow caster; the display's draws are not renderers, so by themselves they are not found, and a
        /// scene whose casters are all the display's would get no shadow map at all. This is the same declaration
        /// Unity's own route makes with every Graphics.RenderPrimitivesIndexedIndirect of ShadowCastingMode.On: a draw
        /// registered with the batch's world bounds. Here it is a draw of NO instance (one entry of zeros, ShadowsOnly),
        /// so it draws nothing in any pass and nothing is drawn twice; it costs Unity one zero-instance indirect draw
        /// per cascade in its shadow pass, whatever the command count. Once per camera per frame.
        /// </summary>
        private void DeclareCasterBounds(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int layer)
        {
            int frame = Time.frameCount;
            if (_declaredFrame == frame && ReferenceEquals(_declaredCamera, camera))
            {
                return;
            }

            _declaredFrame = frame;
            _declaredCamera = camera;
            using (DeclareMarker.Auto())
            {
                if (_zeroArguments == null)
                {
                    _zeroArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, GraphicsBuffer.IndirectDrawIndexedArgs.size) { name = "VP Native Caster Bounds (no instance)" };
                    _zeroArguments.SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs() });
                }

                var parameters = new RenderParams(material)
                {
                    camera = camera,
                    layer = layer,
                    matProps = _declareProperties,
                    worldBounds = batch.WorldBounds,
                    shadowCastingMode = ShadowCastingMode.ShadowsOnly,
                    receiveShadows = false,
                };
                Graphics.RenderPrimitivesIndexedIndirect(parameters, MeshTopology.Triangles, buffers.IndexBuffer, _zeroArguments, 1, 0);
                CasterBoundsDeclared++;
            }
        }

        /// <summary>The plugin's counters and this route's, for the log.</summary>
        public string Describe()
        {
            var text = new StringBuilder();
            text.Append("native draw route: enqueued ").Append(Enqueued).Append(", recorded ").Append(Recorded)
                .Append(", bodies taken ").Append(BodiesTaken).Append(", issued ").Append(BodiesIssued)
                .Append(" (last variant ").Append(LastBodyVariant ?? "none").Append(")")
                .Append(", caster runs taken ").Append(CastersTaken).Append(", caster issues ").Append(CastersIssued)
                .Append(" (last splits ").Append(LastSplitCount).Append("), caster runs not drawn ").Append(CasterRunsNotDrawn)
                .Append(LastCasterSkipReason != null ? " (last: " + LastCasterSkipReason + ")" : "")
                .Append(", XR frames not drawn ").Append(XrFramesNotDrawn)
                .Append(", refused ").Append(Refused).Append(", ring full ").Append(RingFull)
                .Append(", pipeline failures ").Append(PipelineFailures)
                .Append(", shadow map format refusals ").Append(ShadowMapFormatRefusals)
                .Append(", runs of frames never executed: bodies ").Append(BodiesDropped).Append(", casters ").Append(CasterRunsDropped)
                .Append(", caster bounds declared ").Append(CasterBoundsDeclared)
                .Append(", native pointers asked ").Append(_pointers.Acquisitions).Append(" / kept ").Append(_pointers.Hits)
                .Append(", issues after dispose ").Append(IssuesAfterDispose).Append(", buffers retired through the route ").Append(BuffersRetired)
                .Append(", events never run ").Append(EventsNeverRan).Append(", results lost to reuse ").Append(ResultsLost).Append("; ").Append(VpNativeDrawRelease.Describe())
                .Append(", last frame issues ").Append(LastFrameIssues);
            VpNativeDrawData ring = _drawData ?? _ringAfterDispose;
            if (ring != null && !ring.IsDisposed)
            {
                text.Append("; ").Append(ring.Name).Append(": in flight ").Append(ring.InFlight).Append(" (prepared ").Append(ring.InFlightIn(VpNativeDrawData.BlockState.Prepared))
                    .Append(", recorded ").Append(ring.InFlightIn(VpNativeDrawData.BlockState.Recorded)).Append(", submitted ").Append(ring.InFlightIn(VpNativeDrawData.BlockState.Submitted))
                    .Append("), never recorded ").Append(ring.NeverRecorded).Append(", discards confirmed ").Append(ring.DiscardsConfirmed);
            }
            if (LastFailure != null) text.Append("; last failure: ").Append(LastFailure);
            text.Append("; results:");
            for (int i = 0; i < ResultsByKind.Length; i++)
            {
                if (ResultsByKind[i] != 0) text.Append(' ').Append((Plugin.Result)i).Append(' ').Append(ResultsByKind[i]);
            }

            if (_environmentFailure == null && _environmentChecked)
            {
                text.Append("; plugin: ").Append(Plugin.ReadCounters());
                foreach (PipelineEntry entry in _pipelines.Values)
                {
                    text.Append("; pipeline ").Append(entry.pipeline.Describe());
                }
            }

            return text.ToString();
        }

        /// <summary>
        /// Reads the plugin's answers for every issue so far (<see cref="ResultsByKind"/>, <see cref="Refused"/>). The
        /// passes do this as they record; a test that draws once and ends asks here, after the frame has rendered.
        /// </summary>
        public void CollectResults()
        {
            CollectResults(int.MaxValue);
        }

        /// <summary>
        /// Ends the route: nothing is issued after this (a pass already enqueued for the frame issues nothing and
        /// counts it, <see cref="IssuesAfterDispose"/>). What the events already issued still need -- the event data
        /// blocks, the pipelines they name, the buffers the GPU reads for them -- is not released here but handed to
        /// <see cref="VpNativeDrawRelease"/>, which releases it once every block is consumed or confirmed never to
        /// run. A camera disabled or destroyed right after an issue is therefore safe: the issued event runs or is
        /// confirmed discarded, and only then do its data and resources go.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var owed = new List<IDisposable>();
            foreach (PipelineEntry entry in _pipelines.Values)
            {
                owed.Add(entry.pipeline);
                foreach (GpuBlock block in entry.gpuBlocks) owed.Add(block.offsets);
            }

            _pipelines.Clear();
            foreach (CameraPasses passes in _cameras)
            {
                owed.Add(passes.body.TakeGpuConstants());
                owed.Add(passes.casters.TakeGpuConstants());
            }

            _cameras.Clear();
            owed.Add(_zeroArguments);
            _zeroArguments = null;
            _pointers.Clear();
            _blocks.Clear();
            _ringAfterDispose = _drawData;
            VpNativeDrawRelease.Defer("VP native draw route" + (_drawData != null ? " (" + _drawData.Name + ")" : ""), _drawData, owed);
            _drawData = null;
        }

        /// <summary>
        /// A buffer an owner (the display's batch or geometry) is done with that this route's issued events may still
        /// name: disposed once every block issued so far is consumed or confirmed never to run -- through the keeper,
        /// which waits on this route's ring (the one in use, or the one handed over at dispose). An owner that has no
        /// route disposes its buffers itself; one that drew through several routes waits on all of them
        /// (<see cref="VpNativeDrawRelease.Retire"/>).
        /// </summary>
        public void Retire(GraphicsBuffer buffer)
        {
            if (buffer == null)
            {
                return;
            }

            BuffersRetired++;
            VpNativeDrawRelease.Defer("VP native draw route retired buffer", IssuedEventsWait(), new IDisposable[] { buffer });
        }

        /// <summary>
        /// The wait on every event this route has issued so far (the ring's blocks up to now; the ring in use, or the
        /// one handed over at dispose): null when none can still be read.
        /// </summary>
        public VpNativeDrawRelease.IRingWait IssuedEventsWait()
        {
            VpNativeDrawData ring = _drawData ?? _ringAfterDispose;
            if (ring == null || ring.IsDisposed || ring.InFlight == 0)
            {
                return null;
            }

            return new VpNativeDrawRelease.RingView(ring);
        }

        /// <summary>How many buffers owners gave up to this route for a release after their events.</summary>
        public long BuffersRetired { get; private set; }

        /// <summary>The ring's blocks that may still be read by the rendering thread (the ring in use, or the one handed over at dispose).</summary>
        public int BlocksInFlight => (_drawData ?? _ringAfterDispose) is { IsDisposed: false } ring ? ring.InFlight : 0;

        /// <summary>Of those, how many are in <paramref name="state"/>.</summary>
        public int BlocksInFlightIn(VpNativeDrawData.BlockState state) => (_drawData ?? _ringAfterDispose) is { IsDisposed: false } ring ? ring.InFlightIn(state) : 0;

        /// <summary>Issued blocks whose events never ran (their commands cleared or orphaned), confirmed by a sentinel or the device gone.</summary>
        public long EventsNeverRan { get; private set; }

        /// <summary>Issued blocks whose slot was reused before their result was read here (the result is lost to the tally, nothing else).</summary>
        public long ResultsLost { get; private set; }

        private static int s_ringsMade;

        /// <summary>Issues a pass would have made after the route was disposed (none are made).</summary>
        public long IssuesAfterDispose { get; private set; }

        // A camera destroyed since: its passes go, and the buffers its events may still read are deferred.
        private void ForgetDeadCameras()
        {
            for (int i = _cameras.Count - 1; i >= 0; i--)
            {
                if (_cameras[i].camera != null)
                {
                    continue;
                }

                CameraPasses passes = _cameras[i];
                _cameras.RemoveAt(i);
                DeferCameraBuffers(passes);
            }
        }

        private CameraPasses PassesOf(Camera camera, bool make)
        {
            for (int i = 0; i < _cameras.Count; i++)
            {
                if (ReferenceEquals(_cameras[i].camera, camera))
                {
                    return _cameras[i];
                }
            }

            if (!make)
            {
                return null;
            }

            var passes = new CameraPasses { camera = camera };
            passes.body = new BodyPass(this, passes);
            passes.casters = new CasterPass(this, passes);
            _cameras.Add(passes);
            return passes;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpNativeDrawRoute));
            }
        }

        // Reads the plugin's answers for blocks issued in earlier frames. A block whose slot was reused since (its
        // serial no longer in the header) had its result lost to the tally; one the ring has released without a
        // result never ran (confirmed by a sentinel, or the device gone). No frame count decides anything here.
        private void CollectResults(int frame)
        {
            VpNativeDrawData ring = _drawData;
            for (int i = _blocks.Count - 1; i >= 0; i--)
            {
                Block block = _blocks[i];
                if (block.frame >= frame)
                {
                    continue;
                }

                if (VpNativeDrawData.SerialOf(block.data) != block.serial)
                {
                    ResultsLost++;
                    _blocks.RemoveAt(i);
                    continue;
                }

                Plugin.Result? result = VpNativeDrawData.ResultOf(block.data);
                if (result == null)
                {
                    if (ring != null && ring.StateOf(block.serial) == VpNativeDrawData.BlockState.Free)
                    {
                        EventsNeverRan++;
                        _blocks.RemoveAt(i);
                    }

                    continue;
                }

                ResultsByKind[Mathf.Clamp((int)result.Value, 0, ResultsByKind.Length - 1)]++;
                if (result.Value != Plugin.Result.Recorded)
                {
                    Refused++;
                    LastFailure = "the plugin refused an issue: " + result.Value;
                }

                _blocks.RemoveAt(i);
            }
        }

        // ---- the variants ------------------------------------------------------------------------------------------

        /// <summary>
        /// The body variant of a frame: the shadow keywords URP sets for it -- none when the main light casts no
        /// shadow this frame; the single map or the cascades by the cascade count; soft when the light is soft and
        /// the pipeline supports it (per-light soft shadow quality is supported on this platform, so the one soft
        /// keyword). The names are those of VpNativeShaderBake.
        /// </summary>
        public static string BodyVariantFor(UniversalLightData lightData, UniversalShadowData shadowData)
        {
            int index = lightData.mainLightIndex;
            Light light = index >= 0 ? lightData.visibleLights[index].light : null;
            bool shadows = shadowData.supportsMainLightShadows && light != null && light.shadows != LightShadows.None
                && lightData.visibleLights[index].lightType == LightType.Directional;
            if (!shadows)
            {
                return "forward-plugin-noshadow";
            }

            bool soft = light.shadows == LightShadows.Soft && shadowData.supportsSoftShadows;
            bool cascade = shadowData.mainLightShadowCascadesCount > 1;
            return cascade ? (soft ? "forward-plugin-cascade-soft" : "forward-plugin-cascade") : (soft ? "forward-plugin-shadows-soft" : "forward-plugin-shadows");
        }

        // The names of the variants, fixed strings: nothing is concatenated per frame.
        private static string VariantName(string body, bool stereo)
        {
            if (!stereo) return body;
            switch (body)
            {
                case "forward-plugin-noshadow": return "forward-plugin-noshadow-stereo";
                case "forward-plugin-shadows": return "forward-plugin-shadows-stereo";
                case "forward-plugin-shadows-soft": return "forward-plugin-shadows-soft-stereo";
                case "forward-plugin-cascade": return "forward-plugin-cascade-stereo";
                case "forward-plugin-cascade-soft": return "forward-plugin-cascade-soft-stereo";
                default: return body + "-stereo";
            }
        }

        // ---- pipelines ---------------------------------------------------------------------------------------------

        private PipelineEntry PipelineFor(Material material, string variant, VpNativeRenderState state, out string failure)
        {
            failure = null;
            var key = new PipelineKey(material, variant, state);
            if (_pipelines.TryGetValue(key, out PipelineEntry entry))
            {
                return entry;
            }

            var asset = Resources.Load<TextAsset>(VpNativeShaderBinary.ResourceFolder + "/" + variant);
            if (asset == null)
            {
                failure = "the shader binary '" + variant + "' is not in the build";
                return null;
            }

            if (!VpNativeShaderBinary.TryRead(asset.bytes, out VpNativeShaderBinary binary, out failure))
            {
                return null;
            }

            Texture baseMap = material.HasProperty(BaseMapId) ? material.GetTexture(BaseMapId) : null;
            Texture specCube = ReflectionProbe.defaultTexture;
            Texture paletteSurface = Shader.GetGlobalTexture(PaletteSurfaceId);
            Texture paletteCaps = Shader.GetGlobalTexture(PaletteCapsId);
            Plugin.SamplerKind compare = ShadowCompareSampler;
            (Plugin.SamplerKind, int) SamplerOf(string name)
            {
                Texture texture;
                switch (name)
                {
                    case "_BaseMap": texture = baseMap; break;
                    case "unity_SpecCube0": texture = specCube; break;
                    case "_VpPaletteSurface": texture = paletteSurface; break;
                    case "_VpPaletteCaps": texture = paletteCaps; break;
                    case "": return (compare, 1);
                    default: texture = null; break;
                }

                Plugin.SamplerKind kind = Plugin.SamplerOf(texture, out int anisotropy);
                return (kind, anisotropy);
            }

            if (!VpNativePipeline.TryCreate(variant + " " + material.name, binary, state, SamplerOf, out VpNativePipeline pipeline, out failure))
            {
                return null;
            }

            entry = new PipelineEntry { pipeline = pipeline };
            foreach (VpNativeShaderBinary.Stage stage in binary.Stages)
            {
                Plugin.Stage kind = stage.Kind == VpNativeShaderBinary.StageKind.Vertex ? Plugin.Stage.Vertex : Plugin.Stage.Pixel;
                foreach (VpNativeShaderBinary.ConstantBuffer buffer in stage.ConstantBuffers)
                {
                    int binding = pipeline.Find(kind, Plugin.BindingKind.ConstantBuffer, buffer.Name);
                    if (binding < 0)
                    {
                        continue; // the command constant
                    }

                    // The blocks whose values are the frame's: Unity's globals, URP's shadow receiver constants, and
                    // the stereo blocks (UnityStereoViewBuffer, UnityStereoEyeIndices) of a Single Pass Instanced draw.
                    if (buffer.Name == "$Globals" || buffer.Name == "LightShadows" || buffer.Name.StartsWith("UnityStereo", StringComparison.Ordinal))
                    {
                        var offsets = new uint[SlotCount];
                        for (int i = 0; i < offsets.Length; i++) offsets[i] = Unused;
                        foreach ((string name, int slot) in Slots)
                        {
                            VpNativeShaderBinary.Constant constant = buffer.Find(name);
                            if (constant != null) offsets[slot] = (uint)constant.Offset;
                        }

                        var table = new GraphicsBuffer(GraphicsBuffer.Target.Structured, SlotCount, sizeof(uint)) { name = "VP Native Offsets " + buffer.Name };
                        table.SetData(offsets);
                        entry.gpuBlocks.Add(new GpuBlock { binding = binding, size = buffer.Size, offsets = table });
                    }
                    else
                    {
                        entry.cpuBlocks.Add(new CpuBlock { binding = binding, block = new VpNativeConstantBlock(buffer), name = buffer.Name });
                    }
                }
            }

            entry.vertexArguments = pipeline.Find(Plugin.Stage.Vertex, Plugin.BindingKind.Buffer, "_VpNativeArguments");
            entry.vertexVertices = pipeline.Find(Plugin.Stage.Vertex, Plugin.BindingKind.Buffer, "_VpVertices");
            entry.vertexInstances = pipeline.Find(Plugin.Stage.Vertex, Plugin.BindingKind.Buffer, "_VpInstanceObjectToWorld");
            entry.vertexClips = pipeline.Find(Plugin.Stage.Vertex, Plugin.BindingKind.Buffer, "_VpInstanceClip");
            entry.vertexVisible = pipeline.Find(Plugin.Stage.Vertex, Plugin.BindingKind.Buffer, "_VpVisible");
            entry.vertexCullCommands = pipeline.Find(Plugin.Stage.Vertex, Plugin.BindingKind.Buffer, "_VpCullCommands");
            entry.pixelSpecCube = pipeline.Find(Plugin.Stage.Pixel, Plugin.BindingKind.Texture, "unity_SpecCube0");
            entry.pixelShadowMap = pipeline.Find(Plugin.Stage.Pixel, Plugin.BindingKind.Texture, "_MainLightShadowmapTexture");
            entry.pixelPaletteSurface = pipeline.Find(Plugin.Stage.Pixel, Plugin.BindingKind.Texture, "_VpPaletteSurface");
            entry.pixelPaletteCaps = pipeline.Find(Plugin.Stage.Pixel, Plugin.BindingKind.Texture, "_VpPaletteCaps");
            entry.pixelBaseMap = pipeline.Find(Plugin.Stage.Pixel, Plugin.BindingKind.Texture, "_BaseMap");
            _pipelines.Add(key, entry);
            return entry;
        }

        // The per-material and per-draw constants Unity would give the draw: the material's own values, and what a
        // renderer outside every probe gets -- the scene's ambient probe and the default reflection (the batch's draws
        // leave both probe usages off; measured against a MeshRenderer 2026-10-06). The material's values are read
        // every frame (a material may be changed by its owner; reading allocates nothing); the ambient probe is packed
        // once a frame for every issue of the route. Nothing here allocates.
        private void FillCpuBlock(CpuBlock block, Material material, int frame)
        {
            VpNativeConstantBlock constants = block.block;
            Array.Clear(constants.Bytes, 0, constants.Bytes.Length);
            if (block.name == "UnityPerMaterial")
            {
                constants.TrySet("_BaseMap_ST", material.HasProperty(BaseMapStId) ? material.GetVector(BaseMapStId) : new Vector4(1f, 1f, 0f, 0f));
                Color baseColor = material.HasProperty(BaseColorId) ? material.GetColor(BaseColorId) : Color.white;
                if (QualitySettings.activeColorSpace == ColorSpace.Linear)
                {
                    baseColor = baseColor.linear;
                }

                constants.TrySet("_BaseColor", (Vector4)baseColor);
                constants.TrySet("_VpUsePaletteAtlas", material.HasProperty(UsePaletteAtlasId) ? material.GetFloat(UsePaletteAtlasId) : 0f);
            }
            else if (block.name == "UnityPerDraw")
            {
                if (_ambientFrame != frame)
                {
                    _ambientFrame = frame;
                    VpNativeConstants.PackSphericalHarmonics(RenderSettings.ambientProbe, _ambient);
                }

                constants.TrySet("unity_SHAr", _ambient[0]);
                constants.TrySet("unity_SHAg", _ambient[1]);
                constants.TrySet("unity_SHAb", _ambient[2]);
                constants.TrySet("unity_SHBr", _ambient[3]);
                constants.TrySet("unity_SHBg", _ambient[4]);
                constants.TrySet("unity_SHBb", _ambient[5]);
                constants.TrySet("unity_SHC", _ambient[6]);
                constants.TrySet("unity_SpecCube0_HDR", ReflectionProbe.defaultTextureHDRDecodeValues);
                constants.TrySet("unity_LightData", new Vector4(0f, 0f, 1f, 0f));
            }
        }

        // ---- one issue ---------------------------------------------------------------------------------------------

        // Records one issue -- the GPU assembly of the stage constants, then the plugin event -- into the pass's
        // command buffer. False, counted and with the reason kept, when nothing was issued.
        // The native pointers one issue binds, taken from the cache in one go (the Pointers marker), so that what
        // Unity is asked is apart from what is written.
        private struct BoundPointers
        {
            public IntPtr arguments, visible, instances, clips, cullCommands, indexBuffer, vertexBuffer, specCube, paletteSurface, paletteCaps, baseMap, shadowMap, gpuConstants;
            public Texture specCubeTexture, paletteSurfaceTexture, paletteCapsTexture, baseMapTexture;
            // A texture that is a render texture is bound through its colour render buffer handle (resolved by Unity
            // when the event runs), not a texture pointer that a re-creation would leave stale.
            public bool specCubeIsRenderBuffer, paletteSurfaceIsRenderBuffer, paletteCapsIsRenderBuffer, baseMapIsRenderBuffer;
            public uint shadowMapFormat;
        }

        // The pointer a texture is bound by: a render texture's colour render buffer handle, else its native texture.
        private IntPtr TexturePointer(Texture texture, out bool renderBuffer)
        {
            if (texture is RenderTexture renderTexture)
            {
                renderBuffer = true;
                return _pointers.ColourRenderBuffer(renderTexture);
            }

            renderBuffer = false;
            return _pointers.Texture(texture);
        }

        private static void BindTexture(VpNativeDrawData drawData, int binding, IntPtr pointer, bool renderBuffer, Texture texture)
        {
            uint format = Plugin.DxgiFormat(texture.graphicsFormat);
            Plugin.TextureKind kind = texture.dimension == TextureDimension.Cube ? Plugin.TextureKind.Cube : Plugin.TextureKind.Tex2D;
            if (renderBuffer)
            {
                drawData.SetRenderBuffer(binding, pointer, format, kind);
            }
            else
            {
                drawData.SetTexture(binding, pointer, format, kind);
            }
        }

        private bool IssueOne(
            CommandBuffer commands, PipelineEntry entry, Material material, in Bound bound, int startCommand, int commandCount,
            Rect viewport, in GlobalsInputs inputs, GraphicsBuffer gpuConstants, ref int gpuConstantsUsed, int frame)
        {
            if (entry.gpuBlocks.Count > _gpuOffsets.Length)
            {
                PipelineFailures++;
                LastFailure = "more GPU-assembled constant blocks than the route expects (" + entry.gpuBlocks.Count + ")";
                return false;
            }

            // 1. The native pointers: kept ones, or asked of Unity for a resource not seen before.
            BoundPointers pointers;
            using (PointersMarker.Auto())
            {
                pointers = default;
                pointers.arguments = _pointers.Buffer(bound.arguments, bound.argumentsGeneration);
                pointers.visible = _pointers.Buffer(bound.visible, bound.visibleGeneration);
                pointers.instances = _pointers.Buffer(bound.instances, bound.instancesGeneration);
                pointers.clips = _pointers.Buffer(bound.clips, bound.clipsGeneration);
                pointers.indexBuffer = _pointers.Buffer(bound.indexBuffer, bound.indexGeneration);
                pointers.vertexBuffer = _pointers.Buffer(bound.vertexBuffer, bound.vertexGeneration);
                pointers.gpuConstants = _pointers.Buffer(gpuConstants);   // the route's own: written by the GPU alone
                if (entry.vertexCullCommands >= 0)
                {
                    pointers.cullCommands = _pointers.Buffer(bound.cullCommands, bound.cullCommandsGeneration);
                }

                if (entry.pixelSpecCube >= 0)
                {
                    Texture specCube = ReflectionProbe.defaultTexture;
                    pointers.specCubeTexture = specCube != null ? specCube : Texture2D.blackTexture;
                    pointers.specCube = TexturePointer(pointers.specCubeTexture, out pointers.specCubeIsRenderBuffer);
                }

                if (entry.pixelShadowMap >= 0)
                {
                    if (bound.shadowMap == null)
                    {
                        PipelineFailures++;
                        LastFailure = "the variant reads the shadow map but the frame has none";
                        return false;
                    }

                    // The map's depth format must be one the plugin samples through a known view format; any other
                    // is refused here rather than bound as a wrong format.
                    pointers.shadowMapFormat = Plugin.DxgiDepthSampledFormat(bound.shadowMap.depthStencilFormat);
                    if (pointers.shadowMapFormat == 0)
                    {
                        ShadowMapFormatRefusals++;
                        LastFailure = "the shadow map's depth format " + bound.shadowMap.depthStencilFormat + " has no sampled format in the plugin's table";
                        return false;
                    }

                    pointers.shadowMap = _pointers.DepthRenderBuffer(bound.shadowMap);
                    if (pointers.shadowMap == IntPtr.Zero)
                    {
                        PipelineFailures++;
                        LastFailure = "the frame's shadow map is not created";
                        return false;
                    }
                }

                if (entry.pixelPaletteSurface >= 0)
                {
                    pointers.paletteSurfaceTexture = TextureOrWhite(Shader.GetGlobalTexture(PaletteSurfaceId));
                    pointers.paletteSurface = TexturePointer(pointers.paletteSurfaceTexture, out pointers.paletteSurfaceIsRenderBuffer);
                }

                if (entry.pixelPaletteCaps >= 0)
                {
                    pointers.paletteCapsTexture = TextureOrWhite(Shader.GetGlobalTexture(PaletteCapsId));
                    pointers.paletteCaps = TexturePointer(pointers.paletteCapsTexture, out pointers.paletteCapsIsRenderBuffer);
                }

                if (entry.pixelBaseMap >= 0)
                {
                    pointers.baseMapTexture = TextureOrWhite(material.HasProperty(BaseMapId) ? material.GetTexture(BaseMapId) : null);
                    pointers.baseMap = TexturePointer(pointers.baseMapTexture, out pointers.baseMapIsRenderBuffer);
                }

                if (pointers.specCube == IntPtr.Zero && entry.pixelSpecCube >= 0 || pointers.paletteSurface == IntPtr.Zero && entry.pixelPaletteSurface >= 0
                    || pointers.paletteCaps == IntPtr.Zero && entry.pixelPaletteCaps >= 0 || pointers.baseMap == IntPtr.Zero && entry.pixelBaseMap >= 0)
                {
                    PipelineFailures++;
                    LastFailure = "a texture the variant reads is a render texture that is not created";
                    return false;
                }
            }

            // 2. The stage constants assembled on the GPU: the commands recorded for it.
            int[] gpuOffsets = _gpuOffsets;
            using (GpuConstantsMarker.Auto())
            {
                ComputeShader copy = _copy;
                int kernel = _copyKernel;
                for (int b = 0; b < entry.gpuBlocks.Count; b++)
                {
                    GpuBlock block = entry.gpuBlocks[b];
                    int padded = (block.size + ConstantAlignment - 1) / ConstantAlignment * ConstantAlignment;
                    if (gpuConstantsUsed + padded > GpuConstantBytesPerPass)
                    {
                        RingFull++;
                        LastFailure = "the pass's GPU constant buffer is full";
                        return false;
                    }

                    gpuOffsets[b] = gpuConstantsUsed;
                    gpuConstantsUsed += padded;
                    commands.SetComputeBufferParam(copy, kernel, OffsetsId, block.offsets);
                    commands.SetComputeBufferParam(copy, kernel, ConstantsId, gpuConstants);
                    commands.SetComputeIntParam(copy, DestinationId, gpuOffsets[b]);
                    commands.SetComputeMatrixParam(copy, MatrixVpId, inputs.matrixVP);
                    commands.SetComputeMatrixParam(copy, MatrixVId, inputs.matrixV);
                    commands.SetComputeMatrixParam(copy, ProjectionId, inputs.projection);
                    commands.SetComputeIntParam(copy, InstanceMultiplierId, inputs.instanceMultiplier);
                    commands.SetComputeVectorParam(copy, ShadowBiasId, inputs.shadowBias);
                    commands.SetComputeVectorParam(copy, LightDirectionId, inputs.lightDirection);
                    commands.SetComputeFloatParam(copy, ShadowSliceSelectionId, inputs.sliceSelection);
                    if (inputs.stereo)
                    {
                        commands.SetComputeMatrixArrayParam(copy, StereoMatrixVpId, _stereoVP);
                        commands.SetComputeMatrixArrayParam(copy, StereoMatrixVId, _stereoV);
                        commands.SetComputeMatrixArrayParam(copy, StereoMatrixPId, _stereoP);
                        commands.SetComputeMatrixArrayParam(copy, StereoCameraProjectionId, _stereoCameraProjection);
                        commands.SetComputeVectorArrayParam(copy, StereoCameraPosId, _stereoPos);
                    }

                    commands.DispatchCompute(copy, kernel, 1, 1, 1);
                }
            }

            // 3. The per-material and per-draw constants, written on the CPU.
            using (CpuConstantsMarker.Auto())
            {
                for (int b = 0; b < entry.cpuBlocks.Count; b++)
                {
                    FillCpuBlock(entry.cpuBlocks[b], material, frame);
                }
            }

            // 4. The event data block, and the plugin event's reservation in the command buffer.
            using (EventDataMarker.Auto())
            {
                VpNativeDrawData drawData = _drawData;
                if (!drawData.TryBegin(entry.pipeline.Id, pointers.arguments, startCommand, commandCount, pointers.indexBuffer, (long)bound.indexBuffer.count * bound.indexBuffer.stride, bound.indexBuffer.stride))
                {
                    RingFull++;
                    LastFailure = "every event data block is still with the plugin";
                    return false;
                }

                drawData.AddViewport(viewport);
                drawData.SetBuffer(entry.vertexArguments, pointers.arguments, bound.arguments.count, bound.arguments.stride, true);
                drawData.SetBuffer(entry.vertexVertices, pointers.vertexBuffer, bound.vertexBuffer.count, bound.vertexBuffer.stride, false);
                drawData.SetBuffer(entry.vertexInstances, pointers.instances, bound.instances.count, bound.instances.stride, false);
                drawData.SetBuffer(entry.vertexClips, pointers.clips, bound.clips.count, bound.clips.stride, false);
                drawData.SetBuffer(entry.vertexVisible, pointers.visible, bound.visible.count, bound.visible.stride, false);
                if (entry.vertexCullCommands >= 0)
                {
                    drawData.SetBuffer(entry.vertexCullCommands, pointers.cullCommands, bound.cullCommands.count, bound.cullCommands.stride, false);
                }

                if (entry.pixelSpecCube >= 0)
                {
                    BindTexture(drawData, entry.pixelSpecCube, pointers.specCube, pointers.specCubeIsRenderBuffer, pointers.specCubeTexture);
                }

                if (entry.pixelShadowMap >= 0)
                {
                    drawData.SetDepthRenderBuffer(entry.pixelShadowMap, pointers.shadowMap, pointers.shadowMapFormat);
                }

                if (entry.pixelPaletteSurface >= 0)
                {
                    BindTexture(drawData, entry.pixelPaletteSurface, pointers.paletteSurface, pointers.paletteSurfaceIsRenderBuffer, pointers.paletteSurfaceTexture);
                }

                if (entry.pixelPaletteCaps >= 0)
                {
                    BindTexture(drawData, entry.pixelPaletteCaps, pointers.paletteCaps, pointers.paletteCapsIsRenderBuffer, pointers.paletteCapsTexture);
                }

                if (entry.pixelBaseMap >= 0)
                {
                    BindTexture(drawData, entry.pixelBaseMap, pointers.baseMap, pointers.baseMapIsRenderBuffer, pointers.baseMapTexture);
                }

                for (int b = 0; b < entry.gpuBlocks.Count; b++)
                {
                    drawData.SetGpuConstants(entry.gpuBlocks[b].binding, pointers.gpuConstants, gpuOffsets[b]);
                }

                for (int b = 0; b < entry.cpuBlocks.Count; b++)
                {
                    drawData.SetConstants(entry.cpuBlocks[b].binding, entry.cpuBlocks[b].block);
                }

                IntPtr eventData = drawData.Finish(out uint serial);
                // Not recorded here: the pass records every event after its last constant dispatch (RecordPrepared).
                // A compute dispatch recorded right after a plugin event wrote nothing (play-27, 2026-10-08: of four
                // caster splits only the first's GPU block was assembled; the other three read back as zero), so no
                // Unity compute work of the route follows a plugin event within a pass. The block is Prepared until then.
                _blocks.Add(new Block { data = eventData, frame = frame, serial = serial });
            }

            LastFrameIssues++;
            return true;
        }

        /// <summary>
        /// Records the plugin events of every issue prepared in this Execute, in order, after all their constant
        /// dispatches (the ring's Prepared blocks become Recorded). Called once at the end of a pass's Execute.
        /// </summary>
        private void IssuePendingEvents(CommandBuffer commands)
        {
            _drawData?.RecordPrepared(commands, Plugin.RenderEventFunction, Plugin.DrawEventId);
        }

        // At the start of a pass's Execute: a Prepared block left by an Execute that ended early was never recorded,
        // and is freed by the ring (counted there) rather than recorded into another pass's command buffer.
        private void DropUnrecorded()
        {
            _drawData?.DropPrepared();
        }

        private static Texture TextureOrWhite(Texture texture)
        {
            return texture != null ? texture : Texture2D.whiteTexture;
        }

        /// <summary>
        /// The pass's GPU constant buffer, made on its first use. The name is put together only then: a concatenation
        /// with the camera's name every frame was the allocation under BodyPrepare / VP Native Casters in natP3..natP5
        /// (two calls a frame each: the camera's name read from the engine and the concatenation).
        /// </summary>
        private static GraphicsBuffer EnsureGpuConstants(ref GraphicsBuffer buffer, string purpose, Camera camera)
        {
            if (buffer == null)
            {
                buffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, GpuConstantBytesPerPass / 4, 4) { name = purpose + (camera != null ? camera.name : "") };
            }

            return buffer;
        }

        // ---- the body pass -----------------------------------------------------------------------------------------

        private sealed class BodyPassData
        {
            public BodyPass pass;
            public TextureHandle colour;
            public TextureHandle depth;
            public TextureHandle shadowMap;
            public UniversalCameraData cameraData;
            public GraphicsFormat colourFormat;
            public GraphicsFormat depthFormat;
            public int sampleCount;
            public int width;
            public int height;
            public string variant;
            public bool stereo;
            public Rect viewport;
        }

        private sealed class BodyPass : ScriptableRenderPass
        {
            private readonly VpNativeDrawRoute _route;
            private readonly CameraPasses _passes;
            private GraphicsBuffer _gpuConstants;
            private int _gpuConstantsUsed;

            public BodyPass(VpNativeDrawRoute route, CameraPasses passes)
            {
                _route = route;
                _passes = passes;
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
                profilingSampler = new ProfilingSampler("VP Native Draw");
            }

            /// <summary>Hands the GPU constant buffer over (to be released once no event reads it) and forgets it.</summary>
            public GraphicsBuffer TakeGpuConstants()
            {
                GraphicsBuffer buffer = _gpuConstants;
                _gpuConstants = null;
                return buffer;
            }

            public GraphicsBuffer GpuConstantsForDiagnosis => _gpuConstants;

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(cameraData.camera, _passes.camera))
                {
                    return;
                }

                _route.Recorded++;
                _route.CollectResults(_passes.frame);
                _route.LastFrameIssues = 0;
                if (_passes.bodies.Count == 0)
                {
                    return;
                }

                // An XR pass: both eyes at once (Single Pass Instanced) is drawn by the stereo variant into every slice
                // of the eye texture; a pass of one view (multi-pass) is drawn as a camera of its own. Any other XR
                // arrangement is not drawn, and counted.
                bool xr = cameraData.xr != null && cameraData.xr.enabled;
                bool stereo = xr && cameraData.xr.singlePassEnabled;
                if (xr && !stereo && cameraData.xr.viewCount != 1)
                {
                    _route.XrFramesNotDrawn++;
                    _route.LastFailure = "an XR pass of " + cameraData.xr.viewCount + " views that is not single pass is not drawn by the native route";
                    _passes.bodies.Clear();
                    return;
                }

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                TextureHandle colour = resourceData.activeColorTexture;
                TextureHandle depth = resourceData.activeDepthTexture;
                if (!colour.IsValid() || !depth.IsValid())
                {
                    _route.LastFailure = "no active colour or depth target";
                    _route.BodiesDropped += _passes.bodies.Count;
                    _passes.bodies.Clear();
                    return;
                }

                string variant = _route.BodyVariantOverride ?? VariantName(BodyVariantFor(frameData.Get<UniversalLightData>(), frameData.Get<UniversalShadowData>()), stereo);
                _route.LastBodyVariant = variant;
                TextureHandle shadowMap = resourceData.mainShadowsTexture;
                TextureDesc colourDesc = renderGraph.GetTextureDesc(colour);
                TextureDesc depthDesc = renderGraph.GetTextureDesc(depth);
                RenderTextureDescriptor target = cameraData.cameraTargetDescriptor;
                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(passName, out BodyPassData data, profilingSampler))
                {
                    data.pass = this;
                    data.colour = colour;
                    data.depth = depth;
                    data.shadowMap = shadowMap;
                    data.cameraData = cameraData;
                    data.variant = variant;
                    data.colourFormat = colourDesc.format != GraphicsFormat.None ? colourDesc.format : target.graphicsFormat;
                    data.depthFormat = depthDesc.format != GraphicsFormat.None ? depthDesc.format : target.depthStencilFormat;
                    data.sampleCount = colourDesc.msaaSamples != MSAASamples.None ? (int)colourDesc.msaaSamples : Mathf.Max(1, target.msaaSamples);
                    data.width = colourDesc.width > 0 ? colourDesc.width : target.width;
                    data.height = colourDesc.height > 0 ? colourDesc.height : target.height;
                    data.stereo = stereo;
                    data.viewport = xr ? cameraData.xr.GetViewport(0) : new Rect(0f, 0f, data.width, data.height);
                    builder.UseTexture(colour, AccessFlags.ReadWrite);
                    builder.UseTexture(depth, AccessFlags.ReadWrite);
                    if (shadowMap.IsValid())
                    {
                        builder.UseTexture(shadowMap, AccessFlags.Read);
                    }

                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (BodyPassData passData, UnsafeGraphContext context) => passData.pass.Execute(passData, context));
                }
            }

            private void Execute(BodyPassData data, UnsafeGraphContext context)
            {
                if (_route._disposed)
                {
                    _route.IssuesAfterDispose += _passes.bodies.Count;
                    _passes.bodies.Clear();
                    return;
                }

                _route.DropUnrecorded();
                CommandBuffer commands = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                RTHandle colourHandle = data.colour;
                RTHandle depthHandle = data.depth;
                if (data.stereo)
                {
                    // Every slice of the eye texture: the vertex stage picks the eye's slice (SV_RenderTargetArrayIndex).
                    commands.SetRenderTarget(colourHandle, depthHandle, 0, CubemapFace.Unknown, -1);
                }
                else
                {
                    commands.SetRenderTarget(colourHandle, depthHandle);
                }

                GlobalsInputs inputs;
                VpNativeRenderState state;
                RenderTexture shadowMap = null;
                GraphicsBuffer gpuConstants;
                using (BodyPrepareMarker.Auto())
                {
                    bool flipped = data.cameraData.IsRenderTargetProjectionMatrixFlipped(colourHandle, depthHandle);
                    inputs = new GlobalsInputs
                    {
                        matrixV = data.cameraData.GetViewMatrix(0),
                        projection = GL.GetGPUProjectionMatrix(data.cameraData.GetProjectionMatrix(0), flipped),
                        instanceMultiplier = data.stereo ? 2 : 1,
                        stereo = data.stereo,
                    };
                    inputs.matrixVP = inputs.projection * inputs.matrixV;
                    if (data.stereo)
                    {
                        for (int eye = 0; eye < 2; eye++)
                        {
                            Matrix4x4 view = data.cameraData.GetViewMatrix(eye);
                            Matrix4x4 cameraProjection = data.cameraData.GetProjectionMatrix(eye);
                            Matrix4x4 gpuProjection = GL.GetGPUProjectionMatrix(cameraProjection, flipped);
                            _route._stereoV[eye] = view;
                            _route._stereoP[eye] = gpuProjection;
                            _route._stereoCameraProjection[eye] = cameraProjection;
                            _route._stereoVP[eye] = gpuProjection * view;
                            Vector3 position = view.inverse.GetColumn(3);
                            _route._stereoPos[eye] = new Vector4(position.x, position.y, position.z, 1f);
                        }
                    }

                    state = new VpNativeRenderState
                    {
                        colourFormat = data.colourFormat,
                        depthFormat = data.depthFormat,
                        sampleCount = data.sampleCount,
                        cullMode = Plugin.CullMode.Back,
                        frontCounterClockwise = _route.FrontCounterClockwise ?? flipped,
                        depthFunc = SystemInfo.usesReversedZBuffer ? Plugin.Compare.GreaterEqual : Plugin.Compare.LessEqual,
                        depthWrite = true,
                        colourWrite = true,
                    };
                    if (data.shadowMap.IsValid())
                    {
                        RTHandle shadowHandle = data.shadowMap;
                        shadowMap = shadowHandle.rt;
                    }

                    gpuConstants = EnsureGpuConstants(ref _gpuConstants, "VP Native Body Constants ", _passes.camera);
                    _gpuConstantsUsed = 0;
                }

                Rect viewport = data.viewport;
                {
                    for (int i = 0; i < _passes.bodies.Count; i++)
                    {
                        Issue issue = _passes.bodies[i];
                        PipelineEntry entry = _route.PipelineFor(issue.material, data.variant, state, out string failure);
                        if (entry == null)
                        {
                            _route.PipelineFailures++;
                            _route.LastFailure = failure;
                            continue;
                        }

                        // The body's entries hold two physical instances a kept instance exactly when the batch was
                        // uploaded for Single Pass Instanced; the stereo variant reads them so, and no other way.
                        if (issue.batch.SinglePassInstanced != data.stereo)
                        {
                            _route.PipelineFailures++;
                            _route.LastFailure = data.stereo
                                ? "a Single Pass Instanced frame, but the batch's arguments are not doubled for it"
                                : "the batch's arguments are doubled for Single Pass Instanced, but the frame is not one";
                            continue;
                        }

                        issue.batch.NativeDrawBuffers(
                            issue.view, out GraphicsBuffer forwardArguments, out GraphicsBuffer forwardVisible, out _, out _,
                            out GraphicsBuffer instances, out GraphicsBuffer clips, out GraphicsBuffer cullCommands);
                        var bound = new Bound
                        {
                            arguments = forwardArguments, visible = forwardVisible, instances = instances, clips = clips, cullCommands = cullCommands,
                            indexBuffer = issue.buffers.IndexBuffer, vertexBuffer = issue.buffers.VertexBuffer, shadowMap = shadowMap,
                        };
                        bound.TakeGenerations(issue.batch, issue.buffers);
                        if (_route.IssueOne(commands, entry, issue.material, bound, issue.startCommand, issue.commandCount, viewport, inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame))
                        {
                            _route.BodiesIssued++;
                            // No string is made here: the name is the binding's, read by the diagnosis that asks.
                            _route.LastBodyGpuBlocks.Clear();
                            for (int b = 0; b < entry.gpuBlocks.Count; b++)
                            {
                                _route.LastBodyGpuBlocks.Add((entry.pipeline.Bindings[entry.gpuBlocks[b].binding], _route._gpuOffsets[b], entry.gpuBlocks[b].size));
                            }
                        }
                    }
                }

                _passes.bodies.Clear();
                _route.IssuePendingEvents(commands);
            }
        }

        // ---- the caster pass ---------------------------------------------------------------------------------------

        private sealed class CasterPassData
        {
            public CasterPass pass;
            public TextureHandle shadowMap;
            public UniversalCameraData cameraData;
            public int splitCount;
            public Vector4 lightDirection;
            public int width;
            public int height;
        }

        private sealed class CasterPass : ScriptableRenderPass
        {
            private readonly VpNativeDrawRoute _route;
            private readonly CameraPasses _passes;
            private readonly Matrix4x4[] _views = new Matrix4x4[MaxSplits];
            private readonly Matrix4x4[] _projections = new Matrix4x4[MaxSplits];
            private readonly Vector4[] _biases = new Vector4[MaxSplits];
            private readonly Rect[] _viewports = new Rect[MaxSplits];
            private GraphicsBuffer _gpuConstants;
            private int _gpuConstantsUsed;

            public GraphicsBuffer GpuConstantsForDiagnosis => _gpuConstants;

            public CasterPass(VpNativeDrawRoute route, CameraPasses passes)
            {
                _route = route;
                _passes = passes;
                renderPassEvent = RenderPassEvent.AfterRenderingShadows;
                profilingSampler = new ProfilingSampler("VP Native Casters");
            }

            /// <summary>Hands the GPU constant buffer over (to be released once no event reads it) and forgets it.</summary>
            public GraphicsBuffer TakeGpuConstants()
            {
                GraphicsBuffer buffer = _gpuConstants;
                _gpuConstants = null;
                return buffer;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(cameraData.camera, _passes.camera) || _passes.casterRuns.Count == 0)
                {
                    return;
                }

                string skip = Prepare(frameData, renderGraph, out TextureHandle shadowMap, out int splits, out Vector4 lightDirection, out int width, out int height);
                if (skip != null)
                {
                    _route.CasterRunsNotDrawn += _passes.casterRuns.Count;
                    _route.LastCasterSkipReason = skip;
                    _passes.casterRuns.Clear();
                    return;
                }

                _route.LastSplitCount = splits;
                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(passName, out CasterPassData data, profilingSampler))
                {
                    data.pass = this;
                    data.shadowMap = shadowMap;
                    data.cameraData = cameraData;
                    data.splitCount = splits;
                    data.lightDirection = lightDirection;
                    data.width = width;
                    data.height = height;
                    builder.UseTexture(shadowMap, AccessFlags.ReadWrite);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (CasterPassData passData, UnsafeGraphContext context) => passData.pass.Execute(passData, context));
                }
            }

            // The slices as URP computes them for this frame (the public ShadowUtils from the inputs URP uses), or why
            // the casters are not drawn: no main light shadow map this frame, which is also when URP draws none.
            private string Prepare(ContextContainer frameData, RenderGraph renderGraph, out TextureHandle shadowMap, out int splits, out Vector4 lightDirection, out int width, out int height)
            {
                shadowMap = TextureHandle.nullHandle;
                splits = 0;
                lightDirection = Vector4.zero;
                width = height = 0;
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
                UniversalLightData lightData = frameData.Get<UniversalLightData>();
                UniversalShadowData shadowData = frameData.Get<UniversalShadowData>();
                int lightIndex = lightData.mainLightIndex;
                if (lightIndex < 0)
                {
                    return "no main light";
                }

                VisibleLight visibleLight = lightData.visibleLights[lightIndex];
                if (!shadowData.supportsMainLightShadows || visibleLight.light == null || visibleLight.light.shadows == LightShadows.None || visibleLight.lightType != LightType.Directional)
                {
                    return "the main light is not a directional light casting shadows";
                }

                CullingResults cullResults = renderingData.cullResults;
                if (!cullResults.GetShadowCasterBounds(lightIndex, out Bounds _))
                {
                    return "no shadow caster bounds: URP binds an empty shadow map";
                }

                shadowMap = resourceData.mainShadowsTexture;
                if (!shadowMap.IsValid())
                {
                    return "no main light shadow texture";
                }

                int cascades = shadowData.mainLightShadowCascadesCount;
                if (cascades <= 0 || cascades > MaxSplits)
                {
                    return "the main light's shadow map has " + cascades + " splits";
                }

                width = shadowData.mainLightShadowmapWidth;
                height = shadowData.mainLightShadowmapHeight;
                int renderTargetHeight = cascades == 2 ? height >> 1 : height;
                TextureDesc description = renderGraph.GetTextureDesc(shadowMap);
                if (description.width != width || description.height != renderTargetHeight)
                {
                    return "URP bound an empty main light shadow map";
                }

                height = renderTargetHeight;
                using (CasterSlicesMarker.Auto())
                {
                    int resolution = ShadowUtils.GetMaxTileResolutionInAtlas(width, shadowData.mainLightShadowmapHeight, cascades);
                    for (int cascade = 0; cascade < cascades; cascade++)
                    {
                        if (!ShadowUtils.ExtractDirectionalLightMatrix(ref cullResults, shadowData, lightIndex, cascade, width, renderTargetHeight, resolution,
                                visibleLight.light.shadowNearPlane, out Vector4 _, out ShadowSliceData slice))
                        {
                            return "invalid cascade slice " + cascade;
                        }

                        _views[cascade] = slice.viewMatrix;
                        _projections[cascade] = slice.projectionMatrix;
                        _viewports[cascade] = new Rect(slice.offsetX, slice.offsetY, slice.resolution, slice.resolution);
                        _biases[cascade] = ShadowUtils.GetShadowBias(ref visibleLight, lightIndex, shadowData, slice.projectionMatrix, slice.resolution);
                    }
                }

                Vector3 direction = -visibleLight.localToWorldMatrix.GetColumn(2);
                lightDirection = new Vector4(direction.x, direction.y, direction.z, 0f);
                splits = cascades;
                return null;
            }

            private void Execute(CasterPassData data, UnsafeGraphContext context)
            {
                if (_route._disposed)
                {
                    _route.IssuesAfterDispose += _passes.casterRuns.Count;
                    _passes.casterRuns.Clear();
                    return;
                }

                _route.DropUnrecorded();
                CommandBuffer commands = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                RTHandle shadowHandle = data.shadowMap;
                commands.SetRenderTarget(shadowHandle, shadowHandle);
                RenderTexture shadowMap = shadowHandle.rt;
                bool flipped = data.cameraData.IsRenderTargetProjectionMatrixFlipped(shadowHandle, shadowHandle);
                // The map must be the atlas the slices were laid out for: URP binds a tiny empty map when it finds no
                // caster to render, and the slices' viewports lie outside it. Refused, counted, never drawn into.
                if (shadowMap == null || shadowMap.width != data.width || shadowMap.height != data.height)
                {
                    _route.CasterRunsNotDrawn += _passes.casterRuns.Count;
                    _route.LastCasterSkipReason = "the frame's shadow map is " + (shadowMap == null ? "no texture" : shadowMap.width + "x" + shadowMap.height) + ", not the " + data.width + "x" + data.height + " atlas the slices were laid out for";
                    _passes.casterRuns.Clear();
                    return;
                }

                GraphicsBuffer gpuConstants = EnsureGpuConstants(ref _gpuConstants, "VP Native Caster Constants ", _passes.camera);
                _gpuConstantsUsed = 0;
                float sliceSelection = VpGpuCullSetup.ShadowSliceSelection ? 1f : 0f;
                {
                    for (int i = 0; i < _passes.casterRuns.Count; i++)
                    {
                        Issue issue = _passes.casterRuns[i];
                        var state = new VpNativeRenderState
                        {
                            colourFormat = GraphicsFormat.None,
                            depthFormat = shadowMap != null ? shadowMap.depthStencilFormat : GraphicsFormat.D16_UNorm,
                            sampleCount = 1,
                            cullMode = CullOf(issue.material),
                            frontCounterClockwise = _route.FrontCounterClockwise ?? flipped,
                            depthFunc = SystemInfo.usesReversedZBuffer ? Plugin.Compare.GreaterEqual : Plugin.Compare.LessEqual,
                            depthWrite = true,
                            colourWrite = false,
                            depthBias = _route.CasterDepthBiasSigned,
                            slopeScaledDepthBias = _route.CasterSlopeScaledDepthBiasSigned,
                        };
                        PipelineEntry entry = _route.PipelineFor(issue.material, CasterVariant, state, out string failure);
                        if (entry == null)
                        {
                            _route.PipelineFailures++;
                            _route.LastFailure = failure;
                            continue;
                        }

                        issue.batch.NativeDrawBuffers(
                            issue.view, out _, out _, out GraphicsBuffer shadowArguments, out GraphicsBuffer shadowVisible,
                            out GraphicsBuffer instances, out GraphicsBuffer clips, out GraphicsBuffer cullCommands);
                        var bound = new Bound
                        {
                            arguments = shadowArguments, visible = shadowVisible, instances = instances, clips = clips, cullCommands = cullCommands,
                            indexBuffer = issue.buffers.IndexBuffer, vertexBuffer = issue.buffers.VertexBuffer,
                        };
                        bound.TakeGenerations(issue.batch, issue.buffers);
                        for (int split = 0; split < data.splitCount; split++)
                        {
                            // The shadow map is a render texture: the same GPU projection SetViewProjectionMatrices would derive.
                            var inputs = new GlobalsInputs
                            {
                                matrixV = _views[split],
                                projection = GL.GetGPUProjectionMatrix(_projections[split], true),
                                instanceMultiplier = 1,
                                shadowBias = _biases[split],
                                lightDirection = data.lightDirection,
                                sliceSelection = sliceSelection,
                            };
                            inputs.matrixVP = inputs.projection * inputs.matrixV;
                            if (_route.IssueOne(commands, entry, issue.material, bound, issue.startCommand, issue.commandCount, _viewports[split], inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame))
                            {
                                _route.CastersIssued++;
                                if (i == 0 && split == 0) _route.LastCasterGpuBlocks.Clear();
                                for (int b = 0; b < entry.gpuBlocks.Count; b++)
                                {
                                    _route.LastCasterGpuBlocks.Add((entry.pipeline.Bindings[entry.gpuBlocks[b].binding], _route._gpuOffsets[b], entry.gpuBlocks[b].size, split));
                                }
                            }
                        }
                    }
                }

                _passes.casterRuns.Clear();
                _route.IssuePendingEvents(commands);
            }

            private static Plugin.CullMode CullOf(Material material)
            {
                if (!material.HasProperty(CullId))
                {
                    return Plugin.CullMode.Back;
                }

                switch ((CullMode)Mathf.RoundToInt(material.GetFloat(CullId)))
                {
                    case CullMode.Off: return Plugin.CullMode.None;
                    case CullMode.Front: return Plugin.CullMode.Front;
                    default: return Plugin.CullMode.Back;
                }
            }
        }
    }
}
