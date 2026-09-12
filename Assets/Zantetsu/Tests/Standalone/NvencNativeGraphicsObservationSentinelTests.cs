using System;
using System.Collections;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
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

        /// <summary>
        /// The admitted session is initialized with the fixed encoder request,
        /// once, and still closes normally afterwards.
        /// </summary>
        /// <remarks>
        /// The capability snapshot and the canonical input are checked first,
        /// as the caller's own admission; the initialization does not
        /// re-evaluate them. Nothing is encoded here: no completion event,
        /// texture, input buffer, or bitstream buffer is created.
        /// </remarks>
        [Test]
        public void Player_InitializesItsEncoderOnceWithTheFixedRequest()
        {
            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner owner),
                Is.True,
                "this Player's device must be able to open an encoder session.");

            try
            {
                NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);

                // Admission first, from this very session.
                NvencBringUpCapabilityV1 capability =
                    new NvencBringUpCapabilityProbeExecutionCoordinator(
                        new NvencBringUpCapabilityProbe(owner)).Execute();

                Assert.That(
                    NvencBringUpAdmissionValidatorV1.Evaluate(
                        profile, capability, MakeCanonicalInput(profile)),
                    Is.EqualTo(NvencBringUpAdmissionDecision.Supported));

                owner.InitializeEncoder(profile);
                Assert.That(owner.IsOpen, Is.True,
                    "the initialized session is still open.");

                // One session, one initialization - and the refusal keeps the
                // session.
                Assert.Throws<InvalidOperationException>(
                    () => owner.InitializeEncoder(profile));
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
        /// The initialized session binds the production pool's eight capture
        /// render targets as its sources, prepares its fixed sets of NV12
        /// input surfaces, output bitstream buffers, and completion events,
        /// refuses to close while any of them is held, and closes once all of
        /// them are released in the reverse order.
        /// </summary>
        /// <remarks>
        /// The sources are the real render targets a Run would capture into,
        /// created by the production pool and still owned by it: what crosses
        /// the boundary is the eight native pointers, read once on the main
        /// thread, and nothing comes back. Nothing is drawn, converted,
        /// mapped, waited on, or read here, and no texture, view, registration,
        /// handle, slot, or count reaches managed code: what is pinned is the
        /// order - sources, surfaces, buffers, events, then events, buffers,
        /// surfaces, sources - and that each step happens once. The encode
        /// sentinel of a later unit is what shows all eight slots actually in
        /// use.
        /// </remarks>
        [Test]
        public void Player_PreparesAndReleasesItsSlotResources()
        {
            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner owner),
                Is.True,
                "this Player's device must be able to open an encoder session.");

            // The production pool, with the Run's own fixed capacity and
            // profile. It keeps owning its render targets throughout.
            CaptureFrameProfile captureProfile = new CaptureFrameProfile(
                7,
                45.0,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(
                    0, 0, NvencBringUpProfileV1.Width, NvencBringUpProfileV1.Height),
                0,
                CapturePixelFormat.Rgba32);

            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(
                NvencNativeEncoderSessionOwner.SourceSurfaceCount, captureProfile);

            try
            {
                NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);

                NvencBringUpCapabilityV1 capability =
                    new NvencBringUpCapabilityProbeExecutionCoordinator(
                        new NvencBringUpCapabilityProbe(owner)).Execute();
                Assert.That(
                    NvencBringUpAdmissionValidatorV1.Evaluate(
                        profile, capability, MakeCanonicalInput(profile)),
                    Is.EqualTo(NvencBringUpAdmissionDecision.Supported));

                owner.InitializeEncoder(profile);

                // The input surfaces wait for the sources, and asking too
                // early costs nothing.
                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareInputSurfaces());

                // The eight pointers, read once from the pool that owns the
                // textures. Nothing is rented to read them.
                IntPtr[] sources =
                    new IntPtr[NvencNativeEncoderSessionOwner.SourceSurfaceCount];
                pool.CopyNativeTexturePointers(sources);
                foreach (IntPtr source in sources)
                {
                    Assert.That(source, Is.Not.EqualTo(IntPtr.Zero));
                }

                owner.BindSourceSurfaces(sources);
                Assert.That(owner.IsOpen, Is.True);

                // Bound sources hold the session open, and one owner binds
                // once.
                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);
                Assert.Throws<InvalidOperationException>(
                    () => owner.BindSourceSurfaces(sources));

                // The output buffers wait for the input surfaces, and
                // asking too early costs nothing.
                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareOutputBuffers());

                owner.PrepareInputSurfaces();
                Assert.That(owner.IsOpen, Is.True);

                // Prepared surfaces hold the session open, and refusing the
                // close leaves it closable later.
                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                // One session, one preparation.
                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareInputSurfaces());

                // The output buffers wait for the conversion commands, and
                // asking too early costs nothing.
                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareOutputBuffers());

                owner.PrepareConversionCommands();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareConversionCommands());

                owner.PrepareOutputBuffers();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareOutputBuffers());

                owner.PrepareCompletionEvents();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.PrepareCompletionEvents());

                // The events go before the buffers and the buffers before
                // the surfaces, and asking too early costs nothing: the same
                // owner still releases them after.
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseOutputBuffers());
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseConversionCommands());
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseInputSurfaces());
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseSourceSurfaces());
                Assert.That(owner.IsOpen, Is.True);

                owner.ReleaseCompletionEvents();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseCompletionEvents());

                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseInputSurfaces());

                owner.ReleaseOutputBuffers();
                Assert.That(owner.IsOpen, Is.True);

                // One session, one release.
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseOutputBuffers());

                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseSourceSurfaces());
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseInputSurfaces());

                owner.ReleaseConversionCommands();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseConversionCommands());

                owner.ReleaseInputSurfaces();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseInputSurfaces());

                // Bound sources alone still hold the session open, and
                // refusing that close spends nothing.
                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                owner.ReleaseSourceSurfaces();
                Assert.That(owner.IsOpen, Is.True);

                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseSourceSurfaces());
                Assert.That(owner.IsOpen, Is.True);
            }
            finally
            {
                owner.Dispose();

                // The pool owned its render targets the whole way through and
                // disposes them itself.
                pool.Dispose();
            }

            Assert.That(owner.IsOpen, Is.False);

            // Closed once; disposing again asks the native side for nothing.
            owner.Dispose();
            Assert.That(owner.IsOpen, Is.False);
        }

        /// <summary>
        /// The Player issues real conversion render events: every sync slot
        /// runs, one slot is reused sixteen times, two commands with different
        /// source and sample slots ride the same frame, a worker collects each
        /// completion off the render thread, and Unity keeps rendering
        /// afterwards.
        /// </summary>
        /// <remarks>
        /// Nothing is mapped, encoded, or read back here: what is pinned is
        /// that the callback really runs through Unity's render thread, that a
        /// completion is distinguished by its generation rather than by time,
        /// that two commands in one frame do not overwrite each other's event
        /// data, and that the session refuses to give up its resources while a
        /// command is uncollected.
        /// </remarks>
        [UnityTest]
        public IEnumerator Player_RunsAndCollectsItsConversionCommands()
        {
            Assert.That(
                NvencNativeEncoderSessionOwner.TryOpen(
                    out NvencNativeEncoderSessionOwner owner),
                Is.True,
                "this Player's device must be able to open an encoder session.");

            CaptureFrameProfile captureProfile = new CaptureFrameProfile(
                7,
                45.0,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(
                    0, 0, NvencBringUpProfileV1.Width, NvencBringUpProfileV1.Height),
                0,
                CapturePixelFormat.Rgba32);

            CaptureFrameRenderTargetPool pool = new CaptureFrameRenderTargetPool(
                NvencNativeEncoderSessionOwner.SourceSurfaceCount, captureProfile);

            bool prepared = false;

            try
            {
                NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);
                new NvencBringUpCapabilityProbeExecutionCoordinator(
                    new NvencBringUpCapabilityProbe(owner)).Execute();
                owner.InitializeEncoder(profile);

                IntPtr[] sources =
                    new IntPtr[NvencNativeEncoderSessionOwner.SourceSurfaceCount];
                pool.CopyNativeTexturePointers(sources);
                owner.BindSourceSurfaces(sources);
                owner.PrepareInputSurfaces();
                owner.PrepareConversionCommands();
                prepared = true;

                // Every sync slot runs one command, each against its own
                // source and its own encode sample slot.
                for (int slot = 0;
                    slot < NvencNativeEncoderSessionOwner.ConversionCommandSlotCount;
                    slot++)
                {
                    owner.IssueConversionCommand(slot, slot, slot, 1);
                }

                // A command that has been issued but not collected holds the
                // session's resources, and refusing that costs nothing.
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseConversionCommands());
                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                for (int slot = 0;
                    slot < NvencNativeEncoderSessionOwner.ConversionCommandSlotCount;
                    slot++)
                {
                    yield return CollectOnWorker(owner, slot, 1, result =>
                        Assert.That(
                            result, Is.True,
                            "sync slot " + slot + " must complete its first command."));
                }

                // One slot, sixteen reuses. Only the generation tells them
                // apart, and an older one is never accepted as this one.
                for (ulong generation = 2; generation <= 17; generation++)
                {
                    ulong current = generation;

                    Assert.That(
                        owner.TryCollectConversionCommand(0, current, 0),
                        Is.False,
                        "a generation that has not been issued must not complete.");

                    owner.IssueConversionCommand(0, 0, 0, current);

                    Assert.That(
                        owner.TryCollectConversionCommand(0, current - 1, 0),
                        Is.False,
                        "an older generation must not be satisfied by a newer signal.");

                    yield return CollectOnWorker(owner, 0, current, result =>
                        Assert.That(
                            result, Is.True,
                            "generation " + current + " must complete."));

                    Assert.That(
                        owner.TryCollectConversionCommand(0, current, 0),
                        Is.False,
                        "a completion is collected once.");
                }

                // Two commands in one frame, on different sync slots, with
                // different sources and different sample slots: each carries
                // its own event data, so neither overwrites the other.
                owner.IssueConversionCommand(2, 3, 5, 100);
                owner.IssueConversionCommand(6, 1, 7, 200);

                yield return CollectOnWorker(owner, 2, 100, result =>
                    Assert.That(result, Is.True, "the first same-frame command completes."));
                yield return CollectOnWorker(owner, 6, 200, result =>
                    Assert.That(result, Is.True, "the second same-frame command completes."));

                // Unity still renders after the callbacks handed the pipeline
                // back.
                yield return null;
                AssertUnityStillRenders();
            }
            finally
            {
                if (prepared)
                {
                    owner.ReleaseConversionCommands();
                    owner.ReleaseInputSurfaces();
                    owner.ReleaseSourceSurfaces();
                }

                owner.Dispose();
                pool.Dispose();
            }

            Assert.That(owner.IsOpen, Is.False);
        }

        /// <summary>
        /// Collects one completion on a worker thread - never the main thread -
        /// and reports what it got once that thread has finished.
        /// </summary>
        private static IEnumerator CollectOnWorker(
            NvencNativeEncoderSessionOwner owner,
            int syncSlotIndex,
            ulong generation,
            Action<bool> check)
        {
            bool collected = false;
            Thread worker = new Thread(() =>
            {
                collected = owner.TryCollectConversionCommand(
                    syncSlotIndex, generation, 5000);
            });

            worker.Start();
            while (worker.IsAlive)
            {
                yield return null;
            }

            worker.Join();
            check(collected);
        }

        /// <summary>
        /// Draws one known colour through Unity's own pipeline and reads it
        /// back, so a callback that left the pipeline broken would show up.
        /// </summary>
        private static void AssertUnityStillRenders()
        {
            RenderTexture target = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
            Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            Texture2D readback = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            RenderTexture previous = RenderTexture.active;

            try
            {
                source.SetPixel(0, 0, new Color32(0, 255, 0, 255));
                source.Apply();

                Graphics.Blit(source, target);

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, 4, 4), 0, 0);
                readback.Apply();

                Color32 pixel = readback.GetPixel(2, 2);
                Assert.That(pixel.g, Is.GreaterThan((byte)200));
                Assert.That(pixel.r, Is.LessThan((byte)64));
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(readback);
                UnityEngine.Object.Destroy(source);
                target.Release();
                UnityEngine.Object.Destroy(target);
            }
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
