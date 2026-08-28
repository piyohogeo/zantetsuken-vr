namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of a capture frame draft submission. Explicitly valued and
    /// append-only; numeric values are fixed and must never be reused or
    /// reordered.
    /// </summary>
    internal enum CaptureFrameDraftSubmissionStatus : int
    {
        /// <summary>No submission performed. Default sentinel; never returned.</summary>
        None = 0,

        /// <summary>Admission rejected for lack of registry capacity; no draft or ID.</summary>
        AdmissionRejected = 1,

        /// <summary>Draft admitted and request/lease scheduling completed.</summary>
        Scheduled = 2,

        /// <summary>Draft admitted but request/lease scheduling is incomplete.</summary>
        SchedulingBackpressured = 3,
    }
}
