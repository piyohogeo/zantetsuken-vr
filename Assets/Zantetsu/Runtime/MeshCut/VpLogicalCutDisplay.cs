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
    /// One provisional cap this display has prepared: the <c>TemporaryRenderCapRecord</c> of DESIGN 5.2, being one
    /// side of one adopted cut plane together with the finite Cap Bounds Polygon that side would be masked inside.
    /// <para>
    /// **It is an input to a drawing that has not been made.** Nothing here is drawn yet, and this polygon must never
    /// be drawn opaquely on its own: it is the cross-section of the body's bounds box, not the cut's real outline, and
    /// DESIGN 5.2 shows it only through the stencil that restricts it to the body. The cut still looks open.
    /// </para>
    /// <para>
    /// **One record per side that has area**, not one per submesh: a body drawn as several commands because it has
    /// several materials is one body with one cross-section, and the same board is not prepared again for each of
    /// them. A plane that misses the body's box, or touches it in a point or along an edge, gives no record at all on
    /// either side rather than an empty board.
    /// </para>
    /// <para>
    /// **The vertices are in world space**, the placement snapshot applied and then this side's separation added, and
    /// they are wound so that <c>Cross(p1 - p0, p2 - p0)</c> points along <see cref="outwardNormal"/> — which is the
    /// outward direction for the side that is kept: against the plane's normal on the positive side, along it on the
    /// negative one. The two sides of one cut are the same polygon in opposite directions.
    /// </para>
    /// </summary>
    public readonly struct LogicalCutCapRecord
    {
        internal LogicalCutCapRecord(
            LogicalFragmentId source,
            CutOperationId operation,
            float side,
            bool published,
            LogicalFragmentId fragment,
            bool fixedByAnchors,
            Vector3 offset,
            Vector4 worldPlane,
            Vector3 outwardNormal,
            int vertexStart,
            int vertexCount)
        {
            this.source = source;
            this.operation = operation;
            this.side = side;
            this.published = published;
            this.fragment = fragment;
            this.fixedByAnchors = fixedByAnchors;
            this.offset = offset;
            this.worldPlane = worldPlane;
            this.outwardNormal = outwardNormal;
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
        }

        /// <summary>The fragment this display entry was given, which is the body the cross-section was taken of.</summary>
        public readonly LogicalFragmentId source;

        /// <summary>The admitted cut whose adopted face this cap lies in.</summary>
        public readonly CutOperationId operation;

        /// <summary>+1 or -1: which side of that face this cap belongs to.</summary>
        public readonly float side;

        /// <summary>
        /// Whether this side is a **published** child. Before publication a cap belongs to the source and the
        /// operation and to no child, exactly as the drawn sides do: no child id is invented early.
        /// </summary>
        public readonly bool published;

        /// <summary>The published child this cap belongs to, or an unset id before publication.</summary>
        public readonly LogicalFragmentId fragment;

        /// <summary>Whether the anchors make this side fixed. A fixed side has a cap like any other.</summary>
        public readonly bool fixedByAnchors;

        /// <summary>The separation already added to every vertex of this cap. Zero for a fixed side.</summary>
        public readonly Vector3 offset;

        /// <summary>The adopted face in world space, before the separation, as <c>(n.xyz, d)</c> with n normalized.</summary>
        public readonly Vector4 worldPlane;

        /// <summary>The outward normal of the side this cap closes, in world space, which the winding agrees with.</summary>
        public readonly Vector3 outwardNormal;

        /// <summary>How many vertices this cap's polygon has: three to six, never fewer.</summary>
        public readonly int vertexCount;

        internal readonly int vertexStart;
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
    /// **The provisional caps are prepared and drawn through the stencil.** Each split that has an area prepares the
    /// two <see cref="LogicalCutCapRecord"/>s of DESIGN 5.2 — one per side, one per body and never one per submesh —
    /// with the finite Cap Bounds Polygon each is masked inside. They are prepared with the candidate and adopted with
    /// it, so what is on screen and what is masked can never disagree, and a refused collection keeps the caps it had
    /// along with the sides it had. The drawing is DESIGN 5.6's, through a <see cref="VpStencilCapBatch"/> this
    /// display owns: two fixed stencil groups, one for every positive side and one for every negative side, each
    /// counting its own side's volumes — the same stored geometry, the same buffers and range, with the side's own
    /// clip record — and then drawing that side's polygon only where the count says the opening is. The polygon is
    /// never drawn as an opaque plate; the stencil restricts it to the real cross-section. Both groups are uploaded
    /// together, once, before any draw is registered, and each is one volume issue and one cap issue per frame.
    /// </para>
    /// <para>
    /// **Two batches, one arrangement.** The body's surfaces go to the display batch and the counting and the caps to
    /// the stencil batch, from one candidate. Neither is written until both have said they will accept the whole of
    /// it — the stencil batch is asked with its own non-writing judgement first — so an ordinary refusal leaves the
    /// body and its caps on screen together as they were, never the body new and its caps old. The provisional cap
    /// colour is the red of DESIGN 5.3.
    /// </para>
    /// <para>
    /// **What the two fixed groups do and do not cover.** The scope this is established for is ONE body with ONE
    /// cut. The groups are two because a side is what a cap belongs to, not because gathering sides is known to be
    /// safe: DESIGN 5.6's counting is per stencil group, so two bodies whose volumes overlap **on screen** in the
    /// same group count into one another and the caps that follow are not to be trusted. Several bodies may be shown
    /// here, and each contributes its own commands to the group of its side, but only an arrangement whose volumes do
    /// not overlap in the view is covered by what has been checked. Nothing here classifies, separates or arbitrates
    /// between bodies, and nothing in the ledger limits what may be admitted; deciding a colour per overlapping body
    /// is later work.
    /// </para>
    /// <para>
    /// **One stencil batch per camera.** DESIGN 5.6 gives one camera one aggregate batch, and the stencil byte is
    /// shared: a second display drawing its own stencil work into the same camera would initialise and count over
    /// this one's. So registering two independent displays — or any other stencil batch — for one camera is outside
    /// this contract, and this class does not detect it.
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

            /// <summary>
            /// The bounds of the vertices this geometry's indices reach, in the geometry's own frame, measured once
            /// when the body was taken in. The cap's cross-section is taken of this box, and it is the whole body's —
            /// not one submesh's — which is what keeps several materials from becoming several caps.
            /// </summary>
            public Bounds localBounds;

            /// <summary>
            /// The cap polygon prepared for this body: the cross-section itself, in world space and **before any
            /// separation**, together with the three things it was made of. While those are unchanged the intersection,
            /// the placing and the ordering are not done again (DESIGN 5.7); a body drawn further apart is placed
            /// again, not intersected again. A count of zero is a prepared answer too - a plane that misses this box is
            /// not asked about twice.
            /// </summary>
            public readonly Vector3[] capPolygon = new Vector3[VpCapBoundsPolygon.MaxVertices];

            public bool capPrepared;
            public int capVertexCount;
            public float4 capWorldPlane;
            public float4 capLocalPlane;
            public Matrix4x4 capPlacement;
            public Bounds capBounds;

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

            /// <summary>The same adopted face in the geometry's own frame, which is where the box's edges are.</summary>
            public float4 localPlane;
        }

        private readonly VpCpuGeometryStorage _storage;
        private readonly VpGeometryReferenceTable _table;
        private readonly LogicalCutLedger _ledger;
        private readonly IReadOnlyDictionary<int, Material> _materials;
        private readonly Material _shadowMaterial;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly VpStencilCapBatch _stencil;
        private readonly VpStencilCapMaterials _stencilMaterials;
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

        // The caps of the adopted snapshot, in the same fate as everything else here: prepared with the candidate and
        // adopted with it, so what is drawn and what would be masked never disagree.
        private LogicalCutCapRecord[] _capRecords = Array.Empty<LogicalCutCapRecord>();
        private Vector3[] _capVertices = Array.Empty<Vector3>();
        private int _capRecordCount;

        // The candidate being built. It becomes the adopted snapshot only when the upload succeeds.
        private List<LogicalCutDisplaySide> _candidateSides = new List<LogicalCutDisplaySide>(4);
        private VpIndirectCommand[] _candidateCommands = Array.Empty<VpIndirectCommand>();
        private Material[] _candidateCommandMaterials = Array.Empty<Material>();
        private Matrix4x4[] _candidateTransforms = Array.Empty<Matrix4x4>();
        private VpInstanceClip[] _candidateClips = Array.Empty<VpInstanceClip>();
        private LogicalCutCapRecord[] _candidateCapRecords = Array.Empty<LogicalCutCapRecord>();
        private Vector3[] _candidateCapVertices = Array.Empty<Vector3>();
        private int _candidateCapRecordCount;

        private readonly VpCapBoundsPolygon _capPolygon = new VpCapBoundsPolygon();

        // The stencil arrangement of the candidate: one command per side per body command, the positive sides first
        // and the negative sides after, so that each stencil group is one contiguous command range; the cap indices
        // fan each side's polygon; the two groups name their ranges. Built with the candidate, sized once.
        private VpIndirectCommand[] _candidateStencilCommands = Array.Empty<VpIndirectCommand>();
        private Matrix4x4[] _candidateStencilTransforms = Array.Empty<Matrix4x4>();
        private VpInstanceClip[] _candidateStencilClips = Array.Empty<VpInstanceClip>();
        private int[] _candidateCapIndices = Array.Empty<int>();
        private readonly VpStencilCapColor[] _candidateStencilColors = new VpStencilCapColor[StencilGroups];

        /// <summary>The two fixed stencil groups: every positive side, then every negative side.</summary>
        private const int StencilGroups = 2;

        /// <summary>The provisional cap colour of DESIGN 5.3: red until the geometry is committed.</summary>
        private static readonly Color ProvisionalCapColour = Color.red;

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
            VpStencilCapBatch stencil,
            VpStencilCapMaterials stencilMaterials,
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
            _stencil = stencil;
            _stencilMaterials = stencilMaterials;
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

        /// <summary>
        /// How many times a cap polygon was actually taken of a box and a plane. It stays where it is while the box,
        /// the face and the placement are the same, publication included: only a changed input makes another one.
        /// </summary>
        public int CapPolygonBuilds { get; private set; }

        /// <summary>How many arrangements the stencil batch has taken; one per settled collection.</summary>
        public int StencilUploads => _stencil.Uploads;

        /// <summary>Initialisation issues of the stencil batch: one per group per draw.</summary>
        public int StencilInitIssues => _stencil.StencilInitIssues;

        /// <summary>Volume issues of the stencil batch: one per group per draw, whatever the group's command count.</summary>
        public int StencilVolumeIssues => _stencil.VolumeIssues;

        /// <summary>Cap issues of the stencil batch: one per group per draw.</summary>
        public int StencilCapIssues => _stencil.CapIssues;

        /// <summary>The stencil groups the adopted snapshot holds: 0 with no split shown, otherwise 2.</summary>
        public int StencilGroupCount => _stencil.ColorCount;

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
            VpStencilCapBatch stencil = null;
            VpStencilCapMaterials stencilMaterials = null;
            bool taken = false;
            try
            {
                buffers = new VpGpuIndexedGeometryBuffers(storage.VertexCapacity, storage.IndexCapacity);
                batch = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity);

                // The stencil side is sized from the same capacity: every body command may be drawn as two sides, so
                // twice the commands and one instance each; every split body has two caps of at most the polygon's
                // vertex count, fanned. The capacity is fixed with the rest and checked before every upload.
                stencil = new VpStencilCapBatch(
                    StencilGroups,
                    commandCapacity * 2,
                    commandCapacity * 2,
                    commandCapacity * 2 * VpCapBoundsPolygon.MaxVertices,
                    commandCapacity * 2 * (VpCapBoundsPolygon.MaxVertices - 2) * 3);
                if (!VpStencilCapMaterials.TryCreate(StencilGroups, out stencilMaterials))
                {
                    return false;
                }

                display = new VpLogicalCutDisplay(
                    storage, table, ledger, materialsBySourceIndex, shadowMaterial, buffers, batch, stencil,
                    stencilMaterials, commandCapacity, instanceCapacity, frameSource);
                taken = true;
                return true;
            }
            finally
            {
                if (!taken)
                {
                    stencilMaterials?.Dispose();
                    stencil?.Dispose();
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

            if (!TryPrepare(
                    geometry, out VpIndirectCommand[] commands, out Material[] commandMaterials, out Bounds localBounds))
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
                localBounds = localBounds,
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

            // The counting and the caps, after the surfaces: their queues put them after the opaque bodies, so each
            // cap is depth-tested against the surfaces of this frame and drawn only inside its side's opening.
            _stencil.Render(_stencilMaterials, _buffers, layer, camera);
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

        /// <summary>
        /// How many provisional caps the settled collection prepared: two per split that has an area, none otherwise.
        /// Nothing is drawn from them yet.
        /// </summary>
        public int CapRecordCount => _capRecordCount;

        /// <summary>One prepared cap of the settled collection. The polygon itself is read with <see cref="TryGetCapVertex"/>.</summary>
        public bool TryGetCapRecord(int index, out LogicalCutCapRecord record)
        {
            if (index < 0 || index >= _capRecordCount)
            {
                record = default;
                return false;
            }

            record = _capRecords[index];
            return true;
        }

        /// <summary>
        /// One world-space vertex of one prepared cap's polygon, in the order it is wound. The display's own buffer is
        /// never handed out, so nothing outside can reorder or resize what a settled collection holds.
        /// </summary>
        public bool TryGetCapVertex(int recordIndex, int vertexIndex, out Vector3 world)
        {
            world = default;
            if (recordIndex < 0 || recordIndex >= _capRecordCount)
            {
                return false;
            }

            LogicalCutCapRecord record = _capRecords[recordIndex];
            if (vertexIndex < 0 || vertexIndex >= record.vertexCount)
            {
                return false;
            }

            world = _capVertices[record.vertexStart + vertexIndex];
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
            _capRecordCount = 0;
            _candidateCapRecordCount = 0;
            _hasSnapshot = false;
            _stencilMaterials.Dispose();
            _stencil.Dispose();
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
            int capRecordsNeeded = 0;
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

                if (plan.split)
                {
                    // Room for both sides of this body's one cross-section, however many commands it draws as.
                    capRecordsNeeded += 2;
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
            EnsureCandidateRoom(commandCount, instanceCount, capRecordsNeeded);
            _candidateSides.Clear();
            _candidateCapRecordCount = 0;
            int command = 0;
            int instance = 0;
            int capVertex = 0;
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

                    // The caps of this body, once for the body and not once per command. A malformed placement or
                    // bounds is an ordinary refusal here, decided before anything is uploaded.
                    if (!TryAddCaps(plan, positiveOffset, negativeOffset, ref capVertex))
                    {
                        GiveBackSecondInstancesTakenThisPass();
                        _candidateSides.Clear();
                        _candidateCapRecordCount = 0;
                        return false;
                    }
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

            // 5. The stencil arrangement of the same candidate: each split body's commands once per side, the positive
            //    sides first, and each side's polygon fanned into the cap index range of its group.
            BuildStencilArrangement(command, out int stencilCommands, out int capIndexCount, out int stencilGroups);

            // 6. Both batches are asked before either is written. The display batch's conditions are the capacity
            //    settled in step 2 and the shapes built here; the stencil batch says for itself, without writing. So
            //    an ordinary refusal from either leaves the body and its caps on screen together as they were.
            VpIndirectCommand[] commands = Slice(_candidateCommands, command);
            Matrix4x4[] transforms = Slice(_candidateTransforms, instance);
            VpInstanceClip[] clips = Slice(_candidateClips, instance);
            VpIndirectCommand[] stencilCommandsSlice = Slice(_candidateStencilCommands, stencilCommands);
            Matrix4x4[] stencilTransforms = Slice(_candidateStencilTransforms, stencilCommands);
            VpInstanceClip[] stencilClips = Slice(_candidateStencilClips, stencilCommands);
            if (!_stencil.CanUpload(
                    stencilCommandsSlice, stencilTransforms, stencilClips, _candidateCapVertices, capVertex,
                    _candidateCapIndices, capIndexCount, _candidateStencilColors, stencilGroups))
            {
                GiveBackSecondInstancesTakenThisPass();
                _candidateSides.Clear();
                _candidateCapRecordCount = 0;
                return false;
            }

            // 7. The uploads. An ordinary refusal from the display batch here would contradict step 2 and leaves
            //    nothing written; a refusal from the stencil batch after the display batch has been written would
            //    contradict step 6 and cannot be undone, so it stops the display like a GPU failure. A GPU call that
            //    throws is different, as elsewhere: what reached it cannot be established.
            bool uploaded;
            try
            {
                uploaded = _batch.TryUpload(commands, transforms, clips, false);
                if (uploaded && !_stencil.TryUpload(
                        stencilCommandsSlice, stencilTransforms, stencilClips, _candidateCapVertices, capVertex,
                        _candidateCapIndices, capIndexCount, _candidateStencilColors, stencilGroups))
                {
                    _broken = true;
                    throw new InvalidOperationException(
                        "the stencil batch refused an arrangement it had said it would accept, after the display batch "
                        + "was written; the body and its caps could no longer be kept together, so this display stops");
                }
            }
            catch
            {
                _broken = true;
                _candidateSides.Clear();
                _candidateCapRecordCount = 0;
                throw;
            }

            if (!uploaded)
            {
                // Nothing of this collection is adopted, and the instances it took are given straight back.
                GiveBackSecondInstancesTakenThisPass();
                _candidateSides.Clear();
                _candidateCapRecordCount = 0;
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

                plan.localPlane = operation.plane;
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

            plan.localPlane = published.plane;
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
        /// The two provisional caps of one split, prepared into the candidate: one cross-section of this body's local
        /// bounds box, placed in world, and given to each side with that side's own separation and outward direction.
        /// <para>
        /// A plane that misses the box, or meets it in a point or along an edge, leaves both sides without a record.
        /// That is a normal empty result and no board is invented for it. Returns false only for input the polygon
        /// cannot be taken of at all, which is an ordinary refusal of the whole collection.
        /// </para>
        /// </summary>
        /// <summary>
        /// The stencil batch's view of the candidate. The display batch draws each body's command with two instances,
        /// one per side; a stencil group has to count one side alone, so here every split body's command becomes two
        /// commands of one instance — the positive one in the first group's range, the negative one in the second's —
        /// with the same index range, transform and clip record as the display instance it stands for. Nothing is cut
        /// or copied. Each side's polygon is then fanned into its group's index range, and with no split shown there
        /// are no groups at all.
        /// </summary>
        private void BuildStencilArrangement(
            int commandCount, out int stencilCommands, out int capIndexCount, out int groups)
        {
            EnsureStencilRoom(commandCount);
            int positive = 0;
            int negative = 0;
            int splitCommands = 0;
            for (int c = 0; c < commandCount; c++)
            {
                if (_candidateCommands[c].instanceCount == 2)
                {
                    splitCommands++;
                }
            }

            // Positive sides occupy [0, splitCommands), negative sides [splitCommands, 2 * splitCommands). The
            // candidate's instances run command by command, the positive instance before the negative one.
            int instance = 0;
            for (int c = 0; c < commandCount; c++)
            {
                VpIndirectCommand source = _candidateCommands[c];
                if (source.instanceCount != 2)
                {
                    instance += source.instanceCount;
                    continue;
                }

                var one = new VpIndirectCommand(source.range, source.localBounds, 1);
                _candidateStencilCommands[positive] = one;
                _candidateStencilTransforms[positive] = _candidateTransforms[instance];
                _candidateStencilClips[positive] = _candidateClips[instance];
                positive++;
                instance++;

                int at = splitCommands + negative;
                _candidateStencilCommands[at] = one;
                _candidateStencilTransforms[at] = _candidateTransforms[instance];
                _candidateStencilClips[at] = _candidateClips[instance];
                negative++;
                instance++;
            }

            stencilCommands = positive + negative;

            // The caps: the positive records' polygons fanned first, then the negative ones, so that each group's cap
            // index range is contiguous. A record's vertices are already in the candidate's cap vertex array.
            int index = 0;
            int positiveIndexStart = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                float side = pass == 0 ? 1f : -1f;
                if (pass == 1)
                {
                    positiveIndexStart = index;
                }

                for (int r = 0; r < _candidateCapRecordCount; r++)
                {
                    LogicalCutCapRecord record = _candidateCapRecords[r];
                    if (record.side != side)
                    {
                        continue;
                    }

                    for (int v = 1; v + 1 < record.vertexCount; v++)
                    {
                        _candidateCapIndices[index++] = record.vertexStart;
                        _candidateCapIndices[index++] = record.vertexStart + v;
                        _candidateCapIndices[index++] = record.vertexStart + v + 1;
                    }
                }
            }

            capIndexCount = index;
            groups = splitCommands > 0 ? StencilGroups : 0;
            if (groups > 0)
            {
                _candidateStencilColors[0] = new VpStencilCapColor(
                    0, splitCommands, 0, positiveIndexStart, ProvisionalCapColour);
                _candidateStencilColors[1] = new VpStencilCapColor(
                    splitCommands, splitCommands, positiveIndexStart, index - positiveIndexStart, ProvisionalCapColour);
            }
        }

        private void EnsureStencilRoom(int commandCount)
        {
            int commands = commandCount * 2;
            if (_candidateStencilCommands.Length < commands)
            {
                _candidateStencilCommands = new VpIndirectCommand[commands];
                _candidateStencilTransforms = new Matrix4x4[commands];
                _candidateStencilClips = new VpInstanceClip[commands];
            }

            int indices = commandCount * 2 * (VpCapBoundsPolygon.MaxVertices - 2) * 3;
            if (_candidateCapIndices.Length < indices)
            {
                _candidateCapIndices = new int[indices];
            }
        }

        private bool TryAddCaps(Plan plan, Vector3 positiveOffset, Vector3 negativeOffset, ref int capVertex)
        {
            Shown entry = plan.entry;
            if (!TryPrepareCapPolygon(entry, plan.localPlane))
            {
                return false;
            }

            int count = entry.capVertexCount;
            if (count == 0)
            {
                return true;
            }

            // The same prepared polygon twice: the negative side keeps the order it was built in, and the positive side
            // reads it backwards, because its outward direction is the opposite one. Each side's separation is added as
            // it is placed, which is what makes these the caps of the two sides as they are actually drawn apart - and
            // what lets the separation change without the cross-section being taken again.
            int positiveStart = capVertex;
            int negativeStart = capVertex + count;
            for (int i = 0; i < count; i++)
            {
                _candidateCapVertices[positiveStart + i] = entry.capPolygon[count - 1 - i] + positiveOffset;
                _candidateCapVertices[negativeStart + i] = entry.capPolygon[i] + negativeOffset;
            }

            Vector4 face = ToVector4(entry.capWorldPlane);
            var normal = new Vector3(entry.capWorldPlane.x, entry.capWorldPlane.y, entry.capWorldPlane.z);
            _candidateCapRecords[_candidateCapRecordCount++] = new LogicalCutCapRecord(
                entry.fragment, plan.operation, 1f, plan.published, plan.positiveChild, plan.positiveFixed,
                positiveOffset, face, -normal, positiveStart, count);
            _candidateCapRecords[_candidateCapRecordCount++] = new LogicalCutCapRecord(
                entry.fragment, plan.operation, -1f, plan.published, plan.negativeChild, plan.negativeFixed,
                negativeOffset, face, normal, negativeStart, count);

            capVertex = negativeStart + count;
            return true;
        }

        /// <summary>
        /// The cap polygon of one body, prepared once and then kept. The box, the adopted face and the placement are
        /// everything it is made of, so while those three are the same the intersection, the conversion and the
        /// ordering are not repeated - not on the next frame, and not when the cut is published, which changes who the
        /// sides belong to and not where the face is (DESIGN 5.7). The separation is deliberately not part of it.
        /// <para>
        /// The placement and the bounds are settled when the body is taken in and are not changed afterwards; they are
        /// compared here all the same, so that the reuse rests on what the polygon was made of rather than on that
        /// being remembered. Nothing is kept across bodies and there is no store to keep: this is one prepared answer
        /// living on the body it belongs to.
        /// </para>
        /// </summary>
        private bool TryPrepareCapPolygon(Shown entry, float4 localPlane)
        {
            if (entry.capPrepared
                && Same(entry.capLocalPlane, localPlane)
                && Same(entry.capPlacement, entry.objectToWorld)
                && Same(entry.capBounds, entry.localBounds))
            {
                return true;
            }

            entry.capPrepared = false;
            entry.capVertexCount = 0;
            if (!_capPolygon.TryBuild(
                    entry.localBounds,
                    localPlane,
                    entry.objectToWorld,
                    VpCapBoundsPolygon.EpsilonFor(entry.localBounds),
                    entry.capPolygon,
                    0,
                    out int count,
                    out float4 worldPlane))
            {
                return false;
            }

            entry.capVertexCount = count;
            entry.capWorldPlane = worldPlane;
            entry.capLocalPlane = localPlane;
            entry.capPlacement = entry.objectToWorld;
            entry.capBounds = entry.localBounds;
            entry.capPrepared = true;
            CapPolygonBuilds++;
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

            LogicalCutCapRecord[] capRecords = _capRecords;
            Vector3[] capVertices = _capVertices;
            _capRecords = _candidateCapRecords;
            _capVertices = _candidateCapVertices;
            _candidateCapRecords = capRecords;
            _candidateCapVertices = capVertices;
            _capRecordCount = _candidateCapRecordCount;
            _candidateCapRecordCount = 0;

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

        /// <summary>
        /// What this display needs to hold about a body it is taking in: the commands, their materials, and the local
        /// bounds of the vertices the indices actually reach. The bounds are measured here, once, by the same build
        /// that makes the commands — not per frame, not per camera and not per submesh — which is what lets the cap's
        /// cross-section be the body's own and a change of colour or of viewpoint never re-make it.
        /// </summary>
        private bool TryPrepare(
            VpStoredGeometry geometry,
            out VpIndirectCommand[] commands,
            out Material[] commandMaterials,
            out Bounds localBounds)
        {
            commands = null;
            commandMaterials = null;
            if (!_storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out int indexStart, out _)
                || state != VpIndexRangeState.Published
                || !VpStoredGeometryDraw.TryBuildCommands(
                    _storage, geometry, indexStart, out commands, out int[] materialIndices, out localBounds))
            {
                localBounds = default;
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

        /// <summary>
        /// Room for the candidate, taken before anything is built and well before anything is adopted: the adoption
        /// itself only trades arrays, so nothing there can grow, allocate or fail.
        /// </summary>
        private void EnsureCandidateRoom(int commandCount, int instanceCount, int capRecordCount)
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

            if (_candidateCapRecords.Length < capRecordCount)
            {
                _candidateCapRecords = new LogicalCutCapRecord[capRecordCount];
                _candidateCapVertices = new Vector3[capRecordCount * VpCapBoundsPolygon.MaxVertices];
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

        // What a prepared polygon was made of is compared value by value: Unity's own equality for Vector4, Matrix4x4
        // and Bounds is approximate, and something merely close to the face the polygon was taken of is a different
        // face. Nothing here is a tolerance.
        private static bool Same(float4 a, float4 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    if (a[row, column] != b[row, column])
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool Same(Bounds a, Bounds b)
        {
            Vector3 aMin = a.min;
            Vector3 bMin = b.min;
            Vector3 aMax = a.max;
            Vector3 bMax = b.max;
            return aMin.x == bMin.x && aMin.y == bMin.y && aMin.z == bMin.z
                && aMax.x == bMax.x && aMax.y == bMax.y && aMax.z == bMax.z;
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
