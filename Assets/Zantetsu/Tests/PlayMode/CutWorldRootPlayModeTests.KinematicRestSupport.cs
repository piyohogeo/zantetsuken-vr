using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The kinematic rest's support watch (2026-09-29): a held or sleeping piece keeps the identity of what grounds it
    /// (a static collider, a kinematic body that is not a piece, an anchored piece) and is woken when that supporter is
    /// disabled, moved or retired -- only the pieces that depended on it -- while the switch to kinematic alone, whose
    /// contact exits are an artefact, wakes nothing. And a piece's clock is its own: another publication does not reset it.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static IEnumerator HoldAndExpectNoRelease(CutWorldRoot root, double seconds, string what)
        {
            int woken = root.Rest.WokenBySupportLoss, lost = root.Rest.LostGrounds, rested = root.Rest.RestedNow;
            yield return PhysicsSeconds(seconds);
            WriteKinematicRecord(root, what + ": held " + seconds + " s more, lost grounds " + root.Rest.LostGrounds);
            Assert.That(root.Rest.WokenBySupportLoss, Is.EqualTo(woken), what + ": the switch alone (its contact exits) woke nothing");
            Assert.That(root.Rest.LostGrounds, Is.EqualTo(lost), what + ": and no ground was found lost");
            Assert.That(root.Rest.RestedNow, Is.EqualTo(rested), what + ": still held");
        }

        /// <summary>
        /// **Disabling the floor under held pieces wakes them, and they fall; being held alone does not.** The two halves
        /// rest on the floor; a second of holding releases nothing; the floor's collider is disabled; the next turn finds the
        /// ground lost, wakes the lower half and, its chain broken, the upper one; both fall.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_DisablingTheFloor_WakesTheHeldPieces_AndTheyFall()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            yield return UntilRested(root, 2, "the pieces' rest");
            yield return HoldAndExpectNoRelease(root, 1.0, "before the floor goes");
            Rigidbody a = BodyOf(root, sides[0]), b = BodyOf(root, sides[1]);
            float yBefore = Mathf.Min(a.worldCenterOfMass.y, b.worldCenterOfMass.y);

            BoxCollider floor = GameObject.Find("Floor").GetComponent<BoxCollider>();
            floor.enabled = false;
            yield return PhysicsSeconds(0.1);   // a turn with a step (a frame may skip its step)
            WriteKinematicRecord(root, "after the floor was disabled");
            Assert.That(root.Rest.LostGrounds, Is.GreaterThanOrEqualTo(1), "the floor was found lost under a held piece");
            Assert.That(root.Rest.WokenBySupportLoss, Is.EqualTo(2), "both were woken: the lower lost its ground, the upper its chain");
            Assert.That(a.isKinematic || b.isKinematic, Is.False, "both dynamic again");
            yield return PhysicsSeconds(1.0);
            float fallen = yBefore - Mathf.Min(a.worldCenterOfMass.y, b.worldCenterOfMass.y);
            WriteKinematicRecord(root, "one second later: fallen " + fallen.ToString("F3") + " m");
            Assert.That(fallen, Is.GreaterThan(0.5f), "they fell");
            Assert.That(root.Rest.RestedNow, Is.Zero, "nothing held in the air");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Moving a kinematic support that is not a piece wakes the pieces on it, and they fall.** A kinematic platform (a
        /// Rigidbody that is not tracked) carries the building above a far floor; the halves rest on it; a second of holding
        /// releases nothing; the platform is moved 8 m aside; the halves are woken and fall.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_MovingAnExternalKinematicSupport_WakesThePiecesOnIt_AndTheyFall()
        {
            CutWorldRoot root = NewKinematicRestWorld(false, -12f);
            GameObject platform = Track(new GameObject("Kinematic Platform"));
            var box = platform.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(6f, 1f, 6f);
            var mover = platform.AddComponent<Rigidbody>();
            mover.isKinematic = true;
            mover.useGravity = false;
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            yield return UntilRested(root, 2, "the pieces' rest on the platform");
            yield return HoldAndExpectNoRelease(root, 1.0, "before the platform moves");
            Rigidbody a = BodyOf(root, sides[0]), b = BodyOf(root, sides[1]);
            float yBefore = Mathf.Min(a.worldCenterOfMass.y, b.worldCenterOfMass.y);

            Vector3 moved = mover.position + new Vector3(8f, 0f, 0f);   // clear of the pieces (the platform is 6 m wide)
            platform.transform.position = moved;
            mover.position = moved;
            yield return PhysicsSeconds(0.1);   // a turn with a step (a frame may skip its step)
            WriteKinematicRecord(root, "after the platform moved 8 m");
            Assert.That(root.Rest.LostGrounds, Is.GreaterThanOrEqualTo(1), "the platform was found moved under a held piece");
            Assert.That(root.Rest.WokenBySupportLoss, Is.EqualTo(2), "both were woken");
            Assert.That(a.isKinematic || b.isKinematic, Is.False, "both dynamic again");
            yield return PhysicsSeconds(1.0);
            float fallen = yBefore - Mathf.Min(a.worldCenterOfMass.y, b.worldCenterOfMass.y);
            WriteKinematicRecord(root, "one second later: fallen " + fallen.ToString("F3") + " m");
            Assert.That(fallen, Is.GreaterThan(0.5f), "they fell");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Retiring an anchored piece that supports a held piece wakes it, and it falls.** An anchored building is cut into
        /// the ledger-fixed lower side and a free upper half; the upper half rests on the anchored one; a second of holding
        /// releases nothing; the anchored side is retired (ledger, owners, geometry); the upper half is woken at once and
        /// falls to the floor, where it is held again.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_RetiringAnAnchoredSupporter_WakesThePieceOnIt_AndItFalls()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddAnchoredBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            LogicalFragmentId fixedSide = root.Owners.TryGet(sides[0], out PhysicsFragmentOwner o0) && o0.FixedByAnchors ? sides[0] : sides[1];
            LogicalFragmentId free = Other(fixedSide, sides);
            Assert.That(root.Owners.TryGet(fixedSide, out PhysicsFragmentOwner fixedOwner) && fixedOwner.FixedByAnchors && fixedOwner.Body.isKinematic, Is.True, "the layout: the lower side is fixed by the ledger");
            Assert.That(BodyOf(root, free).worldCenterOfMass.y, Is.GreaterThan(fixedOwner.Body.worldCenterOfMass.y), "the layout: the free half is the upper one");
            yield return UntilRested(root, 1, "the upper half's rest on the anchored side");
            Assert.That(root.Rest.TrackedAnchored, Is.EqualTo(1), "the anchored side is tracked as ground");
            yield return HoldAndExpectNoRelease(root, 1.0, "before the anchored side goes");
            Rigidbody upper = BodyOf(root, free);
            float yBefore = upper.worldCenterOfMass.y;

            Assert.That(root.Ledger.Retire(fixedSide), Is.True, "the anchored side retired in the ledger");
            Assert.That(root.Owners.Retire(fixedSide), Is.True, "and its owner");
            root.Geometry?.Forget(fixedSide);
            yield return null;
            WriteKinematicRecord(root, "after the anchored side's retirement");
            Assert.That(root.Rest.LostGrounds, Is.GreaterThanOrEqualTo(1), "the anchored ground was found lost");
            Assert.That(root.Rest.WokenBySupportLoss, Is.EqualTo(1), "the upper half was woken");
            Assert.That(upper.isKinematic, Is.False, "the upper half is dynamic again");
            yield return PhysicsSeconds(1.0);
            float fallen = yBefore - upper.worldCenterOfMass.y;
            WriteKinematicRecord(root, "one second later: fallen " + fallen.ToString("F3") + " m");
            Assert.That(fallen, Is.GreaterThan(0.5f), "it fell");
            yield return UntilRested(root, 1, "held again on the floor");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A sleeping piece is watched like a held one.** On a kinematic platform two boxes carry a slab, all held; one
        /// box is re-cut, so the slab returns to dynamic and is put to sleep (its support through the other box is still
        /// confirmed); then the platform is moved away: the other box, held, and the slab, asleep, lose their chain to the
        /// ground and are woken; the slab falls.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_ASleepingPiece_IsWokenWhenItsSupportGoes_LikeAHeldOne()
        {
            CutWorldRoot root = NewKinematicRestWorld(false, -12f);
            GameObject platform = Track(new GameObject("Kinematic Platform"));
            var box = platform.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, -1.5f, 0f);
            box.size = new Vector3(8f, 1f, 8f);
            var mover = platform.AddComponent<Rigidbody>();
            mover.isKinematic = true;
            mover.useGravity = false;
            LogicalFragmentId s1 = AddBuilding(root, new Vector3(-1.2f, 0f, 0f));
            LogicalFragmentId s2 = AddBuilding(root, new Vector3(1.2f, 0f, 0f));
            LogicalFragmentId slab = AddBuilding(root, new Vector3(0f, 2.05f, 0f));
            yield return null;
            yield return UntilRested(root, 3, "the two boxes and the slab held on the platform");
            var children = new LogicalFragmentId[2];
            yield return CutInTwo(root, s1, new float4(0f, 0f, 1f, 0f), children);
            Rigidbody slabBody = BodyOf(root, slab);
            WriteKinematicRecord(root, "after the re-cut of one box: slab asleep by the rest " + root.Rest.IsAsleepByRest(slab));
            Assert.That(root.Rest.SleptOnRelease, Is.EqualTo(1), "the slab was put to sleep with its support through the other box");
            Assert.That(slabBody.isKinematic, Is.False, "the slab is dynamic");
            float yBefore = slabBody.worldCenterOfMass.y;

            Vector3 moved = mover.position + new Vector3(10f, 0f, 0f);
            platform.transform.position = moved;
            mover.position = moved;
            yield return PhysicsSeconds(0.1);   // a turn with a step (a frame may skip its step)
            WriteKinematicRecord(root, "after the platform moved 10 m");
            Assert.That(root.Rest.LostGrounds, Is.GreaterThanOrEqualTo(1), "the platform was found moved");
            Assert.That(root.Rest.WokenBySupportLoss, Is.GreaterThanOrEqualTo(2), "the held box and the sleeping slab were woken (the children lose their ground by their own contacts)");
            Assert.That(BodyOf(root, s2).isKinematic, Is.False, "the other box is dynamic again");
            Assert.That(slabBody.IsSleeping(), Is.False, "the slab is awake");
            yield return PhysicsSeconds(1.0);
            float fallen = yBefore - slabBody.worldCenterOfMass.y;
            WriteKinematicRecord(root, "one second later: the slab fell " + fallen.ToString("F3") + " m");
            Assert.That(fallen, Is.GreaterThan(0.5f), "it fell");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A piece's clock is its own across other publications.** The halves of a tower are published together; half a
        /// second later the upper half is re-cut (nothing stands on it: the lower half is not a candidate); the lower half's
        /// clock is unchanged and it is held a timeout after its own publication, before the new quarters are.
        /// </summary>
        [UnityTest]
        public IEnumerator KinematicRest_AnotherPublication_DoesNotResetAPiecesClock()
        {
            CutWorldRoot root = NewKinematicRestWorld();
            LogicalFragmentId building = AddBuilding(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutInTwo(root, building, new float4(0f, 1f, 0f, 0f), sides);
            LogicalFragmentId upper = Upper(root, sides), lower = Other(upper, sides);
            Assert.That(root.Rest.TryGetClock(lower, out double lowerClock), Is.True, "the lower half's clock");
            double published = CutPhysicsStep.Clock.PhysicsSeconds;
            Assert.That(lowerClock, Is.EqualTo(published).Within(0.05), "started at its publication");
            yield return PhysicsSeconds(0.5);

            var quarters = new LogicalFragmentId[2];
            yield return CutInTwo(root, upper, new float4(1f, 0f, 0f, 0f), quarters);
            double secondPublished = CutPhysicsStep.Clock.PhysicsSeconds;
            WriteKinematicRecord(root, "after the upper half's re-cut at t " + secondPublished.ToString("F3") + " (the lower half's publication t " + lowerClock.ToString("F3") + ")");
            Assert.That(root.Rest.TryGetClock(lower, out double lowerClockAfter) && lowerClockAfter == lowerClock, Is.True, "the lower half's clock is unchanged by the other publication");
            Assert.That(root.Rest.MaxReCutCandidates, Is.Zero, "nothing stood on the upper half: no candidate");
            Assert.That(root.Rest.TryGetClock(quarters[0], out double quarterClock) && quarterClock >= secondPublished - 0.05, Is.True, "the new quarters' clocks start at their publication");

            double lowerRestedAt = -1.0;
            float deadline = Time.realtimeSinceStartup + RestSettleSeconds;
            while (lowerRestedAt < 0.0 && Time.realtimeSinceStartup < deadline)
            {
                if (root.Rest.IsRested(lower)) lowerRestedAt = CutPhysicsStep.Clock.PhysicsSeconds;
                yield return null;
            }

            WriteKinematicRecord(root, "the lower half held at t " + lowerRestedAt.ToString("F3"));
            Assert.That(lowerRestedAt, Is.GreaterThan(0.0), "the lower half was held in time");
            Assert.That(lowerRestedAt - lowerClock, Is.GreaterThanOrEqualTo(root.Rest.Settings.timeoutSeconds - 0.02), "after its own timeout");
            Assert.That(lowerRestedAt, Is.LessThan(secondPublished + root.Rest.Settings.timeoutSeconds - 0.1), "and before a timeout counted from the other publication would end");
            Assert.That(root.Rest.IsRested(quarters[0]) || root.Rest.IsRested(quarters[1]), Is.False, "the young quarters wait for their own clocks");
            yield return UntilRested(root, 3, "all three held");
            yield return EndWorld(root);
        }
    }
}
