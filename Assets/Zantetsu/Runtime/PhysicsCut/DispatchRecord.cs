#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The record of the shared dispatch for an evaluation run (GRAPH-CONNECT-17 §3-E4; development builds only -- a Release
    /// Player has none of it): asked for by <c>-zantetsuDispatchRecord [folder]</c>, kept off by <c>-zantetsuDispatchObserveOff</c>.
    /// <list type="bullet">
    /// <item>every work's own way through the dispatcher (<see cref="DispatchObservation.WorkLog"/>), written per world at its end:
    /// <c>dispatch-works-&lt;world&gt;.csv</c> (one row a work, Stopwatch ms, an absent clock empty) and
    /// <c>dispatch-summary-&lt;world&gt;.txt</c> (the whole tally), with short identified lines in the log
    /// (<c>DISPATCH SUMMARY &lt;world&gt; k/N</c>, each at most <see cref="LineRoom"/> characters, the last naming the file and its SHA-256);</item>
    /// <item>the clocks' points (world start, the script's start, each pause's events, the end): <c>clock-points.csv</c>, and the
    /// game pause's events as the public <see cref="GamePause"/> events raise them, in their order: <c>pause-events.csv</c>.</item>
    /// </list>
    /// One sequence numbers the frame rows' samples (the check's frames.csv) and the pause events together, so their order within a
    /// frame is known. A reading only: nothing here decides, orders, delays or pauses anything, and the game pause is not changed.
    /// </summary>
    public static class DispatchRecord
    {
        public const string RecordArgument = "-zantetsuDispatchRecord";
        public const string ObserveOffArgument = "-zantetsuDispatchObserveOff";

        /// <summary>The longest a summary line in the log may be (a Quest's log cuts a line at 1,023).</summary>
        public const int LineRoom = 900;

#if ZANTETSU_OBSERVE
        /// <summary>Asked for by the run (the argument, a test, or the pool replay).</summary>
        public static bool Requested;

        /// <summary>Kept off whatever asked (<c>-zantetsuDispatchObserveOff</c>).</summary>
        public static bool Off;

        /// <summary>Where the files go; null until the run names it (the argument's folder, or the check's comparison folder).</summary>
        public static string Folder;

        /// <summary>Whether the record is taken now.</summary>
        public static bool Active => Requested && !Off;

        private static bool s_read, s_subscribed;
        private static long s_sequence;
        private static readonly List<ClockPoint> s_points = new List<ClockPoint>();
        private static readonly List<PauseEvent> s_events = new List<PauseEvent>();

        /// <summary>One moment read on every clock at once (GRAPH-CONNECT-17 §3-E4 "時計の対応点").</summary>
        public struct ClockPoint
        {
            public string kind;
            public long sequence, stopwatch;
            public double gamePauseUnscaled, engineUnscaled, engineRealtime, pausedSeconds, clockStart;
            public int engineFrame;
            public bool paused, manual, system;
        }

        /// <summary>One event of the game pause, in the order the public events raised it.</summary>
        public struct PauseEvent
        {
            public string kind;   // Paused, Manual, System
            public bool value;
            public long sequence, stopwatch;
            public int engineFrame;
            public double gamePauseUnscaled, engineUnscaled, pausedSeconds;
            public bool pausedAfter, manualAfter, systemAfter;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
            Requested = Off = false;
            Folder = null;
            s_read = false;
            s_sequence = 0;
            s_points.Clear();
            s_events.Clear();
            if (s_subscribed)
            {
                GamePause.PausedChanged -= OnPaused;
                GamePause.ManualChanged -= OnManual;
                GamePause.SystemChanged -= OnSystem;
                s_subscribed = false;
            }
        }

        /// <summary>The next number of the sequence the frame samples and the pause events share.</summary>
        public static long NextSequence() => ++s_sequence;

        /// <summary>
        /// One frame row's clocks as the row is sampled (GRAPH-CONNECT-17 §3-E4): the game pause's own clock and delta (as the check's
        /// real and delta read them), the Stopwatch now (NOT a frame boundary), the frame's start on the Stopwatch (the engine's
        /// unscaledTime is the frame's start, its realtimeSinceStartup is now: their difference is the frame's time up to here), the
        /// engine's delta and unscaledTime, the engine's frame, the game pause's state, and the next number of the shared sequence.
        /// </summary>
        public struct FrameClockSample
        {
            public long sequence, stopwatch, frameStart;
            public double gamePauseUnscaled, gamePauseDeltaMs, engineDeltaMs, engineUnscaled;
            public int engineFrame;
            public bool paused, manual, system;
        }

        /// <summary>Takes one sample (main thread). Reads only: the game pause is read as any reader reads it.</summary>
        public static FrameClockSample SampleFrame()
        {
            double gamePause = GamePause.UnscaledTimeAsDouble;
            float gamePauseDelta = GamePause.UnscaledDeltaTime;
            long now = Stopwatch.GetTimestamp();
            double realtime = Time.realtimeSinceStartupAsDouble, unscaled = Time.unscaledTimeAsDouble;
            return new FrameClockSample
            {
                sequence = NextSequence(), stopwatch = now, frameStart = now - (long)((realtime - unscaled) * Stopwatch.Frequency),
                gamePauseUnscaled = gamePause, gamePauseDeltaMs = gamePauseDelta * 1000.0, engineDeltaMs = Time.unscaledDeltaTime * 1000.0, engineUnscaled = unscaled,
                engineFrame = Time.frameCount, paused = GamePause.IsPaused, manual = GamePause.Manual, system = GamePause.System,
            };
        }

        public static IReadOnlyList<ClockPoint> Points => s_points;
        public static IReadOnlyList<PauseEvent> Events => s_events;

        /// <summary>Reads the run's switches once (the first world, or a test).</summary>
        public static void ReadArguments(string[] arguments)
        {
            if (s_read || arguments == null) return;
            s_read = true;
            if (Array.IndexOf(arguments, ObserveOffArgument) >= 0)
            {
                Off = true;
                DispatchObservation.WorkLog = false;
                UnityEngine.Debug.Log("DISPATCH RECORD: off -- asked for by " + ObserveOffArgument + " (no work log, no clock points, no pause events, no frame clock columns)");
            }
            int at = Array.IndexOf(arguments, RecordArgument);
            if (at >= 0)
            {
                Requested = true;
                if (at + 1 < arguments.Length && !arguments[at + 1].StartsWith("-", StringComparison.Ordinal)) UseFolder(arguments[at + 1]);
                UnityEngine.Debug.Log("DISPATCH RECORD: " + (Off ? "asked for, but OFF" : "on") + " -- asked for by " + RecordArgument
                    + (Folder != null ? " (files to " + Folder + ")" : " (files to the check's comparison folder)"));
            }
        }

        /// <summary>
        /// Names the folder when none was named yet (the argument's, or the check's comparison folder); the split's timeline rows go
        /// there too when no file was named for them (<c>split-timeline.csv</c>), so a run on a device needs no path of its own.
        /// </summary>
        public static void UseFolder(string folder)
        {
            if (!Active || string.IsNullOrEmpty(folder) || Folder != null) return;
            Folder = folder;
            if (CutSpatialSplit.TimelineCsvPath == null) CutSpatialSplit.TimelineCsvPath = Path.Combine(folder, "split-timeline.csv");
        }

        /// <summary>A world begins: the work log on, the pause events listened to, the world's start point.</summary>
        public static void BeginWorld(string worldName)
        {
            if (!Active) return;
            DispatchObservation.WorkLog = true;
            if (!s_subscribed)
            {
                GamePause.PausedChanged += OnPaused;
                GamePause.ManualChanged += OnManual;
                GamePause.SystemChanged += OnSystem;
                s_subscribed = true;
            }
            NotePoint("world start " + worldName, double.NaN);
        }

        /// <summary>Records one moment on every clock (the check's script start and end, a world's start).</summary>
        public static void NotePoint(string kind, double clockStart)
        {
            if (!Active) return;
            s_points.Add(new ClockPoint
            {
                kind = kind, sequence = NextSequence(), stopwatch = Stopwatch.GetTimestamp(),
                gamePauseUnscaled = GamePause.UnscaledTimeAsDouble, engineUnscaled = Time.unscaledTimeAsDouble, engineRealtime = Time.realtimeSinceStartupAsDouble,
                pausedSeconds = GamePause.PausedSeconds, clockStart = clockStart, engineFrame = Time.frameCount,
                paused = GamePause.IsPaused, manual = GamePause.Manual, system = GamePause.System,
            });
        }

        private static void OnPaused(bool value) => NoteEvent("Paused", value);
        private static void OnManual(bool value) => NoteEvent("Manual", value);
        private static void OnSystem(bool value) => NoteEvent("System", value);

        private static void NoteEvent(string kind, bool value)
        {
            if (!Active) return;
            s_events.Add(new PauseEvent
            {
                kind = kind, value = value, sequence = NextSequence(), stopwatch = Stopwatch.GetTimestamp(), engineFrame = Time.frameCount,
                gamePauseUnscaled = GamePause.UnscaledTimeAsDouble, engineUnscaled = Time.unscaledTimeAsDouble, pausedSeconds = GamePause.PausedSeconds,
                pausedAfter = GamePause.IsPaused, manualAfter = GamePause.Manual, systemAfter = GamePause.System,
            });
        }

        /// <summary>Stopwatch ticks as ms with 4 decimals; empty for an absent clock (0), never a 0.</summary>
        public static string Ms(long ticks) => ticks == 0 ? "" : (ticks * 1000.0 / Stopwatch.Frequency).ToString("F4", CultureInfo.InvariantCulture);

        private static string R(double v) => double.IsNaN(v) ? "" : v.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>The pause intervals in Stopwatch ticks, from the Paused events (an interval still open at the end runs to <paramref name="end"/>).</summary>
        private static List<KeyValuePair<long, long>> PauseIntervals(long end)
        {
            var spans = new List<KeyValuePair<long, long>>();
            long open = 0;
            foreach (PauseEvent e in s_events)
            {
                if (e.kind != "Paused") continue;
                if (e.value && open == 0) open = e.stopwatch;
                else if (!e.value && open != 0) { spans.Add(new KeyValuePair<long, long>(open, e.stopwatch)); open = 0; }
            }
            if (open != 0) spans.Add(new KeyValuePair<long, long>(open, end));
            return spans;
        }

        private static long Overlap(long from, long to, List<KeyValuePair<long, long>> spans)
        {
            if (from == 0 || to == 0 || to <= from) return 0;
            long sum = 0;
            foreach (var s in spans)
            {
                long a = Math.Max(from, s.Key), b = Math.Min(to, s.Value);
                if (b > a) sum += b - a;
            }
            return sum;
        }

        /// <summary>
        /// A world's end: its works' rows and its whole tally to the folder, the tally in short lines to the log. Every work offered
        /// while the log was on is a row, the ones still held as they stand (Waiting, Accepted, Running, FinishedNotCollected).
        /// </summary>
        public static void WriteWorld(string worldName, SharedWorkDispatcher dispatcher, string timelineLine)
        {
            if (!Active || dispatcher == null) return;
            if (Folder == null)
            {
                UnityEngine.Debug.LogError("DISPATCH RECORD " + worldName + ": no folder was named (neither " + RecordArgument + " <folder> nor a check's comparison folder): nothing written");
                return;
            }
            string safe = Safe(worldName);
            long now = Stopwatch.GetTimestamp();
            DispatchWorkRecord[] works = dispatcher.SnapshotWorkLog();
            List<KeyValuePair<long, long>> pauses = PauseIntervals(now);
            CultureInfo inv = CultureInfo.InvariantCulture;
            var csv = new StringBuilder(works.Length * 120 + 256);
            csv.Append("serial,purpose,destination,kind,label,t0Ms,t1Ms,t2Ms,t3Ms,t4Ms,tEndMs,refused,firstRefusalMs,lastRefusalMs,worker,end,pausedWaitMs,pausedRunMs,movedTo\n");
            foreach (DispatchWorkRecord w in works)
            {
                long waitEnd = w.t2 != 0 ? w.t2 : (w.tEnd != 0 ? w.tEnd : now);
                long runEnd = w.t3 != 0 ? w.t3 : now;
                csv.Append(w.serial).Append(',').Append(w.purpose).Append(',').Append(w.destination).Append(',').Append(w.kind).Append(',').Append(w.label)
                    .Append(',').Append(Ms(w.t0)).Append(',').Append(Ms(w.t1)).Append(',').Append(Ms(w.t2)).Append(',').Append(Ms(w.t3)).Append(',').Append(Ms(w.t4)).Append(',').Append(Ms(w.tEnd))
                    .Append(',').Append(w.refused).Append(',').Append(Ms(w.firstRefusal)).Append(',').Append(Ms(w.lastRefusal)).Append(',').Append(w.worker).Append(',').Append(w.end)
                    .Append(',').Append((Overlap(w.t0, waitEnd, pauses) * 1000.0 / Stopwatch.Frequency).ToString("F4", inv))
                    .Append(',').Append(w.t2 != 0 ? (Overlap(w.t2, runEnd, pauses) * 1000.0 / Stopwatch.Frequency).ToString("F4", inv) : "")
                    .Append(',').Append(w.moved ? w.movedTo.ToString() : "")
                    .Append('\n');
            }
            string summary = Summary(worldName, dispatcher, works, now, timelineLine);
            try
            {
                Directory.CreateDirectory(Folder);
                string csvPath = Path.Combine(Folder, "dispatch-works-" + safe + ".csv");
                string sumPath = Path.Combine(Folder, "dispatch-summary-" + safe + ".txt");
                File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(false));
                byte[] bytes = new UTF8Encoding(false).GetBytes(summary);
                File.WriteAllBytes(sumPath, bytes);
                LogLines(worldName, summary, Path.GetFileName(sumPath), bytes, works.Length, Path.GetFileName(csvPath));
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("DISPATCH RECORD " + worldName + ": the files could not be written: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string Safe(string name)
        {
            var s = new StringBuilder(name.Length);
            foreach (char c in name) s.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return s.ToString();
        }

        private static string Summary(string worldName, SharedWorkDispatcher d, DispatchWorkRecord[] works, long now, string timelineLine)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(4096);
            text.Append("dispatch record of world ").Append(worldName).Append(": written at ").Append(Ms(now)).Append(" ms (Stopwatch)")
                .Append("; works logged ").Append(works.Length).Append(" (not kept ").Append(d.WorkLogNotKept).Append(")")
                .Append("; Background works only (moved in from another destination, not logged: ").Append(d.WorkLogMovedIn).Append(")")
                .Append("; dispatch opportunities while logging ").Append(d.WorkLogOpportunities).Append('\n');
            for (int p = 0; p <= (int)WorkPurpose.MobPlanning; p++)
            {
                var purpose = (WorkPurpose)p;
                int n = 0, refusedWorks = 0;
                long refusals = 0;
                var ends = new int[Enum.GetValues(typeof(DispatchWorkEnd)).Length];
                Stat wait = default, run = default, completion = default;
                foreach (DispatchWorkRecord w in works)
                {
                    if (w.purpose != purpose) continue;
                    n++;
                    ends[(int)w.end]++;
                    if (w.refused > 0) { refusedWorks++; refusals += w.refused; }
                    if (w.t0 != 0 && w.t1 != 0) wait.Add(w.t1 - w.t0);
                    if (w.t2 != 0 && w.t3 != 0) run.Add(w.t3 - w.t2);
                    if (w.t0 != 0 && w.t4 != 0) completion.Add(w.t4 - w.t0);
                }
                if (n == 0) continue;
                text.Append(purpose).Append(": works ").Append(n).Append("; ends");
                for (int e = 0; e < ends.Length; e++) if (ends[e] > 0) text.Append(' ').Append((DispatchWorkEnd)e).Append(' ').Append(ends[e]);
                text.Append("; wait t1-t0 ").Append(wait.Describe()).Append("; run t3-t2 ").Append(run.Describe()).Append("; completion t4-t0 ").Append(completion.Describe())
                    .Append("; refused works ").Append(refusedWorks).Append(", refusals ").Append(refusals).Append('\n');
            }
            for (int dest = 0; dest < 4; dest++)
            {
                if (d.Executor((WorkDestination)dest) is WorkerPoolExecutor pool)
                {
                    text.Append((WorkDestination)dest).Append(" workers (").Append(pool.Workers.Count).Append(", capacity ").Append(pool.Capacity).Append(") busy s:");
                    foreach (WorkerPoolExecutor.WorkerObservation w in pool.Workers) text.Append(' ').Append(w.BusySeconds.ToString("F4", inv)).Append(" (").Append(w.Executed).Append(" works)");
                    text.Append('\n');
                }
            }
            text.Append("pause events ").Append(s_events.Count).Append(", clock points ").Append(s_points.Count).Append('\n');
            if (!string.IsNullOrEmpty(timelineLine)) text.Append(timelineLine).Append('\n');
            return text.ToString();
        }

        private struct Stat
        {
            public long n, sum, max;
            public void Add(long ticks) { n++; sum += ticks; if (ticks > max) max = ticks; }
            public string Describe() => n == 0 ? "n 0" : "n " + n + ", mean " + (sum * 1000.0 / Stopwatch.Frequency / n).ToString("F4", CultureInfo.InvariantCulture)
                + " ms, max " + (max * 1000.0 / Stopwatch.Frequency).ToString("F4", CultureInfo.InvariantCulture) + " ms";
        }

        private static void LogLines(string worldName, string summary, string fileName, byte[] bytes, int works, string csvName)
        {
            foreach (string line in SummaryLines(worldName, summary, fileName, bytes, works, csvName)) UnityEngine.Debug.Log(line);
        }

        /// <summary>
        /// The summary's short lines, every one 'DISPATCH SUMMARY &lt;world&gt; k/N: &lt;text&gt;' (at most <see cref="LineRoom"/>
        /// characters): the summary's text in parts ('\n' as '|'), then the last, 'N/N: end: file &lt;name&gt; bytes &lt;n&gt; sha256
        /// &lt;hex&gt;; rows &lt;n&gt; in &lt;csv&gt;'. One form for every line, the one the walk tools read (check_quest17.py).
        /// </summary>
        internal static List<string> SummaryLines(string worldName, string summary, string fileName, byte[] bytes, int works, string csvName)
        {
            string head = "DISPATCH SUMMARY " + worldName + " ";
            int room = LineRoom - head.Length - 16;
            if (room < 64) room = 64;
            string flat = summary.Replace('\n', '|');
            var parts = new List<string>();
            for (int i = 0; i < flat.Length; i += room) parts.Add(flat.Substring(i, Math.Min(room, flat.Length - i)));
            int total = parts.Count + 1;
            var lines = new List<string>(total);
            for (int i = 0; i < parts.Count; i++) lines.Add(head + (i + 1) + "/" + total + ": " + parts[i]);
            string sha;
            using (SHA256 h = SHA256.Create())
            {
                var hex = new StringBuilder(64);
                foreach (byte b in h.ComputeHash(bytes)) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                sha = hex.ToString();
            }
            lines.Add(head + total + "/" + total + ": end: file " + fileName + " bytes " + bytes.Length + " sha256 " + sha + "; rows " + works + " in " + csvName);
            return lines;
        }

        /// <summary>Tests: everything back to a session's start (the switches, the folder, the points, the events, the listening).</summary>
        internal static void ResetForTests() => ResetForSession();

        /// <summary>
        /// Tests: frame samples as the check's frames.csv names them (frame = the engine's frame, real = the game pause's clock less
        /// <paramref name="clockStart"/>, delta = the game pause's delta in seconds, then the record's clock columns), so that the
        /// walk tools read a test's frames and a run's the same way.
        /// </summary>
        internal static void WriteFrameSamples(string path, IReadOnlyList<FrameClockSample> samples, double clockStart)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(256 + samples.Count * 120);
            text.Append("frame,phase,real,delta,sampleSeq,swMs,frameStartSwMs,engineDeltaMs,engineUnscaled,pausedAtSample,manualAtSample,systemAtSample\n");
            foreach (FrameClockSample s in samples)
            {
                text.Append(s.engineFrame).Append(",replay,").Append((s.gamePauseUnscaled - clockStart).ToString("R", inv)).Append(',').Append((s.gamePauseDeltaMs / 1000.0).ToString("R", inv))
                    .Append(',').Append(s.sequence).Append(',').Append(Ms(s.stopwatch)).Append(',').Append(Ms(s.frameStart)).Append(',').Append(s.engineDeltaMs.ToString("R", inv))
                    .Append(',').Append(s.engineUnscaled.ToString("R", inv)).Append(',').Append(s.paused ? 1 : 0).Append(',').Append(s.manual ? 1 : 0).Append(',').Append(s.system ? 1 : 0).Append('\n');
            }
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        }

        /// <summary>The clocks' points and the pause events to the folder (the check, at its end).</summary>
        public static void WriteClockFiles(string folder)
        {
            if (!Active || folder == null) return;
            var points = new StringBuilder(256 + s_points.Count * 160);
            points.Append("kind,sequence,stopwatchMs,gamePauseUnscaled,engineUnscaled,engineRealtime,pausedSeconds,clockStart,realOfGamePause,engineFrame,paused,manual,system\n");
            foreach (ClockPoint p in s_points)
            {
                points.Append(p.kind.Replace(',', ';')).Append(',').Append(p.sequence).Append(',').Append(Ms(p.stopwatch)).Append(',').Append(R(p.gamePauseUnscaled)).Append(',').Append(R(p.engineUnscaled))
                    .Append(',').Append(R(p.engineRealtime)).Append(',').Append(R(p.pausedSeconds)).Append(',').Append(R(p.clockStart))
                    .Append(',').Append(double.IsNaN(p.clockStart) ? "" : R(p.gamePauseUnscaled - p.clockStart)).Append(',').Append(p.engineFrame)
                    .Append(',').Append(p.paused ? 1 : 0).Append(',').Append(p.manual ? 1 : 0).Append(',').Append(p.system ? 1 : 0).Append('\n');
            }
            var events = new StringBuilder(256 + s_events.Count * 120);
            events.Append("sequence,kind,value,engineFrame,stopwatchMs,gamePauseUnscaled,engineUnscaled,pausedSeconds,pausedAfter,manualAfter,systemAfter\n");
            foreach (PauseEvent e in s_events)
            {
                events.Append(e.sequence).Append(',').Append(e.kind).Append(',').Append(e.value ? 1 : 0).Append(',').Append(e.engineFrame).Append(',').Append(Ms(e.stopwatch))
                    .Append(',').Append(R(e.gamePauseUnscaled)).Append(',').Append(R(e.engineUnscaled)).Append(',').Append(R(e.pausedSeconds))
                    .Append(',').Append(e.pausedAfter ? 1 : 0).Append(',').Append(e.manualAfter ? 1 : 0).Append(',').Append(e.systemAfter ? 1 : 0).Append('\n');
            }
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "clock-points.csv"), points.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(folder, "pause-events.csv"), events.ToString(), new UTF8Encoding(false));
                UnityEngine.Debug.Log("DISPATCH RECORD: clock points " + s_points.Count + ", pause events " + s_events.Count + " written to " + folder);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("DISPATCH RECORD: the clock files could not be written: " + e.GetType().Name + ": " + e.Message);
            }
        }
#else
        /// <summary>Nothing in a Release Player.</summary>
        public static bool Active => false;
        public static void ReadArguments(string[] arguments) { }
        public static void UseFolder(string folder) { }
        public static void BeginWorld(string worldName) { }
        public static void NotePoint(string kind, double clockStart) { }
        public static void WriteWorld(string worldName, SharedWorkDispatcher dispatcher, string timelineLine) { }
        public static void WriteClockFiles(string folder) { }
#endif
    }
}
