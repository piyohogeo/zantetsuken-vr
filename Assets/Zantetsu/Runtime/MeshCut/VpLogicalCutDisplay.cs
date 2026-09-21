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
        /// The provisional split: the same parent geometry drawn once per render fragment, each clipped to its own
        /// region. A registered fragment is in this state once anything below it is drawn apart; a fragment below a
        /// registered one is in it while the adopted snapshot draws it or something below it.
        /// </summary>
        ProvisionalSplit = 3,
    }

    /// <summary>Why a <see cref="VpLogicalCutDisplay"/> stopped drawing. Each is its own reason, never merged.</summary>
    public enum LogicalCutDisplayHaltReason
    {
        /// <summary>It has not stopped.</summary>
        None = 0,

        /// <summary>A snapshot could not be built from the input (<see cref="VpMultiCutBuildOutcome.InvalidInput"/>).</summary>
        InvalidInput = 1,

        /// <summary>
        /// A retired fragment lies inside what the new snapshot would draw once
        /// (<see cref="VpMultiCutBuildOutcome.RetiredInsideAggregate"/>): the new snapshot cannot be built.
        /// </summary>
        RetiredInsideAggregate = 3,

        /// <summary>
        /// The new snapshot was refused for want of room, and the snapshot adopted earlier -- the one that would keep
        /// drawing -- draws a fragment retired since, anywhere, in an aggregate or not: keeping it would show what is no
        /// longer there, and nothing of the new one is adopted in part to take it away.
        /// </summary>
        RetiredWhileShown = 4,
    }

    /// <summary>
    /// One instance the display drew: one render fragment of one registration, for one of its commands.
    /// </summary>
    public readonly struct LogicalCutDisplaySide
    {
        internal LogicalCutDisplaySide(
            LogicalFragmentId source,
            int renderFragment,
            CutOperationId operation,
            float side,
            bool published,
            LogicalFragmentId fragment,
            bool fixedByAnchors,
            VpInstanceClip clip)
        {
            this.source = source;
            this.renderFragment = renderFragment;
            this.operation = operation;
            this.side = side;
            this.published = published;
            this.fragment = fragment;
            this.fixedByAnchors = fixedByAnchors;
            this.clip = clip;
        }

        /// <summary>The fragment this display entry was given, which is the body the geometry belongs to.</summary>
        public readonly LogicalFragmentId source;

        /// <summary>The render fragment of the adopted snapshot this instance draws.</summary>
        public readonly int renderFragment;

        /// <summary>
        /// The cut this render fragment is the side of: its own pending cut when it is drawn as one side of it, else the
        /// cut that made it. Unset for a body drawn as it was registered.
        /// </summary>
        public readonly CutOperationId operation;

        /// <summary>+1 or -1 for a side of that cut; 0 for a body drawn as it was registered.</summary>
        public readonly float side;

        /// <summary>
        /// Whether this side is a **published** child. Before publication a side has no child of its own: the display
        /// does not issue an id early and does not treat its own slot as a child that exists (DESIGN 7.1.2).
        /// </summary>
        public readonly bool published;

        /// <summary>The published child this side is, or an unset id before publication.</summary>
        public readonly LogicalFragmentId fragment;

        /// <summary>
        /// Whether the anchors make this side fixed, read from that cut's own settled distribution; the display has no
        /// judgement of its own about anchors. Whether it is drawn apart is the whole lineage's, in <see cref="offset"/>.
        /// </summary>
        public readonly bool fixedByAnchors;

        /// <summary>The clip record this side was drawn with: its selected boundaries, at most eight.</summary>
        public readonly VpInstanceClip clip;
    }

    /// <summary>
    /// One drawing cap this display has prepared: the <c>TemporaryRenderCapRecord</c> of DESIGN 5.2, being one render
    /// fragment and one of its selected boundaries, with the polygon that side would be masked inside.
    /// <para>
    /// **It is an input to the stencil, not a plate.** The polygon is the cross-section of the body's bounds box, cut by
    /// the render fragment's other selected half-spaces -- not the cut's real outline -- and it is only ever drawn
    /// through the stencil that restricts it to the body.
    /// </para>
    /// <para>
    /// **One record per render fragment and selected boundary**, not one per submesh and never one for an Ignored
    /// boundary. A polygon cut down to nothing keeps its record with no vertices: a normal empty result, never drawn.
    /// </para>
    /// <para>
    /// **The vertices are in world space**, the placement snapshot applied and nothing added after it, and they are
    /// wound so that <c>Cross(p1 - p0, p2 - p0)</c> points along <see cref="outwardNormal"/>.
    /// </para>
    /// </summary>
    public readonly struct LogicalCutCapRecord
    {
        internal LogicalCutCapRecord(
            LogicalFragmentId source,
            int renderFragment,
            CutOperationId operation,
            float side,
            bool published,
            LogicalFragmentId fragment,
            bool fixedByAnchors,
            Vector4 worldPlane,
            Vector3 outwardNormal,
            int vertexStart,
            int vertexCount)
        {
            this.source = source;
            this.renderFragment = renderFragment;
            this.operation = operation;
            this.side = side;
            this.published = published;
            this.fragment = fragment;
            this.fixedByAnchors = fixedByAnchors;
            this.worldPlane = worldPlane;
            this.outwardNormal = outwardNormal;
            this.vertexStart = vertexStart;
            this.vertexCount = vertexCount;
        }

        /// <summary>The fragment this display entry was given, which is the body the cross-section was taken of.</summary>
        public readonly LogicalFragmentId source;

        /// <summary>The render fragment of the adopted snapshot this cap closes.</summary>
        public readonly int renderFragment;

        /// <summary>The admitted cut whose adopted face this cap lies in.</summary>
        public readonly CutOperationId operation;

        /// <summary>+1 or -1: which side of that face this cap belongs to.</summary>
        public readonly float side;

        /// <summary>
        /// Whether that side is a **published** child. Before publication a cap belongs to the source and the
        /// operation and to no child, exactly as the drawn sides do: no child id is invented early.
        /// </summary>
        public readonly bool published;

        /// <summary>The published child on this side of that face, or an unset id before publication.</summary>
        public readonly LogicalFragmentId fragment;

        /// <summary>Whether the anchors make this side of that face fixed. A fixed side has a cap like any other.</summary>
        public readonly bool fixedByAnchors;

        /// <summary>The adopted face in world space, as <c>(n.xyz, d)</c> with n normalized.</summary>
        public readonly Vector4 worldPlane;

        /// <summary>The outward normal of the side this cap closes, in world space, which the winding agrees with.</summary>
        public readonly Vector3 outwardNormal;

        /// <summary>How many vertices this cap's polygon has: 0 to 14. Zero is an empty cap, kept and never drawn.</summary>
        public readonly int vertexCount;

        internal readonly int vertexStart;
    }

    /// <summary>
    /// Shows logical cuts from the ledger's own state (DESIGN 5.1, 5.2, 5.6; D-180, D-181): each registered geometry, and
    /// once cuts are admitted and their inputs are ready, that **same** geometry drawn once per render fragment of the
    /// multi-cut snapshot (<see cref="VpMultiCutSnapshot"/>), each clipped to its selected boundaries and drawn at the
    /// placement it follows. Nothing here moves anything for the display. One cut and several go through this one path.
    /// <para>
    /// **The ledger is the state; this only reads it.** Admission, anchor distribution, publication, Abort and Stale
    /// all belong to <see cref="LogicalCutLedger"/>, and nothing here keeps a second copy of them or a publication
    /// state machine of its own. Every collection reads the ledger and builds what to draw from it. This never calls
    /// Publish, Abort, CompleteGeometry or Terminate — a display cannot advance a cut — and updating or ending a
    /// display therefore never returns a share of the incomplete budget.
    /// </para>
    /// <para>
    /// **Registrations.** <see cref="TryShow(LogicalFragmentId, VpStoredGeometry, Matrix4x4, Matrix4x4, IReadOnlyCollection{VpClipBoundary})"/>
    /// takes a live fragment with its geometry, its placement, the mapping from its lineage's frame to the geometry's
    /// coordinates and the boundaries the geometry already reflects, all stated by the caller. The older
    /// <see cref="TryShow(LogicalFragmentId, VpStoredGeometry, Matrix4x4)"/> is the same call under its caller contract:
    /// the whole lineage is in the geometry's own frame (identity) and nothing is reflected. Both share one registry,
    /// and a fragment that is an ancestor or a descendant of one already registered is refused.
    /// </para>
    /// <para>
    /// **What each logical state looks like.** Before admission, the whole body. Admitted with the anchor
    /// distribution prepared, one render fragment per side; admitted without it, still the whole, told apart as
    /// <see cref="LogicalCutDisplayState.AwaitingInputs"/>. Published, the same render fragments carried over to the
    /// children, which the sides then name; further cuts divide them again. Up to eight boundaries are drawn per
    /// render fragment; past that a lineage is drawn once as the shape before its first Ignored boundary, and an
    /// Ignored boundary makes no clip, offset, volume or cap. Aborted below a registered fragment, the retired part is
    /// drawn as nothing; a registered fragment that is itself retired is let go. Stale goes back to what was before.
    /// </para>
    /// <para>
    /// **One geometry, drawn many times.** Every render fragment addresses the same stored geometry, the same vertex
    /// and index buffers and the same index range: one command per submesh, whose instance count is the number of
    /// render fragments. Nothing is cut, duplicated, re-meshed or transferred again. The geometry registration is held
    /// once, and one display instance reference is held per render fragment (never fewer than one); each reference
    /// this display took is given back exactly once.
    /// </para>
    /// <para>
    /// **Body, depth and shadow read one record; an ordinary colour's stencil volume reads its own face.** Each render
    /// fragment's clip record (every selected half-space) and its placement are what the surfaces, the depth and both
    /// casters are drawn with. In an ordinary colour a stencil volume is drawn with the same
    /// geometry and placement but clipped by one face only -- the cap's own face and kept side
    /// (<see cref="VpCapJob.volumeClip"/>) -- never by the render fragment's other selected faces. The last colour's
    /// volumes are the old kind: the render fragment's own clip record, every selected face (D-186). The kerf is zero:
    /// no plane is moved to open a gap.
    /// </para>
    /// <para>
    /// **Caps, per camera (DESIGN 5.6, D-183).** One cap per render fragment and selected boundary, up to fourteen
    /// vertices; every camera's arrangement comes from <see cref="VpCapJobClassification"/> over every registration's
    /// render fragments together, and from nothing else. A cap whose drawing polygon is not empty and which the both-eye
    /// visibility test keeps is a cap job. Jobs whose volumes are exactly the same volume form one volume group, whose
    /// volume is issued once: the representative render fragment's draw ranges (one command per submesh) and placement,
    /// clipped by the cap's own face only (<see cref="VpCapJob.volumeClip"/>), applied once -- never the
    /// render fragment's clip of every selected face, which stays the body's, the depth's and the shadow's. Each job's
    /// cap is its clipped drawing polygon, fanned. Groups are given ordinary colours from the initial sections'
    /// projection in both eyes, at most the limit less one of them (DESIGN D-185, D-186). The groups that fit no ordinary
    /// colour go, whole, to the last colour, which is drawn the old way: after its initialisation, one volume per render
    /// fragment of its jobs, each once -- that render fragment's commands (every submesh) with its body's own clip record
    /// of every selected face -- and then those jobs' caps, each once; a cap drawn in an ordinary colour is never drawn
    /// again there. When nothing is left the last colour is not issued at all. What the last colour draws wrongly is
    /// accepted (DESIGN 5.2, exception 8); the colour limit alone never refuses a camera. Within an ordinary colour the
    /// order is initialisation, every volume group of the colour, every cap job of the colour. The draw ranges each
    /// registration's volumes are compared and drawn with are a table built with the snapshot, in its registration
    /// order, and adopted with it. Every cap is shaded by the one shading of DESIGN 5.3 that the body and the real caps
    /// use (<c>VpShadeSurface</c>), with its own outward normal: those normals are put on the GPU once per adoption, one
    /// per cap vertex of the adopted snapshot, and this display's cap materials read them. Every cap is drawn in
    /// DESIGN 5.3's colour for a temporary cut face
    /// (<see cref="VpCutSurfaceColour.CurrentProvisional"/>: the shared ordinary colour, or red while the one debug
    /// switch is on), read once per preparation and the same in every colour, the last one included.
    /// </para>
    /// <para>
    /// **One stencil batch per registered camera.** A camera is registered with <see cref="TryRegisterCamera"/>, up to
    /// the fixed <see cref="VpStencilSettings.cameraCapacity"/>, and owns a stencil batch of its own for as long as it is
    /// registered, so preparing one camera never writes the buffers another camera's registered draws read. Once a
    /// camera's draws are registered in a frame it is not prepared again and not unregistered in that frame.
    /// </para>
    /// <para>
    /// **Snapshot and adoption.** A collection builds a new snapshot beside the adopted one, over every registration
    /// together, and adopts it only when all of it was built and it fits: the commands, the instances, the display
    /// instances it needs, and the largest stencil arrangement it could need against every registered camera's batch.
    /// A capacity shortfall alone keeps the previous snapshot drawing, whole: nothing of the new one is adopted or
    /// uploaded -- but only when that snapshot draws no fragment retired since it was adopted; otherwise the display
    /// stops (<see cref="LogicalCutDisplayHaltReason.RetiredWhileShown"/>). The body's upload is asked, by the counts and
    /// contents it will send, before anything is written; an upload refused after that is not a shortfall and stops the
    /// display as broken. A preparation belongs to the frame and the adopted snapshot it was made for.
    /// </para>
    /// <para>
    /// **What stops the display.** A snapshot that cannot be built for a reason other than room -- a retired fragment
    /// inside what would be drawn once (<see cref="VpMultiCutBuildOutcome.RetiredInsideAggregate"/>), input that
    /// cannot be built from, a distribution that is not settled -- is not a reason to keep drawing the previous one,
    /// which may show what is no longer there. The display then stops for good: <see cref="IsHalted"/> is true,
    /// <see cref="HaltReason"/> keeps the first reason, collections and new bodies are refused, and preparing or drawing
    /// throws. This is decided when a frame is collected, before that frame registers any draw. Nothing is repaired,
    /// retried or dropped, and nothing is released on the spot: <see cref="Dispose"/> gives everything back under the
    /// usual frame guard. What decides it is read from the ledger before any room is taken, so a shortage of room is
    /// never what hides it (see <see cref="VpMultiCutSnapshot"/> for the one exception, an overflow).
    /// </para>
    /// <para>
    /// **A preparation allocates nothing once the display is made.** The classification's room, the arrangement and the
    /// batches are made with the display at the sizes its capacities give, filled to a count each time and never
    /// grown. A cap is read as a look at the adopted snapshot's own vertices, held only while the preparation runs. A
    /// preparation runs to its end before another starts: a call into this display made while one is running is
    /// refused before it changes anything.
    /// </para>
    /// <para>
    /// **Two casters, and who owns them.** A body with any render fragment clipped is cast two-sided and every other
    /// body one-sided, which is DESIGN 5.4's division. Both materials are the caller's; this class creates neither and
    /// disposes neither, and it refuses to be made with one of them alone.
    /// </para>
    /// <para>
    /// **One stereo condition for the whole arrangement.** <see cref="SinglePassInstanced"/> is read in one place —
    /// where a collection uploads — and given to both batches together.
    /// </para>
    /// <para>
    /// **When updates happen.** <see cref="TryBeginFrame"/> collects the ledger's state and settles the body's buffers
    /// for that frame; <see cref="TryPrepareCamera(Camera)"/> then makes one camera's stencil arrangement from what was
    /// settled; <see cref="Render"/> registers that camera's draws. A frame is identified by the engine's frame
    /// counter, so collecting again inside one frame changes nothing. A GPU call that throws stops the display as
    /// broken: what reached the GPU cannot be established, and only <see cref="Dispose"/> is left. Main thread only.
    /// </para>
    /// </summary>
    /// <summary>
    /// One surface boundary a geometry commit really published (DESIGN 4.5.6, 8): the cut it came from, the two sides
    /// it lies between, the adopted plane and the frame that plane is in, and how much surface the cut really made.
    /// A cut that made no surface -- the plane missed the geometry, or one side is empty -- has no record at all.
    /// <para>
    /// This is not the "already reflected" set a registration carries. That set says which temporary clip and cap are
    /// no longer drawn, and a cut that made no surface still belongs to it; this says which boundary exists in the
    /// geometry. The cut operation is the identity of both the boundary and its plane: no new permanent id is made.
    /// </para>
    /// </summary>
    public readonly struct LogicalCutBoundaryRecord
    {
        internal LogicalCutBoundaryRecord(
            CutOperationId operation,
            LogicalFragmentId positive,
            LogicalFragmentId negative,
            VpStoredGeometry positiveGeometry,
            VpStoredGeometry negativeGeometry,
            Matrix4x4 positiveObjectToWorld,
            Matrix4x4 negativeObjectToWorld,
            Vector4 plane,
            Matrix4x4 lineageToGeometryLocal,
            int capTriangles,
            long generation)
        {
            this.operation = operation;
            this.positive = positive;
            this.negative = negative;
            this.positiveGeometry = positiveGeometry;
            this.negativeGeometry = negativeGeometry;
            this.positiveObjectToWorld = positiveObjectToWorld;
            this.negativeObjectToWorld = negativeObjectToWorld;
            this.plane = plane;
            this.lineageToGeometryLocal = lineageToGeometryLocal;
            this.capTriangles = capTriangles;
            this.generation = generation;
        }

        /// <summary>The cut this boundary is of, which is the identity of the boundary and of its plane.</summary>
        public readonly CutOperationId operation;

        /// <summary>The fragment on the plane's positive side, and the one on its negative side.</summary>
        public readonly LogicalFragmentId positive, negative;

        /// <summary>
        /// The geometry each side was at the commit, named and not owned: what it was, not what the fragment is
        /// afterwards. A later cut of either side replaces that fragment's geometry and does not change this, and
        /// nothing here keeps the geometry alive or delays its retirement.
        /// </summary>
        public readonly VpStoredGeometry positiveGeometry, negativeGeometry;

        /// <summary>Where each side's geometry stood at the commit: the placement it was drawn at.</summary>
        public readonly Matrix4x4 positiveObjectToWorld, negativeObjectToWorld;

        /// <summary>The adopted plane, in the lineage's frame -- the frame it was admitted in.</summary>
        public readonly Vector4 plane;

        /// <summary>From that lineage frame to the coordinates the two sides' geometry is in.</summary>
        public readonly Matrix4x4 lineageToGeometryLocal;

        /// <summary>Cap triangles the cut made. Always positive: a record exists only where a surface does.</summary>
        public readonly int capTriangles;

        /// <summary>The display generation this was published at.</summary>
        public readonly long generation;
    }

    public sealed class VpLogicalCutDisplay : IDisposable
    {
        private sealed class Shown
        {
            public LogicalFragmentId fragment;
            public VpStoredGeometry geometry;
            public VpGeometryReference reference;

            // Every display instance this registration holds, the one taken with the geometry first: one per render
            // fragment it is drawn as, and never fewer than one.
            public readonly List<VpDisplayInstanceReference> instances = new List<VpDisplayInstanceReference>(2);
            public int takenThisPass;

            public Matrix4x4 objectToWorld;

            public Matrix4x4 lineageToGeometryLocal;
            public VpClipBoundary[] reflected;
            public VpIndirectCommand[] commands;
            public Material[] commandMaterials;

            // The draw range of every command, in order: what this registration's stencil volumes are drawn from, and
            // what tells one volume from another (VpCapJobGeometry). Made once, when the body is taken in.
            public VpGeometryRange[] ranges;

            /// <summary>
            /// The bounds of the vertices this geometry's indices reach, in the geometry's own frame, measured once
            /// when the body was taken in: the whole body's, not one submesh's.
            /// </summary>
            public Bounds localBounds;

            // What the adopted snapshot shows of it.
            public bool splitShown;
            public bool awaitingShown;
            public int renderFragmentsShown;

            // What the collection under way decided, before anything is changed.
            public bool dropping;
            public bool awaiting;
            public bool split;
            public bool clipped;
            public int renderFragments;
            public int firstRenderFragment;
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

        // The outward normal of every cap vertex of the adopted snapshot, in its order: the scratch it is gathered in
        // and the buffer the cap materials read. Fixed at the cap vertex capacity, written by count, never grown.
        private readonly Vector4[] _capNormals;
        private readonly GraphicsBuffer _capNormalBuffer;
        private int _capNormalCount;
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

        // Registrations a committed cut replaced. They are no longer collected, but what they hold is still what the
        // adopted snapshot is drawing, so they are let go only after the next adoption has taken their place.
        private readonly List<Shown> _retiring = new List<Shown>(2);

        // The surface boundaries the commits have published, in the order they were published.
        private readonly List<LogicalCutBoundaryRecord> _boundaries = new List<LogicalCutBoundaryRecord>(2);
        private readonly List<VpMultiCutRegistration> _registrations = new List<VpMultiCutRegistration>(2);

        // The adopted snapshot and the one a collection builds beside it. They change places on adoption, so the one
        // just replaced is what the next collection builds in, and sections are reused from the adopted one.
        private VpMultiCutSnapshot _snapshot;
        private VpMultiCutSnapshot _building;
        private readonly VpCapJobClassification _capJobs;

        // The draw ranges of every registration of the adopted snapshot, and of the one being built, in the snapshot's
        // registration order -- a registration drawn as nothing and one being let go included. They change places with
        // the snapshots on adoption, so a preparation reads the table its snapshot was built with. Both are made with the
        // display at the instance capacity -- every registration holds at least one instance, so there are never more
        // registrations than instances -- and never grown.
        private GeometryTable _geometries;
        private GeometryTable _candidateGeometries;

        // The adopted draw data: what the GPU holds and what is being drawn. Nothing here is touched until an upload
        // has succeeded, so a refused collection leaves exactly this on screen. Each has a candidate twin of the same
        // fixed size; adoption trades them.
        private List<LogicalCutDisplaySide> _sides = new List<LogicalCutDisplaySide>(4);
        private VpIndirectCommand[] _commands;
        private Material[] _commandMaterials;
        private bool[] _commandProvisional;
        private Matrix4x4[] _transforms;
        private VpInstanceClip[] _clips;
        private LogicalCutCapRecord[] _capRecords;
        private int[] _rfCommandStart;
        private int[] _rfCommandCount;
        private Matrix4x4[] _rfTransform;
        private LogicalFragmentId[] _roots = Array.Empty<LogicalFragmentId>();
        private int _commandCount;
        private int _capRecordCount;

        private List<LogicalCutDisplaySide> _candidateSides = new List<LogicalCutDisplaySide>(4);
        private VpIndirectCommand[] _candidateCommands;
        private Material[] _candidateCommandMaterials;
        private bool[] _candidateCommandProvisional;
        private Matrix4x4[] _candidateTransforms;
        private VpInstanceClip[] _candidateClips;
        private LogicalCutCapRecord[] _candidateCapRecords;
        private int[] _candidateRfCommandStart;
        private int[] _candidateRfCommandCount;
        private Matrix4x4[] _candidateRfTransform;
        private LogicalFragmentId[] _candidateRoots = Array.Empty<LogicalFragmentId>();

        // Counts adoptions. A camera's preparation names the snapshot it was made for, and a newer one voids it.
        private long _generation;

        // A stencil arrangement being made: for the largest one a candidate could need, checked before adoption, and
        // for one camera's colours when it is prepared. Made once, at the stencil capacity, and never grown.
        private readonly VpIndirectCommand[] _candidateStencilCommands;
        private readonly Matrix4x4[] _candidateStencilTransforms;
        private readonly VpInstanceClip[] _candidateStencilClips;
        private readonly int[] _candidateCapIndices;
        private readonly VpStencilCapColor[] _candidateStencilColors;

        private readonly int _capRecordCapacity;
        private int _preparationRecordLimit;
        private bool _preparing;

        // What the last preparation's arrangement filled of the scratch above; for tests.
        private int _arrangedVolumes;
        private int _arrangedCapIndices;

        /// <summary>
        /// Every registration's draw ranges, in the order of the snapshot they were built with. Its room is fixed when it
        /// is made and never grown; a collection fills it, and a preparation only reads it.
        /// </summary>
        private sealed class GeometryTable : IReadOnlyList<VpCapJobGeometry>
        {
            private readonly VpCapJobGeometry[] _items;

            public GeometryTable(int capacity)
            {
                _items = new VpCapJobGeometry[capacity];
            }

            public int Capacity => _items.Length;

            public int Count { get; private set; }

            public VpCapJobGeometry this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)Count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _items[index];
                }
            }

            /// <summary>
            /// Lets go of every range the table refers to and empties it, after confirming that
            /// <paramref name="count"/> entries fit its fixed room. Nothing is made.
            /// </summary>
            /// <exception cref="InvalidOperationException">More entries than the room; never grown.</exception>
            public void Restart(int count)
            {
                Array.Clear(_items, 0, Count);
                Count = 0;
                if (count > _items.Length)
                {
                    throw new InvalidOperationException(
                        "more registrations (" + count + ") than the draw-range table's fixed room (" + _items.Length + ")");
                }
            }

            public void Add(VpGeometryRange[] ranges)
            {
                if (Count >= _items.Length)
                {
                    throw new InvalidOperationException("the draw-range table's fixed room is full");
                }

                _items[Count++] = new VpCapJobGeometry(VpArrayRange<VpGeometryRange>.Whole(ranges));
            }

            /// <summary>Slots past the count that still refer to a range: always zero. For tests.</summary>
            public int HeldPastCount
            {
                get
                {
                    int held = 0;
                    for (int i = Count; i < _items.Length; i++)
                    {
                        held += _items[i].ranges.IsNull ? 0 : 1;
                    }

                    return held;
                }
            }

            public IEnumerator<VpCapJobGeometry> GetEnumerator()
            {
                for (int i = 0; i < Count; i++)
                {
                    yield return _items[i];
                }
            }

            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
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

        private static readonly int CapNormalsId = Shader.PropertyToID("_VpCapNormals");
        private static readonly int CapShadedId = Shader.PropertyToID("_VpCapShaded");

        /// <summary>
        /// The colour every cap this display draws is given: DESIGN 5.3's own choice for a temporary cut face
        /// (<see cref="VpCutSurfaceColour.CurrentProvisional"/>) -- the shared ordinary colour, or red while the one
        /// debug switch is on. It is read once per camera preparation and carried in that preparation's colour records,
        /// so a change takes effect at the next preparation of each camera; nothing here reaches into a camera prepared
        /// already. Reading it changes no geometry, transfer, section or material.
        /// </summary>
        private static Color ProvisionalCapColour => VpCutSurfaceColour.CurrentProvisional;

        // The frame whose collection succeeded, and the frame that may draw. They are not the same: a frame whose
        // collection was refused for room may still draw the snapshot adopted earlier.
        private int _settledFrame = int.MinValue;
        private int _openFrame = int.MinValue;
        private bool _hasSnapshot;

        /// <summary>
        /// What the adopted snapshot's structure was settled from: the ledger's revision then, and this display's own.
        /// Both must still hold for that structure to be taken over instead of settled again. -1 means nothing has
        /// been settled yet.
        /// </summary>
        private long _structureLedgerRevision = -1;
        private long _structureInputRevision = -1;

        /// <summary>
        /// Counts the changes to what this display itself puts into a snapshot: a registration shown or let go, a
        /// geometry commit changing what a body is and reflects, a placement lookup being changed. The ledger counts
        /// its own; this counts the rest, so that between them nothing a structure was settled from can change
        /// unnoticed.
        /// </summary>
        private long _inputRevision;
        private bool _drawRegisteredThisFrame;
        private bool _broken;
        private bool _halted;
        private LogicalCutDisplayHaltReason _haltReason = LogicalCutDisplayHaltReason.None;
        private VpMultiCutInvalidInput _haltInvalidInput = VpMultiCutInvalidInput.None;
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
            GraphicsBuffer capNormals,
            VpStencilSettings settings,
            int commandCapacity,
            int instanceCapacity,
            in DerivedCapacities derived,
            VpMultiCutSnapshot snapshot,
            VpMultiCutSnapshot building,
            VpCapJobClassification capJobs,
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
            _snapshot = snapshot;
            _building = building;
            _capJobs = capJobs;
            _geometries = new GeometryTable(instanceCapacity);
            _candidateGeometries = new GeometryTable(instanceCapacity);
            _cameraStencils = new CameraStencil[settings.cameraCapacity];
            _candidateStencilColors = new VpStencilCapColor[settings.maxStencilColors];

            // Every volume group is one stencil command per command of its body; a render fragment has at most eight
            // groups, one per selected boundary, so there are at most eight volume commands per instance. Every cap is
            // fanned. Each camera's batch is made to these sizes, derived and checked before anything was made
            // (TryDeriveCapacities).
            _stencilCommandCapacity = derived.stencilCommands;
            _stencilCapVertexCapacity = derived.capVertices;

            // DESIGN 5.3: one outward normal per cap vertex, so that the cap pass shades a temporary cut face with the
            // same function the body and the real caps use. The buffer was made with the other GPU resources and is
            // this display's from here on; its room is the cap vertices' own, never grown, written by count when a
            // snapshot is taken up, and given back with this display.
            _capNormals = new Vector4[derived.capVertices];
            _capNormalBuffer = capNormals;
            for (int c = 0; c < stencilMaterials.ColorCount; c++)
            {
                Material cap = stencilMaterials.Cap(c);
                cap.SetBuffer(CapNormalsId, _capNormalBuffer);
                cap.SetFloat(CapShadedId, 1f);
            }

            _stencilCapIndexCapacity = derived.capIndices;
            _candidateStencilCommands = new VpIndirectCommand[_stencilCommandCapacity];
            _candidateStencilTransforms = new Matrix4x4[_stencilCommandCapacity];
            _candidateStencilClips = new VpInstanceClip[_stencilCommandCapacity];
            _candidateCapIndices = new int[_stencilCapIndexCapacity];

            _commands = new VpIndirectCommand[commandCapacity];
            _commandMaterials = new Material[commandCapacity];
            _commandProvisional = new bool[commandCapacity];
            _candidateCommands = new VpIndirectCommand[commandCapacity];
            _candidateCommandMaterials = new Material[commandCapacity];
            _candidateCommandProvisional = new bool[commandCapacity];
            _transforms = new Matrix4x4[instanceCapacity];
            _clips = new VpInstanceClip[instanceCapacity];
            _candidateTransforms = new Matrix4x4[instanceCapacity];
            _candidateClips = new VpInstanceClip[instanceCapacity];
            _rfCommandStart = new int[derived.renderFragments];
            _rfCommandCount = new int[derived.renderFragments];
            _rfTransform = new Matrix4x4[derived.renderFragments];
            _candidateRfCommandStart = new int[derived.renderFragments];
            _candidateRfCommandCount = new int[derived.renderFragments];
            _candidateRfTransform = new Matrix4x4[derived.renderFragments];
            _capRecordCapacity = derived.caps;
            _capRecords = new LogicalCutCapRecord[derived.caps];
            _candidateCapRecords = new LogicalCutCapRecord[derived.caps];
            _preparationRecordLimit = derived.caps;
            _commandCapacity = commandCapacity;
            _instanceCapacity = instanceCapacity;
            _frameSource = frameSource;
        }

        /// <summary>The sizes a display is made to, every one of them an int.</summary>
        internal readonly struct DerivedCapacities
        {
            public DerivedCapacities(
                int stencilCommands, int capVertices, int capIndices, int renderFragments, int caps, int branches,
                int candidates, int chainDepth)
            {
                this.stencilCommands = stencilCommands;
                this.capVertices = capVertices;
                this.capIndices = capIndices;
                this.renderFragments = renderFragments;
                this.caps = caps;
                this.branches = branches;
                this.candidates = candidates;
                this.chainDepth = chainDepth;
            }

            public readonly int stencilCommands;
            public readonly int capVertices;
            public readonly int capIndices;
            public readonly int renderFragments;
            public readonly int caps;
            public readonly int branches;
            public readonly int candidates;
            public readonly int chainDepth;

            /// <summary>The snapshot room these sizes stand for.</summary>
            public VpMultiCutCapacities Snapshot => new VpMultiCutCapacities(branches, candidates, renderFragments, caps, chainDepth);
        }

        /// <summary>
        /// Every size a display is made to, worked out in 64-bit arithmetic from its explicit capacities: a render
        /// fragment takes at least one instance, since every body has a command, so there are at most as many render
        /// fragments as instances; eight caps per render fragment; fourteen vertices and twelve fanned triangles per
        /// cap. Stencil volume commands are instances times eight: a volume group is issued with one command per command
        /// of its body, and a render fragment has at most one group per selected boundary -- at most eight -- so one
        /// render fragment's volume commands are at most eight times its body's commands, each of which is one of its
        /// instances. The logical branches, candidates and chain depth are not derived from
        /// anything: they are the caller's. False when any input is out of range or any size is not an int; nothing
        /// is decided here beyond that, and no limit of its own is set.
        /// </summary>
        internal static bool TryDeriveCapacities(
            int commandCapacity,
            int instanceCapacity,
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            out DerivedCapacities derived)
        {
            derived = default;
            if (commandCapacity <= 0 || instanceCapacity <= 0 || branchCapacity <= 0 || candidateCapacity < 0
                || chainDepth <= 0)
            {
                return false;
            }

            long renderFragments = instanceCapacity;
            long caps = renderFragments * VpClipCandidates.Capacity;
            long stencilCommands = (long)instanceCapacity * VpClipCandidates.Capacity;
            long capVertices = caps * VpCapPolygonClip.MaxVertices;
            long capIndices = caps * (VpCapPolygonClip.MaxVertices - 2) * 3;
            long stack = ((long)branchCapacity * 2) + 2;
            if (!FitsInt(caps) || !FitsInt(stencilCommands) || !FitsInt(capVertices) || !FitsInt(capIndices)
                || !FitsInt(stack))
            {
                return false;
            }

            derived = new DerivedCapacities(
                (int)stencilCommands, (int)capVertices, (int)capIndices, instanceCapacity, (int)caps, branchCapacity,
                candidateCapacity, chainDepth);
            return true;
        }

        private static bool FitsInt(long value)
        {
            return value > 0 && value <= int.MaxValue;
        }

        /// <summary>
        /// Whether the draws this display registers are for Single Pass Instanced stereo: one issue carrying two
        /// instances, one per eye. It is read where a collection uploads, and both batches are told the same thing in
        /// that one place. Default false. Whether XR is up is the caller's to find out; setting it takes effect from the
        /// next settled collection.
        /// </summary>
        public bool SinglePassInstanced { get; set; }

        /// <summary>How many bodies this display holds.</summary>
        public int ShownCount => _shown.Count;

        /// <summary>
        /// Where fragments stand, when they stand somewhere of their own (DESIGN 5.1, 7.1.2). Null — the default —
        /// means every registration is drawn at its own placement, which is what a display of shapes that move
        /// together does. It is read while a collection builds its snapshot and never while one draws.
        /// </summary>
        public IVpFragmentPlacement Placement
        {
            get => _placement;
            set
            {
                if (!ReferenceEquals(_placement, value))
                {
                    // Which lookup answers is part of what a structure was settled from, because a different one can
                    // answer Missing where the last one followed something.
                    _placement = value;
                    InputChanged();
                }
            }
        }

        private IVpFragmentPlacement _placement;

        /// <summary>
        /// How many instances the last settled collection draws: per body, its command count times its render
        /// fragments.
        /// </summary>
        public int SideCount => _sides.Count;

        public bool IsDisposed => _disposed;

        /// <summary>
        /// Whether a GPU update was interrupted part-way. Such a display draws and collects no more, because what
        /// reached the GPU cannot be established; only <see cref="Dispose"/> is left to call.
        /// </summary>
        public bool IsBroken => _broken;

        /// <summary>
        /// Whether this display stopped drawing because a snapshot could not be built for a reason other than room
        /// (see the class notes). It stays stopped until <see cref="Dispose"/>; nothing recovers it.
        /// </summary>
        public bool IsHalted => _halted;

        /// <summary>Why this display stopped: the first reason, kept. <see cref="LogicalCutDisplayHaltReason.None"/> while it has not.</summary>
        public LogicalCutDisplayHaltReason HaltReason => _haltReason;

        /// <summary>
        /// When <see cref="HaltReason"/> is <see cref="LogicalCutDisplayHaltReason.InvalidInput"/>, which kind: in
        /// particular whether the conservative numeric check refused the input or a value really came out not finite.
        /// <see cref="VpMultiCutInvalidInput.None"/> otherwise.
        /// </summary>
        public VpMultiCutInvalidInput HaltInvalidInput => _haltInvalidInput;

        /// <summary>
        /// Asked just before the body's upload; when it answers true the upload is taken as refused by the batch, and
        /// when it throws, as a GPU call that threw. For tests only, to reach what no input can; null otherwise.
        /// </summary>
        internal Func<bool> RefuseBodyUploadForTest { get; set; }

        /// <summary>Whether a draw has already been registered in the frame this display last settled.</summary>
        public bool HasDrawnThisFrame => _drawRegisteredThisFrame;

        /// <summary>
        /// Whether this frame has been opened for drawing: a collection has settled and <see cref="TryBeginFrame"/>
        /// has opened it. A caller that draws asks this first -- a frame that is not open has nothing to draw, which
        /// is an ordinary state and not an error, while <see cref="Render"/> refuses it as a mistake of order.
        /// </summary>
        public bool IsFrameOpen => _hasSnapshot && _openFrame == CurrentFrame;

        /// <summary>How many collections have settled a frame. A collection refused settles nothing.</summary>
        public int SettledCollections { get; private set; }

        /// <summary>How many command and instance uploads this display has issued, its first upload included.</summary>
        public int CommandUploads { get; private set; }

        /// <summary>Shadow calls issued with the one-sided caster: one per run of such commands per draw.</summary>
        public int OneSidedShadowIssues { get; private set; }

        /// <summary>
        /// Shadow calls issued with the two-sided caster: the bodies with a render fragment clipped. One per run of such
        /// commands per draw, and none at all while nothing is clipped.
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
        /// How many times a cap's box-and-plane section was actually taken, over every collection attempt. It stays
        /// where it is while the box, the face, the plane and the placement are the same -- publication included:
        /// only a changed input makes another one.
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
        /// </summary>
        public bool DrawsSinglePassInstanced => _batch.SinglePassInstanced;

        private int CurrentFrame => _frameSource != null ? _frameSource() : Time.frameCount;

        /// <summary>
        /// Makes a display over one storage and one ledger. The GPU buffers are the storage's own shape, made once and
        /// kept: no growth, no replacement. Nothing is shown until <see cref="TryShow(LogicalFragmentId, VpStoredGeometry, Matrix4x4)"/>
        /// is called.
        /// </summary>
        /// <param name="commandCapacity">Draw commands: one per submesh of every body drawn.</param>
        /// <param name="instanceCapacity">
        /// Draw instances: per body, its commands times its render fragments. Render fragments, stencil volume commands
        /// (eight per instance), caps (eight per render fragment), cap vertices and cap indices are derived from it.
        /// </param>
        /// <param name="branchCapacity">Logical branches over every registration together.</param>
        /// <param name="candidateCapacity">Clip candidates kept over every branch together; not cut at eight.</param>
        /// <param name="chainDepth">The longest chain of boundaries one fragment may have.</param>
        public static bool TryCreate(
            VpCpuGeometryStorage storage,
            VpGeometryReferenceTable table,
            LogicalCutLedger ledger,
            IReadOnlyDictionary<int, Material> materialsBySourceIndex,
            Material shadowMaterial,
            Material provisionalShadowMaterial,
            int commandCapacity,
            int instanceCapacity,
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            VpStencilSettings stencilSettings,
            out VpLogicalCutDisplay display)
        {
            return TryCreate(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth, stencilSettings, null,
                out display);
        }

        /// <summary>
        /// The same, with the frame counter given by the caller instead of taken from the engine. It exists for tests,
        /// which have no frame loop to advance.
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
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            VpStencilSettings stencilSettings,
            Func<int> frameSource,
            out VpLogicalCutDisplay display)
        {
            display = null;
            if (storage == null || table == null || ledger == null || materialsBySourceIndex == null)
            {
                return false;
            }

            // Every size is worked out wide and must be an int, before any GPU buffer, material or scratch is made.
            if (!TryDeriveCapacities(
                    commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth,
                    out DerivedCapacities derived))
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
            if ((shadowMaterial == null) != (provisionalShadowMaterial == null))
            {
                return false;
            }

            VpGpuIndexedGeometryBuffers buffers = null;
            VpIndexedIndirectDrawBatch batch = null;
            VpStencilCapMaterials stencilMaterials = null;
            GraphicsBuffer capNormals = null;
            bool taken = false;
            try
            {
                VpMultiCutCapacities snapshotCapacities = derived.Snapshot;
                var snapshot = new VpMultiCutSnapshot(snapshotCapacities);
                var building = new VpMultiCutSnapshot(snapshotCapacities);
                var capJobs = new VpCapJobClassification(snapshotCapacities);

                buffers = new VpGpuIndexedGeometryBuffers(storage.VertexCapacity, storage.IndexCapacity);
                batch = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity);

                // One material set for every camera; the stencil batches themselves come with the cameras.
                if (!VpStencilCapMaterials.TryCreate(stencilSettings.maxStencilColors, out stencilMaterials))
                {
                    return false;
                }

                // DESIGN 5.3: one outward normal per cap vertex for the cap pass to shade with. Made here, beside the
                // other GPU resources, so that anything failing before the display takes them gives this back too.
                capNormals = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, Math.Max(1, derived.capVertices), sizeof(float) * 4);

                display = new VpLogicalCutDisplay(
                    storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial, buffers,
                    batch, stencilMaterials, capNormals, stencilSettings, commandCapacity, instanceCapacity, derived,
                    snapshot, building, capJobs, frameSource);
                taken = true;
                return true;
            }
            finally
            {
                if (!taken)
                {
                    capNormals?.Dispose();
                    stencilMaterials?.Dispose();
                    batch?.Dispose();
                    buffers?.Dispose();
                }
            }
        }

        // ----- cameras ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Registers <paramref name="camera"/> for stencil work: a stencil batch of its own is made for it, sized like
        /// every other camera's. False, making nothing, when the camera is null or already registered, every slot of
        /// <see cref="VpStencilSettings.cameraCapacity"/> is taken, or the display has stopped.
        /// </summary>
        public bool TryRegisterCamera(Camera camera)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();
            if (_halted || ReferenceEquals(camera, null) || FindCamera(camera) != null)
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
        /// False when the camera is not registered or has already drawn in this frame -- changing nothing -- or when this
        /// attempt is refused because the snapshot holds more caps than a preparation has room for
        /// (<see cref="VpStencilPreparationOutcome.CapacityExceeded"/>). The colour limit is never a refusal: what the
        /// ordinary colours cannot take is drawn in the last colour (D-185, D-186). A refused attempt has already voided
        /// the camera's earlier preparation: the camera is not prepared, nothing is uploaded
        /// or written, <see cref="Render"/> refuses it, and <see cref="TryGetCameraStencil"/> tells which refusal it was.
        /// Neither refusal stops the display or touches another camera; the camera may be prepared again before it
        /// draws. An upload refused within the capacity checked at adoption, or a GPU call that throws, stops the display
        /// as broken. A display that has stopped throws.
        /// </para>
        /// <para>
        /// Works in this display's own room and allocates nothing (see the class notes). Calling into this display
        /// while a preparation is running -- from inside it -- throws before anything is changed.
        /// </para>
        /// </summary>
        public bool TryPrepareCamera(Camera camera, in VpCapEye left, in VpCapEye right)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfHalted();
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
                // Not prepared until this one has been uploaded: whatever this attempt comes to, the camera's earlier
                // preparation is gone from here on, and a refusal leaves it unprepared, so Render refuses it.
                slot.preparedFrame = int.MinValue;
                slot.preparedGeneration = -1;
                slot.preparation = default;
                if (!TryArrange(
                        left, right, out int commands, out int capIndices, out int colours,
                        out VpStencilPreparation preparation))
                {
                    // Refused for room before anything was uploaded or written. The batch
                    // still holds its last upload, which nothing draws: Render asks the preparation, not the batch.
                    slot.preparation = preparation;
                    return false;
                }

                // Uploaded by count: the scratch is the stencil capacity long, and only what this arrangement filled
                // is checked, sent and drawn. The cap vertices are the adopted snapshot's own.
                try
                {
                    if (!slot.batch.TryUpload(
                            _candidateStencilCommands, commands, _candidateStencilTransforms, _candidateStencilClips,
                            _snapshot.CapVertexArray, _snapshot.CapVertexCount, _candidateCapIndices, capIndices,
                            _candidateStencilColors, colours, _batch.SinglePassInstanced))
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
                // The classification is shared by every camera and is nobody's result: what a camera keeps is its own
                // batch and its own counts. Its looks at the snapshot and its ledger references go here.
                _capJobs.Release();
                _preparing = false;
            }
        }

        /// <summary>
        /// How many caps one preparation may take, at most the room it was made with. Lowered only by tests, to reach
        /// the refusal of a snapshot that holds more caps than a preparation has room for, which the collection's own
        /// capacity otherwise keeps from happening.
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
        /// How many looks at the adopted snapshot the classification's room still holds. Zero whenever no preparation
        /// is running. For tests.
        /// </summary>
        internal int HeldPreparationLooks => _capJobs.HeldViews;

        /// <summary>
        /// The shared cap-job classification. It is readable only while a preparation runs -- every preparation lets it
        /// go in the end -- so tests read it from <see cref="CapJobsClassifiedForTest"/>, or check it holds nothing.
        /// </summary>
        internal VpCapJobClassification CapJobs => _capJobs;

        /// <summary>
        /// Called inside a preparation, once its classification has succeeded for the adopted snapshot and before
        /// anything is arranged or uploaded, with that classification. For tests only, which read it and change nothing;
        /// null otherwise.
        /// </summary>
        internal Action<VpCapJobClassification> CapJobsClassifiedForTest { get; set; }

        /// <summary>The draw-range table the adopted snapshot was built with, for tests.</summary>
        internal IReadOnlyList<VpCapJobGeometry> AdoptedGeometries => _geometries;

        /// <summary>
        /// Both draw-range tables' room, as the objects it is held in, their fixed capacity, and the slots past each count
        /// still referring to a range. For tests, which check that the room is never replaced and nothing is left held.
        /// </summary>
        internal void GeometryTableRoomForTest(out object adopted, out object candidate, out int capacity, out int heldPastCount)
        {
            adopted = _geometries;
            candidate = _candidateGeometries;
            capacity = _geometries.Capacity;
            heldPastCount = _geometries.HeldPastCount + _candidateGeometries.HeldPastCount;
        }

        /// <summary>The adopted snapshot itself, for tests that classify it again on their own.</summary>
        internal VpMultiCutSnapshot AdoptedSnapshot => _snapshot;

        /// <summary>
        /// How often the structure has been settled from the ledger since this display was made: the lineage walked,
        /// the candidates collected, the Selected and Ignored decided and grouped. A frame in which nothing of the
        /// input changed leaves this where it was, however much anything moved.
        /// </summary>
        public long StructureBuilds => _snapshot.StructureBuilds + _building.StructureBuilds;

        /// <summary>How often the structural checks over the ledger have run. Rises with <see cref="StructureBuilds"/>.</summary>
        public long StructureValidations => _snapshot.StructureValidations + _building.StructureValidations;

        /// <summary>How often placements and what they decide have been settled, by either route.</summary>
        public long PlacementPasses => _snapshot.PlacementPasses + _building.PlacementPasses;

        /// <summary>What this display's own inputs are at, for a test that wants to see a change noticed.</summary>
        internal long InputRevision => _inputRevision;

        /// <summary>Records that something this display puts into a snapshot has changed.</summary>
        private void InputChanged()
        {
            _inputRevision++;
        }

        /// <summary>
        /// How many volume commands and cap indices the last arrangement made -- of whichever camera, successful or not.
        /// For tests, which read it right after the preparation it came from.
        /// </summary>
        internal int ArrangedVolumeCount => _arrangedVolumes;

        internal int ArrangedCapIndexCount => _arrangedCapIndices;

        /// <summary>One volume command of the last arrangement, as it was (or would have been) uploaded. For tests.</summary>
        internal bool TryGetArrangedVolume(int index, out VpIndirectCommand command, out Matrix4x4 transform, out VpInstanceClip clip)
        {
            bool ok = index >= 0 && index < _arrangedVolumes;
            command = ok ? _candidateStencilCommands[index] : default;
            transform = ok ? _candidateStencilTransforms[index] : default;
            clip = ok ? _candidateStencilClips[index] : default;
            return ok;
        }

        /// <summary>One cap index of the last arrangement: a place in the adopted snapshot's cap vertices. For tests.</summary>
        internal bool TryGetArrangedCapIndex(int index, out int vertex)
        {
            bool ok = index >= 0 && index < _arrangedCapIndices;
            vertex = ok ? _candidateCapIndices[index] : -1;
            return ok;
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
                slot.batch.CapIssues, slot.batch.VolumeGpuDraws, slot.batch.ColorCount, slot.batch.SinglePassInstanced,
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
        /// DESIGN 5.6 / D-183, D-185, D-186 over the adopted snapshot, for two eyes, through the one cap-job classification
        /// of every registration's render fragments together, with the draw-range table adopted with that snapshot; then
        /// the arrangement, colour by colour, into the stencil scratch. An ordinary colour: each of its volume groups
        /// once -- its representative render fragment's commands, one per command of its body, with that render
        /// fragment's transform and the group's own-face clip -- and then each of its cap jobs, its drawing polygon
        /// fanned. The last colour, when anything went to it: each render fragment of its jobs once -- its commands, its
        /// transform and its body's own clip record of every selected face -- and then each of its cap jobs. A group's
        /// representative render fragment never stands in for the last colour's volumes.
        /// <para>
        /// **Room.** No arrangement is larger than the largest one asked of the batches at adoption, which takes every
        /// non-empty cap as a job with an own-face volume of its render fragment's commands. A render fragment whose
        /// jobs are all in ordinary colours issues at most that; one with any job in the last colour issues one volume
        /// less for each such job and one volume -- the same commands -- in the last colour, which is never more.
        /// The commands per volume are the render fragment's body commands either way, and the clip record is one fixed-size
        /// record whatever its plane count, so the adoption's check of counts and form covers the last colour's volumes too.
        /// </para>
        /// False, with the refusal said in <paramref name="preparation"/>, for room only; nothing is uploaded here.
        /// </summary>
        private bool TryArrange(
            in VpCapEye left, in VpCapEye right, out int commandCount, out int capIndexCount, out int colourCount,
            out VpStencilPreparation preparation)
        {
            commandCount = 0;
            capIndexCount = 0;
            colourCount = 0;
            _arrangedVolumes = 0;
            _arrangedCapIndices = 0;
            int capRecords = _snapshot.CapCount;

            // Room is decided before anything is read, let alone uploaded.
            if (capRecords > _preparationRecordLimit)
            {
                preparation = Refusal(VpStencilPreparationOutcome.CapacityExceeded, capRecords);
                return false;
            }

            VpCapJobOutcome outcome = _capJobs.TryClassify(
                _snapshot, _geometries, left, right, _settings.facingEpsilon, _settings.ndcMargin,
                _settings.maxStencilColors);
            if (outcome == VpCapJobOutcome.CapacityExceeded)
            {
                preparation = Refusal(VpStencilPreparationOutcome.CapacityExceeded, capRecords);
                return false;
            }

            if (outcome != VpCapJobOutcome.Classified)
            {
                // The colour limit no longer ends a classification (D-185); nothing else is expected here.
                throw new InvalidOperationException("the cap-job classification ended unexpectedly: " + outcome);
            }

            // The result names the snapshot it was made from; nothing of another build is arranged.
            if (!_capJobs.IsFor(_snapshot))
            {
                throw new InvalidOperationException("the cap-job classification is not of the adopted snapshot");
            }

            CapJobsClassifiedForTest?.Invoke(_capJobs);

            // DESIGN 5.3's colour for a temporary cut face, read once here: every colour of this preparation, the last
            // one included, is given the same one, and the choice never changes how the jobs, groups or colours came out.
            Color capColour = ProvisionalCapColour;
            int capsDrawn = 0;
            int colours = _capJobs.ColourCount;
            for (int colour = 0; colour < colours; colour++)
            {
                _capJobs.TryGetColour(colour, out VpCapJobColour range);
                int volumeStart = commandCount;
                int capStart = capIndexCount;

                if (range.last)
                {
                    // The last colour, the old way: every render fragment of its jobs once, clipped by every selected
                    // face -- the body's own record -- before any of its caps.
                    for (int p = 0; p < _capJobs.LastColourRenderFragmentCount; p++)
                    {
                        _capJobs.TryGetLastColourRenderFragment(p, out int rf);
                        _snapshot.TryGetRenderFragment(rf, out VpMultiCutRenderFragment fragment);
                        AppendVolume(rf, fragment.clip, _commands, _rfCommandStart, _rfCommandCount, _rfTransform, ref commandCount);
                    }
                }
                else
                {
                    // Every volume group of the colour, each once, before any cap of the colour.
                    for (int p = range.groupStart; p < range.groupStart + range.groupCount; p++)
                    {
                        _capJobs.TryGetGroupOfColour(p, out int g);
                        _capJobs.TryGetVolumeGroup(g, out VpCapVolumeGroup group);
                        AppendVolume(
                            group.renderFragment, group.volumeClip, _commands, _rfCommandStart, _rfCommandCount,
                            _rfTransform, ref commandCount);
                    }
                }

                // Then every cap job of those groups, each once: its own cap's clipped drawing polygon, never the initial
                // section. A job is in exactly one colour, so no cap is drawn in two.
                for (int p = range.groupStart; p < range.groupStart + range.groupCount; p++)
                {
                    _capJobs.TryGetGroupOfColour(p, out int g);
                    _capJobs.TryGetVolumeGroup(g, out VpCapVolumeGroup group);
                    for (int k = group.jobStart; k < group.jobStart + group.jobCount; k++)
                    {
                        _capJobs.TryGetJobOfGroup(k, out int j);
                        _capJobs.TryGetJob(j, out VpCapJob job);
                        AppendFan(_capRecords[job.capIndex], ref capIndexCount);
                        capsDrawn++;
                    }
                }

                _candidateStencilColors[colourCount++] = new VpStencilCapColor(
                    volumeStart, commandCount - volumeStart, capStart, capIndexCount - capStart, capColour);
            }

            _arrangedVolumes = commandCount;
            _arrangedCapIndices = capIndexCount;
            preparation = new VpStencilPreparation(
                VpStencilPreparationOutcome.Prepared, capRecords, _capJobs.EmptyCapCount, _capJobs.HiddenCapCount,
                _capJobs.JobCount, _capJobs.VolumeGroupCount, colourCount, commandCount, capsDrawn,
                _capJobs.OrdinaryVolumeGroupCount, _capJobs.LastColourRenderFragmentCount, _capJobs.LastColourJobCount);
            return true;
        }

        /// <summary>
        /// The outward normal of every cap vertex the snapshot about to be taken up holds, in its order, put on the GPU
        /// for the cap materials to shade with (DESIGN 5.3). It is written **before** that snapshot is adopted and with
        /// the body's upload, so that a GPU call that throws stops the display as broken with the previous snapshot
        /// still the adopted one, rather than leaving new bodies beside normals that may or may not have arrived. The
        /// room was fixed when this display was made and the count was found to fit a moment ago; it is checked here
        /// all the same, before anything is written, and a snapshot with no cap vertex writes nothing at all. Nothing
        /// is allocated.
        /// </summary>
        private void UploadCapNormals(VpMultiCutSnapshot snapshot)
        {
            int vertices = snapshot.CapVertexCount;
            if (vertices < 0 || vertices > _capNormals.Length)
            {
                throw new InvalidOperationException("the snapshot holds more cap vertices than this display's room");
            }

            for (int c = 0; c < snapshot.CapCount; c++)
            {
                snapshot.TryGetCap(c, out VpMultiCutCap cap);
                Vector3 outward = cap.outwardNormal;
                var normal = new Vector4(outward.x, outward.y, outward.z, 0f);
                for (int v = 0; v < cap.vertexCount; v++)
                {
                    _capNormals[cap.vertexStart + v] = normal;
                }
            }

            if (vertices > 0)
            {
                _capNormalBuffer.SetData(_capNormals, 0, 0, vertices);
            }

            _capNormalCount = vertices;
        }

        /// <summary>Makes the cap normals' GPU write throw once, to reach the broken path. For tests only; null otherwise.</summary>
        internal Func<bool> RefuseCapNormalUploadForTest { get; set; }

        /// <summary>How many cap-vertex normals the last upload put on the GPU. For tests.</summary>
        internal int CapNormalCount => _capNormalCount;

        /// <summary>A refused preparation: the cap records are settled by the snapshot, and nothing else was made.</summary>
        private static VpStencilPreparation Refusal(VpStencilPreparationOutcome outcome, int capRecords)
        {
            return new VpStencilPreparation(outcome, capRecords, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        }

        /// <summary>
        /// One volume: every command of the render fragment's body, once, with one instance, the body's transform and
        /// the clip given -- the same geometry, range and placement the body is drawn with. The clip is the volume's
        /// own (one face), applied by the shader as it is for the body.
        /// </summary>
        private void AppendVolume(
            int renderFragment,
            in VpInstanceClip clip,
            VpIndirectCommand[] commands,
            int[] commandStart,
            int[] commandCountOf,
            Matrix4x4[] transforms,
            ref int commandCount)
        {
            int start = commandStart[renderFragment];
            int count = commandCountOf[renderFragment];
            for (int c = 0; c < count; c++)
            {
                VpIndirectCommand source = commands[start + c];
                _candidateStencilCommands[commandCount] = new VpIndirectCommand(source.range, source.localBounds, 1);
                _candidateStencilTransforms[commandCount] = transforms[renderFragment];
                _candidateStencilClips[commandCount] = clip;
                commandCount++;
            }
        }

        /// <summary>A cap's polygon as a fan; a cap of fewer than three vertices has no triangle and adds nothing.</summary>
        private void AppendFan(in LogicalCutCapRecord record, ref int capIndexCount)
        {
            for (int v = 1; v + 1 < record.vertexCount; v++)
            {
                _candidateCapIndices[capIndexCount++] = record.vertexStart;
                _candidateCapIndices[capIndexCount++] = record.vertexStart + v;
                _candidateCapIndices[capIndexCount++] = record.vertexStart + v + 1;
            }
        }

        // ----- bodies ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Shows <paramref name="geometry"/> as the live fragment <paramref name="fragment"/> under the caller contract of
        /// this spelling: the whole lineage is in the geometry's own frame (the identity mapping) and the geometry
        /// reflects no boundary. It is the other <c>TryShow</c> with those two stated, nothing more.
        /// </summary>
        public bool TryShow(LogicalFragmentId fragment, VpStoredGeometry geometry, Matrix4x4 objectToWorld)
        {
            return TryShow(fragment, geometry, objectToWorld, Matrix4x4.identity, Array.Empty<VpClipBoundary>());
        }

        /// <summary>
        /// Shows <paramref name="geometry"/> as the live fragment <paramref name="fragment"/> and everything its lineage
        /// becomes, at the placement given. The geometry is transferred to the GPU once, here, and the registration and
        /// one display instance are taken and held until this display lets the body go.
        /// <para>
        /// Refused, changing nothing, when the fragment is not live, when it is already shown here or is an ancestor or a
        /// descendant of a fragment shown here, when the box, the placement or the mapping is outside the snapshot's
        /// input contract, when the box, the placement and the vertex epsilon it would be built with fail the snapshot's
        /// conservative section bounds (<see cref="VpMultiCutInvalidInput.ConservativeSection"/>), when the geometry cannot be prepared or resolved to materials, when what it needs does not
        /// fit the fixed capacity, or when the display has stopped.
        /// </para>
        /// </summary>
        /// <param name="lineageToGeometryLocal">
        /// The mapping from the lineage's common logical frame to the geometry's coordinates: rigid. Required.
        /// </param>
        /// <param name="reflected">
        /// The boundaries the geometry already reflects. Required: null is not "none". It is copied here.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="reflected"/> is null.</exception>
        public bool TryShow(
            LogicalFragmentId fragment,
            VpStoredGeometry geometry,
            Matrix4x4 objectToWorld,
            Matrix4x4 lineageToGeometryLocal,
            IReadOnlyCollection<VpClipBoundary> reflected)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();
            if (reflected == null)
            {
                throw new ArgumentNullException(
                    nameof(reflected), "what the geometry reflects must be said, even if it is nothing");
            }

            if (_halted)
            {
                return false;
            }

            // A frame this display has already settled, or already drawn, is not changed from here.
            int frame = CurrentFrame;
            if ((_hasSnapshot && _settledFrame == frame) || (_openFrame == frame && _drawRegisteredThisFrame))
            {
                return false;
            }

            if (!fragment.IsSet
                || !_ledger.TryGetFragmentState(fragment, out LogicalFragmentState state)
                || state != LogicalFragmentState.Live
                || IsOnShownLineage(fragment))
            {
                return false;
            }

            if (!TryPrepare(
                    geometry, out VpIndirectCommand[] commands, out Material[] commandMaterials, out Bounds localBounds)
                || !VpMultiCutSnapshot.IsWithinInputContract(localBounds, objectToWorld, lineageToGeometryLocal)
                || !VpMultiCutSnapshot.IsWithinSectionBounds(
                    localBounds, objectToWorld, VpCapBoundsPolygon.EpsilonFor(localBounds)))
            {
                // What the registration settles by itself -- its box, its placement and the epsilon it will be built
                // with -- is refused here, before anything is transferred, registered or taken; the collection asks the
                // same function again.
                return false;
            }

            // Room for the body now, and for a second render fragment it may take later.
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

            var reflectedCopy = new VpClipBoundary[reflected.Count];
            int k = 0;
            foreach (VpClipBoundary boundary in reflected)
            {
                reflectedCopy[k++] = boundary;
            }

            var ranges = new VpGeometryRange[commands.Length];
            for (int c = 0; c < commands.Length; c++)
            {
                ranges[c] = commands[c].range;
            }

            var entry = new Shown
            {
                fragment = fragment,
                geometry = geometry,
                reference = reference,
                objectToWorld = objectToWorld,
                lineageToGeometryLocal = lineageToGeometryLocal,
                reflected = reflectedCopy,
                commands = commands,
                commandMaterials = commandMaterials,
                ranges = ranges,
                localBounds = localBounds,
            };
            entry.instances.Add(instance);
            _shown.Add(entry);
            InputChanged();
            return true;
        }

        /// <summary>
        /// Puts one committed cut's real geometry in place of the body it was cut from (DESIGN 4.5.6): the two sides,
        /// where each of them stands, the boundary each now reflects and the surface boundary the cut really made, as
        /// one consistent change. The cut's own temporary clip and cap go with it, because each side is registered as
        /// reflecting this cut's boundary on its own side; every later cut of the branch stays temporary and is
        /// rebuilt on the new bodies at the next collection.
        /// <para>
        /// **Where each side stands.** Where it stood before: the placement the side follows, or the body's when it
        /// follows nothing. A commit moves the root down to the two sides and takes in no movement of its own, so a
        /// side is drawn at the same place across it. That is the whole of the rule -- there is no quantity here to
        /// lose or to count twice.
        /// </para>
        /// <para>
        /// **What it does not wait for.** Nothing here adopts a snapshot, prepares a camera or draws: it changes what
        /// the *next* collection will be built from. A collection that has already settled this frame keeps drawing
        /// what it settled, and this commit is in the one after it — a prepared snapshot is never left with one side
        /// old and one side new. The body it replaces keeps its geometry and display instances until that next
        /// collection has been adopted, so nothing a prepared snapshot still reads is freed early.
        /// </para>
        /// <para>
        /// **The sides.** A produced side becomes a registration of its own. A side that is the input borrowed back
        /// keeps that very registration, re-keyed to the child that borrowed it, with nothing transferred and nothing
        /// taken. An empty side is registered as nothing at all: no renderer and no stand-in geometry, and its logical
        /// and physics child is untouched. A cut whose two sides are both empty has nothing shown for it and is
        /// accepted as a change to nothing.
        /// </para>
        /// <para>
        /// **What it transfers.** The vertices the cut appended go across once, and the two sides' indices are one
        /// contiguous run and go across in one <c>SetData</c> — never one transfer per side (DESIGN 4.5.6). A side
        /// that reuses the input transfers nothing at all. Every ordinary refusal is decided **before** the transfer,
        /// so a cut offered again never sends the same run twice.
        /// </para>
        /// <para>
        /// **The boundary.** Where the cut really made a surface — <paramref name="capTriangles"/> above zero, with
        /// both sides produced — one <see cref="LogicalCutBoundaryRecord"/> is published here with it. Where it made
        /// none there is no record: the "already reflected" set a side carries is a different thing and is set either
        /// way.
        /// </para>
        /// <para>
        /// Refused, changing nothing at all, when the body is not shown here, when a produced side cannot be prepared,
        /// resolved to materials or placed within the snapshot's input contract, when what it needs does not fit the
        /// drawing capacity or the reference table's room, when a live side follows a placement it was not given, or
        /// when the display has stopped. Nothing is taken and nothing is transferred on any of those, so the cut keeps its two sides and may
        /// be offered again.
        /// </para>
        /// </summary>
        public bool TryCommitCut(
            LogicalFragmentId source,
            CutOperationId operation,
            Vector4 plane,
            LogicalFragmentId positiveFragment,
            in VpStorageCutSide positive,
            LogicalFragmentId negativeFragment,
            in VpStorageCutSide negative,
            int capTriangles)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();
            if (_halted || !source.IsSet || !operation.IsSet)
            {
                return false;
            }

            // Whatever this comes to, it is about to change what a body is, what it reflects, or which bodies there
            // are. Counted here, at the one way in, so that no path out of it can leave a change unnoticed; counting
            // an attempt that changes nothing only costs one structure settled again.
            InputChanged();
            Shown body = null;
            for (int i = 0; i < _shown.Count; i++)
            {
                if (_shown[i].fragment == source)
                {
                    body = _shown[i];
                    break;
                }
            }

            if (body == null)
            {
                // Nothing of this body is shown. Two empty sides change nothing, so that is not a refusal; a side with
                // geometry cannot be placed without the body's placement, so it is.
                return positive.IsEmpty && negative.IsEmpty;
            }

            var face = new VpCapFace(_ledger, operation);
            if (positive.IsBorrowed || negative.IsBorrowed)
            {
                // The plane did not cut: one child is the input itself. The registration stays exactly as it is, with
                // the child's name and the boundary it now reflects -- there is no surface here, so no boundary record
                // is published.
                bool positiveBorrows = positive.IsBorrowed;
                float side = positiveBorrows ? 1f : -1f;
                LogicalFragmentId kept = positiveBorrows ? positiveFragment : negativeFragment;
                if (!kept.IsSet || !TrySidePlacements(kept, body, out SidePlacements keptPlacements))
                {
                    return false;
                }

                body.fragment = kept;
                body.objectToWorld = keptPlacements.registered;
                body.reflected = Reflecting(body.reflected, face, side);
                return true;
            }

            if (positive.IsEmpty && negative.IsEmpty)
            {
                // Neither side has geometry: the body stops being shown, and no child is registered for it.
                Retire(body);
                return true;
            }

            // Everything that can be judged without changing anything, first: the two sides, where they stand, the
            // drawing room after the swap and the reference table's room. Only then is anything transferred or taken.
            VpIndirectCommand[] positiveCommands = null;
            Material[] positiveMaterials = null;
            Bounds positiveBounds = default;
            SidePlacements positivePlacements = default;
            VpIndirectCommand[] negativeCommands = null;
            Material[] negativeMaterials = null;
            Bounds negativeBounds = default;
            SidePlacements negativePlacements = default;
            int commands = 0;
            int registrations = 0;
            if (positive.IsProduced)
            {
                if (!positiveFragment.IsSet
                    || !TrySidePlacements(positiveFragment, body, out positivePlacements)
                    || !TryPrepareSide(
                        positive.geometry, body, in positivePlacements, out positiveCommands, out positiveMaterials,
                        out positiveBounds))
                {
                    return false;
                }

                commands += positiveCommands.Length;
                registrations++;
            }

            if (negative.IsProduced)
            {
                if (!negativeFragment.IsSet
                    || !TrySidePlacements(negativeFragment, body, out negativePlacements)
                    || !TryPrepareSide(
                        negative.geometry, body, in negativePlacements, out negativeCommands, out negativeMaterials,
                        out negativeBounds))
                {
                    return false;
                }

                commands += negativeCommands.Length;
                registrations++;
            }

            if (commands == 0)
            {
                return false;
            }

            // Two different rooms, judged apart. The drawing data is judged on what is drawn after the swap: the body
            // goes and the sides that replace it come, each at one render fragment. Holding the body's own geometry and
            // display instances until the next adoption is not drawing data at all -- it is room in the reference
            // table, and the table is asked by its own rule whether both sides could really be taken.
            int bodyInstances = body.commands.Length * Math.Max(1, body.renderFragmentsShown);
            if (ShownCommandCount() - body.commands.Length + commands > _commandCapacity
                || CurrentInstanceCount() - bodyInstances + commands > _instanceCapacity
                || !_table.HasRoomForGeometriesWithDisplayInstances(registrations))
            {
                return false;
            }

            // One vertex transfer for what the cut appended -- both sides share it -- and one index transfer for the
            // two sides together, which is the one contiguous run they were written as.
            VpStoredGeometry appended = positive.IsProduced ? positive.geometry : negative.geometry;
            int vertices;
            int indices;
            try
            {
                if (!VpStoredGeometryTransfer.TryUploadCommittedVertices(
                        _storage, _buffers.VertexBuffer, appended.vertexStart, appended.vertexCount, out vertices))
                {
                    return false;
                }

                bool uploaded = positive.IsProduced && negative.IsProduced
                    ? VpStoredGeometryTransfer.TryUploadPublishedIndexRun(
                        _storage, _buffers.IndexBuffer, positive.geometry.indexRange, negative.geometry.indexRange, out indices)
                    : VpStoredGeometryTransfer.TryUploadPublishedIndices(
                        _storage,
                        _buffers.IndexBuffer,
                        positive.IsProduced ? positive.geometry.indexRange : negative.geometry.indexRange,
                        out indices);
                if (!uploaded)
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

            // The room was checked above, so taking these cannot fail for want of it. One that fails all the same is
            // this display's own invariant broken, not an ordinary refusal: nothing here may give a produced side's
            // index range back, because the cut still owns it until a commit is established.
            if (positive.IsProduced)
            {
                TakeBody(
                    positiveFragment, positive.geometry, body, in positivePlacements, positiveCommands, positiveMaterials,
                    positiveBounds, Reflecting(body.reflected, face, 1f));
            }

            if (negative.IsProduced)
            {
                TakeBody(
                    negativeFragment, negative.geometry, body, in negativePlacements, negativeCommands, negativeMaterials,
                    negativeBounds, Reflecting(body.reflected, face, -1f));
            }

            if (capTriangles > 0 && positive.IsProduced && negative.IsProduced)
            {
                _boundaries.Add(new LogicalCutBoundaryRecord(
                    operation,
                    positiveFragment,
                    negativeFragment,
                    positive.geometry,
                    negative.geometry,
                    positivePlacements.drawn,
                    negativePlacements.drawn,
                    plane,
                    body.lineageToGeometryLocal,
                    capTriangles,
                    _generation));
            }

            Retire(body);
            return true;
        }

        /// <summary>
        /// Where one shown registration stands: the placement it is drawn at.
        /// </summary>
        internal bool TryGetShownPlacement(LogicalFragmentId fragment, out Matrix4x4 objectToWorld)
        {
            for (int i = 0; i < _shown.Count; i++)
            {
                if (_shown[i].fragment == fragment)
                {
                    objectToWorld = _shown[i].objectToWorld;
                    return true;
                }
            }

            objectToWorld = Matrix4x4.identity;
            return false;
        }

        /// <summary>The surface boundaries the commits have published.</summary>
        public int BoundaryRecordCount => _boundaries.Count;

        /// <summary>One published surface boundary, in the order they were published.</summary>
        public bool TryGetBoundaryRecord(int index, out LogicalCutBoundaryRecord record)
        {
            if (index < 0 || index >= _boundaries.Count)
            {
                record = default;
                return false;
            }

            record = _boundaries[index];
            return true;
        }

        /// <summary>
        /// The two placements a committed side keeps apart. They differ whenever the side follows a fragment
        /// placement of its own: the registration holds the body's, while the side is drawn where it follows.
        /// Nothing is added to either -- a boundary becoming permanent changes which registration a side is drawn
        /// from, never where that side stands.
        /// </summary>
        private struct SidePlacements
        {
            /// <summary>What the side's registration holds afterwards: the body's own placement.</summary>
            internal Matrix4x4 registered;

            /// <summary>Where the side really stands: what its bounds are judged at and a boundary record keeps.</summary>
            internal Matrix4x4 drawn;
        }

        /// <summary>
        /// Where one side of a commit is drawn: the placement its fragment follows, or the registration's when it
        /// follows nothing. Nothing is applied after it.
        /// <para>
        /// A fragment that is not a current target has no placement of its own — it has been replaced or retired, and
        /// its shape stands where its geometry stands. That is not the same as a live fragment that follows something
        /// and was not said to be anywhere: the second is refused here rather than drawn at a placement that is not
        /// its own.
        /// </para>
        /// </summary>
        private bool TryBaseline(LogicalFragmentId fragment, Shown body, out Matrix4x4 baseline)
        {
            baseline = body.objectToWorld;
            if (Placement == null || !_ledger.IsCurrentTarget(fragment))
            {
                return true;
            }

            // The body itself, not a side of anything: this is where the whole registration is drawn from.
            switch (Placement.TryGetGeometryLocalToWorld(fragment, default, 0f, out Matrix4x4 followed))
            {
                case VpFragmentPlacementKind.Following:
                    baseline = followed;
                    return true;
                case VpFragmentPlacementKind.Static:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// The two placements of one side of a commit (DESIGN 5.1). A commit takes in no movement of its own: the
        /// side is drawn at the placement it follows, which is what it was drawn at before the commit, and its
        /// registration keeps the body's own placement. Which side of which cut it is does not come into it.
        /// </summary>
        private bool TrySidePlacements(LogicalFragmentId fragment, Shown body, out SidePlacements placements)
        {
            placements = default;
            if (!TryBaseline(fragment, body, out Matrix4x4 baseline))
            {
                return false;
            }

            placements.registered = body.objectToWorld;
            placements.drawn = baseline;
            return true;
        }

        /// <summary>One side of a commit, judged where it will really stand: the placement it is drawn at.</summary>
        private bool TryPrepareSide(
            VpStoredGeometry geometry,
            Shown body,
            in SidePlacements placements,
            out VpIndirectCommand[] commands,
            out Material[] commandMaterials,
            out Bounds localBounds)
        {
            Matrix4x4 placement = placements.drawn;
            return TryPrepare(geometry, out commands, out commandMaterials, out localBounds)
                && VpMultiCutSnapshot.IsWithinInputContract(localBounds, placement, body.lineageToGeometryLocal)
                && VpMultiCutSnapshot.IsWithinSectionBounds(
                    localBounds, placement, VpCapBoundsPolygon.EpsilonFor(localBounds));
        }

        /// <summary>
        /// Takes one produced side in, where it stands. The room was decided before anything was transferred, so a
        /// refusal here is an invariant broken rather than an ordinary outcome, and it stops the display.
        /// </summary>
        private void TakeBody(
            LogicalFragmentId fragment,
            VpStoredGeometry geometry,
            Shown body,
            in SidePlacements placements,
            VpIndirectCommand[] commands,
            Material[] commandMaterials,
            Bounds localBounds,
            VpClipBoundary[] reflected)
        {
            if (!_table.TryRegisterGeometryWithDisplayInstance(
                    geometry, out VpGeometryReference reference, out VpDisplayInstanceReference instance))
            {
                _broken = true;
                throw new InvalidOperationException(
                    "a committed side could not be registered although its room was taken into account");
            }

            var ranges = new VpGeometryRange[commands.Length];
            for (int c = 0; c < commands.Length; c++)
            {
                ranges[c] = commands[c].range;
            }

            var entry = new Shown
            {
                fragment = fragment,
                geometry = geometry,
                reference = reference,
                objectToWorld = placements.registered,
                lineageToGeometryLocal = body.lineageToGeometryLocal,
                reflected = reflected,
                commands = commands,
                commandMaterials = commandMaterials,
                ranges = ranges,
                localBounds = localBounds,
            };
            entry.instances.Add(instance);
            _shown.Add(entry);
            InputChanged();
        }

        /// <summary>
        /// Stops collecting a body and keeps what it holds until the next adoption has replaced what is drawn from it.
        /// </summary>
        private void Retire(Shown body)
        {
            _shown.Remove(body);
            _retiring.Add(body);
            InputChanged();
        }

        /// <summary>What a side reflects once it is committed: what the body reflected, and this cut on its own side.</summary>
        private static VpClipBoundary[] Reflecting(VpClipBoundary[] reflected, VpCapFace face, float side)
        {
            var with = new VpClipBoundary[reflected.Length + 1];
            Array.Copy(reflected, with, reflected.Length);
            with[reflected.Length] = new VpClipBoundary(face, side);
            return with;
        }

        // ----- frames ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Collects the ledger's state and settles this frame's draw data. Calling it again within one frame does
        /// nothing at all and answers true: a frame that has drawn is not rewritten.
        /// <para>
        /// False means this frame could not be settled from the latest logical state. For want of room -- commands,
        /// instances, display instances, snapshot or stencil room -- or a batch refusing the upload, the previous
        /// snapshot stays exactly as it was and keeps drawing, and the caller is told it is not the latest. For any other
        /// reason the snapshot could not be built, the display stops (<see cref="IsHalted"/>) before this frame draws;
        /// false from then on. Nothing about the ledger is changed either way.
        /// </para>
        /// </summary>
        public bool TryBeginFrame()
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfPreparing();
            if (_halted)
            {
                return false;
            }

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
                // A stop is decided here, before anything of this frame is drawn: the frame is not opened.
                if (_hasSnapshot && !_halted)
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
        /// registered and prepared in this frame for the adopted snapshot, and the display must not have stopped;
        /// otherwise this throws before registering anything.
        /// </summary>
        public void Render(int layer, Camera camera)
        {
            ThrowIfDisposed();
            ThrowIfBroken();
            ThrowIfHalted();
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

            // The surfaces, grouped by material: one forward call per run of commands sharing one.
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

            // The casters, grouped by which side of DESIGN 5.4's division a command falls on, over the same commands,
            // transforms, clip records and offsets as the surfaces above.
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

            // The counting and the caps, after the surfaces: their queues put them after the opaque bodies.
            cameraStencil.batch.Render(_stencilMaterials, _buffers, layer, camera);
        }

        // ----- what is shown ---------------------------------------------------------------------------------------

        /// <summary>
        /// How this display is showing that fragment: as a registered body, or as a fragment below one that the adopted
        /// snapshot draws, or below which it draws.
        /// </summary>
        public LogicalCutDisplayState StateOf(LogicalFragmentId fragment)
        {
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

                return entry.awaitingShown ? LogicalCutDisplayState.AwaitingInputs : LogicalCutDisplayState.Whole;
            }

            if (!_hasSnapshot || !fragment.IsSet)
            {
                return LogicalCutDisplayState.NotShown;
            }

            // Below a registered fragment: on the way from an adopted branch up to its registration's root.
            int steps = _ledger.OperationCount + 1;
            for (int b = 0; b < _snapshot.BranchCount; b++)
            {
                _snapshot.TryGetBranch(b, out VpMultiCutBranch branch);
                LogicalFragmentId root = _roots[branch.registration];
                LogicalFragmentId at = branch.fragment;
                for (int step = 0; step <= steps && at != root; step++)
                {
                    if (at == fragment)
                    {
                        return LogicalCutDisplayState.ProvisionalSplit;
                    }

                    if (!_ledger.TryGetOrigin(at, out CutOperationId origin, out _)
                        || !_ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                    {
                        break;
                    }

                    at = cut.source;
                }
            }

            return LogicalCutDisplayState.NotShown;
        }

        /// <summary>How many draw commands the adopted snapshot registers.</summary>
        public int DrawCommandCount => _commandCount;

        /// <summary>
        /// One command of the settled collection: its index range is the geometry's own, and its instance count is the
        /// number of render fragments that body is drawn as, which is how they all come to address the same range.
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

        /// <summary>How many caps the settled collection prepared: one per render fragment and selected boundary.</summary>
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
        /// never handed out.
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

            world = _snapshot.CapVertexArray[record.vertexStart + vertexIndex];
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

        // ----- diagnostics: the adopted snapshot, read only ------------------------------------------------------------

        /// <summary>The logical branches of the adopted snapshot, over every registration.</summary>
        public int BranchCount => _hasSnapshot ? _snapshot.BranchCount : 0;

        /// <summary>One branch of the adopted snapshot: its candidates, how many are selected, and what it is drawn as.</summary>
        public bool TryGetBranch(int index, out VpMultiCutBranch branch)
        {
            branch = default;
            return _hasSnapshot && _snapshot.TryGetBranch(index, out branch);
        }

        /// <summary>One candidate of the adopted snapshot and what the selection made of it.</summary>
        public bool TryGetCandidate(int index, out VpClipCandidate candidate, out VpClipSelectionState state)
        {
            candidate = default;
            state = default;
            return _hasSnapshot && _snapshot.TryGetCandidate(index, out candidate, out state);
        }

        /// <summary>The render fragments of the adopted snapshot.</summary>
        public int RenderFragmentCount => _hasSnapshot ? _snapshot.RenderFragmentCount : 0;

        /// <summary>One render fragment of the adopted snapshot: its root, its clip record, its offset and its caps.</summary>
        public bool TryGetRenderFragment(int index, out VpMultiCutRenderFragment renderFragment)
        {
            renderFragment = default;
            return _hasSnapshot && _snapshot.TryGetRenderFragment(index, out renderFragment);
        }

        /// <summary>
        /// Gives back everything this display owns: the buffers, the batches, and every geometry registration and
        /// display instance it took, each exactly once. Refused, changing nothing, while any camera has draws
        /// registered in the current frame; a later frame may dispose. A display that has stopped is disposed the same
        /// way. The ledger, the storage and the borrowed materials are left alone.
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

            for (int i = 0; i < _retiring.Count; i++)
            {
                // A body a commit replaced and whose next collection never came: it is the frame boundary here too.
                ReleaseReferences(_retiring[i]);
            }

            _retiring.Clear();
            _shown.Clear();
            _registrations.Clear();
            _geometries.Restart(0);
            _candidateGeometries.Restart(0);
            _capJobs.Release();
            _sides.Clear();
            _candidateSides.Clear();
            _commandCount = 0;
            _capRecordCount = 0;
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
            _capNormalBuffer.Dispose();
            _batch.Dispose();
            _buffers.Dispose();
        }

        // ----- collection ----------------------------------------------------------------------------------------

        private bool TryCollectAndUpload()
        {
            // 1. What each registration is now, read from the ledger, changing nothing.
            _registrations.Clear();
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                bool known = _ledger.TryGetFragmentState(entry.fragment, out LogicalFragmentState state);
                entry.dropping = !known || state == LogicalFragmentState.Retired;
                entry.awaiting = known && state == LogicalFragmentState.Live
                    && _ledger.TryGetActiveOperation(entry.fragment, out CutOperationId active)
                    && !_ledger.TryGetPreparedAnchorDistribution(active, out _);
                entry.split = false;
                entry.clipped = false;
                entry.renderFragments = 0;
                entry.firstRenderFragment = 0;
                entry.takenThisPass = 0;
                _registrations.Add(new VpMultiCutRegistration(
                    entry.fragment, entry.localBounds, entry.objectToWorld, entry.lineageToGeometryLocal, entry.reflected,
                    VpCapBoundsPolygon.EpsilonFor(entry.localBounds)));
            }

            // 2. One snapshot of every registration together, beside the adopted one. Room short is an ordinary
            //    refusal; anything else stops the display, decided here before this frame draws.
            //    <para>
            //    The structure -- the ledger's own validity, the lineage, the candidates, what is Selected and what is
            //    Ignored, and how the Ignored are grouped -- is settled again only when something it was settled from
            //    has changed. Where things stand is settled every time, because that is what moving an actor changes.
            //    A structure that cannot be taken over, for whatever reason, is settled again: taking it over is never
            //    what decides whether a frame is right.
            //    </para>
            long ledgerRevision = _ledger.Revision;
            long inputRevision = _inputRevision;
            bool structureStillHolds = _hasSnapshot
                && _snapshot.IsBuilt
                && _structureLedgerRevision == ledgerRevision
                && _structureInputRevision == inputRevision
                && _snapshot.RegistrationCount == _registrations.Count;
            VpMultiCutBuildOutcome outcome = structureStillHolds
                ? _building.TryBuildPlacementsFrom(_snapshot, _ledger, _registrations, Placement)
                : _building.TryBuild(_ledger, _registrations, _snapshot, Placement);
            if (structureStillHolds && outcome == VpMultiCutBuildOutcome.CapacityExceeded)
            {
                // The candidate could not hold that structure. Settling it again is what answers that, and only then
                // is a shortage this display's to refuse for.
                structureStillHolds = false;
                outcome = _building.TryBuild(_ledger, _registrations, _snapshot, Placement);
            }

            CapPolygonBuilds += _building.SectionBuildCount;
            if (outcome == VpMultiCutBuildOutcome.CapacityExceeded)
            {
                return RefuseForRoom();
            }

            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                if (!_halted && outcome == VpMultiCutBuildOutcome.InvalidInput)
                {
                    _haltInvalidInput = _building.InvalidInputReason;
                }

                Halt(ReasonOf(outcome));
                return false;
            }

            // 3. What each registration is drawn as; the commands and instances that takes.
            int renderFragments = _building.RenderFragmentCount;
            for (int r = 0; r < renderFragments; r++)
            {
                _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                Shown entry = _shown[rf.registration];
                if (entry.renderFragments == 0)
                {
                    entry.firstRenderFragment = r;
                }

                entry.renderFragments++;
                entry.clipped |= rf.clip.PlaneCount > 0;
                entry.split |= rf.root != entry.fragment || rf.rootPendingSide != 0f || rf.aggregated;
            }

            long commandCount = 0;
            long instanceCount = 0;
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                entry.split |= entry.renderFragments > 1;
                if (entry.renderFragments > 0)
                {
                    commandCount += entry.commands.Length;
                    instanceCount += (long)entry.commands.Length * entry.renderFragments;
                }
            }

            // Fixed capacity, decided before anything is taken or uploaded. The stencil side's room follows from these:
            // no more volume commands than eight per instance, no more caps than the snapshot holds.
            // The draw-range table holds one entry per registration in its fixed room. TryShow keeps every registration
            // at an instance or more within the instance capacity, so this is never short; it is asked here all the
            // same, before anything is taken, rather than found out while the candidate is built.
            if (commandCount > _commandCapacity || instanceCount > _instanceCapacity
                || _shown.Count > _candidateGeometries.Capacity)
            {
                return RefuseForRoom();
            }

            // 4. The display instances the new snapshot needs that are not held yet. A failure gives back what this
            //    pass took, and nothing already held is given back early to make room.
            if (!TryTakeInstances())
            {
                return RefuseForRoom();
            }

            // 5. The candidate, beside the adopted draw data.
            BuildCandidate(out int commands, out int instances);

            // 6. The largest stencil arrangement this candidate could need -- every non-empty cap a job of its own, its
            //    own-face volume and its fan, in one colour -- asked of the fixed sizes and of every registered camera's
            //    batch, by count, before anything is written. A judgement of capacity and form, not a promise of each
            //    later upload, and not an approval of any sharing.
            BuildLargestStencilArrangement(out int stencilCommands, out int capIndexCount, out int stencilColours);
            int capVertices = _building.CapVertexCount;
            bool stencilFits = stencilCommands <= _stencilCommandCapacity
                && capVertices <= _stencilCapVertexCapacity
                && capIndexCount <= _stencilCapIndexCapacity;
            if (stencilFits)
            {
                foreach (CameraStencil slot in _cameraStencils)
                {
                    if (slot != null && !slot.batch.CanUpload(
                            _candidateStencilCommands, stencilCommands, _candidateStencilTransforms,
                            _candidateStencilClips, _building.CapVertexArray, capVertices, _candidateCapIndices,
                            capIndexCount, _candidateStencilColors, stencilColours))
                    {
                        stencilFits = false;
                        break;
                    }
                }
            }

            // The body batch is asked too, by exactly the counts and contents it would be sent, before anything is written.
            if (stencilFits && !_batch.CanUpload(_candidateCommands, commands, _candidateTransforms, _candidateClips))
            {
                stencilFits = false;
            }

            if (!stencilFits)
            {
                GiveBackInstancesTakenThisPass();
                _candidateSides.Clear();
                return RefuseForRoom();
            }

            // 7. The body's upload, by count. The stereo condition is read once, here. It was asked a moment ago with
            //    these very counts; a refusal now is not a shortfall, and like a GPU call that throws it stops the display
            //    as broken, since what reached the GPU cannot be established. Whatever references this pass took stay
            //    with their registrations, and Dispose gives every one back, once.
            bool singlePassInstanced = SinglePassInstanced;
            bool uploaded;
            try
            {
                // The cap normals of the snapshot about to be taken up go first, under this same guard: what reached
                // the GPU cannot be established after a throw, so the display stops rather than drawing new bodies
                // with normals of another build.
                if (RefuseCapNormalUploadForTest != null && RefuseCapNormalUploadForTest())
                {
                    throw new InvalidOperationException("the cap normals' upload was refused for a test");
                }

                UploadCapNormals(_building);
                uploaded = RefuseBodyUploadForTest != null && RefuseBodyUploadForTest()
                    ? false
                    : _batch.TryUpload(_candidateCommands, commands, _candidateTransforms, _candidateClips, singlePassInstanced);
            }
            catch
            {
                _broken = true;
                _candidateSides.Clear();
                throw;
            }

            if (!uploaded)
            {
                _broken = true;
                _candidateSides.Clear();
                throw new InvalidOperationException(
                    "the display batch refused an upload it had accepted by the same counts a moment before; this display "
                    + "stops");
            }

            // 8. Everything the GPU needed has arrived: the candidate becomes the adopted snapshot, and the arrays and
            //    the snapshots change places. The revisions this structure answers to are recorded **here**, where the
            //    update is really taken: a build that was refused anywhere above leaves them as they were, so the next
            //    frame settles the structure again rather than treating an update it never took as dealt with.
            Adopt(commands);
            _structureLedgerRevision = ledgerRevision;
            _structureInputRevision = inputRevision;
            CommandUploads++;

            // 9. Only now is the display's own state changed: the references no longer needed are given back, and a
            //    registered fragment that is retired is let go. A body a commit replaced goes here too, never earlier:
            //    until this adoption it was what the snapshot on screen was drawn from.
            for (int r = _retiring.Count - 1; r >= 0; r--)
            {
                ReleaseReferences(_retiring[r]);
                _retiring.RemoveAt(r);
            }

            for (int g = _shown.Count - 1; g >= 0; g--)
            {
                Shown entry = _shown[g];
                entry.takenThisPass = 0;
                if (entry.dropping)
                {
                    ReleaseReferences(entry);
                    _shown.RemoveAt(g);
                    InputChanged();
                    continue;
                }

                entry.splitShown = entry.split;
                entry.awaitingShown = entry.awaiting && !entry.split;
                entry.renderFragmentsShown = entry.renderFragments;
                int required = Math.Max(1, entry.renderFragments);
                while (entry.instances.Count > required)
                {
                    int last = entry.instances.Count - 1;
                    _table.TryRetireDisplayInstance(entry.instances[last]);
                    entry.instances.RemoveAt(last);
                }
            }

            return true;
        }

        /// <summary>
        /// Takes, for every registration kept, the display instances it lacks: one per render fragment, never fewer than
        /// one. A registration being let go takes none. On a failure, everything this pass took is given back and
        /// nothing else is touched.
        /// </summary>
        private bool TryTakeInstances()
        {
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                if (entry.dropping)
                {
                    continue;
                }

                int required = Math.Max(1, entry.renderFragments);
                while (entry.instances.Count < required)
                {
                    if (!_table.TryAddDisplayInstance(entry.reference, out VpDisplayInstanceReference instance))
                    {
                        GiveBackInstancesTakenThisPass();
                        return false;
                    }

                    entry.instances.Add(instance);
                    entry.takenThisPass++;
                }
            }

            return true;
        }

        /// <summary>Gives back exactly what this pass took, newest first; what the adopted snapshot holds stays held.</summary>
        private void GiveBackInstancesTakenThisPass()
        {
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                while (entry.takenThisPass > 0)
                {
                    int last = entry.instances.Count - 1;
                    _table.TryRetireDisplayInstance(entry.instances[last]);
                    entry.instances.RemoveAt(last);
                    entry.takenThisPass--;
                }
            }
        }

        /// <summary>
        /// The candidate draw data from the built snapshot: per registration and command, one command whose instances
        /// are its render fragments in order, each with the body's transform and that render fragment's clip record of
        /// every selected face; where each render fragment's commands and transform are, which a stencil volume is later
        /// drawn from with its own face's clip; every registration's draw ranges; and one cap record per snapshot cap.
        /// </summary>
        private void BuildCandidate(out int commandCount, out int instanceCount)
        {
            _candidateSides.Clear();
            int command = 0;
            int instance = 0;
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                if (entry.renderFragments == 0)
                {
                    continue;
                }

                int bodyStart = command;
                for (int c = 0; c < entry.commands.Length; c++)
                {
                    VpIndirectCommand source = entry.commands[c];
                    _candidateCommands[command] = new VpIndirectCommand(source.range, source.localBounds, entry.renderFragments);
                    _candidateCommandMaterials[command] = entry.commandMaterials[c];
                    _candidateCommandProvisional[command] = entry.clipped;
                    command++;
                    for (int k = 0; k < entry.renderFragments; k++)
                    {
                        int r = entry.firstRenderFragment + k;
                        _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                        _candidateTransforms[instance] = rf.geometryLocalToWorld;
                        _candidateClips[instance] = rf.clip;
                        _candidateSides.Add(SideOf(entry, r, rf));
                        instance++;
                    }
                }

                for (int k = 0; k < entry.renderFragments; k++)
                {
                    int r = entry.firstRenderFragment + k;
                    _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment placed);
                    _candidateRfCommandStart[r] = bodyStart;
                    _candidateRfCommandCount[r] = entry.commands.Length;
                    _candidateRfTransform[r] = placed.geometryLocalToWorld;
                }
            }

            if (_candidateRoots.Length < _shown.Count)
            {
                _candidateRoots = new LogicalFragmentId[_shown.Count];
            }

            // The draw ranges of every registration the snapshot was built from, in its order and as many -- those drawn
            // as nothing and those being let go included -- so that the table is the snapshot's, adopted with it.
            _candidateGeometries.Restart(_shown.Count);
            for (int g = 0; g < _shown.Count; g++)
            {
                _candidateRoots[g] = _shown[g].fragment;
                _candidateGeometries.Add(_shown[g].ranges);
            }

            int caps = _building.CapCount;
            for (int i = 0; i < caps; i++)
            {
                _candidateCapRecords[i] = CapRecordOf(i);
            }

            commandCount = command;
            instanceCount = instance;
        }

        /// <summary>
        /// What one render fragment is, as a side: the cut it is a side of, and whether that side is published.
        /// <para>
        /// Every part of that is a fact about the ledger, settled when the structure was, and read back here. It used
        /// to be asked of the ledger here instead -- once per render fragment, inside the loop over a body's commands
        /// -- which asked the structure again on frames that had settled it already.
        /// </para>
        /// </summary>
        private LogicalCutDisplaySide SideOf(Shown entry, int renderFragment, in VpMultiCutRenderFragment rf)
        {
            if (!_building.TryGetSideIdentity(renderFragment, out VpMultiCutSnapshot.VpMultiCutSideIdentity identity)
                || !identity.operation.IsSet)
            {
                return new LogicalCutDisplaySide(
                    entry.fragment, renderFragment, default, 0f, false, default, false, rf.clip);
            }

            return new LogicalCutDisplaySide(
                entry.fragment, renderFragment, identity.operation, identity.side, identity.published,
                identity.published ? rf.root : default, identity.fixedByAnchors, rf.clip);
        }

        /// <summary>One snapshot cap as a record: its boundary's cut and side, published or not, and its polygon's place.</summary>
        private LogicalCutCapRecord CapRecordOf(int capIndex)
        {
            _building.TryGetCap(capIndex, out VpMultiCutCap cap);
            _building.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment rf);
            _building.TryGetBranch(rf.branchStart, out VpMultiCutBranch representative);
            _building.TryGetCandidate(representative.candidateStart + (capIndex - rf.capStart), out VpClipCandidate candidate, out _);

            // What this cap's boundary is, as the ledger said when the structure was settled. Only where the cap is
            // -- its world plane, its outward normal and its vertices -- comes from this frame.
            int candidateIndex = representative.candidateStart + (capIndex - rf.capStart);
            _building.TryGetCapIdentity(candidateIndex, out VpMultiCutSnapshot.VpMultiCutCapIdentity identity);
            return new LogicalCutCapRecord(
                _shown[rf.registration].fragment, cap.renderFragment, cap.boundary.face.operation, cap.boundary.side,
                identity.published, identity.child, identity.fixedByAnchors, ToVector4(cap.worldPlane),
                cap.outwardNormal, cap.vertexStart, cap.vertexCount);
        }

        /// <summary>
        /// The most any camera's arrangement of this candidate could hold, in one colour: every cap whose drawing polygon
        /// is not empty taken as a job of its own -- no two sharing a volume -- with its own-face volume, and every such
        /// cap's fan. It is only asked about, never uploaded, and it judges room and form alone: that the volume groups
        /// of a real preparation may share is not decided here, and a camera's colour limit is never a reason to refuse
        /// an adoption. It also bounds the last colour (D-186): a render fragment's one volume there replaces at least
        /// one own-face volume of the same commands (see <see cref="TryArrange"/>).
        /// </summary>
        private void BuildLargestStencilArrangement(out int stencilCommands, out int capIndexCount, out int colours)
        {
            stencilCommands = 0;
            capIndexCount = 0;
            _arrangedVolumes = 0;
            _arrangedCapIndices = 0;
            int caps = _building.CapCount;
            for (int i = 0; i < caps; i++)
            {
                _building.TryGetCap(i, out VpMultiCutCap cap);
                if (cap.vertexCount == 0)
                {
                    continue;
                }

                _building.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment rf);
                var world = new Vector4(cap.worldPlane.x, cap.worldPlane.y, cap.worldPlane.z, cap.worldPlane.w);
                AppendVolume(
                    cap.renderFragment, VpInstanceClip.Keep(world, cap.boundary.side), _candidateCommands,
                    _candidateRfCommandStart, _candidateRfCommandCount, _candidateRfTransform, ref stencilCommands);
                AppendFan(_candidateCapRecords[i], ref capIndexCount);
            }

            colours = stencilCommands > 0 || capIndexCount > 0 ? 1 : 0;
            if (colours > 0)
            {
                // Only the counts and the form of this record are asked about; it is never uploaded or drawn, and the
                // colour is the same one a real preparation would use.
                _candidateStencilColors[0] = new VpStencilCapColor(0, stencilCommands, 0, capIndexCount, ProvisionalCapColour);
            }
        }

        /// <summary>
        /// The candidate becomes what is drawn. Everything trades places -- the snapshots, the arrays and the list of
        /// sides alike -- so what was adopted a moment ago becomes the room the next candidate is built in. Nothing is
        /// copied and nothing can grow here.
        /// </summary>
        private void Adopt(int commandCount)
        {
            Swap(ref _snapshot, ref _building);
            Swap(ref _commands, ref _candidateCommands);
            Swap(ref _commandMaterials, ref _candidateCommandMaterials);
            Swap(ref _commandProvisional, ref _candidateCommandProvisional);
            Swap(ref _transforms, ref _candidateTransforms);
            Swap(ref _clips, ref _candidateClips);
            Swap(ref _capRecords, ref _candidateCapRecords);
            Swap(ref _rfCommandStart, ref _candidateRfCommandStart);
            Swap(ref _rfCommandCount, ref _candidateRfCommandCount);
            Swap(ref _rfTransform, ref _candidateRfTransform);
            Swap(ref _roots, ref _candidateRoots);
            Swap(ref _geometries, ref _candidateGeometries);
            Swap(ref _sides, ref _candidateSides);
            _candidateSides.Clear();

            _commandCount = commandCount;
            _capRecordCount = _snapshot.CapCount;
            _hasSnapshot = true;

            // Every camera's preparation was for the snapshot just replaced.
            _generation++;
        }

        private static void Swap<T>(ref T a, ref T b)
        {
            T held = a;
            a = b;
            b = held;
        }

        /// <summary>
        /// A collection refused for want of room keeps the adopted snapshot drawing, as it was -- unless that snapshot
        /// draws a fragment retired since it was adopted, in an aggregate or not. Then keeping it would show what is no
        /// longer there, so the display stops instead; nothing of the new snapshot is adopted in part.
        /// </summary>
        private bool RefuseForRoom()
        {
            if (_hasSnapshot && AdoptedDrawsARetiredFragment())
            {
                Halt(LogicalCutDisplayHaltReason.RetiredWhileShown);
            }

            return false;
        }

        /// <summary>
        /// Whether a fragment the ledger has retired -- the source of an aborted cut, which is the only way one retires --
        /// is, or lies below, a branch the adopted snapshot draws. A branch is a live fragment when it was adopted, so a
        /// fragment retired before that is never at or below one: a match is a retirement since.
        /// </summary>
        private bool AdoptedDrawsARetiredFragment()
        {
            int steps = _ledger.OperationCount + 1;
            for (int position = 0; _ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
            {
                if (operation.state != LogicalCutOperationState.Aborted)
                {
                    continue;
                }

                LogicalFragmentId at = operation.source;
                for (int step = 0; step <= steps; step++)
                {
                    if (IsAdoptedBranch(at))
                    {
                        return true;
                    }

                    if (!_ledger.TryGetOrigin(at, out CutOperationId origin, out _)
                        || !_ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                    {
                        break;
                    }

                    at = cut.source;
                }
            }

            return false;
        }

        private bool IsAdoptedBranch(LogicalFragmentId fragment)
        {
            for (int b = 0; b < _snapshot.BranchCount; b++)
            {
                _snapshot.TryGetBranch(b, out VpMultiCutBranch branch);
                if (branch.fragment == fragment)
                {
                    return true;
                }
            }

            return false;
        }

        private static LogicalCutDisplayHaltReason ReasonOf(VpMultiCutBuildOutcome outcome)
        {
            switch (outcome)
            {
                case VpMultiCutBuildOutcome.RetiredInsideAggregate:
                    return LogicalCutDisplayHaltReason.RetiredInsideAggregate;
                default:
                    return LogicalCutDisplayHaltReason.InvalidInput;
            }
        }

        private void Halt(LogicalCutDisplayHaltReason reason)
        {
            if (_halted)
            {
                return;
            }

            _halted = true;
            _haltReason = reason;
        }

        /// <summary>
        /// Whether that fragment is shown here, or is an ancestor or a descendant of a fragment shown here -- asked of the
        /// ledger's origins, so a publication that happened between two collections is seen just the same.
        /// </summary>
        private bool IsOnShownLineage(LogicalFragmentId fragment)
        {
            int steps = _ledger.OperationCount + 1;
            for (int i = 0; i < _shown.Count; i++)
            {
                LogicalFragmentId shown = _shown[i].fragment;
                if (shown == fragment || IsBelow(fragment, shown, steps) || IsBelow(shown, fragment, steps))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether <paramref name="ancestor"/> is on <paramref name="fragment"/>'s origins, strictly above it.</summary>
        private bool IsBelow(LogicalFragmentId fragment, LogicalFragmentId ancestor, int steps)
        {
            LogicalFragmentId at = fragment;
            for (int step = 0; step <= steps; step++)
            {
                if (!_ledger.TryGetOrigin(at, out CutOperationId origin, out _)
                    || !_ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return false;
                }

                at = cut.source;
                if (at == ancestor)
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
                count += _shown[i].commands.Length * Math.Max(1, _shown[i].renderFragmentsShown);
            }

            return count;
        }

        /// <summary>The commands every registration would draw at one render fragment each: the room a swap must fit.</summary>
        private int ShownCommandCount()
        {
            int count = 0;
            for (int i = 0; i < _shown.Count; i++)
            {
                count += _shown[i].commands.Length;
            }

            return count;
        }

        private void ReleaseReferences(Shown entry)
        {
            for (int i = entry.instances.Count - 1; i >= 0; i--)
            {
                _table.TryRetireDisplayInstance(entry.instances[i]);
            }

            entry.instances.Clear();
            entry.takenThisPass = 0;
            _table.TryRetireGeometry(entry.reference);
        }

        /// <summary>
        /// What this display needs to hold about a body it is taking in: the commands, their materials, and the local
        /// bounds of the vertices the indices actually reach, measured here once.
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

        private void ThrowIfHalted()
        {
            if (_halted)
            {
                throw new InvalidOperationException(
                    "this display stopped drawing (" + _haltReason + "): what it adopted earlier may show what is no "
                    + "longer there, so it neither prepares nor draws; dispose it");
            }
        }
    }
}
