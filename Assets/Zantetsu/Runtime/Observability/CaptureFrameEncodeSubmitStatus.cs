namespace Zantetsu.Observability
{
    /// <summary>Result of attempting to submit one frame for encoding.</summary>
    /// <remarks>Explicitly valued and append-only.</remarks>
    internal enum CaptureFrameEncodeSubmitStatus : int
    {
        Accepted = 0,
        Backpressured = 1,
        NotAccepting = 2
    }
}
