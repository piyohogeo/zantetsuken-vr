namespace Zantetsu.Trace
{
    /// <summary>
    /// Layout constants for the versioned trace binary format. All multi-byte
    /// values are little-endian; the format carries no BOM and no string
    /// headers.
    /// </summary>
    public static class TraceBinaryFormat
    {
        /// <summary>Major version of the trace binary format.</summary>
        public const ushort MajorVersion = 1;

        /// <summary>Minor version of the trace binary format.</summary>
        public const ushort MinorVersion = 0;

        /// <summary>Fixed size in bytes of the file header.</summary>
        public const int HeaderSize = 32;

        /// <summary>Fixed size in bytes of a single event record.</summary>
        public const int EventRecordSize = 140;
    }
}
