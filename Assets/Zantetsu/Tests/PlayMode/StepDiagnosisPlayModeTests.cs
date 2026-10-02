using System;
using System.Collections;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The step decision's diagnosis is off by default (2026-10-02): a session without ZTK_STEP_DIAG=1 records nothing --
    /// frames go by and no record is kept -- and the test callback writes nothing even when an output directory is named.
    /// </summary>
    public class StepDiagnosisPlayModeTests
    {
        [UnityTest]
        public IEnumerator StepDiagnosis_Off_RecordsNothing_AndWritesNothing()
        {
            if (Environment.GetEnvironmentVariable(CutPhysicsStep.DiagnosisVariable) == "1")
            {
                Assert.Ignore("this session records the step decision (" + CutPhysicsStep.DiagnosisVariable + "=1): the off case is not this run's");
            }

            Assert.That(CutPhysicsStep.DiagnosisOn, Is.False, "off by default");
            Assert.That(CutPhysicsStep.DiagnosisCount, Is.Zero, "nothing recorded");
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            Assert.That(CutPhysicsStep.DiagnosisCount, Is.Zero, "frames went by, still nothing recorded");

            string dir = Path.Combine(Application.temporaryCachePath, "step-diagnosis-off-" + Guid.NewGuid().ToString("N"));
            string before = Environment.GetEnvironmentVariable("ZTK_STEP_DIAG_OUT");
            Environment.SetEnvironmentVariable("ZTK_STEP_DIAG_OUT", dir);
            try
            {
                StepDiagnosisCallback.Mark("begin", "off case", null);
                StepDiagnosisCallback.Mark("end", "off case", "Passed");
            }
            finally
            {
                Environment.SetEnvironmentVariable("ZTK_STEP_DIAG_OUT", before);
            }

            Assert.That(Directory.Exists(dir), Is.False, "no directory or file made while off");
            Assert.That(CutPhysicsStep.DiagnosisCount, Is.Zero);
        }
    }
}
