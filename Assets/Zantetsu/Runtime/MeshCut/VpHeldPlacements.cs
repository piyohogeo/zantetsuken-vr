using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// What the held placements did, counted (2026-10-07, DESIGN 5.6, D-205). Cumulative in a display; a collection's
    /// share is taken as a difference. A Place pass is of one kind:
    /// <list type="bullet">
    /// <item>**selective** -- the structure kept, the host vouching for its step count and naming its reference
    /// points: the ordinary targets and the held ones a proximity box finds are asked, the other held ones are not
    /// (<see cref="omitted"/>);</item>
    /// <item>**structure** -- the structure was gone through again: every render fragment asked, nothing held;</item>
    /// <item>**unvouched** -- the structure kept, but no step count vouched for or no reference points: every render
    /// fragment asked, nothing held;</item>
    /// <item>**other** -- a test's or a diagnosis's pass that asks everything.</item>
    /// </list>
    /// So, a pass that ran to its end: render fragments = omitted + queried (ordinary + near + selected + shifted) in a
    /// selective one, = queried (structure / unvouched / other) in the others. A collection that let the adopted snapshot stand
    /// (D-204) runs no pass and is counted nowhere here.
    /// </summary>
    public struct VpHeldPlacementTotals
    {
        public long passesSelective, passesStructure, passesUnvouched, passesOther;

        // A selective pass: held and outside every proximity box -- not asked; asked because not held (moving, or not
        // yet seen standing over two step results); asked because held and inside a proximity box; asked because it
        // has selected boundaries (clipped: never held).
        public long omitted, queriedOrdinary, queriedNear, queriedSelected;

        // The passes that ask everything, by why.
        public long queriedStructure, queriedUnvouched, queriedOther;

        // Ordinary targets that became held; held ones found moved (or reached by shifted ranges while near) and made
        // ordinary again; times every held one was made ordinary because a placement input changed outside a step;
        // times everything was forgotten for a structure gone through again.
        public long promoted, demoted, invalidations, structureResets;

        // Selective passes in which a render fragment made conditions or caps, so that every later one's ranges begin
        // elsewhere and each is asked and built again as before; and the held, far ones among those, asked after all
        // (not in omitted, not in the three above).
        public long shiftedPasses, queriedShifted;

        // The tree: searches (one a proximity box a selective pass) and their time whole, with the marking of the
        // ordinary targets; and the time of the insertions and removals (inside the Place pass's own time).
        public long searches;
        public double searchSeconds, updateSeconds;

        // Not sums: as they stood after the last collection added.
        public int heldAtEnd, ordinaryAtEnd, pointsAtEnd;

        public void Add(in VpHeldPlacementTotals a)
        {
            passesSelective += a.passesSelective; passesStructure += a.passesStructure; passesUnvouched += a.passesUnvouched; passesOther += a.passesOther;
            omitted += a.omitted; queriedOrdinary += a.queriedOrdinary; queriedNear += a.queriedNear; queriedSelected += a.queriedSelected;
            queriedStructure += a.queriedStructure; queriedUnvouched += a.queriedUnvouched; queriedOther += a.queriedOther;
            promoted += a.promoted; demoted += a.demoted; invalidations += a.invalidations; structureResets += a.structureResets;
            shiftedPasses += a.shiftedPasses; queriedShifted += a.queriedShifted;
            searches += a.searches; searchSeconds += a.searchSeconds; updateSeconds += a.updateSeconds;
            heldAtEnd = a.heldAtEnd; ordinaryAtEnd = a.ordinaryAtEnd; pointsAtEnd = a.pointsAtEnd;
        }

        public void Subtract(in VpHeldPlacementTotals a)
        {
            passesSelective -= a.passesSelective; passesStructure -= a.passesStructure; passesUnvouched -= a.passesUnvouched; passesOther -= a.passesOther;
            omitted -= a.omitted; queriedOrdinary -= a.queriedOrdinary; queriedNear -= a.queriedNear; queriedSelected -= a.queriedSelected;
            queriedStructure -= a.queriedStructure; queriedUnvouched -= a.queriedUnvouched; queriedOther -= a.queriedOther;
            promoted -= a.promoted; demoted -= a.demoted; invalidations -= a.invalidations; structureResets -= a.structureResets;
            shiftedPasses -= a.shiftedPasses; queriedShifted -= a.queriedShifted;
            searches -= a.searches; searchSeconds -= a.searchSeconds; updateSeconds -= a.updateSeconds;
        }
    }

    /// <summary>
    /// The render fragments of a display whose placement is held and not asked (2026-10-07, DESIGN 5.6, D-205).
    /// <para>
    /// **Two groups.** A render fragment is *ordinary* -- asked where it stands in every Place pass, however far away --
    /// until a query's answer is, bit for bit, the placement the adopted snapshot holds for it from the result of
    /// **another physics step** (the display's own keep test; nothing more is compared). Then it is *held*: its world
    /// box at that placement, widened by <see cref="Margin"/> on each axis, goes into a tree of boxes. In a Place pass
    /// the ordinary ones are asked, and of the held ones those whose box meets a proximity box -- <see cref="Reach"/>
    /// on each axis about each reference point the host names (its cameras' positions). The other held ones are not
    /// asked: they are drawn, and cast their shadows, where they are held. A held one that is asked and has moved is
    /// ordinary again; one that has not stays in the tree as it is.
    /// </para>
    /// <para>
    /// **Nearness alone decides who is asked.** No view frustum, no shadow volume, no rendering path. A held target
    /// far from every reference point that does move is drawn where it was held until a reference point comes near or
    /// something below forgets it: accepted (TL, 2026-10-07).
    /// </para>
    /// <para>
    /// **What forgets.** The structure gone through again (a registration, a retirement, a publication, a commit, a
    /// selection: the ledger's and the display's own revisions) forgets everything -- the render fragments are numbered
    /// anew. A placement input changed outside a step (the host's count) makes every held one ordinary, so the next
    /// pass asks them all. A pass with no step count vouched for forgets everything and asks everything.
    /// </para>
    /// <para>
    /// **A collection that is not adopted** leaves nothing wrong here: becoming held says that a query's answer was the
    /// adopted placement at a later step, which is so whatever becomes of that collection; becoming ordinary only asks
    /// more; and the step a placement is remembered from is only ever made later, never earlier, than the truth.
    /// </para>
    /// The render fragments are named by their index in the structure the two snapshots keep.
    /// </summary>
    internal sealed class VpHeldPlacements
    {
        internal const float DefaultMargin = 0.5f, DefaultReach = 20f;
        private const long Unknown = long.MinValue;

        /// <summary>Metres added on each axis to a held target's box.</summary>
        public float Margin = DefaultMargin;

        /// <summary>Metres on each axis about a reference point: the proximity box.</summary>
        public float Reach = DefaultReach;

        private readonly Tree _tree = new Tree();
        private long[] _readStep = Array.Empty<long>();   // the step whose result the adopted placement was first seen at
        private int[] _leaf = Array.Empty<int>();         // a held one's leaf in the tree; -1 while ordinary
        private int[] _ordinaryAt = Array.Empty<int>();   // an ordinary one's place in _ordinary; -1 while held
        private int[] _ordinary = Array.Empty<int>();
        private int _ordinaryCount, _count;
        private ulong[] _picked = Array.Empty<ulong>();   // this pass's targets: the ordinary and the held found near
        private int _pickedWords;
        private bool _valid, _outsideKnown;
        private long _stampLedger = -1, _stampInputs = -1, _outside;

        // This pass, as the display said it before the snapshot was built.
        private bool _active;
        private long _step, _passOutside;
        private readonly List<Vector3> _points = new List<Vector3>(4);

        private VpHeldPlacementTotals _totals;

        public VpHeldPlacementTotals Totals
        {
            get
            {
                VpHeldPlacementTotals t = _totals;
                t.heldAtEnd = HeldCount; t.ordinaryAtEnd = _valid ? _ordinaryCount : 0; t.pointsAtEnd = _points.Count;
                return t;
            }
        }

        public int HeldCount => _valid ? _count - _ordinaryCount : 0;
        public bool IsHeld(int renderFragment) => _valid && renderFragment < _count && _leaf[renderFragment] >= 0;

        /// <summary>
        /// Before a snapshot is built: whether the host vouches for its counts and names reference points (it may name
        /// none: then nothing is near), the counts, and the points.
        /// </summary>
        public void SetPass(bool active, long step, long outside, List<Vector3> points)
        {
            _active = active;
            _step = step;
            _passOutside = outside;
            _points.Clear();
            if (active && points != null) for (int i = 0; i < points.Count; i++) _points.Add(points[i]);
        }

        /// <summary>Everything forgotten: the next pass asks everything, and nothing is held until two step results agree again.</summary>
        public void Forget()
        {
            _valid = false;
            _tree.Clear();
            _ordinaryCount = 0;
            _count = 0;
            _outsideKnown = false;
        }

        /// <summary>
        /// The structure was gone through again and its pass asks every render fragment: all ordinary, each placement
        /// known as of this pass's step (or of none, when no count is vouched for).
        /// </summary>
        public void ResetForStructure(int count, long stampLedger, long stampInputs)
        {
            _totals.passesStructure++;
            _totals.structureResets++;
            AllOrdinary(count, _active ? _step : Unknown);
            _valid = stampLedger >= 0 && stampInputs >= 0;
            _stampLedger = stampLedger;
            _stampInputs = stampInputs;
            _outsideKnown = _active;
            _outside = _passOutside;
        }

        /// <summary>A pass that asked everything took this many queries: counted by its kind (0 structure, 1 unvouched, 2 other).</summary>
        public void CountConventional(int kind, long queries)
        {
            if (kind == 0) _totals.queriedStructure += queries;
            else if (kind == 1) _totals.queriedUnvouched += queries;
            else _totals.queriedOther += queries;
        }

        /// <summary>A kept-structure pass that a test or a diagnosis makes ask everything.</summary>
        public void BeginOther()
        {
            _totals.passesOther++;
            Forget();
        }

        /// <summary>
        /// A pass over the kept structure begins. False: it asks every render fragment (no step count vouched for).
        /// True: its targets are picked -- the ordinary ones and the held ones a proximity box meets.
        /// </summary>
        public bool BeginKept(int count, long stampLedger, long stampInputs)
        {
            if (!_active)
            {
                _totals.passesUnvouched++;
                Forget();
                return false;
            }

            if (!_valid || _count != count || _stampLedger != stampLedger || _stampInputs != stampInputs)
            {
                // A structure this was not made for (it is made in the pass that goes through the structure): nothing
                // is known of its placements' steps.
                AllOrdinary(count, Unknown);
                _valid = true;
                _stampLedger = stampLedger;
                _stampInputs = stampInputs;
                _outsideKnown = false;
            }

            if (_outsideKnown && _outside != _passOutside)
            {
                // Something outside a step put an owner somewhere, brought one or took one away: which, nobody says.
                // Every held one is asked again. What is remembered of the steps stands: an answer that is the adopted
                // placement still, at another step, is two step results agreeing.
                if (_count != _ordinaryCount) _totals.invalidations++;
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                _tree.Clear();
                _ordinaryCount = 0;
                for (int r = 0; r < _count; r++)
                {
                    _leaf[r] = -1;
                    _ordinaryAt[r] = _ordinaryCount;
                    _ordinary[_ordinaryCount++] = r;
                }

                _totals.updateSeconds += SecondsSince(began);
            }

            _outside = _passOutside;
            _outsideKnown = true;

            long searchBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            _pickedWords = (_count + 63) >> 6;
            Array.Clear(_picked, 0, _pickedWords);
            for (int i = 0; i < _ordinaryCount; i++)
            {
                int r = _ordinary[i];
                _picked[r >> 6] |= 1UL << (r & 63);
            }

            int near = 0;
            float reach = Reach;
            for (int i = 0; i < _points.Count; i++)
            {
                Vector3 p = _points[i];
                near += _tree.Pick(new float3(p.x - reach, p.y - reach, p.z - reach), new float3(p.x + reach, p.y + reach, p.z + reach), _picked);
                _totals.searches++;
            }

            _totals.searchSeconds += SecondsSince(searchBegan);
            _totals.passesSelective++;
            _totals.omitted += _count - _ordinaryCount - near;
            return true;
        }

        /// <summary>The next of this pass's targets after <paramref name="renderFragment"/> (-1 to begin), in order; -1 at the end.</summary>
        public int NextPicked(int renderFragment)
        {
            int r = renderFragment + 1;
            int word = r >> 6;
            if (word >= _pickedWords) return -1;
            ulong bits = _picked[word] & (ulong.MaxValue << (r & 63));
            while (bits == 0)
            {
                if (++word >= _pickedWords) return -1;
                bits = _picked[word];
            }

            int found = (word << 6) + math.tzcnt(bits);
            return found < _count ? found : -1;
        }

        public bool IsPicked(int renderFragment) => (_picked[renderFragment >> 6] & (1UL << (renderFragment & 63))) != 0;

        /// <summary>Every later render fragment's ranges begin elsewhere from here on in this pass.</summary>
        public void NoteShifted() => _totals.shiftedPasses++;

        /// <summary>
        /// A render fragment was asked and stands, bit for bit, where the adopted snapshot has it (the keep test held).
        /// An ordinary one whose adopted placement is known from another step's result becomes held.
        /// </summary>
        public void Stood(int r, bool wasHeld, bool unpicked, in Bounds localBounds, in Matrix4x4 geometryLocalToWorld)
        {
            if (wasHeld)
            {
                CountHeldAsked(unpicked);
                return;
            }

            _totals.queriedOrdinary++;
            long read = _readStep[r];
            if (read == Unknown)
            {
                _readStep[r] = _step;   // it stands so as of this step; another step's answer will be the second
                return;
            }

            if (read == _step)
            {
                return;   // the same step's result twice says nothing of standing still
            }

            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            WorldBox(localBounds, geometryLocalToWorld, Margin, out float3 lo, out float3 hi);
            _leaf[r] = _tree.Insert(lo, hi, r);
            int at = _ordinaryAt[r], last = _ordinary[--_ordinaryCount];
            _ordinary[at] = last;
            _ordinaryAt[last] = at;
            _ordinaryAt[r] = -1;
            _totals.promoted++;
            _totals.updateSeconds += SecondsSince(began);
        }

        /// <summary>
        /// A render fragment was asked and is placed anew (it moved, it is clipped, or its ranges begin elsewhere): its
        /// placement is of this step, and a held one is ordinary again.
        /// </summary>
        public void PlacedAnew(int r, bool wasHeld, bool unpicked, bool selected)
        {
            if (wasHeld)
            {
                CountHeldAsked(unpicked);
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                _tree.Remove(_leaf[r]);
                _leaf[r] = -1;
                _ordinaryAt[r] = _ordinaryCount;
                _ordinary[_ordinaryCount++] = r;
                _totals.demoted++;
                _totals.updateSeconds += SecondsSince(began);
            }
            else if (selected)
            {
                _totals.queriedSelected++;
            }
            else
            {
                _totals.queriedOrdinary++;
            }

            _readStep[r] = _step;
        }

        // A held one was asked: because a proximity box met it, or -- not picked, counted as not asked when the pass
        // began -- because the ranges before it began elsewhere.
        private void CountHeldAsked(bool unpicked)
        {
            if (unpicked)
            {
                _totals.omitted--;
                _totals.queriedShifted++;
            }
            else
            {
                _totals.queriedNear++;
            }
        }

        private void AllOrdinary(int count, long readStep)
        {
            _tree.Clear();
            if (_readStep.Length < count)
            {
                int room = Math.Max(count, _readStep.Length * 2);
                _readStep = new long[room];
                _leaf = new int[room];
                _ordinaryAt = new int[room];
                _ordinary = new int[room];
                _picked = new ulong[(room + 63) >> 6];
            }

            _count = count;
            _ordinaryCount = count;
            for (int r = 0; r < count; r++)
            {
                _readStep[r] = readStep;
                _leaf[r] = -1;
                _ordinaryAt[r] = r;
                _ordinary[r] = r;
            }
        }

        private static double SecondsSince(long began) =>
            (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>The world box of a local box at a placement, widened by <paramref name="margin"/> on each axis.</summary>
        internal static void WorldBox(in Bounds local, in Matrix4x4 m, float margin, out float3 lo, out float3 hi)
        {
            Vector3 c = local.center, e = local.extents;
            float3 centre = new float3(
                m.m00 * c.x + m.m01 * c.y + m.m02 * c.z + m.m03,
                m.m10 * c.x + m.m11 * c.y + m.m12 * c.z + m.m13,
                m.m20 * c.x + m.m21 * c.y + m.m22 * c.z + m.m23);
            float3 reach = new float3(
                math.abs(m.m00) * e.x + math.abs(m.m01) * e.y + math.abs(m.m02) * e.z + margin,
                math.abs(m.m10) * e.x + math.abs(m.m11) * e.y + math.abs(m.m12) * e.z + margin,
                math.abs(m.m20) * e.x + math.abs(m.m21) * e.y + math.abs(m.m22) * e.z + margin);
            lo = centre - reach;
            hi = centre + reach;
        }

        internal Tree TreeForTest => _tree;

        /// <summary>
        /// A tree of boxes that takes one box in and gives one up at a time (the well-known dynamic tree: a new leaf
        /// goes where it enlarges the boxes above it least, by their surface, and the way back up is kept in balance
        /// by rotations). A leaf holds one item; a search marks, in a set of bits, the items whose boxes meet a box
        /// (touching counts). Nothing is allocated except when the nodes outgrow their array.
        /// </summary>
        internal sealed class Tree
        {
            private struct Node
            {
                public float3 lo, hi;
                public int parent;   // of a free node: the next free one
                public int left, right;   // -1 in a leaf
                public int height;   // 0 a leaf; -1 free
                public int item;
            }

            private Node[] _n = new Node[64];
            private int _root = -1, _free = -1, _used;
            private int[] _stack = new int[64];

            public int Count { get; private set; }

            public void Clear()
            {
                _root = -1;
                _free = -1;
                _used = 0;
                Count = 0;
            }

            /// <summary>Takes a box in; the leaf it is given names it for <see cref="Remove"/>.</summary>
            public int Insert(float3 lo, float3 hi, int item)
            {
                int leaf = Allocate();
                _n[leaf].lo = lo;
                _n[leaf].hi = hi;
                _n[leaf].item = item;
                InsertLeaf(leaf);
                Count++;
                return leaf;
            }

            public void Remove(int leaf)
            {
                RemoveLeaf(leaf);
                Release(leaf);
                Count--;
            }

            /// <summary>Sets the bit of every item whose box meets the box; says how many bits it set that were not set.</summary>
            public int Pick(float3 lo, float3 hi, ulong[] bits)
            {
                if (_root == -1) return 0;
                int newly = 0, top = 0;
                _stack[top++] = _root;
                while (top > 0)
                {
                    int i = _stack[--top];
                    if (math.any(_n[i].lo > hi) || math.any(_n[i].hi < lo)) continue;
                    if (_n[i].left == -1)
                    {
                        int item = _n[i].item;
                        ulong bit = 1UL << (item & 63);
                        if ((bits[item >> 6] & bit) == 0)
                        {
                            bits[item >> 6] |= bit;
                            newly++;
                        }

                        continue;
                    }

                    if (top + 2 > _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
                    _stack[top++] = _n[i].left;
                    _stack[top++] = _n[i].right;
                }

                return newly;
            }

            private int Allocate()
            {
                int i;
                if (_free != -1)
                {
                    i = _free;
                    _free = _n[i].parent;
                }
                else
                {
                    if (_used == _n.Length) Array.Resize(ref _n, _n.Length * 2);
                    i = _used++;
                }

                _n[i].parent = -1;
                _n[i].left = -1;
                _n[i].right = -1;
                _n[i].height = 0;
                _n[i].item = -1;
                return i;
            }

            private void Release(int i)
            {
                _n[i].parent = _free;
                _n[i].height = -1;
                _free = i;
            }

            private static float Surface(float3 lo, float3 hi)
            {
                float3 d = hi - lo;
                return d.x * d.y + d.y * d.z + d.z * d.x;
            }

            private void InsertLeaf(int leaf)
            {
                if (_root == -1)
                {
                    _root = leaf;
                    _n[leaf].parent = -1;
                    return;
                }

                // Down to the node beside which the leaf enlarges the boxes above least.
                float3 lo = _n[leaf].lo, hi = _n[leaf].hi;
                int index = _root;
                while (_n[index].left != -1)
                {
                    int left = _n[index].left, right = _n[index].right;
                    float surface = Surface(_n[index].lo, _n[index].hi);
                    float joined = Surface(math.min(_n[index].lo, lo), math.max(_n[index].hi, hi));
                    float here = 2f * joined;                    // a new parent for this node and the leaf
                    float inherited = 2f * (joined - surface);   // what going down adds to this node
                    float costLeft = Descend(left, lo, hi) + inherited, costRight = Descend(right, lo, hi) + inherited;
                    if (here < costLeft && here < costRight) break;
                    index = costLeft < costRight ? left : right;
                }

                int sibling = index;
                int oldParent = _n[sibling].parent;
                int parent = Allocate();   // may move the array: nothing of it is held across this
                _n[parent].parent = oldParent;
                _n[parent].lo = math.min(lo, _n[sibling].lo);
                _n[parent].hi = math.max(hi, _n[sibling].hi);
                _n[parent].height = _n[sibling].height + 1;
                if (oldParent != -1)
                {
                    if (_n[oldParent].left == sibling) _n[oldParent].left = parent;
                    else _n[oldParent].right = parent;
                }
                else
                {
                    _root = parent;
                }

                _n[parent].left = sibling;
                _n[parent].right = leaf;
                _n[sibling].parent = parent;
                _n[leaf].parent = parent;
                Refit(_n[leaf].parent);
            }

            private float Descend(int child, float3 lo, float3 hi)
            {
                float joined = Surface(math.min(_n[child].lo, lo), math.max(_n[child].hi, hi));
                return _n[child].left == -1 ? joined : joined - Surface(_n[child].lo, _n[child].hi);
            }

            private void RemoveLeaf(int leaf)
            {
                if (leaf == _root)
                {
                    _root = -1;
                    return;
                }

                int parent = _n[leaf].parent;
                int grand = _n[parent].parent;
                int sibling = _n[parent].left == leaf ? _n[parent].right : _n[parent].left;
                if (grand != -1)
                {
                    if (_n[grand].left == parent) _n[grand].left = sibling;
                    else _n[grand].right = sibling;
                    _n[sibling].parent = grand;
                    Release(parent);
                    Refit(grand);
                }
                else
                {
                    _root = sibling;
                    _n[sibling].parent = -1;
                    Release(parent);
                }
            }

            // From a node up to the root: each balanced, then its height and box made from its children's.
            private void Refit(int index)
            {
                while (index != -1)
                {
                    index = Balance(index);
                    int left = _n[index].left, right = _n[index].right;
                    _n[index].height = 1 + math.max(_n[left].height, _n[right].height);
                    _n[index].lo = math.min(_n[left].lo, _n[right].lo);
                    _n[index].hi = math.max(_n[left].hi, _n[right].hi);
                    index = _n[index].parent;
                }
            }

            // A node whose children's heights differ by more than one: the taller child takes its place, and the
            // taller of that child's own children stays with it. Returns the node now standing there.
            private int Balance(int a)
            {
                if (_n[a].left == -1 || _n[a].height < 2) return a;
                int b = _n[a].left, c = _n[a].right;
                int lean = _n[c].height - _n[b].height;
                if (lean > 1) return Raise(a, c, b, false);
                if (lean < -1) return Raise(a, b, c, true);
                return a;
            }

            // up: the child of a that takes a's place; other: a's other child; upIsLeft: whether up was a's left child.
            private int Raise(int a, int up, int other, bool upIsLeft)
            {
                int f = _n[up].left, g = _n[up].right;
                _n[up].left = a;
                _n[up].parent = _n[a].parent;
                _n[a].parent = up;
                int above = _n[up].parent;
                if (above != -1)
                {
                    if (_n[above].left == a) _n[above].left = up;
                    else _n[above].right = up;
                }
                else
                {
                    _root = up;
                }

                // The taller of up's children stays under up; the other goes under a, where up was.
                int stays = _n[f].height > _n[g].height ? f : g;
                int goes = stays == f ? g : f;
                _n[up].right = stays;
                if (upIsLeft) _n[a].left = goes;
                else _n[a].right = goes;
                _n[goes].parent = a;
                _n[a].lo = math.min(_n[other].lo, _n[goes].lo);
                _n[a].hi = math.max(_n[other].hi, _n[goes].hi);
                _n[up].lo = math.min(_n[a].lo, _n[stays].lo);
                _n[up].hi = math.max(_n[a].hi, _n[stays].hi);
                _n[a].height = 1 + math.max(_n[other].height, _n[goes].height);
                _n[up].height = 1 + math.max(_n[a].height, _n[stays].height);
                return up;
            }

            /// <summary>For tests: null when every link, height and box of the tree is as it must be, or what is not.</summary>
            internal string Check()
            {
                if (_root == -1) return Count == 0 ? null : "no root with " + Count + " items";
                if (_n[_root].parent != -1) return "the root has a parent";
                int leaves = 0;
                string wrong = Check(_root, ref leaves);
                if (wrong != null) return wrong;
                return leaves == Count ? null : "leaves " + leaves + ", items " + Count;
            }

            private string Check(int i, ref int leaves)
            {
                if (_n[i].left == -1)
                {
                    leaves++;
                    if (_n[i].right != -1 || _n[i].height != 0 || _n[i].item < 0) return "leaf " + i + " is not a leaf";
                    return null;
                }

                int left = _n[i].left, right = _n[i].right;
                if (_n[left].parent != i || _n[right].parent != i) return "node " + i + ": a child does not name it";
                if (_n[i].height != 1 + math.max(_n[left].height, _n[right].height)) return "node " + i + ": height";
                if (math.any(_n[i].lo != math.min(_n[left].lo, _n[right].lo)) || math.any(_n[i].hi != math.max(_n[left].hi, _n[right].hi))) return "node " + i + ": box";
                return Check(left, ref leaves) ?? Check(right, ref leaves);
            }

            internal int HeightForTest => _root == -1 ? 0 : _n[_root].height;
        }
    }
}
