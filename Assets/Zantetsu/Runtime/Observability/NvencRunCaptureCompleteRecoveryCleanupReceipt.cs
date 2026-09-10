using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one Phase 0.11 NVENC recovery CaptureComplete
    /// cleanup finished: the exact cleaner that performed it and the exact
    /// operation it performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those two references are the whole state; the CaptureComplete receipt
    /// and operation, the Capture Index classification and its snapshot, the
    /// optional commit receipt, the publication recovery decision, the
    /// authoritative plan, the root layout, and the Run identity are forwarded
    /// from the operation's graph rather than copied.
    /// </para>
    /// <para>
    /// This is process-local evidence of one successful synchronous call. It is
    /// not a filesystem snapshot, not a proof of full durability, and not
    /// evidence that the OS lock was released. It is minted only on success,
    /// through the success-only factory, which requires a cleaner, an
    /// operation, and that the operation is still valid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, touches no file, releases
    /// no lock, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. <see cref="IsValid"/> never throws, so once the OS
    /// lock is released the operation becomes invalid and this receipt follows
    /// it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryCleanupReceipt
    {
        private readonly INvencRunCaptureCompleteRecoveryCleaner _cleaner;
        private readonly NvencRunCaptureCompleteRecoveryCleanupOperation _operation;

        private NvencRunCaptureCompleteRecoveryCleanupReceipt(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            _cleaner = cleaner;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one finished cleanup. There is no failure
        /// receipt: a cleanup that did not finish is reported as Failed with no
        /// receipt at all.
        /// </summary>
        internal static NvencRunCaptureCompleteRecoveryCleanupReceipt Cleaned(
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

            return new NvencRunCaptureCompleteRecoveryCleanupReceipt(cleaner, operation);
        }

        internal INvencRunCaptureCompleteRecoveryCleaner Cleaner => _cleaner;

        internal NvencRunCaptureCompleteRecoveryCleanupOperation Operation => _operation;

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

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _cleaner != null && _operation != null && _operation.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether this receipt is the evidence of that exact cleaner
        /// performing that exact operation, and is still valid.
        /// </summary>
        internal bool IsIssuedFor(
            INvencRunCaptureCompleteRecoveryCleaner cleaner,
            NvencRunCaptureCompleteRecoveryCleanupOperation operation)
        {
            try
            {
                return ReferenceEquals(_cleaner, cleaner)
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
