namespace Zantetsu.Observability
{
    /// <summary>
    /// Whether an artifact verification executed to a terminal content
    /// classification (<see cref="Completed"/>) or could not execute
    /// (<see cref="Deferred"/>). <see cref="None"/> is the uninitialized value
    /// and is never a valid result disposition.
    /// </summary>
    internal enum CaptureArtifactVerificationExecutionDisposition : int
    {
        None = 0,
        Completed = 1,
        Deferred = 2
    }
}
