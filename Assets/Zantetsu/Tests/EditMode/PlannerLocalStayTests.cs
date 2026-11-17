using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// PLANNER-LOCAL-STAY: the cost a search gives a candidate for its time in a small place does not depend on the order its
    /// siblings were evaluated in -- it equals the cost of the same segments evaluated alone. (The first version shared one track
    /// between a parent's successors and a child's stop path overwrote it: the siblings after that child were not judged.)
    /// Runs the real search on the crowd's root-motion data, on an open map, for an NPC that has stood still for five seconds.
    /// </summary>
    public sealed class PlannerLocalStayTests
    {
        private const string AssetsPath = "Assets/Licensed/WalkCity/MobPlanAssetsWalkCity.asset";

        private sealed class Open : WalkableMap
        {
            public override float ClearanceAt(float x, float z) => 10f;
            public override bool IsWalkableAt(float x, float z) => true;
        }

        private static SearchRequest Request(ClipDataset dataset, PlannerConfig config)
        {
            int start = -1;
            for (int i = 0; i < dataset.Clips.Length; i++) if (dataset.Clips[i].IsWaitCapable) { start = i; break; }
            Assert.That(start, Is.GreaterThanOrEqualTo(0), "a wait-capable clip");
            var t = new float[11]; var x = new float[11]; var z = new float[11];
            for (int i = 0; i < t.Length; i++) t[i] = 5f + i * 0.5f - 0.01f;   // standing at the origin from 5 s to the start
            return new SearchRequest
            {
                AgentId = 1, Radius = 0.35f, GoalX = 0f, GoalZ = 30f, StartClip = start, StartPose = new Pose2(0f, 0f, 0f), StartTime = 10f,
                HorizonEnd = 10f + config.planningHorizonSeconds, Config = config, Deadline = new Deadline(600000), MaxExpansions = config.maxExpansionsPerAgent, MaxCandidates = 6,
                HistoryT = t, HistoryX = x, HistoryZ = z,
            };
        }

        /// <summary>
        /// The count of a recorded play takes the record's saved cycles and its retirements and starts in the order of the play,
        /// also where a cycle and the events after it fall into one step of the count (72 steps a second against 90 frames): an
        /// NPC retired in the frame after a saved cycle stays off the stage, one started then stays on. (First version: the events
        /// up to the step were applied and then the cycle's NPCs were put over them -- the retired NPC was back for a whole
        /// cycle.) An event of the cycle's own frame is in the cycle already: the cycle's time is that frame's as a float, the
        /// event's as a double, a little off either way. Starts are counted in scope by the player of their moment.
        /// </summary>
        [Test]
        public void TheRecordedPlaysCount_TakesCyclesAndEventsInThePlaysOrder_AlsoWithinOneStep()
        {
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            int idle = LocomotionSimulation.FindIdleClip(dataset);
            Assert.That(idle, Is.GreaterThanOrEqualTo(0));

            AgentPlan Standing(int id, float x) => new AgentPlan { AgentId = id, Segments = new System.Collections.Generic.List<PlanSegment> { new PlanSegment(idle, 0f, 100f, new Pose2(x, 0f, 0f)) } };
            PropCapture.CycleStart Cycle(double time, int[] onStage, int[] waiting)
            {
                var cycle = new PropCapture.CycleStart { Time = time };
                foreach (int id in onStage) { cycle.Baseline.Add(Standing(id, id == 5 ? 30f : id)); cycle.BaselineGeneration.Add(0); }
                foreach (int id in waiting) { cycle.Baseline.Add(Standing(id, id == 5 ? 30f : id)); cycle.BaselineGeneration.Add(0); cycle.Waiting.Add(id); }
                return cycle;
            }

            PropCapture.Event Retired(double time, int id) => new PropCapture.Event { Kind = PropCapture.RetiredRecord, Time = time, Id = id };
            // a start's event carries the place the crowd judged it at
            PropCapture.Event Started(double time, int id) => new PropCapture.Event { Kind = PropCapture.StartAttemptRecord, Result = PropCapture.StartStarted, Time = time, Id = id, X = id == 5 ? 30f : id, Z = 0f };

            // frame times as doubles; a cycle's time is its frame's as a float. One below its float and one above, to have both.
            double frameB = 11.001, frameC = 12.003;
            double below = frameB, above = frameC;
            for (int i = 0; i < 200 && !((double)(float)below > below); i++) below += 0.0001;
            for (int i = 0; i < 200 && !((double)(float)above < above); i++) above += 0.0001;
            frameB = below; frameC = above;
            Assert.That((double)(float)frameB, Is.GreaterThan(frameB), "the float is above this frame's time");
            Assert.That((double)(float)frameC, Is.LessThan(frameC), "the float is below this frame's time");

            var session = new PropCapture.Session();
            session.Builds.Add(new PropCapture.Build { Input = new PropObstacleInput { Cell = 0.2f, Cells = 250, PlaneY = 1.7f, MaxDistance = 3f } });
            // A at 10 s: 1, 2, 9 on stage; 3, 4 and 5 waiting.
            session.Starts.Add(Cycle(10.0, new[] { 1, 2, 9 }, new[] { 3, 4, 5 }));
            // B: in its own frame 9 is retired (before the queueing: B has it no more). In the frame after B: 2 retired, 3 started.
            session.Events.Add(Retired(frameB, 9));
            session.Starts.Add(Cycle((float)frameB, new[] { 1, 2 }, new[] { 3, 4, 5 }));
            session.Events.Add(Retired(frameB + 0.004, 2));
            session.Events.Add(Started(frameB + 0.006, 3));
            // C: in its own frame 4 and 5 are started (C has them on stage); 5 stands 30 m from the player.
            session.Events.Add(Started(frameC, 4));
            session.Events.Add(Started(frameC, 5));
            session.Starts.Add(Cycle((float)frameC, new[] { 1, 3, 4, 5 }, new int[0]));
            // after C: 6 is started, 6 m from the player, with a plan no saved cycle of the span has
            session.Events.Add(Started(frameC + 0.02, 6));
            session.Starts.Add(Cycle(13.2, new[] { 1, 3, 4, 5 }, new int[0]));   // beyond the span counted
            foreach (PropCapture.CycleStart cycle in session.Starts) session.Ends.Add(new PropCapture.CycleEnd { QueuedTime = cycle.Time, Time = cycle.Time + 0.1, Outcome = "published" });

            const float step = 1f / 72f;
            var replay = new PropReplay(session, dataset, null, null, new ScenarioSettings()) { CaptureSquareMeters = 80f };
            PropReplay.RecordedResult result = replay.Recorded(10.0, 13.0, 60f, 1.7f, step);

            Assert.That(result.CyclesWhoseNpcsAreNotTheEventsNpcs, Is.EqualTo(0), "every saved cycle's NPCs are the ones the events gave");
            Assert.That(result.RetirementsOfNoNpc, Is.EqualTo(0));
            Assert.That(result.StartsOfAKnownNpc, Is.EqualTo(0));
            Assert.That(result.StartsWithoutAPlace, Is.EqualTo(1), "NPC 6");
            Assert.That(result.StartsComparedWithThePlan, Is.EqualTo(3));
            Assert.That(result.StartPlaceDifferenceMost, Is.LessThan(0.5f), "standing plans: the saved plan's place is the event's");
            Assert.That(result.Cycles, Is.EqualTo(3), "A, B and C");
            Assert.That(result.Sections.Sum(s => s.Retired), Is.EqualTo(2));
            Assert.That(result.Sections.Sum(s => s.Starts), Is.EqualTo(4));
            Assert.That(result.Sections.Sum(s => s.StartsInScope), Is.EqualTo(3), "the start 30 m from the player is outside the 50 m square; the one without a saved plan is judged at its event's place");

            // the steps each NPC is on stage for: counted here the way the play went
            int Steps(double fromTime, double toTime)
            {
                int n = 0;
                for (double t = 10.0; t <= 13.0; t += step) if (t >= fromTime && t < toTime) n++;
                return n;
            }

            Assert.That(result.StepsOf[1], Is.EqualTo(Steps(10.0, 99.0)));
            Assert.That(result.StepsOf[9], Is.EqualTo(Steps(10.0, Math.Min(frameB, (float)frameB))), "retired in B's frame");
            Assert.That(result.StepsOf[2], Is.EqualTo(Steps(10.0, frameB + 0.004)), "retired in the frame after B: not back with B's NPCs");
            Assert.That(result.StepsOf[3], Is.EqualTo(Steps(frameB + 0.006, 99.0)), "started in the frame after B: not taken off by B's NPCs");
            Assert.That(result.StepsOf[4], Is.EqualTo(Steps(Math.Min(frameC, (float)frameC), 99.0)));
            Assert.That(result.StepsOf[5], Is.EqualTo(result.StepsOf[4]));
            Assert.That(result.StepsOf.ContainsKey(6), Is.False);
            Assert.That(result.NpcStepsWithoutAPlace, Is.EqualTo(Steps(frameC + 0.02, 99.0)), "NPC 6 is on stage without a place");
            // the case is one: B and the two events after it fall into the same step
            int stepOfB = (int)Math.Ceiling(((float)frameB - 10.0) / step), stepOfLast = (int)Math.Ceiling((frameB + 0.006 - 10.0) / step);
            Assert.That(stepOfLast, Is.EqualTo(stepOfB), "the case");

            Assert.That(PropReplay.CycleScale(frameB), Is.EqualTo((double)(float)frameB));
        }

        /// <summary>
        /// A saved state carries what PROP-GOAL-REACH keeps from one cycle to the next (the point, what it was set for, the goal
        /// stage, the running no-point time): read back, every agent has them, and saving the read state gives the same bytes.
        /// A state of the format before (1) is read too; those start as in a new agent and the rest is as saved.
        /// </summary>
        [Test]
        public void ASavedState_CarriesTheReachState_AndTheFormatBeforeIsStillRead()
        {
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            var map = new Open { OriginX = -500f, OriginZ = -500f, WorldWidth = 1000f, WorldHeight = 1000f };
            LocomotionSimulation Made()
            {
                var made = new LocomotionSimulation(dataset, MobPlanPreset.Planner());
                made.Reset(new ScenarioSettings(), map);
                return made;
            }

            LocomotionSimulation sim = Made();
            Assert.That(sim.Agents.Count, Is.GreaterThanOrEqualTo(2), "the scenario's agents");
            SimAgent led = sim.Agents[0], none = sim.Agents[1];
            led.GoalRevision = 7; led.GoalStage = 5;
            led.ReachActive = true; led.ReachX = 12.25f; led.ReachZ = -3.5f; led.ReachGoalRevision = 7; led.ReachVerified = true; led.ReachStandoff = true; led.ReachNoneRevision = 6;
            none.GoalRevision = 3; none.GoalStage = 4;
            none.ReachNone = true; none.ReachNoneSince = 41.5f; none.ReachNoneStage = 4; none.ReachNoneRevision = 3;
            Assert.That(led.ReachValid, Is.True);
            Assert.That(none.NoPointSeconds(50f), Is.EqualTo(8.5f));

            byte[] Bytes(LocomotionSimulation of, int format)
            {
                var stream = new System.IO.MemoryStream();
                using (var w = new System.IO.BinaryWriter(stream)) of.SaveState(w, format);
                return stream.ToArray();
            }

            LocomotionSimulation Read(byte[] state)
            {
                LocomotionSimulation into = Made();
                using (var r = new System.IO.BinaryReader(new System.IO.MemoryStream(state, false))) into.LoadState(r);
                return into;
            }

            Assert.That(LocomotionSimulation.StateFormat, Is.EqualTo(2));
            byte[] now = Bytes(sim, 2);
            using (var check = new System.IO.MemoryStream())
            {
                using (var w = new System.IO.BinaryWriter(check)) sim.SaveState(w);
                Assert.That(check.ToArray(), Is.EqualTo(now), "SaveState writes the newest format");
            }

            LocomotionSimulation back = Read(now);
            Assert.That(back.Agents.Count, Is.EqualTo(sim.Agents.Count));
            SimAgent a = back.Agents[0], b = back.Agents[1];
            Assert.That(a.Id, Is.EqualTo(led.Id));
            Assert.That(a.GoalStage, Is.EqualTo(5));
            Assert.That(a.ReachActive && a.ReachValid && a.ReachVerified && a.ReachStandoff, Is.True);
            Assert.That(a.ReachX, Is.EqualTo(12.25f)); Assert.That(a.ReachZ, Is.EqualTo(-3.5f));
            Assert.That(a.ReachGoalRevision, Is.EqualTo(7)); Assert.That(a.ReachNoneRevision, Is.EqualTo(6));
            Assert.That(a.PlanGoalX, Is.EqualTo(12.25f), "the planner is led to the point again");
            Assert.That(b.GoalStage, Is.EqualTo(4));
            Assert.That(b.ReachNone, Is.True); Assert.That(b.ReachNoneStage, Is.EqualTo(4)); Assert.That(b.ReachNoneRevision, Is.EqualTo(3));
            Assert.That(b.ReachNoneSince, Is.EqualTo(41.5f));
            Assert.That(b.NoPointSeconds(50f), Is.EqualTo(8.5f), "the running time goes on");
            Assert.That(b.ReachActive, Is.False);
            Assert.That(Bytes(back, 2), Is.EqualTo(now), "saved again: the same bytes");

            // the format before: shorter by the added part, read, the added part as in a new agent, the rest as saved
            byte[] old = Bytes(sim, 1);
            Assert.That(now.Length - old.Length, Is.EqualTo(sim.Agents.Count * 32), "32 bytes an agent");
            LocomotionSimulation early = Read(old);
            Assert.That(early.Agents.Count, Is.EqualTo(sim.Agents.Count));
            var fresh = new SimAgent();
            foreach (SimAgent e in early.Agents)
            {
                Assert.That(e.GoalStage, Is.EqualTo(fresh.GoalStage));
                Assert.That(e.ReachActive, Is.False); Assert.That(e.ReachNone, Is.False); Assert.That(e.ReachVerified, Is.False); Assert.That(e.ReachStandoff, Is.False);
                Assert.That(e.ReachGoalRevision, Is.EqualTo(fresh.ReachGoalRevision)); Assert.That(e.ReachNoneRevision, Is.EqualTo(fresh.ReachNoneRevision));
                Assert.That(e.ReachNoneStage, Is.EqualTo(fresh.ReachNoneStage));
                Assert.That(float.IsNaN(e.ReachNoneSince), Is.True);
            }

            Assert.That(early.Agents[0].GoalRevision, Is.EqualTo(7));
            Assert.That(early.Agents[0].PlanGoalX, Is.EqualTo(early.Agents[0].GoalX), "no point: the goal itself");
            Assert.That(Bytes(early, 1), Is.EqualTo(old), "the rest is as saved");

            // a format this code does not know is refused
            byte[] unknown = (byte[])now.Clone();
            unknown[0] = 3;
            Assert.Throws<System.IO.InvalidDataException>(() => Read(unknown));
        }

        [Test]
        public void ACandidatesLocalStayCost_IsThatOfItsSegmentsAlone_WhateverWasEvaluatedBeforeIt()
        {
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            var map = new Open { OriginX = -500f, OriginZ = -500f, WorldWidth = 1000f, WorldHeight = 1000f };
            PlannerConfig config = MobPlanPreset.Planner();
            config.localStayCost = true;

            SearchRequest request = Request(dataset, config);
            var candidates = new SingleAgentSearch(dataset, map).Run(request, out SearchStats stats);
            Assert.That(candidates.Count, Is.GreaterThan(0));
            Assert.That(stats.LocalStayUnjudgedClips, Is.EqualTo(0), "every clip was judged with the track of its own start");
            bool any = false;
            foreach (PlanCandidate candidate in candidates)
            {
                CostBreakdown alone = new SingleAgentSearch(dataset, map).EvaluateSegments(request, candidate.Suffix, out _);
                Assert.That(candidate.Cost.LocalStaySeconds, Is.EqualTo(alone.LocalStaySeconds).Within(0.02f), "candidate " + candidate.Index + ": the search's seconds against the segments alone");
                Assert.That(candidate.Cost.LocalStay, Is.EqualTo(alone.LocalStay).Within(0.01f));
                any |= alone.LocalStaySeconds > 0f;
            }

            Assert.That(any, Is.True, "an NPC that stood still pays for the first moments of any plan: the case has something to compare");

            // the first version's defect, kept behind a switch: here it leaves clips unjudged -- so this case would have seen it
            PlannerConfig broken = config.Clone();
            broken.localStayNoRestoreForTest = true;
            new SingleAgentSearch(dataset, map).Run(Request(dataset, broken), out SearchStats before);
            Assert.That(before.LocalStayUnjudgedClips, Is.GreaterThan(0), "the case reaches the place where the shared track was overwritten");
        }

        [Test]
        public void WithWorkLimitsOnly_NoDecisionReadsTheClock()
        {
            Assert.That(new Deadline(0).Expired, Is.True, "a spent wall-clock limit ends the work");
            var counted = new Deadline(0, true);
            Assert.That(counted.Expired, Is.False);
            Assert.That(counted.ElapsedMilliseconds, Is.EqualTo(0.0));
            Assert.That(counted.RemainingMilliseconds, Is.EqualTo(0.0), "the whole limit remains, whatever the clock says");
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            var map = new Open { OriginX = -500f, OriginZ = -500f, WorldWidth = 1000f, WorldHeight = 1000f };
            PlannerConfig config = MobPlanPreset.Planner();
            SearchRequest spent = Request(dataset, config);
            spent.Deadline = new Deadline(0);
            new SingleAgentSearch(dataset, map).Run(spent, out SearchStats cut);
            Assert.That(cut.Timeout, Is.True, "the case: the same search under a spent clock is cut short");
            SearchRequest a = Request(dataset, config), b = Request(dataset, config);
            a.Deadline = new Deadline(0, true); b.Deadline = new Deadline(0, true);
            var first = new SingleAgentSearch(dataset, map).Run(a, out SearchStats one);
            var second = new SingleAgentSearch(dataset, map).Run(b, out SearchStats two);
            Assert.That(one.Timeout || two.Timeout, Is.False);
            Assert.That(one.Expansions, Is.GreaterThan(0));
            Assert.That(two.Expansions, Is.EqualTo(one.Expansions));
            Assert.That(second.Count, Is.EqualTo(first.Count));
            for (int i = 0; i < first.Count; i++)
            {
                Assert.That(second[i].Suffix.Count, Is.EqualTo(first[i].Suffix.Count));
                Assert.That(second[i].Cost.Total, Is.EqualTo(first[i].Cost.Total), "candidate " + i);
                for (int s = 0; s < first[i].Suffix.Count; s++) Assert.That(second[i].Suffix[s].Clip, Is.EqualTo(first[i].Suffix[s].Clip));
            }
        }

        private static PropShape Box(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            float[] lohi = { x0, y0, z0, x1, y1, z1 };
            var vertices = new float[24];
            for (int v = 0; v < 8; v++)
            {
                vertices[v * 3] = lohi[(v & 1) != 0 ? 3 : 0]; vertices[v * 3 + 1] = lohi[(v & 2) != 0 ? 4 : 1]; vertices[v * 3 + 2] = lohi[(v & 4) != 0 ? 5 : 2];
            }

            int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            return new PropShape(1, vertices, new[] { 0, 8 }, edges, new[] { 0, 12 }, lohi);
        }

        private static CycleResult Cycle(ClipDataset dataset, WalkableMap map, PlannerConfig config, float now, AgentPlan plan, PropObstacleMap props)
        {
            var snapshot = new PlanningSnapshot { Now = now, Revision = 1, Props = props };
            snapshot.Agents.Add(new AgentSnapshot { Id = 1, Radius = 0.35f, GoalX = 0f, GoalZ = 40f, Plan = plan.Clone() });
            return LocomotionPlanner.RunCycle(dataset, map, config, snapshot);
        }

        /// <summary>The trial's planner (PlannerConfig.ApplyPropTrial): the parts taken are on, the re-check of the frozen part is off, nothing else differs from the defaults.</summary>
        [Test]
        public void TheTrialsPlanner_TurnsOnWhatWasTaken_AndLeavesTheRecheckOff()
        {
            var plain = new PlannerConfig();
            Assert.That(plain.propGoalField || plain.goalFieldRecovery || plain.idleCostLowTranslation || plain.propGoalReach || plain.propPrefixRecheck, Is.False, "the defaults");
            var trial = new PlannerConfig { propPrefixRecheck = true };
            trial.ApplyPropTrial();
            Assert.That(trial.propGoalField && trial.goalFieldRecovery && trial.idleCostLowTranslation && trial.propGoalReach, Is.True);
            Assert.That(trial.propGoalFieldCellMeters, Is.EqualTo(1f));
            Assert.That(trial.propGoalNoReachMode, Is.EqualTo(2));
            Assert.That(trial.propPrefixRecheck, Is.False);
            trial.propGoalField = false; trial.propGoalFieldCellMeters = 0f; trial.goalFieldRecovery = false; trial.idleCostLowTranslation = false; trial.propGoalReach = false; trial.propGoalNoReachMode = 0;
            Assert.That(UnityEngine.JsonUtility.ToJson(trial), Is.EqualTo(UnityEngine.JsonUtility.ToJson(plain)), "nothing else");
        }

        /// <summary>
        /// PROP-PREFIX-RECHECK: a freeze ended early for an overlap must be followed by a search, also where the old plan would
        /// otherwise be kept without one. The re-check samples the plan every 0.1 s; the old plan's cost samples each clip at the
        /// planner's collision rate from the clip's start, so the two can differ about a short overlap. Here the cost's rate is made
        /// so low that it looks at clip starts only and a small piece sits at the middle of a walking clip of the frozen part: the
        /// cost sees nothing, the walker counts as satisfied, and only the re-check knows.
        /// </summary>
        [Test]
        public void AFreezeEndedEarlyForAnOverlap_IsSearchedAgain_AlsoWhereTheOldPlanWouldBeKeptWithoutASearch()
        {
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            var map = new Open { OriginX = -500f, OriginZ = -500f, WorldWidth = 1000f, WorldHeight = 1000f };
            PlannerConfig first = MobPlanPreset.Planner();
            first.lodEnabled = false; first.workLimitsOnly = true; first.skipSatisfiedAgents = true;
            int wait = -1;
            for (int i = 0; i < dataset.Clips.Length; i++) if (dataset.Clips[i].IsWaitCapable) { wait = i; break; }
            Assert.That(wait, Is.GreaterThanOrEqualTo(0), "a wait-capable clip");
            var standing = new AgentPlan { AgentId = 1 };
            standing.Segments.Add(new PlanSegment(wait, 0f, dataset.Clips[wait].Duration, new Pose2(0f, 0f, 0f)));
            CycleResult begun = Cycle(dataset, map, first, 0f, standing, null);
            Assert.That(begun.NewPlans.ContainsKey(1), Is.True, "a first plan");
            AgentPlan walking = begun.NewPlans[1];

            // a time and a walking clip of the part a cycle would freeze, later than the earliest end the re-check allows, whose
            // middle is well away from every clip start of the plan
            PlannerConfig config = MobPlanPreset.Planner();
            config.lodEnabled = false; config.workLimitsOnly = true; config.skipSatisfiedAgents = true;
            config.collisionSampleHz = 0.001f;   // the old plan's cost: one sample a clip, at its start
            config.propPrefixRecheckCommitSeconds = 1f;
            float now = -1f; PropObstacleMap props = null;
            var durations = new System.Text.StringBuilder();
            foreach (PlanSegment segment in walking.Segments) durations.Append((segment.EndTime - segment.StartTime).ToString("F2")).Append(dataset.Clips[segment.Clip].IsStationary ? "s " : " ");
            Pose2 last = AgentPlan.PoseAt(dataset, walking.Segments, walking.EndTime);
            for (float at = 0.25f; at < 12f && now < 0f; at += 0.25f)
            {
                int boundary = -1, earliest = -1;
                for (int i = 0; i < walking.Segments.Count; i++) if (walking.Segments[i].EndTime >= at + config.commitTargetSeconds - 1e-4f) { boundary = i; break; }
                for (int i = 0; i < walking.Segments.Count; i++) if (walking.Segments[i].EndTime >= at + 1f - 1e-4f) { earliest = i; break; }
                if (boundary < 0 || earliest < 0 || earliest >= boundary) continue;
                float from = walking.Segments[earliest].EndTime, until = walking.Segments[boundary].EndTime;
                // a thin piece through the plane (one or two cells), beside the plan at one of the times the re-check looks at
                for (float time = from + 0.3f; time <= until - 0.3f && now < 0f; time += 0.1f)
                {
                    Pose2 on = AgentPlan.PoseAt(dataset, walking.Segments, time);
                    for (int o = 0; o < 49 && now < 0f; o++)
                    {
                        float px = on.X + (o % 7 - 3) * 0.15f, pz = on.Z + (o / 7 - 3) * 0.15f;
                        var input = new PropObstacleInput();
                        input.SetWindow(new PropObstacleSettings { cells = 150, cellMeters = 0.2f, planeHeightMeters = 1.5f, maxDistanceMeters = 3f }, (float)Math.Floor(on.X) - 15f, (float)Math.Floor(on.Z) - 15f);
                        int item = input.Add(Box(-0.01f, 0f, -0.01f, 0.01f, 3f, 0.01f), 1, 1, 0);
                        Array.Clear(input.Matrices, item * 12, 12);
                        input.Matrices[item * 12] = 1f; input.Matrices[item * 12 + 5] = 1f; input.Matrices[item * 12 + 10] = 1f; input.Matrices[item * 12 + 3] = px; input.Matrices[item * 12 + 11] = pz;
                        var made = new PropObstacleMap();
                        new PropObstacleBuilder().Build(input, null, made);
                        // the re-check must see it ...
                        bool seen = false;
                        for (float look = from; look <= until; look += 0.1f)
                        {
                            Pose2 where = AgentPlan.PoseAt(dataset, walking.Segments, look);
                            if (made.DistanceAt(where.X, where.Z) < 0.45f) { seen = true; break; }
                        }

                        if (!seen) continue;
                        // ... and no clip start after the earliest end, nor the place the plan ends at, may be near it (the cost looks there)
                        bool apart = made.DistanceAt(last.X, last.Z) >= 0.5f;
                        for (int i = earliest + 1; i < walking.Segments.Count && apart; i++)
                        {
                            Pose2 start = walking.Segments[i].WorldStart;
                            if (made.DistanceAt(start.X, start.Z) < 0.5f) apart = false;
                        }

                        if (apart) { now = at; props = made; }
                    }
                }
            }

            Assert.That(now, Is.GreaterThan(0f), "a place for a piece that the re-check sees and no clip start is near; the plan's clips (s): " + durations);

            // the case: without the re-check the walker keeps its plan with no search and no overlap in its cost
            config.propPrefixRecheck = false;
            AgentCycleInfo kept = Cycle(dataset, map, config, now, walking, props).Agents[0];
            Assert.That(kept.PrefixShortened, Is.False);
            Assert.That(kept.Candidates[0].Cost.PropOverlapSeconds, Is.EqualTo(0f), "the case: the old plan's cost does not see the piece");
            Assert.That(kept.Stats.Expansions, Is.EqualTo(0), "the case: the old plan is kept without a search");

            config.propPrefixRecheck = true;
            AgentCycleInfo checkedAgain = Cycle(dataset, map, config, now, walking, props).Agents[0];
            Assert.That(checkedAgain.PrefixShortened, Is.True, "the re-check sees the piece in the frozen part");
            Assert.That(checkedAgain.Boundary, Is.LessThan(kept.Boundary), "and ends the freeze earlier");
            Assert.That(checkedAgain.Stats.Expansions, Is.GreaterThan(0), "and the NPC is searched again");
        }

        /// <summary>
        /// PROP-PREFIX-RECHECK with propPrefixRecheckClearStart: where the earliest end of the freeze is itself within the pieces,
        /// the freeze is left as it is (counted); without the setting it is ended there.
        /// </summary>
        [Test]
        public void WithClearStart_AFreezeIsNotEndedWhereTheSearchWouldStartInsideThePieces()
        {
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            var map = new Open { OriginX = -500f, OriginZ = -500f, WorldWidth = 1000f, WorldHeight = 1000f };
            PlannerConfig config = MobPlanPreset.Planner();
            config.lodEnabled = false; config.workLimitsOnly = true;
            int wait = -1;
            for (int i = 0; i < dataset.Clips.Length; i++) if (dataset.Clips[i].IsWaitCapable) { wait = i; break; }
            Assert.That(wait, Is.GreaterThanOrEqualTo(0), "a wait-capable clip");
            var standing = new AgentPlan { AgentId = 1 };
            standing.Segments.Add(new PlanSegment(wait, 0f, dataset.Clips[wait].Duration, new Pose2(0f, 0f, 0f)));
            AgentPlan walking = Cycle(dataset, map, config, 0f, standing, null).NewPlans[1];
            float now = -1f; int earliest = -1;
            for (float at = 0.25f; at < 12f && now < 0f; at += 0.25f)
            {
                int boundary = -1, early = -1;
                for (int i = 0; i < walking.Segments.Count; i++) if (walking.Segments[i].EndTime >= at + config.commitTargetSeconds - 1e-4f) { boundary = i; break; }
                for (int i = 0; i < walking.Segments.Count; i++) if (walking.Segments[i].EndTime >= at + 1f - 1e-4f) { early = i; break; }
                if (boundary >= 0 && early >= 0 && early < boundary && !dataset.Clips[walking.Segments[boundary].Clip].IsStationary) { now = at; earliest = early; }
            }

            Assert.That(now, Is.GreaterThan(0f), "a time at which two clip ends lie in the part to freeze");
            // a 2 m piece about the place the earliest end of the freeze is at: that place and the plan after it are inside
            Pose2 early0 = AgentPlan.PoseAt(dataset, walking.Segments, walking.Segments[earliest].EndTime);
            var input = new PropObstacleInput();
            input.SetWindow(new PropObstacleSettings { cells = 150, cellMeters = 0.2f, planeHeightMeters = 1.5f, maxDistanceMeters = 3f }, (float)Math.Floor(early0.X) - 15f, (float)Math.Floor(early0.Z) - 15f);
            int item = input.Add(Box(-1f, 0f, -1f, 1f, 3f, 1f), 1, 1, 0);
            Array.Clear(input.Matrices, item * 12, 12);
            input.Matrices[item * 12] = 1f; input.Matrices[item * 12 + 5] = 1f; input.Matrices[item * 12 + 10] = 1f; input.Matrices[item * 12 + 3] = early0.X; input.Matrices[item * 12 + 11] = early0.Z;
            var props = new PropObstacleMap();
            new PropObstacleBuilder().Build(input, null, props);
            Assert.That(props.DistanceAt(early0.X, early0.Z), Is.LessThan(0.45f));

            config.propPrefixRecheck = true; config.propPrefixRecheckCommitSeconds = 1f;
            config.propPrefixRecheckClearStart = false;
            CycleResult ended = Cycle(dataset, map, config, now, walking, props);
            Assert.That(ended.Agents[0].PrefixShortened, Is.True, "the case: without the setting the freeze ends inside the pieces");
            config.propPrefixRecheckClearStart = true;
            CycleResult left = Cycle(dataset, map, config, now, walking, props);
            Assert.That(left.Agents[0].PrefixShortened, Is.False);
            Assert.That(left.Metrics.propPrefixStartInside, Is.EqualTo(1));
            Assert.That(left.Agents[0].Boundary, Is.GreaterThan(ended.Agents[0].Boundary), "the freeze keeps its usual end");
        }

        [Test]
        public void WithTheCostOff_NothingIsCounted()
        {
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) Assert.Ignore("no " + AssetsPath);
            ClipDataset dataset = ClipDataset.Read(assets.dataset.bytes);
            var map = new Open { OriginX = -500f, OriginZ = -500f, WorldWidth = 1000f, WorldHeight = 1000f };
            PlannerConfig config = MobPlanPreset.Planner();
            Assert.That(config.localStayCost || config.idleCostLowTranslation, Is.False, "both evaluation switches are off in the product's preset");
            var candidates = new SingleAgentSearch(dataset, map).Run(Request(dataset, config), out SearchStats stats);
            foreach (PlanCandidate candidate in candidates) Assert.That(candidate.Cost.LocalStay == 0f && candidate.Cost.LocalStaySeconds == 0f, Is.True);
            Assert.That(stats.LocalStayUnjudgedClips, Is.EqualTo(0));
        }
    }
}
