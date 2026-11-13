using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// SPLIT-CANDIDATES-1: the count of the fragments an additional-split attempt could be issued for, as the probe takes it -- none
    /// before a cut, the cut pieces after, the fragment of the attempt out left aside while another piece is still counted -- and
    /// that taking it changes nothing of the walk or of the attempt out. Then the turn's own sampling: one sample a frame, with what
    /// the turn's evaluations came to, written at the end.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static int CandidatesByExamine(CutWorldRoot root, CutSpatialSplit split, LogicalFragmentId? except)
        {
            var fragments = new List<LogicalFragmentId>();
            root.Owners.CopyFragmentsTo(fragments);
            int n = 0;
            foreach (LogicalFragmentId f in fragments)
            {
                if (except.HasValue && f.Equals(except.Value)) continue;
                if (split.Examine(f) == SpatialSplitPass.Candidate) n++;
            }

            return n;
        }

        [UnityTest]
        public IEnumerator SplitCandidates_AreCountedAsTheWalkWouldSeeThem_BesideTheAttemptOut_AndTheLookChangesNothing()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            LogicalFragmentId first = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f, trunkBottomBone: 1);
            LogicalFragmentId second = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f), true, handLowX: 0.8f, trunkBottomBone: 1);
            yield return null;
            CutSpatialSplitSettings settings = split.Settings;

            // 1. Nothing cut: no candidate.
            CutSpatialSplit.CandidateSample none = split.TakeCandidateSample(in settings);
            Assert.That(none.candidates == 0 && none.attempt == "none" && !none.attemptFragmentSeen, Is.True, "two bodies, not cut: " + none.candidates + " of " + none.owners);

            // 2. One body cut: its pieces that could be attempted are counted (the split is closed: nothing is issued).
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, first, sides);
            yield return null;
            CutSpatialSplit.CandidateSample one = split.TakeCandidateSample(in settings);
            int expectedOne = CandidatesByExamine(root, split, null);
            Assert.That(expectedOne, Is.GreaterThanOrEqualTo(1), "the layout: the lower side can be attempted");
            Assert.That(one.candidates, Is.EqualTo(expectedOne));

            // 3. Both cut: two or more, the real number.
            var secondSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, second, secondSides);
            yield return null;
            CutSpatialSplit.CandidateSample two = split.TakeCandidateSample(in settings);
            int expectedTwo = CandidatesByExamine(root, split, null);
            Assert.That(expectedTwo, Is.GreaterThanOrEqualTo(2), "the layout: a piece of each body");
            Assert.That(two.candidates, Is.EqualTo(expectedTwo));
            Assert.That(two.scanMs, Is.GreaterThanOrEqualTo(0.0));
            Assert.That(split.Issued, Is.EqualTo(0), "the look issued nothing");

            // 4. An attempt out (issued, then held where its publication would need the budget): its fragment is left aside, the
            //    other body's piece is still counted -- and the look leaves the walk and the attempt as they were.
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "an attempt was issued");
            remaining[0] = 0.0;
            Assert.That(split.TryGetAttemptFragmentForTests(out LogicalFragmentId attemptFragment, out long serial), Is.True);
            (int cursor, int round, bool passedForDisplayState, bool idle, bool roundWanting) walkBefore = split.WalkStateForTests;
            long issuedBefore = split.Issued, turnsBefore = split.Turns, examinedBefore = split.Examined;
            int budgetFrameBefore = split.LastBudgetDecision.Frame;
            SpatialSplitHold holdBefore = split.LastHold;
            CutSpatialSplit.CandidateSample during = split.TakeCandidateSample(in settings);
            Assert.That(during.attempt, Is.Not.EqualTo("none"), "the attempt's state is told: " + during.attempt);
            Assert.That(during.attemptSerial, Is.EqualTo(serial));
            Assert.That(during.attemptFragmentSeen, Is.True, "its fragment was among the owners and left aside");
            // (With several attempts out at once every fragment with one out is left aside, not the first alone.)
            if (split.MaxAttempts == 1) Assert.That(during.candidates, Is.EqualTo(CandidatesByExamine(root, split, attemptFragment)));
            // (With several attempts out at once the other body's piece was issued in the same turn: no candidate is left.)
            if (split.MaxAttempts == 1) Assert.That(during.candidates, Is.GreaterThanOrEqualTo(1), "the other body's piece, not issued, is counted while the attempt is out");
            Assert.That(split.WalkStateForTests, Is.EqualTo(walkBefore), "the walk's place and flags as they were");
            // (Under the several-attempts control the scan is the walk's own: its looks are the walk's looks.)
            Assert.That(split.Issued == issuedBefore && split.Turns == turnsBefore && (split.Examined == examinedBefore || split.MaxAttempts > 1), Is.True, "no turn, no look of the walk, no issue counted");
            Assert.That(split.LastBudgetDecision.Frame == budgetFrameBefore && split.LastHold == holdBefore, Is.True, "no budget decision written, no hold");
            Assert.That(split.TryGetAttemptFragmentForTests(out LogicalFragmentId stillFragment, out long stillSerial) && stillFragment.Equals(attemptFragment) && stillSerial == serial, Is.True, "the same attempt out");

            // 5. The turn's own sampling: a sample a frame, with the turn's evaluation of that frame, written at the end.
            string folder = Path.Combine(Path.GetTempPath(), "zantetsu-candidate-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                CutSpatialSplit.CandidateProbePath = Path.Combine(folder, "candidates.csv");
                int before = split.CandidateSamples.Count;
                int frame0 = Time.frameCount;
                for (int k = 0; k < 6; k++) yield return null;
                int taken = split.CandidateSamples.Count - before;
                Assert.That(taken, Is.InRange(5, 7), "one sample a frame: " + taken + " over " + (Time.frameCount - frame0) + " frames");
                var frames = new HashSet<int>();
                for (int k = before; k < split.CandidateSamples.Count; k++) Assert.That(frames.Add(split.CandidateSamples[k].frame), Is.True, "one sample a frame at most");
                CutSpatialSplit.CandidateSample last = split.CandidateSamples[split.CandidateSamples.Count - 2];   // a frame whose turn has ended
                TestContext.Out.WriteLine("a sampled frame: attempt " + last.attempt + ", candidates " + last.candidates + ", hold " + last.hold + ", budget " + last.budget + " (" + last.budgetStage + "), scan ms " + last.scanMs);
                if (last.attempt.StartsWith("collected", StringComparison.Ordinal))
                {
                    // (a publication held past its retry bound goes through on a short budget: the decision is told either way)
                    Assert.That(last.hold != "not evaluated" && last.budget == "short", Is.True, "the turn's own decision of that frame: " + last.hold + ", " + last.budget);
                }
                else if (last.attempt != "none" && split.MaxAttempts == 1)
                {
                    // (Under the several-attempts control the turn asks its issue gate with attempts out: it does evaluate.)
                    Assert.That(last.hold == "not evaluated" && last.budget == "not evaluated", Is.True, "a turn with the attempt not collected evaluates nothing");
                }

                split.WriteCandidateProbe();
                string[] files = Directory.GetFiles(folder, "candidates*.csv");
                files = Array.FindAll(files, f => !f.EndsWith(".waits.csv", StringComparison.Ordinal));
                Assert.That(files.Length, Is.EqualTo(1));
                Assert.That(File.Exists(files[0] + ".waits.csv"), Is.True, "the waits beside it");
                string[] lines = File.ReadAllLines(files[0]);
                Assert.That(lines.Length, Is.EqualTo(split.CandidateSamples.Count + 1), "a header and a line a sample");
                StringAssert.StartsWith("frame,seconds,owners,candidates,", lines[0]);
                Assert.That(File.Exists(files[0] + ".summary.txt"), Is.True);
                StringAssert.Contains("samples " + split.CandidateSamples.Count, split.DescribeCandidateProbe());
            }
            finally
            {
                CutSpatialSplit.CandidateProbePath = null;
                try { Directory.Delete(folder, true); } catch (Exception) { }
            }
        }
    }
}
