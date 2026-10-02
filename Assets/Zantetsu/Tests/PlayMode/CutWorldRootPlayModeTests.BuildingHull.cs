using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The building hull trial (TL, 2026-09-30): a small building as one body and one hull, hit -> hull cut -> display
    /// cut -> fusion -> hit again; the colliders, hulls and cooks not growing with the members; the old state kept while
    /// a cut prepares; refusals, dropped candidates and the ending; the same Slash kept out, a next Slash waiting and going on;
    /// siblings aggregated with their placement changed and a hit in the filled gap; and the costs by part.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>The hull trial's world: hull on, kinematic building rest on (timeout 0.3 s of physics time, 3 support steps), the old fusion off, World D6 off, a floor with its top at y = -1.</summary>
        private CutWorldRoot NewHullWorld(float deadline = 0.5f, System.Action<CutWorldProfile> more = null, float restTimeout = 0.3f, System.Action<CutWorldRoot> beforeAwake = null)
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, profile =>
            {
                SetPrivate(profile, "buildingWorldEnabled", false);
                SetPrivate(profile, "buildingFusionEnabled", false);
                SetPrivate(profile, "buildingRestEnabled", true);
                SetPrivate(profile, "buildingRestMode", BuildingRestMode.Kinematic);
                SetPrivate(profile, "buildingRestTimeoutSeconds", restTimeout);
                SetPrivate(profile, "buildingHullEnabled", true);
                SetPrivate(profile, "buildingHullDeadlineSeconds", deadline);
                more?.Invoke(profile);
            }, beforeAwake);
            Assert.That(root.Hulls, Is.Not.Null, "the hull trial is on");
            Assert.That(root.Fusion, Is.Null, "the old fusion is off");
            Assert.That(root.Rest.Enabled && root.Rest.Settings.mode == BuildingRestMode.Kinematic, Is.True, "the kinematic rest is on");
            root.Driver.RemainingMainSeconds = () => 1.0;
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
            _hullFloor = floor;
            return root;
        }

        private GameObject _hullFloor;

        /// <summary>A building box (the fixture's) as a hull group: a body without colliders of its own, the display geometry, the anchors given (actor frame).</summary>
        private HullGroup AddHullBuilding(CutWorldRoot root, Vector3 at, float3[] anchors, double mass, out LogicalFragmentId fragment)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Hull Building"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            Assert.That(root.TryAddBuildingHull(actor, shape, geometry, Matrix4x4.identity, anchors, mass, out fragment, out HullGroup group), Is.True, "the building was taken into the hull trial");
            _registered.Add(actor);
            return group;
        }

        private static void RefuseSide(CutWorldRoot root, int[] serial, bool positive)
        {
            serial[0] = -1;
            root.Cook.meshOverrideForTest = (request, m, mesh) =>
            {
                if (serial[0] < 0) serial[0] = request.Serial;
                if (request.Serial != serial[0] || m != (positive ? 0 : 1)) return false;
                MakeRefusedConvex(mesh);
                return true;
            };
        }

        private static string HullRecord(BuildingHullFusion h)
        {
            return "groups " + h.GroupCount + " (made " + h.GroupsMade + "), cuts " + h.GroupCuts + " (failed " + h.CutsFailed + ", PhysX refusals " + h.HullRefusals + ", no change " + h.NoChanges + "), cook requests " + h.CookRequests + ", hull checks " + h.HullChecks
                + ", meshes baked " + h.MeshesBaked + " (fusion bakes " + h.FusionBakes + "), display members scanned " + h.DisplayMembersScanned + " (side reads at the publications " + h.SideLookups + ", without geometry " + h.MembersWithoutGeometry + "), display cuts " + h.DisplayCuts + " (refused " + h.DisplayCutsRefused + ", open " + h.DisplayOperationsOpen + ", failed " + h.DisplayOperationsFailed + "), room waits " + h.RoomWaits
                + ", hits published/refused/held/resumed " + h.HitsPublished + "/" + h.HitsRefused + "/" + h.HitsHeld + "/" + h.HitsResumed + " (duplicates refused " + h.HitsDuplicate + "), aggregation re-evaluations " + h.Reevaluations + ", one hull not achieved " + h.HullsNotAchieved
                + ", groups held " + h.GroupsHeld + " released " + h.GroupsReleased + ", merges " + h.Unions + " (aggregations by the deadline " + h.UnionsByDeadline + ", by the next Slash " + h.UnionsByNextSlash + "), fusions " + h.Fusions + " (candidates begun " + h.FusionsBegun + ", refused " + h.FusionsRefused + ", dropped " + h.FusionsStale + ", retried " + h.FusionsRetried + ", given up " + h.FusionsGivenUp + ", PhysX " + h.FusionHullRefusals + ", mixed waits " + h.FusionWaitsMixed + ", offers refused " + h.OffersRefused + ", reoffers " + h.Reoffers + ")"
                + ", hull exchanges " + h.HullExchanges + ", colliders made " + h.CollidersMade + " live " + h.LiveColliders + ", bodies made/destroyed " + h.BodiesMade + "/" + h.BodiesDestroyed + ", most vertices " + h.MaxVertices + " faces " + h.MaxFaces + ", most hulls at once " + h.MaxLiveHulls + "; class outcomes: " + h.DescribeAggregationStates()
                + "; ms: hits " + (h.HitSeconds * 1000).ToString("F3") + ", preparation " + (h.PrepareSeconds * 1000).ToString("F3") + ", publish " + (h.PublishSeconds * 1000).ToString("F3") + " (first " + (h.FirstPublishSeconds * 1000).ToString("F3") + ", max after " + (h.MaxPublishSecondsAfterFirst * 1000).ToString("F3") + "; physics " + (h.PublishPhysicsSeconds * 1000).ToString("F3") + ", display " + (h.PublishDisplaySeconds * 1000).ToString("F3")
                + "), unions " + (h.UniteSeconds * 1000).ToString("F3") + ", fusion scan (worker) " + (h.FusionWorkerSeconds * 1000).ToString("F3") + ", mesh preparation (Main) " + (h.MeshPrepareSeconds * 1000).ToString("F3") + ", fusion bake (worker) " + (h.FusionBakeSeconds * 1000).ToString("F3") + ", hull exchange " + (h.HullExchangeSeconds * 1000).ToString("F3") + ", step max " + (h.MaxStepSeconds * 1000).ToString("F3") + " (over budget " + h.OverrunFrames + ", deferred " + h.Deferred
                + "), ask to publish max " + (h.MaxAskToPublishSeconds * 1000).ToString("F1") + "; filled volume " + h.OverhangVolume.ToString("F4") + " (max " + h.MaxOverhangVolume.ToString("F4") + "), fusion envelope expansion max " + h.MaxFusionExpansion.ToString("E2") + " m shrink max " + h.MaxFusionShrink.ToString("E2") + " m, deepest penetration after a fusion " + h.MaxFusionPenetration.ToString("F4");
        }

        private static void WriteHullRecord(CutWorldRoot root, string what)
        {
            BuildingHullFusion h = root.Hulls;
            TestContext.Out.WriteLine(what + ": " + HullRecord(h));
            foreach (HullGroup g in h.Groups) TestContext.Out.WriteLine("  group " + g.Id + " (building " + g.Building + ", generation " + g.Generation + "): " + g.State + ", " + (g.Anchored ? "anchored" : g.Kinematic ? "held" : "free") + ", hulls " + g.HullCount + ", vertices " + g.VertexCount + ", faces " + g.FaceCount + ", members " + g.MemberCount + ", anchors " + g.AnchorCount + ", mass " + g.Mass.ToString("F3") + ", consumed " + g.ConsumedCount);
            foreach (BuildingHullFusion.HullHit hit in h.Hits) TestContext.Out.WriteLine("  hit " + hit.id + ": slash " + hit.slashId + " group " + hit.group + " g" + hit.generation + " => " + (hit.outcome ?? "PENDING") + (hit.displayOperations.Count > 0 ? " (display operations " + hit.displayOperations.Count + ")" : ""));
            foreach (string e in h.Events) TestContext.Out.WriteLine("  " + e);
            foreach (string f in h.Failures) TestContext.Out.WriteLine("  failure: " + f);
            foreach (string n in h.NotAchieved) TestContext.Out.WriteLine("  not achieved: " + n);
            if (h.LastPenetrationComparison != null) TestContext.Out.WriteLine("  last exchange's penetration by opponent: " + h.LastPenetrationComparison);
        }

        private static IEnumerator UntilHullsSettled(CutWorldRoot root, string what, float seconds = 30f)
        {
            yield return UntilWithin(() => root.Hulls.IsSettled, seconds, what);
        }

        /// <summary>Until no cut is in progress; on the timeout the record is written before the failure, so that what waited is known.</summary>
        private static IEnumerator UntilCutsEnd(CutWorldRoot root, float seconds, string what)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (root.Hulls.CutsInProgress > 0 && Time.realtimeSinceStartup < until) yield return null;
            if (root.Hulls.CutsInProgress > 0)
            {
                WriteHullRecord(root, what + ": timed out after " + seconds + " s with cuts in progress; " + root.Hulls.DescribeUnsettled());
                Assert.Fail(what + ": within " + seconds + " s");
            }
        }

        private static IEnumerator UntilOneGroup(CutWorldRoot root, string what, float seconds = 40f)
        {
            yield return UntilHull(root, () => root.Hulls.IsSettled && root.Hulls.GroupCount == 1 && root.Hulls.PairsWaiting == 0 && root.Hulls.Groups[0].State == HullGroupState.Idle, seconds, what);
        }

        /// <summary>Until a condition holds; on the timeout the hull record (and the rest's) is written before the failure, with the trial's own account of what waits.</summary>
        private static IEnumerator UntilHull(CutWorldRoot root, System.Func<bool> condition, float seconds, string what)
        {
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until) yield return null;
            if (!condition())
            {
                WriteHullRecord(root, what + ": timed out after " + seconds + " s; " + root.Hulls.DescribeUnsettled());
                if (root.Rest != null && root.Rest.Enabled) TestContext.Out.WriteLine(RestRecord(root));
                Assert.Fail(what + ": within " + seconds + " s (" + root.Hulls.DescribeUnsettled() + ")");
            }
        }

        private static void AssertDisplayConsistent(CutWorldRoot root, string when)
        {
            BuildingHullFusion h = root.Hulls;
            foreach (HullGroup g in h.Groups)
            {
                Assert.That(g.HullCount, Is.EqualTo(1), when + ": group " + g.Id + " holds one hull");
                Assert.That(g.Collider != null && g.Collider.attachedRigidbody == g.Body && g.Collider.sharedMesh == g.Shape.MeshOf(0), Is.True, when + ": the collider is the hull's on the group's body");
                Assert.That(g.Collider.GeometryHolder.Type, Is.EqualTo(UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh), when + ": a cooked convex");
                Assert.That(g.Body.isKinematic, Is.EqualTo(g.Kinematic), when + ": held as its body is");
                Assert.That(g.Body.mass, Is.EqualTo((float)g.Mass).Within(1e-3f * (float)g.Mass), when + ": the body carries the group's mass");
                foreach (LogicalFragmentId f in g.Fragments)
                {
                    Assert.That(root.Ledger.IsCurrentTarget(f), Is.True, when + ": member " + f.value + " is live");
                    Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsDisplayOnly && o.Root != null && o.Root.transform.IsChildOf(g.Root.transform), Is.True, when + ": member " + f.value + " is display-only under its group");
                    Assert.That(root.Placement.TryGetGeometryLocalToWorld(f, default, 0f, out Matrix4x4 _), Is.EqualTo(VpFragmentPlacementKind.Following), when + ": and placed");
                    Assert.That(IsHitTarget(root, f), Is.False, when + ": and never a hit target of its own");
                }
            }
        }

        /// <summary>
        /// **One hull: hit, cut, display, fusion, hit again.** An anchored box as one group; an upright sweep hits the hull:
        /// Pending, the old hull and collider stand until the two child hulls are cooked and checked; then two groups (the
        /// anchored side held, the other free), the old body kept by the positive side, one cook, two colliders, the display
        /// member cut through the DAG and committed. The free side falls, rests and is held; by the deadline the two are
        /// united and fused: one group, one hull, one collider, two display members, the filled volume recorded. The same
        /// Slash never hits again; a new Slash cuts the fused hull; a sweep through the filled gap hits the hull.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_OneHull_HitCutDisplayFuseAndHitAgain()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup group = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out LogicalFragmentId building);
            Rigidbody oldBody = group.Body;
            Mesh hullMesh = group.Collider.sharedMesh;
            Assert.That(group.Kinematic && oldBody.isKinematic && group.VertexCount == 8 && group.FaceCount == 6, Is.True, "the box's hull, held by its anchor");
            AssertDisplayConsistent(root, "registered");
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;

            Evaluate(detector, Upright(1, 0.3f), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1), "the hull was hit");
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending), "and its cut prepares");
            Assert.That(group.State, Is.EqualTo(HullGroupState.Cutting));
            Assert.That(group.Collider.sharedMesh == hullMesh && group.Generation == 1, Is.True, "the old hull stands while the cut prepares");
            yield return UntilWithin(() => h.CookRequests == 1, 5f, "the cut submitted under the budget");
            Assert.That(group.State == HullGroupState.Cutting && group.Collider.sharedMesh == hullMesh && group.Generation == 1, Is.True, "the old hull still stands while the cook runs");
            yield return UntilCutsEnd(root, 30f, "the cut published");
            WriteHullRecord(root, "after the first cut");
            Assert.That(h.GroupCuts, Is.EqualTo(1));
            Assert.That(h.GroupCount, Is.EqualTo(2), "two groups");
            HullGroup positive = null, negative = null;
            foreach (HullGroup g in h.Groups) { if (g.Body == oldBody) positive = g; else negative = g; }
            Assert.That(positive, Is.SameAs(group), "the positive side keeps the old body and group");
            Assert.That(negative, Is.Not.Null);
            Assert.That(positive.Generation, Is.EqualTo(2));
            Assert.That(negative.Kinematic && negative.AnchorCount == 1, Is.True, "the anchor's side is held");
            Assert.That(positive.Kinematic, Is.False, "the other side is free");
            Assert.That(positive.Mass + negative.Mass, Is.EqualTo(12.0).Within(1e-3), "the masses sum to the building's");
            Assert.That(h.LiveColliders, Is.EqualTo(2), "one collider a group");
            Assert.That(h.DisplayCuts, Is.EqualTo(1), "the display member was cut");
            Assert.That(root.Ledger.IsCurrentTarget(building), Is.False, "the source display fragment retired");
            Assert.That(positive.MemberCount == 1 && negative.MemberCount == 1, Is.True, "a display child each");
            CutOperationId displayOp = h.Hits[0].displayOperations[0];
            yield return UntilWithin(() => root.Geometry.StageOf(displayOp) == CutGeometryStage.Committed, 30f, "the display cut's geometry committed");
            AssertDisplayConsistent(root, "after the cut");

            // The free side falls, rests and is held; the deadline makes the candidate, adopted: the two merge.
            yield return UntilWithin(() => positive.Kinematic, 30f, "the free side rested and was held");
            Assert.That(h.Unions, Is.Zero, "no body merged before the candidate is judged");
            yield return UntilOneGroup(root, "the sides fused into one group");
            WriteHullRecord(root, "after the fusion");
            HullGroup fused = h.Groups[0];
            Assert.That(h.Unions == 1 && h.Fusions == 1, Is.True, "one merge at the one adoption");
            Assert.That(fused.HullCount == 1 && fused.MemberCount == 2 && h.LiveColliders == 1, Is.True, "one hull, one collider, two display members");
            Assert.That(fused.VertexCount, Is.LessThanOrEqualTo(128));
            Assert.That(fused.Mass, Is.EqualTo(12.0).Within(1e-3), "the mass is the sum, not the filled hull's");
            Assert.That(h.OverhangVolume, Is.GreaterThan(0.0), "the gap between the fallen side and the standing one was filled (recorded)");
            Assert.That(fused.IsConsumedBy(1), Is.True, "the Slash is consumed on the fused group");
            AssertDisplayConsistent(root, "after the fusion");

            // The same Slash again: nothing. A new Slash: the fused hull is cut.
            Evaluate(detector, Level(1, 0.5f, -3f, 3f), 1);
            Assert.That(detector.HitCount, Is.Zero, "the consumed Slash finds nothing");
            Evaluate(detector, Level(2, -0.5f, -3f, 3f), 2);
            Assert.That(detector.HitCount, Is.EqualTo(1), "a new Slash hits the fused hull");
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            yield return UntilWithin(() => h.CutsInProgress == 0, 30f, "the second cut published");
            Assert.That(h.GroupCuts, Is.EqualTo(2));
            Assert.That(h.CookRequests, Is.EqualTo(2), "one cook a cut, whatever the members");
            Assert.That(h.LiveColliders, Is.LessThanOrEqualTo(2));
            AssertDisplayConsistent(root, "after the second cut");
            yield return UntilOneGroup(root, "fused again", 60f);
            WriteHullRecord(root, "after the second fusion");
            Assert.That(h.Fusions, Is.EqualTo(2));
            Assert.That(h.Groups[0].MemberCount, Is.GreaterThanOrEqualTo(3), "the display members grew");
            Assert.That(h.LiveColliders, Is.EqualTo(1), "the colliders did not");

            // A sweep through the filled gap: the hull is hit (the display decides whether anything changes).
            HullGroup whole = h.Groups[0];
            whole.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
            float3 top = math.transform((float4x4)whole.Root.transform.localToWorldMatrix, new float3((lo.x + hi.x) * 0.5f, hi.y - 0.05f, (lo.z + hi.z) * 0.5f));
            Evaluate(detector, Level(3, top.y, top.x - 0.3f, top.x + 0.3f, top.z - 0.3f, top.z + 0.3f), 3);
            TestContext.Out.WriteLine("gap sweep at " + ((Vector3)top).ToString("F3") + ": hits " + detector.HitCount + (detector.HitCount > 0 ? " " + detector.HitAt(0).Acceptance : ""));
            Assert.That(detector.HitCount, Is.EqualTo(1), "the filled hull is hit near its top");
            yield return UntilHullsSettled(root, "the gap hit settled");
            WriteHullRecord(root, "at the end");
            Assert.That(h.CutsFailed, Is.Zero);
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero, "no classification block left");
        }

        /// <summary>
        /// **Ten cuts and fusions with changing planes and poses: the colliders, hulls and cooks stay one a group.** Each
        /// round cuts the one group by a plane of a different orientation, waits for the sides to rest and fuse, and checks
        /// one hull, one collider, one cook a cut, the vertices within the limit, the masses summing, the display members
        /// growing and placed.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_TenCutsAndFusions_KeepOneHullOneColliderOneCookACut()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f), new float3(0.5f, -0.9f, 0.5f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            var simulates = new List<double>();
            for (int round = 0; round < 10; round++)
            {
                HullGroup g = h.Groups[0];
                g.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                float3 c = math.transform((float4x4)g.Root.transform.localToWorldMatrix, (lo + hi) * 0.5f);
                long slash = 10 + round;
                SlashSweep sweep = round % 3 == 0 ? Upright(slash, c.x + 0.15f * (round % 2 == 0 ? 1 : -1), c.y)
                    : round % 3 == 1 ? Level(slash, c.y + 0.1f * (round % 4 - 1.5f), c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f)
                    : new SlashSweep(slash, 0.0, false, new Plane(Vector3.forward, -(c.z + 0.1f)), Vector3.right, Vector3.up, new Vector3(c.x - 3f, c.y - 3f, c.z + 0.1f), new Vector3(c.x - 3f, c.y + 3f, c.z + 0.1f), new Vector3(c.x + 3f, c.y - 3f, c.z + 0.1f), new Vector3(c.x + 3f, c.y + 3f, c.z + 0.1f));
                Evaluate(detector, sweep, slash);
                Assert.That(detector.HitCount, Is.EqualTo(1), "round " + round + ": the sweep hits the hull");
                int cuts = h.GroupCuts;
                yield return UntilCutsEnd(root, 30f, "round " + round + ": the cut");
                Assert.That(h.GroupCuts, Is.EqualTo(cuts + 1), "round " + round + ": the cut published (" + h.Hits[h.Hits.Count - 1].outcome + ")");
                yield return Steps(1);
                simulates.Add(CutPhysicsStep.LastSimulateSeconds * 1000.0);
                foreach (HullGroup side in h.Groups) Assert.That(side.VertexCount, Is.LessThanOrEqualTo(128), "round " + round + ": a side's hull within the limit");
                yield return UntilOneGroup(root, "round " + round + ": fused", 60f);
                Assert.That(h.Groups[0].HullCount, Is.EqualTo(1), "round " + round + ": one hull after the fusion");
                Assert.That(h.Groups[0].Mass, Is.EqualTo(12.0).Within(1e-2), "round " + round + ": the mass is kept");
                AssertDisplayConsistent(root, "round " + round);
            }

            WriteHullRecord(root, "after ten rounds");
            TestContext.Out.WriteLine("Simulate after each publication (ms): " + string.Join(" / ", simulates.ConvertAll(v => v.ToString("F3"))));
            Assert.That(h.GroupCuts, Is.EqualTo(10), "every round cut");
            Assert.That(h.Fusions, Is.EqualTo(10), "and every round fused");
            Assert.That(h.CookRequests, Is.EqualTo(10), "one cook request a cut");
            Assert.That(h.MeshesBaked, Is.EqualTo(1 + 2 * 10 + h.FusionBakes), "the meshes baked: the registration's, two child meshes a cut, one a fusion");
            Assert.That(h.FusionBakes, Is.EqualTo(10));
            Assert.That(h.LiveColliders, Is.EqualTo(1), "one collider at the end");
            Assert.That(h.MaxLiveHulls, Is.LessThanOrEqualTo(2), "never more than two hulls at once (the two sides)");
            Assert.That(h.Unions, Is.EqualTo(10), "one merge an adoption");
            Assert.That(h.MaxVertices, Is.LessThanOrEqualTo(128));
            Assert.That(h.Groups[0].MemberCount, Is.GreaterThan(5), "the display members grew with the cuts");
            Assert.That(h.CutsFailed, Is.Zero);
            Assert.That(h.RoomWaits, Is.Zero);
            Assert.That(h.SideLookups, Is.EqualTo(h.DisplayMembersScanned), "a prepared side is read once a member at the publications (" + h.SideLookups + " reads for " + h.DisplayMembersScanned + " members scanned): linear, not quadratic");
            Assert.That(h.DisplayOperationsOpen, Is.Zero, "every display operation ended");
            Assert.That(h.IsSettled && h.PairsApart == 0, Is.True, "nothing waits: " + h.DescribeUnsettled());
            yield return EndWorld(root);
        }

        /// <summary>
        /// **While a cut prepares the old state stands, a second Slash waits and goes on; a refused hull leaves the group as
        /// it was; a stale fusion is dropped; the ending abandons what waits.** The checks held: the hit's group keeps its
        /// hull, collider, body and generation, a second Slash's hit is held; the checks let go: published, and the held hit
        /// is resumed on the child its plane crosses. Then a cut whose positive child PhysX refuses, and one whose negative
        /// child it refuses: the group unchanged each time, the refusal attributed to the cook's request and the mesh. A
        /// candidate whose snapshot went stale is dropped, the groups stay apart, and the building is looked at again: the
        /// next candidate is adopted. The world ended with a cut preparing: abandoned, nothing left.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHull_PendingKeepsTheOldState_RefusalsStaleAndTheEnding()
        {
            LogAssert.ignoreFailingMessages = true;
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup group = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f), new float3(0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Mesh hullMesh = group.Collider.sharedMesh;
            Rigidbody body = group.Body;
            root.Cook.holdHullChecksForTest = true;
            Evaluate(detector, Upright(1, 0.2f), 1);
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            yield return UntilWithin(() => h.Hits[0].cookSerial >= 0 && root.Cook.CutsChecking > 0, 20f, "the cut reached its check");
            Assert.That(group.State == HullGroupState.Cutting && group.Collider.sharedMesh == hullMesh && group.Body == body && group.Generation == 1 && h.GroupCount == 1, Is.True, "the old state stands while the checks are held");
            Evaluate(detector, Upright(2, -0.5f), 1, 2);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Held, Is.True, "a second Slash's hit is held");
            Assert.That(h.HeldNow, Is.EqualTo(1));
            root.Cook.holdHullChecksForTest = false;
            yield return UntilWithin(() => h.GroupCuts == 1, 30f, "the first cut published");
            yield return UntilWithin(() => h.HitsResumed == 1 && h.GroupCuts == 2 || h.HitsRefused > 0, 30f, "the held hit resumed on the child its plane crosses and cut it");
            WriteHullRecord(root, "after the held hit");
            Assert.That(h.HitsResumed, Is.EqualTo(1));
            Assert.That(h.GroupCuts, Is.EqualTo(2), "the held hit cut the child");
            yield return UntilWithin(() => h.IsSettled, 30f, "settled");
            AssertDisplayConsistent(root, "after the resumed cut");
            yield return UntilOneGroup(root, "fused into one", 60f);

            // A refused positive child, then a refused negative child: the group as it was.
            foreach (bool positive in new[] { true, false })
            {
                HullGroup g = h.Groups[0];
                int generation = g.Generation, members = g.MemberCount, refusals = h.HullRefusals, cuts = h.GroupCuts;
                Mesh mesh = g.Collider.sharedMesh;
                var serial = new int[1];
                RefuseSide(root, serial, positive);
                g.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                float3 c = math.transform((float4x4)g.Root.transform.localToWorldMatrix, (lo + hi) * 0.5f);
                long slash = positive ? 5 : 6;
                Evaluate(detector, Upright(slash, c.x + 0.05f, c.y), slash);
                Assert.That(detector.HitCount, Is.EqualTo(1), "the hull was hit");
                yield return UntilWithin(() => h.CutsInProgress == 0, 30f, "the refused cut ended");
                root.Cook.meshOverrideForTest = null;
                Assert.That(h.HullRefusals, Is.EqualTo(refusals + 1), (positive ? "positive" : "negative") + ": refused by PhysX");
                Assert.That(h.GroupCuts, Is.EqualTo(cuts), "not published");
                Assert.That(g.Generation == generation && g.MemberCount == members && g.Collider.sharedMesh == mesh && g.State == HullGroupState.Idle && h.GroupCount == 1, Is.True, "the group as it was");
                BuildingHullFusion.HullHit hit = h.Hits[h.Hits.Count - 1];
                Assert.That(hit.cookSerial, Is.EqualTo(serial[0]), "the record names the cook's request");
                Assert.That(hit.failedMesh, Does.StartWith("Zantetsu Physics Cut " + serial[0] + "."), "and the mesh");
                Assert.That(root.Cook.AttributedHullRejections, Is.EqualTo(refusals + 1), "attributed once");
                Assert.That(g.IsConsumedBy(slash), Is.True, "the Slash stays consumed");
                Assert.That(ShapelessColliders(), Is.Empty);
                AssertDisplayConsistent(root, "after the refusal");
            }

            // A stale candidate: the snapshot's generation moved before the adoption; dropped, the groups stay apart, and the next candidate goes through.
            {
                HullGroup g = h.Groups[0];
                g.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                float3 c = math.transform((float4x4)g.Root.transform.localToWorldMatrix, (lo + hi) * 0.5f);
                h.bumpGenerationAfterFusionSnapshotForTest = true;
                Evaluate(detector, Level(7, c.y + 0.2f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), 7);
                yield return UntilWithin(() => h.CutsInProgress == 0, 30f, "the cut");
                yield return UntilWithin(() => h.FusionsStale >= 1, 40f, "a stale candidate dropped");
                int fusions = h.Fusions, begun = h.FusionsBegun, unions = h.Unions;
                h.bumpGenerationAfterFusionSnapshotForTest = false;
                WriteHullRecord(root, "after the stale candidate");
                Assert.That(h.Unions, Is.EqualTo(unions), "no body merged for the dropped candidate");
                foreach (HullGroup side in h.Groups) Assert.That(side.HullCount, Is.EqualTo(1), "group " + side.Id + " stays its own hull (a new candidate may already have it as a participant)");
                yield return UntilWithin(() => h.Fusions > fusions, 40f, "the next candidate adopted");
                Assert.That(h.FusionsBegun, Is.GreaterThan(begun), "a new candidate was made after the drop");
                yield return UntilOneGroup(root, "one group with one hull again", 40f);
                Assert.That(h.Groups[0].HullCount, Is.EqualTo(1));
                AssertDisplayConsistent(root, "after the next candidate");
            }

            // The ending with a cut preparing: abandoned, nothing left.
            root.Cook.holdHullChecksForTest = true;
            {
                HullGroup g = h.Groups[0];
                g.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                float3 c = math.transform((float4x4)g.Root.transform.localToWorldMatrix, (lo + hi) * 0.5f);
                Evaluate(detector, Upright(9, c.x + 0.1f, c.y), 9);
                Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "a cut prepares at the ending");
                yield return UntilWithin(() => root.Cook.CutsChecking > 0, 20f, "held at its check");
            }

            BuildingHullFusion.HullHit last = h.Hits[h.Hits.Count - 1];
            Assert.That(last.IsPending, Is.True);
            WriteHullRecord(root, "before the ending");
            yield return EndWorld(root);
            Assert.That(last.outcome, Does.StartWith("Abandoned"), "the pending hit was abandoned by the ending");
            Assert.That(PhysicsCutClassification.Live, Is.Zero, "no classification block left");
        }
    }
}
