using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        /// <summary>
        /// **A hit that takes a character's cut lets that character go, in its own evaluation, and nothing else.** Two
        /// prepared characters are candidates, one registered twice (still once). A hit whose cut is refused as a No-op
        /// keeps it; the hit that takes its cut drops exactly it from the candidates in that same evaluation -- while the
        /// other stays -- and it is not a candidate again. The handle prepared for the next individual is a candidate
        /// once, beside the other and never beside the old one. Its cut, accepted as Pending under a spent Main budget,
        /// lets it go at the hit too, while its root still stands until the cut is published: it waits for the
        /// publication, is not taken again, and is withdrawn by the publication.
        /// </summary>
        [UnityTest]
        public IEnumerator AHitThatTakesACharactersCut_LetsOnlyThatCharacterGo_AndTheNextHandleIsACandidateOnce()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            var a = PrepareBonedCharacter(out Transform _, out GameObject aRoot);
            var other = PrepareBonedCharacter(out Transform _, out GameObject otherRoot);
            otherRoot.transform.position = new Vector3(10f, 0f, 0f);
            yield return null;
            Assert.That(a.TryFinishPreparation() && other.TryFinishPreparation(), Is.True);
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(a);
            detector.AddCharacter(other);
            detector.AddCharacter(a);
            Assert.That(detector.CharacterCount, Is.EqualTo(2), "a character registered twice is a candidate once");

            List<SlashHitConfirmed> noOp = Evaluate(detector, CharacterLevel(1, 1f, -3f, 3f), 1);
            Assert.That(noOp.Count, Is.EqualTo(1), "the layout: only the first character is under the sweep");
            Assert.That(noOp[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.EmptySide));
            Assert.That(detector.HasCharacter(a) && detector.CharacterCount == 2, Is.True, "a No-op takes no cut: it stays a candidate");

            List<SlashHitConfirmed> cut = Evaluate(detector, CharacterLevel(2, 0.3f, -3f, 3f), 1, 2);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            Assert.That(detector.HasCharacter(a), Is.False, "the hit that took its cut let it go in that same evaluation");
            Assert.That(detector.HasCharacter(other), Is.True, "and nothing else");
            Assert.That(detector.CharacterCount, Is.EqualTo(1));
            Assert.That(aRoot.activeInHierarchy, Is.False, "the layout: withdrawn at its publication");
            yield return null;
            Assert.That(detector.HasCharacter(a), Is.False, "not a candidate again");
            yield return UntilCommitted(cut[0].Operation);
            Assert.That(a.IsDisposed, Is.True, "the layout: its handle ended with its cut");

            // The next individual's handle: a candidate once, beside the other, not beside the old one.
            // It stands apart from the first character's pieces, which a sweep at the origin would meet too.
            var next = PrepareBonedCharacter(out Transform _, out GameObject nextRoot);
            nextRoot.transform.position = new Vector3(0f, 0f, 20f);
            yield return null;
            Assert.That(next.TryFinishPreparation(), Is.True);
            detector.RemoveCharacter(a);
            detector.AddCharacter(next);
            detector.AddCharacter(next);
            Assert.That(detector.CharacterCount, Is.EqualTo(2), "the next handle and the other, each once");
            Assert.That(detector.HasCharacter(next) && detector.HasCharacter(other) && !detector.HasCharacter(a), Is.True);

            // Its cut waits for the budget: let go at the hit, standing until published.
            coldWorld.Driver.RemainingMainSeconds = () => 0;
            List<SlashHitConfirmed> nextCut = Evaluate(detector, CharacterLevel(3, 0.3f, 17f, 23f), 3);
            Assert.That(nextCut.Count, Is.EqualTo(1));
            Assert.That(nextCut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Pending));
            Assert.That(nextCut[0].Fragment, Is.EqualTo(next.Source), "the hit is the next individual's, a new fragment");
            Assert.That(nextCut[0].Fragment, Is.Not.EqualTo(cut[0].Fragment));
            Assert.That(detector.HasCharacter(next), Is.False);
            Assert.That(detector.CharacterCount, Is.EqualTo(1), "only the other is left");
            Assert.That(nextRoot.activeInHierarchy, Is.True, "the layout: it stands until its cut is published");
            yield return null;
            Assert.That(detector.HasCharacter(next), Is.False, "and is no candidate meanwhile");
            Assert.That(Evaluate(detector, CharacterLevel(3, 0.3f, 17f, 23f), 3), Is.Empty, "no second acceptance");
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            yield return UntilCommitted(nextCut[0].Operation);
            Assert.That(nextRoot.activeInHierarchy, Is.False, "withdrawn at the publication");
            Assert.That(detector.CharacterCount == 1 && detector.HasCharacter(other), Is.True, "the other is the only candidate left");
        }
    }
}
