using System;
using System.Collections.Generic;
using System.IO;

namespace Zantetsu.Core.MobPlan
{
    // PROP-OBSTACLE-1: the simulation's state as numbers, so a saved section of play can be taken up from its start: the
    // clock, the random numbers' state, the next ids, the player circle and every agent with its goal, its bookkeeping and
    // its plan. What is rebuilt from these (goal fields, grids) and what only counts (statistics) is not written.
    // Format 2 (2026-11-16) adds, per agent, what PROP-GOAL-REACH carries from one cycle to the next: the point the NPC is led
    // to and what it was set for, the goal stage, and the running "no point found" time. A format 1 state is still read; these
    // then start as in a new agent (no point, stage 0, no running time).
    public sealed partial class LocomotionSimulation
    {
        public const int StateFormat = 2;
        public const int OldestStateFormat = 1;

        /// <summary>Worker only (the simulation's own thread), between two cycles.</summary>
        public void SaveState(BinaryWriter w) => SaveState(w, StateFormat);

        /// <summary>An earlier format on purpose: for the test of reading one.</summary>
        internal void SaveState(BinaryWriter w, int format)
        {
            if (format < OldestStateFormat || format > StateFormat) throw new ArgumentOutOfRangeException(nameof(format));
            w.Write(format);
            w.Write(Time); w.Write(Revision); w.Write(NextCycleTime); w.Write(nextRecycleCheck);
            w.Write(goalRandom.State);
            w.Write(nextAgentId);
            w.Write(ViewYaw);
            w.Write(ExternalPlayer);
            w.Write(Obstacle != null);
            if (Obstacle != null) { w.Write(Obstacle.X); w.Write(Obstacle.Z); w.Write(Obstacle.HeadingDeg); w.Write(Obstacle.Speed); }
            w.Write(TotalGoalsReached);
            w.Write(Agents.Count);
            foreach (var a in Agents)
            {
                w.Write(a.Id); w.Write(a.Radius);
                Write(w, a.Start); Write(w, a.Pose);
                w.Write(a.GoalX); w.Write(a.GoalZ);
                w.Write(a.CurrentClip); w.Write(a.ClipLocalTime);
                w.Write(a.GoalsReached); w.Write(a.NextReplanTime); w.Write(a.GoalRevision);
                w.Write(a.GoalBlocked); w.Write(a.StandoffX); w.Write(a.StandoffZ); w.Write(a.BlockedWaitSeconds); w.Write(a.GoalsAbandoned);
                w.Write(a.FlowPhase); w.Write(a.FlowOffset); w.Write(a.FlowDirX); w.Write(a.FlowDirZ); w.Write(a.PassCount); w.Write(a.InPassRing);
                w.Write(a.PlannedGoalRevision); w.Write(a.BestGoalDistance); w.Write(a.LastProgressTime); w.Write(a.ProgressRevision);
                w.Write(a.StuckGoals); w.Write(a.LastRecycleTime); w.Write(a.Recycles); w.Write(a.Deaths); w.Write(a.Tier);
                w.Write(a.TravelDistance); w.Write(a.StationarySeconds); w.Write(a.MovingSeconds); w.Write(a.LastGoalTime);
                if (format >= 2)
                {
                    w.Write(a.GoalStage); w.Write(a.ReachNoneStage);
                    w.Write(a.ReachActive); w.Write(a.ReachX); w.Write(a.ReachZ); w.Write(a.ReachGoalRevision); w.Write(a.ReachNoneRevision);
                    w.Write(a.ReachVerified); w.Write(a.ReachNone); w.Write(a.ReachNoneSince); w.Write(a.ReachStandoff);
                }

                w.Write(a.Plan != null);
                if (a.Plan != null) WritePlan(w, a.Plan);
            }
        }

        /// <summary>Takes a saved state into a simulation made with the same data, map and scenario (Reset done). The caches follow by themselves.</summary>
        public void LoadState(BinaryReader r)
        {
            var format = r.ReadInt32();
            if (format < OldestStateFormat || format > StateFormat) throw new InvalidDataException("the simulation state's format " + format);
            Time = r.ReadSingle(); Revision = r.ReadInt32(); NextCycleTime = r.ReadSingle(); nextRecycleCheck = r.ReadSingle();
            goalRandom.State = r.ReadUInt64();
            nextAgentId = r.ReadInt32();
            ViewYaw = r.ReadSingle();
            ExternalPlayer = r.ReadBoolean();
            if (r.ReadBoolean())
            {
                var x = r.ReadSingle(); var z = r.ReadSingle(); var heading = r.ReadSingle(); var speed = r.ReadSingle();
                if (Obstacle != null) { Obstacle.X = x; Obstacle.Z = z; Obstacle.HeadingDeg = heading; Obstacle.Speed = speed; }
            }

            TotalGoalsReached = r.ReadSingle();
            var count = r.ReadInt32();
            Agents.Clear();
            for (var i = 0; i < count; i++)
            {
                var a = new SimAgent();
                a.Id = r.ReadInt32(); a.Radius = r.ReadSingle();
                a.Start = ReadPose(r); a.Pose = ReadPose(r);
                a.GoalX = r.ReadSingle(); a.GoalZ = r.ReadSingle();
                a.CurrentClip = r.ReadInt32(); a.ClipLocalTime = r.ReadSingle();
                a.GoalsReached = r.ReadInt32(); a.NextReplanTime = r.ReadSingle(); a.GoalRevision = r.ReadInt32();
                a.GoalBlocked = r.ReadBoolean(); a.StandoffX = r.ReadSingle(); a.StandoffZ = r.ReadSingle(); a.BlockedWaitSeconds = r.ReadSingle(); a.GoalsAbandoned = r.ReadInt32();
                a.FlowPhase = r.ReadInt32(); a.FlowOffset = r.ReadSingle(); a.FlowDirX = r.ReadSingle(); a.FlowDirZ = r.ReadSingle(); a.PassCount = r.ReadInt32(); a.InPassRing = r.ReadBoolean();
                a.PlannedGoalRevision = r.ReadInt32(); a.BestGoalDistance = r.ReadSingle(); a.LastProgressTime = r.ReadSingle(); a.ProgressRevision = r.ReadInt32();
                a.StuckGoals = r.ReadInt32(); a.LastRecycleTime = r.ReadSingle(); a.Recycles = r.ReadInt32(); a.Deaths = r.ReadInt32(); a.Tier = r.ReadInt32();
                a.TravelDistance = r.ReadSingle(); a.StationarySeconds = r.ReadSingle(); a.MovingSeconds = r.ReadSingle(); a.LastGoalTime = r.ReadSingle();
                if (format >= 2)
                {
                    a.GoalStage = r.ReadInt32(); a.ReachNoneStage = r.ReadInt32();
                    a.ReachActive = r.ReadBoolean(); a.ReachX = r.ReadSingle(); a.ReachZ = r.ReadSingle(); a.ReachGoalRevision = r.ReadInt32(); a.ReachNoneRevision = r.ReadInt32();
                    a.ReachVerified = r.ReadBoolean(); a.ReachNone = r.ReadBoolean(); a.ReachNoneSince = r.ReadSingle(); a.ReachStandoff = r.ReadBoolean();
                }

                if (r.ReadBoolean()) a.Plan = ReadPlan(r);
                Agents.Add(a);
            }

            goalFields.Clear();
        }

        public static void Write(BinaryWriter w, Pose2 pose) { w.Write(pose.X); w.Write(pose.Z); w.Write(pose.YawDeg); }
        public static Pose2 ReadPose(BinaryReader r) => new Pose2(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        public static void WritePlan(BinaryWriter w, AgentPlan plan)
        {
            w.Write(plan.AgentId); w.Write(plan.Revision); w.Write(plan.GeneratedAt); w.Write(plan.CommitUntil); w.Write(plan.Status ?? string.Empty);
            WriteSegments(w, plan.Segments);
        }

        public static AgentPlan ReadPlan(BinaryReader r)
        {
            var plan = new AgentPlan { AgentId = r.ReadInt32(), Revision = r.ReadInt32(), GeneratedAt = r.ReadSingle(), CommitUntil = r.ReadSingle(), Status = r.ReadString() };
            plan.Segments = ReadSegments(r);
            return plan;
        }

        public static void WriteSegments(BinaryWriter w, IReadOnlyList<PlanSegment> segments)
        {
            w.Write(segments.Count);
            for (var i = 0; i < segments.Count; i++)
            {
                var s = segments[i];
                w.Write(s.Clip); w.Write(s.StartTime); w.Write(s.EndTime); Write(w, s.WorldStart);
            }
        }

        public static List<PlanSegment> ReadSegments(BinaryReader r)
        {
            var count = r.ReadInt32();
            if (count < 0 || count > 100000) throw new InvalidDataException("a plan's segment count " + count);
            var segments = new List<PlanSegment>(count);
            for (var i = 0; i < count; i++) segments.Add(new PlanSegment(r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), ReadPose(r)));
            return segments;
        }
    }
}
