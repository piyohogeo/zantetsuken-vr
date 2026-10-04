using System.Collections.Generic;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One array of some room, as it stands (TL, 2026-10-05): what it is called, how long one item is, how many items
    /// may be used and how many are reserved for, and the bytes behind it by kind. For a report and for a test of the
    /// sizes; nothing is decided from it.
    /// </summary>
    public struct VpRoomLine
    {
        public string name;

        /// <summary>The bytes of one item.</summary>
        public int itemBytes;

        /// <summary>How many items may be used.</summary>
        public long length;

        /// <summary>How many items the room can ever hold without moving: the reservation's, or the managed array's own length.</summary>
        public long reservedLength;

        /// <summary>Address space reserved, in bytes; 0 for a managed array.</summary>
        public long reservedBytes;

        /// <summary>Pages committed, in bytes; 0 for a managed array.</summary>
        public long committedBytes;

        /// <summary>Managed bytes: the array's elements; 0 on reserved address space.</summary>
        public long managedBytes;

        /// <summary>Whether reserved address space stands behind it.</summary>
        public bool native;

        /// <summary>Where a native room's numbers begin; it never changes while the room lives. Zero for a managed array.</summary>
        public System.IntPtr baseAddress;

        public static VpRoomLine Of<T>(string name, VpNumericRoom<T> room) where T : unmanaged
        {
            return new VpRoomLine
            {
                name = name,
                itemBytes = UnsafeUtility.SizeOf<T>(),
                length = room.Length,
                reservedLength = room.ReservedLength,
                reservedBytes = room.ReservedBytes,
                committedBytes = room.CommittedBytes,
                managedBytes = room.ManagedBytes,
                native = room.IsNative,
                baseAddress = room.BaseAddress,
            };
        }

        public static VpRoomLine OfManaged<T>(string name, T[] array) where T : struct
        {
            return OfManaged(name, UnsafeUtility.SizeOf<T>(), array.Length);
        }

        public static VpRoomLine OfManaged(string name, int itemBytes, long length)
        {
            return new VpRoomLine
            {
                name = name, itemBytes = itemBytes, length = length, reservedLength = length, managedBytes = length * itemBytes,
            };
        }

        public override string ToString()
        {
            return name + ": " + length + " x " + itemBytes + " B" + (native
                ? " native, in use " + (length * itemBytes) + " B, committed " + committedBytes + " B, reserved " + reservedBytes + " B (" + reservedLength + " items)"
                : " managed, " + managedBytes + " B");
        }
    }

    /// <summary>
    /// What some room is made of, in bytes, by kind (TL, 2026-10-05): the managed arrays, and the reserved and committed
    /// address space behind the numeric rooms. A sum for a report and for a test of the sizes; nothing is decided from it.
    /// </summary>
    public struct VpRoomBytes
    {
        /// <summary>Managed arrays' elements (the array headers are not counted).</summary>
        public long managed;

        /// <summary>Address space reserved.</summary>
        public long nativeReserved;

        /// <summary>Pages committed (whole pages, so no less than the part in use).</summary>
        public long nativeCommitted;

        /// <summary>The part of the native rooms that may be used.</summary>
        public long nativeInUse;

        public static VpRoomBytes Of(List<VpRoomLine> lines)
        {
            var bytes = new VpRoomBytes();
            for (int i = 0; i < lines.Count; i++)
            {
                VpRoomLine line = lines[i];
                bytes.managed += line.managedBytes;
                bytes.nativeReserved += line.reservedBytes;
                bytes.nativeCommitted += line.committedBytes;
                if (line.native)
                {
                    bytes.nativeInUse += line.length * line.itemBytes;
                }
            }

            return bytes;
        }

        public override string ToString()
        {
            return "managed " + managed + " B, native reserved " + nativeReserved + " B, committed " + nativeCommitted + " B, in use " + nativeInUse + " B";
        }
    }
}
