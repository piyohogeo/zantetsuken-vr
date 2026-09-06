using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free terminal result of one
    /// <see cref="NvencSubmittedOutputCollector"/> attempt. A readonly value
    /// type with three mutually exclusive shapes: Succeeded (an exact work
    /// token plus a valid owned Access Unit lease), ControlledFailure (an exact
    /// work token with no owned lease), and None (the default, uninitialized).
    /// </summary>
    internal readonly struct NvencSubmittedOutputCollectResult
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencOwnedAccessUnitLease _ownedLease;
        private readonly bool _succeeded;

        private NvencSubmittedOutputCollectResult(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease,
            bool succeeded)
        {
            _workToken = workToken;
            _ownedLease = ownedLease;
            _succeeded = succeeded;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencOwnedAccessUnitLease OwnedLease => _ownedLease;

        internal bool IsSucceeded => _succeeded && _workToken.IsValid && _ownedLease.IsValid;

        internal bool IsControlledFailure => !_succeeded && _workToken.IsValid && !_ownedLease.IsValid;

        internal bool IsNone => !_workToken.IsValid;

        internal static NvencSubmittedOutputCollectResult Succeeded(
            in CaptureFrameWorkToken workToken,
            in NvencOwnedAccessUnitLease ownedLease)
        {
            return new NvencSubmittedOutputCollectResult(workToken, ownedLease, true);
        }

        internal static NvencSubmittedOutputCollectResult ControlledFailure(
            in CaptureFrameWorkToken workToken)
        {
            return new NvencSubmittedOutputCollectResult(workToken, default, false);
        }
    }
}
