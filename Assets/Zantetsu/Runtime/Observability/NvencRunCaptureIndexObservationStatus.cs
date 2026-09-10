namespace Zantetsu.Observability
{
    /// <summary>
    /// What one Phase 0.11 NVENC Capture Index document was observed to be,
    /// already compared against the authoritative plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately not the generic publication document observation
    /// status: it carries the comparison as well as the read.
    /// <see cref="MatchesAuthoritative"/> means the canonical bytes were the
    /// authoritative plan's, and <see cref="CanonicalMismatch"/> means they
    /// were canonical but described something else. The comparison belongs to
    /// the inspector that read the bytes, so the classifier never re-serializes
    /// or re-compares a plan; this is an observation, not an authority token.
    /// </para>
    /// <para>
    /// Values are explicit and append-only.
    /// </para>
    /// </remarks>
    internal enum NvencRunCaptureIndexObservationStatus : int
    {
        None = 0,
        Absent = 1,
        MatchesAuthoritative = 2,
        CanonicalMismatch = 3,
        Invalid = 4,
        LimitExceeded = 5
    }
}
