using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// A completed or errored capture frame readback result. Does not own the
    /// pooled buffer; the buffer is returned to the pool through the
    /// dispatcher's <see cref="UnityRenderTextureReadbackDispatcher.Release"/>.
    /// A value type with no reference-type fields and no public constructor.
    /// </summary>
    public readonly struct CaptureFrameReadbackResult
    {
        public long OperationId { get; }

        public CaptureFrameRequest FrameRequest { get; }

        public int BufferSlotIndex { get; }

        public bool HasError { get; }

        /// <summary>
        /// Token of the owning dispatcher. Internal so only the dispatcher can
        /// match it; used to reject results that belong to another dispatcher.
        /// </summary>
        internal Guid OwnerToken { get; }

        public bool IsValid => OperationId > 0 && FrameRequest.IsValid;

        internal CaptureFrameReadbackResult(
            Guid ownerToken,
            long operationId,
            CaptureFrameRequest frameRequest,
            int bufferSlotIndex,
            bool hasError)
        {
            OwnerToken = ownerToken;
            OperationId = operationId;
            FrameRequest = frameRequest;
            BufferSlotIndex = bufferSlotIndex;
            HasError = hasError;
        }
    }
}
