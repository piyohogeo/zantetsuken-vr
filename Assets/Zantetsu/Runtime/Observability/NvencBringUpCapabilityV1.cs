using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable capability snapshot for the NVENC bring-up boundary, bound to
    /// the running configuration actually used for capture: the active adapter
    /// and the current Graphics API. A simple value type carrying only the
    /// OS / active-adapter / API / encoder facts the admission validator
    /// compares. Every value is caller-supplied, so Tier A tests can inject
    /// fakes; no OS, GPU, or NVENC probe is performed here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every member is a fact that can be observed directly before the encoder
    /// is initialized. The encoder facts are kept as the plain observed values
    /// they are: three booleans and two maximum dimensions. A false or a zero -
    /// or a negative maximum - is an initialized snapshot that reports no
    /// support, not an invalid one, so a probe that found nothing can still be
    /// represented exactly as it was observed and rejected by the admission
    /// boundary rather than by a constructor.
    /// </para>
    /// <para>
    /// What deliberately has no member here is anything that cannot be observed
    /// on its own. The driver model is one: an NVENC session opened on the
    /// D3D11 device this process is already using, reporting async encode
    /// support, is the path that has to hold - there is no separate WDDM or TCC
    /// fact to consult, and no NVML or NVAPI dependency is taken to invent one.
    /// Completion-event availability is another: whether an event can be
    /// created and registered is answered by actually doing it during Run
    /// initialization, not guessed beforehand. Keeping the encoder's output out
    /// of video memory is a third - that is a value this project requests when
    /// it initializes the encoder, not a capability to query.
    /// </para>
    /// <para>
    /// No adapter identity, SDK or driver string, codec, profile, or
    /// input-format GUID list is held. Every member is a pre-initialization
    /// observation and nothing more: a snapshot in which all of them are
    /// supported does not prove that an encoder session initializes, that a
    /// completion event registers, or that any particular combination of the
    /// two maximums is accepted. Those answers belong to the real session
    /// initialization.
    /// </para>
    /// </remarks>
    internal readonly struct NvencBringUpCapabilityV1
    {
        private readonly bool _initialized;

        internal bool IsWindows10OrNewer { get; }

        internal bool IsActiveAdapterNvidia { get; }

        internal bool IsCurrentGraphicsApiD3D11 { get; }

        internal bool ActiveAdapterSupportsAsyncEncode { get; }

        internal bool ActiveAdapterSupportsH264Encode { get; }

        internal bool ActiveAdapterSupportsH264HighProfile { get; }

        internal bool ActiveAdapterSupportsNv12Input { get; }

        /// <summary>
        /// The largest encode width the observation reported, as observed. Zero
        /// or a negative value reports no usable width.
        /// </summary>
        internal int MaximumEncodeWidth { get; }

        /// <summary>
        /// The largest encode height the observation reported, as observed.
        /// Zero or a negative value reports no usable height.
        /// </summary>
        internal int MaximumEncodeHeight { get; }

        internal bool IsInitialized => _initialized;

        internal NvencBringUpCapabilityV1(
            bool isWindows10OrNewer,
            bool isActiveAdapterNvidia,
            bool isCurrentGraphicsApiD3D11,
            bool activeAdapterSupportsAsyncEncode,
            bool activeAdapterSupportsH264Encode,
            bool activeAdapterSupportsH264HighProfile,
            bool activeAdapterSupportsNv12Input,
            int maximumEncodeWidth,
            int maximumEncodeHeight)
        {
            IsWindows10OrNewer = isWindows10OrNewer;
            IsActiveAdapterNvidia = isActiveAdapterNvidia;
            IsCurrentGraphicsApiD3D11 = isCurrentGraphicsApiD3D11;
            ActiveAdapterSupportsAsyncEncode = activeAdapterSupportsAsyncEncode;
            ActiveAdapterSupportsH264Encode = activeAdapterSupportsH264Encode;
            ActiveAdapterSupportsH264HighProfile = activeAdapterSupportsH264HighProfile;
            ActiveAdapterSupportsNv12Input = activeAdapterSupportsNv12Input;
            MaximumEncodeWidth = maximumEncodeWidth;
            MaximumEncodeHeight = maximumEncodeHeight;
            _initialized = true;
        }
    }
}
