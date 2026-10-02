using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The aggregation of a building's groups (2026-09-30, TL): after a cut, the sides are left to move; by a deadline
    /// in real playing time, or when a different Slash hits the building, the groups the earlier cuts left are brought
    /// to at most one resting group (kinematic: the building's) and one free group (dynamic: the parts still in the
    /// air, never absorbed into the resting one), and once the free group rests it merges into the building's as
    /// every held group does. While a building aggregates, a hit on it is held -- the building, the Slash and the world
    /// plane -- and cut after the aggregation, so that no new cut adds groups under the aggregation. The switch of a
    /// member between groups is Main work split over frames within the fusion's per-frame budget; the sums of a
    /// group's mass properties run in a Burst job; the free groups' union composes its velocity and angular velocity
    /// from momentum, and the union of parts that are apart moves them as one (an approximation, counted).
    /// </summary>
    public sealed partial class BuildingFusion
    {
        /// <summary>A hit held while its building aggregates: what the cut needs, applied after.</summary>
        private sealed class HeldRequest
        {
            public LogicalFragmentId fragment;   // the member hit (its group, if it still stands, is the one cut)
            public float4 planeWorld;
            public float positiveImpulse, negativeImpulse;
            public float3 renderAnchor;
            public long slashId;
            public double heldAt;   // real seconds
            public string reason;   // "aggregation" (routed at the completion), or why the group could not take it (routed again when it is free)
            public long planeId;   // the adopted plane's identity the hit carried
            public HitRecord hit;   // the hit's one record: its events and outcome go there
        }

        /// <summary>One building's aggregation state.</summary>
        private sealed class Building
        {
            public int key;
            public bool hasCut;
            public double lastCutAt;   // real seconds of the last group cut published
            public long lastCutSlashId;
            public int cutGeneration;   // one more at every group cut published
            public int aggregatedGeneration;   // the generation the last completed aggregation was for
            public bool aggregating;
            public double aggregatingSince;
            public string trigger;
            public int waitFrames;
            public FusedGroup freeTarget;   // the free groups' union target while aggregating
            public readonly List<HeldRequest> held = new List<HeldRequest>();
        }

        private readonly Dictionary<int, Building> _buildings = new Dictionary<int, Building>();
        private readonly List<FusedGroup> _ofKey = new List<FusedGroup>();

        /// <summary>The real playing time the deadline is measured in (it advances through frames without a physics step). Tests may substitute it.</summary>
        internal Func<double> realSeconds = () => Time.realtimeSinceStartupAsDouble;

        /// <summary>
        /// Tests only: the momentum of the last union step -- before: the target body's plus the source body's (the
        /// source as its one body, meaningful when the whole source moved); after: the united body's, from the members'
        /// composition. Linear, and angular about the world origin.
        /// </summary>
        internal struct UnionRecord
        {
            public double3 linearBefore, linearAfter, angularBefore, angularAfter;
            public int membersMoved;
            public bool wholeSource;
            public double massAfter;
        }

        internal UnionRecord lastUnion;

        /// <summary>
        /// Where one held hit was routed when its hold ended: into a cut path (a preparation of its own, an attachment,
        /// an inclusion -- <see cref="routed"/>), or refused there (dropped, no group, not accepted). This is the routing,
        /// not the result: whether the cut it went into was published is the hit's own <see cref="HitRecord.outcome"/>.
        /// </summary>
        public readonly struct HeldOutcome
        {
            public readonly long slashId;
            public readonly int fragment;   // the member the hit was on
            public readonly double waitSeconds;
            public readonly string outcome;   // "prepared by preparation N", "attached to preparation N", "included ...", "Dropped: ...", "NotAccepted: ...", per group tried, joined by " | "
            public readonly bool routed;   // into a cut path; false when refused at the routing

            public HeldOutcome(long slashId, int fragment, double waitSeconds, string outcome, bool routed)
            {
                this.slashId = slashId; this.fragment = fragment; this.waitSeconds = waitSeconds; this.outcome = outcome; this.routed = routed;
            }
        }

        private readonly List<HeldOutcome> _heldOutcomes = new List<HeldOutcome>();

        /// <summary>Every held hit's outcome, in the order they were processed.</summary>
        public IReadOnlyList<HeldOutcome> HeldOutcomes => _heldOutcomes;

        /// <summary>One crossed member's operation as the fusion issued and ended it: for a reconciliation against the ledger and the geometry.</summary>
        public readonly struct MemberOperation
        {
            public readonly CutOperationId operation;
            public readonly LogicalFragmentId source, positive, negative;
            public readonly string outcome;   // "Published" or "Failed: <why>"
            public readonly double physicsSeconds;
            public readonly int preparation;   // the preparation whose group cut crossed the member (a hit's record names the same one)
            public readonly bool hullRefused;   // failed because PhysX refused a convex of its cut (the cook's hull check), attributed to this operation

            public MemberOperation(CutOperationId operation, LogicalFragmentId source, LogicalFragmentId positive, LogicalFragmentId negative, string outcome, double physicsSeconds, int preparation, bool hullRefused)
            {
                this.operation = operation; this.source = source; this.positive = positive; this.negative = negative; this.outcome = outcome; this.physicsSeconds = physicsSeconds;
                this.preparation = preparation; this.hullRefused = hullRefused;
            }

            public bool Published => outcome == "Published";
        }

        private readonly List<MemberOperation> _memberOperations = new List<MemberOperation>();

        /// <summary>Every crossed member's operation the fusion issued, with how it ended (a Final published its two children, or it failed).</summary>
        public IReadOnlyList<MemberOperation> MemberOperations => _memberOperations;

        private string _lastRefusal;

        /// <summary>What a completed aggregation left: the resting and the free groups of the building at that moment, and the hits held for it.</summary>
        public readonly struct Completion
        {
            public readonly int building, resting, free, heldHits;
            public readonly double seconds;
            public readonly string trigger;

            public Completion(int building, int resting, int free, int heldHits, double seconds, string trigger)
            {
                this.building = building; this.resting = resting; this.free = free; this.heldHits = heldHits; this.seconds = seconds; this.trigger = trigger;
            }
        }

        private readonly List<Completion> _completions = new List<Completion>();

        /// <summary>Every completed aggregation, in order.</summary>
        public IReadOnlyList<Completion> Completions => _completions;

        // ---- the record ----

        public int AggregationsBegun { get; private set; }
        public int AggregationsByDeadline { get; private set; }
        public int AggregationsBySlash { get; private set; }
        public int AggregationsCompleted { get; private set; }
        public double MaxAggregationSeconds { get; private set; }   // real seconds from the beginning to the completion
        public double AggregationSeconds { get; private set; }
        public int AggregationWaitFrames { get; private set; }   // frames an aggregation waited for the Finals of cuts in progress
        public int HeldRequests { get; private set; }   // hits held (for an aggregation, a preparation or a cut in progress)
        public int HeldProcessed { get; private set; }   // holds ended: routed or refused
        public int HeldRouted { get; private set; }   // routed into a cut path: a preparation of its own, an attachment or an inclusion. NOT a publication count: a hit's publication or refusal is its HitRecord.outcome
        public int HeldRefused { get; private set; }   // refused at the routing: dropped (the member replaced by a Final), no group, not accepted
        public double MaxHeldWaitSeconds { get; private set; }
        public int FreeUnions { get; private set; }   // free groups joined into another free group: parts moving as one from then on (the approximation)
        public int UnionMembersMoved { get; private set; }
        public int BodiesMade { get; private set; }   // group Rigidbodies made
        public int BodiesDestroyed { get; private set; }
        public int BodiesReused { get; private set; }   // a union kept the target's body
        public int CollidersMade { get; private set; }   // child members' and shadows' colliders
        public int CollidersDestroyed { get; private set; }
        public int BuildingsAggregating
        {
            get { int n = 0; foreach (Building b in _buildings.Values) if (b.aggregating) n++; return n; }
        }

        /// <summary>Whether a building is aggregating right now, and since when (real seconds) and why.</summary>
        public bool IsAggregating(int buildingKey, out double since, out string trigger)
        {
            since = 0.0; trigger = null;
            if (!_buildings.TryGetValue(buildingKey, out Building b) || !b.aggregating) return false;
            since = b.aggregatingSince; trigger = b.trigger;
            return true;
        }

        /// <summary>The hits held for a building right now.</summary>
        public int HeldRequestsOf(int buildingKey) => _buildings.TryGetValue(buildingKey, out Building b) ? b.held.Count : 0;

        private Building BuildingOf(int key)
        {
            if (!_buildings.TryGetValue(key, out Building b))
            {
                b = new Building { key = key, lastCutSlashId = long.MinValue };
                _buildings[key] = b;
            }

            return b;
        }

        private void NoteCut(int key, long slashId)
        {
            Building b = BuildingOf(key);
            b.hasCut = true;
            b.lastCutAt = realSeconds();
            b.lastCutSlashId = slashId;
            b.cutGeneration++;
        }

        /// <summary>Whether cuts were published since the last aggregation completed: what a new aggregation would be for. Without one, the groups as they stand (a resting and a free one) are left alone.</summary>
        private static bool HasNewCuts(Building b) => b.cutGeneration != b.aggregatedGeneration;

        /// <summary>The groups of a building, into the scratch list.</summary>
        private List<FusedGroup> GroupsOf(int key)
        {
            _ofKey.Clear();
            foreach (FusedGroup g in _groups) if (g.Key == key && g.Root != null) _ofKey.Add(g);
            return _ofKey;
        }

        /// <summary>Whether the earlier cuts of a building left more than one group: what an aggregation brings together.</summary>
        private bool NeedsAggregation(int key) => GroupsOf(key).Count > 1;

        /// <summary>
        /// The gate a hit passes before its cut: held while the building aggregates; begins the aggregation (and is held)
        /// when it comes from a Slash other than the one that made the building's last cut and that cut's groups are
        /// still apart; otherwise the cut goes on. True when the hit was held.
        /// </summary>
        private bool HoldOrBegin(FusedGroup group, FusedGroup.Member hit, in ProvisionalCutAsk ask, HitRecord record)
        {
            Building b = BuildingOf(group.Key);
            if (!b.aggregating)
            {
                if (!b.hasCut || ask.slashId == b.lastCutSlashId || !HasNewCuts(b) || !NeedsAggregation(group.Key))
                {
                    return false;
                }

                BeginAggregation(b, "slash " + ask.slashId + " after slash " + b.lastCutSlashId);
                AggregationsBySlash++;
            }

            b.held.Add(new HeldRequest
            {
                fragment = ask.source, planeWorld = AdoptedPlaneWorld(hit, in ask), planeId = ask.adoptedPlaneId, positiveImpulse = ask.positiveSeparationImpulse,
                negativeImpulse = ask.negativeSeparationImpulse, renderAnchor = ask.renderAnchor, slashId = ask.slashId, heldAt = realSeconds(), reason = "aggregation", hit = record,
            });
            HeldRequests++;
            HitsHeld++;
            Note(record, "held", 0, "held: the building aggregates (" + b.trigger + ")");
            Record("hit held t " + _physicsSeconds().ToString("F3") + " building " + b.key + " slash " + ask.slashId + " member " + ask.source.value + " (" + b.held.Count + " held)");
            return true;
        }

        private void BeginAggregation(Building b, string trigger)
        {
            b.aggregating = true;
            b.aggregatingSince = realSeconds();
            b.trigger = trigger;
            b.waitFrames = 0;
            b.freeTarget = null;
            AggregationsBegun++;
            Record("aggregation begun t " + _physicsSeconds().ToString("F3") + " building " + b.key + ": " + trigger + ", groups " + GroupsOf(b.key).Count);
        }

        /// <summary>The deadline: a building whose last cut's groups are still apart after the settings' real seconds begins to aggregate.</summary>
        private void BeginByDeadline()
        {
            if (!(_settings.deadlineSeconds > 0.0))
            {
                return;
            }

            double now = realSeconds();
            foreach (Building b in _buildings.Values)
            {
                if (b.aggregating || !b.hasCut || !HasNewCuts(b) || now - b.lastCutAt < _settings.deadlineSeconds || !NeedsAggregation(b.key))
                {
                    continue;
                }

                BeginAggregation(b, "deadline " + _settings.deadlineSeconds.ToString("F2") + " s after the last cut");
                AggregationsByDeadline++;
            }
        }

        /// <summary>
        /// One turn of every aggregation, within the budget: waits for the Finals of the cuts in progress, lets the
        /// resting groups merge (the ordinary merge turn does that), joins the free groups into one, and completes --
        /// then the held hits are cut. Returns the budget used.
        /// </summary>
        private void AdvanceAggregations()
        {
            foreach (Building b in _buildings.Values)
            {
                if (!b.aggregating)
                {
                    continue;
                }

                List<FusedGroup> groups = GroupsOf(b.key);
                bool busy = false;
                int resting = 0, free = 0;
                FusedGroup largestFree = null;
                foreach (FusedGroup g in groups)
                {
                    if (g.Busy || g.Preparing > 0) busy = true;
                    else if (g.Kinematic) resting++;
                    else { free++; if (largestFree == null || g.MemberCount > largestFree.MemberCount) largestFree = g; }
                }

                if (busy)
                {
                    b.waitFrames++;
                    AggregationWaitFrames++;
                    continue;   // the Finals come on their own frames (with or without a physics step); the sides are not moved under a cut
                }

                if (free > 1)
                {
                    if (b.freeTarget == null || b.freeTarget.Root == null || b.freeTarget.Kinematic || b.freeTarget.Busy)
                    {
                        b.freeTarget = largestFree;   // the body kept: the largest free group's
                        BodiesReused++;
                    }

                    foreach (FusedGroup g in groups)
                    {
                        if (ReferenceEquals(g, b.freeTarget) || g.Kinematic || g.Busy || g.Preparing > 0 || g.MemberCount == 0) continue;
                        if (!MayStart()) return;
                        Unite(g, b.freeTarget);
                    }

                    continue;
                }

                if (resting > 1)
                {
                    continue;   // the resting groups merge in the ordinary merge turn, some members a frame
                }

                if (!MayStart())
                {
                    return;   // the completion cuts the held hits: its own units
                }

                Complete(b);
            }
        }

        /// <summary>The load fell (an aggregation completed, a group rested): a physics kept from stepping by a stale estimate is asked to measure again.</summary>
        internal static void AskCostReevaluation()
        {
            if (CutPhysicsStep.SkippedForCostInARow > 0)
            {
                CutPhysicsStep.RequestCostReevaluation();
            }
        }

        private void Complete(Building b)
        {
            double seconds = realSeconds() - b.aggregatingSince;
            b.aggregating = false;
            b.freeTarget = null;
            b.aggregatedGeneration = b.cutGeneration;   // this generation is done; the held hits below make the next
            AggregationsCompleted++;
            AskCostReevaluation();
            AggregationSeconds += seconds;
            MaxAggregationSeconds = Math.Max(MaxAggregationSeconds, seconds);
            List<FusedGroup> groups = GroupsOf(b.key);
            int resting = 0, free = 0;
            foreach (FusedGroup g in groups) { if (g.Kinematic) resting++; else free++; }
            Record("aggregation complete t " + _physicsSeconds().ToString("F3") + " building " + b.key + " after " + seconds.ToString("F3") + " s (" + b.waitFrames + " frames waiting for Finals): resting " + resting + ", free " + free + ", held hits " + b.held.Count);
            _completions.Add(new Completion(b.key, resting, free, b.held.Count, seconds, b.trigger));
            if (b.held.Count == 0)
            {
                return;
            }

            var held = new List<HeldRequest>(b.held);
            b.held.Clear();
            foreach (HeldRequest h in held)
            {
                CutHeld(b, h);
            }
        }

        /// <summary>
        /// A hit held for the aggregation, routed at its completion: against the group its member stands in (prepared,
        /// attached, included, or held again with its reason) when that member still stands; else -- the member was
        /// replaced by a Final of an earlier Slash -- prepared on every free group of the building the plane may cross
        /// (the held link goes with the first), and refused on record when no group is free. The gate is not passed again.
        /// </summary>
        private void CutHeld(Building b, HeldRequest h)
        {
            if (_registry.TryGet(h.fragment, out PhysicsFragmentOwner owner) && owner.IsFused && owner.Group.Root != null && owner.Group.Key == b.key)
            {
                RouteHeld(b, h, owner.Group);
                return;
            }

            MaxHeldWaitSeconds = Math.Max(MaxHeldWaitSeconds, realSeconds() - h.heldAt);
            bool any = false;
            var refusedNow = new System.Text.StringBuilder();
            foreach (FusedGroup group in new List<FusedGroup>(GroupsOf(b.key)))
            {
                string why = group.Busy ? "busy (being cut by an earlier hit)" : group.Preparing > 0 ? "preparing (an earlier hit's cut)" : group.MemberCount == 0 ? "empty" : null;
                if (why != null)
                {
                    if (refusedNow.Length > 0) refusedNow.Append(" | ");
                    refusedNow.Append("group ").Append(group.Root.name).Append(": ").Append(why);
                    continue;
                }

                var ask = new ProvisionalCutAsk { source = group.members[0].fragment, positiveSeparationImpulse = h.positiveImpulse, negativeSeparationImpulse = h.negativeImpulse, renderAnchor = h.renderAnchor, slashId = h.slashId, adoptedPlaneId = h.planeId, adoptedPlaneWorld = h.planeWorld };
                ProvisionalCutAcceptance acceptance = BeginPreparation(group, group.members[0], h.planeWorld, in ask, any ? null : h, any ? null : h.hit, out _);
                if (acceptance == ProvisionalCutAcceptance.Pending)
                {
                    if (!any)
                    {
                        HeldProcessed++;
                        HeldRouted++;
                        _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, "prepared by preparation " + _lastPreparationId + " (the member was replaced by a Final: the building's free groups; the result is the hit's record)", true));
                    }

                    any = true;
                }
                else
                {
                    if (refusedNow.Length > 0) refusedNow.Append(" | ");
                    refusedNow.Append("group ").Append(group.Root.name).Append(": ").Append(acceptance).Append(_lastRefusal != null ? ": " + _lastRefusal : "");
                }
            }

            if (!any)
            {
                HeldProcessed++;
                HeldRefused++;
                string outcome = "Dropped: the member was replaced by a Final; " + (refusedNow.Length > 0 ? refusedNow.ToString() : "no group to cut");
                _heldOutcomes.Add(new HeldOutcome(h.slashId, h.fragment.value, realSeconds() - h.heldAt, outcome, false));
                Note(h.hit, "dropped", 0, outcome);
                SetOutcome(h.hit, outcome, false);
                Record("held hit refused t " + _physicsSeconds().ToString("F3") + " building " + b.key + " slash " + h.slashId + ": " + outcome);
            }
        }

        // ---- the union of free groups ----

        /// <summary>
        /// Moves up to <paramref name="budget"/> members of a free group into the free target: the target keeps its body
        /// and takes their momentum (linear, and angular about its new centre), the source keeps its motion for the members
        /// it still has, and an emptied source goes. Parts that were apart move as one from here (counted).
        /// </summary>
        private void Unite(FusedGroup source, FusedGroup target)
        {
            if (source.Body == null || target.Body == null)
            {
                return;
            }

            BeginPart();
            Rigidbody sb = source.Body, tb = target.Body;
            double3 vS = (float3)sb.linearVelocity, wS = (float3)sb.angularVelocity, cS = (float3)sb.worldCenterOfMass;
            double3 vT = (float3)tb.linearVelocity, wT = (float3)tb.angularVelocity, cT = (float3)tb.worldCenterOfMass;
            double mT = tb.mass, mS = sb.mass;
            // The target's momentum now, or what an earlier batch of this Step already composed for it (its apply is pending).
            double3 linear, angular;
            if (_pendingMass.TryGetValue(target, out PendingMass earlier) && earlier.compose)
            {
                linear = earlier.linear; angular = earlier.angular;
            }
            else
            {
                linear = mT * vT;
                angular = math.mul(WorldInertia(tb), wT) + mT * math.cross(cT, vT);
            }

            lastUnion = default;
            lastUnion.linearBefore = linear + mS * vS;
            lastUnion.angularBefore = angular + math.mul(WorldInertia(sb), wS) + mS * math.cross(cS, vS);
            if (source.MemberCount > 0 && FreeUnionsBegun(source)) FreeUnions++;

            int moved = 0;
            while (source.members.Count > 0)
            {
                if (moved > 0 && !MayStart())
                {
                    break;   // the batch ends here; what moved is made whole below
                }

                FusedGroup.Member m = source.members[source.members.Count - 1];
                if (m.owner.Root == null) { RemoveMember(source, m); continue; }
                double3 c = (float3)m.owner.Root.transform.TransformPoint(m.centreLocal);
                double3 v = vS + math.cross(wS, c - cS);
                double3x3 r = new double3x3(new float3x3(math.mul((quaternion)m.owner.Root.transform.rotation, m.inertiaRotation)));
                double3x3 iw = math.mul(math.mul(r, new double3x3(m.inertia.x, 0, 0, 0, m.inertia.y, 0, 0, 0, m.inertia.z)), math.transpose(r));
                linear += m.mass * v;
                angular += math.mul(iw, wS) + m.mass * math.cross(c, v);
                RemoveMember(source, m);
                m.owner.Root.transform.SetParent(target.Root.transform, true);
                m.owner.FuseInto(target, m.mass);
                AddMember(target, m);
                moved++;
            }

            lastUnion.membersMoved = moved;
            UnionMembersMoved += moved;
            // The target's mass properties for what it holds now, then its motion from the momentum (v = P / M, w = I^-1 (L - M c x v)): at the Step's end.
            PendingMass pending = ScheduleMass(target, null, true);
            if (pending != null)
            {
                pending.compose = true;
                pending.linear = linear;
                pending.angular = angular;
            }

            lastUnion.wholeSource = source.members.Count == 0;
            _rest.TrackGroupColliders(tb, target.Root);
            if (source.members.Count == 0)
            {
                DropGroup(source, "united into a free group");   // its body goes (the rest forgets it; what stood on it is judged again)
                Record("free groups united t " + _physicsSeconds().ToString("F3") + " building " + target.Key + ": " + moved + " members into the group of " + target.MemberCount);
            }
            else
            {
                // The source keeps its rigid motion for the members it still has: its centre moves, its velocity field does not.
                PendingMass kept = ScheduleMass(source, null, true);
                if (kept != null)
                {
                    kept.keepField = true;
                    kept.fieldV = vS; kept.fieldW = wS; kept.fieldC = cS;
                }

                _rest.TrackGroupColliders(sb, source.Root);
            }

            double seconds = EndPart();
            UnionSeconds += seconds;
            MaxUnionSeconds = Math.Max(MaxUnionSeconds, seconds);
        }

        public double UnionSeconds { get; private set; }
        public double MaxUnionSeconds { get; private set; }

        private readonly HashSet<FusedGroup> _unionsBegun = new HashSet<FusedGroup>();

        private bool FreeUnionsBegun(FusedGroup source) => _unionsBegun.Add(source);

        private static double3x3 WorldInertia(Rigidbody body)
        {
            double3x3 r = new double3x3(new float3x3(math.mul((quaternion)body.rotation, (quaternion)body.inertiaTensorRotation)));
            Vector3 i = body.inertiaTensor;
            return math.mul(math.mul(r, new double3x3(i.x, 0, 0, 0, i.y, 0, 0, 0, i.z)), math.transpose(r));
        }

        // ---- the mass sums, in a Burst job ----

        /// <summary>One member's (or a crossed member's side's) contribution: its mass, its centre in the world, its principal inertia and their world orientation.</summary>
        private struct MassSample
        {
            public double mass;
            public double3 centre;
            public double3 inertia;
            public quaternion axes;
        }

        /// <summary>The sums of the mass properties about the world origin: the total, the weighted centre, and the tensor (I of each about its own centre, plus its mass about the origin).</summary>
        [BurstCompile]
        private struct FusedMassJob : IJob
        {
            [ReadOnly] public NativeArray<MassSample> samples;
            public NativeArray<double> sums;   // total, weighted xyz, xx yy zz xy xz yz

            public void Execute()
            {
                double total = 0.0;
                double3 weighted = 0.0;
                double xx = 0, yy = 0, zz = 0, xy = 0, xz = 0, yz = 0;
                for (int i = 0; i < samples.Length; i++)
                {
                    MassSample s = samples[i];
                    total += s.mass;
                    weighted += s.centre * s.mass;
                    double3x3 r = new double3x3(new float3x3(s.axes));
                    double3x3 w = math.mul(math.mul(r, new double3x3(s.inertia.x, 0, 0, 0, s.inertia.y, 0, 0, 0, s.inertia.z)), math.transpose(r));
                    double3 c = s.centre;
                    double cc = math.dot(c, c);
                    xx += w.c0.x + s.mass * (cc - c.x * c.x); yy += w.c1.y + s.mass * (cc - c.y * c.y); zz += w.c2.z + s.mass * (cc - c.z * c.z);
                    xy += w.c1.x - s.mass * c.x * c.y; xz += w.c2.x - s.mass * c.x * c.z; yz += w.c2.y - s.mass * c.y * c.z;
                }

                sums[0] = total; sums[1] = weighted.x; sums[2] = weighted.y; sums[3] = weighted.z;
                sums[4] = xx; sums[5] = yy; sums[6] = zz; sums[7] = xy; sums[8] = xz; sums[9] = yz;
            }
        }

    }
}
