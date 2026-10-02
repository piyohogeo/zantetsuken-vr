using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The check's reading of a stop's transition (Sandbox/HullDropStartTracker, TL 2026-10-01): a drop stopped by a re-cut,
    /// the stopping hit's children born at the stopped pose, the next drop's start checked once and kept; a held member
    /// that does not move is held to its stop pose; a child moved on and cut again is not held to an old ancestor's stop.
    /// A child born off the stop pose fails, and stays failed after its drop completes; a stop whose start was never
    /// checked fails. The trial stops a drop and publishes the stopping hit's children (their drop begun) in one Step, so no
    /// frame lies between them where a child could be moved: the off start is made by shifting the start the record shows
    /// the tracker (the observation), and the unchecked stop by a re-cut the ledger refuses after the stop (no children).
    /// And the drop with the game's own gravity of -4.9 m/s2.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private CutWorldRoot NewStartWorld(int maxIncompleteCuts = 0)
        {
            CutWorldRoot root = NewKinematicWorld(more: profile =>
            {
                SetPrivate(profile, "buildingHullGeometryLimit", 16);
                SetPrivate(profile, "buildingHullDropExponent", 1f);
                SetPrivate(profile, "buildingHullDropBaseMetres", 0.5f);
                if (maxIncompleteCuts > 0) SetPrivate(profile, "maxIncompleteCuts", maxIncompleteCuts);
            });
            Assert.That(root.Hulls.Settings.LimitOn, Is.True);
            return root;
        }

        /// <summary>Frames until a condition, the tracker reading each one; an optional action before each reading.</summary>
        private static IEnumerator Tracked(CutWorldRoot root, HullDropStartTracker tracker, System.Func<bool> done, string what, System.Action before = null)
        {
            for (int i = 0; i < 600 && !done(); i++)
            {
                yield return null;
                before?.Invoke();
                tracker.Frame(root.Hulls, root.Ledger);
            }

            if (!done()) WriteDrops(root.Hulls, what + ": not reached");
            Assert.That(done(), Is.True, what + " (drops " + root.Hulls.DropRecords.Count + "; " + root.Hulls.DescribeUnsettled() + ")");
        }

        private static void WriteTracker(HullDropStartTracker tracker, string what, out bool passed, out string detail)
        {
            passed = tracker.Judge(out detail);
            TestContext.Out.WriteLine(what + ": " + (passed ? "passed" : "FAILED") + " -- " + detail);
            foreach (string line in tracker.Lines()) TestContext.Out.WriteLine("  " + line);
        }

        /// <summary>The level cut, then the upright re-cut once the first drop is a third of the way: the tracker follows from the start.</summary>
        private IEnumerator StopByAReCut(CutWorldRoot root, HullDropStartTracker tracker, System.Action beforeEachFrame = null)
        {
            BuildingHullFusion h = root.Hulls;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            FrameClock(h);
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            yield return Tracked(root, tracker, () => h.DropRecords.Count == 1 && h.DropRecords[0].Phase > 0.3 && h.DisplayOperationsOpen == 0, "the first drop a third of the way");
            Evaluate(detector, Upright(2, 0.1f), 2);
            yield return Tracked(root, tracker, () => h.DropRecords[0].end == "stopped", "the first drop stopped", beforeEachFrame);
        }

        /// <summary>
        /// **The child starts at the stop pose and completes; cut again, it is not held to the old ancestor's stop; the member
        /// that did not move is held to its stop pose all along.**
        /// </summary>
        [UnityTest]
        public IEnumerator HullDropStart_TheChildStartsAtTheStop_CutAgainIsNotHeldToIt_AndTheStillOneIsHeld()
        {
            CutWorldRoot root = NewStartWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            yield return null;
            var tracker = new HullDropStartTracker();
            yield return StopByAReCut(root, tracker);
            yield return Tracked(root, tracker, () => h.DropRecords.Count == 2 && h.DropRecords[1].end == "completed" && Quiet(h), "the re-cut's drop completed");
            int starts = tracker.Starts.Count, readsBefore = tracker.HoldReads;
            Assert.That(starts, Is.GreaterThan(0), "the child's start checked");
            Assert.That(System.Linq.Enumerable.All(tracker.Starts, s => s.ok), Is.True, "at the stop pose");

            // The still child stays held; then a cut across everything (the moved child and the still one): no old stop asked of them.
            yield return Tracked(root, tracker, () => tracker.HoldReads >= readsBefore + 5, "the still child held over more frames");
            var detector = new SlashHitDetector(root, in k_hitSettings);
            Evaluate(detector, Across(3, 0.1f), 3);
            yield return Tracked(root, tracker, () => h.DropRecords.Count == 3 && h.DropRecords[2].end == "completed" && Quiet(h), "the third cut's drop completed");
            WriteTracker(tracker, "start at the stop, cut again", out bool passed, out string detail);
            Assert.That(passed, Is.True, detail);
            Assert.That(tracker.Starts.Count, Is.EqualTo(starts), "the third drop is not held to the old stop");
            Assert.That(tracker.HoldReads, Is.GreaterThan(readsBefore + 4), "the still child read while held");
            Assert.That(tracker.MaxHoldOff, Is.LessThan(HullDropStartTracker.Tolerance), "it kept its stop pose");
            Assert.That(tracker.HoldsEndedByAnotherCut, Is.GreaterThan(0), "the still child's hold ended by the third cut, not carried to its children");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A child's start off the stop pose fails, and stays failed after its drop completes.** The new drop's record
        /// shows its members' starts 1 cm off before the tracker reads it (the observation shifted: the trial gives no frame
        /// between the stop and the children's start where a child could be moved).
        /// </summary>
        [UnityTest]
        public IEnumerator HullDropStart_AChildBornOffTheStop_Fails_AndStaysFailedAfterItCompletes()
        {
            CutWorldRoot root = NewStartWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            yield return null;
            var tracker = new HullDropStartTracker();
            bool shifted = false;
            System.Action shift = () =>
            {
                if (shifted || h.DropRecords.Count < 2) return;
                foreach (BuildingHullFusion.DropMember m in h.DropRecords[1].members) m.from += new Vector3(0f, 0f, 0.01f);
                shifted = true;
            };
            yield return StopByAReCut(root, tracker, shift);
            yield return Tracked(root, tracker, () => shifted, "the new drop's record shifted before the tracker read it", shift);
            yield return Tracked(root, tracker, () => h.DropRecords.Count == 2 && h.DropRecords[1].end == "completed" && Quiet(h), "the re-cut's drop completed");
            WriteTracker(tracker, "a child's start shown 1 cm off", out bool passed, out string detail);
            Assert.That(passed, Is.False, "failed");
            Assert.That(System.Linq.Enumerable.Any(tracker.Starts, s => !s.ok && s.off > 0.009 && s.off < 0.011), Is.True, "the start found 1 cm off");
            Assert.That(h.DropRecords[1].end, Is.EqualTo("completed"), "its drop completed");
            var j = new SectionJudgement();
            j.Expect(passed, "[hull drop] the next drop started from the stop");
            Assert.That(j.Code, Is.Not.Zero, "a non-zero code");
            yield return Tracked(root, tracker, () => tracker.HoldReads > 0, "more frames");
            Assert.That(tracker.Judge(out _), Is.False, "still failed later");
            yield return EndWorld(root);
        }

        /// <summary>**A still member moved off its stop pose before any drop moved it fails.**</summary>
        [UnityTest]
        public IEnumerator HullDropStart_AStillMemberMovedOffItsStop_Fails()
        {
            CutWorldRoot root = NewStartWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            yield return null;
            var tracker = new HullDropStartTracker();
            yield return StopByAReCut(root, tracker);
            yield return Tracked(root, tracker, () => h.DropRecords.Count == 2 && h.DropRecords[1].end == "completed" && Quiet(h), "the re-cut's drop completed");
            // The still child of the stopped member: a child of its operation that the re-cut's drop does not move.
            HullGroup.DisplayMember still = null;
            foreach (CutOperationId op in h.Hits[1].displayOperations)
            {
                Assert.That(root.Ledger.TryGetOperation(op, out LogicalCutOperation o), Is.True);
                if (o.source != h.DropRecords[0].members[0].fragment) continue;
                foreach (HullGroup g in h.Groups)
                    foreach (HullGroup.DisplayMember m in g.Members)
                        if ((m.fragment == o.positive || m.fragment == o.negative) && !h.DropRecords[1].members.Exists(d => d.fragment == m.fragment)) still = m;
            }

            Assert.That(still, Is.Not.Null, "a still child held");
            still.root.transform.localPosition += new Vector3(0.01f, 0f, 0f);
            yield return Tracked(root, tracker, () => tracker.HoldFailures.Count > 0, "the hold read off");
            WriteTracker(tracker, "a still member moved", out bool passed, out string detail);
            Assert.That(passed, Is.False);
            yield return EndWorld(root);
        }

        /// <summary>**A stop whose start was never checked (the re-cut refused by the ledger after the stop: no children) is not a pass.**</summary>
        [UnityTest]
        public IEnumerator HullDropStart_AStopWithNoStartChecked_IsNotAPass()
        {
            CutWorldRoot root = NewStartWorld(maxIncompleteCuts: 1);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            yield return null;
            var tracker = new HullDropStartTracker();
            yield return StopByAReCut(root, tracker);
            yield return Tracked(root, tracker, () => !h.Hits[1].IsPending && Quiet(h), "the re-cut answered");
            TestContext.Out.WriteLine("the re-cut: " + h.Hits[1].outcome);
            Assert.That(h.Hits[1].outcome, Does.StartWith("Refused"), "refused by the ledger after the stop");
            Assert.That(h.Hits[1].displayOperations.Count, Is.Zero, "no children");
            Assert.That(h.DropRecords.Count, Is.EqualTo(1), "no new drop");
            WriteTracker(tracker, "stopped, no start yet", out bool passed, out string detail);
            Assert.That(tracker.Stops, Is.EqualTo(1));
            Assert.That(passed, Is.False, "not exercised: not a pass");
            StringAssert.Contains("stops with no start checked [1]", detail);
            yield return EndWorld(root);
        }

        /// <summary>**The game's gravity of -4.9 m/s2: the arc from the setting, 4.9 T^2 / 8 (about 3.83 cm) at the middle; x0 + d at the end.**</summary>
        [UnityTest]
        public IEnumerator HullDrop_TheGamesGravityOfMinus4Point9_GivesItsOwnArc()
        {
            Vector3 gravityBefore = Physics.gravity;
            Physics.gravity = new Vector3(0f, -4.9f, 0f);
            try
            {
                CutWorldRoot root = NewKinematicWorld();
                BuildingHullFusion h = root.Hulls;
                AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
                var detector = new SlashHitDetector(root, in k_hitSettings);
                yield return null;
                FrameClock(h);
                Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
                var p = new PathReads();
                yield return UntilDrops(root, () => h.DropRecords.Count == 1 && h.DropRecords[0].end != null && Quiet(h), p, "the drop ended");
                WriteDrops(h, "gravity -4.9");
                BuildingHullFusion.DropRecord r = h.DropRecords[0];
                double expectedArc = -Physics.gravity.y * r.seconds * r.seconds / 8.0;
                TestContext.Out.WriteLine("the arc at the middle from the setting " + expectedArc.ToString("R") + " m; read up to " + p.maxArcUp.ToString("R") + " m (" + p.mid + " reads near the middle), most off the curve " + p.maxOff.ToString("R") + " m");
                Assert.That(r.gravityWorld.y, Is.EqualTo(-4.9f), "g read from the game at the start");
                Assert.That(expectedArc, Is.EqualTo(0.03828).Within(0.00001), "about 3.83 cm for 4.9 m/s2 and 0.25 s");
                Assert.That(p.maxOff, Is.LessThan(1e-5), "on the curve at every frame");
                Assert.That(p.maxArcUp, Is.EqualTo(expectedArc).Within(0.003), "lifted by the setting's arc near the middle");
                AssertCompletedAtEnd(r, "the drop");
                yield return EndWorld(root);
            }
            finally
            {
                Physics.gravity = gravityBefore;
            }
        }
    }
}
