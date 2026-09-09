using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Readonly value result of one NVENC Run CaptureComplete cleanup attempt:
    /// the exact cleaner, the exact operation, and a status with a receipt held
    /// only for the <see cref="NvencRunCaptureCompleteCleanupStatus.Cleaned"/>
    /// shape. The two terminal shapes are mutually exclusive;
    /// <see cref="NvencRunCaptureCompleteCleanupStatus.None"/> is the
    /// uninitialized default and is never visible as a terminal shape.
    /// </summary>
    /// <remarks>
    /// The CaptureComplete, capture index, and artifact publication receipts
    /// and operations, the plan, the root layout, and the run identity are
    /// forwarded from the held operation and never duplicated as fields, and no
    /// new proof, token, or nonce is introduced.
    /// </remarks>
    internal readonly struct NvencRunCaptureCompleteCleanupAttemptResult
    {
        private readonly INvencRunCaptureCompleteCleaner _cleaner;
        private readonly NvencRunCaptureCompleteCleanupOperation _operation;
        private readonly NvencRunCaptureCompleteCleanupReceipt _receipt;
        private readonly NvencRunCaptureCompleteCleanupStatus _status;

        private NvencRunCaptureCompleteCleanupAttemptResult(
            INvencRunCaptureCompleteCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation,
            NvencRunCaptureCompleteCleanupReceipt receipt,
            NvencRunCaptureCompleteCleanupStatus status)
        {
            _cleaner = cleaner;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal INvencRunCaptureCompleteCleaner Cleaner => _cleaner;

        internal NvencRunCaptureCompleteCleanupOperation Operation => _operation;

        internal NvencRunCaptureCompleteCleanupReceipt Receipt => _receipt;

        internal NvencRunCaptureCompleteCleanupStatus Status => _status;

        internal NvencRunCaptureCompleteReceipt CaptureCompleteReceipt =>
            _operation.CaptureCompleteReceipt;

        internal NvencRunCaptureCompleteOperation CaptureCompleteOperation =>
            _operation.CaptureCompleteOperation;

        internal NvencRunCaptureIndexCommitReceipt CaptureIndexCommitReceipt =>
            _operation.CaptureIndexCommitReceipt;

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt =>
            _operation.ArtifactPublicationReceipt;

        internal CapturePublicationPlan Plan => _operation.Plan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsNone => _status == NvencRunCaptureCompleteCleanupStatus.None;

        internal bool IsCleaned =>
            _status == NvencRunCaptureCompleteCleanupStatus.Cleaned
            && _cleaner != null
            && _operation != null
            && _receipt != null
            && _receipt.IsIssuedFor(_cleaner, _operation);

        internal bool IsFailed =>
            _status == NvencRunCaptureCompleteCleanupStatus.Failed
            && _cleaner != null
            && _operation != null
            && _operation.IsValid
            && _receipt == null;

        internal bool IsValid => IsCleaned || IsFailed;

        internal bool IsIssuedFor(
            INvencRunCaptureCompleteCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            return cleaner != null
                && operation != null
                && ReferenceEquals(_cleaner, cleaner)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        internal static NvencRunCaptureCompleteCleanupAttemptResult Cleaned(
            INvencRunCaptureCompleteCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            if (cleaner == null)
            {
                throw new ArgumentNullException(nameof(cleaner));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            NvencRunCaptureCompleteCleanupReceipt receipt =
                NvencRunCaptureCompleteCleanupReceipt.Create(cleaner, operation);

            return new NvencRunCaptureCompleteCleanupAttemptResult(
                cleaner, operation, receipt, NvencRunCaptureCompleteCleanupStatus.Cleaned);
        }

        internal static NvencRunCaptureCompleteCleanupAttemptResult Failed(
            INvencRunCaptureCompleteCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation)
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
                throw new ArgumentException(
                    "The operation is no longer valid.", nameof(operation));
            }

            return new NvencRunCaptureCompleteCleanupAttemptResult(
                cleaner, operation, null, NvencRunCaptureCompleteCleanupStatus.Failed);
        }
    }
}
