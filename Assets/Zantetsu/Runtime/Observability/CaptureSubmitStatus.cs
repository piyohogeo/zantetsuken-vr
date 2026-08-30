namespace Zantetsu.Observability
{
    /// <summary>Result of submitting one capture surface to an evidence backend.</summary>
    internal enum CaptureSubmitStatus : int
    {
        None = 0,
        Accepted = 1,
        Backpressured = 2,
        NotAccepting = 3
    }
}
