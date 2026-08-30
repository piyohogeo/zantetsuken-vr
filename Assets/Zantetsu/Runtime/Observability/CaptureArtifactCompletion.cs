using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    internal sealed class CaptureArtifactCompletion
    {
        internal CaptureArtifactCompletion(
            in CaptureFrameWorkToken workToken,
            long captureFrameId,
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactFrameRelation frameRelation,
            CaptureArtifactCompletionStatus status,
            ICaptureArtifactStorageReceipt storageReceipt,
            ExceptionDispatchInfo failure)
        {
            if (!workToken.IsValid) throw new ArgumentException("Work token must be valid.", nameof(workToken));
            if (captureFrameId <= 0 || captureFrameId != workToken.CaptureFrameId) throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            if (frameRelation == null || !frameRelation.IsValid) throw new ArgumentException("Frame relation must be valid.", nameof(frameRelation));
            if (status != CaptureArtifactCompletionStatus.Staged
                && status != CaptureArtifactCompletionStatus.Failed
                && status != CaptureArtifactCompletionStatus.Cancelled)
            {
                throw new ArgumentOutOfRangeException(nameof(status));
            }

            if ((status == CaptureArtifactCompletionStatus.Staged) != (storageReceipt != null)
                || (status == CaptureArtifactCompletionStatus.Failed) != (failure != null))
            {
                throw new ArgumentException("Completion payload does not match status.", nameof(status));
            }

            if (storageReceipt != null && !ReferenceEquals(storageReceipt.Descriptor, descriptor))
            {
                throw new ArgumentException("Receipt must reference the exact descriptor.", nameof(storageReceipt));
            }

            WorkToken = workToken;
            CaptureFrameId = captureFrameId;
            Descriptor = descriptor;
            FrameRelation = frameRelation;
            Status = status;
            StorageReceipt = storageReceipt;
            Failure = failure;
        }

        internal CaptureFrameWorkToken WorkToken { get; }
        internal long CaptureFrameId { get; }
        internal CaptureArtifactDescriptor Descriptor { get; }
        internal CaptureArtifactFrameRelation FrameRelation { get; }
        internal CaptureArtifactCompletionStatus Status { get; }
        internal long ByteLength => Descriptor.ByteLength;
        internal string ContentHash => Descriptor.ContentHash;
        internal ICaptureArtifactStorageReceipt StorageReceipt { get; }
        internal ExceptionDispatchInfo Failure { get; }

        internal bool IsValid => WorkToken.IsValid
            && CaptureFrameId == WorkToken.CaptureFrameId
            && Descriptor != null
            && Descriptor.IsValid
            && FrameRelation != null
            && FrameRelation.IsValid
            && (Status == CaptureArtifactCompletionStatus.Staged
                || Status == CaptureArtifactCompletionStatus.Failed
                || Status == CaptureArtifactCompletionStatus.Cancelled)
            && ((Status == CaptureArtifactCompletionStatus.Staged) == (StorageReceipt != null))
            && ((Status == CaptureArtifactCompletionStatus.Failed) == (Failure != null))
            && (StorageReceipt == null || ReferenceEquals(StorageReceipt.Descriptor, Descriptor));
    }
}
