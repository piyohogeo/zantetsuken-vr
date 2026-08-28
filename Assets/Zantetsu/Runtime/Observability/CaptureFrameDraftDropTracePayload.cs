using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, value-type payload for one normal capture frame draft drop
    /// trace. Holds only the draft's correlation context and the drop reason.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Instances are created only by
    /// <see cref="CaptureFrameDraftRegistry.TryConsumeDropTrace"/> and are valid
    /// only for the three normal draft drop reasons
    /// (<see cref="CaptureFrameDropReason.PngEncodeFailed"/>,
    /// <see cref="CaptureFrameDropReason.PngStagingStoreFull"/>, and
    /// <see cref="CaptureFrameDropReason.CaptureCancelled"/>). The freeze
    /// terminal reason is never represented by this payload.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> is computed from the held values and no independent
    /// validity field is stored. The struct holds only value-type fields, owns
    /// nothing, and does not implement <see cref="IDisposable"/>. It is
    /// internal so it is never exposed as a public result or receipt.
    /// </para>
    /// </remarks>
    internal readonly struct CaptureFrameDraftDropTracePayload
    {
        public CaptureFrameDraftTraceContext TraceContext { get; }

        public CaptureFrameDropReason Reason { get; }

        public bool IsValid =>
            TraceContext.CaptureFrameId > 0
            && TraceContext.TestRunId > 0
            && (Reason == CaptureFrameDropReason.PngEncodeFailed
                || Reason == CaptureFrameDropReason.PngStagingStoreFull
                || Reason == CaptureFrameDropReason.CaptureCancelled);

        internal CaptureFrameDraftDropTracePayload(
            in CaptureFrameDraftTraceContext traceContext,
            CaptureFrameDropReason reason)
        {
            if (reason != CaptureFrameDropReason.PngEncodeFailed
                && reason != CaptureFrameDropReason.PngStagingStoreFull
                && reason != CaptureFrameDropReason.CaptureCancelled)
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Reason must be PngEncodeFailed, PngStagingStoreFull, or CaptureCancelled.");
            }

            if (traceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(traceContext), traceContext.CaptureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (traceContext.TestRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(traceContext), traceContext.TestRunId, "Test run ID must be greater than zero.");
            }

            TraceContext = traceContext;
            Reason = reason;
        }
    }
}
