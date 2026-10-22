using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// PCVR's display step as the player loop runs it (DESIGN 4.4, 2026-10-20), on a 90 Hz display clock put in for the
    /// test (the Editor has no XR): frames longer than two steps owe two, and each such frame takes one double step --
    /// one <c>Physics.Simulate(1/45)</c>, the body moved by it, the step id by one -- when its estimate fits; a double
    /// step that does not fit falls back to the normal one; the verification step is a normal step; a pause owes nothing.
    /// The arithmetic is <c>DisplayStepClockTests</c>'.
    /// </summary>
    public sealed class CutPhysicsDisplayStepPlayModeTests
    {
        private const float Speed = 3f;
        private readonly List<GameObject> _objects = new List<GameObject>();
        private ManualPhysicsClock _before;

        [SetUp]
        public void PutTheDisplayClockIn()
        {
            _before = CutPhysicsStep.UseClockForTest(new ManualPhysicsClock(90, Stopwatch.Frequency, doubleStep: true), PhysicsStepMode.Display);
        }

        [TearDown]
        public void PutTheSessionsClockBack()
        {
            CutPhysicsStep.SetPaused(false);
            CutPhysicsStep.UseClockForTest(_before, PhysicsStepMode.Fixed);
            CutPhysicsStep.SettleFramePeriod();
            CutPhysicsStep.InjectExpectedCostForTest(0.0);
            foreach (GameObject go in _objects)
            {
                if (go != null) Object.Destroy(go);
            }

            _objects.Clear();
        }

        /// <summary>
        /// Sleeps after the frame's decision and collections (CutPhysicsStep's after-collect turn), so the next frame
        /// starts later -- more owed -- with its whole budget. (Not at the end of the frame: in batch mode
        /// WaitForEndOfFrame did not come, 2026-10-20 play-all-1.)
        /// </summary>
        private sealed class LongFrames : MonoBehaviour
        {
            internal int sleepMs = 30;
            private System.Action _sleep;

            private void OnEnable()
            {
                _sleep = () => { if (sleepMs > 0) System.Threading.Thread.Sleep(sleepMs); };
                CutPhysicsStep.JoinAfterCollect(_sleep);
            }

            private void OnDisable()
            {
                CutPhysicsStep.LeaveAfterCollect(_sleep);
            }
        }

        /// <summary>Reads, at the start of each frame's updates, the last decision and the body.</summary>
        [DefaultExecutionOrder(-30000)]
        private sealed class Reader : MonoBehaviour
        {
            internal readonly List<(int frame, long step, double taken, double physics, float x)> seen = new List<(int, long, double, double, float)>();
            internal Rigidbody body;

            private void Update()
            {
                seen.Add((Time.frameCount, CutPhysicsStep.Clock.StepId, CutPhysicsStep.LastDecisionStepSeconds, CutPhysicsStep.Clock.PhysicsSeconds, body.position.x));
            }
        }

        private Reader Setup(int sleepMs)
        {
            var go = new GameObject("Display-stepped body");
            _objects.Add(go);
            go.transform.position = new Vector3(0f, 700f, 0f);
            go.AddComponent<SphereCollider>();
            Rigidbody body = go.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.linearVelocity = new Vector3(Speed, 0f, 0f);
            body.linearDamping = 0f;
            body.sleepThreshold = 0f;
            var host = new GameObject("Display step readers");
            _objects.Add(host);
            host.AddComponent<LongFrames>().sleepMs = sleepMs;
            var reader = host.AddComponent<Reader>();
            reader.body = body;
            return reader;
        }

        [UnityTest]
        public IEnumerator LongFrames_TakeOneDoubleStepEach_OneSimulationMovingTheBodyByTwoSteps_AndThePhysicsTimeIsWhatWasTaken()
        {
            Assert.That(Physics.simulationMode, Is.EqualTo(SimulationMode.Script));
            CutPhysicsStep.InjectExpectedCostForTest(0.0001);   // cheap: both steps fit the budget
            Reader reader = Setup(30);
            double h = 1.0 / 90.0;
            long doublesAtStart = CutPhysicsStep.Clock.DoubleSteps;
            float began = Time.realtimeSinceStartup;
            for (int i = 0; i < 12; i++) yield return null;
            Assert.That(Time.realtimeSinceStartup - began, Is.GreaterThanOrEqualTo(12 * 0.025f), "the layout: frames of 30 ms");

            int doubles = 0;
            double taken = 0.0;
            for (int i = 1; i < reader.seen.Count; i++)
            {
                var p = reader.seen[i - 1];
                var n = reader.seen[i];
                long steps = n.step - p.step;
                Assert.That(steps, Is.InRange(0L, 1L), "at most one simulation a frame (frame " + n.frame + ")");
                if (steps == 0)
                {
                    Assert.That(n.x, Is.EqualTo(p.x), "no step, no movement");
                    continue;
                }

                Assert.That(n.taken, Is.EqualTo(h).Within(1e-12).Or.EqualTo(2.0 * h).Within(1e-12), "a step is 1/90 or 1/45 s");
                Assert.That(n.physics - p.physics, Is.EqualTo(n.taken).Within(1e-9), "the physics time moves by what was taken");
                Assert.That(n.x - p.x, Is.EqualTo((float)(Speed * n.taken)).Within(1e-4f), "and the body by it: one simulation of the taken step");
                taken += n.taken;
                if (n.taken > 1.5 * h) doubles++;
            }

            Assert.That(doubles, Is.GreaterThanOrEqualTo(5), "frames of 30 ms owe two steps: double steps taken");
            Assert.That(CutPhysicsStep.Clock.DoubleSteps - doublesAtStart, Is.GreaterThanOrEqualTo(doubles));
            Assert.That(CutPhysicsStep.Clock.DroppedSeconds, Is.GreaterThan(0.0), "what was over two steps was dropped, not caught up");
            Assert.That(CutPhysicsStep.Clock.UnsimulatedSeconds, Is.LessThanOrEqualTo(2.0 * h + 1e-9), "never more than two steps owed");
            Assert.That(CutPhysicsStep.MainBudgetSeconds, Is.EqualTo(CutPhysicsStep.BudgetFor(CutPhysicsStep.ApplicationFrameSeconds)), "the budget is the period's, the double step adds nothing");
            Assert.That(CutPhysicsStep.MaxDoubleSimulateSeconds, Is.GreaterThan(0.0), "the double steps were measured apart");
            TestContext.Out.WriteLine("double steps " + doubles + ", time taken " + taken.ToString("R") + " s; " + CutPhysicsStep.DescribeStepCounts());
        }

        [UnityTest]
        public IEnumerator AndroidFlat60Hz_LongFramesSimulateOne30HzStep_MovingTheBodyByItsActualDuration()
        {
            CutPhysicsStep.UseClockForTest(new ManualPhysicsClock(60, Stopwatch.Frequency, doubleStep: true), PhysicsStepMode.AndroidFlat);
            CutPhysicsStep.SettleFramePeriod();
            CutPhysicsStep.InjectExpectedCostForTest(0.0001);
            Reader reader = Setup(40);
            const double h = 1.0 / 60.0;
            Assert.That(Physics.simulationMode, Is.EqualTo(SimulationMode.Script));
            Assert.That(CutPhysicsStep.StepMode, Is.EqualTo(PhysicsStepMode.AndroidFlat));
            for (int i = 0; i < 12; i++) yield return null;

            int doubles = 0;
            for (int i = 1; i < reader.seen.Count; i++)
            {
                var previous = reader.seen[i - 1];
                var current = reader.seen[i];
                long simulations = current.step - previous.step;
                Assert.That(simulations, Is.InRange(0L, 1L), "one simulation at most, including a 1/30 s catch-up (frame " + current.frame + ")");
                if (simulations == 0)
                {
                    Assert.That(current.x, Is.EqualTo(previous.x), "without a simulation the body stays still");
                    continue;
                }

                Assert.That(current.taken, Is.EqualTo(h).Within(1e-12).Or.EqualTo(2.0 * h).Within(1e-12));
                Assert.That(current.physics - previous.physics, Is.EqualTo(current.taken).Within(1e-9));
                Assert.That(current.x - previous.x, Is.EqualTo((float)(Speed * current.taken)).Within(1e-4f),
                    "the real Rigidbody integrates the one chosen 1/60 or 1/30 s Physics.Simulate");
                if (current.taken > 1.5 * h) doubles++;
            }

            Assert.That(doubles, Is.GreaterThanOrEqualTo(5), "40 ms frames owe at least two 60 Hz steps");
            Assert.That(CutPhysicsStep.Clock.DoubleSteps, Is.GreaterThanOrEqualTo(doubles));
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(h).Within(1e-12), "catch-up does not enlarge the 60 fps render period");
            Assert.That(CutPhysicsStep.MainBudgetSeconds, Is.EqualTo(CutPhysicsStep.BudgetFor(h)).Within(1e-12));
            Assert.That(CutPhysicsStep.Clock.UnsimulatedSeconds, Is.LessThanOrEqualTo(2.0 * h + 1e-9), "the post-decision carry-over stays bounded");
        }

        [UnityTest]
        public IEnumerator AndroidFlat60Hz_Expensive30HzStepFallsBackToOne60HzSimulation()
        {
            CutPhysicsStep.UseClockForTest(new ManualPhysicsClock(60, Stopwatch.Frequency, doubleStep: true), PhysicsStepMode.AndroidFlat);
            CutPhysicsStep.SettleFramePeriod();
            Reader reader = Setup(40);
            yield return null;
            yield return null;
            // A 30 ms double estimate exceeds the phone's ~15 ms main budget; a 1 ms normal one fits.
            CutPhysicsStep.InjectExpectedCostForTest(0.001);
            CutPhysicsStep.InjectExpectedDoubleCostForTest(0.030);
            long doubles = CutPhysicsStep.Clock.DoubleSteps;
            long fellBack = CutPhysicsStep.DoubleFellBackToNormal;
            int from = reader.seen.Count;
            for (int i = 0; i < 4; i++) yield return null;

            Assert.That(CutPhysicsStep.DoubleFellBackToNormal, Is.GreaterThan(fellBack));
            Assert.That(CutPhysicsStep.Clock.DoubleSteps, Is.EqualTo(doubles), "the over-budget 1/30 s simulation was never taken");
            Assert.That(CutPhysicsStep.LastDecisionCandidateSeconds, Is.EqualTo(1.0 / 30.0).Within(1e-12), "two normal steps were owed");
            int normals = 0;
            for (int i = from + 1; i < reader.seen.Count; i++)
            {
                var previous = reader.seen[i - 1];
                var current = reader.seen[i];
                long simulations = current.step - previous.step;
                Assert.That(simulations, Is.InRange(0L, 1L));
                if (simulations == 0) continue;
                normals++;
                Assert.That(current.taken, Is.EqualTo(1.0 / 60.0).Within(1e-12));
                Assert.That(current.physics - previous.physics, Is.EqualTo(1.0 / 60.0).Within(1e-9));
                Assert.That(current.x - previous.x, Is.EqualTo(Speed / 60f).Within(1e-4f), "the actual Rigidbody moved by the normal step only");
            }

            Assert.That(normals, Is.GreaterThanOrEqualTo(1));
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(1.0 / 60.0).Within(1e-12));
            Assert.That(CutPhysicsStep.MainBudgetSeconds, Is.EqualTo(CutPhysicsStep.BudgetFor(1.0 / 60.0)).Within(1e-12));
        }

        [UnityTest]
        public IEnumerator ADoubleStepWhoseEstimateDoesNotFit_FallsBackToTheNormalStep()
        {
            Reader reader = Setup(30);
            yield return null;
            yield return null;
            // A normal step expected at 6 ms: the double step's first estimate is twice it, 12 ms, over the ~10 ms budget.
            CutPhysicsStep.InjectExpectedCostForTest(0.006);
            long fellBack = CutPhysicsStep.DoubleFellBackToNormal;
            int from = reader.seen.Count;
            yield return null;
            yield return null;
            Assert.That(CutPhysicsStep.LastDecisionCandidateSeconds, Is.EqualTo(2.0 / 90.0).Within(1e-12), "the layout: two steps owed, the double step the candidate");
            Assert.That(CutPhysicsStep.DoubleFellBackToNormal, Is.GreaterThan(fellBack), "two steps owed, the double did not fit: the normal step instead");
            var n = reader.seen[reader.seen.Count - 1];
            Assert.That(reader.seen.Count, Is.GreaterThan(from));
            Assert.That(n.taken, Is.EqualTo(1.0 / 90.0).Within(1e-12), "the step taken was the normal one");
        }

        /// <summary>
        /// **A stale high estimate of the double step is taken again** (TL review, 2026-10-20): the normal step goes on
        /// (its 1 ms fits), the double step's 12 ms never does, so no double step would ever be measured again. A
        /// re-evaluation asked for is not cleared by the normal steps: at the next double candidacy the estimate is taken
        /// again from the normal step's (twice it), compared with the budget as ever -- and the double step resumes.
        /// </summary>
        [UnityTest]
        public IEnumerator AStaleDoubleEstimate_IsTakenAgain_WhenARevaluationIsAsked_ThoughNormalStepsGoOn()
        {
            Setup(30);
            yield return null;
            CutPhysicsStep.InjectExpectedCostForTest(0.001);
            CutPhysicsStep.InjectExpectedDoubleCostForTest(0.012);   // over the ~10 ms budget for good
            long doubles = CutPhysicsStep.Clock.DoubleSteps, fellBack = CutPhysicsStep.DoubleFellBackToNormal;
            for (int i = 0; i < 6; i++) yield return null;
            Assert.That(CutPhysicsStep.Clock.DoubleSteps, Is.EqualTo(doubles), "the stale estimate holds the double step back");
            Assert.That(CutPhysicsStep.DoubleFellBackToNormal - fellBack, Is.GreaterThanOrEqualTo(4), "the normal step taken instead");

            int refreshes = CutPhysicsStep.DoubleEstimateRefreshes, verifications = CutPhysicsStep.VerificationSteps;
            CutPhysicsStep.RequestCostReevaluation();
            float limit = Time.realtimeSinceStartup + 2f;
            while (CutPhysicsStep.Clock.DoubleSteps == doubles && Time.realtimeSinceStartup < limit) yield return null;
            Assert.That(CutPhysicsStep.DoubleEstimateRefreshes, Is.EqualTo(refreshes + 1), "taken again once, on the request");
            Assert.That(CutPhysicsStep.Clock.DoubleSteps, Is.GreaterThan(doubles), "and the double step resumes once its estimate fits");
            Assert.That(CutPhysicsStep.VerificationSteps, Is.EqualTo(verifications), "no step was taken past the budget for it");
        }

        [UnityTest]
        public IEnumerator AStaleDoubleEstimate_IsTakenAgain_After45FallbacksInARow()
        {
            Setup(30);
            yield return null;
            CutPhysicsStep.InjectExpectedCostForTest(0.001);
            CutPhysicsStep.InjectExpectedDoubleCostForTest(0.012);
            long doubles = CutPhysicsStep.Clock.DoubleSteps, fellBack = CutPhysicsStep.DoubleFellBackToNormal;
            int refreshes = CutPhysicsStep.DoubleEstimateRefreshes, verifications = CutPhysicsStep.VerificationSteps;
            float limit = Time.realtimeSinceStartup + 4f;
            while (CutPhysicsStep.Clock.DoubleSteps == doubles && Time.realtimeSinceStartup < limit) yield return null;
            Assert.That(CutPhysicsStep.DoubleFellBackToNormal - fellBack, Is.GreaterThanOrEqualTo(CutPhysicsStep.DoubleRefreshAfterFallbacks), "45 fallbacks first");
            Assert.That(CutPhysicsStep.DoubleEstimateRefreshes, Is.EqualTo(refreshes + 1), "then the estimate taken again");
            Assert.That(CutPhysicsStep.Clock.DoubleSteps, Is.GreaterThan(doubles), "and the double step resumes");
            Assert.That(CutPhysicsStep.VerificationSteps, Is.EqualTo(verifications), "no verification step: nothing past the budget");
        }

        [UnityTest]
        public IEnumerator TheVerificationStep_IsANormalStep_EvenWithTwoStepsOwed()
        {
            Setup(30);
            yield return null;
            CutPhysicsStep.InjectExpectedCostForTest(1.0);   // nothing fits
            CutPhysicsStep.RequestCostReevaluation();
            int verifications = CutPhysicsStep.VerificationSteps;
            float limit = Time.realtimeSinceStartup + 2f;
            while (CutPhysicsStep.VerificationSteps == verifications && Time.realtimeSinceStartup < limit) yield return null;
            Assert.That(CutPhysicsStep.VerificationSteps, Is.EqualTo(verifications + 1), "one verification step, asked for");
            Assert.That(CutPhysicsStep.LastDecisionCandidateSeconds, Is.EqualTo(2.0 / 90.0).Within(1e-12), "the layout: two steps were owed");
            Assert.That(CutPhysicsStep.LastDecisionStepSeconds, Is.EqualTo(1.0 / 90.0).Within(1e-12), "a normal step, never the double one");
            Assert.That(CutPhysicsStep.ExpectedDoubleSimulateSeconds, Is.EqualTo(2.0 * CutPhysicsStep.ExpectedSimulateSeconds).Within(1e-12),
                "the double step's estimate starts over from twice the new measurement");
        }

        [UnityTest]
        public IEnumerator WhilePaused_NothingIsOwed_AndTheResumeTakesNoDoubleStepForThePause()
        {
            CutPhysicsStep.InjectExpectedCostForTest(0.0001);
            Reader reader = Setup(30);
            yield return null;
            CutPhysicsStep.SetPaused(true);
            yield return null;
            long pausedAt = CutPhysicsStep.Clock.StepId;
            double owed = CutPhysicsStep.Clock.UnsimulatedSeconds;
            for (int i = 0; i < 8; i++) yield return null;
            Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(pausedAt), "nothing simulated while paused");
            Assert.That(CutPhysicsStep.Clock.UnsimulatedSeconds, Is.EqualTo(owed), "the pause is not owed");

            reader.GetComponent<LongFrames>().sleepMs = 0;   // short frames after the resume: only what was owed before, and new time
            CutPhysicsStep.SetPaused(false);
            yield return null;
            Assert.That(CutPhysicsStep.Clock.StepId - pausedAt, Is.LessThanOrEqualTo(1L), "the first frame after the resume: one step at most");
            Assert.That(CutPhysicsStep.LastDecisionStepSeconds, Is.LessThanOrEqualTo(owed + 1e-9).Or.EqualTo(0.0),
                "no more than what was owed before the pause: the resume only realigns");
        }
    }
}
