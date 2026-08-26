using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Value-type handle that ties a logical work item's samples together on
    /// the CPU Profiler timeline across threads and jobs.
    /// </summary>
    /// <remarks>
    /// Flow events must be emitted from inside the corresponding fixed
    /// <see cref="ProfilerMarker"/> sample:
    /// <list type="bullet">
    /// <item><description>Schedule-side sample: <see cref="Begin"/>.</description></item>
    /// <item><description>Job/worker start sample: <see cref="Next"/>.</description></item>
    /// <item><description>Multiple parallel branches: <see cref="ParallelNext"/>.</description></item>
    /// <item><description>Final commit/dispose sample: <see cref="End"/>.</description></item>
    /// </list>
    /// The handle contains no reference-type fields and can be copied by value
    /// into jobs. <see cref="TaskId"/> is retained only for correlation with
    /// the domain trace; <see cref="FlowId"/> is valid only while the Profiler
    /// is active and must not be used as a persistent ID in a saved trace.
    /// </remarks>
    public readonly struct WorkItemProfilerFlow
    {
        public readonly long TaskId;
        public readonly uint FlowId;

        /// <summary>Whether this handle carries an active Profiler flow ID.</summary>
        public bool IsValid => FlowId != 0;

        private WorkItemProfilerFlow(long taskId, uint flowId)
        {
            TaskId = taskId;
            FlowId = flowId;
        }

        /// <summary>Creates a flow handle for the given work-item task ID.</summary>
        public static WorkItemProfilerFlow Create(long taskId)
        {
            uint flowId = ProfilerUnsafeUtility.CreateFlow(ProfilerUnsafeUtility.CategoryScripts);
            return new WorkItemProfilerFlow(taskId, flowId);
        }

        /// <summary>Marks the scheduling sample as the flow start point.</summary>
        public void Begin()
        {
            if (FlowId != 0)
            {
                ProfilerUnsafeUtility.FlowEvent(FlowId, ProfilerFlowEventType.Begin);
            }
        }

        /// <summary>Marks the next sample as a flow continuation point.</summary>
        public void Next()
        {
            if (FlowId != 0)
            {
                ProfilerUnsafeUtility.FlowEvent(FlowId, ProfilerFlowEventType.Next);
            }
        }

        /// <summary>Marks the next sample as a parallel flow continuation point.</summary>
        public void ParallelNext()
        {
            if (FlowId != 0)
            {
                ProfilerUnsafeUtility.FlowEvent(FlowId, ProfilerFlowEventType.ParallelNext);
            }
        }

        /// <summary>Marks the final sample as the flow end point.</summary>
        public void End()
        {
            if (FlowId != 0)
            {
                ProfilerUnsafeUtility.FlowEvent(FlowId, ProfilerFlowEventType.End);
            }
        }
    }
}
