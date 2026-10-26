using System.IO;
using System.Linq;
using System.Xml;
using UnityEditor.Android;
using UnityEditor.Build;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>Only a phone build's generated Gradle files are changed; Quest assets stay intact.</summary>
    public static class AndroidFlatManifest
    {
        private const string Android = "http://schemas.android.com/apk/res/android";

        public static int RemoveVrRequirements(XmlDocument manifest)
        {
            if (manifest.DocumentElement == null || manifest.DocumentElement.Name != "manifest")
                throw new BuildFailedException("The Android phone manifest has no manifest element");
            int removed = 0;
            foreach (var element in manifest.SelectNodes("//*").Cast<XmlElement>().ToArray())
            {
                string name = element.GetAttribute("name", Android);
                bool vr = element.Name == "uses-feature" && (name.StartsWith("android.hardware.vr.") || name == "android.software.vr.mode")
                    || element.Name == "category" && name == "com.oculus.intent.category.VR"
                    || element.Name == "meta-data" && (name.StartsWith("com.oculus.") || name.StartsWith("com.meta.openxr."));
                if (!vr) continue;
                element.ParentNode.RemoveChild(element); removed++;
            }
            foreach (XmlElement activity in manifest.SelectNodes("/manifest/application/activity"))
                activity.RemoveAttribute("excludeFromRecents", Android);
            return removed;
        }

        public sealed class Hook : IPostGenerateGradleAndroidProject
        {
            public int callbackOrder => 11000;
            public void OnPostGenerateGradleAndroidProject(string path)
            {
                if (!QuestDistributionManifest.NonXrBuildInProgress) return;
                foreach (string file in new[] { Path.Combine(path, "src/main/AndroidManifest.xml"), Path.Combine(path, "xrmanifest.androidlib/AndroidManifest.xml") })
                {
                    if (!File.Exists(file)) continue;
                    var manifest = new XmlDocument { PreserveWhitespace = true }; manifest.Load(file);
                    int removed = RemoveVrRequirements(manifest); manifest.Save(file);
                    UnityEngine.Debug.Log("ANDROID FLAT MANIFEST: " + file + "; VR requirements removed=" + removed);
                }
            }
        }
    }
}
