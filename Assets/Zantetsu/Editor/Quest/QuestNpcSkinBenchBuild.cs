using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Zantetsu.EditorTools.Sandbox;
using Zantetsu.Quest;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The Quest's APK of the NPC skin benchmark (DESIGN 3.4, npc-skin-bench): the benchmark scene made from the
    /// configured Quest city-walk scene (its materials, pipeline and profile), built alone into an APK of its own
    /// package, as the static demo's is. The city-walk scene must have been configured first (QuestCityWalkBuild.Configure).
    /// </summary>
    public static class QuestNpcSkinBenchBuild
    {
        public static void Configure()
        {
            try
            {
                var report = new List<string>();
                QuestStaticDemoBuild.ConfigureCommon(report);
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, QuestNpcSkinBench.PackageName);
                if (!File.Exists(NpcSkinBenchSceneBuild.QuestSourceScene)) throw new FileNotFoundException("Configure the Quest city walk first (QuestCityWalkBuild.Configure)", NpcSkinBenchSceneBuild.QuestSourceScene);
                report.Add(NpcSkinBenchSceneBuild.Build(NpcSkinBenchSceneBuild.QuestSourceScene, NpcSkinBenchSceneBuild.QuestScenePath, NpcSkinBenchSceneBuild.QuestProfilePath));
                AssetDatabase.SaveAssets();
                Debug.Log("QUEST NPC SKIN BENCH CONFIGURE: " + string.Join("; ", report));
            }
            catch (Exception e) { Debug.LogError(e); if (Application.isBatchMode) EditorApplication.Exit(1); throw; }
        }

        public static void BuildApk()
        {
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, QuestNpcSkinBench.PackageName);
            QuestStaticDemoBuild.BuildApkForScene(NpcSkinBenchSceneBuild.QuestScenePath);
        }
    }
}
