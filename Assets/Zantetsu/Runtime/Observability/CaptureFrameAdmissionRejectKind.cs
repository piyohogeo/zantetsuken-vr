namespace Zantetsu.Observability
{
    /// <summary>
    /// Reasons a capture frame draft admission reservation was rejected.
    /// Explicitly valued and append-only; numeric values are fixed and must
    /// never be reused or reordered.
    /// </summary>
    internal enum CaptureFrameAdmissionRejectKind : int
    {
        /// <summary>No rejection. Default sentinel.</summary>
        None = 0,

        /// <summary>The reusable pending slot pool was full.</summary>
        PendingLimit = 1,

        /// <summary>The append-only per-run entry store was full.</summary>
        RunEntryLimit = 2,
    }
}
