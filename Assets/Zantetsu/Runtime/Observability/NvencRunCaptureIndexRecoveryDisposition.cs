namespace Zantetsu.Observability
{
    /// <summary>
    /// What one Phase 0.11 NVENC Capture Index recovery observation means for
    /// a Run whose publication was already found recoverable.
    /// </summary>
    /// <remarks>
    /// There is no Incomplete and no Deferred here: an incomplete, deferred, or
    /// colliding publication is refused before the Capture Index inspection
    /// operation can be issued at all. Values are explicit and append-only.
    /// </remarks>
    internal enum NvencRunCaptureIndexRecoveryDisposition : int
    {
        None = 0,
        CommitRequired = 1,
        CaptureCompleteRequired = 2,
        PublicationRecoveryCollision = 3
    }
}
