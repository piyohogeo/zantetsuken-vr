using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>One clip played from StartTime with its world start placement (Section 10.1).</summary>
    [Serializable]
    public struct PlanSegment
    {
        public int Clip;
        public float StartTime;
        public float EndTime;
        public Pose2 WorldStart;

        public PlanSegment(int clip, float startTime, float endTime, Pose2 worldStart)
        {
            Clip = clip; StartTime = startTime; EndTime = endTime; WorldStart = worldStart;
        }

        public float Duration => EndTime - StartTime;
    }

    /// <summary>Published plan of one agent: contiguous segments from ValidFrom, frozen up to CommitUntil.</summary>
    public sealed class AgentPlan
    {
        public int AgentId;
        public int Revision;
        public float GeneratedAt;
        public float CommitUntil;
        public string Status = "baseline";
        public List<PlanSegment> Segments = new List<PlanSegment>();

        public float StartTime => Segments.Count > 0 ? Segments[0].StartTime : 0f;
        public float EndTime => Segments.Count > 0 ? Segments[Segments.Count - 1].EndTime : 0f;

        public AgentPlan Clone()
        {
            return new AgentPlan { AgentId = AgentId, Revision = Revision, GeneratedAt = GeneratedAt, CommitUntil = CommitUntil, Status = Status, Segments = new List<PlanSegment>(Segments) };
        }

        public int SegmentIndexAt(float time)
        {
            if (Segments.Count == 0) return -1;
            if (time <= Segments[0].StartTime) return 0;
            // Binary search on StartTime.
            var lo = 0;
            var hi = Segments.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (Segments[mid].StartTime <= time) lo = mid; else hi = mid - 1;
            }

            return lo;
        }

        public static Pose2 PoseAt(ClipDataset dataset, IReadOnlyList<PlanSegment> segments, float time)
        {
            if (segments.Count == 0) return Pose2.Identity;
            var index = 0;
            if (time > segments[0].StartTime)
            {
                var lo = 0;
                var hi = segments.Count - 1;
                while (lo < hi)
                {
                    var mid = (lo + hi + 1) / 2;
                    if (segments[mid].StartTime <= time) lo = mid; else hi = mid - 1;
                }

                index = lo;
            }

            var segment = segments[index];
            var local = time - segment.StartTime;
            if (local < 0f) local = 0f;
            return segment.WorldStart.Compose(dataset.LocalPoseAt(segment.Clip, local));
        }

        public Pose2 PoseAt(ClipDataset dataset, float time) => PoseAt(dataset, Segments, time);

        public static Pose2 EndPose(ClipDataset dataset, IReadOnlyList<PlanSegment> segments)
        {
            if (segments.Count == 0) return Pose2.Identity;
            var last = segments[segments.Count - 1];
            return last.WorldStart.Compose(dataset.Clips[last.Clip].EndPose);
        }

        /// <summary>Samples the segments at a uniform rate from `from` up to (and including) the last sample &lt;= plan end.</summary>
        public static WorldTrajectory Sample(ClipDataset dataset, IReadOnlyList<PlanSegment> segments, float from, float hz, float minUntil = float.NegativeInfinity)
        {
            var dt = 1f / hz;
            var end = segments.Count > 0 ? segments[segments.Count - 1].EndTime : from;
            if (end < minUntil) end = minUntil;
            var count = Math.Max(1, (int)Math.Floor((end - from) / dt + 1e-4f) + 1);
            var trajectory = new WorldTrajectory { StartTime = from, Dt = dt, X = new float[count], Z = new float[count], Yaw = new float[count] };
            var index = 0;
            for (var i = 0; i < count; i++)
            {
                var time = from + i * dt;
                while (index + 1 < segments.Count && segments[index + 1].StartTime <= time) index++;
                var segment = segments[Math.Min(index, segments.Count - 1)];
                var local = Math.Max(0f, time - segment.StartTime);
                var pose = segment.WorldStart.Compose(dataset.LocalPoseAt(segment.Clip, local));
                trajectory.X[i] = pose.X;
                trajectory.Z[i] = pose.Z;
                trajectory.Yaw[i] = pose.YawDeg;
            }

            trajectory.ComputeBounds();
            return trajectory;
        }

        /// <summary>Appends the stationary continuation of the last segment's clip until the plan reaches `until`. Returns false when impossible.</summary>
        public static bool FillStationary(ClipDataset dataset, List<PlanSegment> segments, float until, int maxSegments = 64)
        {
            if (segments.Count == 0) return false;
            var guard = 0;
            while (segments[segments.Count - 1].EndTime < until - 1e-4f)
            {
                var last = segments[segments.Count - 1];
                var next = dataset.Clips[last.Clip].StationaryContinuation;
                if (next < 0 || ++guard > maxSegments) return false;
                var start = last.WorldStart.Compose(dataset.Clips[last.Clip].EndPose);
                segments.Add(new PlanSegment(next, last.EndTime, last.EndTime + dataset.Clips[next].Duration, start));
            }

            return true;
        }
    }
}
