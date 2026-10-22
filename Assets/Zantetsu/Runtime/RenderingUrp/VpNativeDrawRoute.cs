#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
﻿using System;
using System.Collections.Generic;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using Plugin = Zantetsu.Rendering.VpNativeDrawPlugin;

using Zantetsu.Rendering;
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
    /// **The command compaction on Direct3D 12 (DESIGN 4.5.11, D-218, D-219).** Where the launch asks for it and
    /// the device can -- the fixed draw is this route's default
    /// (<see cref="VpCommandCompaction.TrySelect"/>; the choice and its reason are logged once a route) -- an ordinary
    /// run -- the selection's own commands of one material, for the body or for the casters -- is compacted on the
    /// GPU after the selection and drawn by ExecuteIndirect with the count the GPU wrote: the compacted entries are
    /// the argument buffer, the run's length the most, and the vertex stage still reads the selection's own entries
    /// by the command's number. A frame's steps are, in the GPU's order: the selection; the count slots of the sets
    /// the frame uses zeroed by a copy (the route's first pass); in each pass its compactions, then its draws. Each
    /// follows the one before by the resource state it needs, so the route records no event of the plugin between
    /// them. A clip list's run is not compacted: it is issued as before.
    /// </para>
    /// <para>
    /// **What it does not do yet.** An XR pass (both eyes at once) is not drawn, and counted as such. The plugin not
    /// being available, a shader binary not being in the build, or a pipeline the plugin refuses are failures this
    /// route records; it never draws through Unity's calls instead.
    /// </para>
    /// </summary>
    public sealed class VpNativeDrawRoute : IVpNativeDrawSink, IVpNativeClipDrawSink, IVpNativeClipListDrawSink, IDisposable
    {
        // RenderGraph takes the caller's file through [CallerFilePath], which would put the building machine's
        // folders into the Player (unit 11): the project-relative path is given instead.
        private const string RenderGraphFile = "Assets/Zantetsu/Runtime/RenderingUrp/VpNativeDrawRoute.cs";


        // The clock reading that begins an observed span: nothing in a Release Player (DESIGN 21.18).
        internal static long Began()
        {
#if ZANTETSU_OBSERVE
            return System.Diagnostics.Stopwatch.GetTimestamp();
#else
            return 0;
#endif
        }
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
        private static readonly ProfilingSampler ConstantCopySampler = new ProfilingSampler("VP Native Constant Copy");
        private static readonly ProfilingSampler ConstantCopyWarmSampler = new ProfilingSampler("VP Native Constant Copy Warm");
        private static readonly ProfilingSampler CullWarmSampler = new ProfilingSampler("VP GPU Cull Warm");
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
            ("_PrevViewProjMatrixStereo", 38), ("_NonJitteredViewProjMatrixStereo", 39), ("_SpaceWarpNDCModifier", 40),
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

        // Direct3D 12 compaction: the zeroes every count buffer is zeroed from by a copy (made at the first frame that compacts).
        private GraphicsBuffer _zeroCounts;
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
            public string layoutKey;
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

            // A run of the batch's clip list (DESIGN 4.5.9): startCommand and commandCount are then entries of the
            // list, and no view's selection is read. Which of the entries are issued is read, when the pass runs, from
            // the camera's judgement of the list; null, or not valid, issues every entry.
            public bool clipList;
            public VpClipSelection selection;
        }

        private struct ClipIssue
        {
            public VpGpuIndexedGeometryBuffers buffers;
            public Material material;
            public VpGeometryRange range;
            public Matrix4x4 objectToWorld;
            public VpInstanceClip clip;

            // The camera's judgement of the clip list and this record's entry in it; null or below zero: always drawn.
            public VpClipSelection selection;
            public int entry;
        }
        public bool SeparateClipDraws => Plugin.Vulkan;

        /// <summary>
        /// Why this build's plugin route cannot draw clipped instances apart (DESIGN 4.5.9), or null when it can: the
        /// Direct3D 12 route draws them from clip lists, by variants that must be in the build. Asked by whoever makes
        /// the display, before it is made (<see cref="VpGpuCullSetup.ClipSeparationUnavailable"/>).
        /// </summary>
        public static string ClipDrawsUnavailable => Plugin.Vulkan || ClipListVariantsBaked
            ? null
            : "the plugin's variants for clip lists (" + ClipListCasterVariant + ", " + CasterVariant + NoClipSuffix + ") are not in the build";

        /// <summary>
        /// The Direct3D 12 route draws a batch's clip list (DESIGN 4.5.9) through the same ExecuteIndirect as every
        /// other issue, from the list's own entries, by the variants baked for it ("-list": the plugin's command
        /// number, no selection, the clip record evaluated); the Vulkan route draws each clipped instance directly
        /// (<see cref="SeparateClipDraws"/>). False when those variants are not in the build: a display that separates
        /// its clipped instances then refuses this route (it is to be made with the separation off:
        /// <see cref="ClipDrawsUnavailable"/>).
        /// </summary>
        public bool DrawsClipLists => !Plugin.Vulkan && ClipListVariantsBaked;

        /// <summary>The body variant of an ordinary instance's draw, of a clip list's draw, and the casters' of each.</summary>
        public const string NoClipSuffix = "-noclip";
        public const string ClipListCasterVariant = "caster-list";
        public static string ClipListVariantOf(string pluginVariant) => pluginVariant.Replace("-plugin", "-list");

        private static bool? s_clipListVariantsBaked;

        /// <summary>Whether the build holds the Direct3D variants the separation draws by (asked once).</summary>
        public static bool ClipListVariantsBaked
        {
            get
            {
                if (!s_clipListVariantsBaked.HasValue)
                {
                    s_clipListVariantsBaked =
                        Resources.Load<TextAsset>(VpNativeShaderBinary.ResourceFolder + "/" + ClipListCasterVariant) != null
                        && Resources.Load<TextAsset>(VpNativeShaderBinary.ResourceFolder + "/" + CasterVariant + NoClipSuffix) != null;
                }

                return s_clipListVariantsBaked.Value;
            }
        }

        // Whether a material's own draws are of the variant with no clip: the display switched that keyword on in the
        // copy it draws the selection with when it draws its clipped instances apart (asked once a material).
        private readonly Dictionary<Material, bool> _noClipMaterials = new Dictionary<Material, bool>();

        private bool OrdinaryDrawsHaveNoClip(Material material)
        {
            if (!_noClipMaterials.TryGetValue(material, out bool noClip))
            {
                noClip = material.IsKeywordEnabled(VpGpuCullSetup.NoClipKeyword);
                _noClipMaterials.Add(material, noClip);
            }

            return noClip;
        }

        /// <summary>
        /// The clip lists drawn (DESIGN 4.5.9), an entry being one clipped instance. Bodies: the runs and the entries
        /// taken, the entries the camera's judgement kept, and the issues made for them -- one for each run of kept
        /// entries, so kept entries that are not next to each other make more issues than runs. Casters: the runs and
        /// the entries taken, the entries kept in each split (an entry is counted once for every split it is drawn
        /// into), and the issues made. What is counted is what was issued, not what the GPU then drew.
        /// </summary>
        public long ClipBodyRunsTaken { get; private set; }
        public long ClipBodyEntriesTaken { get; private set; }
        public long ClipBodyEntriesKept { get; private set; }
        public long ClipBodyIssues { get; private set; }
        public long ClipCasterRunsTaken { get; private set; }
        public long ClipCasterEntriesTaken { get; private set; }
        public long ClipCasterIssues { get; private set; }
        private readonly long[] _clipCasterEntriesKept = new long[VpCullConditions.SplitCapacity];
        public long ClipCasterEntriesKept(int split) => _clipCasterEntriesKept[split];

        /// <summary>
        /// The direct clip draws of the Vulkan route: the records taken for the colour, the motion vectors and the
        /// casters (a caster record once, whatever the splits), and the records kept for each -- the casters' by split.
        /// </summary>
        public long DirectClipBodiesTaken { get; private set; }
        public long DirectClipBodiesKept { get; private set; }
        public long DirectClipMotionTaken { get; private set; }
        public long DirectClipMotionKept { get; private set; }
        public long DirectClipCastersTaken { get; private set; }
        private readonly long[] _directClipCastersKept = new long[VpCullConditions.SplitCapacity];
        public long DirectClipCastersKept(int split) => _directClipCastersKept[split];

        public void AddClipBodyRun(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int firstEntry, int entryCount, int layer, VpClipSelection selection)
        {
            if (!DrawsClipLists || entryCount == 0) return;
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null)
            {
                return;
            }

            ClipBodyRunsTaken++;
            passes.bodies.Add(new Issue { batch = batch, buffers = buffers, material = material, startCommand = firstEntry, commandCount = entryCount, view = -1, clipList = true, selection = selection });
        }

        public void AddClipCasterRun(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int firstEntry, int entryCount, int layer, VpClipSelection selection)
        {
            if (!DrawsClipLists || entryCount == 0) return;
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null)
            {
                return;
            }

            // The casters' bounds are declared by the ordinary AddCasters of the same camera: they are the batch's,
            // every adopted instance's, the clipped ones included.
            ClipCasterRunsTaken++;
            passes.casterRuns.Add(new Issue { batch = batch, buffers = buffers, material = material, startCommand = firstEntry, commandCount = entryCount, view = -1, clipList = true, selection = selection });
        }
        private VpNativeDirectClipProbe _directClipProbe;
        private readonly Plugin.DirectCommand[] _directCommands = new Plugin.DirectCommand[32];
        private readonly List<IntPtr> _groupPointers = new List<IntPtr>(4);
        private readonly Dictionary<(string layout, int context), int> _copiedGpuBlocks = new Dictionary<(string, int), int>();
        public long DirectClipDrawsIssued { get; private set; }
        public long GpuConstantDispatches { get; private set; }
        public long SharedGpuConstantBlocks { get; private set; }

        private sealed class PrewarmRequest
        {
            public readonly List<Material> bodies = new List<Material>(4);
            public readonly List<PipelineEntry> entries = new List<PipelineEntry>(16);
            public readonly List<PipelineEntry> colourEntries = new List<PipelineEntry>(8);
            public Material shadow, provisionalShadow;
            public VpIndexedIndirectDrawBatch batch;
            public VpGpuIndexedGeometryBuffers buffers;
            public bool unityCastersDeclared;
            public bool colourPrepared, colourSubmitted, motionSubmitted, shadowSubmitted, computeSubmitted;
            public int pending, completed;
            public string failure;
        }

        private struct Block
        {
            public IntPtr data;
            public int frame;
            public uint serial;
            public PrewarmRequest warmup;
            public bool zero;   // a zero event (ZeroPass): its refusal halts the compaction
            public bool barrier;   // the compute barrier event before a compaction: its refusal halts the compaction too
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

            // The command compaction (DESIGN 4.5.10): set, the arguments are the compacted entries of the run and the
            // GPU counted them into slot countSlot of counts; the run's length is the most the draw may take
            // (vkCmdDrawIndexedIndirectCount). The frame's zero pass zeroed the slots before any compaction. zeroCounts,
            // with snapshot, names the 4-byte slot the plugin copies the count into in this draw's preparation.
            public GraphicsBuffer counts, zeroCounts;

            // Direct3D 12 (DESIGN 4.5.11): the vertex stage reads a command's entry by the command's own number, so
            // with the arguments compacted it stays bound to the selection's entries, given here; null where the
            // stage reads the argument buffer itself (every issue that is not compacted, and the Vulkan route's).
            public GraphicsBuffer shaderArguments;
            public uint shaderArgumentsGeneration;
            public bool snapshot;          // zeroCounts names a 4-byte slot (countSnapshotSlot) the plugin copies this draw's count into: the set's draw counts, or the diagnosis's ring
            public bool drawFromCopy;      // the draw reads its count from that slot (VpCommandCompaction.DrawFromCopy)
            public int countSnapshotSlot;
            public int countSlot;

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
            public ZeroPass zero;
            public BodyPass body;
            public CasterPass casters;
            public BodyPass motion;
            public ColourDrawPass colourDraw;
            public readonly List<Block> colourBlocks = new List<Block>(8);
            public readonly List<Issue> motionBodies = new List<Issue>(8);
            public readonly List<Issue> bodies = new List<Issue>(8);
            public readonly List<Issue> casterRuns = new List<Issue>(8);
            public readonly List<ClipIssue> clipBodies = new List<ClipIssue>(4);
            public readonly List<ClipIssue> clipMotion = new List<ClipIssue>(4);
            public readonly List<ClipIssue> clipCasters = new List<ClipIssue>(4);
            public int frame;
            public int directProbeFrame = -1;
            public PrewarmRequest prewarm;
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
        /// to confirm it. Vulkan uses the URP value 1 directly in depthBiasConstantFactor; the D3D12 calibration does not apply.
        /// </summary>
        public int CasterDepthBias { get; set; } = Plugin.Vulkan ? 1 : 256;

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
        public long MotionTaken { get; private set; }
        public long MotionIssued { get; private set; }
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

        /// <summary>
        /// Bodies issued for a single-view camera from a batch uploaded for Single Pass Instanced (the second copy of
        /// each instance dropped by the shader): a picture camera beside the XR one. Not a failure; counted so that a
        /// run says whether it drew this way at all.
        /// </summary>
        public long SingleViewBodiesFromDoubled { get; private set; }
        public string LastFailure { get; private set; }
        public int LastFrameIssues { get; private set; }

        /// <summary>Experiment (2026-10-10): issues drawn by the GPU's count of compacted commands, and runs that drew fixed for want of a count slot.</summary>
        public long IndirectCountIssues { get; private set; }
        public long IndirectCountSlotsShort { get; private set; }
        public long IndirectCountCompactions { get; private set; }

        /// <summary>
        /// Whether this frame's issues compact: what the launch asks of this device's route
        /// (<see cref="VpNativeDrawPlugin.IndirectCountAsked"/>), taken where <see cref="VpCommandCompaction.TrySelect"/>
        /// finds every condition.
        /// </summary>
        private bool CompactsCommands
        {
            get
            {
                // Chosen once for what is asked (and again only if what is asked changes before the first frame, as a
                // launch's configuration may): VpCommandCompaction.TrySelect, with its reason in the log.
                bool asked = Plugin.IndirectCountAsked;
                if (!_compactionChoiceMade || asked != _compactionChoiceAsked)
                {
                    _compactionChoiceMade = true;
                    _compactionChoiceAsked = asked;
                    _compactionChosen = VpCommandCompaction.TrySelect(out string reason);
                    CommandCompactionChoice = reason;
                    if (Plugin.Vulkan)
                    {
                        VpCommandCompaction.LastChoice = reason;
                        // The device and its driver version beside the choice: the count's copy answers this driver's behaviour (DESIGN 4.5.10).
                        Debug.Log("VP COMMAND COMPACTION: " + reason + " [device " + SystemInfo.graphicsDeviceName + "; " + SystemInfo.graphicsDeviceVersion + "]");
                    }
                    else if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
                    {
                        // The Direct3D 12 route says which way it draws whichever it is (DESIGN 4.5.11), and whether
                        // the launch asked for the GPU check, whose tallies are read back: such a run is no measurement.
                        VpCommandCompaction.LastChoice = reason;
                        if (_compactionChosen && Array.IndexOf(Environment.GetCommandLineArgs(), VpCommandCompaction.VerifyArgument) >= 0) VpCommandCompaction.VerificationEnabled = true;
                        Debug.Log("VP COMMAND COMPACTION: " + reason + (VpCommandCompaction.VerificationEnabled && _compactionChosen ? "; THE GPU CHECK IS ON (its tallies are read back: not a performance sample)" : "")
                            + " [device " + SystemInfo.graphicsDeviceName + "; " + SystemInfo.graphicsDeviceVersion + "]");
                    }
                }

                return _compactionChosen && CompactionHalted == null;
            }
        }

        private bool _compactionChoiceMade, _compactionChoiceAsked, _compactionChosen;

        /// <summary>What this route chose between the compaction and the fixed draw, and why (DESIGN 4.5.10); null until its first frame.</summary>
        public string CommandCompactionChoice { get; private set; }

        /// <summary>Experiment: zero events recorded (one a column of a set a frame), runs drawn fixed because their column's zeroing was not reserved, and zero events the plugin refused.</summary>
        public long IndirectCountZeroEvents { get; private set; }
        public long IndirectCountZeroMissed { get; private set; }
        public long IndirectCountZeroRefused { get; private set; }
        public long IndirectCountBarriers { get; private set; }

        /// <summary>Direct3D 12: count buffers zeroed by a copy (one a column a set a frame), and the commands of the runs compacted, summed.</summary>
        public long IndirectCountCopies { get; private set; }
        public long IndirectCountRunCommands { get; private set; }

        /// <summary>
        /// The GPU check's tallies, read back now, when the launch asked for it (<see cref="VpCommandCompaction.VerifyArgument"/>)
        /// and this route compacted; null otherwise. A synchronous readback: for the end of a check run.
        /// </summary>
        public string ReadCompactionCheck()
        {
            return VpCommandCompaction.VerificationEnabled && IndirectCountCompactions > 0 ? VpCommandCompaction.ReadVerification() : null;
        }

        /// <summary>
        /// Why the compaction is off for the rest of this route's life, or null: a zero event the plugin refused means a
        /// frame's count slots may not have been zeroed before that frame's compaction, so from then on every run draws
        /// fixed (the frame itself is past; its draws were refused by the same cause when the device was the cause).
        /// </summary>
        public string CompactionHalted { get; private set; }

        // One compaction of a pass: each draw unit (run) of the pass takes the next count slot of this frame's set. The
        // set's slots were zeroed by the plugin after the last draw that read them (the previous use of the set, four
        // frames ago), in the preparation of the frame after that draw. Returns false when the slots are spent (the run
        // then draws fixed, counted).
        private int _countSlotsUsed;

        private void BeginCompactions()
        {
            // 0 but for the diagnosis that moves a pass's first run to a later slot (VpCommandCompaction.FirstSlot).
            _countSlotsUsed = VpCommandCompaction.FirstSlot;
            _barrierPending = true;
        }

        // The plugin's compute barrier (BarrierEvent) between the dispatches that wrote the entries a compaction reads
        // and the compaction itself. Unity put no barrier between two compute dispatches on one buffer on Quest 3S
        // (2026-10-10: APK 2's reset against the atomics; the city walk's motion selection, dispatched in the pass right
        // before its compaction, against icW4 without it), so the order is the plugin's: once per pass before its
        // first compaction, and again after any selection the pass dispatched. Without the barrier the run draws fixed.
        private bool _barrierPending;

        /// <summary>A selection was dispatched in this pass: the next compaction records the compute barrier first.</summary>
        private void NoteSelectionIssued() => _barrierPending = true;

        private bool IssueComputeBarrier(CommandBuffer commands, int frame)
        {
            // The barrier's block is recorded alone: a block of this pass still prepared and unrecorded would be recorded
            // under the barrier's event; none is expected here (each issue records its own at once).
            if (_drawData.InFlightIn(VpNativeDrawData.BlockState.Prepared) != 0)
            {
                LastFailure = "a prepared block of another issue stands before the compute barrier";
                return false;
            }

            if (!_drawData.TryBeginBarrier())
            {
                RingFull++;
                LastFailure = "every event data block is still with the plugin (compute barrier event)";
                return false;
            }

            IntPtr block = _drawData.Finish(out uint serial);
            _blocks.Add(new Block { data = block, frame = frame, serial = serial, barrier = true });
            _drawData.RecordPrepared(b => commands.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.BarrierEventId, b));
            IndirectCountBarriers++;
            VpCommandCompaction.Barriers++;
            _barrierPending = false;
            return true;
        }

        // column: 0 the body, 1 the casters, 2 the motion -- each its own entries and count slots of the set, so that
        // two passes compacting the same source (the motion pass with no selection of its own compacts the body's
        // entries) never add to one slot (icB7, 2026-10-10: the body's count was 1.5x and half its entries doubled).
        private bool CompactRun(CommandBuffer commands, VpIndexedIndirectDrawBatch batch, VpIndexedIndirectDrawBatch.Compaction compaction,
            GraphicsBuffer source, int column, int startCommand, int commandCount, int frame, ref Bound bound)
        {
            // Only a column whose count slots this frame's zero pass reserved for zeroing is compacted: otherwise the run
            // draws fixed (counted), and a set with an old count left in it is never added to.
            if (!compaction.ZeroReserved(column, frame))
            {
                IndirectCountZeroMissed++;
                VpCommandCompaction.ZeroMissed++;
                NoteRunDrawnFixed("the column's count slots were not zeroed in this frame");
                return false;
            }

            if (_countSlotsUsed >= VpCommandCompaction.Slots)
            {
                IndirectCountSlotsShort++;
                VpCommandCompaction.SlotsShort++;
                NoteRunDrawnFixed("the pass has more runs than a count buffer has slots (" + VpCommandCompaction.Slots + ")");
                return false;
            }

            if (VpCommandCompaction.ZeroesByCopy)
            {
                // Direct3D 12 (DESIGN 4.5.11). The dispatch reads the selection's entries as a shader resource and
                // writes the compacted entries and the count for unordered access; the selection wrote the first for
                // unordered access, the frame's copy left the count a copy destination, and the draw will ask both
                // outputs as indirect arguments. Each of those is a state transition Direct3D 12 requires between the
                // commands, so the order is its own: no event of the plugin is recorded here, and none may be -- a
                // compute dispatch recorded after a plugin event in a pass wrote nothing (play-27, 2026-10-08).
                long start = Began();
                int taken = _countSlotsUsed++;
                using (new ProfilingScope(commands, CompactionSampler))
                {
                    batch.IssueCompaction(commands, compaction, source, column, startCommand, commandCount, taken, frame);
                    if (VpCommandCompaction.VerificationEnabled) batch.IssueVerification(commands, compaction, source, column, startCommand, commandCount, taken);
                }

                IndirectCountCompactions++;
                IndirectCountRunCommands += commandCount;
                bound.shaderArguments = source;
                bound.shaderArgumentsGeneration = bound.argumentsGeneration;
                bound.arguments = compaction.Entries(column);
                bound.argumentsGeneration = 0;   // GPU-written alone
                bound.counts = compaction.Counts(column);
                bound.zeroCounts = null;
                bound.countSlot = taken;
#if ZANTETSU_OBSERVE
                VpCommandCompaction.NoteRecordTime(frame, System.Diagnostics.Stopwatch.GetTimestamp() - start);
#endif
                return true;
            }

            if (_barrierPending && !IssueComputeBarrier(commands, frame))
            {
                VpCommandCompaction.BarrierMissed++;
                return false;
            }

            long began = Began();

            int slot = _countSlotsUsed++;
            batch.IssueCompaction(commands, compaction, source, column, startCommand, commandCount, slot, frame);
            IndirectCountCompactions++;
            if (VpCommandCompaction.VerificationEnabled) batch.IssueVerification(commands, compaction, source, column, startCommand, commandCount, slot);
            bound.arguments = compaction.Entries(column);
            bound.argumentsGeneration = 0;
            bound.counts = compaction.Counts(column);
            bound.zeroCounts = null;   // the frame's zero pass (ZeroPass) zeroed this set's slots before any compaction
            if (VpCommandCompaction.DrawFromCopy)
            {
                // The draw reads a transfer copy of its count, made by the plugin in the draw's preparation: the run's
                // own slot of the set's draw counts (the set's lifetime is the count's).
                bound.zeroCounts = compaction.DrawCounts(column);
                bound.snapshot = true;
                bound.drawFromCopy = true;
                bound.countSnapshotSlot = slot;
            }
            else if (VpCommandCompaction.SnapshotEnabled)
            {
                // Diagnosis: the plugin copies this draw's count into the ring, where the draw reads it.
                bound.zeroCounts = VpCommandCompaction.SnapshotBuffer;
                bound.snapshot = true;
                bound.countSnapshotSlot = VpCommandCompaction.TakeSnapshotSlot(column, frame, startCommand, commandCount);
            }
            bound.countSlot = slot;
#if ZANTETSU_OBSERVE
            VpCommandCompaction.NoteRecordTime(frame, System.Diagnostics.Stopwatch.GetTimestamp() - began);
#endif
            return true;
        }
        // Direct3D 12: a run of a route that compacts drawn fixed all the same is said, the first time, with why; it
        // is counted every time (the route's line has the counts). The Vulkan route counts it, as before.
        private bool _runDrawnFixedSaid;

        private void NoteRunDrawnFixed(string why)
        {
            if (Plugin.Vulkan || _runDrawnFixedSaid) return;
            _runDrawnFixedSaid = true;
            Debug.LogWarning("VP COMMAND COMPACTION: a run of a route that compacts was drawn fixed: " + why + " (said once; the route's line counts them)");
        }

        private static readonly ProfilingSampler CompactionSampler = new ProfilingSampler("VP Command Compaction");
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
            var asset = Resources.Load<TextAsset>((Plugin.Vulkan ? "VpNativeVulkan" : VpNativeShaderBinary.ResourceFolder) + "/" + variant);
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

#if UNITY_EDITOR
        /// <summary>Tests may simulate an unavailable native device without changing route selection or Unity rendering.</summary>
        public static string EnvironmentFailureForTest;
#endif

        /// <summary>
        /// Whether the route can draw at all: URP recording through the render graph, Direct3D 12, the plugin
        /// loaded and of this contract, its compute shader in the build. Null when it can; otherwise why not.
        /// </summary>
        public static string CheckEnvironment()
        {
#if UNITY_EDITOR
            if (EnvironmentFailureForTest != null) return EnvironmentFailureForTest;
#endif
            string failure = VpGpuCullCameraRoute.CheckEnvironment();
            if (failure != null)
            {
                return failure;
            }

            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 && !Plugin.Vulkan)
            {
                return "the graphics device is " + SystemInfo.graphicsDeviceType + "; native drawing requires Direct3D 12 or Vulkan";
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
                    _drawData = new VpNativeDrawData(DrawBlocks, 16, Plugin.Vulkan ? 16 : 8, Plugin.Vulkan ? 65536 : 8192) { Name = "route ring " + (++s_ringsMade) };
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
            passes.colourBlocks.Clear();
            passes.clipBodies.Clear(); passes.clipMotion.Clear(); passes.clipCasters.Clear();
            passes.motionBodies.Clear();
            passes.casterRuns.Clear();
            passes.frame = Time.frameCount;
            if (passes.prewarm != null && passes.prewarm.failure == null && !passes.prewarm.unityCastersDeclared)
            {
                PrewarmRequest request = passes.prewarm;
                // Same zero-instance procedural declarations as real native casters, using the actual materials.
                if (request.shadow != null) DeclareCasterBounds(camera, request.batch, request.buffers, request.shadow, 0, true);
                if (request.provisionalShadow != null && request.provisionalShadow != request.shadow)
                    DeclareCasterBounds(camera, request.batch, request.buffers, request.provisionalShadow, 0, true);
                request.unityCastersDeclared = true;
            }
            if (_pointersSweptFrame != passes.frame)
            {
                // Once a frame: pointers of buffers disposed and textures destroyed since are dropped, so that a new
                // object is asked for and a dead one is never bound (a pass over the kept entries, about 15).
                _pointersSweptFrame = passes.frame;
                _pointers.DropDead();
            }

            display.NativeDrawSink = this;
            if (CompactsCommands) renderer.EnqueuePass(passes.zero);
            renderer.EnqueuePass(passes.casters);
            renderer.EnqueuePass(passes.body);
            if (passes.colourDraw != null) renderer.EnqueuePass(passes.colourDraw);
            if (Plugin.Vulkan) renderer.EnqueuePass(passes.motion);
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
            var resources = new List<IDisposable> { passes.body.TakeGpuConstants(), passes.casters.TakeGpuConstants(), passes.motion.TakeGpuConstants() };
            resources.AddRange(passes.motion.TakeMotionSelections());
            VpNativeDrawRelease.Defer("VP native draw route camera", IssuedEventsWait(), resources.ToArray());
        }

        public long PrewarmRecorded { get; private set; }
        public long PrewarmCompleted { get; private set; }
        public long PrewarmConstantDispatches { get; private set; }
        public long PrewarmCullDispatches { get; private set; }

        /// <summary>
        /// Whether <see cref="RequestPrewarm"/>'s preconditions of the route itself hold for the camera: the route's draw
        /// data made and the camera enqueued at least once (its passes exist). Until then a request is refused as "not
        /// enqueued yet"; a caller that waits for the route asks this rather than reading that refusal's words.
        /// </summary>
        public bool IsCameraEnqueued(Camera camera) => !_disposed && camera != null && _drawData != null && PassesOf(camera, false) != null;

        /// <summary>Quest-only startup request. Materials must be the display's own culled copies, not asset clones.</summary>
        public bool RequestPrewarm(Camera camera, IVpNativeDrawHost display, out string failure, bool motion = true)
        {
            ThrowIfDisposed();
            failure = null;
            if (!Plugin.Vulkan || Plugin.WarmPipelineEventId == 0) { failure = "native Vulkan warm event is unavailable"; return false; }
            if (_drawData == null) { failure = "the camera's native route has not been enqueued yet"; return false; }
            if (!(display is IVpNativeDrawWarmupHost host) || camera == null) { failure = "no native display warmup material source"; return false; }
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null) { failure = "the native camera is not enqueued"; return false; }
            if (passes.prewarm != null) { failure = passes.prewarm.failure; return failure == null; }
            var request = new PrewarmRequest { motionSubmitted = !motion };
            host.CopyNativePrewarmMaterials(request.bodies, out request.shadow, out request.provisionalShadow);
            host.NativePrewarmBuffers(out request.batch, out request.buffers);
            if (request.bodies.Count == 0) { failure = "the native display has no materials to prepare"; return false; }
            request.shadowSubmitted = request.shadow == null && request.provisionalShadow == null;
            passes.prewarm = request;
            return true;
        }

        /// <summary>Complete only after every requested pass's warm events were consumed successfully.</summary>
        public bool GetPrewarmStatus(Camera camera, out bool complete, out string failure)
        {
            complete = false; failure = null;
            PrewarmRequest request = PassesOf(camera, false)?.prewarm;
            if (request == null) return false;
            CollectResults(Time.frameCount);
            failure = request.failure;
            complete = failure == null && request.colourSubmitted && request.motionSubmitted && request.shadowSubmitted && request.computeSubmitted && request.pending == 0;
            return true;
        }

        public string DescribePrewarm(Camera camera)
        {
            PrewarmRequest request = PassesOf(camera, false)?.prewarm;
            if (request == null) return "native prewarm: not requested";
            GetPrewarmStatus(camera, out bool complete, out string failure);
            var text = new StringBuilder("native prewarm: complete=").Append(complete)
                .Append(" colour=").Append(request.colourSubmitted).Append(" motion=").Append(request.motionSubmitted)
                .Append(" shadow=").Append(request.shadowSubmitted).Append(" compute=").Append(request.computeSubmitted)
                .Append(" constantDispatches=").Append(PrewarmConstantDispatches).Append(" cullDispatches=").Append(PrewarmCullDispatches)
                .Append(" pending=").Append(request.pending)
                .Append(" consumed=").Append(request.completed).Append(" failure=").Append(failure ?? "none");
            foreach (PipelineEntry entry in request.entries) text.AppendLine().Append(entry.pipeline.Describe());
            return text.ToString();
        }

        private PipelineEntry PrewarmPipeline(PrewarmRequest request, Material material, string variant, VpNativeRenderState state)
        {
            if (material == null || request.failure != null) return null;
            PipelineEntry entry = PipelineFor(material, variant, state, out string failure);
            if (entry == null) { request.failure = "native prewarm " + variant + ": " + failure; return null; }
            if (!request.entries.Contains(entry)) request.entries.Add(entry);
            return entry;
        }

        private void PrepareBodyPrewarm(PrewarmRequest request, string variant, VpNativeRenderState state, List<PipelineEntry> into)
        {
            foreach (Material material in request.bodies)
            {
                PipelineEntry ordinary = PrewarmPipeline(request, material, variant, state);
                PipelineEntry clip = PrewarmPipeline(request, material, variant.Replace("-noclip", "").Replace("-plugin", "-direct"), state);
                if (ordinary != null) into.Add(ordinary);
                if (clip != null) into.Add(clip);
            }
        }

        private void PrewarmCompute(CommandBuffer commands, PrewarmRequest request, List<PipelineEntry> entries,
            GraphicsBuffer gpuConstants, in GlobalsInputs inputs)
        {
            if (request.failure != null || request.computeSubmitted) return;
            // The NPC startup batch is empty. The kernel's count guard makes this dispatch touch no commands/instances.
            // A populated batch already ran normal culling; do not rewrite its selection for startup preparation.
            if (request.batch != null && request.batch.CommandCount == 0)
            {
                var conditions = new VpCullConditions();
                conditions.KeepEverything();
                using (new ProfilingScope(commands, CullWarmSampler))
                    request.batch.IssueCull(commands, 0, conditions, true);
                PrewarmCullDispatches++;
            }
            foreach (PipelineEntry entry in entries)
            {
                if (entry.gpuBlocks.Count == 0) continue;
                // Borrow the actual offset table and write at zero in this pass's existing scratch buffer.
                // No draw reads these warm bytes. Real IssueOne resets its allocation and overwrites them afterwards.
                using (new ProfilingScope(commands, ConstantCopyWarmSampler))
                    RecordConstantCopy(commands, entry.gpuBlocks[0], gpuConstants, 0, inputs);
                PrewarmConstantDispatches++;
                break; // One kernel without keywords: body, shadow and stereo bindings share the compute pipeline.
            }
            request.computeSubmitted = true;
        }

        private void RecordConstantCopy(CommandBuffer commands, GpuBlock block, GraphicsBuffer gpuConstants,
            int destination, in GlobalsInputs inputs)
        {
            using (new ProfilingScope(commands, ConstantCopySampler))
            {
                ComputeShader copy = _copy;
                int kernel = _copyKernel;
                commands.SetComputeBufferParam(copy, kernel, OffsetsId, block.offsets);
                commands.SetComputeBufferParam(copy, kernel, ConstantsId, gpuConstants);
                commands.SetComputeIntParam(copy, DestinationId, destination);
                commands.SetComputeMatrixParam(copy, MatrixVpId, inputs.matrixVP);
                commands.SetComputeMatrixParam(copy, MatrixVId, inputs.matrixV);
                commands.SetComputeMatrixParam(copy, ProjectionId, inputs.projection);
                commands.SetComputeIntParam(copy, InstanceMultiplierId, inputs.instanceMultiplier);
                commands.SetComputeVectorParam(copy, ShadowBiasId, inputs.shadowBias);
                commands.SetComputeVectorParam(copy, LightDirectionId, inputs.lightDirection);
                commands.SetComputeFloatParam(copy, ShadowSliceSelectionId, Plugin.Vulkan ? 0f : inputs.sliceSelection);
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

        private void RecordPrewarm(CommandBuffer commands, PrewarmRequest request, List<PipelineEntry> entries, int frame, bool restartRenderPass, Action<IntPtr> recordEvent = null)
        {
            if (request.failure != null) return;
            // Flush this event class before actual draw preparations: no warm header can reach a draw callback.
            foreach (PipelineEntry entry in entries)
            {
                if (!_drawData.TryBeginWarmup(entry.pipeline.Id, restartRenderPass)) { request.failure = "native prewarm ring is full"; break; }
                IntPtr data = _drawData.Finish(out uint serial);
                _blocks.Add(new Block { data = data, frame = frame, serial = serial, warmup = request });
                request.pending++;
            }
            int recorded = recordEvent != null ? _drawData.RecordPrepared(recordEvent) :
                _drawData.RecordPrepared(commands, Plugin.RenderEventFunction, Plugin.WarmPipelineEventId);
            PrewarmRecorded += recorded;
        }

        private void FinishPrewarmBlock(in Block block, string failure)
        {
            if (block.warmup == null) return;
            block.warmup.pending--;
            if (failure != null) block.warmup.failure = failure;
            else { block.warmup.completed++; PrewarmCompleted++; }
        }

        public void AddBody(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer)
        {
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null)
            {
                return;
            }

            BodiesTaken++;
            var issue = new Issue { batch = batch, buffers = buffers, material = material, startCommand = startCommand, commandCount = commandCount, view = view };
            passes.bodies.Add(issue);
            if (Plugin.Vulkan) passes.motionBodies.Add(issue);
            if (VpNativeDirectClipProbe.Enabled && SeparateClipDraws && passes.directProbeFrame != passes.frame)
            {
                passes.directProbeFrame = passes.frame;
                (_directClipProbe ??= new VpNativeDirectClipProbe()).Append(this, camera, material);
            }
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

        public void AddClipBody(Camera camera, VpGpuIndexedGeometryBuffers buffers, Material material, VpGeometryRange range,
            Matrix4x4 objectToWorld, VpInstanceClip clip, Bounds worldBounds, int layer, VpClipSelection selection = null, int entry = -1)
        {
            // Every record given is drawn, one of no plane too: its command is of the clip route (DESIGN 4.5.9), so the
            // selection keeps none of its instances, and the direct variant clips nothing of a record of no plane.
            if (!SeparateClipDraws || range.indexCount == 0) return;
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null || passes.frame != Time.frameCount) throw new InvalidOperationException("clip draw camera was not enqueued");
            var issue = new ClipIssue { buffers = buffers, material = material, range = range, objectToWorld = objectToWorld, clip = clip, selection = selection, entry = entry };
            passes.clipBodies.Add(issue); passes.clipMotion.Add(issue);
        }
        public void AddClipCasters(Camera camera, VpGpuIndexedGeometryBuffers buffers, Material material, VpGeometryRange range,
            Matrix4x4 objectToWorld, VpInstanceClip clip, Bounds worldBounds, int layer, VpClipSelection selection = null, int entry = -1)
        {
            if (!SeparateClipDraws || range.indexCount == 0) return;   // a record of no plane too: see AddClipBody
            CameraPasses passes = PassesOf(camera, false);
            if (passes == null || passes.frame != Time.frameCount) throw new InvalidOperationException("clip caster camera was not enqueued");
            passes.clipCasters.Add(new ClipIssue { buffers = buffers, material = material, range = range, objectToWorld = objectToWorld, clip = clip, selection = selection, entry = entry });
            // The ordinary AddCasters declaration already includes every adopted instance's bounds, including
            // those excluded from the ordinary GPU lists. Do not duplicate that zero-instance Unity declaration.
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
        private void DeclareCasterBounds(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int layer, bool warming = false)
        {
            int frame = Time.frameCount;
            if (!warming && _declaredFrame == frame && ReferenceEquals(_declaredCamera, camera))
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
                    _zeroArguments.SetData(new[] { new GraphicsBuffer.IndirectDrawIndexedArgs() }, "VpNativeDrawRoute.cs:1258");
                }

                batch.NativeDrawBuffers(0, out _, out GraphicsBuffer visible, out _, out _, out GraphicsBuffer instances, out GraphicsBuffer clips, out GraphicsBuffer cullCommands);
                _declareProperties.SetBuffer("_VpVertices", buffers.VertexBuffer);
                _declareProperties.SetBuffer("_VpInstanceObjectToWorld", instances);
                _declareProperties.SetBuffer("_VpInstanceClip", clips);
                _declareProperties.SetBuffer("_VpCullCommands", cullCommands);
                _declareProperties.SetBuffer("_VpVisible", visible);
                var parameters = new RenderParams(material)
                {
                    camera = camera,
                    layer = layer,
                    matProps = _declareProperties,
                    worldBounds = warming ? new Bounds(camera.transform.position, Vector3.one * 10000f) : batch.WorldBounds,
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
                .Append(", motion taken ").Append(MotionTaken).Append(", issued ").Append(MotionIssued)
                .Append(", caster runs taken ").Append(CastersTaken).Append(", caster issues ").Append(CastersIssued)
                .Append(" (last splits ").Append(LastSplitCount).Append("), caster runs not drawn ").Append(CasterRunsNotDrawn)
                .Append(LastCasterSkipReason != null ? " (last: " + LastCasterSkipReason + ")" : "")
                .Append(", XR frames not drawn ").Append(XrFramesNotDrawn)
                .Append(", refused ").Append(Refused).Append(", ring full ").Append(RingFull)
                .Append(", pipeline failures ").Append(PipelineFailures)
                .Append(", single-view bodies from doubled arguments ").Append(SingleViewBodiesFromDoubled)
                .Append(", shadow map format refusals ").Append(ShadowMapFormatRefusals)
                .Append(", runs of frames never executed: bodies ").Append(BodiesDropped).Append(", casters ").Append(CasterRunsDropped)
                .Append(", caster bounds declared ").Append(CasterBoundsDeclared)
                .Append(", caster bias ").Append(CasterDepthBiasSigned).Append("/").Append(CasterSlopeScaledDepthBiasSigned)
                .Append(", warm events ").Append(PrewarmRecorded).Append(" completed ").Append(PrewarmCompleted)
                .Append(", warm compute dispatches: constants ").Append(PrewarmConstantDispatches).Append(" cull ").Append(PrewarmCullDispatches)
                .Append(", native pointers asked ").Append(_pointers.Acquisitions).Append(" / kept ").Append(_pointers.Hits)
                .Append(", issues after dispose ").Append(IssuesAfterDispose).Append(", buffers retired through the route ").Append(BuffersRetired)
                .Append(", events never run ").Append(EventsNeverRan).Append(", results lost to reuse ").Append(ResultsLost).Append("; ").Append(VpNativeDrawRelease.Describe())
                .Append(", last frame issues ").Append(LastFrameIssues)
                .Append(", indirect-count issues ").Append(IndirectCountIssues).Append(" (compactions ").Append(IndirectCountCompactions)
                .Append(", zero events ").Append(IndirectCountZeroEvents).Append(", runs fixed for want of a slot ").Append(IndirectCountSlotsShort)
                .Append(", runs fixed for want of a zero reservation ").Append(IndirectCountZeroMissed).Append(", zero events refused ").Append(IndirectCountZeroRefused)
                .Append(", compute barriers ").Append(IndirectCountBarriers)
                .Append(", counts zeroed by copy ").Append(IndirectCountCopies).Append(", commands of the compacted runs ").Append(IndirectCountRunCommands)
                .Append(CompactionHalted != null ? "; compaction halted: " + CompactionHalted : "").Append(")")
                .Append(", direct clip draws ").Append(DirectClipDrawsIssued)
                .Append(", direct clip records: colour taken ").Append(DirectClipBodiesTaken).Append(" / kept ").Append(DirectClipBodiesKept)
                .Append(", motion taken ").Append(DirectClipMotionTaken).Append(" / kept ").Append(DirectClipMotionKept)
                .Append(", casters taken ").Append(DirectClipCastersTaken).Append(" / kept by split ")
                .Append(_directClipCastersKept[0]).Append("/").Append(_directClipCastersKept[1]).Append("/").Append(_directClipCastersKept[2]).Append("/").Append(_directClipCastersKept[3])
                .Append(", clip list bodies: runs taken ").Append(ClipBodyRunsTaken).Append(", entries taken ").Append(ClipBodyEntriesTaken)
                .Append(" / kept ").Append(ClipBodyEntriesKept).Append(", issues ").Append(ClipBodyIssues)
                .Append(", clip list casters: runs taken ").Append(ClipCasterRunsTaken).Append(", entries taken ").Append(ClipCasterEntriesTaken)
                .Append(" / kept by split ")
                .Append(_clipCasterEntriesKept[0]).Append("/").Append(_clipCasterEntriesKept[1]).Append("/").Append(_clipCasterEntriesKept[2]).Append("/").Append(_clipCasterEntriesKept[3])
                .Append(", issues ").Append(ClipCasterIssues)
                .Append(", GPU constant dispatches ").Append(GpuConstantDispatches)
                .Append(", shared constant blocks ").Append(SharedGpuConstantBlocks);
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
                if (Plugin.TryReadIndirectCountInfo(out bool waveOperations, out uint lanesLeast, out uint lanesMost, out ulong countDraws, out ulong countDrawMost))
                {
                    text.Append(", count draws ").Append(countDraws).Append(" (most commands ").Append(countDrawMost).Append("), wave operations ")
                        .Append(waveOperations ? "of " + lanesLeast + " to " + lanesMost + " lanes" : "none");
                }

                if (CommandCompactionChoice != null && !Plugin.Vulkan) text.Append("; command compaction: ").Append(VpCommandCompaction.DescribeRun());
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

            _directClipProbe?.Dispose();
            _directClipProbe = null;
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
                owed.Add(passes.motion.TakeGpuConstants());
                owed.AddRange(passes.motion.TakeMotionSelections());
            }

            _cameras.Clear();
            owed.Add(_zeroArguments);
            _zeroArguments = null;
            if (_zeroCounts != null) owed.Add(_zeroCounts);
            _zeroCounts = null;
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
            passes.zero = new ZeroPass(this, passes);
            passes.body = new BodyPass(this, passes);
            if (passes.body.PreparesColour) passes.colourDraw = new ColourDrawPass(this, passes);
            passes.motion = new BodyPass(this, passes, true);
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
                    FinishPrewarmBlock(block, "native prewarm result generation was lost");
                    _blocks.RemoveAt(i);
                    continue;
                }

                Plugin.Result? result = VpNativeDrawData.ResultOf(block.data);
                if (result == null)
                {
                    if (ring != null && ring.StateOf(block.serial) == VpNativeDrawData.BlockState.Free)
                    {
                        EventsNeverRan++;
                        FinishPrewarmBlock(block, "native prewarm event was never executed");
                        _blocks.RemoveAt(i);
                    }

                    continue;
                }

                if (block.warmup != null)
                {
                    FinishPrewarmBlock(block, result.Value == Plugin.Result.Recorded ? null : "native prewarm refused: " + result.Value);
                    _blocks.RemoveAt(i);
                    continue;
                }
                ResultsByKind[Mathf.Clamp((int)result.Value, 0, ResultsByKind.Length - 1)]++;
                if (result.Value != Plugin.Result.Recorded)
                {
                    Refused++;
                    LastFailure = "the plugin refused an issue: " + result.Value;
                    if (block.zero || block.barrier)
                    {
                        // The count slots of that frame's set may not have been zeroed before its compaction, or a
                        // compaction may have read entries before the selection wrote them: no further compaction from
                        // here; every run draws fixed. Said once, kept.
                        IndirectCountZeroRefused++;
                        VpCommandCompaction.ZeroRefused++;
                        if (CompactionHalted == null)
                        {
                            CompactionHalted = (block.zero ? "a zero event" : "a compute barrier event") + " was refused (" + result.Value + ") in frame " + block.frame + ": the compaction is off, the runs draw fixed";
                            VpCommandCompaction.Halted = CompactionHalted;
                            Debug.LogWarning("VP COMMAND COMPACTION: halted: " + CompactionHalted);
                        }
                    }
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

            var asset = Resources.Load<TextAsset>((Plugin.Vulkan ? "VpNativeVulkan" : VpNativeShaderBinary.ResourceFolder) + "/" + variant);
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
                    if (buffer.Name == "$Globals" || buffer.Name.StartsWith("VGlobals", StringComparison.Ordinal) || buffer.Name.StartsWith("PGlobals", StringComparison.Ordinal) || buffer.Name == "LightShadows" || buffer.Name.StartsWith("UnityStereo", StringComparison.Ordinal))
                    {
                        var offsets = new uint[SlotCount];
                        for (int i = 0; i < offsets.Length; i++) offsets[i] = Unused;
                        foreach ((string name, int slot) in Slots)
                        {
                            VpNativeShaderBinary.Constant constant = buffer.Find(name);
                            if (constant != null) offsets[slot] = (uint)constant.Offset;
                        }

                        var table = new GraphicsBuffer(GraphicsBuffer.Target.Structured, SlotCount, sizeof(uint)) { name = "VP Native Offsets " + buffer.Name };
                        table.SetData(offsets, "VpNativeDrawRoute.cs:1695");
                        entry.gpuBlocks.Add(new GpuBlock { binding = binding, size = buffer.Size, offsets = table,
                            layoutKey = buffer.Size + ":" + string.Join(",", offsets) });
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
            public IntPtr arguments, shaderArguments, visible, instances, clips, cullCommands, indexBuffer, vertexBuffer, specCube, paletteSurface, paletteCaps, baseMap, shadowMap, gpuConstants, counts, zeroCounts;
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
            Rect viewport, in GlobalsInputs inputs, GraphicsBuffer gpuConstants, ref int gpuConstantsUsed, int frame,
            List<ClipIssue> direct = null, int directStart = 0, int directCount = 0, int constantContext = 0)
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
                // The indirect command reader needs this even when the vertex shader does not bind it.
                if (directCount == 0) pointers.arguments = _pointers.Buffer(bound.arguments, bound.argumentsGeneration);
                // The entries the vertex stage reads: the argument buffer itself, but for a compacted run of the
                // Direct3D 12 route, whose stage reads the selection's own.
                pointers.shaderArguments = bound.shaderArguments != null && entry.vertexArguments >= 0
                    ? _pointers.Buffer(bound.shaderArguments, bound.shaderArgumentsGeneration) : pointers.arguments;
                if (bound.counts != null) pointers.counts = _pointers.Buffer(bound.counts);   // GPU-written alone: one generation
                if (bound.zeroCounts != null) pointers.zeroCounts = _pointers.Buffer(bound.zeroCounts);
                if (entry.vertexVisible >= 0) pointers.visible = _pointers.Buffer(bound.visible, bound.visibleGeneration);
                if (entry.vertexInstances >= 0) pointers.instances = _pointers.Buffer(bound.instances, bound.instancesGeneration);
                if (entry.vertexClips >= 0) pointers.clips = _pointers.Buffer(bound.clips, bound.clipsGeneration);
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
                for (int b = 0; b < entry.gpuBlocks.Count; b++)
                {
                    var sharedKey = (entry.gpuBlocks[b].layoutKey, constantContext);
                    if (Plugin.Vulkan && Plugin.VulkanShareGpuConstants && _copiedGpuBlocks.TryGetValue(sharedKey, out int sharedOffset))
                    {
                        gpuOffsets[b] = sharedOffset; SharedGpuConstantBlocks++; continue;
                    }
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
                    RecordConstantCopy(commands, block, gpuConstants, gpuOffsets[b], inputs);
                    GpuConstantDispatches++;
                    if (Plugin.Vulkan && Plugin.VulkanShareGpuConstants) _copiedGpuBlocks[sharedKey] = gpuOffsets[b];
                }
            }

            // 3. The per-material and per-draw constants, written on the CPU.
            using (CpuConstantsMarker.Auto())
            {
                for (int b = 0; b < entry.cpuBlocks.Count; b++)
                {
                    if (entry.cpuBlocks[b].name == "VpNativeDirect") FillDirectConstants(entry.cpuBlocks[b].block, direct, directStart, directCount);
                    else FillCpuBlock(entry.cpuBlocks[b], material, frame);
                }
            }

            // 4. The event data block, and the plugin event's reservation in the command buffer.
            using (EventDataMarker.Auto())
            {
                VpNativeDrawData drawData = _drawData;
                bool began = directCount > 0
                    ? drawData.TryBeginDirect(entry.pipeline.Id, pointers.indexBuffer, (long)bound.indexBuffer.count * bound.indexBuffer.stride,
                        bound.indexBuffer.stride, new ReadOnlySpan<Plugin.DirectCommand>(_directCommands, 0, directCount))
                    : bound.counts != null
                    ? drawData.TryBeginIndirectCount(entry.pipeline.Id, pointers.arguments, startCommand, commandCount, pointers.counts, (uint)(bound.countSlot * sizeof(uint)),
                        bound.snapshot ? pointers.zeroCounts : IntPtr.Zero, bound.snapshot ? (uint)(bound.countSnapshotSlot * sizeof(uint)) : 0u, 0u,
                        pointers.indexBuffer, (long)bound.indexBuffer.count * bound.indexBuffer.stride, bound.indexBuffer.stride, bound.drawFromCopy)
                    : drawData.TryBegin(entry.pipeline.Id, pointers.arguments, startCommand, commandCount, pointers.indexBuffer,
                        (long)bound.indexBuffer.count * bound.indexBuffer.stride, bound.indexBuffer.stride);
                if (began && bound.counts != null) { IndirectCountIssues++; VpCommandCompaction.Issues++; }
                if (!began)
                {
                    RingFull++;
                    LastFailure = "every event data block is still with the plugin";
                    return false;
                }

                drawData.AddViewport(viewport);
                if (entry.vertexArguments >= 0)
                {
                    GraphicsBuffer read = bound.shaderArguments ?? bound.arguments;
                    drawData.SetBuffer(entry.vertexArguments, pointers.shaderArguments, read.count, read.stride, true);
                }
                drawData.SetBuffer(entry.vertexVertices, pointers.vertexBuffer, bound.vertexBuffer.count, bound.vertexBuffer.stride, false);
                if (entry.vertexInstances >= 0) drawData.SetBuffer(entry.vertexInstances, pointers.instances, bound.instances.count, bound.instances.stride, false);
                if (entry.vertexClips >= 0) drawData.SetBuffer(entry.vertexClips, pointers.clips, bound.clips.count, bound.clips.stride, false);
                if (entry.vertexVisible >= 0) drawData.SetBuffer(entry.vertexVisible, pointers.visible, bound.visible.count, bound.visible.stride, false);
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
        private void IssuePendingEvents(CommandBuffer commands, int firstBlock = -1, bool group = false)
        {
            if (!group) { _drawData?.RecordPrepared(commands, Plugin.RenderEventFunction, Plugin.DrawEventId); return; }
            PrepareGroupEvents(commands, firstBlock);
            foreach (IntPtr block in _groupPointers)
                commands.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.GroupPreparedEventId, block);
        }
        private void PrepareGroupEvents(CommandBuffer commands, int firstBlock)
        {
            _drawData.GroupPrepared(_groupPointers);
            // The draws' entries give way to their groups'; a compute barrier event's entry (its own event, recorded already) stays.
            for (int i = _blocks.Count - 1; i >= firstBlock; i--) if (!_blocks[i].barrier) _blocks.RemoveAt(i);
            foreach (IntPtr block in _groupPointers)
                _blocks.Add(new Block { data = block, frame = Time.frameCount, serial = VpNativeDrawData.SerialOf(block) });
            _drawData.RecordPrepared(commands, Plugin.RenderEventFunction, Plugin.GroupPrepareEventId);
        }
        private void FillDirectConstants(VpNativeConstantBlock block, List<ClipIssue> records, int start, int count)
        {
            if (records == null || count < 1 || count > 32 || block.Bytes.Length != 32 * 208)
                throw new InvalidOperationException("direct Clip constant layout must be 32 records of 208 bytes");
            Array.Clear(block.Bytes, 0, block.Bytes.Length);
            for (int record = 0; record < count; record++)
            {
                ClipIssue issue = records[start + record];
                block.SetDirectClipRecord(record, issue.objectToWorld, issue.clip);
            }
        }
        // The records of <paramref name="all"/> the camera's judgement of the clip list keeps for one stage, into
        // _keptClips: the colour by the body's judgement; the motion vectors by their own conditions when they have
        // some (eyes that end nearer), else by the body's; a shadow split by the casters' judgement and that slice's
        // own. A record with no judgement (the probe's, or a camera whose selection was not issued) is kept.
        private readonly List<ClipIssue> _keptClips = new List<ClipIssue>(8);

        private List<ClipIssue> KeptClipBodies(List<ClipIssue> all, VpCullConditions motionConditions)
        {
            _keptClips.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                ClipIssue record = all[i];
                if (record.selection == null || record.entry < 0
                    || (motionConditions != null ? record.selection.KeptBy(record.entry, motionConditions) : record.selection.BodyKept(record.entry)))
                {
                    _keptClips.Add(record);
                }
            }

            return _keptClips;
        }

        private List<ClipIssue> KeptClipCasters(List<ClipIssue> all, Matrix4x4 sliceViewProjection, float bias, bool sliceSelection)
        {
            _keptClips.Clear();
            for (int i = 0; i < all.Count; i++)
            {
                ClipIssue record = all[i];
                if (record.selection == null || record.entry < 0
                    || record.selection.CasterKeptInSlice(record.entry, sliceViewProjection, bias, sliceSelection))
                {
                    _keptClips.Add(record);
                }
            }

            return _keptClips;
        }

        private int IssueClipDraws(CommandBuffer commands, List<ClipIssue> records, string variant, VpNativeRenderState state,
            RenderTexture shadowMap, Rect viewport, GlobalsInputs inputs, GraphicsBuffer gpuConstants, ref int used, int frame, int context = 0)
        {
            int issued = 0, start = 0;
            while (start < records.Count)
            {
                ClipIssue first = records[start];
                int count = 1;
                while (count < 32 && start + count < records.Count && ReferenceEquals(records[start + count].material, first.material)
                    && ReferenceEquals(records[start + count].buffers, first.buffers)) count++;
                var directState = state;
                if (variant == CasterVariant) directState.cullMode = CasterPass.CullOf(first.material);
                PipelineEntry entry = PipelineFor(first.material, variant.Replace("-plugin", "-direct"), directState, out string failure);
                if (entry == null) { PipelineFailures++; LastFailure = failure; start += count; continue; }
                for (int i = 0; i < count; i++)
                {
                    VpGeometryRange range = records[start + i].range;
                    _directCommands[i] = new Plugin.DirectCommand { indexCount = (uint)range.indexCount, instanceCount = 1,
                        firstIndex = (uint)range.indexStart, vertexOffset = 0, firstInstance = (uint)i };
                }
                var bound = new Bound { indexBuffer = first.buffers.IndexBuffer, vertexBuffer = first.buffers.VertexBuffer, shadowMap = shadowMap,
                    indexGeneration = first.buffers.IndexGeneration, vertexGeneration = first.buffers.VertexGeneration };
                if (IssueOne(commands, entry, first.material, bound, 0, count, viewport, inputs, gpuConstants, ref used, frame,
                    records, start, count, context)) { issued += count; DirectClipDrawsIssued += count; }
                start += count;
            }
            return issued;
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
                buffer = new GraphicsBuffer(Plugin.Vulkan ? GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Constant : GraphicsBuffer.Target.Raw, GpuConstantBytesPerPass / 4, 4) { name = purpose + (camera != null ? camera.name : "") };
            }

            return buffer;
        }

        // ---- the body pass -----------------------------------------------------------------------------------------

        private sealed class BodyPassData
        {
            public BodyPass pass;
            public bool preparedProjectionFlipped;
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
            private readonly bool _motion;
            public bool PreparesColour { get; }
            private RTHandle _motionColour, _motionDepth;
            private List<Issue> Bodies => _motion ? _passes.motionBodies : _passes.bodies;
            private List<ClipIssue> Clips => _motion ? _passes.clipMotion : _passes.clipBodies;
            private bool NeedsPrewarm => _passes.prewarm != null && _passes.prewarm.failure == null &&
                (_motion ? !_passes.prewarm.motionSubmitted : !_passes.prewarm.colourPrepared);
            private GraphicsBuffer _gpuConstants;
            private int _gpuConstantsUsed;
            private readonly Dictionary<VpIndexedIndirectDrawBatch, VpIndexedIndirectDrawBatch.CullOutput> _motionSelections = new();
            /// <summary>The motion selections this pass keeps (one a batch), for the zero pass to find their compaction sets.</summary>
            internal void ForEachMotionSelection(Action<VpIndexedIndirectDrawBatch, VpIndexedIndirectDrawBatch.CullOutput> act)
            {
                foreach (var pair in _motionSelections) act(pair.Key, pair.Value);
            }
            private readonly HashSet<VpIndexedIndirectDrawBatch> _motionSelected = new();
            private readonly VpCullConditions _motionConditions = new();

            public IDisposable[] TakeMotionSelections()
            {
                var values = new IDisposable[_motionSelections.Count]; int i = 0;
                foreach (var value in _motionSelections.Values) values[i++] = value;
                _motionSelections.Clear(); return values;
            }

            public BodyPass(VpNativeDrawRoute route, CameraPasses passes, bool motion = false)
            {
                _route = route;
                _passes = passes;
                _motion = motion;
                PreparesColour = !motion && Plugin.Vulkan && Plugin.VulkanColourPreparation;
                renderPassEvent = motion ? RenderPassEvent.AfterRenderingPrePasses : PreparesColour ? RenderPassEvent.BeforeRenderingOpaques : RenderPassEvent.AfterRenderingOpaques;
                profilingSampler = new ProfilingSampler(motion ? "VP Native AppSW" : PreparesColour ? "VP Native Colour Prepare" : "VP Native Draw");
            }

            /// <summary>Hands the GPU constant buffer over (to be released once no event reads it) and forgets it.</summary>
            public GraphicsBuffer TakeGpuConstants()
            {
                _motionColour?.Release(); _motionColour = null;
                _motionDepth?.Release(); _motionDepth = null;
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

                if (_motion && (cameraData.xr == null || !cameraData.xr.enabled || !cameraData.xr.hasMotionVectorPass)) { Bodies.Clear(); return; }
                if (_motion) _route.MotionTaken += Bodies.Count;
                else _route.Recorded++;
                _route.CollectResults(_passes.frame);
                if (!_motion) _route.LastFrameIssues = 0;
                if (Bodies.Count == 0 && Clips.Count == 0 && !NeedsPrewarm)
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
                    Bodies.Clear();
                    return;
                }

                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                TextureHandle colour = resourceData.activeColorTexture;
                TextureHandle depth = resourceData.activeDepthTexture;
                if (!colour.IsValid() || !depth.IsValid())
                {
                    _route.LastFailure = "no active colour or depth target";
                    _route.BodiesDropped += Bodies.Count;
                    Bodies.Clear();
                    return;
                }

                string variant = _route.BodyVariantOverride ?? VariantName(BodyVariantFor(frameData.Get<UniversalLightData>(), frameData.Get<UniversalShadowData>()), stereo);
                if (_motion) variant = "motion-plugin-stereo";
                if (Plugin.Vulkan && (_motion ? Plugin.VulkanNoClipMotionShaders : Plugin.VulkanNoClipColourShaders)) variant += "-noclip";
                if (!_motion) _route.LastBodyVariant = variant;
                TextureHandle shadowMap = _motion ? TextureHandle.nullHandle : resourceData.mainShadowsTexture;
                if (_motion)
                {
                    var id = cameraData.xr.motionVectorRenderTarget;
                    var desc = cameraData.xr.motionVectorRenderTargetDesc;
                    if (_motionColour == null) _motionColour = RTHandles.Alloc(id);
                    else if (_motionColour.nameID != id) RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _motionColour, id);
                    if (_motionDepth == null) _motionDepth = RTHandles.Alloc(id);
                    else if (_motionDepth.nameID != id) RTHandleStaticHelpers.SetRTHandleUserManagedWrapper(ref _motionDepth, id);
                    var info = new RenderTargetInfo { width = desc.width, height = desc.height, volumeDepth = desc.volumeDepth, msaaSamples = desc.msaaSamples, format = desc.graphicsFormat };
                    colour = renderGraph.ImportTexture(_motionColour, info);
                    info.format = desc.depthStencilFormat;
                    depth = renderGraph.ImportTexture(_motionDepth, info);
                }
                RenderTargetInfo colourDesc = renderGraph.GetRenderTargetInfo(colour);
                RenderTargetInfo depthDesc = renderGraph.GetRenderTargetInfo(depth);
                RenderTextureDescriptor target = _motion ? cameraData.xr.motionVectorRenderTargetDesc : cameraData.cameraTargetDescriptor;
                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(passName, out BodyPassData data, profilingSampler, file: RenderGraphFile))
                {
                    data.pass = this;
                    // Match UniversalCameraData's target convention without resolving/reading the attachments
                    // before their first draw. Such a read forces RenderGraph to open an empty clear pass.
                    data.preparedProjectionFlipped = !SystemInfo.graphicsUVStartsAtTop ||
                        cameraData.isSceneViewCamera || cameraData.isPreviewCamera || cameraData.targetTexture != null ||
                        !resourceData.isActiveTargetBackBuffer;
                    data.colour = colour;
                    data.depth = depth;
                    data.shadowMap = shadowMap;
                    data.cameraData = cameraData;
                    data.variant = variant;
                    data.colourFormat = colourDesc.format != GraphicsFormat.None ? colourDesc.format : target.graphicsFormat;
                    data.depthFormat = depthDesc.format != GraphicsFormat.None ? depthDesc.format : target.depthStencilFormat;
                    data.sampleCount = colourDesc.msaaSamples > 0 ? colourDesc.msaaSamples : Mathf.Max(1, target.msaaSamples);
                    data.width = colourDesc.width > 0 ? colourDesc.width : target.width;
                    data.height = colourDesc.height > 0 ? colourDesc.height : target.height;
                    data.stereo = stereo;
                    data.viewport = xr && !_motion ? cameraData.xr.GetViewport(0) : new Rect(0f, 0f, data.width, data.height);
                    if (!PreparesColour)
                    {
                        builder.UseTexture(colour, AccessFlags.ReadWrite);
                        builder.UseTexture(depth, AccessFlags.ReadWrite);
                    }
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
                    _route.IssuesAfterDispose += Bodies.Count;
                    Bodies.Clear();
                    return;
                }

                _route.DropUnrecorded();
                CommandBuffer commands = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                RTHandle colourHandle = PreparesColour ? null : data.colour;
                RTHandle depthHandle = PreparesColour ? null : data.depth;
                if (!PreparesColour && data.stereo)
                {
                    // Every slice of the eye texture: the vertex stage picks the eye's slice (SV_RenderTargetArrayIndex).
                    commands.SetRenderTarget(colourHandle, depthHandle, 0, CubemapFace.Unknown, -1);
                }
                else
                {
                    if (!PreparesColour) commands.SetRenderTarget(colourHandle, depthHandle);
                }

                GlobalsInputs inputs;
                VpNativeRenderState state;
                RenderTexture shadowMap = null;
                GraphicsBuffer gpuConstants;
                using (BodyPrepareMarker.Auto())
                {
                    bool flipped = _motion ? false : PreparesColour ? data.preparedProjectionFlipped : data.cameraData.IsRenderTargetProjectionMatrixFlipped(colourHandle, depthHandle);
                    inputs = new GlobalsInputs
                    {
                        matrixV = data.cameraData.GetViewMatrix(0),
                        projection = GL.GetGPUProjectionMatrix(data.cameraData.GetProjectionMatrix(0), flipped),
                        instanceMultiplier = data.stereo && !Plugin.Vulkan ? 2 : 1,
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
                    _route._copiedGpuBlocks.Clear();
                }

                if (NeedsPrewarm)
                {
                    PrewarmRequest request = _passes.prewarm;
                    if (PreparesColour)
                    {
                        _route.PrepareBodyPrewarm(request, data.variant, state, request.colourEntries);
                        _route.PrewarmCompute(commands, request, request.colourEntries, gpuConstants, inputs);
                        request.colourPrepared = true;
                    }
                    else
                    {
                        var entries = new List<PipelineEntry>(request.bodies.Count * 2);
                        _route.PrepareBodyPrewarm(request, data.variant, state, entries);
                        _route.PrewarmCompute(commands, request, entries, gpuConstants, inputs);
                        _route.RecordPrewarm(commands, request, entries, _passes.frame, true);
                        if (_motion) request.motionSubmitted = true;
                        else { request.colourPrepared = true; request.colourSubmitted = true; }
                    }
                }

                int firstBlock = _route._blocks.Count;
                Rect viewport = data.viewport;
                float motionFar = _motion ? VpAppSwDistanceExperiment.DistanceFor(_passes.camera) : 0f;
                bool compacts = _route.CompactsCommands;
                if (compacts) _route.BeginCompactions();
                _motionSelected.Clear();
                if (motionFar > 0f)
                {
                    _motionConditions.KeepEverything();
                    int eyes = data.stereo ? 2 : 1;
                    for (int eye = 0; eye < eyes; eye++)
                    {
                        _motionConditions.SetEye(eye, data.cameraData.GetViewMatrix(eye), data.cameraData.GetProjectionMatrix(eye));
                        // Replace only each eye's far culling plane. The rendering projections stay at colour far.
                        Vector4 row = data.cameraData.GetViewMatrix(eye).GetRow(2);
                        _motionConditions.eyePlanes[eye * VpCullConditions.EyePlaneCount + 5] = new Vector4(row.x, row.y, row.z, row.w + motionFar);
                    }
                    _motionConditions.eyeCount = eyes;
                }
                {
                    for (int i = 0; i < Bodies.Count; i++)
                    {
                        Issue issue = Bodies[i];
                        // Direct3D 12: a clip list's run is drawn by the variant that reads no selection; the
                        // selection's own commands by the variant with no clip when the display draws its clipped
                        // instances apart (its material says so), and with the clip record otherwise. The Vulkan
                        // route's variant was settled when the pass was recorded.
                        string issueVariant = Plugin.Vulkan ? data.variant
                            : issue.clipList ? ClipListVariantOf(data.variant)
                            : _route.OrdinaryDrawsHaveNoClip(issue.material) ? data.variant + NoClipSuffix : data.variant;
                        PipelineEntry entry = _route.PipelineFor(issue.material, issueVariant, state, out string failure);
                        if (entry == null)
                        {
                            _route.PipelineFailures++;
                            _route.LastFailure = failure;
                            continue;
                        }

                        if (issue.clipList)
                        {
                            // The list's entries are doubled as the batch's are; the same rule as below.
                            if (data.stereo && !issue.batch.ClipEntriesSinglePassInstanced)
                            {
                                _route.PipelineFailures++;
                                _route.LastFailure = "a Single Pass Instanced frame, but the clip list's arguments are not doubled for it";
                                continue;
                            }

                            inputs.instanceMultiplier = issue.batch.ClipEntriesSinglePassInstanced ? 2 : 1;
                            issue.batch.NativeClipDrawBuffers(out GraphicsBuffer listArguments, out _, out GraphicsBuffer listInstances, out GraphicsBuffer listClips);
                            var listBound = new Bound
                            {
                                arguments = listArguments, instances = listInstances, clips = listClips,
                                indexBuffer = issue.buffers.IndexBuffer, vertexBuffer = issue.buffers.VertexBuffer, shadowMap = shadowMap,
                            };
                            listBound.TakeGenerations(issue.batch, issue.buffers);
                            listBound.argumentsGeneration = issue.batch.ClipArgumentGeneration;

                            // The camera's judgement of the list, made when its selection was issued (before this
                            // pass): one issue for each run of entries it keeps. The argument buffers are the list's
                            // own, unchanged; an entry not kept is simply in no run.
                            int listEnd = issue.startCommand + issue.commandCount;
                            _route.ClipBodyEntriesTaken += issue.commandCount;
                            for (int at = issue.startCommand; at < listEnd;)
                            {
                                if (issue.selection != null && !issue.selection.BodyKept(at))
                                {
                                    at++;
                                    continue;
                                }

                                int run = 1;
                                while (at + run < listEnd && (issue.selection == null || issue.selection.BodyKept(at + run)))
                                {
                                    run++;
                                }

                                _route.ClipBodyEntriesKept += run;
                                if (_route.IssueOne(commands, entry, issue.material, listBound, at, run, viewport, inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame))
                                {
                                    _route.ClipBodyIssues++;
                                }

                                at += run;
                            }

                            continue;
                        }

                        // The body's entries hold two physical instances a kept instance exactly when the batch was
                        // uploaded for Single Pass Instanced. The stereo variant reads them one an eye, and cannot
                        // draw a batch that holds one. A single-view camera of the same frame -- a picture camera
                        // beside the XR one -- draws the doubled batch by the shader's own rule, as by Unity's calls
                        // (ProvisionalCutDriver.CollectSnapshot): the multiplier says the entries are doubled and the
                        // single-view variant drops the second copy of each instance (2026-10-09: until then such a
                        // camera's bodies were not issued, and its pictures of a walk were without them).
                        // Vulkan multiview reads one instance per object and never uses doubled entries.
                        if (Plugin.Vulkan ? issue.batch.SinglePassInstanced : data.stereo && !issue.batch.SinglePassInstanced)
                        {
                            _route.PipelineFailures++;
                            _route.LastFailure = Plugin.Vulkan
                                ? "a Vulkan multiview frame, but the batch's arguments are doubled for Single Pass Instanced"
                                : "a Single Pass Instanced frame, but the batch's arguments are not doubled for it";
                            continue;
                        }

                        inputs.instanceMultiplier = issue.batch.SinglePassInstanced ? 2 : 1;

                        issue.batch.NativeDrawBuffers(
                            issue.view, out GraphicsBuffer forwardArguments, out GraphicsBuffer forwardVisible, out _, out _,
                            out GraphicsBuffer instances, out GraphicsBuffer clips, out GraphicsBuffer cullCommands);
                        if (motionFar > 0f)
                        {
                            if (!_motionSelections.TryGetValue(issue.batch, out var selection))
                                _motionSelections.Add(issue.batch, selection = issue.batch.CreateCullOutput());
                            if (_motionSelected.Add(issue.batch))
                            {
                                // The CPU time of recording the extra culling's commands (VpAppSwMarkers), not the GPU's.
                                using (VpAppSwMarkers.NativeCullRecord.Auto())
                                {
                                    // The commands of the clip route keep nothing here either: the batch's commands say which.
                                    issue.batch.IssueCull(commands, selection, _motionConditions);
                                    // Its entries are read by this pass's compaction next: the plugin's barrier goes between.
                                    _route.NoteSelectionIssued();
                                }
                            }
                            forwardArguments = selection.ForwardArguments; forwardVisible = selection.ForwardVisible;
                        }
                        var bound = new Bound
                        {
                            arguments = forwardArguments, visible = forwardVisible, instances = instances, clips = clips, cullCommands = cullCommands,
                            indexBuffer = issue.buffers.IndexBuffer, vertexBuffer = issue.buffers.VertexBuffer, shadowMap = shadowMap,
                        };
                        bound.TakeGenerations(issue.batch, issue.buffers);
                        if (compacts)
                        {
                            // Experiment: this run's body entries compacted (the motion's from its own selection), drawn by count.
                            VpIndexedIndirectDrawBatch.Compaction compaction = motionFar > 0f
                                ? issue.batch.CompactionOf(_motionSelections[issue.batch], _passes.frame) : issue.batch.CompactionOf(issue.view, _passes.frame);
                            _route.CompactRun(commands, issue.batch, compaction, forwardArguments, _motion ? 2 : 0, issue.startCommand, issue.commandCount, _passes.frame, ref bound);
                        }
                        if (_route.IssueOne(commands, entry, issue.material, bound, issue.startCommand, issue.commandCount, viewport, inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame))
                        {
                            if (_motion) _route.MotionIssued++; else _route.BodiesIssued++;
                            if (!_motion && !data.stereo && issue.batch.SinglePassInstanced) _route.SingleViewBodiesFromDoubled++;
                            // No string is made here: the name is the binding's, read by the diagnosis that asks.
                            _route.LastBodyGpuBlocks.Clear();
                            for (int b = 0; b < entry.gpuBlocks.Count; b++)
                            {
                                _route.LastBodyGpuBlocks.Add((entry.pipeline.Bindings[entry.gpuBlocks[b].binding], _route._gpuOffsets[b], entry.gpuBlocks[b].size));
                            }
                        }
                    }
                }

                // The direct clip draws: only the records the camera keeps for this stage. With none kept the pass ends
                // as one with no clipped instance does (the ordinary selection above still left every one of them out).
                bool grouped = false;
                if (Clips.Count > 0)
                {
                    // The motion pass's selection is one section with the distance or without it (VpAppSwMarkers).
                    if (_motion) VpAppSwMarkers.MotionClipKeep.Begin();
                    List<ClipIssue> kept = _route.KeptClipBodies(Clips, _motion && motionFar > 0f ? _motionConditions : null);
                    if (_motion) VpAppSwMarkers.MotionClipKeep.End();
                    if (_motion)
                    {
                        _route.DirectClipMotionTaken += Clips.Count;
                        _route.DirectClipMotionKept += kept.Count;
                    }
                    else
                    {
                        _route.DirectClipBodiesTaken += Clips.Count;
                        _route.DirectClipBodiesKept += kept.Count;
                    }

                    grouped = kept.Count > 0;
                    if (grouped) _route.IssueClipDraws(commands, kept, data.variant.Replace("-noclip", ""), state, shadowMap, viewport,
                        inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame);
                }

                Clips.Clear();
                Bodies.Clear();
                if (PreparesColour)
                {
                    if (grouped) _route.PrepareGroupEvents(commands, firstBlock);
                    // The pass's draws, for the colour pass to record as prepared draws -- not a compute barrier event's
                    // block, which had its own event here already (icS2, 2026-10-10: issued again as a draw, it was
                    // refused as unprepared and the refusal halted the compaction).
                    for (int i = firstBlock; i < _route._blocks.Count; i++) if (!_route._blocks[i].barrier) _passes.colourBlocks.Add(_route._blocks[i]);
                    // Mark Recorded now so the ring and camera sentinel protect both callbacks' shared block.
                    if (!grouped) _route._drawData.RecordPrepared(commands, Plugin.RenderEventFunction, Plugin.PrepareEventId);
                }
                else _route.IssuePendingEvents(commands, firstBlock, grouped);
            }
        }

        // Colour preparation ran before opaques. This pass only records prepared native draws into the
        // existing attachments; no compute dispatch, AccessBuffer or texture transition occurs here.
        /// <summary>
        /// Experiment (2026-10-10): the first pass of the route in a frame that compacts. For every compaction set the
        /// frame will use (the view's of each run registered, and the motion selections'), one plugin zero event a
        /// column: the count slots are zeroed before any compaction of the frame, in the GPU's order (the plugin's own
        /// barriers), and never depend on a draw having happened -- a frame with no draw, a camera held, a refused
        /// event or a set coming back after frames of rest leave no count behind (TL review, 2026-10-10). The pass
        /// records no compute of its own after its events.
        /// </summary>
        private sealed class ZeroPass : ScriptableRenderPass
        {
            private readonly VpNativeDrawRoute _route;
            private readonly CameraPasses _passes;
            private readonly List<VpIndexedIndirectDrawBatch.Compaction> _sets = new List<VpIndexedIndirectDrawBatch.Compaction>(4);
            private readonly List<int> _setCommands = new List<int>(4);   // the commands in use of each set's batch (the diagnosis's fill)
            private sealed class Data { public ZeroPass pass; }

            public ZeroPass(VpNativeDrawRoute route, CameraPasses passes)
            {
                _route = route; _passes = passes;
                renderPassEvent = RenderPassEvent.BeforeRendering;
                profilingSampler = new ProfilingSampler("VP Native Zero Counts");
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(camera.camera, _passes.camera)) return;
                using (var builder = graph.AddUnsafePass<Data>(passName, out var data, profilingSampler, file: RenderGraphFile))
                {
                    data.pass = this;
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (Data value, UnsafeGraphContext context) => value.pass.Execute(CommandBufferHelpers.GetNativeCommandBuffer(context.cmd)));
                }
            }

            private void AddSet(VpIndexedIndirectDrawBatch.Compaction set, int commands)
            {
                if (set != null && !_sets.Contains(set)) { _sets.Add(set); _setCommands.Add(commands); }
            }

            // Direct3D 12: a set with the columns of it this frame compacts (bit 0 the body's, bit 1 the casters').
            private void AddColumn(VpIndexedIndirectDrawBatch.Compaction set, int columnBit)
            {
                int at = _sets.IndexOf(set);
                if (at < 0) { _sets.Add(set); _setCommands.Add(columnBit); }
                else _setCommands[at] |= columnBit;
            }

            // Direct3D 12 (DESIGN 4.5.11): the count slots of every column this frame compacts, zeroed by a copy on
            // the GPU in this pass -- Unity's own command, no event of the plugin -- before the passes that compact.
            // It does not depend on a draw: a frame with no draw, a camera held, or a set coming back after frames of
            // rest leaves no count behind, and a column not zeroed here is not compacted in this frame (its runs draw
            // fixed, counted and said).
            private void ExecuteCopies(CommandBuffer commands)
            {
                long began = Began();
                _sets.Clear(); _setCommands.Clear();
                foreach (Issue issue in _passes.bodies) if (!issue.clipList) AddColumn(issue.batch.CompactionOf(issue.view, _passes.frame), 1);
                if (!VpCommandCompaction.CastersFixed)
                {
                    foreach (Issue issue in _passes.casterRuns) if (!issue.clipList) AddColumn(issue.batch.CompactionOf(issue.view, _passes.frame), 2);
                }

                int copies = 0;
                for (int s = 0; s < _sets.Count; s++)
                {
                    for (int column = 0; column < 2; column++)
                    {
                        if ((_setCommands[s] & (1 << column)) == 0) continue;
                        _route._zeroCounts ??= VpCommandCompaction.NewZeroSource();
                        VpCommandCompaction.ZeroCounts(commands, _route._zeroCounts, _sets[s].Counts(column));
                        _sets[s].MarkZeroReserved(column, _passes.frame);
                        copies++;
                    }
                }

                _route.IndirectCountCopies += copies;
#if ZANTETSU_OBSERVE
                VpCommandCompaction.NoteRecordTime(_passes.frame, System.Diagnostics.Stopwatch.GetTimestamp() - began);
#endif
            }

            private void Execute(CommandBuffer commands)
            {
                if (_route._disposed || !_route.CompactsCommands) return;
                if (VpCommandCompaction.ZeroesByCopy)
                {
                    ExecuteCopies(commands);
                    return;
                }

                long began = Began();
                if (VpCommandCompaction.SnapshotEnabled) VpCommandCompaction.PumpSnapshots(_passes.frame);
                _route.DropUnrecorded();
                _sets.Clear(); _setCommands.Clear();
                foreach (Issue issue in _passes.bodies) if (!issue.clipList) AddSet(issue.batch.CompactionOf(issue.view, _passes.frame), issue.batch.CommandCount);
                foreach (Issue issue in _passes.casterRuns) if (!issue.clipList) AddSet(issue.batch.CompactionOf(issue.view, _passes.frame), issue.batch.CommandCount);
                foreach (Issue issue in _passes.motionBodies) if (!issue.clipList) AddSet(issue.batch.CompactionOf(issue.view, _passes.frame), issue.batch.CommandCount);
                _passes.motion.ForEachMotionSelection((batch, output) => AddSet(batch.CompactionOf(output, _passes.frame), batch.CommandCount));
                int recorded = 0, fills = 0;
                for (int s = 0; s < _sets.Count; s++)
                {
                    VpIndexedIndirectDrawBatch.Compaction set = _sets[s];
                    for (int column = 0; column < 3; column++)
                    {
                        // Diagnosis (VpCommandCompaction.PoisonEntries): the used range of the column's compacted
                        // entries filled with zeroes by the same event, before the frame's compactions. A fill that
                        // cannot be reserved is only not made (the diagnosis is the weaker for that frame, counted).
                        if (VpCommandCompaction.PoisonEntries && _setCommands[s] > 0)
                        {
                            GraphicsBuffer entries = set.Entries(column);
                            if (_route._drawData.TryBeginZero(_route._pointers.Buffer(entries), 0, (uint)(_setCommands[s] * entries.stride)))
                            {
                                IntPtr fill = _route._drawData.Finish(out uint fillSerial);
                                _route._blocks.Add(new Block { data = fill, frame = _passes.frame, serial = fillSerial, zero = true });
                                recorded++; fills++;
                            }
                            else _route.RingFull++;
                        }

                        GraphicsBuffer counts = set.Counts(column);
                        if (!_route._drawData.TryBeginZero(_route._pointers.Buffer(counts), 0, (uint)(VpCommandCompaction.Slots * sizeof(uint))))
                        {
                            _route.RingFull++;
                            _route.LastFailure = "every event data block is still with the plugin (zero event)";
                            continue;
                        }

                        IntPtr block = _route._drawData.Finish(out uint serial);
                        _route._blocks.Add(new Block { data = block, frame = _passes.frame, serial = serial, zero = true });
                        set.MarkZeroReserved(column, _passes.frame);
                        recorded++;
                    }
                }

                if (recorded > 0)
                {
                    _route._drawData.RecordPrepared(block => commands.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.ZeroEventId, block));
                    _route.IndirectCountZeroEvents += recorded - fills;
                    VpCommandCompaction.ZeroEvents += recorded - fills;
                    VpCommandCompaction.EntryFills += fills;
                }

#if ZANTETSU_OBSERVE
                VpCommandCompaction.NoteRecordTime(_passes.frame, System.Diagnostics.Stopwatch.GetTimestamp() - began);
#endif
            }
        }

        private sealed class ColourDrawPass : ScriptableRenderPass
        {
            private readonly VpNativeDrawRoute _route;
            private readonly CameraPasses _passes;
            private sealed class Data { public ColourDrawPass pass; }
            public ColourDrawPass(VpNativeDrawRoute route, CameraPasses passes)
            {
                _route = route; _passes = passes;
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
                profilingSampler = new ProfilingSampler("VP Native Colour Draw");
            }
            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                if (!ReferenceEquals(camera.camera, _passes.camera)) return;
                var resources = frameData.Get<UniversalResourceData>();
                if (!resources.activeColorTexture.IsValid() || !resources.activeDepthTexture.IsValid()) return;
                using (var builder = graph.AddRasterRenderPass<Data>(passName, out var data, profilingSampler, file: RenderGraphFile))
                {
                    data.pass = this;
                    builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                    builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                    if (resources.mainShadowsTexture.IsValid()) builder.UseTexture(resources.mainShadowsTexture, AccessFlags.Read);
                    builder.AllowGlobalStateModification(true);
                    builder.AllowPassCulling(false);
                    builder.SetRenderFunc(static (Data value, RasterGraphContext context) => value.pass.Execute(context));
                }
            }
            private void Execute(RasterGraphContext context)
            {
                if (_route._disposed) return;
                PrewarmRequest request = _passes.prewarm;
                if (request != null && request.failure == null && request.colourPrepared && !request.colourSubmitted)
                {
                    _route.RecordPrewarm(null, request, request.colourEntries, _passes.frame, false,
                        block => context.cmd.IssuePluginEventAndData(Plugin.RenderEventFunction, Plugin.WarmPipelineEventId, block));
                    request.colourSubmitted = true;
                    request.colourEntries.Clear();
                }
                foreach (var block in _passes.colourBlocks)
                {
                    if (block.frame != _passes.frame) continue;
                    context.cmd.IssuePluginEventAndData(Plugin.RenderEventFunction,
                        VpNativeDrawData.ModeOf(block.data) == Plugin.DrawMode.Group ? Plugin.GroupPreparedEventId : Plugin.PreparedDrawEventId, block.data);
                }
                _passes.colourBlocks.Clear();
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
                bool warm = _passes.prewarm != null && _passes.prewarm.failure == null && !_passes.prewarm.shadowSubmitted;
                if (!ReferenceEquals(cameraData.camera, _passes.camera) || (_passes.casterRuns.Count == 0 && _passes.clipCasters.Count == 0 && !warm))
                {
                    return;
                }

                string skip = Prepare(frameData, renderGraph, out TextureHandle shadowMap, out int splits, out Vector4 lightDirection, out int width, out int height);
                if (skip != null)
                {
                    _route.CasterRunsNotDrawn += _passes.casterRuns.Count;
                    _route.LastCasterSkipReason = skip;
                    if (warm) _passes.prewarm.failure = "native prewarm shadow: " + skip;
                    _passes.casterRuns.Clear();
                    return;
                }

                _route.LastSplitCount = splits;
                using (IUnsafeRenderGraphBuilder builder = renderGraph.AddUnsafePass(passName, out CasterPassData data, profilingSampler, file: RenderGraphFile))
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
                PrewarmRequest request = _passes.prewarm;
                if (request != null && request.failure == null && !request.shadowSubmitted)
                {
                    var entries = new List<PipelineEntry>(4);
                    foreach (Material material in new[] { request.shadow, request.provisionalShadow })
                    {
                        if (material == null) continue;
                        var warmState = new VpNativeRenderState { colourFormat = GraphicsFormat.None, depthFormat = shadowMap.depthStencilFormat,
                            sampleCount = 1, cullMode = CullOf(material), frontCounterClockwise = _route.FrontCounterClockwise ?? flipped,
                            depthFunc = SystemInfo.usesReversedZBuffer ? Plugin.Compare.GreaterEqual : Plugin.Compare.LessEqual,
                            depthWrite = true, colourWrite = false, depthBias = _route.CasterDepthBiasSigned,
                            slopeScaledDepthBias = _route.CasterSlopeScaledDepthBiasSigned };
                        PipelineEntry ordinary = _route.PrewarmPipeline(request, material,
                            Plugin.VulkanNoClipShadowShaders ? CasterVariant + "-noclip" : CasterVariant, warmState);
                        PipelineEntry clip = _route.PrewarmPipeline(request, material, "caster-direct", warmState);
                        if (ordinary != null && !entries.Contains(ordinary)) entries.Add(ordinary);
                        if (clip != null && !entries.Contains(clip)) entries.Add(clip);
                    }
                    var warmInputs = new GlobalsInputs { matrixV = _views[0], projection = GL.GetGPUProjectionMatrix(_projections[0], true),
                        instanceMultiplier = 1, shadowBias = _biases[0], lightDirection = data.lightDirection };
                    warmInputs.matrixVP = warmInputs.projection * warmInputs.matrixV;
                    _route.PrewarmCompute(commands, request, entries, gpuConstants, warmInputs);
                    _route.RecordPrewarm(commands, request, entries, _passes.frame, true);
                    request.shadowSubmitted = true;
                }

                _gpuConstantsUsed = 0;
                _route._copiedGpuBlocks.Clear();
                int firstBlock = _route._blocks.Count;
                float sliceSelection = VpGpuCullSetup.ShadowSliceSelection ? 1f : 0f;
                bool compacts = _route.CompactsCommands;
                if (compacts) _route.BeginCompactions();
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
                        string casterVariant = Plugin.Vulkan ? (Plugin.VulkanNoClipShadowShaders ? CasterVariant + "-noclip" : CasterVariant)
                            : issue.clipList ? ClipListCasterVariant
                            : _route.OrdinaryDrawsHaveNoClip(issue.material) ? CasterVariant + NoClipSuffix : CasterVariant;
                        PipelineEntry entry = _route.PipelineFor(issue.material, casterVariant, state, out string failure);
                        if (entry == null)
                        {
                            _route.PipelineFailures++;
                            _route.LastFailure = failure;
                            continue;
                        }

                        Bound bound;
                        if (issue.clipList)
                        {
                            // A clip list's casters: the list's shadow entries (one logical instance each), the shared
                            // instance and clip buffers, no selection and no command bounds -- drawn into every split.
                            issue.batch.NativeClipDrawBuffers(out _, out GraphicsBuffer listShadowArguments, out GraphicsBuffer listInstances, out GraphicsBuffer listClips);
                            bound = new Bound
                            {
                                arguments = listShadowArguments, instances = listInstances, clips = listClips,
                                indexBuffer = issue.buffers.IndexBuffer, vertexBuffer = issue.buffers.VertexBuffer,
                            };
                            bound.TakeGenerations(issue.batch, issue.buffers);
                            bound.argumentsGeneration = issue.batch.ClipArgumentGeneration;
                        }
                        else
                        {
                            issue.batch.NativeDrawBuffers(
                                issue.view, out _, out _, out GraphicsBuffer shadowArguments, out GraphicsBuffer shadowVisible,
                                out GraphicsBuffer instances, out GraphicsBuffer clips, out GraphicsBuffer cullCommands);
                            bound = new Bound
                            {
                                arguments = shadowArguments, visible = shadowVisible, instances = instances, clips = clips, cullCommands = cullCommands,
                                indexBuffer = issue.buffers.IndexBuffer, vertexBuffer = issue.buffers.VertexBuffer,
                            };
                            bound.TakeGenerations(issue.batch, issue.buffers);
                            // Experiment: the run's caster entries compacted once, drawn by count into every split.
                            if (compacts && !VpCommandCompaction.CastersFixed) _route.CompactRun(commands, issue.batch, issue.batch.CompactionOf(issue.view, _passes.frame),
                                shadowArguments, 1, issue.startCommand, issue.commandCount, _passes.frame, ref bound);
                        }
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
                            if (issue.clipList)
                            {
                                // A clip list's casters in this split: one issue for each run of entries the camera's
                                // judgement keeps here -- an entry that casts into some split, and whose box does not
                                // lie wholly outside this slice (the judgement the selection's casters make in their
                                // vertex stage, asked under the same conditions: the switch on, a slice that is a box).
                                bool slices = sliceSelection > 0.5f && inputs.projection.m33 == 1f;
                                float bias = Mathf.Abs(_biases[split].x) + Mathf.Abs(_biases[split].y);
                                int listEnd = issue.startCommand + issue.commandCount;
                                if (split == 0) _route.ClipCasterEntriesTaken += issue.commandCount;
                                for (int at = issue.startCommand; at < listEnd;)
                                {
                                    if (issue.selection != null && !issue.selection.CasterKeptInSlice(at, inputs.matrixVP, bias, slices))
                                    {
                                        at++;
                                        continue;
                                    }

                                    int run = 1;
                                    while (at + run < listEnd && (issue.selection == null || issue.selection.CasterKeptInSlice(at + run, inputs.matrixVP, bias, slices)))
                                    {
                                        run++;
                                    }

                                    if (split < VpCullConditions.SplitCapacity) _route._clipCasterEntriesKept[split] += run;
                                    if (_route.IssueOne(commands, entry, issue.material, bound, at, run, _viewports[split], inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame, constantContext: split))
                                    {
                                        _route.ClipCasterIssues++;
                                    }

                                    at += run;
                                }

                                continue;
                            }

                            if (_route.IssueOne(commands, entry, issue.material, bound, issue.startCommand, issue.commandCount, _viewports[split], inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame, constantContext: split))
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

                // The direct clip casters: for each split, only the records the camera keeps there. With none kept in
                // any split the pass ends as one with no clipped instance does.
                bool grouped = false;
                if (_passes.clipCasters.Count > 0)
                {
                    _route.DirectClipCastersTaken += _passes.clipCasters.Count;
                    // Clip casters retain their two-sided rasterizer setting. Share each split's prepared frame blocks.
                    for (int split = 0; split < data.splitCount; split++)
                    {
                        var inputs = new GlobalsInputs { matrixV = _views[split], projection = GL.GetGPUProjectionMatrix(_projections[split], true),
                            instanceMultiplier = 1, shadowBias = _biases[split], lightDirection = data.lightDirection };
                        inputs.matrixVP = inputs.projection * inputs.matrixV;
                        // IssueClipDraws selects the rasterizer cull mode for each material packet.
                        var state = new VpNativeRenderState { colourFormat = GraphicsFormat.None, depthFormat = shadowMap.depthStencilFormat,
                            sampleCount = 1, cullMode = CullOf(_passes.clipCasters[0].material), frontCounterClockwise = _route.FrontCounterClockwise ?? flipped,
                            depthFunc = SystemInfo.usesReversedZBuffer ? Plugin.Compare.GreaterEqual : Plugin.Compare.LessEqual,
                            depthWrite = true, colourWrite = false, depthBias = _route.CasterDepthBiasSigned,
                            slopeScaledDepthBias = _route.CasterSlopeScaledDepthBiasSigned };
                        List<ClipIssue> kept = _route.KeptClipCasters(_passes.clipCasters, inputs.matrixVP,
                            Mathf.Abs(_biases[split].x) + Mathf.Abs(_biases[split].y), sliceSelection > 0.5f && inputs.projection.m33 == 1f);
                        if (split < VpCullConditions.SplitCapacity) _route._directClipCastersKept[split] += kept.Count;
                        if (kept.Count == 0) continue;
                        grouped = true;
                        state.cullMode = CullOf(kept[0].material);
                        _route.CastersIssued += _route.IssueClipDraws(commands, kept, CasterVariant, state, null,
                            _viewports[split], inputs, gpuConstants, ref _gpuConstantsUsed, _passes.frame, split);
                    }
                }
                _passes.clipCasters.Clear();
                _passes.casterRuns.Clear();
                _route.IssuePendingEvents(commands, firstBlock, grouped);
            }

            public static Plugin.CullMode CullOf(Material material)
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
