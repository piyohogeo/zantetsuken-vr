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
    /// A fused group's support after the fusion (2026-09-30): the pieces' outside support relations pass to the group,
    /// a held group is a tracked supporter and not an unconditional ground, and when its support goes the group's one
    /// body returns to dynamic with the group's mass properties applied, falls from the next physics step, rests again
    /// on landing and is the building's resting group again -- repeatedly, and without touching another building's
    /// group or a group an anchor fixes.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private GameObject AddStaticPlatform(string name, Vector3 centre, Vector3 size)
        {
            GameObject platform = Track(new GameObject(name));
            platform.transform.position = centre;
            var box = platform.AddComponent<BoxCollider>();
            box.size = size;
            return platform;
        }

        private static FusedGroup GroupOf(CutWorldRoot root, LogicalFragmentId member)
        {
            Assert.That(root.Owners.TryGet(member, out PhysicsFragmentOwner owner) && owner.IsFused, Is.True, "piece " + member.value + " is a fused member");
            return owner.Group;
        }

        private static Bounds GroupBoundsOf(CutWorldRoot root, FusedGroup group)
        {
            Bounds b = default; bool first = true;
            foreach (LogicalFragmentId f in group.Fragments)
            {
                Bounds one = ColliderBoundsOf(root, f);
                if (first) { b = one; first = false; } else b.Encapsulate(one);
            }

            Assert.That(first, Is.False, "the group has members with colliders");
            return b;
        }

        private static IEnumerator OnePhysicsStep()
        {
            long step = CutPhysicsStep.Clock.StepId;
            yield return Until(() => CutPhysicsStep.Clock.StepId > step, "one physics step");
        }

        /// <summary>
        /// **A resting group falls when its platform goes, rests again on landing, three times over, and the others stand.**
        /// A building on a kinematic platform (over a static one, over the floor), another building on its own platform,
        /// and an anchored building; all fused. The platform is destroyed, then the next one's collider disabled, then the
        /// floor moved down: each time the group's one body is dynamic within the rest's next turn with the whole mass
        /// and centre applied, falls from the next physics step, lands, is held again by the rest and is the building's
        /// resting group again (its state and its body agree). The other building's group and the anchored group never
        /// move or change. At the end the re-held group is cut, and its held sides merge back into one resting group.
        /// </summary>
        [UnityTest]
        public IEnumerator FusionSupport_AGroupOnAPlatform_FallsWhenThePlatformGoes_AndRestsAgain_ThreeTimes()
        {
            CutWorldRoot root = NewFusionWorld(-12f);
            GameObject platformA = AddKinematicPlatform();   // top y -1, under the first building
            GameObject platformB = AddStaticPlatform("Platform B", new Vector3(0f, -4.5f, 0f), new Vector3(6f, 1f, 6f));   // top y -4
            AddStaticPlatform("Platform C", new Vector3(20f, -1.5f, 0f), new Vector3(6f, 1f, 6f));   // the other building's own
            GameObject floor = GameObject.Find("Floor");
            Assert.That(floor, Is.Not.Null);
            LogicalFragmentId first = AddBuilding(root, Vector3.zero);
            LogicalFragmentId other = AddBuilding(root, new Vector3(20f, 0f, 0f));
            LogicalFragmentId anchoredBuilding = AddAnchoredBuildingAt(root, new Vector3(40f, 0f, 0f), new float3(-0.5f, -0.9f, 0f));
            yield return null;
            var halves = new LogicalFragmentId[2];
            yield return CutInTwo(root, first, new float4(0f, 1f, 0f, 0f), halves);
            LogicalFragmentId lower = Other(UpperByRoot(root, halves), halves);
            var otherHalves = new LogicalFragmentId[2];
            yield return CutInTwo(root, other, new float4(0f, 1f, 0f, 0f), otherHalves);
            var anchoredHalves = new LogicalFragmentId[2];
            yield return CutInTwo(root, anchoredBuilding, new float4(0f, 1f, 0f, 0f), anchoredHalves);
            yield return UntilFused(root, 6, "all six pieces fused");
            Assert.That(root.Fusion.GroupCount, Is.EqualTo(3), "three resting groups, one a building");
            FusedGroup group = GroupOf(root, lower), otherGroup = GroupOf(root, otherHalves[0]), anchoredGroup = GroupOf(root, anchoredHalves[0]);
            Assert.That(group.Kinematic && group.Body.isKinematic && !group.Anchored, Is.True, "the first building's group rests, held");
            Assert.That(otherGroup.Kinematic && otherGroup.Body.isKinematic && !otherGroup.Anchored, Is.True);
            Assert.That(anchoredGroup.Kinematic && anchoredGroup.Body.isKinematic && anchoredGroup.Anchored, Is.True, "the anchored building's group is anchored");
            double whole = group.Mass;
            Vector3 otherAt = otherGroup.Body.position, anchoredAt = anchoredGroup.Body.position;
            Assert.That(root.Rest.TryDescribeGroup(group.Body, out string described), Is.True, "the rest follows the group");
            TestContext.Out.WriteLine("group after the fusion: " + described);
            Assert.That(described, Does.Contain("held").And.Contain("group"));
            Assert.That(root.Rest.TryDescribeGroup(anchoredGroup.Body, out string anchoredDescribed) && anchoredDescribed.Contains("anchored"), Is.True, "the anchored group is followed as ground");

            // Static while the platform stands.
            Bounds before = ColliderBoundsOf(root, lower);
            yield return PhysicsSeconds(1.0);
            Assert.That(group.Kinematic && group.Body.isKinematic, Is.True, "still held a second later");
            Assert.That((ColliderBoundsOf(root, lower).center - before.center).magnitude, Is.LessThan(1e-4f), "and it did not move");

            var landings = new[] { -4f, -12f, -15f };
            for (int cycle = 0; cycle < 3; cycle++)
            {
                int releasedBefore = root.Fusion.GroupsReleased, heldBefore = root.Fusion.GroupsHeld, groupsBefore = root.Fusion.GroupCount, fusedBefore = root.Fusion.FusedPieces;
                Bounds standing = GroupBoundsOf(root, group);
                switch (cycle)
                {
                    case 0:
                        UnityEngine.Object.DestroyImmediate(platformA);
                        _objects.Remove(platformA);
                        break;
                    case 1:
                        platformB.GetComponent<BoxCollider>().enabled = false;
                        break;
                    default:
                        floor.transform.position += new Vector3(0f, -3f, 0f);
                        break;
                }

                // Dynamic within the rest's next turn, with the group's mass properties on its one body.
                yield return UntilWithin(() => !group.Kinematic && group.Body != null && !group.Body.isKinematic, 1f, "cycle " + cycle + ": the group is released");
                Assert.That(root.Fusion.GroupsReleased, Is.EqualTo(releasedBefore + 1), "one release");
                Assert.That(group.Body.mass, Is.EqualTo(whole).Within(1e-3), "the whole mass on the one body");
                Assert.That((group.Body.worldCenterOfMass - standing.center).magnitude, Is.LessThan(0.05f), "the centre of mass is the group's (the two halves' box centre)");
                Assert.That(RigidbodiesUnder(group.Fragments, root), Is.EqualTo(1), "no member body came back");
                Vector3 velocity = group.Body.linearVelocity;
                float y = ColliderBoundsOf(root, lower).center.y;
                // Falls from the next physics step.
                yield return OnePhysicsStep();
                TestContext.Out.WriteLine("cycle " + cycle + ": released with v " + velocity.ToString("F3") + ", after one step v " + group.Body.linearVelocity.ToString("F3") + " y " + ColliderBoundsOf(root, lower).center.y.ToString("F4") + " from " + y.ToString("F4"));
                Assert.That(group.Body.linearVelocity.y, Is.LessThan(0f), "falling after one step");
                Assert.That(ColliderBoundsOf(root, lower).center.y, Is.LessThan(y), "and lower");

                // Lands, rests, and is the building's resting group again.
                yield return UntilWithin(() => group.Kinematic && group.Body != null && group.Body.isKinematic, RestSettleSeconds, "cycle " + cycle + ": held again on landing");
                Assert.That(root.Fusion.GroupsHeld, Is.EqualTo(heldBefore + 1), "one hold");
                Assert.That(ColliderBoundsOf(root, lower).min.y, Is.EqualTo(landings[cycle]).Within(0.05f), "landed on the next support");
                Assert.That(root.Fusion.GroupCount, Is.EqualTo(groupsBefore), "no group made or lost");
                Assert.That(root.Fusion.FusedPieces, Is.EqualTo(fusedBefore), "the members stayed fused");
                Assert.That(root.Rest.TryDescribeGroup(group.Body, out described) && described.Contains("held"), Is.True, "held by the rest: " + described);
                TestContext.Out.WriteLine("cycle " + cycle + ": " + described);
                // The others are untouched.
                Assert.That(otherGroup.Kinematic && otherGroup.Body.isKinematic && (otherGroup.Body.position - otherAt).magnitude < 1e-4f, Is.True, "the other building's group stands");
                Assert.That(anchoredGroup.Kinematic && anchoredGroup.Body.isKinematic && anchoredGroup.Anchored && (anchoredGroup.Body.position - anchoredAt).magnitude < 1e-4f, Is.True, "the anchored group stands");
                WriteFusionRecord(root, "cycle " + cycle + " done");
            }

            // The re-held group is the building's resting group: a cut of it makes two dynamic sides, which rest and merge into one again.
            int mergedBefore = root.Fusion.GroupsMerged;
            Bounds bounds = ColliderBoundsOf(root, lower);
            ProvisionalCutAsk ask = Ask(lower, InShapeFrame(root, lower, new float4(1f, 0f, 0f, -bounds.center.x)));
            ask.positiveSeparationImpulse = 12f;
            ask.negativeSeparationImpulse = 12f;
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return Until(() => root.Fusion.PreparationsInFlight == 0, "the preparation of the cut asked (ask)");
            Assert.That(root.Fusion.GroupCuts, Is.EqualTo(1), "the re-held group was cut");
            yield return Until(() => root.Fusion.FinalsPublished == 2 || root.Fusion.FinalsFailed > 0, "the Finals");
            Assert.That(root.Fusion.FinalsFailed, Is.Zero);
            yield return UntilWithin(() => root.Fusion.GroupCount == 3 && root.Fusion.GroupsMerged > mergedBefore, RestSettleSeconds, "the held sides merged into one resting group again");
            WriteFusionRecord(root, "after the cut and the merge");
            Assert.That(otherGroup.Kinematic && (otherGroup.Body.position - otherAt).magnitude < 1e-4f, Is.True);
            Assert.That(anchoredGroup.Kinematic && (anchoredGroup.Body.position - anchoredAt).magnitude < 1e-4f, Is.True);
            yield return EndWorld(root);
        }
    }
}
