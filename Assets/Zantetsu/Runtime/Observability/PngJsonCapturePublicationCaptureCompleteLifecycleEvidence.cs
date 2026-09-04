using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free evidence that correlates an accepted
    /// PngJson capture-complete notification result with the exact ownership
    /// lease that owns the Run's OS lock, distinguished by whether the
    /// notification result originates from a Fresh frozen-Run seed or a
    /// Recovery open outcome. The freeze receipt and the recovery open outcome
    /// are non-owning, non-disposable references that only distinguish the
    /// provenance kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the notification
    /// result, the fresh freeze receipt, the recovery open outcome, and the
    /// ownership lease — and has no public constructor. The Fresh and Recovery
    /// provenance references are mutually exclusive: <see cref="FromFresh"/>
    /// stores only the freeze receipt and leaves the open outcome null, while
    /// <see cref="FromRecovery"/> stores only the open outcome and leaves the
    /// freeze receipt null. <see cref="Kind"/> is derived from that exclusive
    /// state, never from a duplicated field.
    /// </para>
    /// <para>
    /// Each factory first null-checks every input with the matching
    /// <see cref="ArgumentNullException"/>, then requires a fully valid
    /// notification result exactly once. The notification result validates the
    /// upstream evidence the notification and cleanup boundaries need — the
    /// cleanup step list, notification receipt, plan and path-set correlation,
    /// and the accepted status and disposition. On top of that, the Fresh
    /// factory re-checks the exact freeze receipt it directly holds and
    /// exposes (an O(1) fixed-reference and state check with no entry scan),
    /// and then both factories run only O(1) correlation on the same
    /// notification result instance: the exact authority kind, the exact
    /// provenance reference, the root layout, lock identity evidence, and run
    /// identity correlation, and the exact live ownership lease. Evidence is
    /// issued only after every check succeeds.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the full correlation from the held
    /// values without throwing. A released ownership lease, a partially
    /// released ownership lease, a reflection-replaced owner, freeze receipt,
    /// open outcome, lock identity evidence, or notification result, or a
    /// provenance mismatch all converge to <c>false</c>. The evidence never
    /// disposes or mutates the freeze receipt, session, open outcome,
    /// registries, or ownership lease.
    /// </para>
    /// <para>
    /// This type performs no filesystem, registry, or cleanup backend work and
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteLifecycleEvidence
    {
        private readonly PngJsonCapturePublicationCaptureCompleteNotificationResult _notificationResult;
        private readonly CaptureEvidenceRunFreezeReceipt _freezeReceipt;
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private PngJsonCapturePublicationCaptureCompleteLifecycleEvidence(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _notificationResult = notificationResult;
            _freezeReceipt = freezeReceipt;
            _openOutcome = openOutcome;
            _ownershipLease = ownershipLease;
        }

        /// <summary>
        /// Validated factory for the Fresh path: the notification result must be
        /// valid and carry a <c>FreshFrozenRun</c> authority, the exact freeze
        /// receipt must be reference-equal to the fresh seed's freeze receipt,
        /// and the ownership lease must be the exact live lease the notification
        /// result's lock identity evidence was issued for.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteLifecycleEvidence FromFresh(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (notificationResult == null)
            {
                throw new ArgumentNullException(nameof(notificationResult));
            }

            if (freezeReceipt == null)
            {
                throw new ArgumentNullException(nameof(freezeReceipt));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (!notificationResult.IsValid)
            {
                throw new ArgumentException(
                    "Notification result must be valid.",
                    nameof(notificationResult));
            }

            if (!IsFreshCorrelated(notificationResult, freezeReceipt, ownershipLease))
            {
                throw new ArgumentException(
                    "Notification result, freeze receipt, and ownership lease must be correlated.",
                    nameof(freezeReceipt));
            }

            return new PngJsonCapturePublicationCaptureCompleteLifecycleEvidence(
                notificationResult, freezeReceipt, null, ownershipLease);
        }

        /// <summary>
        /// Validated factory for the Recovery path: the notification result must
        /// be valid and carry a <c>RecoveryDecision</c> authority, the open
        /// outcome must be reference-equal to the notification graph's
        /// provenance open outcome, and the ownership lease must be the exact
        /// live lease the notification result's lock identity evidence was
        /// issued for.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteLifecycleEvidence FromRecovery(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult,
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (notificationResult == null)
            {
                throw new ArgumentNullException(nameof(notificationResult));
            }

            if (openOutcome == null)
            {
                throw new ArgumentNullException(nameof(openOutcome));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (!notificationResult.IsValid)
            {
                throw new ArgumentException(
                    "Notification result must be valid.",
                    nameof(notificationResult));
            }

            if (!IsRecoveryCorrelated(notificationResult, openOutcome, ownershipLease))
            {
                throw new ArgumentException(
                    "Notification result, open outcome, and ownership lease must be correlated.",
                    nameof(openOutcome));
            }

            return new PngJsonCapturePublicationCaptureCompleteLifecycleEvidence(
                notificationResult, null, openOutcome, ownershipLease);
        }

        /// <summary>
        /// Derived from the exclusive held reference shape, never stored.
        /// </summary>
        internal CaptureRunPublicationCaptureCompleteLifecycleOwnerKind Kind
        {
            get
            {
                if (_freezeReceipt != null && _openOutcome == null)
                {
                    return CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.FreshSession;
                }

                if (_freezeReceipt == null && _openOutcome != null)
                {
                    return CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.RecoveryOpenOutcome;
                }

                return CaptureRunPublicationCaptureCompleteLifecycleOwnerKind.None;
            }
        }

        internal PngJsonCapturePublicationCaptureCompleteNotificationResult NotificationResult => _notificationResult;

        internal CaptureEvidenceRunFreezeReceipt FreezeReceipt => _freezeReceipt;

        internal CaptureRunInitializationSession RunSession =>
            _freezeReceipt != null ? _freezeReceipt.RunSession : null;

        internal CaptureFrameDraftRegistry Drafts =>
            _freezeReceipt != null ? _freezeReceipt.Drafts : null;

        internal CaptureArtifactRegistry Artifacts =>
            _freezeReceipt != null ? _freezeReceipt.Artifacts : null;

        internal CaptureRunInitializationOpenOutcome OpenOutcome => _openOutcome;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunRootLayout RootLayout => _notificationResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _notificationResult.LockIdentityEvidence;

        internal long TestRunId => _notificationResult.TestRunId;

        internal string RunInitializationId => _notificationResult.RunInitializationId;

        internal string RunManifestContentSha256 => _notificationResult.RunManifestContentSha256;

        internal string CaptureIndexPath => _notificationResult.CaptureIndexPath;

        /// <summary>
        /// Exception-safe recomputation from the currently held graph, without
        /// throwing. The notification result is fully re-validated first, and
        /// then the path-specific shared correlation predicate is re-run —
        /// including, on the Fresh path, the O(1) re-check of the exact freeze
        /// receipt this evidence directly holds and exposes. Any corrupted or
        /// replaced value converges to <c>false</c>.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_notificationResult == null)
                {
                    return false;
                }

                if (_freezeReceipt != null && _openOutcome == null)
                {
                    return _notificationResult.IsValid
                        && IsFreshCorrelated(_notificationResult, _freezeReceipt, _ownershipLease);
                }

                if (_freezeReceipt == null && _openOutcome != null)
                {
                    return _notificationResult.IsValid
                        && IsRecoveryCorrelated(_notificationResult, _openOutcome, _ownershipLease);
                }

                return false;
            }
        }

        /// <summary>
        /// Shared Fresh correlation predicate used by both the factory and
        /// <see cref="IsValid"/>. After the notification result's full
        /// validation it requires the Fresh authority kind and the exact freeze
        /// receipt reference, re-checks that exact freeze receipt's own
        /// validity (a fixed-reference and state check with no entry scan, so
        /// the predicate stays O(1)), and then correlates the root layout, lock
        /// identity evidence, run identity, and the exact live ownership lease.
        /// It never re-validates the fresh seed. Never throws.
        /// </summary>
        private static bool IsFreshCorrelated(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            try
            {
                if (notificationResult == null || freezeReceipt == null || ownershipLease == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = GetAuthority(notificationResult);
                if (authority == null
                    || authority.Kind != PngJsonCapturePublicationArtifactInspectionAuthorityKind.FreshFrozenRun)
                {
                    return false;
                }

                PngJsonCaptureFrozenRunArtifactInspectionSeed freshSeed = authority.FreshSeed;
                if (freshSeed == null)
                {
                    return false;
                }

                if (!ReferenceEquals(freshSeed.FreezeReceipt, freezeReceipt))
                {
                    return false;
                }

                // This evidence directly holds and exposes the freeze receipt
                // through Drafts and Artifacts, so the receipt's own validity
                // must hold even though the notification result does not
                // re-check it. This is an O(1) fixed-reference and state check
                // with no entry scan.
                if (!freezeReceipt.IsValid)
                {
                    return false;
                }

                if (freshSeed.RootLayout == null
                    || !ReferenceEquals(freshSeed.RootLayout, notificationResult.RootLayout))
                {
                    return false;
                }

                if (!ReferenceEquals(freshSeed.LockIdentityEvidence, notificationResult.LockIdentityEvidence))
                {
                    return false;
                }

                if (freshSeed.TestRunId != notificationResult.TestRunId
                    || !string.Equals(
                        freshSeed.RunInitializationId,
                        notificationResult.RunInitializationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                if (!ReferenceEquals(freezeReceipt.LockIdentityEvidence, freshSeed.LockIdentityEvidence))
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = notificationResult.LockIdentityEvidence;
                if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
                {
                    return false;
                }

                if (!lockIdentityEvidence.IsIssuedFor(ownershipLease))
                {
                    return false;
                }

                if (!ownershipLease.IsCreated)
                {
                    return false;
                }

                if (lockIdentityEvidence.LockPathSet == null
                    || !ReferenceEquals(lockIdentityEvidence.LockPathSet, ownershipLease.LockPathSet)
                    || lockIdentityEvidence.RootLayout == null
                    || !ReferenceEquals(lockIdentityEvidence.RootLayout, freshSeed.RootLayout))
                {
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Shared Recovery correlation predicate used by both the factory and
        /// <see cref="IsValid"/>. It requires only O(1) correlation after the
        /// notification result's full validation: the Recovery authority kind,
        /// the exact provenance open outcome reference, the cheap
        /// publication-recovery-required shape (status and no session), the
        /// root layout, lock identity evidence, and run identity correlation,
        /// and the exact live ownership lease. It never re-validates the open
        /// outcome. Never throws.
        /// </summary>
        private static bool IsRecoveryCorrelated(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult,
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            try
            {
                if (notificationResult == null || openOutcome == null || ownershipLease == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = GetAuthority(notificationResult);
                if (authority == null
                    || authority.Kind != PngJsonCapturePublicationArtifactInspectionAuthorityKind.RecoveryDecision)
                {
                    return false;
                }

                if (!ReferenceEquals(GetProvenanceOpenOutcome(authority), openOutcome))
                {
                    return false;
                }

                if (openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
                {
                    return false;
                }

                if (openOutcome.Session != null)
                {
                    return false;
                }

                CaptureRunInitializationRecoveryOrchestrationResult orchestrationResult = openOutcome.OrchestrationResult;
                if (orchestrationResult == null)
                {
                    return false;
                }

                if (!ReferenceEquals(orchestrationResult.RootLayout, notificationResult.RootLayout))
                {
                    return false;
                }

                if (orchestrationResult.TestRunId != notificationResult.TestRunId)
                {
                    return false;
                }

                if (!string.Equals(
                        orchestrationResult.RunInitializationId,
                        notificationResult.RunInitializationId,
                        StringComparison.Ordinal))
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = notificationResult.LockIdentityEvidence;
                if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
                {
                    return false;
                }

                if (!lockIdentityEvidence.IsIssuedFor(ownershipLease))
                {
                    return false;
                }

                if (!ReferenceEquals(orchestrationResult.LockIdentityEvidence, lockIdentityEvidence))
                {
                    return false;
                }

                if (!ownershipLease.IsCreated)
                {
                    return false;
                }

                if (lockIdentityEvidence.LockPathSet == null
                    || openOutcome.LockPathSet == null
                    || !ReferenceEquals(lockIdentityEvidence.LockPathSet, openOutcome.LockPathSet)
                    || !ReferenceEquals(ownershipLease.LockPathSet, openOutcome.LockPathSet))
                {
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static PngJsonCapturePublicationArtifactInspectionAuthority GetAuthority(
            PngJsonCapturePublicationCaptureCompleteNotificationResult notificationResult)
        {
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult = notificationResult.CleanupResult;
            return cleanupResult != null ? cleanupResult.Authority : null;
        }

        private static CaptureRunInitializationOpenOutcome GetProvenanceOpenOutcome(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            CaptureRunPublicationRecoveryDecision decision = authority.RecoveryDecision;
            if (decision == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = snapshot.Operation;
            if (operation == null)
            {
                return null;
            }

            return operation.OpenOutcome;
        }
    }
}
