using System;
using System.Collections.Generic;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// A read-only look at <see cref="Count"/> items of an array somebody else owns, from <see cref="Start"/> on. It
    /// copies nothing and owns nothing: what it shows is whatever the owner's array holds when it is read, so it is
    /// good only for as long as the owner leaves that part of the array as it was. The owner says how long that is;
    /// nothing here can tell.
    /// <para>
    /// The count is its own, never the array's length, so an array may hold more than one range, and a range may be of
    /// any length the array has room for. <c>default</c> is the null range — no array at all — which is told apart from
    /// an empty range of a real array by <see cref="IsNull"/>.
    /// </para>
    /// </summary>
    public readonly struct VpArrayRange<T>
    {
        private readonly T[] _items;

        // The other kind of owner: numbers on reserved address space (a VpNumericRoom), looked at where they are. The
        // address is that of the range's first item; it does not move while the room lives.
        private readonly IntPtr _native;

        private VpArrayRange(IntPtr native, int start, int count)
        {
            _items = null;
            _native = native;
            Start = start;
            Count = count;
        }

        /// <summary>
        /// A range of numbers that stand at <paramref name="baseAddress"/>, each <paramref name="itemBytes"/> long,
        /// of which <paramref name="length"/> may be read. For a type of plain numbers only; the owner keeps the
        /// memory for as long as the range is read.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The range is not inside what may be read.</exception>
        public static unsafe VpArrayRange<T> OfNative(IntPtr baseAddress, int itemBytes, int length, int start, int count)
        {
            if (baseAddress == IntPtr.Zero || itemBytes <= 0 || start < 0 || count < 0 || start > length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "The range lies inside the room in use.");
            }

            return new VpArrayRange<T>((IntPtr)((byte*)baseAddress + ((long)start * itemBytes)), start, count);
        }

        /// <summary>
        /// A range of <paramref name="items"/>. A null array gives the null range, and then the start and the count
        /// must be zero.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">The range is not inside the array.</exception>
        public VpArrayRange(T[] items, int start, int count)
        {
            int length = items == null ? 0 : items.Length;
            if (start < 0 || count < 0 || start > length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(count), "The range lies inside the array.");
            }

            _items = items;
            _native = IntPtr.Zero;
            Start = start;
            Count = count;
        }

        /// <summary>The whole of <paramref name="items"/>, or the null range for a null array.</summary>
        public static VpArrayRange<T> Whole(T[] items)
        {
            return new VpArrayRange<T>(items, 0, items == null ? 0 : items.Length);
        }

        /// <summary>Where in the owner's array this range begins.</summary>
        public int Start { get; }

        /// <summary>How many items this range shows.</summary>
        public int Count { get; }

        /// <summary>Whether there is no array behind this range at all.</summary>
        public bool IsNull => _items == null && _native == IntPtr.Zero;

        /// <summary>The item at <paramref name="index"/> within this range, read from the owner's array now.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The index is not inside the range.</exception>
        public T this[int index]
        {
            get
            {
                if ((uint)index >= (uint)Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _items != null ? _items[Start + index] : AsSpan()[index];
            }
        }

        /// <summary>The same items as a span, read from the owner's array; empty for the null range.</summary>
        public unsafe ReadOnlySpan<T> AsSpan()
        {
            if (_items != null)
            {
                return new ReadOnlySpan<T>(_items, Start, Count);
            }

            return _native == IntPtr.Zero ? ReadOnlySpan<T>.Empty : new ReadOnlySpan<T>((void*)_native, Count);
        }
    }

    /// <summary>
    /// Items read either from a list the caller handed over — referenced, never copied, so a change the caller makes
    /// to it afterwards is what the next read sees — or from a <see cref="VpArrayRange{T}"/>. Whoever reads them reads
    /// both the same way, through <see cref="Count"/> and the indexer; nothing is allocated either way. <c>default</c>
    /// is the null source, as is a null list.
    /// </summary>
    public readonly struct VpReadOnlyItems<T>
    {
        private readonly IReadOnlyList<T> _list;
        private readonly VpArrayRange<T> _range;
        private readonly bool _fromList;

        /// <summary>Reads <paramref name="list"/> itself, as it is at the time of each read.</summary>
        public VpReadOnlyItems(IReadOnlyList<T> list)
        {
            _list = list;
            _range = default;
            _fromList = true;
        }

        /// <summary>Reads <paramref name="range"/>, as its owner's array is at the time of each read.</summary>
        public VpReadOnlyItems(VpArrayRange<T> range)
        {
            _list = null;
            _range = range;
            _fromList = false;
        }

        /// <summary>Whether there is nothing behind these items at all: no list, or the null range.</summary>
        public bool IsNull => _fromList ? _list == null : _range.IsNull;

        /// <summary>How many items there are now; none for the null source.</summary>
        public int Count => _fromList ? (_list == null ? 0 : _list.Count) : _range.Count;

        /// <summary>The item at <paramref name="index"/>, read now.</summary>
        public T this[int index]
        {
            get
            {
                if (!_fromList)
                {
                    return _range[index];
                }

                if (_list == null)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return _list[index];
            }
        }
    }
}
