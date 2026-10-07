using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The four parts of a Prop Slash run (TL, 2026-10-07), chosen at the start and fixed for the run.
    /// <para>
    /// **Scenario**: the fixed preparation, the script and the recorded input's replay, the walk's progress and the
    /// run's end -- what makes the load. Always on in a run started with <see cref="SandboxPropSlashPlayerCheck.Argument"/>;
    /// a functional run and a measurement run are the same scenario.
    /// **Metrics**: the frame timeline (frames.csv) and the Profiler's recorders it reads.
    /// **Checks**: the judgements of what was drawn, simulated, registered and hit, with every scan, track and
    /// collection that exists to judge.
    /// **Detailed diagnostics**: the records of each individual, the detail files and the pictures.
    /// </para>
    /// The ordinary measurement leaves the checks and the detailed diagnostics out. A part left out does none of its
    /// work -- it is not the judgement alone that is skipped -- and what it would have judged is written as not run,
    /// never as passed.
    /// </summary>
    public readonly struct PropSlashParts
    {
        public readonly bool scenario, metrics, checks, detail;

        /// <summary>Why a part is not as it was asked, or null.</summary>
        public readonly string note;

        public PropSlashParts(bool scenario, bool metrics, bool checks, bool detail, string note)
        {
            this.scenario = scenario;
            this.metrics = metrics;
            this.checks = checks;
            this.detail = detail;
            this.note = note;
        }

        /// <summary>Whether anything is asked of the hits, the pieces and the characters beyond the scenario's own.</summary>
        public bool Watching => checks || detail;

        private static string OnOff(bool on) => on ? "on" : "off";

        public string Describe() =>
            "scenario=" + OnOff(scenario) + " metrics=" + OnOff(metrics) + " checks=" + OnOff(checks) + " detailed diagnostics=" + OnOff(detail)
            + (string.IsNullOrEmpty(note) ? "" : " (" + note + ")");
    }

    /// <summary>
    /// The accepted cuts a run still has something to follow of, and how many of them its end waits for. A cut is
    /// followed until it has been seen published and committed and is then let go, so a frame looks at the unfinished
    /// ones only -- not at every cut the run ever accepted. The end waits for the cuts added as waited for and not yet
    /// committed; what answers for the others (a fusion, a hull group) is asked apart.
    /// </summary>
    internal sealed class OpenCuts<T> where T : class
    {
        [Flags]
        internal enum Progress
        {
            None = 0,

            /// <summary>A cut the end waits for was seen committed in this step.</summary>
            WaitedForCommitted = 1,

            /// <summary>Nothing more is to be followed of this cut.</summary>
            Finished = 2,
        }

        private readonly List<T> _open = new List<T>();

        /// <summary>The cuts the end waits for that are not committed yet.</summary>
        public int Waiting { get; private set; }

        /// <summary>The cuts still followed.</summary>
        public int Open => _open.Count;

        public int Added { get; private set; }

        /// <summary>Observation: the cuts looked at by <see cref="Advance"/> so far.</summary>
        public long Visits { get; private set; }

        /// <param name="follow">Whether there is anything to follow of it (an operation of its own).</param>
        /// <param name="waitedFor">Whether the run's end waits for its commit.</param>
        public void Add(T cut, bool follow, bool waitedFor)
        {
            Added++;
            if (waitedFor) Waiting++;
            if (follow) _open.Add(cut);
        }

        /// <summary>One look at each cut still followed, in the order they were added; the finished ones are let go, the order of the others kept.</summary>
        public void Advance(Func<T, Progress> step)
        {
            int kept = 0;
            for (int i = 0; i < _open.Count; i++)
            {
                T cut = _open[i];
                Visits++;
                Progress progress = step(cut);
                if ((progress & Progress.WaitedForCommitted) != 0) Waiting--;
                if ((progress & Progress.Finished) == 0) _open[kept++] = cut;
            }

            if (kept < _open.Count) _open.RemoveRange(kept, _open.Count - kept);
        }
    }

    /// <summary>
    /// How a run ended, apart from what its checks said (TL, 2026-10-07): whether the scenario went through to its
    /// ordinary end, and whether the checks passed, failed or were not run. A run whose checks were not run has not
    /// passed them.
    /// </summary>
    internal readonly struct PropSlashRunResult
    {
        public readonly bool completed;
        public readonly string notCompleted;   // what kept it from completing, or null
        public readonly bool checksRun;
        public readonly int checksHeld, checksFailed, checksNotRun;
        public readonly int code;

        public PropSlashRunResult(bool completed, string notCompleted, bool checksRun, int checksHeld, int checksFailed, int checksNotRun, int code)
        {
            this.completed = completed;
            this.notCompleted = notCompleted;
            this.checksRun = checksRun;
            this.checksHeld = checksHeld;
            this.checksFailed = checksFailed;
            this.checksNotRun = checksNotRun;
            this.code = code;
        }

        public string RunLine => "run: " + (completed ? "completed (the script, the cuts' completion and the ordinary ending)" : "NOT completed (" + notCompleted + ")");

        public string ChecksLine =>
            "checks: " + (!checksRun ? "not run (left out of this run: neither passed nor failed" + (checksNotRun > 0 ? "; " + checksNotRun + " expectations reached and not judged" : "") + ")"
                : checksFailed == 0 ? "passed (" + checksHeld + " expectations held)"
                : "FAILED (" + checksFailed + " failed, " + checksHeld + " held)");

        /// <summary>
        /// The process's code: what it always was while the checks run (any failure 14, else 18 for a replay that began
        /// before ready, else 0). With the checks left out it speaks of the run alone: 14 when it did not complete.
        /// </summary>
        public static int CodeOf(bool checksRun, int failures, bool completed, bool replayBeforeReady) =>
            CheckEnding.CodeOf(failures + (!checksRun && !completed ? 1 : 0), replayBeforeReady);
    }

    public static partial class SandboxPropSlashPlayerCheck
    {
        /// <summary>The frame timeline and its recorders: "on" (the default) or "off".</summary>
        public const string MetricsArgument = "-zantetsuPropMetrics";

        /// <summary>The judgements and everything that exists to judge: "on" (the default) or "off".</summary>
        public const string ChecksArgument = "-zantetsuPropChecks";

        /// <summary>The records of each individual, the detail files and the pictures: "on" (the default) or "off".</summary>
        public const string DetailArgument = "-zantetsuPropDetail";

        /// <summary>
        /// The parts a Player started with <paramref name="arguments"/> runs. Every part is on unless its argument says
        /// "off". The checks and the detailed diagnostics can be left out of a city walk only (the script's walk, not a
        /// person's): elsewhere they stay on and the note says so. The checks read the frame timeline, so the metrics
        /// stay on with them.
        /// </summary>
        public static PropSlashParts PartsFor(string[] arguments)
        {
            string ValueOf(string name)
            {
                for (int i = 0; i < arguments.Length - 1; i++)
                {
                    if (string.Equals(arguments[i], name, StringComparison.OrdinalIgnoreCase)) return arguments[i + 1];
                }

                return null;
            }

            bool Given(string name) => Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) >= 0;
            if (string.IsNullOrEmpty(ValueOf(Argument)))
            {
                return new PropSlashParts(false, false, false, false, "not a Prop Slash run: no " + Argument);
            }

            var notes = new List<string>();
            bool Read(string name)
            {
                string value = ValueOf(name);
                if (value == null)
                {
                    if (Given(name)) notes.Add(name + " has no value: on");
                    return true;
                }

                if (string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)) notes.Add(name + " " + value + " is neither on nor off: on");
                return true;
            }

            bool metrics = Read(MetricsArgument), checks = Read(ChecksArgument), detail = Read(DetailArgument);
            string mobPlan = ValueOf(MobPlanArgument);
            bool scriptedCityWalk = Given(CityWalkArgument) && !string.IsNullOrEmpty(mobPlan) && !string.Equals(mobPlan, "live", StringComparison.OrdinalIgnoreCase);
            if ((!checks || !detail) && !scriptedCityWalk)
            {
                checks = detail = true;
                notes.Add("the checks and the detailed diagnostics can be left out of a scripted city walk only (" + CityWalkArgument + " with " + MobPlanArgument + " <script>): both on");
            }

            if (checks && !metrics)
            {
                metrics = true;
                notes.Add("the checks read the frame timeline: metrics on");
            }

            return new PropSlashParts(true, metrics, checks, detail, notes.Count > 0 ? string.Join("; ", notes) : null);
        }

        // The coarse sections of the run's own work in a frame (TL, 2026-10-07), each entered once or a few times a
        // frame and never one a piece or a row. The check's old sections are kept by their names
        // (Zantetsu.Check.LateUpdate around the whole LateUpdate; MobPlanFrame, MobPlanDisplay, MobPlanLifetime and
        // MobPlanHitRegistry inside it), so a time is read as a section's own, its children's taken out.
        private static readonly ProfilerMarker s_scenarioFrame = new ProfilerMarker("Zantetsu.Scenario.Frame");     // the script's drive, the input's feed, the cuts the end waits for
        private static readonly ProfilerMarker s_metricsFrame = new ProfilerMarker("Zantetsu.Metrics.RecordFrame");   // the frame timeline's row
        private static readonly ProfilerMarker s_checkHits = new ProfilerMarker("Zantetsu.Check.Hits");               // a hit's judgements and records
        private static readonly ProfilerMarker s_checkPieces = new ProfilerMarker("Zantetsu.Check.Pieces");           // the pieces and the hulls, frame by frame
        private static readonly ProfilerMarker s_checkCuts = new ProfilerMarker("Zantetsu.Check.Cuts");               // every accepted cut's stages and the ledger's fragments
        private static readonly ProfilerMarker s_checkNpcs = new ProfilerMarker("Zantetsu.Check.Npcs");               // the crowd's individuals: retired and drawn, registered, models
        private static readonly ProfilerMarker s_checkDetail = new ProfilerMarker("Zantetsu.Check.Detail");           // the detail rows of a frame

        /// <summary>The most rows the timeline's room is made for; a longer run's further rows are made in their frames.</summary>
        internal const int TimelineRoomCap = 80000;

        /// <summary>
        /// The seconds the timeline is expected to cover, from the script's start to the run's end: the script's own
        /// end and the 30 s its cuts may still take (a scripted run; 120 s otherwise), the observation (20 s; none in a
        /// light run), the hold before the ending, and 20 s for what follows the script and the ending (15 s at most).
        /// The waits before the script (the preparation, the registration, the head's first move) are not in the timeline.
        /// </summary>
        internal static double TimelineSecondsFor(double scriptSeconds, bool scripted, bool light, float endHoldSeconds) =>
            (scripted ? scriptSeconds + 30.0 : 120.0) + (light ? 0.0 : 20.0) + endHoldSeconds + 20.0;

        /// <summary>The rows of the timeline's room: one a frame for those seconds at the display's rate, a twentieth more, never past the cap.</summary>
        internal static int TimelineRowsFor(double scriptSeconds, bool scripted, bool light, float endHoldSeconds, float rate) =>
            (int)Math.Min(TimelineRoomCap, Math.Ceiling(TimelineSecondsFor(scriptSeconds, scripted, light, endHoldSeconds) * rate * 1.05));

        /// <summary>Tests: the process-wide retained-heap difference across making the rows, as an observation only.</summary>
        internal static long TimelineRoomBytesForTest(int rows, int markers) => Walk.TimelineRoomBytes(rows, markers);

        // The parts of this process's run, decided once where the run is started (StartIfAsked).
        private static PropSlashParts s_parts = new PropSlashParts(true, true, true, true, null);

        /// <summary>For a launch record: the parts a Player started with these arguments runs.</summary>
        public static string DescribeParts(string[] arguments) => PartsFor(arguments).Describe();

        private sealed partial class Walk
        {
            /// <summary>The parts of this run, fixed before it starts.</summary>
            internal PropSlashParts runParts = new PropSlashParts(true, true, true, true, null);

            // The cuts still followed and the count the end waits for (the scenario's own; the checks' records of the
            // same cuts are the Accepted list).
            private readonly OpenCuts<Accepted> _openCuts = new OpenCuts<Accepted>();
            private Func<Accepted, OpenCuts<Accepted>.Progress> _followCut;
            private int _followFrame, _followUpdate;
            private bool _followLogged;
            private string _cutPicture;

            // What the run ended as, apart from the checks: the script and the cuts' completion reached, what kept the
            // run from completing, and the expectations' counts.
            private bool _scriptCompleted;
            private readonly List<string> _runFailures = new List<string>();
            private int _checksHeld, _checkFailures, _checksNotRun;
            private bool _timelineBegun;
            private int _closingFailures, _scenarioHits;

            /// <summary>
            /// A detail file of a check's rows. With the detailed diagnostics left out and the check still run, the check
            /// goes on gathering what it judges and its rows go nowhere: no file is made.
            /// </summary>
            private StreamWriter DetailWriter(string file) =>
                runParts.detail ? new StreamWriter(Path.Combine(directory, file)) : new StreamWriter(Stream.Null);

            /// <summary>
            /// A hit the driver accepted becomes a cut the run follows: the record made here, as the check always made
            /// it. Null for a hit that is no cut. It is not added here: the caller adds it where it always was added.
            /// </summary>
            private Accepted ScenarioAccept(in SlashHitConfirmed hit, int frame)
            {
                // The multi-NPC mode and the building E2E follow a Pending cut as they follow a Published one.
                bool pendingCut = (multiNpc || building || MobPlanMode) && hit.Acceptance == ProvisionalCutAcceptance.Pending;
                if (hit.Acceptance != ProvisionalCutAcceptance.Published && !pendingCut)
                {
                    return null;
                }

                bool child = _world.Ledger.TryGetOrigin(hit.Fragment, out _, out _);
                return new Accepted
                {
                    slash = hit.SlashId,
                    fragment = hit.Fragment,
                    operation = hit.Operation,
                    child = child,
                    acceptedFrame = frame,
                    // A number is enough to wait for it; where anything is written of it, the check names it for its lineage (LogHit).
                    name = "op" + hit.Operation.value + (child ? "-child" : "-root"),
                    pending = pendingCut,
                    provisionalFrame = pendingCut ? -1 : frame,
                    fusion = pendingCut && _world.Fusion != null && _world.Owners.TryGet(hit.Fragment, out PhysicsFragmentOwner fusedOwner) && fusedOwner.IsFused,
                    hull = pendingCut && _world.Hulls != null && hit.Operation.value == 0 && hit.Admission == LogicalCutAdmission.NoOp,
                };
            }

                /// <summary>
            /// A hit the driver holds (a character whose cut is taken up later) is remembered by the scenario where nothing
            /// else watches the hits: the cut it becomes is one the end waits for. As MultiOnHit remembers it.
            /// </summary>
            private void ScenarioNoteHeld(in SlashHitConfirmed hit, int frame)
            {
                if (hit.Acceptance != ProvisionalCutAcceptance.Held) return;
                MultiFollowIdentification();
                string of = LineageOf(hit.Fragment);
                foreach (KeyValuePair<LogicalFragmentId, SandboxNpcCharacter> root in _multiRoots)
                {
                    if ("npc-" + Model(root.Value) == of) _multiHeld.Add((root.Value, hit.SlashId, hit.Fragment, frame));
                }
            }

            /// <summary>
            /// With the checks left out: what the scenario did, for the record of the run -- the script's steps (the same
            /// file the checks' summary writes) and the counts a comparison of two runs' loads reads.
            /// </summary>
            private void ScenarioRecord()
            {
                Log("summary: not run (the checks are left out of this run); the scenario's own record follows");
                int committed = 0;
                foreach (Accepted a in _accepted) if (a.committedFrame >= 0) committed++;
                Log("scenario: hits read " + _scenarioHits + ", cuts accepted " + _accepted.Count + " (committed " + committed + ", still followed " + _openCuts.Open + ", waited for " + _openCuts.Waiting
                    + "), cuts looked at while following " + _openCuts.Visits + "; replay fed " + _recorder.ReplayIndex
                    + (MobPlanMode ? "; chunks begun " + (_mpChunk + 1) + ", walked " + _mpTravel.ToString("F1", Inv) + " m, script seconds " + MobPlanNow.ToString("F1", Inv) + " (end " + _mpEnd.ToString("R", Inv) + ")" : ""));
                if (cityWalk && _mpSteps != null) CityWalkStepsSummary();
            }

            /// <summary>The cut joins the run's records: followed while it has an operation, waited for unless a fusion or a hull group answers for it.</summary>
            private void ScenarioAdd(Accepted cut)
            {
                _accepted.Add(cut);
                _openCuts.Add(cut, cut.operation.value != CutOperationId.Unset, !cut.fusion && !cut.hull);
            }

            /// <summary>
            /// The cuts still followed, once a frame: the frame each was published and committed, written down when
            /// anything is written of them. Only the unfinished ones are read.
            /// </summary>
            private void ScenarioFollowCuts(int frame, int update)
            {
                _followFrame = frame;
                _followUpdate = update;
                _followLogged = runParts.Watching;
                _followCut ??= FollowCut;
                _openCuts.Advance(_followCut);
            }

            private OpenCuts<Accepted>.Progress FollowCut(Accepted a)
            {
                var progress = OpenCuts<Accepted>.Progress.None;
                if (_world.Ledger.TryGetOperation(a.operation, out LogicalCutOperation op))
                {
                    if (a.publishedFrame < 0 && op.state != LogicalCutOperationState.Admitted)
                    {
                        a.publishedFrame = _followFrame;
                        if (_followLogged)
                        {
                            Log(a.name + " Final/Logical published: state=" + op.state + " frame=" + _followFrame
                                + " children=" + op.positive.value + "," + op.negative.value + Clocks(_followUpdate));
                            _cutPicture ??= a.name + "-2-final";
                        }
                    }

                    if (a.committedFrame < 0 && _world.Geometry.StageOf(a.operation) == CutGeometryStage.Committed)
                    {
                        a.committedFrame = _followFrame;
                        if (!a.fusion && !a.hull) progress |= OpenCuts<Accepted>.Progress.WaitedForCommitted;
                        if (_followLogged)
                        {
                            Log(a.name + " geometry committed: frame=" + _followFrame + " ledger=" + op.state + Clocks(_followUpdate));
                            _cutPicture ??= a.name + "-3-geometry";
                        }
                    }
                }

                if (a.publishedFrame >= 0 && a.committedFrame >= 0) progress |= OpenCuts<Accepted>.Progress.Finished;
                return progress;
            }

            /// <summary>
            /// A condition of the run itself -- not of what a check judges: counted and written whether the checks run
            /// or not, with the line an expectation writes.
            /// </summary>
            private void RunExpect(bool held, string what)
            {
                if (!held)
                {
                    _failures++;
                    _runFailures.Add(what);
                }

                Log((held ? "ok: " : "FAILED: ") + what);
            }

            /// <summary>What the run ended as: whether it completed, and what the checks said or that they were not run.</summary>
            private PropSlashRunResult RunResult(CheckEnding ending, int? fixedCode, int closingFailures, int code)
            {
                var why = new List<string>();
                if (fixedCode.HasValue) why.Add("ended early with code " + fixedCode.Value);
                if (_cwRegistrationCutShort) why.Add("the registration was cut short at its deadline");
                if (!fixedCode.HasValue && !_cwRegistrationCutShort && !_scriptCompleted) why.Add("the script and the cuts' completion did not finish before the deadline");
                foreach (string stage in _cwDeadlinesReached) why.Add("deadline reached: " + stage);
                foreach (string failure in _runFailures) why.Add(failure);
                if (ending != null && !fixedCode.HasValue && !ending.WorldReleased) why.Add("the world was not released by the ordinary ending");
                if (closingFailures > 0) why.Add(closingFailures + " closing step(s) failed");
                return new PropSlashRunResult(why.Count == 0, why.Count == 0 ? null : string.Join("; ", why), runParts.checks, _checksHeld, _checkFailures, _checksNotRun, code);
            }

            /// <summary>The run's result, beside its other files and in the log: the run and the checks apart.</summary>
            private void WriteRunResult(in PropSlashRunResult result)
            {
                string numeric =
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    VpNumericDiagnosis.Describe();
#else
                    "not compiled (a non-Development Player)";
#endif
                Log(result.RunLine);
                Log(result.ChecksLine);
                Log("parts of this run: " + runParts.Describe() + "; numeric diagnosis " + numeric + "; code " + result.code);
                try
                {
                    var text = new StringBuilder();
                    text.Append(result.RunLine).Append('\n').Append(result.ChecksLine).Append('\n')
                        .Append("parts: ").Append(runParts.Describe()).Append('\n')
                        .Append("numeric diagnosis: ").Append(numeric).Append('\n')
                        .Append("code: ").Append(result.code).Append('\n')
                        .Append("metrics: ").Append(runParts.metrics ? "frames.csv rows " + _timeline.Count + ", rows made past the room prepared " + _rowsPastRoom
                            + ", room prepared for " + _rowRoom + " rows" : "not recorded").Append('\n');
                    File.WriteAllText(Path.Combine(directory, "run-result.txt"), text.ToString());
                }
                catch (IOException e)
                {
                    Log("run result file: " + e.Message);
                }
            }
        }
    }
}
