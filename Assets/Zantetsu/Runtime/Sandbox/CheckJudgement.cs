namespace Zantetsu.Sandbox
{
    /// <summary>
    /// How the check judges an expectation whose case belongs to one scenario (TL, 2026-10-03): required where its scenario
    /// brings the case about; elsewhere judged only when the case arose by itself, and otherwise written down as not applicable
    /// (its section did not run) or not exercised (its case did not arise) -- neither counted as passed.
    /// </summary>
    public static class CheckJudgement
    {
        public enum Kind
        {
            /// <summary>Judged, and the case itself required.</summary>
            Required,
            /// <summary>The case arose by itself: what it must keep is judged; that it arose is not required.</summary>
            JudgedWhereItArose,
            /// <summary>The case arose by itself where it is not required: counted, nothing to judge beyond what is judged everywhere.</summary>
            Counted,
            /// <summary>The section that brings the case about did not run, and the case did not arise.</summary>
            NotApplicable,
            /// <summary>Not required here, and the case did not arise.</summary>
            NotExercised,
        }

        /// <summary>
        /// [hull drop] a drop stopped by a re-cut: required where the synthetic re-cut during a drop ran; elsewhere a stop
        /// the run's own re-cuts made is judged, and with none the expectation does not apply.
        /// </summary>
        public static Kind HullDropStop(bool syntheticSectionRan, int stopped) =>
            syntheticSectionRan ? Kind.Required : stopped > 0 ? Kind.JudgedWhereItArose : Kind.NotApplicable;

        /// <summary>
        /// [scenario] a released slot activated again: required of the MobPlan scenarios; a city walk counts a reuse and
        /// writes none down as not exercised (what a reuse must keep is judged everywhere, apart).
        /// </summary>
        public static Kind SlotReuse(bool cityWalk, int reusedActivations) =>
            !cityWalk ? Kind.Required : reusedActivations > 0 ? Kind.Counted : Kind.NotExercised;
    }
}
