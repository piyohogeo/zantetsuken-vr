using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Run-lifecycle entry point that selects the format-neutral publication
    /// plan store for both post-freeze persistence and restart recovery.
    /// Legacy PngJson publication contracts are not consulted here.
    /// </summary>
    internal sealed class CaptureEvidenceRunPublicationCoordinator
    {
        private readonly CaptureArtifactFileStore _store;
        private readonly CaptureEvidencePublicationCoordinator _publication;
        private readonly CapturePublicationRecoveryCoordinator _recovery;
        private readonly object _recoveryReceiptAuthority;
        private readonly object _freshPublicationGate;

        internal CaptureEvidenceRunPublicationCoordinator(CaptureArtifactFileStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _publication = new CaptureEvidencePublicationCoordinator(store);
            _recovery = new CapturePublicationRecoveryCoordinator(store);
            _recoveryReceiptAuthority = new object();
            _freshPublicationGate = new object();
        }

        internal CaptureArtifactFileStore Store => _store;

        /// <summary>
        /// Per-call opaque proof minted only inside <see cref="PersistFrozenRun"/>
        /// after the plan was persisted. It binds to this exact coordinator, the
        /// coordinator's private fresh-publication gate, and the exact freeze
        /// receipt, write receipt, draft registry, artifact registry, session,
        /// and lock lease captured before persistence, so a proof cannot be
        /// reused across calls, coordinators, or swapped references.
        /// </summary>
        internal sealed class IssuanceProof
        {
            private readonly CaptureEvidenceRunPublicationCoordinator _coordinator;
            private readonly object _gate;
            private readonly CaptureEvidenceRunFreezeReceipt _freezeReceipt;
            private readonly CapturePublicationPlanWriteReceipt _writeReceipt;
            private readonly CaptureFrameDraftRegistry _drafts;
            private readonly CaptureArtifactRegistry _artifacts;
            private readonly CaptureRunLockIdentityEvidence _lockIdentityEvidence;

            internal IssuanceProof(
                CaptureEvidenceRunPublicationCoordinator coordinator,
                object gate,
                CaptureEvidenceRunFreezeReceipt freezeReceipt,
                CapturePublicationPlanWriteReceipt writeReceipt,
                CaptureFrameDraftRegistry drafts,
                CaptureArtifactRegistry artifacts,
                CaptureRunLockIdentityEvidence lockIdentityEvidence)
            {
                _coordinator = coordinator;
                _gate = gate;
                _freezeReceipt = freezeReceipt;
                _writeReceipt = writeReceipt;
                _drafts = drafts;
                _artifacts = artifacts;
                _lockIdentityEvidence = lockIdentityEvidence;
            }

            internal bool IsMintedFor(
                CaptureEvidenceRunPublicationCoordinator coordinator,
                object gate,
                CaptureEvidenceRunFreezeReceipt freezeReceipt,
                CapturePublicationPlanWriteReceipt writeReceipt,
                CaptureFrameDraftRegistry drafts,
                CaptureArtifactRegistry artifacts,
                CaptureRunLockIdentityEvidence lockIdentityEvidence)
            {
                return coordinator != null
                    && gate != null
                    && freezeReceipt != null
                    && writeReceipt != null
                    && drafts != null
                    && artifacts != null
                    && lockIdentityEvidence != null
                    && ReferenceEquals(_coordinator, coordinator)
                    && ReferenceEquals(_gate, gate)
                    && ReferenceEquals(_freezeReceipt, freezeReceipt)
                    && ReferenceEquals(_writeReceipt, writeReceipt)
                    && ReferenceEquals(_drafts, drafts)
                    && ReferenceEquals(_artifacts, artifacts)
                    && ReferenceEquals(_lockIdentityEvidence, lockIdentityEvidence);
            }
        }

        internal bool IsMintedByThis(
            IssuanceProof proof,
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            CapturePublicationPlanWriteReceipt writeReceipt)
        {
            if (proof == null || freezeReceipt == null || writeReceipt == null)
            {
                return false;
            }

            if (!freezeReceipt.TryGetIssuedBindings(
                    out CaptureFrameDraftRegistry drafts,
                    out CaptureArtifactRegistry artifacts,
                    out CaptureRunInitializationSession session,
                    out CaptureRunLockIdentityEvidence lockIdentityEvidence))
            {
                return false;
            }

            return proof.IsMintedFor(
                this,
                _freshPublicationGate,
                freezeReceipt,
                writeReceipt,
                drafts,
                artifacts,
                lockIdentityEvidence);
        }

        internal CaptureEvidenceFrozenRunPublicationResult PersistFrozenRun(
            CaptureEvidenceRunFreezeReceipt freezeReceipt,
            string runManifestContentHash)
        {
            if (freezeReceipt == null) throw new ArgumentNullException(nameof(freezeReceipt));
            if (runManifestContentHash == null) throw new ArgumentNullException(nameof(runManifestContentHash));
            if (!IsLowerHex(runManifestContentHash, 64))
                throw new ArgumentException("Manifest hash must be 64 lowercase hex characters.", nameof(runManifestContentHash));
            if (!freezeReceipt.IsValid) throw new ArgumentException("Freeze receipt must remain valid.", nameof(freezeReceipt));
            if (!ReferenceEquals(freezeReceipt.RootLayout, _store.RootLayout))
                throw new ArgumentException("Freeze receipt and store must share the exact Run root layout.", nameof(freezeReceipt));

            CaptureFrameDraftRegistry drafts = freezeReceipt.Drafts;
            CaptureArtifactRegistry artifacts = freezeReceipt.Artifacts;
            CaptureRunLockIdentityEvidence lockIdentityEvidence = freezeReceipt.LockIdentityEvidence;

            CapturePublicationPlanWriteReceipt writeReceipt = _publication.BuildAndPersist(
                drafts,
                artifacts,
                freezeReceipt.RunInitializationId,
                runManifestContentHash);

            IssuanceProof proof = new IssuanceProof(
                this,
                _freshPublicationGate,
                freezeReceipt,
                writeReceipt,
                drafts,
                artifacts,
                lockIdentityEvidence);

            return CaptureEvidenceFrozenRunPublicationResult.Create(this, proof, freezeReceipt, writeReceipt);
        }

        internal CaptureEvidenceRunRecoveryInspectionReceipt RecoverAfterRestart(
            CaptureRunInitializationOpenOutcome openOutcome,
            int maximumCanonicalByteCount)
        {
            RequireRecoveryOutcome(openOutcome);
            CapturePublicationRecoverySnapshot snapshot = _recovery.InspectPersisted(_store, maximumCanonicalByteCount);
            if (!IsRecoveryContextFor(openOutcome, snapshot))
                throw new InvalidOperationException("Recovered snapshot is not correlated with the locked Run.");
            return new CaptureEvidenceRunRecoveryInspectionReceipt(
                this, _recoveryReceiptAuthority, openOutcome, snapshot);
        }

        internal CapturePublicationRecoveryDisposition ContinueRecovery(
            CaptureEvidenceRunRecoveryInspectionReceipt inspectionReceipt)
        {
            if (inspectionReceipt == null) throw new ArgumentNullException(nameof(inspectionReceipt));
            if (!inspectionReceipt.IsIssuedFor(this))
                throw new ArgumentException("Inspection receipt must remain valid and be issued by this coordinator.", nameof(inspectionReceipt));
            return _recovery.ExecuteMissing(inspectionReceipt.Snapshot);
        }

        internal bool IsRecoveryContextFor(
            CaptureRunInitializationOpenOutcome openOutcome,
            CapturePublicationRecoverySnapshot snapshot)
        {
            if (openOutcome == null || snapshot == null || !snapshot.IsValid) return false;
            if (!openOutcome.IsValid
                || openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired
                || !ReferenceEquals(openOutcome.RootLayout, _store.RootLayout)) return false;
            CapturePublicationPlan plan = snapshot.Plan;
            return plan != null
                && plan.IsValid
                && plan.TestRunId == openOutcome.TestRunId
                && plan.TestRunId == _store.RootLayout.TestRunId;
        }

        internal bool IsRecoveryReceiptAuthority(object authority) =>
            ReferenceEquals(_recoveryReceiptAuthority, authority);

        private void RequireRecoveryOutcome(CaptureRunInitializationOpenOutcome openOutcome)
        {
            if (openOutcome == null) throw new ArgumentNullException(nameof(openOutcome));
            if (!openOutcome.IsValid
                || openOutcome.Status != CaptureRunInitializationOpenStatus.PublicationRecoveryRequired)
                throw new ArgumentException("Open outcome must hold publication recovery and the OS Run lock.", nameof(openOutcome));
            if (!ReferenceEquals(openOutcome.RootLayout, _store.RootLayout))
                throw new ArgumentException("Open outcome and store must share the exact Run root layout.", nameof(openOutcome));
        }

        private static bool IsLowerHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
