namespace Zantetsu.Observability
{
    /// <summary>
    /// Disposition selected by the pure publication recovery classifier for one
    /// observed Capture Run. Values are fixed, explicitly numbered, and
    /// append-only; existing values must never be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is never a valid classification result and is
    /// rejected by the decision constructor's recomputation.
    /// </remarks>
    internal enum CaptureRunPublicationRecoveryDisposition : int
    {
        None = 0,
        NoAuthoritativeDocument = 1,
        PublicationPlanAuthoritative = 2,
        CaptureIndexAuthoritative = 3,
        RunRootCollision = 4
    }
}
