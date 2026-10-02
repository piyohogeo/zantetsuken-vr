using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's hit record (2026-10-02, writer "CheckHits", SandboxPropSlashPlayerCheck.HitLog.cs): one recording a walk,
    /// its seq and counts of its own, a start, a record per observed hit and the summary that ends it; nothing after the end.
    /// Made here directly, with hits of the test's own (the check itself runs only in the Player). Each test names its
    /// recording and seq in the test's output, so that the saved session file can be checked after the run
    /// (Tools/DevelopmentLog/check_writer_jsonl.py CheckHits "check hits"). A complete record says nothing about a check's verdict.
    /// </summary>
    public class CheckHitLogPlayModeTests
    {
        private readonly List<string> _ended = new List<string>();

        private void Capture(string message, string stack, LogType type)
        {
            if (message.StartsWith("[" + SandboxPropSlashPlayerCheck.HitLogLabel + "] ended:")) _ended.Add(message);
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

        private static SlashHitConfirmed Hit(long slash, double at, int fragment, float side, ProvisionalCutAcceptance acceptance, int operation)
        {
            return new SlashHitConfirmed(slash, at, false, new LogicalFragmentId(fragment), side, acceptance, LogicalCutAdmission.Admitted, new CutOperationId(operation));
        }

        // No hit at all, ended normally: the start and the summary, both accepted, the end line naming them.
        [UnityTest]
        public IEnumerator NoHit_EndsNormally_StartAndSummaryOnly()
        {
            yield return null;
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-no-hit");
            yield return null;
            SandboxPropSlashPlayerCheck.EndHitLog(log, 0);
            Assert.That(log.Ended, Is.True);
            Assert.That(log.Attempted, Is.EqualTo(2), "start and summary");
            Assert.That(log.Accepted, Is.EqualTo(2));
            Assert.That(_ended.Count, Is.EqualTo(1));
            Assert.That(_ended[0], Does.StartWith("[check hits] ended: recording " + log.Recording + ", attempted 2 (seq 1..2, the summary last), accepted 2, refused: queue full 0, unavailable 0, invalid value 0, disabled 0"));
            Assert.That(_ended[0], Does.EndWith("; hits 0; ops not applicable"), "a recording made without ops: not applicable, not zero");
        }

        // Two walks one after the other (two iterations): two recordings, each with its own seq and counts and its own end.
        [UnityTest]
        public IEnumerator TwoIterations_TwoRecordings_EachCountedAndEndedOnItsOwn()
        {
            DevelopmentLogRecording first = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-iter-00");
            SandboxPropSlashPlayerCheck.WriteHit(first, Hit(1, 10.5, 3, 0f, ProvisionalCutAcceptance.Published, 7), Time.frameCount, "npc-a", false, 4);
            yield return null;
            SandboxPropSlashPlayerCheck.WriteHit(first, Hit(1, 10.6, 9, 1f, ProvisionalCutAcceptance.EmptySide, 0), Time.frameCount, "npc-a", true, 5);
            SandboxPropSlashPlayerCheck.EndHitLog(first, 2);
            yield return null;
            DevelopmentLogRecording second = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 1, "test-iter-01");
            SandboxPropSlashPlayerCheck.WriteHit(second, Hit(2, 20.25, 4, -1f, ProvisionalCutAcceptance.Pending, 8), Time.frameCount, "npc-b", true, 0);
            SandboxPropSlashPlayerCheck.EndHitLog(second, 1);
            Debug.Log("[check hits test] two iterations: recordings " + first.Recording + " and " + second.Recording);
            Assert.That(second.Recording, Is.EqualTo(first.Recording + 1), "a recording of its own");
            Assert.That(first.Attempted, Is.EqualTo(4), "start, two hits, summary");
            Assert.That(second.Attempted, Is.EqualTo(3), "start, one hit, summary: seq from 1 again");
            Assert.That(first.Accepted + second.Accepted, Is.EqualTo(7));
            Assert.That(_ended.Count, Is.EqualTo(2));
            Assert.That(_ended[0], Does.StartWith("[check hits] ended: recording " + first.Recording + ", attempted 4"));
            Assert.That(_ended[1], Does.StartWith("[check hits] ended: recording " + second.Recording + ", attempted 3"));
        }

        // A walk that ends early (its scenario not finished) still ends its record at the records' close: the record is then
        // complete, which is a statement about the record only -- whether the scenario passed is the check's own verdict.
        [UnityTest]
        public IEnumerator AnEarlyEnd_EndsTheRecord_TheRecordAloneIsNoVerdict()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("building", 0, "test-early-end");
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(5, 3.0, 2, 0f, ProvisionalCutAcceptance.NotAccepted, 0), Time.frameCount, "building", false, 1);
            yield return null;
            SandboxPropSlashPlayerCheck.EndHitLog(log, 1);   // as the records' close of an early ending would
            Assert.That(log.Ended, Is.True);
            Assert.That(log.Accepted, Is.EqualTo(log.Attempted), "complete as a record");
            Assert.That(_ended[0], Does.EndWith("; hits 1; ops not applicable"));
        }

        // A record the logger refuses (a NaN input time) is counted as refused, its seq stays taken, and the record still ends:
        // the end line then names the refusal, and the saved file lacks that seq (checked after the run).
        [UnityTest]
        public IEnumerator ARefusedRecord_IsCounted_AndTheEndNamesIt()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("mobPlan", 0, "test-refused");
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(9, double.NaN, 2, 0f, ProvisionalCutAcceptance.Published, 3), Time.frameCount, "npc-c", false, 2);
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(9, 1.25, 6, 0f, ProvisionalCutAcceptance.Published, 4), Time.frameCount, "npc-c", false, 2);
            yield return null;
            SandboxPropSlashPlayerCheck.EndHitLog(log, 2);
            Debug.Log("[check hits test] refused: recording " + log.Recording + " seq 2 refused (NaN at)");
            Assert.That(log.Attempted, Is.EqualTo(4));
            Assert.That(log.Accepted, Is.EqualTo(3));
            Assert.That(log.Refused, Is.EqualTo(1));
            Assert.That(_ended[0], Does.Contain("invalid value 1"));
        }

        // After the end nothing more is tried: a hit or a second end adds no record and no end line.
        [UnityTest]
        public IEnumerator AfterTheEnd_NothingMoreIsRecorded()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-after-end");
            SandboxPropSlashPlayerCheck.EndHitLog(log, 0);
            long attempted = log.Attempted, accepted = log.Accepted;
            yield return null;
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(3, 2.0, 2, 0f, ProvisionalCutAcceptance.Published, 3), Time.frameCount, "npc-d", false, 0);
            SandboxPropSlashPlayerCheck.EndHitLog(log, 1);
            Assert.That(log.Attempted, Is.EqualTo(attempted), "nothing tried after the end");
            Assert.That(log.Accepted, Is.EqualTo(accepted));
            Assert.That(_ended.Count, Is.EqualTo(1), "one end line");
        }
    }
}
