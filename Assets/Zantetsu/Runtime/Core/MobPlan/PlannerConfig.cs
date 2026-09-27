using System;

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
