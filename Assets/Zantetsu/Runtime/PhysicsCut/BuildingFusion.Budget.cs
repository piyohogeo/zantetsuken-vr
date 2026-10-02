using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The fusion's Main time (2026-09-30, TL): one budget for every path, the preparation of a cut off Main, and the
    /// timing that goes with them.
    /// <para>
    /// **The budget.** <see cref="Step"/> starts a clock; every unit of work that can wait -- a Final's collection, a
    /// member's snapshot for a preparation, a prepared cut's publication, a member's move in a union or a merge, a
    /// piece's fusion, a held hit's routing -- asks <see cref="MayStart"/> first and is left for the next frame when the
    /// settings' Main budget, less what the end of the Step's mass applies are expected to cost, is spent
    /// (<see cref="DeferredForBudget"/>). A unit that began runs to its end: what it ran past the budget is recorded as an
    /// overrun (<see cref="OverrunSeconds"/>), never cut short, so that the physics is never handed a half-done state.
    /// </para>
    /// <para>
    /// **The preparation.** A hit on a group is answered Pending and only what the cut needs is written down: the
    /// adopted plane, carried once into the group's own frame, so that a snapshot spread over frames of a moving group
    /// still takes one plane. The Step snapshots the group's members under the budget (their shapes held, the plane
    /// carried from the group's frame into each member's, a classification block allocated on Main), offers the scan
    /// and the box rule to the shared dispatcher's background pool, and a later Step publishes the cut -- after judging
    /// every member again on Main, all or none -- if the group is still the one snapshotted (its generation: every
    /// member added or removed moves it). A stale preparation is made again once; then the hit is refused. A scan the
    /// dispatcher has no room for waits to be offered again; nothing is scanned on Main.
    /// </para>
    /// <para>
    /// **Ownership of the inputs.** The snapshot's holds and blocks are the fusion's until the work is offered, the
    /// dispatcher's from the offer to the collection (the worker may be reading them), and the fusion's again once the
    /// work has been collected -- finished or cancelled. They are released by whoever owns them when the preparation
    /// ends: at once when the fusion does, at the collection when the dispatcher does (a work the fusion has let go
    /// releases them itself when it is collected, and the dispatcher collects everything at its shutdown before the
    /// fusion is disposed).
    /// </para>
    /// <para>
    /// **A hit while a group is being prepared or cut** is not refused out of hand. A hit carries the identity of the
    /// plane its Slash adopted (<see cref="ProvisionalCutAsk.adoptedPlaneId"/>, one per sweep of the hit detector, with
    /// the plane's value): the same Slash and the same adopted plane is the same request, already included -- attached
    /// to the preparation, or answered by the cut that made the group busy -- and any other plane, however close, is
    /// held and routed again when the group is free. Every hit is one record (<see cref="Hits"/>) that carries its
    /// events, its preparation and its one final outcome; an attached hit's outcome is its preparation's.
    /// </para>
    /// <para>
    /// **The masses.** A group's mass properties are gathered on Main and summed in a Burst job scheduled at once; the
    /// job is completed at the end of the same Step, after the rest of the turn's work, and applied to the body then --
    /// before this frame's physics step. Main's wait for the jobs is not gone: it moved to the end of the Step, and it
    /// is counted (<see cref="MassWaitSeconds"/>, including the wait of a job replaced by a second schedule in one Step).
    /// </para>
    /// </summary>
    public sealed partial class BuildingFusion
    {
        // ---- the timing ----

        private readonly Stopwatch _stepWatch = new Stopwatch();
        private long _partStart;
        private double _partMassAtStart;

        /// <summary>The whole of every <see cref="Step"/> (the Finals' collection, the preparations, the aggregations, the merges, the fusions, the masses), Main.</summary>
        public double StepSeconds { get; private set; }
        public double MaxStepSeconds { get; private set; }

        /// <summary>The hits answered outside a Step (the driver's requests: the gate and the routing; no snapshot), Main. Disjoint from <see cref="StepSeconds"/>.</summary>
        public double RequestSeconds { get; private set; }
        public double MaxRequestSeconds { get; private set; }

        /// <summary>The breakdown, disjoint parts of the two above: the snapshot, judgement and admissions of a prepared cut on Main.</summary>
        public double ClassifySeconds { get; private set; }
        public double MaxClassifySeconds { get; private set; }

        /// <summary>The publication of the sides (the roots, the moves, the shadows, the rest), without the masses.</summary>
        public double PublishSeconds { get; private set; }
        public double MaxPublishSeconds { get; private set; }

        /// <summary>The scans and box divisions on the worker (not Main).</summary>
        public double WorkerSeconds { get; private set; }
        public double MaxWorkerSeconds { get; private set; }

        /// <summary>Main's wait for the mass jobs: at the end of a Step, and for a job replaced by a second schedule of the same group within a Step.</summary>
        public double MassWaitSeconds { get; private set; }
        public double MaxMassWaitSeconds { get; private set; }
        public double MassRescheduleWaitSeconds { get; private set; }
        public int MassReschedules { get; private set; }

        // ---- the publication's and the masses' breakdown (2026-09-30, the coexistence run's 130 ms publication) ----

        /// <summary>The publication's parts, Main, summed over every group cut (disjoint; their sum is about <see cref="PublishSeconds"/>): the sides made and every member moved under its side; the crossed members' copies made; the collisions ignored between the copies and the crossed members' own colliders; the rest told and the old group dropped; the crossed members' cooks submitted.</summary>
        public double PublishMoveSeconds { get; private set; }
        public double PublishShadowSeconds { get; private set; }
        public double PublishIgnoreSeconds { get; private set; }
        public double PublishRestSeconds { get; private set; }
        public double PublishSubmitSeconds { get; private set; }

        /// <summary>The collision pairs ignored between the crossed members' copies and the crossed members' own colliders (every copy with every crossed member's collider): in all, the most in one cut, and the longest one cut's ignoring took.</summary>
        public long IgnoredPairs { get; private set; }
        public int MaxIgnoredPairs { get; private set; }
        public double MaxIgnoreSeconds { get; private set; }

        /// <summary>The heaviest publication's own parts, as one line (members, crossed, pairs and each part's milliseconds).</summary>
        public string MaxPublishBreakdown { get; private set; } = "";

        /// <summary>The masses' parts, Main, within <see cref="MassSeconds"/>: the samples gathered when a sum is scheduled (every member's world centre and axes read), and the applies at the Step's end (their waits included, as <see cref="MassWaitSeconds"/> counts); how many sums were scheduled and how many samples they held; and how many of those schedules a member's retirement asked for.</summary>
        public double MassSampleSeconds { get; private set; }
        public double MaxMassSampleSeconds { get; private set; }
        public double MassApplySeconds { get; private set; }
        public double MaxMassApplySeconds { get; private set; }
        public int MassSchedules { get; private set; }
        public long MassSamplesGathered { get; private set; }
        public int MassSchedulesByRetirement { get; private set; }

        /// <summary>
        /// The applies at the Step's end by part (TL, 2026-09-30: 54 ms in one Step with only 54 ms of job waits over the
        /// whole run -- the wait being short does not by itself put the rest on the Body's setters). Parts, Main seconds
        /// summed over every apply: the job's wait, the centre and tensor and their diagonalization, the Body's automatic
        /// flags, its mass, its centre, its inertia, the motion that follows (the centre read back and the velocities),
        /// and the arrays freed. With them: the applies (Step ends with at least one), the groups applied, their members'
        /// samples and their members' colliders, and the heaviest Step end's own line.
        /// </summary>
        public static readonly string[] MassApplyPartNames = { "wait", "tensor", "flags", "mass", "centre", "inertia", "motion", "free" };
        public double[] MassApplyPartSeconds { get; } = new double[8];
        public int MassApplyEnds { get; private set; }
        public int MassGroupsApplied { get; private set; }
        public long MassMembersApplied { get; private set; }
        public long MassHullsApplied { get; private set; }
        public string MaxMassApplyBreakdown { get; private set; } = "";

        private void BeginPart()
        {
            _partStart = Stopwatch.GetTimestamp();
            _partMassAtStart = MassSeconds;
        }

        /// <summary>The part's own seconds: what elapsed, less the mass work that ran inside it (counted in <see cref="MassSeconds"/>).</summary>
        private double EndPart()
        {
            return (Stopwatch.GetTimestamp() - _partStart) / (double)Stopwatch.Frequency - (MassSeconds - _partMassAtStart);
        }

        // ---- the budget ----

        private long _budgetStart;
        private bool _inStep;
        private double _massApplyEstimate = 0.0002;   // seconds one pending group's apply is expected to take at the Step's end (a running mean of the measured)

        /// <summary>Units of work not begun because the Step's Main budget was spent (each waits for the next frame).</summary>
        public int DeferredForBudget { get; private set; }

        /// <summary>Steps that ran past the budget, and by how much in all: a unit that began runs to its end.</summary>
        public int OverrunFrames { get; private set; }
        public double OverrunSeconds { get; private set; }
        public double MaxOverrunSeconds { get; private set; }

        /// <summary>What the pending mass applies at the end of this Step are expected to cost, held back from the budget before a unit begins.</summary>
        public double MassReserveSeconds => _pendingMass.Count * _massApplyEstimate;

        /// <summary>The running mean of one group's mass apply at the end of a Step (seconds), which the reserve is made of.</summary>
        public double MassApplyEstimateSeconds => _massApplyEstimate;

        private double BudgetRemaining => _settings.mainBudgetSeconds - (Stopwatch.GetTimestamp() - _budgetStart) / (double)Stopwatch.Frequency;

        /// <summary>Whether a unit of work may begin now: the Step's budget, less the reserve for the masses waiting at its end, is not spent. Outside a Step, always.</summary>
        private bool MayStart()
        {
            if (!_inStep || BudgetRemaining - MassReserveSeconds > 0.0)
            {
                return true;
            }

            DeferredForBudget++;
            return false;
        }

        // ---- the hits: one record each, with its events and its one outcome ----

        /// <summary>
        /// One hit on a fused member, from the request to its final outcome: what happened to it (its events, in order),
        /// the preparation it is prepared by or attached to, and its one outcome -- set once, when the preparation it
        /// belongs to ends (an attached hit takes that preparation's outcome, so an attachment is never a success by
        /// itself), when the cut in progress answers it, or when it is refused or dropped.
        /// </summary>
        public sealed class HitRecord
        {
            public int id;
            public long slashId;
            public int fragment;
            public long planeId;   // the adopted plane's identity the hit carried (0: none carried; never the same as another)
            public double physicsSeconds;
            public string state;   // "prepared", "attached", "included", "held", "invalid"
            public int preparation;   // the preparation it is prepared by or attached to, or 0
            public string outcome;   // null while pending; "Published", "Included: ...", "NotAccepted: ...", "Abandoned: ...", "Dropped: ...", "Invalid"
            public bool published;
            public readonly List<string> events = new List<string>(4);

            public bool IsPending => outcome == null;
            public string Events => string.Join(" | ", events);
        }

        private readonly List<HitRecord> _hits = new List<HitRecord>();

        /// <summary>Every hit on a fused member, one record each, in the order of the requests.</summary>
        public IReadOnlyList<HitRecord> Hits => _hits;

        /// <summary>The hits by their one outcome: published (a preparation of its own or one it was attached to published, or the cut in progress answered it), refused (not accepted, abandoned, dropped, invalid), and those still pending.</summary>
        public int HitsPublished { get; private set; }
        public int HitsRefused { get; private set; }
        public int HitsPending { get { int n = 0; foreach (HitRecord h in _hits) if (h.IsPending) n++; return n; } }

        /// <summary>The events, counted apart from the hits (a hit has several): preparations begun for a hit (at the hit or from a hold), attachments, inclusions, holds (first and again).</summary>
        public int HitsPrepared { get; private set; }
        public int HitsAttached { get; private set; }
        public int HitsIncluded { get; private set; }
        public int HitsHeld { get; private set; }
        public int HitsHeldAgain { get; private set; }

        private HitRecord NewHit(in ProvisionalCutAsk ask)
        {
            var hit = new HitRecord { id = _hits.Count + 1, slashId = ask.slashId, fragment = ask.source.value, planeId = ask.adoptedPlaneId, physicsSeconds = _physicsSeconds(), state = "hit" };
            _hits.Add(hit);
            return hit;
        }

        private void Note(HitRecord hit, string state, int preparation, string what)
        {
            if (hit == null) return;
            hit.state = state;
            if (preparation > 0) hit.preparation = preparation;
            if (hit.events.Count < 16) hit.events.Add("t " + _physicsSeconds().ToString("F3") + " " + what);
        }

        private void SetOutcome(HitRecord hit, string outcome, bool published)
        {
            if (hit == null || hit.outcome != null) return;
            hit.outcome = outcome;
            hit.published = published;
            if (published) HitsPublished++; else HitsRefused++;
        }

        /// <summary>The same request: the same Slash and the same adopted plane, by the identity the hit detector gave the plane (a hit that carries none matches nothing).</summary>
        internal static bool SameAdoptedPlane(long slashA, long planeA, long slashB, long planeB) => planeA != 0 && slashA == slashB && planeA == planeB;

        /// <summary>The adopted plane in the world: the one the hit carried, or -- for an ask that carries none -- the ask's plane carried out of the member's shape frame.</summary>
        private static float4 AdoptedPlaneWorld(FusedGroup.Member hit, in ProvisionalCutAsk ask)
        {
            if (ask.adoptedPlaneId != 0 && math.all(math.isfinite(ask.adoptedPlaneWorld)) && math.lengthsq(ask.adoptedPlaneWorld.xyz) > 0f)
            {
                return ask.adoptedPlaneWorld;
            }

            float4x4 hitShapeToWorld = math.mul((float4x4)hit.owner.Root.transform.localToWorldMatrix, hit.owner.Shape.LocalToOwner);
            return TransformPlane(hitShapeToWorld, ask.plane);
        }

        /// <summary>
        /// A hit on a group, after the aggregation's gate: prepared as a cut of its own when the group is free; attached
        /// to the group's preparation when it is the same request (the preparation classifies every member, so the hit's
        /// member is in it); answered as already cut when it is the same request as the cut that made the group busy;
        /// otherwise held and routed again when the group is free. Pending for all but the included, which is Published.
        /// </summary>
        private ProvisionalCutAcceptance RouteHit(FusedGroup group, FusedGroup.Member hit, in ProvisionalCutAsk ask, HitRecord record, out LogicalCutAdmission admission)
        {
            admission = LogicalCutAdmission.NoOp;
            float4 planeWorld = AdoptedPlaneWorld(hit, in ask);
            if (!math.all(math.isfinite(planeWorld)))
            {
                Note(record, "invalid", 0, "invalid: the plane is not finite");
                SetOutcome(record, "Invalid", false);
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            if (group.Preparing > 0)
            {
                Preparation p = PreparationOf(group);
                if (p != null && SameAdoptedPlane(p.slashId, p.planeId, ask.slashId, ask.adoptedPlaneId))
                {
                    Attach(p, record, "the same request as the group's preparation");
                    return ProvisionalCutAcceptance.Pending;
                }

                HoldHit(group.Key, ask.source, planeWorld, in ask, record, "preparing: the group's cut is being prepared" + (p != null ? " (preparation " + p.id + ", another request)" : ""));
                return ProvisionalCutAcceptance.Pending;
            }

            if (group.Busy)
            {
                if (SameAdoptedPlane(group.LastCutSlashId, group.LastCutPlaneId, ask.slashId, ask.adoptedPlaneId))
                {
                    Include(group, record, "the cut that made the group busy is the same request");
                    return ProvisionalCutAcceptance.Published;
                }

                HoldHit(group.Key, ask.source, planeWorld, in ask, record, "busy: the group is being cut (another request)");
                return ProvisionalCutAcceptance.Pending;
            }

            return BeginPreparation(group, hit, planeWorld, in ask, null, record, out admission);
        }

        private void Attach(Preparation p, HitRecord record, string why)
        {
            p.hits.Add(record);
            HitsAttached++;
            Note(record, "attached", p.id, "attached to preparation " + p.id + ": " + why);
            Record("hit " + record.id + " attached t " + _physicsSeconds().ToString("F3") + " slash " + record.slashId + " member " + record.fragment + " to preparation " + p.id);
        }

        private void Include(FusedGroup group, HitRecord record, string why)
        {
            HitsIncluded++;
            Note(record, "included", 0, "included: " + why);
            SetOutcome(record, "Included: the cut of slash " + group.LastCutSlashId + " plane " + group.LastCutPlaneId + " in progress on the group (published)", true);
            Record("hit " + record.id + " included t " + _physicsSeconds().ToString("F3") + " slash " + record.slashId + " member " + record.fragment);
        }

        private void HoldHit(int key, LogicalFragmentId fragment, float4 planeWorld, in ProvisionalCutAsk ask, HitRecord record, string why)
        {
            Building b = BuildingOf(key);
            b.held.Add(new HeldRequest
            {
                fragment = fragment, planeWorld = planeWorld, planeId = ask.adoptedPlaneId, positiveImpulse = ask.positiveSeparationImpulse, negativeImpulse = ask.negativeSeparationImpulse,
                renderAnchor = ask.renderAnchor, slashId = ask.slashId, heldAt = realSeconds(), reason = why, hit = record,
            });
            HeldRequests++;
            HitsHeld++;
            Note(record, "held", 0, "held: " + why);
            Record("hit " + (record != null ? record.id.ToString() : "?") + " held t " + _physicsSeconds().ToString("F3") + " building " + b.key + " slash " + ask.slashId + " member " + fragment.value + ": " + why + " (" + b.held.Count + " held)");
        }

        /// <summary>A hit held once already (its count stands), held again with a new reason: its group was preparing or busy with another request when it was routed.</summary>
        private void HoldAgain(Building b, HeldRequest h, string why)
        {
            h.reason = why;
            b.held.Add(h);
            HitsHeldAgain++;
            Note(h.hit, "held", 0, "held again: " + why);
        }

        /// <summary>
        /// A held hit against the group its member stands in now: attached when the group prepares the same request;
        /// included when the group is busy with that same cut; kept (with its reason) while the group prepares or cuts
        /// another; prepared when the group is free. Its outcome is set when it is final.
        /// </summary>
        private void RouteHeld(Building b, HeldRequest h, FusedGroup group)
        {
            if (group.Preparing > 0)
            {
                Preparation p = PreparationOf(group);
                if (p != null && SameAdoptedPlane(p.slashId, p.planeId, h.slashId, h.planeId))
                {
                    HeldProcessed++;
                    HeldRouted++;
                    _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, "attached to preparation " + p.id + " (the same request; the result is the hit's record)", true));
                    Attach(p, h.hit, "the same request as the group's preparation (from held: " + h.reason + ")");
                    return;
                }

                HoldAgain(b, h, "preparing: the group's cut is being prepared (another request)");
                return;
            }

            if (group.Busy)
            {
                if (SameAdoptedPlane(group.LastCutSlashId, group.LastCutPlaneId, h.slashId, h.planeId))
                {
                    HeldProcessed++;
                    HeldRouted++;
                    _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, "included in the cut that made the group busy (the same request)", true));
                    Include(group, h.hit, "the cut that made the group busy is the same request (from held: " + h.reason + ")");
                    return;
                }

                HoldAgain(b, h, "busy: the group is being cut (another request)");
                return;
            }

            if (group.MemberCount == 0)
            {
                HeldProcessed++;
                HeldRefused++;
                _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, "the group is empty", false));
                Note(h.hit, "refused", 0, "refused: the group is empty");
                SetOutcome(h.hit, "NotAccepted: the group is empty", false);
                return;
            }

            MaxHeldWaitSeconds = Math.Max(MaxHeldWaitSeconds, realSeconds() - h.heldAt);
            FusedGroup.Member member = group.byFragment.TryGetValue(h.fragment, out FusedGroup.Member m) ? m : group.members[0];
            var ask = new ProvisionalCutAsk { source = member.fragment, positiveSeparationImpulse = h.positiveImpulse, negativeSeparationImpulse = h.negativeImpulse, renderAnchor = h.renderAnchor, slashId = h.slashId, adoptedPlaneId = h.planeId, adoptedPlaneWorld = h.planeWorld };
            ProvisionalCutAcceptance acceptance = BeginPreparation(group, member, h.planeWorld, in ask, h, h.hit, out _);
            HeldProcessed++;
            if (acceptance == ProvisionalCutAcceptance.Pending)
            {
                HeldRouted++;
                _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, "prepared by preparation " + _lastPreparationId + " (the result is the hit's record)", true));
            }
            else
            {
                HeldRefused++;
                _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, acceptance + (_lastRefusal != null ? ": " + _lastRefusal : ""), false));
                Note(h.hit, "refused", 0, "refused: " + acceptance);
                SetOutcome(h.hit, acceptance + (_lastRefusal != null ? ": " + _lastRefusal : ""), false);
            }
        }

        /// <summary>
        /// The held hits of the buildings that are not aggregating (those held for a preparation or a cut in progress),
        /// routed again under the budget; dropped -- on record -- when the member was replaced by a Final meanwhile (a
        /// child is not cut by a plane that was asked of its parent).
        /// </summary>
        private void AdvanceHeld()
        {
            foreach (Building b in _buildings.Values)
            {
                if (b.aggregating || b.held.Count == 0)
                {
                    continue;
                }

                var held = new List<HeldRequest>(b.held);
                b.held.Clear();
                for (int i = 0; i < held.Count; i++)
                {
                    HeldRequest h = held[i];
                    if (!MayStart())
                    {
                        for (; i < held.Count; i++) b.held.Add(held[i]);   // the rest wait for the next Step
                        return;
                    }

                    if (!_registry.TryGet(h.fragment, out PhysicsFragmentOwner owner) || !owner.IsFused || owner.Group.Root == null)
                    {
                        HeldProcessed++;
                        HeldRefused++;
                        string outcome = "Dropped: the member was replaced by a Final before this hit (another request) could be cut";
                        _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, outcome, false));
                        Note(h.hit, "dropped", 0, outcome);
                        SetOutcome(h.hit, outcome, false);
                        Record("held hit dropped t " + _physicsSeconds().ToString("F3") + " building " + b.key + " slash " + h.slashId + " member " + h.fragment.value);
                        continue;
                    }

                    RouteHeld(b, h, owner.Group);
                }
            }
        }

        // ---- the preparation of a cut ----

        private sealed class MemberSnapshot
        {
            public FusedGroup.Member member;
            public PhysicsOwnerShape shape;
            public bool held;   // the shape's work hold, taken at the snapshot
            public float4 planeLocal;
            public float mass;
            public float3 inertia;
            public quaternion inertiaRotation;
            public NativeArray<byte> block;   // the classification's block, allocated on Main
            public float4x4 rootToGroup;   // the member's Root in the group's frame, read at the snapshot (a group's members do not move in it while its generation holds)
            // The worker's results.
            public bool classified, split, positive, divided;
            public PhysicsCutClassification classification;   // kept for a crossed member (the cook reads it); null otherwise
            public ProvisionalBoxMass.Side positiveSide, negativeSide;

            public void Release()
            {
                classification?.Dispose();
                classification = null;
                if (block.IsCreated) { block.Dispose(); Interlocked.Decrement(ref s_liveSnapshotBlocks); }
                if (held) { shape.ReleaseFromWork(); held = false; }
            }
        }

        private static int s_liveSnapshotBlocks;

        /// <summary>Classification blocks allocated for snapshots and not yet handed to a classification or given back (tests: a leak shows here).</summary>
        public static int LiveSnapshotBlocks => Volatile.Read(ref s_liveSnapshotBlocks);

        /// <summary>The scan and the box rule of every member, on the dispatcher's background pool; the inputs go back with the work when its owner has gone.</summary>
        private sealed class PreparationWork : IDispatchWork
        {
            private readonly List<MemberSnapshot> _members;
            private readonly float _supportEpsilon;
            private readonly int _vertexLimit;
            private readonly ManualResetEventSlim _gate;
            private int _done;
            private int _begun;
            internal volatile bool collected;   // the dispatcher handed it back (finished or cancelled): the inputs are the fusion's again
            internal volatile bool abandoned;   // the fusion let it go while the dispatcher had it: the inputs are released at the collection
            internal bool released;
            internal WorkCompletion completion;
            internal Exception failure;
            internal double seconds;
            internal int threadId;

            internal PreparationWork(List<MemberSnapshot> members, float supportEpsilon, int vertexLimit, ManualResetEventSlim gate)
            {
                _members = members;
                _supportEpsilon = supportEpsilon;
                _vertexLimit = vertexLimit;
                _gate = gate;
            }

            internal bool Done => Volatile.Read(ref _done) == 1;
            internal bool Begun => Volatile.Read(ref _begun) == 1;

            public void Begin()
            {
                long begin = Stopwatch.GetTimestamp();
                threadId = Thread.CurrentThread.ManagedThreadId;
                Volatile.Write(ref _begun, 1);
                try
                {
                    _gate?.Wait(30000);   // tests only: the scan held until the test lets it go
                    foreach (MemberSnapshot m in _members)
                    {
                        NativeArray<byte> block = m.block;
                        m.block = default;   // the classification owns it from here, on either answer
                        Interlocked.Decrement(ref s_liveSnapshotBlocks);
                        if (!PhysicsCutClassification.TryClassifyInto(m.shape, m.planeLocal, _supportEpsilon, m.mass, _vertexLimit, block, out PhysicsCutClassification cls))
                        {
                            continue;   // not classifiable: the publication refuses the whole cut
                        }

                        m.classified = true;
                        m.split = cls.SplitsBothSides;
                        m.positive = m.split || cls.SupportsPositive || !cls.SupportsNegative;
                        if (m.split)
                        {
                            m.classification = cls;
                            m.divided = ProvisionalBoxMass.TryDivide(m.shape, m.planeLocal, m.mass, m.inertia, m.inertiaRotation, out m.positiveSide, out m.negativeSide, out _);
                        }
                        else
                        {
                            cls.Dispose();
                        }
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    Volatile.Write(ref _done, 1);
                }
            }

            public bool IsComplete => Done;

            /// <summary>The dispatcher hands the work back, finished or cancelled: the inputs are the fusion's again -- released here at once when the fusion has already let this preparation go.</summary>
            public void Collect(WorkCompletion completion)
            {
                this.completion = completion;
                collected = true;
                if (abandoned)
                {
                    Release();
                }
            }

            internal void Release()
            {
                if (released) return;
                released = true;
                foreach (MemberSnapshot m in _members) m.Release();
            }
        }

        private sealed class Preparation
        {
            public int id;
            public FusedGroup group;
            public int building;
            public int generation;
            public float4 planeWorld;   // the adopted plane as it was in the world at the request (the record; the group may have moved since)
            public float4 planeGroup;   // the adopted plane in the group's own frame, taken once: what every member's plane is carried from
            public long planeId;
            public long slashId;
            public float positiveImpulse, negativeImpulse;
            public float3 renderAnchor;
            public LogicalFragmentId hit;
            public readonly List<HitRecord> hits = new List<HitRecord>(2);   // the hit it was made for, and those attached: they share its outcome
            public List<MemberSnapshot> members;   // null until the snapshot began
            public int snapshotIndex;
            public bool snapshotted;
            public PreparationWork work;
            public WorkTicket ticket;
            public bool offered;
            public int retries;
            public double madeAt;
            public HeldRequest held;   // when the hit was held: its outcome is recorded at the end
            public bool workCounted;   // the worker's seconds taken into the record (once, however often the publication is tried)
            public bool waitingForLayers;   // scanned and judged, waiting for a free layer pair
            public readonly Dictionary<FusedGroup.Member, PreparedShadow> shadows = new Dictionary<FusedGroup.Member, PreparedShadow>();   // the crossed members' copies made so far, inactive under the group's root
        }

        /// <summary>A crossed member's copy made ahead of the switch: inactive under the old group's root (it moves with the group and answers nothing), its colliders as made.</summary>
        private sealed class PreparedShadow
        {
            public GameObject root;
            public readonly List<MeshCollider> colliders = new List<MeshCollider>(4);
        }

        /// <summary>Copies made ahead (in all), made ahead and then thrown away (the preparation refused, made again or abandoned), and the Main seconds of making them (a part of <see cref="ClassifySeconds"/>' frames, apart from it).</summary>
        public int ShadowsPrepared { get; private set; }
        public int ShadowsPreparedDropped { get; private set; }
        public double ShadowPrepareSeconds { get; private set; }
        public double MaxShadowPrepareSeconds { get; private set; }

        /// <summary>
        /// The crossed members' copies of a scanned preparation, one member a unit under the budget, over as many Steps as
        /// it takes: each made inactive under the group's root at its member's place (the group stays as it is and the
        /// copies answer nothing until the switch). True when every crossed member has its copy.
        /// </summary>
        private bool PrepareShadows(Preparation p)
        {
            foreach (MemberSnapshot m in p.members)
            {
                if (!m.split || !m.classified || p.shadows.ContainsKey(m.member))
                {
                    continue;
                }

                if (!MayStart())
                {
                    return false;
                }

                long begin = Stopwatch.GetTimestamp();
                var prepared = new PreparedShadow();
                prepared.root = MakeShadow(m.member, p.group.Root.transform, prepared.colliders, true);
                p.shadows.Add(m.member, prepared);
                ShadowsPrepared++;
                double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                ShadowPrepareSeconds += seconds;
                MaxShadowPrepareSeconds = Math.Max(MaxShadowPrepareSeconds, seconds);
            }

            return true;
        }

        /// <summary>The copies made ahead go (the preparation did not publish, or is made again).</summary>
        private void DropPreparedShadows(Preparation p)
        {
            foreach (PreparedShadow prepared in p.shadows.Values)
            {
                if (prepared.root != null) PhysicsOwnerBuilder.DestroyObject(prepared.root);
                ShadowsPreparedDropped++;
            }

            p.shadows.Clear();
        }

        private readonly List<Preparation> _preparations = new List<Preparation>();
        private SharedWorkDispatcher _dispatcher;
        private int _lastPreparationId;

        /// <summary>Tests only: a gate every worker scan waits on before it begins, so that a preparation can be kept running; null for none.</summary>
        internal ManualResetEventSlim workerGate;

        /// <summary>
        /// Tests only: asked right after an offer succeeds; true has the offer taken back from the dispatcher's queue
        /// (as its shutdown or a reclassification would) and handed its one Cancelled collection, in this same Step --
        /// the hook may burn the Step's budget as well. What the fusion then does with the collected, unbegun work is
        /// what the test looks at.
        /// </summary>
        internal Func<bool> cancelOfferForTest;

        /// <summary>Tests only: at most this many members snapshotted in one Step (0: as the budget allows), so that a snapshot is spread over frames on purpose.</summary>
        internal int snapshotMembersPerStepForTest;

        /// <summary>Preparations not yet published, refused or abandoned.</summary>
        public int PreparationsInFlight => _preparations.Count;

        /// <summary>
        /// Whether nothing of this fusion is waiting: no hit without its outcome, no hold not ended, no preparation, no
        /// crossed member's cut in progress and no aggregation begun and not completed. What waits, when something does,
        /// is <see cref="DescribeUnsettled"/>.
        /// </summary>
        public bool IsSettled => HitsPending == 0 && HeldProcessed == HeldRequests && _preparations.Count == 0 && _cuts.Count == 0
            && BuildingsAggregating == 0 && AggregationsCompleted == AggregationsBegun;

        /// <summary>What waits, by kind and reason (a check's record when a wait for the fusion ends at its deadline).</summary>
        public string DescribeUnsettled()
        {
            var hits = new Dictionary<string, int>();
            foreach (HitRecord h in _hits) if (h.IsPending) { hits.TryGetValue(h.state ?? "none", out int n); hits[h.state ?? "none"] = n + 1; }
            int snapshotting = 0, unoffered = 0, onWorker = 0, waitingForBusy = 0, waitingForLayers = 0, scanned = 0;
            foreach (Preparation p in _preparations)
            {
                if (!p.snapshotted) snapshotting++;
                else if (!p.offered) unoffered++;
                else if (p.work == null || !p.work.Done) onWorker++;
                else if (p.group != null && p.group.Busy) waitingForBusy++;
                else if (p.waitingForLayers) waitingForLayers++;
                else scanned++;
            }

            var stages = new Dictionary<string, int>();
            foreach (MemberCut c in _cuts) { string stage = c.request == null ? "none" : c.request.IsOver ? "over, Final not collected" : c.request.Stage.ToString(); stages.TryGetValue(stage, out int n); stages[stage] = n + 1; }
            string Join(Dictionary<string, int> d) => d.Count == 0 ? "none" : string.Join(", ", System.Linq.Enumerable.Select(d, e => e.Key + " " + e.Value));
            return "hits without an outcome " + HitsPending + " (by state: " + Join(hits) + "); holds not ended " + (HeldRequests - HeldProcessed) + "; preparations " + _preparations.Count
                + " (snapshot " + snapshotting + ", not yet offered " + unoffered + ", on the worker " + onWorker + ", scanned and waiting for a busy group " + waitingForBusy + ", waiting for a free layer pair " + waitingForLayers + ", scanned " + scanned + ")"
                + "; crossed members' cuts in progress " + _cuts.Count + " (by stage: " + Join(stages) + "); buildings aggregating " + BuildingsAggregating + ", aggregations begun " + AggregationsBegun + " completed " + AggregationsCompleted;
        }
        public int PreparationsMade { get; private set; }
        public int PreparationsPublished { get; private set; }
        public int PreparationsRefused { get; private set; }   // judged on Main and refused as a whole (a member's own refusal, the room, an empty side, a failure on the worker)
        public int PreparationsStale { get; private set; }   // the group changed under the preparation: made again
        public int PreparationsAbandoned { get; private set; }   // stale twice, the group gone, or the world's end
        public int PreparationsReoffered { get; private set; }   // cancelled by the dispatcher before the scan began (its shutdown, a reclassification): offered again
        public int PreparationsCancelledAtEnd { get; private set; }   // at the end: works the dispatcher had handed back as Cancelled (never begun) before the fusion was disposed
        public double MaxPreparationSeconds { get; private set; }   // real seconds from the hit to the publication

        /// <summary>Tests only: the worker thread the last preparation's scan ran on (0 when it never ran).</summary>
        internal int lastPreparationThreadId;

        /// <summary>Tests only: whether some preparation's work is with the dispatcher and not yet collected.</summary>
        internal bool AnyPreparationOffered
        {
            get { foreach (Preparation p in _preparations) if (p.offered && p.work != null && !p.work.collected) return true; return false; }
        }

        /// <summary>
        /// Tests only: the planes the first in-flight preparation's snapshotted members carry, each brought back into the
        /// group's frame through the member's own placement in the group, and the plane the preparation adopted in that
        /// frame. One plane for all is what a split snapshot of a moving group must show.
        /// </summary>
        internal bool TryReadSnapshotPlanes(List<float4> planesInGroupFrame, out float4 planeGroup)
        {
            planesInGroupFrame.Clear();
            planeGroup = default;
            if (_preparations.Count == 0 || _preparations[0].members == null || _preparations[0].group.Root == null) return false;
            Preparation p = _preparations[0];
            planeGroup = p.planeGroup;
            float4x4 worldToGroup = (float4x4)p.group.Root.transform.worldToLocalMatrix;
            foreach (MemberSnapshot m in p.members)
            {
                if (m.member.owner.Root == null) continue;
                float4x4 shapeToGroup = math.mul(worldToGroup, math.mul((float4x4)m.member.owner.Root.transform.localToWorldMatrix, m.shape.LocalToOwner));
                float4 plane = TransformPlane(shapeToGroup, m.planeLocal);
                planesInGroupFrame.Add(plane / math.length(plane.xyz));
            }

            return true;
        }

        private Preparation PreparationOf(FusedGroup group)
        {
            foreach (Preparation p in _preparations) if (ReferenceEquals(p.group, group)) return p;
            return null;
        }

        /// <summary>
        /// A hit's cut begins as a preparation: only what the cut needs is written down here (the group, the hit, the
        /// adopted plane carried once into the group's frame, the impulses, the Slash, the held link); the snapshot,
        /// the offer and the publication are the Steps', under the budget. Pending.
        /// </summary>
        private ProvisionalCutAcceptance BeginPreparation(FusedGroup group, FusedGroup.Member hit, float4 planeWorld, in ProvisionalCutAsk ask, HeldRequest held, HitRecord record, out LogicalCutAdmission admission)
        {
            admission = LogicalCutAdmission.NoOp;
            if (!math.all(math.isfinite(planeWorld)) || group.Root == null)
            {
                Note(record, "invalid", 0, "invalid: the plane is not finite or the group is gone");
                SetOutcome(record, "Invalid", false);
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            var p = new Preparation
            {
                id = ++_lastPreparationId, group = group, building = group.Key, planeWorld = planeWorld, planeId = ask.adoptedPlaneId, slashId = ask.slashId,
                planeGroup = TransformPlane((float4x4)group.Root.transform.worldToLocalMatrix, planeWorld),
                positiveImpulse = ask.positiveSeparationImpulse, negativeImpulse = ask.negativeSeparationImpulse, renderAnchor = ask.renderAnchor,
                hit = hit.fragment, held = held, madeAt = realSeconds(),
            };
            if (record != null) p.hits.Add(record);
            group.Preparing++;
            _preparations.Add(p);
            PreparationsMade++;
            HitsPrepared++;
            Note(record, "prepared", p.id, held != null ? "prepared by preparation " + p.id + " (from held: " + held.reason + ")" : "prepared by preparation " + p.id);
            return ProvisionalCutAcceptance.Pending;
        }

        /// <summary>
        /// The snapshot of a preparation's group, some members a call, under the budget: every member's shape held and its
        /// plane (from the group's frame, through the member's own placement in it, so that a group moving between the
        /// calls changes nothing) and block made; the generation is read at the start and a group that changed meanwhile
        /// starts over. True when the snapshot is complete; false while it is not (or was refused, then the preparation
        /// is over).
        /// </summary>
        private bool ContinueSnapshot(Preparation p, out bool refused)
        {
            refused = false;
            FusedGroup group = p.group;
            if (p.members == null)
            {
                p.members = new List<MemberSnapshot>(group.MemberCount);
                p.generation = group.Generation;
                p.snapshotIndex = 0;
            }
            else if (group.Generation != p.generation)
            {
                foreach (MemberSnapshot made in p.members) made.Release();
                p.members.Clear();
                p.generation = group.Generation;
                p.snapshotIndex = 0;
                PreparationsStale++;
            }

            float4x4 worldToGroup = (float4x4)group.Root.transform.worldToLocalMatrix;
            int madeNow = 0;
            while (p.snapshotIndex < group.members.Count)
            {
                if (snapshotMembersPerStepForTest > 0 && madeNow >= snapshotMembersPerStepForTest)
                {
                    return false;
                }

                if (p.snapshotIndex > 0 && !MayStart())
                {
                    return false;
                }

                FusedGroup.Member m = group.members[p.snapshotIndex];
                if (m.owner.Root == null || m.owner.Shape == null || m.owner.Shape.IsFreed || PhysicsCutClassification.RequiredBlockBytes(m.owner.Shape) < 0)
                {
                    foreach (MemberSnapshot made in p.members) made.Release();
                    p.members.Clear();
                    MembersRefused++;
                    refused = true;
                    _lastRefusal = "member " + m.fragment.value + " has no shape to classify -- the whole cut is refused, nothing changed";
                    return false;
                }

                BeginPart();
                int bytes = PhysicsCutClassification.RequiredBlockBytes(m.owner.Shape);
                float4x4 rootToGroup = math.mul(worldToGroup, (float4x4)m.owner.Root.transform.localToWorldMatrix);
                float4x4 shapeToGroup = math.mul(rootToGroup, m.owner.Shape.LocalToOwner);
                var snapshot = new MemberSnapshot
                {
                    member = m, shape = m.owner.Shape, planeLocal = TransformPlane(math.inverse(shapeToGroup), p.planeGroup), mass = m.mass, inertia = m.inertia, inertiaRotation = m.inertiaRotation,
                    rootToGroup = rootToGroup,
                    block = PhysicsCutBlocks.Take<byte>(bytes),
                };
                Interlocked.Increment(ref s_liveSnapshotBlocks);
                snapshot.shape.AcquireForWork();
                snapshot.held = true;
                p.members.Add(snapshot);
                p.snapshotIndex++;
                madeNow++;
                MembersClassified++;
                ClassifySeconds += EndPart();
            }

            p.work = new PreparationWork(p.members, _supportEpsilon, _vertexLimit, workerGate);
            p.snapshotted = true;
            p.offered = false;
            p.ticket = default;
            return true;
        }

        private void Offer(Preparation p)
        {
            if (p.offered || !p.snapshotted)
            {
                return;
            }

            if (_dispatcher != null && _dispatcher.TryEnqueue(WorkPurpose.Speculative, p.work, out WorkTicket ticket))
            {
                p.ticket = ticket;
                p.offered = true;
            }
        }

        /// <summary>
        /// One turn of the preparations, each under the budget: the unsnapshotted are snapshotted (some members a call),
        /// the unoffered are offered again (the dispatcher's room decides; nothing is scanned on Main), a scan the
        /// dispatcher cancelled before it began is offered again, and the scanned are judged and published.
        /// </summary>
        private void AdvancePreparations()
        {
            for (int i = 0; i < _preparations.Count; i++)
            {
                Preparation p = _preparations[i];
                if (!p.snapshotted)
                {
                    if (p.group.Root == null || p.group.MemberCount == 0)
                    {
                        if (p.members != null) foreach (MemberSnapshot m in p.members) m.Release();
                        EndPreparation(p, false, "Abandoned: the group is gone");
                        PreparationsAbandoned++;
                        _preparations.RemoveAt(i--);
                        continue;
                    }

                    if (!MayStart())
                    {
                        return;
                    }

                    if (!ContinueSnapshot(p, out bool refused))
                    {
                        if (refused)
                        {
                            PreparationsRefused++;
                            Refuse(p.group, LogicalCutAdmission.NoOp, _lastRefusal, out _);
                            EndPreparation(p, false, "NotAccepted: " + _lastRefusal);
                            _preparations.RemoveAt(i--);
                        }

                        continue;
                    }
                }

                if (!p.offered)
                {
                    Offer(p);
                    if (!p.offered)
                    {
                        continue;   // no room on the dispatcher this Step: offered again next Step
                    }

                    if (cancelOfferForTest != null && cancelOfferForTest() && _dispatcher.Cancel(p.ticket))
                    {
                        p.work.Collect(WorkCompletion.Cancelled);
                        continue;
                    }
                }

                if (p.work.collected && !p.work.Begun)
                {
                    // Cancelled by the dispatcher before it began (a reclassification, a shutdown): the inputs are ours again; offered again.
                    p.offered = false;
                    p.ticket = default;
                    p.work.collected = false;
                    PreparationsReoffered++;
                    Offer(p);
                    continue;
                }

                if (!p.work.Done)
                {
                    continue;
                }

                if (!MayStart())
                {
                    return;
                }

                if (TryPublishPrepared(p))
                {
                    _preparations.RemoveAt(i--);
                }
            }
        }

        /// <summary>The preparation is over: its group's count goes back, and every hit it carries (its own and the attached) takes its one outcome.</summary>
        private void EndPreparation(Preparation p, bool published, string outcome)
        {
            DropPreparedShadows(p);   // a published preparation handed its copies to its cuts (none left here)
            if (p.group != null) p.group.Preparing = Math.Max(0, p.group.Preparing - 1);
            MaxPreparationSeconds = Math.Max(MaxPreparationSeconds, realSeconds() - p.madeAt);
            foreach (HitRecord hit in p.hits)
            {
                Note(hit, published ? "published" : "refused", p.id, "preparation " + p.id + " ended: " + outcome);
                SetOutcome(hit, outcome, published);
            }

            if (p.held != null)
            {
                // Its routing was counted when the hold ended; its result is the hit's record (set just above).
                Record("held hit's preparation " + p.id + " ended t " + _physicsSeconds().ToString("F3") + " building " + p.building + " slash " + p.held.slashId + ": " + outcome);
            }

            if (p.hits.Count > 1)
            {
                Record("preparation " + p.id + " ended " + outcome + ": " + (p.hits.Count - 1) + " attached hit(s) take that outcome");
            }
        }

        /// <summary>
        /// A scanned preparation, judged on Main and published, all or none: the group must be the one snapshotted (its
        /// generation) and free; every member classifiable; the ledger's room and every crossed member's own preconditions;
        /// then the admissions and the sides, with the adopted plane where the group stands now. True when the
        /// preparation is over (published, refused or abandoned): false when it must wait (the group busy) or was made again.
        /// </summary>
        private bool TryPublishPrepared(Preparation p)
        {
            FusedGroup group = p.group;
            lastPreparationThreadId = p.work.threadId;
            if (!p.workCounted)
            {
                p.workCounted = true;
                WorkerSeconds += p.work.seconds;
                MaxWorkerSeconds = Math.Max(MaxWorkerSeconds, p.work.seconds);
            }

            if (p.work.failure != null)
            {
                p.work.Release();
                PreparationsRefused++;
                string failure = "the preparation failed on the worker: " + p.work.failure.GetType().Name + ": " + p.work.failure.Message;
                Refuse(group, LogicalCutAdmission.NoOp, failure, out _);
                EndPreparation(p, false, "NotAccepted: " + failure);
                return true;
            }

            if (group.Root == null || group.MemberCount == 0)
            {
                p.work.Release();
                PreparationsAbandoned++;
                EndPreparation(p, false, "Abandoned: the group is gone");
                return true;
            }

            if (group.Busy)
            {
                return false;   // its Finals first
            }

            bool stale = group.Generation != p.generation;
            if (!stale)
            {
                foreach (MemberSnapshot m in p.members)
                {
                    if (!ReferenceEquals(m.member.owner.Shape, m.shape) || m.shape.IsFreed || m.member.owner.Root == null || !group.byFragment.ContainsKey(m.member.fragment)) { stale = true; break; }
                }
            }

            if (stale)
            {
                p.work.Release();
                PreparationsStale++;
                if (p.retries >= 1)
                {
                    PreparationsAbandoned++;
                    EndPreparation(p, false, "Abandoned: the group changed twice under the preparation");
                    return true;
                }

                // Made again, once, on the group the hit's member stands in now (the same one, or the one it moved to); the plane goes where the old group stands now.
                DropPreparedShadows(p);
                p.workCounted = false;
                p.retries++;
                float4 planeWorldNow = group.Root != null ? TransformPlane((float4x4)group.Root.transform.localToWorldMatrix, p.planeGroup) : p.planeWorld;
                FusedGroup now = _registry.TryGet(p.hit, out PhysicsFragmentOwner owner) && owner.IsFused && owner.Group.Root != null ? owner.Group : group.Root != null ? group : null;
                if (now == null || now.MemberCount == 0)
                {
                    PreparationsAbandoned++;
                    EndPreparation(p, false, "Abandoned: the hit's member is gone");
                    return true;
                }

                if (!ReferenceEquals(now, group))
                {
                    group.Preparing = Math.Max(0, group.Preparing - 1);
                    now.Preparing++;
                    p.group = now;
                }

                p.planeWorld = planeWorldNow;
                p.planeGroup = TransformPlane((float4x4)now.Root.transform.worldToLocalMatrix, planeWorldNow);
                p.members = null;
                p.snapshotted = false;
                p.offered = false;
                p.ticket = default;
                p.work = null;
                foreach (HitRecord hit in p.hits) Note(hit, hit.state, p.id, "preparation " + p.id + " made again: the group changed");
                Record("preparation " + p.id + " made again t " + _physicsSeconds().ToString("F3") + " building " + p.building + " slash " + p.slashId + ": the group changed");
                return false;
            }

            if (!PrepareShadows(p))
            {
                return false;   // the budget is spent: the rest of the copies in the next Step (the group is judged again then)
            }

            BeginPart();
            var memberCuts = new List<MemberCut>();
            string refused = null;
            LogicalCutAdmission why = LogicalCutAdmission.NoOp;
            _classified.Clear();
            try
            {
                bool anyPositive = false, anyNegative = false;
                int crossed = 0;
                foreach (MemberSnapshot m in p.members)
                {
                    if (!m.classified) { refused = "member " + m.member.fragment.value + " could not be classified against the plane"; MembersRefused++; break; }
                    if (m.split && !m.divided) { refused = "member " + m.member.fragment.value + " has a mass the plane cannot divide"; MembersRefused++; break; }
                    if (m.split) crossed++;
                    _classified.Add(new Classified(m.member, m.split ? m.classification : null, m.planeLocal, m.split, m.positive));
                    anyPositive |= !m.split && m.positive; anyNegative |= !m.split && !m.positive;
                }

                if (refused == null && crossed == 0 && !(anyPositive && anyNegative))
                {
                    _classified.Clear();
                    p.work.Release();
                    ClassifySeconds += EndPart();
                    EndPreparation(p, false, "EmptySide");
                    return true;
                }

                MaxMembersInOneCut = Math.Max(MaxMembersInOneCut, _classified.Count);
                if (refused == null)
                {
                    int room = _ledger.Budget.MaxIncompleteCutOperationCount - _ledger.Budget.IncompleteCutOperationCount;
                    if (crossed > room) { refused = crossed + " crossed members, ledger room " + room; why = LogicalCutAdmission.Full; }
                }

                bool positiveFixed = false, negativeFixed = false;
                if (refused == null)
                {
                    foreach (MemberSnapshot m in p.members)
                    {
                        if (!m.split)
                        {
                            if (m.positive) positiveFixed |= m.member.fixedByAnchors; else negativeFixed |= m.member.fixedByAnchors;
                            continue;
                        }

                        LogicalFragmentId fragment = m.member.fragment;
                        if (!_ledger.IsCurrentTarget(fragment)) { refused = "member " + fragment.value + " is not a live fragment"; why = LogicalCutAdmission.SourceNotLive; MembersRefused++; break; }
                        if (_ledger.TryGetActiveOperation(fragment, out _)) { refused = "member " + fragment.value + " is under a cut already"; why = LogicalCutAdmission.SourceActive; MembersRefused++; break; }
                        if (!_ledger.TryJudgeAnchorDistribution(fragment, m.planeLocal, _anchorEpsilon, out AnchorDistributionResult anchors))
                        {
                            refused = "member " + fragment.value + " has anchors the plane cannot distribute (" + anchors.status + ")"; MembersRefused++; break;
                        }

                        positiveFixed |= anchors.IsPositiveFixed;
                        negativeFixed |= anchors.IsNegativeFixed;
                        memberCuts.Add(new MemberCut { member = m.member, classification = m.classification, planeLocal = m.planeLocal, anchors = anchors, positiveSide = m.positiveSide, negativeSide = m.negativeSide });
                    }
                }

                if (refused != null)
                {
                    _classified.Clear();
                    p.work.Release();
                    PreparationsRefused++;
                    ClassifySeconds += EndPart();
                    Refuse(group, why, refused + " -- the whole cut is refused, nothing changed", out _);
                    EndPreparation(p, false, "NotAccepted: " + refused);
                    return true;
                }

                // The exclusion of the copies: a layer pair when one is free; none free, the preparation waits for one before
                // anything is admitted (Pending), and is judged again next Step.
                GroupCutExclusion exclusion = memberCuts.Count > 0 ? ChooseExclusion(memberCuts) : null;
                if (memberCuts.Count > 0 && exclusion == null)
                {
                    ClassifySeconds += EndPart();
                    if (!p.waitingForLayers)
                    {
                        p.waitingForLayers = true;
                        foreach (HitRecord hit in p.hits) Note(hit, hit.state, p.id, "preparation " + p.id + " waits for a free layer pair");
                    }

                    return false;
                }

                // The admissions, each judged possible above on this same state in this same call: another answer is a broken invariant.
                foreach (MemberCut cut in memberCuts)
                {
                    LogicalCutAdmission a = _dag != null
                        ? _dag.TryAdmit(cut.member.fragment, cut.planeLocal, true, out CutOperationId operation)
                        : _ledger.Admit(cut.member.fragment, cut.planeLocal, true, out operation);
                    if (a != LogicalCutAdmission.Admitted)
                    {
                        throw new InvalidOperationException("the ledger answered " + a + " for member " + cut.member.fragment.value + " whose admission had just been judged possible");
                    }

                    cut.operation = operation;
                    AnchorPreparationOutcome prepared = _ledger.PrepareAnchorDistribution(operation, _anchorEpsilon, out cut.anchors);
                    if (prepared != AnchorPreparationOutcome.Prepared)
                    {
                        throw new InvalidOperationException("the ledger answered " + prepared + " for the anchors of member " + cut.member.fragment.value + " whose distribution had just been judged possible");
                    }
                }

                double judged = EndPart();
                ClassifySeconds += judged;
                MaxClassifySeconds = Math.Max(MaxClassifySeconds, judged);
                BeginPart();
                var ask = new ProvisionalCutAsk { source = p.hit, positiveSeparationImpulse = p.positiveImpulse, negativeSeparationImpulse = p.negativeImpulse, renderAnchor = p.renderAnchor, slashId = p.slashId, adoptedPlaneId = p.planeId, adoptedPlaneWorld = p.planeWorld };
                int members = group.MemberCount;
                float4 planeWorldNow = TransformPlane((float4x4)group.Root.transform.localToWorldMatrix, p.planeGroup);   // the adopted plane where the group stands now
                using (s_cutMarker.Auto())
                {
                    PublishSides(group, planeWorldNow, in ask, memberCuts, positiveFixed, negativeFixed, p, exclusion);
                }

                double published = EndPart();
                PublishSeconds += published;
                if (published > MaxPublishSeconds) { MaxPublishSeconds = published; MaxCutMembers = members; }
                // The crossed members' classifications went to their cuts; the snapshot's holds go back (the cuts took holds of their own).
                foreach (MemberSnapshot m in p.members) { m.classification = null; m.Release(); }
                p.work.released = true;
                PreparationsPublished++;
                if (p.group != null) p.group.Preparing = Math.Max(0, p.group.Preparing - 1);
                p.group = null;   // the group went with its sides
                EndPreparation(p, true, "Published");
                return true;
            }
            finally
            {
                _classified.Clear();
            }
        }

        /// <summary>
        /// At the end: every preparation's inputs go back by their owner. Unsnapshotted or unoffered: the fusion's, now.
        /// Collected (the dispatcher hands everything back at its shutdown, which comes before this -- a work that had
        /// not begun comes back Cancelled): the fusion's, now. Still the dispatcher's: cancelled and released now when it
        /// had not begun; otherwise let go, and the work releases them itself when it is collected -- the dispatcher
        /// confirms its workers have stopped before then. Every hit still pending is refused as abandoned.
        /// </summary>
        private void AbandonPreparations()
        {
            foreach (Preparation p in _preparations)
            {
                if (p.work == null)
                {
                    if (p.members != null) foreach (MemberSnapshot m in p.members) m.Release();   // a snapshot part way, never offered
                }
                else if (!p.offered || p.work.collected || (_dispatcher != null && p.ticket.IsSet && _dispatcher.Cancel(p.ticket)))
                {
                    if (p.work.collected && !p.work.Begun) PreparationsCancelledAtEnd++;
                    p.work.Release();
                }
                else
                {
                    p.work.abandoned = true;   // running: released by its own collection
                    if (p.work.collected) p.work.Release();   // collected between the two reads: released here after all
                }

                EndPreparation(p, false, "Abandoned: the world ended");
                PreparationsAbandoned++;
            }

            _preparations.Clear();
        }

        // ---- the masses, summed off Main and applied before the physics step ----

        private sealed class PendingMass
        {
            public FusedGroup group;
            public NativeArray<MassSample> samples;
            public NativeArray<double> sums;
            public JobHandle handle;
            public bool scheduled;
            // What follows the apply: the group's motion from a union's momentum, or a side's inheritance with its push, or a source's kept field.
            public bool compose;
            public double3 linear, angular;
            public bool inherit;
            public double3 v0, w0, c0, push;
            public bool keepField;
            public double3 fieldV, fieldW, fieldC;
            public bool localFrame;   // the samples are in the group root's own frame (a new side's, from the snapshot), not the world's
        }

        private readonly Dictionary<FusedGroup, PendingMass> _pendingMass = new Dictionary<FusedGroup, PendingMass>();
        private readonly List<MassSample> _massSamples = new List<MassSample>(64);

        /// <summary>The number of mass applies waiting for the end of the Step (tests).</summary>
        internal int PendingMassCount => _pendingMass.Count;

        /// <summary>
        /// Gathers a group's members (their world centres and axes are read here, on Main) and schedules their sum; the
        /// apply comes at the end of the Step (<see cref="CompleteMassJobs"/>). A second call for the same group in one
        /// Step replaces the first (its job completed -- Main waits for it, counted in <see cref="MassRescheduleWaitSeconds"/> -- and its
        /// arrays freed). Outside a Step the apply comes with the next.
        /// </summary>
        private PendingMass ScheduleMass(FusedGroup group, List<MemberCut> cuts, bool positiveSide)
        {
            if (group.Body == null)
            {
                return null;
            }

            long begin = Stopwatch.GetTimestamp();
            double rescheduleWait = 0.0;
            // The members, and on a negative side the crossed members standing there by their copies; each member's cut
            // (when it has one among those asked with) found by a lookup, not a scan of the cuts per member.
            FillCutLookup(cuts);
            var carried = new List<FusedGroup.Member>(group.members);
            if (cuts != null && !positiveSide)
            {
                foreach (MemberCut cut in cuts) if (!IsMemberOf(group, cut.member)) carried.Add(cut.member);
            }

            _massSamples.Clear();
            foreach (FusedGroup.Member m in carried)
            {
                if (!Contribution(m, cuts != null, positiveSide, out double mass, out float3 centreLocal, out float3 inertia, out quaternion inertiaRotation, out Transform at)) continue;
                _massSamples.Add(new MassSample { mass = mass, centre = (float3)at.TransformPoint(centreLocal), inertia = inertia, axes = math.mul((quaternion)at.rotation, inertiaRotation) });
            }

            // A second schedule of the same group in one Step: the first job is completed (Main waits for it; counted) and replaced.
            double waitedBefore = MassRescheduleWaitSeconds;
            PendingMass pending = PendMass(group);
            rescheduleWait = MassRescheduleWaitSeconds - waitedBefore;
            double scheduled = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            MassSeconds += scheduled;
            MassSampleSeconds += scheduled - rescheduleWait;
            MaxMassSampleSeconds = Math.Max(MaxMassSampleSeconds, scheduled - rescheduleWait);
            MassSchedules++;
            MassSamplesGathered += _massSamples.Count;
            return pending;
        }

        private void ApplyMass(FusedGroup group, List<MemberCut> cuts, bool positiveSide) => ScheduleMass(group, cuts, positiveSide);

        /// <summary>
        /// A new side's mass sum from the preparation's snapshot, in the group's frame -- which is the side root's (a side
        /// is made where the group stands): a member of the side by its own properties, a crossed member by the side's
        /// share (the positive side by the member itself, the negative by its copy, both at the member's place). No
        /// member's transform is read here; the apply at the Step's end takes the sums as the root's.
        /// </summary>
        private PendingMass ScheduleSideMass(FusedGroup side, List<MemberSnapshot> snapshots, bool positiveSide)
        {
            if (side.Body == null)
            {
                return null;
            }

            long begin = Stopwatch.GetTimestamp();
            _massSamples.Clear();
            foreach (MemberSnapshot m in snapshots)
            {
                if (!m.split && m.positive != positiveSide)
                {
                    continue;
                }

                double mass; float3 centre, inertia; quaternion axes;
                if (m.split)
                {
                    ProvisionalBoxMass.Side share = positiveSide ? m.positiveSide : m.negativeSide;
                    mass = share.mass; centre = share.centerOfMass; inertia = share.inertia; axes = share.inertiaRotation;
                }
                else
                {
                    mass = m.member.mass; centre = m.member.centreLocal; inertia = m.member.inertia; axes = m.member.inertiaRotation;
                }

                _massSamples.Add(new MassSample { mass = mass, centre = math.transform(m.rootToGroup, centre), inertia = inertia, axes = math.mul(new quaternion(new float3x3(m.rootToGroup)), axes) });
            }

            PendingMass pending = PendMass(side);
            pending.localFrame = true;
            double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            MassSeconds += seconds;
            MassSampleSeconds += seconds;
            MaxMassSampleSeconds = Math.Max(MaxMassSampleSeconds, seconds);
            MassSchedules++;
            MassSamplesGathered += _massSamples.Count;
            return pending;
        }

        /// <summary>The gathered samples' sum scheduled for a group, replacing one already pending for it (its job completed first: counted).</summary>
        private PendingMass PendMass(FusedGroup group)
        {
            if (_pendingMass.TryGetValue(group, out PendingMass earlier))
            {
                long waitBegin = Stopwatch.GetTimestamp();
                FreePending(earlier);
                double waited = (Stopwatch.GetTimestamp() - waitBegin) / (double)Stopwatch.Frequency;
                MassRescheduleWaitSeconds += waited;
                MassWaitSeconds += waited;
                MassReschedules++;
                _pendingMass.Remove(group);
            }

            var pending = new PendingMass { group = group };
            if (_massSamples.Count > 0)
            {
                pending.samples = new NativeArray<MassSample>(_massSamples.Count, Allocator.TempJob, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < _massSamples.Count; i++) pending.samples[i] = _massSamples[i];
                pending.sums = new NativeArray<double>(10, Allocator.TempJob);
                pending.handle = new FusedMassJob { samples = pending.samples, sums = pending.sums }.Schedule();
                pending.scheduled = true;
            }

            _pendingMass[group] = pending;
            return pending;
        }

        private static void FreePending(PendingMass pending)
        {
            if (pending.scheduled) pending.handle.Complete();
            if (pending.samples.IsCreated) pending.samples.Dispose();
            if (pending.sums.IsCreated) pending.sums.Dispose();
            pending.scheduled = false;
        }

        /// <summary>Every pending sum completed (Main waits here for what is still running: the wait moved to the Step's end, it did not go away) and applied to its group's body, then the motion that follows it. Once per Step, at its end.</summary>
        private void CompleteMassJobs()
        {
            if (_pendingMass.Count == 0)
            {
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            double waited = 0.0;
            var parts = new double[8];
            int groups = 0, members = 0, hulls = 0;
            long mark = begin;
            void Part(int index)
            {
                long now = Stopwatch.GetTimestamp();
                parts[index] += (now - mark) / (double)Stopwatch.Frequency;
                mark = now;
            }

            foreach (KeyValuePair<FusedGroup, PendingMass> entry in _pendingMass)
            {
                PendingMass pending = entry.Value;
                FusedGroup group = entry.Key;
                mark = Stopwatch.GetTimestamp();
                try
                {
                    if (!pending.scheduled || group.Body == null || group.Root == null)
                    {
                        continue;
                    }

                    long waitBegin = Stopwatch.GetTimestamp();
                    pending.handle.Complete();
                    waited += (Stopwatch.GetTimestamp() - waitBegin) / (double)Stopwatch.Frequency;
                    Part(0);
                    groups++;
                    members += pending.samples.Length;
                    hulls += group.memberOfCollider.Count;
                    double total = pending.sums[0];
                    if (!(total > 0.0))
                    {
                        continue;
                    }

                    double3 centre = new double3(pending.sums[1], pending.sums[2], pending.sums[3]) / total;
                    double cc = math.dot(centre, centre);
                    var tensor = new SymmetricMatrix3
                    {
                        xx = pending.sums[4] - total * (cc - centre.x * centre.x), yy = pending.sums[5] - total * (cc - centre.y * centre.y), zz = pending.sums[6] - total * (cc - centre.z * centre.z),
                        xy = pending.sums[7] + total * centre.x * centre.y, xz = pending.sums[8] + total * centre.x * centre.z, yz = pending.sums[9] + total * centre.y * centre.z,
                    };
                    Transform root = group.Root.transform;
                    SymmetricMatrix3 localTensor;
                    if (pending.localFrame)
                    {
                        localTensor = tensor;   // summed in the root's own frame already
                    }
                    else
                    {
                        double3x3 rootR = new double3x3(new float3x3((quaternion)root.rotation));
                        double3x3 local = math.mul(math.mul(math.transpose(rootR), tensor.ToMatrix()), rootR);
                        localTensor = new SymmetricMatrix3 { xx = local.c0.x, yy = local.c1.y, zz = local.c2.z, xy = local.c1.x, xz = local.c2.x, yz = local.c2.y };
                    }

                    Vector3 centreLocal = pending.localFrame ? (Vector3)(float3)centre : root.InverseTransformPoint((Vector3)(float3)centre);
                    bool diagonal = PrincipalInertia.TryDiagonalize(in localTensor, out double3 moments, out quaternion rotation) && math.all(moments > 0.0);
                    Part(1);
                    Rigidbody body = group.Body;
                    body.automaticCenterOfMass = false;
                    body.automaticInertiaTensor = false;
                    Part(2);
                    body.mass = (float)total;
                    Part(3);
                    body.centerOfMass = centreLocal;
                    Part(4);
                    if (diagonal)
                    {
                        body.inertiaTensor = (Vector3)(float3)moments;
                        body.inertiaTensorRotation = rotation;
                    }

                    Part(5);
                    group.Mass = total;
                    if (pending.samples.Length > MaxMassMembers) MaxMassMembers = pending.samples.Length;
                    if (body.isKinematic)
                    {
                        continue;
                    }

                    double3 c2 = (float3)body.worldCenterOfMass;
                    if (pending.compose)
                    {
                        // A union's momentum: v = P / M, w = I^-1 (L - M c x v).
                        double3 v2 = pending.linear / total;
                        double3 w2 = math.mul(math.inverse(WorldInertia(body)), pending.angular - total * math.cross(c2, v2));
                        if (math.all(math.isfinite(v2)) && math.all(math.isfinite(w2)))
                        {
                            body.linearVelocity = (Vector3)(float3)v2;
                            body.angularVelocity = (Vector3)(float3)w2;
                        }

                        lastUnion.linearAfter = total * v2;
                        lastUnion.angularAfter = math.mul(WorldInertia(body), w2) + total * math.cross(c2, v2);
                        lastUnion.massAfter = total;
                    }
                    else if (pending.inherit)
                    {
                        // A side's inheritance of its group's motion at its own centre (DESIGN 7.2), and its separation push on top.
                        double3 v = pending.v0 + math.cross(pending.w0, c2 - pending.c0) + pending.push / total;
                        body.linearVelocity = (Vector3)(float3)v;
                        body.angularVelocity = (Vector3)(float3)pending.w0;
                    }
                    else if (pending.keepField)
                    {
                        // A source that kept some members keeps its rigid motion: the same field, read at its new centre.
                        body.linearVelocity = (Vector3)(float3)(pending.fieldV + math.cross(pending.fieldW, c2 - pending.fieldC));
                        body.angularVelocity = (Vector3)(float3)pending.fieldW;
                    }

                    Part(6);
                }
                finally
                {
                    mark = Stopwatch.GetTimestamp();
                    FreePending(pending);
                    Part(7);
                }
            }

            int count = _pendingMass.Count;
            _pendingMass.Clear();
            double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            for (int i = 0; i < parts.Length; i++) MassApplyPartSeconds[i] += parts[i];
            MassApplyEnds++;
            MassGroupsApplied += groups;
            MassMembersApplied += members;
            MassHullsApplied += hulls;
            if (seconds > MaxMassApplySeconds)
            {
                var line = new System.Text.StringBuilder();
                line.Append((seconds * 1000).ToString("F3")).Append(" ms: pending ").Append(count).Append(", groups applied ").Append(groups).Append(", members ").Append(members).Append(", member colliders ").Append(hulls).Append("; ms");
                for (int i = 0; i < parts.Length; i++) line.Append(' ').Append(MassApplyPartNames[i]).Append(' ').Append((parts[i] * 1000).ToString("F3"));
                MaxMassApplyBreakdown = line.ToString();
            }

            MassSeconds += seconds;
            MassApplySeconds += seconds;
            MaxMassApplySeconds = Math.Max(MaxMassApplySeconds, seconds);
            MassWaitSeconds += waited;
            MaxMassWaitSeconds = Math.Max(MaxMassWaitSeconds, waited);
            if (seconds > MaxMassSeconds) MaxMassSeconds = seconds;
            if (count > 0)
            {
                // The running mean of one group's apply (its wait included), for the reserve the budget holds back before a unit begins.
                _massApplyEstimate = 0.8 * _massApplyEstimate + 0.2 * (seconds / count);
            }
        }

        /// <summary>Tests only: applies what is pending now (as the end of a Step does).</summary>
        internal void CompleteMassNow() => CompleteMassJobs();

        /// <summary>Tests only: a group's mass summed from its members now (as a union's or a retirement's schedule does), applied by the next <see cref="CompleteMassNow"/> or Step end.</summary>
        internal void ScheduleMassForTest(FusedGroup group) => ScheduleMass(group, null, true);

        private void DisposePendingMass()
        {
            foreach (PendingMass pending in _pendingMass.Values) FreePending(pending);
            _pendingMass.Clear();
        }
    }
}
