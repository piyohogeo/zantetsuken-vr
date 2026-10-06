using UnityEngine;
using UnityEngine.Serialization;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The one place the settings of a cut world come from (DESIGN 4.5.6, 6.1, 7.1, 7.2): the kernel's limit, how many
    /// cuts may cook at once, the room the geometry storage and the display are made with, the sizes the shared
    /// dispatcher is made with, and the two epsilons an accepted cut is decided by. A <see cref="CutWorldRoot"/> is
    /// given one of these and builds everything from it; nothing here is read from a test, and no value of a test's is
    /// carried over silently.
    /// <para>
    /// **Every number here is a count, not a size in bytes.** They say how many vertices, indices, descriptors,
    /// commands or cameras there may be. The vertex and index counts are three different things (DESIGN 4.5.4): a CPU
    /// reservation of address space, which is their absolute limit and costs no memory by itself; a first commit, which
    /// is the memory taken at once and grows as room is given out; and the GPU copy's first capacity, which grows up to
    /// the reservation within the device's largest buffer. None of them is a memory budget.
    /// </para>
    /// <para>
    /// **What it does not decide.** Neither what a hit is, nor anything of Character or XR. The separation impulse is
    /// here only as the switch and the coefficient of its provisional mass-only strength (DESIGN 7.2): switched off, a
    /// cut's impulses are the caller's own values. Which body is a building is not here
    /// either: that is said when the body is registered. What is here of the building constraint is its three
    /// settings and the system constraint capacity (DESIGN 7.2.2, O-048).
    /// </para>
    /// </summary>
    [CreateAssetMenu(menuName = "Zantetsu/Cut World Profile", fileName = "CutWorldProfile")]
    public sealed class CutWorldProfile : ScriptableObject
    {
        [Header("Kernel")]
        [Tooltip("The per-convex vertex limit L. DESIGN 7.2 gives 128; a different value is a change to that.")]
        [SerializeField]
        private int vertexLimit = 128;

        [Tooltip(
            "How many accepted cuts may hold a cook reservation at the same time. Two or more, so that cuts of "
            + "different owners really run beside each other: with one, the second waits for the first to finish.")]
        [SerializeField]
        private int concurrentCookReservations = 4;

        [Header("Acceptance")]
        [Tooltip("The support epsilon the robust-support scan of DESIGN 7.6 classifies a plane with, in metres.")]
        [SerializeField]
        private float supportEpsilon = 1e-4f;

        [Tooltip("The epsilon the ledger distributes a source's anchors with (DESIGN 7.1), in metres.")]
        [SerializeField]
        private float anchorEpsilon = 1e-5f;

        [Header("Geometry storage (counts)")]
        [Tooltip(
            "The CPU vertex reservation (DESIGN 4.5.4): address space for this many vertices (16 bytes each) and their "
            + "topology entries (4 bytes each) is reserved once, and this is the vertices' absolute limit. Address space "
            + "only: memory is what is committed. 67108864 = 1 GiB of vertices.")]
        [SerializeField] private int vertexReserve = 67108864;

        [Tooltip("How many vertices are committed at once; more are committed as room is given out, up to the reservation.")]
        [FormerlySerializedAs("vertexCapacity")]
        [SerializeField] private int vertexInitialCommit = 65536;

        [Tooltip(
            "The CPU index reservation (DESIGN 4.5.4): address space for this many 32-bit indices, the indices' absolute "
            + "limit. 268435456 = 1 GiB of indices.")]
        [SerializeField] private int indexReserve = 268435456;

        [Tooltip("How many indices are committed at once; more are committed as ranges are reserved, up to the reservation.")]
        [FormerlySerializedAs("indexCapacity")]
        [SerializeField] private int indexInitialCommit = 262144;

        [Tooltip(
            "The GPU copy's first capacity in vertices (DESIGN 4.5.4). When a transfer needs more, a larger buffer is made, "
            + "what is drawn is transferred into it and it replaces the old one at a drawing boundary; never more than the "
            + "CPU reservation or the device's largest buffer.")]
        [SerializeField] private int gpuVertexInitialCapacity = 16777216;   // 2026-10-01: 256 MiB at 16 bytes a vertex, so that play does not grow it

        [Tooltip("The GPU copy's first capacity in indices, by the same rule.")]
        [SerializeField] private int gpuIndexInitialCapacity = 67108864;   // 2026-10-01: 256 MiB at 4 bytes an index

        [Tooltip(
            "The storage's fixed bookkeeping tables, allocated whole at the start (they do not grow): index-range descriptors "
            + "(one per published geometry, two per open cut; about 112 bytes each), submesh spans (about 40 bytes each) and "
            + "vertex-block spans (8-byte runs; a geometry at cut depth d holds d + 1, a cut of it takes d + 2 more, and they "
            + "come back only with the whole lineage). Sized so that long play meets the display's own limits (4096) or the "
            + "vertex and index reserves first -- a few MB in all -- not the tables (2026-09-29: 2048 blocks ran out with "
            + "room to spare in vertices and indices).")]
        [SerializeField] private int geometryDescriptorCapacity = 8192;
        [SerializeField] private int submeshCapacity = 32768;
        [SerializeField] private int vertexBlockCapacity = 65536;

        [Header("Shared dispatcher")]
        [Tooltip("How many works may wait to be submitted at once.")]
        [SerializeField] private int waitingCapacity = 32;

        [Tooltip("How many of those places are kept for urgent work.")]
        [SerializeField] private int reservedForUrgent = 8;

        [Tooltip("How many submissions one frame may make. It refills when the frame id changes, never within one.")]
        [SerializeField] private int frameBudget = 64;

        [Tooltip("How many works the Unity Job destination may hold at once.")]
        [SerializeField] private int unityJobCapacity = 8;

        [SerializeField] private int geometryWorkerCount = 4;
        [SerializeField] private int backgroundWorkerCount = 2;

        [Header("Display (counts)")]
        [Tooltip(
            "Where each display count starts. A collection that needs more grows it -- at least doubling, never past its "
            + "limit below -- and switches to the larger room whole at a drawing boundary. A need past a limit ends the "
            + "Player (DESIGN 4).")]
        // 1024 each (TL, 2026-10-05; they were 256, 512, 512, 512, 128, 512): the first room is what a scene reaches,
        // made, committed and written before play, so that crossing 128, 256 or 512 pieces is no longer a growth inside
        // a frame. The reservation behind the display's numeric rooms is made from the limits below.
        // The draw command slots and the instance records are 65536, their limit (TL, 2026-10-06; DESIGN 5.6, D-200):
        // a slot is taken at the end and never again, so these two are used up by everything ever drawn, not by what
        // is drawn at once, and their whole room is made before play so that it is never grown during it. What is
        // sized from how many render fragments are drawn AT ONCE -- the snapshot, the caps, every camera's stencil
        // batch -- has its own count, renderFragmentCapacity, and stays at 1024.
        [SerializeField] private int drawCommandCapacity = 65536;
        [SerializeField] private int drawInstanceCapacity = 65536;
        [SerializeField] private int renderFragmentCapacity = 1024;
        [SerializeField] private int geometryReferenceCapacity = 1024;
        [SerializeField] private int displayInstanceCapacity = 1024;
        [SerializeField] private int branchCapacity = 1024;
        [SerializeField] private int candidateCapacity = 1024;
        [Tooltip(
            "The longest chain of cut boundaries one fragment may have (its cut depth): the multi-cut snapshot's own work "
            + "arrays, a few entries a level, not per instance. It is not grown, and a deeper chain ends the Player, so it is "
            + "sized past any lineage play reaches (2026-09-29: 16 was reached by re-cutting the playable city's building); "
            + "the planes an instance clips with are still chosen, at most VpInstanceClip.PlaneCapacity, from the chain.")]
        [SerializeField] private int chainDepth = 256;

        [Tooltip(
            "How far each display count may grow; at least its first capacity. Equal keeps that count fixed. 65536 each "
            + "(TL, 2026-09-30: the coexistence run ended the Player at 2048 draw commands; the six are raised together). "
            + "Room is still made only as needed, doubling from the first capacity; a room grown past 32768 render "
            + "fragments is the full 65536 and holds several hundred MB of stencil and cap arrays per camera (it is the "
            + "render fragments' count that those follow, not the draw instances'). The storage's fixed tables above "
            + "(index-range descriptors first) bind before these do.")]
        [SerializeField] private int drawCommandCapacityLimit = 65536;
        [SerializeField] private int drawInstanceCapacityLimit = 65536;
        [SerializeField] private int renderFragmentCapacityLimit = 65536;
        [SerializeField] private int geometryReferenceCapacityLimit = 65536;
        [SerializeField] private int displayInstanceCapacityLimit = 65536;
        [SerializeField] private int branchCapacityLimit = 65536;   // 2026-09-29: 1024 was reached by the playable city's re-cut building (k1-k5) and ended the Player
        [SerializeField] private int candidateCapacityLimit = 65536;

        [Header("Stencil caps")]
        [SerializeField] private int maxStencilColours = 4;
        [SerializeField] private float capFacingEpsilon = 0.01f;
        [SerializeField] private float capPlaneEpsilon = 1e-4f;
        [SerializeField] private Vector2 capNdcMargin = new Vector2(0.01f, 0.01f);
        [SerializeField] private int stencilCameraCapacity = 4;

        [Header("Piece lifetime (DESIGN 7.10, provisional)")]
        [Tooltip("Retire far, unseen pieces of characters' cuts once more than the threshold are alive. Off: nothing new is started.")]
        [SerializeField] private bool pieceLifetimeEnabled = true;

        [Tooltip("Live character pieces above which retirements start.")]
        [SerializeField] private int pieceLifetimeThreshold = 256;

        [Tooltip("A candidate's bounds are at least this far from the viewer, in metres, and outside both eyes' views.")]
        [SerializeField] private float pieceLifetimeDistance = 20f;

        [Tooltip("Candidates looked at, and retirements started, per frame at most.")]
        [SerializeField] private int pieceLifetimeExaminePerFrame = 16;
        [SerializeField] private int pieceLifetimeRetirePerFrame = 4;

        [Tooltip("A frame with less Main budget left than this, in seconds, looks at nothing; a step stops after the other.")]
        [SerializeField] private float pieceLifetimeMinRemainingMainSeconds = 0.004f;
        [SerializeField] private float pieceLifetimeMaxStepSeconds = 0.0005f;

        [Header("Building rest (trial, 2026-09-29)")]
        [Tooltip("Put supported building pieces to sleep explicitly after a timeout (a game rule; off by default). Off: nothing is tracked.")]
        [SerializeField] private bool buildingRestEnabled = false;

        [Tooltip("Sleep: Rigidbody.Sleep once. Kinematic: velocities zeroed and isKinematic until the same building's next re-cut is published.")]
        [SerializeField] private BuildingRestMode buildingRestMode = BuildingRestMode.Sleep;

        [Tooltip("Physics seconds a cut's pieces must have been published for before they may rest.")]
        [SerializeField] private double buildingRestTimeoutSeconds = 1.0;

        [Tooltip("Consecutive physics steps a piece must be supported for (a from-below contact chain to the ground).")]
        [SerializeField] private int buildingRestSupportSteps = 3;

        [Tooltip("A contact counts as support when |normal.y| is at least this (0.5: within 60 degrees of the vertical) and lies under the piece's centre.")]
        [SerializeField] private float buildingRestSupportNormalCos = 0.5f;

        [Tooltip("Sleep and wake events kept in memory for the record.")]
        [SerializeField] private int buildingRestEventRecords = 256;

        [Header("Building fusion (trial, 2026-09-29)")]
        [Tooltip("Fuse held building pieces into one kinematic group body and cut a group into two compound bodies (off by default; needs the kinematic rest and no building World D6).")]
        [SerializeField] private bool buildingFusionEnabled = false;

        [Tooltip("How many pieces one frame may fuse or move between groups (the switch is Main work).")]
        [SerializeField] private int buildingFusionPerFrame = 8;

        [Tooltip("Real playing seconds after a building's last cut before the groups it left are aggregated (at most one resting and one free group); a different Slash on the building aggregates at once. 0: only by the next Slash.")]
        [SerializeField] private float buildingFusionDeadlineSeconds = 2f;

        [Tooltip("The Main time (ms) one fusion Step may begin work within: Finals, aggregations, merges, fusions, publications. A unit begun runs to its end and the overrun is recorded.")]
        [SerializeField] private float buildingFusionMainBudgetMs = 1.5f;

        [Header("Building hull (trial, 2026-09-30)")]
        [Tooltip("A building is one group with one convex hull: hits, cuts and fusions read that hull; the display members carry no physics (off by default; buildings only; needs the building World D6 off).")]
        [SerializeField] private bool buildingHullEnabled = false;

        [Tooltip("Real seconds after a hull cut before its two sides are fused back into one hull (a next Slash on the building fuses at once). 0: only by the next Slash.")]
        [SerializeField] private float buildingHullDeadlineSeconds = 0.9f;

        [Tooltip("The Main time (ms) one hull Step may begin units within (checks, publications, unions, adoptions).")]
        [SerializeField] private float buildingHullMainBudgetMs = 1.5f;

        /// <summary>The penetration a fused hull may add into anything outside its group (metres): a diagnostic value, not a product one (0.1 in the first Player run, 0.005 asked for the next).</summary>
        [SerializeField] private float buildingHullMaxNewPenetrationMetres = 0.1f;

        /// <summary>The sides' motion time after a hull cut (real seconds; 0: off, the rest decides as before): past it, or at the next Slash's hit on the building, the moving sides are fixed for show (velocity zero, kinematic), not by their support. A diagnostic value (0.25 asked first).</summary>
        [SerializeField] private float buildingHullStageSeconds = 0f;

        /// <summary>Whether the two sides of a hull cut are held by a short sibling constraint (the Provisional D6: along the normal 0 .. opening, the plane and the twist free) until they are fixed. Only with a motion time.</summary>
        [SerializeField] private bool buildingHullSiblingD6 = false;

        /// <summary>The opening the sibling constraint allows along the normal (metres). A diagnostic value (0.05 asked first).</summary>
        [SerializeField] private float buildingHullSiblingOpeningMetres = 0.05f;

        /// <summary>The always-kinematic mode (2026-09-30, Editor prototype, off by default): a building is one kinematic body, hull and collider for good; a hit cuts the display (animated: the upper side drops) and exchanges the hull best-effort.</summary>
        [SerializeField] private bool buildingHullKinematicDisplay = false;

        /// <summary>The display's drop time (real seconds). A diagnostic value (0.25 asked first).</summary>
        [SerializeField] private float buildingHullAnimationSeconds = 0.25f;

        /// <summary>How far the upper side drops for a horizontal cut / a vertical one (metres; interpolated in between). Diagnostic values (0.15 / 0.02 asked first).</summary>
        [SerializeField] private float buildingHullDropHorizontalMetres = 0.15f;
        [SerializeField] private float buildingHullDropVerticalMetres = 0.02f;

        /// <summary>
        /// A quality setting of the always-kinematic mode (2026-10-01; 0: off): N, the display geometries a building may be cut
        /// into -- once its live, non-empty, committed display fragments reach N it is cut no more. An integer of 2 or more.
        /// </summary>
        [SerializeField] private int buildingHullGeometryLimit = 0;

        /// <summary>Art settings of the always-kinematic mode with a cut limit: the drop D(n) = D0 (1 - log2 n / log2 N)^p, p (finite, above 0) and D0 (metres, above 0).</summary>
        [SerializeField] private float buildingHullDropExponent = 1f;
        [SerializeField] private float buildingHullDropBaseMetres = 0.5f;

        [Header("Ledger")]
        [Tooltip(
            "How many accepted cuts may be incomplete at once (DESIGN 7.1's incomplete budget): a count compared at "
            + "acceptance, nothing is allocated for it. A group cut takes one per crossed member all at once, so it is "
            + "sized past any group (2026-09-30: 32 refused a 34-member cut of the one-anchor college_001 even with the "
            + "ledger empty). The cook's concurrency and the Main budget bound the work, not this.")]
        [SerializeField]
        private int maxIncompleteCuts = 4096;

        [Header("Separation impulse (DESIGN 7.2; provisional, mass only)")]
        [Tooltip(
            "Whether each free child's separation impulse is decided from its mass (J = k × mass, below). Off, the "
            + "callers' own values are used.")]
        [SerializeField]
        private bool separationImpulseByMass;

        [Tooltip(
            "k, in N·s per kg, used when the switch above is on: each free child is given J = k × its mass at the first "
            + "application, along the adopted normal, whatever the plane's orientation. 0 is a valid value and gives no "
            + "separation impulse. The direction input of DESIGN 7.2 is not implemented.")]
        [SerializeField]
        private float separationImpulsePerKg;

        // The adopted values (DESIGN 7.2.2, 2026-09-28). A profile that stores no value of its own takes these, so a
        // change of them reaches every scene whose profile does not store the three; the shared sandbox profile stores
        // them explicitly.
        [Header("Building World D6 (DESIGN 7.2.2)")]
        [Tooltip("L1: the horizontal distance limit of a first-split building child, in metres.")]
        [SerializeField]
        private float buildingWorldFirstLimitMetres = 1f;

        [Tooltip("Off (a trial comparison only): no building World D6 is made at all. On: as DESIGN 7.2.2.")]
        [SerializeField] private bool buildingWorldEnabled = true;

        [Tooltip(
            "A1: the requested symmetric angle limit of a first-split building child, in degrees (0 to 180). The "
            + "current engine keeps a swing (Y, Z) limit at 3 degrees at least; twist (X) keeps the request (DESIGN 7.2.2).")]
        [SerializeField]
        private float buildingWorldFirstAngleDegrees = 30f;

        [Tooltip("r: the common ratio both limits shrink by per split depth, strictly between 0 and 1.")]
        [SerializeField]
        private float buildingWorldRatio = 0.5f;

        [Tooltip(
            "How many system constraints -- Provisional sibling constraints and building World D6 together -- the "
            + "scene may hold: a count compared at acceptance, nothing is allocated for it. A cut whose constraints may "
            + "not fit is not accepted, and its source stays as it is (the room comes back as Provisional pairs end). "
            + "Sized past what the display's limits can hold (one D6 at most per building piece, one sibling constraint "
            + "per Provisional pair), so it is not what ends play (2026-09-29: 256 was reached in the playable city).")]
        [SerializeField]
        private int systemConstraintCapacity = 8192;

        [Header("Ending")]
        [Tooltip(
            "How long an explicit shutdown gives the workers to hand back what they are running, in milliseconds. "
            + "It is a deadline for an ending that was asked for, not a wait inside a frame.")]
        [SerializeField]
        private int shutdownTimeoutMilliseconds = 5000;

        public int VertexLimit => vertexLimit;

        public int ConcurrentCookReservations => concurrentCookReservations;

        public float SupportEpsilon => supportEpsilon;

        public float AnchorEpsilon => anchorEpsilon;

        /// <summary>The CPU vertex reservation: the vertices' absolute limit, in vertices.</summary>
        public int VertexReserve => vertexReserve;

        /// <summary>The vertices committed at once.</summary>
        public int VertexInitialCommit => vertexInitialCommit;

        /// <summary>The CPU index reservation: the indices' absolute limit, in indices.</summary>
        public int IndexReserve => indexReserve;

        /// <summary>The indices committed at once.</summary>
        public int IndexInitialCommit => indexInitialCommit;

        /// <summary>The GPU copy's first vertex capacity.</summary>
        public int GpuVertexInitialCapacity => gpuVertexInitialCapacity;

        /// <summary>The GPU copy's first index capacity.</summary>
        public int GpuIndexInitialCapacity => gpuIndexInitialCapacity;

        public int GeometryDescriptorCapacity => geometryDescriptorCapacity;

        public int SubmeshCapacity => submeshCapacity;

        public int VertexBlockCapacity => vertexBlockCapacity;

        public int WaitingCapacity => waitingCapacity;

        public int ReservedForUrgent => reservedForUrgent;

        public int FrameBudget => frameBudget;

        public int UnityJobCapacity => unityJobCapacity;

        public int GeometryWorkerCount => geometryWorkerCount;

        public int BackgroundWorkerCount => backgroundWorkerCount;

        public int DrawCommandCapacity => drawCommandCapacity;

        public int DrawInstanceCapacity => drawInstanceCapacity;

        /// <summary>How many render fragments the display has room for at once, at first (DESIGN 5.6).</summary>
        public int RenderFragmentCapacity => renderFragmentCapacity;

        public int GeometryReferenceCapacity => geometryReferenceCapacity;

        public int DisplayInstanceCapacity => displayInstanceCapacity;

        public int BranchCapacity => branchCapacity;

        public int CandidateCapacity => candidateCapacity;

        public int GeometryReferenceCapacityLimit => geometryReferenceCapacityLimit;

        public int DisplayInstanceCapacityLimit => displayInstanceCapacityLimit;

        /// <summary>The piece lifetime's settings (DESIGN 7.10), from the provisional values above.</summary>
        public CutFragmentLifetimeSettings PieceLifetime =>
            new CutFragmentLifetimeSettings(
                pieceLifetimeEnabled, pieceLifetimeThreshold, pieceLifetimeDistance, pieceLifetimeExaminePerFrame,
                pieceLifetimeRetirePerFrame, pieceLifetimeMinRemainingMainSeconds, pieceLifetimeMaxStepSeconds);

        /// <summary>The trial fusion of held building pieces (2026-09-29): off unless the profile switches it on.</summary>
        public BuildingFusionSettings BuildingFusion => new BuildingFusionSettings(buildingFusionEnabled, buildingFusionPerFrame, buildingFusionDeadlineSeconds, buildingFusionMainBudgetMs * 0.001);

        public BuildingHullSettings BuildingHull => new BuildingHullSettings(buildingHullEnabled, buildingHullDeadlineSeconds, buildingHullMainBudgetMs * 0.001, buildingHullMaxNewPenetrationMetres, buildingHullStageSeconds, buildingHullSiblingD6, buildingHullSiblingOpeningMetres, buildingHullKinematicDisplay, buildingHullAnimationSeconds, buildingHullDropHorizontalMetres, buildingHullDropVerticalMetres,
            buildingHullGeometryLimit, buildingHullDropExponent, buildingHullDropBaseMetres);

        /// <summary>The trial rest of building pieces (2026-09-29): off unless the profile switches it on.</summary>
        public BuildingRestSettings BuildingRest =>
            new BuildingRestSettings(
                buildingRestEnabled, buildingRestMode, buildingRestTimeoutSeconds, buildingRestSupportSteps, buildingRestSupportNormalCos,
                buildingRestEventRecords);

        /// <summary>How far the display's counts may grow, from the limits above.</summary>
        public VpLogicalCutDisplayLimits DisplayLimits =>
            new VpLogicalCutDisplayLimits(
                drawCommandCapacityLimit, drawInstanceCapacityLimit, branchCapacityLimit, candidateCapacityLimit,
                renderFragmentCapacity, renderFragmentCapacityLimit);

        public int ChainDepth => chainDepth;

        public int MaxIncompleteCuts => maxIncompleteCuts;

        public BuildingWorldD6Settings BuildingWorld =>
            new BuildingWorldD6Settings(buildingWorldFirstLimitMetres, buildingWorldFirstAngleDegrees, buildingWorldRatio, buildingWorldEnabled);

        public int SystemConstraintCapacity => systemConstraintCapacity;

        public int ShutdownTimeoutMilliseconds => shutdownTimeoutMilliseconds;

        /// <summary>Whether the separation impulse is decided from each child's mass (J = k × mass).</summary>
        public bool SeparationImpulseByMass => separationImpulseByMass;

        /// <summary>k of the provisional strength J = k × mass, in N·s per kg. 0 is a valid value: no impulse.</summary>
        public float SeparationImpulsePerKg => separationImpulsePerKg;

        /// <summary>
        /// The strength a world gives its driver: J = k × mass when the switch is on (k may be 0), null when it is off,
        /// which leaves the callers' own values.
        /// </summary>
        public SeparationImpulseStrength SeparationStrength
        {
            get
            {
                if (!separationImpulseByMass)
                {
                    return null;
                }

                float perKg = separationImpulsePerKg;
                return mass => (float)(perKg * mass);
            }
        }

        /// <summary>The cap stencil settings of DESIGN 5.6, made from the values above.</summary>
        public VpStencilSettings StencilSettings =>
            new VpStencilSettings(
                maxStencilColours, capFacingEpsilon, capPlaneEpsilon, capNdcMargin, stencilCameraCapacity);

        /// <summary>
        /// Whether every value holds together well enough to build a world from. It says what is wrong rather than
        /// correcting anything: a profile is the author's, and nothing here is quietly raised to a minimum.
        /// </summary>
        public bool IsUsable(out string reason)
        {
            reason = null;
            if (vertexLimit <= 0)
            {
                reason = "the kernel's vertex limit must be positive";
            }
            else if (concurrentCookReservations < 2)
            {
                reason = "at least two cook reservations, so that cuts of different owners can run beside each other";
            }
            else if (!(supportEpsilon >= 0f) || !(anchorEpsilon >= 0f)
                     || float.IsNaN(supportEpsilon) || float.IsNaN(anchorEpsilon)
                     || float.IsInfinity(supportEpsilon) || float.IsInfinity(anchorEpsilon))
            {
                reason = "the epsilons must be finite and not negative";
            }
            else if (vertexInitialCommit <= 0 || indexInitialCommit <= 0 || geometryDescriptorCapacity <= 0
                     || submeshCapacity <= 0 || vertexBlockCapacity <= 0)
            {
                reason = "the storage counts must be positive";
            }
            else if (vertexReserve < vertexInitialCommit || indexReserve < indexInitialCommit)
            {
                reason = "a reservation must hold at least what is committed at once";
            }
            else if (gpuVertexInitialCapacity <= 0 || gpuIndexInitialCapacity <= 0
                     || gpuVertexInitialCapacity > vertexReserve || gpuIndexInitialCapacity > indexReserve)
            {
                reason = "the GPU's first capacities must be positive and within the CPU reservations";
            }
            else if (waitingCapacity <= 0 || reservedForUrgent < 0 || reservedForUrgent >= waitingCapacity
                     || frameBudget <= 0 || unityJobCapacity <= 0 || geometryWorkerCount <= 0
                     || backgroundWorkerCount <= 0)
            {
                reason = "the dispatcher's sizes must be positive, and the urgent reserve smaller than the queue";
            }
            else if (drawCommandCapacity <= 0 || drawInstanceCapacity <= 0 || renderFragmentCapacity <= 0
                     || geometryReferenceCapacity <= 0
                     || displayInstanceCapacity <= 0 || branchCapacity <= 0 || candidateCapacity <= 0
                     || chainDepth <= 0)
            {
                reason = "the display's counts must be positive";
            }
            else if (drawCommandCapacityLimit < drawCommandCapacity || drawInstanceCapacityLimit < drawInstanceCapacity
                     || renderFragmentCapacityLimit < renderFragmentCapacity
                     || geometryReferenceCapacityLimit < geometryReferenceCapacity
                     || displayInstanceCapacityLimit < displayInstanceCapacity || branchCapacityLimit < branchCapacity
                     || candidateCapacityLimit < candidateCapacity)
            {
                reason = "a display count's limit must be at least its first capacity";
            }
            else if (!PieceLifetime.IsValid)
            {
                reason = "the piece lifetime's counts and times must be positive and its threshold and distance not negative";
            }
            else if (!BuildingRest.IsValid)
            {
                reason = "the building rest's timeout must not be negative, its support steps at least 1, its normal cosine in (0, 1] and its record count not negative";
            }
            else if (maxStencilColours <= 0 || stencilCameraCapacity <= 0)
            {
                reason = "at least one stencil colour and one camera";
            }
            else if (maxIncompleteCuts <= 0)
            {
                reason = "at least one incomplete cut may be outstanding";
            }
            else if (shutdownTimeoutMilliseconds < 0)
            {
                reason = "the shutdown deadline cannot be negative";
            }
            else if (!(separationImpulsePerKg >= 0f) || float.IsInfinity(separationImpulsePerKg))
            {
                reason = "the separation impulse per kg must be finite and not negative";
            }

            return reason == null;
        }
    }
}
