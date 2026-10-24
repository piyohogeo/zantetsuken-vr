using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Zantetsu.EditorTools.Quest;
using Zantetsu.EditorTools.Sandbox;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>A versioned, custom-signed Release APK or AAB with external statistics.</summary>
    public static class AndroidFlatDistributionBuild
    {
        public const string NoticesPath = "Assets/StreamingAssets/ThirdPartyNotices.txt";
        public const string StorePasswordVariable = "ZANTETSU_KEYSTORE_PASSWORD";
        public const string KeyPasswordVariable = "ZANTETSU_KEY_PASSWORD";
        public sealed class Request
        {
            public string versionName, keystore, keyAlias, output;
            public int versionCode;
            public bool IsBundle => string.Equals(Path.GetExtension(output), ".aab", StringComparison.OrdinalIgnoreCase);
        }

        public static string TryRead(string[] args, out Request request)
        {
            request = null;
            var r = new Request();
            string error;
            if ((error = Read(args, "-zantetsuDistVersionName", out r.versionName)) != null) return error;
            if ((error = Read(args, "-zantetsuDistVersionCode", out string code)) != null) return error;
            if ((error = Read(args, "-zantetsuDistKeystore", out r.keystore)) != null) return error;
            if ((error = Read(args, "-zantetsuDistKeyAlias", out r.keyAlias)) != null) return error;
            if ((error = Read(args, "-zantetsuAndroidFlatOut", out r.output)) != null) return error;
            if (code.Length == 0 || !code.All(c => c >= '0' && c <= '9') || !int.TryParse(code, out r.versionCode)
                || r.versionCode < 1 || r.versionCode > 2100000000) return "versionCode must be digits from 1 to 2100000000";
            if (r.versionName.StartsWith("-") || r.versionName.Any(char.IsWhiteSpace)) return "versionName must not start with '-' or contain whitespace";
            if (r.keyAlias.StartsWith("-") || r.keyAlias.Any(char.IsWhiteSpace)) return "key alias must not start with '-' or contain whitespace";
            if (!r.IsBundle && !r.output.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return "distribution output must be an .apk or .aab";
            if (args.Contains("-zantetsuAndroidFlatDev")) return "a distribution build is never a Development build";
            try { if (DemoBuildPolicy.Parse(args) != DemoBuildMode.Commercial) return "a distribution build requires -zantetsuBuildMode commercial"; }
            catch (BuildFailedException exception) { return exception.Message; }
            request = r;
            return null;
        }

        private static string Read(string[] args, string name, out string value)
        {
            value = null;
            if (args == null) return "no arguments";
            int at = Array.IndexOf(args, name);
            if (at < 0 || at + 1 >= args.Length || string.IsNullOrWhiteSpace(args[at + 1]) || args[at + 1].StartsWith("-")) return name + " needs a value";
            if (Array.LastIndexOf(args, name) != at) return name + " was given twice";
            value = args[at + 1];
            return null;
        }

        public static string Validate(Request request, bool keystoreExists, string storePassword, string keyPassword)
        {
            if (request == null) return "no request";
            if (!keystoreExists) return "the upload keystore does not exist";
            string extension = Path.GetExtension(request.keystore);
            if (!string.Equals(extension, ".jks", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(extension, ".keystore", StringComparison.OrdinalIgnoreCase)) return "the upload keystore must be .jks or .keystore";
            if (string.IsNullOrEmpty(storePassword) || string.IsNullOrEmpty(keyPassword)) return "upload key passwords must be supplied through environment variables";
            return null;
        }

        public sealed class SettingsScope : IDisposable
        {
            private readonly string version = PlayerSettings.bundleVersion;
            private readonly int code = PlayerSettings.Android.bundleVersionCode;
            private readonly bool custom = PlayerSettings.Android.useCustomKeystore;
            private readonly string alias = PlayerSettings.Android.keyaliasName;
            private readonly UnityEngine.Object settings = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0];
            private readonly string keystore;

            public SettingsScope(Request request)
            {
                keystore = new SerializedObject(settings).FindProperty("AndroidKeystoreName").stringValue;
                PlayerSettings.bundleVersion = request.versionName;
                PlayerSettings.Android.bundleVersionCode = request.versionCode;
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = request.keystore;
                PlayerSettings.Android.keyaliasName = request.keyAlias;
                PlayerSettings.Android.keystorePass = Environment.GetEnvironmentVariable(StorePasswordVariable);
                PlayerSettings.Android.keyaliasPass = Environment.GetEnvironmentVariable(KeyPasswordVariable);
            }
            public void Dispose()
            {
                PlayerSettings.Android.keystorePass = PlayerSettings.Android.keyaliasPass = "";
                PlayerSettings.bundleVersion = version;
                PlayerSettings.Android.bundleVersionCode = code;
                PlayerSettings.Android.useCustomKeystore = custom;
                // The public setter rewrites an empty stored path to "{inproject}: ". Restore the serialized value exactly.
                var serialized = new SerializedObject(settings);
                serialized.FindProperty("AndroidKeystoreName").stringValue = keystore;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                PlayerSettings.Android.keyaliasName = alias;
            }
        }

        public static void BuildBundle() => Build(".aab");
        public static void BuildApk() => Build(".apk");

        private static void Build(string extension)
        {
            string failure = TryRead(Environment.GetCommandLineArgs(), out Request request);
            if (failure == null && !request.output.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) failure = "this entry requires " + extension;
            failure ??= Validate(request, request != null && File.Exists(request.keystore),
                Environment.GetEnvironmentVariable(StorePasswordVariable), Environment.GetEnvironmentVariable(KeyPasswordVariable));
            if (failure == null && (!File.Exists(NoticesPath) || new FileInfo(NoticesPath).Length == 0))
                failure = "prepare the bundled third-party notices before building a distribution";
            if (failure == null) failure = QuestDistributionBuild.CheckStatisticsConfig(
                new QuestDistributionBuild.Request { statistics = QuestDistributionBuild.StatisticsRequired }, out _);
            string[] args = Environment.GetCommandLineArgs();
            string manifest = QuestDistributionInputSources.Value(args, QuestDistributionInputSources.ManifestArgument);
            if (failure == null && manifest != null)
            {
                failure = QuestDistributionInputSources.ReadManifest(manifest, out var entries);
                if (failure == null) failure = QuestDistributionInputSources.Check(entries,
                    QuestDistributionInputSources.ExpectBlank,
                    QuestDistributionInputSources.Value(args, QuestDistributionInputSources.AsideArgument), out _);
            }
            if (failure != null) throw new BuildFailedException("ANDROID DISTRIBUTION refused: " + failure);
            AndroidFlatBuild.BuildPlayer(request);
        }
    }
}
