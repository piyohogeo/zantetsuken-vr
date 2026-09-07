namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only lifecycle of the Run chunk writer. A writer is Open right
    /// after construction, moves to Finalizing for the single finalize pass,
    /// and then is fixed to Finalized on success or Faulted on any append or
    /// finalize failure. There is no API to move the state backward.
    /// </summary>
    internal enum NvencRunChunkWriterState : int
    {
        None = 0,
        Open = 1,
        Finalizing = 2,
        Finalized = 3,
        Faulted = 4,
    }
}
