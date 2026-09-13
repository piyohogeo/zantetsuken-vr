namespace Zantetsu.Observability
{
    /// <summary>
    /// Where everything sits in a saved variable-length trace, version 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The file is a fixed header, then one entry per page that had anything
    /// committed: the page's ordinal, how many bytes of it were committed, and
    /// those bytes. Pages that were never written to are not in the file at
    /// all, and neither is the tail of a page a record could not use.
    /// </para>
    /// <para>
    /// Every integer is little-endian, written out a byte at a time so the
    /// file reads the same whatever machine wrote it. Nothing in here carries
    /// a pointer, a capacity, an owner, a receipt, a generation, a time, a
    /// checksum, or a signature, and no drop is broken down by event or lane -
    /// the file says what was kept and how much was lost, and nothing more.
    /// </para>
    /// </remarks>
    internal static class TracePagedHistoryFileFormat
    {
        /// <summary>What a version 1 file starts with: "ZTRCHIST" in ASCII.</summary>
        internal static readonly byte[] Magic =
        {
            (byte)'Z', (byte)'T', (byte)'R', (byte)'C',
            (byte)'H', (byte)'I', (byte)'S', (byte)'T',
        };

        internal const int MagicByteLength = 8;

        /// <summary>The only version this format has so far.</summary>
        internal const int Version = 1;

        internal const int VersionOffset = MagicByteLength;

        /// <summary>
        /// Where the header says how long it is, so what follows can be found
        /// without knowing this layout by heart.
        /// </summary>
        internal const int HeaderByteLengthOffset = VersionOffset + sizeof(int);

        internal const int PageCountOffset = HeaderByteLengthOffset + sizeof(int);

        internal const int IntegrityStateOffset = PageCountOffset + sizeof(int);

        internal const int CommittedRecordCountOffset = IntegrityStateOffset + sizeof(int);

        internal const int LaneDropCountOffset = CommittedRecordCountOffset + sizeof(long);

        internal const int HistoryDropCountOffset = LaneDropCountOffset + sizeof(long);

        internal const int CommittedByteTotalOffset = HistoryDropCountOffset + sizeof(long);

        /// <summary>The header's whole length, which never varies in version 1.</summary>
        internal const int HeaderBytes = CommittedByteTotalOffset + sizeof(long);

        internal const int PageOrdinalOffset = 0;

        internal const int PageByteLengthOffset = PageOrdinalOffset + sizeof(int);

        /// <summary>What stands in front of one page's bytes.</summary>
        internal const int PageMetadataBytes = PageByteLengthOffset + sizeof(int);

        /// <summary>Writes one 32-bit value, least significant byte first.</summary>
        internal static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>Writes one 64-bit value, least significant byte first.</summary>
        internal static void WriteInt64(byte[] buffer, int offset, long value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
            buffer[offset + 4] = (byte)(value >> 32);
            buffer[offset + 5] = (byte)(value >> 40);
            buffer[offset + 6] = (byte)(value >> 48);
            buffer[offset + 7] = (byte)(value >> 56);
        }
    }
}
