using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of a completed publication artifact inspection:
    /// which inspector produced it, which operation it observed, the trace
    /// manifest evidence, and one observation per plan entry in index order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot owns and disposes nothing — neither the operation, the
    /// lease, the decision, the plan, nor the artifacts. It holds no
    /// <c>TraceRunManifest</c> and no raw bytes; a
    /// <see cref="CaptureRunPublicationEvidenceStatus.MatchesExpected"/> trace
    /// status is the recorded fact that the inspector confirmed canonical
    /// decode, the operation's test run ID, and the manifest correlation.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes every correlation without throwing, so
    /// a snapshot whose lease has been released, or whose entries were forged,
    /// becomes invalid.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactInspectionSnapshot
    {
        private readonly ICaptureRunPublicationArtifactInspector _issuedBy;
        private readonly CaptureRunPublicationArtifactInspectionOperation _operation;
        private readonly CaptureRunPublicationEvidenceStatus _traceManifestStatus;
        private readonly long _traceManifestProbedByteCount;
        private readonly CaptureRunPublicationArtifactEntryObservation[] _entries;

        internal CaptureRunPublicationArtifactInspectionSnapshot(
            ICaptureRunPublicationArtifactInspector issuedBy,
            CaptureRunPublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus traceManifestStatus,
            long traceManifestProbedByteCount,
            CaptureRunPublicationArtifactEntryObservation[] entries)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            TraceRequireEvidence(
                traceManifestStatus, traceManifestProbedByteCount, TraceRunManifestCodec.MaximumCanonicalByteCount,
                nameof(traceManifestStatus), nameof(traceManifestProbedByteCount));

            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (entries.Length != operation.EntryCount)
            {
                throw new ArgumentException("Entry observation count must match the operation entry count.", nameof(entries));
            }

            for (int i = 0; i < entries.Length; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = entries[i];
                if (observation == null)
                {
                    throw new ArgumentException("Entry observation array must not contain null elements.", nameof(entries));
                }

                if (!observation.IsValid)
                {
                    throw new ArgumentException("Entry observation must be valid.", nameof(entries));
                }

                if (!ReferenceEquals(observation.ArtifactPaths, operation.GetArtifactPaths(i))
                    || observation.ArtifactPaths.EntryIndex != i)
                {
                    throw new ArgumentException("Entry observation must correspond to its index in the operation.", nameof(entries));
                }
            }

            CaptureRunPublicationArtifactEntryObservation[] copy = new CaptureRunPublicationArtifactEntryObservation[entries.Length];
            Array.Copy(entries, copy, entries.Length);

            _issuedBy = issuedBy;
            _operation = operation;
            _traceManifestStatus = traceManifestStatus;
            _traceManifestProbedByteCount = traceManifestProbedByteCount;
            _entries = copy;
        }

        internal ICaptureRunPublicationArtifactInspector IssuedBy => _issuedBy;

        internal CaptureRunPublicationArtifactInspectionOperation Operation => _operation;

        internal CaptureRunPublicationRecoveryDecision Decision => _operation.Decision;

        internal CapturePublicationPlan Plan => _operation.Plan;

        internal CaptureRunPublicationEvidenceStatus TraceManifestStatus => _traceManifestStatus;

        internal long TraceManifestProbedByteCount => _traceManifestProbedByteCount;

        internal int Count => _entries.Length;

        internal CaptureRunPublicationArtifactEntryObservation GetEntry(int index)
        {
            if (index < 0 || index >= _entries.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the entry count.");
            }

            return _entries[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_issuedBy == null || _operation == null || !_operation.IsValid)
                {
                    return false;
                }

                if (!TraceEvidenceSatisfied(
                    _traceManifestStatus, _traceManifestProbedByteCount, TraceRunManifestCodec.MaximumCanonicalByteCount))
                {
                    return false;
                }

                if (_entries == null || _entries.Length != _operation.EntryCount)
                {
                    return false;
                }

                for (int i = 0; i < _entries.Length; i++)
                {
                    CaptureRunPublicationArtifactEntryObservation observation = _entries[i];
                    if (observation == null || !observation.IsValid)
                    {
                        return false;
                    }

                    if (!ReferenceEquals(observation.ArtifactPaths, _operation.GetArtifactPaths(i))
                        || observation.ArtifactPaths.EntryIndex != i)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private static bool TraceEvidenceSatisfied(
            CaptureRunPublicationEvidenceStatus status,
            long probedByteCount,
            long limit)
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

        private static void TraceRequireEvidence(
            CaptureRunPublicationEvidenceStatus status,
            long probedByteCount,
            long limit,
            string statusParamName,
            string countParamName)
        {
            if (!CaptureRunPublicationArtifactEntryObservation.IsDefinedStatus(status))
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
    }
}
