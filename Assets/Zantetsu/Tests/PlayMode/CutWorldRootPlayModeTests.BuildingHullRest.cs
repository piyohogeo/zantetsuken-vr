using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The hull trial's groups rested by the building rest (TL, 2026-09-30): a free side falls until it lands and is
    /// held only then, by the rest's clock and support -- never in the air by a deadline; a held group cut again is
    /// released and its sides rest again; a held group whose support goes is released by the rest and falls; an ending
    /// with a candidate waiting for the dispatcher gives everything back; a candidate refused three times is given up
    /// with the groups apart and cuttable; a candidate whose hull spans an obstacle is not adopted, the groups stay apart
    /// and are cut again by a synthetic Slash, and the candidate after the obstacle goes is adopted and cut again; a
    /// support lost while a candidate is in flight drops the candidate and leaves the groups cuttable.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static HullGroup FreeSide(BuildingHullFusion h)
        {
            foreach (HullGroup g in h.Groups) if (!g.Anchored) return g;
            return null;
        }

        private static HullGroup AnchoredSide(BuildingHullFusion h)
        {
            foreach (HullGroup g in h.Groups) if (g.Anchored) return g;
            return null;
        }

        private static string RestRecord(CutWorldRoot root)
        {
            BuildingRest rest = root.Rest;
            var text = new System.Text.StringBuilder("rest: tracked " + rest.TrackedDynamic + "+" + rest.TrackedAnchored + ", kinematic rests " + rest.KinematicRests + ", held now " + rest.RestedNow + ", groups released " + rest.GroupsReleased
                + ", woken by support loss " + rest.WokenBySupportLoss + ", grounds lost " + rest.LostGrounds + ", held without support " + rest.AsleepWithoutSupport
                + "; ms contacts/support/switch/rest " + (rest.ContactSeconds * 1000).ToString("F2") + "/" + (rest.SupportSeconds * 1000).ToString("F2") + "/" + (rest.SwitchSeconds * 1000).ToString("F3") + "/" + (rest.SleepSeconds * 1000).ToString("F2") + " over " + rest.Steps + " turns");
            foreach (HullGroup g in root.Hulls.Groups) if (rest.TryDescribeGroup(g.Body, out string state)) text.Append("\n  group " + g.Id + ": " + state);
            foreach (string e in rest.Events) text.Append("\n  ").Append(e);
            return text.ToString();
        }

        private static int MeshesNamed(string prefix)
        {
            int n = 0;
            foreach (Mesh m in Resources.FindObjectsOfTypeAll<Mesh>()) if (m != null && m.name.StartsWith(prefix)) n++;
            return n;
        }

        /// <summary>
        /// The re-cut of a group by a synthetic Slash through its hull (a level plane through the hull's centre, the sweep
        /// covering it), fed into the ordinary detection path, and judged in three parts, apart: the sweep hit the real
        /// hull; the hit was accepted (Pending or Held); the cut was published and its display operations committed.
        /// Nothing calls the driver directly.
        /// </summary>
        private static IEnumerator RecutJudged(CutWorldRoot root, SlashHitDetector detector, HullGroup g, long slash, string what)
        {
            BuildingHullFusion h = root.Hulls;
            float3 c = CentreOf(g);
            int hitsBefore = h.Hits.Count, cutsBefore = h.GroupCuts;
            Evaluate(detector, Level(slash, c.y, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), slash);
            bool hitReal = false;
            int accepted = 0;
            for (int i = 0; i < detector.HitCount; i++)
            {
                foreach (LogicalFragmentId f in g.Fragments) hitReal |= f == detector.HitAt(i).Fragment;
                if (detector.HitAt(i).Acceptance == ProvisionalCutAcceptance.Pending || detector.HitAt(i).Acceptance == ProvisionalCutAcceptance.Held) accepted++;
            }

            TestContext.Out.WriteLine(what + ": synthetic slash " + slash + " level at y " + c.y.ToString("F3") + " through group " + g.Id + ": detector hits " + detector.HitCount + ", real hull " + hitReal + ", accepted " + accepted);
            Assert.That(hitReal, Is.True, what + ": (1) the synthetic sweep through the hull hit the real hull of group " + g.Id + " (detector hits " + detector.HitCount + ")");
            Assert.That(accepted, Is.GreaterThanOrEqualTo(1), what + ": (2) the hit was accepted by the ordinary acceptance");
            yield return UntilHull(root, () => { for (int i = hitsBefore; i < h.Hits.Count; i++) if (h.Hits[i].IsPending) return false; return h.CutsInProgress == 0; }, 30f, what + ": the hit answered");
            int published = 0;
            var operations = new List<CutOperationId>();
            for (int i = hitsBefore; i < h.Hits.Count; i++)
            {
                if (h.Hits[i].outcome != "Published") continue;
                published++;
                operations.AddRange(h.Hits[i].displayOperations);
            }

            Assert.That(published, Is.GreaterThanOrEqualTo(1), what + ": (3) a cut was published (" + (h.Hits.Count > hitsBefore ? h.Hits[hitsBefore].outcome : "no request") + ")");
            Assert.That(h.GroupCuts, Is.EqualTo(cutsBefore + published));
            yield return UntilHull(root, () => h.DisplayOperationsOpen == 0, 30f, what + ": the display operations ended");
            foreach (CutOperationId op in operations) Assert.That(root.Geometry.StageOf(op), Is.EqualTo(CutGeometryStage.Committed), what + ": (3) display operation " + op.value + " committed");
            Assert.That(root.Ledger.TryGetOperation(operations[0], out LogicalCutOperation record) && record.state == LogicalCutOperationState.Completed, Is.True, what + ": (3) the display operation is Completed in the ledger");
        }

        /// <summary>
        /// **A free side falls until it lands; held only then.** The building stands 3 m above the floor, fixed by its
        /// anchor; the side the cut frees falls. Past the rest's timeout it is still falling and not held (the rest holds
        /// nothing without confirmed support). Once it lands it is held by the rest, the trial's group follows, and the
        /// held side and the anchored side are aggregated by the deadline: the candidate adopted, one anchored group.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_FreeSideFallsUntilItLands_ThenTheRestHoldsIt()
        {
            CutWorldRoot root = NewHullWorld(3f);   // a long deadline: the hold is observed before the aggregation (both happen in one frame otherwise)
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, new Vector3(0f, 3f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            Assert.That(root.Rest.TrackedAnchored, Is.EqualTo(1), "the anchored group is the rest's ground");
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Upright(1, 0.3f, 3f), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilCutsEnd(root, 30f, "the cut");
            HullGroup free = FreeSide(h);
            Assert.That(free, Is.Not.Null.And.Property("Kinematic").False, "the free side is dynamic");
            Assert.That(root.Rest.TrackedDynamic, Is.EqualTo(1), "and followed by the rest");
            double cutAt = CutPhysicsStep.Clock.PhysicsSeconds;
            float y0 = free.Body.position.y;
            yield return UntilWithin(() => CutPhysicsStep.Clock.PhysicsSeconds - cutAt > root.Rest.Settings.timeoutSeconds + 0.1, 20f, "past the rest's timeout");
            Assert.That(free.Kinematic, Is.False, "past the timeout, still falling: not held in the air");
            Assert.That(free.Body.position.y, Is.LessThan(y0 - 0.2f), "it fell");
            Assert.That(root.Rest.RestedNow, Is.Zero);
            Assert.That(h.GroupsHeld, Is.Zero);
            yield return UntilWithin(() => free.Kinematic, 30f, "landed and held");
            WriteHullRecord(root, "after the landing");
            TestContext.Out.WriteLine(RestRecord(root));
            Assert.That(h.GroupsHeld, Is.EqualTo(1), "held by the rest");
            Assert.That(free.Held && !free.Anchored && free.Body.isKinematic, Is.True);
            Assert.That(root.Rest.RestedNow, Is.EqualTo(1));
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero, "nothing held without a chain to the ground");
            Assert.That(free.Body.position.y, Is.LessThan(1f), "on the floor, not in the air");
            Assert.That(h.Unions, Is.Zero, "no body merged before the candidate is judged");
            yield return UntilOneGroup(root, "the held side's candidate adopted into the anchored one", 60f);
            WriteHullRecord(root, "after the adoption");
            Assert.That(h.Groups[0].Anchored && h.Groups[0].Kinematic, Is.True, "the fused group is the anchored one");
            Assert.That(h.Unions == 1 && h.Fusions == 1, Is.True, "one merge at the one adoption");
            Assert.That(root.Rest.TrackedAnchored == 1 && root.Rest.TrackedDynamic == 0, Is.True, "the rest follows one anchored group: " + RestRecord(root));
            AssertDisplayConsistent(root, "after the adoption");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A held group cut again: released by the cut, its free side rests again.** With no deadline (aggregation only
        /// by a next Slash), the free side of a first cut rests on the floor and is held. A second Slash on the held side
        /// is held behind the class's candidate, which the next Slash brings on (held + anchored -> one anchored group),
        /// then resumes on the fused group: the side its plane frees is dynamic again with a fresh clock, and is held
        /// again once it rests. The rest holds twice in all; the anchored side is never released.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_AHeldGroupCutAgain_RestsAgain()
        {
            CutWorldRoot root = NewHullWorld(0f);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Upright(1, 0.3f), 1);
            yield return UntilCutsEnd(root, 30f, "the first cut");
            HullGroup free = FreeSide(h);
            yield return UntilWithin(() => free.Kinematic, 30f, "the free side rested and was held");
            Assert.That(h.GroupsHeld, Is.EqualTo(1));
            Assert.That(h.PairsWaiting, Is.EqualTo(1), "no deadline: the class waits for a next Slash");

            float3 c = CentreOf(free);
            Evaluate(detector, Level(2, c.y + 0.1f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), 2);
            Assert.That(detector.HitCount, Is.GreaterThanOrEqualTo(1), "the next Slash hits (both sides stand at that height)");
            for (int i = 0; i < detector.HitCount; i++) Assert.That(detector.HitAt(i).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Held), "hit " + i + " is held behind the class's candidate");
            yield return UntilWithin(() => h.Fusions == 1, 60f, "the next Slash brought the candidate on, adopted");
            Assert.That(h.UnionsByNextSlash, Is.EqualTo(1));
            yield return UntilWithin(() => h.HitsResumed == 1 && h.GroupCuts == 2, 60f, "the held hit resumed and cut the fused group");
            yield return UntilCutsEnd(root, 30f, "the second cut");
            HullGroup freed = FreeSide(h);
            Assert.That(freed, Is.Not.Null, "the second cut freed a side");
            Assert.That(freed.Kinematic, Is.False, "dynamic again, with a fresh clock");
            yield return UntilWithin(() => freed.Kinematic, 30f, "and held again once it rests");
            WriteHullRecord(root, "after the second rest");
            TestContext.Out.WriteLine(RestRecord(root));
            Assert.That(h.GroupsHeld, Is.EqualTo(2), "held twice in all");
            Assert.That(h.GroupsReleased, Is.Zero, "nothing released for a lost support");
            Assert.That(root.Rest.KinematicRests, Is.EqualTo(2));
            Assert.That(root.Rest.AsleepWithoutSupport, Is.Zero);
            foreach (HullGroup g in h.Groups) if (g.Anchored) Assert.That(g.Kinematic && g.Body.isKinematic, Is.True, "the anchored side stayed fixed");
            AssertDisplayConsistent(root, "after the second rest");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A held group whose support goes is released by the rest and falls.** The free side rests on the floor and is
        /// held; the floor is taken away: the rest finds the ground gone, releases the group (the trial applies its mass
        /// and follows), and it falls. The anchored side stays fixed. The display member of a released group is not
        /// retired, and the group is not released for a member's retirement either.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_SupportLoss_ReleasesTheHeldGroup()
        {
            CutWorldRoot root = NewHullWorld(0f);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Upright(1, 0.3f), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            HullGroup free = FreeSide(h), anchored = AnchoredSide(h);
            yield return UntilWithin(() => free.Kinematic, 30f, "the free side rested and was held");
            double massHeld = free.Mass;
            float y0 = free.Body.position.y;

            _hullFloor.SetActive(false);
            yield return UntilWithin(() => h.GroupsReleased == 1, 20f, "the rest released the group for its lost ground");
            WriteHullRecord(root, "after the release");
            TestContext.Out.WriteLine(RestRecord(root));
            Assert.That(free.Kinematic == false && free.Body.isKinematic == false, Is.True, "dynamic again");
            Assert.That(free.Body.mass, Is.EqualTo((float)massHeld).Within(1e-3f), "the mass applied at the release");
            Assert.That(root.Rest.GroupsReleased, Is.EqualTo(1));
            Assert.That(root.Rest.LostGrounds, Is.GreaterThanOrEqualTo(1));
            yield return Steps(20);
            Assert.That(free.Body.position.y, Is.LessThan(y0 - 0.05f), "it falls");
            Assert.That(anchored.Kinematic && anchored.Body.isKinematic, Is.True, "the anchored side stays fixed");
            Assert.That(root.Rest.TrackedAnchored, Is.EqualTo(1));
            AssertDisplayConsistent(root, "after the release");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An ending with a candidate waiting for the dispatcher gives everything back.** The class is due by the
        /// deadline but its scan is never taken (every offer refused): the candidate is in flight, both groups its
        /// participants, unsettled; no body has merged. The world ends: the hits are answered, the groups' bodies,
        /// colliders, hulls and meshes go, no classification block is left, and the rest follows nothing.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_EndingWithACandidateWaiting_GivesEverythingBack()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            int hullMeshes = MeshesNamed("Building hull");
            Assert.That(hullMeshes, Is.EqualTo(1), "the registration's hull mesh");
            h.refuseOffersForTest = int.MaxValue;
            Evaluate(detector, Upright(1, 0.3f), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            yield return UntilWithin(() => h.FusionsInFlight == 1, 60f, "the class's candidate offered (refused)");
            Assert.That(h.IsSettled, Is.False, h.DescribeUnsettled());
            Assert.That(h.OffersRefused, Is.GreaterThanOrEqualTo(1));
            Assert.That(h.GroupCount, Is.EqualTo(2), "the groups stand apart: no body merged before the candidate is judged");
            foreach (HullGroup g in h.Groups) Assert.That(g.State == HullGroupState.Fusing && g.HullCount == 1, Is.True, "group " + g.Id + " is a participant, its one hull");
            Assert.That(h.Unions, Is.Zero);
            WriteHullRecord(root, "before the ending");
            int bodiesBefore = 0;
            foreach (HullGroup g in h.Groups) if (g.Body != null) bodiesBefore++;
            Assert.That(bodiesBefore, Is.EqualTo(2));
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero, "no classification block left");
            Assert.That(MeshesNamed("Building hull"), Is.Zero, "the hull meshes (registration, candidate) went");
            Assert.That(MeshesNamed("Zantetsu Physics Cut"), Is.Zero, "the cook's child meshes went");
            Assert.That(ShapelessColliders(), Is.Empty);
            Assert.That(Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length, Is.Zero, "no group body left");
            Assert.That(Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length, Is.Zero, "no hull collider left");
        }

        /// <summary>
        /// **A candidate given up is a failure completion with the groups apart and cuttable; the building's other groups
        /// go on.** Every hull scan is refused: the first building's class is refused three times and given up. Its two
        /// groups keep their own hulls, members, masses and collider meshes; no body merged; the trial is settled but not
        /// without failure. A synthetic Slash through the anchored group is accepted and published (the class changes:
        /// the give-up is the old class's); the second building, with the refusal lifted, is cut and fused meanwhile;
        /// the ending gives everything back.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_CandidateGivenUp_IsAFailureCompletion_GroupsApartAndCuttable()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup other = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.refuseHullForTest = true;
            Evaluate(detector, Upright(1, 0.3f), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1));
            yield return UntilCutsEnd(root, 30f, "the first building's cut");
            yield return UntilWithin(() => h.FusionsRefused >= 1, 60f, "the first refusal");
            Assert.That(h.IsSettled, Is.False, "a refused scan awaits its retry: " + h.DescribeUnsettled());
            StringAssert.Contains("awaiting a retry 1", h.DescribeUnsettled());
            yield return UntilWithin(() => h.FusionsGivenUp == 1, 60f, "given up after three refusals");
            WriteHullRecord(root, "after the give-up");
            HullGroup anchored = null, held = null;
            foreach (HullGroup g in h.Groups) { if (g.Building == other.Building) continue; if (g.Anchored) anchored = g; else held = g; }
            Assert.That(anchored != null && held != null && held.Kinematic, Is.True, "the first building's two groups stand apart, both resting");
            Assert.That(h.GroupCount, Is.EqualTo(3), "two groups of the first building and the other building");
            Assert.That(h.Unions, Is.Zero, "no body merged");
            Assert.That(h.LiveColliders, Is.EqualTo(3), "one collider a group");
            Assert.That(h.FusionsRetried, Is.EqualTo(2), "tried again twice before the give-up");
            Assert.That(h.IsSettled, Is.True, "nothing in progress: " + h.DescribeUnsettled());
            Assert.That(h.IsSettledWithoutFailure, Is.False, "but not a normal completion");
            Assert.That(h.HasGivenUpFusions, Is.True);
            StringAssert.Contains("given up 1", h.DescribeUnsettled());
            StringAssert.Contains("given up", h.DescribeAggregationStates());
            Assert.That(h.Failures.Count, Is.EqualTo(1));
            StringAssert.Contains("refused 3 times", h.Failures[0]);
            StringAssert.Contains("refused (test)", h.Failures[0]);
            Assert.That(h.MaxNotAchievedSeconds, Is.GreaterThan(0.0), "the class standing given up is measured while it stands");
            AssertDisplayConsistent(root, "after the give-up");

            // The given-up class is cuttable: a synthetic Slash through the anchored group, judged in three parts.
            yield return RecutJudged(root, detector, anchored, 3, "the anchored group of the given-up class");
            Assert.That(h.HasGivenUpFusions, Is.False, "the give-up was the old class's: the cut changed the class");
            Assert.That(h.FusionsGivenUp, Is.EqualTo(1), "the count stands");
            Assert.That(h.NotAchievedSeconds, Is.GreaterThan(0.0), "the time the old class stood given up was recorded when it ended");

            // The other building goes on: cut, rested and fused, with the refusal lifted.
            h.refuseHullForTest = false;
            Evaluate(detector, Upright(4, 20.3f), 4);
            Assert.That(detector.HitCount, Is.EqualTo(1), "the other building is hit");
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            yield return UntilCutsEnd(root, 30f, "the other building's cut");
            yield return UntilWithin(() => h.Fusions >= 1, 60f, "the other building fused");
            yield return UntilHull(root, () => h.IsSettled, 90f, "everything settled");
            WriteHullRecord(root, "at the end");
            int otherGroups = 0;
            foreach (HullGroup g in h.Groups) if (g.Building == other.Building) otherGroups++;
            Assert.That(otherGroups, Is.EqualTo(1), "the other building is one hull again");
            Assert.That(h.FusionsGivenUp, Is.EqualTo(1), "the give-up stands in the count");
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero, "no classification block left");
            Assert.That(MeshesNamed("Building hull"), Is.Zero, "the hull meshes went");
            Assert.That(MeshesNamed("Zantetsu Physics Cut"), Is.Zero);
            Assert.That(Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length, Is.Zero);
            Assert.That(Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length, Is.Zero);
        }

        /// <summary>
        /// **A candidate whose hull spans an obstacle is not adopted: the groups stay apart and are cut again; without
        /// the obstacle the next candidate is adopted and the fused hull is cut again.** The free side is moved 1 m away
        /// from the standing side before it rests; a thin static obstacle stands on the floor in the gap. Before the
        /// exchange no hull penetrates it; the candidate fills the gap and encloses the obstacle: the penetration at the
        /// same pose is recorded before and after, the difference is what the candidate would add, and the candidate is
        /// not adopted (one hull not achieved for this class): no body merged, both groups their own hull, settled
        /// without failure, one hull not achieved. A synthetic Slash through the anchored group is accepted, published
        /// and committed (the class changes). The obstacle goes; the freed top lands and is held; the new class's
        /// candidate is adopted into one group; a synthetic Slash through the fused hull is accepted, published and
        /// committed.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_CandidateAcrossAnObstacle_NotAdopted_GroupsCutAgain_ThenAdoptedAndCutAgain()
        {
            CutWorldRoot root = NewHullWorld(0.5f, profile => SetPrivate(profile, "buildingHullMaxNewPenetrationMetres", 0.005f));   // the diagnosis's value, read back from the profile
            BuildingHullFusion h = root.Hulls;
            Assert.That(h.Settings.maxNewPenetrationMetres, Is.EqualTo(0.005).Within(1e-9), "the profile's threshold reached the trial");
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Upright(1, 0.3f), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            HullGroup free = FreeSide(h), anchored = AnchoredSide(h);
            free.Body.position += new Vector3(1f, 0f, 0f);   // the gap: x in [0.3, 1.3]
            yield return Steps(2);
            GameObject obstacle = Track(new GameObject("Obstacle in the gap"));
            var box = obstacle.AddComponent<BoxCollider>();
            box.center = new Vector3(0.8f, -0.7f, 0f);
            box.size = new Vector3(0.2f, 0.6f, 0.6f);
            yield return UntilWithin(() => free.Kinematic, 30f, "the moved side rested and was held");
            yield return UntilWithin(() => h.HullsNotAchieved == 1, 60f, "the candidate hull judged against the obstacle");
            WriteHullRecord(root, "after the candidate across the obstacle");
            Assert.That(h.GroupCount == 2 && h.LiveColliders == 2 && h.Unions == 0, Is.True, "the groups stay apart, each its own hull, no body merged: one hull not achieved");
            Assert.That(free.State == HullGroupState.Idle && anchored.State == HullGroupState.Idle, Is.True, "both idle again");
            Assert.That(h.Fusions, Is.Zero, "the candidate was not adopted");
            Assert.That(h.MaxPenetrationBeforeExchange, Is.LessThan(0.01f), "before the exchange the hulls did not penetrate the obstacle: " + h.MaxPenetrationBeforeExchange);
            Assert.That(h.MaxRejectedNewPenetration, Is.GreaterThan(0.5f), "the candidate would have penetrated the obstacle: " + h.MaxRejectedNewPenetration + " m added");
            Assert.That(h.NotAchieved.Count, Is.EqualTo(1));
            Assert.That(h.NotAchieved[0], Does.Contain("static collider 'Obstacle in the gap'").And.Contain("added").And.Contain("allowed 0.005"), h.NotAchieved[0]);
            Assert.That(h.IsSettled, Is.True, "nothing in progress and nothing to try for this class: the end does not wait (" + h.DescribeUnsettled() + ")");
            Assert.That(h.IsSettledWithoutFailure, Is.True, "not achieved is a record, not a failure");
            Assert.That(h.IsOneHullAchieved, Is.False, "but one hull is not achieved: no overall pass (" + h.DescribeCounts() + ")");
            Assert.That(h.AggregationsEnded, Is.EqualTo(h.AggregationsBegun), "the terminal accounting holds");
            Assert.That(h.PenetrationRecords.Count, Is.EqualTo(1));
            StringAssert.Contains("NOT ADOPTED", h.PenetrationRecords[0]);
            StringAssert.Contains("Obstacle in the gap", h.PenetrationRecords[0]);
            Assert.That(h.FusionsGivenUp, Is.Zero);
            Assert.That(h.NotAchievedSecondsNow, Is.GreaterThan(0.0), "the class's standing is measured while it stands");
            Assert.That(h.MaxNotAchievedSeconds, Is.GreaterThan(0.0), "and the longest includes the one standing");
            StringAssert.Contains("not achieved", h.DescribeAggregationStates());
            AssertDisplayConsistent(root, "after the candidate was kept out");

            // The groups apart are cuttable: a synthetic Slash through the anchored group, judged in three parts.
            int begun = h.AggregationsBegun;
            yield return RecutJudged(root, detector, anchored, 2, "the anchored group after the candidate was not adopted");
            Assert.That(h.NotAchievedSeconds, Is.GreaterThan(0.0), "the class changed: the time it stood was recorded");
            Assert.That(h.BuildingsNotAchievedNow, Is.Zero);

            // The obstacle goes before the freed top rests: the new class's candidate is adopted.
            obstacle.SetActive(false);
            yield return UntilHull(root, () => h.Fusions == 1, 90f, "the new class's candidate adopted without the obstacle");
            yield return UntilOneGroup(root, "one group", 60f);
            WriteHullRecord(root, "after the adoption");
            Assert.That(h.AggregationsBegun, Is.GreaterThan(begun), "a new candidate was made for the new class");
            Assert.That(h.HullsNotAchieved, Is.EqualTo(1), "the record of the first candidate stands");
            Assert.That(h.Unions, Is.GreaterThanOrEqualTo(2), "every other group merged at the adoption (the synthetic sweep may have cut the moved side too): " + h.Unions + " merges");
            Assert.That(h.IsSettled && h.IsOneHullAchieved, Is.True, h.DescribeCounts());
            AssertDisplayConsistent(root, "after the adoption");

            // The fused hull is cuttable: a synthetic Slash through it, judged in three parts.
            yield return RecutJudged(root, detector, h.Groups[0], 3, "the fused hull");
            yield return UntilOneGroup(root, "fused again", 60f);
            WriteHullRecord(root, "at the end");
            Assert.That(h.CutsFailed, Is.Zero);
            AssertDisplayConsistent(root, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A support lost while a candidate is in flight drops the candidate; the groups are cuttable.** The free side
        /// rests and is held; the class's candidate is offered and refused for good (in flight, both groups participants).
        /// The floor goes: the rest releases the held participant (support lost) while the candidate is in flight. The
        /// offers accepted again, the candidate is verified before it is taken: a participant is not kinematic any more,
        /// so it is dropped -- no body merged, the released group falls, the anchored group idle. A synthetic Slash
        /// through the anchored group is accepted, published and committed. The ending gives everything back.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_SupportLostWhileACandidateIsInFlight_DropsIt_GroupsCuttable()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.refuseOffersForTest = int.MaxValue;
            Evaluate(detector, Upright(1, 0.3f), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            HullGroup free = FreeSide(h), anchored = AnchoredSide(h);
            yield return UntilWithin(() => free.Kinematic, 30f, "the free side rested and was held");
            yield return UntilWithin(() => h.FusionsInFlight == 1, 60f, "the class's candidate offered (refused)");
            Assert.That(free.State == HullGroupState.Fusing && anchored.State == HullGroupState.Fusing, Is.True, "both participants");

            _hullFloor.SetActive(false);
            yield return UntilWithin(() => h.GroupsReleased == 1, 20f, "the rest released the held participant for its lost ground");
            Assert.That(free.Kinematic, Is.False, "released while the candidate is in flight");
            Assert.That(h.FusionsInFlight, Is.EqualTo(1), "the candidate still in flight");
            float y0 = free.Body.position.y;

            h.refuseOffersForTest = 0;
            yield return UntilWithin(() => h.FusionsStale == 1, 30f, "the candidate dropped at its verification");
            WriteHullRecord(root, "after the drop");
            TestContext.Out.WriteLine(RestRecord(root));
            Assert.That(h.FusionsInFlight, Is.Zero);
            Assert.That(h.Unions == 0 && h.Fusions == 0, Is.True, "no body merged");
            Assert.That(h.GroupCount, Is.EqualTo(2));
            Assert.That(free.State == HullGroupState.Idle && anchored.State == HullGroupState.Idle, Is.True, "both idle again");
            Assert.That(h.AggregationsEnded, Is.EqualTo(h.AggregationsBegun), "the dropped candidate ended its aggregation");
            bool dropped = false;
            foreach (string e in h.Events) dropped |= e.Contains("dropped") && e.Contains("not kinematic any more");
            Assert.That(dropped, Is.True, "the drop names the released participant");
            yield return Steps(20);
            Assert.That(free.Body.position.y, Is.LessThan(y0 - 0.05f), "the released group falls");
            AssertDisplayConsistent(root, "after the drop");

            yield return RecutJudged(root, detector, anchored, 2, "the anchored group after the drop");
            yield return EndWorld(root);
            Assert.That(PhysicsCutClassification.Live, Is.Zero, "no classification block left");
            Assert.That(MeshesNamed("Building hull"), Is.Zero, "the hull meshes went");
            Assert.That(Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length, Is.Zero);
            Assert.That(Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length, Is.Zero);
        }

        /// <summary>
        /// **A group held after the building's wait was consumed brings the aggregation back.** The building stands 3 m
        /// up. A first cut frees a side, which is cut again in the air by a level plane: two free pieces, the lower one
        /// landing first. Its hold makes a candidate with the anchored side (the wait consumed); the upper piece, held
        /// later, would stand apart for good -- the hold looks at the building again with the cut's own clock, and the
        /// resting groups' candidate is adopted. Two resting groups of one building are never a completion.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullRest_AHoldAfterTheWaitWasConsumed_BringsTheAggregationBack()
        {
            CutWorldRoot root = NewHullWorld(0.3f);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, new Vector3(0f, 3f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Upright(1, 0.3f, 3f), 1);
            yield return UntilCutsEnd(root, 30f, "the first cut");
            HullGroup free = FreeSide(h);
            Assert.That(free, Is.Not.Null);
            // The free side, still in the air, cut by a level plane through it.
            float3 c = CentreOf(free);
            Evaluate(detector, Level(2, c.y, c.x - 0.3f, c.x + 0.3f, c.z - 3f, c.z + 3f), 1, 2);   // across the free side only, not the anchored one beside it
            Assert.That(detector.HitCount, Is.EqualTo(1), "the free side is hit in the air");
            yield return UntilCutsEnd(root, 30f, "the second cut");
            int freeGroups = 0;
            HullGroup upper = null;
            foreach (HullGroup g in h.Groups) { if (g.Kinematic) continue; freeGroups++; if (upper == null || CentreOf(g).y > CentreOf(upper).y) upper = g; }
            Assert.That(freeGroups, Is.EqualTo(2), "two free pieces");
            Assert.That(h.IsSettled, Is.False, "one fixed and two free groups: the end waits for their rest and re-aggregation (" + h.DescribeUnsettled() + ")");
            Assert.That(h.IsOneHullAchieved, Is.False);
            upper.Body.linearVelocity = new Vector3(1.5f, 7f, 0f);   // the upper piece thrown up and aside: it lands, and is held, well after the lower one
            // The first hold's candidate is adopted into the anchored side; while the other piece is still free, two resting groups may stand: not a completion until aggregated.
            yield return UntilWithin(() => h.Fusions >= 1, 60f, "the first piece held and its candidate adopted");
            yield return UntilWithin(() => h.GroupsHeld >= 2, 60f, "the second piece held");
            bool twoResting = false;
            foreach (HullGroup g in h.Groups) if (g.Kinematic && !g.Anchored) twoResting = true;
            if (twoResting) Assert.That(h.IsSettled, Is.False, "two resting groups of one building are not a completion: " + h.DescribeUnsettled());
            yield return UntilOneGroup(root, "the second piece's hold brought the aggregation back: one group", 60f);
            WriteHullRecord(root, "after the second aggregation");
            TestContext.Out.WriteLine(RestRecord(root));
            Assert.That(h.Reevaluations, Is.GreaterThanOrEqualTo(1), "a hold looked at the building again");
            Assert.That(h.AggregationsBegun, Is.EqualTo(2), "two aggregations: the first hold's, and the second hold's after the wait was consumed");
            Assert.That(h.AggregationsEnded, Is.EqualTo(2));
            Assert.That(h.UnionsByDeadline, Is.EqualTo(2), "both by the cut's clock, none reset");
            Assert.That(h.AllGroupsAtRest, Is.True);
            Assert.That(h.IsSettled && h.IsOneHullAchieved, Is.True, "the end: settled and one hull achieved (" + h.DescribeCounts() + ")");
            yield return EndWorld(root);
        }
    }
}
