using System;
using System.Collections;
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
    /// it, so a refused collection keeps the caps it had along with the sides it had. The polygon is never drawn as an
    /// opaque plate; the stencil restricts it to the real cross-section. The provisional cap colour is the red of
    /// DESIGN 5.3.
    /// </para>
    /// <para>
    /// **Colours, per camera (DESIGN 5.6).** Which caps are seen, and which bodies may overlap on screen, depend on
    /// the camera, so the stencil arrangement is made per camera by <see cref="TryPrepareCamera(Camera)"/> from the
    /// adopted snapshot alone, in DESIGN 5.6's order: each cap's both-eye visibility (<see cref="VpCapVisibility"/>),
    /// the compatibility groups over every cut condition, seen or not (<see cref="VpCapCompatibility"/>), the groups
    /// none of whose caps is seen left out altogether — their volumes and their caps, while the body and its shadow
    /// stay — and the colours within the limit (<see cref="VpStencilColors"/>, with
    /// <see cref="VpCapProjectionConflict"/> saying who must be kept apart, and caps left out by visibility passed on
    /// as not complete). A group with a cap still seen keeps the volumes of every target in it. Every colour is uploaded
    /// together, once per preparation, into that camera's own stencil batch; within a colour the order stays
    /// initialisation, every volume, every cap. No fixed positive and negative group is used.
    /// </para>
    /// <para>
    /// **One stencil batch per registered camera.** A camera is registered with
    /// <see cref="TryRegisterCamera"/>, up to the fixed <see cref="VpStencilSettings.cameraCapacity"/>, and owns a stencil
    /// batch of its own for as long as it is registered, so preparing one camera never writes the buffers another
    /// camera's registered draws read. The geometry, its GPU buffers and the display batch are shared; only the
    /// arrangement and its buffers are per camera. Once a camera's draws are registered in a frame it is not prepared
    /// again and not unregistered in that frame. Other stencil work drawing into the same camera is still outside this
    /// contract, and this class does not detect it.
    /// </para>
    /// <para>
    /// **Snapshot and preparation.** Adopting a snapshot needs both the display batch and the stencil side to have
    /// room for it: the largest stencil arrangement the candidate could need is checked against the stencil capacity,
    /// and against every registered camera's batch, before anything is written, and an ordinary refusal keeps the
    /// previous snapshot as before. That check is of capacity and form; it does not promise the later upload, and an
    /// upload refused against it, like a GPU call that throws, stops the display. A preparation belongs to the frame
    /// and the adopted snapshot it was made for, so a camera prepared before a newer snapshot was adopted, or in an
    /// earlier frame, draws nothing until it is prepared again — and <see cref="Render"/> refuses it before
    /// registering anything, the body included.
    /// </para>
    /// <para>
    /// **A preparation allocates nothing once the display is made.** Everything a camera's preparation works in — the
    /// targets, their conditions and caps, the visibility, the groups, what is kept, the colours and the arrangement
    /// itself — is this display's own scratch, made when the display is made at the sizes its fixed capacity gives
    /// (every body command drawn as two sides, and two caps per split body), filled to a count each time and never
    /// grown. A cap is read as a look at the adopted snapshot's own vertices, not copied; those looks last only while
    /// the preparation runs and are cleared when it ends, so nothing kept by a camera, and nothing kept from one
    /// adoption to the next, refers to them. The arrangement is uploaded by count, so nothing an earlier, larger
    /// arrangement left in the scratch is checked, sent or drawn. A preparation runs to its end before another
    /// starts: a call into this display made while one is running is refused before it changes anything.
    /// </para>
    /// <para>
    /// **Two casters, and who owns them.** A body drawn as a provisional split is cast two-sided and every other body
    /// one-sided, which is DESIGN 5.4's division: no cap is drawn into the shadow map, so what occludes behind the
    /// opening is the back of the shell, and only a two-sided caster puts it there. Both materials are the caller's,
    /// made and destroyed by the caller; this class creates neither and disposes neither, and it refuses to be made
    /// with one of them alone, whichever one that is -- either both casters or no shadows at all. Which commands fall on which side is decided where the candidate is built, from the
    /// plan that says the body is split -- not by reading back what the clip records hold, which is an input to the
    /// drawing rather than a statement about the logical state. Every caster reads this frame's adopted snapshot: the
    /// same geometry, transform, clip record and offset as the surfaces, never a second look at the ledger.
    /// </para>
    /// <para>
    /// **One stereo condition for the whole arrangement.** <see cref="SinglePassInstanced"/> is the only way to say
    /// that the draws are stereo, and it is read in one place — where the arrangement uploads — and given to both
    /// batches together, so the body, the initialisation, the volumes and the caps cannot end up on different eye
    /// counts. Finding out whether XR is up, and which mode it settled on, is the caller's; this class asks the engine
    /// nothing and waits for nothing.
    /// </para>
    /// <para>
    /// **When updates happen.** <see cref="TryBeginFrame"/> collects the ledger's state and settles the body's buffers
    /// for that frame; <see cref="TryPrepareCamera(Camera)"/> then makes one camera's stencil arrangement from what was
    /// settled, as often as the view changes before that camera draws, without collecting the ledger or transferring
    /// geometry again; <see cref="Render"/> registers that camera's draws. A frame is identified by the engine's frame
    /// counter, so collecting again inside one frame changes nothing and nothing rewrites what a frame has already
    /// drawn. Capacity is fixed and an ordinary shortage is decided **before** anything is uploaded.
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
        private readonly Material _provisionalShadowMaterial;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        private readonly VpIndexedIndirectDrawBatch _batch;
        private readonly VpStencilCapMaterials _stencilMaterials;
        private readonly VpStencilSettings _settings;

        // One slot per camera that may hold stencil work: fixed in number, filled and emptied only by registration.
        private readonly CameraStencil[] _cameraStencils;
        private readonly int _stencilCommandCapacity;
        private readonly int _stencilCapVertexCapacity;
        private readonly int _stencilCapIndexCapacity;

        // What the stencil batches of cameras no longer registered had counted, so the totals do not go backwards.
        private int _retiredStencilUploads;
        private int _retiredStencilBufferWrites;
        private int _retiredStencilInitIssues;
        private int _retiredStencilVolumeIssues;
        private int _retiredStencilCapIssues;
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

        // Which of the adopted commands are a body drawn as a provisional split. It is written where the candidate is
        // built, from the plan, and adopted with everything else -- never worked out afterwards by looking at what the
        // clip records happen to hold.
        private bool[] _commandProvisional = Array.Empty<bool>();
        private int _commandCount;

        // The caps of the adopted snapshot, in the same fate as everything else here: prepared with the candidate and
        // adopted with it, so what is drawn and what would be masked never disagree.
        private LogicalCutCapRecord[] _capRecords = Array.Empty<LogicalCutCapRecord>();
        private Vector3[] _capVertices = Array.Empty<Vector3>();
        private int _capRecordCount;
        private int _capVertexCount;

        // Each instance's transform and clip record, adopted with the rest, so that a camera's stencil arrangement is
        // made from exactly what the body is drawn with.
        private Matrix4x4[] _transforms = Array.Empty<Matrix4x4>();
        private VpInstanceClip[] _clips = Array.Empty<VpInstanceClip>();

        // Counts adoptions. A camera's preparation names the snapshot it was made for, and a newer one voids it.
        private long _generation;

        // The candidate being built. It becomes the adopted snapshot only when the upload succeeds.
        private List<LogicalCutDisplaySide> _candidateSides = new List<LogicalCutDisplaySide>(4);
        private VpIndirectCommand[] _candidateCommands = Array.Empty<VpIndirectCommand>();
        private Material[] _candidateCommandMaterials = Array.Empty<Material>();
        private bool[] _candidateCommandProvisional = Array.Empty<bool>();
        private Matrix4x4[] _candidateTransforms = Array.Empty<Matrix4x4>();
        private VpInstanceClip[] _candidateClips = Array.Empty<VpInstanceClip>();
        private LogicalCutCapRecord[] _candidateCapRecords = Array.Empty<LogicalCutCapRecord>();
        private Vector3[] _candidateCapVertices = Array.Empty<Vector3>();
        private int _candidateCapRecordCount;

        private readonly VpCapBoundsPolygon _capPolygon = new VpCapBoundsPolygon();

        // A stencil arrangement being made: for the largest one a candidate could need, checked before adoption, and
        // for one camera's colours when it is prepared. Scratch only; each upload copies what it takes.
        // Made once, at the stencil capacity, and never grown: the arrangement is filled to a count and uploaded by it.
        private readonly VpIndirectCommand[] _candidateStencilCommands;
        private readonly Matrix4x4[] _candidateStencilTransforms;
        private readonly VpInstanceClip[] _candidateStencilClips;
        private readonly int[] _candidateCapIndices;
        private readonly VpStencilCapColor[] _candidateStencilColors;

        // One preparation's scratch, one slot per cap record the capacity allows: made once, filled to a count, never
        // grown, and read no further than that count. The targets hold looks at the adopted cap vertices and at the
        // two scratch arrays below them; those are cleared when the preparation ends.
        private readonly int _capRecordCapacity;
        private readonly VpCapProjectionTarget[] _prepTargets;
        private readonly VpCapCompatibilityTarget[] _prepConditions;
        private readonly bool[] _prepSeen;
        private readonly int[] _prepGroupOfRecord;
        private readonly bool[] _prepGroupSeen;
        private readonly bool[] _prepCapIssued;
        private readonly int[] _prepKeptGroup;
        private readonly int[] _prepKeptRecords;
        private readonly VpCapProjectionTarget[] _prepKeptTargets;
        private readonly int[] _prepKeptGroupOf;
        private readonly int[] _prepColourOfGroup;
        private readonly VpCapConstraint[] _prepConstraints;
        private readonly VpArrayRange<Vector3>[] _prepCaps;
        private readonly CountedList<VpCapCompatibilityTarget> _prepConditionList;
        private readonly CountedList<VpCapProjectionTarget> _prepKeptTargetList;
        private readonly CountedList<int> _prepKeptGroupOfList;
        private int _prepRecordsUsed;
        private int _preparationRecordLimit;
        private bool _preparing;

        /// <summary>
        /// A read-only look at the first <see cref="Count"/> items of an array this display owns, for the classifiers
        /// that take lists. Made once with its array; only the count changes, so nothing is allocated when it is handed
        /// over, and nothing past the count -- an earlier, longer fill -- is ever read through it.
        /// </summary>
        private sealed class CountedList<T> : IReadOnlyList<T>
        {
            private readonly T[] _items;
            private int _count;

            public CountedList(T[] items)
            {
                _items = items;
            }

            public int Count => _count;

            public T this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _items[index];
                }
            }

            public void SetCount(int count)
            {
                if ((uint)count > (uint)_items.Length)
                {
                    throw new ArgumentOutOfRangeException(nameof(count));
                }

                _count = count;
            }

            public IEnumerator<T> GetEnumerator()
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return _items[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        /// <summary>One registered camera's stencil work: its own batch, and what it was last prepared and drawn for.</summary>
        private sealed class CameraStencil
        {
            public Camera camera;
            public VpStencilCapBatch batch;
            public int preparedFrame = int.MinValue;
            public long preparedGeneration = -1;
            public int drawnFrame = int.MinValue;
            public VpStencilPreparation preparation;
        }

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
            Material provisionalShadowMaterial,
            VpGpuIndexedGeometryBuffers buffers,
            VpIndexedIndirectDrawBatch batch,
            VpStencilCapMaterials stencilMaterials,
            VpStencilSettings settings,
            int commandCapacity,
            int instanceCapacity,
            in DerivedCapacities derived,
            Func<int> frameSource)
        {
            _storage = storage;
            _table = table;
            _ledger = ledger;
            _materials = materials;
            _shadowMaterial = shadowMaterial;
            _provisionalShadowMaterial = provisionalShadowMaterial;
            _buffers = buffers;
            _batch = batch;
            _stencilMaterials = stencilMaterials;
            _settings = settings;
            _cameraStencils = new CameraStencil[settings.cameraCapacity];
            _candidateStencilColors = new VpStencilCapColor[settings.maxStencilColors];

            // Every body command may be drawn as two sides, one instance each, and every split body has two caps of at
            // most the polygon's vertex count, fanned. Each camera's batch is made to these sizes, derived and checked
            // before anything was made (DeriveCapacities).
            _stencilCommandCapacity = derived.stencilCommands;
            _stencilCapVertexCapacity = derived.capVertices;
            _stencilCapIndexCapacity = derived.capIndices;
            _candidateStencilCommands = new VpIndirectCommand[_stencilCommandCapacity];
            _candidateStencilTransforms = new Matrix4x4[_stencilCommandCapacity];
            _candidateStencilClips = new VpInstanceClip[_stencilCommandCapacity];
            _candidateCapIndices = new int[_stencilCapIndexCapacity];

            // Two caps per split body, and a body takes at least one command: a collection with more is refused before
            // it is adopted, so no preparation meets more records than this.
            _capRecordCapacity = derived.capRecords;
            int records = _capRecordCapacity;
            _prepTargets = new VpCapProjectionTarget[records];
            _prepConditions = new VpCapCompatibilityTarget[records];
            _prepSeen = new bool[records];
            _prepGroupOfRecord = new int[records];
            _prepGroupSeen = new bool[records];
            _prepCapIssued = new bool[records];
            _prepKeptGroup = new int[records];
            _prepKeptRecords = new int[records];
            _prepKeptTargets = new VpCapProjectionTarget[records];
            _prepKeptGroupOf = new int[records];
            _prepColourOfGroup = new int[records];
            _prepConstraints = new VpCapConstraint[derived.constraints];
            _prepCaps = new VpArrayRange<Vector3>[derived.caps];
            _prepConditionList = new CountedList<VpCapCompatibilityTarget>(_prepConditions);
            _prepKeptTargetList = new CountedList<VpCapProjectionTarget>(_prepKeptTargets);
            _prepKeptGroupOfList = new CountedList<int>(_prepKeptGroupOf);
            _preparationRecordLimit = records;
            _commandCapacity = commandCapacity;
            _instanceCapacity = instanceCapacity;
            _frameSource = frameSource;
        }

        /// <summary>The sizes a display of one command capacity is made to, every one of them an int.</summary>
        internal readonly struct DerivedCapacities
        {
            public DerivedCapacities(int stencilCommands, int capVertices, int capIndices, int capRecords, int constraints, int caps)
            {
                this.stencilCommands = stencilCommands;
                this.capVertices = capVertices;
                this.capIndices = capIndices;
                this.capRecords = capRecords;
                this.constraints = constraints;
                this.caps = caps;
            }

            public readonly int stencilCommands;
            public readonly int capVertices;
            public readonly int capIndices;
            public readonly int capRecords;
            public readonly int constraints;
            public readonly int caps;
        }

        /// <summary>
        /// Every size a display of <paramref name="commandCapacity"/> commands is made to -- the stencil commands, cap
        /// vertices and cap indices of each camera's batch, and the cap records, conditions and cap looks of a
        /// preparation -- worked out in 64-bit arithmetic. False when any of them is not a positive int, which is a
        /// capacity that cannot be represented; nothing is decided here beyond that, and no limit of its own is set.
        /// </summary>
        internal static bool TryDeriveCapacities(int commandCapacity, out DerivedCapacities derived)
        {
            derived = default;
            if (commandCapacity <= 0)
            {
                return false;
            }

            long stencilCommands = (long)commandCapacity * 2;
            long capVertices = stencilCommands * VpCapBoundsPolygon.MaxVertices;
            long capIndices = stencilCommands * (VpCapBoundsPolygon.MaxVertices - 2) * 3;
            long capRecords = stencilCommands;
            long constraints = capRecords * VpCapCompatibility.SingleCutConstraints;
            long caps = capRecords * VpCapProjectionConflict.SingleCutCaps;
            if (!FitsInt(stencilCommands) || !FitsInt(capVertices) || !FitsInt(capIndices) || !FitsInt(capRecords)
                || !FitsInt(constraints) || !FitsInt(caps))
            {
                return false;
            }

            derived = new DerivedCapacities(
                (int)stencilCommands, (int)capVertices, (int)capIndices, (int)capRecords, (int)constraints, (int)caps);
            return true;
        }

        private static bool FitsInt(long value)
        {
            return value > 0 && value <= int.MaxValue;
        }

        /// <summary>
        /// How far the free side of a split is drawn apart, along the cut plane's own normal. A fixed side is not
        /// moved whatever this says, and the plane itself is never moved: the kerf stays zero.
        /// </summary>
        public float Separation { get; set; } = 0.05f;

        /// <summary>
        /// Whether the draws this display registers are for Single Pass Instanced stereo: one issue carrying two
        /// instances, one per eye. It is read where a collection uploads, and both batches are told the same thing in
        /// that one place, so the body's surfaces, the stencil initialisation, the volumes and the caps are never
        /// drawn on different conditions. Default false, which is what every existing caller keeps.
        /// <para>
        /// Whether XR is up, and which stereo mode it settled on, is the caller's to find out: nothing here asks the
        /// engine, waits for frames or changes this on its own. Setting it invalidates no adopted snapshot — what the
        /// GPU holds keeps the condition of the upload that wrote it, which <see cref="DrawsSinglePassInstanced"/> reports
        /// and every camera's stencil preparation takes — and takes effect from the next settled collection.
        /// </para>
        /// </summary>
        public bool SinglePassInstanced { get; set; }

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

        /// <summary>
        /// Shadow calls issued with the one-sided caster: the ordinary bodies, the ones with no provisional split.
        /// One per run of such commands per draw.
        /// </summary>
        public int OneSidedShadowIssues { get; private set; }

        /// <summary>
        /// Shadow calls issued with the two-sided caster: the bodies drawn as a provisional split. One per run of such
        /// commands per draw, and none at all while nothing is split.
        /// </summary>
        public int TwoSidedShadowIssues { get; private set; }

        /// <summary>Whether command <paramref name="index"/> of the adopted snapshot is cast two-sided.</summary>
        public bool CommandCastsTwoSided(int index)
        {
            if (index < 0 || index >= _commandCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            return _commandProvisional[index];
        }

        /// <summary>How many commands the adopted snapshot holds.</summary>
        public int CommandCount => _commandCount;

        /// <summary>Vertex transfers this display has issued. One per body shown, and never one for a split.</summary>
        public int VertexTransfers { get; private set; }

        /// <summary>Index transfers this display has issued. One per body shown, and never one for a split.</summary>
        public int IndexTransfers { get; private set; }

        /// <summary>
        /// How many times a cap polygon was actually taken of a box and a plane. It stays where it is while the box,
        /// the face and the placement are the same, publication included: only a changed input makes another one.
        /// </summary>
        public int CapPolygonBuilds { get; private set; }

        /// <summary>How many arrangements the cameras' stencil batches have taken; one per camera preparation.</summary>
        public int StencilUploads => SumStencil(b => b.Uploads) + _retiredStencilUploads;

        /// <summary>Initialisation issues of the cameras' stencil batches: one per colour per draw.</summary>
        public int StencilInitIssues => SumStencil(b => b.StencilInitIssues) + _retiredStencilInitIssues;

        /// <summary>Volume issues of the cameras' stencil batches: one per colour per draw, whatever its command count.</summary>
        public int StencilVolumeIssues => SumStencil(b => b.VolumeIssues) + _retiredStencilVolumeIssues;

        /// <summary>Cap issues of the cameras' stencil batches: one per colour per draw.</summary>
        public int StencilCapIssues => SumStencil(b => b.CapIssues) + _retiredStencilCapIssues;

        /// <summary>
        /// Buffer writes the cameras' stencil batches have made. Drawing makes none: preparation writes, so this
        /// standing still across a frame's draws is what says no transfer was added per colour.
        /// </summary>
        public int StencilBufferWrites => SumStencil(b => b.BufferWrites) + _retiredStencilBufferWrites;

        /// <summary>The settings this display was made with.</summary>
        public VpStencilSettings StencilSettings => _settings;

        /// <summary>How many cameras are registered for stencil work now.</summary>
        public int RegisteredCameraCount
        {
            get
            {
                int n = 0;
                foreach (CameraStencil slot in _cameraStencils)
                {
                    n += slot != null ? 1 : 0;
                }

                return n;
            }
        }

        /// <summary>
        /// The stereo condition the adopted body surfaces are drawn with, as the upload that wrote them settled it.
        /// Only the forward arguments carry the doubling; the shadow arguments hold the logical instance count, as the
        /// shadow pass is not stereo.
        /// </summary>
        public bool DrawsSinglePassInstanced => _batch.SinglePassInstanced;


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
            Material provisionalShadowMaterial,
            int commandCapacity,
            int instanceCapacity,
            VpStencilSettings stencilSettings,
            out VpLogicalCutDisplay display)
        {
            return TryCreate(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, stencilSettings, null, out display);
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
            Material provisionalShadowMaterial,
            int commandCapacity,
            int instanceCapacity,
            VpStencilSettings stencilSettings,
            Func<int> frameSource,
            out VpLogicalCutDisplay display)
        {
            display = null;
            if (storage == null || table == null || ledger == null || materialsBySourceIndex == null
                || commandCapacity <= 0 || instanceCapacity <= 0)
            {
                return false;
            }

            // Every size derived from the command capacity is worked out wide and must be an int, before any GPU
            // buffer, material or scratch is made.
            if (!TryDeriveCapacities(commandCapacity, out DerivedCapacities derived))
            {
                return false;
            }

            // The settings are taken as given: a colour limit past what the materials can order is refused, not cut
            // down, and there is no default to fall back on.
            if (!stencilSettings.IsValid(VpStencilCapMaterials.MaxColors, out _))
            {
                return false;
            }

            // Casting shadows at all means casting both kinds, so the two casters are given together or not at all.
            // One of them alone is refused, in either direction and for the same reason: with only the one-sided
            // caster a provisional split would cast a one-sided shadow, which looks like an ordinary shadow while
            // being the wrong one; with only the two-sided caster nothing would cast at all, because a display with
            // no one-sided caster issues no shadow call. Both silent, both wrong, both refused here.
            if ((shadowMaterial == null) != (provisionalShadowMaterial == null))
            {
                return false;
            }

            VpGpuIndexedGeometryBuffers buffers = null;
            VpIndexedIndirectDrawBatch batch = null;
            VpStencilCapMaterials stencilMaterials = null;
            bool taken = false;
            try
            {
                buffers = new VpGpuIndexedGeometryBuffers(storage.VertexCapacity, storage.IndexCapacity);
                batch = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity);

                // One material set for every camera: the queues order the colours, and each camera's batch carries its
                // own buffers and properties. The stencil batches themselves come with the cameras.
                if (!VpStencilCapMaterials.TryCreate(stencilSettings.maxStencilColors, out stencilMaterials))
                {
                    return false;
                }

                display = new VpLogicalCutDisplay(
                    storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial, buffers,
                    batch, stencilMaterials, stencilSettings, commandCapacity, instanceCapacity, derived, frameSource);
                taken = true;
                return true;
            }
            finally
            {
                if (!taken)
                {
                    stencilMaterials?.Dispose();
                    batch?.Dispose();
                    buffers?.Dispose();
                }
            }
        }

        // ----- cameras ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Registers <paramref name="camera"/> for stencil work: a stencil batch of its own is made for it, sized like
        /// every other camera's. False, making nothing, when the camera is null or already registered, or every slot of
        /// <see cref="VpStencilSettings.cameraCapacity"/> is taken.
        /// </summary>
        public bool TryRegisterCamera(Camera camera)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();
            if (ReferenceEquals(camera, null) || FindCamera(camera) != null)
            {
                return false;
            }

            for (int i = 0; i < _cameraStencils.Length; i++)
            {
                if (_cameraStencils[i] != null)
                {
                    continue;
                }

                var batch = new VpStencilCapBatch(
                    _settings.maxStencilColors, _stencilCommandCapacity, _stencilCommandCapacity,
                    _stencilCapVertexCapacity, _stencilCapIndexCapacity);
                _cameraStencils[i] = new CameraStencil { camera = camera, batch = batch };
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gives back <paramref name="camera"/>'s stencil batch. Refused while that camera has draws registered in the
        /// current frame, since they read its buffers until the frame is drawn; a later frame may let it go. False
        /// also for a camera that is not registered.
        /// </summary>
        public bool TryUnregisterCamera(Camera camera)
        {
            ThrowIfDisposed();
            ThrowIfPreparing();
            for (int i = 0; i < _cameraStencils.Length; i++)
            {
                CameraStencil slot = _cameraStencils[i];
                if (slot == null || !ReferenceEquals(slot.camera, camera))
                {
                    continue;
                }

                if (slot.drawnFrame == CurrentFrame)
                {
                    return false;
                }

                RetireStencil(slot.batch);
                _cameraStencils[i] = null;
                return true;
            }

            return false;
        }

        /// <summary>The monoscopic spelling: the camera's own position and non-GPU projection stand for both eyes.</summary>
        public bool TryPrepareCamera(Camera camera)
        {
            if (ReferenceEquals(camera, null))
            {
                throw new ArgumentNullException(nameof(camera));
            }

            var eye = new VpCapEye(camera.transform.position, camera.projectionMatrix * camera.worldToCameraMatrix);
            return TryPrepareCamera(camera, eye, eye);
        }

        /// <summary>
        /// Makes <paramref name="camera"/>'s stencil arrangement for this frame from the adopted snapshot and uploads
        /// it, every colour at once, into that camera's own batch. It may be called again, with another view, as long
        /// as the camera has not drawn in this frame; nothing about the ledger, the body or the geometry is read again
        /// or transferred.
        /// <para>
        /// False when the camera is not registered, has already drawn in this frame, a cap could not be read into
        /// the tests, or the snapshot holds more cap records than a preparation has room for; the camera is then not
        /// prepared, nothing is uploaded, and <see cref="Render"/> refuses it. An upload refused within the capacity
        /// checked at adoption, or a GPU call that throws, stops the display.
        /// </para>
        /// <para>
        /// Works in this display's own scratch and allocates nothing (see the class notes). Calling into this display
        /// while a preparation is running -- from inside it -- throws before anything is changed.
        /// </para>
        /// </summary>
        public bool TryPrepareCamera(Camera camera, in VpCapEye left, in VpCapEye right)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();
            if (ReferenceEquals(camera, null))
            {
                throw new ArgumentNullException(nameof(camera));
            }

            if (!_hasSnapshot || _openFrame != CurrentFrame)
            {
                throw new InvalidOperationException(
                    "TryBeginFrame has not opened this frame, so there is no snapshot to prepare a camera for");
            }

            CameraStencil slot = FindCamera(camera);
            if (slot == null || slot.drawnFrame == CurrentFrame)
            {
                return false;
            }

            _preparing = true;
            try
            {
                // Not prepared until this one has been uploaded.
                slot.preparedFrame = int.MinValue;
                if (!TryArrange(
                        left, right, out int commands, out int capIndices, out int colours,
                        out VpStencilPreparation preparation))
                {
                    return false;
                }

                // Uploaded by count: the scratch is the stencil capacity long, and only what this arrangement filled
                // is checked, sent and drawn.
                try
                {
                    if (!slot.batch.TryUpload(
                            _candidateStencilCommands, commands, _candidateStencilTransforms, _candidateStencilClips,
                            _capVertices, _capVertexCount, _candidateCapIndices, capIndices, _candidateStencilColors,
                            colours, _batch.SinglePassInstanced))
                    {
                        _broken = true;
                        throw new InvalidOperationException(
                            "a camera's stencil batch refused an arrangement inside the capacity checked when the "
                            + "snapshot was adopted; this display stops");
                    }
                }
                catch
                {
                    _broken = true;
                    throw;
                }

                slot.preparedFrame = CurrentFrame;
                slot.preparedGeneration = _generation;
                slot.preparation = preparation;
                return true;
            }
            finally
            {
                ClearPreparationScratch();
                _preparing = false;
            }
        }

        /// <summary>
        /// How many cap records one preparation may take, at most the room its scratch was made with. Lowered only by
        /// tests, to reach the refusal of a snapshot that holds more records than a preparation has room for, which
        /// the collection's own refusal otherwise keeps from happening.
        /// </summary>
        internal int PreparationRecordLimit
        {
            get => _preparationRecordLimit;
            set
            {
                if (value < 0 || value > _capRecordCapacity)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _preparationRecordLimit = value;
            }
        }

        /// <summary>Whether a preparation is running now. For tests, which look in from a frame source.</summary>
        internal bool IsPreparing => _preparing;

        /// <summary>
        /// How many looks the preparation scratch still holds -- at cap vertices, conditions or caps. Zero whenever no
        /// preparation is running. For tests.
        /// </summary>
        internal int HeldPreparationLooks
        {
            get
            {
                int held = 0;
                for (int i = 0; i < _prepTargets.Length; i++)
                {
                    held += _prepTargets[i].visibleCaps.IsNull && _prepTargets[i].conditions.constraints.IsNull ? 0 : 1;
                    held += _prepKeptTargets[i].visibleCaps.IsNull && _prepKeptTargets[i].conditions.constraints.IsNull ? 0 : 1;
                    held += _prepConditions[i].constraints.IsNull ? 0 : 1;
                }

                for (int i = 0; i < _prepCaps.Length; i++)
                {
                    held += _prepCaps[i].IsNull ? 0 : 1;
                }

                for (int i = 0; i < _prepConstraints.Length; i++)
                {
                    held += _prepConstraints[i].face.scope == null ? 0 : 1;
                }

                return held;
            }
        }

        /// <summary>One colour range of what <paramref name="camera"/>'s batch holds from its last upload. For tests.</summary>
        internal bool TryGetPreparedColor(Camera camera, int index, out VpStencilCapColor color)
        {
            CameraStencil slot = FindCamera(camera);
            if (slot == null)
            {
                color = default;
                return false;
            }

            return slot.batch.TryGetColor(index, out color);
        }

        /// <summary>
        /// The looks a preparation took -- at the adopted cap vertices, and at its own conditions and caps -- are let
        /// go when it ends, so that none of them outlives it or crosses into another adoption.
        /// </summary>
        private void ClearPreparationScratch()
        {
            int used = _prepRecordsUsed;
            Array.Clear(_prepTargets, 0, used);
            Array.Clear(_prepConditions, 0, used);
            Array.Clear(_prepKeptTargets, 0, used);
            Array.Clear(_prepConstraints, 0, used * VpCapCompatibility.SingleCutConstraints);
            Array.Clear(_prepCaps, 0, used * VpCapProjectionConflict.SingleCutCaps);
            _prepConditionList.SetCount(0);
            _prepKeptTargetList.SetCount(0);
            _prepKeptGroupOfList.SetCount(0);
            _prepRecordsUsed = 0;
        }

        /// <summary>What <paramref name="camera"/>'s last preparation made, and what its stencil batch has counted.</summary>
        public bool TryGetCameraStencil(Camera camera, out VpStencilPreparation preparation, out VpStencilCameraCounts counts)
        {
            CameraStencil slot = FindCamera(camera);
            if (slot == null)
            {
                preparation = default;
                counts = default;
                return false;
            }

            preparation = slot.preparation;
            counts = new VpStencilCameraCounts(
                slot.batch.Uploads, slot.batch.BufferWrites, slot.batch.StencilInitIssues, slot.batch.VolumeIssues,
                slot.batch.CapIssues, slot.batch.ColorCount, slot.batch.SinglePassInstanced,
                slot.preparedFrame == CurrentFrame && slot.preparedGeneration == _generation);
            return true;
        }

        private CameraStencil FindCamera(Camera camera)
        {
            foreach (CameraStencil slot in _cameraStencils)
            {
                if (slot != null && ReferenceEquals(slot.camera, camera))
                {
                    return slot;
                }
            }

            return null;
        }

        private int SumStencil(Func<VpStencilCapBatch, int> count)
        {
            int n = 0;
            foreach (CameraStencil slot in _cameraStencils)
            {
                if (slot != null)
                {
                    n += count(slot.batch);
                }
            }

            return n;
        }

        private void RetireStencil(VpStencilCapBatch batch)
        {
            _retiredStencilUploads += batch.Uploads;
            _retiredStencilBufferWrites += batch.BufferWrites;
            _retiredStencilInitIssues += batch.StencilInitIssues;
            _retiredStencilVolumeIssues += batch.VolumeIssues;
            _retiredStencilCapIssues += batch.CapIssues;
            batch.Dispose();
        }

        /// <summary>
        /// DESIGN 5.6's order over the adopted snapshot, for two eyes: visibility, compatibility groups over every cut
        /// condition, the groups with no cap seen left out, colours within the limit, and the arrangement, colour by
        /// colour, into the stencil scratch.
        /// </summary>
        private bool TryArrange(
            in VpCapEye left, in VpCapEye right, out int commandCount, out int capIndexCount, out int colourCount,
            out VpStencilPreparation preparation)
        {
            commandCount = 0;
            capIndexCount = 0;
            colourCount = 0;
            preparation = default;
            int records = _capRecordCount;

            // Room is decided before anything is read into the scratch, let alone uploaded.
            if (records > _preparationRecordLimit)
            {
                return false;
            }

            // Every slot up to here may hold a look from this preparation from now on, and is cleared when it ends.
            _prepRecordsUsed = records;
            VpCapProjectionTarget[] targets = _prepTargets;
            VpCapCompatibilityTarget[] conditions = _prepConditions;
            bool[] seen = _prepSeen;
            for (int r = 0; r < records; r++)
            {
                if (!VpCapVisibility.TryClassify(this, r, left, right, _settings.facingEpsilon, out VpCapVisibilityVerdict verdict)
                    || !VpCapProjectionConflict.TryGetSingleCutTarget(
                        this, r, left, right, _settings.facingEpsilon,
                        _prepConstraints, r * VpCapCompatibility.SingleCutConstraints,
                        _prepCaps, r * VpCapProjectionConflict.SingleCutCaps, out targets[r]))
                {
                    return false;
                }

                seen[r] = verdict.Keep;
                conditions[r] = targets[r].conditions;
            }

            int[] groupOfRecord = _prepGroupOfRecord;
            _prepConditionList.SetCount(records);
            int groups = records == 0
                ? 0
                : VpCapCompatibility.Classify(_prepConditionList, _settings.planeEpsilon, _settings.offsetEpsilon, groupOfRecord);

            // A group is drawn when any cap in it is seen; then every target in it keeps its volumes, and only the
            // caps that were seen are drawn.
            bool[] groupSeen = _prepGroupSeen;
            bool[] capIssued = _prepCapIssued;
            SelectStencilWork(seen, records, groupOfRecord, groups, groupSeen, capIssued);

            int[] keptGroup = _prepKeptGroup;
            int kept = 0;
            for (int g = 0; g < groups; g++)
            {
                keptGroup[g] = groupSeen[g] ? kept++ : -1;
            }

            int[] keptRecords = _prepKeptRecords;
            int keptCount = 0;
            for (int r = 0; r < records; r++)
            {
                if (keptGroup[groupOfRecord[r]] >= 0)
                {
                    keptRecords[keptCount++] = r;
                }
            }

            VpCapProjectionTarget[] keptTargets = _prepKeptTargets;
            int[] keptGroupOf = _prepKeptGroupOf;
            for (int k = 0; k < keptCount; k++)
            {
                keptTargets[k] = targets[keptRecords[k]];
                keptGroupOf[k] = keptGroup[groupOfRecord[keptRecords[k]]];
            }

            int[] colourOfGroup = _prepColourOfGroup;
            int max = _settings.maxStencilColors;
            if (kept > 0)
            {
                _prepKeptTargetList.SetCount(keptCount);
                _prepKeptGroupOfList.SetCount(keptCount);
                VpStencilColors.Assign(
                    _prepKeptTargetList, _prepKeptGroupOfList, kept, left, right, _settings.ndcMargin,
                    _settings.planeEpsilon, _settings.offsetEpsilon, max, colourOfGroup);
            }

            // The arrangement, colour by colour in their order: each colour's volumes and then its caps are contiguous.
            int inLast = 0;
            for (int g = 0; g < kept; g++)
            {
                inLast += colourOfGroup[g] == max - 1 ? 1 : 0;
            }

            int ordinary = 0;
            int volumeTargets = 0;
            int capsDrawn = 0;
            for (int colour = 0; colour < max; colour++)
            {
                int volumeStart = commandCount;
                int capStart = capIndexCount;
                bool any = false;
                for (int k = 0; k < keptCount; k++)
                {
                    if (colourOfGroup[keptGroupOf[k]] != colour)
                    {
                        continue;
                    }

                    any = true;
                    LogicalCutCapRecord record = _capRecords[keptRecords[k]];
                    AppendVolumes(record, ref commandCount);
                    volumeTargets++;
                    if (!capIssued[keptRecords[k]])
                    {
                        continue;
                    }

                    capsDrawn++;
                    for (int v = 1; v + 1 < record.vertexCount; v++)
                    {
                        _candidateCapIndices[capIndexCount++] = record.vertexStart;
                        _candidateCapIndices[capIndexCount++] = record.vertexStart + v;
                        _candidateCapIndices[capIndexCount++] = record.vertexStart + v + 1;
                    }
                }

                if (!any)
                {
                    continue;
                }

                if (colour < max - 1)
                {
                    ordinary++;
                }

                _candidateStencilColors[colourCount++] = new VpStencilCapColor(
                    volumeStart, commandCount - volumeStart, capStart, capIndexCount - capStart, ProvisionalCapColour);
            }

            preparation = new VpStencilPreparation(
                records, groups, groups - kept, colourCount, ordinary, inLast, volumeTargets, capsDrawn);
            return true;
        }

        /// <summary>
        /// Which stencil work each cap record takes: a group is kept when any of its caps is seen, every record of a
        /// kept group has its volumes issued, and a record's cap is issued only when it was seen itself. A cap that was
        /// not seen still counted towards its group's compatibility; it is only not drawn.
        /// </summary>
        /// <param name="groupKept">Written: per group, whether any of its caps is seen.</param>
        /// <param name="capIssued">Written: per record, whether its cap is drawn.</param>
        internal static void SelectStencilWork(
            bool[] seen, int[] groupOfRecord, int groupCount, bool[] groupKept, bool[] capIssued)
        {
            SelectStencilWork(seen, seen.Length, groupOfRecord, groupCount, groupKept, capIssued);
        }

        /// <summary>The same over the first <paramref name="recordCount"/> records only.</summary>
        internal static void SelectStencilWork(
            bool[] seen, int recordCount, int[] groupOfRecord, int groupCount, bool[] groupKept, bool[] capIssued)
        {
            Array.Clear(groupKept, 0, groupCount);
            for (int r = 0; r < recordCount; r++)
            {
                groupKept[groupOfRecord[r]] |= seen[r];
            }

            for (int r = 0; r < recordCount; r++)
            {
                capIssued[r] = seen[r] && groupKept[groupOfRecord[r]];
            }
        }

        /// <summary>
        /// The volumes of the side a cap closes: every adopted command of that body, once, with that side's own
        /// instance transform and clip record -- the same geometry and range the body is drawn with.
        /// </summary>
        private void AppendVolumes(LogicalCutCapRecord record, ref int commandCount)
        {
            int instance = 0;
            for (int c = 0; c < _commandCount; c++)
            {
                VpIndirectCommand source = _commands[c];
                for (int i = 0; i < source.instanceCount; i++, instance++)
                {
                    LogicalCutDisplaySide side = _sides[instance];
                    if (side.source != record.source || side.side != record.side)
                    {
                        continue;
                    }

                    _candidateStencilCommands[commandCount] = new VpIndirectCommand(source.range, source.localBounds, 1);
                    _candidateStencilTransforms[commandCount] = _transforms[instance];
                    _candidateStencilClips[commandCount] = _clips[instance];
                    commandCount++;
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
            ThrowIfPreparing();

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
            ThrowIfPreparing();

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
        /// Registers this frame's draws for <paramref name="camera"/>: one forward call per run of commands sharing a
        /// material, each followed by its shadow call when a shadow material was given, and then that camera's own
        /// stencil work as <see cref="TryPrepareCamera(Camera)"/> made it. Drawing writes nothing. The camera must be
        /// registered and prepared in this frame for the adopted snapshot; otherwise this throws before registering
        /// anything.
        /// </summary>
        public void Render(int layer, Camera camera)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();

            if (camera == null)
            {
                throw new ArgumentNullException(
                    nameof(camera), "the stencil work is prepared per camera, so a draw names the camera it is for");
            }

            if (!_hasSnapshot || _openFrame != CurrentFrame)
            {
                throw new InvalidOperationException(
                    "TryBeginFrame has not opened this frame, so there is nothing to draw. Call it once per frame, before drawing.");
            }

            // Everything is checked before anything is registered: a camera that is not registered, not prepared this
            // frame, or prepared for an earlier snapshot draws nothing at all -- not the body without its stencil.
            CameraStencil cameraStencil = FindCamera(camera);
            if (cameraStencil == null)
            {
                throw new InvalidOperationException("this camera is not registered with the display");
            }

            if (cameraStencil.preparedFrame != CurrentFrame || cameraStencil.preparedGeneration != _generation)
            {
                throw new InvalidOperationException(
                    "this camera is not prepared for this frame's adopted snapshot; call TryPrepareCamera first");
            }

            _drawRegisteredThisFrame = true;
            cameraStencil.drawnFrame = CurrentFrame;

            // The surfaces, grouped by material exactly as before: one forward call per run of commands sharing one.
            int start = 0;
            while (start < _commandCount)
            {
                int end = start + 1;
                while (end < _commandCount && ReferenceEquals(_commandMaterials[end], _commandMaterials[start]))
                {
                    end++;
                }

                _batch.RenderForward(_commandMaterials[start], _properties, _buffers, layer, start, end - start, camera);
                start = end;
            }

            // The casters, grouped by something else: which side of DESIGN 5.4's division a command falls on. Cull is
            // a drawing state of the material, so a run cast one-sided and a run cast two-sided are separate draws,
            // over the same commands, the same transforms, the same clip records and the same offsets as the surfaces
            // above -- this frame's adopted snapshot, never a second reading of the ledger.
            if (_shadowMaterial != null)
            {
                start = 0;
                while (start < _commandCount)
                {
                    bool provisional = _commandProvisional[start];
                    int end = start + 1;
                    while (end < _commandCount && _commandProvisional[end] == provisional)
                    {
                        end++;
                    }

                    _batch.RenderShadows(
                        provisional ? _provisionalShadowMaterial : _shadowMaterial, _properties, _buffers, layer,
                        start, end - start, camera);
                    if (provisional)
                    {
                        TwoSidedShadowIssues++;
                    }
                    else
                    {
                        OneSidedShadowIssues++;
                    }

                    start = end;
                }
            }

            // The counting and the caps, after the surfaces: their queues put them after the opaque bodies, so each
            // cap is depth-tested against the surfaces of this frame and drawn only inside its side's opening.
            cameraStencil.batch.Render(_stencilMaterials, _buffers, layer, camera);
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

        /// <summary>The ledger this display reads, which is the scope its cap records' operation ids were issued in.</summary>
        internal LogicalCutLedger Ledger => _ledger;

        /// <summary>
        /// The body box and the placement one prepared cap's polygon was made from: the box in the geometry's own frame,
        /// and the object-to-world transform, without the separation. Read-only; false when there is no such cap or its
        /// body is no longer held.
        /// </summary>
        internal bool TryGetCapBody(int capIndex, out Bounds localBounds, out Matrix4x4 objectToWorld)
        {
            localBounds = default;
            objectToWorld = default;
            if (capIndex < 0 || capIndex >= _capRecordCount)
            {
                return false;
            }

            LogicalFragmentId source = _capRecords[capIndex].source;
            for (int i = 0; i < _shown.Count; i++)
            {
                Shown entry = _shown[i];
                if (entry.fragment == source && entry.capPrepared)
                {
                    localBounds = entry.capBounds;
                    objectToWorld = entry.capPlacement;
                    return true;
                }
            }

            return false;
        }

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

        /// <summary>
        /// One prepared cap's polygon as a look at the adopted snapshot's own vertices, in the order they are wound:
        /// nothing is copied. It is good only until another snapshot is adopted, which reuses those vertices, and is
        /// for a preparation to read while it runs; <see cref="TryGetCapVertex"/> is how anything else reads a cap.
        /// </summary>
        internal bool TryGetCapPolygon(int capIndex, out VpArrayRange<Vector3> polygon)
        {
            if (capIndex < 0 || capIndex >= _capRecordCount)
            {
                polygon = default;
                return false;
            }

            LogicalCutCapRecord record = _capRecords[capIndex];
            polygon = new VpArrayRange<Vector3>(_capVertices, record.vertexStart, record.vertexCount);
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
        /// Gives back everything this display owns: the buffers, the batches, and every geometry registration and
        /// display instance it took, each exactly once. Refused, changing nothing, while any camera has draws
        /// registered in the current frame; a later frame may dispose. The ledger, the storage and the borrowed materials are left
        /// alone — in particular no cut is completed, terminated or aborted by a display ending.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            ThrowIfPreparing();

            // Draws registered in this frame read these buffers until the frame is drawn, so nothing is let go before
            // the frame boundary. This is the frame boundary, not a confirmation that the GPU has finished.
            foreach (CameraStencil slot in _cameraStencils)
            {
                if (slot != null && slot.drawnFrame == CurrentFrame)
                {
                    throw new InvalidOperationException(
                        "a camera has draws registered in this frame; dispose the display after the frame has been drawn");
                }
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
            for (int i = 0; i < _cameraStencils.Length; i++)
            {
                if (_cameraStencils[i] != null)
                {
                    RetireStencil(_cameraStencils[i].batch);
                    _cameraStencils[i] = null;
                }
            }

            _stencilMaterials.Dispose();
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

            // 2. The fixed capacity is decided here, before anything is taken or uploaded. The cap records are bounded
            //    by the commands already -- two per split body, and a body has a command -- and are checked all the
            //    same, because a camera's preparation has room for no more than that.
            if (commandCount > _commandCapacity || instanceCount > _instanceCapacity
                || capRecordsNeeded > _capRecordCapacity)
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
                    _candidateCommandProvisional[command] = plan.split;
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

            // 5. The largest stencil arrangement this candidate could need, of every side and every cap in one
            //    colour: no camera's arrangement of it has more commands, cap vertices or cap indices.
            BuildLargestStencilArrangement(command, out int stencilCommands, out int capIndexCount, out int stencilColours);

            // 6. Both sides are asked before anything is written: the display batch's conditions are the capacity
            //    settled in step 2 and the shapes built here; the stencil side's are its fixed sizes and every
            //    registered camera's own non-writing judgement. So an ordinary refusal keeps the previous snapshot.
            //    This is a judgement of capacity and form, not a promise that each camera's later upload succeeds.
            VpIndirectCommand[] commands = Slice(_candidateCommands, command);
            Matrix4x4[] transforms = Slice(_candidateTransforms, instance);
            VpInstanceClip[] clips = Slice(_candidateClips, instance);
            bool stencilFits = stencilCommands <= _stencilCommandCapacity
                && capVertex <= _stencilCapVertexCapacity
                && capIndexCount <= _stencilCapIndexCapacity;
            if (stencilFits)
            {
                // Asked by count, as each camera's preparation uploads.
                foreach (CameraStencil slot in _cameraStencils)
                {
                    if (slot != null && !slot.batch.CanUpload(
                            _candidateStencilCommands, stencilCommands, _candidateStencilTransforms,
                            _candidateStencilClips, _candidateCapVertices, capVertex, _candidateCapIndices,
                            capIndexCount, _candidateStencilColors, stencilColours))
                    {
                        stencilFits = false;
                        break;
                    }
                }
            }

            if (!stencilFits)
            {
                GiveBackSecondInstancesTakenThisPass();
                _candidateSides.Clear();
                _candidateCapRecordCount = 0;
                return false;
            }

            // 7. The body's upload. The stencil arrangement is each camera's, uploaded when that camera is prepared
            //    from the snapshot adopted here. The stereo condition is read once, here; the cameras' stencil work
            //    takes the condition this upload settled, so the body and its caps are never on different eye counts.
            //    A GPU call that throws stops the display: what reached it cannot be established.
            bool singlePassInstanced = SinglePassInstanced;
            bool uploaded;
            try
            {
                uploaded = _batch.TryUpload(commands, transforms, clips, singlePassInstanced);
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
            AdoptCandidate(command, capVertex);
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
        /// <summary>
        /// Every split side's commands and every cap's fan, in one colour: the most any camera's arrangement of this
        /// candidate could hold. It is only asked about, never uploaded.
        /// </summary>
        private void BuildLargestStencilArrangement(
            int commandCount, out int stencilCommands, out int capIndexCount, out int colours)
        {
            // The scratch is the stencil capacity long, made with the display: twice the commands this candidate was
            // allowed, and every cap fanned.
            stencilCommands = 0;
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
                for (int side = 0; side < 2; side++, instance++)
                {
                    _candidateStencilCommands[stencilCommands] = one;
                    _candidateStencilTransforms[stencilCommands] = _candidateTransforms[instance];
                    _candidateStencilClips[stencilCommands] = _candidateClips[instance];
                    stencilCommands++;
                }
            }

            int index = 0;
            for (int r = 0; r < _candidateCapRecordCount; r++)
            {
                LogicalCutCapRecord record = _candidateCapRecords[r];
                for (int v = 1; v + 1 < record.vertexCount; v++)
                {
                    _candidateCapIndices[index++] = record.vertexStart;
                    _candidateCapIndices[index++] = record.vertexStart + v;
                    _candidateCapIndices[index++] = record.vertexStart + v + 1;
                }
            }

            capIndexCount = index;
            colours = stencilCommands > 0 || index > 0 ? 1 : 0;
            if (colours > 0)
            {
                _candidateStencilColors[0] = new VpStencilCapColor(0, stencilCommands, 0, index, ProvisionalCapColour);
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
        private void AdoptCandidate(int commandCount, int capVertexCount)
        {
            Matrix4x4[] transforms = _transforms;
            VpInstanceClip[] clips = _clips;
            _transforms = _candidateTransforms;
            _clips = _candidateClips;
            _candidateTransforms = transforms;
            _candidateClips = clips;

            VpIndirectCommand[] commands = _commands;
            Material[] materials = _commandMaterials;
            bool[] provisional = _commandProvisional;
            _commands = _candidateCommands;
            _commandMaterials = _candidateCommandMaterials;
            _commandProvisional = _candidateCommandProvisional;
            _candidateCommands = commands;
            _candidateCommandMaterials = materials;
            _candidateCommandProvisional = provisional;

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
            _capVertexCount = capVertexCount;
            _hasSnapshot = true;

            // Every camera's preparation was for the snapshot just replaced.
            _generation++;
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
                _candidateCommandProvisional = new bool[commandCount];
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

        private void ThrowIfPreparing()
        {
            if (_preparing)
            {
                throw new InvalidOperationException(
                    "a camera is being prepared; this display takes one call at a time and nothing from inside a preparation");
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
