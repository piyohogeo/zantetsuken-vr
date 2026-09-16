using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>Where a logical fragment stands (DESIGN 7.1.2, 7.1.3).</summary>
    public enum LogicalFragmentState
    {
        /// <summary>A current target: it can be admitted for a cut and is what a hit resolves to.</summary>
        Live,

        /// <summary>
        /// Replaced by the two children of a published cut. No longer a current target, but its identity and history
        /// remain, and its earlier geometry work is not invalidated by this (DESIGN 7.1.2: 親の現在対象終了は祖先
        /// Pending／Geometry Workの失効を意味せず).
        /// </summary>
        Replaced,

        /// <summary>Retired: by an Abort of its own cut here (DESIGN 7.1.3). Nothing is admitted against it again.</summary>
        Retired,
    }

    /// <summary>
    /// Where an admitted cut stands. An operation counts as incomplete while it is <see cref="Admitted"/> or
    /// <see cref="Published"/>; every other state is terminal and was reached by exactly one decrement of the incomplete
    /// count (DESIGN 7.7).
    /// </summary>
    public enum LogicalCutOperationState
    {
        /// <summary>Admitted, with its physics outcome still to come. This is the source's active operation.</summary>
        Admitted,

        /// <summary>
        /// The two children and the operation are published (DESIGN 7.1.2). The physics side is over; the geometry
        /// responsibility remains, and the incomplete count still holds this operation.
        /// </summary>
        Published,

        /// <summary>Geometry responsibility completed after publication. Terminal.</summary>
        Completed,

        /// <summary>Ended after publication without geometry completing, by a legitimate terminal notice. Terminal.</summary>
        Terminated,

        /// <summary>Final physics could not be established before publication; the source was retired. Terminal.</summary>
        Aborted,

        /// <summary>
        /// The result arrived after the source's ownership authority had changed under it (DESIGN 8): reclaimed without
        /// being applied, and without retiring the source. Terminal.
        /// </summary>
        Stale,
    }

    /// <summary>
    /// What admission decided (DESIGN 4.2, 7.6, 7.7), in the order the checks run. Only <see cref="Admitted"/> issues
    /// an operation id or changes anything; every other value is a skip that stored nothing and will not be retried.
    /// </summary>
    public enum LogicalCutAdmission
    {
        Admitted,

        /// <summary>The source is not a live fragment of this ledger.</summary>
        SourceNotLive,

        /// <summary>The source already has an active operation; the request is skipped, not queued.</summary>
        SourceActive,

        /// <summary>Classified before admission as not splitting both sides: a no-op, nothing changes.</summary>
        NoOp,

        /// <summary>The incomplete-operation count is at its limit; nothing changes and the request is not kept.</summary>
        Full,
    }

    /// <summary>What happened to a result or a notice handed to the ledger for an operation.</summary>
    public enum LogicalCutResultOutcome
    {
        /// <summary>Applied, exactly once.</summary>
        Applied,

        /// <summary>
        /// Not applied: the operation was active but the source's authority had changed since admission. The
        /// operation was reclaimed as stale; the source and every other fragment are untouched.
        /// </summary>
        Stale,

        /// <summary>
        /// Not applied and nothing changed: the operation is unknown, or is not in the state this notice is for — a
        /// duplicate, a late arrival after a terminal state, or a geometry notice for a cut never published.
        /// </summary>
        NotActive,

        /// <summary>
        /// Not applied and nothing changed: the cut's anchor distribution was never prepared, so there is nothing to
        /// hand the children. An unprepared distribution is never read as "this owner had no anchors" (DESIGN 7.1) —
        /// the caller prepares it and publishes again.
        /// </summary>
        AnchorsNotPrepared,
    }

    /// <summary>
    /// What preparing an admitted cut's anchor distribution decided. Only <see cref="Prepared"/> leaves a
    /// distribution the publication can hand on; neither of the others changes any anchor set, and neither is a
    /// reason on its own to retire a source — infeasibility is Abort's to declare and lost authority is found as
    /// Stale when the result arrives.
    /// </summary>
    public enum AnchorPreparationOutcome
    {
        /// <summary>
        /// A distribution is prepared and waiting for publication. Also the answer for an operation that was already
        /// prepared: the existing distribution stands, unchanged, whatever epsilon the second call passed.
        /// </summary>
        Prepared,

        /// <summary>The operation is unknown, or is not admitted — already published, ended, or never issued.</summary>
        OperationNotActive,

        /// <summary>
        /// The distribution itself was refused (see the result's own status): the operation stays unprepared and
        /// nothing else changed.
        /// </summary>
        DistributionRefused,
    }

    /// <summary>
    /// A published or admitted cut as the ledger holds it: parent, adopted plane, and after publication the child on
    /// each side (DESIGN 7.1.2: 親・子・面・Sideを持つLogicalCutOperation). A copy; changing it changes nothing.
    /// </summary>
    public readonly struct LogicalCutOperation
    {
        internal LogicalCutOperation(
            CutOperationId id,
            LogicalFragmentId source,
            float4 plane,
            LogicalCutOperationState state,
            LogicalFragmentId positive,
            LogicalFragmentId negative)
        {
            this.id = id;
            this.source = source;
            this.plane = plane;
            this.state = state;
            this.positive = positive;
            this.negative = negative;
        }

        public readonly CutOperationId id;
        public readonly LogicalFragmentId source;
        public readonly float4 plane;
        public readonly LogicalCutOperationState state;

        /// <summary>The positive-side child; unset until the operation is published.</summary>
        public readonly LogicalFragmentId positive;

        /// <summary>The negative-side child; unset until the operation is published.</summary>
        public readonly LogicalFragmentId negative;

        public bool IsIncomplete => state == LogicalCutOperationState.Admitted || state == LogicalCutOperationState.Published;
    }
}
