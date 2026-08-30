namespace Zantetsu.Observability
{
    /// <summary>
    /// Observation status of a publication <c>frames</c> subtree entry. Values
    /// are fixed, explicitly numbered, and append-only; existing values must
    /// never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunPublicationFramesObservationStatus : int
    {
        Absent = 0,
        Directory = 1,
        Invalid = 2
    }
}
