using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The GPU bake probe's scene (DESIGN 4.5.14, skin-bake-probe): the benchmark's stripping of the city scene
    /// (<see cref="NpcSkinBenchSceneBuild.Build"/>: the rig and its camera, the light, the cut world, the pipeline and
    /// volume, the slot templates, a ground plane) with the probe's runner in place of the benchmark's, given the same
    /// references. The PC scene comes from the licensed WalkCity scene, the Quest scene from the configured QuestCityWalk
    /// scene.
    /// </summary>
    public static class SkinBakeProbeSceneBuild
    {
        public const string PcScenePath = "Assets/Licensed/SkinBakeProbe/SkinBakeProbe.unity";
        public const string PcProfilePath = "Assets/Licensed/SkinBakeProbe/SkinBakeProbeProfile.asset";
        public const string QuestScenePath = "Assets/Licensed/Quest3s/QuestSkinBakeProbe.unity";
        public const string QuestProfilePath = "Assets/Licensed/Quest3s/QuestSkinBakeProbeProfile.asset";

        /// <summary>For -executeMethod: the PC probe scene from the licensed city scene, then exits.</summary>
        public static void BuildPcFromCommandLine() => FromCommandLine(NpcSkinBenchSceneBuild.PcSourceScene, PcScenePath, PcProfilePath);

        /// <summary>For -executeMethod: the Quest probe scene from the configured Quest city-walk scene, then exits.</summary>
        public static void BuildQuestFromCommandLine() => FromCommandLine(NpcSkinBenchSceneBuild.QuestSourceScene, QuestScenePath, QuestProfilePath);

        private static void FromCommandLine(string source, string destination, string profilePath)
        {
            try
            {
                Debug.Log("SKIN BAKE PROBE SCENE: " + Build(source, destination, profilePath));
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>Makes <paramref name="destination"/> from <paramref name="source"/> by the benchmark's stripping, then swaps the runner; the report in words.</summary>
        public static string Build(string source, string destination, string profilePath)
        {
            string report = NpcSkinBenchSceneBuild.Build(source, destination, profilePath);
            Scene scene = EditorSceneManager.OpenScene(destination, OpenSceneMode.Single);
            NpcSkinBenchRunner bench = Object.FindObjectsByType<NpcSkinBenchRunner>(FindObjectsInactive.Include, FindObjectsSortMode.None).FirstOrDefault();
            if (bench == null) throw new InvalidOperationException("the stripped scene has no benchmark runner to take the references from");
            var from = new SerializedObject(bench);
            var runnerObject = new GameObject("Skin Bake Probe");
            var runner = runnerObject.AddComponent<SkinBakeProbeRunner>();
            var to = new SerializedObject(runner);
            foreach (string field in new[] { "world", "assets", "mainLight", "rig", "eye" }) to.FindProperty(field).objectReferenceValue = from.FindProperty(field).objectReferenceValue;
            SerializedProperty templatesFrom = from.FindProperty("templates"), templatesTo = to.FindProperty("templates");
            templatesTo.arraySize = templatesFrom.arraySize;
            for (int i = 0; i < templatesFrom.arraySize; i++) templatesTo.GetArrayElementAtIndex(i).objectReferenceValue = templatesFrom.GetArrayElementAtIndex(i).objectReferenceValue;
            to.ApplyModifiedPropertiesWithoutUndo();
            Object.DestroyImmediate(bench.gameObject);
            if (!EditorSceneManager.SaveScene(scene, destination)) throw new IOException("the scene could not be saved at " + destination);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return report + "; runner swapped for the skin bake probe's (templates " + templatesTo.arraySize + "); saved " + destination;
        }
    }
}
