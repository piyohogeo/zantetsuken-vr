using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable capability snapshot for the NVENC bring-up boundary. A simple
    /// value type carrying only the OS / adapter / API facts the admission
    /// validator compares. Every value is caller-supplied, so Tier A tests can
    /// inject fakes; no OS, GPU, or NVENC probe is performed here.
    /// </summary>
    internal readonly struct NvencBringUpCapabilityV1
    {
        private readonly bool _initialized;

        internal bool IsWindows10OrNewer { get; }

        internal bool HasNvidiaAdapter { get; }

        internal bool SupportsD3D11 { get; }

        internal bool SupportsWddm { get; }

        internal bool SupportsAsyncEncode { get; }

        internal bool SupportsCompletionEvent { get; }

        internal bool IsTcc { get; }

        internal bool CanUseOutputInVidmemZero { get; }

        internal bool IsInitialized => _initialized;

        internal NvencBringUpCapabilityV1(
            bool isWindows10OrNewer,
            bool hasNvidiaAdapter,
            bool supportsD3D11,
            bool supportsWddm,
            bool supportsAsyncEncode,
            bool supportsCompletionEvent,
            bool isTcc,
            bool canUseOutputInVidmemZero)
        {
            IsWindows10OrNewer = isWindows10OrNewer;
            HasNvidiaAdapter = hasNvidiaAdapter;
            SupportsD3D11 = supportsD3D11;
            SupportsWddm = supportsWddm;
            SupportsAsyncEncode = supportsAsyncEncode;
            SupportsCompletionEvent = supportsCompletionEvent;
            IsTcc = isTcc;
            CanUseOutputInVidmemZero = canUseOutputInVidmemZero;
            _initialized = true;
        }
    }
}
