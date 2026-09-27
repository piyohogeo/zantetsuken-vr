using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    public sealed partial class LocomotionSimulation
    {
        public void AcknowledgeExternal(CycleResult cycle)
        {
            foreach (var info in cycle.Agents)
            {
                var agent = Agents.Find(a => a.Id == info.AgentId);
                if (agent == null) continue;
                agent.NextReplanTime = info.NextReplanTime; agent.Tier = info.Tier;
                if (!info.LodSkipped) agent.PlannedGoalRevision = agent.GoalRevision;
            }
        }
        public bool ExternalPlayer { get; set; }
        private int nextAgentId = 1000;
        /// <summary>Worker only. Restore the actually published baseline before goal updates and search.</summary>
        public PlanningSnapshot PrepareExternal(float now, IReadOnlyDictionary<int, PublishedMobPlan> baseline,
            float playerX, float playerZ, float velocityX, float velocityZ, float viewYaw)
        {
            AutoPlan = false;
            ExternalPlayer = true;
            Agents.RemoveAll(a => !baseline.ContainsKey(a.Id));
            foreach (var agent in Agents)
            {
                agent.Plan = baseline[agent.Id].CopyForPlanning();
                AgentPlan.FillStationary(Dataset, agent.Plan.Segments, now + Config.planningHorizonSeconds + 1);
            }
            // Snapshot prediction uses velocity heading; visibility uses the independently tracked HMD heading.
            Obstacle.X = playerX; Obstacle.Z = playerZ;
            Obstacle.Speed = (float)Math.Sqrt(velocityX * velocityX + velocityZ * velocityZ);
            Obstacle.HeadingDeg = Obstacle.Speed > .001f ? (float)Math.Atan2(velocityX, velocityZ) * Pose2.Rad2Deg : viewYaw;
            ViewYaw = viewYaw;
            Tick(Math.Max(0, now - Time));
            while (Agents.Count < Scenario.npcCount)
            {
                var replacement = new SimAgent { Id = nextAgentId++, Radius = Scenario.npcRadius };
                if (!TryFindSpawn(replacement, replacement.Radius + Config.staticMarginMeters + .3f, out float x, out float z)) break;
                Teleport(replacement, x, z, "replacement");
                Agents.Add(replacement);
            }
            if (EventLog.Count > 256) EventLog.RemoveRange(0, EventLog.Count - 256);
            var snapshot = Snapshot();
            Revision = snapshot.Revision;
            return snapshot;
        }
        public float ViewYaw { get; private set; }
    }
}
