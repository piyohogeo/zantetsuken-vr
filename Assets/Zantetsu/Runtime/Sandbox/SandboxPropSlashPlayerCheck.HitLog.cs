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

        /// <summary>Ends a recording of hits: the summary with the hits recorded, the last record.</summary>
        internal static void EndHitLog(DevelopmentLogRecording log, int hits)
        {
            log?.End(Time.frameCount, Stopwatch.GetTimestamp(), new object[] { "hits", hits }, "hits " + hits);
        }

        private sealed partial class Walk
        {
            private DevelopmentLogRecording _hitLog;
            private int _hitLogHits;

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

            private void HitLogEnd()
            {
                EndHitLog(_hitLog, _hitLogHits);
            }
        }
    }
}
