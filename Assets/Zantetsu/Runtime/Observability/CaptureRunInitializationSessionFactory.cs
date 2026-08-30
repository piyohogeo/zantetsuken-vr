using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless factory that transfers ownership of a held lock lease into a
    /// new Run session exactly once, correlating the lease with ready
    /// evidence produced by either the fresh initialization path or the
    /// recovery path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The factory validates everything before constructing the session and
    /// nulls the caller's lease reference only after the session is fully
    /// built. On any failure the caller's lease reference is left untouched
    /// and ownership stays with the caller; the factory never disposes the
    /// lease.
    /// </para>
    /// <para>
    /// After a successful transfer only the session owns the lease and may
    /// dispose it. The recovery operation, snapshot, and orchestration result
    /// may keep a non-owning reference to that lease for correlation, but
    /// disposal responsibility belongs to the session alone. For the recovery
    /// path, the lease referenced by the evidence's recovery result must be
    /// the same instance as the lease being transferred.
    /// </para>
    /// <para>
    /// This type holds no fields, state, array, or collection and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationSessionFactory
    {
        internal static CaptureRunInitializationSession Create(
            ref CaptureRunLockLease lockLease,
            CaptureRunInitializationReadyEvidence evidence)
        {
            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            if (lockLease == null)
            {
                throw new ArgumentNullException(nameof(lockLease));
            }

            if (!lockLease.IsCreated)
            {
                throw new ArgumentException("Lock lease must be created.", nameof(lockLease));
            }

            if (!evidence.IsValid)
            {
                throw new ArgumentException("Ready evidence must be valid.", nameof(evidence));
            }

            CaptureRunLockPathSet pathSet = lockLease.PathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock lease must hold a path set.", nameof(lockLease));
            }

            if (!ReferenceEquals(pathSet.RootLayout, evidence.RootLayout))
            {
                throw new ArgumentException("Lock lease and ready evidence must share the same root layout.", nameof(evidence));
            }

            if (pathSet.RootLayout.TestRunId != evidence.TestRunId)
            {
                throw new ArgumentException("Lock lease and ready evidence must share the same test run ID.", nameof(evidence));
            }

            if (evidence.IsRecovery)
            {
                CaptureRunLockLease evidenceLease = evidence.RecoveryOrchestrationResult.LockLease;
                if (!ReferenceEquals(evidenceLease, lockLease))
                {
                    throw new ArgumentException("Recovery ready evidence must reference the lease being transferred.", nameof(lockLease));
                }
            }

            CaptureRunInitializationSession session = new CaptureRunInitializationSession(lockLease, evidence);

            lockLease = null;
            return session;
        }
    }
}
