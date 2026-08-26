using Zantetsu.Trace;

namespace Zantetsu.Observability.Editor
{
    /// <summary>
    /// Optional, AND-combined filter criteria for trace timeline events. The
    /// default value matches everything. An explicit zero is a valid search
    /// value and is distinguished from "unspecified".
    /// </summary>
    public readonly struct TraceTimelineFilter
    {
        public readonly long? SlashId;
        public readonly long? ObjectId;
        public readonly uint? ObjectGeneration;
        public readonly long? MobId;
        public readonly uint? PlanGeneration;
        public readonly long? TaskId;
        public readonly TraceEventType? EventType;
        public readonly TraceReason? Reason;

        public TraceTimelineFilter(
            long? slashId,
            long? objectId,
            uint? objectGeneration,
            long? mobId,
            uint? planGeneration,
            long? taskId,
            TraceEventType? eventType,
            TraceReason? reason)
        {
            SlashId = slashId;
            ObjectId = objectId;
            ObjectGeneration = objectGeneration;
            MobId = mobId;
            PlanGeneration = planGeneration;
            TaskId = taskId;
            EventType = eventType;
            Reason = reason;
        }

        public bool Matches(in TraceEvent traceEvent)
        {
            if (SlashId.HasValue && traceEvent.SlashId != SlashId.Value)
            {
                return false;
            }

            if (ObjectId.HasValue && traceEvent.ObjectId != ObjectId.Value)
            {
                return false;
            }

            if (ObjectGeneration.HasValue && traceEvent.ObjectGeneration != ObjectGeneration.Value)
            {
                return false;
            }

            if (MobId.HasValue && traceEvent.MobId != MobId.Value)
            {
                return false;
            }

            if (PlanGeneration.HasValue && traceEvent.PlanGeneration != PlanGeneration.Value)
            {
                return false;
            }

            if (TaskId.HasValue && traceEvent.TaskId != TaskId.Value)
            {
                return false;
            }

            if (EventType.HasValue && traceEvent.EventType != EventType.Value)
            {
                return false;
            }

            if (Reason.HasValue && traceEvent.Reason != Reason.Value)
            {
                return false;
            }

            return true;
        }
    }
}
