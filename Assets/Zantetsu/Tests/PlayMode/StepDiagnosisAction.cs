using NUnit.Framework.Interfaces;
using UnityEngine.TestRunner;

// DIAGNOSIS ONLY (2026-10-02): every PlayMode test of this assembly marked in the step decision's record
// (CutPhysicsStep.Diagnosis, records of writer "CutPhysicsStep" in the development logger), so that the state a test
// leaves and the next one finds can be read between the marks. At the run's end the record is ended (DiagnosisEnd): the
// session's counts are its last record and the same line in the Editor's log, which does not depend on the logger -- what
// the saved file must be checked against; nothing is recorded after it.
// Nothing is done unless the session records (ZTK_STEP_DIAG=1). (An assembly-level NUnit ITestAction is not called by the
// PlayMode runner; the runner's own callback is.)
[assembly: TestRunCallback(typeof(Zantetsu.PhysicsCut.PlayModeTests.StepDiagnosisCallback))]

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public sealed class StepDiagnosisCallback : ITestRunCallback
    {
        public void RunStarted(ITest testsToRun) => Mark("run started", testsToRun.FullName, null);

        public void RunFinished(ITestResult testResults)
        {
            Mark("run finished", testResults.Test.FullName, testResults.ResultState.ToString());
            CutPhysicsStep.DiagnosisEnd();
        }

        public void TestStarted(ITest test)
        {
            if (!test.IsSuite) Mark("begin", test.FullName, null);
        }

        public void TestFinished(ITestResult result)
        {
            if (!result.Test.IsSuite) Mark("end", result.Test.FullName, result.ResultState.ToString());
        }

        internal static void Mark(string what, string name, string outcome)
        {
            CutPhysicsStep.DiagnosisMark(what, name, outcome);
        }
    }
}
