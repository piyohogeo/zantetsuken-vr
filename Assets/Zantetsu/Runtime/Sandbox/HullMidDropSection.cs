using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// CHECK ONLY (TL, 2026-10-01): the coexistence run's re-cut during a drop as a <b>required</b> section of its own, run
    /// before the script while the always-kinematic building is below its cut limit. Whether it is required is decided
    /// first (the coexistence scenario, the always-kinematic hull and the limit), never by what the run later finds; a
    /// required section that is not started, finds no target, runs out of time, ends part way or overlaps the external
    /// replay is a failure, as is one whose judgements fail.
    /// <list type="bullet">
    /// <item>One level Slash through the building's hull, given once to the ordinary detector; once its cut is published
    /// with its drop running, one upright Slash given once the same way. Each is followed by its building id and Slash id
    /// to its hit record and display operations (never the run's total of cuts), a held one to its end.</item>
    /// <item>Required: the first drop running when the second was given; that drop stopped part way (where it stood);
    /// both cuts published with every display operation completed and committed; exactly one body, one enabled collider and
    /// one hull for the building at every frame of the section; no other hit on the building meanwhile; everything else
    /// each Slash set going (another NPC, a prop) ended through the ordinary completion wait.</item>
    /// <item>The two cuts count towards n like any other; nothing is reset after the section.</item>
    /// </list>
    /// Nothing is cut, published or moved from here other than through the ordinary detector.
    /// </summary>
    public sealed class HullMidDropSection
    {
        public enum Result { NotStarted, Running, NoTarget, TimedOut, Interrupted, ExternalOverlap, Failed, Passed }

        /// <summary>Decided before the run: whether the section must run and pass.</summary>
        public readonly bool Required;
        public readonly string RequiredBecause;
        public Result Outcome { get; private set; } = Result.NotStarted;
        public bool Started { get; private set; }
        public bool Completed { get; private set; }
        public bool Passed => Outcome == Result.Passed;
        public string Detail { get; private set; } = "";

        public int Building { get; private set; } = -1;
        public long FirstSlash { get; private set; }
        public long SecondSlash { get; private set; }
        public BuildingHullFusion.HullHit FirstHit { get; private set; }
        public BuildingHullFusion.HullHit SecondHit { get; private set; }
        public int GeometriesAtStart { get; private set; } = -1;
        public int GeometriesAtEnd { get; private set; } = -1;
        public bool FirstDropRunningAtSecond { get; private set; }
        public double StopFraction { get; private set; } = double.NaN;
        public int Frames { get; private set; }
        public int FramesNotOneEach { get; private set; }
        public int FirstFrame { get; private set; } = -1;
        public int LastFrame { get; private set; } = -1;

        // Deadlines (real seconds), each bounded and never extended.
        public float settleSeconds = 10f, publishSeconds = 10f, endSeconds = 30f;

        private readonly List<string> _failures = new List<string>();
        private string _notOneEachWhy;

        public HullMidDropSection(bool required, string because)
        {
            Required = required;
            RequiredBecause = because;
        }

        /// <summary>Whether the building has exactly one live group, with its one kinematic body, one enabled collider and one hull now.</summary>
        public static bool OneEach(BuildingHullFusion h, int building, out string why)
        {
            why = null;
            int groups = 0;
            foreach (HullGroup g in h.Groups)
            {
                if (g.Building != building || g.State == HullGroupState.Gone) continue;
                groups++;
                int enabled = 0;
                if (g.Root != null) foreach (Collider c in g.Root.GetComponentsInChildren<Collider>(true)) if (c.enabled) enabled++;
                if (g.Body == null || !g.Body.isKinematic || enabled != 1 || g.HullCount != 1 || g.Shape == null)
                    why = "group " + g.Id + ": body " + (g.Body != null) + " kinematic " + (g.Body != null && g.Body.isKinematic) + ", enabled colliders " + enabled + ", hull " + (g.Shape != null);
            }

            if (groups != 1) why = "building " + building + ": " + groups + " live groups";
            return why == null;
        }

        private void Fail(string what)
        {
            if (!_failures.Contains(what)) _failures.Add(what);
        }

        private void End(Result result, string detail, Action<string> log)
        {
            Outcome = result;
            Detail = detail;
            Completed = true;
            log?.Invoke("hull mid-drop section: " + Describe());
        }

        /// <param name="building">The building to cut (-1: none found, the section ends with no target).</param>
        /// <param name="nextSlashId">A Slash id not used before.</param>
        /// <param name="external">Non-null when the external replay is seen to have begun (the section's condition broken).</param>
        /// <param name="onFrame">Called once each frame of the section, its first included (the run's per-frame records).</param>
        /// <param name="afterEvaluate">Called right after each evaluation (the caller's reading of the detector's list).</param>
        /// <param name="clock">The clock the building trial's drops use (read for the records only).</param>
        public IEnumerator Run(CutWorldRoot world, SlashHitDetector detector, int building, Func<long> nextSlashId, Func<string> external, Action<int> onFrame,
            Action afterEvaluate, Func<IEnumerable<VpPreparedCharacterCut>> characters, Func<CutOperationId, bool> failureRecorded, Action<string> log, Func<double> clock)
        {
            Started = true;
            Outcome = Result.Running;
            Building = building;
            BuildingHullFusion h = world != null ? world.Hulls : null;
            log?.Invoke("hull mid-drop section: begins at frame " + Time.frameCount + " (required " + Required + ": " + RequiredBecause + "), building " + building);
            if (h == null || building < 0) { End(Result.NoTarget, h == null ? "the world has no building hull trial" : "no registered building of the hull trial was found", log); yield break; }

            // Each frame: the run's records, the one body, collider and hull, the world's end, the external replay.
            string Tick()
            {
                int frame = Time.frameCount;
                if (frame == LastFrame) return null;
                if (FirstFrame < 0) FirstFrame = frame;
                LastFrame = frame;
                Frames++;
                if (world.IsEnding || world.Hulls == null) return "interrupted: the world ended at frame " + frame;
                onFrame?.Invoke(frame);
                if (!OneEach(h, building, out string why)) { FramesNotOneEach++; _notOneEachWhy ??= "frame " + frame + ": " + why; }
                string seen = external?.Invoke();
                return seen != null ? "external: " + seen + " at frame " + frame : null;
            }

            Result Broken(string what) => what.StartsWith("external", StringComparison.Ordinal) ? Result.ExternalOverlap : Result.Interrupted;

            string stop = Tick();
            if (stop != null) { End(Broken(stop), stop, log); yield break; }

            float by = Time.realtimeSinceStartup + settleSeconds;
            while (!h.IsSettled)
            {
                if (Time.realtimeSinceStartup >= by) { End(Result.TimedOut, "the building trial not settled within " + settleSeconds.ToString("R") + " s: " + h.DescribeUnsettled(), log); yield break; }
                yield return null;
                stop = Tick();
                if (stop != null) { End(Broken(stop), stop, log); yield break; }
            }

            HullGroup group = null;
            foreach (HullGroup g in h.Groups)
                if (g.Building == building && g.State == HullGroupState.Idle && g.Root != null && g.Shape != null && (group == null || g.Mass > group.Mass)) group = g;
            if (group == null) { End(Result.NoTarget, "no idle group of building " + building + " with its hull", log); yield break; }
            if (h.IsCutStopped(building)) { End(Result.NoTarget, "building " + building + " is already cut no more (its limit reached)", log); yield break; }
            GeometriesAtStart = h.CountDisplayGeometries(building);

            group.Shape.ConvexBounds(0, out float3 glo, out float3 ghi);
            float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
            float4x4 toWorld = (float4x4)group.Root.transform.localToWorldMatrix;
            for (int k = 0; k < 8; k++)
            {
                float3 corner = math.transform(toWorld, new float3((k & 1) != 0 ? ghi.x : glo.x, (k & 2) != 0 ? ghi.y : glo.y, (k & 4) != 0 ? ghi.z : glo.z));
                lo = math.min(lo, corner); hi = math.max(hi, corner);
            }

            float3 c = (lo + hi) * 0.5f;
            int hullHitsBefore = h.Hits.Count;
            int startedBefore = h.AnimationsStarted, completedBefore = h.AnimationsCompleted, stoppedBefore = h.AnimationsStoppedByReCut;

            // The first: level, through the hull's centre.
            FirstSlash = nextSlashId();
            var level = new SlashSweep(FirstSlash, Time.unscaledTimeAsDouble, false, new Plane(Vector3.up, -c.y), Vector3.right, Vector3.forward,
                new Vector3(lo.x - 1f, c.y, lo.z - 1f), new Vector3(lo.x - 1f, c.y, hi.z + 1f), new Vector3(hi.x + 1f, c.y, lo.z - 1f), new Vector3(hi.x + 1f, c.y, hi.z + 1f));
            MobPlanSlashAftermath firstAfter = Evaluate(world, detector, level, afterEvaluate, characters, failureRecorded, hullHitsBefore, out string firstHits);
            FirstHit = HitOf(h, hullHitsBefore, FirstSlash, building);
            log?.Invoke("hull mid-drop section: Slash " + FirstSlash + " (level y " + c.y.ToString("F3") + " through group " + group.Id + ", n " + GeometriesAtStart + ") given at frame " + Time.frameCount + ": " + firstHits
                + "; its building hit record " + (FirstHit != null ? FirstHit.id + (FirstHit.held ? " (held)" : "") : "NONE"));
            if (FirstHit == null) { End(Result.Failed, "the first Slash " + FirstSlash + " made no hit record on building " + building, log); yield break; }

            // Its publication, then its drop seen part way (TL, 2026-10-01): the drop's applied phase u = t / T with 0 < u < 1, it
            // still running, and a member of it read back away from its start (a real move, not a registration). Until then no
            // second Slash; no progress by the deadline fails.
            by = Time.realtimeSinceStartup + publishSeconds;
            bool publishedSeen = false;
            while (true)
            {
                if (!FirstHit.IsPending && FirstHit.outcome != "Published") { End(Result.Failed, "the first Slash's cut was not published: " + FirstHit.outcome, log); yield break; }
                if (!FirstHit.IsPending && !publishedSeen)
                {
                    publishedSeen = true;
                    Event("the first cut published (seen)", h, clock, log, " (its publishedAt " + FirstHit.publishedAt.ToString("R") + ", display operations " + FirstHit.displayOperations.Count + ", drops started " + (h.AnimationsStarted - startedBefore) + ")");
                }

                if (publishedSeen)
                {
                    FirstDrop = DropOf(h, FirstHit.id);
                    FirstProgress = FirstDrop != null ? FirstDrop.Phase : double.NaN;
                    FirstMoved = FirstDrop != null ? Moved(FirstDrop) : 0.0;
                    bool ended = (FirstDrop != null && FirstDrop.end != null) || h.AnimationsCompleted != completedBefore || h.AnimationsStoppedByReCut != stoppedBefore;
                    if (ended) { End(Result.Failed, "the first drop ended before it was seen part way (u " + FirstProgress.ToString("R") + ", " + (FirstDrop != null ? FirstDrop.end : "no drop") + ")", log); yield break; }
                    if (FirstProgress > 0.0 && FirstProgress < 1.0 && FirstMoved > 0.0)
                    {
                        Event("the first drop seen part way", h, clock, log, " (drop " + FirstDrop.id + ", applied t " + FirstDrop.appliedSeconds.ToString("R") + " s of T " + FirstDrop.seconds.ToString("R") + ", u " + FirstProgress.ToString("R")
                            + ", a member read back " + FirstMoved.ToString("R") + " m from its start)");
                        break;
                    }
                }

                if (Time.realtimeSinceStartup >= by)
                {
                    End(Result.TimedOut, "the first Slash's cut not seen published and moving part way within " + publishSeconds.ToString("R") + " s (outcome " + (FirstHit.outcome ?? "pending") + ", u " + FirstProgress.ToString("R") + ", moved " + FirstMoved.ToString("R") + " m"
                        + ", drops started " + (h.AnimationsStarted - startedBefore) + "): no second Slash given", log);
                    yield break;
                }

                yield return null;
                stop = Tick();
                if (stop != null) { End(Broken(stop), stop, log); yield break; }
            }

            // The second: upright through the hull's centre, while that drop runs (moved, not completed, not stopped).
            FirstDropRunningAtSecond = h.AnimationsStarted > startedBefore && h.AnimationsRunning > 0 && h.AnimationsCompleted == completedBefore && h.AnimationsStoppedByReCut == stoppedBefore && FirstProgress > 0.0 && FirstProgress < 1.0 && FirstMoved > 0.0;
            int givenAt = Time.frameCount;
            SecondSlash = nextSlashId();
            var upright = new SlashSweep(SecondSlash, Time.unscaledTimeAsDouble, false, new Plane(Vector3.right, -c.x), Vector3.down, Vector3.forward,
                new Vector3(c.x, hi.y + 1f, lo.z - 1f), new Vector3(c.x, hi.y + 1f, hi.z + 1f), new Vector3(c.x, lo.y - 1f, lo.z - 1f), new Vector3(c.x, lo.y - 1f, hi.z + 1f));
            MobPlanSlashAftermath secondAfter = Evaluate(world, detector, upright, afterEvaluate, characters, failureRecorded, hullHitsBefore, out string secondHits);
            SecondHit = HitOf(h, hullHitsBefore, SecondSlash, building);
            log?.Invoke("hull mid-drop section: Slash " + SecondSlash + " (upright x " + c.x.ToString("F3") + ") given at frame " + givenAt + " with the first drop running " + FirstDropRunningAtSecond + " (drops running " + h.AnimationsRunning + "): " + secondHits
                + "; its building hit record " + (SecondHit != null ? SecondHit.id + (SecondHit.held ? " (held)" : "") : "NONE"));
            if (SecondHit == null) { End(Result.Failed, "the second Slash " + SecondSlash + " made no hit record on building " + building, log); yield break; }
            Event("the second Slash accepted " + (SecondHit.held ? "held" : "pending"), h, clock, log, "");

            // Both to their end, a held one included, and everything else each Slash set going (the ordinary completion wait).
            by = Time.realtimeSinceStartup + endSeconds;
            bool resumedSeen = false, stopSeen = false;
            while (true)
            {
                if (!resumedSeen && SecondHit.prepareFirstFrame >= 0) { resumedSeen = true; Event("the second's preparation seen begun (its first unit at frame " + SecondHit.prepareFirstFrame + ")", h, clock, log, ""); }
                if (!stopSeen && h.AnimationsStoppedByReCut != stoppedBefore) { stopSeen = true; Event("the first drop seen stopped", h, clock, log, " (at " + h.LastStopFraction.ToString("R") + " of its way)"); }
                bool first = firstAfter.Poll(), second = secondAfter.Poll();
                if (first && second && !FirstHit.IsPending && !SecondHit.IsPending && h.IsSettled) break;
                if (Time.realtimeSinceStartup >= by)
                {
                    End(Result.TimedOut, "not ended within " + endSeconds.ToString("R") + " s: first " + (FirstHit.outcome ?? "pending") + ", second " + (SecondHit.outcome ?? "pending") + "; " + h.DescribeUnsettled()
                        + "; the first Slash's other work: " + firstAfter.Describe() + "; the second's: " + secondAfter.Describe(), log);
                    yield break;
                }

                yield return null;
                stop = Tick();
                if (stop != null) { End(Broken(stop), stop, log); yield break; }
            }

            // The judgements, each by these two hits.
            int stopped = h.AnimationsStoppedByReCut - stoppedBefore;
            // The stop of this hit's own drop: its record ended "stopped" at its applied phase u (0 < u < 1).
            StopFraction = FirstDrop != null && FirstDrop.end == "stopped" ? FirstDrop.Phase : double.NaN;
            if (FirstDrop == null || FirstDrop.end != "stopped") Fail("the first hit's drop " + (FirstDrop != null ? FirstDrop.id + " ended " + (FirstDrop.end ?? "not") : "was not found") + ", not stopped by the re-cut");
            if (!FirstDropRunningAtSecond) Fail("the first drop was not running when the second Slash was given");
            if (stopped != 1 || !(StopFraction > 0.0 && StopFraction < 1.0)) Fail("the first drop was not stopped part way by the re-cut (" + stopped + " stopped, at " + StopFraction.ToString("R") + ")");
            foreach ((string name, BuildingHullFusion.HullHit hit) in new[] { ("first", FirstHit), ("second", SecondHit) })
            {
                if (hit.outcome != "Published") { Fail("the " + name + " cut was not published (" + hit.outcome + ")"); continue; }
                if (hit.displayOperations.Count == 0) Fail("the " + name + " cut has no display operation");
                foreach (CutOperationId op in hit.displayOperations)
                {
                    bool done = world.Ledger.TryGetOperation(op, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Completed && world.Geometry.StageOf(op) == CutGeometryStage.Committed;
                    if (!done) Fail("the " + name + " cut's display operation " + op.value + " not completed and committed");
                }
            }

            for (int i = hullHitsBefore; i < h.Hits.Count; i++)
            {
                BuildingHullFusion.HullHit other = h.Hits[i];
                if (ReferenceEquals(other, FirstHit) || ReferenceEquals(other, SecondHit) || BuildingOf(h, other.group) != building) continue;
                Fail("another hit on the building meanwhile: " + other.id + " slash " + other.slashId);
            }

            if (FramesNotOneEach > 0) Fail("not one body, one enabled collider and one hull at " + FramesNotOneEach + " frames (" + _notOneEachWhy + ")");
            if (!firstAfter.Passed) Fail("the first Slash's other work: " + firstAfter.Describe());
            if (!secondAfter.Passed) Fail("the second Slash's other work: " + secondAfter.Describe());
            GeometriesAtEnd = h.CountDisplayGeometries(building);
            End(_failures.Count == 0 ? Result.Passed : Result.Failed, string.Join("; ", _failures), log);
        }

        private static MobPlanSlashAftermath Evaluate(CutWorldRoot world, SlashHitDetector detector, SlashSweep sweep, Action afterEvaluate, Func<IEnumerable<VpPreparedCharacterCut>> characters,
            Func<CutOperationId, bool> failureRecorded, int hullHitsBefore, out string described)
        {
            int opsBefore = world.Ledger.OperationCount;
            detector.Evaluate(new[] { sweep }, new[] { sweep.SlashId });
            var hits = new List<SlashHitConfirmed>();
            var parts = new List<string>();
            for (int i = 0; i < detector.HitCount; i++)
            {
                SlashHitConfirmed hit = detector.HitAt(i);
                hits.Add(hit);
                parts.Add("fragment " + hit.Fragment.value + " " + hit.Acceptance);
            }

            afterEvaluate?.Invoke();
            described = "detector hits " + hits.Count + (parts.Count > 0 ? " [" + string.Join(", ", parts) + "]" : "");
            return new MobPlanSlashAftermath(world, sweep.SlashId, hits, characters != null ? characters() : Array.Empty<VpPreparedCharacterCut>(), opsBefore, hullHitsBefore, failureRecorded);
        }

        /// <summary>The drop the building trial started for this hit, or null.</summary>
        private static BuildingHullFusion.DropRecord DropOf(BuildingHullFusion h, int hit)
        {
            foreach (BuildingHullFusion.DropRecord r in h.DropRecords) if (r.hit == hit) return r;
            return null;
        }

        /// <summary>The farthest any member of the drop is read back from its start (world metres).</summary>
        private static double Moved(BuildingHullFusion.DropRecord r)
        {
            double most = 0.0;
            foreach (BuildingHullFusion.DropMember m in r.members)
            {
                if (m.root == null) continue;
                Transform parent = m.root.parent;
                Vector3 local = m.root.localPosition - m.from;
                most = Math.Max(most, (parent != null ? parent.TransformVector(local) : local).magnitude);
            }

            return most;
        }

        private void Event(string what, BuildingHullFusion h, Func<double> clock, Action<string> log, string more)
        {
            string line = what + " at frame " + Time.frameCount + ", Step " + (CutPhysicsStep.Clock != null ? CutPhysicsStep.Clock.StepId : -1) + ", clock " + (clock != null ? clock().ToString("R") : "none") + more;
            _events.Add(line);
            log?.Invoke("hull mid-drop section: " + line);
        }

        public IReadOnlyList<string> Events => _events;
        private readonly List<string> _events = new List<string>();

        /// <summary>The first drop's applied phase u = t / T when the second Slash was given, its record, and how far a member of it was read back from its start (m).</summary>
        public double FirstProgress { get; private set; } = double.NaN;
        public BuildingHullFusion.DropRecord FirstDrop { get; private set; }
        public double FirstMoved { get; private set; }

        private static int BuildingOf(BuildingHullFusion h, int group)
        {
            foreach (HullGroup g in h.Groups) if (g.Id == group) return g.Building;
            return -1;
        }

        private static BuildingHullFusion.HullHit HitOf(BuildingHullFusion h, int from, long slash, int building)
        {
            for (int i = from; i < h.Hits.Count; i++) if (h.Hits[i].slashId == slash && BuildingOf(h, h.Hits[i].group) == building) return h.Hits[i];
            return null;
        }

        /// <summary>The section's outcome, judged once at the run's end: a required one passes only when it ran to its end and passed.</summary>
        public void Judge(Action<bool, string> expect, Action<string> log)
        {
            log?.Invoke("hull mid-drop section at the end: " + Describe());
            if (!Required) return;
            string state = Outcome == Result.Running ? "Interrupted (started, never ended)" : Outcome == Result.NotStarted ? "NotStarted (never run)" : Outcome.ToString();
            expect(Outcome == Result.Passed, "[hull mid-drop] the required re-cut during a drop, before the script, ran to its end and passed (" + state + (Detail.Length > 0 ? ": " + Detail : "") + ")");
        }

        public string Describe()
        {
            string Hit(BuildingHullFusion.HullHit hit) => hit == null ? "none" : "hit " + hit.id + " slash " + hit.slashId + (hit.held ? " held" : "") + " => " + (hit.outcome ?? "PENDING") + " (display operations " + hit.displayOperations.Count + ", n before " + hit.geometriesBefore + ", after " + hit.geometriesAfter + ", distance " + hit.limitDistance.ToString("R") + ")";
            return Outcome + (Detail.Length > 0 ? " (" + Detail + ")" : "") + "; required " + Required + " (" + RequiredBecause + "); started " + Started + ", completed " + Completed + "; building " + Building + "; first " + Hit(FirstHit) + "; second " + Hit(SecondHit)
                + "; first drop running at the second " + FirstDropRunningAtSecond + " (progress " + FirstProgress.ToString("R") + "), stopped at " + StopFraction.ToString("R") + "; n " + GeometriesAtStart + " -> " + GeometriesAtEnd + "; frames " + Frames + " (" + FirstFrame + ".." + LastFrame + "), not one each " + FramesNotOneEach;
        }
    }
}
