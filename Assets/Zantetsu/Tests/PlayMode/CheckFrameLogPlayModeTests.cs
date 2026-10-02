using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's per-frame rows (2026-10-02, writer "CheckFrames", formerly multi.csv): a recording of their own, its start
    /// naming the walk's CheckHits recording, a "frame" record per row, the summary that ends it; nothing after it. Made here
    /// directly (the check itself runs only in the Player). Each test names its recordings in the test's output; what was
    /// saved is checked after the run (analyze_frames.py, check_writer_jsonl.py CheckFrames "check frames").
    /// </summary>
    public class CheckFrameLogPlayModeTests
    {
        private readonly List<string> _ended = new List<string>();

        private void Capture(string message, string stack, LogType type)
        {
            if (message.StartsWith("[" + SandboxPropSlashPlayerCheck.FrameLogLabel + "] ended:")) _ended.Add(message);
        }

        [SetUp]
        public void Listen()
        {
            _ended.Clear();
            Application.logMessageReceived += Capture;
        }

        [TearDown]
        public void StopListening()
        {
            Application.logMessageReceived -= Capture;
        }

        private static SandboxPropSlashPlayerCheck.CheckFrameRow Row(int frame, double real, int pieces, double? unsimulated, long stepId)
        {
            return new SandboxPropSlashPlayerCheck.CheckFrameRow
            {
                frame = frame, real = real, waves = 1, uncutNpcs = 9, liveFragments = 12, livePieces = pieces, liveConvexes = pieces * 3,
                acceptedOps = 2, pendingOps = 0, incompleteOps = 1, acceptedThisFrame = 1, provisionalThisFrame = 1, finalThisFrame = 0,
                committedThisFrame = 0, unsimulated = unsimulated, stepId = stepId,
            };
        }

        // A walk's frames beside its hits: the frame recording names the hit recording; rows with and without a step clock.
        [UnityTest]
        public IEnumerator Frames_AreARecordingOfTheirOwn_NamingTheWalksHitRecording()
        {
            DevelopmentLogRecording hits = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-frames-multi");
            DevelopmentLogRecording frames = SandboxPropSlashPlayerCheck.BeginFrameLog("multiNpc", 0, "test-frames-multi", hits.Recording);
            SandboxPropSlashPlayerCheck.WriteFrameRow(frames, Row(Time.frameCount, 0.0123, 2, 0.011, 7));
            yield return null;
            SandboxPropSlashPlayerCheck.WriteFrameRow(frames, Row(Time.frameCount, 0.0234, 4, null, -1));   // no step clock: null, -1
            yield return null;
            SandboxPropSlashPlayerCheck.WriteFrameRow(frames, Row(Time.frameCount, 0.0345, 4, 0.0, 8));
            SandboxPropSlashPlayerCheck.EndFrameLog(frames, 3);
            SandboxPropSlashPlayerCheck.EndHitLog(hits, 0);
            Debug.Log("[check frames test] multi: frames recording " + frames.Recording + ", hits recording " + hits.Recording);
            Assert.That(frames.Attempted, Is.EqualTo(5), "start, three rows, summary");
            Assert.That(frames.Accepted, Is.EqualTo(5));
            Assert.That(hits.Attempted, Is.EqualTo(2), "the hit recording holds no frame");
            Assert.That(_ended.Count, Is.EqualTo(1));
            Assert.That(_ended[0], Does.StartWith("[check frames] ended: recording " + frames.Recording + ", attempted 5").And.EndWith("; frames 3"));
        }

        // Two walks (iterations) and a MobPlan walk: recordings of their own, seq from 1 each.
        [UnityTest]
        public IEnumerator TwoIterationsAndMobPlan_AreRecordingsOfTheirOwn()
        {
            DevelopmentLogRecording first = SandboxPropSlashPlayerCheck.BeginFrameLog("multiNpc", 0, "test-frames-iter-00", 0);
            SandboxPropSlashPlayerCheck.WriteFrameRow(first, Row(Time.frameCount, 0.1, 1, 0.0, 1));
            SandboxPropSlashPlayerCheck.EndFrameLog(first, 1);
            yield return null;
            DevelopmentLogRecording second = SandboxPropSlashPlayerCheck.BeginFrameLog("multiNpc", 1, "test-frames-iter-01", 0);
            SandboxPropSlashPlayerCheck.WriteFrameRow(second, Row(Time.frameCount, 0.2, 1, 0.0, 2));
            SandboxPropSlashPlayerCheck.WriteFrameRow(second, Row(Time.frameCount, 0.3, 1, 0.0, 3));
            SandboxPropSlashPlayerCheck.EndFrameLog(second, 2);
            DevelopmentLogRecording mobPlan = SandboxPropSlashPlayerCheck.BeginFrameLog("mobPlan", 0, "test-frames-mobplan", 0);
            SandboxPropSlashPlayerCheck.EndFrameLog(mobPlan, 0);
            Debug.Log("[check frames test] recordings " + first.Recording + ", " + second.Recording + ", " + mobPlan.Recording);
            Assert.That(second.Recording, Is.EqualTo(first.Recording + 1));
            Assert.That(first.Attempted, Is.EqualTo(3));
            Assert.That(second.Attempted, Is.EqualTo(4));
            Assert.That(mobPlan.Attempted, Is.EqualTo(2), "no row: start and summary");
            Assert.That(_ended[2], Does.EndWith("; frames 0"));
        }

        // A row the logger refuses (a NaN check time) is counted; the end names it; the walk's hit recording is untouched.
        [UnityTest]
        public IEnumerator ARefusedRow_IsCounted_AndLeavesTheHitRecordingAlone()
        {
            DevelopmentLogRecording hits = SandboxPropSlashPlayerCheck.BeginHitLog("mobPlan", 0, "test-frames-refused");
            DevelopmentLogRecording frames = SandboxPropSlashPlayerCheck.BeginFrameLog("mobPlan", 0, "test-frames-refused", hits.Recording);
            SandboxPropSlashPlayerCheck.WriteFrameRow(frames, Row(Time.frameCount, double.NaN, 1, 0.0, 1));
            SandboxPropSlashPlayerCheck.WriteFrameRow(frames, Row(Time.frameCount, 0.5, 1, 0.0, 2));
            yield return null;
            SandboxPropSlashPlayerCheck.EndFrameLog(frames, 2);
            SandboxPropSlashPlayerCheck.EndHitLog(hits, 0);
            Debug.Log("[check frames test] refused: frames recording " + frames.Recording + " (seq 2 refused), hits recording " + hits.Recording);
            Assert.That(frames.Refused, Is.EqualTo(1));
            Assert.That(frames.Accepted, Is.EqualTo(3));
            Assert.That(hits.Refused, Is.Zero);
            Assert.That(_ended[0], Does.Contain("invalid value 1"));
        }

        // After the end, no row is written.
        [UnityTest]
        public IEnumerator AfterTheEnd_NoRowIsWritten()
        {
            DevelopmentLogRecording frames = SandboxPropSlashPlayerCheck.BeginFrameLog("multiNpc", 0, "test-frames-after-end", 0);
            SandboxPropSlashPlayerCheck.EndFrameLog(frames, 0);
            long attempted = frames.Attempted;
            yield return null;
            SandboxPropSlashPlayerCheck.WriteFrameRow(frames, Row(Time.frameCount, 0.1, 1, 0.0, 1));
            SandboxPropSlashPlayerCheck.EndFrameLog(frames, 1);
            Assert.That(frames.Attempted, Is.EqualTo(attempted));
            Assert.That(_ended.Count, Is.EqualTo(1));
        }
    }
}
