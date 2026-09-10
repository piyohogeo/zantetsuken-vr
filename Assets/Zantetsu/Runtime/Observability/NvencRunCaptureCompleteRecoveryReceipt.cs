using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one Phase 0.11 NVENC recovery CaptureComplete
    /// call succeeded: the exact completer that accepted it and the exact
    /// operation it accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those two references are the whole state. The decision, snapshot,
    /// optional commit receipt, publication recovery decision, authoritative
    /// plan, root layout, and Run identity are forwarded from the operation's
    /// graph rather than copied, and which of the two authorities the Run
    /// arrived by stays visible through
    /// <see cref="HasCommitReceipt"/> rather than being flattened away.
    /// </para>
    /// <para>
    /// This is not a durable filesystem proof, not evidence that any cleanup
    /// ran, and not evidence that the OS lock was released. It says only that a
    /// synchronous call made under the still-held lock returned successfully.
    /// It is minted only on success, through the success-only factory, which
    /// requires a completer, an operation, and that the operation is still
    /// valid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, touches no file, releases
    /// no lock, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. <see cref="IsValid"/> never throws, so once the OS
    /// lock is released the operation becomes invalid and this receipt follows
    /// it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryReceipt
    {
        private readonly INvencRunCaptureCompleteRecoveryCompleter _completer;
        private readonly NvencRunCaptureCompleteRecoveryOperation _operation;

        private NvencRunCaptureCompleteRecoveryReceipt(
            INvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            _completer = completer;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one accepted CaptureComplete. There is no
        /// failure or unknown-outcome receipt: a call that did not succeed
        /// leaves by an exception.
        /// </summary>
        internal static NvencRunCaptureCompleteRecoveryReceipt Completed(
            INvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
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
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            return new NvencRunCaptureCompleteRecoveryReceipt(completer, operation);
        }

        internal INvencRunCaptureCompleteRecoveryCompleter Completer => _completer;

        internal NvencRunCaptureCompleteRecoveryOperation Operation => _operation;

        internal NvencRunCaptureIndexRecoveryDecision CaptureIndexRecoveryDecision =>
            _operation.CaptureIndexRecoveryDecision;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot CaptureIndexRecoverySnapshot =>
            _operation.CaptureIndexRecoverySnapshot;

        /// <summary>
        /// The recovery commit receipt, present only when the Run reached an
        /// authoritative Capture Index by committing one.
        /// </summary>
        internal NvencRunCaptureIndexRecoveryCommitReceipt CaptureIndexRecoveryCommitReceipt =>
            _operation.CaptureIndexRecoveryCommitReceipt;

        internal bool HasCommitReceipt => _operation.HasCommitReceipt;

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            _operation.PublicationRecoveryDecision;

        internal CapturePublicationPlan AuthoritativePlan => _operation.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _completer != null && _operation != null && _operation.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether this receipt is the evidence of that exact completer
        /// accepting that exact operation, and is still valid.
        /// </summary>
        internal bool IsIssuedFor(
            INvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            try
            {
                return ReferenceEquals(_completer, completer)
                    && ReferenceEquals(_operation, operation)
                    && IsValid;
            }
            catch
            {
                return false;
            }
        }
    }
}
