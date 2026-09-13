using System;
using System.Collections;
using System.Collections.Generic;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;
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

        /// <summary>
        /// The one environment variable the fixed-path runner hands the
        /// decoder over in. The path itself is the runner's to resolve and
        /// check; this fixture only uses what it was given.
        /// </summary>
        private const string DecoderPathVariable = "ZANTETSU_FFMPEG";

        /// <summary>
        /// Wall-clock budget for the one decode this sentinel runs, after the
        /// Run is over and nothing of the capture path is still alive.
        /// </summary>
        private const int DecodeTimeoutMs = 60000;

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

        /// <summary>
        /// Nine frames through one session, one set of eight slots and one
        /// pair of workers: the capacity is filled once, refuses the ninth
        /// frame while it is full, and then reuses a slot for it under a new
        /// generation once a single completion has been collected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The repeated single-frame sentinel rebuilds its session every time,
        /// so it never shows what happens inside one Run: that the eight fixed
        /// slots circulate, that a full pipeline answers an admission with
        /// ordinary backpressure rather than a failure, and that a returned
        /// slot is handed out again with a generation that tells it apart from
        /// the frame that had it before. Nine is the smallest number that
        /// shows all three.
        /// </para>
        /// <para>
        /// The Main Thread advances only the two non-waiting entries this path
        /// has. The Output Worker is never notified from here - not while the
        /// frames run and not during the terminal - so everything it does is
        /// driven by the enqueue wake and the gate-release wake alone.
        /// </para>
        /// </remarks>
        [UnityTest]
        public IEnumerator Player_RunsNineFramesThroughOneSession()
        {
            const int Filled = NvencBringUpProfileV1.WorkSlotCount;
            const int Total = Filled + 1;

            SentinelRun run = new SentinelRun();
            bool teardownDone = false;
            bool stalled = false;
            string stalledAt = null;

            CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[Total];
            CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[Total];

            try
            {
                run.Build();
                SentinelMarker("nine-frame run built");

                // Fill the fixed capacity. Nothing is collected while this
                // happens, so every Work Slot and Frame Completion credit the
                // admission reserves is still held at the end of it.
                for (int i = 0; i < Filled; i++)
                {
                    Assert.That(
                        run.Pool.TryRent(out CaptureFrameRenderTargetLease rented), Is.True,
                        "the fixed source pool must supply one surface per frame");

                    // Each frame gets its own grey, far enough apart that
                    // compression cannot reorder them, registered from this
                    // thread before the surface becomes the backend's.
                    ClearSourceToGrey(run.Pool, rented, GreyForFrame(i + 1));

                    surfaces[i] = new CaptureSurfaceLease(run.Pool, rented);
                    run.Track(surfaces[i]);

                    Assert.That(
                        run.Admission.TryAccept(
                            MakeSentinelFrame(i + 1), surfaces[i], out tokens[i]),
                        Is.EqualTo(CaptureSubmitStatus.Accepted),
                        "frame " + (i + 1) + " must be accepted before anything is collected");
                    Assert.That(tokens[i].IsValid, Is.True);
                    Assert.That(surfaces[i].IsBackendOwned, Is.True);
                }

                Assert.That(run.Context.AcceptedFrameCount, Is.EqualTo(Filled));

                // Let the workers carry all eight through to a published
                // completion. Nothing is collected, so the capacity stays full.
                bool filled = false;
                yield return AdvanceUntil(
                    () => run.ChunkWriter.AppendCount == Filled &&
                        run.OutputQueue.Count == 0 &&
                        !run.OutputProcessor.HasPendingWork,
                    run.AdvanceMainThread,
                    value => filled = value);

                if (!filled)
                {
                    stalled = true;
                    stalledAt = run.DescribeState("the first " + Filled + " frames did not finish");
                    run.ProcessState.TryPoison();
                }

                if (!stalled)
                {
                    Assert.That(run.WorkSlots.OccupiedCount, Is.EqualTo(Filled));
                    Assert.That(run.CompletionCredits.OccupiedCount, Is.EqualTo(Filled));

                    // One of the eight source surfaces has come back through
                    // the ordinary Main Thread release, and the ninth frame is
                    // built on it.
                    CaptureFrameRenderTargetLease reused = default;
                    bool returned = false;
                    yield return AdvanceUntil(
                        () => run.Pool.TryRent(out reused),
                        run.AdvanceMainThread,
                        value => returned = value);

                    if (!returned)
                    {
                        stalled = true;
                        stalledAt = run.DescribeState("no source surface came back for the ninth frame");
                        run.ProcessState.TryPoison();
                    }
                    else
                    {
                        // The reused surface still carries the grey of the
                        // frame that gave it back, so it is repainted for this
                        // one after the return and the re-rent.
                        ClearSourceToGrey(run.Pool, reused, GreyForFrame(Total));

                        surfaces[Filled] = new CaptureSurfaceLease(run.Pool, reused);
                        run.Track(surfaces[Filled]);
                    }
                }

                if (!stalled)
                {
                    // A full pipeline refuses the ninth frame, and refuses it
                    // the ordinary way: nothing is reserved, nothing is
                    // issued, nothing is enqueued, the Run does not record it,
                    // the caller keeps its surface, and nothing is poisoned.
                    Assert.That(
                        run.Admission.TryAccept(
                            MakeSentinelFrame(Total), surfaces[Filled],
                            out CaptureFrameWorkToken refused),
                        Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(refused.IsValid, Is.False);
                    Assert.That(surfaces[Filled].IsCallerOwned, Is.True);
                    Assert.That(surfaces[Filled].IsBackendOwned, Is.False);
                    Assert.That(run.SubmissionQueue.Count, Is.EqualTo(0));
                    Assert.That(run.Context.AcceptedFrameCount, Is.EqualTo(Filled));
                    Assert.That(run.WorkSlots.OccupiedCount, Is.EqualTo(Filled));
                    Assert.That(run.SyncSlots.OccupiedCount, Is.EqualTo(0));
                    Assert.That(run.ProcessState.IsPoisoned, Is.False);
                    Assert.That(run.ChunkWriter.AppendCount, Is.EqualTo(Filled));

                    // Collect exactly one, which returns exactly one Work Slot
                    // and one Frame Completion credit.
                    Assert.That(
                        run.CompletionBoundary.TryCollect(
                            out NvencFrameCompletionRecord firstCompletion),
                        Is.True);
                    Assert.That(
                        firstCompletion.Status,
                        Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                    Assert.That(firstCompletion.WorkToken.CaptureFrameId, Is.EqualTo(1L));
                    Assert.That(run.WorkSlots.OccupiedCount, Is.EqualTo(Filled - 1));
                    Assert.That(run.CompletionCredits.OccupiedCount, Is.EqualTo(Filled - 1));

                    // The same frame, on the same caller-owned surface, is now
                    // admitted - and it gets the slot that was just returned,
                    // under a generation that tells it apart from the frame
                    // that had it before.
                    Assert.That(
                        run.Admission.TryAccept(
                            MakeSentinelFrame(Total), surfaces[Filled], out tokens[Filled]),
                        Is.EqualTo(CaptureSubmitStatus.Accepted));
                    Assert.That(tokens[Filled].IsValid, Is.True);
                    Assert.That(surfaces[Filled].IsBackendOwned, Is.True);
                    Assert.That(
                        tokens[Filled].SlotIndex,
                        Is.EqualTo(firstCompletion.WorkToken.SlotIndex),
                        "the ninth frame must reuse the slot the first frame gave back");
                    Assert.That(
                        tokens[Filled].Generation,
                        Is.GreaterThan(firstCompletion.WorkToken.Generation),
                        "a reused slot must carry a later generation than the frame before it");
                    Assert.That(run.Context.AcceptedFrameCount, Is.EqualTo(Total));
                }

                if (!stalled)
                {
                    // The rest, in the order they were accepted.
                    for (int i = 1; i < Total; i++)
                    {
                        long expected = i + 1;
                        NvencFrameCompletionRecord collected = default;
                        bool gotOne = false;
                        yield return AdvanceUntil(
                            () => run.CompletionBoundary.TryCollect(out collected),
                            run.AdvanceMainThread,
                            value => gotOne = value);

                        if (!gotOne)
                        {
                            stalled = true;
                            stalledAt = run.DescribeState("frame " + expected + " never completed");
                            run.ProcessState.TryPoison();
                            break;
                        }

                        Assert.That(
                            collected.Status,
                            Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                        Assert.That(collected.Reason, Is.EqualTo(NvencFrameCompletionReason.None));
                        Assert.That(
                            collected.WorkToken.CaptureFrameId, Is.EqualTo(expected),
                            "completions must arrive in the order the frames were accepted");
                    }
                }

                if (!stalled)
                {
                    // Nine access units appended into one open chunk, and
                    // nothing finalized per frame: the chunk is still the
                    // Run's to add to until the Run itself ends.
                    Assert.That(run.ChunkWriter.AppendCount, Is.EqualTo((long)Total));
                    Assert.That(
                        run.ChunkWriter.State, Is.EqualTo(NvencRunChunkWriterState.Open),
                        "a frame must never finalize the Run's chunk");
                    Assert.That(run.ChunkWriter.AccumulatedByteLength, Is.GreaterThan(0L));

                    // Both workers did their own half on their own threads.
                    // The appends are the Output Worker's by construction -
                    // this thread never drives either processor - and the copy
                    // that produced their bytes is recorded here.
                    Assert.That(
                        run.OutputSource.ExecutingThreadName,
                        Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                    Assert.That(
                        run.Submitter.ExecutingThreadName,
                        Is.EqualTo(NvencOrderedSubmitWorkerService.WorkerThreadName));

                    // Everything the nine frames borrowed is back.
                    bool drainedResources = false;
                    yield return AdvanceUntil(
                        () => run.SyncSlots.OccupiedCount == 0 &&
                            run.WorkSlots.OccupiedCount == 0 &&
                            run.SampleSlots.OccupiedCount == 0 &&
                            run.SubmitCredits.OccupiedCount == 0 &&
                            run.CompletionCredits.OccupiedCount == 0 &&
                            run.OutputQueue.Count == 0 &&
                            run.SubmissionQueue.Count == 0,
                        run.AdvanceMainThread,
                        value => drainedResources = value);

                    if (!drainedResources)
                    {
                        stalled = true;
                        stalledAt = run.DescribeState("the Run did not give every resource back");
                        run.ProcessState.TryPoison();
                    }
                }

                if (!stalled)
                {
                    Assert.That(run.ProcessState.IsPoisoned, Is.False);
                    Assert.That(run.SubmitWorker.TryGetFailure(out Exception _), Is.False);
                    Assert.That(run.OutputWorker.TryGetFailure(out Exception _), Is.False);

                    // Keep rendering: an append alone does not prove the main
                    // loop and Present can still progress.
                    for (int postFrame = 0; postFrame < 8; postFrame++)
                    {
                        yield return null;
                    }

                    // Every conversion of this Run has completed and the
                    // session is still open, so this is where the render
                    // callbacks' effect on Unity's own drawing shows: it draws
                    // one known colour through Unity's pipeline and reads it
                    // back, once, before any teardown begins. Inherited from
                    // the retired conversion-command sentinel, which was the
                    // only place that checked it.
                    AssertUnityStillRenders();

                    yield return run.Terminate(
                        Total,
                        value =>
                        {
                            stalled = value != null;
                            stalledAt = value;
                        });
                }

                if (!stalled)
                {
                    // One finalization for the whole Run, not one per frame,
                    // and a result that re-verifies its own correlation.
                    Assert.That(
                        run.ChunkWriter.State, Is.EqualTo(NvencRunChunkWriterState.Finalized));
                    Assert.That(run.FinalizationResult, Is.Not.Null);
                    Assert.That(run.FinalizationResult.IsValid, Is.True);
                    Assert.That(run.FinalizationResult.AppendedCount, Is.EqualTo((long)Total));
                    Assert.That(run.FinalizationResult.LastFrameId, Is.EqualTo((long)Total));
                    Assert.That(run.FinalizationResult.ByteLength, Is.GreaterThan(0L));

                    for (int i = 0; i < Total; i++)
                    {
                        ReturnSentinelSurface(surfaces[i], run.BackendOwner, tokens[i]);
                        surfaces[i] = null;
                    }

                    run.ReleaseAfterProvenStop();

                    // Only now: the Run is over, both workers stopped, both
                    // teardowns done, and the chunk's handles and this Run's
                    // lock released. The path comes from the descriptor, never
                    // from a file name copied into this test.
                    CaptureArtifactDescriptor descriptor = run.FinalizationResult.Descriptor;
                    Assert.That(descriptor.IsValid, Is.True);

                    string confirmedChunk = Path.Combine(
                        run.Layout.StagingRunRoot,
                        descriptor.StagingRelativePath.Replace('/', Path.DirectorySeparatorChar));

                    Assert.That(
                        File.Exists(confirmedChunk), Is.True,
                        "the descriptor's staging path must name a confirmed chunk");

                    // Its own directory holds that one file and nothing else,
                    // so no pending entry was left behind.
                    string[] chunkEntries =
                        Directory.GetFileSystemEntries(Path.GetDirectoryName(confirmedChunk));
                    Assert.That(chunkEntries.Length, Is.EqualTo(1));
                    Assert.That(
                        Path.GetFullPath(chunkEntries[0]),
                        Is.EqualTo(Path.GetFullPath(confirmedChunk)));

                    // Read once, streamed: the length, the first four
                    // bytes and the hash, without ever holding the chunk.
                    long fileLength;
                    string fileHash;
                    byte[] prefix = new byte[4];
                    using (FileStream chunkStream = new FileStream(
                        confirmedChunk, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        fileLength = chunkStream.Length;

                        int read = 0;
                        while (read < prefix.Length)
                        {
                            int got = chunkStream.Read(prefix, read, prefix.Length - read);
                            if (got <= 0)
                            {
                                break;
                            }

                            read += got;
                        }

                        Assert.That(read, Is.EqualTo(prefix.Length));

                        chunkStream.Position = 0;
                        fileHash = Sha256Hex(chunkStream);
                    }

                    Assert.That(
                        fileLength, Is.EqualTo(descriptor.ByteLength),
                        "the file on disk must be exactly as long as the descriptor says");
                    Assert.That(
                        fileHash, Is.EqualTo(descriptor.ContentHash),
                        "the file on disk must hash to the descriptor's content hash");

                    // Annex-B at the start, and nothing parsed beyond that.
                    Assert.That(prefix[0], Is.EqualTo((byte)0x00));
                    Assert.That(prefix[1], Is.EqualTo((byte)0x00));
                    Assert.That(
                        prefix[2] == 0x01 || (prefix[2] == 0x00 && prefix[3] == 0x01),
                        Is.True,
                        "the chunk must start with an Annex-B start code");

                    // The chunk is confirmed and nothing of the capture
                    // path is alive any more: the encoder session is closed,
                    // both workers stopped, both teardowns done and this Run's
                    // lock released. Only now is it handed to a decoder.
                    DecodeAndCheckFrameOrder(run, confirmedChunk, Total);

                    run.DeleteTemporaryBase();
                    teardownDone = true;
                    SentinelMarker("nine-frame teardown complete");
                }
            }
            finally
            {
                run.Retire(teardownDone, stalledAt);
            }

            Assert.That(
                stalled, Is.False,
                "nine frames did not get through one session: " + stalledAt);
            Assert.That(run.Owner.IsOpen, Is.False);
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

                // 5. the one notification this Run needs, bound once: the
                //    wake that follows every shared-gate release, which is what
                //    both an enqueued record and a retried collector rely on.
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
        /// <summary>
        /// The grey one frame is painted with. Monotonic in the frame id and
        /// far enough apart that an encoder cannot swap two of them, which is
        /// all this is for: it is not a colour, range, or orientation claim.
        /// </summary>
        private static float GreyForFrame(int captureFrameId)
        {
            return 0.08f + (0.09f * (captureFrameId - 1));
        }

        /// <summary>
        /// Registers a clear of the whole source surface from the Main Thread,
        /// before the surface is handed to the backend. No shader, no
        /// Texture2D, no CPU upload, and nothing here waits for the GPU.
        /// </summary>
        private static void ClearSourceToGrey(
            CaptureFrameRenderTargetPool pool, in CaptureFrameRenderTargetLease lease, float grey)
        {
            RenderTexture target = pool.GetRenderTexture(lease);

            // A command buffer rather than RenderTexture.active, so no global
            // render state is borrowed and none has to be put back.
            using (CommandBuffer commands = new CommandBuffer { name = "ZantetsuSentinelSourceClear" })
            {
                commands.SetRenderTarget(target);
                commands.ClearRenderTarget(false, true, new Color(grey, grey, grey, 1f));
                Graphics.ExecuteCommandBuffer(commands);
            }
        }

        /// <summary>
        /// Decodes the confirmed chunk once with the decoder the runner
        /// resolved, and checks that it holds exactly the frames that went in,
        /// in the order they went in.
        /// </summary>
        /// <remarks>
        /// The greys are read back from a small region in the middle of each
        /// frame, seeking to it rather than reading the frame, so nothing here
        /// holds a decoded frame - let alone all of them. What is asserted is
        /// the count and the order, never an exact value, a tolerance, a
        /// colour space, or an orientation.
        /// </remarks>
        private static void DecodeAndCheckFrameOrder(
            SentinelRun run, string chunkPath, int expectedFrames)
        {
            string decoder = Environment.GetEnvironmentVariable(DecoderPathVariable);
            Assert.That(
                string.IsNullOrEmpty(decoder), Is.False,
                "the runner must hand this Player a decoder path in " + DecoderPathVariable);

            string rawPath = Path.Combine(run.TemporaryBase, "decoded.rgb24");

            ProcessStartInfo start = new ProcessStartInfo(decoder)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            // Quoted one argument at a time, so a path with spaces stays one
            // argument and nothing is handed to a shell to re-parse.
            // -vsync 0 because a raw H.264 stream carries no timestamps: without
            // it the output is made constant-rate and a frame is duplicated, so
            // the decode would no longer be the frames that went in, in that
            // order. It is a decode condition, not a fallback.
            start.Arguments = string.Join(
                " ",
                "-nostdin",
                "-v", "error",
                "-vsync", "0",
                "-f", "h264",
                "-i", Quote(chunkPath),
                "-map", "0:v:0",
                "-pix_fmt", "rgb24",
                "-f", "rawvideo",
                Quote(rawPath));

            string standardError;
            using (Process decode = new Process { StartInfo = start })
            {
                System.Text.StringBuilder errorText = new System.Text.StringBuilder();
                System.Text.StringBuilder outputText = new System.Text.StringBuilder();
                decode.OutputDataReceived += (sender, args) => outputText.Append(args.Data);
                decode.ErrorDataReceived += (sender, args) => errorText.Append(args.Data);

                Assert.That(decode.Start(), Is.True, "the decoder did not start");

                // Both pipes are drained while it runs, so neither can fill
                // and stall the decoder.
                decode.BeginOutputReadLine();
                decode.BeginErrorReadLine();

                if (!decode.WaitForExit(DecodeTimeoutMs))
                {
                    // Only this instance, and only after it is confirmed gone.
                    QuietStep(() => decode.Kill());
                    decode.WaitForExit(DecodeTimeoutMs);
                    Assert.Fail(
                        "the decoder did not finish within " + DecodeTimeoutMs + " ms");
                }

                standardError = errorText.ToString();
                Assert.That(
                    decode.ExitCode, Is.EqualTo(0),
                    "the decoder refused the chunk: " + standardError);
            }

            // Exactly the frames that went in, and no partial tail.
            long frameBytes =
                (long)NvencBringUpProfileV1.Width * NvencBringUpProfileV1.Height * 3L;
            long expectedLength = frameBytes * expectedFrames;

            using (FileStream raw = new FileStream(
                rawPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.That(
                    raw.Length, Is.EqualTo(expectedLength),
                    "the decode must hold exactly " + expectedFrames + " whole frames");

                // A small region in the middle of each frame is enough to tell
                // the greys apart.
                const int RegionSide = 16;
                int firstColumn = (NvencBringUpProfileV1.Width - RegionSide) / 2;
                int firstRow = (NvencBringUpProfileV1.Height - RegionSide) / 2;
                byte[] row = new byte[RegionSide * 3];
                double[] averages = new double[expectedFrames];

                for (int frame = 0; frame < expectedFrames; frame++)
                {
                    double total = 0.0;
                    for (int offsetRow = 0; offsetRow < RegionSide; offsetRow++)
                    {
                        long position = (frameBytes * frame)
                            + (((long)(firstRow + offsetRow) * NvencBringUpProfileV1.Width)
                                + firstColumn) * 3L;
                        raw.Position = position;

                        int read = 0;
                        while (read < row.Length)
                        {
                            int got = raw.Read(row, read, row.Length - read);
                            if (got <= 0)
                            {
                                break;
                            }

                            read += got;
                        }

                        Assert.That(read, Is.EqualTo(row.Length));

                        for (int i = 0; i < row.Length; i++)
                        {
                            total += row[i];
                        }
                    }

                    averages[frame] = total / row.Length / RegionSide;
                }

                // In the order they were given, and far enough apart that
                // compression cannot have reordered them.
                for (int frame = 1; frame < expectedFrames; frame++)
                {
                    Assert.That(
                        averages[frame], Is.GreaterThan(averages[frame - 1]),
                        "decoded frame " + (frame + 1) + " must be brighter than frame " + frame
                        + " (" + string.Join(", ", Array.ConvertAll(averages, a => a.ToString("F1")))
                        + ")");
                }

                Assert.That(
                    averages[expectedFrames - 1] - averages[0], Is.GreaterThan(20.0),
                    "the greys must stay far enough apart to be an ordering at all");
            }

            SentinelMarker(
                "decoded " + expectedFrames + " frames in order with " + decoder);
        }

        private static string Quote(string path)
        {
            return "\"" + path + "\"";
        }

        /// <summary>
        /// The same hash the descriptor carries, computed over what is
        /// actually on disk. Streamed, so the chunk is never materialized as
        /// one array however long it is.
        /// </summary>
        private static string Sha256Hex(Stream content)
        {
            using (System.Security.Cryptography.SHA256 sha =
                System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(content);
                System.Text.StringBuilder text = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    text.Append(value.ToString("x2"));
                }

                return text.ToString();
            }
        }

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
        /// <summary>
        /// One Run's worth of objects for the real-device sentinels: one
        /// native session, one set of fixed-capacity pools, one Submit Worker
        /// and one Output Worker, wired by hand in the one order they can be
        /// wired in. It is this fixture's own scaffolding, not a composition
        /// root: nothing outside these tests can reach it, and it decides
        /// nothing the product does not already decide.
        /// </summary>
        private sealed class SentinelRun
        {
            internal NvencNativeEncoderSessionOwner Owner;
            internal CaptureFrameRenderTargetPool Pool;
            internal NvencCaptureProcessState ProcessState;
            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencGpuConversionSyncPool SyncSlots;
            internal NvencSubmitToOutputCreditPool SubmitCredits;
            internal NvencFrameCompletionCreditPool CompletionCredits;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmissionQueue;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal Guid BackendOwner;

            internal string TemporaryBase;
            internal CaptureRunRootLayout Layout;
            internal CaptureRunInitializationSessionIssue Issue;
            internal NvencRunChunkFileSession FileSession;
            internal NvencRunChunkWriter ChunkWriter;
            internal NvencChunkFinalizationResult FinalizationResult;
            internal NvencNativeOutputWorkerTeardown Teardown;
            internal NvencMainThreadResourceTeardown MainThreadTeardown;
            internal SentinelEncodePictureSubmitter Submitter;
            internal SentinelOutputBitstreamSource OutputSource;

            internal NvencSourceResourceReleaseCoordinator ReleaseCoordinator;
            internal NvencRunChunkContext Context;
            internal NvencFrameCompletionBoundary CompletionBoundary;
            internal NvencOrderedOutputProcessor OutputProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;
            internal NvencOrderedOutputWorkerService OutputWorker;
            internal NvencSubmissionAdmissionCoordinator Admission;

            /// <summary>
            /// The Main Thread's whole contribution: hand back a release when
            /// one is waiting, and tell the Submit Worker that something may
            /// have changed. The Output Worker is never told anything from
            /// here - its wakes come from the enqueue and from the shared gate
            /// being released.
            /// </summary>
            internal Action AdvanceMainThread;

            private readonly List<CaptureSurfaceLease> _surfaces = new List<CaptureSurfaceLease>();
            private GCHandle _lifetimeRoot;

            /// <summary>
            /// Roots the whole graph before anything native exists, so a
            /// coroutine abandoned after a failure cannot have its session,
            /// textures or workers finalized out from under a native call.
            /// </summary>
            internal SentinelRun()
            {
                _lifetimeRoot = GCHandle.Alloc(this);
            }

            internal void Build()
            {
                ProcessState = new NvencCaptureProcessState();
                WorkSlots = new NvencCaptureWorkSlotPool(ProcessState);
                SampleSlots = new NvencEncodeSampleSlotPool(ProcessState);
                SyncSlots = new NvencGpuConversionSyncPool(ProcessState);
                SubmitCredits = new NvencSubmitToOutputCreditPool(ProcessState);
                CompletionCredits = new NvencFrameCompletionCreditPool(ProcessState);
                SubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                BackendOwner = Guid.NewGuid();

                Assert.That(
                    NvencNativeEncoderSessionOwner.TryOpen(out Owner), Is.True,
                    "this Player's device must be able to open an encoder session.");

                Pool = new CaptureFrameRenderTargetPool(
                    NvencNativeEncoderSessionOwner.SourceSurfaceCount,
                    new CaptureFrameProfile(
                        7,
                        45.0,
                        CaptureSource.UnityRenderTexture,
                        CaptureEye.Left,
                        new CaptureImageRect(
                            0, 0, NvencBringUpProfileV1.Width, NvencBringUpProfileV1.Height),
                        0,
                        CapturePixelFormat.Rgba32));

                // The native session, once, in the established order.
                NvencBringUpProfileV1 profile = new NvencBringUpProfileV1(7);
                new NvencBringUpCapabilityProbeExecutionCoordinator(
                    new NvencBringUpCapabilityProbe(Owner)).Execute();
                Owner.InitializeEncoder(profile);

                IntPtr[] sources =
                    new IntPtr[NvencNativeEncoderSessionOwner.SourceSurfaceCount];
                Pool.CopyNativeTexturePointers(sources);
                Owner.BindSourceSurfaces(sources);
                Owner.PrepareInputSurfaces();
                Owner.PrepareConversionCommands();
                Owner.PrepareOutputBuffers();
                Owner.PrepareCompletionEvents();

                // The real teardown of this session's native resource
                // groups, which the Output Worker runs on its own thread.
                Teardown = new NvencNativeOutputWorkerTeardown(Owner);

                ReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    ProcessState, WorkSlots, SampleSlots, SyncSlots,
                    SubmitCredits, CompletionCredits,
                    new NvencNativeSourceReadCompletedSource(Owner),
                    new NvencSourceSurfaceReturnBoundary(), BackendOwner);

                // A real Run, initialized by the production boundaries. This
                // fixture creates only what a trusted base root already is:
                // the base itself and the one relative parent a Run sits
                // under. The Run roots, the markers, the lock, the chunks
                // directory and the chunk file are all made by the product.
                TemporaryBase = Path.Combine(
                    Path.GetTempPath(), "zantetsu-tierb-" + Guid.NewGuid().ToString("N"));
                Layout = new CaptureRunRootLayout(
                    Path.Combine(TemporaryBase, "staging"),
                    Path.Combine(TemporaryBase, "final"),
                    SentinelTestRunId);
                Directory.CreateDirectory(Path.GetDirectoryName(Layout.StagingRunRoot));
                Directory.CreateDirectory(Path.GetDirectoryName(Layout.FinalRunRoot));

                Assert.That(
                    new CaptureRunInitializationBootstrapCoordinator(
                        new CaptureRunLockAcquisitionCoordinator(CaptureRunLockOsBackend.Create()),
                        new CryptographicCaptureRunInitializationIdSource(),
                        new CaptureRunInitializationExecutionCoordinator(
                            CaptureRunRootOsProvisioner.Create(),
                            CaptureRunMarkerOsAtomicWriter.Create()))
                    .TryInitialize(Layout, out Issue),
                    Is.True,
                    "the production Run initialization must succeed on the temporary base");

                // One writer instance is both the appender and the finalizer,
                // over the one file session this Run's chunk lives in.
                FileSession = NvencRunChunkFileSession.Create(Issue);
                ChunkWriter = new NvencRunChunkWriter(FileSession);

                // One buffer, one sink, one collector, one completion
                // boundary: the Output Processor and the Run chunk context
                // must be talking about the same ones.
                NvencOwnedAccessUnitBuffer accessUnitBuffer =
                    new NvencOwnedAccessUnitBuffer(ProcessState);
                NvencRunChunkSink sink =
                    new NvencRunChunkSink(ProcessState, accessUnitBuffer, ChunkWriter);
                Context = new NvencRunChunkContext(
                    Issue, sink,
                    new NvencRunChunkFinalizationCoordinator(ChunkWriter), "chunk/0");
                OutputSource = new SentinelOutputBitstreamSource(Owner);
                NvencSubmittedOutputCollector collector = new NvencSubmittedOutputCollector(
                    ProcessState, WorkSlots, SampleSlots, SubmitCredits, CompletionCredits,
                    accessUnitBuffer, OutputSource);
                CompletionBoundary = new NvencFrameCompletionBoundary(
                    ProcessState, WorkSlots, SampleSlots, SubmitCredits, CompletionCredits,
                    accessUnitBuffer);

                // ---- the one composition order, fixed in one place ----
                // 1. Submit Processor
                Submitter = new SentinelEncodePictureSubmitter(Owner);
                NvencOrderedSubmitProcessor submitProcessor = new NvencOrderedSubmitProcessor(
                    ProcessState, SubmissionQueue, OutputQueue, WorkSlots, SampleSlots,
                    ReleaseCoordinator, Submitter);

                // 2. Submit Worker
                SubmitWorker = new NvencOrderedSubmitWorkerService(ProcessState, submitProcessor);

                // 3. Output Processor
                OutputProcessor = new NvencOrderedOutputProcessor(
                    ProcessState,
                    OutputQueue,
                    collector,
                    sink,
                    new NvencFailedBeforeSubmitReleaseCoordinator(
                        ProcessState, WorkSlots, SampleSlots, SubmitCredits, CompletionCredits),
                    new NvencSubmittedOutputAbandonRecoveryCoordinator(
                        ProcessState, collector, SampleSlots, accessUnitBuffer),
                    CompletionBoundary);

                // 4. Output Worker, which needs the Submit Worker to exist -
                //    the reason the wake cannot be a constructor argument
                OutputWorker = new NvencOrderedOutputWorkerService(
                    ProcessState, OutputProcessor, Context, SubmitWorker, Teardown);

                // 5. the one notification this Run needs, bound once: the
                //    wake that follows every shared-gate release, which is what
                //    both an enqueued record and a retried collector rely on.
                ProcessState.BindResourceResolutionReleaseNotification(OutputWorker);

                // 6. Only now may the Submit Worker run.
                SubmitWorker.Start();
                // ---- end of the composition ----

                // The real teardown of what Unity owns, which the Main
                // Thread runs after both workers have physically stopped.
                MainThreadTeardown = new NvencMainThreadResourceTeardown(
                    Context, Owner, Pool);

                Admission = new NvencSubmissionAdmissionCoordinator(
                    ProcessState, WorkSlots, SampleSlots, SyncSlots, SubmitCredits,
                    CompletionCredits, SubmissionQueue,
                    new NvencNativeGpuConversionCommandIssuer(Owner),
                    Context, BackendOwner);

                AdvanceMainThread = () =>
                {
                    ReleaseCoordinator.TryApplyPendingRelease();
                    SubmitWorker.Notify();
                };
            }

            /// <summary>Keeps a rented surface rooted for the Player's life.</summary>
            internal void Track(CaptureSurfaceLease surface)
            {
                _surfaces.Add(surface);
            }

            /// <summary>
            /// What the pipeline looks like right now, read from references
            /// this run already holds. Nothing here is a product observation
            /// surface, and nothing is changed by reading it.
            /// </summary>
            internal string DescribeState(string what)
            {
                Exception submitFailure = null;
                Exception outputFailure = null;
                SubmitWorker?.TryGetFailure(out submitFailure);
                OutputWorker?.TryGetFailure(out outputFailure);

                return what +
                    " subQ=" + SubmissionQueue.Count +
                    " outQ=" + OutputQueue.Count +
                    " outputPending=" + (OutputProcessor != null && OutputProcessor.HasPendingWork) +
                    " outputStopped=" + (OutputWorker != null && OutputWorker.IsStopped) +
                    " submitStopped=" + (SubmitWorker != null && SubmitWorker.IsStopped) +
                    " work=" + WorkSlots.OccupiedCount +
                    " sample=" + SampleSlots.OccupiedCount +
                    " sync=" + SyncSlots.OccupiedCount +
                    " submitCredits=" + SubmitCredits.OccupiedCount +
                    " completionCredits=" + CompletionCredits.OccupiedCount +
                    " appends=" + (ChunkWriter == null ? -1L : ChunkWriter.AppendCount) +
                    " writerState=" + (ChunkWriter == null ? "none" : ChunkWriter.State.ToString()) +
                    " runRoot=" + (Layout == null ? "none" : Layout.StagingRunRoot) +
                    " poisoned=" + ProcessState.IsPoisoned +
                    " submitFatal=" + (submitFailure == null ? "none" : submitFailure.GetType().Name) +
                    " outputFatal=" + (outputFailure == null ? "none" : outputFailure.GetType().Name);
            }

            /// <summary>
            /// The ordinary end of a Run: freeze the ledger, drain, finalize
            /// once, collect the terminal, tear the Output Worker down, and
            /// confirm both threads physically stopped. Reports the first step
            /// that did not finish and changes nothing else.
            /// </summary>
            internal IEnumerator Terminate(int expectedAcceptedFrames, Action<string> failure)
            {
                Assert.That(ProcessState.TryBeginDrain(), Is.True);
                Assert.That(SubmitWorker.BeginDrain(), Is.True);

                bool drained = false;
                yield return AdvanceUntil(
                    () => SubmitWorker.DrainCompleted, AdvanceMainThread, value => drained = value);
                if (!drained)
                {
                    failure(DescribeState("the Submit Worker did not complete its drain"));
                    yield break;
                }

                // Freeze the admitted ledger once the Run has stopped
                // accepting and before the Output Worker finalizes the
                // context: the freeze is only offered while draining.
                Assert.That(
                    Context.TryFreezeAcceptedFrames(
                        out NvencRunAcceptedFrameSnapshot acceptedFrames), Is.True);
                Assert.That(acceptedFrames.Count, Is.EqualTo(expectedAcceptedFrames));

                bool requested = false;
                yield return AdvanceUntil(
                    () => OutputWorker.TryRequestFinalize(), AdvanceMainThread,
                    value => requested = value);
                if (!requested)
                {
                    failure(DescribeState("the Output terminal request was never accepted"));
                    yield break;
                }

                NvencRunChunkTerminalOutcome outcome = default;
                bool collected = false;
                yield return AdvanceUntil(
                    () => OutputWorker.TryCollectTerminal(out outcome), AdvanceMainThread,
                    value => collected = value);
                if (!collected)
                {
                    failure(DescribeState("the Output terminal result was never collected"));
                    yield break;
                }

                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(outcome.Result, Is.Not.Null);
                FinalizationResult = outcome.Result;

                bool teardownRequested = false;
                yield return AdvanceUntil(
                    () => OutputWorker.TryRequestTeardown(), AdvanceMainThread,
                    value => teardownRequested = value);

                bool teardownCompleted = false;
                if (teardownRequested)
                {
                    yield return AdvanceUntil(
                        () => OutputWorker.TeardownCompleted, AdvanceMainThread,
                        value => teardownCompleted = value);
                }

                if (!teardownCompleted)
                {
                    failure(DescribeState("the Output Worker teardown did not complete"));
                    yield break;
                }

                bool stopped = false;
                yield return AdvanceUntil(
                    () => SubmitWorker.IsStopped && OutputWorker.IsStopped, null,
                    value => stopped = value);
                if (!stopped)
                {
                    failure(DescribeState("a worker thread did not physically stop"));
                    yield break;
                }

                failure(null);
            }

            /// <summary>
            /// Releases the native resource groups in the exact reverse of the
            /// order they were prepared in, and only once both workers have
            /// physically stopped and every lease is back - the two things
            /// that together exclude a worker still being inside the driver.
            /// </summary>
            internal void ReleaseAfterProvenStop()
            {
                // The Output Worker already released the native groups and
                // closed the session inside its own teardown; this side only
                // runs once that is done and both threads are gone.
                Assert.That(OutputWorker.TeardownCompleted, Is.True);
                Assert.That(SubmitWorker.IsStopped, Is.True);
                Assert.That(OutputWorker.IsStopped, Is.True);
                Assert.That(SyncSlots.OccupiedCount, Is.Zero);
                Assert.That(SampleSlots.OccupiedCount, Is.Zero);
                Assert.That(WorkSlots.OccupiedCount, Is.Zero);
                Assert.That(
                    Owner.IsOpen, Is.False,
                    "the Output Worker teardown must already have closed the session");

                // Physically stopped, so no held record is waiting on the gate
                // wake any more and the binding can go. It is never released
                // before the stop, and never on the poison path.
                ProcessState.UnbindResourceResolutionReleaseNotification(OutputWorker);

                // Only a stopped worker is disposed.
                SubmitWorker.Dispose();
                OutputWorker.Dispose();

                // What Unity owns goes last, on this thread, through the
                // production boundary - and its receipt is only issued for
                // this exact Run.
                NvencMainThreadTextureTeardownReceipt receipt = MainThreadTeardown.TearDown();
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsIssuedFor(MainThreadTeardown, Context), Is.True);

                // A session that really closed cannot be used again, and a
                // pool that really went cannot lend another surface.
                Assert.Throws<InvalidOperationException>(() => Owner.PrepareCompletionEvents());
                Assert.That(
                    () => Pool.TryRent(out CaptureFrameRenderTargetLease _), Throws.Exception,
                    "a disposed source pool cannot lend another surface");

                // The chunk's file handles go before the ownership lease that
                // holds this Run's OS lock, so the lock is never released
                // while the Run still has a file open under it.
                FileSession.Dispose();
                Issue.OwnershipLease.Dispose();
                Assert.That(Issue.OwnershipLease.IsReleaseComplete, Is.True);
            }

            /// <summary>
            /// Removes this Run's temporary tree, and only on the path that
            /// has proved every handle and the lock were released: an unknown
            /// state keeps the tree for whoever looks afterwards.
            /// </summary>
            internal void DeleteTemporaryBase()
            {
                Assert.That(Issue.OwnershipLease.IsReleaseComplete, Is.True);
                Directory.Delete(TemporaryBase, true);
                Assert.That(Directory.Exists(TemporaryBase), Is.False);
            }

            /// <summary>
            /// Ends the run's hold on this process. A completed teardown
            /// releases the root; anything else keeps the whole graph alive
            /// for the Player's lifetime, because Poison and Notify can ask a
            /// worker to stop but cannot cancel a native call or prove the GPU
            /// is finished with these resources.
            /// </summary>
            internal void Retire(bool teardownDone, string stalledAt)
            {
                if (teardownDone)
                {
                    _lifetimeRoot.Free();
                    return;
                }

                QuietStep(() => ProcessState?.TryPoison());
                QuietStep(() => SubmitWorker?.Notify());
                QuietStep(() => OutputWorker?.Notify());

                // Nothing on disk is guessed at either: a Run whose workers,
                // native calls or file handles are in an unknown state keeps
                // its lock, its handles and its temporary tree, and the path
                // is written down for whoever looks afterwards.
                SentinelMarker(
                    "RETAINED owner graph; external process termination required; " +
                    (stalledAt ?? DescribeState("no stage recorded")) +
                    " retainedPath=" + (TemporaryBase ?? "none"));
            }
        }

        private sealed class SentinelChunkWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private readonly byte[] _accessUnit =
                new byte[(int)NvencBringUpProfileV1.MaxAccessUnitByteLength];

            private int _appendCount;
            private int _finalizeCount;

            internal int AppendCount => Volatile.Read(ref _appendCount);

            internal int FinalizeCount => Volatile.Read(ref _finalizeCount);

            internal int ValidLength { get; private set; }

            internal int ShortestValidLength { get; private set; } = int.MaxValue;

            internal int LongestValidLength { get; private set; }

            internal bool EveryAppendStartedWithAnnexB { get; private set; } = true;

            internal byte[] AccessUnit => _accessUnit;

            internal string AppendThreadName { get; private set; }

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                AppendThreadName = Thread.CurrentThread.Name;
                ValidLength = validLength;

                if (validLength < ShortestValidLength)
                {
                    ShortestValidLength = validLength;
                }

                if (validLength > LongestValidLength)
                {
                    LongestValidLength = validLength;
                }

                if (buffer != null && validLength > 0 && validLength <= _accessUnit.Length)
                {
                    Array.Copy(buffer, offset, _accessUnit, 0, validLength);
                }

                // Checked here rather than kept, so every access unit is
                // examined and not only the last one handed over.
                bool annexB = validLength >= 4 &&
                    _accessUnit[0] == 0x00 && _accessUnit[1] == 0x00 &&
                    (_accessUnit[2] == 0x01 ||
                        (_accessUnit[2] == 0x00 && _accessUnit[3] == 0x01));
                if (!annexB)
                {
                    EveryAppendStartedWithAnnexB = false;
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
