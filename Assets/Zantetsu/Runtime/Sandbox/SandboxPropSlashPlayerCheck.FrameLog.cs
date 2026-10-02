using System.Diagnostics;
using UnityEngine;
using Zantetsu.Core;

namespace Zantetsu.Sandbox
{
    // DIAGNOSIS (2026-10-02): the check's per-frame row of the multi-NPC and MobPlan modes, one record each of the development
    // logger (DESIGN 21.17, writer "CheckFrames"), where multi.csv was written before. A writer and a recording of its own,
    // apart from the walk's CheckHits recording, so that a gap in the many per-frame records leaves the hits and ops usable;
    // the start names that recording (hitRecording), the walk's mode, iteration and directory. A "frame" record per replay
    // frame the check ran MultiFrame in, and the "summary" that ends it at the records' close; nothing after it. The logger's
    // queue is shared with every other writer: a recording apart does not separate its load.
    //
    // A frame record: eventFrame -- the frame the check ran MultiFrame in, its LateUpdate (order 400), the old CSV's "frame".
    // seconds -- Stopwatch at that moment, from this recording's start. The old columns as they were: real -- the check's
    // clock, Time.unscaledTimeAsDouble since the replay's start (seconds, the same clock as the op records' times; not the
    // Stopwatch, not the replay input clock of a hit's at); waves, uncutNpcs, liveFragments, livePieces, liveConvexes,
    // acceptedOps, pendingOps, incompleteOps, acceptedThisFrame, provisionalThisFrame, finalThisFrame, committedThisFrame --
    // the counts the check read in that frame; unsimulated -- the step clock's unsimulated seconds then (null without a
    // clock; the old empty field); stepId -- its step id then (-1 without a clock). The logger's own time and frame on the
    // record are those of the call, not the observation's.
    public static partial class SandboxPropSlashPlayerCheck
    {
        internal const string FrameLogWriter = "CheckFrames";
        internal const string FrameLogLabel = "check frames";

        private static int s_frameRecordings;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetFrameLogForSession()
        {
            s_frameRecordings = 0;
        }

        /// <summary>The old multi.csv row, column for column.</summary>
        internal struct CheckFrameRow
        {
            public int frame;
            public double real;
            public int waves, uncutNpcs, liveFragments, livePieces, liveConvexes, acceptedOps, pendingOps, incompleteOps;
            public int acceptedThisFrame, provisionalThisFrame, finalThisFrame, committedThisFrame;
            public double? unsimulated;
            public long stepId;
        }

        /// <summary>A recording of frames, begun with its "start" record naming the walk's CheckHits recording. Tests make one directly.</summary>
        internal static DevelopmentLogRecording BeginFrameLog(string mode, int iteration, string directory, int hitRecording)
        {
            var log = new DevelopmentLogRecording(FrameLogWriter, FrameLogLabel, ++s_frameRecordings, Stopwatch.GetTimestamp());
            log.Write("start", Time.frameCount, Stopwatch.GetTimestamp(), new object[]
            {
                null, null, null, null, null, null, null, null,
                "mode", mode, "iteration", iteration, "directory", directory, "hitRecording", hitRecording > 0 ? (object)hitRecording : null,
            });
            return log;
        }

        /// <summary>One frame's row (eventFrame is the row's frame).</summary>
        internal static void WriteFrameRow(DevelopmentLogRecording log, in CheckFrameRow r)
        {
            if (log == null || log.Ended)
            {
                return;
            }

            log.Write("frame", r.frame, Stopwatch.GetTimestamp(), new object[]
            {
                null, null, null, null, null, null, null, null,
                "real", r.real, "waves", r.waves, "uncutNpcs", r.uncutNpcs, "liveFragments", r.liveFragments, "livePieces", r.livePieces,
                "liveConvexes", r.liveConvexes, "acceptedOps", r.acceptedOps, "pendingOps", r.pendingOps, "incompleteOps", r.incompleteOps,
                "acceptedThisFrame", r.acceptedThisFrame, "provisionalThisFrame", r.provisionalThisFrame, "finalThisFrame", r.finalThisFrame,
                "committedThisFrame", r.committedThisFrame, "unsimulated", r.unsimulated, "stepId", r.stepId,
            });
        }

        /// <summary>Ends a recording of frames: the summary with the rows tried, the last record.</summary>
        internal static void EndFrameLog(DevelopmentLogRecording log, int rows)
        {
            log?.End(Time.frameCount, Stopwatch.GetTimestamp(), new object[] { "frames", rows }, "frames " + rows);
        }

        private sealed partial class Walk
        {
            private DevelopmentLogRecording _frameLog;
            private int _frameLogRows;

            // One recording a walk, opened after the walk's hit record so that its start names it, and ended after it (MultiClose).
            private void FrameLogOpen(string mode)
            {
                if (_frameLog != null)
                {
                    Log("frame record: already recording (recording " + _frameLog.Recording + "), " + mode + " not opened again");
                    return;
                }

                _frameLog = BeginFrameLog(mode, iteration, directory, _hitLog != null ? _hitLog.Recording : 0);
                _frameLogRows = 0;
            }

            private void FrameLogRow(in CheckFrameRow r)
            {
                if (_frameLog == null || _frameLog.Ended)
                {
                    return;
                }

                _frameLogRows++;
                WriteFrameRow(_frameLog, in r);
            }

            private void FrameLogEnd()
            {
                EndFrameLog(_frameLog, _frameLogRows);
            }
        }
    }
}
