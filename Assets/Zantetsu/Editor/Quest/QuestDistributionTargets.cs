using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.MetaQuestSupport;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The devices the ALPHA distribution is for (DESIGN 3.5, D-240): Quest 3 and Quest 3S, as one explicit
    /// definition that the build (the Meta Quest feature's target devices), the manifest hook (the
    /// com.oculus.supportedDevices value) and the inspection (its expected value, passed by the wrapper) all use.
    /// The Unity package names Quest 3 "eureka" inside; the manifest's canonical name is "quest3". The package's
    /// names are never rewritten here: the feature's per-device "enabled" is set for the build and put back.
    /// </summary>
    public static class QuestDistributionTargets
    {
        /// <summary>A target: the package's manifestName and the canonical name Meta's specification uses.</summary>
        public readonly struct Target
        {
            public readonly string internalName, canonicalName;
            public Target(string internalName, string canonicalName) { this.internalName = internalName; this.canonicalName = canonicalName; }
        }

        /// <summary>The distribution's devices, in the order the manifest value lists them.</summary>
        public static readonly Target[] Devices = { new Target("eureka", "quest3"), new Target("quest3s", "quest3s") };

        /// <summary>The value the finished APK's com.oculus.supportedDevices holds: quest3|quest3s.</summary>
        public static readonly string CanonicalValue = string.Join("|", Array.ConvertAll(Devices, d => d.canonicalName));

        public const string MetaDataName = "com.oculus.supportedDevices";

        /// <summary>The package's internal names known here and their canonical names (the identity for the rest).</summary>
        private static readonly Dictionary<string, string> KnownInternal = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "eureka", "quest3" }, { "cambria", "questpro" }, { "quest", "quest" }, { "quest2", "quest2" }, { "quest3s", "quest3s" },
        };

        /// <summary>The canonical name of a name as the package writes it (eureka -> quest3); an unknown name is given back as it is.</summary>
        public static string Canonical(string name) => KnownInternal.TryGetValue(name, out string canonical) ? canonical : name;

        /// <summary>
        /// Splits a supportedDevices value: the failure (null: none) names an empty value, an empty part (a
        /// trailing or doubled '|') or a repeated name. Case-sensitive, as Android's identifiers are.
        /// </summary>
        public static string Split(string value, out List<string> names)
        {
            names = new List<string>();
            if (string.IsNullOrEmpty(value)) return "the supportedDevices value is empty";
            foreach (string part in value.Split('|'))
            {
                if (part.Length == 0) return "the supportedDevices value holds an empty part: '" + value + "'";
                if (names.Contains(part)) return "the supportedDevices value repeats '" + part + "': '" + value + "'";
                names.Add(part);
            }
            return null;
        }

        /// <summary>
        /// Whether the names (canonical or the package's internal) are exactly the distribution's devices, in any
        /// order: the failure (null: none) names what is missing, extra or unknown. Nothing is corrected.
        /// </summary>
        public static string Check(IEnumerable<string> names)
        {
            var canonical = new List<string>();
            foreach (string name in names)
            {
                string c = Canonical(name);
                if (canonical.Contains(c)) return "the device '" + name + "' is listed twice";
                canonical.Add(c);
            }
            var missing = new List<string>();
            foreach (Target d in Devices) if (!canonical.Contains(d.canonicalName)) missing.Add(d.canonicalName);
            var extra = new List<string>();
            foreach (string c in canonical) if (Array.FindIndex(Devices, d => d.canonicalName == c) < 0) extra.Add(c);
            if (missing.Count > 0 || extra.Count > 0)
                return "the devices are not the distribution's " + CanonicalValue + ": missing [" + string.Join(", ", missing) + "], extra or unknown [" + string.Join(", ", extra) + "]";
            return null;
        }

        /// <summary>
        /// The Android Meta Quest feature's target device list, or null with the failure: the OpenXR settings for
        /// Android, the feature, its being enabled, the list, and every distribution device in it are required.
        /// Reads only.
        /// </summary>
        public static SerializedObject FindFeatureDevices(out SerializedProperty devices, out string failure)
        {
            devices = null;
            failure = null;
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            if (settings == null) { failure = "no OpenXR settings for Android"; return null; }
            MetaQuestFeature feature = settings.GetFeature<MetaQuestFeature>();
            if (feature == null) { failure = "the Meta Quest Support feature is not in the Android OpenXR settings"; return null; }
            if (!feature.enabled) { failure = "the Meta Quest Support feature is not enabled for Android"; return null; }
            var so = new SerializedObject(feature);
            SerializedProperty list = so.FindProperty("targetDevices");
            if (list == null || !list.isArray) { failure = "the Meta Quest Support feature has no targetDevices list (package changed?)"; return null; }
            foreach (Target d in Devices)
            {
                bool found = false;
                for (int i = 0; i < list.arraySize && !found; i++)
                    found = list.GetArrayElementAtIndex(i).FindPropertyRelative("manifestName")?.stringValue == d.internalName;
                if (!found) { failure = "the Meta Quest Support feature's target devices do not include '" + d.internalName + "' (" + d.canonicalName + ")"; return null; }
            }
            devices = list;
            return so;
        }

        /// <summary>For tests: whether the Android Meta Quest feature is enabled (false with no feature), and a way to set it.</summary>
        public static bool TryGetFeatureEnabled(out bool enabled)
        {
            enabled = false;
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            MetaQuestFeature feature = settings != null ? settings.GetFeature<MetaQuestFeature>() : null;
            if (feature == null) return false;
            enabled = feature.enabled;
            return true;
        }

        /// <summary>For tests: sets the Android Meta Quest feature's enabled flag (in memory; nothing is saved here).</summary>
        public static void SetFeatureEnabled(bool enabled)
        {
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
            MetaQuestFeature feature = settings != null ? settings.GetFeature<MetaQuestFeature>() : null;
            if (feature == null) throw new InvalidOperationException("no Meta Quest feature in the Android OpenXR settings");
            feature.enabled = enabled;
        }

        /// <summary>The names the feature lists, with the enabled ones marked, for a log line.</summary>
        public static string Describe(SerializedProperty devices)
        {
            var parts = new List<string>();
            for (int i = 0; i < devices.arraySize; i++)
            {
                SerializedProperty d = devices.GetArrayElementAtIndex(i);
                parts.Add(d.FindPropertyRelative("manifestName").stringValue + (d.FindPropertyRelative("enabled").boolValue ? "=on" : "=off"));
            }
            return string.Join(", ", parts);
        }

        /// <summary>
        /// The feature's per-device "enabled" set for the build -- the distribution's devices on, every other off --
        /// and put back as it was when disposed, whether the build ends normally or by an exception. Names, order,
        /// the other features and the other settings are untouched. Refuses (by <see cref="BuildFailedException"/>)
        /// before changing anything when <see cref="FindFeatureDevices"/> fails.
        /// <para>
        /// The change is made on the feature object in memory, through the package's internal list, and the asset is
        /// not marked dirty: the build then neither saves nor re-imports the settings asset (the first attempt of
        /// 2026-10-17, which applied the change through a SerializedObject, made the build re-import it; the APK of
        /// that attempt still held the package's manifest entries, so the re-import was not shown to break anything --
        /// it is avoided here only so that the asset stays as it was). The in-memory objects are what the package
        /// reads for its manifest requirements.
        /// </para>
        /// </summary>
        public sealed class Scope : IDisposable
        {
            private readonly MetaQuestFeature _feature;
            private readonly bool[] _enabledBefore;
            private bool _disposed;

            public string Before { get; }
            public string During { get; }

            public Scope()
            {
                SerializedObject so = FindFeatureDevices(out SerializedProperty devices, out string failure);
                if (so == null) throw new BuildFailedException("distribution targets: " + failure);
                _feature = (MetaQuestFeature)so.targetObject;
                Before = Describe(devices);
                _enabledBefore = ReadEnabled(_feature, out string[] names);
                var during = new bool[names.Length];
                for (int i = 0; i < names.Length; i++) during[i] = Array.FindIndex(Devices, t => t.internalName == names[i]) >= 0;
                WriteEnabled(_feature, during);
                During = Describe(new SerializedObject(_feature).FindProperty("targetDevices"));
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                WriteEnabled(_feature, _enabledBefore);
            }
        }

        // The package's internal list (List<TargetDevice> targetDevices; TargetDevice { manifestName, enabled, ... }),
        // read and written in memory. A changed package shape fails loudly here, never silently.
        private static System.Collections.IList DeviceList(MetaQuestFeature feature, out System.Reflection.FieldInfo nameField, out System.Reflection.FieldInfo enabledField)
        {
            System.Reflection.FieldInfo listField = typeof(MetaQuestFeature).GetField("targetDevices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var list = listField?.GetValue(feature) as System.Collections.IList;
            if (list == null) throw new BuildFailedException("distribution targets: the Meta Quest Support feature's targetDevices list is not where this version expects it (package changed?)");
            Type element = list.GetType().IsGenericType ? list.GetType().GetGenericArguments()[0] : null;
            nameField = element?.GetField("manifestName");
            enabledField = element?.GetField("enabled");
            if (nameField == null || enabledField == null || enabledField.FieldType != typeof(bool) || nameField.FieldType != typeof(string))
                throw new BuildFailedException("distribution targets: the Meta Quest Support feature's target device has no manifestName/enabled fields (package changed?)");
            return list;
        }

        private static bool[] ReadEnabled(MetaQuestFeature feature, out string[] names)
        {
            System.Collections.IList list = DeviceList(feature, out var nameField, out var enabledField);
            var enabled = new bool[list.Count];
            names = new string[list.Count];
            for (int i = 0; i < list.Count; i++) { enabled[i] = (bool)enabledField.GetValue(list[i]); names[i] = (string)nameField.GetValue(list[i]); }
            return enabled;
        }

        private static void WriteEnabled(MetaQuestFeature feature, bool[] enabled)
        {
            System.Collections.IList list = DeviceList(feature, out _, out var enabledField);
            for (int i = 0; i < list.Count && i < enabled.Length; i++)
            {
                object device = list[i];           // a boxed copy of the struct
                enabledField.SetValue(device, enabled[i]);
                list[i] = device;                   // put back into the list
            }
        }
    }
}
