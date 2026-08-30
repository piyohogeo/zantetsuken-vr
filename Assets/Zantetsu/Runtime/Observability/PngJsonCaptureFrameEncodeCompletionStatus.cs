namespace Zantetsu.Observability
{
    /// <summary>Terminal outcome of one accepted encode operation.</summary>
    /// <remarks>Explicitly valued and append-only.</remarks>
    internal enum PngJsonCaptureFrameEncodeCompletionStatus : int
    {
        Succeeded = 0,
        Failed = 1,
        Cancelled = 2
    }
}
