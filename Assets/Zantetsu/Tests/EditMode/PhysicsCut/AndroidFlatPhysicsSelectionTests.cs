using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>The real session selection and frame-budget entry points, with no XR runtime or device required.</summary>
    public sealed class AndroidFlatPhysicsSelectionTests
    {
        private readonly Dictionary<FieldInfo, object> _before = new Dictionary<FieldInfo, object>();

        [SetUp]
        public void SaveSessionSettings()
        {
            // The selector replaces scalar/session fields, never the readonly collectors or the cost histories.
            // Restoring those fields keeps these EditMode integration checks independent of their execution order.
            foreach (FieldInfo field in typeof(CutPhysicsStep).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                if (!field.IsInitOnly && !field.IsLiteral) _before.Add(field, field.GetValue(null));
        }

        [TearDown]
        public void RestoreSessionSettings()
        {
            foreach (KeyValuePair<FieldInfo, object> entry in _before) entry.Key.SetValue(null, entry.Value);
            _before.Clear();
        }

        [TestCase(false, 0, 0)]
        [TestCase(true, 0, 0)]
        [TestCase(false, 45, 1)]
        [TestCase(true, 90, 2)]
        public void AndroidFlat_IsSettledAt60HzWithOne30HzCatchUp_WithoutWaitingForXr(bool pc, int explicitHz, int request)
        {
            CutPhysicsStep.ChooseStepAtSessionStart(pc, explicitHz, (CutPhysicsStep.PhysicsStepRequest)request, androidFlat: true);
            Assert.That(CutPhysicsStep.StepMode, Is.EqualTo(PhysicsStepMode.AndroidFlat));
            Assert.That(CutPhysicsStep.StepSettled, Is.True, "a phone never waits for an XR display to start");
            Assert.That(CutPhysicsStep.PcvrTargetHz, Is.Zero);
            Assert.That(CutPhysicsStep.Clock.FrequencyHz, Is.EqualTo(60));
            Assert.That(CutPhysicsStep.Clock.StepSeconds, Is.EqualTo(1.0 / 60.0));
            Assert.That(CutPhysicsStep.Clock.DoubleStep, Is.True);
            Assert.That(CutPhysicsStep.Clock.StepId, Is.Zero);
            Assert.That(CutPhysicsStep.DescribeStepSettings(), Does.Contain("Android non-VR target 60 Hz"));
        }

        [Test]
        public void AndroidFlat_UsesThe60HzRenderBudget_EvenWhenAHostOffers30HzOrPhysicsOwesADoubleStep()
        {
            CutPhysicsStep.ChooseStepAtSessionStart(false, 0, CutPhysicsStep.PhysicsStepRequest.Auto, androidFlat: true);
            int hostReads = 0;
            CutPhysicsStep.SetApplicationFrameSource(() => { hostReads++; return 1.0 / 30.0; });
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(hostReads, Is.Zero, "a stale Quest/AppSW host is not a phone frame-rate source");
            Assert.That(CutPhysicsStep.ApplicationFrameSource, Is.EqualTo(ApplicationFrameSourceKind.AndroidFlat));
            Assert.That(CutPhysicsStep.ApplicationFrameSeconds, Is.EqualTo(1.0 / 60.0));
            Assert.That(CutPhysicsStep.CommonMarginSeconds, Is.EqualTo(0.0015).Within(1e-12));
            double budget = CutPhysicsStep.MainBudgetSeconds;
            Assert.That(budget, Is.EqualTo((1.0 / 60.0) - 0.0015).Within(1e-12));

            // The real session clock runs on Stopwatch ticks; a second owed certainly makes one 1/30 s candidate.
            CutPhysicsStep.Clock.Accumulate(0);
            CutPhysicsStep.Clock.Accumulate(System.Diagnostics.Stopwatch.Frequency);
            Assert.That(CutPhysicsStep.Clock.Choose(0.0, 0.0, budget), Is.EqualTo(1.0 / 30.0));
            CutPhysicsStep.SettleFramePeriod();
            Assert.That(CutPhysicsStep.MainBudgetSeconds, Is.EqualTo(budget), "the double physics step adds no render budget");
        }

        [TestCase(false, 0, 0, 45, true)]
        [TestCase(false, 90, 0, 90, true)]
        [TestCase(true, 0, 0, 45, false)]
        [TestCase(true, 90, 0, 90, true)]
        [TestCase(true, 0, 1, 45, true)]
        public void ExistingQuestAndPcSessionSelection_RemainsTheSame(bool pc, int explicitHz, int request, int hz, bool settled)
        {
            CutPhysicsStep.ChooseStepAtSessionStart(pc, explicitHz, (CutPhysicsStep.PhysicsStepRequest)request, androidFlat: false);
            Assert.That(CutPhysicsStep.StepMode, Is.EqualTo(PhysicsStepMode.Fixed));
            Assert.That(CutPhysicsStep.Clock.FrequencyHz, Is.EqualTo(hz));
            Assert.That(CutPhysicsStep.Clock.DoubleStep, Is.False);
            Assert.That(CutPhysicsStep.StepSettled, Is.EqualTo(settled));
        }
    }
}
