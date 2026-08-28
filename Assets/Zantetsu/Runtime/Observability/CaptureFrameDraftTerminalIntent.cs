using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, value-carrier for one stage or drop terminal intent produced
    /// for a capture frame draft, to be handed to the future single terminal
    /// coordinator through the terminal intent queue.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An intent has no public constructor; instances are created only by
    /// <see cref="CreateStage"/> and <see cref="CreateDrop"/>. After creation it
    /// is immutable. <see cref="IsStage"/>, <see cref="IsDrop"/>, and
    /// <see cref="HasPrivateBuffer"/> are derived from <see cref="StagingEntry"/>
    /// and <see cref="DropReason"/>; no independent mutable flag is stored.
    /// </para>
    /// <para>
    /// Ownership: a successful <see cref="CreateStage"/> does not transfer the
    /// PNG entry; the producer still owns it until the future terminal intent
    /// queue returns
    /// <see cref="TerminalIntentEnqueueStatus.Accepted"/>, at which point the
    /// logical ownership of the intent and its entry moves to the queue and
    /// coordinator. Any non-<c>Accepted</c> result (including
    /// <c>Backpressured</c>) leaves the entry producer-owned. A drop intent
    /// carries no private buffer.
    /// </para>
    /// <para>
    /// This type does not implement <see cref="IDisposable"/> and never
    /// disposes the entry. It never modifies, rolls back, registers, or
    /// transitions the entry or the draft registry, and never generates a trace.
    /// It holds no copied PNG array, manifest, logger, recorder, queue,
    /// registry, render target lease, or readback result.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftTerminalIntent
    {
        private readonly CaptureFrameRequest _request;
        private readonly CaptureFramePngStagingEntry _stagingEntry;
        private readonly CaptureFrameDropReason _dropReason;

        private CaptureFrameDraftTerminalIntent(
            CaptureFrameRequest request,
            CaptureFramePngStagingEntry stagingEntry,
            CaptureFrameDropReason dropReason)
        {
            _request = request;
            _stagingEntry = stagingEntry;
            _dropReason = dropReason;
        }

        public CaptureFrameRequest Request => _request;

        public CaptureFramePngStagingEntry StagingEntry => _stagingEntry;

        public CaptureFrameDropReason DropReason => _dropReason;

        public bool IsStage => _stagingEntry != null;

        public bool IsDrop => _stagingEntry == null;

        public bool HasPrivateBuffer => _stagingEntry != null;

        public int PrivateBufferByteCount => _stagingEntry != null ? _stagingEntry.ByteCount : 0;

        /// <summary>
        /// Creates a stage intent from a validated request and a caller-owned
        /// staging entry. The entry is referenced, never owned or disposed.
        /// </summary>
        internal static CaptureFrameDraftTerminalIntent CreateStage(
            in CaptureFrameRequest request,
            CaptureFramePngStagingEntry stagingEntry)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (request.TraceContext.TestRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.TestRunId, "Test run ID must be greater than zero.");
            }

            if (request.TraceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.CaptureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (stagingEntry == null)
            {
                throw new ArgumentNullException(nameof(stagingEntry));
            }

            if (!stagingEntry.IsCreated)
            {
                throw new ObjectDisposedException(stagingEntry.GetType().Name);
            }

            if (stagingEntry.TestRunId != request.TraceContext.TestRunId)
            {
                throw new ArgumentException("Staging entry test run ID must match the request.", nameof(stagingEntry));
            }

            if (stagingEntry.CaptureFrameId != request.TraceContext.CaptureFrameId)
            {
                throw new ArgumentException("Staging entry capture frame ID must match the request.", nameof(stagingEntry));
            }

            return new CaptureFrameDraftTerminalIntent(request, stagingEntry, CaptureFrameDropReason.None);
        }

        /// <summary>
        /// Creates a drop intent from a validated request and one of the three
        /// normal draft drop reasons. The intent carries no staging entry and no
        /// private buffer.
        /// </summary>
        internal static CaptureFrameDraftTerminalIntent CreateDrop(
            in CaptureFrameRequest request,
            CaptureFrameDropReason dropReason)
        {
            if (!request.IsValid)
            {
                throw new ArgumentException("Request must be valid.", nameof(request));
            }

            if (request.TraceContext.TestRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.TestRunId, "Test run ID must be greater than zero.");
            }

            if (request.TraceContext.CaptureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(request), request.TraceContext.CaptureFrameId, "Capture frame ID must be greater than zero.");
            }

            if (dropReason != CaptureFrameDropReason.PngEncodeFailed
                && dropReason != CaptureFrameDropReason.PngStagingStoreFull
                && dropReason != CaptureFrameDropReason.CaptureCancelled)
            {
                throw new ArgumentOutOfRangeException(nameof(dropReason), dropReason, "Drop reason must be PngEncodeFailed, PngStagingStoreFull, or CaptureCancelled.");
            }

            return new CaptureFrameDraftTerminalIntent(request, null, dropReason);
        }

        /// <summary>
        /// Delegates to <see cref="CaptureFrameRequest.IdenticalTo"/> so callers
        /// never re-implement partial request comparison.
        /// </summary>
        internal bool HasIdenticalRequest(in CaptureFrameRequest request)
        {
            return _request.IdenticalTo(request);
        }
    }
}
