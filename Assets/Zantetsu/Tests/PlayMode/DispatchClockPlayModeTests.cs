using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The frame clocks of the dispatch record (GRAPH-CONNECT-17 §3-E4, "対応の試験" 1-10), on real frames and the real game pause: a
    /// frame row is sampled in LateUpdate (DispatchRecord.SampleFrame, the check's own sampling), its delta is from the previous frame's
    /// start to this frame's, so a long delta(i) is the interval [F(i-1), F(i)) of the frame starts -- a work that ran only while the main
    /// thread was held in frame k lies in row k+1's interval, not in row k's or k+2's, and the long frame is no deficit of the clocks;
    /// the game pause's events come PausedChanged first and then their cause (only the cause when the other pause still holds); a row's
    /// state is the state AT ITS SAMPLE, a pause taken or released after the sample in the same frame does not make that row paused or
    /// resumed; a system pause's end taken in a frame makes that frame's delta 0 even when a manual pause still held and is released in
    /// the same frame; the paused rows' real is the pause's GamePause value less the script's start (which is not 0); a work across a pause
    /// records its paused overlap. Each case's samples, events and points are written as the walk tools read them (frames.csv,
    /// pause-events.csv, clock-points.csv) under ZANTETSU_CLOCK_FIXTURES (or a temporary folder) for the tools' own check of them.
    /// </summary>
    public sealed class DispatchClockPlayModeTests
    {
        [DefaultExecutionOrder(400)]
        private sealed class Sampler : MonoBehaviour
        {
            public readonly List<DispatchRecord.FrameClockSample> samples = new List<DispatchRecord.FrameClockSample>();
            public int index = -1;   // the frame's index since the sampler began
            private Action<int> _before, _after;
            private int _beforeBase, _afterBase;

            // a step's frame number counts from its registration (TL review 8, P2-8): 0 is the first frame after it is set, however
            // many frames the sampler ran before -- a step can never be registered past its own frame
            public Action<int> beforeSample { set { _before = value; _beforeBase = index + 1; } }   // Update, before the sample
            public Action<int> afterSample { set { _after = value; _afterBase = index + 1; } }     // LateUpdate, after the sample

            private void Update()
            {
                index++;
                _before?.Invoke(index - _beforeBase);
            }

            private void LateUpdate()
            {
                samples.Add(DispatchRecord.SampleFrame());
                _after?.Invoke(index - _afterBase);
            }
        }

        private sealed class HoldWork : IDispatchWork
        {
            public readonly ManualResetEventSlim started = new ManualResetEventSlim(false), release = new ManualResetEventSlim(false);
            public long began, ended;
            public volatile bool collected;
            public void Begin()
            {
                began = Stopwatch.GetTimestamp();
                started.Set();
                release.Wait();
                ended = Stopwatch.GetTimestamp();
            }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { collected = true; }
        }

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

        private GameObject _host;
        private Sampler _sampler;
        private string _folder;
        private double _clockStart;
        private float _timeScaleBefore;

        private static string FixtureRoot()
        {
            string root = Environment.GetEnvironmentVariable("ZANTETSU_CLOCK_FIXTURES");
            return string.IsNullOrEmpty(root) ? Path.Combine(Path.GetTempPath(), "zantetsu-dispatch-clock") : root;
        }

        private void Begin(string name)
        {
            _timeScaleBefore = Time.timeScale;
            GamePause.ResetForTest();
            DispatchRecord.ResetForTests();
            DispatchRecord.Requested = true;
            _folder = Path.Combine(FixtureRoot(), name);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
            Directory.CreateDirectory(_folder);
            DispatchRecord.UseFolder(_folder);
            DispatchRecord.BeginWorld("clock-test");
            _host = new GameObject("dispatch clock test");
            _sampler = _host.AddComponent<Sampler>();
        }

        private void ScriptStart()
        {
            _clockStart = GamePause.UnscaledTimeAsDouble;
            Assert.That(_clockStart, Is.GreaterThan(0.0), "the script's start is not 0 (TL review 5: the real's origin)");
            DispatchRecord.NotePoint("script start", _clockStart);
        }

        private void Finish()
        {
            DispatchRecord.NotePoint("frames written", _clockStart);
            DispatchRecord.WriteFrameSamples(Path.Combine(_folder, "frames.csv"), _sampler.samples, _clockStart);
            DispatchRecord.WriteClockFiles(_folder);
        }

        private readonly List<HoldWork> _holds = new List<HoldWork>();
        private readonly List<WorkerPoolExecutor> _pools = new List<WorkerPoolExecutor>();
        private readonly List<SharedWorkDispatcher> _dispatchers = new List<SharedWorkDispatcher>();

        // made through these, a held work is released and a pool and a dispatcher are stopped at the tear-down whatever ended the test
        private HoldWork NewHold() { var w = new HoldWork(); _holds.Add(w); return w; }

        private WorkerPoolExecutor NewPool()
        {
            WorkerPoolExecutor pool = WorkerPoolExecutor.BackgroundPoolForReplay(1, 1);
            _pools.Add(pool);
            return pool;
        }

        private SharedWorkDispatcher NewDispatcher(WorkerPoolExecutor pool)
        {
            var d = new SharedWorkDispatcher(8, 2, 16, new UnityJobWorkExecutor(1), new IdleExecutor(WorkDestination.GeometryPool), pool);
            _dispatchers.Add(d);
            return d;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (HoldWork w in _holds) w.release.Set();
            foreach (SharedWorkDispatcher d in _dispatchers) { try { d.Shutdown(5000); } catch (Exception) { } }
            foreach (WorkerPoolExecutor p in _pools) { try { p.Dispose(); } catch (Exception) { } }
            _holds.Clear();
            _dispatchers.Clear();
            _pools.Clear();
            if (_host != null) UnityEngine.Object.Destroy(_host);
            GamePause.ResetForTest();
            Time.timeScale = _timeScaleBefore;
            DispatchRecord.ResetForTests();
            DispatchObservation.WorkLog = false;
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

        private List<DispatchRecord.PauseEvent> EventsSince(long sequence) => DispatchRecord.Events.Where(e => e.sequence > sequence).ToList();

        [UnityTest]
        public IEnumerator AKnownLongFrame_AndAWorkOnlyInsideIt_LieInTheRowWithTheLongDelta_AndAreNoDeficitOfTheClocks()
        {
            Begin("long-frame");
            ScriptStart();
            var pool = NewPool();
            Assert.That(pool.WaitUntilStartupFinished(5000), Is.True);
            var work = NewHold();
            const int HoldFrame = 5;
            int holdEngineFrame = -1;
            _sampler.beforeSample = i =>
            {
                if (i != HoldFrame) return;
                holdEngineFrame = Time.frameCount;
                long holdStart = Stopwatch.GetTimestamp();
                Assert.That(pool.TryAccept(work), Is.True);
                Assert.That(work.started.Wait(1000), Is.True, "the work began while the main thread holds");
                while (Stopwatch.GetTimestamp() < holdStart + Stopwatch.Frequency * 80 / 1000) Thread.SpinWait(200);
                work.release.Set();
                while (work.ended == 0) Thread.SpinWait(50);   // the work ends inside the hold
                while (Stopwatch.GetTimestamp() < holdStart + Stopwatch.Frequency * 100 / 1000) Thread.SpinWait(200);
            };
            for (int i = 0; i < HoldFrame + 4; i++) yield return null;
            Finish();
            pool.Dispose();
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int k = s.FindIndex(x => x.engineFrame == holdEngineFrame);
            Assert.That(k, Is.GreaterThan(0), "the hold frame was sampled");
            int i1 = k + 1;   // the row whose delta spans the held frame
            Assert.That(i1 + 1, Is.LessThan(s.Count));
            Assert.That(s[i1].gamePauseDeltaMs, Is.GreaterThanOrEqualTo(99.0), "the row after the held frame carries the long delta");
            Assert.That(s[k].gamePauseDeltaMs, Is.LessThan(90.0), "the held frame's own row does not");
            // the rule: row i <- [F(i-1), F(i)); the work's run lies in row k+1's interval only
            Assert.That(s[k].frameStart, Is.LessThanOrEqualTo(work.began), "F(k) <= t2");
            Assert.That(work.ended, Is.LessThanOrEqualTo(s[i1].frameStart), "t3 <= F(k+1): the work lies in [F(k), F(k+1)) = row k+1");
            Assert.That(work.ended, Is.GreaterThan(s[k - 1].frameStart), "not in row k's interval [F(k-1), F(k))");
            Assert.That(work.began, Is.GreaterThanOrEqualTo(s[k].frameStart), "and not before it");
            Assert.That(work.began, Is.LessThan(s[i1].frameStart), "not in row k+2's interval [F(k+1), F(k+2))");
            Assert.That(s[k].stopwatch - s[k].frameStart, Is.LessThan(s[i1].frameStart - s[k].frameStart), "the sample of row k is inside its frame, before the frame's end");
            // the clocks' check on frame starts: the long frame is no deficit
            for (int i = 1; i < s.Count; i++)
            {
                double diff = Math.Abs(s[i].gamePauseDeltaMs - Ms(s[i].frameStart - s[i - 1].frameStart));
                Assert.That(diff, Is.LessThanOrEqualTo(0.1), "1000 delta(i) vs F(i) - F(i-1) at row " + i + " (long frame included)");
            }
        }

        [UnityTest]
        public IEnumerator AManualPauseAndItsResume_RaisePausedFirst_TheRowsFollowTheContract_AndTheRealIsTheFrozenValueLessTheScriptStart()
        {
            Begin("manual");
            for (int i = 0; i < 3; i++) yield return null;   // the world's start apart from the script's
            ScriptStart();
            long mark = DispatchRecord.NextSequence();
            _sampler.beforeSample = i =>
            {
                if (i == 3) GamePause.SetManual(true);
                if (i == 8) GamePause.SetManual(false);
            };
            for (int i = 0; i < 12; i++) yield return null;
            Finish();
            List<DispatchRecord.PauseEvent> e = EventsSince(mark);
            Assert.That(e.Select(x => x.kind + ":" + x.value).ToArray(), Is.EqualTo(new[] { "Paused:True", "Manual:True", "Paused:False", "Manual:False" }), "PausedChanged first, then the cause");
            double frozenReal = e[0].gamePauseUnscaled - _clockStart;
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            var paused = s.Where(x => x.paused).ToList();
            Assert.That(paused.Count, Is.EqualTo(5), "the rows of frames 3 to 7 were sampled paused");
            foreach (var p in paused)
            {
                Assert.That(p.gamePauseDeltaMs, Is.Zero, "delta 0 while paused");
                Assert.That(p.gamePauseUnscaled - _clockStart, Is.EqualTo(frozenReal).Within(1e-9), "real = the pause's GamePause value less the script's start");
            }
            int resume = s.FindIndex(x => x.engineFrame == e[2].engineFrame);
            Assert.That(resume, Is.GreaterThan(0));
            Assert.That(s[resume].paused, Is.False, "the resume came before this frame's sample");
            Assert.That(s[resume].gamePauseDeltaMs, Is.EqualTo(s[resume].engineDeltaMs).Within(1e-6), "2a: the manual resume's delta is the engine's");
            Assert.That(s[resume].gamePauseUnscaled - _clockStart, Is.EqualTo(frozenReal).Within(1e-9), "2a: real stands at the frozen value");
            Assert.That(s[resume + 1].gamePauseDeltaMs, Is.EqualTo(s[resume + 1].engineDeltaMs).Within(1e-6), "the next row is a normal row");
        }

        [UnityTest]
        public IEnumerator ASystemPause_ItsEndIsTakenAtTheNextFrame_WithDeltaZero_AndTheEventsComePausedFirst()
        {
            Begin("system");
            ScriptStart();
            long mark = DispatchRecord.NextSequence();
            _sampler.beforeSample = i =>
            {
                if (i == 3) GamePause.SetSystem(true);
                if (i == 7) GamePause.SetSystem(false);   // the end is taken by the next frame's first reading
            };
            for (int i = 0; i < 12; i++) yield return null;
            Finish();
            List<DispatchRecord.PauseEvent> e = EventsSince(mark);
            Assert.That(e.Select(x => x.kind + ":" + x.value).ToArray(), Is.EqualTo(new[] { "Paused:True", "System:True", "Paused:False", "System:False" }));
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int back = s.FindIndex(x => x.engineFrame == e[2].engineFrame);
            Assert.That(back, Is.GreaterThan(0));
            Assert.That(s[back].paused, Is.False);
            Assert.That(s[back].gamePauseDeltaMs, Is.Zero, "the frame that took the system pause's end has delta 0");
            Assert.That(s[back].gamePauseUnscaled - _clockStart, Is.EqualTo(e[0].gamePauseUnscaled - _clockStart).Within(1e-9), "real does not jump");
            Assert.That(s[back + 1].gamePauseDeltaMs, Is.EqualTo(s[back + 1].engineDeltaMs).Within(1e-6), "the next row is normal");
            Assert.That(s[back - 1].paused, Is.True, "the frame of the release request is still paused (the end is taken at the next frame)");
        }

        [UnityTest]
        public IEnumerator APauseTakenAfterTheSample_InTheSameFrame_DoesNotMakeThatRowPaused()
        {
            Begin("pause-after-sample");
            ScriptStart();
            _sampler.afterSample = i =>
            {
                if (i == 3) GamePause.SetManual(true);
                if (i == 8) GamePause.SetManual(false);
            };
            for (int i = 0; i < 12; i++) yield return null;
            Finish();
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int taken = s.FindIndex(x => x.paused);
            Assert.That(taken, Is.GreaterThan(0));
            Assert.That(s[taken - 1].paused, Is.False, "the row of the frame the pause was taken in was sampled before it");
            Assert.That(s[taken - 1].gamePauseDeltaMs, Is.GreaterThan(0.0), "and keeps its delta (nothing is asked of it backwards)");
            Assert.That(s[taken].gamePauseDeltaMs, Is.Zero, "the next row is paused: delta 0");
            DispatchRecord.PauseEvent first = DispatchRecord.Events.First(x => x.kind == "Paused" && x.value);
            Assert.That(first.sequence, Is.GreaterThan(s[taken - 1].sequence), "the event's number is after the row's sample");
            Assert.That(first.sequence, Is.LessThan(s[taken].sequence));
        }

        [UnityTest]
        public IEnumerator AManualResumeAfterTheSample_InTheSameFrame_TheNextFramesRowHasAdvancedFromTheResume()
        {
            Begin("resume-after-sample");
            ScriptStart();
            _sampler.beforeSample = i => { if (i == 3) GamePause.SetManual(true); };
            _sampler.afterSample = i => { if (i == 8) GamePause.SetManual(false); };
            for (int i = 0; i < 12; i++) yield return null;
            Finish();
            DispatchRecord.PauseEvent resume = DispatchRecord.Events.First(x => x.kind == "Paused" && !x.value);
            DispatchRecord.PauseEvent pause = DispatchRecord.Events.First(x => x.kind == "Paused" && x.value);
            double frozenReal = pause.gamePauseUnscaled - _clockStart;
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int last = s.FindIndex(x => x.engineFrame == resume.engineFrame);
            Assert.That(s[last].paused, Is.True, "the resume's own frame was sampled before the resume: still paused");
            Assert.That(s[last].gamePauseDeltaMs, Is.Zero);
            DispatchRecord.FrameClockSample next = s[last + 1];
            Assert.That(next.paused, Is.False);
            Assert.That(next.gamePauseDeltaMs, Is.EqualTo(next.engineDeltaMs).Within(1e-6), "2b: delta is the engine's");
            Assert.That(next.gamePauseUnscaled - _clockStart, Is.Not.EqualTo(frozenReal).Within(1e-9), "2b: real has advanced -- the frozen value is not asked of it");
            Assert.That(next.gamePauseUnscaled - _clockStart - frozenReal, Is.EqualTo(next.engineUnscaled - resume.engineUnscaled).Within(1e-6), "2b: the advance is the engine's from the resume");
        }

        [UnityTest]
        public IEnumerator Overlapping_ManualThenSystem_ManualReleasedFirst_StaysPausedWithDeltaZero_UntilTheSystemEnds()
        {
            Begin("overlap-manual-first");
            ScriptStart();
            long mark = DispatchRecord.NextSequence();
            _sampler.beforeSample = i =>
            {
                if (i == 2) GamePause.SetManual(true);
                if (i == 4) GamePause.SetSystem(true);
                if (i == 6) GamePause.SetManual(false);
                if (i == 9) GamePause.SetSystem(false);
            };
            for (int i = 0; i < 14; i++) yield return null;
            Finish();
            List<DispatchRecord.PauseEvent> e = EventsSince(mark);
            Assert.That(e.Select(x => x.kind + ":" + x.value).ToArray(), Is.EqualTo(new[] { "Paused:True", "Manual:True", "System:True", "Manual:False", "Paused:False", "System:False" }),
                "a cause that changes no effective pause raises only its own event");
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int manualOff = s.FindIndex(x => x.engineFrame == e[3].engineFrame);
            Assert.That(s[manualOff].paused, Is.True, "manual off while the system pause holds: still paused");
            Assert.That(s[manualOff].gamePauseDeltaMs, Is.Zero);
            int back = s.FindIndex(x => x.engineFrame == e[4].engineFrame);
            Assert.That(s[back].paused, Is.False);
            Assert.That(s[back].gamePauseDeltaMs, Is.Zero, "the system return's frame: delta 0");
        }

        [UnityTest]
        public IEnumerator Overlapping_SystemThenManual_SystemEndsFirst_StaysPaused_ThenTheManualResumeHasTheEnginesDelta()
        {
            Begin("overlap-system-first");
            ScriptStart();
            long mark = DispatchRecord.NextSequence();
            _sampler.beforeSample = i =>
            {
                if (i == 2) GamePause.SetSystem(true);
                if (i == 4) GamePause.SetManual(true);
                if (i == 6) GamePause.SetSystem(false);   // its end is taken at frame 7, the manual pause holds
                if (i == 10) GamePause.SetManual(false);
            };
            for (int i = 0; i < 14; i++) yield return null;
            Finish();
            List<DispatchRecord.PauseEvent> e = EventsSince(mark);
            Assert.That(e.Select(x => x.kind + ":" + x.value).ToArray(), Is.EqualTo(new[] { "Paused:True", "System:True", "Manual:True", "System:False", "Paused:False", "Manual:False" }));
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int systemEnd = s.FindIndex(x => x.engineFrame == e[3].engineFrame);
            Assert.That(s[systemEnd].paused, Is.True, "the system ended while the manual pause holds: still paused");
            Assert.That(s[systemEnd].gamePauseDeltaMs, Is.Zero);
            int resume = s.FindIndex(x => x.engineFrame == e[4].engineFrame);
            Assert.That(s[resume].paused, Is.False);
            Assert.That(s[resume].gamePauseDeltaMs, Is.EqualTo(s[resume].engineDeltaMs).Within(1e-6), "the manual resume in a later frame: the engine's delta");
        }

        [UnityTest]
        public IEnumerator TheSystemEndsFirst_AndTheManualIsReleasedInTheSameFrame_ThatFramesDeltaIsZero_AndTheNextIsNormal()
        {
            Begin("system-end-and-manual-resume-same-frame");
            ScriptStart();
            long mark = DispatchRecord.NextSequence();
            int releaseFrame = -1;
            _sampler.beforeSample = i =>
            {
                if (i == 2) GamePause.SetManual(true);
                if (i == 3) GamePause.SetSystem(true);
                if (i == 6) GamePause.SetSystem(false);   // the end is taken at the first reading of frame 7
                if (i == 7) { releaseFrame = Time.frameCount; GamePause.SetManual(false); }   // the same frame
            };
            for (int i = 0; i < 12; i++) yield return null;
            Finish();
            List<DispatchRecord.PauseEvent> e = EventsSince(mark);
            Assert.That(e.Select(x => x.kind + ":" + x.value).ToArray(), Is.EqualTo(new[] { "Paused:True", "Manual:True", "System:True", "System:False", "Paused:False", "Manual:False" }));
            Assert.That(e[3].engineFrame, Is.EqualTo(releaseFrame), "the system's end was taken in the frame the manual pause was released in");
            Assert.That(e[4].engineFrame, Is.EqualTo(releaseFrame));
            List<DispatchRecord.FrameClockSample> s = _sampler.samples;
            int row = s.FindIndex(x => x.engineFrame == releaseFrame);
            Assert.That(s[row].paused, Is.False, "the effective pause is over at the sample");
            Assert.That(s[row].gamePauseDeltaMs, Is.Zero, "yet the delta is 0: the system's end was taken in this frame (TL review 7)");
            Assert.That(s[row].engineDeltaMs, Is.GreaterThan(0.0), "the engine's own delta is not");
            Assert.That(s[row + 1].gamePauseDeltaMs, Is.EqualTo(s[row + 1].engineDeltaMs).Within(1e-6), "the next frame is normal");
        }

        [UnityTest]
        public IEnumerator TheWorldStartApartFromTheScriptStart_AndAWorkAcrossAPause_RecordsItsPausedOverlap()
        {
            Begin("work-across-pause");
            var pool = NewPool();
            Assert.That(pool.WaitUntilStartupFinished(5000), Is.True);
            var dispatcher = NewDispatcher(pool);
            Assert.That(DispatchObservation.WorkLog, Is.True, "the record's world start turned the work log on");
            for (int i = 0; i < 5; i++) yield return null;   // the world's start well before the script's
            ScriptStart();
            var work = NewHold();
            int frame = 0;
            _sampler.beforeSample = i =>
            {
                dispatcher.BeginFrame(++frame);
                dispatcher.Dispatch();
                if (i == 2) Assert.That(dispatcher.TryEnqueue(WorkPurpose.Speculative, work, out WorkTicket _), Is.True);
                if (i == 5) GamePause.SetManual(true);
                if (i == 15) GamePause.SetManual(false);
                if (i == 20) work.release.Set();
            };
            for (int i = 0; i < 30 && !work.collected; i++) yield return null;
            Assert.That(work.collected, Is.True);
            Finish();
            DispatchRecord.WriteWorld("clock-test", dispatcher, null);
            dispatcher.Shutdown(5000);
            pool.Dispose();
            string[] rows = File.ReadAllLines(Path.Combine(_folder, "dispatch-works-clock-test.csv"));
            Assert.That(rows.Length, Is.EqualTo(2), "one work logged");
            string[] header = rows[0].Split(','), values = rows[1].Split(',');
            double pausedRun = double.Parse(values[Array.IndexOf(header, "pausedRunMs")], System.Globalization.CultureInfo.InvariantCulture);
            DispatchRecord.PauseEvent on = DispatchRecord.Events.First(x => x.kind == "Paused" && x.value), off = DispatchRecord.Events.First(x => x.kind == "Paused" && !x.value);
            double pauseMs = Ms(off.stopwatch - on.stopwatch);
            Assert.That(pausedRun, Is.EqualTo(pauseMs).Within(0.01), "the work ran through the whole pause: its paused overlap is the pause");
            var points = DispatchRecord.Points;
            Assert.That(points[0].kind, Does.StartWith("world start"));
            Assert.That(points.Any(p => p.kind == "script start"), Is.True);
            Assert.That(points.First(p => p.kind == "script start").stopwatch - points[0].stopwatch, Is.GreaterThan(0L), "the script began after the world");
        }
    }
}
