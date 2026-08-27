using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Summary of the non-starting half of a pipeline tick: the persistence
    /// stage outcome, the readback completion stage outcome, and the completed
    /// artifact and sidecar receipt when a sidecar was published.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a value type with no public constructor; instances are created
    /// only by <see cref="CaptureFramePipelineCoordinator.AdvancePendingWork"/>
    /// and
    /// <see cref="CaptureFrameRenderTargetPipelineCoordinator.AdvancePendingWork"/>.
    /// It owns neither the completed artifact nor the sidecar receipt.
    /// </para>
    /// <para>
    /// Invariant: <see cref="HasCompletedArtifact"/> is true exactly when
    /// <see cref="PersistenceStatus"/> is
    /// <see cref="CaptureFramePngArtifactPersistenceStatus.SidecarCompleted"/>;
    /// in that case both <see cref="CompletedArtifact"/> and
    /// <see cref="SidecarReceipt"/> are non-null, and otherwise both are null.
    /// There is no independent boolean field; <see cref="HasCompletedArtifact"/>
    /// is computed from <see cref="PersistenceStatus"/>.
    /// </para>
    /// <para>
    /// <c>default</c> is a valid not-run state reporting <c>None</c> /
    /// <c>None</c> with null artifact and receipt.
    /// </para>
    /// </remarks>
    public readonly struct CaptureFramePipelineAdvanceResult
    {
        public CaptureFramePngArtifactPersistenceStatus PersistenceStatus { get; }

        public CaptureFramePngQueueStatus ReadbackCompletionStatus { get; }

        public CaptureFramePngArtifact CompletedArtifact { get; }

        public CaptureFramePngArtifactSaveReceipt SidecarReceipt { get; }

        public bool HasCompletedArtifact =>
            PersistenceStatus == CaptureFramePngArtifactPersistenceStatus.SidecarCompleted;

        internal CaptureFramePipelineAdvanceResult(
            CaptureFramePngArtifactPersistenceStatus persistenceStatus,
            CaptureFramePngQueueStatus readbackCompletionStatus,
            CaptureFramePngArtifact completedArtifact,
            CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            if (persistenceStatus != CaptureFramePngArtifactPersistenceStatus.None
                && persistenceStatus != CaptureFramePngArtifactPersistenceStatus.PngPrepared
                && persistenceStatus != CaptureFramePngArtifactPersistenceStatus.SidecarCompleted)
            {
                throw new ArgumentException("Undefined persistence status.", nameof(persistenceStatus));
            }

            if (readbackCompletionStatus != CaptureFramePngQueueStatus.None
                && readbackCompletionStatus != CaptureFramePngQueueStatus.Queued
                && readbackCompletionStatus != CaptureFramePngQueueStatus.Dropped)
            {
                throw new ArgumentException("Undefined readback completion status.", nameof(readbackCompletionStatus));
            }

            if (persistenceStatus == CaptureFramePngArtifactPersistenceStatus.SidecarCompleted)
            {
                if (completedArtifact == null)
                {
                    throw new ArgumentNullException(nameof(completedArtifact));
                }

                if (sidecarReceipt == null)
                {
                    throw new ArgumentNullException(nameof(sidecarReceipt));
                }
            }
            else
            {
                if (completedArtifact != null)
                {
                    throw new ArgumentException("Completed artifact must be null unless the persistence status is SidecarCompleted.", nameof(completedArtifact));
                }

                if (sidecarReceipt != null)
                {
                    throw new ArgumentException("Sidecar receipt must be null unless the persistence status is SidecarCompleted.", nameof(sidecarReceipt));
                }
            }

            PersistenceStatus = persistenceStatus;
            ReadbackCompletionStatus = readbackCompletionStatus;
            CompletedArtifact = completedArtifact;
            SidecarReceipt = sidecarReceipt;
        }
    }
}
