using System;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// A fixed-capacity allocator of contiguous spans of slots, for the parts of the geometry storage that are
    /// addressed by position and not by handle: the vertices and their topology entries, the submesh descriptors and
    /// the vertex blocks (DESIGN 4.5.3). Several cuts may hold spans of the same array at once, each writing only its
    /// own, which is what lets their kernels run together.
    /// <para>
    /// Free spans are kept in address order, none empty and none adjacent to another. A request takes the start of the
    /// first free span large enough and leaves the rest free; a span given back merges with its free neighbours. There
    /// is no growth, no compaction and nothing is ever moved, so a span handed out stays where it is for as long as its
    /// holder keeps it -- a reservation taken while a worker reads another cannot invalidate that worker's pointers.
    /// </para>
    /// <para>
    /// **Capacity is not lost by giving back.** Because a returned span merges with its neighbours, a cut that reserves
    /// and cancels repeatedly leaves the allocator exactly as it found it. A give-back that cannot be recorded would be
    /// a broken invariant rather than an ordinary outcome: between any two free spans lies at least one slot in use, so
    /// the list cannot be longer than half the capacity plus one, which is what it is sized for.
    /// </para>
    /// <para>
    /// The main thread alone uses the allocator. A failed request returns false and changes nothing.
    /// </para>
    /// </summary>
    public sealed class VpSpanAllocator
    {
        // Free spans by ascending start, none empty and none adjacent.
        private readonly int[] _freeStarts;
        private readonly int[] _freeCounts;
        private int _freeSpanCount;

        public VpSpanAllocator(int capacity)
        {
            if (capacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must not be negative.");
            }

            Capacity = capacity;

            // Between any two free spans lies a slot in use, so at most half the slots, plus one, can be the starts of
            // free spans. Worked out in 64 bits: at a capacity near int.MaxValue the same expression in ints wraps
            // negative, and a bound that wrapped would not be one.
            int maxFreeSpans = (int)Math.Min(int.MaxValue, ((long)capacity + 1) / 2 + 1);
            _freeStarts = new int[maxFreeSpans];
            _freeCounts = new int[maxFreeSpans];
            if (capacity > 0)
            {
                _freeStarts[0] = 0;
                _freeCounts[0] = capacity;
                _freeSpanCount = 1;
            }
        }

        /// <summary>How many slots there are in all.</summary>
        public int Capacity { get; }

        /// <summary>How many slots are not free: taken by an open span or by something published.</summary>
        public int Used
        {
            get
            {
                int free = 0;
                for (int i = 0; i < _freeSpanCount; i++)
                {
                    free += _freeCounts[i];
                }

                return Capacity - free;
            }
        }

        /// <summary>How many free spans there are, which is what fragmentation looks like from outside.</summary>
        public int FreeSpanCount => _freeSpanCount;

        /// <summary>The largest span that could be taken right now.</summary>
        public int LargestFreeSpan
        {
            get
            {
                int largest = 0;
                for (int i = 0; i < _freeSpanCount; i++)
                {
                    largest = Math.Max(largest, _freeCounts[i]);
                }

                return largest;
            }
        }

        /// <summary>
        /// Whether every slot of <paramref name="count"/> from <paramref name="start"/> is taken -- none of it free.
        /// An empty range is taken by this reading, and a range outside the capacity is not.
        /// </summary>
        public bool IsWhollyTaken(int start, int count)
        {
            if (count < 0 || start < 0 || start > Capacity - count)
            {
                return false;
            }

            int end = start + count;
            for (int i = 0; i < _freeSpanCount; i++)
            {
                int freeEnd = _freeStarts[i] + _freeCounts[i];
                if (_freeStarts[i] < end && start < freeEnd)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Takes <paramref name="count"/> contiguous slots, at the start of the first free span that holds them. A
        /// count of zero takes nothing and answers 0, which is a position no caller writes through. False when no free
        /// span is large enough, having changed nothing.
        /// </summary>
        public bool TryTake(int count, out int start)
        {
            start = 0;
            if (count < 0)
            {
                return false;
            }

            if (count == 0)
            {
                return true;
            }

            for (int i = 0; i < _freeSpanCount; i++)
            {
                if (_freeCounts[i] < count)
                {
                    continue;
                }

                start = _freeStarts[i];
                if (_freeCounts[i] == count)
                {
                    RemoveFreeSpan(i);
                }
                else
                {
                    _freeStarts[i] += count;
                    _freeCounts[i] -= count;
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Gives <paramref name="count"/> slots from <paramref name="start"/> back, merging them with any free
        /// neighbours. A count of zero does nothing. Throws when the span is outside the capacity or overlaps a span
        /// that is already free: either is a caller giving back what it does not hold.
        /// </summary>
        public void GiveBack(int start, int count)
        {
            if (count == 0)
            {
                return;
            }

            if (count < 0 || start < 0 || start > Capacity - count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(start), start, "A span given back lies within the capacity: " + count + " from " + start + " of " + Capacity + ".");
            }

            int end = start + count;
            int at = 0;
            while (at < _freeSpanCount && _freeStarts[at] < start)
            {
                at++;
            }

            if ((at > 0 && _freeStarts[at - 1] + _freeCounts[at - 1] > start)
                || (at < _freeSpanCount && _freeStarts[at] < end))
            {
                throw new InvalidOperationException(
                    "a span given back overlaps one that is already free: " + count + " from " + start + ".");
            }

            bool joinsBefore = at > 0 && _freeStarts[at - 1] + _freeCounts[at - 1] == start;
            bool joinsAfter = at < _freeSpanCount && _freeStarts[at] == end;
            if (joinsBefore && joinsAfter)
            {
                _freeCounts[at - 1] += count + _freeCounts[at];
                RemoveFreeSpan(at);
                return;
            }

            if (joinsBefore)
            {
                _freeCounts[at - 1] += count;
                return;
            }

            if (joinsAfter)
            {
                _freeStarts[at] = start;
                _freeCounts[at] += count;
                return;
            }

            if (_freeSpanCount == _freeStarts.Length)
            {
                // Between any two free spans lies a slot in use, so this cannot happen for a caller giving back what
                // it holds. Reaching it means the bookkeeping is wrong, not that the storage is full.
                throw new InvalidOperationException("the free span list is full, which its bound says cannot happen.");
            }

            for (int i = _freeSpanCount; i > at; i--)
            {
                _freeStarts[i] = _freeStarts[i - 1];
                _freeCounts[i] = _freeCounts[i - 1];
            }

            _freeStarts[at] = start;
            _freeCounts[at] = count;
            _freeSpanCount++;
        }

        private void RemoveFreeSpan(int index)
        {
            for (int i = index; i < _freeSpanCount - 1; i++)
            {
                _freeStarts[i] = _freeStarts[i + 1];
                _freeCounts[i] = _freeCounts[i + 1];
            }

            _freeSpanCount--;
        }
    }
}
