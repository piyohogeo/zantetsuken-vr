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
    /// <summary>The publication of a hull cut, the display members' cuts, the aggregation of a building's resting groups (a candidate hull scanned and baked off Main, judged, and only then the bodies merged), the trial's rest, the held hits, the record and the end.</summary>
    public sealed partial class BuildingHullFusion
    {
        // ---- the publication: one Main section before the physics step ----

        /// <summary>A building whose groups are more than one since its last cut: aggregated by the deadline from that cut, or by the next Slash on the building.</summary>
        private sealed class BuildingWait
        {
            public int building;
            public double cutAt;
            public bool nextSlash;
        }

        private readonly List<BuildingWait> _waits = new List<BuildingWait>();

        /// <summary>Buildings whose groups wait to be aggregated (more than one group since a cut).</summary>
        public int PairsWaiting => _waits.Count;

        private enum PublishOutcome { Published, WaitingForRoom, Failed }

        private bool IsDue(BuildingWait w) => w.nextSlash || (_settings.deadlineSeconds > 0.0 && _realSeconds() - w.cutAt >= _settings.deadlineSeconds);

        private BuildingWait WaitOf(int building)
        {
            foreach (BuildingWait w in _waits) if (w.building == building) return w;
            return null;
        }

        private readonly Dictionary<int, double> _lastCutAt = new Dictionary<int, double>();

        private void NoteCut(int building)
        {
            BuildingWait w = WaitOf(building);
            if (w == null) { w = new BuildingWait { building = building }; _waits.Add(w); }
            w.cutAt = _realSeconds();
            w.nextSlash = false;
            _lastCutAt[building] = w.cutAt;
        }

        /// <summary>
        /// The fixed and free classes of a building changed (a hold, a release): its aggregation is looked at again. The
        /// building's clock is its last cut's, never reset: a deadline already past is due at the next Step within the
        /// budget. Nothing to do for a building of one group.
        /// </summary>
        private void Reevaluate(int building, string why)
        {
            int groups = 0;
            foreach (HullGroup g in _groups) if (g.Building == building && g.State != HullGroupState.Gone) groups++;
            if (groups < 2 || WaitOf(building) != null) return;
            var w = new BuildingWait { building = building, cutAt = _lastCutAt.TryGetValue(building, out double at) ? at : _realSeconds() };
            _waits.Add(w);
            Reevaluations++;
            Record("building " + building + " aggregation looked at again (" + why + "; " + groups + " groups; " + (IsDue(w) ? "due now" : "by the deadline") + ")");
        }

        public int Reevaluations { get; private set; }

        /// <summary>
        /// The groups of a building that can be aggregated now: its idle groups by their kinematic class (fixed or held
        /// against free). A resting class of two or more groups is one to make a candidate of. False when a group of the
        /// building is busy (cutting, or a participant of a candidate in flight).
        /// </summary>
        private bool TryClassify(int building, List<HullGroup> kinematic, List<HullGroup> free)
        {
            kinematic.Clear(); free.Clear();
            foreach (HullGroup g in _groups)
            {
                if (g.Building != building || g.State == HullGroupState.Gone) continue;
                if (g.State != HullGroupState.Idle) return false;
                if (HasMovingPair(building)) return false;   // the sides still move: they are fixed first
                (g.Kinematic ? kinematic : free).Add(g);
            }

            return true;
        }

        private readonly List<HullGroup> _kinematicScratch = new List<HullGroup>(), _freeScratch = new List<HullGroup>();

        /// <summary>Whether a due wait has a resting class of two or more groups to make a candidate of (a free group waits for its rest; a class already judged apart is not tried again until it changes).</summary>
        private bool HasDueUnion(BuildingWait w) => IsDue(w) && HasUnitableClass(w.building);

        /// <summary>A resting class (fixed or held) of two or more groups of a building whose candidate has not been judged: one to aggregate.</summary>
        private bool HasUnitableClass(int building)
        {
            int kinematic = 0;
            foreach (HullGroup g in _groups) { if (g.Building != building || g.State == HullGroupState.Gone) continue; if (g.Kinematic) kinematic++; }
            return kinematic >= 2 && !IsRejectedNow(building);
        }

        /// <summary>
        /// The cut's products are ready: the display operations are verified admissible first (the sources live, under no
        /// other cut, the ledger's room enough) -- short of room the cut waits here with the old state whole; a source
        /// gone or under a cut refuses it, the old state whole -- then, in one section, the display cuts are admitted, the
        /// physics switched (the positive side keeps the old Root, Body and collider with the positive child's mesh; the
        /// negative side is one new body), the display members placed on their sides, and the masses and motions given.
        /// </summary>
        private PublishOutcome Publish(PendingCut cut)
        {
            HullGroup group = cut.group;
            PhysicsCutProducts products = cut.request.Products;
            long begin = Stopwatch.GetTimestamp();
            if (products.PartCount(true) != 1 || products.PartCount(false) != 1)
            {
                Fail(cut, "Failed: the cut gave " + products.PartCount(true) + " positive and " + products.PartCount(false) + " negative convexes (one each expected)");
                return PublishOutcome.Failed;
            }

            // The display operations wanted: every crossed member's. Admissible, or the cut waits or is refused with nothing changed.
            var crossed = new List<MemberSide>();
            foreach (MemberSide s in cut.sides) if (s.side == 0) crossed.Add(s);
            foreach (MemberSide s in crossed)
            {
                if (!_ledger.IsCurrentTarget(s.member.fragment)) { Fail(cut, "Refused: display member " + s.member.fragment.value + " is not live (nothing changed)"); DisplayCutsRefused++; return PublishOutcome.Failed; }
                if (_ledger.TryGetActiveOperation(s.member.fragment, out _)) { Fail(cut, "Refused: display member " + s.member.fragment.value + " is under another cut (nothing changed)"); DisplayCutsRefused++; return PublishOutcome.Failed; }
            }

            if (crossed.Count > _ledger.Budget.MaxIncompleteCutOperationCount)
            {
                Fail(cut, "Refused: " + crossed.Count + " display cuts, the ledger's capacity " + _ledger.Budget.MaxIncompleteCutOperationCount + " (nothing changed)");
                DisplayCutsRefused++;
                return PublishOutcome.Failed;
            }

            int room = _ledger.Budget.MaxIncompleteCutOperationCount - _ledger.Budget.IncompleteCutOperationCount;
            if (crossed.Count > room)
            {
                if (cut.roomWaits == 0) Record("hit " + cut.hit.id + " waits: " + crossed.Count + " display cuts, ledger room " + room);
                return PublishOutcome.WaitingForRoom;
            }

            if (!PhysicsOwnerBuilder.TryFinalMassFrame(products, group.Mass, out quaternion rot, out float3 off, out PhysicsOwnerBuildOutcome outcome)
                || !PhysicsOwnerBuilder.TryFinalSideMassInFrame(products, group.Mass, true, rot, off, out double massP, out float3 centreP, out float3 inertiaP, out quaternion rotP, out outcome)
                || !PhysicsOwnerBuilder.TryFinalSideMassInFrame(products, group.Mass, false, rot, off, out double massN, out float3 centreN, out float3 inertiaN, out quaternion rotN, out outcome))
            {
                Fail(cut, "Failed: the sides' masses could not be established (" + outcome + ")");
                return PublishOutcome.Failed;
            }

            PhysicsShapeSource source = PhysicsShapeSource.For(products);
            source.Acquire();
            PhysicsOwnerShape positiveShape, negativeShape;
            try
            {
                positiveShape = PhysicsOwnerShape.OfSide(group.Shape, products, source, true);
                negativeShape = PhysicsOwnerShape.OfSide(group.Shape, products, source, false);
                source.TakeOwnership();
            }
            catch (Exception e)
            {
                source.Release();
                Fail(cut, "Failed: the child shapes could not be made (" + e.GetType().Name + ")");
                return PublishOutcome.Failed;
            }

            // ---- one Main section from here: nothing below refuses ----
            StopPairsOf(group, "the group is cut again");   // never inherited: the new sides get a pair of their own
            source.Release();
            cut.request.Products = null;   // owned by the shapes' source from here
            MeshesBaked += 2;
            group.Shape.ReleaseFromWork();
            cut.classification.Dispose();
            cut.classification = null;

            // The display cuts, admitted first (judged possible above on this same state: another answer is a broken invariant).
            long displayBegin = Stopwatch.GetTimestamp();
            var admitted = new List<(MemberSide side, CutOperationId operation)>(crossed.Count);
            foreach (MemberSide s in crossed)
            {
                LogicalCutAdmission admission = _dag.TryAdmit(s.member.fragment, s.planeMember, true, out CutOperationId operation);
                if (admission != LogicalCutAdmission.Admitted) throw new InvalidOperationException("the ledger answered " + admission + " for display member " + s.member.fragment.value + " whose admission had just been judged possible");
                if (_ledger.PrepareAnchorDistribution(operation, _anchorEpsilon, out _) != AnchorPreparationOutcome.Prepared) throw new InvalidOperationException("the anchors of display member " + s.member.fragment.value + " could not be prepared");
                admitted.Add((s, operation));
            }

            double displaySeconds = (Stopwatch.GetTimestamp() - displayBegin) / (double)Stopwatch.Frequency;

            var positiveAnchors = new List<float3>();
            var negativeAnchors = new List<float3>();
            foreach (float3 a in group.Anchors)
            {
                float s = math.dot(cut.planeGroup.xyz, a) + cut.planeGroup.w;
                if (s >= -_anchorEpsilon) positiveAnchors.Add(a);
                if (s <= _anchorEpsilon) negativeAnchors.Add(a);
            }

            bool positiveFixed = positiveAnchors.Count > 0, negativeFixed = negativeAnchors.Count > 0;
            Vector3 v0 = group.Body.isKinematic ? Vector3.zero : group.Body.linearVelocity, w0 = group.Body.isKinematic ? Vector3.zero : group.Body.angularVelocity;
            Vector3 c0 = group.Body.worldCenterOfMass;
            Transform root = group.Root.transform;

            long physicsBegin = Stopwatch.GetTimestamp();
            HullGroup positive = group;
            ReplaceShape(positive, positiveShape, null, null);
            positive.HullIsFused = false;
            positive.Anchors.Clear(); positive.Anchors.AddRange(positiveAnchors);
            positive.Staged = false;
            positive.Kinematic = positiveFixed;
            positive.Body.isKinematic = positiveFixed;
            positive.Mass = massP; positive.Centre = centreP; positive.Inertia = inertiaP; positive.InertiaRotation = rotP;
            ApplyMass(positive);
            var negative = new HullGroup(++_lastGroupId, group.Building) { owner = this, Generation = 1 };
            negative.Root = new GameObject("Building hull group " + negative.Id + " -");
            negative.Root.transform.SetPositionAndRotation(root.position, root.rotation);
            negative.Body = negative.Root.AddComponent<Rigidbody>();
            negative.Body.automaticCenterOfMass = false;
            negative.Body.automaticInertiaTensor = false;
            negative.Body.useGravity = true;
            negative.Body.isKinematic = negativeFixed;
            negative.Kinematic = negativeFixed;
            negative.Shape = negativeShape;
            negative.Collider = MakeCollider(negative.Root, negativeShape);
            negative.Anchors.AddRange(negativeAnchors);
            negative.Mass = massN; negative.Centre = centreN; negative.Inertia = inertiaN; negative.InertiaRotation = rotN;
            ApplyMass(negative);
            foreach (KeyValuePair<long, SlashReservation> r in group.Reserved) negative.Reserved[r.Key] = r.Value;   // both children carry the lineage's reservations
            _groups.Add(negative);
            _byId[negative.Id] = negative;
            GroupsMade++;
            BodiesMade++;
            positive.Generation++;
            positive.State = HullGroupState.Idle;
            negative.State = HullGroupState.Idle;
            float3 normal = math.normalize(cut.planeWorld.xyz);
            if (!positiveFixed) { positive.Body.WakeUp(); Inherit(positive.Body, v0, w0, c0, normal * DefaultSeparationImpulse); }
            if (!negativeFixed) { negative.Body.WakeUp(); Inherit(negative.Body, v0, w0, c0, -normal * DefaultSeparationImpulse); }
            // The rest: what stood on the old body stands on the anchored side (or on the positive one, judged by the
            // contacts), and each side is followed from now with its own clock -- an anchored side as ground, a free one
            // for its rest; a side that was held before its cut is free again unless an anchor fixes it.
            double restNow = _physicsSeconds();
            _rest.SplitGroup(positive.Body, positive.Body, positiveFixed, negative.Body, negativeFixed, positive.Collider, negative.Collider);
            _rest.TrackGroup(positive.Body, positive.Building, new[] { positive.Collider }, restNow, positiveFixed);
            _rest.TrackGroup(negative.Body, negative.Building, new[] { negative.Collider }, restNow, negativeFixed);
            if (_settings.stageSeconds > 0.0 && !(positiveFixed && negativeFixed)) BeginPair(positive, negative, normal, cut.planeGroup);
            PublishPhysicsSeconds += (Stopwatch.GetTimestamp() - physicsBegin) / (double)Stopwatch.Frequency;

            // The display members to their sides; the admitted cuts published, their children display-only owners on their sides.
            // The preparation classified the members in their order (one side a member, cut.sides[i] for Members[i]; the
            // list does not change while the group is cut): each is read once, by its index.
            displayBegin = Stopwatch.GetTimestamp();
            int moved = 0;
            var members = new List<HullGroup.DisplayMember>(positive.Members);
            positive.Members.Clear();
            var admittedOf = new Dictionary<MemberSide, CutOperationId>();
            foreach ((MemberSide side, CutOperationId operation) in admitted) admittedOf[side] = operation;
            for (int i = 0; i < members.Count; i++)
            {
                HullGroup.DisplayMember m = members[i];
                MemberSide known = i < cut.sides.Count && ReferenceEquals(cut.sides[i].member, m) ? cut.sides[i] : null;
                SideLookups++;
                if (known == null) throw new InvalidOperationException("the display members of group " + positive.Id + " changed while it was cut: member " + i + " is not the one classified");
                if (known.side == 0 && admittedOf.TryGetValue(known, out CutOperationId operation)) { PublishDisplayCut(positive, negative, known, operation, cut.hit); continue; }
                if (known.side < 0) { m.root.transform.SetParent(negative.Root.transform, true); negative.Members.Add(m); moved++; }
                else positive.Members.Add(m);
            }

            displaySeconds += (Stopwatch.GetTimestamp() - displayBegin) / (double)Stopwatch.Frequency;
            PublishDisplaySeconds += displaySeconds;
            double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            PublishSeconds += seconds;
            if (GroupCuts == 0) FirstPublishSeconds = seconds; else MaxPublishSecondsAfterFirst = Math.Max(MaxPublishSecondsAfterFirst, seconds);
            MaxPublishSeconds = Math.Max(MaxPublishSeconds, seconds);
            GroupCuts++;
            NoteHullSize(positive);
            NoteHullSize(negative);
            NoteLiveHulls();
            positive.lastCutAt = negative.lastCutAt = _realSeconds();
            NoteCut(positive.Building);
            cut.hit.positiveGroup = positive.Id; cut.hit.negativeGroup = negative.Id;
            Finish(cut.hit, "Published", true);
            Record("hit " + cut.hit.id + " published t " + _physicsSeconds().ToString("F3") + ": group " + positive.Id + " -> " + positive.Id + " (+, " + positive.VertexCount + " v, " + (positiveFixed ? "held" : "free") + ", mass " + massP.ToString("R") + " (body " + positive.Body.mass.ToString("R") + ", inertia " + positive.Body.inertiaTensor.ToString("G9") + "), members " + positive.MemberCount + ") and "
                + negative.Id + " (-, " + negative.VertexCount + " v, " + (negativeFixed ? "held" : "free") + ", mass " + massN.ToString("R") + " (body " + negative.Body.mass.ToString("R") + ", inertia " + negative.Body.inertiaTensor.ToString("G9") + "), members " + negative.MemberCount + "); display members moved " + moved + ", cut " + admitted.Count + "; " + (seconds * 1000).ToString("F3") + " ms");
            return PublishOutcome.Published;
        }

        private static void Inherit(Rigidbody body, Vector3 v0, Vector3 w0, Vector3 c0, float3 push)
        {
            Vector3 c = body.worldCenterOfMass;
            body.linearVelocity = v0 + Vector3.Cross(w0, c - c0) + (Vector3)push / Mathf.Max(body.mass, 1e-6f);
            body.angularVelocity = w0;
        }

        /// <summary>A group's hull replaced: the collider's mesh, the shape, and what the old one owned (an authored brep and mesh) let go.</summary>
        private void ReplaceShape(HullGroup group, PhysicsOwnerShape shape, HullBrep ownedBrep, Mesh ownedMesh)
        {
            long begin = Stopwatch.GetTimestamp();
            PhysicsOwnerShape old = group.Shape;
            HullBrep oldBrep = group.OwnedBrep;
            Mesh oldMesh = group.OwnedMesh;
            group.Shape = shape;
            group.OwnedBrep = ownedBrep;
            group.OwnedMesh = ownedMesh;
            group.Collider.sharedMesh = shape.MeshOf(0);
            old?.Dispose();
            oldBrep?.Dispose();
            if (oldMesh != null) PhysicsCutCook.DestroyMesh(oldMesh);
            HullExchanges++;
            HullExchangeSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
        }

        /// <summary>An admitted display cut published: its two children display-only owners with a Root each under their side at the member's pose; the source retired. Inside the publication's section: nothing here refuses.</summary>
        private void PublishDisplayCut(HullGroup positive, HullGroup negative, MemberSide side, CutOperationId operation, HullHit hit)
        {
            HullGroup.DisplayMember m = side.member;
            if (_ledger.PreparePublication(operation) != LogicalCutResultOutcome.Applied || _ledger.Publish(operation, out LogicalFragmentId positiveChild, out LogicalFragmentId negativeChild) != LogicalCutResultOutcome.Applied)
            {
                throw new InvalidOperationException("the ledger refused the publication of display member " + m.fragment.value + " whose admission it had just given");
            }

            _registry.TryGet(m.fragment, out PhysicsFragmentOwner sourceOwner);
            BuildingLineage lineage = sourceOwner != null ? sourceOwner.Building.ChildOfSplit() : BuildingLineage.RegisteredBuilding.ChildOfSplit();
            Transform at = m.root.transform;
            var positiveMember = new HullGroup.DisplayMember { fragment = positiveChild, root = NewMemberRoot(positive.Root.transform, at, "Display member " + positiveChild.value) };
            var negativeMember = new HullGroup.DisplayMember { fragment = negativeChild, root = NewMemberRoot(negative.Root.transform, at, "Display member " + negativeChild.value) };
            _registry.Reserve(2);
            _registry.Add(positiveChild, PhysicsFragmentOwner.DisplayOnly(positiveMember.root, Matrix4x4.identity, lineage));
            _registry.Add(negativeChild, PhysicsFragmentOwner.DisplayOnly(negativeMember.root, Matrix4x4.identity, lineage));
            positive.Members.Add(positiveMember);
            negative.Members.Add(negativeMember);
            _registry.Retire(m.fragment);
            m.root = null;
            hit.displayOperations.Add(operation);
            _displayOperations.Add(operation);
            DisplayCuts++;
        }

        // ---- the display operations still making their geometry ----

        private readonly List<CutOperationId> _displayOperations = new List<CutOperationId>();
        public int DisplayOperationsOpen => _displayOperations.Count;
        public int DisplayOperationsFailed { get; private set; }

        private void SettleDisplayOperations()
        {
            for (int i = _displayOperations.Count - 1; i >= 0; i--)
            {
                CutGeometryStage stage = _dag.StageOf(_displayOperations[i]);
                if (stage == CutGeometryStage.Committed) { DisplayOperationEnded(_displayOperations[i]); _displayOperations.RemoveAt(i); continue; }
                if (stage == CutGeometryStage.Reclaimed)
                {
                    // Ended without a commit: the DAG's own failure (recorded there and here); the group is not retired for it.
                    DisplayOperationsFailed++;
                    Record("display operation " + _displayOperations[i].value + " ended without a commit: " + _dag.FailureOf(_displayOperations[i]));
                    DisplayOperationEnded(_displayOperations[i]);
                    _displayOperations.RemoveAt(i);
                }
            }
        }

        // ---- the aggregation (TL, 2026-09-30): a candidate hull of the building's resting groups, scanned and baked off Main and judged first; the bodies merge only when it is adopted ----

        private enum FusionStage { Scanning = 0, Baking = 2 }

        /// <summary>One group taking part in a candidate, as it stood when the candidate was made: its generation and its pose relative to the target's frame.</summary>
        private readonly struct Participant
        {
            public readonly HullGroup group;
            public readonly int generation;
            public readonly float4x4 toTarget;
            public Participant(HullGroup group, float4x4 toTarget) { this.group = group; generation = group.Generation; this.toTarget = toTarget; }
        }

        /// <summary>One candidate's work for the dispatcher: the hull scan of every participant's vertices in the target's frame, then -- with the mesh made on Main between -- its bake; each offered once, waited for when the dispatcher has no room, offered again when cancelled before it began.</summary>
        private sealed class FusionWork : IDispatchWork
        {
            public readonly List<float3> points;
            public readonly List<Participant> participants = new List<Participant>();
            public HullGroup target;   // the anchored participant, else the heaviest: keeps its Root, Body and collider at the adoption
            public int building;
            public string key;   // the resting class the candidate is of
            public bool retry;
            public FusionStage stage;
            public float3[] vertices; public int[] faceOffsets, faceIndices; public HullBuildReport report;
            public Mesh mesh;   // made on Main once scanned, baked by the work, checked on Main
            public MeshColliderCookingOptions cooking;
            public int vertexLimit;
            public int meshId;
            public bool offered, collected, cancelled, abandoned;
            public WorkTicket ticket;
            public int offers, reoffers;
            private int _done, _begun;
            public double scanSeconds, bakeSeconds;

            public FusionWork(List<float3> points) { this.points = points; }
            public bool Done => Volatile.Read(ref _done) == 1;
            public bool Begun => Volatile.Read(ref _begun) == 1;
            public bool IsComplete => Done;

            public void Begin()
            {
                Volatile.Write(ref _begun, 1);
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    if (stage == FusionStage.Scanning)
                    {
                        if (!ConvexHullBuilder.TryBuild(points, vertexLimit, out vertices, out faceOffsets, out faceIndices, out report)) vertices = null;
                    }
                    else
                    {
                        Physics.BakeMesh(meshId, true, cooking);   // any thread
                    }
                }
                catch (Exception e)
                {
                    report.reason = e.GetType().Name + ": " + e.Message;
                    vertices = null;
                }
                finally
                {
                    double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    if (stage == FusionStage.Scanning) scanSeconds = seconds; else bakeSeconds = seconds;
                    Volatile.Write(ref _done, 1);
                }
            }

            /// <summary>The dispatcher hands it back (Main): finished, or cancelled before it began.</summary>
            public void Collect(WorkCompletion completion)
            {
                collected = true;
                cancelled = completion.outcome == WorkOutcome.Cancelled;
                if (abandoned && mesh != null) { PhysicsCutCook.DestroyMesh(mesh); mesh = null; }
            }

            /// <summary>Ready for its next offer: the flags of the last one cleared.</summary>
            public void Reset(FusionStage next)
            {
                stage = next;
                offered = collected = cancelled = false;
                ticket = default;
                Volatile.Write(ref _done, 0);
                Volatile.Write(ref _begun, 0);
            }

            public string Names { get { var t = new System.Text.StringBuilder(); foreach (Participant p in participants) { if (t.Length > 0) t.Append(", "); t.Append(p.group.Id); } return t.ToString(); } }
        }

        private readonly List<FusionWork> _fusions = new List<FusionWork>();
        public int FusionsInFlight => _fusions.Count;

        /// <summary>
        /// What the last candidate of a building's resting class came to, for the class as it stood (its groups and their
        /// generations): a candidate not adopted is not made again for the same class -- the groups stay apart, each its
        /// own hull, each cuttable -- and the class changing (a cut, a hold, a release) is a new class. A scan the builder
        /// refused is tried again after the deadline, three times, then given up (a failure completion for that class).
        /// </summary>
        private sealed class AggregationState
        {
            public int building;
            public string key;
            public int refusals;
            public double retryAt = double.PositiveInfinity;
            public bool givenUp;   // a failure completion for this class
            public bool notAchieved;   // the candidate would add a penetration outside the participants, or could not be verified: recorded, not a failure
            public string why;
            public double since = double.NaN;   // real seconds: when the class began standing not achieved or given up
            public bool Rejected => givenUp || notAchieved;
        }

        private readonly Dictionary<int, AggregationState> _aggregations = new Dictionary<int, AggregationState>();
        private readonly List<int> _aggregationScratch = new List<int>();

        private static string ClassKey(List<HullGroup> cls)
        {
            var ids = new List<HullGroup>(cls);
            ids.Sort((a, b) => a.Id.CompareTo(b.Id));
            var t = new System.Text.StringBuilder();
            foreach (HullGroup g in ids) { if (t.Length > 0) t.Append(','); t.Append(g.Id).Append(':').Append(g.Generation); }
            return t.ToString();
        }

        /// <summary>The building's resting class now (every kinematic group standing, whatever its state), as a key.</summary>
        private string CurrentRestingKey(int building)
        {
            _keyScratch.Clear();
            foreach (HullGroup g in _groups) if (g.Building == building && g.State != HullGroupState.Gone && g.Kinematic) _keyScratch.Add(g);
            return ClassKey(_keyScratch);
        }

        private readonly List<HullGroup> _keyScratch = new List<HullGroup>();

        /// <summary>Whether the building's resting class, as it stands, is one whose candidate was not adopted (not achieved or given up): nothing new to try until the class changes.</summary>
        private bool IsRejectedNow(int building) => _aggregations.TryGetValue(building, out AggregationState a) && a.Rejected && a.key == CurrentRestingKey(building);

        /// <summary>Whether the building's resting class, as it stands, has a refused scan waiting for its retry.</summary>
        private bool IsRetryPending(int building) => _aggregations.TryGetValue(building, out AggregationState a) && !a.Rejected && a.refusals > 0 && a.key == CurrentRestingKey(building);

        private AggregationState StateOf(int building, string key)
        {
            if (_aggregations.TryGetValue(building, out AggregationState a) && a.key == key) return a;
            if (a != null) EndAggregationState(a);
            a = new AggregationState { building = building, key = key };
            _aggregations[building] = a;
            return a;
        }

        /// <summary>A class outcome ends (the class changed, or its candidate was adopted): the time it stood not achieved or given up is recorded.</summary>
        private void EndAggregationState(AggregationState a)
        {
            if (a.Rejected && !double.IsNaN(a.since))
            {
                double s = _realSeconds() - a.since;
                NotAchievedSeconds += s;
                _maxNotAchievedEnded = Math.Max(_maxNotAchievedEnded, s);
            }

            _aggregations.Remove(a.building);
        }

        /// <summary>The class outcomes whose class is gone (a cut, a hold or a release changed the resting class): ended.</summary>
        private void SettleAggregationStates()
        {
            _aggregationScratch.Clear();
            foreach (KeyValuePair<int, AggregationState> p in _aggregations) if (p.Value.key != CurrentRestingKey(p.Key)) _aggregationScratch.Add(p.Key);
            foreach (int b in _aggregationScratch)
            {
                AggregationState a = _aggregations[b];
                Record("building " + b + ": the resting class [" + a.key + "] " + (a.givenUp ? "given up" : a.notAchieved ? "not achieved" : "awaiting a retry") + " is gone (a cut, a hold or a release); its outcome ends" + (a.Rejected && !double.IsNaN(a.since) ? " after " + (_realSeconds() - a.since).ToString("F2") + " s" : ""));
                EndAggregationState(a);
            }
        }

        /// <summary>Tests only: every candidate's snapshot is made stale at once (its target's generation moved), so its adoption drops it.</summary>
        internal bool bumpGenerationAfterFusionSnapshotForTest;

        /// <summary>Tests only: this many offers to the dispatcher are refused (the work waits, nothing runs on Main).</summary>
        internal int refuseOffersForTest;

        /// <summary>Tests only: asked after an offer succeeds; true takes the offer back out of the dispatcher's queue as its shutdown would, handing the work its Cancelled collection.</summary>
        internal Func<bool> cancelOfferForTest;

        /// <summary>Tests only: every hull scan reports a refusal (the groups stay apart).</summary>
        internal bool refuseHullForTest;

        private bool Offer(FusionWork w)
        {
            if (w.offered) return true;
            w.offers++;
            if (refuseOffersForTest > 0) { refuseOffersForTest--; OffersRefused++; return false; }
            if (!_dispatcher.TryEnqueue(WorkPurpose.Speculative, w, out WorkTicket ticket)) { OffersRefused++; return false; }
            w.ticket = ticket;
            w.offered = true;
            if (cancelOfferForTest != null && cancelOfferForTest() && _dispatcher.Cancel(w.ticket)) w.Collect(WorkCompletion.Cancelled);
            return true;
        }

        private bool HasCandidateInFlight(int building)
        {
            foreach (FusionWork w in _fusions) if (w.building == building) return true;
            return false;
        }

        private void StepFusions()
        {
            SettleAggregationStates();

            // The candidates in flight, each by its stage: offered when it is not, offered again when cancelled before it
            // began, its scan taken (the mesh made) or its bake taken (the candidate judged, and adopted or not) when it came back.
            for (int i = _fusions.Count - 1; i >= 0; i--)
            {
                FusionWork w = _fusions[i];
                if (!w.offered) { if (!MayStart()) return; Offer(w); continue; }
                if (!w.collected) continue;
                if (w.cancelled && !w.Begun)
                {
                    // Cancelled while it waited (a stop, a reclassification): the inputs are ours again; offered again.
                    w.Reset(w.stage);
                    w.reoffers++;
                    Reoffers++;
                    continue;
                }

                if (!MayStart()) return;
                if (w.stage == FusionStage.Scanning)
                {
                    FusionWorkerSeconds += w.scanSeconds;
                    long takeBegin = Stopwatch.GetTimestamp();
                    if (!TakeScan(w)) _fusions.RemoveAt(i);   // dropped or refused: over
                    Unit("mesh preparation", takeBegin);
                    continue;
                }

                FusionBakeSeconds += w.bakeSeconds;
                FusionBakes++;
                MeshesBaked++;
                _fusions.RemoveAt(i);
                long adoptBegin = Stopwatch.GetTimestamp();
                Adopt(w);
                Unit("hull exchange", adoptBegin);
            }

            // The buildings whose deadline passed, or which a next Slash reached: the idle resting groups (fixed or held) of
            // each are the candidate's participants -- into the anchored one, else the heaviest. Nothing is fixed in the air
            // for a deadline: a free group waits for its rest. A class whose candidate was not adopted is not tried again
            // until the class changes; the wait ends.
            for (int i = _waits.Count - 1; i >= 0; i--)
            {
                BuildingWait w = _waits[i];
                if (!IsDue(w)) continue;
                if (!TryClassify(w.building, _kinematicScratch, _freeScratch)) continue;   // a group cutting or a candidate in flight: after it
                if (_kinematicScratch.Count + _freeScratch.Count <= 1) { _waits.RemoveAt(i); continue; }
                if (_kinematicScratch.Count < 2) { FusionWaitsMixed++; continue; }   // a free group waits for its rest: nothing is fixed in the air, nothing free is fused
                if (IsRejectedNow(w.building)) { _waits.RemoveAt(i); continue; }   // the class as it stands was judged: apart, each cuttable, until it changes
                if (HasCandidateInFlight(w.building)) continue;
                if (!MayStart()) return;
                HullGroup target = null;
                foreach (HullGroup g in _kinematicScratch) if (target == null || (g.Anchored && !target.Anchored) || (g.Anchored == target.Anchored && g.Mass > target.Mass)) target = g;
                if (w.nextSlash) UnionsByNextSlash++; else UnionsByDeadline++;
                AggregationsBegun++;
                BeginCandidate(_kinematicScratch, target, w.nextSlash ? "next Slash" : "deadline", false);
                _waits.RemoveAt(i);   // what is left apart (one fixed, one free) is not waited for; the next cut waits again
            }

            // A refused scan of a class still standing: tried again after the deadline's wait, unless given up.
            double now = _realSeconds();
            _aggregationScratch.Clear();
            foreach (KeyValuePair<int, AggregationState> p in _aggregations) if (!p.Value.Rejected && p.Value.refusals > 0 && now >= p.Value.retryAt) _aggregationScratch.Add(p.Key);
            foreach (int b in _aggregationScratch)
            {
                AggregationState a = _aggregations[b];
                if (HasCandidateInFlight(b) || !TryClassify(b, _kinematicScratch, _freeScratch) || _kinematicScratch.Count < 2 || ClassKey(_kinematicScratch) != a.key) continue;
                if (!MayStart()) return;
                HullGroup target = null;
                foreach (HullGroup g in _kinematicScratch) if (target == null || (g.Anchored && !target.Anchored) || (g.Anchored == target.Anchored && g.Mass > target.Mass)) target = g;
                FusionsRetried++;
                a.retryAt = double.PositiveInfinity;
                BeginCandidate(_kinematicScratch, target, "retry " + a.refusals + " of 3", true);
            }
        }

        /// <summary>The candidate: the convex hull of every participant's hull in the target's frame, to be scanned off Main; the participants are Fusing until it is adopted, refused or dropped (a hit on one is held for what results).</summary>
        private void BeginCandidate(List<HullGroup> cls, HullGroup target, string why, bool retry)
        {
            var points = new List<float3>();
            float4x4 fromWorld = math.inverse((float4x4)target.Root.transform.localToWorldMatrix);
            var work = new FusionWork(points) { target = target, building = target.Building, key = ClassKey(cls), retry = retry, cooking = Cooking, stage = FusionStage.Scanning, vertexLimit = _vertexLimit };
            if (_aggregations.TryGetValue(target.Building, out AggregationState judged) && judged.Rejected && judged.key == work.key) CandidatesForJudgedClass++;   // never expected: a class judged apart is not tried again
            foreach (HullGroup g in cls)
            {
                float4x4 toTarget = ReferenceEquals(g, target) ? float4x4.identity : math.mul(fromWorld, (float4x4)g.Root.transform.localToWorldMatrix);
                HullBrep.CopyVertices(g.Shape.BankOf(0), g.Shape.Convex(0), points, toTarget);
                work.participants.Add(new Participant(g, toTarget));
                g.State = HullGroupState.Fusing;
            }

            FusionsBegun++;
            if (bumpGenerationAfterFusionSnapshotForTest) target.Generation++;
            _fusions.Add(work);
            Offer(work);   // no room: offered again next Step; nothing runs on Main
            Record("candidate for building " + target.Building + " t " + _physicsSeconds().ToString("F3") + ": groups [" + work.Names + "] into " + target.Id + " (" + points.Count + " points; " + why + ")");
        }

        /// <summary>
        /// Whether every participant still is what the candidate was made of: standing, Fusing for this candidate, at the
        /// generation scanned, kinematic, and at the same pose relative to the target (a released group fell; a group
        /// cut is another generation). False names the first difference.
        /// </summary>
        private static bool Verify(FusionWork w, out string why)
        {
            why = null;
            HullGroup target = w.target;
            if (target.State == HullGroupState.Gone || target.Root == null) { why = "the target group " + target.Id + " is gone"; return false; }
            float4x4 fromWorld = math.inverse((float4x4)target.Root.transform.localToWorldMatrix);
            foreach (Participant p in w.participants)
            {
                HullGroup g = p.group;
                if (g.State == HullGroupState.Gone || g.Root == null) { why = "group " + g.Id + " is gone"; return false; }
                if (g.State != HullGroupState.Fusing) { why = "group " + g.Id + " is " + g.State + ", not fusing"; return false; }
                if (g.Generation != p.generation) { why = "group " + g.Id + " is at generation " + g.Generation + ", scanned at " + p.generation; return false; }
                if (!g.Kinematic) { why = "group " + g.Id + " is not kinematic any more (released)"; return false; }
                float4x4 now = ReferenceEquals(g, target) ? float4x4.identity : math.mul(fromWorld, (float4x4)g.Root.transform.localToWorldMatrix);
                float moved = math.length(now.c3.xyz - p.toTarget.c3.xyz);
                float turned = math.max(math.max(math.length(now.c0.xyz - p.toTarget.c0.xyz), math.length(now.c1.xyz - p.toTarget.c1.xyz)), math.length(now.c2.xyz - p.toTarget.c2.xyz));
                if (moved > 1e-3f || turned > 1e-3f) { why = "group " + g.Id + " moved relative to the target (" + moved.ToString("E2") + " m, " + turned.ToString("E2") + ")"; return false; }
            }

            return true;
        }

        /// <summary>Every participant of a candidate that is over (adopted, refused or dropped) is idle again.</summary>
        private static void ReleaseParticipants(FusionWork w)
        {
            foreach (Participant p in w.participants) if (p.group.State == HullGroupState.Fusing) p.group.State = HullGroupState.Idle;
        }

        /// <summary>A candidate whose participants changed under it: dropped, everything as it stands; the building is looked at again (a new candidate of the class as it is now, by the cut's clock).</summary>
        private void DropCandidate(FusionWork w, string why)
        {
            FusionsStale++;
            if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }
            ReleaseParticipants(w);
            Record("candidate for building " + w.building + " [" + w.Names + "] dropped: " + why);
            Reevaluate(w.building, "candidate dropped: " + why);
        }

        /// <summary>A scanned hull taken (Main): the participants verified; the mesh is made from the arrays and the work offered again for its bake. False when the work is over (dropped or refused).</summary>
        private bool TakeScan(FusionWork w)
        {
            if (!Verify(w, out string why)) { DropCandidate(w, why + " (at the scan)"); return false; }
            if (refuseHullForTest) { w.vertices = null; w.report.reason = "refused (test)"; }
            if (w.vertices == null || w.vertices.Length > _vertexLimit)
            {
                RefuseCandidate(w, w.vertices == null ? w.report.reason : w.vertices.Length + " vertices past the limit " + _vertexLimit, w.points);
                return false;
            }

            long begin = Stopwatch.GetTimestamp();
            ConvexHullBuilder.ColliderArrays(w.vertices, w.faceOffsets, w.faceIndices, out Vector3[] meshVertices, out int[] triangles);
            w.mesh = new Mesh { name = "Building hull " + w.target.Id + " g" + (w.target.Generation + 1), hideFlags = HideFlags.HideAndDontSave };
            w.mesh.vertices = meshVertices;
            w.mesh.triangles = triangles;
            w.mesh.RecalculateBounds();
            w.meshId = w.mesh.GetEntityId();
            MeshPrepareSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            w.Reset(FusionStage.Baking);
            Offer(w);
            return true;
        }

        /// <summary>
        /// A baked candidate judged (Main): the participants verified again; the mesh put on the target's collider and
        /// checked by PhysX; the penetration into everything outside the participants measured before (each participant's
        /// hull) and after (the candidate) at the same pose; a penetration added past the settings' depth, or one that
        /// cannot be verified, keeps every group as it is (one hull not achieved for this class, recorded with the
        /// opponent); else the bodies merge into the target and the candidate is its hull.
        /// </summary>
        private void Adopt(FusionWork w)
        {
            long begin = Stopwatch.GetTimestamp();
            if (!Verify(w, out string why))
            {
                DropCandidate(w, why + " (at the adoption)");
                return;
            }

            HullGroup g = w.target;
            HullBrep brep = HullBrep.FromArrays(w.vertices, w.faceOffsets, w.faceIndices);
            double volumeBefore = 0.0;
            foreach (Participant p in w.participants) volumeBefore += MassOf(p.group.Shape).volume;
            bool verifiable = MeasurePenetrations(w, _penetrationBefore, null);
            float penetrationBefore = 0f;
            foreach (KeyValuePair<Collider, float> p in _penetrationBefore) penetrationBefore = Mathf.Max(penetrationBefore, p.Value);
            Mesh oldMesh = g.Collider.sharedMesh;
            g.Collider.sharedMesh = w.mesh;
            if (g.Collider.GeometryHolder.Type != UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh)
            {
                g.Collider.sharedMesh = oldMesh;
                brep.Dispose();
                FusionHullRefusals++;
                RefuseCandidate(w, "PhysX refused the fused hull's mesh");
                HullExchangeSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                return;
            }

            verifiable &= MeasurePenetrations(w, _penetrationAfter, g.Collider);
            string worstOpponent = null; float worstNew = 0f, worstBefore = 0f, worstAfter = 0f;
            foreach (KeyValuePair<Collider, float> p in _penetrationAfter)
            {
                _penetrationBefore.TryGetValue(p.Key, out float before);
                float added = p.Value - before;
                if (added > worstNew) { worstNew = added; worstOpponent = OpponentOf(w, p.Key); worstBefore = before; worstAfter = p.Value; }
            }

            MaxPenetrationBeforeExchange = Mathf.Max(MaxPenetrationBeforeExchange, penetrationBefore);
            if (!verifiable || worstNew > _settings.maxNewPenetrationMetres)
            {
                g.Collider.sharedMesh = oldMesh;
                PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null;
                brep.Dispose();
                ReleaseParticipants(w);
                AggregationState a = StateOf(w.building, w.key);
                a.notAchieved = true;
                a.since = _realSeconds();
                a.retryAt = double.PositiveInfinity;
                a.why = !verifiable ? "the penetration could not be verified (more colliders near than the query holds)"
                    : "the fused hull would penetrate " + worstOpponent + " by " + worstAfter.ToString("F3") + " m (" + worstBefore.ToString("F3") + " m before the exchange, " + worstNew.ToString("F3") + " m added, allowed " + _settings.maxNewPenetrationMetres.ToString("F3") + ")";
                HullsNotAchieved++;
                MaxRejectedNewPenetration = Mathf.Max(MaxRejectedNewPenetration, worstNew);
                _notAchieved.Add("building " + w.building + " class [" + w.Names + "] (" + w.participants.Count + " hulls, " + w.vertices.Length + " vertices): " + a.why);
                if (_penetrationRecords.Count < 256) _penetrationRecords.Add("building " + w.building + " class [" + w.Names + "] NOT ADOPTED (" + w.participants.Count + " hulls -> candidate " + w.vertices.Length + " v): " + DescribePenetrations(w) + (verifiable ? "" : " [unverifiable]"));
                Record("building " + w.building + " one hull not achieved t " + _physicsSeconds().ToString("F3") + " for the class [" + w.Names + "]: " + a.why + "; the groups stay apart, each its own hull, each cuttable");
                HullExchangeSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                return;
            }

            LastPenetrationComparison = "building " + w.building + " class [" + w.Names + "]: " + DescribePenetrations(w);
            if (_penetrationRecords.Count < 256) _penetrationRecords.Add("building " + w.building + " class [" + w.Names + "] adopted (" + w.participants.Count + " hulls -> " + w.vertices.Length + " v): " + DescribePenetrations(w));

            // Adopted: the target takes the candidate as its hull, and every other participant merges into it (one section; nothing below refuses).
            PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(brep.Bank, new[] { brep.Range }, new List<Mesh> { w.mesh }, PhysicsShapeSource.External(), float4x4.identity);
            Mesh mesh = w.mesh;
            w.mesh = null;
            ReplaceShape(g, shape, brep, mesh);
            g.HullIsFused = true;
            foreach (Participant p in w.participants) StopPairsOf(p.group, "merged at an adoption");
            foreach (Participant p in w.participants)
            {
                if (ReferenceEquals(p.group, g)) continue;
                long mergeBegin = Stopwatch.GetTimestamp();
                Merge(p.group, g);
                Unit("union", mergeBegin);
            }

            g.State = HullGroupState.Idle;
            g.Generation++;
            if (_aggregations.TryGetValue(w.building, out AggregationState ended)) EndAggregationState(ended);
            if (_lastStopAt.TryGetValue(w.building, out double stoppedAt)) { _stopToFusion.Add(_realSeconds() - stoppedAt); _lastStopAt.Remove(w.building); }
            Fusions++;
            double volumeAfter = brep.Mass().volume;
            double overhang = volumeAfter - volumeBefore;
            OverhangVolume += Math.Max(0.0, overhang);
            MaxOverhangVolume = Math.Max(MaxOverhangVolume, overhang);
            MaxFusionExpansion = Math.Max(MaxFusionExpansion, w.report.expansion);
            MaxFusionShrink = Math.Max(MaxFusionShrink, w.report.shrink);
            float penetration = worstAfter;
            foreach (KeyValuePair<Collider, float> p in _penetrationAfter) penetration = Mathf.Max(penetration, p.Value);
            MaxFusionPenetration = Mathf.Max(MaxFusionPenetration, penetration);
            MaxNewPenetration = Mathf.Max(MaxNewPenetration, worstNew);
            NoteHullSize(g);
            NoteLiveHulls();
            HullExchangeSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            Record("group " + g.Id + " fused t " + _physicsSeconds().ToString("F3") + ": one hull of " + brep.VertexCount + " vertices, " + brep.FaceCount + " faces (generation " + g.Generation + ") from " + w.participants.Count + " groups; volume " + volumeBefore.ToString("F4") + " -> " + volumeAfter.ToString("F4")
                + " (filled " + overhang.ToString("F4") + "; envelope expansion " + w.report.expansion.ToString("E2") + " m, shrink " + w.report.shrink.ToString("E2") + " m); deepest penetration before " + penetrationBefore.ToString("F4") + " m, after " + penetration.ToString("F4") + " m (same pose); members " + g.MemberCount + "; scan " + (w.scanSeconds * 1000).ToString("F3") + " ms, bake " + (w.bakeSeconds * 1000).ToString("F3") + " ms off Main");
        }

        /// <summary>
        /// A participant merges into the target at the adoption (both kinematic, where they stand): its display members,
        /// anchors, consumed Slashes and mass go onto the target's body; the rest is told the body that goes is fused into
        /// the one that stays; its Root, body, collider, shape and mesh go.
        /// </summary>
        private void Merge(HullGroup from, HullGroup into)
        {
            long begin = Stopwatch.GetTimestamp();
            Transform intoRoot = into.Root.transform;
            float4x4 toGroup = math.mul(math.inverse((float4x4)intoRoot.localToWorldMatrix), (float4x4)from.Root.transform.localToWorldMatrix);
            foreach (HullGroup.DisplayMember m in from.Members) { m.root.transform.SetParent(intoRoot, true); into.Members.Add(m); }
            from.Members.Clear();
            foreach (float3 a in from.Anchors) into.Anchors.Add(math.transform(toGroup, a));
            foreach (KeyValuePair<long, SlashReservation> r in from.Reserved) if (!into.Reserved.ContainsKey(r.Key)) into.Reserved[r.Key] = r.Value;
            double m1 = into.Mass, m2 = from.Mass, total = m1 + m2;
            double3 c1 = into.Centre, c2 = math.transform(toGroup, from.Centre), centre = (m1 * c1 + m2 * c2) / total;
            SymmetricMatrix3 sum = InertiaAbout(into.Inertia, into.InertiaRotation, m1, c1 - centre) + InertiaAbout(from.Inertia, math.mul(new quaternion(new float3x3(toGroup)), from.InertiaRotation), m2, c2 - centre);
            into.Mass = total;
            into.Centre = (float3)centre;
            if (PrincipalInertia.TryDiagonalize(in sum, out double3 moments, out quaternion rotation) && math.all(moments > 0.0)) { into.Inertia = (float3)moments; into.InertiaRotation = rotation; }
            ApplyMass(into);

            // The rest: the body that goes is fused into the one that stays (its outside supports and what stood on it pass over; nothing is woken).
            _colliderScratch.Clear();
            _colliderScratch.Add(into.Collider);
            _rest.FuseIntoGroup(from.Body, into.Body, into.Building, _colliderScratch, into.Anchored);

            from.State = HullGroupState.Gone;
            _byId.Remove(from.Id);
            _groups.Remove(from);
            PhysicsOwnerBuilder.DestroyObject(from.Root);
            from.Root = null; from.Body = null; from.Collider = null;
            from.Shape?.Dispose();
            from.OwnedBrep?.Dispose();
            if (from.OwnedMesh != null) PhysicsCutCook.DestroyMesh(from.OwnedMesh);
            from.Shape = null; from.OwnedBrep = null; from.OwnedMesh = null;
            BodiesDestroyed++;
            Unions++;
            UniteSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            Record("group " + from.Id + " merged into " + into.Id + " at the adoption t " + _physicsSeconds().ToString("F3") + " (members " + into.MemberCount + ", mass " + total.ToString("F3") + ")");
        }

        private static SymmetricMatrix3 InertiaAbout(float3 principal, quaternion axes, double mass, double3 offset)
        {
            double3x3 r = new double3x3(new float3x3(axes));
            double3x3 local = math.mul(math.mul(r, new double3x3(principal.x, 0, 0, 0, principal.y, 0, 0, 0, principal.z)), math.transpose(r));
            double d2 = math.dot(offset, offset);
            return new SymmetricMatrix3
            {
                xx = local.c0.x + mass * (d2 - offset.x * offset.x), yy = local.c1.y + mass * (d2 - offset.y * offset.y), zz = local.c2.z + mass * (d2 - offset.z * offset.z),
                xy = local.c1.x - mass * offset.x * offset.y, xz = local.c2.x - mass * offset.x * offset.z, yz = local.c2.y - mass * offset.y * offset.z,
            };
        }

        /// <summary>A candidate the builder (or PhysX) refused: the groups stay apart, each its own hull; scanned again after the deadline -- three times for the same class; then the class is given up (a failure completion, recorded with the scan's input), until the class changes.</summary>
        private void RefuseCandidate(FusionWork w, string why, IReadOnlyList<float3> points = null)
        {
            if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }
            ReleaseParticipants(w);
            FusionsRefused++;
            AggregationState a = StateOf(w.building, w.key);
            a.refusals++;
            if (a.refusals >= 3)
            {
                a.givenUp = true;
                a.why = why;
                a.since = _realSeconds();
                a.retryAt = double.PositiveInfinity;
                FusionsGivenUp++;
                var text = new System.Text.StringBuilder("building " + w.building + " class [" + w.Names + "]: the candidate of its " + w.participants.Count + " hulls was refused " + a.refusals + " times (" + why + "); the groups stay apart, each cuttable, until the class changes");
                if (points != null)
                {
                    // The scan's input, exactly, so that the case can be replayed in an Editor test.
                    text.Append("; points ").Append(points.Count).Append(':');
                    foreach (float3 p in points) text.Append(' ').Append(p.x.ToString("R")).Append(',').Append(p.y.ToString("R")).Append(',').Append(p.z.ToString("R"));
                }

                _failures.Add(text.ToString());
                Record("candidate for building " + w.building + " [" + w.Names + "] refused: " + why + " (given up after " + a.refusals + " refusals; the groups stay apart, each cuttable)");
                return;
            }

            a.retryAt = _realSeconds() + Math.Max(_settings.deadlineSeconds, 0.1);
            Record("candidate for building " + w.building + " [" + w.Names + "] refused: " + why + " (the groups stay apart; tried again after the deadline, " + a.refusals + " of 3)");
        }

        private readonly List<Collider> _colliderScratch = new List<Collider>(8);
        private readonly Collider[] _near = new Collider[1024];
        private readonly Dictionary<Collider, float> _penetrationBefore = new Dictionary<Collider, float>(), _penetrationAfter = new Dictionary<Collider, float>();

        /// <summary>
        /// The penetration of the participants' hulls (each's collider; or one collider given, the candidate on the
        /// target's) into each collider outside every participant, at the current pose, by opponent. False when the query
        /// could not hold every collider near (unverifiable).
        /// </summary>
        private bool MeasurePenetrations(FusionWork w, Dictionary<Collider, float> into, Collider only)
        {
            long begin = Stopwatch.GetTimestamp();
            into.Clear();
            bool verifiable = true;
            if (only != null) verifiable &= MeasurePenetrationsOf(w, only, into);
            else foreach (Participant p in w.participants) verifiable &= MeasurePenetrationsOf(w, p.group.Collider, into);
            double s = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            PenetrationSeconds += s;
            MaxPenetrationSeconds = Math.Max(MaxPenetrationSeconds, s);
            return verifiable;
        }

        /// <summary>The Main time of the penetration measurements of the candidates (a part of the hull exchange), in all and the longest one.</summary>
        public double PenetrationSeconds { get; private set; }
        public double MaxPenetrationSeconds { get; private set; }

        private static bool IsParticipant(FusionWork w, Collider other)
        {
            foreach (Participant p in w.participants) if (p.group.Root != null && other.transform.IsChildOf(p.group.Root.transform)) return true;
            return false;
        }

        private bool MeasurePenetrationsOf(FusionWork w, Collider own, Dictionary<Collider, float> into)
        {
            if (own == null) return true;
            Bounds b = own.bounds;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents + Vector3.one * 0.01f, _near, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
            bool verifiable = n < _near.Length;
            for (int i = 0; i < n; i++)
            {
                Collider other = _near[i];
                if (other == null || IsParticipant(w, other)) continue;
                if (!Physics.ComputePenetration(own, own.transform.position, own.transform.rotation, other, other.transform.position, other.transform.rotation, out _, out float depth)) continue;
                into.TryGetValue(other, out float had);
                into[other] = Mathf.Max(had, depth);
            }

            return verifiable;
        }

        /// <summary>What an opponent collider is, for the record: another group of the trial (by its body), a static collider (the floor, the city), or another body.</summary>
        private string OpponentOf(FusionWork w, Collider other)
        {
            if (other == null) return "(gone)";
            Rigidbody body = other.attachedRigidbody;
            if (body != null && TryGroupOf(body, out HullGroup otherGroup)) return "hull group " + otherGroup.Id + " (" + (otherGroup.Anchored ? "anchored" : otherGroup.Kinematic ? "held" : "free") + ", building " + otherGroup.Building + ")";
            if (body == null) return "static collider '" + other.name + "'";
            return "body '" + body.name + "'";
        }

        /// <summary>The penetration comparison of the last adopted exchange, by opponent: before and after, at the same pose.</summary>
        public string LastPenetrationComparison { get; private set; }

        private string DescribePenetrations(FusionWork w)
        {
            var text = new System.Text.StringBuilder();
            var opponents = new HashSet<Collider>(_penetrationBefore.Keys);
            foreach (Collider c in _penetrationAfter.Keys) opponents.Add(c);
            foreach (Collider c in opponents)
            {
                _penetrationBefore.TryGetValue(c, out float before); _penetrationAfter.TryGetValue(c, out float after);
                if (before <= 0f && after <= 0f) continue;
                if (text.Length > 0) text.Append("; ");
                text.Append(OpponentOf(w, c)).Append(' ').Append(before.ToString("F3")).Append(" -> ").Append(after.ToString("F3")).Append(" m");
            }

            return text.Length > 0 ? text.ToString() : "no penetration before or after";
        }

        private readonly List<string> _notAchieved = new List<string>();

        /// <summary>Real seconds buildings stood with a class whose candidate was not adopted (apart, each group cuttable): the total over ended classes, the longest (a class still standing included), and the classes standing now.</summary>
        public double NotAchievedSeconds { get; private set; }
        private double _maxNotAchievedEnded;
        public double MaxNotAchievedSeconds { get { double m = _maxNotAchievedEnded, now = _realSeconds(); foreach (AggregationState a in _aggregations.Values) if (a.Rejected && !double.IsNaN(a.since)) m = Math.Max(m, now - a.since); return m; } }
        public double NotAchievedSecondsNow { get { double now = _realSeconds(), s = 0.0; foreach (AggregationState a in _aggregations.Values) if (a.Rejected && !double.IsNaN(a.since)) s += now - a.since; return s; } }

        /// <summary>Candidates made but not adopted ("one hull not achieved" for the class), with their opponent: a record, not a failure.</summary>
        public IReadOnlyList<string> NotAchieved => _notAchieved;
        public int HullsNotAchieved { get; private set; }
        public float MaxRejectedNewPenetration { get; private set; }
        private int FreeGroupsOf(int building) { int n = 0; foreach (HullGroup g in _groups) if (g.Building == building && g.State != HullGroupState.Gone && !g.Kinematic) n++; return n; }

        /// <summary>Candidates made for a class already judged apart (not achieved or given up): an unneeded retry; zero expected.</summary>
        public int CandidatesForJudgedClass { get; private set; }

        /// <summary>Whether a group stands resting in its building's class judged apart (its candidate not adopted): the groups kept as they were after a rejection.</summary>
        public bool IsApartAfterRejection(HullGroup g) => g != null && g.State != HullGroupState.Gone && g.Kinematic && IsRejectedNow(g.Building);

        /// <summary>Buildings whose resting class, as it stands, was judged not achieved / given up / has a refused scan awaiting its retry.</summary>
        public int BuildingsNotAchievedNow { get { int n = 0; foreach (KeyValuePair<int, AggregationState> p in _aggregations) if (p.Value.notAchieved && p.Value.key == CurrentRestingKey(p.Key)) n++; return n; } }
        public int BuildingsGivenUpNow { get { int n = 0; foreach (KeyValuePair<int, AggregationState> p in _aggregations) if (p.Value.givenUp && p.Value.key == CurrentRestingKey(p.Key)) n++; return n; } }
        public int BuildingsAwaitingRetry { get { int n = 0; foreach (int b in _aggregations.Keys) if (IsRetryPending(b)) n++; return n; } }

        /// <summary>Each building's standing class outcome, for the record.</summary>
        public string DescribeAggregationStates()
        {
            var text = new System.Text.StringBuilder();
            foreach (KeyValuePair<int, AggregationState> p in _aggregations)
            {
                AggregationState a = p.Value;
                if (text.Length > 0) text.Append("; ");
                text.Append("building ").Append(p.Key).Append(" class [").Append(a.key).Append("] ").Append(a.givenUp ? "given up" : a.notAchieved ? "not achieved" : "awaiting a retry (" + a.refusals + " refusals)")
                    .Append(a.key == CurrentRestingKey(p.Key) ? " (standing" : " (the class changed").Append(a.Rejected && !double.IsNaN(a.since) ? ", " + (_realSeconds() - a.since).ToString("F2") + " s)" : ")").Append(a.why != null ? ": " + a.why : "");
            }

            return text.Length > 0 ? text.ToString() : "none";
        }

        /// <summary>
        /// One hull achieved, on the real counts (apart from the processing's end): every building at most one fixed and one
        /// free group, every group one hull on its own body with one collider. A building whose resting groups stand apart
        /// (its candidate not adopted) leaves this false.
        /// </summary>
        public bool IsOneHullAchieved
        {
            get
            {
                var fixedOf = new Dictionary<int, int>(); var freeOf = new Dictionary<int, int>();
                foreach (HullGroup g in _groups)
                {
                    if (g.State == HullGroupState.Gone) continue;
                    if (g.Body == null || g.Collider == null) return false;
                    Dictionary<int, int> d = g.Kinematic ? fixedOf : freeOf;
                    d.TryGetValue(g.Building, out int n); d[g.Building] = n + 1;
                    if (n + 1 > 1) return false;
                }

                return true;
            }
        }

        /// <summary>The bodies, hulls and colliders of the groups now, for a record: "groups G (fixed F, free R), hulls H, bodies B, colliders C".</summary>
        public string DescribeCounts()
        {
            int groups = 0, fixedGroups = 0, freeGroups = 0, hulls = 0, bodies = 0;
            foreach (HullGroup g in _groups) { if (g.State == HullGroupState.Gone) continue; groups++; if (g.Kinematic) fixedGroups++; else freeGroups++; hulls += g.HullCount; if (g.Body != null) bodies++; }
            return "groups " + groups + " (fixed " + fixedGroups + ", free " + freeGroups + "), hulls " + hulls + ", bodies " + bodies + ", colliders " + LiveColliders;
        }

        /// <summary>Every exchange judged, adopted or not: the building, its class, the decision and each opponent's penetration before and after (metres), for the comparison later.</summary>
        public IReadOnlyList<string> PenetrationRecords => _penetrationRecords;
        private readonly List<string> _penetrationRecords = new List<string>();

        /// <summary>The fastest a free group moved (m/s), seen at the Steps, and where: an abnormal speed after an exchange or a cut would show here.</summary>
        public float MaxGroupSpeed { get; private set; }
        public string MaxGroupSpeedAt { get; private set; }

        private void NoteSpeeds()
        {
            foreach (HullGroup g in _groups)
            {
                if (g.State == HullGroupState.Gone || g.Kinematic || g.Body == null) continue;
                float speed = g.Body.linearVelocity.magnitude;
                if (speed > MaxGroupSpeed) { MaxGroupSpeed = speed; MaxGroupSpeedAt = "group " + g.Id + " generation " + g.Generation + " t " + _physicsSeconds().ToString("F3") + " (mass " + g.Mass.ToString("R") + ", body " + g.Body.mass.ToString("R") + ", inertia " + g.Body.inertiaTensor.ToString("G9") + ", group inertia " + g.Inertia + ")"; }
            }
        }

        /// <summary>Whether every group stands at rest (fixed or held): a state apart from the trial's completion.</summary>
        public bool AllGroupsAtRest { get { foreach (HullGroup g in _groups) if (g.State != HullGroupState.Gone && !g.Kinematic) return false; return true; } }

        // ---- the held hits, routed to what a cut or a candidate left, never past a candidate still to be judged ----

        private void RouteHeld()
        {
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                HeldHit h = _held[i];
                if (!MayStart()) return;
                if (BuildingBusy(h.group.Building)) continue;   // a cut, a candidate or a due class of the building first
                _held.RemoveAt(i);
                // Resumed only where its own reservation stands: the group it was held on, or what that group became (a
                // child, a merge); a group reserved for the Slash by another request is another request's.
                HullGroup target = null;
                foreach (HullGroup g in _groups)
                {
                    if (g.State != HullGroupState.Idle || g.Building != h.group.Building) continue;
                    if (!g.Reserved.TryGetValue(h.slashId, out SlashReservation r) || !ReferenceEquals(r, h.hit.reservation)) continue;
                    float4 planeGroup = BuildingFusion.TransformPlane(math.inverse((float4x4)g.Root.transform.localToWorldMatrix), h.planeWorld);
                    g.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                    float3 c = (lo + hi) * 0.5f, e = (hi - lo) * 0.5f;
                    float centre = math.dot(planeGroup.xyz, c) + planeGroup.w, reach = math.abs(planeGroup.x) * e.x + math.abs(planeGroup.y) * e.y + math.abs(planeGroup.z) * e.z;
                    if (math.abs(centre) <= reach) { target = g; break; }
                }

                if (target != null && !PassesLimit(target, h.hit, out string limitRefusal))
                {
                    RefuseByLimit(h.hit, limitRefusal);   // resumed from the settled n: a stopped building, n = 0, or n >= N
                    continue;
                }

                if (target == null)
                {
                    const string why = "Dropped: no idle hull of the building reserved for this request is crossed by the held plane";
                    Finish(h.hit, why, false);
                    Record("hit " + h.hit.id + " dropped after its hold: " + why);
                    continue;
                }

                h.hit.group = target.Id; h.hit.generation = target.Generation;
                HitsResumed++;
                float4 plane = BuildingFusion.TransformPlane(math.inverse((float4x4)target.Root.transform.localToWorldMatrix), h.planeWorld);
                plane /= math.length(plane.xyz);
                h.hit.state = "prepared";
                target.State = HullGroupState.Cutting;
                _cuts.Add(new PendingCut { group = target, generation = target.Generation, hit = h.hit, planeGroup = plane, planeWorld = h.planeWorld, stage = CutStage.Preparing });
                Record("hit " + h.hit.id + " resumed on group " + target.Id);
            }
        }

        // ---- the record ----

        public int GroupsMade { get; private set; }
        public int GroupCount { get { int n = 0; foreach (HullGroup g in _groups) if (g.State != HullGroupState.Gone) n++; return n; } }
        public int GroupCuts { get; private set; }
        public int CutsFailed { get; private set; }
        public int HullRefusals { get; private set; }
        public int NoChanges { get; private set; }
        public int CookRequests { get; private set; }
        public int HullChecks { get; private set; }
        public int MeshesBaked { get; private set; }   // the cook's child meshes (two a cut), the registrations' and the candidates'
        public int FusionBakes { get; private set; }
        public int DisplayCuts { get; private set; }
        public int DisplayCutsRefused { get; private set; }
        public int DisplayMembersScanned { get; private set; }
        public int MembersWithoutGeometry { get; private set; }   // members whose display cut ended without a commit: on no side
        public int SideLookups { get; private set; }   // reads of a prepared member side at the publications: one a member (linear, never quadratic)
        public int RoomWaits { get; private set; }
        public int HitsPublished { get; private set; }
        public int HitsRefused { get; private set; }
        public int HitsHeld { get; private set; }
        public int HitsResumed { get; private set; }
        public int GroupsHeld { get; private set; }   // by the building rest (its clock and support)
        public int GroupsReleased { get; private set; }   // by the building rest (a support lost)
        /// <summary>Merges of a participant's body into the target at an adoption (a candidate of n groups adopted is n-1 unions).</summary>
        public int Unions { get; private set; }
        /// <summary>Aggregations begun by the deadline / by a next Slash (one a class of resting groups; the unions happen only if the candidate is adopted).</summary>
        public int UnionsByDeadline { get; private set; }
        public int UnionsByNextSlash { get; private set; }
        /// <summary>Aggregations begun: one a class of resting groups whose candidate is made. Each ends adopted (fused), one hull not achieved, given up, or dropped (its participants changed under it; the building is looked at again).</summary>
        public int AggregationsBegun { get; private set; }
        public int AggregationsEnded => Fusions + HullsNotAchieved + FusionsGivenUp + FusionsStale;
        public int MaxGroups { get; private set; }
        public int FusionsBegun { get; private set; }
        public int Fusions { get; private set; }
        public int FusionsRefused { get; private set; }
        /// <summary>Candidates dropped because a participant changed under them (cut, released, moved, gone): at the scan or at the adoption.</summary>
        public int FusionsStale { get; private set; }
        public int FusionsRetried { get; private set; }
        public int FusionsGivenUp { get; private set; }
        public int FusionHullRefusals { get; private set; }
        public int FusionWaitsMixed { get; private set; }
        public int OffersRefused { get; private set; }
        public int Reoffers { get; private set; }
        public int HullExchanges { get; private set; }
        public int CollidersMade { get; private set; }
        public int BodiesMade { get; private set; }
        public int BodiesDestroyed { get; private set; }
        public int MaxVertices { get; private set; }
        public int MaxFaces { get; private set; }
        public int MaxLiveHulls { get; private set; }
        public int Deferred { get; private set; }
        public int OverrunFrames { get; private set; }
        public double OverrunSeconds { get; private set; }
        public double StepSeconds { get; private set; }
        public double MaxStepSeconds { get; private set; }
        public double HitSeconds { get; private set; }
        public double PrepareSeconds { get; private set; }
        public double PrepareDisplaySeconds { get; private set; }   // of the preparation: the display members' vertex scans
        public double PublishSeconds { get; private set; }
        public double MaxPublishSeconds { get; private set; }
        public double FirstPublishSeconds { get; private set; }
        public double MaxPublishSecondsAfterFirst { get; private set; }
        public double PublishPhysicsSeconds { get; private set; }
        public double PublishDisplaySeconds { get; private set; }
        public double UniteSeconds { get; private set; }
        public double FusionWorkerSeconds { get; private set; }
        public double FusionBakeSeconds { get; private set; }
        public double MeshPrepareSeconds { get; private set; }
        public double HullExchangeSeconds { get; private set; }
        public double AskToPublishSeconds { get; private set; }
        public double MaxAskToPublishSeconds { get; private set; }
        public double OverhangVolume { get; private set; }
        public double MaxOverhangVolume { get; private set; }
        public double MaxFusionExpansion { get; private set; }
        public double MaxFusionShrink { get; private set; }
        public float MaxFusionPenetration { get; private set; }
        public float MaxPenetrationBeforeExchange { get; private set; }
        public float MaxNewPenetration { get; private set; }   // after minus before, at the same pose: what the adopted hull alone added

        /// <summary>The Main time of the physics side (hits, classification, hull checks, the physics of publications, merges, mesh preparation, hull exchanges) and of the display side (member scans, display admissions and publications). The worker's scan and bake are not Main.</summary>
        public double MainPhysicsSeconds => HitSeconds + (PrepareSeconds - PrepareDisplaySeconds) + PublishPhysicsSeconds + UniteSeconds + MeshPrepareSeconds + HullExchangeSeconds;
        public double MainDisplaySeconds => PrepareDisplaySeconds + PublishDisplaySeconds;
        /// <summary>One collider a group, always: the groups standing.</summary>
        public int LiveColliders => GroupCount;
        public int LiveHulls => GroupCount;

        private readonly List<string> _events = new List<string>();
        private readonly List<string> _failures = new List<string>();
        public IReadOnlyList<string> Events => _events;
        public IReadOnlyList<string> Failures => _failures;

        private void NoteHullSize(HullGroup g)
        {
            MaxVertices = Math.Max(MaxVertices, g.VertexCount);
            MaxFaces = Math.Max(MaxFaces, g.FaceCount);
        }

        private void NoteLiveHulls()
        {
            MaxLiveHulls = Math.Max(MaxLiveHulls, LiveHulls);
            MaxGroups = Math.Max(MaxGroups, GroupCount);
        }

        private void Record(string line)
        {
            if (_events.Count < 4096) _events.Add(line);
        }

        /// <summary>
        /// Whether nothing waits to be processed: no cut, no candidate in flight, no held hit, no display operation still
        /// making its geometry, no class due that could be aggregated, no refused scan awaiting its retry. A pair apart with
        /// one side held and one free is a settled state, not a wait; so is a class whose candidate was not adopted (judged
        /// apart, IsOneHullAchieved false) until the class changes.
        /// </summary>
        public bool IsSettled
        {
            get
            {
                if (_cuts.Count > 0 || _fusions.Count > 0 || _held.Count > 0 || _displayOperations.Count > 0 || _pairs.Count > 0 || _animations.Count > 0 || _hullWorks.Count > 0 || _waitingRequest.Count > 0) return false;
                foreach (HullGroup g in _groups) if (g.State != HullGroupState.Gone && g.State != HullGroupState.Idle) return false;
                // The configuration itself, not only a wait: two resting groups of one building whose class was not judged
                // are not a completion, nor two free ones (they rest and are looked at again; the trial does not wait for
                // every group's sleep, only for the count).
                foreach (HullGroup g in _groups) if (g.State != HullGroupState.Gone && (HasUnitableClass(g.Building) || FreeGroupsOf(g.Building) >= 2 || IsRetryPending(g.Building))) return false;
                return true;
            }
        }

        /// <summary>Buildings whose groups stand apart as one fixed and one free (the normal resting state, not a wait): due, with nothing to unite.</summary>
        public int PairsApart { get { int n = 0; foreach (BuildingWait w in _waits) if (IsDue(w) && !HasDueUnion(w)) n++; return n; } }

        /// <summary>Whether a building's standing class was given up: settled, but a failure completion, never a normal one.</summary>
        public bool HasGivenUpFusions => BuildingsGivenUpNow > 0;

        /// <summary>Settled with nothing failed: what a check's pass condition asks.</summary>
        public bool IsSettledWithoutFailure => IsSettled && !HasGivenUpFusions && FusionsGivenUp == 0 && DisplayOperationsFailed == 0;

        public string DescribeUnsettled()
        {
            int notIdle = 0, due = 0, fixedGroups = 0, freeGroups = 0;
            foreach (HullGroup g in _groups) { if (g.State == HullGroupState.Gone) continue; if (g.State != HullGroupState.Idle) notIdle++; if (g.Kinematic) fixedGroups++; else freeGroups++; }
            foreach (BuildingWait w in _waits) if (HasDueUnion(w)) due++;
            return "cuts " + _cuts.Count + ", display drops running " + _animations.Count + ", hull updates running " + _hullWorks.Count + " (requests waiting " + _waitingRequest.Count + "), sides moving " + _pairs.Count + " (constraints live " + ConstraintsLive + "), candidates in flight " + _fusions.Count + ", held hits " + _held.Count + ", display operations open " + _displayOperations.Count + " (failed " + DisplayOperationsFailed + "), groups " + GroupCount + " (resting " + fixedGroups + ", free " + freeGroups + ") not idle " + notIdle
                + ", buildings whose standing class was judged apart: not achieved " + BuildingsNotAchievedNow + ", given up " + BuildingsGivenUpNow + " (a failure completion), awaiting a retry " + BuildingsAwaitingRetry
                + ", buildings due to aggregate " + due + " (apart, fixed and free: " + PairsApart + "), all groups at rest " + AllGroupsAtRest;
        }

        /// <summary>The end: the cuts abandoned, the candidates cancelled or let go to release themselves, every hit pending abandoned, every group's objects and shapes let go.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (PendingCut cut in _cuts)
            {
                if (cut.request != null && !cut.request.IsOver) _cook.Abandon(cut.request);
                else cut.request?.Products?.Dispose();
                cut.classification?.Dispose();
                if (cut.request != null) cut.group.Shape?.ReleaseFromWork();
                Finish(cut.hit, "Abandoned: the world ended", false);
            }

            _cuts.Clear();
            foreach (SiblingPair p in _pairs) RemoveConstraint(p);   // before any body goes
            DisposeKinematic();
            _pairs.Clear();
            foreach (FusionWork w in _fusions)
            {
                if (!w.offered || w.collected || _dispatcher.Cancel(w.ticket))
                {
                    if (w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }   // ours: not with the dispatcher
                    if (w.offered && !w.collected) FusionsCancelledAtEnd++;
                }
                else
                {
                    w.abandoned = true;   // running: its mesh goes when it is collected
                    if (w.collected && w.mesh != null) { PhysicsCutCook.DestroyMesh(w.mesh); w.mesh = null; }
                    FusionsAbandonedRunning++;
                }
            }

            _fusions.Clear();
            foreach (HeldHit h in _held) Finish(h.hit, "Abandoned: the world ended", false);
            _held.Clear();
            _waits.Clear();
            _aggregations.Clear();
            _displayOperations.Clear();
            foreach (HullGroup g in _groups)
            {
                g.Shape?.Dispose();
                g.OwnedBrep?.Dispose();
                if (g.OwnedMesh != null) PhysicsCutCook.DestroyMesh(g.OwnedMesh);
                g.Shape = null; g.OwnedBrep = null; g.OwnedMesh = null;
                g.State = HullGroupState.Gone;
                PhysicsOwnerBuilder.DestroyObject(g.Root);
                g.Root = null; g.Body = null; g.Collider = null;
            }

            _groups.Clear();
            _byId.Clear();
        }

        public int FusionsCancelledAtEnd { get; private set; }
        public int FusionsAbandonedRunning { get; private set; }
    }
}
