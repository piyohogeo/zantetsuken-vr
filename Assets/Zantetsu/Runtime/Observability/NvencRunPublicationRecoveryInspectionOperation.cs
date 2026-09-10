using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free operation authorizing one Phase 0.11 NVENC
    /// publication recovery inspection: the exact open outcome that still holds
    /// the OS lock, and the exact root layout whose Run the observation
    /// describes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Issuance requires a valid open outcome whose status is
    /// <see cref="CaptureRunInitializationOpenStatus.PublicationRecoveryRequired"/>,
    /// that holds no session, that still holds its lock through valid lock
    /// identity evidence bound to its exact path set, and whose exact root
    /// layout is the one supplied here. Every check is the existing
    /// initialization predicate; no new proof, receipt, token, nonce, or
    /// generation is introduced.
    /// </para>
    /// <para>
    /// This type reads no file, parses no plan, computes no hash, deletes
    /// nothing, releases no lock, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject. It never restores or infers the
    /// previous process's <see cref="NvencRunEvidenceDisposition"/>.
    /// <see cref="IsValid"/> recomputes every check without throwing, so an
    /// operation whose outcome or lock has gone becomes invalid.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunPublicationRecoveryInspectionOperation
    {
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunRootLayout _rootLayout;

        internal NvencRunPublicationRecoveryInspectionOperation(
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunRootLayout rootLayout)
        {
            if (openOutcome == null)
            {
                throw new ArgumentNullException(nameof(openOutcome));
            }

            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            if (!openOutcome.IsValid)
            {
                throw new ArgumentException("Open outcome must be valid.", nameof(openOutcome));
            }

            if (openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
            {
                throw new ArgumentException(
                    "Open outcome must require publication recovery.", nameof(openOutcome));
            }

            if (openOutcome.Session != null)
            {
                throw new ArgumentException(
                    "Open outcome must not hold a session.", nameof(openOutcome));
            }

            if (!HoldsLock(openOutcome))
            {
                throw new ArgumentException(
                    "Open outcome must still hold its lock through valid identity evidence.",
                    nameof(openOutcome));
            }

            if (!rootLayout.IsValid || !ReferenceEquals(openOutcome.RootLayout, rootLayout))
            {
                throw new ArgumentException(
                    "Root layout must be the open outcome's exact valid root layout.", nameof(rootLayout));
            }

            if (openOutcome.TestRunId != rootLayout.TestRunId)
            {
                throw new ArgumentException(
                    "Open outcome and root layout must describe the same Run.", nameof(rootLayout));
            }

            _openOutcome = openOutcome;
            _rootLayout = rootLayout;
        }

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;

        internal CaptureRunRootLayout RootLayout => _rootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence =>
            _openOutcome.OrchestrationResult.LockIdentityEvidence;

        internal long TestRunId => _openOutcome.TestRunId;

        internal string RunInitializationId => _openOutcome.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _openOutcome != null
                        && _rootLayout != null
                        && _openOutcome.IsValid
                        && _openOutcome.Status
                            == CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                        && _openOutcome.Session == null
                        && HoldsLock(_openOutcome)
                        && _rootLayout.IsValid
                        && ReferenceEquals(_openOutcome.RootLayout, _rootLayout)
                        && _openOutcome.TestRunId == _rootLayout.TestRunId;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool HoldsLock(CaptureRunInitializationOpenOutcome openOutcome)
        {
            CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult =
                openOutcome.OrchestrationResult;
            if (orchestrationResult == null)
            {
                return false;
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = orchestrationResult.LockIdentityEvidence;

            return lockIdentityEvidence != null
                && lockIdentityEvidence.IsValid
                && openOutcome.LockPathSet != null
                && ReferenceEquals(lockIdentityEvidence.LockPathSet, openOutcome.LockPathSet);
        }
    }
}
