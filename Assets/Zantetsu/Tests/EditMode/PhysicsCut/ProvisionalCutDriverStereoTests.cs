using NUnit.Framework;
using UnityEngine.XR;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The stereo condition the driver gives the logical cut display before each collection: two instances per body
    /// only while an XR device renders Single Pass Instanced. Without it a Single Pass Instanced frame drew each body with
    /// its logical instance count, so instance 0 went to the left eye alone and a second instance to the right eye alone.
    /// </summary>
    public sealed class ProvisionalCutDriverStereoTests
    {
        [Test]
        public void SinglePassInstanced_OnlyWithAnActiveDeviceInThatMode()
        {
            Assert.That(ProvisionalCutDriver.RendersSinglePassInstanced(true, XRSettings.StereoRenderingMode.SinglePassInstanced), Is.True);
        }

        [TestCase(XRSettings.StereoRenderingMode.MultiPass)]
        [TestCase(XRSettings.StereoRenderingMode.SinglePass)]
        [TestCase(XRSettings.StereoRenderingMode.SinglePassMultiview)]
        public void OtherStereoModes_DrawOneInstancePerBody(XRSettings.StereoRenderingMode mode)
        {
            Assert.That(ProvisionalCutDriver.RendersSinglePassInstanced(true, mode), Is.False);
        }

        [Test]
        public void NoActiveDevice_DrawsOneInstancePerBody_WhateverTheMode()
        {
            Assert.That(ProvisionalCutDriver.RendersSinglePassInstanced(false, XRSettings.StereoRenderingMode.SinglePassInstanced), Is.False);
            Assert.That(ProvisionalCutDriver.RendersSinglePassInstanced(false, XRSettings.StereoRenderingMode.MultiPass), Is.False);
        }
    }
}
