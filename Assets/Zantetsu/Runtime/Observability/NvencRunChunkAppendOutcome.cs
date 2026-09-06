namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal outcome of one synchronous append attempt reported by a
    /// pre-opened Run chunk writer. <c>None</c> is the zero value and is
    /// never treated as a successful append; it is handled as an unknown
    /// result. <c>Appended</c> means every byte was written exactly once,
    /// <c>RejectedBeforeWrite</c> means a known rejection was detected before
    /// any write, and <c>Indeterminate</c> means a partial write or an unknown
    /// result whose ownership must not be guessed.
    /// </summary>
    internal enum NvencRunChunkAppendOutcome
    {
        /// <summary>No outcome was produced; the zero value. Treated as an
        /// unknown result, never as a successful append.</summary>
        None = 0,

        /// <summary>All bytes were appended exactly once.</summary>
        Appended = 1,

        /// <summary>A known rejection was detected before any byte was written.</summary>
        RejectedBeforeWrite = 2,

        /// <summary>The append partially wrote or its result is unknown.</summary>
        Indeterminate = 3,
    }
}
