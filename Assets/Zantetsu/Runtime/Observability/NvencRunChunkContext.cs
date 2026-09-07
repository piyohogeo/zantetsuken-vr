using System;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Phase 0.11 Run chunk context: the per-Run single-chunk boundary that
    /// records the accepted Capture Frame Id sequence in a fixed array and
    /// drives the exclusive, exactly-once terminal transition
    /// <c>Open -&gt; Finalized</c> or <c>Open -&gt; Abandoned</c>. It binds the
    /// exact session, sink, and finalization coordinator, and holds the
    /// finalized <see cref="NvencChunkFinalizationResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type owns no thread, queue, native resource, OS lock, Registry, or
    /// Publication duty, and is not an <see cref="IDisposable"/>. The ownership
    /// lease is never transferred to, exposed by, or disposed by this type.
    /// </para>
    /// <para>
    /// Finalization contacts the exact coordinator exactly once, after the
    /// accepted sequence is verified to match the sink's finalization evidence
    /// in count and order. No retry, file re-read, rename retry, or result
    /// fabrication exists; exceptions propagate for the Run coordinator to
    /// classify.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunChunkContext
    {
        private readonly CaptureRunInitializationSession _session;
        private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;
        private readonly NvencRunChunkSink _sink;
        private readonly NvencRunChunkFinalizationCoordinator _finalizationCoordinator;
        private readonly string _artifactId;
        private readonly long[] _acceptedFrameIds;

        private int _acceptedCount;
        private NvencRunChunkContextState _state;
        private NvencChunkFinalizationResult _finalizationResult;
        private bool _finalizeAttempted;

        internal NvencRunChunkContext(
            CaptureRunInitializationSessionIssue issue,
            NvencRunChunkSink sink,
            NvencRunChunkFinalizationCoordinator finalizationCoordinator,
            string artifactId)
        {
            if (issue == null)
            {
                throw new ArgumentNullException(nameof(issue));
            }

            if (!issue.IsValid)
            {
                throw new ArgumentException("Session issue must be valid.", nameof(issue));
            }

            CaptureRunInitializationSession session = issue.Session;
            if (session == null || !session.IsValid)
            {
                throw new ArgumentException("Session issue must hold a valid session.", nameof(issue));
            }

            if (session.RootLayout == null)
            {
                throw new ArgumentException("Session must hold a root layout.", nameof(issue));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = issue.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Session issue must hold valid lock identity evidence.", nameof(issue));
            }

            if (!ReferenceEquals(session.RootLayout, lockIdentityEvidence.RootLayout))
            {
                throw new ArgumentException(
                    "Session and lock identity evidence must share the same root layout.", nameof(issue));
            }

            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (finalizationCoordinator == null)
            {
                throw new ArgumentNullException(nameof(finalizationCoordinator));
            }

            RequireArtifactId(artifactId);

            _session = session;
            _lockIdentityEvidence = lockIdentityEvidence;
            _sink = sink;
            _finalizationCoordinator = finalizationCoordinator;
            _artifactId = artifactId;
            _acceptedFrameIds = new long[NvencBringUpProfileV1.CadenceTickCount];
            _state = NvencRunChunkContextState.Open;
        }

        internal NvencRunChunkContextState State => _state;

        internal long TestRunId => _session.TestRunId;

        internal string RunInitializationId => _session.RunInitializationId;

        internal CaptureRunRootLayout RootLayout => _session.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        internal int AcceptedFrameCount => _acceptedCount;

        internal NvencRunChunkSink Sink => _sink;

        /// <summary>
        /// Single allocation-free registration entry, called from the
        /// acceptance linearization point. Records a positive, strictly
        /// increasing Capture Frame Id into the pre-allocated accepted array,
        /// in accepted order, while Open. A duplicate, a backward id, a
        /// non-positive id, an over-capacity id, or a non-Open state is
        /// rejected atomically without partially updating the count or array.
        /// </summary>
        internal bool TryRecordAcceptedFrame(long captureFrameId)
        {
            if (_state != NvencRunChunkContextState.Open)
            {
                return false;
            }

            if (captureFrameId <= 0)
            {
                return false;
            }

            if (_acceptedCount >= NvencBringUpProfileV1.CadenceTickCount)
            {
                return false;
            }

            if (_acceptedCount > 0 && captureFrameId <= _acceptedFrameIds[_acceptedCount - 1])
            {
                return false;
            }

            _acceptedFrameIds[_acceptedCount] = captureFrameId;
            _acceptedCount++;
            return true;
        }

        /// <summary>
        /// Range-checked read of one accepted Capture Frame Id, in accepted
        /// order. The index must be in <c>[0, AcceptedFrameCount)</c>.
        /// </summary>
        internal bool TryGetAcceptedFrameId(int index, out long captureFrameId)
        {
            captureFrameId = 0;

            if (index < 0 || index >= _acceptedCount)
            {
                return false;
            }

            captureFrameId = _acceptedFrameIds[index];
            return true;
        }

        /// <summary>
        /// Finalizes the chunk exactly once. The accepted sequence must match
        /// the sink's finalization evidence in count and order before the
        /// finalization coordinator is contacted. Only after the whole result
        /// is fixed are <see cref="NvencRunChunkContextState.Finalized"/> and
        /// the result published in the same synchronous boundary. A mismatch,
        /// an empty accepted sequence, or a repeated call is rejected before
        /// any finalizer contact; a finalizer exception propagates unchanged.
        /// </summary>
        internal bool TryFinalize(out NvencChunkFinalizationResult result)
        {
            result = null;

            if (_state != NvencRunChunkContextState.Open)
            {
                return false;
            }

            if (_acceptedCount < 1)
            {
                return false;
            }

            if (_finalizeAttempted)
            {
                return false;
            }

            _finalizeAttempted = true;

            if (!_sink.TryCaptureFinalizationEvidence(_acceptedCount, out NvencRunChunkSinkFinalizationEvidence evidence))
            {
                return false;
            }

            CaptureArtifactFrameRelation relation = evidence.FrameRelation;
            if (relation == null || (long)relation.Count != _acceptedCount)
            {
                return false;
            }

            for (int i = 0; i < _acceptedCount; i++)
            {
                if (relation.GetCaptureFrameId(i) != _acceptedFrameIds[i])
                {
                    return false;
                }
            }

            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperationFactory.Build(_sink, evidence, _artifactId);

            NvencChunkFinalizationResult finalizationResult =
                _finalizationCoordinator.Execute(operation);

            if (!MatchesFinalizationResult(finalizationResult, operation, relation))
            {
                return false;
            }

            _finalizationResult = finalizationResult;
            _state = NvencRunChunkContextState.Finalized;
            result = finalizationResult;
            return true;
        }

        /// <summary>
        /// Abandons the chunk exactly once from <c>Open</c>. No result,
        /// descriptor, or frame relation is issued, and no file delete,
        /// truncate, rename, or lease release happens. A repeated request, or
        /// a request from a terminal state, returns false without side effect.
        /// </summary>
        internal bool TryAbandon()
        {
            if (_state != NvencRunChunkContextState.Open)
            {
                return false;
            }

            _state = NvencRunChunkContextState.Abandoned;
            return true;
        }

        /// <summary>
        /// Publishes the finalized result only while the context is
        /// <see cref="NvencRunChunkContextState.Finalized"/> and the held
        /// result is still valid and correlated to this context's exact sink
        /// and artifact id. A swapped or nulled result fails closed.
        /// </summary>
        internal bool TryGetFinalizationResult(out NvencChunkFinalizationResult result)
        {
            NvencChunkFinalizationResult held = _finalizationResult;

            if (_state == NvencRunChunkContextState.Finalized &&
                held != null &&
                held.IsValid &&
                ReferenceEquals(held.Sink, _sink) &&
                string.Equals(held.ArtifactId, _artifactId, StringComparison.Ordinal))
            {
                result = held;
                return true;
            }

            result = null;
            return false;
        }

        private bool MatchesFinalizationResult(
            NvencChunkFinalizationResult finalizationResult,
            NvencRunChunkFinalizationOperation operation,
            CaptureArtifactFrameRelation relation)
        {
            if (finalizationResult == null || !finalizationResult.IsValid)
            {
                return false;
            }

            if (!ReferenceEquals(finalizationResult.Sink, _sink) ||
                !ReferenceEquals(finalizationResult.Operation, operation))
            {
                return false;
            }

            CaptureArtifactDescriptor descriptor = finalizationResult.Descriptor;
            if (descriptor == null || !descriptor.IsValid)
            {
                return false;
            }

            if (!string.Equals(descriptor.ArtifactId, _artifactId, StringComparison.Ordinal) ||
                descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence ||
                descriptor.ByteLength != operation.AccumulatedByteLength)
            {
                return false;
            }

            return ReferenceEquals(finalizationResult.FrameRelation, relation);
        }

        private static void RequireArtifactId(string artifactId)
        {
            if (artifactId == null)
            {
                throw new ArgumentNullException(nameof(artifactId));
            }

            if (artifactId.Length == 0 || Encoding.UTF8.GetByteCount(artifactId) > 512)
            {
                throw new ArgumentException(
                    "Artifact ID must be non-empty and at most 512 UTF-8 bytes.", nameof(artifactId));
            }
        }
    }
}
