using System;
using System.Buffers.Binary;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The payload of one <c>SlashHitConfirmed</c> trace record (DESIGN 21.16.6): which Slash, the input time of the
    /// update whose sweep hit, the fragment, the Provisional side, and what the cut acceptance said, with the operation
    /// it issued or none. It is the payload of a record of the existing lane format; nothing of that format changes.
    /// <para>
    /// Little-endian, <see cref="Length"/> bytes: SlashId (int64), update time (IEEE double as int64), fragment
    /// (int32), operation (int32, 0 for none), acceptance (byte), admission (byte), side (sbyte), flags (byte, bit 0 the
    /// latch update).
    /// </para>
    /// </summary>
    public static class SlashHitConfirmedTraceRecord
    {
        public const int Length = 28;

        public static void Write(in SlashHitConfirmed hit, Span<byte> into)
        {
            if (into.Length < Length)
            {
                throw new ArgumentException("the record is " + Length + " bytes", nameof(into));
            }

            BinaryPrimitives.WriteInt64LittleEndian(into.Slice(0, 8), hit.SlashId);
            BinaryPrimitives.WriteInt64LittleEndian(into.Slice(8, 8), BitConverter.DoubleToInt64Bits(hit.At));
            BinaryPrimitives.WriteInt32LittleEndian(into.Slice(16, 4), hit.Fragment.value);
            BinaryPrimitives.WriteInt32LittleEndian(into.Slice(20, 4), hit.Operation.value);
            into[24] = (byte)hit.Acceptance;
            into[25] = (byte)hit.Admission;
            into[26] = (byte)(sbyte)(hit.Side > 0f ? 1 : hit.Side < 0f ? -1 : 0);
            into[27] = (byte)(hit.AtLatch ? 1 : 0);
        }

        /// <summary>Reads one payload back; false for one that is not of this length.</summary>
        public static bool TryRead(ReadOnlySpan<byte> payload, out SlashHitConfirmed hit)
        {
            hit = default;
            if (payload.Length != Length)
            {
                return false;
            }

            int fragment = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(16, 4));
            int operation = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(20, 4));
            if (fragment < 0 || operation < 0)
            {
                return false;
            }

            hit = new SlashHitConfirmed(
                BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(0, 8)),
                BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(8, 8))),
                (payload[27] & 1) != 0,
                new LogicalFragmentId(fragment),
                (sbyte)payload[26],
                (ProvisionalCutAcceptance)payload[24],
                (LogicalCutAdmission)payload[25],
                new CutOperationId(operation));
            return true;
        }
    }
}
