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
            float playerX, float playerZ, float velocityX, float velocityZ, float viewYaw, PropObstacleMap props = null)
        {
            Props = props;
            UpdatePropFields();
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
            UpdateGoalReach();
            var snapshot = Snapshot();
            Revision = snapshot.Revision;
            return snapshot;
        }
        public float ViewYaw { get; private set; }
        /// <summary>PROP-OBSTACLE-1: the prop fragments' map version of the cycle being prepared (null: none): spawn spots and new goals keep out of its occupied cells, and the snapshot carries it to the planner.</summary>
        public PropObstacleMap Props { get; set; }
        /// <summary>Spawn spots refused because they overlap the prop fragments' map.</summary>
        public int PropSpawnRejects { get; private set; }
        /// <summary>Goal picks refused because they overlap the prop fragments' map.</summary>
        public int PropGoalRejects { get; private set; }
        /// <summary>PROP-GOAL-REACH: points set / goals with no standable point in their reach / points set without a field path seen from the NPC / goals reached while led to such a point.</summary>
        public int GoalReachFound { get; private set; }
        public int GoalReachNone { get; private set; }
        public int GoalReachUnverified { get; private set; }
        public int GoalReachArrivals { get; private set; }
        /// <summary>No-point policy (PlannerConfig.propGoalNoReachMode): standoffs set / none within the standoff distance / arrivals at a standoff / goals given up / goals picked again.</summary>
        public int GoalStandoffSet { get; private set; }
        public int GoalStandoffNone { get; private set; }
        public int GoalStandoffArrivals { get; private set; }
        /// <summary>Goals reached by the goal's own condition while the NPC was led to a standoff (part of the goals reached, not of the standoff arrivals).</summary>
        public int GoalReachedWhileAtStandoff { get; private set; }
        public int GoalsGivenUpForProps { get; private set; }
        public int GoalsPickedAgainForProps { get; private set; }

        /// <summary>
        /// PROP-GOAL-REACH, once per prepared cycle: for an NPC whose goal point is nearer the prop cells than walking keeps, the
        /// nearest point within the goal's reach (goalRadiusMeters less the inset) that walking can stand on and, of the first few,
        /// one whose field has a path at the NPC's place. A point is kept while its goal is the same and it can still be stood on.
        /// </summary>
        private void UpdateGoalReach()
        {
            if (!Config.propGoalReach || !Config.propObstacles || Props == null)
            {
                foreach (var agent in Agents) { agent.ReachActive = false; agent.ReachStandoff = false; agent.ObserveNoPoint(false, Time); }
                return;
            }

            foreach (var agent in Agents)
            {
                if (agent.GoalBlocked) { agent.ReachActive = false; agent.ReachStandoff = false; agent.ReachGoalRevision = -1; agent.ObserveNoPoint(false, Time); continue; }
                float required = agent.Radius + Config.staticMarginMeters, keep = agent.Radius + Config.propKeepMeters;
                if (Props.DistanceAt(agent.GoalX, agent.GoalZ) >= keep) { agent.ReachActive = false; agent.ReachStandoff = false; agent.ObserveNoPoint(false, Time); continue; }   // the goal point is not the prop cells' matter
                if (agent.ReachValid && Map.ClearanceAt(agent.ReachX, agent.ReachZ) >= required && Props.DistanceAt(agent.ReachX, agent.ReachZ) >= keep)
                {
                    // kept. A point inside the reach ends the no-point time; a standoff does not (the sample still finds none)
                    if (!agent.ReachStandoff) agent.ObserveNoPoint(false, Time);
                    continue;
                }

                agent.ReachActive = false; agent.ReachStandoff = false;
                float reach = Config.goalRadiusMeters - Config.propGoalReachInsetMeters;
                const float step = 0.1f;
                int span = (int)Math.Floor(reach / step);
                reachCandidates.Clear();
                for (int dz = -span; dz <= span; dz++)
                {
                    for (int dx = -span; dx <= span; dx++)
                    {
                        float ox = dx * step, oz = dz * step, d2 = ox * ox + oz * oz;
                        if (d2 > reach * reach) continue;
                        float x = agent.GoalX + ox, z = agent.GoalZ + oz;
                        if (Map.ClearanceAt(x, z) < required || Props.DistanceAt(x, z) < keep) continue;
                        reachCandidates.Add((d2, dx, dz));
                    }
                }

                if (reachCandidates.Count == 0)
                {
                    if (agent.ReachNoneRevision != agent.GoalRevision) { agent.ReachNoneRevision = agent.GoalRevision; GoalReachNone++; }   // the per-goal count
                    agent.ObserveNoPoint(true, Time);                                                                                 // the running time, apart from it
                    NoReachPolicy(agent, required, keep);
                    continue;
                }

                reachCandidates.Sort((a, b) => a.d2 != b.d2 ? a.d2.CompareTo(b.d2) : a.dz != b.dz ? a.dz.CompareTo(b.dz) : a.dx.CompareTo(b.dx));
                int pick = -1;
                if (UseGoalField)
                {
                    for (int i = 0; i < reachCandidates.Count && i < 6; i++)
                    {
                        float x = agent.GoalX + reachCandidates[i].dx * step, z = agent.GoalZ + reachCandidates[i].dz * step;
                        var field = FieldFor(x, z, agent.Radius, agent.Pose.X, agent.Pose.Z);
                        if (field != null && !float.IsPositiveInfinity(field.PathDistanceAt(agent.Pose.X, agent.Pose.Z))) { pick = i; break; }
                    }
                }
                else pick = 0;

                agent.ReachVerified = pick >= 0;
                if (pick < 0) { pick = 0; GoalReachUnverified++; }
                agent.ObserveNoPoint(false, Time);
                agent.ReachX = agent.GoalX + reachCandidates[pick].dx * step; agent.ReachZ = agent.GoalZ + reachCandidates[pick].dz * step;
                agent.ReachActive = true;
                agent.ReachGoalRevision = agent.GoalRevision;
                GoalReachFound++;
            }
        }

        /// <summary>The sample found no point to stand on inside the goal's reach: what PlannerConfig.propGoalNoReachMode says, once it has lasted.</summary>
        private void NoReachPolicy(SimAgent agent, float required, float keep)
        {
            if (Config.propGoalNoReachMode == 0 || agent.NoPointSeconds(Time) < Config.propGoalNoReachSeconds) return;
            if (Config.propGoalNoReachMode == 1)
            {
                const float step = 0.2f;
                int span = (int)Math.Floor(Config.propGoalStandoffMeters / step);
                reachCandidates.Clear();
                for (int dz = -span; dz <= span; dz++)
                {
                    for (int dx = -span; dx <= span; dx++)
                    {
                        float ox = dx * step, oz = dz * step, d2 = ox * ox + oz * oz;
                        if (d2 > Config.propGoalStandoffMeters * Config.propGoalStandoffMeters) continue;
                        float x = agent.GoalX + ox, z = agent.GoalZ + oz;
                        if (Map.ClearanceAt(x, z) < required || Props.DistanceAt(x, z) < keep) continue;
                        reachCandidates.Add((d2, dx, dz));
                    }
                }

                if (reachCandidates.Count == 0) { GoalStandoffNone++; return; }
                reachCandidates.Sort((a, b) => a.d2 != b.d2 ? a.d2.CompareTo(b.d2) : a.dz != b.dz ? a.dz.CompareTo(b.dz) : a.dx.CompareTo(b.dx));
                int pick = -1;
                if (UseGoalField)
                {
                    for (int i = 0; i < reachCandidates.Count && i < 6; i++)
                    {
                        float x = agent.GoalX + reachCandidates[i].dx * step, z = agent.GoalZ + reachCandidates[i].dz * step;
                        var field = FieldFor(x, z, agent.Radius, agent.Pose.X, agent.Pose.Z);
                        if (field != null && !float.IsPositiveInfinity(field.PathDistanceAt(agent.Pose.X, agent.Pose.Z))) { pick = i; break; }
                    }
                }
                else pick = 0;

                agent.ReachVerified = pick >= 0;
                if (pick < 0) pick = 0;
                agent.ReachX = agent.GoalX + reachCandidates[pick].dx * step; agent.ReachZ = agent.GoalZ + reachCandidates[pick].dz * step;
                agent.ReachActive = true; agent.ReachStandoff = true;   // still "no point in the sample": the running time goes on
                agent.ReachGoalRevision = agent.GoalRevision;
                GoalStandoffSet++;
                return;
            }

            // 2 give up, 3 pick again: the goal is replaced (not reached)
            agent.GoalsAbandoned++;
            agent.ObserveNoPoint(false, Time);
            if (Config.propGoalNoReachMode == 3 && agent.FlowPhase == 2) { GoalsPickedAgainForProps++; AssignFarGoal(agent); return; }
            if (Config.propGoalNoReachMode == 3) GoalsPickedAgainForProps++; else GoalsGivenUpForProps++;
            if (agent.FlowPhase == 1) AssignFarGoal(agent);
            else AssignNextGoal(agent, "the goal cannot be stood on (prop fragments)");
        }

        private readonly List<(float d2, int dx, int dz)> reachCandidates = new List<(float d2, int dx, int dz)>();

        private bool InProps(float x, float z, float radius) => Config.propObstacles && Props != null && Props.Overlaps(x, z, radius);
    }
}
