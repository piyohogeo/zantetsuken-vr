using UnityEngine;
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
    /// commands or cameras there may be; what that costs in memory is the storage's own business and is not what these
    /// limit. A profile is not a memory budget and does not stand in for one.
    /// </para>
    /// <para>
    /// **What it does not decide.** The separation impulses of a cut are the caller's own two values (DESIGN 7.2) and
    /// are not here; neither is what a hit is, nor anything of Character, the building constraint or XR.
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
        [SerializeField] private int vertexCapacity = 65536;
        [SerializeField] private int indexCapacity = 262144;
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
        [SerializeField] private int drawCommandCapacity = 256;
        [SerializeField] private int drawInstanceCapacity = 512;
        [SerializeField] private int geometryReferenceCapacity = 512;
        [SerializeField] private int displayInstanceCapacity = 512;
        [SerializeField] private int branchCapacity = 128;
        [SerializeField] private int candidateCapacity = 512;
        [SerializeField] private int chainDepth = 16;

        [Header("Stencil caps")]
        [SerializeField] private int maxStencilColours = 4;
        [SerializeField] private float capFacingEpsilon = 0.01f;
        [SerializeField] private float capPlaneEpsilon = 1e-4f;
        [SerializeField] private Vector2 capNdcMargin = new Vector2(0.01f, 0.01f);
        [SerializeField] private int stencilCameraCapacity = 4;

        [Header("Ledger")]
        [Tooltip("How many accepted cuts may be incomplete at once (DESIGN 7.1's incomplete budget).")]
        [SerializeField]
        private int maxIncompleteCuts = 32;

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

        public int VertexCapacity => vertexCapacity;

        public int IndexCapacity => indexCapacity;

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

        public int ChainDepth => chainDepth;

        public int MaxIncompleteCuts => maxIncompleteCuts;

        public int ShutdownTimeoutMilliseconds => shutdownTimeoutMilliseconds;

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
            else if (vertexCapacity <= 0 || indexCapacity <= 0 || geometryDescriptorCapacity <= 0
                     || submeshCapacity <= 0 || vertexBlockCapacity <= 0)
            {
                reason = "the storage counts must be positive";
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

            return reason == null;
        }
    }
}
