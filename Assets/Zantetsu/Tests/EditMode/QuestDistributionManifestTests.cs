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

        private const string Devices = "    <meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"eureka|quest3s\" />\n";

        private static string Manifest(string installLocation = " android:installLocation=\"preferExternal\"", string headTracking = "<uses-feature android:name=\"android.hardware.vr.headtracking\" android:required=\"true\" android:version=\"1\" />",
            string launchName = QuestDistributionManifest.LaunchActivity, string exclude = "", string vrCategory = "<category android:name=\"com.oculus.intent.category.VR\" />", string moreActivities = "", string devices = Devices, string activityMetaData = "")
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<manifest xmlns:android=\"" + A + "\"" + installLocation + ">\n  <uses-permission android:name=\"android.permission.INTERNET\" />\n  " + headTracking + "\n"
                + "  <application android:label=\"x\">\n    <meta-data android:name=\"unity.splash-mode\" android:value=\"0\" />\n" + devices
                + "    <activity android:name=\"" + launchName + "\"" + exclude + ">\n      <intent-filter>\n        <category android:name=\"android.intent.category.LAUNCHER\" />\n        <action android:name=\"android.intent.action.MAIN\" />\n        " + vrCategory + "\n      </intent-filter>\n" + activityMetaData + "    </activity>\n"
                + moreActivities + "  </application>\n</manifest>\n";
        }

        private static string DevicesValue(XmlDocument d) => ((XmlElement)d.DocumentElement.SelectSingleNode("application/meta-data[@android:name='com.oculus.supportedDevices']", Ns(d))).GetAttribute("value", A);

        [Test]
        public void TheSupportedDevices_AreWrittenCanonically_OnceVerified_AndTheOtherMetaDataStays()
        {
            XmlDocument d = Load(Manifest());
            List<string> changes = QuestDistributionManifest.Apply(d);
            Assert.That(DevicesValue(d), Is.EqualTo("quest3|quest3s"), "eureka (the package's Quest 3) -> quest3");
            Assert.That(changes, Does.Contain("meta-data com.oculus.supportedDevices=quest3|quest3s (was eureka|quest3s)"));
            Assert.That(QuestDistributionManifest.Apply(d), Is.Empty, "applied again: nothing changes");
            Assert.That(DevicesValue(d), Is.EqualTo("quest3|quest3s"));
            Assert.That(d.DocumentElement.SelectNodes("application/meta-data", Ns(d)).Count, Is.EqualTo(2), "the other meta-data is kept");
            Assert.That(((XmlElement)d.DocumentElement.SelectSingleNode("application/meta-data[@android:name='unity.splash-mode']", Ns(d))).GetAttribute("value", A), Is.EqualTo("0"));
            // The canonical names already, in the other order: accepted and written in the distribution's order.
            d = Load(Manifest(devices: "    <meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"quest3s|quest3\" />\n"));
            QuestDistributionManifest.Apply(d);
            Assert.That(DevicesValue(d), Is.EqualTo("quest3|quest3s"));
        }

        private const string XrLibrary = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<manifest xmlns:android=\"" + A + "\" package=\"com.UnityTechnologies.XR.Manifest\">\n  <application>\n    <activity android:name=\"com.unity3d.player.UnityPlayerGameActivity\" android:exported=\"true\">\n      <meta-data android:name=\"com.oculus.vr.focusaware\" android:value=\"true\" />\n    </activity>\n    <meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"eureka|quest3s\" />\n  </application>\n  <uses-feature android:name=\"android.hardware.vr.headtracking\" android:required=\"true\" android:version=\"1\" />\n</manifest>\n";

        [Test]
        public void TheDevicesAndTheHeadTracking_InXrManagementsLibraryManifest_AreFoundThere_AndNothingIsDuplicated()
        {
            // As the Gradle project is generated: the unityLibrary manifest holds neither, XR Management's library holds both.
            XmlDocument unity = Load(Manifest(headTracking: "", devices: ""));
            XmlDocument xr = Load(XrLibrary);
            List<string> changes = QuestDistributionManifest.Apply(unity, xr);
            Assert.That(((XmlElement)xr.DocumentElement.SelectSingleNode("application/meta-data[@android:name='com.oculus.supportedDevices']", Ns(xr))).GetAttribute("value", A), Is.EqualTo("quest3|quest3s"), "canonical, in the library manifest where it is");
            Assert.That(unity.DocumentElement.SelectNodes("application/meta-data[@android:name='com.oculus.supportedDevices']", Ns(unity)).Count, Is.EqualTo(0), "none added to the unityLibrary");
            Assert.That(unity.DocumentElement.SelectNodes("uses-feature[@android:name='android.hardware.vr.headtracking']", Ns(unity)).Count, Is.EqualTo(0), "the headtracking feature held by the library is not duplicated");
            Assert.That(xr.DocumentElement.SelectNodes("uses-feature[@android:name='android.hardware.vr.headtracking']", Ns(xr)).Count, Is.EqualTo(1));
            Assert.That(xr.DocumentElement.SelectNodes("application/activity/meta-data", Ns(xr)).Count, Is.EqualTo(1), "the library's other meta-data stays");
            Assert.That(changes, Does.Contain("meta-data com.oculus.supportedDevices=quest3|quest3s (was eureka|quest3s)"));
            Assert.That(QuestDistributionManifest.Apply(unity, xr), Is.Empty, "applied again: nothing changes");
            // The devices in both manifests: two, refused.
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest()), Load(XrLibrary)));
            // The devices in neither: refused.
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(devices: "")), Load(XrLibrary.Replace("<meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"eureka|quest3s\" />", ""))));
            // Wrong devices in the library manifest: refused, left as they were.
            XmlDocument wrong = Load(XrLibrary.Replace("eureka|quest3s", "quest2|eureka|quest3s"));
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(devices: "")), wrong));
            Assert.That(((XmlElement)wrong.DocumentElement.SelectSingleNode("application/meta-data[@android:name='com.oculus.supportedDevices']", Ns(wrong))).GetAttribute("value", A), Is.EqualTo("quest2|eureka|quest3s"));
        }

        [Test]
        public void TheSupportedDevices_MissingExtraUnknownRepeatedEmpty_OrTheMetaDataMissingOrRepeated_AreRefused_NotCorrected()
        {
            foreach (string value in new[] { "quest3s", "eureka", "quest2|eureka|quest3s", "eureka|quest3s|quest2", "eureka|quest3s|", "eureka||quest3s", "eureka|quest3s|quest3s", "eureka|quest3s|quest3", "Eureka|quest3s", "", "unknown|quest3s" })
            {
                XmlDocument d = Load(Manifest(devices: "    <meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"" + value + "\" />\n"));
                Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(d), "refused: '" + value + "'");
                Assert.That(DevicesValue(d), Is.EqualTo(value), "and left as it was: '" + value + "'");
            }
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(devices: "    <meta-data android:name=\"com.oculus.supportedDevices\" />\n"))), "no value");
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(devices: ""))), "no meta-data");
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(devices: Devices + Devices))), "two meta-data");
            // The meta-data inside the activity does not count: with none under application the build fails.
            const string inActivity = "      <meta-data android:name=\"com.oculus.supportedDevices\" android:value=\"eureka|quest3s\" />\n";
            Assert.Throws<BuildFailedException>(() => QuestDistributionManifest.Apply(Load(Manifest(devices: "", activityMetaData: inActivity))), "only inside the activity");
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
            Assert.That(changes, Is.EqualTo(new[] { "manifest android:installLocation=auto", "activity " + QuestDistributionManifest.LaunchActivity + " android:excludeFromRecents=true", "meta-data com.oculus.supportedDevices=quest3|quest3s (was eureka|quest3s)" }));
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
                    QuestDistributionBuild.KeystoreArgument, "C:/keys/a.keystore", QuestDistributionBuild.KeyAliasArgument, "a", QuestDistributionBuild.OutputArgument, "C:/out/a.apk", QuestDistributionBuild.DevicesArgument, "quest3|quest3s" }, out var r), Is.Null);
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
