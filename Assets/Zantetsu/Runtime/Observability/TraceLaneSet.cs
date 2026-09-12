using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Every variable-length trace lane one Run has, built together from one
    /// profile and held for the Run's whole length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The profile is validated and totalled first; only then is anything
    /// allocated. The lanes are built in profile order, and they are built all
    /// or none: if one cannot be built, the lanes already made are released in
    /// the reverse order and the original failure is passed on. Nothing here
    /// falls back to a smaller lane, skips a lane, or tries again.
    /// </para>
    /// <para>
    /// After that the set is fixed. No lane is added, removed, or swapped, and
    /// a lane is addressed only by its ordinal in the profile - there is no
    /// registry, no name, no producer identity, and no writer lease. A writer
    /// taken for an ordinal is the same value type a single producer uses
    /// directly, and it carries no reference back to this object.
    /// </para>
    /// <para>
    /// Stopping producers and consumers is not this type's job and it does not
    /// try. <see cref="Dispose"/> is for after they have stopped; calling it
    /// twice does nothing the second time.
    /// </para>
    /// </remarks>
    internal sealed unsafe class TraceLaneSet : IDisposable
    {
        private readonly TraceLaneSetProfile _profile;
        private TraceLane[] _lanes;

        /// <summary>
        /// Builds every lane the profile describes, or none of them.
        /// </summary>
        internal TraceLaneSet(TraceLaneSetProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            TraceLane[] lanes = new TraceLane[profile.LaneCount];
            int built = 0;
            try
            {
                for (; built < lanes.Length; built++)
                {
                    TraceLaneSettings settings = profile.Lane(built);
                    lanes[built] = new TraceLane(
                        settings.PayloadCapacity,
                        profile.MaxPayloadLength,
                        settings.IndexCapacity,
                        settings.Enabled);
                }
            }
            catch
            {
                // Whatever was taken before the failure goes back, newest
                // first, and the failure itself is what the caller sees.
                for (int ordinal = built - 1; ordinal >= 0; ordinal--)
                {
                    lanes[ordinal].Dispose();
                }

                throw;
            }

            _profile = profile;
            _lanes = lanes;
        }

        internal int LaneCount => _lanes == null ? 0 : _lanes.Length;

        /// <summary>The longest record any of these lanes accepts.</summary>
        internal int MaxPayloadLength => _profile.MaxPayloadLength;

        /// <summary>How many records one ordinary drain may take at most.</summary>
        internal int NormalDrainMaxRecordCount => _profile.NormalDrainMaxRecordCount;

        /// <summary>
        /// The unmanaged bytes the lanes hold between them right now, which is
        /// nothing once the set has been released.
        /// </summary>
        internal long AllocatedBytes
        {
            get
            {
                if (_lanes == null)
                {
                    return 0L;
                }

                long total = 0L;
                for (int ordinal = 0; ordinal < _lanes.Length; ordinal++)
                {
                    total += _lanes[ordinal].AllocatedBytes;
                }

                return total;
            }
        }

        /// <summary>
        /// The writer for one lane, for the single producer bound to that
        /// ordinal.
        /// </summary>
        internal TraceLaneWriter CreateWriter(int ordinal)
        {
            return Lane(ordinal).CreateWriter();
        }

        /// <summary>
        /// The next committed record of one lane, for that lane's single
        /// consumer. Nothing is leased and no record object is made.
        /// </summary>
        internal bool TryPeek(int ordinal, out TraceLaneIndexEntry entry, out byte* payload)
        {
            return Lane(ordinal).TryPeek(out entry, out payload);
        }

        /// <summary>
        /// Releases the record <see cref="TryPeek"/> returned for one lane.
        /// </summary>
        internal void Consume(int ordinal, in TraceLaneIndexEntry entry)
        {
            Lane(ordinal).Consume(entry);
        }

        /// <summary>
        /// Releases every lane, newest first. Only valid once the producers
        /// and consumers have stopped; this type has no way to stop them.
        /// </summary>
        public void Dispose()
        {
            if (_lanes == null)
            {
                return;
            }

            TraceLane[] lanes = _lanes;
            _lanes = null;
            for (int ordinal = lanes.Length - 1; ordinal >= 0; ordinal--)
            {
                lanes[ordinal].Dispose();
            }
        }

        /// <summary>
        /// The lane an ordinal names. Asking for one that does not exist, or
        /// for any lane after the set is gone, is a composition mistake rather
        /// than something a producer can meet at runtime.
        /// </summary>
        private TraceLane Lane(int ordinal)
        {
            if (_lanes == null)
            {
                throw new ObjectDisposedException(nameof(TraceLaneSet));
            }

            if ((uint)ordinal >= (uint)_lanes.Length)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ordinal), ordinal, "There is no lane with this ordinal.");
            }

            return _lanes[ordinal];
        }
    }
}
