#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// SPLIT-CANDIDATES-1 (2026-11-13; development only, off without its launch argument): how many fragments the additional split
    /// could issue an attempt for in one frame, beside the one attempt it has out -- a count for judging whether issuing several at
    /// once is worth looking at. Observation, nothing more: every frame (one sample a world and frame), after the turn knows it
    /// is on and before anything else of the turn (an attempt out, a publication waiting, the idle wait, a hold), every fragment
    /// with an owner is looked at as the walk looks at it (<see cref="Examine(LogicalFragmentId)"/>, which reads only) and the
    /// candidates are counted, the fragment of the attempt out left aside. The walk's place, its marks, its waits and its holds are
    /// the turn's own and are not touched; the flag the look leaves (<c>_passedForDisplayState</c>) is put back. The sample also
    /// says what the turn was at: the attempt's state, the frame's holds as they stand at the look, what the turn's own budget
    /// decision came to in that frame (not evaluated where the turn made none), and the Background pool's queue.
    /// </summary>
    public sealed partial class CutSpatialSplit
    {
        public const string CandidateProbeArgument = "-zantetsuSplitCandidateProbe";

        /// <summary>The CSV the samples go to at the world's end (a second world's gets its number before the extension); null: no probe.</summary>
        public static string CandidateProbePath
        {
            get
            {
                if (!s_candidateProbeRead)
                {
                    s_candidateProbeRead = true;
                    string[] arguments = Environment.GetCommandLineArgs();
                    int at = Array.FindIndex(arguments, a => string.Equals(a, CandidateProbeArgument, StringComparison.OrdinalIgnoreCase));
                    if (at >= 0 && at + 1 < arguments.Length && Path.IsPathRooted(arguments[at + 1])) s_candidateProbePath = arguments[at + 1];
                    else if (at >= 0) UnityEngine.Debug.LogError("SPLIT CANDIDATE PROBE: " + CandidateProbeArgument + " needs an absolute file path; nothing is sampled");
                    if (s_candidateProbePath != null) UnityEngine.Debug.Log("SPLIT CANDIDATE PROBE: on, every " + CandidateProbeInterval + " frames, into " + s_candidateProbePath);
                }

                return s_candidateProbePath;
            }
            set
            {
                s_candidateProbeRead = true;
                s_candidateProbePath = value;
            }
        }

        public const int CandidateProbeInterval = 1;   // every frame (the instruction as last given, 2026-11-13: overlaps of a few frames are wanted too)
        private static string s_candidateProbePath;
        private static bool s_candidateProbeRead;
        private static int s_candidateProbeWorlds;

        /// <summary>One sample.</summary>
        public struct CandidateSample
        {
            public int frame;
            public double seconds;
            /// <summary>Fragments with an owner looked at, and of them the candidates (the attempt's own fragment left aside: <see cref="attemptFragmentSeen"/> says it was among them).</summary>
            public int owners, candidates;
            public bool attemptFragmentSeen;
            /// <summary>Of those passed over: marked not-again, waiting (deferred), at a search limit, for what the display shows or has room for now.</summary>
            public int suppressed, deferred, limitReached, displayState;
            public string attempt;
            public long attemptSerial;
            /// <summary>The frame's own hold as it stands at the look: None, HeavyWork, GarbageCollection, CutInFlight.</summary>
            public string frameHold;
            public bool structureChanged, displayRoomChanged, idle;
            /// <summary>The Main time left right before the scan (a reference; the turn's own decision is below).</summary>
            public double remainingBeforeMs;
            /// <summary>The turn's own evaluations in this frame, read after it: "not evaluated" where it made none.</summary>
            public string hold, budget, budgetStage;
            public double budgetRemainingMs;
            public int poolWaiting, poolSubmitted, poolHeld, poolCapacity;
            public bool poolCanAccept;
            public double scanMs;
            /// <summary>SPLIT-CANDIDATES-2: the attempts out (all of them left aside by the count), by where they are; what they hold; and the split's running totals.</summary>
            public int attemptsOut, attemptsOnWorker, attemptsDone, attemptsCollected;
            public long heldBytes;
            public int issuedTotal, endedTotal, publishedTotal;
            /// <summary>Whether the display's answers came from the one-pass view (else from the display, a fragment at a time).</summary>
            public bool usedView;
            /// <summary>
            /// The review of 2026-11-13: the scan's parts. Fragments that ended at the light look and those looked at in detail;
            /// candidates held for needing more bytes alone than the limit (not among <see cref="candidates"/>); whether the
            /// display's view was taken in this scan; the times of the light part, the detailed looks and the view (their sum is
            /// <see cref="scanMs"/>), of the probe's own record keeping after the scan, and of the light marks' check where one
            /// was made (with the fragments it found the detailed look to call candidates: never).
            /// </summary>
            public int lightExcluded, detailed, tooLarge;
            /// <summary>SPLIT-CANDIDATES-3: fragments passed over in this scan for a geometry known to be one topology component (light and detailed looks), and the inputs so resolved without an attempt so far.</summary>
            public int oneComponent;
            public long oneComponentInputs;
            public bool viewFilled;
            public double lightMs, detailMs, viewMs, recordMs, checkMs;
            public int checkDisagreed;
            /// <summary>
            /// The turn after the sample: the scans it made itself (none where the sample's own stood) and their time; what it
            /// issued; why it issued nothing or no more ("control" under the control as before, "not evaluated" where the turn
            /// did not come to its issues); the candidates left waiting and the places free for an attempt, at the turn's end.
            /// </summary>
            public int turnScans, issuedInTurn, foundLeft, freePlaces;
            public double turnScanMs;
            public string noIssue;
            internal long scansAfter;
            internal double scanSecondsAfter;
        }

        private readonly HashSet<LogicalFragmentId> _probeOut = new HashSet<LogicalFragmentId>();

        /// <summary>Every how many samples the light marks are checked against the detailed look (0: never). Its time is the check's own column.</summary>
        public static int CandidateProbeCheckEvery = 97;
        private int _issueEvaluatedFrame = -1;

        // The wait of a candidate: when each fragment was first seen a candidate for its present input (by the probe), and -- at its
        // issue -- how long ago that was.
        private struct FirstSeen
        {
            internal Input input;
            internal long ticks;
            internal int frame, lastFrame;
        }

        /// <summary>One issue as the probe saw it: the attempt, its fragment, the frames it was first seen a candidate and issued, and the wait (ms; -1: never seen a candidate before its issue).</summary>
        public struct CandidateWait
        {
            public long serial;
            public int fragment, seenFrame, issuedFrame;
            public double waitMs;
        }

        private readonly Dictionary<LogicalFragmentId, FirstSeen> _probeFirstSeen = new Dictionary<LogicalFragmentId, FirstSeen>();
        private readonly List<LogicalFragmentId> _probeGone = new List<LogicalFragmentId>();
        private readonly List<CandidateWait> _probeWaits = new List<CandidateWait>();
        public IReadOnlyList<CandidateWait> CandidateWaits => _probeWaits;

        private void NoteIssueForProbe(Attempt attempt)
        {
            if (CandidateProbePath == null) return;
            var w = new CandidateWait { serial = attempt.serial, fragment = attempt.fragment.value, issuedFrame = Time.frameCount, seenFrame = -1, waitMs = -1.0 };
            if (_probeFirstSeen.TryGetValue(attempt.fragment, out FirstSeen seen) && seen.input.Equals(attempt.input))
            {
                w.seenFrame = seen.frame;
                w.waitMs = (Stopwatch.GetTimestamp() - seen.ticks) * 1000.0 / Stopwatch.Frequency;
            }

            _probeFirstSeen.Remove(attempt.fragment);
            _probeWaits.Add(w);
        }
        private readonly List<CandidateSample> _probeSamples = new List<CandidateSample>();
        private int _probeFrame = -1, _probePendingAt = -1, _holdEvaluatedFrame = -1;
        private long _probeFirstTicks;

        /// <summary>The samples taken (tests, the summary).</summary>
        public IReadOnlyList<CandidateSample> CandidateSamples => _probeSamples;

        /// <summary>Tests: the walk's place and flags (the cursor, the round's length, the look's flag, idle), and the fragment of the attempt out.</summary>
        internal (int cursor, int round, bool passedForDisplayState, bool idle, bool roundWanting) WalkStateForTests => (_cursor, _round.Count, _passedForDisplayState, _idle, _roundWanting);

        internal bool TryGetAttemptFragmentForTests(out LogicalFragmentId fragment, out long serial)
        {
            Attempt first = FirstOut;
            fragment = first != null ? first.fragment : default;
            serial = first != null ? first.serial : 0;
            return first != null;
        }

        // ---- one attempt's own state and end (tests; SPLIT-CANDIDATES-2 review 2) ----------------------------------------------
        // With several attempts out at once "the attempt" and "the last end" are not one fragment's: a test asks about its own
        // fragment here.

        /// <summary>How one attempt ended: its fragment and number, the end, what its search and its graph stage said, whether it cut convexes.</summary>
        internal struct AttemptEndRecord
        {
            public LogicalFragmentId fragment;
            public long serial;
            public SpatialSplitEnd end;
            public SpatialSplitSearchResult search;
            public SpatialSplitGraphRecord graph;
            public bool cut;
            public int frame;
        }

        /// <summary>The attempt out for a fragment, as it stands now.</summary>
        internal struct AttemptView
        {
            public bool isOut, offered, collected, complete, cuts, applied, baking;
            public int hullsChecked, meshCount;
            public long serial;
        }

        private readonly List<AttemptEndRecord> _endRecords = new List<AttemptEndRecord>();

        private void NoteEndRecord(Attempt attempt, SpatialSplitEnd end)
        {
            if (_endRecords.Count >= 1024) _endRecords.RemoveRange(0, 512);
            _endRecords.Add(new AttemptEndRecord { fragment = attempt.fragment, serial = attempt.serial, end = end, search = attempt.search, graph = attempt.graph, cut = attempt.cut, frame = Time.frameCount });
        }

        /// <summary>The last end of an attempt of this fragment (false: none ended).</summary>
        internal bool TryGetLastEndOf(LogicalFragmentId fragment, out AttemptEndRecord record)
        {
            for (int i = _endRecords.Count - 1; i >= 0; i--)
            {
                if (_endRecords[i].fragment.Equals(fragment)) { record = _endRecords[i]; return true; }
            }

            record = default;
            return false;
        }

        /// <summary>How many attempts of this fragment ended so.</summary>
        internal int EndsOf(LogicalFragmentId fragment, SpatialSplitEnd end)
        {
            int n = 0;
            for (int i = 0; i < _endRecords.Count; i++) if (_endRecords[i].fragment.Equals(fragment) && _endRecords[i].end == end) n++;
            return n;
        }

        /// <summary>Whether this fragment's last attempt ended so.</summary>
        internal bool LastEndOfIs(LogicalFragmentId fragment, SpatialSplitEnd end) => TryGetLastEndOf(fragment, out AttemptEndRecord r) && r.end == end;

        /// <summary>Whether a mark (tried, not split) stands for this fragment.</summary>
        internal bool IsMarked(LogicalFragmentId fragment) => _suppressed.ContainsKey(fragment);

        internal AttemptView AttemptOf(LogicalFragmentId fragment)
        {
            Attempt a = _attempt != null && _attempt.fragment.Equals(fragment) ? _attempt : null;
            for (int i = 0; a == null && i < _attempts.Count; i++) if (_attempts[i].fragment.Equals(fragment)) a = _attempts[i];
            if (a == null) return default;
            return new AttemptView
            {
                isOut = true, offered = a.offered, collected = a.collected, complete = a.IsComplete, cuts = a.collected && a.cut, applied = a.applied,
                baking = a.stage == AttemptStage.Baking, hullsChecked = a.hullsChecked, meshCount = a.meshCount, serial = a.serial,
            };
        }

        // In Step, right after the turn knows it is on.
        private void ProbeCandidatesAtTurn(in CutSpatialSplitSettings settings)
        {
            if (CandidateProbePath == null) return;
            int frame = Time.frameCount;
            if (frame == _probeFrame || frame % CandidateProbeInterval != 0) return;
            _probeFrame = frame;
            _probeSamples.Add(TakeCandidateSample(in settings));
            _probePendingAt = _probeSamples.Count - 1;
        }

        /// <summary>
        /// One look at every owner's fragment now (tests call it directly; the turn calls it every frame): the candidates
        /// counted, nothing of the walk changed.
        /// </summary>
        internal CandidateSample TakeCandidateSample(in CutSpatialSplitSettings settings)
        {
            var s = new CandidateSample { frame = Time.frameCount, hold = "not evaluated", budget = "not evaluated", budgetStage = "" };
            long now = Stopwatch.GetTimestamp();
            if (_probeFirstTicks == 0) _probeFirstTicks = now;
            s.seconds = (now - _probeFirstTicks) / (double)Stopwatch.Frequency;
            Attempt attempt = FirstOut;   // the one in hand, or the first of the several out
            s.attempt = attempt == null ? "none"
                : !attempt.collected ? (attempt.stage == AttemptStage.Baking ? (attempt.IsComplete ? "bake-done" : attempt.offered ? "bake-out" : "bake-not-offered")
                    : attempt.IsComplete ? "numbers-done" : attempt.beganAt != 0 ? "numbers-running" : "numbers-waiting")
                : attempt.cut ? (!attempt.applied ? "collected-cut-apply" : attempt.hullsChecked < attempt.meshCount ? "collected-hull-check" : "collected-cut-publish")
                : "collected-publish";
            s.attemptSerial = attempt != null ? attempt.serial : 0;
            _probeOut.Clear();
            if (attempt != null) _probeOut.Add(attempt.fragment);
            for (int i = 0; i < _attempts.Count; i++)
            {
                Attempt a = _attempts[i];
                _probeOut.Add(a.fragment);
                if (a.collected) s.attemptsCollected++; else if (a.IsComplete) s.attemptsDone++; else s.attemptsOnWorker++;
            }

            if (attempt != null && !_attempts.Contains(attempt))
            {
                if (attempt.collected) s.attemptsCollected++; else if (attempt.IsComplete) s.attemptsDone++; else s.attemptsOnWorker++;
            }

            s.attemptsOut = _probeOut.Count;
            if (_attempts.Count > 0) s.attempt = "several";
            s.heldBytes = AttemptsHeldBytes + (attempt != null && !_attempts.Contains(attempt) ? attempt.HeldBytes : 0);
            s.issuedTotal = Issued;
            s.publishedTotal = Published;
            for (int i = 0; i < _ends.Length; i++) s.endedTotal += (int)_ends[i];
            s.frameHold = _world.FrameEventsHold(out _).ToString();
            s.structureChanged = _world.Ledger != null && _world.Ledger.Revision != _revisionSeen;
            s.displayRoomChanged = _world.DisplayRoomSerial() != _roomSeen;
            s.idle = _idle;
            SharedWorkDispatcher dispatcher = _world.Dispatcher;
            if (dispatcher != null)
            {
                s.poolWaiting = dispatcher.WaitingCountFor(WorkDestination.BackgroundPool);
                s.poolSubmitted = dispatcher.SubmittedCountFor(WorkDestination.BackgroundPool);
                IWorkExecutor pool = dispatcher.Executor(WorkDestination.BackgroundPool);
                if (pool != null) { s.poolHeld = pool.Held; s.poolCapacity = pool.Capacity; s.poolCanAccept = pool.CanAccept; }
            }

            s.remainingBeforeMs = RemainingMainSeconds() * 1000.0;

            // ---- the look: the scan the several-attempts control uses (CutSpatialSplit.Scan.cs) -- the light look first, the
            // detailed look for the rest, the display's view taken when a look first needs it. Under that control the scan is the
            // walk's own and the turn that follows uses its candidates; beside the control as before it changes nothing of the walk.
            SpatialSplitScan scan = Scan(in settings, MaxAttempts > 1, false, 0.0);
            s.owners = scan.owners;
            s.candidates = scan.candidates;
            s.attemptFragmentSeen = scan.attemptOut > 0;
            s.suppressed = scan.suppressed + scan.lightSuppressed;
            s.deferred = scan.deferred + scan.lightDeferred;
            s.limitReached = scan.limitReached + scan.lightLimitReached;
            s.displayState = scan.displayState;
            s.tooLarge = scan.tooLarge;
            s.oneComponent = scan.oneComponent + scan.lightOneComponent;
            s.oneComponentInputs = OneComponentInputs;
            s.lightExcluded = scan.LightExcluded;
            s.detailed = scan.detailed;
            s.viewFilled = scan.viewFilled;
            s.usedView = scan.viewFilled || scan.viewReused;
            s.scanMs = scan.totalSeconds * 1000.0;
            s.lightMs = scan.LightSeconds * 1000.0;
            s.detailMs = scan.detailSeconds * 1000.0;
            s.viewMs = scan.viewSeconds * 1000.0;

            // ---- the probe's own record: when each candidate was first seen one for its present input
            long recordBegin = Stopwatch.GetTimestamp();
            for (int i = 0; i < _scanCandidates.Count; i++)
            {
                Found c = _scanCandidates[i];
                if (_probeFirstSeen.TryGetValue(c.fragment, out FirstSeen seen) && seen.input.Equals(c.input)) { seen.lastFrame = s.frame; _probeFirstSeen[c.fragment] = seen; }
                else _probeFirstSeen[c.fragment] = new FirstSeen { input = c.input, ticks = recordBegin, frame = s.frame, lastFrame = s.frame };
            }

            if (_probeFirstSeen.Count > s.candidates)
            {
                // a fragment no longer a candidate (ended, gone, its input changed): its first sight is forgotten
                _probeGone.Clear();
                foreach (KeyValuePair<LogicalFragmentId, FirstSeen> e in _probeFirstSeen) if (e.Value.lastFrame != s.frame) _probeGone.Add(e.Key);
                for (int i = 0; i < _probeGone.Count; i++) _probeFirstSeen.Remove(_probeGone[i]);
            }

            s.recordMs = (Stopwatch.GetTimestamp() - recordBegin) * 1000.0 / Stopwatch.Frequency;
            if (CandidateProbeCheckEvery > 0 && _probeSamples.Count % CandidateProbeCheckEvery == CandidateProbeCheckEvery - 1)
            {
                long disagreedBefore = LightDisagreed;
                s.checkMs = CheckLightMarks(in settings) * 1000.0;
                s.checkDisagreed = (int)(LightDisagreed - disagreedBefore);
            }

            s.noIssue = MaxAttempts > 1 ? "not evaluated" : "control";
            s.scansAfter = Scans;
            s.scanSecondsAfter = ScanSeconds;
            return s;
        }

        // At Step's end: what the turn's own evaluations came to in this frame, for the sample taken in it.
        private void FinishCandidateProbe()
        {
            if (_probePendingAt < 0) return;
            int at = _probePendingAt;
            CandidateSample s = _probeSamples[at];
            _probePendingAt = -1;
            if (s.frame != Time.frameCount) return;
            if (_holdEvaluatedFrame == s.frame) s.hold = LastHold.ToString();
            SpatialSplitBudgetDecision decision = LastBudgetDecision;
            if (decision.Frame == s.frame)
            {
                s.budget = decision.Held ? "short" : "enough";
                s.budgetStage = decision.Stage.ToString();
                s.budgetRemainingMs = decision.Remaining * 1000.0;
            }

            s.turnScans = (int)(Scans - s.scansAfter);
            s.turnScanMs = (ScanSeconds - s.scanSecondsAfter) * 1000.0;
            s.foundLeft = _found.Count;
            s.freePlaces = Math.Max(0, MaxAttempts - _attempts.Count - (_attempt != null && !_attempts.Contains(_attempt) ? 1 : 0));
            if (_issueEvaluatedFrame == s.frame)
            {
                s.issuedInTurn = LastIssuedInTurn;
                s.noIssue = LastNoIssue.ToString();
            }

            _probeSamples[at] = s;
        }

        /// <summary>The samples as CSV and a summary beside it (the world's end); nothing without the launch argument or a sample.</summary>
        public void WriteCandidateProbe()
        {
            if (CandidateProbePath == null || _probeSamples.Count == 0) return;
            CultureInfo inv = CultureInfo.InvariantCulture;
            int world = System.Threading.Interlocked.Increment(ref s_candidateProbeWorlds);
            string path = world == 1 ? CandidateProbePath
                : Path.Combine(Path.GetDirectoryName(CandidateProbePath) ?? "", Path.GetFileNameWithoutExtension(CandidateProbePath) + "-" + world.ToString(inv) + Path.GetExtension(CandidateProbePath));
            var text = new StringBuilder(_probeSamples.Count * 160);
            text.Append("frame,seconds,owners,candidates,attemptFragmentSeen,suppressed,deferred,limitReached,displayState,attempt,attemptSerial,frameHold,structureChanged,displayRoomChanged,idle,remainingBeforeMs,hold,budget,budgetStage,budgetRemainingMs,poolWaiting,poolSubmitted,poolHeld,poolCapacity,poolCanAccept,scanMs,attemptsOut,attemptsOnWorker,attemptsDone,attemptsCollected,heldBytes,issuedTotal,endedTotal,publishedTotal,usedView,lightExcluded,detailed,tooLarge,viewFilled,lightMs,detailMs,viewMs,recordMs,checkMs,checkDisagreed,turnScans,turnScanMs,issuedInTurn,noIssue,foundLeft,freePlaces,oneComponent,oneComponentInputs\n");
            foreach (CandidateSample s in _probeSamples)
            {
                text.Append(s.frame).Append(',').Append(s.seconds.ToString("F4", inv)).Append(',').Append(s.owners).Append(',').Append(s.candidates).Append(',').Append(s.attemptFragmentSeen ? 1 : 0).Append(',')
                    .Append(s.suppressed).Append(',').Append(s.deferred).Append(',').Append(s.limitReached).Append(',').Append(s.displayState).Append(',').Append(s.attempt).Append(',').Append(s.attemptSerial).Append(',')
                    .Append(s.frameHold).Append(',').Append(s.structureChanged ? 1 : 0).Append(',').Append(s.displayRoomChanged ? 1 : 0).Append(',').Append(s.idle ? 1 : 0).Append(',')
                    .Append(s.remainingBeforeMs.ToString("F4", inv)).Append(',').Append(s.hold).Append(',').Append(s.budget).Append(',').Append(s.budgetStage).Append(',').Append(s.budgetRemainingMs.ToString("F4", inv)).Append(',')
                    .Append(s.poolWaiting).Append(',').Append(s.poolSubmitted).Append(',').Append(s.poolHeld).Append(',').Append(s.poolCapacity).Append(',').Append(s.poolCanAccept ? 1 : 0).Append(',')
                    .Append(s.scanMs.ToString("F4", inv)).Append(',').Append(s.attemptsOut).Append(',').Append(s.attemptsOnWorker).Append(',').Append(s.attemptsDone).Append(',').Append(s.attemptsCollected).Append(',')
                    .Append(s.heldBytes).Append(',').Append(s.issuedTotal).Append(',').Append(s.endedTotal).Append(',').Append(s.publishedTotal).Append(',').Append(s.usedView ? 1 : 0).Append(',')
                    .Append(s.lightExcluded).Append(',').Append(s.detailed).Append(',').Append(s.tooLarge).Append(',').Append(s.viewFilled ? 1 : 0).Append(',')
                    .Append(s.lightMs.ToString("F4", inv)).Append(',').Append(s.detailMs.ToString("F4", inv)).Append(',').Append(s.viewMs.ToString("F4", inv)).Append(',')
                    .Append(s.recordMs.ToString("F4", inv)).Append(',').Append(s.checkMs.ToString("F4", inv)).Append(',').Append(s.checkDisagreed).Append(',')
                    .Append(s.turnScans).Append(',').Append(s.turnScanMs.ToString("F4", inv)).Append(',').Append(s.issuedInTurn).Append(',').Append(s.noIssue).Append(',')
                    .Append(s.foundLeft).Append(',').Append(s.freePlaces).Append(',').Append(s.oneComponent).Append(',').Append(s.oneComponentInputs).Append('\n');
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            var waits = new StringBuilder(_probeWaits.Count * 40);
            waits.Append("serial,fragment,seenFrame,issuedFrame,waitMs\n");
            foreach (CandidateWait w in _probeWaits)
            {
                waits.Append(w.serial).Append(',').Append(w.fragment).Append(',').Append(w.seenFrame).Append(',').Append(w.issuedFrame).Append(',').Append(w.waitMs.ToString("F3", inv)).Append('\n');
            }

            File.WriteAllText(path + ".waits.csv", waits.ToString(), new UTF8Encoding(false));
            string summary = DescribeCandidateProbe() + "\n" + DescribeSeveral();
            File.WriteAllText(path + ".summary.txt", summary + "\n", new UTF8Encoding(false));
            UnityEngine.Debug.Log(summary);
        }

        /// <summary>The samples' tallies: the candidates' counts, with an attempt out and without, and the scan's time.</summary>
        public string DescribeCandidateProbe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            int n = _probeSamples.Count, zero = 0, one = 0, more = 0, withAttempt = 0, withAttemptAndCandidate = 0, noAttempt = 0, noAttemptTwo = 0, most = 0;
            long sum = 0;
            var scans = new List<double>(n);
            var counts = new List<int>(n);
            foreach (CandidateSample s in _probeSamples)
            {
                if (s.candidates == 0) zero++; else if (s.candidates == 1) one++; else more++;
                if (s.attemptsOut > 0) { withAttempt++; if (s.candidates >= 1) withAttemptAndCandidate++; }
                else { noAttempt++; if (s.candidates >= 2) noAttemptTwo++; }
                sum += s.candidates;
                most = Math.Max(most, s.candidates);
                scans.Add(s.scanMs);
                counts.Add(s.candidates);
            }

            scans.Sort();
            counts.Sort();
            double scanSum = 0.0;
            foreach (double v in scans) scanSum += v;
            string P(double part, int of) => of == 0 ? "-" : (100.0 * part / of).ToString("F1", inv) + " %";
            return "SPLIT CANDIDATE PROBE: samples " + n + " (every " + CandidateProbeInterval + " frames; Background workers " + WorkerPoolExecutor.DefaultBackgroundWorkerCount + "): candidates not issued 0: " + zero + " (" + P(zero, n)
                + "), 1: " + one + " (" + P(one, n) + "), 2 or more: " + more + " (" + P(more, n) + "); mean " + (n == 0 ? "-" : (sum / (double)n).ToString("F2", inv)) + ", p95 "
                + (n == 0 ? "-" : counts[Math.Min(n - 1, (int)Math.Round(0.95 * (n - 1)))].ToString(inv)) + ", most " + most
                + "; with an attempt out " + withAttempt + ", of them with a candidate " + withAttemptAndCandidate + " (" + P(withAttemptAndCandidate, withAttempt) + "); without an attempt " + noAttempt
                + ", of them with two or more " + noAttemptTwo + " (" + P(noAttemptTwo, noAttempt) + "); scan ms mean " + (n == 0 ? "-" : (scanSum / n).ToString("F4", inv)) + ", p95 "
                + (n == 0 ? "-" : scans[Math.Min(n - 1, (int)Math.Round(0.95 * (n - 1)))].ToString("F4", inv)) + ", most " + (n == 0 ? "-" : scans[n - 1].ToString("F4", inv));
        }
    }
}
#endif
