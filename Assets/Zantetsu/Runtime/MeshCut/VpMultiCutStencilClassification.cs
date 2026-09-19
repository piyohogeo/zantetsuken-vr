using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>What the classification decided for one render fragment of the snapshot.</summary>
    public readonly struct VpMultiCutStencilRenderFragment
    {
        internal VpMultiCutStencilRenderFragment(
            bool isTarget, int conditionCount, int nonEmptyCaps, int visibleCaps, bool capsComplete, int group, int colour,
            bool volumeIssued)
        {
            this.isTarget = isTarget;
            this.conditionCount = conditionCount;
            this.nonEmptyCaps = nonEmptyCaps;
            this.visibleCaps = visibleCaps;
            this.capsComplete = capsComplete;
            this.group = group;
            this.colour = colour;
            this.volumeIssued = volumeIssued;
        }

        /// <summary>Whether it is a stencil target at all: it has a cut condition. A body under none opens nothing.</summary>
        public readonly bool isTarget;

        /// <summary>How many conditions its compatibility target was given: every one the snapshot holds for it.</summary>
        public readonly int conditionCount;

        /// <summary>Its caps with at least one vertex.</summary>
        public readonly int nonEmptyCaps;

        /// <summary>Its caps the two eyes keep.</summary>
        public readonly int visibleCaps;

        /// <summary>
        /// Whether every non-empty cap was kept: then the caps given to the projection test are all its caps bound. An
        /// empty cap is not an omission; a non-empty one left out by visibility is. False with no non-empty cap.
        /// </summary>
        public readonly bool capsComplete;

        /// <summary>Its compatibility group, or -1 when it is not a target.</summary>
        public readonly int group;

        /// <summary>Its stencil colour, or -1 when its group was left out (no cap seen) or it is not a target.</summary>
        public readonly int colour;

        /// <summary>Whether its volume is to be issued: its group has a cap seen. Once, however many caps it has.</summary>
        public readonly bool volumeIssued;
    }

    /// <summary>What the classification decided for one cap of the snapshot.</summary>
    public readonly struct VpMultiCutStencilCap
    {
        internal VpMultiCutStencilCap(bool empty, bool visible, bool issued)
        {
            this.empty = empty;
            this.visible = visible;
            this.issued = issued;
        }

        /// <summary>The snapshot built it with no vertex: a normal result with nothing to draw, never visible.</summary>
        public readonly bool empty;

        /// <summary>Kept by the both-eye visibility test.</summary>
        public readonly bool visible;

        /// <summary>To be drawn: visible, in a group that is kept.</summary>
        public readonly bool issued;
    }

    /// <summary>
    /// The existing stencil classifiers run over a <see cref="VpMultiCutSnapshot"/> for one pair of eyes, on the CPU:
    /// visibility per cap (<see cref="VpCapVisibility"/>), compatibility and colours per render fragment
    /// (<see cref="VpCapCompatibility"/>, <see cref="VpCapProjectionConflict"/>, <see cref="VpStencilColors"/>), and
    /// which volumes and caps would be issued (DESIGN 5.6, D-181). Nothing is issued, uploaded or drawn here.
    /// <para>
    /// **Units.** A cap is judged by its own outward normal and its world vertices with the separation already in them.
    /// A render fragment with at least one condition is one target: every condition the snapshot holds for it and its
    /// offset go to compatibility unchanged -- a cap not seen drops nothing -- and its seen caps go to the projection
    /// test, with <see cref="VpMultiCutStencilRenderFragment.capsComplete"/> saying whether any non-empty one was left out.
    /// A group with any cap seen keeps every render fragment's volume, once each; only the caps seen are drawn. A render
    /// fragment with no condition is no target: it opens nothing. Caps of Ignored boundaries do not exist in the
    /// snapshot and are not made here.
    /// </para>
    /// <para>
    /// **Room and lifetime.** All the working room is made once, from the snapshot capacities it is sized for. The
    /// snapshot's vertices and conditions are read through looks at its own arrays, held only while a classification
    /// runs and cleared when it ends; the results kept are counts, groups, colours and flags. Classifying again for
    /// other eyes reads the same snapshot, which is never rebuilt or changed. A failure -- a snapshot that is not built
    /// or does not fit -- leaves nothing prepared.
    /// </para>
    /// </summary>
    public sealed class VpMultiCutStencilClassification
    {
        private readonly VpMultiCutCapacities _capacities;

        // Results.
        private readonly VpMultiCutStencilRenderFragment[] _renderFragments;
        private readonly VpMultiCutStencilCap[] _caps;

        // Working room: per render fragment, and per cap.
        private readonly int[] _targetOfRenderFragment;
        private readonly int[] _renderFragmentOfTarget;
        private readonly VpCapProjectionTarget[] _targets;
        private readonly VpCapCompatibilityTarget[] _conditions;
        private readonly int[] _groupOfTarget;
        private readonly bool[] _groupKept;
        private readonly int[] _keptGroup;
        private readonly VpCapProjectionTarget[] _keptTargets;
        private readonly int[] _keptGroupOf;
        private readonly int[] _colourOfGroup;
        private readonly VpArrayRange<Vector3>[] _visibleCaps;
        private readonly CountedList<VpCapCompatibilityTarget> _conditionList;
        private readonly CountedList<VpCapProjectionTarget> _keptTargetList;
        private readonly CountedList<int> _keptGroupOfList;
        private int _targetsUsed;
        private int _viewsUsed;

        private int _renderFragmentCount;
        private int _capCount;

        /// <summary>Makes the room for snapshots made with <paramref name="capacities"/>.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive.</exception>
        public VpMultiCutStencilClassification(VpMultiCutCapacities capacities)
        {
            if (capacities.renderFragments <= 0 || capacities.caps < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities));
            }

            int renderFragments = capacities.renderFragments;
            _capacities = capacities;
            _renderFragments = new VpMultiCutStencilRenderFragment[renderFragments];
            _caps = new VpMultiCutStencilCap[capacities.caps];
            _targetOfRenderFragment = new int[renderFragments];
            _renderFragmentOfTarget = new int[renderFragments];
            _targets = new VpCapProjectionTarget[renderFragments];
            _conditions = new VpCapCompatibilityTarget[renderFragments];
            _groupOfTarget = new int[renderFragments];
            _groupKept = new bool[renderFragments];
            _keptGroup = new int[renderFragments];
            _keptTargets = new VpCapProjectionTarget[renderFragments];
            _keptGroupOf = new int[renderFragments];
            _colourOfGroup = new int[renderFragments];
            _visibleCaps = new VpArrayRange<Vector3>[capacities.caps];
            _conditionList = new CountedList<VpCapCompatibilityTarget>(_conditions);
            _keptTargetList = new CountedList<VpCapProjectionTarget>(_keptTargets);
            _keptGroupOfList = new CountedList<int>(_keptGroupOf);
        }

        public VpMultiCutCapacities Capacities => _capacities;

        /// <summary>Whether the last classification succeeded; nothing is readable otherwise.</summary>
        public bool IsPrepared { get; private set; }

        public int RenderFragmentCount => IsPrepared ? _renderFragmentCount : 0;
        public int CapCount => IsPrepared ? _capCount : 0;

        /// <summary>Render fragments that are stencil targets.</summary>
        public int TargetCount { get; private set; }

        public int GroupCount { get; private set; }

        /// <summary>Groups with no cap seen: neither volumes nor caps.</summary>
        public int CulledGroupCount { get; private set; }

        /// <summary>Colours in use: the ordinary ones any group took, and the last one if any group is in it.</summary>
        public int ColourCount { get; private set; }

        public int OrdinaryColourCount { get; private set; }

        public int GroupsInLastColour { get; private set; }

        /// <summary>Render fragments whose volume would be issued, once each.</summary>
        public int VolumeTargetCount { get; private set; }

        /// <summary>Caps that would be drawn.</summary>
        public int IssuedCapCount { get; private set; }

        public bool TryGetRenderFragment(int index, out VpMultiCutStencilRenderFragment result)
        {
            bool ok = IsPrepared && index >= 0 && index < _renderFragmentCount;
            result = ok ? _renderFragments[index] : default;
            return ok;
        }

        public bool TryGetCap(int index, out VpMultiCutStencilCap result)
        {
            bool ok = IsPrepared && index >= 0 && index < _capCount;
            result = ok ? _caps[index] : default;
            return ok;
        }

        /// <summary>
        /// Called with the count of targets written so far, each time one is written. For tests only, to throw part of the
        /// way through and see that nothing is left held; null otherwise, and nothing recovers from what it throws.
        /// </summary>
        internal Action<int> AfterTargetWritten { get; set; }

        /// <summary>How many looks at a snapshot the working room still holds: zero outside a classification. For tests.</summary>
        internal int HeldViews
        {
            get
            {
                int held = 0;
                for (int i = 0; i < _targets.Length; i++)
                {
                    held += _targets[i].conditions.constraints.IsNull && _targets[i].visibleCaps.IsNull ? 0 : 1;
                    held += _keptTargets[i].conditions.constraints.IsNull && _keptTargets[i].visibleCaps.IsNull ? 0 : 1;
                    held += _conditions[i].constraints.IsNull ? 0 : 1;
                }

                for (int i = 0; i < _visibleCaps.Length; i++)
                {
                    held += _visibleCaps[i].IsNull ? 0 : 1;
                }

                return held;
            }
        }

        /// <summary>
        /// Classifies <paramref name="snapshot"/> for these eyes. False, leaving nothing prepared, when the snapshot is
        /// not built or holds more render fragments or caps than this was made for. The snapshot is only read.
        /// </summary>
        /// <exception cref="ArgumentNullException">The snapshot is null.</exception>
        /// <exception cref="ArgumentException">The settings are not valid stencil settings.</exception>
        public bool TryClassify(VpMultiCutSnapshot snapshot, in VpCapEye left, in VpCapEye right, in VpStencilSettings settings)
        {
            // Unprepared from the first moment of any attempt: an earlier result is never readable after a failed one,
            // an argument refused included.
            Reset();
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!settings.IsValid(VpStencilCapMaterials.MaxColors, out string reason))
            {
                throw new ArgumentException(reason, nameof(settings));
            }

            if (!snapshot.IsBuilt
                || snapshot.RenderFragmentCount > _renderFragments.Length
                || snapshot.CapCount > _caps.Length)
            {
                return false;
            }

            try
            {
                Classify(snapshot, left, right, settings);
                IsPrepared = true;
                return true;
            }
            finally
            {
                ClearViews();
            }
        }

        private void Classify(VpMultiCutSnapshot snapshot, in VpCapEye left, in VpCapEye right, in VpStencilSettings settings)
        {
            int renderFragments = snapshot.RenderFragmentCount;
            int caps = snapshot.CapCount;

            // 1. Each cap: empty, or kept by both eyes' frustum and facing.
            for (int c = 0; c < caps; c++)
            {
                snapshot.TryGetCap(c, out VpMultiCutCap cap);
                bool empty = cap.vertexCount == 0;
                bool visible = !empty && VpCapVisibility.Classify(
                    snapshot.CapPolygon(c).AsSpan(), cap.outwardNormal, left, right, settings.facingEpsilon).Keep;
                _caps[c] = new VpMultiCutStencilCap(empty, visible, false);
            }

            // 2. Each render fragment with a condition is one target: every condition, the offset, its seen caps.
            int targets = 0;
            int views = 0;
            for (int r = 0; r < renderFragments; r++)
            {
                snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                int nonEmpty = 0;
                int seen = 0;
                int viewStart = views;
                for (int c = 0; c < rf.capCount; c++)
                {
                    VpMultiCutStencilCap cap = _caps[rf.capStart + c];
                    nonEmpty += cap.empty ? 0 : 1;
                    if (cap.visible)
                    {
                        // Counted for release before it is written, so an exception past here still lets it go.
                        _viewsUsed = views + 1;
                        _visibleCaps[views++] = snapshot.CapPolygon(rf.capStart + c);
                        seen++;
                    }
                }

                bool complete = nonEmpty > 0 && seen == nonEmpty;
                bool isTarget = rf.conditionCount > 0;
                _targetOfRenderFragment[r] = isTarget ? targets : -1;
                if (isTarget)
                {
                    _targetsUsed = targets + 1;
                    var conditions = new VpCapCompatibilityTarget(snapshot.Conditions(rf), rf.offset);
                    _conditions[targets] = conditions;
                    // Each render fragment is judged against its own registration's box and placement.
                    _targets[targets] = new VpCapProjectionTarget(
                        conditions, rf.localBounds, rf.geometryLocalToWorld,
                        new VpArrayRange<VpArrayRange<Vector3>>(_visibleCaps, viewStart, seen), complete);
                    _renderFragmentOfTarget[targets] = r;
                    targets++;
                    AfterTargetWritten?.Invoke(targets);
                }

                _renderFragments[r] = new VpMultiCutStencilRenderFragment(
                    isTarget, isTarget ? rf.conditionCount : 0, nonEmpty, seen, complete, -1, -1, false);
            }


            // 3. Groups over every condition, seen or not.
            _conditionList.SetCount(targets);
            int groups = targets == 0
                ? 0
                : VpCapCompatibility.Classify(_conditionList, settings.planeEpsilon, settings.offsetEpsilon, _groupOfTarget);

            // 4. A group with any cap seen is kept: every member's volume, only the seen caps.
            Array.Clear(_groupKept, 0, groups);
            for (int t = 0; t < targets; t++)
            {
                _groupKept[_groupOfTarget[t]] |= _renderFragments[_renderFragmentOfTarget[t]].visibleCaps > 0;
            }

            int kept = 0;
            for (int g = 0; g < groups; g++)
            {
                _keptGroup[g] = _groupKept[g] ? kept++ : -1;
            }

            int keptTargets = 0;
            for (int t = 0; t < targets; t++)
            {
                int keptIndex = _keptGroup[_groupOfTarget[t]];
                if (keptIndex >= 0)
                {
                    _keptTargets[keptTargets] = _targets[t];
                    _keptGroupOf[keptTargets] = keptIndex;
                    keptTargets++;
                }
            }

            // 5. Colours within the limit, first fit in input order, the rest in the last colour.
            int max = settings.maxStencilColors;
            if (kept > 0)
            {
                _keptTargetList.SetCount(keptTargets);
                _keptGroupOfList.SetCount(keptTargets);
                VpStencilColors.Assign(
                    _keptTargetList, _keptGroupOfList, kept, left, right, settings.ndcMargin, settings.planeEpsilon,
                    settings.offsetEpsilon, max, _colourOfGroup);
            }

            // 6. The results, as numbers and flags only.
            int volumes = 0;
            int issuedCaps = 0;
            for (int r = 0; r < renderFragments; r++)
            {
                VpMultiCutStencilRenderFragment result = _renderFragments[r];
                int t = _targetOfRenderFragment[r];
                if (t < 0)
                {
                    continue;
                }

                int group = _groupOfTarget[t];
                int keptIndex = _keptGroup[group];
                bool volume = keptIndex >= 0;
                int colour = volume ? _colourOfGroup[keptIndex] : -1;
                volumes += volume ? 1 : 0;
                _renderFragments[r] = new VpMultiCutStencilRenderFragment(
                    true, result.conditionCount, result.nonEmptyCaps, result.visibleCaps, result.capsComplete, group, colour,
                    volume);

                snapshot.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf);
                for (int c = 0; c < rf.capCount; c++)
                {
                    VpMultiCutStencilCap cap = _caps[rf.capStart + c];
                    bool issued = volume && cap.visible;
                    issuedCaps += issued ? 1 : 0;
                    _caps[rf.capStart + c] = new VpMultiCutStencilCap(cap.empty, cap.visible, issued);
                }
            }

            int inLast = 0;
            int ordinaryUsed = 0;
            for (int g = 0; g < kept; g++)
            {
                inLast += _colourOfGroup[g] == max - 1 ? 1 : 0;
            }

            // Ordinary colours in use: those below the last, counted once each.
            for (int colour = 0; colour < max - 1; colour++)
            {
                for (int g = 0; g < kept; g++)
                {
                    if (_colourOfGroup[g] == colour)
                    {
                        ordinaryUsed++;
                        break;
                    }
                }
            }

            _renderFragmentCount = renderFragments;
            _capCount = caps;
            TargetCount = targets;
            GroupCount = groups;
            CulledGroupCount = groups - kept;
            ColourCount = ordinaryUsed + (inLast > 0 ? 1 : 0);
            OrdinaryColourCount = ordinaryUsed;
            GroupsInLastColour = inLast;
            VolumeTargetCount = volumes;
            IssuedCapCount = issuedCaps;
        }

        private void Reset()
        {
            IsPrepared = false;
            _renderFragmentCount = 0;
            _capCount = 0;
            TargetCount = 0;
            GroupCount = 0;
            CulledGroupCount = 0;
            ColourCount = 0;
            OrdinaryColourCount = 0;
            GroupsInLastColour = 0;
            VolumeTargetCount = 0;
            IssuedCapCount = 0;
        }

        /// <summary>Lets go of every look at the snapshot, so none outlives the classification.</summary>
        private void ClearViews()
        {
            Array.Clear(_targets, 0, _targetsUsed);
            Array.Clear(_keptTargets, 0, _targetsUsed);
            Array.Clear(_conditions, 0, _targetsUsed);
            Array.Clear(_visibleCaps, 0, _viewsUsed);
            _conditionList.SetCount(0);
            _keptTargetList.SetCount(0);
            _keptGroupOfList.SetCount(0);
            _targetsUsed = 0;
            _viewsUsed = 0;
            if (!IsPrepared)
            {
                Reset();
            }
        }

        /// <summary>A read-only look at the first items of an array this owns, for the classifiers that take lists.</summary>
        private sealed class CountedList<T> : IReadOnlyList<T>
        {
            private readonly T[] _items;
            private int _count;

            public CountedList(T[] items)
            {
                _items = items;
            }

            public int Count => _count;

            public T this[int index]
            {
                get
                {
                    if ((uint)index >= (uint)_count)
                    {
                        throw new ArgumentOutOfRangeException(nameof(index));
                    }

                    return _items[index];
                }
            }

            public void SetCount(int count)
            {
                _count = count;
            }

            public IEnumerator<T> GetEnumerator()
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return _items[i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
