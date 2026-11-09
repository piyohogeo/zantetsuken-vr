using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.Rendering;
using Zantetsu.EditorTools.Sandbox;

namespace Zantetsu.Core.Tests
{
    public class WindowsScenarioPlayerPackageTests
    {
        [Test]
        public void DistributionBuildPackagesBothDesktopEntriesWithoutStartingAPlayer()
        {
            if (UnityEngine.Application.platform != UnityEngine.RuntimePlatform.WindowsEditor)
                Assert.Ignore("The desktop launcher is assembled on Windows.");
            string folder = Path.Combine(Path.GetTempPath(), "Zantetsu package test " + Guid.NewGuid().ToString("N"));
            try
            {
                WindowsScenarioPlayerPackage.BuildLauncher(folder + Path.DirectorySeparatorChar);
                Assert.That(File.Exists(Path.Combine(folder, "Zantetsu.exe")), Is.True);
                Assert.That(File.Exists(Path.Combine(folder, "起動設定.exe")), Is.True);
                Assert.That(File.Exists(Path.Combine(folder, "CutWorldSandbox.exe")), Is.False);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        [TestCase("0.1.1")]
        [TestCase("0.1.1-alpha.1")]
        [TestCase("0.1.1-alpha.1+build.3")]
        public void SuccessfulScenarioBuildUsesTheRequestedVersionAndRestoresIt(string version)
        {
            string before = PlayerSettings.bundleVersion;
            using (WindowsScenarioPlayerPackage.BuildSettings(version))
                Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(version));
            Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(before));
        }

        [Test]
        public void FailedScenarioBuildRestoresTheRequestedVersion()
        {
            string before = PlayerSettings.bundleVersion;
            Assert.Throws<InvalidOperationException>(() =>
            {
                using (WindowsScenarioPlayerPackage.BuildSettings("0.1.1-alpha.1"))
                {
                    Assert.That(PlayerSettings.bundleVersion, Is.EqualTo("0.1.1-alpha.1"));
                    throw new InvalidOperationException("Simulated build failure.");
                }
            });
            Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(before));
        }

        [TestCase("")]
        [TestCase(" ")]
        [TestCase("0.1")]
        [TestCase("0.1.1 alpha.1")]
        [TestCase("0.1.1-alpha..1")]
        [TestCase("0.1.1\n")]
        public void InvalidVersionIsRejectedBeforeChangingBuildSettings(string version)
        {
            const BuildTarget target = BuildTarget.StandaloneWindows64;
            bool automatic = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            GraphicsDeviceType[] apis = PlayerSettings.GetGraphicsAPIs(target);
            bool splash = PlayerSettings.SplashScreen.show;
            string before = PlayerSettings.bundleVersion;
            Assert.Throws<ArgumentException>(() =>
            {
                using (WindowsScenarioPlayerPackage.BuildSettings(version)) { }
            });
            Assert.That(PlayerSettings.GetUseDefaultGraphicsAPIs(target), Is.EqualTo(automatic));
            Assert.That(PlayerSettings.GetGraphicsAPIs(target), Is.EqualTo(apis));
            Assert.That(PlayerSettings.SplashScreen.show, Is.EqualTo(splash));
            Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(before));
        }

        [Test]
        public void AbsentVersionOverrideLeavesTheVersionUnderCallerControl()
        {
            string before = PlayerSettings.bundleVersion;
            try
            {
                using (WindowsScenarioPlayerPackage.BuildSettings())
                {
                    Assert.That(PlayerSettings.bundleVersion, Is.EqualTo(before));
                    PlayerSettings.bundleVersion = "0.0.0-caller";
                }
                Assert.That(PlayerSettings.bundleVersion, Is.EqualTo("0.0.0-caller"));
            }
            finally { PlayerSettings.bundleVersion = before; }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void FailedScenarioBuildRestoresTheApisAndSplashSetting(bool splashBefore)
        {
            const BuildTarget target = BuildTarget.StandaloneWindows64;
            bool automatic = PlayerSettings.GetUseDefaultGraphicsAPIs(target);
            GraphicsDeviceType[] before = PlayerSettings.GetGraphicsAPIs(target);
            bool originalSplash = PlayerSettings.SplashScreen.show;
            try
            {
                PlayerSettings.SplashScreen.show = splashBefore;
                Assert.Throws<InvalidOperationException>(() =>
                {
                    using (WindowsScenarioPlayerPackage.BuildSettings())
                    {
                        Assert.That(PlayerSettings.GetUseDefaultGraphicsAPIs(target), Is.False);
                        Assert.That(PlayerSettings.GetGraphicsAPIs(target),
                            Is.EqualTo(new[] { GraphicsDeviceType.Direct3D12, GraphicsDeviceType.Direct3D11 }));
                        Assert.That(PlayerSettings.SplashScreen.show, Is.False);
                        throw new InvalidOperationException("Simulated build failure.");
                    }
                });
                Assert.That(PlayerSettings.GetGraphicsAPIs(target), Is.EqualTo(before));
                Assert.That(PlayerSettings.GetUseDefaultGraphicsAPIs(target), Is.EqualTo(automatic));
                Assert.That(PlayerSettings.SplashScreen.show, Is.EqualTo(splashBefore));
            }
            finally { PlayerSettings.SplashScreen.show = originalSplash; }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SuccessfulScenarioBuildDisablesTheSplashAndRestoresItsSetting(bool splashBefore)
        {
            bool originalSplash = PlayerSettings.SplashScreen.show;
            try
            {
                PlayerSettings.SplashScreen.show = splashBefore;
                using (WindowsScenarioPlayerPackage.BuildSettings())
                    Assert.That(PlayerSettings.SplashScreen.show, Is.False);
                Assert.That(PlayerSettings.SplashScreen.show, Is.EqualTo(splashBefore));
            }
            finally { PlayerSettings.SplashScreen.show = originalSplash; }
        }
    }
}
