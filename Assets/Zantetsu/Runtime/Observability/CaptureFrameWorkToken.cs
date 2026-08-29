using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Identifies one accepted use of one fixed service slot. The generation
    /// prevents a completion from an earlier use of the slot being accepted as
    /// current work.
    /// </summary>
    internal readonly struct CaptureFrameWorkToken
    {
        internal Guid OwnerToken { get; }

        internal int SlotIndex { get; }

        internal long Generation { get; }

        internal long TestRunId { get; }

        internal long CaptureFrameId { get; }

        internal bool IsValid =>
            OwnerToken != Guid.Empty &&
            SlotIndex >= 0 &&
            Generation > 0 &&
            TestRunId > 0 &&
            CaptureFrameId > 0;

        internal CaptureFrameWorkToken(
            Guid ownerToken,
            int slotIndex,
            long generation,
            long testRunId,
            long captureFrameId)
        {
            if (ownerToken == Guid.Empty)
            {
                throw new ArgumentException("Owner token must not be empty.", nameof(ownerToken));
            }

            if (slotIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(slotIndex));
            }

            if (generation <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(generation));
            }

            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId));
            }

            if (captureFrameId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            }

            OwnerToken = ownerToken;
            SlotIndex = slotIndex;
            Generation = generation;
            TestRunId = testRunId;
            CaptureFrameId = captureFrameId;
        }

        internal bool IdenticalTo(in CaptureFrameWorkToken other)
        {
            return OwnerToken == other.OwnerToken &&
                SlotIndex == other.SlotIndex &&
                Generation == other.Generation &&
                TestRunId == other.TestRunId &&
                CaptureFrameId == other.CaptureFrameId;
        }
    }
}
