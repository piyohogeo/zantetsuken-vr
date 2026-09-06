namespace Zantetsu.Observability
{
    /// <summary>
    /// Terminal outcome of one synchronous append attempt reported by a
    /// pre-opened Run chunk writer. Exactly one of the three values is true
    /// for a single call: <c>Appended</c> means every byte was written exactly
    /// once, <c>RejectedBeforeWrite</c> means a known rejection was detected
    /// before any write, and <c>Indeterminate</c> means a partial write or an
    /// unknown result whose ownership must not be guessed.
    /// </summary>
    internal enum NvencRunChunkAppendOutcome
    {
        /// <summary>All bytes were appended exactly once.</summary>
        Appended,

        /// <summary>A known rejection was detected before any byte was written.</summary>
        RejectedBeforeWrite,

        /// <summary>The append partially wrote or its result is unknown.</summary>
        Indeterminate,
    }
}
