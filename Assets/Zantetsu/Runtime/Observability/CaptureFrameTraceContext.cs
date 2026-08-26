namespace Zantetsu.Observability
{
    /// <summary>
    /// Correlation identifiers for a capture frame trace event. All values are
    /// supplied by the caller; this struct performs no Unity static lookup and
    /// holds no reference types.
    /// </summary>
    public readonly struct CaptureFrameTraceContext
    {
        public readonly long Timestamp;
        public readonly long UnityFrameId;
        public readonly long FixedStepId;
        public readonly int ThreadId;
        public readonly long CaptureFrameId;
        public readonly long OpenXRFrameId;
        public readonly long TestRunId;
        public readonly long SlashId;
        public readonly long FrontEdgeId;
        public readonly long ObjectId;
        public readonly uint ObjectGeneration;
        public readonly long TaskId;

        public CaptureFrameTraceContext(
            long timestamp,
            long unityFrameId,
            long fixedStepId,
            int threadId,
            long captureFrameId,
            long openXRFrameId,
            long testRunId,
            long slashId,
            long frontEdgeId,
            long objectId,
            uint objectGeneration,
            long taskId)
        {
            Timestamp = timestamp;
            UnityFrameId = unityFrameId;
            FixedStepId = fixedStepId;
            ThreadId = threadId;
            CaptureFrameId = captureFrameId;
            OpenXRFrameId = openXRFrameId;
            TestRunId = testRunId;
            SlashId = slashId;
            FrontEdgeId = frontEdgeId;
            ObjectId = objectId;
            ObjectGeneration = objectGeneration;
            TaskId = taskId;
        }
    }
}
