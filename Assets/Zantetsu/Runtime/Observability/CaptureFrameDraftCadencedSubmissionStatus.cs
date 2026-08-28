namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of a cadence-gated capture frame draft submission. Explicitly
    /// valued and append-only; numeric values are fixed and must never be
    /// reused or reordered.
    /// </summary>
    internal enum CaptureFrameDraftCadencedSubmissionStatus : int
    {
        /// <summary>No submission performed. Default sentinel; never returned.</summary>
        None = 0,

        /// <summary>The cadence selector did not select this frame.</summary>
        NotSelected = 1,

        /// <summary>Admission rejected for lack of registry capacity; no draft or ID.</summary>
        AdmissionRejected = 2,

        /// <summary>Draft admitted and request/lease scheduling completed.</summary>
        Scheduled = 3,

        /// <summary>Draft admitted but request/lease scheduling is incomplete.</summary>
        SchedulingBackpressured = 4,
    }
}
