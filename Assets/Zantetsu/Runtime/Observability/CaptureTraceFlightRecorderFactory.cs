using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Builds a <see cref="TraceFlightRecorder"/> for a capture run from a
    /// matching <see cref="CaptureFrameProfile"/> and
    /// <see cref="CaptureTraceProfile"/> pair, deriving the freeze terminal
    /// reserve from the trace profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pair must share the same capture profile ID. The trace profile's
    /// held capacities are re-verified before anything is built, and the freeze
    /// terminal reserve is derived as <c>MaxInFlightDraftCount + 1</c>. The
    /// resulting recorder reserves <c>PostRollCapacity - FreezeTerminalTraceReserve</c>
    /// slots for normal post-roll duplication.
    /// </para>
    /// <para>
    /// Any mismatch or invalid capacity is rejected before the recorder is
    /// built, without touching the logger's state, history, queue, or counters.
    /// This factory owns and disposes nothing and performs no file I/O,
    /// logging, trace recording, or Unity static API access.
    /// </para>
    /// </remarks>
    internal static class CaptureTraceFlightRecorderFactory
    {
        internal static TraceFlightRecorder Create(
            TraceLogger logger,
            CaptureFrameProfile frameProfile,
            CaptureTraceProfile traceProfile)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (frameProfile == null)
            {
                throw new ArgumentNullException(nameof(frameProfile));
            }

            if (traceProfile == null)
            {
                throw new ArgumentNullException(nameof(traceProfile));
            }

            if (frameProfile.ProfileId != traceProfile.CaptureProfileId)
            {
                throw new ArgumentException("The frame profile ID must match the trace profile capture profile ID.", nameof(frameProfile));
            }

            // Defensively re-verify the trace profile's held invariants.
            if (traceProfile.CaptureProfileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(traceProfile), "The trace profile's capture profile ID must be greater than zero.");
            }

            if (traceProfile.MaxInFlightDraftCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(traceProfile), "The trace profile's max in-flight draft count must be at least 1.");
            }

            if (traceProfile.MaxDraftCountPerRun < 1 || traceProfile.MaxDraftCountPerRun > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(traceProfile), "The trace profile's max draft count per run must be between 1 and 100000.");
            }

            if (traceProfile.MaxInFlightDraftCount > traceProfile.MaxDraftCountPerRun)
            {
                throw new ArgumentOutOfRangeException(nameof(traceProfile), "The trace profile's max in-flight draft count must not exceed the max draft count per run.");
            }

            int freezeTerminalTraceReserve = checked(traceProfile.MaxInFlightDraftCount + 1);
            if (freezeTerminalTraceReserve <= 0 || freezeTerminalTraceReserve > traceProfile.PostRollCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(traceProfile), "The trace profile's freeze terminal reserve must be positive and within the post-roll capacity.");
            }

            return new TraceFlightRecorder(logger, traceProfile.PostRollCapacity, freezeTerminalTraceReserve);
        }
    }
}
