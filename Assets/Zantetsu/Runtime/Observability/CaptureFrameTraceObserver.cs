using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Records capture-frame lifecycle trace events into a
    /// <see cref="TraceLogger"/>. Does not own, drain, or dispose the logger.
    /// </summary>
    public sealed class CaptureFrameTraceObserver
    {
        private readonly TraceLogger _logger;

        public CaptureFrameTraceObserver(TraceLogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            _logger = logger;
        }

        public void RecordQueued(in CaptureFrameTraceContext context)
        {
            TraceEvent e = BuildEvent(context, TraceEventType.CaptureFrameQueued);
            _logger.Enqueue(e);
        }

        public void RecordEncoded(in CaptureFrameTraceContext context, double encodeDurationMilliseconds, int encodedByteCount)
        {
            if (double.IsNaN(encodeDurationMilliseconds) || double.IsInfinity(encodeDurationMilliseconds) || encodeDurationMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(encodeDurationMilliseconds), encodeDurationMilliseconds, "Encode duration must be finite and non-negative.");
            }

            if (encodedByteCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(encodedByteCount), encodedByteCount, "Encoded byte count must not be negative.");
            }

            TraceEvent e = BuildEvent(context, TraceEventType.CaptureFrameEncoded);
            e.Value0 = encodeDurationMilliseconds;
            e.Value1 = encodedByteCount;
            _logger.Enqueue(e);
        }

        /// <summary>
        /// Format-independent success observation used by capture evidence
        /// backends. The fixed Phase 0 trace event remains wire-compatible.
        /// </summary>
        internal void RecordMediaProcessed(
            in CaptureFrameTraceContext context,
            double processingDurationMilliseconds,
            long artifactByteCount)
        {
            if (artifactByteCount <= 0 || artifactByteCount > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(artifactByteCount));
            }

            RecordEncoded(context, processingDurationMilliseconds, (int)artifactByteCount);
        }

        public void RecordDropped(in CaptureFrameTraceContext context, CaptureFrameDropReason reason)
        {
            if (reason != CaptureFrameDropReason.RequestQueueFull
                && reason != CaptureFrameDropReason.ReadbackFailed
                && reason != CaptureFrameDropReason.EncodedPngQueueFull
                && reason != CaptureFrameDropReason.FrameRecordRegistryFull)
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Reason must be a defined non-None value.");
            }

            TraceEvent e = BuildEvent(context, TraceEventType.CaptureFrameDropped);
            e.Value1 = (int)reason;
            _logger.Enqueue(e);
        }

        /// <summary>
        /// Records a capture frame admission rejection as a dedicated
        /// <c>CaptureFrameAdmissionRejected</c> trace event. The admission was
        /// refused before any positive capture frame ID was issued, so the
        /// context's capture frame ID must already be zero.
        /// </summary>
        internal void RecordAdmissionRejected(
            in CaptureFrameTraceContext context,
            CaptureFrameAdmissionRejectKind rejectKind)
        {
            if (context.CaptureFrameId != 0)
            {
                throw new ArgumentException("Capture frame ID must be zero for an admission rejection.", nameof(context));
            }

            if (context.TestRunId <= 0)
            {
                throw new ArgumentException("Test run ID must be greater than zero.", nameof(context));
            }

            if (rejectKind != CaptureFrameAdmissionRejectKind.PendingLimit
                && rejectKind != CaptureFrameAdmissionRejectKind.RunEntryLimit)
            {
                throw new ArgumentOutOfRangeException(nameof(rejectKind), rejectKind, "Reject kind must be PendingLimit or RunEntryLimit.");
            }

            TraceEvent e = BuildEvent(context, TraceEventType.CaptureFrameAdmissionRejected);
            e.Value0 = (int)rejectKind;
            e.Value1 = (int)CaptureFrameDropReason.FrameDraftRegistryFull;
            _logger.Enqueue(e);
        }

        /// <summary>
        /// Consumes a draft's one-time drop trace from the registry and enqueues
        /// a <see cref="TraceEventType.CaptureFrameDropped"/> event exactly once.
        /// Returns <c>false</c> without touching the logger when there is no
        /// consumable drop trace.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The registry's emission state is advanced to <c>Attempted</c> before
        /// the logger is touched, so a logger enqueue exception (disposal or
        /// capture-run seal conflict) never rolls back the dropped status, the
        /// freed slot, or the emission state; the caller must not retry the same
        /// frame through this method.
        /// </para>
        /// </remarks>
        internal bool RecordDraftDropped(
            CaptureFrameDraftRegistry registry,
            long captureFrameId)
        {
            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            if (!registry.TryConsumeDropTrace(captureFrameId, out CaptureFrameDraftDropTracePayload payload))
            {
                return false;
            }

            TraceEvent e = default;
            e.Timestamp = payload.TraceContext.Timestamp;
            e.FrameId = payload.TraceContext.UnityFrameId;
            e.FixedStepId = payload.TraceContext.FixedStepId;
            e.ThreadId = payload.TraceContext.ThreadId;
            e.CaptureFrameId = payload.TraceContext.CaptureFrameId;
            e.OpenXRFrameId = payload.TraceContext.OpenXRFrameId;
            e.TestRunId = payload.TraceContext.TestRunId;
            e.SlashId = payload.TraceContext.SlashId;
            e.FrontEdgeId = payload.TraceContext.FrontEdgeId;
            e.ObjectId = payload.TraceContext.ObjectId;
            e.ObjectGeneration = payload.TraceContext.ObjectGeneration;
            e.TaskId = payload.TraceContext.TaskId;

            // The draft trace context does not carry these; they stay zero.
            e.SlashGeneration = 0;
            e.MobId = 0;
            e.PlanGeneration = 0;

            e.EventType = TraceEventType.CaptureFrameDropped;
            e.TaskType = TraceTaskType.None;
            e.FromState = (int)CaptureFrameDraftStatus.Pending;
            e.ToState = (int)CaptureFrameDraftStatus.Dropped;
            e.Reason = TraceReason.None;
            e.Value0 = 0.0;
            e.Value1 = (int)payload.Reason;

            _logger.Enqueue(e);
            return true;
        }

        public void RecordRingFrozen(in CaptureFrameTraceContext context)
        {
            TraceEvent e = BuildEvent(context, TraceEventType.CaptureRingFrozen);
            _logger.Enqueue(e);
        }

        private static TraceEvent BuildEvent(in CaptureFrameTraceContext context, TraceEventType eventType)
        {
            TraceEvent e = default;
            e.Timestamp = context.Timestamp;
            e.FrameId = context.UnityFrameId;
            e.FixedStepId = context.FixedStepId;
            e.ThreadId = context.ThreadId;
            e.CaptureFrameId = context.CaptureFrameId;
            e.OpenXRFrameId = context.OpenXRFrameId;
            e.TestRunId = context.TestRunId;
            e.SlashId = context.SlashId;
            e.FrontEdgeId = context.FrontEdgeId;
            e.ObjectId = context.ObjectId;
            e.ObjectGeneration = context.ObjectGeneration;
            e.TaskId = context.TaskId;
            e.EventType = eventType;
            e.TaskType = TraceTaskType.None;
            e.Reason = TraceReason.None;
            e.Value0 = 0;
            e.Value1 = 0;
            return e;
        }
    }
}
