using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of one successful session issuance: the session and
    /// the ownership lease that owns the OS lock, issued together and bound to
    /// one another.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> is the only way to obtain one. It validates both
    /// inputs and their mutual correlation before constructing anything,
    /// and it is the only place a session is constructed, so a session cannot
    /// exist except as part of an issue. <see cref="IsValid"/> recomputes the
    /// same correlations from the held values without throwing.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing -- the OS lock belongs to the
    /// ownership lease -- and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSessionIssue
    {
        private readonly CaptureRunInitializationSession _session;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private CaptureRunInitializationSessionIssue(
            CaptureRunInitializationSession session,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _session = session;
            _ownershipLease = ownershipLease;
        }

        internal static CaptureRunInitializationSessionIssue Create(
            CaptureRunInitializationSessionOwnershipLease ownershipLease,
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

            if (!evidence.IsValid)
            {
                throw new ArgumentException("Ready evidence must be valid.", nameof(evidence));
            }

            if (!ownershipLease.IsCreated)
            {
                throw new ArgumentException("Ownership lease must be live.", nameof(ownershipLease));
            }

            CaptureRunLockPathSet pathSet = ownershipLease.LockPathSet;
            if (pathSet == null || pathSet.RootLayout == null)
            {
                throw new ArgumentException("Ownership lease must hold a lock path set with a root layout.", nameof(ownershipLease));
            }

            if (!ReferenceEquals(pathSet.RootLayout, evidence.RootLayout))
            {
                throw new ArgumentException("The held lock and the ready evidence must share the same root layout.", nameof(evidence));
            }

            if (pathSet.RootLayout.TestRunId != evidence.TestRunId)
            {
                throw new ArgumentException("The held lock and the ready evidence must share the same test run ID.", nameof(evidence));
            }

            return new CaptureRunInitializationSessionIssue(
                new CaptureRunInitializationSession(evidence),
                ownershipLease);
        }

        internal CaptureRunInitializationSession Session => _session;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunLockPathSet LockPathSet => _ownershipLease.LockPathSet;

        internal bool IsValid
        {
            get
            {
                if (_session == null || !_session.IsValid)
                {
                    return false;
                }

                if (_ownershipLease == null || !_ownershipLease.IsCreated)
                {
                    return false;
                }

                CaptureRunLockPathSet pathSet = _ownershipLease.LockPathSet;
                if (pathSet == null || pathSet.RootLayout == null)
                {
                    return false;
                }

                return ReferenceEquals(pathSet.RootLayout, _session.RootLayout)
                    && pathSet.RootLayout.TestRunId == _session.TestRunId;
            }
        }
    }
}
