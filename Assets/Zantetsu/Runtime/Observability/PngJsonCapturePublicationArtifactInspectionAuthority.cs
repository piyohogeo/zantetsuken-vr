using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free value that fixes the single exclusive owner
    /// of the authoritative publication plan handed to artifact inspection:
    /// either the Recovery decision or the Fresh frozen-Run seed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the recovery
    /// decision and the fresh seed — and has no public or internal
    /// constructor. It duplicates no plan, path set, root layout, lease,
    /// identifier, hash, or disposition; every accessor forwards from the held
    /// graph, and <see cref="Kind"/> is derived from the exclusive state of the
    /// two references rather than stored as a field.
    /// </para>
    /// <para>
    /// Each static factory validates its single input once as the sole
    /// full-validation boundary, then performs only structural guards and
    /// reference/value correlation before assigning fields through the private
    /// assignment constructor, so neither a recovery decision nor a fresh seed
    /// can be substituted after construction.
    /// </para>
    /// <para>
    /// This authority does not observe any filesystem path, does not mint a
    /// recovery snapshot, and does not run artifact inspection. A Fresh
    /// authority does not claim that any publication plan bytes were observed
    /// as a PngJson document, that artifact inspection completed, that
    /// artifacts exist, match, or were published, or that capture index,
    /// cleanup, or notification completed. A Recovery authority extends no
    /// proof beyond what the held decision already establishes.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactInspectionAuthority
    {
        private readonly CaptureRunPublicationRecoveryDecision _recoveryDecision;
        private readonly PngJsonCaptureFrozenRunArtifactInspectionSeed _freshSeed;

        private PngJsonCapturePublicationArtifactInspectionAuthority(
            CaptureRunPublicationRecoveryDecision recoveryDecision,
            PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed)
        {
            _recoveryDecision = recoveryDecision;
            _freshSeed = freshSeed;
        }

        /// <summary>
        /// Validated factory for the Recovery path. The recovery decision is
        /// validated once as the sole full Recovery boundary, then only
        /// structural guards and reference/value correlation run before the
        /// fields are assigned.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactInspectionAuthority FromRecovery(
            CaptureRunPublicationRecoveryDecision recoveryDecision)
        {
            if (recoveryDecision == null)
            {
                throw new ArgumentNullException(nameof(recoveryDecision));
            }

            if (!IsRecoveryDecisionCorrelated(recoveryDecision))
            {
                throw new ArgumentException(
                    "Recovery decision must hold an authoritative plan correlated with its publication paths and lease.",
                    nameof(recoveryDecision));
            }

            return new PngJsonCapturePublicationArtifactInspectionAuthority(recoveryDecision, null);
        }

        /// <summary>
        /// Validated factory for the Fresh path. The fresh seed is validated
        /// once as the sole full Fresh boundary, then only structural guards
        /// and reference/value correlation run before the fields are assigned.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactInspectionAuthority FromFresh(
            PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed)
        {
            if (freshSeed == null)
            {
                throw new ArgumentNullException(nameof(freshSeed));
            }

            if (!IsFreshSeedCorrelated(freshSeed))
            {
                throw new ArgumentException(
                    "Fresh seed must hold an authoritative plan correlated with its binding, session, and lease.",
                    nameof(freshSeed));
            }

            return new PngJsonCapturePublicationArtifactInspectionAuthority(null, freshSeed);
        }

        /// <summary>
        /// Derived from the exclusive state of the two references, never stored.
        /// </summary>
        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind Kind
        {
            get
            {
                bool recovery = _recoveryDecision != null;
                bool fresh = _freshSeed != null;

                if (recovery && !fresh)
                {
                    return PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision;
                }

                if (fresh && !recovery)
                {
                    return PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun;
                }

                return PngJsonCapturePublicationArtifactInspectionAuthorityKind.None;
            }
        }

        internal bool IsRecovery => Kind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision;

        internal bool IsFresh => Kind == PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun;

        internal CaptureRunPublicationRecoveryDecision RecoveryDecision => _recoveryDecision;

        internal PngJsonCaptureFrozenRunArtifactInspectionSeed FreshSeed => _freshSeed;

        internal PngJsonCapturePublicationPlan AuthoritativePlan
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.AuthoritativePlan;
                }

                if (IsFresh)
                {
                    return _freshSeed.AuthoritativePlan;
                }

                return null;
            }
        }

        internal CaptureRunPublicationRecoveryDisposition Disposition
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.Disposition;
                }

                if (IsFresh)
                {
                    return CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative;
                }

                return CaptureRunPublicationRecoveryDisposition.None;
            }
        }

        internal CaptureRunPublicationPathSet PublicationPaths
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.Snapshot.Operation.PublicationPaths;
                }

                if (IsFresh)
                {
                    return _freshSeed.PublicationPaths;
                }

                return null;
            }
        }

        internal CaptureRunRootLayout RootLayout
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.RootLayout;
                }

                if (IsFresh)
                {
                    return _freshSeed.RootLayout;
                }

                return null;
            }
        }

        internal CaptureRunLockLease LockLease
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.Snapshot.Operation.LockLease;
                }

                if (IsFresh)
                {
                    return _freshSeed.LockLease;
                }

                return null;
            }
        }

        internal long TestRunId
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.TestRunId;
                }

                if (IsFresh)
                {
                    return _freshSeed.TestRunId;
                }

                return 0L;
            }
        }

        internal string RunInitializationId
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.RunInitializationId;
                }

                if (IsFresh)
                {
                    return _freshSeed.RunInitializationId;
                }

                return null;
            }
        }

        internal string RunManifestContentSha256
        {
            get
            {
                if (IsRecovery)
                {
                    return _recoveryDecision.AuthoritativePlan.RunManifestContentSha256;
                }

                if (IsFresh)
                {
                    return _freshSeed.RunManifestContentSha256;
                }

                return null;
            }
        }

        /// <summary>
        /// Derives <see cref="Kind"/> from the exclusive state and re-validates
        /// only the held path, without throwing and without generating any
        /// plan, path set, snapshot, or array.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (IsRecovery)
                {
                    return IsRecoveryDecisionCorrelated(_recoveryDecision);
                }

                if (IsFresh)
                {
                    return IsFreshSeedCorrelated(_freshSeed);
                }

                return false;
            }
        }

        private static bool IsRecoveryDecisionCorrelated(CaptureRunPublicationRecoveryDecision recoveryDecision)
        {
            if (recoveryDecision == null)
            {
                return false;
            }

            if (!recoveryDecision.IsValid)
            {
                return false;
            }

            CaptureRunPublicationRecoveryDisposition disposition = recoveryDecision.Disposition;
            if (disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative
                && disposition != CaptureRunPublicationRecoveryDisposition.CaptureIndexAuthoritative)
            {
                return false;
            }

            PngJsonCapturePublicationPlan plan = recoveryDecision.AuthoritativePlan;
            if (plan == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = recoveryDecision.Snapshot;
            if (snapshot == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;
            if (operation == null)
            {
                return false;
            }

            CaptureRunPublicationPathSet publicationPaths = operation.PublicationPaths;
            if (publicationPaths == null)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = recoveryDecision.RootLayout;
            if (rootLayout == null)
            {
                return false;
            }

            CaptureRunLockLease lockLease = operation.LockLease;
            if (lockLease == null || !lockLease.IsCreated)
            {
                return false;
            }

            if (!ReferenceEquals(rootLayout, operation.RootLayout)
                || !ReferenceEquals(rootLayout, publicationPaths.RootLayout))
            {
                return false;
            }

            if (!ReferenceEquals(lockLease, operation.LockLease))
            {
                return false;
            }

            if (recoveryDecision.TestRunId != operation.TestRunId
                || recoveryDecision.TestRunId != plan.TestRunId)
            {
                return false;
            }

            if (!string.Equals(recoveryDecision.RunInitializationId, operation.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(recoveryDecision.RunInitializationId, plan.RunInitializationId, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        private static bool IsFreshSeedCorrelated(PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed)
        {
            if (freshSeed == null)
            {
                return false;
            }

            if (!freshSeed.IsValid)
            {
                return false;
            }

            if (freshSeed.Disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative)
            {
                return false;
            }

            PngJsonCapturePublicationPlan plan = freshSeed.AuthoritativePlan;
            if (plan == null)
            {
                return false;
            }

            CaptureRunPublicationPathSet publicationPaths = freshSeed.PublicationPaths;
            if (publicationPaths == null)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = freshSeed.RootLayout;
            if (rootLayout == null)
            {
                return false;
            }

            CaptureRunLockLease lockLease = freshSeed.LockLease;
            if (lockLease == null || !lockLease.IsCreated)
            {
                return false;
            }

            PngJsonCaptureFrozenRunPublicationPlanBinding binding = freshSeed.PlanBinding;
            CaptureEvidenceFrozenRunPublicationResult frozen = freshSeed.FrozenPublicationResult;
            CaptureRunInitializationSession session = freshSeed.RunSession;
            if (binding == null || frozen == null || session == null)
            {
                return false;
            }

            if (!ReferenceEquals(rootLayout, binding.RootLayout)
                || !ReferenceEquals(rootLayout, frozen.RootLayout))
            {
                return false;
            }

            if (!session.OwnsLockLease(lockLease))
            {
                return false;
            }

            if (!ReferenceEquals(plan, binding.LegacyPlan))
            {
                return false;
            }

            CapturePublicationPlan genericPlan = freshSeed.GenericPlan;
            if (genericPlan == null)
            {
                return false;
            }

            if (freshSeed.TestRunId != binding.TestRunId
                || freshSeed.TestRunId != frozen.TestRunId
                || freshSeed.TestRunId != genericPlan.TestRunId
                || freshSeed.TestRunId != plan.TestRunId
                || freshSeed.TestRunId != rootLayout.TestRunId)
            {
                return false;
            }

            if (!string.Equals(freshSeed.RunInitializationId, binding.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(freshSeed.RunInitializationId, frozen.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(freshSeed.RunInitializationId, genericPlan.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(freshSeed.RunInitializationId, plan.RunInitializationId, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(freshSeed.RunManifestContentSha256, binding.RunManifestContentHash, StringComparison.Ordinal)
                || !string.Equals(freshSeed.RunManifestContentSha256, frozen.RunManifestContentHash, StringComparison.Ordinal)
                || !string.Equals(freshSeed.RunManifestContentSha256, genericPlan.RunManifestContentHash, StringComparison.Ordinal)
                || !string.Equals(freshSeed.RunManifestContentSha256, plan.RunManifestContentSha256, StringComparison.Ordinal))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Issuance proof minted only after the whole authority is fully
        /// validated once. It captures a linear snapshot of the authoritative
        /// plan's entry references together with the exact plan, publication
        /// path set, root layout, and lock lease, so each entry can later be
        /// validated in O(1) without re-running the full authority validation
        /// or scanning the plan again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The token is bound to its exact authority by reference, is never
        /// held as a field by the authority or by any path set, and exposes no
        /// proof array or entry reference list. A token presented against a
        /// different authority, or a stale token whose lease has been
        /// released, is rejected without throwing.
        /// </para>
        /// </remarks>
        internal sealed class ValidationToken
        {
            private readonly PngJsonCapturePublicationArtifactInspectionAuthority _authority;
            private readonly CaptureRunLockLease _lease;
            private readonly PngJsonCapturePublicationPlan _plan;
            private readonly CaptureRunPublicationPathSet _publicationPaths;
            private readonly CaptureRunRootLayout _rootLayout;
            private readonly PngJsonCapturePublicationPlanEntry[] _entries;

            private ValidationToken(
                PngJsonCapturePublicationArtifactInspectionAuthority authority,
                CaptureRunLockLease lease,
                PngJsonCapturePublicationPlan plan,
                CaptureRunPublicationPathSet publicationPaths,
                CaptureRunRootLayout rootLayout,
                PngJsonCapturePublicationPlanEntry[] entries)
            {
                _authority = authority;
                _lease = lease;
                _plan = plan;
                _publicationPaths = publicationPaths;
                _rootLayout = rootLayout;
                _entries = entries;
            }

            /// <summary>
            /// Reports whether this token was issued for the given authority.
            /// The binding is reference-identical.
            /// </summary>
            internal bool IsIssuedFor(PngJsonCapturePublicationArtifactInspectionAuthority authority)
            {
                return authority != null && ReferenceEquals(_authority, authority);
            }

            /// <summary>
            /// O(1), exception-safe index-local correlation: confirms this
            /// token is bound to the exact authority, the lease is still live,
            /// the plan, publication path set, root layout, and lease are the
            /// exact references captured at issuance, and the entry at the
            /// given index is the exact entry captured at issuance.
            /// </summary>
            internal bool IsIndexLocalCorrelated(
                PngJsonCapturePublicationArtifactInspectionAuthority authority,
                int entryIndex)
            {
                if (authority == null || !ReferenceEquals(_authority, authority))
                {
                    return false;
                }

                if (entryIndex < 0 || entryIndex >= _entries.Length)
                {
                    return false;
                }

                if (_lease == null || !_lease.IsCreated)
                {
                    return false;
                }

                try
                {
                    PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
                    return ReferenceEquals(plan, _plan)
                        && ReferenceEquals(authority.PublicationPaths, _publicationPaths)
                        && ReferenceEquals(authority.RootLayout, _rootLayout)
                        && ReferenceEquals(authority.LockLease, _lease)
                        && ReferenceEquals(plan.GetEntry(entryIndex), _entries[entryIndex]);
                }
                catch (Exception)
                {
                    return false;
                }
            }

            /// <summary>
            /// Performs the full authority validation exactly once, captures
            /// the linear entry snapshot, and mints a token only on success.
            /// The private constructor keeps the token unfabricable by
            /// callers.
            /// </summary>
            internal static bool TryAcquire(
                PngJsonCapturePublicationArtifactInspectionAuthority authority,
                out ValidationToken token)
            {
                token = null;
                if (authority == null || !authority.IsValid)
                {
                    return false;
                }

                PngJsonCapturePublicationPlan plan = authority.AuthoritativePlan;
                CaptureRunPublicationPathSet publicationPaths = authority.PublicationPaths;
                CaptureRunRootLayout rootLayout = authority.RootLayout;
                CaptureRunLockLease lease = authority.LockLease;

                int count = plan.EntryCount;
                PngJsonCapturePublicationPlanEntry[] entries = new PngJsonCapturePublicationPlanEntry[count];
                for (int i = 0; i < count; i++)
                {
                    entries[i] = plan.GetEntry(i);
                }

                token = new ValidationToken(authority, lease, plan, publicationPaths, rootLayout, entries);
                return true;
            }

            /// <summary>
            /// Throwing mint entry point used by the normal path-set
            /// constructor: full validation and token issuance happen exactly
            /// once.
            /// </summary>
            internal static ValidationToken Acquire(PngJsonCapturePublicationArtifactInspectionAuthority authority)
            {
                if (authority == null)
                {
                    throw new ArgumentNullException(nameof(authority));
                }

                if (!TryAcquire(authority, out ValidationToken token))
                {
                    throw new InvalidOperationException("Authority must be fully valid before issuing a validation token.");
                }

                return token;
            }
        }
    }
}
