using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable observation of the four artifacts of one authoritative plan
    /// entry: staging PNG, staging sidecar, final PNG, and final sidecar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The observation holds its operation and artifact path set as non-owning
    /// references and re-verifies the path-set/decision/entry-index correlation
    /// in both the constructor and <see cref="IsValid"/>. It records only
    /// observed facts; the artifact recovery classifier, not this type, later
    /// interprets them.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, owns and disposes nothing, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactEntryObservation
    {
        private readonly CaptureRunPublicationArtifactInspectionOperation _operation;
        private readonly CaptureRunPublicationArtifactPathSet _artifactPaths;
        private readonly CaptureRunPublicationEvidenceStatus _stagingPngStatus;
        private readonly long _stagingPngProbedByteCount;
        private readonly CaptureRunPublicationEvidenceStatus _stagingSidecarStatus;
        private readonly long _stagingSidecarProbedByteCount;
        private readonly CaptureRunPublicationEvidenceStatus _finalPngStatus;
        private readonly long _finalPngProbedByteCount;
        private readonly CaptureRunPublicationEvidenceStatus _finalSidecarStatus;
        private readonly long _finalSidecarProbedByteCount;

        internal CaptureRunPublicationArtifactEntryObservation(
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationArtifactPathSet artifactPaths,
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

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (artifactPaths == null)
            {
                throw new ArgumentNullException(nameof(artifactPaths));
            }

            if (!artifactPaths.IsValid)
            {
                throw new ArgumentException("Artifact path set must be valid.", nameof(artifactPaths));
            }

            if (!ReferenceEquals(artifactPaths.Decision, operation.Decision))
            {
                throw new ArgumentException("Artifact path set must share the operation's decision.", nameof(artifactPaths));
            }

            int entryIndex = artifactPaths.EntryIndex;
            if (entryIndex < 0 || entryIndex >= operation.EntryCount)
            {
                throw new ArgumentException("Artifact path set entry index must be within the operation entry count.", nameof(artifactPaths));
            }

            if (!ReferenceEquals(artifactPaths, operation.GetArtifactPaths(entryIndex)))
            {
                throw new ArgumentException("Artifact path set must be the operation's path set for its entry index.", nameof(artifactPaths));
            }

            CapturePublicationPlanEntry entry = artifactPaths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

            RequireEvidence(stagingPngStatus, stagingPngProbedByteCount, pngLimit, nameof(stagingPngStatus), nameof(stagingPngProbedByteCount));
            RequireEvidence(stagingSidecarStatus, stagingSidecarProbedByteCount, sidecarLimit, nameof(stagingSidecarStatus), nameof(stagingSidecarProbedByteCount));
            RequireEvidence(finalPngStatus, finalPngProbedByteCount, pngLimit, nameof(finalPngStatus), nameof(finalPngProbedByteCount));
            RequireEvidence(finalSidecarStatus, finalSidecarProbedByteCount, sidecarLimit, nameof(finalSidecarStatus), nameof(finalSidecarProbedByteCount));

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

        internal CaptureRunPublicationArtifactPathSet ArtifactPaths => _artifactPaths;

        internal CaptureRunPublicationEvidenceStatus StagingPngStatus => _stagingPngStatus;

        internal long StagingPngProbedByteCount => _stagingPngProbedByteCount;

        internal CaptureRunPublicationEvidenceStatus StagingSidecarStatus => _stagingSidecarStatus;

        internal long StagingSidecarProbedByteCount => _stagingSidecarProbedByteCount;

        internal CaptureRunPublicationEvidenceStatus FinalPngStatus => _finalPngStatus;

        internal long FinalPngProbedByteCount => _finalPngProbedByteCount;

        internal CaptureRunPublicationEvidenceStatus FinalSidecarStatus => _finalSidecarStatus;

        internal long FinalSidecarProbedByteCount => _finalSidecarProbedByteCount;

        internal bool IsValid
        {
            get
            {
                if (_operation == null || !_operation.IsValid || _artifactPaths == null || !_artifactPaths.IsValid)
                {
                    return false;
                }

                if (!ReferenceEquals(_artifactPaths.Decision, _operation.Decision))
                {
                    return false;
                }

                int entryIndex = _artifactPaths.EntryIndex;
                if (entryIndex < 0 || entryIndex >= _operation.EntryCount)
                {
                    return false;
                }

                if (!ReferenceEquals(_artifactPaths, _operation.GetArtifactPaths(entryIndex)))
                {
                    return false;
                }

                CapturePublicationPlanEntry entry = _artifactPaths.Entry;
                long pngLimit = Min(entry.PngByteLength, _operation.MaximumPngByteCount);
                long sidecarLimit = Min(entry.SidecarByteLength, CaptureFramePngArtifactCodec.MaximumCanonicalByteCount);

                return EvidenceSatisfied(_stagingPngStatus, _stagingPngProbedByteCount, pngLimit)
                    && EvidenceSatisfied(_stagingSidecarStatus, _stagingSidecarProbedByteCount, sidecarLimit)
                    && EvidenceSatisfied(_finalPngStatus, _finalPngProbedByteCount, pngLimit)
                    && EvidenceSatisfied(_finalSidecarStatus, _finalSidecarProbedByteCount, sidecarLimit);
            }
        }

        internal static bool IsDefinedStatus(CaptureRunPublicationEvidenceStatus status)
        {
            return status == CaptureRunPublicationEvidenceStatus.Absent
                || status == CaptureRunPublicationEvidenceStatus.MatchesExpected
                || status == CaptureRunPublicationEvidenceStatus.Mismatch
                || status == CaptureRunPublicationEvidenceStatus.Invalid
                || status == CaptureRunPublicationEvidenceStatus.LimitExceeded;
        }

        internal static bool EvidenceSatisfied(CaptureRunPublicationEvidenceStatus status, long probedByteCount, long limit)
        {
            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    return probedByteCount == 0;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
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

        internal static void RequireEvidence(
            CaptureRunPublicationEvidenceStatus status,
            long probedByteCount,
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
