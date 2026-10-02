using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// A prepared character's first cut with no room for its sibling constraint (2026-09-29), through the product's own
    /// hit and update. The two are not the same answer: a hit meets it as **Full** -- not accepted, nothing held, the
    /// character still a target and cut by a later hit once there is room -- while a request already **Held** (for display
    /// room) that the driver takes up with no constraint room stays **Held**, and goes on once the room is back.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        [UnityTest]
        public IEnumerator NoConstraintRoom_AHitIsFull_TheCharacterStaysATarget_AndALaterHitCutsIt()
        {
            yield return ReadyCharacter(SmallRoom);
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            coldWorld.Driver.ConfigureConstraints(BuildingWorldD6Settings.Adopted, 0);
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);

            bone.localPosition = new Vector3(0, 0, 5);   // where the sweeps below pass (as the held case does)
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.NotAccepted), "full: not accepted");
            Assert.That(hits[0].Admission, Is.EqualTo(LogicalCutAdmission.Full));
            Assert.That(hits[0].Operation.IsSet, Is.False);
            Assert.That(handle.IsHeld, Is.False, "full is not held");
            Assert.That(coldWorld.Driver.HeldCharacterCutCount, Is.Zero);
            Assert.That(coldWorld.Driver.ConstraintRoomRefusals, Is.EqualTo(1));
            Assert.That(handle.IsReady, Is.True, "the character stays usable");
            Assert.That(detector.HasCharacter(handle), Is.True, "and a target");
            Assert.That(root.activeInHierarchy, Is.True, "not withdrawn");

            // Room again: a later hit, by another Slash, cuts it the ordinary way.
            coldWorld.Driver.ConfigureConstraints(BuildingWorldD6Settings.Adopted, 8);
            hits = Evaluate(detector, CharacterLevel(2, 0.3f, 2f, 8f), 2);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Operation.IsSet, Is.True, "accepted: one Operation");
            yield return UntilCommitted(hits[0].Operation);
            Assert.That(coldWorld.TerminationRequested, Is.False);
        }

        [UnityTest]
        public IEnumerator AHeldRequestTakenUpWithNoConstraintRoom_StaysHeld_AndGoesOnOnceThereIsRoom()
        {
            yield return ReadyCharacter(SmallRoom);
            var handle = PrepareBonedCharacter(out Transform bone, out GameObject root);
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);
            coldWorld.Driver.RemainingMainSeconds = () => 1;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(handle);
            VpCutOutputReservation held = HoldAllButThreeVertices();

            bone.localPosition = new Vector3(0, 0, 5);
            var hits = Evaluate(detector, CharacterLevel(1, 0.3f, 2f, 8f), 1);
            Assert.That(hits.Count, Is.EqualTo(1));
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Held), "held for display room");
            Assert.That(handle.IsHeld, Is.True);

            // The display room comes back, but there is no constraint room: taken up, the request stays held.
            coldWorld.Driver.ConfigureConstraints(BuildingWorldD6Settings.Adopted, 0);
            Assert.That(coldWorld.Storage.TryCancelCutOutput(held), Is.True);
            for (int i = 0; i < 3; i++) yield return null;
            Assert.That(handle.IsHeld, Is.True, "held, not full: the request is kept");
            Assert.That(handle.Operation.IsSet, Is.False);
            Assert.That(coldWorld.Driver.ConstraintRoomRefusals, Is.GreaterThan(0));
            Assert.That(root.activeInHierarchy, Is.True);

            // Room: the same request goes on once.
            coldWorld.Driver.ConfigureConstraints(BuildingWorldD6Settings.Adopted, 8);
            yield return null;
            Assert.That(handle.IsHeld, Is.False, "taken up");
            Assert.That(handle.Operation.IsSet, Is.True);
            yield return UntilCommitted(handle.Operation);
            Assert.That(coldWorld.Ledger.OperationCount, Is.EqualTo(1), "one acceptance, not two");
            Assert.That(coldWorld.TerminationRequested, Is.False);
        }
    }
}
