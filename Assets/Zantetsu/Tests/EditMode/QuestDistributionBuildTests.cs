using System;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using Zantetsu.EditorTools.Quest;
using Zantetsu.EditorTools.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The ALPHA distribution build's request (DESIGN 3.5): read from the arguments, refused before any build when a
    /// value is missing or malformed, when the keystore or a password is missing, when the mode is not commercial or
    /// a development option is given; and its settings scope puts every player setting back and clears the passwords.
    /// No build is run here.
    /// </summary>
    public sealed class QuestDistributionBuildTests
    {
        private static string[] Args(string versionName = "0.2.0", string versionCode = "3", string keystore = "C:/keys/alpha.keystore", string alias = "alpha", string output = "C:/out/alpha.apk", string devices = "quest3|quest3s", params string[] more)
        {
            var a = new System.Collections.Generic.List<string> { "Unity.exe", "-batchmode" };
            if (versionName != null) a.AddRange(new[] { QuestDistributionBuild.VersionNameArgument, versionName });
            if (versionCode != null) a.AddRange(new[] { QuestDistributionBuild.VersionCodeArgument, versionCode });
            if (keystore != null) a.AddRange(new[] { QuestDistributionBuild.KeystoreArgument, keystore });
            if (alias != null) a.AddRange(new[] { QuestDistributionBuild.KeyAliasArgument, alias });
            if (output != null) a.AddRange(new[] { QuestDistributionBuild.OutputArgument, output });
            if (devices != null) a.AddRange(new[] { QuestDistributionBuild.DevicesArgument, devices });
            a.AddRange(more);
            return a.ToArray();
        }

        [Test]
        public void TheDevices_AreRequired_AndMustBeTheDistributions()
        {
            Assert.That(QuestDistributionBuild.TryRead(Args(devices: null), out var r), Does.Contain(QuestDistributionBuild.DevicesArgument).And.Contain("missing"));
            Assert.That(QuestDistributionBuild.TryRead(Commercial(), out r), Is.Null);
            Assert.That(r.devices, Is.EqualTo("quest3|quest3s"));
            Assert.That(QuestDistributionBuild.Validate(r, true, "s", "k", Commercial()), Is.Null);
            foreach (string wrong in new[] { "quest3s|quest3", "quest3", "quest3|quest3s|quest2", "Quest3|quest3s", "eureka|quest3s" })
            {
                Assert.That(QuestDistributionBuild.TryRead(Args(devices: wrong, more: new[] { DemoBuildPolicy.ModeArgument, "commercial" }), out r), Is.Null);
                Assert.That(QuestDistributionBuild.Validate(r, true, "s", "k", Commercial()), Does.Contain("QuestDistributionTargets"), "the wrapper's '" + wrong + "' must equal the build's definition exactly");
            }
        }

        private static string[] Commercial(params string[] more)
        {
            var a = new System.Collections.Generic.List<string>(Args());
            a.AddRange(new[] { DemoBuildPolicy.ModeArgument, "commercial" });
            a.AddRange(more);
            return a.ToArray();
        }

        [Test]
        public void TheRequest_IsReadWhole_AndEveryMissingArgumentIsNamed()
        {
            Assert.That(QuestDistributionBuild.TryRead(Args(), out var r), Is.Null);
            Assert.That(new object[] { r.versionName, r.versionCode, r.keystore, r.keyAlias, r.output }, Is.EqualTo(new object[] { "0.2.0", 3, "C:/keys/alpha.keystore", "alpha", "C:/out/alpha.apk" }));
            Assert.That(QuestDistributionBuild.TryRead(Args(versionName: null), out r), Does.Contain(QuestDistributionBuild.VersionNameArgument).And.Contain("missing"));
            Assert.That(r, Is.Null);
            Assert.That(QuestDistributionBuild.TryRead(Args(versionCode: null), out r), Does.Contain(QuestDistributionBuild.VersionCodeArgument).And.Contain("missing"));
            Assert.That(QuestDistributionBuild.TryRead(Args(keystore: null), out r), Does.Contain(QuestDistributionBuild.KeystoreArgument).And.Contain("missing"));
            Assert.That(QuestDistributionBuild.TryRead(Args(alias: null), out r), Does.Contain(QuestDistributionBuild.KeyAliasArgument).And.Contain("missing"));
            Assert.That(QuestDistributionBuild.TryRead(Args(output: null), out r), Does.Contain(QuestDistributionBuild.OutputArgument).And.Contain("missing"));
            Assert.That(QuestDistributionBuild.TryRead(Args(more: new[] { QuestDistributionBuild.KeyAliasArgument, "other" }), out r), Does.Contain("twice"));
            Assert.That(QuestDistributionBuild.TryRead(new[] { "Unity.exe", QuestDistributionBuild.VersionNameArgument }, out r), Does.Contain("no value"));
            Assert.That(QuestDistributionBuild.TryRead(new[] { "Unity.exe", QuestDistributionBuild.VersionNameArgument, QuestDistributionBuild.VersionCodeArgument, "3" }, out r), Does.Contain("no value"), "the next argument is not a value");
            Assert.That(QuestDistributionBuild.TryRead(null, out r), Is.Not.Null);
        }

        [Test]
        public void TheVersionCode_IsAPositiveWholeNumberInDigitsAlone_AndTheVersionNameHasNoSpace()
        {
            foreach (string bad in new[] { "0", "-1", "abc", "1.5", "+2", " 7", "", "２", "99999999999" })
                Assert.That(QuestDistributionBuild.TryRead(Args(versionCode: bad), out _), Does.Contain(QuestDistributionBuild.VersionCodeArgument), "refused: '" + bad + "'");
            Assert.That(QuestDistributionBuild.TryRead(Args(versionCode: "1"), out var r), Is.Null);
            Assert.That(r.versionCode, Is.EqualTo(1));
            Assert.That(QuestDistributionBuild.TryRead(Args(versionCode: "2147483647"), out r), Is.Null);
            Assert.That(r.versionCode, Is.EqualTo(int.MaxValue));
            Assert.That(QuestDistributionBuild.TryRead(Args(versionCode: "2147483648"), out r), Is.Not.Null, "beyond an int");
            Assert.That(QuestDistributionBuild.TryRead(Args(versionName: "0.2.0 alpha"), out r), Does.Contain(QuestDistributionBuild.VersionNameArgument));
            Assert.That(QuestDistributionBuild.TryRead(Args(versionName: "0.2.0-alpha.1"), out r), Is.Null);
        }

        [Test]
        public void BeforeAnyBuild_TheKeystoreThePasswordsTheModeAndTheDevelopmentOption_AreChecked()
        {
            Assert.That(QuestDistributionBuild.TryRead(Commercial(), out var r), Is.Null);
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Commercial()), Is.Null, "everything given: no refusal");
            Assert.That(QuestDistributionBuild.Validate(r, false, "store-pass", "key-pass", Commercial()), Does.Contain("keystore does not exist"));
            Assert.That(QuestDistributionBuild.Validate(r, true, "", "key-pass", Commercial()), Does.Contain(QuestDistributionBuild.KeystorePasswordVariable));
            Assert.That(QuestDistributionBuild.Validate(r, true, null, "key-pass", Commercial()), Does.Contain(QuestDistributionBuild.KeystorePasswordVariable));
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "", Commercial()), Does.Contain(QuestDistributionBuild.KeyPasswordVariable));
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Args()), Does.Contain("commercial"), "no mode: refused, not auto");
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Args(more: new[] { DemoBuildPolicy.ModeArgument, "auto" })), Does.Contain("commercial"));
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Args(more: new[] { DemoBuildPolicy.ModeArgument, "demo" })), Does.Contain("commercial"));
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Args(more: new[] { DemoBuildPolicy.ModeArgument, "commercial", DemoBuildPolicy.ModeArgument, "demo" })), Is.Not.Null, "the mode given twice is the policy's refusal");
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Commercial(QuestDistributionBuild.DevelopmentArgument)), Does.Contain(QuestDistributionBuild.DevelopmentArgument));
            Assert.That(QuestDistributionBuild.TryRead(Commercial(QuestDistributionBuild.KeystoreArgument, "C:/keys/alpha.p12"), out r), Is.Not.Null, "given twice");
            Assert.That(QuestDistributionBuild.TryRead(Args(keystore: "C:/keys/alpha.p12", more: new[] { DemoBuildPolicy.ModeArgument, "commercial" }), out r), Is.Null);
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Commercial()), Does.Contain(".keystore or .jks"));
            Assert.That(QuestDistributionBuild.TryRead(Args(output: "C:/out/alpha.aab", more: new[] { DemoBuildPolicy.ModeArgument, "commercial" }), out r), Is.Null);
            Assert.That(QuestDistributionBuild.Validate(r, true, "store-pass", "key-pass", Commercial()), Does.Contain(".apk"));
            Assert.That(QuestDistributionBuild.ApplicationIdentifier, Is.EqualTo("com.zantetsuken.quest"));
        }

        [Test]
        public void TheSettingsScope_SetsTheDistributionsSettings_AndPutsEverySettingBack_WithThePasswordsCleared()
        {
            string identifier = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            string versionName = PlayerSettings.bundleVersion;
            int versionCode = PlayerSettings.Android.bundleVersionCode;
            bool custom = PlayerSettings.Android.useCustomKeystore;
            string keystore = PlayerSettings.Android.keystoreName, alias = PlayerSettings.Android.keyaliasName;
            Assert.That(QuestDistributionBuild.TryRead(Commercial(), out var r), Is.Null);
            try
            {
                using (new QuestDistributionBuild.SettingsScope(r, "store-pass", "key-pass"))
                {
                    Assert.That(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android), Is.EqualTo("com.zantetsuken.quest"));
                    Assert.That(PlayerSettings.bundleVersion, Is.EqualTo("0.2.0"));
                    Assert.That(PlayerSettings.Android.bundleVersionCode, Is.EqualTo(3));
                    Assert.That(PlayerSettings.Android.useCustomKeystore, Is.True);
                    Assert.That(PlayerSettings.Android.keystoreName, Is.EqualTo("C:/keys/alpha.keystore"));
                    Assert.That(PlayerSettings.Android.keyaliasName, Is.EqualTo("alpha"));
                    Assert.That(PlayerSettings.Android.keystorePass, Is.EqualTo("store-pass"));
                    Assert.That(PlayerSettings.Android.keyaliasPass, Is.EqualTo("key-pass"));
                }
                AssertRestored(identifier, versionName, versionCode, custom, keystore, alias);
                // A build that throws inside the scope: put back all the same.
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using (new QuestDistributionBuild.SettingsScope(r, "store-pass", "key-pass")) throw new InvalidOperationException("the build failed");
                });
                AssertRestored(identifier, versionName, versionCode, custom, keystore, alias);
            }
            finally
            {
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
            }
        }

        /// <summary>D-257: the ordinary log line carries no stack trace in a distribution build; the other kinds keep theirs, and the project's is put back.</summary>
        [Test]
        public void TheSettingsScope_TakesTheOrdinaryLogLinesStackTraceAway_AndPutsItBack()
        {
            LogType[] others = { LogType.Warning, LogType.Error, LogType.Assert, LogType.Exception };
            StackTraceLogType log = PlayerSettings.GetStackTraceLogType(LogType.Log);
            StackTraceLogType[] before = Array.ConvertAll(others, PlayerSettings.GetStackTraceLogType);
            Assert.That(QuestDistributionBuild.TryRead(Commercial(), out var r), Is.Null);
            try
            {
                // a project whose ordinary lines carry a stack trace, as this project's do
                PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.ScriptOnly);
                using (new QuestDistributionBuild.SettingsScope(r, "store-pass", "key-pass"))
                {
                    Assert.That(PlayerSettings.GetStackTraceLogType(LogType.Log), Is.EqualTo(StackTraceLogType.None));
                    Assert.That(Array.ConvertAll(others, PlayerSettings.GetStackTraceLogType), Is.EqualTo(before), "warnings, errors, assertions and exceptions keep theirs");
                }
                Assert.That(PlayerSettings.GetStackTraceLogType(LogType.Log), Is.EqualTo(StackTraceLogType.ScriptOnly), "put back");
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using (new QuestDistributionBuild.SettingsScope(r, "store-pass", "key-pass")) throw new InvalidOperationException("the build failed");
                });
                Assert.That(PlayerSettings.GetStackTraceLogType(LogType.Log), Is.EqualTo(StackTraceLogType.ScriptOnly), "put back when the build throws");
            }
            finally
            {
                PlayerSettings.SetStackTraceLogType(LogType.Log, log);
                PlayerSettings.Android.keystorePass = "";
                PlayerSettings.Android.keyaliasPass = "";
            }
        }

        private static void AssertRestored(string identifier, string versionName, int versionCode, bool custom, string keystore, string alias)
        {
            Assert.That(PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android), Is.EqualTo(identifier));
            Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(versionName));
            Assert.That(PlayerSettings.Android.bundleVersionCode, Is.EqualTo(versionCode));
            Assert.That(PlayerSettings.Android.useCustomKeystore, Is.EqualTo(custom));
            Assert.That(PlayerSettings.Android.keystoreName, Is.EqualTo(keystore));
            Assert.That(PlayerSettings.Android.keyaliasName, Is.EqualTo(alias));
            Assert.That(PlayerSettings.Android.keystorePass, Is.Empty, "the passwords are cleared");
            Assert.That(PlayerSettings.Android.keyaliasPass, Is.Empty);
        }
    }
}
