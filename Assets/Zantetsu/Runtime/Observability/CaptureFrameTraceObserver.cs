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

        public void RecordDropped(in CaptureFrameTraceContext context, CaptureFrameDropReason reason)
        {
            if (reason != CaptureFrameDropReason.RequestQueueFull && reason != CaptureFrameDropReason.ReadbackFailed)
            {
                throw new ArgumentOutOfRangeException(nameof(reason), reason, "Reason must be a defined non-None value.");
            }

            TraceEvent e = BuildEvent(context, TraceEventType.CaptureFrameDropped);
            e.Value1 = (int)reason;
            _logger.Enqueue(e);
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
