using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Core.MobPlan;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The NPC skin benchmark's scene (DESIGN 3.4, npc-skin-bench): the city scene stripped to what the benchmark needs --
    /// the XR rig and its camera, the light, the cut world and its physics bootstrap, the scenario's pipeline and volume,
    /// the crowd's slot objects as the benchmark's templates (one a model family, inactive, the character root beneath
    /// its holder) -- with a ground plane and the benchmark's runner added; everything else of the city (the placed
    /// cuttables, the crowd and its plan, the katana, the walking inputs, the recorders, the bone level of detail) is
    /// removed. Made from the licensed WalkCity scene for the PC Player and from the configured QuestCityWalk scene for
    /// the Quest's APK, by the same stripping, so that the two keep each platform's materials, pipeline and profile. The
    /// world's profile is copied with the skin draw's model room widened to every family the benchmark may use.
    /// </summary>
    public static class NpcSkinBenchSceneBuild
    {
        public const string PcSourceScene = "Assets/Licensed/WalkCity/WalkCity.unity";
        public const string PcScenePath = "Assets/Licensed/NpcSkinBench/NpcSkinBench.unity";
        public const string PcProfilePath = "Assets/Licensed/NpcSkinBench/NpcSkinBenchProfile.asset";
        public const string QuestSourceScene = "Assets/Licensed/Quest3s/QuestCityWalk.unity";
        public const string QuestScenePath = "Assets/Licensed/Quest3s/QuestNpcSkinBench.unity";
        public const string QuestProfilePath = "Assets/Licensed/Quest3s/QuestNpcSkinBenchProfile.asset";

        /// <summary>The skin draw's model room of the benchmark's profile: every family the benchmark may use at once.</summary>
        public const int SkinModelCapacity = NpcSkinBenchSettings.FamiliesLimit;

        /// <summary>The city objects kept, by name (the slots are found by their component, not by name).</summary>
        private static readonly string[] k_kept = { "XR Origin", "Directional Light", "Prop Cut World", "World Physics Bootstrap", "Global Volume", "Scenario Rendering" };

        /// <summary>For -executeMethod: the PC benchmark scene from the licensed city scene, then exits.</summary>
        public static void BuildPcFromCommandLine() => FromCommandLine(PcSourceScene, PcScenePath, PcProfilePath);

        /// <summary>For -executeMethod: the Quest benchmark scene from the configured Quest city-walk scene, then exits.</summary>
        public static void BuildQuestFromCommandLine() => FromCommandLine(QuestSourceScene, QuestScenePath, QuestProfilePath);

        private static void FromCommandLine(string source, string destination, string profilePath)
        {
            try
            {
                Debug.Log("NPC SKIN BENCH SCENE: " + Build(source, destination, profilePath));
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Makes <paramref name="destination"/> from <paramref name="source"/>; the report in words.</summary>
        public static string Build(string source, string destination, string profilePath)
        {
            if (!File.Exists(source)) throw new FileNotFoundException("the source scene is missing (a licensed scene of this working copy)", source);
            string folder = Path.GetDirectoryName(destination).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(folder))
            {
                string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
                if (!AssetDatabase.IsValidFolder(parent)) throw new DirectoryNotFoundException("the destination's parent folder is missing: " + parent);
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            }

            var report = new List<string> { "source " + source };
            Scene scene = EditorSceneManager.OpenScene(source, OpenSceneMode.Single);

            // What the crowd gives before it goes: its assets (the pose table every slot is configured with).
            MobPlanCrowd[] crowds = Object.FindObjectsByType<MobPlanCrowd>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (crowds.Length != 1) throw new InvalidOperationException("the city scene has " + crowds.Length + " crowds; one is needed for its assets");
            var assets = new SerializedObject(crowds[0]).FindProperty("assets").objectReferenceValue as MobPlanAssets;
            if (assets == null || assets.initialPose == null) throw new InvalidOperationException("the crowd has no assets or no initial pose table");

            // The slots: each holder (the SandboxNpcCharacter) with its character root; one template a family.
            SandboxNpcCharacter[] slots = Object.FindObjectsByType<SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            var keep = new HashSet<GameObject>();
            var templates = new List<(string family, GameObject holder)>();
            foreach (SandboxNpcCharacter npc in slots.OrderBy(s => s.Family, StringComparer.Ordinal))
            {
                if (npc.CharacterRoot == null || string.IsNullOrEmpty(npc.Family) || templates.Any(t => t.family == npc.Family)) continue;
                GameObject holder = npc.gameObject;
                GameObject root = npc.CharacterRoot;
                if (holder.transform.parent != null) holder.transform.SetParent(null, true);
                // The root beneath its holder: one object to instantiate, the holder's references remapped into the copy.
                root.transform.SetParent(holder.transform, true);
                var fields = new SerializedObject(npc);
                fields.FindProperty("hit").objectReferenceValue = null;       // no hit target: no detector reads the benchmark's crowd
                fields.FindProperty("poseLod").objectReferenceValue = null;   // no bone level of detail: every bone is posed by the runner
                fields.ApplyModifiedPropertiesWithoutUndo();
                holder.name = "NPC template " + npc.Family;
                holder.SetActive(false);
                keep.Add(holder);
                templates.Add((npc.Family, holder));
            }

            if (templates.Count == 0) throw new InvalidOperationException("the city scene has no NPC slots");
            report.Add("templates " + templates.Count + " families");

            GameObject[] roots = scene.GetRootGameObjects();
            int removed = 0;
            foreach (GameObject root in roots)
            {
                if (keep.Contains(root)) continue;
                if (Array.IndexOf(k_kept, root.name) >= 0) { keep.Add(root); continue; }
                Object.DestroyImmediate(root);
                removed++;
            }

            report.Add("root objects removed " + removed + ", kept " + keep.Count);
            foreach (string name in k_kept)
                if (!keep.Any(g => g != null && g.name == name)) report.Add("NOT FOUND: " + name);

            // The rig: its locomotion goes; it stands at the origin, facing +Z.
            GameObject rig = keep.FirstOrDefault(g => g.name == "XR Origin");
            if (rig == null) throw new InvalidOperationException("the city scene has no XR Origin");
            foreach (Component c in rig.GetComponents<Component>().ToArray())
            {
                string type = c.GetType().Name;
                if (type == "PlayerLocomotion" || type == "SandboxLocomotionInput") { Object.DestroyImmediate(c); report.Add("rig: removed " + type); }
            }

            rig.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            Camera eye = Camera.main;
            if (eye == null) throw new InvalidOperationException("the scene has no main camera");

            // The world: no hit detector, no probe; its profile copied with the benchmark's skin room.
            GameObject worldObject = keep.FirstOrDefault(g => g.name == "Prop Cut World");
            CutWorldRoot world = worldObject != null ? worldObject.GetComponent<CutWorldRoot>() : null;
            if (world == null) throw new InvalidOperationException("the city scene has no Prop Cut World with a CutWorldRoot");
            foreach (Component c in worldObject.GetComponents<Component>().ToArray())
            {
                string type = c.GetType().Name;
                if (type == "SandboxSlashPropHit" || type == "SandboxCutWorldProbe") { Object.DestroyImmediate(c); report.Add("world: removed " + type); }
            }

            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(profilePath);
            if (profile == null) { profile = Object.Instantiate(world.Profile); AssetDatabase.CreateAsset(profile, profilePath); }
            else EditorUtility.CopySerialized(world.Profile, profile);
            profile.name = Path.GetFileNameWithoutExtension(profilePath);
            var settings = new SerializedObject(profile);
            settings.FindProperty("skinModelCapacity").intValue = SkinModelCapacity;
            settings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(profile);
            var binding = new SerializedObject(world);
            binding.FindProperty("profile").objectReferenceValue = profile;
            binding.ApplyModifiedPropertiesWithoutUndo();
            report.Add("profile " + profilePath + " (skin models " + SkinModelCapacity + ", from " + world.Profile.name + ")");

            // The drawing draws for the eye.
            CutWorldCameraDrawing drawing = worldObject.GetComponent<CutWorldCameraDrawing>();
            if (drawing == null) throw new InvalidOperationException("the world has no CutWorldCameraDrawing");
            var cameras = new SerializedObject(drawing).FindProperty("cameras");
            bool eyeListed = false;
            for (int i = 0; i < cameras.arraySize; i++) eyeListed |= cameras.GetArrayElementAtIndex(i).objectReferenceValue == eye;
            report.Add("drawing cameras " + cameras.arraySize + (eyeListed ? " (the eye among them)" : " (THE EYE IS NOT LISTED)"));

            // The ground: a plane the shadows fall on, under the rows.
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Bench Ground";
            ground.transform.SetPositionAndRotation(new Vector3(0f, 0f, 20f), Quaternion.identity);
            ground.transform.localScale = new Vector3(10f, 1f, 10f);   // 100 x 100 m
            ground.isStatic = true;

            // The runner.
            Light light = keep.Select(g => g.GetComponent<Light>()).FirstOrDefault(l => l != null);
            var runnerObject = new GameObject("NPC Skin Bench");
            var runner = runnerObject.AddComponent<NpcSkinBenchRunner>();
            var fields2 = new SerializedObject(runner);
            fields2.FindProperty("world").objectReferenceValue = world;
            fields2.FindProperty("assets").objectReferenceValue = assets;
            fields2.FindProperty("mainLight").objectReferenceValue = light;
            fields2.FindProperty("rig").objectReferenceValue = rig.transform;
            fields2.FindProperty("eye").objectReferenceValue = eye;
            SerializedProperty list = fields2.FindProperty("templates");
            list.arraySize = templates.Count;
            for (int i = 0; i < templates.Count; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = templates[i].holder;
            fields2.ApplyModifiedPropertiesWithoutUndo();
            report.Add("runner: light " + (light != null ? light.name : "NONE") + ", eye " + eye.name + ", assets " + assets.name + ", clip " + assets.initialPose.name);

            if (!EditorSceneManager.SaveScene(scene, destination)) throw new IOException("the scene could not be saved at " + destination);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            report.Add("saved " + destination);
            return string.Join("; ", report);
        }

        /// <summary>For -executeMethod: lists the source scene's top-level objects and their components, then exits.</summary>
        public static void InventoryFromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                int at = Array.IndexOf(args, "-zantetsuBenchSource");
                string source = at >= 0 && at + 1 < args.Length ? args[at + 1] : PcSourceScene;
                Scene scene = EditorSceneManager.OpenScene(source, OpenSceneMode.Single);
                var text = new StringBuilder("NPC SKIN BENCH INVENTORY of " + source + ": " + scene.rootCount + " root objects\n");
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    text.Append(root.activeSelf ? "[on]  " : "[off] ").Append(root.name).Append(" children ").Append(root.transform.childCount)
                        .Append(" components [").Append(string.Join(", ", root.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name))).Append("]\n");
                }

                Debug.Log(text.ToString());
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }
    }
}
