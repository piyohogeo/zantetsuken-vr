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
        /// <summary>
        /// Evaluation (PlannerConfig.workLimitsOnly): no decision reads the clock -- nothing has elapsed, the whole limit remains and it
        /// never expires; what ends the work is the counted limits alone (expansions, beam width, candidates, regeneration rounds,
        /// assignment sweeps). The time itself is still measured (MeasuredMilliseconds).
        /// </summary>
        public readonly bool WorkLimitsOnly;

        public Deadline(double limitMilliseconds, bool workLimitsOnly = false) { LimitMilliseconds = limitMilliseconds; WorkLimitsOnly = workLimitsOnly; }
        public double ElapsedMilliseconds => WorkLimitsOnly ? 0.0 : watch.Elapsed.TotalMilliseconds;
        public double RemainingMilliseconds => WorkLimitsOnly ? LimitMilliseconds : LimitMilliseconds - watch.Elapsed.TotalMilliseconds;
        public bool Expired => !WorkLimitsOnly && watch.Elapsed.TotalMilliseconds >= LimitMilliseconds;
        public double MeasuredMilliseconds => watch.Elapsed.TotalMilliseconds;
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
        /// <summary>PROP-OBSTACLE-1: the part of DynamicOverlapSeconds that is overlap with the prop fragments' map (its cost is in Dynamic).</summary>
        public float PropOverlapSeconds;
        /// <summary>PLANNER-LOCAL-STAY: the seconds of the track that stay in a small place (PlannerConfig.localStayCost) and their cost.</summary>
        public float LocalStay;
        public float LocalStaySeconds;
        // FOR A RECORD ONLY (not in Total): of the progress cost's clip samples, the seconds whose distance was a patch's estimate; of
        // those the seconds where the estimate lies more than 1 m below the nearest connected ground's value (FieldRecovery's gap),
        // and what the progress cost would have been higher by had those samples taken that value instead.
        public float EstimateSeconds, EstimateGapSeconds, EstimateGapCost;
        public float Total => Progress + Heading + Speed + Idle + Turn + Facing + Churn + OtherAgent + Penalty + Dynamic + LocalStay;

        public CostBreakdown Clone() => (CostBreakdown)MemberwiseClone();

        public void Reset()
        {
            EstimateSeconds = 0f; EstimateGapSeconds = 0f; EstimateGapCost = 0f;
            Progress = 0f; Heading = 0f; Speed = 0f; Idle = 0f; Turn = 0f; Facing = 0f; Churn = 0f; OtherAgent = 0f; Penalty = 0f; Dynamic = 0f; DynamicOverlapSeconds = 0f; PropOverlapSeconds = 0f; LocalStay = 0f; LocalStaySeconds = 0f;
        }

        public void Add(CostBreakdown other)
        {
            Progress += other.Progress; Heading += other.Heading; Speed += other.Speed; Idle += other.Idle; Turn += other.Turn; Facing += other.Facing; Churn += other.Churn; OtherAgent += other.OtherAgent; Penalty += other.Penalty;
            Dynamic += other.Dynamic; DynamicOverlapSeconds += other.DynamicOverlapSeconds; PropOverlapSeconds += other.PropOverlapSeconds;
            LocalStay += other.LocalStay; LocalStaySeconds += other.LocalStaySeconds;
            EstimateSeconds += other.EstimateSeconds; EstimateGapSeconds += other.EstimateGapSeconds; EstimateGapCost += other.EstimateGapCost;
        }

        public string Describe() => $"total {Total:F2} = prog {Progress:F2} head {Heading:F2} spd {Speed:F2} idle {Idle:F2} turn {Turn:F2} facing {Facing:F2} churn {Churn:F2} other {OtherAgent:F2}{(Dynamic > 0f ? $" dyn {Dynamic:F1} ({DynamicOverlapSeconds:F1} s{(PropOverlapSeconds > 0f ? $", props {PropOverlapSeconds:F1} s" : "")})" : "")}{(Penalty > 0f ? $" PENALTY {Penalty:F0}" : "")}";
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
        // Evaluation (PLANNER-LOCAL-STAY): the search's states by whether they get away from the search's start -- "advancing":
        // at least 0.5 m and 0.3 m/s from it -- or stay about it ("local"): how many were made, lost to the static check, cut by
        // the beam (its width or the per-clip limit), lost to dominance, and reached the search's end; the best cost of each there.
        public int AdvEvaluated, LocEvaluated, AdvStaticRejects, LocStaticRejects, AdvBeamPruned, LocBeamPruned, AdvDominancePruned, LocDominancePruned, AdvFinals, LocFinals;
        /// <summary>States that reached the search's end BEFORE their stop path and wait tail were checked (AdvFinals / LocFinals count the completed ones, with their best cost).</summary>
        public int AdvReachedEnd, LocReachedEnd;
        /// <summary>PLANNER-LOCAL-STAY: clips evaluated with no track prepared for their start (not judged for a stay). 0 unless something overwrote the shared track.</summary>
        public int LocalStayUnjudgedClips;
        /// <summary>Seconds of the progress cost's samples, over every clip this search evaluated, whose distance to the goal was the straight line for want of a field path and a way back / a recovery patch's connected value / a patch's estimate (PLANNER-FIELD-RECOVERY).</summary>
        public float StraightFallbackSeconds, PatchSeconds, EstimateSeconds;
        /// <summary>
        /// States the beam met whose chain has samples on an estimate more than 1 m below the nearest connected ground (CostBreakdown
        /// .EstimateGapSeconds > 0): kept / cut, of each the advancing ones, and the sums of what their progress cost would have been
        /// higher by; the same for the states WITHOUT such samples that the beam cut while it kept one with.
        /// </summary>
        public int GapKept, GapKeptAdv, GapCut, GapCutAdv, CleanCutWhileGapKept, CleanCutAdvWhileGapKept;
        public float GapKeptCost, GapCutCost, GapKeptCostMost;
        /// <summary>Of those: kept states with such samples whose key would have stood above the best cut state's key without the gap's saving (the gap decided that they were kept).</summary>
        public int GapKeptOnlyByGap;
        /// <summary>Of GapKept / GapCut: states whose bucket correction read a gapped estimate. Cheap ranks that read one at a successor's end, and how many of those successors were taken for a full evaluation.</summary>
        public int KeyGapKept, KeyGapCut, RankGapReads, RankGapChosen;
        /// <summary>With SearchRequest.Follow: what became of the states along that plan (FollowTraceHeader).</summary>
        public List<string> FollowTrace;
        /// <summary>
        /// A record only: states expanded within goalRadiusMeters of where arriving is judged, and of them those taken as "far" all
        /// the same because Threatened(pose, time, 0.5) holds (an external circle, or the prop cells within radius + keep + 0.5 m);
        /// wait-capable successors ending within that radius, and of them those refused the early "waits at its goal" candidate for
        /// the same reason.
        /// </summary>
        public int InReachExpansions, InReachThreatened, InReachWaits, InReachWaitsThreatened;
        public float BestAdvFinal = float.NaN, BestLocFinal = float.NaN;
        /// <summary>With SearchRequest.Trace: a line per time bucket -- kept and cut by class, the best kept key, the best cut advancing key.</summary>
        public List<string> Trace;
        /// <summary>
        /// With SearchRequest.Trace: per time bucket, the best few states kept and cut of each class with their cost terms, the key the
        /// beam ranked them by (the cost, and what the time-bucket correction added), where they are, where the field guides from
        /// there, and for the clip that made them the distance to the goal used at every sample with where it came from
        /// (SingleAgentSearch.StateTraceHeader).
        /// </summary>
        public List<string> StateTrace;

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
        /// <summary>PROP-OBSTACLE-1: the version of the prop fragments' map this cycle took (null: none); soft cost only, as the circles.</summary>
        public PropObstacleMap Props;
        /// <summary>PLANNER-LOCAL-STAY: the track before StartTime (time, x, z; ascending); null: unknown.</summary>
        public float[] HistoryT, HistoryX, HistoryZ;
        /// <summary>Evaluation: keep SearchStats.Trace.</summary>
        public bool Trace;
        /// <summary>PROP-GOAL-REACH: "at the goal" is judged against this point (the goal proper); GoalX/GoalZ is where the plan walks to.</summary>
        public bool HasArrive;
        public float ArriveX, ArriveZ;
        /// <summary>Evaluation: the clips of a plan to report on (SearchStats.FollowTrace); null: none.</summary>
        public List<int> Follow;
        /// <summary>PLANNER-FIELD-RECOVERY: the NPC's recovery patch of this cycle (null: the option is off).</summary>
        public FieldRecoveryHolder Recovery;
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
            // evaluation (SearchRequest.Trace): what the bucket correction added to the key and the distance it used, the seconds of the
            // chain's progress cost by where the distance came from, and the last clip's samples
            public float KeyAdjust, KeyDistance;
            /// <summary>A record only: what the beam key's bucket correction is lower by because it read an estimate more than 1 m below the nearest connected ground.</summary>
            public float KeyGapSaving;
            /// <summary>Evaluation: the chain so far is the first clips of SearchRequest.Follow.</summary>
            public bool OnFollow;
            public char KeySource;
            public float[] SourceSeconds;
            public string Samples;
        }

        /// <summary>
        /// Where a distance to the goal used by the cost came from: E no field (straight line); A the field's own cell (a path longer than
        /// the straight line by the margin); a the field's own cell, the goal "in view" (straight line used); N a neighbouring cell's
        /// value (the point's own cell has no path, or is closed and was seeded about the goal); n the same, in view (straight line);
        /// P the recovery patch (the walk to the field + the field's rest); S no path and no way back (straight line);
        /// X no path and no way back, inside a patch: the patch's estimate for the cost (a connected cell's value + the distance to it).
        /// </summary>
        public const string SourceCodes = "EAaNnPSX";
        public const string FollowTraceHeader = "what,depth,clip,state_time,detail";

        /// <summary>Evaluation: the distances the cheap rank reads for a successor -- at the state and at the successor's end -- with their sources and the estimate's gap there.</summary>
        private string FollowDistances(State state, int next)
        {
            var end = state.Pose.Compose(dataset.Clips[next].EndPose);
            var startDistance = GoalDistance(state.Pose.X, state.Pose.Z, out var startSource);
            var endDistance = GoalDistance(end.X, end.Z, out var endSource);
            var patch = request.Recovery?.Patch;
            return FormattableString.Invariant($"start {state.Pose.X:F2} {state.Pose.Z:F2} distance {startDistance:F2}{startSource} gap {(patch != null ? patch.EstimateGapAt(state.Pose.X, state.Pose.Z) : 0f):F2} end {end.X:F2} {end.Z:F2} distance {endDistance:F2}{endSource} gap {(patch != null ? patch.EstimateGapAt(end.X, end.Z) : 0f):F2} duration {dataset.Clips[next].Duration:F2}");
        }

        private void FollowNote(SearchStats stats, string what, int depth, int clip, float time, string detail)
        {
            if (stats.FollowTrace == null) stats.FollowTrace = new List<string>();
            var c = clip >= 0 ? dataset.Clips[clip] : null;
            stats.FollowTrace.Add(FormattableString.Invariant($"{what},{depth},{(c != null ? (c.Category + "/" + c.DisplayName).Replace(',', ';').Replace(' ', '_') : "root")},{time:F2},{detail}"));
        }
        public const string StateTraceHeader = "bucket,status,class,rank,depth,clip,end_time,x,z,yaw,away_m,cost_total,progress,heading,speed,idle,turn,facing,churn,other,penalty,dynamic,local_stay,beam_key,key_adjust,key_distance,key_source,guided,guide_x,guide_z,guide_bearing,heading_error_to_guide,s_E,s_A,s_a,s_N,s_n,s_P,s_S,s_X,overlap_s,prop_overlap_s,estimate_s,estimate_gap_s,estimate_gap_cost,samples";
        private readonly List<float> traceT = new List<float>(), traceD = new List<float>(), traceDt = new List<float>();
        private readonly List<char> traceS = new List<char>();
        private readonly List<float> traceG = new List<float>();
        private readonly HashSet<State> keptSetScratch = new HashSet<State>();
        private readonly HashSet<int> rankGapClips = new HashSet<int>();
        private int rankGapReads, rankGapChosen;

        private readonly ClipDataset dataset;
        private readonly WalkableMap map;
        private SearchRequest request;
        private PlannerConfig config;
        private float required;
        private float horizonLength;
        private readonly List<float> sampleX = new List<float>(512);
        private readonly List<float> sampleZ = new List<float>(512);
        private readonly List<float> sampleT = new List<float>(512);
        // PLANNER-LOCAL-STAY: the track before the clip being evaluated (time, x, z ascending, ending at trailEnd), made by
        // PrepareTrail for a state or a segment list; EvaluateClip uses it only when it ends where the clip starts.
        private float[] trailT = new float[128], trailX = new float[128], trailZ = new float[128];
        private int trailCount;
        private float trailEnd = float.NaN;
        private float fallbackSeconds, patchSeconds, estimateSeconds;   // of the progress cost's samples in this search, by where the distance came from
        private object trailFor;          // the state the shared track was made for (null: a segment list's, or none)
        private int localStayUnjudged;
        private readonly List<State> chainScratch = new List<State>();
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
            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                if (!config.staticCheckWholePlan && segment.StartTime >= req.HorizonEnd) break;
                if (config.localStayCost && !dataset.Clips[segment.Clip].IsStationary) PrepareTrail(segments, index);
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

        private void TrailAdd(float t, float x, float z)
        {
            if (trailCount == trailT.Length) { Array.Resize(ref trailT, trailCount * 2); Array.Resize(ref trailX, trailCount * 2); Array.Resize(ref trailZ, trailCount * 2); }
            trailT[trailCount] = t; trailX[trailCount] = x; trailZ[trailCount] = z; trailCount++;
        }

        private void TrailHistory(float from, float before)
        {
            var t = request.HistoryT;
            if (t == null) return;
            for (var i = 0; i < t.Length; i++) if (t[i] >= from && t[i] < before - 1e-3f) TrailAdd(t[i], request.HistoryX[i], request.HistoryZ[i]);
        }

        private void TrailSegment(PlanSegment segment, float from)
        {
            var step = 1f / Math.Max(1f, config.collisionSampleHz);
            for (var time = Math.Max(segment.StartTime, from); time < segment.EndTime - 1e-3f; time += step)
            {
                var pose = segment.WorldStart.Compose(dataset.LocalPoseAt(segment.Clip, time - segment.StartTime));
                TrailAdd(time, pose.X, pose.Z);
            }
        }

        /// <summary>The track up to a state's end: the request's history, then the segments of the state's chain, within the window.</summary>
        private void PrepareTrail(State state)
        {
            trailCount = 0; trailEnd = state.Time; trailFor = state;
            if (!config.localStayCost) return;
            var from = state.Time - config.localStayWindowSeconds - 1.2f;
            chainScratch.Clear();
            for (var s = state; s != null && !s.IsRoot; s = s.Parent)
            {
                chainScratch.Add(s);
                if (s.Segment.StartTime <= from) break;
            }

            var oldest = chainScratch.Count > 0 ? chainScratch[chainScratch.Count - 1].Segment.StartTime : state.Time;
            TrailHistory(from, oldest);
            for (var i = chainScratch.Count - 1; i >= 0; i--) TrailSegment(chainScratch[i].Segment, from);
            TrailAdd(state.Time, state.Pose.X, state.Pose.Z);
        }

        /// <summary>The track up to the start of segments[upTo]: the request's history, then the segments before it.</summary>
        private void PrepareTrail(IReadOnlyList<PlanSegment> segments, int upTo)
        {
            trailCount = 0; trailEnd = segments[upTo].StartTime; trailFor = null;
            if (!config.localStayCost) return;
            var from = trailEnd - config.localStayWindowSeconds - 1.2f;
            TrailHistory(from, segments[0].StartTime);
            for (var i = 0; i < upTo; i++) if (segments[i].EndTime > from) TrailSegment(segments[i], from);
            TrailAdd(trailEnd, segments[upTo].WorldStart.X, segments[upTo].WorldStart.Z);
        }

        /// <summary>
        /// The seconds of the clip just sampled (sampleT/X/Z) that stay in a small place: for each sample, over the window before it
        /// (the trail, then the clip's own samples), the net displacement is small and nothing is far. A sample without a whole
        /// window of track before it is not judged.
        /// </summary>
        private float LocalStaySeconds()
        {
            if (trailCount == 0 || Math.Abs(trailEnd - sampleT[0]) > 0.02f) { localStayUnjudged++; return 0f; }   // no track prepared for this start
            var window = config.localStayWindowSeconds;
            var net2 = config.localStayNetMeters * config.localStayNetMeters;
            var far2 = config.localStayMaxSeparationMeters * config.localStayMaxSeparationMeters;
            var goalRadius2 = config.goalRadiusMeters * config.goalRadiusMeters;
            var seconds = 0f;
            for (var j = 1; j < sampleT.Count; j++)
            {
                var t = sampleT[j];
                if (t > request.HorizonEnd) break;
                var x = sampleX[j]; var z = sampleZ[j];
                if ((x - request.GoalX) * (x - request.GoalX) + (z - request.GoalZ) * (z - request.GoalZ) <= goalRadius2) continue;
                var from = t - window;
                var oldestT = t; var oldestX = x; var oldestZ = z;
                var far = false;
                for (var k = j - 1; k >= 0 && !far; k--)
                {
                    if (sampleT[k] < from) break;
                    var dx = sampleX[k] - x; var dz = sampleZ[k] - z;
                    if (dx * dx + dz * dz > far2) far = true;
                    oldestT = sampleT[k]; oldestX = sampleX[k]; oldestZ = sampleZ[k];
                }

                if (!far && sampleT[0] >= from)
                {
                    for (var k = trailCount - 1; k >= 0; k--)
                    {
                        if (trailT[k] < from) break;
                        var dx = trailX[k] - x; var dz = trailZ[k] - z;
                        if (dx * dx + dz * dz > far2) { far = true; break; }
                        oldestT = trailT[k]; oldestX = trailX[k]; oldestZ = trailZ[k];
                    }
                }

                if (far || oldestT > from + 1.05f) continue;   // went far within the window, or less than the window is known
                if ((oldestX - x) * (oldestX - x) + (oldestZ - z) * (oldestZ - z) <= net2) seconds += t - sampleT[j - 1];
            }

            return seconds;
        }

        /// <summary>Evaluation: one state of a traced search as the beam met it (StateTraceHeader).</summary>
        private void TraceState(SearchStats stats, int bucket, State state, bool advancing, string status, int rank)
        {
            if (stats.StateTrace == null) stats.StateTrace = new List<string>();
            var k = state.Cost;
            var c = state.IsRoot ? null : dataset.Clips[state.Clip];
            var name = c != null ? (c.Category + "/" + c.DisplayName).Replace(',', ';').Replace(' ', '_') : "root";
            var gx = request.GoalX; var gz = request.GoalZ;
            var guided = request.Field != null && FieldGuide(state.Pose, out gx, out gz);
            var bearing = (float)Math.Atan2(gx - state.Pose.X, gz - state.Pose.Z) * Pose2.Rad2Deg;
            var s = state.SourceSeconds ?? new float[SourceCodes.Length];
            stats.StateTrace.Add(FormattableString.Invariant($"{bucket},{status},{(advancing ? "advancing" : "local")},{rank},{state.Depth},{name},{state.Time:F2},{state.Pose.X:F2},{state.Pose.Z:F2},{state.Pose.YawDeg:F0},{state.Pose.DistanceTo(request.StartPose.X, request.StartPose.Z):F2},")
                + FormattableString.Invariant($"{k.Total:F3},{k.Progress:F3},{k.Heading:F3},{k.Speed:F3},{k.Idle:F3},{k.Turn:F3},{k.Facing:F3},{k.Churn:F3},{k.OtherAgent:F3},{k.Penalty:F1},{k.Dynamic:F3},{k.LocalStay:F3},{state.BeamKey:F3},{state.KeyAdjust:F3},{state.KeyDistance:F2},{(state.KeySource == 0 ? '-' : state.KeySource)},")
                + FormattableString.Invariant($"{(guided ? 1 : 0)},{gx:F2},{gz:F2},{bearing:F0},{state.Pose.HeadingErrorTo(gx, gz):F0},{s[0]:F1},{s[1]:F1},{s[2]:F1},{s[3]:F1},{s[4]:F1},{s[5]:F1},{s[6]:F1},{s[7]:F1},{k.DynamicOverlapSeconds:F2},{k.PropOverlapSeconds:F2},{k.EstimateSeconds:F2},{k.EstimateGapSeconds:F2},{k.EstimateGapCost:F3},{state.Samples ?? string.Empty}"));
        }

        /// <summary>Evaluation: whether a pose at a time got away from the search's start (0.5 m at least, 0.3 m/s at least).</summary>
        private bool Advancing(Pose2 pose, float time)
        {
            var d = pose.DistanceTo(request.StartPose.X, request.StartPose.Z);
            return d >= Math.Max(0.5f, 0.3f * (time - request.StartTime));
        }

        private float ArriveDistance(Pose2 pose) => request.HasArrive ? pose.DistanceTo(request.ArriveX, request.ArriveZ) : pose.DistanceTo(request.GoalX, request.GoalZ);

        private void Bind(SearchRequest req)
        {
            request = req;
            config = req.Config;
            required = req.Radius + config.staticMarginMeters;
            horizonLength = Math.Max(0.5f, req.HorizonEnd - req.StartTime);
        }

        /// <summary>The goal counts as "in view" when the field's path is barely longer than the straight line; then the exact straight line is used (the 8-connected field over-estimates by up to 8% and quantises directions).</summary>
        private const float FieldDetourMeters = 0.5f;

        /// <summary>
        /// PLANNER-FIELD-RECOVERY: this NPC's patch of the way back to the field, made at the first place without a field path (about
        /// the search's start; walkable = FieldRecovery.WalkRule: the static clearance the search itself asks and the prop cells at
        /// radius + keep, relaxed only about an NPC that already stands nearer). Null: off, or no way back within reach.
        /// </summary>
        private FieldRecovery Recovery()
        {
            var holder = request.Recovery;
            if (holder == null) return null;
            if (!holder.Tried)
            {
                holder.Tried = true;
                var rule = FieldRecovery.WalkRule(map, required, request.Props, request.Radius + config.propKeepMeters, request.StartPose.X, request.StartPose.Z, config.goalFieldRecoveryEscapeMeters, out var overlapping);
                holder.Overlapping = overlapping;
                holder.Patch = new FieldRecovery(request.Field, request.StartPose.X, request.StartPose.Z, config.goalFieldRecoveryMeters, rule);
            }

            return holder.Patch != null && holder.Patch.Sources > 0 ? holder.Patch : null;
        }

        /// <summary>
        /// The distance to the goal for the cost: the field's path where it is longer than the straight line by the detour margin,
        /// else the straight line; where the field has NO path at the point, the way back to the field + the field's remaining
        /// distance when a patch gives one (PLANNER-FIELD-RECOVERY), else the straight line as before.
        /// </summary>
        private float FieldDistance(float x, float z, float euclid) => FieldDistance(x, z, euclid, out _);

        private float FieldDistance(float x, float z, float euclid, out char source)
        {
            var path = request.Field.PathDistanceAt(x, z);
            if (!float.IsPositiveInfinity(path))
            {
                var detour = path - euclid > FieldDetourMeters;
                // the source is told apart for a traced search only (one more read of the field)
                source = !request.Trace ? 'A' : !float.IsPositiveInfinity(request.Field.CellPath(x, z)) ? (detour ? 'A' : 'a') : (detour ? 'N' : 'n');
                return detour ? path : euclid;
            }

            var patch = Recovery();
            if (patch != null && patch.TryDistance(x, z, out var back)) { source = 'P'; return back; }
            // for the cost only: beside the way back, the patch's estimate, not the straight line
            if (patch != null && config.goalFieldRecoveryExtrapolate && patch.TryEstimate(x, z, out var estimated, out _, out _, out _)) { source = 'X'; return estimated; }
            source = 'S';
            return euclid;
        }

        /// <summary>Path distance to the goal (field, when the straight line is blocked) or straight line.</summary>
        private float GoalDistance(float x, float z) => GoalDistance(x, z, out _);

        private float GoalDistance(float x, float z, out char source)
        {
            var dx = x - request.GoalX; var dz = z - request.GoalZ;
            var euclid = (float)Math.Sqrt(dx * dx + dz * dz);
            source = 'E';
            if (request.Field == null) return euclid;
            return FieldDistance(x, z, euclid, out source);
        }

        /// <summary>For a test or a record: the distance to the goal the cost would use at a point of this request, and where it came from (E A a N n P X S).</summary>
        public float GoalDistanceFor(SearchRequest req, float x, float z, out char source)
        {
            Bind(req);
            return GoalDistance(x, z, out source);
        }

        /// <summary>Whether the field guides from a pose, and to which point: its next waypoint when the goal is around a corner, or the way back to the field where it has no path there.</summary>
        private bool FieldGuide(Pose2 pose, out float x, out float z)
        {
            x = request.GoalX; z = request.GoalZ;
            var euclid = pose.DistanceTo(request.GoalX, request.GoalZ);
            var path = request.Field.PathDistanceAt(pose.X, pose.Z);
            if (float.IsPositiveInfinity(path))
            {
                var patch = Recovery();
                return patch != null && patch.TryGuide(pose.X, pose.Z, config.goalFieldLookAheadMeters, out x, out z);
            }

            if (path - euclid <= FieldDetourMeters) return false;
            request.Field.Waypoint(pose.X, pose.Z, config.goalFieldLookAheadMeters, out x, out z);
            return true;
        }

        /// <summary>Point the agent should face from `pose`: the goal, or the field's next waypoint when the goal is around a corner.</summary>
        private void HeadingTarget(Pose2 pose, out float x, out float z)
        {
            x = request.GoalX; z = request.GoalZ;
            if (request.Field == null) return;
            if (!FieldGuide(pose, out var gx, out var gz)) return;
            x = gx; z = gz;
        }

        /// <summary>Heading error towards the goal, or towards the field's next waypoint when the goal is around a corner.</summary>
        private float HeadingError(Pose2 pose)
        {
            if (request.Field == null) return pose.HeadingErrorTo(request.GoalX, request.GoalZ);
            return FieldGuide(pose, out var wx, out var wz) ? pose.HeadingErrorTo(wx, wz) : pose.HeadingErrorTo(request.GoalX, request.GoalZ);
        }

        private float HeadingCost(Pose2 end)
        {
            var far = ArriveDistance(end) > config.goalRadiusMeters;
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
            sampleT.Clear();
            if (request.Trace) { traceT.Clear(); traceD.Clear(); traceDt.Clear(); traceS.Clear(); traceG.Clear(); }
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
            // PLANNER-IDLE-TURN: a clip that hardly translates stands, for the idle cost (its facing and heading terms stay as they were)
            var lowTranslation = config.idleCostLowTranslation && !c.IsStationary && c.AverageSpeed < config.idleCostSpeedMps;
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
                sampleX.Add(wx); sampleZ.Add(wz); sampleT.Add(startTime + dataset.T[index]);
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
                        var cameFrom = 'E';
                        if (request.Field != null) distance = FieldDistance(wx, wz, euclid, out cameFrom);
                        var estimateGap = 0f;
                        if (cameFrom == 'X')
                        {
                            // a record only: nothing below reads it
                            cost.EstimateSeconds += stepDt;
                            estimateGap = request.Recovery?.Patch?.EstimateGapAt(wx, wz) ?? 0f;
                            if (estimateGap > 1f) { cost.EstimateGapSeconds += stepDt; cost.EstimateGapCost += config.wProgress * estimateGap * stepDt / horizonLength; }
                        }

                        if (request.Trace) { traceT.Add(time); traceD.Add(distance); traceDt.Add(stepDt); traceS.Add(cameFrom); traceG.Add(estimateGap); }
                        if (cameFrom == 'S') fallbackSeconds += stepDt; else if (cameFrom == 'P') patchSeconds += stepDt; else if (cameFrom == 'X') estimateSeconds += stepDt;
                        cost.Progress += config.wProgress * distance * stepDt / horizonLength;
                        if ((request.HasArrive ? (float)Math.Sqrt((wx - request.ArriveX) * (wx - request.ArriveX) + (wz - request.ArriveZ) * (wz - request.ArriveZ)) : euclid) > goalRadius)
                        {
                            var vx = (wx - previousX) / stepDt; var vz = (wz - previousZ) / stepDt;
                            var speed = (float)Math.Sqrt(vx * vx + vz * vz);
                            cost.Speed += config.wSpeed * Math.Abs(speed - config.desiredSpeedMps) * stepDt / horizonLength;
                            if (c.IsStationary || lowTranslation) stationarySeconds += stepDt;
                            if (!c.IsStationary)
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
            // PLANNER-LOCAL-STAY: the seconds of this clip's track that stay in a small place, judged across the clips before it
            if (config.localStayCost && !c.IsStationary)
            {
                var staying = LocalStaySeconds();
                if (staying > 0f) { cost.LocalStaySeconds += staying; cost.LocalStay += config.wLocalStay * staying; }
            }
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

            // PROP-OBSTACLE-1: the prop fragments' map, the same way -- predicted seconds inside the occupied cells (the NPC radius and
            // the keep counted once against the map's distance, which is not inflated) are a soft cost; the map does not move in
            // the prediction (the version this cycle took).
            var props = request.Props;
            if (props != null)
            {
                var inflate = request.Radius + config.propKeepMeters;
                if (props.BoxNear(minX, maxX, minZ, maxZ, inflate))
                {
                    var endTime = startTime + c.Duration;
                    var stepT = 1f / config.collisionSampleHz;
                    var seconds = 0f;
                    for (var t = startTime; t <= endTime; t += stepT)
                    {
                        var pose = start.Compose(dataset.LocalPoseAt(clip, t - startTime));
                        if (props.DistanceAt(pose.X, pose.Z) < inflate) seconds += stepT;
                    }

                    if (seconds > 0f)
                    {
                        cost.DynamicOverlapSeconds += seconds;
                        cost.PropOverlapSeconds += seconds;
                        cost.Dynamic += config.wPropObstacle * seconds;
                    }
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
            localStayUnjudged = 0;
            fallbackSeconds = 0f; patchSeconds = 0f; estimateSeconds = 0f;
            rankGapReads = 0; rankGapChosen = 0;
            var watch = Stopwatch.StartNew();
            var result = new List<PlanCandidate>();
            var startClip = dataset.Clips[req.StartClip];
            var root = new State { Clip = req.StartClip, Pose = req.StartPose, Time = req.StartTime, Cost = new CostBreakdown(), Depth = 0, IsRoot = true, OnFollow = req.Follow != null };
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
                int keptAdv = 0, keptLoc = 0, cutAdv = 0, cutLoc = 0;
                float bestKept = float.NaN, bestKeptAdv = float.NaN, bestCutAdv = float.NaN;
                int beamRank = 0;
                foreach (var state in bucket)
                {
                    var advancing = state.IsRoot ? false : Advancing(state.Pose, state.Time);
                    beamRank++;
                    if (kept.Count >= (req.BeamWidth > 0 ? req.BeamWidth : config.beamWidth))
                    {
                        stats.BeamPruned++;
                        if (advancing) { stats.AdvBeamPruned++; cutAdv++; if (float.IsNaN(bestCutAdv)) bestCutAdv = state.BeamKey; } else { stats.LocBeamPruned++; cutLoc++; }
                        if (req.Trace && (advancing ? cutAdv <= 3 : cutLoc <= 1)) TraceState(stats, firstKey, state, advancing, "cut-width", beamRank);
                        continue;
                    }

                    perClip.TryGetValue(state.Clip, out var count);
                    if (count >= config.maxStatesPerClipPerBucket)
                    {
                        stats.BeamPruned++;
                        if (advancing) { stats.AdvBeamPruned++; cutAdv++; if (float.IsNaN(bestCutAdv)) bestCutAdv = state.BeamKey; } else { stats.LocBeamPruned++; cutLoc++; }
                        if (req.Trace && (advancing ? cutAdv <= 3 : cutLoc <= 1)) TraceState(stats, firstKey, state, advancing, "cut-per-clip", beamRank);
                        continue;
                    }

                    var key = DominanceKey(state);
                    if (dominance.TryGetValue(key, out var best) && best < state.Cost.Total - 1e-4f) { stats.DominancePruned++; if (advancing) stats.AdvDominancePruned++; else stats.LocDominancePruned++; continue; }
                    dominance[key] = state.Cost.Total;
                    perClip[state.Clip] = count + 1;
                    kept.Add(state);
                    if (advancing) { keptAdv++; if (float.IsNaN(bestKeptAdv)) bestKeptAdv = state.BeamKey; } else keptLoc++;
                    if (req.Trace && (advancing ? keptAdv <= 3 : keptLoc <= 3)) TraceState(stats, firstKey, state, advancing, "kept", beamRank);
                    if (float.IsNaN(bestKept)) bestKept = state.BeamKey;
                }

                {
                    // a record only: the states with samples on a gapped estimate, kept and cut (the bucket is sorted by key; kept is in that order)
                    var gapKeptHere = 0; var worstKeptKey = float.NegativeInfinity; var bestCutKey = float.PositiveInfinity;
                    var keptSet = keptSetScratch; keptSet.Clear();
                    foreach (var state in kept) { keptSet.Add(state); if (state.BeamKey > worstKeptKey) worstKeptKey = state.BeamKey; }
                    foreach (var state in bucket) if (!keptSet.Contains(state) && state.BeamKey < bestCutKey) bestCutKey = state.BeamKey;
                    foreach (var state in kept)
                    {
                        var saving = state.Cost.EstimateGapCost + state.KeyGapSaving;   // the clips' samples and the bucket correction
                        if (state.IsRoot || saving <= 0f) continue;
                        gapKeptHere++;
                        stats.GapKept++; if (Advancing(state.Pose, state.Time)) stats.GapKeptAdv++;
                        if (state.KeyGapSaving > 0f) stats.KeyGapKept++;
                        stats.GapKeptCost += saving; if (saving > stats.GapKeptCostMost) stats.GapKeptCostMost = saving;
                        if (state.BeamKey + saving > bestCutKey) stats.GapKeptOnlyByGap++;
                    }

                    if (req.Follow != null)
                    {
                        var place = 0;
                        foreach (var state in bucket)
                        {
                            place++;
                            if (!state.OnFollow || state.IsRoot) continue;
                            var isKept = keptSet.Contains(state);
                            FollowNote(stats, isKept ? "beam-kept" : "beam-not-kept", state.Depth, state.Clip, state.Time, FormattableString.Invariant($"bucket {firstKey} place {place} of {bucket.Count} kept {kept.Count} key {state.BeamKey:F3} worst-kept-key {worstKeptKey:F3} best-not-kept-key {bestCutKey:F3} total {state.Cost.Total:F3} progress {state.Cost.Progress:F3} dynamic {state.Cost.Dynamic:F3} prop-overlap-s {state.Cost.PropOverlapSeconds:F2} estimate-s {state.Cost.EstimateSeconds:F2} at {state.Pose.X:F2} {state.Pose.Z:F2}"));
                        }
                    }

                    foreach (var state in bucket)
                    {
                        if (state.IsRoot || keptSet.Contains(state)) continue;
                        var adv = Advancing(state.Pose, state.Time);
                        var saving = state.Cost.EstimateGapCost + state.KeyGapSaving;
                        if (saving > 0f) { stats.GapCut++; if (adv) stats.GapCutAdv++; stats.GapCutCost += saving; if (state.KeyGapSaving > 0f) stats.KeyGapCut++; }
                        else if (gapKeptHere > 0) { stats.CleanCutWhileGapKept++; if (adv) stats.CleanCutAdvWhileGapKept++; }
                    }
                }

                if (req.Trace)
                {
                    if (stats.Trace == null) stats.Trace = new List<string>();
                    stats.Trace.Add(FormattableString.Invariant($"{firstKey},{req.StartTime + firstKey * bucketSeconds:F2},{keptAdv},{keptLoc},{cutAdv},{cutLoc},{bestKept:F3},{bestKeptAdv:F3},{bestCutAdv:F3}"));
                }

                foreach (var state in kept)
                {
                    if (req.Deadline != null && req.Deadline.Expired) { stats.Timeout = true; break; }
                    if (expansions >= req.MaxExpansions) { stats.ExpansionCap = true; break; }
                    expansions++;
                    var current = dataset.Clips[state.Clip];
                    // An agent under threat from an external circle is treated as "far" so moving successors are ranked, not idling.
                    var far = ArriveDistance(state.Pose) > config.goalRadiusMeters || Threatened(state.Pose, state.Time, 0.5f);
                    if (ArriveDistance(state.Pose) <= config.goalRadiusMeters) { stats.InReachExpansions++; if (far) stats.InReachThreatened++; }   // a record only
                    var atGoalDistance = GoalDistance(state.Pose.X, state.Pose.Z);

                    // Successor set (Section 11.4: stationary nodes expose one continuation + capped exits).
                    ranked.Clear();
                    stats.SuccessorsSeen += current.Outgoing.Length;
                    rankGapClips.Clear();
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
                    if (rankGapClips.Count > 0) foreach (var taken in chosen) if (rankGapClips.Contains(taken)) rankGapChosen++;   // a record only
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

                    if (state.OnFollow && state.Depth < req.Follow.Count)
                    {
                        var wanted = req.Follow[state.Depth];
                        var at = -1; var wantedRank = float.NaN;
                        for (var i = 0; i < ranked.Count; i++) if (ranked[i].clip == wanted) { at = i; wantedRank = ranked[i].rank; break; }
                        FollowNote(stats, chosen.Contains(wanted) ? "taken" : at >= 0 ? "ranked-not-taken" : Array.IndexOf(current.Outgoing, wanted) >= 0 ? "pruned-before-rank" : "not-a-successor", state.Depth + 1, wanted, state.Time,
                            FormattableString.Invariant($"rank-place {at + 1} of {ranked.Count} rank {wantedRank:F3} best-rank {(ranked.Count > 0 ? ranked[0].rank : float.NaN):F3} taken {chosen.Count} far {(far ? 1 : 0)} ") + FollowDistances(state, wanted));
                    }

                    PrepareTrail(state);
                    foreach (var next in chosen)
                    {
                        var n = dataset.Clips[next];
                        stats.SuccessorsEvaluated++;
                        // the shared track is the parent's for every sibling: a sibling's stop path or wait tail (below) makes it that child's
                        if (config.localStayCost && !config.localStayNoRestoreForTest && !ReferenceEquals(trailFor, state)) PrepareTrail(state);
                        var part = EvaluateClip(state.Pose, state.Time, next, out var staticOk);
                        var childAdvancing = Advancing(state.Pose.Compose(n.EndPose), state.Time + n.Duration);
                        if (childAdvancing) stats.AdvEvaluated++; else stats.LocEvaluated++;
                        if (!staticOk && state.OnFollow && state.Depth < req.Follow.Count && next == req.Follow[state.Depth]) FollowNote(stats, "static-reject", state.Depth + 1, next, state.Time, string.Empty);
                        if (!staticOk) { stats.StaticRejects++; if (childAdvancing) stats.AdvStaticRejects++; else stats.LocStaticRejects++; continue; }
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
                        child.OnFollow = state.OnFollow && state.Depth < req.Follow.Count && next == req.Follow[state.Depth];
                        if (req.Trace)
                        {
                            // the chain's progress-cost seconds by where the distance came from, and this clip's samples
                            child.SourceSeconds = state.SourceSeconds != null ? (float[])state.SourceSeconds.Clone() : new float[SourceCodes.Length];
                            var text = new System.Text.StringBuilder();
                            for (var s = 0; s < traceT.Count; s++)
                            {
                                var code = SourceCodes.IndexOf(traceS[s]);
                                if (code >= 0) child.SourceSeconds[code] += traceDt[s];
                                text.Append((traceT[s] - state.Time).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(':').Append(traceD[s].ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(traceS[s]);
                                if (traceG[s] > 1f) text.Append('+').Append(traceG[s].ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
                                text.Append(' ');
                            }

                            child.Samples = text.ToString().Trim();
                        }

                        if (n.IsWaitCapable && ArriveDistance(child.Pose) <= config.goalRadiusMeters) { stats.InReachWaits++; if (Threatened(child.Pose, child.Time, 0.5f)) stats.InReachWaitsThreatened++; }   // a record only
                        if (child.Time >= searchEnd - 1e-4f)
                        {
                            if (childAdvancing) stats.AdvReachedEnd++; else stats.LocReachedEnd++;
                            // Search horizon reached: stop path, then idle continuation up to the full planning horizon.
                            var final = Finalize(child, stats);
                            if (final != null) final = FillToHorizon(final, stats);
                            if (final != null)
                            {
                                finals.Add(final);
                                // counted when complete: the stop path and the wait tail passed their checks
                                if (childAdvancing) { stats.AdvFinals++; if (float.IsNaN(stats.BestAdvFinal) || final.Cost.Total < stats.BestAdvFinal) stats.BestAdvFinal = final.Cost.Total; }
                                else { stats.LocFinals++; if (float.IsNaN(stats.BestLocFinal) || final.Cost.Total < stats.BestLocFinal) stats.BestLocFinal = final.Cost.Total; }
                            }
                        }
                        else if (n.IsWaitCapable && ArriveDistance(child.Pose) <= config.goalRadiusMeters && !Threatened(child.Pose, child.Time, 0.5f))
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
                                if (config.beamKeyExtraRoundingForTest)
                                {
                                    // evaluation only: the very statements of run-13, which changed the beam's choice -- the distance and the
                                    // product held in locals AND stored to the state's float fields before the key is added to (holding them
                                    // in locals alone, in either form, changed nothing: run-16, run-17)
                                    var keyDistance = GoalDistance(child.Pose.X, child.Pose.Z, out var keySource);
                                    var adjust = config.wProgress * keyDistance * remaining / horizonLength;
                                    child.BeamKey += adjust;
                                    child.KeyAdjust = adjust; child.KeyDistance = keyDistance; child.KeySource = keySource;
                                }
                                else child.BeamKey += config.wProgress * GoalDistance(child.Pose.X, child.Pose.Z) * remaining / horizonLength;
                                if (config.goalFieldRecoveryExtrapolate && req.Recovery != null)
                                {
                                    // a record only: did the correction read a gapped estimate
                                    GoalDistance(child.Pose.X, child.Pose.Z, out var gapSource);
                                    if (gapSource == 'X')
                                    {
                                        var keyGap = req.Recovery.Patch?.EstimateGapAt(child.Pose.X, child.Pose.Z) ?? 0f;
                                        if (keyGap > 1f) child.KeyGapSaving = config.wProgress * keyGap * remaining / horizonLength;
                                    }
                                }

                                // evaluation: read again for the record, apart from the key itself -- the key's own expression is left exactly
                                // as it was (a value held in a float local on the way rounds differently, and that alone changes which
                                // states the beam keeps: seen 2026-11-15, run-13)
                                if (req.Trace)
                                {
                                    child.KeyDistance = GoalDistance(child.Pose.X, child.Pose.Z, out var keySource);
                                    child.KeySource = keySource;
                                    child.KeyAdjust = child.BeamKey - child.Cost.Total;
                                }
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
            stats.LocalStayUnjudgedClips = localStayUnjudged;
            stats.StraightFallbackSeconds = fallbackSeconds; stats.PatchSeconds = patchSeconds; stats.EstimateSeconds = estimateSeconds;
            stats.RankGapReads = rankGapReads; stats.RankGapChosen = rankGapChosen;
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

            if (request.Props != null && request.Props.Overlaps(pose.X, pose.Z, request.Radius + config.propKeepMeters + slack)) return true;
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

            if (request.Props != null)
            {
                var inflate = request.Radius + config.propKeepMeters;
                var d = request.Props.DistanceAt(end.X, end.Z);
                if (d < inflate) rank += 3f; else if (d < inflate + 1f) rank += 1f * (inflate + 1f - d);
            }

            return rank;
        }

        private float CheapRank(State state, int next, bool far, float distanceStart)
        {
            var n = dataset.Clips[next];
            var end = state.Pose.Compose(n.EndPose);
            var distanceEnd = GoalDistance(end.X, end.Z);
            if (config.goalFieldRecoveryExtrapolate && request.Recovery != null)
            {
                // a record only: did the rank read a gapped estimate
                GoalDistance(end.X, end.Z, out var rankSource);
                if (rankSource == 'X' && (request.Recovery.Patch?.EstimateGapAt(end.X, end.Z) ?? 0f) > 1f) { rankGapReads++; rankGapClips.Add(next); }
            }

            var obstacleRank = request.Obstacles.Count > 0 || request.Props != null ? ObstacleRank(end, state.Time + n.Duration) : 0f;
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
                if (config.localStayCost && !n.IsStationary) PrepareTrail(cursor);
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
                if (config.localStayCost && !n.IsStationary) PrepareTrail(cursor);
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
