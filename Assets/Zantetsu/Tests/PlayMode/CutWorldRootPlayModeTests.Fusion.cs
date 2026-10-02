using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The building fusion trial (<see cref="BuildingFusion"/>, 2026-09-29) on the product's cut path: held building
    /// pieces fuse into one kinematic group body without their display or colliders moving; a cut asked of a member
    /// splits the group into two compound bodies and the crossed members commit; the sides rest, fuse again and can be
    /// cut again; anchors decide a side's fixity; a member's retirement and the world's ending give everything back.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private CutWorldRoot NewFusionWorld(float floorTop = -1f, int perFrame = 8)
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile =>
            {
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingWorldEnabled", false);
                SetPrivate(profile, "buildingFusionEnabled", true);
                SetPrivate(profile, "buildingFusionPerFrame", perFrame);
            });
            Assert.That(root.Fusion, Is.Not.Null, "the fusion is on");
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, floorTop - 0.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
            return root;
        }

        /// <summary>A building box with one anchor at a place of its own frame.</summary>
        private LogicalFragmentId AddAnchoredBuildingAt(CutWorldRoot root, Vector3 at, float3 anchorLocal)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Anchored Building"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, new[] { anchorLocal }, true, out LogicalFragmentId fragment);
            if (added) _registered.Add(actor);
            Assert.That(added, Is.True, "the anchored building was taken into the world");
            return fragment;
        }

        /// <summary>The upper of two sides by their Roots (a fused member has no body to read).</summary>
        private static LogicalFragmentId UpperByRoot(CutWorldRoot root, LogicalFragmentId[] sides)
        {
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner a) && root.Owners.TryGet(sides[1], out PhysicsFragmentOwner b), Is.True);
            // Both children of a cut stand at the source's placement: the colliders' bounds tell the sides apart.
            return ColliderBoundsOf(root, sides[0]).center.y > ColliderBoundsOf(root, sides[1]).center.y ? sides[0] : sides[1];
        }

        private static IEnumerator UntilFused(CutWorldRoot root, int pieces, string what)
        {
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Fusion.FusedPieces >= pieces || Time.realtimeSinceStartup > deadline, what);
            Assert.That(root.Fusion.FusedPieces, Is.GreaterThanOrEqualTo(pieces), what + ": fused pieces");
        }

        private static void WriteFusionRecord(CutWorldRoot root, string what)
        {
            BuildingFusion f = root.Fusion;
            TestContext.Out.WriteLine(what + ": groups " + f.GroupCount + " (made " + f.GroupsMade + ", merged " + f.GroupsMerged + "), fused pieces " + f.FusedPieces + ", group cuts " + f.GroupCuts + " (refused " + f.GroupCutsRefused
                + ", members classified " + f.MembersClassified + ", most in one " + f.MaxMembersInOneCut + ", crossed " + f.MembersSplit + "), finals " + f.FinalsPublished + " (failed " + f.FinalsFailed + "), sides dynamic/kinematic " + f.SidesDynamic + "/" + f.SidesKinematic
                + ", deferred fusions/merges " + f.FusionsDeferred + "/" + f.MergesDeferred + ", groups held/released " + f.GroupsHeld + "/" + f.GroupsReleased
                + "; Main ms: step total " + (f.StepSeconds * 1000).ToString("F3") + " max " + (f.MaxStepSeconds * 1000).ToString("F3") + ", request " + (f.RequestSeconds * 1000).ToString("F3") + "; parts: classify " + (f.ClassifySeconds * 1000).ToString("F3") + ", publish " + (f.PublishSeconds * 1000).ToString("F3") + " (max " + (f.MaxPublishSeconds * 1000).ToString("F3") + ", " + f.MaxCutMembers + " members), final "
                + (f.FinalSeconds * 1000).ToString("F3") + " (max " + (f.MaxFinalSeconds * 1000).ToString("F3") + "), union " + (f.UnionSeconds * 1000).ToString("F3") + ", mass " + (f.MassSeconds * 1000).ToString("F3") + " (max " + (f.MaxMassSeconds * 1000).ToString("F3") + ", " + f.MaxMassMembers + " members, wait " + (f.MassWaitSeconds * 1000).ToString("F3") + "), fuse+merge " + (f.FuseSeconds * 1000).ToString("F3")
                + "; worker " + (f.WorkerSeconds * 1000).ToString("F3") + "; budget deferred " + f.DeferredForBudget + " over " + f.OverrunFrames + " (" + (f.OverrunSeconds * 1000).ToString("F3") + " ms); preparations " + f.PreparationsMade + "/" + f.PreparationsPublished + "/" + f.PreparationsRefused + "/" + f.PreparationsStale + "/" + f.PreparationsAbandoned + "/" + f.PreparationsReoffered + " (made/published/refused/stale/abandoned/re-offered); hits " + f.Hits.Count + " published/refused/pending " + f.HitsPublished + "/" + f.HitsRefused + "/" + f.HitsPending + "; events prepared/attached/included/held(+again) " + f.HitsPrepared + "/" + f.HitsAttached + "/" + f.HitsIncluded + "/" + f.HitsHeld + "(+" + f.HitsHeldAgain + ")");
            foreach (FusedGroup g in f.Groups)
            {
                TestContext.Out.WriteLine("  group " + g.Key + ": members " + g.MemberCount + ", kinematic " + g.Kinematic + " (body " + (g.Body != null ? g.Body.isKinematic.ToString() : "none") + "), busy " + g.Busy + ", mass " + g.Mass.ToString("F3"));
            }

            foreach (string e in f.Events)
            {
                TestContext.Out.WriteLine("  " + e);
            }

            BuildingRest rest = root.Rest;
            TestContext.Out.WriteLine("  rest: tracked " + rest.TrackedDynamic + "+" + rest.TrackedAnchored + ", held " + rest.RestedNow + ", unsupported past timeout " + rest.UnsupportedPastTimeout + ", rests " + rest.KinematicRests);
            foreach (string e in rest.Events) TestContext.Out.WriteLine("  rest: " + e);
        }

        private static int RigidbodiesUnder(IEnumerable<LogicalFragmentId> fragments, CutWorldRoot root)
        {
            var seen = new HashSet<Rigidbody>();
            foreach (LogicalFragmentId f in fragments)
            {
                if (!root.Owners.TryGet(f, out PhysicsFragmentOwner owner) || owner.Root == null) continue;
                Rigidbody body = owner.Root.GetComponentInParent<Rigidbody>();
                if (body != null) seen.Add(body);
            }

            return seen.Count;
        }

        private static Matrix4x4 PlacementOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            Assert.That(root.Placement.TryGetGeometryLocalToWorld(fragment, default, 0f, out Matrix4x4 m), Is.Not.EqualTo(VpFragmentPlacementKind.Missing), "placement of " + fragment.value);
            return m;
        }

        private static Bounds ColliderBoundsOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner), Is.True);
            Bounds b = default; bool first = true;
            foreach (MeshCollider c in owner.Root.GetComponentsInChildren<MeshCollider>())
            {
                if (first) { b = c.bounds; first = false; } else b.Encapsulate(c.bounds);
            }

            Assert.That(first, Is.False, "piece " + fragment.value + " has colliders");
            return b;
        }

        private static void AssertSame(Matrix4x4 a, Matrix4x4 b, string what)
        {
            for (int i = 0; i < 16; i++) Assert.That(b[i], Is.EqualTo(a[i]).Within(1e-4f), what + " [" + i + "]");
        }

        /// <summary>
        /// **Held pieces at different poses fuse into one kinematic body; display and colliders stay where they were;
        /// the members do not move on their own.** Three building boxes, two turned, rest on the floor and are fused;
        /// every fragment's display placement and collider bounds are the same before and after; the three Roots stand
        /// under one Rigidbody, none of their own; they are still hit targets; a sphere dropped on them moves nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator Fusion_HeldPiecesFuseIntoOneBody_AndNothingOfTheDisplayOrCollidersMoves()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddBuilding(root, new Vector3(0f, 0f, 0f));
            BodyOf(root, building).rotation = Quaternion.Euler(0f, 30f, 0f);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            var quarters = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(1f, 0f, 0f, 0f), quarters);
            var pieces = new List<LogicalFragmentId> { lower, quarters[0], quarters[1] };
            // The fusion takes a held piece in the very turn the rest holds it (both in LateUpdate), so the placements
            // are read every frame and the ones read in the frame before the fusion are the "before".
            var placements = new Dictionary<LogicalFragmentId, Matrix4x4>();
            var bounds = new Dictionary<LogicalFragmentId, Bounds>();
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            while (root.Fusion.FusedPieces < 3 && Time.realtimeSinceStartup < deadline)
            {
                foreach (LogicalFragmentId f in pieces)
                {
                    if (root.Owners.TryGet(f, out PhysicsFragmentOwner o) && !o.IsFused) { placements[f] = PlacementOf(root, f); bounds[f] = ColliderBoundsOf(root, f); }
                }

                yield return null;
            }

            Assert.That(root.Fusion.FusedPieces, Is.EqualTo(3), "the three fused");
            WriteFusionRecord(root, "after the fusion");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1), "one group");
            FusedGroup group = root.Fusion.Groups[0];
            Assert.That(group.MemberCount, Is.EqualTo(3));
            Assert.That(group.Body.isKinematic, Is.True, "the group body is kinematic");
            Assert.That(RigidbodiesUnder(pieces, root), Is.EqualTo(1), "after: one body for the three");
            Assert.That(placements.Count == 3 && bounds.Count == 3, Is.True, "the placements before the fusion were read");
            foreach (LogicalFragmentId f in pieces)
            {
                Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner owner) && owner.IsFused && owner.Body == null, Is.True, "piece " + f.value + " is a body-less member");
                Assert.That(owner.Root.GetComponent<Rigidbody>(), Is.Null, "no body of its own");
                AssertSame(placements[f], PlacementOf(root, f), "display placement of " + f.value);
                Bounds b = ColliderBoundsOf(root, f);
                Assert.That((b.center - bounds[f].center).magnitude + (b.size - bounds[f].size).magnitude, Is.LessThan(1e-3f), "collider bounds of " + f.value);
                Assert.That(IsHitTarget(root, f), Is.True, "still a hit target");
                Assert.That(owner.Mass, Is.GreaterThan(0f), "its mass is kept");
            }

            GameObject sphere = Track(new GameObject("Outside Sphere"));
            sphere.transform.position = new Vector3(0.3f, 5f, 0.2f);
            sphere.AddComponent<SphereCollider>().radius = 0.25f;
            var ball = sphere.AddComponent<Rigidbody>();
            ball.mass = 2f;
            ball.useGravity = true;
            yield return PhysicsSeconds(1.5);
            foreach (LogicalFragmentId f in pieces) AssertSame(placements[f], PlacementOf(root, f), "after the sphere: " + f.value);
            Assert.That(group.Body.isKinematic, Is.True, "still held");
            yield return EndWorld(root);
            Assert.That(root.Fusion, Is.Null, "the ending released the fusion");
        }

        /// <summary>
        /// **A cut asked of a member splits the group into two sides, the crossed member commits, the sides rest and
        /// fuse again, and can be cut again.** A box is cut into halves, both held and fused into one group (two
        /// members); a cut of the upper member at y = 0.5 crosses only it: the group becomes a positive side (the top
        /// slab, dynamic) and a negative side (the lower half and the upper's lower child); the crossed member's Final
        /// publishes two body-less children and its geometry commits; the sides rest, are held and merged into one
        /// group again; a second cut of a child works the same way.
        /// </summary>
        [UnityTest]
        public IEnumerator Fusion_ACutOfAMember_SplitsTheGroup_CommitsTheCrossedMember_AndRepeats()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            yield return UntilFused(root, 2, "the halves fused");
            WriteFusionRecord(root, "fused");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));

            // The cut, through the driver as any cut: the plane in the upper member's own frame.
            ProvisionalCutAsk ask = Ask(upper, new float4(0f, 1f, 0f, -0.5f));
            Assert.That(root.TryAsk(in ask), Is.True, "the cut was asked");
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked");
            WriteFusionRecord(root, "after the group cut");
            List<CutOperationId> admitted = AdmittedFor(root, new[] { upper });
            Assert.That(admitted.Count, Is.EqualTo(1), "the crossed member's cut was admitted");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1), "one group cut");
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(1), "the upper member is crossed, the lower is not");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(2), "two sides");
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.IsFused && lowerOwner.Body == null, Is.True, "the lower half is a member still");

            yield return Until(() => root.Fusion.FinalsPublished == 1 || root.Fusion.FinalsFailed > 0, "the crossed member's Final");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero, "no failed Final");
            LogicalCutOperation record = OperationOf(root, admitted[0]);
            yield return Until(() => root.Geometry.StageOf(admitted[0]) == CutGeometryStage.Committed, "the crossed member's geometry committed");
            Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner p) && p.IsFused && p.Body == null, Is.True, "the positive child is a body-less member");
            Assert.That(root.Owners.TryGet(record.negative, out PhysicsFragmentOwner n) && n.IsFused && n.Body == null, Is.True, "the negative child is a body-less member");
            Assert.That(root.Owners.TryGet(upper, out _), Is.False, "the crossed member is retired");
            Assert.That(p.Group, Is.Not.SameAs(n.Group), "the children are on different sides");
            Assert.That(n.Group, Is.SameAs(lowerOwner.Group), "the negative child shares the lower half's side");
            Assert.That(IsHitTarget(root, record.positive) && IsHitTarget(root, record.negative) && IsHitTarget(root, lower), Is.True, "all members are hit targets");
            WriteFusionRecord(root, "after the Final and the commit");

            // The sides rest (they stand on the floor and on each other), are held, and merge into one group again.
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            yield return Until(() => root.Fusion.GroupCount == 1 || Time.realtimeSinceStartup > deadline, "the sides fused again");
            WriteFusionRecord(root, "fused again");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1), "one group again");
            Assert.That(root.Fusion.Groups[0].MemberCount, Is.EqualTo(3), "three members: the lower half and the two children");
            Assert.That(root.Fusion.Groups[0].Body.isKinematic, Is.True);

            // A second cut, of a child this time, vertical through everything (the world plane x = 0, carried into the
            // child's numerical frame, which a cook's products turn): both sides dynamic, three crossed members.
            Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner child), Is.True);
            float4x4 childShapeToWorld = math.mul((float4x4)child.Root.transform.localToWorldMatrix, child.Shape.LocalToOwner);
            float4 planeInChild = BuildingFusion.TransformPlane(math.inverse(childShapeToWorld), new float4(1f, 0f, 0f, 0f));
            ProvisionalCutAsk again = Ask(record.positive, planeInChild);
            Assert.That(root.TryAsk(in again), Is.True, "the second cut was asked");
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the second cut asked");
            WriteFusionRecord(root, "after the second group cut");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(2));
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(4), "three more crossed members");
            yield return Until(() => root.Fusion.FinalsPublished == 4 || root.Fusion.FinalsFailed > 0, "the three Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            List<CutOperationId> second = AdmittedFor(root, new[] { record.positive, record.negative, lower });
            Assert.That(second.Count, Is.EqualTo(3), "three admitted cuts");
            foreach (CutOperationId op in second) yield return Until(() => root.Geometry.StageOf(op) == CutGeometryStage.Committed, "committed " + op.value);
            WriteFusionRecord(root, "after the second round's commits");
            int live = 0; var all = new List<LogicalFragmentId>(); root.Owners.CopyFragmentsTo(all);
            foreach (LogicalFragmentId f in all) if (root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsFused) live++;
            Assert.That(live, Is.EqualTo(6), "six members live");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Anchors decide a side's fixity; the free side falls; the sides' masses are the members'.** An anchored box
        /// (anchor at x = -0.5) is cut at y = 0: the lower half is fixed by the ledger, the upper rests on it, both fuse.
        /// A vertical cut at x = 0 asked of the upper crosses both members: the negative side keeps the anchor and is
        /// kinematic, the positive side is dynamic and, with the floor far below, falls; after the Finals the dynamic
        /// side's body mass is its members' sum, and the anchored children are FixedByAnchors.
        /// </summary>
        [UnityTest]
        public IEnumerator Fusion_AnchorsKeepTheirSideKinematic_AndTheFreeSideFalls()
        {
            CutWorldRoot root = NewFusionWorld(-12f);
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId upper = UpperByRoot(root, halves), lower = Other(upper, halves);
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.FixedByAnchors, Is.True, "the lower half is anchored");
            yield return UntilFused(root, 2, "both fused (the anchored one as well)");
            WriteFusionRecord(root, "fused");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(1));

            // The cut with a separation impulse, as the product gives one (J = k x mass): without it the two sides stay in
            // face contact along the plane and friction against the kinematic side can carry the free one.
            ProvisionalCutAsk ask = Ask(upper, new float4(1f, 0f, 0f, 0f));
            ask.positiveSeparationImpulse = 12f;
            ask.negativeSeparationImpulse = 12f;
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            WriteFusionRecord(root, "after the vertical group cut");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1));
            Assert.That(root.Fusion.MembersSplit, Is.EqualTo(2), "both members are crossed");
            Assert.That(root.Fusion.SidesKinematic, Is.EqualTo(1), "one side keeps the anchor");
            Assert.That(root.Fusion.SidesDynamic, Is.EqualTo(1), "the other is free");
            FusedGroup dynamicSide = null, fixedSide = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Kinematic) fixedSide = g; else dynamicSide = g; }
            Assert.That(dynamicSide != null && fixedSide != null, Is.True);
            float yBefore = dynamicSide.Body.position.y;
            Vector3 fixedAt = fixedSide.Body.position;
            yield return Until(() => root.Fusion.FinalsPublished == 2 || root.Fusion.FinalsFailed > 0, "the two Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return PhysicsSeconds(1.0);
            WriteFusionRecord(root, "one second later: free side y " + dynamicSide.Body.position.y.ToString("F3") + " from " + yBefore.ToString("F3"));
            Assert.That(yBefore - dynamicSide.Body.position.y, Is.GreaterThan(0.5f), "the free side fell");
            Assert.That(fixedSide.Body.isKinematic, Is.True, "the anchored side stayed kinematic");
            Assert.That((fixedSide.Body.position - fixedAt).magnitude, Is.LessThan(1e-3f), "and did not move");
            double sum = 0.0; int anchoredMembers = 0;
            foreach (LogicalFragmentId f in dynamicSide.Fragments) { Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o), Is.True); sum += o.Mass; Assert.That(o.FixedByAnchors, Is.False, "a free member"); }
            foreach (LogicalFragmentId f in fixedSide.Fragments) { Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o), Is.True); if (o.FixedByAnchors) anchoredMembers++; }
            Assert.That(dynamicSide.Body.mass, Is.EqualTo((float)sum).Within(1e-3f), "the free side's mass is its members' sum");
            Assert.That(anchoredMembers, Is.EqualTo(1), "the anchored child is the lower half's negative child");
            Assert.That(sum + fixedSide.Mass, Is.EqualTo(ParentMass).Within(1e-3), "the box's mass is conserved across the cuts");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **One member's retirement leaves the group and the other members intact; the ending gives everything back.**
        /// Three fused members; one is retired the lifetime's way: its colliders go, the group keeps two members under
        /// the same body, the others are unmoved and still hit targets; a second building's group is untouched by a cut
        /// of the first; after the ending no group object remains.
        /// </summary>
        [UnityTest]
        public IEnumerator Fusion_RetiringOneMember_LeavesTheGroupAndTheOthers_AndTheEndingReclaims()
        {
            CutWorldRoot root = NewFusionWorld();
            var pieces = new List<LogicalFragmentId> { AddBuilding(root, Vector3.zero), AddBuilding(root, new Vector3(3f, 0f, 0f)), AddBuilding(root, new Vector3(6f, 0f, 0f)) };
            LogicalFragmentId other = AddBuilding(root, new Vector3(0f, 0f, 10f));
            yield return null;
            yield return UntilFused(root, 4, "all fused");
            WriteFusionRecord(root, "fused");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(4), "each registered building is its own group (its own key)");
            FusedGroup group = root.Owners.TryGet(pieces[0], out PhysicsFragmentOwner first) ? first.Group : null;
            Assert.That(group, Is.Not.Null);
            Matrix4x4 keep1 = PlacementOf(root, pieces[1]);

            Assert.That(root.Ledger.Retire(pieces[0]), Is.True);
            Assert.That(root.Owners.Retire(pieces[0]), Is.True);
            root.Geometry?.Forget(pieces[0]);
            yield return null;
            yield return null;
            WriteFusionRecord(root, "after one member's retirement");
            Assert.That(group.MemberCount, Is.Zero, "its own group is empty (each building is its own)");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(3), "the empty group went; the others stand");
            Assert.That(root.Owners.TryGet(pieces[1], out PhysicsFragmentOwner second) && second.IsFused && second.Root != null, Is.True, "another member is intact");
            AssertSame(keep1, PlacementOf(root, pieces[1]), "its placement");
            Assert.That(IsHitTarget(root, pieces[1]) && IsHitTarget(root, pieces[2]), Is.True);

            // A cut of one building's member does nothing to another building's group.
            Assert.That(root.Owners.TryGet(other, out PhysicsFragmentOwner otherOwner) && otherOwner.IsFused, Is.True);
            FusedGroup otherGroup = otherOwner.Group;
            ProvisionalCutAsk ask = Ask(pieces[1], new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            yield return Until(() => root.Fusion.FinalsPublished == 1 || root.Fusion.FinalsFailed > 0, "the Final");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            Assert.That(otherOwner.Group, Is.SameAs(otherGroup), "the other building's member is in the same group");
            Assert.That(otherGroup.Body != null && otherGroup.Body.isKinematic, Is.True, "still held");
            WriteFusionRecord(root, "after a cut of the first building");
            yield return EndWorld(root);
            Assert.That(root.Fusion, Is.Null);
            yield return null;
            Assert.That(GameObject.Find("Fused Group 1") == null && GameObject.Find("Fused Group 2") == null, Is.True, "no group object remains after the ending");
        }
    }
}
