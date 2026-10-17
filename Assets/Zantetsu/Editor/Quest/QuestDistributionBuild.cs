using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Zantetsu.EditorTools.Sandbox;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The ALPHA distribution build of the Quest APK of every scenario (DESIGN 3.5): the same package and the same
    /// build as <see cref="QuestScenarioPlayerBuild"/>, as a Release (never Development) commercial (never the demo)
    /// build, signed with the distribution key and carrying the version the distributor names. Everything of the
    /// request is read from the command line but the two passwords, which come from the environment
    /// (<see cref="KeystorePasswordVariable"/>, <see cref="KeyPasswordVariable"/>) and are never written anywhere.
    /// What can be known before the build -- a missing or malformed value, a missing keystore, a build mode other
    /// than commercial, a development option -- fails the build before it starts (exit 2); nothing falls back to the
    /// debug signing. The player settings the build changes (identifier, version, keystore) are put back whether the
    /// build succeeds or fails, and the passwords are cleared; Tools/Quest3s/Build-Distribution.ps1 also puts the
    /// settings file itself back. The version code is never incremented here: the distributor names it, larger than
    /// every one uploaded before (the procedure is in docs/quest3s-alpha-distribution.md).
    /// </summary>
    public static class QuestDistributionBuild
    {
        public const string VersionNameArgument = "-zantetsuDistVersionName";
        public const string VersionCodeArgument = "-zantetsuDistVersionCode";
        public const string KeystoreArgument = "-zantetsuDistKeystore";
        public const string KeyAliasArgument = "-zantetsuDistKeyAlias";
        /// <summary>The output argument of the shared build (<see cref="QuestStaticDemoBuild.BuildApkForScenes"/>).</summary>
        public const string OutputArgument = "-zantetsuQuestOut";
        /// <summary>The development option of the shared build, which a distribution build refuses.</summary>
        public const string DevelopmentArgument = "-zantetsuQuestDev";
        public const string KeystorePasswordVariable = "ZANTETSU_KEYSTORE_PASSWORD";
        public const string KeyPasswordVariable = "ZANTETSU_KEY_PASSWORD";
        /// <summary>The package of the distribution: the one of every scenario. Never another.</summary>
        public const string ApplicationIdentifier = QuestScenarioPlayerBuild.ApplicationIdentifier;
        /// <summary>The exit code of a request refused before the build.</summary>
        public const int RefusedExitCode = 2;

        /// <summary>What a distribution build is asked for, from the command line (the passwords are not here).</summary>
        public sealed class Request
        {
            public string versionName;
            public int versionCode;
            public string keystore;
            public string keyAlias;
            public string output;
        }

        /// <summary>
        /// Reads the request from the arguments: the failure (null: none) names the first argument missing, given
        /// twice, or malformed. The version code must be a whole number from 1 upward written in digits alone.
        /// </summary>
        public static string TryRead(string[] args, out Request request)
        {
            request = null;
            if (args == null) return "no arguments";
            string failure;
            var r = new Request();
            if ((failure = ReadValue(args, VersionNameArgument, out r.versionName)) != null) return failure;
            if ((failure = ReadValue(args, VersionCodeArgument, out string code)) != null) return failure;
            if ((failure = ReadValue(args, KeystoreArgument, out r.keystore)) != null) return failure;
            if ((failure = ReadValue(args, KeyAliasArgument, out r.keyAlias)) != null) return failure;
            if ((failure = ReadValue(args, OutputArgument, out r.output)) != null) return failure;
            if (!IsDigits(code) || !int.TryParse(code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out r.versionCode) || r.versionCode < 1)
                return VersionCodeArgument + " must be a whole number from 1 upward written in digits alone, not '" + code + "'";
            foreach (char c in r.versionName)
                if (char.IsWhiteSpace(c) || char.IsControl(c)) return VersionNameArgument + " must hold no space or control character";
            request = r;
            return null;
        }

        /// <summary>
        /// What is wrong with a request before any build, or null: the keystore must exist, both passwords must be
        /// given, the build mode must be commercial by its argument, and the development option must be absent.
        /// </summary>
        public static string Validate(Request request, bool keystoreExists, string keystorePassword, string keyPassword, string[] args)
        {
            if (request == null) return "no request";
            if (!keystoreExists) return "the keystore does not exist: " + request.keystore;
            string extension = Path.GetExtension(request.keystore).ToLowerInvariant();
            if (extension != ".keystore" && extension != ".jks") return "the keystore must be a .keystore or .jks file: " + request.keystore;
            if (string.IsNullOrEmpty(keystorePassword)) return "the keystore password is not set in the environment variable " + KeystorePasswordVariable;
            if (string.IsNullOrEmpty(keyPassword)) return "the key password is not set in the environment variable " + KeyPasswordVariable;
            if (!request.output.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)) return OutputArgument + " must name an .apk file";
            DemoBuildMode mode;
            try { mode = DemoBuildPolicy.Parse(args); }
            catch (BuildFailedException e) { return e.Message; }
            if (mode != DemoBuildMode.Commercial) return "a distribution build must be asked for with " + DemoBuildPolicy.ModeArgument + " commercial (it never switches to the demo)";
            if (Array.IndexOf(args, DevelopmentArgument) >= 0) return "a distribution build is never a development build: remove " + DevelopmentArgument;
            return null;
        }

        /// <summary>
        /// The player settings a distribution build sets, put back when disposed -- the identifier, the version name
        /// and code, the custom keystore, its name and the alias -- and the two passwords, which are cleared then.
        /// </summary>
        public sealed class SettingsScope : IDisposable
        {
            private readonly string _identifier, _versionName, _keystore, _alias;
            private readonly int _versionCode;
            private readonly bool _customKeystore;
            private bool _disposed;

            public SettingsScope(Request request, string keystorePassword, string keyPassword)
            {
                _identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
                _versionName = PlayerSettings.bundleVersion;
                _versionCode = PlayerSettings.Android.bundleVersionCode;
                _customKeystore = PlayerSettings.Android.useCustomKeystore;
                _keystore = PlayerSettings.Android.keystoreName;
                _alias = PlayerSettings.Android.keyaliasName;
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
                PlayerSettings.bundleVersion = request.versionName;
                PlayerSettings.Android.bundleVersionCode = request.versionCode;
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = request.keystore;
                PlayerSettings.Android.keyaliasName = request.keyAlias;
                PlayerSettings.Android.keystorePass = keystorePassword;
                PlayerSettings.Android.keyaliasPass = keyPassword;
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, _identifier);
                PlayerSettings.bundleVersion = _versionName;
                PlayerSettings.Android.bundleVersionCode = _versionCode;
                PlayerSettings.Android.useCustomKeystore = _customKeystore;
                PlayerSettings.Android.keystoreName = _keystore;
                PlayerSettings.Android.keyaliasName = _alias;
            }
        }

        /// <summary>The Unity entry of Tools/Quest3s/Build-Distribution.ps1.</summary>
        public static void BuildApk()
        {
            string[] args = Environment.GetCommandLineArgs();
            string keystorePassword = Environment.GetEnvironmentVariable(KeystorePasswordVariable);
            string keyPassword = Environment.GetEnvironmentVariable(KeyPasswordVariable);
            string failure = TryRead(args, out Request request);
            if (failure == null) failure = Validate(request, File.Exists(request.keystore), keystorePassword, keyPassword, args);
            if (failure != null)
            {
                Debug.LogError("QUEST DISTRIBUTION BUILD refused: " + failure);
                if (Application.isBatchMode) EditorApplication.Exit(RefusedExitCode);
                throw new BuildFailedException(failure);
            }

            Debug.Log("QUEST DISTRIBUTION BUILD: package=" + ApplicationIdentifier + " versionName=" + request.versionName + " versionCode=" + request.versionCode
                + " keystore=" + Path.GetFileName(request.keystore) + " alias=" + request.keyAlias + " output=" + request.output + "; Release, commercial, custom keystore");
            bool built = false;
            try
            {
                using (new SettingsScope(request, keystorePassword, keyPassword))
                {
                    QuestScenarioPlayerBuild.Build(false);
                    built = true;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("QUEST DISTRIBUTION BUILD failed: " + e.Message);
            }
            finally
            {
                // The scope is disposed by now (also when the build threw); the passwords are cleared with it.
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
            }

            Debug.Log("QUEST DISTRIBUTION BUILD: " + (built ? "built " + request.output : "NOT built") + "; settings put back");
            if (!built && Application.isBatchMode) EditorApplication.Exit(1);
        }

        private static string ReadValue(string[] args, string name, out string value)
        {
            value = null;
            int at = -1;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.Ordinal)) continue;
                if (at >= 0) return name + " is given twice";
                at = i;
            }

            if (at < 0) return name + " is missing";
            if (at + 1 >= args.Length || args[at + 1].StartsWith("-", StringComparison.Ordinal) || args[at + 1].Length == 0) return name + " is followed by no value";
            value = args[at + 1];
            return null;
        }

        private static bool IsDigits(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length > 10) return false;
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return true;
        }
    }
}
