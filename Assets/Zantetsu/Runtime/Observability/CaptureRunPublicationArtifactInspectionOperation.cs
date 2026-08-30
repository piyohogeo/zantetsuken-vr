using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free operation describing one Capture Run
    /// publication artifact inspection: the authoritative decision, the PNG
    /// probe bound, and the pre-resolved per-entry artifact path sets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The operation never disposes the decision, the lease, or any path set.
    /// Every plan entry's expected PNG and sidecar byte lengths must fit the
    /// operation's probe bounds, and every entry must resolve to a valid
    /// <see cref="CaptureRunPublicationArtifactPathSet"/>. <see cref="IsValid"/>
    /// recomputes these checks without throwing, so an operation whose lease
    /// has been released becomes invalid.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactInspectionOperation
    {
        internal const long MaximumAllowedPngByteCount = 1024L * 1024L * 1024L;

        /// <summary>
        /// Proof that this operation was fully validated. Only this operation
        /// can mint tokens, so callers cannot substitute a cheap check for the
        /// full <see cref="IsValid"/> pass.
        /// </summary>
        internal sealed class ValidationToken
        {
            private readonly CaptureRunPublicationArtifactInspectionOperation _operation;

            private ValidationToken(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                _operation = operation;
            }

            /// <summary>
            /// Reports whether this token was issued for the given operation.
            /// The binding is reference-identical; the token carries no other
            /// state and exposes no reference back to its operation.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                return operation != null && ReferenceEquals(_operation, operation);
            }

            internal static ValidationToken Acquire(CaptureRunPublicationArtifactInspectionOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!operation.IsValid)
                {
                    throw new InvalidOperationException("Operation must be fully valid before issuing a validation token.");
                }

                return new ValidationToken(operation);
            }
        }

        /// <summary>
        /// Construction-grade proof minted only after the decision graph, plan,
        /// and probe bounds are fully validated. Distinct from
        /// <see cref="ValidationToken"/> so it can unlock index-local path set
        /// assembly but never index-local validity checks.
        /// </summary>
        internal sealed class ConstructionToken
        {
            private readonly CaptureRunPublicationRecoveryDecision _decision;

            private ConstructionToken(CaptureRunPublicationRecoveryDecision decision)
            {
                _decision = decision;
            }

            /// <summary>
            /// Reports whether this token was issued for the given decision.
            /// The binding is reference-identical; the token carries no other
            /// state and exposes no reference back to its decision.
            /// </summary>
            internal bool IsIssuedFor(CaptureRunPublicationRecoveryDecision decision)
            {
                return decision != null && ReferenceEquals(_decision, decision);
            }

            internal static ConstructionToken Acquire(
                CaptureRunPublicationRecoveryDecision decision,
                long maximumPngByteCount)
            {
                if (decision == null)
                {
                    throw new ArgumentNullException(nameof(decision));
                }

                if (!decision.IsValid)
                {
                    throw new ArgumentException("Decision must be valid.", nameof(decision));
                }

                CaptureRunPublicationRecoveryDisposition disposition = decision.Disposition;
                if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                    && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
                {
                    throw new ArgumentException("Decision must carry an authoritative plan.", nameof(decision));
                }

                PngJsonCapturePublicationPlan plan = decision.AuthoritativePlan;
                if (plan == null || !plan.IsValid)
                {
                    throw new ArgumentException("Decision must hold a valid authoritative plan.", nameof(decision));
                }

                if (!DecisionGraphCorrelated(decision))
                {
                    throw new ArgumentException("Decision graph must be fully correlated with its lease and publication paths.", nameof(decision));
                }

                if (maximumPngByteCount < 1 || maximumPngByteCount > MaximumAllowedPngByteCount)
                {
                    throw new ArgumentOutOfRangeException(nameof(maximumPngByteCount), maximumPngByteCount,
                        "Maximum PNG byte count must be between 1 and " + MaximumAllowedPngByteCount + ".");
                }

                return new ConstructionToken(decision);
            }
        }

        private readonly CaptureRunPublicationRecoveryDecision _decision;
        private readonly long _maximumPngByteCount;
        private readonly CaptureRunPublicationArtifactPathSet[] _artifactPaths;

        internal CaptureRunPublicationArtifactInspectionOperation(
            CaptureRunPublicationRecoveryDecision decision,
            long maximumPngByteCount)
        {
            ConstructionToken token = ConstructionToken.Acquire(decision, maximumPngByteCount);

            PngJsonCapturePublicationPlan plan = decision.AuthoritativePlan;

            CaptureRunPublicationArtifactPathSet[] paths = new CaptureRunPublicationArtifactPathSet[plan.EntryCount];
            for (int i = 0; i < plan.EntryCount; i++)
            {
                PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(i);

                if (entry.PngByteLength > maximumPngByteCount)
                {
                    throw new ArgumentException("Plan entry PNG byte length must not exceed the maximum PNG byte count.", nameof(decision));
                }

                if (entry.SidecarByteLength > CaptureFramePngArtifactCodec.MaximumCanonicalByteCount)
                {
                    throw new ArgumentException("Plan entry sidecar byte length must not exceed the sidecar canonical byte count.", nameof(decision));
                }

                paths[i] = CaptureRunPublicationArtifactPathSet.CreateIndexLocal(token, decision, i);
            }

            _decision = decision;
            _maximumPngByteCount = maximumPngByteCount;
            _artifactPaths = paths;
        }

        internal CaptureRunPublicationRecoveryDecision Decision => _decision;

        internal PngJsonCapturePublicationPlan Plan => _decision.AuthoritativePlan;

        internal int EntryCount => Plan.EntryCount;

        internal long MaximumPngByteCount => _maximumPngByteCount;

        internal int MaximumSidecarByteCount => CaptureFramePngArtifactCodec.MaximumCanonicalByteCount;

        internal int MaximumTraceManifestByteCount => TraceRunManifestCodec.MaximumCanonicalByteCount;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal CaptureRunLockLease LockLease => _decision.Snapshot.Operation.LockLease;

        internal long TestRunId => _decision.TestRunId;

        internal string RunInitializationId => _decision.RunInitializationId;

        internal string RunManifestContentSha256 => Plan.RunManifestContentSha256;

        internal CaptureRunPublicationArtifactPathSet GetArtifactPaths(int index)
        {
            if (index < 0 || index >= _artifactPaths.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the entry count.");
            }

            return _artifactPaths[index];
        }

        internal bool TryGetArtifactPaths(int index, out CaptureRunPublicationArtifactPathSet paths)
        {
            paths = null;
            if (_artifactPaths == null || index < 0 || index >= _artifactPaths.Length)
            {
                return false;
            }

            paths = _artifactPaths[index];
            return true;
        }

        /// <summary>
        /// Issues a validation token only after a full <see cref="IsValid"/>
        /// pass, proving the whole decision graph, plan, lease, and artifact
        /// path array are currently valid. The token cannot be fabricated by
        /// callers and is the only way to reach index-local validity checks.
        /// </summary>
        internal ValidationToken AcquireValidationToken()
        {
            return ValidationToken.Acquire(this);
        }

        internal bool IsValid
        {
            get
            {
                if (_decision == null || !_decision.IsValid)
                {
                    return false;
                }

                CaptureRunPublicationRecoveryDisposition disposition = _decision.Disposition;
                if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                    && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
                {
                    return false;
                }

                PngJsonCapturePublicationPlan plan = _decision.AuthoritativePlan;
                if (plan == null || !plan.IsValid)
                {
                    return false;
                }

                if (_maximumPngByteCount < 1 || _maximumPngByteCount > MaximumAllowedPngByteCount)
                {
                    return false;
                }

                if (!DecisionGraphCorrelated(_decision))
                {
                    return false;
                }

                if (_artifactPaths == null || _artifactPaths.Length != plan.EntryCount)
                {
                    return false;
                }

                for (int i = 0; i < _artifactPaths.Length; i++)
                {
                    CaptureRunPublicationArtifactPathSet paths = _artifactPaths[i];
                    if (paths == null || !paths.IsValidIndexLocal() || !ReferenceEquals(paths.Decision, _decision) || paths.EntryIndex != i)
                    {
                        return false;
                    }

                    PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(i);
                    if (entry.PngByteLength > _maximumPngByteCount
                        || entry.SidecarByteLength > CaptureFramePngArtifactCodec.MaximumCanonicalByteCount)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        private static bool DecisionGraphCorrelated(CaptureRunPublicationRecoveryDecision decision)
        {
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null || !snapshot.IsValid)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;
            if (operation == null || !operation.IsValid)
            {
                return false;
            }

            CaptureRunLockLease lockLease = operation.LockLease;
            if (lockLease == null || !lockLease.IsCreated)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = operation.RootLayout;
            if (rootLayout == null || !rootLayout.IsValid)
            {
                return false;
            }

            CaptureRunPublicationPathSet publicationPaths = operation.PublicationPaths;
            return publicationPaths != null
                && publicationPaths.IsValid
                && ReferenceEquals(publicationPaths.RootLayout, rootLayout)
                && ReferenceEquals(decision.RootLayout, rootLayout);
        }
    }
}
