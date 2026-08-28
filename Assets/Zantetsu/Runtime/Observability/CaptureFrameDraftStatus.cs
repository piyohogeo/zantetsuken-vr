namespace Zantetsu.Observability
{
    /// <summary>
    /// Lifecycle status of a capture frame draft before its PNG artifact is
    /// committed. Explicitly valued and append-only; numeric values are fixed
    /// and must never be reused or reordered.
    /// </summary>
    internal enum CaptureFrameDraftStatus : int
    {
        /// <summary>Accepted and awaiting terminal processing.</summary>
        Pending = 0,

        /// <summary>Registered as a PNG staging entry.</summary>
        Staged = 1,

        /// <summary>
        /// Terminal state excluded from the final record and expected artifact
        /// set.
        /// </summary>
        Dropped = 2,
    }
}
