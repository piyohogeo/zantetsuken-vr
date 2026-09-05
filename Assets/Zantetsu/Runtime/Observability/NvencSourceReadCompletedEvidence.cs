namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free, immutable proof that one accepted work's source surface
    /// has been fully read. It binds the exact completion source instance, the
    /// work token, the GPU conversion sync credit, and the exact surface
    /// reference. It transfers no ownership, holds no fence, query, command
    /// buffer, texture, or native handle, and is not disposable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>default</c> is invalid. Evidence produced by one source does not
    /// satisfy another source, does not satisfy a different work token or a
    /// different surface, and is only meaningful while the bound sync credit is
    /// still active in its pool. <see cref="Matches"/> re-checks the exact
    /// source, work token, sync credit, and surface reference without throwing
    /// or allocating.
    /// </para>
    /// </remarks>
    internal readonly struct NvencSourceReadCompletedEvidence
    {
        private readonly INvencSourceReadCompletedSource _source;
        private readonly CaptureFrameWorkToken _workToken;
        private readonly NvencGpuConversionSyncLease _syncSlot;
        private readonly CaptureSurfaceLease _surface;

        internal NvencSourceReadCompletedEvidence(
            INvencSourceReadCompletedSource source,
            CaptureFrameWorkToken workToken,
            NvencGpuConversionSyncLease syncSlot,
            CaptureSurfaceLease surface)
        {
            _source = source;
            _workToken = workToken;
            _syncSlot = syncSlot;
            _surface = surface;
        }

        internal INvencSourceReadCompletedSource Source => _source;

        internal CaptureFrameWorkToken WorkToken => _workToken;

        internal NvencGpuConversionSyncLease SyncSlot => _syncSlot;

        internal CaptureSurfaceLease Surface => _surface;

        internal bool IsValid =>
            _source != null && _workToken.IsValid && _syncSlot.IsValid && _surface != null;

        internal static NvencSourceReadCompletedEvidence Create(
            INvencSourceReadCompletedSource source, in NvencSubmissionRecord record)
        {
            return new NvencSourceReadCompletedEvidence(
                source, record.WorkToken, record.SyncSlot, record.Surface);
        }

        internal bool Matches(INvencSourceReadCompletedSource source, in NvencSubmissionRecord record)
        {
            return ReferenceEquals(_source, source) &&
                _workToken.IdenticalTo(record.WorkToken) &&
                _syncSlot.SlotIndex == record.SyncSlot.SlotIndex &&
                _syncSlot.Generation == record.SyncSlot.Generation &&
                _syncSlot.OwnerToken == record.SyncSlot.OwnerToken &&
                ReferenceEquals(_surface, record.Surface);
        }
    }
}
