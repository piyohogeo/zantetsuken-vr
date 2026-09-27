using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Zantetsu.Core.MobPlan
{
    public sealed class AgentSnapshot
    {
        public int Id;
        public float Radius;
        public float GoalX;
        public float GoalZ;
        public AgentPlan Plan;
        /// <summary>Cost-to-go field of this agent's goal (null = straight-line distance).</summary>
        public GoalField Field;
        /// <summary>LOD state carried between cycles: earliest time this agent wants a new search, and whether its goal changed since its plan.</summary>
        public float NextReplanTime;
        public bool GoalChanged;
    }

    /// <summary>Immutable input of one planning cycle (Section 14.1). Plans are cloned; dataset and map are read-only.</summary>
    public sealed class PlanningSnapshot
    {
        public float Now;
        public int Revision;
        public List<AgentSnapshot> Agents = new List<AgentSnapshot>();
        public List<ObstaclePrediction> Obstacles = new List<ObstaclePrediction>();
        public float PlayerMaxSpeed = 1f;
    }

    public sealed class AgentCycleInfo
    {
        public int AgentId;
        public float Boundary;                 // actual freeze boundary (first clip end >= now + commit)
        public int PrefixCount;
        public int StartClip;
        public Pose2 StartPose;
        public List<PlanCandidate> Candidates = new List<PlanCandidate>();
        public int Chosen;
        public SearchStats Stats = new SearchStats();
        public string Status = string.Empty;
        public bool Changed;                   // chosen != old suffix
        public int Tier;                        // 0 near, 1 mid, 2 far (distance from the player)
        public float DistanceToPlayer = float.PositiveInfinity;
        public float NextReplanTime;            // written back to the agent on publish
        public float CommitSeconds;             // commit target used for this agent (tier / player reach)
        public bool LodSkipped;
    }

    [Serializable]
    public sealed class CycleMetrics
    {
        public int revision;
        public float now;
        public int agentCount;
        public double wallMilliseconds;
        public double candidateMilliseconds;
        public double compatibilityMilliseconds;
        public double assignmentMilliseconds;
        public int expansions;
        public int successorsSeen;
        public int successorsEvaluated;
        public int idleExitsPruned;
        public int staticRejects;
        public int deadEndRejects;
        public int stopTailRejects;
        public int candidatesTotal;
        public int agentsWithoutNewCandidates;
        public int searchTimeouts;
        public int agentsSkipped;         // old suffix kept without a search (reaches the goal, static-safe, conflict-free)
        public int circleAccepts;
        public int familyPruned;
        public int parametricCandidates;  // search-free wait / delay / step-away candidates added this cycle
        public int regenerationRounds;    // Section 13.4 rounds run this cycle
        public int regenerationCandidates;
        public int regenerationSuccesses; // rounds after which the regenerated agent got a better compatible plan
        public int localDeadlocks;        // agents still blocked (or without any alternative while far from the goal) after the rounds
        public int dynamicPredictedHits;  // chosen candidates that still overlap an external circle in prediction
        public float dynamicPredictedSeconds;
        public int lodNear;
        public int lodMid;
        public int lodFar;
        public int lodSkipped;            // agents that kept their plan because their tier's replan interval has not elapsed
        public int components;            // interaction components solved separately this cycle
        public int largestComponent;
        public int pairChecks;
        public int pairPrefiltered;
        public int sweepChanges;
        public int pairRepairs;
        public int blockedAgents;
        public int assignmentChanges;
        public bool baselineFeasible;
        public bool baselineRepaired;
        public bool timeout;
        public float minSeparation;
        public float actualCommitHorizonMin;
        public float actualCommitHorizonMax;
        public float plannedUntilMin;
        public string status = string.Empty;

        public static string CsvHeader => "revision,now,agents,wall_ms,cand_ms,compat_ms,assign_ms,expansions,seen,evaluated,idle_pruned,static_rejects,dead_end,stop_tail,candidates,no_new,search_timeouts,skipped,circle_accepts,family_pruned,regen_rounds,regen_candidates,regen_success,local_deadlocks,dyn_hits,dyn_seconds,lod_near,lod_mid,lod_far,lod_skipped,components,largest_component,pair_checks,pair_prefiltered,sweep_changes,pair_repairs,blocked,changes,baseline,timeout,min_sep,commit_min,commit_max,planned_until_min,status";

        public string ToCsv() => string.Join(",", revision, now.ToString("F2"), agentCount, wallMilliseconds.ToString("F1"), candidateMilliseconds.ToString("F1"), compatibilityMilliseconds.ToString("F1"), assignmentMilliseconds.ToString("F1"),
            expansions, successorsSeen, successorsEvaluated, idleExitsPruned, staticRejects, deadEndRejects, stopTailRejects, candidatesTotal, agentsWithoutNewCandidates, searchTimeouts, agentsSkipped, circleAccepts, familyPruned, regenerationRounds, regenerationCandidates, regenerationSuccesses, localDeadlocks, dynamicPredictedHits, dynamicPredictedSeconds.ToString("F1"), lodNear, lodMid, lodFar, lodSkipped, components, largestComponent, pairChecks, pairPrefiltered, sweepChanges, pairRepairs, blockedAgents, assignmentChanges,
            baselineFeasible, timeout, minSeparation.ToString("F3"), actualCommitHorizonMin.ToString("F2"), actualCommitHorizonMax.ToString("F2"), plannedUntilMin.ToString("F2"), status.Replace(',', ';'));
    }

    public sealed class CycleResult
    {
        public int Revision;
        public float Now;
        public bool Published;
        public string Status = string.Empty;
        public Dictionary<int, AgentPlan> NewPlans = new Dictionary<int, AgentPlan>();
        public List<AgentCycleInfo> Agents = new List<AgentCycleInfo>();
        public CycleMetrics Metrics = new CycleMetrics();
        public float MinCommitUntil = float.PositiveInfinity;
    }

    /// <summary>
    /// One receding-horizon planning cycle (Sections 10, 13, 14): frozen prefix per agent, candidate pool with the old
    /// suffix as candidate 0, compatibility matrix and anytime assignment, all under one wall-clock deadline.
    /// Pure C#: safe to run on a background thread with an immutable snapshot.
    /// </summary>
    public static class LocomotionPlanner
    {
        public static CycleResult RunCycle(ClipDataset dataset, WalkableMap map, PlannerConfig config, PlanningSnapshot snapshot)
        {
            var result = new CycleResult { Revision = snapshot.Revision, Now = snapshot.Now };
            var metrics = result.Metrics;
            metrics.revision = snapshot.Revision;
            metrics.now = snapshot.Now;
            metrics.agentCount = snapshot.Agents.Count;
            var deadline = new Deadline(config.cycleBudgetMilliseconds);
            var wall = Stopwatch.StartNew();
            obstacles = snapshot.Obstacles ?? new List<ObstaclePrediction>();
            var now = snapshot.Now;
            var horizonEnd = now + config.planningHorizonSeconds;
            var search = new SingleAgentSearch(dataset, map);
            var infos = new List<AgentCycleInfo>();
            var prefixes = new List<List<PlanSegment>>();
            var oldTrajectories = new List<OtherAgentTrajectory>();

            // 1. Frozen prefix and old suffix (candidate 0) per agent; old trajectories for the soft cost.
            // Player position for the distance tiers: the first external circle at `now`; without one everybody is "near".
            var hasPlayer = config.lodEnabled && obstacles.Count > 0;
            var playerX = 0f; var playerZ = 0f; var playerRadius = 0f;
            if (hasPlayer) { obstacles[0].Trajectory.PositionAt(now, out playerX, out playerZ); playerRadius = obstacles[0].Radius; }
            foreach (var agent in snapshot.Agents)
            {
                var info = new AgentCycleInfo { AgentId = agent.Id };
                var plan = agent.Plan;
                // Tier and commit: far NPCs freeze longer, but never longer than the player needs to reach them (Section 14.2 quota).
                var commit = config.commitTargetSeconds;
                if (hasPlayer)
                {
                    var current = plan.PoseAt(dataset, now);
                    info.DistanceToPlayer = current.DistanceTo(playerX, playerZ);
                    info.Tier = info.DistanceToPlayer > config.lodFarMeters ? 2 : info.DistanceToPlayer > config.lodNearMeters ? 1 : 0;
                    if (info.Tier > 0)
                    {
                        var tierCommit = info.Tier == 2 ? config.lodFarCommitSeconds : config.lodMidCommitSeconds;
                        var exclusion = agent.Radius + playerRadius + config.dynamicKeepMeters;
                        var reach = (info.DistanceToPlayer - exclusion) / Math.Max(0.1f, snapshot.PlayerMaxSpeed);
                        commit = Math.Max(config.commitTargetSeconds, Math.Min(tierCommit, reach));
                    }
                }

                if (info.Tier == 0) metrics.lodNear++; else if (info.Tier == 1) metrics.lodMid++; else metrics.lodFar++;
                info.CommitSeconds = commit;
                var freezeTarget = now + commit;
                // A plan that ran out (no cycle published for a while) is extended by its idle continuation first.
                if (plan.EndTime < freezeTarget + 0.5f) AgentPlan.FillStationary(dataset, plan.Segments, freezeTarget + 0.5f);
                var boundaryIndex = -1;
                for (var i = 0; i < plan.Segments.Count; i++)
                {
                    if (plan.Segments[i].EndTime >= freezeTarget - 1e-4f) { boundaryIndex = i; break; }
                }

                if (boundaryIndex < 0) boundaryIndex = plan.Segments.Count - 1;
                var prefix = plan.Segments.GetRange(0, boundaryIndex + 1);
                var suffix = plan.Segments.GetRange(boundaryIndex + 1, plan.Segments.Count - boundaryIndex - 1);
                var boundarySegment = plan.Segments[boundaryIndex];
                info.Boundary = boundarySegment.EndTime;
                info.PrefixCount = prefix.Count;
                info.StartClip = boundarySegment.Clip;
                info.StartPose = boundarySegment.WorldStart.Compose(dataset.Clips[boundarySegment.Clip].EndPose);
                // Candidate 0: old suffix extended by the stationary continuation up to the new horizon.
                var oldSuffix = new List<PlanSegment>(suffix);
                var oldValid = true;
                if (oldSuffix.Count == 0)
                {
                    var continuation = dataset.Clips[info.StartClip].StationaryContinuation;
                    if (continuation >= 0) oldSuffix.Add(new PlanSegment(continuation, info.Boundary, info.Boundary + dataset.Clips[continuation].Duration, info.StartPose));
                    else oldValid = false;
                }

                if (oldValid && !AgentPlan.FillStationary(dataset, oldSuffix, horizonEnd)) oldValid = false;
                var candidate0 = new PlanCandidate { Index = 0, Origin = "old", Suffix = oldSuffix, Note = oldValid ? "old suffix" : "old suffix not extendable" };
                info.Candidates.Add(candidate0);
                info.Status = oldValid ? "ok" : "InvalidBaseline";
                infos.Add(info);
                prefixes.Add(prefix);
                var full = new List<PlanSegment>(prefix);
                full.AddRange(oldSuffix);
                candidate0.Full = full;
                candidate0.Trajectory = AgentPlan.Sample(dataset, full, now, config.collisionSampleHz);
                oldTrajectories.Add(new OtherAgentTrajectory { AgentId = agent.Id, Radius = agent.Radius, Trajectory = candidate0.Trajectory });
            }

            // Old-suffix costs (for urgency and fair comparison); satisfied agents keep their suffix without a search.
            var satisfied = new bool[infos.Count];
            for (var a = 0; a < snapshot.Agents.Count; a++)
            {
                var agent = snapshot.Agents[a];
                var info = infos[a];
                var request = MakeRequest(agent, info, config, horizonEnd, null, null);
                info.Candidates[0].Cost = search.EvaluateSegments(request, info.Candidates[0].Suffix, out var staticOk);
                if (!staticOk)
                {
                    // Idle drift can push a waiting NPC into the wall margin; the old suffix stays the fallback but any safe candidate beats it.
                    info.Candidates[0].Note += " (static violation in old suffix)";
                    info.Candidates[0].Cost.Penalty = 100f;
                }

                if (info.Candidates[0].Cost.DynamicOverlapSeconds > 0f) info.Candidates[0].Cost.Dynamic += config.dynamicCollisionPenalty;
                var eventful = !staticOk || info.Candidates[0].Suffix.Count == 0 || info.Candidates[0].Cost.DynamicOverlapSeconds > 0f || agent.GoalChanged;
                var end = AgentPlan.EndPose(dataset, info.Candidates[0].Suffix);
                var reachesGoal = end.DistanceTo(agent.GoalX, agent.GoalZ) <= config.goalRadiusMeters;
                // LOD: a mid/far agent whose replan interval has not elapsed keeps its plan unless something happened
                // (goal changed, predicted hit, static violation, conflict with a neighbour, or its walking portion runs out).
                if (info.Tier > 0 && !eventful && now < agent.NextReplanTime)
                {
                    var suffix0 = info.Candidates[0].Suffix;
                    var movingUntil0 = suffix0[suffix0.Count - 1].EndTime;
                    for (var i = 0; i < suffix0.Count; i++) if (dataset.Clips[suffix0[i].Clip].IsStationary) { movingUntil0 = suffix0[i].StartTime; break; }
                    var interval = info.Tier == 2 ? config.lodFarReplanSeconds : config.lodMidReplanSeconds;
                    var runsOut = !reachesGoal && movingUntil0 - now < interval + info.CommitSeconds;
                    var quiet = true;
                    if (!runsOut)
                    {
                        var mine0 = info.Candidates[0].Trajectory;
                        foreach (var other in oldTrajectories)
                        {
                            if (other.AgentId == agent.Id) continue;
                            var threshold = agent.Radius + other.Radius + config.agentMarginMeters;
                            if (!mine0.BoundsOverlap(other.Trajectory, threshold)) continue;
                            if (CircleSweep.MinSeparation(mine0, other.Trajectory, threshold, out _) < threshold) { quiet = false; break; }
                        }
                    }

                    if (!runsOut && quiet)
                    {
                        satisfied[a] = true;
                        info.LodSkipped = true;
                        info.NextReplanTime = agent.NextReplanTime;
                        info.Status = $"lod tier {info.Tier}: plan kept until {agent.NextReplanTime:F1}";
                        metrics.lodSkipped++;
                        continue;
                    }
                }

                if (!config.skipSatisfiedAgents || eventful) continue;
                if (!reachesGoal)
                {
                    // Event-driven variant: an unobstructed walker keeps its plan while it still moves for skipMovingMinSeconds and gains ground.
                    var suffix = info.Candidates[0].Suffix;
                    var movingUntil = suffix[suffix.Count - 1].EndTime;
                    for (var i = 0; i < suffix.Count; i++) if (dataset.Clips[suffix[i].Clip].IsStationary) { movingUntil = suffix[i].StartTime; break; }
                    if (movingUntil - now < config.skipMovingMinSeconds) continue;
                    var atMoving = AgentPlan.PoseAt(dataset, suffix, movingUntil);
                    if (info.StartPose.DistanceTo(agent.GoalX, agent.GoalZ) - atMoving.DistanceTo(agent.GoalX, agent.GoalZ) < 1f) continue;
                }

                var conflict = false;
                var mine = info.Candidates[0].Trajectory;
                foreach (var other in oldTrajectories)
                {
                    if (other.AgentId == agent.Id) continue;
                    // Reaching the goal: any actual conflict disqualifies. Walking: any neighbour closer than 2r + skipProximity does (quiet neighbourhood only).
                    var threshold = agent.Radius + other.Radius + config.agentMarginMeters + (reachesGoal ? 0f : config.skipProximityMeters);
                    if (!mine.BoundsOverlap(other.Trajectory, threshold)) continue;
                    if (CircleSweep.MinSeparation(mine, other.Trajectory, threshold, out _) < threshold) { conflict = true; break; }
                }

                if (conflict) continue;
                satisfied[a] = true;
                info.Status = "satisfied (old suffix kept)";
                metrics.agentsSkipped++;
            }

            // 1b. Search-free candidates for standing agents (MAPF-style wait / delay / step-away). Cheap, always in the pool,
            // so the selector can resolve most head-on conflicts without a regeneration search.
            if (config.parametricCandidates)
            {
                for (var a = 0; a < infos.Count; a++)
                {
                    var agent = snapshot.Agents[a];
                    var info = infos[a];
                    if (satisfied[a] || !dataset.Clips[info.StartClip].IsStationary) continue;
                    var request = MakeRequest(agent, info, config, horizonEnd, null, null);
                    foreach (var suffix in ParametricSuffixes(dataset, info, horizonEnd))
                    {
                        var cost = search.EvaluateSegments(request, suffix, out var staticOk);
                        if (!staticOk) continue;
                        var candidate = new PlanCandidate { Index = info.Candidates.Count, Origin = "param", Suffix = suffix, Cost = cost, Note = dataset.Clips[suffix[0].Clip].DisplayName };
                        var full = new List<PlanSegment>(prefixes[a]);
                        full.AddRange(suffix);
                        candidate.Full = full;
                        candidate.Trajectory = AgentPlan.Sample(dataset, full, now, config.collisionSampleHz);
                        info.Candidates.Add(candidate);
                        metrics.parametricCandidates++;
                    }
                }
            }

            // 2. Candidate generation in urgency order (worst status quo first) with a shared budget (70% of the cycle).
            var order = new List<int>();
            for (var a = 0; a < infos.Count; a++) if (!satisfied[a]) order.Add(a);
            order.Sort((x, y) => infos[y].Candidates[0].TotalCost.CompareTo(infos[x].Candidates[0].TotalCost));
            var generationBudget = config.cycleBudgetMilliseconds * (config.maxRegenerationRounds > 0 ? config.candidateBudgetShare : 0.7f);
            var candidateWatch = Stopwatch.StartNew();
            var remainingAgents = order.Count;
            foreach (var a in order)
            {
                var agent = snapshot.Agents[a];
                var info = infos[a];
                var remainingBudget = generationBudget - deadline.ElapsedMilliseconds;
                var slice = Math.Max(5.0, remainingBudget / Math.Max(1, remainingAgents));
                remainingAgents--;
                var agentDeadline = new Deadline(slice);
                if (remainingBudget <= 0) { info.Status += " skipped(budget)"; metrics.searchTimeouts++; continue; }
                var others = new List<OtherAgentTrajectory>();
                foreach (var other in oldTrajectories) if (other.AgentId != agent.Id) others.Add(other);
                var oldNext = info.Candidates[0].Suffix.Count > 0 ? info.Candidates[0].Suffix[0].Clip : -1;
                var request = MakeRequest(agent, info, config, horizonEnd, others, agentDeadline);
                request.OldNextClip = oldNext;
                if (info.Tier == 1) { request.BeamWidth = config.lodMidBeam; request.MaxCandidates = Math.Max(1, config.lodMidCandidates - 1); }
                else if (info.Tier == 2) { request.BeamWidth = config.lodFarBeam; request.MaxCandidates = Math.Max(1, config.lodFarCandidates - 1); }
                var candidates = search.Run(request, out var stats);
                info.Stats = stats;
                if (stats.Timeout) metrics.searchTimeouts++;
                foreach (var candidate in candidates)
                {
                    candidate.Index = info.Candidates.Count;
                    if (candidate.Cost.DynamicOverlapSeconds > 0f) candidate.Cost.Dynamic += config.dynamicCollisionPenalty;
                    var full = new List<PlanSegment>(prefixes[a]);
                    full.AddRange(candidate.Suffix);
                    candidate.Full = full;
                    candidate.Trajectory = AgentPlan.Sample(dataset, full, now, config.collisionSampleHz);
                    info.Candidates.Add(candidate);
                }

                if (candidates.Count == 0) metrics.agentsWithoutNewCandidates++;
                metrics.expansions += stats.Expansions;
                metrics.successorsSeen += stats.SuccessorsSeen;
                metrics.successorsEvaluated += stats.SuccessorsEvaluated;
                metrics.idleExitsPruned += stats.IdleExitsPruned;
                metrics.staticRejects += stats.StaticRejects;
                metrics.deadEndRejects += stats.DeadEndRejects;
                metrics.stopTailRejects += stats.StopTailRejects;
                metrics.circleAccepts += stats.CircleAccepts;
                metrics.familyPruned += stats.FamilyPruned;
                metrics.candidatesTotal += info.Candidates.Count;
            }

            for (var a = 0; a < infos.Count; a++) if (satisfied[a]) metrics.candidatesTotal += infos[a].Candidates.Count;

            metrics.candidateMilliseconds = candidateWatch.Elapsed.TotalMilliseconds;

            // 3. Compatibility + assignment.
            var selection = new List<SelectionAgent>();
            for (var a = 0; a < infos.Count; a++)
            {
                selection.Add(new SelectionAgent { AgentId = infos[a].AgentId, Radius = snapshot.Agents[a].Radius, Candidates = infos[a].Candidates, Urgency = infos[a].Candidates[0].TotalCost });
            }

            var componentSolver = new ComponentSolver(selection, snapshot, config);
            var selectionStats = componentSolver.SelectAll(deadline);
            metrics.components = componentSolver.ComponentCount;
            metrics.largestComponent = componentSolver.LargestComponent;

            // 3b. Regeneration rounds (Section 13.4): the most conflicted agent re-searches with everybody else's chosen
            // trajectory as a hard reservation; its new candidates join the pool and the assignment is redone.
            var regenerated = new HashSet<int>();
            // An infeasible baseline (idle drift below 2r, etc.) gets extra rounds: the conflicting agent re-searches with hard reservations.
            var maxRounds = selectionStats.FinalFeasible ? config.maxRegenerationRounds : config.maxRegenerationRounds + 2;
            for (var round = 0; round < maxRounds; round++)
            {
                if (deadline.RemainingMilliseconds < config.cycleBudgetMilliseconds * 0.12) break;   // keep ~10% for the final selection + publication
                var pick = -1;
                var pickPriority = 0f;
                if (!selectionStats.FinalFeasible)
                {
                    foreach (var a in selectionStats.ConflictingAgents) if (!regenerated.Contains(a)) { pick = a; break; }
                }

                for (var a = 0; a < selection.Count && (pick < 0 || selectionStats.FinalFeasible); a++)
                {
                    if (regenerated.Contains(a)) continue;
                    if (infos[a].Tier == 2 && !config.lodFarRegeneration) continue;
                    var sel = selection[a];
                    var chosenCost = sel.Candidates[sel.Chosen].TotalCost;
                    var bestCost = sel.Candidates[MultiAgentSelector.BestIndex(sel)].TotalCost;
                    var far = infos[a].StartPose.DistanceTo(snapshot.Agents[a].GoalX, snapshot.Agents[a].GoalZ) > config.goalRadiusMeters;
                    var blocked = MultiAgentSelector.BestIndex(sel) != sel.Chosen;
                    var stuck = sel.Chosen == 0 && far && sel.Candidates.Count > 1 && !blocked ? false : sel.Chosen == 0 && far;
                    if (!blocked && !stuck) continue;
                    // Priority: what the agent loses by being blocked; a stuck agent (keeps its old suffix while far from the goal) counts its status-quo cost.
                    var priority = blocked ? chosenCost - bestCost : chosenCost * 0.5f;
                    if (priority > pickPriority) { pickPriority = priority; pick = a; }
                }

                if (pick < 0) break;
                regenerated.Add(pick);
                var agent = snapshot.Agents[pick];
                var info = infos[pick];
                var reservations = new List<OtherAgentTrajectory>();
                for (var j = 0; j < selection.Count; j++)
                {
                    if (j == pick) continue;
                    reservations.Add(new OtherAgentTrajectory { AgentId = selection[j].AgentId, Radius = snapshot.Agents[j].Radius, Trajectory = selection[j].Candidates[selection[j].Chosen].Trajectory });
                }

                var slice = Math.Max(10.0, Math.Min(deadline.RemainingMilliseconds - config.cycleBudgetMilliseconds * 0.1, config.cycleBudgetMilliseconds * 0.15));
                var request = MakeRequest(agent, info, config, horizonEnd, new List<OtherAgentTrajectory>(), new Deadline(slice));
                request.Reservations = reservations;
                request.OldNextClip = info.Candidates[0].Suffix.Count > 0 ? info.Candidates[0].Suffix[0].Clip : -1;
                var extra = search.Run(request, out var regenStats);
                metrics.regenerationRounds++;
                info.Status += $" regen{round}:{extra.Count}";
                if (extra.Count == 0) continue;
                foreach (var candidate in extra)
                {
                    candidate.Index = info.Candidates.Count;
                    candidate.Origin = "regen";
                    if (candidate.Cost.DynamicOverlapSeconds > 0f) candidate.Cost.Dynamic += config.dynamicCollisionPenalty;
                    var full = new List<PlanSegment>(prefixes[pick]);
                    full.AddRange(candidate.Suffix);
                    candidate.Full = full;
                    candidate.Trajectory = AgentPlan.Sample(dataset, full, now, config.collisionSampleHz);
                    info.Candidates.Add(candidate);
                    metrics.regenerationCandidates++;
                }

                var before = selection[pick].Candidates[selection[pick].Chosen].TotalCost;
                selectionStats = componentSolver.Reselect(pick, deadline);
                if (selectionStats.FinalFeasible && selection[pick].Candidates[selection[pick].Chosen].TotalCost < before - 1e-3f) metrics.regenerationSuccesses++;
            }

            for (var a = 0; a < selection.Count; a++)
            {
                var sel = selection[a];
                var far = infos[a].StartPose.DistanceTo(snapshot.Agents[a].GoalX, snapshot.Agents[a].GoalZ) > config.goalRadiusMeters;
                if (MultiAgentSelector.BestIndex(sel) != sel.Chosen || (sel.Chosen == 0 && far)) metrics.localDeadlocks++;
            }

            metrics.compatibilityMilliseconds = selectionStats.CompatibilityMilliseconds;
            metrics.assignmentMilliseconds = selectionStats.AssignmentMilliseconds;
            metrics.pairChecks = selectionStats.PairChecks;
            metrics.pairPrefiltered = selectionStats.PairPrefiltered;
            metrics.sweepChanges = selectionStats.SweepChanges;
            metrics.pairRepairs = selectionStats.PairRepairs;
            metrics.blockedAgents = selectionStats.BlockedAgents;
            metrics.baselineFeasible = selectionStats.BaselineFeasible;
            metrics.timeout = selectionStats.Timeout || metrics.searchTimeouts > 0;
            metrics.minSeparation = selectionStats.MinSeparation;

            var invalidBaseline = false;
            foreach (var info in infos) if (info.Status.StartsWith("InvalidBaseline")) invalidBaseline = true;
            metrics.baselineRepaired = !selectionStats.BaselineFeasible && selectionStats.FinalFeasible;
            if (!selectionStats.FinalFeasible || invalidBaseline)
            {
                result.Published = false;
                result.Status = invalidBaseline ? "InvalidBaseline: an old suffix could not be extended" : "InvalidBaseline: old suffixes are mutually incompatible and no compatible assignment was found";
                metrics.status = result.Status;
                metrics.wallMilliseconds = wall.Elapsed.TotalMilliseconds;
                result.Agents = infos;
                return result;
            }

            // 4. Publish.
            metrics.actualCommitHorizonMin = float.PositiveInfinity;
            metrics.actualCommitHorizonMax = 0f;
            metrics.plannedUntilMin = float.PositiveInfinity;
            for (var a = 0; a < infos.Count; a++)
            {
                var info = infos[a];
                info.Chosen = selection[a].Chosen;
                info.Changed = info.Chosen != 0;
                if (info.Changed) metrics.assignmentChanges++;
                var chosen = info.Candidates[info.Chosen];
                var plan = new AgentPlan
                {
                    AgentId = info.AgentId,
                    Revision = snapshot.Revision,
                    GeneratedAt = now,
                    CommitUntil = info.Boundary,
                    Status = info.Changed ? "replanned" : "kept",
                    Segments = new List<PlanSegment>(chosen.Full)
                };
                result.NewPlans[info.AgentId] = plan;
                if (!info.LodSkipped) info.NextReplanTime = now + (info.Tier == 2 ? config.lodFarReplanSeconds : info.Tier == 1 ? config.lodMidReplanSeconds : 0f);
                if (chosen.Cost.DynamicOverlapSeconds > 0f) { metrics.dynamicPredictedHits++; metrics.dynamicPredictedSeconds += chosen.Cost.DynamicOverlapSeconds; }
                metrics.actualCommitHorizonMin = Math.Min(metrics.actualCommitHorizonMin, info.Boundary - now);
                metrics.actualCommitHorizonMax = Math.Max(metrics.actualCommitHorizonMax, info.Boundary - now);
                metrics.plannedUntilMin = Math.Min(metrics.plannedUntilMin, plan.EndTime - now);
                result.MinCommitUntil = Math.Min(result.MinCommitUntil, info.Boundary);
            }

            result.Agents = infos;
            result.Published = true;
            result.Status = $"published r{snapshot.Revision}: {metrics.assignmentChanges}/{infos.Count} changed, blocked {metrics.blockedAgents}, min sep {metrics.minSeparation:F2} m{(metrics.baselineRepaired ? " (baseline repaired)" : "")}";
            metrics.status = result.Status;
            metrics.wallMilliseconds = wall.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        /// Interaction components: agents whose candidate bounding boxes (inflated by the separation threshold) overlap
        /// transitively are solved together; independent components never enter each other's pair checks or sweeps.
        /// </summary>
        private sealed class ComponentSolver
        {
            private readonly List<SelectionAgent> agents;
            private readonly PlannerConfig config;
            private readonly List<List<int>> components = new List<List<int>>();
            private readonly int[] componentOf;
            private readonly SelectionStats[] componentStats;

            public int ComponentCount => components.Count;
            public int LargestComponent { get; private set; }

            public ComponentSolver(List<SelectionAgent> agents, PlanningSnapshot snapshot, PlannerConfig config)
            {
                this.agents = agents;
                this.config = config;
                var n = agents.Count;
                componentOf = new int[n];
                var parent = new int[n];
                for (var i = 0; i < n; i++) parent[i] = i;
                int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
                if (config.componentDecomposition)
                {
                    var minX = new float[n]; var maxX = new float[n]; var minZ = new float[n]; var maxZ = new float[n];
                    for (var i = 0; i < n; i++)
                    {
                        minX[i] = float.MaxValue; maxX[i] = float.MinValue; minZ[i] = float.MaxValue; maxZ[i] = float.MinValue;
                        foreach (var c in agents[i].Candidates)
                        {
                            var t = c.Trajectory;
                            if (t.MinX < minX[i]) minX[i] = t.MinX; if (t.MaxX > maxX[i]) maxX[i] = t.MaxX;
                            if (t.MinZ < minZ[i]) minZ[i] = t.MinZ; if (t.MaxZ > maxZ[i]) maxZ[i] = t.MaxZ;
                        }
                    }

                    for (var i = 0; i < n; i++)
                    {
                        for (var j = i + 1; j < n; j++)
                        {
                            var inflate = agents[i].Radius + agents[j].Radius + config.agentMarginMeters;
                            if (maxX[i] + inflate < minX[j] || maxX[j] + inflate < minX[i] || maxZ[i] + inflate < minZ[j] || maxZ[j] + inflate < minZ[i]) continue;
                            var a = Find(i); var b = Find(j);
                            if (a != b) parent[a] = b;
                        }
                    }
                }
                else
                {
                    for (var i = 1; i < n; i++) parent[Find(i)] = Find(0);
                }

                var index = new Dictionary<int, int>();
                for (var i = 0; i < n; i++)
                {
                    var root = Find(i);
                    if (!index.TryGetValue(root, out var id)) { id = components.Count; index[root] = id; components.Add(new List<int>()); }
                    components[id].Add(i);
                    componentOf[i] = id;
                }

                componentStats = new SelectionStats[components.Count];
                foreach (var component in components) LargestComponent = Math.Max(LargestComponent, component.Count);
            }

            public SelectionStats SelectAll(Deadline deadline)
            {
                for (var c = 0; c < components.Count; c++) componentStats[c] = SolveComponent(c, deadline);
                return Aggregate();
            }

            public SelectionStats Reselect(int agentIndex, Deadline deadline)
            {
                var c = componentOf[agentIndex];
                componentStats[c] = SolveComponent(c, deadline);
                return Aggregate();
            }

            private SelectionStats SolveComponent(int c, Deadline deadline)
            {
                var members = components[c];
                if (members.Count == 1)
                {
                    // Nobody to conflict with: the cheapest candidate wins.
                    var agent = agents[members[0]];
                    agent.Chosen = MultiAgentSelector.BestIndex(agent);
                    return new SelectionStats { BaselineFeasible = true, FinalFeasible = true };
                }

                var subset = new List<SelectionAgent>(members.Count);
                foreach (var m in members) subset.Add(agents[m]);
                var stats = MultiAgentSelector.Select(subset, config, deadline);
                for (var k = 0; k < stats.ConflictingAgents.Count; k++) stats.ConflictingAgents[k] = members[stats.ConflictingAgents[k]];
                return stats;
            }

            private SelectionStats Aggregate()
            {
                var total = new SelectionStats { BaselineFeasible = true, FinalFeasible = true };
                foreach (var s in componentStats)
                {
                    if (s == null) continue;
                    total.PairChecks += s.PairChecks; total.PairPrefiltered += s.PairPrefiltered; total.SweepChanges += s.SweepChanges; total.PairRepairs += s.PairRepairs;
                    total.Sweeps = Math.Max(total.Sweeps, s.Sweeps); total.BlockedAgents += s.BlockedAgents;
                    total.CompatibilityMilliseconds += s.CompatibilityMilliseconds; total.AssignmentMilliseconds += s.AssignmentMilliseconds;
                    total.BaselineFeasible &= s.BaselineFeasible; total.FinalFeasible &= s.FinalFeasible; total.Timeout |= s.Timeout;
                    if (s.MinSeparation < total.MinSeparation) { total.MinSeparation = s.MinSeparation; total.MinSeparationAgentA = s.MinSeparationAgentA; total.MinSeparationAgentB = s.MinSeparationAgentB; }
                    total.ConflictingAgents.AddRange(s.ConflictingAgents);
                }

                return total;
            }
        }

        /// <summary>
        /// Search-free suffixes for a standing agent: (a) wait k idle segments then replay the old suffix's clips (delay),
        /// (b) one short step (0.2-0.6 m) then its stop path and idle fill (step away). Every edge is checked against the graph.
        /// </summary>
        private static List<List<PlanSegment>> ParametricSuffixes(ClipDataset dataset, AgentCycleInfo info, float horizonEnd)
        {
            var result = new List<List<PlanSegment>>();
            var start = dataset.Clips[info.StartClip];
            var old = info.Candidates[0].Suffix;
            // (a) delay: k idle continuations, then the old suffix's non-idle clips re-timed and re-placed.
            var firstMoving = -1;
            for (var i = 0; i < old.Count; i++) if (!dataset.Clips[old[i].Clip].IsStationary) { firstMoving = i; break; }
            if (firstMoving >= 0)
            {
                for (var k = 1; k <= 3; k++)
                {
                    var suffix = new List<PlanSegment>();
                    var clip = info.StartClip;
                    var pose = info.StartPose;
                    var time = info.Boundary;
                    var ok = true;
                    for (var j = 0; j < k && ok; j++)
                    {
                        var next = dataset.Clips[clip].StationaryContinuation;
                        if (next < 0) { ok = false; break; }
                        suffix.Add(new PlanSegment(next, time, time + dataset.Clips[next].Duration, pose));
                        pose = pose.Compose(dataset.Clips[next].EndPose);
                        time += dataset.Clips[next].Duration;
                        clip = next;
                    }

                    if (!ok || !dataset.HasEdge(clip, old[firstMoving].Clip)) continue;
                    for (var i = firstMoving; i < old.Count; i++)
                    {
                        var c = old[i].Clip;
                        if (i > firstMoving && !dataset.HasEdge(old[i - 1].Clip, c)) { ok = false; break; }
                        suffix.Add(new PlanSegment(c, time, time + dataset.Clips[c].Duration, pose));
                        pose = pose.Compose(dataset.Clips[c].EndPose);
                        time += dataset.Clips[c].Duration;
                    }

                    if (!ok || !AgentPlan.FillStationary(dataset, suffix, horizonEnd)) continue;
                    result.Add(suffix);
                }
            }

            // (b) step away: one short non-stationary successor, its stop path, idle fill.
            var steps = 0;
            foreach (var next in start.Outgoing)
            {
                var n = dataset.Clips[next];
                if (n.IsStationary || !n.CanReachWait || n.TotalDisplacement < 0.2f || n.TotalDisplacement > 0.6f || Math.Abs(n.TotalYaw) > 20f) continue;
                var suffix = new List<PlanSegment>();
                var pose = info.StartPose;
                var time = info.Boundary;
                suffix.Add(new PlanSegment(next, time, time + n.Duration, pose));
                pose = pose.Compose(n.EndPose);
                time += n.Duration;
                var ok = true;
                foreach (var s in n.StopPath)
                {
                    suffix.Add(new PlanSegment(s, time, time + dataset.Clips[s].Duration, pose));
                    pose = pose.Compose(dataset.Clips[s].EndPose);
                    time += dataset.Clips[s].Duration;
                }

                if (!AgentPlan.FillStationary(dataset, suffix, horizonEnd)) ok = false;
                if (ok) { result.Add(suffix); steps++; }
                if (steps >= 6) break;
            }

            return result;
        }

        [ThreadStatic] private static List<ObstaclePrediction> obstacles;

        private static SearchRequest MakeRequest(AgentSnapshot agent, AgentCycleInfo info, PlannerConfig config, float horizonEnd, List<OtherAgentTrajectory> others, Deadline deadline)
        {
            return new SearchRequest
            {
                AgentId = agent.Id,
                Radius = agent.Radius,
                GoalX = agent.GoalX,
                GoalZ = agent.GoalZ,
                Field = agent.Field,   // the simulation decides (config toggle, or always on scene maps)
                Obstacles = obstacles,
                StartClip = info.StartClip,
                StartPose = info.StartPose,
                StartTime = info.Boundary,
                HorizonEnd = horizonEnd,
                Others = others ?? new List<OtherAgentTrajectory>(),
                Config = config,
                Deadline = deadline,
                MaxExpansions = config.maxExpansionsPerAgent,
                MaxCandidates = Math.Max(1, config.maxCandidatesPerAgent - 1)
            };
        }
    }

    /// <summary>Offline re-validation of published plans, independent of the planner's own checks (Section 17.2).</summary>
    public static class PlanValidator
    {
        [Serializable]
        public sealed class Report
        {
            public int agentPairsChecked;
            public int controlledCollisions;
            public int staticViolations;
            public int illegalEdges;
            public float minSeparation = float.PositiveInfinity;
            public List<string> details = new List<string>();
        }

        public static Report Validate(ClipDataset dataset, WalkableMap map, PlannerConfig config, IReadOnlyList<AgentSnapshot> agents, float from)
        {
            var report = new Report();
            var trajectories = new List<WorldTrajectory>();
            foreach (var agent in agents)
            {
                var segments = agent.Plan.Segments;
                for (var i = 1; i < segments.Count; i++)
                {
                    if (!dataset.HasEdge(segments[i - 1].Clip, segments[i].Clip))
                    {
                        report.illegalEdges++;
                        report.details.Add($"agent {agent.Id}: illegal edge {dataset.Clips[segments[i - 1].Clip].DisplayName} -> {dataset.Clips[segments[i].Clip].DisplayName} at {segments[i].StartTime:F2}");
                    }
                }

                var trajectory = AgentPlan.Sample(dataset, segments, from, config.collisionSampleHz);
                trajectories.Add(trajectory);
                var required = agent.Radius + config.staticMarginMeters;
                foreach (var segment in segments)
                {
                    if (segment.EndTime <= from) continue;
                    var c = dataset.Clips[segment.Clip];
                    var unsafeSample = map.ClipUnsafeSample(dataset, segment.Clip, segment.WorldStart, required, config.staticCheckMinSegmentMeters, config.boundingCircleAccept, out _);
                    if (unsafeSample >= 0)
                    {
                        report.staticViolations++;
                        var index = c.SampleOffset + unsafeSample;
                        segment.WorldStart.TransformPoint(dataset.X[index], dataset.Z[index], out var wx, out var wz);
                        if (report.details.Count < 200) report.details.Add($"agent {agent.Id}: static violation in {c.DisplayName} at t={segment.StartTime + dataset.T[index]:F2} ({wx:F2}, {wz:F2})");
                    }
                }
            }

            for (var i = 0; i < agents.Count; i++)
            {
                for (var j = i + 1; j < agents.Count; j++)
                {
                    report.agentPairsChecked++;
                    var threshold = agents[i].Radius + agents[j].Radius;
                    var separation = CircleSweep.MinSeparation(trajectories[i], trajectories[j], threshold, out var firstConflict);
                    if (separation < report.minSeparation) report.minSeparation = separation;
                    if (separation < threshold)
                    {
                        report.controlledCollisions++;
                        if (report.details.Count < 200) report.details.Add($"agents {agents[i].Id}/{agents[j].Id}: separation {separation:F3} < {threshold:F3} at t={firstConflict:F2}");
                    }
                }
            }

            return report;
        }
    }
}
