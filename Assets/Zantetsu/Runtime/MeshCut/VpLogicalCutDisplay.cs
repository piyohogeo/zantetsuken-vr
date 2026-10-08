using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Collections;
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

        /// <summary>
        /// A need past a limit of <see cref="VpLogicalCutDisplayLimits"/>, or room that could not be made, told to
        /// <see cref="VpLogicalCutDisplay.RoomFailureHandler"/>: the owner ends the Player, and nothing older is drawn in
        /// the meantime.
        /// </summary>
        RoomNotEstablished = 5,
    }

    /// <summary>
    /// How far a display may grow each of its counts. The counts it is made with are where it starts; a collection
    /// that needs more grows the room it builds in, up to these, and switches to it whole at adoption. A limit equal to
    /// its first capacity keeps that count fixed. <c>default</c> keeps every count fixed.
    /// <para>
    /// **Two different counts stand behind "instances" (DESIGN 5.6).** The draw command slots and the instance
    /// records are used up over the display's life: a slot is taken at the end and never again, so their room is
    /// about everything ever drawn. How many render fragments are drawn at once is another count, and everything
    /// sized from it -- the snapshot's render fragments and caps, the cap records, vertices, indices and normals, the
    /// stencil commands of every camera's batch, the registrations' own tables -- is about what is alive. A caller
    /// that gives the two apart names the render fragments' first room and limit here; one that does not
    /// (<see cref="firstRenderFragments"/> and <see cref="renderFragments"/> zero) has them follow the instances',
    /// as before there were two.
    /// </para>
    /// </summary>
    public readonly struct VpLogicalCutDisplayLimits
    {
        public VpLogicalCutDisplayLimits(int commands, int instances, int branches, int candidates)
            : this(commands, instances, branches, candidates, 0, 0)
        {
        }

        public VpLogicalCutDisplayLimits(
            int commands, int instances, int branches, int candidates, int firstRenderFragments, int renderFragments)
        {
            this.commands = commands;
            this.instances = instances;
            this.branches = branches;
            this.candidates = candidates;
            this.firstRenderFragments = firstRenderFragments;
            this.renderFragments = renderFragments;
        }

        public readonly int commands;
        public readonly int instances;
        public readonly int branches;
        public readonly int candidates;

        /// <summary>Where the render fragments' room starts; zero: at the first instance capacity.</summary>
        public readonly int firstRenderFragments;

        /// <summary>How far the render fragments' room may grow; zero: as far as the instances'.</summary>
        public readonly int renderFragments;

        internal bool IsDefault =>
            commands == 0 && instances == 0 && branches == 0 && candidates == 0 && firstRenderFragments == 0 && renderFragments == 0;
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
    /// **Room grows.** A count that falls short is grown -- at least doubled, never past its limit
    /// (<see cref="VpLogicalCutDisplayLimits"/>) -- in the room the collection builds in, and the build is tried again,
    /// within that collection -- at most as many times as the counts can still grow before their limits. Only what nothing draws from is replaced: the snapshot being
    /// built, the candidate arrays and the scratch. A GPU buffer too small for the candidate is replaced by a new one
    /// written whole before it is switched to at adoption, and the one replaced is released only after a readback asked
    /// for when it was replaced has completed; nothing waits for the GPU. A need past a limit, or room that cannot be
    /// made, is told once to <see cref="RoomFailureHandler"/> and stops the display
    /// (<see cref="LogicalCutDisplayHaltReason.RoomNotEstablished"/>), so no older snapshot keeps drawing for good.
    /// Without a handler, a shortfall keeps the previous snapshot drawing, whole, as it always did: nothing of the new
    /// one is adopted or uploaded -- but only when that snapshot draws no fragment retired since it was adopted;
    /// otherwise the display stops (<see cref="LogicalCutDisplayHaltReason.RetiredWhileShown"/>). The body's upload is asked, by the counts and
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
    /// **A preparation allocates nothing.** The classification's room, the arrangement and the batches are made at the
    /// sizes the display's room gives, and made again larger only by a collection that grew that room -- never by a
    /// preparation, which fills them to a count each time. A cap is read as a look at the adopted snapshot's own vertices, held only while the preparation runs. A
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

    public sealed partial class VpLogicalCutDisplay : IDisposable, IVpGpuCullTarget, IVpNativeDrawHost
    {
        internal sealed class Shown
        {
            public LogicalFragmentId fragment;
            internal long readFamilyRevision = -1;
            internal LogicalFragmentId readFragment;
            public VpStoredGeometry geometry;
            public VpGeometryReference reference;

            // Every display instance this registration holds, the one taken with the geometry first: one per render
            // fragment it is drawn as, and never fewer than one.
            public readonly List<VpDisplayInstanceReference> instances = new List<VpDisplayInstanceReference>(2);
            public int takenThisPass;

            public Matrix4x4 objectToWorld;

            public Matrix4x4 lineageToGeometryLocal;

            // What the geometry reflects: an unchangeable set with its lookup, made when the body was taken in or a commit
            // added a boundary, and read by the snapshot through that lookup (2026-10-01).
            public VpReflectedSet reflected;
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

            // Where its render fragments began in the adopted snapshot's numbering: what a side's number is read from.
            public int firstRenderFragmentShown;

            // The draw slots this registration holds (DESIGN 5.6): of the side last built, which is the adopted one
            // whenever no collection is under way. And, for a collection under way: the collection that last changed
            // them, what they were before it (put back if it is not adopted), and what it has to write.
            public SlotState slots, slotsBefore;
            public int slotPass = -1, writtenPass = -1;
            public bool rewrite, releasing;

            // The frame of the last adopted collection whose ordinary processing wrote an instance record of this
            // registration (DESIGN 5.6, D-202): a placement update or a structural write, whatever it wrote. Unset
            // until first adopted. The compaction orders by it; nothing else reads it. And the collection under way
            // that wrote one, before it is adopted.
            public int lastWrittenFrame = int.MinValue;
            public int notedPass = -1;

            // The next registration of the same family in _shown, or -1: the chain a ledger notice is followed along.
            internal int familyNext = -1;
        }

        private readonly VpCpuGeometryStorage _storage;
        private readonly VpGeometryReferenceTable _table;
        private readonly LogicalCutLedger _ledger;
        private readonly IReadOnlyDictionary<int, Material> _materials;
        private readonly Material _shadowMaterial;
        private readonly Material _provisionalShadowMaterial;
        private readonly VpGpuIndexedGeometryBuffers _buffers;
        // Replaced only at adoption, by one written whole for the candidate (see the class notes on room).
        private VpIndexedIndirectDrawBatch _batch;

        // Selecting on the GPU (DESIGN 4.5.7): what the body batch -- and every larger one that replaces it -- is made
        // with, or null for VP Stage 3; and the materials this display made for it, copies of the ones it was given
        // with the variant that reads the selection's list switched on, which are its own to destroy.
        private VpGpuCullSetup _cull;
        private List<Material> _ownedMaterials;
        private bool _cullMissLogged;
        private long _pastCullDispatches;
        private readonly VpStencilCapMaterials _stencilMaterials;
        private readonly VpStencilSettings _settings;

        // One slot per camera that may hold stencil work: fixed in number, filled and emptied only by registration.
        private readonly CameraStencil[] _cameraStencils;
        private int _stencilCommandCapacity;
        private int _stencilCapVertexCapacity;

        // The outward normal of every cap vertex of the adopted snapshot, in its order: the scratch it is gathered in
        // and the buffer the cap materials read. At the cap vertex capacity, written by count; the buffer is replaced,
        // written whole, at the adoption of a snapshot it is too small for.
        private VpNumericRoom<Vector4> _capNormals;
        private GraphicsBuffer _capNormalBuffer;
        private int _capNormalCount;

        // The caps as they are drawn (DESIGN 5.6, D-208). The room and the buffer above hold a record a cap now -- four
        // float4: the three rows of its render fragment's placement, then its outward normal -- and this room holds the
        // adopted snapshot's cap vertices in their geometry's local frame, the cap's number in w, laid out when a
        // collection that settled a structure is adopted and at no other time. A camera's batch sends them when it
        // holds another layout than this one.
        internal const int CapPlacementStride = 4;
        private VpNumericRoom<Vector4> _capLocalVertices;
        private long _capLayout;
        private bool _capLayoutStale = true;
        private int _capLayoutRoom = -1;

        /// <summary>
        /// Observation: times a camera's batch was sent the cap vertices (it held another layout, or none), and the
        /// vertices sent, since this display was made -- counted where they are sent, so a camera that is no longer
        /// drawn for takes nothing away. A body that moves sends none.
        /// </summary>
        public long CameraCapLayoutUploads { get; private set; }
        public long CameraCapLayoutVertices { get; private set; }

        /// <summary>
        /// Observation: of those, the sendings that were a camera's first -- its batch held no cap vertices yet (a
        /// camera newly drawn for, or one that had seen no cap). The others sent another layout to a camera that held one.
        /// </summary>
        public long CameraCapLayoutFirstUploads { get; private set; }
        public long CameraCapLayoutFirstVertices { get; private set; }

        // What the collections' records have told of the two sums above (a record tells what was sent since the one before).
        private long _cameraCapUploadsTold, _cameraCapVerticesTold, _cameraCapFirstUploadsTold, _cameraCapFirstVerticesTold;

        /// <summary>Observation: times the placed (world) cap vertices were made for a reader of either snapshot. The collection and the drawing ask for none.</summary>
        internal long PlacedCapFills => _snapshot.PlacedCapFills + (ReferenceEquals(_building, _snapshot) ? 0 : _building.PlacedCapFills);

        /// <summary>For tests: one of the local cap vertices as laid out for the adopted snapshot (the cap's number in w).</summary>
        internal Vector4 CapLocalVertexForTest(int index) => _capLocalVertices[index];

        /// <summary>Observation: times the local cap vertices were laid out (a structure adopted), and the vertices laid out.</summary>
        public long CapLayouts { get; private set; }
        public long CapLayoutVertices { get; private set; }
        private int _stencilCapIndexCapacity;

        // What the stencil batches of cameras no longer registered had counted, so the totals do not go backwards.
        private int _retiredStencilUploads;
        private int _retiredStencilBufferWrites;
        private int _retiredStencilInitIssues;
        private int _retiredStencilVolumeIssues;
        private int _retiredStencilCapIssues;
        private readonly MaterialPropertyBlock _properties = new MaterialPropertyBlock();
        private readonly Func<int> _frameSource;

        // The room a collection builds in, and how far it may grow. The adopted side may be smaller for one collection
        // after a growth: it is read by count only. The commands and the instances are the draw slots' room -- command
        // slots and instance records, used up as they are taken at the end (DESIGN 5.6). The fragments are how many
        // render fragments, and registrations, there may be at once: the snapshot's render fragments and caps, the cap
        // and stencil arrays and every camera's stencil batch are sized from them, and never from the instances.
        private int _commandCapacity;
        private int _instanceCapacity;
        private int _fragmentCapacity;
        private int _branchCapacity;
        private int _candidateCapacity;
        private readonly int _chainDepth;
        private readonly VpLogicalCutDisplayLimits _limits;
        private readonly int _fragmentLimit;

        // GPU objects replaced by larger ones, each kept until a readback asked for when it was replaced has completed.
        private readonly List<RetiredGpu> _retiredGpu = new List<RetiredGpu>(2);
        private bool _retiredReadbackErrorLogged;
        private bool _roomFailed;


        // A replaced batch, disposed through the display's hand-over: every plugin route whose events may still name
        // its buffers is waited on.
        private sealed class RetiredBatch : IDisposable
        {
            private readonly VpIndexedIndirectDrawBatch _batch;
            private readonly VpLogicalCutDisplay _display;

            public RetiredBatch(VpIndexedIndirectDrawBatch batch, VpLogicalCutDisplay display)
            {
                _batch = batch;
                _display = display;
            }

            public void Dispose() => _batch.Dispose(_display._giveUpBuffer);
        }

        private sealed class RetiredGpu
        {
            public IDisposable owner;
            public UnityEngine.Rendering.AsyncGPUReadbackRequest request;
            public bool done;
            public bool error;

            public void Completed(UnityEngine.Rendering.AsyncGPUReadbackRequest completed)
            {
                done = true;
                error = completed.hasError;
            }
        }

        /// <summary>
        /// The draw slots of one registration (DESIGN 5.6). Its commands stand in <c>commandStart .. + commands.Length</c>,
        /// one slot a command, in the commands' order; its instance records in a region of
        /// <c>commands.Length * instanceStride</c> from <c>instanceStart</c>, where command <c>c</c> has room for
        /// <c>instanceStride</c> records from <c>instanceStart + c * instanceStride</c> on and draws the first
        /// <c>renderFragments</c> of them -- its k-th render fragment at the k-th. Neither moves while the registration
        /// is drawn, except that the region is taken anew when more render fragments are drawn than it has room for.
        /// <c>part</c> is what the content was written for: the structure the snapshot settled for this registration.
        /// </summary>
        internal struct SlotState
        {
            public bool held;
            public int commandStart, instanceStart, instanceStride, renderFragments;
            public bool clipped;
            public object part;
            public long partSerial;
        }

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
        private VpCapJobClassification _capJobs;

        // The draw ranges of every registration of the adopted snapshot, and of the one being built, in the snapshot's
        // registration order -- a registration drawn as nothing and one being let go included. They change places with
        // the snapshots on adoption, so a preparation reads the table its snapshot was built with. Both are made with the
        // display at the instance capacity -- every registration holds at least one instance, so there are never more
        // registrations than instances -- and the candidate's is made again larger when the instances grow.
        private GeometryTable _geometries;
        private GeometryTable _candidateGeometries;

        // The adopted draw data: what the GPU holds and what is being drawn. Nothing here is touched until an upload
        // has succeeded, so a refused collection leaves exactly this on screen. Each has a candidate twin of the same
        // fixed size; adoption trades them.
        // The commands, their materials and which caster each takes are by command slot; the transforms, the clips and
        // the sides by instance record (DESIGN 5.6). A free command slot holds a command of no instance and no material.
        private VpNumericRoom<LogicalCutDisplaySide> _sides;
        private VpNumericRoom<VpIndirectCommand> _commands;
        private VpNumericRoom<int> _commandStarts;
        private Material[] _commandMaterials;
        private VpNumericRoom<bool> _commandProvisional;
        private VpNumericRoom<Matrix4x4> _transforms;
        private VpNumericRoom<VpInstanceClip> _clips;
        private VpNumericRoom<LogicalCutCapRecord> _capRecords;
        private VpNumericRoom<int> _rfCommandStart;
        private VpNumericRoom<int> _rfCommandCount;
        private VpNumericRoom<Matrix4x4> _rfTransform;
        private VpNumericRoom<LogicalFragmentId> _roots;
        private int _commandCount;
        private int _capRecordCount;

        private VpNumericRoom<LogicalCutDisplaySide> _candidateSides;
        private VpNumericRoom<VpIndirectCommand> _candidateCommands;
        private VpNumericRoom<int> _candidateCommandStarts;
        private Material[] _candidateCommandMaterials;
        private VpNumericRoom<bool> _candidateCommandProvisional;
        private VpNumericRoom<Matrix4x4> _candidateTransforms;
        private VpNumericRoom<VpInstanceClip> _candidateClips;
        private VpNumericRoom<LogicalCutCapRecord> _candidateCapRecords;
        private VpNumericRoom<int> _candidateRfCommandStart;
        private VpNumericRoom<int> _candidateRfCommandCount;
        private VpNumericRoom<Matrix4x4> _candidateRfTransform;
        private VpNumericRoom<LogicalFragmentId> _candidateRoots;

        // Counts adoptions. A camera's preparation names the snapshot it was made for, and a newer one voids it.
        private long _generation;

        // A stencil arrangement being made: for the largest one a candidate could need, checked before adoption, and
        // for one camera's colours when it is prepared. At the stencil capacity, made again larger when it grows.
        private VpNumericRoom<VpIndirectCommand> _candidateStencilCommands;
        private VpNumericRoom<Matrix4x4> _candidateStencilTransforms;
        private VpNumericRoom<VpInstanceClip> _candidateStencilClips;
        private VpNumericRoom<int> _candidateCapIndices;
        private readonly VpStencilCapColor[] _candidateStencilColors;

        private int _capRecordCapacity;
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

            // Selecting on the GPU (DESIGN 4.5.7): which of the body batch's views is this camera's -- its slot -- and,
            // for the frame it last registered draws in, the batch those draws read and whether their selection was
            // issued.
            public int view;
            public VpIndexedIndirectDrawBatch cullBatch;
            public int cullRequestedFrame = int.MinValue;
            public int cullIssuedFrame = int.MinValue;

            // The view this camera was last rendered with (D-210), as its host noted it at the end of that rendering:
            // how many eyes, six planes an eye (normals inward), and the frame it was noted in. Read by the held
            // placements of a collection in that frame or the one after.
            public readonly Vector4[] viewPlanes = new Vector4[VpHeldPlacements.ViewPlanes];
            public int viewEyes;
            public int viewFrame = int.MinValue;
        }

        private static readonly int CapNormalsId = Shader.PropertyToID("_VpCapNormals");
        private static readonly int CapPlacementsId = Shader.PropertyToID("_VpCapPlacements");
        private static readonly int CapLocalId = Shader.PropertyToID("_VpCapLocal");
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
        /// Reusable immutable structural results, shared by the adopted and building snapshots. Dirty families are
        /// found from the ledger's family revisions and registration inputs, never the display-wide revision.
        /// </summary>
        private readonly VpMultiCutSnapshot.StructurePool _structurePool;
        internal object StructureForTest(LogicalFragmentId root) => _snapshot.StructureForTest(root);
        internal long FamiliesRebuiltForTest => _snapshot.FamiliesRebuilt + _building.FamiliesRebuilt;

        /// <summary>
        /// Counts the changes to what this display itself puts into a snapshot: a registration shown or let go, a
        /// geometry commit changing what a body is and reflects, a placement lookup being changed. The ledger counts
        /// its own. This remains a diagnostic counter; structural invalidation compares family revisions and
        /// registration inputs directly, so a placement change cannot invalidate unrelated families.
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
            int fragmentCapacity,
            int fragmentLimit,
            in DerivedCapacities derived,
            in DerivedCapacities reserved,
            IVpPageBacking pages,
            VpMultiCutSnapshot snapshot,
            VpMultiCutSnapshot building,
            VpCapJobClassification capJobs,
            Func<int> frameSource,
            in VpLogicalCutDisplayLimits limits)
        {
            _pages = pages;
            _storage = storage;
            _table = table;
            _ledger = ledger;
            _materials = materials;
            _shadowMaterial = shadowMaterial;
            _provisionalShadowMaterial = provisionalShadowMaterial;
            _buffers = buffers;
            _batch = batch;
            _giveUpBuffer = GiveUpBuffer;
            _stencilMaterials = stencilMaterials;
            _settings = settings;
            _snapshot = snapshot;
            _building = building;
            _structurePool = new VpMultiCutSnapshot.StructurePool(fragmentCapacity);
            _capJobs = capJobs;
            _geometries = new GeometryTable(fragmentCapacity);
            _candidateGeometries = new GeometryTable(fragmentCapacity);
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
            _capNormalBuffer = capNormals;
            for (int c = 0; c < stencilMaterials.ColorCount; c++)
            {
                Material cap = stencilMaterials.Cap(c);
                cap.SetBuffer(CapNormalsId, _capNormalBuffer);
                cap.SetBuffer(CapPlacementsId, _capNormalBuffer);
                cap.SetFloat(CapLocalId, 1f);
                cap.SetFloat(CapShadedId, 1f);
            }

            _stencilCapIndexCapacity = derived.capIndices;
            _commandMaterials = new Material[commandCapacity];
            _candidateCommandMaterials = new Material[commandCapacity];
            // The lists kept per registration are made for the room too -- there are never more registrations than
            // render fragments -- so that showing bodies up to the room makes neither of them again.
            _shown.Capacity = Math.Max(_shown.Capacity, fragmentCapacity);
            _registrations.Capacity = Math.Max(_registrations.Capacity, fragmentCapacity);

            // The numbers (TL, 2026-10-05): each array is a room on address space reserved for what the limits allow and
            // committed for the first room, every page of the committed part written here, before play. The adopted
            // side and the candidate each have their own, exchanged at adoption as the arrays were. What holds a
            // reference -- the materials above, the draw-range tables -- stays in managed arrays.
            try
            {
                _capNormals = Room<Vector4>(reserved.capVertices, derived.capVertices);
                _capLocalVertices = Room<Vector4>(reserved.capVertices, derived.capVertices);
                _candidateStencilCommands = Room<VpIndirectCommand>(reserved.stencilCommands, derived.stencilCommands);
                _candidateStencilTransforms = Room<Matrix4x4>(reserved.stencilCommands, derived.stencilCommands);
                _candidateStencilClips = Room<VpInstanceClip>(reserved.stencilCommands, derived.stencilCommands);
                _candidateCapIndices = Room<int>(reserved.capIndices, derived.capIndices);
                _commands = Room<VpIndirectCommand>(limits.commands, commandCapacity);
                _commandStarts = Room<int>(limits.commands, commandCapacity);
                _candidateCommandStarts = Room<int>(limits.commands, commandCapacity);
                _sides = Room<LogicalCutDisplaySide>(limits.instances, instanceCapacity);
                _candidateSides = Room<LogicalCutDisplaySide>(limits.instances, instanceCapacity);
                _commandProvisional = Room<bool>(limits.commands, commandCapacity);
                _candidateCommands = Room<VpIndirectCommand>(limits.commands, commandCapacity);
                _candidateCommandProvisional = Room<bool>(limits.commands, commandCapacity);
                _transforms = Room<Matrix4x4>(limits.instances, instanceCapacity);
                _clips = Room<VpInstanceClip>(limits.instances, instanceCapacity);
                _candidateTransforms = Room<Matrix4x4>(limits.instances, instanceCapacity);
                _candidateClips = Room<VpInstanceClip>(limits.instances, instanceCapacity);
                _roots = Room<LogicalFragmentId>(fragmentLimit, fragmentCapacity);
                _candidateRoots = Room<LogicalFragmentId>(fragmentLimit, fragmentCapacity);
                _rfCommandStart = Room<int>(reserved.renderFragments, derived.renderFragments);
                _rfCommandCount = Room<int>(reserved.renderFragments, derived.renderFragments);
                _rfTransform = Room<Matrix4x4>(reserved.renderFragments, derived.renderFragments);
                _candidateRfCommandStart = Room<int>(reserved.renderFragments, derived.renderFragments);
                _candidateRfCommandCount = Room<int>(reserved.renderFragments, derived.renderFragments);
                _candidateRfTransform = Room<Matrix4x4>(reserved.renderFragments, derived.renderFragments);
                _capRecords = Room<LogicalCutCapRecord>(reserved.caps, derived.caps);
                _candidateCapRecords = Room<LogicalCutCapRecord>(reserved.caps, derived.caps);
            }
            catch
            {
                // Nothing of a display that was not made is held: what was reserved before the refusal is given back.
                DisposeRooms();
                throw;
            }

            _capRecordCapacity = derived.caps;
            _preparationRecordLimit = derived.caps;
            _commandCapacity = commandCapacity;
            _instanceCapacity = instanceCapacity;
            _fragmentCapacity = fragmentCapacity;
            _fragmentLimit = fragmentLimit;
            _branchCapacity = derived.branches;
            _candidateCapacity = derived.candidates;
            _chainDepth = derived.chainDepth;
            _limits = limits;
            _frameSource = frameSource;
        }

        /// <summary>The sizes a display is made to, every one of them an int.</summary>
        private readonly IVpPageBacking _pages;

        private sealed class RoomNotMadeException : Exception
        {
            public RoomNotMadeException(string message)
                : base(message)
            {
            }
        }

        private VpNumericRoom<T> Room<T>(int reserved, int length) where T : unmanaged
        {
            if (!VpNumericRoom<T>.TryCreateNative(_pages, reserved, length, out VpNumericRoom<T> room, out string failure))
            {
                throw new RoomNotMadeException(failure);
            }

            return room;
        }

        // Every numeric room of this display, the adopted side's and the candidate's, given back once each.
        private void DisposeRooms()
        {
            _capNormals?.Dispose();
            _capLocalVertices?.Dispose();
            _candidateStencilCommands?.Dispose();
            _candidateStencilTransforms?.Dispose();
            _candidateStencilClips?.Dispose();
            _candidateCapIndices?.Dispose();
            _commands?.Dispose();
            _commandStarts?.Dispose();
            _candidateCommandStarts?.Dispose();
            _sides?.Dispose();
            _candidateSides?.Dispose();
            _commandProvisional?.Dispose();
            _candidateCommands?.Dispose();
            _candidateCommandProvisional?.Dispose();
            _transforms?.Dispose();
            _clips?.Dispose();
            _candidateTransforms?.Dispose();
            _candidateClips?.Dispose();
            _roots?.Dispose();
            _candidateRoots?.Dispose();
            _rfCommandStart?.Dispose();
            _rfCommandCount?.Dispose();
            _rfTransform?.Dispose();
            _candidateRfCommandStart?.Dispose();
            _candidateRfCommandCount?.Dispose();
            _candidateRfTransform?.Dispose();
            _capRecords?.Dispose();
            _candidateCapRecords?.Dispose();
        }

        /// <summary>Why the last <c>TryCreate</c> made no display for want of room, in words; null when it made one or refused for another reason.</summary>
        public static string LastCreationFailure { get; private set; }

        /// <summary>How long making the room took when this display was made: the reservations, the first commits, every page written, the GPU buffers made and written whole. Milliseconds.</summary>
        public double RoomPreparationMilliseconds { get; private set; }

        /// <summary>When this display's room stood (Stopwatch ticks): the end of <see cref="RoomPreparationMilliseconds"/>.</summary>
        public long RoomReadyAt { get; private set; }

        /// <summary>How long the last camera's stencil room took to make, stage and write whole. Milliseconds; 0 until a camera is registered.</summary>
        public double LastCameraRoomMilliseconds { get; private set; }

        /// <summary>When the first camera's room stood (Stopwatch ticks); 0 until then.</summary>
        public long FirstCameraRoomAt { get; private set; }

        /// <summary>When this display first registered a draw (Stopwatch ticks); 0 until then.</summary>
        public long FirstRenderAt { get; private set; }

        /// <summary>How many GPU objects were replaced by larger ones since this display was made: body batches, cap normal buffers and camera stencil batches.</summary>
        public int GpuReplacements { get; private set; }

        /// <summary>The most this display has drawn from at once since it was made, taken at each adoption: render fragments, commands, branches, candidates, caps and cap vertices.</summary>
        public int MostRenderFragments { get; private set; }

        public int MostCommands { get; private set; }

        public int MostBranches { get; private set; }

        public int MostCandidates { get; private set; }

        public int MostCaps { get; private set; }

        public int MostCapVertices { get; private set; }

        /// <summary>
        /// What this display's room is made of, in bytes: its own numeric rooms, the two snapshots', the cap job
        /// classification's and the batches' staging, with the managed arrays that hold references counted as managed.
        /// </summary>
        public VpRoomBytes RoomBytes()
        {
            var lines = new List<VpRoomLine>();
            DescribeRooms(lines);
            return VpRoomBytes.Of(lines);
        }

        /// <summary>
        /// Every array of this display's room, one line each: its own numeric rooms (the adopted side's and the
        /// candidate's), the two snapshots', the cap job classification's, the batches' staging, and the managed
        /// arrays that hold references.
        /// </summary>
        public void DescribeRooms(List<VpRoomLine> into)
        {
            into.Add(VpRoomLine.Of("display.capNormals", _capNormals));
            into.Add(VpRoomLine.Of("display.capLocalVertices", _capLocalVertices));
            into.Add(VpRoomLine.Of("display.stencilCommands", _candidateStencilCommands));
            into.Add(VpRoomLine.Of("display.stencilTransforms", _candidateStencilTransforms));
            into.Add(VpRoomLine.Of("display.stencilClips", _candidateStencilClips));
            into.Add(VpRoomLine.Of("display.capIndices", _candidateCapIndices));
            into.Add(VpRoomLine.Of("display.commands", _commands));
            into.Add(VpRoomLine.Of("display.commands'", _candidateCommands));
            into.Add(VpRoomLine.Of("display.commandStarts", _commandStarts));
            into.Add(VpRoomLine.Of("display.commandStarts'", _candidateCommandStarts));
            into.Add(VpRoomLine.Of("display.sides", _sides));
            into.Add(VpRoomLine.Of("display.sides'", _candidateSides));
            into.Add(VpRoomLine.Of("display.commandProvisional", _commandProvisional));
            into.Add(VpRoomLine.Of("display.commandProvisional'", _candidateCommandProvisional));
            into.Add(VpRoomLine.Of("display.transforms", _transforms));
            into.Add(VpRoomLine.Of("display.transforms'", _candidateTransforms));
            into.Add(VpRoomLine.Of("display.clips", _clips));
            into.Add(VpRoomLine.Of("display.clips'", _candidateClips));
            into.Add(VpRoomLine.Of("display.roots", _roots));
            into.Add(VpRoomLine.Of("display.roots'", _candidateRoots));
            into.Add(VpRoomLine.Of("display.rfCommandStart", _rfCommandStart));
            into.Add(VpRoomLine.Of("display.rfCommandStart'", _candidateRfCommandStart));
            into.Add(VpRoomLine.Of("display.rfCommandCount", _rfCommandCount));
            into.Add(VpRoomLine.Of("display.rfCommandCount'", _candidateRfCommandCount));
            into.Add(VpRoomLine.Of("display.rfTransform", _rfTransform));
            into.Add(VpRoomLine.Of("display.rfTransform'", _candidateRfTransform));
            into.Add(VpRoomLine.Of("display.capRecords", _capRecords));
            into.Add(VpRoomLine.Of("display.capRecords'", _candidateCapRecords));
            _snapshot.DescribeRooms(into, "snapshot");
            _building.DescribeRooms(into, "snapshot'");
            _capJobs.DescribeRooms(into, "capJobs");
            _batch.DescribeStaging(into, "bodyBatch");
            for (int i = 0; i < _cameraStencils.Length; i++)
            {
                if (_cameraStencils[i] != null)
                {
                    _cameraStencils[i].batch.DescribeStaging(into, "camera" + i);
                }
            }

            // References: one per command for the materials, one draw-range look per instance, and the sides' lists.
            into.Add(VpRoomLine.OfManaged("display.commandMaterials (ref)", IntPtr.Size, _commandMaterials.Length));
            into.Add(VpRoomLine.OfManaged("display.commandMaterials' (ref)", IntPtr.Size, _candidateCommandMaterials.Length));
            int geometry = Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<VpCapJobGeometry>();
            into.Add(VpRoomLine.OfManaged("display.geometries (ref)", geometry, _geometries.Capacity));
            into.Add(VpRoomLine.OfManaged("display.geometries' (ref)", geometry, _candidateGeometries.Capacity));
            into.Add(VpRoomLine.OfManaged("display.registrations (ref)", Unity.Collections.LowLevel.Unsafe.UnsafeUtility.SizeOf<VpMultiCutRegistration>(), _registrations.Capacity));
            into.Add(VpRoomLine.OfManaged("display.shown (ref)", IntPtr.Size, _shown.Capacity));
        }

        /// <summary>The bytes of the GPU buffers this display made for its room: the body's batch, the cap normals, and each registered camera's stencil batch.</summary>
        public long RoomGpuBytes
        {
            get
            {
                long bytes = _batch.GpuBytes + ((long)_capNormalBuffer.count * _capNormalBuffer.stride);
                foreach (CameraStencil slot in _cameraStencils)
                {
                    if (slot != null)
                    {
                        bytes += slot.batch.GpuBytes;
                    }
                }

                return bytes;
            }
        }

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
        /// Every size a display is made to, worked out in 64-bit arithmetic from its explicit capacities. The second
        /// one is how many render fragments there may be at once -- the instances' capacity for a caller that gives no
        /// other (a render fragment takes at least one instance, since every body has a command), and the render
        /// fragments' own room otherwise: eight caps per render fragment; fourteen vertices and twelve fanned triangles
        /// per cap. Stencil volume commands are that count times eight: a volume group is issued with one command per command
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

        /// <summary>Opt this display's owned provisional-cap materials into the shared atlas. Set once at setup;
        /// forward materials opt in separately via _VpUsePaletteAtlas. Does not collect/upload geometry or clone a
        /// material. The caller owns the bound texture pair and changes debug through VpCutSurfaceColour.</summary>
        public void SetCapPaletteAtlasEnabled(bool enabled)
        {
            ThrowIfDisposed();
            for (int c = 0; c < _stencilMaterials.ColorCount; c++)
                _stencilMaterials.Cap(c).SetFloat("_VpUsePaletteAtlas", enabled ? 1f : 0f);
        }

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
                    // answer Missing where the last one followed something. And what the last one said of the held and
                    // the fixed ones is no longer anyone's word: every one is asked once in the next pass (2026-10-08).
                    _placement = value;
                    InputChanged();
                    _holds.TellAll();
                }
            }
        }

        private IVpFragmentPlacement _placement;

        /// <summary>
        /// How many instances the last settled collection draws: per body, its command count times its render
        /// fragments.
        /// </summary>
        public int SideCount => _liveInstances;

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

            EnsureIndexView();
            return _commandProvisional[_viewCommandSlots[index]];
        }

        /// <summary>How many commands the adopted snapshot draws with: the slots of what is drawn no more are not counted.</summary>
        public int CommandCount => _commandCount;

        // ----- the draw slots, as they are adopted (DESIGN 5.6) --------------------------------------------------------

        /// <summary>
        /// One past the last command slot that is processed -- drawn through and, selecting on the GPU, selected
        /// through -- the slots of what is drawn no more included; and the same of the instance records that may be
        /// named. Both are how many were ever taken: slots are taken at the end and never again by another
        /// registration, so neither comes down while the display lives.
        /// </summary>
        public int DrawCommandEnd => _commandEnd;
        public int DrawInstanceEnd => _instanceEnd;

        /// <summary>How many instance records the adopted commands draw: per registration, its commands times its render fragments.</summary>
        public int DrawInstanceCount => _liveInstances;

        /// <summary>
        /// The draw slots a registered fragment holds in the adopted draw data: where its commands and its instance
        /// records stand, how many records each command has room for and how many it draws. False when the fragment
        /// is not registered or is drawn as nothing.
        /// </summary>
        public bool TryGetDrawSlots(
            LogicalFragmentId fragment, out int commandStart, out int commandCount, out int instanceStart, out int instanceStride,
            out int renderFragments)
        {
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                if (entry.fragment == fragment && entry.slots.held)
                {
                    commandStart = entry.slots.commandStart;
                    commandCount = entry.commands.Length;
                    instanceStart = entry.slots.instanceStart;
                    instanceStride = entry.slots.instanceStride;
                    renderFragments = entry.slots.renderFragments;
                    return true;
                }
            }

            commandStart = commandCount = instanceStart = instanceStride = renderFragments = 0;
            return false;
        }

        /// <summary>
        /// One command slot of the adopted draw data, free or not: its command (no instance when free), where its
        /// instances begin, and whether it is free. False outside [0, <see cref="DrawCommandEnd"/>).
        /// </summary>
        public bool TryGetCommandSlot(int slot, out VpIndirectCommand command, out int startInstance, out bool free)
        {
            if (slot < 0 || slot >= _commandEnd)
            {
                command = default;
                startInstance = 0;
                free = true;
                return false;
            }

            command = _commands[slot];
            startInstance = _commandStarts[slot];
            free = _commandMaterials[slot] == null;
            return true;
        }

        /// <summary>Tests only: one instance record of the adopted draw data, by its place.</summary>
        internal Matrix4x4 InstanceRecordTransformForTest(int record) => _transforms[record];
        internal VpInstanceClip InstanceRecordClipForTest(int record) => _clips[record];

        /// <summary>Tests only: where instance <paramref name="index"/> of the accessors that take an index stands.</summary>
        internal int InstanceRecordOfIndexForTest(int index)
        {
            EnsureIndexView();
            return _viewInstanceRecords[index];
        }

        // The accessors that take an index go through the registrations in their order -- per registration its
        // commands, per command its render fragments -- which is the order the draw data was packed in before the
        // slots, and say where each stands now. A registration no longer shown that is still drawn (a body a commit
        // replaced, until the next adoption) comes after them. Made when asked for, for the draw data then adopted.
        private readonly List<int> _viewCommandSlots = new List<int>(), _viewInstanceRecords = new List<int>(), _viewRenderFragments = new List<int>();
        private long _viewGeneration = -1, _viewInputs = -1;

        private void EnsureIndexView()
        {
            if (_viewGeneration == _generation && _viewInputs == _inputRevision)
            {
                return;
            }

            _viewCommandSlots.Clear();
            _viewInstanceRecords.Clear();
            _viewRenderFragments.Clear();
            for (int g = 0; g < _shown.Count; g++) AddToIndexView(_shown[g]);
            for (int g = 0; g < _retiring.Count; g++) AddToIndexView(_retiring[g]);
            for (int g = 0; g < _unshownWithSlots.Count; g++) AddToIndexView(_unshownWithSlots[g]);
            _viewGeneration = _generation;
            _viewInputs = _inputRevision;
        }

        private void AddToIndexView(Shown entry)
        {
            if (!entry.slots.held)
            {
                return;
            }

            for (int c = 0; c < entry.commands.Length; c++)
            {
                _viewCommandSlots.Add(entry.slots.commandStart + c);
                for (int k = 0; k < entry.slots.renderFragments; k++)
                {
                    _viewInstanceRecords.Add(entry.slots.instanceStart + (c * entry.slots.instanceStride) + k);
                    _viewRenderFragments.Add(entry.firstRenderFragmentShown + k);
                }
            }
        }

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
        /// (eight per render fragment), caps (eight per render fragment), cap vertices and cap indices are derived from
        /// it too, unless the limits name a room of their own for the render fragments
        /// (<see cref="VpLogicalCutDisplayLimits.firstRenderFragments"/>).
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
        /// The same, with the GPU copy's first capacities given (DESIGN 4.5.4): not the CPU reservation, which is the
        /// copy's limit (within the device's largest buffer) and not what it starts with.
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
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            VpStencilSettings stencilSettings,
            int gpuVertexInitialCapacity,
            int gpuIndexInitialCapacity,
            out VpLogicalCutDisplay display)
        {
            return TryCreateCore(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth, stencilSettings, null,
                gpuVertexInitialCapacity, gpuIndexInitialCapacity, out display);
        }

        /// <summary>
        /// The same, with how far each count may grow (<see cref="VpLogicalCutDisplayLimits"/>). The capacities given
        /// are where the display starts. False, as for any other size, when a limit is below its capacity or a size
        /// derived from the limits is not an int.
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
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            VpStencilSettings stencilSettings,
            int gpuVertexInitialCapacity,
            int gpuIndexInitialCapacity,
            VpLogicalCutDisplayLimits limits,
            out VpLogicalCutDisplay display)
        {
            return TryCreateCore(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth, stencilSettings, null,
                gpuVertexInitialCapacity, gpuIndexInitialCapacity, out display, limits);
        }

        /// <summary>
        /// The same, with the page backing the display's room is reserved on -- the one the world's storage stands on.
        /// Null is the system's own.
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
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            VpStencilSettings stencilSettings,
            int gpuVertexInitialCapacity,
            int gpuIndexInitialCapacity,
            VpLogicalCutDisplayLimits limits,
            IVpPageBacking pages,
            out VpLogicalCutDisplay display)
        {
            return TryCreateCore(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth, stencilSettings, null,
                gpuVertexInitialCapacity, gpuIndexInitialCapacity, out display, limits, pages);
        }

        /// <summary>
        /// The same, drawing the body through the GPU selection of DESIGN 4.5.7 when <paramref name="culling"/> is given:
        /// the body batch is made to select, and the surface and caster materials are replaced by copies of them with
        /// the variant that reads the selection's list switched on, owned and destroyed by the display. The caps and
        /// the stencil work are drawn as they always are. False, with <see cref="LastCreationFailure"/>, when the
        /// setup has fewer views than the cameras the stencil settings allow, or a material's shader has no such
        /// variant; nothing is made then, and the caller may make the display without it. Null is the display as it
        /// always was.
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
            int branchCapacity,
            int candidateCapacity,
            int chainDepth,
            VpStencilSettings stencilSettings,
            int gpuVertexInitialCapacity,
            int gpuIndexInitialCapacity,
            VpLogicalCutDisplayLimits limits,
            IVpPageBacking pages,
            VpGpuCullSetup culling,
            out VpLogicalCutDisplay display)
        {
            return TryCreateCore(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth, stencilSettings, null,
                gpuVertexInitialCapacity, gpuIndexInitialCapacity, out display, limits, pages, culling);
        }

        // The materials of a display that selects on the GPU: one copy per material given, the same copy wherever the
        // same material was given (so commands still group by material), with the variant switched on. Every copy
        // made is put in `owned`, whatever the outcome, for the caller to destroy or to hand to the display.
        private static bool TryMakeCulledMaterials(
            IReadOnlyDictionary<int, Material> given, ref Material shadowMaterial, ref Material provisionalShadowMaterial,
            List<Material> owned, out Dictionary<int, Material> culled, out string failure)
        {
            culled = new Dictionary<int, Material>(given.Count);
            failure = null;
            var copies = new Dictionary<Material, Material>(given.Count);
            foreach (KeyValuePair<int, Material> entry in given)
            {
                if (entry.Value == null)
                {
                    culled[entry.Key] = null;
                    continue;
                }

                if (!TryCulledCopy(entry.Value, copies, owned, out Material copy, out failure))
                {
                    return false;
                }

                culled[entry.Key] = copy;
            }

            if (shadowMaterial != null && !TryCulledCopy(shadowMaterial, copies, owned, out shadowMaterial, out failure))
            {
                return false;
            }

            return provisionalShadowMaterial == null
                || TryCulledCopy(provisionalShadowMaterial, copies, owned, out provisionalShadowMaterial, out failure);
        }

        private static bool TryCulledCopy(
            Material source, Dictionary<Material, Material> copies, List<Material> owned, out Material copy, out string failure)
        {
            failure = null;
            if (copies.TryGetValue(source, out copy))
            {
                return true;
            }

            if (source.shader == null || !source.shader.keywordSpace.FindKeyword(VpGpuCullSetup.Keyword).isValid)
            {
                failure = "the material '" + source.name + "' has no variant that reads the GPU selection ("
                          + VpGpuCullSetup.Keyword + ")";
                return false;
            }

            copy = new Material(source) { name = source.name + " (GPU culled)" };
            copy.EnableKeyword(VpGpuCullSetup.Keyword);
            owned.Add(copy);
            copies[source] = copy;
            return true;
        }

        private static void DestroyMaterials(List<Material> materials)
        {
            if (materials == null)
            {
                return;
            }

            for (int i = 0; i < materials.Count; i++)
            {
                if (materials[i] == null)
                {
                    continue;
                }

                if (Application.isPlaying)
                {
                    UnityEngine.Object.Destroy(materials[i]);
                }
                else
                {
                    UnityEngine.Object.DestroyImmediate(materials[i]);
                }
            }

            materials.Clear();
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
            // Without a GPU first capacity of its own, the copy starts at what the storage committed first -- for the
            // fixed-size storage, its whole capacity, as it always did.
            return TryCreateCore(
                storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial,
                commandCapacity, instanceCapacity, branchCapacity, candidateCapacity, chainDepth, stencilSettings, frameSource,
                storage != null ? storage.CommittedVertexCapacity : 0, storage != null ? storage.CommittedIndexCapacity : 0,
                out display);
        }

        internal static bool TryCreateCore(
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
            int gpuVertexInitialCapacity,
            int gpuIndexInitialCapacity,
            out VpLogicalCutDisplay display,
            VpLogicalCutDisplayLimits limits = default,
            IVpPageBacking pages = null,
            VpGpuCullSetup culling = null)
        {
            display = null;
            LastCreationFailure = null;
            if (storage == null || table == null || ledger == null || materialsBySourceIndex == null)
            {
                return false;
            }

            pages = pages ?? VpWindowsPageBacking.Instance;

            // No limits means every count stays where it starts.
            if (limits.IsDefault)
            {
                limits = new VpLogicalCutDisplayLimits(commandCapacity, instanceCapacity, branchCapacity, candidateCapacity);
            }

            // The render fragments' own room (DESIGN 5.6): given apart from the instances', or following them.
            int fragmentCapacity = limits.firstRenderFragments > 0 ? limits.firstRenderFragments : instanceCapacity;
            int fragmentLimit = limits.renderFragments > 0 ? limits.renderFragments : limits.instances;

            // Every size the limits give must be an int too, so that no growth can come to one that is not.
            if (limits.commands < commandCapacity || limits.instances < instanceCapacity || limits.branches < branchCapacity
                || limits.candidates < candidateCapacity || limits.firstRenderFragments < 0 || limits.renderFragments < 0
                || fragmentLimit < fragmentCapacity
                || !TryDeriveCapacities(
                    limits.commands, fragmentLimit, limits.branches, limits.candidates, chainDepth,
                    out DerivedCapacities reserved))
            {
                return false;
            }

            // The GPU copy's limit: the CPU reservation, and never past the device's largest buffer.
            long deviceBytes = SystemInfo.maxGraphicsBufferSize;
            int gpuVertexLimit = (int)Math.Min(storage.VertexCapacity, deviceBytes / VpRenderVertex.Stride);
            int gpuIndexLimit = (int)Math.Min(storage.IndexCapacity, deviceBytes / VpGpuIndexedGeometryBuffers.IndexStride);
            if (gpuVertexInitialCapacity <= 0 || gpuIndexInitialCapacity <= 0
                || gpuVertexInitialCapacity > gpuVertexLimit || gpuIndexInitialCapacity > gpuIndexLimit)
            {
                return false;
            }

            // Every size is worked out wide and must be an int, before any GPU buffer, material or scratch is made.
            if (instanceCapacity <= 0
                || !TryDeriveCapacities(
                    commandCapacity, fragmentCapacity, branchCapacity, candidateCapacity, chainDepth,
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

            // Selecting on the GPU: a view per camera slot, and the materials' copies. Nothing else is made before
            // these are known to stand.
            List<Material> ownedMaterials = null;
            if (culling != null)
            {
                if (culling.ViewCapacity < stencilSettings.cameraCapacity)
                {
                    LastCreationFailure = "the GPU selection has " + culling.ViewCapacity + " views for "
                                          + stencilSettings.cameraCapacity + " cameras";
                    return false;
                }

                ownedMaterials = new List<Material>(materialsBySourceIndex.Count + 2);
                if (!TryMakeCulledMaterials(
                        materialsBySourceIndex, ref shadowMaterial, ref provisionalShadowMaterial, ownedMaterials,
                        out Dictionary<int, Material> culledMaterials, out string materialFailure))
                {
                    DestroyMaterials(ownedMaterials);
                    LastCreationFailure = "the GPU selection cannot be drawn: " + materialFailure;
                    return false;
                }

                materialsBySourceIndex = culledMaterials;
            }

            VpGpuIndexedGeometryBuffers buffers = null;
            VpIndexedIndirectDrawBatch batch = null;
            VpStencilCapMaterials stencilMaterials = null;
            GraphicsBuffer capNormals = null;
            VpMultiCutSnapshot snapshot = null;
            VpMultiCutSnapshot building = null;
            VpCapJobClassification capJobs = null;
            bool taken = false;
            long preparationBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // The room: reserved for what the limits allow, committed for the first room and written, before play.
                VpMultiCutCapacities snapshotCapacities = derived.Snapshot;
                VpMultiCutCapacities snapshotReserve = reserved.Snapshot;
                if (!VpMultiCutSnapshot.TryCreateOnBacking(pages, snapshotCapacities, snapshotReserve, out snapshot, out string roomFailure)
                    || !VpMultiCutSnapshot.TryCreateOnBacking(pages, snapshotCapacities, snapshotReserve, out building, out roomFailure)
                    || !VpCapJobClassification.TryCreateOnBacking(pages, snapshotCapacities, snapshotReserve, out capJobs, out roomFailure))
                {
                    LastCreationFailure = "the display's room could not be made: " + roomFailure;
                    return false;
                }

                buffers = new VpGpuIndexedGeometryBuffers(gpuVertexInitialCapacity, gpuIndexInitialCapacity, gpuVertexLimit, gpuIndexLimit);
                try
                {
                    batch = new VpIndexedIndirectDrawBatch(commandCapacity, instanceCapacity, pages, culling);
                }
                catch (InvalidOperationException exception)
                {
                    LastCreationFailure = "the display's room could not be made: " + exception.Message;
                    return false;
                }

                // Written whole before play. A write that cannot be made is room that could not be established: no
                // display is made, and everything made so far is given back below.
                try
                {
                    batch.WriteWholeOnce();
                }
                catch (Exception exception)
                {
                    LastCreationFailure = "the display's room could not be made: the body's buffers could not be written whole: " + exception.Message;
                    return false;
                }

                // One material set for every camera; the stencil batches themselves come with the cameras.
                if (!VpStencilCapMaterials.TryCreate(stencilSettings.maxStencilColors, out stencilMaterials))
                {
                    return false;
                }

                // DESIGN 5.3: one outward normal per cap vertex for the cap pass to shade with. Made here, beside the
                // other GPU resources, so that anything failing before the display takes them gives this back too.
                capNormals = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured, Math.Max(1, derived.capVertices), sizeof(float) * 4);
                try
                {
                    WriteWholeOnce(capNormals);
                }
                catch (Exception exception)
                {
                    LastCreationFailure = "the display's room could not be made: the cap normals could not be written whole: " + exception.Message;
                    return false;
                }

                try
                {
                    display = new VpLogicalCutDisplay(
                        storage, table, ledger, materialsBySourceIndex, shadowMaterial, provisionalShadowMaterial, buffers,
                        batch, stencilMaterials, capNormals, stencilSettings, commandCapacity, instanceCapacity,
                        fragmentCapacity, fragmentLimit, derived, reserved, pages, snapshot, building, capJobs, frameSource,
                        limits);
                }
                catch (RoomNotMadeException exception)
                {
                    LastCreationFailure = "the display's room could not be made: " + exception.Message;
                    return false;
                }

                display._cull = culling;
                display._ownedMaterials = ownedMaterials;
                display.RoomReadyAt = System.Diagnostics.Stopwatch.GetTimestamp();
                display.RoomPreparationMilliseconds = (display.RoomReadyAt - preparationBegin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                taken = true;
                return true;
            }
            finally
            {
                if (!taken)
                {
                    DestroyMaterials(ownedMaterials);
                    capNormals?.Dispose();
                    stencilMaterials?.Dispose();
                    batch?.Dispose();
                    buffers?.Dispose();
                    capJobs?.Dispose();
                    building?.Dispose();
                    snapshot?.Dispose();
                }
            }
        }

        // The cap normals' buffer written whole once with zeros, so that it stands on the device before play.
        private static void WriteWholeOnce(GraphicsBuffer normals)
        {
            NativeArray<Vector4> zeros = default;
            try
            {
                zeros = VpWholeWrite.Zeros<Vector4>("cap normals", normals.count);
                VpWholeWrite.Step("write cap normals");
                normals.SetData(zeros, 0, 0, normals.count);
            }
            finally
            {
                VpWholeWrite.Release(ref zeros);
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

                // The camera's GPU room is made here, at its registration, and written whole once: it stands before
                // the first frame this camera is prepared in. Making it, writing it and handing it to the camera's slot
                // are one protection: a batch that was not handed over -- its staging refused, a buffer that could not
                // be made or written -- is given back here, and the camera is not registered.
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                VpStencilCapBatch batch = null;
                bool handedOver = false;
                try
                {
                    batch = new VpStencilCapBatch(
                        _settings.maxStencilColors, _stencilCommandCapacity, _stencilCommandCapacity,
                        _stencilCapVertexCapacity, _stencilCapIndexCapacity, _pages);
                    batch.WriteWholeOnce();
                    _cameraStencils[i] = new CameraStencil { camera = camera, batch = batch, view = i };
                    handedOver = true;
                }
                catch (Exception exception)
                {
                    LastRoomFailure = "a camera's stencil room could not be made: " + exception.Message;
                    return false;
                }
                finally
                {
                    if (!handedOver)
                    {
                        batch?.Dispose();
                    }
                }

                LastCameraRoomMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (FirstCameraRoomAt == 0)
                {
                    FirstCameraRoomAt = System.Diagnostics.Stopwatch.GetTimestamp();
                }

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
                    if (_capLayoutStale)
                    {
                        LayOutLocalCapVertices();   // an adopted snapshot never laid out (the first, or a room made larger)
                    }

                    long sentBefore = slot.batch.CapLayoutUploads, verticesBefore = slot.batch.CapLayoutVertices;
                    if (!slot.batch.TryUploadLocalCaps(
                            _candidateStencilCommands.Valid, commands, _candidateStencilTransforms.Valid, _candidateStencilClips.Valid,
                            _capLocalVertices.Valid, _snapshot.CapVertexCount, _capLayout, _candidateCapIndices.Valid, capIndices,
                            _candidateStencilColors, colours, _batch.SinglePassInstanced))
                    {
                        _broken = true;
                        throw new InvalidOperationException(
                            "a camera's stencil batch refused an arrangement inside the capacity checked when the "
                            + "snapshot was adopted; this display stops");
                    }

                    // Observation: what this camera was sent of the cap vertices just now, and whether it was its first.
                    long sent = slot.batch.CapLayoutUploads - sentBefore;
                    if (sent > 0)
                    {
                        long vertices = slot.batch.CapLayoutVertices - verticesBefore;
                        CameraCapLayoutUploads += sent;
                        CameraCapLayoutVertices += vertices;
                        if (sentBefore == 0)
                        {
                            CameraCapLayoutFirstUploads += sent;
                            CameraCapLayoutFirstVertices += vertices;
                        }
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
        public long StructureBuilds => _snapshot.StructureBuilds + _building.StructureBuilds + _retiredStructureBuilds;

        private long _retiredStructureBuilds, _retiredStructureValidations, _retiredPlacementPasses;

        /// <summary>How often the structural checks over the ledger have run. Rises with <see cref="StructureBuilds"/>.</summary>
        public long StructureValidations => _snapshot.StructureValidations + _building.StructureValidations + _retiredStructureValidations;

        /// <summary>How often placements and what they decide have been settled, by either route.</summary>
        public long PlacementPasses => _snapshot.PlacementPasses + _building.PlacementPasses + _retiredPlacementPasses;

        /// <summary>How often the snapshot being built was made again for a larger room (its counts kept, above).</summary>
        public int SnapshotRegrowths { get; private set; }

        /// <summary>
        /// What the collections of one frame did, counted where they ran (2026-09-30, for observation): the counters
        /// above read before and after each collection, the difference given to the frame the collection ran in
        /// (<see cref="Time.frameCount"/>); a room's growth and a snapshot made again counted in the frame they happen.
        /// A frame's markers and these counts then belong to the same frame, wherever in the frame an observer reads them.
        /// </summary>
        /// <summary>
        /// What the collections of a frame kept of the static data (TL, 2026-10-08): placement passes a step alone did
        /// not make, every target being fixed or held and far; the structure tally (the room asked for the
        /// registrations) run or kept from the structure it was last run for; the instance takes and the release pass
        /// over the registrations run or kept likewise; and the runs' time. Every one added over the frame.
        /// </summary>
        public struct VpStaticCollectCounts
        {
            public long passesSkipped;
            public long tallies, talliesSkipped, instanceTakes, instanceTakesSkipped, releases, releasesSkipped;
            public double tallySeconds, instanceTakeSeconds, releaseSeconds;
        }

        public struct FrameCounts
        {
            public int frame, collections, roomGrowths, snapshotRegrowths;
            public long structureBuilds, structureValidations, placementPasses;

            /// <summary>The static data kept by this frame's collections (2026-10-08).</summary>
            public VpStaticCollectCounts statics;

            /// <summary>The validations' parts in this frame, every one of them added (2026-10-01); null in a frame that validated nothing.</summary>
            public VpValidateCounts validate;

            /// <summary>The Place passes in this frame, the structural builds' and the placement-only ones' apart (2026-10-01); null as above.</summary>
            public VpPlaceCounts placeStructural, placePlacementOnly;

            /// <summary>What this frame's collections did to the body's draw data, and where it stood afterwards (2026-10-06).</summary>
            public VpDrawDataCounts draw;

            /// <summary>The snapshot builds of this frame by stage, and the collections' own time whole and by stage, every one added (2026-10-07).</summary>
            public VpSnapshotStageTotals stages;
            public VpCollectTimes times;

            /// <summary>What the held placements did in this frame's collections, every one added (2026-10-07, D-205).</summary>
            public VpHeldPlacementTotals holds;
        }

        /// <summary>
        /// Observation, since this display was made: commands written into a side being built (a command zeroed
        /// because it draws no more is one) and commands that side was first given from the adopted one; registrations
        /// whose commands were written; instance regions taken anew (a registration first drawn, or one whose records
        /// could not be lengthened where they stood); and, of those, the regions that now stand elsewhere, with the
        /// instance records that stood in them. A command slot never moves.
        /// </summary>
        public long CommandRecordsWritten { get; private set; }
        public long CommandRecordsCaughtUp { get; private set; }
        public long RegistrationsWritten { get; private set; }
        public long RegionsTaken { get; private set; }
        public long RegionsMoved { get; private set; }
        public long InstanceRecordsMoved { get; private set; }

        /// <summary>Observation: what the body's batches sent whole -- a first upload, a batch that took a smaller one's place, another stereo condition.</summary>
        public long BodyWholeArgumentElementsTransferred => _pastWholeArguments + _batch.WholeArgumentElementsTransferred;
        public long BodyWholeInstanceElementsTransferred => _pastWholeInstances + _batch.WholeInstanceElementsTransferred;
        public long BodyWholeSetDataCalls => _pastWholeCalls + _batch.WholeSetDataCalls;
        private long _pastWholeArguments, _pastWholeInstances, _pastWholeCalls;

        // The cumulative counters a collection's share of VpDrawDataCounts is the difference of.
        private VpDrawDataCounts DrawTotals()
        {
            return new VpDrawDataCounts
            {
                commandsWritten = CommandRecordsWritten, commandsCaughtUp = CommandRecordsCaughtUp,
                instancesWritten = InstanceRecordsWritten, instancesCaughtUp = InstanceRecordsCaughtUp,
                registrationsWritten = RegistrationsWritten, regionsTaken = RegionsTaken,
                regionsMoved = RegionsMoved, instancesMoved = InstanceRecordsMoved,
                argumentElements = BodyArgumentElementsTransferred, argumentCalls = BodyArgumentSetDataCalls,
                instanceElements = BodyInstanceElementsTransferred, instanceCalls = BodyInstanceSetDataCalls,
                transformCalls = BodyInstanceTransformSetDataCalls, clipCalls = BodyInstanceClipSetDataCalls,
                wholeArgumentElements = BodyWholeArgumentElementsTransferred, wholeInstanceElements = BodyWholeInstanceElementsTransferred,
                wholeCalls = BodyWholeSetDataCalls,
            };
        }

        // This collection's share, added to its frame's; where the draw data stands now replaces what an earlier
        // collection of the frame said.
        private void CountDraw(in VpDrawDataCounts before)
        {
            VpDrawDataCounts now = DrawTotals();
            CountingFrame();
            ref VpDrawDataCounts into = ref _countsNow.draw;
            into.known = true;
            into.commandsWritten += now.commandsWritten - before.commandsWritten;
            into.commandsCaughtUp += now.commandsCaughtUp - before.commandsCaughtUp;
            into.instancesWritten += now.instancesWritten - before.instancesWritten;
            into.instancesCaughtUp += now.instancesCaughtUp - before.instancesCaughtUp;
            into.registrationsWritten += now.registrationsWritten - before.registrationsWritten;
            into.regionsTaken += now.regionsTaken - before.regionsTaken;
            into.regionsMoved += now.regionsMoved - before.regionsMoved;
            into.instancesMoved += now.instancesMoved - before.instancesMoved;
            into.argumentElements += now.argumentElements - before.argumentElements;
            into.argumentCalls += now.argumentCalls - before.argumentCalls;
            into.instanceElements += now.instanceElements - before.instanceElements;
            into.instanceCalls += now.instanceCalls - before.instanceCalls;
            into.transformCalls += now.transformCalls - before.transformCalls;
            into.clipCalls += now.clipCalls - before.clipCalls;
            into.wholeArgumentElements += now.wholeArgumentElements - before.wholeArgumentElements;
            into.wholeInstanceElements += now.wholeInstanceElements - before.wholeInstanceElements;
            into.wholeCalls += now.wholeCalls - before.wholeCalls;

            // Where the adopted draw data stands: what is processed, what of it draws, the room, and the holes.
            into.commandEnd = _commandEnd;
            into.commandsLive = _commandCount;
            into.instanceEnd = _instanceEnd;
            into.instancesLive = _liveInstances;
            into.commandCapacity = _commandCapacity;
            into.instanceCapacity = _instanceCapacity;
        }

        // The validations' parts of the snapshots made again (their sums kept, as the counters above), and two sums to take a collection's difference.
        private readonly VpValidateCounts _retiredValidate = new VpValidateCounts(), _validateBefore = new VpValidateCounts(), _validateAfter = new VpValidateCounts();

        private void SumValidate(VpValidateCounts into)
        {
            into.CopyFrom(_snapshot.ValidateCounts);
            if (!ReferenceEquals(_building, _snapshot)) into.Add(_building.ValidateCounts);
            into.Add(_retiredValidate);
        }

        // The Place passes' records likewise, the structural and the placement-only ones apart.
        private readonly VpPlaceCounts _retiredPlaceStructural = new VpPlaceCounts(), _retiredPlacePlacementOnly = new VpPlaceCounts();
        private readonly VpPlaceCounts _placeStructuralBefore = new VpPlaceCounts(), _placeStructuralAfter = new VpPlaceCounts();
        private readonly VpPlaceCounts _placePlacementOnlyBefore = new VpPlaceCounts(), _placePlacementOnlyAfter = new VpPlaceCounts();

        private void SumPlace(VpPlaceCounts structural, VpPlaceCounts placementOnly)
        {
            structural.CopyFrom(_snapshot.StructuralPlaceCounts);
            placementOnly.CopyFrom(_snapshot.PlacementOnlyPlaceCounts);
            if (!ReferenceEquals(_building, _snapshot))
            {
                structural.Add(_building.StructuralPlaceCounts);
                placementOnly.Add(_building.PlacementOnlyPlaceCounts);
            }

            structural.Add(_retiredPlaceStructural);
            placementOnly.Add(_retiredPlacePlacementOnly);
        }

        // The last two frames that collected, the later one still counting while its frame runs.
        private FrameCounts _countsNow = new FrameCounts { frame = int.MinValue }, _countsBefore = new FrameCounts { frame = int.MinValue };

        /// <summary>
        /// The collections of <paramref name="frame"/>: false when that frame is older than the two last collecting
        /// frames kept (unknown), true with zeros when it collected nothing. Read it in a later frame for a whole frame.
        /// </summary>
        public bool TryGetFrameCounts(int frame, out FrameCounts counts)
        {
            if (frame == _countsNow.frame) { counts = _countsNow; return true; }
            if (frame == _countsBefore.frame) { counts = _countsBefore; return true; }
            counts = new FrameCounts { frame = frame };
            return frame > _countsBefore.frame;
        }

        // This frame's counts, the frame before kept when this is a new one.
        private void CountingFrame()
        {
            int frame = Time.frameCount;
            if (_countsNow.frame != frame)
            {
                VpValidateCounts reuse = _countsBefore.validate;   // the frame two back: its counts are no longer asked for
                VpPlaceCounts reuseStructural = _countsBefore.placeStructural, reusePlacementOnly = _countsBefore.placePlacementOnly;
                _countsBefore = _countsNow;
                _countsNow = new FrameCounts
                {
                    frame = frame, validate = reuse ?? new VpValidateCounts(),
                    placeStructural = reuseStructural ?? new VpPlaceCounts(), placePlacementOnly = reusePlacementOnly ?? new VpPlaceCounts(),
                };
                _countsNow.validate.Clear();
                _countsNow.placeStructural.Clear();
                _countsNow.placePlacementOnly.Clear();
            }
        }

        /// <summary>Tests only: the Place passes' counts since this display was made, the structural ones' and the placement-only ones' (copies).</summary>
        internal void PlaceTotalsForTest(VpPlaceCounts structural, VpPlaceCounts placementOnly) => SumPlace(structural, placementOnly);

        /// <summary>Tests only: the snapshots' stages since this display was made.</summary>
        internal VpSnapshotStageTotals StageTotalsForTest => SumStages();

        private void CountCollection(long builds, long validations, long placements)
        {
            CountingFrame();
            _countsNow.collections++;
            _countsNow.structureBuilds += builds;
            _countsNow.structureValidations += validations;
            _countsNow.placementPasses += placements;
        }

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
                        _snapshot.TryGetCap(job.capIndex, out VpMultiCutCap drawn);
                        AppendFan(_capRecords[job.capIndex], drawn.mirrored, ref capIndexCount);
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
        /// <para>
        /// What the room and the buffer hold is the adopted snapshot's normals, vertex for vertex. Only the one range
        /// of caps the snapshot's placement pass recorded as changed beside the adopted one (where each cap is made:
        /// <see cref="VpMultiCutSnapshot.ChangedCaps"/>) has its normals made again and sent -- the vertices from the
        /// first of those caps to the end of the last, unchanged caps between them included. No changed cap: nothing is
        /// made and nothing is sent. <paramref name="everything"/> -- a buffer that was never written to, or the test
        /// switch -- makes and sends every cap's.
        /// </para>
        /// </summary>
        private void UploadCapNormals(VpMultiCutSnapshot snapshot, GraphicsBuffer into, bool everything)
        {
            // A record a cap (D-208): the three rows of its render fragment's placement and its outward normal. The cap's
            // vertices are the structure's, in the local frame, and are not touched here.
            int caps = snapshot.CapCount;
            int elements = caps * CapPlacementStride;
            if (elements > _capNormals.Length || elements > into.count)
            {
                throw new InvalidOperationException("the snapshot holds more caps than this display's room");
            }

            int from = 0, to = caps;
            if (!everything)
            {
                snapshot.ChangedCaps(_snapshot, out from, out to);
            }

            for (int c = from; c < to; c++)
            {
                snapshot.TryGetCap(c, out VpMultiCutCap cap);
                snapshot.TryGetRenderFragment(cap.renderFragment, out VpMultiCutRenderFragment placed);
                Matrix4x4 m = placed.geometryLocalToWorld;
                Vector3 outward = cap.outwardNormal;
                int at = c * CapPlacementStride;
                _capNormals[at] = new Vector4(m.m00, m.m01, m.m02, m.m03);
                _capNormals[at + 1] = new Vector4(m.m10, m.m11, m.m12, m.m13);
                _capNormals[at + 2] = new Vector4(m.m20, m.m21, m.m22, m.m23);
                _capNormals[at + 3] = new Vector4(outward.x, outward.y, outward.z, 0f);
            }

            if (to > from)
            {
                int first = from * CapPlacementStride, count = (to - from) * CapPlacementStride;
                into.SetData(_capNormals.First(elements), first, first, count);
                CapNormalTransfers++;
                CapNormalVerticesTransferred += count;
                CapNormalsMade += count;
            }

            _capNormalCount = elements;
        }

        // The adopted snapshot's cap vertices, laid out in the caps' order in their geometry's local frame with the cap's
        // number in w: copied from the structure's kept shapes, placed nowhere. Done when a collection that settled a
        // structure is adopted (the layout follows from the structure alone), and never for a placement.
        private void LayOutLocalCapVertices()
        {
            int caps = _snapshot.CapCount, laid = 0;
            for (int c = 0; c < caps; c++)
            {
                _snapshot.TryGetCap(c, out VpMultiCutCap cap);
                ReadOnlySpan<Vector3> local = _snapshot.LocalCapPolygon(c);
                for (int v = 0; v < local.Length; v++)
                {
                    Vector3 p = local[v];
                    _capLocalVertices[cap.vertexStart + v] = new Vector4(p.x, p.y, p.z, c);
                }

                laid += local.Length;
            }

            _capLayout++;
            _capLayoutStale = false;
            CapLayouts++;
            CapLayoutVertices += laid;
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
            VpNumericRoom<VpIndirectCommand> commands,
            VpNumericRoom<int> commandStart,
            VpNumericRoom<int> commandCountOf,
            VpNumericRoom<Matrix4x4> transforms,
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
        /// <remarks>
        /// The kept local order is wound about the outward normal under a placement that does not mirror; under one
        /// that does, the triangles are named the other way round -- the shape is not made again (D-208).
        /// </remarks>
        private void AppendFan(in LogicalCutCapRecord record, bool mirrored, ref int capIndexCount)
        {
            for (int v = 1; v + 1 < record.vertexCount; v++)
            {
                _candidateCapIndices[capIndexCount++] = record.vertexStart;
                _candidateCapIndices[capIndexCount++] = record.vertexStart + (mirrored ? v + 1 : v);
                _candidateCapIndices[capIndexCount++] = record.vertexStart + (mirrored ? v : v + 1);
            }
        }

        // ----- bodies ----------------------------------------------------------------------------------------------

        /// <summary>
        /// Shows <paramref name="geometry"/> as the live fragment <paramref name="fragment"/> under the caller contract of
        /// this spelling: the whole lineage is in the geometry's own frame (the identity mapping) and the geometry
        /// reflects no boundary. It is the other <c>TryShow</c> with those two stated, nothing more.
        /// </summary>
        /// <summary>The GPU copy's room, in words. Log text only.</summary>
        public string DescribeGpuRoom() => _buffers.DescribeRoom();

        /// <summary>Diagnosis only (a lifetime test or drill): the GPU copy's current vertex buffer, to see whether it is still valid after the display gave it up.</summary>
        public GraphicsBuffer GpuVertexBufferForDiagnosis => _buffers.VertexBuffer;

        /// <summary>The GPU copy's current capacities and how often it has grown, for observation.</summary>
        public int GpuVertexCapacity => _buffers.VertexCapacity;

        public int GpuIndexCapacity => _buffers.IndexCapacity;

        public int GpuGrowthCount => _buffers.GrowthCount;

        /// <summary>
        /// The GPU copy's figures (2026-10-01): each buffer's stride and bytes, the time its first buffers took, how often the
        /// vertex and the index buffer each grew, the most replaced buffers awaiting release at once and now, and the
        /// furthest a transfer asked into each (the range used).
        /// </summary>
        public int GpuVertexStride => VpRenderVertex.Stride;
        public int GpuIndexStride => VpGpuIndexedGeometryBuffers.IndexStride;
        public long GpuVertexBytes => (long)_buffers.VertexCapacity * VpRenderVertex.Stride;
        public long GpuIndexBytes => (long)_buffers.IndexCapacity * VpGpuIndexedGeometryBuffers.IndexStride;
        public double GpuCreationSeconds => _buffers.CreationSeconds;
        public int GpuVertexGrowthCount => _buffers.VertexGrowthCount;
        public int GpuIndexGrowthCount => _buffers.IndexGrowthCount;
        public int GpuMaxRetired => _buffers.MaxRetiredCount;
        public int GpuRetiredNow => _buffers.RetiredCount;
        public int GpuVertexHighWater { get; private set; }
        public int GpuIndexHighWater { get; private set; }

        /// <summary>The GPU copy itself, for a test that reads back what was transferred.</summary>
        internal VpGpuIndexedGeometryBuffers Buffers => _buffers;

        /// <summary>
        /// Told once, on the main thread, when the GPU copy cannot be given the room a transfer needs -- past its limit,
        /// or a buffer that cannot be made (DESIGN 4.5.4). The owner turns that into the common termination; the
        /// transfer's registration is refused.
        /// </summary>
        public Action<string> BackingFailureHandler { get; set; }

        private bool _gpuBackingFailed;

        /// <summary>
        /// Makes the GPU copy hold vertices [0, <paramref name="vertexEnd"/>) and indices [0, <paramref name="indexEnd"/>)
        /// before a transfer into them. When a buffer is replaced, everything the reference table holds is transferred
        /// into the new one from the CPU copy -- each live geometry's vertex blocks (merged where they touch, so that
        /// nothing unpublished is read) and its index range -- before this returns, and so before anything is drawn
        /// from it. False when the room cannot be made; the handler has then been told.
        /// </summary>
        private bool TryMakeGpuRoom(int vertexEnd, int indexEnd)
        {
            GpuVertexHighWater = Math.Max(GpuVertexHighWater, vertexEnd);
            GpuIndexHighWater = Math.Max(GpuIndexHighWater, indexEnd);
            _buffers.ReleaseRetired(_giveUpBuffer);
            if (vertexEnd <= _buffers.VertexCapacity && indexEnd <= _buffers.IndexCapacity)
            {
                return true;
            }

            if (_gpuBackingFailed)
            {
                return false;
            }

            if (!_buffers.TryGrow(vertexEnd, indexEnd, out bool vertexGrew, out bool indexGrew, out string failure))
            {
                FailGpuBacking(failure);
                return false;
            }

            if (!TryTransferDrawnAgain(vertexGrew, indexGrew))
            {
                FailGpuBacking("what is drawn could not be transferred into the grown GPU copy");
                return false;
            }

            return true;
        }

        private readonly List<(int start, int count)> _regrowRuns = new List<(int start, int count)>();

        private bool TryTransferDrawnAgain(bool vertices, bool indices)
        {
            _regrowRuns.Clear();
            for (int slot = 0; slot < _table.GeometryCapacity; slot++)
            {
                if (!_table.TryGetLiveGeometryAt(slot, out VpStoredGeometry geometry))
                {
                    continue;
                }

                if (indices && !VpStoredGeometryTransfer.TryUploadPublishedIndices(_storage, _buffers.IndexBuffer, geometry.indexRange, out _))
                {
                    return false;
                }

                if (vertices)
                {
                    if (!_storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _))
                    {
                        return false;
                    }

                    for (int b = 0; b < blocks.Length; b++)
                    {
                        _regrowRuns.Add((blocks[b].vertexStart, blocks[b].vertexCount));
                    }
                }
            }

            if (!vertices || _regrowRuns.Count == 0)
            {
                return true;
            }

            // Blocks shared between geometries are transferred once: sorted, and runs that overlap or touch merged --
            // every vertex in a merged run belongs to one of them, and all of them are published.
            _regrowRuns.Sort((a, b) => a.start.CompareTo(b.start));
            int runStart = _regrowRuns[0].start;
            int runEnd = runStart + _regrowRuns[0].count;
            for (int i = 1; i <= _regrowRuns.Count; i++)
            {
                if (i < _regrowRuns.Count && _regrowRuns[i].start <= runEnd)
                {
                    runEnd = Math.Max(runEnd, _regrowRuns[i].start + _regrowRuns[i].count);
                    continue;
                }

                if (runEnd > runStart
                    && !VpStoredGeometryTransfer.TryUploadCommittedVertices(_storage, _buffers.VertexBuffer, runStart, runEnd - runStart, out _))
                {
                    return false;
                }

                if (i < _regrowRuns.Count)
                {
                    runStart = _regrowRuns[i].start;
                    runEnd = runStart + _regrowRuns[i].count;
                }
            }

            return true;
        }

        private void FailGpuBacking(string failure)
        {
            if (_gpuBackingFailed)
            {
                return;
            }

            _gpuBackingFailed = true;
            BackingFailureHandler?.Invoke("the GPU copy could not be given room: " + failure + " (" + _buffers.DescribeRoom() + ")");
        }

        private bool TryIndexEnd(VpIndexRangeHandle range, out int end)
        {
            end = 0;
            if (!_storage.TryGetIndexState(range, out _, out int start, out int count))
            {
                return false;
            }

            end = start + count;
            return true;
        }

        /// <summary>Tests only: called in TryShow just before the table registration, after everything that can throw was made.</summary>
        internal static Action showBeforeRegistrationHookForTest;

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
        /// <remarks>
        /// An exception from this call (an argument, a disposed, broken or preparing display, a failed GPU transfer --
        /// which also marks the display broken -- or anything before the registration) leaves the geometry with no
        /// registration in the table and no entry here: it is still the caller's to give back. Nothing after the
        /// registration can throw.
        /// </remarks>
        public bool TryShow(
            LogicalFragmentId fragment,
            VpStoredGeometry geometry,
            Matrix4x4 objectToWorld,
            Matrix4x4 lineageToGeometryLocal,
            IReadOnlyCollection<VpClipBoundary> reflected)
            => TryShowCore(fragment, geometry, objectToWorld, lineageToGeometryLocal, reflected, null);

        private bool TryShowCore(
            LogicalFragmentId fragment, VpStoredGeometry geometry, Matrix4x4 objectToWorld,
            Matrix4x4 lineageToGeometryLocal, IReadOnlyCollection<VpClipBoundary> reflected, PreparedRoot prepared)
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

            VpIndirectCommand[] commands; Material[] commandMaterials; Bounds localBounds;
            bool ready = prepared == null
                ? TryPrepare(geometry, out commands, out commandMaterials, out localBounds)
                : TryPrepareRootCommand(prepared, geometry, out commands, out commandMaterials, out localBounds);
            if (!ready)
            {
                return false;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // The numeric input-contract diagnosis (DESIGN 5.6; not compiled for a non-Development Player): what the
            // registration settles by itself -- its box, its placement and the epsilon it will be built with -- is
            // refused here, before anything is transferred, registered or taken; the collection asks the same
            // function again.
            if (_numeric
                && (!VpMultiCutSnapshot.IsWithinInputContract(localBounds, objectToWorld, lineageToGeometryLocal)
                    || !VpMultiCutSnapshot.IsWithinSectionBounds(
                        localBounds, objectToWorld, VpCapBoundsPolygon.EpsilonFor(localBounds))))
            {
                return false;
            }
#endif

            // Room for the body at the end of the draw slots (DESIGN 5.6): what was ever taken, what the bodies
            // shown and not yet drawn will take, and this one -- a slot a command, and an instance record a command
            // with as many again for a second render fragment it may take later. An early refusal, not a reservation:
            // a body drawn as more than its region holds takes a region of its own at the end when it comes to it.
            // Within the limits, since a collection grows the room to what it needs. Past a limit is told, not only
            // refused; slots of what is drawn no more are not taken again, so it stays refused.
            long waiting = WaitingForSlots();
            if (_commandTail + waiting + commands.Length > _limits.commands)
            {
                ReportRoom("draw commands", _commandTail + waiting + commands.Length, _commandCapacity, _limits.commands,
                    "a body could not be shown");
                return false;
            }

            if (_instanceTail + waiting + (commands.Length * 2) > _limits.instances)
            {
                ReportRoom("draw instances", _instanceTail + waiting + (commands.Length * 2), _instanceCapacity,
                    _limits.instances, "a body could not be shown");
                return false;
            }

            if (prepared != null && _shown.Count == _shown.Capacity)
            {
                return false;
            }

            // The GPU copy's room for this geometry, before anything is consumed or transferred.
            if (!TryIndexEnd(geometry.indexRange, out int shownIndexEnd)
                || !TryMakeGpuRoom(geometry.vertexStart + geometry.vertexCount, shownIndexEnd))
            {
                return false;
            }

            // Consume before the first transfer: no reusable scratch arm can escape on failure.
            Shown coldEntry = prepared?.Take();
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

            // Everything that can throw -- the copies, the entry, the room in the lists -- is made before the
            // registration (2026-10-03): once the table holds the geometry, only assignments and additions within the
            // room made here follow, so an exception from this call never leaves a registration or an entry behind and
            // the geometry stays the caller's.
            VpReflectedSet reflectedCopy = VpReflectedSet.Of(reflected, _reflectedSets);

            var ranges = coldEntry == null ? new VpGeometryRange[commands.Length] : coldEntry.ranges;
            for (int c = 0; c < commands.Length; c++)
            {
                ranges[c] = commands[c].range;
            }

            var entry = coldEntry ?? new Shown();
            if (entry.instances.Count == entry.instances.Capacity)
            {
                entry.instances.Capacity = Math.Max(2, entry.instances.Capacity * 2);
            }

            if (_shown.Count == _shown.Capacity)
            {
                // The cold path refused a full list above, so this is the ordinary path's growth, made here.
                _shown.Capacity = Math.Max(2, _shown.Capacity * 2);
            }

            showBeforeRegistrationHookForTest?.Invoke();
            if (!_table.TryRegisterGeometryWithDisplayInstance(
                    geometry, out VpGeometryReference reference, out VpDisplayInstanceReference instance))
            {
                return false;
            }

            entry.fragment = fragment;
            entry.geometry = geometry;
            entry.reference = reference;
            entry.objectToWorld = objectToWorld;
            entry.lineageToGeometryLocal = lineageToGeometryLocal;
            entry.reflected = reflectedCopy;
            entry.commands = commands;
            entry.commandMaterials = commandMaterials;
            entry.ranges = ranges;
            entry.localBounds = localBounds;
            entry.instances.Add(instance);
            _shown.Add(entry);
            InputChanged();
            if (prepared != null) prepared.IsCommitted = true;
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
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            _commitNow = new CommitRecord { frame = Time.frameCount, operation = operation.value, source = source.value, shown = _shown.Count, outcome = "an exception" };
            _commitMark = begin;
            double setsBefore = _reflectedSets.seconds;
            try
            {
                return TryCommitCutCore(source, operation, plane, positiveFragment, in positive, negativeFragment, in negative, capTriangles);
            }
            finally
            {
                double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
                CommitCalls++;
                CommitSeconds += seconds;
                MaxCommitSeconds = Math.Max(MaxCommitSeconds, seconds);
                _commitNow.total = seconds;
                _commitNow.sets = _reflectedSets.seconds - setsBefore;
                _commitNow.register = Math.Max(0.0, _commitNow.register - _commitNow.sets);   // the sets are made inside the taking in
                if (_commitRecords.Count < CommitRecordLimit) _commitRecords.Add(_commitNow);
            }
        }

        private bool TryCommitCutCore(
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
                _commitNow.outcome = "refused: halted or unset";
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

            CommitLap(ref _commitNow.search);
            if (body == null)
            {
                // Nothing of this body is shown. Two empty sides change nothing, so that is not a refusal; a side with
                // geometry cannot be placed without the body's placement, so it is.
                _commitNow.outcome = positive.IsEmpty && negative.IsEmpty ? "no body shown, both sides empty" : "refused: no body shown";
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
                    _commitNow.outcome = "refused: the kept side's placement";
                    CommitLap(ref _commitNow.prepare);
                    return false;
                }

                CommitLap(ref _commitNow.prepare);
                body.fragment = kept;
                body.objectToWorld = keptPlacements.registered;
                body.reflected = body.reflected.With(new VpClipBoundary(face, side), _reflectedSets);
                CommitLap(ref _commitNow.register);
                _commitNow.outcome = "borrowed: the plane did not cut";
                return true;
            }

            if (positive.IsEmpty && negative.IsEmpty)
            {
                // Neither side has geometry: the body stops being shown, and no child is registered for it.
                Retire(body);
                CommitLap(ref _commitNow.register);
                _commitNow.outcome = "both sides empty: the body retired";
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    || !TryPrepareSide(
                        positive.geometry, body, in positivePlacements, out positiveCommands, out positiveMaterials,
                        out positiveBounds))
#else
                    || !TryPrepareSide(positive.geometry, out positiveCommands, out positiveMaterials, out positiveBounds))
#endif
                {
                    _commitNow.outcome = "refused: the positive side's placement or preparation";
                    CommitLap(ref _commitNow.prepare);
                    return false;
                }

                commands += positiveCommands.Length;
                registrations++;
            }

            if (negative.IsProduced)
            {
                if (!negativeFragment.IsSet
                    || !TrySidePlacements(negativeFragment, body, out negativePlacements)
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    || !TryPrepareSide(
                        negative.geometry, body, in negativePlacements, out negativeCommands, out negativeMaterials,
                        out negativeBounds))
#else
                    || !TryPrepareSide(negative.geometry, out negativeCommands, out negativeMaterials, out negativeBounds))
#endif
                {
                    _commitNow.outcome = "refused: the negative side's placement or preparation";
                    CommitLap(ref _commitNow.prepare);
                    return false;
                }

                commands += negativeCommands.Length;
                registrations++;
            }

            CommitLap(ref _commitNow.prepare);
            if (commands == 0)
            {
                _commitNow.outcome = "refused: no commands";
                return false;
            }

            // Two different rooms, judged apart. The drawing data is judged on the end of the draw slots (DESIGN 5.6):
            // the sides that replace the body take their commands and their instance records there, each at one
            // render fragment, and the body's own slots are not taken again. Holding the body's own geometry and
            // display instances until the next adoption is not drawing data at all -- it is room in the reference
            // table, and the table is asked by its own rule whether both sides could really be taken.
            long waiting = WaitingForSlots();
            if (_commandTail + waiting + commands > _limits.commands)
            {
                ReportRoom("draw commands", _commandTail + waiting + commands, _commandCapacity,
                    _limits.commands, "a cut could not be committed");
                _commitNow.outcome = "refused: draw command room";
                CommitLap(ref _commitNow.room);
                return false;
            }

            if (_instanceTail + waiting + commands > _limits.instances)
            {
                ReportRoom("draw instances", _instanceTail + waiting + commands, _instanceCapacity,
                    _limits.instances, "a cut could not be committed");
                _commitNow.outcome = "refused: draw instance room";
                CommitLap(ref _commitNow.room);
                return false;
            }

            if (!_table.HasRoomForGeometriesWithDisplayInstances(registrations))
            {
                ReportRoom("geometry references and display instances", registrations, _table.GeometryCapacity,
                    _table.GeometryLimit, "a cut could not be committed; " + _table.DescribeRoom());
                _commitNow.outcome = "refused: reference table room";
                CommitLap(ref _commitNow.room);
                return false;
            }

            CommitLap(ref _commitNow.room);

            // One vertex transfer for what the cut appended -- both sides share it -- and one index transfer for the
            // two sides together, which is the one contiguous run they were written as.
            VpStoredGeometry appended = positive.IsProduced ? positive.geometry : negative.geometry;

            // The GPU copy's room for both sides, before anything is transferred.
            int positiveEnd = 0;
            int negativeEnd = 0;
            if ((positive.IsProduced && !TryIndexEnd(positive.geometry.indexRange, out positiveEnd))
                || (negative.IsProduced && !TryIndexEnd(negative.geometry.indexRange, out negativeEnd))
                || !TryMakeGpuRoom(appended.vertexStart + appended.vertexCount, Math.Max(positiveEnd, negativeEnd)))
            {
                _commitNow.outcome = "refused: GPU room";
                CommitLap(ref _commitNow.growth);
                return false;
            }

            CommitLap(ref _commitNow.growth);

            int vertices;
            int indices;
            try
            {
                if (!VpStoredGeometryTransfer.TryUploadCommittedVertices(
                        _storage, _buffers.VertexBuffer, appended.vertexStart, appended.vertexCount, out vertices))
                {
                    _commitNow.outcome = "refused: vertex transfer";
                    CommitLap(ref _commitNow.transfer);
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
                    _commitNow.outcome = "refused: index transfer";
                    CommitLap(ref _commitNow.transfer);
                    return false;
                }
            }
            catch
            {
                _broken = true;
                throw;
            }

            CommitLap(ref _commitNow.transfer);

            VertexTransfers += vertices > 0 ? 1 : 0;
            IndexTransfers += indices > 0 ? 1 : 0;

            // The room was checked above, so taking these cannot fail for want of it. One that fails all the same is
            // this display's own invariant broken, not an ordinary refusal: nothing here may give a produced side's
            // index range back, because the cut still owns it until a commit is established.
            if (positive.IsProduced)
            {
                TakeBody(
                    positiveFragment, positive.geometry, body, in positivePlacements, positiveCommands, positiveMaterials,
                    positiveBounds, body.reflected.With(new VpClipBoundary(face, 1f), _reflectedSets));
            }

            if (negative.IsProduced)
            {
                TakeBody(
                    negativeFragment, negative.geometry, body, in negativePlacements, negativeCommands, negativeMaterials,
                    negativeBounds, body.reflected.With(new VpClipBoundary(face, -1f), _reflectedSets));
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
            CommitLap(ref _commitNow.register);
            _commitNow.outcome = "committed";
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

        /// <summary>
        /// One side of a commit: its draw commands and its box. Where the numeric input-contract diagnosis is compiled
        /// (the Editor and Development Players, DESIGN 5.6) it is also judged where it will really stand -- the placement
        /// it is drawn at -- and the body and the placements are taken for that judgement alone.
        /// </summary>
        private bool TryPrepareSide(
            VpStoredGeometry geometry,
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            Shown body,
            in SidePlacements placements,
#endif
            out VpIndirectCommand[] commands,
            out Material[] commandMaterials,
            out Bounds localBounds)
        {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (!TryPrepare(geometry, out commands, out commandMaterials, out localBounds)) return false;
            if (!_numeric) return true;   // the diagnosis is off in this process: nothing is read for it or asked
            Matrix4x4 placement = placements.drawn;
            return VpMultiCutSnapshot.IsWithinInputContract(localBounds, placement, body.lineageToGeometryLocal)
                && VpMultiCutSnapshot.IsWithinSectionBounds(
                    localBounds, placement, VpCapBoundsPolygon.EpsilonFor(localBounds));
#else
            return TryPrepare(geometry, out commands, out commandMaterials, out localBounds);
#endif
        }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
        // Whether the numeric input-contract diagnosis runs in this display (VpNumericDiagnosis, D-211): taken once,
        // when the display is made, as its snapshots take it when they are made. Where it is false a registration and
        // a commit's sides are not asked the contract and are not refused by it.
        private readonly bool _numeric = VpNumericDiagnosis.Enabled;
#endif

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
            VpReflectedSet reflected)
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

        // The reflected sets this display made: how many, their boundaries, and the time making them (their lookups included).
        private readonly VpReflectedSet.Counts _reflectedSets = new VpReflectedSet.Counts();

        /// <summary>The reflected sets made (a registration taken in, a commit's side), their boundaries in all, and the time making them.</summary>
        public long ReflectedSetsMade => _reflectedSets.made;
        public long ReflectedSetEntries => _reflectedSets.entries;
        public double ReflectedSetSeconds => _reflectedSets.seconds;

        /// <summary>The boundaries the registrations now shown hold in their reflected sets (what is kept, not what was made).</summary>
        public long ReflectedEntriesHeld
        {
            get
            {
                long n = 0;
                foreach (Shown entry in _shown) n += entry.reflected.Count;
                return n;
            }
        }

        /// <summary>Tests only: the reflected set a registration now shown holds.</summary>
        internal bool TryGetReflectedForTest(LogicalFragmentId fragment, out VpReflectedSet set)
        {
            foreach (Shown entry in _shown)
            {
                if (entry.fragment == fragment) { set = entry.reflected; return true; }
            }

            set = null;
            return false;
        }

        /// <summary>
        /// One commit attempt, for observation (2026-10-01): its frame, operation, source fragment, the registrations
        /// shown when it came, what it came to, and its time by stage -- finding the body, preparing the sides, judging the
        /// room, the GPU capacity's management (<c>growth</c>: making room in the GPU copy -- the replaced buffers' release,
        /// a larger buffer and what is drawn transferred into it again), transferring, making the reflected sets, and taking
        /// the sides in (the sets apart) -- and in all. Laps at the stages' ends only.
        /// </summary>
        public struct CommitRecord
        {
            public int frame, operation, source, shown;
            public string outcome;
            public double search, prepare, room, growth, transfer, sets, register, total;
        }

        private const int CommitRecordLimit = 16384;
        private readonly List<CommitRecord> _commitRecords = new List<CommitRecord>(256);
        private CommitRecord _commitNow;
        private long _commitMark;

        /// <summary>The commit attempts recorded (the first 16384).</summary>
        public IReadOnlyList<CommitRecord> CommitRecords => _commitRecords;

        private void CommitLap(ref double part)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            part += (now - _commitMark) / (double)System.Diagnostics.Stopwatch.Frequency;
            _commitMark = now;
        }

        /// <summary>The commits of a cut's sides: how many, their time in all and the longest (the reflected sets' making included).</summary>
        public long CommitCalls { get; private set; }
        public double CommitSeconds { get; private set; }
        public double MaxCommitSeconds { get; private set; }

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

            // Replaced GPU buffers the GPU is past go here as well as at drawing, so that a frame with no camera does not
            // keep them.
            _buffers.ReleaseRetired(_giveUpBuffer);
            ReleaseRetiredRoom();
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
            _buffers.ReleaseRetired(_giveUpBuffer);
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
            if (FirstRenderAt == 0)
            {
                FirstRenderAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }
            cameraStencil.drawnFrame = CurrentFrame;

            // Selecting on the GPU: these draws read what this camera's selection writes, which whoever put this
            // display in the frame issues before the camera's passes run (IssueCull). The batch they read is noted
            // here, so that the selection is issued for that very batch.
            bool culled = _batch.CullsOnGpu;
            if (culled)
            {
                NoteCullRequest(cameraStencil);
            }

            // A batch whose selection writes the plugin's argument entries (DESIGN 4.5.8) is drawn by the plugin alone:
            // Unity's own draw cannot read those entries, so without a sink nothing is issued and the caller is told.
            IVpNativeDrawSink nativeSink = NativeDrawSink;
            if (_batch.NativeArguments && nativeSink == null)
            {
                throw new InvalidOperationException(
                    "this display's batch writes the plugin's argument entries; set NativeDrawSink before drawing");
            }

            // The surfaces, grouped by material: one forward call per run of command slots whose commands share one.
            // A free slot -- no material, a command of no instance -- breaks no run and begins none: it is drawn
            // through, drawing nothing, when it lies inside a run, and passed over otherwise (DESIGN 5.6).
            int start = 0;
            while (start < _commandEnd)
            {
                Material material = _commandMaterials[start];
                if (material == null)
                {
                    start++;
                    continue;
                }

                int end = start + 1, next = end;
                while (next < _commandEnd)
                {
                    Material other = _commandMaterials[next];
                    if (other != null)
                    {
                        if (!ReferenceEquals(other, material))
                        {
                            break;
                        }

                        end = next + 1;
                    }

                    next++;
                }

                if (nativeSink != null)
                {
                    nativeSink.AddBody(camera, _batch, _buffers, material, start, end - start, cameraStencil.view, layer);
                }
                else if (culled)
                {
                    _batch.RenderForward(material, _properties, _buffers, layer, start, end - start, camera, cameraStencil.view);
                }
                else
                {
                    _batch.RenderForward(material, _properties, _buffers, layer, start, end - start, camera);
                }

                start = next;
            }

            // The casters, grouped by which side of DESIGN 5.4's division a command falls on, over the same commands,
            // transforms, clip records and offsets as the surfaces above; free slots are passed over the same way.
            if (_shadowMaterial != null)
            {
                start = 0;
                while (start < _commandEnd)
                {
                    if (_commandMaterials[start] == null)
                    {
                        start++;
                        continue;
                    }

                    bool provisional = _commandProvisional[start];
                    int end = start + 1, next = end;
                    while (next < _commandEnd)
                    {
                        if (_commandMaterials[next] != null)
                        {
                            if (_commandProvisional[next] != provisional)
                            {
                                break;
                            }

                            end = next + 1;
                        }

                        next++;
                    }

                    if (nativeSink != null)
                    {
                        nativeSink.AddCasters(
                            camera, _batch, _buffers, provisional ? _provisionalShadowMaterial : _shadowMaterial, start, end - start, cameraStencil.view, layer);
                    }
                    else if (culled)
                    {
                        _batch.RenderShadows(
                            provisional ? _provisionalShadowMaterial : _shadowMaterial, _properties, _buffers, layer,
                            start, end - start, camera, cameraStencil.view);
                    }
                    else
                    {
                        _batch.RenderShadows(
                            provisional ? _provisionalShadowMaterial : _shadowMaterial, _properties, _buffers, layer,
                            start, end - start, camera);
                    }

                    if (provisional)
                    {
                        TwoSidedShadowIssues++;
                    }
                    else
                    {
                        OneSidedShadowIssues++;
                    }

                    start = next;
                }
            }

            // The counting and the caps, after the surfaces: their queues put them after the opaque bodies.
            cameraStencil.batch.Render(_stencilMaterials, _buffers, layer, camera);
        }

        /// <summary>
        /// Where the draws go when the batch writes the plugin's argument entries (DESIGN 4.5.8; see
        /// <see cref="VpIndexedIndirectDrawBatch.NativeArguments"/>): set by whoever puts the display in the frame,
        /// before <see cref="Render"/>. Null for a display drawn by Unity's own calls. Every sink set here is
        /// remembered (<see cref="SinksUsed"/>): a buffer this display gives up waits on all of them, since a former
        /// route's events may still name it when a new route has taken over (the drawing component disabled and
        /// enabled makes a new route while the old one's events can be unconsumed -- TL, 2026-10-08).
        /// </summary>
        public IVpNativeDrawSink NativeDrawSink
        {
            get => _nativeDrawSink;
            set
            {
                _nativeDrawSink = value;
                if (value != null && !_sinksUsed.Contains(value)) _sinksUsed.Add(value);
            }
        }

        private IVpNativeDrawSink _nativeDrawSink;
        private readonly List<IVpNativeDrawSink> _sinksUsed = new List<IVpNativeDrawSink>(2);
        private readonly Action<GraphicsBuffer> _giveUpBuffer;

        /// <summary>The sinks this display has drawn through whose events may still name its buffers (the current one always).</summary>
        public IReadOnlyList<IVpNativeDrawSink> SinksUsed => _sinksUsed;

        /// <summary>How many buffers this display gave up with more than one route's events to wait on.</summary>
        public long BuffersRetiredAcrossRoutes { get; private set; }

        // A buffer this display is done with (replaced, or the display ending): disposed at once when no plugin route
        // ever drew it; given up to the one route that did; or, when several did, kept until every one of their issued
        // events is over (VpNativeDrawRelease.Retire) -- the last sink is never taken for the buffer's only user.
        private void GiveUpBuffer(GraphicsBuffer buffer)
        {
            if (buffer == null) return;
            for (int i = _sinksUsed.Count - 1; i >= 0; i--)
            {
                // A former sink none of whose events can still read anything draws for this display no more: forgotten.
                if (!ReferenceEquals(_sinksUsed[i], _nativeDrawSink) && _sinksUsed[i].IssuedEventsWait() == null) _sinksUsed.RemoveAt(i);
            }

            if (_sinksUsed.Count == 0)
            {
                buffer.Dispose();
            }
            else if (_sinksUsed.Count == 1)
            {
                _sinksUsed[0].Retire(buffer);
            }
            else
            {
                BuffersRetiredAcrossRoutes++;
                VpNativeDrawRelease.Retire("VP display buffer (several routes)", _sinksUsed, buffer);
            }
        }

        /// <summary>Whether the batch's selection writes the plugin's argument entries, so that only <see cref="NativeDrawSink"/> can draw it.</summary>
        public bool NativeArguments => _batch != null && _batch.NativeArguments;

        /// <summary>Whether the body is drawn through the GPU selection of DESIGN 4.5.7. Fixed when the display is made.</summary>
        public bool CullsOnGpu => _cull != null;

        /// <summary>
        /// Observation: selections issued for a camera's registered draws; and frames in which a camera registered
        /// draws whose selection had not been issued when it next drew -- its draws then read the selection of an
        /// earlier frame, which is a fault of whoever puts the selection in the frame, told once in the log.
        /// </summary>
        public long CullSelectionsIssued { get; private set; }
        public long CullSelectionsMissed { get; private set; }

        /// <summary>Observation: selections dispatched by the body batches since this display was made. GPU work, not transfers.</summary>
        public long CullDispatches => _pastCullDispatches + _batch.CullDispatches;

        /// <summary>The bytes the selection's results take on the GPU now, every camera's together; zero for VP Stage 3.</summary>
        public long CullViewBytes => _batch.CullViewBytes;

        /// <summary>
        /// Diagnosis only -- tests and evidence runs: reads back, **waiting for the GPU**, what the last selection
        /// issued for <paramref name="camera"/> kept: of how many instances, how many for the body and how many as
        /// casters. The product never calls this. False when the display does not select on the GPU, no selection has
        /// been issued for that camera, or the batch it was issued for has since been replaced.
        /// </summary>
        public bool TryReadCullCountsForDiagnosis(Camera camera, out int instances, out int forwardKept, out int shadowKept)
        {
            instances = 0;
            forwardKept = 0;
            shadowKept = 0;
            if (_disposed || _cull == null)
            {
                return false;
            }

            CameraStencil slot = FindCamera(camera);
            if (slot == null || slot.cullIssuedFrame == int.MinValue || !ReferenceEquals(slot.cullBatch, _batch))
            {
                return false;
            }

            // The instances that are drawn: the records the batch goes through include free ones (DESIGN 5.6).
            instances = _liveInstances;
            _batch.ReadCullCountsForDiagnosis(slot.view, out forwardKept, out shadowKept);
            return true;
        }

        private void NoteCullRequest(CameraStencil slot)
        {
            if (slot.cullRequestedFrame != int.MinValue && slot.cullRequestedFrame != CurrentFrame
                && slot.cullIssuedFrame != slot.cullRequestedFrame)
            {
                CullSelectionsMissed++;
                if (!_cullMissLogged)
                {
                    _cullMissLogged = true;
                    Debug.LogError(
                        "VpLogicalCutDisplay: " + (slot.camera != null ? slot.camera.name : "a camera") + " drew frame "
                        + slot.cullRequestedFrame + " without its GPU selection having been issued; its draws read an "
                        + "earlier frame's selection. Told once.");
                }
            }

            slot.cullRequestedFrame = CurrentFrame;
            slot.cullBatch = _batch;
        }

        /// <summary>
        /// Issues, into <paramref name="commands"/>, the selection for the draws <see cref="Render"/> registered for
        /// <paramref name="camera"/> in this frame, from <paramref name="conditions"/> (DESIGN 4.5.7). The commands must
        /// be executed before that camera's shadow maps and colour are drawn; it is the caller that puts them there.
        /// It may be issued again in the same frame, for a camera that renders more than one pass: each issue serves
        /// the draws that follow it. False, issuing nothing, when the display does not select on the GPU, has ended,
        /// or registered no draw for that camera in this frame.
        /// </summary>
        public bool IssueCull(UnityEngine.Rendering.CommandBuffer commands, Camera camera, VpCullConditions conditions)
        {
            if (_disposed || _cull == null)
            {
                return false;
            }

            CameraStencil slot = FindCamera(camera);
            if (slot == null || slot.cullBatch == null || slot.cullRequestedFrame != CurrentFrame)
            {
                return false;
            }

            slot.cullBatch.IssueCull(commands, slot.view, conditions);
            slot.cullIssuedFrame = CurrentFrame;
            CullSelectionsIssued++;
            return true;
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

            EnsureIndexView();
            command = _commands[_viewCommandSlots[index]];
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
            if (index < 0 || index >= _liveInstances)
            {
                side = default;
                return false;
            }

            // What the side is was settled with the structure and is kept where its instance record stands; its clip is
            // where it stands now, kept with the instances' own; and its render fragment's number is the adopted
            // snapshot's, read from where its registration's render fragments begin there.
            EnsureIndexView();
            int record = _viewInstanceRecords[index];
            LogicalCutDisplaySide kept = _sides[record];
            side = new LogicalCutDisplaySide(
                kept.source, _viewRenderFragments[index], kept.operation, kept.side, kept.published, kept.fragment, kept.fixedByAnchors,
                _clips[record]);
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
            while (_preparedRoots != null) _preparedRoots.Dispose();
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
            _capJobs.Dispose();
            _commandCount = 0;
            _commandEnd = 0;
            _instanceEnd = 0;
            _liveInstances = 0;
            _commandTail = 0;
            _instanceTail = 0;
            _unshownWithSlots.Clear();
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
            _batch.Dispose(_giveUpBuffer);
            _buffers.Dispose(_giveUpBuffer);
            DestroyMaterials(_ownedMaterials);

            // Teardown alone waits: for each replaced object's readback, so the GPU is past it when it is released.
            foreach (RetiredGpu retired in _retiredGpu)
            {
                if (!retired.done && !retired.error)
                {
                    retired.request.WaitForCompletion();
                }

                retired.owner.Dispose();
            }

            _retiredGpu.Clear();

            // The room: no reader is left -- the cameras' batches and the body's were given back above -- so the
            // snapshots' and this display's own numbers go back to the backing, once each.
            _snapshot.Dispose();
            _building.Dispose();
            DisposeRooms();
        }

        // ----- collection ----------------------------------------------------------------------------------------

        // The collection's stages, each on a marker of its own (diagnosis: which stage a long collection spends in). Exactly
        // one is open at a time; the wrapper closes the last one whichever way the collection ends.
        private static readonly Unity.Profiling.ProfilerMarker[] s_collectStages =
        {
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.0Room"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.1Read"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.2Snapshot"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.3Draw"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.4Instances"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.5Candidate"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.6Stencil"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.7Upload"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.8Adopt"),
            new Unity.Profiling.ProfilerMarker("Zantetsu.Display.Collect.9Release"),
        };

        private int _collectStage = -1;

        // The collection under way, timed whole and by stage with the clock itself (2026-10-07): the Profiler's markers
        // above are not there in a Player that is not a Development build. One clock read a stage; summed into the
        // frame's counts when the collection ends. And the snapshot's build calls of the collection, timed apart.
        private static readonly double s_secondsPerTick = 1.0 / System.Diagnostics.Stopwatch.Frequency;
        private readonly double[] _stageSeconds = new double[10];
        private long _stageBegan, _collectBegan;
        private int _buildCalls;
        private double _buildSeconds;
        private long _buildHeapDelta;

        // ----- placements asked only when they can have changed (DESIGN 5.6, D-204) -----------------------------------
        //
        // Where things stand changes when the physics is stepped, or when something outside a step puts an owner
        // somewhere, brings one or takes one away. The host counts both (the steps really simulated; the placement
        // inputs changed outside a step) and gives the two numbers when asked. A collection that finds them as they were
        // at the last ADOPTED placement pass, with the adopted snapshot of the very structure the ledger and this
        // display's inputs are at now, lets that snapshot stand: nothing is asked, no clip or cap is made again. The two
        // snapshots change places for the collection -- the adopted one is read as the one built -- and the adoption
        // changes them back; a collection that is not adopted changes them back itself. Everything else of the
        // collection runs as ever.
        //
        // The numbers are taken at the collection that asks the placements and kept only when it is adopted: a
        // collection that failed leaves them as they were, so the next one asks. A host that gives no numbers, or says
        // it cannot vouch for them, is asked every time. Letting a snapshot stand says nothing of anything standing
        // still: no render fragment is counted as kept for it, and no history is advanced.

        /// <summary>The host's two counts; false when it cannot vouch that a placement changes only with one of them.</summary>
        public delegate bool PlacementSerialSource(out long step, out long outsideStep);

        private PlacementSerialSource _placementSerial;

        /// <summary>
        /// Asked once a collection for the count of physics steps simulated and of placement inputs changed outside a
        /// step. Null (the default): every collection asks every placement.
        /// </summary>
        public PlacementSerialSource PlacementSerial
        {
            get => _placementSerial;
            set
            {
                _placementSerial = value;
                _placedSerialKnown = false;   // whatever was adopted was not counted by this source
                _holds.Forget();              // nor was any step a placement is remembered from
            }
        }

        private bool _placementReused, _placementPassSkipped, _buildSerialKnown, _placedSerialKnown;
        private long _buildStep, _buildOutside, _placedStep, _placedOutside;

        /// <summary>Observation: collections that let the adopted snapshot stand, and the placement queries they did not make.</summary>
        public long PlacementReuses { get; private set; }
        public long PlacementQueriesOmitted { get; private set; }

        /// <summary>
        /// Observation (TL, 2026-10-08): collections after a new step that made no placement pass because the pass
        /// would have had no target -- every render fragment fixed (its lookup vouches a step does not move it,
        /// <see cref="IVpFixedPlacementSource"/>) or held and far, nothing told. The adopted snapshot stood, as for a
        /// collection with no new step; its queries are in <see cref="PlacementQueriesOmitted"/>.
        /// </summary>
        public long PlacementPassesSkipped { get; private set; }

        /// <summary>How many render fragments are fixed now: vouched for by the lookup, not asked for a step.</summary>
        public int FixedPlacements => _holds.FixedCount;

        /// <summary>Observation: the structure tally (the room asked for the registrations) run, and kept from the structure it was last run for.</summary>
        public long StructureTallies { get; private set; }
        public long StructureTalliesSkipped { get; private set; }

        /// <summary>Observation: the instance takes (one display instance a render fragment) run, and kept while the structure stands.</summary>
        public long InstanceTakes { get; private set; }
        public long InstanceTakesSkipped { get; private set; }

        // The structure (ledger revision, input revision) the tally was last run for; the instances stand as the last
        // adopted structure needs them (one a render fragment, taken in stage 4 and trimmed in stage 9).
        private long _talliedLedger = -1, _talliedInputs = -1;
        private bool _instancesSettled;

        // ----- held placements: who is asked in a pass that does ask (DESIGN 5.6, D-205) ------------------------------
        //
        // A collection after a new step asks where things stand. Of the render fragments, the ones seen standing, bit
        // for bit, where the adopted snapshot has them over two different step results are held: their world boxes go
        // into a tree, and a pass asks only the others and the held ones near one of the host's reference points --
        // its cameras' positions -- and, where that camera's view is known, in it (D-210). A held one that is not
        // asked is drawn, and casts its shadow, where it is held. See VpHeldPlacements. A host that names no reference
        // points (null, the default) or does not vouch for its step count has every render fragment asked, as before.

        /// <summary>Adds the host's reference points -- the positions near which a held placement is asked again.</summary>
        public delegate void PlacementProximitySource(List<Vector3> into);

        private PlacementProximitySource _placementProximity;
        private readonly VpHeldPlacements _holds = new VpHeldPlacements();
        private readonly List<Vector3> _proximityPoints = new List<Vector3>(4);

        /// <summary>
        /// Adds the host's reference points and, beside each, the camera it is the position of (D-210) -- a camera
        /// registered with this display, or null for a point that is no camera's. A held placement near a point is
        /// asked again only if it is also in the view that point's camera was last rendered with
        /// (<see cref="NoteCameraView"/>); a point with no camera, or whose camera's view is not known, asks by
        /// nearness alone.
        /// </summary>
        public delegate void PlacementProximityViewSource(List<Vector3> points, List<Camera> cameras);

        private PlacementProximityViewSource _placementProximityViews;
        private readonly List<Camera> _proximityCameras = new List<Camera>(4);

        /// <summary>
        /// Asked once in a collection that asks placements, for the reference points and their cameras; it stands in
        /// for <see cref="PlacementProximity"/> when both are set. Null (the default): the points come from
        /// <see cref="PlacementProximity"/>, with no camera -- nearness alone.
        /// </summary>
        public PlacementProximityViewSource PlacementProximityViews
        {
            get => _placementProximityViews;
            set
            {
                _placementProximityViews = value;
                _holds.Forget();
            }
        }

        /// <summary>
        /// The view <paramref name="camera"/> was rendered with in this frame: <paramref name="eyes"/> eyes (1, or 2
        /// under XR) whose world-to-clip matrices are given (<see cref="Camera.projectionMatrix"/>'s convention: inside
        /// is -w &lt;= x, y, z &lt;= w; the second is not read for one eye). The host calls it at the end of that
        /// camera's rendering, when the camera's matrices are the ones rendered with. It is kept with the camera and
        /// read by the held placements of a collection in this frame or the next (D-210); an older one counts as not
        /// known. False, keeping nothing, for a camera that is not registered; a view that is not finite is kept as
        /// not known. Nothing is drawn, asked or changed by it.
        /// </summary>
        public bool NoteCameraView(Camera camera, int eyes, in Matrix4x4 leftWorldToClip, in Matrix4x4 rightWorldToClip)
        {
            if (_disposed || ReferenceEquals(camera, null))
            {
                return false;
            }

            CameraStencil slot = FindCamera(camera);
            if (slot == null)
            {
                return false;
            }

            slot.viewEyes = 0;
            slot.viewFrame = CurrentFrame;
            if (eyes < 1 || eyes > VpHeldPlacements.EyeCapacity)
            {
                return true;
            }

            for (int e = 0; e < eyes; e++)
            {
                Matrix4x4 m = e == 0 ? leftWorldToClip : rightWorldToClip;
                Vector4 x = m.GetRow(0), y = m.GetRow(1), z = m.GetRow(2), w = m.GetRow(3);
                int first = e * VpHeldPlacements.EyePlanes;
                slot.viewPlanes[first + 0] = w + x;
                slot.viewPlanes[first + 1] = w - x;
                slot.viewPlanes[first + 2] = w + y;
                slot.viewPlanes[first + 3] = w - y;
                slot.viewPlanes[first + 4] = w + z;
                slot.viewPlanes[first + 5] = w - z;
                for (int k = 0; k < VpHeldPlacements.EyePlanes; k++)
                {
                    Vector4 p = slot.viewPlanes[first + k];
                    float length = Mathf.Sqrt(p.x * p.x + p.y * p.y + p.z * p.z);
                    if (!(length > 0f) || float.IsInfinity(length) || float.IsNaN(p.w) || float.IsInfinity(p.w))
                    {
                        return true;   // not a view: kept as not known
                    }

                    slot.viewPlanes[first + k] = p / length;
                }
            }

            slot.viewEyes = eyes;
            CameraViewsNoted++;
            return true;
        }

        /// <summary>Observation: views noted that were whole (finite, with their planes).</summary>
        public long CameraViewsNoted { get; private set; }

        /// <summary>
        /// Asked once in a collection that asks placements, for the reference points. Null (the default): nothing is
        /// held, every render fragment is asked. A source that adds no point says nothing is near.
        /// </summary>
        public PlacementProximitySource PlacementProximity
        {
            get => _placementProximity;
            set
            {
                _placementProximity = value;
                _holds.Forget();
            }
        }

        /// <summary>
        /// Gives the fragments the changes outside a step were told with since <paramref name="cursor"/>, and the count
        /// of the changes told with no target; false when it no longer has them all.
        /// </summary>
        public delegate bool PlacementChangeSource(ref long cursor, List<LogicalFragmentId> into, out long untargeted);

        private PlacementChangeSource _placementChanges;
        private long _changesCursor = -1;
        private readonly List<LogicalFragmentId> _changedFragments = new List<LogicalFragmentId>(8);

        /// <summary>
        /// Asked once in a collection that asks placements, for what the changes outside a step concerned (D-207): the
        /// held placements of those fragments' families are asked, the others are not. Null (the default): every
        /// change outside a step has every held placement asked.
        /// </summary>
        public PlacementChangeSource PlacementChanges
        {
            get => _placementChanges;
            set
            {
                _placementChanges = value;
                _changesCursor = -1;
                _holds.Forget();
            }
        }

        /// <summary>Metres on each axis about a reference point within which a held placement is asked again (20 by default).</summary>
        public float PlacementHoldReach { get => _holds.Reach; set => _holds.Reach = value; }

        /// <summary>Metres added on each axis to a held target's box (0.5 by default). Changing it forgets what is held.</summary>
        public float PlacementHoldMargin
        {
            get => _holds.Margin;
            set
            {
                _holds.Margin = value;
                _holds.Forget();
            }
        }

        /// <summary>Observation: what the held placements did since this display was made, and how many are held now.</summary>
        public VpHeldPlacementTotals HeldPlacementTotals => _holds.Totals;
        public int HeldPlacements => _holds.HeldCount;
        internal VpHeldPlacements HeldPlacementsForTest => _holds;

        private VpSnapshotStageTotals SumStages()
        {
            VpSnapshotStageTotals sum = _snapshot.StageTotals;
            if (!ReferenceEquals(_building, _snapshot)) sum.Add(_building.StageTotals);

            // The display's own counts of the caps' vertices and records (D-208).
            sum.capLayouts = CapLayouts;
            sum.capLayoutVertices = CapLayoutVertices;
            sum.cameraCapUploads = CameraCapLayoutUploads;
            sum.cameraCapVertices = CameraCapLayoutVertices;
            sum.cameraCapFirstUploads = CameraCapLayoutFirstUploads;
            sum.cameraCapFirstVertices = CameraCapLayoutFirstVertices;
            sum.capRecordElements = CapNormalVerticesTransferred;
            return sum;
        }

        private void EnterCollectStage(int stage)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_collectStage >= 0)
            {
                s_collectStages[_collectStage].End();
                _stageSeconds[_collectStage] += (now - _stageBegan) * s_secondsPerTick;
            }

            _stageBegan = now;
            s_collectStages[stage].Begin();
            _collectStage = stage;
            if (_compactThisPass)
            {
                _compactionStageAt[stage] = System.Diagnostics.Stopwatch.GetTimestamp();   // the compaction's cost, by stage (D-202)
            }
        }

        // ----- what a collection keeps from the one before (DESIGN 5.6) -----------------------------------------------
        //
        // What a structure is settled from is counted by two numbers: the ledger's Revision (every change a reader could
        // see, whoever made it) and this display's own input revision (a registration taken in, replaced by a commit or
        // let go; the placement lookup replaced). Whatever was worked out for those two numbers stands while they do.

        // The registrations' ledger state and the registration list: what they were read at.
        private long _readInputRevision = -1, _readLedgerRevision = -1;
        private readonly List<LogicalFragmentId> _changedFamilies = new List<LogicalFragmentId>(32);
        private readonly Dictionary<LogicalFragmentId, int> _familyHead = new Dictionary<LogicalFragmentId, int>();

        // What each registration is drawn as (stage 3): what it was worked out for, and what it came to.
        private long _drawnLedger = -1, _drawnInputs = -1;

        // The tables that go by the snapshot's own numbering -- where each render fragment's commands are and where it
        // stands, the roots and the draw ranges of every registration -- on each side, the adopted one and the one a
        // collection builds in: what they were made for (negative: not to be relied on). They are made again when
        // the structure is another; the draw slots below are not.
        private long _sideLedger = -1, _sideInputs = -1, _candidateSideLedger = -1, _candidateSideInputs = -1;

        // The one range of render fragments over which the two sides' tables may differ: what the side last built was
        // written over. Empty: start = end = 0.
        private int _renderFragmentDiffStart, _renderFragmentDiffEnd;

        // ----- the draw slots (DESIGN 5.6) -------------------------------------------------------------------------------
        //
        // Every registration that is drawn holds a run of command slots and an instance region (SlotState). Both are
        // taken at the end of everything taken so far -- the two counts below, which only grow -- and are never taken
        // again by another registration: what is drawn no more leaves its command slots holding commands of no
        // instance, and its instance records unnamed, for as long as the display lives. Nothing is searched, split
        // or joined, and nothing is moved. What a collection takes it takes one after another, so what it adds is one
        // run of each buffer. A collection that is not adopted puts the two counts back: what it took was never
        // published. A collection goes through the command slots in [0, end) and may name the instance records in
        // [0, end); the room grows to where they end, within the limits.
        private int _commandTail, _instanceTail, _commandTailBefore, _instanceTailBefore;

        // The adopted draw data: where it ends and how much of it draws.
        private int _commandEnd, _instanceEnd, _liveInstances;

        // What the collection under way wrote into the side it builds in, recorded as it is written: what is sent, and
        // -- once adopted -- what the other side lacks. And what the side being built lacks of the adopted one: it is
        // given that from it first. Recorded by whoever writes; nothing is compared to find it.
        //
        // The commands: ranges, joined when near. The instance records (transform, clip, side): ONE range, from the
        // first record written to past the last -- the transforms and the clips are sent as that one range, a buffer
        // write each, with whatever lies between (DESIGN 5.6). So the side being built is given the whole range it
        // lacks before anything is written: every record of the range sent is then the adopted content with this
        // collection's writes over it. A transform and a clip are written together everywhere, so the two buffers'
        // ranges are the same one. This is what is sent; the history of ordinary writes (D-202) is noted apart.
        private VpChangedRanges _writtenCommands = new VpChangedRanges(16, 8);
        private VpChangedRanges _lackingCommands = new VpChangedRanges(16, 8);
        private InstanceSpan _writtenInstances, _lackingInstances;

        // One contiguous range of instance records, [start, end); empty when end is not past start.
        private struct InstanceSpan
        {
            public int start, end;

            public bool IsEmpty => end <= start;

            public int Count => end > start ? end - start : 0;

            public void Add(int from, int to)
            {
                if (to <= from)
                {
                    return;
                }

                if (end <= start)
                {
                    start = from;
                    end = to;
                    return;
                }

                if (from < start) start = from;
                if (to > end) end = to;
            }

            public void Add(in InstanceSpan other) => Add(other.start, other.end);

            public void Clear()
            {
                start = 0;
                end = 0;
            }
        }

        // The side being built cannot be brought up by ranges -- nothing was ever written to it, or its room was made
        // again larger: it is written whole, every slot.
        private bool _candidateSlotsWhole = true;

        // The collection under way: its number; whether it is still to be adopted or put back; the registrations
        // whose slots it changed; how many instance records its commands draw. And the registrations that are no
        // longer shown and still hold slots: let go by the next collection.
        private int _slotPass;
        private bool _slotPassOpen;
        private readonly List<Shown> _slotTouched = new List<Shown>(16);
        private readonly List<Shown> _unshownWithSlots = new List<Shown>(4);
        private int _plannedCommands, _plannedInstances, _plannedCommandsBefore, _plannedInstancesBefore;

        // ----- the compaction of the instance regions (DESIGN 5.6, D-202) ------------------------------------------------
        //
        // The regions are taken at the end and never again, so what is drawn no more leaves records that nothing draws
        // -- holes -- among the live ones, and the live ones spread over everything ever taken. A compaction lays every
        // live region out again from the first record, in the order of the frame the ordinary processing last wrote a
        // record of each registration -- the longest unwritten first, the most recently written last -- each region
        // exactly what it draws, and the tail comes down to what is live: what is written, and so sent, stands
        // together. The records are written from the snapshot into the side being built, as any other write of a
        // collection; nothing is moved in place. The command slots are not compacted: a command stays in its slot and
        // names its region's new start.
        //
        // **What the history is.** A registration's last-written frame advances when a collection is adopted whose
        // ordinary processing wrote an instance record of it, whatever the record held before: a render fragment the
        // placement pass placed anew (one record of the registration is enough), or the registration written by a
        // structural collection (first drawn, cut, published, committed, or one of its family was). It is noted at
        // those write sites and nowhere else, with no comparison of values: when a write is spared there one day, its
        // note goes with it. The compaction's own rewrite, the catch-up copy between the sides, records only inside a
        // range sent, and a side or a GPU buffer written whole with what was held are not ordinary writes. A whole
        // write in the same collection does not hide an ordinary one: what the placement pass placed anew, and what a
        // structural collection planned, are noted as such whoever wrote the record. The history is for the order of
        // the regions alone: nothing is updated less or drawn differently for it.
        //
        // **When.** Whether a compaction is a candidate is this display's: a collection whose structure stands, at
        // least the minimum interval of adopted collections after the last compaction (or the last candidate that
        // found nothing to move), and since then an ordinary write adopted or a hole appeared. A candidate whose order moves no
        // region writes and sends nothing and resets that baseline. Whether this frame may bear it is the world's,
        // asked through the gate with the expected cost: heavy work this frame (refill, MobPlan, the static index, a
        // garbage collection), a cut in flight, the frame's remaining budget. Inside the collection it is also not run
        // when the structure changed, when room was grown or GPU room is needed, or when the side is written whole.
        // A candidate that is skipped, or whose collection is not adopted, keeps everything pending.
        private int _compactionMinimumInterval = 300;   // adopted collections; provisional (TL, 2026-10-06)
        private bool _compactThisPass;
        private long _compactionBegan;
        private readonly long[] _compactionStageAt = new long[10];
        private int _roomGrowthsAtPassStart, _snapshotRegrowthsAtPassStart;
        private readonly double[] _compactionCosts = new double[5];
        private int _compactionCostCount;

        // The baseline the next candidate is judged against: the adopted collections at the last compaction (or the
        // last candidate with nothing to move), the registrations' ordinary writes adopted since, the holes then.
        private long _adoptedPasses, _compactionBaselinePass;
        private int _compactionBaselineHoles;
        private long _writesSinceCompaction;
        private bool _candidateOpen;

        // The registrations this collection's ordinary processing wrote (not yet adopted), and the order's work arrays.
        private readonly List<Shown> _writtenThisPass = new List<Shown>(16);
        private long[] _orderKeys = new long[64];
        private Shown[] _orderEntries = new Shown[64];

        /// <summary>
        /// The seed of the expected cost, a second per live record, before any compaction was measured (D-202). Read
        /// from the Editor's measurement of one; the Player's own replaces it from the first compaction on.
        /// </summary>
        public const double CompactionSeedSecondsPerRecord = 1.0e-6;

        /// <summary>
        /// Asked, with the expected cost in seconds, whether this frame may bear a compaction that is a candidate:
        /// null to allow it, or the reason not to (kept and counted). Null when nobody answers: allowed.
        /// </summary>
        public Func<double, string> CompactionGate { get; set; }

        /// <summary>
        /// How many adopted collections must pass after a compaction before another is a candidate (D-202). The
        /// product's value is the default; tests lower it.
        /// </summary>
        public int CompactionMinimumInterval
        {
            get => _compactionMinimumInterval;
            set => _compactionMinimumInterval = Math.Max(1, value);
        }

        /// <summary>
        /// Observation: how many times a compaction became a candidate (once per baseline), the collections it was a
        /// candidate in (one that is skipped is a candidate again in the next), the compactions run, the records laid
        /// out, the registrations that put a record elsewhere.
        /// </summary>
        public long CompactionCandidates { get; private set; }
        public long CompactionCandidateCollections { get; private set; }
        public long Compactions { get; private set; }
        public long CompactionRecordsLaidOut { get; private set; }
        public long CompactionRegistrationsMoved { get; private set; }

        /// <summary>Observation: candidates whose order moved no region (nothing written or sent, the baseline reset).</summary>
        public long CompactionsWithoutChange { get; private set; }

        /// <summary>Observation: candidate collections that did not run (the gate, or the collection's own reasons), and why the last one did not.</summary>
        public long CompactionsSkipped { get; private set; }
        public string LastCompactionSkipReason { get; private set; }
        private readonly Dictionary<string, int> _compactionSkipReasons = new Dictionary<string, int>();

        /// <summary>
        /// Observation: the history. How many times a registration's last-written frame was advanced: once for a
        /// registration and an adopted collection whose ordinary processing wrote a record of it.
        /// </summary>
        public long HistoryRegistrationWrites { get; private set; }

        /// <summary>The last compaction: its records, its registrations, the oldest and the newest last-written frame among them, the frame it was adopted in.</summary>
        public int LastCompactionRecords { get; private set; }
        public int LastCompactionRegistrations { get; private set; }
        public int LastCompactionOldestFrame { get; private set; }
        public int LastCompactionNewestFrame { get; private set; }
        public int LastCompactionFrame { get; private set; } = int.MinValue;

        /// <summary>
        /// The last compaction's cost in seconds: from the decision to the adoption (stages 3 to 8 of its collection),
        /// and of that the order (the sort and the layout), the candidate's build (stage 5: every live record written
        /// from the snapshot) and the upload (stage 7).
        /// </summary>
        public double LastCompactionSeconds { get; private set; }
        public double LastCompactionOrderSeconds { get; private set; }
        public double LastCompactionWriteSeconds { get; private set; }
        public double LastCompactionUploadSeconds { get; private set; }

        /// <summary>Tests only: the collection that decided to compact is refused right after, as if its room were short.</summary>
        internal bool FailAfterCompactionPlanForTest { get; set; }

        /// <summary>
        /// The frame of the last adopted collection whose ordinary processing wrote an instance record of a registered
        /// fragment; false when it is not registered or was never adopted.
        /// </summary>
        public bool TryGetLastWrittenFrame(LogicalFragmentId fragment, out int frame)
        {
            for (int g = 0; g < _shown.Count; g++)
            {
                if (_shown[g].fragment == fragment && _shown[g].lastWrittenFrame != int.MinValue)
                {
                    frame = _shown[g].lastWrittenFrame;
                    return true;
                }
            }

            frame = int.MinValue;
            return false;
        }

        /// <summary>
        /// What a compaction is expected to cost now, in seconds: the median of the last five measured, or the seed
        /// times the live records before any was.
        /// </summary>
        public double ExpectedCompactionSeconds
        {
            get
            {
                if (_compactionCostCount == 0)
                {
                    return _liveInstances * CompactionSeedSecondsPerRecord;
                }

                int n = Math.Min(_compactionCostCount, _compactionCosts.Length);
                var sorted = new double[n];
                Array.Copy(_compactionCosts, sorted, n);
                Array.Sort(sorted);
                return sorted[n / 2];
            }
        }

        /// <summary>The compaction's record, in words. Log text only.</summary>
        public string DescribeCompaction()
        {
            var reasons = new System.Text.StringBuilder();
            foreach (KeyValuePair<string, int> pair in _compactionSkipReasons)
            {
                reasons.Append(reasons.Length == 0 ? "" : ", ").Append(pair.Value).Append("x ").Append(pair.Key);
            }

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            return "compaction candidates " + CompactionCandidates + " (in " + CompactionCandidateCollections + " collections), run " + Compactions
                   + " (records laid out " + CompactionRecordsLaidOut + ", registrations whose records moved " + CompactionRegistrationsMoved
                   + "; the last at frame " + LastCompactionFrame + ": " + LastCompactionRegistrations + " registrations, " + LastCompactionRecords
                   + " records, last written in frames " + LastCompactionOldestFrame + ".." + LastCompactionNewestFrame + ", "
                   + (LastCompactionSeconds * 1000.0).ToString("F3", inv) + " ms of which the order " + (LastCompactionOrderSeconds * 1000.0).ToString("F3", inv)
                   + ", the build " + (LastCompactionWriteSeconds * 1000.0).ToString("F3", inv) + ", the upload " + (LastCompactionUploadSeconds * 1000.0).ToString("F3", inv)
                   + " ms; expected now " + (ExpectedCompactionSeconds * 1000.0).ToString("F3", inv) + " ms), candidates with nothing to move " + CompactionsWithoutChange
                   + ", candidate collections not run " + CompactionsSkipped + (reasons.Length > 0 ? " [" + reasons + "]" : "")
                   + "; history: registrations' ordinary writes adopted " + HistoryRegistrationWrites
                   + "; since the baseline: such writes " + _writesSinceCompaction + ", adopted collections " + (_adoptedPasses - _compactionBaselinePass) + " (interval "
                   + _compactionMinimumInterval + "); holes now " + (_instanceTail - _liveInstances) + " of " + _instanceTail + " records taken";
        }

        // This collection's ordinary processing writes an instance record of the registration: history when the
        // collection is adopted (D-202). Called where such a record is written, and where it would be were the
        // registration or the side not written whole in the same collection. No value is looked at.
        private void NoteWritten(Shown entry)
        {
            if (entry.notedPass != _slotPass)
            {
                entry.notedPass = _slotPass;
                _writtenThisPass.Add(entry);
            }
        }

        // The render fragments the placement pass placed anew, noted and nothing written: for a collection that
        // writes the side whole, where no record is written one by one -- the ordinary updates of that collection are
        // history all the same, and the rest of the side, written with what it held, is not.
        private void NotePlacedAnewWrites()
        {
            bool all = _building.AllRenderFragmentsPlacedAnew;
            int count = all ? _building.RenderFragmentCount : _building.PlacedAnewCount;
            for (int a = 0; a < count; a++)
            {
                _building.TryGetRenderFragment(all ? a : _building.PlacedAnewAt(a), out VpMultiCutRenderFragment rf);
                Shown entry = _shown[rf.registration];
                if (entry.slots.held)
                {
                    NoteWritten(entry);
                }
            }
        }

        // A collection whose structure stands: is a compaction a candidate, may this frame bear it, and would it move
        // anything? Nothing is written here; the regions are laid out again on the registrations (put back if the
        // collection is not adopted), and the tail brought down. False when the collection is to be refused for a test.
        private bool ConsiderCompaction()
        {
            int holes = _instanceTail - _liveInstances;
            if (_liveInstances == 0 || _adoptedPasses - _compactionBaselinePass < _compactionMinimumInterval
                || (_writesSinceCompaction == 0 && holes <= _compactionBaselineHoles))
            {
                return true;
            }

            if (!_candidateOpen)
            {
                _candidateOpen = true;
                CompactionCandidates++;
            }

            CompactionCandidateCollections++;
            CountingFrame();
            _countsNow.draw.compactionCandidates++;
            string reason = null;
            if (_candidateSlotsWhole)
            {
                reason = "the side is written whole anyway";
            }
            else if (RoomGrowths != _roomGrowthsAtPassStart || SnapshotRegrowths != _snapshotRegrowthsAtPassStart)
            {
                reason = "room was grown in this collection";
            }
            else if (Math.Max(_building.CapVertexCount, _building.CapCount * CapPlacementStride) > _capNormalBuffer.count || AnyCameraStencilSmallerThanRoom())
            {
                reason = "GPU room is needed in this collection";
            }

            if (reason == null && CompactionGate != null)
            {
                reason = CompactionGate(ExpectedCompactionSeconds);
            }

            if (reason != null)
            {
                // Skipped: the history and the baseline stand, and it is a candidate again in the next collection.
                CompactionsSkipped++;
                LastCompactionSkipReason = reason;
                _compactionSkipReasons.TryGetValue(reason, out int times);
                _compactionSkipReasons[reason] = times + 1;
                _countsNow.draw.compactionsSkipped++;
                return true;
            }

            // The order: the live registrations by the frame the ordinary processing last wrote a record of them, the
            // oldest first; among those of one frame, as they stand now, so that nothing moves for no reason.
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            int n = 0;
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                if (!entry.slots.held || entry.dropping || entry.renderFragments == 0)
                {
                    continue;
                }

                if (n == _orderKeys.Length)
                {
                    Array.Resize(ref _orderKeys, n * 2);
                    Array.Resize(ref _orderEntries, n * 2);
                }

                _orderKeys[n] = ((long)entry.lastWrittenFrame << 32) | (uint)entry.slots.instanceStart;
                _orderEntries[n] = entry;
                n++;
            }

            Array.Sort(_orderKeys, _orderEntries, 0, n);

            // Would any region move? Each is exactly what it draws, from where the order has come to.
            int tail = 0, moved = 0;
            for (int i = 0; i < n; i++)
            {
                Shown entry = _orderEntries[i];
                if (entry.slots.instanceStart != tail || entry.slots.instanceStride != entry.renderFragments)
                {
                    moved++;
                }

                tail += entry.commands.Length * entry.renderFragments;
            }

            if (moved == 0)
            {
                // Nothing to move: no copy and no transfer, and this state is not examined again until there is
                // something new -- an ordinary write adopted or a hole -- and the interval has passed again.
                CompactionsWithoutChange++;
                _compactionBaselinePass = _adoptedPasses + 1;   // counted from this collection, as after a compaction
                _compactionBaselineHoles = holes;
                _writesSinceCompaction = 0;
                _candidateOpen = false;
                Array.Clear(_orderEntries, 0, n);
                return true;
            }

            _compactionBegan = began;
            _compactThisPass = true;
            tail = 0;
            for (int i = 0; i < n; i++)
            {
                Shown entry = _orderEntries[i];
                Touch(entry);
                entry.slots.instanceStart = tail;
                entry.slots.instanceStride = entry.renderFragments;
                entry.rewrite = true;
                tail += entry.commands.Length * entry.renderFragments;
            }

            _instanceTail = tail;
            LastCompactionRecords = tail;
            LastCompactionRegistrations = n;
            LastCompactionOldestFrame = (int)(_orderKeys[0] >> 32);
            LastCompactionNewestFrame = (int)(_orderKeys[n - 1] >> 32);
            CompactionRegistrationsMoved += moved;
            LastCompactionOrderSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            Array.Clear(_orderEntries, 0, n);
            return !FailAfterCompactionPlanForTest;
        }

        private bool AnyCameraStencilSmallerThanRoom()
        {
            foreach (CameraStencil slot in _cameraStencils)
            {
                if (slot != null && IsSmallerThanRoom(slot.batch))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Observation, since this display was made: registrations whose state was read from the ledger; times the
        /// registration list was made again; times what each registration is drawn as was worked out; times a side's
        /// commands, materials, draw ranges and sides were assembled; instance records (a transform and a clip) written
        /// by a collection; instance records given to a side from the other before it was written; and cap-normal
        /// transfers with their vertices.
        /// </summary>
        public long LedgerStateReads { get; private set; }
        public long RegistrationListBuilds { get; private set; }
        public long DrawArrangements { get; private set; }
        public long CandidateAssemblies { get; private set; }
        public long InstanceRecordsWritten { get; private set; }
        public long InstanceRecordsCaughtUp { get; private set; }
        public long CapNormalTransfers { get; private set; }
        public long CapNormalVerticesTransferred { get; private set; }

        /// <summary>Observation: cap-vertex normals made again on the CPU (each cap's, a normal a vertex).</summary>
        public long CapNormalsMade { get; private set; }

        /// <summary>Observation: the body batches' buffer writes themselves (SetData calls), since this display was made.</summary>
        public long BodyArgumentSetDataCalls => _pastArgumentCalls + _batch.ArgumentSetDataCalls;
        public long BodyInstanceSetDataCalls => _pastInstanceCalls + _batch.InstanceSetDataCalls;

        /// <summary>Observation: of those, the writes of the instance records' range: the transforms' buffer and the clips', counted where each is made.</summary>
        public long BodyInstanceTransformSetDataCalls => _pastTransformCalls + _batch.InstanceTransformSetDataCalls;
        public long BodyInstanceClipSetDataCalls => _pastClipCalls + _batch.InstanceClipSetDataCalls;
        private long _pastTransformCalls, _pastClipCalls;

        /// <summary>Tests only: the cap normals' GPU buffer now drawn from.</summary>
        internal GraphicsBuffer CapNormalBufferForTest => _capNormalBuffer;

        /// <summary>
        /// Observation, since this display was made (the batches replaced by larger ones included): how many times the
        /// body's arguments were sent (one update: the forward and the shadow buffer) and how many commands in all; how
        /// many times its instances were (one update: the transforms and the clips, the same range of both) and how
        /// many instance records in all.
        /// </summary>
        public long BodyArgumentTransfers => _pastArgumentTransfers + _batch.ArgumentTransfers;
        public long BodyArgumentElementsTransferred => _pastArgumentElements + _batch.ArgumentElementsTransferred;
        public long BodyInstanceTransfers => _pastInstanceTransfers + _batch.InstanceTransfers;
        public long BodyInstanceElementsTransferred => _pastInstanceElements + _batch.InstanceElementsTransferred;
        private long _pastArgumentTransfers, _pastArgumentElements, _pastInstanceTransfers, _pastInstanceElements, _pastArgumentCalls, _pastInstanceCalls;

        /// <summary>Observation: snapshot builds that kept the whole structure, and builds that went through every registration.</summary>
        public long StructuresKeptWhole => _snapshot.StructuresKeptWhole + _building.StructuresKeptWhole;
        public long StructureWalks => _snapshot.StructureWalks + _building.StructureWalks;

        /// <summary>Tests only: every collection assembles the candidate whole and sends it whole, as before the keeping.</summary>
        internal bool collectEverythingForTest;

        /// <summary>Tests only: the body's batch now drawn from, and the adopted transform of one instance.</summary>
        internal VpIndexedIndirectDrawBatch BodyBatchForTest => _batch;
        internal Matrix4x4 InstanceTransformForTest(int instance)
        {
            EnsureIndexView();
            return _transforms[_viewInstanceRecords[instance]];
        }

        // The tables of the side being built are not ones to keep: its next collection makes them again.
        private void InvalidateCandidateSide()
        {
            _candidateSideLedger = -1;
        }

        // ----- a collection's hold on the draw slots ------------------------------------------------------------------------

        private void OpenSlotPass()
        {
            _slotPass++;
            _slotPassOpen = true;
            _slotTouched.Clear();
            _writtenCommands.Clear();
            _writtenInstances.Clear();
            _plannedCommandsBefore = _plannedCommands;
            _plannedInstancesBefore = _plannedInstances;
            _commandTailBefore = _commandTail;
            _instanceTailBefore = _instanceTail;
            _compactThisPass = false;
            _writtenThisPass.Clear();
            _roomGrowthsAtPassStart = RoomGrowths;
            _snapshotRegrowthsAtPassStart = SnapshotRegrowths;
        }

        // The first time this collection changes a registration's slots: what they were is kept, to be put back.
        private void Touch(Shown entry)
        {
            if (entry.slotPass == _slotPass)
            {
                return;
            }

            entry.slotPass = _slotPass;
            entry.slotsBefore = entry.slots;
            entry.rewrite = false;
            entry.releasing = false;
            _slotTouched.Add(entry);
        }

        // A registration drawn no more: its commands are made to draw nothing by this collection, and it holds no
        // slots from here on. They are not taken again.
        private void PlanRelease(Shown entry)
        {
            if (!entry.slots.held)
            {
                return;
            }

            Touch(entry);
            int commands = entry.commands.Length;
            _plannedCommands -= commands;
            _plannedInstances -= commands * entry.slots.renderFragments;
            entry.slots = default;
            entry.releasing = true;
        }

        // What the registrations that are shown and hold no slots yet will take at the end when they are first drawn:
        // a command slot a command, and at least an instance record a command.
        private long WaitingForSlots()
        {
            long waiting = 0;
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                if (!entry.slots.held && !entry.dropping)
                {
                    waiting += entry.commands.Length;
                }
            }

            return waiting;
        }

        /// <summary>
        /// Stage 3 of a collection whose structure is another: which registrations' slots this collection writes, with
        /// the slots a registration lacks taken. A registration is written when it holds none, when it is drawn as
        /// another number of render fragments or its clipped state is another, or when the structure the snapshot
        /// settled for it is not the one its slots were written for -- which the snapshot says by the part it shares
        /// between builds, so nothing is compared. Everything else stands: no slot of it is touched.
        /// <para>
        /// A registration first drawn takes a run of command slots, one a command, and an instance region of exactly
        /// what it draws, both at the end. One that draws more render fragments than its region has room for takes
        /// a new region at the end, its own alone; its commands stay where they are and name the new region. One that
        /// draws as many as its region has room for, or fewer, is written where it stands and keeps the room it has.
        /// The slots of what is drawn no more, and a region left for a larger one, are not taken by anything again.
        /// Nothing is written here.
        /// </para>
        /// False when the end would pass a limit (the caller puts back what was taken).
        /// </summary>
        private bool TryPlanSlots(out string shortOf, out long needed, out string failure)
        {
            shortOf = null;
            needed = 0;
            failure = null;
            for (int i = 0; i < _retiring.Count; i++)
            {
                PlanRelease(_retiring[i]);
            }

            for (int i = 0; i < _unshownWithSlots.Count; i++)
            {
                PlanRelease(_unshownWithSlots[i]);
            }

            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                int commands = entry.commands.Length;
                if (entry.renderFragments == 0 || commands == 0)
                {
                    PlanRelease(entry);
                    continue;
                }

                object part = _building.StructurePartAt(g, out long serial);
                bool another = !entry.slots.held || entry.slots.renderFragments != entry.renderFragments
                    || entry.slots.clipped != entry.clipped;
                if (!another && part != null && ReferenceEquals(entry.slots.part, part) && entry.slots.partSerial == serial)
                {
                    continue;
                }

                Touch(entry);
                if (!entry.slots.held)
                {
                    if (_commandTail > _limits.commands - commands)
                    {
                        shortOf = "draw commands";
                        needed = (long)_commandTail + commands;
                        failure = "no room at the end for " + commands + " command slots: " + _commandTail + " of "
                                  + _limits.commands + " were taken since the display was made (" + _plannedCommands
                                  + " of them draw; a slot is not taken again)";
                        return false;
                    }

                    entry.slots.commandStart = _commandTail;
                    entry.slots.instanceStride = 0;
                    _commandTail += commands;
                    _plannedCommands += commands;
                }

                if (entry.renderFragments > entry.slots.instanceStride)
                {
                    int records = checked(commands * entry.renderFragments);
                    if (_instanceTail > _limits.instances - records)
                    {
                        shortOf = "draw instances";
                        needed = (long)_instanceTail + records;
                        failure = "no room at the end for " + records + " instance records: " + _instanceTail + " of "
                                  + _limits.instances + " were taken since the display was made (" + _plannedInstances
                                  + " of them are drawn; a record is not taken again)";
                        return false;
                    }

                    RegionsTaken++;
                    if (entry.slots.held)
                    {
                        RegionsMoved++;
                        InstanceRecordsMoved += commands * entry.slots.renderFragments;
                    }

                    entry.slots.instanceStart = _instanceTail;
                    entry.slots.instanceStride = entry.renderFragments;
                    _instanceTail += records;
                }

                _plannedInstances += commands * (entry.renderFragments - (entry.slots.held ? entry.slots.renderFragments : 0));
                entry.slots.held = true;
                entry.slots.renderFragments = entry.renderFragments;
                entry.slots.clipped = entry.clipped;
                entry.slots.part = part;
                entry.slots.partSerial = serial;
                entry.rewrite = true;
            }

            return true;
        }

        // The collection was not adopted: the registrations hold the slots they held, the two ends are where they
        // were -- what it took there was never published, and is taken by the next collection -- and what it wrote
        // into the side it built in is what that side now lacks of the adopted one.
        private void PutSlotsBack()
        {
            for (int i = 0; i < _slotTouched.Count; i++)
            {
                Shown entry = _slotTouched[i];
                entry.slots = entry.slotsBefore;
                entry.slotsBefore = default;
                entry.slotPass = -1;
                entry.rewrite = false;
                entry.releasing = false;
            }

            _slotTouched.Clear();
            _compactThisPass = false;
            _writtenThisPass.Clear();   // not adopted: no history of it, and the baseline stands
            _commandTail = _commandTailBefore;
            _instanceTail = _instanceTailBefore;
            _plannedCommands = _plannedCommandsBefore;
            _plannedInstances = _plannedInstancesBefore;
            _lackingCommands.AddAll(_writtenCommands);
            _lackingInstances.Add(_writtenInstances);   // with what it lacked before, if a compaction left that uncopied
            _writtenCommands.Clear();
            _writtenInstances.Clear();
            _slotPassOpen = false;

            // What the registrations are drawn as was worked out for a collection that planned slots it no longer
            // has: the next one works it out and plans again, whatever it is settled from.
            _drawnLedger = -1;
        }

        // The collection was adopted: the slots are as it planned them, and what it wrote is what the other side --
        // the one the next collection builds in -- lacks.
        private void KeepSlots()
        {
            for (int i = 0; i < _slotTouched.Count; i++)
            {
                Shown entry = _slotTouched[i];
                entry.slotsBefore = default;
                entry.rewrite = false;
                entry.releasing = false;
            }

            // The placements adopted are of the counts taken when they were asked (D-204); a collection that let the
            // adopted snapshot stand changes nothing of that -- except one that stood it for a new step because the
            // pass would have had no target (2026-10-08): that step's placements are the adopted ones, settled by the
            // fixed and held ones' standing, and are taken as placed at these counts.
            if (!_placementReused || _placementPassSkipped)
            {
                _placedSerialKnown = _buildSerialKnown;
                _placedStep = _buildStep;
                _placedOutside = _buildOutside;
            }

            // The history (D-202): what this collection's ordinary processing wrote is adopted with it, and only then.
            _adoptedPasses++;
            int adoptedFrame = CurrentFrame;
            int written = _writtenThisPass.Count;
            for (int i = 0; i < written; i++)
            {
                _writtenThisPass[i].lastWrittenFrame = adoptedFrame;
            }

            _writtenThisPass.Clear();
            HistoryRegistrationWrites += written;
            _writesSinceCompaction += written;
            if (written > 0)
            {
                CountingFrame();
                _countsNow.draw.historyWrites += written;
            }

            if (_compactThisPass)
            {
                double frequency = System.Diagnostics.Stopwatch.Frequency;
                double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - _compactionBegan) / frequency;
                _compactionCosts[_compactionCostCount % _compactionCosts.Length] = seconds;
                _compactionCostCount++;
                LastCompactionSeconds = seconds;
                LastCompactionWriteSeconds = (_compactionStageAt[6] - _compactionStageAt[5]) / frequency;
                LastCompactionUploadSeconds = (_compactionStageAt[8] - _compactionStageAt[7]) / frequency;
                LastCompactionFrame = adoptedFrame;
                Compactions++;
                CompactionRecordsLaidOut += LastCompactionRecords;
                CountingFrame();
                _countsNow.draw.compactions++;
                _countsNow.draw.compactionRecordsMoved += LastCompactionRecords;
                _countsNow.draw.compactionMilliseconds += seconds * 1000.0;
                _countsNow.draw.compactionOrderMilliseconds += LastCompactionOrderSeconds * 1000.0;
                _countsNow.draw.compactionWriteMilliseconds += LastCompactionWriteSeconds * 1000.0;
                _countsNow.draw.compactionUploadMilliseconds += LastCompactionUploadSeconds * 1000.0;
                _compactThisPass = false;

                // The baseline of the next candidate. What this very collection's ordinary processing wrote was laid
                // out by the history it had before, so it stays pending; the compaction's own rewrite is not among it.
                _compactionBaselinePass = _adoptedPasses;
                _compactionBaselineHoles = 0;
                _writesSinceCompaction = written;
                _candidateOpen = false;
            }

            _slotTouched.Clear();
            _unshownWithSlots.Clear();   // each was let go by this collection
            Swap(ref _lackingCommands, ref _writtenCommands);
            _lackingInstances = _writtenInstances;   // a compaction's range covers every live record, so nothing older is owed
            _writtenCommands.Clear();
            _writtenInstances.Clear();
            _candidateSlotsWhole = false;
            _slotPassOpen = false;
        }

        // One registration's state, read from the ledger, changing nothing there.
        private void ReadLedgerState(Shown entry, out LogicalFragmentId family)
        {
            LedgerStateReads++;
            bool known = _ledger.TryGetFragmentState(entry.fragment, out LogicalFragmentState state);
            // Let go when retired, or when replaced by cuts whose every piece has been retired before a geometry
            // commit took the registration over (DESIGN 4.5.3, 7.10): nothing of it can be drawn or cut again, and
            // holding it would hold its lineage's room for good. One whose pieces still live is kept, as before.
            _ledger.TryGetFamily(entry.fragment, out family, out long familyRevision);
            entry.dropping = !known || state == LogicalFragmentState.Retired
                || (state == LogicalFragmentState.Replaced && !HasLiveDescendant(entry.fragment));
            entry.readFamilyRevision = familyRevision;
            entry.readFragment = entry.fragment;
            entry.awaiting = known && state == LogicalFragmentState.Live
                && _ledger.TryGetActiveOperation(entry.fragment, out CutOperationId active)
                && !_ledger.TryGetPreparedAnchorDistribution(active, out _);
        }

        // Every registration read, the registration list made again, and the families' chains with it: when this
        // display's own inputs changed (the list is of them), at the first collection, and when the ledger's notice
        // does not reach back far enough.
        private void ReadEveryRegistration()
        {
            RegistrationListBuilds++;
            _registrations.Clear();
            _familyHead.Clear();
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                ReadLedgerState(entry, out LogicalFragmentId family);
                entry.familyNext = -1;
                if (family.IsSet)
                {
                    if (_familyHead.TryGetValue(family, out int head))
                    {
                        entry.familyNext = head;
                    }

                    _familyHead[family] = g;
                }

                _registrations.Add(new VpMultiCutRegistration(
                    entry.fragment, entry.localBounds, entry.objectToWorld, entry.lineageToGeometryLocal, entry.reflected,
                    VpCapBoundsPolygon.EpsilonFor(entry.localBounds)));
            }
        }

        // Only the registrations of the families the ledger's notice names; the registration list stands.
        private void ReadChangedFamilies()
        {
            LogicalFragmentId last = default;
            for (int i = 0; i < _changedFamilies.Count; i++)
            {
                LogicalFragmentId family = _changedFamilies[i];
                if (family == last)
                {
                    continue;   // named again by the very next change
                }

                last = family;
                if (!_familyHead.TryGetValue(family, out int g))
                {
                    continue;   // no registration of this display is of that family
                }

                for (; g >= 0; g = _shown[g].familyNext)
                {
                    ReadLedgerState(_shown[g], out _);
                }
            }
        }

        private bool TryCollectAndUpload()
        {
            long builds = StructureBuilds, validations = StructureValidations, placements = PlacementPasses;
            VpDrawDataCounts drawBefore = DrawTotals();
            VpSnapshotStageTotals stagesBefore = SumStages();
            VpHeldPlacementTotals holdsBefore = _holds.Totals;
            _placementReused = false;
            _placementPassSkipped = false;
            _collectBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            OpenSlotPass();
            SumValidate(_validateBefore);
            SumPlace(_placeStructuralBefore, _placePlacementOnlyBefore);
            try
            {
                return TryCollectAndUploadStages();
            }
            catch (OutOfMemoryException exception)
            {
                return FailRoom("snapshot structure", _shown.Count, _fragmentCapacity, _fragmentLimit,
                    "memory could not be had: " + exception.Message);
            }
            finally
            {
                long ended = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_collectStage >= 0)
                {
                    s_collectStages[_collectStage].End();
                    _stageSeconds[_collectStage] += (ended - _stageBegan) * s_secondsPerTick;
                }

                _collectStage = -1;

                // A collection that was not adopted leaves the draw slots as the adopted side has them -- and the
                // adopted snapshot where it was, if it stood as the one built (D-204).
                if (_slotPassOpen)
                {
                    if (_placementReused)
                    {
                        Swap(ref _snapshot, ref _building);
                    }

                    PutSlotsBack();
                }

                _placementReused = false;
                _placementPassSkipped = false;

                CountCollection(StructureBuilds - builds, StructureValidations - validations, PlacementPasses - placements);

                // The collection's own time, whole (to here: the counting below is not in it) and by stage, and the
                // snapshot's stages (2026-10-07).
                _countsNow.times.Add((ended - _collectBegan) * s_secondsPerTick, _stageSeconds, _buildCalls, _buildSeconds, _buildHeapDelta);
                Array.Clear(_stageSeconds, 0, _stageSeconds.Length);
                _buildCalls = 0;
                _buildSeconds = 0.0;
                _buildHeapDelta = 0;
                VpSnapshotStageTotals stagesAfter = SumStages();
                stagesAfter.Subtract(stagesBefore);

                // The cameras are sent the local cap vertices where they are prepared -- between collections, never
                // within one, so the difference above holds none. This collection's record tells what they were sent
                // since the collection before it (the layout that one adopted), from the display's running totals.
                stagesAfter.cameraCapUploads = CameraCapLayoutUploads - _cameraCapUploadsTold;
                stagesAfter.cameraCapVertices = CameraCapLayoutVertices - _cameraCapVerticesTold;
                stagesAfter.cameraCapFirstUploads = CameraCapLayoutFirstUploads - _cameraCapFirstUploadsTold;
                stagesAfter.cameraCapFirstVertices = CameraCapLayoutFirstVertices - _cameraCapFirstVerticesTold;
                _cameraCapUploadsTold = CameraCapLayoutUploads;
                _cameraCapVerticesTold = CameraCapLayoutVertices;
                _cameraCapFirstUploadsTold = CameraCapLayoutFirstUploads;
                _cameraCapFirstVerticesTold = CameraCapLayoutFirstVertices;
                _countsNow.stages.Add(stagesAfter);
                VpHeldPlacementTotals holdsAfter = _holds.Totals;
                holdsAfter.Subtract(holdsBefore);
                _countsNow.holds.Add(holdsAfter);
                CountDraw(drawBefore);
                SumValidate(_validateAfter);
                _countsNow.validate.AddDifference(_validateAfter, _validateBefore);
                SumPlace(_placeStructuralAfter, _placePlacementOnlyAfter);
                _countsNow.placeStructural.AddDifference(_placeStructuralAfter, _placeStructuralBefore);
                _countsNow.placePlacementOnly.AddDifference(_placePlacementOnlyAfter, _placePlacementOnlyBefore);
            }
        }

        private bool TryCollectAndUploadStages()
        {
            EnterCollectStage(0);
            // 0. The room this collection builds in is the room grown so far: what adoption traded back from the side
            //    last drawn may be smaller, and nothing draws from it now.
            if (!TryEnsureCandidateRoom(true, out string roomFailure))
            {
                return FailRoom("candidate room", _instanceCapacity, _instanceCapacity, _limits.instances, roomFailure);
            }

            EnterCollectStage(1);
            // 1. What each registration is now, read from the ledger, changing nothing. What was read is kept: the
            //    ledger says which families changed since (LogicalCutLedger.TryReadChangedFamilies), and only their
            //    registrations are read again; a change of this display's own inputs reads every one again and makes
            //    the registration list again. No registration is asked anything to find out whether something changed.
            long ledgerRevision = _ledger.Revision;
            bool readEvery = collectEverythingForTest || _readInputRevision != _inputRevision || _readLedgerRevision < 0;
            if (!readEvery && _readLedgerRevision != ledgerRevision)
            {
                _changedFamilies.Clear();
                readEvery = !_ledger.TryReadChangedFamilies(_readLedgerRevision, _changedFamilies);
            }

            if (readEvery)
            {
                ReadEveryRegistration();
            }
            else if (_readLedgerRevision != ledgerRevision)
            {
                ReadChangedFamilies();
            }

            _readInputRevision = _inputRevision;
            _readLedgerRevision = ledgerRevision;
            long stampLedger = collectEverythingForTest ? -1 : ledgerRevision;

            EnterCollectStage(2);
            // 2. One snapshot of every registration together, beside the adopted one. Room short is grown below, or
            //    told when it cannot be; anything else stops the display, decided here before this frame draws.
            //    <para>
            //    The structure -- the ledger's own validity, the lineage, the candidates, what is Selected and what is
            //    Ignored, and how the Ignored are grouped -- is settled again only when something it was settled from
            //    has changed. Where things stand is settled every time, because that is what moving an actor changes.
            //    A structure that cannot be taken over, for whatever reason, is settled again: taking it over is never
            //    what decides whether a frame is right.
            //    </para>
            if (!TryGrowForRegistrations(out string registrationsFailure))
            {
                return FailRoom("room for the registrations", _shown.Count, _fragmentCapacity, _fragmentLimit, registrationsFailure);
            }

            // Where things stand, asked only when it can have changed (D-204): the host's counts as they were at the
            // last adopted placement pass, and the adopted snapshot of the structure as it is now.
            _buildSerialKnown = _placementSerial != null && _placementSerial(out _buildStep, out _buildOutside);
            if (_buildSerialKnown && _placedSerialKnown && _buildStep == _placedStep && _buildOutside == _placedOutside && _hasSnapshot
                && _snapshot.IsOfStructure(_structurePool, _registrations.Count, stampLedger, _inputRevision))
            {
                // The adopted snapshot stands as the one built for this collection; the adoption changes them back.
                Swap(ref _snapshot, ref _building);
                _building.NotePlacementsReused();
                _placementReused = true;
                PlacementReuses++;
                PlacementQueriesOmitted += _building.RenderFragmentCount;
            }

            if (!_placementReused)
            {
                // The snapshot's build, timed apart from the rest of this stage, with the managed heap's change across it
                // (2026-10-07, for observation: what the build took of the heap, less what a collector freed meanwhile).
                // Who is asked (D-205): the host's reference points, when it vouches for its step count.
                _proximityPoints.Clear();
                _proximityCameras.Clear();
                bool holding = _buildSerialKnown && (_placementProximityViews != null || _placementProximity != null);
                if (holding)
                {
                    if (_placementProximityViews != null) _placementProximityViews(_proximityPoints, _proximityCameras);
                    else _placementProximity(_proximityPoints);
                }

                // What changed outside a step (D-207): the families of the fragments told. A change told with no target,
                // more changes than the source keeps, or a fragment that is not this ledger's has every held one asked.
                // With no source every change counts as one with no target.
                long untargeted = _buildOutside;
                if (holding && _placementChanges != null)
                {
                    _changedFragments.Clear();
                    if (!_placementChanges(ref _changesCursor, _changedFragments, out untargeted)) _holds.TellAll();
                    for (int i = 0; i < _changedFragments.Count; i++)
                    {
                        if (_ledger.TryGetFamily(_changedFragments[i], out LogicalFragmentId family, out long _)) _holds.Tell(family);
                        else _holds.TellAll();
                    }
                }

                _holds.SetPass(holding, _buildStep, untargeted, _proximityPoints);

                // Each reference point's view (D-210): the one its camera was rendered with in the frame before -- this
                // frame's is not made yet when a collection runs -- or in this frame already. A point with no camera,
                // or whose camera's view was not noted then, has none: its proximity box alone decides for it. (This
                // is done only by a collection that asks placements: one that lets the adopted snapshot stand does not
                // come here, whatever its cameras' views did.)
                if (holding)
                {
                    int frame = CurrentFrame;
                    for (int i = 0; i < _proximityCameras.Count && i < _proximityPoints.Count; i++)
                    {
                        Camera of = _proximityCameras[i];
                        if (ReferenceEquals(of, null)) continue;
                        CameraStencil slot = FindCamera(of);
                        if (slot != null && slot.viewEyes > 0 && (slot.viewFrame == frame || slot.viewFrame == frame - 1))
                        {
                            _holds.SetView(i, slot.viewEyes, slot.viewPlanes);
                        }
                    }
                }

                // A pass whose targets are none (TL, 2026-10-08): with the structure kept whole, the pass over it would
                // ask the ordinary render fragments, the held ones near a camera and in its view, and the ones told
                // of -- and when there are none of these (every one fixed, its lookup vouching that a step does not
                // move it, or held and far; nothing told) it would settle every record as the adopted snapshot has it.
                // So the adopted snapshot stands, as for a collection with no new step (D-204): no record is reset, no
                // target picked twice (the pass's targets are picked here, once, before the build). The step's counts
                // are taken as placed at the adoption (KeepSlots). A pass with any target runs whole below: the fixed
                // ones are not asked in it, the others are. A lookup's guarantee alone makes a fragment fixed; a
                // dynamic one that stood twice is held, and asked when near.
                if (holding && _hasSnapshot && _building.WouldKeepStructure(_structurePool, _snapshot, _registrations.Count, stampLedger, _inputRevision)
                    && _holds.BeginKept(_snapshot.RenderFragmentCount, stampLedger, _inputRevision) && _holds.PickedCount == 0)
                {
                    _holds.PassEnded();
                    Swap(ref _snapshot, ref _building);
                    _building.NotePlacementsReused();
                    _placementReused = true;
                    _placementPassSkipped = true;
                    PlacementPassesSkipped++;
                    PlacementQueriesOmitted += _building.RenderFragmentCount;
                    CountingFrame();
                    _countsNow.statics.passesSkipped++;
                }
            }

            if (!_placementReused)
            {
                long heapBefore = GC.GetTotalMemory(false);
                long buildBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                VpMultiCutBuildOutcome outcome = _building.TryBuildIncremental(
                    _structurePool, _snapshot, _ledger, _registrations, Placement, stampLedger, _inputRevision, _holds);
                _buildCalls++;
                _buildSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - buildBegan) * s_secondsPerTick;

                // A shortage grows the count the snapshot named and builds again. Every growth at least doubles a count
                // below its limit, or takes it to its limit, and a count at its limit is not grown but told -- so the builds
                // are bounded by what the room and the limits allow, worked out here, and never by a number of their own.
                int growthsLeft = GrowthsToLimits();
                for (int growths = 0; outcome == VpMultiCutBuildOutcome.CapacityExceeded; growths++)
                {
                    CapPolygonBuilds += _building.SectionBuildCount;
                    VpMultiCutShortage shortage = _building.Shortage;
                    if (growths > growthsLeft)
                    {
                        // Not reachable while every growth takes at least one step: said, not looped on.
                        return FailRoom(ShortageName(shortage), -1, HeldFor(shortage), LimitFor(shortage),
                            "more growths than the limits allow (" + growthsLeft + ")");
                    }

                    if (!TryGrowFor(shortage, out string failure))
                    {
                        return FailRoom(ShortageName(shortage), -1, HeldFor(shortage), LimitFor(shortage), failure);
                    }

                    buildBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                    outcome = _building.TryBuildIncremental(
                        _structurePool, _snapshot, _ledger, _registrations, Placement, stampLedger, _inputRevision, _holds);
                    _buildCalls++;
                    _buildSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - buildBegan) * s_secondsPerTick;
                }

                _buildHeapDelta += GC.GetTotalMemory(false) - heapBefore;
                CapPolygonBuilds += _building.SectionBuildCount;

                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    if (!_halted && outcome == VpMultiCutBuildOutcome.InvalidInput)
                    {
                        _haltInvalidInput = _building.InvalidInputReason;
                    }

                    Halt(ReasonOf(outcome));
                    return false;
                }

            }

            EnterCollectStage(3);
            // 3. What each registration is drawn as; the commands and instances that takes. All of it follows from the
            //    structure -- which render fragments a registration has, whether any carries a plane (as many planes as
            //    it has selected boundaries), whether it is split -- so it is worked out again only when what a
            //    structure is settled from has changed, and stands otherwise.
            bool structural = !(stampLedger >= 0 && _drawnLedger == stampLedger && _drawnInputs == _inputRevision);
            if (structural || collectEverythingForTest)
            {
                _capLayoutStale = true;   // the caps' shapes and their order follow from the structure (D-208)
            }
            if (structural)
            {
                DrawArrangements++;
                _drawnLedger = -1;
                for (int g = 0; g < _shown.Count; g++)
                {
                    Shown entry = _shown[g];
                    entry.split = false;
                    entry.clipped = false;
                    entry.renderFragments = 0;
                    entry.firstRenderFragment = 0;
                    entry.takenThisPass = 0;
                }

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

                for (int g = 0; g < _shown.Count; g++)
                {
                    Shown entry = _shown[g];
                    entry.split |= entry.renderFragments > 1;
                }

                // The draw slots (DESIGN 5.6): which registrations this collection writes, the slots they lack taken.
                // Nothing is written yet, and nothing of the adopted side is touched.
                if (!TryPlanSlots(out string shortOf, out long slotsNeeded, out string slotFailure))
                {
                    bool commandsShort = shortOf == "draw commands";
                    return FailRoom(
                        shortOf, slotsNeeded, commandsShort ? _commandCapacity : _instanceCapacity,
                        commandsShort ? _limits.commands : _limits.instances, slotFailure);
                }

                _drawnInputs = _inputRevision;
                _drawnLedger = stampLedger;
            }
            else if (!ConsiderCompaction())
            {
                // The compaction of the instance regions (DESIGN 5.6, D-202): a collection whose structure stands may
                // lay the live regions out again. Refused here only for a test.
                return FailRoom("compaction", _instanceTail, _instanceCapacity, _limits.instances, "refused after the plan, for a test");
            }

            // Capacity, decided before anything is written or uploaded, and grown to fit when short -- the snapshot just
            // built is kept as it is. The draw slots' room reaches to where they end: everything ever taken, drawn or
            // not. The stencil side's room follows from the render fragments, not from these: no more volume commands
            // than eight per render fragment, no more caps than the snapshot holds. The draw-range table holds one
            // entry per registration and grows with the render fragments, which are never fewer than the
            // registrations; it is asked here all the same, before anything is taken, rather than found out while the
            // candidate is built.
            int commandEnd = _commandTail;
            int instanceEnd = _instanceTail;
            if (commandEnd > _commandCapacity && !TryGrow(RoomKind.Commands, commandEnd, false, out string commandFailure))
            {
                return FailRoom("draw commands", commandEnd, _commandCapacity, _limits.commands, commandFailure);
            }

            if (instanceEnd > _instanceCapacity
                && !TryGrow(RoomKind.Instances, instanceEnd, false, out string instanceFailure))
            {
                return FailRoom("draw instances", instanceEnd, _instanceCapacity, _limits.instances, instanceFailure);
            }

            if (_shown.Count > _fragmentCapacity
                && !TryGrow(RoomKind.Fragments, _shown.Count, false, out string fragmentFailure))
            {
                return FailRoom("render fragments", _shown.Count, _fragmentCapacity, _fragmentLimit, fragmentFailure);
            }

            if (_shown.Count > _candidateGeometries.Capacity)
            {
                return FailRoom("draw-range table", _shown.Count, _candidateGeometries.Capacity, _fragmentLimit,
                    "the table was not grown with the render fragments");
            }

            EnterCollectStage(4);
            // 4. The display instances the new snapshot needs that are not held yet. A failure gives back what this
            //    pass took, and nothing already held is given back early to make room; the table grows to its limit
            //    by itself, so a failure here is past it.
            //    <para>
            //    How many a registration needs is one a render fragment, which follows from the structure alone; a
            //    collection whose structure stands, after one whose release pass (9) left every registration holding
            //    exactly that many, takes none and trims none (TL, 2026-10-08). Anything that changes the need -- a
            //    registration shown or retired, a cut, a commit -- changes the ledger or this display's inputs, so the
            //    collection is structural and goes through every registration as before.
            //    </para>
            bool instancesStand = !structural && _instancesSettled && !collectEverythingForTest;
            CountingFrame();
            if (instancesStand)
            {
                InstanceTakesSkipped++;
                _countsNow.statics.instanceTakesSkipped++;
            }
            else
            {
                _instancesSettled = false;
                long takeBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                bool taken = TryTakeInstances();
                InstanceTakes++;
                _countsNow.statics.instanceTakes++;
                _countsNow.statics.instanceTakeSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - takeBegan) * s_secondsPerTick;
                if (!taken)
                {
                    return FailRoom("display instances", -1, _table.DisplayInstanceCapacity, _table.DisplayInstanceLimit,
                        _table.DescribeRoom());
                }
            }

            EnterCollectStage(5);
            // 5. The candidate, beside the adopted draw data: first given what the adopted side was written over and
            //    it was not; then the registrations whose slots this collection writes, and the instance records of
            //    what the placement pass placed anew. Every other slot stands as it is.
            BuildCandidate(stampLedger, structural, commandEnd, instanceEnd);

            EnterCollectStage(6);
            // 6. The largest stencil arrangement this candidate could need -- every non-empty cap a job of its own, its
            //    own-face volume and its fan, in one colour -- asked of the fixed sizes and of every registered camera's
            //    batch, by count, before anything is written. A judgement of capacity and form, not a promise of each
            //    later upload, and not an approval of any sharing.
            BuildLargestStencilArrangement(out int stencilCommands, out int capIndexCount, out int stencilColours);
            int capVertices = _building.CapVertexCount;
            bool stencilFits = stencilCommands <= _stencilCommandCapacity
                && capVertices <= _stencilCapVertexCapacity
                && capIndexCount <= _stencilCapIndexCapacity;

            // A camera's batch is filled again by every preparation, and this frame has prepared none yet, so a batch
            // smaller than the room is replaced now; the one replaced is released once the GPU is past it. The body's
            // batch and the cap normals are still drawn from until adoption: larger ones are made here, written in 7,
            // and switched to in 8.
            VpIndexedIndirectDrawBatch grownBatch = null;
            GraphicsBuffer grownNormals = null;
            if (stencilFits && !TryMakeGpuRoomForCandidate(
                    commandEnd, instanceEnd, capVertices, out grownBatch, out grownNormals, out string gpuFailure))
            {
                GiveBackInstancesTakenThisPass();
                InvalidateCandidateSide();
                return FailRoom("GPU buffers", instanceEnd, _batch.InstanceCapacity, _limits.instances, gpuFailure);
            }

            VpIndexedIndirectDrawBatch bodyBatch = grownBatch ?? _batch;
            if (stencilFits)
            {
                foreach (CameraStencil slot in _cameraStencils)
                {
                    if (slot != null && !slot.batch.CanUpload(
                            _candidateStencilCommands.Valid, stencilCommands, _candidateStencilTransforms.Valid,
                            _candidateStencilClips.Valid, _building.CapVertexRoom.Valid, capVertices, _candidateCapIndices.Valid,
                            capIndexCount, _candidateStencilColors, stencilColours))
                    {
                        stencilFits = false;
                        break;
                    }
                }
            }

            // The body batch is asked too, by the very ends it would be sent, before anything is written.
            if (stencilFits && !bodyBatch.CanUploadSlots(
                    _candidateCommands.Valid, _candidateCommandStarts.Valid, commandEnd, _candidateTransforms.Valid,
                    _candidateClips.Valid, instanceEnd))
            {
                stencilFits = false;
            }

            if (!stencilFits)
            {
                // Nothing drew from what was made a moment ago.
                grownBatch?.Dispose();
                grownNormals?.Dispose();
                GiveBackInstancesTakenThisPass();
                InvalidateCandidateSide();
                return FailRoom("stencil arrangement", stencilCommands, _stencilCommandCapacity, _stencilCommandCapacity,
                    "cap vertices " + capVertices + " of " + _stencilCapVertexCapacity + ", cap indices " + capIndexCount
                    + " of " + _stencilCapIndexCapacity + ", or a batch refused the counts");
            }

            EnterCollectStage(7);
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

                // A larger buffer made a moment ago holds nothing yet: it takes every cap's normals.
                UploadCapNormals(_building, grownNormals ?? _capNormalBuffer, grownNormals != null || collectEverythingForTest);

                // Only what this collection wrote goes: the ranges of commands and of instance records recorded as
                // they were written. Everything outside them is on the GPU already, as the adopted side has it. A batch
                // never sent to -- one that takes a smaller one's place -- and another stereo condition take everything
                // below the two ends instead, which the batch counts apart.
                uploaded = RefuseBodyUploadForTest != null && RefuseBodyUploadForTest()
                    ? false
                    : bodyBatch.TryUploadSlots(
                        _candidateCommands.Valid, _candidateCommandStarts.Valid, commandEnd, _candidateTransforms.Valid,
                        _candidateClips.Valid, instanceEnd, singlePassInstanced, _writtenCommands, _writtenInstances.start, _writtenInstances.end);
            }
            catch
            {
                _broken = true;
                InvalidateCandidateSide();
                grownBatch?.Dispose();
                grownNormals?.Dispose();
                throw;
            }

            if (!uploaded)
            {
                _broken = true;
                InvalidateCandidateSide();
                grownBatch?.Dispose();
                grownNormals?.Dispose();
                throw new InvalidOperationException(
                    "the display batch refused an upload it had accepted by the same counts a moment before; this display "
                    + "stops");
            }

            // The larger GPU objects hold the whole candidate now: they are switched to here, at the adoption below, and
            // the ones they replace are kept until the GPU is past them.
            if (grownNormals != null)
            {
                RetireLater(_capNormalBuffer, _capNormalBuffer);
                _capNormalBuffer = grownNormals;
                for (int c = 0; c < _stencilMaterials.ColorCount; c++)
                {
                    _stencilMaterials.Cap(c).SetBuffer(CapNormalsId, _capNormalBuffer);
                    _stencilMaterials.Cap(c).SetBuffer(CapPlacementsId, _capNormalBuffer);
                }
            }

            if (grownBatch != null)
            {
                RetireLater(new RetiredBatch(_batch, this), _batch.RetirementFence);
                _pastArgumentTransfers += _batch.ArgumentTransfers;
                _pastArgumentElements += _batch.ArgumentElementsTransferred;
                _pastInstanceTransfers += _batch.InstanceTransfers;
                _pastInstanceElements += _batch.InstanceElementsTransferred;
                _pastArgumentCalls += _batch.ArgumentSetDataCalls;
                _pastInstanceCalls += _batch.InstanceSetDataCalls;
                _pastTransformCalls += _batch.InstanceTransformSetDataCalls;
                _pastClipCalls += _batch.InstanceClipSetDataCalls;
                _pastWholeArguments += _batch.WholeArgumentElementsTransferred;
                _pastWholeInstances += _batch.WholeInstanceElementsTransferred;
                _pastWholeCalls += _batch.WholeSetDataCalls;
                _pastCullDispatches += _batch.CullDispatches;
                _batch = grownBatch;
            }

            EnterCollectStage(8);
            // 8. Only the complete candidate is adopted. Shared family results stay owned by both snapshots
            //    until the old reader is cleared on the next build; GPU resources keep their existing retirement.
            Adopt(commandEnd, instanceEnd);
            CommandUploads++;

            EnterCollectStage(9);
            // 9. Only now is the display's own state changed: the references no longer needed are given back, and a
            //    registered fragment that is retired is let go. A body a commit replaced goes here too, never earlier:
            //    until this adoption it was what the snapshot on screen was drawn from.
            for (int r = _retiring.Count - 1; r >= 0; r--)
            {
                ReleaseReferences(_retiring[r]);
                _retiring.RemoveAt(r);
            }

            // The pass over the registrations: what each is shown as, and the instances past its need let go. With the
            // structure standing and the instances settled (stage 4), every value it would write is the one standing
            // and nothing is past its need: kept (TL, 2026-10-08). A registration being let go came with a ledger
            // change, so its collection is structural and comes here.
            CountingFrame();
            if (instancesStand)
            {
                _countsNow.statics.releasesSkipped++;
            }
            else
            {
                long releaseBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int g = _shown.Count - 1; g >= 0; g--)
                {
                    Shown entry = _shown[g];
                    entry.takenThisPass = 0;
                    if (entry.dropping)
                    {
                        ReleaseReferences(entry);
                        _shown.RemoveAt(g);
                        InputChanged();

                        // Still drawn as this adoption has it: its slots are let go by the next collection.
                        if (entry.slots.held)
                        {
                            entry.firstRenderFragmentShown = entry.firstRenderFragment;
                            _unshownWithSlots.Add(entry);
                        }

                        continue;
                    }

                    entry.splitShown = entry.split;
                    entry.awaitingShown = entry.awaiting && !entry.split;
                    entry.renderFragmentsShown = entry.renderFragments;
                    entry.firstRenderFragmentShown = entry.firstRenderFragment;
                    int required = Math.Max(1, entry.renderFragments);
                    while (entry.instances.Count > required)
                    {
                        int last = entry.instances.Count - 1;
                        _table.TryRetireDisplayInstance(entry.instances[last]);
                        entry.instances.RemoveAt(last);
                    }
                }

                _countsNow.statics.releases++;
                _countsNow.statics.releaseSeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - releaseBegan) * s_secondsPerTick;
            }

            // Every registration holds exactly what the adopted structure needs: the next collection of this structure
            // takes and trims nothing.
            _instancesSettled = true;
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
        /// The candidate draw data from the built snapshot, in the side that is not drawn from (DESIGN 5.6).
        /// <para>
        /// **The slots.** A side that can be brought up is first given, from the adopted side, the ranges that side was
        /// written over and this one was not; then the registrations this collection planned to write are written --
        /// their commands, each with where its instances begin, its material and its caster, and every instance
        /// record of theirs -- and the commands of what is drawn no more are zeroed; then the render fragments the
        /// placement pass placed anew have their transforms and clips written, a record a command. A side that cannot
        /// be brought up has every slot written instead. Each write records its range; nothing else is touched.
        /// </para>
        /// <para>
        /// **The tables by the snapshot's numbering** -- where each render fragment's commands are (the slots of its
        /// registration) and where it stands, which a stencil volume is later drawn from with its own face's clip;
        /// every registration's root and draw ranges -- are made again when the structure is another, and otherwise
        /// only written where something was placed anew. And one cap record per snapshot cap.
        /// </para>
        /// </summary>
        private void BuildCandidate(long stampLedger, bool structural, int commandEnd, int instanceEnd)
        {
            if (_candidateSlotsWhole || collectEverythingForTest)
            {
                WriteEverySlot(commandEnd, instanceEnd);
                NotePlacedAnewWrites();   // the side is written whole: the ordinary updates among it are noted apart (D-202)
            }
            else
            {
                // A compacting collection writes every live registration's records where they now stand, so the side
                // is not given the adopted one's records first: it would be given them where they used to be.
                CatchUpSlots(!_compactThisPass);
                if (structural || _compactThisPass)
                {
                    WritePlannedSlots();
                }

                WritePlacedAnewRecords();
            }

            // The tables are kept when they were made for the very structure this collection is of, the adopted side's
            // were too (so the two differ only over the one range kept), and the placement pass told the render
            // fragments it placed anew apart.
            bool tablesKept = stampLedger >= 0
                              && _candidateSideLedger == stampLedger && _candidateSideInputs == _inputRevision
                              && _sideLedger == stampLedger && _sideInputs == _inputRevision
                              && !_building.AllRenderFragmentsPlacedAnew;
            if (tablesKept)
            {
                WritePlacedAnewTables();
            }
            else
            {
                AssembleTables(stampLedger);
            }

            // One cap record per snapshot cap: where a cap is -- its plane, its normal, its vertices -- is of this frame.
            int caps = _building.CapCount;
            for (int i = 0; i < caps; i++)
            {
                _candidateCapRecords[i] = CapRecordOf(i);
            }
        }

        // The side being built is given what the adopted side was written over and it was not: the commands' recorded
        // ranges and the instance records' one range, copied, and nothing else. After this the two sides hold the same
        // in every slot. Without the instances (a compaction, which writes every live record where it now stands) the
        // range is kept as owed: the side lacks it still if this collection is not adopted.
        private void CatchUpSlots(bool instances)
        {
            for (int r = 0; r < _lackingCommands.Count; r++)
            {
                int start = _lackingCommands.StartAt(r), count = _lackingCommands.EndAt(r) - start;
                _candidateCommands.CopyFrom(_commands, start, start, count);
                _candidateCommandStarts.CopyFrom(_commandStarts, start, start, count);
                _candidateCommandProvisional.CopyFrom(_commandProvisional, start, start, count);
                Array.Copy(_commandMaterials, start, _candidateCommandMaterials, start, count);
                CommandRecordsCaughtUp += count;
            }

            if (instances)
            {
                if (!_lackingInstances.IsEmpty)
                {
                    int start = _lackingInstances.start, count = _lackingInstances.Count;
                    _candidateTransforms.CopyFrom(_transforms, start, start, count);
                    _candidateClips.CopyFrom(_clips, start, start, count);
                    _candidateSides.CopyFrom(_sides, start, start, count);
                    InstanceRecordsCaughtUp += count;
                }

                _lackingInstances.Clear();
            }

            _lackingCommands.Clear();
        }

        // Every slot of a side that cannot be brought up by ranges: the free command slots zeroed, every registration
        // that holds slots written. Everything below the two ends is then what this collection wrote.
        private void WriteEverySlot(int commandEnd, int instanceEnd)
        {
            CandidateAssemblies++;
            _candidateCommands.Clear(0, commandEnd);
            _candidateCommandStarts.Clear(0, commandEnd);
            _candidateCommandProvisional.Clear(0, commandEnd);
            Array.Clear(_candidateCommandMaterials, 0, commandEnd);
            for (int g = 0; g < _shown.Count; g++)
            {
                if (_shown[g].slots.held)
                {
                    WriteRegistration(_shown[g]);
                    if (_shown[g].rewrite && !_compactThisPass)
                    {
                        NoteWritten(_shown[g]);   // the structure's plan wrote it: as where the planned slots are written (D-202)
                    }
                }
            }

            _writtenCommands.Clear();
            _writtenCommands.Add(0, commandEnd);
            _writtenInstances.Clear();
            _writtenInstances.Add(0, instanceEnd);
            _lackingCommands.Clear();
            _lackingInstances.Clear();
        }

        // What stage 3 planned: the registrations to write, and the commands of what is drawn no more made to draw
        // nothing -- a command of no instance, which the next selection and the next draw read as such. A compacting
        // collection has every live registration here, each written where its region now stands (D-202).
        private void WritePlannedSlots()
        {
            for (int i = 0; i < _slotTouched.Count; i++)
            {
                Shown entry = _slotTouched[i];
                if (entry.releasing)
                {
                    int start = entry.slotsBefore.commandStart, count = entry.commands.Length;
                    _candidateCommands.Clear(start, count);
                    _candidateCommandStarts.Clear(start, count);
                    _candidateCommandProvisional.Clear(start, count);
                    Array.Clear(_candidateCommandMaterials, start, count);
                    _writtenCommands.Add(start, start + count);
                    CommandRecordsWritten += count;
                }
            }

            for (int i = 0; i < _slotTouched.Count; i++)
            {
                Shown entry = _slotTouched[i];
                if (!entry.releasing && entry.rewrite)
                {
                    WriteRegistration(entry);
                    if (!_compactThisPass)
                    {
                        // Written because its structure changed (first drawn, cut, published, committed, or one of
                        // its family was): an ordinary write (D-202). A compaction's rewrite is none.
                        NoteWritten(entry);
                    }
                }
            }
        }

        // One registration's slots, whole: its commands -- each with where its instances begin, its material and its
        // caster -- and, per command, the instance record of every render fragment it is drawn as.
        private void WriteRegistration(Shown entry)
        {
            SlotState slots = entry.slots;
            int commands = entry.commands.Length;
            for (int c = 0; c < commands; c++)
            {
                VpIndirectCommand source = entry.commands[c];
                int slot = slots.commandStart + c;
                int first = slots.instanceStart + (c * slots.instanceStride);
                _candidateCommands[slot] = new VpIndirectCommand(source.range, source.localBounds, slots.renderFragments);
                _candidateCommandStarts[slot] = first;
                _candidateCommandMaterials[slot] = entry.commandMaterials[c];
                _candidateCommandProvisional[slot] = slots.clipped;
                for (int k = 0; k < slots.renderFragments; k++)
                {
                    int r = entry.firstRenderFragment + k;
                    _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                    _candidateTransforms[first + k] = rf.geometryLocalToWorld;
                    _candidateClips[first + k] = rf.clip;
                    _candidateSides[first + k] = SideOf(entry, r, rf);
                }
            }

            entry.writtenPass = _slotPass;
            _writtenCommands.Add(slots.commandStart, slots.commandStart + commands);
            _writtenInstances.Add(slots.instanceStart, slots.instanceStart + (commands * slots.instanceStride));
            CommandRecordsWritten += commands;
            InstanceRecordsWritten += commands * slots.renderFragments;
            RegistrationsWritten++;
        }

        // The render fragments the placement pass placed anew, of registrations this collection did not write whole:
        // their transforms and clips, a record a command, where their registration's region has them. No comparison.
        private void WritePlacedAnewRecords()
        {
            if (_building.AllRenderFragmentsPlacedAnew)
            {
                int every = _building.RenderFragmentCount;
                for (int r = 0; r < every; r++)
                {
                    WritePlacedAnewRecord(r);
                }

                return;
            }

            int anew = _building.PlacedAnewCount;
            for (int a = 0; a < anew; a++)
            {
                WritePlacedAnewRecord(_building.PlacedAnewAt(a));
            }
        }

        private void WritePlacedAnewRecord(int r)
        {
            _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
            Shown entry = _shown[rf.registration];
            if (!entry.slots.held)
            {
                return;   // drawn as nothing
            }

            // The history (D-202): the placement pass placed it anew, so its record is an ordinary write of this
            // collection -- here, or a moment ago with its registration written whole (by the structure's plan or by
            // a compaction, whose own rewrite of the others is no such write).
            NoteWritten(entry);
            if (entry.writtenPass == _slotPass)
            {
                return;   // written whole a moment ago
            }

            int commands = entry.commands.Length;
            int stride = entry.slots.instanceStride;
            int first = entry.slots.instanceStart + (r - entry.firstRenderFragment);
            for (int c = 0; c < commands; c++)
            {
                int i = first + (c * stride);
                _candidateTransforms[i] = rf.geometryLocalToWorld;
                _candidateClips[i] = rf.clip;
            }

            _writtenInstances.Add(first, first + ((commands - 1) * stride) + 1);
            InstanceRecordsWritten += commands;
        }

        // The tables by the snapshot's numbering stand: the side is given the one range the other was written over, and
        // where the render fragments placed anew stand is written.
        private void WritePlacedAnewTables()
        {
            if (_renderFragmentDiffEnd > _renderFragmentDiffStart)
            {
                _candidateRfTransform.CopyFrom(
                    _rfTransform, _renderFragmentDiffStart, _renderFragmentDiffStart, _renderFragmentDiffEnd - _renderFragmentDiffStart);
            }

            int rfFrom = int.MaxValue, rfTo = 0;
            int anew = _building.PlacedAnewCount;
            for (int a = 0; a < anew; a++)
            {
                int r = _building.PlacedAnewAt(a);
                _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                _candidateRfTransform[r] = rf.geometryLocalToWorld;
                if (r < rfFrom) rfFrom = r;
                if (r + 1 > rfTo) rfTo = r + 1;
            }

            _renderFragmentDiffStart = rfTo > 0 ? rfFrom : 0;
            _renderFragmentDiffEnd = rfTo;
        }

        // The tables by the snapshot's numbering made again: where each render fragment's commands are -- its
        // registration's command slots -- and where it stands; and every registration's root and draw ranges.
        private void AssembleTables(long stampLedger)
        {
            InvalidateCandidateSide();   // until they are whole
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                for (int k = 0; k < entry.renderFragments; k++)
                {
                    int r = entry.firstRenderFragment + k;
                    _building.TryGetRenderFragment(r, out VpMultiCutRenderFragment placed);
                    _candidateRfCommandStart[r] = entry.slots.held ? entry.slots.commandStart : 0;
                    _candidateRfCommandCount[r] = entry.slots.held ? entry.commands.Length : 0;
                    _candidateRfTransform[r] = placed.geometryLocalToWorld;
                }
            }

            if (_candidateRoots.Length < _shown.Count)
            {
                // The roots' room is the instances', grown with them before anything was taken (stage 3).
                throw new InvalidOperationException("the roots' room was not grown with the instances");
            }

            // The draw ranges of every registration the snapshot was built from, in its order and as many -- those drawn
            // as nothing and those being let go included -- so that the table is the snapshot's, adopted with it.
            _candidateGeometries.Restart(_shown.Count);
            for (int g = 0; g < _shown.Count; g++)
            {
                _candidateRoots[g] = _shown[g].fragment;
                _candidateGeometries.Add(_shown[g].ranges);
            }

            // Whole: the two sides' tables may differ anywhere.
            _renderFragmentDiffStart = 0;
            _renderFragmentDiffEnd = _building.RenderFragmentCount;
            _candidateSideInputs = _inputRevision;
            _candidateSideLedger = stampLedger;
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
                _building.TryGetCap(i, out VpMultiCutCap candidateCap);
                AppendFan(_candidateCapRecords[i], candidateCap.mirrored, ref capIndexCount);
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
        /// The candidate becomes what is drawn. Everything trades places -- the snapshots and the arrays alike -- so
        /// what was adopted a moment ago becomes the room the next candidate is built in. Nothing is copied and nothing
        /// can grow here. The draw slots stay where they are: what the side just replaced lacks of this one is the
        /// ranges this collection wrote, kept for the collection that builds in it next (DESIGN 5.6).
        /// </summary>
        private void Adopt(int commandEnd, int instanceEnd)
        {
            Swap(ref _snapshot, ref _building);
            Swap(ref _commands, ref _candidateCommands);
            Swap(ref _commandStarts, ref _candidateCommandStarts);
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

            // What each side's tables were made for goes with it.
            Swap(ref _sideLedger, ref _candidateSideLedger);
            Swap(ref _sideInputs, ref _candidateSideInputs);

            _commandEnd = commandEnd;
            _instanceEnd = instanceEnd;
            _commandCount = _plannedCommands;
            _liveInstances = _plannedInstances;
            _capRecordCount = _snapshot.CapCount;
            _hasSnapshot = true;
            if (_capLocalVertices.Length != _capLayoutRoom)
            {
                _capLayoutRoom = _capLocalVertices.Length;   // a room made larger holds no layout
                _capLayoutStale = true;
            }

            if (_capLayoutStale)
            {
                LayOutLocalCapVertices();
            }
            MostRenderFragments = Math.Max(MostRenderFragments, _snapshot.RenderFragmentCount);
            MostCommands = Math.Max(MostCommands, _commandCount);
            MostBranches = Math.Max(MostBranches, _snapshot.BranchCount);
            MostCandidates = Math.Max(MostCandidates, _snapshot.CandidateCount);
            MostCaps = Math.Max(MostCaps, _snapshot.CapCount);
            MostCapVertices = Math.Max(MostCapVertices, _snapshot.CapVertexCount);

            // The collection is adopted: what it stopped using is free, and what it wrote is what the other side lacks.
            KeepSlots();

            // Every camera's preparation was for the snapshot just replaced.
            _generation++;
        }

        private static void Swap<T>(ref T a, ref T b)
        {
            T held = a;
            a = b;
            b = held;
        }

        private enum RoomKind
        {
            Commands,
            Instances,
            Fragments,
            Branches,
            Candidates,
        }

        /// <summary>
        /// Called once, on the main thread, when a need is past a limit or room cannot be made, with what was short,
        /// how much was needed and held, the limit and what came of it. The owner turns that into the common
        /// termination; the display stops (<see cref="LogicalCutDisplayHaltReason.RoomNotEstablished"/>). Without one,
        /// a shortfall is refused as it always was, and only <see cref="LastRoomFailure"/> says so.
        /// </summary>
        public Action<string> RoomFailureHandler { get; set; }

        /// <summary>The last shortfall that could not be answered by growing, in words; null while there has been none.</summary>
        public string LastRoomFailure { get; private set; }

        /// <summary>
        /// Asked when a count is about to grow; when it answers true the memory is taken as not to be had. For tests only,
        /// to reach what no input can; null otherwise.
        /// </summary>
        internal Func<bool> FailRoomAllocationForTest { get; set; }

        /// <summary>How many times a count of this display's room has grown.</summary>
        public int RoomGrowths { get; private set; }

        /// <summary>GPU objects replaced by larger ones and not yet released: the GPU may still be reading them.</summary>
        public int RetiredGpuObjects => _retiredGpu.Count;

        public int CommandCapacity => _commandCapacity;

        public int InstanceCapacity => _instanceCapacity;

        /// <summary>
        /// How many render fragments, and registrations, there is room for at once: what the snapshot, the caps and
        /// every camera's stencil batch are sized from (DESIGN 5.6). Not the draw slots' room.
        /// </summary>
        public int RenderFragmentCapacity => _fragmentCapacity;

        public int BranchCapacity => _branchCapacity;

        public int CandidateCapacity => _candidateCapacity;

        /// <summary>This display's room, in words. Log text only.</summary>
        public string DescribeRoom()
            => "display room: commands " + _commandCapacity + " (limit " + _limits.commands + "), instances " + _instanceCapacity
               + " (limit " + _limits.instances + "), render fragments " + _fragmentCapacity + " (limit " + _fragmentLimit
               + "), branches " + _branchCapacity + " (limit " + _limits.branches + "), candidates "
               + _candidateCapacity + " (limit " + _limits.candidates + "), grown " + RoomGrowths + " times, replaced GPU objects "
               + "awaiting release " + _retiredGpu.Count + "; " + _table.DescribeRoom();

        /// <summary>
        /// Makes every array a collection builds in, and every piece of scratch, at least as large as the room -- the
        /// snapshot being built too when <paramref name="building"/> says it holds nothing yet. Only what nothing draws
        /// from is replaced; what it held is rebuilt by every collection. False when memory cannot be had.
        /// </summary>
        private bool TryEnsureCandidateRoom(bool building, out string failure)
        {
            failure = null;
            if (!TryDeriveCapacities(
                    _commandCapacity, _fragmentCapacity, _branchCapacity, _candidateCapacity, _chainDepth,
                    out DerivedCapacities derived))
            {
                failure = "a size derived from the room is not an int";
                return false;
            }

            // Each owner grows in place and whole (TL, 2026-10-05): more pages committed behind the same base for the
            // numbers -- nothing made again, nothing moved -- and larger arrays for what holds a reference. Nothing here
            // is published: an owner that grew before another refused only holds more room than is used, and the counts
            // this display works by are the caller's, set back when any of it could not be had. The snapshot being built
            // keeps its counters and what it remembers of the registrations, since it is the same snapshot.
            VpMultiCutCapacities room = derived.Snapshot;
            if (building && !Holds(_building.Capacities, room))
            {
                if (!_building.TryGrowTo(room, out failure))
                {
                    return false;
                }

                SnapshotRegrowths++;
                CountingFrame();
                _countsNow.snapshotRegrowths++;
            }

            // Shared by the preparations, which never run during a collection.
            if (!Holds(_capJobs.Capacities, room) && !_capJobs.TryGrowTo(room, out failure))
            {
                return false;
            }

            // A side whose room is made larger is written whole in it: what holds a reference is made again, empty.
            if (_candidateCommands.Length < _commandCapacity || _candidateTransforms.Length < _instanceCapacity
                || _candidateRfTransform.Length < derived.renderFragments || _candidateCommandMaterials.Length < _commandCapacity
                || _candidateGeometries.Capacity < _fragmentCapacity)
            {
                InvalidateCandidateSide();
                _candidateSlotsWhole = true;
            }

            if (!_candidateCommands.TryGrow(_commandCapacity, out failure)
                || !_candidateCommandStarts.TryGrow(_commandCapacity, out failure)
                || !_candidateCommandProvisional.TryGrow(_commandCapacity, out failure)
                || !_candidateTransforms.TryGrow(_instanceCapacity, out failure)
                || !_candidateClips.TryGrow(_instanceCapacity, out failure)
                || !_candidateSides.TryGrow(_instanceCapacity, out failure)
                || !_candidateRoots.TryGrow(_fragmentCapacity, out failure)
                || !_candidateRfCommandStart.TryGrow(derived.renderFragments, out failure)
                || !_candidateRfCommandCount.TryGrow(derived.renderFragments, out failure)
                || !_candidateRfTransform.TryGrow(derived.renderFragments, out failure)
                || !_candidateCapRecords.TryGrow(derived.caps, out failure)
                || !_candidateStencilCommands.TryGrow(derived.stencilCommands, out failure)
                || !_candidateStencilTransforms.TryGrow(derived.stencilCommands, out failure)
                || !_candidateStencilClips.TryGrow(derived.stencilCommands, out failure)
                || !_candidateCapIndices.TryGrow(derived.capIndices, out failure)
                || !_capNormals.TryGrow(derived.capVertices, out failure)
                || !_capLocalVertices.TryGrow(derived.capVertices, out failure))
            {
                return false;
            }

            try
            {
                AtLeast(ref _candidateCommandMaterials, _commandCapacity);
                if (_candidateGeometries.Capacity < _fragmentCapacity)
                {
                    _candidateGeometries.Restart(0);
                    _candidateGeometries = new GeometryTable(_fragmentCapacity);
                }

                if (_shown.Capacity < _fragmentCapacity)
                {
                    _shown.Capacity = _fragmentCapacity;
                }

                if (_registrations.Capacity < _fragmentCapacity)
                {
                    _registrations.Capacity = _fragmentCapacity;
                }
            }
            catch (OutOfMemoryException exception)
            {
                failure = "memory could not be had: " + exception.Message;
                return false;
            }

            // A preparation limit a test lowered stays lowered; one at the room grows with it.
            if (_preparationRecordLimit == _capRecordCapacity)
            {
                _preparationRecordLimit = derived.caps;
            }

            _capRecordCapacity = derived.caps;
            _stencilCommandCapacity = derived.stencilCommands;
            _stencilCapVertexCapacity = derived.capVertices;
            _stencilCapIndexCapacity = derived.capIndices;
            return true;
        }

        private static bool Holds(in VpMultiCutCapacities have, in VpMultiCutCapacities need)
        {
            return have.branches >= need.branches && have.candidates >= need.candidates
                && have.renderFragments >= need.renderFragments && have.caps >= need.caps && have.chainDepth >= need.chainDepth;
        }

        private static void AtLeast<T>(ref T[] array, int length)
        {
            if (array.Length < length)
            {
                array = new T[length];
            }
        }

        /// <summary>
        /// Grows one count to at least <paramref name="needed"/> -- at least doubling it, never past its limit -- and the
        /// room built in with it. False, changing no count, when the need is past the limit or the room cannot be made.
        /// </summary>
        private bool TryGrow(RoomKind kind, long needed, bool building, out string failure)
        {
            failure = null;
            int held = RoomOf(kind);
            int limit = LimitOf(kind);
            needed = Math.Max(needed, held + 1L);
            if (needed > limit)
            {
                failure = "past the limit";
                return false;
            }

            int grown = (int)Math.Min(limit, Math.Max(needed, held * 2L));
            int commands = _commandCapacity;
            int instances = _instanceCapacity;
            int fragments = _fragmentCapacity;
            int branches = _branchCapacity;
            int candidates = _candidateCapacity;
            SetRoom(kind, grown);
            bool refusedForTest = FailRoomAllocationForTest != null && FailRoomAllocationForTest();
            if (refusedForTest)
            {
                failure = "memory could not be had: refused for a test";
            }

            if (refusedForTest || !TryEnsureCandidateRoom(building, out failure))
            {
                _commandCapacity = commands;
                _instanceCapacity = instances;
                _fragmentCapacity = fragments;
                _branchCapacity = branches;
                _candidateCapacity = candidates;
                return false;
            }

            RoomGrowths++;
            CountingFrame();
            _countsNow.roomGrowths++;
            Debug.Log("VpLogicalCutDisplay: " + NameOf(kind) + " grown from " + held + " to " + grown + " (needed " + needed
                      + ", limit " + limit + "); " + DescribeRoom());
            return true;
        }

        /// <summary>
        /// Grows, together and once, every count that the registrations themselves show to be short, before anything is
        /// built: a registration that is kept has at least one branch and one render fragment, and all its commands with
        /// at least one instance each, so the kept registrations are a floor under the branches and the render
        /// fragments, and their commands one under the commands and the instances. A count whose floor is past its
        /// limit is left as it is -- the build and the checks after it tell that shortfall as they always did. False,
        /// changing no count, when the room could not be made.
        /// </summary>
        private bool TryGrowForRegistrations(out string failure)
        {
            failure = null;

            // The tally -- the registrations kept and their commands -- follows from the registrations as read (stage
            // 1): the same ledger revision and input revision, the same tally, and room that was enough is enough
            // still (it only grows). Kept while they stand (TL, 2026-10-08); run again for any change of either, as a
            // test that collects everything does.
            CountingFrame();
            if (!collectEverythingForTest && _readLedgerRevision >= 0 && _talliedLedger == _readLedgerRevision && _talliedInputs == _readInputRevision)
            {
                StructureTalliesSkipped++;
                _countsNow.statics.talliesSkipped++;
                return true;
            }

            long tallyBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            long kept = 0;
            long keptCommands = 0;
            for (int g = 0; g < _shown.Count; g++)
            {
                Shown entry = _shown[g];
                if (!entry.dropping)
                {
                    kept++;
                    keptCommands += entry.commands.Length;
                }
            }

            int commands = _commandCapacity;
            int instances = _instanceCapacity;
            int fragments = _fragmentCapacity;
            int branches = _branchCapacity;
            int candidates = _candidateCapacity;
            int grownCommands = GrownFor(keptCommands, commands, _limits.commands);
            int grownInstances = GrownFor(keptCommands, instances, _limits.instances);
            int grownFragments = GrownFor(kept, fragments, _fragmentLimit);
            int grownBranches = GrownFor(kept, branches, _limits.branches);
            StructureTallies++;
            _countsNow.statics.tallies++;
            _countsNow.statics.tallySeconds += (System.Diagnostics.Stopwatch.GetTimestamp() - tallyBegan) * s_secondsPerTick;
            if (grownCommands == commands && grownInstances == instances && grownFragments == fragments && grownBranches == branches)
            {
                _talliedLedger = _readLedgerRevision;
                _talliedInputs = _readInputRevision;
                return true;
            }

            _commandCapacity = grownCommands;
            _instanceCapacity = grownInstances;
            _fragmentCapacity = grownFragments;
            _branchCapacity = grownBranches;
            bool refusedForTest = false;
            for (int kind = 0; kind < 4 && !refusedForTest; kind++)
            {
                bool grows = kind == 0 ? grownBranches != branches
                    : kind == 1 ? grownFragments != fragments
                    : kind == 2 ? grownCommands != commands
                    : grownInstances != instances;
                refusedForTest = grows && FailRoomAllocationForTest != null && FailRoomAllocationForTest();
            }

            if (refusedForTest)
            {
                failure = "memory could not be had: refused for a test";
            }

            if (refusedForTest || !TryEnsureCandidateRoom(true, out failure))
            {
                _commandCapacity = commands;
                _instanceCapacity = instances;
                _fragmentCapacity = fragments;
                _branchCapacity = branches;
                _candidateCapacity = candidates;
                return false;
            }

            if (grownBranches != branches) NoteGrown(RoomKind.Branches, branches, grownBranches, kept);
            if (grownFragments != fragments) NoteGrown(RoomKind.Fragments, fragments, grownFragments, kept);
            if (grownCommands != commands) NoteGrown(RoomKind.Commands, commands, grownCommands, keptCommands);
            if (grownInstances != instances) NoteGrown(RoomKind.Instances, instances, grownInstances, keptCommands);
            _talliedLedger = _readLedgerRevision;
            _talliedInputs = _readInputRevision;
            return true;
        }

        // What a count held becomes when at least needed is wanted: itself when it holds that or the need is past the
        // limit, else at least double, never past the limit.
        private static int GrownFor(long needed, int held, int limit)
        {
            return needed <= held || needed > limit ? held : (int)Math.Min(limit, Math.Max(needed, held * 2L));
        }

        private void NoteGrown(RoomKind kind, int held, int grown, long needed)
        {
            RoomGrowths++;
            CountingFrame();
            _countsNow.roomGrowths++;
            Debug.Log("VpLogicalCutDisplay: " + NameOf(kind) + " grown from " + held + " to " + grown + " (needed " + needed
                      + ", limit " + LimitOf(kind) + "); " + DescribeRoom());
        }

        /// <summary>
        /// How many growths the room can still take, every count from where it is to its limit, each growth at least
        /// doubling (or reaching the limit). The bound of one collection's builds.
        /// </summary>
        private int GrowthsToLimits()
        {
            return StepsToLimit(_commandCapacity, _limits.commands) + StepsToLimit(_instanceCapacity, _limits.instances)
                + StepsToLimit(_fragmentCapacity, _fragmentLimit)
                + StepsToLimit(_branchCapacity, _limits.branches) + StepsToLimit(_candidateCapacity, _limits.candidates);
        }

        private static int StepsToLimit(int held, int limit)
        {
            int steps = 0;
            for (long at = held; at < limit; steps++)
            {
                at = Math.Min(limit, Math.Max(at + 1L, at * 2L));
            }

            return steps;
        }

        /// <summary>What a snapshot shortage is answered with: the count its room follows from, grown.</summary>
        private bool TryGrowFor(VpMultiCutShortage shortage, out string failure)
        {
            switch (shortage)
            {
                case VpMultiCutShortage.Branches:
                    return TryGrow(RoomKind.Branches, _branchCapacity + 1L, true, out failure);
                case VpMultiCutShortage.Candidates:
                    return TryGrow(RoomKind.Candidates, _candidateCapacity + 1L, true, out failure);
                case VpMultiCutShortage.RenderFragments:
                case VpMultiCutShortage.Caps:
                    // Caps are eight per render fragment.
                    return TryGrow(RoomKind.Fragments, _fragmentCapacity + 1L, true, out failure);
                default:
                    // The chain depth is a limit of the lineage, not room to grow.
                    failure = "the chain depth is not grown";
                    return false;
            }
        }

        private int RoomOf(RoomKind kind)
        {
            switch (kind)
            {
                case RoomKind.Commands: return _commandCapacity;
                case RoomKind.Instances: return _instanceCapacity;
                case RoomKind.Fragments: return _fragmentCapacity;
                case RoomKind.Branches: return _branchCapacity;
                default: return _candidateCapacity;
            }
        }

        private int LimitOf(RoomKind kind)
        {
            switch (kind)
            {
                case RoomKind.Commands: return _limits.commands;
                case RoomKind.Instances: return _limits.instances;
                case RoomKind.Fragments: return _fragmentLimit;
                case RoomKind.Branches: return _limits.branches;
                default: return _limits.candidates;
            }
        }

        private void SetRoom(RoomKind kind, int value)
        {
            switch (kind)
            {
                case RoomKind.Commands: _commandCapacity = value; break;
                case RoomKind.Instances: _instanceCapacity = value; break;
                case RoomKind.Fragments: _fragmentCapacity = value; break;
                case RoomKind.Branches: _branchCapacity = value; break;
                default: _candidateCapacity = value; break;
            }
        }

        private static string NameOf(RoomKind kind)
        {
            switch (kind)
            {
                case RoomKind.Commands: return "draw commands";
                case RoomKind.Instances: return "draw instances";
                case RoomKind.Fragments: return "render fragments";
                case RoomKind.Branches: return "branches";
                default: return "candidates";
            }
        }

        private static string ShortageName(VpMultiCutShortage shortage)
        {
            switch (shortage)
            {
                case VpMultiCutShortage.Branches: return "branches";
                case VpMultiCutShortage.Candidates: return "candidates";
                case VpMultiCutShortage.ChainDepth: return "chain depth";
                case VpMultiCutShortage.RenderFragments: return "render fragments";
                case VpMultiCutShortage.Caps: return "caps (render fragments)";
                default: return "snapshot room";
            }
        }

        private int HeldFor(VpMultiCutShortage shortage)
        {
            switch (shortage)
            {
                case VpMultiCutShortage.Branches: return _branchCapacity;
                case VpMultiCutShortage.Candidates: return _candidateCapacity;
                case VpMultiCutShortage.ChainDepth: return _chainDepth;
                default: return _fragmentCapacity;
            }
        }

        private int LimitFor(VpMultiCutShortage shortage)
        {
            switch (shortage)
            {
                case VpMultiCutShortage.Branches: return _limits.branches;
                case VpMultiCutShortage.Candidates: return _limits.candidates;
                case VpMultiCutShortage.ChainDepth: return _chainDepth;
                default: return _fragmentLimit;
            }
        }

        /// <summary>
        /// Every GPU object the candidate needs larger than it is: each registered camera's stencil batch, replaced now
        /// -- this frame has prepared none yet, and every preparation fills its batch again -- and the body's batch and
        /// the cap-normal buffer, made here and handed back to be written and switched to at adoption. False, having
        /// made nothing that is kept, when the device cannot tell when a replaced object is no longer used or a buffer
        /// cannot be made.
        /// </summary>
        private bool TryMakeGpuRoomForCandidate(
            int commands, int instances, int capVertices, out VpIndexedIndirectDrawBatch grownBatch,
            out GraphicsBuffer grownNormals, out string failure)
        {
            grownBatch = null;
            grownNormals = null;
            failure = null;
            bool cameraShort = false;
            foreach (CameraStencil slot in _cameraStencils)
            {
                cameraShort |= slot != null && IsSmallerThanRoom(slot.batch);
            }

            bool bodyShort = commands > _batch.CommandCapacity || instances > _batch.InstanceCapacity;
            bool normalsShort = Math.Max(capVertices, _building.CapCount * CapPlacementStride) > _capNormalBuffer.count;
            if (!cameraShort && !bodyShort && !normalsShort)
            {
                return true;
            }

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                failure = "the device cannot tell when a replaced buffer is no longer used (no asynchronous readback)";
                return false;
            }

            try
            {
                if (bodyShort)
                {
                    grownBatch = new VpIndexedIndirectDrawBatch(_commandCapacity, _instanceCapacity, _pages, _cull);
                    grownBatch.WriteCullArgumentsZero();
                }

                if (normalsShort)
                {
                    grownNormals = new GraphicsBuffer(
                        GraphicsBuffer.Target.Structured, Math.Max(1, _stencilCapVertexCapacity), sizeof(float) * 4);
                }

                for (int i = 0; i < _cameraStencils.Length; i++)
                {
                    CameraStencil slot = _cameraStencils[i];
                    if (slot == null || !IsSmallerThanRoom(slot.batch))
                    {
                        continue;
                    }

                    var batch = new VpStencilCapBatch(
                        _settings.maxStencilColors, _stencilCommandCapacity, _stencilCommandCapacity,
                        _stencilCapVertexCapacity, _stencilCapIndexCapacity, _pages);
                    VpStencilCapBatch replaced = slot.batch;
                    GpuReplacements++;
                    slot.batch = batch;
                    slot.preparedFrame = int.MinValue;
                    slot.preparedGeneration = -1;
                    slot.preparation = default;
                    RetireStencilLater(replaced);
                }
            }
            catch (Exception exception)
            {
                grownBatch?.Dispose();
                grownNormals?.Dispose();
                grownBatch = null;
                grownNormals = null;
                failure = "a GPU buffer could not be made: " + exception.Message;
                return false;
            }

            GpuReplacements += (bodyShort ? 1 : 0) + (normalsShort ? 1 : 0);
            Debug.Log("VpLogicalCutDisplay: GPU room made for " + commands + " commands, " + instances + " instances and "
                      + capVertices + " cap vertices (body batch " + (bodyShort ? "replaced" : "kept") + ", cap normals "
                      + (normalsShort ? "replaced" : "kept") + ", camera batches " + (cameraShort ? "replaced" : "kept") + ")");
            return true;
        }

        private bool IsSmallerThanRoom(VpStencilCapBatch batch)
        {
            return batch.CommandCapacity < _stencilCommandCapacity || batch.CapVertexCapacity < _stencilCapVertexCapacity
                || batch.CapIndexCapacity < _stencilCapIndexCapacity;
        }

        /// <summary>A camera's batch replaced: its counts are kept, and it is released once the GPU is past it.</summary>
        private void RetireStencilLater(VpStencilCapBatch batch)
        {
            _retiredStencilUploads += batch.Uploads;
            _retiredStencilBufferWrites += batch.BufferWrites;
            _retiredStencilInitIssues += batch.StencilInitIssues;
            _retiredStencilVolumeIssues += batch.VolumeIssues;
            _retiredStencilCapIssues += batch.CapIssues;
            RetireLater(batch, batch.RetirementFence);
        }

        /// <summary>
        /// Keeps <paramref name="owner"/> until a readback of <paramref name="fence"/>, asked for now, has completed:
        /// the GPU is then past everything issued from it before. One whose readback cannot be asked for, or fails, is
        /// kept until Dispose. Never waits.
        /// </summary>
        private void RetireLater(IDisposable owner, GraphicsBuffer fence)
        {
            var retired = new RetiredGpu { owner = owner };
            try
            {
                retired.request = UnityEngine.Rendering.AsyncGPUReadback.Request(fence, fence.stride, 0, retired.Completed);
            }
            catch (Exception)
            {
                retired.error = true;
            }

            _retiredGpu.Add(retired);
        }

        /// <summary>Releases, once each, the replaced objects whose readback has completed. Never waits.</summary>
        private void ReleaseRetiredRoom()
        {
            for (int i = _retiredGpu.Count - 1; i >= 0; i--)
            {
                RetiredGpu retired = _retiredGpu[i];
                if (retired.error)
                {
                    if (!_retiredReadbackErrorLogged)
                    {
                        _retiredReadbackErrorLogged = true;
                        Debug.LogError("VpLogicalCutDisplay: a readback of a replaced GPU object failed; it is kept until Dispose.");
                    }

                    continue;
                }

                if (!retired.done)
                {
                    continue;
                }

                retired.owner.Dispose();
                _retiredGpu.RemoveAt(i);
            }
        }

        /// <summary>
        /// A shortfall of a collection that growing could not answer. With a handler it is told once, the display stops
        /// and the collection is refused, so no older snapshot keeps drawing; without one it is refused for room, as it
        /// always was.
        /// </summary>
        private bool FailRoom(string kind, long needed, long held, long limit, string result)
        {
            if (!ReportRoom(kind, needed, held, limit, result))
            {
                return RefuseForRoom();
            }

            return false;
        }

        /// <summary>
        /// Tells a shortfall past a limit, or room that could not be made, to <see cref="RoomFailureHandler"/>, once,
        /// and stops the display. False when there is no handler, which leaves the display as it is.
        /// </summary>
        private bool ReportRoom(string kind, long needed, long held, long limit, string result)
        {
            LastRoomFailure = "the display's " + kind + " could not be given room: " + (needed < 0 ? "more" : needed.ToString())
                              + " needed, " + held + " held, limit " + limit + " -- " + result;
            Action<string> handler = RoomFailureHandler;
            if (handler == null)
            {
                return false;
            }

            Halt(LogicalCutDisplayHaltReason.RoomNotEstablished);
            if (!_roomFailed)
            {
                _roomFailed = true;
                handler(LastRoomFailure + " (" + DescribeRoom() + ")");
            }

            return true;
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

        /// <summary>
        /// Whether the adopted snapshot draws <paramref name="fragment"/> as a branch of an aggregate -- past an Ignored
        /// boundary, as part of the shape before it. Retiring such a fragment would stop this display
        /// (<see cref="LogicalCutDisplayHaltReason.RetiredInsideAggregate"/>), so a lifetime policy asks this first.
        /// False when there is no snapshot or it does not draw the fragment in an aggregate. Reads only.
        /// </summary>
        public bool IsDrawnInsideAggregate(LogicalFragmentId fragment)
        {
            if (!_hasSnapshot)
            {
                return false;
            }

            for (int b = 0; b < _snapshot.BranchCount; b++)
            {
                _snapshot.TryGetBranch(b, out VpMultiCutBranch branch);
                if (branch.fragment == fragment
                    && _snapshot.TryGetRenderFragment(branch.renderFragment, out VpMultiCutRenderFragment rf)
                    && rf.aggregated)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a replaced fragment has a live fragment below it: through the cut that replaced it, either child,
        /// recursively. Reads the ledger only.
        /// </summary>
        private bool HasLiveDescendant(LogicalFragmentId fragment)
        {
            if (!_ledger.TryGetReplacingOperation(fragment, out CutOperationId replacing)
                || !_ledger.TryGetOperation(replacing, out LogicalCutOperation cut))
            {
                return false;
            }

            return IsLiveOrHasLiveDescendant(cut.positive) || IsLiveOrHasLiveDescendant(cut.negative);
        }

        private bool IsLiveOrHasLiveDescendant(LogicalFragmentId fragment)
        {
            if (!fragment.IsSet || !_ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
            {
                return false;
            }

            return state == LogicalFragmentState.Live || (state == LogicalFragmentState.Replaced && HasLiveDescendant(fragment));
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
