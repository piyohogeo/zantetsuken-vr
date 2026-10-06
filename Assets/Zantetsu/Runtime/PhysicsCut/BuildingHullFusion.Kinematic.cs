using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The always-kinematic mode (TL, 2026-09-30; an Editor prototype, off unless the profile asks for it). A building
    /// is one kinematic body, one hull and one collider for good -- its group is the building's stable target: the Slashes
    /// it took are reserved on it, and it is never split, never published as physics children, never held or released by
    /// the rest. A hit cuts the display alone, through the ordinary path (the members' display vertices classified, the
    /// DAG's admission, display-only owners, the commit), and the upper side's members drop by a short fixed animation;
    /// the hit's outcome is the display's. The hull follows best-effort, off Main: the old hull cut by the plane, the
    /// dropped side moved, one hull of both built and baked, and exchanged only when it succeeds on the generation it was
    /// made from -- else the old hull stays. Nothing of a candidate is a body or a collider before its exchange.
    /// <para>
    /// **The slide's direction** (TL, 2026-09-30; independent of the normal's sign, fixed at the cut): gravity g projected
    /// onto the cut plane, t = g - (g.n) n, when it is not shorter than sin 5 deg (the plane is within 5 deg of horizontal
    /// otherwise); else the sweep's travel direction projected onto the plane, when not shorter than 0.1; else world +X
    /// projected onto the plane, or +Z when X is within 0.1 of the normal. Never straight down in the world. The slide is
    /// in the plane, so the dropping side's move d satisfies d.n = 0; the display and the hull's candidate get the same d.
    /// </para>
    /// <para>
    /// **Which side slides** (independent of the normal's sign): with the world normal n, c = n.y. When |c| is at least
    /// cos 80 deg, the side the upward normal (n * sign c) points to drops. Nearer vertical: the side away from the
    /// building's anchors when they stand on one side only; else the side with fewer display members; else, even, the
    /// side of the canonical normal (its largest component made positive). **How far**: the vertical distance + (the
    /// horizontal's - the vertical's) x |c|, over the animation time, along the slide's direction in the plane.
    /// </para>
    /// <para>
    /// **The drop's path** (TL, 2026-10-01): with x0 the member's start, d the slide in the plane above (D(n) with the cut
    /// limit), g the game's gravity at the start and T the animation's time, both fixed at the start, the member stands at
    /// x(t) = x0 + (t / T) d + 1/2 g t (t - T), t the seconds since the start clamped to [0, T], computed from the time each
    /// Step (nothing integrated); at T it is x0 + d. The arc is the world's vertical laid over the slide (about 7.7 cm at
    /// the middle for 9.81 m/s2 and 0.25 s, never scaled by D(n)); rotations do not change. The hull's update is given d.
    /// </para>
    /// <para>
    /// **A re-cut** stops every drop of the building where it is on its curve (the pose it has then is kept, its height
    /// included: not back to its start, not on to its end, nothing of its time, speed or arc handed on) and is classified
    /// from that pose; its own drop starts afresh from there.
    /// </para>
    /// <para>
    /// **The hull's generation and the requests** (TL, 2026-09-30, best-effort): each building has one hull update running
    /// at most and one request waiting at most -- the latest cut's. A newer cut's request replaces the one waiting (the
    /// replaced one is counted as skipped; nothing of the display, its drop or the hit is undone). An update is made from
    /// the building's hull of its generation then and exchanged only if the hull is still of that generation. A stale or
    /// cancelled update's request waits again only when nothing newer waits -- never in front of a newer one (else it is
    /// skipped too); a refused update (the builder, the vertex limit, PhysX) is dropped and the old hull stays. The work
    /// left is thus one running and one waiting a building, whatever the number of cuts. The display never waits for it.
    /// </para>
    /// </summary>
    public sealed partial class BuildingHullFusion
    {
        /// <summary>The near-vertical boundary of the drop's side rule: |n.up| under cos 80 deg.</summary>
        internal const float NearVerticalCos = 0.17364818f;

        /// <summary>The near-horizontal boundary of the slide's direction: gravity's projection onto the plane shorter than sin 5 deg.</summary>
        internal const float NearHorizontalSin = 0.08715574f;

        /// <summary>The slide's direction in the cut plane by the rule above (a unit vector, d.n = 0), and the rule's case for the record.</summary>
        internal static float3 SlideDirection(float3 normalWorld, float3 travelWorld, out string rule)
        {
            float3 n = math.normalize(normalWorld);
            float3 g = new float3(0f, -1f, 0f);
            float3 t = g - math.dot(g, n) * n;
            if (math.length(t) >= NearHorizontalSin) { rule = "down the plane"; return math.normalize(t); }
            float3 travel = math.lengthsq(travelWorld) > 0f ? math.normalize(travelWorld) : float3.zero;
            float3 along = travel - math.dot(travel, n) * n;
            if (math.length(along) >= 0.1f) { rule = "near horizontal: the sweep's travel along the plane"; return math.normalize(along); }
            float3 x = new float3(1f, 0f, 0f) - n.x * n;
            if (math.length(x) >= 0.1f) { rule = "near horizontal, no travel: world +X along the plane"; return math.normalize(x); }
            float3 z = new float3(0f, 0f, 1f) - n.z * n;
            rule = "near horizontal, no travel: world +Z along the plane";
            return math.normalize(z);
        }

        private struct HullOp
        {
            public float4 plane;   // the group's frame
            public bool movingPositive;
            public float3 displacement;   // the group's frame
        }

        private sealed class DisplayAnimation
        {
            public HullGroup group;
            public readonly List<(HullGroup.DisplayMember member, Vector3 from)> items = new List<(HullGroup.DisplayMember, Vector3)>();
            public Vector3 delta;   // the group Root's local space
            public Vector3 gravity;   // the game's gravity at the start, in the group Root's local space (fixed for the drop)
            public double start;
            public double seconds;   // T, fixed at the start
            public DropRecord record;
        }

        /// <summary>
        /// One drop as it was given and as far as it went (TL, 2026-10-01), for the checks: the hit, d, g and T fixed at its
        /// start, the time last applied, and how it ended. Its members' start positions and rotations are kept.
        /// </summary>
        public sealed class DropRecord
        {
            public int id, hit, group, building;
            public Vector3 deltaLocal, gravityLocal;   // the group Root's local space
            public float3 deltaWorld, gravityWorld, normalWorld;
            public double seconds, start;   // T; the clock's value at the start
            public double appliedSeconds;   // t of the last placement, in [0, T]
            public int placements;
            public string end;   // null while it runs; "completed", "stopped" (by a re-cut) or "cleared" (the world's end)
            public int stoppedByHit;   // the re-cut's hit that stopped it (0 unless stopped)
            public Quaternion bodyRotation;   // the group's body at the start
            public readonly List<DropMember> members = new List<DropMember>();
            public double Phase => seconds > 0.0 ? appliedSeconds / seconds : 1.0;   // u = t / T of the last placement
        }

        public sealed class DropMember
        {
            public LogicalFragmentId fragment;
            public Transform root;
            public Vector3 from;   // local, at the start
            public Quaternion rotation;   // local, at the start
            public Vector3 at;   // local, read back at the drop's end (completed or stopped)
            public bool atRead;
        }

        /// <summary>
        /// The drop's position at t seconds of T: x(t) = x0 + (t / T) d + 1/2 g t (t - T), t clamped to [0, T]; at T exactly
        /// x0 + d. The slide in the plane and the arc of the game's gravity over it; nothing integrated.
        /// </summary>
        public static Vector3 DropPosition(Vector3 from, Vector3 delta, Vector3 gravity, double seconds, double t)
        {
            if (!(seconds > 0.0) || t >= seconds) return from + delta;
            if (!(t > 0.0)) return from;
            return from + delta * (float)(t / seconds) + gravity * (float)(0.5 * t * (t - seconds));
        }

        private readonly List<DropRecord> _dropRecords = new List<DropRecord>();

        /// <summary>Every drop given, in order, as far as it went.</summary>
        public IReadOnlyList<DropRecord> DropRecords => _dropRecords;

        private sealed class HullUpdateWork : IDispatchWork
        {
            public HullGroup group;
            public int generation;
            public readonly List<HullOp> ops = new List<HullOp>();
            public float3[] vertices; public int[] faceOffsets, faceIndices;   // in: the hull of the generation; out: the candidate
            public int vertexLimit;
            public string reason;
            public HullBuildReport report;
            public bool scanning = true;
            public Mesh mesh;
            public int meshId;
            public MeshColliderCookingOptions cooking;
            public bool offered, collected, cancelled, abandoned;
            public WorkTicket ticket;
            private int _done, _begun;
            public double scanSeconds, bakeSeconds;
            public bool Done => Volatile.Read(ref _done) == 1;
            public bool Begun => Volatile.Read(ref _begun) == 1;
            public bool IsComplete => Done;

            public void Begin()
            {
                Volatile.Write(ref _begun, 1);
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    if (scanning)
                    {
                        foreach (HullOp op in ops)
                        {
                            List<float3> points = CutAndMove(vertices, faceOffsets, faceIndices, op);
                            if (!ConvexHullBuilder.TryBuild(points, vertexLimit, out float3[] v, out int[] fo, out int[] fi, out report)) { reason = report.reason; vertices = null; return; }
                            vertices = v; faceOffsets = fo; faceIndices = fi;
                        }
                    }
                    else
                    {
                        Physics.BakeMesh(meshId, true, cooking);   // any thread
                    }
                }
                catch (Exception e)
                {
                    reason = e.GetType().Name + ": " + e.Message;
                    vertices = null;
                }
                finally
                {
                    double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    if (scanning) scanSeconds = seconds; else bakeSeconds = seconds;
                    Volatile.Write(ref _done, 1);
                }
            }

            public void Collect(WorkCompletion completion)
            {
                collected = true;
                cancelled = completion.outcome == WorkOutcome.Cancelled;
                if (abandoned && mesh != null) { PhysicsCutCook.DestroyMesh(mesh); mesh = null; }
            }

            public void ResetForBake()
            {
                scanning = false;
                offered = collected = cancelled = false;
                ticket = default;
                Volatile.Write(ref _done, 0);
                Volatile.Write(ref _begun, 0);
            }
        }

        private readonly List<DisplayAnimation> _animations = new List<DisplayAnimation>();
        private readonly List<HullUpdateWork> _hullWorks = new List<HullUpdateWork>();
        private readonly Dictionary<HullGroup, HullOp> _waitingRequest = new Dictionary<HullGroup, HullOp>();
        private readonly List<string> _hullUpdateRecords = new List<string>();

        public int AnimationsStarted { get; private set; }
        public int AnimationsCompleted { get; private set; }
        public int AnimationsStoppedByReCut { get; private set; }
        public int AnimationsRunning => _animations.Count;
        /// <summary>How far along its way the last drop stopped by a re-cut was (0..1), or NaN before any.</summary>
        public double LastStopFraction { get; private set; } = double.NaN;
        public int SlideMembersMeasured { get; private set; }   // at each drop's end (completed or stopped): its members' world moves read from their transforms
        public double MaxDropPositionError { get; private set; }   // the largest distance (m) of a member read back from the formula at the time last applied
        public double MaxDropRotationDegrees { get; private set; }   // the largest rotation of a member or the body since its drop began
        public double AnimationSeconds { get; private set; }   // Main: the members' placement updates of the drops
        public int HullUpdatesBegun { get; private set; }
        public int HullUpdatesAdopted { get; private set; }
        public int HullUpdatesRefused { get; private set; }
        public int HullUpdatesStale { get; private set; }
        public int HullUpdatesCancelled { get; private set; }
        public int HullRequestsMade { get; private set; }   // one a display cut
        public int HullRequestsSkipped { get; private set; }   // replaced by a newer one while waiting, or a stale/cancelled one with a newer one waiting
        public int HullRequestsDropped { get; private set; }   // their update refused: the old hull stayed
        /// <summary>Hull updates running now (made, offered, not yet exchanged or dropped): one a building at most.</summary>
        public int HullUpdatesInFlight => _hullWorks.Count;
        /// <summary>Requests waiting now: one a building at most (the latest).</summary>
        public int WaitingHullRequests => _waitingRequest.Count;
        public int MaxHullUpdatesInFlight { get; private set; }
        public int MaxWaitingHullRequests { get; private set; }
        public double HullUpdateMainSeconds { get; private set; }
        public double HullUpdateWorkerSeconds { get; private set; }
        public IReadOnlyList<string> HullUpdateRecords => _hullUpdateRecords;

        /// <summary>Tests only: the next hull update's snapshot is made stale at once (the group's hull generation moved), so its exchange drops it.</summary>
        internal bool bumpHullGenerationForTest;

        /// <summary>
        /// The display members now: held; of them, with their geometry committed, known empty (an empty side), and neither
        /// yet (a child whose geometry is still being made); and the ledger's history (fragments, operations) -- its own
        /// counts, not a walk of it.
        /// </summary>
        public void CountDisplay(out int held, out int committed, out int empty, out int pending, out int ledgerFragments, out int ledgerOperations)
        {
            held = 0; committed = 0; empty = 0; pending = 0;
            foreach (HullGroup g in _groups)
            {
                if (g.State == HullGroupState.Gone) continue;
                foreach (HullGroup.DisplayMember m in g.Members)
                {
                    held++;
                    if (_dag.HasNoGeometry(m.fragment)) empty++;
                    else if (_dag.TryGetGeometryFrame(m.fragment, out Matrix4x4 _)) committed++;
                    else pending++;
                }
            }

            ledgerFragments = _ledger.FragmentCount;
            ledgerOperations = _ledger.OperationCount;
        }

        /// <summary>Which side of a cut drops, and how far, by the rule above; the rule's case for the record.</summary>
        internal static void ChooseDrop(float3 normalWorld, int anchorsPositive, int anchorsNegative, int membersPositive, int membersNegative, double dropHorizontal, double dropVertical, out bool movingPositive, out float distance, out string rule)
        {
            float3 n = math.normalize(normalWorld);
            float c = n.y;
            distance = (float)(dropVertical + (dropHorizontal - dropVertical) * math.abs(c));
            if (math.abs(c) >= NearVerticalCos) { movingPositive = c > 0f; rule = "the side above"; return; }
            if ((anchorsPositive > 0) != (anchorsNegative > 0)) { movingPositive = anchorsNegative > 0; rule = "near vertical: the side away from the anchors"; return; }
            if (membersPositive != membersNegative) { movingPositive = membersPositive < membersNegative; rule = "near vertical: the side with fewer display members"; return; }
            float3 a = math.abs(n);
            float major = a.x >= a.y && a.x >= a.z ? n.x : a.y >= a.z ? n.y : n.z;
            movingPositive = major > 0f;
            rule = "near vertical, even: the canonical normal's side";
        }

        /// <summary>
        /// The display alone publishes the cut (the always-kinematic mode): the crossed members' cuts admitted and published
        /// (both children under the group's Root), the dropping side's members animated, the hull's update asked for. Short
        /// of ledger room the cut waits, nothing changed; a member gone or under another cut refuses it, nothing changed.
        /// </summary>
        private PublishOutcome PublishDisplayOnly(PendingCut cut)
        {
            HullGroup group = cut.group;
            long begin = Stopwatch.GetTimestamp();
            var crossed = new List<MemberSide>();
            foreach (MemberSide s in cut.sides) if (s.side == 0) crossed.Add(s);
            foreach (MemberSide s in crossed)
            {
                if (!_ledger.IsCurrentTarget(s.member.fragment)) { EndWithoutCut(cut, "Refused: display member " + s.member.fragment.value + " is not live (nothing changed)"); DisplayCutsRefused++; return PublishOutcome.Failed; }
                if (_ledger.TryGetActiveOperation(s.member.fragment, out _)) { EndWithoutCut(cut, "Refused: display member " + s.member.fragment.value + " is under another cut (nothing changed)"); DisplayCutsRefused++; return PublishOutcome.Failed; }
            }

            if (crossed.Count > _ledger.Budget.MaxIncompleteCutOperationCount) { EndWithoutCut(cut, "Refused: " + crossed.Count + " display cuts, the ledger's capacity " + _ledger.Budget.MaxIncompleteCutOperationCount + " (nothing changed)"); DisplayCutsRefused++; return PublishOutcome.Failed; }
            if (crossed.Count > _ledger.Budget.MaxIncompleteCutOperationCount - _ledger.Budget.IncompleteCutOperationCount)
            {
                if (cut.roomWaits == 0) Record("hit " + cut.hit.id + " waits: " + crossed.Count + " display cuts, ledger room " + (_ledger.Budget.MaxIncompleteCutOperationCount - _ledger.Budget.IncompleteCutOperationCount));
                return PublishOutcome.WaitingForRoom;
            }

            // Which side drops, and how far.
            int anchorsPositive = 0, anchorsNegative = 0, membersPositive = 0, membersNegative = 0;
            foreach (float3 a in group.Anchors) { float s = math.dot(cut.planeGroup.xyz, a) + cut.planeGroup.w; if (s > _anchorEpsilon) anchorsPositive++; else if (s < -_anchorEpsilon) anchorsNegative++; }
            foreach (MemberSide s in cut.sides) { if (s.side == 1 || s.side == 0) membersPositive++; if (s.side == -1 || s.side == 0) membersNegative++; }
            ChooseDrop(cut.planeWorld.xyz, anchorsPositive, anchorsNegative, membersPositive, membersNegative, _settings.dropHorizontalMetres, _settings.dropVerticalMetres, out bool movingPositive, out float distance, out string rule);
            if (LimitOn)
            {
                // The cut limit's drop, D(n) from the n the cut went on from, in place of the horizontal / vertical one (the side's rule kept).
                distance = (float)cut.hit.limitDistance;
                rule += "; D(n) for n = " + cut.hit.geometriesBefore;
            }
            float3 slide = SlideDirection(cut.planeWorld.xyz, cut.hit.travelWorld, out string slideRule);
            cut.hit.slideRule = slideRule;
            cut.hit.slideWorld = slide * distance;
            cut.hit.normalWorld = math.normalize(cut.planeWorld.xyz);
            cut.hit.membersClassified = cut.sides.Count;
            Transform root = group.Root.transform;
            Vector3 dropLocal = root.InverseTransformVector((Vector3)(slide * distance));   // the display's and the hull's same move, in the plane

            // A building drawn by its own renderers until now: its display registered here, just before the display cut that
            // takes the drawing over (TL, 2026-10-03). Refused or thrown: the cut ends here, nothing changed, and the group
            // is taken back out at the Step's end.
            if (group.Handover != null)
            {
                bool shown;
                try
                {
                    shown = group.Handover.show != null && group.Handover.show();
                }
                catch (Exception e)
                {
                    shown = false;
                    Record("hit " + cut.hit.id + ": the building's display registration threw: " + e.Message);
                }

                if (!shown)
                {
                    EndWithoutCut(cut, "Refused: the display did not take the building at its first cut (nothing changed)");
                    DisplayCutsRefused++;
                    return PublishOutcome.Failed;
                }
            }

            // The display cuts admitted (judged possible above on this same state), then published: both children under the group's Root.
            var admitted = new Dictionary<MemberSide, CutOperationId>();
            foreach (MemberSide s in crossed)
            {
                LogicalCutAdmission admission = _dag.TryAdmit(s.member.fragment, s.planeMember, true, out CutOperationId operation);
                if (admission != LogicalCutAdmission.Admitted) throw new InvalidOperationException("the ledger answered " + admission + " for display member " + s.member.fragment.value + " whose admission had just been judged possible");
                if (_ledger.PrepareAnchorDistribution(operation, _anchorEpsilon, out _) != AnchorPreparationOutcome.Prepared) throw new InvalidOperationException("the anchors of display member " + s.member.fragment.value + " could not be prepared");
                admitted[s] = operation;
            }

            var members = new List<HullGroup.DisplayMember>(group.Members);
            group.Members.Clear();
            var dropping = new List<HullGroup.DisplayMember>();
            for (int i = 0; i < members.Count; i++)
            {
                HullGroup.DisplayMember m = members[i];
                MemberSide known = i < cut.sides.Count && ReferenceEquals(cut.sides[i].member, m) ? cut.sides[i] : null;
                SideLookups++;
                if (known == null) throw new InvalidOperationException("the display members of group " + group.Id + " changed while it was cut: member " + i + " is not the one classified");
                if (known.side == 0 && admitted.TryGetValue(known, out CutOperationId operation))
                {
                    PublishDisplayChildren(group, known, operation, cut.hit, out HullGroup.DisplayMember positiveChild, out HullGroup.DisplayMember negativeChild);
                    dropping.Add(movingPositive ? positiveChild : negativeChild);
                    continue;
                }

                group.Members.Add(m);
                if ((known.side == 1 && movingPositive) || (known.side == -1 && !movingPositive)) dropping.Add(m);
            }

            StartAnimation(group, dropping, dropLocal, cut.hit);
            if (LimitOn) _limitAwaiting[group.Building] = cut.hit;   // its n after is counted once its display operations settle
            bool replaced = _waitingRequest.ContainsKey(group);
            if (replaced) HullRequestsSkipped++;   // the older request waiting is skipped: the latest cut's stands
            _waitingRequest[group] = new HullOp { plane = cut.planeGroup, movingPositive = movingPositive, displacement = dropLocal };
            HullRequestsMade++;
            MaxWaitingHullRequests = Math.Max(MaxWaitingHullRequests, _waitingRequest.Count);
            group.State = HullGroupState.Idle;
            double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            PublishSeconds += seconds;
            PublishDisplaySeconds += seconds;
            if (GroupCuts == 0) FirstPublishSeconds = seconds; else MaxPublishSecondsAfterFirst = Math.Max(MaxPublishSecondsAfterFirst, seconds);
            MaxPublishSeconds = Math.Max(MaxPublishSeconds, seconds);
            GroupCuts++;
            cut.hit.positiveGroup = cut.hit.negativeGroup = group.Id;
            cut.hit.membersHeldAfter = group.MemberCount;
            cut.hit.publishMs = seconds * 1000;
            Finish(cut.hit, "Published", true);
            if (group.Handover != null)
            {
                // The display has the cut from here: the building's own drawing and colliders leave, once.
                HullGroup.FirstCutHandover handover = group.Handover;
                group.Handover = null;
                HandedOver++;
                handover.handedOver?.Invoke();
            }

            cut.hit.hullOutcome = "the hull's update asked for (" + (_hullWorks.Exists(w => ReferenceEquals(w.group, group)) ? "one running: this one waits" : "the next update") + (replaced ? "; the request waiting before it skipped" : "") + ")";
            Record("hit " + cut.hit.id + " published by the display t " + _physicsSeconds().ToString("F3") + ": group " + group.Id + ", display cuts " + admitted.Count + ", members dropping " + dropping.Count + " (" + (movingPositive ? "+" : "-") + " side, " + rule + "; " + distance.ToString("F4") + " m " + slideRule + " " + ((Vector3)slide).ToString("F3") + " over " + _settings.animationSeconds.ToString("F2") + " s); members held " + group.MemberCount + "; " + (seconds * 1000).ToString("F3") + " ms");
            return PublishOutcome.Published;
        }

        /// <summary>A display member's admitted cut published: its two children display-only owners under the group's Root at the member's pose now; the source retired.</summary>
        private void PublishDisplayChildren(HullGroup group, MemberSide side, CutOperationId operation, HullHit hit, out HullGroup.DisplayMember positiveMember, out HullGroup.DisplayMember negativeMember)
        {
            HullGroup.DisplayMember m = side.member;
            if (_ledger.PreparePublication(operation) != LogicalCutResultOutcome.Applied || _ledger.Publish(operation, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild) != LogicalCutResultOutcome.Applied)
            {
                throw new InvalidOperationException("the ledger refused the publication of display member " + m.fragment.value + " whose admission it had just given");
            }

            _registry.TryGet(m.fragment, out PhysicsFragmentOwner sourceOwner);
            BuildingLineage lineage = sourceOwner != null ? sourceOwner.Building.ChildOfSplit() : BuildingLineage.RegisteredBuilding.ChildOfSplit();
            Transform at = m.root.transform;
            positiveMember = new HullGroup.DisplayMember { fragment = positiveChild, root = NewMemberRoot(group.Root.transform, at, "Display member " + positiveChild.value) };
            negativeMember = new HullGroup.DisplayMember { fragment = negativeChild, root = NewMemberRoot(group.Root.transform, at, "Display member " + negativeChild.value) };
            _registry.Reserve(2);
            _registry.Add(positiveChild, PhysicsFragmentOwner.DisplayOnly(positiveMember.root, Matrix4x4.identity, lineage));
            _registry.Add(negativeChild, PhysicsFragmentOwner.DisplayOnly(negativeMember.root, Matrix4x4.identity, lineage));
            group.Members.Add(positiveMember);
            group.Members.Add(negativeMember);
            _registry.Retire(m.fragment);
            m.root = null;
            hit.displayOperations.Add(operation);
            _displayOperations.Add(operation);
            NoteDisplayOperation(group.Building, operation);
            DisplayCuts++;
        }

        private void StartAnimation(HullGroup group, List<HullGroup.DisplayMember> members, Vector3 deltaLocal, HullHit hit)
        {
            if (members.Count == 0) return;
            Transform root = group.Root.transform;
            Vector3 gravityWorld = Physics.gravity;   // the game's gravity at the start, fixed for the drop
            var record = new DropRecord
            {
                id = _dropRecords.Count + 1, hit = hit.id, group = group.Id, building = group.Building, deltaLocal = deltaLocal, gravityLocal = root.InverseTransformVector(gravityWorld),
                deltaWorld = hit.slideWorld, gravityWorld = gravityWorld, normalWorld = hit.normalWorld, seconds = _settings.animationSeconds, start = _realSeconds(),
                bodyRotation = group.Body != null ? group.Body.rotation : Quaternion.identity,
            };
            var a = new DisplayAnimation { group = group, delta = deltaLocal, gravity = record.gravityLocal, start = record.start, seconds = record.seconds, record = record };
            foreach (HullGroup.DisplayMember m in members)
            {
                if (m.root == null) continue;
                a.items.Add((m, m.root.transform.localPosition));
                record.members.Add(new DropMember { fragment = m.fragment, root = m.root.transform, from = m.root.transform.localPosition, rotation = m.root.transform.localRotation });
            }

            _dropRecords.Add(record);
            AnimationsStarted++;
            _animations.Add(a);   // where it is until the next physics step; from then on placed with every step
        }

        /// <summary>The drop's time now: seconds since its start, clamped to [0, T].</summary>
        private double DropSeconds(DisplayAnimation a, double now) => math.clamp(now - a.start, 0.0, a.seconds);

        /// <summary>
        /// Right after a physics step was simulated, before that frame's collections (the world joins the step for it;
        /// 2026-10-07, DESIGN 7.2.4 and 5.6, D-204): every drop is placed at its time. A drop moves with the steps and
        /// never between two of them, so its members' placements are part of what a step leaves -- the display, which
        /// asks placements only after a new step, reads every one of them, and nothing has to be said or asked for a
        /// drop. Until 2026-10-07 the drops were placed in every frame, whether a step was simulated or not.
        /// </summary>
        public void StepDropsAfterPhysicsStep()
        {
            if (!Enabled) return;
            StepAnimations();
        }

        /// <summary>Every drop placed at its time (the slide and the arc, from the time alone); a finished one ends at x0 + d.</summary>
        private void StepAnimations()
        {
            if (_animations.Count == 0) return;
            long begin = Stopwatch.GetTimestamp();
            double now = _realSeconds();
            for (int i = _animations.Count - 1; i >= 0; i--)
            {
                DisplayAnimation a = _animations[i];
                double t = DropSeconds(a, now);
                Place(a, t);
                if (t >= a.seconds) { a.record.end = "completed"; MeasureDrop(a); _animations.RemoveAt(i); AnimationsCompleted++; }
            }

            AnimationSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
        }

        private static void Place(DisplayAnimation a, double t)
        {
            foreach ((HullGroup.DisplayMember m, Vector3 from) in a.items) if (m.root != null) m.root.transform.localPosition = DropPosition(from, a.delta, a.gravity, a.seconds, t);
            a.record.appliedSeconds = t;
            a.record.placements++;
        }

        /// <summary>
        /// A drop's end (completed or stopped): each member read back from its transform against the formula at the time
        /// last applied (on the curve where it stopped; x0 + d when completed), and its rotation and the body's against the start.
        /// </summary>
        private void MeasureDrop(DisplayAnimation a)
        {
            DropRecord r = a.record;
            foreach (DropMember m in r.members)
            {
                if (m.root == null) continue;
                Vector3 expected = DropPosition(m.from, r.deltaLocal, r.gravityLocal, r.seconds, r.appliedSeconds);
                Transform parent = m.root.parent;
                Vector3 off = m.root.localPosition - expected;
                m.at = m.root.localPosition;
                m.atRead = true;
                double error = (parent != null ? parent.TransformVector(off) : off).magnitude;
                SlideMembersMeasured++;
                MaxDropPositionError = Math.Max(MaxDropPositionError, error);
                MaxDropRotationDegrees = Math.Max(MaxDropRotationDegrees, Quaternion.Angle(m.root.localRotation, m.rotation));
            }

            if (a.group.Body != null) MaxDropRotationDegrees = Math.Max(MaxDropRotationDegrees, Quaternion.Angle(a.group.Body.rotation, r.bodyRotation));
        }

        /// <summary>What became of a published hit's display children, read now (2026-09-30, TL: the ones cut again or retired since apart from the ones still not done).</summary>
        public struct ChildCounts
        {
            public int empty;       // known empty
            public int withShape;   // a current target, its geometry committed
            public int cutAgain;    // cut again since (Replaced): no current frame of its own, by design
            public int retired;     // retired since
            public int pending;     // a current target whose operation has not completed: not decided yet
            public int mismatch;    // a current target whose operation Completed, but neither geometry nor known empty
            public int unknown;     // the operation or the child's state not in the ledger
        }

        public ChildCounts CountChildren(HullHit hit)
        {
            var counts = new ChildCounts();
            foreach (CutOperationId op in hit.displayOperations)
            {
                if (!_ledger.TryGetOperation(op, out LogicalCutOperation record)) { counts.unknown += 2; continue; }
                foreach (LogicalFragmentId child in new[] { record.positive, record.negative })
                {
                    if (_dag.HasNoGeometry(child)) { counts.empty++; continue; }
                    if (!_ledger.TryGetFragmentState(child, out LogicalFragmentState state)) { counts.unknown++; continue; }
                    if (state == LogicalFragmentState.Replaced) { counts.cutAgain++; continue; }
                    if (state == LogicalFragmentState.Retired) { counts.retired++; continue; }
                    if (_dag.TryGetGeometryFrame(child, out Matrix4x4 _)) counts.withShape++;
                    else if (record.state == LogicalCutOperationState.Completed) counts.mismatch++;
                    else counts.pending++;
                }
            }

            return counts;
        }

        /// <summary>A re-cut of the building: its drops stopped where they are now (placed at this time once more, then left there).</summary>
        private void StopAnimationsOf(HullGroup group, string why, int byHit)
        {
            double now = _realSeconds();
            for (int i = _animations.Count - 1; i >= 0; i--)
            {
                DisplayAnimation a = _animations[i];
                if (!ReferenceEquals(a.group, group)) continue;
                double t = DropSeconds(a, now);
                Place(a, t);
                CutPhysicsStep.NotePlacementInputChanged();   // the one placement of a drop outside a physics step (D-204)
                a.record.end = t >= a.seconds ? "completed" : "stopped";
                if (t < a.seconds) a.record.stoppedByHit = byHit;
                MeasureDrop(a);
                _animations.RemoveAt(i);
                if (t >= a.seconds) AnimationsCompleted++;
                else { AnimationsStoppedByReCut++; LastStopFraction = a.record.Phase; Record("group " + group.Id + ": drop " + a.record.id + " (hit " + a.record.hit + ") stopped by " + why + " at u = " + a.record.Phase.ToString("R") + " (t " + t.ToString("R") + " s of " + a.seconds.ToString("R") + "; the pose on its curve kept)"); }
            }
        }

        /// <summary>The old hull cut by an op's plane and the dropping side moved: the points of both sides (each side's vertices and the plane's crossings, the dropping side's moved), for one hull of both.</summary>
        private static List<float3> CutAndMove(float3[] vertices, int[] faceOffsets, int[] faceIndices, HullOp op)
        {
            var points = new List<float3>(vertices.Length * 2 + 16);
            var side = new float[vertices.Length];
            for (int i = 0; i < vertices.Length; i++) side[i] = math.dot(op.plane.xyz, vertices[i]) + op.plane.w;
            for (int i = 0; i < vertices.Length; i++)
            {
                bool positive = side[i] > 0f, negative = side[i] < 0f;
                bool moves = op.movingPositive ? positive : negative, stays = op.movingPositive ? negative : positive;
                if (stays || side[i] == 0f) points.Add(vertices[i]);
                if (moves || side[i] == 0f) points.Add(vertices[i] + op.displacement);
            }

            for (int f = 0; f + 1 < faceOffsets.Length; f++)
            {
                int from = faceOffsets[f], count = faceOffsets[f + 1] - from;
                for (int k = 0; k < count; k++)
                {
                    int a = faceIndices[from + k], b = faceIndices[from + (k + 1) % count];
                    if (!((side[a] > 0f && side[b] < 0f) || (side[a] < 0f && side[b] > 0f))) continue;
                    float t = side[a] / (side[a] - side[b]);
                    float3 x = vertices[a] + t * (vertices[b] - vertices[a]);
                    points.Add(x);
                    points.Add(x + op.displacement);
                }
            }

            return points;
        }

        private static unsafe void ReadHull(PhysicsOwnerShape shape, out float3[] vertices, out int[] faceOffsets, out int[] faceIndices)
        {
            ConvexBrepBank bank = shape.BankOf(0);
            ConvexBrepRange r = shape.Convex(0);
            vertices = new float3[r.vertexCount];
            for (int i = 0; i < r.vertexCount; i++) vertices[i] = bank.vertices[r.vertexBase + i];
            faceOffsets = new int[r.faceCount + 1];
            var indices = new List<int>();
            for (int f = 0; f < r.faceCount; f++)
            {
                int from = bank.faceOffsets[r.faceBase + f], to = bank.faceOffsets[r.faceBase + f + 1];
                for (int k = from; k < to; k++) indices.Add(bank.faceIndices[r.faceIndexBase + k]);
                faceOffsets[f + 1] = indices.Count;
            }

            faceIndices = indices.ToArray();
        }

        private bool OfferHull(HullUpdateWork w)
        {
            if (w.offered) return true;
            if (refuseOffersForTest > 0) { refuseOffersForTest--; OffersRefused++; return false; }
            if (!_dispatcher.TryEnqueue(WorkPurpose.Speculative, w, out WorkTicket ticket)) { OffersRefused++; return false; }
            w.ticket = ticket;
            w.offered = true;
            return true;
        }

        /// <summary>The hull updates: each in flight taken at its stage; a building with cuts waiting and none in flight gets one, made from its hull now with every cut waiting.</summary>
        private void StepHullUpdates()
        {
            for (int i = _hullWorks.Count - 1; i >= 0; i--)
            {
                HullUpdateWork w = _hullWorks[i];
                if (!w.offered) { if (!MayStart()) return; OfferHull(w); continue; }
                if (!w.collected) continue;
                if (w.cancelled && !w.Begun)
                {
                    _hullWorks.RemoveAt(i);
                    HullUpdatesCancelled++;
                    GiveBack(w, "cancelled before it ran");
                    continue;
                }

                if (!MayStart()) return;
                long begin = Stopwatch.GetTimestamp();
                if (w.scanning)
                {
                    HullUpdateWorkerSeconds += w.scanSeconds;
                    if (w.group.State == HullGroupState.Gone || w.group.Root == null) { _hullWorks.RemoveAt(i); continue; }
                    if (w.group.HullGeneration != w.generation) { _hullWorks.RemoveAt(i); HullUpdatesStale++; GiveBack(w, "stale at the scan (generation " + w.generation + " made, " + w.group.HullGeneration + " now)"); continue; }
                    if (refuseHullForTest) { w.vertices = null; w.reason = "refused (test)"; }
                    if (w.vertices == null || w.vertices.Length > _vertexLimit)
                    {
                        _hullWorks.RemoveAt(i);
                        Refuse(w, w.vertices == null ? w.reason : w.vertices.Length + " vertices past the limit " + _vertexLimit);
                        continue;
                    }

                    ConvexHullBuilder.ColliderArrays(w.vertices, w.faceOffsets, w.faceIndices, out Vector3[] meshVertices, out int[] triangles);
                    w.mesh = new Mesh { name = "Building hull " + w.group.Id + " update g" + (w.generation + 1), hideFlags = HideFlags.HideAndDontSave };
                    w.mesh.vertices = meshVertices;
                    w.mesh.triangles = triangles;
                    w.mesh.RecalculateBounds();
                    w.meshId = w.mesh.GetEntityId();
                    w.ResetForBake();
                    OfferHull(w);
                    HullUpdateMainSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    Unit("hull update mesh", begin);
                    continue;
                }

                HullUpdateWorkerSeconds += w.bakeSeconds;
                MeshesBaked++;
                _hullWorks.RemoveAt(i);
                ExchangeHull(w);
                HullUpdateMainSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                Unit("hull update exchange", begin);
            }

            foreach (KeyValuePair<HullGroup, HullOp> p in _waitingRequest)
            {
                HullGroup g = p.Key;
                if (g.State == HullGroupState.Gone || g.Root == null || g.Shape == null || _hullWorks.Exists(x => ReferenceEquals(x.group, g))) continue;
                if (!MayStart()) return;
                long begin = Stopwatch.GetTimestamp();
                ReadHull(g.Shape, out float3[] v, out int[] fo, out int[] fi);
                var w = new HullUpdateWork { group = g, generation = g.HullGeneration, vertices = v, faceOffsets = fo, faceIndices = fi, vertexLimit = _vertexLimit, cooking = Cooking };
                w.ops.Add(p.Value);
                _waitingRequest.Remove(g);
                if (bumpHullGenerationForTest) { bumpHullGenerationForTest = false; g.HullGeneration++; }
                _hullWorks.Add(w);
                MaxHullUpdatesInFlight = Math.Max(MaxHullUpdatesInFlight, _hullWorks.Count);
                HullUpdatesBegun++;
                OfferHull(w);
                HullUpdateMainSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                return;   // one begun a Step: the dictionary is not walked on after a change
            }
        }

        /// <summary>A stale or cancelled update: the old hull kept; its request waits again only when no newer one waits (never in front of it), else it is skipped.</summary>
        private void GiveBack(HullUpdateWork w, string why)
        {
            if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }
            if (w.group.State == HullGroupState.Gone) return;
            bool newer = _waitingRequest.ContainsKey(w.group);
            if (newer) HullRequestsSkipped += w.ops.Count;
            else _waitingRequest[w.group] = w.ops[w.ops.Count - 1];
            if (_hullUpdateRecords.Count < 512) _hullUpdateRecords.Add("group " + w.group.Id + ": update not exchanged, " + why + "; the old hull kept; " + (newer ? "a newer request waits: this one skipped" : "its request waits again (none newer)"));
        }

        /// <summary>A refused update: its cuts dropped, the old hull kept (the display went on without it).</summary>
        private void Refuse(HullUpdateWork w, string why)
        {
            if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }
            HullUpdatesRefused++;
            HullRequestsDropped += w.ops.Count;
            if (_hullUpdateRecords.Count < 512) _hullUpdateRecords.Add("group " + w.group.Id + ": update of " + w.ops.Count + " cut(s) refused (" + why + "); the old hull kept (generation " + w.group.HullGeneration + ")");
        }

        /// <summary>A baked candidate (Main): the generation checked again, the mesh put on the group's collider and checked by PhysX; exchanged, or the old hull kept.</summary>
        private void ExchangeHull(HullUpdateWork w)
        {
            HullGroup g = w.group;
            if (g.State == HullGroupState.Gone || g.Root == null) { if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; } return; }
            if (g.HullGeneration != w.generation) { HullUpdatesStale++; GiveBack(w, "stale at the exchange (generation " + w.generation + " made, " + g.HullGeneration + " now)"); return; }
            Mesh oldMesh = g.Collider.sharedMesh;
            g.Collider.sharedMesh = w.mesh;
            if (g.Collider.GeometryHolder.Type != UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh)
            {
                g.Collider.sharedMesh = oldMesh;
                Refuse(w, "PhysX refused the candidate's mesh");
                return;
            }

            HullBrep brep = HullBrep.FromArrays(w.vertices, w.faceOffsets, w.faceIndices);
            PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(brep.Bank, new[] { brep.Range }, new List<Mesh> { w.mesh }, PhysicsShapeSource.External(), float4x4.identity);
            Mesh mesh = w.mesh;
            w.mesh = null;
            ReplaceShape(g, shape, brep, mesh);
            g.HullGeneration++;
            HullUpdatesAdopted++;
            NoteHullSize(g);
            if (_hullUpdateRecords.Count < 512) _hullUpdateRecords.Add("group " + g.Id + ": update of " + w.ops.Count + " cut(s) exchanged: " + brep.VertexCount + " vertices, " + brep.FaceCount + " faces (generation " + g.HullGeneration + "); scan " + (w.scanSeconds * 1000).ToString("F3") + " ms, bake " + (w.bakeSeconds * 1000).ToString("F3") + " ms off Main");
        }

        /// <summary>The end: the drops forgotten, the updates cancelled or let go to release themselves, the waits dropped.</summary>
        private void DisposeKinematic()
        {
            foreach (DisplayAnimation a in _animations) a.record.end ??= "cleared";
            _animations.Clear();
            foreach (HullUpdateWork w in _hullWorks)
            {
                if (!w.offered || w.collected || _dispatcher.Cancel(w.ticket))
                {
                    if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }
                }
                else
                {
                    w.abandoned = true;   // running: its mesh goes when it is collected
                }
            }

            _hullWorks.Clear();
            _waitingRequest.Clear();
        }
    }
}
