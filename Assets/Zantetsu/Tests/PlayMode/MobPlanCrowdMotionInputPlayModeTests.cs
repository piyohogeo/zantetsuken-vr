#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.Core.Animation;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The walking crowd itself (<see cref="MobPlanCrowd"/>) in the Editor, in the licensed walk city scene, for what its
    /// characters give their cuts (DESIGN 9, D-197): no body follows a character -- the body its mass settings were
    /// authored on is set aside, the character's hierarchy holds none, and the crowd's own updates move none -- the
    /// crowd gives each character its motion as numbers, and a real cut of a walking character gives its source body
    /// the character's numbers for where it then stands. Ignored, saying so, where the scene is not present.
    /// </summary>
    public class MobPlanCrowdMotionInputPlayModeTests
    {
        // The licensed walk city scene; ZANTETSU_WALK_SCENE names another scene with a crowd.
        private static string ScenePath => Environment.GetEnvironmentVariable("ZANTETSU_WALK_SCENE") ?? "Assets/Licensed/WalkCity/WalkCity.unity";

        private static Vector3 RootVelocityOf(VpPreparedCharacterCut handle, out Vector3 angular)
        {
            const BindingFlags f = BindingFlags.Instance | BindingFlags.NonPublic;
            angular = (Vector3)typeof(VpPreparedCharacterCut).GetField("rootAngularVelocity", f).GetValue(handle);
            return (Vector3)typeof(VpPreparedCharacterCut).GetField("rootVelocity", f).GetValue(handle);
        }

        private static GameObject SourceObjectOf(VpPreparedCharacterCut handle) =>
            (GameObject)typeof(VpPreparedCharacterCut).GetField("actor", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(handle);

        [UnityTest, Timeout(600000)]
        public IEnumerator TheWalkingCrowdsCharacters_AreFollowedByNoBody_AndACutOfOneTakesItsPlacementAndMotionAsNumbers()
        {
            if (!File.Exists(ScenePath)) Assert.Ignore("the licensed scene is not present: " + ScenePath);
            LogAssert.ignoreFailingMessages = true;   // the scene's own start-up messages (no headset here) are not this test's subject
            yield return EditorSceneManager.LoadSceneAsyncInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Single));
            yield return null;
            MobPlanCrowd crowd = UnityEngine.Object.FindFirstObjectByType<MobPlanCrowd>();
            Assert.That(crowd, Is.Not.Null, "the scene has a crowd");
            CutWorldRoot world = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
            Assert.That(world, Is.Not.Null, "and a world");
            var input = UnityEngine.Object.FindFirstObjectByType<MobPlanPlayerInput>();
            if (input != null) input.liveInput = false;
            var retired = new List<SandboxNpcCharacter>();
            crowd.ActorRetired += (id, character) => retired.Add(character);
            double until = Time.realtimeSinceStartupAsDouble + 180;
            while (!crowd.IsReady && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(crowd.IsReady, "the crowd is ready within 180 s");
            for (int f = 0; f < 30; f++) yield return null;

            // 1. Every prepared character of the scene: its numbers read, its authored body aside, no body in its hierarchy.
            SandboxNpcCharacter[] every = UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            SandboxNpcCharacter[] prepared = every.Where(c => c.Handle != null).ToArray();
            Assert.That(prepared.Length, Is.GreaterThan(0), "the crowd prepared its characters");
            int byParts = 0;
            var reasons = new Dictionary<string, int>();
            foreach (SandboxNpcCharacter c in prepared)
            {
                Assert.That(c.Failure, Is.Null, c.name);
                Assert.That(c.MassProperties.IsUsable, Is.True, c.name + ": its mass properties were read");
                Assert.That(c.MotionBodyAside, Is.True, c.name + ": its authored body was set aside");
                Assert.That(c.MotionBody != null && !c.MotionBody.gameObject.activeInHierarchy, Is.True, c.name + ": which is out of the physics");
                Assert.That(c.MotionBody.transform.IsChildOf(c.CharacterRoot.transform), Is.False, c.name + ": and not under the character");
                Assert.That(c.MotionBody.transform.parent, Is.SameAs(c.transform), c.name + ": but under its preparation's own object");
                Assert.That(c.CharacterRoot.GetComponentsInChildren<Rigidbody>(true), Is.Empty, c.name + ": the character holds no body");
                if (c.WithdrawsParts)
                {
                    byParts++;
                }
                else
                {
                    // The reason with the object's own name taken off, so that the characters' reasons can be counted.
                    string why = c.WholeRootReason ?? "(none given)";
                    int on = why.IndexOf(" on ", StringComparison.Ordinal);
                    string kind = on >= 0 ? why.Substring(0, on) : why;
                    reasons[kind] = reasons.TryGetValue(kind, out int n) ? n + 1 : 1;
                    Assert.That(why, Does.Not.Contain("the character holds"), c.name + ": no body, collider or joint in the character asks for its whole root");
                }
            }

            VpCharacterMassProperties first = prepared[0].MassProperties;
            TestContext.Out.WriteLine("characters in the scene " + every.Length + ", prepared " + prepared.Length + " (not prepared: " + string.Join(", ", every.Where(c => c.Handle == null).Select(c => c.name))
                + "); withdrawn by parts " + byParts + " of " + prepared.Length + "; mass properties of " + prepared[0].name + ": mass " + first.Mass + " kg, centre of mass "
                + first.CentreOfMass.ToString("F4") + " in its root's frame, inertia " + first.InertiaTensor.ToString("F4") + ", principal axes " + first.InertiaTensorRotation.eulerAngles.ToString("F2"));
            TestContext.Out.WriteLine("characters keeping their whole root at withdrawal, by reason: " + (reasons.Count == 0 ? "none" : string.Join("; ", reasons.Select(r => r.Value + " x " + r.Key))));

            // No body in the scene's physics stands under any character.
            Rigidbody[] inScene = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (Rigidbody body in inScene)
                foreach (SandboxNpcCharacter c in prepared)
                    Assert.That(body.transform.IsChildOf(c.CharacterRoot.transform), Is.False, "a body in the physics under " + c.CharacterRoot.name + ": " + body.name);
            TestContext.Out.WriteLine("bodies in the scene's physics before any cut: " + inScene.Length + " (kinematic " + inScene.Count(b => b.isKinematic) + "), none under a character");

            // 2. The crowd walks its characters: the bodies set aside and the cuts' source bodies do not move, and the
            //    crowd gives the characters their motion as numbers.
            SandboxNpcCharacter[] live = prepared.Where(c => c.IsTarget && c.Handle != null && c.Handle.IsReady).ToArray();
            Assert.That(live.Length, Is.GreaterThan(0), "characters are walking");
            var rootWas = live.ToDictionary(c => c, c => c.CharacterRoot.transform.position);
            var asideWas = live.ToDictionary(c => c, c => (c.MotionBody.transform.position, c.MotionBody.transform.rotation));
            var sourceWas = live.ToDictionary(c => c, c => (SourceObjectOf(c.Handle).transform.position, SourceObjectOf(c.Handle).transform.rotation));
            double watched = Time.timeAsDouble;
            const double Seconds = 3.0, AtMost = 40.0;
            int frames = 0;
            bool walked = false;
            until = Time.realtimeSinceStartupAsDouble + 120;
            while ((Time.timeAsDouble - watched < Seconds || !walked) && Time.timeAsDouble - watched < AtMost && Time.realtimeSinceStartupAsDouble < until)
            {
                yield return null;
                frames++;
                if (frames % 30 == 0)
                    foreach (SandboxNpcCharacter c in live)
                        if (c.IsTarget && c.Handle != null && c.Handle.IsReady && Vector3.Distance(c.CharacterRoot.transform.position, rootWas[c]) > 0.5f) walked = true;
                foreach (SandboxNpcCharacter c in live)
                {
                    if (!c.IsTarget || c.Handle == null || !c.Handle.IsReady) continue;
                    if (frames % 30 == 0) Assert.That(c.CharacterRoot.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
                    Assert.That(c.MotionBody.gameObject.activeInHierarchy, Is.False);
                    Assert.That(c.MotionBody.transform.position == asideWas[c].position && c.MotionBody.transform.rotation == asideWas[c].rotation, Is.True, c.name + ": the body set aside does not follow");
                    GameObject source = SourceObjectOf(c.Handle);
                    Assert.That(!source.activeSelf && source.transform.parent == null && source.transform.position == sourceWas[c].position && source.transform.rotation == sourceWas[c].rotation,
                        Is.True, c.name + ": the cut's source body is out of the scene, under no character, and does not follow");
                }
            }

            watched = Time.timeAsDouble - watched;
            int moved = 0, told = 0;
            SandboxNpcCharacter walker = null;
            float fastest = 0f;
            foreach (SandboxNpcCharacter c in live)
            {
                if (!c.IsTarget || c.Handle == null || !c.Handle.IsReady) continue;
                float went = Vector3.Distance(c.CharacterRoot.transform.position, rootWas[c]);
                Vector3 velocity = RootVelocityOf(c.Handle, out Vector3 turning);
                TestContext.Out.WriteLine("  " + c.name + ": went " + went.ToString("F3") + " m, told " + velocity.magnitude.ToString("F3") + " m/s and " + turning.magnitude.ToString("F3") + " rad/s");
                if (went > 0.05f) moved++;
                if (velocity.sqrMagnitude > 1e-6f) told++;
                if (went > 0.05f && velocity.magnitude > fastest)
                {
                    fastest = velocity.magnitude;
                    walker = c;
                }
            }

            TestContext.Out.WriteLine("over " + frames + " frames (" + watched.ToString("F2") + " s of game time): live characters " + live.Length + ", moved more than 5 cm " + moved
                + ", given a motion other than rest " + told);
            Assert.That(moved, Is.GreaterThan(0), "the crowd moved its characters");
            Assert.That(walker, Is.Not.Null, "a character that moved was given its motion as numbers");

            // 3. A real cut of a walking character: its source body is given its numbers, for where it then stands.
            GameObject cutSource = SourceObjectOf(walker.Handle);
            PhysicsFragmentOwner taken = null;
            Vector3 givenCentre = default, givenVelocity = default, givenAngular = default, givenInertia = default;
            float givenMass = 0f;
            void Added(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
            {
                if (owner.Root != cutSource) return;
                taken = owner;
                givenCentre = owner.Body.worldCenterOfMass;
                givenVelocity = owner.Body.linearVelocity;
                givenAngular = owner.Body.angularVelocity;
                givenInertia = owner.Body.inertiaTensor;
                givenMass = owner.Body.mass;
            }

            walker.CharacterRoot.GetComponent<PoseTablePlayer>().ApplyNow(null, 0);
            var mesh = new Mesh();
            walker.Renderer.BakeMesh(mesh);
            float y = mesh.bounds.center.y;
            UnityEngine.Object.DestroyImmediate(mesh);
            walker.CharacterRoot.transform.GetPositionAndRotation(out Vector3 rootPosition, out Quaternion rootRotation);
            Vector3 rootVelocity = RootVelocityOf(walker.Handle, out Vector3 rootAngular);
            VpCharacterMassProperties numbers = walker.MassProperties;
            Vector3 centre = rootPosition + rootRotation * numbers.CentreOfMass;
            world.Owners.OwnerAdded += Added;
            VpCharacterCutResult result;
            try { result = walker.Handle.TryCut(new float4(0, 1, 0, -y), (float3)walker.Renderer.transform.position); }
            finally { world.Owners.OwnerAdded -= Added; }
            Assert.That(result.Outcome, Is.EqualTo(VpCharacterCutOutcome.Requested), "the cut was taken: " + walker.Handle.LastFailure);
            Assert.That(taken, Is.Not.Null, "its source body was registered at the acceptance");
            TestContext.Out.WriteLine("cut of " + walker.name + " (" + result.Acceptance + "): root at " + rootPosition.ToString("F3") + ", told velocity " + rootVelocity.ToString("F3") + " m/s and angular velocity "
                + rootAngular.ToString("F3") + " rad/s; source body given mass " + givenMass + " kg, centre of mass " + givenCentre.ToString("F3") + " (expected " + centre.ToString("F3") + "), velocity "
                + givenVelocity.ToString("F3") + ", angular velocity " + givenAngular.ToString("F3"));
            Assert.That(givenMass, Is.EqualTo(numbers.Mass));
            Assert.That(Vector3.Distance(givenCentre, centre), Is.LessThan(1e-3f), "the centre of mass: the kept one, placed by the root as it stood");
            Assert.That(Vector3.Distance(givenInertia, numbers.InertiaTensor), Is.LessThan(1e-4f));
            Assert.That(Vector3.Distance(givenVelocity, rootVelocity + Vector3.Cross(rootAngular, centre - rootPosition)), Is.LessThan(1e-3f), "the velocity: the root's, carried to the centre of mass");
            Assert.That(Vector3.Distance(givenAngular, rootAngular), Is.LessThan(1e-4f));
            Assert.That(rootVelocity.magnitude, Is.GreaterThan(0f), "a walking character's cut inherits a motion");

            // The crowd retires it; its hierarchy still holds no body and its authored body stays aside.
            until = Time.realtimeSinceStartupAsDouble + 30;
            while (!retired.Contains(walker) && Time.realtimeSinceStartupAsDouble < until) yield return null;
            Assert.That(retired.Contains(walker), Is.True, "the crowd retired the cut character within 30 s");
            Assert.That(walker.CharacterRoot.GetComponentsInChildren<Rigidbody>(true), Is.Empty);
            Assert.That(walker.MotionBody != null && !walker.MotionBody.gameObject.activeInHierarchy && walker.MotionBody.transform.parent == walker.transform, Is.True);
        }
    }
}
#endif
