using System;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Phase 0.11 Run chunk finalization request: the exact sink and
    /// its finalization evidence bound to a caller-supplied artifact id. It is
    /// the input contract for the future filesystem finalizer. It owns no
    /// writer, buffer, lease, stream, hash state, or descriptor, performs no
    /// file, hash, thread, or task operation, and is not an
    /// <see cref="IDisposable"/>.
    /// </summary>
    /// <remarks>
    /// The only normal issue path is <see cref="Create"/>. <see cref="IsValid"/>
    /// re-checks the current
    /// <see cref="NvencRunChunkSinkFinalizationEvidence.IsIssuedFor"/> relation
    /// and the held values without throwing, so the operation goes invalid as
    /// soon as the sink appends further, is poisoned, or is run-abandoned.
    /// </remarks>
    internal sealed class NvencRunChunkFinalizationOperation
    {
        private readonly NvencRunChunkSink _sink;
        private readonly NvencRunChunkSinkFinalizationEvidence _evidence;
        private readonly string _artifactId;

        private NvencRunChunkFinalizationOperation(
            NvencRunChunkSink sink,
            NvencRunChunkSinkFinalizationEvidence evidence,
            string artifactId)
        {
            _sink = sink;
            _evidence = evidence;
            _artifactId = artifactId;
        }

        internal static NvencRunChunkFinalizationOperation Create(
            NvencRunChunkSink sink,
            NvencRunChunkSinkFinalizationEvidence evidence,
            string artifactId)
        {
            if (sink == null)
            {
                throw new ArgumentNullException(nameof(sink));
            }

            if (evidence == null)
            {
                throw new ArgumentNullException(nameof(evidence));
            }

            if (!evidence.IsIssuedFor(sink))
            {
                throw new ArgumentException(
                    "Evidence must have been issued for the sink.", nameof(evidence));
            }

            RequireArtifactId(artifactId);

            long count = evidence.AppendedCount;
            if (count < 1 || count > NvencBringUpProfileV1.CadenceTickCount)
            {
                throw new ArgumentOutOfRangeException(nameof(evidence));
            }

            long length = evidence.AccumulatedByteLength;
            if (length < 1 || length > NvencBringUpProfileV1.MaxChunkByteLength)
            {
                throw new ArgumentOutOfRangeException(nameof(evidence));
            }

            if (evidence.LastFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(evidence));
            }

            CaptureArtifactFrameRelation relation = evidence.FrameRelation;
            if (relation == null || !relation.IsValid || (long)relation.Count != count)
            {
                throw new ArgumentException(
                    "Evidence frame relation must be valid and match the appended count.", nameof(evidence));
            }

            return new NvencRunChunkFinalizationOperation(sink, evidence, artifactId);
        }

        internal NvencRunChunkSink Sink => _sink;

        internal NvencRunChunkSinkFinalizationEvidence Evidence => _evidence;

        internal string ArtifactId => _artifactId;

        internal long AppendedCount => _evidence.AppendedCount;

        internal long AccumulatedByteLength => _evidence.AccumulatedByteLength;

        internal long LastFrameId => _evidence.LastFrameId;

        internal CaptureArtifactFrameRelation FrameRelation => _evidence.FrameRelation;

        internal CaptureArtifactKind ArtifactKind => CaptureArtifactKind.FrameSequence;

        internal string FormatId => NvencRunChunkArtifactDescriptorFactory.FormatId;

        internal int FormatVersion => NvencRunChunkArtifactDescriptorFactory.FormatVersion;

        internal string StagingRelativePath => NvencRunChunkArtifactDescriptorFactory.StagingRelativePath;

        internal string FinalRelativePath => NvencRunChunkArtifactDescriptorFactory.FinalRelativePath;

        internal string PendingRelativePath => NvencRunChunkArtifactDescriptorFactory.PendingRelativePath;

        /// <summary>
        /// True when the issued binding is intact: the exact sink, evidence,
        /// and artifact id are held, the artifact id rule holds, the evidence
        /// ledger binding is intact, and the held count, length, and frame
        /// relation shape are valid. Poison, run abandonment, a parked append,
        /// and buffer phase are not considered; this is the post-issuance
        /// binding check used after finalization is known to have succeeded.
        /// </summary>
        internal bool IsIssuanceBindingIntact
        {
            get
            {
                try
                {
                    if (_sink == null || _evidence == null || _artifactId == null)
                    {
                        return false;
                    }

                    if (!IsArtifactIdValid(_artifactId))
                    {
                        return false;
                    }

                    if (!_evidence.IsLedgerBindingIntact(_sink))
                    {
                        return false;
                    }

                    long count = _evidence.AppendedCount;
                    if (count < 1 || count > NvencBringUpProfileV1.CadenceTickCount)
                    {
                        return false;
                    }

                    long length = _evidence.AccumulatedByteLength;
                    if (length < 1 || length > NvencBringUpProfileV1.MaxChunkByteLength)
                    {
                        return false;
                    }

                    if (_evidence.LastFrameId <= 0)
                    {
                        return false;
                    }

                    CaptureArtifactFrameRelation relation = _evidence.FrameRelation;
                    if (relation == null || !relation.IsValid || (long)relation.Count != count)
                    {
                        return false;
                    }

                    return true;
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is ArgumentOutOfRangeException
                    || ex is EncoderFallbackException
                    || ex is InvalidOperationException)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Pre-finalization acceptance: the issuance binding is intact and the
        /// evidence is currently issued for the sink, so poison, run
        /// abandonment, a parked append, and a non-Free buffer all reject.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    return IsIssuanceBindingIntact && _evidence.IsIssuedFor(_sink);
                }
                catch (Exception ex) when (ex is ArgumentException
                    || ex is ArgumentOutOfRangeException
                    || ex is EncoderFallbackException
                    || ex is InvalidOperationException)
                {
                    return false;
                }
            }
        }

        private static void RequireArtifactId(string artifactId)
        {
            if (artifactId == null)
            {
                throw new ArgumentNullException(nameof(artifactId));
            }

            if (!IsArtifactIdValid(artifactId))
            {
                throw new ArgumentException(
                    "Artifact ID must be non-empty and at most 512 UTF-8 bytes.", nameof(artifactId));
            }
        }

        private static bool IsArtifactIdValid(string artifactId)
        {
            return artifactId.Length > 0 && Encoding.UTF8.GetByteCount(artifactId) <= 512;
        }
    }
}
