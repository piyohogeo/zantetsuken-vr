using System.Runtime.InteropServices;

namespace Zantetsu.Trace
{
    /// <summary>
    /// Fixed-size, reference-free trace event record. All values are supplied
    /// by the caller; this struct performs no timing, frame, or state lookup.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TraceEvent
    {
        public long Timestamp;
        public long FrameId;
        public long FixedStepId;
        public int ThreadId;

        public long SlashId;
        public uint SlashGeneration;
        public long FrontEdgeId;
        public long ObjectId;
        public uint ObjectGeneration;

        public long MobId;
        public uint PlanGeneration;
        public long TaskId;

        public long CaptureFrameId;
        public long OpenXRFrameId;
        public long TestRunId;

        public TraceEventType EventType;
        public TraceTaskType TaskType;
        public int FromState;
        public int ToState;
        public TraceReason Reason;

        public double Value0;
        public double Value1;
    }
}
