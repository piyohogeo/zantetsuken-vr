using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One array of plain numbers that an owner works in, with the part that may be used -- <see cref="Length"/> -- kept
    /// apart from what stands behind it (TL, 2026-10-05). Two things can stand behind it, read and written the same way:
    /// <list type="bullet">
    /// <item><b>reserved address space</b> (<see cref="TryCreateNative"/>): a <see cref="VpVirtualArray{T}"/> owns the
    /// reservation, the commits and the release; this room borrows its view. More room is more pages committed behind the
    /// same base: nothing moves and nothing already written changes. Only <c>[0, Length)</c> is ever touched; what is
    /// reserved and not committed is never read or written, and what is committed past <see cref="Length"/> is not
    /// either.</item>
    /// <item><b>a managed array</b> (<see cref="Managed"/>): for an owner made on its own, with no backing and nothing
    /// to release. More room is a larger array with what was there copied into it.</item>
    /// </list>
    /// <para>
    /// More room is taken in two steps so that an owner of several rooms can take all or none: <see cref="TryPrepare"/>
    /// makes the larger room available and changes nothing an observer can see; <see cref="Grant"/> then makes it usable
    /// and cannot fail. A prepare that was not followed by a grant leaves committed pages or a spare array behind, and
    /// nothing else.
    /// </para>
    /// <para>
    /// The numbers are zero when a room is made and when it is granted more: every page of the granted part has been
    /// written once before anyone works in it, so the first write of a page is not paid inside a frame.
    /// </para>
    /// What is lent -- <see cref="Valid"/>, <see cref="First"/>, a span -- is never disposed by the borrower; the room
    /// is, by its owner, once no job uses it.
    /// </summary>
    /// <summary>How many rooms on reserved address space are alive in this process: made and not yet given back. For the end of a session and for tests.</summary>
    public static class VpNumericRoomCensus
    {
        internal static int s_live;

        public static int LiveNativeRooms => s_live;
    }

    public sealed unsafe class VpNumericRoom<T> : IDisposable where T : unmanaged
    {
        private VpVirtualArray<T> _memory;
        private NativeArray<T> _view;
        private T* _base;
        private T[] _managed;
        private T[] _prepared;
        private int _length;
        private bool _disposed;

        private VpNumericRoom()
        {
        }

        /// <summary>A room of <paramref name="length"/> numbers in a managed array; nothing to release.</summary>
        public static VpNumericRoom<T> Managed(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            return new VpNumericRoom<T> { _managed = new T[length], _length = length };
        }

        /// <summary>
        /// A room of <paramref name="length"/> numbers on <paramref name="reservedLength"/> reserved: the first commit
        /// holds the length, and every page of it is written (zero) here. False, holding nothing, when the reservation
        /// or the commit is refused or the lengths do not hold together.
        /// </summary>
        public static bool TryCreateNative(
            IVpPageBacking backing, int reservedLength, int length, out VpNumericRoom<T> room, out string failure)
        {
            room = null;
            if (length < 0 || reservedLength < length)
            {
                failure = "the room asked (" + length + ") is not within what is reserved (" + reservedLength + ")";
                return false;
            }

            if (!VpVirtualArray<T>.TryCreate(backing, reservedLength, length, out VpVirtualArray<T> memory, out failure))
            {
                return false;
            }

            NativeArray<T> view = memory.View;
            var made = new VpNumericRoom<T>
            {
                _memory = memory,
                _view = view,
                _base = (T*)NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(view),
            };
            made.Touch(0, length);
            made._length = length;
            room = made;
            System.Threading.Interlocked.Increment(ref VpNumericRoomCensus.s_live);
            return true;
        }

        /// <summary>How many numbers may be used: the whole of what a borrower may read or write.</summary>
        public int Length => _length;

        /// <summary>Whether reserved address space stands behind this room (else a managed array).</summary>
        public bool IsNative => _memory != null;

        /// <summary>The most this room can ever hold without moving: the reservation, or the array's own length.</summary>
        public int ReservedLength => _memory != null ? _memory.ReservedLength : _managed.Length;

        /// <summary>The address space reserved, in bytes; 0 for a managed array.</summary>
        public long ReservedBytes => _memory != null ? _memory.ReservedBytes : 0L;

        /// <summary>The pages committed, in bytes (whole pages, so no less than the room in use); 0 for a managed array.</summary>
        public long CommittedBytes => _memory != null ? _memory.CommittedBytes : 0L;

        /// <summary>The managed bytes this room holds: its array's elements, or 0 on reserved address space.</summary>
        public long ManagedBytes => _memory != null ? 0L : (long)_managed.Length * sizeof(T);

        /// <summary>The bytes of the part in use.</summary>
        public long ValidBytes => (long)_length * sizeof(T);

        /// <summary>Where the numbers begin; it never changes while the room lives. Zero for a managed array.</summary>
        public IntPtr BaseAddress => (IntPtr)_base;

        /// <summary>The number at <paramref name="index"/>, to read or to write in place.</summary>
        public ref T this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)_length)
                {
                    ThrowIndex(index);
                }

                return ref (_managed != null ? ref _managed[index] : ref _base[index]);
            }
        }

        /// <summary>
        /// The part in use, as the owner's view cut to it: what a reader, a job or an upload is given, never the
        /// reservation. Reserved address space only.
        /// </summary>
        public NativeArray<T> Valid => First(_length);

        /// <summary>The first <paramref name="count"/> numbers of the part in use. Reserved address space only.</summary>
        public NativeArray<T> First(int count)
        {
            ThrowIfDisposed();
            if (_memory == null)
            {
                throw new InvalidOperationException("a room in a managed array lends no native view");
            }

            if ((uint)count > (uint)_length)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return _view.GetSubArray(0, count);
        }

        /// <summary>The array behind a managed room, whole; null on reserved address space.</summary>
        public T[] ManagedArray => _managed;

        /// <summary><paramref name="count"/> numbers from <paramref name="start"/>, to read and write.</summary>
        public Span<T> AsSpan(int start, int count)
        {
            if (start < 0 || count < 0 || start > _length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            return _managed != null ? new Span<T>(_managed, start, count) : new Span<T>(_base + start, count);
        }

        /// <summary>Makes <paramref name="count"/> numbers from <paramref name="start"/> zero.</summary>
        public void Clear(int start, int count)
        {
            AsSpan(start, count).Clear();
        }

        /// <summary>Copies <paramref name="count"/> numbers from <paramref name="from"/> into this room.</summary>
        public void CopyFrom(VpNumericRoom<T> from, int fromStart, int start, int count)
        {
            from.AsSpan(fromStart, count).CopyTo(AsSpan(start, count));
        }

        /// <summary>Copies <paramref name="count"/> numbers from an array into this room.</summary>
        public void CopyFrom(T[] from, int fromStart, int start, int count)
        {
            new ReadOnlySpan<T>(from, fromStart, count).CopyTo(AsSpan(start, count));
        }

        /// <summary>Copies <paramref name="count"/> numbers of this room into an array.</summary>
        public void CopyTo(int start, T[] into, int intoStart, int count)
        {
            AsSpan(start, count).CopyTo(new Span<T>(into, intoStart, count));
        }

        /// <summary>
        /// Makes a room of <paramref name="length"/> available without making it usable: pages committed behind the
        /// same base, or a larger array set aside. Nothing a borrower sees changes. False when the length is past the
        /// reservation, the commit is refused, or memory cannot be had; whatever was prepared before stays prepared.
        /// </summary>
        public bool TryPrepare(int length, out string failure)
        {
            ThrowIfDisposed();
            failure = null;
            if (length <= _length)
            {
                return true;
            }

            if (_memory != null)
            {
                return _memory.TryCommitTo(length, out failure);
            }

            if (_prepared != null && _prepared.Length >= length)
            {
                return true;
            }

            try
            {
                _prepared = new T[length];
                return true;
            }
            catch (OutOfMemoryException exception)
            {
                failure = "memory could not be had: " + exception.Message;
                return false;
            }
        }

        /// <summary>
        /// Makes the prepared room usable: the part in use becomes <paramref name="length"/>, what was written stays
        /// where it was, and the new part is zero with every page of it written. Never fails after a prepare of at
        /// least this length; smaller than the room in use changes nothing.
        /// </summary>
        public void Grant(int length)
        {
            ThrowIfDisposed();
            if (length <= _length)
            {
                return;
            }

            if (_memory != null)
            {
                if (length > _memory.CommittedLength)
                {
                    throw new InvalidOperationException("the room granted (" + length + ") was not prepared (" + _memory.CommittedLength + " committed)");
                }

                Touch(_length, length - _length);
                _length = length;
                return;
            }

            if (_prepared == null || _prepared.Length < length)
            {
                throw new InvalidOperationException("the room granted (" + length + ") was not prepared");
            }

            Array.Copy(_managed, _prepared, _length);
            _managed = _prepared;
            _prepared = null;
            _length = length;
        }

        /// <summary>Prepares and grants in one: for an owner of this room alone.</summary>
        public bool TryGrow(int length, out string failure)
        {
            if (!TryPrepare(length, out failure))
            {
                return false;
            }

            Grant(length);
            return true;
        }

        /// <summary>Gives the reservation back, once; a managed room only stops being usable. A job still using a view lent from here makes this throw.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_memory != null)
            {
                _memory.Dispose();
                System.Threading.Interlocked.Decrement(ref VpNumericRoomCensus.s_live);
            }

            _disposed = true;
            _memory = null;
            _view = default;
            _base = null;
            _managed = null;
            _prepared = null;
            _length = 0;
        }

        // Every page of the range written once: zero, which is also what the numbers are to start from.
        private void Touch(int start, int count)
        {
            if (count > 0)
            {
                UnsafeUtility.MemClear(_base + start, (long)count * sizeof(T));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpNumericRoom<T>));
            }
        }

        private void ThrowIndex(int index)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpNumericRoom<T>));
            }

            throw new IndexOutOfRangeException("index " + index + " is not within the room in use (" + _length + ")");
        }
    }
}
