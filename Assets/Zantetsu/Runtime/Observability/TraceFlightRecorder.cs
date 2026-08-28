using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Protects the recent trace history of a <see cref="TraceLogger"/> when an
    /// anomaly is detected, without owning or disposing the logger.
    /// </summary>
    /// <remarks>
    /// The recorder does not own the logger and never disposes it. Capture
    /// storage is allocated once in the constructor and reused across trigger
    /// and reset cycles; no per-drain allocation, LINQ, or string formatting is
    /// performed. Events enqueued in parallel have no guaranteed order, but the
    /// capture preserves their drain order.
    /// </remarks>
    public sealed class TraceFlightRecorder
    {
        private readonly TraceLogger _logger;
        private readonly int _postRollCapacity;
        private readonly int _freezeTerminalTraceReserve;
        private readonly TraceRingBuffer _capture;

        private TraceFlightRecorderState _state = TraceFlightRecorderState.Armed;
        private int _triggerHistoryCount;
        private int _capturedPostRollCount;
        private bool _wasHistoryOverwrittenAtTrigger;
        private int _traceCaptureOverflowCount;

        public TraceFlightRecorder(TraceLogger logger, int postRollCapacity)
            : this(logger, postRollCapacity, 0)
        {
        }

        internal TraceFlightRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (postRollCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(postRollCapacity), postRollCapacity, "Post-roll capacity must not be negative.");
            }

            if (freezeTerminalTraceReserve < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(freezeTerminalTraceReserve), freezeTerminalTraceReserve, "Freeze terminal trace reserve must not be negative.");
            }

            if (freezeTerminalTraceReserve > postRollCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(freezeTerminalTraceReserve), freezeTerminalTraceReserve, "Freeze terminal trace reserve must not exceed the post-roll capacity.");
            }

            int historyCapacity = logger.HistoryCapacity;
            long total = (long)historyCapacity + (long)postRollCapacity;
            if (total > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(postRollCapacity), postRollCapacity, "Combined capture capacity exceeds the maximum supported size.");
            }

            _logger = logger;
            _postRollCapacity = postRollCapacity;
            _freezeTerminalTraceReserve = freezeTerminalTraceReserve;
            _capture = new TraceRingBuffer((int)total);
        }

        public TraceFlightRecorderState State => _state;

        /// <summary>The logger this recorder drains from.</summary>
        internal TraceLogger Logger => _logger;

        public int PostRollCapacity => _postRollCapacity;

        /// <summary>Number of post-roll slots reserved for the freeze terminal.</summary>
        public int FreezeTerminalTraceReserve => _freezeTerminalTraceReserve;

        /// <summary>Number of post-roll slots available to normal duplication.</summary>
        public int NormalPostRollCapacity => _postRollCapacity - _freezeTerminalTraceReserve;

        /// <summary>Number of pre-trigger history events captured at trigger time.</summary>
        public int TriggerHistoryCount => _triggerHistoryCount;

        /// <summary>
        /// Number of events recorded into the capture after trigger, including
        /// the freeze-terminal direct append. If only the count of normal
        /// post-roll duplications is needed, it must be separated by verifying
        /// the terminal tail structure.
        /// </summary>
        public int CapturedPostRollCount => _capturedPostRollCount;

        /// <summary>Total number of captured events (pre-trigger + post-roll).</summary>
        public int CapturedCount => _capture.Count;

        /// <summary>Whether the logger history had already overwritten events at trigger time.</summary>
        public bool WasHistoryOverwrittenAtTrigger => _wasHistoryOverwrittenAtTrigger;

        /// <summary>
        /// Number of events drained while CapturingPostRoll that could not be
        /// duplicated into the normal capture region. Non-negative and
        /// saturating at <see cref="int.MaxValue"/>.
        /// </summary>
        public int TraceCaptureOverflowCount => _traceCaptureOverflowCount;

        /// <summary>
        /// Drains the logger according to the current state. In Armed and Frozen
        /// states this drains normally; while CapturingPostRoll it also
        /// duplicates up to the remaining post-roll slots into the capture.
        /// </summary>
        public int Drain()
        {
            switch (_state)
            {
                case TraceFlightRecorderState.CapturingPostRoll:
                    return DrainCapturingPostRoll();

                case TraceFlightRecorderState.AwaitingFreezeTerminal:
                    throw new InvalidOperationException("The recorder is awaiting the freeze terminal append and cannot drain.");

                default:
                    return _logger.Drain();
            }
        }

        /// <summary>
        /// Snapshots the current logger history into the capture and advances
        /// the state. Returns false, leaving the capture unchanged, when the
        /// recorder is not Armed.
        /// </summary>
        public bool TryTrigger()
        {
            if (_state != TraceFlightRecorderState.Armed)
            {
                return false;
            }

            // Drain queued events first so an event enqueued immediately before
            // the trigger is included in the pre-trigger history.
            _logger.Drain();

            int historyCount = _logger.HistoryCount;
            _wasHistoryOverwrittenAtTrigger = _logger.OverwrittenCount > 0;

            for (int i = 0; i < historyCount; i++)
            {
                _capture.Write(_logger.GetHistoryEvent(i));
            }

            _triggerHistoryCount = historyCount;
            _capturedPostRollCount = 0;

            _state = _postRollCapacity > 0
                ? TraceFlightRecorderState.CapturingPostRoll
                : TraceFlightRecorderState.Frozen;

            return true;
        }

        /// <summary>
        /// Freezes the capture immediately, even if post-roll slots remain.
        /// Returns false when not CapturingPostRoll, or when a freeze terminal
        /// reserve is configured (which must complete through the terminal API
        /// instead of this legacy freeze).
        /// </summary>
        public bool Freeze()
        {
            if (_freezeTerminalTraceReserve > 0 || _state != TraceFlightRecorderState.CapturingPostRoll)
            {
                return false;
            }

            _state = TraceFlightRecorderState.Frozen;
            return true;
        }

        /// <summary>
        /// Begins the freeze terminal append by validating the completed seal
        /// and transitioning this recorder into
        /// <see cref="TraceFlightRecorderState.AwaitingFreezeTerminal"/>. Must
        /// be called from the thread that constructed the capture logger.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every precondition is verified before the state is changed, so any
        /// failure leaves the recorder and logger completely unchanged. The
        /// capture, its counters, the capacities, and the logger's history and
        /// counters are never modified by this method.
        /// </para>
        /// <para>
        /// <paramref name="captureAdmissionStopped"/> is internal evidence that
        /// the coordinator has stopped admitting new captures; this unit does
        /// not connect to the pipeline.
        /// </para>
        /// </remarks>
        internal void BeginFreezeTerminalAppend(
            TraceRunSealReceipt sealReceipt,
            bool captureAdmissionStopped)
        {
            // 1. A receipt is required.
            if (sealReceipt == null)
            {
                throw new ArgumentNullException(nameof(sealReceipt));
            }

            // 2. Main-thread only.
            if (!_logger.IsOnConstructingThread)
            {
                throw new InvalidOperationException("The freeze terminal append must begin on the thread that constructed the capture logger.");
            }

            // 3. The recorder must be CapturingPostRoll.
            if (_state != TraceFlightRecorderState.CapturingPostRoll)
            {
                throw new InvalidOperationException("The recorder must be CapturingPostRoll to begin the freeze terminal append.");
            }

            // 4. A freeze terminal reserve is required.
            if (_freezeTerminalTraceReserve <= 0)
            {
                throw new InvalidOperationException("A freeze terminal reserve is required to begin the freeze terminal append.");
            }

            // 5. Capture admission must be stopped.
            if (!captureAdmissionStopped)
            {
                throw new ArgumentException("Capture admission must be stopped before beginning the freeze terminal append.", nameof(captureAdmissionStopped));
            }

            // 6. The receipt must have been issued by this recorder's logger,
            // issued to this recorder, and be the exact instance the logger
            // issued.
            if (!ReferenceEquals(sealReceipt.IssuedBy, _logger))
            {
                throw new ArgumentException("The seal receipt was not issued by this recorder's logger.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(sealReceipt.IssuedTo, this))
            {
                throw new ArgumentException("The seal receipt was not issued to this recorder.", nameof(sealReceipt));
            }

            if (!ReferenceEquals(sealReceipt, _logger.IssuedSealReceipt))
            {
                throw new ArgumentException("The seal receipt is not the exact receipt issued by the logger.", nameof(sealReceipt));
            }

            // 7. The receipt's run ID must be positive.
            if (sealReceipt.TestRunId <= 0)
            {
                throw new ArgumentException("The seal receipt has an invalid test run ID.", nameof(sealReceipt));
            }

            // 8. The receipt's run ID must match the logger's bound run.
            if (sealReceipt.TestRunId != _logger.TestRunId)
            {
                throw new ArgumentException("The seal receipt's test run ID does not match the logger's bound run.", nameof(sealReceipt));
            }

            // 9. The logger must be sealed.
            if (_logger.SealState != TraceRunSealState.Sealed)
            {
                throw new InvalidOperationException("The capture run is not sealed.");
            }

            // 10. The logger's normal queue must be empty.
            if (!_logger.IsQueueEmpty)
            {
                throw new InvalidOperationException("The capture run queue is not empty.");
            }

            // 11. The receipt's captured post-roll count must match the recorder.
            if (sealReceipt.CapturedPostRollCount != _capturedPostRollCount)
            {
                throw new ArgumentException("The seal receipt's captured post-roll count does not match the recorder.", nameof(sealReceipt));
            }

            // 12. The receipt's overflow count must match the recorder.
            if (sealReceipt.TraceCaptureOverflowCount != _traceCaptureOverflowCount)
            {
                throw new ArgumentException("The seal receipt's overflow count does not match the recorder.", nameof(sealReceipt));
            }

            // 13. The receipt's sealed failure count must match the logger.
            if (sealReceipt.SealedTraceEnqueueFailureCount != _logger.SealedTraceEnqueueFailureCount)
            {
                throw new ArgumentException("The seal receipt's sealed failure count does not match the logger.", nameof(sealReceipt));
            }

            // 14. The recorder's count invariant must hold.
            if ((long)_triggerHistoryCount + (long)_capturedPostRollCount != (long)_capture.Count)
            {
                throw new InvalidOperationException("Recorder capture counters are inconsistent with the captured event count.");
            }

            // 15. The captured post-roll count must fit the normal region.
            if (_capturedPostRollCount > NormalPostRollCapacity)
            {
                throw new InvalidOperationException("The captured post-roll count exceeds the normal post-roll capacity.");
            }

            // Only after every check passes, transition the state. Nothing else
            // changes.
            _state = TraceFlightRecorderState.AwaitingFreezeTerminal;
        }

        /// <summary>
        /// Appends a validated freeze terminal trace buffer to the capture and
        /// completes the <c>AwaitingFreezeTerminal → Frozen</c> transition in one
        /// all-or-none step. Main-thread only and not thread-safe; this method
        /// owns and disposes none of the buffer, set, checkpoint, or logger.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every precondition and every event is verified before the capture is
        /// modified, so any failure leaves the recorder state, the capture, all
        /// counters, and the logger completely unchanged. After a successful
        /// append the recorder is <see cref="TraceFlightRecorderState.Frozen"/>
        /// and a later call is rejected without double-appending.
        /// </para>
        /// </remarks>
        internal void AppendFreezeTerminalEvents(FreezeTerminalTraceBuffer terminalBuffer)
        {
            if (terminalBuffer == null)
            {
                throw new ArgumentNullException(nameof(terminalBuffer));
            }

            if (!_logger.IsOnConstructingThread)
            {
                throw new InvalidOperationException("The freeze terminal append must run on the thread that constructed the capture logger.");
            }

            if (_state != TraceFlightRecorderState.AwaitingFreezeTerminal)
            {
                throw new InvalidOperationException("The recorder must be AwaitingFreezeTerminal to append the freeze terminal events.");
            }

            if (_freezeTerminalTraceReserve <= 0)
            {
                throw new InvalidOperationException("A freeze terminal reserve is required to append the freeze terminal events.");
            }

            if (!_logger.IsCaptureRun)
            {
                throw new InvalidOperationException("The capture logger must be bound to a positive capture run.");
            }

            if (_logger.SealState != TraceRunSealState.Sealed)
            {
                throw new InvalidOperationException("The capture run is not sealed.");
            }

            if (!_logger.IsQueueEmpty)
            {
                throw new InvalidOperationException("The capture run queue is not empty.");
            }

            if (terminalBuffer.TestRunId != _logger.TestRunId)
            {
                throw new ArgumentException("The terminal buffer's test run ID does not match the logger's bound run.", nameof(terminalBuffer));
            }

            FreezeTerminalCheckpoint checkpoint = terminalBuffer.Checkpoint;
            ForcedDropFrameIdSet forcedDropFrameIds = terminalBuffer.ForcedDropFrameIds;

            if (forcedDropFrameIds == null)
            {
                throw new ArgumentException("The terminal buffer's forced-drop set is missing.", nameof(terminalBuffer));
            }

            if (!checkpoint.IsValid)
            {
                throw new ArgumentException("The terminal buffer's checkpoint is invalid.", nameof(terminalBuffer));
            }

            if (checkpoint.TestRunId != terminalBuffer.TestRunId || checkpoint.TestRunId != forcedDropFrameIds.TestRunId)
            {
                throw new ArgumentException("The terminal buffer's checkpoint test run ID does not match the buffer or the set.", nameof(terminalBuffer));
            }

            if (!forcedDropFrameIds.IsValid)
            {
                throw new ArgumentException("The terminal buffer's forced-drop set is invalid.", nameof(terminalBuffer));
            }

            CaptureFrameDraftRegistry registry = forcedDropFrameIds.IssuedBy;
            if (registry == null || !ReferenceEquals(forcedDropFrameIds, registry.IssuedForcedDropFrameIdSet))
            {
                throw new ArgumentException("The terminal buffer's forced-drop set is not the issuing registry's canonical set.", nameof(terminalBuffer));
            }

            if (terminalBuffer.ForcedDropCount != forcedDropFrameIds.Count)
            {
                throw new ArgumentException("The terminal buffer's forced-drop count does not match the set.", nameof(terminalBuffer));
            }

            if (terminalBuffer.Count != checked(terminalBuffer.ForcedDropCount + 1))
            {
                throw new ArgumentException("The terminal buffer's event count does not equal the forced-drop count plus one.", nameof(terminalBuffer));
            }

            if (terminalBuffer.Count > _freezeTerminalTraceReserve)
            {
                throw new ArgumentException("The terminal buffer exceeds the freeze terminal reserve.", nameof(terminalBuffer));
            }

            if ((long)_triggerHistoryCount + (long)_capturedPostRollCount != (long)_capture.Count)
            {
                throw new InvalidOperationException("Recorder capture counters are inconsistent with the captured event count.");
            }

            if (_capturedPostRollCount > NormalPostRollCapacity)
            {
                throw new InvalidOperationException("The captured post-roll count exceeds the normal post-roll capacity.");
            }

            if ((long)_capture.Count + (long)terminalBuffer.Count > (long)_capture.Capacity)
            {
                throw new InvalidOperationException("The terminal buffer exceeds the remaining capture capacity.");
            }

            if ((long)_capturedPostRollCount + (long)terminalBuffer.Count > (long)_postRollCapacity)
            {
                throw new InvalidOperationException("The terminal buffer exceeds the post-roll capacity.");
            }

            for (int i = 0; i < terminalBuffer.ForcedDropCount; i++)
            {
                CaptureFrameDraftTraceContext context = registry.GetForcedDropTraceContext(forcedDropFrameIds, i);
                if (!ForcedDropEventMatches(terminalBuffer.GetEvent(i), context))
                {
                    throw new ArgumentException("A forced-drop event does not match its draft trace context.", nameof(terminalBuffer));
                }
            }

            if (!RingEventMatches(terminalBuffer.GetEvent(terminalBuffer.Count - 1), checkpoint, terminalBuffer.ForcedDropCount))
            {
                throw new ArgumentException("The trailing ring event does not match the checkpoint.", nameof(terminalBuffer));
            }

            // Every check passed: append with no remaining exception point.
            for (int i = 0; i < terminalBuffer.Count; i++)
            {
                _capture.Write(terminalBuffer.GetEvent(i));
            }

            _capturedPostRollCount += terminalBuffer.Count;
            _state = TraceFlightRecorderState.Frozen;
        }

        private static bool ForcedDropEventMatches(TraceEvent e, in CaptureFrameDraftTraceContext context)
        {
            return e.Timestamp == context.Timestamp
                && e.FrameId == context.UnityFrameId
                && e.FixedStepId == context.FixedStepId
                && e.ThreadId == context.ThreadId
                && e.SlashId == context.SlashId
                && e.SlashGeneration == 0
                && e.FrontEdgeId == context.FrontEdgeId
                && e.ObjectId == context.ObjectId
                && e.ObjectGeneration == context.ObjectGeneration
                && e.MobId == 0
                && e.PlanGeneration == 0
                && e.TaskId == context.TaskId
                && e.CaptureFrameId == context.CaptureFrameId
                && e.OpenXRFrameId == context.OpenXRFrameId
                && e.TestRunId == context.TestRunId
                && e.EventType == TraceEventType.CaptureFrameDropped
                && e.TaskType == TraceTaskType.None
                && e.FromState == 0
                && e.ToState == 2
                && e.Reason == TraceReason.None
                && IsPositiveZero(e.Value0)
                && BitConverter.DoubleToInt64Bits(e.Value1) == BitConverter.DoubleToInt64Bits(9.0);
        }

        private static bool RingEventMatches(TraceEvent e, in FreezeTerminalCheckpoint checkpoint, int forcedDropCount)
        {
            return e.Timestamp == checkpoint.Timestamp
                && e.FrameId == checkpoint.FrameId
                && e.FixedStepId == checkpoint.FixedStepId
                && e.ThreadId == checkpoint.ThreadId
                && e.SlashId == 0
                && e.SlashGeneration == 0
                && e.FrontEdgeId == 0
                && e.ObjectId == 0
                && e.ObjectGeneration == 0
                && e.MobId == 0
                && e.PlanGeneration == 0
                && e.TaskId == 0
                && e.CaptureFrameId == 0
                && e.OpenXRFrameId == 0
                && e.TestRunId == checkpoint.TestRunId
                && e.EventType == TraceEventType.CaptureRingFrozen
                && e.TaskType == TraceTaskType.None
                && e.FromState == 3
                && e.ToState == 2
                && e.Reason == TraceReason.None
                && BitConverter.DoubleToInt64Bits(e.Value0) == BitConverter.DoubleToInt64Bits((double)forcedDropCount)
                && IsPositiveZero(e.Value1);
        }

        private static bool IsPositiveZero(double value)
        {
            return BitConverter.DoubleToInt64Bits(value) == 0L;
        }

        /// <summary>
        /// Returns the captured event at the given chronological index, where 0
        /// is the oldest captured event.
        /// </summary>
        public TraceEvent GetCapturedEvent(int chronologicalIndex)
        {
            return _capture[chronologicalIndex];
        }

        /// <summary>
        /// Copies the captured events, oldest first, into
        /// <paramref name="destination"/> starting at
        /// <paramref name="destinationIndex"/>.
        /// </summary>
        public void CopyCapturedTo(TraceEvent[] destination, int destinationIndex)
        {
            _capture.CopyTo(destination, destinationIndex);
        }

        /// <summary>
        /// Creates an immutable snapshot of the frozen capture. Only valid when
        /// the recorder is <see cref="TraceFlightRecorderState.Frozen"/>. The
        /// logger is not referenced, so the snapshot can be created even after
        /// the logger has been disposed.
        /// </summary>
        public TraceCaptureSnapshot CreateFrozenSnapshot()
        {
            if (_state != TraceFlightRecorderState.Frozen)
            {
                throw new InvalidOperationException("A snapshot can only be created from a frozen recorder.");
            }

            int total = _capture.Count;
            if ((long)_triggerHistoryCount + (long)_capturedPostRollCount != (long)total)
            {
                throw new InvalidOperationException("Recorder capture counters are inconsistent with the captured event count.");
            }

            TraceEvent[] events = new TraceEvent[total];
            _capture.CopyTo(events, 0);

            return new TraceCaptureSnapshot(
                events,
                _triggerHistoryCount,
                _capturedPostRollCount,
                _wasHistoryOverwrittenAtTrigger);
        }

        /// <summary>
        /// Clears the capture and its counters and returns to Armed. The logger
        /// history, queue and counters are left untouched, and the logger is not
        /// disposed.
        /// </summary>
        /// <remarks>
        /// The reset is rejected without changing anything when the recorder is
        /// <see cref="TraceFlightRecorderState.AwaitingFreezeTerminal"/>, or when
        /// the capture logger is sealing or sealed.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// Thrown when the recorder is
        /// <see cref="TraceFlightRecorderState.AwaitingFreezeTerminal"/>, or when
        /// the capture logger is sealing or sealed.
        /// </exception>
        public void Reset()
        {
            if (_state == TraceFlightRecorderState.AwaitingFreezeTerminal)
            {
                throw new InvalidOperationException("The recorder is awaiting the freeze terminal append and cannot be reset.");
            }

            if (_logger.SealState != TraceRunSealState.Open)
            {
                throw new InvalidOperationException("The capture run is sealing or sealed and the recorder cannot be reset.");
            }

            _capture.Clear();
            _triggerHistoryCount = 0;
            _capturedPostRollCount = 0;
            _traceCaptureOverflowCount = 0;
            _wasHistoryOverwrittenAtTrigger = false;
            _state = TraceFlightRecorderState.Armed;
        }

        private int DrainCapturingPostRoll()
        {
            int remaining = NormalPostRollCapacity - _capturedPostRollCount;
            if (remaining <= 0)
            {
                if (_freezeTerminalTraceReserve == 0)
                {
                    _state = TraceFlightRecorderState.Frozen;
                }

                int drainedOnly = _logger.Drain();
                _traceCaptureOverflowCount = SaturatingAdd(_traceCaptureOverflowCount, drainedOnly);
                return drainedOnly;
            }

            int drained = _logger.Drain(_capture, remaining, out int captured);
            _capturedPostRollCount += captured;
            _traceCaptureOverflowCount = SaturatingAdd(_traceCaptureOverflowCount, drained - captured);

            if (_capturedPostRollCount >= NormalPostRollCapacity && _freezeTerminalTraceReserve == 0)
            {
                _state = TraceFlightRecorderState.Frozen;
            }

            return drained;
        }

        internal static int SaturatingAdd(int current, int delta)
        {
            long sum = (long)current + (long)delta;
            if (sum > int.MaxValue)
            {
                return int.MaxValue;
            }

            return (int)sum;
        }
    }
}
