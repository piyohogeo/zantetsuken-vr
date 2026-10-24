#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The intervals of an attempt's way through the shared dispatch (GRAPH-CONNECT-14 §2), each between two of its stamps:
    /// t0 the issue's enqueue (Main), t1 the pool's acceptance (Main, inside the pool's accept lock), t2 the worker's Begin,
    /// t3 the worker's end, t4 the Main collection, t5 the first Main handling of the result, t6 the end on Main.
    /// </summary>
    public enum SpatialSplitTimelineInterval
    {
        /// <summary>W1: t1 − t0, the wait in the dispatcher's own queue until the pool took the work.</summary>
        DispatcherWait = 0,
        /// <summary>W2: t2 − t1, the wait in the pool's queue until a worker began it.</summary>
        PoolWait,
        /// <summary>X: t3 − t2, the worker's run (the existing workerSeconds).</summary>
        WorkerRun,
        /// <summary>W3: t4 − t3, from the worker's end to the Main collection.</summary>
        CollectWait,
        /// <summary>W4: t5 − t4, from the collection to the first Main handling of the result.</summary>
        HandleWait,
        /// <summary>S: t6 − t5, the Main handling's span (several turns for an attempt that cuts convexes).</summary>
        HandleSpan,
        Count,
    }

    /// <summary>One interval's tallies: how many attempts had both stamps, their sum and their longest, and fixed bins (ms).</summary>
    public struct SpatialSplitIntervalStat
    {
        public long count;
        public double sumSeconds, maxSeconds;
        public int[] bins;

        internal void Add(double seconds, double[] binUpperMs)
        {
            if (bins == null) bins = new int[binUpperMs.Length];
            count++;
            sumSeconds += seconds;
            if (seconds > maxSeconds) maxSeconds = seconds;
            double ms = seconds * 1000.0;
            for (int i = 0; i < binUpperMs.Length; i++)
            {
                if (ms <= binUpperMs[i]) { bins[i]++; return; }
            }
            bins[binUpperMs.Length - 1]++;
        }

        /// <summary>The mean of the attempts that had the interval (not a price: a mean of this run's observations), or NaN.</summary>
        public double MeanSeconds => count > 0 ? sumSeconds / count : double.NaN;
    }

    /// <summary>The stamps of one attempt (Stopwatch ticks; 0 where not taken), for the record and the tests.</summary>
    public struct SpatialSplitTimelineRecord
    {
        public long serial;
        public SpatialSplitEnd end;
        public long t0, t1, t2, t3, t4, t5, t6;
        public bool bent, graph, meshLed, cut, graphLed;
        public double workerSeconds, bentSeconds, graphSeconds;

        public long Stamp(int index)
        {
            switch (index)
            {
                case 0: return t0;
                case 1: return t1;
                case 2: return t2;
                case 3: return t3;
                case 4: return t4;
                case 5: return t5;
                default: return t6;
            }
        }

        /// <summary>Whether every present stamp is in order (an absent stamp is skipped, never read as 0).</summary>
        public bool InOrder()
        {
            long last = 0;
            for (int i = 0; i <= 6; i++)
            {
                long s = Stamp(i);
                if (s == 0) continue;
                if (s < last) return false;
                last = s;
            }
            return true;
        }

        /// <summary>The interval's seconds where both of its stamps are present, or NaN.</summary>
        public double Seconds(SpatialSplitTimelineInterval interval)
        {
            int a, b;
            switch (interval)
            {
                case SpatialSplitTimelineInterval.DispatcherWait: a = 0; b = 1; break;
                case SpatialSplitTimelineInterval.PoolWait: a = 1; b = 2; break;
                case SpatialSplitTimelineInterval.WorkerRun: a = 2; b = 3; break;
                case SpatialSplitTimelineInterval.CollectWait: a = 3; b = 4; break;
                case SpatialSplitTimelineInterval.HandleWait: a = 4; b = 5; break;
                default: a = 5; b = 6; break;
            }
            long from = Stamp(a), to = Stamp(b);
            if (from == 0 || to == 0) return double.NaN;
            return (to - from) / (double)Stopwatch.Frequency;
        }
    }

    public sealed partial class CutSpatialSplit
    {
        /// <summary>The upper edges of the interval bins, ms (the last takes everything above).</summary>
        public static readonly double[] TimelineBinUpperMs = { 0.1, 0.5, 1.0, 2.0, 5.0, 11.1, 22.2, 50.0, 100.0, double.PositiveInfinity };

        /// <summary>The clock reading of a new timeline stamp: 0 when the timeline is not observed (the existing clocks are not these).</summary>
        internal static long TimelineClock()
        {
#if ZANTETSU_OBSERVE
            return TimelineObserve ? Stopwatch.GetTimestamp() : 0;
#else
            return 0;
#endif
        }

#if ZANTETSU_OBSERVE
        /// <summary>
        /// Whether the new stamps and tallies of GRAPH-CONNECT-14 are taken (development builds). Off (<c>-zantetsuSplitTimelineOff</c>):
        /// t0, t1, t4, t5, t6 are not read, nothing is tallied or kept, and the description says so; the existing clocks (the
        /// worker's run, the bent and graph stages, the publishing turn) stay as they were.
        /// </summary>
        public static bool TimelineObserve = true;

        /// <summary>Where the per-attempt rows go at the world's end (<c>-zantetsuSplitTimeline &lt;file&gt;</c>); null: no rows are kept.</summary>
        public static string TimelineCsvPath;

        private const int TimelineRowRoom = 65536;
        private readonly SpatialSplitIntervalStat[] _timeline = new SpatialSplitIntervalStat[(int)SpatialSplitTimelineInterval.Count];
        private readonly long[] _timelineEnds = new long[(int)SpatialSplitEnd.Count];
        private readonly List<SpatialSplitTimelineRecord> _timelineRows = new List<SpatialSplitTimelineRecord>();
        private long _timelineRowsNotKept;

        // The queue's state as the split's turn saw it (GRAPH-CONNECT-14 §2, the dwell): sampled once a Step while the split is on.
        private long _queueSamples, _queueWaitingSum, _queueSubmittedSum;
        private int _queueWaitingMax, _queueSubmittedMax;
        private readonly long[] _heldHistogram = new long[4];   // the Background pool's Held 0, 1, 2, 3 or more

        /// <summary>Attempts whose end was tallied with a timeline (t0 present).</summary>
        public long TimelineStamped { get; private set; }

        /// <summary>The last ended attempt's stamps (tests, the record).</summary>
        public SpatialSplitTimelineRecord LastTimeline { get; private set; }

        /// <summary>One interval's tallies over this world.</summary>
        public SpatialSplitIntervalStat Timeline(SpatialSplitTimelineInterval interval) => _timeline[(int)interval];

        private void NoteTimeline(Attempt attempt, SpatialSplitEnd end)
        {
            var record = new SpatialSplitTimelineRecord
            {
                serial = attempt.serial, end = end,
                t0 = attempt.enqueuedAt, t1 = attempt.acceptedAt, t2 = attempt.beganAt, t3 = attempt.endedAt, t4 = attempt.collectedAt,
                t5 = attempt.handledAt, t6 = attempt.handledEndAt,
                bent = attempt.search.bent, graph = attempt.graph.Entered, meshLed = attempt.search.meshLed, cut = attempt.cut, graphLed = attempt.graphLed,
                workerSeconds = attempt.workerSeconds, bentSeconds = attempt.bentSeconds, graphSeconds = attempt.graph.seconds,
            };
            LastTimeline = record;
            if (!TimelineObserve || record.t0 == 0)
            {
                return;   // not observed, or an attempt from before the switch: nothing tallied (never a 0 interval)
            }
            TimelineStamped++;
            _timelineEnds[(int)end]++;
            for (int i = 0; i < (int)SpatialSplitTimelineInterval.Count; i++)
            {
                double seconds = record.Seconds((SpatialSplitTimelineInterval)i);
                if (!double.IsNaN(seconds)) _timeline[i].Add(seconds, TimelineBinUpperMs);
            }
            if (TimelineCsvPath != null)
            {
                if (_timelineRows.Count < TimelineRowRoom) _timelineRows.Add(record); else _timelineRowsNotKept++;
            }
        }

        private void SampleQueue()
        {
            if (!TimelineObserve || _world.Dispatcher == null)
            {
                return;
            }
            int waiting = _world.Dispatcher.WaitingCountFor(WorkDestination.BackgroundPool);
            int submitted = _world.Dispatcher.SubmittedCountFor(WorkDestination.BackgroundPool);
            IWorkExecutor pool = _world.Dispatcher.Executor(WorkDestination.BackgroundPool);
            int held = pool != null ? pool.Held : 0;
            _queueSamples++;
            _queueWaitingSum += waiting;
            _queueSubmittedSum += submitted;
            if (waiting > _queueWaitingMax) _queueWaitingMax = waiting;
            if (submitted > _queueSubmittedMax) _queueSubmittedMax = submitted;
            _heldHistogram[Math.Min(3, Math.Max(0, held))]++;
        }

        /// <summary>
        /// The timeline's tallies (GRAPH-CONNECT-14 §2): per interval the count of attempts that had both stamps, the mean and the
        /// longest (ms), the bins; the ends tallied; the queue's dwell as the turns saw it; the Background pool's workers' busy time;
        /// the dispatcher's counts by purpose and its capacity-blocked opportunities. A missing stamp is never a 0 interval.
        /// </summary>
        public string DescribeTimeline()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(512);
            text.Append("timeline (").Append(TimelineObserve ? "on" : "off: the new stamps and tallies are not taken; the existing clocks stand").Append("): attempts stamped ").Append(TimelineStamped);
            if (TimelineObserve)
            {
                text.Append("; ends:");
                for (int i = 0; i < _timelineEnds.Length; i++) if (_timelineEnds[i] > 0) text.Append(' ').Append((SpatialSplitEnd)i).Append(' ').Append(_timelineEnds[i]);
                string[] names = { "W1 dispatcher wait", "W2 pool wait", "X worker run", "W3 collect wait", "W4 handle wait", "S handle span" };
                for (int i = 0; i < (int)SpatialSplitTimelineInterval.Count; i++)
                {
                    SpatialSplitIntervalStat s = _timeline[i];
                    text.Append("; ").Append(names[i]).Append(": n ").Append(s.count);
                    if (s.count > 0)
                    {
                        text.Append(", mean ").Append((s.MeanSeconds * 1000.0).ToString("F3", inv)).Append(" ms, max ").Append((s.maxSeconds * 1000.0).ToString("F3", inv)).Append(" ms, bins");
                        for (int b = 0; b < s.bins.Length; b++) text.Append(' ').Append(s.bins[b]);
                    }
                }
                text.Append("; queue dwell (").Append(_queueSamples).Append(" turns): waiting mean ").Append(_queueSamples > 0 ? (_queueWaitingSum / (double)_queueSamples).ToString("F3", inv) : "-")
                    .Append(" max ").Append(_queueWaitingMax).Append(", submitted mean ").Append(_queueSamples > 0 ? (_queueSubmittedSum / (double)_queueSamples).ToString("F3", inv) : "-")
                    .Append(" max ").Append(_queueSubmittedMax).Append(", pool held 0/1/2/3+ ").Append(_heldHistogram[0]).Append('/').Append(_heldHistogram[1]).Append('/').Append(_heldHistogram[2]).Append('/').Append(_heldHistogram[3]);
                if (TimelineCsvPath != null) text.Append("; rows kept ").Append(_timelineRows.Count).Append(" (not kept ").Append(_timelineRowsNotKept).Append(") for ").Append(TimelineCsvPath);
            }
            SharedWorkDispatcher dispatcher = _world.Dispatcher;
            if (dispatcher != null)
            {
                text.Append("; dispatcher submitted/collected by purpose:");
                for (int p = 0; p <= (int)WorkPurpose.MobPlanning; p++)
                {
                    int sub = dispatcher.SubmittedByPurpose((WorkPurpose)p), col = dispatcher.CollectedByPurpose((WorkPurpose)p);
                    if (sub > 0 || col > 0) text.Append(' ').Append((WorkPurpose)p).Append(' ').Append(sub).Append('/').Append(col);
                }
                text.Append("; capacity-blocked opportunities: Background ").Append(dispatcher.CapacityBlockedOpportunities(WorkDestination.BackgroundPool))
                    .Append(", Geometry ").Append(dispatcher.CapacityBlockedOpportunities(WorkDestination.GeometryPool));
                if (dispatcher.Executor(WorkDestination.BackgroundPool) is WorkerPoolExecutor pool)
                {
                    text.Append("; Background workers busy s:");
                    foreach (WorkerPoolExecutor.WorkerObservation w in pool.Workers) text.Append(' ').Append(w.BusySeconds.ToString("F3", inv)).Append(" (").Append(w.Executed).Append(" works)");
                }
            }
            return text.ToString();
        }

        /// <summary>Writes the kept rows (one attempt a line, ms from the first t0; an absent stamp is empty) to <see cref="TimelineCsvPath"/>, at the world's end.</summary>
        public void WriteTimelineCsv()
        {
            if (TimelineCsvPath == null || !TimelineObserve) return;
            CultureInfo inv = CultureInfo.InvariantCulture;
            double toMs = 1000.0 / Stopwatch.Frequency;
            long origin = 0;
            foreach (SpatialSplitTimelineRecord r in _timelineRows) if (r.t0 != 0 && (origin == 0 || r.t0 < origin)) origin = r.t0;
            var text = new StringBuilder();
            text.AppendLine("serial,end,t0Ms,t1Ms,t2Ms,t3Ms,t4Ms,t5Ms,t6Ms,w1Ms,w2Ms,xMs,w3Ms,w4Ms,sMs,bent,graph,meshLed,cut,graphLed,workerMs,bentMs,graphMs");
            foreach (SpatialSplitTimelineRecord r in _timelineRows)
            {
                text.Append(r.serial).Append(',').Append(r.end);
                for (int i = 0; i <= 6; i++) { long s = r.Stamp(i); text.Append(',').Append(s == 0 ? "" : ((s - origin) * toMs).ToString("F3", inv)); }
                for (int i = 0; i < (int)SpatialSplitTimelineInterval.Count; i++) { double v = r.Seconds((SpatialSplitTimelineInterval)i); text.Append(',').Append(double.IsNaN(v) ? "" : (v * 1000.0).ToString("F3", inv)); }
                text.Append(',').Append(r.bent ? 1 : 0).Append(',').Append(r.graph ? 1 : 0).Append(',').Append(r.meshLed ? 1 : 0).Append(',').Append(r.cut ? 1 : 0).Append(',').Append(r.graphLed ? 1 : 0)
                    .Append(',').Append((r.workerSeconds * 1000.0).ToString("F3", inv)).Append(',').Append((r.bentSeconds * 1000.0).ToString("F3", inv)).Append(',').Append((r.graphSeconds * 1000.0).ToString("F3", inv));
                text.AppendLine();
            }
            string directory = Path.GetDirectoryName(TimelineCsvPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(TimelineCsvPath, text.ToString(), new UTF8Encoding(false));
        }
#else
        /// <summary>Nothing in a Release Player: the timeline is development-only (DESIGN 21.18).</summary>
        public string DescribeTimeline() => "timeline (not observed in this build)";

        public void WriteTimelineCsv() { }
#endif
    }
}
