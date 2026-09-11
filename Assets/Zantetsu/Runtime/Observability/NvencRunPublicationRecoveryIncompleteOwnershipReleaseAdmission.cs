namespace Zantetsu.Observability
{
    /// <summary>
    /// The single place that decides whether one orphan-cleanup release attempt
    /// may be started, shared by the release boundary and its synchronous
    /// execution coordinator so the two can never disagree.
    /// </summary>
    /// <remarks>
    /// A first attempt and a retry after a partial release failure are told
    /// apart from the lease's own state rather than from a latch: a fully
    /// retained lease has never been released, and a lease that is no longer
    /// fully retained but whose disposal has not completed is a partial
    /// release. Admission validity is required only for a first attempt,
    /// because a partial release necessarily makes it false. A completed
    /// release, a broken binding, and an operation that was already
    /// inadmissible are refused. A cleaned and a failed cleanup are not
    /// distinguished here - both have finished with this attempt's lock - and
    /// no retry counter, attempt latch, or partial-release status is
    /// introduced. This type holds no state, reads no process state, and
    /// changes nothing.
    /// </remarks>
    internal static class NvencRunPublicationRecoveryIncompleteOwnershipReleaseAdmission
    {
        /// <summary>
        /// A first attempt: the operation is admissible in its own right, and
        /// its exact lease is still fully retained and releasable.
        /// </summary>
        internal static bool IsFirstAttemptAdmissible(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            return operation != null
                && operation.IsValid
                && operation.OwnershipLease.IsCreated
                && operation.CanRelease;
        }

        /// <summary>
        /// A retry after a partial release failure: the reference correlation
        /// still holds, the lease is no longer fully retained, its disposal has
        /// not completed, and another attempt is therefore still possible. The
        /// operation's admission validity is deliberately not required, because
        /// a partially released lease always fails it.
        /// </summary>
        internal static bool IsRetryAdmissible(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            return operation != null
                && operation.IsBindingIntact
                && !operation.OwnershipLease.IsCreated
                && operation.CanRelease
                && !operation.OwnershipLease.IsReleaseComplete;
        }

        internal static bool IsAdmissible(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseOperation operation)
        {
            return IsFirstAttemptAdmissible(operation) || IsRetryAdmissible(operation);
        }
    }
}
