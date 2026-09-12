using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// One lane's fixed configuration: which events it accepts and how big the
    /// two rings behind it are.
    /// </summary>
    /// <remarks>
    /// This carries no name and no producer identity. A lane is addressed by
    /// its ordinal in the profile, and that ordinal is what a producer is
    /// bound to when the Run is composed.
    /// </remarks>
    internal readonly struct TraceLaneSettings
    {
        private readonly TraceLaneEventMask _enabled;
        private readonly int _payloadCapacity;
        private readonly int _indexCapacity;

        internal TraceLaneSettings(
            TraceLaneEventMask enabled, int payloadCapacity, int indexCapacity)
        {
            _enabled = enabled;
            _payloadCapacity = payloadCapacity;
            _indexCapacity = indexCapacity;
        }

        internal TraceLaneEventMask Enabled => _enabled;

        internal int PayloadCapacity => _payloadCapacity;

        internal int IndexCapacity => _indexCapacity;
    }

    /// <summary>
    /// The whole variable-length trace configuration for one Run, settled
    /// before the Run starts and unchanged for its whole length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything is checked here, before a single byte is allocated: there is
    /// at least one lane, every capacity is positive, every payload ring is
    /// big enough for the longest record the Run allows, and the ordinary
    /// drain limit is positive. The total the lanes will take is added up in
    /// checked arithmetic from the same calculation the lane itself allocates
    /// by, so the figure validated here is the figure taken later.
    /// </para>
    /// <para>
    /// The lane array is copied, so a caller that keeps and edits its own
    /// array afterwards changes nothing here. That total counts the lanes'
    /// unmanaged storage only - not the objects that hold them.
    /// </para>
    /// <para>
    /// This is not <c>CaptureTraceProfile</c> and does not replace it: it
    /// describes the variable-length lanes alone, and nothing in the existing
    /// trace configuration is read or changed.
    /// </para>
    /// </remarks>
    internal sealed class TraceLaneSetProfile
    {
        private readonly TraceLaneSettings[] _lanes;
        private readonly int _maxPayloadLength;
        private readonly int _normalDrainMaxRecordCount;
        private readonly long _totalStorageBytes;

        /// <summary>
        /// Validates the whole configuration and fixes it. A profile that
        /// cannot be built describes nothing and allocates nothing.
        /// </summary>
        internal TraceLaneSetProfile(
            int maxPayloadLength,
            int normalDrainMaxRecordCount,
            TraceLaneSettings[] lanes)
        {
            if (lanes == null)
            {
                throw new ArgumentNullException(nameof(lanes));
            }

            if (lanes.Length < 1)
            {
                throw new ArgumentException("A profile must describe at least one lane.", nameof(lanes));
            }

            if (maxPayloadLength <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxPayloadLength), maxPayloadLength, "Max payload length must be positive.");
            }

            if (normalDrainMaxRecordCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(normalDrainMaxRecordCount),
                    normalDrainMaxRecordCount,
                    "The ordinary drain limit must be positive.");
            }

            // Copied first, so nothing below can be read from an array the
            // caller is free to change afterwards.
            TraceLaneSettings[] copy = new TraceLaneSettings[lanes.Length];
            Array.Copy(lanes, copy, lanes.Length);

            long total = 0L;
            checked
            {
                for (int ordinal = 0; ordinal < copy.Length; ordinal++)
                {
                    TraceLaneSettings lane = copy[ordinal];
                    if (lane.PayloadCapacity <= 0)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(lanes),
                            lane.PayloadCapacity,
                            "Lane " + ordinal + ": payload capacity must be positive.");
                    }

                    if (lane.IndexCapacity <= 0)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(lanes),
                            lane.IndexCapacity,
                            "Lane " + ordinal + ": index capacity must be positive.");
                    }

                    if (lane.PayloadCapacity < maxPayloadLength)
                    {
                        throw new ArgumentOutOfRangeException(
                            nameof(lanes),
                            lane.PayloadCapacity,
                            "Lane " + ordinal + ": the payload ring must hold the longest record.");
                    }

                    total += TraceLane.StorageBytesFor(lane.PayloadCapacity, lane.IndexCapacity);
                }
            }

            _lanes = copy;
            _maxPayloadLength = maxPayloadLength;
            _normalDrainMaxRecordCount = normalDrainMaxRecordCount;
            _totalStorageBytes = total;
        }

        /// <summary>The longest record any lane of this Run accepts.</summary>
        internal int MaxPayloadLength => _maxPayloadLength;

        /// <summary>How many records one ordinary drain may take at most.</summary>
        internal int NormalDrainMaxRecordCount => _normalDrainMaxRecordCount;

        internal int LaneCount => _lanes.Length;

        /// <summary>
        /// The unmanaged bytes every lane in this profile will hold between
        /// them. Managed object overhead is not part of it.
        /// </summary>
        internal long TotalStorageBytes => _totalStorageBytes;

        /// <summary>One lane's settings, by the ordinal it is addressed by.</summary>
        internal TraceLaneSettings Lane(int ordinal)
        {
            if ((uint)ordinal >= (uint)_lanes.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordinal), ordinal, "There is no lane with this ordinal.");
            }

            return _lanes[ordinal];
        }
    }
}
