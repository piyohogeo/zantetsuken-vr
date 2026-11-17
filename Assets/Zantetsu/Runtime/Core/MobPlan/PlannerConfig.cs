using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>Measurement defaults of LOCOMOTION_SEARCH_PLAN.md Section 20 plus the unary cost weights (Section 12). JSON-serialisable.</summary>
    [Serializable]
    public sealed class PlannerConfig
    {
        public float planningHz = 1f;
        public float commitTargetSeconds = 2f;
        public float planningHorizonSeconds = 20f;
        public float maxStopPathSeconds = 10f;
        public int cycleBudgetMilliseconds = 800;
        public float beamTimeBucketSeconds = 1f;
        public int beamWidth = 12;
        /// <summary>The beam only searches this far ahead; the rest of the planning horizon is filled with the stop path + idle continuation.</summary>
        public float searchHorizonSeconds = 12f;
        /// <summary>Dominance quantisation (Section 11.5): states of the same clip within one cell / yaw bin / 0.5 s keep only the cheapest.</summary>
        public float dominanceCellMeters = 0.25f;
        public float dominanceYawDeg = 10f;
        public int maxStatesPerClipPerBucket = 3;
        public int maxSuccessorsEvaluatedPerState = 12;
        public int idleExitCap = 8;
        public int maxExpansionsPerAgent = 2000;
        public int maxCandidatesPerAgent = 8;
        /// <summary>Of a base clip and its yaw-rate variants, at most this many (best cheap rank) are fully evaluated per state.</summary>
        public int maxVariantsPerFamily = 2;
        /// <summary>Agents whose old suffix already reaches the goal, is static-safe and conflicts with nobody keep it without searching.</summary>
        public bool skipSatisfiedAgents = true;
        /// <summary>A walking agent also keeps its old suffix (no search) while it is conflict-free, static-safe, makes progress and keeps moving for at least this long.</summary>
        public float skipMovingMinSeconds = 6f;
        /// <summary>A walker is only skipped when no other agent's old plan comes closer than 2r + this over the horizon (quiet neighbourhood).</summary>
        public float skipProximityMeters = 2f;
        /// <summary>Solve the assignment per interaction component (agents whose candidate boxes overlap) instead of over all agents.</summary>
        public bool componentDecomposition = true;
        /// <summary>Whole-clip static acceptance from one clearance lookup at the start pose (start clearance >= r + margin + clip bounding radius).</summary>
        public bool boundingCircleAccept = true;
        public int assignmentSweeps = 3;
        public bool pairRepairEnabled = true;
        public int maxRegenerationRounds = 2;
        /// <summary>Cheap search-free candidates for standing agents: wait k idle segments then resume the old plan, or one step away then wait.</summary>
        public bool parametricCandidates = false;
        /// <summary>Static-check stored segments beyond the planning horizon too (old suffix / parametric candidates).</summary>
        public bool staticCheckWholePlan = false;
        /// <summary>Share of the cycle budget for the first candidate generation pass; the rest serves compatibility, regeneration rounds and publication.</summary>
        public float candidateBudgetShare = 0.55f;
        public float staticMarginMeters = 0.02f;
        public float agentMarginMeters = 0.02f;
        public float npcRadiusMeters = 0.35f;
        public float collisionSampleHz = 10f;
        public float staticCheckMinSegmentMeters = 0.05f;
        public float desiredSpeedMps = 1.4f;
        public float goalRadiusMeters = 0.6f;
        /// <summary>Use a per-goal Dijkstra distance field (path distance) instead of the straight line for progress, cheap rank and heading.</summary>
        public bool useGoalField = false;              // raster presets: measured worse in narrow synthetic maps (2026-09-14); scene polygon maps always use the field
        public float goalFieldLookAheadMeters = 2f;
        public bool goalFieldOnSceneMaps = true;       // scene polygon maps: field on regardless of useGoalField (goals behind blocks)
        public float goalFieldCellMeters = 0.5f;        // passability grid cell for polygon (scene) maps
        public float goalFieldExtraClearanceMeters = 0.4f;   // field routes need radius + margin + this (scene maps): keeps NPCs out of 1 m slots between blocks where they jam
        public float goalFieldMaxPathMeters = 150f;     // Dijkstra bound: max(this, 3 x straight line + 40 m) from the goal
        public float reachableDetourFactor = 2f;        // a goal is reachable when path <= factor x straight line + 20 m (field maps only)
        public float stuckGoalSeconds = 15f;            // field maps (scene) only: no progress (path distance not improved by 0.5 m) for this long while > stuckGoalMinMeters away => new goal; 0 disables
        public float stuckGoalMinMeters = 3f;           // closer than this the arrival shuffle (stop / step clips) is not counted as stuck
        public float minGoalDistanceMeters = 6f;
        public bool useBackgroundThread = true;
        public int deterministicSeed = 1;

        // Unary cost weights (Section 12).
        public float wProgress = 1f;      // mean remaining distance to goal over the horizon (m)
        public float wHeading = 0.3f;     // heading error at the candidate end (fraction of 180 deg), only while far from the goal
        public float wSpeed = 0.2f;       // mean |speed - desired| while far from the goal (m/s)
        public float wIdle = 0.5f;        // stationary seconds while far from the goal
        // PLANNER-IDLE-TURN (2026-11-15, under evaluation, off): the idle cost also for a clip that hardly translates though it is not
        // "stationary" (IsStationary asks little translation AND little yaw, so a turn on the spot paid no idle cost: 80 deg/s of
        // turning cost 0.044 a second by wTurn against 0.5 a second of waiting). IsStationary itself is untouched: it also decides
        // the stationary continuation and the successor limits. The test is the clip's own average speed.
        public bool idleCostLowTranslation = false;
        public float idleCostSpeedMps = 0.15f;   // a clip slower than this on average counts as standing for the idle cost
        // PLANNER-LOCAL-STAY (2026-11-15, under evaluation, off): a cost on the time a candidate's track stays in a small place,
        // judged over a few seconds of the track ACROSS clip borders (the NPC's last seconds and the fixed part of its plan before
        // the candidate count too), so that several clips making a small circle, a turn on the spot and a step out and back are
        // one thing. A sample is "staying" when, over the window before it, the net displacement is at most localStayNetMeters
        // AND no sample of the window is farther than localStayMaxSeparationMeters from it. Stationary clips are left to the
        // idle cost; near the goal nothing is counted (as the idle cost). No goal distance enters: a detour that leads away
        // from the goal for a while is not a stay. The same test for a new candidate, the old plan and the stop and wait tails.
        public bool localStayCost = false;
        public float localStayWindowSeconds = 4f;          // first comparison value (3-5 s)
        public float localStayNetMeters = 0.5f;
        public float localStayMaxSeparationMeters = 1.5f;
        public float wLocalStay = 0.5f;                    // a staying second (the idle cost's weight)
        /// <summary>Evaluation only: the shared track is NOT made again for a sibling after a stop path overwrote it (the defect of the first version, kept to count what it changed).</summary>
        [NonSerialized] public bool localStayNoRestoreForTest;
        /// <summary>Evaluation: the NPC ids whose searches keep a per-bucket record of what the beam kept and cut (SearchStats.Trace).</summary>
        [NonSerialized] public HashSet<int> traceAgents;
        public float wTurn = 0.2f;        // yaw travel (sum of |yaw change|, wiggles included) in full turns (0.05 on TotalYaw before 2026-09-27)
        public float wFacing = 3f;        // mean angle between travel direction and facing (fraction of 180 deg) while far: penalises strafing / backing up (INERTIAL 3, COREMOTION 1.5 via ApplyDatasetDefaults)
        public float wHeadingPath = 0.8f; // mean heading error towards the goal (or field waypoint) along the trajectory while far, not only at the end
        public int reservedStraightSuccessors = 2;
        public bool beamTimeNormalized = true;         // beam ranks states by cost extrapolated to the bucket end (else raw cumulative cost, which favours short clips); INERTIAL on, COREMOTION off (ApplyDatasetDefaults)   // best loop / straight successors evaluated beyond the cheap-rank cap while far
        public float wChurn = 0.3f;       // first suffix clip differs from the old plan
        public float wOtherAgent = 3f;    // predicted collision seconds with other agents' old plans (soft, candidate generation only)
        // Distance tiers from the player (dynamic obstacle): near / mid / far change replan interval, commit, beam, candidates and regeneration.
        public bool lodEnabled = true;
        public float lodNearMeters = 8f;
        public float lodFarMeters = 16f;
        public float lodMidReplanSeconds = 2f;
        public float lodFarReplanSeconds = 3f;
        public float lodMidCommitSeconds = 3f;
        public float lodFarCommitSeconds = 4f;
        public int lodMidBeam = 8;
        public int lodFarBeam = 6;
        public int lodMidCandidates = 6;
        public int lodFarCandidates = 4;
        public bool lodFarRegeneration = true;
        public float wDynamicObstacle = 10f;      // predicted overlap seconds with an external circle (soft, Section 9.4)
        public float dynamicCollisionPenalty = 20f; // per candidate that overlaps an external circle at all: collision-free candidates always win
        public float dynamicKeepMeters = 0.3f;    // extra distance beyond touching that counts as overlap for the planner
        // PROP-OBSTACLE-1: the map of prop fragments (a felled tree's pieces) as a best-effort dynamic obstacle, where the host gives one.
        public bool propObstacles = true;         // false: the map is not looked at (the comparison's OFF)
        public float wPropObstacle = 10f;         // predicted overlap seconds with the occupied cells (soft, as the external circle's)
        public float propKeepMeters = 0.1f;       // beyond the NPC radius; the map itself is not inflated, so radius and keep count once
        public float propSpawnKeepMeters = 0.3f;  // a spawn spot keeps this beyond the NPC radius from the occupied cells (hard)
        public bool propGoalField = false;        // comparison option: goal fields route around the occupied cells (a field per map version)
        public float propGoalFieldCellMeters = 0f; // with propGoalField and occupied cells: the goal fields' cell (m); 0 = goalFieldCellMeters
        // Evaluation (off): the goal field's two reaches in METRES instead of cells, as they are at the usual 0.5 m cell -- the step
        // its waypoint search probes at (3 cells: 1.5 m at 0.5 m, 3 m at 1 m) and the neighbourhood a point without a path reads
        // (+-2 cells: +-1 m at 0.5 m, +-2 m at 1 m). With it a coarser cell changes the cell only.
        public bool goalFieldMetricReach = false;
        // PLANNER-FIELD-RECOVERY (under evaluation, off): from a place the goal field has no path at, the progress, the heading and the
        // cheap rank use "the walk to the nearest place the field can be entered + the field's remaining distance" (GoalFieldRecovery.cs)
        // instead of the straight line; the walk is looked for over goalFieldRecoveryMeters with the WALKING clearance.
        public bool goalFieldRecovery = false;
        public float goalFieldRecoveryMeters = 4f;
        // With goalFieldRecovery (off): a point inside a patch's reach that has no field path and no way back takes, FOR THE COST ONLY, the
        // least of "a patch cell's value + the distance to it" instead of the straight line to the goal -- so that staying beside the
        // way back is not costed some metres nearer the goal than the way back itself. Never a way: paths, guide points and the
        // "way back found" count come from the connected values alone.
        public bool goalFieldRecoveryExtrapolate = false;
        /// <summary>Evaluation only: the beam key's time-bucket correction is held in a float local before it is added (the same arithmetic rounded once more; it changed which states the beam kept, 2026-11-15) -- a second condition to see how far a comparison's direction holds.</summary>
        [NonSerialized] public bool beamKeyExtraRoundingForTest;
        /// <summary>
        /// Evaluation only (a saved-input replay): the cycle's and the searches' deadlines never read the clock (Deadline.WorkLimitsOnly),
        /// so the per-NPC time slices, the start of a regeneration round and the end of the assignment cannot differ between two runs;
        /// the counted limits stay as they are. The product runs with this off.
        /// </summary>
        [NonSerialized] public bool workLimitsOnly;
        // PROP-GOAL-REACH (off: evaluation): a goal whose point is nearer the prop fragments' cells than walking keeps (radius +
        // propKeepMeters) is not moved; the planner is led to the nearest point within goalRadiusMeters - propGoalReachInsetMeters of
        // it that walking can stand on (static clearance and the prop cells) and that has a way. Arriving stays "within
        // goalRadiusMeters of the goal itself, standing".
        public bool propGoalReach = false;
        public float propGoalReachInsetMeters = 0.1f;
        // What is done when the sample (a 0.1 m grid within goalRadiusMeters - inset of the goal) finds no point to stand on, once that
        // has lasted propGoalNoReachSeconds: 0 nothing (as before); 1 standoff -- the nearest standable point within
        // propGoalStandoffMeters of the goal, standing there counts as arriving (counted apart); 2 give up -- the goal is dropped and
        // the next goal of the scenario's order is taken (a pass waypoint -> its far goal; a far goal -> a new pass waypoint);
        // 3 pick again -- another goal of the same kind (a far goal -> another far goal; a pass waypoint -> its far goal, as 2).
        public int propGoalNoReachMode = 0;
        public float propGoalNoReachSeconds = 2f;
        public float propGoalStandoffMeters = 3f;
        // PROP-PREFIX-RECHECK (off): each cycle looks at the part of a plan it would freeze against the newest prop map; where
        // that part overlaps the cells (radius + propKeepMeters) later than propPrefixRecheckCommitSeconds from now, the freeze ends
        // at the first clip end at or after now + propPrefixRecheckCommitSeconds instead, and the search starts from there.
        public bool propPrefixRecheck = false;
        public float propPrefixRecheckCommitSeconds = 1f;
        // true: the freeze is not ended early where the search would then start within radius + propKeepMeters of the cells (the
        // NPC is already going in by then: a search from inside the pieces found only overlapping plans and circled there --
        // NPC 1182, record 27); the plan made earlier is left to carry it through.
        public bool propPrefixRecheckClearStart = false;
        /// <summary>Evaluation only: per NPC id, plans (suffixes from the cycle's boundary) to be costed again by this cycle's cost function (AgentCycleInfo.Rescored).</summary>
        [NonSerialized] public Dictionary<int, List<List<PlanSegment>>> rescoreSuffixes;
        /// <summary>Evaluation only: per NPC id, a plan whose clips the search is to report on as it goes -- ranked, taken, made, kept or cut (SearchStats.FollowTrace).</summary>
        [NonSerialized] public Dictionary<int, List<PlanSegment>> followSuffixes;
        public float goalFieldRecoveryEscapeMeters = 1f;   // an NPC already nearer the prop cells than walking keeps may get out through cells no nearer than it is now -- within this of where it stands only
        // With propGoalField: the distance from the prop cells the goal fields close (m); 0 = the field's own clearance (radius + margin +
        // goalFieldExtraClearanceMeters = 0.77 m). The extra 0.4 m is for narrow STATIC slots; the walking cost keeps radius + propKeepMeters.
        public float propGoalFieldClosureMeters = 0f;
        // Goal inside the external circle (simulation layer): 0 = ignore (NPC idles at the ring edge), 1 = arriving at the ring counts
        // as reached, 2 = plan towards a standoff point away from the circle until the goal is free again.
        public int blockedGoalMode = 2;
        public float blockedGoalStandoffMeters = 1.5f;   // standoff point = circle + NPC radius + keep + this, on the NPC's side
        public float blockedGoalAbandonSeconds = 5f;     // waiting near the standoff point this long abandons the goal (0 = never)
        public float blockedGoalHysteresisMeters = 0.4f;
        // PlayerFlow (simulation layer): the pass waypoint counts as passed this far before it, without stopping, so no Stop clip is
        // planned before the far goal arrives (COREMOTION stops take 2-4.5 m; INERTIAL 0.2 m).
        public float flowSwitchMeters = 5f;
        public float flowWaypointStepMeters = 1f;        // the waypoint follows the player in steps of this size (each step forces a replan) // the goal is free again once it is this far outside the blocked distance

        /// <summary>
        /// PROP-OBSTACLE-1, the configuration adopted with the prop fragments' map (D-264, 2026-11-17; tried in ordinary play
        /// from 2026-11-16): the goal fields route around the pieces at 1 m cells, the recovery patch, the idle cost fix, and a
        /// goal among the pieces is led to a standable point of its reach or given up. The re-check of the frozen part stays
        /// off. The crowd applies it whenever the map is on (the default); the fields' own defaults stay the planner of
        /// before, which a launch with -zantetsuPropObstaclesOff runs. The replay's `on+field1.0+recover+idlefix+giveup`.
        /// </summary>
        public void ApplyPropTrial()
        {
            propGoalField = true;
            propGoalFieldCellMeters = 1f;
            goalFieldRecovery = true;
            idleCostLowTranslation = true;
            propGoalReach = true;
            propGoalNoReachMode = 2;
            propPrefixRecheck = false;
        }

        public PlannerConfig Clone() => (PlannerConfig)MemberwiseClone();

        /// <summary>Planner defaults that depend on the clip dataset (clip length, stop time, walking speed).</summary>
        public void ApplyDatasetDefaults(string datasetId)
        {
            if (datasetId == "INERTIAL")
            {
                // Clips average 1.1 s, a stop takes 0.8 s and a 45-degree turn 1.3 s, so a short look-ahead is enough; measured 2026-09-21:
                // search 12 -> 6 s and stored plan 20 -> 12 s keep the goal counts and cut the cycle time to ~40%; 6 evaluated successors suffice.
                desiredSpeedMps = 0.9f;        // fast walk loops are 0.92 m/s, slow 0.61 m/s
                flowSwitchMeters = 2f;
                searchHorizonSeconds = 6f;
                planningHorizonSeconds = 12f;
                maxSuccessorsEvaluatedPerState = 6;
                wFacing = 3f;                  // 2026-09-27 sweep: backstep 13-15% -> 2-3% of walking time, goals unchanged or up
                beamTimeNormalized = true;     // 1 s turn clips vs 1.3 s walk cycles: raw cumulative cost made the beam pick turn chains (yaw 40 -> 20 deg/s, goals +50%)
            }
            else
            {
                desiredSpeedMps = 1.4f;
                flowSwitchMeters = 5f;
                searchHorizonSeconds = 12f;
                planningHorizonSeconds = 20f;
                maxSuccessorsEvaluatedPerState = 12;
                wFacing = 1.5f;                // 3 costs goals in the corridor / T-junction (sidesteps are how COREMOTION passes)
                beamTimeNormalized = false;    // mixed on COREMOTION (corridor / player flow up, scene crossing / flow down 25%): kept raw
            }
        }

        public string Describe() =>
            $"hz {planningHz} commit {commitTargetSeconds}s horizon {planningHorizonSeconds}s budget {cycleBudgetMilliseconds}ms beam {beamWidth}/{maxStatesPerClipPerBucket} search {searchHorizonSeconds}s dom {dominanceCellMeters}m/{dominanceYawDeg}deg succ {maxSuccessorsEvaluatedPerState} fam {maxVariantsPerFamily} idleExit {idleExitCap} exp {maxExpansionsPerAgent} cand {maxCandidatesPerAgent} r {npcRadiusMeters} m{(skipSatisfiedAgents ? " skipSat" : "")}{(boundingCircleAccept ? " circle" : "")}";
    }
}
