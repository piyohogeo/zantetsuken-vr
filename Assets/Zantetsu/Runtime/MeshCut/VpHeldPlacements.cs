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
    /// <item>**selective** -- the structure kept: the ordinary targets and the held ones a proximity box finds are
    /// asked, the other held ones are not (<see cref="omitted"/>);</item>
    /// <item>**structure** -- the structure was gone through again: what is held was mapped to the new numbers, and
    /// the pass asks its targets in the same way;</item>
    /// <item>**unvouched** -- no step count vouched for or no reference points: every render fragment asked, nothing
    /// held;</item>
    /// <item>**other** -- a test's or a diagnosis's pass that asks everything.</item>
    /// </list>
    /// So, a selective or structure pass that ran to its end: render fragments = omitted + queried (ordinary + near +
    /// selected + notified); the others: = queried (unvouched / other). A collection that let the adopted snapshot
    /// stand (D-204) runs no pass and is counted nowhere here.
    /// </summary>
    public struct VpHeldPlacementTotals
    {
        public long passesSelective, passesStructure, passesUnvouched, passesOther;

        // Held and outside every proximity box -- not asked; asked because not held (moving, new, or not yet seen
        // standing over two step results); asked because held and inside a proximity box; asked because it has selected
        // boundaries (clipped: never held); asked, though held, because a placement input was said to have changed
        // outside a step -- of its own family, or with no target (every held one is asked once then).
        public long omitted, queriedOrdinary, queriedNear, queriedSelected, queriedNotified;

        // The passes that ask everything, by why.
        public long queriedUnvouched, queriedOther;

        // Families told of a change outside a step (one a telling: a family told twice counts twice).
        public long toldFamilies;

        // Ordinary targets that became held; held ones asked and found moved, made ordinary again; passes in which
        // every held one was asked for a change outside a step; times everything was forgotten (no step count vouched
        // for, a source or the margin changed, a structure these were not mapped for).
        public long promoted, demoted, invalidations, structureResets;

        // Passes in which a render fragment made conditions or caps, so that every later one's ranges begin elsewhere
        // and each is built again as before; and the held, far ones among those -- built again where they are held,
        // without being asked (they are in omitted).
        public long shiftedPasses, shiftedRebuilt;

        // The tree: searches (one a proximity box a pass) and their time whole, with the marking of the ordinary
        // targets; and the time of the insertions and removals (inside the Place pass's own time).
        public long searches;
        public double searchSeconds, updateSeconds;

        // A structure gone through again: mappings of the parts to the new numbers; the render fragments whose state
        // was carried (their part was the one mapped before) and those that started ordinary (a part new or changed);
        // the held ones of parts that were gone, taken out of the tree; and the mappings' time (before the pass; no
        // placement is asked in it).
        public long remaps, carried, fresh, droppedHeld;
        public double remapSeconds;

        // The view (D-210). Held ones a proximity box found, before any view was asked (each once a pass, however
        // many boxes found it); of those, the ones left unasked because they met no view of a camera whose box found
        // them (they are in omitted too); the tests of a box against a camera's view; and the reference points of the
        // passes by whether a view was known for them -- one with none asks by its proximity box alone, as before.
        //
        // viewSeconds is the time of **choosing among the candidates by the view**, apart from the tree search's: for
        // every leaf a search found, reading its item, marking it as a candidate once (a bit set), passing over one
        // already taken (a bit read), the planes' arithmetic against its kept box where the point has a view, taking it
        // (a bit set) and the counting. It is not the planes' arithmetic alone.
        //
        // Not in viewSeconds but done for these counts, in searchSeconds: a set of bits, one a render fragment of the
        // display, is cleared before the searches of a pass, and gone over once more (a word at a time, 64 render
        // fragments a word) when some candidate was left unasked, to count them. That is a pass over bits -- over
        // every registered render fragment's bit -- and reads no Transform, no matrix and no bounds.
        public long nearCandidates, omittedOutOfView, viewTests;
        public double viewSeconds;
        public long pointsWithView, pointsWithoutView;

        // Of the held ones taken as near: those taken through a reference point that had no view, by its proximity
        // box alone (a camera that is not rendered, or a point that is no camera's). The others were taken through a
        // point's box and view. One taken by both kinds of point is counted for the one that took it first.
        public long pickedWithoutView;

        // The tree's boxes taken in and given up, one at a time (a mapping that carries a box does neither; a
        // forgetting empties the tree at once and is counted in structureResets, not here).
        public long treeInserts, treeRemovals;

        // Not sums: as they stood after the last collection added.
        public int heldAtEnd, ordinaryAtEnd, pointsAtEnd;

        public void Add(in VpHeldPlacementTotals a)
        {
            passesSelective += a.passesSelective; passesStructure += a.passesStructure; passesUnvouched += a.passesUnvouched; passesOther += a.passesOther;
            omitted += a.omitted; queriedOrdinary += a.queriedOrdinary; queriedNear += a.queriedNear; queriedSelected += a.queriedSelected; queriedNotified += a.queriedNotified;
            queriedUnvouched += a.queriedUnvouched; queriedOther += a.queriedOther; toldFamilies += a.toldFamilies;
            promoted += a.promoted; demoted += a.demoted; invalidations += a.invalidations; structureResets += a.structureResets;
            shiftedPasses += a.shiftedPasses; shiftedRebuilt += a.shiftedRebuilt;
            searches += a.searches; searchSeconds += a.searchSeconds; updateSeconds += a.updateSeconds;
            remaps += a.remaps; carried += a.carried; fresh += a.fresh; droppedHeld += a.droppedHeld; remapSeconds += a.remapSeconds;
            nearCandidates += a.nearCandidates; omittedOutOfView += a.omittedOutOfView; viewTests += a.viewTests; viewSeconds += a.viewSeconds;
            pointsWithView += a.pointsWithView; pointsWithoutView += a.pointsWithoutView; treeInserts += a.treeInserts; treeRemovals += a.treeRemovals;
            pickedWithoutView += a.pickedWithoutView;
            heldAtEnd = a.heldAtEnd; ordinaryAtEnd = a.ordinaryAtEnd; pointsAtEnd = a.pointsAtEnd;
        }

        public void Subtract(in VpHeldPlacementTotals a)
        {
            passesSelective -= a.passesSelective; passesStructure -= a.passesStructure; passesUnvouched -= a.passesUnvouched; passesOther -= a.passesOther;
            omitted -= a.omitted; queriedOrdinary -= a.queriedOrdinary; queriedNear -= a.queriedNear; queriedSelected -= a.queriedSelected; queriedNotified -= a.queriedNotified;
            queriedUnvouched -= a.queriedUnvouched; queriedOther -= a.queriedOther; toldFamilies -= a.toldFamilies;
            promoted -= a.promoted; demoted -= a.demoted; invalidations -= a.invalidations; structureResets -= a.structureResets;
            shiftedPasses -= a.shiftedPasses; shiftedRebuilt -= a.shiftedRebuilt;
            searches -= a.searches; searchSeconds -= a.searchSeconds; updateSeconds -= a.updateSeconds;
            remaps -= a.remaps; carried -= a.carried; fresh -= a.fresh; droppedHeld -= a.droppedHeld; remapSeconds -= a.remapSeconds;
            nearCandidates -= a.nearCandidates; omittedOutOfView -= a.omittedOutOfView; viewTests -= a.viewTests; viewSeconds -= a.viewSeconds;
            pointsWithView -= a.pointsWithView; pointsWithoutView -= a.pointsWithoutView; treeInserts -= a.treeInserts; treeRemovals -= a.treeRemovals;
            pickedWithoutView -= a.pickedWithoutView;
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
    /// **Near and in view (D-210).** A reference point may come with its camera's view: the planes of the eyes that
    /// camera was last rendered with (one eye, or two under XR), as the host noted them at the end of that rendering.
    /// A held one a proximity box finds is then asked only if its kept box -- the box in the tree, margin and all --
    /// also meets that view: either eye's (a target only one eye sees is asked). With several cameras a held one is
    /// asked when, for at least one of them, it is in that camera's proximity box **and** that camera's view. A
    /// reference point with no view known -- none noted yet, or one older than the frame before -- asks by its
    /// proximity box alone, as before the view was used; it never asks everything. The search goes down the tree by
    /// the proximity box only; the view is asked of what the search found, and nothing of the tree is touched for it:
    /// a view that turns inserts and removes nothing. No Transform is read and no box is made again for the choosing.
    /// </para>
    /// <para>
    /// **What is not asked is drawn where it is held.** No shadow volume and no rendering path comes into it. A held
    /// target that moves while far from every reference point, or near one but out of its view, is drawn -- and casts
    /// its shadow -- where it was held, until a pass asks it: accepted (TL, 2026-10-07).
    /// </para>
    /// <para>
    /// **When one that came into view is asked -- no frame is promised.** A placement pass reads the view the camera
    /// was rendered with in the frame before (this frame's is not made yet when it runs). And a pass runs only when a
    /// collection asks placements at all: with no new step result, nothing told and no structure change, a collection
    /// lets the adopted snapshot stand (D-204) whatever the view did -- no search, no view asked. So a held target
    /// that moved out of view and is then looked at is asked in the **next pass that runs** in which its kept box
    /// meets a proximity box and that camera's view as noted the frame before -- the frame after the turn if a step's
    /// result is new then, later if steps are passed over, as long as they are. Nothing is done to make it sooner: the
    /// reuse is not given up for a view that moved, and nothing asks every one. The margin of the kept box may let a
    /// target be picked up early, when the view comes within it; it is no promise either -- a quick turn passes it.
    /// </para>
    /// <para>
    /// **What is held goes with a registration's structure part.** The arrays here go by a render fragment's number in
    /// the structure being built, and a structure gone through again numbers them anew. A snapshot's structure is made
    /// of parts, one a registration: the pooled part and which taking of it. A registration whose family did not change
    /// is given the adopted snapshot's own part, so the same part at the next mapping says that its render fragments,
    /// what each stands as and its geometry are the same -- nothing compared. Such a part's render fragments carry
    /// their state to their new numbers: held or ordinary, the step remembered, the tree's leaf as it is (only the
    /// number the leaf names is rewritten). A part that is not the one mapped before -- its family changed (a cut, a
    /// commit, a split, a membership change), or the registration is new -- starts ordinary. What was held of parts
    /// that are gone (retired, or replaced) is taken out of the tree. No placement is asked for the mapping.
    /// </para>
    /// <para>
    /// **A change outside a step** (an owner put somewhere, brought, handed over or taken away) is told with the
    /// fragment it concerns wherever the teller has one (D-207). The display gives that fragment's family: the held
    /// render fragments of that family's registrations are asked in the next pass that runs to its end, and no other
    /// family's. A change told with no target has every held one asked. Either way one that stands where it is held
    /// stays held, its leaf untouched; one that moved is ordinary. What was told is kept until a pass has run to its
    /// end, so a build that fails part way and is made again asks them again. A pass with no step count vouched for
    /// forgets everything.
    /// </para>
    /// <para>
    /// **A collection that is not adopted** leaves nothing wrong here. Becoming held says that a query's answer was
    /// the adopted placement at a later step, which is so whatever becomes of that collection; becoming ordinary only
    /// asks more; the step remembered is only ever made later than the truth. And the mapping follows the structure
    /// being built, not the adopted one: a structure built and not adopted is gone through again by the next
    /// collection, which maps again from the parts it then has -- a part taken for the build that failed is not the
    /// part of the next build (a new taking), so nothing is carried to it; a part of the adopted snapshot that both
    /// builds took over is carried through both.
    /// </para>
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
        private long[] _readStep = Array.Empty<long>(), _readStepNext = Array.Empty<long>();   // the step whose result the adopted placement was first seen at
        private int[] _leaf = Array.Empty<int>(), _leafNext = Array.Empty<int>();              // a held one's leaf in the tree; -1 while ordinary
        private int[] _ordinaryAt = Array.Empty<int>();   // an ordinary one's place in _ordinary; -1 while held
        private int[] _ordinary = Array.Empty<int>();
        private int _ordinaryCount, _count, _nextCount;
        private ulong[] _picked = Array.Empty<ulong>();   // this pass's targets: the ordinary and the held found near
        private int _pickedWords;
        private bool _valid, _outsideKnown, _allHeldAsked;
        private long _stampLedger = -1, _stampInputs = -1, _outside;

        // Which mapping the parts' marks are of: a part marked with another number was not in the last mapping.
        private long _epoch = 1;
        private long _remapBegan;

        // The families' registrations as the last mapping had them: each family's parts in a chain (D-207).
        private readonly Dictionary<LogicalFragmentId, int> _familyHead = new Dictionary<LogicalFragmentId, int>();
        private int[] _partStart = Array.Empty<int>(), _partRenders = Array.Empty<int>(), _partNext = Array.Empty<int>();
        private int _parts;
        private bool _familiesKnown;

        // Told since the last pass that ran to its end: the families whose placement inputs changed outside a step, and
        // whether a change was told that names none. And, of this pass, the held ones asked for their family's telling.
        private readonly List<LogicalFragmentId> _told = new List<LogicalFragmentId>(8);
        private bool _toldAll, _toldAny;
        private ulong[] _toldBits = Array.Empty<ulong>();

        // This pass, as the display said it before the snapshot was built.
        private bool _active;
        private long _step, _passOutside;
        private readonly List<Vector3> _points = new List<Vector3>(4);

        // The view of each reference point's camera (D-210): how many eyes (0: no view known -- its proximity box alone
        // decides), and six planes an eye, normals inward (inside: n.p + d >= 0).
        internal const int EyePlanes = 6, EyeCapacity = 2, ViewPlanes = EyePlanes * EyeCapacity;
        private int[] _viewEyes = new int[4];
        private Vector4[] _viewPlanes = new Vector4[4 * ViewPlanes];

        // The held ones a proximity box found in this pass, each once; and the leaves one search found.
        private ulong[] _nearBits = Array.Empty<ulong>();
        private int[] _found = new int[64];

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
            if (_viewEyes.Length < _points.Count)
            {
                Array.Resize(ref _viewEyes, _points.Count);
                Array.Resize(ref _viewPlanes, _points.Count * ViewPlanes);
            }

            Array.Clear(_viewEyes, 0, _points.Count);   // no view known, until SetView says one
        }

        /// <summary>
        /// The view of reference point <paramref name="point"/>'s camera, after <see cref="SetPass"/>:
        /// <paramref name="eyes"/> eyes (1 or 2) of six planes each from <paramref name="planes"/>, normals inward. A
        /// point not told here, or told with planes that are not finite or have no normal, has no view: its proximity
        /// box alone decides.
        /// </summary>
        public void SetView(int point, int eyes, Vector4[] planes)
        {
            if (point < 0 || point >= _points.Count || planes == null || eyes < 1 || eyes > EyeCapacity || planes.Length < eyes * EyePlanes) return;
            for (int k = 0; k < eyes * EyePlanes; k++)
            {
                Vector4 p = planes[k];
                float normal = p.x * p.x + p.y * p.y + p.z * p.z;
                if (!(normal > 0f) || float.IsInfinity(normal) || float.IsNaN(p.w) || float.IsInfinity(p.w)) return;
                _viewPlanes[point * ViewPlanes + k] = p;
            }

            _viewEyes[point] = eyes;
        }

        /// <summary>A placement input of this family changed outside a step: its held render fragments are asked in the next pass.</summary>
        public void Tell(LogicalFragmentId family) => _told.Add(family);

        /// <summary>A placement input changed outside a step and whose is not known: every held one is asked in the next pass.</summary>
        public void TellAll() => _toldAll = true;

        /// <summary>
        /// The pass ran to its end: what was told is taken -- every target it concerned was asked. (A pass that stops
        /// part way leaves it: the build made again asks them again.)
        /// </summary>
        public void PassEnded()
        {
            _outside = _passOutside;
            _outsideKnown = true;
            _told.Clear();
            _toldAll = false;
        }

        /// <summary>Everything forgotten: the next pass asks everything, and nothing is held until two step results agree again.</summary>
        public void Forget()
        {
            if (_valid) _totals.structureResets++;
            _told.Clear();
            _toldAll = false;
            _familiesKnown = false;
            _valid = false;
            _tree.Clear();
            _ordinaryCount = 0;
            _count = 0;
            _outsideKnown = false;
            _epoch++;   // no part's mark is of this mapping any more
        }

        /// <summary>A pass that asked everything took this many queries: counted by its kind (1 unvouched, 2 other).</summary>
        public void CountConventional(int kind, long queries)
        {
            if (kind == 1) _totals.queriedUnvouched += queries;
            else _totals.queriedOther += queries;
        }

        /// <summary>A pass that a test or a diagnosis makes ask everything.</summary>
        public void BeginOther()
        {
            _totals.passesOther++;
            Forget();
        }

        /// <summary>
        /// A pass through a structure gone through again begins: its <paramref name="count"/> render fragments are
        /// numbered anew. False: no step count is vouched for -- everything is forgotten and the pass asks every one.
        /// True: each part is to be told (<see cref="MapPart"/>), then <see cref="EndStructure"/>.
        /// </summary>
        public bool BeginStructure(int count)
        {
            if (!_active)
            {
                _totals.passesUnvouched++;
                Forget();
                return false;
            }

            _remapBegan = System.Diagnostics.Stopwatch.GetTimestamp();
            _totals.passesStructure++;
            _totals.remaps++;
            Room(count);
            _nextCount = count;
            _familyHead.Clear();
            _parts = 0;
            return true;
        }

        /// <summary>
        /// One registration's part, whose render fragments are numbered from <paramref name="start"/> now. The part
        /// mapped before (the same taking, in the last mapping) carries their state; any other starts them ordinary,
        /// nothing known of their placements' steps.
        /// </summary>
        public void MapPart(VpMultiCutSnapshot.StructurePool.Part part, int start)
        {
            int n = part.rendersCount;
            if (_valid && part.holdEpoch == _epoch && part.holdSerial == part.serial)
            {
                int old = part.holdStart;
                for (int j = 0; j < n; j++)
                {
                    int leaf = _leaf[old + j];
                    _readStepNext[start + j] = _readStep[old + j];
                    _leafNext[start + j] = leaf;
                    if (leaf >= 0)
                    {
                        _tree.SetItem(leaf, start + j);   // the box stands as it is; only what it is called changes
                        _leaf[old + j] = -1;              // carried: not one of the gone
                    }
                }

                _totals.carried += n;
            }
            else
            {
                for (int j = 0; j < n; j++)
                {
                    _readStepNext[start + j] = Unknown;
                    _leafNext[start + j] = -1;
                }

                _totals.fresh += n;
            }

            part.holdEpoch = _epoch + 1;
            part.holdSerial = part.serial;
            part.holdStart = start;

            // The family's registrations, for a change told of the family.
            if (_parts == _partStart.Length)
            {
                int room = Math.Max(16, _parts * 2);
                Array.Resize(ref _partStart, room);
                Array.Resize(ref _partRenders, room);
                Array.Resize(ref _partNext, room);
            }

            _partStart[_parts] = start;
            _partRenders[_parts] = n;
            _partNext[_parts] = _familyHead.TryGetValue(part.family, out int head) ? head : -1;
            _familyHead[part.family] = _parts++;
        }

        /// <summary>
        /// Every part was told. What was held of parts that are gone leaves the tree; the new numbers stand; the pass's
        /// targets are picked.
        /// </summary>
        public void EndStructure(long stampLedger, long stampInputs)
        {
            if (_valid)
            {
                for (int old = 0; old < _count; old++)
                {
                    if (_leaf[old] >= 0)
                    {
                        _tree.Remove(_leaf[old]);
                        _totals.droppedHeld++;
                        _totals.treeRemovals++;
                    }
                }
            }

            (_readStep, _readStepNext) = (_readStepNext, _readStep);
            (_leaf, _leafNext) = (_leafNext, _leaf);
            _count = _nextCount;
            _ordinaryCount = 0;
            for (int r = 0; r < _count; r++)
            {
                if (_leaf[r] >= 0)
                {
                    _ordinaryAt[r] = -1;
                }
                else
                {
                    _ordinaryAt[r] = _ordinaryCount;
                    _ordinary[_ordinaryCount++] = r;
                }
            }

            _epoch++;
            _valid = true;
            _familiesKnown = true;
            _stampLedger = stampLedger;
            _stampInputs = stampInputs;
            _totals.remapSeconds += SecondsSince(_remapBegan);
            Pick();
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
                // A structure these were not mapped for (they are mapped in the pass that goes through the structure):
                // nothing is known of it.
                Forget();
                Room(count);
                _count = count;
                _ordinaryCount = count;
                for (int r = 0; r < count; r++)
                {
                    _readStep[r] = Unknown;
                    _leaf[r] = -1;
                    _ordinaryAt[r] = r;
                    _ordinary[r] = r;
                }

                _valid = true;
                _stampLedger = stampLedger;
                _stampInputs = stampInputs;
            }

            _totals.passesSelective++;
            Pick();
            return true;
        }

        // This pass's targets: the ordinary ones, the held ones a proximity box meets and the held ones of the families
        // told of a change outside a step -- or every one, when such a change was told with no target (or of a family
        // while the families' registrations are not known).
        private void Pick()
        {
            bool all = _toldAll || (_outsideKnown && _outside != _passOutside) || (_told.Count > 0 && !_familiesKnown);
            _allHeldAsked = all && _count != _ordinaryCount;
            if (_allHeldAsked) _totals.invalidations++;
            _toldAny = false;

            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            _pickedWords = (_count + 63) >> 6;
            if (_allHeldAsked)
            {
                for (int w = 0; w < _pickedWords; w++) _picked[w] = ulong.MaxValue;
                if ((_count & 63) != 0) _picked[_pickedWords - 1] = (1UL << (_count & 63)) - 1UL;
            }
            else
            {
                Array.Clear(_picked, 0, _pickedWords);
                for (int i = 0; i < _ordinaryCount; i++)
                {
                    int r = _ordinary[i];
                    _picked[r >> 6] |= 1UL << (r & 63);
                }

                // The held ones near a reference point: found in the tree by the proximity box alone. Of what a box
                // found, the ones that also meet its camera's view are this pass's targets (every one, where no view
                // is known for that point). The view is asked of the kept boxes the search found, afterwards: the
                // search's time and the view's are taken apart.
                int near = 0, candidates = 0;
                float reach = Reach;
                if (_nearBits.Length < _pickedWords) _nearBits = new ulong[_picked.Length];
                Array.Clear(_nearBits, 0, _pickedWords);
                double viewSeconds = 0.0;
                for (int i = 0; i < _points.Count; i++)
                {
                    Vector3 p = _points[i];
                    int found = _tree.Find(new float3(p.x - reach, p.y - reach, p.z - reach), new float3(p.x + reach, p.y + reach, p.z + reach), ref _found);
                    _totals.searches++;
                    int eyes = _viewEyes[i];
                    if (eyes > 0) _totals.pointsWithView++; else _totals.pointsWithoutView++;
                    long viewBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                    for (int f = 0; f < found; f++)
                    {
                        int leaf = _found[f];
                        int r = _tree.ItemOf(leaf);
                        ulong bit = 1UL << (r & 63);
                        if ((_nearBits[r >> 6] & bit) == 0)
                        {
                            _nearBits[r >> 6] |= bit;
                            candidates++;
                        }

                        if ((_picked[r >> 6] & bit) != 0) continue;   // another point's box and view already took it
                        if (eyes > 0)
                        {
                            _totals.viewTests++;
                            if (!_tree.MeetsView(leaf, _viewPlanes, i * ViewPlanes, eyes)) continue;
                        }
                        else
                        {
                            _totals.pickedWithoutView++;
                        }

                        _picked[r >> 6] |= bit;
                        near++;
                    }

                    viewSeconds += SecondsSince(viewBegan);
                }

                _totals.nearCandidates += candidates;
                _totals.viewSeconds += viewSeconds;
                began += (long)(viewSeconds * System.Diagnostics.Stopwatch.Frequency);   // the search's time, without the view's

                // The held ones of the families told: through the family's registrations, as the last mapping had them.
                int toldNow = 0;
                if (_told.Count > 0)
                {
                    if (_toldBits.Length < _pickedWords) _toldBits = new ulong[_picked.Length];
                    Array.Clear(_toldBits, 0, _pickedWords);
                    _toldAny = true;
                    _totals.toldFamilies += _told.Count;
                    for (int t = 0; t < _told.Count; t++)
                    {
                        if (!_familyHead.TryGetValue(_told[t], out int part)) continue;   // no registration of it here
                        for (; part >= 0; part = _partNext[part])
                        {
                            for (int r = _partStart[part], end = r + _partRenders[part]; r < end; r++)
                            {
                                if (_leaf[r] < 0) continue;   // ordinary: asked anyway
                                ulong bit = 1UL << (r & 63);
                                _toldBits[r >> 6] |= bit;
                                if ((_picked[r >> 6] & bit) != 0) continue;
                                _picked[r >> 6] |= bit;
                                toldNow++;
                            }
                        }
                    }
                }

                _totals.omitted += _count - _ordinaryCount - near - toldNow;

                // Of the near ones, those left unasked: they met no view (one told of is asked, in view or not).
                if (candidates > near)
                {
                    int unasked = 0;
                    for (int w = 0; w < _pickedWords; w++) unasked += math.countbits(_nearBits[w] & ~_picked[w]);
                    _totals.omittedOutOfView += unasked;
                }
            }

            _totals.searchSeconds += SecondsSince(began);
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

        /// <summary>For tests: whether the last pass's targets held that render fragment.</summary>
        internal bool IsPickedForTest(int renderFragment) => renderFragment >= 0 && renderFragment < _count && IsPicked(renderFragment);

        /// <summary>Every later render fragment's ranges begin elsewhere from here on in this pass.</summary>
        public void NoteShifted() => _totals.shiftedPasses++;

        /// <summary>A held, far render fragment built again where it is held because its ranges begin elsewhere: not asked.</summary>
        public void NoteShiftedRebuilt() => _totals.shiftedRebuilt++;

        /// <summary>
        /// A render fragment was asked and stands, bit for bit, where the adopted snapshot has it (the keep test held).
        /// An ordinary one whose adopted placement is known from another step's result becomes held.
        /// </summary>
        public void Stood(int r, bool wasHeld, in Bounds localBounds, in Matrix4x4 geometryLocalToWorld)
        {
            if (wasHeld)
            {
                CountHeldAsked(r);
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
            _totals.treeInserts++;
            int at = _ordinaryAt[r], last = _ordinary[--_ordinaryCount];
            _ordinary[at] = last;
            _ordinaryAt[last] = at;
            _ordinaryAt[r] = -1;
            _totals.promoted++;
            _totals.updateSeconds += SecondsSince(began);
        }

        /// <summary>
        /// A render fragment was asked and is placed anew: it moved, it is clipped, or its ranges begin elsewhere. A
        /// held one is ordinary again -- unless <paramref name="standsWhereHeld"/>: it was built again only for its
        /// ranges and stands, bit for bit, where it is held. Otherwise its placement is of this step.
        /// </summary>
        public void PlacedAnew(int r, bool wasHeld, bool selected, bool standsWhereHeld)
        {
            if (wasHeld)
            {
                CountHeldAsked(r);
                if (standsWhereHeld) return;
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                _tree.Remove(_leaf[r]);
                _totals.treeRemovals++;
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

        // A held one was asked: for a change told (of its family, or with no target), or because a proximity box met it
        // and, where that box's camera has a view, the view did too.
        private void CountHeldAsked(int r)
        {
            if (_allHeldAsked || (_toldAny && (_toldBits[r >> 6] & (1UL << (r & 63))) != 0)) _totals.queriedNotified++;
            else _totals.queriedNear++;
        }

        // Room for that many render fragments in every array, what they hold kept.
        private void Room(int count)
        {
            if (_readStep.Length >= count) return;
            int room = Math.Max(count, _readStep.Length * 2);
            Array.Resize(ref _readStep, room);
            Array.Resize(ref _readStepNext, room);
            Array.Resize(ref _leaf, room);
            Array.Resize(ref _leafNext, room);
            Array.Resize(ref _ordinaryAt, room);
            Array.Resize(ref _ordinary, room);
            Array.Resize(ref _picked, (room + 63) >> 6);
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

            /// <summary>The item a leaf holds is called otherwise from now on; nothing of the tree moves.</summary>
            public void SetItem(int leaf, int item) => _n[leaf].item = item;

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

            /// <summary>The leaves whose boxes meet the box, into <paramref name="leaves"/> (grown as needed); how many.</summary>
            public int Find(float3 lo, float3 hi, ref int[] leaves)
            {
                if (_root == -1) return 0;
                int count = 0, top = 0;
                _stack[top++] = _root;
                while (top > 0)
                {
                    int i = _stack[--top];
                    if (math.any(_n[i].lo > hi) || math.any(_n[i].hi < lo)) continue;
                    if (_n[i].left == -1)
                    {
                        if (count == leaves.Length) Array.Resize(ref leaves, leaves.Length * 2);
                        leaves[count++] = i;
                        continue;
                    }

                    if (top + 2 > _stack.Length) Array.Resize(ref _stack, _stack.Length * 2);
                    _stack[top++] = _n[i].left;
                    _stack[top++] = _n[i].right;
                }

                return count;
            }

            /// <summary>The item a leaf holds.</summary>
            public int ItemOf(int leaf) => _n[leaf].item;

            /// <summary>
            /// Whether a leaf's box may meet the view of any of <paramref name="eyes"/> eyes, six planes each from
            /// <paramref name="first"/>, normals inward: outside an eye only when the whole box is beyond one of its
            /// planes (the box's corner farthest along the plane's normal is still behind it). It never says no of a
            /// box that touches a view; it may say yes of one that draws nothing.
            /// </summary>
            public bool MeetsView(int leaf, Vector4[] planes, int first, int eyes)
            {
                float3 lo = _n[leaf].lo, hi = _n[leaf].hi;
                for (int e = 0; e < eyes; e++)
                {
                    bool outside = false;
                    for (int k = 0; k < EyePlanes && !outside; k++)
                    {
                        Vector4 p = planes[first + e * EyePlanes + k];
                        float farthest = p.x * (p.x >= 0f ? hi.x : lo.x) + p.y * (p.y >= 0f ? hi.y : lo.y) + p.z * (p.z >= 0f ? hi.z : lo.z) + p.w;
                        outside = farthest < 0f;
                    }

                    if (!outside) return true;
                }

                return false;
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
