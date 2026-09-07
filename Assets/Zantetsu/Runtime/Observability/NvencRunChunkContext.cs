using System;
using System.Text;
using System.Threading;

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
    /// The exact sink and coordinator are bound at construction to the same
    /// writer that implements both the chunk appender and finalizer.
    /// Finalization contacts the coordinator exactly once, after the accepted
    /// sequence is verified to match the sink's finalization evidence in count
    /// and order. No retry, file re-read, rename retry, or result fabrication
    /// exists.
    /// </para>
    /// <para>
    /// The terminal transition — finalize claim, abandon, and result
    /// publication — is serialized on a private gate. The finalized result is
    /// written before the <c>Finalized</c> state is published with release
    /// semantics; readers observe the state with acquire semantics. A
    /// post-finalization correlation failure is a fatal invariant, and a
    /// finalizer exception propagates for the Run coordinator to classify.
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
        private readonly object _terminalGate;

        private int _acceptedCount;
        private int _state;
        private NvencChunkFinalizationResult _finalizationResult;
        private bool _finalizeClaimed;

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

            // The sink and the finalizer must be the same writer: the exact
            // finalizer is also the chunk appender the sink is backed by.
            INvencRunChunkFinalizer finalizer = finalizationCoordinator.Finalizer;
            if (!(finalizer is INvencRunChunkAppender appender))
            {
                throw new ArgumentException(
                    "Finalization coordinator finalizer must implement the chunk appender.", nameof(finalizationCoordinator));
            }

            if (!sink.IsBackedBy(appender))
            {
                throw new ArgumentException(
                    "Sink must be backed by the exact finalizer writer.", nameof(sink));
            }

            RequireArtifactId(artifactId);

            _session = session;
            _lockIdentityEvidence = lockIdentityEvidence;
            _sink = sink;
            _finalizationCoordinator = finalizationCoordinator;
            _artifactId = artifactId;
            _acceptedFrameIds = new long[NvencBringUpProfileV1.CadenceTickCount];
            _terminalGate = new object();
            _state = (int)NvencRunChunkContextState.Open;
        }

        internal NvencRunChunkContextState State => (NvencRunChunkContextState)Volatile.Read(ref _state);

        internal long TestRunId => _session.TestRunId;

        internal string RunInitializationId => _session.RunInitializationId;

        internal CaptureRunRootLayout RootLayout => _session.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _lockIdentityEvidence;

        internal int AcceptedFrameCount => _acceptedCount;

        internal NvencRunChunkSink Sink => _sink;

        /// <summary>
        /// Single allocation-free registration entry, called from the
        /// acceptance linearization point. It records a positive, strictly
        /// increasing Capture Frame Id into the pre-allocated accepted array,
        /// in accepted order, serialized on the terminal gate. A duplicate, a
        /// backward id, a non-positive id, an over-capacity id, a claimed
        /// finalize, or a non-Open state is rejected atomically without
        /// partially updating the count or array.
        /// </summary>
        internal bool TryRecordAcceptedFrame(long captureFrameId)
        {
            lock (_terminalGate)
            {
                if (_finalizeClaimed ||
                    (NvencRunChunkContextState)Volatile.Read(ref _state) != NvencRunChunkContextState.Open)
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
        /// Finalizes the chunk exactly once. The accepted-sequence snapshot and
        /// match, and the exactly-once claim, are linearized on the terminal
        /// gate before the finalizer is contacted; a false there releases the
        /// gate without claiming, so it stays retryable. The finalizer I/O
        /// runs outside the gate exactly once. Only after the whole result is
        /// fixed are <see cref="NvencRunChunkContextState.Finalized"/> and the
        /// result published; the result is written first and the state is
        /// published with release semantics. A post-finalization correlation
        /// failure is a fatal invariant, and a finalizer exception propagates
        /// unchanged.
        /// </summary>
        internal bool TryFinalize(out NvencChunkFinalizationResult result)
        {
            result = null;

            NvencRunChunkFinalizationOperation operation = null;
            CaptureArtifactFrameRelation relation = null;

            // Pre-side-effect verification and the exactly-once claim are
            // serialized on the terminal gate, so an accepted frame cannot be
            // added between the snapshot verification and the claim.
            lock (_terminalGate)
            {
                if (_finalizeClaimed ||
                    (NvencRunChunkContextState)Volatile.Read(ref _state) != NvencRunChunkContextState.Open)
                {
                    return false;
                }

                if (_acceptedCount < 1)
                {
                    return false;
                }

                if (!_sink.TryCaptureFinalizationEvidence(_acceptedCount, out NvencRunChunkSinkFinalizationEvidence evidence))
                {
                    return false;
                }

                relation = evidence.FrameRelation;
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

                operation = NvencRunChunkFinalizationOperationFactory.Build(_sink, evidence, _artifactId);

                _finalizeClaimed = true;
            }

            // The finalizer I/O runs outside the gate, exactly once; an
            // exception propagates unchanged for the Run coordinator to
            // classify.
            NvencChunkFinalizationResult finalizationResult =
                _finalizationCoordinator.Execute(operation);

            // After the finalizer has succeeded (close and rename are
            // known-success in the real writer), a null, foreign, or corrupted
            // result is a fatal invariant, never a controllable false that
            // could route the chunk into Abandon.
            RequireFinalizationResultCorrelation(finalizationResult, operation, relation);

            lock (_terminalGate)
            {
                _finalizationResult = finalizationResult;
                Volatile.Write(ref _state, (int)NvencRunChunkContextState.Finalized);
            }

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
            lock (_terminalGate)
            {
                if (_finalizeClaimed || (NvencRunChunkContextState)Volatile.Read(ref _state) != NvencRunChunkContextState.Open)
                {
                    return false;
                }

                Volatile.Write(ref _state, (int)NvencRunChunkContextState.Abandoned);
                return true;
            }
        }

        /// <summary>
        /// Publishes the finalized result only while the context is
        /// <see cref="NvencRunChunkContextState.Finalized"/> and the held
        /// result is still valid and correlated to this context's exact sink
        /// and artifact id. A swapped or nulled result fails closed.
        /// </summary>
        internal bool TryGetFinalizationResult(out NvencChunkFinalizationResult result)
        {
            // Acquire the terminal publication before reading the result.
            if ((NvencRunChunkContextState)Volatile.Read(ref _state) != NvencRunChunkContextState.Finalized)
            {
                result = null;
                return false;
            }

            NvencChunkFinalizationResult held = _finalizationResult;

            if (held != null &&
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

        private void RequireFinalizationResultCorrelation(
            NvencChunkFinalizationResult finalizationResult,
            NvencRunChunkFinalizationOperation operation,
            CaptureArtifactFrameRelation relation)
        {
            if (finalizationResult == null || !finalizationResult.IsValid ||
                !ReferenceEquals(finalizationResult.Sink, _sink) ||
                !ReferenceEquals(finalizationResult.Operation, operation) ||
                finalizationResult.Descriptor == null ||
                !finalizationResult.Descriptor.IsValid ||
                !string.Equals(finalizationResult.Descriptor.ArtifactId, _artifactId, StringComparison.Ordinal) ||
                finalizationResult.Descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence ||
                finalizationResult.Descriptor.ByteLength != operation.AccumulatedByteLength ||
                !ReferenceEquals(finalizationResult.FrameRelation, relation))
            {
                throw new InvalidOperationException(
                    "Finalization result does not correlate to the finalized chunk operation.");
            }
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
