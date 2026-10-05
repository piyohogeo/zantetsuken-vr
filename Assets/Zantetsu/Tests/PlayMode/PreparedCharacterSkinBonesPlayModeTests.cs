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
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        private const string HullOnlyBone = "Hull only bone";

        private const string BonesIntakeJson =
            "{\"assets\":[{\"family\":\"bones\",\"objectName\":\"bones mesh\",\"topologyMap\":[0,1,2,3],\"topologyCount\":4}]}";

        // Two hulls in the renderer's bind frame: the authored box about the weighted bone (at the origin), and the same
        // box about a bone four metres along z that no vertex is weighted to.
        private static string BonesHullsJson()
        {
            string Corners(float z) => string.Join(",", BoxCorners(0f).Select(c => c.x + "," + c.y + "," + (c.z + z)));
            string Hull(string bone, float z) => "{\"boneName\":\"" + bone + "\",\"rendererBindVertices\":[" + Corners(z) + "],\"faceOffsets\":[0,4,8,12,16,20,24],"
                                                   + "\"faceIndices\":[" + string.Join(",", BoxFaceIndices()) + "]}";
            return "{\"hulls\":[" + Hull("Lent character bone", 0f) + "," + Hull(HullOnlyBone, 4f) + "]}";
        }

        /// <summary>
        /// A crowd slot over the lent rig given four listed bones -- a helper, the weighted bone, a bone only a hull
        /// stands on, another helper -- every vertex on the second. <paramref name="shortened"/>: the renderer's list
        /// and the mesh are shortened to the weighted bone, as a model's preparation does
        /// (<see cref="VpSkinBoneCompaction"/>); <paramref name="map"/>: 1 gives it the model's complete correspondence
        /// (<see cref="VpSkinBones"/>), 2 one whose Transforms are in another order, 0 none.
        /// </summary>
        private SandboxNpcCharacter BonesSlot(bool shortened, int map, Vector3 at, PoseLodDirector lod, out GameObject setup, out Transform hullOnly)
        {
            SkinnedMeshRenderer r = LentBonedRig(out Transform bone, out GameObject root, out Rigidbody motion);
            Transform Extra(string name, Vector3 local)
            {
                Transform t = new GameObject(name).transform;
                t.SetParent(root.transform, false);
                t.localPosition = local;
                return t;
            }

            Transform helper0 = Extra("Helper 0", new Vector3(0f, 3f, 0f));
            hullOnly = Extra(HullOnlyBone, new Vector3(0f, 0f, 4f));
            Transform helper1 = Extra("Helper 1", new Vector3(2f, 0f, 0f));
            Transform[] all = { helper0, bone, hullOnly, helper1 };
            Mesh mesh = r.sharedMesh;
            mesh.name = "bones mesh";
            mesh.bindposes = all.Select(b => b.worldToLocalMatrix * r.transform.localToWorldMatrix).ToArray();
            mesh.boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 1, weight0 = 1 }, 4).ToArray();
            r.bones = all;
            r.updateWhenOffscreen = true;   // as the scenes' characters are built
            if (shortened)
            {
                int[] kept = VpSkinBoneCompaction.UsedBones(mesh);
                Assert.That(kept, Is.EqualTo(new[] { 1 }));
                Mesh compact = ColdTrack(VpSkinBoneCompaction.Compact(mesh, kept));
                Assert.That(VpSkinBoneCompaction.Differences(mesh, compact, kept), Is.Empty);
                VpSkinBoneMap boneMap = ColdTrack(VpSkinBoneMap.Create(all.Select(b => b.name).ToArray(), mesh.bindposes, kept));
                r.sharedMesh = compact;
                r.bones = kept.Select(i => all[i]).ToArray();
                if (map == 1) r.gameObject.AddComponent<VpSkinBones>().Set(boneMap, all);
                if (map == 2) r.gameObject.AddComponent<VpSkinBones>().Set(boneMap, all.Reverse().ToArray());
            }

            root.transform.position = at;
            // The table drives both hull bones (a hull's bone must be driven); the weighted one is its first.
            var table = ColdTrack(new TextAsset(PoseTablePlayerPlayModeTests.Table("Lent character bone", HullOnlyBone)));
            root.AddComponent<PoseTablePlayer>().Configure(table, root.transform, 0.0, true);
            setup = new GameObject("bones slot");
            setup.SetActive(false);
            var character = setup.AddComponent<SandboxNpcCharacter>();
            void Set(string field, object value) => typeof(SandboxNpcCharacter).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(character, value);
            Set("world", coldWorld);
            Set("characterRoot", root);
            Set("motionBody", motion);
            Set("intake", ColdTrack(new TextAsset(BonesIntakeJson) { name = "bones intake" }));
            Set("hulls", ColdTrack(new TextAsset(BonesHullsJson()) { name = "bones hulls" }));
            Set("family", "bones");
            if (lod != null) Set("poseLod", lod);
            character.PrepareAsSlot(new SandboxNpcCharacter.SlotShare());
            setup.SetActive(true);
            return character;
        }

        private static T SlotField<T>(SandboxNpcCharacter slot, string name) =>
            (T)typeof(SandboxNpcCharacter).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(slot);

        /// <summary>
        /// **A hull on a bone the renderer no longer lists** (TL, 2026-10-05). A slot whose renderer lists the weighted
        /// bone alone, with its model's complete bone correspondence beside it, is prepared as the slot listing all four
        /// bones is: two hulls, the second on the unweighted bone -- this individual's own Transform -- and the hulls'
        /// points in their bones' frames the same numbers. It is then cut at its current pose through the ordinary
        /// detector, the cut is committed, the slot comes back, is prepared again and carries a second individual that
        /// is cut in its turn.
        /// </summary>
        [UnityTest]
        public IEnumerator ASlotOfAShortenedBoneList_ResolvesAHullOnABoneTheRendererNoLongerLists_AndIsCutAndReused()
        {
            ColdWorld();
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;
            SandboxNpcCharacter full = BonesSlot(false, 0, new Vector3(0f, 0f, 40f), null, out GameObject fullSetup, out Transform fullHullOnly);
            SandboxNpcCharacter slot = BonesSlot(true, 1, Vector3.zero, null, out GameObject setup, out Transform hullOnly);
            yield return UntilPrepared(full, slot);
            Assert.That(full.IsPrepared, Is.True, full.Failure);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            Assert.That(full.Renderer.bones.Length, Is.EqualTo(4));
            Assert.That(slot.Renderer.bones.Length, Is.EqualTo(1), "the drawn renderer lists the weighted bone alone");
            Assert.That(slot.Renderer.sharedMesh.bindposes.Length, Is.EqualTo(1));
            Assert.That(slot.HullCount, Is.EqualTo(2));
            Assert.That(full.HullCount, Is.EqualTo(2));
            Transform[] convexBones = SlotField<Transform[]>(slot, "_convexBones");
            Assert.That(convexBones[0], Is.SameAs(slot.CharacterRoot.transform.Find("Lent character bone")));
            Assert.That(convexBones[1], Is.SameAs(hullOnly), "the second hull stands on this individual's unweighted bone");
            Assert.That(slot.Renderer.bones.Contains(hullOnly), Is.False, "which the renderer does not list");
            Assert.That(SlotField<Transform[]>(full, "_convexBones")[1], Is.SameAs(fullHullOnly));
            Assert.That(SlotField<float3[]>(slot, "_bankPoints"), Is.EqualTo(SlotField<float3[]>(full, "_bankPoints")), "the hulls' points in their bones' frames are the full list's, number for number");

            // The first individual, cut at its current pose; then the slot again.
            ActivateStill(slot);
            VpPreparedCharacterCut first = slot.Handle;
            var detector = new SlashHitDetector(coldWorld, in k_characterHitSettings);
            detector.AddCharacter(first);
            yield return null;
            List<SlashHitConfirmed> cut = Evaluate(detector, PlaneSweep(1, new double3(0, 1, 0), new double3(0.125, 0.125, 0.125), new double3(1, 0, 0)), 1);
            Assert.That(cut.Count, Is.EqualTo(1), "the Slash through the body hits it");
            Assert.That(cut[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            yield return UntilCommitted(cut[0].Operation);
            for (int i = 0; i < 60 && !slot.IsReturnReady; i++) yield return null;
            Assert.That(slot.IsReturnReady, Is.True);
            Assert.That(slot.TryReprepare(), Is.True, slot.Failure);
            yield return UntilPrepared(slot);
            Assert.That(slot.IsPrepared, Is.True, slot.Failure);
            ActivateStill(slot);
            VpPreparedCharacterCut second = slot.Handle;
            Assert.That(slot.Activations, Is.EqualTo(2));
            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(slot.Renderer.bones.Length, Is.EqualTo(1), "the reused slot still lists the weighted bone alone");
            Assert.That(slot.Renderer.GetComponent<VpSkinBones>(), Is.Null, "(the drawn renderer is the slot's own unit-scale one; the correspondence stays beside the model's)");
            Assert.That(SlotField<Transform[]>(slot, "_convexBones")[1], Is.SameAs(hullOnly), "and its second hull is still on the unweighted bone");
            detector.AddCharacter(second);
            yield return null;
            Assert.That(MobPlanReuseProbe.TryAim(second, null, Vector3.forward, 2, 0.0, 0.25f, out SlashSweep sweep, out string aimed), Is.True, aimed);
            List<SlashHitConfirmed> mine = Evaluate(detector, sweep, 2).FindAll(h => h.Fragment == second.Source);
            Assert.That(mine.Count, Is.EqualTo(1), "the second individual is hit at its current pose");
            Assert.That(mine[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            yield return UntilCommitted(mine[0].Operation);

            Object.Destroy(setup);
            Object.Destroy(fullSetup);
            yield return null;
            for (int i = 0; i < 120 && !coldWorld.Shutdown(); i++) yield return null;
            Assert.That(coldWorld.IsReleased, Is.True);
        }

        /// <summary>
        /// **What resolves the hull's bone is the correspondence, not the renderer's list.** The same shortened slot
        /// without its model's bone map does not find the unweighted hull bone; with a correspondence whose Transforms
        /// are in another order it is refused before anything is resolved.
        /// </summary>
        [UnityTest]
        public IEnumerator WithoutItsModelsBoneMap_AShortenedListDoesNotResolveThatHull_AndACorrespondenceOutOfOrderIsRefused()
        {
            ColdWorld();
            // Each refusal is said once, as an error, by the slot itself.
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("the character was not prepared: no bone " + HullOnlyBone));
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("the character was not prepared: the character.s bones and its model.s bone map are not of one correspondence"));
            SandboxNpcCharacter bare = BonesSlot(true, 0, Vector3.zero, null, out GameObject s1, out Transform _);
            SandboxNpcCharacter wrong = BonesSlot(true, 2, new Vector3(0f, 0f, 20f), null, out GameObject s2, out Transform _);
            yield return UntilPrepared(bare, wrong);
            Assert.That(bare.IsPrepared, Is.False);
            Assert.That(bare.Failure, Is.EqualTo("no bone " + HullOnlyBone), "the renderer's shortened list does not hold the hull's bone");
            Assert.That(wrong.IsPrepared, Is.False);
            Assert.That(wrong.Failure, Does.StartWith("the character's bones and its model's bone map are not of one correspondence"), wrong.Failure);
            TestContext.Out.WriteLine("refused: " + wrong.Failure);
            yield return Destroy(s1, s2);
        }

        // A camera the test draws with, once when asked: what Unity's own culling makes of a renderer.
        private Camera ReturnCamera(string name)
        {
            var target = ColdTrack(new RenderTexture(64, 64, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            target.Create();
            Camera camera = ColdTrack(new GameObject(name)).AddComponent<Camera>();
            camera.enabled = false;
            camera.fieldOfView = 60f;   // on a square target: thirty degrees to each side
            camera.aspect = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.targetTexture = target;
            return camera;
        }

        private static void DrawOnce(Camera camera)
        {
            var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = camera.targetTexture };
            Assert.That(UnityEngine.Rendering.RenderPipeline.SupportsRenderRequest(camera, request), Is.True, "render request");
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(camera, request);
        }

        // The eye put so that the left edge of its view stands <paramref name="along"/> metres from the centre in the
        // level direction u, the view beyond it: at the centre's height, what reaches farther than that along u is in
        // view, what does not is left of the view.
        private static void PutLeftEdge(Transform eye, Vector3 centre, Vector3 u, float along, float back)
        {
            Vector3 v = Quaternion.AngleAxis(-90f, Vector3.up) * u;
            eye.SetPositionAndRotation(centre + u * along - v * back, Quaternion.LookRotation(Quaternion.AngleAxis(30f, Vector3.up) * v, Vector3.up));
        }

        private static Matrix4x4 WorldToClip(Camera eye) => eye.projectionMatrix * eye.worldToCameraMatrix;

        /// <summary>
        /// **The return of an unseen character: a corner only, and one eye only** (TL, 2026-10-05). Unity culls the
        /// renderer by the world-axis box about its fixed bounds; a corner of that box reaches farther than the sphere
        /// about the level of detail's range. With only that corner in the view -- for the one camera, then for the
        /// left eye alone of two -- the character counts as in view, its whole current pose is put on in that frame,
        /// before the drawing, and Unity draws it. Judged by the range's sphere alone (as it was) it counted as out of
        /// view while Unity drew it.
        /// </summary>
        [UnityTest]
        public IEnumerator ACharacterWhoseBoundsReachTheViewByACornerOnly_OrForOneEyeOnly_IsGivenItsPoseBeforeItIsDrawn()
        {
            ColdWorld();
            Camera camera = ReturnCamera("return camera");
            PoseLodDirector director = ColdTrack(new GameObject("return lod")).AddComponent<PoseLodDirector>();
            director.DisplayRateHz = 72f;
            director.ViewCamera = camera;
            Shader shader = Shader.Find("Zantetsu/VP Mesh Surface");
            Assert.That(shader, Is.Not.Null);
            Material surface = ColdTrack(new Material(shader));
            surface.SetFloat("_VpUsePaletteAtlas", 0f);
            try
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
                SandboxNpcCharacter slot = BonesSlot(true, 1, new Vector3(10f, 0f, 10f), director, out GameObject setup, out Transform _);
                slot.CharacterRoot.transform.rotation = Quaternion.Euler(0f, 45f, 0f);   // turned: the world-axis box is wider than the box
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                slot.Renderer.sharedMaterial = surface;

                // Behind the camera first.
                camera.transform.SetPositionAndRotation(new Vector3(10f, 1f, -40f), Quaternion.LookRotation(Vector3.back));
                Assert.That(slot.Activate(), Is.True);
                SkinnedMeshRenderer drawn = slot.Renderer;
                PoseLodCharacter lod = slot.Lod;
                Assert.That(lod, Is.Not.Null);
                Assert.That(slot.SkinsOnlyWhenSeen, Is.True);
                for (int f = 0; f < 6; f++)
                {
                    yield return null;
                    DrawOnce(camera);
                }

                Assert.That(lod.ViewStrength, Is.GreaterThan(0), "behind the camera: out of view");
                Assert.That(lod.Level, Is.GreaterThan(0));
                Assert.That(drawn.isVisible, Is.False, "and Unity does not draw it");

                // The box Unity culls, and how far its level corner reaches beyond the range's sphere.
                Bounds box = drawn.bounds;
                Vector3 centre = box.center, w = box.extents;
                float rangeRadius = lod.RangeBounds.extents.magnitude;
                Vector3 u = new Vector3(w.x, 0f, w.z).normalized;
                float corner = Mathf.Sqrt(w.x * w.x + w.z * w.z);
                TestContext.Out.WriteLine("the box Unity culls: centre " + centre.ToString("F2") + ", half size " + w.ToString("F2") + "; its level corner reaches " + corner.ToString("F3")
                    + " m from the centre, the range's sphere " + rangeRadius.ToString("F3") + " m");
                Assert.That(corner, Is.GreaterThan(rangeRadius + 0.2f), "a corner of the box stands outside the range's sphere");

                // 1. One camera, the left edge of its view between the sphere and the corner: only the corner is in view.
                float along = (rangeRadius + corner) * 0.5f;
                PutLeftEdge(camera.transform, centre, u, along, 6f);
                Plane[] planes = GeometryUtility.CalculateFrustumPlanes(camera);
                Assert.That(planes[0].GetDistanceToPoint(centre), Is.EqualTo(-along).Within(1e-3f), "the centre stands that far left of the view");
                Assert.That(along, Is.GreaterThan(rangeRadius), "the range's sphere does not reach the view");
                Assert.That(GeometryUtility.TestPlanesAABB(planes, box), Is.True, "the box does, by its corner");
                yield return null;   // the director has decided and applied for this frame; nothing is drawn yet
                Assert.That(lod.ViewStrength, Is.EqualTo(0), "the character counts as in view");
                Assert.That(lod.Level, Is.EqualTo(0));
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount), "its whole current pose was put on in this frame, before the drawing");
                DrawOnce(camera);
                Assert.That(drawn.isVisible, Is.True, "and Unity draws it");

                // As it was judged before -- by the range's sphere alone: out of view, though Unity draws it.
                lod.SetDrawBounds(null, default);
                yield return null;
                int byTheRangeAlone = lod.ViewStrength;
                DrawOnce(camera);
                TestContext.Out.WriteLine("only a corner in view: judged by the range's sphere alone, view strength " + byTheRangeAlone + " (level " + lod.Level + "), Unity draws it: " + drawn.isVisible
                    + "; judged by the bounds Unity culls, view strength 0");
                Assert.That(byTheRangeAlone, Is.GreaterThan(0), "by the range's sphere alone the character counted as out of view");
                Assert.That(drawn.isVisible, Is.True, "while Unity drew it");
                lod.SetDrawBounds(drawn.transform, drawn.localBounds);
                yield return null;
                Assert.That(lod.ViewStrength, Is.EqualTo(0));
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount), "come into view: the pose of this frame, at once");
                DrawOnce(camera);

                // 2. Two eyes, 64 mm apart. Both away first; then the left eye's view takes 3 cm of the corner and the
                //    right eye's, shifted to the right, takes nothing of the box.
                Camera left = ReturnCamera("left eye"), right = ReturnCamera("right eye");
                left.transform.SetPositionAndRotation(new Vector3(10f, 1f, -40f), Quaternion.LookRotation(Vector3.back));
                right.transform.SetPositionAndRotation(new Vector3(10.064f, 1f, -40f), Quaternion.LookRotation(Vector3.back));
                director.OverrideEyes(new[] { WorldToClip(left), WorldToClip(right) }, new[] { left.transform.position, right.transform.position });
                for (int f = 0; f < 4; f++) yield return null;
                Assert.That(lod.ViewStrength, Is.GreaterThan(0), "neither eye sees it");
                PutLeftEdge(left.transform, centre, u, corner - 0.03f, 6f);
                right.transform.SetPositionAndRotation(left.transform.position + left.transform.right * 0.064f, left.transform.rotation);
                Assert.That(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(left), box), Is.True, "the left eye's view holds a corner of the box");
                Assert.That(GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(right), box), Is.False, "the right eye's holds nothing of it");
                Assert.That(corner - 0.03f, Is.GreaterThan(rangeRadius), "and the range's sphere reaches neither");
                director.OverrideEyes(new[] { WorldToClip(left), WorldToClip(right) }, new[] { left.transform.position, right.transform.position });
                yield return null;
                Assert.That(lod.ViewStrength, Is.EqualTo(0), "seen by one eye is seen");
                Assert.That(lod.Level, Is.EqualTo(0));
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount), "its whole current pose in the frame the one eye first holds it");

                // The judgement itself, eye by eye: its sphere (about the box) taken 3 cm into the left eye's view and
                // left 2.5 cm short of the right eye's. Two such right eyes: out of view. The left with the right, in
                // either order: in view, with the pose of that frame.
                float sphere = w.magnitude;
                PutLeftEdge(left.transform, centre, u, sphere - 0.03f, 6f);
                right.transform.SetPositionAndRotation(left.transform.position + left.transform.right * 0.064f, left.transform.rotation);
                director.OverrideEyes(new[] { WorldToClip(right), WorldToClip(right) }, new[] { right.transform.position, right.transform.position });
                for (int f = 0; f < 3; f++) yield return null;
                Assert.That(lod.ViewStrength, Is.GreaterThan(0), "the right eye's view alone does not reach the sphere");
                director.OverrideEyes(new[] { WorldToClip(left), WorldToClip(right) }, new[] { left.transform.position, right.transform.position });
                yield return null;
                Assert.That(lod.ViewStrength, Is.EqualTo(0), "the left eye's does: in view");
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount), "with the pose of that frame");
                director.OverrideEyes(new[] { WorldToClip(right), WorldToClip(right) }, new[] { right.transform.position, right.transform.position });
                for (int f = 0; f < 3; f++) yield return null;
                Assert.That(lod.ViewStrength, Is.GreaterThan(0));
                director.OverrideEyes(new[] { WorldToClip(right), WorldToClip(left) }, new[] { right.transform.position, left.transform.position });
                yield return null;
                Assert.That(lod.ViewStrength, Is.EqualTo(0), "whichever of the two eyes it is");
                Assert.That(lod.FullPoseFrame, Is.EqualTo(Time.frameCount));
                director.OverrideEyes(null, null);
                yield return Destroy(setup);
            }
            finally
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
            }
        }

        /// <summary>
        /// **A far character coming into view is given its pose in that frame.** Sixty metres away its level is the
        /// distance's (3) in view and out; coming into view does not lower it. Its pose is put on all the same in the
        /// frame it comes into view -- the frame after a scheduled update, where the schedule alone gives none.
        /// </summary>
        [UnityTest]
        public IEnumerator AFarCharacterComingIntoView_IsGivenItsPoseInThatFrame_ThoughItsLevelStays()
        {
            ColdWorld();
            Camera camera = ReturnCamera("far camera");
            PoseLodDirector director = ColdTrack(new GameObject("far lod")).AddComponent<PoseLodDirector>();
            director.DisplayRateHz = 72f;
            director.ViewCamera = camera;
            try
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
                SandboxNpcCharacter slot = BonesSlot(true, 1, new Vector3(0f, 0f, 60f), director, out GameObject setup, out Transform _);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                camera.transform.SetPositionAndRotation(new Vector3(0f, 1f, 0f), Quaternion.LookRotation(Vector3.back));
                Assert.That(slot.Activate(), Is.True);
                PoseLodCharacter lod = slot.Lod;
                for (int f = 0; f < 4; f++) yield return null;
                Assert.That(lod.Level, Is.EqualTo(3));
                Assert.That(lod.ViewStrength, Is.GreaterThan(0));

                // Wait for a scheduled update: the next one by the schedule is several frames away.
                for (int f = 0; f < 40 && lod.LastUpdateFrame != Time.frameCount; f++) yield return null;
                Assert.That(lod.LastUpdateFrame, Is.EqualTo(Time.frameCount), "a scheduled update, this frame");
                int scheduled = Time.frameCount;
                camera.transform.rotation = Quaternion.LookRotation(Vector3.forward);
                yield return null;
                Assert.That(Time.frameCount, Is.EqualTo(scheduled + 1));
                Assert.That(lod.ViewStrength, Is.EqualTo(0), "in view now");
                Assert.That(lod.Level, Is.EqualTo(3), "its level is still the distance's");
                Assert.That(lod.LastUpdateFrame, Is.EqualTo(Time.frameCount), "its pose was put on in the frame it came into view, the frame after a scheduled one");
                yield return null;
                Assert.That(lod.LastUpdateFrame, Is.EqualTo(scheduled + 1), "and then by the schedule again");
                yield return Destroy(setup);
            }
            finally
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
            }
        }

        /// <summary>
        /// **Under the level of detail, the drawn renderer is skinned only when seen, under bounds of its own.** At its
        /// activation the slot's renderer is no longer updated when offscreen, has no root bone, and has the level of
        /// detail's range -- in the character root's frame -- as its bounds in its own frame; the bounds go with the
        /// root wherever it is put, and a second activation leaves them so. A run that keeps the earlier behaviour
        /// leaves the renderer as it was built, and then says how Unity's own bounds lie against the draw bounds.
        /// </summary>
        [UnityTest]
        public IEnumerator UnderTheLevelOfDetail_TheRendererIsSkinnedOnlyWhenSeen_UnderBoundsThatGoWithTheRoot()
        {
            ColdWorld();
            PoseLodDirector director = ColdTrack(new GameObject("bones lod")).AddComponent<PoseLodDirector>();
            director.DisplayRateHz = 72f;
            try
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
                SandboxNpcCharacter slot = BonesSlot(true, 1, new Vector3(3f, 0f, -2f), director, out GameObject setup, out Transform _);
                yield return UntilPrepared(slot);
                Assert.That(slot.IsPrepared, Is.True, slot.Failure);
                Assert.That(slot.SkinsOnlyWhenSeen, Is.False, "a dormant slot has no level of detail yet");
                Assert.That(slot.Activate(), Is.True);
                Assert.That(slot.Lod, Is.Not.Null, "the level of detail took the slot");
                SkinnedMeshRenderer drawn = slot.Renderer;
                Assert.That(slot.SkinsOnlyWhenSeen, Is.True);
                Assert.That(drawn.updateWhenOffscreen, Is.False, "Unity skins it only while a camera sees it");
                Assert.That(drawn.rootBone, Is.Null, "its bounds are not carried by a bone");
                Assert.That(slot.TryGetDrawBounds(out Bounds range), Is.True);
                Assert.That(range.min, Is.EqualTo(slot.Lod.RangeBounds.min), "this skin lies inside its hulls at the bind pose: the draw bounds are the range");
                Assert.That(range.max, Is.EqualTo(slot.Lod.RangeBounds.max));
                // The renderer stands at the root, unturned and unscaled: its own frame is the root's.
                Assert.That(Vector3.Distance(drawn.localBounds.min, range.min), Is.LessThan(1e-4f));
                Assert.That(Vector3.Distance(drawn.localBounds.max, range.max), Is.LessThan(1e-4f));
                Vector3 rootAt = slot.CharacterRoot.transform.position;
                Assert.That(Vector3.Distance(drawn.bounds.center, rootAt + range.center), Is.LessThan(1e-3f), "in the world the bounds stand at the root");
                // The hulls at the table's poses -- the weighted bone goes from x = 0 to x = 2 -- are inside them.
                Assert.That(range.min.x, Is.LessThan(-1f));
                Assert.That(range.max.x, Is.GreaterThan(3f));

                // The root is put elsewhere while nothing draws it: the bounds are there too.
                slot.CharacterRoot.transform.position = new Vector3(-40f, 2f, 15f);
                yield return null;
                Assert.That(Vector3.Distance(drawn.bounds.center, new Vector3(-40f, 2f, 15f) + range.center), Is.LessThan(1e-3f), "the bounds went with the root");
                Assert.That(slot.TryDrawBoundsExcess(out _), Is.False, "skinned only when seen: Unity's bounds are the draw bounds themselves");
                yield return Destroy(setup);

                // A run that keeps the earlier behaviour: the renderer as it was built.
                SandboxNpcCharacter.SkinWhenUnseen = true;
                SandboxNpcCharacter kept = BonesSlot(true, 1, new Vector3(0f, 0f, 30f), director, out GameObject keptSetup, out Transform _);
                yield return UntilPrepared(kept);
                Assert.That(kept.Activate(), Is.True);
                Assert.That(kept.Lod, Is.Not.Null);
                Assert.That(kept.SkinsOnlyWhenSeen, Is.False);
                Assert.That(kept.Renderer.updateWhenOffscreen, Is.True, "updated when offscreen, as built");
                yield return null;
                yield return null;
                Assert.That(kept.TryDrawBoundsExcess(out float excess), Is.True);
                TestContext.Out.WriteLine("kept as before: Unity's bounds reach " + excess.ToString("F4") + " m outside the draw bounds (below zero: inside)");
                Assert.That(excess, Is.LessThanOrEqualTo(1e-3f), "the bounds Unity makes from the bones lie inside the draw bounds");
                yield return Destroy(keptSetup);
            }
            finally
            {
                SandboxNpcCharacter.SkinWhenUnseen = false;
            }
        }
    }
}
