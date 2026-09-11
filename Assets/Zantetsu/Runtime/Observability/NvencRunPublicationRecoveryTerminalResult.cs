using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Exclusive sum type carrying one already-issued Phase 0.11 NVENC recovery
    /// release receipt as a single fixed-capacity terminal value: the
    /// CaptureComplete release, the incomplete/orphan cleanup release, or the
    /// stopping release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is not an authority on anything. Each of the three receipts is
    /// its own authority and was minted by the boundary that performed that
    /// release; all this does is carry exactly one of them, so a caller with
    /// three differently shaped outcomes has one value to hold. There is no new
    /// status, no nested result wrapper, no issuer, proof, token, nonce,
    /// generation, or latch.
    /// </para>
    /// <para>
    /// Exactly one of the three references is non-<c>null</c> in a valid
    /// value, and the uninitialized default carries none of them: it is
    /// invalid, all three path predicates are false, and the forwarded graph is
    /// null or zero.
    /// </para>
    /// <para>
    /// Because every one of those receipts attests a completed release, the
    /// upstream decision and operation are no longer admissible by the time one
    /// exists. <see cref="IsValid"/> therefore asks only that exactly one
    /// receipt is held and that this receipt is itself valid, and never
    /// re-requires an upstream validity a release has ended. The converse also
    /// holds: a released lease is no evidence on its own, so a value with no
    /// receipt is never valid.
    /// </para>
    /// <para>
    /// The graph the three paths share is forwarded from the held receipt
    /// rather than copied into fields of its own. What is specific to one path
    /// - the cleanup result and its status, the Capture Index commit receipt,
    /// the stopping disposition - stays reachable through that receipt and is
    /// deliberately not re-exposed here.
    /// </para>
    /// </remarks>
    internal readonly struct NvencRunPublicationRecoveryTerminalResult
    {
        private readonly NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt
            _captureCompleteRelease;

        private readonly NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt
            _incompleteRelease;

        private readonly NvencRunPublicationRecoveryStopOwnershipReleaseReceipt _stopRelease;

        private NvencRunPublicationRecoveryTerminalResult(
            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt captureCompleteRelease,
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt incompleteRelease,
            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt stopRelease)
        {
            _captureCompleteRelease = captureCompleteRelease;
            _incompleteRelease = incompleteRelease;
            _stopRelease = stopRelease;
        }

        /// <summary>
        /// A Run that was recovered, completed, cleaned up, and released.
        /// </summary>
        internal static NvencRunPublicationRecoveryTerminalResult CaptureCompleted(
            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt)
        {
            RequireIssued(receipt, receipt?.IsValid ?? false);

            return new NvencRunPublicationRecoveryTerminalResult(receipt, null, null);
        }

        /// <summary>
        /// A Run whose orphan cleanup finished - cleaned or failed - and whose
        /// lease was then released.
        /// </summary>
        internal static NvencRunPublicationRecoveryTerminalResult IncompleteReleased(
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt)
        {
            RequireIssued(receipt, receipt?.IsValid ?? false);

            return new NvencRunPublicationRecoveryTerminalResult(null, receipt, null);
        }

        /// <summary>
        /// A Run whose recovery stopped without changing a file - a collision
        /// or a deferred verification - and whose lease was released.
        /// </summary>
        internal static NvencRunPublicationRecoveryTerminalResult Stopped(
            NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt)
        {
            RequireIssued(receipt, receipt?.IsValid ?? false);

            return new NvencRunPublicationRecoveryTerminalResult(null, null, receipt);
        }

        internal NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt CaptureCompleteRelease =>
            _captureCompleteRelease;

        internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt IncompleteRelease =>
            _incompleteRelease;

        internal NvencRunPublicationRecoveryStopOwnershipReleaseReceipt StopRelease =>
            _stopRelease;

        internal bool IsCaptureCompleted => _captureCompleteRelease != null && IsValid;

        internal bool IsIncompleteReleased => _incompleteRelease != null && IsValid;

        internal bool IsStopped => _stopRelease != null && IsValid;

        /// <summary>
        /// Exactly one receipt is held, and that receipt is itself valid. No
        /// upstream validity is re-required, and no lease state stands in for a
        /// missing receipt.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    if (_captureCompleteRelease != null)
                    {
                        return _incompleteRelease == null
                            && _stopRelease == null
                            && _captureCompleteRelease.IsValid;
                    }

                    if (_incompleteRelease != null)
                    {
                        return _stopRelease == null && _incompleteRelease.IsValid;
                    }

                    return _stopRelease != null && _stopRelease.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The publication recovery classification every path started from,
        /// read through the held receipt.
        /// </summary>
        internal NvencRunPublicationRecoveryDecision PublicationRecoveryDecision
        {
            get
            {
                if (_captureCompleteRelease != null)
                {
                    return _captureCompleteRelease.CleanupOperation?.PublicationRecoveryDecision;
                }

                if (_incompleteRelease != null)
                {
                    return _incompleteRelease.Decision;
                }

                return _stopRelease?.Decision;
            }
        }

        internal NvencRunPublicationRecoveryInspectionSnapshot Snapshot =>
            PublicationRecoveryDecision?.Snapshot;

        internal CaptureRunInitializationOpenOutcome OpenOutcome
        {
            get
            {
                if (_captureCompleteRelease != null)
                {
                    return _captureCompleteRelease.OpenOutcome;
                }

                if (_incompleteRelease != null)
                {
                    return _incompleteRelease.OpenOutcome;
                }

                return _stopRelease?.OpenOutcome;
            }
        }

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease
        {
            get
            {
                if (_captureCompleteRelease != null)
                {
                    return _captureCompleteRelease.OwnershipLease;
                }

                if (_incompleteRelease != null)
                {
                    return _incompleteRelease.OwnershipLease;
                }

                return _stopRelease?.OwnershipLease;
            }
        }

        internal CaptureRunRootLayout RootLayout
        {
            get
            {
                if (_captureCompleteRelease != null)
                {
                    return _captureCompleteRelease.RootLayout;
                }

                if (_incompleteRelease != null)
                {
                    return _incompleteRelease.RootLayout;
                }

                return _stopRelease?.RootLayout;
            }
        }

        internal long TestRunId
        {
            get
            {
                if (_captureCompleteRelease != null)
                {
                    return _captureCompleteRelease.TestRunId;
                }

                if (_incompleteRelease != null)
                {
                    return _incompleteRelease.TestRunId;
                }

                return _stopRelease != null ? _stopRelease.TestRunId : 0L;
            }
        }

        internal string RunInitializationId
        {
            get
            {
                if (_captureCompleteRelease != null)
                {
                    return _captureCompleteRelease.RunInitializationId;
                }

                if (_incompleteRelease != null)
                {
                    return _incompleteRelease.RunInitializationId;
                }

                return _stopRelease?.RunInitializationId;
            }
        }

        /// <summary>
        /// The whole admission of a factory: the receipt exists and is
        /// currently valid. Its graph is its own to vouch for and is not
        /// re-checked here.
        /// </summary>
        private static void RequireIssued(object receipt, bool isValid)
        {
            // Every factory names its one argument "receipt", so the rejected
            // parameter is the same in all three.
            if (receipt == null)
            {
                throw new ArgumentNullException("receipt");
            }

            if (!isValid)
            {
                throw new ArgumentException("Release receipt must be valid.", "receipt");
            }
        }
    }
}
