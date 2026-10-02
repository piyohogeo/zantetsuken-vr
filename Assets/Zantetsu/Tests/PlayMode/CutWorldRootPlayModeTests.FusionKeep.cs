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
    /// A group cut keeps the old group's Root and Body for one side and makes one new body for the other (TL, 2026-09-30):
    /// each direction, the choice, the kept body's state as the side after the cut (fixed or free, masses, motion, tables,
    /// generation, the rest's relations), the Finals held, partial and refused, a re-cut, a re-aggregation and the ending.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static int CountBodies() => Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;

        /// <summary>After a publication (Finals held): the sides of a cut group and what each holds.</summary>
        private static void AssertKeptSides(CutWorldRoot root, List<LogicalFragmentId> members, List<LogicalFragmentId> crossed, Rigidbody oldBody, GameObject oldRoot, int bodiesBefore, string when, out FusedGroup positive, out FusedGroup negative)
        {
            positive = null; negative = null;
            foreach (LogicalFragmentId f in crossed)
            {
                Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && o.IsFused, Is.True);
                positive = o.Group;   // the crossed originals stand on the positive side
            }

            foreach (LogicalFragmentId f in members)
            {
                if (crossed.Contains(f)) continue;
                root.Owners.TryGet(f, out PhysicsFragmentOwner o);
                if (!ReferenceEquals(o.Group, positive)) negative = o.Group;
            }

            GameObject shadow = GameObject.Find("Shadow of " + crossed[0].value);
            Assert.That(shadow, Is.Not.Null, when + ": the copy stands");
            Rigidbody copyBody = shadow.GetComponentInChildren<MeshCollider>().attachedRigidbody;
            foreach (FusedGroup g in root.Fusion.Groups) if (g.Body == copyBody) negative = g;
            Assert.That(positive != null && negative != null && !ReferenceEquals(positive, negative), Is.True, when + ": two sides");
            Assert.That(copyBody, Is.EqualTo(negative.Body), when + ": the copies stand on the negative side");
            Assert.That(ReferenceEquals(positive.Body, oldBody) ^ ReferenceEquals(negative.Body, oldBody), Is.True, when + ": exactly one side keeps the old body");
            Assert.That(ReferenceEquals(positive.Root, oldRoot) ^ ReferenceEquals(negative.Root, oldRoot), Is.True, when + ": and the old Root");
            Assert.That(CountBodies(), Is.EqualTo(bodiesBefore + 1), when + ": one new body");
            foreach (FusedGroup side in new[] { positive, negative })
            {
                foreach (LogicalFragmentId f in side.Fragments)
                {
                    Assert.That(root.Owners.TryGet(f, out PhysicsFragmentOwner o) && ReferenceEquals(o.Group, side) && o.Root.transform.parent == side.Root.transform, Is.True, when + ": member " + f.value + " stands under its side");
                    foreach (MeshCollider c in side.byFragment[f].colliders) Assert.That(c.attachedRigidbody, Is.EqualTo(side.Body), when + ": member " + f.value + "'s colliders belong to its side's body");
                    Assert.That(IsHitTarget(root, f), Is.True, when + ": member " + f.value + " is a hit target");
                    Assert.That(root.Placement.TryGetGeometryLocalToWorld(f, default, 0f, out Matrix4x4 _), Is.Not.EqualTo(VpFragmentPlacementKind.Missing), when + ": and is placed");
                }

                Assert.That(side.Kinematic, Is.EqualTo(side.Body.isKinematic), when + ": " + side.Root.name + " is held as its body is");
                Assert.That(side.Busy, Is.True, when + ": both sides wait for the Finals");
            }
        }

        /// <summary>
        /// **Either side keeps the old Root and Body; one new body; everything stands on its side.** Four members resting as
        /// one group, a horizontal cut crossing the two upper ones, with the positive side kept, the negative kept, and the
        /// choice by colliders; the Finals held: the old body and Root on one side, one new body, the crossed originals
        /// positive and their copies negative, every collider on its side's body, the members hit targets and placed, the
        /// kept members' Transforms untouched, and the exclusion by its layer pair. After the Finals the pair is back and
        /// the groups whole; a re-cut of the kept side and its re-aggregation go through, then the ending.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionKeep_EitherSideKeepsTheOldBody_OneNewBody_AndEverythingStandsOnItsSide([Values(1, -1, 0)] int keep)
        {
            CutWorldRoot root = NewFusionWorld();
            root.Fusion.keepSideForTest = keep;
            var members = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, members);
            FusedGroup old = root.Fusion.Groups[0];
            Rigidbody oldBody = old.Body;
            GameObject oldRoot = old.Root;
            int oldId = oldBody.GetInstanceID(), bodies = CountBodies(), lentBefore = ExclusionLayerPairs.LentNow, generation = old.Generation;
            var poses = new Dictionary<LogicalFragmentId, Matrix4x4>();
            foreach (LogicalFragmentId f in members) { root.Owners.TryGet(f, out PhysicsFragmentOwner o); poses[f] = o.Root.transform.localToWorldMatrix; }
            root.Fusion.holdFinals = true;
            int cuts = root.Fusion.GroupCuts;
            Assert.That(root.TryAsk(SlashAsk(root, members[0], new float4(0f, 1f, 0f, -0.5f), 2, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the group cut");
            var tally = new ContactTally();
            List<LogicalFragmentId> crossed = Classify(root, members, tally, "");
            AssertKeptSides(root, members, crossed, oldBody, oldRoot, bodies, "published", out FusedGroup positive, out FusedGroup negative);
            FusedGroup kept = ReferenceEquals(positive.Body, oldBody) ? positive : negative;
            if (keep == 1) Assert.That(kept, Is.SameAs(positive), "the positive side kept as asked");
            if (keep == -1) Assert.That(kept, Is.SameAs(negative), "the negative side kept as asked");
            Assert.That(oldBody.GetInstanceID(), Is.EqualTo(oldId));
            Assert.That(kept.Generation, Is.GreaterThan(generation), "the kept group's generation moved");
            TestContext.Out.WriteLine("keep " + keep + ": kept " + (ReferenceEquals(kept, positive) ? "positive" : "negative") + "; " + root.Fusion.MaxPublishBreakdown);
            foreach (LogicalFragmentId f in kept.Fragments)
            {
                root.Owners.TryGet(f, out PhysicsFragmentOwner o);
                Assert.That(o.Root.transform.localToWorldMatrix, Is.EqualTo(poses[f]), "kept member " + f.value + " did not move");
            }

            MeshCollider own = positive.byFragment[crossed[0]].colliders[0];
            MeshCollider copy = GameObject.Find("Shadow of " + crossed[0].value).GetComponentInChildren<MeshCollider>();
            Assert.That(Physics.GetIgnoreLayerCollision(own.gameObject.layer, copy.gameObject.layer), Is.True, "the copy and its original apart by the layer pair");
            root.Fusion.holdFinals = false;
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the Finals");
            Assert.That(ExclusionLayerPairs.LentNow, Is.EqualTo(lentBefore), "the pair is back");
            foreach (FusedGroup g in root.Fusion.Groups) foreach (LogicalFragmentId f in g.Fragments) foreach (MeshCollider c in g.byFragment[f].colliders) Assert.That(c.attachedRigidbody, Is.EqualTo(g.Body), "after the Finals every collider on its group's body");

            // A re-cut of the group the kept body carries, once it rests again, and its aggregation.
            yield return UntilWithin(() => GroupsNear(root, Vector3.zero) == 1 && GroupOfBuildingAt(root, Vector3.zero) is FusedGroup g && !g.Busy && g.Kinematic, 40f, "one resting group again");
            FusedGroup again = GroupOfBuildingAt(root, Vector3.zero);
            LogicalFragmentId member = again.Representative;
            Bounds b = ColliderBoundsOf(root, member);
            cuts = root.Fusion.GroupCuts;
            Assert.That(root.TryAsk(SlashAsk(root, member, new float4(0f, 0f, 1f, -b.center.z), 3, 0f)), Is.True, "a re-cut");
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the re-cut");
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "its Finals");
            yield return UntilWithin(() => GroupsNear(root, Vector3.zero) == 1 && GroupOfBuildingAt(root, Vector3.zero) is FusedGroup g && !g.Busy && g.Kinematic, 40f, "and one resting group again");
            AssertGroupsWhole(root, "after the re-cut");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return EndWorld(root);
            Assert.That(ExclusionLayerPairs.LentNow, Is.Zero, "nothing lent after the ending");
        }

        /// <summary>
        /// **An anchored group kept as its free side turns free, falls, and is judged by the rest as a new side.** An anchored
        /// building (the anchor at the left) cut in two and fused: one anchored group, held. A vertical cut keeps the old
        /// body for the free right side: it is dynamic and awake, its mass its side's; the new body (left) is held
        /// by its anchor; the rest follows the old body afresh -- it is not held again before its own timeout.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionKeep_AnAnchoredGroupKeptAsItsFreeSide_TurnsFree_AndIsFollowedAfresh()
        {
            CutWorldRoot root = NewFusionWorld();
            LogicalFragmentId building = AddAnchoredBuildingAt(root, Vector3.zero, new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), halves);
            yield return UntilFused(root, 2, "both halves fused");
            yield return UntilWithin(() => root.Fusion.GroupCount == 1 && root.Fusion.Groups[0].Kinematic && !root.Fusion.Groups[0].Busy, 30f, "one held group");
            FusedGroup old = root.Fusion.Groups[0];
            Assert.That(old.Anchored && old.Body.isKinematic, Is.True, "anchored and held");
            Rigidbody oldBody = old.Body;
            float massBefore = oldBody.mass;
            root.Fusion.keepSideForTest = 1;   // the positive side (x > 0.2): free, away from the anchor
            int cuts = root.Fusion.GroupCuts, heldBefore = root.Rest.RestedNow;
            Assert.That(root.TryAsk(SlashAsk(root, halves[0], new float4(1f, 0f, 0f, -0.2f), 2, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the cut");
            FusedGroup kept = null, other = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Body == oldBody) kept = g; else other = g; }
            Assert.That(kept, Is.Not.Null, "the old body carries a side");
            Assert.That(kept.Kinematic || kept.Body.isKinematic, Is.False, "the kept side is free");
            Assert.That(kept.Body.IsSleeping(), Is.False, "and awake");
            // Held by the anchor the plane gives it (the crossed anchored member itself stands on the positive side until its
            // Final, as before: the negative side's fixedness comes from the anchors' distribution, not from its members' flags).
            Assert.That(other.Kinematic && other.Body.isKinematic, Is.True, "the new side is held by its anchor");
            yield return Steps(1);
            Assert.That(kept.Body.mass, Is.EqualTo((float)kept.Mass).Within(1e-3f * (float)kept.Mass), "the kept body carries its side's mass (" + kept.Body.mass + "; the group's body had " + massBefore + ")");
            float y = kept.Body.worldCenterOfMass.y;
            yield return Steps(8);
            TestContext.Out.WriteLine("kept free side: mass " + kept.Body.mass.ToString("F3") + " (group was " + massBefore.ToString("F3") + "), centre y " + y.ToString("F3") + " -> " + kept.Body.worldCenterOfMass.y.ToString("F3") + ", rest held before " + heldBefore + " now " + root.Rest.RestedNow);
            Assert.That(kept.Body.isKinematic || kept.Kinematic, Is.False, "still free within the rest's timeout: no held state carried from the old body");
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the Finals");
            AssertGroupsWhole(root, "after the Finals");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A spinning, falling group's sides carry its rigid motion, whichever keeps the old body.** Four members resting,
        /// lifted clear of the floor and released spinning; a vertical cut: both sides turn at the group's angular velocity,
        /// and their velocities differ by it across their centres (the field of one rigid motion), the kept body as the new.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionKeep_ASpinningGroupsSides_CarryItsRigidMotion([Values(1, -1)] int keep)
        {
            CutWorldRoot root = NewFusionWorld();
            var members = new List<LogicalFragmentId>();
            yield return FourMemberGroup(root, Vector3.zero, 1, members);
            FusedGroup g0 = root.Fusion.Groups[0];
            Rigidbody oldBody = g0.Body;
            root.Fusion.OnGroupReleasing(oldBody);   // free, as the rest returns a group
            oldBody.isKinematic = false;
            root.Fusion.CompleteMassNow();
            oldBody.position += new Vector3(0f, 3f, 0f);
            oldBody.angularVelocity = new Vector3(0.3f, 2f, 0.1f);
            oldBody.linearVelocity = new Vector3(0.5f, 0f, 0.2f);
            root.Fusion.keepSideForTest = keep;
            root.Fusion.holdFinals = true;
            int cuts = root.Fusion.GroupCuts;
            Assert.That(root.TryAsk(SlashAsk(root, members[0], new float4(1f, 0f, 0f, -0.3f), 2, 0f)), Is.True);
            yield return UntilWithin(() => root.Fusion.GroupCuts > cuts, 30f, "the cut");
            FusedGroup a = null, b = null;
            foreach (FusedGroup g in root.Fusion.Groups) { if (g.Body == oldBody) a = g; else if (b == null) b = g; }
            Assert.That(a != null && b != null, Is.True, "the old body carries one side, a new one the other");
            Vector3 wa = a.Body.angularVelocity, wb = b.Body.angularVelocity;
            Vector3 relative = a.Body.linearVelocity - b.Body.linearVelocity;
            Vector3 expected = Vector3.Cross(wb, a.Body.worldCenterOfMass - b.Body.worldCenterOfMass);
            TestContext.Out.WriteLine("keep " + keep + ": w kept " + wa.ToString("F4") + " new " + wb.ToString("F4") + "; relative velocity " + relative.ToString("F4") + " expected " + expected.ToString("F4"));
            Assert.That((wa - wb).magnitude, Is.LessThan(0.05f * Mathf.Max(0.1f, wb.magnitude)), "both sides turn as one");
            Assert.That((relative - expected).magnitude, Is.LessThan(0.05f * Mathf.Max(0.1f, expected.magnitude) + 0.02f), "their velocities are one rigid field");
            Assert.That(wb.magnitude, Is.GreaterThan(1f), "the spin was carried");
            root.Fusion.holdFinals = false;
            yield return UntilWithin(() => root.Fusion.CutsInProgress == 0, 30f, "the Finals");
            yield return EndWorld(root);
        }
    }
}
