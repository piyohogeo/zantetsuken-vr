using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Which trace events one lane accepts, as a fixed set of bits. Immutable,
    /// unmanaged, and small enough to sit inside a Burst-compiled writer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One bit per <see cref="TraceEventType"/> value, in a single 64-bit word:
    /// the existing event set runs from 0 to 46, so one word already holds all
    /// of it with room to spare. Nothing here invents an identifier of its own
    /// - the event type is the identity, and a value that could not be
    /// represented is simply not enabled rather than being remapped.
    /// </para>
    /// <para>
    /// A mask is built once and never changed: <see cref="With"/> returns
    /// another mask rather than modifying this one, so a writer copied into a
    /// job carries exactly the set it was created with.
    /// </para>
    /// </remarks>
    internal readonly struct TraceLaneEventMask
    {
        /// <summary>
        /// The highest event value one mask can represent. The current set
        /// ends well below it; a value above it is never enabled.
        /// </summary>
        internal const int HighestRepresentableEvent = 63;

        private readonly ulong _bits;

        internal TraceLaneEventMask(ulong bits)
        {
            _bits = bits;
        }

        /// <summary>A mask that accepts nothing.</summary>
        internal static TraceLaneEventMask None => default;

        internal ulong Bits => _bits;

        /// <summary>
        /// The same mask with one more event accepted. A value outside the
        /// representable range changes nothing.
        /// </summary>
        internal TraceLaneEventMask With(TraceEventType eventType)
        {
            int value = (int)eventType;
            if ((uint)value > HighestRepresentableEvent)
            {
                return this;
            }

            return new TraceLaneEventMask(_bits | (1UL << value));
        }

        /// <summary>
        /// True only for an event this lane accepts. No branch here allocates,
        /// waits, or logs, so it is callable from a Burst-compiled job.
        /// </summary>
        internal bool IsEnabled(TraceEventType eventType)
        {
            int value = (int)eventType;
            if ((uint)value > HighestRepresentableEvent)
            {
                return false;
            }

            return (_bits & (1UL << value)) != 0UL;
        }
    }
}
