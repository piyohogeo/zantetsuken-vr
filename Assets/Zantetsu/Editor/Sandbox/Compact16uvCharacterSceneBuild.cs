using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    public static class Compact16uvCharacterSceneBuild
    {
        static bool FixedScale => Environment.GetEnvironmentVariable("VP_CHARACTER_FIXED_SCALE")=="1";
        static string Root => FixedScale ? "Assets/Licensed/Compact16uvConvexRepair" : "Assets/Licensed/Compact16uvIntake";
        static string Scene => Root + "/SceneIntegration/Compact16uvCurrentPose.unity";
        public static void BuildPlayer()
        {
            string output = Environment.GetEnvironmentVariable("VP_CHARACTER_PLAYER_OUT");
            if (string.IsNullOrEmpty(output)) throw new Exception("Set VP_CHARACTER_PLAYER_OUT");
            var manifest = JsonUtility.FromJson<Input>(File.ReadAllText(Root + "/intake.json"));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory(Root+"/SceneIntegration"); AssetDatabase.Refresh();
            var probe = new GameObject("Current pose scene diagnostic (no physics owner)").AddComponent<Compact16uvCharacterSceneProbe>();
            probe.normalizeFixedScale=FixedScale;
            probe.intake = AssetDatabase.LoadAssetAtPath<TextAsset>(Root + "/intake.json");
            probe.models = new[] { "character-casual", "character-professional" }.Select(f => {
                var entry = manifest.assets.Single(e => e.family == f);
                using var hash = SHA256.Create();
                string actual = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(entry.assetPath))).Replace("-", "").ToLowerInvariant();
                if (actual != entry.sourceSha256) throw new Exception("Pinned source changed");
                return AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath);
            }).ToArray();
            probe.animationClips=new[] { "character-casual", "character-professional" }.Select(f=> {
                var entry=manifest.assets.Single(e=>e.family==f);
                var clips=AssetDatabase.LoadAllAssetsAtPath(entry.assetPath).OfType<AnimationClip>()
                    .Where(c=>!c.name.StartsWith("__preview__")).ToArray();
                foreach (var clip in clips) Debug.Log($"FIXED SCALE CLIP family={f} name={clip.name} length={clip.length:R} curves={AnimationUtility.GetCurveBindings(clip).Length}");
                Debug.Log($"FIXED SCALE CLIP INVENTORY family={f} count={clips.Length}");
                return clips.FirstOrDefault(c=>c.length>0 && AnimationUtility.GetCurveBindings(c).Length>0);
            }).ToArray();
            probe.normalAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas/Normal.png");
            probe.debugAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas/Debug.png");
            probe.meshMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Licensed/Compact16uvIntake/Resources/AppearanceMigration/SourcePalette.mat");
            string path = Root + "/SceneIntegration/CharacterForward.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null) { material = new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")); AssetDatabase.CreateAsset(material, path); }
            material.SetFloat("_VpUsePaletteAtlas", 1); material.SetColor("_BaseColor", Color.white); material.SetTexture("_BaseMap", probe.normalAtlas);
            EditorUtility.SetDirty(material); probe.vpMaterial = material;
            if (probe.models.Any(x => x == null) || probe.meshMaterial == null || probe.normalAtlas == null || probe.debugAtlas == null) throw new Exception("Prepare private intake/appearance/atlas resources first");
            if (!EditorSceneManager.SaveScene(scene, Scene)) throw new Exception("Private scene save failed");
            var xr = UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Standalone);
            bool previous = xr.InitManagerOnStart;
            bool success;
            try { xr.InitManagerOnStart = false; EditorUtility.SetDirty(xr); AssetDatabase.SaveAssets(); success = CutWorldSandboxPlayerBuild.Build(output, Scene); }
            finally { xr.InitManagerOnStart = previous; EditorUtility.SetDirty(xr); AssetDatabase.SaveAssets(); }
            EditorApplication.Exit(success ? 0 : 1);
        }
        [Serializable] sealed class Input { public Entry[] assets; }
        [Serializable] sealed class Entry { public string family, assetPath, sourceSha256; }
    }
}
