using System;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Observability;

namespace Zantetsu.Observability.StandaloneTests
{
    /// <summary>
    /// Windows Standalone sentinel for the native graphics binding: in a real
    /// Player, on the real graphics device, the plugin loaded and holds the
    /// D3D11 device Unity is using.
    /// </summary>
    /// <remarks>
    /// This assembly is built for Windows Standalone x64 only, so the test runs
    /// where it means something instead of being ignored in the Editor. What it
    /// proves stops at the binding: no encoder session is opened, no capability
    /// is queried, no completion event is registered, and nothing is claimed
    /// about the adapter's vendor.
    /// </remarks>
    public class NvencNativeGraphicsObservationSentinelTests
    {
        [Test]
        public void Player_LoadedThePluginAndHoldsTheCurrentD3D11Device()
        {
            // The Player really is running on D3D11, which is what the plugin
            // reports about.
            Assert.That(
                SystemInfo.graphicsDeviceType,
                Is.EqualTo(GraphicsDeviceType.Direct3D11));

            Assert.That(NvencNativeGraphicsObservationBridge.IsObservable, Is.True);
            Assert.That(
                NvencNativeGraphicsObservationBridge.TryObserve(
                    out NvencGraphicsObservationV1 observation),
                Is.True);

            Assert.That(observation.IsInitialized, Is.True);
            Assert.That(observation.IsUnityPluginLoaded, Is.True);
            Assert.That(observation.HasCurrentD3D11Device, Is.True);

            // The ABI this build speaks is V1; a mismatch would have thrown
            // above rather than reaching here.
            Assert.That(NvencNativeGraphicsObservationBridge.AbiVersion, Is.EqualTo(1u));
        }

        /// <summary>
        /// One encoder session is opened on the device the plugin holds, kept
        /// while other work happens, and closed - and a second owner can do it
        /// again afterwards.
        /// </summary>
        /// <remarks>
        /// Nothing is asked of the session here: no codec, profile, input
        /// format, capability, preset, or encoder initialization. That it
        /// opened and stayed open is the whole claim.
        /// </remarks>
        [Test]
        public void Player_OpensHoldsAndClosesOneEncoderSession()
        {
            Assert.That(
                NvencNativeGraphicsObservationBridge.TryObserve(
                    out NvencGraphicsObservationV1 before),
                Is.True);
            Assert.That(before.IsUnityPluginLoaded, Is.True);
            Assert.That(before.HasCurrentD3D11Device, Is.True);

            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner owner),
                Is.True,
                "this Player's device must be able to open an encoder session.");
            Assert.That(owner, Is.Not.Null);
            Assert.That(owner.IsOpen, Is.True);

            try
            {
                // The graphics binding is untouched by the open, and the
                // session outlives other work happening in between.
                Assert.That(
                    NvencNativeGraphicsObservationBridge.TryObserve(
                        out NvencGraphicsObservationV1 during),
                    Is.True);
                Assert.That(during.IsUnityPluginLoaded, Is.True);
                Assert.That(during.HasCurrentD3D11Device, Is.True);
                Assert.That(owner.IsOpen, Is.True);

                Assert.That(NvencBringUpProfileV1.CodecId, Is.EqualTo("h264"));
                Assert.That(owner.IsOpen, Is.True);
            }
            finally
            {
                owner.Dispose();
            }

            // Closed once, and a second Dispose asks the native side for
            // nothing.
            Assert.That(owner.IsOpen, Is.False);
            owner.Dispose();
            Assert.That(owner.IsOpen, Is.False);

            // A new owner opens its own session on the same device.
            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner second),
                Is.True);
            Assert.That(second.IsOpen, Is.True);
            second.Dispose();
            Assert.That(second.IsOpen, Is.False);

            Assert.That(NvencNativeEncoderSessionOwner.AbiVersion, Is.EqualTo(1u));
        }

        /// <summary>
        /// The session this Player opened reports what its encoder supports,
        /// once, and closes normally afterwards.
        /// </summary>
        /// <remarks>
        /// The reported maximums are only required to cover the fixed
        /// 1280x720 this bring-up encodes; the device's actual limits, its
        /// model, its driver, and the number or order of the GUIDs behind the
        /// answer are not part of this contract. Nothing is initialized,
        /// registered, or allocated on the encoder here.
        /// </remarks>
        [Test]
        public void Player_ObservesItsEncoderCapabilitiesOnceThenClosesTheSession()
        {
            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner owner),
                Is.True,
                "this Player's device must be able to open an encoder session.");

            try
            {
                NvencEncoderCapabilityObservationV1 observation =
                    owner.ObserveCapabilities();

                Assert.That(observation.IsInitialized, Is.True);
                Assert.That(observation.SupportsH264Encode, Is.True);
                Assert.That(observation.SupportsH264HighProfile, Is.True);
                Assert.That(observation.SupportsNv12Input, Is.True);
                Assert.That(observation.SupportsAsyncEncode, Is.True);

                // Enough for the fixed size this bring-up encodes; the actual
                // limit is the device's business.
                Assert.That(
                    observation.MaximumEncodeWidth,
                    Is.GreaterThanOrEqualTo(NvencBringUpProfileV1.Width));
                Assert.That(
                    observation.MaximumEncodeHeight,
                    Is.GreaterThanOrEqualTo(NvencBringUpProfileV1.Height));

                // One session, one observation - and the refusal leaves the
                // session open rather than reopening anything.
                Assert.Throws<InvalidOperationException>(
                    () => owner.ObserveCapabilities());
                Assert.That(owner.IsOpen, Is.True);
            }
            finally
            {
                owner.Dispose();
            }

            Assert.That(owner.IsOpen, Is.False);

            // Closed once; disposing again asks the native side for nothing.
            owner.Dispose();
            Assert.That(owner.IsOpen, Is.False);
        }

        /// <summary>
        /// The production probe reads one bring-up capability snapshot out of
        /// a session this test owns, and that snapshot admits the fixed
        /// bring-up profile.
        /// </summary>
        /// <remarks>
        /// The session is observed only through the probe: observing it
        /// directly first would spend the one observation it has. The probe
        /// neither opens nor closes it - this test does both - and the encoder
        /// values are checked against what the bring-up requires rather than
        /// against this device's actual limits.
        /// </remarks>
        [Test]
        public void Player_ProbesTheBringUpCapabilityFromItsOpenSession()
        {
            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner owner),
                Is.True,
                "this Player's device must be able to open an encoder session.");

            try
            {
                NvencBringUpCapabilityProbe probe = new NvencBringUpCapabilityProbe(owner);
                NvencBringUpCapabilityProbeExecutionCoordinator coordinator =
                    new NvencBringUpCapabilityProbeExecutionCoordinator(probe);

                NvencBringUpCapabilityV1 capability = coordinator.Execute();

                Assert.That(capability.IsInitialized, Is.True);

                // Compared independently from the version this process
                // reports.
                Assert.That(capability.IsWindows10OrNewer, Is.True);

                // Established by the opened session.
                Assert.That(capability.IsActiveAdapterNvidia, Is.True);
                Assert.That(capability.IsCurrentGraphicsApiD3D11, Is.True);

                // The session's own observation, as the bring-up needs it.
                Assert.That(capability.ActiveAdapterSupportsAsyncEncode, Is.True);
                Assert.That(capability.ActiveAdapterSupportsH264Encode, Is.True);
                Assert.That(capability.ActiveAdapterSupportsH264HighProfile, Is.True);
                Assert.That(capability.ActiveAdapterSupportsNv12Input, Is.True);
                Assert.That(
                    capability.MaximumEncodeWidth,
                    Is.GreaterThanOrEqualTo(NvencBringUpProfileV1.Width));
                Assert.That(
                    capability.MaximumEncodeHeight,
                    Is.GreaterThanOrEqualTo(NvencBringUpProfileV1.Height));

                // What the snapshot is for: the fixed profile is admitted on
                // this machine.
                NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);
                Assert.That(
                    NvencBringUpAdmissionValidatorV1.Evaluate(
                        profile, capability, MakeCanonicalInput(profile)),
                    Is.EqualTo(NvencBringUpAdmissionDecision.Supported));

                // The session observes once, so a second execution fails -
                // and nothing is reopened or closed to make it succeed.
                Assert.Throws<InvalidOperationException>(() => coordinator.Execute());
                Assert.That(owner.IsOpen, Is.True,
                    "the probe neither closed nor replaced the session.");
            }
            finally
            {
                // The session is this test's to close, not the probe's.
                owner.Dispose();
            }

            Assert.That(owner.IsOpen, Is.False);
        }

        /// <summary>The input layout the fixed profile describes.</summary>
        private static NvencBringUpInputLayoutV1 MakeCanonicalInput(
            NvencBringUpProfileV1 profile)
        {
            return new NvencBringUpInputLayoutV1(
                profile.ProfileId,
                NvencBringUpProfileV1.Width,
                NvencBringUpProfileV1.Height,
                profile.ImageRect,
                profile.Eye,
                profile.PixelFormat,
                profile.GraphicsFormat,
                profile.ColorSpace,
                profile.SampleCount,
                profile.HasMipmaps,
                profile.HasDynamicResolution,
                profile.IsTextureArray,
                profile.ArrayIndex,
                profile.MipLevel,
                profile.Orientation);
        }
    }
}
