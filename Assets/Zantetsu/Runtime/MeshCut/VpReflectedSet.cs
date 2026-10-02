using System.Collections;
using System.Collections.Generic;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The boundaries a display's registration reflects, held as an unchangeable value with its lookup made once
    /// (2026-10-01): made when the registration is taken in (from the caller's set, copied) and when a commit adds a
    /// boundary (a new set: the old one's boundaries and the new one), never changed after. A snapshot reads a set of
    /// this type through its lookup instead of indexing it again at every structure build; any other collection it is
    /// given is read again at every build, as before. Its boundaries keep their order and their repeats; a boundary is
    /// in it when one of them equals it by <see cref="VpClipBoundary"/>'s own equality (the ledger by reference, the
    /// operation, the side: +0 and -0 alike, a NaN side never).
    /// </summary>
    internal sealed class VpReflectedSet : IReadOnlyCollection<VpClipBoundary>
    {
        /// <summary>What the sets made cost, for observation (a display's own).</summary>
        internal sealed class Counts
        {
            public long made, entries;
            public double seconds;
        }

        internal static readonly VpReflectedSet Empty = new VpReflectedSet(System.Array.Empty<VpClipBoundary>(), null, 0);

        private readonly VpClipBoundary[] _items;
        private readonly HashSet<VpClipBoundary> _lookup;

        // Made from its entry (Of, With) at <paramref name="begin"/>: the time counted is from there to here, the array's
        // making and copy included, once (2026-10-01: it used to start here and leave those out).
        private VpReflectedSet(VpClipBoundary[] items, Counts counts, long begin)
        {
            _items = items;
            _lookup = new HashSet<VpClipBoundary>(VpReflectedIndex.BoundaryEquality.Instance);
            foreach (VpClipBoundary boundary in items)
            {
                _lookup.Add(boundary);
            }

            if (counts != null)
            {
                counts.made++;
                counts.entries += items.Length;
                counts.seconds += (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }

        /// <summary>A set of the caller's boundaries, copied in their order.</summary>
        internal static VpReflectedSet Of(IReadOnlyCollection<VpClipBoundary> boundaries, Counts counts)
        {
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            var items = new VpClipBoundary[boundaries.Count];
            int k = 0;
            foreach (VpClipBoundary boundary in boundaries)
            {
                items[k++] = boundary;
            }

            return new VpReflectedSet(items, counts, begin);
        }

        /// <summary>A new set: these boundaries and one more after them. This set is not changed.</summary>
        internal VpReflectedSet With(VpClipBoundary boundary, Counts counts)
        {
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            var items = new VpClipBoundary[_items.Length + 1];
            System.Array.Copy(_items, items, _items.Length);
            items[_items.Length] = boundary;
            return new VpReflectedSet(items, counts, begin);
        }

        /// <summary>Whether one of the boundaries equals <paramref name="boundary"/>.</summary>
        internal bool Contains(VpClipBoundary boundary) => _lookup.Contains(boundary);

        public int Count => _items.Length;

        /// <summary>The boundary at <paramref name="index"/>, in the order they were given (read only).</summary>
        internal VpClipBoundary this[int index] => _items[index];

        public IEnumerator<VpClipBoundary> GetEnumerator()
        {
            for (int i = 0; i < _items.Length; i++)
            {
                yield return _items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
