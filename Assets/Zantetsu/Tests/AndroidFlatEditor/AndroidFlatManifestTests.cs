using System.Xml;
using NUnit.Framework;
using UnityEditor.Build;
using Zantetsu.EditorTools.AndroidFlat;

namespace Zantetsu.AndroidFlat.Tests
{
    public sealed class AndroidFlatManifestTests
    {
        private const string Android = "http://schemas.android.com/apk/res/android";

        [Test]
        public void RemovesQuestRequirementsAndKeepsThePhoneLauncherAndOtherFeatures()
        {
            var manifest = new XmlDocument();
            manifest.LoadXml("<manifest xmlns:android='" + Android + "'>"
                + "<uses-feature android:name='android.hardware.vr.headtracking' android:required='true'/>"
                + "<uses-feature android:name='android.hardware.vr.high_performance'/>"
                + "<uses-feature android:name='android.hardware.vulkan.level'/>"
                + "<uses-permission android:name='android.permission.INTERNET'/>"
                + "<application><meta-data android:name='com.oculus.supportedDevices' android:value='quest3|quest3s'/>"
                + "<activity android:name='com.unity3d.player.UnityPlayerGameActivity' android:excludeFromRecents='true' android:screenOrientation='fullSensor'>"
                + "<intent-filter><action android:name='android.intent.action.MAIN'/>"
                + "<category android:name='android.intent.category.LAUNCHER'/><category android:name='com.oculus.intent.category.VR'/>"
                + "</intent-filter><meta-data android:name='unityplayer.UnityActivity' android:value='true'/></activity></application></manifest>");
            Assert.That(AndroidFlatManifest.RemoveVrRequirements(manifest), Is.EqualTo(4));
            Assert.That(manifest.OuterXml, Does.Not.Contain("com.oculus.").And.Not.Contain("android.hardware.vr.").And.Not.Contain("excludeFromRecents"));
            Assert.That(manifest.OuterXml, Does.Contain("android.intent.category.LAUNCHER").And.Contain("android.hardware.vulkan.level")
                .And.Contain("android.permission.INTERNET").And.Contain("unityplayer.UnityActivity").And.Contain("fullSensor"));
        }

        [Test]
        public void RemovesVrFromAnXrLibraryManifestWithoutRequiringAnActivity()
        {
            var manifest = new XmlDocument();
            manifest.LoadXml("<manifest xmlns:android='" + Android + "'><uses-feature android:name='android.software.vr.mode'/>"
                + "<application><meta-data android:name='com.meta.openxr.feature' android:value='true'/></application></manifest>");
            Assert.That(AndroidFlatManifest.RemoveVrRequirements(manifest), Is.EqualTo(2));
            Assert.That(AndroidFlatManifest.RemoveVrRequirements(manifest), Is.Zero);
        }

        [Test]
        public void RejectsInvalidDocumentInsteadOfTreatingItAsAValidManifest()
        {
            var manifest = new XmlDocument(); manifest.LoadXml("<application/>");
            Assert.Throws<BuildFailedException>(() => AndroidFlatManifest.RemoveVrRequirements(manifest));
        }
    }
}
