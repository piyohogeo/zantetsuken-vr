using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Readonly value result of one NVENC Run CaptureComplete attempt: the
    /// exact completer, the exact operation, and a status with a receipt held
    /// only for the <see cref="NvencRunCaptureCompleteStatus.Completed"/>
    /// shape. The two terminal shapes are mutually exclusive;
    /// <see cref="NvencRunCaptureCompleteStatus.None"/> is the uninitialized
    /// default and is never visible as a terminal shape.
    /// </summary>
    /// <remarks>
    /// The capture index and artifact publication receipts and operations, the
    /// plan, the root layout, and the run identity are forwarded from the held
    /// operation and never duplicated as fields, and no new proof, token,
    /// nonce, or persistent snapshot is introduced.
    /// </remarks>
    internal readonly struct NvencRunCaptureCompleteAttemptResult
    {
        private readonly INvencRunCaptureCompleter _completer;
        private readonly NvencRunCaptureCompleteOperation _operation;
        private readonly NvencRunCaptureCompleteReceipt _receipt;
        private readonly NvencRunCaptureCompleteStatus _status;

        private NvencRunCaptureCompleteAttemptResult(
            INvencRunCaptureCompleter completer,
            NvencRunCaptureCompleteOperation operation,
            NvencRunCaptureCompleteReceipt receipt,
            NvencRunCaptureCompleteStatus status)
        {
            _completer = completer;
            _operation = operation;
            _receipt = receipt;
            _status = status;
        }

        internal INvencRunCaptureCompleter Completer => _completer;

        internal NvencRunCaptureCompleteOperation Operation => _operation;

        internal NvencRunCaptureCompleteReceipt Receipt => _receipt;

        internal NvencRunCaptureCompleteStatus Status => _status;

        internal NvencRunCaptureIndexCommitReceipt CaptureIndexCommitReceipt =>
            _operation.CaptureIndexCommitReceipt;

        internal NvencRunCaptureIndexCommitOperation CaptureIndexCommitOperation =>
            _operation.CaptureIndexCommitOperation;

        internal NvencRunArtifactPublicationReceipt ArtifactPublicationReceipt =>
            _operation.ArtifactPublicationReceipt;

        internal NvencRunArtifactPublicationOperation ArtifactPublicationOperation =>
            _operation.ArtifactPublicationOperation;

        internal CapturePublicationPlan Plan => _operation.Plan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsNone => _status == NvencRunCaptureCompleteStatus.None;

        internal bool IsCompleted =>
            _status == NvencRunCaptureCompleteStatus.Completed
            && _completer != null
            && _operation != null
            && _receipt != null
            && _receipt.IsIssuedFor(_completer, _operation);

        internal bool IsFailed =>
            _status == NvencRunCaptureCompleteStatus.Failed
            && _completer != null
            && _operation != null
            && _operation.IsValid
            && _receipt == null;

        internal bool IsValid => IsCompleted || IsFailed;

        internal bool IsIssuedFor(
            INvencRunCaptureCompleter completer,
            NvencRunCaptureCompleteOperation operation)
        {
            return completer != null
                && operation != null
                && ReferenceEquals(_completer, completer)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        internal static NvencRunCaptureCompleteAttemptResult Completed(
            INvencRunCaptureCompleter completer,
            NvencRunCaptureCompleteOperation operation)
        {
            if (completer == null)
            {
                throw new ArgumentNullException(nameof(completer));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            NvencRunCaptureCompleteReceipt receipt =
                NvencRunCaptureCompleteReceipt.Create(completer, operation);

            return new NvencRunCaptureCompleteAttemptResult(
                completer, operation, receipt, NvencRunCaptureCompleteStatus.Completed);
        }

        internal static NvencRunCaptureCompleteAttemptResult Failed(
            INvencRunCaptureCompleter completer,
            NvencRunCaptureCompleteOperation operation)
        {
            if (completer == null)
            {
                throw new ArgumentNullException(nameof(completer));
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

            return new NvencRunCaptureCompleteAttemptResult(
                completer, operation, null, NvencRunCaptureCompleteStatus.Failed);
        }
    }
}
