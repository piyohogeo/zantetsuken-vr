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
