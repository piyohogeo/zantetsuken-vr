using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, reference-free snapshot of the freeze terminal checkpoint: the
    /// timestamp, frame, fixed step, thread, and run identity fixed once after
    /// the capture run logger is sealed and finally drained. All values are
    /// supplied by the caller; this struct performs no clock, frame, or thread
    /// lookup.
    /// </summary>
    /// <remarks>
    /// The struct holds only value-type fields and performs no managed
    /// allocation. <see cref="IsValid"/> is derived from the held values rather
    /// than stored, so <c>default(FreezeTerminalCheckpoint)</c> is invalid.
    /// </remarks>
    internal readonly struct FreezeTerminalCheckpoint
    {
        public readonly long Timestamp;
        public readonly long FrameId;
        public readonly long FixedStepId;
        public readonly int ThreadId;
        public readonly long TestRunId;

        internal FreezeTerminalCheckpoint(
            long timestamp,
            long frameId,
            long fixedStepId,
            int threadId,
            long testRunId)
        {
            if (timestamp < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(timestamp), timestamp, "Timestamp must not be negative.");
            }

            if (frameId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(frameId), frameId, "Frame ID must not be negative.");
            }

            if (fixedStepId < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fixedStepId), fixedStepId, "Fixed step ID must not be negative.");
            }

            if (threadId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(threadId), threadId, "Thread ID must be greater than zero.");
            }

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            Timestamp = timestamp;
            FrameId = frameId;
            FixedStepId = fixedStepId;
            ThreadId = threadId;
            TestRunId = testRunId;
        }

        /// <summary>
        /// Whether this checkpoint holds a meaningful, non-negative timestamp,
        /// frame, step, thread, and run identity. Derived from the held values
        /// so it re-checks the constructor's invariants even for values restored
        /// outside the constructor; <c>default</c> is invalid because its thread
        /// and run IDs are zero.
        /// </summary>
        public bool IsValid =>
            Timestamp >= 0 &&
            FrameId >= 0 &&
            FixedStepId >= 0 &&
            ThreadId > 0 &&
            TestRunId > 0;

        /// <summary>
        /// Compares all five held fields explicitly without reflection, boxing,
        /// <see cref="ValueType.Equals(object)"/>, or stringification.
        /// </summary>
        internal bool IdenticalTo(in FreezeTerminalCheckpoint other)
        {
            return Timestamp == other.Timestamp
                && FrameId == other.FrameId
                && FixedStepId == other.FixedStepId
                && ThreadId == other.ThreadId
                && TestRunId == other.TestRunId;
        }
    }
}
