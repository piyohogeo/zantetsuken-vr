namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only reason for a completed or deferred artifact verification
    /// terminal state. Values are explicit and must never be renumbered.
    /// </summary>
    internal enum CaptureArtifactVerificationFailureReason : int
    {
        None = 0,
        FileAbsent = 1,
        ShorterThanDeclared = 2,
        LongerThanDeclared = 3,
        HashMismatch = 4,
        ReadIoFailure = 5,
        CheckedLengthOverflow = 6,
        FileChangedDuringRead = 7,
        ReparsePointOrInvalidFileKind = 8,
        PathOrRunCorrelationMismatch = 9,
        BufferUnavailable = 10,
        Cancelled = 11
    }
}
