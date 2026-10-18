using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The application's name and icon in the project settings (TL 2026-10-18): the store listing's display name and the
    /// 斬 icon, as the default product name and default icon (no Android override), so every build shows them.
    /// </summary>
    public sealed class QuestAppIdentityTests
    {
        [Test]
        public void TheProductName_IsTheStoreListingsDisplayName()
        {
            Assert.That(PlayerSettings.productName, Is.EqualTo("WORLD CUTTER -斬鉄剣-"));
            Assert.That(QuestAppIdentity.ProductName, Is.EqualTo(PlayerSettings.productName));
        }

        [Test]
        public void TheDefaultIcon_IsTheBrandingIcon_And_AndroidHasNoOverride()
        {
            Texture2D[] icons = PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any);
            Assert.That(icons, Is.Not.Null.And.Not.Empty, "a default icon is set");
            Assert.That(AssetDatabase.GetAssetPath(icons[0]), Is.EqualTo(QuestAppIdentity.IconPath));
            Assert.That(icons[0].width, Is.EqualTo(512));
            Texture2D[] android = PlayerSettings.GetIcons(NamedBuildTarget.Android, IconKind.Any);
            Assert.That(android == null || android.Length == 0 || android[0] == null || android[0] == icons[0], Is.True,
                "Android has no icon of its own (it uses the default)");
        }
    }
}
