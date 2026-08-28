namespace Zantetsu.Observability
{
    /// <summary>
    /// Orthogonal emission state for a capture frame draft's drop trace,
    /// independent of <see cref="CaptureFrameDraftStatus"/>. Explicitly valued
    /// and append-only; numeric values are fixed and must never be reused or
    /// reordered.
    /// </summary>
    /// <remarks>
    /// Rollback is not permitted: <c>Attempted</c> must never transition back
    /// to <c>Pending</c>, and no state may transition back to <c>None</c>.
    /// </remarks>
    internal enum DraftDropTraceEmissionState : int
    {
        /// <summary>No drop trace emission scheduled. Default sentinel.</summary>
        None = 0,

        /// <summary>Awaiting issuance of a normal drop trace.</summary>
        Pending = 1,

        /// <summary>
        /// Emission was attempted regardless of enqueue success or failure.
        /// </summary>
        Attempted = 2,
    }
}
