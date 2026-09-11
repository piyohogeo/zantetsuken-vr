using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the synchronous NVENC bring-up capability
    /// observation boundary: one probe call per Execute, the observed snapshot
    /// returned unchanged whatever it reports, an uninitialized value refused,
    /// and a probe failure propagated as it was thrown.
    /// </summary>
    /// <remarks>
    /// The probe is a fixture-local recording fake. Nothing here touches a
    /// thread, a task, the filesystem, a Unity static API, or a native entry
    /// point, and the coordinator is never asked to classify anything - the
    /// admission validator's own fixture owns that.
    /// </remarks>
    public class NvencBringUpCapabilityProbeExecutionContractTests
    {
        private static NvencBringUpCapabilityV1 MakeCapability(
            bool isWindows10OrNewer = true,
            bool isActiveAdapterNvidia = true,
            bool isCurrentGraphicsApiD3D11 = true,
            bool activeAdapterSupportsWddm = true,
            bool activeAdapterSupportsAsyncEncode = true,
            bool activeAdapterSupportsCompletionEvent = true,
            bool isActiveAdapterTcc = false,
            bool activeAdapterCanUseOutputInVidmemZero = true,
            bool activeAdapterSupportsH264Encode = true,
            bool activeAdapterSupportsH264HighProfile = true,
            bool activeAdapterSupportsNv12Input = true,
            int maximumEncodeWidth = 4096,
            int maximumEncodeHeight = 4096)
        {
            return new NvencBringUpCapabilityV1(
                isWindows10OrNewer,
                isActiveAdapterNvidia,
                isCurrentGraphicsApiD3D11,
                activeAdapterSupportsWddm,
                activeAdapterSupportsAsyncEncode,
                activeAdapterSupportsCompletionEvent,
                isActiveAdapterTcc,
                activeAdapterCanUseOutputInVidmemZero,
                activeAdapterSupportsH264Encode,
                activeAdapterSupportsH264HighProfile,
                activeAdapterSupportsNv12Input,
                maximumEncodeWidth,
                maximumEncodeHeight);
        }

        private static void AssertSameFacts(
            NvencBringUpCapabilityV1 expected, NvencBringUpCapabilityV1 observed)
        {
            Assert.That(observed.IsInitialized, Is.EqualTo(expected.IsInitialized));
            Assert.That(observed.IsWindows10OrNewer, Is.EqualTo(expected.IsWindows10OrNewer));
            Assert.That(observed.IsActiveAdapterNvidia,
                Is.EqualTo(expected.IsActiveAdapterNvidia));
            Assert.That(observed.IsCurrentGraphicsApiD3D11,
                Is.EqualTo(expected.IsCurrentGraphicsApiD3D11));
            Assert.That(observed.ActiveAdapterSupportsWddm,
                Is.EqualTo(expected.ActiveAdapterSupportsWddm));
            Assert.That(observed.ActiveAdapterSupportsAsyncEncode,
                Is.EqualTo(expected.ActiveAdapterSupportsAsyncEncode));
            Assert.That(observed.ActiveAdapterSupportsCompletionEvent,
                Is.EqualTo(expected.ActiveAdapterSupportsCompletionEvent));
            Assert.That(observed.IsActiveAdapterTcc, Is.EqualTo(expected.IsActiveAdapterTcc));
            Assert.That(observed.ActiveAdapterCanUseOutputInVidmemZero,
                Is.EqualTo(expected.ActiveAdapterCanUseOutputInVidmemZero));
            Assert.That(observed.ActiveAdapterSupportsH264Encode,
                Is.EqualTo(expected.ActiveAdapterSupportsH264Encode));
            Assert.That(observed.ActiveAdapterSupportsH264HighProfile,
                Is.EqualTo(expected.ActiveAdapterSupportsH264HighProfile));
            Assert.That(observed.ActiveAdapterSupportsNv12Input,
                Is.EqualTo(expected.ActiveAdapterSupportsNv12Input));
            Assert.That(observed.MaximumEncodeWidth, Is.EqualTo(expected.MaximumEncodeWidth));
            Assert.That(observed.MaximumEncodeHeight, Is.EqualTo(expected.MaximumEncodeHeight));
        }

        // ---- Construction ----

        [Test]
        public void Constructor_NullProbe_Rejected()
        {
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => new NvencBringUpCapabilityProbeExecutionCoordinator(null)).ParamName,
                Is.EqualTo("probe"));
        }

        /// <summary>
        /// One readonly probe reference and nothing else: no retained
        /// snapshot, latch, counter, or cache, and no disposal duty.
        /// </summary>
        [Test]
        public void Coordinator_HoldsOneReadonlyProbeAndIsNotDisposable()
        {
            Type type = typeof(NvencBringUpCapabilityProbeExecutionCoordinator);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].IsPublic, Is.False);
            Assert.That(fields[0].IsInitOnly, Is.True);
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(INvencBringUpCapabilityProbe)));
            Assert.That(
                type.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static),
                Is.Empty);
        }

        // ---- One observation per call ----

        [Test]
        public void Execute_CallsTheProbeExactlyOnce()
        {
            RecordingProbe probe = new RecordingProbe(MakeCapability());
            NvencBringUpCapabilityProbeExecutionCoordinator coordinator =
                new NvencBringUpCapabilityProbeExecutionCoordinator(probe);

            coordinator.Execute();

            Assert.That(probe.CallCount, Is.EqualTo(1));

            // Each call is one attempt, so a second call is a second
            // observation - and nothing is served from a cache.
            coordinator.Execute();

            Assert.That(probe.CallCount, Is.EqualTo(2));
        }

        [Test]
        public void Execute_ReturnsASupportedObservationUnchanged()
        {
            NvencBringUpCapabilityV1 observed = MakeCapability(
                maximumEncodeWidth: 4096, maximumEncodeHeight: 2160);
            RecordingProbe probe = new RecordingProbe(observed);

            NvencBringUpCapabilityV1 result =
                new NvencBringUpCapabilityProbeExecutionCoordinator(probe).Execute();

            Assert.That(probe.CallCount, Is.EqualTo(1));
            Assert.That(result.IsInitialized, Is.True);
            AssertSameFacts(observed, result);
        }

        /// <summary>
        /// A completed observation that found nothing supported is still an
        /// observation: it comes back with the false, zero, and negative values
        /// it was observed with, and this boundary does not turn it into a
        /// decision.
        /// </summary>
        [Test]
        public void Execute_ReturnsAnUnsupportedObservationUnchangedAndClassifiesNothing()
        {
            NvencBringUpCapabilityV1 observed = MakeCapability(
                isWindows10OrNewer: false,
                isActiveAdapterNvidia: false,
                isCurrentGraphicsApiD3D11: false,
                activeAdapterSupportsWddm: false,
                activeAdapterSupportsAsyncEncode: false,
                activeAdapterSupportsCompletionEvent: false,
                isActiveAdapterTcc: true,
                activeAdapterCanUseOutputInVidmemZero: false,
                activeAdapterSupportsH264Encode: false,
                activeAdapterSupportsH264HighProfile: false,
                activeAdapterSupportsNv12Input: false,
                maximumEncodeWidth: 0,
                maximumEncodeHeight: -1);
            RecordingProbe probe = new RecordingProbe(observed);

            NvencBringUpCapabilityV1 result =
                new NvencBringUpCapabilityProbeExecutionCoordinator(probe).Execute();

            Assert.That(probe.CallCount, Is.EqualTo(1));
            Assert.That(result.IsInitialized, Is.True);
            AssertSameFacts(observed, result);
            Assert.That(result.MaximumEncodeWidth, Is.Zero);
            Assert.That(result.MaximumEncodeHeight, Is.EqualTo(-1));
        }

        // ---- What is not an observation ----

        [Test]
        public void Execute_UninitializedSnapshot_Rejected()
        {
            RecordingProbe probe = new RecordingProbe(default);
            NvencBringUpCapabilityProbeExecutionCoordinator coordinator =
                new NvencBringUpCapabilityProbeExecutionCoordinator(probe);

            Assert.Throws<InvalidOperationException>(() => coordinator.Execute());

            // The refusal is not a retry either.
            Assert.That(probe.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_ProbeFailure_PropagatesThatExactExceptionWithoutRetrying()
        {
            InvalidTimeZoneException failure = new InvalidTimeZoneException("probe failed");
            RecordingProbe probe = new RecordingProbe(MakeCapability()) { Throw = failure };
            NvencBringUpCapabilityProbeExecutionCoordinator coordinator =
                new NvencBringUpCapabilityProbeExecutionCoordinator(probe);

            Assert.That(
                Assert.Throws<InvalidTimeZoneException>(() => coordinator.Execute()),
                Is.SameAs(failure));
            Assert.That(probe.CallCount, Is.EqualTo(1));

            // A later call is the caller's own new attempt, and the boundary
            // kept nothing from the failed one.
            probe.Throw = null;
            NvencBringUpCapabilityV1 result = coordinator.Execute();

            Assert.That(probe.CallCount, Is.EqualTo(2));
            Assert.That(result.IsInitialized, Is.True);
        }

        // ---- Fixture helpers ----

        /// <summary>
        /// A probe that records its calls and returns - or throws - exactly
        /// what the test configured. It observes nothing.
        /// </summary>
        private sealed class RecordingProbe : INvencBringUpCapabilityProbe
        {
            private readonly NvencBringUpCapabilityV1 _capability;

            internal RecordingProbe(NvencBringUpCapabilityV1 capability)
            {
                _capability = capability;
            }

            internal int CallCount { get; private set; }

            internal Exception Throw { get; set; }

            public NvencBringUpCapabilityV1 Probe()
            {
                CallCount++;

                if (Throw != null)
                {
                    throw Throw;
                }

                return _capability;
            }
        }
    }
}
