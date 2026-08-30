namespace Zantetsu.Observability
{
    /// <summary>
    /// Identifies which fixed publication document a publication recovery
    /// observation describes. Values are fixed, explicitly numbered, and
    /// append-only; existing values must never be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is never a valid observation kind and is rejected by
    /// the observation constructor.
    /// </remarks>
    internal enum CaptureRunPublicationDocumentKind : int
    {
        None = 0,
        PublicationPlanTemporary = 1,
        PublicationPlan = 2,
        CaptureIndexTemporary = 3,
        CaptureIndex = 4
    }
}
