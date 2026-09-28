using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.Core.Animation;
using Zantetsu.Core.MobPlan;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox.Editor
{
    public static class MobPlanSceneBuild
    {
        public const string ScenePath = "Assets/Licensed/MobPlan/MobPlanCity.unity";
        public const string FloorName = "MobPlan Floor";
        // The map window MobPlanCrowd loads (PolygonWalkable.Read(polygons, -220, -170, 300, 300)), in plan coordinates.
        private const float MapCentreX = -220f, MapCentreZ = -170f, MapWindowMeters = 300f, FloorThickness = 1f;
        [MenuItem("Zantetsu/MobPlan/Build imported city scene")]
        public static void Build()
        {
            AssetDatabase.Refresh();
            const string folder = "Assets/Licensed/MobPlan/";
            var data = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(folder + "MobPlanAssets.asset");
            if (data == null) { data = ScriptableObject.CreateInstance<MobPlanAssets>(); AssetDatabase.CreateAsset(data, folder + "MobPlanAssets.asset"); }
            data.dataset = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "dataset.bytes");
            data.polygons = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "polygons.bytes");
            data.poseTables = Directory.GetFiles(folder + "Tables", "*.bytes").OrderBy(p => p, StringComparer.Ordinal)
                .Select(p => AssetDatabase.LoadAssetAtPath<TextAsset>(p.Replace('\\', '/'))).ToArray();
            data.initialPose = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "Tables/Inertial_AS_Base_AS_Base.bytes");
            data.provenance = AssetDatabase.LoadAssetAtPath<TextAsset>(folder + "manifest.json");
            if (data.poseTables.Length != 320 || data.initialPose == null) throw new InvalidOperationException("Run the private table import first");
            EditorUtility.SetDirty(data);
            var scene = EditorSceneManager.OpenScene("Assets/Licensed/XrSimCity/XrSimCity.unity");
            foreach (var behaviour in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (behaviour != null && (behaviour.GetType().Name == "XrSimCityCrowd" || behaviour.GetType().Name == "XrSimCityWalker"))
                    UnityEngine.Object.DestroyImmediate(behaviour);
            var characters = UnityEngine.Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(c => c.name, StringComparer.Ordinal).ToArray();
            if (characters.Length < 20) throw new InvalidOperationException("The source city requires at least 20 audited Casual rigs");
            foreach (var npc in characters.Skip(20)) { UnityEngine.Object.DestroyImmediate(npc.CharacterRoot); UnityEngine.Object.DestroyImmediate(npc.gameObject); }
            characters = characters.Take(20).ToArray();
            foreach (var npc in characters)
            {
                npc.gameObject.SetActive(true); npc.CharacterRoot.SetActive(true);
                var pose = npc.CharacterRoot.GetComponent<PoseTablePlayer>();
                pose.Configure(data.initialPose, pose.ModelRoot, 0, true);
            }
            // Keep a never-prepared template separate from every cut hierarchy; cloning remaps the rig references.
            var template = new GameObject("MobPlan replacement template");
            template.SetActive(false);
            var templateRoot = UnityEngine.Object.Instantiate(characters[0].CharacterRoot, template.transform);
            var templateSetup = UnityEngine.Object.Instantiate(characters[0].gameObject, template.transform);
            var npcTemplate = templateSetup.GetComponent<SandboxNpcCharacter>();
            var npcSerialized = new SerializedObject(npcTemplate);
            npcSerialized.FindProperty("characterRoot").objectReferenceValue = templateRoot;
            npcSerialized.FindProperty("motionBody").objectReferenceValue = templateRoot.GetComponentInChildren<Rigidbody>(true);
            npcSerialized.ApplyModifiedPropertiesWithoutUndo();
            var crowd = new GameObject("MobPlan").AddComponent<MobPlanCrowd>();
            var serialized = new SerializedObject(crowd);
            serialized.FindProperty("assets").objectReferenceValue = data;
            serialized.FindProperty("world").objectReferenceValue = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
            serialized.FindProperty("player").objectReferenceValue = UnityEngine.Object.FindFirstObjectByType<PlayerLocomotion>();
            serialized.FindProperty("view").objectReferenceValue = Camera.main.transform;
            serialized.FindProperty("replacementTemplate").objectReferenceValue = template;
            var array = serialized.FindProperty("initialCharacters"); array.arraySize = 20;
            for (int i = 0; i < 20; i++) array.GetArrayElementAtIndex(i).objectReferenceValue = characters[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            foreach (var input in UnityEngine.Object.FindObjectsByType<SandboxLocomotionInput>(FindObjectsSortMode.None))
            {
                input.enabled = false;
                var inputSettings = new SerializedObject(input);
                inputSettings.FindProperty("moveSpeed").floatValue = 1.49f;
                inputSettings.ApplyModifiedPropertiesWithoutUndo();
            }
            var manual = crowd.gameObject.AddComponent<MobPlanPlayerInput>();
            manual.crowd = crowd;
            manual.player = UnityEngine.Object.FindFirstObjectByType<PlayerLocomotion>();
            // The source city's floor covers only its own walking area near the origin, and its road tiles have no
            // colliders: the plan's area gets one flat static floor at the height the plan stands the NPCs on (its offset's
            // y), over the map's window. Nothing is drawn with it; the plan's map, not this collider, bounds movement.
            Vector3 offset = serialized.FindProperty("mapOffset").vector3Value;
            var floor = new GameObject(FloorName);
            floor.transform.position = new Vector3(MapCentreX + offset.x, offset.y - FloorThickness / 2f, MapCentreZ + offset.z);
            floor.isStatic = true;
            floor.AddComponent<BoxCollider>().size = new Vector3(MapWindowMeters, FloorThickness, MapWindowMeters);
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("MOBPLAN scene built: " + ScenePath);
        }
        public static void BatchBuild()
        {
            try { Build(); EditorApplication.Exit(0); }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
        }

        // The XR Player of the built city (-zantetsuPlayerOut <dir>), through the sandbox's own Player build. The frame
        // timing statistics are on for this build only, as the XR Simulator city's measurement Player has them.
        public static void BuildPlayerFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-zantetsuPlayerOut");
            string directory = at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            bool before = PlayerSettings.enableFrameTimingStats;
            bool built = false;
            try
            {
                PlayerSettings.enableFrameTimingStats = true;
                built = !string.IsNullOrEmpty(directory) && Zantetsu.EditorTools.Sandbox.CutWorldSandboxPlayerBuild.Build(directory, ScenePath);
            }
            catch (Exception e) { Debug.LogException(e); }
            finally
            {
                PlayerSettings.enableFrameTimingStats = before;
                AssetDatabase.SaveAssets();
            }
            EditorApplication.Exit(built ? 0 : 1);
        }
    }
}
