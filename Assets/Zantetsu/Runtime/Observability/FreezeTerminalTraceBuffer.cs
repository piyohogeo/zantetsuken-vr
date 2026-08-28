using System;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, pre-allocated freeze terminal trace buffer: the forced-drop
    /// (reason 9) drop event column followed by exactly one trailing
    /// <see cref="TraceEventType.CaptureRingFrozen"/> event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Count"/> always equals <see cref="ForcedDropCount"/> + 1; the
    /// last event is always <see cref="TraceEventType.CaptureRingFrozen"/> and
    /// is emitted even when there are zero forced drops. This buffer is the sole
    /// allocator of its backing array: it allocates exactly one array, fills it,
    /// and never returns it; <see cref="GetEvent"/> returns a value copy, so no
    /// external reference to the array can mutate this buffer.
    /// </para>
    /// <para>
    /// This buffer owns and disposes no set, registry, checkpoint, or event and
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class FreezeTerminalTraceBuffer
    {
        private readonly long _testRunId;
        private readonly int _forcedDropCount;
        private readonly int _count;
        private readonly FreezeTerminalCheckpoint _checkpoint;
        private readonly ForcedDropFrameIdSet _forcedDropFrameIds;
        private readonly TraceEvent[] _events;

        internal FreezeTerminalTraceBuffer(
            CaptureFrameDraftRegistry draftRegistry,
            ForcedDropFrameIdSet forcedDropFrameIds,
            in FreezeTerminalCheckpoint checkpoint)
        {
            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (forcedDropFrameIds == null)
            {
                throw new ArgumentNullException(nameof(forcedDropFrameIds));
            }

            if (!ReferenceEquals(forcedDropFrameIds.IssuedBy, draftRegistry))
            {
                throw new ArgumentException("Forced-drop set must be issued by the registry.", nameof(forcedDropFrameIds));
            }

            if (!ReferenceEquals(forcedDropFrameIds, draftRegistry.IssuedForcedDropFrameIdSet))
            {
                throw new ArgumentException("Forced-drop set must be the registry's issued forced-drop set.", nameof(forcedDropFrameIds));
            }

            if (!forcedDropFrameIds.IsValid)
            {
                throw new ArgumentException("Forced-drop set must be valid.", nameof(forcedDropFrameIds));
            }

            if (!checkpoint.IsValid)
            {
                throw new ArgumentException("Checkpoint must be valid.", nameof(checkpoint));
            }

            if (forcedDropFrameIds.TestRunId != draftRegistry.Run.TestRunId)
            {
                throw new ArgumentException("Forced-drop set test run ID must match the registry run.", nameof(forcedDropFrameIds));
            }

            if (checkpoint.TestRunId != forcedDropFrameIds.TestRunId)
            {
                throw new ArgumentException("Checkpoint test run ID must match the forced-drop set.", nameof(checkpoint));
            }

            int count = checked(forcedDropFrameIds.Count + 1);
            TraceEvent[] events = new TraceEvent[count];

            for (int i = 0; i < forcedDropFrameIds.Count; i++)
            {
                CaptureFrameDraftTraceContext context = draftRegistry.GetForcedDropTraceContext(forcedDropFrameIds, i);
                events[i] = BuildForcedDropEvent(context);
            }

            events[count - 1] = BuildRingFrozenEvent(checkpoint, forcedDropFrameIds.Count);

            _testRunId = checkpoint.TestRunId;
            _forcedDropCount = forcedDropFrameIds.Count;
            _count = count;
            _checkpoint = checkpoint;
            _forcedDropFrameIds = forcedDropFrameIds;
            _events = events;
        }

        private static TraceEvent BuildForcedDropEvent(in CaptureFrameDraftTraceContext context)
        {
            TraceEvent e = default;
            e.Timestamp = context.Timestamp;
            e.FrameId = context.UnityFrameId;
            e.FixedStepId = context.FixedStepId;
            e.ThreadId = context.ThreadId;
            e.SlashId = context.SlashId;
            e.SlashGeneration = 0;
            e.FrontEdgeId = context.FrontEdgeId;
            e.ObjectId = context.ObjectId;
            e.ObjectGeneration = context.ObjectGeneration;
            e.MobId = 0;
            e.PlanGeneration = 0;
            e.TaskId = context.TaskId;
            e.CaptureFrameId = context.CaptureFrameId;
            e.OpenXRFrameId = context.OpenXRFrameId;
            e.TestRunId = context.TestRunId;
            e.EventType = TraceEventType.CaptureFrameDropped;
            e.TaskType = TraceTaskType.None;
            e.FromState = 0; // Pending
            e.ToState = 2; // Dropped
            e.Reason = TraceReason.None;
            e.Value0 = 0.0;
            e.Value1 = 9.0; // FreezeDrainTimeout
            return e;
        }

        private static TraceEvent BuildRingFrozenEvent(in FreezeTerminalCheckpoint checkpoint, int forcedDropCount)
        {
            TraceEvent e = default;
            e.Timestamp = checkpoint.Timestamp;
            e.FrameId = checkpoint.FrameId;
            e.FixedStepId = checkpoint.FixedStepId;
            e.ThreadId = checkpoint.ThreadId;
            e.SlashId = 0;
            e.SlashGeneration = 0;
            e.FrontEdgeId = 0;
            e.ObjectId = 0;
            e.ObjectGeneration = 0;
            e.MobId = 0;
            e.PlanGeneration = 0;
            e.TaskId = 0;
            e.CaptureFrameId = 0;
            e.OpenXRFrameId = 0;
            e.TestRunId = checkpoint.TestRunId;
            e.EventType = TraceEventType.CaptureRingFrozen;
            e.TaskType = TraceTaskType.None;
            e.FromState = 3; // AwaitingFreezeTerminal
            e.ToState = 2; // Frozen
            e.Reason = TraceReason.None;
            e.Value0 = (double)forcedDropCount;
            e.Value1 = 0.0;
            return e;
        }

        public long TestRunId => _testRunId;

        public int ForcedDropCount => _forcedDropCount;

        public int Count => _count;

        internal FreezeTerminalCheckpoint Checkpoint => _checkpoint;

        internal ForcedDropFrameIdSet ForcedDropFrameIds => _forcedDropFrameIds;

        public TraceEvent GetEvent(int index)
        {
            if (index < 0 || index >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the buffer.");
            }

            return _events[index];
        }
    }
}
