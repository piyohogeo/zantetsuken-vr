using System;
using System.Collections.Generic;


namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// Receding-horizon driver (Section 14): plays published plans, snapshots them at ~1 Hz, runs the planner
    /// (inline or on one background task) and publishes results whose frozen boundaries are still in the future.
    /// No Unity objects; the window and the batch runner both drive this.
    /// </summary>
    public sealed partial class LocomotionSimulation
    {
        public ClipDataset Dataset { get; }
        public WalkableMap Map { get; private set; }
        public PlannerConfig Config { get; set; }
        public ScenarioSettings Scenario { get; private set; }

        /// <summary>
        /// Applies the goal / player knobs that are safe to change while running (the map, NPC count, seeds and starts need a Reset):
        /// player ring, pass offsets, far goal, reassignment, obstacle speed and prediction, goals-inside-obstacle probability.
        /// </summary>
        public void ApplyLiveSettings(ScenarioSettings s)
        {
            if (Scenario == null) return;
            Scenario.flowRingMeters = s.flowRingMeters;
            Scenario.flowPassOffsetMinMeters = s.flowPassOffsetMinMeters;
            Scenario.flowPassOffsetMaxMeters = s.flowPassOffsetMaxMeters;
            Scenario.flowFarGoalMeters = s.flowFarGoalMeters;
            Scenario.flowFarGoalConeDeg = s.flowFarGoalConeDeg;
            Scenario.reassignGoals = s.reassignGoals;
            Scenario.obstaclePrediction = s.obstaclePrediction;
            Scenario.obstaclePredictionSeconds = s.obstaclePredictionSeconds;
            Scenario.goalNearObstacleProbability = s.goalNearObstacleProbability;
            Scenario.recycleEnabled = s.recycleEnabled;
            Scenario.recycleDistanceMeters = s.recycleDistanceMeters;
            Scenario.recycleSpawnMinMeters = s.recycleSpawnMinMeters;
            Scenario.recycleSpawnMaxMeters = s.recycleSpawnMaxMeters;
            Scenario.recycleViewDeg = s.recycleViewDeg;
            Scenario.recycleViewRangeMeters = s.recycleViewRangeMeters;
            Scenario.recycleOcclusionInView = s.recycleOcclusionInView;
            Scenario.recycleCooldownSeconds = s.recycleCooldownSeconds;
            Scenario.attackRangeMeters = s.attackRangeMeters;
            Scenario.attackAngleDeg = s.attackAngleDeg;
            if (Obstacle != null) Obstacle.MaxSpeed = Scenario.obstacleMaxSpeed = s.obstacleMaxSpeed;
            if (FlowStats != null) FlowStats.ringMeters = s.flowRingMeters;
        }
        public List<SimAgent> Agents { get; } = new List<SimAgent>();
        public float Time { get; private set; }
        public int Revision { get; private set; }
        public float NextCycleTime { get; private set; }
        public bool AutoPlan { get; set; } = true;
        public bool PlannerBusy => false;
        public CycleResult LastResult { get; private set; }
        public List<CycleMetrics> MetricsHistory { get; } = new List<CycleMetrics>();
        public int StaleResults { get; private set; }
        public int FailedCycles { get; private set; }
        public int PublishedCycles { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public int IdleClip { get; private set; } = -1;
        public List<string> EventLog { get; } = new List<string>();
        public float TotalGoalsReached { get; private set; }
        public double LastPlannerWallMilliseconds { get; private set; }
        public double LastPlannerLatencyMilliseconds { get; private set; }   // request -> publish, wall clock
        public float LastPlannerLatencySimSeconds { get; private set; }


        public DynamicCircle Obstacle { get; private set; }
        public int BlockedGoalEvents { get; private set; }
        public int AbandonedGoals { get; private set; }
        public int BlockedArrivals { get; private set; }
        public float BlockedWaitAgentSeconds { get; private set; }
        public float StuckAgentSeconds { get; private set; }
        public FlowStats FlowStats { get; private set; }
        public bool PlayerGoals => Scenario != null && (Scenario.goals == GoalPreset.PlayerNear || Scenario.goals == GoalPreset.PlayerFlow);
        public float FocusX => Obstacle != null ? Obstacle.X : Map.CenterX;
        public float FocusZ => Obstacle != null ? Obstacle.Z : Map.CenterZ;   // goal inside the circle and the NPC within 1 m of it
        public ObstacleStats ObstacleStats { get; private set; } = new ObstacleStats();
        public ObstaclePrediction LastPrediction { get; private set; }
        private readonly Dictionary<long, GoalField> goalFields = new Dictionary<long, GoalField>();
        public int GoalFieldCount => goalFields.Count;
        public double GoalFieldMilliseconds { get; private set; }

        /// <summary>Cost-to-go field for a goal, cached per goal cell and radius; built on the main thread when a goal is assigned.</summary>
        /// <summary>Goal fields are on for scene (polygon) maps, where goals sit behind city blocks; raster presets follow the config toggle.</summary>
        public bool UseGoalField => Config.useGoalField || Config.goalFieldOnSceneMaps && Map is PolygonWalkable;
        public int StuckGoalEvents { get; private set; }
        public int RecycleEvents { get; private set; }
        public int RecycleVisibleSpawns { get; private set; }     // spawns that were visible from the player at the moment of spawning (should stay 0)
        public int RecycleNoSpot { get; private set; }            // recycle attempts without a valid spawn spot
        public int Kills { get; private set; }
        public int Attacks { get; private set; }
        public float LastAttackTime { get; private set; } = float.NegativeInfinity;
        private float nextRecycleCheck;

        private FieldGrid FieldGridFor(float required)
        {
            var key = (long)Math.Round(required * 1000f);
            if (!fieldGrids.TryGetValue(key, out var grid))
            {
                grid = new FieldGrid(Map, required, Config.goalFieldCellMeters);
                fieldGrids[key] = grid;
                GoalFieldMilliseconds += grid.BuildMilliseconds;
            }

            return grid;
        }

        /// <summary>Field for a goal, cached per goal cell and radius; the Dijkstra is bounded by max(goalFieldMaxPathMeters, 3 x distance from `fromX/Z` + 40).</summary>
        public GoalField FieldFor(float goalX, float goalZ, float radius, float fromX = float.NaN, float fromZ = float.NaN)
        {
            if (Map == null) return null;
            // Scene maps route with extra clearance so the field avoids slots barely wider than the NPC (several NPCs jam there).
            var required = radius + Config.staticMarginMeters + (Map is PolygonWalkable ? Config.goalFieldExtraClearanceMeters : 0f);
            var grid = FieldGridFor(required);
            var key = ((long)grid.CellX(goalX) << 40) ^ ((long)grid.CellZ(goalZ) << 16) ^ (long)Math.Round(required * 1000f);
            if (!goalFields.TryGetValue(key, out var field))
            {
                var bound = Config.goalFieldMaxPathMeters;
                if (!float.IsNaN(fromX)) bound = Math.Max(bound, 3f * (float)Math.Sqrt((fromX - goalX) * (fromX - goalX) + (fromZ - goalZ) * (fromZ - goalZ)) + 40f);
                field = new GoalField(grid, goalX, goalZ, bound);
                goalFields[key] = field;
                GoalFieldMilliseconds += field.BuildMilliseconds;
                if (goalFields.Count > 256) goalFields.Clear();
            }

            return field;
        }

        /// <summary>Field maps only: the goal has a path from (x, z) that is not a huge detour. Without a field every goal counts as reachable.</summary>
        public bool GoalReachable(float x, float z, float goalX, float goalZ, float radius)
        {
            if (!UseGoalField) return true;
            var field = FieldFor(goalX, goalZ, radius, x, z);
            var path = field.PathDistanceAt(x, z);
            if (float.IsPositiveInfinity(path)) return false;
            var euclid = (float)Math.Sqrt((x - goalX) * (x - goalX) + (z - goalZ) * (z - goalZ));
            return path <= Config.reachableDetourFactor * euclid + 20f;
        }
        private System.Diagnostics.Stopwatch taskWatch;
        private float taskRequestedAt;
        private Random goalRandom;
        private readonly Dictionary<long, FieldGrid> fieldGrids = new Dictionary<long, FieldGrid>();

        public LocomotionSimulation(ClipDataset dataset, PlannerConfig config)
        {
            Dataset = dataset;
            Config = config;
        }

        /// <summary>Wait-capable clip used for the startup baseline: the manifest idle when present, else the first wait-capable node.</summary>
        public static int FindIdleClip(ClipDataset dataset)
        {
            var best = -1;
            for (var i = 0; i < dataset.Count; i++)
            {
                var c = dataset.Clips[i];
                if (!c.IsWaitCapable) continue;
                if (c.NodeId.IndexOf("Stand_Idle_01", StringComparison.OrdinalIgnoreCase) >= 0 && c.YawRateDegps == 0f) return i;
                if (best < 0 || c.TotalDisplacement < dataset.Clips[best].TotalDisplacement) best = i;
            }

            return best;
        }

        public void Reset(ScenarioSettings scenario, WalkableMap map = null)
        {
            Scenario = scenario.Clone();
            Map = map ?? ScenarioBuilder.BuildMap(Scenario);
            Agents.Clear();
            goalFields.Clear();
            fieldGrids.Clear();
            GoalFieldMilliseconds = 0;
            StuckGoalEvents = 0;
            RecycleEvents = 0; RecycleVisibleSpawns = 0; RecycleNoSpot = 0; nextRecycleCheck = 0f;
            Kills = 0; Attacks = 0; LastAttackTime = float.NegativeInfinity;
            MetricsHistory.Clear();
            EventLog.Clear();
            LastResult = null;

            Time = 0f;
            Revision = 0;
            NextCycleTime = 0f;
            StaleResults = 0;
            FailedCycles = 0;
            PublishedCycles = 0;
            TotalGoalsReached = 0;
            goalRandom = new Random(Scenario.seed * 31 + 7);
            Obstacle = null;
            ObstacleStats = new ObstacleStats();
            LastPrediction = null;
            BlockedGoalEvents = 0; AbandonedGoals = 0; BlockedArrivals = 0; BlockedWaitAgentSeconds = 0f; StuckAgentSeconds = 0f;
            FlowStats = new FlowStats { ringMeters = Scenario.flowRingMeters };
            if (Scenario.dynamicObstacle)
            {
                // Start at the map centre (nudged to clearance); NPC starts are kept away from it below.
                var ox = Map.CenterX; var oz = Map.CenterZ;
                ScenarioBuilder.NudgeToClearance(Map, Scenario.obstacleRadius + 0.2f, ref ox, ref oz);
                Obstacle = new DynamicCircle(Scenario.obstacleSeed, ox, oz, Scenario.obstacleRadius, Scenario.obstacleMaxSpeed);
            }

            IdleClip = FindIdleClip(Dataset);
            if (IdleClip < 0) { Status = "InvalidInitialScenario: no wait-capable clip in the dataset"; return; }
            Agents.AddRange(ScenarioBuilder.BuildAgents(Scenario, Map, Config));
            if (Obstacle != null)
            {
                foreach (var agent in Agents)
                {
                    // Push NPCs that start inside the obstacle radially outwards.
                    var dx = agent.Start.X - Obstacle.X; var dz = agent.Start.Z - Obstacle.Z;
                    var d = (float)Math.Sqrt(dx * dx + dz * dz);
                    var need = Obstacle.Radius + agent.Radius + 0.3f;
                    if (d < need)
                    {
                        var ux = d > 1e-3f ? dx / d : 1f; var uz = d > 1e-3f ? dz / d : 0f;
                        var x = Obstacle.X + ux * need; var z = Obstacle.Z + uz * need;
                        ScenarioBuilder.NudgeToClearance(Map, agent.Radius + Config.staticMarginMeters + 0.1f, ref x, ref z);
                        agent.Start = new Pose2(x, z, agent.Start.YawDeg);
                        agent.Pose = agent.Start;
                    }
                }
            }
            if (PlayerGoals) foreach (var agent in Agents) AssignNextGoal(agent, "initial goal", true);
            else if (UseGoalField)
            {
                // Preset goals (crossing, corridor ends, random) can sit behind a city block: replace those without a path.
                foreach (var agent in Agents)
                    if (!GoalReachable(agent.Start.X, agent.Start.Z, agent.GoalX, agent.GoalZ, agent.Radius)) AssignNextGoal(agent, "initial goal unreachable");
            }
            // Startup baseline (Section 10.5): every agent idles in place for the horizon; validated below.
            foreach (var agent in Agents)
            {
                var plan = new AgentPlan { AgentId = agent.Id, Revision = 0, GeneratedAt = 0f, CommitUntil = 0f, Status = "baseline" };
                plan.Segments.Add(new PlanSegment(IdleClip, 0f, Dataset.Clips[IdleClip].Duration, agent.Start));
                AgentPlan.FillStationary(Dataset, plan.Segments, Config.planningHorizonSeconds + 1f);
                agent.Plan = plan;
            }

            var report = PlanValidator.Validate(Dataset, Map, Config, Snapshot().Agents, 0f);
            Status = report.controlledCollisions == 0 && report.staticViolations == 0
                ? $"scenario ready: {Agents.Count} NPC, map {Map.Description}, idle {Dataset.Clips[IdleClip].DisplayName}"
                : $"InvalidInitialScenario: {report.controlledCollisions} overlaps, {report.staticViolations} static violations";
            EventLog.Add(Status);
            UpdateAgents();
        }

        public PlanningSnapshot Snapshot()
        {
            var snapshot = new PlanningSnapshot { Now = Time, Revision = Revision + 1, PlayerMaxSpeed = Scenario.obstacleMaxSpeed };
            if (Obstacle != null)
            {
                LastPrediction = ObstaclePrediction.Build(0, Obstacle, Scenario.obstaclePrediction, Scenario.obstaclePredictionSeconds, Time, Config.planningHorizonSeconds + 12f, Config.collisionSampleHz, Map);
                snapshot.Obstacles.Add(LastPrediction);
            }

            foreach (var agent in Agents)
            {
                var plan = agent.Plan.Clone();
                // Drop segments that already ended (keep the one containing now) so plans do not grow without bound.
                var keepFrom = plan.SegmentIndexAt(Time);
                if (keepFrom > 0) plan.Segments.RemoveRange(0, keepFrom);
                snapshot.Agents.Add(new AgentSnapshot { Id = agent.Id, Radius = agent.Radius, GoalX = agent.PlanGoalX, GoalZ = agent.PlanGoalZ, Plan = plan, Field = UseGoalField ? FieldFor(agent.PlanGoalX, agent.PlanGoalZ, agent.Radius, agent.Pose.X, agent.Pose.Z) : null, NextReplanTime = agent.NextReplanTime, GoalChanged = agent.GoalRevision != agent.PlannedGoalRevision });
            }

            return snapshot;
        }

        public void Tick(float dt)
        {
            if (Agents.Count == 0) return;
            Time += dt;
            if (Obstacle != null)
            {
                if (!ExternalPlayer) Obstacle.Tick(dt, Time, Map);
                UpdateAgents();
                var nearest = float.PositiveInfinity; var nearestGap = float.PositiveInfinity;
                foreach (var agent in Agents)
                {
                    var d = agent.Pose.DistanceTo(Obstacle.X, Obstacle.Z);
                    if (d < nearest) nearest = d;
                    var gap = d - Obstacle.Radius - agent.Radius;
                    if (gap < nearestGap) nearestGap = gap;
                    if (gap < 1f && agent.CurrentClip >= 0 && Dataset.Clips[agent.CurrentClip].IsStationary) ObstacleStats.loiterAgentSeconds += dt;
                }

                if (!ExternalPlayer) ObstacleStats.Record(dt, nearest, nearestGap);
                UpdateBlockedGoals(dt);
            }
            else UpdateAgents();

            if (!ExternalPlayer && (Obstacle != null || PlayerGoals)) FlowStats.Record(dt, Agents, Dataset, FocusX, FocusZ);
            if (Scenario.goals == GoalPreset.PlayerFlow) UpdateFlowGoals();
            if (Scenario.recycleEnabled && Obstacle != null && PlayerGoals && Time >= nextRecycleCheck) { nextRecycleCheck = Time + 0.5f; UpdateRecycling(); }
            CheckGoals();
            if (AutoPlan && Time >= NextCycleTime) StartCycle();
        }

        // The host owns scheduling. This type is confined to the dedicated planning worker.
        public void StartCycle()
        {
            var snapshot = Snapshot();
            taskRequestedAt = Time;
            taskWatch = System.Diagnostics.Stopwatch.StartNew();
            NextCycleTime = Time + 1f / Math.Max(0.1f, Config.planningHz);
            Publish(LocomotionPlanner.RunCycle(Dataset, Map, Config.Clone(), snapshot));
        }

        private void Publish(CycleResult result)
        {
            LastPlannerWallMilliseconds = result.Metrics.wallMilliseconds;
            LastPlannerLatencyMilliseconds = taskWatch?.Elapsed.TotalMilliseconds ?? result.Metrics.wallMilliseconds;
            LastPlannerLatencySimSeconds = Time - taskRequestedAt;
            LastResult = result;
            MetricsHistory.Add(result.Metrics);
            if (MetricsHistory.Count > 2000) MetricsHistory.RemoveAt(0);
            if (!result.Published)
            {
                FailedCycles++;
                Status = result.Status;
                EventLog.Add($"t={Time:F1} r{result.Revision} not published: {result.Status}");
                return;
            }

            if (result.MinCommitUntil < Time)
            {
                StaleResults++;
                Status = $"stale result r{result.Revision}: boundary {result.MinCommitUntil:F2} < now {Time:F2}; old plans kept";
                EventLog.Add($"t={Time:F1} {Status}");
                result.Metrics.status += " STALE";
                return;
            }

            foreach (var agent in Agents)
            {
                // A cycle computed from a snapshot taken before this agent was teleported (recycle / respawn) plans from its old
                // position: keep the idle baseline at the new spot and let the next cycle plan from there.
                if (agent.LastRecycleTime > taskRequestedAt) continue;
                if (result.NewPlans.TryGetValue(agent.Id, out var plan)) agent.Plan = plan;
                var info = result.Agents.Find(i => i.AgentId == agent.Id);
                if (info != null) { agent.NextReplanTime = info.NextReplanTime; agent.Tier = info.Tier; if (!info.LodSkipped) agent.PlannedGoalRevision = agent.GoalRevision; }
            }

            Revision = result.Revision;
            PublishedCycles++;
            Status = result.Status + $" ({result.Metrics.wallMilliseconds:F0} ms)";
        }

        private void UpdateAgents()
        {
            foreach (var agent in Agents)
            {
                var plan = agent.Plan;
                var index = plan.SegmentIndexAt(Time);
                if (index < 0) continue;
                var segment = plan.Segments[index];
                var local = Math.Max(0f, Math.Min(Time - segment.StartTime, segment.Duration));
                var pose = segment.WorldStart.Compose(Dataset.LocalPoseAt(segment.Clip, local));
                var moved = Pose2.Distance(pose, agent.Pose);
                agent.TravelDistance += moved;
                agent.Pose = pose;
                agent.CurrentClip = segment.Clip;
                agent.ClipLocalTime = local;
            }
        }

        private void CheckGoals()
        {
            if (!Scenario.reassignGoals) return;
            foreach (var agent in Agents)
            {
                // Mode 1: while the goal is blocked, standing at the ring point counts as arriving.
                var arriveAtRing = agent.GoalBlocked && Config.blockedGoalMode == 1;
                var distance = arriveAtRing ? agent.Pose.DistanceTo(agent.StandoffX, agent.StandoffZ) : agent.Pose.DistanceTo(agent.GoalX, agent.GoalZ);
                if (agent.FlowPhase == 1)
                {
                    // Pass waypoint: switch to the far goal early and without stopping (also when the NPC already went past it).
                    var ahead = (agent.GoalX - agent.Pose.X) * agent.FlowDirX + (agent.GoalZ - agent.Pose.Z) * agent.FlowDirZ;
                    if (distance > Config.flowSwitchMeters && ahead > 0f) continue;
                    agent.PassCount++;
                    AssignFarGoal(agent);
                    continue;
                }

                if (distance > Config.goalRadiusMeters)
                {
                    // Stuck detection (field maps only, where progress is measured along a path): the goal distance has to improve by
                    // 0.5 m every stuckGoalSeconds, else the goal is replaced. Off on the raster presets: waiting in a corridor is legitimate.
                    // Progress is measured along the field when there is one (a detour around a block moves away from the goal in a straight line).
                    var progressDistance = UseGoalField ? FieldFor(agent.PlanGoalX, agent.PlanGoalZ, agent.Radius, agent.Pose.X, agent.Pose.Z).DistanceAt(agent.Pose.X, agent.Pose.Z) : distance;
                    if (agent.ProgressRevision != agent.GoalRevision) { agent.ProgressRevision = agent.GoalRevision; agent.BestGoalDistance = progressDistance; agent.LastProgressTime = Time; }
                    else if (progressDistance < agent.BestGoalDistance - 0.5f) { agent.BestGoalDistance = progressDistance; agent.LastProgressTime = Time; }
                    else if (Config.stuckGoalSeconds > 0f && UseGoalField && !agent.GoalBlocked && progressDistance > Config.stuckGoalMinMeters && Time - agent.LastProgressTime >= Config.stuckGoalSeconds)
                    {
                        agent.StuckGoals++;
                        StuckGoalEvents++;
                        AssignNextGoal(agent, $"stuck {Time - agent.LastProgressTime:F0} s at {distance:F1} m from the goal");
                    }

                    continue;
                }

                if (agent.CurrentClip < 0 || !Dataset.Clips[agent.CurrentClip].IsStationary) continue;
                agent.GoalsReached++;
                TotalGoalsReached++;
                if (arriveAtRing) BlockedArrivals++;
                agent.GoalTimes.Add(Time - agent.LastGoalTime);
                if (agent.GoalTimes.Count > 256) agent.GoalTimes.RemoveAt(0);
                AssignNextGoal(agent, $"reached goal #{agent.GoalsReached}{(arriveAtRing ? " (blocked, at ring)" : string.Empty)}");
            }
        }

        private void AssignNextGoal(SimAgent agent, string reason, bool initial = false)
        {
            agent.LastGoalTime = Time;
            var required = agent.Radius + Config.staticMarginMeters + 0.1f;
            float gx, gz;
            agent.FlowPhase = 0;
            if (Scenario.goals == GoalPreset.PlayerFlow)
            {
                if (AssignPassWaypoint(agent, initial)) { agent.GoalBlocked = false; agent.BlockedWaitSeconds = 0f; agent.GoalRevision++; EventLog.Add($"t={Time:F1} agent {agent.Id} {reason}; pass waypoint ({agent.GoalX:F1}, {agent.GoalZ:F1}) offset {agent.FlowOffset:F1}"); return; }
                AssignFarGoal(agent);
                return;
            }

            if (Scenario.goals == GoalPreset.PlayerNear && TryPointInRing(FocusX, FocusZ, Scenario.flowRingMeters, required, out gx, out gz))
            {
                // Naive density: goal uniformly inside the ring (outside the circle itself).
            }
            else if (Obstacle != null && Scenario.goalNearObstacleProbability > 0f && goalRandom.NextDouble() < Scenario.goalNearObstacleProbability)
            {
                // Test knob: a goal inside the player circle (uniform over the disc).
                var angle = goalRandom.NextDouble() * Math.PI * 2.0;
                var rho = Obstacle.Radius * Math.Sqrt(goalRandom.NextDouble());
                gx = Obstacle.X + (float)(Math.Cos(angle) * rho); gz = Obstacle.Z + (float)(Math.Sin(angle) * rho);
                ScenarioBuilder.NudgeToClearance(Map, required, ref gx, ref gz);
            }
            else PickReachableGoal(agent, required, Config.minGoalDistanceMeters, agent.Pose.X, agent.Pose.Z, out gx, out gz);
            agent.GoalX = gx; agent.GoalZ = gz;
            agent.GoalBlocked = false;
            agent.BlockedWaitSeconds = 0f;
            agent.GoalRevision++;
            EventLog.Add($"t={Time:F1} agent {agent.Id} {reason}; next ({gx:F1}, {gz:F1})");
        }

        /// <summary>Random goal at least minDistance from (fromX, fromZ) with a path from the agent (field maps); last attempt is kept regardless.</summary>
        private void PickReachableGoal(SimAgent agent, float required, float minDistance, float fromX, float fromZ, out float gx, out float gz)
        {
            gx = fromX; gz = fromZ;
            for (var attempt = 0; attempt < 12; attempt++)
            {
                ScenarioBuilder.PickGoal(Map, goalRandom, required, minDistance, fromX, fromZ, out gx, out gz);
                if (GoalReachable(agent.Pose.X, agent.Pose.Z, gx, gz, agent.Radius)) return;
            }
        }

        // ---- recycling: teleport NPCs left far behind to an unseen spot near the player (simulation layer, planner unchanged) ----

        /// <summary>Whether the player (heading HeadingDeg, field of view recycleViewDeg, range recycleViewRangeMeters) can see a point.</summary>
        public bool PlayerSees(float x, float z)
        {
            if (Obstacle == null) return false;
            var dx = x - Obstacle.X; var dz = z - Obstacle.Z;
            var d = (float)Math.Sqrt(dx * dx + dz * dz);
            if (d > Scenario.recycleViewRangeMeters) return false;
            if (d < 1e-3f) return true;
            var bearing = (float)Math.Atan2(dx, dz) * Pose2.Rad2Deg;
            if (Math.Abs(Pose2.WrapDeg(bearing - (ExternalPlayer ? ViewYaw : Obstacle.HeadingDeg))) > Scenario.recycleViewDeg * 0.5f) return false;
            return !Map.SegmentBlocked(Obstacle.X, Obstacle.Z, x, z);
        }

        private void UpdateRecycling()
        {
            var required = Config.npcRadiusMeters + Config.staticMarginMeters + 0.3f;
            foreach (var agent in Agents)
            {
                if (Time - agent.LastRecycleTime < Scenario.recycleCooldownSeconds) continue;
                if (agent.Pose.DistanceTo(Obstacle.X, Obstacle.Z) <= Scenario.recycleDistanceMeters) continue;
                if (PlayerSees(agent.Pose.X, agent.Pose.Z)) continue;   // wait until the player looks away or a building hides it
                if (!TryFindSpawn(agent, required, out var x, out var z)) { RecycleNoSpot++; agent.LastRecycleTime = Time; continue; }
                Teleport(agent, x, z, "recycled");
                return;   // one per check so the planner absorbs them one at a time
            }
        }

        /// <summary>
        /// Spawn spot: in the distance band around the player, outside the view (or occluded when allowed), on walkable ground with
        /// clearance, inside the scenario window, clear of other NPCs, with a path to the player; the angular sector holding the
        /// fewest NPCs is tried first so the crowd stays balanced around the player.
        /// </summary>
        private bool TryFindSpawn(SimAgent agent, float required, out float x, out float z)
        {
            const int sectors = 8;
            var counts = new int[sectors];
            foreach (var other in Agents)
            {
                if (other == agent) continue;
                var dx = other.Pose.X - Obstacle.X; var dz = other.Pose.Z - Obstacle.Z;
                if (dx * dx + dz * dz > Scenario.recycleDistanceMeters * Scenario.recycleDistanceMeters) continue;
                var relative = Pose2.WrapDeg((float)Math.Atan2(dx, dz) * Pose2.Rad2Deg - (ExternalPlayer ? ViewYaw : Obstacle.HeadingDeg));
                counts[((int)Math.Floor((relative + 180f) / (360f / sectors))) % sectors]++;
            }

            var order = new int[sectors];
            for (var i = 0; i < sectors; i++) order[i] = i;
            Array.Sort(order, (a, b) => counts[a] != counts[b] ? counts[a].CompareTo(counts[b]) : a.CompareTo(b));
            var min = Math.Max(Obstacle.Radius + agent.Radius + Config.dynamicKeepMeters + 1f, Scenario.recycleSpawnMinMeters);
            var max = Math.Max(min + 1f, Scenario.recycleSpawnMaxMeters);
            foreach (var sector in order)
            {
                var centre = -180f + (sector + 0.5f) * (360f / sectors);
                for (var attempt = 0; attempt < 12; attempt++)
                {
                    var relative = centre + (float)(goalRandom.NextDouble() - 0.5) * (360f / sectors);
                    var angle = ((ExternalPlayer ? ViewYaw : Obstacle.HeadingDeg) + relative) * Pose2.Deg2Rad;
                    var rho = min + (float)goalRandom.NextDouble() * (max - min);
                    x = Obstacle.X + (float)Math.Sin(angle) * rho; z = Obstacle.Z + (float)Math.Cos(angle) * rho;
                    if (x < Map.SampleOriginX || x > Map.SampleOriginX + Map.SampleWidth || z < Map.SampleOriginZ || z > Map.SampleOriginZ + Map.SampleHeight) continue;
                    if (Map.ClearanceAt(x, z) < required) continue;
                    var inView = Math.Abs(Pose2.WrapDeg(relative)) <= Scenario.recycleViewDeg * 0.5f;
                    if (inView && (!Scenario.recycleOcclusionInView || !Map.SegmentBlocked(Obstacle.X, Obstacle.Z, x, z))) continue;
                    if (TooCloseNow(agent, x, z, 2f)) continue;
                    if (!GoalReachable(x, z, FocusX, FocusZ, agent.Radius)) continue;
                    return true;
                }
            }

            x = 0f; z = 0f;
            return false;
        }

        /// <summary>Whether another NPC's current pose (or its plan within the next 2 s) is within `gap` of the point; TooClose in ScenarioBuilder compares initial starts only.</summary>
        private bool TooCloseNow(SimAgent self, float x, float z, float gap)
        {
            foreach (var other in Agents)
            {
                if (other == self) continue;
                if (other.Pose.DistanceTo(x, z) < gap) return true;
                var plan = other.Plan;
                if (plan == null) continue;
                for (var t = Time; t <= Time + 2f; t += 0.5f)
                {
                    var index = plan.SegmentIndexAt(t);
                    if (index < 0) continue;
                    var segment = plan.Segments[index];
                    var pose = segment.WorldStart.Compose(Dataset.LocalPoseAt(segment.Clip, Math.Max(0f, Math.Min(t - segment.StartTime, segment.Duration))));
                    if (pose.DistanceTo(x, z) < gap) return true;
                }
            }

            return false;
        }

        /// <summary>Moves an agent: idle baseline plan at the new pose from now, goal state cleared, a fresh player goal assigned.</summary>
        public void Teleport(SimAgent agent, float x, float z, string reason)
        {
            var yaw = (float)Math.Atan2(FocusX - x, FocusZ - z) * Pose2.Rad2Deg;
            var visible = PlayerSees(x, z);
            agent.Start = new Pose2(x, z, yaw);
            agent.Pose = agent.Start;
            var plan = new AgentPlan { AgentId = agent.Id, Revision = agent.Plan != null ? agent.Plan.Revision + 1 : 0, GeneratedAt = Time, CommitUntil = Time, Status = reason };
            plan.Segments.Add(new PlanSegment(IdleClip, Time, Time + Dataset.Clips[IdleClip].Duration, agent.Start));
            AgentPlan.FillStationary(Dataset, plan.Segments, Time + Config.planningHorizonSeconds + 1f);
            agent.Plan = plan;
            agent.CurrentClip = IdleClip; agent.ClipLocalTime = 0f;
            agent.FlowPhase = 0; agent.FlowDirX = 0f; agent.FlowDirZ = 0f;
            agent.GoalBlocked = false; agent.BlockedWaitSeconds = 0f;
            agent.ProgressRevision = -1;
            agent.LastRecycleTime = Time;
            agent.Recycles++;
            RecycleEvents++;
            if (visible) RecycleVisibleSpawns++;
            AssignNextGoal(agent, reason + (visible ? " (visible!)" : string.Empty), true);
        }

        private bool TryPointInRing(float cx, float cz, float ring, float required, out float x, out float z)
        {
            var keepOut = Obstacle != null ? Obstacle.Radius + Config.npcRadiusMeters + Config.dynamicKeepMeters + 0.5f : 0f;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var angle = goalRandom.NextDouble() * Math.PI * 2.0;
                var rho = ring * Math.Sqrt(goalRandom.NextDouble());
                if (rho < keepOut) continue;
                x = cx + (float)(Math.Sin(angle) * rho); z = cz + (float)(Math.Cos(angle) * rho);
                if (Map.ClearanceAt(x, z) >= required + 0.3f) return true;
            }

            x = 0f; z = 0f;
            return false;
        }

        /// <summary>PlayerFlow phase 1: waypoint beside the player, offset perpendicular to the approach direction. False if the NPC is already next to the player.</summary>
        private bool AssignPassWaypoint(SimAgent agent, bool initial)
        {
            var keepOut = Obstacle != null ? Obstacle.Radius + agent.Radius + Config.dynamicKeepMeters + 0.5f : 1f;
            var min = Math.Max(keepOut, Scenario.flowPassOffsetMinMeters);
            var max = Math.Max(min, Scenario.flowPassOffsetMaxMeters);
            var offset = min + (float)goalRandom.NextDouble() * (max - min);
            if (goalRandom.NextDouble() < 0.5) offset = -offset;
            var toPlayer = agent.Pose.DistanceTo(FocusX, FocusZ);
            if (toPlayer < Math.Abs(offset) + Config.flowSwitchMeters + 1f) return false;
            agent.FlowOffset = offset;
            agent.FlowPhase = 1;
            agent.FlowDirX = 0f; agent.FlowDirZ = 0f;
            PlaceWaypoint(agent, out agent.GoalX, out agent.GoalZ, out agent.FlowDirX, out agent.FlowDirZ);
            return true;
        }

        private void PlaceWaypoint(SimAgent agent, out float wx, out float wz, out float dirX, out float dirZ)
        {
            // The approach direction is refreshed only while the NPC is still far away; near the player it is frozen, otherwise the
            // waypoint (perpendicular to the current bearing) would keep rotating away and could never be reached.
            dirX = FocusX - agent.Pose.X; dirZ = FocusZ - agent.Pose.Z;
            var d = (float)Math.Sqrt(dirX * dirX + dirZ * dirZ);
            var freezeAt = Math.Abs(agent.FlowOffset) + Config.flowSwitchMeters + 4f;
            if (d < freezeAt && (agent.FlowDirX != 0f || agent.FlowDirZ != 0f)) { dirX = agent.FlowDirX; dirZ = agent.FlowDirZ; }
            else
            {
                if (d < 1e-3f) { dirX = 1f; dirZ = 0f; d = 1f; }
                dirX /= d; dirZ /= d;
            }

            wx = FocusX + dirZ * agent.FlowOffset; wz = FocusZ - dirX * agent.FlowOffset;   // left-hand perpendicular
            ScenarioBuilder.NudgeToClearance(Map, agent.Radius + Config.staticMarginMeters + 0.1f, ref wx, ref wz);
        }

        /// <summary>PlayerFlow phase 2: far goal beyond the player within a cone of the pass direction; falls back to a random far goal.</summary>
        private void AssignFarGoal(SimAgent agent)
        {
            agent.FlowPhase = 2;
            agent.GoalBlocked = false; agent.BlockedWaitSeconds = 0f;
            var required = agent.Radius + Config.staticMarginMeters + 0.1f;
            // Direction of the pass; an NPC that never had a pass waypoint (it started next to the player) walks away from the
            // player instead, and a random direction when it stands on the player, so a group does not share one far goal.
            var dirX = agent.FlowDirX; var dirZ = agent.FlowDirZ;
            if (dirX == 0f && dirZ == 0f) { dirX = agent.Pose.X - FocusX; dirZ = agent.Pose.Z - FocusZ; }
            var baseAngle = dirX == 0f && dirZ == 0f ? goalRandom.NextDouble() * Math.PI * 2.0 : Math.Atan2(dirX, dirZ);
            var cone = Scenario.flowFarGoalConeDeg * Pose2.Deg2Rad;
            var far = Scenario.flowFarGoalMeters;
            for (var attempt = 0; attempt < 24; attempt++)
            {
                var spread = attempt < 12 ? cone : Math.PI * 0.75;   // widen when the map edge is in the way
                var angle = baseAngle + (goalRandom.NextDouble() * 2.0 - 1.0) * spread;
                var dist = far * (0.9f + 0.2f * (float)goalRandom.NextDouble());
                var gx = FocusX + (float)(Math.Sin(angle) * dist); var gz = FocusZ + (float)(Math.Cos(angle) * dist);
                if (Map.ClearanceAt(gx, gz) < required + 0.3f) continue;
                if (!GoalReachable(agent.Pose.X, agent.Pose.Z, gx, gz, agent.Radius)) continue;
                agent.GoalX = gx; agent.GoalZ = gz; agent.GoalRevision++;
                EventLog.Add($"t={Time:F1} agent {agent.Id} passed the player (#{agent.PassCount}); far goal ({gx:F1}, {gz:F1})");
                return;
            }

            PickReachableGoal(agent, required, Math.Max(Config.minGoalDistanceMeters, far * 0.5f), FocusX, FocusZ, out var fx, out var fz);
            agent.GoalX = fx; agent.GoalZ = fz; agent.GoalRevision++;
            EventLog.Add($"t={Time:F1} agent {agent.Id} passed the player (#{agent.PassCount}); random far goal ({fx:F1}, {fz:F1})");
        }

        /// <summary>The pass waypoint follows the player in flowWaypointStepMeters steps (each step is a goal change for the planner).</summary>
        private void UpdateFlowGoals()
        {
            foreach (var agent in Agents)
            {
                if (agent.FlowPhase != 1) continue;
                PlaceWaypoint(agent, out var wx, out var wz, out var dx, out var dz);
                var step = Config.flowWaypointStepMeters;
                if ((wx - agent.GoalX) * (wx - agent.GoalX) + (wz - agent.GoalZ) * (wz - agent.GoalZ) < step * step) continue;
                agent.GoalX = wx; agent.GoalZ = wz; agent.FlowDirX = dx; agent.FlowDirZ = dz;
                agent.GoalRevision++;
            }
        }

        /// <summary>
        /// Goal inside the external circle: the planner's soft obstacle cost and the progress cost balance at the ring edge, so the
        /// NPC would idle against the player forever. While blocked the planner is given a standoff point on the NPC's side instead
        /// (mode 2) or the ring point itself (mode 1); the real goal comes back when the circle has moved away. Waiting near the
        /// standoff point for blockedGoalAbandonSeconds abandons the goal (here: a new one; in a game the goal owner decides).
        /// </summary>
        private void UpdateBlockedGoals(float dt)
        {
            if (Obstacle == null) return;
            // Mode-independent measure of the problem: agent-seconds spent within 1 m of the circle while the goal is inside it.
            foreach (var agent in Agents)
            {
                var reach = Obstacle.Radius + agent.Radius + Config.dynamicKeepMeters - Config.goalRadiusMeters + 0.2f;
                var gd = (agent.GoalX - Obstacle.X) * (agent.GoalX - Obstacle.X) + (agent.GoalZ - Obstacle.Z) * (agent.GoalZ - Obstacle.Z);
                if (gd < reach * reach && agent.Pose.DistanceTo(Obstacle.X, Obstacle.Z) - Obstacle.Radius - agent.Radius < 1f) StuckAgentSeconds += dt;
            }

            if (Config.blockedGoalMode == 0)
            {
                foreach (var agent in Agents) if (agent.GoalBlocked) { agent.GoalBlocked = false; agent.GoalRevision++; }
                return;
            }

            foreach (var agent in Agents)
            {
                var inflate = Obstacle.Radius + agent.Radius + Config.dynamicKeepMeters;
                // Blocked when arriving (within the goal radius, stationary) is impossible without standing in the cost zone.
                var blockedDistance = inflate - Config.goalRadiusMeters + 0.2f;
                var goalDistance = (float)Math.Sqrt((agent.GoalX - Obstacle.X) * (agent.GoalX - Obstacle.X) + (agent.GoalZ - Obstacle.Z) * (agent.GoalZ - Obstacle.Z));
                var blocked = agent.GoalBlocked ? goalDistance < blockedDistance + Config.blockedGoalHysteresisMeters : goalDistance < blockedDistance;
                if (!blocked)
                {
                    if (agent.GoalBlocked)
                    {
                        agent.GoalBlocked = false; agent.BlockedWaitSeconds = 0f; agent.GoalRevision++;
                        EventLog.Add($"t={Time:F1} agent {agent.Id} goal free again");
                    }

                    continue;
                }

                // Standoff point on the NPC's side of the circle, so nobody is sent around the player.
                var dx = agent.Pose.X - Obstacle.X; var dz = agent.Pose.Z - Obstacle.Z;
                var d = (float)Math.Sqrt(dx * dx + dz * dz);
                if (d < 1e-3f) { dx = 1f; dz = 0f; d = 1f; }
                var ring = inflate + (Config.blockedGoalMode == 1 ? 0.05f : Config.blockedGoalStandoffMeters);
                var sx = Obstacle.X + dx / d * ring; var sz = Obstacle.Z + dz / d * ring;
                ScenarioBuilder.NudgeToClearance(Map, agent.Radius + Config.staticMarginMeters + 0.1f, ref sx, ref sz);
                if (!agent.GoalBlocked)
                {
                    agent.GoalBlocked = true; agent.StandoffX = sx; agent.StandoffZ = sz; agent.BlockedWaitSeconds = 0f; agent.GoalRevision++;
                    BlockedGoalEvents++;
                    EventLog.Add($"t={Time:F1} agent {agent.Id} goal blocked by the obstacle; standoff ({sx:F1}, {sz:F1})");
                }
                else if ((sx - agent.StandoffX) * (sx - agent.StandoffX) + (sz - agent.StandoffZ) * (sz - agent.StandoffZ) > 0.5f * 0.5f)
                {
                    // The circle (or the NPC) moved: follow it, but only in 0.5 m steps so the goal does not change every tick.
                    agent.StandoffX = sx; agent.StandoffZ = sz; agent.GoalRevision++;
                }

                if (agent.Pose.DistanceTo(agent.StandoffX, agent.StandoffZ) <= 1.5f)
                {
                    agent.BlockedWaitSeconds += dt;
                    BlockedWaitAgentSeconds += dt;
                    if (Scenario.reassignGoals && Config.blockedGoalAbandonSeconds > 0f && agent.BlockedWaitSeconds >= Config.blockedGoalAbandonSeconds)
                    {
                        agent.GoalsAbandoned++;
                        AbandonedGoals++;
                        AssignNextGoal(agent, $"abandoned a blocked goal after {agent.BlockedWaitSeconds:F1} s");
                    }
                }
            }
        }

        /// <summary>Re-checks the currently published plans from now with the offline validator.</summary>
        public PlanValidator.Report ValidatePublished() => PlanValidator.Validate(Dataset, Map, Config, Snapshot().Agents, Time);

        public static float Percentile(List<double> values, double p)
        {
            if (values.Count == 0) return 0f;
            var sorted = new List<double>(values);
            sorted.Sort();
            var index = (int)Math.Ceiling(p * sorted.Count) - 1;
            return (float)sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }
    }
}
