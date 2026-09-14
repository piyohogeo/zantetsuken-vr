using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Zantetsu.MeshCut.Verification;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Phase 0.9 focused tests: the sandbox scene shows a few representative
    /// Unity Meshes, with instances of one shape sharing one Mesh at different
    /// transforms, lit by one directional light plus ambient, with shadows.
    /// Built-in primitives are always present. The adopted fixture instances
    /// reference licensed meshes generated outside git, so every test passes
    /// in a checkout without them: the scene's references are always checked,
    /// and the meshes themselves only where they have been generated.
    /// Only the scene's configuration is checked -- no pixels, topology,
    /// submeshes or GPU state.
    /// </summary>
    public class SandboxUnityMeshDisplayTests
    {
        private const string SandboxScenePath = "Assets/Scenes/Sandbox.unity";
        private const string DisplayRootName = "Unity Mesh Display";
        private const string AdoptedPrefix = "Adopted ";
        private const string LicensedMeshFolder = "Assets/Licensed/DisplayMeshes/";
        private const int MaximumAdoptedTriangles = 10000;

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

        private GameObject FindDisplayRoot()
        {
            GameObject found = null;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == DisplayRootName)
                {
                    Assert.That(found, Is.Null, "more than one display root");
                    found = root;
                }
            }

            Assert.That(found, Is.Not.Null, "no display root");
            return found;
        }

        private static void AssertPlacedDifferently(Transform a, Transform b)
        {
            bool sameTransform = a.position == b.position && a.rotation == b.rotation && a.lossyScale == b.lossyScale;
            Assert.That(sameTransform, Is.False, a.name + " and " + b.name + " are placed differently");
        }

        // Each MeshFilter's saved mesh reference, by GameObject name, read from the
        // scene file: a reference to a mesh this checkout does not have is still there.
        private static Dictionary<string, string> MeshReferencesInSceneFile()
        {
            string text = File.ReadAllText(SandboxScenePath);
            Dictionary<string, string> gameObjectNames = new Dictionary<string, string>();
            Dictionary<string, string> meshByGameObject = new Dictionary<string, string>();
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
                else if (header.Groups[1].Value == "33")
                {
                    Match owner = Regex.Match(document, @"m_GameObject: \{fileID: (\d+)\}");
                    Match mesh = Regex.Match(document, @"m_Mesh: (\{[^}]*\})");
                    if (owner.Success && mesh.Success)
                    {
                        meshByGameObject[owner.Groups[1].Value] = mesh.Groups[1].Value;
                    }
                }
            }

            Dictionary<string, string> references = new Dictionary<string, string>();
            foreach (KeyValuePair<string, string> pair in meshByGameObject)
            {
                if (gameObjectNames.TryGetValue(pair.Key, out string name))
                {
                    references[name] = pair.Value;
                }
            }

            return references;
        }

        [Test]
        public void TheSandboxScene_HasExactlyOneUnityMeshDisplayRoot()
        {
            GameObject displayRoot = FindDisplayRoot();

            Assert.That(displayRoot.activeSelf, Is.True);
            Assert.That(displayRoot.GetComponentsInChildren<MeshFilter>(true).Length, Is.GreaterThan(1),
                "the display has more than one instance");
        }

        [Test]
        public void BuiltInInstancesOfOneShape_ShareOneMeshAtDifferentTransforms()
        {
            MeshFilter[] filters = FindDisplayRoot().GetComponentsInChildren<MeshFilter>(true)
                .Where(filter => !filter.name.StartsWith(AdoptedPrefix)).ToArray();

            Dictionary<Mesh, List<MeshFilter>> byMesh = new Dictionary<Mesh, List<MeshFilter>>();
            foreach (MeshFilter filter in filters)
            {
                Assert.That(filter.sharedMesh, Is.Not.Null, filter.name + " has no mesh");
                if (!byMesh.TryGetValue(filter.sharedMesh, out List<MeshFilter> group))
                {
                    group = new List<MeshFilter>();
                    byMesh.Add(filter.sharedMesh, group);
                }

                group.Add(filter);
            }

            Assert.That(byMesh.Count, Is.GreaterThanOrEqualTo(2), "more than one built-in shape");
            foreach (KeyValuePair<Mesh, List<MeshFilter>> shape in byMesh)
            {
                List<MeshFilter> group = shape.Value;
                Assert.That(group.Count, Is.GreaterThanOrEqualTo(2), shape.Key.name + " is shown more than once");

                for (int i = 0; i < group.Count; i++)
                {
                    Assert.That(group[i].sharedMesh, Is.SameAs(group[0].sharedMesh),
                        group[i].name + " shares " + group[0].name + "'s Mesh");

                    for (int j = i + 1; j < group.Count; j++)
                    {
                        AssertPlacedDifferently(group[i].transform, group[j].transform);
                    }
                }
            }
        }

        [Test]
        public void TheDisplay_HasTwoAdoptedSlotsPerCategoryReferencingItsLicensedMesh()
        {
            Assert.That(LicensedDisplayMeshes.Selection.Select(entry => entry.Category),
                Is.EquivalentTo(new[] { "Character", "Vehicle", "Building", "Prop" }));

            Transform displayRoot = FindDisplayRoot().transform;
            Dictionary<string, string> references = MeshReferencesInSceneFile();

            foreach (LicensedDisplayMesh entry in LicensedDisplayMeshes.Selection)
            {
                StringAssert.StartsWith(LicensedMeshFolder, LicensedDisplayMeshes.AssetPathFor(entry),
                    "licensed meshes are kept in the git-ignored folder");
                string guid = LicensedDisplayMeshes.GuidFor(entry);

                Transform[] slots = new Transform[2];
                for (int i = 0; i < slots.Length; i++)
                {
                    string slotName = AdoptedPrefix + entry.Category + " " + i;
                    slots[i] = displayRoot.Find(slotName);
                    Assert.That(slots[i], Is.Not.Null, "no " + slotName);
                    Assert.That(slots[i].GetComponent<MeshFilter>(), Is.Not.Null, slotName + " has a MeshFilter");

                    Assert.That(references.TryGetValue(slotName, out string reference), Is.True,
                        slotName + " has a saved mesh reference");
                    StringAssert.Contains("fileID: 4300000,", reference, slotName + " references a mesh asset");
                    StringAssert.Contains("guid: " + guid + ",", reference, slotName + " references the " + entry.Category + " mesh");
                }

                AssertPlacedDifferently(slots[0], slots[1]);
            }
        }

        [Test]
        public void AdoptedSlots_ShowTheLicensedMeshWhereItHasBeenGenerated()
        {
            Transform displayRoot = FindDisplayRoot().transform;
            AdoptedFixtureCatalog licensed = null;

            foreach (LicensedDisplayMesh entry in LicensedDisplayMeshes.Selection)
            {
                MeshFilter first = displayRoot.Find(AdoptedPrefix + entry.Category + " 0").GetComponent<MeshFilter>();
                MeshFilter second = displayRoot.Find(AdoptedPrefix + entry.Category + " 1").GetComponent<MeshFilter>();
                string assetPath = LicensedDisplayMeshes.AssetPathFor(entry);
                Mesh asset = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);

                if (asset == null)
                {
                    // Not generated in this checkout: both slots simply show nothing.
                    Assert.That(first.sharedMesh == null, Is.True, first.name + " has no mesh to show");
                    Assert.That(second.sharedMesh == null, Is.True, second.name + " has no mesh to show");
                    continue;
                }

                Assert.That(AssetDatabase.AssetPathToGUID(assetPath), Is.EqualTo(LicensedDisplayMeshes.GuidFor(entry)),
                    entry.Category + " was generated with its fixed GUID");
                Assert.That(first.sharedMesh, Is.Not.Null, first.name + " shows the generated mesh");
                Assert.That(first.sharedMesh, Is.SameAs(asset), first.name + " shows the generated mesh");
                Assert.That(second.sharedMesh, Is.SameAs(first.sharedMesh), second.name + " shares " + first.name + "'s Mesh");
                Assert.That(asset.GetIndexCount(0) / 3, Is.LessThanOrEqualTo((uint)MaximumAdoptedTriangles),
                    entry.Category + " stays within the triangle budget");

                if (!LicensedDisplayMeshes.IsPrivateRepositoryAvailable)
                {
                    continue;
                }

                if (licensed == null)
                {
                    licensed = AdoptedFixtureCatalog.LoadLicensed(LicensedDisplayMeshes.PrivateRepositoryRoot);
                }

                ZcgDocument geometry = licensed.Resolve(AdoptedFixtureUse.RenderCut)
                    .Single(fixture => fixture.ArtifactId == entry.ArtifactId).Geometry;
                Assert.That(geometry.Kind, Is.EqualTo(ZcgGeometryKind.TriangleMesh));
                Assert.That(geometry.Triangles.Length, Is.LessThanOrEqualTo(MaximumAdoptedTriangles));
                Assert.That(asset.vertexCount, Is.EqualTo(geometry.Positions.Length), entry.Category + " vertices");
                Assert.That(asset.GetIndexCount(0), Is.EqualTo((uint)(geometry.Triangles.Length * 3)), entry.Category + " indices");
            }
        }

        [Test]
        public void DisplayRenderers_AreEnabledWithAMaterialAndCastAndReceiveShadows()
        {
            GameObject displayRoot = FindDisplayRoot();
            MeshRenderer[] renderers = displayRoot.GetComponentsInChildren<MeshRenderer>(true);
            Assert.That(renderers.Length, Is.EqualTo(displayRoot.GetComponentsInChildren<MeshFilter>(true).Length),
                "every instance renders");

            foreach (MeshRenderer meshRenderer in renderers)
            {
                Assert.That(meshRenderer.gameObject.activeInHierarchy, Is.True, meshRenderer.name + " is active");
                Assert.That(meshRenderer.enabled, Is.True, meshRenderer.name + " is enabled");
                Assert.That(meshRenderer.sharedMaterial, Is.Not.Null, meshRenderer.name + " has a material");
                Assert.That(meshRenderer.shadowCastingMode, Is.EqualTo(ShadowCastingMode.On), meshRenderer.name + " casts shadows");
                Assert.That(meshRenderer.receiveShadows, Is.True, meshRenderer.name + " receives shadows");
            }

            Assert.That(displayRoot.GetComponentsInChildren<Collider>(true), Is.Empty, "the display is display only");
        }

        [Test]
        public void TheSandboxScene_IsLitByAShadowingDirectionalLightPlusAmbient()
        {
            int directionalLights = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Light sceneLight in root.GetComponentsInChildren<Light>(true))
                {
                    if (sceneLight.type != LightType.Directional)
                    {
                        continue;
                    }

                    directionalLights++;
                    Assert.That(sceneLight.isActiveAndEnabled, Is.True, "the directional light is on");
                    Assert.That(sceneLight.intensity, Is.GreaterThan(0f));
                    Assert.That(sceneLight.shadows, Is.Not.EqualTo(LightShadows.None), "the directional light casts shadows");
                }
            }

            Assert.That(directionalLights, Is.EqualTo(1), "one directional light");

            Assert.That(RenderSettings.ambientIntensity, Is.GreaterThan(0f), "ambient is not switched off");
            if (RenderSettings.ambientMode == AmbientMode.Skybox)
            {
                Assert.That(RenderSettings.skybox, Is.Not.Null, "skybox ambient has a skybox to sample");
            }
            else
            {
                Color sky = RenderSettings.ambientSkyColor;
                Assert.That(sky.r + sky.g + sky.b, Is.GreaterThan(0f), "ambient light is not black");
            }
        }
    }
}
