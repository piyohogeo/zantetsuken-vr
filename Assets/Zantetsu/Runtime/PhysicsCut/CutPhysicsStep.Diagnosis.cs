using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Zantetsu.PhysicsCut
{
    // DIAGNOSIS ONLY (2026-10-02, the integration worktree): the step decision of every frame, recorded with what it read, so
    // that a decision that does not step can be told apart -- paused, nothing owed, no budget left, the estimate over the
    // budget, not in script mode. Off unless the environment variable ZTK_STEP_DIAG is "1" when a play session starts; then
    // a fixed ring of records is filled frame by frame (no allocation per frame) and events can be added by name. Nothing of
    // the decision itself is changed by it.
    public static partial class CutPhysicsStep
    {
        internal const string DiagnosisVariable = "ZTK_STEP_DIAG";

        internal struct DecisionRecord
        {
            public int frame;
            public double seconds;          // since the session started (Stopwatch)
            public bool scripted, clockPaused, gamePaused, applicationPaused;
            public double owed, stepSeconds, dropped, remaining, expected, budget;
            public bool shouldStep, ownedAndBudget, withinCostAlone, stepped, verify, reevaluateAsked, frameStartWasZero;
            public int skippedInARow, collectors, costSamples;
            public long stepId;
            public string reason;
        }

        private const int DiagnosisCapacity = 16384;
        private static DecisionRecord[] s_diagnosis;
        private static int s_diagnosisCount;   // total written; the ring holds the last DiagnosisCapacity
        private static long s_diagnosisStart;

        /// <summary>Whether this session records its decisions (ZTK_STEP_DIAG=1 at the session's start).</summary>
        internal static bool DiagnosisOn => s_diagnosis != null;

        internal static int DiagnosisCount => s_diagnosisCount;

        private static void ResetDiagnosisForSession()
        {
            bool on = Environment.GetEnvironmentVariable(DiagnosisVariable) == "1";
            s_diagnosis = on ? new DecisionRecord[DiagnosisCapacity] : null;
            s_diagnosisCount = 0;
            s_diagnosisStart = Stopwatch.GetTimestamp();
            if (on) UnityEngine.Debug.Log("[step diagnosis] on for this session (" + DiagnosisVariable + "=1)");
        }

        private static void RecordDecision(bool scripted, double remaining, double expected, bool shouldStep, bool step, bool verify, long frameStart)
        {
            if (s_diagnosis == null)
            {
                return;
            }

            bool owedAndBudget = s_clock.ShouldStep(0.0, remaining);
            bool withinCostAlone = s_clock.ShouldStep(expected, double.PositiveInfinity);
            string reason;
            if (!scripted) reason = "not-script-mode";
            else if (verify) reason = "verification-step";
            else if (step) reason = "stepped";
            else if (s_clock.IsPaused) reason = "paused";
            else if (s_clock.UnsimulatedSeconds < s_clock.StepSeconds) reason = "not-owed";
            else if (remaining < 0.0) reason = "no-budget-left";
            else if (!shouldStep && owedAndBudget && withinCostAlone) reason = "estimate-over-remaining";
            else reason = "other";
            ref DecisionRecord r = ref s_diagnosis[s_diagnosisCount % DiagnosisCapacity];
            r.frame = UnityEngine.Time.frameCount;
            r.seconds = (double)(Stopwatch.GetTimestamp() - s_diagnosisStart) / Stopwatch.Frequency;
            r.scripted = scripted;
            r.clockPaused = s_clock.IsPaused;
            r.gamePaused = s_gamePaused;
            r.applicationPaused = s_applicationPaused;
            r.owed = s_clock.UnsimulatedSeconds;
            r.stepSeconds = s_clock.StepSeconds;
            r.dropped = s_clock.DroppedSeconds;
            r.remaining = remaining;
            r.expected = expected;
            r.budget = s_mainBudgetSeconds;
            r.shouldStep = shouldStep;
            r.ownedAndBudget = owedAndBudget;
            r.withinCostAlone = withinCostAlone;
            r.stepped = step;
            r.verify = verify;
            r.reevaluateAsked = s_reevaluateAsked;
            r.frameStartWasZero = frameStart == 0;
            r.skippedInARow = SkippedForCostInARow;
            r.collectors = s_collectors.Count;
            r.costSamples = s_costs.Count;
            r.stepId = s_clock.StepId;
            r.reason = reason;
            s_diagnosisCount++;
        }

        /// <summary>The state the next decision will read, in one line (for an event mark).</summary>
        internal static string DiagnosisState()
        {
            var b = new StringBuilder();
            b.Append("clock paused ").Append(s_clock.IsPaused).Append(", game paused ").Append(s_gamePaused).Append(", application paused ").Append(s_applicationPaused)
                .Append(", owed ").Append(R(s_clock.UnsimulatedSeconds)).Append(" s (step ").Append(R(s_clock.StepSeconds)).Append(" s, dropped ").Append(R(s_clock.DroppedSeconds))
                .Append(" s), step id ").Append(s_clock.StepId).Append(", physics s ").Append(R(s_clock.PhysicsSeconds))
                .Append(", budget ").Append(R(s_mainBudgetSeconds)).Append(" s, expected ").Append(R(s_costs.ExpectedSeconds)).Append(" s from ").Append(s_costs.Count).Append(" samples")
                .Append(", skipped for cost in a row ").Append(SkippedForCostInARow).Append(", re-evaluation asked ").Append(s_reevaluateAsked)
                .Append(", verification steps ").Append(VerificationSteps).Append(", collectors ").Append(s_collectors.Count)
                .Append(", last decided frame ").Append(LastDecidedFrame).Append(", last simulated frame ").Append(LastSimulatedFrame)
                .Append(", last decision stepped ").Append(LastDecisionStepped).Append(" (remaining ").Append(R(LastDecisionRemainingSeconds)).Append(" s, expected ").Append(R(LastDecisionExpectedSeconds)).Append(" s)")
                .Append(", frame start set ").Append(s_frameStart != 0).Append(", simulation mode ").Append(UnityEngine.Physics.simulationMode)
                .Append(", frame ").Append(UnityEngine.Time.frameCount);
            return b.ToString();
        }

        /// <summary>The records from <paramref name="from"/> (a count, as <see cref="DiagnosisCount"/> returned) to now, as CSV lines.</summary>
        internal static void AppendDiagnosisCsv(StringBuilder b, int from)
        {
            if (s_diagnosis == null)
            {
                return;
            }

            int start = Math.Max(from, s_diagnosisCount - DiagnosisCapacity);
            for (int i = start; i < s_diagnosisCount; i++)
            {
                DecisionRecord r = s_diagnosis[i % DiagnosisCapacity];
                b.Append(r.frame).Append(',').Append(R(r.seconds)).Append(',').Append(r.reason).Append(',').Append(r.stepped).Append(',').Append(r.verify).Append(',')
                    .Append(r.scripted).Append(',').Append(r.clockPaused).Append(',').Append(r.gamePaused).Append(',').Append(r.applicationPaused).Append(',')
                    .Append(R(r.owed)).Append(',').Append(R(r.stepSeconds)).Append(',').Append(R(r.dropped)).Append(',').Append(R(r.remaining)).Append(',').Append(R(r.expected)).Append(',').Append(R(r.budget)).Append(',')
                    .Append(r.shouldStep).Append(',').Append(r.ownedAndBudget).Append(',').Append(r.withinCostAlone).Append(',').Append(r.reevaluateAsked).Append(',').Append(r.frameStartWasZero).Append(',')
                    .Append(r.skippedInARow).Append(',').Append(r.collectors).Append(',').Append(r.costSamples).Append(',').Append(r.stepId).Append('\n');
            }
        }

        internal const string DiagnosisCsvHeader = "frame,seconds,reason,stepped,verify,scripted,clockPaused,gamePaused,applicationPaused,owed,stepSeconds,dropped,remaining,expected,budget,shouldStep,owedAndBudget,withinCostAlone,reevaluateAsked,frameStartWasZero,skippedInARow,collectors,costSamples,stepId";

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
