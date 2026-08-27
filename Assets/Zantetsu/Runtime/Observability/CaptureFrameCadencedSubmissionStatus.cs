namespace Zantetsu.Observability
{
    /// <summary>
    /// Outcome of a cadence-gated capture frame submission. Explicitly valued
    /// and append-only; numeric values are fixed.
    /// </summary>
    public enum CaptureFrameCadencedSubmissionStatus : int
    {
        /// <summary>No submission attempted. Default sentinel and reserved state.</summary>
        None = 0,

        /// <summary>The frame was not selected by the cadence selector; nothing was submitted.</summary>
        NotSelected = 1,

        /// <summary>The frame was selected and submitted successfully.</summary>
        Submitted = 2,

        /// <summary>The frame was selected but rejected by downstream backpressure.</summary>
        Backpressured = 3,
    }
}
