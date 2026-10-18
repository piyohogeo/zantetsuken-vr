using NUnit.Framework;
using UnityEditor;
using UnityEngine.XR.OpenXR;
using Zantetsu.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The project's own OpenXR feature in the Android settings the Quest builds read (unit 11, 2026-10-18: its id
    /// changed to the public one): there, enabled, and held under the id its attribute gives -- a feature whose stored id
    /// differs from its type's would not be the one the build enables.
    /// </summary>
    public sealed class QuestOpenXrFeatureSettingsTests
    {
        [Test]
        public void TheDisplayRefreshRateFeature_IsInTheAndroidSettings_EnabledUnderItsPublicId()
        {
            Assert.That(QuestDisplayRefreshRateFeature.featureId, Is.EqualTo("com.zantetsuken.quest.displayrefreshrate"));
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            Assert.That(settings, Is.Not.Null, "the Android OpenXR settings");
            var feature = settings.GetFeature<QuestDisplayRefreshRateFeature>();
            Assert.That(feature, Is.Not.Null, "the feature is in the Android settings");
            Assert.That(feature.enabled, Is.True, "and enabled");
            SerializedProperty id = new SerializedObject(feature).FindProperty("featureIdInternal");
            Assert.That(id, Is.Not.Null);
            Assert.That(id.stringValue, Is.EqualTo(QuestDisplayRefreshRateFeature.featureId), "stored under the id of its type");
        }
    }
}
