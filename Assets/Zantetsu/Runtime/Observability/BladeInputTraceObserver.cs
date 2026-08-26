using System;
using Zantetsu.Core.Input;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Converts <see cref="BladeInputProcessingResult"/> state transitions and
    /// gate decisions into integer trace events for a <see cref="TraceLogger"/>.
    /// Main thread only.
    /// </summary>
    public sealed class BladeInputTraceObserver
    {
        private readonly TraceLogger _logger;

        // Gate observation state for duplicate suppression.
        private bool _hasObservedGate;
        private bool _lastGateAccepted;
        private BladeEdgeGateReason _lastGateReason;

        public BladeInputTraceObserver(TraceLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _hasObservedGate = false;
            _lastGateAccepted = false;
            _lastGateReason = BladeEdgeGateReason.None;
        }

        /// <summary>
        /// Records tracking transitions and gate decisions from a processing
        /// result. Returns the number of events enqueued by this call. Does not
        /// drain the logger.
        /// </summary>
        public int Record(
            in BladeInputTraceContext context,
            in BladePoseSample sample,
            in BladeInputProcessingResult result)
        {
            int enqueued = 0;

            if (result.TrackingTransition == BladeTrackingTransition.Lost)
            {
                EnqueueTrackingLost(context, sample, result);
                EnqueueSamplesReset(context, sample, result);
                enqueued += 2;
                ResetGateObservation();
            }
            else if (result.TrackingTransition == BladeTrackingTransition.Restored)
            {
                EnqueueTrackingRestored(context, sample, result);
                enqueued += 1;
            }

            if (result.HasGateDecision)
            {
                enqueued += RecordGateDecision(context, sample, result);
            }
            else
            {
                ResetGateObservation();
            }

            return enqueued;
        }

        /// <summary>
        /// Resets only the gate duplicate-suppression state. The logger queue
        /// and history are untouched and no trace event is generated.
        /// </summary>
        public void Reset()
        {
            ResetGateObservation();
        }

        private int RecordGateDecision(in BladeInputTraceContext context, in BladePoseSample sample, in BladeInputProcessingResult result)
        {
            BladeEdgeGateReason reason = result.GateDecision.Reason;

            if (result.IsGateAccepted)
            {
                if (!_hasObservedGate || !_lastGateAccepted)
                {
                    EnqueueEdgeGateEntered(context, sample, result);
                    _hasObservedGate = true;
                    _lastGateAccepted = true;
                    _lastGateReason = BladeEdgeGateReason.None;
                    return 1;
                }

                return 0;
            }

            if (!_hasObservedGate || _lastGateAccepted || _lastGateReason != reason)
            {
                bool fromAccepted = _hasObservedGate && _lastGateAccepted;
                EnqueueEdgeGateRejected(context, sample, result, fromAccepted);
                _hasObservedGate = true;
                _lastGateAccepted = false;
                _lastGateReason = reason;
                return 1;
            }

            return 0;
        }

        private void ResetGateObservation()
        {
            _hasObservedGate = false;
            _lastGateAccepted = false;
            _lastGateReason = BladeEdgeGateReason.None;
        }

        private TraceEvent CreateBase(in BladeInputTraceContext context, in BladePoseSample sample, TraceEventType eventType)
        {
            TraceEvent traceEvent = default;
            traceEvent.Timestamp = context.Timestamp;
            traceEvent.FrameId = sample.FrameId;
            traceEvent.FixedStepId = context.FixedStepId;
            traceEvent.ThreadId = context.ThreadId;
            traceEvent.OpenXRFrameId = context.OpenXRFrameId;
            traceEvent.TestRunId = context.TestRunId;
            traceEvent.EventType = eventType;
            return traceEvent;
        }

        private void EnqueueTrackingLost(in BladeInputTraceContext context, in BladePoseSample sample, in BladeInputProcessingResult result)
        {
            TraceEvent traceEvent = CreateBase(context, sample, TraceEventType.BladeTrackingLost);
            traceEvent.FromState = 1;
            traceEvent.ToState = 0;
            traceEvent.Value0 = (int)sample.TrackingState;
            traceEvent.Value1 = sample.TimestampSeconds;
            _logger.Enqueue(traceEvent);
        }

        private void EnqueueSamplesReset(in BladeInputTraceContext context, in BladePoseSample sample, in BladeInputProcessingResult result)
        {
            TraceEvent traceEvent = CreateBase(context, sample, TraceEventType.BladeSamplesReset);
            traceEvent.FromState = 1;
            traceEvent.ToState = 0;
            traceEvent.Value0 = (int)result.Status;
            traceEvent.Value1 = sample.TimestampSeconds;
            _logger.Enqueue(traceEvent);
        }

        private void EnqueueTrackingRestored(in BladeInputTraceContext context, in BladePoseSample sample, in BladeInputProcessingResult result)
        {
            TraceEvent traceEvent = CreateBase(context, sample, TraceEventType.BladeTrackingRestored);
            traceEvent.FromState = 0;
            traceEvent.ToState = 1;
            traceEvent.Value0 = (int)sample.TrackingState;
            traceEvent.Value1 = sample.TimestampSeconds;
            _logger.Enqueue(traceEvent);
        }

        private void EnqueueEdgeGateEntered(in BladeInputTraceContext context, in BladePoseSample sample, in BladeInputProcessingResult result)
        {
            TraceEvent traceEvent = CreateBase(context, sample, TraceEventType.EdgeGateEntered);
            traceEvent.FromState = 0;
            traceEvent.ToState = 1;
            traceEvent.Value0 = result.Motion.EdgeLeadScore;
            traceEvent.Value1 = 0.0;
            _logger.Enqueue(traceEvent);
        }

        private void EnqueueEdgeGateRejected(in BladeInputTraceContext context, in BladePoseSample sample, in BladeInputProcessingResult result, bool fromAccepted)
        {
            TraceEvent traceEvent = CreateBase(context, sample, TraceEventType.EdgeGateRejected);
            traceEvent.FromState = fromAccepted ? 1 : 0;
            traceEvent.ToState = 0;
            traceEvent.Value0 = result.Motion.EdgeLeadScore;
            traceEvent.Value1 = (int)result.GateDecision.Reason;
            _logger.Enqueue(traceEvent);
        }
    }
}
