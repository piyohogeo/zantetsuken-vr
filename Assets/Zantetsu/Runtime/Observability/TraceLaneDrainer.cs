using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Where drained trace records are handed to, one record at a time, on the
    /// draining thread.
    /// </summary>
    /// <remarks>
    /// The payload is the lane's own memory and is valid only until this call
    /// returns: a destination copies whatever it needs while it is called, and
    /// never keeps the pointer or takes ownership of it. Nothing is returned,
    /// because there is nothing to acknowledge - a record that was handed over
    /// is a record the lane may forget.
    /// </remarks>
    internal unsafe interface ITraceRecordDestination
    {
        void Receive(TraceEventType recordKind, byte* payload, int payloadLength);
    }

    /// <summary>
    /// Takes a bounded number of records out of a lane set and hands them to
    /// one destination, going round the lanes in ordinal order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One drain takes at most the profile's ordinary drain limit. It visits
    /// the lanes in a fixed order, takes one record from each lane that has
    /// one, and skips the lanes that are empty; if every lane is empty it
    /// takes nothing. Each lane comes out in its own order - nothing here puts
    /// records from different lanes into one sequence, and no record carries a
    /// time or a producer of its own.
    /// </para>
    /// <para>
    /// The next drain starts after the lane this one last took a record from,
    /// so a lane late in the order is not starved by lanes before it that keep
    /// being written to. A drain that takes nothing leaves that resume point
    /// where it was.
    /// </para>
    /// <para>
    /// A record is only forgotten once it has been handed over: the payload is
    /// passed while the lane still holds it, and the lane is told to release it
    /// after the destination returns. Draining happens on the calling thread
    /// and nothing else - no thread, task, timer, poll, sleep, or queue - and
    /// it allocates nothing, boxes nothing, and turns no payload into a managed
    /// array or a string on the way.
    /// </para>
    /// <para>
    /// The drainer owns neither the lanes nor the destination and releases
    /// neither of them.
    /// </para>
    /// </remarks>
    internal sealed unsafe class TraceLaneDrainer
    {
        private readonly TraceLaneSet _lanes;

        private int _nextOrdinal;

        internal TraceLaneDrainer(TraceLaneSet lanes)
        {
            _lanes = lanes ?? throw new ArgumentNullException(nameof(lanes));
        }

        /// <summary>
        /// The lane the next drain will look at first: the one after the lane
        /// the last drain took a record from.
        /// </summary>
        internal int NextOrdinal => _nextOrdinal;

        /// <summary>
        /// Hands over up to the profile's ordinary drain limit of records and
        /// returns how many were handed over.
        /// </summary>
        internal int Drain(ITraceRecordDestination destination)
        {
            if (destination == null)
            {
                throw new ArgumentNullException(nameof(destination));
            }

            int laneCount = _lanes.LaneCount;
            if (laneCount == 0)
            {
                // Only a released set has no lanes, and asking it for a record
                // says so the way the rest of the boundary does.
                _lanes.TryPeek(0, out TraceLaneIndexEntry _, out byte* _);
                return 0;
            }

            int limit = _lanes.NormalDrainMaxRecordCount;
            int ordinal = _nextOrdinal;
            int drained = 0;
            int skipped = 0;

            while (drained < limit)
            {
                if (_lanes.TryPeek(ordinal, out TraceLaneIndexEntry entry, out byte* payload))
                {
                    // Handed over while the lane still holds it, and released
                    // only once the destination has taken what it wanted.
                    destination.Receive(entry.RecordKind, payload, entry.PayloadLength);
                    _lanes.Consume(ordinal, entry);

                    drained++;
                    skipped = 0;
                    ordinal = ordinal + 1 == laneCount ? 0 : ordinal + 1;
                    _nextOrdinal = ordinal;
                    continue;
                }

                skipped++;
                if (skipped == laneCount)
                {
                    // Every lane was looked at and none had anything.
                    break;
                }

                ordinal = ordinal + 1 == laneCount ? 0 : ordinal + 1;
            }

            return drained;
        }
    }
}
