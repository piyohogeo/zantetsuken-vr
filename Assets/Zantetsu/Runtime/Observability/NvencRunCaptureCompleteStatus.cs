namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only status of one NVENC Run CaptureComplete attempt.
    /// <see cref="None"/> is the uninitialized default and is never a terminal
    /// attempt outcome. <see cref="Completed"/> means CaptureComplete finished
    /// in one synchronous call and a receipt can be issued;
    /// <see cref="Failed"/> means it did not finish.
    /// </summary>
    /// <remarks>
    /// Failed deliberately carries no sub-classification and no failure
    /// reason: deciding what an unfinished CaptureComplete means for the Run,
    /// and whether it becomes a Recovery case, is the later Coordinator's
    /// responsibility, not this status's.
    /// </remarks>
    internal enum NvencRunCaptureCompleteStatus
    {
        None = 0,
        Completed = 1,
        Failed = 2,
    }
}
