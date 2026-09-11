using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous boundary around one NVENC bring-up capability observation:
    /// it calls its exact probe once and hands back the snapshot that probe
    /// returned, unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only thing checked here is that the observation produced an
    /// initialized snapshot. A default or otherwise uninitialized value is not
    /// an observation and is refused with an
    /// <see cref="InvalidOperationException"/>; a snapshot reporting no support
    /// - false facts, a zero or negative maximum - is a completed observation
    /// and is returned exactly as it was observed.
    /// </para>
    /// <para>
    /// Nothing is classified, corrected, defaulted, or normalized on the way
    /// through, and this type never decides Supported or Unsupported - that
    /// stays <see cref="NvencBringUpAdmissionValidatorV1"/>'s. A probe
    /// exception propagates with its own stack: it is not caught, wrapped,
    /// retried, or turned into a snapshot.
    /// </para>
    /// <para>
    /// It holds one readonly probe reference and nothing else - no retained
    /// snapshot, success latch, attempt counter, cache, thread, timer, or
    /// handle - and is not an <see cref="IDisposable"/>: the probe's own
    /// lifetime belongs to whoever supplied it. Each call is one attempt, so
    /// calling twice is two observations, exactly as the caller asked.
    /// </para>
    /// </remarks>
    internal sealed class NvencBringUpCapabilityProbeExecutionCoordinator
    {
        private readonly INvencBringUpCapabilityProbe _probe;

        internal NvencBringUpCapabilityProbeExecutionCoordinator(
            INvencBringUpCapabilityProbe probe)
        {
            _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        }

        /// <summary>
        /// One observation attempt: the probe is called exactly once and its
        /// snapshot is returned if it is initialized.
        /// </summary>
        internal NvencBringUpCapabilityV1 Execute()
        {
            NvencBringUpCapabilityV1 capability = _probe.Probe();

            if (!capability.IsInitialized)
            {
                throw new InvalidOperationException(
                    "The capability probe returned an uninitialized snapshot.");
            }

            return capability;
        }
    }
}
