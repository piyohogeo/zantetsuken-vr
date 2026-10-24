#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The pool replay (GRAPH-CONNECT-14 §3-1): the saved samples' attempts run through the product's own
    /// <see cref="SharedWorkDispatcher"/> and a real Background pool (<see cref="WorkerPoolExecutor.BackgroundPool"/>: the
    /// profile's capacity 2, the platform's worker count, the lowest priority), one attempt out at a time as the split has it,
    /// with this thread standing for Main: a tick every <see cref="TickSeconds"/> (the frame's period at 90 Hz) opens a frame
    /// and dispatches (collection then submission), and the ACTUAL interval of every tick is recorded. The product's own
    /// scheduling -- the Main budget's gate, the walk's order, the inputs that follow a publication -- is not reproduced: what
    /// this measures is how long the same input waits and runs on the real pool.
    /// <para>
    /// The synthetic competitor (§3-1-1): a work of a FIXED amount (a deterministic loop of <see cref="CompetitorWorkSteps"/>
    /// steps, not a timed spin), submitted as <see cref="WorkPurpose.Speculative"/>, kept outstanding up to the level (L0 none,
    /// L1 one, L2 two: the pool's capacity). A competitor collected at a dispatch is replaced AT THE SAME TICK (the top-up runs
    /// right after the dispatch that collected it, and a second dispatch of the tick submits it), so the load holds across the
    /// attempt. The competitors are established at a tick of their own before the attempt is enqueued at the next one. Every
    /// competitor carries the condition, the sample and the warm-up flag of the attempt it ran beside, with its own stamps
    /// (<see cref="IDispatchObserved"/> for the acceptance), and each attempt's record says how many competitors ran during its
    /// worker run and for how long their runs overlapped it (the evidence of a shared CPU), beside the dispatch opportunities
    /// at which the attempt could not be submitted for want of room (<see cref="SharedWorkDispatcher.CapacityBlockedOpportunities"/>).
    /// Whether the load condition held is read from those afterwards: a 0 says the condition did not arise, not that the
    /// instrument failed. After each attempt the pool is drained (no new competitor; ticks until nothing is held) so that
    /// every run starts from an empty pool.
    /// </para>
    /// <para>
    /// **A work that does not come back keeps everything it holds.** When the attempt is not collected within the attempt
    /// timeout, this does NOT return to the caller as if the attempt had ended: the dispatcher is shut down (which waits, up to the
    /// stop timeout, for the running worker to finish and collects what ended) and a <see cref="SpatialSplitReplayPoolStopException"/>
    /// is thrown that says whether the work was collected and whether the workers stopped. The caller frees the attempt's input and
    /// scratch only when the work was collected; otherwise it holds them (a worker may still be reading them) and the session ends.
    /// </para>
    /// </summary>
    internal sealed class SpatialSplitReplayPool : IDisposable
    {
        public const double TickSeconds = 1.0 / 90.0;
        public const int DefaultDrainTicks = 180;      // 2 s
        public const int DefaultAttemptTicks = 90 * 60; // 1 min
        public const int DefaultStopMilliseconds = 10000;
        public const int PoolCapacity = 2;             // the profile's Background pool capacity on the PC (BackgroundWorkerCount)

        private readonly SharedWorkDispatcher _dispatcher;
        private readonly WorkerPoolExecutor _background;
        private readonly UnityJobWorkExecutor _unityJob;
        private readonly IdleExecutor _geometry;
        private readonly int _level;
        private readonly long _competitorWork;
        private readonly long _tickTicks;
        private readonly int _attemptTicks, _stopMilliseconds, _drainTicks;
        private readonly List<double> _intervals = new List<double>();
        private readonly List<CompetitorWork> _outstanding = new List<CompetitorWork>();
        private readonly List<CompetitorWork> _competitors = new List<CompetitorWork>();   // every competitor of the run, in issue order
        private int _frame;
        private long _lastTick;
        private long _origin;   // the first t0 of the run: the lines' ms are from it
        private int _competitorsIssued, _competitorsRefused, _competitorsCollected;
        private bool _stopped;
        private string _condition = "", _sample = "";

        /// <summary>Attempts run (the warm-up apart), the warm-up's attempts and their wall time, the drains that emptied the pool and those that did not within <see cref="DrainTicks"/>.</summary>
        public int Attempts { get; private set; }
        public int WarmupAttempts { get; private set; }
        public double WarmupSeconds { get; private set; }
        public double WarmupBusySeconds { get; private set; }
        public int Drains { get; private set; }
        public int DrainFailures { get; private set; }

        /// <summary>Set by the caller around the warm-up runs: their attempts and competitors are marked warm-up and the attempts' rows are not written.</summary>
        public bool Warmup { get; set; }

        /// <summary>Tests: a gate the attempt's Begin waits at (the worker holds the input), to make a timeout; null in a run.</summary>
        internal ManualResetEventSlim TestBeginGate;

        public int Level => _level;
        public long CompetitorWorkSteps => _competitorWork;
        public long Origin => _origin;
        public bool Stopped => _stopped;
        public SharedWorkDispatcher Dispatcher => _dispatcher;
        public WorkerPoolExecutor Background => _background;
        public IReadOnlyList<CompetitorWork> Competitors => _competitors;

        public SpatialSplitReplayPool(int level, long competitorWork, int attemptTicks = DefaultAttemptTicks, int stopMilliseconds = DefaultStopMilliseconds, int drainTicks = DefaultDrainTicks)
        {
            if (drainTicks <= 0) throw new ArgumentOutOfRangeException(nameof(drainTicks));
            _drainTicks = drainTicks;
            if (level < 0 || level > PoolCapacity) throw new ArgumentOutOfRangeException(nameof(level), level, "the competitor level is L0, L1 or L2 (the pool's capacity)");
            if (competitorWork <= 0) throw new ArgumentOutOfRangeException(nameof(competitorWork), "the competitor's work is a positive count of steps");
            if (attemptTicks <= 0) throw new ArgumentOutOfRangeException(nameof(attemptTicks));
            if (stopMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(stopMilliseconds));
            _level = level;
            _competitorWork = competitorWork;
            _attemptTicks = attemptTicks;
            _stopMilliseconds = stopMilliseconds;
            _tickTicks = (long)(TickSeconds * Stopwatch.Frequency);
            _unityJob = new UnityJobWorkExecutor(1);
            _geometry = new IdleExecutor(WorkDestination.GeometryPool);
            _background = WorkerPoolExecutor.BackgroundPool(PoolCapacity);
            _dispatcher = new SharedWorkDispatcher(32, 8, 64, _unityJob, _geometry, _background);
            if (!_background.WaitUntilStartupFinished(5000))
            {
                throw new TimeoutException("the Background pool's workers did not start within 5 s");
            }
            _lastTick = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// Runs one attempt through the dispatcher and the pool (see <see cref="RunWork"/>) and fills the result's pool record from the
        /// attempt's own stamps (0 where the timeline is off). The attempt's input stays the caller's; it may be freed only when this
        /// returns, or when a <see cref="SpatialSplitReplayPoolStopException"/> says the work was collected.
        /// </summary>
        public void RunAttempt(CutSpatialSplit.Attempt attempt, SpatialSplitReplayResult result, string condition)
        {
            IDispatchWork work = TestBeginGate != null ? new GatedWork(attempt, TestBeginGate) : (IDispatchWork)attempt;
            RunRecord run = RunWork(work, () => attempt.collected, condition, result.world + "/" + result.attempt, t0 => attempt.enqueuedAt = t0);
            result.pool = new SpatialSplitPoolRecord
            {
                condition = condition ?? "", level = _level, competitorWork = _competitorWork, warmup = Warmup,
                t0 = attempt.enqueuedAt, t1 = attempt.acceptedAt, t2 = attempt.beganAt, t3 = attempt.endedAt, t4 = attempt.collectedAt,
                ticks = run.ticks, blocked = run.blocked, heldBefore = run.heldBefore, drained = run.drained, wallSeconds = run.wallSeconds,
                competitorsDuring = run.competitorsDuring, overlapSeconds = run.overlapSeconds, workerBusyDeltaSeconds = run.workerBusyDelta,
            };
        }

        /// <summary>One run's record. A measurement that was not taken (the observation or the timeline off) is null, never 0.</summary>
        internal struct RunRecord
        {
            public int ticks, heldBefore;
            public int? blocked, competitorsDuring;
            public bool drained;
            public double wallSeconds;
            public double? overlapSeconds;
            public double[] workerBusyDelta;   // null when the observation is off
        }

        /// <summary>
        /// The run of one work: the competitors established at a tick of their own, the work enqueued at the next (t0 handed to the
        /// caller) and dispatched, ticks until <paramref name="isCollected"/> says the dispatcher collected it, then the drain. The
        /// competitors issued meanwhile carry <paramref name="condition"/> and <paramref name="sample"/>. Throws
        /// <see cref="SpatialSplitReplayPoolStopException"/> when the work is not collected within the attempt timeout, after the stop.
        /// </summary>
        internal RunRecord RunWork(IDispatchWork work, Func<bool> isCollected, string condition, string sample, Action<long> noteEnqueued)
        {
            if (_stopped)
            {
                // nothing runs after a stop; the work handed in here was never enqueued, so the caller may free it (WorkCollected true)
                throw new SpatialSplitReplayPoolStopException("the pool replay was stopped by an earlier timeout: nothing runs after it", true, false, SpatialSplitReplayPoolStopReason.AlreadyStopped, null);
            }
            _condition = condition ?? "";
            _sample = sample ?? "";
            long wallBegin = Stopwatch.GetTimestamp();
            bool observing = DispatchObservation.Enabled;          // the workers' busy time and the blocked count are the observation's
            bool stamping = CutSpatialSplit.TimelineObserve;      // the competitors' and the work's stamps are the timeline's
            double[] busyBefore = observing ? BusyNow() : null;
            int competitorsBefore = _competitors.Count;   // every competitor from here on (the level's own included) is this run's
            // 1. the level: the competitors first, at a tick of their own, so that the room is theirs when the work comes
            Tick();
            TopUp();
            Pump();
            // 2. the work at the next tick: enqueued, then this tick's dispatch (submission in the same opportunity where there is room)
            Tick();
            int blockedBefore = _dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool);
            int heldBefore = _background.Held;
            long t0 = CutSpatialSplit.TimelineClock();
            if (!_dispatcher.TryEnqueue(WorkPurpose.Maintenance, work, out WorkTicket _))
            {
                throw new InvalidOperationException("the dispatcher refused the work (waiting " + _dispatcher.WaitingCount + ")");
            }
            noteEnqueued?.Invoke(t0);
            if (_origin == 0 && t0 != 0) _origin = t0;
            int ticks = 0;
            PumpAndTopUp();
            while (!isCollected())
            {
                Tick();
                ticks++;
                if (ticks > _attemptTicks)
                {
                    Stop(work, isCollected, ticks);   // throws; nothing of the work is freed by this
                }
                PumpAndTopUp();
            }
            int? blocked = observing ? _dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool) - blockedBefore : (int?)null;
            // 3. the drain: no new competitor; ticks until nothing is held, so that the next run starts from an empty pool. A drain that
            // does not empty the pool within its ticks ends the session (TL P2): the work IS collected (its input may be freed), but a
            // competitor is still out on a worker, so the next condition would not start from the same state and the workers' time
            // would be mixed -- the dispatcher is shut down and the stop exception says so.
            bool drained = Drain();
            if (!drained)
            {
                _stopped = true;
                DispatchShutdownResult shutdown;
                try { shutdown = _dispatcher.Shutdown(_stopMilliseconds); }
                catch (Exception e) { throw new SpatialSplitReplayPoolStopException("the drain did not empty the pool within " + _drainTicks + " ticks, and the stop itself failed: " + e.GetType().Name + ": " + e.Message, true, false, SpatialSplitReplayPoolStopReason.DrainTimeout, e); }
                throw new SpatialSplitReplayPoolStopException(
                    "the drain did not empty the pool within " + _drainTicks + " ticks (competitors still out: " + _outstanding.Count + "); the dispatcher was shut down (" + _stopMilliseconds + " ms): workers stopped "
                    + (shutdown.workersStopped ? "yes" : "NO") + "; the work itself was collected (its input may be freed); the next condition is not run",
                    true, shutdown.workersStopped, SpatialSplitReplayPoolStopReason.DrainTimeout, null);
            }
            double wall = (Stopwatch.GetTimestamp() - wallBegin) / (double)Stopwatch.Frequency;
            double[] delta = null;
            if (observing)
            {
                double[] busyAfter = BusyNow();
                delta = new double[busyAfter.Length];
                double busySum = 0.0;
                for (int i = 0; i < delta.Length; i++) { delta[i] = busyAfter[i] - busyBefore[i]; busySum += delta[i]; }
                if (Warmup) WarmupBusySeconds += busySum;
            }
            if (Warmup) { WarmupAttempts++; WarmupSeconds += wall; } else { Attempts++; }
            // the competitors that ran during the work's run, and their overlap with it: measured only with the timeline on and the
            // work's own stamps present (null otherwise: not a 0 overlap)
            int? during = null;
            double? overlap = null;
            if (stamping && work is IRunStamped stamped && stamped.BeganAt != 0 && stamped.EndedAt != 0)
            {
                int d = 0;
                double o = 0.0;
                for (int i = competitorsBefore; i < _competitors.Count; i++)
                {
                    CompetitorWork c = _competitors[i];
                    if (c.beganAt == 0 || c.endedAt == 0) continue;
                    long from = Math.Max(c.beganAt, stamped.BeganAt), to = Math.Min(c.endedAt, stamped.EndedAt);
                    if (to > from) { d++; o += (to - from) / (double)Stopwatch.Frequency; }
                }
                during = d;
                overlap = o;
            }
            return new RunRecord { ticks = ticks, blocked = blocked, heldBefore = heldBefore, drained = drained, wallSeconds = wall, competitorsDuring = during, overlapSeconds = overlap, workerBusyDelta = delta };
        }

        // The stop after a timeout: the dispatcher closes, waits for the running worker (the stop timeout) and collects what ended;
        // then the exception, which says whether this work was collected (its input may be freed) and whether the workers stopped.
        private void Stop(IDispatchWork work, Func<bool> isCollected, int ticks)
        {
            _stopped = true;
            DispatchShutdownResult result;
            try { result = _dispatcher.Shutdown(_stopMilliseconds); }
            catch (Exception e) { throw new SpatialSplitReplayPoolStopException("the work was not collected within " + ticks + " ticks, and the stop itself failed: " + e.GetType().Name + ": " + e.Message, false, false, SpatialSplitReplayPoolStopReason.AttemptTimeout, e); }
            bool collected = isCollected();
            throw new SpatialSplitReplayPoolStopException(
                "the work was not collected within " + ticks + " ticks; the dispatcher was shut down (" + _stopMilliseconds + " ms): workers stopped " + (result.workersStopped ? "yes" : "NO")
                + ", the work collected " + (collected ? "yes (its input may be freed)" : "NO (its input and scratch are HELD: a worker may still read them)"),
                collected, result.workersStopped, SpatialSplitReplayPoolStopReason.AttemptTimeout, null);
        }

        private double[] BusyNow()
        {
            var busy = new double[_background.Workers.Count];
            for (int i = 0; i < busy.Length; i++) busy[i] = _background.Workers[i].BusySeconds;
            return busy;
        }

        // One tick's dispatch, then the top-up at the same tick: a competitor the dispatch collected is replaced in a second dispatch
        // of the same tick (the plan's "at the same opportunity as the collection"), so the load holds across the work.
        private void PumpAndTopUp()
        {
            Pump();
            if (TopUp()) Pump();
        }

        private bool TopUp()
        {
            bool added = false;
            while (_outstanding.Count < _level)
            {
                var competitor = new CompetitorWork(_competitorWork, this, _condition, _sample, Warmup);
                if (!_dispatcher.TryEnqueue(WorkPurpose.Speculative, competitor, out WorkTicket _))
                {
                    _competitorsRefused++;
                    break;
                }
                competitor.enqueuedAt = CutSpatialSplit.TimelineClock();
                _outstanding.Add(competitor);
                _competitors.Add(competitor);
                _competitorsIssued++;
                added = true;
            }
            return added;
        }

        private void Pump()
        {
            _dispatcher.BeginFrame(++_frame);
            _dispatcher.Dispatch();
        }

        private bool Drain()
        {
            for (int i = 0; i < _drainTicks; i++)
            {
                if (_outstanding.Count == 0 && _dispatcher.WaitingCountFor(WorkDestination.BackgroundPool) == 0
                    && _dispatcher.SubmittedCountFor(WorkDestination.BackgroundPool) == 0 && _background.Held == 0)
                {
                    Drains++;
                    return true;
                }
                Tick();
                Pump();
            }
            DrainFailures++;
            return false;
        }

        // The next tick: waits for the period from the last tick (Sleep(1) while more than 2 ms remain, then a spin), and records
        // the actual interval -- a common measurement, whatever the timeline's switch.
        private void Tick()
        {
            long due = _lastTick + _tickTicks;
            long now;
            while ((now = Stopwatch.GetTimestamp()) < due)
            {
                if (due - now > Stopwatch.Frequency / 500) Thread.Sleep(1);
                else Thread.SpinWait(64);
            }
            _intervals.Add((now - _lastTick) / (double)Stopwatch.Frequency);
            _lastTick = now;
        }

        internal void NoteCompetitorCollected(CompetitorWork competitor)
        {
            _outstanding.Remove(competitor);
            _competitorsCollected++;
        }

        /// <summary>The run's own record: the level, the ticks' actual intervals, the competitors (by warm-up and by condition), the drains, the pool's workers.</summary>
        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(768);
            text.Append("pool replay: level L").Append(_level).Append(" (competitor work ").Append(_competitorWork).Append(" steps, Speculative; pool capacity ").Append(PoolCapacity)
                .Append(", workers ").Append(_background.Workers.Count).Append("); attempts ").Append(Attempts).Append(" (warm-up ").Append(WarmupAttempts).Append(", ").Append(WarmupSeconds.ToString("F3", inv)).Append(" s wall, ")
                .Append(WarmupBusySeconds.ToString("F3", inv)).Append(" s workers busy)")
                .Append("; ticks ").Append(_intervals.Count).Append(": interval ms median ").Append(Ms(_intervals, 0.5, inv)).Append(", p95 ").Append(Ms(_intervals, 0.95, inv)).Append(", max ").Append(Ms(_intervals, 1.0, inv))
                .Append(" (nominal ").Append((TickSeconds * 1000.0).ToString("F3", inv)).Append(")")
                .Append("; competitors issued ").Append(_competitorsIssued).Append(", refused ").Append(_competitorsRefused).Append(", collected ").Append(_competitorsCollected)
                .Append(" (one line each in competitors.jsonl: condition, sample, warm-up, stamps)");
            foreach (string group in new[] { "warm-up", "D", "C" })
            {
                var waits = new List<double>(); var runs = new List<double>(); var completions = new List<double>(); int missing = 0, count = 0;
                foreach (CompetitorWork c in _competitors)
                {
                    if (group == "warm-up" ? !c.warmup : (c.warmup || c.condition != group)) continue;
                    count++;
                    if (c.enqueuedAt == 0 || c.beganAt == 0 || c.endedAt == 0 || c.collectedAt == 0) { missing++; continue; }
                    double f = Stopwatch.Frequency;
                    waits.Add((c.beganAt - c.enqueuedAt) / f); runs.Add((c.endedAt - c.beganAt) / f); completions.Add((c.collectedAt - c.enqueuedAt) / f);
                }
                if (count == 0) continue;
                text.Append("; competitors ").Append(group).Append(": ").Append(count).Append(" (stamps missing ").Append(missing).Append(")");
                if (waits.Count > 0)
                {
                    text.Append(", wait t2-t0 ms median ").Append(Ms(waits, 0.5, inv)).Append(" p95 ").Append(Ms(waits, 0.95, inv)).Append(" max ").Append(Ms(waits, 1.0, inv))
                        .Append(", run ms median ").Append(Ms(runs, 0.5, inv)).Append(" max ").Append(Ms(runs, 1.0, inv))
                        .Append(", completion t4-t0 ms median ").Append(Ms(completions, 0.5, inv)).Append(" p95 ").Append(Ms(completions, 0.95, inv)).Append(" max ").Append(Ms(completions, 1.0, inv));
                }
            }
            text.Append("; drains ").Append(Drains).Append(" (not emptied within ").Append(_drainTicks).Append(" ticks: ").Append(DrainFailures).Append(")")
                .Append("; observation ").Append(DispatchObservation.Enabled ? "on" : "OFF (blocked counts, busy deltas: not measured)").Append(", timeline ").Append(CutSpatialSplit.TimelineObserve ? "on" : "OFF (stamps, overlaps: not measured)")
                .Append("; stopped by a timeout: ").Append(_stopped ? "YES" : "no")
                .Append("; dispatcher submitted/collected: Maintenance ").Append(_dispatcher.SubmittedByPurpose(WorkPurpose.Maintenance)).Append('/').Append(_dispatcher.CollectedByPurpose(WorkPurpose.Maintenance))
                .Append(", Speculative ").Append(_dispatcher.SubmittedByPurpose(WorkPurpose.Speculative)).Append('/').Append(_dispatcher.CollectedByPurpose(WorkPurpose.Speculative))
                .Append("; capacity-blocked opportunities (Background) ").Append(_dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool))
                .Append("; workers busy s (the whole run, warm-up included; the per-attempt deltas are on the lines):");
            foreach (WorkerPoolExecutor.WorkerObservation w in _background.Workers) text.Append(' ').Append(w.BusySeconds.ToString("F3", inv)).Append(" (").Append(w.Executed).Append(" works)");
            return text.ToString();
        }

        /// <summary>One line a competitor (condition, sample, warm-up, the stamps as ms from the run's origin or null, the intervals or null).</summary>
        public IEnumerable<string> CompetitorLines()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            double toMs = 1000.0 / Stopwatch.Frequency;
            string Stamp(long t) => t == 0 ? "null" : ((t - _origin) * toMs).ToString("F3", inv);
            string Interval(long a, long b) => a == 0 || b == 0 ? "null" : ((b - a) * toMs).ToString("F3", inv);
            int index = 0;
            foreach (CompetitorWork c in _competitors)
            {
                yield return "{\"index\":" + index++ + ",\"condition\":" + SpatialSplitCaptureSink.Json(c.condition) + ",\"sample\":" + SpatialSplitCaptureSink.Json(c.sample) + ",\"warmup\":" + (c.warmup ? "true" : "false")
                    + ",\"steps\":" + _competitorWork + ",\"t0Ms\":" + Stamp(c.enqueuedAt) + ",\"t1Ms\":" + Stamp(c.acceptedAt) + ",\"t2Ms\":" + Stamp(c.beganAt) + ",\"t3Ms\":" + Stamp(c.endedAt) + ",\"t4Ms\":" + Stamp(c.collectedAt)
                    + ",\"waitMs\":" + Interval(c.enqueuedAt, c.beganAt) + ",\"runMs\":" + Interval(c.beganAt, c.endedAt) + ",\"completionMs\":" + Interval(c.enqueuedAt, c.collectedAt)
                    + ",\"collected\":" + (c.collectedAt != 0 || c.collectedWithoutClock ? "true" : "false") + "}";
            }
        }

        private static string Ms(List<double> values, double quantile, CultureInfo inv)
        {
            if (values.Count == 0) return "-";
            var copy = new List<double>(values);
            copy.Sort();
            int at = quantile >= 1.0 ? copy.Count - 1 : Math.Min(copy.Count - 1, (int)(copy.Count * quantile));
            return (copy[at] * 1000.0).ToString("F3", inv);
        }

        public void Dispose()
        {
            if (!_stopped)
            {
                try { _dispatcher.Shutdown(_stopMilliseconds); }
                catch (Exception) { /* the pool is disposed below either way */ }
            }
            _background.Dispose();
        }

        /// <summary>A work whose own run is stamped (Begin's entry and end), for the overlap with the competitors.</summary>
        internal interface IRunStamped
        {
            long BeganAt { get; }
            long EndedAt { get; }
        }

        /// <summary>The competitor: a work of a fixed amount of steps (a xorshift loop: deterministic, not a timed spin), stamped like the attempt, with the attempt's condition and sample.</summary>
        internal sealed class CompetitorWork : IDispatchWork, IDispatchObserved, IRunStamped
        {
            private readonly long _steps;
            private readonly SpatialSplitReplayPool _owner;
            internal readonly string condition, sample;
            internal readonly bool warmup;
            internal long enqueuedAt, acceptedAt, beganAt, endedAt, collectedAt;
            internal bool collectedWithoutClock;
            private ulong _result;

            /// <summary>The last competitor's result: read nowhere, written so that the loop is not removed.</summary>
            public static ulong Sink;

            public CompetitorWork(long steps, SpatialSplitReplayPool owner, string condition, string sample, bool warmup)
            {
                _steps = steps;
                _owner = owner;
                this.condition = condition;
                this.sample = sample;
                this.warmup = warmup;
            }

            public long BeganAt => beganAt;
            public long EndedAt => endedAt;

            public void Begin()
            {
                beganAt = CutSpatialSplit.TimelineClock();
                ulong x = 88172645463325252UL;
                for (long i = 0; i < _steps; i++)
                {
                    x ^= x << 13;
                    x ^= x >> 7;
                    x ^= x << 17;
                }
                _result = x;
                Sink = x;
                endedAt = CutSpatialSplit.TimelineClock();
            }

            public bool IsComplete => true;

            public void Collect(WorkCompletion completion)
            {
                collectedAt = CutSpatialSplit.TimelineClock();
                collectedWithoutClock = collectedAt == 0;
                _owner.NoteCompetitorCollected(this);
            }

            public void NoteAccepted(long timestamp)
            {
                if (acceptedAt == 0) acceptedAt = timestamp;
            }
        }

        /// <summary>Tests: the attempt behind a gate -- Begin waits at it before the attempt's own Begin, so a worker holds the input for as long as the test wants.</summary>
        private sealed class GatedWork : IDispatchWork, IDispatchObserved
        {
            private readonly CutSpatialSplit.Attempt _attempt;
            private readonly ManualResetEventSlim _gate;
            public GatedWork(CutSpatialSplit.Attempt attempt, ManualResetEventSlim gate) { _attempt = attempt; _gate = gate; }
            public void Begin() { _gate.Wait(); _attempt.Begin(); }
            public bool IsComplete => _attempt.IsComplete;
            public void Collect(WorkCompletion completion) => _attempt.Collect(completion);
            public void NoteAccepted(long timestamp) => _attempt.NoteAccepted(timestamp);
        }

        /// <summary>A destination that takes nothing: stands in for the Geometry pool, which this replay does not use.</summary>
        private sealed class IdleExecutor : IWorkExecutor
        {
            public IdleExecutor(WorkDestination destination) { Destination = destination; }
            public WorkDestination Destination { get; }
            public int Capacity => 1;
            public int Held => 0;
            public bool CanAccept => false;
            public bool TryAccept(IDispatchWork work) => false;
            public void BeginAccepted(IDispatchWork work) { }
            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion) { work = null; completion = default; return false; }
            public void CloseForNewWork() { }
            public bool StopAndConfirm(int timeoutMilliseconds) => true;
        }
    }

    /// <summary>
    /// The pool replay stopped: a work was not collected within the attempt timeout, the dispatcher was shut down. <see cref="WorkCollected"/>
    /// says whether the work came back (its input may be freed); <see cref="WorkersStopped"/> whether the pool's workers confirmed their
    /// stop. The session does not go on to the next sample after this.
    /// </summary>
    internal sealed class SpatialSplitReplayPoolStopException : Exception
    {
        public SpatialSplitReplayPoolStopException(string message, bool workCollected, bool workersStopped, SpatialSplitReplayPoolStopReason reason, Exception inner) : base(message, inner)
        {
            WorkCollected = workCollected;
            WorkersStopped = workersStopped;
            Reason = reason;
        }

        /// <summary>Whether the work handed in came back (its input may be freed). Apart from <see cref="WorkersStopped"/>: a drain that failed leaves the work collected and a competitor out.</summary>
        public bool WorkCollected { get; }

        /// <summary>Whether the pool's workers confirmed their stop within the stop timeout (false: a worker is still running something).</summary>
        public bool WorkersStopped { get; }

        public SpatialSplitReplayPoolStopReason Reason { get; }
    }

    internal enum SpatialSplitReplayPoolStopReason { AttemptTimeout, DrainTimeout, AlreadyStopped }

    /// <summary>One attempt's pool record (GRAPH-CONNECT-14 §3-1): the condition, the level, the stamps (ticks; 0 absent), the ticks it took, the capacity-blocked opportunities met, the competitors during its run and their overlap, the workers' busy deltas, the drain.</summary>
    internal sealed class SpatialSplitPoolRecord
    {
        internal string condition;
        internal int level;
        internal long competitorWork;
        internal bool warmup, drained;
        internal long t0, t1, t2, t3, t4;
        internal int ticks, heldBefore;
        internal int? blocked, competitorsDuring;   // null: not measured (the observation / the timeline off)
        internal double wallSeconds;
        internal double? overlapSeconds;            // null: not measured
        internal double[] workerBusyDeltaSeconds;   // null: not measured

        /// <summary>The record as JSON: the stamps as ms from the run's origin (null where absent), the intervals where both stamps are present (null otherwise).</summary>
        internal string Json(long origin, CultureInfo inv)
        {
            double toMs = 1000.0 / Stopwatch.Frequency;
            string Stamp(long t) => t == 0 ? "null" : ((t - origin) * toMs).ToString("F3", inv);
            string Interval(long a, long b) => a == 0 || b == 0 ? "null" : ((b - a) * toMs).ToString("F3", inv);
            string busy = "null";
            if (workerBusyDeltaSeconds != null)
            {
                var b = new StringBuilder("[");
                for (int i = 0; i < workerBusyDeltaSeconds.Length; i++) b.Append(i > 0 ? "," : "").Append((workerBusyDeltaSeconds[i] * 1000.0).ToString("F3", inv));
                busy = b.Append(']').ToString();
            }
            return "{\"level\":" + level + ",\"competitorWork\":" + competitorWork + ",\"warmup\":" + (warmup ? "true" : "false")
                + ",\"t0Ms\":" + Stamp(t0) + ",\"t1Ms\":" + Stamp(t1) + ",\"t2Ms\":" + Stamp(t2) + ",\"t3Ms\":" + Stamp(t3) + ",\"t4Ms\":" + Stamp(t4)
                + ",\"w1Ms\":" + Interval(t0, t1) + ",\"w2Ms\":" + Interval(t1, t2) + ",\"xMs\":" + Interval(t2, t3) + ",\"w3Ms\":" + Interval(t3, t4)
                + ",\"ticks\":" + ticks + ",\"blocked\":" + (blocked.HasValue ? blocked.Value.ToString(inv) : "null") + ",\"heldBefore\":" + heldBefore + ",\"drained\":" + (drained ? "true" : "false")
                + ",\"competitorsDuring\":" + (competitorsDuring.HasValue ? competitorsDuring.Value.ToString(inv) : "null")
                + ",\"overlapMs\":" + (overlapSeconds.HasValue ? (overlapSeconds.Value * 1000.0).ToString("F3", inv) : "null")
                + ",\"workerBusyDeltaMs\":" + busy + ",\"wallMs\":" + (wallSeconds * 1000.0).ToString("F3", inv)
                + ",\"observed\":{\"dispatch\":" + (blocked.HasValue ? "true" : "false") + ",\"timeline\":" + (t0 != 0 ? "true" : "false") + "}}";
        }
    }
}
#endif
