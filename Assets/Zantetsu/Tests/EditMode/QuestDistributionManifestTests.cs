using System.Collections.Generic;
using System.Xml;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The manifest guarantees of a distribution build (DESIGN 3.5, D-239) applied to a generated unityLibrary
    /// manifest: what is missing is added, what holds is left, and the launch activity is the one with MAIN and must
    /// be UnityPlayerGameActivity. Also the settings scope's SDK levels and install location (applied and put back).
    /// </summary>
    public sealed class QuestDistributionManifestTests
    {
        private const string A = QuestDistributionManifest.AndroidNamespace;

        private static string Manifest(string installLocation = " android:installLocation=\"preferExternal\"", string headTracking = "<uses-feature android:name=\"android.hardware.vr.headtracking\" android:required=\"true\" android:version=\"1\" />",
            string launchName = QuestDistributionManifest.LaunchActivity, string exclude = "", string vrCategory = "<category android:name=\"com.oculus.intent.category.VR\" />", string moreActivities = "")
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<manifest xmlns:android=\"" + A + "\"" + installLocation + ">\n  <uses-permission android:name=\"android.permission.INTERNET\" />\n  " + headTracking + "\n"
                + "  <application android:label=\"x\">\n    <meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"quest3s\" />\n"
                + "    <activity android:name=\"" + launchName + "\"" + exclude + ">\n      <intent-filter>\n        <category android:name=\"android.intent.category.LAUNCHER\" />\n        <action android:name=\"android.intent.action.MAIN\" />\n        " + vrCategory + "\n      </intent-filter>\n    </activity>\n"
                + moreActivities + "  </application>\n</manifest>\n";
        }

        private static XmlDocument Load(string xml) { var d = new XmlDocument(); d.LoadXml(xml); return d; }
        private static XmlElement Launch(XmlDocument d) => (XmlElement)d.DocumentElement.SelectSingleNode("application/activity[@android:name='" + QuestDistributionManifest.LaunchActivity + "']", Ns(d));
        private static XmlNamespaceManager Ns(XmlDocument d) { var ns = new XmlNamespaceManager(d.NameTable); ns.AddNamespace("android", A); return ns; }

        [Test]
        public void WhatIsMissing_IsAdded_OnTheRealLaunchActivity_AndWhatHolds_IsLeft()
        {
            XmlDocument d = Load(Manifest());
            List<string> changes = QuestDistributionManifest.Apply(d);
            Assert.That(d.DocumentElement.GetAttribute("installLocation", A), Is.EqualTo("auto"), "preferExternal -> auto");
            Assert.That(Launch(d).GetAttribute("excludeFromRecents", A), Is.EqualTo("true"));
            Assert.That(d.DocumentElement.SelectNodes("uses-feature[@android:name='android.hardware.vr.headtracking']", Ns(d)).Count, Is.EqualTo(1), "the feature that held is not duplicated");
            Assert.That(Launch(d).SelectNodes("intent-filter/category[@android:name='com.oculus.intent.category.VR']", Ns(d)).Count, Is.EqualTo(1), "the category that held is not duplicated");
            Assert.That(changes, Is.EqualTo(new[] { "manifest android:installLocation=auto", "activity " + QuestDistributionManifest.LaunchActivity + " android:excludeFromRecents=true" }));
            // Applied again: nothing to set.
            Assert.That(QuestDistributionManifest.Apply(d), Is.Empty);
        }

        [Test]
        public void TheHeadTrackingFeature_AndTheVrCategory_AreAddedWhenMissing_AndTheFeaturesValuesAreSet()
        {
            XmlDocument d = Load(Manifest(installLocation: " android:installLocation=\"auto\"", headTracking: "", vrCategory: "", exclude: " android:excludeFromRecents=\"true\""));
            List<string> changes = QuestDistributionManifest.Apply(d);
            XmlNodeList features = d.DocumentElement.SelectNodes("uses-feature[@android:name='android.hardware.vr.headtracking']", Ns(d));
            Assert.That(features.Count, Is.EqualTo(1));
            Assert.That(((XmlElement)features[0]).GetAttribute("required", A), Is.EqualTo("true"));
            Assert.That(((XmlElement)features[0]).GetAttribute("version", A), Is.EqualTo("1"));
            Assert.That(features[0].NextSibling.Name, Is.EqualTo("application"), "inserted before <application>, as a manifest child");
            Assert.That(Launch(d).SelectNodes("intent-filter/category[@android:name='com.oculus.intent.category.VR']", Ns(d)).Count, Is.EqualTo(1));
            Assert.That(changes, Does.Contain("launch intent-filter category com.oculus.intent.category.VR").And.Contain("uses-feature android.hardware.vr.headtracking"));
            Assert.That(changes, Does.Not.Contain("manifest android:installLocation=auto"), "auto held");
            // A feature that is there with the wrong values: set, not duplicated.
            d = Load(Manifest(headTracking: "<uses-feature android:name=\"android.hardware.vr.headtracking\" android:required=\"false\" />"));
            changes = QuestDistributionManifest.Apply(d);
            features = d.DocumentElement.SelectNodes("uses-feature[@android:name='android.hardware.vr.headtracking']", Ns(d));
            Assert.That(features.Count, Is.EqualTo(1));
            Assert.That(((XmlElement)features[0]).GetAttribute("required", A), Is.EqualTo("true"));
            Assert.That(((XmlElement)features[0]).GetAttribute("version", A), Is.EqualTo("1"));
        }

        [Test]
        public void TheLaunchActivity_MustBeTheOneWithMain_AndBeUnityPlayerGameActivity_NeverAnother()
        {
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(launchName: "com.example.OtherActivity"))), "MAIN on another activity: refused");
            const string second = "    <activity android:name=\"com.example.Second\">\n      <intent-filter>\n        <action android:name=\"android.intent.action.MAIN\" />\n      </intent-filter>\n    </activity>\n";
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(moreActivities: second))), "two activities with MAIN: refused");
            // An activity without MAIN beside the launch activity: left alone, the attribute goes on the launch activity.
            const string other = "    <activity android:name=\"com.example.Other\" android:excludeFromRecents=\"true\" />\n";
            XmlDocument d = Load(Manifest(moreActivities: other));
            QuestDistributionManifest.Apply(d);
            Assert.That(Launch(d).GetAttribute("excludeFromRecents", A), Is.EqualTo("true"));
            Assert.That(((XmlElement)d.DocumentElement.SelectSingleNode("application/activity[@android:name='com.example.Other']", Ns(d))).SelectNodes("intent-filter").Count, Is.EqualTo(0), "the other activity gained nothing");
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load("<manifest xmlns:android=\"" + A + "\"><application /></manifest>")), "no activity with MAIN: refused");
        }

        [Test]
        public void TheSettingsScope_FixesTheSdkLevelsAndTheInstallLocation_AndPutsThemBack_AlsoAfterAnException()
        {
            AndroidSdkVersions min = PlayerSettings.Android.minSdkVersion, target = PlayerSettings.Android.targetSdkVersion;
            AndroidPreferredInstallLocation location = PlayerSettings.Android.preferredInstallLocation;
            try
            {
                // Before: values that differ from the distribution's.
                PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevel36;
                PlayerSettings.Android.preferredInstallLocation = AndroidPreferredInstallLocation.PreferExternal;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel33;
                Assert.That(QuestDistributionBuild.TryRead(new[] { "Unity.exe", QuestDistributionBuild.VersionNameArgument, "0.2.0", QuestDistributionBuild.VersionCodeArgument, "3",
                    QuestDistributionBuild.KeystoreArgument, "C:/keys/a.keystore", QuestDistributionBuild.KeyAliasArgument, "a", QuestDistributionBuild.OutputArgument, "C:/out/a.apk" }, out var r), Is.Null);
                using (new QuestDistributionBuild.SettingsScope(r, "s", "k"))
                {
                    Assert.That(PlayerSettings.Android.minSdkVersion, Is.EqualTo(AndroidSdkVersions.AndroidApiLevel32));
                    Assert.That(PlayerSettings.Android.targetSdkVersion, Is.EqualTo(AndroidSdkVersions.AndroidApiLevel34), "named, not the automatic level");
                    Assert.That(PlayerSettings.Android.preferredInstallLocation, Is.EqualTo(AndroidPreferredInstallLocation.Auto));
                }
                Assert.That(PlayerSettings.Android.minSdkVersion, Is.EqualTo(AndroidSdkVersions.AndroidApiLevel33), "put back after a normal end");
                Assert.That(PlayerSettings.Android.targetSdkVersion, Is.EqualTo(AndroidSdkVersions.AndroidApiLevel36));
                Assert.That(PlayerSettings.Android.preferredInstallLocation, Is.EqualTo(AndroidPreferredInstallLocation.PreferExternal));
                Assert.Throws<System.InvalidOperationException>(() => { using (new QuestDistributionBuild.SettingsScope(r, "s", "k")) throw new System.InvalidOperationException("the build failed"); });
                Assert.That(PlayerSettings.Android.minSdkVersion, Is.EqualTo(AndroidSdkVersions.AndroidApiLevel33), "put back after an exception");
                Assert.That(PlayerSettings.Android.targetSdkVersion, Is.EqualTo(AndroidSdkVersions.AndroidApiLevel36));
                Assert.That(PlayerSettings.Android.preferredInstallLocation, Is.EqualTo(AndroidPreferredInstallLocation.PreferExternal));
                Assert.That(PlayerSettings.Android.keystorePass, Is.Empty);
            }
            finally
            {
                PlayerSettings.Android.minSdkVersion = min;
                PlayerSettings.Android.targetSdkVersion = target;
                PlayerSettings.Android.preferredInstallLocation = location;
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
            }
        }
    }
}
