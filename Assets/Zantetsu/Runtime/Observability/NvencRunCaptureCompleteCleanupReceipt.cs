using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run CaptureComplete cleanup receipt: the
    /// exact cleaner and the exact cleanup operation whose cleanup is known to
    /// have finished in one synchronous call. It is issued only from the
    /// <see cref="NvencRunCaptureCompleteCleanupAttemptResult.Cleaned"/> path
    /// and has no public constructor.
    /// </summary>
    /// <remarks>
    /// This type holds only the exact cleaner and the exact operation, performs
    /// no filesystem, hash, or serialization work, deletes nothing, releases no
    /// lease, and is not an <see cref="IDisposable"/>. The CaptureComplete,
    /// capture index, and artifact publication receipts and operations, the
    /// plan, the root layout, and the run identity are forwarded from the
    /// operation and never duplicated as fields, and no new proof, token, or
    /// nonce is introduced. A receipt is process-local evidence that one
    /// synchronous cleanup call succeeded.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteCleanupReceipt
    {
        private readonly INvencRunCaptureCompleteCleaner _cleaner;
        private readonly NvencRunCaptureCompleteCleanupOperation _operation;

        private NvencRunCaptureCompleteCleanupReceipt(
            INvencRunCaptureCompleteCleaner cleaner,
            NvencRunCaptureCompleteCleanupOperation operation)
        {
            _cleaner = cleaner;
            _operation = operation;
        }

        internal static NvencRunCaptureCompleteCleanupReceipt Create(
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

            return new NvencRunCaptureCompleteCleanupReceipt(cleaner, operation);
        }

        internal INvencRunCaptureCompleteCleaner Cleaner => _cleaner;

        internal NvencRunCaptureCompleteCleanupOperation Operation => _operation;

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

        internal bool IsValid =>
            _cleaner != null
            && _operation != null
            && _operation.IsValid;

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
    }
}
