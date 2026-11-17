using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>One variant of a replay: the map on or off, and the map's settings where they differ from the record's.</summary>
    public sealed class PropReplayOptions
    {
        public string Name = "on";
        /// <summary>False: the planner and the start check are given no map (the comparison's OFF).</summary>
        public bool Props = true;
        /// <summary>The cell's side (m); 0: as recorded. The window stays as wide.</summary>
        public float Cell;
        /// <summary>The plane's height (m); 0: as recorded.</summary>
        public float Plane;
        /// <summary>Every k-th recorded build is made (1: all): a longer interval between builds.</summary>
        public int BuildEvery = 1;
        /// <summary>From a build's input to its publication (s); negative: as recorded for that build.</summary>
        public double PublishDelay = -1.0;
        /// <summary>From a cycle's queueing to its collection (virtual s).</summary>
        public double CycleLatency = 0.1;
        /// <summary>
        /// Negative: the windows of the record. Otherwise the replay places the window itself by the product's rule (centred on
        /// the player again when the player is nearer than this to its edge, at most 0.4 of the window) from the first build of
        /// the session on. Needs a record that kept the shapes of a square holding every such window (captureSquareMeters).
        /// </summary>
        public float FollowMargin = -1f;
        /// <summary>FollowMargin on a record without the capture square (to check the placing against the recorded windows only).</summary>
        public bool FollowUnchecked;
        /// <summary>Added to a build's publication when the replay's window moved at it and the record's did not (s): the static mask made again on the worker.</summary>
        public double MaskDelay;
        /// <summary>The virtual frame (s).</summary>
        public float Step = 1f / 72f;
        /// <summary>The planner's goal fields route around the occupied cells (PlannerConfig.propGoalField).</summary>
        public bool GoalField;
        /// <summary>Keep every virtual frame's poses (a viewer).</summary>
        public bool KeepFrames;
        /// <summary>
        /// The yardstick's planes (m): what "inside the sections" is measured against is the union of the sections at these
        /// heights (null: the record's one plane). The same for every variant of a comparison, whatever plane the variant's map uses.
        /// </summary>
        public float[] YardPlanes;
        /// <summary>PLANNER-IDLE-TURN: the idle cost also for clips that hardly translate (PlannerConfig.idleCostLowTranslation).</summary>
        public bool IdleFix;
        /// <summary>With GoalField: the goal fields' cell while the prop map closes cells (m); 0: the usual 0.5 m.</summary>
        public float GoalFieldCell;
        /// <summary>The goal fields' cell always (m); 0: the config's (0.5 m). With it the cell does not change when the prop map empties.</summary>
        public float FieldCellAlways;
        /// <summary>PLANNER-FIELD-RECOVERY (PlannerConfig.goalFieldRecovery).</summary>
        public bool Recovery;
        /// <summary>The patch's estimate for the cost beside the way back (PlannerConfig.goalFieldRecoveryExtrapolate).</summary>
        public bool Extrapolate;
        /// <summary>Evaluation only: the beam key's correction rounded once more (PlannerConfig.beamKeyExtraRoundingForTest).</summary>
        public bool ExtraRounding;
        /// <summary>No decision of the planner reads the clock (PlannerConfig.workLimitsOnly): two runs of one variant must agree to the last plan.</summary>
        public bool WorkLimits;
        /// <summary>Evaluation: this variant without the searches' trace even when the run names NPCs to trace.</summary>
        public bool NoTrace;
        /// <summary>PROP-GOAL-REACH (PlannerConfig.propGoalReach).</summary>
        public bool GoalReach;
        /// <summary>PlannerConfig.propGoalNoReachMode: 0 nothing, 1 standoff, 2 give up, 3 pick again.</summary>
        public int NoReachMode;
        /// <summary>PROP-PREFIX-RECHECK: PlannerConfig.propPrefixRecheckCommitSeconds; negative = off.</summary>
        public float PrefixRecheck = -1f;
        /// <summary>PlannerConfig.propPrefixRecheckClearStart.</summary>
        public bool PrefixRecheckClearStart;
        /// <summary>Evaluation: at every cycle that searched a traced NPC, the cycle is run again with the estimate switched the other way, and the two chosen plans are costed by both cost functions (PropReplayResult.RescoreRows).</summary>
        public bool Rescore;
        /// <summary>The distance from the prop cells the goal fields close (m); 0: the field's own clearance (PlannerConfig.propGoalFieldClosureMeters).</summary>
        public float PropBand;
        /// <summary>The goal field's reaches in metres (PlannerConfig.goalFieldMetricReach).</summary>
        public bool MetricReach;
        /// <summary>Evaluation only: the first version's defect kept (PlannerConfig.localStayNoRestoreForTest).</summary>
        public bool LocalStayNoRestore;
        /// <summary>PLANNER-LOCAL-STAY: the cost on the track's time in a small place (PlannerConfig.localStayCost).</summary>
        public bool LocalStay;
        /// <summary>The NPC ids whose searches keep what the beam kept and cut per time bucket (PropReplayResult.TraceRows).</summary>
        public HashSet<int> TraceAgents;
        /// <summary>Keep a row per NPC per cycle of what was chosen against the best waiting, turning and moving candidates (PropReplayResult.CycleRows).</summary>
        public bool CycleRows = true;
        /// <summary>The local-stay measurement's settings (PropDwell.cs).</summary>
        public DwellSettings Dwell = new DwellSettings();
        /// <summary>The cycle's wall budget (ms); 0: the config's. A replay runs faster than play, so the wall budget is the search's own limit only.</summary>
        public int CycleBudgetMilliseconds;
    }

    /// <summary>What one replay of one section gave.</summary>
    public sealed class PropReplayResult
    {
        public string Name;
        public double From, To;
        public int Cycles, CyclesPublished, CyclesUnpublished, CyclesStale, CyclesStaleVisible;
        public int Starts, StartsInView, StartsUnplaced, StartsRefusedInProps;
        /// <summary>Starts whose spot is inside the sections (the centre in an occupied cell of the yardstick map) / overlaps them with the NPC radius.</summary>
        public int StartsInsideProps, StartsOverlappingProps;
        public int Retired, RetirementsNotMatched;
        /// <summary>Times an NPC's centre entered the sections, and the NPC-seconds it spent inside.</summary>
        public int Crossings;
        public double CrossingSeconds;
        /// <summary>NPC-seconds with the NPC circle overlapping the sections.</summary>
        public double OverlapSeconds;
        /// <summary>NPC-seconds within 1 m of the sections without overlapping them (passing by).</summary>
        public double NearSeconds;
        /// <summary>NPC-seconds standing (under 0.05 m/s) within 1.5 m of the sections.</summary>
        public double StandingNearSeconds;
        public double AgentSeconds;
        public float GoalsReached;
        public int StuckGoalEvents;
        public double TravelMeters;
        public int PropSpawnRejects, PropGoalRejects;
        public int PropPredictedHits;
        public double PropPredictedSeconds;
        // the map
        public int MapsBuilt, MapsUsedByCycles, CyclesWithoutMap;
        public double MapAgeAtCycleMean, MapAgeAtCycleMost;      // s, at the cycle's queueing: the input's age
        public double MapPublishDelayMean, MapPublishDelayMost;  // s, input to publication
        public int AgentStepsOutsideWindow;
        public int MostOccupiedCells, MostItems, MostSections;
        public long LowFragments;                                // fragments wholly below the plane, summed over the builds made
        // real CPU of this replay (ms)
        public double MapBuildMillisecondsMean, MapBuildMillisecondsMost, MapBuildMillisecondsInAll;
        public double CycleMillisecondsMean, CycleMillisecondsMost, CycleMillisecondsInAll;
        public double PrepareMillisecondsInAll;
        /// <summary>Of the preparation: making goal fields and their grids (the simulation's own clock), ms.</summary>
        public double FieldMillisecondsInAll;
        public int FieldResets;
        // the goal fields of this run: made / taken from the cache, the cells their searches reached, their own time and the grids'
        public int FieldBuilds, FieldCacheHits, FieldGridBuilds;
        public long FieldCellsReached;
        public double FieldBuildMilliseconds, FieldGridMilliseconds;
        /// <summary>Clips evaluated with no track prepared (PLANNER-LOCAL-STAY), summed over the searches.</summary>
        public long LocalStayUnjudgedClips;
        /// <summary>Over every search of the run: seconds of the progress cost's samples costed by the straight line for want of a field path and a way back, by a patch's connected value, by a patch's estimate; searches that hit their time slice or their expansion cap.</summary>
        public double StraightFallbackSeconds, PatchSeconds, EstimateSeconds;
        public int SearchTimeouts, SearchCaps, Searches;
        /// <summary>
        /// What the cycles worked, whatever the diagnosis keeps: expansions, regeneration rounds, candidates in the pools, cycles whose
        /// metrics say a time limit was met; a fingerprint of every plan the cycles published (NPC, clips, times, start poses, bit
        /// for bit) and one row per cycle of the same.
        /// </summary>
        public long Expansions, CandidatesTotal;
        public int RegenerationRounds, CycleTimeouts, SkippedForBudget;
        public ulong PlanHash = 1469598103934665603UL;
        public bool WorkLimits;
        public readonly List<string> CycleHashRows = new List<string>();
        public const string CycleHashHeader = "time,revision,published,agents,plans,expansions,regeneration_rounds,candidates,search_timeouts,timeout,plan_hash";
        /// <summary>Recovery patches whose estimate was read, those with a step of more than 1 m left beside a connected cell, and the largest such step (m).</summary>
        public int EstimatePatches, EstimateBoundaryPatches;
        public float EstimateBoundaryStepMost;
        /// <summary>The preparation and the planner together, per cycle (ms).</summary>
        public double WholeCycleMillisecondsMean => Cycles > 0 ? (PrepareMillisecondsInAll + CycleMillisecondsInAll) / Cycles : 0;
        public double PrepareMillisecondsMean => Cycles > 0 ? PrepareMillisecondsInAll / Cycles : 0;
        // the local stays (PropDwell.cs)
        public readonly List<DwellInterval> Dwells = new List<DwellInterval>();
        public DwellTotals DwellAll, DwellNear, DwellNearStatic, DwellNearAny;
        // per NPC per cycle: the chosen candidate's class (move: walking begins within 2.5 s of the first changeable moment; else turn or
        // wait by which takes more of the time before walking), against the best candidate of each class
        public readonly List<string> CycleRows = new List<string>();
        public int ChoseTurn, ChoseTurnWithMoveCandidate, ChoseTurnWithoutMoveCandidate, ChoseWait, ChoseMove, WalkPostponed;
        public double TurnOverMoveGapSum, TurnOverWaitGapSum;
        public int ChoseTurnWithWaitCandidate;
        // by the track itself over the 5 s after the first changeable moment (PlanCandidate.Full): "leave" -- 1.5 m or more net;
        // "local" -- never 1 m from where it was; "loop" -- went 1 m or more away and is under 1.5 m net. Counted for the rows whose
        // NPC is 3 s or more into a stay and was searched in the cycle.
        public int StayRows, StayChoseLeave, StayChoseLocal, StayChoseLoop, StayNotLeaveWithLeaveCandidate, StayNotLeaveWithoutLeaveCandidate;
        public double StayLeaveGapSum;
        public readonly List<string> TraceRows = new List<string>();
        public readonly List<string> StateRows = new List<string>();
        // for the traced NPCs, per cycle: where the search starts, the goal, the goal field's path length there against the straight
        // line, the point the field guides to, and where the chosen candidate is 5 s on
        public readonly List<string> GuideRows = new List<string>();
        public const string GuideRowHeader = "time,id,start_x,start_z,goal_x,goal_z,straight_m,field_path_m,detour,guide_x,guide_z,guide_bearing,goal_bearing,chosen_geo,chosen_first_clip,chosen_x_5s,chosen_z_5s,chosen_bearing,n_leave,cost_total,cost_dynamic,field_cell,field_probe_m,field_cells_reached,own_cell_passable,goal_cell_passable,nearest_cell_with_path_m,required_clearance_m,prop_distance_m,static_clearance_m,recovery,recovery_sources,recovery_connect_m,recovery_value_m,recovery_entry_x,recovery_entry_z,now_x,now_z,now_field_path_m,now_prop_distance_m,commit_s,start_patch_value_m,start_estimate_m,start_estimate_from_x,start_estimate_from_z,start_estimate_away_m";
        // PLANNER-FIELD-RECOVERY: patches made (an NPC of a cycle that stood where the field had no path), those with a way back, their time
        public int RecoveryPatches, RecoveryFound, RecoveryOverlapping, RecoveryOverlappingFound;
        public double RecoveryMilliseconds;
        public long RecoveryReads;
        /// <summary>NPC-cycles (searched) whose search started where the field had no path / whose goal's own cell was closed in the field.</summary>
        public int StartsWithoutFieldPath, GoalsInClosedCells, SearchedAgentCycles;
        // the searched NPC-cycles whose goal's cell is closed in the guiding grid, by level (1 the goal point inside an obstacle; 2 outside
        // but overlapping with the NPC radius + the walking margin; 3 walkable but inside the guiding grid's extra clearance; 4 the point
        // passes that too -- only the cell's centre is closed) and by what closes it (0 static, 1 prop, 2 both): [level * 3 + source]
        public readonly int[] GoalClosed = new int[15];
        public readonly int[] GoalClosedInStay = new int[5];   // of them with the NPC 3 s or more into a stay, by level
        public readonly List<string> GoalRows = new List<string>();
        /// <summary>One row per NPC per planning cycle: where it is, its goal, and what happened to the goal since the last cycle.</summary>
        public readonly List<string> NpcRows = new List<string>();
        public const string NpcRowHeader = "time,id,event,x,z,goal_x,goal_z,plan_goal_x,plan_goal_z,goal_m,goals_reached,goal_revision,blocked,abandoned,flow_phase,yaw,reach,path_m_since,abs_yaw_since,no_point,standoff,goal_stage,no_point_s";
        /// <summary>PROP-GOAL-REACH: NPC-cycles led to a point inside the goal's reach; points set, goals with none, points set without a path seen, goals reached while led.</summary>
        public int ReachAgentCycles, ReachFound, ReachNone, ReachUnverified, ReachArrivals;
        /// <summary>
        /// Counted by goal (one NPC's one goal revision), from what each cycle sees: goals led to a point inside the reach, of them
        /// with a path seen / without; goals for which the sample found no point in some cycle; goals that were both; goals led to
        /// a standoff. Goals reached (the goal's own condition) whose goal had been in the no-point state and never led.
        /// The no-point policy's own counts.
        /// </summary>
        public int LedGoals, LedGoalsVerified, LedGoalsUnverified, NoPointGoals, LedAndNoPointGoals, StandoffGoals, ReachedNoPointGoals;
        public int StandoffSet, StandoffNone, StandoffArrivals, GivenUpForProps, PickedAgainForProps;
        /// <summary>PROP-PREFIX-RECHECK: freezes ended early, the frozen seconds given back.</summary>
        public int PrefixShortened, PrefixOnlyEvent, PrefixStartInside; public double PrefixCutSeconds;
        /// <summary>Measured only: the re-check's own time over the cycles (sum, the most in one cycle) and the places it looked at; the static mask made again (count, sum and most, ms).</summary>
        public double PrefixRecheckMilliseconds, PrefixRecheckMostMilliseconds, MaskMilliseconds, MaskMostMilliseconds; public int PrefixRecheckSamples, MaskBuilds;
        /// <summary>The replay's window: moves within the section, builds whose window is not the record's, publications delayed for the mask.</summary>
        public int WindowMoves, WindowsNotTheRecords, MaskDelays, PublicationsHeldForOrder;
        /// <summary>NPC-seconds within what is counted (InScope); equal to AgentSeconds without a capture square.</summary>
        public double AgentSecondsInScope;
        /// <summary>One row per NPC and cycle whose freeze was ended early or whose search starts within radius + keep of the prop cells: where the search starts, what it found, what was taken.</summary>
        /// <summary>
        /// For the traced NPCs (TraceAgents), one row per cycle and candidate: its cost, and which other NPCs' TAKEN plans it comes
        /// nearer than the selection allows (2 radii + agentMarginMeters) -- why a cheaper candidate was not the one taken.
        /// Made from the cycle's result; the planner is not touched.
        /// </summary>
        public readonly List<string> SelectionRows = new List<string>();
        public const string SelectionRowHeader = "time,id,status,candidate,origin,taken,total,dynamic,prop_overlap_s,npc_overlap_s,first_clip,blocked_by_n,blocked_by(id:least_separation_m@time:their_origin)";
        public readonly List<string> PrefixRows = new List<string>();
        public const string PrefixRowHeader = "time,id,shortened,lod_skipped,status,boundary,now_x,now_z,now_prop_m,start_x,start_z,start_prop_m,candidates,clean_candidates,chosen,chosen_origin,chosen_first_clip,chosen_prop_overlap_s,chosen_dynamic,chosen_total,best_clean_total,old_prop_overlap_s,old_total,chosen_net_m_4s,chosen_least_prop_m_4s,chosen_prop_m_after_4s,expansions";
        /// <summary>The window of the map against the player, at the cycles: how far the player is from its centre (the larger of |dx|, |dz|), the NPCs within 25 m of the player and those of them outside the window now or in the next six seconds of the plan chosen.</summary>
        public int WindowCycles, NpcCyclesNearPlayer, NpcCyclesNearOutsideWindow, NpcCyclesNearPlanOutsideWindow; public double WindowOffCentreSum, WindowOffCentreMost;
        /// <summary>Goals reached by the goal's own condition while led to a standoff (in the goals reached, not in the standoff arrivals).</summary>
        public int ReachedWhileAtStandoff;
        /// <summary>One row per NPC-cycle led to a point (inside the reach or a standoff): the point, what stands about it, and what the search did within the goal's radius.</summary>
        public readonly List<string> ReachRows = new List<string>();
        /// <summary>One row per entry of an NPC's centre into the yardstick's sections: where and when, what the planner's map said there, the plan being played and what its cycle knew, the goal.</summary>
        public readonly List<string> CrossingRows = new List<string>();
        public const string CrossingRowHeader = "id,enter,exit,seconds,ended,enter_x,enter_z,exit_x,exit_z,deepest_m,clip_at_entry,planner_map_version,planner_map_input_time,planner_map_age_s,planner_map_distance_at_entry,planner_map_occupied_at_entry,planner_map_least_distance_during,"
            + "plan_cycle_time,plan_age_s,plan_map_version,plan_map_input_time,plan_origin,plan_changed,plan_prop_overlap_s,plan_dynamic,plan_total,plan_least_prop_m,plan_least_at,candidates,clean_candidates,best_clean_total,plan_boundary,entry_in_fixed_part,plan_least_prop_m_changeable_part,plan_least_at_changeable_part,deciding_cycle_time,deciding_age_s,deciding_boundary,deciding_map_version,deciding_map_input_time,deciding_origin,deciding_map_distance_at_plan_place_at_entry_time,deciding_prop_overlap_s,deciding_dynamic,deciding_candidates,deciding_clean_candidates,deciding_best_clean_total,deciding_total,"
            + "goal_x,goal_z,plan_goal_x,plan_goal_z,led,standoff,flow_phase,goal_stage,last_abandon_time,since_abandon_s,"
            + "deciding_plan_place_x,deciding_plan_place_z,deciding_map_window_min_x,deciding_map_window_min_z,deciding_map_window_m,deciding_map_place_in_window,deciding_map_occupied_cells,deciding_map_cells_min_x,deciding_map_cells_max_x,deciding_map_cells_min_z,deciding_map_cells_max_z";
        public const string ReachRowHeader = "time,id,flow_phase,standoff,verified,goal_x,goal_z,point_x,point_z,offset_m,point_prop_m,point_static_m,point_threatened,goal_m,point_m,searched,in_reach_expansions,in_reach_threatened,in_reach_waits,in_reach_waits_threatened,goal_finals,candidates,chosen_origin,chosen_first_clip";
        public readonly List<string> RescoreRows = new List<string>();
        /// <summary>With Rescore, where the two chosen plans differ: this variant's search asked what became of the OTHER variant's plan (time, id, then SingleAgentSearch.FollowTraceHeader).</summary>
        public readonly List<string> FollowRows = new List<string>();
        public const string RescoreRowHeader = "time,id,plan,scored_with,static_ok,total,progress,heading,speed,idle,turn,facing,churn,other,penalty,dynamic,overlap_s,prop_overlap_s,local_stay,estimate_s,estimate_gap_s,segments,first_clip,end_x,end_z,own_search_total";
        /// <summary>One row per NPC-cycle whose recovery patch had its estimate read: the patch, its largest step beyond one cell and where, its gap, and what the search and the chosen candidate had of it.</summary>
        public readonly List<string> PatchRows = new List<string>();
        public const string PatchRowHeader = "time,id,centre_x,centre_z,found,sources,reached,estimate_reads,step_beyond_one_cell_most,steps_over_1m,step_cell_x,step_cell_z,step_estimate,step_neighbour_x,step_neighbour_z,step_neighbour_value,gap_most,gap_cells_over_1m,"
            + "search_estimate_s,gap_kept,gap_kept_adv,gap_cut,gap_cut_adv,clean_cut_while_gap_kept,clean_cut_adv_while_gap_kept,gap_kept_only_by_gap,gap_kept_cost_sum,gap_kept_cost_most,gap_cut_cost_sum,"
            + "key_gap_kept,key_gap_cut,rank_gap_reads,rank_gap_chosen,candidates,candidates_with_gap,chosen,chosen_origin,chosen_total,chosen_progress,chosen_estimate_s,chosen_gap_s,chosen_gap_cost,best_other_total,best_other_gap_cost,best_clean_total";
        public const string GoalRowHeader = "time,id,goal_x,goal_z,level,closed_by,static_clearance_m,prop_distance_m,cell_static_clearance_m,cell_prop_distance_m,walk_static_m,walk_prop_m,guide_static_m,guide_prop_m,in_stay_s,flow_phase,goal_blocked,start_has_field_path";

        public sealed class GuideMark
        {
            public float[] Back;   // the way back to the field from the search's start (x, z pairs); null: none
            public double Time;
            public int Id;
            public float StartX, StartZ, GoalX, GoalZ, GuideX, GuideZ;
            public bool Detour;
            public float[] Track;   // the chosen candidate over the 5 s after the search's start: x, z pairs
        }

        public readonly List<GuideMark> GuideMarks = new List<GuideMark>();
        public const string TraceRowHeader = "time,id,bucket,bucket_start,kept_advancing,kept_local,cut_advancing,cut_local,best_kept_key,best_kept_advancing_key,best_cut_advancing_key";
        public const string CycleRowHeader = "time,id,tier,lod_skipped,status,in_stay_s,commit_s,prefix,candidates,n_move,n_turn,n_wait,chosen,origin,chosen_class,chosen_first_clip,walk_start_s,postponed,cost_total,cost_progress,cost_heading,cost_speed,cost_idle,cost_turn,cost_facing,cost_churn,cost_other,cost_penalty,cost_dynamic,best_move,best_turn,best_wait,seen,evaluated,static_rejects,dead_end,stop_tail,beam_pruned,dominance_pruned,family_pruned,idle_exits_pruned,reservation_rejects,finals,timeout,cap,geo_class,net_5s,max_sep_5s,n_leave,n_loop,n_local,best_leave,best_loop,best_local,cost_local_stay,local_stay_s,adv_evaluated,loc_evaluated,adv_static_rejects,loc_static_rejects,adv_beam_pruned,loc_beam_pruned,adv_dominance_pruned,loc_dominance_pruned,adv_finals,loc_finals,best_adv_final,best_loc_final,adv_reached_end,loc_reached_end,local_stay_unjudged";
        public DwellSettings DwellSettings;
        /// <summary>The real time the stay measurement took in this replay (ms).</summary>
        public double DwellMeasureMilliseconds;
        public double ReplayWallSeconds;
        public readonly List<Frame> Frames = new List<Frame>();
        public readonly List<StartMark> StartMarks = new List<StartMark>();

        public struct Agent { public int Id; public float X, Z, Yaw; public bool Inside; }
        public sealed class Frame
        {
            public double Time;
            public Agent[] Agents;
            public int MapVersion, TruthBuild;
            public double MapInputTime;
        }

        public struct StartMark { public double Time; public int Id; public float X, Z; public byte Result; public bool Inside; }

        /// <summary>Per 1000 NPC-seconds of the run.</summary>
        public double Per(double value) => AgentSeconds > 0 ? value * 1000.0 / AgentSeconds : 0;

        public static string CsvHeader => "name,from,to,agent_s,cycles,published,unpublished,stale,stale_visible,starts,starts_in_view,starts_unplaced,starts_refused_in_props,starts_inside_props,starts_overlapping_props,retired,retire_unmatched,crossings,crossing_s,overlap_s,near_s,standing_near_s,goals,stuck_goals,travel_m,spawn_rejects,goal_rejects,pred_hits,pred_s,maps_built,cycles_with_map,cycles_without_map,map_age_mean_ms,map_age_most_ms,map_delay_mean_ms,map_delay_most_ms,steps_outside_window,most_cells,most_items,most_sections,low_fragments,map_ms_mean,map_ms_most,map_ms_all,cycle_ms_mean,cycle_ms_most,cycle_ms_all,prepare_ms_all,replay_wall_s,prepare_ms_mean,field_ms_all,field_resets,whole_cycle_ms_mean,dwell_measure_ms," + DwellTotals.CsvHeader("stay_all") + "," + DwellTotals.CsvHeader("stay_near") + "," + DwellTotals.CsvHeader("stay_static") + "," + DwellTotals.CsvHeader("stay_any") + ",chose_move,chose_turn,chose_wait,chose_turn_with_move_candidate,chose_turn_without_move_candidate,turn_over_move_gap_mean,chose_turn_with_wait_candidate,turn_over_wait_gap_mean,walk_postponed,stay_rows,stay_chose_leave,stay_chose_local,stay_chose_loop,stay_not_leave_with_leave_candidate,stay_not_leave_without_leave_candidate,stay_leave_gap_mean,field_builds,field_cache_hits,field_cells_reached,field_build_ms,field_grid_builds,field_grid_ms,local_stay_unjudged_clips,searched_npc_cycles,starts_without_field_path,goals_in_closed_cells,recovery_patches,recovery_found,recovery_ms,recovery_reads,recovery_overlapping_starts,recovery_overlapping_found,goal_l1_static,goal_l1_prop,goal_l1_both,goal_l2_static,goal_l2_prop,goal_l2_both,goal_l3_static,goal_l3_prop,goal_l3_both,goal_l4_static,goal_l4_prop,goal_l4_both,goal_l1_in_stay,goal_l2_in_stay,goal_l3_in_stay,goal_l4_in_stay,straight_fallback_s,patch_value_s,patch_estimate_s,searches,search_timeouts,search_caps,work_limits,expansions,regeneration_rounds,candidates,cycle_timeouts,plan_hash,estimate_patches,estimate_boundary_patches,estimate_boundary_step_most,reach_agent_cycles,reach_set_events,reach_none_events,reach_set_unverified_events,reach_arrivals,led_goals,led_goals_verified,led_goals_unverified,no_point_goals,led_and_no_point_goals,standoff_goals,reached_no_point_goals,standoff_set,standoff_none,standoff_arrivals,given_up_for_props,picked_again_for_props,reached_while_at_standoff,prefix_shortened,prefix_only_event,prefix_start_inside,prefix_recheck_samples,prefix_recheck_ms,prefix_recheck_most_ms_a_cycle,mask_builds,mask_ms,mask_most_ms,window_moves,windows_not_the_records,mask_delays,publications_held_for_order,agent_s_in_scope,prefix_cut_s,window_cycles,window_off_centre_mean_m,window_off_centre_most_m,npc_cycles_within_25m_of_player,of_those_outside_window,of_those_plan_6s_outside_window" + ",all_stay_s_per_1000,near_stay_s_per_1000,all_rotating_on_s_per_1000,near_rotating_on_s_per_1000,all_rotating_seconds_per_1000,near_rotating_seconds_per_1000";

        public string ToCsv()
        {
            return FormattableString.Invariant($"{Name},{From:F2},{To:F2},{AgentSeconds:F1},{Cycles},{CyclesPublished},{CyclesUnpublished},{CyclesStale},{CyclesStaleVisible},{Starts},{StartsInView},{StartsUnplaced},{StartsRefusedInProps},{StartsInsideProps},{StartsOverlappingProps},{Retired},{RetirementsNotMatched},")
                + FormattableString.Invariant($"{Crossings},{CrossingSeconds:F2},{OverlapSeconds:F2},{NearSeconds:F2},{StandingNearSeconds:F2},{GoalsReached:F0},{StuckGoalEvents},{TravelMeters:F1},{PropSpawnRejects},{PropGoalRejects},{PropPredictedHits},{PropPredictedSeconds:F1},")
                + FormattableString.Invariant($"{MapsBuilt},{MapsUsedByCycles},{CyclesWithoutMap},{MapAgeAtCycleMean * 1000:F0},{MapAgeAtCycleMost * 1000:F0},{MapPublishDelayMean * 1000:F0},{MapPublishDelayMost * 1000:F0},{AgentStepsOutsideWindow},{MostOccupiedCells},{MostItems},{MostSections},{LowFragments},")
                + FormattableString.Invariant($"{MapBuildMillisecondsMean:F3},{MapBuildMillisecondsMost:F3},{MapBuildMillisecondsInAll:F1},{CycleMillisecondsMean:F2},{CycleMillisecondsMost:F2},{CycleMillisecondsInAll:F1},{PrepareMillisecondsInAll:F1},{ReplayWallSeconds:F2},")
                + FormattableString.Invariant($"{PrepareMillisecondsMean:F2},{FieldMillisecondsInAll:F1},{FieldResets},{WholeCycleMillisecondsMean:F2},{DwellMeasureMilliseconds:F1},") + DwellAll.ToCsv() + "," + DwellNear.ToCsv() + "," + DwellNearStatic.ToCsv() + "," + DwellNearAny.ToCsv()
                + FormattableString.Invariant($",{ChoseMove},{ChoseTurn},{ChoseWait},{ChoseTurnWithMoveCandidate},{ChoseTurnWithoutMoveCandidate},{(ChoseTurnWithMoveCandidate > 0 ? TurnOverMoveGapSum / ChoseTurnWithMoveCandidate : 0):F3},{ChoseTurnWithWaitCandidate},{(ChoseTurnWithWaitCandidate > 0 ? TurnOverWaitGapSum / ChoseTurnWithWaitCandidate : 0):F3},{WalkPostponed},{StayRows},{StayChoseLeave},{StayChoseLocal},{StayChoseLoop},{StayNotLeaveWithLeaveCandidate},{StayNotLeaveWithoutLeaveCandidate},{(StayNotLeaveWithLeaveCandidate > 0 ? StayLeaveGapSum / StayNotLeaveWithLeaveCandidate : 0):F3},{FieldBuilds},{FieldCacheHits},{FieldCellsReached},{FieldBuildMilliseconds:F1},{FieldGridBuilds},{FieldGridMilliseconds:F1},{LocalStayUnjudgedClips},{SearchedAgentCycles},{StartsWithoutFieldPath},{GoalsInClosedCells},{RecoveryPatches},{RecoveryFound},{RecoveryMilliseconds:F1},{RecoveryReads},{RecoveryOverlapping},{RecoveryOverlappingFound},{GoalClosed[3]},{GoalClosed[4]},{GoalClosed[5]},{GoalClosed[6]},{GoalClosed[7]},{GoalClosed[8]},{GoalClosed[9]},{GoalClosed[10]},{GoalClosed[11]},{GoalClosed[12]},{GoalClosed[13]},{GoalClosed[14]},{GoalClosedInStay[1]},{GoalClosedInStay[2]},{GoalClosedInStay[3]},{GoalClosedInStay[4]},{StraightFallbackSeconds:F1},{PatchSeconds:F1},{EstimateSeconds:F1},{Searches},{SearchTimeouts},{SearchCaps},{(WorkLimits ? 1 : 0)},{Expansions},{RegenerationRounds},{CandidatesTotal},{CycleTimeouts},{PlanHash:X16},{EstimatePatches},{EstimateBoundaryPatches},{EstimateBoundaryStepMost:F2},{ReachAgentCycles},{ReachFound},{ReachNone},{ReachUnverified},{ReachArrivals},{LedGoals},{LedGoalsVerified},{LedGoalsUnverified},{NoPointGoals},{LedAndNoPointGoals},{StandoffGoals},{ReachedNoPointGoals},{StandoffSet},{StandoffNone},{StandoffArrivals},{GivenUpForProps},{PickedAgainForProps},{ReachedWhileAtStandoff},{PrefixShortened},{PrefixOnlyEvent},{PrefixStartInside},{PrefixRecheckSamples},{PrefixRecheckMilliseconds:F3},{PrefixRecheckMostMilliseconds:F4},{MaskBuilds},{MaskMilliseconds:F3},{MaskMostMilliseconds:F3},{WindowMoves},{WindowsNotTheRecords},{MaskDelays},{PublicationsHeldForOrder},{AgentSecondsInScope:F1},{PrefixCutSeconds:F1},{WindowCycles},{(WindowCycles > 0 ? WindowOffCentreSum / WindowCycles : 0):F2},{WindowOffCentreMost:F2},{NpcCyclesNearPlayer},{NpcCyclesNearOutsideWindow},{NpcCyclesNearPlanOutsideWindow}")
                + FormattableString.Invariant($",{Per(DwellAll.Seconds):F1},{Per(DwellNear.Seconds):F1},{Per(DwellAll.RotatingOnSeconds):F1},{Per(DwellNear.RotatingOnSeconds):F1},{Per(DwellAll.RotatingWindowSeconds):F1},{Per(DwellNear.RotatingWindowSeconds):F1}");
        }
    }

    /// <summary>
    /// PROP-OBSTACLE-1: a minimal simulation of the crowd from a capture (PropCapture.Session), on virtual time, with the
    /// product's Core -- the simulation, the planner and the map builder -- and nothing else: no scene, no bones. A section
    /// starts at a saved cycle (the simulation's state and the published plans of that moment); from there the NPCs follow the
    /// plans THIS run makes. The player, the fragments' poses and the cuts' retirements come from the record (the outside
    /// inputs); a retirement takes the NPC nearest the recorded place. The host's part of the crowd -- accepting a cycle,
    /// dropping a start in view or in a felled piece -- is redone here after MobPlanCrowd, without slots or frame budgets.
    /// What is measured against "the sections" uses one yardstick map for every variant: the record's plane, 0.1 m cells,
    /// the build newest at that moment.
    /// </summary>
    public sealed class PropReplay
    {
        private readonly PropCapture.Session session;
        private readonly ClipDataset dataset;
        private readonly WalkableMap map;
        private readonly PlannerConfig config;
        private readonly ScenarioSettings scenario;
        private const float YardstickCell = 0.1f;
        /// <summary>The record's capture square (m; 0: the record holds the shapes of its windows only).</summary>
        public float CaptureSquareMeters;
        private readonly Dictionary<float, float[]> placedWindows = new Dictionary<float, float[]>();

        /// <summary>The window's corner (x, z) at every build of the session under the product's follow rule with this margin (PropObstacleSource.BeginGather), from the first build on.</summary>
        public float[] Windows(float margin)
        {
            if (placedWindows.TryGetValue(margin, out var known)) return known;
            var corners = new float[session.Builds.Count * 2];
            bool has = false; float ox = 0f, oz = 0f;
            for (var i = 0; i < session.Builds.Count; i++)
            {
                var source = session.Builds[i].Input;
                // the player the product decided this build's window with; a record without it has the player at the gathering's end only
                WindowPlayer(source, out float px, out float pz);
                PropObstacleWindow.Follow(source.Cells * source.Cell, source.Cell, margin, ref has, ref ox, ref oz, px, pz);
                corners[i * 2] = ox; corners[i * 2 + 1] = oz;
            }

            placedWindows[margin] = corners;
            return corners;
        }

        public static void WindowPlayer(PropObstacleInput source, out float x, out float z)
        {
            bool recorded = !float.IsNaN(source.WindowPlayerX) && !float.IsNaN(source.WindowPlayerZ);
            x = recorded ? source.WindowPlayerX : source.PlayerX; z = recorded ? source.WindowPlayerZ : source.PlayerZ;
        }

        /// <summary>
        /// The yardstick's input: the record's window, or -- with a capture square -- that square about the player the build's
        /// window was decided with (the square's own centre), the same for every variant. The sections of the whole square serve
        /// the distances; what is COUNTED is the 50 m about the player alone (InScope).
        /// </summary>
        private PropObstacleInput YardInput(int build, float plane)
        {
            if (CaptureSquareMeters <= 0f) return InputOf(build, YardstickCell, plane);
            var source = session.Builds[build].Input;
            WindowPlayer(source, out float px, out float pz);
            float ox = (float)Math.Floor((px - CaptureSquareMeters * 0.5f) / YardstickCell) * YardstickCell;
            float oz = (float)Math.Floor((pz - CaptureSquareMeters * 0.5f) / YardstickCell) * YardstickCell;
            return InputOf(build, YardstickCell, plane, ox, oz, CaptureSquareMeters);
        }

        /// <summary>
        /// What the entries, the time inside, overlapping and near are counted over, with a capture square: the square of the
        /// window's size centred on the player (the ground the map is meant to cover), the same for every way of following.
        /// Without a capture square: everywhere the yardstick has sections (the record's window), as before.
        /// </summary>
        private bool InScope(float x, float z, float playerX, float playerZ, float windowMeters)
            => CaptureSquareMeters <= 0f || (Math.Abs(x - playerX) <= windowMeters * 0.5f && Math.Abs(z - playerZ) <= windowMeters * 0.5f);

        /// <summary>One section of Recorded: what a replay's results.csv row counts of the same things.</summary>
        public sealed class RecordedSection
        {
            public double From, To, AgentSeconds, AgentSecondsInScope, InsideSeconds, OverlapSeconds, NearSeconds, StandingNearSeconds;
            public int Entries, InsideAtStart, Starts, StartsInScope, StartsInside, StartsOverlapping, Retired, Cycles, CyclesPublished, WindowMoves;
            public static string CsvHeader => "from,to,agent_s,agent_s_in_scope,crossings,inside_at_section_start,crossing_s,overlap_s,near_s,standing_near_s,starts,starts_in_scope,starts_in_scope_inside_props,starts_in_scope_overlapping_props,retired,cycles,cycles_published,window_moves";
            public string ToCsv() => FormattableString.Invariant($"{From:F2},{To:F2},{AgentSeconds:F2},{AgentSecondsInScope:F2},{Entries},{InsideAtStart},{InsideSeconds:F2},{OverlapSeconds:F2},{NearSeconds:F2},{StandingNearSeconds:F2},{Starts},{StartsInScope},{StartsInside},{StartsOverlapping},{Retired},{Cycles},{CyclesPublished},{WindowMoves}");
        }

        public sealed class RecordedResult
        {
            public const string EntryHeader = "id,enter,exit,seconds,ended,enter_x,enter_z,exit_x,exit_z,deepest_m";
            public const string StartHeader = "time,id,x,z,player_x,player_z,in_scope,inside_props,overlapping_props,distance_m,plan_x,plan_z,plan_differs_m";
            public readonly List<RecordedSection> Sections = new List<RecordedSection>();
            public readonly List<string> EntryRows = new List<string>();
            /// <summary>One row for each start in scope that overlaps the yardstick's sections, or has no place in a saved plan, or whose saved plan's place is more than 0.05 m from the event's.</summary>
            public readonly List<string> StartRows = new List<string>();
            /// <summary>Steps on stage with a place, by NPC.</summary>
            public readonly Dictionary<int, int> StepsOf = new Dictionary<int, int>();
            public double From, To; public float Step, Plane, ScopeMeters, CaptureSquareMeters;
            public int YardBuilds, Cycles, CyclesWhoseNpcsAreNotTheEventsNpcs, CollectionsNotFound, RetirementsOfNoNpc, StartsOfAKnownNpc, StartsWithoutAPlace, StartsComparedWithThePlan;
            public float StartPlaceDifferenceMost;
            public long NpcSteps, NpcStepsWithoutAPlace, NpcStepsOnTheNextCyclesPlan;
            public string Describe() => FormattableString.Invariant($"recorded play, the saved plans followed: {From:F2} to {To:F2} s, step {Step:F5} s, yardstick plane {Plane:F2} m at {YardstickCell:F2} m cells over {(CaptureSquareMeters > 0f ? CaptureSquareMeters : ScopeMeters):F1} m, counted within {(CaptureSquareMeters > 0f ? "the square of the window's size about the player" : "the record's window")} ({ScopeMeters:F1} m)\n")
                + FormattableString.Invariant($"yardstick builds {YardBuilds}; saved cycles passed {Cycles}, of them with NPCs other than the events gave {CyclesWhoseNpcsAreNotTheEventsNpcs}, without a collection on record {CollectionsNotFound}; retirements of no NPC on stage {RetirementsOfNoNpc}; starts of an NPC already on stage {StartsOfAKnownNpc}; starts without a place in a saved plan {StartsWithoutAPlace} (judged at the event's place all the same); starts whose event's place was compared with the saved plan's {StartsComparedWithThePlan}, the most apart {StartPlaceDifferenceMost:F3} m\n")
                + FormattableString.Invariant($"NPC steps {NpcSteps}, of them without a place in any saved plan {NpcStepsWithoutAPlace}, placed by the next cycle's saved plan (after the collection) {NpcStepsOnTheNextCyclesPlan}");
        }

        /// <summary>
        /// A saved cycle's time is its frame's time as a float; an event's is the frame's time as a double. The frame an event
        /// belongs to, on the cycles' scale.
        /// </summary>
        public static double CycleScale(double eventTime) => (double)(float)eventTime;

        /// <summary>
        /// What the recorded play itself did, counted as Run counts a replay: no planning here. The NPCs on stage are those of
        /// a saved cycle (its plans less its waiting ones) changed by the record's retirements and starts AFTER it, all taken in
        /// the order of the play: in a frame the crowd retires and starts first and queues its cycle last (MobPlanCrowd.
        /// UpdateCrowd), so an event of a cycle's own frame comes before the cycle and is in it already, and an event of a later
        /// frame comes after it -- also where both fall into one step of this count. An NPC's place is that of its saved plan. A
        /// cycle's own publication is not saved apart, so after the cycle's collection (published) the plan the NEXT saved cycle
        /// starts with is taken where it covers the time -- it is what was published, or has the same frozen part. The yardstick
        /// and the scope (the window's size about the player) are Run's; a start is counted in scope by the player of its moment,
        /// at the place its event recorded (the place the crowd judged the start at); the saved plan's place is compared with it.
        /// </summary>
        public RecordedResult Recorded(double from, double to, float sectionSeconds, float plane = 0f, float step = 1f / 72f)
        {
            var result = new RecordedResult { Step = step, CaptureSquareMeters = CaptureSquareMeters };
            if (session.Starts.Count == 0 || session.Builds.Count == 0) return result;
            int c = 0;
            while (c + 1 < session.Starts.Count && session.Starts[c + 1].Time <= from) c++;
            double t0 = Math.Max(from, session.Starts[c].Time);
            to = Math.Min(to, session.Starts[session.Starts.Count - 1].Time);
            result.From = t0; result.To = to;
            float radius = scenario.npcRadius;
            float scopeMeters = session.Builds[0].Input.Cells * session.Builds[0].Input.Cell;
            result.ScopeMeters = scopeMeters;
            var collected = new Dictionary<double, PropCapture.CycleEnd>();
            foreach (var e in session.Ends) collected[e.QueuedTime] = e;

            Dictionary<int, PublishedMobPlan> PlansOf(int cycle)
            {
                var plans = new Dictionary<int, PublishedMobPlan>();
                var start = session.Starts[cycle];
                for (int i = 0; i < start.Baseline.Count; i++)
                    if (start.Baseline[i].Segments.Count > 0) plans[start.Baseline[i].AgentId] = new PublishedMobPlan(start.Baseline[i], start.BaselineGeneration[i]);
                return plans;
            }

            HashSet<int> OnStage(int cycle)
            {
                var start = session.Starts[cycle];
                var on = new HashSet<int>();
                foreach (var plan in start.Baseline) on.Add(plan.AgentId);
                foreach (int id in start.Waiting) on.Remove(id);
                return on;
            }

            Dictionary<int, PublishedMobPlan> now = PlansOf(c), next = c + 1 < session.Starts.Count ? PlansOf(c + 1) : null;
            HashSet<int> stage = OnStage(c);
            collected.TryGetValue(session.Starts[c].Time, out var end);
            if (end == null) result.CollectionsNotFound++;
            result.Cycles = 1;
            // the events of the first cycle's frame and before are in that cycle
            int eventAt = 0;
            while (eventAt < session.Events.Count && CycleScale(session.Events[eventAt].Time) <= session.Starts[c].Time) eventAt++;

            var yardBuilder = new PropObstacleBuilder(); var yard = new PropObstacleMap(); int yardBuild = -1;
            var inside = new Dictionary<int, (double enter, float x, float z, float deepest)>();
            var previous = new Dictionary<int, Pose2>();
            var ids = new List<int>();
            var section = new RecordedSection { From = t0, To = t0 + sectionSeconds };
            result.Sections.Add(section);
            float lastOriginX = float.NaN, lastOriginZ = float.NaN;

            void Close(int id, double at, float x, float z, string ended)
            {
                if (!inside.TryGetValue(id, out var entry)) return;
                inside.Remove(id);
                result.EntryRows.Add(FormattableString.Invariant($"{id},{entry.enter:F2},{at:F2},{at - entry.enter:F2},{ended},{entry.x:F2},{entry.z:F2},{x:F2},{z:F2},{entry.deepest:F2}"));
            }

            // the yardstick: Run's -- the newest recorded build at that time
            void Yard(double at)
            {
                int truth = session.BuildIndexAt(at);
                if (truth == yardBuild || truth < 0) return;
                var source = session.Builds[truth].Input;
                result.Plane = plane > 0f ? plane : source.PlaneY;
                yardBuilder.Build(YardInput(truth, result.Plane), null, yard);
                yardBuild = truth; result.YardBuilds++;
                if (!float.IsNaN(lastOriginX) && (source.OriginX != lastOriginX || source.OriginZ != lastOriginZ)) section.WindowMoves++;
                lastOriginX = source.OriginX; lastOriginZ = source.OriginZ;
            }

            bool Published() => end != null && end.Outcome != null && end.Outcome.StartsWith("published", StringComparison.Ordinal);

            bool Place(int id, double at, out Pose2 pose, out bool byNext)
            {
                byNext = false;
                if (next != null && Published() && at >= end.Time && next.TryGetValue(id, out var later) && later.TryResolve(at, dataset, out _, out _, out pose)) { byNext = true; return true; }
                if (now.TryGetValue(id, out var plan) && plan.TryResolve(at, dataset, out _, out _, out pose)) return true;
                if (next != null && next.TryGetValue(id, out var only) && only.TryResolve(at, dataset, out _, out _, out pose)) return true;   // not in the cycle's plans: started since
                pose = default;
                return false;
            }

            void Apply(PropCapture.Event happened)
            {
                if (happened.Kind == PropCapture.RetiredRecord)
                {
                    if (!stage.Remove(happened.Id)) { result.RetirementsOfNoNpc++; return; }
                    previous.Remove(happened.Id);
                    Close(happened.Id, happened.Time, float.NaN, float.NaN, "retired");
                    section.Retired++;
                }
                else if (happened.Kind == PropCapture.StartAttemptRecord && happened.Result == PropCapture.StartStarted)
                {
                    if (!stage.Add(happened.Id)) { result.StartsOfAKnownNpc++; return; }
                    section.Starts++;
                    // where: the place the event recorded -- the one the crowd judged the start at. The saved plan's place at
                    // that moment is compared with it (a start whose plan is in no saved cycle has none to compare).
                    float x = happened.X, z = happened.Z;
                    float differs = float.NaN; Pose2 planned = new Pose2(float.NaN, float.NaN, 0f);
                    if (Place(happened.Id, happened.Time, out Pose2 found, out _))
                    {
                        planned = found;
                        differs = (float)Math.Sqrt((found.X - x) * (found.X - x) + (found.Z - z) * (found.Z - z));
                        result.StartsComparedWithThePlan++;
                        if (differs > result.StartPlaceDifferenceMost) result.StartPlaceDifferenceMost = differs;
                    }
                    else result.StartsWithoutAPlace++;

                    var player = session.PlayerAt(happened.Time);
                    if (!InScope(x, z, player.X, player.Z, scopeMeters)) return;
                    section.StartsInScope++;
                    Yard(happened.Time);
                    bool sections = yardBuild >= 0 && yard.OccupiedCells > 0;
                    bool within = sections && yard.OccupiedAt(x, z), overlapping = sections && yard.Overlaps(x, z, radius);
                    if (within) section.StartsInside++;
                    if (overlapping) section.StartsOverlapping++;
                    if (overlapping || float.IsNaN(differs) || differs > 0.05f)
                        result.StartRows.Add(FormattableString.Invariant($"{happened.Time:F2},{happened.Id},{x:F2},{z:F2},{player.X:F2},{player.Z:F2},1,{(within ? 1 : 0)},{(overlapping ? 1 : 0)},{(sections ? yard.DistanceAt(x, z) : float.PositiveInfinity):F2},{planned.X:F2},{planned.Z:F2},{differs:F3}"));
                }
            }

            for (double t = t0; t <= to; t += step)
            {
                while (t >= section.To)
                {
                    section = new RecordedSection { From = section.To, To = section.To + sectionSeconds, InsideAtStart = inside.Count };
                    result.Sections.Add(section);
                }

                // ---- the record's events and saved cycles up to now, in the order of the play ----
                while (true)
                {
                    bool anEvent = eventAt < session.Events.Count && Math.Min(session.Events[eventAt].Time, CycleScale(session.Events[eventAt].Time)) <= t;
                    bool aCycle = c + 1 < session.Starts.Count && session.Starts[c + 1].Time <= t;
                    // an event of the cycle's frame or of one before comes first; the cycle comes before the events of later frames
                    if (anEvent && (!aCycle || CycleScale(session.Events[eventAt].Time) <= session.Starts[c + 1].Time)) { Apply(session.Events[eventAt++]); continue; }
                    if (!aCycle) break;
                    c++;
                    HashSet<int> saved = OnStage(c);
                    if (!saved.SetEquals(stage)) result.CyclesWhoseNpcsAreNotTheEventsNpcs++;
                    ids.Clear(); ids.AddRange(stage);
                    foreach (int id in ids) if (!saved.Contains(id)) { previous.Remove(id); Close(id, session.Starts[c].Time, float.NaN, float.NaN, "gone-at-a-cycle"); }
                    stage = saved;
                    now = next ?? PlansOf(c);
                    next = c + 1 < session.Starts.Count ? PlansOf(c + 1) : null;
                    collected.TryGetValue(session.Starts[c].Time, out end);
                    if (end == null) result.CollectionsNotFound++;
                    result.Cycles++; section.Cycles++;
                    if (Published()) section.CyclesPublished++;
                }

                Yard(t);
                var playerNow = session.PlayerAt(t);
                ids.Clear(); ids.AddRange(stage);
                foreach (int id in ids)
                {
                    result.NpcSteps++;
                    if (!Place(id, t, out Pose2 pose, out bool byNext)) { result.NpcStepsWithoutAPlace++; continue; }
                    if (byNext) result.NpcStepsOnTheNextCyclesPlan++;
                    result.StepsOf.TryGetValue(id, out int steps); result.StepsOf[id] = steps + 1;
                    section.AgentSeconds += step;
                    bool inScope = InScope(pose.X, pose.Z, playerNow.X, playerNow.Z, scopeMeters);
                    if (inScope) section.AgentSecondsInScope += step;
                    bool isInside = false;
                    if (yardBuild >= 0 && yard.OccupiedCells > 0)
                    {
                        float d = inScope ? yard.DistanceAt(pose.X, pose.Z) : float.PositiveInfinity;
                        isInside = inScope && yard.OccupiedAt(pose.X, pose.Z);
                        if (isInside) section.InsideSeconds += step;
                        if (d < radius) section.OverlapSeconds += step;
                        else if (d < radius + 1f) section.NearSeconds += step;
                        if (previous.TryGetValue(id, out var before))
                        {
                            float moved = (float)Math.Sqrt((pose.X - before.X) * (pose.X - before.X) + (pose.Z - before.Z) * (pose.Z - before.Z));
                            if (moved / step < 0.05f && d < radius + 1.5f) section.StandingNearSeconds += step;
                        }

                        bool wasInside = inside.TryGetValue(id, out var going);
                        if (isInside && !wasInside) { section.Entries++; inside[id] = (t, pose.X, pose.Z, d); }
                        else if (isInside && d < going.deepest) inside[id] = (going.enter, going.x, going.z, d);
                        else if (!isInside && wasInside) Close(id, t, pose.X, pose.Z, "left");
                    }
                    else if (inside.ContainsKey(id)) Close(id, t, pose.X, pose.Z, "no-sections");

                    previous[id] = pose;
                }
            }

            ids.Clear(); ids.AddRange(inside.Keys);
            foreach (int id in ids) Close(id, to, float.NaN, float.NaN, "end");
            return result;
        }

        public PropReplay(PropCapture.Session session, ClipDataset dataset, WalkableMap map, PlannerConfig config, ScenarioSettings scenario)
        {
            this.session = session; this.dataset = dataset; this.map = map; this.config = config; this.scenario = scenario;
        }

        /// <summary>The last yardstick map and sections of a run (a viewer reads them after Run with KeepFrames).</summary>
        public PropObstacleInput InputOf(int build, float cell, float plane, float originX = float.NaN, float originZ = float.NaN, float windowMeters = 0f)
        {
            var source = session.Builds[build].Input;
            var input = new PropObstacleInput();
            float size = windowMeters > 0f ? windowMeters : source.Cells * source.Cell;
            input.OriginX = float.IsNaN(originX) ? source.OriginX : originX; input.OriginZ = float.IsNaN(originZ) ? source.OriginZ : originZ;
            input.Cell = cell > 0f ? cell : source.Cell;
            input.Cells = Math.Max(8, (int)Math.Round(size / input.Cell));
            input.PlaneY = plane > 0f ? plane : source.PlaneY;
            input.MaxDistance = source.MaxDistance;
            input.Time = source.Time; input.PlayerX = source.PlayerX; input.PlayerZ = source.PlayerZ;
            input.WindowPlayerX = source.WindowPlayerX; input.WindowPlayerZ = source.WindowPlayerZ;
            for (var i = 0; i < source.Count; i++)
            {
                var index = input.Add(source.Shapes[i], source.Fragments[i], source.Roots[i], source.Sides[i]);
                Array.Copy(source.Matrices, i * 12, input.Matrices, index * 12, 12);
            }

            return input;
        }

        /// <summary>What a cycle knew of the plan it chose for an NPC (for the record of an entry into the sections).</summary>
        private struct PlanNote
        {
            public double CycleTime, MapInputTime, LeastAt, Boundary, LeastAtAfterBoundary;
            public float LeastAfterBoundary;
            /// <summary>The plan's own track against its cycle's map: the distance to the prop cells every 0.1 s from the cycle's time (null: no map).</summary>
            public float[] Track;
            /// <summary>The plan's places at the track's times, and the window and the occupied cells of the cycle's map (an infinite distance means outside the window or nothing occupied).</summary>
            public float[] TrackX, TrackZ;
            public float MapOriginX, MapOriginZ, MapSize, MapMinX, MapMaxX, MapMinZ, MapMaxZ;
            public int MapOccupied;
            public int MapVersion, Candidates, CleanCandidates;
            public float OverlapSeconds, Dynamic, Total, LeastDistance, BestCleanTotal;
            public string Origin;
            public bool Changed, Has;
        }

        private sealed class Crossing
        {
            public double Enter;
            public float EnterX, EnterZ, Deepest, PlannerAtEntry, PlannerLeast;
            public int PlannerVersion; public double PlannerInputTime; public bool PlannerOccupied;
            public PlanNote Note;
            /// <summary>The last cycle that could still change where the NPC is at the entry (its fixed part ends at or before the entry), and what its map said of the plan's place at the entry's time.</summary>
            public PlanNote Deciding; public float DecidingDistance, DecidingPlaceX = float.NaN, DecidingPlaceZ = float.NaN;
            public float GoalX, GoalZ, PlanGoalX, PlanGoalZ; public bool Led, Standoff, HasAgent; public int FlowPhase, GoalStage;
            public double LastAbandon;
            public string Clip;
        }

        private sealed class Pending
        {
            public Dictionary<int, PlanNote> Notes;
            public CycleResult Cycle;
            public Dictionary<int, PublishedMobPlan> Plans;
            public List<int> Relocated;
            public HashSet<int> Baseline;
            public int Generation;
            public double CollectAt;
        }

        public PropReplayResult Run(double from, double to, PropReplayOptions options)
        {
            var wall = Stopwatch.StartNew();
            var result = new PropReplayResult { Name = options.Name };
            // the section's start: the first saved cycle at or after `from`
            PropCapture.CycleStart start = null;
            foreach (var c in session.Starts) if (c.Time >= from && c.State != null) { start = c; break; }
            if (start == null) throw new InvalidOperationException("no saved cycle at or after " + from);
            var simConfig = config.Clone();
            simConfig.propObstacles = options.Props;
            simConfig.propGoalField = options.GoalField;
            simConfig.idleCostLowTranslation = options.IdleFix;
            simConfig.localStayCost = options.LocalStay;
            simConfig.localStayNoRestoreForTest = options.LocalStayNoRestore;
            simConfig.propGoalFieldCellMeters = options.GoalFieldCell;
            if (options.FieldCellAlways > 0f) simConfig.goalFieldCellMeters = options.FieldCellAlways;
            simConfig.goalFieldMetricReach = options.MetricReach;
            simConfig.goalFieldRecovery = options.Recovery;
            simConfig.goalFieldRecoveryExtrapolate = options.Extrapolate;
            simConfig.beamKeyExtraRoundingForTest = options.ExtraRounding;
            simConfig.workLimitsOnly = options.WorkLimits;
            simConfig.propGoalReach = options.GoalReach;
            simConfig.propGoalNoReachMode = options.NoReachMode;
            if (options.PrefixRecheck >= 0f) { simConfig.propPrefixRecheck = true; simConfig.propPrefixRecheckCommitSeconds = options.PrefixRecheck; simConfig.propPrefixRecheckClearStart = options.PrefixRecheckClearStart; }
            result.WorkLimits = options.WorkLimits;
            simConfig.propGoalFieldClosureMeters = options.PropBand;
            simConfig.traceAgents = options.TraceAgents;
            var sim = new LocomotionSimulation(dataset, simConfig) { AutoPlan = false, ExternalPlayer = true };
            sim.Reset(scenario.Clone(), map);
            using (var r = new BinaryReader(new MemoryStream(start.State, false))) sim.LoadState(r);
            int generation = start.Generation;
            var actors = new Dictionary<int, PublishedMobPlan>();
            var waiting = new Dictionary<int, PublishedMobPlan>();
            var waitingIds = new HashSet<int>(start.Waiting);
            for (var i = 0; i < start.Baseline.Count; i++)
            {
                var plan = new PublishedMobPlan(start.Baseline[i], start.BaselineGeneration[i]);
                if (waitingIds.Contains(plan.AgentId)) waiting[plan.AgentId] = plan; else actors[plan.AgentId] = plan;
            }

            double t0 = start.Time;
            result.From = t0; result.To = to;
            float goals0 = sim.TotalGoalsReached; int stuck0 = sim.StuckGoalEvents;
            float radius = scenario.npcRadius;

            // the planner's maps of this variant, and the yardstick
            var pool = new PropObstacleMapPool();
            var builder = new PropObstacleBuilder();
            var yardBuilder = new PropObstacleBuilder();
            var yard = new PropObstacleMap();
            var yardOther = new PropObstacleMap();
            int yardBuild = -1;
            byte[] mask = null; float maskX = float.NaN, maskZ = float.NaN; int maskCells = 0;
            int nextBuild = Math.Max(0, session.BuildIndexAt(t0));   // the build newest at the start is made first
            float[] placed = options.FollowMargin >= 0f ? Windows(options.FollowMargin) : null;
            float scopeMeters = session.Builds.Count > 0 ? session.Builds[0].Input.Cells * session.Builds[0].Input.Cell : 50f;
            if (session.Builds.Count == 0) nextBuild = int.MaxValue;
            var due = new List<(double at, PropObstacleMap map, double input)>();
            double lastPublishAt = double.NegativeInfinity, lastPublishedInput = double.NegativeInfinity;
            double buildMs = 0, buildMsMost = 0; double delaySum = 0, delayMost = 0; int delays = 0;
            double ageSum = 0, ageMost = 0;
            double cycleMs = 0, cycleMsMost = 0, prepareMs = 0;

            Pending pending = null;
            CycleResult lastAccepted = null;
            bool busy = false;
            double nextRequest = t0;
            bool first = true;
            int eventAt = 0;
            while (eventAt < session.Events.Count && session.Events[eventAt].Time < t0) eventAt++;
            options.Dwell.GoalRadiusMeters = config.goalRadiusMeters;
            var dwell = new DwellTracker(options.Dwell, radius);
            var plannedWalk = new Dictionary<int, double>();
            // whether a point 3 m away can be reached from (x, z) without entering the yardstick's sections or the static map (0.25 m steps, 4 m about)
            dwell.EscapeProbe = (px, pz) =>
            {
                const int half = 16; const float step = 0.25f;
                int side = half * 2 + 1;
                var seen = new bool[side * side];
                var queue = new Queue<int>();
                seen[half * side + half] = true; queue.Enqueue(half * side + half);
                while (queue.Count > 0)
                {
                    int cell = queue.Dequeue();
                    int cx = cell % side - half, cz = cell / side - half;
                    if (cx * cx + cz * cz >= 12 * 12) return "path";
                    for (int n = 0; n < 4; n++)
                    {
                        int nx = cx + (n == 0 ? 1 : n == 1 ? -1 : 0), nz = cz + (n == 2 ? 1 : n == 3 ? -1 : 0);
                        if (nx < -half || nz < -half || nx > half || nz > half) continue;
                        int index = (nz + half) * side + nx + half;
                        if (seen[index]) continue;
                        seen[index] = true;
                        float wx = px + nx * step, wz = pz + nz * step;
                        if (map.ClearanceAt(wx, wz) < radius) continue;
                        if (yardBuild >= 0 && yard.OccupiedCells > 0 && yard.DistanceAt(wx, wz) < radius) continue;
                        queue.Enqueue(index);
                    }
                }

                return "no-path";
            };
            var dwellClock = new Stopwatch();
            var goalSeen = new Dictionary<int, (int revision, int reached, bool blocked)>();
            double fieldMs0 = sim.GoalFieldMilliseconds; int fieldResets0 = sim.PropFieldResets;
            int fieldBuilds0 = sim.GoalFieldBuilds, fieldHits0 = sim.GoalFieldCacheHits, gridBuilds0 = sim.FieldGridBuilds; long fieldCells0 = sim.GoalFieldCellsReached;
            double fieldBuildMs0 = sim.GoalFieldBuildMilliseconds, gridMs0 = sim.FieldGridMilliseconds;
            var inside = new Dictionary<int, bool>();
            var previous = new Dictionary<int, Pose2>();
            var sincePath = new Dictionary<int, float>(); var sinceYaw = new Dictionary<int, float>();   // per NPC since its last row of NpcRows
            var planNotes = new Dictionary<int, PlanNote>(); var crossings = new Dictionary<int, Crossing>(); var lastAbandon = new Dictionary<int, double>(); var abandonSeen = new Dictionary<int, int>();
            Dictionary<int, PlanNote> cycleNotes = null;
            var noteHistory = new Dictionary<int, List<PlanNote>>();
            void CloseCrossing(int id, double at, float x, float z, string ended)
            {
                if (!crossings.TryGetValue(id, out var c)) return;
                crossings.Remove(id);
                var n = c.Note;
                result.CrossingRows.Add(FormattableString.Invariant($"{id},{c.Enter:F2},{at:F2},{at - c.Enter:F2},{ended},{c.EnterX:F2},{c.EnterZ:F2},{x:F2},{z:F2},{c.Deepest:F2},{c.Clip},{c.PlannerVersion},{c.PlannerInputTime:F2},{c.Enter - c.PlannerInputTime:F2},{c.PlannerAtEntry:F2},{(c.PlannerOccupied ? 1 : 0)},{c.PlannerLeast:F2},")
                    + (n.Has ? FormattableString.Invariant($"{n.CycleTime:F2},{c.Enter - n.CycleTime:F2},{n.MapVersion},{n.MapInputTime:F2},{n.Origin},{(n.Changed ? 1 : 0)},{n.OverlapSeconds:F2},{n.Dynamic:F3},{n.Total:F3},{n.LeastDistance:F2},{n.LeastAt:F2},{n.Candidates},{n.CleanCandidates},{n.BestCleanTotal:F3},{n.Boundary:F2},{(c.Enter < n.Boundary ? 1 : 0)},{n.LeastAfterBoundary:F2},{n.LeastAtAfterBoundary:F2},") : ",,,,,,,,,,,,,,,,,,")
                    + (c.Deciding.Has ? FormattableString.Invariant($"{c.Deciding.CycleTime:F2},{c.Enter - c.Deciding.CycleTime:F2},{c.Deciding.Boundary:F2},{c.Deciding.MapVersion},{c.Deciding.MapInputTime:F2},{c.Deciding.Origin},{c.DecidingDistance:F2},{c.Deciding.OverlapSeconds:F2},{c.Deciding.Dynamic:F3},{c.Deciding.Candidates},{c.Deciding.CleanCandidates},{c.Deciding.BestCleanTotal:F3},{c.Deciding.Total:F3},") : ",,,,,,,,,,,,,")
                    + (c.HasAgent ? FormattableString.Invariant($"{c.GoalX:F2},{c.GoalZ:F2},{c.PlanGoalX:F2},{c.PlanGoalZ:F2},{(c.Led ? 1 : 0)},{(c.Standoff ? 1 : 0)},{c.FlowPhase},{c.GoalStage},{c.LastAbandon:F2},{(double.IsNaN(c.LastAbandon) ? double.NaN : c.Enter - c.LastAbandon):F2}") : ",,,,,,,,,")
                    + (c.Deciding.Has && c.Deciding.Track != null ? FormattableString.Invariant($",{c.DecidingPlaceX:F2},{c.DecidingPlaceZ:F2},{c.Deciding.MapOriginX:F2},{c.Deciding.MapOriginZ:F2},{c.Deciding.MapSize:F2},{(c.DecidingPlaceX >= c.Deciding.MapOriginX && c.DecidingPlaceZ >= c.Deciding.MapOriginZ && c.DecidingPlaceX < c.Deciding.MapOriginX + c.Deciding.MapSize && c.DecidingPlaceZ < c.Deciding.MapOriginZ + c.Deciding.MapSize ? 1 : 0)},{c.Deciding.MapOccupied},{c.Deciding.MapMinX:F2},{c.Deciding.MapMaxX:F2},{c.Deciding.MapMinZ:F2},{c.Deciding.MapMaxZ:F2}") : ",,,,,,,,,,,"));
            }

            var ledGoals = new HashSet<long>(); var ledVerified = new HashSet<long>(); var ledUnverified = new HashSet<long>(); var noPointGoals = new HashSet<long>(); var standoffGoals = new HashSet<long>();
            var gone = new List<int>();
            var clock = new Stopwatch();
            float dt = options.Step;

            for (double t = t0; t <= to; t += dt)
            {
                var player = session.PlayerAt(t);
                if (first) { player.X = start.PlayerX; player.Z = start.PlayerZ; player.VelocityX = start.VelocityX; player.VelocityZ = start.VelocityZ; player.Yaw = start.Yaw; }

                // ---- the maps: this variant's builds as their inputs come, published after their delay ----
                while (options.Props && nextBuild < session.Builds.Count && session.Builds[nextBuild].Input.Time <= t)
                {
                    var recorded = session.Builds[nextBuild];
                    int index = nextBuild++;
                    if (options.BuildEvery > 1 && index % options.BuildEvery != 0) continue;
                    var input = placed != null ? InputOf(index, options.Cell, options.Plane, placed[index * 2], placed[index * 2 + 1]) : InputOf(index, options.Cell, options.Plane);
                    if (input.OriginX != recorded.Input.OriginX || input.OriginZ != recorded.Input.OriginZ) result.WindowsNotTheRecords++;
                    bool windowMoved = false;
                    if (mask == null || maskCells != input.Cells || maskX != input.OriginX || maskZ != input.OriginZ)
                    {
                        windowMoved = mask != null && maskCells == input.Cells;
                        if (windowMoved) result.WindowMoves++;
                        mask = new byte[input.Cells * input.Cells]; maskCells = input.Cells; maskX = input.OriginX; maskZ = input.OriginZ;
                        long maskFrom = Stopwatch.GetTimestamp();
                        PropObstacleBuilder.StaticMask(map, input.OriginX, input.OriginZ, input.Cell, input.Cells, mask);
                        double maskMs = (Stopwatch.GetTimestamp() - maskFrom) * 1000.0 / Stopwatch.Frequency;
                        result.MaskBuilds++; result.MaskMilliseconds += maskMs; if (maskMs > result.MaskMostMilliseconds) result.MaskMostMilliseconds = maskMs;
                    }

                    var target = pool.Take(input.Cells);
                    clock.Restart();
                    builder.Build(input, mask, target);
                    double ms = clock.Elapsed.TotalMilliseconds;
                    buildMs += ms; if (ms > buildMsMost) buildMsMost = ms;
                    result.MapsBuilt++;
                    result.LowFragments += target.Stats.ItemsBelowPlane;
                    if (target.OccupiedCells > result.MostOccupiedCells) result.MostOccupiedCells = target.OccupiedCells;
                    if (target.Stats.Items > result.MostItems) result.MostItems = target.Stats.Items;
                    if (target.Stats.Sections > result.MostSections) result.MostSections = target.Stats.Sections;
                    double delay = options.PublishDelay >= 0.0 ? options.PublishDelay : !double.IsNaN(recorded.PublishTime) ? Math.Max(0.0, recorded.PublishTime - recorded.Input.Time) : 0.05;
                    if (windowMoved && placed != null && options.MaskDelay > 0.0 && index > 0)
                    {
                        // the static mask is made again on the worker before this build: its publication is that much later -- unless
                        // the record's window moved at this build too (its recorded delay holds that already)
                        var before = session.Builds[index - 1].Input;
                        if (before.OriginX == recorded.Input.OriginX && before.OriginZ == recorded.Input.OriginZ) { delay += options.MaskDelay; result.MaskDelays++; }
                    }

                    if (index == Math.Max(0, session.BuildIndexAt(t0))) delay = 0.0;   // the map standing at the section's start was published before it
                    else { delaySum += delay; delays++; if (delay > delayMost) delayMost = delay; }
                    target.Retain();   // held until it is published (the pool must not hand it out again)
                    // one worker makes the maps one after another: a map is never published before the one made before it (a delay
                    // added to an earlier map would otherwise let a later one out first and then put the older one back)
                    double publishAt = input.Time + delay;
                    if (publishAt < lastPublishAt) { publishAt = lastPublishAt; result.PublicationsHeldForOrder++; }
                    lastPublishAt = publishAt;
                    due.Add((publishAt, target, input.Time));
                }

                for (var i = 0; i < due.Count; i++)
                {
                    if (due[i].at > t) continue;
                    if (due[i].input < lastPublishedInput) throw new InvalidOperationException(FormattableString.Invariant($"a map of input {due[i].input:F3} s would be published after one of {lastPublishedInput:F3} s"));
                    lastPublishedInput = due[i].input;
                    pool.Publish(due[i].map);
                    due[i].map.Release();
                    due.RemoveAt(i--);
                }

                // ---- the yardstick: the newest recorded build, the record's plane, 0.1 m ----
                int truth = session.BuildIndexAt(t);
                if (truth != yardBuild && truth >= 0)
                {
                    var source = session.Builds[truth].Input;
                    if (options.YardPlanes == null || options.YardPlanes.Length == 0) yardBuilder.Build(YardInput(truth, source.PlaneY), null, yard);
                    else
                    {
                        // the union of the sections at several heights: the least of the distances, cell by cell
                        yardBuilder.Build(YardInput(truth, options.YardPlanes[0]), null, yard);
                        for (var p = 1; p < options.YardPlanes.Length; p++)
                        {
                            yardBuilder.Build(YardInput(truth, options.YardPlanes[p]), null, yardOther);
                            if (yardOther.OccupiedCells == 0) continue;
                            var a = yard.Distance; var b = yardOther.Distance;
                            var occupied = 0;
                            for (var i = 0; i < a.Length; i++) { if (b[i] < a[i]) a[i] = b[i]; if (a[i] <= 0f) occupied++; }
                            yard.OccupiedCells = occupied;
                            yard.MinX = Math.Min(yard.MinX, yardOther.MinX); yard.MaxX = Math.Max(yard.MaxX, yardOther.MaxX);
                            yard.MinZ = Math.Min(yard.MinZ, yardOther.MinZ); yard.MaxZ = Math.Max(yard.MaxZ, yardOther.MaxZ);
                        }
                    }

                    yardBuild = truth;
                }

                // ---- the cuts of the record: the NPC nearest the recorded place is retired ----
                while (eventAt < session.Events.Count && session.Events[eventAt].Time <= t)
                {
                    var e = session.Events[eventAt++];
                    if (e.Kind != PropCapture.RetiredRecord) continue;
                    int nearest = -1; float best = 2f * 2f;
                    foreach (var pair in actors)
                    {
                        if (!pair.Value.TryResolve(t, dataset, out _, out _, out var at)) continue;
                        float dx = at.X - e.X, dz = at.Z - e.Z;
                        if (dx * dx + dz * dz < best) { best = dx * dx + dz * dz; nearest = pair.Key; }
                    }

                    if (nearest < 0) { result.RetirementsNotMatched++; continue; }
                    actors.Remove(nearest); inside.Remove(nearest); previous.Remove(nearest);
                    dwell.Retire(nearest);
                    CloseCrossing(nearest, t, float.NaN, float.NaN, "retired");
                    result.Retired++;
                    generation++;
                    nextRequest = 0;
                }

                // ---- a cycle's collection (MobPlanCrowd.CollectCycle) ----
                if (pending != null && t >= pending.CollectAt)
                {
                    var p = pending; pending = null; busy = false;
                    if (generation != p.Generation || (p.Cycle.Published && p.Cycle.MinCommitUntil <= t)) { result.CyclesStale++; nextRequest = t; }
                    else if (!p.Cycle.Published) result.CyclesUnpublished++;
                    else
                    {
                        bool visible = false;
                        foreach (int id in p.Relocated)
                        {
                            if (!p.Plans[id].TryResolve(t, dataset, out _, out _, out var proposed) || Visible(player, proposed.X, proposed.Z)
                                || (actors.TryGetValue(id, out var old) && old.TryResolve(t, dataset, out _, out _, out var live) && Visible(player, live.X, live.Z))) { visible = true; break; }
                        }

                        if (visible) { result.CyclesStaleVisible++; nextRequest = t; }
                        else
                        {
                            gone.Clear();
                            foreach (var pair in waiting) if (!p.Plans.ContainsKey(pair.Key)) gone.Add(pair.Key);
                            foreach (int id in gone) waiting.Remove(id);
                            foreach (var pair in p.Plans)
                            {
                                if (actors.TryGetValue(pair.Key, out var replaced))
                                {
                                    // for the stay's record: whether what the NPC plays now, or 1.5 s on, is another clip than before
                                    bool changed = replaced.TryResolve(t, dataset, out var oldNow, out _, out _) != pair.Value.TryResolve(t, dataset, out var newNow, out _, out _) || oldNow.Clip != newNow.Clip
                                        || replaced.TryResolve(t + 1.5, dataset, out var oldNext, out _, out _) != pair.Value.TryResolve(t + 1.5, dataset, out var newNext, out _, out _) || oldNext.Clip != newNext.Clip;
                                    dwell.PlanUpdated(pair.Key, changed);
                                }

                                if (actors.ContainsKey(pair.Key) && p.Notes != null && p.Notes.TryGetValue(pair.Key, out var planNote))
                                {
                                    planNotes[pair.Key] = planNote;
                                    if (!noteHistory.TryGetValue(pair.Key, out var history)) noteHistory[pair.Key] = history = new List<PlanNote>();
                                    history.Add(planNote);
                                    if (history.Count > 16) history.RemoveAt(0);
                                }
                                if (actors.ContainsKey(pair.Key)) actors[pair.Key] = pair.Value;
                                else if (waiting.ContainsKey(pair.Key) || !p.Baseline.Contains(pair.Key)) waiting[pair.Key] = pair.Value;
                            }

                            lastAccepted = p.Cycle;
                            result.CyclesPublished++;
                            result.PropPredictedHits += p.Cycle.Metrics.propPredictedHits;
                            result.PropPredictedSeconds += p.Cycle.Metrics.propPredictedSeconds;
                        }
                    }
                }

                // ---- the waiting starts (MobPlanCrowd.StartWaiting, without slots) ----
                if (waiting.Count > 0)
                {
                    gone.Clear();
                    foreach (var pair in waiting)
                    {
                        byte outcome;
                        float x = 0f, z = 0f;
                        if (!pair.Value.TryResolve(t, dataset, out _, out _, out var root)) outcome = PropCapture.StartUnplaced;
                        else
                        {
                            x = root.X; z = root.Z;
                            var current = options.Props ? pool.Current : null;
                            if (Visible(player, x, z)) outcome = PropCapture.StartInView;
                            else if (current != null && current.Overlaps(x, z, radius + simConfig.propSpawnKeepMeters)) outcome = PropCapture.StartInProps;
                            else outcome = PropCapture.StartStarted;
                        }

                        bool inProps = outcome != PropCapture.StartUnplaced && yardBuild >= 0 && yard.OccupiedAt(x, z);
                        if (outcome == PropCapture.StartStarted)
                        {
                            actors[pair.Key] = pair.Value;
                            result.Starts++;
                            if (inProps) result.StartsInsideProps++;
                            if (yardBuild >= 0 && yard.Overlaps(x, z, radius)) result.StartsOverlappingProps++;
                        }
                        else if (outcome == PropCapture.StartInView) result.StartsInView++;
                        else if (outcome == PropCapture.StartInProps) result.StartsRefusedInProps++;
                        else result.StartsUnplaced++;
                        if (options.KeepFrames) result.StartMarks.Add(new PropReplayResult.StartMark { Time = t, Id = pair.Key, X = x, Z = z, Result = outcome, Inside = inProps });
                        gone.Add(pair.Key);
                    }

                    foreach (int id in gone) waiting.Remove(id);
                }

                // ---- a cycle's queueing (MobPlanCrowd.QueueCycle), run at once; collected after the latency ----
                if (!busy && t >= nextRequest)
                {
                    var baseline = new Dictionary<int, PublishedMobPlan>(actors);
                    foreach (var pair in waiting) baseline[pair.Key] = pair.Value;
                    var baselineIds = new HashSet<int>(baseline.Keys);
                    PropObstacleMap taken = options.Props ? pool.RetainCurrent() : null;
                    if (options.Props)
                    {
                        if (taken == null) result.CyclesWithoutMap++;
                        else { result.MapsUsedByCycles++; double age = t - taken.InputTime; ageSum += age; if (age > ageMost) ageMost = age; }
                    }

                    clock.Restart();
                    if (lastAccepted != null) sim.AcknowledgeExternal(lastAccepted);
                    lastAccepted = null;
                    var snapshot = sim.PrepareExternal((float)t, baseline, player.X, player.Z, player.VelocityX, player.VelocityZ, player.Yaw, taken);
                    prepareMs += clock.Elapsed.TotalMilliseconds;
                    // the goals as the simulation has them now: an event when one was reached, changed or blocked since the last cycle
                    dwellClock.Start();
                    foreach (var agent in sim.Agents)
                    {
                        string what = null;
                        if (goalSeen.TryGetValue(agent.Id, out var seen))
                        {
                            if (agent.GoalsReached != seen.reached) what = "reached";
                            else if (agent.GoalRevision != seen.revision) what = "changed";
                            else if (agent.GoalBlocked != seen.blocked) what = agent.GoalBlocked ? "blocked" : "unblocked";
                        }

                        if (what == "reached")
                        {
                            long was = ((long)agent.Id << 32) | (uint)seen.revision;
                            if (noPointGoals.Contains(was) && !ledGoals.Contains(was) && !standoffGoals.Contains(was)) result.ReachedNoPointGoals++;
                        }

                        {
                            long now = ((long)agent.Id << 32) | (uint)agent.GoalRevision;
                            if (agent.ReachValid && !agent.GoalBlocked && agent.ReachStandoff) standoffGoals.Add(now);
                            else if (agent.ReachValid && !agent.GoalBlocked) { ledGoals.Add(now); (agent.ReachVerified ? ledVerified : ledUnverified).Add(now); }
                            if (agent.ReachNone) noPointGoals.Add(now);
                        }

                        goalSeen[agent.Id] = (agent.GoalRevision, agent.GoalsReached, agent.GoalBlocked);
                        result.NpcRows.Add(FormattableString.Invariant($"{t:F2},{agent.Id},{what ?? ""},{agent.Pose.X:F2},{agent.Pose.Z:F2},{agent.GoalX:F2},{agent.GoalZ:F2},{agent.PlanGoalX:F2},{agent.PlanGoalZ:F2},{agent.Pose.DistanceTo(agent.GoalX, agent.GoalZ):F2},{agent.GoalsReached},{agent.GoalRevision},{(agent.GoalBlocked ? 1 : 0)},{agent.GoalsAbandoned},{agent.FlowPhase},{agent.Pose.YawDeg:F0},{(agent.ReachValid ? 1 : 0)},{(sincePath.TryGetValue(agent.Id, out float pathSince) ? pathSince : 0f):F2},{(sinceYaw.TryGetValue(agent.Id, out float yawSince) ? yawSince : 0f):F0},{(agent.ReachNone ? 1 : 0)},{(agent.ReachValid && agent.ReachStandoff ? 1 : 0)},{agent.GoalStage},{agent.NoPointSeconds((float)t):F1}"));
                        sincePath[agent.Id] = 0f; sinceYaw[agent.Id] = 0f;
                        if (agent.ReachValid && !agent.GoalBlocked) result.ReachAgentCycles++;
                        if (abandonSeen.TryGetValue(agent.Id, out int abandonedBefore) && agent.GoalsAbandoned != abandonedBefore) lastAbandon[agent.Id] = t;
                        abandonSeen[agent.Id] = agent.GoalsAbandoned;
                        dwell.Goal(t, agent.Id, agent.PlanGoalX, agent.PlanGoalZ, what);
                    }

                    dwellClock.Stop();
                    var cycleConfig = sim.Config.Clone();
                    if (options.CycleBudgetMilliseconds > 0) cycleConfig.cycleBudgetMilliseconds = options.CycleBudgetMilliseconds;
                    clock.Restart();
                    var cycle = LocomotionPlanner.RunCycle(dataset, map, cycleConfig, snapshot);
                    double ms = clock.Elapsed.TotalMilliseconds;
                    cycleMs += ms; if (ms > cycleMsMost) cycleMsMost = ms;
                    if (options.TraceAgents != null)
                    {
                        float allowed = 2f * simConfig.npcRadiusMeters + simConfig.agentMarginMeters;
                        foreach (var info in cycle.Agents)
                        {
                            if (!options.TraceAgents.Contains(info.AgentId)) continue;
                            for (int c = 0; c < info.Candidates.Count; c++)
                            {
                                var candidate = info.Candidates[c];
                                var blockers = new List<string>();
                                if (candidate.Trajectory != null)
                                {
                                    foreach (var other in cycle.Agents)
                                    {
                                        if (other == info || other.Chosen < 0 || other.Chosen >= other.Candidates.Count) continue;
                                        var theirs = other.Candidates[other.Chosen];
                                        if (theirs.Trajectory == null || !candidate.Trajectory.BoundsOverlap(theirs.Trajectory, allowed)) continue;
                                        float separation = CircleSweep.MinSeparation(candidate.Trajectory, theirs.Trajectory, allowed, out float at);
                                        if (separation < allowed) blockers.Add(FormattableString.Invariant($"{other.AgentId}:{separation:F2}@{at:F2}:{theirs.Origin}"));
                                    }
                                }

                                int firstOfIt = candidate.Suffix.Count > 0 ? candidate.Suffix[0].Clip : -1;
                                string firstName = firstOfIt >= 0 ? (dataset.Clips[firstOfIt].Category + "/" + dataset.Clips[firstOfIt].DisplayName).Replace(',', ';').Replace(' ', '_') : string.Empty;
                                result.SelectionRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},{(info.Status ?? string.Empty).Replace(',', ';')},{c},{candidate.Origin},{(c == info.Chosen ? 1 : 0)},{candidate.TotalCost:F3},{candidate.Cost.Dynamic:F3},{candidate.Cost.PropOverlapSeconds:F2},{candidate.Cost.DynamicOverlapSeconds:F2},{firstName},{blockers.Count},") + string.Join(" ", blockers));
                            }
                        }
                    }

                    cycleNotes = new Dictionary<int, PlanNote>();
                    if (taken != null)
                    {
                        float windowSize = taken.Cells * taken.Cell;
                        double off = Math.Max(Math.Abs(player.X - (taken.OriginX + windowSize * 0.5f)), Math.Abs(player.Z - (taken.OriginZ + windowSize * 0.5f)));
                        result.WindowCycles++; result.WindowOffCentreSum += off; if (off > result.WindowOffCentreMost) result.WindowOffCentreMost = off;
                        foreach (var info in cycle.Agents)
                        {
                            if (info.Chosen < 0 || info.Chosen >= info.Candidates.Count || info.Candidates[info.Chosen].Full.Count == 0) continue;
                            var planned = info.Candidates[info.Chosen].Full;
                            var here = AgentPlan.PoseAt(dataset, planned, (float)t);
                            float startProp = taken.DistanceAt(info.StartPose.X, info.StartPose.Z);
                            if (info.PrefixShortened || startProp < simConfig.npcRadiusMeters + simConfig.propKeepMeters)
                            {
                                var took = info.Candidates[info.Chosen]; var oldPlan = info.Candidates[0];
                                int cleanCount = 0; float bestClean = float.NaN;
                                foreach (var candidate in info.Candidates)
                                {
                                    if (candidate.Cost.PropOverlapSeconds > 0f) continue;
                                    cleanCount++;
                                    if (float.IsNaN(bestClean) || candidate.Cost.Total < bestClean) bestClean = candidate.Cost.Total;
                                }

                                float least = float.PositiveInfinity;
                                for (double at = info.Boundary; at <= info.Boundary + 4.0; at += 0.1)
                                {
                                    var where = AgentPlan.PoseAt(dataset, planned, (float)at);
                                    float pd = taken.DistanceAt(where.X, where.Z);
                                    if (pd < least) least = pd;
                                }

                                var after = AgentPlan.PoseAt(dataset, planned, info.Boundary + 4f);
                                int firstClip = took.Suffix.Count > 0 ? took.Suffix[0].Clip : -1;
                                string clipName = firstClip >= 0 ? (dataset.Clips[firstClip].Category + "/" + dataset.Clips[firstClip].DisplayName).Replace(',', ';').Replace(' ', '_') : string.Empty;
                                result.PrefixRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},{(info.PrefixShortened ? 1 : 0)},{(info.LodSkipped ? 1 : 0)},{(info.Status ?? string.Empty).Replace(',', ';')},{info.Boundary:F2},{here.X:F2},{here.Z:F2},{taken.DistanceAt(here.X, here.Z):F2},{info.StartPose.X:F2},{info.StartPose.Z:F2},{startProp:F2},")
                                    + FormattableString.Invariant($"{info.Candidates.Count},{cleanCount},{info.Chosen},{took.Origin},{clipName},{took.Cost.PropOverlapSeconds:F2},{took.Cost.Dynamic:F3},{took.Cost.Total:F3},{bestClean:F3},{oldPlan.Cost.PropOverlapSeconds:F2},{oldPlan.Cost.Total:F3},{after.DistanceTo(info.StartPose.X, info.StartPose.Z):F2},{least:F2},{taken.DistanceAt(after.X, after.Z):F2},{info.Stats.Expansions}"));
                            }

                            double px = here.X - player.X, pz = here.Z - player.Z;
                            if (px * px + pz * pz > 25.0 * 25.0) continue;
                            result.NpcCyclesNearPlayer++;
                            if (!taken.Covers(here.X, here.Z)) result.NpcCyclesNearOutsideWindow++;
                            for (double at = t; at <= t + 6.0; at += 0.2)
                            {
                                var where = AgentPlan.PoseAt(dataset, planned, (float)at);
                                if (taken.Covers(where.X, where.Z)) continue;
                                result.NpcCyclesNearPlanOutsideWindow++;
                                break;
                            }
                        }
                    }

                    foreach (var info in cycle.Agents)
                    {
                        if (info.Chosen < 0 || info.Chosen >= info.Candidates.Count) continue;
                        var picked = info.Candidates[info.Chosen];
                        var note = new PlanNote { Has = true, CycleTime = t, MapVersion = taken?.Version ?? 0, MapInputTime = taken?.InputTime ?? double.NaN, OverlapSeconds = picked.Cost.PropOverlapSeconds, Dynamic = picked.Cost.Dynamic, Total = picked.Cost.Total,
                            Origin = picked.Origin, Changed = info.Changed, Candidates = info.Candidates.Count, LeastDistance = float.PositiveInfinity, LeastAt = double.NaN, BestCleanTotal = float.NaN,
                            Boundary = info.Boundary, LeastAfterBoundary = float.PositiveInfinity, LeastAtAfterBoundary = double.NaN };
                        foreach (var candidate in info.Candidates)
                        {
                            if (candidate.Cost.PropOverlapSeconds > 0f) continue;
                            note.CleanCandidates++;
                            if (float.IsNaN(note.BestCleanTotal) || candidate.Cost.Total < note.BestCleanTotal) note.BestCleanTotal = candidate.Cost.Total;
                        }

                        if (taken != null && picked.Full.Count > 0)
                        {
                            var track = new List<float>(); var trackX = new List<float>(); var trackZ = new List<float>();
                            note.MapOriginX = taken.OriginX; note.MapOriginZ = taken.OriginZ; note.MapSize = taken.Cells * taken.Cell; note.MapOccupied = taken.OccupiedCells;
                            note.MapMinX = taken.MinX; note.MapMaxX = taken.MaxX; note.MapMinZ = taken.MinZ; note.MapMaxZ = taken.MaxZ;
                            // the plan's own track against the map its cycle had: the least distance to the prop cells and when
                            for (double at = t; at <= t + simConfig.planningHorizonSeconds; at += 0.1)
                            {
                                var where = AgentPlan.PoseAt(dataset, picked.Full, (float)at);
                                float d = taken.DistanceAt(where.X, where.Z);
                                track.Add(d); trackX.Add(where.X); trackZ.Add(where.Z);
                                if (d < note.LeastDistance) { note.LeastDistance = d; note.LeastAt = at; }
                                if (at >= info.Boundary && d < note.LeastAfterBoundary) { note.LeastAfterBoundary = d; note.LeastAtAfterBoundary = at; }   // the part this cycle could still change
                            }

                            note.Track = track.ToArray(); note.TrackX = trackX.ToArray(); note.TrackZ = trackZ.ToArray();
                        }

                        cycleNotes[info.AgentId] = note;
                    }

                    if (simConfig.propGoalReach)
                    {
                        foreach (var agent in sim.Agents)
                        {
                            if (!agent.ReachValid || agent.GoalBlocked) continue;
                            var info = cycle.Agents.Find(i => i.AgentId == agent.Id);
                            bool searched = info != null && !info.LodSkipped && info.Stats.Expansions > 0;
                            var s = info?.Stats ?? new SearchStats();
                            float walkProp = agent.Radius + simConfig.propKeepMeters;
                            bool has = info != null && info.Chosen >= 0 && info.Chosen < info.Candidates.Count;
                            var firstSegments = has ? info.Candidates[info.Chosen].Suffix : null;
                            var firstClip = firstSegments != null && firstSegments.Count > 0 ? dataset.Clips[firstSegments[0].Clip] : null;
                            result.ReachRows.Add(FormattableString.Invariant($"{t:F2},{agent.Id},{agent.FlowPhase},{(agent.ReachStandoff ? 1 : 0)},{(agent.ReachVerified ? 1 : 0)},{agent.GoalX:F2},{agent.GoalZ:F2},{agent.ReachX:F2},{agent.ReachZ:F2},{Math.Sqrt((agent.ReachX - agent.GoalX) * (agent.ReachX - agent.GoalX) + (agent.ReachZ - agent.GoalZ) * (agent.ReachZ - agent.GoalZ)):F2},{(taken != null ? taken.DistanceAt(agent.ReachX, agent.ReachZ) : -1f):F2},{map.ClearanceAt(agent.ReachX, agent.ReachZ):F2},{(taken != null && taken.Overlaps(agent.ReachX, agent.ReachZ, walkProp + 0.5f) ? 1 : 0)},{agent.Pose.DistanceTo(agent.GoalX, agent.GoalZ):F2},{agent.Pose.DistanceTo(agent.ReachX, agent.ReachZ):F2},{(searched ? 1 : 0)},{s.InReachExpansions},{s.InReachThreatened},{s.InReachWaits},{s.InReachWaitsThreatened},{s.GoalFinals},{(info != null ? info.Candidates.Count : 0)},{(has ? info.Candidates[info.Chosen].Origin : "")},{(firstClip != null ? (firstClip.Category + "/" + firstClip.DisplayName).Replace(',', ';').Replace(' ', '_') : "")}"));
                        }
                    }

                    if (options.Rescore && options.TraceAgents != null && simConfig.goalFieldRecovery)
                    {
                        // evaluation: the same snapshot planned with the estimate switched the other way; then both chosen plans of
                        // each traced NPC costed by both cost functions (two more cycles whose results are only read for the costs)
                        bool mineIsEstimate = cycleConfig.goalFieldRecoveryExtrapolate;
                        var otherConfig = cycleConfig.Clone(); otherConfig.goalFieldRecoveryExtrapolate = !mineIsEstimate; otherConfig.traceAgents = null;
                        var otherCycle = LocomotionPlanner.RunCycle(dataset, map, otherConfig, snapshot);
                        var plans = new Dictionary<int, List<List<PlanSegment>>>();
                        var own = new Dictionary<int, (float mine, float other)>();
                        foreach (int id in options.TraceAgents)
                        {
                            var mine = cycle.Agents.Find(i => i.AgentId == id); var theirs = otherCycle.Agents.Find(i => i.AgentId == id);
                            if (mine == null || theirs == null || mine.LodSkipped || theirs.LodSkipped || mine.Candidates.Count == 0 || theirs.Candidates.Count == 0) continue;
                            if (mine.Stats.Expansions == 0 || theirs.Stats.Expansions == 0) continue;   // not searched this cycle
                            plans[id] = new List<List<PlanSegment>> { mine.Candidates[mine.Chosen].Suffix, theirs.Candidates[theirs.Chosen].Suffix };
                            own[id] = (mine.Candidates[mine.Chosen].Cost.Total, theirs.Candidates[theirs.Chosen].Cost.Total);
                        }

                        foreach (var pair in plans)
                        {
                            var planA = pair.Value[0]; var planB = pair.Value[1];
                            bool same = planA.Count == planB.Count;
                            for (int i = 0; same && i < planA.Count; i++) same = planA[i].Clip == planB[i].Clip;
                            if (same) continue;
                            for (int which = 0; which < 2; which++)
                            {
                                // this variant's search asked about the other's plan, and the other's search about its own plan (the comparison)
                                var followConfig = (which == 0 ? cycleConfig : otherConfig).Clone(); followConfig.traceAgents = null;
                                followConfig.followSuffixes = new Dictionary<int, List<PlanSegment>> { { pair.Key, planB } };
                                var followed = LocomotionPlanner.RunCycle(dataset, map, followConfig, snapshot);
                                var followedInfo = followed.Agents.Find(i => i.AgentId == pair.Key);
                                string by = (which == 0) == mineIsEstimate ? "estimate" : "base";
                                if (followedInfo?.Stats.FollowTrace == null) { result.FollowRows.Add(FormattableString.Invariant($"{t:F2},{pair.Key},{by},none,0,,0,")); continue; }
                                foreach (string line in followedInfo.Stats.FollowTrace) result.FollowRows.Add(FormattableString.Invariant($"{t:F2},{pair.Key},{by},") + line);
                            }
                        }

                        if (plans.Count > 0)
                        {
                            for (int pass = 0; pass < 2; pass++)
                            {
                                var scoreConfig = (pass == 0 ? cycleConfig : otherConfig).Clone(); scoreConfig.traceAgents = null; scoreConfig.rescoreSuffixes = plans;
                                bool scoreIsEstimate = pass == 0 ? mineIsEstimate : !mineIsEstimate;
                                var scored = LocomotionPlanner.RunCycle(dataset, map, scoreConfig, snapshot);
                                foreach (var info in scored.Agents)
                                {
                                    if (info.Rescored == null) continue;
                                    for (int p = 0; p < info.Rescored.Count; p++)
                                    {
                                        var again = info.Rescored[p]; var suffix = plans[info.AgentId][p];
                                        bool planIsEstimate = p == 0 ? mineIsEstimate : !mineIsEstimate;
                                        var end = suffix.Count > 0 ? AgentPlan.PoseAt(dataset, suffix, suffix[suffix.Count - 1].EndTime) : info.StartPose;
                                        var firstClip = suffix.Count > 0 ? dataset.Clips[suffix[0].Clip] : null;
                                        result.RescoreRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},{(planIsEstimate ? "estimate" : "base")},{(scoreIsEstimate ? "estimate" : "base")},{(info.RescoredStaticOk[p] ? 1 : 0)},{again.Total:F3},{again.Progress:F3},{again.Heading:F3},{again.Speed:F3},{again.Idle:F3},{again.Turn:F3},{again.Facing:F3},{again.Churn:F3},{again.OtherAgent:F3},{again.Penalty:F1},{again.Dynamic:F3},{again.DynamicOverlapSeconds:F2},{again.PropOverlapSeconds:F2},{again.LocalStay:F3},{again.EstimateSeconds:F2},{again.EstimateGapSeconds:F2},{suffix.Count},{(firstClip != null ? (firstClip.Category + "/" + firstClip.DisplayName).Replace(',', ';').Replace(' ', '_') : "")},{end.X:F2},{end.Z:F2},{(p == 0 ? own[info.AgentId].mine : own[info.AgentId].other):F3}"));
                                    }
                                }
                            }
                        }
                    }

                    taken?.Release();
                    result.Cycles++;
                    {
                        // the cycle's work and the plans it published, whatever the diagnosis below keeps
                        ulong h = 1469598103934665603UL;
                        h = Mix(h, (uint)cycle.Revision); h = Mix(h, cycle.Published ? 1u : 0u);
                        var ids = new List<int>(cycle.NewPlans.Keys);
                        ids.Sort();
                        foreach (int id in ids)
                        {
                            var segments = cycle.NewPlans[id].Segments;
                            h = Mix(h, (uint)id); h = Mix(h, (uint)segments.Count);
                            foreach (var segment in segments)
                            {
                                h = Mix(h, (uint)segment.Clip); h = Mix(h, Bits(segment.StartTime)); h = Mix(h, Bits(segment.EndTime));
                                h = Mix(h, Bits(segment.WorldStart.X)); h = Mix(h, Bits(segment.WorldStart.Z)); h = Mix(h, Bits(segment.WorldStart.YawDeg));
                            }
                        }

                        var m = cycle.Metrics;
                        result.Expansions += m.expansions; result.RegenerationRounds += m.regenerationRounds; result.CandidatesTotal += m.candidatesTotal;
                        if (m.timeout) result.CycleTimeouts++;
                        result.PrefixShortened += m.propPrefixShortened; result.PrefixCutSeconds += m.propPrefixCutSeconds; result.PrefixOnlyEvent += m.propPrefixOnlyEvent; result.PrefixStartInside += m.propPrefixStartInside;
                        result.PrefixRecheckSamples += m.propPrefixRecheckSamples; result.PrefixRecheckMilliseconds += m.propPrefixRecheckMilliseconds; if (m.propPrefixRecheckMilliseconds > result.PrefixRecheckMostMilliseconds) result.PrefixRecheckMostMilliseconds = m.propPrefixRecheckMilliseconds;
                        result.PlanHash = Mix(Mix(result.PlanHash, (uint)(h >> 32)), (uint)h);
                        result.CycleHashRows.Add(FormattableString.Invariant($"{t:F3},{cycle.Revision},{(cycle.Published ? 1 : 0)},{snapshot.Agents.Count},{ids.Count},{m.expansions},{m.regenerationRounds},{m.candidatesTotal},{m.searchTimeouts},{(m.timeout ? 1 : 0)},{h:X16}"));
                        foreach (var info in cycle.Agents)
                        {
                            if (info.Status != null && info.Status.Contains("skipped(budget)")) result.SkippedForBudget++;
                            var patch = info.Recovery?.Patch;
                            if (patch == null || !patch.EstimateMade) continue;
                            result.EstimatePatches++;
                            {
                                var s = info.Stats;
                                int withGap = 0; float bestOther = float.NaN, bestOtherGap = 0f, bestClean = float.NaN;
                                for (int c = 0; c < info.Candidates.Count; c++)
                                {
                                    var candidateCost = info.Candidates[c].Cost;
                                    if (candidateCost.EstimateGapSeconds > 0f) withGap++;
                                    else if (float.IsNaN(bestClean) || candidateCost.Total < bestClean) bestClean = candidateCost.Total;
                                    if (c != info.Chosen && (float.IsNaN(bestOther) || candidateCost.Total < bestOther)) { bestOther = candidateCost.Total; bestOtherGap = candidateCost.EstimateGapCost; }
                                }

                                bool has = info.Chosen >= 0 && info.Chosen < info.Candidates.Count;
                                var chosenCost = has ? info.Candidates[info.Chosen].Cost : new CostBreakdown();
                                result.PatchRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},{patch.CentreX:F2},{patch.CentreZ:F2},{(patch.Found ? 1 : 0)},{patch.Sources},{patch.Reached},{patch.EstimateReads},{patch.EstimateBoundaryStepMost:F2},{patch.EstimateBoundarySteps},{patch.StepCellX:F2},{patch.StepCellZ:F2},{patch.StepEstimate:F2},{patch.StepNeighbourX:F2},{patch.StepNeighbourZ:F2},{patch.StepNeighbourValue:F2},{patch.EstimateGapMost:F2},{patch.EstimateGapCells},")
                                    + FormattableString.Invariant($"{s.EstimateSeconds:F1},{s.GapKept},{s.GapKeptAdv},{s.GapCut},{s.GapCutAdv},{s.CleanCutWhileGapKept},{s.CleanCutAdvWhileGapKept},{s.GapKeptOnlyByGap},{s.GapKeptCost:F3},{s.GapKeptCostMost:F3},{s.GapCutCost:F3},")
                                    + FormattableString.Invariant($"{s.KeyGapKept},{s.KeyGapCut},{s.RankGapReads},{s.RankGapChosen},{info.Candidates.Count},{withGap},{info.Chosen},{(has ? info.Candidates[info.Chosen].Origin : "")},{chosenCost.Total:F3},{chosenCost.Progress:F3},{chosenCost.EstimateSeconds:F2},{chosenCost.EstimateGapSeconds:F2},{chosenCost.EstimateGapCost:F3},{bestOther:F3},{bestOtherGap:F3},{bestClean:F3}"));
                            }
                            if (patch.EstimateBoundarySteps > 0) result.EstimateBoundaryPatches++;
                            if (patch.EstimateBoundaryStepMost > result.EstimateBoundaryStepMost) result.EstimateBoundaryStepMost = patch.EstimateBoundaryStepMost;
                        }
                    }

                    if (options.CycleRows)
                    {
                        dwellClock.Start();
                        foreach (var info in cycle.Agents)
                        {
                            Diagnose(t, info, simConfig, dwell, plannedWalk, result); result.LocalStayUnjudgedClips += info.Stats.LocalStayUnjudgedClips;
                            result.StraightFallbackSeconds += info.Stats.StraightFallbackSeconds; result.PatchSeconds += info.Stats.PatchSeconds; result.EstimateSeconds += info.Stats.EstimateSeconds;
                            if (!info.LodSkipped) result.Searches++;
                            if (info.Stats.Timeout) result.SearchTimeouts++;
                            if (info.Stats.ExpansionCap) result.SearchCaps++;
                        }
                        foreach (var info in cycle.Agents)
                        {
                            if (info.Recovery != null && info.Recovery.Patch != null)
                            {
                                result.RecoveryPatches++; result.RecoveryMilliseconds += info.Recovery.Patch.BuildMilliseconds; result.RecoveryReads += info.Recovery.Patch.Reads;
                                if (info.Recovery.Patch.Found) result.RecoveryFound++;
                                if (info.Recovery.Overlapping) { result.RecoveryOverlapping++; if (info.Recovery.Patch.Found) result.RecoveryOverlappingFound++; }
                            }
                        }

                        foreach (var agent in snapshot.Agents)
                        {
                            var info = cycle.Agents.Find(i => i.AgentId == agent.Id);
                            if (info == null || info.LodSkipped || agent.Field == null) continue;
                            result.SearchedAgentCycles++;
                            if (float.IsPositiveInfinity(agent.Field.PathDistanceAt(info.StartPose.X, info.StartPose.Z))) result.StartsWithoutFieldPath++;
                            agent.Field.NearestPathMeters(agent.GoalX, agent.GoalZ, 0f, out _, out bool goalOpen);
                            int level = 0;
                            if (!goalOpen)
                            {
                                result.GoalsInClosedCells++;
                                // why the goal's cell is closed: the goal point against the static map and the prop cells of this cycle's version
                                float gx = agent.GoalX, gz = agent.GoalZ;
                                var cycleProps = simConfig.propObstacles && simConfig.propGoalField ? snapshot.Props : null;
                                float staticAt = map.ClearanceAt(gx, gz), propAt = cycleProps != null ? cycleProps.DistanceAt(gx, gz) : float.PositiveInfinity;
                                float walkStatic = agent.Radius + simConfig.staticMarginMeters, walkProp = agent.Radius + simConfig.propKeepMeters;
                                float guideStatic = agent.Field.RequiredClearance, guideProp = simConfig.propGoalFieldClosureMeters > 0f ? simConfig.propGoalFieldClosureMeters : agent.Field.RequiredClearance;
                                bool staticIn = !map.IsWalkableAt(gx, gz), propIn = cycleProps != null && cycleProps.OccupiedAt(gx, gz);
                                bool s, p;
                                if (staticIn || propIn) { level = 1; s = staticIn; p = propIn; }
                                else if (staticAt < walkStatic || propAt < walkProp) { level = 2; s = staticAt < walkStatic; p = propAt < walkProp; }
                                else if (staticAt < guideStatic || propAt < guideProp) { level = 3; s = staticAt < guideStatic; p = propAt < guideProp; }
                                else
                                {
                                    level = 4;
                                    agent.Field.CellCentre(gx, gz, out float ccx, out float ccz);
                                    s = map.ClearanceAt(ccx, ccz) < guideStatic; p = cycleProps != null && cycleProps.DistanceAt(ccx, ccz) < guideProp;
                                }

                                int source = s && p ? 2 : p ? 1 : 0;
                                result.GoalClosed[level * 3 + source]++;
                                double inStay = dwell.OpenSeconds(agent.Id);
                                if (inStay >= 3.0) result.GoalClosedInStay[level]++;
                                agent.Field.CellCentre(gx, gz, out float cx0, out float cz0);
                                var simAgent = sim.Agents.Find(a => a.Id == agent.Id);
                                result.GoalRows.Add(FormattableString.Invariant($"{t:F2},{agent.Id},{gx:F2},{gz:F2},{level},{(source == 2 ? "both" : source == 1 ? "prop" : "static")},{staticAt:F2},{(float.IsInfinity(propAt) ? -1f : propAt):F2},{map.ClearanceAt(cx0, cz0):F2},{(cycleProps != null ? cycleProps.DistanceAt(cx0, cz0) : -1f):F2},{walkStatic:F2},{walkProp:F2},{guideStatic:F2},{guideProp:F2},{inStay:F1},{(simAgent != null ? simAgent.FlowPhase : -1)},{(simAgent != null && simAgent.GoalBlocked ? 1 : 0)},{(float.IsPositiveInfinity(agent.Field.PathDistanceAt(info.StartPose.X, info.StartPose.Z)) ? 0 : 1)}"));
                            }

                            dwell.Cycle(agent.Id, level);
                        }
                        if (options.TraceAgents != null)
                        {
                            guideProps = snapshot.Props;
                            foreach (var agent in snapshot.Agents)
                            {
                                if (!options.TraceAgents.Contains(agent.Id)) continue;
                                var info = cycle.Agents.Find(i => i.AgentId == agent.Id);
                                if (info != null) Guide(t, agent, info, simConfig, result);
                            }
                        }
                        dwellClock.Stop();
                    }

                    var made = new Pending { Cycle = cycle, Generation = generation, Baseline = baselineIds, CollectAt = t + options.CycleLatency, Notes = cycleNotes };
                    if (cycle.Published)
                    {
                        made.Relocated = new List<int>();
                        foreach (var agent in snapshot.Agents)
                        {
                            if (!baseline.TryGetValue(agent.Id, out var old) ||
                                (old.TryResolve(t, dataset, out _, out _, out var at) && Pose2.Distance(at, AgentPlan.PoseAt(dataset, agent.Plan.Segments, (float)t)) > .1f)) made.Relocated.Add(agent.Id);
                        }

                        made.Plans = new Dictionary<int, PublishedMobPlan>();
                        foreach (var pair in cycle.NewPlans) made.Plans[pair.Key] = new PublishedMobPlan(pair.Value, generation);
                    }

                    pending = made; busy = true; nextRequest = t + 1.0;
                }

                first = false;

                // ---- what the NPCs do this frame, against the yardstick ----
                PropReplayResult.Agent[] kept = options.KeepFrames ? new PropReplayResult.Agent[actors.Count] : null;
                int k = 0;
                var plannerMap = options.Props ? pool.Current : null;
                foreach (var pair in actors)
                {
                    if (!pair.Value.TryResolve(t, dataset, out var playing, out _, out var pose)) { if (kept != null) kept[k++] = new PropReplayResult.Agent { Id = pair.Key, X = float.NaN, Z = float.NaN }; continue; }
                    result.AgentSeconds += dt;
                    bool inScope = InScope(pose.X, pose.Z, player.X, player.Z, scopeMeters);
                    if (inScope) result.AgentSecondsInScope += dt;
                    bool isInside = false;
                    if (yardBuild >= 0 && yard.OccupiedCells > 0)
                    {
                        // outside what is counted an NPC is neither inside nor near: an entry in progress ends where it leaves the scope
                        float d = inScope ? yard.DistanceAt(pose.X, pose.Z) : float.PositiveInfinity;
                        isInside = inScope && yard.OccupiedAt(pose.X, pose.Z);
                        if (isInside) result.CrossingSeconds += dt;
                        if (d < radius) result.OverlapSeconds += dt;
                        else if (d < radius + 1f) result.NearSeconds += dt;
                        if (previous.TryGetValue(pair.Key, out var before))
                        {
                            float moved = (float)Math.Sqrt((pose.X - before.X) * (pose.X - before.X) + (pose.Z - before.Z) * (pose.Z - before.Z));
                            result.TravelMeters += moved;
                            if (moved / dt < 0.05f && d < radius + 1.5f) result.StandingNearSeconds += dt;
                        }

                        bool wasInside = inside.TryGetValue(pair.Key, out bool was) && was;
                        if (isInside && !wasInside)
                        {
                            result.Crossings++;
                            var entered = new Crossing { Enter = t, EnterX = pose.X, EnterZ = pose.Z, Deepest = d, LastAbandon = lastAbandon.TryGetValue(pair.Key, out double gaveUp) ? gaveUp : double.NaN,
                                PlannerVersion = plannerMap?.Version ?? 0, PlannerInputTime = plannerMap?.InputTime ?? double.NaN, PlannerAtEntry = plannerMap != null ? plannerMap.DistanceAt(pose.X, pose.Z) : float.NaN,
                                PlannerOccupied = plannerMap != null && plannerMap.DistanceAt(pose.X, pose.Z) <= 0f, PlannerLeast = plannerMap != null ? plannerMap.DistanceAt(pose.X, pose.Z) : float.NaN,
                                Clip = (dataset.Clips[playing.Clip].Category + "/" + dataset.Clips[playing.Clip].DisplayName).Replace(',', ';').Replace(' ', '_') };
                            planNotes.TryGetValue(pair.Key, out entered.Note);
                            entered.DecidingDistance = float.NaN;
                            if (noteHistory.TryGetValue(pair.Key, out var earlier))
                            {
                                for (int h = earlier.Count - 1; h >= 0; h--)
                                {
                                    if (earlier[h].Boundary > t) continue;
                                    entered.Deciding = earlier[h];
                                    int slot = (int)Math.Round((t - earlier[h].CycleTime) / 0.1);
                                    if (earlier[h].Track != null && slot >= 0 && slot < earlier[h].Track.Length) { entered.DecidingDistance = earlier[h].Track[slot]; entered.DecidingPlaceX = earlier[h].TrackX[slot]; entered.DecidingPlaceZ = earlier[h].TrackZ[slot]; }
                                    break;
                                }
                            }
                            var simAgent = sim.Agents.Find(a => a.Id == pair.Key);
                            if (simAgent != null)
                            {
                                entered.HasAgent = true; entered.GoalX = simAgent.GoalX; entered.GoalZ = simAgent.GoalZ; entered.PlanGoalX = simAgent.PlanGoalX; entered.PlanGoalZ = simAgent.PlanGoalZ;
                                entered.Led = simAgent.ReachValid && !simAgent.GoalBlocked; entered.Standoff = simAgent.ReachValid && simAgent.ReachStandoff; entered.FlowPhase = simAgent.FlowPhase; entered.GoalStage = simAgent.GoalStage;
                            }

                            crossings[pair.Key] = entered;
                        }
                        else if (isInside && crossings.TryGetValue(pair.Key, out var going))
                        {
                            if (d < going.Deepest) going.Deepest = d;
                            if (plannerMap != null) { float pd = plannerMap.DistanceAt(pose.X, pose.Z); if (pd < going.PlannerLeast) going.PlannerLeast = pd; }
                        }
                        else if (!isInside && wasInside) CloseCrossing(pair.Key, t, pose.X, pose.Z, "left");
                    }
                    else if (previous.TryGetValue(pair.Key, out var before))
                    {
                        result.TravelMeters += Math.Sqrt((pose.X - before.X) * (pose.X - before.X) + (pose.Z - before.Z) * (pose.Z - before.Z));
                    }

                    dwellClock.Start();
                    dwell.Frame(t, pair.Key, pose.X, pose.Z, pose.YawDeg, yardBuild >= 0 && yard.OccupiedCells > 0 && inScope ? yard.DistanceAt(pose.X, pose.Z) : float.PositiveInfinity,
                        plannerMap?.Version ?? 0, playing.Clip, dataset.Clips[playing.Clip].IsStationary, map.ClearanceAt(pose.X, pose.Z));
                    dwellClock.Stop();
                    if (plannerMap != null && !plannerMap.Covers(pose.X, pose.Z)) result.AgentStepsOutsideWindow++;
                    inside[pair.Key] = isInside;
                    if (previous.TryGetValue(pair.Key, out var last))
                    {
                        sincePath.TryGetValue(pair.Key, out float walked); sinceYaw.TryGetValue(pair.Key, out float turned);
                        float turn = pose.YawDeg - last.YawDeg; turn -= 360f * (float)Math.Round(turn / 360f);
                        sincePath[pair.Key] = walked + (float)Math.Sqrt((pose.X - last.X) * (pose.X - last.X) + (pose.Z - last.Z) * (pose.Z - last.Z));
                        sinceYaw[pair.Key] = turned + Math.Abs(turn);
                    }

                    previous[pair.Key] = pose;
                    if (kept != null) kept[k++] = new PropReplayResult.Agent { Id = pair.Key, X = pose.X, Z = pose.Z, Yaw = pose.YawDeg, Inside = isInside };
                }

                if (kept != null) result.Frames.Add(new PropReplayResult.Frame { Time = t, Agents = kept, MapVersion = plannerMap?.Version ?? 0, MapInputTime = plannerMap?.InputTime ?? double.NaN, TruthBuild = yardBuild });
            }

            dwellClock.Start();
            dwell.Finish();
            result.Dwells.AddRange(dwell.Stays);
            result.DwellAll = DwellTotals.Of(dwell.Stays, options.Dwell, false);
            result.DwellNear = DwellTotals.Of(dwell.Stays, options.Dwell, true);
            result.DwellNearStatic = DwellTotals.Of(dwell.Stays, options.Dwell, 2);
            result.DwellNearAny = DwellTotals.Of(dwell.Stays, options.Dwell, 3);
            result.DwellSettings = options.Dwell;
            result.DwellMeasureMilliseconds = dwellClock.Elapsed.TotalMilliseconds;
            result.FieldMillisecondsInAll = sim.GoalFieldMilliseconds - fieldMs0;
            result.FieldResets = sim.PropFieldResets - fieldResets0;
            result.FieldBuilds = sim.GoalFieldBuilds - fieldBuilds0; result.FieldCacheHits = sim.GoalFieldCacheHits - fieldHits0; result.FieldGridBuilds = sim.FieldGridBuilds - gridBuilds0;
            result.FieldCellsReached = sim.GoalFieldCellsReached - fieldCells0;
            result.FieldBuildMilliseconds = sim.GoalFieldBuildMilliseconds - fieldBuildMs0; result.FieldGridMilliseconds = sim.FieldGridMilliseconds - gridMs0;
            result.GoalsReached = sim.TotalGoalsReached - goals0;
            result.StuckGoalEvents = sim.StuckGoalEvents - stuck0;
            result.PropSpawnRejects = sim.PropSpawnRejects;
            foreach (int id in new List<int>(crossings.Keys)) CloseCrossing(id, to, float.NaN, float.NaN, "section-end");
            result.PropGoalRejects = sim.PropGoalRejects;
            result.LedGoals = ledGoals.Count; result.LedGoalsVerified = ledVerified.Count; result.LedGoalsUnverified = ledUnverified.Count; result.NoPointGoals = noPointGoals.Count; result.StandoffGoals = standoffGoals.Count;
            foreach (long key in ledGoals) if (noPointGoals.Contains(key)) result.LedAndNoPointGoals++;
            result.StandoffSet = sim.GoalStandoffSet; result.StandoffNone = sim.GoalStandoffNone; result.StandoffArrivals = sim.GoalStandoffArrivals; result.ReachedWhileAtStandoff = sim.GoalReachedWhileAtStandoff; result.GivenUpForProps = sim.GoalsGivenUpForProps; result.PickedAgainForProps = sim.GoalsPickedAgainForProps;
            result.ReachFound = sim.GoalReachFound; result.ReachNone = sim.GoalReachNone; result.ReachUnverified = sim.GoalReachUnverified; result.ReachArrivals = sim.GoalReachArrivals;
            result.MapBuildMillisecondsInAll = buildMs; result.MapBuildMillisecondsMost = buildMsMost; result.MapBuildMillisecondsMean = result.MapsBuilt > 0 ? buildMs / result.MapsBuilt : 0;
            result.CycleMillisecondsInAll = cycleMs; result.CycleMillisecondsMost = cycleMsMost; result.CycleMillisecondsMean = result.Cycles > 0 ? cycleMs / result.Cycles : 0;
            result.PrepareMillisecondsInAll = prepareMs;
            result.MapAgeAtCycleMean = result.MapsUsedByCycles > 0 ? ageSum / result.MapsUsedByCycles : 0; result.MapAgeAtCycleMost = ageMost;
            result.MapPublishDelayMean = delays > 0 ? delaySum / delays : 0; result.MapPublishDelayMost = delayMost;
            result.ReplayWallSeconds = wall.Elapsed.TotalSeconds;
            return result;
        }

        private string KindOf(int clip, PlannerConfig c) => clip < 0 ? "none" : dataset.Clips[clip].IsStationary ? "wait" : dataset.Clips[clip].AverageSpeed < c.idleCostSpeedMps ? "turn" : "move";

        /// <summary>A candidate's class from its segments after `from`: when walking begins, and what fills the time before (at most 6 s looked at).</summary>
        private string ClassOf(IReadOnlyList<PlanSegment> segments, float from, PlannerConfig c, out float walkStart)
        {
            walkStart = -1f;
            float turn = 0f, wait = 0f;
            for (int i = 0; i < segments.Count; i++)
            {
                PlanSegment s = segments[i];
                if (s.EndTime <= from) continue;
                if (s.StartTime > from + 6f) break;
                string kind = KindOf(s.Clip, c);
                if (kind == "move") { walkStart = Math.Max(0f, s.StartTime - from); break; }
                float seconds = Math.Min(s.EndTime, from + 6f) - Math.Max(s.StartTime, from);
                if (kind == "turn") turn += seconds; else wait += seconds;
            }

            if (walkStart >= 0f && walkStart <= 2.5f) return "move";
            return turn >= wait && turn > 0f ? "turn" : "wait";
        }

        private PropObstacleMap guideProps;   // the version the cycle being recorded took

        private static ulong Mix(ulong h, uint v)
        {
            for (int i = 0; i < 4; i++) { h = (h ^ (v & 0xffu)) * 1099511628211UL; v >>= 8; }
            return h;
        }

        private static uint Bits(float f) => BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);

        private static float Bearing(float fromX, float fromZ, float toX, float toZ) => (float)Math.Atan2(toX - fromX, toZ - fromZ) * Pose2.Rad2Deg;

        /// <summary>A traced NPC of one cycle: what the goal field says at the search's start, and where the chosen candidate goes.</summary>
        private void Guide(double t, AgentSnapshot agent, AgentCycleInfo info, PlannerConfig c, PropReplayResult result)
        {
            if (info.Candidates.Count == 0 || info.Chosen < 0 || info.Chosen >= info.Candidates.Count) return;
            float x = info.StartPose.X, z = info.StartPose.Z;
            float straight = (float)Math.Sqrt((x - agent.GoalX) * (x - agent.GoalX) + (z - agent.GoalZ) * (z - agent.GoalZ));
            float path = float.NaN, gx = agent.GoalX, gz = agent.GoalZ;
            bool detour = false;
            if (agent.Field != null)
            {
                path = agent.Field.PathDistanceAt(x, z);
                // as SingleAgentSearch.HeadingTarget: the field guides only when its path is longer than the straight line by 0.5 m
                detour = agent.Field.DistanceAt(x, z) - straight > 0.5f;
                if (detour) agent.Field.Waypoint(x, z, c.goalFieldLookAheadMeters, out gx, out gz);
            }

            // with the recovery: the same guide the search used from a place without a path
            FieldRecovery patch = info.Recovery != null ? info.Recovery.Patch : null;
            if (agent.Field != null && float.IsPositiveInfinity(path) && patch != null && patch.Found && patch.TryGuide(x, z, c.goalFieldLookAheadMeters, out float bx, out float bz)) { gx = bx; gz = bz; detour = true; }
            PlanCandidate chosen = info.Candidates[info.Chosen];
            string geo = chosen.Full != null && chosen.Full.Count > 0 ? GeoClass(chosen.Full, info.Boundary, out _, out _) : "none";
            var track = new float[22];
            for (int i = 0; i <= 10; i++)
            {
                Pose2 p = AgentPlan.PoseAt(dataset, chosen.Full, info.Boundary + i * 0.5f);
                track[i * 2] = p.X; track[i * 2 + 1] = p.Z;
            }

            int nLeave = 0;
            foreach (PlanCandidate candidate in info.Candidates) if (candidate.Full != null && candidate.Full.Count > 0 && GeoClass(candidate.Full, info.Boundary, out _, out _) == "leave") nLeave++;
            int first = chosen.Suffix.Count > 0 ? chosen.Suffix[0].Clip : -1;
            string name = first >= 0 ? (dataset.Clips[first].Category + "/" + dataset.Clips[first].DisplayName).Replace(',', ';').Replace(' ', '_') : "none";
            float cell = sim_cell(c);
            float startValue = float.NaN, startEstimate = float.NaN, estimateX = float.NaN, estimateZ = float.NaN, estimateAway = float.NaN;
            if (info.Recovery != null && info.Recovery.Patch != null)
            {
                if (info.Recovery.Patch.TryDistance(x, z, out float connected)) startValue = connected;
                if (info.Recovery.Patch.TryEstimate(x, z, out float est, out float ex, out float ez, out float ea)) { startEstimate = est; estimateX = ex; estimateZ = ez; estimateAway = ea; }
            }

            // where the NPC is at this cycle (the search starts later, after what is fixed: info.StartPose at info.Boundary)
            Pose2 nowPose = AgentPlan.PoseAt(dataset, agent.Plan.Segments, (float)t);
            int reached = 0; bool own = false, goalOpen = false; float nearest = float.NaN;
            if (agent.Field != null) { reached = agent.Field.ReachableCells; nearest = agent.Field.NearestPathMeters(x, z, 6f, out own, out goalOpen); }
            float propDistance = guideProps != null ? guideProps.DistanceAt(x, z) : float.NaN;
            result.GuideRows.Add(FormattableString.Invariant($"{t:F2},{agent.Id},{x:F2},{z:F2},{agent.GoalX:F2},{agent.GoalZ:F2},{straight:F2},{path:F2},{(detour ? 1 : 0)},{gx:F2},{gz:F2},{Bearing(x, z, gx, gz):F0},{Bearing(x, z, agent.GoalX, agent.GoalZ):F0},{geo},{name},{track[20]:F2},{track[21]:F2},{Bearing(x, z, track[20], track[21]):F0},{nLeave},{chosen.TotalCost:F2},{chosen.Cost.Dynamic:F2},{cell:F1},{(c.goalFieldMetricReach ? 1.5f : cell * 3f):F1},{reached},{(own ? 1 : 0)},{(goalOpen ? 1 : 0)},{nearest:F2},{(agent.Field != null ? agent.Field.RequiredClearance : 0f):F2},{propDistance:F2},{map.ClearanceAt(x, z):F2},{(patch == null ? "none" : patch.Found ? "found" : "no-way")},{(patch != null ? patch.Sources : 0)},{(patch != null ? patch.ConnectMeters : float.NaN):F2},{(patch != null ? patch.CentreValue : float.NaN):F2},{(patch != null ? patch.EntryX : float.NaN):F2},{(patch != null ? patch.EntryZ : float.NaN):F2},{nowPose.X:F2},{nowPose.Z:F2},{(agent.Field != null ? agent.Field.PathDistanceAt(nowPose.X, nowPose.Z) : float.NaN):F2},{(guideProps != null ? guideProps.DistanceAt(nowPose.X, nowPose.Z) : float.NaN):F2},{info.Boundary - t:F2},{startValue:F2},{startEstimate:F2},{estimateX:F2},{estimateZ:F2},{estimateAway:F2}"));
            result.GuideMarks.Add(new PropReplayResult.GuideMark { Time = t, Id = agent.Id, StartX = x, StartZ = z, GoalX = agent.GoalX, GoalZ = agent.GoalZ, GuideX = gx, GuideZ = gz, Detour = detour, Track = track, Back = patch != null && patch.Found ? patch.CentrePath().ToArray() : null });
        }

        // the goal fields' cell a config gives while the prop map closes cells (what the traced rows report)
        private static float sim_cell(PlannerConfig c) => c.propGoalField && c.propGoalFieldCellMeters > 0f ? c.propGoalFieldCellMeters : c.goalFieldCellMeters;

        /// <summary>A candidate's class by its track over the 5 s after `from`: net displacement and the farthest it gets from where it was.</summary>
        private string GeoClass(IReadOnlyList<PlanSegment> full, float from, out float net, out float far)
        {
            Pose2 origin = AgentPlan.PoseAt(dataset, full, from);
            net = 0f; far = 0f;
            for (float s = 0.5f; s <= 5.01f; s += 0.5f)
            {
                Pose2 p = AgentPlan.PoseAt(dataset, full, from + s);
                float d = p.DistanceTo(origin.X, origin.Z);
                if (d > far) far = d;
                net = d;
            }

            return net >= 1.5f ? "leave" : far < 1f ? "local" : "loop";
        }

        /// <summary>One NPC of one cycle: what was chosen, the best candidate of each class, why candidates were lost, when walking is planned.</summary>
        private void Diagnose(double t, AgentCycleInfo info, PlannerConfig c, DwellTracker dwell, Dictionary<int, double> plannedWalk, PropReplayResult result)
        {
            if (info.Candidates.Count == 0 || info.Chosen < 0 || info.Chosen >= info.Candidates.Count) return;
            float boundary = info.Boundary;
            int nMove = 0, nTurn = 0, nWait = 0;
            float bestMove = float.NaN, bestTurn = float.NaN, bestWait = float.NaN;
            string chosenClass = "wait";
            for (int i = 0; i < info.Candidates.Count; i++)
            {
                PlanCandidate candidate = info.Candidates[i];
                string kind = ClassOf(candidate.Suffix, boundary, c, out _);
                float total = candidate.TotalCost;
                if (kind == "move") { nMove++; if (float.IsNaN(bestMove) || total < bestMove) bestMove = total; }
                else if (kind == "turn") { nTurn++; if (float.IsNaN(bestTurn) || total < bestTurn) bestTurn = total; }
                else { nWait++; if (float.IsNaN(bestWait) || total < bestWait) bestWait = total; }
                if (i == info.Chosen) chosenClass = kind;
            }

            PlanCandidate chosen = info.Candidates[info.Chosen];
            // what is fixed until the plan can change: the kinds of the segments from now to the boundary
            var prefix = new System.Text.StringBuilder();
            foreach (PlanSegment s in chosen.Full)
            {
                if (s.EndTime <= t || s.StartTime >= boundary) continue;
                prefix.Append(KindOf(s.Clip, c)).Append(':').Append((Math.Min(s.EndTime, boundary) - Math.Max(s.StartTime, (float)t)).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
            }

            ClassOf(chosen.Full, (float)t, c, out float walkStart);
            bool postponed = false;
            if (walkStart >= 0f)
            {
                double at = t + walkStart;
                if (plannedWalk.TryGetValue(info.AgentId, out double before) && before > t && at > before + 0.25) postponed = true;   // it had not begun, and begins later now
                plannedWalk[info.AgentId] = at;
            }
            else if (plannedWalk.TryGetValue(info.AgentId, out double before) && before > t) { postponed = true; plannedWalk.Remove(info.AgentId); }   // the walk that was planned is gone

            if (chosenClass == "move") result.ChoseMove++;
            else if (chosenClass == "wait") result.ChoseWait++;
            else
            {
                result.ChoseTurn++;
                if (nMove > 0) { result.ChoseTurnWithMoveCandidate++; result.TurnOverMoveGapSum += bestMove - chosen.TotalCost; } else result.ChoseTurnWithoutMoveCandidate++;
                if (nWait > 0) { result.ChoseTurnWithWaitCandidate++; result.TurnOverWaitGapSum += bestWait - chosen.TotalCost; }
            }

            if (postponed) result.WalkPostponed++;
            // the same by the track itself
            int nLeave = 0, nLoop = 0, nLocal = 0;
            float bestLeave = float.NaN, bestLoop = float.NaN, bestLocal = float.NaN, chosenNet = 0f, chosenFar = 0f;
            string chosenGeo = "local";
            for (int i = 0; i < info.Candidates.Count; i++)
            {
                PlanCandidate candidate = info.Candidates[i];
                if (candidate.Full == null || candidate.Full.Count == 0) continue;
                string geo = GeoClass(candidate.Full, boundary, out float net, out float far);
                float total = candidate.TotalCost;
                if (geo == "leave") { nLeave++; if (float.IsNaN(bestLeave) || total < bestLeave) bestLeave = total; }
                else if (geo == "loop") { nLoop++; if (float.IsNaN(bestLoop) || total < bestLoop) bestLoop = total; }
                else { nLocal++; if (float.IsNaN(bestLocal) || total < bestLocal) bestLocal = total; }
                if (i == info.Chosen) { chosenGeo = geo; chosenNet = net; chosenFar = far; }
            }

            double inStay = dwell.OpenSeconds(info.AgentId);
            if (inStay >= 3.0 && !info.LodSkipped)
            {
                result.StayRows++;
                if (chosenGeo == "leave") result.StayChoseLeave++;
                else
                {
                    if (chosenGeo == "local") result.StayChoseLocal++; else result.StayChoseLoop++;
                    if (nLeave > 0) { result.StayNotLeaveWithLeaveCandidate++; result.StayLeaveGapSum += bestLeave - chosen.TotalCost; } else result.StayNotLeaveWithoutLeaveCandidate++;
                }
            }

            if (info.Stats.StateTrace != null)
            {
                foreach (string line in info.Stats.StateTrace) result.StateRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},{info.Boundary:F2},{info.StartPose.X:F2},{info.StartPose.Z:F2},{info.StartPose.YawDeg:F0},") + line);
            }

            if (info.Stats.Trace != null)
            {
                foreach (string line in info.Stats.Trace) result.TraceRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},") + line);
            }

            CostBreakdown k = chosen.Cost;
            SearchStats st = info.Stats;
            int first = chosen.Suffix.Count > 0 ? chosen.Suffix[0].Clip : -1;
            string name = first >= 0 ? (dataset.Clips[first].Category + "/" + dataset.Clips[first].DisplayName).Replace(',', ';').Replace(' ', '_') : "none";
            result.CycleRows.Add(FormattableString.Invariant($"{t:F2},{info.AgentId},{info.Tier},{(info.LodSkipped ? 1 : 0)},{(info.Status ?? string.Empty).Replace(',', ';').Trim()},{dwell.OpenSeconds(info.AgentId):F1},{boundary - t:F2},{prefix.ToString().Trim()},{info.Candidates.Count},{nMove},{nTurn},{nWait},{info.Chosen},{chosen.Origin},{chosenClass},{name},{walkStart:F2},{(postponed ? 1 : 0)},")
                + FormattableString.Invariant($"{k.Total:F3},{k.Progress:F3},{k.Heading:F3},{k.Speed:F3},{k.Idle:F3},{k.Turn:F3},{k.Facing:F3},{k.Churn:F3},{k.OtherAgent:F3},{k.Penalty:F1},{k.Dynamic:F2},{bestMove:F3},{bestTurn:F3},{bestWait:F3},")
                + FormattableString.Invariant($"{st.SuccessorsSeen},{st.SuccessorsEvaluated},{st.StaticRejects},{st.DeadEndRejects},{st.StopTailRejects},{st.BeamPruned},{st.DominancePruned},{st.FamilyPruned},{st.IdleExitsPruned},{st.ReservationRejects},{st.Finals},{(st.Timeout ? 1 : 0)},{(st.ExpansionCap ? 1 : 0)},")
                + FormattableString.Invariant($"{chosenGeo},{chosenNet:F2},{chosenFar:F2},{nLeave},{nLoop},{nLocal},{bestLeave:F3},{bestLoop:F3},{bestLocal:F3},{k.LocalStay:F3},{k.LocalStaySeconds:F2},{st.AdvEvaluated},{st.LocEvaluated},{st.AdvStaticRejects},{st.LocStaticRejects},{st.AdvBeamPruned},{st.LocBeamPruned},{st.AdvDominancePruned},{st.LocDominancePruned},{st.AdvFinals},{st.LocFinals},{st.BestAdvFinal:F3},{st.BestLocFinal:F3},{st.AdvReachedEnd},{st.LocReachedEnd},{st.LocalStayUnjudgedClips}"));
        }

        /// <summary>MobPlanCrowd.Visible: within 50 m, within 55 degrees of the view's heading, and not hidden by the static map -- from the recorded eye.</summary>
        private bool Visible(PropCapture.PlayerSample player, float x, float z)
        {
            float dx = x - player.EyeX, dz = z - player.EyeZ;
            if (dx * dx + dz * dz > 50f * 50f) return false;
            float bearing = (float)Math.Atan2(dx, dz) * Pose2.Rad2Deg;
            return Math.Abs(Pose2.WrapDeg(bearing - player.Yaw)) <= 55f && !map.SegmentBlocked(player.EyeX, player.EyeZ, x, z);
        }
    }
}
