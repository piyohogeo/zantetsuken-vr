using System;
using System.IO;
using System.Text;
using NUnit.Framework.Interfaces;
using UnityEngine.TestRunner;

// DIAGNOSIS ONLY (2026-10-02, the integration worktree): every PlayMode test of this assembly marked in the step decision's
// record (CutPhysicsStep.Diagnosis), with the decisions between the marks, so that the state a test leaves and the next one
// finds can be read. Nothing is done unless the session records (ZTK_STEP_DIAG=1) and ZTK_STEP_DIAG_OUT names a directory;
// the record is written to a new file there, never over an existing one. (An assembly-level NUnit ITestAction is not called
// by the PlayMode runner; the runner's own callback is.)
[assembly: TestRunCallback(typeof(Zantetsu.PhysicsCut.PlayModeTests.StepDiagnosisCallback))]

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    public sealed class StepDiagnosisCallback : ITestRunCallback
    {
        private static string s_file;
        private static int s_written;

        public void RunStarted(ITest testsToRun) => Mark("run started", testsToRun.FullName, null);

        public void RunFinished(ITestResult testResults) => Mark("run finished", testResults.Test.FullName, testResults.ResultState.ToString());

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
            if (!CutPhysicsStep.DiagnosisOn)
            {
                return;
            }

            string dir = Environment.GetEnvironmentVariable("ZTK_STEP_DIAG_OUT");
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var b = new StringBuilder();
            if (s_file == null)
            {
                Directory.CreateDirectory(dir);
                s_file = Path.Combine(dir, "step-decisions.csv");
                using (new FileStream(s_file, FileMode.CreateNew, FileAccess.Write))   // refuses an existing file
                {
                }

                b.Append(CutPhysicsStep.DiagnosisCsvHeader).Append('\n');
            }

            CutPhysicsStep.AppendDiagnosisCsv(b, s_written);
            s_written = CutPhysicsStep.DiagnosisCount;
            b.Append("# ").Append(what).Append(' ').Append(name).Append(outcome != null ? " -> " + outcome : "").Append(" | ").Append(CutPhysicsStep.DiagnosisState()).Append('\n');
            File.AppendAllText(s_file, b.ToString());
        }
    }
}
