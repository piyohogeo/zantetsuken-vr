using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Append-only, process-local disposition of one Phase 0.11 NVENC Run.
    /// <see cref="None"/> advances to <see cref="Finalized"/> or
    /// <see cref="Incomplete"/> at the Trace freeze boundary, and the
    /// Publication Plan commit boundary advances <see cref="Finalized"/> to
    /// <see cref="Committed"/>, <see cref="Incomplete"/>, or
    /// <see cref="CommitOutcomeUnknown"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="None"/> is the uninitialized state and can never be used as
    /// the basis for classification, cleanup, publication, or CaptureComplete.
    /// Any unknown value is fail-closed by callers. This type owns, mutates,
    /// and disposes nothing and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </remarks>
    internal enum NvencRunEvidenceDisposition
    {
        None = 0,
        Finalized = 1,
        Incomplete = 2,
        Committed = 3,
        CommitOutcomeUnknown = 4,
        PublicationRecoveryRequired = 5,
    }
}
