using System;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.EditorTools.Rendering;

namespace Zantetsu.Rendering.Tests
{
    public class VpClipShaderPolicyTests
    {
        private bool? _previous;

        [SetUp]
        public void SaveMode() { _previous = VpClipShaderPolicy.PixelClipOverrideForTests; }

        [TearDown]
        public void RestoreMode() { VpClipShaderPolicy.PixelClipOverrideForTests = _previous; }

        [TestCase(false, false, false, false, false)]
        [TestCase(false, true, false, true, false)]
        [TestCase(true, false, false, false, true)]
        [TestCase(true, false, true, false, true)]
        [TestCase(true, true, false, false, true)]
        [TestCase(true, true, true, false, false)]
        [TestCase(true, true, true, true, true)]
        public void DeviceSelection_RequiresEnabledFeature_AndFallsBackWhenUnobserved(
            bool vulkan, bool observed, bool enabled, bool forced, bool expected)
        {
            Assert.That(VpClipShaderPolicy.SelectPixelClipForTests(vulkan, observed, enabled, forced), Is.EqualTo(expected));
        }

        [Test]
        public void PixelMode_SelectsMatchingBakedClipAssets_AndRetainsOrdinaryAndD3DAssets()
        {
            VpClipShaderPolicy.PixelClipOverrideForTests = true;
            Assert.That(Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword), Is.True);
            int clips = 0, ordinary = 0;
            foreach (VpNativeShaderBake.Variant variant in VpNativeShaderBake.GetVariants(true))
            {
                if (variant.Name.EndsWith("-pixelclip", StringComparison.Ordinal)) continue;
                bool noClip = Array.IndexOf(variant.Keywords, VpNativeShaderBake.NoClipKeyword) >= 0;
                string selected = VpClipShaderPolicy.NativeVariant(variant.Name, true);
                Assert.That(selected, Is.EqualTo(noClip ? variant.Name : variant.Name + "-pixelclip"));
                Assert.That(Resources.Load<TextAsset>("VpNativeVulkan/" + selected), Is.Not.Null, selected);
                Assert.That(ReferenceEquals(selected, VpClipShaderPolicy.NativeVariant(variant.Name, true)), Is.True,
                    "prewarm and repeated draw selection reuse the same name");
                Assert.That(VpClipShaderPolicy.NativeVariant(selected, true), Is.EqualTo(selected));
                Assert.That(VpClipShaderPolicy.NativeVariant(variant.Name, false), Is.EqualTo(variant.Name));
                if (noClip) ordinary++; else clips++;
            }
            Assert.That(clips, Is.EqualTo(26));
            Assert.That(ordinary, Is.EqualTo(14));

            VpClipShaderPolicy.PixelClipOverrideForTests = false;
            Assert.That(Shader.IsKeywordEnabled(VpClipShaderPolicy.PixelClipKeyword), Is.False);
            Assert.That(VpClipShaderPolicy.NativeVariant("body-plugin", true), Is.EqualTo("body-plugin"));
        }
    }
}
