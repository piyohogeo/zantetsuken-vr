namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, reference-free copy of the capture frame trace correlation
    /// identifiers for a draft. All values are bit-for-bit copies of
    /// <see cref="CaptureFrameTraceContext"/> with no correction, normalization,
    /// ID generation, or Unity static lookup.
    /// </summary>
    /// <remarks>
    /// The struct holds only value-type fields and performs no managed
    /// allocation. It carries no validity contract of its own; draft admission
    /// validation is the responsibility of the future draft factory.
    /// </remarks>
    internal readonly struct CaptureFrameDraftTraceContext
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

        /// <summary>
        /// Copies every field of <paramref name="context"/> exactly, without
        /// altering the source or applying any correction or normalization.
        /// </summary>
        internal CaptureFrameDraftTraceContext(in CaptureFrameTraceContext context)
        {
            Timestamp = context.Timestamp;
            UnityFrameId = context.UnityFrameId;
            FixedStepId = context.FixedStepId;
            ThreadId = context.ThreadId;
            CaptureFrameId = context.CaptureFrameId;
            OpenXRFrameId = context.OpenXRFrameId;
            TestRunId = context.TestRunId;
            SlashId = context.SlashId;
            FrontEdgeId = context.FrontEdgeId;
            ObjectId = context.ObjectId;
            ObjectGeneration = context.ObjectGeneration;
            TaskId = context.TaskId;
        }

        /// <summary>
        /// Compares all twelve held fields explicitly without reflection,
        /// boxing, ValueType.Equals, or stringification.
        /// </summary>
        internal bool IdenticalTo(in CaptureFrameDraftTraceContext other)
        {
            return Timestamp == other.Timestamp
                && UnityFrameId == other.UnityFrameId
                && FixedStepId == other.FixedStepId
                && ThreadId == other.ThreadId
                && CaptureFrameId == other.CaptureFrameId
                && OpenXRFrameId == other.OpenXRFrameId
                && TestRunId == other.TestRunId
                && SlashId == other.SlashId
                && FrontEdgeId == other.FrontEdgeId
                && ObjectId == other.ObjectId
                && ObjectGeneration == other.ObjectGeneration
                && TaskId == other.TaskId;
        }

        /// <summary>
        /// Compares all twelve held fields against the source context explicitly
        /// without reflection, boxing, ValueType.Equals, or stringification.
        /// </summary>
        internal bool IdenticalTo(in CaptureFrameTraceContext other)
        {
            return Timestamp == other.Timestamp
                && UnityFrameId == other.UnityFrameId
                && FixedStepId == other.FixedStepId
                && ThreadId == other.ThreadId
                && CaptureFrameId == other.CaptureFrameId
                && OpenXRFrameId == other.OpenXRFrameId
                && TestRunId == other.TestRunId
                && SlashId == other.SlashId
                && FrontEdgeId == other.FrontEdgeId
                && ObjectId == other.ObjectId
                && ObjectGeneration == other.ObjectGeneration
                && TaskId == other.TaskId;
        }
    }
}
