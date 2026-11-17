using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>The settings of the local-stay measurement (first values, to be adjusted; written with every result).</summary>
    public sealed class DwellSettings
    {
        /// <summary>A stay counts once it lasted this long (s).</summary>
        public float MinSeconds = 3f;
        /// <summary>A stay goes on while every frame's position is within this of the position it began at (m).</summary>
        public float RadiusMeters = 0.5f;
        /// <summary>A stay is "near the obstacle" when the least distance to the yardstick's sections during it is under this (m; the NPC radius is added by the caller).</summary>
        public float NearMeters = 1.5f;
        /// <summary>A one-second window "rotates" at this much absolute yaw or more (degrees).</summary>
        public float RotatingWindowDegrees = 30f;
        /// <summary>A stay is "rotating on" with this many rotating windows or more, or this much absolute yaw in all.</summary>
        public int SustainedWindows = 4;
        public float SustainedDegrees = 360f;
        /// <summary>The goal counts as reached within this of it (m): the planner's goal radius.</summary>
        public float GoalRadiusMeters = 0.6f;

        public string Describe() => FormattableString.Invariant($"a stay: {MinSeconds:F1} s or longer within {RadiusMeters:F2} m of where it began (every frame; no speed condition); near the obstacle: least distance to the sections under NPC radius + {NearMeters:F1} m; a rotating second: {RotatingWindowDegrees:F0} deg or more of absolute yaw in a 1 s window; rotating on: {SustainedWindows} rotating seconds or more, or {SustainedDegrees:F0} deg of absolute yaw in all; goal reached within {GoalRadiusMeters:F2} m");
    }

    /// <summary>One local stay of one NPC.</summary>
    public sealed class DwellInterval
    {
        public int Id;
        public double Start, End;
        public float StartX, StartZ;
        /// <summary>Why it ended: left (the position went out of the radius), retired, section-end.</summary>
        public string EndedBy = "left";
        public float MaxDistance, PathMeters;
        /// <summary>The sum of the frame-to-frame yaw changes, each wrapped to +-180 degrees: absolute, and signed (+ = clockwise).</summary>
        public float AbsYaw, SignedYaw;
        /// <summary>The absolute yaw of every whole or partial 1 s window from the start.</summary>
        public readonly List<float> WindowYaw = new List<float>();
        public int RotatingWindows, LongestRotatingRun;
        /// <summary>Goal events inside the stay: (time, "reached" | "changed" | "blocked" | "unblocked"). Seen at the planning cycles (about one a second).</summary>
        public readonly List<(double time, string what)> GoalEvents = new List<(double, string)>();
        public double GoalUnreachedSeconds;
        /// <summary>Metres nearer the goal, summed over the spans of one and the same goal (straight line; a change of goal starts a new span and adds nothing).</summary>
        public float ProgressMeters;
        public float LeastObstacleDistance = float.PositiveInfinity;
        public double ObstacleDistanceSum;
        public int Frames;
        public int FirstMapVersion, LastMapVersion, MapVersions;
        public int PlanUpdates, PlanUpdatesThatChangedTheClip;
        /// <summary>The clips played, in order, with their seconds (run lengths; at most 24 entries, then counted only).</summary>
        public readonly List<(int clip, float seconds)> Clips = new List<(int, float)>();
        public int ClipRuns;
        public double StationaryClipSeconds;
        public bool Near;
        /// <summary>
        /// Planning cycles during the stay in which this NPC was searched, and of them those whose goal's cell was closed in the guiding
        /// grid, by level: [1] the goal point inside an obstacle, [2] outside but overlapping with the NPC radius and the walking
        /// margin, [3] walkable but inside the guiding grid's extra clearance, [4] the point passes that too and only the cell's centre is closed.
        /// </summary>
        public int SearchedCycles;
        public readonly int[] GoalClosedCycles = new int[5];
        /// <summary>The least clearance to the STATIC map during the stay (the static map's own conservative distance), and whether it is under NPC radius + NearMeters.</summary>
        public float LeastStaticDistance = float.PositiveInfinity;
        public bool NearStatic;
        /// <summary>
        /// At the moment the stay reached its minimum length: "path" (a GEOMETRIC path to a point 3 m away exists over the sections and the
        /// static map of that moment, in 0.25 m steps), "no-path" (none found), or "" (not asked). It says nothing of whether a
        /// sequence of clips can follow it, nor of a path to the goal.
        /// </summary>
        public string Escape = string.Empty;
        /// <summary>After a stay that ended by leaving: the seconds observed after it (to the run's end, a retirement, or 5 s), and the path walked in them.</summary>
        public float AfterObservedSeconds, AfterPathMeters;
        /// <summary>In those seconds: the distance from where the stay began at the last observed frame (net), the greatest such distance, and whether the NPC came back within 0.75 m of it after having been 1.5 m away.</summary>
        public float AfterNetMeters, AfterMaxMeters;
        public bool AfterReapproached;
        /// <summary>The seconds from this stay's end to the same NPC's next stay's start (-1: none in the run), and that stay's kind.</summary>
        public double NextStayGap = -1.0;
        public string NextStayKind = string.Empty;

        /// <summary>
        /// What became of the NPC after the stay: "walked-on" (left; 5 s after it is at least 2 m from where the stay began -- net, not
        /// path -- and did not come back to it, and no new stay began in them), "re-entered" (a new stay began within 5 s),
        /// "left-looped" (2 m of path or more but under 2 m net, or it came back), "left-slow" (under 2 m of path), "left-unknown"
        /// (under 5 s observed after), or the way it ended otherwise (retired, section-end).
        /// </summary>
        public string Outcome
        {
            get
            {
                if (EndedBy != "left") return EndedBy;
                if (NextStayGap >= 0.0 && NextStayGap <= 5.0) return "re-entered";
                if (AfterObservedSeconds < 4.9f) return "left-unknown";
                if (AfterNetMeters >= 2f && !AfterReapproached) return "walked-on";
                return AfterPathMeters >= 2f ? "left-looped" : "left-slow";
            }
        }

        public double Seconds => End - Start;
        public float YawRate => Seconds > 0 ? (float)(AbsYaw / Seconds) : 0f;
        public float MeanObstacleDistance => Frames > 0 ? (float)(ObstacleDistanceSum / Frames) : float.PositiveInfinity;

        /// <summary>still | turn-then-wait | rotating-on (one-way) | rotating-on (back-and-forth) | rotating-on (mixed).</summary>
        public string Kind(DwellSettings s)
        {
            if (RotatingWindows >= s.SustainedWindows || AbsYaw >= s.SustainedDegrees)
            {
                float ratio = AbsYaw > 0f ? Math.Abs(SignedYaw) / AbsYaw : 0f;
                return ratio > 0.8f ? "rotating-on (one-way)" : ratio < 0.3f ? "rotating-on (back-and-forth)" : "rotating-on (mixed)";
            }

            return RotatingWindows == 0 && AbsYaw < 45f ? "still" : "turn-then-wait";
        }

        public static string CsvHeader => "id,start,end,seconds,ended_by,kind,near,start_x,start_z,max_distance,path_m,abs_yaw,signed_yaw,yaw_rate,rotating_windows,longest_rotating_run,goal_unreached_s,progress_m,least_obstacle_m,mean_obstacle_m,map_first,map_last,map_versions,plan_updates,plan_updates_clip_changed,clip_runs,stationary_clip_s,goal_events,clips,window_yaw,least_static_m,near_static,geometric_path_3m,outcome,after_observed_s,after_path_m,next_stay_gap_s,next_stay_kind,after_net_m,after_max_m,after_reapproached,searched_cycles,goal_closed_l1,goal_closed_l2,goal_closed_l3,goal_closed_l4";

        public string ToCsv(DwellSettings s, ClipDataset dataset)
        {
            var events = new StringBuilder();
            foreach (var e in GoalEvents) events.Append(e.what).Append('@').Append(e.time.ToString("F1", CultureInfo.InvariantCulture)).Append(' ');
            var clips = new StringBuilder();
            foreach (var c in Clips) clips.Append(dataset != null && c.clip >= 0 && c.clip < dataset.Clips.Length ? Clean(dataset.Clips[c.clip].Category + "/" + dataset.Clips[c.clip].DisplayName) : c.clip.ToString(CultureInfo.InvariantCulture)).Append(':').Append(c.seconds.ToString("F1", CultureInfo.InvariantCulture)).Append(' ');
            if (ClipRuns > Clips.Count) clips.Append("(+").Append(ClipRuns - Clips.Count).Append(" more)");
            var windows = new StringBuilder();
            foreach (float w in WindowYaw) windows.Append(w.ToString("F0", CultureInfo.InvariantCulture)).Append(' ');
            return FormattableString.Invariant($"{Id},{Start:F2},{End:F2},{Seconds:F2},{EndedBy},{Kind(s)},{(Near ? 1 : 0)},{StartX:F2},{StartZ:F2},{MaxDistance:F3},{PathMeters:F2},{AbsYaw:F0},{SignedYaw:F0},{YawRate:F1},{RotatingWindows},{LongestRotatingRun},{GoalUnreachedSeconds:F2},{ProgressMeters:F2},")
                + FormattableString.Invariant($"{(float.IsInfinity(LeastObstacleDistance) ? -1f : LeastObstacleDistance):F2},{(float.IsInfinity(MeanObstacleDistance) ? -1f : MeanObstacleDistance):F2},{FirstMapVersion},{LastMapVersion},{MapVersions},{PlanUpdates},{PlanUpdatesThatChangedTheClip},{ClipRuns},{StationaryClipSeconds:F1},{events.ToString().Trim()},{clips.ToString().Trim()},{windows.ToString().Trim()},")
                + FormattableString.Invariant($"{(float.IsInfinity(LeastStaticDistance) ? -1f : LeastStaticDistance):F2},{(NearStatic ? 1 : 0)},{Escape},{Outcome},{AfterObservedSeconds:F1},{AfterPathMeters:F2},{NextStayGap:F1},{NextStayKind},{AfterNetMeters:F2},{AfterMaxMeters:F2},{(AfterReapproached ? 1 : 0)},{SearchedCycles},{GoalClosedCycles[1]},{GoalClosedCycles[2]},{GoalClosedCycles[3]},{GoalClosedCycles[4]}");
        }

        private static string Clean(string name) => string.IsNullOrEmpty(name) ? "?" : name.Replace(',', ';').Replace(' ', '_');
    }

    /// <summary>The stays of a run, summed: everywhere, and near the obstacle.</summary>
    public struct DwellTotals
    {
        public int Count, Npcs, RotatingOn, OneWay, BackAndForth, Still, TurnThenWait;
        public double Seconds, Longest, GoalUnreachedSeconds, AbsYaw, RotatingOnSeconds, RotatingWindowSeconds;
        public double StillSeconds, TurnThenWaitSeconds, StationaryClipSeconds;
        // the rotating-on stays: how they ended, and how long until the NPC left where it was
        public int RotatingWalkedOn, RotatingReentered, RotatingLooped, RotatingLeftOther, RotatingToSectionEnd, RotatingRetired, RotatingOpen, RotatingClosed;
        public double RotatingSecondsToLeaveSum, RotatingSecondsToLeaveMost;
        public int RotatingLeft;
        public double RotatingSecondsToLeaveMean => RotatingLeft > 0 ? RotatingSecondsToLeaveSum / RotatingLeft : 0;

        public double YawRate => Seconds > 0 ? AbsYaw / Seconds : 0;

        public static string CsvHeader(string p) => $"{p}_stays,{p}_npcs,{p}_s,{p}_longest_s,{p}_goal_unreached_s,{p}_abs_yaw,{p}_yaw_per_s,{p}_rotating_on,{p}_rotating_on_s,{p}_rotating_seconds,{p}_one_way,{p}_back_and_forth,{p}_still,{p}_turn_then_wait,{p}_still_s,{p}_turn_then_wait_s,{p}_stationary_clip_s,{p}_rot_left,{p}_rot_walked_on,{p}_rot_reentered,{p}_rot_left_other,{p}_rot_to_section_end,{p}_rot_retired,{p}_rot_s_to_leave_mean,{p}_rot_s_to_leave_most,{p}_rot_geo_path,{p}_rot_no_geo_path,{p}_rot_looped";
        public string ToCsv() => FormattableString.Invariant($"{Count},{Npcs},{Seconds:F1},{Longest:F1},{GoalUnreachedSeconds:F1},{AbsYaw:F0},{YawRate:F1},{RotatingOn},{RotatingOnSeconds:F1},{RotatingWindowSeconds:F0},{OneWay},{BackAndForth},{Still},{TurnThenWait},{StillSeconds:F1},{TurnThenWaitSeconds:F1},{StationaryClipSeconds:F1},{RotatingLeft},{RotatingWalkedOn},{RotatingReentered},{RotatingLeftOther},{RotatingToSectionEnd},{RotatingRetired},{RotatingSecondsToLeaveMean:F1},{RotatingSecondsToLeaveMost:F1},{RotatingOpen},{RotatingClosed},{RotatingLooped}");

        /// <summary>`where`: 0 everywhere, 1 near the prop sections, 2 near the static map, 3 near either.</summary>
        public static DwellTotals Of(IEnumerable<DwellInterval> stays, DwellSettings s, bool nearOnly) => Of(stays, s, nearOnly ? 1 : 0);

        public static DwellTotals Of(IEnumerable<DwellInterval> stays, DwellSettings s, int where)
        {
            var t = new DwellTotals();
            var ids = new HashSet<int>();
            foreach (DwellInterval d in stays)
            {
                if ((where == 1 && !d.Near) || (where == 2 && !d.NearStatic) || (where == 3 && !d.Near && !d.NearStatic)) continue;
                t.StationaryClipSeconds += d.StationaryClipSeconds;
                t.Count++; ids.Add(d.Id);
                t.Seconds += d.Seconds; if (d.Seconds > t.Longest) t.Longest = d.Seconds;
                t.GoalUnreachedSeconds += d.GoalUnreachedSeconds; t.AbsYaw += d.AbsYaw; t.RotatingWindowSeconds += d.RotatingWindows;
                string kind = d.Kind(s);
                if (kind.StartsWith("rotating-on"))
                {
                    t.RotatingOn++; t.RotatingOnSeconds += d.Seconds; if (kind.Contains("one-way")) t.OneWay++; else if (kind.Contains("back")) t.BackAndForth++;
                    string outcome = d.Outcome;
                    if (d.EndedBy == "left") { t.RotatingLeft++; t.RotatingSecondsToLeaveSum += d.Seconds; if (d.Seconds > t.RotatingSecondsToLeaveMost) t.RotatingSecondsToLeaveMost = d.Seconds; }
                    if (outcome == "walked-on") t.RotatingWalkedOn++; else if (outcome == "re-entered") t.RotatingReentered++; else if (outcome == "left-looped") t.RotatingLooped++; else if (outcome == "section-end") t.RotatingToSectionEnd++; else if (outcome == "retired") t.RotatingRetired++; else t.RotatingLeftOther++;
                    if (d.Escape == "path") t.RotatingOpen++; else if (d.Escape == "no-path") t.RotatingClosed++;
                }
                else if (kind == "still") { t.Still++; t.StillSeconds += d.Seconds; } else { t.TurnThenWait++; t.TurnThenWaitSeconds += d.Seconds; }
            }

            t.Npcs = ids.Count;
            return t;
        }
    }

    /// <summary>
    /// PROP-OBSTACLE-1 (evaluation): finds, per NPC, the local stays of a run -- a stretch of frames all within a radius of the
    /// position the stretch began at -- and what the NPC did during each: how much it turned (and which way), whether its goal
    /// was reached or changed, how near the obstacle it was, what it played. No speed condition: a turn with a small movement
    /// of the root is a stay too. A stretch that leaves the radius ends there and the next begins at that frame, so the
    /// stretches of one NPC never overlap; one that lasted the minimum counts, once, with its whole length. Fed every frame.
    /// </summary>
    public sealed class DwellTracker
    {
        public readonly DwellSettings Settings;
        public readonly List<DwellInterval> Stays = new List<DwellInterval>();
        private readonly Dictionary<int, Open> open = new Dictionary<int, Open>();
        private readonly float npcRadius;

        private sealed class Open
        {
            public DwellInterval stay;
            public float lastX, lastZ, lastYaw;
            public double windowStart;
            public float windowYaw;
            public int rotatingRun;
            public bool hasGoal;
            public float goalX, goalZ, spanStartDistance, lastGoalDistance;
            public int lastMapVersion = int.MinValue;
            public int lastClip = int.MinValue;
            public double lastTime;
            public DwellInterval after;        // the stay that ended by leaving, while its 5 s after are observed
            public DwellInterval previous;     // the NPC's last counted stay
            public bool asked;
        }

        /// <summary>Asked once per stay, when it reaches the minimum length, with the NPC's position: "open" or "closed" (PropReplay answers from the maps of that moment).</summary>
        public Func<float, float, string> EscapeProbe;

        public DwellTracker(DwellSettings settings, float npcRadius)
        {
            Settings = settings ?? new DwellSettings();
            this.npcRadius = npcRadius;
        }

        /// <summary>One frame of one NPC. `obstacleDistance` is to the yardstick's sections (infinite: none); `clip` what it plays; `stationaryClip` whether that clip is a standing one.</summary>
        public void Frame(double time, int id, float x, float z, float yaw, float obstacleDistance, int mapVersion, int clip, bool stationaryClip, float staticDistance = float.PositiveInfinity)
        {
            if (!open.TryGetValue(id, out Open o))
            {
                o = new Open();
                open[id] = o;
                Begin(o, time, id, x, z, yaw);
            }
            else
            {
                if (o.after != null)
                {
                    // the 5 s after a stay the NPC left: how far it went
                    if (time - o.after.End <= 5.0)
                    {
                        o.after.AfterObservedSeconds = (float)(time - o.after.End);
                        o.after.AfterPathMeters += (float)Math.Sqrt((x - o.lastX) * (x - o.lastX) + (z - o.lastZ) * (z - o.lastZ));
                        float away = (float)Math.Sqrt((x - o.after.StartX) * (x - o.after.StartX) + (z - o.after.StartZ) * (z - o.after.StartZ));
                        o.after.AfterNetMeters = away;
                        if (away > o.after.AfterMaxMeters) o.after.AfterMaxMeters = away;
                        if (o.after.AfterMaxMeters >= 1.5f && away < 0.75f) o.after.AfterReapproached = true;
                    }
                    else o.after = null;
                }

                float dx = x - o.stay.StartX, dz = z - o.stay.StartZ;
                float distance = (float)Math.Sqrt(dx * dx + dz * dz);
                if (distance > Settings.RadiusMeters)
                {
                    // out of the radius: the stay ended at the frame before; the next begins here
                    bool hadGoal = o.hasGoal; float gx = o.goalX, gz = o.goalZ;
                    Close(o, "left");
                    Begin(o, time, id, x, z, yaw);
                    if (hadGoal) SetGoal(o, gx, gz, x, z);
                }
                else
                {
                    DwellInterval s = o.stay;
                    double dt = time - o.lastTime;
                    if (distance > s.MaxDistance) s.MaxDistance = distance;
                    s.PathMeters += (float)Math.Sqrt((x - o.lastX) * (x - o.lastX) + (z - o.lastZ) * (z - o.lastZ));
                    float turn = Pose2.WrapDeg(yaw - o.lastYaw);
                    s.AbsYaw += Math.Abs(turn); s.SignedYaw += turn;
                    while (time - o.windowStart >= 1.0) EndWindow(o);
                    o.windowYaw += Math.Abs(turn);
                    s.End = time;
                    if (o.hasGoal)
                    {
                        float gd = (float)Math.Sqrt((x - o.goalX) * (x - o.goalX) + (z - o.goalZ) * (z - o.goalZ));
                        if (gd > Settings.GoalRadiusMeters) s.GoalUnreachedSeconds += dt;
                        o.lastGoalDistance = gd;
                    }

                    if (stationaryClip) s.StationaryClipSeconds += dt;
                    if (clip == o.lastClip && s.Clips.Count > 0 && s.ClipRuns <= s.Clips.Count) s.Clips[s.Clips.Count - 1] = (clip, s.Clips[s.Clips.Count - 1].seconds + (float)dt);
                }
            }

            DwellInterval stay = o.stay;
            stay.Frames++;
            if (staticDistance < stay.LeastStaticDistance) stay.LeastStaticDistance = staticDistance;
            if (!o.asked && stay.Seconds >= Settings.MinSeconds)
            {
                o.asked = true;
                if (EscapeProbe != null) stay.Escape = EscapeProbe(x, z);
            }

            if (obstacleDistance < stay.LeastObstacleDistance) stay.LeastObstacleDistance = obstacleDistance;
            if (!float.IsInfinity(obstacleDistance)) stay.ObstacleDistanceSum += obstacleDistance; else stay.ObstacleDistanceSum += 99.0;
            if (mapVersion != o.lastMapVersion)
            {
                if (stay.MapVersions == 0) stay.FirstMapVersion = mapVersion;
                stay.LastMapVersion = mapVersion; stay.MapVersions++;
                o.lastMapVersion = mapVersion;
            }

            if (clip != o.lastClip)
            {
                stay.ClipRuns++;
                if (stay.Clips.Count < 24) stay.Clips.Add((clip, 0f));
                o.lastClip = clip;
            }

            o.lastX = x; o.lastZ = z; o.lastYaw = yaw; o.lastTime = time;
        }

        private static void Begin(Open o, double time, int id, float x, float z, float yaw)
        {
            o.stay = new DwellInterval { Id = id, Start = time, End = time, StartX = x, StartZ = z };
            o.lastX = x; o.lastZ = z; o.lastYaw = yaw; o.lastTime = time;
            o.windowStart = time; o.windowYaw = 0f; o.rotatingRun = 0;
            o.hasGoal = false; o.lastMapVersion = int.MinValue; o.lastClip = int.MinValue;
            o.asked = false;
        }

        private void EndWindow(Open o)
        {
            DwellInterval s = o.stay;
            s.WindowYaw.Add(o.windowYaw);
            if (o.windowYaw >= Settings.RotatingWindowDegrees)
            {
                s.RotatingWindows++;
                if (++o.rotatingRun > s.LongestRotatingRun) s.LongestRotatingRun = o.rotatingRun;
            }
            else o.rotatingRun = 0;
            o.windowYaw = 0f;
            o.windowStart += 1.0;
        }

        private static void SetGoal(Open o, float goalX, float goalZ, float x, float z)
        {
            o.hasGoal = true; o.goalX = goalX; o.goalZ = goalZ;
            o.spanStartDistance = o.lastGoalDistance = (float)Math.Sqrt((x - goalX) * (x - goalX) + (z - goalZ) * (z - goalZ));
        }

        /// <summary>
        /// The NPC's goal as the simulation has it now (told at every planning cycle): `what` is null when nothing happened,
        /// else "reached", "changed", "blocked" or "unblocked" -- kept as an event of the stay; a new goal starts a new span of
        /// progress (the distance to the old goal and to the new one are never subtracted from each other).
        /// </summary>
        public void Goal(double time, int id, float goalX, float goalZ, string what)
        {
            if (!open.TryGetValue(id, out Open o)) return;
            bool moved = !o.hasGoal || goalX != o.goalX || goalZ != o.goalZ;
            if (what != null) o.stay.GoalEvents.Add((time, what));
            if (!moved) return;
            if (o.hasGoal) o.stay.ProgressMeters += o.spanStartDistance - o.lastGoalDistance;
            SetGoal(o, goalX, goalZ, o.lastX, o.lastZ);
        }

        /// <summary>A planning cycle searched this NPC; `goalClosedLevel` 1-4 when its goal's cell was closed in the guiding grid (0: open).</summary>
        public void Cycle(int id, int goalClosedLevel)
        {
            if (!open.TryGetValue(id, out Open o)) return;
            o.stay.SearchedCycles++;
            if (goalClosedLevel >= 1 && goalClosedLevel <= 4) o.stay.GoalClosedCycles[goalClosedLevel]++;
        }

        /// <summary>How long the NPC's open stretch has lasted (0: none).</summary>
        public double OpenSeconds(int id) => open.TryGetValue(id, out Open o) ? o.stay.Seconds : 0.0;

        /// <summary>A plan was published to the NPC; `clipChanged`: what it plays now or next is another clip than before.</summary>
        public void PlanUpdated(int id, bool clipChanged)
        {
            if (!open.TryGetValue(id, out Open o)) return;
            o.stay.PlanUpdates++;
            if (clipChanged) o.stay.PlanUpdatesThatChangedTheClip++;
        }

        /// <summary>The NPC is gone (retired by a cut, or replaced): its stay ends here.</summary>
        public void Retire(int id)
        {
            if (!open.TryGetValue(id, out Open o)) return;
            Close(o, "retired");
            open.Remove(id);
        }

        /// <summary>The run's end: every open stay ends.</summary>
        public void Finish()
        {
            foreach (Open o in open.Values) Close(o, "section-end");
            open.Clear();
        }

        private void Close(Open o, string why)
        {
            DwellInterval s = o.stay;
            if (s.Seconds < Settings.MinSeconds) return;
            if (o.windowYaw > 0f || s.WindowYaw.Count == 0) EndWindow(o);
            if (o.hasGoal) s.ProgressMeters += o.spanStartDistance - o.lastGoalDistance;
            s.EndedBy = why;
            s.Near = s.LeastObstacleDistance < npcRadius + Settings.NearMeters;
            s.NearStatic = s.LeastStaticDistance < npcRadius + Settings.NearMeters;
            if (o.previous != null) { o.previous.NextStayGap = s.Start - o.previous.End; o.previous.NextStayKind = s.Kind(Settings).Replace(' ', '_'); }
            o.previous = s;
            o.after = why == "left" ? s : null;
            Stays.Add(s);
        }
    }
}
