using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Zantetsu.Core.Tests
{
    public class WorldPhysicsBootstrapTests
    {
        private const string ProfileAssetPath = "Assets/Zantetsu/Settings/WorldPhysicsProfile.asset";
        private const string SampleScenePath = "Assets/Scenes/SampleScene.unity";
        private const string SampleSceneName = "SampleScene";
        private const float GravityTolerance = 1e-4f;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            // If a test failed while in Play Mode (before reaching ExitPlayMode),
            // leave Play Mode so subsequent tests run from a clean EditMode state.
            if (EditorApplication.isPlaying)
            {
                yield return new ExitPlayMode();
            }
        }

        [Test]
        public void StandardProfileAsset_HasVersionOneAndPoCGravity()
        {
            WorldPhysicsProfile profile = AssetDatabase.LoadAssetAtPath<WorldPhysicsProfile>(ProfileAssetPath);
            Assert.That(profile, Is.Not.Null, "Standard profile asset not found at " + ProfileAssetPath);
            Assert.That(profile.ProfileVersion, Is.EqualTo(1));
            Assert.That(profile.Gravity.x, Is.EqualTo(0f).Within(GravityTolerance));
            Assert.That(profile.Gravity.y, Is.EqualTo(-4.9f).Within(GravityTolerance));
            Assert.That(profile.Gravity.z, Is.EqualTo(0f).Within(GravityTolerance));
        }

        [Test]
        public void Apply_WithProfile_SetsPhysicsGravity()
        {
            Vector3 originalGravity = Physics.gravity;
            WorldPhysicsProfile profile = ScriptableObject.CreateInstance<WorldPhysicsProfile>();
            GameObject go = new GameObject("Bootstrap");
            try
            {
                WorldPhysicsBootstrap bootstrap = go.AddComponent<WorldPhysicsBootstrap>();
                SetProfile(bootstrap, profile);

                bool result = bootstrap.Apply();

                Assert.That(result, Is.True);
                Assert.That(Physics.gravity.y, Is.EqualTo(profile.Gravity.y).Within(GravityTolerance));
            }
            finally
            {
                Physics.gravity = originalGravity;
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Apply_WithoutProfile_LogsErrorDisablesAndReturnsFalse()
        {
            GameObject go = new GameObject("Bootstrap");
            try
            {
                WorldPhysicsBootstrap bootstrap = go.AddComponent<WorldPhysicsBootstrap>();
                LogAssert.Expect(LogType.Error, new Regex("has no WorldPhysicsProfile"));

                bool result = bootstrap.Apply();

                Assert.That(result, Is.False);
                Assert.That(bootstrap.enabled, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [UnityTest]
        public IEnumerator SampleScene_EnterPlayMode_AppliesGravity()
        {
            yield return new EnterPlayMode();

            // SampleScene is loaded in Play Mode (not Edit Mode), so the editor's
            // scene setup is left untouched. Capture the gravity before the
            // bootstrap applies the profile, and restore it afterwards.
            Vector3 originalGravity = Physics.gravity;
            SceneManager.LoadScene(SampleSceneName);
            yield return null;

            Assert.That(Physics.gravity.x, Is.EqualTo(0f).Within(GravityTolerance));
            Assert.That(Physics.gravity.y, Is.EqualTo(-4.9f).Within(GravityTolerance));
            Assert.That(Physics.gravity.z, Is.EqualTo(0f).Within(GravityTolerance));

            Physics.gravity = originalGravity;

            yield return new ExitPlayMode();
        }

        [Test]
        public void SampleScene_HasSingleBootstrapReferencingStandardProfile()
        {
            SceneSetup[] setup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                Scene scene = EditorSceneManager.OpenScene(SampleScenePath, OpenSceneMode.Single);
                List<WorldPhysicsBootstrap> bootstraps = new List<WorldPhysicsBootstrap>();
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    bootstraps.AddRange(root.GetComponentsInChildren<WorldPhysicsBootstrap>(true));
                }

                Assert.That(bootstraps.Count, Is.EqualTo(1));
                Assert.That(bootstraps[0].Profile, Is.Not.Null);
                Assert.That(AssetDatabase.GetAssetPath(bootstraps[0].Profile), Is.EqualTo(ProfileAssetPath));
            }
            finally
            {
                if (setup != null && setup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(setup);
                }
                else
                {
                    // The Play Mode test that ran before this one leaves the
                    // editor with no loaded scene. Restore a clean Edit Mode
                    // scene the same way the test runner itself does.
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        private static void SetProfile(WorldPhysicsBootstrap bootstrap, WorldPhysicsProfile profile)
        {
            SerializedObject serialized = new SerializedObject(bootstrap);
            serialized.FindProperty("profile").objectReferenceValue = profile;
            serialized.ApplyModifiedProperties();
        }
    }
}
