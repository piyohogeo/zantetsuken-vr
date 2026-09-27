using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>Wall-clock deadline shared by every planner loop (Section 14.2).</summary>
    public sealed class Deadline
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        public double LimitMilliseconds;

        public Deadline(double limitMilliseconds) { LimitMilliseconds = limitMilliseconds; }
        public double ElapsedMilliseconds => watch.Elapsed.TotalMilliseconds;
        public double RemainingMilliseconds => LimitMilliseconds - watch.Elapsed.TotalMilliseconds;
        public bool Expired => watch.Elapsed.TotalMilliseconds >= LimitMilliseconds;
    }

    [Serializable]
    public sealed class CostBreakdown
    {
        public float Progress;
        public float Heading;
        public float Speed;
        public float Idle;
        public float Turn;
        public float Facing;
        public float Churn;
        public float OtherAgent;
        /// <summary>Hard-ish penalty: the old suffix (candidate 0) violates the static map after idle drift; any safe alternative should win.</summary>
        public float Penalty;
        /// <summary>External dynamic circle: overlap seconds x weight + one fixed penalty when any overlap is predicted (Section 9.4).</summary>
        public float Dynamic;
        public float DynamicOverlapSeconds;
        public float Total => Progress + Heading + Speed + Idle + Turn + Facing + Churn + OtherAgent + Penalty + Dynamic;

        public CostBreakdown Clone() => (CostBreakdown)MemberwiseClone();

        public void Reset()
        {
            Progress = 0f; Heading = 0f; Speed = 0f; Idle = 0f; Turn = 0f; Facing = 0f; Churn = 0f; OtherAgent = 0f; Penalty = 0f; Dynamic = 0f; DynamicOverlapSeconds = 0f;
        }

        public void Add(CostBreakdown other)
        {
            Progress += other.Progress; Heading += other.Heading; Speed += other.Speed; Idle += other.Idle; Turn += other.Turn; Facing += other.Facing; Churn += other.Churn; OtherAgent += other.OtherAgent; Penalty += other.Penalty;
            Dynamic += other.Dynamic; DynamicOverlapSeconds += other.DynamicOverlapSeconds;
        }

        public string Describe() => $"total {Total:F2} = prog {Progress:F2} head {Heading:F2} spd {Speed:F2} idle {Idle:F2} turn {Turn:F2} facing {Facing:F2} churn {Churn:F2} other {OtherAgent:F2}{(Dynamic > 0f ? $" dyn {Dynamic:F1} ({DynamicOverlapSeconds:F1} s)" : "")}{(Penalty > 0f ? $" PENALTY {Penalty:F0}" : "")}";
    }

    public sealed class PlanCandidate
    {
        public int Index;
        public string Origin = "search";       // old | search
        public List<PlanSegment> Suffix = new List<PlanSegment>();
        public List<PlanSegment> Full = new List<PlanSegment>();
        public CostBreakdown Cost = new CostBreakdown();
        public WorldTrajectory Trajectory;
        public string Note = string.Empty;
        public float TotalCost => Cost.Total;
    }

    [Serializable]
    public sealed class SearchStats
    {
        public int Expansions;
        public int SuccessorsSeen;
        public int SuccessorsEvaluated;
        public int IdleExitsPruned;
        public int StaticRejects;
        public int DeadEndRejects;
        public int DominancePruned;
        public int BeamPruned;
        public int Finals;
        public int StopTailRejects;
        public int ReservationRejects;  // clips rejected by a hard reservation (regeneration round)
        public int CircleAccepts;       // clips whose static check was settled by the start-pose bounding circle
        public int FamilyPruned;        // successors dropped because a better yaw variant of the same clip was already chosen
        public int PartialFinals;       // candidates finalised from unexpanded states at the deadline / cap (walk, stop, wait)
        public int GoalFinals;          // candidates finalised early because the agent waits at its goal
        public bool Timeout;
        public bool ExpansionCap;
        public double Milliseconds;

        public string Describe() => $"exp {Expansions} seen {SuccessorsSeen} eval {SuccessorsEvaluated} idlePruned {IdleExitsPruned} static {StaticRejects} deadEnd {DeadEndRejects} dom {DominancePruned} beam {BeamPruned} finals {Finals} (partial {PartialFinals}, goal {GoalFinals}) stopRej {StopTailRejects} resRej {ReservationRejects} circle {CircleAccepts} fam {FamilyPruned} {Milliseconds:F0} ms{(Timeout ? " TIMEOUT" : "")}{(ExpansionCap ? " CAP" : "")}";
    }

    public sealed class OtherAgentTrajectory
    {
        public int AgentId;
        public float Radius;
        public WorldTrajectory Trajectory;
    }

    public sealed class SearchRequest
    {
        public int AgentId;
        public float Radius;
        public float GoalX;
        public float GoalZ;
        public GoalField Field;
        public int StartClip;
        public Pose2 StartPose;
        public float StartTime;
        public float HorizonEnd;
        public int OldNextClip = -1;
        public List<OtherAgentTrajectory> Others = new List<OtherAgentTrajectory>();
        /// <summary>Regeneration round (Section 13.4): other agents' chosen trajectories; any overlap rejects the clip (hard).</summary>
        public List<OtherAgentTrajectory> Reservations = new List<OtherAgentTrajectory>();
        /// <summary>External dynamic circles (soft cost only, never a hard rejection).</summary>
        public List<ObstaclePrediction> Obstacles = new List<ObstaclePrediction>();
        public PlannerConfig Config;
        public Deadline Deadline;
        public int MaxExpansions;
        public int MaxCandidates;
        public int BeamWidth;   // 0 = config.beamWidth
    }

    /// <summary>
    /// Time-bucketed beam search over the clip graph for one agent (LOCOMOTION_SEARCH_PLAN.md Section 11).
    /// States live on clip boundaries; successors are ranked cheaply from descriptors, only the top ones get a
    /// world trajectory and a static check; stationary nodes expose one continuation plus a capped exit set.
    /// </summary>
    public sealed class SingleAgentSearch
    {
        private sealed class State
        {
            public int Clip;
            public Pose2 Pose;          // world pose at the end of Clip (start of the successor)
            public float Time;          // end time of Clip
            public CostBreakdown Cost;
            public State Parent;
            public PlanSegment Segment; // the segment that produced this state (Clip placed at Segment.WorldStart)
            public int Depth;
            public bool IsRoot;
            public float BeamKey;       // cost used for beam ranking: Cost.Total extrapolated to the end of the state's time bucket
        }

        private readonly ClipDataset dataset;
        private readonly WalkableMap map;
        private SearchRequest request;
        private PlannerConfig config;
        private float required;
        private float horizonLength;
        private readonly List<float> sampleX = new List<float>(512);
        private readonly List<float> sampleZ = new List<float>(512);
        private int circleAccepts;
        private int reservationRejects;
        private readonly Dictionary<int, int> familyBest = new Dictionary<int, int>();
        // Scratch objects reused across evaluations (Mono's allocator is slow; callers consume the result immediately).
        private readonly CostBreakdown scratchCost = new CostBreakdown();
        private readonly int[] binBest = new int[8];
        private readonly float[] binRank = new float[8];
        private readonly Dictionary<int, int> perClipScratch = new Dictionary<int, int>();
        private readonly List<State> keptScratch = new List<State>();

        public SingleAgentSearch(ClipDataset dataset, WalkableMap map)
        {
            this.dataset = dataset;
            this.map = map;
        }

        // ---- cost of an arbitrary segment list (used for the old suffix so candidates are comparable) ----

        public CostBreakdown EvaluateSegments(SearchRequest req, IReadOnlyList<PlanSegment> segments, out bool staticOk)
        {
            Bind(req);
            var cost = new CostBreakdown();
            staticOk = true;
            foreach (var segment in segments)
            {
                if (!config.staticCheckWholePlan && segment.StartTime >= req.HorizonEnd) break;
                var part = EvaluateClip(segment.WorldStart, segment.StartTime, segment.Clip, out var ok);
                if (!ok) staticOk = false;
                cost.Add(part);
            }

            if (segments.Count > 0)
            {
                var end = AgentPlan.EndPose(dataset, segments);
                cost.Heading += HeadingCost(end);
            }

            return cost;
        }

        private void Bind(SearchRequest req)
        {
            request = req;
            config = req.Config;
            required = req.Radius + config.staticMarginMeters;
            horizonLength = Math.Max(0.5f, req.HorizonEnd - req.StartTime);
        }

        /// <summary>The goal counts as "in view" when the field's path is barely longer than the straight line; then the exact straight line is used (the 8-connected field over-estimates by up to 8% and quantises directions).</summary>
        private const float FieldDetourMeters = 0.5f;

        /// <summary>Path distance to the goal (field, when the straight line is blocked) or straight line.</summary>
        private float GoalDistance(float x, float z)
        {
            var dx = x - request.GoalX; var dz = z - request.GoalZ;
            var euclid = (float)Math.Sqrt(dx * dx + dz * dz);
            if (request.Field == null) return euclid;
            var path = request.Field.DistanceAt(x, z);
            return path - euclid > FieldDetourMeters ? path : euclid;
        }

        /// <summary>Point the agent should face from `pose`: the goal, or the field's next waypoint when the goal is around a corner.</summary>
        private void HeadingTarget(Pose2 pose, out float x, out float z)
        {
            x = request.GoalX; z = request.GoalZ;
            if (request.Field == null) return;
            var euclid = pose.DistanceTo(request.GoalX, request.GoalZ);
            if (request.Field.DistanceAt(pose.X, pose.Z) - euclid <= FieldDetourMeters) return;
            request.Field.Waypoint(pose.X, pose.Z, config.goalFieldLookAheadMeters, out x, out z);
        }

        /// <summary>Heading error towards the goal, or towards the field's next waypoint when the goal is around a corner.</summary>
        private float HeadingError(Pose2 pose)
        {
            if (request.Field == null) return pose.HeadingErrorTo(request.GoalX, request.GoalZ);
            var euclid = pose.DistanceTo(request.GoalX, request.GoalZ);
            if (request.Field.DistanceAt(pose.X, pose.Z) - euclid <= FieldDetourMeters) return pose.HeadingErrorTo(request.GoalX, request.GoalZ);
            request.Field.Waypoint(pose.X, pose.Z, config.goalFieldLookAheadMeters, out var wx, out var wz);
            return pose.HeadingErrorTo(wx, wz);
        }

        private float HeadingCost(Pose2 end)
        {
            var far = end.DistanceTo(request.GoalX, request.GoalZ) > config.goalRadiusMeters;
            return far ? config.wHeading * HeadingError(end) / 180f : 0f;
        }

        /// <summary>
        /// Places `clip` at `start`, accumulates the cost terms over the part inside the horizon, checks every sample
        /// segment against the static map (hard) and counts predicted seconds of overlap with other agents' old plans (soft).
        /// </summary>
        private CostBreakdown EvaluateClip(Pose2 start, float startTime, int clip, out bool staticOk)
        {
            var c = dataset.Clips[clip];
            var cost = scratchCost;
            cost.Reset();
            staticOk = true;
            var dt = 1f / dataset.SampleHz;
            var goalX = request.GoalX;
            var goalZ = request.GoalZ;
            var goalRadius = config.goalRadiusMeters;
            sampleX.Clear();
            sampleZ.Clear();
            var minX = float.MaxValue; var maxX = float.MinValue; var minZ = float.MaxValue; var maxZ = float.MinValue;
            var previousX = 0f; var previousZ = 0f;
            var previousT = 0f;
            var stationarySeconds = 0f;
            // Evaluate at the collision sample rate (stride over the dataset samples) plus the exact end sample.
            var stride = Math.Max(1, (int)Math.Round(dataset.SampleHz / Math.Max(1f, config.collisionSampleHz)));
            var last = c.SampleCount - 1;
            var cosYaw = (float)Math.Cos(start.YawDeg * Pose2.Deg2Rad);
            var sinYaw = (float)Math.Sin(start.YawDeg * Pose2.Deg2Rad);
            // Heading target for this clip: the goal, or the field waypoint when the goal is around a corner (once per clip).
            HeadingTarget(start, out var targetX, out var targetZ);
            var facingFraction = c.FacingErrorDeg / 180f;
            // Static check shared with PlanValidator (bounding-circle acceptance, else every sample chord): planner-pass implies validator-pass.
            staticOk = map.ClipUnsafeSample(dataset, clip, start, required, config.staticCheckMinSegmentMeters, config.boundingCircleAccept, out var circleAccepted) < 0;
            if (circleAccepted) circleAccepts++;
            for (var i = 0; i <= last; i++)
            {
                var index = c.SampleOffset + i;
                var lx = dataset.X[index]; var lz = dataset.Z[index];
                var wx = start.X + lx * cosYaw + lz * sinYaw;
                var wz = start.Z - lx * sinYaw + lz * cosYaw;
                if (i % stride != 0 && i != last) continue;
                sampleX.Add(wx); sampleZ.Add(wz);
                if (wx < minX) minX = wx; if (wx > maxX) maxX = wx; if (wz < minZ) minZ = wz; if (wz > maxZ) maxZ = wz;
                var time = startTime + dataset.T[index];
                if (i > 0)
                {
                    var stepDt = dataset.T[index] - previousT;
                    if (time <= request.HorizonEnd && stepDt > 0f)
                    {
                        var ddx = wx - goalX; var ddz = wz - goalZ;
                        var euclid = (float)Math.Sqrt(ddx * ddx + ddz * ddz);
                        var distance = euclid;
                        if (request.Field != null)
                        {
                            var path = request.Field.DistanceAt(wx, wz);
                            if (path - euclid > FieldDetourMeters) distance = path;
                        }
                        cost.Progress += config.wProgress * distance * stepDt / horizonLength;
                        if (euclid > goalRadius)
                        {
                            var vx = (wx - previousX) / stepDt; var vz = (wz - previousZ) / stepDt;
                            var speed = (float)Math.Sqrt(vx * vx + vz * vz);
                            cost.Speed += config.wSpeed * Math.Abs(speed - config.desiredSpeedMps) * stepDt / horizonLength;
                            if (c.IsStationary) stationarySeconds += stepDt;
                            else
                            {
                                // Walk facing the way you go, and keep facing the goal along the way (not only at the candidate end).
                                cost.Facing += config.wFacing * facingFraction * stepDt / horizonLength;
                                if (config.wHeadingPath > 0f)
                                {
                                    var yaw = start.YawDeg + dataset.Yaw[index];
                                    var error = Math.Abs(Pose2.WrapDeg((float)Math.Atan2(targetX - wx, targetZ - wz) * Pose2.Rad2Deg - yaw));
                                    cost.Heading += config.wHeadingPath * error / 180f * stepDt / horizonLength;
                                }
                            }
                        }
                    }
                }

                previousX = wx; previousZ = wz; previousT = dataset.T[index];
            }

            cost.Idle += config.wIdle * stationarySeconds;
            cost.Turn += config.wTurn * c.YawTravelDeg / 360f;

            // External dynamic circles (Section 9.4): predicted overlap seconds are a large soft cost; never a hard rejection.
            if (request.Obstacles.Count > 0)
            {
                var endTime = startTime + c.Duration;
                var stepT = 1f / config.collisionSampleHz;
                var seconds = 0f;
                foreach (var obstacle in request.Obstacles)
                {
                    var inflate = request.Radius + obstacle.Radius + config.dynamicKeepMeters;
                    if (!obstacle.Trajectory.BucketsOverlap(startTime, endTime, minX, maxX, minZ, maxZ, inflate) && !(startTime > obstacle.Trajectory.EndTime)) continue;
                    var thresholdSq = inflate * inflate;
                    for (var t = startTime; t <= endTime; t += stepT)
                    {
                        var pose = start.Compose(dataset.LocalPoseAt(clip, t - startTime));
                        obstacle.Trajectory.PositionAt(t, out var ox, out var oz);   // held at the last prediction sample afterwards
                        var dx = pose.X - ox; var dz = pose.Z - oz;
                        if (dx * dx + dz * dz < thresholdSq) seconds += stepT;
                    }
                }

                if (seconds > 0f)
                {
                    cost.DynamicOverlapSeconds += seconds;
                    cost.Dynamic += config.wDynamicObstacle * seconds;
                }
            }

            // Hard reservations (regeneration round): the clip is rejected if it overlaps another agent's chosen trajectory.
            if (request.Reservations.Count > 0)
            {
                var endTime = startTime + c.Duration;
                var stepT = 1f / config.collisionSampleHz;
                foreach (var other in request.Reservations)
                {
                    var inflate = request.Radius + other.Radius + config.agentMarginMeters;
                    if (!other.Trajectory.BucketsOverlap(startTime, endTime, minX, maxX, minZ, maxZ, inflate)) continue;
                    var thresholdSq = inflate * inflate;
                    var hit = false;
                    for (var t = startTime; t <= endTime && !hit; t += stepT)
                    {
                        var pose = start.Compose(dataset.LocalPoseAt(clip, t - startTime));
                        other.Trajectory.PositionAt(t, out var ox, out var oz);   // held at the last sample after the trajectory ends
                        var dx = pose.X - ox; var dz = pose.Z - oz;
                        if (dx * dx + dz * dz < thresholdSq) hit = true;
                    }

                    if (hit) { staticOk = false; reservationRejects++; break; }
                }
            }

            // Soft collision with other agents' old plans (aligned samples at the collision rate).
            if (request.Others.Count > 0 && config.wOtherAgent > 0f)
            {
                var endTime = startTime + c.Duration;
                var seconds = 0f;
                var stepT = 1f / config.collisionSampleHz;
                foreach (var other in request.Others)
                {
                    var inflate = request.Radius + other.Radius + config.agentMarginMeters;
                    if (!other.Trajectory.BucketsOverlap(startTime, endTime, minX, maxX, minZ, maxZ, inflate)) continue;
                    var thresholdSq = inflate * inflate;
                    for (var t = startTime; t <= endTime && t <= other.Trajectory.EndTime; t += stepT)
                    {
                        var local = t - startTime;
                        var pose = start.Compose(dataset.LocalPoseAt(clip, local));
                        other.Trajectory.PositionAt(t, out var ox, out var oz);
                        var dx = pose.X - ox; var dz = pose.Z - oz;
                        if (dx * dx + dz * dz < thresholdSq) seconds += stepT;
                    }
                }

                cost.OtherAgent += config.wOtherAgent * seconds;
            }

            return cost;
        }

        // ---- beam search ----

        public List<PlanCandidate> Run(SearchRequest req, out SearchStats stats)
        {
            Bind(req);
            stats = new SearchStats();
            circleAccepts = 0;
            reservationRejects = 0;
            var watch = Stopwatch.StartNew();
            var result = new List<PlanCandidate>();
            var startClip = dataset.Clips[req.StartClip];
            var root = new State { Clip = req.StartClip, Pose = req.StartPose, Time = req.StartTime, Cost = new CostBreakdown(), Depth = 0, IsRoot = true };
            var buckets = new SortedDictionary<int, List<State>>();
            AddToBucket(buckets, root, 0);
            var dominance = new Dictionary<long, float>();
            var finals = new List<State>();
            var bucketSeconds = Math.Max(0.1f, config.beamTimeBucketSeconds);
            var searchEnd = Math.Min(req.HorizonEnd, req.StartTime + Math.Max(1f, config.searchHorizonSeconds));
            var expansions = 0;
            var ranked = new List<(float rank, int clip)>();
            var chosen = new List<int>();

            while (buckets.Count > 0)
            {
                if (req.Deadline != null && req.Deadline.Expired) { stats.Timeout = true; break; }
                if (expansions >= req.MaxExpansions) { stats.ExpansionCap = true; break; }
                var firstKey = -1;
                foreach (var key in buckets.Keys) { firstKey = key; break; }
                var bucket = buckets[firstKey];
                buckets.Remove(firstKey);

                // Beam pruning: best cost first, at most maxStatesPerClipPerBucket per terminal clip, beamWidth total.
                // Ranking by the raw cumulative cost favours states that consumed less time (short turn clips over a 1.3 s walk
                // cycle: zigzag chains, 2026-09-27), so states are compared as if each waited at its end pose until the bucket end.
                bucket.Sort((a, b) => a.BeamKey != b.BeamKey ? a.BeamKey.CompareTo(b.BeamKey) : a.Clip.CompareTo(b.Clip));
                var perClip = perClipScratch;
                perClip.Clear();
                var kept = keptScratch;
                kept.Clear();
                foreach (var state in bucket)
                {
                    if (kept.Count >= (req.BeamWidth > 0 ? req.BeamWidth : config.beamWidth)) { stats.BeamPruned++; continue; }
                    perClip.TryGetValue(state.Clip, out var count);
                    if (count >= config.maxStatesPerClipPerBucket) { stats.BeamPruned++; continue; }
                    var key = DominanceKey(state);
                    if (dominance.TryGetValue(key, out var best) && best < state.Cost.Total - 1e-4f) { stats.DominancePruned++; continue; }
                    dominance[key] = state.Cost.Total;
                    perClip[state.Clip] = count + 1;
                    kept.Add(state);
                }

                foreach (var state in kept)
                {
                    if (req.Deadline != null && req.Deadline.Expired) { stats.Timeout = true; break; }
                    if (expansions >= req.MaxExpansions) { stats.ExpansionCap = true; break; }
                    expansions++;
                    var current = dataset.Clips[state.Clip];
                    // An agent under threat from an external circle is treated as "far" so moving successors are ranked, not idling.
                    var far = state.Pose.DistanceTo(req.GoalX, req.GoalZ) > config.goalRadiusMeters || Threatened(state.Pose, state.Time, 0.5f);
                    var atGoalDistance = GoalDistance(state.Pose.X, state.Pose.Z);

                    // Successor set (Section 11.4: stationary nodes expose one continuation + capped exits).
                    ranked.Clear();
                    stats.SuccessorsSeen += current.Outgoing.Length;
                    foreach (var next in current.Outgoing)
                    {
                        var n = dataset.Clips[next];
                        if (!n.CanReachWait) { stats.DeadEndRejects++; continue; }
                        if (current.IsStationary && n.IsStationary && next != current.StationaryContinuation) { stats.IdleExitsPruned++; continue; }
                        ranked.Add((CheapRank(state, next, far, atGoalDistance), next));
                    }

                    ranked.Sort((a, b) => a.rank != b.rank ? a.rank.CompareTo(b.rank) : a.clip.CompareTo(b.clip));
                    // Yaw-variant families: a base clip and its +-10/20/30 deg/s variants mostly differ in heading, so only the
                    // best few of each family (by cheap rank) are worth a full evaluation.
                    if (config.maxVariantsPerFamily > 0)
                    {
                        familyBest.Clear();
                        var keptCount = 0;
                        for (var i = 0; i < ranked.Count; i++)
                        {
                            var family = dataset.Clips[ranked[i].clip].FamilyId;
                            familyBest.TryGetValue(family, out var count);
                            if (count >= config.maxVariantsPerFamily) { stats.FamilyPruned++; continue; }
                            familyBest[family] = count + 1;
                            ranked[keptCount++] = ranked[i];
                        }

                        ranked.RemoveRange(keptCount, ranked.Count - keptCount);
                    }

                    chosen.Clear();
                    // Regeneration round: the cheap rank cannot see the reservations (a straight Start may be the one clip that is
                    // blocked), so every ranked successor is evaluated and direction diversity also applies to idle exits.
                    var exhaustive = req.Reservations.Count > 0;
                    var limit = exhaustive ? ranked.Count : current.IsStationary ? Math.Min(config.maxSuccessorsEvaluatedPerState, config.idleExitCap + 1) : config.maxSuccessorsEvaluatedPerState;
                    for (var i = 0; i < ranked.Count && chosen.Count < limit; i++) chosen.Add(ranked[i].clip);
                    if (current.IsStationary) stats.IdleExitsPruned += Math.Max(0, ranked.Count - chosen.Count);
                    if (far && (!current.IsStationary || exhaustive))
                    {
                        // Direction diversity: keep the best successor of each displacement-angle octant.
                        for (var b = 0; b < 8; b++) { binBest[b] = -1; binRank[b] = float.MaxValue; }
                        foreach (var (rank, clip) in ranked)
                        {
                            var n = dataset.Clips[clip];
                            if (n.TotalDisplacement < 0.2f) continue;
                            state.Pose.Rotate(n.EndX, n.EndZ, out var dx, out var dz);
                            var bin = ((int)Math.Floor((Math.Atan2(dx, dz) + Math.PI) / (Math.PI / 4.0))) & 7;
                            if (rank < binRank[bin]) { binRank[bin] = rank; binBest[bin] = clip; }
                        }

                        var extras = 0;
                        for (var b = 0; b < 8 && extras < 4; b++) if (binBest[b] >= 0 && !chosen.Contains(binBest[b])) { chosen.Add(binBest[b]); extras++; }
                        // Reserved slots: the best loop / straight successors are always evaluated, so a forward walk cycle cannot be
                        // pushed out of the cap by turn clips whose end pose lands marginally closer (INERTIAL: 224 turn_moving vs 112 loops).
                        var reserved = 0;
                        for (var i = 0; i < ranked.Count && reserved < config.reservedStraightSuccessors; i++)
                        {
                            var n = dataset.Clips[ranked[i].clip];
                            if (!(n.IsLoop || n.IsStraight) || n.TotalDisplacement < 0.2f || chosen.Contains(ranked[i].clip)) continue;
                            chosen.Add(ranked[i].clip);
                            reserved++;
                        }
                    }

                    foreach (var next in chosen)
                    {
                        var n = dataset.Clips[next];
                        stats.SuccessorsEvaluated++;
                        var part = EvaluateClip(state.Pose, state.Time, next, out var staticOk);
                        if (!staticOk) { stats.StaticRejects++; continue; }
                        var cost = state.Cost.Clone();
                        cost.Add(part);
                        if (state.IsRoot && req.OldNextClip >= 0 && next != req.OldNextClip) cost.Churn += config.wChurn;
                        var child = new State
                        {
                            Clip = next,
                            Pose = state.Pose.Compose(n.EndPose),
                            Time = state.Time + n.Duration,
                            Cost = cost,
                            Parent = state,
                            Segment = new PlanSegment(next, state.Time, state.Time + n.Duration, state.Pose),
                            Depth = state.Depth + 1
                        };
                        if (child.Time >= searchEnd - 1e-4f)
                        {
                            // Search horizon reached: stop path, then idle continuation up to the full planning horizon.
                            var final = Finalize(child, stats);
                            if (final != null) final = FillToHorizon(final, stats);
                            if (final != null) finals.Add(final);
                        }
                        else if (n.IsWaitCapable && child.Pose.DistanceTo(req.GoalX, req.GoalZ) <= config.goalRadiusMeters && !Threatened(child.Pose, child.Time, 0.5f))
                        {
                            // At the goal in a wait state: fill with the idle continuation instead of expanding idle chains.
                            var final = FillToHorizon(child, stats);
                            if (final != null) { finals.Add(final); stats.GoalFinals++; }
                        }
                        else
                        {
                            var bucketIndex = (int)Math.Floor((child.Time - req.StartTime) / bucketSeconds);
                            child.BeamKey = child.Cost.Total;
                            if (config.beamTimeNormalized)
                            {
                                var bucketEnd = req.StartTime + (bucketIndex + 1) * bucketSeconds;
                                var remaining = Math.Max(0f, Math.Min(bucketEnd, req.HorizonEnd) - child.Time);
                                child.BeamKey += config.wProgress * GoalDistance(child.Pose.X, child.Pose.Z) * remaining / horizonLength;
                            }

                            AddToBucket(buckets, child, bucketIndex);
                        }
                    }
                }
            }

            // Anytime candidates: when the search stops early, the best unexpanded states become complete plans by
            // appending their stop path and the idle continuation up to the horizon (validated like any other candidate).
            if (buckets.Count > 0)
            {
                var pending = new List<State>();
                foreach (var list in buckets.Values) pending.AddRange(list);
                pending.Sort((a, b) => a.Cost.Total != b.Cost.Total ? a.Cost.Total.CompareTo(b.Cost.Total) : b.Time.CompareTo(a.Time));
                var limit = Math.Max(4, req.MaxCandidates * 3);
                var taken = 0;
                foreach (var state in pending)
                {
                    if (taken >= limit) break;
                    var final = Finalize(state, stats);
                    if (final == null) continue;
                    final = FillToHorizon(final, stats);
                    if (final == null) continue;
                    finals.Add(final);
                    stats.PartialFinals++;
                    taken++;
                }
            }

            stats.Expansions = expansions;
            stats.CircleAccepts = circleAccepts;
            stats.ReservationRejects = reservationRejects;
            stats.Finals = finals.Count;
            result = SelectDiverse(finals, req.MaxCandidates);
            stats.Milliseconds = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        private static void AddToBucket(SortedDictionary<int, List<State>> buckets, State state, int key)
        {
            if (!buckets.TryGetValue(key, out var list)) { list = new List<State>(); buckets[key] = list; }
            list.Add(state);
        }

        private long DominanceKey(State state)
        {
            // terminal clip, 0.5 s time bucket, dominanceCellMeters grid, dominanceYawDeg yaw bin (Section 11.5; 0.10 m / 5 deg in the plan)
            var cell = Math.Max(0.01f, config.dominanceCellMeters);
            var yawBin = Math.Max(0.5f, config.dominanceYawDeg);
            var t = (long)Math.Floor(state.Time * 2f);
            var x = (long)Math.Floor(state.Pose.X / cell);
            var z = (long)Math.Floor(state.Pose.Z / cell);
            var yaw = (long)Math.Floor((state.Pose.YawDeg + 180f) / yawBin);
            long hash = state.Clip;
            hash = hash * 1000003L + (t & 0xFFFF);
            hash = hash * 1000003L + (x & 0xFFFF);
            hash = hash * 1000003L + (z & 0xFFFF);
            hash = hash * 1000003L + (yaw & 0xFF);
            return hash;
        }

        /// <summary>True when an external circle's prediction is within its exclusion distance (+ slack) of the pose at that time.</summary>
        private bool Threatened(Pose2 pose, float time, float slack)
        {
            foreach (var obstacle in request.Obstacles)
            {
                obstacle.Trajectory.PositionAt(time, out var ox, out var oz);
                var inflate = request.Radius + obstacle.Radius + config.dynamicKeepMeters + slack;
                var dx = pose.X - ox; var dz = pose.Z - oz;
                if (dx * dx + dz * dz < inflate * inflate) return true;
            }

            return false;
        }

        /// <summary>Cheap rank term: how close the successor's end pose comes to the external circles at its end time.</summary>
        private float ObstacleRank(Pose2 end, float endTime)
        {
            var rank = 0f;
            foreach (var obstacle in request.Obstacles)
            {
                obstacle.Trajectory.PositionAt(endTime, out var ox, out var oz);
                var inflate = request.Radius + obstacle.Radius + config.dynamicKeepMeters;
                var d = end.DistanceTo(ox, oz);
                if (d < inflate) rank += 3f; else if (d < inflate + 1f) rank += 1f * (inflate + 1f - d);
            }

            return rank;
        }

        private float CheapRank(State state, int next, bool far, float distanceStart)
        {
            var n = dataset.Clips[next];
            var end = state.Pose.Compose(n.EndPose);
            var distanceEnd = GoalDistance(end.X, end.Z);
            var obstacleRank = request.Obstacles.Count > 0 ? ObstacleRank(end, state.Time + n.Duration) : 0f;
            if (far)
            {
                var rank = -(distanceStart - distanceEnd) / Math.Max(0.1f, n.Duration);
                rank += 0.003f * HeadingError(end);
                rank += 0.003f * n.FacingErrorDeg * config.wFacing;   // strafing / backing up ranks below a forward walk of equal progress
                if (n.IsStationary) rank += 0.5f;
                if (state.IsRoot && next == request.OldNextClip) rank -= 0.3f;
                return rank + obstacleRank;
            }

            // Near the goal: prefer stopping and staying close.
            var near = n.AverageSpeed + distanceEnd;
            if (n.IsWaitCapable) near -= 1f;
            if (n.IsStationary) near -= 0.5f;
            return near + obstacleRank;
        }

        /// <summary>Appends the precomputed stop path so every candidate ends in a wait-capable node; the tail must be static-safe.</summary>
        private State Finalize(State leaf, SearchStats stats)
        {
            var c = dataset.Clips[leaf.Clip];
            if (c.IsWaitCapable) return leaf;
            var cursor = leaf;
            foreach (var next in c.StopPath)
            {
                var n = dataset.Clips[next];
                var part = EvaluateClip(cursor.Pose, cursor.Time, next, out var staticOk);
                if (!staticOk) { stats.StopTailRejects++; return null; }
                var cost = cursor.Cost.Clone();
                cost.Add(part);
                cursor = new State
                {
                    Clip = next,
                    Pose = cursor.Pose.Compose(n.EndPose),
                    Time = cursor.Time + n.Duration,
                    Cost = cost,
                    Parent = cursor,
                    Segment = new PlanSegment(next, cursor.Time, cursor.Time + n.Duration, cursor.Pose),
                    Depth = cursor.Depth + 1
                };
            }

            if (!dataset.Clips[cursor.Clip].IsWaitCapable) { stats.StopTailRejects++; return null; }
            return cursor;
        }

        /// <summary>Appends the stationary continuation of a wait-capable state until the horizon, accumulating cost; null when impossible or unsafe.</summary>
        private State FillToHorizon(State leaf, SearchStats stats)
        {
            var cursor = leaf;
            var guard = 0;
            while (cursor.Time < request.HorizonEnd - 1e-4f)
            {
                var next = dataset.Clips[cursor.Clip].StationaryContinuation;
                if (next < 0 || ++guard > 64) { stats.StopTailRejects++; return null; }
                var n = dataset.Clips[next];
                var part = EvaluateClip(cursor.Pose, cursor.Time, next, out var staticOk);
                if (!staticOk) { stats.StopTailRejects++; return null; }
                var cost = cursor.Cost.Clone();
                cost.Add(part);
                cursor = new State
                {
                    Clip = next,
                    Pose = cursor.Pose.Compose(n.EndPose),
                    Time = cursor.Time + n.Duration,
                    Cost = cost,
                    Parent = cursor,
                    Segment = new PlanSegment(next, cursor.Time, cursor.Time + n.Duration, cursor.Pose),
                    Depth = cursor.Depth + 1
                };
            }

            return cursor;
        }

        private List<PlanCandidate> SelectDiverse(List<State> finals, int maxCandidates)
        {
            var result = new List<PlanCandidate>();
            if (finals.Count == 0) return result;
            finals.Sort((a, b) => a.Cost.Total != b.Cost.Total ? a.Cost.Total.CompareTo(b.Cost.Total) : a.Time.CompareTo(b.Time));
            var seen = new HashSet<long>();
            var picked = new List<State>();
            // First pass: best per diversity cluster (Section 11.6).
            foreach (var state in finals)
            {
                var key = ClusterKey(state);
                if (seen.Add(key)) picked.Add(state);
                if (picked.Count >= maxCandidates) break;
            }

            // Second pass: fill by cost.
            foreach (var state in finals)
            {
                if (picked.Count >= maxCandidates) break;
                if (!picked.Contains(state)) picked.Add(state);
            }

            picked.Sort((a, b) => a.Cost.Total.CompareTo(b.Cost.Total));
            foreach (var state in picked)
            {
                var candidate = new PlanCandidate { Origin = "search", Cost = state.Cost };
                var chain = new List<PlanSegment>();
                for (var s = state; s != null && !s.IsRoot; s = s.Parent) chain.Add(s.Segment);
                chain.Reverse();
                candidate.Suffix = chain;
                candidate.Cost.Heading += HeadingCost(state.Pose);
                result.Add(candidate);
            }

            result.Sort((a, b) => a.TotalCost.CompareTo(b.TotalCost));
            return result;
        }

        private long ClusterKey(State state)
        {
            // first suffix clip, displacement direction after ~2 s, mid-horizon 1 m cell, stationary-at-end class
            State first = null;
            State mid = null;
            var midTime = request.StartTime + horizonLength * 0.5f;
            for (var s = state; s != null && !s.IsRoot; s = s.Parent)
            {
                first = s;
                if (s.Time >= midTime) mid = s;
            }

            var firstClip = first?.Clip ?? -1;
            var twoSeconds = request.StartTime + 2f;
            var p = PoseAlong(state, twoSeconds);
            var dx = p.X - request.StartPose.X; var dz = p.Z - request.StartPose.Z;
            var bin = dx * dx + dz * dz < 0.04f ? 8 : (int)Math.Floor((Math.Atan2(dx, dz) + Math.PI) / (Math.PI / 4.0)) & 7;
            var m = mid ?? state;
            var cx = (long)Math.Floor(m.Pose.X);
            var cz = (long)Math.Floor(m.Pose.Z);
            var stationaryEnd = dataset.Clips[state.Clip].IsStationary ? 1 : 0;
            return (((firstClip * 16L + bin) * 4096L + (cx & 0xFFF)) * 4096L + (cz & 0xFFF)) * 2L + stationaryEnd;
        }

        private Pose2 PoseAlong(State state, float time)
        {
            var chain = new List<PlanSegment>();
            for (var s = state; s != null && !s.IsRoot; s = s.Parent) chain.Add(s.Segment);
            chain.Reverse();
            return chain.Count == 0 ? request.StartPose : AgentPlan.PoseAt(dataset, chain, time);
        }
    }
}
