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
    }
}
