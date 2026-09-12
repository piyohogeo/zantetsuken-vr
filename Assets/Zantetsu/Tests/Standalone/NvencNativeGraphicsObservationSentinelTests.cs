using System;
using System.Collections;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.IO;
using System.Runtime.InteropServices;
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
        /// <summary>
        /// Frame and wall-clock budgets for the two-worker sentinel. Both are
        /// bounds on this test, never on the pipeline: nothing in the product
        /// is timed, retried, or given up on because of them.
        /// </summary>
        private const int SentinelWatchdogMs = 30000;

        private const int SentinelIterations = 32;

        private const long SentinelTestRunId = 1;

        private const string SentinelRunInitId = "0123456789abcdef0123456789abcdef";

        private const string SentinelChunkHash =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

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
                    Assert.That(owner.TryIssueConversionCommand(slot, slot, slot, 1), Is.True);
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

                    // A generation this slot does not hold is a caller asking
                    // about work that is not there - never "not yet".
                    Assert.Throws<InvalidOperationException>(
                        () => owner.TryCollectConversionCommand(0, current, 0),
                        "a generation that has not been issued is not a pending completion.");

                    Assert.That(owner.TryIssueConversionCommand(0, 0, 0, current), Is.True);

                    Assert.Throws<InvalidOperationException>(
                        () => owner.TryCollectConversionCommand(0, current - 1, 0),
                        "an older generation is not satisfied by a newer signal.");

                    yield return CollectOnWorker(owner, 0, current, result =>
                        Assert.That(
                            result, Is.True,
                            "generation " + current + " must complete."));

                    Assert.Throws<InvalidOperationException>(
                        () => owner.TryCollectConversionCommand(0, current, 0),
                        "a completion is collected once.");
                }

                // Two commands in one frame, on different sync slots, with
                // different sources and different sample slots: each carries
                // its own event data, so neither overwrites the other.
                Assert.That(owner.TryIssueConversionCommand(2, 3, 5, 100), Is.True);
                Assert.That(owner.TryIssueConversionCommand(6, 1, 7, 200), Is.True);

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
        /// The production completion source, against the real session and the
        /// real render callback: it says no before the conversion is issued,
        /// converges to yes after it, and the evidence it hands back binds
        /// exactly that source, work, sync lease, and surface.
        /// </summary>
        /// <remarks>
        /// One logical submission is built here - not eight, and not the submit
        /// or output pipeline around it - because what is being checked is the
        /// boundary between one sync lease and one native conversion.
        /// </remarks>
        [UnityTest]
        public IEnumerator Player_ProductionCompletionSourceReportsTheRealConversion()
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

            NvencCaptureProcessState processState = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workSlots = new NvencCaptureWorkSlotPool(processState);
            NvencEncodeSampleSlotPool sampleSlots = new NvencEncodeSampleSlotPool(processState);
            NvencGpuConversionSyncPool syncSlots = new NvencGpuConversionSyncPool(processState);
            NvencSubmitToOutputCreditPool submitCredits =
                new NvencSubmitToOutputCreditPool(processState);
            NvencFrameCompletionCreditPool completionCredits =
                new NvencFrameCompletionCreditPool(processState);
            Guid backendOwner = Guid.NewGuid();

            bool prepared = false;
            CaptureSurfaceLease builtSurface = null;
            CaptureFrameWorkToken builtToken = default;

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

                // One logical submission, from the real pools.
                Assert.That(workSlots.TryRent(out NvencCaptureWorkSlotLease work), Is.True);
                Assert.That(sampleSlots.TryRent(out NvencEncodeSampleSlotLease sample), Is.True);
                Assert.That(syncSlots.TryRent(out NvencGpuConversionSyncLease sync), Is.True);
                Assert.That(
                    submitCredits.TryRent(out NvencSubmitToOutputCreditLease submitCredit),
                    Is.True);
                Assert.That(
                    completionCredits.TryRent(
                        out NvencFrameCompletionCreditLease completionCredit),
                    Is.True);
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease rented), Is.True);

                // The lease's generation is the positive, forward-only number
                // the conversion is armed with; nothing translates it.
                Assert.That(sync.Generation, Is.GreaterThan(0L));

                CaptureFrameWorkToken workToken = new CaptureFrameWorkToken(
                    backendOwner, work.SlotIndex, work.Generation, 1, 1);
                CaptureSurfaceLease surface = new CaptureSurfaceLease(pool, rented);
                surface.TransferToBackend(backendOwner, workToken);
                builtSurface = surface;
                builtToken = workToken;

                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    backendOwner, workToken, work, sample, sync, submitCredit,
                    completionCredit, surface, workSlots, sampleSlots, syncSlots,
                    submitCredits, completionCredits);

                NvencNativeSourceReadCompletedSource completionSource =
                    new NvencNativeSourceReadCompletedSource(owner);

                // No conversion has been armed for this lease, so asking about
                // it is a caller asking about work that is not there. That is a
                // broken contract, not a completion still to come - answering
                // "not yet" would have a caller wait for something that is never
                // coming.
                Assert.Throws<InvalidOperationException>(
                    () => completionSource.TryGetEvidence(
                        record, out NvencSourceReadCompletedEvidence _),
                    "a record whose conversion was never issued is not pending.");

                Assert.That(owner.TryIssueConversionCommand(
                    sync.SlotIndex, 0, sample.SlotIndex, (ulong)sync.Generation), Is.True);

                // It converges, within a bounded number of frames rather than a
                // blocking wait. Every attempt that is not the completion is a
                // plain false with default evidence.
                NvencSourceReadCompletedEvidence evidence = default;
                bool completed = false;
                for (int attempt = 0; attempt < 600 && !completed; attempt++)
                {
                    completed = completionSource.TryGetEvidence(record, out evidence);
                    if (!completed)
                    {
                        Assert.That(evidence.IsValid, Is.False);
                        yield return null;
                    }
                }

                Assert.That(
                    completed, Is.True,
                    "the issued conversion must complete within the watchdog.");

                Assert.That(evidence.IsValid, Is.True);
                Assert.That(evidence.Matches(completionSource, record), Is.True);
                Assert.That(evidence.Source, Is.SameAs(completionSource));
                Assert.That(evidence.Surface, Is.SameAs(surface));
                Assert.That(evidence.WorkToken.IdenticalTo(workToken), Is.True);
                Assert.That(evidence.SyncSlot.SlotIndex, Is.EqualTo(sync.SlotIndex));
                Assert.That(evidence.SyncSlot.Generation, Is.EqualTo(sync.Generation));

                // The completion belongs to that one command: asking again is
                // not a second proof, it is a broken contract.
                Assert.Throws<InvalidOperationException>(
                    () => completionSource.TryGetEvidence(
                        record, out NvencSourceReadCompletedEvidence _));
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

                // The one surface this test rented goes back before the pool it
                // came from is disposed.
                if (builtSurface != null && builtSurface.IsCreated)
                {
                    if (builtSurface.IsBackendOwned)
                    {
                        builtSurface.ReleaseFromBackend(backendOwner, builtToken);
                    }
                    else
                    {
                        builtSurface.Dispose();
                    }
                }

                pool.Dispose();
            }

            Assert.That(owner.IsOpen, Is.False);
        }

        /// <summary>
        /// One whole frame through the production adapters on the real device:
        /// the conversion completes, the picture is submitted, its completion
        /// is awaited, and the access unit is copied out - leaving the slot
        /// usable and the session closable.
        /// </summary>
        /// <remarks>
        /// The bytes are only checked for what this unit can honestly claim: a
        /// length inside the fixed storage and an Annex-B start code. No
        /// parsing, no NAL classification, no colour, no decoder comparison,
        /// and one frame rather than a qualification run.
        /// </remarks>
        [UnityTest]
        public IEnumerator Player_EncodesAndCollectsOneFrameThroughTheProductionAdapters()
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

            NvencCaptureProcessState processState = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workSlots = new NvencCaptureWorkSlotPool(processState);
            NvencEncodeSampleSlotPool sampleSlots = new NvencEncodeSampleSlotPool(processState);
            NvencGpuConversionSyncPool syncSlots = new NvencGpuConversionSyncPool(processState);
            NvencSubmitToOutputCreditPool submitCredits =
                new NvencSubmitToOutputCreditPool(processState);
            NvencFrameCompletionCreditPool completionCredits =
                new NvencFrameCompletionCreditPool(processState);
            Guid backendOwner = Guid.NewGuid();

            bool prepared = false;
            CaptureSurfaceLease builtSurface = null;
            CaptureFrameWorkToken builtToken = default;

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
                owner.PrepareOutputBuffers();
                owner.PrepareCompletionEvents();
                prepared = true;

                Assert.That(workSlots.TryRent(out NvencCaptureWorkSlotLease work), Is.True);
                Assert.That(
                    sampleSlots.TryRent(out NvencEncodeSampleSlotLease sample), Is.True);
                Assert.That(syncSlots.TryRent(out NvencGpuConversionSyncLease sync), Is.True);
                Assert.That(
                    submitCredits.TryRent(out NvencSubmitToOutputCreditLease submitCredit),
                    Is.True);
                Assert.That(
                    completionCredits.TryRent(
                        out NvencFrameCompletionCreditLease completionCredit),
                    Is.True);
                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease rented), Is.True);

                CaptureFrameWorkToken workToken = new CaptureFrameWorkToken(
                    backendOwner, work.SlotIndex, work.Generation, 1, 1);
                CaptureSurfaceLease surface = new CaptureSurfaceLease(pool, rented);
                surface.TransferToBackend(backendOwner, workToken);
                builtSurface = surface;
                builtToken = workToken;

                NvencNativeSourceReadCompletedSource completionSource =
                    new NvencNativeSourceReadCompletedSource(owner);

                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    backendOwner, workToken, work, sample, sync, submitCredit,
                    completionCredit, surface, workSlots, sampleSlots, syncSlots,
                    submitCredits, completionCredits);

                // The picture is only meaningful once its conversion has
                // actually written the surface, and the conversion is issued
                // the way the admission boundary issues it: through the
                // production issuer, from the record itself.
                NvencNativeGpuConversionCommandIssuer issuer =
                    new NvencNativeGpuConversionCommandIssuer(owner);
                Assert.That(
                    issuer.TryIssue(record), Is.True,
                    "the production issuer must issue this record's conversion.");

                bool converted = false;
                for (int attempt = 0; attempt < 600 && !converted; attempt++)
                {
                    converted = completionSource.TryGetEvidence(
                        record, out NvencSourceReadCompletedEvidence _);
                    if (!converted)
                    {
                        yield return null;
                    }
                }

                Assert.That(
                    converted, Is.True, "the conversion must complete before encoding.");

                // The production adapters, from here on.
                NvencNativeEncodePictureSubmitter submitter =
                    new NvencNativeEncodePictureSubmitter(owner);
                NvencNativeOutputBitstreamSource outputSource =
                    new NvencNativeOutputBitstreamSource(owner);

                NvencEncodePictureSubmitOperation operation =
                    NvencEncodePictureSubmitOperation.Create(record, workSlots, sampleSlots);

                Assert.That(
                    submitter.TrySubmit(operation), Is.True,
                    "the converted surface must map and submit one picture.");

                // The driver has the frame: nothing it was built on may go yet.
                Assert.Throws<InvalidOperationException>(
                    () => owner.ReleaseCompletionEvents());
                Assert.Throws<InvalidOperationException>(() => owner.Dispose());
                Assert.That(owner.IsOpen, Is.True);

                byte[] accessUnit =
                    new byte[(int)NvencBringUpProfileV1.MaxAccessUnitByteLength];

                Assert.That(
                    outputSource.TryCopyCompletedOutput(
                        workToken, sample, accessUnit, accessUnit.Length,
                        out int validLength),
                    Is.True,
                    "the submitted picture's access unit must copy out.");

                Assert.That(validLength, Is.GreaterThanOrEqualTo(1));
                Assert.That(
                    validLength,
                    Is.LessThanOrEqualTo((int)NvencBringUpProfileV1.MaxAccessUnitByteLength));

                // Annex-B and nothing more.
                Assert.That(validLength, Is.GreaterThanOrEqualTo(4));
                Assert.That(accessUnit[0], Is.EqualTo((byte)0x00));
                Assert.That(accessUnit[1], Is.EqualTo((byte)0x00));
                bool startCode = accessUnit[2] == 0x01 ||
                    (accessUnit[2] == 0x00 && accessUnit[3] == 0x01);
                Assert.That(startCode, Is.True, "the access unit must start with Annex-B.");
            }
            finally
            {
                if (prepared)
                {
                    owner.ReleaseCompletionEvents();
                    owner.ReleaseOutputBuffers();
                    owner.ReleaseConversionCommands();
                    owner.ReleaseInputSurfaces();
                    owner.ReleaseSourceSurfaces();
                }

                owner.Dispose();

                if (builtSurface != null && builtSurface.IsCreated)
                {
                    if (builtSurface.IsBackendOwned)
                    {
                        builtSurface.ReleaseFromBackend(backendOwner, builtToken);
                    }
                    else
                    {
                        builtSurface.Dispose();
                    }
                }

                pool.Dispose();
            }

            Assert.That(owner.IsOpen, Is.False);
        }

        /// <summary>
        /// Repeats one frame through both real workers on the real device to
        /// exercise conversion, rendering overlap and session teardown: admission
        /// issues the conversion, the Submit Worker waits for it on its own
        /// thread and submits the picture, the enqueue that follows wakes the
        /// Output Worker, and that worker waits for the encoder's completion
        /// event, copies the access unit out, appends it, and publishes one
        /// Frame Completion.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every stage is the production type - the admission coordinator with
        /// the native issuer, the native completion source behind the release
        /// coordinator, the native submitter behind the Submit Processor, the
        /// native output source behind the collector, and both worker services
        /// - composed once, here, in the one order they can be composed in.
        /// The chunk appender/finalizer, teardown receipts and diagnostic
        /// decorators belong to this fixture. Decorators preserve production
        /// calls and record which worker actually made them. Each iteration
        /// creates and closes one session, without a factory or registry.
        /// </para>
        /// <para>
        /// The Main Thread advances only the two non-waiting entries this path
        /// has - applying a handed-back release and telling the Submit Worker
        /// that something may have changed - and never waits on a native
        /// handle: the completion event is waited on by the Output Worker
        /// alone, on its own thread. A watchdog that expires reports the last
        /// stage actually reached and then leaves everything alone, because a
        /// worker still inside the driver cannot be joined, disposed, or
        /// released around.
        /// </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator Player_RepeatsOneFrameThroughBothWorkers()
        {
            for (int iteration = 1; iteration <= SentinelIterations; iteration++)
            {
                SentinelMarker("iteration enter " + iteration + "/" + SentinelIterations);
                yield return RunOneFrameThroughBothWorkers();
            }
        }

        private static IEnumerator RunOneFrameThroughBothWorkers()
        {
            NvencNativeEncoderSessionOwner owner = null;
            CaptureFrameRenderTargetPool pool = null;

            CaptureFrameProfile captureProfile = new CaptureFrameProfile(
                7,
                45.0,
                CaptureSource.UnityRenderTexture,
                CaptureEye.Left,
                new CaptureImageRect(
                    0, 0, NvencBringUpProfileV1.Width, NvencBringUpProfileV1.Height),
                0,
                CapturePixelFormat.Rgba32);

            NvencCaptureProcessState processState = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workSlots = new NvencCaptureWorkSlotPool(processState);
            NvencEncodeSampleSlotPool sampleSlots = new NvencEncodeSampleSlotPool(processState);
            NvencGpuConversionSyncPool syncSlots = new NvencGpuConversionSyncPool(processState);
            NvencSubmitToOutputCreditPool submitCredits =
                new NvencSubmitToOutputCreditPool(processState);
            NvencFrameCompletionCreditPool completionCredits =
                new NvencFrameCompletionCreditPool(processState);
            NvencFixedSpscQueue<NvencSubmissionRecord> submissionQueue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();
            NvencFixedSpscQueue<NvencSubmitToOutputRecord> outputQueue =
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
            Guid backendOwner = Guid.NewGuid();

            SentinelChunkWriter writer = new SentinelChunkWriter();
            SentinelOutputWorkerTeardown teardown = new SentinelOutputWorkerTeardown();

            bool teardownDone = false;
            bool stalled = false;
            string stalledAt = null;
            CaptureSurfaceLease surface = null;
            CaptureFrameWorkToken workToken = default;
            NvencOrderedSubmitWorkerService submitWorker = null;
            NvencOrderedOutputWorkerService outputWorker = null;

            // Root every resource even if the coroutine is abandoned after a
            // failure. An uncertain native operation is only reclaimed by the
            // external harness terminating this dedicated Player process.
            object[] ownerGraph = new object[6];
            ownerGraph[5] = processState;
            GCHandle lifetimeRoot = GCHandle.Alloc(ownerGraph);
            try
            {
                Assert.That(NvencNativeEncoderSessionOwner.TryOpen(out owner), Is.True,
                    "this Player's device must be able to open an encoder session.");
                ownerGraph[0] = owner;
                pool = new CaptureFrameRenderTargetPool(
                    NvencNativeEncoderSessionOwner.SourceSurfaceCount, captureProfile);
                ownerGraph[1] = pool;
                // The native session, in the established preparation order.
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
                owner.PrepareOutputBuffers();
                owner.PrepareCompletionEvents();

                NvencSourceResourceReleaseCoordinator releaseCoordinator =
                    new NvencSourceResourceReleaseCoordinator(
                        processState, workSlots, sampleSlots, syncSlots,
                        submitCredits, completionCredits,
                        new NvencNativeSourceReadCompletedSource(owner),
                        new NvencSourceSurfaceReturnBoundary(), backendOwner);

                // One buffer, one sink, one collector, one completion boundary:
                // the Output Processor and the Run chunk context must be
                // talking about the same ones.
                NvencOwnedAccessUnitBuffer accessUnitBuffer =
                    new NvencOwnedAccessUnitBuffer(processState);
                NvencRunChunkSink sink =
                    new NvencRunChunkSink(processState, accessUnitBuffer, writer);
                NvencRunChunkContext context = new NvencRunChunkContext(
                    MakeRunIssue(),
                    sink,
                    new NvencRunChunkFinalizationCoordinator(writer),
                    "chunk/0");
                SentinelOutputBitstreamSource outputSource = new SentinelOutputBitstreamSource(owner);
                NvencSubmittedOutputCollector collector = new NvencSubmittedOutputCollector(
                    processState, workSlots, sampleSlots, submitCredits, completionCredits,
                    accessUnitBuffer, outputSource);
                NvencFrameCompletionBoundary completionBoundary =
                    new NvencFrameCompletionBoundary(
                        processState, workSlots, sampleSlots, submitCredits, completionCredits,
                        accessUnitBuffer);

                // ---- the one composition order, fixed in one place ----
                // 1. Submit Processor
                SentinelEncodePictureSubmitter submitter = new SentinelEncodePictureSubmitter(owner);
                NvencOrderedSubmitProcessor submitProcessor = new NvencOrderedSubmitProcessor(
                    processState, submissionQueue, outputQueue, workSlots, sampleSlots,
                    releaseCoordinator, submitter);

                // 2. Submit Worker
                submitWorker = new NvencOrderedSubmitWorkerService(processState, submitProcessor);
                ownerGraph[2] = submitWorker;

                // 3. Output Processor
                NvencOrderedOutputProcessor outputProcessor = new NvencOrderedOutputProcessor(
                    processState,
                    outputQueue,
                    collector,
                    sink,
                    new NvencFailedBeforeSubmitReleaseCoordinator(
                        processState, workSlots, sampleSlots, submitCredits, completionCredits),
                    new NvencSubmittedOutputAbandonRecoveryCoordinator(
                        processState, collector, sampleSlots, accessUnitBuffer),
                    completionBoundary);

                // 4. Output Worker, which needs the Submit Worker to exist -
                //    the reason the wake cannot be a constructor argument
                outputWorker = new NvencOrderedOutputWorkerService(
                    processState, outputProcessor, context, submitWorker, teardown);
                ownerGraph[3] = outputWorker;

                // 5. the two notifications this Run needs, each bound once:
                //    the enqueue wake, and the wake that retries a collector
                //    which could not take the shared gate.
                submitProcessor.BindOutputWorkerNotification(outputWorker);
                processState.BindResourceResolutionReleaseNotification(outputWorker);

                // 6. Only now may the Submit Worker run.
                submitWorker.Start();
                // ---- end of the composition ----

                NvencSubmissionAdmissionCoordinator admission =
                    new NvencSubmissionAdmissionCoordinator(
                        processState, workSlots, sampleSlots, syncSlots, submitCredits,
                        completionCredits, submissionQueue,
                        new NvencNativeGpuConversionCommandIssuer(owner),
                        context, backendOwner);

                Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease rented), Is.True);
                surface = new CaptureSurfaceLease(pool, rented);
                ownerGraph[4] = surface;

                // Exactly one frame, and the admission issues its conversion
                // exactly once through the production issuer.
                Assert.That(
                    admission.TryAccept(MakeSentinelFrame(1), surface, out workToken),
                    Is.EqualTo(CaptureSubmitStatus.Accepted));
                Assert.That(workToken.IsValid, Is.True);
                Assert.That(surface.IsBackendOwned, Is.True);
                // The live Submit Worker may already have dequeued the record.

                // The Main Thread's whole contribution: hand back a release
                // when one is waiting, and tell the Submit Worker that
                // something may have changed. The Output Worker is told
                // nothing from here - its wake comes from the enqueue.
                Action advanceMainThread = () =>
                {
                    releaseCoordinator.TryApplyPendingRelease();
                    submitWorker.Notify();
                };

                NvencFrameCompletionRecord completion = default;
                bool completed = false;
                bool submitPublished = false;

                // The wall clock is the only deadline. Frames are counted
                // for the report and decide nothing: this Player renders
                // thousands of them a second, so a frame budget would turn an
                // ordinary scheduling or driver delay into a timeout long
                // before the intended thirty seconds had passed.
                int frames = 0;
                Stopwatch watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < SentinelWatchdogMs)
                {
                    frames++;
                    advanceMainThread();

                    // Stages, read from what the pipeline already shows: the
                    // Submit Processor letting go of its work means the picture
                    // reached the Submit-to-Output Queue, and an append means
                    // the Output Worker got past the completion event and the
                    // copy.
                    submitPublished = submitPublished ||
                        (!submitProcessor.HasPendingWork && submissionQueue.Count == 0);

                    if (completionBoundary.TryCollect(out completion))
                    {
                        completed = true;
                        break;
                    }

                    yield return null;
                }

                if (!completed)
                {
                    stalled = true;
                    stalledAt = DescribeStall(submitPublished, writer.AppendCount);

                    // What the pipeline looked like at the deadline, read from
                    // the references this test already holds and written down
                    // before the poison changes any of it.
                    Exception submitFatal = null;
                    Exception outputFatal = null;
                    submitWorker.TryGetFailure(out submitFatal);
                    outputWorker.TryGetFailure(out outputFatal);
                    SentinelMarker(
                        "TIMEOUT elapsedMs=" + watch.ElapsedMilliseconds +
                        " frames=" + frames +
                        " outQ=" + outputQueue.Count +
                        " outputPending=" + outputProcessor.HasPendingWork +
                        " outputStopped=" + outputWorker.IsStopped +
                        " submitStopped=" + submitWorker.IsStopped +
                        " submitFatal=" + (submitFatal == null ? "none" : submitFatal.GetType().Name) +
                        " outputFatal=" + (outputFatal == null ? "none" : outputFatal.GetType().Name) +
                        " appends=" + writer.AppendCount);

                    // Nothing is fabricated and nothing is taken back: the
                    // process is poisoned so neither worker starts new work,
                    // and whatever a worker may still be inside is left exactly
                    // where it is.
                    processState.TryPoison();
                }

                if (!stalled)
                {
                    Assert.That(
                        completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.None));
                    Assert.That(completion.IsValid, Is.True);
                    Assert.That(completion.WorkToken.IdenticalTo(workToken), Is.True);
                    Assert.That(completion.WorkToken.CaptureFrameId, Is.EqualTo(1L));

                    // One access unit, and it is the one this picture produced:
                    // the native collection copies only for the exact sample
                    // slot and generation that were submitted, so bytes at all
                    // are evidence that the slot matched.
                    Assert.That(writer.AppendCount, Is.EqualTo(1));
                    Assert.That(writer.ValidLength, Is.GreaterThanOrEqualTo(1));
                    Assert.That(
                        writer.ValidLength,
                        Is.LessThanOrEqualTo((int)NvencBringUpProfileV1.MaxAccessUnitByteLength));

                    // Annex-B and nothing more.
                    Assert.That(writer.ValidLength, Is.GreaterThanOrEqualTo(4));
                    Assert.That(writer.AccessUnit[0], Is.EqualTo((byte)0x00));
                    Assert.That(writer.AccessUnit[1], Is.EqualTo((byte)0x00));
                    Assert.That(
                        writer.AccessUnit[2] == 0x01 ||
                        (writer.AccessUnit[2] == 0x00 && writer.AccessUnit[3] == 0x01),
                        Is.True,
                        "the access unit must start with Annex-B.");

                    // Each worker did its own half on its own thread. This
                    // thread never drove either processor, so the copy that
                    // produced these bytes and the actual picture submission
                    // submit can only have happened on the workers' threads.
                    Assert.That(
                        writer.AppendThreadName,
                        Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                    Assert.That(
                        outputSource.ExecutingThreadName,
                        Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                    Assert.That(
                        submitter.ExecutingThreadName,
                        Is.EqualTo(NvencOrderedSubmitWorkerService.WorkerThreadName));
                    Assert.That(
                        Thread.CurrentThread.Name,
                        Is.Not.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                    Assert.That(
                        Thread.CurrentThread.Name,
                        Is.Not.EqualTo(NvencOrderedSubmitWorkerService.WorkerThreadName));

                    // The frame gave everything back.
                    // Applying the handoff can transiently lose the short
                    // resource gate to a worker. Observe success across frames
                    // instead of treating one nonblocking miss as failure.
                    bool sourceReleased = false;
                    yield return AdvanceUntil(
                        () => syncSlots.OccupiedCount == 0 && !surface.IsCreated,
                        advanceMainThread, value => sourceReleased = value);
                    Assert.That(sourceReleased, Is.True,
                        "the evidenced source release did not return the texture");
                    Assert.That(syncSlots.OccupiedCount, Is.EqualTo(0));
                    Assert.That(workSlots.OccupiedCount, Is.EqualTo(0));
                    Assert.That(sampleSlots.OccupiedCount, Is.EqualTo(0));
                    Assert.That(submitCredits.OccupiedCount, Is.EqualTo(0));
                    Assert.That(completionCredits.OccupiedCount, Is.EqualTo(0));
                    Assert.That(outputQueue.Count, Is.EqualTo(0));
                    Assert.That(processState.IsPoisoned, Is.False);
                    Assert.That(submitWorker.TryGetFailure(out Exception _), Is.False);
                    Assert.That(outputWorker.TryGetFailure(out Exception _), Is.False);

                    Assert.That(surface.IsCreated, Is.False,
                        "the evidenced source release must already have returned the texture");

                    // Keep rendering after collection: appender entry alone did
                    // not prove that Present or the main loop could progress.
                    for (int postFrame = 0; postFrame < 8; postFrame++)
                    {
                        yield return null;
                    }

                    // ---- normal termination, only now ----
                    // 1. the process drains
                    Assert.That(processState.TryBeginDrain(), Is.True);

                    // 2. the Submit Worker drains, confirmed by the drain
                    //    itself rather than by a settle
                    Assert.That(submitWorker.BeginDrain(), Is.True);
                    bool drained = false;
                    yield return AdvanceUntil(
                        () => submitWorker.DrainCompleted, advanceMainThread,
                        value => drained = value);
                    if (!drained)
                    {
                        stalled = true;
                        stalledAt = "the Submit Worker did not complete its drain";
                        processState.TryPoison();
                    }
                }

                // Terminal calls use nonblocking resource gates. A pre-side-
                // effect miss is retried on a notification by contract; after
                // Submit has drained it will no longer supply output wakes.
                // Initial frame delivery above still relies on its real enqueue.
                Action advanceTerminal = () =>
                {
                    advanceMainThread();
                    outputWorker.Notify();
                };

                if (!stalled)
                {
                    // Freeze the admitted frame ledger after StopAccepting and
                    // before the Output Worker attempts context finalization.
                    Assert.That(context.TryFreezeAcceptedFrames(
                        out NvencRunAcceptedFrameSnapshot acceptedFrames), Is.True);
                    Assert.That(acceptedFrames.Count, Is.EqualTo(1));
                    Assert.That(acceptedFrames.TryGetCaptureFrameId(0, out long acceptedFrameId), Is.True);
                    Assert.That(acceptedFrameId, Is.EqualTo(workToken.CaptureFrameId));

                    // 3. the terminal is requested on the exact context
                    bool requested = false;
                    yield return AdvanceUntil(
                        () => outputWorker.TryRequestFinalize(), advanceTerminal,
                        value => requested = value);
                    if (!requested)
                    {
                        stalled = true;
                        stalledAt = "the Output terminal request was never accepted";
                        processState.TryPoison();
                    }
                }

                NvencRunChunkTerminalOutcome outcome = default;
                if (!stalled)
                {
                    // 4. and its result is collected, never assumed
                    bool collected = false;
                    yield return AdvanceUntil(
                        () =>
                        {
                            if (!outputWorker.TryCollectTerminal(
                                out NvencRunChunkTerminalOutcome value))
                            {
                                return false;
                            }

                            outcome = value;
                            return true;
                        },
                        advanceTerminal,
                        value => collected = value);
                    if (!collected)
                    {
                        stalled = true;
                        stalledAt = "the Output terminal result was never collected";
                        processState.TryPoison();
                    }
                }

                if (!stalled)
                {
                    Assert.That(outcome.IsFinalized, Is.True);
                    Assert.That(outcome.Result, Is.Not.Null);
                    Assert.That(writer.FinalizeCount, Is.EqualTo(1));

                    // 5. the teardown is requested, and confirmed by its own
                    //    evidence rather than by a settle
                    bool teardownRequested = false;
                    yield return AdvanceUntil(
                        () => outputWorker.TryRequestTeardown(), advanceTerminal,
                        value => teardownRequested = value);

                    bool teardownCompleted = false;
                    if (teardownRequested)
                    {
                        yield return AdvanceUntil(
                            () => outputWorker.TeardownCompleted, advanceTerminal,
                            value => teardownCompleted = value);
                    }

                    if (!teardownCompleted)
                    {
                        stalled = true;
                        stalledAt = "the Output Worker teardown did not complete";
                        processState.TryPoison();
                    }
                }

                if (!stalled)
                {
                    Assert.That(teardown.CallCount, Is.EqualTo(1));
                    Assert.That(
                        teardown.ExecutingThreadName,
                        Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));

                    // 7. both worker threads have physically stopped
                    bool stopped = false;
                    yield return AdvanceUntil(
                        () => submitWorker.IsStopped && outputWorker.IsStopped, null,
                        value => stopped = value);
                    if (!stopped)
                    {
                        stalled = true;
                        stalledAt = "a worker thread did not physically stop";
                        processState.TryPoison();
                    }
                    else
                    {
                        // Normal frame completion plus zero sync/sample leases
                        // proves our callback, GPU conversion and encoder no
                        // longer own these resources. Physical stop additionally
                        // excludes races with any worker native call.
                        Assert.That(syncSlots.OccupiedCount, Is.Zero);
                        Assert.That(sampleSlots.OccupiedCount, Is.Zero);
                        Assert.That(surface.IsCreated, Is.False);

                        // Physically stopped, so no held record is waiting on
                        // the gate wake any more and the binding can go. It is
                        // never released before the stop, and never on the
                        // poison path below.
                        processState.UnbindResourceResolutionReleaseNotification(outputWorker);

                        owner.ReleaseCompletionEvents();
                        owner.ReleaseOutputBuffers();
                        owner.ReleaseConversionCommands();
                        owner.ReleaseInputSurfaces();
                        owner.ReleaseSourceSurfaces();

                        // Only a stopped worker is disposed.
                        submitWorker.Dispose();
                        outputWorker.Dispose();

                        // 9. the session closes last
                        owner.Dispose();

                        ReturnSentinelSurface(surface, backendOwner, workToken);
                        surface = null;
                        pool.Dispose();
                        teardownDone = true;
                        SentinelMarker("teardown complete");
                    }
                }
            }
            finally
            {
                if (teardownDone)
                {
                    lifetimeRoot.Free();
                }
                else
                {
                    // Poison/Notify request stop; they cannot cancel a native
                    // call or prove callback/GPU quiescence. Never release even
                    // a subset around an assertion failure or a partial teardown.
                    // The root deliberately survives for the Player lifetime.
                    bool alreadyPoisoned = processState.IsPoisoned;
                    Exception submitFailure = null;
                    Exception outputFailure = null;
                    submitWorker?.TryGetFailure(out submitFailure);
                    outputWorker?.TryGetFailure(out outputFailure);
                    string retainedState =
                        " surfaceCreated=" + (surface == null ? "null" : surface.IsCreated.ToString()) +
                        " backendOwned=" + (surface == null ? "null" : surface.IsBackendOwned.ToString()) +
                        " sync=" + syncSlots.OccupiedCount +
                        " work=" + workSlots.OccupiedCount +
                        " sample=" + sampleSlots.OccupiedCount +
                        " submitCredits=" + submitCredits.OccupiedCount +
                        " completionCredits=" + completionCredits.OccupiedCount +
                        " poisonedBeforeCleanup=" + alreadyPoisoned +
                        " submitFailure=" + (submitFailure == null ? "none" : submitFailure.Message) +
                        " outputFailure=" + (outputFailure == null ? "none" : outputFailure.Message);
                    QuietStep(() => processState.TryPoison());
                    QuietStep(() => submitWorker?.Notify());
                    QuietStep(() => outputWorker?.Notify());
                    SentinelMarker("RETAINED owner graph; external process termination required" +
                        (stalledAt == null ? "" : "; " + stalledAt) + retainedState);
                }
            }

            Assert.That(
                stalled, Is.False,
                "one frame did not get through both workers: " + stalledAt);
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

        [DllImport("kernel32.dll", ExactSpelling = true)]
        private static extern uint GetCurrentThreadId();

        private static void SentinelMarker(string stage)
        {
            UnityEngine.Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, null, "{0}",
                "[2W] ticks=" + Stopwatch.GetTimestamp() +
                " managed=" + Thread.CurrentThread.ManagedThreadId +
                " os=0x" + GetCurrentThreadId().ToString("x") +
                " name=" + (Thread.CurrentThread.Name ?? "unnamed") + " " + stage);
        }

        private sealed class SentinelEncodePictureSubmitter : INvencEncodePictureSubmitter
        {
            private readonly NvencNativeEncodePictureSubmitter _inner;
            private string _executingThreadName;

            internal string ExecutingThreadName => Volatile.Read(ref _executingThreadName);

            internal SentinelEncodePictureSubmitter(NvencNativeEncoderSessionOwner owner)
            {
                _inner = new NvencNativeEncodePictureSubmitter(owner);
            }

            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                Volatile.Write(ref _executingThreadName, Thread.CurrentThread.Name);
                return _inner.TrySubmit(operation);
            }
        }

        private sealed class SentinelOutputBitstreamSource : INvencOutputBitstreamSource
        {
            private readonly NvencNativeOutputBitstreamSource _inner;
            private string _executingThreadName;

            internal string ExecutingThreadName => Volatile.Read(ref _executingThreadName);

            internal SentinelOutputBitstreamSource(NvencNativeEncoderSessionOwner owner)
            {
                _inner = new NvencNativeOutputBitstreamSource(owner);
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken, in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination, int destinationCapacity, out int validLength)
            {
                Volatile.Write(ref _executingThreadName, Thread.CurrentThread.Name);
                return _inner.TryCopyCompletedOutput(
                    workToken, sampleSlot, destination, destinationCapacity, out validLength);
            }
        }

        /// <summary>
        /// Advances frames until the real condition holds or the watchdog
        /// expires, running the caller's per-frame coordination each time. It
        /// waits on no handle, joins no thread, and reports only what it
        /// actually observed.
        /// </summary>
        private static IEnumerator AdvanceUntil(
            Func<bool> condition, Action perFrame, Action<bool> observed)
        {
            Stopwatch watch = Stopwatch.StartNew();
            bool met = condition();

            while (!met && watch.ElapsedMilliseconds < SentinelWatchdogMs)
            {
                yield return null;

                if (perFrame != null)
                {
                    perFrame();
                }

                met = condition();
            }

            observed(met);
        }

        /// <summary>
        /// The furthest stage a run actually reached, from observations the
        /// pipeline already makes. It says where progress stopped and claims
        /// nothing about why.
        /// </summary>
        private static string DescribeStall(bool submitPublished, int appendCount)
        {
            if (appendCount > 0)
            {
                return "the appender completed its copy but Main did not collect Frame Completion";
            }

            if (submitPublished)
            {
                return "the picture reached the Submit-to-Output Queue but no access unit was appended";
            }

            return "the picture never reached the Submit-to-Output Queue";
        }

        /// <summary>
        /// Requests poison/wakeup after failure without replacing the original
        /// exception. Never used to release an owned resource.
        /// </summary>
        private static void QuietStep(Action step)
        {
            try
            {
                step();
            }
            catch (Exception)
            {
            }
        }

        private static void ReturnSentinelSurface(
            CaptureSurfaceLease surface, Guid backendOwner, CaptureFrameWorkToken workToken)
        {
            if (surface == null || !surface.IsCreated)
            {
                return;
            }

            if (surface.IsBackendOwned)
            {
                surface.ReleaseFromBackend(backendOwner, workToken);
            }
            else
            {
                surface.Dispose();
            }
        }

        /// <summary>One frame of the fixed profile, on the sentinel's Run.</summary>
        private static CaptureFrameEnvelope MakeSentinelFrame(long captureFrameId)
        {
            CaptureFrameTraceContext trace = new CaptureFrameTraceContext(
                10, 20, 4, 1, captureFrameId, 30, SentinelTestRunId, 40, 50, 60, 2, 70);
            CaptureFrameRequest request = new CaptureFrameRequest(
                trace, CaptureSource.UnityRenderTexture, CaptureEye.Left,
                new CaptureImageRect(
                    0, 0, NvencBringUpProfileV1.Width, NvencBringUpProfileV1.Height),
                0, CapturePixelFormat.Rgba32);
            CaptureFrameTiming timing = new CaptureFrameTiming(1.0, 0.01, true, 2.0, 3.0, 4);
            CapturePoseSample head = new CapturePoseSample(
                new Vector3(1, 2, 3), Quaternion.identity);
            return new CaptureFrameEnvelope(
                request, timing, head, CapturePoseSample.Unavailable,
                CapturePoseSample.Unavailable, 8, 9, CaptureColorSpace.Srgb, 91,
                "sentinel-build", "sentinel-scene", 123);
        }

        /// <summary>
        /// The one Run session the chunk context is bound to. Nothing is
        /// provisioned, written, or locked on disk: the provisioner, marker
        /// writer, and lock handles issue only the receipts the context's own
        /// validation needs.
        /// </summary>
        private static CaptureRunInitializationSessionIssue MakeRunIssue()
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                SentinelTestRunId);

            CaptureRunInitializationExecutionReceipt receipt =
                new CaptureRunInitializationExecutionCoordinator(
                    new SentinelRootProvisioner(), new SentinelMarkerWriter())
                .Execute(new CaptureRunInitializationWriteBatch(
                    CaptureRunInitializationDocumentSetFactory.Create(
                        layout, SentinelRunInitId)));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new SentinelLockHandle(pathSet.FirstLockPath),
                new SentinelLockHandle(pathSet.SecondLockPath));
            CaptureRunInitializationSessionOwnershipLease ownership =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);

            return CaptureRunInitializationSessionFactory.Create(
                ownership,
                CaptureRunLockIdentityEvidence.Create(ownership, ownership.LockPathSet),
                CaptureRunInitializationReadyEvidence.FromFresh(receipt));
        }

        /// <summary>
        /// This fixture's chunk boundary: it keeps the one access unit it is
        /// handed, notes which thread handed it over, and issues the
        /// finalization receipt. No file is opened, nothing is hashed over real
        /// content, and no call waits or retries.
        /// </summary>
        private sealed class SentinelChunkWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private readonly byte[] _accessUnit =
                new byte[(int)NvencBringUpProfileV1.MaxAccessUnitByteLength];

            private int _appendCount;
            private int _finalizeCount;

            internal int AppendCount => Volatile.Read(ref _appendCount);

            internal int FinalizeCount => Volatile.Read(ref _finalizeCount);

            internal int ValidLength { get; private set; }

            internal byte[] AccessUnit => _accessUnit;

            internal string AppendThreadName { get; private set; }

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                AppendThreadName = Thread.CurrentThread.Name;
                ValidLength = validLength;

                if (buffer != null && validLength > 0 && validLength <= _accessUnit.Length)
                {
                    Array.Copy(buffer, offset, _accessUnit, 0, validLength);
                }

                Interlocked.Increment(ref _appendCount);
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(
                NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);
                return NvencRunChunkFinalizationReceipt.Create(
                    this,
                    operation,
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        operation.ArtifactId, operation.AccumulatedByteLength, SentinelChunkHash));
            }
        }

        /// <summary>
        /// This fixture's Output Worker teardown: it records that it ran and on
        /// which thread, and issues the receipt. The native resources are
        /// released by the test in its own ordered step, so nothing here
        /// touches the session.
        /// </summary>
        private sealed class SentinelOutputWorkerTeardown : INvencOutputWorkerTeardown
        {
            private int _callCount;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal string ExecutingThreadName { get; private set; }

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;
                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class SentinelRootProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class SentinelMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(
                CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class SentinelLockHandle : ICaptureRunLockHandle
        {
            internal SentinelLockHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
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
