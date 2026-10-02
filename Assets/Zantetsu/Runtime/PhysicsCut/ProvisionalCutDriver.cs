using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The Player termination request of DESIGN 4, as the things that must stop see it: one non-persistent latch,
    /// owned by the composition root and fixed one way by the first cause Main handled.
    /// <para>
    /// **What reads it stops doing two things**: accepting new cuts, and publishing what is not published yet. It is
    /// read on the main thread, where those two happen. Nothing here ends anything, waits for anything or frees
    /// anything -- that is the Player's own ending, and a different thing entirely.
    /// </para>
    /// </summary>
    public interface ICutTerminationLatch
    {
        /// <summary>Whether the termination has been requested. Once true, it stays true.</summary>
        bool TerminationRequested { get; }
    }

    /// <summary>What one asked-for cut came to, as far as this driver is concerned.</summary>
    public enum ProvisionalCutAcceptance
    {
        /// <summary>Accepted and published: the Provisional pair is in the scene, in this same call.</summary>
        Published = 0,

        /// <summary>
        /// The plane leaves one side of the shape with no convex at all (DESIGN 7.6). Nothing is accepted and nothing
        /// changes: the existing no-op, not a failure and not an abort.
        /// </summary>
        EmptySide = 1,

        /// <summary>
        /// The ledger did not accept it, or its anchor distribution was refused: nothing of the physics changed and
        /// the fragment is as it was. An ordinary outcome of asking, with the ledger's own reason beside it.
        /// </summary>
        NotAccepted = 2,

        /// <summary>
        /// The request itself does not hold together -- no such fragment, no owner, a plane or an epsilon that is not
        /// usable, a driver that is not bound. Nothing is accepted.
        /// </summary>
        InvalidRequest = 3,

        /// <summary>
        /// It was accepted but could not be built or published, so it took the ordinary continuation of
        /// DESIGN 7.1.1: the cut is aborted and the source retired (unless the ledger found it stale, which retires
        /// nothing).
        /// </summary>
        Aborted = 4,

        /// <summary>
        /// The ledger refused the anchor distribution itself (DESIGN 7.1): the cut stays accepted and unprepared, and
        /// nothing else changed. It is **not** a wait -- the same epsilon over the same anchors is refused again, so
        /// there is nothing to come back for -- and it is **not** an abort, which DESIGN reserves for infeasibility.
        /// What this driver held goes back; what the ledger holds is the ledger's.
        /// </summary>
        AnchorsRefused = 5,

        /// <summary>
        /// The ledger refused to publish it: its authority moved since acceptance, or it is no longer the active cut.
        /// Nothing of the physics changed, and the cut's own record is ended -- what it held has gone back, and a
        /// stale cut retires no fragment.
        /// </summary>
        Stale = 6,

        /// <summary>Admitted once; input and any unpublished candidate are held until frame budget permits publication.</summary>
        Pending = 7,

        /// <summary>
        /// **Not accepted yet** -- before the ledger's admission, so no Operation and no admission was asked: a prepared
        /// character's hit whose display input could not be given room that others hold and will return (DESIGN 4.5.3,
        /// 4.5.4). The request -- the pose the hit met, its plane and impulses, the character's fragment -- is held, and
        /// a later update of the driver takes it up once as the same request, into the ordinary acceptance. Room that
        /// cannot come back is not this: that is the common termination.
        /// </summary>
        Held = 8,
    }

    /// <summary>The two Main stages of a Provisional cut the frame budget is asked for (DESIGN 7.1.1).</summary>
    public enum ProvisionalBudgetStage
    {
        /// <summary>The inactive pair's build.</summary>
        Build = 0,

        /// <summary>The pair's publication into the scene.</summary>
        Publication = 1,
    }

    /// <summary>
    /// One limited run of a Pending cut's stage past the frame budget (DESIGN 7.1.1): taken when the stage's prediction
    /// is more than the whole Main budget of a frame, so that no frame could ever fit it. What was expected, what the
    /// budget and the remainder were at the decision, and what the stage then measured.
    /// </summary>
    public readonly struct ProvisionalBudgetOverrun
    {
        public ProvisionalBudgetOverrun(
            int frame, CutOperationId operation, ProvisionalBudgetStage stage,
            double expectedSeconds, double budgetSeconds, double remainingSeconds, double measuredSeconds)
        {
            Frame = frame;
            Operation = operation;
            Stage = stage;
            ExpectedSeconds = expectedSeconds;
            BudgetSeconds = budgetSeconds;
            RemainingSeconds = remainingSeconds;
            MeasuredSeconds = measuredSeconds;
        }

        public int Frame { get; }

        public CutOperationId Operation { get; }

        public ProvisionalBudgetStage Stage { get; }

        /// <summary>The prediction the decision compared: build and publication together for a build, else publication.</summary>
        public double ExpectedSeconds { get; }

        /// <summary>The whole Main budget of a frame the prediction was more than.</summary>
        public double BudgetSeconds { get; }

        /// <summary>What was left of this frame's Main budget at the decision; it may be 0 or less.</summary>
        public double RemainingSeconds { get; }

        /// <summary>What the stage took, as it was added to that stage's cost history.</summary>
        public double MeasuredSeconds { get; }

        public override string ToString()
        {
            return "frame " + Frame + " op" + Operation.value + " " + Stage + " expected " + (ExpectedSeconds * 1000.0).ToString("F3")
                + " ms > budget " + (BudgetSeconds * 1000.0).ToString("F3") + " ms, remaining " + (RemainingSeconds * 1000.0).ToString("F3")
                + " ms, measured " + (MeasuredSeconds * 1000.0).ToString("F3") + " ms";
        }
    }

    /// <summary>What a caller asks for when it asks for a cut.</summary>
    public struct ProvisionalCutAsk
    {
        /// <summary>The live fragment to cut.</summary>
        public LogicalFragmentId source;

        /// <summary>The adopted plane, in the source fragment's own logical frame (DESIGN 5.2).</summary>
        public float4 plane;

        /// <summary>
        /// The impulse each child owner is given, in newton-seconds (DESIGN 7.2). **The caller's two values**: the
        /// magnitudes are not decided here, no constant of this path's own stands in for them, and the direction they
        /// are applied in is the existing one.
        /// </summary>
        public float positiveSeparationImpulse;

        /// <summary>The negative child's, by the same rule.</summary>
        public float negativeSeparationImpulse;

        /// <summary>The FragmentRenderAnchor of the source, in the world, for the first-split velocity (DESIGN 7.2).</summary>
        public float3 renderAnchor;

        /// <summary>The Slash the hit belongs to (the hit detector's), or 0: a building's aggregation tells a next Slash from more hits of the same one by it.</summary>
        public long slashId;

        /// <summary>
        /// The identity of the plane the Slash adopted for this hit (the hit detector's: one per sweep, shared by every
        /// hit that sweep found), or 0 when the ask carries none. A building's fusion tells a hit that is the same
        /// request as a cut being prepared or in progress -- the same Slash and the same adopted plane -- from another
        /// plane, however close, by this and never by a tolerance.
        /// </summary>
        public long adoptedPlaneId;

        /// <summary>The adopted plane's value in the world, as the detector had it, for the fusion's own frames.</summary>
        public float4 adoptedPlaneWorld;
    }

    /// <summary>
    /// The one product caller of the Provisional path: it takes an asked-for cut, accepts it, builds the pair and puts
    /// it into the physics scene when budget permits, normally in the same update (DESIGN 7.1.1, 14 T-091).
    /// Budget exhaustion preserves the admission for a later safe opportunity.
    /// <para>
    /// **It is bound, not built.** The ledger, the correspondence, the cut cook, the shared frame and the display are
    /// given to it (<see cref="Bind"/>); it creates none of them and owns none of them. It is not a composition root
    /// and has no scheduler: the only thing it keeps is what each accepted cut owns
    /// (<see cref="ProvisionalCutTransaction"/>). Deferred admissions stay there, not in a retry queue.
    /// </para>
    /// <para>
    /// **The normal order, when budget permits.** In <c>Update</c>: classify the source's shape against the plane, accept the cut,
    /// prepare its anchors, build the unpublished pair, publish it — and then, in the same call, submit the final cut
    /// and let <see cref="SharedWorkFrame.Update"/> collect and submit what the frame's remaining budget allows. In
    /// <c>LateUpdate</c>: one more turn of the same frame, which may publish too. Then, after every late update,
    /// <see cref="CutPhysicsStep"/> -- the one place the physics scene is advanced (DESIGN 4.4) -- decides whether to
    /// simulate once and lets this driver's display collect (<see cref="CollectSnapshot"/>): publication, the
    /// simulation, the collection, the drawing, in that order (DESIGN 7.1.1). Nothing here steps or simulates, and a
    /// switch is never inside a simulation: the simulation is a call of its own on the main thread, after the late
    /// updates.
    /// </para>
    /// <para>
    /// **Nothing of the cut is waited for.** Taking the cook's reservation, the numbers coming back, the bake, and the
    /// display geometry are none of them conditions of publishing: the pair goes into the scene first and the cut
    /// follows it. What **is** required beforehand is the classification and the anchor distribution -- a display
    /// cannot draw two sides without one, and a pair cannot be allocated without the other -- so a cut missing those
    /// is an ordinary wait or an ordinary refusal, and only a pair that cannot be established is an abort.
    /// </para>
    /// </summary>
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-100)]
    public sealed partial class ProvisionalCutDriver : MonoBehaviour
    {
        // The driver's three turns of a frame, each whole and none inside another.
        private static readonly ProfilerMarker s_update = new ProfilerMarker("Zantetsu.Driver.Update");
        private static readonly ProfilerMarker s_lateUpdate = new ProfilerMarker("Zantetsu.Driver.LateUpdate");
        private static readonly ProfilerMarker s_afterRenderingTurn = new ProfilerMarker("Zantetsu.Driver.AfterRendering");

        // A cut's acceptance, apart from each other: the Provisional pair's build and its publication.
        private static readonly ProfilerMarker s_build = new ProfilerMarker("Zantetsu.Request.Build");
        private static readonly ProfilerMarker s_publish = new ProfilerMarker("Zantetsu.Request.Publish");

        private readonly List<ProvisionalCutTransaction> _transactions = new List<ProvisionalCutTransaction>(4);
        private readonly List<ProvisionalCutAsk> _asked = new List<ProvisionalCutAsk>(4);
        private LogicalCutLedger _ledger;
        private PhysicsOwnerRegistry _registry;
        private PhysicsCutCook _cook;

        /// <summary>
        /// The world's collider template, handed to every Provisional build; see
        /// <see cref="ProvisionalOwnerBuildInput.colliderTemplate"/>. None means the builds make colliders call by call.
        /// </summary>
        internal MeshCollider ColliderTemplate { get; set; }

        /// <summary>The trial fusion of building groups (2026-09-29), when the world has one: a cut of a fused member is the group's.</summary>
        internal BuildingFusion Fusion { get; set; }
        private SharedWorkFrame _frame;
        private VpLogicalCutDisplay _display;
        private float _supportEpsilon;
        private float _anchorEpsilon;

        // The building World D6 settings and the system constraint capacity (DESIGN 7.2.2, O-048). Until the world
        // configures them: the adopted settings, and no capacity counted.
        private BuildingWorldD6Settings _buildingWorld = BuildingWorldD6Settings.Adopted;
        private int? _constraintCapacity;
        private int _vertexLimit;
        private Func<int> _frameSource;
        private ProvisionalCutRecovery _recovery;
        private CutDag _dag;
        private ICutTerminationLatch _latch;

        // The product reads the same live Main clock as Physics.Simulate. Tests may supply a deterministic remainder.
        internal Func<double> RemainingMainSeconds;

        // The whole Main budget of a frame, which a prediction is compared with to tell a stage that cannot fit any frame
        // from one that did not fit this frame's remainder: the product's own (CutPhysicsStep.MainBudgetSeconds, the
        // session's). Tests may supply a deterministic one.
        internal Func<double> MainBudgetSeconds;

        // The frame this world last ran a Pending stage past the budget in: at most one stage -- a build or a publication
        // -- in a frame, however many times the frame's updates call this driver (DESIGN 7.1.1).
        private int _overrunFrame = int.MinValue;

        /// <summary>The last limited run past the budget (DESIGN 7.1.1), or the default if none has been taken.</summary>
        public ProvisionalBudgetOverrun LastBudgetOverrun { get; private set; }

        /// <summary>How many limited runs past the budget this driver has taken.</summary>
        public int BudgetOverrunCount { get; private set; }

        /// <summary>
        /// One cut whose physics came back without products and was therefore aborted (DESIGN 7.1.1): its operation, how
        /// the cut ended, and -- for a kernel failure -- the kernel's status, its clip status and the convex it failed on.
        /// </summary>
        public struct CutFailure
        {
            public CutOperationId operation;
            public PhysicsCutOutcomeKind outcome;
            public ConvexCutOwnerStatus kernelStatus;
            public int cutStatus, failedConvex, frame;
            public LogicalFragmentId source;
            public PhysicsCutHullRejection hull;   // the first convex PhysX refused, when that ended the cut
            public int hullMeshes;   // how many of its convexes PhysX refused
        }

        /// <summary>
        /// One cut the driver aborted -- the ordinary continuation of DESIGN 7.1.1, which retires the source -- and why:
        /// its build refused (the outcome, with the system constraint room it was built against and what it needed) or its
        /// Final set could not be established (the publication's outcome).
        /// </summary>
        public struct CutAbort
        {
            public CutOperationId operation;
            public int frame;
            public string stage;
            public PhysicsOwnerBuildOutcome build;
            public string publication;
            public int constraintRoom, constraintsNeeded;
        }

        private readonly CutAbort[] _firstAborts = new CutAbort[8];
        private readonly Dictionary<string, int> _abortReasons = new Dictionary<string, int>();

        /// <summary>Cuts aborted by this driver (build or Final set), by reason; the first eight are kept.</summary>
        public int AbortCount { get; private set; }

        public int KeptAbortCount => Math.Min(AbortCount, _firstAborts.Length);

        public CutAbort KeptAbort(int index) => _firstAborts[index];

        /// <summary>Requests not accepted because the system constraints they may need had no room; their sources stay as they were.</summary>
        public int ConstraintRoomRefusals { get; private set; }

        private void NoteAbort(in CutAbort abort)
        {
            if (AbortCount < _firstAborts.Length) _firstAborts[AbortCount] = abort;
            AbortCount++;
            string reason = abort.stage + "/" + (abort.stage == "build" ? abort.build.ToString() : abort.publication);
            _abortReasons[reason] = _abortReasons.TryGetValue(reason, out int n) ? n + 1 : 1;
        }

        /// <summary>Raised once per failed cut, when its record takes the ended cut in (never again for the same cut).</summary>
        public event Action<CutFailure> CutFailed;

        /// <summary>Cuts that came back without products and were aborted, and of those the kernel failures.</summary>
        public int FailedCutCount { get; private set; }

        public int KernelFailedCount { get; private set; }

        /// <summary>Of the failed cuts, those whose cook ended because PhysX refused a produced convex (the cook's hull check).</summary>
        public int HullRejectedCutCount { get; private set; }

        private readonly int[] _kernelFailuresByCutStatus = new int[32];

        /// <summary>Kernel failures whose clip ended with the given <c>CutStatus</c> value.</summary>
        public int KernelFailuresWithCutStatus(int cutStatus) => cutStatus >= 0 && cutStatus < _kernelFailuresByCutStatus.Length ? _kernelFailuresByCutStatus[cutStatus] : 0;

        /// <summary>
        /// The first failed cuts, kept to identify them (at most <see cref="KeptFailures"/>; no more are kept). This is
        /// which cut failed and how -- not enough to reproduce it: the plane and the input shape are not kept.
        /// </summary>
        public const int KeptFailures = 8;

        private readonly CutFailure[] _firstFailures = new CutFailure[KeptFailures];

        public int KeptFailureCount { get; private set; }

        public CutFailure KeptFailure(int i) => _firstFailures[i];

        private void CountFailure(ProvisionalCutTransaction at, PhysicsCutRequest request)
        {
            var failure = new CutFailure
            {
                operation = at.Operation, outcome = request.Outcome, kernelStatus = request.KernelStatus,
                cutStatus = request.cut.kernel.cutStatus, failedConvex = request.cut.kernel.failedConvex, frame = CurrentFrame,
                source = at.Source, hull = request.HullRejection, hullMeshes = request.HullRejections.Count,
            };
            if (failure.hull != null)
            {
                _cook.AttributeHullRejection(request, at.Operation, at.Source, "driver");
                HullRejectedCutCount++;
            }

            FailedCutCount++;
            if (failure.outcome == PhysicsCutOutcomeKind.KernelFailed)
            {
                KernelFailedCount++;
                if (failure.cutStatus >= 0 && failure.cutStatus < _kernelFailuresByCutStatus.Length) _kernelFailuresByCutStatus[failure.cutStatus]++;
            }

            if (KeptFailureCount < KeptFailures) _firstFailures[KeptFailureCount++] = failure;
            CutFailed?.Invoke(failure);
        }

        /// <summary>
        /// One line for the end of a world: the failed cuts, the kernel failures by clip status, and the ones kept.
        /// </summary>
        public string FailureSummary()
        {
            var text = new System.Text.StringBuilder();
            text.Append("failed cuts ").Append(FailedCutCount).Append(", KernelFailed ").Append(KernelFailedCount).Append(" [");
            bool first = true;
            for (int s = 0; s < _kernelFailuresByCutStatus.Length; s++)
            {
                if (_kernelFailuresByCutStatus[s] == 0) continue;
                text.Append(first ? "" : ", ").Append((CutStatus)s).Append(' ').Append(_kernelFailuresByCutStatus[s]);
                first = false;
            }

            text.Append("], kept ").Append(KeptFailureCount);
            for (int i = 0; i < KeptFailureCount; i++)
            {
                CutFailure f = _firstFailures[i];
                text.Append("; operation ").Append(f.operation.value).Append(" source ").Append(f.source.value).Append(" frame ").Append(f.frame).Append(' ').Append(f.outcome)
                    .Append(" kernel ").Append(f.kernelStatus).Append(" clip ").Append((CutStatus)f.cutStatus).Append(" convex ").Append(f.failedConvex);
                if (f.hull != null) text.Append(" hull [").Append(f.hull).Append(']').Append(f.hullMeshes > 1 ? " and " + (f.hullMeshes - 1) + " more refused mesh(es)" : "");
            }

            text.Append("; aborted ").Append(AbortCount).Append(" [");
            first = true;
            foreach (KeyValuePair<string, int> r in _abortReasons)
            {
                text.Append(first ? "" : ", ").Append(r.Key).Append(' ').Append(r.Value);
                first = false;
            }

            text.Append("]");
            for (int i = 0; i < KeptAbortCount; i++)
            {
                CutAbort a = _firstAborts[i];
                text.Append("; abort operation ").Append(a.operation.value).Append(" frame ").Append(a.frame).Append(' ').Append(a.stage).Append('/')
                    .Append(a.stage == "build" ? a.build.ToString() : a.publication).Append(" constraint room ").Append(a.constraintRoom).Append(" needed ").Append(a.constraintsNeeded);
            }

            text.Append("; not accepted for system constraint room ").Append(ConstraintRoomRefusals);
            text.Append("; of the failed cuts, refused by PhysX's cooking ").Append(HullRejectedCutCount);

            return text.ToString();
        }

        /// <summary>
        /// Whether this driver is done with an operation: it holds no record of it, neither a live one nor one whose work
        /// is still coming back (the recovery). A cut the driver aborted is settled only once its work has come back.
        /// </summary>
        public bool IsSettled(CutOperationId operation) => TransactionOf(operation) == null && (_recovery == null || !_recovery.Holds(operation));

        /// <summary>
        /// The separation impulse's magnitude per child owner from its mass (DESIGN 7.2), used by every Provisional
        /// publication in place of the ask's two values when set: the hit gives the plane, and the strength is decided
        /// where the children's masses are. Unset, the ask's values are used. The world connects it from its profile.
        /// </summary>
        public SeparationImpulseStrength SeparationStrength { get; set; }
        private readonly SimulateCostHistory _buildCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _publishCosts = new SimulateCostHistory();

        // The two stages' cost histories, for tests that give the predictions deterministically.
        internal SimulateCostHistory BuildCosts => _buildCosts;
        internal SimulateCostHistory PublishCosts => _publishCosts;

        private double RemainingMain => RemainingMainSeconds != null ? RemainingMainSeconds() : CutPhysicsStep.RemainingMainSeconds;

        private double WholeMainBudget => MainBudgetSeconds != null ? MainBudgetSeconds() : CutPhysicsStep.MainBudgetSeconds;

        private bool FitsMain(double expected)
        {
            double remaining = RemainingMain;
            return _frame.Dispatcher.RemainingBudget > 0 && remaining > 0 && expected <= remaining;
        }

        /// <summary>
        /// Whether a Pending stage that did not fit may run past the budget now (DESIGN 7.1.1): only when its prediction
        /// is more than the whole Main budget of a frame -- no remainder could ever fit it, so the ordinary carry-over
        /// would never end and its cost would never be measured again -- and only once in this world's frame. The
        /// dispatch budget is still asked for; the Main remainder need not be positive. A stage that merely did not fit
        /// this frame's remainder is carried over as before.
        /// </summary>
        private bool MayOverrun(double expected)
        {
            return expected > WholeMainBudget && _overrunFrame != CurrentFrame && _frame.Dispatcher.RemainingBudget > 0;
        }

        private void RecordOverrun(ProvisionalCutTransaction transaction, ProvisionalBudgetStage stage, double expected, double remaining, double measured)
        {
            LastBudgetOverrun = new ProvisionalBudgetOverrun(
                CurrentFrame, transaction.Operation, stage, expected, WholeMainBudget, remaining, measured);
            BudgetOverrunCount++;
        }

        private int? RemainingConstraintRoom(ProvisionalCutTransaction except = null)
        {
            if (!_constraintCapacity.HasValue) return null;
            int used = _registry.SystemConstraintCount;
            // A budget-deferred candidate is not in the registry yet, but already owns its constraints; an accepted cut
            // not built yet holds what it may need (its reservation).
            foreach (ProvisionalCutTransaction transaction in _transactions)
            {
                if (transaction == except) continue;
                ProvisionalOwnerCandidate candidate = transaction.Candidate;
                if (candidate == null)
                {
                    if (transaction.Pair == null) used += transaction.ConstraintReservation;
                    continue;
                }

                if (candidate.IsDisposed || candidate.IsDetached) continue;
                used += (candidate.Separation != null ? 1 : 0)
                    + (candidate.Positive.BuildingWorld != null ? 1 : 0)
                    + (candidate.Negative.BuildingWorld != null ? 1 : 0);
            }
            return Math.Max(0, _constraintCapacity.Value - used);
        }

        // The system constraints a cut of this owner may need: the sibling constraint, and a building World D6 on each
        // side of a building child at most (which sides need one is known only from the anchors, after acceptance).
        private static int ConstraintsAtMost(PhysicsFragmentOwner owner) => 1 + (owner.Building.ChildOfSplit().IsBuildingDerived ? 2 : 0);

        /// <summary>
        /// The frame this driver counts by: the engine's, or whatever the caller counts with instead. A display created
        /// with its own frame source has to be given the same one, or "the same frame" would be two different numbers.
        /// </summary>
        private int CurrentFrame => _frameSource != null ? _frameSource() : Time.frameCount;

        /// <summary>Whether this has been given what it drives.</summary>
        public bool IsBound { get; private set; }

        /// <summary>The cuts this is holding something for, published or not.</summary>
        public IReadOnlyList<ProvisionalCutTransaction> Transactions => _transactions;

        /// <summary>What the last <see cref="SharedWorkFrame.Update"/> of this driver came to. For tests.</summary>
        public SharedWorkFrameProgress LastFrameProgress { get; private set; }

        /// <summary>
        /// What each ask taken up in <see cref="DriveUpdate"/> came to, in order, since the last one. It is how a
        /// caller that asked outside the update finds out what became of its ask.
        /// </summary>
        public IReadOnlyList<ProvisionalCutAcceptance> LastTaken => _taken;

        private readonly List<ProvisionalCutAcceptance> _taken = new List<ProvisionalCutAcceptance>(4);

        /// <summary>
        /// Gives this driver the services it drives. None of them is created or owned here, and none is optional.
        /// </summary>
        /// <param name="supportEpsilon">
        /// The distance epsilon of the robust-support classification (DESIGN 7.6). **Supplied from outside**, like
        /// everything else here: no value of this path's own stands in for it.
        /// </param>
        /// <param name="anchorEpsilon">
        /// The epsilon the ledger distributes anchors with (DESIGN 7.1). Also supplied: whether the product uses one
        /// number for this and for <paramref name="supportEpsilon"/> is the product's to decide, not this driver's.
        /// </param>
        /// <param name="vertexLimit">The kernel's per-convex vertex limit L (DESIGN 7.2 gives 128). Supplied.</param>
        /// <param name="frameSource">
        /// What counts frames, when it is not the engine: a caller that gives its display a frame source of its own
        /// gives this the same one, so that "published and collected in one frame" is one number and not two.
        /// </param>
        /// <param name="dag">
        /// Where the display geometry of an accepted cut is carried (DESIGN 4.5.6): the cut is admitted **through**
        /// it, so one acceptance registers both duties at once and no second admission exists. It is the caller's --
        /// created, given its storage, its commit and its failure reporting, and disposed by whoever owns it -- and it
        /// is added to the bound frame here, so the work it holds is pumped by the frame's owner and outlives this
        /// component, exactly as an ending record is.
        /// <para>
        /// Optional: without one, this driver is the physics path alone and nothing of the display geometry is carried
        /// for its cuts. It is left out only where there is no display geometry to cut.
        /// </para>
        /// </param>
        public void Bind(
            LogicalCutLedger ledger,
            PhysicsOwnerRegistry registry,
            PhysicsCutCook cook,
            SharedWorkFrame frame,
            VpLogicalCutDisplay display,
            float supportEpsilon,
            float anchorEpsilon,
            int vertexLimit,
            Func<int> frameSource = null,
            CutDag dag = null,
            ICutTerminationLatch latch = null)
        {
            _preparedBindingEpoch++;
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _cook = cook ?? throw new ArgumentNullException(nameof(cook));
            _cook.MainRemaining = () => RemainingMain;   // the pump's hull checks share this frame's Main budget
            _frame = frame ?? throw new ArgumentNullException(nameof(frame));
            _display = display;
            if (!(supportEpsilon >= 0f) || !math.isfinite(supportEpsilon)
                || !(anchorEpsilon >= 0f) || !math.isfinite(anchorEpsilon)
                || vertexLimit <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(supportEpsilon), "the epsilons must be finite and not negative, and the vertex limit positive");
            }

            _supportEpsilon = supportEpsilon;
            _anchorEpsilon = anchorEpsilon;
            _vertexLimit = vertexLimit;
            _frameSource = frameSource;

            // What carries an ending record to the end. It is a participant of the bound frame, which is pumped by
            // whoever owns it and outlives this component: that is how a cut whose work was still out when this driver
            // went away is still finished.
            if (_recovery == null)
            {
                _recovery = new ProvisionalCutRecovery();
                frame.Add(_recovery);
            }

            // Where the Player's termination request is read, when there is one: this driver is both the acceptance
            // entrance and the entrance of the Final publication, which are the two things that stop.
            _latch = latch;

            // The same reason, for the geometry of an accepted cut: the DAG holds work that outlives this component,
            // and the frame is what pumps it. Adding it twice does nothing.
            _dag = dag;
            if (_dag != null)
            {
                frame.Add(_dag);
            }

            IsBound = true;
        }

        /// <summary>
        /// The records that have been asked to end and are waiting for their work to come back. They are carried by the
        /// bound frame, not by this driver, so they are finished even after this component has gone.
        /// </summary>
        public ProvisionalCutRecovery Recovery => _recovery;

        /// <summary>
        /// Where this driver's accepted cuts have their display geometry carried, when it was given one. It is not
        /// this driver's to dispose: it is the caller's, and it is pumped by the bound frame.
        /// </summary>
        public CutDag Geometry => _dag;

        /// <summary>
        /// Asks for one cut. With sufficient budget, classification, construction and publication happen in this
        /// call on the main thread, outside any physics step. Otherwise the accepted transaction retains its input
        /// and resumes through <see cref="Advance"/> without another request or classification.
        /// <para>
        /// It is the only entrance. Where the ask comes from -- a weapon, a test, a tool -- is not this driver's
        /// concern, and nothing of what follows it happens anywhere but here.
        /// </para>
        /// </summary>
        /// <param name="admission">
        /// What the ledger said, when it was asked. A source that is not live or already has an active cut reads the
        /// ledger's own <see cref="LogicalCutAdmission.SourceNotLive"/> or <see cref="LogicalCutAdmission.SourceActive"/>,
        /// found before its shape is read. On the paths that do not reach the ledger at all -- a request that does not
        /// hold together, and the empty-side no-op -- it reads <see cref="LogicalCutAdmission.NoOp"/>, which is what
        /// those come to: nothing changed.
        /// </param>
        public ProvisionalCutAcceptance RequestCut(
            in ProvisionalCutAsk ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission)
            => RequestCutCore(in ask, null, out transaction, out admission);

        private ProvisionalCutAcceptance RequestCutCore(
            in ProvisionalCutAsk ask, PreparedCutLease prepared,
            out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission)
        {
            transaction = null;

            // Nothing has changed until the ledger is asked, which is what this value means on every path that does
            // not reach it: the empty-side case is the ledger's own NoOp -- classified before admission as not
            // splitting both sides -- and a request that does not hold together never gets that far either.
            admission = LogicalCutAdmission.NoOp;

            // **Closed by the Player's termination request** (DESIGN 4): from the moment that latch is fixed, no cut
            // is accepted. Nothing has changed here and nothing is ended -- the ending of the Player is its own.
            if (_latch != null && _latch.TerminationRequested)
            {
                return ProvisionalCutAcceptance.NotAccepted;
            }

            if (!IsBound || !ask.source.IsSet
                || !math.all(math.isfinite(ask.plane)) || math.lengthsq(ask.plane.xyz) <= 0f
                || !math.all(math.isfinite(ask.renderAnchor)))
            {
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            // **The source comes first** (DESIGN 4.2): whether it is live, and whether a cut of it is already active,
            // before anything of its shape is read. An active source's own body has left the scene for its Provisional
            // pair, so asking about the shape first would answer "no such owner" where the answer is "passed over":
            // nothing is classified, issued or kept for either, and the ledger's own reason is what is handed back.
            // A prepared cut keeps its own contract -- a lease that is not this driver's, or is spent, is a request
            // that does not hold together, whatever its source -- so this is the ordinary path's.
            if (prepared == null)
            {
                if (!_ledger.IsCurrentTarget(ask.source))
                {
                    admission = LogicalCutAdmission.SourceNotLive;
                    return ProvisionalCutAcceptance.NotAccepted;
                }

                if (_ledger.TryGetActiveOperation(ask.source, out _))
                {
                    admission = LogicalCutAdmission.SourceActive;
                    return ProvisionalCutAcceptance.NotAccepted;
                }
            }

            if (!_registry.TryGet(ask.source, out PhysicsFragmentOwner owner) || owner.IsWithdrawn
                || owner.Shape == null || owner.Shape.IsFreed)
            {
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            // **A fused member is cut as its group** (BuildingFusion, 2026-09-29): the group is split by this plane into
            // two compound bodies, and only the members the plane crosses get a logical cut of their own. No record of
            // this driver is made for it; the fusion keeps its own.
            if (owner.IsFused)
            {
                if (Fusion == null || prepared != null)
                {
                    return ProvisionalCutAcceptance.InvalidRequest;
                }

                _frame.Dispatcher.BeginFrame(CurrentFrame);
                return Fusion.TryRequestGroupCut(owner, in ask, out admission);
            }

            // **Room for its constraints, before anything is asked or consumed** (DESIGN 7.1.1, 7.2.2): a cut that may
            // not fit the system constraints is not accepted, so a shortage never reaches the abort that retires the
            // source. Nothing has changed: the source stays as it is, and the room comes back as Provisional pairs end.
            int mayNeed = ConstraintsAtMost(owner);
            int? room = RemainingConstraintRoom();
            if (room.HasValue && room.Value < mayNeed)
            {
                ConstraintRoomRefusals++;
                return ProvisionalCutAcceptance.NotAccepted;
            }

            // The classification comes first, and before the acceptance: it is what says whether this plane cuts the
            // shape at all. The question is whether **the set** has support on both sides of the plane (DESIGN 7.6),
            // not how the convexes were allocated -- a convex with no support at all goes to the positive side by the
            // allocation rule, and that alone is not something to cut off. A no-op must not leave an accepted cut
            // behind, so this is asked before the ledger is.
            PhysicsCutClassification classification;
            if (prepared != null)
            {
                if (!prepared.TryConsume(this, owner, in ask, out classification))
                    return ProvisionalCutAcceptance.InvalidRequest;
            }
            else if (!PhysicsCutClassification.TryClassify(
                    owner.Shape, ask.plane, _supportEpsilon, owner.Mass, _vertexLimit, out classification))
            {
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            bool ownedByTransaction = false;
            try
            {
                if (!classification.SplitsBothSides)
                {
                    return ProvisionalCutAcceptance.EmptySide;
                }

                // **One acceptance.** Where there is display geometry to cut, the ledger is asked through the DAG, which
                // registers that cut's geometry work with the very operation the ledger issued; where there is none, the
                // ledger is asked directly. Either way it is asked once, here, after the support scan has decided that
                // there is a cut at all -- so a no-op and a refused admission leave no geometry work behind, because
                // nothing was admitted for one to belong to. The plane is the adopted one, in the source fragment's own
                // logical frame: the kernel's own is made from it where the geometry is read, and nothing here rebuilds it
                // from where the actor happens to stand.
                admission = _dag != null
                    ? _dag.TryAdmit(ask.source, ask.plane, true, out CutOperationId operation)
                    : _ledger.Admit(ask.source, ask.plane, true, out operation);
                if (admission != LogicalCutAdmission.Admitted)
                {
                    return ProvisionalCutAcceptance.NotAccepted;
                }

                // The record is made before anything can be waited for, and it keeps the one classification: a cut that
                // waits here is taken up again from where it left off, not classified a second time.
                // The parent mass of this cut, read from the source once, here: the temporary split and the final
                // masses are both against this one number (DESIGN 7.2), and after the publication there is no source body
                // left to read it from.
                var made = new ProvisionalCutTransaction(operation, ask.source, classification) { ConstraintReservation = mayNeed };
                made.Asked(in ask, owner.Mass);
                _transactions.Add(made);
                ownedByTransaction = true;
                transaction = made;
                made.HoldInput(owner.Shape);
                _frame.Dispatcher.BeginFrame(CurrentFrame);
                return TryEstablish(made, owner, mayOverrun: false);
            }
            finally
            {
                if (!ownedByTransaction) classification.Dispose();
            }
        }

        /// <summary>
        /// Resumes an existing admission. Anchors are cached by the ledger, input and classification are retained,
        /// and an inactive pair is built at most once. Budget exhaustion leaves these intact; publication and final
        /// submission happen once at a later opportunity without another admission.
        /// <para>
        /// <paramref name="mayOverrun"/>: whether this is a later update's opportunity, where a stage whose prediction
        /// is more than the whole frame budget may run past it once in the frame (DESIGN 7.1.1). An acceptance itself
        /// never does.
        /// </para>
        /// </summary>
        private ProvisionalCutAcceptance TryEstablish(ProvisionalCutTransaction transaction, PhysicsFragmentOwner owner, bool mayOverrun)
        {
            if (_latch != null && _latch.TerminationRequested) return ProvisionalCutAcceptance.Pending;
            AnchorPreparationOutcome prepared = _ledger.PrepareAnchorDistribution(
                transaction.Operation, _anchorEpsilon, out AnchorDistributionResult anchors);
            if (prepared != AnchorPreparationOutcome.Prepared)
            {
                // The two reasons the ledger gives are not the same thing, and **neither of them is a wait**: this
                // ledger settles a distribution when it is asked or refuses it, and asking again with the same epsilon
                // over the same anchors gets the same answer. So nothing is retried by itself.
                if (prepared == AnchorPreparationOutcome.OperationNotActive)
                {
                    // The ledger has already ended it -- published, aborted, reclaimed, or never issued. There is
                    // nothing left of it to end, so the record goes too.
                    Give(transaction);
                    return ProvisionalCutAcceptance.NotAccepted;
                }

                // Refused. **The cut is still the ledger's active operation**, holding a unit of its incomplete
                // budget, and this is not an abort to declare (DESIGN 7.1.1 keeps that for infeasibility). So the
                // resources go back and the record stays: the ending entrance is what closes what the ledger has.
                transaction.GiveUpUnpublished();
                return ProvisionalCutAcceptance.AnchorsRefused;
            }

            // Authority is rechecked before allocating, including when the source was retired while Pending.
            LogicalCutResultOutcome authority = _ledger.PreparePublication(transaction.Operation);
            if (authority != LogicalCutResultOutcome.Applied)
            {
                Give(transaction);
                return authority == LogicalCutResultOutcome.Stale ? ProvisionalCutAcceptance.Stale : ProvisionalCutAcceptance.NotAccepted;
            }
            if (owner == null || owner.IsWithdrawn || !ReferenceEquals(owner.Shape, transaction.InputShape))
            {
                Give(transaction);
                return ProvisionalCutAcceptance.NotAccepted;
            }
            // A build is predicted with its publication, as it runs on into it when it can; a built pair only with its
            // publication. Either sum may be more than the whole budget although each part fits it.
            bool building = transaction.Candidate == null;
            double expected = building
                ? _buildCosts.ExpectedSeconds + _publishCosts.ExpectedSeconds : _publishCosts.ExpectedSeconds;
            ProvisionalBudgetStage? overrun = null;
            double remaining = 0.0;
            if (!FitsMain(expected))
            {
                if (!mayOverrun || !MayOverrun(expected)) return ProvisionalCutAcceptance.Pending;

                // The one stage this world runs past the budget in this frame: the allowance is taken before it runs.
                overrun = building ? ProvisionalBudgetStage.Build : ProvisionalBudgetStage.Publication;
                remaining = RemainingMain;
                _overrunFrame = CurrentFrame;
            }

            ProvisionalCutAcceptance established = TryBuildAndPublish(transaction, owner, anchors, overrun, expected, remaining);
            if (established != ProvisionalCutAcceptance.Published)
            {
                return established;
            }

            // The final cut is submitted **after** the pair is in the scene, and its hold on the input taken with it.
            // Taking a reservation, running and baking are the frame's business from here, not the publication's.
            // An exception here is after the switch: the pair stays published and this record keeps it.
            SubmitFinalCut(transaction, owner);
            return ProvisionalCutAcceptance.Published;
        }

        /// <summary>
        /// Carries everything forward as far as this frame allows: the shared frame collects what the workers finished
        /// and submits what that made ready, any cut that ended has its products taken into its own record and is
        /// handed to its Final publication, and **what that publication made possible is carried on in this same
        /// call**.
        /// <para>
        /// **The two alternate.** A handoff publishes an operation's two children, which is the condition the geometry
        /// of that operation was waiting on to commit, which is in turn what a cut of one of those children was
        /// waiting on to run — so a collection makes more for the frame to do. And the other way round: a turn of the
        /// frame collects the numbers and the bake of some other cut, which is what makes *that* cut ready to be
        /// handed over — so a turn of the frame makes more for the collection to do. Either one left at the end would
        /// be a wait created by the order these calls happen to come in, so they alternate until neither has anything
        /// more, and **the collection always comes last**: what the final turn of the frame brought back is taken into
        /// its record here and not at the next call.
        /// </para>
        /// <para>
        /// **It ends by itself.** A further turn is taken only when the collection before it really ended something
        /// **and the frame still has budget to spend**. Those are two different things and neither stands for the
        /// other: a turn of the frame pumps its participants even with nothing left to spend, so "the frame moved
        /// something" is not "the frame may go on"; and a turn that moved nothing may still be followed by a
        /// collection that published a cut, which is progress of its own. So the budget is asked for directly
        /// (<see cref="SharedWorkDispatcher.RemainingBudget"/>), the collection speaks for itself, and the collection
        /// is always the last thing done either way. The budget is never refilled:
        /// <see cref="SharedWorkFrame.Update"/> with the same id continues that frame, and the turns are reported as
        /// the one frame's work they are. Nothing is waited for, completed by force or polled.
        /// </para>
        /// </summary>
        public void Advance(int frameId)
        {
            if (!IsBound)
            {
                return;
            }

            var progress = default(SharedWorkFrameProgress);
            _frame.Dispatcher.BeginFrame(frameId);
            // Resume existing admissions, not asks. Each is visited once; no busy polling when budget is exhausted.
            for (int i = 0; i < _transactions.Count;)
            {
                ProvisionalCutTransaction pending = _transactions[i];
                if (pending.Phase == ProvisionalCutPhase.Accepted && !pending.IsEnding)
                {
                    _registry.TryGet(pending.Source, out PhysicsFragmentOwner owner);
                    TryEstablish(pending, owner, mayOverrun: true);
                }
                if (i < _transactions.Count && ReferenceEquals(_transactions[i], pending)) i++;
            }
            while (true)
            {
                progress = progress.Plus(_frame.Update(frameId));

                // Always after a turn of the frame, including the last one: a result that came back in it is taken
                // into its record and handed over here, not at the next call.
                bool ended = CollectEndedCuts();
                if (!ended || _frame.Dispatcher.RemainingBudget <= 0)
                {
                    break;
                }
            }

            LastFrameProgress = progress;
        }

        /// <summary>
        /// Ends one cut, all of it: the published pair leaves the scene and is destroyed, **the ledger ends the cut**
        /// and retires the source by its own rule, a cut still running is abandoned, and everything the record holds
        /// goes back -- once each.
        /// <para>
        /// **A stale cut retires no fragment.** The ledger is what tells the two apart: it reclaims an operation whose
        /// source's authority moved and retires nothing for it, and only an abort it applied retires the source. This
        /// path asks and does not decide.
        /// </para>
        /// <para>
        /// A cut whose work is still with the dispatcher keeps its input and its classification alive until that work
        /// has been collected: the record ends when the request comes back, not when it was abandoned. Nothing is
        /// waited for and nothing is completed by force.
        /// </para>
        /// </summary>
        public bool EndCut(CutOperationId operation)
        {
            ProvisionalCutTransaction found = TransactionOf(operation);
            if (found == null)
            {
                return false;
            }

            Abort(found);
            return true;
        }

        /// <summary>The record of one cut, while this driver still holds something for it.</summary>
        public ProvisionalCutTransaction TransactionOf(CutOperationId operation)
        {
            for (int i = 0; i < _transactions.Count; i++)
            {
                if (_transactions[i].Operation.Equals(operation))
                {
                    return _transactions[i];
                }
            }

            return null;
        }

        /// <summary>
        /// Notes one ask, to be taken up in the **next update of this driver** — which is where the accepting, the
        /// building and the publishing happen. It is for a caller that runs in some other phase: a weapon in its own
        /// update, an editor tool, a test. A caller already in the update phase may call
        /// <see cref="RequestCut"/> itself and get its answer at once; both are the same path through this driver.
        /// <para>
        /// The asks noted before an update are admitted in that update, in order, and this list is emptied.
        /// Accepted transactions can remain Pending across frames when construction or publication lacks budget.
        /// </para>
        /// </summary>
        public void Ask(in ProvisionalCutAsk ask)
        {
            _asked.Add(ask);
        }

        /// <summary>
        /// What this driver does in <c>Update</c>, and what Unity calls it for: take up every ask made since the last
        /// one — accepting, building and publishing each — and then carry the frame as far as its budget allows.
        /// </summary>
        public void DriveUpdate()
        {
#if VP_DIAGNOSTIC_SCENE_AB
            using var measured = SceneAbCounters.Measure(0);
#endif
            _taken.Clear();

            // Requests held before acceptance come first: they were made before this update's asks.
            ResumeHeldCharacterCuts();
            for (int i = 0; i < _asked.Count; i++)
            {
                ProvisionalCutAsk ask = _asked[i];
                _taken.Add(RequestCut(in ask, out ProvisionalCutTransaction _, out LogicalCutAdmission _));
            }

            _asked.Clear();

            // The frame this driver is in, from the one source it was given: the late update's turn below must be
            // the **same** frame, or the dispatcher would take it for a new one and refill the budget.
            Advance(CurrentFrame);
        }

        /// <summary>
        /// What this driver does in <c>LateUpdate</c>: carry the frame once more. The display does not collect here any
        /// more: it collects in <see cref="CollectSnapshot"/>, which <see cref="CutPhysicsStep"/> calls after every late
        /// update and after its decision to simulate, so the snapshot is taken after this frame's publications and after
        /// the step they are simulated in (DESIGN 7.1.1).
        /// <para>
        /// **The second turn is the same frame's, not a new one.** It is given the same frame id, so the budget is
        /// not refilled and the frame spends what it was given: <see cref="SharedWorkFrame.Update"/> with an id it
        /// has already begun continues that frame. It exists because a work submitted in the update can finish after
        /// the update's own turns have ended — the turns stop as soon as nothing moves, and in the runs recorded the
        /// submitted work had not come back by then — and without this, what is **already finished** would wait for
        /// the next frame's update merely because of when the calls happen to come. Nothing is waited for here,
        /// nothing is completed by force and nothing is polled:
        /// this is one more ordinary occasion, later in the same frame, and it stops by the same rules.
        /// </para>
        /// </summary>
        public void DriveLateUpdate()
        {
#if VP_DIAGNOSTIC_SCENE_AB
            using var measured = SceneAbCounters.Measure(1);
#endif
            Advance(CurrentFrame);
        }

        /// <summary>
        /// Lets the display collect this frame's snapshot -- after this frame's publications and after the frame's
        /// decision to simulate, which is where <see cref="CutPhysicsStep"/> calls it. The snapshot the display settles
        /// is settled by its own rules (DESIGN 5.6); this only says when. Whether it collected is the display's answer.
        /// </summary>
        public bool CollectSnapshot()
        {
            if (_display == null)
            {
                return false;
            }

            // The upload is made for the stereo mode this frame renders in: under Single Pass Instanced one issue carries
            // two instances per body, one per eye, and the display is told so here, the one place it collects. A camera
            // that draws a single view in the same frame drops the second copy itself (the shader's own rule).
            _display.SinglePassInstanced = RendersSinglePassInstanced(
                UnityEngine.XR.XRSettings.isDeviceActive, UnityEngine.XR.XRSettings.stereoRenderingMode);
            return _display.TryBeginFrame();
        }

        /// <summary>
        /// Whether a frame renders Single Pass Instanced stereo: an XR device is active and its stereo mode is that one.
        /// Every other case -- no device, multi pass, any other mode -- draws one instance per body.
        /// </summary>
        internal static bool RendersSinglePassInstanced(bool xrDeviceActive, UnityEngine.XR.XRSettings.StereoRenderingMode mode)
        {
            return xrDeviceActive && mode == UnityEngine.XR.XRSettings.StereoRenderingMode.SinglePassInstanced;
        }

        private void Update()
        {
            using (s_update.Auto())
            {
                DriveUpdate();
            }
        }

        private void LateUpdate()
        {
            using (s_lateUpdate.Auto())
            {
                DriveLateUpdate();
            }
        }

        /// <summary>
        /// Going away is asking every cut to end, not forcing it: for each one the published pair leaves the scene, the
        /// ledger ends the cut and retires the source by its own rule, any running work is abandoned, and the record
        /// goes back **as soon as its work has come back**.
        /// <para>
        /// A record whose work is still out moves to <see cref="Recovery"/>, holding its classification and its input
        /// because the kernel is still pointing at them. **That recovery is a participant of the bound frame**, which
        /// outlives this component and is pumped by whoever owns it, so those records are finished there. Nothing here
        /// waits, sleeps or completes anything by force.
        /// </para>
        /// </summary>
        private void OnDestroy()
        {
            if (_latch != null && _latch.TerminationRequested)
            {
                // The Player is ending (DESIGN 4). The ordinary ending of every cut -- the aborts, the retirements,
                // the collection that follows them -- is exactly what that contract does not do, and being destroyed
                // is not a reason to start it. An explicit ending is still an explicit ending: EndEveryCut called by
                // a caller does what it always did.
                return;
            }

            EndEveryCut();
        }

        /// <summary>
        /// What this driver does when it goes away, and what <c>OnDestroy</c> calls: see above.
        /// </summary>
        public void EndEveryCut()
        {
            _asked.Clear();
            EndHeldCharacterCuts();
            if (!IsBound)
            {
                return;
            }

            // The same ending as EndCut, for each: the pair out of the scene, the ledger's own abort, the source
            // retired unless the ledger finds it stale, and the record carried to the end by the frame.
            for (int i = _transactions.Count - 1; i >= 0; i--)
            {
                Abort(_transactions[i]);
            }
        }

        /// <summary>
        /// The building World D6 settings every building child of this driver's cuts is built with, and how many
        /// system constraints -- sibling and building ones together -- the scene may hold (DESIGN 7.2.2). A cut whose
        /// pair would need more than is left cannot be built, and goes the way any build that cannot be made goes.
        /// </summary>
        public void ConfigureConstraints(BuildingWorldD6Settings buildingWorld, int constraintCapacity)
        {
            if (!buildingWorld.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(buildingWorld), "the building World D6 settings are not usable");
            }

            if (constraintCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(constraintCapacity));
            }

            _buildingWorld = buildingWorld;
            _constraintCapacity = constraintCapacity;
        }

        private ProvisionalCutAcceptance TryBuildAndPublish(
            ProvisionalCutTransaction transaction,
            PhysicsFragmentOwner owner,
            AnchorDistributionResult anchors,
            ProvisionalBudgetStage? overrun,
            double overrunExpected,
            double overrunRemaining)
        {
            ProvisionalCutAsk ask = transaction.Ask;
            ProvisionalOwnerCandidate candidate = transaction.Candidate;
            if (candidate == null)
            {
                var build = new ProvisionalOwnerBuildInput
                {
                    sourceShape = owner.Shape,
                    sides = transaction.Classification.Sides,
                    planeLocal = ask.plane,
                    placement = owner.ReadPlacement(),
                    sourceMotion = owner.ReadMotion(ask.renderAnchor),
                    anchors = anchors,
                    parentMass = transaction.ParentMass,
                    sourceInertia = owner.Body.inertiaTensor,
                    sourceInertiaRotation = owner.Body.inertiaTensorRotation,
                    cooking = _cook.Cooking,
                    colliderTemplate = ColliderTemplate,

                    // The children's lineage, planned now, before anything is built (DESIGN 7.2.2): the pair and the
                    // children are published with this one value.
                    childLineage = owner.Building.ChildOfSplit(),
                    buildingWorld = _buildingWorld,
                    constraintRoom = RemainingConstraintRoom(transaction),
                    name = owner.Root != null ? owner.Root.name : "Provisional",
                };

                PhysicsOwnerBuildOutcome built;
                long buildStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    bool builtOk;
                    using (s_build.Auto())
                    {
                        builtOk = ProvisionalOwnerBuilder.TryBuild(in build, out candidate, out built);
                    }

                    if (!builtOk)
                    {
                        // Nothing was built, so there is nothing of it to give back. The cut cannot be established
                        // (DESIGN 7.1.1): it is aborted, and the source retired unless the ledger finds it stale.
                        NoteAbort(new CutAbort
                        {
                            operation = transaction.Operation, frame = CurrentFrame, stage = "build", build = built,
                            constraintRoom = build.constraintRoom ?? -1,
                            constraintsNeeded = 1 + BuildingWorldD6.Needed(in _buildingWorld, in build.childLineage, build.anchors.IsPositiveFixed, build.anchors.IsNegativeFixed),
                        });
                        Abort(transaction);
                        return ProvisionalCutAcceptance.Aborted;
                    }
                }
                catch (Exception)
                {
                    // Before the switch, so nothing of the pair is in the scene and the source is untouched. What this
                    // record made goes back and the error is passed on -- it is not read as a physics failure, and an
                    // exception is not an infeasibility to declare, so the cut is not aborted for one. **The record
                    // stays**: the ledger still has the acceptance and its budget unit, and this record is what the ending
                    // entrance closes them through.
                    transaction.GiveUpUnpublished();
                    throw;
                }
                finally
                {
                    double buildSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - buildStarted) / (double)System.Diagnostics.Stopwatch.Frequency;
                    _buildCosts.Add(buildSeconds);
                    if (overrun == ProvisionalBudgetStage.Build)
                    {
                        RecordOverrun(transaction, ProvisionalBudgetStage.Build, overrunExpected, overrunRemaining, buildSeconds);
                    }
                }

                transaction.Took(candidate);
            }
            // A build can consume the remainder. Keep both inactive sides, never rebuild or publish half a pair. After a
            // build run past the budget, the publication goes back to the ordinary decision; only a publication that is
            // itself this frame's run past the budget skips it.
            if (overrun != ProvisionalBudgetStage.Publication && !FitsMain(_publishCosts.ExpectedSeconds)) return ProvisionalCutAcceptance.Pending;
            var publication = new ProvisionalPhysicsPublicationInput
            {
                ledger = _ledger,
                registry = _registry,
                operation = transaction.Operation,
                source = transaction.Source,
                candidate = candidate,
                builtFrom = owner.Shape,
                renderAnchor = ask.renderAnchor,
                positiveSeparationImpulse = ask.positiveSeparationImpulse,
                negativeSeparationImpulse = ask.negativeSeparationImpulse,
                separationStrength = SeparationStrength,
            };

            PhysicsPublicationOutcome published;
            ProvisionalOwnerPair pair;
            LogicalCutResultOutcome ledgerOutcome;
            long publishStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                using (s_publish.Auto())
                {
                    published = ProvisionalPhysicsPublication.TryPublish(in publication, out pair, out ledgerOutcome);
                }
            }
            catch (Exception)
            {
                // **Which side of the switch it threw on is what the correspondence says**: a pair the cut has is a
                // pair that was published, whatever happened afterwards. The publication takes its own pair back out
                // before letting an exception through from the other side, so there is nothing ambiguous to read here.
                if (_registry.TryGetProvisional(transaction.Operation, out ProvisionalOwnerPair standing)
                    && !standing.IsEnded)
                {
                    // After the switch. The pair is in the scene and this record is what holds it and what can end it
                    // later, so **neither is given back**: the error is passed on as it is, and nothing is read as an
                    // unpublished failure.
                    transaction.Published(standing, CurrentFrame);
                    throw;
                }

                // Before the switch: nothing of the pair is in the scene, and the acceptance is still the ledger's.
                // The resources go back; the record stays to be ended.
                transaction.GiveUpUnpublished();
                throw;
            }
            finally
            {
                double publishSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - publishStarted) / (double)System.Diagnostics.Stopwatch.Frequency;
                _publishCosts.Add(publishSeconds);
                if (overrun == ProvisionalBudgetStage.Publication)
                {
                    RecordOverrun(transaction, ProvisionalBudgetStage.Publication, overrunExpected, overrunRemaining, publishSeconds);
                }
            }

            if (published == PhysicsPublicationOutcome.Published)
            {
                transaction.Published(pair, CurrentFrame);
                return ProvisionalCutAcceptance.Published;
            }

            // Not published, and the three reasons are not the same thing.
            if (published == PhysicsPublicationOutcome.PhysicsNotEstablished)
            {
                // The publication already aborted the cut and retired the source, by the ordinary rule of
                // DESIGN 7.1.1. What is left here is this record's own.
                Give(transaction);
                return ProvisionalCutAcceptance.Aborted;
            }

            // A refusal: nothing of the physics changed. A stale cut has been reclaimed by the ledger and retires no
            // fragment; anything else the ledger refused leaves the cut as it was. Either way this record is done, and
            // nothing of it is aborted for it.
            Give(transaction);
            return ledgerOutcome == LogicalCutResultOutcome.Stale
                ? ProvisionalCutAcceptance.Stale
                : ProvisionalCutAcceptance.NotAccepted;
        }

        private void SubmitFinalCut(ProvisionalCutTransaction transaction, PhysicsFragmentOwner owner)
        {
            // After the switch. An exception here is not an unpublished failure and is not converted into one: the
            // pair is in the scene, this record holds it, and the error is passed on as it is.
            PhysicsCutRequest request = _cook.Submit(transaction.Classification.Input, owner.Shape.LocalToOwner);
            transaction.Submitted(request, owner.Shape);
        }

        /// <summary>
        /// The ordinary continuation of a cut that cannot be established (DESIGN 7.1.1): the ledger aborts it and the
        /// source is retired — **unless the ledger finds it stale**, which reclaims the operation and retires nothing.
        /// The published pair, if there is one, leaves the scene with it, and this record's own is given back.
        /// </summary>
        private void Abort(ProvisionalCutTransaction transaction)
        {
            _registry.EndProvisional(transaction.Operation);
            FinalPhysicsPublication.AbortAfterPhysicsFailure(_ledger, _registry, transaction.Operation);
            Give(transaction);
        }

        /// <summary>
        /// Hands one cut's finished products to the Final publication of the same cut (DESIGN 7.2). It is tried in the
        /// collection that took the products in, and nothing is waited for.
        /// <para>
        /// **The answers are kept apart.** Published: the two actors are the logical children now and this record has
        /// let everything go. Refused by the ledger -- a moved authority, a stale result -- **changes nothing of the
        /// physics**, and a stale one retires no fragment, because the fragment that is there now is not the one this
        /// cut was of; the products are given up with the rest of this record. A final set that cannot be established is
        /// the ordinary physics failure of DESIGN 7.1.1: the pair out of the scene, the ledger's abort, and the source
        /// retired unless the ledger finds it stale.
        /// </para>
        /// </summary>
        private void TryHandOff(ProvisionalCutTransaction transaction)
        {
            if (transaction.Pair == null || transaction.Pair.IsEnded || transaction.InputShape == null)
            {
                return;
            }

            // **Nothing unpublished is published after the termination request** (DESIGN 4). The latch may have been
            // fixed earlier in this very update -- a geometry failure is reported while the frame is being carried,
            // which is before this collection -- so it is read here and not only where a cut is accepted. The record
            // keeps its products: ending it is the caller's, and the Player's ending guarantees no collection.
            if (_latch != null && _latch.TerminationRequested)
            {
                return;
            }

            var handoff = new FinalHandoffInput
            {
                ledger = _ledger,
                registry = _registry,
                operation = transaction.Operation,
                products = transaction.Products,
                cutFrom = transaction.InputShape,

                // The mass this cut was accepted with, not one read back from the two temporary bodies.
                parentMass = transaction.ParentMass,
            };

            PhysicsPublicationOutcome handed;
            try
            {
                handed = FinalHandoffPublication.TryHandOff(
                    in handoff, out LogicalFragmentId _, out LogicalFragmentId _, out LogicalCutResultOutcome _);
            }
            catch (Exception)
            {
                // **Which side of the handover it threw on is what the correspondence says**: a cut that still has its
                // pair never got as far as giving the actors away, and one that has none did. The publication moves
                // the products' ownership at that same point, so the two cannot disagree; and a pair that is still
                // there holds whatever the switch had begun putting the actors on, which the ending below gives back.
                if (!_registry.TryGetProvisional(transaction.Operation, out ProvisionalOwnerPair standing)
                    || standing.IsEnded)
                {
                    // The actors are the children's now and so are the products. This record is brought to where that
                    // leaves it **before the error goes on**: holding them any longer would let the ending of this
                    // record dispose meshes the published children's colliders are using.
                    transaction.HandedOffTo(CurrentFrame);
                    _transactions.Remove(transaction);
                }

                throw;
            }

            switch (handed)
            {
                case PhysicsPublicationOutcome.Published:
                    transaction.HandedOffTo(CurrentFrame);
                    _transactions.Remove(transaction);
                    return;

                case PhysicsPublicationOutcome.LedgerRefused:
                    // Nothing of the physics changed, and nothing is aborted for a refusal: the pair this driver
                    // published is ended and the products are given up with this record. The current source, which is
                    // somebody else's now, is left alone.
                    _registry.EndProvisional(transaction.Operation);
                    Give(transaction);
                    return;

                default:
                    // A final set that cannot be established, and a call that does not hold together, both end the
                    // ordinary way rather than being tried again with the same input at every collection.
                    NoteAbort(new CutAbort
                    {
                        operation = transaction.Operation, frame = CurrentFrame, stage = "final", publication = handed.ToString(),
                        constraintRoom = -1, constraintsNeeded = -1,
                    });
                    Abort(transaction);
                    return;
            }
        }

        /// <summary>
        /// This record is done with: what it holds goes back as soon as it may, and this driver stops holding it. A
        /// submitted work that has not come back keeps the record alive until then — asking is not collecting — and it
        /// is the recovery, a participant of the bound frame, that carries it to the end.
        /// <para>
        /// It is for a cut the ledger has no more use for. A cut the ledger **still holds** as its source's active
        /// operation is not ended this way: <see cref="ProvisionalCutTransaction.GiveUpUnpublished"/> gives its
        /// resources back and keeps the record, so that the ending entrance can close the acceptance too.
        /// </para>
        /// </summary>
        private void Give(ProvisionalCutTransaction transaction)
        {
            transaction.Ending();
            if (transaction.Cut != null)
            {
                _cook.Abandon(transaction.Cut);
            }

            _transactions.Remove(transaction);
            if (!transaction.TryFinishIfCollected())
            {
                // Its work has not come back, so it is not done with: the frame carries it the rest of the way.
                _recovery.Keep(transaction);
            }
        }

        /// <summary>
        /// Takes the finished cuts into their records, and hands each one that produced something to its Final
        /// publication. True when something really ended here, which is what tells the caller that the frame has more
        /// to do: a publication is what a geometry commit was waiting for, and that commit is what a child's cut was
        /// waiting for.
        /// </summary>
        private bool CollectEndedCuts()
        {
            bool moved = false;
            for (int i = _transactions.Count - 1; i >= 0; i--)
            {
                ProvisionalCutTransaction at = _transactions[i];
                PhysicsCutRequest request = at.Cut;
                if (request == null || !request.IsOver)
                {
                    continue;
                }

                moved = true;

                // The products are taken into the record. A handoff reads their borrowed parts, and the hold on the
                // input stays until it has.
                at.CutEnded(request.Outcome, request.Products);
                if (at.Products != null)
                {
                    // And it is tried **now**, in the update the products came back in: putting it off to the next one
                    // would be a frame spent on the order the calls happen to come in.
                    TryHandOff(at);
                    continue;
                }

                // A record that was asked to end is not here: it is with the recovery, which the frame pumps. What is
                // left here is a cut nobody has ended.
                // The cut produced nothing, so there is no final set to hand over and never will be: the cut cannot be
                // established. It ends the ordinary way -- the pair out of the scene, the ledger's abort, the source
                // retired unless the ledger finds it stale -- and not merely as a record this driver forgets. It is
                // counted here, once: a record takes its ended cut in only once.
                CountFailure(at, request);
                Abort(at);
            }

            return moved;
        }
    }
}
