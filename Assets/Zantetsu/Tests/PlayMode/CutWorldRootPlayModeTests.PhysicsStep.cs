using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The shared physics clock and step (DESIGN 4.4, 2026-09-30): a stale high cost estimate is measured again by a
    /// verification step and the steps resume, with the time owed capped at two steps. A plain world: no building rest
    /// or fusion is needed for this, and nothing else asks for a re-evaluation.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>Waits until the shared physics clock has advanced by the given seconds (the test's own deadline applies).</summary>
        private static IEnumerator PhysicsSeconds(double seconds)
        {
            double until = CutPhysicsStep.Clock.PhysicsSeconds + seconds;
            yield return Until(() => CutPhysicsStep.Clock.PhysicsSeconds >= until, "physics time advanced " + seconds + " s");
        }

        /// <summary>Waits for the condition within the given real seconds, and fails when it did not come.</summary>
        private static IEnumerator UntilWithin(Func<bool> condition, float seconds, string what)
        {
            float deadline = Time.realtimeSinceStartup + seconds;
            yield return Until(() => condition() || Time.realtimeSinceStartup > deadline, what);
            Assert.That(condition(), Is.True, what + ": within " + seconds + " s");
        }

        /// <summary>
        /// **A stale high cost estimate does not hold the physics for ever: a verification step measures again and the
        /// steps resume, with the owed time capped.** The estimate is set as if five simulations had cost a second; the
        /// decisions that are owed a step and have budget skip for the cost alone and are counted; after the bound of
        /// such decisions one verification step runs past the budget, the estimate starts over from its real cost, and
        /// the steps resume at the step's pace -- the time owed never exceeded the cap while the steps were skipped.
        /// </summary>
        [UnityTest]
        public IEnumerator PhysicsStep_AStaleHighEstimate_IsMeasuredAgainByAVerificationStep_AndTheStepsResume()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            yield return PhysicsSeconds(0.2);
            ManualPhysicsClock clock = CutPhysicsStep.Clock;
            double cap = ManualPhysicsClock.MaxOwedSteps * clock.StepSeconds;
            int verificationsBefore = CutPhysicsStep.VerificationSteps;
            CutPhysicsStep.InjectExpectedCostForTest(1.0);
            long stepAtInjection = clock.StepId;
            int frames = 0, budgetShort = 0, maxSkips = 0;
            double maxOwed = 0.0;
            float deadline = Time.realtimeSinceStartup + 10f;
            while (CutPhysicsStep.VerificationSteps == verificationsBefore && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
                frames++;
                if (CutPhysicsStep.VerificationSteps > verificationsBefore) break;   // the verification step itself
                if (CutPhysicsStep.LastDecisionRemainingSeconds < 0.0) budgetShort++;
                maxSkips = Mathf.Max(maxSkips, CutPhysicsStep.SkippedForCostInARow);
                maxOwed = Math.Max(maxOwed, clock.UnsimulatedSeconds);
                Assert.That(clock.StepId, Is.EqualTo(stepAtInjection), "no step while the estimate holds and the bound is not reached");
            }

            TestContext.Out.WriteLine("verification after " + frames + " frames (" + budgetShort + " with the budget already spent): cost " + (CutPhysicsStep.LastVerificationSeconds * 1000).ToString("F3") + " ms, overrun "
                + (CutPhysicsStep.VerificationOverrunSeconds * 1000).ToString("F3") + " ms, skipped decisions counted up to " + maxSkips + ", most owed " + (maxOwed * 1000).ToString("F1") + " ms (cap " + (cap * 1000).ToString("F1")
                + "), dropped " + (clock.DroppedSeconds * 1000).ToString("F1") + " ms over the run, expected now " + (CutPhysicsStep.ExpectedSimulateSeconds * 1000).ToString("F3") + " ms");
            Assert.That(CutPhysicsStep.VerificationSteps, Is.EqualTo(verificationsBefore + 1), "one verification step");
            Assert.That(clock.StepId, Is.EqualTo(stepAtInjection + 1), "and it is the one step taken");
            Assert.That(maxSkips, Is.GreaterThanOrEqualTo(CutPhysicsStep.VerifyAfterSkippedFrames - 1), "after the bound of decisions skipped for the cost alone");
            Assert.That(CutPhysicsStep.ExpectedSimulateSeconds, Is.LessThan(0.01), "the estimate starts over from the real cost");
            Assert.That(maxOwed, Is.LessThanOrEqualTo(cap + 1e-9), "what was owed never exceeded the cap (no catch-up is stored)");
            long resumedFrom = clock.StepId;
            yield return UntilWithin(() => clock.StepId >= resumedFrom + 10, 3f, "the steps resumed");
            Assert.That(CutPhysicsStep.SkippedForCostInARow, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A re-evaluation asked for is answered by the next decision, once, past the budget.** With the same stale
        /// estimate, <see cref="CutPhysicsStep.RequestCostReevaluation"/> has the very next decision that would skip
        /// for the cost simulate instead (one verification step), and the estimate starts over from that measurement.
        /// </summary>
        [UnityTest]
        public IEnumerator PhysicsStep_AReevaluationAskedFor_IsAnsweredByTheNextDecision_Once()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            yield return PhysicsSeconds(0.2);
            ManualPhysicsClock clock = CutPhysicsStep.Clock;
            int verificationsBefore = CutPhysicsStep.VerificationSteps;
            CutPhysicsStep.InjectExpectedCostForTest(1.0);
            long stepAtInjection = clock.StepId;
            yield return null;
            yield return null;
            Assert.That(clock.StepId, Is.EqualTo(stepAtInjection), "the stale estimate holds the steps");
            CutPhysicsStep.RequestCostReevaluation();
            yield return UntilWithin(() => CutPhysicsStep.VerificationSteps > verificationsBefore, 2f, "the verification step");
            Assert.That(CutPhysicsStep.VerificationSteps, Is.EqualTo(verificationsBefore + 1), "one verification step, not a catch-up");
            Assert.That(CutPhysicsStep.ExpectedSimulateSeconds, Is.LessThan(0.01), "the estimate starts over from the real cost");
            long resumedFrom = clock.StepId;
            yield return UntilWithin(() => clock.StepId >= resumedFrom + 5, 3f, "the steps resumed");
            yield return EndWorld(root);
        }
    }
}
