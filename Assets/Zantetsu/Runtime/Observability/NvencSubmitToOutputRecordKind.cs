namespace Zantetsu.Observability
{
    /// <summary>
    /// Exclusive variant kind of one Phase 0.11 Submit-to-Output record.
    /// Explicitly valued and append-only; existing values never change.
    /// </summary>
    internal enum NvencSubmitToOutputRecordKind : int
    {
        /// <summary>No record. Default sentinel.</summary>
        None = 0,

        /// <summary>The accepted work was submitted to NVENC.</summary>
        Submitted = 1,

        /// <summary>The accepted work failed before an NVENC submit.</summary>
        FailedBeforeSubmit = 2,
    }
}
