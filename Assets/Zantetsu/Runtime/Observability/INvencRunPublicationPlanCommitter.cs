namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 NVENC Run publication plan commit boundary. It
    /// commits exactly one issued
    /// <see cref="NvencRunPublicationPlanCommitOperation"/> and returns one
    /// <see cref="NvencRunPublicationPlanCommitAttemptResult"/> per call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is contractually the future Publication Service; caller
    /// thread selection is not this boundary's duty, and no dispatcher or
    /// thread identity mechanism is added here.
    /// </para>
    /// <para>
    /// Before any side effect a <c>null</c> operation must throw
    /// <see cref="System.ArgumentNullException"/> for <c>operation</c>, and an
    /// invalid operation (one whose current
    /// <see cref="NvencRunPublicationPlanCommitOperation.IsValid"/> is false)
    /// must throw <see cref="System.ArgumentException"/> for <c>operation</c>.
    /// The operation is never mutated. Each call is one attempt and returns
    /// exactly one result; the implementation performs no second attempt and
    /// no undo.
    /// </para>
    /// </remarks>
    internal interface INvencRunPublicationPlanCommitter
    {
        NvencRunPublicationPlanCommitAttemptResult Commit(
            NvencRunPublicationPlanCommitOperation operation);
    }
}
