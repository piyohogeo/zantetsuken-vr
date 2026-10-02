using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Zantetsu.Core;

namespace Zantetsu.PhysicsCut
{
    // DIAGNOSIS ONLY (2026-10-02): the step decision of every frame, recorded with what it read, so that a decision that
    // does not step can be told apart -- paused, nothing owed, no budget left, the estimate over the budget, not in script
    // mode. Off unless the environment variable ZTK_STEP_DIAG is "1" when a play session starts (and only where DEBUG is
    // defined); then each decision, and each mark named by a caller, is one record of the development logger (DESIGN 21.17,
    // writer "CutPhysicsStep"), its value the field names and values in turn. Nothing of the decision itself is changed.
    //
    // The record has an explicit end (DiagnosisEnd): from the session's start to it every record tried is counted, the
    // summary is the last record, and afterwards no decision or mark is recorded (the physics goes on; only the record stops).
    // The logger is best effort (accepted is not saved): each record carries its own sequence number (seq, one per record
    // tried, from 1), and the attempts, acceptances and refusals by reason are counted here, so that the saved file can be
    // checked against them afterwards; a record is complete only if every record from seq 1 to the summary was saved.
    //
    // seconds: the Stopwatch (QueryPerformanceCounter) clock, from the moment ResetDiagnosisForSession ran (the session's
    // SubsystemRegistration reset). A decision's seconds are the decision's own reading -- the one its remaining budget
    // was computed from; a mark's and the summary's are read when they are made.
    public static partial class CutPhysicsStep
    {
        internal const string DiagnosisVariable = "ZTK_STEP_DIAG";
        internal const string DiagnosisWriter = "CutPhysicsStep";

        private static bool s_diagnosisOn;
        private static bool s_diagnosisEnded;
        private static long s_diagnosisStart;
        private static long s_diagnosisSeq;
        private static long s_diagnosisAccepted;
        private static long s_diagnosisQueueFull, s_diagnosisUnavailable, s_diagnosisInvalid, s_diagnosisDisabled;

        /// <summary>Whether this session records its decisions (ZTK_STEP_DIAG=1 at the session's start, DEBUG defined).</summary>
        internal static bool DiagnosisOn => s_diagnosisOn;

        /// <summary>Whether the record was ended (<see cref="DiagnosisEnd"/>): nothing is recorded after it.</summary>
        internal static bool DiagnosisEnded => s_diagnosisEnded;

        /// <summary>Records tried this session: the seq of the last one.</summary>
        internal static long DiagnosisAttempted => s_diagnosisSeq;

        /// <summary>Records the logger accepted (queued -- not yet known to be saved).</summary>
        internal static long DiagnosisAccepted => s_diagnosisAccepted;

        private static void ResetDiagnosisForSession()
        {
            s_diagnosisEnded = false;
            s_diagnosisSeq = 0;
            s_diagnosisAccepted = 0;
            s_diagnosisQueueFull = s_diagnosisUnavailable = s_diagnosisInvalid = s_diagnosisDisabled = 0;
            s_diagnosisStart = Stopwatch.GetTimestamp();
#if DEBUG
            s_diagnosisOn = Environment.GetEnvironmentVariable(DiagnosisVariable) == "1";
            if (s_diagnosisOn) UnityEngine.Debug.Log("[step diagnosis] on for this session (" + DiagnosisVariable + "=1): records of writer " + DiagnosisWriter + " to the development logger");
#else
            s_diagnosisOn = false;
#endif
        }

        private static void RecordDecision(bool scripted, double remaining, double expected, bool shouldStep, bool step, bool verify, long frameStart, long now)
        {
            if (!s_diagnosisOn || s_diagnosisEnded)
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
            WriteDiagnosis("decision", now, new object[]
            {
                "reason", reason, "stepped", B(step), "verify", B(verify), "scripted", B(scripted),
                "clockPaused", B(s_clock.IsPaused), "gamePaused", B(s_gamePaused), "applicationPaused", B(s_applicationPaused),
                "owed", s_clock.UnsimulatedSeconds, "stepSeconds", s_clock.StepSeconds, "dropped", s_clock.DroppedSeconds,
                "remaining", remaining, "expected", expected, "budget", s_mainBudgetSeconds,
                "shouldStep", B(shouldStep), "owedAndBudget", B(owedAndBudget), "withinCostAlone", B(withinCostAlone),
                "reevaluateAsked", B(s_reevaluateAsked), "frameStartWasZero", B(frameStart == 0),
                "skippedInARow", SkippedForCostInARow, "collectors", s_collectors.Count, "costSamples", s_costs.Count, "stepId", s_clock.StepId,
            });
        }

        /// <summary>A named mark (a test's begin or end, say) with the state the next decision will read; nothing while off or after the end.</summary>
        internal static void DiagnosisMark(string what, string name, string outcome)
        {
            if (!s_diagnosisOn || s_diagnosisEnded)
            {
                return;
            }

            WriteDiagnosis("mark", Stopwatch.GetTimestamp(), new object[] { "what", what, "name", name, "outcome", outcome, "state", DiagnosisState() });
        }

        /// <summary>
        /// Ends the record (once; nothing while off or once ended): the summary is tried as the last record, then no decision or
        /// mark is recorded for the rest of the session. The summary record's counts are of the records before it
        /// (seq 1..N-1); the summary itself is seq N. The same counts, the summary included (attempted N, accepted and refused
        /// over seq 1..N), go to the Editor's log, which does not depend on the logger: what the saved file is checked against.
        /// </summary>
        internal static void DiagnosisEnd()
        {
            if (!s_diagnosisOn || s_diagnosisEnded)
            {
                return;
            }

            WriteDiagnosis("summary", Stopwatch.GetTimestamp(), new object[]
            {
                "attemptedBefore", s_diagnosisSeq, "acceptedBefore", s_diagnosisAccepted, "queueFull", s_diagnosisQueueFull,
                "unavailable", s_diagnosisUnavailable, "invalidValue", s_diagnosisInvalid, "disabled", s_diagnosisDisabled,
            });
            s_diagnosisEnded = true;
            UnityEngine.Debug.Log("[step diagnosis] ended: attempted " + s_diagnosisSeq + " (seq 1.." + s_diagnosisSeq + ", the summary last), accepted " + s_diagnosisAccepted
                + ", refused: queue full " + s_diagnosisQueueFull + ", unavailable " + s_diagnosisUnavailable + ", invalid value " + s_diagnosisInvalid
                + ", disabled " + s_diagnosisDisabled);
        }

        // One record: seq, the frame of the moment it is made and its seconds (from the reading given), then the caller's fields.
        private static void WriteDiagnosis(string tag, long timestamp, object[] fields)
        {
            long seq = ++s_diagnosisSeq;
            var values = new object[6 + fields.Length];
            values[0] = "seq";
            values[1] = seq;
            values[2] = "eventFrame";
            values[3] = UnityEngine.Time.frameCount;
            values[4] = "seconds";
            values[5] = (double)(timestamp - s_diagnosisStart) / Stopwatch.Frequency;
            Array.Copy(fields, 0, values, 6, fields.Length);
#if DEBUG
            DevelopmentLogResult result = DevelopmentLogger.Instance.write_log(DiagnosisWriter, tag, values);
            switch (result)
            {
                case DevelopmentLogResult.Accepted: s_diagnosisAccepted++; break;
                case DevelopmentLogResult.QueueFull: s_diagnosisQueueFull++; break;
                case DevelopmentLogResult.Unavailable: s_diagnosisUnavailable++; break;
                case DevelopmentLogResult.InvalidValue: s_diagnosisInvalid++; break;
                default: s_diagnosisDisabled++; break;
            }
#endif
        }

        private static int B(bool value) => value ? 1 : 0;

        /// <summary>The state the next decision will read, in one line (for a mark).</summary>
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

        private static string R(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
