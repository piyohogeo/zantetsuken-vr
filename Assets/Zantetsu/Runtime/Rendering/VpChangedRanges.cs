using System;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The parts of one array that were written since they were last sent somewhere: a few ranges, recorded as the
    /// writes happen (DESIGN 5.6). Nothing here looks at the array -- no element is compared, scanned or hashed to find
    /// out what changed; a writer says where it wrote.
    /// <para>
    /// The ranges are kept in order, none empty, and no two closer than the merge gap: a range added that overlaps a
    /// kept one, touches it, or lies within the gap of it is joined to it, so what a reader gets is never more ranges
    /// than there are separate places written. There is room for a fixed number of them; one more joins the two that
    /// lie closest. A joined range covers elements nobody wrote: whoever sends it sends those too, so they must be
    /// current where it is read from, which is the owner's to keep.
    /// </para>
    /// <para>
    /// The main thread alone uses it. Nothing is allocated after it is made.
    /// </para>
    /// </summary>
    public sealed class VpChangedRanges
    {
        private readonly int[] _starts;
        private readonly int[] _ends;
        private readonly int _capacity;
        private readonly int _mergeGap;
        private int _count;

        /// <param name="capacity">How many separate ranges are kept before the closest two are joined.</param>
        /// <param name="mergeGap">Ranges with no more than this many elements between them are kept as one.</param>
        public VpChangedRanges(int capacity, int mergeGap)
        {
            if (capacity <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be positive.");
            }

            if (mergeGap < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(mergeGap), mergeGap, "Must not be negative.");
            }

            _capacity = capacity;
            _mergeGap = mergeGap;

            // One more than is kept: a range is put in first and the closest two joined afterwards.
            _starts = new int[capacity + 1];
            _ends = new int[capacity + 1];
        }

        public int Count => _count;

        public bool IsEmpty => _count == 0;

        public int StartAt(int index) => index >= 0 && index < _count ? _starts[index] : throw new ArgumentOutOfRangeException(nameof(index));

        public int EndAt(int index) => index >= 0 && index < _count ? _ends[index] : throw new ArgumentOutOfRangeException(nameof(index));

        /// <summary>How many elements the ranges cover together.</summary>
        public long Elements
        {
            get
            {
                long total = 0;
                for (int i = 0; i < _count; i++)
                {
                    total += _ends[i] - _starts[i];
                }

                return total;
            }
        }

        public void Clear()
        {
            _count = 0;
        }

        /// <summary>Records that [<paramref name="start"/>, <paramref name="end"/>) was written. An empty range records nothing.</summary>
        public void Add(int start, int end)
        {
            if (start < 0 || end < start)
            {
                throw new ArgumentOutOfRangeException(nameof(start), start, "A range written is [start, end) with 0 <= start <= end.");
            }

            if (end == start)
            {
                return;
            }

            // Every kept range the new one reaches -- within the gap -- is joined into it and taken out; they are
            // neighbours in the order, so what is left stays in order.
            int at = 0;
            while (at < _count && (long)_ends[at] + _mergeGap < start)
            {
                at++;
            }

            int last = at;
            while (last < _count && (long)_starts[last] <= (long)end + _mergeGap)
            {
                start = Math.Min(start, _starts[last]);
                end = Math.Max(end, _ends[last]);
                last++;
            }

            int removed = last - at;
            if (removed != 1)
            {
                // Make room for exactly one range at 'at': close up over those joined, or open up when none was.
                int shift = 1 - removed;
                if (shift > 0)
                {
                    for (int i = _count - 1; i >= at; i--)
                    {
                        _starts[i + 1] = _starts[i];
                        _ends[i + 1] = _ends[i];
                    }
                }
                else
                {
                    for (int i = last; i < _count; i++)
                    {
                        _starts[i + shift] = _starts[i];
                        _ends[i + shift] = _ends[i];
                    }
                }

                _count += shift;
            }

            _starts[at] = start;
            _ends[at] = end;
            if (_count > _capacity)
            {
                JoinClosest();
            }
        }

        /// <summary>Records every range of <paramref name="other"/> here as well.</summary>
        public void AddAll(VpChangedRanges other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }

            for (int i = 0; i < other._count; i++)
            {
                Add(other._starts[i], other._ends[i]);
            }
        }

        // One range too many: the two with the fewest elements between them become one.
        private void JoinClosest()
        {
            int closest = 0;
            long gap = long.MaxValue;
            for (int i = 0; i + 1 < _count; i++)
            {
                long between = (long)_starts[i + 1] - _ends[i];
                if (between < gap)
                {
                    gap = between;
                    closest = i;
                }
            }

            _ends[closest] = _ends[closest + 1];
            for (int i = closest + 1; i + 1 < _count; i++)
            {
                _starts[i] = _starts[i + 1];
                _ends[i] = _ends[i + 1];
            }

            _count--;
        }
    }
}
