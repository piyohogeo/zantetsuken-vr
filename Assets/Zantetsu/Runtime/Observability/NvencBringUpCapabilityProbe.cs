using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The production capability probe: it reads one bring-up capability
    /// snapshot out of an encoder session that is already open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is someone else's. This probe does not open, close,
    /// replace, or dispose it, and holds nothing but the reference it was
    /// given - no snapshot, attempt counter, success latch, or native handle of
    /// its own. Observing twice fails because the session observes once, not
    /// because this type counts.
    /// </para>
    /// <para>
    /// Three of the snapshot's facts are settled by that session existing
    /// rather than asked for again. The session was opened on the exact D3D11
    /// device the native plugin holds as Unity's current one, which is what
    /// makes the current graphics API D3D11; and NVENC accepted that device and
    /// opened an encoder on it, which is what makes the active adapter an
    /// NVIDIA one. Neither is re-derived from a device vendor string, a GPU
    /// name, a PCI ID, NVML, NVAPI, or a second adapter enumeration. The
    /// Windows version is compared as this process reports it. The remaining
    /// six values are the session's own observation, copied through unchanged.
    /// </para>
    /// <para>
    /// What the snapshot is for is admission before the encoder is
    /// initialized. It does not promise that NvEncInitializeEncoder will
    /// succeed, that this device will still be Unity's current one after a
    /// reset, that a completion event will register, that keeping the output
    /// out of video memory will be accepted, or that the fixed profile's whole
    /// combination will be. The real initialization answers those, and no
    /// device generation or reset history is tracked here to pretend otherwise.
    /// </para>
    /// </remarks>
    internal sealed class NvencBringUpCapabilityProbe : INvencBringUpCapabilityProbe
    {
        private readonly NvencNativeEncoderSessionOwner _sessionOwner;

        internal NvencBringUpCapabilityProbe(NvencNativeEncoderSessionOwner sessionOwner)
        {
            if (sessionOwner == null)
            {
                throw new ArgumentNullException(nameof(sessionOwner));
            }

            if (!sessionOwner.IsOpen)
            {
                throw new ArgumentException(
                    "The encoder session must be open to observe its capabilities.",
                    nameof(sessionOwner));
            }

            _sessionOwner = sessionOwner;
        }

        public NvencBringUpCapabilityV1 Probe()
        {
            if (!_sessionOwner.IsOpen)
            {
                throw new InvalidOperationException(
                    "The encoder session is no longer open; there is nothing to observe.");
            }

            NvencEncoderCapabilityObservationV1 observation =
                _sessionOwner.ObserveCapabilities();

            return new NvencBringUpCapabilityV1(
                IsWindows10OrNewer(),
                // NVENC opened an encoder on this exact device.
                true,
                // The session was opened on the device the plugin holds as
                // Unity's current D3D11 device.
                true,
                observation.SupportsAsyncEncode,
                observation.SupportsH264Encode,
                observation.SupportsH264HighProfile,
                observation.SupportsNv12Input,
                observation.MaximumEncodeWidth,
                observation.MaximumEncodeHeight);
        }

        /// <summary>
        /// The Windows version this process reports, compared and nothing more.
        /// </summary>
        private static bool IsWindows10OrNewer()
        {
            OperatingSystem operatingSystem = Environment.OSVersion;

            return operatingSystem.Platform == PlatformID.Win32NT
                && operatingSystem.Version.Major >= 10;
        }
    }
}
