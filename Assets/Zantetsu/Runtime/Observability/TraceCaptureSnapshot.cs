using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of a frozen flight recorder capture. The event array
    /// is owned privately and never exposed, so the snapshot cannot change after
    /// creation regardless of recorder reset, re-trigger, logger drain, or
    /// logger disposal.
    /// </summary>
    public sealed class TraceCaptureSnapshot
    {
        private readonly TraceEvent[] _events;
        private readonly int _triggerHistoryCount;
        private readonly int _capturedPostRollCount;
        private readonly bool _wasHistoryOverwrittenAtTrigger;

        /// <summary>
        /// Constructs a snapshot and takes ownership of <paramref name="events"/>.
        /// Internal to prevent callers from building snapshots whose counters are
        /// inconsistent with the event array.
        /// </summary>
        internal TraceCaptureSnapshot(
            TraceEvent[] events,
            int triggerHistoryCount,
            int capturedPostRollCount,
            bool wasHistoryOverwrittenAtTrigger)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (triggerHistoryCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(triggerHistoryCount), triggerHistoryCount, "Trigger history count must not be negative.");
            }

            if (capturedPostRollCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capturedPostRollCount), capturedPostRollCount, "Captured post-roll count must not be negative.");
            }

            long total = (long)triggerHistoryCount + (long)capturedPostRollCount;
            if (total > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(capturedPostRollCount), capturedPostRollCount, "Combined event counts overflow.");
            }

            if (total != (long)events.Length)
            {
                throw new ArgumentException("Event counts do not match the event array length.", nameof(events));
            }

            _events = events;
            _triggerHistoryCount = triggerHistoryCount;
            _capturedPostRollCount = capturedPostRollCount;
            _wasHistoryOverwrittenAtTrigger = wasHistoryOverwrittenAtTrigger;
        }

        /// <summary>Total number of captured events.</summary>
        public int EventCount => _events.Length;

        /// <summary>Number of pre-trigger history events.</summary>
        public int TriggerHistoryCount => _triggerHistoryCount;

        /// <summary>
        /// Number of events recorded into the capture after trigger, including
        /// the freeze-terminal direct append and, in a snapshot produced by the
        /// trace integrity summary factory, the single appended integrity
        /// summary event. If only the count of normal post-roll duplications is
        /// needed, it must be separated by verifying the terminal tail
        /// structure.
        /// </summary>
        public int CapturedPostRollCount => _capturedPostRollCount;

        /// <summary>Whether the logger history had already overwritten events at trigger time.</summary>
        public bool WasHistoryOverwrittenAtTrigger => _wasHistoryOverwrittenAtTrigger;

        /// <summary>
        /// Returns the captured event at the given chronological index, where 0
        /// is the oldest event. The struct is returned by value.
        /// </summary>
        public TraceEvent GetEvent(int chronologicalIndex)
        {
            if (chronologicalIndex < 0 || chronologicalIndex >= _events.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(chronologicalIndex), chronologicalIndex, "Chronological index is out of range.");
            }

            return _events[chronologicalIndex];
        }

        /// <summary>
        /// Copies the captured events, oldest first, into
        /// <paramref name="destination"/> starting at
        /// <paramref name="destinationIndex"/>.
        /// </summary>
        public void CopyEventsTo(TraceEvent[] destination, int destinationIndex)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            if (destinationIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(destinationIndex), destinationIndex, "Destination index must not be negative.");
            }

            if (destination.Length - destinationIndex < _events.Length)
            {
                throw new ArgumentException("Destination array does not have enough space for all events.", nameof(destination));
            }

            Array.Copy(_events, 0, destination, destinationIndex, _events.Length);
        }
    }
}
