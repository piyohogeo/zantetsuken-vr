using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run publication artifact inspection
    /// operation for the exclusive inspection authority: binds the authority,
    /// the PNG probe bound, and the pre-resolved per-entry artifact path sets
    /// into a single value contract shared by the Recovery and Fresh paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly three fields — the authority, the maximum PNG
    /// byte count, and the artifact path array — and has no public or internal
    /// constructor. It duplicates no plan, entry, root layout, lease,
    /// identifier, hash, or publication path set; every accessor forwards from
    /// the authority. The only construction path is <see cref="Create"/>,
    /// which validates the authority once through its validation token and
    /// builds each path set through the trusted index-local path.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the whole correlation in O(n) without
    /// allocating a proof array, and <see cref="TryValidate"/> issues a
    /// validation token only after a full validation succeeds. The operation
    /// performs no filesystem work, no serialization or decoding, no hash
    /// computation, and no inspection, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactInspectionOperation
    {
        internal const long MaximumAllowedPngByteCount = 1024L * 1024L * 1024L;

        private readonly PngJsonCapturePublicationArtifactInspectionAuthority _authority;
        private readonly long _maximumPngByteCount;
        private readonly PngJsonCapturePublicationArtifactInspectionPathSet[] _artifactPaths;

        private PngJsonCapturePublicationArtifactInspectionOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount,
            PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPaths)
        {
            _authority = authority;
            _maximumPngByteCount = maximumPngByteCount;
            _artifactPaths = artifactPaths;
        }

        /// <summary>
        /// Atomic validated factory: the single validation-and-construction
        /// site. It validates the authority once through its validation token,
        /// confirms the exact plan, publication path, root layout, and lease
        /// binding, allocates the exact-length path array once, and builds each
        /// entry through the trusted index-local path before assigning fields.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactInspectionOperation Create(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount)
        {
            if (authority == null)
            {
                throw new ArgumentNullException(nameof(authority));
            }

            if (maximumPngByteCount < 1 || maximumPngByteCount > MaximumAllowedPngByteCount)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumPngByteCount), maximumPngByteCount,
                    "Maximum PNG byte count must be between 1 and " + MaximumAllowedPngByteCount + ".");
            }

            if (!PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.TryAcquire(
                authority,
                out PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken authorityToken))
            {
                throw new ArgumentException("Authority must be fully valid.", nameof(authority));
            }

            PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
            CaptureRunPublicationPathSet publicationPaths = authority.PublicationPaths;
            CaptureRunRootLayout rootLayout = authority.RootLayout;
            CaptureRunLockLease lockLease = authority.LockLease;
            if (plan == null || publicationPaths == null || rootLayout == null || lockLease == null)
            {
                throw new ArgumentException(
                    "Authority must hold an authoritative plan, publication paths, root layout, and lock lease.",
                    nameof(authority));
            }

            int entryCount = plan.EntryCount;
            PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPaths =
                new PngJsonCapturePublicationArtifactInspectionPathSet[entryCount];

            for (int i = 0; i < entryCount; i++)
            {
                PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(i);
                if (entry == null)
                {
                    throw new ArgumentException("Plan entry must not be null.", nameof(authority));
                }

                if (entry.PngByteLength > maximumPngByteCount)
                {
                    throw new ArgumentException(
                        "Plan entry PNG byte length must not exceed the maximum PNG byte count.",
                        nameof(authority));
                }

                if (entry.SidecarByteLength > CaptureFramePngArtifactCodec.MaximumCanonicalByteCount)
                {
                    throw new ArgumentException(
                        "Plan entry sidecar byte length must not exceed the sidecar canonical byte count.",
                        nameof(authority));
                }

                artifactPaths[i] = PngJsonCapturePublicationArtifactInspectionPathSet.CreateIndexLocal(
                    authorityToken, authority, i);
            }

            return new PngJsonCapturePublicationArtifactInspectionOperation(authority, maximumPngByteCount, artifactPaths);
        }

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _authority.Kind;

        internal PngJsonCapturePublicationPlan Plan => _authority.AuthoritativePlan;

        internal int EntryCount => Plan.EntryCount;

        internal long MaximumPngByteCount => _maximumPngByteCount;

        internal int MaximumSidecarByteCount => CaptureFramePngArtifactCodec.MaximumCanonicalByteCount;

        internal int MaximumTraceManifestByteCount => TraceRunManifestCodec.MaximumCanonicalByteCount;

        internal CaptureRunPublicationPathSet PublicationPaths => _authority.PublicationPaths;

        internal CaptureRunRootLayout RootLayout => _authority.RootLayout;

        internal CaptureRunLockLease LockLease => _authority.LockLease;

        internal long TestRunId => _authority.TestRunId;

        internal string RunInitializationId => _authority.RunInitializationId;

        internal string RunManifestContentSha256 => _authority.RunManifestContentSha256;

        internal PngJsonCapturePublicationArtifactInspectionPathSet GetArtifactPaths(int index)
        {
            if (index < 0 || index >= _artifactPaths.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the entry count.");
            }

            return _artifactPaths[index];
        }

        internal bool TryGetArtifactPaths(int index, out PngJsonCapturePublicationArtifactInspectionPathSet paths)
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
        /// O(n), exception-safe full validity: validates the authority once,
        /// confirms the lease liveness, then re-checks the array length and
        /// every path set's index, authority, byte limits, and index-local
        /// path correlation without allocating a proof array.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_authority == null || !_authority.IsValid)
                {
                    return false;
                }

                CaptureRunLockLease lockLease = _authority.LockLease;
                if (lockLease == null || !lockLease.IsCreated)
                {
                    return false;
                }

                return ValidateEntries(_authority, _maximumPngByteCount, _artifactPaths);
            }
        }

        /// <summary>
        /// Full validation plus token issuance: issues a validation token only
        /// after the whole operation validates, so a stale or corrupted
        /// operation never produces a token.
        /// </summary>
        internal bool TryValidate(out ValidationToken token)
        {
            return ValidationToken.TryAcquire(this, out token);
        }

        private static bool ValidateEntries(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount,
            PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPaths)
        {
            if (maximumPngByteCount < 1 || maximumPngByteCount > MaximumAllowedPngByteCount)
            {
                return false;
            }

            PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
            if (plan == null)
            {
                return false;
            }

            int entryCount = plan.EntryCount;

            if (artifactPaths == null || artifactPaths.Length != entryCount)
            {
                return false;
            }

            for (int i = 0; i < entryCount; i++)
            {
                PngJsonCapturePublicationArtifactInspectionPathSet pathSet = artifactPaths[i];
                if (pathSet == null || pathSet.EntryIndex != i || !ReferenceEquals(pathSet.Authority, authority))
                {
                    return false;
                }

                PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(i);
                if (entry == null
                    || entry.PngByteLength > maximumPngByteCount
                    || entry.SidecarByteLength > CaptureFramePngArtifactCodec.MaximumCanonicalByteCount)
                {
                    return false;
                }

                if (!pathSet.IsIndexLocalPathCorrelationIntact())
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Issuance proof minted only after the whole operation validates once.
        /// It binds to the exact operation, authority, authority token, and
        /// artifact path array, and holds the per-index path set references as
        /// a non-public proof so each index can be re-validated in O(1).
        /// </summary>
        /// <remarks>
        /// The token is never held by the operation or by any path set, exposes
        /// no proof array or authority token, and rejects any cross-token,
        /// stale, or corrupted state without throwing.
        /// </remarks>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationArtifactInspectionOperation _operation;
            private readonly PngJsonCapturePublicationArtifactInspectionAuthority _authority;
            private readonly PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken _authorityToken;
            private readonly PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken _authorityTokenProof;
            private readonly PngJsonCapturePublicationArtifactInspectionPathSet[] _artifactPathsArray;
            private readonly PngJsonCapturePublicationArtifactInspectionPathSet[] _proof;
            private readonly long _maximumPngByteCount;

            private ValidationToken(
                PngJsonCapturePublicationArtifactInspectionOperation operation,
                PngJsonCapturePublicationArtifactInspectionAuthority authority,
                PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken authorityToken,
                PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPathsArray,
                PngJsonCapturePublicationArtifactInspectionPathSet[] proof)
            {
                _operation = operation;
                _authority = authority;
                _authorityToken = authorityToken;
                _authorityTokenProof = authorityToken;
                _artifactPathsArray = artifactPathsArray;
                _proof = proof;
                _maximumPngByteCount = operation._maximumPngByteCount;
            }

            /// <summary>
            /// Reports whether this token was issued for the given operation.
            /// The binding is reference-identical.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                return operation != null && ReferenceEquals(_operation, operation);
            }

            /// <summary>
            /// O(1), exception-safe exact-binding check: confirms the exact
            /// operation, authority, authority token binding and lease
            /// liveness, PNG probe bound, and artifact path array reference
            /// without touching any path set element. Never throws and never
            /// exposes the proof array or the authority token.
            /// </summary>
            internal bool IsIssuedForExactBindings(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                if (operation == null || !ReferenceEquals(_operation, operation))
                {
                    return false;
                }

                if (_authority == null || _authorityToken == null || _authorityTokenProof == null
                    || _artifactPathsArray == null || _proof == null)
                {
                    return false;
                }

                if (!ReferenceEquals(_authorityToken, _authorityTokenProof))
                {
                    return false;
                }

                if (operation._maximumPngByteCount != _maximumPngByteCount)
                {
                    return false;
                }

                if (!ReferenceEquals(operation._artifactPaths, _artifactPathsArray))
                {
                    return false;
                }

                if (!ReferenceEquals(operation._authority, _authority))
                {
                    return false;
                }

                return _authorityToken.IsIssuedForExactBindings(_authority);
            }

            /// <summary>
            /// O(1), exception-safe index-local correlation for one index:
            /// confirms the exact operation, authority, authority token, lease
            /// liveness, artifact path array, and path set element reference,
            /// then re-validates the path set against the authority token.
            /// Never throws.
            /// </summary>
            internal bool IsIndexLocalCorrelated(
                PngJsonCapturePublicationArtifactInspectionOperation operation,
                int index)
            {
                if (operation == null || !ReferenceEquals(_operation, operation))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionPathSet[] proof = _proof;
                if (proof == null)
                {
                    return false;
                }

                if (index < 0 || index >= proof.Length)
                {
                    return false;
                }

                if (_authority == null || _authorityToken == null || _authorityTokenProof == null)
                {
                    return false;
                }

                if (operation._maximumPngByteCount != _maximumPngByteCount
                    || !ReferenceEquals(_authorityToken, _authorityTokenProof))
                {
                    return false;
                }

                try
                {
                    PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPaths = operation._artifactPaths;
                    if (artifactPaths == null || !ReferenceEquals(artifactPaths, _artifactPathsArray))
                    {
                        return false;
                    }

                    PngJsonCapturePublicationArtifactInspectionPathSet pathSet = artifactPaths[index];
                    if (pathSet == null || !ReferenceEquals(pathSet, proof[index]))
                    {
                        return false;
                    }

                    if (!ReferenceEquals(operation.Authority, _authority)
                        || !_authorityToken.IsIssuedFor(_authority))
                    {
                        return false;
                    }

                    if (pathSet.EntryIndex != index || !ReferenceEquals(pathSet.Authority, _authority))
                    {
                        return false;
                    }

                    return pathSet.IsValidIndexLocal(_authorityToken);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>
            /// Performs the full operation validation once and mints a token
            /// only on success. The private constructor keeps the token
            /// unfabricable by callers.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationArtifactInspectionOperation operation,
                out ValidationToken token)
            {
                token = null;
                if (operation == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = operation._authority;
                if (authority == null)
                {
                    return false;
                }

                if (!PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken.TryAcquire(
                    authority,
                    out PngJsonCapturePublicationArtifactInspectionAuthority.ValidationToken authorityToken))
                {
                    return false;
                }

                if (!ValidateEntries(authority, operation._maximumPngByteCount, operation._artifactPaths))
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionPathSet[] artifactPaths = operation._artifactPaths;
                int count = artifactPaths.Length;
                PngJsonCapturePublicationArtifactInspectionPathSet[] proof =
                    new PngJsonCapturePublicationArtifactInspectionPathSet[count];
                for (int i = 0; i < count; i++)
                {
                    proof[i] = artifactPaths[i];
                }

                token = new ValidationToken(operation, authority, authorityToken, artifactPaths, proof);
                return true;
            }

            /// <summary>
            /// Throwing mint entry point: full validation and token issuance
            /// happen exactly once.
            /// </summary>
            internal static ValidationToken Acquire(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!TryAcquire(operation, out ValidationToken token))
                {
                    throw new InvalidOperationException("Operation must be fully valid before issuing a validation token.");
                }

                return token;
            }
        }
    }
}
