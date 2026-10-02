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
    /// The check's op records (2026-10-02, tag "op" in the CheckHits recording, formerly multi-ops.csv / building-ops.csv):
    /// written at the summary, all of them before the recording's last summary; the ops' tally apart from the hits' --
    /// whether the mode writes ops at all, how many were to be written (fixed before the writing), tried, and accepted.
    /// Made here directly with records of the test's own (the check itself runs only in the Player). Each test names its
    /// recording in the test's output; what was saved is checked after the run (analyze_ops.py).
    /// </summary>
    public class CheckOpLogPlayModeTests
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

        private static SlashHitConfirmed Hit(long slash, int fragment, ProvisionalCutAcceptance acceptance, int operation)
        {
            return new SlashHitConfirmed(slash, 1.5, false, new LogicalFragmentId(fragment), 0f, acceptance, LogicalCutAdmission.Admitted, new CutOperationId(operation));
        }

        private static SandboxPropSlashPlayerCheck.OpRecord MultiOp(int operation, long slash, int source, int frame, bool pending)
        {
            return new SandboxPropSlashPlayerCheck.OpRecord
            {
                name = "op" + operation + "-npc-a-root", operation = operation, slash = slash, source = source, acceptance = pending ? "Pending" : "Published",
                acceptedFrame = frame, provisionalFrame = pending ? frame + 3 : frame, finalFrame = frame + 4, committedFrame = frame + 5,
                acceptedTime = 0.25, provisionalTime = 0.3, finalTime = 0.35, committedTime = 0.4, ledgerState = "Completed", pendingEnd = null,
                of = "npc-a", child = false,
            };
        }

        // A multi-NPC walk: a hit, its op (all fields), the ops written before the last summary; the tally in the summary.
        [UnityTest]
        public IEnumerator Ops_AreWrittenBeforeTheLastSummary_WithTheirTally()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-ops-multi");
            var ops = new SandboxPropSlashPlayerCheck.OpsTally { applicable = true };
            int frame = Time.frameCount;
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(1, 3, ProvisionalCutAcceptance.Published, 7), frame, "npc-a", false, 4);
            yield return null;
            SandboxPropSlashPlayerCheck.PlanOps(log, ref ops, 2);
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, MultiOp(7, 1, 3, frame, false));
            SandboxPropSlashPlayerCheck.OpRecord second = MultiOp(8, 1, 9, frame, true);
            second.provisionalTime = double.NaN;   // not observed: written null
            second.provisionalFrame = -1;
            second.pendingEnd = "ended without publication at frame 99 phase=record ended";
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, second);
            SandboxPropSlashPlayerCheck.EndHitLog(log, 1, ops);
            Debug.Log("[check ops test] multi: recording " + log.Recording);
            Assert.That(ops.planned, Is.EqualTo(2));
            Assert.That(ops.attempted, Is.EqualTo(2));
            Assert.That(ops.accepted, Is.EqualTo(2));
            Assert.That(log.Attempted, Is.EqualTo(5), "start, hit, two ops, summary");
            Assert.That(_ended[0], Does.EndWith("; hits 1; ops planned 2, attempted 2, accepted 2"));
        }

        // A building walk: the building's columns; an operation-0 row (a hull Pending: no operation of its own); the same
        // operation twice (kept as two records); no observed times.
        [UnityTest]
        public IEnumerator BuildingOps_KeepOperationZeroAndRepeatedRows()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("building", 0, "test-ops-building");
            var ops = new SandboxPropSlashPlayerCheck.OpsTally { applicable = true };
            int frame = Time.frameCount;
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(2, 5, ProvisionalCutAcceptance.Published, 11), frame, "building", false, 1);
            SandboxPropSlashPlayerCheck.WriteHit(log, Hit(2, 6, ProvisionalCutAcceptance.Pending, 0), frame, "building", true, 1);
            yield return null;
            var building = new SandboxPropSlashPlayerCheck.OpRecord
            {
                name = "op11-building-root", operation = 11, slash = 2, source = 5, acceptance = "Published",
                acceptedFrame = frame, provisionalFrame = frame, finalFrame = frame + 1, committedFrame = frame + 1,
                acceptedTime = double.NaN, provisionalTime = double.NaN, finalTime = double.NaN, committedTime = double.NaN,
                ledgerState = "Completed", sourceDepth = 0, sourceAnchors = 21, sourceFixed = true, cut = "horizontal",
                normalX = 0.239257455f, normalY = 0.9641157f, normalZ = 0.115051821f, positive = 12, negative = 13,
            };
            var hull = new SandboxPropSlashPlayerCheck.OpRecord
            {
                name = "op0-building-child", operation = 0, slash = 2, source = 6, acceptance = "Pending", hull = true,
                acceptedFrame = frame, provisionalFrame = -1, finalFrame = -1, committedFrame = -1,
                acceptedTime = double.NaN, provisionalTime = double.NaN, finalTime = double.NaN, committedTime = double.NaN,
                ledgerState = "none", sourceDepth = -1, sourceAnchors = -1, sourceFixed = false, cut = null, positive = 0, negative = 0,
                pendingEnd = "ended without publication at frame 7 phase=record ended",
            };
            SandboxPropSlashPlayerCheck.PlanOps(log, ref ops, 3);
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, building);
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, hull);
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, building);   // the same operation again: a second record
            SandboxPropSlashPlayerCheck.EndHitLog(log, 2, ops);
            Debug.Log("[check ops test] building: recording " + log.Recording);
            Assert.That(ops.attempted, Is.EqualTo(3));
            Assert.That(ops.accepted, Is.EqualTo(3));
            Assert.That(_ended[0], Does.EndWith("; hits 2; ops planned 3, attempted 3, accepted 3"));
        }

        // A mode that writes ops, with nothing accepted: zero ops planned -- apart from a mode that writes none at all.
        [UnityTest]
        public IEnumerator NoneAccepted_IsZeroPlanned_NotTheSameAsNotApplicable()
        {
            DevelopmentLogRecording zero = SandboxPropSlashPlayerCheck.BeginHitLog("building", 0, "test-ops-zero");
            var zeroOps = new SandboxPropSlashPlayerCheck.OpsTally { applicable = true };
            SandboxPropSlashPlayerCheck.PlanOps(zero, ref zeroOps, 0);
            SandboxPropSlashPlayerCheck.EndHitLog(zero, 0, zeroOps);
            yield return null;
            DevelopmentLogRecording mobPlan = SandboxPropSlashPlayerCheck.BeginHitLog("mobPlan", 0, "test-ops-not-applicable");
            var none = new SandboxPropSlashPlayerCheck.OpsTally { applicable = false };
            SandboxPropSlashPlayerCheck.PlanOps(mobPlan, ref none, 4);   // not applicable: nothing planned
            SandboxPropSlashPlayerCheck.WriteOpCounted(mobPlan, ref none, MultiOp(3, 1, 2, Time.frameCount, false));   // nor written
            SandboxPropSlashPlayerCheck.EndHitLog(mobPlan, 0, none);
            yield return null;
            DevelopmentLogRecording unreached = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-ops-unreached");
            var unreachedOps = new SandboxPropSlashPlayerCheck.OpsTally { applicable = true };   // the summary never reached the ops
            SandboxPropSlashPlayerCheck.EndHitLog(unreached, 0, unreachedOps);
            Debug.Log("[check ops test] zero " + zero.Recording + ", not applicable " + mobPlan.Recording + ", unreached " + unreached.Recording);
            Assert.That(zeroOps.planned, Is.EqualTo(0));
            Assert.That(none.planned, Is.Null);
            Assert.That(none.attempted, Is.Zero);
            Assert.That(mobPlan.Attempted, Is.EqualTo(2), "start and summary only");
            Assert.That(_ended[0], Does.EndWith("; ops planned 0, attempted 0, accepted 0"));
            Assert.That(_ended[1], Does.EndWith("; ops not applicable"));
            Assert.That(_ended[2], Does.EndWith("; ops applicable, not written (no summary reached them)"));
        }

        // An op the logger refuses (a NaN normal) is tried, not accepted: the tally shows it, the saved file lacks its seq.
        [UnityTest]
        public IEnumerator ARefusedOp_IsAttemptedNotAccepted()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("building", 0, "test-ops-refused");
            var ops = new SandboxPropSlashPlayerCheck.OpsTally { applicable = true };
            SandboxPropSlashPlayerCheck.PlanOps(log, ref ops, 1);
            var bad = new SandboxPropSlashPlayerCheck.OpRecord { name = "op5-building-root", operation = 5, slash = 1, source = 2, acceptance = "Published", normalX = float.NaN };
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, bad);
            yield return null;
            SandboxPropSlashPlayerCheck.EndHitLog(log, 0, ops);
            Debug.Log("[check ops test] refused: recording " + log.Recording);
            Assert.That(ops.attempted, Is.EqualTo(1));
            Assert.That(ops.accepted, Is.Zero);
            Assert.That(_ended[0], Does.Contain("invalid value 1").And.EndWith("; ops planned 1, attempted 1, accepted 0"));
        }

        // After the end, no op is written or counted.
        [UnityTest]
        public IEnumerator AfterTheEnd_NoOpIsWritten()
        {
            DevelopmentLogRecording log = SandboxPropSlashPlayerCheck.BeginHitLog("multiNpc", 0, "test-ops-after-end");
            var ops = new SandboxPropSlashPlayerCheck.OpsTally { applicable = true };
            SandboxPropSlashPlayerCheck.PlanOps(log, ref ops, 0);
            SandboxPropSlashPlayerCheck.EndHitLog(log, 0, ops);
            long attempted = log.Attempted;
            yield return null;
            SandboxPropSlashPlayerCheck.WriteOpCounted(log, ref ops, MultiOp(4, 1, 2, Time.frameCount, false));
            Assert.That(log.Attempted, Is.EqualTo(attempted));
            Assert.That(ops.attempted, Is.Zero);
        }
    }
}
