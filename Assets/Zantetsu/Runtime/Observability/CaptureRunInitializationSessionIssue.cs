using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one successful session issuance: the session, the
    /// ownership lease that owns the OS lock, and the lock identity evidence,
    /// issued together and bound to one another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only valid issuance path is
    /// <see cref="CaptureRunInitializationSession.IssuanceProof.Mint"/>, which
    /// mints an opaque proof bound to the exact issue nonce, session, ownership
    /// lease, identity evidence, and ready evidence. The internal assignment
    /// constructor is retained only for invalid/uninitialized-shape tests; it
    /// cannot produce a valid issue without the exact opaque proof. There is no
    /// factory that returns a Session alone, so a Session cannot be minted
    /// except as part of an atomic session issue.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSessionIssue
    {
        private readonly CaptureRunInitializationSession _session;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;
        private readonly CaptureRunInitializationSession.IssuanceProof _proof;
        private readonly object _nonce;

        internal CaptureRunInitializationSessionIssue(
            CaptureRunInitializationSession session,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            CaptureRunInitializationSession.IssuanceProof proof,
            object nonce)
        {
            _session = session;
            _ownershipLease = ownershipLease;
            _lockIdentityEvidence = lockIdentityEvidence;
            _proof = proof;
            _nonce = nonce;
        }

        internal CaptureRunInitializationSession Session => _session;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        /// <summary>
        /// Exception-safe exact-binding comparison: returns only the
        /// <see cref="ReferenceEquals"/> result of the held proof, nonce,
        /// session, ownership lease, identity evidence, and ready evidence
        /// against the supplied values. It never returns the held proof,
        /// nonce, or any reference out of this type.
        /// </summary>
        internal bool MatchesIssuedBindings(
            CaptureRunInitializationSession.IssuanceProof proof,
            object nonce,
            CaptureRunInitializationSession session,
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockIdentityEvidence identityEvidence,
            CaptureRunInitializationReadyEvidence readyEvidence)
        {
            if (proof == null || nonce == null || session == null || ownershipLease == null || identityEvidence == null || readyEvidence == null)
            {
                return false;
            }

            return ReferenceEquals(_proof, proof)
                && ReferenceEquals(_nonce, nonce)
                && ReferenceEquals(_session, session)
                && ReferenceEquals(_ownershipLease, ownershipLease)
                && ReferenceEquals(_lockIdentityEvidence, identityEvidence)
                && ReferenceEquals(_session.ReadyEvidence, readyEvidence);
        }

        internal bool IsValid
        {
            get
            {
                if (_proof == null || !_proof.IsIssuedFor(this))
                {
                    return false;
                }

                if (_session == null || !_session.IsValid)
                {
                    return false;
                }

                if (_ownershipLease == null || !_ownershipLease.IsCreated)
                {
                    return false;
                }

                if (_lockIdentityEvidence == null || !_lockIdentityEvidence.IsValid)
                {
                    return false;
                }

                if (!_lockIdentityEvidence.IsIssuedFor(_ownershipLease))
                {
                    return false;
                }

                CaptureRunLockPathSet pathSet = _lockIdentityEvidence.LockPathSet;
                if (pathSet == null || pathSet.RootLayout == null)
                {
                    return false;
                }

                return ReferenceEquals(pathSet.RootLayout, _session.RootLayout)
                    && _lockIdentityEvidence.TestRunId == _session.TestRunId;
            }
        }
    }
}
