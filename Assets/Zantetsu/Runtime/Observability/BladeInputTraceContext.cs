namespace Zantetsu.Observability
{
    /// <summary>
    /// Caller-supplied correlation context for blade input trace events.
    /// Reference-free value type; the observer never fetches or generates any
    /// of these values itself.
    /// </summary>
    public readonly struct BladeInputTraceContext
    {
        public readonly long Timestamp;
        public readonly long FixedStepId;
        public readonly int ThreadId;
        public readonly long OpenXRFrameId;
        public readonly long TestRunId;

        public BladeInputTraceContext(long timestamp, long fixedStepId, int threadId, long openXRFrameId, long testRunId)
        {
            Timestamp = timestamp;
            FixedStepId = fixedStepId;
            ThreadId = threadId;
            OpenXRFrameId = openXRFrameId;
            TestRunId = testRunId;
        }
    }
}
