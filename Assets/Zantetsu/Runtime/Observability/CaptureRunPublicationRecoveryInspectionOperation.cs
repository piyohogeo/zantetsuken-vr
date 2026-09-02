using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free operation describing one Capture Run
    /// publication recovery inspection: the publication paths to observe, the
    /// three observation limits, and the non-owning reference to the open
    /// outcome and its held lock lease that authorize the observation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation never disposes the outcome or the lease. The publication
    /// path set is derived from the outcome's root layout and must share that
    /// layout. <see cref="MaximumRootEntryCount"/> is forwarded from the
    /// initialization recovery inspection operation inside the outcome's
    /// orchestration result; <see cref="RootEntryProbeCount"/> is one more
    /// than that bound.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes every check from the held values
    /// without throwing, so an operation whose outcome has been disposed
    /// becomes invalid.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationRecoveryInspectionOperation
    {
        internal const int MaximumAllowedPlanBytes = 16 * 1024 * 1024;
        internal const int MaximumAllowedEntryCount = 100000;
        internal const int MaximumAllowedPathBytes = 512;

        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunPublicationPathSet _publicationPaths;
        private readonly int _maximumPlanBytes;
        private readonly int _maximumEntryCount;
        private readonly int _maximumPathBytes;

        internal CaptureRunPublicationRecoveryInspectionOperation(
            CaptureRunInitializationOpenOutcome openOutcome,
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes)
        {
            if (openOutcome == null)
            {
                throw new ArgumentNullException(nameof(openOutcome));
            }

            if (!openOutcome.IsValid)
            {
                throw new ArgumentException("Open outcome must be valid.", nameof(openOutcome));
            }

            if (openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
            {
                throw new ArgumentException("Open outcome must require publication recovery.", nameof(openOutcome));
            }

            if (openOutcome.Session != null)
            {
                throw new ArgumentException("Open outcome must not hold a session.", nameof(openOutcome));
            }

            CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult = openOutcome.OrchestrationResult;
            if (orchestrationResult == null)
            {
                throw new ArgumentException("Open outcome must hold an orchestration result.", nameof(openOutcome));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = orchestrationResult.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Open outcome must hold valid lock identity evidence.", nameof(openOutcome));
            }

            if (openOutcome.LockPathSet == null || !ReferenceEquals(lockIdentityEvidence.LockPathSet, openOutcome.LockPathSet))
            {
                throw new ArgumentException("Open outcome lock identity evidence must match its path set.", nameof(openOutcome));
            }

            CaptureRunRootLayout rootLayout = openOutcome.RootLayout;
            if (rootLayout == null || !rootLayout.IsValid)
            {
                throw new ArgumentException("Open outcome root layout must be valid.", nameof(openOutcome));
            }

            CaptureRunPublicationPathSet publicationPaths = new CaptureRunPublicationPathSet(rootLayout);
            if (!publicationPaths.IsValid || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
            {
                throw new ArgumentException("Publication path set must be valid and share the root layout.", nameof(openOutcome));
            }

            if (maximumPlanBytes < 1 || maximumPlanBytes > MaximumAllowedPlanBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPlanBytes), maximumPlanBytes, "Maximum plan bytes must be between 1 and " + MaximumAllowedPlanBytes + ".");
            }

            if (maximumEntryCount < 0 || maximumEntryCount > MaximumAllowedEntryCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumEntryCount), maximumEntryCount, "Maximum entry count must be between 0 and " + MaximumAllowedEntryCount + ".");
            }

            if (maximumPathBytes < 1 || maximumPathBytes > MaximumAllowedPathBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPathBytes), maximumPathBytes, "Maximum path bytes must be between 1 and " + MaximumAllowedPathBytes + ".");
            }

            _openOutcome = openOutcome;
            _publicationPaths = publicationPaths;
            _maximumPlanBytes = maximumPlanBytes;
            _maximumEntryCount = maximumEntryCount;
            _maximumPathBytes = maximumPathBytes;
        }

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;

        internal CaptureRunPublicationPathSet PublicationPaths => _publicationPaths;

        internal int MaximumPlanBytes => _maximumPlanBytes;

        internal int MaximumEntryCount => _maximumEntryCount;

        internal int MaximumPathBytes => _maximumPathBytes;

        internal CaptureRunRootLayout RootLayout => _publicationPaths.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _openOutcome.OrchestrationResult.LockIdentityEvidence;

        internal long TestRunId => _openOutcome.TestRunId;

        internal string RunInitializationId => _openOutcome.RunInitializationId;

        internal int MaximumRootEntryCount => _openOutcome.OrchestrationResult.Snapshot.Operation.MaximumRootEntryCount;

        internal int RootEntryProbeCount => checked(MaximumRootEntryCount + 1);

        internal bool IsValid
        {
            get
            {
                if (_openOutcome == null || _publicationPaths == null
                    || _maximumPlanBytes < 1 || _maximumPlanBytes > MaximumAllowedPlanBytes
                    || _maximumEntryCount < 0 || _maximumEntryCount > MaximumAllowedEntryCount
                    || _maximumPathBytes < 1 || _maximumPathBytes > MaximumAllowedPathBytes)
                {
                    return false;
                }

                if (!_openOutcome.IsValid)
                {
                    return false;
                }

                if (_openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
                {
                    return false;
                }

                if (_openOutcome.Session != null)
                {
                    return false;
                }

                CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult = _openOutcome.OrchestrationResult;
                if (orchestrationResult == null)
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = orchestrationResult.LockIdentityEvidence;
                if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid || !ReferenceEquals(lockIdentityEvidence.LockPathSet, _openOutcome.LockPathSet))
                {
                    return false;
                }

                if (!_publicationPaths.IsValid || !ReferenceEquals(_publicationPaths.RootLayout, _openOutcome.RootLayout))
                {
                    return false;
                }

                return true;
            }
        }
    }
}
