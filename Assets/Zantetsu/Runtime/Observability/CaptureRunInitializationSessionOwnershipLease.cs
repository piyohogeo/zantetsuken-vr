using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Sole owner of a Run's two acquired lock handles for the full duration of
    /// the Run. It is the only type that may hold and release the raw
    /// <see cref="CaptureRunLockLease"/>, and it is bound to the exact lock
    /// path set and root layout acquired at lock acquisition, independent of
    /// any session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The raw lease is never exposed. Disposal delegates to the raw lease's
    /// reverse-order release and is the only release path; after a partial
    /// release failure the same ownership lease may be retried, and a fully
    /// successful disposal is idempotent. <see cref="IsCreated"/> reflects
    /// full retention of both handles, so a partially released lease is no
    /// longer live even though disposal may still be retried.
    /// </para>
    /// <para>
    /// This type has no public API, is not a Unity Object, and holds no static
    /// mutable state.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSessionOwnershipLease : IDisposable
    {
        private readonly CaptureRunLockLease _lockLease;
        private readonly CaptureRunLockPathSet _lockPathSet;
        private bool _disposed;

        private CaptureRunInitializationSessionOwnershipLease(
            CaptureRunLockLease lockLease,
            CaptureRunLockPathSet lockPathSet)
        {
            _lockLease = lockLease;
            _lockPathSet = lockPathSet;
        }

        /// <summary>
        /// Validated factory: binds the live raw lock lease to its exact path
        /// set and transfers ownership here, nulling the caller's reference
        /// only on full success.
        /// </summary>
        internal static CaptureRunInitializationSessionOwnershipLease Create(
            ref CaptureRunLockLease lockLease)
        {
            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            if (!lockLease.IsCreated)
            {
                throw new ArgumentException("Lock lease must be created.", nameof(lockLease));
            }

            CaptureRunLockPathSet pathSet = lockLease.PathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock lease must hold a path set.", nameof(lockLease));
            }

            CaptureRunInitializationSessionOwnershipLease ownershipLease =
                new CaptureRunInitializationSessionOwnershipLease(lockLease, pathSet);

            lockLease = null;
            return ownershipLease;
        }

        internal CaptureRunLockPathSet LockPathSet => _lockPathSet;

        /// <summary>
        /// Reports full retention of both acquired lock handles. A partial
        /// release failure makes this false while disposal remains retryable.
        /// </summary>
        internal bool IsCreated => !_disposed && _lockLease != null && _lockLease.IsCreated;

        /// <summary>
        /// Reports that disposal has not yet completed, so the next
        /// <see cref="Dispose"/> may still be attempted (first attempt or a
        /// retry after a partial failure).
        /// </summary>
        internal bool CanRelease => !_disposed && _lockLease != null;

        /// <summary>
        /// Reports that the raw lock lease completed disposal without throwing
        /// and both handles are released. This is derived from the raw lease's
        /// handle state, never from the disposal flag alone, so reflection-
        /// setting <c>_disposed</c> cannot fabricate release success.
        /// </summary>
        internal bool IsReleaseComplete => _lockLease != null && _lockLease.IsFullyReleased;

        /// <summary>
        /// Releases the raw lock lease exactly once. A partial failure keeps
        /// this ownership lease retryable; the raw lease never double-touches a
        /// released OS handle.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _lockLease.Dispose();
            _disposed = true;
        }
    }
}
