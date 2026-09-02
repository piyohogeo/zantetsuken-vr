using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning Run lock identity evidence: the exact lock path
    /// set and root layout acquired for the Run, bound to the exact ownership
    /// lease that holds them. It is session-independent, so it is valid on the
    /// recovery and publication paths before any session exists.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsValid"/> re-confirms, without throwing, that the held
    /// ownership lease still fully holds the lock and is still bound to the
    /// exact path set captured at issuance. A foreign, replaced, released, or
    /// reflection-nulled value converges to <c>false</c>.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunLockIdentityEvidence
    {
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;
        private readonly CaptureRunLockPathSet _lockPathSet;

        private CaptureRunLockIdentityEvidence(
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockPathSet lockPathSet)
        {
            _ownershipLease = ownershipLease;
            _lockPathSet = lockPathSet;
        }

        /// <summary>
        /// Validated factory: binds the exact ownership lease and lock path set
        /// together. The ownership lease reference is kept private and only
        /// used for liveness checks.
        /// </summary>
        internal static CaptureRunLockIdentityEvidence Create(
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockPathSet lockPathSet)
        {
            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (lockPathSet == null)
            {
                throw new ArgumentNullException(nameof(lockPathSet));
            }

            if (!ReferenceEquals(ownershipLease.LockPathSet, lockPathSet))
            {
                throw new ArgumentException("Ownership lease must be bound to the exact path set.", nameof(ownershipLease));
            }

            if (!ownershipLease.IsCreated)
            {
                throw new ArgumentException("Ownership lease must be live.", nameof(ownershipLease));
            }

            if (lockPathSet.RootLayout == null)
            {
                throw new ArgumentException("Lock path set must hold a root layout.", nameof(lockPathSet));
            }

            return new CaptureRunLockIdentityEvidence(ownershipLease, lockPathSet);
        }

        internal CaptureRunLockPathSet LockPathSet => _lockPathSet;

        internal CaptureRunRootLayout RootLayout => _lockPathSet.RootLayout;

        internal long TestRunId => _lockPathSet.RootLayout.TestRunId;

        /// <summary>
        /// Exception-safe recomputation of the exact issuance correlation and
        /// the ownership lease liveness. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_ownershipLease == null || _lockPathSet == null)
                {
                    return false;
                }

                if (!_ownershipLease.IsCreated)
                {
                    return false;
                }

                if (!ReferenceEquals(_ownershipLease.LockPathSet, _lockPathSet))
                {
                    return false;
                }

                return _lockPathSet.RootLayout != null;
            }
        }

        /// <summary>
        /// Exception-safe exact-issuance check: the given ownership lease must
        /// be the exact instance this evidence was issued for, bound to the
        /// exact lock path set, and both must still be live. A foreign,
        /// replaced, released, or reflection-nulled ownership lease converges
        /// to <c>false</c> without throwing.
        /// </summary>
        internal bool IsIssuedFor(
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (ownershipLease == null || _ownershipLease == null || _lockPathSet == null)
            {
                return false;
            }

            if (!ReferenceEquals(_ownershipLease, ownershipLease))
            {
                return false;
            }

            if (!_ownershipLease.IsCreated)
            {
                return false;
            }

            CaptureRunLockPathSet ownedPathSet = ownershipLease.LockPathSet;
            if (ownedPathSet == null || !ReferenceEquals(ownedPathSet, _lockPathSet))
            {
                return false;
            }

            return _lockPathSet.RootLayout != null;
        }
    }
}
