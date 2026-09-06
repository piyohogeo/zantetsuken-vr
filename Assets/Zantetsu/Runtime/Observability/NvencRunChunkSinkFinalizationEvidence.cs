using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable finalization snapshot of one
    /// <see cref="NvencRunChunkSink"/> after its appended frames complete. It
    /// binds the exact sink, the appended count, the accumulated byte length,
    /// the last Capture Frame Id, and a copied
    /// <see cref="CaptureArtifactFrameRelation"/> of the appended frame ids. It
    /// holds no writer, buffer, lease, stream, hash state, or process state,
    /// performs no file operation, and is not an <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="IsIssuedFor"/> re-checks the exact sink reference, the
    /// current finalization admissibility, and the current count, length, last
    /// id, and frame-id sequence, so evidence goes stale as soon as the sink
    /// appends further, is poisoned, or is run-abandoned.
    /// </remarks>
    internal sealed class NvencRunChunkSinkFinalizationEvidence
    {
        private readonly NvencRunChunkSink _sink;
        private readonly long _appendedCount;
        private readonly long _accumulatedByteLength;
        private readonly long _lastFrameId;
        private readonly CaptureArtifactFrameRelation _frameRelation;

        private NvencRunChunkSinkFinalizationEvidence(
            NvencRunChunkSink sink,
            long appendedCount,
            long accumulatedByteLength,
            long lastFrameId,
            CaptureArtifactFrameRelation frameRelation)
        {
            _sink = sink;
            _appendedCount = appendedCount;
            _accumulatedByteLength = accumulatedByteLength;
            _lastFrameId = lastFrameId;
            _frameRelation = frameRelation;
        }

        internal static NvencRunChunkSinkFinalizationEvidence Create(
            NvencRunChunkSink sink,
            long appendedCount,
            long accumulatedByteLength,
            long lastFrameId,
            CaptureArtifactFrameRelation frameRelation)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (frameRelation == null)
            {
                throw new ArgumentNullException(nameof(frameRelation));
            }

            if (!sink.IsFinalizationAdmissible())
            {
                throw new InvalidOperationException(
                    "The sink does not currently admit finalization.");
            }

            if (appendedCount <= 0 || appendedCount > NvencBringUpProfileV1.CadenceTickCount)
            {
                throw new ArgumentOutOfRangeException(nameof(appendedCount));
            }

            if (accumulatedByteLength <= 0 || accumulatedByteLength > NvencBringUpProfileV1.MaxChunkByteLength)
            {
                throw new ArgumentOutOfRangeException(nameof(accumulatedByteLength));
            }

            if (lastFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lastFrameId));
            }

            if ((long)frameRelation.Count != appendedCount)
            {
                throw new ArgumentException(
                    "Frame relation count must equal the appended count.", nameof(frameRelation));
            }

            if (lastFrameId != frameRelation.GetCaptureFrameId((int)appendedCount - 1))
            {
                throw new ArgumentException(
                    "Last frame id must equal the final frame relation element.", nameof(frameRelation));
            }

            return new NvencRunChunkSinkFinalizationEvidence(
                sink, appendedCount, accumulatedByteLength, lastFrameId, frameRelation);
        }

        internal long AppendedCount => _appendedCount;

        internal long AccumulatedByteLength => _accumulatedByteLength;

        internal long LastFrameId => _lastFrameId;

        internal CaptureArtifactFrameRelation FrameRelation => _frameRelation;

        internal bool IsIssuedFor(NvencRunChunkSink sink)
        {
            return sink != null &&
                ReferenceEquals(_sink, sink) &&
                sink.IsFinalizationAdmissible() &&
                _appendedCount == sink.AppendedCount &&
                _accumulatedByteLength == sink.AccumulatedByteLength &&
                _lastFrameId == sink.LastFrameId &&
                sink.MatchesFrameRelation(_frameRelation);
        }
    }
}
