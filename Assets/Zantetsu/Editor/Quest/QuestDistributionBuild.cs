using System;
using System.IO;
using System.Linq;
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
        /// <summary>The devices the wrapper expects (quest3|quest3s): it must equal <see cref="QuestDistributionTargets.CanonicalValue"/>, so that the build, the inspection and the record agree (D-240).</summary>
        public const string DevicesArgument = "-zantetsuDistDevices";
        /// <summary>The output argument of the shared build (<see cref="QuestStaticDemoBuild.BuildApkForScenes"/>).</summary>
        public const string OutputArgument = "-zantetsuQuestOut";
        /// <summary>
        /// Whether the play statistics' send configuration must be in the build (unit 9-B): "required" (the default: the
        /// local config asset must exist, be able to send and name the external role, else the build is refused) or
        /// "none" (a build that sends nothing, said so on purpose: a diagnosis or a check build, never the ALPHA).
        /// </summary>
        public const string StatisticsArgument = "-zantetsuDistStatistics";
        public const string StatisticsRequired = "required", StatisticsNone = "none";
        /// <summary>The development option of the shared build, which a distribution build refuses.</summary>
        public const string DevelopmentArgument = "-zantetsuQuestDev";
        public const string KeystorePasswordVariable = "ZANTETSU_KEYSTORE_PASSWORD";
        public const string KeyPasswordVariable = "ZANTETSU_KEY_PASSWORD";
        /// <summary>The package of the distribution: the one of every scenario. Never another.</summary>
        public const string ApplicationIdentifier = QuestScenarioPlayerBuild.ApplicationIdentifier;
        /// <summary>The exit code of a request refused before the build.</summary>
        public const int RefusedExitCode = 2;
        /// <summary>The Android settings a distribution build fixes (DESIGN 3.5, D-239): the SDK levels and the install location.</summary>
        public const AndroidSdkVersions MinSdk = AndroidSdkVersions.AndroidApiLevel32;
        public const AndroidSdkVersions TargetSdk = AndroidSdkVersions.AndroidApiLevel34;
        public const AndroidPreferredInstallLocation InstallLocation = AndroidPreferredInstallLocation.Auto;
        /// <summary>
        /// The stack trace of an ordinary log line (<see cref="LogType.Log"/>) in a distribution build (D-257, 2026-10-26): none.
        /// The lines themselves stay (the world's start and end lines are what a device check reads); warnings, errors,
        /// assertions and exceptions keep the project's stack traces.
        /// </summary>
        public const StackTraceLogType LogStackTrace = StackTraceLogType.None;

        /// <summary>What a distribution build is asked for, from the command line (the passwords are not here).</summary>
        public sealed class Request
        {
            public string versionName;
            public int versionCode;
            public string keystore;
            public string keyAlias;
            public string output;
            public string devices;
            /// <summary>"required" or "none" (<see cref="StatisticsArgument"/>; absent means required).</summary>
            public string statistics = StatisticsRequired;
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
            if ((failure = ReadValue(args, DevicesArgument, out r.devices)) != null) return failure;
            int statisticsAt = Array.IndexOf(args, StatisticsArgument);
            if (statisticsAt >= 0)
            {
                if (statisticsAt + 1 >= args.Length) return StatisticsArgument + " is missing its value (required|none)";
                r.statistics = args[statisticsAt + 1];
                if (r.statistics != StatisticsRequired && r.statistics != StatisticsNone) return StatisticsArgument + " must be required or none, not '" + r.statistics + "'";
            }
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
            if (!string.Equals(request.devices, QuestDistributionTargets.CanonicalValue, StringComparison.Ordinal))
                return DevicesArgument + " is '" + request.devices + "' but the distribution's devices are " + QuestDistributionTargets.CanonicalValue + " (QuestDistributionTargets): the wrapper and the build must agree";
            return null;
        }

        /// <summary>
        /// The player settings a distribution build sets, put back when disposed -- the identifier, the version name
        /// and code, the custom keystore, its name and the alias, the SDK levels, the install location and the ordinary
        /// log line's stack trace -- and the two passwords, which are cleared then.
        /// </summary>
        public sealed class SettingsScope : IDisposable
        {
            private readonly string _identifier, _versionName, _keystore, _alias;
            private readonly int _versionCode;
            private readonly bool _customKeystore;
            private readonly AndroidSdkVersions _minSdk, _targetSdk;
            private readonly AndroidPreferredInstallLocation _installLocation;
            private readonly StackTraceLogType _logStackTrace;
            private bool _disposed;

            public SettingsScope(Request request, string keystorePassword, string keyPassword)
            {
                _identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
                _versionName = PlayerSettings.bundleVersion;
                _versionCode = PlayerSettings.Android.bundleVersionCode;
                _customKeystore = PlayerSettings.Android.useCustomKeystore;
                _keystore = PlayerSettings.Android.keystoreName;
                _alias = PlayerSettings.Android.keyaliasName;
                _minSdk = PlayerSettings.Android.minSdkVersion;
                _targetSdk = PlayerSettings.Android.targetSdkVersion;
                _installLocation = PlayerSettings.Android.preferredInstallLocation;
                _logStackTrace = PlayerSettings.GetStackTraceLogType(LogType.Log);
                PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationIdentifier);
                PlayerSettings.bundleVersion = request.versionName;
                PlayerSettings.Android.bundleVersionCode = request.versionCode;
                PlayerSettings.Android.useCustomKeystore = true;
                PlayerSettings.Android.keystoreName = request.keystore;
                PlayerSettings.Android.keyaliasName = request.keyAlias;
                PlayerSettings.Android.keystorePass = keystorePassword;
                PlayerSettings.Android.keyaliasPass = keyPassword;
                PlayerSettings.Android.minSdkVersion = MinSdk;
                PlayerSettings.Android.targetSdkVersion = TargetSdk;   // named, never the automatic (highest installed) level
                PlayerSettings.Android.preferredInstallLocation = InstallLocation;
                PlayerSettings.SetStackTraceLogType(LogType.Log, LogStackTrace);
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
                PlayerSettings.Android.minSdkVersion = _minSdk;
                PlayerSettings.Android.targetSdkVersion = _targetSdk;
                PlayerSettings.Android.preferredInstallLocation = _installLocation;
                PlayerSettings.SetStackTraceLogType(LogType.Log, _logStackTrace);
            }
        }

        /// <summary>The receiver a distribution sends to (D-244): the production Worker's ingest endpoint, HTTPS.</summary>
        public const string StatisticsEndpoint = "https://zantetsuken-telemetry-api.hogehogeo.workers.dev/v1/telemetry";

        /// <summary>
        /// What the project holds of the play statistics' configuration, as the build would take it (TL review 2026-10-18
        /// [P1]): every asset of the configuration's type anywhere, every asset of any type under a Resources folder by
        /// the name the runtime loads, and what the runtime's own load (<c>Resources.Load</c> by that name and type)
        /// returns, with its path.
        /// </summary>
        public sealed class StatisticsConfigFound
        {
            public string[] typedPaths = new string[0];
            public string[] namedPaths = new string[0];
            public Zantetsu.Sandbox.PlayStatisticsConfig loaded;
            public string loadedPath;

            public static StatisticsConfigFound InThisProject()
            {
                var found = new StatisticsConfigFound();
                found.typedPaths = AssetDatabase.FindAssets("t:" + nameof(Zantetsu.Sandbox.PlayStatisticsConfig))
                    .Select(AssetDatabase.GUIDToAssetPath).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray();
                found.namedPaths = AssetDatabase.FindAssets(Zantetsu.Sandbox.PlayStatisticsConfig.ResourceName)
                    .Select(AssetDatabase.GUIDToAssetPath).Distinct()
                    .Where(p => string.Equals(Path.GetFileNameWithoutExtension(p), Zantetsu.Sandbox.PlayStatisticsConfig.ResourceName, StringComparison.OrdinalIgnoreCase)
                        && p.Replace('\\', '/').Split('/').Any(part => string.Equals(part, "Resources", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(p => p, StringComparer.Ordinal).ToArray();
                found.loaded = Zantetsu.Sandbox.PlayStatisticsConfig.Load();   // the runtime's own load: the same name and type
                found.loadedPath = found.loaded != null ? AssetDatabase.GetAssetPath(found.loaded) : null;
                return found;
            }

            public string Describe() => "configs of the type: " + (typedPaths.Length == 0 ? "none" : string.Join(", ", typedPaths))
                + "; named " + Zantetsu.Sandbox.PlayStatisticsConfig.ResourceName + " under Resources: " + (namedPaths.Length == 0 ? "none" : string.Join(", ", namedPaths))
                + "; the runtime's load: " + (loaded == null ? "null" : loadedPath);
        }

        /// <summary>
        /// The play statistics' send configuration as the build will carry it (unit 9-B; TL review 2026-10-18). With
        /// "required": exactly one configuration of the type in the project, at the decided path, the only asset of
        /// the runtime's name under any Resources folder, and the one the runtime's own load returns; it must send to
        /// the decided HTTPS endpoint, hold a token, have sending on and name the external tester role. With "none":
        /// no configuration of the type anywhere, nothing of the name under Resources, and the runtime's load null --
        /// a build meant to send nothing carries no token. The token's value is never in the result.
        /// The failure, or null; <paramref name="state"/> for the log.
        /// </summary>
        public static string CheckStatisticsConfig(Request request, out string state)
        {
            return CheckStatisticsConfig(request, StatisticsConfigFound.InThisProject(), out state);
        }

        /// <summary>The same for what was found (tests give it).</summary>
        public static string CheckStatisticsConfig(Request request, StatisticsConfigFound found, out string state)
        {
            string expected = Zantetsu.Editor.Sandbox.PlayStatisticsConfigSetup.AssetPath;
            Zantetsu.Sandbox.PlayStatisticsConfig config = found.loaded;
            state = "statistics config: " + found.Describe() + (config != null ? " -- " + config.Describe() : "");
            if (request.statistics == StatisticsNone)
            {
                if (found.typedPaths.Length > 0 || found.namedPaths.Length > 0 || config != null)
                {
                    return "statistics none was asked, but the project holds a play statistics config (" + found.Describe() + "): a build that sends nothing must carry no token; move it aside";
                }

                return null;
            }

            if (found.typedPaths.Length == 0 && config == null)
            {
                return "the play statistics' config asset is absent (" + expected + "): a distribution build must be able to send (make it from the environment: Tools/Quest3s/Set-StatisticsConfig.ps1), or say " + StatisticsArgument + " none on purpose";
            }

            if (found.typedPaths.Length != 1 || !string.Equals(found.typedPaths[0], expected, StringComparison.Ordinal))
            {
                return "the project must hold exactly one play statistics config, at " + expected + " (" + found.Describe() + "): another one would be carried or loaded in its place";
            }

            if (found.namedPaths.Length != 1 || !string.Equals(found.namedPaths[0], expected, StringComparison.Ordinal))
            {
                return "another asset is named " + Zantetsu.Sandbox.PlayStatisticsConfig.ResourceName + " under a Resources folder (" + found.Describe() + "): the runtime's load could find it";
            }

            if (config == null || !string.Equals(found.loadedPath, expected, StringComparison.Ordinal))
            {
                return "the runtime's load (Resources.Load<PlayStatisticsConfig>(\"" + Zantetsu.Sandbox.PlayStatisticsConfig.ResourceName + "\")) does not return the config at " + expected + " (" + found.Describe() + ")";
            }

            if (!config.CanSend) return "the play statistics' config cannot send (" + config.Describe() + "): endpoint, token and sending on are required";
            if (!string.Equals(config.endpointUrl, StatisticsEndpoint, StringComparison.Ordinal))
            {
                return "the play statistics' config sends to '" + config.endpointUrl + "', not the decided endpoint " + StatisticsEndpoint;
            }

            if (config.testerRole != "external") return "the play statistics' config names the tester role '" + config.testerRole + "'; a distributed build must name external";
            if (config.timeoutSeconds <= 0) return "the play statistics' config has no timeout (" + config.timeoutSeconds + " s)";
            return null;
        }

        /// <summary>The Unity entry of Tools/Quest3s/Build-Distribution.ps1.</summary>
        public static void BuildApk()
        {
            string[] args = Environment.GetCommandLineArgs();
            string keystorePassword = Environment.GetEnvironmentVariable(KeystorePasswordVariable);
            string keyPassword = Environment.GetEnvironmentVariable(KeyPasswordVariable);
            string failure = TryRead(args, out Request request);
            if (failure == null) failure = Validate(request, File.Exists(request.keystore), keystorePassword, keyPassword, args);
            if (failure == null && QuestDistributionTargets.FindFeatureDevices(out _, out string targets) == null) failure = "distribution targets: " + targets;
            string statisticsState = null;
            if (failure == null) failure = CheckStatisticsConfig(request, out statisticsState);
            // The licensed inputs with their source blanked by the wrapper (unit 11): imported from disk and checked
            // before the build reads them; a build asked for them is refused when Unity does not hold them so.
            string inputSourcesState = "input sources: none named";
            string inputManifest = QuestDistributionInputSources.Value(args, QuestDistributionInputSources.ManifestArgument);
            if (failure == null && inputManifest != null)
            {
                failure = QuestDistributionInputSources.ReadManifest(inputManifest, out var inputEntries);
                if (failure == null)
                {
                    failure = QuestDistributionInputSources.Check(inputEntries, QuestDistributionInputSources.ExpectBlank,
                        QuestDistributionInputSources.Value(args, QuestDistributionInputSources.AsideArgument), out inputSourcesState);
                }
            }

            if (failure != null)
            {
                Debug.LogError("QUEST DISTRIBUTION BUILD refused: " + failure);
                if (Application.isBatchMode) EditorApplication.Exit(RefusedExitCode);
                throw new BuildFailedException(failure);
            }

            Debug.Log("QUEST DISTRIBUTION BUILD: package=" + ApplicationIdentifier + " versionName=" + request.versionName + " versionCode=" + request.versionCode
                + " keystore=" + Path.GetFileName(request.keystore) + " alias=" + request.keyAlias + " output=" + request.output + "; " + statisticsState + " (" + StatisticsArgument + " " + request.statistics + "); " + inputSourcesState
                + "; Release, commercial, custom keystore; minSdk " + (int)MinSdk + ", targetSdk " + (int)TargetSdk + ", install location " + InstallLocation + ", log stack trace " + LogStackTrace + ", the manifest's guarantees");
            bool built = false;
            try
            {
                QuestDistributionManifest.Required = true;
                using (var targetsScope = new QuestDistributionTargets.Scope())
                using (new SettingsScope(request, keystorePassword, keyPassword))
                {
                    Debug.Log("QUEST DISTRIBUTION TARGETS: " + QuestDistributionTargets.CanonicalValue + "; the feature's devices before: " + targetsScope.Before + "; during the build: " + targetsScope.During);
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
                QuestDistributionManifest.Required = false;
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
