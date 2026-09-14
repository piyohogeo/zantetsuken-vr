using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The sandbox scene's VP displays (DESIGN 4.5.5 Stage 1): a probe root showing the built-in cube, a small subset of
    /// the adopted licensed meshes shown through the VP path next to the Unity mesh adopted grid, each shape twice at
    /// different transforms, and a shared geometry probe drawing one licensed mesh's single VP range at each of its child
    /// transforms. Licensed mesh references are read from the scene file, so every test passes in a checkout without the
    /// licensed meshes. Only the scene's configuration is checked; drawing is covered by VpDirectDrawTests.
    /// </summary>
    public class SandboxVpMeshDisplayTests
    {
        private const string SandboxScenePath = "Assets/Scenes/Sandbox.unity";
        private const string ProbeRootName = "VP Display Probe";
        private const string UnityMeshDisplayRootName = "Unity Mesh Display";
        private const string AdoptedGridName = "Adopted Grid";
        private const string SubsetName = "VP Adopted Subset";
        private const string SharedProbeName = "VP Shared Geometry Probe";
        private const string SharedProbeCategory = "Character";
        private const string VpShaderName = "Zantetsu/VP Unlit";
        private const string VpMeshDisplayScriptPath = "Assets/Zantetsu/Runtime/Rendering/VpMeshDisplay.cs";
        private const string VpSharedMeshDisplayScriptPath = "Assets/Zantetsu/Runtime/Rendering/VpSharedMeshDisplay.cs";
        private const int SubsetInstancesPerCategory = 2;
        private static readonly string[] SubsetCategories = { "Character", "Vehicle" };

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

        // The saved mesh reference of each component of the script, by GameObject name, read from the scene file: a
        // reference to a mesh this checkout does not have is still there.
        private static Dictionary<string, string> MeshReferencesInSceneFile(string scriptPath)
        {
            string scriptGuid = AssetDatabase.AssetPathToGUID(scriptPath);
            Assert.That(scriptGuid, Is.Not.Empty, scriptPath);
            string text = File.ReadAllText(SandboxScenePath);
            var gameObjectNames = new Dictionary<string, string>();
            var meshByGameObject = new Dictionary<string, string>();
            foreach (string document in Regex.Split(text, @"^--- ", RegexOptions.Multiline))
            {
                Match header = Regex.Match(document, @"^!u!(\d+) &(\d+)");
                if (!header.Success)
                {
                    continue;
                }

                if (header.Groups[1].Value == "1")
                {
                    Match name = Regex.Match(document, @"^\s*m_Name: (.*)$", RegexOptions.Multiline);
                    if (name.Success)
                    {
                        gameObjectNames[header.Groups[2].Value] = name.Groups[1].Value.Trim();
                    }
                }
                else if (header.Groups[1].Value == "114" && document.Contains("guid: " + scriptGuid + ","))
                {
                    Match owner = Regex.Match(document, @"m_GameObject: \{fileID: (\d+)\}");
                    Match mesh = Regex.Match(document, @"^\s*mesh: (\{[^}]*\})", RegexOptions.Multiline);
                    if (owner.Success && mesh.Success)
                    {
                        meshByGameObject[owner.Groups[1].Value] = mesh.Groups[1].Value;
                    }
                }
            }

            return meshByGameObject
                .Where(pair => gameObjectNames.ContainsKey(pair.Key))
                .ToDictionary(pair => gameObjectNames[pair.Key], pair => pair.Value);
        }

        private Transform UnityMeshDisplay()
        {
            return scene.GetRootGameObjects().Single(root => root.name == UnityMeshDisplayRootName).transform;
        }

        /// <summary>The component's saved licensed mesh reference and its resolved mesh match the category's generated mesh, if any.</summary>
        private static void AssertLicensedMesh(Component component, string savedReference, string category, string what)
        {
            LicensedDisplayMesh entry = LicensedDisplayMeshes.Selection.Single(selected => selected.Category == category);
            StringAssert.Contains("fileID: 4300000,", savedReference, what + " references a mesh asset");
            StringAssert.Contains("guid: " + LicensedDisplayMeshes.GuidFor(entry) + ",", savedReference, what + " references the " + category + " mesh");

            var serialized = new SerializedObject(component);
            Assert.That(serialized.FindProperty("shader").objectReferenceValue, Is.SameAs(Shader.Find(VpShaderName)), what + " uses the VP shader");
            Object mesh = serialized.FindProperty("mesh").objectReferenceValue;
            Mesh asset = AssetDatabase.LoadAssetAtPath<Mesh>(LicensedDisplayMeshes.AssetPathFor(entry));
            if (asset == null)
            {
                // Not generated in this checkout: the reference resolves to nothing and nothing is shown.
                Assert.That(mesh == null, Is.True, what + " has no mesh to show");
            }
            else
            {
                Assert.That(mesh, Is.SameAs(asset), what + " shows the generated " + category + " mesh");
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
            Assert.That(serialized.FindProperty("shader").objectReferenceValue, Is.SameAs(Shader.Find(VpShaderName)));

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

        [Test]
        public void TheAdoptedSubset_ShowsEachOfTwoLicensedMeshesTwiceThroughTheVpPathOnly()
        {
            Transform display = UnityMeshDisplay();
            Transform[] subsets = display.GetComponentsInChildren<Transform>(true).Where(t => t.name == SubsetName).ToArray();
            Assert.That(subsets, Has.Length.EqualTo(1), "one subset root");
            Transform subset = subsets[0];
            Assert.That(subset.parent, Is.SameAs(display), "the subset sits directly under the Unity mesh display");

            Transform grid = display.Find(AdoptedGridName);
            Assert.That(grid, Is.Not.Null, "the Unity mesh adopted grid stays");
            Assert.That(grid.GetComponentsInChildren<MeshFilter>(true), Is.Not.Empty, "the grid still shows Unity meshes");

            Assert.That(subset.childCount, Is.EqualTo(SubsetCategories.Length * SubsetInstancesPerCategory), "a few instances only");
            Assert.That(subset.GetComponentsInChildren<Renderer>(true), Is.Empty, "no Unity renderers");
            Assert.That(subset.GetComponentsInChildren<MeshFilter>(true), Is.Empty, "no mesh filters");
            Assert.That(subset.GetComponentsInChildren<Collider>(true), Is.Empty, "display only");

            Dictionary<string, string> references = MeshReferencesInSceneFile(VpMeshDisplayScriptPath);
            foreach (string category in SubsetCategories)
            {
                var instances = new Transform[SubsetInstancesPerCategory];
                var savedReferences = new string[SubsetInstancesPerCategory];
                for (int i = 0; i < instances.Length; i++)
                {
                    string instanceName = "VP " + category + " " + i;
                    instances[i] = subset.Find(instanceName);
                    Assert.That(instances[i], Is.Not.Null, "no " + instanceName);
                    VpMeshDisplay vpDisplay = instances[i].GetComponent<VpMeshDisplay>();
                    Assert.That(vpDisplay, Is.Not.Null, instanceName + " has a VpMeshDisplay");
                    Assert.That(vpDisplay.enabled, Is.True, instanceName + " is enabled");

                    Assert.That(references.TryGetValue(instanceName, out savedReferences[i]), Is.True, instanceName + " has a saved mesh reference");
                    AssertLicensedMesh(vpDisplay, savedReferences[i], category, instanceName);
                }

                Assert.That(savedReferences[1], Is.EqualTo(savedReferences[0]), category + " instances reference one mesh");
                bool sameTransform = instances[0].position == instances[1].position
                    && instances[0].rotation == instances[1].rotation
                    && instances[0].lossyScale == instances[1].lossyScale;
                Assert.That(sameTransform, Is.False, category + " instances are placed differently");
            }
        }

        [Test]
        public void TheSharedGeometryProbe_DrawsOneLicensedMeshAtEachOfItsChildTransforms()
        {
            Transform display = UnityMeshDisplay();
            Transform[] probes = display.GetComponentsInChildren<Transform>(true).Where(t => t.name == SharedProbeName).ToArray();
            Assert.That(probes, Has.Length.EqualTo(1), "one shared geometry probe");
            Transform probe = probes[0];
            Assert.That(probe.parent, Is.SameAs(display), "the probe sits directly under the Unity mesh display");
            Assert.That(display.Find(SubsetName), Is.Not.Null, "the VP adopted subset stays");
            Assert.That(display.Find(AdoptedGridName).GetComponentsInChildren<MeshFilter>(true), Is.Not.Empty, "the Unity mesh adopted grid stays");

            VpSharedMeshDisplay shared = probe.GetComponent<VpSharedMeshDisplay>();
            Assert.That(shared, Is.Not.Null, "the probe root holds the shared display");
            Assert.That(shared.enabled, Is.True);
            Assert.That(probe.GetComponentsInChildren<VpSharedMeshDisplay>(true), Has.Length.EqualTo(1), "one shared display");
            Assert.That(probe.GetComponentsInChildren<VpMeshDisplay>(true), Is.Empty, "no per-instance displays");
            Assert.That(probe.GetComponentsInChildren<Renderer>(true), Is.Empty, "no Unity renderers");
            Assert.That(probe.GetComponentsInChildren<MeshFilter>(true), Is.Empty, "no mesh filters");
            Assert.That(probe.GetComponentsInChildren<Collider>(true), Is.Empty, "display only");

            Dictionary<string, string> references = MeshReferencesInSceneFile(VpSharedMeshDisplayScriptPath);
            Assert.That(references.TryGetValue(SharedProbeName, out string savedReference), Is.True, "the probe has a saved mesh reference");
            AssertLicensedMesh(shared, savedReference, SharedProbeCategory, SharedProbeName);

            Assert.That(probe.childCount, Is.GreaterThanOrEqualTo(2), "the one geometry is drawn at two or more transforms");
            Transform[] instances = probe.Cast<Transform>().ToArray();
            foreach (Transform instance in instances)
            {
                Assert.That(instance.GetComponents<Component>(), Has.Length.EqualTo(1), instance.name + " carries a transform only");
                Assert.That(instance.gameObject.activeSelf, Is.True, instance.name + " is active");
            }

            for (int i = 0; i < instances.Length; i++)
            {
                for (int j = i + 1; j < instances.Length; j++)
                {
                    string pair = instances[i].name + " and " + instances[j].name;
                    Assert.That(instances[i].position, Is.Not.EqualTo(instances[j].position), pair + " differ in position");
                    Assert.That(instances[i].rotation, Is.Not.EqualTo(instances[j].rotation), pair + " differ in rotation");
                    Assert.That(instances[i].lossyScale, Is.Not.EqualTo(instances[j].lossyScale), pair + " differ in scale");
                }
            }
        }
    }
}
