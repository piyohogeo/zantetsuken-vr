using System.Runtime.InteropServices;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// What stands in front of one record inside a history page: how long the
    /// record is, and what kind of record it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the framing the history uses while a Run is going, and the only
    /// place the two fields and their widths are written down - the profile
    /// works out the largest record a page must hold from
    /// <see cref="Bytes"/>, and the writer that puts records into pages uses
    /// the same value.
    /// </para>
    /// <para>
    /// It is not a stored format. Nothing is written or read through it yet,
    /// and it carries no version, time, checksum, padding, or flags.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TraceRecordHeader
    {
        /// <summary>
        /// The bytes one header takes: the two fields below and nothing else.
        /// A contract test holds this to the size the runtime gives the
        /// struct.
        /// </summary>
        internal const int Bytes = sizeof(int) + sizeof(int);

        /// <summary>How many payload bytes follow this header.</summary>
        internal int RecordLength;

        /// <summary>What kind of record the payload belongs to.</summary>
        internal TraceEventType RecordKind;
    }
}
