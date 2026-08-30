namespace Zantetsu.Observability
{
    internal enum CaptureFrameCompletionStatus : int
    {
        None = 0,
        Succeeded = 1,
        Failed = 2,
        Cancelled = 3
    }
}
