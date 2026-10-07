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
                // Which taking of this part this is: a part is pooled, so the object alone does not say that what it
                // holds is what it held (DESIGN 5.6: what a display's draw slots were written for).
                internal long serial;

                // Where the held placements have this part's render fragments (DESIGN 5.6, D-205): the first one's
                // number in their arrays, of which mapping, for which taking of the part. Theirs to write and read.
                internal long holdEpoch, holdSerial;
                internal int holdStart;
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

                // The caps of this part's render fragments, in the geometry's local frame (D-208): made when the part is,
                // read by whichever snapshot holds it, and gone when it is given back.
                internal readonly LocalCapSet shapes = new LocalCapSet();
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
                    shapes.Clear();
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
            private long _serials;
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
                Part part = _free[index]; _free.RemoveAt(index); part.users = 1; part.serial = ++_serials; return part;
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

        /// <summary>
        /// Whether this snapshot, built, is of the very structure these numbers count -- the pool, how many
        /// registrations, the ledger's revision and the display's own count of its inputs: what a build asks of its two
        /// snapshots before it keeps a structure whole, asked of one.
        /// </summary>
        internal bool IsOfStructure(StructurePool pool, int registrations, long stampLedger, long stampInputs)
        {
            return IsBuilt && _composite && _stampValid && _structurePool == pool && stampLedger >= 0 && stampInputs >= 0
                   && _stampLedger == stampLedger && _stampInputs == stampInputs && _registrationCount == registrations;
        }

        /// <summary>
        /// This snapshot, the adopted one, stands for one more collection as it is (DESIGN 5.6, D-204): no placement is
        /// asked and nothing of it is made again. What its own placement pass reported of itself -- the render
        /// fragments placed anew, the caps that changed, the sections it took -- is of that pass, not of this
        /// collection, and reads as nothing from here on. Not a placement pass, and not counted as one: nothing is
        /// "kept as settled" by it.
        /// </summary>
        internal void NotePlacementsReused()
        {
            _allPlacedAnew = false;
            _placedAnewCount = 0;
            // (The placed cap vertices, if a reader had them made, stand: nothing was placed anew.)
            _capsChangedFrom = int.MaxValue;
            _capsChangedTo = 0;
            SectionBuildCount = 0;
            _stages.placementsReused++;
            _stages.queriesOmitted += _renderFragmentCount;
        }

        // The stages' calls and time, what the rebuilt families held and the work arrays' growth, since this snapshot
        // was made (2026-10-07, for observation; a display takes a collection's share as a difference).
        private VpSnapshotStageTotals _stages;

        internal VpSnapshotStageTotals StageTotals
        {
            get
            {
                VpSnapshotStageTotals totals = _stages;
                totals.structureSeconds = ValidateCounts.structureSeconds;
                totals.familiesRebuilt = FamiliesRebuilt;
                totals.registrationsReused = RegistrationsReused;
                totals.keptWhole = StructuresKeptWhole;
                totals.walks = StructureWalks;
                totals.placeCalls = StructuralPlaceCounts.passes + PlacementOnlyPlaceCounts.passes;
                totals.placeSeconds = StructuralPlaceCounts.seconds + PlacementOnlyPlaceCounts.seconds;
                totals.capShapesBuilt = CapShapesBuilt;
                totals.capShapeVertices = CapShapeVertices;
                return totals;
            }
        }
        internal object StructureForTest(LogicalFragmentId root) => _partByRoot.TryGetValue(root, out int i) ? _parts[i].part : null;

        /// <summary>
        /// What registration <paramref name="registration"/>'s structure is, as something to tell apart and nothing
        /// else: the part it was settled into and which taking of that part. A registration whose family did not
        /// change is given the adopted snapshot's own part (see <see cref="TryBuildIncremental"/>), so the same answer
        /// from two builds says that its branches, its render fragments, what each stands as and what each side is are
        /// the same -- without anything being compared. Null when this snapshot is not made of such parts.
        /// </summary>
        internal object StructurePartAt(int registration, out long serial)
        {
            serial = 0;
            if (!IsBuilt || !_composite || registration < 0 || registration >= _partCount || _parts[registration].part == null)
            {
                return null;
            }

            serial = _parts[registration].part.serial;
            return _parts[registration].part;
        }

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

        private void Room<T>(ref T[] array, int count)
        {
            if (array.Length >= count) return;
            int room = Math.Max(count, checked(array.Length * 2));
            _stages.arrayGrowths++;
            _stages.arrayGrowthElements += room;   // the new array's length (Array.Resize makes a new one and copies)
            Array.Resize(ref array, room);
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

        // What this snapshot's structure was settled from, as its builder counts it (the ledger's Revision and the
        // display's own input revision): equal numbers mean nothing a structure is made from has changed since.
        private long _stampLedger = -1, _stampInputs = -1;
        private bool _stampValid;

        /// <summary>Observation: builds that kept the whole structure, and builds that went through every registration.</summary>
        internal long StructuresKeptWhole { get; private set; }
        internal long StructureWalks { get; private set; }
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        /// <summary>Development diagnosis: a structure that was to be kept whole and whose parts were not the adopted one's.</summary>
        internal long KeptStructureMismatches { get; private set; }
#endif

        // The render fragments the last placement pass did not keep as settled (placed anew, their clip and caps made
        // again), in order; or all of them, when the pass was one that does not tell them apart.
        private int[] _placedAnew = Array.Empty<int>();
        private int _placedAnewCount;
        private bool _allPlacedAnew = true;
        internal bool AllRenderFragmentsPlacedAnew => _allPlacedAnew;
        internal int PlacedAnewCount => _placedAnewCount;
        internal int PlacedAnewAt(int index) => _placedAnew[index];

        private bool CanKeepStructure(StructurePool pool, VpMultiCutSnapshot previous, int registrations, long stampLedger, long stampInputs)
        {
            if (!(IsBuilt && _composite && _stampValid && _structurePool == pool
                  && _stampLedger == stampLedger && _stampInputs == stampInputs && _registrationCount == registrations
                  && previous != null && previous.IsBuilt && previous._composite && previous._stampValid && previous._structurePool == pool
                  && previous._stampLedger == stampLedger && previous._stampInputs == stampInputs
                  && previous._registrationCount == registrations && previous._partCount == _partCount
                  && previous._renderFragmentCount == _renderFragmentCount))
            {
                return false;
            }

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            // The stamp is what says the two are of one structure. Where the diagnosis is compiled, that is looked at too.
            for (int g = 0; g < _partCount; g++)
            {
                if (!ReferenceEquals(previous._parts[g].part, _parts[g].part) || previous._parts[g].render != _parts[g].render)
                {
                    KeptStructureMismatches++;
                    return false;
                }
            }
#endif
            return true;
        }

        // The structure as it stands -- the parts, where each begins, every render fragment's own record -- with only
        // where things stand settled again. What Clear resets of a placement pass is reset; nothing of the structure is.
        private VpMultiCutBuildOutcome TryPlaceOnKeptStructure(VpMultiCutSnapshot previous, LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations, IVpFragmentPlacement placement, VpHeldPlacements holds)
        {
            IsBuilt = false;
            _conditionCount = 0; _capCount = 0; _capVertexCount = 0; _sectionCount = 0;
            _buildGeneration++;
            _invalid = VpMultiCutInvalidInput.None; _shortage = VpMultiCutShortage.None; SectionBuildCount = 0;
            StructuresKeptWhole++;
            try
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                VpMultiCutBuildOutcome valid;
                using (s_validate.Auto()) valid = ValidatePlacementInputs(registrations);
                if (valid != VpMultiCutBuildOutcome.Built) return Fail(valid);
#endif
                // Each render fragment starts where the adopted snapshot placed it, with nothing of a placement on it
                // yet: the same record the structure's assembly would write, read from the two records side by side.
                for (int r = 0; r < _renderFragmentCount; r++)
                {
                    VpMultiCutRenderFragment kept = _renderFragments[r];
                    _renderFragments[r] = new VpMultiCutRenderFragment(kept.registration, kept.localBounds,
                        previous._renderFragments[r].geometryLocalToWorld, kept.root, kept.rootPendingSide, kept.aggregated,
                        kept.branchStart, kept.branchCount, 0, 0, default, 0, 0);
                }
                _placeInto = PlacementOnlyPlaceCounts;

                // Who is asked (DESIGN 5.6, D-205): with held placements, a host that vouches for its step count and
                // a pass that is not a test's or a diagnosis's, the ordinary render fragments and the held ones near a
                // reference point -- picked here, before the pass's own time. Otherwise every one, as ever.
                int asksAll = -1;   // the kind of a pass that asks everything, for the held placements' counts
                if (holds != null && placement == null) holds.Forget();   // nothing is asked of anyone: nothing is held
                else if (holds != null && (PlacePhasedDiagnosis || placeQueriesOnlyForTest || placementRebuildUnchangedForTest)) { holds.BeginOther(); asksAll = 2; }
                else if (holds != null && holds.BeginKept(_renderFragmentCount, _stampLedger, _stampInputs)) _holdsNow = holds;
                else if (holds != null) asksAll = 1;
                long queriesBefore = _placeInto.queries;
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                _renderFragmentsTakenOver = true;
                VpMultiCutBuildOutcome placed;
                try { using (s_place.Auto()) placed = TryApplyPlacements(ledger, registrations, placement, previous); }
                finally { _renderFragmentsTakenOver = false; _holdsNow = null; }
                LastPlaceSeconds = SecondsSince(begin); _placeInto.passes++; _placeInto.seconds += LastPlaceSeconds;
                if (asksAll >= 0) holds.CountConventional(asksAll, _placeInto.queries - queriesBefore);
                else if (holds != null && placement != null && placed == VpMultiCutBuildOutcome.Built) holds.PassEnded();   // what was told is taken (D-207)
                if (placed != VpMultiCutBuildOutcome.Built) return Fail(placed);
                IsBuilt = true;
                return VpMultiCutBuildOutcome.Built;
            }
            catch { Clear(); throw; }
        }

        /// <summary>
        /// <paramref name="stampLedger"/> and <paramref name="stampInputs"/>, when given (not negative), are what the
        /// caller counts of everything a structure is settled from -- the ledger's Revision, and its own count of the
        /// registrations and the placement lookup changing. A snapshot built for those very numbers, beside an adopted
        /// one built for them too, keeps its structure whole: no registration is asked of the ledger, matched or
        /// assembled again, and only where things stand is settled. Any other case goes through every registration as
        /// before -- taking a structure over is never what decides whether a frame is right.
        /// </summary>
        internal VpMultiCutBuildOutcome TryBuildIncremental(StructurePool pool, VpMultiCutSnapshot previous,
            LogicalCutLedger ledger, IReadOnlyList<VpMultiCutRegistration> registrations, IVpFragmentPlacement placement,
            long stampLedger = -1, long stampInputs = -1, VpHeldPlacements holds = null)
        {
            if (previous == this) throw new ArgumentException("the adopted snapshot must be separate", nameof(previous));
            if (stampLedger >= 0 && stampInputs >= 0 && CanKeepStructure(pool, previous, registrations.Count, stampLedger, stampInputs))
            {
                return TryPlaceOnKeptStructure(previous, ledger, registrations, placement, holds);
            }

            StructureWalks++;
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
                    _stages.registrationsRebuilt += group.indices.Count;
                    _stages.renderFragmentsRebuilt += _renderFragmentCount;
                    _stages.branchesRebuilt += _branchCount;
                    _stages.candidatesRebuilt += _candidateCount;
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

                        // The caps' shapes of this registration, with its structure and in its local frame (D-208).
                        VpMultiCutRegistration shapedFor = group.registrations[j];
                        long shapesBegan = System.Diagnostics.Stopwatch.GetTimestamp();
                        VpMultiCutBuildOutcome shaped = TryBuildLocalCaps(part, shapedFor);
                        _stages.capShapeSeconds += SecondsSince(shapesBegan);
                        if (shaped != VpMultiCutBuildOutcome.Built) return Fail(shaped);
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                // The numeric input-contract diagnosis of every registration (DESIGN 5.6): not compiled for a
                // non-Development Player.
                VpMultiCutBuildOutcome valid;
                using (s_validate.Auto()) valid = ValidatePlacementInputs(registrations);
                if (valid != VpMultiCutBuildOutcome.Built) return Fail(valid);
#endif
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

                // The render fragments are numbered anew. What is held goes with the parts (D-205): a part that is the
                // one the held placements mapped before -- a family unchanged, taken over from the adopted snapshot --
                // carries its render fragments' state to their new numbers; any other starts ordinary; what was held of
                // parts that are gone leaves the tree. No placement is asked for this. Then the pass asks its targets
                // only, as a pass over a kept structure does.
                int asksAll = -1;
                if (holds != null && placement == null) holds.Forget();
                else if (holds != null && (PlacePhasedDiagnosis || placeQueriesOnlyForTest || placementRebuildUnchangedForTest)) { holds.BeginOther(); asksAll = 2; }
                else if (holds != null && holds.BeginStructure(_renderFragmentCount))
                {
                    for (int g = 0; g < _partCount; g++) holds.MapPart(_parts[g].part, _parts[g].render);
                    holds.EndStructure(stampLedger, stampInputs);
                    _holdsNow = holds;
                }
                else if (holds != null) asksAll = 1;
                long queriesBefore = _placeInto.queries;
                long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                _renderFragmentsTakenOver = true;
                VpMultiCutBuildOutcome placed;
                try { using (s_place.Auto()) placed = TryApplyPlacements(ledger, registrations, placement, previous); }
                finally { _renderFragmentsTakenOver = false; _holdsNow = null; }
                LastPlaceSeconds = SecondsSince(begin); _placeInto.passes++; _placeInto.seconds += LastPlaceSeconds;
                if (asksAll >= 0) holds.CountConventional(asksAll, _placeInto.queries - queriesBefore);
                else if (holds != null && placement != null && placed == VpMultiCutBuildOutcome.Built) holds.PassEnded();   // what was told is taken (D-207)
                if (placed != VpMultiCutBuildOutcome.Built) return Fail(placed);
                _registrationCount = registrations.Count; IsBuilt = true;
                _stampLedger = stampLedger; _stampInputs = stampInputs; _stampValid = stampLedger >= 0 && stampInputs >= 0;
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

        // The placement pass's two readings, a render fragment at a time, without a search (TL, 2026-10-05). The parts
        // are held in registration order -- _parts[g] is registration g's own part, with where its branches and its
        // render fragments begin in this snapshot's numbering -- and a render fragment's record names its registration
        // and its branch by that numbering. So the part is the one at the registration's index, and the place inside
        // it is the number less where the part begins. The answers are StandsAt's and BranchAt's own; the shared part
        // is only read.
        private VpMultiCutStandsAs StandsOf(int renderFragment, int registration)
        {
            if (!_composite) return _standsAs[renderFragment];
            ref readonly PartRange range = ref _parts[registration];
            return range.part.stands[renderFragment - range.render];
        }

        private int SelectedCountOf(int branch, int registration)
        {
            if (!_composite) return _branches[branch].selectedCount;
            ref readonly PartRange range = ref _parts[registration];
            return range.part.branches[branch - range.branch].selectedCount;
        }

        /// <summary>
        /// Tests only: over every render fragment of this snapshot, how many of the placement pass's readings by the
        /// registration differ from the searched ones (what it stands as; its branch's selected count; and, of a
        /// snapshot of shared parts, that the registration's part is the part a search finds). Also how many render
        /// fragments were looked at, how many of them are of a part that does not begin at this snapshot's zero (their
        /// places inside the part are not their numbers), and how many are not the first of their part.
        /// </summary>
        internal int PlacementReadingsDifferingForTest(out int lookedAt, out int offset, out int notFirst)
        {
            lookedAt = offset = notFirst = 0;
            if (!IsBuilt) return 0;
            int differing = 0;
            for (int r = 0; r < _renderFragmentCount; r++)
            {
                VpMultiCutRenderFragment renderFragment = _renderFragments[r];
                int g = renderFragment.registration;
                lookedAt++;
                VpMultiCutStandsAs direct = StandsOf(r, g), searched = StandsAt(r);
                if (direct.fragment != searched.fragment || !direct.operation.Equals(searched.operation) || direct.side != searched.side) differing++;
                if (SelectedCountOf(renderFragment.branchStart, g) != BranchAt(renderFragment.branchStart).selectedCount) differing++;
                if (_composite)
                {
                    if (g < 0 || g >= _partCount || RangeOf(r, 2) != g || RangeOf(renderFragment.branchStart, 0) != g) differing++;
                    else
                    {
                        if (_parts[g].render > 0) offset++;
                        if (r > _parts[g].render) notFirst++;
                    }
                }
            }

            return differing;
        }
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
