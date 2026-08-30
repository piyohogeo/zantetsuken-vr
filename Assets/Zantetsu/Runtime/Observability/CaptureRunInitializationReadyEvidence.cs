using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completion evidence that a Capture Run reached the ready
    /// state through exactly one path: the fresh initialization path (an
    /// execution receipt) or the recovery path (a recovery orchestration
    /// result whose status is InitializationReady).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one of the two held references is non-null. The factory methods
    /// validate their input and never copy or mutate the receipt, markers,
    /// result, or binding. <see cref="IsValid"/> recomputes the same
    /// acceptance conditions from the held values without throwing, so a
    /// forged nested value yields <c>false</c> rather than an exception.
    /// </para>
    /// <para>
    /// The recovery path accepts only the three initialization-ready
    /// dispositions; StartFresh, CleanupTemporaryAndStartFresh,
    /// RequiresPublicationRecovery, and RunRootCollision are rejected. A
    /// publication result is rejected even when its markers are present,
    /// because publication-side consistency is not yet established.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationReadyEvidence
    {
        private readonly CaptureRunInitializationExecutionReceipt _freshExecutionReceipt;
        private readonly CaptureRunInitializationRecoveryOrchestrationResult _recoveryOrchestrationResult;

        private CaptureRunInitializationReadyEvidence(
            CaptureRunInitializationExecutionReceipt freshExecutionReceipt,
            CaptureRunInitializationRecoveryOrchestrationResult recoveryOrchestrationResult)
        {
            _freshExecutionReceipt = freshExecutionReceipt;
            _recoveryOrchestrationResult = recoveryOrchestrationResult;
        }

        internal static CaptureRunInitializationReadyEvidence FromFresh(CaptureRunInitializationExecutionReceipt receipt)
        {
            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (!IsValidFresh(receipt))
            {
                throw new ArgumentException("Execution receipt must be a valid fresh initialization receipt.", nameof(receipt));
            }

            return new CaptureRunInitializationReadyEvidence(receipt, null);
        }

        internal static CaptureRunInitializationReadyEvidence FromRecovery(CaptureRunInitializationRecoveryOrchestrationResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException(nameof(result));
            }

            if (!IsValidRecovery(result))
            {
                throw new ArgumentException("Recovery orchestration result must be a valid initialization-ready recovery result.", nameof(result));
            }

            return new CaptureRunInitializationReadyEvidence(null, result);
        }

        internal CaptureRunInitializationExecutionReceipt FreshExecutionReceipt => _freshExecutionReceipt;

        internal CaptureRunInitializationRecoveryOrchestrationResult RecoveryOrchestrationResult => _recoveryOrchestrationResult;

        internal CaptureRunRootLayout RootLayout =>
            _freshExecutionReceipt != null ? _freshExecutionReceipt.RootLayout
            : _recoveryOrchestrationResult != null ? _recoveryOrchestrationResult.RootLayout
            : null;

        internal long TestRunId =>
            _freshExecutionReceipt != null ? _freshExecutionReceipt.TestRunId
            : _recoveryOrchestrationResult != null ? _recoveryOrchestrationResult.TestRunId
            : 0;

        internal string RunInitializationId =>
            _freshExecutionReceipt != null ? _freshExecutionReceipt.RunInitializationId
            : _recoveryOrchestrationResult != null ? _recoveryOrchestrationResult.RunInitializationId
            : null;

        internal bool IsRecovery => _recoveryOrchestrationResult != null;

        internal bool IsValid
        {
            get
            {
                bool hasFresh = _freshExecutionReceipt != null;
                bool hasRecovery = _recoveryOrchestrationResult != null;

                if (hasFresh == hasRecovery)
                {
                    return false;
                }

                return hasFresh
                    ? IsValidFresh(_freshExecutionReceipt)
                    : IsValidRecovery(_recoveryOrchestrationResult);
            }
        }

        private static bool IsValidFresh(CaptureRunInitializationExecutionReceipt receipt)
        {
            return receipt != null
                && receipt.IsValid
                && receipt.RootLayout != null
                && receipt.TestRunId > 0
                && IsLowercaseHex(receipt.RunInitializationId, 32);
        }

        private static bool IsValidRecovery(CaptureRunInitializationRecoveryOrchestrationResult result)
        {
            if (result == null || !result.IsValid)
            {
                return false;
            }

            if (result.Status != CaptureRunInitializationRecoveryExecutionStatus.InitializationReady)
            {
                return false;
            }

            if (!IsInitializationReadyDisposition(result.Disposition))
            {
                return false;
            }

            if (result.RootLayout == null || result.TestRunId <= 0 || !IsLowercaseHex(result.RunInitializationId, 32))
            {
                return false;
            }

            CaptureRunMarkerBinding binding = result.Batch != null ? result.Batch.ExpectedBinding : null;
            if (binding == null || binding.TestRunId != result.TestRunId)
            {
                return false;
            }

            if (!string.Equals(binding.RunInitializationId, result.RunInitializationId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(binding.StagingRunRootSha256, result.RootLayout.StagingRunRootSha256, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(binding.FinalRunRootSha256, result.RootLayout.FinalRunRootSha256, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool IsInitializationReadyDisposition(CaptureRunInitializationRecoveryDisposition disposition)
        {
            return disposition == CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization
                || disposition == CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers
                || disposition == CaptureRunInitializationRecoveryDisposition.AlreadyInitialized;
        }

        private static bool IsLowercaseHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                if (!digit && !lower)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
