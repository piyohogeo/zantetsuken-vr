using System;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The application's name and icon as the player shows them (TL 2026-10-18): the display name of the store listing,
    /// WORLD CUTTER -斬鉄剣-, and the 斬 icon (a 512 px copy of the TL's artwork). Kept in ProjectSettings as the default
    /// product name and the default icon, which every platform uses where it has no icon of its own (no Android override).
    /// <see cref="Apply"/> writes them once; the settings file carries them from then on.
    /// </summary>
    public static class QuestAppIdentity
    {
        public const string ProductName = "WORLD CUTTER -斬鉄剣-";
        public const string IconPath = "Assets/Zantetsu/Branding/world_cutter_icon_512.png";

        /// <summary>Sets the product name and the default icon, and saves the project settings (Unity: -executeMethod).</summary>
        public static void Apply()
        {
            AssetDatabase.ImportAsset(IconPath, ImportAssetOptions.ForceSynchronousImport);
            var icon = AssetDatabase.LoadAssetAtPath<Texture2D>(IconPath);
            if (icon == null)
            {
                Debug.LogError("QUEST APP IDENTITY: no icon texture at " + IconPath);
                if (Application.isBatchMode) EditorApplication.Exit(1);
                return;
            }

            PlayerSettings.productName = ProductName;
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);
            AssetDatabase.SaveAssets();
            Debug.Log("QUEST APP IDENTITY: product name and default icon set (" + IconPath + ")");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
    }
}
