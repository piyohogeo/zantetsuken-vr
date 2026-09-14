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
    /// The sandbox scene's VP displays (DESIGN 4.5.5 Stage 1): a probe root showing the built-in cube, and a small
    /// subset of the adopted licensed meshes shown through the VP path next to the Unity mesh adopted grid, each shape
    /// twice at different transforms. The subset's mesh references are read from the scene file, so every test passes
    /// in a checkout without the licensed meshes. Only the scene's configuration is checked; drawing is covered by
    /// VpDirectDrawTests.
    /// </summary>
    public class SandboxVpMeshDisplayTests
    {
        private const string SandboxScenePath = "Assets/Scenes/Sandbox.unity";
        private const string ProbeRootName = "VP Display Probe";
        private const string UnityMeshDisplayRootName = "Unity Mesh Display";
        private const string AdoptedGridName = "Adopted Grid";
        private const string SubsetName = "VP Adopted Subset";
        private const string VpShaderName = "Zantetsu/VP Unlit";
        private const string VpMeshDisplayScriptPath = "Assets/Zantetsu/Runtime/Rendering/VpMeshDisplay.cs";
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

        // Each VpMeshDisplay's saved mesh reference, by GameObject name, read from the scene file: a reference to a
        // mesh this checkout does not have is still there.
        private static Dictionary<string, string> VpMeshReferencesInSceneFile()
        {
            string scriptGuid = AssetDatabase.AssetPathToGUID(VpMeshDisplayScriptPath);
            Assert.That(scriptGuid, Is.Not.Empty, VpMeshDisplayScriptPath);
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
            Transform display = scene.GetRootGameObjects().Single(root => root.name == UnityMeshDisplayRootName).transform;
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

            Dictionary<string, string> references = VpMeshReferencesInSceneFile();
            Shader vpShader = Shader.Find(VpShaderName);
            Assert.That(vpShader, Is.Not.Null, VpShaderName);
            foreach (string category in SubsetCategories)
            {
                LicensedDisplayMesh entry = LicensedDisplayMeshes.Selection.Single(selected => selected.Category == category);
                string guid = LicensedDisplayMeshes.GuidFor(entry);
                Mesh asset = AssetDatabase.LoadAssetAtPath<Mesh>(LicensedDisplayMeshes.AssetPathFor(entry));

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
                    StringAssert.Contains("fileID: 4300000,", savedReferences[i], instanceName + " references a mesh asset");
                    StringAssert.Contains("guid: " + guid + ",", savedReferences[i], instanceName + " references the " + category + " mesh");

                    var serialized = new SerializedObject(vpDisplay);
                    Assert.That(serialized.FindProperty("shader").objectReferenceValue, Is.SameAs(vpShader), instanceName + " uses the VP shader");
                    Object mesh = serialized.FindProperty("mesh").objectReferenceValue;
                    if (asset == null)
                    {
                        // Not generated in this checkout: the reference resolves to nothing and the instance shows nothing.
                        Assert.That(mesh == null, Is.True, instanceName + " has no mesh to show");
                    }
                    else
                    {
                        Assert.That(mesh, Is.SameAs(asset), instanceName + " shows the generated " + category + " mesh");
                    }
                }

                Assert.That(savedReferences[1], Is.EqualTo(savedReferences[0]), category + " instances reference one mesh");
                bool sameTransform = instances[0].position == instances[1].position
                    && instances[0].rotation == instances[1].rotation
                    && instances[0].lossyScale == instances[1].lossyScale;
                Assert.That(sameTransform, Is.False, category + " instances are placed differently");
            }
        }
    }
}
