namespace Zantetsu.Observability
{
    /// <summary>
    /// Fixed, append-only reasons one accepted work reaches
    /// FailedBeforeSubmit in the Phase 0.11 NVENC path. Only the values the
    /// current design requires are defined; no reserved or future values,
    /// native error codes, or exception strings are carried here.
    /// </summary>
    internal enum NvencFailedBeforeSubmitReason : int
    {
        /// <summary>No failure. Default sentinel; never valid on a failure record.</summary>
        None = 0,

        /// <summary>The GPU crop/flip/color-conversion pass failed before submit.</summary>
        GpuConversionFailed = 1,

        /// <summary>The NVENC encode-picture call failed before submit completed.</summary>
        NvencSubmitFailed = 2,

        /// <summary>The accepted work was cancelled before submit.</summary>
        CancelledBeforeSubmit = 3,

        /// <summary>The run was abandoned and accepted-but-unsubmitted work drained.</summary>
        CancelledAfterRunAbandoned = 4,

        /// <summary>The accepted work was never submitted because of shutdown or drain.</summary>
        DrainedBeforeSubmit = 5,
    }
}
