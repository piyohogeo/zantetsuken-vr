using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>The fixed room a <see cref="VpMultiCutSnapshot"/> is made with. No product value is set anywhere.</summary>
    public readonly struct VpMultiCutCapacities
    {
        public VpMultiCutCapacities(int branches, int candidates, int renderFragments, int caps, int chainDepth)
        {
            this.branches = branches;
            this.candidates = candidates;
            this.renderFragments = renderFragments;
            this.caps = caps;
            this.chainDepth = chainDepth;
        }

        /// <summary>Logical branches: live fragments, and each side of a displayable pending cut.</summary>
        public readonly int branches;

        /// <summary>Candidates over every branch together, each branch keeping its own. Not bounded by eight.</summary>
        public readonly int candidates;

        /// <summary>Render fragments: what is drawn once each, after the aggregation at the first Ignored boundary.</summary>
        public readonly int renderFragments;

        /// <summary>
        /// Drawing caps over every render fragment together: one per selected boundary of each. The conditions take the
        /// same room, one per selected boundary, and the cap vertices up to <see cref="VpCapPolygonClip.MaxVertices"/>
        /// per cap.
        /// </summary>
        public readonly int caps;

        /// <summary>
        /// The longest chain of boundaries one fragment may have, reflected ones included. The room for reading a chain
        /// only to check it -- an aggregation root's, a retired fragment's -- is derived from this, so checking never takes
        /// from <see cref="candidates"/>.
        /// </summary>
        public readonly int chainDepth;
    }

    /// <summary>What <see cref="VpMultiCutSnapshot.TryBuild"/> decided.</summary>
    public enum VpMultiCutBuildOutcome
    {
        /// <summary>Built: the snapshot may be adopted.</summary>
        Built = 0,

        /// <summary>Some room was short -- branches, candidates, chain, render fragments, caps or the walk itself.</summary>
        CapacityExceeded = 1,

        /// <summary>
        /// Input that cannot be built from: bounds, a placement or a mapping outside the input contract (checked before
        /// anything else, whether or not there is a plane to convert); an unknown fragment; a lineage that is not a tree
        /// from the root; an aggregation root whose own chain is not the selected prefix; a plane the mapping or the
        /// placement cannot carry; or an offset or a cap vertex that does not come out finite.
        /// </summary>
        InvalidInput = 2,

        /// <summary>A cut on a render fragment's lineage has no settled anchor distribution to take its offset from.</summary>
        UnsettledDistribution = 3,

        /// <summary>
        /// A retired fragment lies past an Ignored boundary, inside what would be drawn once as the shape before it. The
        /// aggregation alone cannot say whether that shape may still be shown, so nothing is guessed and nothing is built.
        /// This is **not** a statement that a previously adopted snapshot is safe to keep drawing -- it may show the
        /// retired region too. What a display does then is unresolved and belongs to the drawing connection; it is not
        /// the same as a capacity shortfall and is not to be handled the same way by default.
        /// </summary>
        RetiredInsideAggregate = 4,
    }

    /// <summary>
    /// One logical branch: a live fragment drawn whole (<see cref="pendingSide"/> 0), or one side of a live fragment's
    /// displayable pending cut (+1 or -1, with no child id -- none is issued before publication). Its candidates, in the
    /// ledger's admission order, and their selection are its own.
    /// </summary>
    public readonly struct VpMultiCutBranch
    {
        internal VpMultiCutBranch(
            LogicalFragmentId fragment, float pendingSide, int candidateStart, int candidateCount, int selectedCount,
            int renderFragment)
        {
            this.fragment = fragment;
            this.pendingSide = pendingSide;
            this.candidateStart = candidateStart;
            this.candidateCount = candidateCount;
            this.selectedCount = selectedCount;
            this.renderFragment = renderFragment;
        }

        public readonly LogicalFragmentId fragment;
        public readonly float pendingSide;
        public readonly int candidateStart;
        public readonly int candidateCount;

        /// <summary>How many of its candidates are Selected: always a prefix of them.</summary>
        public readonly int selectedCount;

        /// <summary>The render fragment this branch is drawn as.</summary>
        public readonly int renderFragment;
    }

    /// <summary>
    /// What is drawn once: one branch whose candidates are all selected, or every branch of one registration that shares
    /// the same selected prefix, drawn as the shape before the first Ignored boundary (DESIGN 5.2, D-181).
    /// </summary>
    public readonly struct VpMultiCutRenderFragment
    {
        internal VpMultiCutRenderFragment(
            LogicalFragmentId root, float rootPendingSide, bool aggregated, int branchStart, int branchCount,
            int conditionStart, int conditionCount, Vector3 offset, VpInstanceClip clip, int capStart, int capCount)
        {
            this.root = root;
            this.rootPendingSide = rootPendingSide;
            this.aggregated = aggregated;
            this.branchStart = branchStart;
            this.branchCount = branchCount;
            this.conditionStart = conditionStart;
            this.conditionCount = conditionCount;
            this.offset = offset;
            this.clip = clip;
            this.capStart = capStart;
            this.capCount = capCount;
        }

        /// <summary>
        /// The fragment whose shape this is: the branch's own (and its pending side) when nothing is Ignored, else the
        /// fragment the first Ignored boundary cut, drawn whole.
        /// </summary>
        public readonly LogicalFragmentId root;

        public readonly float rootPendingSide;

        /// <summary>Whether this is drawn as the shape before an Ignored boundary.</summary>
        public readonly bool aggregated;

        /// <summary>The branches it stands for: contiguous in the branch list.</summary>
        public readonly int branchStart;
        public readonly int branchCount;

        /// <summary>
        /// Its compatibility conditions: every unreflected temporary boundary of <see cref="root"/>, which the prefix
        /// rule makes the selected ones, with each boundary's current world plane before any separation.
        /// </summary>
        public readonly int conditionStart;
        public readonly int conditionCount;

        /// <summary>The separation, in world space, added once after the placement: never to a plane.</summary>
        public readonly Vector3 offset;

        /// <summary>The selected half-spaces in world space, before the separation, and the separation itself.</summary>
        public readonly VpInstanceClip clip;

        public readonly int capStart;
        public readonly int capCount;
    }

    /// <summary>
    /// One drawing cap: one render fragment and one of its selected boundaries (DESIGN 5.2). Its polygon is the box
    /// and the boundary's plane (at most six vertices), cut by the render fragment's other selected half-spaces (at most
    /// fourteen). A count of zero is a normal answer -- nothing of area is left -- and says nothing about visibility or
    /// being Ignored.
    /// </summary>
    public readonly struct VpMultiCutCap
    {
        internal VpMultiCutCap(
            int renderFragment, VpClipBoundary boundary, float4 worldPlane, Vector3 outwardNormal, int vertexStart,
            int initialVertexCount, int vertexCount)
        {
            this.renderFragment = renderFragment;
            this.boundary = boundary;
            this.worldPlane = worldPlane;
            this.outwardNormal = outwardNormal;
            this.vertexStart = vertexStart;
            this.initialVertexCount = initialVertexCount;
            this.vertexCount = vertexCount;
        }

        public readonly int renderFragment;
        public readonly VpClipBoundary boundary;

        /// <summary>The boundary's plane in world space, before the separation.</summary>
        public readonly float4 worldPlane;

        /// <summary>The outward normal of the side the cap closes: <c>-side * n</c>.</summary>
        public readonly Vector3 outwardNormal;

        /// <summary>Where its vertices are: world space, the render fragment's separation added once.</summary>
        public readonly int vertexStart;

        /// <summary>The vertices of the box-and-plane section before the other planes cut it: 0 to 6.</summary>
        public readonly int initialVertexCount;

        /// <summary>The vertices after: 0 to 14.</summary>
        public readonly int vertexCount;
    }

    /// <summary>
    /// The display snapshot of one registration's lineage under several cuts, on the CPU (DESIGN 5.1, 5.2, 5.6; D-180,
    /// D-181). Not connected to drawing: it builds what a display would adopt, and nothing more.
    /// <para>
    /// **Input.** One registration: its root fragment and ledger; the source geometry's box (its own coordinates) and
    /// placement; the one mapping from the lineage's common logical frame -- the frame every adopted plane of this lineage
    /// is given in -- to the geometry's coordinates, which the caller states and nothing here assumes; the boundaries
    /// the geometry already reflects (required: an empty set says "none"); the separation; and the vertex epsilon.
    /// Lineages whose children have frames of their own are not this input.
    /// </para>
    /// <para>
    /// **Input contract, checked first** -- also when there is no candidate and so no plane to convert. The box: finite
    /// centre and extents, extents not negative. The placement: finite, affine (last row exactly 0, 0, 0, 1) and
    /// invertible. The mapping: finite, affine and rigid -- a rotation (orthonormal columns within 1e-4, determinant +1)
    /// and a translation, nothing else. Anything else is <see cref="VpMultiCutBuildOutcome.InvalidInput"/>; no projective
    /// or scaling mapping is taken on.
    /// </para>
    /// <para>
    /// **Branches.** From the root through published operations: a live fragment is a branch, or two -- one per side --
    /// when its pending cut is displayable (prepared); a replaced one is its two children; a retired one is nothing.
    /// Each branch's candidates and their selection are collected and kept by the one rule
    /// (<see cref="VpClipCandidates"/>), per branch, never flattened.
    /// </para>
    /// <para>
    /// **Render fragments.** Branches of this registration that have an Ignored boundary are one render fragment when
    /// they share both the aggregation root -- the fragment the first Ignored boundary cut -- and the selected prefix,
    /// and that root's own chain of unreflected boundaries is exactly the prefix; they are drawn once as that root whole.
    /// A shared prefix alone never merges two roots. A branch with nothing Ignored is its own. A retired fragment past an
    /// Ignored boundary makes that shape undecidable, and the build says so rather than drawing it back.
    /// </para>
    /// <para>
    /// **Offset.** A render fragment's separation is summed down its lineage from the registration's root: each cut adds
    /// <c>side * world normal * separation</c> when that side was free by the cut's own settled anchor distribution,
    /// never by a current owner. Ignored boundaries add nothing (they are below the render fragment's root). A pending
    /// side and its published child give the same value.
    /// </para>
    /// <para>
    /// **Who adds which separation.** Everything before the registration's root is the placement's: the placement given
    /// must already hold whatever separated the root itself, and must **not** hold any of the separation summed here.
    /// From the root down, the separation is this snapshot's, summed for every cut on the way -- a cut whose boundary the
    /// geometry already reflects included, because a boundary reflected in the geometry and a separation taken into the
    /// placement are different things. When real geometry commits are connected, whatever separation is then taken into
    /// a placement must not be summed here again; that connection is not made by this, and a result here is not evidence
    /// that it holds.
    /// </para>
    /// <para>
    /// **Spaces.** Candidate planes are the ledger's, in the lineage's frame. Conditions, clip planes and cap planes are
    /// world space before the separation; cap vertices are world space with the separation added once; the box is the
    /// geometry's own, placed by the placement.
    /// </para>
    /// <para>
    /// **Adoption.** A build writes only this object. It either ends <see cref="VpMultiCutBuildOutcome.Built"/> with
    /// <see cref="IsBuilt"/> true, or leaves <see cref="IsBuilt"/> false with nothing readable; a caller keeps its
    /// adopted snapshot in another instance and swaps only on success. The ledger and the inputs are only read. The room
    /// is fixed when this is made and never grows.
    /// </para>
    /// </summary>
    public sealed class VpMultiCutSnapshot
    {
        private readonly VpMultiCutCapacities _capacities;
        private readonly VpMultiCutBranch[] _branches;
        private readonly VpClipCandidate[] _candidates;
        private readonly VpClipSelectionState[] _states;
        private readonly VpMultiCutRenderFragment[] _renderFragments;
        private readonly VpCapConstraint[] _conditions;
        private readonly VpMultiCutCap[] _caps;
        private readonly Vector3[] _capVertices;

        // Work room, made once.
        private readonly VpClipBoundary[] _chain;

        // The chains read only to be checked -- an aggregation root's, a retired fragment's -- go here, never into the
        // candidates kept: a chain has no more candidates than boundaries, so the chain depth is room enough.
        private readonly VpClipCandidate[] _checkCandidates;
        private readonly VpClipSelectionState[] _checkStates;
        private readonly LogicalFragmentId[] _stack;
        private readonly float4[] _localPlanes = new float4[VpClipCandidates.Capacity];
        private readonly float4[] _worldPlanes = new float4[VpClipCandidates.Capacity];
        private readonly VpClipHalfSpace[] _halfSpaces = new VpClipHalfSpace[VpClipCandidates.Capacity];
        private readonly Vector3[] _initial = new Vector3[VpCapBoundsPolygon.MaxVertices];
        private readonly Vector3[] _clipped = new Vector3[VpCapPolygonClip.MaxVertices];
        private readonly VpCapBoundsPolygon _section = new VpCapBoundsPolygon();
        private readonly RangeList<VpClipCandidate> _selectedCandidates;
        private readonly RangeList<VpClipSelectionState> _selectedStates;
        private readonly RangeList<float4> _selectedPlanes;
        private readonly RangeList<VpClipHalfSpace> _selectedHalfSpaces;

        private int _branchCount;
        private int _candidateCount;
        private int _renderFragmentCount;
        private int _conditionCount;
        private int _capCount;
        private int _capVertexCount;
        private Bounds _localBounds;
        private Matrix4x4 _geometryLocalToWorld;

        /// <summary>
        /// Makes the room. Every derived size -- the cap vertices, the walk -- is worked out in 64-bit arithmetic and
        /// must be an int.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A capacity is not positive, or a derived size does not fit.</exception>
        public VpMultiCutSnapshot(VpMultiCutCapacities capacities)
        {
            if (capacities.branches <= 0 || capacities.candidates < 0 || capacities.renderFragments <= 0
                || capacities.caps < 0 || capacities.chainDepth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities));
            }

            long capVertices = (long)capacities.caps * VpCapPolygonClip.MaxVertices;
            long stack = ((long)capacities.branches * 2) + 2;
            if (capVertices > int.MaxValue || stack > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities), "a derived size does not fit an int");
            }

            _capacities = capacities;
            _branches = new VpMultiCutBranch[capacities.branches];
            _candidates = new VpClipCandidate[capacities.candidates];
            _states = new VpClipSelectionState[capacities.candidates];
            _renderFragments = new VpMultiCutRenderFragment[capacities.renderFragments];
            _conditions = new VpCapConstraint[capacities.caps];
            _caps = new VpMultiCutCap[capacities.caps];
            _capVertices = new Vector3[(int)capVertices];
            _chain = new VpClipBoundary[capacities.chainDepth];
            _checkCandidates = new VpClipCandidate[capacities.chainDepth];
            _checkStates = new VpClipSelectionState[capacities.chainDepth];
            _stack = new LogicalFragmentId[(int)stack];
            _selectedCandidates = new RangeList<VpClipCandidate>(_candidates);
            _selectedStates = new RangeList<VpClipSelectionState>(_states);
            _selectedPlanes = new RangeList<float4>(_worldPlanes);
            _selectedHalfSpaces = new RangeList<VpClipHalfSpace>(_halfSpaces);
        }

        public VpMultiCutCapacities Capacities => _capacities;

        /// <summary>The source geometry's box the last successful build was made with, in the geometry's coordinates.</summary>
        public Bounds LocalBounds => IsBuilt ? _localBounds : default;

        /// <summary>The placement the last successful build was made with: the separation summed here is not in it.</summary>
        public Matrix4x4 GeometryLocalToWorld => IsBuilt ? _geometryLocalToWorld : default;

        /// <summary>
        /// A look at one cap's vertices (world space, separation added) in this snapshot's own array: nothing is copied,
        /// and it is good only until this snapshot is built again. For a caller that finishes with it before then.
        /// </summary>
        internal VpArrayRange<Vector3> CapPolygon(int capIndex)
        {
            if (!TryGetCap(capIndex, out VpMultiCutCap cap))
            {
                throw new ArgumentOutOfRangeException(nameof(capIndex));
            }

            return new VpArrayRange<Vector3>(_capVertices, cap.vertexStart, cap.vertexCount);
        }

        /// <summary>
        /// A look at one render fragment's conditions in this snapshot's own array, on the same terms as
        /// <see cref="CapPolygon"/>.
        /// </summary>
        internal VpArrayRange<VpCapConstraint> Conditions(in VpMultiCutRenderFragment renderFragment)
        {
            if (!IsBuilt || renderFragment.conditionStart < 0
                || renderFragment.conditionStart > _conditionCount - renderFragment.conditionCount)
            {
                throw new ArgumentOutOfRangeException(nameof(renderFragment));
            }

            return new VpArrayRange<VpCapConstraint>(_conditions, renderFragment.conditionStart, renderFragment.conditionCount);
        }

        /// <summary>Whether the last build succeeded; nothing is readable otherwise.</summary>
        public bool IsBuilt { get; private set; }

        public int BranchCount => IsBuilt ? _branchCount : 0;
        public int CandidateCount => IsBuilt ? _candidateCount : 0;
        public int RenderFragmentCount => IsBuilt ? _renderFragmentCount : 0;
        public int ConditionCount => IsBuilt ? _conditionCount : 0;
        public int CapCount => IsBuilt ? _capCount : 0;
        public int CapVertexCount => IsBuilt ? _capVertexCount : 0;

        public bool TryGetBranch(int index, out VpMultiCutBranch branch)
        {
            bool ok = IsBuilt && index >= 0 && index < _branchCount;
            branch = ok ? _branches[index] : default;
            return ok;
        }

        public bool TryGetCandidate(int index, out VpClipCandidate candidate, out VpClipSelectionState state)
        {
            bool ok = IsBuilt && index >= 0 && index < _candidateCount;
            candidate = ok ? _candidates[index] : default;
            state = ok ? _states[index] : default;
            return ok;
        }

        public bool TryGetRenderFragment(int index, out VpMultiCutRenderFragment renderFragment)
        {
            bool ok = IsBuilt && index >= 0 && index < _renderFragmentCount;
            renderFragment = ok ? _renderFragments[index] : default;
            return ok;
        }

        public bool TryGetCondition(int index, out VpCapConstraint condition)
        {
            bool ok = IsBuilt && index >= 0 && index < _conditionCount;
            condition = ok ? _conditions[index] : default;
            return ok;
        }

        public bool TryGetCap(int index, out VpMultiCutCap cap)
        {
            bool ok = IsBuilt && index >= 0 && index < _capCount;
            cap = ok ? _caps[index] : default;
            return ok;
        }

        /// <summary>One vertex of one cap, in world space with the separation added.</summary>
        public bool TryGetCapVertex(int capIndex, int vertex, out Vector3 world)
        {
            world = default;
            if (!TryGetCap(capIndex, out VpMultiCutCap cap) || vertex < 0 || vertex >= cap.vertexCount)
            {
                return false;
            }

            world = _capVertices[cap.vertexStart + vertex];
            return true;
        }

        /// <summary>
        /// Builds this snapshot from one registration's lineage. See the class notes for the input, the spaces and the
        /// adoption rule. Anything but <see cref="VpMultiCutBuildOutcome.Built"/> leaves nothing readable here, and the
        /// ledger and every input exactly as they were.
        /// </summary>
        /// <param name="lineageToGeometryLocal">
        /// The caller's statement: every adopted plane of this lineage is in one logical frame, and this takes that frame
        /// to the geometry's coordinates. Required; identity is a statement too, and is never assumed.
        /// </param>
        /// <param name="reflected">The boundaries the geometry already reflects. Required: null is not "none".</param>
        /// <exception cref="ArgumentNullException">The ledger or the reflected set is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The separation or the epsilon is negative or not finite.</exception>
        public VpMultiCutBuildOutcome TryBuild(
            LogicalCutLedger ledger,
            LogicalFragmentId root,
            Bounds localBounds,
            Matrix4x4 geometryLocalToWorld,
            Matrix4x4 lineageToGeometryLocal,
            IReadOnlyCollection<VpClipBoundary> reflected,
            float separation,
            float vertexEpsilon)
        {
            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (reflected == null)
            {
                throw new ArgumentNullException(nameof(reflected), "what the geometry reflects must be said, even if it is nothing");
            }

            if (!IsFiniteNonNegative(separation))
            {
                throw new ArgumentOutOfRangeException(nameof(separation));
            }

            if (!IsFiniteNonNegative(vertexEpsilon))
            {
                throw new ArgumentOutOfRangeException(nameof(vertexEpsilon));
            }

            IsBuilt = false;
            _branchCount = 0;
            _candidateCount = 0;
            _renderFragmentCount = 0;
            _conditionCount = 0;
            _capCount = 0;
            _capVertexCount = 0;

            if (!IsWithinContract(localBounds) || !IsPlacement(geometryLocalToWorld) || !IsRigid(lineageToGeometryLocal))
            {
                return VpMultiCutBuildOutcome.InvalidInput;
            }

            VpMultiCutBuildOutcome outcome = TryCollectBranches(ledger, root, reflected);
            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                return Fail(outcome);
            }

            outcome = TryGroup(ledger, reflected);
            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                return Fail(outcome);
            }

            for (int r = 0; r < _renderFragmentCount; r++)
            {
                outcome = TryBuildRenderFragment(
                    ledger, root, r, localBounds, geometryLocalToWorld, lineageToGeometryLocal, separation, vertexEpsilon);
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return Fail(outcome);
                }
            }

            _localBounds = localBounds;
            _geometryLocalToWorld = geometryLocalToWorld;
            IsBuilt = true;
            return VpMultiCutBuildOutcome.Built;
        }

        private VpMultiCutBuildOutcome Fail(VpMultiCutBuildOutcome outcome)
        {
            IsBuilt = false;
            _branchCount = 0;
            _candidateCount = 0;
            _renderFragmentCount = 0;
            _conditionCount = 0;
            _capCount = 0;
            _capVertexCount = 0;
            return outcome;
        }

        // ----- branches ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The walk from the root, positive child before negative, each branch collected and selected as it is found.
        /// A retired fragment is checked for lying past an Ignored boundary, and otherwise drawn as nothing.
        /// </summary>
        private VpMultiCutBuildOutcome TryCollectBranches(
            LogicalCutLedger ledger, LogicalFragmentId root, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            int top = 0;
            _stack[top++] = root;
            while (top > 0)
            {
                LogicalFragmentId at = _stack[--top];
                if (!ledger.TryGetFragmentState(at, out LogicalFragmentState state))
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }

                switch (state)
                {
                    case LogicalFragmentState.Live:
                    {
                        if (ledger.TryGetActiveOperation(at, out CutOperationId pending)
                            && ledger.TryGetPreparedAnchorDistribution(pending, out _))
                        {
                            VpMultiCutBuildOutcome positive = TryAddBranch(ledger, at, 1f, reflected);
                            if (positive != VpMultiCutBuildOutcome.Built)
                            {
                                return positive;
                            }

                            VpMultiCutBuildOutcome negative = TryAddBranch(ledger, at, -1f, reflected);
                            if (negative != VpMultiCutBuildOutcome.Built)
                            {
                                return negative;
                            }
                        }
                        else
                        {
                            VpMultiCutBuildOutcome whole = TryAddBranch(ledger, at, 0f, reflected);
                            if (whole != VpMultiCutBuildOutcome.Built)
                            {
                                return whole;
                            }
                        }

                        break;
                    }

                    case LogicalFragmentState.Replaced:
                    {
                        if (!ledger.TryGetReplacingOperation(at, out CutOperationId replacing)
                            || !ledger.TryGetOperation(replacing, out LogicalCutOperation published)
                            || !published.positive.IsSet
                            || !published.negative.IsSet)
                        {
                            return VpMultiCutBuildOutcome.InvalidInput;
                        }

                        if (top + 2 > _stack.Length)
                        {
                            return VpMultiCutBuildOutcome.CapacityExceeded;
                        }

                        _stack[top++] = published.negative;
                        _stack[top++] = published.positive;
                        break;
                    }

                    default:
                    {
                        VpMultiCutBuildOutcome retired = CheckRetired(ledger, at, reflected);
                        if (retired != VpMultiCutBuildOutcome.Built)
                        {
                            return retired;
                        }

                        break;
                    }
                }
            }

            return VpMultiCutBuildOutcome.Built;
        }

        private VpMultiCutBuildOutcome TryAddBranch(
            LogicalCutLedger ledger, LogicalFragmentId fragment, float pendingSide, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            if (_branchCount >= _branches.Length)
            {
                return VpMultiCutBuildOutcome.CapacityExceeded;
            }

            VpMultiCutBuildOutcome collected = Collect(
                ledger, fragment, pendingSide, true, reflected, _candidates, _candidateCount, out int count);
            if (collected != VpMultiCutBuildOutcome.Built)
            {
                return collected;
            }

            int selected = VpClipCandidates.Select(_candidates, _candidateCount, count, _states);
            _branches[_branchCount++] = new VpMultiCutBranch(fragment, pendingSide, _candidateCount, count, selected, -1);
            _candidateCount += count;
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// A retired fragment is drawn as nothing -- unless it lies past an Ignored boundary, where the shape before
        /// that boundary would draw it back. Its chain is read into the check room, not into the candidates kept.
        /// </summary>
        private VpMultiCutBuildOutcome CheckRetired(
            LogicalCutLedger ledger, LogicalFragmentId fragment, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            VpMultiCutBuildOutcome collected = Collect(ledger, fragment, 0f, false, reflected, _checkCandidates, 0, out int count);
            if (collected != VpMultiCutBuildOutcome.Built)
            {
                return collected;
            }

            int selected = VpClipCandidates.Select(_checkCandidates, 0, count, _checkStates);
            return selected < count ? VpMultiCutBuildOutcome.RetiredInsideAggregate : VpMultiCutBuildOutcome.Built;
        }

        private VpMultiCutBuildOutcome Collect(
            LogicalCutLedger ledger, LogicalFragmentId fragment, float pendingSide, bool requireLive,
            IReadOnlyCollection<VpClipBoundary> reflected, VpClipCandidate[] into, int start, out int count)
        {
            switch (VpClipCandidates.CollectInto(
                        ledger, fragment, pendingSide, requireLive, reflected, _chain, into, start, into.Length - start,
                        out count))
            {
                case VpClipCandidates.CollectOutcome.Collected:
                    return VpMultiCutBuildOutcome.Built;
                case VpClipCandidates.CollectOutcome.NotCollectable:
                    return VpMultiCutBuildOutcome.InvalidInput;
                default:
                    return VpMultiCutBuildOutcome.CapacityExceeded;
            }
        }

        // ----- render fragments ---------------------------------------------------------------------------------------

        /// <summary>
        /// Branches that share the aggregation root and the selected prefix are one render fragment; a branch with nothing
        /// Ignored is its own. The walk puts one root's branches next to each other, so a render fragment met again after
        /// another one is a lineage this cannot read, and is refused. Each aggregation root's own chain must be the prefix.
        /// </summary>
        private VpMultiCutBuildOutcome TryGroup(LogicalCutLedger ledger, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            for (int b = 0; b < _branchCount; b++)
            {
                VpMultiCutBranch branch = _branches[b];
                VpMultiCutBuildOutcome rooted = TryRootOf(
                    ledger, branch, out LogicalFragmentId root, out float rootPendingSide, out bool aggregated);
                if (rooted != VpMultiCutBuildOutcome.Built)
                {
                    return rooted;
                }

                if (aggregated)
                {
                    bool joined = false;
                    for (int r = 0; r < _renderFragmentCount; r++)
                    {
                        VpMultiCutRenderFragment existing = _renderFragments[r];
                        if (!existing.aggregated || existing.root != root || !SamePrefix(_branches[existing.branchStart], branch))
                        {
                            continue;
                        }

                        if (r != _renderFragmentCount - 1)
                        {
                            return VpMultiCutBuildOutcome.InvalidInput;
                        }

                        _renderFragments[r] = WithBranches(existing, existing.branchCount + 1);
                        _branches[b] = WithRenderFragment(branch, r);
                        joined = true;
                        break;
                    }

                    if (joined)
                    {
                        continue;
                    }

                    VpMultiCutBuildOutcome consistent = CheckRootChain(ledger, root, branch, reflected);
                    if (consistent != VpMultiCutBuildOutcome.Built)
                    {
                        return consistent;
                    }
                }

                if (_renderFragmentCount >= _renderFragments.Length)
                {
                    return VpMultiCutBuildOutcome.CapacityExceeded;
                }

                _renderFragments[_renderFragmentCount] = new VpMultiCutRenderFragment(
                    root, rootPendingSide, aggregated, b, 1, 0, 0, Vector3.zero, VpInstanceClip.None, 0, 0);
                _branches[b] = WithRenderFragment(branch, _renderFragmentCount);
                _renderFragmentCount++;
            }

            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// What a branch is drawn as: itself (and its pending side) when nothing is Ignored, else the fragment the first
        /// Ignored boundary cut, whole.
        /// </summary>
        private VpMultiCutBuildOutcome TryRootOf(
            LogicalCutLedger ledger, in VpMultiCutBranch branch, out LogicalFragmentId root, out float rootPendingSide,
            out bool aggregated)
        {
            root = branch.fragment;
            rootPendingSide = branch.pendingSide;
            aggregated = branch.selectedCount < branch.candidateCount;
            if (!aggregated)
            {
                return VpMultiCutBuildOutcome.Built;
            }

            VpClipCandidate firstIgnored = _candidates[branch.candidateStart + branch.selectedCount];
            if (!ledger.TryGetOperation(firstIgnored.boundary.face.operation, out LogicalCutOperation cut))
            {
                return VpMultiCutBuildOutcome.InvalidInput;
            }

            root = cut.source;
            rootPendingSide = 0f;
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// The aggregation root's own chain of unreflected boundaries, read into the check room and not kept, must be
        /// exactly the branch's selected prefix: those are the conditions the root is drawn under.
        /// </summary>
        private VpMultiCutBuildOutcome CheckRootChain(
            LogicalCutLedger ledger, LogicalFragmentId root, in VpMultiCutBranch branch, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            VpMultiCutBuildOutcome collected = Collect(ledger, root, 0f, false, reflected, _checkCandidates, 0, out int count);
            if (collected != VpMultiCutBuildOutcome.Built)
            {
                return collected;
            }

            if (count != branch.selectedCount)
            {
                return VpMultiCutBuildOutcome.InvalidInput;
            }

            for (int i = 0; i < count; i++)
            {
                if (_checkCandidates[i].boundary != _candidates[branch.candidateStart + i].boundary)
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }
            }

            return VpMultiCutBuildOutcome.Built;
        }

        private bool SamePrefix(in VpMultiCutBranch a, in VpMultiCutBranch b)
        {
            if (a.selectedCount != b.selectedCount)
            {
                return false;
            }

            for (int i = 0; i < a.selectedCount; i++)
            {
                if (_candidates[a.candidateStart + i].boundary != _candidates[b.candidateStart + i].boundary)
                {
                    return false;
                }
            }

            return true;
        }

        private VpMultiCutBuildOutcome TryBuildRenderFragment(
            LogicalCutLedger ledger,
            LogicalFragmentId registrationRoot,
            int index,
            Bounds localBounds,
            Matrix4x4 geometryLocalToWorld,
            Matrix4x4 lineageToGeometryLocal,
            float separation,
            float vertexEpsilon)
        {
            VpMultiCutRenderFragment renderFragment = _renderFragments[index];
            VpMultiCutBranch representative = _branches[renderFragment.branchStart];

            // 1. The separation, down the lineage from the registration's root.
            VpMultiCutBuildOutcome summed = TrySumOffset(
                ledger, registrationRoot, renderFragment.root, renderFragment.rootPendingSide, geometryLocalToWorld,
                lineageToGeometryLocal, separation, out Vector3 offset);
            if (summed != VpMultiCutBuildOutcome.Built)
            {
                return summed;
            }

            if (!IsFinite(offset))
            {
                return VpMultiCutBuildOutcome.InvalidInput;
            }

            // 2. The selected boundaries: the geometry's own plane, the world plane, the condition and the half-space.
            int selected = representative.selectedCount;
            if (_conditionCount + selected > _conditions.Length || _capCount + selected > _caps.Length)
            {
                return VpMultiCutBuildOutcome.CapacityExceeded;
            }

            int conditionStart = _conditionCount;
            for (int j = 0; j < selected; j++)
            {
                VpClipCandidate candidate = _candidates[representative.candidateStart + j];
                if (!VpCutPlane.TryGeometryLocalToWorld(candidate.plane, lineageToGeometryLocal, out float4 local)
                    || !VpCutPlane.TryGeometryLocalToWorld(local, geometryLocalToWorld, out float4 world))
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }

                _localPlanes[j] = local;
                _worldPlanes[j] = world;
                _halfSpaces[j] = new VpClipHalfSpace(ToVector4(world), candidate.boundary.side);
                _conditions[_conditionCount++] = new VpCapConstraint(
                    candidate.boundary.face, candidate.boundary.side, ToVector4(world));
            }

            _selectedHalfSpaces.Set(0, selected);
            if (!VpInstanceClip.TryKeep(_selectedHalfSpaces, offset, out VpInstanceClip clip))
            {
                return VpMultiCutBuildOutcome.InvalidInput;
            }

            // 3. One cap per selected boundary, cut by the other selected half-spaces.
            _selectedCandidates.Set(representative.candidateStart, selected);
            _selectedStates.Set(representative.candidateStart, selected);
            _selectedPlanes.Set(0, selected);
            int capStart = _capCount;
            for (int j = 0; j < selected; j++)
            {
                VpClipBoundary boundary = _candidates[representative.candidateStart + j].boundary;
                if (!_section.TryBuild(
                        localBounds, _localPlanes[j], geometryLocalToWorld, vertexEpsilon, _initial, 0, out int initial, out _))
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }

                // The negative side keeps the order the section was built in; the positive side reads it backwards,
                // its outward direction being the opposite one.
                if (boundary.side > 0f)
                {
                    Array.Reverse(_initial, 0, initial);
                }

                int clipped = 0;
                if (initial > 0
                    && !VpCapPolygonClip.TryClip(
                        _initial, initial, boundary, _selectedCandidates, _selectedStates, _selectedPlanes,
                        vertexEpsilon, _clipped, out clipped))
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }

                for (int v = 0; v < clipped; v++)
                {
                    // Finite inputs can still overflow when added; such a vertex is refused, never dropped or emptied.
                    Vector3 placed = _clipped[v] + offset;
                    if (!IsFinite(placed))
                    {
                        return VpMultiCutBuildOutcome.InvalidInput;
                    }

                    _capVertices[_capVertexCount + v] = placed;
                }

                float4 plane = _worldPlanes[j];
                var normal = new Vector3(plane.x, plane.y, plane.z);
                _caps[_capCount++] = new VpMultiCutCap(
                    index, boundary, plane, -boundary.side * normal, _capVertexCount, initial, clipped);
                _capVertexCount += clipped;
            }

            _renderFragments[index] = new VpMultiCutRenderFragment(
                renderFragment.root, renderFragment.rootPendingSide, renderFragment.aggregated,
                renderFragment.branchStart, renderFragment.branchCount, conditionStart, selected, offset, clip,
                capStart, selected);
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// Each cut from the registration's root down to <paramref name="root"/> (and its pending side, if drawn as one)
        /// adds its free side's separation, freedom read from the cut's own settled distribution.
        /// </summary>
        private static VpMultiCutBuildOutcome TrySumOffset(
            LogicalCutLedger ledger,
            LogicalFragmentId registrationRoot,
            LogicalFragmentId root,
            float rootPendingSide,
            Matrix4x4 geometryLocalToWorld,
            Matrix4x4 lineageToGeometryLocal,
            float separation,
            out Vector3 offset)
        {
            offset = Vector3.zero;
            if (rootPendingSide != 0f)
            {
                if (!ledger.TryGetActiveOperation(root, out CutOperationId pending))
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }

                VpMultiCutBuildOutcome added = TryAdd(
                    ledger, pending, rootPendingSide, geometryLocalToWorld, lineageToGeometryLocal, separation, ref offset);
                if (added != VpMultiCutBuildOutcome.Built)
                {
                    return added;
                }
            }

            LogicalFragmentId at = root;
            while (at != registrationRoot)
            {
                if (!ledger.TryGetOrigin(at, out CutOperationId origin, out float side)
                    || !ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return VpMultiCutBuildOutcome.InvalidInput;
                }

                VpMultiCutBuildOutcome added = TryAdd(
                    ledger, origin, side, geometryLocalToWorld, lineageToGeometryLocal, separation, ref offset);
                if (added != VpMultiCutBuildOutcome.Built)
                {
                    return added;
                }

                at = cut.source;
            }

            return VpMultiCutBuildOutcome.Built;
        }

        private static VpMultiCutBuildOutcome TryAdd(
            LogicalCutLedger ledger,
            CutOperationId operation,
            float side,
            Matrix4x4 geometryLocalToWorld,
            Matrix4x4 lineageToGeometryLocal,
            float separation,
            ref Vector3 offset)
        {
            if (!ledger.TryGetSettledAnchorDistribution(operation, out AnchorDistributionResult distribution))
            {
                return VpMultiCutBuildOutcome.UnsettledDistribution;
            }

            bool free = !FixedSupportAnchors.IsFixed(side > 0f ? distribution.positiveCount : distribution.negativeCount);
            if (!free)
            {
                return VpMultiCutBuildOutcome.Built;
            }

            if (!ledger.TryGetOperation(operation, out LogicalCutOperation cut)
                || !VpCutPlane.TryGeometryLocalToWorld(cut.plane, lineageToGeometryLocal, out float4 local)
                || !VpCutPlane.TryGeometryLocalToWorld(local, geometryLocalToWorld, out float4 world))
            {
                return VpMultiCutBuildOutcome.InvalidInput;
            }

            offset += side * new Vector3(world.x, world.y, world.z) * separation;
            return IsFinite(offset) ? VpMultiCutBuildOutcome.Built : VpMultiCutBuildOutcome.InvalidInput;
        }

        private static VpMultiCutRenderFragment WithBranches(in VpMultiCutRenderFragment r, int branchCount)
        {
            return new VpMultiCutRenderFragment(
                r.root, r.rootPendingSide, r.aggregated, r.branchStart, branchCount, r.conditionStart, r.conditionCount,
                r.offset, r.clip, r.capStart, r.capCount);
        }

        private static VpMultiCutBranch WithRenderFragment(in VpMultiCutBranch b, int renderFragment)
        {
            return new VpMultiCutBranch(
                b.fragment, b.pendingSide, b.candidateStart, b.candidateCount, b.selectedCount, renderFragment);
        }

        private static Vector4 ToVector4(float4 value)
        {
            return new Vector4(value.x, value.y, value.z, value.w);
        }

        private static bool IsWithinContract(Bounds bounds)
        {
            Vector3 extents = bounds.extents;
            return IsFinite(bounds.center) && IsFinite(extents) && extents.x >= 0f && extents.y >= 0f && extents.z >= 0f;
        }

        /// <summary>Finite, affine (last row exactly 0, 0, 0, 1) and invertible.</summary>
        private static bool IsPlacement(Matrix4x4 m)
        {
            if (!IsFiniteMatrix(m) || m.m30 != 0f || m.m31 != 0f || m.m32 != 0f || m.m33 != 1f)
            {
                return false;
            }

            float determinant = m.determinant;
            return !float.IsNaN(determinant) && !float.IsInfinity(determinant) && determinant != 0f && IsFiniteMatrix(m.inverse);
        }

        /// <summary>Finite, affine, and a rotation (orthonormal columns within 1e-4, determinant +1) with a translation.</summary>
        private static bool IsRigid(Matrix4x4 m)
        {
            if (!IsPlacement(m))
            {
                return false;
            }

            const float tolerance = 1e-4f;
            Vector3 x = m.GetColumn(0);
            Vector3 y = m.GetColumn(1);
            Vector3 z = m.GetColumn(2);
            return Math.Abs(x.sqrMagnitude - 1f) <= tolerance && Math.Abs(y.sqrMagnitude - 1f) <= tolerance
                && Math.Abs(z.sqrMagnitude - 1f) <= tolerance && Math.Abs(Vector3.Dot(x, y)) <= tolerance
                && Math.Abs(Vector3.Dot(y, z)) <= tolerance && Math.Abs(Vector3.Dot(z, x)) <= tolerance
                && Math.Abs(m.determinant - 1f) <= tolerance;
        }

        private static bool IsFiniteMatrix(Matrix4x4 m)
        {
            for (int i = 0; i < 16; i++)
            {
                if (float.IsNaN(m[i]) || float.IsInfinity(m[i]))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsFiniteNonNegative(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        /// <summary>A read-only look at part of an array this snapshot owns, for the calls that take lists.</summary>
        private sealed class RangeList<T> : IReadOnlyList<T>
        {
            private readonly T[] _items;
            private int _start;
            private int _count;

            public RangeList(T[] items)
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

                    return _items[_start + index];
                }
            }

            public void Set(int start, int count)
            {
                _start = start;
                _count = count;
            }

            public IEnumerator<T> GetEnumerator()
            {
                for (int i = 0; i < _count; i++)
                {
                    yield return _items[_start + i];
                }
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }
}
