using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Phase 0.5 / T-014: the Windows Standalone build target must actually
    /// carry the OpenXR loader and the Single Pass stereo setting the Quest
    /// Link smoke run is verified against.
    /// </summary>
    public class StandaloneOpenXrConfigurationTests
    {
        private const BuildTargetGroup Group = BuildTargetGroup.Standalone;

        private static XRGeneralSettings StandaloneGeneralSettings()
        {
            Assert.That(
                EditorBuildSettings.TryGetConfigObject(XRGeneralSettings.k_SettingsKey, out XRGeneralSettingsPerBuildTarget perTarget),
                Is.True,
                "XR Plug-in Management settings are not registered in EditorBuildSettings.");
            Assert.That(perTarget, Is.Not.Null);

            XRGeneralSettings general = perTarget.SettingsForBuildTarget(Group);
            Assert.That(general, Is.Not.Null, "No XR general settings for the Standalone build target.");
            return general;
        }

        [Test]
        public void Standalone_XrLoaderList_ContainsTheOpenXrLoaderAndInitializesOnStart()
        {
            XRGeneralSettings general = StandaloneGeneralSettings();

            Assert.That(general.InitManagerOnStart, Is.True, "XR is not initialized on start for Standalone.");
            Assert.That(general.Manager, Is.Not.Null);

            IReadOnlyList<XRLoader> loaders = general.Manager.activeLoaders;
            Assert.That(loaders, Is.Not.Null.And.Not.Empty, "No XR loader is assigned to Standalone.");

            bool hasOpenXr = false;
            foreach (XRLoader loader in loaders)
            {
                if (loader is OpenXRLoaderBase)
                {
                    hasOpenXr = true;
                    break;
                }
            }

            Assert.That(hasOpenXr, Is.True, "The OpenXR loader is not assigned to the Standalone build target.");
        }

        [Test]
        public void Standalone_OpenXrRenderMode_IsSinglePassInstanced()
        {
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(Group);
            Assert.That(settings, Is.Not.Null, "No OpenXR settings for the Standalone build target.");
            Assert.That(settings.renderMode, Is.EqualTo(OpenXRSettings.RenderMode.SinglePassInstanced));
        }

        [Test]
        public void Standalone_StereoRenderingPath_IsInstancing()
        {
            Assert.That(PlayerSettings.stereoRenderingPath, Is.EqualTo(StereoRenderingPath.Instancing));
        }

        [Test]
        public void Standalone_OculusTouchControllerProfile_IsTheEnabledInteractionProfile()
        {
            OpenXRSettings settings = OpenXRSettings.GetSettingsForBuildTargetGroup(Group);
            Assert.That(settings, Is.Not.Null, "No OpenXR settings for the Standalone build target.");

            OculusTouchControllerProfile profile = settings.GetFeature<OculusTouchControllerProfile>();
            Assert.That(profile, Is.Not.Null, "The Oculus Touch Controller Profile feature is missing.");
            Assert.That(profile.enabled, Is.True,
                "The Quest 3S right-hand controller needs an enabled interaction profile.");
        }
    }
}
