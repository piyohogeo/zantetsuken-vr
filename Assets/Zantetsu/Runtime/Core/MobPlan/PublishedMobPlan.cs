using System;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>Immutable plan publication. Current and Future resolve identically and never advance a clock.</summary>
    public sealed class PublishedMobPlan
    {
        private readonly PlanSegment[] segments;
        public int AgentId { get; }
        public int Generation { get; }
        public float ValidFrom => segments[0].StartTime;
        public float ValidUntil => segments[segments.Length - 1].EndTime;
        public int SegmentCount => segments.Length;
        public PlanSegment Segment(int index) => segments[index];
        public PublishedMobPlan(AgentPlan plan, int generation)
        {
            if (plan == null || plan.Segments.Count == 0) throw new ArgumentException("Empty plan");
            segments = plan.Segments.ToArray();
            AgentId = plan.AgentId;
            Generation = generation;
        }
        public bool TryResolve(double time, ClipDataset dataset, out PlanSegment segment, out double sourceTime, out Pose2 root)
        {
            segment = default; sourceTime = 0; root = default;
            if (!double.IsFinite(time) || time < ValidFrom || time > ValidUntil) return false;
            int lo = 0, hi = segments.Length - 1;
            while (lo < hi) { int mid = (lo + hi + 1) / 2; if (segments[mid].StartTime <= time) lo = mid; else hi = mid - 1; }
            segment = segments[lo];
            double local = Math.Min(time - segment.StartTime, segment.Duration);
            sourceTime = dataset.Clips[segment.Clip].RangeFrom + local;
            root = segment.WorldStart.Compose(dataset.LocalPoseAt(segment.Clip, (float)local));
            return true;
        }
        public AgentPlan CopyForPlanning() => new AgentPlan { AgentId = AgentId, Revision = Generation, Segments = new System.Collections.Generic.List<PlanSegment>(segments) };
    }
}
