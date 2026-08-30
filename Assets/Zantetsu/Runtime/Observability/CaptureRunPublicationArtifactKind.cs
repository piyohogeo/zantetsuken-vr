namespace Zantetsu.Observability
{
    /// <summary>
    /// Identifies which artifact of one plan entry an artifact recovery step
    /// targets. Values are fixed, explicitly numbered, and append-only;
    /// existing values must never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunPublicationArtifactKind : int
    {
        None = 0,
        Png = 1,
        Sidecar = 2
    }
}
