using System;
using System.IO;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable PngJson capture publication capture-complete notification
    /// operation: converts a validated cleanup orchestration result carrying
    /// <c>CaptureCompleteReady</c> into the single stable value a completion
    /// notifier can hand to a downstream lifecycle coordinator.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly two read-only reference fields — the exact cleanup
    /// orchestration result and the exact validation token issued for it — and
    /// has no public constructor. The held token is never exposed. Every
    /// accessor forwards a value from the cleanup result graph: the cleanup
    /// result, the cleanup execution result, root layout, lock identity
    /// evidence, test run id, run initialization id, run manifest content
    /// SHA-256, capture index path, disposition, and status are all forwarded
    /// rather than duplicated.
    /// </para>
    /// <para>
    /// <see cref="Create"/> validates in a fixed order: reject a null result
    /// with <see cref="ArgumentNullException"/>, issue the result's validation
    /// token exactly once, then require the notification-specific correlation.
    /// The operation is stored only after every check succeeds.
    /// <see cref="IsValid"/> re-verifies the cleanup result with the held token
    /// — without re-issuing it — and re-runs the same notification-specific
    /// correlation predicate, so a released lease, corrupted completed step or
    /// receipt, corrupted plan or path set, or replaced result converges to
    /// <c>false</c> without throwing.
    /// </para>
    /// <para>
    /// This operation observes no filesystem: it performs no existence check
    /// and no re-read of <c>capture.index</c>, because the cleanup
    /// orchestration result is the durable index and cleanup-completion
    /// evidence. Only the existing publication path set and pure
    /// <see cref="Path"/> string operations are used. It owns, mutates, and
    /// disposes nothing and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteNotificationOperation
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult _cleanupResult;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult.ValidationToken _token;

        private PngJsonCapturePublicationCaptureCompleteNotificationOperation(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult,
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult.ValidationToken token)
        {
            _cleanupResult = cleanupResult;
            _token = token;
        }

        /// <summary>
        /// Single atomic factory: rejects a null result, issues the result's
        /// validation token exactly once, then verifies the notification-specific
        /// correlation before storing the operation.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteNotificationOperation Create(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            if (cleanupResult == null)
            {
                throw new ArgumentNullException(nameof(cleanupResult));
            }

            if (!cleanupResult.TryValidate(
                    out PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult.ValidationToken token))
            {
                throw new ArgumentException(
                    "Cleanup orchestration result must be valid.",
                    nameof(cleanupResult));
            }

            if (!IsNotificationCorrelated(cleanupResult))
            {
                throw new ArgumentException(
                    "Cleanup orchestration result must be correlated with the capture-complete notification graph.",
                    nameof(cleanupResult));
            }

            return new PngJsonCapturePublicationCaptureCompleteNotificationOperation(cleanupResult, token);
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult CleanupResult => _cleanupResult;

        internal PngJsonCapturePublicationCaptureCompleteCleanupExecutionResult ExecutionResult => _cleanupResult.ExecutionResult;

        internal CaptureRunRootLayout RootLayout => _cleanupResult.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _cleanupResult.LockIdentityEvidence;

        internal long TestRunId => _cleanupResult.TestRunId;

        internal string RunInitializationId => _cleanupResult.RunInitializationId;

        internal string RunManifestContentSha256 => _cleanupResult.ActionPlan.AuthoritativePlan.RunManifestContentSha256;

        internal string CaptureIndexPath => _cleanupResult.OrchestrationResult.InspectionSnapshot.Operation.PublicationPaths.CaptureIndexPath;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _cleanupResult.Disposition;

        internal CaptureRunPublicationCaptureCompleteCleanupExecutionStatus Status => _cleanupResult.Status;

        /// <summary>
        /// Exception-safe recomputation of every correlation this operation
        /// guarantees: re-verifies the cleanup result with the held token and
        /// then re-runs the notification-specific correlation predicate. Never
        /// re-issues a token and never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                try
                {
                    return _cleanupResult != null
                        && _token != null
                        && _cleanupResult.IsValidWithToken(_token)
                        && IsNotificationCorrelated(_cleanupResult);
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Exception-safe notification-specific correlation checks run after the
        /// single full result validation, in the fixed order. Never throws and
        /// never re-issues a token or scans the filesystem.
        /// </summary>
        private static bool IsNotificationCorrelated(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            try
            {
                // Status must be CaptureCompleteReady.
                if (cleanupResult.Status != CaptureRunPublicationCaptureCompleteCleanupExecutionStatus.CaptureCompleteReady)
                {
                    return false;
                }

                // Disposition must be CommitCaptureIndex or CaptureComplete.
                if (!IsAcceptedDisposition(cleanupResult.Disposition))
                {
                    return false;
                }

                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = cleanupResult.ActionPlan;
                if (actionPlan == null)
                {
                    return false;
                }

                // Authoritative plan must be present.
                PngJsonCapturePublicationPlan plan = actionPlan.AuthoritativePlan;
                if (plan == null)
                {
                    return false;
                }

                // Root layout and lock identity evidence must be present and valid.
                CaptureRunRootLayout rootLayout = cleanupResult.RootLayout;
                if (rootLayout == null || !rootLayout.IsValid)
                {
                    return false;
                }

                CaptureRunLockIdentityEvidence lockIdentityEvidence = cleanupResult.LockIdentityEvidence;
                if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
                {
                    return false;
                }

                // Artifact recovery inspection operation.
                PngJsonCapturePublicationArtifactRecoveryOrchestrationResult recovery = cleanupResult.OrchestrationResult;
                if (recovery == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionSnapshot snapshot = recovery.InspectionSnapshot;
                if (snapshot == null)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionOperation inspectionOperation = snapshot.Operation;
                if (inspectionOperation == null
                    || !ReferenceEquals(inspectionOperation.RootLayout, rootLayout)
                    || !ReferenceEquals(inspectionOperation.LockIdentityEvidence, lockIdentityEvidence))
                {
                    return false;
                }

                // Run identity must match across the inspection operation, plan,
                // and cleanup result.
                if (inspectionOperation.TestRunId != plan.TestRunId
                    || cleanupResult.TestRunId != plan.TestRunId)
                {
                    return false;
                }

                if (!string.Equals(inspectionOperation.RunInitializationId, plan.RunInitializationId, StringComparison.Ordinal)
                    || !string.Equals(cleanupResult.RunInitializationId, plan.RunInitializationId, StringComparison.Ordinal))
                {
                    return false;
                }

                // Run manifest content SHA-256 must match exactly.
                if (!string.Equals(inspectionOperation.RunManifestContentSha256, plan.RunManifestContentSha256, StringComparison.Ordinal))
                {
                    return false;
                }

                // Publication path set must be present, valid, and share the exact root layout.
                CaptureRunPublicationPathSet publicationPaths = inspectionOperation.PublicationPaths;
                if (publicationPaths == null
                    || !publicationPaths.IsValid
                    || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
                {
                    return false;
                }

                // Capture index path must be the authoritative capture.index directly under the final run root.
                string captureIndexPath = publicationPaths.CaptureIndexPath;
                if (captureIndexPath == null)
                {
                    return false;
                }

                string expectedCaptureIndexPath = Path.GetFullPath(Path.Combine(rootLayout.FinalRunRoot, "capture.index"));
                return string.Equals(captureIndexPath, expectedCaptureIndexPath, StringComparison.Ordinal);
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool IsAcceptedDisposition(CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            return disposition == CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex
                || disposition == CaptureRunPublicationArtifactRecoveryDisposition.CaptureComplete;
        }
    }
}
