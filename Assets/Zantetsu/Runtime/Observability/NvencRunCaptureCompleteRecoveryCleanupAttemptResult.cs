using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Readonly value result of one Phase 0.11 NVENC recovery CaptureComplete
    /// cleanup attempt: the exact cleaner, the exact operation, a status, and a
    /// receipt held only for the
    /// <see cref="NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned"/>
    /// shape.
    /// </summary>
    /// <remarks>
    /// The two terminal shapes are mutually exclusive and
    /// <see cref="NvencRunCaptureCompleteRecoveryCleanupStatus.None"/> is the
    /// uninitialized default, never a terminal shape. A Failed attempt carries
    /// no receipt and says nothing about how far the cleanup got: there is no
    /// per-target result, partial-progress ledger, unknown outcome, or
    /// retryable flag. The CaptureComplete receipt, decision, snapshot,
    /// optional commit receipt, plan, root layout, and Run identity are
    /// forwarded from the held operation rather than duplicated as fields, and
    /// no new proof, token, or nonce is introduced.
    /// </remarks>
    internal readonly struct NvencRunCaptureCompleteRecoveryCleanupAttemptResult
    {
        private readonly INvencRunCaptureCompleteRecoveryCleaner _cleaner;
        private readonly NvencRunCaptureCompleteRecoveryCleanupOperation _operation;
        private readonly NvencRunCaptureCompleteRecoveryCleanupReceipt _receipt;
        private readonly NvencRunCaptureCompleteRecoveryCleanupStatus _status;

        private NvencRunCaptureCompleteRecoveryCleanupAttemptResult(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation,
            NvencRunCaptureCompleteRecoveryCleanupReceipt receipt,
            NvencRunCaptureCompleteRecoveryCleanupStatus status)
        {
            _cleaner = cleaner;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal static NvencRunCaptureCompleteRecoveryCleanupAttemptResult Cleaned(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            RequireIssuable(cleaner, operation);

            return new NvencRunCaptureCompleteRecoveryCleanupAttemptResult(
                cleaner,
                operation,
                NvencRunCaptureCompleteRecoveryCleanupReceipt.Cleaned(cleaner, operation),
                NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned);
        }

        internal static NvencRunCaptureCompleteRecoveryCleanupAttemptResult Failed(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            RequireIssuable(cleaner, operation);

            return new NvencRunCaptureCompleteRecoveryCleanupAttemptResult(
                cleaner,
                operation,
                null,
                NvencRunCaptureCompleteRecoveryCleanupStatus.Failed);
        }

        internal INvencRunCaptureCompleteRecoveryCleaner Cleaner => _cleaner;

        internal NvencRunCaptureCompleteRecoveryCleanupOperation Operation => _operation;

        internal NvencRunCaptureCompleteRecoveryCleanupReceipt Receipt => _receipt;

        internal NvencRunCaptureCompleteRecoveryCleanupStatus Status => _status;

        internal NvencRunCaptureCompleteRecoveryReceipt CaptureCompleteReceipt =>
            _operation.CaptureCompleteReceipt;

        internal NvencRunCaptureCompleteRecoveryOperation CaptureCompleteOperation =>
            _operation.CaptureCompleteOperation;

        internal NvencRunCaptureIndexRecoveryDecision CaptureIndexRecoveryDecision =>
            _operation.CaptureIndexRecoveryDecision;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot CaptureIndexRecoverySnapshot =>
            _operation.CaptureIndexRecoverySnapshot;

        internal NvencRunCaptureIndexRecoveryCommitReceipt CaptureIndexRecoveryCommitReceipt =>
            _operation.CaptureIndexRecoveryCommitReceipt;

        internal bool HasCommitReceipt => _operation.HasCommitReceipt;

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            _operation.PublicationRecoveryDecision;

        internal CapturePublicationPlan AuthoritativePlan => _operation.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsNone => _status == NvencRunCaptureCompleteRecoveryCleanupStatus.None;

        internal bool IsCleaned =>
            _status == NvencRunCaptureCompleteRecoveryCleanupStatus.Cleaned
            && _cleaner != null
            && _operation != null
            && _receipt != null
            && _receipt.IsIssuedFor(_cleaner, _operation);

        internal bool IsFailed =>
            _status == NvencRunCaptureCompleteRecoveryCleanupStatus.Failed
            && _cleaner != null
            && _operation != null
            && _operation.IsValid
            && _receipt == null;

        internal bool IsValid => IsCleaned || IsFailed;

        internal bool IsIssuedFor(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            return cleaner != null
                && operation != null
                && ReferenceEquals(_cleaner, cleaner)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        private static void RequireIssuable(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            if (cleaner == null)
            {
                throw new ArgumentNullException(nameof(cleaner));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }
        }
    }
}
