using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        /// <summary>
        /// **A crowd slot's skin history, as the Quest builds set it** (2026-10-19, the skins' AppSW motion). The slot's
        /// drawn renderer -- the fixed-scale copy -- carries the source's Object motion and skinned motion vectors; with
        /// the character's skinMotionHistory on, the skinned motion vectors are off while it is dormant (not drawn), off
        /// in the frame it is activated (its first drawn frame), and on from the next. A cut takes the skin off at once
        /// (the VP display draws the pieces); the slot prepared again and reused is again off for its first frame back
        /// and on after. Off -- as every scene but the Quest builds' has it -- nothing is changed.
        /// </summary>
        [UnityTest]
        public IEnumerator ACrowdSlotsSkin_IsWithoutItsStaleHistory_OnItsFirstFrameDrawn_AndAfterAReuse()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            // Destroyed by the class's tear-down however the case ends, so that no later case has this view.
            var target = ColdTrack(new RenderTexture(64, 64, 24) { name = "skin history test view" });
            var view = ColdTrack(new GameObject("skin history test camera")).AddComponent<Camera>();
            view.transform.SetPositionAndRotation(new Vector3(0f, 0.125f, -1.5f), Quaternion.identity);
            view.targetTexture = target;
            SandboxNpcCharacter slot = NeedleSlot(Vector3.zero, out GameObject setup);
            ColdTrack(setup);
            typeof(SandboxNpcCharacter).GetField("skinMotionHistory", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(slot, true);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            SkinnedMeshRenderer skin = slot.Renderer;
            var sources = new List<SkinnedMeshRenderer>(slot.CharacterRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true));
            sources.Remove(skin);
            Assert.That(sources.Count, Is.EqualTo(1), "the source renderer, beside its fixed-scale copy");
            Assert.That(skin.motionVectorGenerationMode, Is.EqualTo(sources[0].motionVectorGenerationMode), "the copy carries the source's motion mode");
            Assert.That(skin.motionVectorGenerationMode, Is.EqualTo(MotionVectorGenerationMode.Object));
            yield return null;
            Assert.That(skin.enabled, Is.False, "dormant: not drawn");
            Assert.That(skin.skinnedMotionVectors, Is.False, "dormant: its history is not kept for a first frame");

            ActivateStill(slot);
            Assert.That(skin.enabled, Is.True);
            Assert.That(skin.skinnedMotionVectors, Is.False, "its first drawn frame: no skinned motion vectors (no stale vertices read)");
            yield return null;
            Assert.That(skin.isVisible, Is.True, "drawn by the view");
            Assert.That(skin.skinnedMotionVectors, Is.True, "from the next frame, its bones' motion");

            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(slot.Handle);
            List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
            Assert.That(cut.Count, Is.EqualTo(1));
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            // Off by the renderer (the cut display's beginning) or by the whole root (a withdrawal of the root).
            bool Drawn() => skin.enabled && skin.gameObject.activeInHierarchy;
            int accepted = Time.frameCount, off = -1;
            for (int i = 0; i < 120 && off < 0; i++)
            {
                if (!Drawn()) off = Time.frameCount;
                else yield return null;
            }

            TestContext.Out.WriteLine("the skin went off " + (off - accepted) + " frames after the cut was accepted (renderer " + (skin.enabled ? "on" : "off")
                + ", root " + (skin.gameObject.activeInHierarchy ? "active" : "inactive") + ")");
            Assert.That(off, Is.GreaterThanOrEqualTo(0), "the cut takes the skin off: the VP display draws it");
            yield return UntilCommitted(cut[0].Operation);
            for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(slot.IsReturnReady, Is.True);
            Assert.That(skin.skinnedMotionVectors, Is.False, "not drawn since the cut");
            Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
            yield return UntilPrepared(slot);
            Assert.That(slot.Renderer, Is.SameAs(skin), "the reused slot draws with the same renderer");
            yield return null;
            ActivateStill(slot);
            Assert.That(Drawn(), Is.True, "reused: drawn again");
            Assert.That(skin.skinnedMotionVectors, Is.False, "reused: its first drawn frame without the vertices of its last individual");
            Assert.That(skin.motionVectorGenerationMode, Is.EqualTo(MotionVectorGenerationMode.Object), "its motion mode kept through the reuse");
            yield return null;
            Assert.That(skin.skinnedMotionVectors, Is.True, "and its bones' motion from the next frame");

            Object.Destroy(setup);
            Object.Destroy(view.gameObject);
            yield return null;
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }

        // The crowd's template as a scene holds it: inactive, its character and the character's root under one holder,
        // with the builds' skin history setting stored on it (the field the builds' serialized write sets). The needle
        // of NeedleSlot, not prepared: the crowd prepares its copies, never the template.
        private GameObject NeedleTemplate()
        {
            var (points, faces) = NeedleHull();
            string intakeJson = "{\"assets\":[{\"family\":\"needle\",\"objectName\":\"needle mesh\",\"topologyMap\":[" + string.Join(",", Enumerable.Range(0, points.Length))
                                + "],\"topologyCount\":" + points.Length + "}]}";
            var offsets = new List<int> { 0 };
            foreach (int[] f in faces) offsets.Add(offsets[offsets.Count - 1] + f.Length);
            string hullJson = "{\"hulls\":[{\"boneName\":\"Lent character bone\",\"rendererBindVertices\":[" + string.Join(",", points.Select(p => p.x.ToString("R") + "," + p.y.ToString("R") + "," + p.z.ToString("R")))
                              + "],\"faceOffsets\":[" + string.Join(",", offsets) + "],\"faceIndices\":[" + string.Join(",", faces.SelectMany(f => f)) + "]}]}";
            SkinnedMeshRenderer r = LentBonedRig(out Transform _, out GameObject root, out Rigidbody motion);
            Mesh m = r.sharedMesh;
            m.Clear();
            double3 centre = points.Aggregate((x, y) => x + y) / points.Length;
            m.vertices = points.Select(p => new Vector3((float)p.x, (float)p.y, (float)p.z)).ToArray();
            m.normals = points.Select(p => ((Vector3)(float3)(p - centre)).normalized).ToArray();
            m.uv = Enumerable.Repeat(new Vector2(.5f, .5f), points.Length).ToArray();
            m.boneWeights = Enumerable.Repeat(new BoneWeight { weight0 = 1 }, points.Length).ToArray();
            m.bindposes = new[] { Matrix4x4.identity };
            m.triangles = faces.SelectMany(f => f).ToArray();
            m.name = "needle mesh";
            root.AddComponent<PoseTablePlayer>();
            var holder = ColdTrack(new GameObject("needle crowd template"));
            holder.SetActive(false);
            root.transform.SetParent(holder.transform, false);
            var characterObject = new GameObject("needle template character");
            characterObject.transform.SetParent(holder.transform, false);
            var character = characterObject.AddComponent<SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(SandboxNpcCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(character, value);
            Set("world", coldWorld);
            Set("characterRoot", root);
            Set("motionBody", motion);
            Set("intake", ColdTrack(new TextAsset(intakeJson) { name = "needle intake" }));
            Set("hulls", ColdTrack(new TextAsset(hullJson) { name = "needle hulls" }));
            Set("family", "needle");
            Set("skinMotionHistory", true);
            return holder;
        }

        /// <summary>
        /// **A spare slot made from the crowd's template** (TL 2026-10-19): as MobPlanCrowd makes its spares -- the
        /// inactive template instantiated, the copy's pose configured, the copy prepared as a slot, then activated -- the
        /// copy carries the builds' setting, and its own drawn skin (its fixed-scale copy, not the template's renderer)
        /// is without the skinned motion vectors while dormant and in its first drawn frame, and with them from the next.
        /// </summary>
        [UnityTest]
        public IEnumerator ASpareSlotMadeFromTheTemplate_CarriesTheSkinHistorySetting_ToItsOwnDrawnSkin()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            var target = ColdTrack(new RenderTexture(64, 64, 24) { name = "skin history test view" });
            var view = ColdTrack(new GameObject("skin history test camera")).AddComponent<Camera>();
            view.transform.SetPositionAndRotation(new Vector3(0f, 0.125f, -1.5f), Quaternion.identity);
            view.targetTexture = target;
            GameObject template = NeedleTemplate();
            SkinnedMeshRenderer templateRenderer = template.GetComponentInChildren<SkinnedMeshRenderer>(true);

            GameObject spare = ColdTrack(Object.Instantiate(template));
            spare.name = "MobPlan slot 1";
            var slot = spare.GetComponentInChildren<SandboxNpcCharacter>(true);
            FieldInfo setting = typeof(SandboxNpcCharacter).GetField("skinMotionHistory", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That((bool)setting.GetValue(slot), Is.True, "the copy carries the template's setting");
            Assert.That(slot.CharacterRoot.transform.IsChildOf(spare.transform), Is.True, "the copy's character is its own root's");
            var table = ColdTrack(new TextAsset(PoseTablePlayerPlayModeTests.Table("Lent character bone")));
            slot.CharacterRoot.GetComponent<PoseTablePlayer>().Configure(table, slot.CharacterRoot.transform, 0.0, true);
            slot.PrepareAsSlot(new SandboxNpcCharacter.SlotShare());
            slot.CharacterRoot.SetActive(true);
            spare.SetActive(true);
            slot.enabled = true;
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            SkinnedMeshRenderer skin = slot.Renderer;
            Assert.That(skin, Is.Not.SameAs(templateRenderer), "the copy draws with its own renderer");
            Assert.That(skin.transform.IsChildOf(spare.transform), Is.True, "under the copy");
            Assert.That(skin.motionVectorGenerationMode, Is.EqualTo(MotionVectorGenerationMode.Object));
            Assert.That(template.activeSelf, Is.False, "the template stays inactive");
            yield return null;
            Assert.That(skin.enabled, Is.False, "dormant: not drawn");
            Assert.That(skin.skinnedMotionVectors, Is.False, "dormant: no history kept for a first frame");

            ActivateStill(slot);
            Assert.That(skin.enabled && skin.gameObject.activeInHierarchy, Is.True);
            Assert.That(skin.skinnedMotionVectors, Is.False, "its first drawn frame: no stale vertices read");
            yield return null;
            Assert.That(skin.isVisible, Is.True, "drawn by the view");
            Assert.That(skin.skinnedMotionVectors, Is.True, "from the next frame, its bones' motion");

            Object.Destroy(spare);
            Object.Destroy(template);
            yield return null;
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }
    }
}
