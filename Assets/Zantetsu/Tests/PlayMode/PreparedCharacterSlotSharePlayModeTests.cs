using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        // Two models of one intake: each an entry of its own family and mesh, over the lent rig's four-vertex mesh.
        private const string SlotIntakeJson =
            "{\"assets\":[{\"family\":\"slot-a\",\"objectName\":\"slot mesh a\",\"topologyMap\":[0,1,2,3],\"topologyCount\":4},"
            + "{\"family\":\"slot-b\",\"objectName\":\"slot mesh b\",\"topologyMap\":[0,1,2,3],\"topologyCount\":4}]}";

        // One hull, the authored box about the rig's bone, in the renderer's bind frame.
        private static string SlotHullsJson()
        {
            string corners = string.Join(",", BoxCorners(0f).Select(c => c.x + "," + c.y + "," + c.z));
            return "{\"hulls\":[{\"boneName\":\"Lent character bone\",\"rendererBindVertices\":[" + corners + "],\"faceOffsets\":[0,4,8,12,16,20,24],"
                   + "\"faceIndices\":[" + string.Join(",", BoxFaceIndices()) + "]}]}";
        }

        private TextAsset _slotIntake, _slotHulls;

        // A slot of a crowd as MobPlanCrowd makes one: the lent rig (renderer, bone, motion body) with a Pose Table that
        // drives the bone, and a SandboxNpcCharacter given the share before its Start (PrepareAsSlot). Its setup object is
        // returned so the case can destroy the slot, as a crowd's end does.
        private SandboxNpcCharacter Slot(string family, string mesh, SandboxNpcCharacter.SlotShare share, Vector3 at, out GameObject setup)
        {
            // Unity's own null test: a case's teardown destroys the assets the previous case made.
            if (_slotIntake == null) _slotIntake = ColdTrack(new TextAsset(SlotIntakeJson) { name = "slot intake" });
            if (_slotHulls == null) _slotHulls = ColdTrack(new TextAsset(SlotHullsJson()) { name = "slot hulls" });
            SkinnedMeshRenderer r = LentBonedRig(out Transform _, out GameObject root, out Rigidbody motion);
            r.sharedMesh.name = mesh;
            root.transform.position = at;
            var table = ColdTrack(new TextAsset(PoseTablePlayerPlayModeTests.Table("Lent character bone")));
            root.AddComponent<PoseTablePlayer>().Configure(table, root.transform, 0.0, true);
            setup = new GameObject("slot " + family);
            setup.SetActive(false);
            var character = setup.AddComponent<SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(SandboxNpcCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(character, value);
            Set("world", coldWorld);
            Set("characterRoot", root);
            Set("motionBody", motion);
            Set("intake", _slotIntake);
            Set("hulls", _slotHulls);
            Set("family", family);
            character.PrepareAsSlot(share);
            setup.SetActive(true);
            return character;
        }

        private static T ShareField<T>(SandboxNpcCharacter.SlotShare share, string name) =>
            (T)typeof(SandboxNpcCharacter.SlotShare).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(share);

        private static string ShareFamily(SandboxNpcCharacter.SlotShare share)
        {
            object entry = typeof(SandboxNpcCharacter.SlotShare).GetField("entry", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(share);
            return entry == null ? null : (string)entry.GetType().GetField("family").GetValue(entry);
        }

        private static IEnumerator UntilPrepared(params SandboxNpcCharacter[] slots)
        {
            for (int i = 0; i < 60 && slots.Any(s => !s.IsPrepared && s.Failure == null); i++) yield return null;
        }

        private static IEnumerator Destroy(params GameObject[] setups)
        {
            foreach (GameObject s in setups) if (s != null) Object.Destroy(s);
            yield return null;
        }

        /// <summary>
        /// **Slots of one model share; another model's slot has its own.** Two slots of model a are given one share, as
        /// the crowd gives one per SlotShareKey, and a slot of model b another. Both a slots are prepared from one parse
        /// (the second reads the share) and skin through one fixed-scale cache with two users; b's share holds b alone.
        /// </summary>
        [UnityTest]
        public IEnumerator SlotsOfOneModelShare_AndAnotherModelsSlotHasItsOwn()
        {
            ColdWorld();
            var shareA = new SandboxNpcCharacter.SlotShare();
            var shareB = new SandboxNpcCharacter.SlotShare();
            SandboxNpcCharacter a1 = Slot("slot-a", "slot mesh a", shareA, new Vector3(0f, 0f, 0f), out GameObject s1);
            SandboxNpcCharacter a2 = Slot("slot-a", "slot mesh a", shareA, new Vector3(0f, 0f, 10f), out GameObject s2);
            SandboxNpcCharacter b1 = Slot("slot-b", "slot mesh b", shareB, new Vector3(0f, 0f, 20f), out GameObject s3);
            yield return UntilPrepared(a1, a2, b1);
            Assert.That(new[] { a1.Failure, a2.Failure, b1.Failure }, Is.All.Null);
            Assert.That(a1.IsPrepared && a2.IsPrepared && b1.IsPrepared, Is.True);
            Assert.That(a1.SlotShareKey, Is.EqualTo(a2.SlotShareKey), "one model, one key");
            Assert.That(b1.SlotShareKey, Is.Not.EqualTo(a1.SlotShareKey), "another model, another key");
            Assert.That(ShareFamily(shareA), Is.EqualTo("slot-a"));
            Assert.That(ShareFamily(shareB), Is.EqualTo("slot-b"));
            Assert.That(shareA.SharedReads, Is.EqualTo(1), "the second a slot read the share instead of parsing");
            Assert.That(shareB.SharedReads, Is.EqualTo(0));
            Assert.That(ShareField<int>(shareA, "users"), Is.EqualTo(2));
            Assert.That(ShareField<int>(shareB, "users"), Is.EqualTo(1));
            var cacheA = ShareField<VpFixedScaleSkinCache>(shareA, "scaleCache");
            var cacheB = ShareField<VpFixedScaleSkinCache>(shareB, "scaleCache");
            Assert.That(cacheA, Is.Not.Null.And.Not.SameAs(cacheB), "each model its own cache");
            Assert.That(cacheA.ActiveInputCount, Is.EqualTo(2), "both a slots skin through a's cache");
            Assert.That(cacheB.ActiveInputCount, Is.EqualTo(1));
            yield return Destroy(s1, s2, s3);
        }

        /// <summary>
        /// **Another model's share is refused and left as it was.** A share already holding model a is given to a slot of
        /// model b: b is not prepared (no handle), and the share keeps a's entry, its reads, its user count and its cache,
        /// which still serves the a slot.
        /// </summary>
        [UnityTest]
        public IEnumerator AnotherModelsShare_IsRefused_AndTheShareIsLeftAsItWas()
        {
            ColdWorld();
            var shareA = new SandboxNpcCharacter.SlotShare();
            SandboxNpcCharacter a1 = Slot("slot-a", "slot mesh a", shareA, Vector3.zero, out GameObject s1);
            yield return UntilPrepared(a1);
            Assert.That(a1.IsPrepared, Is.True, a1.Failure);
            var cacheA = ShareField<VpFixedScaleSkinCache>(shareA, "scaleCache");
            int reads = shareA.SharedReads;

            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("not prepared: the slot share holds slot-a, not slot-b"));
            SandboxNpcCharacter b1 = Slot("slot-b", "slot mesh b", shareA, new Vector3(0f, 0f, 20f), out GameObject s2);
            yield return null;
            yield return null;
            Assert.That(b1.Failure, Is.EqualTo("the slot share holds slot-a, not slot-b"));
            Assert.That(b1.Handle, Is.Null, "nothing was prepared");
            Assert.That(b1.IsPrepared || b1.IsTarget, Is.False);
            Assert.That(ShareFamily(shareA), Is.EqualTo("slot-a"), "the share still holds a");
            Assert.That(shareA.SharedReads, Is.EqualTo(reads), "nothing read from it");
            Assert.That(ShareField<int>(shareA, "users"), Is.EqualTo(1), "no user added");
            Assert.That(ShareField<VpFixedScaleSkinCache>(shareA, "scaleCache"), Is.SameAs(cacheA));
            Assert.That(cacheA.ActiveInputCount, Is.EqualTo(1), "the a slot's input still in it");
            Assert.That(a1.Activate(), Is.True, "the a slot is still usable");
            yield return Destroy(s1, s2);
        }

        /// <summary>
        /// **One slot of a model goes; the other still draws and cuts; the last one frees the share's cache, once.** Two
        /// slots of one model share a cache. Destroying the first leaves one user and the cache alive. The second is
        /// activated -- drawn -- and a Slash cuts it through to Commit. Destroying it frees the cache: the share holds no
        /// cache and the old one takes no more inputs.
        /// </summary>
        [UnityTest]
        public IEnumerator OneSlotGoing_LeavesTheOtherUsable_AndTheLastFreesTheShareOnce()
        {
            ColdWorld();
            var share = new SandboxNpcCharacter.SlotShare();
            SandboxNpcCharacter a1 = Slot("slot-a", "slot mesh a", share, new Vector3(0f, 0f, 20f), out GameObject s1);
            SandboxNpcCharacter a2 = Slot("slot-a", "slot mesh a", share, Vector3.zero, out GameObject s2);
            yield return UntilPrepared(a1, a2);
            Assert.That(a1.IsPrepared && a2.IsPrepared, Is.True, a1.Failure + " / " + a2.Failure);
            var cache = ShareField<VpFixedScaleSkinCache>(share, "scaleCache");

            yield return Destroy(s1);
            Assert.That(ShareField<int>(share, "users"), Is.EqualTo(1), "one user left");
            Assert.That(ShareField<VpFixedScaleSkinCache>(share, "scaleCache"), Is.SameAs(cache), "the cache stays for it");
            Assert.That(cache.ActiveInputCount, Is.EqualTo(1));

            Assert.That(a2.Activate(), Is.True);
            SkinnedMeshRenderer drawn = a2.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(s => s.enabled);
            Assert.That(drawn.sharedMesh, Is.Not.Null, "drawn through the shared cache");
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(a2.Handle);
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            List<SlashHitConfirmed> hits = Evaluate(detector, CharacterLevel(1, 0.3f, -3f, 3f), 1);
            Assert.That(hits.Count, Is.EqualTo(1), "the remaining slot is hit");
            Assert.That(hits[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            yield return UntilCommitted(hits[0].Operation);
            Assert.That(coldWorld.Display.RenderFragmentCount, Is.GreaterThan(0), "its pieces are drawn");

            yield return Destroy(s2);
            Assert.That(ShareField<int>(share, "users"), Is.EqualTo(0));
            Assert.That(ShareField<VpFixedScaleSkinCache>(share, "scaleCache"), Is.Null, "the last slot freed the cache");
            Assert.That(cache.ActiveInputCount, Is.EqualTo(0));
            SkinnedMeshRenderer probe = LentBonedRig(out Transform _, out GameObject probeRoot, out Rigidbody _);
            Assert.Throws<System.ObjectDisposedException>(() => cache.Prepare(probe, probeRoot.transform), "the cache is disposed");
        }
    }
}
