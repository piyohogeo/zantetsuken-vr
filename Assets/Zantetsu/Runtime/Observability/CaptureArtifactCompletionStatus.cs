namespace Zantetsu.Observability
{
    internal enum CaptureArtifactCompletionStatus : int
    {
        None = 0,
        Staged = 1,
        Failed = 2,
        Cancelled = 3
    }
}
