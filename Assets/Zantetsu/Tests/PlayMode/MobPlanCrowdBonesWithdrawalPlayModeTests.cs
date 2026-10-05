#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The walk city's own characters, each with its model's complete bone correspondence (<see cref="VpSkinBones"/>,
    /// DESIGN 9, D-195), in the Editor, in the licensed scene with its crowd running: every one of them is prepared
    /// with its parts -- not its whole root -- as what its cut withdraws; a walking one is cut, published and leaves by
    /// its parts with its correspondence as it was; the crowd retires it and prepares its slot again; and the slot's
    /// next individual, activated as the crowd activates one, is cut in its turn. Ignored, saying so, where the scene
    /// is not present.
    /// </summary>
    public class MobPlanCrowdBonesWithdrawalPlayModeTests
    {
        // The licensed walk city scene; ZANTETSU_WALK_SCENE names another scene with a crowd.
        private static string ScenePath => Environment.GetEnvironmentVariable("ZANTETSU_WALK_SCENE") ?? "Assets/Licensed/WalkCity/WalkCity.unity";

        // A level cut through the middle of the character as it is posed now, through the character's own entry.
        private static VpCharacterCutResult CutNow(SandboxNpcCharacter character)
        {
            character.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyNow(null, 0);
            var mesh = new Mesh();
            character.Renderer.BakeMesh(mesh);
            float y = mesh.bounds.center.y;
            UnityEngine.Object.DestroyImmediate(mesh);
            return character.Handle.TryCut(new float4(0, 1, 0, -y), (float3)character.Renderer.transform.position);
        }

        [UnityTest, Timeout(600000)]
        public IEnumerator TheWalkCitysCharacters_LeaveByTheirParts_KeepTheirBoneCorrespondence_AndASlotCarriesItsNextIndividual()
        {
            if (!File.Exists(ScenePath)) Assert.Ignore("the licensed scene is not present: " + ScenePath);
            LogAssert.ignoreFailingMessages = true;   // the scene's own start-up messages (no headset here) are not this test's subject
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            MobPlanCrowd crowd = UnityEngine.Object.FindFirstObjectByType<MobPlanCrowd>();
            Assert.That(crowd, Is.Not.Null, "the scene has a crowd");
            var input = UnityEngine.Object.FindFirstObjectByType<MobPlanPlayerInput>();
            if (input != null) input.liveInput = false;
            var retired = new List<SandboxNpcCharacter>();
            crowd.ActorRetired += (id, character) => retired.Add(character);
            double until = Time.realtimeSinceStartupAsDouble + 180;
            while (!crowd.IsReady && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(crowd.IsReady, "the crowd is ready within 180 s");

            // 1. Every prepared character: its correspondence beside its model's renderer, and its parts what its cut withdraws.
            SandboxNpcCharacter[] prepared = UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None).Where(c => c.Handle != null).ToArray();
            Assert.That(prepared.Length, Is.GreaterThan(0), "the crowd prepared its characters");
            int withCorrespondence = 0, byParts = 0;
            var reasons = new Dictionary<string, int>();
            foreach (SandboxNpcCharacter c in prepared)
            {
                Assert.That(c.Failure, Is.Null, c.name);
                VpSkinBones held = c.CharacterRoot.GetComponentInChildren<VpSkinBones>(true);
                if (held != null)
                {
                    withCorrespondence++;
                    Assert.That(held.enabled, Is.True, c.name + ": the correspondence is an enabled component, as the scene has it");
                    Assert.That(held.IsConsistentWith(held.GetComponent<SkinnedMeshRenderer>(), out string broken), Is.True, c.name + ": " + broken);
                }

                if (c.WithdrawsParts)
                {
                    byParts++;
                }
                else
                {
                    string why = c.WholeRootReason ?? "(none given)";
                    int on = why.IndexOf(" on ", StringComparison.Ordinal);
                    string kind = on >= 0 ? why.Substring(0, on) : why;
                    reasons[kind] = reasons.TryGetValue(kind, out int n) ? n + 1 : 1;
                }
            }

            TestContext.Out.WriteLine("prepared characters " + prepared.Length + ", with a bone correspondence " + withCorrespondence + ", withdrawn by parts " + byParts
                + "; keeping their whole root, by reason: " + (reasons.Count == 0 ? "none" : string.Join("; ", reasons.Select(r => r.Value + " x " + r.Key))));
            Assert.That(withCorrespondence, Is.EqualTo(prepared.Length), "each of the scene's characters carries its model's correspondence");
            Assert.That(byParts, Is.EqualTo(prepared.Length), "and each is withdrawn by its parts");

            // 2. A walking character, once the crowd has its characters walking.
            SandboxNpcCharacter[] live = prepared.Where(c => c.IsTarget && c.Handle.IsReady).ToArray();
            Assert.That(live.Length, Is.GreaterThan(0), "characters are walking");
            var rootWas = live.ToDictionary(c => c, c => c.CharacterRoot.transform.position);
            SandboxNpcCharacter walker = null;
            double began = Time.timeAsDouble;
            until = Time.realtimeSinceStartupAsDouble + 120;
            while (walker == null && Time.timeAsDouble - began < 40.0 && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
                if (Time.frameCount % 30 != 0) continue;
                walker = live.FirstOrDefault(c => c.IsTarget && c.Handle != null && c.Handle.IsReady && Vector3.Distance(c.CharacterRoot.transform.position, rootWas[c]) > 0.5f);
            }

            Assert.That(walker, Is.Not.Null, "a character walked half a metre");
            VpSkinBones bones = walker.CharacterRoot.GetComponentInChildren<VpSkinBones>(true);
            SkinnedMeshRenderer model = bones.GetComponent<SkinnedMeshRenderer>();
            VpSkinBoneMap mapWas = bones.Map;
            Transform[] bonesWas = Enumerable.Range(0, bones.BoneCount).Select(bones.BoneAt).ToArray();
            void SameCorrespondence(string when)
            {
                Assert.That(bones != null && bones.enabled && bones.gameObject.activeInHierarchy, Is.True, when + ": the correspondence is there, as it was");
                Assert.That(bones.Map, Is.SameAs(mapWas), when + ": its map");
                Assert.That(Enumerable.Range(0, bones.BoneCount).Select(bones.BoneAt).ToArray(), Is.EqualTo(bonesWas), when + ": every Transform, in order");
                Assert.That(bones.IsConsistentWith(model, out string broken), Is.True, when + ": " + broken);
            }

            TestContext.Out.WriteLine("the walking character " + walker.name + ": correspondence of " + bones.BoneCount + " bones, the model's renderer lists " + model.bones.Length
                + ", activations " + walker.Activations + ", prepared again " + walker.Reprepared);

            // 3. Cut, published, withdrawn by its parts, its correspondence as it was.
            VpPreparedCharacterCut first = walker.Handle;
            PoseTablePlayer pose = walker.CharacterRoot.GetComponent<PoseTablePlayer>();
            VpCharacterCutResult result = CutNow(walker);
            Assert.That(result.Outcome, Is.EqualTo(VpCharacterCutOutcome.Requested), "the cut was taken: " + first.LastFailure);
            until = Time.realtimeSinceStartupAsDouble + 30;
            while (!first.IsWithdrawn && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(first.IsWithdrawn, Is.True, "the cut (" + result.Acceptance + ") was published and the character withdrawn within 30 s");
            Assert.That(first.WithdrawsParts, Is.True, "by its parts");
            Assert.That(walker.CharacterRoot.activeInHierarchy, Is.True, "the hierarchy stays");
            Assert.That(walker.Renderer.enabled, Is.False, "not drawn");
            Assert.That(pose.enabled, Is.False, "not posed");
            SameCorrespondence("after the withdrawal by parts");

            // 4. The crowd retires it and prepares its slot again, by its parts again.
            until = Time.realtimeSinceStartupAsDouble + 30;
            while (!retired.Contains(walker) && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(retired.Contains(walker), Is.True, "the crowd retired the cut character within 30 s");
            until = Time.realtimeSinceStartupAsDouble + 90;
            while (!(walker.Reprepared >= 1 && walker.IsPrepared) && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(walker.Reprepared >= 1 && walker.IsPrepared, Is.True, "the crowd prepared the slot again within 90 s: " + walker.Failure);
            Assert.That(walker.Handle, Is.Not.SameAs(first));
            Assert.That(walker.WithdrawsParts, Is.True, "prepared again, by its parts again: " + walker.WholeRootReason);
            Assert.That(walker.CharacterRoot.activeInHierarchy, Is.True);
            SameCorrespondence("after the crowd prepared the slot again");

            // 5. The slot's next individual. The crowd takes its free slots in their order and has many before this one,
            //    so the slot is activated here as the crowd activates one (placed, then SandboxNpcCharacter.Activate),
            //    with the crowd stopped so that it does not take the slot too.
            crowd.enabled = false;
            yield return null;
            pose.PlanSource = null;
            Assert.That(walker.Activate(), Is.True, "the slot carries a new individual: " + walker.Failure);
            Assert.That(walker.Activations, Is.EqualTo(2));
            Assert.That(walker.Renderer.enabled && pose.enabled, Is.True, "drawn and posed again");
            SameCorrespondence("after the slot's next activation");
            yield return null;
            VpPreparedCharacterCut second = walker.Handle;
            VpCharacterCutResult again = CutNow(walker);
            Assert.That(again.Outcome, Is.EqualTo(VpCharacterCutOutcome.Requested), "the next individual's cut was taken: " + second.LastFailure);
            until = Time.realtimeSinceStartupAsDouble + 30;
            while (!second.IsWithdrawn && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(second.IsWithdrawn && second.WithdrawsParts, Is.True, "published (" + again.Acceptance + ") and withdrawn by its parts");
            Assert.That(walker.CharacterRoot.activeInHierarchy, Is.True);
            SameCorrespondence("after the next individual's withdrawal");
            TestContext.Out.WriteLine("first cut " + result.Acceptance + " (operation " + first.Operation.value + "), the slot prepared again " + walker.Reprepared + " time(s), next individual's cut "
                + again.Acceptance + " (operation " + second.Operation.value + "); the correspondence unchanged throughout");
        }
    }
}
#endif
