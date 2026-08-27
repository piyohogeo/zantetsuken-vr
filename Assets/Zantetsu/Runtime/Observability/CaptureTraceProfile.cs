using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable trace capture capacity configuration for one capture profile.
    /// It fixes the draft post-roll capacity, the pending in-flight draft cap,
    /// and the per-run append-only draft cap so later factories can size their
    /// queues and terminal reserve deterministically from a single profile.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PostRollCapacity"/> covers both the normal draft region and
    /// the freeze terminal reserve. The terminal reserve is not stored here:
    /// later factories derive it as <c>MaxInFlightDraftCount + 1</c>.
    /// <see cref="MaxInFlightDraftCount"/> is the cap on the total pending
    /// draft count across every queue and worker, and
    /// <see cref="MaxDraftCountPerRun"/> is the cap on the total append-only
    /// entry count during a run.
    /// </para>
    /// <para>
    /// <see cref="CaptureProfileId"/> is a semantic identifier of the
    /// configuration content. This type does not manage global uniqueness of
    /// the ID; the caller is responsible for assigning distinct IDs to distinct
    /// configurations.
    /// </para>
    /// <para>
    /// Every argument is validated before any field is assigned, and invalid
    /// values are rejected with <see cref="ArgumentOutOfRangeException"/> rather
    /// than corrected or clamped. The type is immutable, owns and disposes
    /// nothing, does not implement <see cref="IDisposable"/>, is not a
    /// MonoBehaviour, ScriptableObject, or singleton, and performs no Unity
    /// static API access, file I/O, logging, trace recording, or queue
    /// operation. Its state never changes after construction.
    /// </para>
    /// </remarks>
    public sealed class CaptureTraceProfile
    {
        private readonly int _captureProfileId;
        private readonly int _postRollCapacity;
        private readonly int _maxInFlightDraftCount;
        private readonly int _maxDraftCountPerRun;

        public CaptureTraceProfile(
            int captureProfileId,
            int postRollCapacity,
            int maxInFlightDraftCount,
            int maxDraftCountPerRun)
        {
            if (captureProfileId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureProfileId), captureProfileId, "Capture profile ID must be greater than zero.");
            }

            if (maxInFlightDraftCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxInFlightDraftCount), maxInFlightDraftCount, "Max in-flight draft count must be at least 1.");
            }

            if (maxDraftCountPerRun < 1 || maxDraftCountPerRun > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(maxDraftCountPerRun), maxDraftCountPerRun, "Max draft count per run must be between 1 and 100000.");
            }

            if (maxInFlightDraftCount > maxDraftCountPerRun)
            {
                throw new ArgumentOutOfRangeException(nameof(maxInFlightDraftCount), maxInFlightDraftCount, "Max in-flight draft count must not exceed the max draft count per run.");
            }

            if (checked(maxInFlightDraftCount + 1) > postRollCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(postRollCapacity), postRollCapacity, "Post-roll capacity must reserve at least one slot beyond the max in-flight draft count.");
            }

            _captureProfileId = captureProfileId;
            _postRollCapacity = postRollCapacity;
            _maxInFlightDraftCount = maxInFlightDraftCount;
            _maxDraftCountPerRun = maxDraftCountPerRun;
        }

        public int CaptureProfileId => _captureProfileId;

        public int PostRollCapacity => _postRollCapacity;

        public int MaxInFlightDraftCount => _maxInFlightDraftCount;

        public int MaxDraftCountPerRun => _maxDraftCountPerRun;
    }
}
