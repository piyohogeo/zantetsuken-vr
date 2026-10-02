using System.Collections;
using System.Collections.Generic;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// A registration's reflected boundaries, looked up by value (2026-10-01): for one structure build, read by its
    /// validation and its collection instead of each ancestor's boundary being looked for from the start of the set.
    /// A display's own unchangeable set (<see cref="VpReflectedSet"/>) is looked up through the lookup it was made with;
    /// any other collection -- the caller's list or array, whose contents may change under the same reference and count
    /// -- is indexed again from its contents at every build. **The same answer as the set's own scan:** a boundary is in
    /// it when an item of the set equals it -- the same ledger (by reference), the same operation, the same side
    /// (<see cref="VpClipBoundary"/>'s own equality) -- so +0 and -0 are one side and a NaN side is never in it. It holds
    /// nothing past a build (<see cref="Release"/>).
    /// </summary>
    internal sealed class VpReflectedIndex : IReadOnlyCollection<VpClipBoundary>
    {
        internal sealed class BoundaryEquality : IEqualityComparer<VpClipBoundary>
        {
            public static readonly BoundaryEquality Instance = new BoundaryEquality();
            public bool Equals(VpClipBoundary a, VpClipBoundary b) => a == b;

            // Equal boundaries hash alike: the face by its operation, the side with -0 as +0 (they are equal).
            public int GetHashCode(VpClipBoundary b) => (b.face.GetHashCode() * 397) ^ (b.side == 0f ? 0 : b.side.GetHashCode());
        }

        /// <summary>What the lookups of one snapshot did, for observation and for the comparison with the scan.</summary>
        internal sealed class Counts
        {
            public long lookups, comparisons, entries, built, reused, byPosition;
        }

        private readonly HashSet<VpClipBoundary> _set = new HashSet<VpClipBoundary>(BoundaryEquality.Instance);
        private IReadOnlyCollection<VpClipBoundary> _source;
        private VpReflectedSet _shared;
        private bool _scan;
        private Counts _counts;

        /// <summary>
        /// Filled from <paramref name="source"/> for one build: a display's set by its own lookup (unless
        /// <paramref name="indexAgain"/>, a test's comparison), anything else indexed again from its contents. With
        /// <paramref name="scan"/> nothing is indexed and each lookup scans the set from its start, as every lookup did
        /// before the index (a test's comparison of the two).
        /// </summary>
        public void Fill(IReadOnlyCollection<VpClipBoundary> source, bool scan, Counts counts, bool indexAgain = false)
        {
            _source = source;
            _scan = scan;
            _counts = counts;
            _set.Clear();
            _shared = null;
            if (scan)
            {
                return;
            }

            if (!indexAgain && source is VpReflectedSet set)
            {
                _shared = set;
                counts.reused++;
                return;
            }

            counts.built++;
            foreach (VpClipBoundary boundary in source)
            {
                _set.Add(boundary);
                counts.entries++;
            }
        }

        /// <summary>Lets go of the build's set: nothing of a registration is held past its build.</summary>
        public void Release()
        {
            _source = null;
            _shared = null;
            _set.Clear();
        }

        /// <summary>Whether the build's set is still held (tests).</summary>
        internal bool HoldsForTest => _source != null || _shared != null;

        /// <summary>
        /// The same answer as <see cref="Contains"/>, asked first of the display set's boundary at <paramref name="position"/>
        /// (2026-10-01: a display appends each cut's boundary, so the k-th ancestor up a registration's chain is the k-th from
        /// its set's end): equal there, it is in the set; anywhere else -- another position, another kind of set, a position
        /// outside it -- the set is asked as always.
        /// </summary>
        public bool ContainsAt(int position, VpClipBoundary boundary)
        {
            if (_shared != null && !byPositionOffForTest && (uint)position < (uint)_shared.Count && _shared[position] == boundary)
            {
                _counts.lookups++;
                _counts.byPosition++;
                return true;
            }

            return Contains(boundary);
        }

        /// <summary>Tests only: every lookup asks the set's hash (the lookup before 2026-10-01).</summary>
        internal static bool byPositionOffForTest;

        /// <summary>Whether the registration's set holds a boundary equal to <paramref name="boundary"/>.</summary>
        public bool Contains(VpClipBoundary boundary)
        {
            _counts.lookups++;
            if (_shared != null)
            {
                return _shared.Contains(boundary);
            }

            if (!_scan)
            {
                return _set.Contains(boundary);
            }

            foreach (VpClipBoundary item in _source)
            {
                _counts.comparisons++;
                if (item == boundary)
                {
                    return true;
                }
            }

            return false;
        }

        public int Count => _source != null ? _source.Count : 0;

        public IEnumerator<VpClipBoundary> GetEnumerator() => (_source ?? System.Array.Empty<VpClipBoundary>()).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
