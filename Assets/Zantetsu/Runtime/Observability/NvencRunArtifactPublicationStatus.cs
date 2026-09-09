namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only status of one NVENC Run artifact publication attempt.
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// attempt outcome. <see cref="Published"/> means final placement and the
    /// full final length-and-hash verification succeeded and a receipt can be
    /// issued; <see cref="Failed"/> means publication could not be completed
    /// and the plan, registry, and chunk are left unchanged for a later
    /// <c>PublicationRecoveryRequired</c> handoff.
    /// </summary>
    /// <remarks>
    /// Failed-before-rename and publish-outcome-unknown are deliberately not
    /// split here: after the plan commit both are handed to Recovery in the
    /// same process without retry or cleanup, so a finer distinction adds no
    /// new decision.
    /// </remarks>
    internal enum NvencRunArtifactPublicationStatus
    {
        None = 0,
        Published = 1,
        Failed = 2,
    }
}
