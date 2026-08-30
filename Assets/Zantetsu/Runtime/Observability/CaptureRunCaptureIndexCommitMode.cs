namespace Zantetsu.Observability
{
    /// <summary>
    /// How one Capture Index commit operation handles the observed
    /// <c>capture.index.tmp</c> state. Values are fixed, explicitly numbered,
    /// and append-only; existing values must never be renumbered or removed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="CreateTemporaryAndCommit"/> is selected when the temporary
    /// index is absent; <see cref="ReuseCanonicalTemporaryAndCommit"/> when it
    /// is canonical and byte-for-byte equal to the authoritative plan;
    /// <see cref="ReplaceInvalidTemporaryAndCommit"/> when it is invalid.
    /// <see cref="None"/> is never a valid held mode.
    /// </para>
    /// </remarks>
    internal enum CaptureRunCaptureIndexCommitMode : int
    {
        None = 0,
        CreateTemporaryAndCommit = 1,
        ReuseCanonicalTemporaryAndCommit = 2,
        ReplaceInvalidTemporaryAndCommit = 3
    }
}
