using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable capability snapshot for the NVENC bring-up boundary, bound to
    /// the running configuration actually used for capture: the active adapter
    /// and the current Graphics API. A simple value type carrying only the
    /// OS / active-adapter / API facts the admission validator compares. Every
    /// value is caller-supplied, so Tier A tests can inject fakes; no OS, GPU,
    /// or NVENC probe is performed here.
    /// </summary>
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

        internal bool IsInitialized => _initialized;

        internal NvencBringUpCapabilityV1(
            bool isWindows10OrNewer,
            bool isActiveAdapterNvidia,
            bool isCurrentGraphicsApiD3D11,
            bool activeAdapterSupportsWddm,
            bool activeAdapterSupportsAsyncEncode,
            bool activeAdapterSupportsCompletionEvent,
            bool isActiveAdapterTcc,
            bool activeAdapterCanUseOutputInVidmemZero)
        {
            IsWindows10OrNewer = isWindows10OrNewer;
            IsActiveAdapterNvidia = isActiveAdapterNvidia;
            IsCurrentGraphicsApiD3D11 = isCurrentGraphicsApiD3D11;
            ActiveAdapterSupportsWddm = activeAdapterSupportsWddm;
            ActiveAdapterSupportsAsyncEncode = activeAdapterSupportsAsyncEncode;
            ActiveAdapterSupportsCompletionEvent = activeAdapterSupportsCompletionEvent;
            IsActiveAdapterTcc = isActiveAdapterTcc;
            ActiveAdapterCanUseOutputInVidmemZero = activeAdapterCanUseOutputInVidmemZero;
            _initialized = true;
        }
    }
}
