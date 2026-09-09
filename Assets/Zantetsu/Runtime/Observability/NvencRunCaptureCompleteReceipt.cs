using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run CaptureComplete receipt: the exact
    /// completer and the exact operation whose CaptureComplete is known to have
    /// finished in one synchronous call. It is issued only from the
    /// <see cref="NvencRunCaptureCompleteAttemptResult.Completed"/> path and has
    /// no public constructor.
    /// </summary>
    /// <remarks>
    /// This type holds only the exact completer and the exact operation,
    /// performs no filesystem, hash, or serialization work, and is not an
    /// <see cref="IDisposable"/>. The capture index and artifact publication
    /// receipts and operations, the plan, the root layout, and the run identity
    /// are forwarded from the operation and never duplicated as fields, and no
    /// new proof, token, nonce, file snapshot, or hash copy is introduced. A
    /// receipt is process-local evidence that one synchronous CaptureComplete
    /// call succeeded; it is not a persistent filesystem snapshot.
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteReceipt
    {
        private readonly INvencRunCaptureCompleter _completer;
        private readonly NvencRunCaptureCompleteOperation _operation;

        private NvencRunCaptureCompleteReceipt(
            INvencRunCaptureCompleter completer,
            NvencRunCaptureCompleteOperation operation)
        {
            _completer = completer;
            _operation = operation;
        }

        internal static NvencRunCaptureCompleteReceipt Create(
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

            return new NvencRunCaptureCompleteReceipt(completer, operation);
        }

        internal INvencRunCaptureCompleter Completer => _completer;

        internal NvencRunCaptureCompleteOperation Operation => _operation;

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

        internal bool IsValid =>
            _completer != null
            && _operation != null
            && _operation.IsBindingIntact;

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
    }
}
