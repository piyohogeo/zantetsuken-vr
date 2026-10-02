using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The step decision's diagnosis is off by default (2026-10-02): a session without ZTK_STEP_DIAG=1 tries no record --
    /// frames go by and nothing is handed to the development logger -- and a mark or an end asked for while off is not one
    /// either. While on, the record's end stops it: after the summary, frames and marks add no record.
    /// </summary>
    public class StepDiagnosisPlayModeTests
    {
        /// <summary>Asks for the on case's own launch: it ends the session's record, so no other test's marks would follow it.</summary>
        internal const string EndTestVariable = "ZTK_STEP_DIAG_END_TEST";

        [UnityTest]
        public IEnumerator StepDiagnosis_Off_RecordsNothing_AndWritesNothing()
        {
            if (Environment.GetEnvironmentVariable(CutPhysicsStep.DiagnosisVariable) == "1")
            {
                Assert.Ignore("this session records the step decision (" + CutPhysicsStep.DiagnosisVariable + "=1): the off case is not this run's");
            }

            Assert.That(CutPhysicsStep.DiagnosisOn, Is.False, "off by default");
            Assert.That(CutPhysicsStep.DiagnosisAttempted, Is.Zero, "nothing tried");
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            Assert.That(CutPhysicsStep.DiagnosisAttempted, Is.Zero, "frames went by, still nothing tried");
            StepDiagnosisCallback.Mark("begin", "off case", null);
            StepDiagnosisCallback.Mark("end", "off case", "Passed");
            CutPhysicsStep.DiagnosisEnd();
            Assert.That(CutPhysicsStep.DiagnosisAttempted, Is.Zero, "a mark or an end while off is no record");
            Assert.That(CutPhysicsStep.DiagnosisAccepted, Is.Zero, "nothing handed to the logger");
            Assert.That(CutPhysicsStep.DiagnosisEnded, Is.False, "nothing to end while off");
        }

        [UnityTest]
        public IEnumerator StepDiagnosis_On_AfterTheEnd_NothingMoreIsRecorded()
        {
            if (Environment.GetEnvironmentVariable(CutPhysicsStep.DiagnosisVariable) != "1" || Environment.GetEnvironmentVariable(EndTestVariable) != "1")
            {
                Assert.Ignore("needs its own launch with " + CutPhysicsStep.DiagnosisVariable + "=1 and " + EndTestVariable + "=1 (it ends the session's record)");
            }

            Assert.That(CutPhysicsStep.DiagnosisOn, Is.True);
            Assert.That(CutPhysicsStep.DiagnosisEnded, Is.False, "not ended before this case");
            long before = CutPhysicsStep.DiagnosisAttempted;
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            Assert.That(CutPhysicsStep.DiagnosisAttempted, Is.GreaterThan(before), "decisions are recorded while on");
            CutPhysicsStep.DiagnosisEnd();
            Assert.That(CutPhysicsStep.DiagnosisEnded, Is.True);
            long attempted = CutPhysicsStep.DiagnosisAttempted, accepted = CutPhysicsStep.DiagnosisAccepted;
            Assert.That(accepted, Is.EqualTo(attempted), "every record up to the summary accepted");
            int lastSimulated = CutPhysicsStep.LastSimulatedFrame;
            // A batchmode frame is a fraction of a millisecond: wait on the clock, not a frame count, for a step to be taken.
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (CutPhysicsStep.LastSimulatedFrame == lastSimulated && wait.Elapsed.TotalSeconds < 5.0)
            {
                yield return null;
            }

            StepDiagnosisCallback.Mark("begin", "after the end", null);
            CutPhysicsStep.DiagnosisEnd();
            Assert.That(CutPhysicsStep.DiagnosisAttempted, Is.EqualTo(attempted), "no decision, mark or second summary tried after the end");
            Assert.That(CutPhysicsStep.DiagnosisAccepted, Is.EqualTo(accepted));
            Assert.That(CutPhysicsStep.LastSimulatedFrame, Is.GreaterThan(lastSimulated), "the physics went on: only the record stopped");
        }
    }
}
