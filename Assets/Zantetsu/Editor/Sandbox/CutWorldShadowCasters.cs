using System;
using UnityEditor;
using UnityEngine;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The two shadow casters a cut world's display draws with (DESIGN 5.4): Stable bodies one-sided (Cull Back) and
    /// bodies still clipped immediately two-sided, without a cap (Cull Off). Both are materials of the existing
    /// "Zantetsu/VP Indexed Indirect Shadow Caster" and live in the project's settings. The scene builders that make the
    /// shared Sandbox scene, the NPC scene and the representative scenes set them on the world here, explicitly; nothing
    /// looks them up at run time.
    /// </summary>
    public static class CutWorldShadowCasters
    {
        public const string StablePath = "Assets/Zantetsu/Settings/CutWorldShadowCaster.mat";
        public const string ImmediatePath = "Assets/Zantetsu/Settings/CutWorldImmediateShadowCaster.mat";

        /// <summary>Sets both casters on a world's serialized component; the caller applies the change.</summary>
        public static void Assign(SerializedObject world)
        {
            world.FindProperty("shadowMaterial").objectReferenceValue = Load(StablePath);
            world.FindProperty("provisionalShadowMaterial").objectReferenceValue = Load(ImmediatePath);
        }

        private static Material Load(string path)
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                throw new InvalidOperationException("The cut world's shadow caster is missing: " + path);
            }

            return material;
        }
    }
}
