using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>How a fragment the display was given is being shown right now.</summary>
    public enum LogicalCutDisplayState
    {
        /// <summary>This display was never given that fragment, or has let it go.</summary>
        NotShown = 0,

        /// <summary>The whole body, unclipped: nothing is admitted against it.</summary>
        Whole = 1,

        /// <summary>
        /// A cut is admitted, but what the display needs is not ready yet — the anchor distribution has not been
        /// prepared — so the whole body is still what is drawn. This is **not** "the owner had no anchors": an
        /// unprepared distribution is nothing at all, and the difference is kept (DESIGN 7.1).
        /// </summary>
        AwaitingInputs = 2,

        /// <summary>
        /// The provisional split: the same parent geometry drawn once per side, each clipped to its own half. Before
        /// publication the sides belong to the operation; after it, to the two published children.
        /// </summary>
        ProvisionalSplit = 3,
    }

    /// <summary>
    /// One instance the display drew, and what it is: the whole body, or one side of a provisional split.
    /// </summary>
    public readonly struct LogicalCutDisplaySide
    {
        internal LogicalCutDisplaySide(
            LogicalFragmentId source,
            CutOperationId operation,
            float side,
            bool published,
            LogicalFragmentId fragment,
            bool fixedByAnchors,
            Vector3 offset,
            VpInstanceClip clip)
        {
            this.source = source;
            this.operation = operation;
            this.side = side;
            this.published = published;
            this.fragment = fragment;
            this.fixedByAnchors = fixedByAnchors;
            this.offset = offset;
            this.clip = clip;
        }

        /// <summary>The fragment this display entry was given, which is the body the geometry belongs to.</summary>
        public readonly LogicalFragmentId source;

        /// <summary>The admitted cut this side belongs to. Unset for a whole body.</summary>
        public readonly CutOperationId operation;

        /// <summary>+1 or -1 for a side of a split; 0 for a whole body.</summary>
        public readonly float side;

        /// <summary>
        /// Whether this side is a **published** child. Before publication a side has no child of its own: the display
        /// does not issue an id early and does not treat its own slot as a child that exists (DESIGN 7.1.2).
        /// </summary>
        public readonly bool published;

        /// <summary>The published child this side is, or an unset id before publication.</summary>
        public readonly LogicalFragmentId fragment;

        /// <summary>
        /// Whether the anchors make this side fixed, which is what decides it is not moved apart. Read from the
        /// prepared distribution before publication and from the published child afterwards; the display has no
        /// judgement of its own about anchors.
        /// </summary>
        public readonly bool fixedByAnchors;

        /// <summary>The separation this side is drawn at, in world space. Zero for a fixed side and a whole body.</summary>
        public readonly Vector3 offset;

        /// <summary>The clip record this side was drawn with.</summary>
        public readonly VpInstanceClip clip;
    }

    /// <summary>
    /// Shows one logical cut's provisional split from the ledger's own state (DESIGN 5.1, 7.1): the parent geometry,
    /// and once a cut is admitted and its inputs are ready, that **same** geometry drawn once per side, each clipped
    /// to its own half and the free side moved a little apart.
    /// <para>
    /// **The ledger is the state; this only reads it.** Admission, anchor distribution, publication, Abort and Stale
    /// all belong to <see cref="LogicalCutLedger"/>, and nothing here keeps a second copy of them or a publication
    /// state machine of its own. Every collection reads the ledger and builds what to draw from it. This never calls
    /// Publish, Abort, CompleteGeometry or Terminate — a display cannot advance a cut — and updating or ending a
    /// display therefore never returns a share of the incomplete budget.
    /// </para>
    /// <para>
    /// **What each logical state looks like.** Before admission, the whole parent. Admitted with the anchor
    /// distribution prepared, the provisional split; admitted without it, still the whole parent, told apart as
    /// <see cref="LogicalCutDisplayState.AwaitingInputs"/>. Published, the same split carried over to the two
    /// children, which is what the sides then name. Aborted, the retired source is dropped and nothing of it is
    /// drawn. Stale leaves the source live with no active operation, so the display goes back to the whole body —
    /// the prepared result is not shown as if it had been published.
    /// </para>
    /// <para>
    /// **The split does not wait for publication**, and the slot it draws before publication is not a child: no child
    /// id is invented early, and the sides say so. After publication each side names the child it became, so the same
    /// face is never registered twice.
    /// </para>
    /// <para>
    /// **One geometry, drawn twice.** Both sides address the same stored geometry, the same vertex and index buffers
    /// and the same index range. Nothing is cut, duplicated, re-meshed or transferred again for the split: only the
    /// per-instance clip records and transforms are uploaded. The geometry registration and the display instances are
    /// held for as long as this display shows them, so a source going Replaced does not retire the geometry a
    /// provisional display is still reading, and each reference this display took is given back exactly once.
    /// </para>
    /// <para>
    /// **Spaces.** The plane the ledger holds is in the fragment's physics frame, which for the synthetic input of
    /// this scope is the geometry's own local space; it is converted to world once per collection with the transform
    /// snapshot this display was given, which does not follow anything afterwards. The clip is evaluated on the world
    /// position **before** the separation is added and the separation is added after the object-to-world transform,
    /// as DESIGN 5.1 requires. The kerf is zero: no plane is moved to open a gap, so a gap appears only because a
    /// free side was moved. A fixed side takes no separation at all, and two fixed sides are still both clipped.
    /// </para>
    /// <para>
    /// **When updates happen.** <see cref="TryBeginFrame"/> collects the ledger's state and settles the buffers for
    /// that frame; <see cref="Render"/> then registers the draws, and every camera and the shadow pass of one frame
    /// read the same settled data. A frame is identified by the engine's frame counter, so collecting again inside
    /// one frame changes nothing and nothing rewrites what a frame has already drawn. Capacity is fixed and an
    /// ordinary shortage is decided **before** anything is uploaded, so a refusal leaves no half-updated draw.
    /// </para>
    /// <para>
    /// **A display failure is never a logical one.** When a collection cannot be made, the previous snapshot stays on
    /// screen and <see cref="TryBeginFrame"/> returns false: the caller is told that the latest logical state is not
    /// what is being shown, and nothing rolls back a publication that already happened. A GPU call that throws is
    /// different, as elsewhere: what reached the GPU cannot be established, so the display stops and only
    /// <see cref="Dispose"/> is left. Main thread only.
    /// </para>
    /// </summary>
    public sealed class VpLogicalCutDisplay : IDisposable
    {
        private sealed class Shown
        {
            public LogicalFragmentId fragment;
            public VpStoredGeometry geometry;
            public VpGeometryReference reference;
            public VpDisplayInstanceReference positiveInstance;
            public VpDisplayInstanceReference secondInstance;
            public bool hasSecondInstance;
            public Matrix4x4 objectToWorld;
            public VpIndirectCommand[] commands;
            public Material[] commandMaterials;

            /// <summary>The cut this entry is showing, remembered from when it was admitted.</summary>
            public CutOperationId operation;

            public bool splitShown;
        }

        /// <summary>What one entry's collection decided, before anything is changed.</summary>
        private struct Plan
        {
            public Shown entry;
            public bool drop;
            public bool split;
            public CutOperationId operation;
            public bool published;
            public LogicalFragmentId positiveChild;
            public LogicalFragmentId negativeChild;
            public bool positiveFixed;
            public bool negativeFixed;
            public float4 worldPlane;
        }

        private readonly VpCpuGeometryStorage _storage;
        private readonly VpGeometryReferenceTable _table;
        private readonly LogicalCutLedger _ledger;
        private readonly IReadOnlyDictionary<int, Material> _materials;
        private readonly Material _shadowMaterial;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private readonly Func<int> _frameSource;
        private readonly int _commandCapacity;
        private readonly int _instanceCapacity;

        private readonly List<Shown> _shown = new List<Shown>(2);
        private readonly List<Plan> _plans = new List<Plan>(2);

        // The adopted snapshot: what the GPU holds and what is being drawn. Nothing here is touched until an upload
        // has succeeded, so a refused collection leaves exactly this on screen.
        private List<LogicalCutDisplaySide> _sides = new List<LogicalCutDisplaySide>(4);
        private VpIndirectCommand[] _commands = Array.Empty<VpIndirectCommand>();
        private Material[] _commandMaterials = Array.Empty<Material>();
        private int _commandCount;

        // The candidate being built. It becomes the adopted snapshot only when the upload succeeds.
        private List<LogicalCutDisplaySide> _candidateSides = new List<LogicalCutDisplaySide>(4);
        private VpIndirectCommand[] _candidateCommands = Array.Empty<VpIndirectCommand>();
        private Material[] _candidateCommandMaterials = Array.Empty<Material>();
        private Matrix4x4[] _candidateTransforms = Array.Empty<Matrix4x4>();
        private VpInstanceClip[] _candidateClips = Array.Empty<VpInstanceClip>();

        // The frame whose collection succeeded, and the frame that may draw. They are not the same: a frame whose
        // collection was refused may still draw the snapshot adopted earlier.
        private int _settledFrame = int.MinValue;
        private int _openFrame = int.MinValue;
        private bool _hasSnapshot;
        private bool _drawRegisteredThisFrame;
        private bool _broken;
        private bool _disposed;

        private VpLogicalCutDisplay(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            LogicalCutLedger ledger,
            IReadOnlyDictionary<int, Material> materials,
            Material shadowMaterial,
            VpGpuIndexedGeometryBuffers buffers,
            VpIndexedIndirectDrawBatch batch,
            int commandCapacity,
            int instanceCapacity,
            Func<int> frameSource)
        {
            _storage = storage;
            _table = table;
            _ledger = ledger;
            _materials = materials;
            _shadowMaterial = shadowMaterial;
            _buffers = buffers;
            _batch = batch;
            _commandCapacity = commandCapacity;
            _instanceCapacity = instanceCapacity;
            _frameSource = frameSource;
        }

        /// <summary>
        /// How far the free side of a split is drawn apart, along the cut plane's own normal. A fixed side is not
        /// moved whatever this says, and the plane itself is never moved: the kerf stays zero.
        /// </summary>
        public float Separation { get; set; } = 0.05f;

        /// <summary>How many bodies this display holds.</summary>
        public int ShownCount => _shown.Count;

        /// <summary>How many instances the last settled collection draws: one per whole body, two per split.</summary>
        public int SideCount => _sides.Count;

        public bool IsDisposed => _disposed;

        /// <summary>
        /// Whether a GPU update was interrupted part-way. Such a display draws and collects no more, because what
        /// reached the GPU cannot be established; only <see cref="Dispose"/> is left to call.
        /// </summary>
        public bool IsBroken => _broken;

        /// <summary>Whether a draw has already been registered in the frame this display last settled.</summary>
        public bool HasDrawnThisFrame => _drawRegisteredThisFrame;

        /// <summary>How many collections have settled a frame. A collection refused settles nothing.</summary>
        public int SettledCollections { get; private set; }

        /// <summary>How many command and instance uploads this display has issued, its first upload included.</summary>
        public int CommandUploads { get; private set; }

        /// <summary>Vertex transfers this display has issued. One per body shown, and never one for a split.</summary>
        public int VertexTransfers { get; private set; }

        /// <summary>Index transfers this display has issued. One per body shown, and never one for a split.</summary>
        public int IndexTransfers { get; private set; }

        private int CurrentFrame => _frameSource != null ? _frameSource() : Time.frameCount;

        /// <summary>
        /// Makes a display over one storage and one ledger. The GPU buffers are the storage's own shape, made once and
        /// kept: no growth, no replacement. Nothing is shown until <see cref="TryShow"/> is called.
        /// </summary>
        public static bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            LogicalCutLedger ledger,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            Material shadowMaterial,
            int commandCapacity,
            int instanceCapacity,
            out VpLogicalCutDisplay display)
        {
            return TryCreate(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial,
                commandCapacity, instanceCapacity, null, out display);
        }

        /// <summary>
        /// The same, with the frame counter given by the caller instead of taken from the engine. It exists for tests,
        /// which have no frame loop to advance: <paramref name="frameSource"/> is what both
        /// <see cref="TryBeginFrame"/> and <see cref="Render"/> ask, so a test decides what one frame means.
        /// </summary>
        internal static bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            LogicalCutLedger ledger,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            Material shadowMaterial,
            int commandCapacity,
            int instanceCapacity,
            Func<int> frameSource,
            out VpLogicalCutDisplay display)
        {
            display = null;
            if (storage == null || table == null || ledger == null || materialsBySourceIndex == null
                || commandCapacity <= 0 || instanceCapacity <= 0)
            {
                return false;
            }

            VpGpuIndexedGeometryBuffers buffers = null;
            VpIndexedIndirectDrawBatch batch = null;
            bool taken = false;
            try
            {
                buffers = new VpGpuIndexedGeometryBuffers(storage.VertexCapacity, storage.IndexCapacity);
                batch = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity);
                display = new VpLogicalCutDisplay(
                    storage, table, ledger, materialsBySourceIndex, shadowMaterial, buffers, batch,
                    commandCapacity, instanceCapacity, frameSource);
                taken = true;
                return true;
            }
            finally
            {
                if (!taken)
                {
                    batch?.Dispose();
                    buffers?.Dispose();
                }
            }
        }

        /// <summary>
        /// Shows <paramref name="geometry"/> as the whole body of the live fragment <paramref name="fragment"/>, at
        /// the transform snapshot given. The geometry is transferred to the GPU once, here, and the registration and
        /// one display instance are taken and held until this display lets the body go.
        /// <para>
        /// Refused, changing nothing, when the fragment is not live, when it or one of its published children is
        /// already shown here — the same face is never registered twice — when the geometry cannot be prepared or
        /// resolved to materials, or when what it needs does not fit the fixed capacity.
        /// </para>
        /// </summary>
        public bool TryShow(LogicalFragmentId fragment, VpStoredGeometry geometry, Matrix4x4 objectToWorld)
        {
            ThrowIfDisposed();
            ThrowIfBroken();

            // A frame this display has already settled, or already drawn, is not changed from here: taking a body
            // now would transfer and register into the very frame that is done with. The caller offers it again
            // before the next frame is settled, and nothing of it has happened in the meantime.
            int frame = CurrentFrame;
            if ((_hasSnapshot && _settledFrame == frame) || (_openFrame == frame && _drawRegisteredThisFrame))
            {
                return false;
            }

            if (!fragment.IsSet
                || !_ledger.TryGetFragmentState(fragment, out LogicalFragmentState state)
                || state != LogicalFragmentState.Live
                || IsAlreadyShown(fragment))
            {
                return false;
            }

            if (!TryPrepare(geometry, out VpIndirectCommand[] commands, out Material[] commandMaterials))
            {
                return false;
            }

            // Room for the body now, and for the second side it may take later: a split must not be the thing that
            // discovers the capacity is gone.
            if (_commandCount + commands.Length > _commandCapacity
                || CurrentInstanceCount() + (commands.Length * 2) > _instanceCapacity)
            {
                return false;
            }

            int vertices;
            int indices;
            try
            {
                if (!VpStoredGeometryTransfer.TryUploadCommittedVertices(
                        _storage, _buffers.VertexBuffer, geometry.vertexStart, geometry.vertexCount, out vertices)
                    || !VpStoredGeometryTransfer.TryUploadPublishedIndices(
                        _storage, _buffers.IndexBuffer, geometry.indexRange, out indices))
                {
                    return false;
                }
            }
            catch
            {
                _broken = true;
                throw;
            }

            VertexTransfers += vertices > 0 ? 1 : 0;
            IndexTransfers += indices > 0 ? 1 : 0;

            if (!_table.TryRegisterGeometryWithDisplayInstance(
                    geometry, out VpGeometryReference reference, out VpDisplayInstanceReference instance))
            {
                return false;
            }

            _shown.Add(new Shown
            {
                fragment = fragment,
                geometry = geometry,
                reference = reference,
                positiveInstance = instance,
                objectToWorld = objectToWorld,
                commands = commands,
                commandMaterials = commandMaterials,
            });

            return true;
        }

        /// <summary>
        /// Collects the ledger's state and settles this frame's draw data. Calling it again within one frame does
        /// nothing at all and answers true: a frame that has drawn is not rewritten.
        /// <para>
        /// False means this frame could not be settled from the latest logical state — the capacity is short, a
        /// reference could not be taken, a plane could not be converted, or the batch refused the upload. The previous
        /// snapshot stays exactly as it was and keeps drawing, so what is on screen is **not** the latest state, and
        /// that is what the caller is being told. Nothing about the ledger is changed either way.
        /// </para>
        /// </summary>
        public bool TryBeginFrame()
        {
            ThrowIfDisposed();
            ThrowIfBroken();

            int frame = CurrentFrame;
            if (_hasSnapshot && _settledFrame == frame)
            {
                // Already settled from the latest state: collecting again would rewrite a frame for nothing.
                return true;
            }

            if (_openFrame == frame && _drawRegisteredThisFrame)
            {
                // This frame has drawn. It is not rewritten, and it was not settled from the latest state either.
                return false;
            }

            if (!TryCollectAndUpload())
            {
                // The latest logical state could not be settled. What was adopted earlier stays on the GPU and
                // stays drawable in this frame; the caller is told that it is not the latest.
                if (_hasSnapshot)
                {
                    _openFrame = frame;
                    _drawRegisteredThisFrame = false;
                }

                return false;
            }

            _settledFrame = frame;
            _openFrame = frame;
            _drawRegisteredThisFrame = false;
            SettledCollections++;
            return true;
        }

        /// <summary>
        /// Registers this frame's draws: one forward call per run of commands sharing a material, each followed by its
        /// shadow call when a shadow material was given. Drawing settles nothing by itself, so a second camera of the
        /// same frame draws exactly the same data.
        /// </summary>
        public void Render(int layer, Camera camera = null)
        {
            ThrowIfDisposed();
            ThrowIfBroken();

            if (!_hasSnapshot || _openFrame != CurrentFrame)
            {
                throw new InvalidOperationException(
                    "TryBeginFrame has not opened this frame, so there is nothing to draw. Call it once per frame, before drawing.");
            }

            _drawRegisteredThisFrame = true;
            int start = 0;
            while (start < _commandCount)
            {
                int end = start + 1;
                while (end < _commandCount && ReferenceEquals(_commandMaterials[end], _commandMaterials[start]))
                {
                    end++;
                }

                if (_shadowMaterial != null)
                {
                    _batch.Render(_commandMaterials[start], _shadowMaterial, _properties, _buffers, layer, start, end - start, camera);
                }
                else
                {
                    _batch.RenderForward(_commandMaterials[start], _properties, _buffers, layer, start, end - start, camera);
                }

                start = end;
            }
        }

        /// <summary>How this display is showing that fragment, whether as a body of its own or as a published child.</summary>
        public LogicalCutDisplayState StateOf(LogicalFragmentId fragment)
        {
            for (int i = 0; i < _sides.Count; i++)
            {
                LogicalCutDisplaySide side = _sides[i];
                if (side.published && side.fragment == fragment)
                {
                    return LogicalCutDisplayState.ProvisionalSplit;
                }
            }

            for (int i = 0; i < _shown.Count; i++)
            {
                Shown entry = _shown[i];
                if (entry.fragment != fragment)
                {
                    continue;
                }

                if (entry.splitShown)
                {
                    return LogicalCutDisplayState.ProvisionalSplit;
                }

                return entry.operation.IsSet ? LogicalCutDisplayState.AwaitingInputs : LogicalCutDisplayState.Whole;
            }

            return LogicalCutDisplayState.NotShown;
        }

        /// <summary>How many draw commands the adopted snapshot registers.</summary>
        public int DrawCommandCount => _commandCount;

        /// <summary>
        /// One command of the settled collection: its index range is the geometry's own, and its instance count is
        /// two for a split, which is how both sides come to address the same range.
        /// </summary>
        public bool TryGetDrawCommand(int index, out VpIndirectCommand command)
        {
            if (index < 0 || index >= _commandCount)
            {
                command = default;
                return false;
            }

            command = _commands[index];
            return true;
        }

        /// <summary>One instance of the settled collection, in the order it is drawn.</summary>
        public bool TryGetSide(int index, out LogicalCutDisplaySide side)
        {
            if (index < 0 || index >= _sides.Count)
            {
                side = default;
                return false;
            }

            side = _sides[index];
            return true;
        }

        /// <summary>
        /// Gives back everything this display owns: the buffers, the batch, and every geometry registration and
        /// display instance it took, each exactly once. The ledger, the storage and the borrowed materials are left
        /// alone — in particular no cut is completed, terminated or aborted by a display ending.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int i = 0; i < _shown.Count; i++)
            {
                ReleaseReferences(_shown[i]);
            }

            _shown.Clear();
            _sides.Clear();
            _candidateSides.Clear();
            _commandCount = 0;
            _hasSnapshot = false;
            _batch.Dispose();
            _buffers.Dispose();
        }

        // ----- collection ----------------------------------------------------------------------------------------

        private bool TryCollectAndUpload()
        {
            // 1. Decide everything first, reading the ledger and changing nothing.
            _plans.Clear();
            int commandCount = 0;
            int instanceCount = 0;
            int secondInstancesNeeded = 0;
            for (int i = 0; i < _shown.Count; i++)
            {
                if (!TryPlan(_shown[i], out Plan plan))
                {
                    return false;
                }

                _plans.Add(plan);
                if (plan.drop)
                {
                    continue;
                }

                commandCount += plan.entry.commands.Length;
                instanceCount += plan.entry.commands.Length * (plan.split ? 2 : 1);
                if (plan.split && !plan.entry.hasSecondInstance)
                {
                    secondInstancesNeeded++;
                }
            }

            // 2. The fixed capacity is decided here, before anything is taken or uploaded.
            if (commandCount > _commandCapacity || instanceCount > _instanceCapacity)
            {
                return false;
            }

            // 3. Take the display instances a new split needs. A failure gives back what this pass took.
            if (secondInstancesNeeded > 0 && !TryTakeSecondInstances())
            {
                return false;
            }

            // 4. Build the candidate. Nothing of the adopted snapshot is touched while this is being made.
            EnsureCandidateRoom(commandCount, instanceCount);
            _candidateSides.Clear();
            int command = 0;
            int instance = 0;
            for (int i = 0; i < _plans.Count; i++)
            {
                Plan plan = _plans[i];
                if (plan.drop)
                {
                    continue;
                }

                Shown entry = plan.entry;
                Vector3 positiveOffset = Vector3.zero;
                Vector3 negativeOffset = Vector3.zero;
                VpInstanceClip positiveClip = VpInstanceClip.None;
                VpInstanceClip negativeClip = VpInstanceClip.None;
                if (plan.split)
                {
                    // The plane is not moved: the kerf is zero, and a gap is only ever the moved side's doing.
                    var normal = new Vector3(plan.worldPlane.x, plan.worldPlane.y, plan.worldPlane.z);
                    positiveOffset = plan.positiveFixed ? Vector3.zero : normal * Separation;
                    negativeOffset = plan.negativeFixed ? Vector3.zero : -normal * Separation;
                    positiveClip = VpInstanceClip.Keep(ToVector4(plan.worldPlane), 1f, positiveOffset);
                    negativeClip = VpInstanceClip.Keep(ToVector4(plan.worldPlane), -1f, negativeOffset);
                }

                for (int c = 0; c < entry.commands.Length; c++)
                {
                    VpIndirectCommand source = entry.commands[c];
                    _candidateCommands[command] = new VpIndirectCommand(
                        source.range, source.localBounds, plan.split ? 2 : 1);
                    _candidateCommandMaterials[command] = entry.commandMaterials[c];
                    command++;

                    if (!plan.split)
                    {
                        _candidateTransforms[instance] = entry.objectToWorld;
                        _candidateClips[instance] = VpInstanceClip.None;
                        instance++;
                        _candidateSides.Add(new LogicalCutDisplaySide(
                            entry.fragment, default, 0f, false, default, false, Vector3.zero, VpInstanceClip.None));
                        continue;
                    }

                    // Both sides share this geometry, this range and this transform: only the clip record differs,
                    // and the separation is in that record, applied after the transform.
                    _candidateTransforms[instance] = entry.objectToWorld;
                    _candidateClips[instance] = positiveClip;
                    instance++;
                    _candidateSides.Add(new LogicalCutDisplaySide(
                        entry.fragment, plan.operation, 1f, plan.published, plan.positiveChild,
                        plan.positiveFixed, positiveOffset, positiveClip));

                    _candidateTransforms[instance] = entry.objectToWorld;
                    _candidateClips[instance] = negativeClip;
                    instance++;
                    _candidateSides.Add(new LogicalCutDisplaySide(
                        entry.fragment, plan.operation, -1f, plan.published, plan.negativeChild,
                        plan.negativeFixed, negativeOffset, negativeClip));
                }
            }

            // 5. One upload of the whole arrangement. An ordinary refusal leaves the batch and the adopted snapshot
            //    exactly as they were; a GPU call that throws is different, because what reached it cannot be
            //    established.
            bool uploaded;
            try
            {
                uploaded = _batch.TryUpload(
                    Slice(_candidateCommands, command),
                    Slice(_candidateTransforms, instance),
                    Slice(_candidateClips, instance),
                    false);
            }
            catch
            {
                _broken = true;
                _candidateSides.Clear();
                throw;
            }

            if (!uploaded)
            {
                // Nothing of this collection is adopted, and the instances it took are given straight back.
                GiveBackSecondInstancesTakenThisPass();
                _candidateSides.Clear();
                return false;
            }

            // The candidate becomes the adopted snapshot, and the arrays change places rather than being copied.
            AdoptCandidate(command);
            CommandUploads++;

            // 6. Only now, with the GPU holding this arrangement, is the display's own state changed.
            for (int i = 0; i < _plans.Count; i++)
            {
                Plan plan = _plans[i];
                if (plan.drop)
                {
                    ReleaseReferences(plan.entry);
                    _shown.Remove(plan.entry);
                    continue;
                }

                plan.entry.operation = plan.operation;
                plan.entry.splitShown = plan.split;
                if (!plan.split && plan.entry.hasSecondInstance)
                {
                    _table.TryRetireDisplayInstance(plan.entry.secondInstance);
                    plan.entry.secondInstance = default;
                    plan.entry.hasSecondInstance = false;
                }
            }

            return true;
        }

        /// <summary>Reads the ledger for one entry and decides what it should look like. Changes nothing.</summary>
        private bool TryPlan(Shown entry, out Plan plan)
        {
            plan = new Plan { entry = entry, operation = entry.operation };

            if (!_ledger.TryGetFragmentState(entry.fragment, out LogicalFragmentState state)
                || state == LogicalFragmentState.Retired)
            {
                // Aborted, or gone: the display lets the body go and draws nothing of it.
                plan.drop = true;
                return true;
            }

            if (state == LogicalFragmentState.Live)
            {
                if (!_ledger.TryGetActiveOperation(entry.fragment, out CutOperationId active))
                {
                    // Nothing admitted, or an operation reclaimed as stale: the whole body, and the cut is forgotten.
                    plan.operation = default;
                    return true;
                }

                plan.operation = active;
                if (!_ledger.TryGetPreparedAnchorDistribution(active, out AnchorDistributionResult distribution)
                    || distribution.status != AnchorDistributionStatus.Ok)
                {
                    // Admitted, but what the display needs is not prepared. Not the same as "no anchors".
                    return true;
                }

                if (!_ledger.TryGetOperation(active, out LogicalCutOperation operation)
                    || !TryWorldPlane(entry, operation.plane, out plan.worldPlane))
                {
                    return false;
                }

                plan.split = true;
                plan.published = false;
                plan.positiveFixed = FixedSupportAnchors.IsFixed(distribution.positiveCount);
                plan.negativeFixed = FixedSupportAnchors.IsFixed(distribution.negativeCount);
                return true;
            }

            // Replaced: the cut was published, and the sides are its children from now on. Which cut that was is
            // asked of the ledger, not remembered from an earlier collection: a publication must not depend on this
            // display having been updated in between, and an operation reclaimed as stale must never be mistaken for
            // the one that really replaced the fragment.
            if (!_ledger.TryGetReplacingOperation(entry.fragment, out CutOperationId replacing)
                || !_ledger.TryGetOperation(replacing, out LogicalCutOperation published)
                || !published.positive.IsSet
                || !published.negative.IsSet
                || !TryWorldPlane(entry, published.plane, out plan.worldPlane))
            {
                return false;
            }

            plan.operation = replacing;
            plan.split = true;
            plan.published = true;
            plan.positiveChild = published.positive;
            plan.negativeChild = published.negative;
            plan.positiveFixed = _ledger.IsFixedOwner(published.positive);
            plan.negativeFixed = _ledger.IsFixedOwner(published.negative);
            return true;
        }

        /// <summary>
        /// The candidate becomes what is drawn. Everything trades places — the arrays and the list of sides alike —
        /// so the snapshot that was adopted a moment ago becomes the scratch the next candidate is built in.
        /// Nothing is copied and nothing can grow here, which is what keeps the adopted CPU state and what the GPU
        /// now holds from ever disagreeing.
        /// </summary>
        private void AdoptCandidate(int commandCount)
        {
            VpIndirectCommand[] commands = _commands;
            Material[] materials = _commandMaterials;
            _commands = _candidateCommands;
            _commandMaterials = _candidateCommandMaterials;
            _candidateCommands = commands;
            _candidateCommandMaterials = materials;

            List<LogicalCutDisplaySide> sides = _sides;
            _sides = _candidateSides;
            _candidateSides = sides;
            _candidateSides.Clear();

            _commandCount = commandCount;
            _hasSnapshot = true;
        }

        /// <summary>
        /// Gives back the display instances this pass took for a split that is not being adopted. An entry that was
        /// already drawn as a split keeps the instance it has: that one belongs to the adopted snapshot.
        /// </summary>
        private void GiveBackSecondInstancesTakenThisPass()
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                Plan plan = _plans[i];
                if (plan.drop || !plan.split || !plan.entry.hasSecondInstance || plan.entry.splitShown)
                {
                    continue;
                }

                _table.TryRetireDisplayInstance(plan.entry.secondInstance);
                plan.entry.secondInstance = default;
                plan.entry.hasSecondInstance = false;
            }
        }

        private bool TryTakeSecondInstances()
        {
            for (int i = 0; i < _plans.Count; i++)
            {
                Plan plan = _plans[i];
                if (plan.drop || !plan.split || plan.entry.hasSecondInstance)
                {
                    continue;
                }

                if (!_table.TryAddDisplayInstance(plan.entry.reference, out VpDisplayInstanceReference second))
                {
                    // Give back only what this pass took, and leave everything else as it was.
                    GiveBackSecondInstancesTakenThisPass();
                    return false;
                }

                plan.entry.secondInstance = second;
                plan.entry.hasSecondInstance = true;
            }

            return true;
        }

        private bool TryWorldPlane(Shown entry, float4 physicsFramePlane, out float4 worldPlane)
        {
            // The ledger's plane is in the fragment's physics frame, which is this geometry's own local space for the
            // synthetic input of this scope. The transform is the snapshot this display was given.
            return VpCutPlane.TryGeometryLocalToWorld(physicsFramePlane, entry.objectToWorld, out worldPlane);
        }

        /// <summary>
        /// Whether this display already shows that fragment, as a body of its own or as one of the published children
        /// of a body it holds. The children are asked of the ledger rather than read off the last snapshot, so a
        /// publication that happened between two collections is seen here just the same.
        /// </summary>
        private bool IsAlreadyShown(LogicalFragmentId fragment)
        {
            for (int i = 0; i < _shown.Count; i++)
            {
                Shown entry = _shown[i];
                if (entry.fragment == fragment)
                {
                    return true;
                }

                if (_ledger.TryGetReplacingOperation(entry.fragment, out CutOperationId replacing)
                    && _ledger.TryGetOperation(replacing, out LogicalCutOperation published)
                    && (published.positive == fragment || published.negative == fragment))
                {
                    return true;
                }
            }

            return false;
        }

        private int CurrentInstanceCount()
        {
            int count = 0;
            for (int i = 0; i < _shown.Count; i++)
            {
                count += _shown[i].commands.Length * (_shown[i].splitShown ? 2 : 1);
            }

            return count;
        }

        private void ReleaseReferences(Shown entry)
        {
            if (entry.hasSecondInstance)
            {
                _table.TryRetireDisplayInstance(entry.secondInstance);
                entry.secondInstance = default;
                entry.hasSecondInstance = false;
            }

            _table.TryRetireDisplayInstance(entry.positiveInstance);
            _table.TryRetireGeometry(entry.reference);
        }

        private bool TryPrepare(VpStoredGeometry geometry, out VpIndirectCommand[] commands, out Material[] commandMaterials)
        {
            commands = null;
            commandMaterials = null;
            if (!_storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out int indexStart, out _)
                || state != VpIndexRangeState.Published
                || !VpStoredGeometryDraw.TryBuildCommands(
                    _storage, geometry, indexStart, out commands, out int[] materialIndices, out _))
            {
                return false;
            }

            var resolved = new Material[commands.Length];
            for (int c = 0; c < commands.Length; c++)
            {
                if (!_materials.TryGetValue(materialIndices[c], out Material material) || material == null)
                {
                    commands = null;
                    return false;
                }

                resolved[c] = material;
            }

            commandMaterials = resolved;
            return true;
        }

        private void EnsureCandidateRoom(int commandCount, int instanceCount)
        {
            if (_candidateCommands.Length < commandCount)
            {
                _candidateCommands = new VpIndirectCommand[commandCount];
                _candidateCommandMaterials = new Material[commandCount];
            }

            if (_candidateTransforms.Length < instanceCount)
            {
                _candidateTransforms = new Matrix4x4[instanceCount];
                _candidateClips = new VpInstanceClip[instanceCount];
            }
        }

        private static VpIndirectCommand[] Slice(VpIndirectCommand[] source, int count)
        {
            if (source.Length == count)
            {
                return source;
            }

            var exact = new VpIndirectCommand[count];
            Array.Copy(source, exact, count);
            return exact;
        }

        private static Matrix4x4[] Slice(Matrix4x4[] source, int count)
        {
            if (source.Length == count)
            {
                return source;
            }

            var exact = new Matrix4x4[count];
            Array.Copy(source, exact, count);
            return exact;
        }

        private static VpInstanceClip[] Slice(VpInstanceClip[] source, int count)
        {
            if (source.Length == count)
            {
                return source;
            }

            var exact = new VpInstanceClip[count];
            Array.Copy(source, exact, count);
            return exact;
        }

        private static Vector4 ToVector4(float4 value)
        {
            return new Vector4(value.x, value.y, value.z, value.w);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpLogicalCutDisplay));
            }
        }

        private void ThrowIfBroken()
        {
            if (_broken)
            {
                throw new InvalidOperationException(
                    "a GPU update of this display was interrupted, so what reached the GPU cannot be established; dispose it");
            }
        }
    }
}
