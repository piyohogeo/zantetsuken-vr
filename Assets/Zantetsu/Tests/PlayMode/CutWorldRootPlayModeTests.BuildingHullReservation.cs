using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A Slash's acceptance fixed at the record (TL, 2026-09-30): through the ordinary detector, two sweeps of one Slash in
    /// one frame and the same Slash in the frames after, while the building's candidate waits for the dispatcher: one hit
    /// is held with a reservation, the second sweep is refused at the acceptance, the later frames find the group reserved;
    /// the Slash ending does not lose the request; the same Slash still cuts another building; when the candidate is
    /// adopted the held hit resumes where its reservation stands and the reservation is collected. A hit refused at once
    /// (NotAccepted) leaves the same record: no re-acceptance of the same target until the Slash ends, the same Slash on
    /// another group not hindered. And the check's owner helpers take a display-only owner.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        [UnityTest]
        public IEnumerator BuildingHullReservation_TwoSweepsAndLaterFrames_OneRequest_ResumedOnceAndCollected()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup other = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            // A cut, the sides resting; the candidate's offer refused for good: the participants stay Fusing, the building busy.
            h.refuseOffersForTest = int.MaxValue;
            Evaluate(detector, Upright(1, 0.3f), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            yield return UntilWithin(() => h.FusionsInFlight == 1, 60f, "the class's candidate waiting for the dispatcher");
            HullGroup fusing = null;
            foreach (HullGroup g in h.Groups) if (g.Building != other.Building) fusing = g;
            Assert.That(fusing != null && fusing.State == HullGroupState.Fusing, Is.True);
            Assert.That(h.Unions, Is.Zero, "no body merged before the candidate is judged");

            // Slash 2: two sweeps in one frame across the fusing group, then the same Slash in the frames after.
            float3 c = CentreOf(fusing);
            SlashSweep a = Level(2, c.y + 0.1f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), b = Level(2, c.y - 0.1f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f);
            int hitsBefore = h.Hits.Count;
            detector.Evaluate(new[] { a, b }, new long[] { 2 });
            Assert.That(detector.HitCount, Is.GreaterThanOrEqualTo(2), "both sweeps reached the acceptance (the candidates were collected before the first)");
            int held = 0, duplicates = 0;
            for (int i = 0; i < detector.HitCount; i++) { if (detector.HitAt(i).Acceptance == ProvisionalCutAcceptance.Held) held++; else if (detector.HitAt(i).Acceptance == ProvisionalCutAcceptance.NotAccepted) duplicates++; }
            Assert.That(held, Is.EqualTo(h.Hits.Count - hitsBefore), "each group crossed: one held request with a reservation");
            Assert.That(duplicates, Is.GreaterThanOrEqualTo(1), "the second sweep is refused at the acceptance: the group is reserved");
            Assert.That(h.HitsDuplicate, Is.EqualTo(duplicates));
            Assert.That(h.HeldNow, Is.EqualTo(held));
            Assert.That(fusing.IsConsumedBy(2), Is.True, "reserved");
            for (int frame = 0; frame < 3; frame++)
            {
                yield return null;
                Evaluate(detector, Level(2, c.y + 0.05f * frame, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), 2);
                Assert.That(detector.HitCount, Is.Zero, "frame " + frame + ": the reserved groups are not candidates for the same Slash");
            }

            Assert.That(h.Hits.Count, Is.EqualTo(hitsBefore + held) & Is.EqualTo(h.HitsPublished + h.HitsRefused + h.HitsPending), "still the same requests");
            // The same Slash on the other building: its own reservation, accepted.
            Evaluate(detector, Upright(2, 20.3f), 2);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "the same Slash cuts another group");
            yield return UntilCutsEnd(root, 30f, "the other building's cut");
            Assert.That(h.Hits[h.Hits.Count - 1].outcome, Is.EqualTo("Published"));

            // The Slash ends (not live any more): the held requests are kept, their reservations open.
            Evaluate(detector, Upright(3, 40f), 3);
            yield return null;
            Evaluate(detector, Upright(3, 40f), 3);
            Assert.That(h.HeldNow, Is.EqualTo(held), "the held requests outlive their Slash");
            Assert.That(fusing.IsConsumedBy(2), Is.True, "its reservation stands");
            Assert.That(h.Hits[hitsBefore].IsPending, Is.True);

            // The candidate goes on and is adopted: the held hits resume where their reservations stand; then the reservations are collected.
            h.refuseOffersForTest = 0;
            yield return UntilWithin(() => { for (int i = hitsBefore; i < hitsBefore + held; i++) if (h.Hits[i].IsPending) return false; return h.CutsInProgress == 0; }, 60f, "the held hits answered after the adoption");
            WriteHullRecord(root, "after the resumed requests");
            Assert.That(h.HitsResumed, Is.GreaterThanOrEqualTo(1));
            Assert.That(h.Hits[hitsBefore].outcome, Does.StartWith("Published").Or.StartWith("EmptySide").Or.StartWith("NoChange").Or.StartWith("Dropped"), h.Hits[hitsBefore].outcome);
            Assert.That(h.HeldNow, Is.Zero);
            Evaluate(detector, Upright(4, 40f), 4);   // Slash 2 is not live: the collected reservations go
            foreach (HullGroup g in h.Groups) if (g.Building != other.Building) Assert.That(g.IsConsumedBy(2), Is.False, "group " + g.Id + ": the reservation was collected with the Slash gone");
            Assert.That(h.Hits.Count, Is.EqualTo(h.HitsPublished + h.HitsRefused + h.HitsPending));
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A hit refused at once leaves the consumption's record.** Every real hit on the first building is refused at
        /// the acceptance (NotAccepted, a test's reason): two sweeps of one Slash in one frame make one refused request and
        /// one duplicate; the frames after find the group reserved (no candidate for the same Slash); the same Slash cuts
        /// the other building; the Slash ending collects the closed reservation, and a new Slash is accepted once the
        /// refusal is lifted.
        /// </summary>
        [UnityTest]
        public IEnumerator BuildingHullReservation_AHitRefusedAtOnce_LeavesTheConsumption_UntilTheSlashEnds()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup first = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            HullGroup other = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.refuseHitForTest = g => g.Building == first.Building ? "refused (test)" : null;

            float3 c = CentreOf(first);
            SlashSweep a = Level(2, c.y + 0.1f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), b = Level(2, c.y - 0.1f, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f);
            detector.Evaluate(new[] { a, b }, new long[] { 2 });
            Assert.That(detector.HitCount, Is.EqualTo(2), "both sweeps reached the acceptance");
            Assert.That(detector.HitAt(0).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted), "the first is refused at once");
            Assert.That(detector.HitAt(1).Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted), "the second is a duplicate: the group is reserved");
            Assert.That(h.Hits.Count, Is.EqualTo(1), "one request recorded");
            Assert.That(h.Hits[0].outcome, Does.StartWith("NotAccepted: refused (test)"), h.Hits[0].outcome);
            Assert.That(h.Hits[0].IsPending, Is.False, "answered at once");
            Assert.That(h.HitsDuplicate, Is.EqualTo(1));
            Assert.That(h.HitsRefused, Is.EqualTo(1));
            Assert.That(first.IsConsumedBy(2), Is.True, "the refused request left the consumption");
            Assert.That(first.State, Is.EqualTo(HullGroupState.Idle));
            for (int frame = 0; frame < 3; frame++)
            {
                yield return null;
                Evaluate(detector, Level(2, c.y + 0.05f * frame, c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f), 2);
                Assert.That(detector.HitCount, Is.Zero, "frame " + frame + ": the reserved group is not a candidate for the same Slash");
            }

            Assert.That(h.Hits.Count, Is.EqualTo(1), "no request was made again");
            // The same Slash on the other building: accepted.
            Evaluate(detector, Upright(2, 20.3f), 2);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "the same Slash cuts another group");
            yield return UntilCutsEnd(root, 30f, "the other building's cut");
            Assert.That(h.Hits[h.Hits.Count - 1].outcome, Is.EqualTo("Published"));

            // Slash 2 ends: the closed reservation is collected; a new Slash, with the refusal lifted, is accepted.
            Evaluate(detector, Upright(3, 40f), 3);
            Assert.That(first.IsConsumedBy(2), Is.False, "the reservation went with the Slash");
            h.refuseHitForTest = null;
            Evaluate(detector, Upright(4, c.x + 0.1f, c.y), 4);
            Assert.That(detector.HitCount == 1 && detector.HitAt(0).Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "a new Slash is accepted");
            yield return UntilCutsEnd(root, 30f, "the first building's cut");
            Assert.That(h.Hits[h.Hits.Count - 1].outcome, Is.EqualTo("Published"));
            WriteHullRecord(root, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>The check's owner helpers on a display-only owner (no shape, no body): no exception, the Root's position.</summary>
        [UnityTest]
        public IEnumerator BuildingHullCheck_OwnerHelpers_TakeADisplayOnlyOwner()
        {
            CutWorldRoot root = NewHullWorld();
            HullGroup group = AddHullBuilding(root, new Vector3(1f, 2f, 3f), new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out LogicalFragmentId member);
            yield return null;
            Assert.That(root.Owners.TryGet(member, out PhysicsFragmentOwner owner) && owner.IsDisplayOnly && owner.Shape == null && owner.Body == null, Is.True, "a display-only owner");
            Assert.That(SandboxPropSlashPlayerCheck.ConvexCountOf(owner), Is.Zero);
            Assert.That((SandboxPropSlashPlayerCheck.PieceCentreOf(owner) - owner.Root.transform.position).magnitude, Is.LessThan(1e-5f));
            Assert.That(SandboxPropSlashPlayerCheck.LowestVertexYOf(owner), Is.EqualTo(owner.Root.transform.position.y).Within(1e-5f));
            Assert.That((SandboxPropSlashPlayerCheck.LowestVertexOf(owner) - owner.Root.transform.position).magnitude, Is.LessThan(1e-5f));
            yield return EndWorld(root);
        }
    }
}
