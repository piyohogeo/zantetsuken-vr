using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>Private real-asset scenario. Generated scene/assets remain outside version control.</summary>
    public static class BuildingAnchorScenarioBuild
    {
        public const string Root = "Assets/Licensed/BuildingAnchorScenario";
        public const string ScenePath = Root + "/BuildingAnchorCity.unity";
        [MenuItem("Zantetsu/Sandbox/Build Building and Anchor City Test")]
        public static void BuildScene()
        {
            AssetDatabase.Refresh();
            var building = LoadAndCheck("bar_001");
            var prop = LoadAndCheck("advertising_001");
            var scene = EditorSceneManager.OpenScene("Assets/Licensed/XrSimCity/XrSimCity.unity");
            foreach (var npc in UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (npc.CharacterRoot != null) UnityEngine.Object.DestroyImmediate(npc.CharacterRoot);
                if (npc != null) UnityEngine.Object.DestroyImmediate(npc.gameObject);
            }
            foreach (var component in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (component != null && (component.GetType().Name == "XrSimCityCrowd" || component.GetType().Name == "XrSimCityWalker"))
                    UnityEngine.Object.DestroyImmediate(component);
            var world = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
            foreach (var probe in UnityEngine.Object.FindObjectsByType<SandboxCutWorldProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(probe);
            foreach (var hit in UnityEngine.Object.FindObjectsByType<SandboxSlashPropHit>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                UnityEngine.Object.DestroyImmediate(hit);
            foreach (var camera in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                camera.gameObject.SetActive(false);
            var view = new GameObject("Building Anchor Check View").AddComponent<Camera>();
            view.transform.position = new Vector3(0, 10, -8);
            view.transform.LookAt(new Vector3(0, 4, 14));
            view.fieldOfView = 55; view.farClipPlane = 400; view.nearClipPlane = 0.1f;
            view.stereoTargetEye = StereoTargetEyeMask.None;
            var draw = new SerializedObject(world.GetComponent<CutWorldCameraDrawing>());
            var cameras = draw.FindProperty("cameras"); cameras.arraySize = 1;
            cameras.GetArrayElementAtIndex(0).objectReferenceValue = view;
            draw.ApplyModifiedPropertiesWithoutUndo();
            // Shared Compact16uv atlas; the authored UV coordinates select its Megacity region.
            var template = AssetDatabase.LoadAssetAtPath<Material>("Assets/Licensed/Compact16uvIntake/SceneIntegration/CharacterForward.mat");
            if (template == null)
                template = AssetDatabase.LoadAssetAtPath<Material>("Assets/Zantetsu/Settings/CutWorldSandboxSide.mat");
            var materialPath = Root + "/MegacityCutSurface.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null) { material = new Material(template); AssetDatabase.CreateAsset(material, materialPath); }
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_VpUsePaletteAtlas", 1);
            var sw = new SerializedObject(world);
            var bindings = sw.FindProperty("materials"); bindings.arraySize = 1;
            bindings.GetArrayElementAtIndex(0).FindPropertyRelative("sourceIndex").intValue = 0;
            bindings.GetArrayElementAtIndex(0).FindPropertyRelative("material").objectReferenceValue = material;
            CutWorldShadowCasters.Assign(sw); sw.ApplyModifiedPropertiesWithoutUndo();
            var scenario = world.gameObject.AddComponent<BuildingAnchorScenario>();
            scenario.world = world; scenario.buildingInput = building; scenario.propInput = prop; scenario.view = view;
            AssetDatabase.SaveAssets();
            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new IOException("Scene save failed");
            Debug.Log("BUILDING ANCHOR SCENE: " + ScenePath);
        }
        static TextAsset LoadAndCheck(string name)
        {
            var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/Inputs/" + name + ".json");
            if (asset == null) throw new FileNotFoundException("Export the private blend inputs first: " + name);
            var data = JsonUtility.FromJson<BuildingAnchorScenario.Fixture>(asset.text);
            var verdict = VpCutInputGate.Check(data.Vertices(), data.indices, data.topology, data.topologyCount,
                new[] { new VpGeometrySubmesh(0, data.indices.Length, 0) });
            if (!verdict.Accepted) throw new InvalidOperationException(name + " topology gate: " + verdict);
            Debug.Log("BUILDING ANCHOR INPUT: " + name + " triangles=" + data.indices.Length / 3 + " anchors=" + data.anchors.Length);
            return asset;
        }
        public static void BuildPlayer()
        {
            var xr = UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            bool previous = xr.InitManagerOnStart;
            bool success = false;
            try
            {
                BuildScene();
                xr.InitManagerOnStart = false; EditorUtility.SetDirty(xr); AssetDatabase.SaveAssets();
                success = CutWorldSandboxPlayerBuild.Build(Path.GetFullPath("Builds/BuildingAnchorScenario/Player"), ScenePath);
            }
            catch (Exception e) { Debug.LogException(e); }
            finally { xr.InitManagerOnStart = previous; EditorUtility.SetDirty(xr); AssetDatabase.SaveAssets(); }
            EditorApplication.Exit(success ? 0 : 1);
        }
    }
}
