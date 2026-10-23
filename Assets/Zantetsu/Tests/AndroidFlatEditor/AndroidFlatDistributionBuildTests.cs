using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using Zantetsu.EditorTools.AndroidFlat;

namespace Zantetsu.AndroidFlat.Tests
{
    public sealed class AndroidFlatDistributionBuildTests
    {
        private static string[] Args(string code = "2", string mode = "commercial", params string[] extra)
        {
            var result = new List<string> { "Unity.exe", "-zantetsuDistVersionName", "0.1.0-closed.1",
                "-zantetsuDistVersionCode", code, "-zantetsuDistKeystore", "C:/keys/upload.jks",
                "-zantetsuDistKeyAlias", "upload", "-zantetsuAndroidFlatOut", "C:/out/phone.aab" };
            if (mode != null) result.AddRange(new[] { "-zantetsuBuildMode", mode });
            result.AddRange(extra);
            return result.ToArray();
        }

        [Test]
        public void RequiresOneCompleteRequestAndACommercialReleaseBundle()
        {
            Assert.That(AndroidFlatDistributionBuild.TryRead(Args(), out var request), Is.Null);
            Assert.That(request.versionCode, Is.EqualTo(2));
            Assert.That(request.output, Does.EndWith(".aab"));
            Assert.That(AndroidFlatDistributionBuild.TryRead(null, out _), Is.Not.Null);
            foreach (string flag in new[] { "-zantetsuDistVersionName", "-zantetsuDistVersionCode", "-zantetsuDistKeystore", "-zantetsuDistKeyAlias", "-zantetsuAndroidFlatOut" })
            {
                var missing = new List<string>(Args());
                int at = missing.IndexOf(flag); missing.RemoveRange(at, 2);
                Assert.That(AndroidFlatDistributionBuild.TryRead(missing.ToArray(), out _), Does.Contain(flag));
                Assert.That(AndroidFlatDistributionBuild.TryRead(Args(extra: new[] { flag, "duplicate" }), out _), Does.Contain("twice"));
            }
            foreach (string mode in new[] { null, "auto", "demo", "unknown" })
                Assert.That(AndroidFlatDistributionBuild.TryRead(Args(mode: mode), out _), Is.Not.Null);
            Assert.That(AndroidFlatDistributionBuild.TryRead(Args(extra: new[] { "-zantetsuAndroidFlatDev" }), out _), Does.Contain("Development"));
            var apk = Args(); apk[Array.IndexOf(apk, "C:/out/phone.aab")] = "C:/out/phone.apk";
            Assert.That(AndroidFlatDistributionBuild.TryRead(apk, out var phone), Is.Null);
            Assert.That(phone.IsBundle, Is.False);
            Assert.That(request.IsBundle, Is.True);
            apk[Array.IndexOf(apk, "C:/out/phone.apk")] = "C:/out/phone.zip";
            Assert.That(AndroidFlatDistributionBuild.TryRead(apk, out _), Does.Contain(".apk or .aab"));
        }

        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("+2")]
        [TestCase("2.0")]
        [TestCase("2100000001")]
        [TestCase("2147483648")]
        public void RefusesCodesOutsideThePlayRange(string code)
        {
            Assert.That(AndroidFlatDistributionBuild.TryRead(Args(code), out _), Is.Not.Null);
        }

        [Test]
        public void AcceptsThePlayMaximumAndRequiresAnExistingUploadKeyAndBothPasswords()
        {
            Assert.That(AndroidFlatDistributionBuild.TryRead(Args("2100000000"), out var request), Is.Null);
            Assert.That(AndroidFlatDistributionBuild.Validate(request, true, "test-store", "test-key"), Is.Null);
            Assert.That(AndroidFlatDistributionBuild.Validate(request, false, "test-store", "test-key"), Is.Not.Null);
            Assert.That(AndroidFlatDistributionBuild.Validate(request, true, null, "test-key"), Is.Not.Null);
            Assert.That(AndroidFlatDistributionBuild.Validate(request, true, "test-store", ""), Is.Not.Null);
            request.keystore = "C:/keys/upload.txt";
            Assert.That(AndroidFlatDistributionBuild.Validate(request, true, "test-store", "test-key"), Is.Not.Null);
        }

        [Test]
        public void SigningScopeRestoresTheProjectOnFailureAndClearsTheTransientPasswords()
        {
            string version = PlayerSettings.bundleVersion, key = PlayerSettings.Android.keystoreName, alias = PlayerSettings.Android.keyaliasName;
            var settings = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset")[0]);
            string storedKey = settings.FindProperty("AndroidKeystoreName").stringValue;
            int code = PlayerSettings.Android.bundleVersionCode;
            bool custom = PlayerSettings.Android.useCustomKeystore;
            string oldStore = Environment.GetEnvironmentVariable(AndroidFlatDistributionBuild.StorePasswordVariable);
            string oldKey = Environment.GetEnvironmentVariable(AndroidFlatDistributionBuild.KeyPasswordVariable);
            try
            {
                Environment.SetEnvironmentVariable(AndroidFlatDistributionBuild.StorePasswordVariable, "test-store");
                Environment.SetEnvironmentVariable(AndroidFlatDistributionBuild.KeyPasswordVariable, "test-key");
                AndroidFlatDistributionBuild.TryRead(Args(), out var request);
                Assert.Throws<InvalidOperationException>(() => {
                    using (new AndroidFlatDistributionBuild.SettingsScope(request))
                    {
                        Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(request.versionName));
                        Assert.That(PlayerSettings.Android.bundleVersionCode, Is.EqualTo(2));
                        Assert.That(PlayerSettings.Android.useCustomKeystore, Is.True);
                        Assert.That(PlayerSettings.Android.keyaliasName, Is.EqualTo("upload"));
                        Assert.That(PlayerSettings.Android.keystorePass, Is.EqualTo("test-store"));
                        Assert.That(PlayerSettings.Android.keyaliasPass, Is.EqualTo("test-key"));
                        throw new InvalidOperationException("simulated build failure");
                    }
                });
                Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(version));
                Assert.That(PlayerSettings.Android.bundleVersionCode, Is.EqualTo(code));
                Assert.That(PlayerSettings.Android.useCustomKeystore, Is.EqualTo(custom));
                Assert.That(PlayerSettings.Android.keystoreName, Is.EqualTo(key));
                Assert.That(PlayerSettings.Android.keyaliasName, Is.EqualTo(alias));
                settings.Update();
                Assert.That(settings.FindProperty("AndroidKeystoreName").stringValue, Is.EqualTo(storedKey));
                Assert.That(PlayerSettings.Android.keystorePass, Is.Empty);
                Assert.That(PlayerSettings.Android.keyaliasPass, Is.Empty);
            }
            finally
            {
                Environment.SetEnvironmentVariable(AndroidFlatDistributionBuild.StorePasswordVariable, oldStore);
                Environment.SetEnvironmentVariable(AndroidFlatDistributionBuild.KeyPasswordVariable, oldKey);
            }
        }
    }
}
