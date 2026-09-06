using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free terminal result of one
    /// <see cref="NvencSubmittedOutputCollector"/> attempt. A readonly value
    /// type with three mutually exclusive shapes: Succeeded (an exact work
    /// token plus a valid owned Access Unit lease and an uninitialized
    /// recovery proof), ControlledFailure (an exact work token with no owned
    /// lease and an initialized recovery proof minted by the exact cancel),
    /// and None (the default, uninitialized). A default or uninitialized
    /// proof fails closed and is never a terminal ControlledFailure shape. The
    /// recovery proof is minted only by the buffer on a successful collector
    /// cancel and cannot be regenerated from external constituent values.
    /// </summary>
    internal readonly struct NvencSubmittedOutputCollectResult
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencOwnedAccessUnitLease _ownedLease;
        private readonly NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof _recoveryProof;
        private readonly bool _succeeded;

        private NvencSubmittedOutputCollectResult(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            in NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof recoveryProof,
            bool succeeded)
        {
            _workToken = workToken;
            _ownedLease = ownedLease;
            _recoveryProof = recoveryProof;
            _succeeded = succeeded;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencOwnedAccessUnitLease OwnedLease => _ownedLease;

        internal NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof RecoveryProof => _recoveryProof;

        internal bool IsSucceeded =>
            _succeeded && _workToken.IsValid && _ownedLease.IsValid && !_recoveryProof.IsInitialized;

        internal bool IsControlledFailure =>
            !_succeeded && _workToken.IsValid && !_ownedLease.IsValid && _recoveryProof.IsInitialized;

        internal bool IsNone => !_workToken.IsValid;

        internal static NvencSubmittedOutputCollectResult Succeeded(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease)
        {
            return new NvencSubmittedOutputCollectResult(workToken, ownedLease, default, true);
        }

        internal static NvencSubmittedOutputCollectResult ControlledFailure(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof recoveryProof)
        {
            return new NvencSubmittedOutputCollectResult(workToken, default, recoveryProof, false);
        }
    }
}
