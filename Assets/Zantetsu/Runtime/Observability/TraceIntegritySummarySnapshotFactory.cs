using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Builds an export snapshot from a frozen recorder capture by copying the
    /// existing frozen snapshot unchanged and appending exactly one
    /// <see cref="TraceEventType.TraceIntegritySummary"/> event at the tail.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This factory is stateless and owns, modifies, or disposes none of its
    /// inputs: the recorder, its logger, the seal receipt, the terminal buffer,
    /// or the existing frozen snapshot. It never enqueues, drains, re-seals, or
    /// records anything back into the recorder or logger, and it samples no
    /// clock, frame, or thread value of its own.
    /// </para>
    /// <para>
    /// On any validation failure the recorder, logger, receipt, terminal buffer,
    /// and original snapshot are left unchanged. Repeated calls over the same
    /// frozen inputs produce an independent snapshot with identical content.
    /// </para>
    /// </remarks>
    internal static class TraceIntegritySummarySnapshotFactory
    {
        internal static TraceCaptureSnapshot Create(
            TraceFlightRecorder recorder,
            TraceRunContext runContext,
            TraceRunSealReceipt sealReceipt,
            FreezeTerminalTraceBuffer terminalBuffer,
            uint priorBundlePublishFailureCount)
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            if (runContext == null)
            {
                throw new ArgumentNullException(nameof(runContext));
            }

            if (sealReceipt == null)
            {
                throw new ArgumentNullException(nameof(sealReceipt));
            }

            if (terminalBuffer == null)
            {
                throw new ArgumentNullException(nameof(terminalBuffer));
            }

            TraceLogger logger = recorder.Logger;

            if (!logger.IsOnConstructingThread)
            {
                throw new InvalidOperationException("The export snapshot must be built on the thread that constructed the capture logger.");
            }

            if (!logger.IsCaptureRun)
            {
                throw new InvalidOperationException("The recorder's logger must be a capture-run logger.");
            }

            if (recorder.State != TraceFlightRecorderState.Frozen)
            {
                throw new InvalidOperationException("The recorder must be Frozen to build an export snapshot.");
            }

            if (logger.SealState != TraceRunSealState.Sealed)
            {
                throw new InvalidOperationException("The capture run must be sealed to build an export snapshot.");
            }

            if (!ReferenceEquals(sealReceipt.IssuedBy, logger))
            {
                throw new ArgumentException("The seal receipt was not issued by the recorder's logger.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(sealReceipt.IssuedTo, recorder))
            {
                throw new ArgumentException("The seal receipt was not issued to this recorder.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(sealReceipt, logger.IssuedSealReceipt))
            {
                throw new ArgumentException("The seal receipt is not the exact receipt issued by the logger.", nameof(sealReceipt));
            }

            long loggerRunId = logger.TestRunId;
            if (sealReceipt.TestRunId != loggerRunId)
            {
                throw new ArgumentException("The seal receipt's test run ID must match the logger's bound run.", nameof(sealReceipt));
            }

            if (runContext.TestRunId != loggerRunId)
            {
                throw new ArgumentException("The run context's test run ID must match the logger's bound run.", nameof(runContext));
            }

            if (terminalBuffer.TestRunId != loggerRunId)
            {
                throw new ArgumentException("The terminal buffer's test run ID must match the logger's bound run.", nameof(terminalBuffer));
            }

            if (sealReceipt.SealedTraceEnqueueFailureCount != logger.SealedTraceEnqueueFailureCount)
            {
                throw new ArgumentException("The seal receipt's sealed failure count must match the logger.", nameof(sealReceipt));
            }

            if (sealReceipt.TraceCaptureOverflowCount != recorder.TraceCaptureOverflowCount)
            {
                throw new ArgumentException("The seal receipt's overflow count must match the recorder.", nameof(sealReceipt));
            }

            TraceCaptureSnapshot frozen = recorder.CreateFrozenSnapshot();

            // Count invariant of the frozen snapshot.
            if ((long)frozen.TriggerHistoryCount + (long)frozen.CapturedPostRollCount != (long)frozen.EventCount)
            {
                throw new InvalidOperationException("The frozen snapshot counters are inconsistent with its event count.");
            }

            // The snapshot tail must equal the terminal buffer in order, field by field.
            int tailStart = frozen.EventCount - terminalBuffer.Count;
            if (tailStart < 0)
            {
                throw new ArgumentException("The terminal buffer is larger than the frozen snapshot.", nameof(terminalBuffer));
            }

            for (int i = 0; i < terminalBuffer.Count; i++)
            {
                if (!IdenticalEvents(frozen.GetEvent(tailStart + i), terminalBuffer.GetEvent(i)))
                {
                    throw new ArgumentException("The frozen snapshot tail does not match the terminal buffer.", nameof(terminalBuffer));
                }
            }

            int eventCount = checked(frozen.EventCount + 1);
            int postRollCount = checked(frozen.CapturedPostRollCount + 1);

            TraceEvent[] events = new TraceEvent[eventCount];
            frozen.CopyEventsTo(events, 0);

            TraceEvent lastEvent = frozen.GetEvent(frozen.EventCount - 1);
            events[eventCount - 1] = BuildSummaryEvent(
                lastEvent,
                terminalBuffer.Checkpoint,
                loggerRunId,
                sealReceipt.SealedTraceEnqueueFailureCount,
                recorder.TraceCaptureOverflowCount,
                priorBundlePublishFailureCount);

            return new TraceCaptureSnapshot(
                events,
                frozen.TriggerHistoryCount,
                postRollCount,
                frozen.WasHistoryOverwrittenAtTrigger);
        }

        private static bool IdenticalEvents(TraceEvent left, TraceEvent right)
        {
            return left.Timestamp == right.Timestamp
                && left.FrameId == right.FrameId
                && left.FixedStepId == right.FixedStepId
                && left.ThreadId == right.ThreadId
                && left.SlashId == right.SlashId
                && left.SlashGeneration == right.SlashGeneration
                && left.FrontEdgeId == right.FrontEdgeId
                && left.ObjectId == right.ObjectId
                && left.ObjectGeneration == right.ObjectGeneration
                && left.MobId == right.MobId
                && left.PlanGeneration == right.PlanGeneration
                && left.TaskId == right.TaskId
                && left.CaptureFrameId == right.CaptureFrameId
                && left.OpenXRFrameId == right.OpenXRFrameId
                && left.TestRunId == right.TestRunId
                && left.EventType == right.EventType
                && left.TaskType == right.TaskType
                && left.FromState == right.FromState
                && left.ToState == right.ToState
                && left.Reason == right.Reason
                && BitConverter.DoubleToInt64Bits(left.Value0) == BitConverter.DoubleToInt64Bits(right.Value0)
                && BitConverter.DoubleToInt64Bits(left.Value1) == BitConverter.DoubleToInt64Bits(right.Value1);
        }

        private static TraceEvent BuildSummaryEvent(
            TraceEvent lastEvent,
            in FreezeTerminalCheckpoint checkpoint,
            long testRunId,
            int sealedFailureCount,
            int overflowCount,
            uint priorBundlePublishFailureCount)
        {
            TraceEvent e = default;

            e.Timestamp = lastEvent.Timestamp;
            e.FrameId = lastEvent.FrameId;
            e.FixedStepId = lastEvent.FixedStepId;
            e.ThreadId = checkpoint.ThreadId;

            e.SlashId = 0;
            e.SlashGeneration = 0;
            e.FrontEdgeId = 0;
            e.ObjectId = 0;
            e.ObjectGeneration = 0;
            e.MobId = 0;
            e.PlanGeneration = 0;
            e.TaskId = 0;
            e.CaptureFrameId = 0;
            e.OpenXRFrameId = 0;
            e.TestRunId = testRunId;

            e.EventType = TraceEventType.TraceIntegritySummary;
            e.TaskType = TraceTaskType.None;

            bool incomplete = sealedFailureCount != 0 || overflowCount != 0;
            e.FromState = incomplete ? (int)TraceIntegrityState.Incomplete : (int)TraceIntegrityState.Complete;
            e.ToState = overflowCount;

            if (sealedFailureCount != 0)
            {
                e.Reason = TraceReason.TraceWriteFailureObserved;
            }
            else if (overflowCount != 0)
            {
                e.Reason = TraceReason.TraceCaptureOverflowObserved;
            }
            else
            {
                e.Reason = TraceReason.None;
            }

            e.Value0 = (double)sealedFailureCount;
            e.Value1 = (double)priorBundlePublishFailureCount;

            return e;
        }
    }
}
