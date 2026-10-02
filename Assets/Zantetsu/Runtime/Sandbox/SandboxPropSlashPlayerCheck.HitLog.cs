using System.Diagnostics;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // DIAGNOSIS (2026-10-02): the hits the check observed in the multi-NPC, building and MobPlan modes, one record each of the
    // development logger (DESIGN 21.17, writer "CheckHits"), where multi-hits.csv / building-hits.csv were written before.
    // Each walk (each iteration of -zantetsuPropIterations) is one recording of its own: a "start" record, a "hit" record per
    // observed hit, and the "summary" that ends it at the records' close (CheckEnding "records close"), before the quit
    // where the logger stops; nothing is recorded after it. The check's judgements are not read from it: they stay on the
    // tallies in memory, and the required hit Trace (slash-hits.ztrace) is its own path.
    //
    // A hit record's fields: eventFrame -- the frame the check observed the hit in (its LateUpdate, order 400, reading the
    // detector's new hit list), the same value as the old CSV's "frame"; it is not the frame the hit was evaluated in, which
    // the hit does not carry and is not made up here. seconds -- Stopwatch at that observation, from the recording's start.
    // slashId, of (the lineage's name), child (1 = the fragment has an origin), fragment, acceptance, admission, operation --
    // as the old CSV. at -- SlashHitConfirmed.At: the input time of the wave update whose sweep hit, in seconds on the replay's
    // input clock (Time.unscaledTimeAsDouble at the replay's start plus each replayed row's recorded offset, then plus each
    // tick's unscaled delta), a clock of its own, not the Stopwatch. side -- SlashHitConfirmed.Side: +1 or -1 for a side of a
    // Provisional pair, 0 for a published owner (no unit). update -- the recorder's replay index minus one when the hit was
    // observed: the index of the last replayed input row (from 0; it stays at the last row while the ticks go on).
    public static partial class SandboxPropSlashPlayerCheck
    {
        internal const string HitLogWriter = "CheckHits";
        internal const string HitLogLabel = "check hits";

        private static int s_hitRecordings;

        /// <summary>Hit recordings begun this session (the last one's number).</summary>
        internal static int HitRecordings => s_hitRecordings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetHitLogForSession()
        {
            s_hitRecordings = 0;
        }

        /// <summary>A recording of hits: begun with its "start" record. Tests make one directly.</summary>
        internal static DevelopmentLogRecording BeginHitLog(string mode, int iteration, string directory)
        {
            var log = new DevelopmentLogRecording(HitLogWriter, HitLogLabel, ++s_hitRecordings, Stopwatch.GetTimestamp());
            log.Write("start", Time.frameCount, Stopwatch.GetTimestamp(), new object[]
            {
                null, null, null, null, null, null, null, null,
                "mode", mode, "iteration", iteration, "directory", directory,
            });
            return log;
        }

        /// <summary>One observed hit, its frame and the replay index as the check observed them.</summary>
        internal static void WriteHit(DevelopmentLogRecording log, in SlashHitConfirmed hit, int frame, string of, bool child, int update)
        {
            if (log == null || log.Ended)
            {
                return;
            }

            log.Write("hit", frame, Stopwatch.GetTimestamp(), new object[]
            {
                null, null, null, null, null, null, null, null,
                "slashId", hit.SlashId, "of", of, "child", child ? 1 : 0, "fragment", hit.Fragment.value,
                "acceptance", hit.Acceptance.ToString(), "admission", hit.Admission.ToString(), "operation", hit.Operation.value,
                "at", hit.At, "side", hit.Side, "update", update,
            });
        }

        /// <summary>
        /// One accepted cut as the check followed it to the end, an "op" record (formerly a row of multi-ops.csv /
        /// building-ops.csv), written at the run's summary. Every stage is what the check observed: acceptedFrame the frame it
        /// observed the hit in (the hit's eventFrame; for a held root taken up, the frame it observed the take-up), the
        /// others the frames it observed the Provisional publication, the operation leaving Admitted (finalFrame) and the
        /// geometry Committed, -1 when not observed; the times are the check's clock at those observations
        /// (Time.unscaledTimeAsDouble since the replay's start, seconds; NaN when not observed, written null); ledgerState and
        /// the children are the ledger's at the summary. Of the multi-NPC mode: of, child; of the building mode: the source's
        /// depth, anchors and fixedness, the cut's kind and world normal as recorded at the acceptance. Null where the mode
        /// has none. name, hull and fusion are the check's own (a held root's name ends "-held"; hull / fusion: a Pending
        /// acceptance answered by a hull group or a fusion, with no operation of its own: operation 0).
        /// </summary>
        internal struct OpRecord
        {
            public string name;
            public int operation;
            public long slash;
            public int source;
            public string acceptance;
            public int acceptedFrame, provisionalFrame, finalFrame, committedFrame;
            public double acceptedTime, provisionalTime, finalTime, committedTime;
            public string ledgerState, pendingEnd;
            public bool hull, fusion;
            public string of;
            public bool? child;
            public int? sourceDepth, sourceAnchors;
            public bool? sourceFixed;
            public string cut;
            public float? normalX, normalY, normalZ;
            public int? positive, negative;
        }

        /// <summary>The ops of a recording: whether its mode writes them, how many were to be written (fixed from the accepted
        /// list before the writing), how many were tried and how many the logger accepted.</summary>
        internal struct OpsTally
        {
            public bool applicable;
            public int? planned;
            public int attempted;
            public long accepted;
        }

        /// <summary>One op record (at the summary: eventFrame and seconds are the summary's).</summary>
        internal static void WriteOp(DevelopmentLogRecording log, in OpRecord r)
        {
            if (log == null || log.Ended)
            {
                return;
            }

            log.Write("op", Time.frameCount, Stopwatch.GetTimestamp(), new object[]
            {
                null, null, null, null, null, null, null, null,
                "name", r.name, "operation", r.operation, "slash", r.slash, "source", r.source, "acceptance", r.acceptance,
                "acceptedFrame", r.acceptedFrame, "provisionalFrame", r.provisionalFrame, "finalFrame", r.finalFrame, "committedFrame", r.committedFrame,
                "acceptedTime", Seconds(r.acceptedTime), "provisionalTime", Seconds(r.provisionalTime), "finalTime", Seconds(r.finalTime), "committedTime", Seconds(r.committedTime),
                "ledgerState", r.ledgerState, "pendingEnd", r.pendingEnd, "hull", r.hull ? 1 : 0, "fusion", r.fusion ? 1 : 0,
                "of", r.of, "child", r.child.HasValue ? (object)(r.child.Value ? 1 : 0) : null,
                "sourceDepth", r.sourceDepth, "sourceAnchors", r.sourceAnchors, "sourceFixed", r.sourceFixed.HasValue ? (object)(r.sourceFixed.Value ? 1 : 0) : null,
                "cut", r.cut, "normalX", r.normalX, "normalY", r.normalY, "normalZ", r.normalZ, "positive", r.positive, "negative", r.negative,
            });
        }

        private static object Seconds(double value) => double.IsNaN(value) ? null : (object)value;

        /// <summary>Fixes how many ops are to be written (from the accepted list, before any is written); nothing when not applicable.</summary>
        internal static void PlanOps(DevelopmentLogRecording log, ref OpsTally ops, int planned)
        {
            if (log == null || log.Ended || !ops.applicable) return;
            ops.planned = planned;
        }

        /// <summary>One op record, counted: tried, and accepted by the logger or not.</summary>
        internal static void WriteOpCounted(DevelopmentLogRecording log, ref OpsTally ops, in OpRecord r)
        {
            if (log == null || log.Ended || !ops.applicable) return;
            ops.attempted++;
            long before = log.Accepted;
            WriteOp(log, in r);
            ops.accepted += log.Accepted - before;
        }

        /// <summary>Ends a recording of hits with no ops in its mode (a test's, or the MobPlan mode's).</summary>
        internal static void EndHitLog(DevelopmentLogRecording log, int hits) => EndHitLog(log, hits, default);

        /// <summary>Ends a recording of hits: the summary, the last record, with the hits and the ops' tally.</summary>
        internal static void EndHitLog(DevelopmentLogRecording log, int hits, in OpsTally ops)
        {
            string detail = "hits " + hits + "; ops " + (!ops.applicable ? "not applicable"
                : !ops.planned.HasValue ? "applicable, not written (no summary reached them)"
                : "planned " + ops.planned.Value + ", attempted " + ops.attempted + ", accepted " + ops.accepted);
            log?.End(Time.frameCount, Stopwatch.GetTimestamp(), new object[]
            {
                "hits", hits, "opsApplicable", ops.applicable ? 1 : 0, "opsPlanned", ops.planned, "opsAttempted", ops.attempted, "opsAccepted", ops.accepted,
            }, detail);
        }

        private sealed partial class Walk
        {
            private DevelopmentLogRecording _hitLog;
            private int _hitLogHits;
            private OpsTally _ops;

            // One recording a walk: a second open (two modes at once) keeps the first rather than leave it without its end.
            private void HitLogOpen(string mode)
            {
                if (_hitLog != null)
                {
                    Log("hit record: already recording (recording " + _hitLog.Recording + "), " + mode + " not opened again");
                    return;
                }

                _hitLog = BeginHitLog(mode, iteration, directory);
                _hitLogHits = 0;
                // The modes whose summary wrote ops before (multi-ops.csv, building-ops.csv); the MobPlan mode never did.
                _ops = new OpsTally { applicable = mode == "multiNpc" || mode == "building" };
            }

            private void HitLogHit(in SlashHitConfirmed hit, int frame, string of, bool child, int update)
            {
                if (_hitLog == null || _hitLog.Ended)
                {
                    return;
                }

                _hitLogHits++;
                WriteHit(_hitLog, hit, frame, of, child, update);
            }

            // The ops of the summary: how many are to be written, fixed from the accepted list before any is written.
            private void HitLogOpsPlanned(int planned) => PlanOps(_hitLog, ref _ops, planned);

            private void HitLogOp(in OpRecord r) => WriteOpCounted(_hitLog, ref _ops, in r);

            private void HitLogEnd()
            {
                EndHitLog(_hitLog, _hitLogHits, in _ops);
            }
        }
    }
}
