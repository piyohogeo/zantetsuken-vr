namespace Zantetsu.Observability
{
    /// <summary>
    /// Evidence status of one publication artifact observation. Values are
    /// fixed, explicitly numbered, and append-only; existing values must never
    /// be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is never a valid held status and is rejected by the
    /// observation constructors.
    /// </remarks>
    internal enum CaptureRunPublicationEvidenceStatus : int
    {
        None = 0,
        Absent = 1,
        MatchesExpected = 2,
        Mismatch = 3,
        Invalid = 4,
        LimitExceeded = 5
    }
}
