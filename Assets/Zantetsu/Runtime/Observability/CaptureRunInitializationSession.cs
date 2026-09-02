using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-owning value that correlates a Run's ready evidence with its run
    /// identity. It holds no lock and cannot release one; OS lock ownership
    /// lives exclusively in <see cref="CaptureRunInitializationSessionOwnershipLease"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session holds only ready evidence — whether from the fresh path or
    /// the recovery path — and forwards run identity straight from it. Session
    /// validity means only that the ready evidence is valid, not that any lock
    /// is still held; lock liveness is confirmed through
    /// <see cref="CaptureRunLockIdentityEvidence"/>.
    /// </para>
    /// <para>
    /// Forwarding properties read straight from the evidence and hold no
    /// copied value. This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSession
    {
        private readonly CaptureRunInitializationReadyEvidence _readyEvidence;

        /// <summary>
        /// Opaque issuance proof minted only by <see cref="Mint"/>, bound to
        /// the exact nonce, session, ownership lease, identity evidence, and
        /// ready evidence of one issued triple. Its constructor is private, so
        /// no other code can fabricate or cross-substitute a proof.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly object _nonce;
            private readonly CaptureRunInitializationSession _session;
            private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;
            private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;
            private readonly CaptureRunInitializationReadyEvidence _readyEvidence;

            private IssuanceProof(
                object nonce,
                CaptureRunInitializationSession session,
                CaptureRunInitializationSessionOwnershipLease ownershipLease,
                CaptureRunLockIdentityEvidence lockIdentityEvidence,
                CaptureRunInitializationReadyEvidence readyEvidence)
            {
                _nonce = nonce;
                _session = session;
                _ownershipLease = ownershipLease;
                _lockIdentityEvidence = lockIdentityEvidence;
                _readyEvidence = readyEvidence;
            }

            /// <summary>
            /// The only session issuance boundary: validates the exact
            /// ownership lease, lock identity evidence, and ready evidence,
            /// mints the session privately, generates a per-issue nonce, binds
            /// an opaque proof to the exact issued triple, and returns only
            /// the <see cref="CaptureRunInitializationSessionIssue"/>.
            /// </summary>
            internal static CaptureRunInitializationSessionIssue Mint(
                CaptureRunInitializationSessionOwnershipLease ownershipLease,
                CaptureRunLockIdentityEvidence lockIdentityEvidence,
                CaptureRunInitializationReadyEvidence evidence)
            {
                if (evidence == null)
                {
                    throw new ArgumentNullException(nameof(evidence));
                }

                if (ownershipLease == null)
                {
                    throw new ArgumentNullException(nameof(ownershipLease));
                }

                if (lockIdentityEvidence == null)
                {
                    throw new ArgumentNullException(nameof(lockIdentityEvidence));
                }

                if (!evidence.IsValid)
                {
                    throw new ArgumentException("Ready evidence must be valid.", nameof(evidence));
                }

                if (!ownershipLease.IsCreated)
                {
                    throw new ArgumentException("Ownership lease must be live.", nameof(ownershipLease));
                }

                if (!lockIdentityEvidence.IsValid)
                {
                    throw new ArgumentException("Lock identity evidence must be valid.", nameof(lockIdentityEvidence));
                }

                if (!lockIdentityEvidence.IsIssuedFor(ownershipLease))
                {
                    throw new ArgumentException("Lock identity evidence must be issued for the exact ownership lease.", nameof(lockIdentityEvidence));
                }

                CaptureRunLockPathSet pathSet = lockIdentityEvidence.LockPathSet;
                if (!ReferenceEquals(pathSet.RootLayout, evidence.RootLayout))
                {
                    throw new ArgumentException("Lock identity evidence and ready evidence must share the same root layout.", nameof(evidence));
                }

                if (pathSet.RootLayout.TestRunId != evidence.TestRunId)
                {
                    throw new ArgumentException("Lock identity evidence and ready evidence must share the same test run ID.", nameof(evidence));
                }

                if (evidence.IsRecovery)
                {
                    CaptureRunLockIdentityEvidence evidenceLock = evidence.RecoveryOrchestrationResult.LockIdentityEvidence;
                    if (!ReferenceEquals(evidenceLock, lockIdentityEvidence))
                    {
                        throw new ArgumentException("Recovery ready evidence must reference the identity evidence being issued.", nameof(lockIdentityEvidence));
                    }
                }

                CaptureRunInitializationSession session = new CaptureRunInitializationSession(evidence);
                object nonce = new object();
                IssuanceProof proof = new IssuanceProof(nonce, session, ownershipLease, lockIdentityEvidence, evidence);
                return new CaptureRunInitializationSessionIssue(session, ownershipLease, lockIdentityEvidence, proof, nonce);
            }

            /// <summary>
            /// Exception-safe exact-binding check: the given issue must be the
            /// exact issue minted for this proof's nonce, session, ownership
            /// lease, identity evidence, and ready evidence. Any forged or
            /// cross-substituted reference converges to <c>false</c>.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunInitializationSessionIssue issue)
            {
                return issue != null
                    && issue.MatchesIssuedBindings(this, _nonce, _session, _ownershipLease, _lockIdentityEvidence, _readyEvidence);
            }
        }

        private CaptureRunInitializationSession(CaptureRunInitializationReadyEvidence readyEvidence)
        {
            _readyEvidence = readyEvidence;
        }

        internal CaptureRunInitializationReadyEvidence ReadyEvidence => _readyEvidence;

        internal CaptureRunInitializationExecutionReceipt ExecutionReceipt => _readyEvidence.FreshExecutionReceipt;

        internal CaptureRunInitializationRecoveryOrchestrationResult RecoveryOrchestrationResult => _readyEvidence.RecoveryOrchestrationResult;

        internal CaptureRunRootLayout RootLayout => _readyEvidence.RootLayout;

        internal long TestRunId => _readyEvidence.TestRunId;

        internal string RunInitializationId => _readyEvidence.RunInitializationId;

        internal bool IsValid => _readyEvidence != null && _readyEvidence.IsValid;

    }
}
