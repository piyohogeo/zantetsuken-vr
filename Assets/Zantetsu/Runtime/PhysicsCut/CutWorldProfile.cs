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
        [SerializeField] private int gpuVertexInitialCapacity = 65536;

        [Tooltip("The GPU copy's first capacity in indices, by the same rule.")]
        [SerializeField] private int gpuIndexInitialCapacity = 262144;

        [SerializeField] private int geometryDescriptorCapacity = 512;
        [SerializeField] private int submeshCapacity = 2048;
        [SerializeField] private int vertexBlockCapacity = 2048;

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
        [SerializeField] private int drawCommandCapacity = 256;
        [SerializeField] private int drawInstanceCapacity = 512;
        [SerializeField] private int geometryReferenceCapacity = 512;
        [SerializeField] private int displayInstanceCapacity = 512;
        [SerializeField] private int branchCapacity = 128;
        [SerializeField] private int candidateCapacity = 512;
        [SerializeField] private int chainDepth = 16;

        [Tooltip("How far each display count may grow; at least its first capacity. Equal keeps that count fixed.")]
        [SerializeField] private int drawCommandCapacityLimit = 2048;
        [SerializeField] private int drawInstanceCapacityLimit = 4096;
        [SerializeField] private int geometryReferenceCapacityLimit = 4096;
        [SerializeField] private int displayInstanceCapacityLimit = 4096;
        [SerializeField] private int branchCapacityLimit = 1024;
        [SerializeField] private int candidateCapacityLimit = 4096;

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

        [Header("Ledger")]
        [Tooltip("How many accepted cuts may be incomplete at once (DESIGN 7.1's incomplete budget).")]
        [SerializeField]
        private int maxIncompleteCuts = 32;

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
            + "scene may hold. A cut whose constraints would not fit cannot be built.")]
        [SerializeField]
        private int systemConstraintCapacity = 256;

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

        /// <summary>How far the display's counts may grow, from the limits above.</summary>
        public VpLogicalCutDisplayLimits DisplayLimits =>
            new VpLogicalCutDisplayLimits(
                drawCommandCapacityLimit, drawInstanceCapacityLimit, branchCapacityLimit, candidateCapacityLimit);

        public int ChainDepth => chainDepth;

        public int MaxIncompleteCuts => maxIncompleteCuts;

        public BuildingWorldD6Settings BuildingWorld =>
            new BuildingWorldD6Settings(buildingWorldFirstLimitMetres, buildingWorldFirstAngleDegrees, buildingWorldRatio);

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
            else if (drawCommandCapacity <= 0 || drawInstanceCapacity <= 0 || geometryReferenceCapacity <= 0
                     || displayInstanceCapacity <= 0 || branchCapacity <= 0 || candidateCapacity <= 0
                     || chainDepth <= 0)
            {
                reason = "the display's counts must be positive";
            }
            else if (drawCommandCapacityLimit < drawCommandCapacity || drawInstanceCapacityLimit < drawInstanceCapacity
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
