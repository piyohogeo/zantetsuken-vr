using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// PCVR's target rate and the step's mode (DESIGN 4.4, 2026-10-20): 72 or 90 Hz from the XR display within the
    /// tolerance, any other rate out of scope and never rounded to the nearer, no rate within the wait "not obtained",
    /// both taking the provisional 90 Hz; the explicit frequency first, then the mode asked for, then the automatic choice;
    /// Quest never on the display step.
    /// </summary>
    public sealed class PcvrDisplayRateTests
    {
        [Test]
        public void OnlyRatesWithinTheToleranceOf72Or90_AreTaken_AndNoneIsRoundedToTheNearer()
        {
            foreach (float r in new[] { 72f, 71.6f, 72.4f, 72.00012f })
                Assert.That(CutPhysicsStep.SupportedPcvrRate(r), Is.EqualTo(72), r.ToString("R"));
            foreach (float r in new[] { 90f, 89.99f, 89.5f, 90.5f, 90.00037f })
                Assert.That(CutPhysicsStep.SupportedPcvrRate(r), Is.EqualTo(90), r.ToString("R"));
            foreach (float r in new[] { 71.4f, 80f, 85f, 120f, 144f, 60f, 45f, 36f, 0f, -90f, float.NaN, float.PositiveInfinity })
                Assert.That(CutPhysicsStep.SupportedPcvrRate(r), Is.Zero, r.ToString("R") + " is out of scope, not the nearer of 72 and 90");
        }

        [Test]
        public void TheReadingAtTheStart_IsObtained_OutOfScope_NotObtained_OrPending()
        {
            // ClassifyPcvr(xr known, display running, display present, rate known, rate, waited)
            Assert.That(CutPhysicsStep.ClassifyPcvr(false, true, true, true, 90f, 0.0, out int hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Obtained));
            Assert.That(hz, Is.EqualTo(90));
            Assert.That(CutPhysicsStep.ClassifyPcvr(true, true, true, true, 72.0001f, 0.5, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Obtained));
            Assert.That(hz, Is.EqualTo(72));

            Assert.That(CutPhysicsStep.ClassifyPcvr(true, true, true, true, 120f, 0.0, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.OutOfScope));
            Assert.That(hz, Is.EqualTo(CutPhysicsStep.PcvrProvisionalHz), "out of scope: the provisional 90 Hz, said so");
            Assert.That(CutPhysicsStep.ClassifyPcvr(true, true, true, true, 45f, 0.0, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.OutOfScope),
                "a reprojected 45 at the start is not a 45 Hz step");
            Assert.That(hz, Is.EqualTo(90));

            Assert.That(CutPhysicsStep.ClassifyPcvr(false, true, true, false, 0f, 0.0, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Pending), "running, no rate yet: waiting");
            Assert.That(CutPhysicsStep.ClassifyPcvr(true, true, true, true, 0f, 2.9, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Pending), "a 0 answer is no answer");
            Assert.That(CutPhysicsStep.ClassifyPcvr(true, true, true, false, 0f, CutPhysicsStep.PcvrSettleSeconds, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.NotObtained));
            Assert.That(hz, Is.EqualTo(CutPhysicsStep.PcvrProvisionalHz), "not obtained: the provisional 90 Hz, said so");
        }

        /// <summary>
        /// **XR not ready is not a desktop** (TL review, 2026-10-20). An XR display created and not started is waited for;
        /// no XR display at all, not known to be PCVR, is a desktop session at once (no hold); one that never starts within
        /// the wait is taken as desktop and said so; PCVR known (seen running, or asked for) never becomes a desktop.
        /// </summary>
        [Test]
        public void AnXrDisplayNotStartedYet_IsWaitedFor_NoXrDisplayAtAll_IsADesktopAtOnce()
        {
            Assert.That(CutPhysicsStep.ClassifyPcvr(false, false, false, false, 0f, 0.0, out int hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.NoXr), "no XR display at all: desktop at once");
            Assert.That(hz, Is.Zero);
            Assert.That(CutPhysicsStep.ClassifyPcvr(false, false, true, false, 0f, 0.0, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Pending), "created, not started: waited for");
            Assert.That(CutPhysicsStep.ClassifyPcvr(false, false, true, false, 0f, 2.9, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Pending));
            Assert.That(CutPhysicsStep.ClassifyPcvr(false, false, true, false, 0f, CutPhysicsStep.PcvrSettleSeconds, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.XrNotStarted),
                "never started within the wait: taken as desktop, said so");
            Assert.That(hz, Is.Zero);
            Assert.That(CutPhysicsStep.ClassifyPcvr(false, true, true, true, 90f, 1.0, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Obtained), "started late within the wait: PCVR");
            Assert.That(CutPhysicsStep.ClassifyPcvr(true, false, false, false, 0f, 1.0, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.Pending),
                "PCVR known (seen running, or asked for) and the display gone for a moment: still waiting, never desktop");
            Assert.That(CutPhysicsStep.ClassifyPcvr(true, false, false, false, 0f, CutPhysicsStep.PcvrSettleSeconds, out hz), Is.EqualTo(CutPhysicsStep.PcvrRateState.NotObtained));
            Assert.That(CutPhysicsStep.ModeFor(true, 0, CutPhysicsStep.PhysicsStepRequest.Auto, CutPhysicsStep.PcvrRateState.XrNotStarted), Is.EqualTo(PhysicsStepMode.Fixed));
        }

        /// <summary>
        /// **The double step's estimate is taken again** only on a double candidacy, when a re-evaluation was asked for or
        /// after 45 fallbacks in a row -- and is then still compared with the budget (the decision's own comparison).
        /// </summary>
        [Test]
        public void TheDoubleStepsEstimate_IsTakenAgain_OnARequestOrAfterTheFallbacks_OnlyAtADoubleCandidacy()
        {
            Assert.That(CutPhysicsStep.DoubleRefreshAfterFallbacks, Is.EqualTo(45));
            Assert.That(CutPhysicsStep.ShouldRefreshDoubleEstimate(true, true, 0), Is.True, "asked for");
            Assert.That(CutPhysicsStep.ShouldRefreshDoubleEstimate(true, false, 45), Is.True, "45 fallbacks in a row");
            Assert.That(CutPhysicsStep.ShouldRefreshDoubleEstimate(true, false, 44), Is.False);
            Assert.That(CutPhysicsStep.ShouldRefreshDoubleEstimate(false, true, 100), Is.False, "not a double candidacy: kept for one");
        }

        [Test]
        public void TheExplicitFrequencyComesFirst_ThenTheModeAskedFor_ThenTheAutomaticChoice_AndQuestStaysFixed()
        {
            var auto = CutPhysicsStep.PhysicsStepRequest.Auto;
            var asFixed = CutPhysicsStep.PhysicsStepRequest.Fixed;
            var display = CutPhysicsStep.PhysicsStepRequest.Display;
            var obtained = CutPhysicsStep.PcvrRateState.Obtained;
            Assert.That(CutPhysicsStep.ModeFor(true, 0, auto, obtained), Is.EqualTo(PhysicsStepMode.Display), "PCVR, nothing given: the display step");
            Assert.That(CutPhysicsStep.ModeFor(true, 0, auto, CutPhysicsStep.PcvrRateState.NotObtained), Is.EqualTo(PhysicsStepMode.Display), "on the provisional 90 Hz");
            Assert.That(CutPhysicsStep.ModeFor(true, 0, auto, CutPhysicsStep.PcvrRateState.OutOfScope), Is.EqualTo(PhysicsStepMode.Display));
            Assert.That(CutPhysicsStep.ModeFor(true, 0, auto, CutPhysicsStep.PcvrRateState.NoXr), Is.EqualTo(PhysicsStepMode.Fixed), "a desktop session: the fixed 45 Hz step");
            Assert.That(CutPhysicsStep.ModeFor(true, 45, auto, obtained), Is.EqualTo(PhysicsStepMode.Fixed), "the explicit frequency first");
            Assert.That(CutPhysicsStep.ModeFor(true, 90, display, obtained), Is.EqualTo(PhysicsStepMode.Fixed), "even over the display mode asked for");
            Assert.That(CutPhysicsStep.ModeFor(true, 0, asFixed, obtained), Is.EqualTo(PhysicsStepMode.Fixed), "the fixed mode asked for (A, B)");
            Assert.That(CutPhysicsStep.ModeFor(true, 0, display, CutPhysicsStep.PcvrRateState.NoXr), Is.EqualTo(PhysicsStepMode.Display), "the display mode asked for");
            Assert.That(CutPhysicsStep.ModeFor(false, 0, display, CutPhysicsStep.PcvrRateState.None), Is.EqualTo(PhysicsStepMode.Fixed), "Quest: never the display step");
            Assert.That(CutPhysicsStep.ModeFor(false, 0, auto, CutPhysicsStep.PcvrRateState.None), Is.EqualTo(PhysicsStepMode.Fixed));
        }

        [Test]
        public void TheStartUpArguments_AreRead_AndAnythingElseIsSaid()
        {
            Assert.That(CutPhysicsStep.FrequencyFromCommandLine(new[] { "app" }), Is.Zero, "none given");
            Assert.That(CutPhysicsStep.FrequencyFromCommandLine(new[] { "app", "-zantetsuPhysicsHz", "90" }), Is.EqualTo(90));
            Assert.That(CutPhysicsStep.FrequencyFromCommandLine(new[] { "app", "-zantetsuPhysicsHz", "45" }), Is.EqualTo(45));
            LogAssert.Expect(LogType.Warning, new Regex("takes 45 or 90; '72'"));
            Assert.That(CutPhysicsStep.FrequencyFromCommandLine(new[] { "app", "-zantetsuPhysicsHz", "72" }), Is.Zero, "72 is the display step's, not an explicit frequency");

            Assert.That(CutPhysicsStep.MarginFromCommandLine(new[] { "app" }), Is.EqualTo(MainMarginKind.PeriodFraction));
            Assert.That(CutPhysicsStep.MarginFromCommandLine(new[] { "app", "-zantetsuMainMargin", "fixed2ms" }), Is.EqualTo(MainMarginKind.Fixed2Ms));
            Assert.That(CutPhysicsStep.MarginFromCommandLine(new[] { "app", "-zantetsuMainMargin", "9percent" }), Is.EqualTo(MainMarginKind.PeriodFraction));
            LogAssert.Expect(LogType.Warning, new Regex("takes 9percent or fixed2ms"));
            Assert.That(CutPhysicsStep.MarginFromCommandLine(new[] { "app", "-zantetsuMainMargin", "3ms" }), Is.EqualTo(MainMarginKind.PeriodFraction));

            Assert.That(CutPhysicsStep.StepRequestFromCommandLine(new[] { "app" }), Is.EqualTo(CutPhysicsStep.PhysicsStepRequest.Auto));
            Assert.That(CutPhysicsStep.StepRequestFromCommandLine(new[] { "app", "-zantetsuPhysicsStep", "fixed" }), Is.EqualTo(CutPhysicsStep.PhysicsStepRequest.Fixed));
            Assert.That(CutPhysicsStep.StepRequestFromCommandLine(new[] { "app", "-zantetsuPhysicsStep", "display" }), Is.EqualTo(CutPhysicsStep.PhysicsStepRequest.Display));
            LogAssert.Expect(LogType.Warning, new Regex("takes fixed or display"));
            Assert.That(CutPhysicsStep.StepRequestFromCommandLine(new[] { "app", "-zantetsuPhysicsStep", "double" }), Is.EqualTo(CutPhysicsStep.PhysicsStepRequest.Auto));
        }
    }
}
