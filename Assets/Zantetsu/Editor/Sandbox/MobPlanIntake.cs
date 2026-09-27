using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.Animation;

namespace Zantetsu.Sandbox.Editor
{
    /// <summary>Extracts the rig's required paths from real skin weights, hulls and ancestors. Licensed output stays ignored.</summary>
    public static class MobPlanIntake
    {
        [Serializable] private sealed class Intake { public Entry[] assets; }
        [Serializable] private sealed class Entry { public string family; public string assetPath; public string objectName; }
        [Serializable] private sealed class Hulls { public Hull[] hulls; }
        [Serializable] private sealed class Hull { public string boneName; }

        public static void ExportRequiredBones()
        {
            GameObject model = null;
            try
            {
                const string source = "Assets/Licensed/Compact16uvConvexRepair/";
                Entry entry = JsonUtility.FromJson<Intake>(File.ReadAllText(source + "intake.json")).assets.Single(e => e.family == "character-casual");
                var hulls = JsonUtility.FromJson<Hulls>(File.ReadAllText(source + "Resources/CharacterPhysicsMigration/character-casual.json"));
                model = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath));
                var skin = model.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh != null && r.sharedMesh.name == entry.objectName);
                if (!PoseTable.TryRead(File.ReadAllBytes("Assets/Licensed/U8Npc/AS_Fast_WalkCycle_1.bytes"), out var table, out var error)) throw new InvalidOperationException(error);
                var needed = new HashSet<Transform>();
                void Add(Transform t) { for (; t != null && t != model.transform; t = t.parent) needed.Add(t); }
                using (var weights = skin.sharedMesh.GetAllBoneWeights())
                    foreach (var weight in weights) if (weight.weight > 0) Add(skin.bones[weight.boneIndex]);
                foreach (var hull in hulls.hulls) Add(skin.bones.First(b => b != null && b.name == hull.boneName));
                Add(skin.transform); Add(skin.rootBone);
                var tableBones = new HashSet<Transform>(Enumerable.Range(0, table.BoneCount).Select(i => model.transform.Find(table.BonePath(i))));
                foreach (Component c in model.GetComponentsInChildren<Component>(true))
                    if (!(c is Transform) && !tableBones.Contains(c.transform)) Add(c.transform.parent);
                string[] paths = Enumerable.Range(0, table.BoneCount).Where(i => needed.Contains(model.transform.Find(table.BonePath(i)))).Select(table.BonePath).ToArray();
                if (paths.Length != 66) throw new InvalidOperationException("Expected the audited 66 bones, found " + paths.Length);
                Directory.CreateDirectory("Assets/Licensed/MobPlan");
                File.WriteAllLines("Assets/Licensed/MobPlan/required-bones.txt", paths);
                Debug.Log("MOBPLAN required paths: " + paths.Length);
                EditorApplication.Exit(0);
            }
            catch (Exception e) { Debug.LogException(e); EditorApplication.Exit(1); }
            finally { if (model != null) UnityEngine.Object.DestroyImmediate(model); }
        }
    }
}
