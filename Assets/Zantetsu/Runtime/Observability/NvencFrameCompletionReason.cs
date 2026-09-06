namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed, append-only reasons for one NVENC Frame Completion in Phase 0.11.
    /// A Succeeded completion pairs with <c>None</c>, a Failed completion pairs
    /// with one of the failure reasons, and a Cancelled completion pairs with
    /// one of the cancellation reasons. No reserved, future, native error code,
    /// or exception string is carried here.
    /// </summary>
    internal enum NvencFrameCompletionReason : int
    {
        /// <summary>No reason. Default sentinel; valid only on a Succeeded completion.</summary>
        None = 0,

        /// <summary>The Run chunk rejected the append before any write (a sink controlled failure).</summary>
        RunChunkControlledFailure = 1,

        /// <summary>The GPU crop/flip/color-conversion pass failed before submit.</summary>
        GpuConversionFailed = 2,

        /// <summary>The NVENC encode-picture call failed before submit completed.</summary>
        NvencSubmitFailed = 3,

        /// <summary>The accepted work was cancelled before submit.</summary>
        CancelledBeforeSubmit = 4,

        /// <summary>The run was abandoned and accepted-but-unsubmitted work drained.</summary>
        CancelledAfterRunAbandoned = 5,

        /// <summary>The accepted work was never submitted because of shutdown or drain.</summary>
        DrainedBeforeSubmit = 6,
    }
}
