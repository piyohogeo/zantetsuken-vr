using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The sandbox scene's VP display probe (DESIGN 4.5.5 Stage 1): one root showing the built-in cube through the VP
    /// path with the VP shader, and no Unity mesh renderer of its own. Only the scene's configuration is checked;
    /// drawing is covered by VpDirectDrawTests.
    /// </summary>
    public class SandboxVpMeshDisplayTests
    {
        private const string SandboxScenePath = "Assets/Scenes/Sandbox.unity";
        private const string ProbeRootName = "VP Display Probe";
        private const string UnityMeshDisplayRootName = "Unity Mesh Display";

        private SceneSetup[] previousSetup;
        private Scene scene;

        [SetUp]
        public void SetUp()
        {
            previousSetup = EditorSceneManager.GetSceneManagerSetup();
            scene = EditorSceneManager.OpenScene(SandboxScenePath, OpenSceneMode.Single);
        }

        [TearDown]
        public void TearDown()
        {
            if (previousSetup != null && previousSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
            else
            {
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }

        [Test]
        public void TheProbe_ShowsTheBuiltInCubeThroughTheVpPathOnly()
        {
            GameObject[] roots = scene.GetRootGameObjects().Where(root => root.name == ProbeRootName).ToArray();
            Assert.That(roots, Has.Length.EqualTo(1), "one probe root");

            VpMeshDisplay[] displays = roots[0].GetComponentsInChildren<VpMeshDisplay>(true);
            Assert.That(displays.Select(display => display.name), Is.EqualTo(new[] { "VP Cube" }));
            Assert.That(displays[0].enabled, Is.True);

            var serialized = new SerializedObject(displays[0]);
            Assert.That(serialized.FindProperty("mesh").objectReferenceValue, Is.SameAs(Resources.GetBuiltinResource<Mesh>("Cube.fbx")));
            Assert.That(serialized.FindProperty("shader").objectReferenceValue, Is.SameAs(Shader.Find("Zantetsu/VP Unlit")));

            Assert.That(roots[0].GetComponentsInChildren<Renderer>(true), Is.Empty);
            Assert.That(roots[0].GetComponentsInChildren<MeshFilter>(true), Is.Empty);
        }

        [Test]
        public void TheProbe_SharesTheSceneWithTheUnityMeshDisplayAndOneShadowingDirectionalLight()
        {
            GameObject[] roots = scene.GetRootGameObjects();
            Assert.That(roots.Count(root => root.name == ProbeRootName), Is.EqualTo(1), "probe roots");
            Assert.That(roots.Count(root => root.name == UnityMeshDisplayRootName), Is.EqualTo(1), "Unity mesh display roots");

            Light[] directionalLights = roots
                .SelectMany(root => root.GetComponentsInChildren<Light>(true))
                .Where(light => light.type == LightType.Directional && light.enabled && light.gameObject.activeInHierarchy)
                .ToArray();
            Assert.That(directionalLights, Has.Length.EqualTo(1), "active directional lights");
            Assert.That(directionalLights[0].shadows, Is.Not.EqualTo(LightShadows.None));
        }
    }
}
