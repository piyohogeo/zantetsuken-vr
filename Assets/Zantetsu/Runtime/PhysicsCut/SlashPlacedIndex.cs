using System;
using Unity.Mathematics;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The world boxes of the placed targets whose placement is told (DESIGN 19.1.7, D-192;
    /// <see cref="SlashHitDetector.AddPlacedStanding"/>), as a tree of boxes: the detector asks it once an update, with
    /// the box of all the update's sweeps, for the targets a sweep could meet at all, and only those are read and tested
    /// as every placed target was before. It is this detector's own and nothing else's: no general spatial service.
    /// <para>
    /// **The boxes are conservative.** An entry's box is the world box the detector's own per-update test would make for
    /// it (the box of its convexes in its frame, carried by its position and rotation), widened by a pad ten times that
    /// test's margin; the search then uses that test's margin again, on every node and every entry, with the same rule
    /// (touching counts). So whatever that test would keep for any sweep of the update is kept here: an entry is only
    /// ever left out when its box and the box of all the sweeps are apart by more than the margins. Centres, distances
    /// to a player or sizes play no part.
    /// </para>
    /// <para>
    /// **Built whole, lazily.** The tree is a binary tree over the entries, split at the median of their centres on the
    /// longest axis (by a selection: the smaller half before the larger, in no order within a half), up to four entries
    /// a leaf. An entry added or changed marks the tree stale, and it is built again whole at the next search, before
    /// anything is searched -- never searched with a box that was given another. An entry taken away is cleared where
    /// it stands (the tree is kept; a search passes it over) and the tree is built again once a quarter of it is
    /// cleared. Nothing is allocated by a search; a build allocates only when the number of entries has outgrown its
    /// arrays.
    /// </para>
    /// <para>
    /// **No order of its own.** What a search finds comes out in the tree's order, each entry once (an entry is in one
    /// leaf); the detector puts them back in the order the targets were added, which is the order it has always tested
    /// them in.
    /// </para>
    /// </summary>
    internal sealed class SlashPlacedIndex<T> where T : class
    {
        private const int LeafSize = 4;

        // The entries, in no order (a taken entry's place is given to the last one).
        private T[] _items = Array.Empty<T>();
        private float3[] _lo = Array.Empty<float3>(), _hi = Array.Empty<float3>();
        private int _count;

        // The tree over the entries as they stood at the last build: an entry cleared since is null in _built.
        private T[] _built = Array.Empty<T>();           // by the tree's own order of entries
        private float3[] _builtLo = Array.Empty<float3>(), _builtHi = Array.Empty<float3>();
        private float[] _builtReach = Array.Empty<float>();   // the largest coordinate of an entry's box, for the margin
        private int _builtCount, _cleared;
        private float3[] _nodeLo = Array.Empty<float3>(), _nodeHi = Array.Empty<float3>();
        private float[] _nodeReach = Array.Empty<float>();
        private int[] _nodeLeft = Array.Empty<int>();    // an inner node's left child (the right is the next); -1 for a leaf
        private int[] _nodeStart = Array.Empty<int>(), _nodeCount = Array.Empty<int>();   // a leaf's entries in _built
        private int _nodes;
        private bool _stale;
        private float[] _keys = Array.Empty<float>();
        private float[] _cx = Array.Empty<float>(), _cy = Array.Empty<float>(), _cz = Array.Empty<float>();   // the entries' centres, for a build
        private int[] _order = Array.Empty<int>();
        private readonly int[] _stack = new int[128];

        /// <summary>The entries held.</summary>
        public int Count => _count;

        /// <summary>Observation: how often the tree was built, how many nodes the last build made, and what the searches visited.</summary>
        public long Builds { get; private set; }
        public int Nodes => _nodes;
        public long Searches { get; private set; }
        public long NodesVisited { get; private set; }
        public long EntriesTested { get; private set; }
        public long EntriesFound { get; private set; }
        public double LastBuildSeconds { get; private set; }
        public double BuildSecondsInAll { get; private set; }

        /// <summary>The margin the detector's own box test gives two boxes of these reaches (touching counts).</summary>
        public static float Margin(float reachA, float reachB) => 1e-4f * math.max(reachA, reachB) + 1e-4f;

        public static float Reach(float3 lo, float3 hi) => math.cmax(math.max(math.abs(lo), math.abs(hi)));

        /// <summary>
        /// Adds an entry with its world box, widened here by the pad. False, with nothing added, when the box is not
        /// finite or is inside out: such a target cannot be found by a box and stays with the search that reads it.
        /// </summary>
        public bool TryAdd(T item, float3 lo, float3 hi, out int slot)
        {
            slot = -1;
            if (item == null || !TryPad(ref lo, ref hi))
            {
                return false;
            }

            if (_count == _items.Length)
            {
                int room = Math.Max(16, _items.Length * 2);
                Array.Resize(ref _items, room);
                Array.Resize(ref _lo, room);
                Array.Resize(ref _hi, room);
            }

            slot = _count++;
            _items[slot] = item;
            _lo[slot] = lo;
            _hi[slot] = hi;
            _stale = true;
            return true;
        }

        /// <summary>
        /// Gives the entry at <paramref name="slot"/> another box. False, with the entry left as it was, when the box
        /// cannot be used: the caller then takes the entry out.
        /// </summary>
        public bool TryChange(int slot, float3 lo, float3 hi)
        {
            if (slot < 0 || slot >= _count || !TryPad(ref lo, ref hi))
            {
                return false;
            }

            _lo[slot] = lo;
            _hi[slot] = hi;
            _stale = true;   // never searched with the box it had: the tree is built again before the next search
            return true;
        }

        /// <summary>
        /// Takes the entry at <paramref name="slot"/> out. Its place is given to the last entry, which is returned with
        /// the slot it now has (null when the entry taken was the last): the caller keeps that entry's slot.
        /// </summary>
        public T RemoveAt(int slot, out int movedTo)
        {
            movedTo = -1;
            if (slot < 0 || slot >= _count)
            {
                throw new ArgumentOutOfRangeException(nameof(slot));
            }

            T taken = _items[slot];
            int last = --_count;
            T moved = null;
            if (slot != last)
            {
                moved = _items[last];
                _items[slot] = moved;
                _lo[slot] = _lo[last];
                _hi[slot] = _hi[last];
                movedTo = slot;
            }

            _items[last] = null;
            if (_count == 0)
            {
                // The last one: nothing is searched until an entry is added, and the tree holds no entry meanwhile.
                Array.Clear(_built, 0, _builtCount);
                _builtCount = 0;
                _cleared = 0;
                _stale = true;
            }
            else if (!_stale)
            {
                // The tree is kept, the entry cleared in it; built again once a quarter of it is cleared.
                for (int i = 0; i < _builtCount; i++)
                {
                    if (ReferenceEquals(_built[i], taken))
                    {
                        _built[i] = null;
                        _cleared++;
                        break;
                    }
                }

                if (_cleared * 4 > _builtCount)
                {
                    _stale = true;
                }
            }

            return moved;
        }

        /// <summary>
        /// Every entry whose box is not apart from <paramref name="lo"/>..<paramref name="hi"/> (a box of reach
        /// <paramref name="reach"/>) by more than the margin, into <paramref name="found"/> from its start, in the tree's
        /// order, each once; the number found. The tree is built first when an entry was added or changed since it was
        /// built. <paramref name="found"/> is made larger only when it is too small for the entries held.
        /// </summary>
        public int Search(float3 lo, float3 hi, float reach, ref T[] found)
        {
            if (_count == 0)
            {
                return 0;
            }

            if (_stale)
            {
                Build();
            }

            if (found == null || found.Length < _count)
            {
                found = new T[Math.Max(_count, found == null ? 16 : found.Length * 2)];
            }

            Searches++;
            int n = 0, top = 0, visited = 0, tested = 0;
            _stack[top++] = 0;
            while (top > 0)
            {
                int node = _stack[--top];
                visited++;
                float margin = Margin(reach, _nodeReach[node]);
                if (math.any(hi < _nodeLo[node] - margin) || math.any(_nodeHi[node] + margin < lo))
                {
                    continue;
                }

                int left = _nodeLeft[node];
                if (left >= 0)
                {
                    _stack[top++] = left;
                    _stack[top++] = left + 1;
                    continue;
                }

                int end = _nodeStart[node] + _nodeCount[node];
                for (int i = _nodeStart[node]; i < end; i++)
                {
                    T item = _built[i];
                    if (item == null)
                    {
                        continue;   // taken out since the tree was built
                    }

                    tested++;
                    float m = Margin(reach, _builtReach[i]);
                    if (math.any(hi < _builtLo[i] - m) || math.any(_builtHi[i] + m < lo))
                    {
                        continue;
                    }

                    found[n++] = item;
                }
            }

            NodesVisited += visited;
            EntriesTested += tested;
            EntriesFound += n;
            return n;
        }

        // The pad: ten times the box test's margin for a box of this reach, on every side.
        private static bool TryPad(ref float3 lo, ref float3 hi)
        {
            if (!math.all(math.isfinite(lo) & math.isfinite(hi)) || math.any(hi < lo))
            {
                return false;
            }

            float pad = 1e-3f * Reach(lo, hi) + 1e-3f;
            lo -= pad;
            hi += pad;
            return math.all(math.isfinite(lo) & math.isfinite(hi));
        }

        private void Build()
        {
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            CutWorldFrameNotes.Note(CutWorldFrameNotes.Work.StaticIndexRebuild);
            if (_built.Length < _count)
            {
                int room = Math.Max(_count, _built.Length * 2);
                _built = new T[room];
                _builtLo = new float3[room];
                _builtHi = new float3[room];
                _builtReach = new float[room];
                _keys = new float[room];
                _order = new int[room];
                _cx = new float[room];
                _cy = new float[room];
                _cz = new float[room];
                // A tree of n entries, four a leaf at most and a split never empty, has fewer than 2n nodes.
                int nodes = Math.Max(2, 2 * room);
                _nodeLo = new float3[nodes];
                _nodeHi = new float3[nodes];
                _nodeReach = new float[nodes];
                _nodeLeft = new int[nodes];
                _nodeStart = new int[nodes];
                _nodeCount = new int[nodes];
            }
            else
            {
                Array.Clear(_built, 0, _builtCount);   // no entry of the tree before is kept alive by it
            }

            for (int i = 0; i < _count; i++)
            {
                _order[i] = i;
                float3 lo = _lo[i], hi = _hi[i];
                _cx[i] = 0.5f * (lo.x + hi.x);
                _cy[i] = 0.5f * (lo.y + hi.y);
                _cz[i] = 0.5f * (lo.z + hi.z);
            }

            // The shape first: which entries each node holds.
            _nodes = 1;
            Split(0, 0, _count);
            for (int i = 0; i < _count; i++)
            {
                int from = _order[i];
                _built[i] = _items[from];
                _builtLo[i] = _lo[from];
                _builtHi[i] = _hi[from];
                _builtReach[i] = Reach(_lo[from], _hi[from]);
            }

            // Then the boxes, from the leaves up: a node's children were numbered after it.
            for (int node = _nodes - 1; node >= 0; node--)
            {
                float3 lo, hi;
                int left = _nodeLeft[node];
                if (left < 0)
                {
                    int start = _nodeStart[node], end = start + _nodeCount[node];
                    lo = _builtLo[start];
                    hi = _builtHi[start];
                    for (int i = start + 1; i < end; i++)
                    {
                        lo = math.min(lo, _builtLo[i]);
                        hi = math.max(hi, _builtHi[i]);
                    }
                }
                else
                {
                    lo = math.min(_nodeLo[left], _nodeLo[left + 1]);
                    hi = math.max(_nodeHi[left], _nodeHi[left + 1]);
                }

                _nodeLo[node] = lo;
                _nodeHi[node] = hi;
                _nodeReach[node] = Reach(lo, hi);
            }

            _builtCount = _count;
            _cleared = 0;
            _stale = false;
            Builds++;
            LastBuildSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
            BuildSecondsInAll += LastBuildSeconds;
        }

        // The node over _order[start .. start + count): a leaf, or two children split at the median of the entries'
        // centres on the longest axis of those centres. Its box is made afterwards (Build).
        private void Split(int node, int start, int count)
        {
            if (count <= LeafSize)
            {
                _nodeLeft[node] = -1;
                _nodeStart[node] = start;
                _nodeCount[node] = count;
                return;
            }

            int end = start + count;
            int first = _order[start];
            float minX = _cx[first], maxX = minX, minY = _cy[first], maxY = minY, minZ = _cz[first], maxZ = minZ;
            for (int i = start + 1; i < end; i++)
            {
                int e = _order[i];
                float x = _cx[e], y = _cy[e], z = _cz[e];
                if (x < minX) minX = x; else if (x > maxX) maxX = x;
                if (y < minY) minY = y; else if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; else if (z > maxZ) maxZ = z;
            }

            float ex = maxX - minX, ey = maxY - minY, ez = maxZ - minZ;
            float[] centres = ex >= ey ? (ex >= ez ? _cx : _cz) : (ey >= ez ? _cy : _cz);
            for (int i = start; i < end; i++)
            {
                _keys[i] = centres[_order[i]];
            }

            int half = count / 2;
            SelectSmallerHalf(start, count, start + half);
            int left = _nodes;
            _nodes += 2;
            _nodeLeft[node] = left;
            _nodeStart[node] = 0;
            _nodeCount[node] = 0;
            Split(left, start, half);
            Split(left + 1, start + half, count - half);
        }

        // Arranges _keys[start .. start + count) (and _order with it) so that no key before k is larger than the key at k
        // and none after it smaller: the smaller half before the larger, which is all a split needs. A selection
        // (quickselect, the middle key as the pivot), not a sort: equal keys -- a row of targets on one line -- cost it
        // nothing more.
        private void SelectSmallerHalf(int start, int count, int k)
        {
            int lo = start, hi = start + count - 1;
            while (lo < hi)
            {
                float pivot = _keys[(lo + hi) >> 1];
                int i = lo, j = hi;
                while (i <= j)
                {
                    while (_keys[i] < pivot) i++;
                    while (_keys[j] > pivot) j--;
                    if (i <= j)
                    {
                        float key = _keys[i];
                        _keys[i] = _keys[j];
                        _keys[j] = key;
                        int entry = _order[i];
                        _order[i] = _order[j];
                        _order[j] = entry;
                        i++;
                        j--;
                    }
                }

                if (k <= j) hi = j;
                else if (k >= i) lo = i;
                else return;   // k is among the keys equal to the pivot, between the two parts
            }
        }
    }
}
