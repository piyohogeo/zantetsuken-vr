using Unity.Collections;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Seal-aware writer for a capture run <see cref="TraceLogger"/>. Unlike
    /// the legacy <see cref="TraceLogger.JobWriter"/>, every enqueue attempt
    /// participates in the logger's atomic seal gate: an event is accepted only
    /// while the run is <see cref="TraceRunSealState.Open"/>.
    /// </summary>
    /// <remarks>
    /// The writer holds only value types (a queue parallel writer, the shared
    /// gate array, and the bound test run ID) and performs no managed
    /// allocation, LINQ, enumeration, logging, or string formatting inside
    /// <see cref="TryEnqueue"/>, so it can be captured by Burst jobs. It owns
    /// and disposes nothing; dispose the logger only after every producer that
    /// captured this writer has finished, the same contract as
    /// <see cref="TraceLogger.JobWriter"/>.
    /// </remarks>
    public readonly struct SealableTraceWriter
    {
        private readonly NativeQueue<TraceEvent>.ParallelWriter _queueWriter;
        private readonly NativeArray<int> _gate;
        private readonly long _testRunId;

        internal SealableTraceWriter(
            NativeQueue<TraceEvent>.ParallelWriter queueWriter,
            NativeArray<int> gate,
            long testRunId)
        {
            _queueWriter = queueWriter;
            _gate = gate;
            _testRunId = testRunId;
        }

        /// <summary>
        /// Attempts to enqueue an event. Returns false without touching the
        /// queue, gate, or counters when the event's
        /// <see cref="TraceEvent.TestRunId"/> does not match the bound run, or
        /// when the run is no longer <see cref="TraceRunSealState.Open"/>. If
        /// the queue write itself fails while Open, the failure is accounted
        /// exactly once as a mutable run failure and the exception rethrown;
        /// the active writer count is always restored.
        /// </summary>
        public bool TryEnqueue(in TraceEvent traceEvent)
        {
            if (traceEvent.TestRunId != _testRunId)
            {
                return false;
            }

            TraceRunSealGate.Increment(_gate, TraceRunSealGate.SlotActiveWriters);

            try
            {
                int sealState = TraceRunSealGate.Read(_gate, TraceRunSealGate.SlotSealState);
                if (sealState != (int)TraceRunSealState.Open)
                {
                    TraceRunSealGate.RecordRejection(_gate, sealState);
                    return false;
                }

                bool enqueued = false;
                try
                {
                    _queueWriter.Enqueue(traceEvent);
                    enqueued = true;
                }
                finally
                {
                    if (!enqueued)
                    {
                        TraceRunSealGate.RecordMutableFailure(_gate);
                    }
                }

                return true;
            }
            finally
            {
                TraceRunSealGate.Decrement(_gate, TraceRunSealGate.SlotActiveWriters);
            }
        }
    }
}
