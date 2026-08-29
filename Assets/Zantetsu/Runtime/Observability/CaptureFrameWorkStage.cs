namespace Zantetsu.Observability
{
    /// <summary>
    /// Operational progress of capture-frame work. This is deliberately
    /// independent from <see cref="CaptureFrameDraftStatus"/>: draft Staged and
    /// Dropped remain terminal states.
    /// </summary>
    /// <remarks>Explicitly valued and append-only.</remarks>
    internal enum CaptureFrameWorkStage : int
    {
        ReadbackCompleted = 0,
        EncodeQueued = 1,
        Encoding = 2,
        Encoded = 3,
        SaveQueued = 4,
        Saving = 5,
        DurableStaged = 6,
        Published = 7,
        Dropped = 8
    }
}
