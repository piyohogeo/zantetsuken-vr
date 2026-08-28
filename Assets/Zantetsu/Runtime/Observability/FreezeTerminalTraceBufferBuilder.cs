using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free builder that expands a <see cref="ForcedDropFrameIdSet"/>
    /// and a <see cref="FreezeTerminalCheckpoint"/> into a
    /// <see cref="FreezeTerminalTraceBuffer"/>: one forced-drop
    /// <c>CaptureFrameDropped</c> event per set ID in set order, followed by one
    /// trailing <c>CaptureRingFrozen</c> event.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder holds only its registry dependency, performs no managed
    /// allocation until the freeze path, and never uses LINQ, mutable
    /// collections, reflection, or stringification to build events. It never
    /// writes to the registry, set, checkpoint, logger, recorder, observer, or
    /// trace queue. Rebuilding from the same set and checkpoint deterministically
    /// reproduces the identical event column.
    /// </para>
    /// </remarks>
    internal sealed class FreezeTerminalTraceBufferBuilder
    {
        private readonly CaptureFrameDraftRegistry _draftRegistry;

        internal FreezeTerminalTraceBufferBuilder(CaptureFrameDraftRegistry draftRegistry)
        {
            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            _draftRegistry = draftRegistry;
        }

        /// <summary>
        /// Returns the registry this builder validates sets against. Exposed for
        /// the freeze terminal coordinator's dependency identity validation only.
        /// </summary>
        internal CaptureFrameDraftRegistry Registry => _draftRegistry;

        internal FreezeTerminalTraceBuffer Build(
            ForcedDropFrameIdSet forcedDropFrameIds,
            in FreezeTerminalCheckpoint checkpoint)
        {
            // The buffer is the sole allocator and validator of the single event
            // array; the builder delegates construction so no external alias to
            // that array can exist.
            return new FreezeTerminalTraceBuffer(_draftRegistry, forcedDropFrameIds, checkpoint);
        }
    }
}
