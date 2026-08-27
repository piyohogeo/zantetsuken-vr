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

        /// <summary>Number of post-roll events duplicated into the capture so far.</summary>
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

                case TraceFlightRecorderState.Armed:
                case TraceFlightRecorderState.Frozen:
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
        public void Reset()
        {
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
