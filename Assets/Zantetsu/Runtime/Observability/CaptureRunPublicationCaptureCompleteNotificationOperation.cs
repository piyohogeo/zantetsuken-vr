using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Capture Run publication capture-complete notification
    /// operation: converts a validated cleanup orchestration result carrying
    /// <c>CaptureCompleteReady</c> into the single stable value a completion
    /// notifier can hand to a downstream lifecycle coordinator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly one read-only reference field — the cleanup
    /// orchestration result — and has no public constructor. Every accessor
    /// forwards a value from that result graph: the cleanup orchestration
    /// result, the cleanup execution result, root layout, lock lease, test run
    /// id, run initialization id, run manifest content SHA-256, capture index
    /// path, disposition, and status are all forwarded rather than duplicated.
    /// </para>
    /// <para>
    /// The constructor validates in a fixed order: reject a null result with
    /// <see cref="ArgumentNullException"/>, reject an invalid result with
    /// <see cref="ArgumentException"/>, then require
    /// <c>CaptureCompleteReady</c> status, an accepted disposition, the full
    /// execution-result-to-batch-to-action-plan-to-recovery-result reference
    /// chain, a valid root layout, a live lock lease, shared root layout and
    /// lock lease across the result, action plan, and inspection operation, a
    /// present authoritative plan, matching test run id, ordinally matching run
    /// initialization id, exactly matching run manifest content SHA-256, a
    /// valid publication path set sharing the root layout, and a capture index
    /// path equal to the authoritative <c>capture.index</c> directly under the
    /// final run root. The field is stored only after every check succeeds.
    /// </para>
    /// <para>
    /// The notification identity is fixed by exactly four values — test run
    /// id, run initialization id, run manifest content SHA-256, and capture
    /// index path — with ordinary string comparison for the three string
    /// values.
    /// </para>
    /// <para>
    /// This operation observes no filesystem: it performs no existence check
    /// and no re-read of <c>capture.index</c>, because the cleanup
    /// orchestration result is the durable index and cleanup-completion
    /// evidence. It owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteNotificationOperation
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult _cleanupResult;

        internal CaptureRunPublicationCaptureCompleteNotificationOperation(
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            if (cleanupResult == null)
            {
                throw new ArgumentNullException(nameof(cleanupResult));
            }

            if (!cleanupResult.IsValid)
            {
                throw new ArgumentException(
                    "Cleanup orchestration result must be valid.",
                    nameof(cleanupResult));
            }

            if (!IsCorrelated(cleanupResult))
            {
                throw new ArgumentException(
                    "Cleanup orchestration result must be correlated with the capture-complete notification graph.",
                    nameof(cleanupResult));
            }

            _cleanupResult = cleanupResult;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _cleanupResult;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _cleanupResult.ExecutionResult;

        internal CaptureRunRootLayout RootLayout => _cleanupResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _cleanupResult.LockIdentityEvidence;

        internal long TestRunId => _cleanupResult.TestRunId;

        internal string RunInitializationId => _cleanupResult.RunInitializationId;

        internal string RunManifestContentSha256 => _cleanupResult.ActionPlan.AuthoritativePlan.RunManifestContentSha256;

        internal string CaptureIndexPath => _cleanupResult.OrchestrationResult.InspectionSnapshot.Decision.Snapshot.Operation.PublicationPaths.CaptureIndexPath;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _cleanupResult.Disposition;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _cleanupResult.Status;

        /// <summary>
        /// Exception-safe recomputation of every correlation this operation
        /// guarantees, from the held graph, without throwing. A released
        /// ownership lease, a corrupted completed-step or receipt sequence, a replaced path set,
        /// a corrupted plan, or a replaced result all converge to
        /// <c>false</c>.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                return _cleanupResult != null
                    && _cleanupResult.IsValid
                    && IsCorrelated(_cleanupResult);
            }
        }

        /// <summary>
        /// Exception-safe correlation checks run after the single full result
        /// validation, in the fixed constructor order. Never throws.
        /// </summary>
        private static bool IsCorrelated(CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            // 3. Status must be CaptureCompleteReady.
            if (cleanupResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
            {
                return false;
            }

            // 4. Disposition must be CommitCaptureIndex or CaptureComplete.
            if (!IsAcceptedDisposition(cleanupResult.Disposition))
            {
                return false;
            }

            // 5. Execution result / batch / action plan / recovery result reference chain.
            CaptureRunPublicationCaptureCompleteCleanupExecutionResult executionResult = cleanupResult.ExecutionResult;
            if (executionResult == null)
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupExecutionBatch batch = cleanupResult.Batch;
            if (batch == null || !ReferenceEquals(executionResult.Batch, batch))
            {
                return false;
            }

            CaptureRunPublicationCaptureCompleteCleanupActionPlan actionPlan = cleanupResult.ActionPlan;
            if (actionPlan == null
                || !ReferenceEquals(executionResult.ActionPlan, actionPlan)
                || !ReferenceEquals(batch.ActionPlan, actionPlan))
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryOrchestrationResult recovery = cleanupResult.OrchestrationResult;
            if (recovery == null
                || !ReferenceEquals(executionResult.OrchestrationResult, recovery)
                || !ReferenceEquals(actionPlan.OrchestrationResult, recovery))
            {
                return false;
            }

            // 6. Root layout must be present and valid.
            CaptureRunRootLayout rootLayout = cleanupResult.RootLayout;
            if (rootLayout == null || !rootLayout.IsValid)
            {
                return false;
            }

            // 7. Lock identity evidence must be present and live.
            CaptureRunLockIdentityEvidence lockIdentityEvidence = cleanupResult.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                return false;
            }

            // 8. Root layout and lock identity evidence must be shared across result, action plan, and inspection operation.
            if (!ReferenceEquals(actionPlan.RootLayout, rootLayout)
                || !ReferenceEquals(actionPlan.LockIdentityEvidence, lockIdentityEvidence))
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryDecision decision = recovery.Decision;
            if (decision == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation inspectionOperation = decision.Operation;
            if (inspectionOperation == null
                || !ReferenceEquals(inspectionOperation.RootLayout, rootLayout)
                || !ReferenceEquals(inspectionOperation.LockIdentityEvidence, lockIdentityEvidence))
            {
                return false;
            }

            // 9. Authoritative plan must be present. Its full entry walk was
            // already performed by the single cleanup result validation, so it
            // is not re-walked here.
            PngJsonCapturePublicationPlan plan = actionPlan.AuthoritativePlan;
            if (plan == null)
            {
                return false;
            }

            // 10. Test run id must match.
            if (cleanupResult.TestRunId != plan.TestRunId
                || inspectionOperation.TestRunId != plan.TestRunId)
            {
                return false;
            }

            // 11. Run initialization id must match ordinally.
            if (!string.Equals(cleanupResult.RunInitializationId, plan.RunInitializationId, StringComparison.Ordinal)
                || !string.Equals(inspectionOperation.RunInitializationId, plan.RunInitializationId, StringComparison.Ordinal))
            {
                return false;
            }

            // 12. Run manifest content SHA-256 must match exactly.
            if (!string.Equals(inspectionOperation.RunManifestContentSha256, plan.RunManifestContentSha256, StringComparison.Ordinal))
            {
                return false;
            }

            // 13. Publication path set must be present, valid, and share the root layout.
            CaptureRunPublicationArtifactInspectionSnapshot artifactSnapshot = recovery.InspectionSnapshot;
            if (artifactSnapshot == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryDecision publicationDecision = artifactSnapshot.Decision;
            if (publicationDecision == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = publicationDecision.Snapshot;
            if (recoverySnapshot == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionOperation recoveryOperation = recoverySnapshot.Operation;
            if (recoveryOperation == null)
            {
                return false;
            }

            CaptureRunPublicationPathSet publicationPaths = recoveryOperation.PublicationPaths;
            if (publicationPaths == null
                || !publicationPaths.IsValid
                || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
            {
                return false;
            }

            // 14. Capture index path must be the authoritative capture.index directly under the final run root.
            string captureIndexPath = publicationPaths.CaptureIndexPath;
            if (captureIndexPath == null)
            {
                return false;
            }

            string expectedCaptureIndexPath;
            try
            {
                expectedCaptureIndexPath = Path.GetFullPath(Path.Combine(rootLayout.FinalRunRoot, "capture.index"));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is IOException)
            {
                return false;
            }

            return string.Equals(captureIndexPath, expectedCaptureIndexPath, StringComparison.Ordinal);
        }

        private static bool IsAcceptedDisposition(CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            return disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex
                || disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
        }
    }
}
