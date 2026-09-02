using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless factory that issues the Run session, ownership lease, and
    /// lock identity evidence as one exact, atomic session issue, correlating
    /// the ready evidence produced by either the fresh initialization path or
    /// the recovery path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is non-owning and holds no release capability; only the
    /// ownership lease owns the raw lock. The factory never transfers, nulls,
    /// or disposes the caller's existing ownership lease or identity evidence;
    /// it validates everything and mints the session privately inside the
    /// atomic issuance boundary, returning only the exact
    /// <see cref="CaptureRunInitializationSessionIssue"/>. On any failure the
    /// caller's ownership lease and identity evidence stay untouched.
    /// </para>
    /// <para>
    /// For the recovery path, the identity evidence referenced by the ready
    /// evidence's recovery result must be the exact instance being issued.
    /// </para>
    /// <para>
    /// This type holds no fields, state, array, or collection and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationSessionFactory
    {
        internal static CaptureRunInitializationSessionIssue Create(
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            CaptureRunInitializationReadyEvidence evidence)
        {
            // Session construction lives exclusively inside the atomic
            // issuance boundary on the Session type; this stateless wrapper
            // forwards to it without ever minting a Session directly.
            return CaptureRunInitializationSession.IssuanceProof.Mint(ownershipLease, lockIdentityEvidence, evidence);
        }
    }
}
