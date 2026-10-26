#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // GRAPH-CONNECT-17 §3-E4 (development builds; an evaluation run that asked for the dispatch record): each frame row's clocks as
    // the row was sampled, so that a frame and the works that ran during it can be matched -- the Stopwatch at the sample (swMs: NOT a
    // frame boundary, it includes the frame's work up to the sample), the frame's start on the Stopwatch (frameStartSwMs, from the
    // engine's unscaledTime -- taken at the frame's start -- and its realtimeSinceStartup read at the sample), the game pause's state
    // at the sample (paused, manual, system), the engine's own delta and unscaledTime, and the sequence the pause events share
    // (DispatchRecord). The columns come only in a run that asked for the record; frames.csv is otherwise as it was. Nothing here
    // changes the game pause or anything else.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
#if ZANTETSU_OBSERVE
            // the row's clocks: development builds only (a Release Player's FrameRow has none of these fields)
            private sealed partial class FrameRow
            {
                public bool clockNoted;
                public long sampleSeq, swTicks, frameStartTicks;
                public double engineDeltaMs, engineUnscaled;
                public bool pausedAtSample, manualAtSample, systemAtSample;
            }
#endif

            partial void NoteDispatchClock(FrameRow row);
            partial void NoteScriptStartClock();
            partial void NoteScriptEndClock();
            partial void WriteDispatchClockFiles();
            static partial void AppendDispatchClockHeader(StringBuilder text);
            static partial void AppendDispatchClockValues(StringBuilder text, FrameRow row);

#if ZANTETSU_OBSERVE
            partial void NoteDispatchClock(FrameRow row)
            {
                row.clockNoted = DispatchRecord.Active;
                if (!row.clockNoted) return;
                DispatchRecord.FrameClockSample s = DispatchRecord.SampleFrame();   // the same sampling the clock tests use
                row.sampleSeq = s.sequence;
                row.swTicks = s.stopwatch;
                row.frameStartTicks = s.frameStart;
                row.engineDeltaMs = s.engineDeltaMs;
                row.engineUnscaled = s.engineUnscaled;
                row.pausedAtSample = s.paused;
                row.manualAtSample = s.manual;
                row.systemAtSample = s.system;
            }

            partial void NoteScriptStartClock()
            {
                if (!DispatchRecord.Active) return;
                DispatchRecord.UseFolder(directory);
                DispatchRecord.NotePoint("script start", _clockStart);
            }

            // the script's end, as it happens (the end of the walk's window, plan §3-E4), apart from the saves that follow it
            partial void NoteScriptEndClock()
            {
                if (!DispatchRecord.Active) return;
                DispatchRecord.NotePoint("script end", _clockStart);
            }

            // after frames.csv is written (both save paths): a point of its own ("frames written"), then the clock files
            partial void WriteDispatchClockFiles()
            {
                if (!DispatchRecord.Active) return;
                DispatchRecord.NotePoint("frames written", _clockStart);
                DispatchRecord.WriteClockFiles(directory);
            }

            static partial void AppendDispatchClockHeader(StringBuilder text)
            {
                if (!DispatchRecord.Active) return;
                text.Append(",sampleSeq,swMs,frameStartSwMs,engineDeltaMs,engineUnscaled,pausedAtSample,manualAtSample,systemAtSample");
            }

            static partial void AppendDispatchClockValues(StringBuilder text, FrameRow row)
            {
                if (!DispatchRecord.Active) return;
                if (!row.clockNoted)
                {
                    text.Append(",,,,,,,,");   // a row sampled before the record was on: no clocks (never a 0)
                    return;
                }
                CultureInfo inv = CultureInfo.InvariantCulture;
                text.Append(',').Append(row.sampleSeq).Append(',').Append(DispatchRecord.Ms(row.swTicks)).Append(',').Append(DispatchRecord.Ms(row.frameStartTicks))
                    .Append(',').Append(row.engineDeltaMs.ToString("R", inv)).Append(',').Append(row.engineUnscaled.ToString("R", inv))
                    .Append(',').Append(row.pausedAtSample ? 1 : 0).Append(',').Append(row.manualAtSample ? 1 : 0).Append(',').Append(row.systemAtSample ? 1 : 0);
            }
#endif
        }
    }
}
