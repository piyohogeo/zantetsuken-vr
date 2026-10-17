using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The distribution's devices (D-240): one definition (quest3|quest3s), the package's internal name for Quest 3
    /// (eureka) understood, values split and checked strictly, and the Meta Quest feature's target devices set to
    /// the two for a build and put back -- after a normal end and after an exception -- in this project's own
    /// Android OpenXR settings.
    /// </summary>
    public sealed class QuestDistributionTargetsTests
    {
        [Test]
        public void TheDefinition_IsQuest3AndQuest3S_WithThePackagesNameForQuest3()
        {
            Assert.That(QuestDistributionTargets.CanonicalValue, Is.EqualTo("quest3|quest3s"));
            Assert.That(Array.ConvertAll(QuestDistributionTargets.Devices, d => d.internalName), Is.EqualTo(new[] { "eureka", "quest3s" }));
            Assert.That(QuestDistributionTargets.Canonical("eureka"), Is.EqualTo("quest3"));
            Assert.That(QuestDistributionTargets.Canonical("quest3s"), Is.EqualTo("quest3s"));
            Assert.That(QuestDistributionTargets.Canonical("cambria"), Is.EqualTo("questpro"));
            Assert.That(QuestDistributionTargets.Canonical("Eureka"), Is.EqualTo("Eureka"), "case-sensitive: not the known name");
            Assert.That(QuestDistributionTargets.MetaDataName, Is.EqualTo("com.oculus.supportedDevices"));
        }

        [Test]
        public void AValue_IsSplitStrictly_AndCheckedAgainstTheDefinition_InAnyOrder()
        {
            Assert.That(QuestDistributionTargets.Split("quest3|quest3s", out List<string> names), Is.Null);
            Assert.That(names, Is.EqualTo(new[] { "quest3", "quest3s" }));
            Assert.That(QuestDistributionTargets.Split("", out names), Does.Contain("empty"));
            Assert.That(QuestDistributionTargets.Split(null, out names), Does.Contain("empty"));
            Assert.That(QuestDistributionTargets.Split("quest3|quest3s|", out names), Does.Contain("empty part"));
            Assert.That(QuestDistributionTargets.Split("quest3||quest3s", out names), Does.Contain("empty part"));
            Assert.That(QuestDistributionTargets.Split("quest3|quest3", out names), Does.Contain("repeats"));
            Assert.That(QuestDistributionTargets.Check(new[] { "quest3", "quest3s" }), Is.Null);
            Assert.That(QuestDistributionTargets.Check(new[] { "quest3s", "quest3" }), Is.Null, "order free");
            Assert.That(QuestDistributionTargets.Check(new[] { "eureka", "quest3s" }), Is.Null, "the package's name understood");
            Assert.That(QuestDistributionTargets.Check(new[] { "quest3s" }), Does.Contain("missing [quest3]"));
            Assert.That(QuestDistributionTargets.Check(new[] { "quest3", "quest3s", "quest2" }), Does.Contain("extra or unknown [quest2]"));
            Assert.That(QuestDistributionTargets.Check(new[] { "quest3", "quest3s", "cambria" }), Does.Contain("[questpro]"));
            Assert.That(QuestDistributionTargets.Check(new[] { "Quest3", "quest3s" }), Does.Contain("missing [quest3]").And.Contain("[Quest3]"), "case matters");
            Assert.That(QuestDistributionTargets.Check(new[] { "eureka", "quest3", "quest3s" }), Does.Contain("twice"), "eureka and quest3 are the same device");
            Assert.That(QuestDistributionTargets.Check(new[] { "quest3", "quest3s", "nosuch" }), Does.Contain("[nosuch]"));
        }

        [Test]
        public void TheFeaturesTargetDevices_AreTheTwoDuringTheScope_AndAsBeforeAfterIt_AlsoAfterAnException()
        {
            // The feature must be enabled for Android: where this project (a verification tree) has it off, it is
            // switched on for the test and back after; the refusal of a disabled feature is checked on the way.
            Assert.That(QuestDistributionTargets.TryGetFeatureEnabled(out bool featureEnabledBefore), Is.True, "the Meta Quest feature in the Android OpenXR settings");
            if (!featureEnabledBefore)
            {
                Assert.That(QuestDistributionTargets.FindFeatureDevices(out _, out string disabled), Is.Null);
                Assert.That(disabled, Does.Contain("not enabled"), "a disabled feature is refused with the reason");
                Assert.Throws<BuildFailedException>(() => new QuestDistributionTargets.Scope(), "and the scope refuses before changing anything");
                QuestDistributionTargets.SetFeatureEnabled(true);
            }
            SerializedObject feature = null;
            SerializedProperty devices = null;
            var before = new List<bool>();
            try
            {
                feature = QuestDistributionTargets.FindFeatureDevices(out devices, out string failure);
                Assert.That(feature, Is.Not.Null, "the Meta Quest feature with the two devices: " + failure);
                for (int i = 0; i < devices.arraySize; i++) before.Add(devices.GetArrayElementAtIndex(i).FindPropertyRelative("enabled").boolValue);
                // Before: every device enabled (the project's state; set so explicitly for the test).
                for (int i = 0; i < devices.arraySize; i++) devices.GetArrayElementAtIndex(i).FindPropertyRelative("enabled").boolValue = true;
                feature.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(Enabled(), Is.EqualTo(All(true)), "all on before");
                using (var scope = new QuestDistributionTargets.Scope())
                {
                    Dictionary<string, bool> during = Enabled();
                    Assert.That(during["eureka"], Is.True);
                    Assert.That(during["quest3s"], Is.True);
                    foreach (KeyValuePair<string, bool> d in during)
                        if (d.Key != "eureka" && d.Key != "quest3s") Assert.That(d.Value, Is.False, d.Key + " off during the build");
                    Assert.That(scope.During, Does.Contain("eureka=on").And.Contain("quest3s=on").And.Contain("quest2=off"));
                    Assert.That(Names(), Is.EqualTo(NamesBefore(devices)), "the names and their order are untouched");
                }
                Assert.That(Enabled(), Is.EqualTo(All(true)), "all on again after a normal end");
                Assert.Throws<InvalidOperationException>(() => { using (new QuestDistributionTargets.Scope()) throw new InvalidOperationException("the build failed"); });
                Assert.That(Enabled(), Is.EqualTo(All(true)), "all on again after an exception");
            }
            finally
            {
                if (feature != null)
                {
                    feature.Update();
                    SerializedProperty list = feature.FindProperty("targetDevices");
                    for (int i = 0; i < list.arraySize && i < before.Count; i++) list.GetArrayElementAtIndex(i).FindPropertyRelative("enabled").boolValue = before[i];
                    feature.ApplyModifiedPropertiesWithoutUndo();
                }
                QuestDistributionTargets.SetFeatureEnabled(featureEnabledBefore);
            }

            Dictionary<string, bool> Enabled()
            {
                var so = QuestDistributionTargets.FindFeatureDevices(out SerializedProperty list, out _);
                var map = new Dictionary<string, bool>();
                for (int i = 0; i < list.arraySize; i++) map[list.GetArrayElementAtIndex(i).FindPropertyRelative("manifestName").stringValue] = list.GetArrayElementAtIndex(i).FindPropertyRelative("enabled").boolValue;
                return map;
            }
            Dictionary<string, bool> All(bool value)
            {
                var map = new Dictionary<string, bool>();
                foreach (string n in NamesBefore(devices)) map[n] = value;
                return map;
            }
            List<string> Names()
            {
                QuestDistributionTargets.FindFeatureDevices(out SerializedProperty list, out _);
                return NamesBefore(list);
            }
        }

        private static List<string> NamesBefore(SerializedProperty list)
        {
            var names = new List<string>();
            for (int i = 0; i < list.arraySize; i++) names.Add(list.GetArrayElementAtIndex(i).FindPropertyRelative("manifestName").stringValue);
            return names;
        }
    }
}
