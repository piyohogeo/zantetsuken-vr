using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed, append-only classification of which current owner of the Run's
    /// OS lock a capture-complete lifecycle evidence correlates to.
    /// </summary>
    /// <remarks>
    /// Values are explicitly fixed and must only ever be appended; existing
    /// values must never be renumbered or removed.
    /// </remarks>
    internal enum CaptureRunPublicationCaptureCompleteLifecycleOwnerKind
    {
        None = 0,
        FreshSession = 1,
        RecoveryOpenOutcome = 2,
    }
}
