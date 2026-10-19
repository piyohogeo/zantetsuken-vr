using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// A record, not a check (2026-10-19, the skins' AppSW motion): what the two Quest builds' source scenes have before
    /// the build changes anything -- each skin's motion settings, its material slots by shader, and the VP Mesh Surface
    /// sources a skin shares with another renderer -- and the copies the builds' rule would make of them. Read only:
    /// nothing is saved and no copy is written. Only where the licensed scenes are.
    /// </summary>
    public sealed class QuestSkinMotionScenesProbe
    {
        [TestCase(QuestStaticDemoBuild.SourceScene)]
        [TestCase(QuestNpcSlashBuild.SourceScene)]
        public void TheSourceScenesSkins_BeforeTheBuild_AreRecorded(string scenePath)
        {
            if (!File.Exists(scenePath)) Assert.Ignore("The licensed scene is not installed: " + scenePath);
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var settings = new Dictionary<string, int>();
            var slots = new Dictionary<string, int>();
            var skinSources = new HashSet<Material>();
            var otherSources = new HashSet<Material>();
            var made = new List<Material>();
            var rule = new QuestSurfaceMotionMaterials(string.Empty, "Surface-", "Surface-Skin-");
            int skins = 0;
            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                bool isSkin = renderer is SkinnedMeshRenderer;
                if (renderer is SkinnedMeshRenderer skin)
                {
                    skins++;
                    string key = "mode=" + skin.motionVectorGenerationMode + "/skinnedMotionVectors=" + skin.skinnedMotionVectors + "/updateWhenOffscreen=" + skin.updateWhenOffscreen;
                    settings[key] = settings.TryGetValue(key, out int n) ? n + 1 : 1;
                }

                foreach (Material source in renderer.sharedMaterials)
                {
                    bool surface = source != null && source.shader != null && source.shader.name == QuestSurfaceMotionMaterials.SurfaceShaderName;
                    if (isSkin)
                    {
                        string key = surface ? "VP Mesh Surface" : (source == null ? "(none)" : source.name + " [" + (source.shader == null ? "no shader" : source.shader.name) + "]");
                        slots[key] = slots.TryGetValue(key, out int n) ? n + 1 : 1;
                    }

                    if (!surface) continue;
                    (isSkin ? skinSources : otherSources).Add(source);
                    made.Add(rule.CopyOf(source, QuestSurfaceMotionMaterials.MotionOf(renderer)));
                }
            }

            int shared = skinSources.Count(otherSources.Contains);
            TestContext.Out.WriteLine("SKIN MOTION PROBE " + scenePath + ": skins=" + skins + "; settings: " + QuestSurfaceMotionMaterials.Describe(settings)
                + "; skin slots: " + QuestSurfaceMotionMaterials.Describe(slots)
                + "; VP Mesh Surface sources: skin " + skinSources.Count + ", other " + otherSources.Count + ", shared by both " + shared
                + "; copies by the rule: camera-only " + rule.CameraOnlyCount + ", object " + rule.ObjectCount);
            foreach (Material copy in made.Distinct()) Object.DestroyImmediate(copy);
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
