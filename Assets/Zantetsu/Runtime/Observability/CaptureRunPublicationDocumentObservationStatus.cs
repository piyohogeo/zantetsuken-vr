namespace Zantetsu.Observability
{
    /// <summary>
    /// Observation status of a single publication document entry within a run
    /// root. Values are fixed, explicitly numbered, and append-only; existing
    /// values must never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunPublicationDocumentObservationStatus : int
    {
        Absent = 0,
        Canonical = 1,
        Invalid = 2,
        LimitExceeded = 3
    }
}
