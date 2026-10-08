using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// VP Stage 3 draw issue: the Stage 2 batch layout (<see cref="VpIndirectDrawBatch"/>) drawn with indexed indirect
    /// calls. It draws next to Unity mesh renderers in the same camera: depth-tested against them, casting URP main light
    /// shadows onto them and receiving theirs.
    /// Per frame it issues two Graphics.RenderPrimitivesIndexedIndirect calls over the same hardware index buffer, vertex
    /// buffer and instance buffer: the forward call uses the forward material, receives shadows and casts none; the
    /// shadow call uses the shadow-caster-only material, casts shadows and draws no colour. Each command is one
    /// GraphicsBuffer.IndirectDrawIndexedArgs entry in each call's argument buffer: indexCountPerInstance and startIndex
    /// are its range's index count and start, baseVertexIndex is 0 because the indices already hold global vertex
    /// numbers, and startInstance is the start of its transforms. Both calls are culled and sorted as a whole by the union
    /// of all instance bounds; nothing is culled per instance.
    /// <para>
    /// Single Pass Instanced stereo is handled as in Stage 2: for a batch uploaded for it, the forward arguments hold
    /// instanceCount and startInstance times 2, and the forward shader reads logical instance physicalId &gt;&gt; 1,
    /// dropping odd physical IDs in a forward pass that renders a single view. The shadow arguments and the instance buffer
    /// always hold the logical instances.
    /// </para>
    /// <para>
    /// Commands and transforms are uploaded at set-up. Rendering a frame writes no buffer and allocates nothing. The batch
    /// owns its two argument buffers and its instance buffer; after <see cref="Dispose"/>, uploading and rendering throw,
    /// and disposing again does nothing.
    /// </para>
    /// <para>
    /// **Selecting on the GPU (VP Stage 3C, DESIGN 4.5.7).** A batch made with a <see cref="VpGpuCullSetup"/> draws only
    /// the instances a compute pass keeps. The CPU then sends no arguments: it sends one <see cref="VpCullCommand"/>
    /// per command, when the commands change, and the transforms and clips exactly as before. For each of its views
    /// the batch owns a list of kept instances and an argument buffer for the body and the same pair for the casters;
    /// <see cref="IssueCull"/> writes them on the GPU for one view from that view's conditions, and the draws of that
    /// view read them. No count is read back: the CPU issues the same commands whatever was kept, and a command that
    /// keeps nothing is drawn with an instance count of zero. Every dispatch writes every command's arguments, so no
    /// draw reads what an earlier dispatch left. Such a batch draws only through the overloads that name a view.
    /// </para>
    /// </summary>
    public sealed class VpIndexedIndirectDrawBatch : IDisposable
    {
        /// <summary>One instance record: a float4x4 object-to-world matrix.</summary>
        public const int InstanceStride = 64;

        /// <summary>
        /// One clip record: <see cref="VpInstanceClip"/>, its eight planes and then the valid
        /// count -- eight float4s and one float, 132 bytes. Fixed whatever the count is: no instance takes a smaller
        /// or a larger record.
        /// </summary>
        public const int InstanceClipStride = 132;

        private static readonly int VerticesId = Shader.PropertyToID("_VpVertices");
        private static readonly int InstanceObjectToWorldId = Shader.PropertyToID("_VpInstanceObjectToWorld");
        private static readonly int InstanceClipId = Shader.PropertyToID("_VpInstanceClip");
        private static readonly int InstanceMultiplierId = Shader.PropertyToID("_VpInstanceMultiplier");
        private static readonly int VisibleId = Shader.PropertyToID("_VpVisible");
        private static readonly int CullCommandsId = Shader.PropertyToID("_VpCullCommands");
        private static readonly int ShadowSliceSelectionId = Shader.PropertyToID("_VpShadowSliceSelection");
        private static readonly int CommandCountId = Shader.PropertyToID("_VpCommandCount");
        private static readonly int ArgumentStrideId = Shader.PropertyToID("_VpArgumentStride");
        private static readonly int ArgumentOffsetId = Shader.PropertyToID("_VpArgumentOffset");
        private static readonly int ForwardMultiplierId = Shader.PropertyToID("_VpForwardMultiplier");
        private static readonly int EyeCountId = Shader.PropertyToID("_VpEyeCount");
        private static readonly int EyePlanesId = Shader.PropertyToID("_VpEyePlanes");
        private static readonly int ShadowSplitCountId = Shader.PropertyToID("_VpShadowSplitCount");
        private static readonly int ShadowPlaneCountsId = Shader.PropertyToID("_VpShadowPlaneCounts");
        private static readonly int ShadowPlanesId = Shader.PropertyToID("_VpShadowPlanes");
        private static readonly int ForwardVisibleId = Shader.PropertyToID("_VpForwardVisible");
        private static readonly int ForwardArgumentsId = Shader.PropertyToID("_VpForwardArguments");
        private static readonly int ShadowVisibleId = Shader.PropertyToID("_VpShadowVisible");
        private static readonly int ShadowArgumentsId = Shader.PropertyToID("_VpShadowArguments");

        // The arguments are made here from the commands, so they are staged before they are sent: in managed arrays, or
        // -- for a batch made with a page backing -- in rooms on reserved address space, made and written at
        // construction. The clips are staged only for an array upload (which may omit them); a native upload sends the
        // caller's own.
        private GraphicsBuffer.IndirectDrawIndexedArgs[] _forwardArguments;
        private GraphicsBuffer.IndirectDrawIndexedArgs[] _shadowArguments;
        private readonly VpNumericRoom<GraphicsBuffer.IndirectDrawIndexedArgs> _forwardStaging;
        private readonly VpNumericRoom<GraphicsBuffer.IndirectDrawIndexedArgs> _shadowStaging;
        private readonly GraphicsBuffer _forwardArgumentBuffer;
        private readonly GraphicsBuffer _shadowArgumentBuffer;
        private readonly GraphicsBuffer _instanceBuffer;
        private readonly GraphicsBuffer _instanceClipBuffer;
        private VpInstanceClip[] _instanceClips;

        // Selecting on the GPU: the commands as the selection reads them take the arguments' place -- staged as the
        // arguments are, sent when they would be -- and each view holds what the selection writes for it. The two
        // argument buffers above and their staging are not made for such a batch.
        private readonly VpGpuCullSetup _cull;
        private VpCullCommand[] _cullCommands;
        private readonly VpNumericRoom<VpCullCommand> _cullStaging;
        private readonly GraphicsBuffer _cullCommandBuffer;
        private readonly CullView[] _cullViews;
        private bool _uploaded;
        private bool _disposed;

        // What the selection writes for one view: for the body and for the casters, the kept instances' numbers (each
        // command's in the slots of its own instance run) and the indexed arguments that draw them.
        private struct CullView
        {
            public GraphicsBuffer forwardVisible;
            public GraphicsBuffer forwardArguments;
            public GraphicsBuffer shadowVisible;
            public GraphicsBuffer shadowArguments;

            public void Dispose()
            {
                forwardVisible?.Dispose();
                forwardArguments?.Dispose();
                shadowVisible?.Dispose();
                shadowArguments?.Dispose();
            }
        }

        public VpIndexedIndirectDrawBatch(int commandCapacity, int instanceCapacity)
            : this(commandCapacity, instanceCapacity, null)
        {
        }

        /// <summary>
        /// The same batch with its staging on <paramref name="stagingBacking"/> (reserved and committed whole here, every
        /// page written), for an owner that uploads native views; null keeps the staging in managed arrays.
        /// </summary>
        /// <exception cref="InvalidOperationException">The backing refused the staging's room.</exception>
        public VpIndexedIndirectDrawBatch(int commandCapacity, int instanceCapacity, IVpPageBacking stagingBacking)
            : this(commandCapacity, instanceCapacity, stagingBacking, null)
        {
        }

        /// <summary>
        /// The same batch selecting its instances on the GPU when <paramref name="culling"/> is given (see the class
        /// notes): the commands' records and every view's lists and arguments are made here, with the other buffers,
        /// and the two CPU-written argument buffers are not. Null is the batch as it always was.
        /// </summary>
        /// <exception cref="InvalidOperationException">The backing refused the staging's room.</exception>
        public VpIndexedIndirectDrawBatch(
            int commandCapacity, int instanceCapacity, IVpPageBacking stagingBacking, VpGpuCullSetup culling)
        {
            if (commandCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCapacity), commandCapacity, "Must be positive.");
            }

            if (instanceCapacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(instanceCapacity), instanceCapacity, "Must be positive.");
            }

            VpNumericRoom<GraphicsBuffer.IndirectDrawIndexedArgs> forwardStaging = null;
            VpNumericRoom<GraphicsBuffer.IndirectDrawIndexedArgs> shadowStaging = null;
            VpNumericRoom<VpCullCommand> cullStaging = null;
            string failure = null;
            if (stagingBacking == null)
            {
                if (culling == null)
                {
                    _forwardArguments = new GraphicsBuffer.IndirectDrawIndexedArgs[commandCapacity];
                    _shadowArguments = new GraphicsBuffer.IndirectDrawIndexedArgs[commandCapacity];
                }
                else
                {
                    _cullCommands = new VpCullCommand[commandCapacity];
                }

                _instanceClips = new VpInstanceClip[instanceCapacity];
            }
            else if (culling == null
                         ? !VpNumericRoom<GraphicsBuffer.IndirectDrawIndexedArgs>.TryCreateNative(
                               stagingBacking, commandCapacity, commandCapacity, out forwardStaging, out failure)
                           || !VpNumericRoom<GraphicsBuffer.IndirectDrawIndexedArgs>.TryCreateNative(
                               stagingBacking, commandCapacity, commandCapacity, out shadowStaging, out failure)
                         : !VpNumericRoom<VpCullCommand>.TryCreateNative(
                               stagingBacking, commandCapacity, commandCapacity, out cullStaging, out failure))
            {
                forwardStaging?.Dispose();
                throw new InvalidOperationException("the batch's staging could not be made: " + failure);
            }

            // Each buffer is held in a local the moment it exists and named afterwards, so a failure anywhere in
            // here can release every buffer that was already made. The fields are set only once all of them stand.
            GraphicsBuffer forwardArgumentBuffer = null;
            GraphicsBuffer shadowArgumentBuffer = null;
            GraphicsBuffer instanceBuffer = null;
            GraphicsBuffer instanceClipBuffer = null;
            GraphicsBuffer cullCommandBuffer = null;
            CullView[] cullViews = null;
            try
            {
                if (culling == null)
                {
                    forwardArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    forwardArgumentBuffer.name = "VP Indexed Indirect Forward Arguments";
                    shadowArgumentBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size);
                    shadowArgumentBuffer.name = "VP Indexed Indirect Shadow Arguments";
                }
                else
                {
                    cullCommandBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, commandCapacity, VpCullCommand.Stride);
                    cullCommandBuffer.name = "VP Cull Commands";
                    cullViews = new CullView[culling.ViewCapacity];
                    for (int v = 0; v < cullViews.Length; v++)
                    {
                        // The arguments are plain indirect-argument buffers, as VP Stage 3's are: on D3D11 the compute
                        // pass writes such a buffer through a RWByteAddressBuffer as the draw's shader reads it through a
                        // ByteAddressBuffer (UnityIndirect.cginc). Asking for Raw or Structured as well is refused or
                        // unneeded there (probed 2026-10-06: perf/gpu-cull/probe-args).
                        // An entry is Unity's five arguments, or -- for a batch the Direct3D 12 plugin draws -- the
                        // command's number and then the five (DESIGN 4.5.8): the stride is the setup's.
                        cullViews[v].forwardVisible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, sizeof(uint));
                        cullViews[v].forwardVisible.name = "VP Cull Forward Visible " + v;
                        cullViews[v].forwardArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, culling.ArgumentStride);
                        cullViews[v].forwardArguments.name = "VP Cull Forward Arguments " + v;
                        cullViews[v].shadowVisible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, sizeof(uint));
                        cullViews[v].shadowVisible.name = "VP Cull Shadow Visible " + v;
                        cullViews[v].shadowArguments = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, culling.ArgumentStride);
                        cullViews[v].shadowArguments.name = "VP Cull Shadow Arguments " + v;
                    }
                }

                instanceBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceStride);
                instanceBuffer.name = "VP Indexed Instance Transforms";
                instanceClipBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceClipStride);
                instanceClipBuffer.name = "VP Indexed Instance Clips";
            }
            catch
            {
                forwardArgumentBuffer?.Dispose();
                shadowArgumentBuffer?.Dispose();
                instanceBuffer?.Dispose();
                instanceClipBuffer?.Dispose();
                cullCommandBuffer?.Dispose();
                if (cullViews != null)
                {
                    for (int v = 0; v < cullViews.Length; v++)
                    {
                        cullViews[v].Dispose();
                    }
                }

                forwardStaging?.Dispose();
                shadowStaging?.Dispose();
                cullStaging?.Dispose();
                throw;
            }

            _forwardStaging = forwardStaging;
            _shadowStaging = shadowStaging;
            _forwardArgumentBuffer = forwardArgumentBuffer;
            _shadowArgumentBuffer = shadowArgumentBuffer;
            _instanceBuffer = instanceBuffer;
            _instanceClipBuffer = instanceClipBuffer;
            _cull = culling;
            _cullStaging = cullStaging;
            _cullCommandBuffer = cullCommandBuffer;
            _cullViews = cullViews;
            CommandCapacity = commandCapacity;
            InstanceCapacity = instanceCapacity;
        }

        /// <summary>Whether this batch draws only the instances a compute pass keeps (see the class notes).</summary>
        public bool CullsOnGpu => _cull != null;

        /// <summary>How many views this batch keeps a selection for; zero for a batch that does not select.</summary>
        public int CullViewCapacity => _cull != null ? _cull.ViewCapacity : 0;

        /// <summary>Observation: how many selections were issued for this batch's views. They are GPU work, not transfers.</summary>
        public long CullDispatches { get; private set; }

        public int CommandCapacity { get; }

        public int InstanceCapacity { get; }

        /// <summary>
        /// A buffer of this batch for an owner that replaces it to read back asynchronously: once that readback has
        /// completed, the GPU is past every draw that was issued from this batch before it was asked for.
        /// </summary>
        public GraphicsBuffer RetirementFence => _instanceBuffer;

        public int CommandCount { get; private set; }

        /// <summary>The number of logical instances, one per uploaded transform.</summary>
        public int InstanceCount { get; private set; }

        /// <summary>Whether the forward arguments hold physical instances for Single Pass Instanced stereo.</summary>
        public bool SinglePassInstanced { get; private set; }

        /// <summary>The union of the world bounds of every uploaded instance: the culling and sorting bounds of both calls.</summary>
        public Bounds WorldBounds { get; private set; }

        /// <summary>The forward call's argument buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer ForwardArgumentBuffer
        {
            get
            {
                ThrowIfDisposed();
                ThrowIfCulled();
                return _forwardArgumentBuffer;
            }
        }

        /// <summary>The shadow call's argument buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer ShadowArgumentBuffer
        {
            get
            {
                ThrowIfDisposed();
                ThrowIfCulled();
                return _shadowArgumentBuffer;
            }
        }

        /// <summary>
        /// What the selection wrote for a view, for reading back inside this assembly and by tests: never read by the
        /// product, which issues its draws without knowing any count. Not to be written.
        /// </summary>
        internal GraphicsBuffer CullForwardArguments(int view) => View(view).forwardArguments;
        internal GraphicsBuffer CullForwardVisible(int view) => View(view).forwardVisible;
        internal GraphicsBuffer CullShadowArguments(int view) => View(view).shadowArguments;
        internal GraphicsBuffer CullShadowVisible(int view) => View(view).shadowVisible;

        /// <summary>
        /// What the Direct3D 12 plugin's draw binds for a view (DESIGN 4.5.8): the selection's entries and lists for
        /// the body and the casters, and the shared instance, clip and command buffers. For a batch whose setup
        /// writes the plugin's entries (<see cref="VpGpuCullSetup.NativeArguments"/>); throws for any other.
        /// Not to be written through.
        /// </summary>
        public void NativeDrawBuffers(
            int view, out GraphicsBuffer forwardArguments, out GraphicsBuffer forwardVisible, out GraphicsBuffer shadowArguments, out GraphicsBuffer shadowVisible,
            out GraphicsBuffer instances, out GraphicsBuffer instanceClips, out GraphicsBuffer cullCommands)
        {
            CullView target = View(view);
            if (!_cull.NativeArguments)
            {
                throw new InvalidOperationException("this batch writes Unity's argument entries; the plugin cannot draw from them");
            }

            forwardArguments = target.forwardArguments;
            forwardVisible = target.forwardVisible;
            shadowArguments = target.shadowArguments;
            shadowVisible = target.shadowVisible;
            instances = _instanceBuffer;
            instanceClips = _instanceClipBuffer;
            cullCommands = _cullCommandBuffer;
        }

        /// <summary>Whether this batch's selection writes the plugin's argument entries (see <see cref="NativeDrawBuffers"/>).</summary>
        public bool NativeArguments => _cull != null && _cull.NativeArguments;

        /// <summary>
        /// The CPU-write generations of the buffers the plugin's draws bind (DESIGN 4.5.8): raised at every SetData
        /// into the instance transforms, the clips, the commands, and any view's lists or arguments. The plugin's
        /// route keeps a native pointer only while the generation it was taken at stands (Unity may change the native
        /// buffer when its data is written through its APIs). A write by the selection's compute pass raises nothing.
        /// </summary>
        public uint InstanceGeneration { get; private set; }

        public uint ClipGeneration { get; private set; }

        public uint CullCommandGeneration { get; private set; }

        public uint CullViewGeneration { get; private set; }
        internal GraphicsBuffer CullCommandBuffer
        {
            get
            {
                ThrowIfDisposed();
                ThrowIfNotCulled();
                return _cullCommandBuffer;
            }
        }

        /// <summary>The instance transform buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer InstanceBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _instanceBuffer;
            }
        }

        /// <summary>The instance clip buffer, for reading back inside this assembly. Not to be written.</summary>
        internal GraphicsBuffer InstanceClipBuffer
        {
            get
            {
                ThrowIfDisposed();
                return _instanceClipBuffer;
            }
        }

        /// <summary>
        /// Replaces the commands and instance transforms as <see cref="VpIndirectDrawBatch.TryUpload(VpIndirectCommand[], Matrix4x4[], bool)"/>
        /// does, writing indexed arguments. Returns false, changing no count, bounds, buffer or Single Pass Instanced mode,
        /// when there are more commands than the command capacity, an instance count or range value is negative, the
        /// instances exceed the instance capacity, or the transform count is not the sum of the instance counts.
        /// </summary>
        public bool TryUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, bool singlePassInstanced)
        {
            return TryUpload(commands, objectToWorlds, null, singlePassInstanced);
        }

        /// <summary>
        /// Whether <see cref="TryUpload(VpIndirectCommand[], Matrix4x4[], VpInstanceClip[], bool)"/> would accept
        /// this input, decided without writing anything. It is the very judgement the upload makes — both call
        /// <c>Accepts</c> — so a caller that has to settle two batches before writing either can ask first and know
        /// the answer will not change. Null arrays are refused here rather than thrown, because asking is not
        /// uploading.
        /// </summary>
        public bool CanUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips)
        {
            ThrowIfDisposed();
            return commands != null && objectToWorlds != null
                && Accepts(commands, commands.Length, objectToWorlds, clips, true, out _);
        }

        /// <summary>
        /// Whether <see cref="TryUpload(VpIndirectCommand[], int, Matrix4x4[], VpInstanceClip[], bool)"/> would accept
        /// the first <paramref name="commandCount"/> commands and the instances they name, decided without writing
        /// anything and by the same judgement that upload makes.
        /// </summary>
        public bool CanUpload(
            VpIndirectCommand[] commands, int commandCount, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips)
        {
            ThrowIfDisposed();
            return commands != null && objectToWorlds != null
                && Accepts(commands, commandCount, objectToWorlds, clips, false, out _);
        }

        // Every ordinary condition of an upload, in one place: the command count, each command's own numbers, the
        // instance total against the capacity, and one transform -- and one clip record, when clips are given -- per
        // instance. Only the first commandCount commands are read. The whole-array uploads ask for exactly one
        // transform and clip per instance; the counted ones for at least that many, the rest of each array being left
        // unread. Nothing here writes or reserves anything.
        private bool Accepts(
            VpIndirectCommand[] commands,
            int commandCount,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            bool exactLengths,
            out long instanceTotal)
        {
            return Accepts(
                new ReadOnlySpan<VpIndirectCommand>(commands), commandCount, objectToWorlds.Length, clips == null ? -1 : clips.Length,
                exactLengths, out instanceTotal);
        }

        // The same judgement for an array upload and a native one: the commands as they stand, and how many transforms
        // and clip records were handed over (clips negative: none given).
        private bool Accepts(
            ReadOnlySpan<VpIndirectCommand> commands,
            int commandCount,
            long transforms,
            long clips,
            bool exactLengths,
            out long instanceTotal)
        {
            instanceTotal = 0;
            if (commandCount < 0 || commandCount > commands.Length || commandCount > CommandCapacity)
            {
                return false;
            }

            for (int c = 0; c < commandCount; c++)
            {
                VpIndirectCommand command = commands[c];
                if (command.instanceCount < 0 || command.range.indexStart < 0 || command.range.indexCount < 0)
                {
                    return false;
                }

                instanceTotal += command.instanceCount;
            }

            if (instanceTotal > InstanceCapacity)
            {
                return false;
            }

            return exactLengths
                ? transforms == instanceTotal && (clips < 0 || clips == instanceTotal)
                : transforms >= instanceTotal && (clips < 0 || clips >= instanceTotal);
        }

        /// <summary>
        /// Whether <see cref="TryUpload(NativeArray{VpIndirectCommand}, int, NativeArray{Matrix4x4}, NativeArray{VpInstanceClip}, bool)"/>
        /// would accept the first <paramref name="commandCount"/> commands and the instances they name, decided without
        /// writing anything and by the same judgement that upload makes.
        /// </summary>
        public bool CanUpload(
            NativeArray<VpIndirectCommand> commands, int commandCount, NativeArray<Matrix4x4> objectToWorlds,
            NativeArray<VpInstanceClip> clips)
        {
            ThrowIfDisposed();
            return commands.IsCreated && objectToWorlds.IsCreated && clips.IsCreated
                && Accepts(commands.AsReadOnlySpan(), commandCount, objectToWorlds.Length, clips.Length, false, out _);
        }

        /// <summary>
        /// The counted upload from native views: the first <paramref name="commandCount"/> commands and, of the
        /// transforms and the clip records, one per instance those commands name. The views are the valid part of their
        /// owner's room and may be longer; what lies past the counts is neither checked nor transferred. The transforms
        /// and the clips go to the GPU from the views themselves: nothing is copied on the way. Accepted and refused as
        /// the array upload is, by the same judgement.
        /// </summary>
        public bool TryUpload(
            NativeArray<VpIndirectCommand> commands, int commandCount, NativeArray<Matrix4x4> objectToWorlds,
            NativeArray<VpInstanceClip> clips, bool singlePassInstanced)
        {
            ThrowIfDisposed();
            if (!commands.IsCreated || !objectToWorlds.IsCreated || !clips.IsCreated)
            {
                throw new ArgumentException("the commands, the transforms and the clips are all given to a native upload");
            }

            if (!Accepts(commands.AsReadOnlySpan(), commandCount, objectToWorlds.Length, clips.Length, false, out long instanceTotal))
            {
                return false;
            }

            WriteArguments(commands.AsReadOnlySpan(), commandCount, objectToWorlds.AsReadOnlySpan(), singlePassInstanced, out Bounds worldBounds);
            if (instanceTotal > 0)
            {
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)instanceTotal);
                InstanceGeneration++;
                _instanceClipBuffer.SetData(clips, 0, 0, (int)instanceTotal);
                ClipGeneration++;
                InstanceSetDataCalls += 2;
                InstanceTransfers++;
                InstanceElementsTransferred += instanceTotal;
            }

            _uploaded = true;
            CommandCount = commandCount;
            InstanceCount = (int)instanceTotal;
            SinglePassInstanced = singlePassInstanced;
            WorldBounds = worldBounds;
            return true;
        }

        /// <summary>
        /// Observation: how many times the two argument buffers were sent and how many commands in all; how many times
        /// the transforms and the clips were sent (the two together counted once) and how many instances in all.
        /// </summary>
        public long ArgumentTransfers { get; private set; }
        public long ArgumentElementsTransferred { get; private set; }
        public long InstanceTransfers { get; private set; }
        public long InstanceElementsTransferred { get; private set; }

        /// <summary>
        /// Observation: the buffer writes themselves (SetData calls) -- two for one sending of the arguments (the
        /// forward and the shadow buffer), two for one sending of instances (the transforms and the clips).
        /// </summary>
        public long ArgumentSetDataCalls { get; private set; }
        public long InstanceSetDataCalls { get; private set; }

        /// <summary>
        /// The upload of **what changed** since the last one: the commands as they stand now, and of the transforms and
        /// the clip records the one range [<paramref name="instanceStart"/>, <paramref name="instanceEnd"/>) -- every
        /// instance outside it is taken to hold on the GPU what the views hold. The views are the whole valid part of
        /// their owner's room, complete and current, as for the counted upload.
        /// <para>
        /// The two argument buffers are made and sent again, whole, only when <paramref name="commandsChanged"/> says
        /// the commands differ from the last upload's, when the stereo condition does, or when nothing was uploaded
        /// yet; otherwise they are left as they stand. A batch that was never uploaded to takes every instance, whatever
        /// range is named. The draw's bounds are gathered again, over every instance, whenever anything was sent, and
        /// kept as they were when nothing was. An empty range with nothing else to send transfers nothing at all.
        /// </para>
        /// <para>
        /// Accepted and refused by the same judgement as the counted upload when the commands are sent; when they are
        /// not, the counts are the last upload's and only the range is judged. False changes nothing.
        /// </para>
        /// </summary>
        public bool TryUploadChanged(
            NativeArray<VpIndirectCommand> commands, int commandCount, NativeArray<Matrix4x4> objectToWorlds,
            NativeArray<VpInstanceClip> clips, bool singlePassInstanced, bool commandsChanged, int instanceStart, int instanceEnd)
        {
            ThrowIfDisposed();
            if (!commands.IsCreated || !objectToWorlds.IsCreated || !clips.IsCreated)
            {
                throw new ArgumentException("the commands, the transforms and the clips are all given to a native upload");
            }

            bool first = !_uploaded;
            bool arguments = first || commandsChanged || singlePassInstanced != SinglePassInstanced || commandCount != CommandCount;
            long instanceTotal;
            if (arguments)
            {
                if (!Accepts(commands.AsReadOnlySpan(), commandCount, objectToWorlds.Length, clips.Length, false, out instanceTotal))
                {
                    return false;
                }
            }
            else
            {
                instanceTotal = InstanceCount;
                if (objectToWorlds.Length < instanceTotal || clips.Length < instanceTotal)
                {
                    return false;
                }
            }

            if (first)
            {
                instanceStart = 0;
                instanceEnd = (int)instanceTotal;
            }

            if (instanceStart < 0 || instanceEnd < instanceStart || instanceEnd > instanceTotal)
            {
                return false;
            }

            bool instances = instanceEnd > instanceStart;
            if (arguments)
            {
                WriteArguments(commands.AsReadOnlySpan(), commandCount, objectToWorlds.AsReadOnlySpan(), singlePassInstanced, out Bounds worldBounds);
                WorldBounds = worldBounds;
            }
            else if (instances)
            {
                WorldBounds = BoundsOfInstances(commands.AsReadOnlySpan(), commandCount, objectToWorlds.AsReadOnlySpan());
            }

            if (instances)
            {
                int count = instanceEnd - instanceStart;
                _instanceBuffer.SetData(objectToWorlds, instanceStart, instanceStart, count);
                InstanceGeneration++;
                _instanceClipBuffer.SetData(clips, instanceStart, instanceStart, count);
                ClipGeneration++;
                InstanceSetDataCalls += 2;
                InstanceTransfers++;
                InstanceElementsTransferred += count;
            }

            _uploaded = true;
            CommandCount = commandCount;
            InstanceCount = (int)instanceTotal;
            SinglePassInstanced = singlePassInstanced;
            return true;
        }

        // The bounds of every instance at its own transform, as WriteArguments gathers them: for an upload that sends
        // instances and no commands.
        private static Bounds BoundsOfInstances(ReadOnlySpan<VpIndirectCommand> commands, int commandCount, ReadOnlySpan<Matrix4x4> objectToWorlds)
        {
            int startInstance = 0;
            bool anyInstance = false;
            Bounds worldBounds = default;
            for (int c = 0; c < commandCount; c++)
            {
                VpIndirectCommand command = commands[c];
                for (int i = startInstance; i < startInstance + command.instanceCount; i++)
                {
                    Bounds instanceBounds = VpDirectDraw.WorldBounds(command.localBounds, objectToWorlds[i]);
                    if (anyInstance)
                    {
                        worldBounds.Encapsulate(instanceBounds);
                    }
                    else
                    {
                        worldBounds = instanceBounds;
                        anyInstance = true;
                    }
                }

                startInstance += command.instanceCount;
            }

            return worldBounds;
        }

        // ----- commands in stable slots (DESIGN 5.6) -----------------------------------------------------------------

        /// <summary>
        /// Observation: what a slot upload sent **whole** -- a batch's first upload, or another stereo condition --
        /// kept apart from the counters of what changed: commands, instance records, and the buffer writes of both.
        /// </summary>
        public long WholeArgumentElementsTransferred { get; private set; }
        public long WholeInstanceElementsTransferred { get; private set; }
        public long WholeSetDataCalls { get; private set; }

        /// <summary>
        /// Observation: the buffer writes of the instance records' range by <see cref="TryUploadSlots"/>, counted where
        /// each is made: the transforms' and the clips'. At most one of each an upload; a whole upload is not among them.
        /// </summary>
        public long InstanceTransformSetDataCalls { get; private set; }
        public long InstanceClipSetDataCalls { get; private set; }

        /// <summary>
        /// Whether <see cref="TryUploadSlots"/> would take these views and ends at all, decided without writing or
        /// reading anything of them: the ends against this batch's room and against the views. What the commands
        /// themselves say is judged by the upload, over the commands it sends.
        /// </summary>
        public bool CanUploadSlots(
            NativeArray<VpIndirectCommand> commands, NativeArray<int> startInstances, int commandEnd,
            NativeArray<Matrix4x4> objectToWorlds, NativeArray<VpInstanceClip> clips, int instanceEnd)
        {
            ThrowIfDisposed();
            return commands.IsCreated && startInstances.IsCreated && objectToWorlds.IsCreated && clips.IsCreated
                && commandEnd >= 0 && commandEnd <= CommandCapacity && commands.Length >= commandEnd && startInstances.Length >= commandEnd
                && instanceEnd >= 0 && instanceEnd <= InstanceCapacity && objectToWorlds.Length >= instanceEnd && clips.Length >= instanceEnd;
        }

        /// <summary>
        /// The upload of a batch whose commands and instances stand in **slots that do not move** (DESIGN 5.6). Command
        /// <c>c</c> draws <c>commands[c].instanceCount</c> instances from <c>startInstances[c]</c> on: where a command's
        /// instances begin is said, not added up from the commands before it, so a command may be taken out -- its
        /// instance count made zero, which draws nothing and selects nothing -- or put in without any other command or
        /// instance changing its place. The commands in [0, <paramref name="commandEnd"/>) are the ones processed, free
        /// ones among them included; the instance records in [0, <paramref name="instanceEnd"/>) are the ones that may
        /// be named.
        /// <para>
        /// **Only what changed is sent**: the commands of <paramref name="commandRanges"/>, a buffer write for each
        /// range, and the instance records (a transform and a clip) of the one range
        /// [<paramref name="instanceFrom"/>, <paramref name="instanceTo"/>): one buffer write for the transforms and
        /// one for the clips, or none when the range is empty. Everything outside is taken to stand on the GPU as the
        /// views hold it. The instance range covers what lies between the records written, so the views are complete
        /// and current over all of it. The command ranges are read, not cleared.
        /// </para>
        /// <para>
        /// **Everything is sent** -- every command and every instance record below the two ends, whatever the ranges
        /// say -- when this batch has not been uploaded to (a batch that takes a smaller one's place holds nothing).
        /// When only the stereo condition differs from the last upload's, every command is sent again (the forward
        /// arguments depend on it) and, of the instance records, the ranges alone. What is sent whole is counted apart
        /// (<see cref="WholeArgumentElementsTransferred"/>, <see cref="WholeInstanceElementsTransferred"/>).
        /// </para>
        /// <para>
        /// False, having written nothing and changed nothing, when the views or the ends do not fit
        /// (<see cref="CanUploadSlots"/>) or a command about to be sent names instances outside
        /// [0, <paramref name="instanceEnd"/>). The draw's bounds are gathered again, over every instance drawn,
        /// whenever anything was sent.
        /// </para>
        /// </summary>
        public bool TryUploadSlots(
            NativeArray<VpIndirectCommand> commands, NativeArray<int> startInstances, int commandEnd,
            NativeArray<Matrix4x4> objectToWorlds, NativeArray<VpInstanceClip> clips, int instanceEnd,
            bool singlePassInstanced, VpChangedRanges commandRanges, int instanceFrom, int instanceTo)
        {
            ThrowIfDisposed();
            if (commandRanges == null)
            {
                throw new ArgumentNullException(nameof(commandRanges));
            }

            if (!CanUploadSlots(commands, startInstances, commandEnd, objectToWorlds, clips, instanceEnd))
            {
                return false;
            }

            ReadOnlySpan<VpIndirectCommand> read = commands.AsReadOnlySpan();
            ReadOnlySpan<int> starts = startInstances.AsReadOnlySpan();
            bool first = !_uploaded;
            bool everyCommand = first || singlePassInstanced != SinglePassInstanced;

            // Judged before anything is written: a refusal leaves every buffer as it was.
            if (everyCommand)
            {
                if (!SlotsAreWithin(read, starts, 0, commandEnd, instanceEnd))
                {
                    return false;
                }
            }
            else
            {
                for (int r = 0; r < commandRanges.Count; r++)
                {
                    if (!SlotsAreWithin(read, starts, Math.Min(commandRanges.StartAt(r), commandEnd), Math.Min(commandRanges.EndAt(r), commandEnd), instanceEnd))
                    {
                        return false;
                    }
                }
            }

            bool sent = false;
            if (everyCommand)
            {
                if (commandEnd > 0)
                {
                    int calls = SendSlotCommands(read, starts, 0, commandEnd, singlePassInstanced);
                    WholeArgumentElementsTransferred += commandEnd;
                    WholeSetDataCalls += calls;
                    sent = true;
                }
            }
            else
            {
                bool anyCommand = false;
                for (int r = 0; r < commandRanges.Count; r++)
                {
                    int from = Math.Min(commandRanges.StartAt(r), commandEnd), to = Math.Min(commandRanges.EndAt(r), commandEnd);
                    if (to > from)
                    {
                        ArgumentSetDataCalls += SendSlotCommands(read, starts, from, to, singlePassInstanced);
                        ArgumentElementsTransferred += to - from;
                        anyCommand = true;
                    }
                }

                ArgumentTransfers += anyCommand ? 1 : 0;
                sent = anyCommand;
            }

            if (first)
            {
                if (instanceEnd > 0)
                {
                    _instanceBuffer.SetData(objectToWorlds, 0, 0, instanceEnd);
                    InstanceGeneration++;
                    _instanceClipBuffer.SetData(clips, 0, 0, instanceEnd);
                    ClipGeneration++;
                    WholeInstanceElementsTransferred += instanceEnd;
                    WholeSetDataCalls += 2;
                    sent = true;
                }
            }
            else
            {
                // One range, a write a buffer: whatever lies between the records written goes with them.
                int from = Math.Max(0, Math.Min(instanceFrom, instanceEnd)), to = Math.Min(instanceTo, instanceEnd);
                if (to > from)
                {
                    _instanceBuffer.SetData(objectToWorlds, from, from, to - from);
                    InstanceGeneration++;
                    InstanceTransformSetDataCalls++;
                    _instanceClipBuffer.SetData(clips, from, from, to - from);
                    ClipGeneration++;
                    InstanceClipSetDataCalls++;
                    InstanceSetDataCalls += 2;
                    InstanceElementsTransferred += to - from;
                    InstanceTransfers++;
                    sent = true;
                }
            }

            if (sent)
            {
                WorldBounds = BoundsOfSlots(read, starts, commandEnd, objectToWorlds.AsReadOnlySpan());
            }

            _uploaded = true;
            CommandCount = commandEnd;
            InstanceCount = instanceEnd;
            SinglePassInstanced = singlePassInstanced;
            return true;
        }

        // Every command of [from, to) names instances inside [0, instanceEnd), or none.
        private static bool SlotsAreWithin(
            ReadOnlySpan<VpIndirectCommand> commands, ReadOnlySpan<int> startInstances, int from, int to, int instanceEnd)
        {
            for (int c = from; c < to; c++)
            {
                VpIndirectCommand command = commands[c];
                if (command.instanceCount < 0 || command.range.indexStart < 0 || command.range.indexCount < 0)
                {
                    return false;
                }

                if (command.instanceCount > 0 && (startInstances[c] < 0 || startInstances[c] > instanceEnd - command.instanceCount))
                {
                    return false;
                }
            }

            return true;
        }

        // The commands of [from, to) staged as this batch sends them -- the two argument buffers, or the selection's
        // commands -- each with the start its slot says, and sent as that one range. Answers the buffer writes made.
        private int SendSlotCommands(
            ReadOnlySpan<VpIndirectCommand> commands, ReadOnlySpan<int> startInstances, int from, int to, bool singlePassInstanced)
        {
            int count = to - from;
            if (_cull != null)
            {
                Span<VpCullCommand> staged = _cullStaging != null
                    ? _cullStaging.AsSpan(from, count)
                    : new Span<VpCullCommand>(_cullCommands, from, count);
                for (int c = from; c < to; c++)
                {
                    VpIndirectCommand command = commands[c];
                    staged[c - from] = new VpCullCommand
                    {
                        indexCount = (uint)command.range.indexCount,
                        startIndex = (uint)command.range.indexStart,
                        startInstance = (uint)startInstances[c],
                        instanceCount = (uint)command.instanceCount,
                        centre = command.localBounds.center,
                        extents = command.localBounds.extents,
                    };
                }

                if (_cullStaging != null)
                {
                    _cullCommandBuffer.SetData(_cullStaging.First(to), from, from, count);
                    CullCommandGeneration++;
                }
                else
                {
                    _cullCommandBuffer.SetData(_cullCommands, from, from, count);
                    CullCommandGeneration++;
                }

                return 1;
            }

            Span<GraphicsBuffer.IndirectDrawIndexedArgs> shadowArguments = _shadowStaging != null
                ? _shadowStaging.AsSpan(from, count)
                : new Span<GraphicsBuffer.IndirectDrawIndexedArgs>(_shadowArguments, from, count);
            Span<GraphicsBuffer.IndirectDrawIndexedArgs> forwardArguments = _forwardStaging != null
                ? _forwardStaging.AsSpan(from, count)
                : new Span<GraphicsBuffer.IndirectDrawIndexedArgs>(_forwardArguments, from, count);
            uint multiplier = singlePassInstanced ? 2u : 1u;
            for (int c = from; c < to; c++)
            {
                VpIndirectCommand command = commands[c];
                uint start = (uint)startInstances[c];
                shadowArguments[c - from] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount,
                    startIndex = (uint)command.range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = start,
                };
                forwardArguments[c - from] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount * multiplier,
                    startIndex = (uint)command.range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = start * multiplier,
                };
            }

            if (_forwardStaging != null)
            {
                _forwardArgumentBuffer.SetData(_forwardStaging.First(to), from, from, count);
                _shadowArgumentBuffer.SetData(_shadowStaging.First(to), from, from, count);
            }
            else
            {
                _forwardArgumentBuffer.SetData(_forwardArguments, from, from, count);
                _shadowArgumentBuffer.SetData(_shadowArguments, from, from, count);
            }

            return 2;
        }

        // The bounds of every instance a slot's command draws, at its own transform; a free slot draws none.
        private static Bounds BoundsOfSlots(
            ReadOnlySpan<VpIndirectCommand> commands, ReadOnlySpan<int> startInstances, int commandEnd, ReadOnlySpan<Matrix4x4> objectToWorlds)
        {
            bool anyInstance = false;
            Bounds worldBounds = default;
            for (int c = 0; c < commandEnd; c++)
            {
                VpIndirectCommand command = commands[c];
                int start = startInstances[c];
                for (int i = start; i < start + command.instanceCount; i++)
                {
                    Bounds instanceBounds = VpDirectDraw.WorldBounds(command.localBounds, objectToWorlds[i]);
                    if (anyInstance)
                    {
                        worldBounds.Encapsulate(instanceBounds);
                    }
                    else
                    {
                        worldBounds = instanceBounds;
                        anyInstance = true;
                    }
                }
            }

            return worldBounds;
        }

        /// <summary>
        /// Writes every GPU buffer of this batch once, whole, with zeros, so that each stands on the device before the
        /// first upload of a frame. Only before the first upload; nothing uploaded, no count and no bound changes.
        /// </summary>
        /// <exception cref="InvalidOperationException">Something was uploaded already.</exception>
        public void WriteWholeOnce()
        {
            ThrowIfDisposed();
            if (_uploaded)
            {
                throw new InvalidOperationException("the buffers are written whole only before the first upload");
            }

            // Each array is taken inside the protection: one that cannot be had, or a write that throws, leaves none of
            // the others behind.
            NativeArray<GraphicsBuffer.IndirectDrawIndexedArgs> arguments = default;
            NativeArray<Matrix4x4> transforms = default;
            NativeArray<VpInstanceClip> clips = default;
            NativeArray<VpCullCommand> cullCommands = default;
            NativeArray<uint> visible = default;
            NativeArray<uint> cullArguments = default;
            try
            {
                arguments = VpWholeWrite.Zeros<GraphicsBuffer.IndirectDrawIndexedArgs>("arguments", CommandCapacity);
                transforms = VpWholeWrite.Zeros<Matrix4x4>("transforms", InstanceCapacity);
                clips = VpWholeWrite.Zeros<VpInstanceClip>("clips", InstanceCapacity);
                if (_cull != null)
                {
                    // What the selection reads and writes stands on the device too: zero arguments draw nothing, so a
                    // view drawn before its first selection draws nothing rather than something undefined. The
                    // arguments are written as words: an entry is Unity's or the plugin's stride (both whole words).
                    cullCommands = VpWholeWrite.Zeros<VpCullCommand>("cull commands", CommandCapacity);
                    visible = VpWholeWrite.Zeros<uint>("visible instances", InstanceCapacity);
                    cullArguments = VpWholeWrite.Zeros<uint>("cull arguments", CommandCapacity * _cull.ArgumentStride / sizeof(uint));
                    VpWholeWrite.Step("write cull commands");
                    _cullCommandBuffer.SetData(cullCommands, 0, 0, CommandCapacity);
                    CullCommandGeneration++;
                    for (int v = 0; v < _cullViews.Length; v++)
                    {
                        VpWholeWrite.Step("write a view's lists and arguments");
                        _cullViews[v].forwardVisible.SetData(visible, 0, 0, InstanceCapacity);
                        CullViewGeneration++;
                        _cullViews[v].forwardArguments.SetData(cullArguments, 0, 0, cullArguments.Length);
                        CullViewGeneration++;
                        _cullViews[v].shadowVisible.SetData(visible, 0, 0, InstanceCapacity);
                        CullViewGeneration++;
                        _cullViews[v].shadowArguments.SetData(cullArguments, 0, 0, cullArguments.Length);
                        CullViewGeneration++;
                    }
                }
                else
                {
                    VpWholeWrite.Step("write forward arguments");
                    _forwardArgumentBuffer.SetData(arguments, 0, 0, CommandCapacity);
                    VpWholeWrite.Step("write shadow arguments");
                    _shadowArgumentBuffer.SetData(arguments, 0, 0, CommandCapacity);
                }

                VpWholeWrite.Step("write transforms");
                _instanceBuffer.SetData(transforms, 0, 0, InstanceCapacity);
                InstanceGeneration++;
                VpWholeWrite.Step("write clips");
                _instanceClipBuffer.SetData(clips, 0, 0, InstanceCapacity);
                ClipGeneration++;
            }
            finally
            {
                VpWholeWrite.Release(ref arguments);
                VpWholeWrite.Release(ref transforms);
                VpWholeWrite.Release(ref clips);
                VpWholeWrite.Release(ref cullCommands);
                VpWholeWrite.Release(ref visible);
                VpWholeWrite.Release(ref cullArguments);
            }
        }

        /// <summary>
        /// Diagnosis only -- tests and evidence runs: reads back, **waiting for the GPU**, how many instances the last
        /// selection issued for <paramref name="view"/> kept for the body and as casters. The product never calls
        /// this: it issues its draws without knowing any count.
        /// </summary>
        public void ReadCullCountsForDiagnosis(int view, out int forwardKept, out int shadowKept)
        {
            CullView target = View(view);
            forwardKept = 0;
            shadowKept = 0;
            if (CommandCount == 0)
            {
                return;
            }

            // Read as words: an entry is Unity's five arguments or the plugin's number and five, and the instance
            // count is the second argument either way.
            int wordsPerEntry = _cull.ArgumentStride / sizeof(uint);
            int countWord = _cull.ArgumentOffset / sizeof(uint) + 1;
            var words = new uint[CommandCount * wordsPerEntry];
            uint multiplier = SinglePassInstanced ? 2u : 1u;
            target.forwardArguments.GetData(words, 0, 0, words.Length);
            for (int c = 0; c < CommandCount; c++)
            {
                forwardKept += (int)(words[c * wordsPerEntry + countWord] / multiplier);
            }

            target.shadowArguments.GetData(words, 0, 0, words.Length);
            for (int c = 0; c < CommandCount; c++)
            {
                shadowKept += (int)words[c * wordsPerEntry + countWord];
            }
        }

        /// <summary>
        /// For a batch that selects on the GPU and takes another's place: writes every view's arguments with zeros, so
        /// that a view drawn before its first selection draws nothing. (<see cref="WriteWholeOnce"/> does this, and
        /// more, for a batch made before play.) Does nothing for a batch that does not select.
        /// </summary>
        public void WriteCullArgumentsZero()
        {
            ThrowIfDisposed();
            if (_cull == null)
            {
                return;
            }

            var zeros = new NativeArray<uint>(CommandCapacity * _cull.ArgumentStride / sizeof(uint), Allocator.Temp, NativeArrayOptions.ClearMemory);
            try
            {
                for (int v = 0; v < _cullViews.Length; v++)
                {
                    _cullViews[v].forwardArguments.SetData(zeros, 0, 0, zeros.Length);
                    CullViewGeneration++;
                    _cullViews[v].shadowArguments.SetData(zeros, 0, 0, zeros.Length);
                    CullViewGeneration++;
                }
            }
            finally
            {
                zeros.Dispose();
            }
        }

        /// <summary>
        /// The bytes of this batch's GPU buffers: two argument buffers, the transforms and the clips -- or, selecting on
        /// the GPU, the commands' records, the transforms, the clips and what every view holds.
        /// </summary>
        public long GpuBytes => _cull == null
            ? ((long)CommandCapacity * GraphicsBuffer.IndirectDrawIndexedArgs.size * 2) + ((long)InstanceCapacity * (InstanceStride + InstanceClipStride))
            : ((long)CommandCapacity * VpCullCommand.Stride) + ((long)InstanceCapacity * (InstanceStride + InstanceClipStride)) + CullViewBytes;

        /// <summary>The bytes the selection's own results take on the GPU, every view together; zero without selection.</summary>
        public long CullViewBytes => _cull == null
            ? 0L
            : (long)_cull.ViewCapacity * 2L * (((long)CommandCapacity * _cull.ArgumentStride) + ((long)InstanceCapacity * sizeof(uint)));

        /// <summary>What this batch's staging is made of, in bytes.</summary>
        public void DescribeStaging(System.Collections.Generic.List<VpRoomLine> into, string owner)
        {
            if (_forwardStaging != null) into.Add(VpRoomLine.Of(owner + ".forwardArguments", _forwardStaging));
            if (_shadowStaging != null) into.Add(VpRoomLine.Of(owner + ".shadowArguments", _shadowStaging));
            if (_forwardArguments != null) into.Add(VpRoomLine.OfManaged(owner + ".forwardArguments", _forwardArguments));
            if (_shadowArguments != null) into.Add(VpRoomLine.OfManaged(owner + ".shadowArguments", _shadowArguments));
            if (_instanceClips != null) into.Add(VpRoomLine.OfManaged(owner + ".instanceClips", _instanceClips));
            if (_cullStaging != null) into.Add(VpRoomLine.Of(owner + ".cullCommands", _cullStaging));
            if (_cullCommands != null) into.Add(VpRoomLine.OfManaged(owner + ".cullCommands", _cullCommands));
        }

        /// <summary>
        /// The same upload, with one <see cref="VpInstanceClip"/> per instance: which parts of up to
        /// <see cref="VpInstanceClip.PlaneCapacity"/> cut planes that instance keeps,
        /// for the provisional display of DESIGN 5.1. **Null** is how the clips are omitted, and gives the ordinary
        /// display; an array must hold exactly one record per instance, so an empty array is accepted only when
        /// there are no instances. Any other count is rejected, changing nothing, as are the conditions of the other
        /// overload.
        /// <para>
        /// Each record is one fixed-size entry whatever its plane count, so nothing here varies with it: no second
        /// path, keyword or draw, and every instance is uploaded the same way. A count out of range cannot arrive
        /// this far — <see cref="VpInstanceClip.TryKeep"/> refuses more than the capacity when the record is built,
        /// before any update — so what the buffer holds always carries between zero and the capacity planes.
        /// </para>
        /// <para>
        /// The clips go into the fixed-capacity buffer this batch owns, and the culling bounds of the draw are the
        /// instance bounds at each instance's own transform, so nothing is culled away from where it is drawn. The
        /// bounds stay the parent's, conservatively: what the planes remove is not subtracted from them.
        /// </para>
        /// <para>
        /// The forward and the shadow call bind that one buffer, so within a registration neither can read a
        /// different plane set, count or offset from the other. That is all the binding does: it is **not** a guard
        /// against uploading again between registrations. Finishing every update before the frame is registered
        /// stays the callers responsibility.
        /// </para>
        /// </summary>
        public bool TryUpload(VpIndirectCommand[] commands, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips, bool singlePassInstanced)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            return Upload(commands, commands.Length, objectToWorlds, clips, singlePassInstanced, true);
        }

        /// <summary>
        /// The same upload of only the first <paramref name="commandCount"/> commands, and of the instances they name:
        /// the first transforms and clip records, one per instance. The arrays may be longer; what lies past those
        /// counts is neither checked nor transferred, so an array kept at a fixed size and filled to a count each time
        /// never sends what an earlier, longer fill left behind. Accepted and refused exactly as
        /// <see cref="CanUpload(VpIndirectCommand[], int, Matrix4x4[], VpInstanceClip[])"/> says, and a count outside the
        /// array or the capacity is refused, changing nothing.
        /// </summary>
        public bool TryUpload(
            VpIndirectCommand[] commands, int commandCount, Matrix4x4[] objectToWorlds, VpInstanceClip[] clips,
            bool singlePassInstanced)
        {
            ThrowIfDisposed();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            return Upload(commands, commandCount, objectToWorlds, clips, singlePassInstanced, false);
        }

        private bool Upload(
            VpIndirectCommand[] commands,
            int commandCount,
            Matrix4x4[] objectToWorlds,
            VpInstanceClip[] clips,
            bool singlePassInstanced,
            bool exactLengths)
        {
            if (objectToWorlds == null)
            {
                throw new ArgumentNullException(nameof(objectToWorlds));
            }

            if (!Accepts(commands, commandCount, objectToWorlds, clips, exactLengths, out long instanceTotal))
            {
                return false;
            }

            WriteArguments(commands, commandCount, objectToWorlds, singlePassInstanced, out Bounds worldBounds);
            if (instanceTotal > 0)
            {
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)instanceTotal);
                InstanceGeneration++;

                // No clips given is the ordinary display: every instance takes a record that clips nothing and
                // moves nothing. Each record is written whole, count and all eight planes together, so a record
                // that now carries fewer planes — or none — leaves no plane of an earlier upload in force.
                if (_instanceClips == null)
                {
                    _instanceClips = new VpInstanceClip[InstanceCapacity];
                }

                for (int i = 0; i < instanceTotal; i++)
                {
                    _instanceClips[i] = clips == null ? VpInstanceClip.None : clips[i];
                }

                _instanceClipBuffer.SetData(_instanceClips, 0, 0, (int)instanceTotal);
                ClipGeneration++;
                InstanceSetDataCalls += 2;
                InstanceTransfers++;
                InstanceElementsTransferred += instanceTotal;
            }

            _uploaded = true;
            CommandCount = commandCount;
            InstanceCount = (int)instanceTotal;
            SinglePassInstanced = singlePassInstanced;
            WorldBounds = worldBounds;
            return true;
        }

        // The two argument buffers made from the commands and sent, and the bounds of every instance at its own transform.
        // Staged where this batch keeps its staging; the commands and the transforms are read as they stand.
        private void WriteArguments(
            ReadOnlySpan<VpIndirectCommand> commands, int commandCount, ReadOnlySpan<Matrix4x4> objectToWorlds,
            bool singlePassInstanced, out Bounds worldBounds)
        {
            if (_cull != null)
            {
                WriteCullCommands(commands, commandCount);
                worldBounds = BoundsOfInstances(commands, commandCount, objectToWorlds);
                return;
            }

            Span<GraphicsBuffer.IndirectDrawIndexedArgs> shadowArguments = _shadowStaging != null
                ? _shadowStaging.AsSpan(0, commandCount)
                : new Span<GraphicsBuffer.IndirectDrawIndexedArgs>(_shadowArguments, 0, commandCount);
            Span<GraphicsBuffer.IndirectDrawIndexedArgs> forwardArguments = _forwardStaging != null
                ? _forwardStaging.AsSpan(0, commandCount)
                : new Span<GraphicsBuffer.IndirectDrawIndexedArgs>(_forwardArguments, 0, commandCount);
            uint multiplier = singlePassInstanced ? 2u : 1u;
            int startInstance = 0;
            bool anyInstance = false;
            worldBounds = default;
            for (int c = 0; c < commandCount; c++)
            {
                VpIndirectCommand command = commands[c];
                shadowArguments[c] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount,
                    startIndex = (uint)command.range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = (uint)startInstance,
                };
                forwardArguments[c] = new GraphicsBuffer.IndirectDrawIndexedArgs
                {
                    indexCountPerInstance = (uint)command.range.indexCount,
                    instanceCount = (uint)command.instanceCount * multiplier,
                    startIndex = (uint)command.range.indexStart,
                    baseVertexIndex = 0,
                    startInstance = (uint)startInstance * multiplier,
                };

                for (int i = startInstance; i < startInstance + command.instanceCount; i++)
                {
                    Bounds instanceBounds = VpDirectDraw.WorldBounds(command.localBounds, objectToWorlds[i]);

                    if (anyInstance)
                    {
                        worldBounds.Encapsulate(instanceBounds);
                    }
                    else
                    {
                        worldBounds = instanceBounds;
                        anyInstance = true;
                    }
                }

                startInstance += command.instanceCount;
            }

            if (commandCount > 0)
            {
                if (_forwardStaging != null)
                {
                    _forwardArgumentBuffer.SetData(_forwardStaging.First(commandCount), 0, 0, commandCount);
                    _shadowArgumentBuffer.SetData(_shadowStaging.First(commandCount), 0, 0, commandCount);
                }
                else
                {
                    _forwardArgumentBuffer.SetData(_forwardArguments, 0, 0, commandCount);
                    _shadowArgumentBuffer.SetData(_shadowArguments, 0, 0, commandCount);
                }

                ArgumentSetDataCalls += 2;
                ArgumentTransfers++;
                ArgumentElementsTransferred += commandCount;
            }
        }

        // A batch that selects on the GPU: the commands as the selection reads them, made and sent where the two
        // argument buffers would have been. One buffer write, counted as one sending of the arguments.
        private void WriteCullCommands(ReadOnlySpan<VpIndirectCommand> commands, int commandCount)
        {
            Span<VpCullCommand> staged = _cullStaging != null
                ? _cullStaging.AsSpan(0, commandCount)
                : new Span<VpCullCommand>(_cullCommands, 0, commandCount);
            int startInstance = 0;
            for (int c = 0; c < commandCount; c++)
            {
                VpIndirectCommand command = commands[c];
                staged[c] = new VpCullCommand
                {
                    indexCount = (uint)command.range.indexCount,
                    startIndex = (uint)command.range.indexStart,
                    startInstance = (uint)startInstance,
                    instanceCount = (uint)command.instanceCount,
                    centre = command.localBounds.center,
                    extents = command.localBounds.extents,
                };
                startInstance += command.instanceCount;
            }

            if (commandCount > 0)
            {
                if (_cullStaging != null)
                {
                    _cullCommandBuffer.SetData(_cullStaging.First(commandCount), 0, 0, commandCount);
                    CullCommandGeneration++;
                }
                else
                {
                    _cullCommandBuffer.SetData(_cullCommands, 0, 0, commandCount);
                    CullCommandGeneration++;
                }

                ArgumentSetDataCalls++;
                ArgumentTransfers++;
                ArgumentElementsTransferred += commandCount;
            }
        }

        /// <summary>
        /// Issues, into <paramref name="commands"/>, the selection of this batch's instances for <paramref name="view"/>
        /// from <paramref name="conditions"/>: the view's lists and arguments, for the body and for the casters, are
        /// written on the GPU when that command buffer is executed. It must be executed before the draws of that view
        /// that were registered with <see cref="RenderForward(Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera, int)"/>
        /// and <see cref="RenderShadows(Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera, int)"/>
        /// are drawn, and it is the caller that puts it there. Nothing is read back and nothing waits.
        /// <para>
        /// Every uploaded command's arguments are written, whatever was kept. With no command uploaded there is nothing
        /// to draw and nothing is issued. The conditions are copied as they are now.
        /// </para>
        /// </summary>
        public void IssueCull(CommandBuffer commands, int view, VpCullConditions conditions)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            if (conditions == null)
            {
                throw new ArgumentNullException(nameof(conditions));
            }

            CullView target = View(view);
            if (CommandCount == 0)
            {
                return;
            }

            ComputeShader shader = _cull.Shader;
            int kernel = _cull.Kernel;
            commands.SetComputeIntParam(shader, CommandCountId, CommandCount);
            commands.SetComputeIntParam(shader, ArgumentStrideId, _cull.ArgumentStride);
            commands.SetComputeIntParam(shader, ArgumentOffsetId, _cull.ArgumentOffset);
            commands.SetComputeIntParam(shader, ForwardMultiplierId, SinglePassInstanced ? 2 : 1);
            commands.SetComputeIntParam(shader, EyeCountId, conditions.eyeCount);
            commands.SetComputeVectorArrayParam(shader, EyePlanesId, conditions.eyePlanes);
            commands.SetComputeIntParam(shader, ShadowSplitCountId, conditions.shadowSplitCount);
            commands.SetComputeVectorParam(shader, ShadowPlaneCountsId, conditions.shadowPlaneCounts);
            commands.SetComputeVectorArrayParam(shader, ShadowPlanesId, conditions.shadowPlanes);
            commands.SetComputeBufferParam(shader, kernel, CullCommandsId, _cullCommandBuffer);
            commands.SetComputeBufferParam(shader, kernel, InstanceObjectToWorldId, _instanceBuffer);
            commands.SetComputeBufferParam(shader, kernel, ForwardVisibleId, target.forwardVisible);
            commands.SetComputeBufferParam(shader, kernel, ForwardArgumentsId, target.forwardArguments);
            commands.SetComputeBufferParam(shader, kernel, ShadowVisibleId, target.shadowVisible);
            commands.SetComputeBufferParam(shader, kernel, ShadowArgumentsId, target.shadowArguments);
            commands.DispatchCompute(shader, kernel, (CommandCount + 63) / 64, 1, 1);
            CullDispatches++;
        }

        /// <summary>
        /// Queues every uploaded command for this frame's cameras, or only <paramref name="camera"/> when given, as a forward
        /// call and a shadow call. Issues nothing when no command is uploaded.
        /// </summary>
        public void Render(
            Material forwardMaterial,
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            Camera camera = null)
        {
            ThrowIfDisposed();
            Render(forwardMaterial, shadowMaterial, properties, buffers, layer, 0, CommandCount, camera);
        }

        /// <summary>
        /// Queues the commands [startCommand, startCommand + commandCount) as two Graphics.RenderPrimitivesIndexedIndirect
        /// calls, in order: the forward call, receiving but not casting shadows, then the shadow call, casting shadows.
        /// Issues nothing when <paramref name="commandCount"/> is 0. Throws when the commands are not within the uploaded
        /// ones. <paramref name="properties"/> is reused by the two calls in turn.
        /// </summary>
        public void Render(
            Material forwardMaterial,
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera = null)
        {
            RenderForward(forwardMaterial, properties, buffers, layer, startCommand, commandCount, camera);
            RenderShadows(shadowMaterial, properties, buffers, layer, startCommand, commandCount, camera);
        }

        /// <summary>
        /// The forward call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera)"/> alone:
        /// the colour pass, receiving but not casting shadows, without the shadow caster call, for a caller that does not
        /// want these commands to cast shadows. The command range check and buffer binding are those of Render, and the
        /// batch keeps owning its buffers.
        /// </summary>
        public void RenderForward(
            Material forwardMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera)
        {
            ThrowIfCulled();
            IssueForward(forwardMaterial, properties, buffers, layer, startCommand, commandCount, camera, _forwardArgumentBuffer);
        }

        /// <summary>
        /// The forward call of a batch that selects on the GPU, for <paramref name="cullView"/>: the same call, drawing
        /// with the arguments and from the list that view's selection writes. The material is one of the variant that
        /// reads the list (<see cref="VpGpuCullSetup.Keyword"/>). The selection of this view for this frame must be
        /// issued before the camera draws (<see cref="IssueCull"/>).
        /// </summary>
        public void RenderForward(
            Material forwardMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera,
            int cullView)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            CullView view = View(cullView);
            properties.SetBuffer(VisibleId, view.forwardVisible);
            IssueForward(forwardMaterial, properties, buffers, layer, startCommand, commandCount, camera, view.forwardArguments);
        }

        private void IssueForward(
            Material forwardMaterial, MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int layer,
            int startCommand, int commandCount, Camera camera, GraphicsBuffer arguments)
        {
            if (!BindCommands(properties, buffers, startCommand, commandCount))
            {
                return;
            }

            properties.SetInteger(InstanceMultiplierId, SinglePassInstanced ? 2 : 1);
            var renderParams = new RenderParams(forwardMaterial)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = WorldBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true,

                // The environment inputs of the shared lighting (VpCutSurfaceShading.hlsl). With both usages off Unity
                // gives the draw the scene's ambient probe and its default reflection as the per-draw constants URP's
                // lighting reads -- the same values a renderer outside every probe gets (measured against a
                // MeshRenderer, 2026-10-06). Stated rather than left to RenderParams' defaults, because the lighting
                // depends on it: a placed light probe or reflection probe does not reach these draws.
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
            };
            Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, arguments, commandCount, startCommand);
        }

        /// <summary>
        /// The shadow call of <see cref="Render(Material, Material, MaterialPropertyBlock, VpGpuIndexedGeometryBuffers, int, int, int, Camera)"/>
        /// alone, over its own range of commands. A caller that must cast one run of commands one-sided and another
        /// two-sided issues the two ranges itself with the material each needs: <c>Cull</c> is a drawing state of the
        /// material, not something an instance carries (DESIGN 5.4).
        /// <para>
        /// The shadow arguments hold the logical instance count whatever the forward ones hold, so a batch uploaded for
        /// Single Pass Instanced casts exactly the instances it would have cast otherwise. The doubling is the forward
        /// call's alone and nothing here multiplies anything.
        /// </para>
        /// </summary>
        public void RenderShadows(
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera)
        {
            ThrowIfCulled();
            IssueShadows(shadowMaterial, properties, buffers, layer, startCommand, commandCount, camera, _shadowArgumentBuffer);
        }

        /// <summary>
        /// The shadow call of a batch that selects on the GPU, for <paramref name="cullView"/>: the same call, casting
        /// only the instances that view's selection keeps as casters, which is a selection of its own and not the
        /// body's -- an instance the camera does not see casts when a split needs it. The shadow caster is also given
        /// the commands' bounds, with which its vertex stage passes over an instance that lies outside the slice being
        /// rendered (<see cref="VpGpuCullSetup.ShadowSliceSelection"/>).
        /// </summary>
        public void RenderShadows(
            Material shadowMaterial,
            MaterialPropertyBlock properties,
            VpGpuIndexedGeometryBuffers buffers,
            int layer,
            int startCommand,
            int commandCount,
            Camera camera,
            int cullView)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            CullView view = View(cullView);
            properties.SetBuffer(VisibleId, view.shadowVisible);
            properties.SetBuffer(CullCommandsId, _cullCommandBuffer);
            properties.SetFloat(ShadowSliceSelectionId, VpGpuCullSetup.ShadowSliceSelection ? 1f : 0f);
            IssueShadows(shadowMaterial, properties, buffers, layer, startCommand, commandCount, camera, view.shadowArguments);
        }

        private void IssueShadows(
            Material shadowMaterial, MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int layer,
            int startCommand, int commandCount, Camera camera, GraphicsBuffer arguments)
        {
            if (!BindCommands(properties, buffers, startCommand, commandCount))
            {
                return;
            }

            var renderParams = new RenderParams(shadowMaterial)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = WorldBounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = false,
            };
            Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, arguments, commandCount, startCommand);
        }

        public void Dispose()
        {
            Dispose(null);
        }

        /// <summary>
        /// Ends the batch. With <paramref name="giveUp"/> (the owner's hand-over to the plugin's route or routes this
        /// batch was drawn through), the buffers their issued events may still name are given up to it, and disposed
        /// by the keeper once no such event can still run; without one they are disposed here (Unity keeps the native
        /// buffers until the GPU is done with its own draws).
        /// </summary>
        public void Dispose(Action<GraphicsBuffer> giveUp)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _forwardArgumentBuffer?.Dispose();
            _shadowArgumentBuffer?.Dispose();
            GiveUp(giveUp, _instanceBuffer);
            GiveUp(giveUp, _instanceClipBuffer);
            _forwardStaging?.Dispose();
            _shadowStaging?.Dispose();
            GiveUp(giveUp, _cullCommandBuffer);
            _cullStaging?.Dispose();
            if (_cullViews != null)
            {
                for (int v = 0; v < _cullViews.Length; v++)
                {
                    GiveUp(giveUp, _cullViews[v].forwardVisible);
                    GiveUp(giveUp, _cullViews[v].forwardArguments);
                    GiveUp(giveUp, _cullViews[v].shadowVisible);
                    GiveUp(giveUp, _cullViews[v].shadowArguments);
                }
            }
        }

        private static void GiveUp(Action<GraphicsBuffer> giveUp, GraphicsBuffer buffer)
        {
            if (buffer == null) return;
            if (giveUp != null) giveUp(buffer); else buffer.Dispose();
        }

        private CullView View(int view)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            if (view < 0 || view >= _cullViews.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(view), view, "The view must be one of this batch's.");
            }

            return _cullViews[view];
        }

        private void ThrowIfCulled()
        {
            if (_cull != null)
            {
                throw new InvalidOperationException(
                    "this batch selects its instances on the GPU: it has no CPU-written arguments, and it draws for a view");
            }
        }

        private void ThrowIfNotCulled()
        {
            if (_cull == null)
            {
                throw new InvalidOperationException("this batch was not made to select its instances on the GPU");
            }
        }

        /// <summary>
        /// Checks the command range and binds the shared buffers. False when there is nothing to issue; throws when the
        /// commands are not within the uploaded ones.
        /// </summary>
        private bool BindCommands(MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int startCommand, int commandCount)
        {
            ThrowIfDisposed();
            if (startCommand < 0 || commandCount < 0 || startCommand > CommandCount - commandCount)
            {
                throw new ArgumentOutOfRangeException(nameof(commandCount), commandCount, "The commands must be within the uploaded ones.");
            }

            if (commandCount == 0)
            {
                return false;
            }

            properties.SetBuffer(VerticesId, buffers.VertexBuffer);
            properties.SetBuffer(InstanceObjectToWorldId, _instanceBuffer);

            // Bound here, where the forward call and the shadow call both pass, so within one registration neither
            // can read a different plane, side or offset from the other. It binds the buffer; it does not stop a
            // caller uploading again between registrations, which is the callers own business to get right.
            properties.SetBuffer(InstanceClipId, _instanceClipBuffer);
            return true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpIndexedIndirectDrawBatch));
            }
        }
    }
}
