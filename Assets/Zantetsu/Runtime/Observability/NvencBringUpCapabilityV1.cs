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
    /// The encoder facts are kept as the plain observed values they are: three
    /// booleans and two maximum dimensions. A false or a zero - or a negative
    /// maximum - is an initialized snapshot that reports no support, not an
    /// invalid one, so a probe that found nothing can still be represented
    /// exactly as it was observed and rejected by the admission boundary rather
    /// than by a constructor.
    /// </para>
    /// <para>
    /// No adapter identity, SDK or driver string, codec, profile, or
    /// input-format GUID list is held, and nothing here proves that an NVENC
    /// session can actually be created: the real session initialization remains
    /// the authority on the requested configuration.
    /// </para>
    /// </remarks>
    internal readonly struct NvencBringUpCapabilityV1
    {
        private readonly bool _initialized;

        internal bool IsWindows10OrNewer { get; }

        internal bool IsActiveAdapterNvidia { get; }

        internal bool IsCurrentGraphicsApiD3D11 { get; }

        internal bool ActiveAdapterSupportsWddm { get; }

        internal bool ActiveAdapterSupportsAsyncEncode { get; }

        internal bool ActiveAdapterSupportsCompletionEvent { get; }

        internal bool IsActiveAdapterTcc { get; }

        internal bool ActiveAdapterCanUseOutputInVidmemZero { get; }

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
            bool activeAdapterSupportsWddm,
            bool activeAdapterSupportsAsyncEncode,
            bool activeAdapterSupportsCompletionEvent,
            bool isActiveAdapterTcc,
            bool activeAdapterCanUseOutputInVidmemZero,
            bool activeAdapterSupportsH264Encode,
            bool activeAdapterSupportsH264HighProfile,
            bool activeAdapterSupportsNv12Input,
            int maximumEncodeWidth,
            int maximumEncodeHeight)
        {
            IsWindows10OrNewer = isWindows10OrNewer;
            IsActiveAdapterNvidia = isActiveAdapterNvidia;
            IsCurrentGraphicsApiD3D11 = isCurrentGraphicsApiD3D11;
            ActiveAdapterSupportsWddm = activeAdapterSupportsWddm;
            ActiveAdapterSupportsAsyncEncode = activeAdapterSupportsAsyncEncode;
            ActiveAdapterSupportsCompletionEvent = activeAdapterSupportsCompletionEvent;
            IsActiveAdapterTcc = isActiveAdapterTcc;
            ActiveAdapterCanUseOutputInVidmemZero = activeAdapterCanUseOutputInVidmemZero;
            ActiveAdapterSupportsH264Encode = activeAdapterSupportsH264Encode;
            ActiveAdapterSupportsH264HighProfile = activeAdapterSupportsH264HighProfile;
            ActiveAdapterSupportsNv12Input = activeAdapterSupportsNv12Input;
            MaximumEncodeWidth = maximumEncodeWidth;
            MaximumEncodeHeight = maximumEncodeHeight;
            _initialized = true;
        }
    }
}
