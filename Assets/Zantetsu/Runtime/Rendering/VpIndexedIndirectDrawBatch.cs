#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
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
        /// count -- eight float4s, one float and three padding floats, 144 bytes. Fixed whatever the count is: no instance takes a smaller
        /// or a larger record.
        /// </summary>
        public const int InstanceClipStride = 144;

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

            // The compacted entries and count slots of each column (VpCommandCompaction), made only when a route asks
            // for them. On Vulkan one set a frame of a ring (CompactionRing): a frame's compaction never writes the set
            // a frame still on the GPU may be drawing from. On Direct3D 12 one set (CompactionSets). GPU-written alone.
            public Compaction[] compactions;

            public void Dispose()
            {
                forwardVisible?.Dispose();
                forwardArguments?.Dispose();
                shadowVisible?.Dispose();
                shadowArguments?.Dispose();
                if (compactions != null) foreach (Compaction c in compactions) c?.Dispose();
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
                    forwardArgumentBuffer = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size, "VP Indexed Indirect Forward Arguments");
                    shadowArgumentBuffer = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, GraphicsBuffer.IndirectDrawIndexedArgs.size, "VP Indexed Indirect Shadow Arguments");
                }
                else
                {
                    cullCommandBuffer = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, commandCapacity, VpCullCommand.Stride, "VP Cull Commands");
                    cullViews = new CullView[culling.ViewCapacity];
                    for (int v = 0; v < cullViews.Length; v++)
                    {
                        // The arguments are plain indirect-argument buffers, as VP Stage 3's are: on D3D11 the compute
                        // pass writes such a buffer through a RWByteAddressBuffer as the draw's shader reads it through a
                        // ByteAddressBuffer (UnityIndirect.cginc). Asking for Raw or Structured as well is refused or
                        // unneeded there (probed 2026-10-06: perf/gpu-cull/probe-args).
                        // An entry is Unity's five arguments, or -- for a batch the Direct3D 12 plugin draws -- the
                        // command's number and then the five (DESIGN 4.5.8): the stride is the setup's.
                        cullViews[v].forwardVisible = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, sizeof(uint), "VP Cull Forward Visible " + v);
                        cullViews[v].forwardArguments = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, culling.ArgumentStride, "VP Cull Forward Arguments " + v);
                        cullViews[v].shadowVisible = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, sizeof(uint), "VP Cull Shadow Visible " + v);
                        cullViews[v].shadowArguments = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, culling.ArgumentStride, "VP Cull Shadow Arguments " + v);
                    }
                }

                instanceBuffer = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceStride, "VP Indexed Instance Transforms");
                instanceClipBuffer = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, instanceCapacity, InstanceClipStride, "VP Indexed Instance Clips");
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

        // ---- the command compaction (DESIGN 4.5.10 Quest 3S / Vulkan; 4.5.11 Direct3D 12) --------------------------
        // The compacted entries and the count slots of one column of one selection (a view's, or a caller-owned
        // output's): GPU-written alone, by VpCommandCompaction, between the selection and the draws. The fixed
        // entries stay as the selection wrote them; a route that does not ask for this never makes it.
        //
        // What a set holds is its device's route's: on Vulkan three columns (the body, the casters, the motion
        // vectors) and a copy of each column's counts for the draws to read; on Direct3D 12 two columns (there is no
        // motion pass), count buffers a copy can zero, and no copy of the counts (the draw reads what the compute
        // wrote, behind the resource state transition between the two).

        /// <summary>The compacted entries and count slots of a selection's columns. Made on first use; never written by the CPU after that.</summary>
        public sealed class Compaction : IDisposable
        {

            // The clock reading that begins an observed span: nothing in a Release Player (DESIGN 21.18).
            private static long Began()
            {
#if ZANTETSU_OBSERVE
                return System.Diagnostics.Stopwatch.GetTimestamp();
#else
                return 0;
#endif
            }

            // The seconds since an observed span began: 0 in a Release Player (DESIGN 21.18).
            private static double SecondsSince(long began)
            {
#if ZANTETSU_OBSERVE
                return (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
#else
                return 0.0;
#endif
            }
            /// <summary>How many columns a set of this device's route has: 3 on Vulkan (body, casters, motion), 2 on Direct3D 12 (body, casters).</summary>
            public static int Columns => VpCommandCompaction.Layout.Columns;

            /// <summary>Whether a set keeps a copy of each column's counts for the draws (the Vulkan route's way; see VpCommandCompaction.DrawFromCopy).</summary>
            public static bool HasDrawCounts => VpCommandCompaction.Layout.DrawCounts;

            public GraphicsBuffer ForwardEntries { get; private set; }
            public GraphicsBuffer ForwardCounts { get; private set; }
            public GraphicsBuffer ShadowEntries { get; private set; }
            public GraphicsBuffer ShadowCounts { get; private set; }
            // The motion pass's own column: with no selection of its own (no AppSW distance) it compacts the view's
            // body entries too, and compacting them into the body's buffers again doubled the count (icB7, 2026-10-10).
            public GraphicsBuffer MotionEntries { get; private set; }
            public GraphicsBuffer MotionCounts { get; private set; }

            /// <summary>Column 0 the body, 1 the casters, 2 the motion.</summary>
            public GraphicsBuffer Entries(int column) => column == 1 ? ShadowEntries : column == 2 ? MotionEntries : ForwardEntries;
            public GraphicsBuffer Counts(int column) => column == 1 ? ShadowCounts : column == 2 ? MotionCounts : ForwardCounts;

            // What the draws read their counts from (VpCommandCompaction.DrawFromCopy): a slot a run, into which the
            // plugin copies the run's count by a transfer just before the draw. The compute never writes these.
            public GraphicsBuffer ForwardDrawCounts { get; private set; }
            public GraphicsBuffer ShadowDrawCounts { get; private set; }
            public GraphicsBuffer MotionDrawCounts { get; private set; }

            /// <summary>Column 0 the body, 1 the casters, 2 the motion: the copies of the counts, read by the draws.</summary>
            public GraphicsBuffer DrawCounts(int column) => column == 1 ? ShadowDrawCounts : column == 2 ? MotionDrawCounts : ForwardDrawCounts;

            // The frame for which each column's count slots were reserved for zeroing (the plugin's zero event taken
            // and finished): a compaction of a column is refused for any other frame, so that a set whose zeroing
            // could not be reserved (the event ring full) is never added to (TL review, 2026-10-10).
            private readonly int[] _zeroReservedFrame = { int.MinValue, int.MinValue, int.MinValue };

            /// <summary>The zero event of <paramref name="column"/>'s count slots is taken for <paramref name="frame"/>.</summary>
            public void MarkZeroReserved(int column, int frame) => _zeroReservedFrame[column] = frame;

            /// <summary>Whether <paramref name="column"/>'s count slots are reserved for zeroing in <paramref name="frame"/>.</summary>
            public bool ZeroReserved(int column, int frame) => column >= 0 && column < 3 && _zeroReservedFrame[column] == frame;

            /// <summary>
            /// The GPU bytes of one set of this device's route: its columns of entries and of count slots, and on Vulkan
            /// of the counts' copies too. No CPU copy is kept.
            /// </summary>
            public static long BytesFor(int commandCapacity, int stride)
            {
                return Columns * (((long)commandCapacity * stride) + ((HasDrawCounts ? 2L : 1L) * VpCommandCompaction.Slots * sizeof(uint)));
            }

            internal Compaction(int commandCapacity, int stride, string name)
            {
                long began = Began();
                try
                {
                    // The counts are written here, once, so that no slot is ever read before something set it; after
                    // that the GPU alone writes them (the plugin's zero event on Vulkan, a copy on Direct3D 12).
                    ForwardEntries = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, stride, "VP Compact Forward Entries " + name);
                    ForwardCounts = VpCommandCompaction.NewCounts("VP Compact Forward Counts " + name);
                    ShadowEntries = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, stride, "VP Compact Shadow Entries " + name);
                    ShadowCounts = VpCommandCompaction.NewCounts("VP Compact Shadow Counts " + name);
                    ForwardCounts.SetData(VpCommandCompaction.ZeroSlots, "VpIndexedIndirectDrawBatch.cs:452");
                    ShadowCounts.SetData(VpCommandCompaction.ZeroSlots, "VpIndexedIndirectDrawBatch.cs:453");
                    if (Columns > 2)
                    {
                        MotionEntries = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, commandCapacity, stride, "VP Compact Motion Entries " + name);
                        MotionCounts = VpCommandCompaction.NewCounts("VP Compact Motion Counts " + name);
                        MotionCounts.SetData(VpCommandCompaction.ZeroSlots, "VpIndexedIndirectDrawBatch.cs:458");
                    }

                    if (HasDrawCounts)
                    {
                        ForwardDrawCounts = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, VpCommandCompaction.Slots, sizeof(uint), "VP Compact Forward Draw Counts " + name);
                        ShadowDrawCounts = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, VpCommandCompaction.Slots, sizeof(uint), "VP Compact Shadow Draw Counts " + name);
                        MotionDrawCounts = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, VpCommandCompaction.Slots, sizeof(uint), "VP Compact Motion Draw Counts " + name);
                        ForwardDrawCounts.SetData(VpCommandCompaction.ZeroSlots, "VpIndexedIndirectDrawBatch.cs:466");
                        ShadowDrawCounts.SetData(VpCommandCompaction.ZeroSlots, "VpIndexedIndirectDrawBatch.cs:467");
                        MotionDrawCounts.SetData(VpCommandCompaction.ZeroSlots, "VpIndexedIndirectDrawBatch.cs:468");
                    }

                    VpCommandCompaction.NoteSetMade(BytesFor(commandCapacity, stride),
                        SecondsSince(began));
                }
                catch { Dispose(); throw; }
            }

            public void Dispose() => GiveUp(null);

            internal void GiveUp(Action<GraphicsBuffer> giveUp)
            {
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, ForwardEntries); ForwardEntries = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, ForwardCounts); ForwardCounts = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, ShadowEntries); ShadowEntries = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, ShadowCounts); ShadowCounts = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, MotionEntries); MotionEntries = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, MotionCounts); MotionCounts = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, ForwardDrawCounts); ForwardDrawCounts = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, ShadowDrawCounts); ShadowDrawCounts = null;
                VpIndexedIndirectDrawBatch.GiveUp(giveUp, MotionDrawCounts); MotionDrawCounts = null;
            }
        }

        /// <summary>
        /// Sets of compaction buffers a selection keeps, one a frame in turn: frame F writes set F mod this, so the
        /// set a frame still on the GPU draws from is never the one being written (a torn entry -- half of one
        /// command, half of another -- was seen on Quest 3S with one set, 2026-10-10: the compacted positions change
        /// from frame to frame with the waves' order, unlike the selection's own entries).
        /// </summary>
        public const int CompactionRing = 4;

        /// <summary>
        /// How many sets a selection keeps on this device: the ring (<see cref="CompactionRing"/>) on Vulkan, one on
        /// Direct3D 12 (DESIGN 4.5.11). There every write and every read of a set is a command of one queue -- the
        /// copy that zeroes the counts, the compaction's dispatch, the draw -- executed in the order recorded and
        /// each behind the resource state it needs, and the CPU never writes a set: a frame still on the GPU has its
        /// draws ahead of the next frame's copy and dispatch, as it has them ahead of the next frame's selection,
        /// whose entries were always one buffer a view.
        /// </summary>
        public static int CompactionSets => VpCommandCompaction.Layout.Sets;

        /// <summary>The compaction buffers of view <paramref name="view"/> for <paramref name="frame"/>, made on first asking. For a batch the plugin draws.</summary>
        public Compaction CompactionOf(int view, int frame)
        {
            View(view);
            if (!_cull.NativeArguments) throw new InvalidOperationException("this batch writes Unity's argument entries; the plugin cannot draw from them");
            ref Compaction[] ring = ref _cullViews[view].compactions;
            int sets = CompactionSets;
            ring ??= new Compaction[sets];
            int slot = (frame % sets + sets) % sets;
            return ring[slot] ??= new Compaction(CommandCapacity, _cull.ArgumentStride, "view " + view + " set " + slot);
        }

        /// <summary>The compaction buffers of a caller-owned selection (<see cref="CreateCullOutput"/>) for <paramref name="frame"/>, made on first asking.</summary>
        public Compaction CompactionOf(CullOutput output, int frame)
        {
            ThrowIfDisposed(); ThrowIfNotCulled();
            if (output == null || !ReferenceEquals(output.owner, this) || output.ForwardArguments == null)
                throw new ArgumentException("A live selection belonging to this batch is required.");
            int sets = CompactionSets;
            output.compactions ??= new Compaction[sets];
            int slot = (frame % sets + sets) % sets;
            return output.compactions[slot] ??= new Compaction(CommandCapacity, _cull.ArgumentStride, "output set " + slot);
        }

        /// <summary>The experiment's check of the run just compacted (VpCommandCompaction.Verify), recorded after <see cref="IssueCompaction"/>.</summary>
        public void IssueVerification(CommandBuffer commands, Compaction compaction, GraphicsBuffer source, int column, int firstCommand, int commandCount, int slot)
        {
            ThrowIfDisposed(); ThrowIfNotCulled();
            VpCommandCompaction.Verify(commands, source, compaction.Entries(column), compaction.Counts(column), firstCommand, commandCount, slot, column);
        }

        /// <summary>
        /// Compacts the run [<paramref name="firstCommand"/>, +<paramref name="commandCount"/>) of the selection's entries
        /// of one column (<paramref name="source"/>: the fixed argument buffer the draw would read) into the compaction's
        /// entries of that column, counted in <paramref name="slot"/>. After the selection's dispatch, before the draw.
        /// </summary>
        public void IssueCompaction(CommandBuffer commands, Compaction compaction, GraphicsBuffer source, int column, int firstCommand, int commandCount, int slot, int frame)
        {
            ThrowIfDisposed(); ThrowIfNotCulled();
            if (firstCommand < 0 || commandCount <= 0 || firstCommand > CommandCount - commandCount)
                throw new ArgumentOutOfRangeException(nameof(commandCount), commandCount, "The run must be within the uploaded commands.");
            if (!compaction.ZeroReserved(column, frame))
                throw new InvalidOperationException("the column's count slots are not reserved for zeroing in this frame: the run is not compacted (it draws fixed)");
            VpCommandCompaction.Compact(commands, source, compaction.Entries(column), compaction.Counts(column), firstCommand, commandCount, slot);
        }

        // ---- the clip list (DESIGN 4.5.9) ----------------------------------------------------------------------
        // The clip route of a batch that selects on the GPU is drawn apart from the selection, so that the
        // selection's own draws need no clip at all: the owner says which COMMANDS are of it, with the commands
        // (VpIndirectCommand.clipRoute; the selection then keeps nothing of them and reads no clip record to know
        // it), and gives their instances here as a list, one argument entry an instance, which is drawn by the
        // variant that reads no selection -- the instance's transform and clip record from the very buffers the
        // selection's draws read, so an instance that only moves needs nothing sent for it. A command with any
        // clipped instance is of the route whole: its instances with no plane are in the list too and are drawn by
        // the same variant, which clips nothing of a record of no plane.
        //
        // The plugin's draw (Direct3D 12) reads ONE pair of argument buffers, written when the list is given and not
        // again: which entries a camera keeps is settled by which runs of them are issued. Unity's own draw cannot
        // leave a command out once it is registered, so there the entries are kept A VIEW EACH (a camera's, as the
        // selection's results are): that view's selection writes an instance count of zero into the entries it does
        // not keep (WriteClipSelection), and no other camera's writing reaches them.
        private GraphicsBuffer _clipForwardArguments;
        private GraphicsBuffer _clipShadowArguments;

        // Unity's draw only: the bounds of each entry's command in the geometry's own frame (VpCullCommand records,
        // read by the entry's number), for the shadow draw's per-slice judgement. Sent with the list, not when an
        // entry moves: the transform is read from the instance buffer.
        private GraphicsBuffer _clipBounds;
        private ClipView[] _clipViews;
        private ClipEntry[] _clipList = Array.Empty<ClipEntry>();
        private int _clipListSerial;

        private sealed class ClipView
        {
            public GraphicsBuffer forward, shadow;
            public uint[] forwardWords = Array.Empty<uint>(), shadowWords = Array.Empty<uint>();

            // What the buffers hold now, an entry: bit 0 the body drawn, bit 1 the caster drawn.
            public byte[] written = Array.Empty<byte>();
            public int serial = -1;
        }

        /// <summary>
        /// One entry of the clip list: an instance of a command of the clip route, by the index range of its command,
        /// the number of its instance record and its command's bounds in the geometry's own frame.
        /// </summary>
        public readonly struct ClipEntry
        {
            public readonly VpGeometryRange range;
            public readonly int instance;
            public readonly Bounds localBounds;

            public ClipEntry(VpGeometryRange range, int instance, Bounds localBounds = default)
            {
                this.range = range;
                this.instance = instance;
                this.localBounds = localBounds;
            }
        }

        /// <summary>How many entries the clip list holds now; zero until one is uploaded, and after an empty one.</summary>
        public int ClipEntryCount { get; private set; }

        /// <summary>Whether the clip list's forward entries hold two physical instances an entry (see <see cref="SinglePassInstanced"/>).</summary>
        public bool ClipEntriesSinglePassInstanced { get; private set; }

        /// <summary>Raised by every upload of the clip list, for a reader that keeps the native pointer (DESIGN 4.5.8).</summary>
        public uint ClipArgumentGeneration { get; private set; }

        /// <summary>Observation: how many times a clip list was given to the GPU. An empty list sends nothing.</summary>
        public long ClipArgumentUploads { get; private set; }

        /// <summary>
        /// Observation: how many writes of clip list data were made to the GPU -- the list's own (the plugin's two
        /// argument buffers, or the bounds of Unity's), a view's entries written whole, and a view's entries written
        /// again by its selection -- and the bytes of them all.
        /// </summary>
        public long ClipArgumentWrites { get; private set; }
        public long ClipArgumentBytes { get; private set; }

        /// <summary>Observation: of those, the writes a view's selection made (<see cref="WriteClipSelection"/>).</summary>
        public long ClipSelectionWrites { get; private set; }

        /// <summary>Observation: of those, the writes that put a view's entries back whole before a frame's selection (<see cref="RestoreClipEntries"/>).</summary>
        public long ClipRestoreWrites { get; private set; }

        /// <summary>
        /// Takes the clip list: <paramref name="count"/> entries, each the index range of its command and the
        /// number of its instance record. The forward entry of an instance draws one physical instance, or two under
        /// Single Pass Instanced, starting at the instance's own record (times two); the shadow entry draws the one
        /// logical instance, as the shadow arguments always do. An entry is Unity's five arguments, or -- for a batch
        /// the plugin draws -- its own number in this list and then the five (the entry is read by that number).
        /// The buffers are made at the first non-empty list, for as many entries as the batch holds instances, and
        /// are the batch's until it ends. An empty list makes nothing, sends nothing and draws nothing.
        /// <para>
        /// For the plugin the two argument buffers are written here. For Unity's draw the entries' bounds are written
        /// here and each view's arguments when that view next draws the list (see the notes above).
        /// </para>
        /// </summary>
        public void UploadClipEntries(ClipEntry[] entries, int count)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            if (count < 0 || (count > 0 && (entries == null || count > entries.Length)) || count > InstanceCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(count), count, "The clip list must be within the entries given and the batch's instances.");
            }

            for (int k = 0; k < count; k++)
            {
                if (entries[k].instance < 0 || entries[k].instance >= InstanceCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(entries), entries[k].instance, "A clip entry names an instance the batch does not hold.");
                }
            }

            ClipEntryCount = count;
            ClipEntriesSinglePassInstanced = SinglePassInstanced;
            _clipListSerial++;
            if (count == 0)
            {
                return;
            }

            if (_clipList.Length < count)
            {
                _clipList = new ClipEntry[Math.Max(count, Math.Max(16, _clipList.Length * 2))];
            }

            Array.Copy(entries, _clipList, count);
            int stride = _cull.ArgumentStride;
            if (_cull.NativeArguments)
            {
                if (_clipForwardArguments == null)
                {
                    MakeClipArguments(out _clipForwardArguments, out _clipShadowArguments, "VP Clip List");
                }

                int words = stride / sizeof(uint);
                var forwardWords = new NativeArray<uint>(count * words, Allocator.Temp, NativeArrayOptions.ClearMemory);
                var shadowWords = new NativeArray<uint>(count * words, Allocator.Temp, NativeArrayOptions.ClearMemory);
                try
                {
                    uint multiplier = SinglePassInstanced ? 2u : 1u;
                    for (int k = 0; k < count; k++)
                    {
                        ClipEntry entry = entries[k];
                        int at = k * words;

                        // The plugin's entry begins with the number its shader reads the entry back by.
                        forwardWords[at] = (uint)k;
                        shadowWords[at] = (uint)k;
                        at++;
                        forwardWords[at] = (uint)entry.range.indexCount;
                        forwardWords[at + 1] = multiplier;
                        forwardWords[at + 2] = (uint)entry.range.indexStart;
                        forwardWords[at + 3] = 0u;
                        forwardWords[at + 4] = (uint)entry.instance * multiplier;
                        shadowWords[at] = (uint)entry.range.indexCount;
                        shadowWords[at + 1] = 1u;
                        shadowWords[at + 2] = (uint)entry.range.indexStart;
                        shadowWords[at + 3] = 0u;
                        shadowWords[at + 4] = (uint)entry.instance;
                    }

                    _clipForwardArguments.SetData(forwardWords, 0, 0, count * words, "VpIndexedIndirectDrawBatch.cs:716");
                    _clipShadowArguments.SetData(shadowWords, 0, 0, count * words, "VpIndexedIndirectDrawBatch.cs:717");
                }
                finally
                {
                    forwardWords.Dispose();
                    shadowWords.Dispose();
                }

                ClipArgumentWrites += 2;
                ClipArgumentBytes += 2L * count * stride;
            }
            else
            {
                if (_clipBounds == null)
                {
                    _clipBounds = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, InstanceCapacity, VpCullCommand.Stride, "VP Clip List Bounds");
                }

                var bounds = new NativeArray<VpCullCommand>(count, Allocator.Temp, NativeArrayOptions.ClearMemory);
                try
                {
                    for (int k = 0; k < count; k++)
                    {
                        ClipEntry entry = entries[k];
                        bounds[k] = new VpCullCommand
                        {
                            indexCount = (uint)entry.range.indexCount,
                            startIndex = (uint)entry.range.indexStart,
                            startInstance = (uint)entry.instance,
                            instanceCount = 1u,
                            centre = entry.localBounds.center,
                            extents = entry.localBounds.extents,
                        };
                    }

                    _clipBounds.SetData(bounds, 0, 0, count, "VpIndexedIndirectDrawBatch.cs:752");
                }
                finally
                {
                    bounds.Dispose();
                }

                ClipArgumentWrites++;
                ClipArgumentBytes += (long)count * VpCullCommand.Stride;
            }

            ClipArgumentGeneration++;
            ClipArgumentUploads++;
        }

        private void MakeClipArguments(out GraphicsBuffer forward, out GraphicsBuffer shadow, string name)
        {
            forward = null;
            shadow = null;
            GraphicsBuffer made = null;
            try
            {
                made = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, InstanceCapacity, _cull.ArgumentStride, name + " Forward Arguments");
                shadow = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, InstanceCapacity, _cull.ArgumentStride, name + " Shadow Arguments");
                forward = made;
            }
            catch
            {
                made?.Dispose();
                throw;
            }
        }

        // Unity's draw: view <paramref name="view"/>'s entries hold the list given last, every entry drawn, unless they
        // already hold it (with whatever that view's selection wrote since). Written at once, when the view's draws
        // are registered, so that a draw whose selection is never issued still reads entries of this list.
        private ClipView EnsureClipView(int view)
        {
            if (_cull.NativeArguments)
            {
                throw new InvalidOperationException("this batch writes the plugin's argument entries; Unity's own draw cannot read them");
            }

            if (view < 0 || view >= _cull.ViewCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(view), view, "The view must be one of the selection's.");
            }

            _clipViews ??= new ClipView[_cull.ViewCapacity];
            ClipView slot = _clipViews[view] ??= new ClipView();
            int count = ClipEntryCount;
            if (slot.serial == _clipListSerial || count == 0)
            {
                return slot;
            }

            if (slot.forward == null)
            {
                MakeClipArguments(out slot.forward, out slot.shadow, "VP Clip List View " + view);
            }

            const int words = VpGpuCullSetup.UnityArgumentStride / sizeof(uint);
            if (slot.forwardWords.Length < count * words)
            {
                int room = Math.Max(count, Math.Max(16, slot.written.Length * 2));
                slot.forwardWords = new uint[room * words];
                slot.shadowWords = new uint[room * words];
                slot.written = new byte[room];
            }

            uint multiplier = ClipEntriesSinglePassInstanced ? 2u : 1u;
            for (int k = 0; k < count; k++)
            {
                ClipEntry entry = _clipList[k];
                int at = k * words;
                slot.forwardWords[at] = (uint)entry.range.indexCount;
                slot.forwardWords[at + 1] = multiplier;
                slot.forwardWords[at + 2] = (uint)entry.range.indexStart;
                slot.forwardWords[at + 3] = 0u;
                slot.forwardWords[at + 4] = (uint)entry.instance * multiplier;
                slot.shadowWords[at] = (uint)entry.range.indexCount;
                slot.shadowWords[at + 1] = 1u;
                slot.shadowWords[at + 2] = (uint)entry.range.indexStart;
                slot.shadowWords[at + 3] = 0u;
                slot.shadowWords[at + 4] = (uint)entry.instance;
                slot.written[k] = 3;
            }

            slot.forward.SetData(slot.forwardWords, 0, 0, count * words, "VpIndexedIndirectDrawBatch.cs:840");
            slot.shadow.SetData(slot.shadowWords, 0, 0, count * words, "VpIndexedIndirectDrawBatch.cs:841");
            slot.serial = _clipListSerial;
            ClipArgumentWrites += 2;
            ClipArgumentBytes += 2L * count * VpGpuCullSetup.UnityArgumentStride;
            return slot;
        }

        /// <summary>
        /// Unity's draw: view <paramref name="view"/>'s entries drawn whole again -- every entry with its instance
        /// count -- before the draws of a new frame are registered for that view. **A frame with no selection keeps
        /// everything, in the entries themselves**: what an earlier frame's selection wrote zero does not outlast that
        /// frame; a selection issued for this frame writes its zeros again after this (<see cref="WriteClipSelection"/>).
        /// Written at once, as the view's entries are when it first draws a list. Nothing is written when no entry
        /// holds a zero. A batch the plugin draws has nothing to put back. Returns the bytes written.
        /// </summary>
        public int RestoreClipEntries(int view)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            int count = ClipEntryCount;
            if (_cull.NativeArguments || count == 0)
            {
                return 0;
            }

            ClipView slot = EnsureClipView(view);
            const int words = VpGpuCullSetup.UnityArgumentStride / sizeof(uint);
            uint multiplier = ClipEntriesSinglePassInstanced ? 2u : 1u;
            bool forwardChanged = false, shadowChanged = false;
            for (int k = 0; k < count; k++)
            {
                byte held = slot.written[k];
                if ((held & 1) == 0)
                {
                    slot.forwardWords[k * words + 1] = multiplier;
                    forwardChanged = true;
                }

                if ((held & 2) == 0)
                {
                    slot.shadowWords[k * words + 1] = 1u;
                    shadowChanged = true;
                }

                slot.written[k] = 3;
            }

            int bytes = 0;
            if (forwardChanged)
            {
                slot.forward.SetData(slot.forwardWords, 0, 0, count * words, "VpIndexedIndirectDrawBatch.cs:891");
                bytes += count * VpGpuCullSetup.UnityArgumentStride;
                ClipArgumentWrites++;
                ClipRestoreWrites++;
            }

            if (shadowChanged)
            {
                slot.shadow.SetData(slot.shadowWords, 0, 0, count * words, "VpIndexedIndirectDrawBatch.cs:899");
                bytes += count * VpGpuCullSetup.UnityArgumentStride;
                ClipArgumentWrites++;
                ClipRestoreWrites++;
            }

            ClipArgumentBytes += bytes;
            return bytes;
        }

        /// <summary>
        /// Unity's draw: writes into view <paramref name="view"/>'s entries what <paramref name="selection"/> keeps for
        /// that view's camera -- the instance count of an entry whose body is not kept, and of an entry that casts into
        /// no slice, is zero; an entry kept again has its count back. The write is put into
        /// <paramref name="commands"/>, the camera's own selection pass, so it is executed before that camera's draws
        /// and after every draw issued before it; the words written are the view's own and are next touched when that
        /// view's draws are registered again (the command buffer holds what was recorded: a later change does not
        /// reach a write already recorded). Nothing is written when the entries already hold what is asked. A batch the plugin
        /// draws writes nothing here: its runs are chosen by whoever issues them. Returns the bytes written.
        /// </summary>
        public int WriteClipSelection(UnityEngine.Rendering.CommandBuffer commands, int view, VpClipSelection selection)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            if (commands == null)
            {
                throw new ArgumentNullException(nameof(commands));
            }

            if (selection == null)
            {
                throw new ArgumentNullException(nameof(selection));
            }

            int count = ClipEntryCount;
            if (_cull.NativeArguments || count == 0 || _clipViews == null || view < 0 || view >= _clipViews.Length)
            {
                return 0;
            }

            ClipView slot = _clipViews[view];
            if (slot == null || slot.serial != _clipListSerial || selection.Count != count)
            {
                // No draw of this list was registered for the view, or the selection is of another list: nothing of
                // it can be said of these entries, which stay as they are (every entry drawn when written whole).
                return 0;
            }

            const int words = VpGpuCullSetup.UnityArgumentStride / sizeof(uint);
            uint multiplier = ClipEntriesSinglePassInstanced ? 2u : 1u;
            bool forwardChanged = false, shadowChanged = false;
            for (int k = 0; k < count; k++)
            {
                byte wanted = (byte)((selection.BodyKept(k) ? 1 : 0) | (selection.CasterKept(k) ? 2 : 0));
                byte held = slot.written[k];
                if (wanted == held)
                {
                    continue;
                }

                if (((wanted ^ held) & 1) != 0)
                {
                    slot.forwardWords[k * words + 1] = (wanted & 1) != 0 ? multiplier : 0u;
                    forwardChanged = true;
                }

                if (((wanted ^ held) & 2) != 0)
                {
                    slot.shadowWords[k * words + 1] = (wanted & 2) != 0 ? 1u : 0u;
                    shadowChanged = true;
                }

                slot.written[k] = wanted;
            }

            int bytes = 0;
            if (forwardChanged)
            {
                if (VpGpuStop.CommandWrite()) commands.SetBufferData(slot.forward, slot.forwardWords, 0, 0, count * words);
                bytes += count * VpGpuCullSetup.UnityArgumentStride;
                ClipArgumentWrites++;
                ClipSelectionWrites++;
            }

            if (shadowChanged)
            {
                if (VpGpuStop.CommandWrite()) commands.SetBufferData(slot.shadow, slot.shadowWords, 0, 0, count * words);
                bytes += count * VpGpuCullSetup.UnityArgumentStride;
                ClipArgumentWrites++;
                ClipSelectionWrites++;
            }

            ClipArgumentBytes += bytes;
            return bytes;
        }

        /// <summary>
        /// The forward call of the clip list's entries [firstEntry, firstEntry + entryCount) for view
        /// <paramref name="view"/>, by Unity's own call: <paramref name="forwardMaterial"/> is of the variant that
        /// reads no selection and evaluates the clip record. <paramref name="worldBounds"/> is what Unity's own
        /// culling of the call goes by: the batch's (<see cref="WorldBounds"/>), as for the selection's own calls, so
        /// that Unity's culling is no further selection of the entries. Issues nothing for no entry.
        /// </summary>
        public void RenderClipForward(
            Material forwardMaterial, MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int layer,
            int firstEntry, int entryCount, Camera camera, int view, Bounds worldBounds)
        {
            if (!BindClipEntries(properties, buffers, firstEntry, entryCount))
            {
                return;
            }

            ClipView slot = EnsureClipView(view);
            properties.SetInteger(InstanceMultiplierId, ClipEntriesSinglePassInstanced ? 2 : 1);
            var renderParams = new RenderParams(forwardMaterial)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = worldBounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = true,
                lightProbeUsage = LightProbeUsage.Off,
                reflectionProbeUsage = ReflectionProbeUsage.Off,
            };
            if (VpGpuStop.Draw()) Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, slot.forward, entryCount, firstEntry);
        }

        /// <summary>
        /// The shadow call of the clip list's entries, by Unity's own call; see <see cref="RenderClipForward"/>. The
        /// entries' bounds are bound for the per-slice judgement of a material of
        /// <see cref="VpGpuCullSetup.ClipListSliceKeyword"/>, made as the selection's casters make it.
        /// </summary>
        public void RenderClipShadows(
            Material shadowMaterial, MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int layer,
            int firstEntry, int entryCount, Camera camera, int view, Bounds worldBounds)
        {
            if (!BindClipEntries(properties, buffers, firstEntry, entryCount))
            {
                return;
            }

            ClipView slot = EnsureClipView(view);
            properties.SetBuffer(CullCommandsId, _clipBounds);
            properties.SetFloat(ShadowSliceSelectionId, VpGpuCullSetup.ShadowSliceSelection ? 1f : 0f);
            var renderParams = new RenderParams(shadowMaterial)
            {
                camera = camera,
                layer = layer,
                matProps = properties,
                worldBounds = worldBounds,
                shadowCastingMode = ShadowCastingMode.On,
                receiveShadows = false,
            };
            if (VpGpuStop.Draw()) Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, slot.shadow, entryCount, firstEntry);
        }

        /// <summary>
        /// What the plugin's draw of the clip list binds (DESIGN 4.5.8, 4.5.9): the list's entries for the body and
        /// for the casters, and the shared instance and clip buffers. Not to be written through.
        /// </summary>
        public void NativeClipDrawBuffers(
            out GraphicsBuffer forwardArguments, out GraphicsBuffer shadowArguments, out GraphicsBuffer instances, out GraphicsBuffer instanceClips)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            if (!_cull.NativeArguments)
            {
                throw new InvalidOperationException("this batch writes Unity's argument entries; the plugin cannot draw from them");
            }

            forwardArguments = _clipForwardArguments;
            shadowArguments = _clipShadowArguments;
            instances = _instanceBuffer;
            instanceClips = _instanceClipBuffer;
        }

        /// <summary>
        /// The clip list's argument buffers, for reading back by tests: the plugin's one pair, or view
        /// <paramref name="view"/>'s of Unity's (written whole for the list given last if they do not hold it yet).
        /// Null until a non-empty list was uploaded.
        /// </summary>
        internal GraphicsBuffer ClipForwardArgumentsForTest(int view = 0)
        {
            return _cull.NativeArguments ? _clipForwardArguments : ClipEntryCount == 0 && (_clipViews == null || _clipViews[view] == null) ? null : EnsureClipView(view).forward;
        }

        internal GraphicsBuffer ClipShadowArgumentsForTest(int view = 0)
        {
            return _cull.NativeArguments ? _clipShadowArguments : ClipEntryCount == 0 && (_clipViews == null || _clipViews[view] == null) ? null : EnsureClipView(view).shadow;
        }

        internal GraphicsBuffer ClipBoundsForTest => _clipBounds;

        private bool BindClipEntries(MaterialPropertyBlock properties, VpGpuIndexedGeometryBuffers buffers, int firstEntry, int entryCount)
        {
            ThrowIfDisposed();
            ThrowIfNotCulled();
            if (firstEntry < 0 || entryCount < 0 || firstEntry > ClipEntryCount - entryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(entryCount), entryCount, "The entries must be within the uploaded clip list.");
            }

            if (entryCount == 0)
            {
                return false;
            }

            properties.SetBuffer(VerticesId, buffers.VertexBuffer);
            properties.SetBuffer(InstanceObjectToWorldId, _instanceBuffer);
            properties.SetBuffer(InstanceClipId, _instanceClipBuffer);
            return true;
        }

        /// <summary>A caller-owned selection for an additional pass; release only after its draws finish.</summary>
        public sealed class CullOutput : IDisposable
        {
            internal readonly VpIndexedIndirectDrawBatch owner;
            public GraphicsBuffer ForwardArguments { get; private set; }
            public GraphicsBuffer ForwardVisible { get; private set; }
            internal GraphicsBuffer ShadowArguments, ShadowVisible;
            internal Compaction[] compactions;   // experiment (2026-10-10): made by CompactionOf(output, frame) when a route asks
            internal CullOutput(VpIndexedIndirectDrawBatch batch)
            {
                owner = batch;
                try
                {
                    ForwardArguments = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, batch.CommandCapacity, batch._cull.ArgumentStride, "VP AppSW distance arguments");
                    ForwardVisible = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, batch.InstanceCapacity, sizeof(uint), "VP AppSW distance visible");
                    ShadowArguments = VpGpuStop.Buffer(GraphicsBuffer.Target.IndirectArguments, batch.CommandCapacity, batch._cull.ArgumentStride, "VP AppSW unused caster arguments");
                    ShadowVisible = VpGpuStop.Buffer(GraphicsBuffer.Target.Structured, batch.InstanceCapacity, sizeof(uint), "VP AppSW unused caster visible");
                }
                catch { Dispose(); throw; }
            }
            public void Dispose()
            {
                ForwardArguments?.Dispose(); ForwardArguments = null;
                ForwardVisible?.Dispose(); ForwardVisible = null;
                ShadowArguments?.Dispose(); ShadowArguments = null;
                ShadowVisible?.Dispose(); ShadowVisible = null;
                if (compactions != null) foreach (Compaction c in compactions) c?.Dispose();
                compactions = null;
            }
        }

        public CullOutput CreateCullOutput() { ThrowIfDisposed(); ThrowIfNotCulled(); return new CullOutput(this); }

        public void IssueCull(CommandBuffer commands, CullOutput output, VpCullConditions conditions)
        {
            ThrowIfDisposed(); ThrowIfNotCulled();
            if (output == null || !ReferenceEquals(output.owner, this) || output.ForwardArguments == null)
                throw new ArgumentException("A live selection belonging to this batch is required.");
            if (commands == null || conditions == null) throw new ArgumentNullException();
            if (CommandCount == 0) return;
            DispatchCull(commands, new CullView { forwardArguments = output.ForwardArguments, forwardVisible = output.ForwardVisible,
                shadowArguments = output.ShadowArguments, shadowVisible = output.ShadowVisible }, conditions);
        }

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
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)instanceTotal, "VpIndexedIndirectDrawBatch.cs:1332");
                InstanceGeneration++;
                _instanceClipBuffer.SetData(clips, 0, 0, (int)instanceTotal, "VpIndexedIndirectDrawBatch.cs:1334");
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
                _instanceBuffer.SetData(objectToWorlds, instanceStart, instanceStart, count, "VpIndexedIndirectDrawBatch.cs:1436");
                InstanceGeneration++;
                _instanceClipBuffer.SetData(clips, instanceStart, instanceStart, count, "VpIndexedIndirectDrawBatch.cs:1438");
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
                    int calls = SendSlotCommands(read, starts, 0, commandEnd, singlePassInstanced, first ? VpUploadKind.Initial : VpUploadKind.Frame);   // a resend for a changed stereo condition is frame data
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
                        ArgumentSetDataCalls += SendSlotCommands(read, starts, from, to, singlePassInstanced, VpUploadKind.Frame);
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
                    _instanceBuffer.SetData(objectToWorlds, 0, 0, instanceEnd, "VpIndexedIndirectDrawBatch.cs:1617", VpUploadKind.Initial);
                    InstanceGeneration++;
                    _instanceClipBuffer.SetData(clips, 0, 0, instanceEnd, "VpIndexedIndirectDrawBatch.cs:1619", VpUploadKind.Initial);
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
                    _instanceBuffer.SetData(objectToWorlds, from, from, to - from, "VpIndexedIndirectDrawBatch.cs:1632");
                    InstanceGeneration++;
                    InstanceTransformSetDataCalls++;
                    _instanceClipBuffer.SetData(clips, from, from, to - from, "VpIndexedIndirectDrawBatch.cs:1635");
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
            ReadOnlySpan<VpIndirectCommand> commands, ReadOnlySpan<int> startInstances, int from, int to, bool singlePassInstanced, VpUploadKind kind)
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
                        flags = command.clipRoute ? VpCullCommand.ClipRouteFlag : 0u,
                        extents = command.localBounds.extents,
                    };
                }

                if (_cullStaging != null)
                {
                    _cullCommandBuffer.SetData(_cullStaging.First(to), from, from, count, "VpIndexedIndirectDrawBatch.cs:1706", kind);
                    CullCommandGeneration++;
                }
                else
                {
                    _cullCommandBuffer.SetData(_cullCommands, from, from, count, "VpIndexedIndirectDrawBatch.cs:1711", kind);
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
                _forwardArgumentBuffer.SetData(_forwardStaging.First(to), from, from, count, "VpIndexedIndirectDrawBatch.cs:1749");
                _shadowArgumentBuffer.SetData(_shadowStaging.First(to), from, from, count, "VpIndexedIndirectDrawBatch.cs:1750");
            }
            else
            {
                _forwardArgumentBuffer.SetData(_forwardArguments, from, from, count, "VpIndexedIndirectDrawBatch.cs:1754");
                _shadowArgumentBuffer.SetData(_shadowArguments, from, from, count, "VpIndexedIndirectDrawBatch.cs:1755");
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
                    _cullCommandBuffer.SetData(cullCommands, 0, 0, CommandCapacity, "VpIndexedIndirectDrawBatch.cs:1824", VpUploadKind.Initial);
                    CullCommandGeneration++;
                    for (int v = 0; v < _cullViews.Length; v++)
                    {
                        VpWholeWrite.Step("write a view's lists and arguments");
                        _cullViews[v].forwardVisible.SetData(visible, 0, 0, InstanceCapacity, "VpIndexedIndirectDrawBatch.cs:1829", VpUploadKind.Initial);
                        CullViewGeneration++;
                        _cullViews[v].forwardArguments.SetData(cullArguments, 0, 0, cullArguments.Length, "VpIndexedIndirectDrawBatch.cs:1831", VpUploadKind.Initial);
                        CullViewGeneration++;
                        _cullViews[v].shadowVisible.SetData(visible, 0, 0, InstanceCapacity, "VpIndexedIndirectDrawBatch.cs:1833", VpUploadKind.Initial);
                        CullViewGeneration++;
                        _cullViews[v].shadowArguments.SetData(cullArguments, 0, 0, cullArguments.Length, "VpIndexedIndirectDrawBatch.cs:1835", VpUploadKind.Initial);
                        CullViewGeneration++;
                    }
                }
                else
                {
                    VpWholeWrite.Step("write forward arguments");
                    _forwardArgumentBuffer.SetData(arguments, 0, 0, CommandCapacity, "VpIndexedIndirectDrawBatch.cs:1842", VpUploadKind.Initial);
                    VpWholeWrite.Step("write shadow arguments");
                    _shadowArgumentBuffer.SetData(arguments, 0, 0, CommandCapacity, "VpIndexedIndirectDrawBatch.cs:1844", VpUploadKind.Initial);
                }

                VpWholeWrite.Step("write transforms");
                _instanceBuffer.SetData(transforms, 0, 0, InstanceCapacity, "VpIndexedIndirectDrawBatch.cs:1848", VpUploadKind.Initial);
                InstanceGeneration++;
                VpWholeWrite.Step("write clips");
                _instanceClipBuffer.SetData(clips, 0, 0, InstanceCapacity, "VpIndexedIndirectDrawBatch.cs:1851", VpUploadKind.Initial);
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
                    _cullViews[v].forwardArguments.SetData(zeros, 0, 0, zeros.Length, "VpIndexedIndirectDrawBatch.cs:1917");
                    CullViewGeneration++;
                    _cullViews[v].shadowArguments.SetData(zeros, 0, 0, zeros.Length, "VpIndexedIndirectDrawBatch.cs:1919");
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
                _instanceBuffer.SetData(objectToWorlds, 0, 0, (int)instanceTotal, "VpIndexedIndirectDrawBatch.cs:2032");
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

                _instanceClipBuffer.SetData(_instanceClips, 0, 0, (int)instanceTotal, "VpIndexedIndirectDrawBatch.cs:2048");
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
                    _forwardArgumentBuffer.SetData(_forwardStaging.First(commandCount), 0, 0, commandCount, "VpIndexedIndirectDrawBatch.cs:2128");
                    _shadowArgumentBuffer.SetData(_shadowStaging.First(commandCount), 0, 0, commandCount, "VpIndexedIndirectDrawBatch.cs:2129");
                }
                else
                {
                    _forwardArgumentBuffer.SetData(_forwardArguments, 0, 0, commandCount, "VpIndexedIndirectDrawBatch.cs:2133");
                    _shadowArgumentBuffer.SetData(_shadowArguments, 0, 0, commandCount, "VpIndexedIndirectDrawBatch.cs:2134");
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
                    flags = command.clipRoute ? VpCullCommand.ClipRouteFlag : 0u,
                    extents = command.localBounds.extents,
                };
                startInstance += command.instanceCount;
            }

            if (commandCount > 0)
            {
                if (_cullStaging != null)
                {
                    _cullCommandBuffer.SetData(_cullStaging.First(commandCount), 0, 0, commandCount, "VpIndexedIndirectDrawBatch.cs:2171");
                    CullCommandGeneration++;
                }
                else
                {
                    _cullCommandBuffer.SetData(_cullCommands, 0, 0, commandCount, "VpIndexedIndirectDrawBatch.cs:2176");
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
        /// A command uploaded as of the clip route (<see cref="VpIndirectCommand.clipRoute"/>) keeps nothing in
        /// either list: whoever uploaded it so draws its instances apart. The selection knows it by the command alone
        /// -- it reads no clip record and no clip buffer is bound for it -- and a batch whose commands say nothing of
        /// the kind is selected whole, as before the separation.
        /// Every uploaded command's arguments are written, whatever was kept: a command of the clip route with a count
        /// of zero, so that what an earlier selection wrote for it while it was an ordinary one draws nothing. With no
        /// command uploaded there is nothing to draw and nothing is issued. The conditions are copied as they are now.
        /// </para>
        /// <para>
        /// Startup may opt into <paramref name="warmEmpty"/> to dispatch one group when CommandCount is zero.
        /// The existing, nonempty buffers remain bound, and CullCommands returns before reading or writing them.
        /// This prepares the compute pipeline without uploading commands or changing draw arguments.
        /// </para>
        /// </summary>
        public void IssueCull(CommandBuffer commands, int view, VpCullConditions conditions, bool warmEmpty = false)
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
            if (CommandCount == 0 && !warmEmpty)
            {
                return;
            }

            DispatchCull(commands, target, conditions);
        }

        private void DispatchCull(CommandBuffer commands, CullView target, VpCullConditions conditions)
        {
            if (!VpGpuStop.Dispatch()) return;   // stop stage 2: nothing of the cull is recorded, the parameters included
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
            // No clip buffer: the kernel declares none (which commands are the clip route's is in the commands).
            commands.SetComputeBufferParam(shader, kernel, ForwardVisibleId, target.forwardVisible);
            commands.SetComputeBufferParam(shader, kernel, ForwardArgumentsId, target.forwardArguments);
            commands.SetComputeBufferParam(shader, kernel, ShadowVisibleId, target.shadowVisible);
            commands.SetComputeBufferParam(shader, kernel, ShadowArgumentsId, target.shadowArguments);
            commands.DispatchCompute(shader, kernel, Math.Max(1, (CommandCount + 63) / 64), 1, 1);
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
            if (VpGpuStop.Draw()) Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, arguments, commandCount, startCommand);
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
            if (VpGpuStop.Draw()) Graphics.RenderPrimitivesIndexedIndirect(renderParams, MeshTopology.Triangles, buffers.IndexBuffer, arguments, commandCount, startCommand);
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
            GiveUp(giveUp, _clipForwardArguments);
            GiveUp(giveUp, _clipShadowArguments);
            GiveUp(giveUp, _clipBounds);
            if (_clipViews != null)
            {
                for (int v = 0; v < _clipViews.Length; v++)
                {
                    if (_clipViews[v] != null)
                    {
                        GiveUp(giveUp, _clipViews[v].forward);
                        GiveUp(giveUp, _clipViews[v].shadow);
                    }
                }
            }
            _cullStaging?.Dispose();
            if (_cullViews != null)
            {
                for (int v = 0; v < _cullViews.Length; v++)
                {
                    GiveUp(giveUp, _cullViews[v].forwardVisible);
                    GiveUp(giveUp, _cullViews[v].forwardArguments);
                    GiveUp(giveUp, _cullViews[v].shadowVisible);
                    GiveUp(giveUp, _cullViews[v].shadowArguments);
                    if (_cullViews[v].compactions != null) foreach (Compaction c in _cullViews[v].compactions) c?.GiveUp(giveUp);
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
