using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of a completed publication artifact inspection:
    /// which inspector produced it, which operation it observed, the trace
    /// manifest evidence, and one entry observation per plan entry in index
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly five fields — the issuer, the operation, the
    /// trace manifest status, the trace manifest probed byte count, and the
    /// entry observation array — and has no public or internal constructor.
    /// It duplicates no plan, authority, root layout, lease, identifier, or
    /// hash; every accessor forwards from the operation. The entry array is
    /// copied once into a private exact-length array during construction.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes every correlation in O(n) without
    /// throwing, and <see cref="TryValidate"/> issues a validation token only
    /// after a full validation succeeds. The snapshot performs no filesystem
    /// work, no serialization or decoding, no hash computation, and no
    /// inspection, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactInspectionSnapshot
    {
        private readonly IPngJsonCapturePublicationArtifactInspector _issuedBy;
        private readonly PngJsonCapturePublicationArtifactInspectionOperation _operation;
        private readonly CaptureRunPublicationEvidenceStatus _traceManifestStatus;
        private readonly long _traceManifestProbedByteCount;
        private readonly PngJsonCapturePublicationArtifactEntryObservation[] _entries;

        private PngJsonCapturePublicationArtifactInspectionSnapshot(
            IPngJsonCapturePublicationArtifactInspector issuedBy,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus traceManifestStatus,
            long traceManifestProbedByteCount,
            PngJsonCapturePublicationArtifactEntryObservation[] entries)
        {
            _issuedBy = issuedBy;
            _operation = operation;
            _traceManifestStatus = traceManifestStatus;
            _traceManifestProbedByteCount = traceManifestProbedByteCount;
            _entries = entries;
        }

        /// <summary>
        /// Atomic validated factory: validates the operation once through its
        /// validation token, validates the trace manifest evidence, then reads
        /// each entry once into a local, validates it in ascending index order,
        /// and stores that same reference into a private exact-length array in
        /// the same loop. The snapshot is constructed only after every entry
        /// validates, so a caller that mutates the source array during
        /// construction cannot smuggle an unvalidated reference past the single
        /// validation/storage loop.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactInspectionSnapshot Create(
            IPngJsonCapturePublicationArtifactInspector issuedBy,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            CaptureRunPublicationEvidenceStatus traceManifestStatus,
            long traceManifestProbedByteCount,
            PngJsonCapturePublicationArtifactEntryObservation[] entries)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (!operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken operationToken))
            {
                throw new ArgumentException("Operation must be fully valid.", nameof(operation));
            }

            TraceRequireEvidence(
                traceManifestStatus,
                traceManifestProbedByteCount,
                operation.MaximumTraceManifestByteCount,
                nameof(traceManifestStatus),
                nameof(traceManifestProbedByteCount));

            if (entries.Length != operation.EntryCount)
            {
                throw new ArgumentException("Entry observation count must match the operation entry count.", nameof(entries));
            }

            PngJsonCapturePublicationArtifactEntryObservation[] copy = new PngJsonCapturePublicationArtifactEntryObservation[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                PngJsonCapturePublicationArtifactEntryObservation entry = entries[i];
                if (entry == null)
                {
                    throw new ArgumentException("Entry observation array must not contain null elements.", nameof(entries));
                }

                if (!entry.IsValidIndexLocal(operationToken))
                {
                    throw new ArgumentException("Entry observation must be valid.", nameof(entries));
                }

                if (!ReferenceEquals(entry.Operation, operation))
                {
                    throw new ArgumentException("Entry observation must share the operation.", nameof(entries));
                }

                if (entry.EntryIndex != i)
                {
                    throw new ArgumentException("Entry observation must correspond to its index.", nameof(entries));
                }

                if (!ReferenceEquals(entry.ArtifactPaths, operation.GetArtifactPaths(i)))
                {
                    throw new ArgumentException("Entry observation must use the operation's path set for its index.", nameof(entries));
                }

                copy[i] = entry;
            }

            return new PngJsonCapturePublicationArtifactInspectionSnapshot(
                issuedBy, operation, traceManifestStatus, traceManifestProbedByteCount, copy);
        }

        internal IPngJsonCapturePublicationArtifactInspector IssuedBy => _issuedBy;

        internal PngJsonCapturePublicationArtifactInspectionOperation Operation => _operation;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _operation.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _operation.AuthorityKind;

        internal PngJsonCapturePublicationPlan Plan => _operation.Plan;

        internal CaptureRunPublicationEvidenceStatus TraceManifestStatus => _traceManifestStatus;

        internal long TraceManifestProbedByteCount => _traceManifestProbedByteCount;

        internal int Count => _entries.Length;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _operation.LockIdentityEvidence;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal string RunManifestContentSha256 => _operation.RunManifestContentSha256;

        internal PngJsonCapturePublicationArtifactEntryObservation GetEntry(int index)
        {
            if (index < 0 || index >= _entries.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the entry count.");
            }

            return _entries[index];
        }

        internal bool TryGetEntry(int index, out PngJsonCapturePublicationArtifactEntryObservation observation)
        {
            observation = null;
            if (_entries == null || index < 0 || index >= _entries.Length)
            {
                return false;
            }

            observation = _entries[index];
            return true;
        }

        /// <summary>
        /// Reports whether this snapshot was issued by the exact inspector for
        /// the exact operation and is still fully valid. Never throws.
        /// </summary>
        internal bool IsIssuedFor(
            IPngJsonCapturePublicationArtifactInspector inspector,
            PngJsonCapturePublicationArtifactInspectionOperation operation)
        {
            if (inspector == null || operation == null)
            {
                return false;
            }

            return ReferenceEquals(_issuedBy, inspector)
                && ReferenceEquals(_operation, operation)
                && IsValid;
        }

        /// <summary>
        /// O(n), exception-safe full validity: validates the operation and
        /// issues its token once, re-validates the trace manifest evidence, and
        /// re-checks every entry in ascending index order without allocating a
        /// snapshot proof array.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_issuedBy == null || _operation == null)
                {
                    return false;
                }

                if (!_operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken operationToken))
                {
                    return false;
                }

                if (!TraceEvidenceSatisfied(
                    _traceManifestStatus, _traceManifestProbedByteCount, _operation.MaximumTraceManifestByteCount))
                {
                    return false;
                }

                if (_entries == null || _entries.Length != _operation.EntryCount)
                {
                    return false;
                }

                for (int i = 0; i < _entries.Length; i++)
                {
                    PngJsonCapturePublicationArtifactEntryObservation entry = _entries[i];
                    if (entry == null || !entry.IsValidIndexLocal(operationToken))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(entry.Operation, _operation)
                        || entry.EntryIndex != i
                        || !ReferenceEquals(entry.ArtifactPaths, _operation.GetArtifactPaths(i)))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// Full validation plus token issuance: issues a snapshot validation
        /// token only after the whole snapshot validates, so a stale or
        /// corrupted snapshot never produces a token.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
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
            if (!PngJsonCapturePublicationArtifactEntryObservation.IsDefinedStatus(status))
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

        /// <summary>
        /// Issuance proof minted only after the whole snapshot validates once.
        /// It binds to the exact snapshot, inspector, operation, operation
        /// token, and entry array, snapshots the trace evidence and each
        /// entry's values, and exposes no proof array or token.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationArtifactInspectionSnapshot _snapshot;
            private readonly IPngJsonCapturePublicationArtifactInspector _issuedBy;
            private readonly PngJsonCapturePublicationArtifactInspectionOperation _operation;
            private readonly PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken _operationToken;
            private readonly PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken _operationTokenProof;
            private readonly CaptureRunPublicationEvidenceStatus _traceManifestStatus;
            private readonly long _traceManifestProbedByteCount;
            private readonly PngJsonCapturePublicationArtifactEntryObservation[] _entriesArray;
            private readonly EntryProof[] _proof;

            private ValidationToken(
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
                IPngJsonCapturePublicationArtifactInspector issuedBy,
                PngJsonCapturePublicationArtifactInspectionOperation operation,
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken operationToken,
                CaptureRunPublicationEvidenceStatus traceManifestStatus,
                long traceManifestProbedByteCount,
                PngJsonCapturePublicationArtifactEntryObservation[] entriesArray,
                EntryProof[] proof)
            {
                _snapshot = snapshot;
                _issuedBy = issuedBy;
                _operation = operation;
                _operationToken = operationToken;
                _operationTokenProof = operationToken;
                _traceManifestStatus = traceManifestStatus;
                _traceManifestProbedByteCount = traceManifestProbedByteCount;
                _entriesArray = entriesArray;
                _proof = proof;
            }

            /// <summary>
            /// Reports whether this token was issued for the given snapshot.
            /// The binding is reference-identical.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
            {
                return snapshot != null && ReferenceEquals(_snapshot, snapshot);
            }

            /// <summary>
            /// O(1), exception-safe exact-binding check: confirms the exact
            /// snapshot, inspector, operation, operation token, trace evidence,
            /// and entry array reference identity without touching any entry
            /// element. Never throws and never exposes the proof array or the
            /// operation token.
            /// </summary>
            internal bool IsIssuedForExactBindings(PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
            {
                if (snapshot == null || !ReferenceEquals(_snapshot, snapshot))
                {
                    return false;
                }

                if (_issuedBy == null || _operation == null
                    || _operationToken == null || _operationTokenProof == null
                    || _entriesArray == null || _proof == null)
                {
                    return false;
                }

                if (_entriesArray.Length != _proof.Length)
                {
                    return false;
                }

                if (!ReferenceEquals(_operationToken, _operationTokenProof))
                {
                    return false;
                }

                if (!ReferenceEquals(snapshot.IssuedBy, _issuedBy)
                    || !ReferenceEquals(snapshot.Operation, _operation))
                {
                    return false;
                }

                if (snapshot._traceManifestStatus != _traceManifestStatus
                    || snapshot._traceManifestProbedByteCount != _traceManifestProbedByteCount)
                {
                    return false;
                }

                if (!ReferenceEquals(snapshot._entries, _entriesArray))
                {
                    return false;
                }

                return _operationToken.IsIssuedForExactBindings(_operation);
            }

            /// <summary>
            /// O(1), exception-safe issued-entry access for one index: confirms
            /// the exact bindings, then re-verifies the issued entry reference
            /// and its value snapshot before returning it. The caller must use
            /// only the returned reference. Never throws.
            /// </summary>
            internal bool TryGetIssuedEntry(
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
                int index,
                out PngJsonCapturePublicationArtifactEntryObservation observation)
            {
                observation = null;
                if (!IsIssuedForExactBindings(snapshot))
                {
                    return false;
                }

                EntryProof[] proof = _proof;
                if (proof == null || index < 0 || index >= proof.Length)
                {
                    return false;
                }

                try
                {
                    PngJsonCapturePublicationArtifactEntryObservation entry = snapshot._entries[index];
                    if (entry == null || !proof[index].Matches(entry))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(entry.ArtifactPaths, _operation.GetArtifactPaths(index)))
                    {
                        return false;
                    }

                    if (!entry.IsValidIndexLocal(_operationToken))
                    {
                        return false;
                    }

                    observation = entry;
                    return true;
                }
                catch (Exception)
                {
                    observation = null;
                    return false;
                }
            }

            /// <summary>
            /// O(1), exception-safe index-local correlation for one index:
            /// confirms the exact snapshot, inspector, operation, operation
            /// token, trace evidence, entry array, and entry element reference,
            /// then re-validates the entry observation against the operation
            /// token. Never throws.
            /// </summary>
            internal bool IsIndexLocalCorrelated(
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
                int index)
            {
                if (snapshot == null || !ReferenceEquals(_snapshot, snapshot))
                {
                    return false;
                }

                EntryProof[] proof = _proof;
                if (proof == null)
                {
                    return false;
                }

                if (index < 0 || index >= proof.Length)
                {
                    return false;
                }

                if (_issuedBy == null || _operation == null
                    || _operationToken == null || _operationTokenProof == null)
                {
                    return false;
                }

                if (snapshot._traceManifestStatus != _traceManifestStatus
                    || snapshot._traceManifestProbedByteCount != _traceManifestProbedByteCount)
                {
                    return false;
                }

                if (!ReferenceEquals(_operationToken, _operationTokenProof))
                {
                    return false;
                }

                try
                {
                    PngJsonCapturePublicationArtifactEntryObservation[] entries = snapshot._entries;
                    if (entries == null || !ReferenceEquals(entries, _entriesArray))
                    {
                        return false;
                    }

                    PngJsonCapturePublicationArtifactEntryObservation observation = entries[index];
                    if (observation == null || !proof[index].Matches(observation))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(snapshot.IssuedBy, _issuedBy)
                        || !ReferenceEquals(snapshot.Operation, _operation))
                    {
                        return false;
                    }

                    if (!_operationToken.IsIssuedFor(_operation))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(observation.ArtifactPaths, _operation.GetArtifactPaths(index)))
                    {
                        return false;
                    }

                    return observation.IsValidIndexLocal(_operationToken);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>
            /// Performs the full snapshot validation once and mints a token
            /// only on success. The private constructor keeps the token
            /// unfabricable by callers.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
                out ValidationToken token)
            {
                token = null;
                if (snapshot == null)
                {
                    return false;
                }

                IPngJsonCapturePublicationArtifactInspector issuedBy = snapshot._issuedBy;
                PngJsonCapturePublicationArtifactInspectionOperation operation = snapshot._operation;
                if (issuedBy == null || operation == null)
                {
                    return false;
                }

                if (!operation.TryValidate(out PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken operationToken))
                {
                    return false;
                }

                if (!TraceEvidenceSatisfied(
                    snapshot._traceManifestStatus, snapshot._traceManifestProbedByteCount, operation.MaximumTraceManifestByteCount))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactEntryObservation[] entries = snapshot._entries;
                if (entries == null || entries.Length != operation.EntryCount)
                {
                    return false;
                }

                for (int i = 0; i < entries.Length; i++)
                {
                    PngJsonCapturePublicationArtifactEntryObservation entry = entries[i];
                    if (entry == null
                        || !entry.IsValidIndexLocal(operationToken)
                        || !ReferenceEquals(entry.Operation, operation)
                        || entry.EntryIndex != i
                        || !ReferenceEquals(entry.ArtifactPaths, operation.GetArtifactPaths(i)))
                    {
                        return false;
                    }
                }

                EntryProof[] proof = new EntryProof[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    proof[i] = new EntryProof(entries[i]);
                }

                token = new ValidationToken(
                    snapshot, issuedBy, operation, operationToken,
                    snapshot._traceManifestStatus, snapshot._traceManifestProbedByteCount,
                    entries, proof);
                return true;
            }

            internal static ValidationToken Acquire(PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
            {
                if (snapshot == null)
                {
                    throw new ArgumentNullException(nameof(snapshot));
                }

                if (!TryAcquire(snapshot, out ValidationToken token))
                {
                    throw new InvalidOperationException("Snapshot must be fully valid before issuing a validation token.");
                }

                return token;
            }
        }

        /// <summary>
        /// Immutable snapshot of one entry observation's exact reference and
        /// every value field, captured once at token issuance so a later
        /// reference or value mutation is detected at the target index in O(1).
        /// </summary>
        private readonly struct EntryProof
        {
            private readonly PngJsonCapturePublicationArtifactEntryObservation _observation;
            private readonly PngJsonCapturePublicationArtifactInspectionOperation _operation;
            private readonly PngJsonCapturePublicationArtifactInspectionPathSet _artifactPaths;
            private readonly int _entryIndex;
            private readonly CaptureRunPublicationEvidenceStatus _stagingPngStatus;
            private readonly long _stagingPngProbedByteCount;
            private readonly CaptureRunPublicationEvidenceStatus _stagingSidecarStatus;
            private readonly long _stagingSidecarProbedByteCount;
            private readonly CaptureRunPublicationEvidenceStatus _finalPngStatus;
            private readonly long _finalPngProbedByteCount;
            private readonly CaptureRunPublicationEvidenceStatus _finalSidecarStatus;
            private readonly long _finalSidecarProbedByteCount;

            internal EntryProof(PngJsonCapturePublicationArtifactEntryObservation observation)
            {
                _observation = observation;
                _operation = observation.Operation;
                _artifactPaths = observation.ArtifactPaths;
                _entryIndex = observation.EntryIndex;
                _stagingPngStatus = observation.StagingPngStatus;
                _stagingPngProbedByteCount = observation.StagingPngProbedByteCount;
                _stagingSidecarStatus = observation.StagingSidecarStatus;
                _stagingSidecarProbedByteCount = observation.StagingSidecarProbedByteCount;
                _finalPngStatus = observation.FinalPngStatus;
                _finalPngProbedByteCount = observation.FinalPngProbedByteCount;
                _finalSidecarStatus = observation.FinalSidecarStatus;
                _finalSidecarProbedByteCount = observation.FinalSidecarProbedByteCount;
            }

            internal bool Matches(PngJsonCapturePublicationArtifactEntryObservation observation)
            {
                return observation != null
                    && ReferenceEquals(_observation, observation)
                    && ReferenceEquals(_operation, observation.Operation)
                    && ReferenceEquals(_artifactPaths, observation.ArtifactPaths)
                    && _entryIndex == observation.EntryIndex
                    && _stagingPngStatus == observation.StagingPngStatus
                    && _stagingPngProbedByteCount == observation.StagingPngProbedByteCount
                    && _stagingSidecarStatus == observation.StagingSidecarStatus
                    && _stagingSidecarProbedByteCount == observation.StagingSidecarProbedByteCount
                    && _finalPngStatus == observation.FinalPngStatus
                    && _finalPngProbedByteCount == observation.FinalPngProbedByteCount
                    && _finalSidecarStatus == observation.FinalSidecarStatus
                    && _finalSidecarProbedByteCount == observation.FinalSidecarProbedByteCount;
            }
        }
    }
}
