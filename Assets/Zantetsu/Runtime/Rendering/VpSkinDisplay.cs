#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>A registered individual of the skin draw: a stable number and the generation it was registered with.</summary>
    public readonly struct VpSkinHandle : IEquatable<VpSkinHandle>
    {
        public readonly int id;
        public readonly int generation;

        public VpSkinHandle(int id, int generation)
        {
            this.id = id;
            this.generation = generation;
        }

        public bool IsSet => generation > 0;

        public bool Equals(VpSkinHandle other) => id == other.id && generation == other.generation;

        public override bool Equals(object obj) => obj is VpSkinHandle other && Equals(other);

        public override int GetHashCode() => (id * 397) ^ generation;

        public override string ToString() => "skin " + id + "." + generation;
    }

    /// <summary>
    /// The skin draw (DESIGN 4.5.12): the uncut skinned individuals of registered models, drawn by the indirect VP draw
    /// of VP Stage 3C with 24-byte vertices skinned in the vertex stage -- the Direct3D 12 and Vulkan plugins' routes
    /// through <see cref="IVpNativeSkinDrawSink"/>, or Unity's own calls with the skin variants -- instead of each
    /// individual's SkinnedMeshRenderer.
    /// <para>
    /// **What it holds.** The models' 24-byte geometry, placed once in a <see cref="VpGpuSkinGeometryBuffers"/> apart
    /// from the 16-byte copy; one <see cref="VpIndexedIndirectDrawBatch"/> whose commands are the models (one each, its
    /// instance run the model's slots) and whose selection judges each instance by bounds of its own
    /// (<see cref="VpGpuCullSetup.InstanceBounds"/>); the instance records (<see cref="VpSkinRecord"/>), the palettes
    /// (two a registered individual, current and previous), the previous instance transforms, the models' bone boxes,
    /// and the instance bounds VpSkinBounds.compute writes from them before each frame's first selection.
    /// </para>
    /// <para>
    /// **Each frame (<see cref="UpdateFrame"/>, after the poses are applied).** For each individual: its frame's
    /// transform is read (one matrix); its palette is gathered -- every bone's matrix -- only when its pose version
    /// says the bones changed (or nothing says, or it is new), into the palette slot that is not current; its record
    /// then names that slot as current and the other as previous. A frame whose pose did not change names the current
    /// slot as previous too, so no motion is written from an older pose. Only what changed is written to the GPU: the
    /// records and transforms of the slots touched (one range each), each gathered palette (one write each), and the
    /// commands whose instance count changed.
    /// </para>
    /// <para>
    /// **The worker gather (SKIN-BENCH-3, <see cref="VpSkinGatherMode.Worker"/>).** The bone matrices are read and
    /// multiplied by Burst jobs over two persistent TransformAccessArrays (the frames, the used bones of every
    /// individual), scheduled by <see cref="ScheduleGather"/> at the end of the late update -- after every writer of the
    /// frame's poses (the pose players and the level of detail in the update, the hit's forced pose and the cut's
    /// withdrawal early in the late update) -- and completed in <see cref="UpdateFrame"/>, before anything is uploaded.
    /// The result is published for an individual only when its pose version and its frame's matrix are still what the
    /// job read; a pose written after the schedule (a path not in the table above) is gathered again on the main thread
    /// (<see cref="LateRegathers"/>). Nothing of an older frame is ever shown for an unfinished job: the completion is
    /// waited for. The arrays are touched (an individual added or removed, the display disposed) only after the job in
    /// flight is complete, and a job's result names the individual (number and generation) it was gathered for.
    /// </para>
    /// <para>
    /// **History (AppSW).** The previous transform and palette are those of the last application frame this display
    /// was updated in. Registration, <see cref="ResetHistory"/> (a slot reused, a teleport the owner knows of), a jump
    /// of more than <see cref="TeleportMetres"/> between two updates, and a pose version that says the bones stood
    /// still over a gap (the owner's <c>stoppedOutOfView</c> answer) make the previous equal to the current for that
    /// frame: object motion alone, no stale bone motion. The GPU's in-flight protection is Unity's: a buffer written
    /// through SetData is ordered after the commands submitted before it, and a replaced buffer is given up through
    /// the sinks that drew it (<see cref="GiveUpBuffer"/>).
    /// </para>
    /// <para>
    /// **Removal.** An individual removed (its cut's display has begun, its slot dormant) leaves its slot in the same
    /// update: the model's last slot takes its place, the command's count drops, and nothing of it is drawn in any pass
    /// of the frames after.
    /// </para>
    /// </summary>
    public sealed unsafe class VpSkinDisplay : IDisposable, IVpGpuCullTarget
    {
        private static readonly ProfilerMarker PaletteMarker = new ProfilerMarker("Zantetsu.SkinDraw.Palette");   // the bone matrices gathered on the CPU
        private static readonly ProfilerMarker UploadMarker = new ProfilerMarker("Zantetsu.SkinDraw.Upload");     // the GPU writes of what changed
        private static readonly ProfilerMarker BoundsMarker = new ProfilerMarker("Zantetsu.SkinDraw.Bounds");     // the bounds dispatch recorded (CPU time)
        private static readonly ProfilerMarker CollectMarker = new ProfilerMarker("Zantetsu.SkinDraw.Collect");   // the per-camera registration of the draws
        private static readonly ProfilerMarker FrameMarker = new ProfilerMarker("Zantetsu.SkinDraw.Frame");       // the whole of UpdateFrame
        private static readonly ProfilerMarker GatherMarker = new ProfilerMarker("Zantetsu.SkinDraw.Palette.Gather");   // within Palette: the bone matrices read and multiplied (SKIN-BENCH-2)
        private static readonly ProfilerMarker PaletteUploadMarker = new ProfilerMarker("Zantetsu.SkinDraw.Upload.Palette");   // within Upload: the palette SetData calls

        /// <summary>
        /// DIAGNOSIS (SKIN-BENCH-2 §6): palette writes whose gap is at most this many matrices are joined into one SetData,
        /// the untouched matrices between them sent again (their CPU copy is what the GPU holds). 0: each gathered palette's
        /// own write, neighbours joined, as before. Read before each frame's upload; changes nothing of what is drawn.
        /// </summary>
        public static int PaletteJoinGapMatrices { get; set; }

        /// <summary>The matrices sent beyond the gathered palettes (the joins' untouched ranges), times 64 bytes.</summary>
        public long PaletteBytesExtra { get; private set; }

        private static readonly int SkinRecordsId = Shader.PropertyToID("_VpSkinRecords");
        private static readonly int SkinPaletteId = Shader.PropertyToID("_VpSkinPalette");
        private static readonly int SkinBoneBoxesId = Shader.PropertyToID("_VpSkinBoneBoxes");
        private static readonly int InstanceBoundsOutId = Shader.PropertyToID("_VpInstanceBoundsOut");
        private static readonly int InstanceBoundsId = Shader.PropertyToID("_VpInstanceBounds");
        private static readonly int InstancePreviousId = Shader.PropertyToID("_VpInstancePrevious");
        private static readonly int SkinInstanceCountId = Shader.PropertyToID("_VpSkinInstanceCount");

        /// <summary>The compute shader that writes the instance bounds, under a Resources folder.</summary>
        public const string BoundsShaderResource = "VpSkinBounds";

        /// <summary>A jump of the frame's origin above this, between two updates, resets the individual's motion history.</summary>
        public const float TeleportMetres = 2f;

        private sealed class Model
        {
            public VpSkinModel model;
            public Material material;
            public Bounds localBounds;
            public VpGeometryRange range;
            public int boneBoxStart;
            public int slotStart, slotCapacity, count;
            public int paletteStart, paletteCapacity;   // entries: slotCapacity pairs of BoneCount
            public readonly Stack<int> freePalettePairs = new Stack<int>();
        }

        private sealed class Individual
        {
            public int id, generation;
            public Model model;
            public int slot;                    // the batch's instance slot, or -1 when removed
            public Transform frame;
            public Transform[] bones;
            public Func<int> poseVersion;
            public Func<bool> stoppedOutOfView;
            public int palettePair;             // this individual's pair of palette slots, of its model's region
            public int current;                 // 0 or 1: which of the pair the current palette is in
            public int lastVersion = int.MinValue;
            public bool historyValid;
            public bool everGathered;
            public Matrix4x4 lastMatrix;
            public bool stoppedBefore;

            // The worker gather's entries of this individual (-1 / null: gathered on the main thread alone, a used bone
            // being null at registration), and what its last schedule read.
            public int jobIndex = -1, frameEntry = -1;
            public int[] boneEntries;
            public int scheduledVersion = int.MinValue, scheduledTarget = -1;
        }

        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly VpGpuSkinGeometryBuffers _geometry;
        private readonly VpGpuCullSetup _cull;
        private readonly Material _shadowMaterial;
        private readonly int _instanceCapacity, _modelCapacity, _paletteCapacity, _boneBoxCapacity;
        private GraphicsBuffer _records, _palette, _bounds, _previous, _boneBoxes;
        private readonly ComputeShader _boundsShader;
        private readonly int _boundsKernel;

        // The CPU side of what the GPU holds, written where a change is, sent as ranges. The palette is native: both the
        // main thread's gather and the worker jobs write it, and the GPU is given ranges of it.
        private readonly VpSkinRecord[] _recordsCpu;
        private NativeArray<float4x4> _paletteCpu;
        private readonly Matrix4x4[] _previousCpu;

        // ---- the worker gather (SKIN-BENCH-3) ------------------------------------------------------------------------
        // Persistent: the frames and the used bones of every individual as TransformAccessArray entries, each entry
        // naming its individual's job number (-1: a freed entry, skipped), the per-job inputs and outputs, and the job in
        // flight. Nothing here is made per frame; a freed entry is reused by the next individual.
        private readonly VpSkinGatherMode _gatherAsked;
        private readonly bool _worker;
        private TransformAccessArray _frameAccess, _boneAccess;
        private NativeList<int> _frameJob;
        private NativeList<VpSkinBoneEntry> _boneEntries;
        private NativeArray<VpSkinGatherInput> _jobInputs;
        private NativeArray<float4x4> _jobFrame, _jobInverse;
        private NativeArray<int> _jobFailures;
        private readonly Stack<int> _freeJobs = new Stack<int>();
        private readonly Stack<int> _freeFrameEntries = new Stack<int>();
        private readonly Stack<int> _freeBoneEntries = new Stack<int>();
        private JobHandle _gather;
        private bool _gatherScheduled, _gatherCompleted;
        private int _scheduledFrame = -1;
        private NativeArray<VpIndirectCommand> _commands;
        private NativeArray<int> _startInstances;
        private NativeArray<Matrix4x4> _transforms;
        private NativeArray<VpInstanceClip> _clips;
        private readonly VpChangedRanges _commandRanges = new VpChangedRanges(4, 0);
        private int _instanceFrom = int.MaxValue, _instanceTo = int.MinValue;
        private int _recordFrom = int.MaxValue, _recordTo = int.MinValue;
        private int _previousFrom = int.MaxValue, _previousTo = int.MinValue;
        private readonly List<(int start, int count)> _paletteWrites = new List<(int, int)>(32);
        private int _instanceEnd;
        private int _boneBoxesPlaced;
        private bool _boneBoxesWritten;

        private readonly List<Model> _models = new List<Model>(2);
        private readonly List<Individual> _individuals = new List<Individual>(64);
        private readonly Individual[] _bySlot;
        private int _nextId = 1;
        private int _generation;

        // The cameras drawn for, each with a view of the batch's selection.
        private readonly Camera[] _cameras;
        private readonly int[] _cameraFrames;
        private int _boundsFrame = -1;
        private int _updatedFrame = -1;
        private bool _uploaded;
        private bool _disposed;
        private bool _singlePassInstanced;

        private readonly VpSkinDrawBuffers _drawBuffers = new VpSkinDrawBuffers();
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private readonly List<IVpNativeDrawSink> _sinksUsed = new List<IVpNativeDrawSink>(2);
        private readonly Action<GraphicsBuffer> _giveUpBuffer;

        // ---- observation ---------------------------------------------------------------------------------------------
        public int ModelCount => _models.Count;
        public int IndividualCount => _individuals.Count;
        public int InstanceCapacity => _instanceCapacity;
        // The room and its use, and the refusals at the door (SKIN-BENCH-3, TL review 3): what a walk's record needs to say
        // whether every model and individual was carried, and why not.
        public int ModelCapacity => _modelCapacity;
        public int BoneBoxCapacity => _boneBoxCapacity;
        public int BoneBoxesPlaced => _boneBoxesPlaced;
        public int InstancesPlaced => _instanceEnd;
        public long ModelRegistrations { get; private set; }
        public long ModelRefusals { get; private set; }
        public long IndividualRefusals { get; private set; }
        public string LastModelRefusal { get; private set; }
        public long PalettesGathered { get; private set; }
        public long PaletteBonesGathered { get; private set; }
        public long PaletteWrites { get; private set; }
        public long PaletteBytesWritten { get; private set; }
        public long RecordWrites { get; private set; }
        public long TransformWrites { get; private set; }
        public long HistoryResets { get; private set; }
        public long BoundsDispatches { get; private set; }
        public long SelectionsIssued { get; private set; }
        public long SelectionsRefused { get; private set; }
        public long BodiesRegistered { get; private set; }
        public long CastersRegistered { get; private set; }
        public long FramesUpdated { get; private set; }
        public long UploadRefusals { get; private set; }
        public string LastFailure { get; private set; }
        public double LastPaletteSeconds { get; private set; }
        public double LastUploadSeconds { get; private set; }

        // ---- the worker gather's observation (SKIN-BENCH-3) --------------------------------------------------------------
        /// <summary>The gathering asked of this display, and what it does: "sync", "worker", "worker-from-n", or "worker-fallback: &lt;why&gt;".</summary>
        public VpSkinGatherMode GatherModeAsked => _gatherAsked;
        /// <summary>Whether the worker gather is in use (its arrays made, <see cref="ScheduleGather"/> wanted every late update) -- the state, not the display string (TL review 2, P2).</summary>
        public bool WorkerActive => _worker;
        /// <summary>The TransformAccessArray lengths (every entry, freed ones included) and the freed entries, for the walk's record (TL review 2).</summary>
        public int FrameEntryCount => _worker && _frameJob.IsCreated ? _frameJob.Length : 0;
        public int BoneEntryCount => _worker && _boneEntries.IsCreated ? _boneEntries.Length : 0;
        public int FreeBoneEntryCount => _freeBoneEntries.Count;
        /// <summary>The last schedule's dirty individuals and their bones (the worker mode), or the last update's gathered individuals and bones (the sync mode).</summary>
        public int LastDirtyIndividuals { get; private set; }
        public int LastDirtyBones { get; private set; }
        /// <summary>The most individuals registered at once, and the most bone entries the arrays ever held.</summary>
        public int PeakIndividuals { get; private set; }
        public int PeakBoneEntries { get; private set; }
        public string GatherModeActual => _worker ? (WorkerFrom > 0 ? "worker-from-" + WorkerFrom : "worker") : _gatherAsked == VpSkinGatherMode.Worker ? "worker-fallback: " + (WorkerFallbackReason ?? "?") : "sync";
        /// <summary>Why the worker gather asked for is not in use (null when it is, or was not asked).</summary>
        public string WorkerFallbackReason { get; private set; }
        public long WorkerFallbacks { get; private set; }
        /// <summary>Frames in which the jobs were scheduled (dirty individuals existed); of them, those scheduled from <see cref="UpdateFrame"/> itself, completed at once.</summary>
        public long JobsScheduled { get; private set; }
        public long LateSchedules { get; private set; }
        /// <summary>Individuals gathered again on the main thread because their pose version or frame changed after the schedule.</summary>
        public long LateRegathers { get; private set; }
        /// <summary>Individuals whose job found a bone gone or a matrix not finite (they keep their last pose).</summary>
        public long JobFailures { get; private set; }
        /// <summary>Individuals the jobs cannot gather (a used bone null at registration), gathered on the main thread under the worker mode.</summary>
        public long SyncOnlyIndividuals { get; private set; }
        /// <summary>Jobs completed by a registration, a removal or a disposal that needed the arrays before the frame's update.</summary>
        public long CompletedEarly { get; private set; }
        /// <summary>Jobs of a frame nobody updated (no camera drew it): completed and dropped by the next frame's schedule.</summary>
        public long UncollectedJobs { get; private set; }
        /// <summary>Frames with dirty individuals but fewer than <see cref="WorkerFrom"/>: gathered on the main thread under the worker mode.</summary>
        public long BelowThresholdFrames { get; private set; }
        /// <summary>The dirty-count threshold in force (<see cref="VpSkinGatherSetup.WorkerFrom"/> at the display's making).</summary>
        public int WorkerFrom { get; }
        /// <summary>The worker gather's spans (Editor and Development only): the inputs prepared and the jobs scheduled (at the schedule), the wait in Complete, the verification and publication (in UpdateFrame).</summary>
        public double LastPrepareSeconds { get; private set; }
        public double LastScheduleSeconds { get; private set; }
        public double LastCompleteWaitSeconds { get; private set; }
        public double LastPublishSeconds { get; private set; }
        /// <summary>Whether the frame of the last <see cref="UpdateFrame"/> had jobs scheduled, and how many individuals they gathered.</summary>
        public bool LastFrameUsedJobs { get; private set; }
        public int LastJobIndividuals { get; private set; }
        /// <summary>Within the last frame's palette pass: the bone matrices read and multiplied (every gathered individual summed); within the upload: the palette SetData calls (SKIN-BENCH-2 section 5).</summary>
        public double LastGatherSeconds { get; private set; }
        public double LastPaletteUploadSeconds { get; private set; }
        /// <summary>The last frame's whole update (UpdateFrame), seconds (Editor and Development only).</summary>
        public double LastFrameSeconds { get; private set; }
        /// <summary>The frame <see cref="UpdateFrame"/> last ran in (the Last* spans are its).</summary>
        public int LastUpdatedFrame => _updatedFrame;
        public int LastChangedIndividuals { get; private set; }

        /// <summary>The batch (its selection, its bounds), for the native route and the tests.</summary>
        public VpIndexedIndirectDrawBatch Batch => _batch;

        public VpGpuSkinGeometryBuffers Geometry => _geometry;

        public bool CullsOnGpu => _batch.CullsOnGpu;

        public bool NativeArguments => _batch.NativeArguments;

        public bool IsDisposed => _disposed;

        /// <summary>The buffers an issue of the plugin's route binds; current for this frame after <see cref="UpdateFrame"/>.</summary>
        public VpSkinDrawBuffers DrawBuffers => _drawBuffers;

        private static long Began()
        {
#if ZANTETSU_OBSERVE
            return System.Diagnostics.Stopwatch.GetTimestamp();
#else
            return 0;
#endif
        }

        private VpSkinDisplay(VpGpuCullSetup culling, Material shadowMaterial, int instanceCapacity, int modelCapacity, int paletteCapacity, int boneBoxCapacity,
            VpGpuSkinGeometryBuffers geometry, ComputeShader boundsShader, int boundsKernel)
        {
            _cull = culling;
            _shadowMaterial = shadowMaterial;
            _instanceCapacity = instanceCapacity;
            _modelCapacity = modelCapacity;
            _paletteCapacity = paletteCapacity;
            _boneBoxCapacity = boneBoxCapacity;
            _geometry = geometry;
            _boundsShader = boundsShader;
            _boundsKernel = boundsKernel;
            _giveUpBuffer = GiveUpBuffer;
            _batch = new VpIndexedIndirectDrawBatch(modelCapacity, instanceCapacity, null, culling);
            try
            {
                _records = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, VpSkinRecord.Stride, "VP Skin Records");
                _palette = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, paletteCapacity, 64, "VP Skin Palette");
                _bounds = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, VpInstanceBounds.Stride, "VP Skin Instance Bounds");
                _previous = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, 64, "VP Skin Previous Transforms");
                _boneBoxes = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, boneBoxCapacity, VpSkinBoneBox.Stride, "VP Skin Bone Boxes");
                if (_records == null || _palette == null || _bounds == null || _previous == null || _boneBoxes == null)
                {
                    throw new InvalidOperationException("the skin draw's buffers were withheld (" + VpGpuStop.Argument + ")");
                }

                _batch.InstanceBounds = _bounds;
                _recordsCpu = new VpSkinRecord[instanceCapacity];
                _paletteCpu = new NativeArray<float4x4>(paletteCapacity, Allocator.Persistent);
                _previousCpu = new Matrix4x4[instanceCapacity];
                _gatherAsked = VpSkinGatherSetup.Mode;
                WorkerFrom = VpSkinGatherSetup.WorkerFrom;
                if (_gatherAsked == VpSkinGatherMode.Worker)
                {
                    try
                    {
                        _frameAccess = new TransformAccessArray(instanceCapacity, -1);
                        _boneAccess = new TransformAccessArray(Math.Max(64, instanceCapacity * 32), -1);
                        _frameJob = new NativeList<int>(instanceCapacity, Allocator.Persistent);
                        _boneEntries = new NativeList<VpSkinBoneEntry>(Math.Max(64, instanceCapacity * 32), Allocator.Persistent);
                        _jobInputs = new NativeArray<VpSkinGatherInput>(instanceCapacity, Allocator.Persistent);
                        _jobFrame = new NativeArray<float4x4>(instanceCapacity, Allocator.Persistent);
                        _jobInverse = new NativeArray<float4x4>(instanceCapacity, Allocator.Persistent);
                        _jobFailures = new NativeArray<int>(instanceCapacity, Allocator.Persistent);
                        for (int job = instanceCapacity - 1; job >= 0; job--) _freeJobs.Push(job);
                        _worker = true;
                    }
                    catch (Exception exception)
                    {
                        // The worker gather asked for cannot be made: the main thread gathers, and the reason is kept.
                        WorkerFallbackReason = exception.Message;
                        WorkerFallbacks++;
                        _worker = false;
                        DisposeWorker();
                    }
                }
                _commands = new NativeArray<VpIndirectCommand>(modelCapacity, Allocator.Persistent);
                _startInstances = new NativeArray<int>(modelCapacity, Allocator.Persistent);
                _transforms = new NativeArray<Matrix4x4>(instanceCapacity, Allocator.Persistent);
                _clips = new NativeArray<VpInstanceClip>(instanceCapacity, Allocator.Persistent);
                _bySlot = new Individual[instanceCapacity];
                _cameras = new Camera[culling.ViewCapacity];
                _cameraFrames = new int[culling.ViewCapacity];
                // Every record and bounds slot stands as empty on the GPU before any selection reads it.
                _records.SetData(_recordsCpu, "VpSkinDisplay.cs:records-whole", VpUploadKind.Initial);
                var empty = new VpInstanceBounds[instanceCapacity];
                for (int i = 0; i < empty.Length; i++) empty[i] = new VpInstanceBounds { extents = new Vector3(-1f, -1f, -1f) };
                _bounds.SetData(empty, "VpSkinDisplay.cs:bounds-whole", VpUploadKind.Initial);
                _previous.SetData(_previousCpu, "VpSkinDisplay.cs:previous-whole", VpUploadKind.Initial);
                _drawBuffers.Records = _records;
                _drawBuffers.Palette = _palette;
                _drawBuffers.InstanceBounds = _bounds;
                _drawBuffers.InstancePrevious = _previous;
                _drawBuffers.VertexBuffer = _geometry.VertexBuffer;
                _drawBuffers.IndexBuffer = _geometry.IndexBuffer;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Makes the display: a selection by instance bounds with <paramref name="nativeArguments"/> as the world's
        /// ordinary display has, the 24-byte geometry buffers, the compute shader of the bounds. False with the reason
        /// when a device or a build lacks what it needs; nothing is made then.
        /// </summary>
        public static bool TryCreate(int viewCapacity, bool nativeArguments, Material shadowMaterial, int instanceCapacity, int modelCapacity, int paletteCapacity,
            int boneBoxCapacity, int gpuVertexCapacity, int gpuIndexCapacity, int gpuVertexLimit, int gpuIndexLimit, out VpSkinDisplay display, out string failure)
        {
            display = null;
            failure = null;
            if (instanceCapacity <= 0 || modelCapacity <= 0 || paletteCapacity <= 0 || boneBoxCapacity <= 0)
            {
                failure = "the skin draw's capacities are positive";
                return false;
            }

            if (!VpGpuCullSetup.TryCreate(viewCapacity, nativeArguments, true, out VpGpuCullSetup culling, out failure))
            {
                return false;
            }

            ComputeShader bounds = Resources.Load<ComputeShader>(BoundsShaderResource);
            if (bounds == null || !bounds.HasKernel("SkinInstanceBounds"))
            {
                failure = "the compute shader '" + BoundsShaderResource + "' with its kernel SkinInstanceBounds is not in the build";
                return false;
            }

            VpGpuSkinGeometryBuffers geometry;
            try
            {
                geometry = new VpGpuSkinGeometryBuffers(gpuVertexCapacity, gpuIndexCapacity, gpuVertexLimit, gpuIndexLimit);
            }
            catch (Exception exception)
            {
                failure = "the skin draw's geometry buffers could not be made: " + exception.Message;
                return false;
            }

            try
            {
                display = new VpSkinDisplay(culling, shadowMaterial, instanceCapacity, modelCapacity, paletteCapacity, boneBoxCapacity, geometry, bounds, bounds.FindKernel("SkinInstanceBounds"));
                return true;
            }
            catch (Exception exception)
            {
                geometry.Dispose();
                failure = "the skin draw could not be made: " + exception.Message;
                return false;
            }
        }

        // ---- models ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Registers a model: its geometry is placed, its bone boxes are written, and <paramref name="individualCapacity"/>
        /// instance slots and palette pairs are set aside for its individuals. <paramref name="localBounds"/> is the
        /// conservative box of every pose, in the individuals' frame (the renderer's), for Unity's culling of the
        /// casters and the batch's world bounds; the GPU selection judges by the instance's own bounds instead.
        /// False with the reason when the capacities are short or the geometry cannot be placed.
        /// </summary>
        public bool TryRegisterModel(VpSkinModel model, Material material, Bounds localBounds, int individualCapacity, out int modelId, out string failure)
        {
            bool registered = RegisterModel(model, material, localBounds, individualCapacity, out modelId, out failure);
            if (registered) ModelRegistrations++;
            else
            {
                ModelRefusals++;
                LastModelRefusal = failure;
            }

            return registered;
        }

        private bool RegisterModel(VpSkinModel model, Material material, Bounds localBounds, int individualCapacity, out int modelId, out string failure)
        {
            ThrowIfDisposed();
            modelId = -1;
            failure = null;
            if (model == null || model.IsDisposed || material == null || individualCapacity <= 0)
            {
                failure = "a live model, a material and a positive individual capacity are needed";
                return false;
            }

            for (int i = 0; i < _models.Count; i++)
            {
                if (ReferenceEquals(_models[i].model, model))
                {
                    failure = "the model is registered already";
                    return false;
                }
            }

            if (_models.Count >= _modelCapacity)
            {
                failure = "the skin draw holds " + _modelCapacity + " models already";
                return false;
            }

            if (_instanceEnd + individualCapacity > _instanceCapacity)
            {
                failure = "the skin draw's instance capacity (" + _instanceCapacity + ") has no room for " + individualCapacity + " more individuals";
                return false;
            }

            int paletteNeeded = individualCapacity * 2 * model.BoneCount;
            int paletteStart = 0;
            for (int i = 0; i < _models.Count; i++) paletteStart += _models[i].paletteCapacity;
            if (paletteStart + paletteNeeded > _paletteCapacity)
            {
                failure = "the skin draw's palette capacity (" + _paletteCapacity + " matrices) has no room for " + paletteNeeded + " more";
                return false;
            }

            if (_boneBoxesPlaced + model.BoneCount > _boneBoxCapacity)
            {
                failure = "the skin draw's bone box capacity (" + _boneBoxCapacity + ") has no room for " + model.BoneCount + " more";
                return false;
            }

            // The bone boxes first, into the room after the placed ones: should the upload fail, nothing is placed and
            // nothing held, and the next registration writes that room again (review 2, item 2).
            var boxes = new NativeArray<VpSkinBoneBox>(model.BoneCount, Allocator.Temp);
            try
            {
                NativeArray<VpSkinBoneBox>.ReadOnly source = model.BoneBoxes;
                for (int b = 0; b < model.BoneCount; b++) boxes[b] = source[b];
                _boneBoxes.SetData(boxes, 0, _boneBoxesPlaced, model.BoneCount, "VpSkinDisplay.cs:bone-boxes", VpUploadKind.Initial);
            }
            finally
            {
                boxes.Dispose();
            }

            if (!_geometry.TryPlace(model, out VpGeometryRange range, out failure))
            {
                return false;
            }

            // A replaced geometry buffer: the issues bind the current one from now on; the old one is given up. The
            // generations go with the buffers (DESIGN 4.5.8): a write through Unity's API may change the native
            // buffer, so the route takes its pointer again after every placement (review 1, P1).
            _geometry.ReleaseRetired(_giveUpBuffer);
            SyncGeometryBuffers();

            var entry = new Model
            {
                model = model, material = material, localBounds = localBounds, range = range, boneBoxStart = _boneBoxesPlaced,
                slotStart = _instanceEnd, slotCapacity = individualCapacity, count = 0, paletteStart = paletteStart, paletteCapacity = paletteNeeded,
            };
            for (int pair = individualCapacity - 1; pair >= 0; pair--) entry.freePalettePairs.Push(pair);
            // The model is read again whenever the geometry grows (every placed model is written into the new
            // buffers): held by this display until it ends, whatever its owner does meanwhile (review 1, item 2). Held
            // after the last thing that can refuse, so that no refused registration keeps a hold (review 2, item 2).
            model.Hold();
            _boneBoxesPlaced += model.BoneCount;
            _instanceEnd += individualCapacity;
            modelId = _models.Count;
            _models.Add(entry);
            _commands[modelId] = new VpIndirectCommand(range, localBounds, 0);
            _startInstances[modelId] = entry.slotStart;
            _commandRanges.Add(modelId, modelId + 1);
            return true;
        }

        /// <summary>The model registered as <paramref name="modelId"/>.</summary>
        public VpSkinModel ModelOf(int modelId) => _models[modelId].model;

        /// <summary>The registration of <paramref name="model"/>, or -1.</summary>
        public int ModelIdOf(VpSkinModel model)
        {
            for (int i = 0; i < _models.Count; i++) if (ReferenceEquals(_models[i].model, model)) return i;
            return -1;
        }

        // The geometry's buffers and their CPU-write generations, as the issues bind them.
        private void SyncGeometryBuffers()
        {
            _drawBuffers.VertexBuffer = _geometry.VertexBuffer;
            _drawBuffers.IndexBuffer = _geometry.IndexBuffer;
            _drawBuffers.VertexGeneration = _geometry.VertexGeneration;
            _drawBuffers.IndexGeneration = _geometry.IndexGeneration;
        }

        /// <summary>How many individuals of the model are registered now.</summary>
        public int IndividualCountOf(int modelId) => _models[modelId].count;

        /// <summary>
        /// DIAGNOSIS, a synchronous readback (never in a measured frame): how many individuals the GPU selection of
        /// <paramref name="camera"/>'s view kept last, summed over the registered models' forward arguments. False when
        /// the camera has no view here or the selection does not run on the GPU.
        /// </summary>
        public bool TryReadVisibleInstances(Camera camera, out int visible)
        {
            visible = 0;
            ThrowIfDisposed();
            int view = ViewOf(camera, false);
            if (view < 0 || !_batch.CullsOnGpu || _models.Count == 0) return false;
            // The entries as the selection writes them: Unity's five words (instanceCount second), or the plugin's six
            // (a leading word, then the five: instanceCount third).
            int stride = NativeArguments ? VpGpuCullSetup.NativeArgumentStride : VpGpuCullSetup.UnityArgumentStride;
            int words = stride / sizeof(uint);
            var raw = new uint[_models.Count * words];
            _batch.CullForwardArguments(view).GetData(raw, 0, 0, raw.Length);
            int at = NativeArguments ? 2 : 1;
            for (int c = 0; c < _models.Count; c++) visible += (int)raw[c * words + at];
            // Single Pass Instanced doubles the instance counts (one instance an eye): the individuals are half.
            if (_batch.SinglePassInstanced) visible /= 2;
            return true;
        }

        // ---- individuals -------------------------------------------------------------------------------------------

        /// <summary>
        /// Registers an individual of model <paramref name="modelId"/>: <paramref name="frame"/> is the renderer's
        /// Transform (rigid, unit scale: the frame the palette is relative to), <paramref name="bones"/> the renderer's
        /// bone list in the model's palette order, <paramref name="poseVersion"/> a number that changes whenever the
        /// bones were written (null: gathered every frame), <paramref name="stoppedOutOfView"/> whether the bones are
        /// left standing by the level of detail (null: never). It is drawn from the next <see cref="UpdateFrame"/>,
        /// with its history reset. False when the model's slots are all taken.
        /// </summary>
        public bool TryAdd(int modelId, Transform frame, Transform[] bones, Func<int> poseVersion, Func<bool> stoppedOutOfView, out VpSkinHandle handle)
        {
            ThrowIfDisposed();
            handle = default;
            if (modelId < 0 || modelId >= _models.Count || frame == null || bones == null) { IndividualRefusals++; return false; }
            Model model = _models[modelId];
            if (bones.Length < model.model.BoneCount || model.count >= model.slotCapacity || model.freePalettePairs.Count == 0) { IndividualRefusals++; return false; }
            CompleteGather();   // the arrays are touched after the job in flight (SKIN-BENCH-3)
            int slot = model.slotStart + model.count;
            var individual = new Individual
            {
                id = _nextId++, generation = ++_generation, model = model, slot = slot, frame = frame, bones = bones, poseVersion = poseVersion,
                stoppedOutOfView = stoppedOutOfView, palettePair = model.freePalettePairs.Pop(), current = 0, historyValid = false,
            };
            // The unused bones' identity, once, in both halves of the pair: the jobs never write them, the main thread's
            // gather writes them again (the same value).
            int boneCount = model.model.BoneCount;
            int pairStart = model.paletteStart + individual.palettePair * 2 * boneCount;
            for (int b = 0; b < boneCount; b++)
            {
                if (model.model.IsBoneUsed(b)) continue;
                _paletteCpu[pairStart + b] = float4x4.identity;
                _paletteCpu[pairStart + boneCount + b] = float4x4.identity;
            }

            if (_worker) AddJobEntries(individual);
            model.count++;
            _individuals.Add(individual);
            if (_individuals.Count > PeakIndividuals) PeakIndividuals = _individuals.Count;
            if (BoneEntryCount > PeakBoneEntries) PeakBoneEntries = BoneEntryCount;
            _bySlot[slot] = individual;
            _commands[modelId] = new VpIndirectCommand(model.range, model.localBounds, model.count);
            _commandRanges.Add(modelId, modelId + 1);
            // Its record stands inactive until its first update gathers its palette and writes the record active.
            _recordsCpu[slot] = default;
            TouchRecord(slot);
            handle = new VpSkinHandle(individual.id, individual.generation);
            return true;
        }

        /// <summary>Whether <paramref name="handle"/> names an individual registered now.</summary>
        public bool IsLive(VpSkinHandle handle) => Find(handle) != null;

        /// <summary>
        /// Removes the individual: it is drawn in no pass of the frames after the next <see cref="UpdateFrame"/> (the
        /// model's last slot takes its place, the command's count drops). Nothing for a handle not registered.
        /// </summary>
        public bool Remove(VpSkinHandle handle)
        {
            ThrowIfDisposed();
            Individual individual = Find(handle);
            if (individual == null) return false;
            CompleteGather();   // a job reading its bones ends before its entries are freed (SKIN-BENCH-3)
            if (_worker) RemoveJobEntries(individual);
            Model model = individual.model;
            int last = model.slotStart + model.count - 1;
            int slot = individual.slot;
            if (slot != last)
            {
                // The last of the model's run takes the freed slot, record and all: its palettes stay where they are.
                Individual moved = _bySlot[last];
                moved.slot = slot;
                _bySlot[slot] = moved;
                _recordsCpu[slot] = _recordsCpu[last];
                _transforms[slot] = _transforms[last];
                _previousCpu[slot] = _previousCpu[last];
                TouchRecord(slot);
                TouchTransform(slot);
                TouchPrevious(slot);
            }

            _bySlot[last] = null;
            _recordsCpu[last] = default;   // inactive: empty bounds, kept by no selection
            TouchRecord(last);
            model.count--;
            model.freePalettePairs.Push(individual.palettePair);
            individual.slot = -1;
            _individuals.Remove(individual);
            int modelId = _models.IndexOf(model);
            _commands[modelId] = new VpIndirectCommand(model.range, model.localBounds, model.count);
            _commandRanges.Add(modelId, modelId + 1);
            return true;
        }

        /// <summary>
        /// The individual's motion history starts again at the next update: the previous transform and palette are the
        /// current ones for that frame (a slot reused for another individual, a teleport the owner made).
        /// </summary>
        public bool ResetHistory(VpSkinHandle handle)
        {
            Individual individual = Find(handle);
            if (individual == null) return false;
            individual.historyValid = false;
            return true;
        }

        /// <summary>
        /// Whether the display is drawing the individual now: registered under this handle, in a slot, and its record
        /// active (its palette gathered at least once; the selection draws active records only). False for an unknown or
        /// removed handle, and before the first update that gathers it. The check's "drawn" under the skin draw
        /// (SKIN-BENCH-3, TL review 3); the native route's counts say what was actually issued.
        /// </summary>
        public bool IsActive(VpSkinHandle handle)
        {
            if (_disposed) return false;
            Individual individual = Find(handle);
            return individual != null && individual.slot >= 0 && individual.slot < _recordsCpu.Length && (_recordsCpu[individual.slot].flags & VpSkinRecord.ActiveFlag) != 0;
        }

        private Individual Find(VpSkinHandle handle)
        {
            if (!handle.IsSet) return null;
            for (int i = 0; i < _individuals.Count; i++)
            {
                Individual individual = _individuals[i];
                if (individual.id == handle.id && individual.generation == handle.generation) return individual;
            }

            return null;
        }

        private void TouchRecord(int slot)
        {
            if (slot < _recordFrom) _recordFrom = slot;
            if (slot + 1 > _recordTo) _recordTo = slot + 1;
        }

        private void TouchTransform(int slot)
        {
            if (slot < _instanceFrom) _instanceFrom = slot;
            if (slot + 1 > _instanceTo) _instanceTo = slot + 1;
        }

        private void TouchPrevious(int slot)
        {
            if (slot < _previousFrom) _previousFrom = slot;
            if (slot + 1 > _previousTo) _previousTo = slot + 1;
        }

        // ---- the frame ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The frame's update, after every pose was applied and before any camera renders (a late update): the
        /// transforms, the changed palettes, the records and the previous transforms are brought current and sent;
        /// the instance bounds are then written on the GPU before the first selection (<see cref="IssueCull"/>).
        /// <paramref name="singlePassInstanced"/> is the frame's stereo condition, as the world's display decides it.
        /// False when the batch refused the upload (counted; nothing of this frame is drawn by it then).
        /// </summary>
        public bool UpdateFrame(bool singlePassInstanced)
        {
            ThrowIfDisposed();
            int frame = Time.frameCount;
            // Once a frame, whoever asks (several drawings of one world ask for their own cameras): a second update
            // in the same frame would make the previous transform and palette the current ones, and the frame's
            // motion zero (review 1, item 6).
            if (_updatedFrame == frame) return _lastUpdateOk;
            using var frameScope = FrameMarker.Auto();
            _singlePassInstanced = singlePassInstanced;
            _paletteWrites.Clear();
            int changed = 0;
            long began = Began();
            long gatherTicks = 0;
            // The worker gather (SKIN-BENCH-3): the jobs scheduled at the end of the late update are completed here,
            // before anything is published or uploaded; a frame nobody scheduled for (no scheduler in the scene, a test)
            // schedules now and completes at once, counted apart.
            bool jobs = false;
            int jobIndividuals = 0;
            LastCompleteWaitSeconds = 0.0;
            if (_worker)
            {
                if (_scheduledFrame != frame && ScheduleGather()) LateSchedules++;
                if (_gatherScheduled && _scheduledFrame == frame)
                {
                    long waitBegan = Began();
                    if (!_gatherCompleted) { _gather.Complete(); _gatherCompleted = true; }
#if ZANTETSU_OBSERVE
                    LastCompleteWaitSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - waitBegan) / (double)System.Diagnostics.Stopwatch.Frequency;
#endif
                    jobs = true;
                }
            }

            int gatheredNow = 0, gatheredBonesNow = 0;
            using (PaletteMarker.Auto())
            {
                for (int i = 0; i < _individuals.Count; i++)
                {
                    Individual individual = _individuals[i];
                    if (individual.frame == null)
                    {
                        continue;   // gone with its object: removed by the owner, drawn as it stood until then
                    }

                    int slot = individual.slot;
                    if (!individual.frame.gameObject.activeInHierarchy)
                    {
                        // Its hierarchy is off (the character root deactivated): drawn in no pass, as its renderer would
                        // not be, and its history starts again when it comes back (review 1, item 5).
                        if ((_recordsCpu[slot].flags & VpSkinRecord.ActiveFlag) != 0)
                        {
                            _recordsCpu[slot].flags &= ~VpSkinRecord.ActiveFlag;
                            TouchRecord(slot);
                            IndividualsHidden++;
                        }

                        individual.historyValid = false;
                        continue;
                    }

                    Matrix4x4 matrix = individual.frame.localToWorldMatrix;
                    Vector3 origin = matrix.GetColumn(3);
                    bool reset = !individual.historyValid;
                    if (!reset)
                    {
                        Vector3 before = individual.lastMatrix.GetColumn(3);
                        if ((origin - before).sqrMagnitude > TeleportMetres * TeleportMetres) reset = true;
                    }

                    bool stopped = individual.stoppedOutOfView != null && individual.stoppedOutOfView();
                    if (!reset && individual.stoppedBefore && !stopped) reset = true;   // back in view: the bones moved from where they stood by an unknown gap
                    individual.stoppedBefore = stopped;
                    int version = individual.poseVersion != null ? individual.poseVersion() : frame;
                    bool gather = !individual.everGathered || version != individual.lastVersion;
                    VpSkinModel model = individual.model.model;
                    int bones = model.BoneCount;
                    int pairStart = individual.model.paletteStart + individual.palettePair * 2 * bones;
                    int currentStart = pairStart + individual.current * bones;
                    int previousStart = currentStart;
                    if (gather)
                    {
                        int other = 1 - individual.current;
                        int target = pairStart + other * bones;
                        bool gathered;
                        // The job's result stands when it was gathered for this individual (number and generation), into
                        // this target, at this pose version, and the frame's matrix is the one the job read; anything
                        // else -- a pose or a root written after the schedule, an individual the jobs cannot read -- is
                        // gathered on the main thread now.
                        bool fromJob = jobs && individual.jobIndex >= 0 && individual.scheduledTarget == target;
                        if (fromJob)
                        {
                            VpSkinGatherInput input = _jobInputs[individual.jobIndex];
                            fromJob = input.gather != 0 && input.id == individual.id && input.generation == individual.generation && input.target == target;
                            if (fromJob && (individual.scheduledVersion != version || !MatricesEqual((Matrix4x4)_jobFrame[individual.jobIndex], matrix)))
                            {
                                fromJob = false;
                                LateRegathers++;
                            }
                        }

                        if (fromJob)
                        {
                            gathered = _jobFailures[individual.jobIndex] == 0;
                            if (!gathered) JobFailures++;
                            jobIndividuals++;
                        }
                        else
                        {
                            long gatherBegan = Began();
                            using (GatherMarker.Auto()) gathered = GatherPalette(individual, matrix, model, target);
#if ZANTETSU_OBSERVE
                            gatherTicks += System.Diagnostics.Stopwatch.GetTimestamp() - gatherBegan;
#endif
                        }

                        if (!gathered)
                        {
                            // A bone missing or a matrix not finite: the individual keeps its last pose; nothing is written.
                            LastFailure = "the palette of " + individual.id + " could not be gathered";
                        }
                        else
                        {
                            previousStart = individual.everGathered && !reset ? currentStart : target;
                            individual.current = other;
                            individual.everGathered = true;
                            individual.lastVersion = version;
                            currentStart = target;
                            _paletteWrites.Add((target, bones));
                            PalettesGathered++;
                            PaletteBonesGathered += bones;
                            gatheredNow++;
                            gatheredBonesNow += bones;
                        }
                    }

                    if (reset) { previousStart = currentStart; HistoryResets++; }
                    var record = new VpSkinRecord
                    {
                        paletteStart = (uint)currentStart, previousPaletteStart = (uint)previousStart, boneCount = (uint)bones,
                        boneBoxStart = (uint)individual.model.boneBoxStart, flags = individual.everGathered ? VpSkinRecord.ActiveFlag : 0u,
                    };
                    if (!RecordsEqual(_recordsCpu[slot], record))
                    {
                        _recordsCpu[slot] = record;
                        TouchRecord(slot);
                    }

                    Matrix4x4 previous = reset ? matrix : individual.lastMatrix;
                    if (!MatricesEqual(_previousCpu[slot], previous))
                    {
                        _previousCpu[slot] = previous;
                        TouchPrevious(slot);
                    }

                    if (!MatricesEqual(_transforms[slot], matrix))
                    {
                        _transforms[slot] = matrix;
                        TouchTransform(slot);
                    }

                    individual.lastMatrix = matrix;
                    individual.historyValid = true;
                    if (gather || reset) changed++;
                }
            }

#if ZANTETSU_OBSERVE
            LastPaletteSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            LastGatherSeconds = gatherTicks / (double)System.Diagnostics.Stopwatch.Frequency;
            // The verification and publication: the loop less the main thread's gathers and the completion's wait.
            LastPublishSeconds = Math.Max(0.0, LastPaletteSeconds - LastGatherSeconds - LastCompleteWaitSeconds);
#endif
            LastChangedIndividuals = changed;
            if (!_worker) { LastDirtyIndividuals = gatheredNow; LastDirtyBones = gatheredBonesNow; }
            LastFrameUsedJobs = jobs;
            LastJobIndividuals = jobIndividuals;
            _gatherScheduled = false;
            _gatherCompleted = false;
            bool ok;
            long uploadBegan = Began();
            using (UploadMarker.Auto())
            {
                ok = Upload();
            }

#if ZANTETSU_OBSERVE
            LastUploadSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - uploadBegan) / (double)System.Diagnostics.Stopwatch.Frequency;
            LastFrameSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
#endif
            _updatedFrame = frame;
            _lastUpdateOk = ok;
            FramesUpdated++;
            return ok;
        }

        private bool _lastUpdateOk;

        /// <summary>How many times an individual's hierarchy was found off and its record made inactive.</summary>
        public long IndividualsHidden { get; private set; }

        private bool GatherPalette(Individual individual, Matrix4x4 frameMatrix, VpSkinModel model, int target)
        {
            Matrix4x4 inverse = frameMatrix.inverse;
            return VpSkinPalette.TryGather(inverse, individual.bones, model, (float4x4*)_paletteCpu.GetUnsafePtr() + target);
        }

        // ---- the worker gather (SKIN-BENCH-3) --------------------------------------------------------------------------

        /// <summary>
        /// Schedules this frame's gather on the workers: for every individual whose pose version changed (or that is new),
        /// the jobs read its frame and its used bones and write its palette into the half that is not current. To be
        /// called at the end of the late update, after every writer of the frame's poses (<see cref="VpSkinGatherLateUpdate"/>);
        /// <see cref="UpdateFrame"/> completes and publishes. False when nothing is to be gathered, the frame was
        /// scheduled or updated already, or the display gathers on the main thread.
        /// </summary>
        public bool ScheduleGather()
        {
            ThrowIfDisposed();
            if (!_worker) return false;
            int frame = Time.frameCount;
            if (_updatedFrame == frame || _scheduledFrame == frame) return false;
            // A job of an earlier frame that no update collected (no camera drew that frame, the drawing disabled, only the
            // scheduler alive): completed and its result dropped before its inputs are written again -- the arrays it reads
            // are about to change (TL review of SKIN-BENCH-3, P2). Its individuals stay dirty and are gathered again now.
            if (_gatherScheduled)
            {
                if (!_gatherCompleted) _gather.Complete();
                _gatherScheduled = false;
                _gatherCompleted = false;
                UncollectedJobs++;
            }

            long began = Began();
            _scheduledFrame = frame;
            int dirty = 0, dirtyBones = 0;
            for (int i = 0; i < _individuals.Count; i++)
            {
                Individual individual = _individuals[i];
                int job = individual.jobIndex;
                if (job < 0) continue;
                var input = default(VpSkinGatherInput);
                _jobFailures[job] = 0;
                individual.scheduledTarget = -1;
                if (individual.frame == null || !individual.frame.gameObject.activeInHierarchy) { _jobInputs[job] = input; continue; }
                int version = individual.poseVersion != null ? individual.poseVersion() : frame;
                if (individual.everGathered && version == individual.lastVersion) { _jobInputs[job] = input; continue; }
                int bones = individual.model.model.BoneCount;
                int target = individual.model.paletteStart + individual.palettePair * 2 * bones + (1 - individual.current) * bones;
                input.gather = 1; input.target = target; input.id = individual.id; input.generation = individual.generation;
                individual.scheduledVersion = version;
                individual.scheduledTarget = target;
                _jobInputs[job] = input;
                dirty++;
                dirtyBones += bones;
            }

            LastDirtyIndividuals = dirty;
            LastDirtyBones = dirtyBones;

#if ZANTETSU_OBSERVE
            LastPrepareSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
#endif
            if (dirty == 0) { _gatherScheduled = false; LastScheduleSeconds = 0.0; return false; }
            // Below the threshold the main thread gathers (UpdateFrame finds no job for the frame and gathers as the sync
            // mode does): the jobs' fixed cost is not worth a few individuals (TL review of SKIN-BENCH-3).
            if (dirty < WorkerFrom) { _gatherScheduled = false; LastScheduleSeconds = 0.0; BelowThresholdFrames++; return false; }
            long scheduleBegan = Began();
            var frames = new VpSkinFrameGatherJob { frameJob = _frameJob.AsArray(), inputs = _jobInputs, frame = _jobFrame, inverse = _jobInverse, failures = _jobFailures };
            JobHandle framesDone = frames.ScheduleReadOnly(_frameAccess, 16);
            var bones2 = new VpSkinBoneGatherJob { entries = _boneEntries.AsArray(), inputs = _jobInputs, inverse = _jobInverse, palette = _paletteCpu, failures = _jobFailures };
            _gather = bones2.ScheduleReadOnly(_boneAccess, 64, framesDone);
            JobHandle.ScheduleBatchedJobs();
            _gatherScheduled = true;
            _gatherCompleted = false;
            JobsScheduled++;
#if ZANTETSU_OBSERVE
            LastScheduleSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - scheduleBegan) / (double)System.Diagnostics.Stopwatch.Frequency;
#endif
            return true;
        }

        /// <summary>Whether jobs scheduled this frame are still to be completed and published by <see cref="UpdateFrame"/>.</summary>
        public bool GatherScheduled => _gatherScheduled && _scheduledFrame == Time.frameCount;

        // The job in flight, completed before anything it reads is touched: the entries, the palette, the display itself.
        private void CompleteGather()
        {
            if (!_gatherScheduled || _gatherCompleted) return;
            _gather.Complete();
            _gatherCompleted = true;
            CompletedEarly++;
        }

        private void AddJobEntries(Individual individual)
        {
            VpSkinModel model = individual.model.model;
            if (individual.frame == null || _freeJobs.Count == 0) { SyncOnlyIndividuals++; return; }
            int used = 0;
            for (int b = 0; b < model.BoneCount; b++)
            {
                if (!model.IsBoneUsed(b)) continue;
                if (individual.bones[b] == null) { SyncOnlyIndividuals++; return; }   // the main thread's gather says what is missing
                used++;
            }

            int job = _freeJobs.Pop();
            individual.jobIndex = job;
            _jobInputs[job] = default;
            individual.frameEntry = TakeFrameEntry(individual.frame, job);
            individual.boneEntries = new int[used];
            int at = 0;
            for (int b = 0; b < model.BoneCount; b++)
            {
                if (!model.IsBoneUsed(b)) continue;
                individual.boneEntries[at++] = TakeBoneEntry(individual.bones[b], new VpSkinBoneEntry { job = job, bone = b, bindPose = model.BindPose(b) });
            }
        }

        private int TakeFrameEntry(Transform frame, int job)
        {
            if (_freeFrameEntries.Count > 0)
            {
                int entry = _freeFrameEntries.Pop();
                _frameAccess[entry] = frame;
                _frameJob[entry] = job;
                return entry;
            }

            _frameAccess.Add(frame);
            _frameJob.Add(job);
            return _frameJob.Length - 1;
        }

        private int TakeBoneEntry(Transform bone, VpSkinBoneEntry entry)
        {
            if (_freeBoneEntries.Count > 0)
            {
                int at = _freeBoneEntries.Pop();
                _boneAccess[at] = bone;
                _boneEntries[at] = entry;
                return at;
            }

            _boneAccess.Add(bone);
            _boneEntries.Add(entry);
            return _boneEntries.Length - 1;
        }

        private void RemoveJobEntries(Individual individual)
        {
            if (individual.jobIndex < 0) return;
            // The entries stay in the arrays, naming no job (-1): skipped by the jobs, taken by the next individual.
            if (individual.frameEntry >= 0) { _frameJob[individual.frameEntry] = -1; _freeFrameEntries.Push(individual.frameEntry); }
            if (individual.boneEntries != null)
            {
                for (int i = 0; i < individual.boneEntries.Length; i++)
                {
                    int at = individual.boneEntries[i];
                    VpSkinBoneEntry entry = _boneEntries[at];
                    entry.job = -1;
                    _boneEntries[at] = entry;
                    _freeBoneEntries.Push(at);
                }
            }

            _jobInputs[individual.jobIndex] = default;
            _freeJobs.Push(individual.jobIndex);
            individual.jobIndex = -1;
            individual.frameEntry = -1;
            individual.boneEntries = null;
        }

        private void DisposeWorker()
        {
            if (_frameAccess.isCreated) _frameAccess.Dispose();
            if (_boneAccess.isCreated) _boneAccess.Dispose();
            if (_frameJob.IsCreated) _frameJob.Dispose();
            if (_boneEntries.IsCreated) _boneEntries.Dispose();
            if (_jobInputs.IsCreated) _jobInputs.Dispose();
            if (_jobFrame.IsCreated) _jobFrame.Dispose();
            if (_jobInverse.IsCreated) _jobInverse.Dispose();
            if (_jobFailures.IsCreated) _jobFailures.Dispose();
            _freeJobs.Clear(); _freeFrameEntries.Clear(); _freeBoneEntries.Clear();
        }

        private static bool RecordsEqual(in VpSkinRecord a, in VpSkinRecord b)
            => a.paletteStart == b.paletteStart && a.previousPaletteStart == b.previousPaletteStart && a.boneCount == b.boneCount
               && a.boneBoxStart == b.boneBoxStart && a.flags == b.flags;

        private static bool MatricesEqual(in Matrix4x4 a, in Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private bool Upload()
        {
            bool ok = true;
            if (_recordTo > _recordFrom)
            {
                _records.SetData(_recordsCpu, _recordFrom, _recordFrom, _recordTo - _recordFrom, "VpSkinDisplay.cs:records");
                _drawBuffers.RecordsGeneration++;
                RecordWrites++;
            }

            // Each gathered palette as one write; neighbours in the buffer are joined, and, under the diagnosis gap, writes
            // within that many matrices of each other too (the untouched matrices between them sent again: their CPU copy
            // is what the GPU holds, so the picture is the same; SKIN-BENCH-2 §6).
            long paletteUploadBegan = Began();
            using (PaletteUploadMarker.Auto())
            {
                int gap = Math.Max(0, PaletteJoinGapMatrices);
                _paletteWrites.Sort((a, b) => a.start.CompareTo(b.start));
                for (int i = 0; i < _paletteWrites.Count;)
                {
                    int start = _paletteWrites[i].start, end = start + _paletteWrites[i].count;
                    long gathered = _paletteWrites[i].count;
                    int j = i + 1;
                    while (j < _paletteWrites.Count && _paletteWrites[j].start <= end + gap)
                    {
                        end = Math.Max(end, _paletteWrites[j].start + _paletteWrites[j].count);
                        gathered += _paletteWrites[j].count;
                        j++;
                    }

                    _palette.SetData(_paletteCpu, start, start, end - start, "VpSkinDisplay.cs:palette");   // the native copy, a range
                    PaletteWrites++;
                    PaletteBytesWritten += (long)(end - start) * 64;
                    PaletteBytesExtra += Math.Max(0L, (end - start) - gathered) * 64;
                    i = j;
                }
            }
#if ZANTETSU_OBSERVE
            LastPaletteUploadSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - paletteUploadBegan) / (double)System.Diagnostics.Stopwatch.Frequency;
#endif

            if (_paletteWrites.Count > 0) _drawBuffers.PaletteGeneration++;
            if (_previousTo > _previousFrom)
            {
                _previous.SetData(_previousCpu, _previousFrom, _previousFrom, _previousTo - _previousFrom, "VpSkinDisplay.cs:previous");
                _drawBuffers.PreviousGeneration++;
            }

            bool commands = !_commandRanges.IsEmpty;
            bool instances = _instanceTo > _instanceFrom;
            if (!_uploaded || commands || instances || _singlePassInstanced != _batch.SinglePassInstanced)
            {
                int from = instances ? _instanceFrom : 0, to = instances ? _instanceTo : 0;
                if (!_batch.TryUploadSlots(_commands, _startInstances, _models.Count, _transforms, _clips, _instanceEnd, _singlePassInstanced, _commandRanges, from, to))
                {
                    UploadRefusals++;
                    LastFailure = "the batch refused the slot upload";
                    ok = false;
                }
                else
                {
                    _uploaded = true;
                    if (instances) TransformWrites++;
                }
            }

            _commandRanges.Clear();
            _instanceFrom = int.MaxValue; _instanceTo = int.MinValue;
            _recordFrom = int.MaxValue; _recordTo = int.MinValue;
            _previousFrom = int.MaxValue; _previousTo = int.MinValue;
            _paletteWrites.Clear();
            return ok;
        }

        // ---- the cameras -------------------------------------------------------------------------------------------

        private int ViewOf(Camera camera, bool take)
        {
            int free = -1;
            for (int v = 0; v < _cameras.Length; v++)
            {
                if (ReferenceEquals(_cameras[v], camera)) return v;
                if (free < 0 && _cameras[v] == null) free = v;
            }

            if (!take || free < 0) return -1;
            _cameras[free] = camera;
            return free;
        }

        /// <summary>Forgets a camera that is no longer drawn for, freeing its view.</summary>
        public void ForgetCamera(Camera camera)
        {
            int view = ViewOf(camera, false);
            if (view >= 0) _cameras[view] = null;
        }

        /// <summary>
        /// Registers this frame's draws for <paramref name="camera"/> with the plugin's route: one body run and one caster
        /// run per material run of the models (DESIGN 4.5.12). The selection is issued later by whoever put this
        /// display in the camera's frame (<see cref="IssueCull"/>). False when the display was not updated this frame,
        /// or has no view left for the camera.
        /// </summary>
        public bool Render(int layer, Camera camera, IVpNativeSkinDrawSink sink)
        {
            ThrowIfDisposed();
            if (sink == null) throw new ArgumentNullException(nameof(sink));
            if (!_uploaded || _updatedFrame != Time.frameCount || _individuals.Count == 0) return false;
            int view = ViewOf(camera, true);
            if (view < 0) return false;
            using var scope = CollectMarker.Auto();
            if (sink is IVpNativeDrawSink drawSink && !_sinksUsed.Contains(drawSink)) _sinksUsed.Add(drawSink);
            _geometry.ReleaseRetired(_giveUpBuffer);
            SyncGeometryBuffers();
            _cameraFrames[view] = Time.frameCount;
            int start = 0;
            while (start < _models.Count)
            {
                Model model = _models[start];
                if (model.count == 0) { start++; continue; }
                int end = start + 1;
                while (end < _models.Count && (_models[end].count == 0 || ReferenceEquals(_models[end].material, model.material))) end++;
                sink.AddSkinBody(camera, _batch, _drawBuffers, model.material, start, end - start, view, layer);
                BodiesRegistered++;
                if (_shadowMaterial != null)
                {
                    sink.AddSkinCasters(camera, _batch, _drawBuffers, _shadowMaterial, start, end - start, view, layer);
                    CastersRegistered++;
                }

                start = end;
            }

            return true;
        }

        /// <summary>
        /// The same draws by Unity's own calls, for a display whose selection writes Unity's argument entries (the
        /// Editor's checks): <paramref name="forward"/> and <paramref name="shadow"/> are materials of the skin variants
        /// (VP_SKIN24, VP_GPU_CULLED and VP_NO_CLIP on). The selection is issued as for the route.
        /// </summary>
        public bool RenderUnity(int layer, Camera camera, Material forward, Material shadow)
        {
            ThrowIfDisposed();
            if (_batch.NativeArguments) throw new InvalidOperationException("this display's batch writes the plugin's argument entries; Unity's calls cannot read them");
            if (!_uploaded || _updatedFrame != Time.frameCount || _individuals.Count == 0 || forward == null) return false;
            int view = ViewOf(camera, true);
            if (view < 0) return false;
            using var scope = CollectMarker.Auto();
            _cameraFrames[view] = Time.frameCount;
            _properties.SetBuffer(SkinRecordsId, _records);
            _properties.SetBuffer(SkinPaletteId, _palette);
            _properties.SetBuffer(InstanceBoundsId, _bounds);
            _properties.SetBuffer(InstancePreviousId, _previous);
            for (int c = 0; c < _models.Count; c++)
            {
                if (_models[c].count == 0) continue;
                _batch.RenderForward(forward, _properties, _geometry.VertexBuffer, _geometry.IndexBuffer, layer, c, 1, camera, view);
                BodiesRegistered++;
                if (shadow != null)
                {
                    _batch.RenderShadows(shadow, _properties, _geometry.VertexBuffer, _geometry.IndexBuffer, layer, c, 1, camera, view);
                    CastersRegistered++;
                }
            }

            return true;
        }

        /// <summary>
        /// Issues the camera's selection: once a frame, first, the bounds of every instance slot are written on the GPU
        /// from the records, the palettes and the bone boxes, and -- where the device needs it -- the sink's compute
        /// barrier is recorded between; then the batch's selection for the camera's view. False when the camera has
        /// no draws registered this frame, or the barrier could not be recorded (no selection is issued then).
        /// </summary>
        public bool IssueCull(CommandBuffer commands, Camera camera, VpCullConditions conditions)
        {
            if (_disposed || commands == null || conditions == null) return false;
            int view = ViewOf(camera, false);
            if (view < 0 || _cameraFrames[view] != Time.frameCount || !_uploaded) return false;
            if (_boundsFrame != Time.frameCount)
            {
                using (BoundsMarker.Auto())
                {
                    if (!VpGpuStop.Dispatch()) return false;
                    commands.SetComputeIntParam(_boundsShader, SkinInstanceCountId, _instanceEnd);
                    commands.SetComputeBufferParam(_boundsShader, _boundsKernel, SkinRecordsId, _records);
                    commands.SetComputeBufferParam(_boundsShader, _boundsKernel, SkinPaletteId, _palette);
                    commands.SetComputeBufferParam(_boundsShader, _boundsKernel, SkinBoneBoxesId, _boneBoxes);
                    commands.SetComputeBufferParam(_boundsShader, _boundsKernel, InstanceBoundsOutId, _bounds);
                    commands.DispatchCompute(_boundsShader, _boundsKernel, Math.Max(1, (_instanceEnd + 63) / 64), 1, 1);
                    BoundsDispatches++;
                    _boundsFrame = Time.frameCount;
                }

                if (_barrierSink != null && !_barrierSink.IssueSelectionBarrier(commands))
                {
                    SelectionsRefused++;
                    return false;
                }
            }

            _batch.IssueCull(commands, view, conditions);
            SelectionsIssued++;
            return true;
        }

        private IVpNativeSkinDrawSink _barrierSink;

        /// <summary>The sink whose compute barrier stands between the bounds and the selection (the plugin's route on Vulkan); null for Unity's calls.</summary>
        public IVpNativeSkinDrawSink BarrierSink
        {
            get => _barrierSink;
            set => _barrierSink = value;
        }

        /// <summary>Marks <paramref name="camera"/> as drawn for in this frame without registering a draw, so that a test may issue its selection (tests only).</summary>
        internal int NoteCameraForTest(Camera camera)
        {
            int view = ViewOf(camera, true);
            if (view >= 0) _cameraFrames[view] = Time.frameCount;
            return view;
        }

        /// <summary>Forgets that the bounds were written this frame, so that a test's next selection writes them again (the Editor's frame count stands still).</summary>
        internal void ForgetBoundsFrameForTest() => _boundsFrame = -1;

        /// <summary>Forgets that the frame was updated, so that a test's next <see cref="UpdateFrame"/> runs (the Editor's frame count stands still).</summary>
        internal void ForgetUpdatedFrameForTest() => _updatedFrame = -1;

        /// <summary>Forgets that the frame was scheduled, so that a test's next <see cref="ScheduleGather"/> schedules (the Editor's frame count stands still).</summary>
        internal void ForgetScheduledFrameForTest() => _scheduledFrame = -1;

        internal GraphicsBuffer PaletteBufferForTest => _palette;
        internal Matrix4x4[] PaletteCpuForTest
        {
            get
            {
                var copy = new Matrix4x4[_paletteCpu.Length];
                for (int i = 0; i < copy.Length; i++) copy[i] = _paletteCpu[i];
                return copy;
            }
        }
        internal int ModelPaletteStartForTest(int modelId) => _models[modelId].paletteStart;
        /// <summary>Whether the worker gather is in use (tests).</summary>
        internal bool WorkerForTest => _worker;

        /// <summary>The bounds of every instance slot, read back (tests and checks only; never the product).</summary>
        public VpInstanceBounds[] ReadInstanceBoundsForTest()
        {
            var result = new VpInstanceBounds[_instanceEnd];
            if (_instanceEnd > 0) _bounds.GetData(result, 0, 0, _instanceEnd);
            return result;
        }

        /// <summary>The record and palette of an individual, as the CPU holds them (tests).</summary>
        public bool TryGetForTest(VpSkinHandle handle, out int slot, out VpSkinRecord record, out Matrix4x4[] palette, out Matrix4x4 previous)
        {
            Individual individual = Find(handle);
            slot = -1; record = default; palette = null; previous = default;
            if (individual == null) return false;
            slot = individual.slot;
            record = _recordsCpu[slot];
            palette = new Matrix4x4[record.boneCount];
            for (int b = 0; b < palette.Length; b++) palette[b] = _paletteCpu[(int)record.paletteStart + b];
            previous = _previousCpu[slot];
            return true;
        }

        public string Describe()
        {
            return "skin draw: models " + _models.Count + ", individuals " + _individuals.Count + " of " + _instanceCapacity + ", frames " + FramesUpdated
                + ", gather " + GatherModeActual + (_worker ? " (jobs " + JobsScheduled + ", late schedules " + LateSchedules + ", late regathers " + LateRegathers + ", job failures " + JobFailures + ", sync-only individuals " + SyncOnlyIndividuals + ", completed early " + CompletedEarly + ", uncollected " + UncollectedJobs + ", below threshold " + BelowThresholdFrames + ", bone entries " + BoneEntryCount + " (free " + FreeBoneEntryCount + ", peak " + PeakBoneEntries + ")" : "")
                + ", peak individuals " + PeakIndividuals
                + ", palettes gathered " + PalettesGathered + " (" + PaletteBonesGathered + " bones), palette writes " + PaletteWrites + " (" + PaletteBytesWritten + " bytes)"
                + ", record writes " + RecordWrites + ", transform writes " + TransformWrites + ", history resets " + HistoryResets
                + ", bounds dispatches " + BoundsDispatches + ", selections issued " + SelectionsIssued + ", refused " + SelectionsRefused
                + ", bodies registered " + BodiesRegistered + ", casters registered " + CastersRegistered + ", upload refusals " + UploadRefusals
                + ", last frame: changed " + LastChangedIndividuals + ", palette " + (LastPaletteSeconds * 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                + " ms, upload " + (LastUploadSeconds * 1000.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture) + " ms; " + _geometry.DescribeRoom()
                + "; room: models " + _models.Count + " of " + _modelCapacity + " (registrations " + ModelRegistrations + ", refusals " + ModelRefusals
                + (LastModelRefusal != null ? ", last: " + LastModelRefusal : "") + "), instance slots " + _instanceEnd + " of " + _instanceCapacity
                + " (individual refusals " + IndividualRefusals + "), bone boxes " + _boneBoxesPlaced + " of " + _boneBoxCapacity
                + (LastFailure != null ? "; last failure: " + LastFailure : "");
        }

        // ---- lifetime ----------------------------------------------------------------------------------------------

        private void GiveUpBuffer(GraphicsBuffer buffer)
        {
            if (buffer == null) return;
            for (int i = _sinksUsed.Count - 1; i >= 0; i--)
            {
                if (_sinksUsed[i].IssuedEventsWait() == null && i < _sinksUsed.Count - 1) _sinksUsed.RemoveAt(i);
            }

            if (_sinksUsed.Count == 0) buffer.Dispose();
            else if (_sinksUsed.Count == 1) _sinksUsed[0].Retire(buffer);
            else VpNativeDrawRelease.Retire("VP skin display buffer (several routes)", _sinksUsed, buffer);
        }

        public void Dispose()
        {
            if (_disposed) return;
            CompleteGather();   // the jobs read the arrays freed below
            _disposed = true;
            DisposeWorker();
            if (_paletteCpu.IsCreated) _paletteCpu.Dispose();
            _batch?.Dispose(_giveUpBuffer);
            _geometry?.Dispose(_giveUpBuffer);
            foreach (Model model in _models) model.model?.Release();
            GiveUpBuffer(_records); _records = null;
            GiveUpBuffer(_palette); _palette = null;
            GiveUpBuffer(_bounds); _bounds = null;
            GiveUpBuffer(_previous); _previous = null;
            GiveUpBuffer(_boneBoxes); _boneBoxes = null;
            if (_commands.IsCreated) _commands.Dispose();
            if (_startInstances.IsCreated) _startInstances.Dispose();
            if (_transforms.IsCreated) _transforms.Dispose();
            if (_clips.IsCreated) _clips.Dispose();
            _individuals.Clear();
            _models.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(VpSkinDisplay));
        }
    }
}
