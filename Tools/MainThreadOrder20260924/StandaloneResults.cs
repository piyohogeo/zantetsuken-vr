// Diagnostic template: a built TestRunner Player can be rerun without its Editor connection.
using System;
using System.IO;
using System.Text;
using NUnit.Framework.Interfaces;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.TestRunner;

[assembly: TestRunCallback(typeof(Zantetsu.PhysicsCut.PlayModeTests.StandaloneResults))]

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    [Preserve]
    public sealed class StandaloneResults : ITestRunCallback
    {
        public void RunStarted(ITest testsToRun) { }
        public void TestStarted(ITest test) { }
        public void TestFinished(ITestResult result) { }

        public void RunFinished(ITestResult result)
        {
            if (Application.isEditor || Environment.GetEnvironmentVariable("ORDER_STANDALONE") != "1") return;
            string folder = Environment.GetEnvironmentVariable("ORDER_OUTPUT");
            if (string.IsNullOrEmpty(folder)) throw new InvalidOperationException("ORDER_OUTPUT is required");
            File.WriteAllText(Path.Combine(folder, "results.xml"), result.ToXml(true).OuterXml, new UTF8Encoding(false));
            Application.Quit(result.ResultState.Status == TestStatus.Passed ? 0 : 2);
        }
    }
}
