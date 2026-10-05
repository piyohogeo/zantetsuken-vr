using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    public sealed partial class VpMultiCutSnapshot
    {
        // Owned by one display. Slots are not returned until both alternating snapshots have let them go.
        internal sealed class StructurePool
        {
            internal readonly struct RenderTemplate
            {
                internal readonly LogicalFragmentId root;
                internal readonly float side;
                internal readonly bool aggregate;
                internal readonly int branch, count;
                internal RenderTemplate(LogicalFragmentId root, float side, bool aggregate, int branch, int count)
                { this.root = root; this.side = side; this.aggregate = aggregate; this.branch = branch; this.count = count; }
            }
            internal sealed class Part
            {
                internal int users, branchesCount, candidatesCount, rendersCount;
                internal LogicalCutLedger ledger;
                internal LogicalFragmentId family;
                internal long revision;
                internal LogicalFragmentId root;
                internal Bounds bounds;
                internal Matrix4x4 lineage;
                internal IReadOnlyCollection<VpClipBoundary> reflected;
                internal float epsilon;
                internal void SetInput(in VpMultiCutRegistration registration)
                {
                    root = registration.root; bounds = registration.localBounds;
                    lineage = registration.lineageToGeometryLocal; reflected = registration.reflected;
                    epsilon = registration.vertexEpsilon;
                }
                internal VpMultiCutBranch[] branches = new VpMultiCutBranch[2];
                internal VpClipCandidate[] candidates = new VpClipCandidate[8];
                internal VpClipSelectionState[] states = new VpClipSelectionState[8];
                internal VpMultiCutCapIdentity[] caps = new VpMultiCutCapIdentity[8];
                internal RenderTemplate[] renders = new RenderTemplate[2];
                internal VpMultiCutStandsAs[] stands = new VpMultiCutStandsAs[2];
                internal VpMultiCutSideIdentity[] sides = new VpMultiCutSideIdentity[2];
                internal bool Matches(LogicalCutLedger owner, LogicalFragmentId familyId, long version, in VpMultiCutRegistration registration)
                {
                    // Product registrations carry immutable VpReflectedSet. An arbitrary mutable collection is
                    // deliberately never cached by identity (the legacy public builder still accepts one).
                    return ledger == owner && family == familyId && revision == version
                        && root == registration.root && registration.reflected is VpReflectedSet
                        && ReferenceEquals(reflected, registration.reflected)
                        && SameBits(lineage, registration.lineageToGeometryLocal)
                        && SameBoundsBits(bounds, registration.localBounds)
                        && epsilon == registration.vertexEpsilon;
                }
                private static bool SameBoundsBits(Bounds a, Bounds b)
                {
                    for (int axis = 0; axis < 3; axis++)
                        if (Unity.Mathematics.math.asint(a.center[axis]) != Unity.Mathematics.math.asint(b.center[axis])
                            || Unity.Mathematics.math.asint(a.extents[axis]) != Unity.Mathematics.math.asint(b.extents[axis])) return false;
                    return true;
                }
                internal void Forget()
                {
                    ledger = null; reflected = null; root = default;
                    Array.Clear(candidates, 0, Math.Min(candidatesCount, candidates.Length));
                    branchesCount = candidatesCount = rendersCount = 0;
                }
            }
            internal sealed class FamilyInput
            {
                internal LogicalFragmentId family;
                internal long revision;
                internal readonly List<VpMultiCutRegistration> registrations = new List<VpMultiCutRegistration>(2);
                internal readonly List<int> indices = new List<int>(2);
            }
            private readonly List<Part> _free = new List<Part>();
            private int _capacity;
            internal readonly Dictionary<LogicalFragmentId, int> families = new Dictionary<LogicalFragmentId, int>();
            internal readonly List<FamilyInput> inputs = new List<FamilyInput>();
            internal int inputCount;
            internal int InUseForTest => _capacity - _free.Count;
            internal StructurePool(int registrations) { Prepare(registrations); }
            internal void Prepare(int registrations)
            {
                families.EnsureCapacity(registrations);
                int needed = checked(registrations * 2);
                if (_free.Capacity < needed) _free.Capacity = needed;
                if (inputs.Capacity < registrations) inputs.Capacity = registrations;
                while (_capacity < needed) { _free.Add(new Part()); _capacity++; }
                while (inputs.Count < registrations) inputs.Add(new FamilyInput());
            }
            internal Part Take()
            {
                if (_free.Count == 0) throw new InvalidOperationException("structural pool has no unused slot");
                int index = _free.Count - 1;
                Part part = _free[index]; _free.RemoveAt(index); part.users = 1; return part;
            }
            internal void Release(Part part)
            {
                if (--part.users != 0) return;
                part.Forget(); _free.Add(part);
            }
            internal void Begin()
            {
                for (int i = 0; i < inputCount; i++) { inputs[i].registrations.Clear(); inputs[i].indices.Clear(); }
                inputCount = 0; families.Clear();
            }
            internal void Add(LogicalFragmentId family, long revision, int index, in VpMultiCutRegistration input)
            {
                if (!families.TryGetValue(family, out int group))
                {
                    group = inputCount++; families.Add(family, group);
                    inputs[group].family = family; inputs[group].revision = revision;
                }
                inputs[group].registrations.Add(input); inputs[group].indices.Add(index);
            }
        }

        private struct PartRange
        {
            internal StructurePool.Part part;
            internal int branch, candidate, render, partFamilyMembers;
        }
        private StructurePool _structurePool;
        private PartRange[] _parts = Array.Empty<PartRange>();
        private int _partCount;
        private bool _composite;
        private LogicalFragmentId _buildingFamily;
        private readonly Dictionary<LogicalFragmentId, int> _partByRoot = new Dictionary<LogicalFragmentId, int>();
        internal long FamiliesRebuilt { get; private set; }
        internal long RegistrationsReused { get; private set; }
        internal object StructureForTest(LogicalFragmentId root) => _partByRoot.TryGetValue(root, out int i) ? _parts[i].part : null;

        private bool TryReadStructureOperation(LogicalCutLedger ledger, int position, out LogicalCutOperation operation)
            => _buildingFamily.IsSet ? ledger.TryGetFamilyOperation(_buildingFamily, position, out operation)
                : ledger.TryGetOperationAtAdmission(position, out operation);

        private void ReleaseStructure()
        {
            for (int i = 0; i < _partCount; i++)
            {
                if (_parts[i].part != null) _structurePool.Release(_parts[i].part);
                _parts[i] = default;
            }
            _partCount = 0; _composite = false; _partByRoot.Clear();
            _selectedCandidates?.Use(_candidates); _selectedStates?.Use(_states);
        }

        private static void Room<T>(ref T[] array, int count)
        {
            if (array.Length < count) Array.Resize(ref array, Math.Max(count, checked(array.Length * 2)));
        }

        // Only a changed family's results are copied out of the existing reusable build workspace.
        private void CapturePart(StructurePool.Part part, int b0, int b1, int c0, int c1, int r0, int r1)
        {
            part.branchesCount = b1 - b0; part.candidatesCount = c1 - c0; part.rendersCount = r1 - r0;
            Room(ref part.branches, part.branchesCount); Room(ref part.candidates, part.candidatesCount);
            Room(ref part.states, part.candidatesCount); Room(ref part.caps, part.candidatesCount);
            Room(ref part.renders, part.rendersCount); Room(ref part.stands, part.rendersCount); Room(ref part.sides, part.rendersCount);
            for (int i = b0; i < b1; i++)
            {
                var b = _branches[i];
                part.branches[i - b0] = new VpMultiCutBranch(0, b.fragment, b.pendingSide, b.pendingOperation,
                    b.candidateStart - c0, b.candidateCount, b.selectedCount, b.renderFragment - r0);
            }
            Array.Copy(_candidates, c0, part.candidates, 0, c1 - c0);
            Array.Copy(_states, c0, part.states, 0, c1 - c0);
            for (int i = c0; i < c1; i++) part.caps[i - c0] = _capIdentity[i];
            for (int i = r0; i < r1; i++)
            {
                var r = _renderFragments[i];
                part.renders[i - r0] = new StructurePool.RenderTemplate(r.root, r.rootPendingSide, r.aggregated, r.branchStart - b0, r.branchCount);
                part.stands[i - r0] = _standsAs[i]; part.sides[i - r0] = _sideIdentity[i];
            }
        }

        internal VpMultiCutBuildOutcome TryBuildIncremental(StructurePool pool, VpMultiCutSnapshot previous,
            LogicalCutLedger ledger, IReadOnlyList<VpMultiCutRegistration> registrations, IVpFragmentPlacement placement)
        {
            if (previous == this) throw new ArgumentException("the adopted snapshot must be separate", nameof(previous));
            Clear(); _structurePool = pool; _buildGeneration++;
            _invalid = VpMultiCutInvalidInput.None; _shortage = VpMultiCutShortage.None; SectionBuildCount = 0;
            pool.Prepare(registrations.Count); pool.Begin();
            Room(ref _parts, registrations.Count); _partCount = registrations.Count;
            bool rebuilt = false;
            long buildsBefore = StructureBuilds, validationsBefore = StructureValidations;
            double collectedSeconds = 0, groupedSeconds = 0;
            try
            {
                for (int i = 0; i < registrations.Count; i++)
                {
                    var input = registrations[i];
                    if (input.reflected == null) throw new ArgumentNullException(nameof(registrations));
                    if (!IsFiniteNonNegative(input.vertexEpsilon)) throw new ArgumentOutOfRangeException(nameof(registrations));
                    if (!ledger.TryGetFamily(input.root, out var family, out long revision) || _partByRoot.ContainsKey(input.root))
                        return Fail(Invalid(VpMultiCutInvalidInput.Lineage));
                    _partByRoot.Add(input.root, i); pool.Add(family, revision, i, input);
                }
                for (int f = 0; f < pool.inputCount; f++)
                {
                    var group = pool.inputs[f];
                    bool reuse = previous != null && previous.IsBuilt && previous._composite && previous._structurePool == pool;
                    int previousMembers = 0;
                    if (reuse)
                    {
                        // Registration membership belongs to the family too (splits and retirements).
                        for (int j = 0; j < group.indices.Count && reuse; j++)
                        {
                            var input = group.registrations[j];
                            reuse = previous._partByRoot.TryGetValue(input.root, out int old)
                                && previous._parts[old].part.Matches(ledger, group.family, group.revision, input);
                            if (reuse) previousMembers = previous._parts[old].partFamilyMembers;
                        }
                        reuse &= previousMembers == group.indices.Count;
                    }
                    if (reuse)
                    {
                        for (int j = 0; j < group.indices.Count; j++)
                        {
                            var part = previous._parts[previous._partByRoot[group.registrations[j].root]].part;
                            part.users++; _parts[group.indices[j]].part = part;
                            _parts[group.indices[j]].partFamilyMembers = group.indices.Count;
                            RegistrationsReused++;
                        }
                        continue;
                    }
                    rebuilt = true; FamiliesRebuilt++;
                    _branchCount = _candidateCount = _renderFragmentCount = 0;
                    _buildingFamily = group.family;
                    VpMultiCutBuildOutcome outcome;
                    long structureBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                    LastCollectSeconds = LastGroupSeconds = 0;
                    try { outcome = TryBuildStructure(ledger, group.registrations); }
                    finally
                    {
                        ValidateCounts.structureSeconds += SecondsSince(structureBegin);
                        collectedSeconds += LastCollectSeconds; groupedSeconds += LastGroupSeconds;
                        _buildingFamily = default;
                        for (int g = 0; g < _reflected.Length; g++) _reflected[g].Release();
                        _lineage.Close(); _segLedger = null; _currentRegistration = -1;
                    }
                    if (outcome != VpMultiCutBuildOutcome.Built) return Fail(outcome);
                    int b0 = 0, c0 = 0, r0 = 0;
                    for (int j = 0; j < group.indices.Count; j++)
                    {
                        int b1 = b0, r1 = r0;
                        while (b1 < _branchCount && _branches[b1].registration == j) b1++;
                        while (r1 < _renderFragmentCount && _renderFragments[r1].registration == j) r1++;
                        int c1 = c0;
                        for (int b = b0; b < b1; b++) c1 = Math.Max(c1, _branches[b].candidateStart + _branches[b].candidateCount);
                        var part = pool.Take(); _parts[group.indices[j]].part = part;
                        _parts[group.indices[j]].partFamilyMembers = group.indices.Count;
                        part.ledger = ledger; part.family = group.family; part.revision = group.revision;
                        part.SetInput(group.registrations[j]);
                        CapturePart(part, b0, b1, c0, c1, r0, r1);
                        b0 = b1; c0 = c1; r0 = r1;
                    }
                }
                _branchCount = _candidateCount = _renderFragmentCount = 0;
                for (int g = 0; g < _partCount; g++)
                {
                    var part = _parts[g].part;
                    _parts[g].branch = _branchCount; _parts[g].candidate = _candidateCount; _parts[g].render = _renderFragmentCount;
                    _branchCount = checked(_branchCount + part.branchesCount);
                    _candidateCount = checked(_candidateCount + part.candidatesCount);
                    _renderFragmentCount = checked(_renderFragmentCount + part.rendersCount);
                }
                if (_branchCount > _branches.Length) return Fail(Short(VpMultiCutShortage.Branches));
                if (_candidateCount > _candidates.Length) return Fail(Short(VpMultiCutShortage.Candidates));
                if (_renderFragmentCount > _renderFragments.Length) return Fail(Short(VpMultiCutShortage.RenderFragments));
                _composite = true;
                VpMultiCutBuildOutcome valid;
                using (s_validate.Auto()) valid = Validate(ledger, registrations, true);
                if (valid != VpMultiCutBuildOutcome.Built) return Fail(valid);
                // The drawing array remains snapshot-local. The branches/candidates/selection arrays are never copied here.
                for (int g = 0; g < _partCount; g++)
                {
                    var range = _parts[g];
                    for (int j = 0; j < range.part.rendersCount; j++)
                    {
                        var r = range.part.renders[j];
                        var matrix = Matrix4x4.identity;
                        if (previous != null && previous.IsBuilt && previous._partByRoot.TryGetValue(range.part.root, out int old)
                            && ReferenceEquals(previous._parts[old].part, range.part))
                            matrix = previous._renderFragments[previous._parts[old].render + j].geometryLocalToWorld;
                        _renderFragments[range.render + j] = new VpMultiCutRenderFragment(g, range.part.bounds, matrix, r.root,
                            r.side, r.aggregate, range.branch + r.branch, r.count, 0, 0, default, 0, 0);
                    }
                }
                _placeInto = rebuilt ? StructuralPlaceCounts : PlacementOnlyPlaceCounts;
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                _renderFragmentsTakenOver = true;
                VpMultiCutBuildOutcome placed;
                try { using (s_place.Auto()) placed = TryApplyPlacements(ledger, registrations, placement, previous); }
                finally { _renderFragmentsTakenOver = false; }
                LastPlaceSeconds = SecondsSince(begin); _placeInto.passes++; _placeInto.seconds += LastPlaceSeconds;
                if (placed != VpMultiCutBuildOutcome.Built) return Fail(placed);
                _registrationCount = registrations.Count; IsBuilt = true;
                return VpMultiCutBuildOutcome.Built;
            }
            catch { Clear(); throw; }
            finally
            {
                LastCollectSeconds = collectedSeconds; LastGroupSeconds = groupedSeconds;
                StructureBuilds = buildsBefore + (rebuilt ? 1 : 0);
                StructureValidations = validationsBefore + (rebuilt ? 1 : 0);
                pool.Begin();
            }
        }

        private int RangeOf(int index, int kind)
        {
            int lo = 0, hi = _partCount;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2; var range = _parts[mid];
                int end = kind == 0 ? range.branch + range.part.branchesCount
                    : kind == 1 ? range.candidate + range.part.candidatesCount : range.render + range.part.rendersCount;
                if (index >= end) lo = mid + 1; else hi = mid;
            }
            return lo;
        }
        private VpMultiCutBranch BranchAt(int index)
        {
            if (!_composite) return _branches[index];
            int g = RangeOf(index, 0); var range = _parts[g]; var b = range.part.branches[index - range.branch];
            return new VpMultiCutBranch(g, b.fragment, b.pendingSide, b.pendingOperation, range.candidate + b.candidateStart,
                b.candidateCount, b.selectedCount, range.render + b.renderFragment);
        }
        private VpClipCandidate CandidateAt(int index)
        { if (!_composite) return _candidates[index]; var r = _parts[RangeOf(index, 1)]; return r.part.candidates[index - r.candidate]; }
        private VpClipSelectionState StateAt(int index)
        { if (!_composite) return _states[index]; var r = _parts[RangeOf(index, 1)]; return r.part.states[index - r.candidate]; }
        private VpMultiCutCapIdentity CapIdentityAt(int index)
        { if (!_composite) return _capIdentity[index]; var r = _parts[RangeOf(index, 1)]; return r.part.caps[index - r.candidate]; }
        private VpMultiCutSideIdentity SideAt(int index)
        { if (!_composite) return _sideIdentity[index]; var r = _parts[RangeOf(index, 2)]; return r.part.sides[index - r.render]; }
        private VpMultiCutStandsAs StandsAt(int index)
        { if (!_composite) return _standsAs[index]; var r = _parts[RangeOf(index, 2)]; return r.part.stands[index - r.render]; }
        private void SetSelectedRange(int index, int count)
        {
            if (_composite && count != 0)
            {
                var r = _parts[RangeOf(index, 1)];
                _selectedCandidates.Use(r.part.candidates); _selectedStates.Use(r.part.states);
                index -= r.candidate;
            }
            else { _selectedCandidates.Use(_candidates); _selectedStates.Use(_states); }
            _selectedCandidates.Set(index, count); _selectedStates.Set(index, count);
        }
    }
}
