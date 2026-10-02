using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// CHECK ONLY (the reused-slot check, TL 2026-10-01): everything one synthetic Slash's one evaluation set going, followed
    /// to its end apart from the run's own records -- every hit of that evaluation, the picked individual's and any other
    /// (another NPC, a prop, a building). Two things are told apart: whether it has <b>finished</b> (nothing left to wait
    /// for) and whether every result was <b>acceptable</b>. An anomaly is recorded as soon as it is certain, and the rest is
    /// still waited for.
    /// <list type="bullet">
    /// <item>A hit's immediate answer: Published, Pending and Held are followed to their operation (a Pending or Held one's
    /// later operation included); EmptySide, NotAccepted and AnchorsRefused are ordinary refusals -- not anomalies, but an
    /// operation one left is still followed to its end (AnchorsRefused leaves its cut the source's active operation, which
    /// only the ending closes: never an early end); InvalidRequest, Aborted and Stale are anomalies (as the run's own hit
    /// check holds them), and an operation they left is still followed to its end.</item>
    /// <item>An operation's end: Completed with its geometry committed is acceptable; its geometry reclaimed (aborted, or a
    /// building's display operation) with the driver done with it is an allowed failure only when a failure record names
    /// it (the caller's records: the driver's failures and the geometry's own), and waits for that record until the
    /// deadline; ended any other way (Terminated, Stale, gone from the ledger) is an anomaly.</item>
    /// <item>A building hit: its record answered -- Published (its display operations each ended as above), or an ordinary
    /// refusal (dropped, not accepted, empty side, no change); Abandoned or anything else is an anomaly -- and the building
    /// trial with no display operation, drop, hull update or hull request left, no cut in progress, settled. A hull update
    /// refused with the old hull kept is an end, as always.</item>
    /// <item>The world ending or released during the wait, or the building trial gone with a building hit to follow, finishes
    /// the wait as an anomaly: never a normal end.</item>
    /// </list>
    /// Passed: finished, every result acceptable, not timed out, the world not ended. Nothing is cut, published, retired or
    /// moved from here.
    /// </summary>
    public sealed class MobPlanSlashAftermath
    {
        private enum Kind { Refused, Abnormal, Character, Hull, Fragment }

        private enum End { Waiting, Ok, AllowedFailure, Abnormal }

        private sealed class Entry
        {
            public SlashHitConfirmed hit;
            public Kind kind;
            public VpPreparedCharacterCut handle;
            public CutOperationId operation;
            public string state = "not read yet";
            public End end;
            public string anomaly;
        }

        private readonly CutWorldRoot _world;
        private readonly long _slashId;
        private readonly int _operationsBefore, _hullHitsBefore;
        private readonly bool _hullAtStart;
        private readonly Func<CutOperationId, bool> _failureRecorded;
        private readonly List<Entry> _entries = new List<Entry>();
        private readonly List<string> _anomalies = new List<string>();
        private readonly Dictionary<int, string> _hullStates = new Dictionary<int, string>();
        // This Slash's building hit records, held from the start: read whatever becomes of the world (an abandoned one included).
        private readonly List<BuildingHullFusion.HullHit> _hullHits = new List<BuildingHullFusion.HullHit>();

        /// <param name="hits">The evaluation's hits, read right after it.</param>
        /// <param name="characters">The prepared cuts that could have been hit (a character hit is matched by its fragment).</param>
        /// <param name="operationsBefore">The ledger's operations before the evaluation: a later operation of a fragment is one admitted after it.</param>
        /// <param name="hullHitsBefore">The building trial's hit records before the evaluation.</param>
        /// <param name="failureRecorded">Whether a failure record names an operation (whatever frame it came in), or null (none does).</param>
        public MobPlanSlashAftermath(CutWorldRoot world, long slashId, IReadOnlyList<SlashHitConfirmed> hits, IEnumerable<VpPreparedCharacterCut> characters,
            int operationsBefore, int hullHitsBefore, Func<CutOperationId, bool> failureRecorded)
        {
            _world = world;
            _slashId = slashId;
            _operationsBefore = operationsBefore;
            _hullHitsBefore = hullHitsBefore;
            _hullAtStart = world.Hulls != null;
            _failureRecorded = failureRecorded;
            if (world.Hulls != null)
            {
                IReadOnlyList<BuildingHullFusion.HullHit> records = world.Hulls.Hits;
                for (int i = hullHitsBefore; i < records.Count; i++) if (records[i].slashId == slashId) _hullHits.Add(records[i]);
            }

            var handles = new List<VpPreparedCharacterCut>();
            foreach (VpPreparedCharacterCut c in characters) if (c != null) handles.Add(c);
            foreach (SlashHitConfirmed hit in hits)
            {
                if (hit.SlashId != slashId) continue;
                var e = new Entry { hit = hit, operation = hit.Operation };
                switch (hit.Acceptance)
                {
                    case ProvisionalCutAcceptance.Published:
                    case ProvisionalCutAcceptance.Pending:
                    case ProvisionalCutAcceptance.Held:
                    {
                        VpPreparedCharacterCut handle = handles.Find(c => c.Source.IsSet && c.Source == hit.Fragment);
                        if (handle != null) { e.kind = Kind.Character; e.handle = handle; }
                        else if (world.Hulls != null && !hit.Operation.IsSet && hit.Admission == LogicalCutAdmission.NoOp && _hullHits.Count > 0) e.kind = Kind.Hull;
                        else e.kind = Kind.Fragment;
                        break;
                    }

                    case ProvisionalCutAcceptance.EmptySide:
                    case ProvisionalCutAcceptance.NotAccepted:
                    case ProvisionalCutAcceptance.AnchorsRefused:
                        e.kind = Kind.Refused;
                        break;
                    default:
                        e.kind = Kind.Abnormal;
                        e.anomaly = "an abnormal immediate answer " + hit.Acceptance + " (fragment " + hit.Fragment.value + ")";
                        _anomalies.Add(e.anomaly);
                        break;
                }

                _entries.Add(e);
            }
        }

        public int HitCount => _entries.Count;

        /// <summary>Nothing is left to wait for (every result ended, the building quiet), or the world ended.</summary>
        public bool Finished { get; private set; }

        /// <summary>No anomaly so far: every ended result is a commit, an ordinary refusal or a failure a record names.</summary>
        public bool Acceptable => _anomalies.Count == 0;

        public bool TimedOut { get; private set; }
        public bool WorldEnded { get; private set; }
        public bool Passed => Finished && Acceptable && !TimedOut && !WorldEnded;
        public int AllowedFailures { get; private set; }
        public double WaitedSeconds { get; private set; }
        public IReadOnlyList<string> Anomalies => _anomalies;

        private void Anomaly(string what)
        {
            if (!_anomalies.Contains(what)) _anomalies.Add(what);
        }

        // An operation's end, as above; display: a building's display operation.
        private End Judge(CutOperationId op, bool display, out string state)
        {
            if (!_world.Ledger.TryGetOperation(op, out LogicalCutOperation record)) { state = "operation " + op.value + " not in the ledger"; return End.Abnormal; }
            CutGeometryStage stage = _world.Geometry.StageOf(op);
            state = "operation " + op.value + " " + record.state + " / " + stage;
            if (record.state == LogicalCutOperationState.Completed && stage == CutGeometryStage.Committed) return End.Ok;
            if (stage == CutGeometryStage.Reclaimed && (record.state == LogicalCutOperationState.Aborted || display) && _world.Driver.IsSettled(op))
            {
                if (_failureRecorded != null && _failureRecorded(op)) { state += ", a failure a record names"; return End.AllowedFailure; }
                state += ", no failure record names it (yet)";
                return End.Waiting;
            }

            if (record.state == LogicalCutOperationState.Terminated || record.state == LogicalCutOperationState.Stale) return End.Abnormal;
            return End.Waiting;
        }

        private static bool OrdinaryHullOutcome(string outcome) =>
            outcome.StartsWith("Dropped", StringComparison.Ordinal) || outcome.StartsWith("NotAccepted", StringComparison.Ordinal)
            || outcome.StartsWith("EmptySide", StringComparison.Ordinal) || outcome.StartsWith("NoChange", StringComparison.Ordinal);

        /// <summary>Reads where everything stands now; true when finished (see <see cref="Finished"/>).</summary>
        public bool Poll()
        {
            BuildingHullFusion h = _world != null ? _world.Hulls : null;

            // A building hit abandoned is never an end, whatever else stands (its record is held, read even after the world's end).
            foreach (BuildingHullFusion.HullHit r in _hullHits)
            {
                if (r.outcome != null && r.outcome.StartsWith("Abandoned", StringComparison.Ordinal)) { Anomaly("building hit " + r.id + " " + r.outcome); _hullStates[r.id] = r.outcome; }
            }

            if (_world == null || _world.IsEnding || _world.IsReleased)
            {
                WorldEnded = true;
                Anomaly("the world ended during the wait (nothing of it is an end)");
                Finished = true;
                return true;
            }

            bool waiting = false;
            AllowedFailures = 0;
            foreach (Entry e in _entries)
            {
                switch (e.kind)
                {
                    case Kind.Hull:
                        e.end = End.Ok;   // its record is followed below, with the building
                        e.state = "a building hit (followed by its record)";
                        continue;
                }

                if (!e.operation.IsSet || e.operation.value <= _operationsBefore)
                {
                    CutOperationId later = default;
                    if (e.kind == Kind.Character && e.handle != null && e.handle.Operation.IsSet) later = e.handle.Operation;
                    else if (_world.Ledger.TryGetReplacingOperation(e.hit.Fragment, out CutOperationId replacing)) later = replacing;
                    else if (_world.Ledger.TryGetActiveOperation(e.hit.Fragment, out CutOperationId active)) later = active;
                    if (later.IsSet && later.value > _operationsBefore) e.operation = later;
                }

                if (!e.operation.IsSet || e.operation.value <= _operationsBefore)
                {
                    if (e.kind == Kind.Refused)
                    {
                        // An ordinary refusal that left no operation: its end.
                        e.end = End.Ok;
                        e.state = "an ordinary refusal (" + e.hit.Acceptance + "), no operation left";
                        continue;
                    }

                    if (e.kind == Kind.Abnormal)
                    {
                        // An abnormal answer that left no operation: nothing more to follow.
                        e.end = End.Abnormal;
                        e.state = e.hit.Acceptance + ": no operation left";
                        continue;
                    }

                    e.end = End.Waiting;
                    e.state = e.hit.Acceptance + ": no operation of its own yet";
                    waiting = true;
                    continue;
                }

                End end = Judge(e.operation, false, out e.state);
                if (e.kind == Kind.Refused) e.state = "an ordinary refusal (" + e.hit.Acceptance + ") that left " + e.state;
                if (end == End.Waiting) { e.end = End.Waiting; waiting = true; continue; }
                if (end == End.Abnormal) Anomaly("fragment " + e.hit.Fragment.value + ": " + e.state);
                if (end == End.AllowedFailure) AllowedFailures++;
                e.end = e.kind == Kind.Abnormal ? End.Abnormal : end;
            }

            bool hullHits = false;
            foreach (Entry e in _entries) hullHits |= e.kind == Kind.Hull;
            if (h == null)
            {
                if (_hullAtStart && hullHits) { Anomaly("the building trial is gone with a building hit to follow"); Finished = true; return true; }
            }
            else
            {
                foreach (BuildingHullFusion.HullHit r in _hullHits)
                {
                    if (r.IsPending) { waiting = true; _hullStates[r.id] = "pending"; continue; }
                    if (r.outcome.StartsWith("Abandoned", StringComparison.Ordinal)) { _hullStates[r.id] = r.outcome; continue; }
                    if (r.outcome != "Published")
                    {
                        _hullStates[r.id] = r.outcome + (OrdinaryHullOutcome(r.outcome) ? " (an ordinary refusal)" : "");
                        if (!OrdinaryHullOutcome(r.outcome)) Anomaly("building hit " + r.id + " " + r.outcome);
                        continue;
                    }

                    int ended = 0, failures = 0;
                    foreach (CutOperationId op in r.displayOperations)
                    {
                        End end = Judge(op, true, out string state);
                        if (end == End.Waiting) { waiting = true; continue; }
                        if (end == End.Abnormal) Anomaly("building hit " + r.id + " display " + state);
                        if (end == End.AllowedFailure) failures++;
                        ended++;
                    }

                    AllowedFailures += failures;
                    _hullStates[r.id] = "Published, display operations " + r.displayOperations.Count + " (ended " + ended + ", allowed failures " + failures + ")";
                }

                waiting |= !(h.DisplayOperationsOpen == 0 && h.AnimationsRunning == 0 && h.HullUpdatesInFlight == 0 && h.WaitingHullRequests == 0 && h.CutsInProgress == 0 && h.IsSettled);
            }

            Finished = !waiting;
            return Finished;
        }

        /// <summary>
        /// Polls once a frame (the frames go on as ever) until finished or <paramref name="seconds"/> of real time from the
        /// call: never extended. An anomaly does not end the wait; the world's end does.
        /// </summary>
        public IEnumerator Wait(float seconds)
        {
            float begin = Time.realtimeSinceStartup;
            float until = begin + seconds;
            while (!Poll())
            {
                if (Time.realtimeSinceStartup >= until)
                {
                    // What was left, named: a wait that ran out is never an acceptable end.
                    TimedOut = true;
                    foreach (Entry e in _entries) if (e.end == End.Waiting) Anomaly("timed out: fragment " + e.hit.Fragment.value + " " + e.state);
                    foreach (KeyValuePair<int, string> r in _hullStates) if (r.Value == "pending") Anomaly("timed out: building hit " + r.Key + " pending");
                    Anomaly("timed out after " + seconds.ToString("F2") + " s with work left");
                    break;
                }

                yield return null;
            }

            WaitedSeconds = Time.realtimeSinceStartup - begin;
        }

        /// <summary>Every hit, its operation and its state; the building's records and what it still has running or waiting; the anomalies.</summary>
        public string Describe()
        {
            var s = new StringBuilder();
            s.Append("Slash ").Append(_slashId).Append(": hits ").Append(_entries.Count).Append(", ").Append(Finished ? "finished" : "NOT finished")
                .Append(Acceptable ? ", every result acceptable" : ", NOT acceptable").Append(TimedOut ? ", TIMED OUT" : "").Append(WorldEnded ? ", THE WORLD ENDED" : "")
                .Append(", allowed failures ").Append(AllowedFailures);
            foreach (Entry e in _entries)
            {
                s.Append("; [").Append(e.kind).Append(" fragment ").Append(e.hit.Fragment.value).Append(" ").Append(e.hit.Acceptance).Append("/").Append(e.hit.Admission)
                    .Append(" -> ").Append(e.state).Append(", ").Append(e.end).Append("]");
            }

            foreach (KeyValuePair<int, string> r in _hullStates) s.Append("; [building hit ").Append(r.Key).Append(": ").Append(r.Value).Append("]");
            BuildingHullFusion h = _world != null && !_world.IsReleased ? _world.Hulls : null;
            if (h != null)
            {
                s.Append("; building: display operations open ").Append(h.DisplayOperationsOpen).Append(", drops running ").Append(h.AnimationsRunning).Append(", hull updates running ").Append(h.HullUpdatesInFlight)
                    .Append(", hull requests waiting ").Append(h.WaitingHullRequests).Append(", cuts in progress ").Append(h.CutsInProgress).Append(", settled ").Append(h.IsSettled)
                    .Append(" (hull updates exchanged ").Append(h.HullUpdatesAdopted).Append(", refused ").Append(h.HullUpdatesRefused).Append(")");
            }

            if (_anomalies.Count > 0) s.Append("; anomalies: ").Append(string.Join("; ", _anomalies));
            return s.ToString();
        }
    }
}
