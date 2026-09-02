using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free evidence that correlates an accepted
    /// capture-complete notification result with the exact ownership lease
    /// that owns the Run's OS lock. The fresh session and the recovery open
    /// outcome are non-owning, non-disposable references that only distinguish
    /// the provenance kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly four read-only reference fields — the
    /// notification result, the fresh freeze receipt, the recovery open
    /// outcome, and the ownership lease — and has no public constructor. The
    /// recovery factory accepts the exact provenance open outcome and the exact
    /// ownership lease, and rejects a fresh receipt, so no caller can inject an
    /// arbitrary four-reference combination. The fresh factory is not yet
    /// accepting because no fresh publication provenance chain exists.
    /// <see cref="Kind"/> is derived from the exclusive owner state, never from
    /// a duplicated field.
    /// </para>
    /// <para>
    /// The fresh factory rejects: the capture-complete notification result
    /// graph is recovery-originated and no reference chain yet connects a
    /// freeze receipt or draft/artifact registry through publication and
    /// cleanup to a notification result, so <see cref="FromFresh"/> throws
    /// <see cref="NotSupportedException"/> for every non-null argument pair
    /// until that provenance chain exists. The recovery factory validates:
    /// null notification result, null open outcome, null ownership lease, a
    /// valid notification result (whose full validation already proves the
    /// publication recovery operation and its open outcome), accepted status
    /// and disposition, the exact provenance open outcome (reference-equal to
    /// the notification graph's inspection operation outcome), and then only
    /// O(1) correlation on that same instance: a live open outcome,
    /// publication-recovery-required status, no session, shared root layout,
    /// matching ids, the same lock identity evidence, the same ownership
    /// lease, and the same lock path set.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the full correlation from the held
    /// values without throwing. A released ownership lease, a re-emerged
    /// registry reservation, or a reflection-replaced owner, open outcome, or
    /// notification result all converge to <c>false</c>. The evidence never
    /// disposes or mutates the registries, session, open outcome, or ownership
    /// lease.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteLifecycleEvidence
    {
        private readonly CaptureRunPublicationCaptureCompleteNotificationResult _notificationResult;
        private readonly CaptureEvidenceRunFreezeReceipt _freezeReceipt;
        private readonly CaptureRunInitializationOpenOutcome _openOutcome;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        private CaptureRunPublicationCaptureCompleteLifecycleEvidence(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            _notificationResult = notificationResult;
            _freezeReceipt = freezeReceipt;
            _openOutcome = openOutcome;
            _ownershipLease = ownershipLease;
        }

        internal static CaptureRunPublicationCaptureCompleteLifecycleEvidence FromFresh(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
            CaptureEvidenceRunFreezeReceipt freezeReceipt)
        {
            if (notificationResult == null)
            {
                throw new ArgumentNullException(nameof(notificationResult));
            }

            if (freezeReceipt == null)
            {
                throw new ArgumentNullException(nameof(freezeReceipt));
            }

            // Fresh provenance is not yet implemented: the capture-complete
            // notification result graph is recovery-originated, and no
            // reference chain connects a freeze receipt or draft/artifact
            // registry through publication and cleanup to a notification
            // result. Until that chain exists, no fresh evidence is accepted.
            throw new NotSupportedException(
                "Fresh capture-complete provenance is not yet implemented.");
        }

        internal static CaptureRunPublicationCaptureCompleteLifecycleEvidence FromRecovery(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
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

            if (!notificationResult.IsValid || !IsRecoveryCorrelated(notificationResult, openOutcome, ownershipLease))
            {
                throw new ArgumentException(
                    "Notification result, open outcome, and ownership lease must be correlated.",
                    nameof(openOutcome));
            }

            return new CaptureRunPublicationCaptureCompleteLifecycleEvidence(notificationResult, null, openOutcome, ownershipLease);
        }

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

        internal CaptureRunPublicationCaptureCompleteNotificationResult NotificationResult => _notificationResult;

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
                    return _notificationResult.IsValid && IsFreshCorrelated(_notificationResult, _freezeReceipt);
                }

                if (_freezeReceipt == null && _openOutcome != null)
                {
                    return _notificationResult.IsValid && IsRecoveryCorrelated(_notificationResult, _openOutcome, _ownershipLease);
                }

                return false;
            }
        }

        private static bool IsFreshCorrelated(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
            CaptureEvidenceRunFreezeReceipt freezeReceipt)
        {
            // Fresh provenance is not yet implemented: no reference chain
            // connects a freeze receipt or draft/artifact registry through
            // publication and cleanup to a notification result. Until that
            // chain exists, no fresh pairing is correlated.
            return false;
        }

        private static bool IsRecoveryCorrelated(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult,
            CaptureRunInitializationOpenOutcome openOutcome,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (notificationResult == null || openOutcome == null || ownershipLease == null)
            {
                return false;
            }

            if (notificationResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
            {
                return false;
            }

            if (!IsAcceptedDisposition(notificationResult.Disposition))
            {
                return false;
            }

            // The notification result's full validation already proves the
            // publication recovery operation and its open outcome, so require
            // the exact provenance instance first and then only O(1)
            // correlation on that same instance.
            if (!ReferenceEquals(GetProvenanceOpenOutcome(notificationResult), openOutcome))
            {
                return false;
            }

            if (!openOutcome.IsValid)
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

            if (!string.Equals(orchestrationResult.RunInitializationId, notificationResult.RunInitializationId, StringComparison.Ordinal))
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

        private static CaptureRunInitializationOpenOutcome GetProvenanceOpenOutcome(
            CaptureRunPublicationCaptureCompleteNotificationResult notificationResult)
        {
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanupResult = notificationResult.CleanupResult;
            if (cleanupResult == null)
            {
                return null;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult recoveryResult = cleanupResult.OrchestrationResult;
            if (recoveryResult == null)
            {
                return null;
            }

            CaptureRunPublicationArtifactInspectionSnapshot artifactSnapshot = recoveryResult.InspectionSnapshot;
            if (artifactSnapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryDecision decision = artifactSnapshot.Decision;
            if (decision == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = decision.Snapshot;
            if (recoverySnapshot == null)
            {
                return null;
            }

            CaptureRunPublicationRecoveryInspectionOperation operation = recoverySnapshot.Operation;
            if (operation == null)
            {
                return null;
            }

            return operation.OpenOutcome;
        }

        private static bool IsAcceptedDisposition(CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            return disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex
                || disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
        }
    }
}
