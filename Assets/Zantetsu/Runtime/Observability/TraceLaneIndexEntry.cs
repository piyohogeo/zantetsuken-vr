using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// One record's place in a trace lane: what it is, where its payload
    /// starts, and how long that payload is. Nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PayloadStart"/> is a monotonically increasing 64-bit
    /// position, not an offset into the ring: the offset is derived from it,
    /// and because it only ever grows it also says how much of the ring a
    /// consumer has finished with, including any padding that was skipped to
    /// keep a record contiguous.
    /// </para>
    /// <para>
    /// There is deliberately no timestamp, producer identity, failure reason,
    /// valid bit, or generation here. Publishing the entry is what commits the
    /// record, so an entry a consumer can see is by definition complete.
    /// </para>
    /// </remarks>
    internal readonly struct TraceLaneIndexEntry
    {
        private readonly long _payloadStart;
        private readonly int _payloadLength;
        private readonly TraceEventType _recordKind;

        internal TraceLaneIndexEntry(TraceEventType recordKind, long payloadStart, int payloadLength)
        {
            _recordKind = recordKind;
            _payloadStart = payloadStart;
            _payloadLength = payloadLength;
        }

        internal TraceEventType RecordKind => _recordKind;

        internal long PayloadStart => _payloadStart;

        internal int PayloadLength => _payloadLength;
    }
}
