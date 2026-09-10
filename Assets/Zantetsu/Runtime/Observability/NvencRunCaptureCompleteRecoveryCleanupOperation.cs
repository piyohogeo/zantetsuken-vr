using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable operation carrying one accepted Phase 0.11 NVENC recovery
    /// CaptureComplete into the cleanup boundary that follows it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exact CaptureComplete receipt is the whole state. The CaptureComplete
    /// operation, the Capture Index classification and its snapshot, the
    /// optional commit receipt, the publication recovery decision, the
    /// authoritative plan, the root layout, and the Run identity are all
    /// forwarded from that graph rather than copied, and how the Run arrived -
    /// with an already authoritative final index or with a committed one -
    /// stays visible through <see cref="HasCommitReceipt"/>.
    /// </para>
    /// <para>
    /// What the Capture Index temporary looked like at inspection time remains
    /// exactly where it was observed, on the snapshot; it is neither copied
    /// into a field here nor translated into a cleanup mode. No canonical
    /// bytes, path, hash, handle, file identity, deletion plan, receipt, proof,
    /// token, nonce, or generation is introduced, and nothing here decides what
    /// may be deleted.
    /// </para>
    /// <para>
    /// Issuance goes through the one factory, which requires a valid receipt
    /// still issued for its own completer and operation over a valid
    /// operation; every detailed correlation of decision, snapshot, plan, root
    /// layout, Run identity, and commit authority is that graph's own and is
    /// not restated as a second validator.
    /// </para>
    /// <para>
    /// This type reads and writes no file, deletes nothing, releases no lock,
    /// owns and disposes nothing, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject. <see cref="IsValid"/> recomputes the
    /// same minimal conditions without throwing, so once the OS lock is
    /// released the receipt becomes invalid and this operation follows it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryCleanupOperation
    {
        private readonly NvencRunCaptureCompleteRecoveryReceipt _captureCompleteReceipt;

        private NvencRunCaptureCompleteRecoveryCleanupOperation(
            NvencRunCaptureCompleteRecoveryReceipt captureCompleteReceipt)
        {
            _captureCompleteReceipt = captureCompleteReceipt;
        }

        internal static NvencRunCaptureCompleteRecoveryCleanupOperation Create(
            NvencRunCaptureCompleteRecoveryReceipt receipt)
        {
            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            if (!IsAcceptedCaptureComplete(receipt))
            {
                throw new ArgumentException(
                    "Receipt must be a valid CaptureComplete acceptance of its own operation.",
                    nameof(receipt));
            }

            return new NvencRunCaptureCompleteRecoveryCleanupOperation(receipt);
        }

        internal NvencRunCaptureCompleteRecoveryReceipt CaptureCompleteReceipt =>
            _captureCompleteReceipt;

        internal NvencRunCaptureCompleteRecoveryOperation CaptureCompleteOperation =>
            _captureCompleteReceipt.Operation;

        internal NvencRunCaptureIndexRecoveryDecision CaptureIndexRecoveryDecision =>
            _captureCompleteReceipt.CaptureIndexRecoveryDecision;

        /// <summary>
        /// The Capture Index observation this recovery classified, including
        /// what the temporary was at that time. It is the observation, not a
        /// cleanup instruction.
        /// </summary>
        internal NvencRunCaptureIndexRecoveryInspectionSnapshot CaptureIndexRecoverySnapshot =>
            _captureCompleteReceipt.CaptureIndexRecoverySnapshot;

        internal NvencRunCaptureIndexRecoveryCommitReceipt CaptureIndexRecoveryCommitReceipt =>
            _captureCompleteReceipt.CaptureIndexRecoveryCommitReceipt;

        internal bool HasCommitReceipt => _captureCompleteReceipt.HasCommitReceipt;

        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision =>
            _captureCompleteReceipt.PublicationRecoveryDecision;

        internal CapturePublicationPlan AuthoritativePlan =>
            _captureCompleteReceipt.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _captureCompleteReceipt.RootLayout;

        internal long TestRunId => _captureCompleteReceipt.TestRunId;

        internal string RunInitializationId => _captureCompleteReceipt.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _captureCompleteReceipt != null
                        && IsAcceptedCaptureComplete(_captureCompleteReceipt);
                }
                catch
                {
                    return false;
                }
            }
        }

        private static bool IsAcceptedCaptureComplete(
            NvencRunCaptureCompleteRecoveryReceipt receipt)
        {
            NvencRunCaptureCompleteRecoveryOperation operation = receipt.Operation;

            return receipt.IsValid
                && receipt.IsIssuedFor(receipt.Completer, operation)
                && operation != null
                && operation.IsValid;
        }
    }
}
