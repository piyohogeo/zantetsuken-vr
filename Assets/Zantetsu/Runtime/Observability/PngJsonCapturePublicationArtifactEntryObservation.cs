using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable observation of the four artifacts of one authoritative plan
    /// entry — staging PNG, staging sidecar, final PNG, and final sidecar —
    /// recorded against the shared inspection operation, independent of the
    /// Recovery or Fresh path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly ten fields — the operation, the artifact path
    /// set, and the four per-artifact status and probed-byte-count pairs — and
    /// has no public or internal constructor. It holds no array, collection,
    /// or token. The normal factory validates the operation once through
    /// <see cref="PngJsonCapturePublicationArtifactInspectionOperation.TryValidate"/>
    /// and delegates to the trusted index-local path with the same token.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, no serialization or decoding, no
    /// hash computation, and no inspection, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactEntryObservation
    {
        private readonly PngJsonCapturePublicationArtifactInspectionOperation _operation;
        private readonly PngJsonCapturePublicationArtifactInspectionPathSet _artifactPaths;
        private readonly CaptureRunPublicationEvidenceStatus _stagingPngStatus;
        private readonly long _stagingPngProbedByteCount;
        private readonly CaptureRunPublicationEvidenceStatus _stagingSidecarStatus;
        private readonly long _stagingSidecarProbedByteCount;
        private readonly CaptureRunPublicationEvidenceStatus _finalPngStatus;
        private readonly long _finalPngProbedByteCount;
        private readonly CaptureRunPublicationEvidenceStatus _finalSidecarStatus;
        private readonly long _finalSidecarProbedByteCount;

        private PngJsonCapturePublicationArtifactEntryObservation(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus,
            long stagingPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus,
            long stagingSidecarProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalPngStatus,
            long finalPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus,
            long finalSidecarProbedByteCount)
        {
            _operation = operation;
            _artifactPaths = artifactPaths;
            _stagingPngStatus = stagingPngStatus;
            _stagingPngProbedByteCount = stagingPngProbedByteCount;
            _stagingSidecarStatus = stagingSidecarStatus;
            _stagingSidecarProbedByteCount = stagingSidecarProbedByteCount;
            _finalPngStatus = finalPngStatus;
            _finalPngProbedByteCount = finalPngProbedByteCount;
            _finalSidecarStatus = finalSidecarStatus;
            _finalSidecarProbedByteCount = finalSidecarProbedByteCount;
        }

        /// <summary>
        /// Normal factory: validates the operation once through
        /// <see cref="PngJsonCapturePublicationArtifactInspectionOperation.TryValidate"/>
        /// and immediately delegates to the trusted index-local path with the
        /// same token, so the operation's full validation runs exactly once.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactEntryObservation Create(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus,
            long stagingPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus,
            long stagingSidecarProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalPngStatus,
            long finalPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus,
            long finalSidecarProbedByteCount)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (artifactPaths == null)
            {
                throw new ArgumentNullException(nameof(artifactPaths));
            }

            if (!operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token))
            {
                throw new ArgumentException("Operation must be fully valid.", nameof(operation));
            }

            return CreateIndexLocal(
                token,
                operation,
                artifactPaths,
                stagingPngStatus,
                stagingPngProbedByteCount,
                stagingSidecarStatus,
                stagingSidecarProbedByteCount,
                finalPngStatus,
                finalPngProbedByteCount,
                finalSidecarStatus,
                finalSidecarProbedByteCount);
        }

        /// <summary>
        /// Trusted index-local factory: verifies only the token binding, entry
        /// index range, exact path set reference and authority, then the four
        /// evidence groups in O(1). It never re-runs the operation's full
        /// validation or issues a new token.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactEntryObservation CreateIndexLocal(
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus,
            long stagingPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus,
            long stagingSidecarProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalPngStatus,
            long finalPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus,
            long finalSidecarProbedByteCount)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (artifactPaths == null)
            {
                throw new ArgumentNullException(nameof(artifactPaths));
            }

            if (!token.IsIssuedFor(operation))
            {
                throw new ArgumentException("Token must be issued for the exact operation.", nameof(token));
            }

            int entryIndex = artifactPaths.EntryIndex;

            if (!operation.TryGetArtifactPaths(entryIndex, out PngJsonCapturePublicationArtifactInspectionPathSet expected)
                || !ReferenceEquals(artifactPaths, expected))
            {
                throw new ArgumentException("Artifact path set must be the operation's path set for its entry index.", nameof(artifactPaths));
            }

            if (!token.IsIndexLocalCorrelated(operation, entryIndex))
            {
                throw new ArgumentException("Operation token must remain correlated with the operation and its entry.", nameof(token));
            }

            if (!ReferenceEquals(artifactPaths.Authority, operation.Authority))
            {
                throw new ArgumentException("Artifact path set must share the operation's authority.", nameof(artifactPaths));
            }

            PngJsonCapturePublicationPlanEntry entry = artifactPaths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

            ArtifactRequireEvidence(stagingPngStatus, stagingPngProbedByteCount, entry.PngByteLength, pngLimit, nameof(stagingPngStatus), nameof(stagingPngProbedByteCount));
            ArtifactRequireEvidence(stagingSidecarStatus, stagingSidecarProbedByteCount, entry.SidecarByteLength, sidecarLimit, nameof(stagingSidecarStatus), nameof(stagingSidecarProbedByteCount));
            ArtifactRequireEvidence(finalPngStatus, finalPngProbedByteCount, entry.PngByteLength, pngLimit, nameof(finalPngStatus), nameof(finalPngProbedByteCount));
            ArtifactRequireEvidence(finalSidecarStatus, finalSidecarProbedByteCount, entry.SidecarByteLength, sidecarLimit, nameof(finalSidecarStatus), nameof(finalSidecarProbedByteCount));

            return new PngJsonCapturePublicationArtifactEntryObservation(
                operation,
                artifactPaths,
                stagingPngStatus,
                stagingPngProbedByteCount,
                stagingSidecarStatus,
                stagingSidecarProbedByteCount,
                finalPngStatus,
                finalPngProbedByteCount,
                finalSidecarStatus,
                finalSidecarProbedByteCount);
        }

        internal PngJsonCapturePublicationArtifactInspectionOperation Operation => _operation;

        internal PngJsonCapturePublicationArtifactInspectionPathSet ArtifactPaths => _artifactPaths;

        internal int EntryIndex => _artifactPaths.EntryIndex;

        internal PngJsonCapturePublicationPlanEntry Entry => _artifactPaths.Entry;

        internal long CaptureFrameId => _artifactPaths.CaptureFrameId;

        internal CaptureRunPublicationEvidenceStatus StagingPngStatus => _stagingPngStatus;

        internal long StagingPngProbedByteCount => _stagingPngProbedByteCount;

        internal CaptureRunPublicationEvidenceStatus StagingSidecarStatus => _stagingSidecarStatus;

        internal long StagingSidecarProbedByteCount => _stagingSidecarProbedByteCount;

        internal CaptureRunPublicationEvidenceStatus FinalPngStatus => _finalPngStatus;

        internal long FinalPngProbedByteCount => _finalPngProbedByteCount;

        internal CaptureRunPublicationEvidenceStatus FinalSidecarStatus => _finalSidecarStatus;

        internal long FinalSidecarProbedByteCount => _finalSidecarProbedByteCount;

        /// <summary>
        /// Full validity: validates the operation and issues a token once, then
        /// re-verifies this entry through the same token without throwing.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_operation == null || _artifactPaths == null)
                {
                    return false;
                }

                if (!_operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token))
                {
                    return false;
                }

                return IsValidIndexLocal(token);
            }
        }

        /// <summary>
        /// O(1) token-gated index-local validity: re-verifies the token's
        /// binding, the exact path set reference and authority, and the four
        /// evidence groups without re-running the operation's full validation.
        /// Never throws.
        /// </summary>
        internal bool IsValidIndexLocal(PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token)
        {
            if (token == null || _operation == null || _artifactPaths == null)
            {
                return false;
            }

            if (!token.IsIssuedFor(_operation))
            {
                return false;
            }

            int entryIndex = _artifactPaths.EntryIndex;

            if (!token.IsIndexLocalCorrelated(_operation, entryIndex))
            {
                return false;
            }

            if (!_operation.TryGetArtifactPaths(entryIndex, out PngJsonCapturePublicationArtifactInspectionPathSet expected)
                || !ReferenceEquals(_artifactPaths, expected))
            {
                return false;
            }

            if (!ReferenceEquals(_artifactPaths.Authority, _operation.Authority))
            {
                return false;
            }

            return EvidenceCombosSatisfied(
                _operation,
                _artifactPaths,
                _stagingPngStatus, _stagingPngProbedByteCount,
                _stagingSidecarStatus, _stagingSidecarProbedByteCount,
                _finalPngStatus, _finalPngProbedByteCount,
                _finalSidecarStatus, _finalSidecarProbedByteCount);
        }

        internal static bool IsDefinedStatus(CaptureRunPublicationEvidenceStatus status)
        {
            return status == CaptureRunPublicationEvidenceStatus.Absent
                || status == CaptureRunPublicationEvidenceStatus.MatchesExpected
                || status == CaptureRunPublicationEvidenceStatus.Mismatch
                || status == CaptureRunPublicationEvidenceStatus.Invalid
                || status == CaptureRunPublicationEvidenceStatus.LimitExceeded;
        }

        private static bool EvidenceCombosSatisfied(
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            PngJsonCapturePublicationArtifactInspectionPathSet artifactPaths,
            CaptureRunPublicationEvidenceStatus stagingPngStatus,
            long stagingPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus stagingSidecarStatus,
            long stagingSidecarProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalPngStatus,
            long finalPngProbedByteCount,
            CaptureRunPublicationEvidenceStatus finalSidecarStatus,
            long finalSidecarProbedByteCount)
        {
            PngJsonCapturePublicationPlanEntry entry = artifactPaths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

            return ArtifactEvidenceSatisfied(stagingPngStatus, stagingPngProbedByteCount, entry.PngByteLength, pngLimit)
                && ArtifactEvidenceSatisfied(stagingSidecarStatus, stagingSidecarProbedByteCount, entry.SidecarByteLength, sidecarLimit)
                && ArtifactEvidenceSatisfied(finalPngStatus, finalPngProbedByteCount, entry.PngByteLength, pngLimit)
                && ArtifactEvidenceSatisfied(finalSidecarStatus, finalSidecarProbedByteCount, entry.SidecarByteLength, sidecarLimit);
        }

        private static bool ArtifactEvidenceSatisfied(
            CaptureRunPublicationEvidenceStatus status,
            long probedByteCount,
            long expectedByteLength,
            long limit)
        {
            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    return probedByteCount == 0;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
                    return probedByteCount == expectedByteLength && expectedByteLength > 0;

                case CaptureRunPublicationEvidenceStatus.Mismatch:
                    return probedByteCount > 0 && probedByteCount <= limit;

                case CaptureRunPublicationEvidenceStatus.Invalid:
                    return probedByteCount >= 0 && probedByteCount <= limit;

                case CaptureRunPublicationEvidenceStatus.LimitExceeded:
                    return probedByteCount == checked(limit + 1);

                default:
                    return false;
            }
        }

        private static void ArtifactRequireEvidence(
            CaptureRunPublicationEvidenceStatus status,
            long probedByteCount,
            long expectedByteLength,
            long limit,
            string statusParamName,
            string countParamName)
        {
            if (!IsDefinedStatus(status))
            {
                throw new ArgumentOutOfRangeException(statusParamName, status, "Evidence status must be defined.");
            }

            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    if (probedByteCount != 0)
                    {
                        throw new ArgumentException("An absent evidence must have a zero probed byte count.", countParamName);
                    }

                    return;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
                    if (probedByteCount != expectedByteLength)
                    {
                        throw new ArgumentException("A matching evidence must probe exactly the expected byte length.", countParamName);
                    }

                    return;

                case CaptureRunPublicationEvidenceStatus.Mismatch:
                    if (probedByteCount <= 0 || probedByteCount > limit)
                    {
                        throw new ArgumentException("Evidence probed byte count must be positive and within the file limit.", countParamName);
                    }

                    return;

                case CaptureRunPublicationEvidenceStatus.Invalid:
                    if (probedByteCount < 0 || probedByteCount > limit)
                    {
                        throw new ArgumentException("Invalid evidence probed byte count must be non-negative and within the file limit.", countParamName);
                    }

                    return;

                case CaptureRunPublicationEvidenceStatus.LimitExceeded:
                    if (probedByteCount != checked(limit + 1))
                    {
                        throw new ArgumentException("A limit-exceeded evidence must probe exactly one byte past the file limit.", countParamName);
                    }

                    return;

                default:
                    throw new ArgumentOutOfRangeException(statusParamName, status, "Evidence status must be defined.");
            }
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
        }
    }
}
