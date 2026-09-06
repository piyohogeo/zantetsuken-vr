using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free terminal result of one
    /// <see cref="NvencRunChunkSink"/> append attempt. A readonly value type
    /// with three mutually exclusive shapes: Appended (an exact work token and
    /// the appended byte length), ControlledFailure (an exact work token with
    /// no appended length), and None (the default, uninitialized).
    /// </summary>
    internal readonly struct NvencRunChunkSinkResult
    {
        private readonly CaptureFrameWorkToken _workToken;
        private readonly int _appendedByteLength;
        private readonly bool _appended;

        private NvencRunChunkSinkResult(
            in CaptureFrameWorkToken workToken,
            int appendedByteLength,
            bool appended)
        {
            _workToken = workToken;
            _appendedByteLength = appendedByteLength;
            _appended = appended;
        }

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal int AppendedByteLength => _appendedByteLength;

        internal bool IsAppended => _appended && _workToken.IsValid && _appendedByteLength > 0;

        internal bool IsControlledFailure => !_appended && _workToken.IsValid && _appendedByteLength == 0;

        internal bool IsNone => !_workToken.IsValid;

        internal static NvencRunChunkSinkResult Appended(
            in CaptureFrameWorkToken workToken,
            int appendedByteLength)
        {
            return new NvencRunChunkSinkResult(workToken, appendedByteLength, true);
        }

        internal static NvencRunChunkSinkResult ControlledFailure(
            in CaptureFrameWorkToken workToken)
        {
            return new NvencRunChunkSinkResult(workToken, 0, false);
        }
    }
}
