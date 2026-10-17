using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The manifest attributes a distribution build guarantees (DESIGN 3.5, D-239), added to the generated Gradle
    /// project's unityLibrary manifest after XR Management and the OpenXR Meta Quest feature have written their
    /// entries (their hook runs at order 1; this one at 10000): nothing is replaced, only what is missing is added
    /// or set. Active only while <see cref="Required"/> is set by <see cref="QuestDistributionBuild"/>.
    /// <list type="bullet">
    /// <item>manifest: android:installLocation="auto"</item>
    /// <item>the launch activity -- the one activity whose intent-filter holds MAIN; it must be
    /// <see cref="LaunchActivity"/> -- gets android:excludeFromRecents="true", and that intent-filter holds the
    /// category com.oculus.intent.category.VR</item>
    /// <item>uses-feature android.hardware.vr.headtracking with required="true" and version="1"</item>
    /// <item>the one meta-data com.oculus.supportedDevices under application holds exactly the distribution's
    /// devices (<see cref="QuestDistributionTargets"/>), written canonically; anything else fails the build</item>
    /// </list>
    /// minSdkVersion and targetSdkVersion come from the player settings (<see cref="QuestDistributionBuild.SettingsScope"/>).
    /// </summary>
    public static class QuestDistributionManifest
    {
        public const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
        public const string LaunchActivity = "com.unity3d.player.UnityPlayerGameActivity";
        public const string MainAction = "android.intent.action.MAIN";
        public const string VrCategory = "com.oculus.intent.category.VR";
        public const string HeadTrackingFeature = "android.hardware.vr.headtracking";

        /// <summary>Set by the distribution build for the time of its build; every other build leaves the manifest alone.</summary>
        public static bool Required { get; set; }

        /// <summary>
        /// Applies the guarantees to <paramref name="manifest"/> (the unityLibrary AndroidManifest.xml as generated)
        /// and lists what was set. Refuses, by <see cref="BuildFailedException"/>, a manifest without exactly one
        /// activity holding MAIN, or whose that activity is not <see cref="LaunchActivity"/>: the attribute goes on
        /// the real launch activity, never on another.
        /// </summary>
        public static List<string> Apply(XmlDocument manifest) => Apply(manifest, null);

        /// <summary>
        /// The same over the two manifests the Gradle project merges: the unityLibrary's own, and XR Management's
        /// library manifest (unityLibrary/xrmanifest.androidlib/AndroidManifest.xml, null where there is none), into
        /// which XR Management writes the features' requirements -- the supported devices and the headtracking feature
        /// among them. The activity and the install location are the unityLibrary's; the devices meta-data must be
        /// exactly one across both applications; the headtracking feature is set where it is and added to the
        /// unityLibrary only when neither holds it.
        /// </summary>
        public static List<string> Apply(XmlDocument manifest, XmlDocument xrLibraryManifest)
        {
            var changes = new List<string>();
            XmlElement root = manifest.DocumentElement;
            if (root == null || root.Name != "manifest") throw new BuildFailedException("the manifest has no <manifest> root");
            var ns = new XmlNamespaceManager(manifest.NameTable);
            ns.AddNamespace("android", AndroidNamespace);
            XmlElement xrRoot = xrLibraryManifest?.DocumentElement;
            if (xrLibraryManifest != null && (xrRoot == null || xrRoot.Name != "manifest")) throw new BuildFailedException("the XR library manifest has no <manifest> root");
            XmlNamespaceManager xrNs = null;
            if (xrLibraryManifest != null) { xrNs = new XmlNamespaceManager(xrLibraryManifest.NameTable); xrNs.AddNamespace("android", AndroidNamespace); }

            if (Set(root, "installLocation", "auto")) changes.Add("manifest android:installLocation=auto");

            XmlElement application = root.SelectSingleNode("application") as XmlElement;
            if (application == null) throw new BuildFailedException("the manifest has no <application>");
            var launchers = new List<(XmlElement activity, XmlElement filter)>();
            foreach (XmlElement activity in application.SelectNodes("activity"))
                foreach (XmlElement filter in activity.SelectNodes("intent-filter"))
                    if (filter.SelectSingleNode("action[@android:name='" + MainAction + "']", ns) != null) { launchers.Add((activity, filter)); break; }
            if (launchers.Count != 1)
                throw new BuildFailedException("the manifest must hold exactly one activity with an intent-filter of " + MainAction + ", found " + launchers.Count);
            string name = launchers[0].activity.GetAttribute("name", AndroidNamespace);
            if (name != LaunchActivity)
                throw new BuildFailedException("the launch activity is '" + name + "', not " + LaunchActivity + ": the distribution's attributes go on the real launch activity only");
            if (Set(launchers[0].activity, "excludeFromRecents", "true")) changes.Add("activity " + LaunchActivity + " android:excludeFromRecents=true");
            if (launchers[0].filter.SelectSingleNode("category[@android:name='" + VrCategory + "']", ns) == null)
            {
                XmlElement category = manifest.CreateElement("category");
                category.SetAttribute("name", AndroidNamespace, VrCategory);
                launchers[0].filter.AppendChild(category);
                changes.Add("launch intent-filter category " + VrCategory);
            }

            // The supported devices (D-240): exactly one meta-data of the name directly under <application>, whose
            // devices -- as the package writes them (eureka) or canonical -- are exactly the distribution's; then the
            // value is written canonically (quest3|quest3s). Nothing missing, extra or unknown is corrected.
            var devicesElements = new List<XmlElement>();
            foreach (XmlElement e in application.SelectNodes("meta-data[@android:name='" + QuestDistributionTargets.MetaDataName + "']", ns)) devicesElements.Add(e);
            XmlElement xrApplication = xrRoot?.SelectSingleNode("application") as XmlElement;
            if (xrApplication != null)
                foreach (XmlElement e in xrApplication.SelectNodes("meta-data[@android:name='" + QuestDistributionTargets.MetaDataName + "']", xrNs)) devicesElements.Add(e);
            if (devicesElements.Count != 1)
                throw new BuildFailedException("the manifests must hold exactly one <meta-data android:name=\"" + QuestDistributionTargets.MetaDataName + "\"> directly under <application> (the unityLibrary's and XR Management's library manifest together), found " + devicesElements.Count);
            XmlElement devicesElement = devicesElements[0];
            string devicesValue = devicesElement.HasAttribute("value", AndroidNamespace) ? devicesElement.GetAttribute("value", AndroidNamespace) : null;
            string devicesFailure = QuestDistributionTargets.Split(devicesValue, out List<string> deviceNames) ?? QuestDistributionTargets.Check(deviceNames);
            if (devicesFailure != null) throw new BuildFailedException(QuestDistributionTargets.MetaDataName + " as generated is '" + devicesValue + "': " + devicesFailure);
            if (Set(devicesElement, "value", QuestDistributionTargets.CanonicalValue)) changes.Add("meta-data " + QuestDistributionTargets.MetaDataName + "=" + QuestDistributionTargets.CanonicalValue + " (was " + devicesValue + ")");

            XmlElement feature = root.SelectSingleNode("uses-feature[@android:name='" + HeadTrackingFeature + "']", ns) as XmlElement;
            if (feature == null && xrRoot != null) feature = xrRoot.SelectSingleNode("uses-feature[@android:name='" + HeadTrackingFeature + "']", xrNs) as XmlElement;
            if (feature == null)
            {
                feature = manifest.CreateElement("uses-feature");
                feature.SetAttribute("name", AndroidNamespace, HeadTrackingFeature);
                root.InsertBefore(feature, application);
                changes.Add("uses-feature " + HeadTrackingFeature);
            }
            if (Set(feature, "required", "true")) changes.Add("uses-feature " + HeadTrackingFeature + " android:required=true");
            if (Set(feature, "version", "1")) changes.Add("uses-feature " + HeadTrackingFeature + " android:version=1");
            return changes;
        }

        private static bool Set(XmlElement element, string attribute, string value)
        {
            if (element.GetAttribute(attribute, AndroidNamespace) == value) return false;
            element.SetAttribute(attribute, AndroidNamespace, value);
            return true;
        }

        /// <summary>The build hook: after the Gradle project is generated, the unityLibrary manifest is read, applied to and written back.</summary>
        public sealed class Hook : IPostGenerateGradleAndroidProject
        {
            public int callbackOrder => 10000;

            public void OnPostGenerateGradleAndroidProject(string path)
            {
                if (!Required) return;
                string file = Path.Combine(path, "src", "main", "AndroidManifest.xml");
                if (!File.Exists(file)) throw new BuildFailedException("the generated unityLibrary manifest is not at " + file);
                var manifest = new XmlDocument { PreserveWhitespace = true };
                manifest.Load(file);
                // XR Management's own library manifest, where it writes the features' requirements (the devices, the headtracking feature).
                string xrFile = Path.Combine(path, "xrmanifest.androidlib", "AndroidManifest.xml");
                XmlDocument xrManifest = null;
                if (File.Exists(xrFile)) { xrManifest = new XmlDocument { PreserveWhitespace = true }; xrManifest.Load(xrFile); }
                List<string> changes = Apply(manifest, xrManifest);
                manifest.Save(file);
                xrManifest?.Save(xrFile);
                Debug.Log("QUEST DISTRIBUTION MANIFEST: " + file + (xrManifest != null ? " and " + xrFile : " (no XR library manifest)") + "; set: " + (changes.Count == 0 ? "nothing (all held already)" : string.Join("; ", changes)));
            }
        }
    }
}
