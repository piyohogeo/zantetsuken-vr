using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The physics step's budget in a Release Player (DESIGN 21.18, D-226): the simulation's measured duration feeds the
    /// cost history the next decision reads, so it must be measured in every build. The Editor always defines the
    /// observation symbol, so a test of the Release side cannot run here; this one reads the source and holds the step
    /// to having no observation gate at all, and the measurement wired to the history. (The behaviour itself is held by
    /// CutPhysicsStepPlayModeTests: LastSimulateSeconds and ExpectedSimulateSeconds above zero after real steps.)
    /// </summary>
    public sealed class CutPhysicsStepBudgetSourceTests
    {
        [Test]
        public void TheSimulateMeasurement_FeedsTheCostHistory_InEveryBuild_NoObservationGateInTheStep()
        {
            string path = Path.Combine(Application.dataPath, "Zantetsu", "Runtime", "PhysicsCut", "CutPhysicsStep.cs");
            Assert.That(File.Exists(path), Is.True, path);
            string source = File.ReadAllText(path);
            Assert.That(source, Does.Not.Contain("ZANTETSU_OBSERVE"), "the step has no observation gate: its clock is the budget's");
            Assert.That(source, Does.Contain("LastSimulateSeconds = (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;"), "the simulation is measured");
            Assert.That(source, Does.Contain("s_costs.Add(LastSimulateSeconds);"), "and the measurement goes to the cost history the next decision reads");
        }
    }
}
