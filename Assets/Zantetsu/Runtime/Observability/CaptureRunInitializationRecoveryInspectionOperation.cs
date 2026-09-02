using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free operation describing one Capture Run recovery
    /// inspection: the root layout to observe and the non-owning reference to
    /// the held lock lease that authorizes the observation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lease must be created and its path set must share the operation's
    /// root layout; the operation never disposes the lease. The maximum root
    /// entry count is a positive bound no greater than
    /// <see cref="MaximumAllowedRootEntryCount"/>; the inspector reads at most
    /// <see cref="ProbeCount"/> direct entries per root before stopping.
    /// <see cref="IsValid"/> recomputes these checks from the held values
    /// without throwing, so an operation whose lease has been released becomes
    /// invalid.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryInspectionOperation
    {
        internal const int MaximumAllowedRootEntryCount = 1024;

        private readonly CaptureRunRootLayout _rootLayout;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;
        private readonly int _maximumRootEntryCount;

        internal CaptureRunInitializationRecoveryInspectionOperation(
            CaptureRunRootLayout rootLayout,
            CaptureRunLockIdentityEvidence lockIdentityEvidence,
            int maximumRootEntryCount)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            if (lockIdentityEvidence == null)
            {
                throw new ArgumentNullException(nameof(lockIdentityEvidence));
            }

            if (!lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Lock identity evidence must be valid.", nameof(lockIdentityEvidence));
            }

            CaptureRunLockPathSet pathSet = lockIdentityEvidence.LockPathSet;
            if (pathSet == null)
            {
                throw new ArgumentException("Lock identity evidence must hold a path set.", nameof(lockIdentityEvidence));
            }

            if (!ReferenceEquals(pathSet.RootLayout, rootLayout))
            {
                throw new ArgumentException("Lock identity evidence path set must share the operation's root layout.", nameof(lockIdentityEvidence));
            }

            if (maximumRootEntryCount < 1 || maximumRootEntryCount > MaximumAllowedRootEntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumRootEntryCount), maximumRootEntryCount, "Maximum root entry count must be between 1 and " + MaximumAllowedRootEntryCount + ".");
            }

            _rootLayout = rootLayout;
            _lockIdentityEvidence = lockIdentityEvidence;
            _maximumRootEntryCount = maximumRootEntryCount;
        }

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        internal int MaximumRootEntryCount => _maximumRootEntryCount;

        internal int ProbeCount => checked(_maximumRootEntryCount + 1);

        internal bool IsValid
        {
            get
            {
                if (_rootLayout == null || _lockIdentityEvidence == null || _maximumRootEntryCount < 1 || _maximumRootEntryCount > MaximumAllowedRootEntryCount)
                {
                    return false;
                }

                if (!_lockIdentityEvidence.IsValid)
                {
                    return false;
                }

                CaptureRunLockPathSet pathSet = _lockIdentityEvidence.LockPathSet;
                return pathSet != null && ReferenceEquals(pathSet.RootLayout, _rootLayout);
            }
        }
    }
}
