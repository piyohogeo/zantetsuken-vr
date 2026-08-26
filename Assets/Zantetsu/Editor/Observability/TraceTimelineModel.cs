using System;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Observability.Editor
{
    /// <summary>
    /// Editor-side snapshot of trace events, deterministically sorted by
    /// timestamp, with lane grouping and filter-based visibility for timeline
    /// display. Getters used during GUI repaint do not allocate, sort, or use
    /// LINQ.
    /// </summary>
    public sealed class TraceTimelineModel
    {
        private TraceEvent[] _events = Array.Empty<TraceEvent>();
        private int _count;
        private int[] _visibleIndices = Array.Empty<int>();
        private int _visibleCount;

        private TraceTimelineLane _lane = TraceTimelineLane.All;
        private TraceTimelineFilter _filter = default;

        public int Count => _count;

        public int VisibleCount => _visibleCount;

        public TraceTimelineLane Lane
        {
            get => _lane;
            set
            {
                ValidateLane(value);
                _lane = value;
            }
        }

        public TraceTimelineFilter Filter
        {
            get => _filter;
            set
            {
                _filter = value;
                RebuildVisible();
            }
        }

        public long MinimumTimestamp => _count == 0 ? 0L : _events[0].Timestamp;

        public long MaximumTimestamp => _count == 0 ? 0L : _events[_count - 1].Timestamp;

        /// <summary>
        /// Loads a defensive, timestamp-sorted copy of the source events.
        /// </summary>
        public void Load(TraceEvent[] source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            int n = source.Length;

            TraceEvent[] events = new TraceEvent[n];
            Array.Copy(source, events, n);

            int[] order = new int[n];
            for (int i = 0; i < n; i++)
            {
                order[i] = i;
            }

            Array.Sort(order, (a, b) =>
            {
                int cmp = events[a].Timestamp.CompareTo(events[b].Timestamp);
                if (cmp != 0)
                {
                    return cmp;
                }

                cmp = events[a].FrameId.CompareTo(events[b].FrameId);
                if (cmp != 0)
                {
                    return cmp;
                }

                // Stable tie-breaker: preserve input order without relying on
                // Array.Sort stability.
                return a.CompareTo(b);
            });

            TraceEvent[] sorted = new TraceEvent[n];
            for (int i = 0; i < n; i++)
            {
                sorted[i] = events[order[i]];
            }

            _events = sorted;
            _count = n;
            _visibleIndices = new int[n];
            RebuildVisible();
        }

        /// <summary>
        /// Loads a snapshot of the logger's drained history. Does not drain the
        /// logger, does not mutate its queue/history/counters, and does not
        /// dispose it.
        /// </summary>
        public void Load(TraceLogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            TraceEvent[] snapshot = new TraceEvent[logger.HistoryCount];
            logger.CopyHistoryTo(snapshot, 0);
            Load(snapshot);
        }

        public void Clear()
        {
            _events = Array.Empty<TraceEvent>();
            _count = 0;
            _visibleIndices = Array.Empty<int>();
            _visibleCount = 0;
        }

        public TraceEvent GetEvent(int chronologicalIndex)
        {
            if (chronologicalIndex < 0 || chronologicalIndex >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(chronologicalIndex));
            }

            return _events[chronologicalIndex];
        }

        public TraceEvent GetVisibleEvent(int visibleIndex)
        {
            if (visibleIndex < 0 || visibleIndex >= _visibleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(visibleIndex));
            }

            return _events[_visibleIndices[visibleIndex]];
        }

        public long GetVisibleLaneKey(int visibleIndex)
        {
            if (visibleIndex < 0 || visibleIndex >= _visibleCount)
            {
                throw new ArgumentOutOfRangeException(nameof(visibleIndex));
            }

            TraceEvent traceEvent = _events[_visibleIndices[visibleIndex]];
            switch (_lane)
            {
                case TraceTimelineLane.Slash:
                    return traceEvent.SlashId;
                case TraceTimelineLane.Object:
                    return traceEvent.ObjectId;
                case TraceTimelineLane.MobPlan:
                    return traceEvent.MobId;
                case TraceTimelineLane.Task:
                    return traceEvent.TaskId;
                case TraceTimelineLane.Thread:
                    return traceEvent.ThreadId;
                case TraceTimelineLane.All:
                default:
                    return 0L;
            }
        }

        private void RebuildVisible()
        {
            _visibleCount = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_filter.Matches(_events[i]))
                {
                    _visibleIndices[_visibleCount] = i;
                    _visibleCount++;
                }
            }
        }

        private static void ValidateLane(TraceTimelineLane lane)
        {
            switch (lane)
            {
                case TraceTimelineLane.All:
                case TraceTimelineLane.Slash:
                case TraceTimelineLane.Object:
                case TraceTimelineLane.MobPlan:
                case TraceTimelineLane.Task:
                case TraceTimelineLane.Thread:
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(lane), lane, "Unknown lane.");
            }
        }
    }
}
