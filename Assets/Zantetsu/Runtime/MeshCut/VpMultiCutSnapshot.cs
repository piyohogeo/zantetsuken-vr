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

        /// <summary>
        /// A retired fragment lies past an Ignored boundary, inside what would be drawn once as the shape before it. The
        /// aggregation alone cannot say whether that shape may still be shown, so nothing is guessed and nothing is built.
        /// This is **not** a statement that a previously adopted snapshot is safe to keep drawing -- it may show the
        /// retired region too. It is not a capacity shortfall: it is found before any room is taken, and
        /// <see cref="VpLogicalCutDisplay"/> stops drawing on it rather than keeping its previous snapshot.
        /// </summary>
        RetiredInsideAggregate = 4,
    }

    /// <summary>
    /// What made a build <see cref="VpMultiCutBuildOutcome.InvalidInput"/>. The conservative numeric check before the walk
    /// and a value that really came out not finite in what would be drawn are different reasons and are never merged.
    /// </summary>
    public enum VpMultiCutInvalidInput
    {
        /// <summary>The last build was not refused as invalid input.</summary>
        None = 0,

        /// <summary>A box, a placement or a mapping outside the input contract.</summary>
        InputContract = 1,

        /// <summary>
        /// The lineage cannot be read as given: an unknown fragment, a root on another's lineage, a walk that is not a
        /// tree, an aggregation root whose chain is not the prefix.
        /// </summary>
        Lineage = 2,

        /// <summary>A plane the mapping or the placement cannot carry.</summary>
        PlaneNotCarried = 3,

        /// <summary>A box-and-plane section could not be taken.</summary>
        SectionNotTaken = 4,

        /// <summary>A clip record or a clipped cap could not be made from finite input.</summary>
        ClipNotTaken = 5,

        /// <summary>
        /// The conservative check before the walk could not establish that a box-and-plane section can be computed in
        /// float: the box's width on an axis, the square of the vertex epsilon, a corner's distance to a plane and the
        /// difference of two such distances, or the placed section's coordinates and the sums its ordering takes, could
        /// pass a float -- whether or not that section is drawn in fact.
        /// </summary>
        ConservativeSection = 10,

        /// <summary>A cap vertex, as built, came out not finite. Kept as a defence after the check above.</summary>
        DrawnCapVertex = 9,
    }

    /// <summary>
    /// Bounds, in double, on what taking a box-and-plane section (<see cref="VpCapBoundsPolygon.TryBuild"/>), clipping it
    /// (<see cref="VpCapPolygonClip"/>) and moving it by an offset can produce in float, so that a conservative check can
    /// refuse, before any section is taken, input whose arithmetic could pass a float. Each answer is "within the bound"
    /// or not; nothing is corrected.
    /// <para>
    /// **Error model, and when it applies.** Every float operation here rounds to nearest: for a result z of an operation
    /// on floats, the computed value is within <c>u|z| + eta</c> of z, u = 2^-24, where <c>eta</c> covers results in or
    /// below the subnormal range -- 2^-150 with gradual underflow, 2^-126 if subnormals are flushed to zero; the bounds use
    /// <see cref="FloatSlack"/> = 1e-30, far above both, per stage. The relative part is used only for magnitudes: a
    /// chain of at most 16 operations grows a magnitude by at most <c>(1 + u)^16 &lt; </c><see cref="FloatGrowth"/>
    /// = 1 + 2^-18, and the absolute part is added as <see cref="FloatSlack"/>. Nothing here relies on a small value being
    /// accurate: underflow can change the shape of a section, which is not this check's concern, but it cannot make a
    /// bound too small. The bounds are computed in double, each of at most a few dozen operations of relative error
    /// 2^-53 on values far below the double range, and then multiplied by <see cref="DoubleGrowth"/> = 1 + 2^-40, so that
    /// the double arithmetic does not make them too small either. A condition near its limit is answered "not within",
    /// never widened.
    /// </para>
    /// <para>
    /// **What is bounded, following the section's own steps.**
    /// (1) The corners are <c>localBounds.min</c> and <c>max</c>, read as the section reads them, so their magnitudes L on
    /// each axis are exact. (2) The plane is normalized again by the section, by its float length; for a normal whose
    /// length is at least 2^-60 that length is at least <c>|n|(1 - 2^-18)</c>, which bounds the normalized components and
    /// the offset term. (3) A corner's distance, three products and three sums, is at most
    /// <c>D = (sum |n_j| L_j + |w|) growth + slack</c>; two of opposite sign differ by at most 2D, so 2D within a float
    /// keeps the difference finite. (4) A crossing point <c>a + (b - a) s</c> has s in [0, 1] -- the difference of
    /// distances of opposite signs is at least the numerator in magnitude, and rounding is monotone -- and b - a is one
    /// axis of the box, whose width within a float keeps it finite; the point is then within L grown by the chain, on
    /// every axis. (5) Placed by <c>MultiplyPoint3x4</c>, three products and three sums per axis, a vertex is at most
    /// <c>W_i = (sum_j |m_ij| L'_j + |t_i|) growth + slack</c>. (6) Ordering sums at most six vertices and takes dots of
    /// their differences with unit axes and a cross of them, all within 12 W. (7) The epsilon is squared once. (8) A
    /// clipped vertex is <c>a + (b - a) t</c> in double with t in [0, 1] from float vertices, rounded to float: its
    /// magnitude on an axis cannot pass the larger of a and b by more than a double's error, far below half a float's
    /// spacing there, so it stays within W_i -- and a cap vertex is that clipped vertex, nothing being added to it.
    /// </para>
    /// </summary>
    internal static class VpSectionBounds
    {
        /// <summary>At least <c>(1 + 2^-24)^16</c>: the relative growth of a magnitude through a chain of float operations.</summary>
        public const double FloatGrowth = 1.0 + (1.0 / (1 << 18));

        /// <summary>Far above the absolute error of a chain of float operations near or below the subnormal range.</summary>
        public const double FloatSlack = 1e-30;

        /// <summary>At least the relative error of this check's own double arithmetic.</summary>
        public const double DoubleGrowth = 1.0 + (1.0 / (1L << 40));

        private const double FloatMax = float.MaxValue;

        /// <summary>The least normal length the relative model is used for; a shorter one is not within.</summary>
        private const double LeastNormalLength = 1.0 / (1L << 60);

        /// <summary>
        /// Whether a section of <paramref name="localBounds"/>, placed by <paramref name="geometryLocalToWorld"/>, with
        /// <paramref name="vertexEpsilon"/>, stays within float in the steps that do not depend on the plane -- the width,
        /// the epsilon squared, the placed coordinates and the ordering -- and, if so, the bound on every placed section or
        /// clipped cap vertex's coordinate on each axis.
        /// </summary>
        public static bool TryPlacedExtent(
            Bounds localBounds, Matrix4x4 geometryLocalToWorld, float vertexEpsilon, out double3 extent)
        {
            extent = default;
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            if (!IsFinite(min) || !IsFinite(max) || float.IsNaN(vertexEpsilon) || float.IsInfinity(vertexEpsilon))
            {
                return false;
            }

            // (7) The epsilon squared, once.
            if ((double)vertexEpsilon * vertexEpsilon * DoubleGrowth * FloatGrowth > FloatMax)
            {
                return false;
            }

            var magnitude = new double3(
                Math.Max(Math.Abs((double)min.x), Math.Abs((double)max.x)),
                Math.Max(Math.Abs((double)min.y), Math.Abs((double)max.y)),
                Math.Max(Math.Abs((double)min.z), Math.Abs((double)max.z)));

            // (4) One axis of the box, b - a, within a float; and the crossing point within the grown magnitude.
            for (int j = 0; j < 3; j++)
            {
                if (((double)max[j] - min[j]) * DoubleGrowth * FloatGrowth > FloatMax)
                {
                    return false;
                }
            }

            double3 local = (magnitude * FloatGrowth) + FloatSlack;

            // (5) Placed, row by row.
            var placed = new double3(
                Row(geometryLocalToWorld.m00, geometryLocalToWorld.m01, geometryLocalToWorld.m02, geometryLocalToWorld.m03, local),
                Row(geometryLocalToWorld.m10, geometryLocalToWorld.m11, geometryLocalToWorld.m12, geometryLocalToWorld.m13, local),
                Row(geometryLocalToWorld.m20, geometryLocalToWorld.m21, geometryLocalToWorld.m22, geometryLocalToWorld.m23, local));

            // (6) The ordering's sums and dots, within 12 W on every axis, grown once more.
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(placed[i]) || 12.0 * placed[i] * FloatGrowth * FloatGrowth * DoubleGrowth > FloatMax)
                {
                    return false;
                }
            }

            extent = placed;
            return true;
        }

        /// <summary>
        /// Whether a corner's distance to <paramref name="localPlane"/>, and the difference of two, stay within float for
        /// the section of <paramref name="localBounds"/> -- after the section normalizes the plane again by its float length.
        /// </summary>
        public static bool IsPlaneWithin(float4 localPlane, Bounds localBounds)
        {
            Vector3 min = localBounds.min;
            Vector3 max = localBounds.max;
            if (!IsFinite(min) || !IsFinite(max) || !math.all(math.isfinite(localPlane)))
            {
                return false;
            }

            double nx = Math.Abs((double)localPlane.x);
            double ny = Math.Abs((double)localPlane.y);
            double nz = Math.Abs((double)localPlane.z);
            double length = Math.Sqrt((nx * nx) + (ny * ny) + (nz * nz));
            if (!(length >= LeastNormalLength))
            {
                return false;
            }

            // (2) The components as the section normalizes them, each within its float division.
            double inverse = 1.0 / (length * (1.0 - (1.0 / (1 << 18))));
            double w = (Math.Abs((double)localPlane.w) * inverse * FloatGrowth) + FloatSlack;
            double sum =
                (((nx * inverse * FloatGrowth) + FloatSlack) * Math.Max(Math.Abs((double)min.x), Math.Abs((double)max.x)))
                + (((ny * inverse * FloatGrowth) + FloatSlack) * Math.Max(Math.Abs((double)min.y), Math.Abs((double)max.y)))
                + (((nz * inverse * FloatGrowth) + FloatSlack) * Math.Max(Math.Abs((double)min.z), Math.Abs((double)max.z)));

            // (3) The distance, and the difference of two of opposite sign, within a float.
            double distance = (((sum + w) * FloatGrowth) + FloatSlack) * DoubleGrowth;
            return 2.0 * distance * FloatGrowth <= FloatMax;
        }

        private static double Row(float a, float b, float c, float t, double3 local)
        {
            double row = (Math.Abs((double)a) * local.x) + (Math.Abs((double)b) * local.y) + (Math.Abs((double)c) * local.z)
                + Math.Abs((double)t);
            return (((row * FloatGrowth) + FloatSlack) * DoubleGrowth);
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x) && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
    }

    /// <summary>
    /// One registration a snapshot is built for: a root fragment and the geometry it is drawn with. Every field is the
    /// caller's statement and is required; nothing here is filled in or guessed.
    /// </summary>
    public readonly struct VpMultiCutRegistration
    {
        /// <param name="root">The fragment the geometry was registered for.</param>
        /// <param name="localBounds">The source geometry's box, in its own coordinates.</param>
        /// <param name="geometryLocalToWorld">The placement this geometry is drawn at.</param>
        /// <param name="lineageToGeometryLocal">
        /// The one mapping from the lineage's common logical frame to the geometry's coordinates. Identity is a statement
        /// too, and is never assumed.
        /// </param>
        /// <param name="reflected">The boundaries the geometry already reflects. Required: null is not "none".</param>
        /// <param name="vertexEpsilon">The cap polygons' vertex-merge epsilon for this geometry.</param>
        public VpMultiCutRegistration(
            LogicalFragmentId root,
            Bounds localBounds,
            Matrix4x4 geometryLocalToWorld,
            Matrix4x4 lineageToGeometryLocal,
            IReadOnlyCollection<VpClipBoundary> reflected,
            float vertexEpsilon)
        {
            this.root = root;
            this.localBounds = localBounds;
            this.geometryLocalToWorld = geometryLocalToWorld;
            this.lineageToGeometryLocal = lineageToGeometryLocal;
            this.reflected = reflected;
            this.vertexEpsilon = vertexEpsilon;
        }

        public readonly LogicalFragmentId root;
        public readonly Bounds localBounds;
        public readonly Matrix4x4 geometryLocalToWorld;

        public readonly Matrix4x4 lineageToGeometryLocal;
        public readonly IReadOnlyCollection<VpClipBoundary> reflected;
        public readonly float vertexEpsilon;
    }

    /// <summary>
    /// One logical branch: a live fragment drawn whole (<see cref="pendingSide"/> 0), or one side of a live fragment's
    /// displayable pending cut (+1 or -1, with no child id -- none is issued before publication). Its candidates, in the
    /// ledger's admission order, and their selection are its own.
    /// </summary>
    public readonly struct VpMultiCutBranch
    {
        internal VpMultiCutBranch(
            int registration, LogicalFragmentId fragment, float pendingSide, int candidateStart, int candidateCount,
            int selectedCount, int renderFragment)
        {
            this.registration = registration;
            this.fragment = fragment;
            this.pendingSide = pendingSide;
            this.candidateStart = candidateStart;
            this.candidateCount = candidateCount;
            this.selectedCount = selectedCount;
            this.renderFragment = renderFragment;
        }

        /// <summary>The registration it belongs to: its index in the list the snapshot was built from.</summary>
        public readonly int registration;

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
            int registration, Bounds localBounds, Matrix4x4 geometryLocalToWorld, LogicalFragmentId root,
            float rootPendingSide, bool aggregated, int branchStart, int branchCount, int conditionStart,
            int conditionCount, VpInstanceClip clip, int capStart, int capCount)
        {
            this.registration = registration;
            this.localBounds = localBounds;
            this.geometryLocalToWorld = geometryLocalToWorld;
            this.root = root;
            this.rootPendingSide = rootPendingSide;
            this.aggregated = aggregated;
            this.branchStart = branchStart;
            this.branchCount = branchCount;
            this.conditionStart = conditionStart;
            this.conditionCount = conditionCount;
            this.clip = clip;
            this.capStart = capStart;
            this.capCount = capCount;
        }

        /// <summary>The registration it is drawn for: its index in the list the snapshot was built from.</summary>
        public readonly int registration;

        /// <summary>That registration's box, in the geometry's own coordinates.</summary>
        public readonly Bounds localBounds;

        /// <summary>That registration's placement, which is where its shapes are drawn.</summary>
        public readonly Matrix4x4 geometryLocalToWorld;

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
        /// rule makes the selected ones, with each boundary's current world plane.
        /// </summary>
        public readonly int conditionStart;
        public readonly int conditionCount;

        /// <summary>The selected half-spaces in world space.</summary>
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

        /// <summary>The boundary's plane in world space.</summary>
        public readonly float4 worldPlane;

        /// <summary>The outward normal of the side the cap closes: <c>-side * n</c>.</summary>
        public readonly Vector3 outwardNormal;

        /// <summary>Where its vertices are: world space, at the render fragment's own placement.</summary>
        public readonly int vertexStart;

        /// <summary>The vertices of the box-and-plane section before the other planes cut it: 0 to 6.</summary>
        public readonly int initialVertexCount;

        /// <summary>The vertices after: 0 to 14.</summary>
        public readonly int vertexCount;
    }

    /// <summary>
    /// The display snapshot of every registration's lineage under several cuts, on the CPU (DESIGN 5.1, 5.2, 5.6; D-180,
    /// D-181). It builds what a display adopts -- <see cref="VpLogicalCutDisplay"/> draws from it -- and nothing more.
    /// <para>
    /// **Input.** The ledger and the registrations (<see cref="VpMultiCutRegistration"/>), each with its
    /// root fragment; the source geometry's box (its own coordinates) and placement; the one mapping from the lineage's
    /// common logical frame -- the frame every adopted plane of this lineage is given in -- to the geometry's
    /// coordinates, which the caller states and nothing here assumes; the boundaries the geometry already reflects
    /// (required: an empty set says "none"); and the vertex epsilon. Lineages whose children have frames of their own
    /// are not this input. No registration's root may lie on another's lineage, as an ancestor or a descendant.
    /// </para>
    /// <para>
    /// **Input contract, checked first** -- for every registration, also when there is no candidate and so no plane to
    /// convert. The box: finite centre and extents, extents not negative. The placement: finite, affine (last row exactly
    /// 0, 0, 0, 1) and invertible. The mapping: finite, affine and rigid -- a rotation (orthonormal columns within 1e-4,
    /// determinant +1) and a translation, nothing else. Anything else is <see cref="VpMultiCutBuildOutcome.InvalidInput"/>;
    /// no projective or scaling mapping is taken on.
    /// </para>
    /// <para>
    /// **What is decided before any room is taken.** After the contract and before the walk, the ledger is read against
    /// the registrations with no array of this snapshot's: that no root is on another's lineage; that a retired fragment
    /// of a lineage does not lie past an Ignored boundary (<see cref="VpMultiCutBuildOutcome.RetiredInsideAggregate"/>);
    /// that every published cut of a lineage has a settled distribution; and that the plane of every cut of a lineage
    /// that is drawn -- published, or pending and prepared -- and of every unreflected boundary above a root, goes through
    /// the mapping and the placement. A plane is checked here whether or not the build would come to need it.
    /// </para>
    /// <para>
    /// **The numeric check, conservative by contract.** Also before the walk and whatever the room, from bounds and never by
    /// taking a section (<see cref="VpSectionBounds"/>): for each registration, that a section of its box can be computed in
    /// float at all -- the box's width, the vertex epsilon squared, the placed box's coordinates and the sums the ordering
    /// takes -- giving a bound W on every cap vertex's placed coordinates on each axis; and, for every plane of the lineage
    /// checked above, that a corner's distance to it and the difference of two such distances stay finite. Anything else is
    /// refused as <see cref="VpMultiCutInvalidInput.ConservativeSection"/>. A cap vertex is decided by W alone: nothing is
    /// added to one after it is clipped. This is stricter than the build -- the whole box stands for
    /// every section of it, and a plane that is not drawn in fact can refuse the input too -- and it is kept
    /// apart from a value that really came out not finite in the build, which the build still checks. Nothing it computes
    /// is drawn, and no section is taken for it. Lineages not registered are not read.
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
    /// **Where a shape stands.** At the placement it is given, and nowhere else. A render fragment is drawn with the
    /// placement its fragment follows, or the registration's when it follows nothing, and this adds nothing to it:
    /// there is no displacement of a side for the display, so two sides of a cut whose owners are at one place are at
    /// one place. What separates them in the product is the physics, through the owners the placements come from.
    /// </para>
    /// <para>
    /// **Spaces.** Candidate planes are the ledger's, in the lineage's frame. Conditions, clip planes, cap planes and
    /// cap vertices are world space; the box is the geometry's own, placed by the placement.
    /// </para>
    /// <para>
    /// **Sections are taken once (DESIGN 5.7), for the caps drawn only.** The box-and-plane section a cap starts from depends
    /// only on the face, the plane in the geometry's coordinates, the box, the placement and the epsilon -- not on the
    /// side or the other planes. It is taken once per build for each such key, shared by both sides and
    /// every render fragment, and taken from the snapshot given as the one to reuse from when that one holds the same key
    /// exactly (compared value by value, not within a tolerance). Only a selected boundary's cap asks for one. Each cap
    /// adds at most one key and the caps are refused for room before any section is asked for, so the room -- one section
    /// per cap of the capacity -- is never short; were it short all the same, the build is refused.
    /// <see cref="SectionBuildCount"/> counts the sections actually taken.
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

        // The sections taken in this build, one per key, and their vertices: at most one per cap.
        private readonly Section[] _sections;
        private readonly Vector3[] _sectionVertices;
        private readonly VpMultiCutRegistration[] _single = new VpMultiCutRegistration[1];

        // Which section each cap was built from: the slot in _sections, so that the section kept for a drawn cap can be
        // read as it is (InitialSection) without being taken again.
        private readonly int[] _capSection;
        private int _registrationCount;
        private long _buildGeneration;

        private int _branchCount;
        private int _candidateCount;
        private int _renderFragmentCount;
        private int _conditionCount;
        private int _capCount;
        private int _capVertexCount;
        private int _sectionCount;
        private VpMultiCutInvalidInput _invalid;
        // Each registration's bound on placed cap coordinates, found by the check before the walk.
        /// <summary>What one section was taken of, and what it came to, in the geometry's coordinates placed in world.</summary>
        private struct Section
        {
            public VpCapFace face;
            public float4 localPlane;
            public Bounds bounds;
            public Matrix4x4 placement;
            public float epsilon;
            public int vertexCount;
        }

        /// <summary>
        /// Makes the room. Every derived size -- the cap vertices, the sections, the walk -- is worked out in 64-bit
        /// arithmetic and must be an int.
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
            long sectionVertices = (long)capacities.caps * VpCapBoundsPolygon.MaxVertices;
            long stack = ((long)capacities.branches * 2) + 2;
            if (capVertices > int.MaxValue || sectionVertices > int.MaxValue || stack > int.MaxValue)
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
            _sections = new Section[capacities.caps];
            _sectionVertices = new Vector3[(int)sectionVertices];
            _capSection = new int[capacities.caps];
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

        /// <summary>
        /// How many box-and-plane sections the last build actually took -- not reused from this build's own earlier caps
        /// or from the snapshot it was told to reuse from. Counted whether or not that build succeeded.
        /// </summary>
        public int SectionBuildCount { get; private set; }


        /// <summary>What made the last build <see cref="VpMultiCutBuildOutcome.InvalidInput"/>; <see cref="VpMultiCutInvalidInput.None"/> otherwise.</summary>
        public VpMultiCutInvalidInput InvalidInputReason => _invalid;

        /// <summary>
        /// This snapshot's own cap vertex array, for an upload that reads the first <see cref="CapVertexCount"/> of them
        /// by count. Not copied; good only until this snapshot is built again.
        /// </summary>
        internal Vector3[] CapVertexArray => _capVertices;

        /// <summary>
        /// A look at one cap's vertices (world space) in this snapshot's own array: nothing is copied,
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
        /// A look at the section one cap started from, as this snapshot keeps it: the box-and-plane section of the cap's
        /// face through its registration's box (at most <see cref="VpCapBoundsPolygon.MaxVertices"/>, six), before the
        /// other selected half-spaces cut it -- in world space, at the render fragment's own placement, which a reader
        /// needs to add nothing to. It is the section taken or reused for the cap in the build, not a copy
        /// and not the build's working room; nothing is taken here. Good only until this snapshot is built again
        /// (<see cref="BuildGeneration"/>). A section of no vertices is a plane that missed the box.
        /// </summary>
        internal VpArrayRange<Vector3> InitialSection(int capIndex)
        {
            if (!TryGetCap(capIndex, out VpMultiCutCap cap))
            {
                throw new ArgumentOutOfRangeException(nameof(capIndex));
            }

            int slot = _capSection[capIndex];
            return new VpArrayRange<Vector3>(
                _sectionVertices, slot * VpCapBoundsPolygon.MaxVertices, _sections[slot].vertexCount);
        }

        /// <summary>How many registrations the last successful build was given, in the order given; 0 otherwise.</summary>
        public int RegistrationCount => IsBuilt ? _registrationCount : 0;

        /// <summary>
        /// Counts the builds this snapshot was asked for, successful or not. A reader that keeps an index into this
        /// snapshot, or a look at it, keeps it only while this value is unchanged.
        /// </summary>
        internal long BuildGeneration => _buildGeneration;

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

        /// <summary>One vertex of one cap, in world space.</summary>
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
        /// Builds this snapshot from one registration's lineage: the same build as for a list of registrations, with this
        /// one alone in it. See the class notes for the input, the spaces and the adoption rule. Anything but
        /// <see cref="VpMultiCutBuildOutcome.Built"/> leaves nothing readable here, and the ledger and every input exactly
        /// as they were.
        /// </summary>
        /// <param name="lineageToGeometryLocal">
        /// The caller's statement: every adopted plane of this lineage is in one logical frame, and this takes that frame
        /// to the geometry's coordinates. Required; identity is a statement too, and is never assumed.
        /// </param>
        /// <param name="reflected">The boundaries the geometry already reflects. Required: null is not "none".</param>
        /// <exception cref="ArgumentNullException">The ledger or the reflected set is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">The epsilon is negative or not finite.</exception>
        public VpMultiCutBuildOutcome TryBuild(
            LogicalCutLedger ledger,
            LogicalFragmentId root,
            Bounds localBounds,
            Matrix4x4 geometryLocalToWorld,
            Matrix4x4 lineageToGeometryLocal,
            IReadOnlyCollection<VpClipBoundary> reflected,
            float vertexEpsilon,
            IVpFragmentPlacement placement = null)
        {
            if (reflected == null)
            {
                throw new ArgumentNullException(nameof(reflected), "what the geometry reflects must be said, even if it is nothing");
            }

            _single[0] = new VpMultiCutRegistration(
                root, localBounds, geometryLocalToWorld, lineageToGeometryLocal, reflected, vertexEpsilon);
            try
            {
                return TryBuild(ledger, _single, null, placement);
            }
            finally
            {
                // The caller's reflected set is not held past the call.
                _single[0] = default;
            }
        }

        /// <summary>
        /// Builds this snapshot from every registration's lineage together. Nothing of it is readable unless every one of
        /// them was built; see the class notes for the input, what is decided before any room is taken, the spaces and
        /// the adoption rule.
        /// </summary>
        /// <exception cref="ArgumentNullException">The ledger, the list or a registration's reflected set is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">An epsilon is negative or not finite.</exception>
        public VpMultiCutBuildOutcome TryBuild(
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations,
            IVpFragmentPlacement placement = null)
        {
            return TryBuild(ledger, registrations, null, placement);
        }

        /// <summary>
        /// The same, taking an unchanged section from <paramref name="reuseFrom"/> -- another snapshot, built -- instead
        /// of taking it again. Nothing of <paramref name="reuseFrom"/> is changed or kept.
        /// </summary>
        internal VpMultiCutBuildOutcome TryBuild(
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations,
            VpMultiCutSnapshot reuseFrom,
            IVpFragmentPlacement placement = null)
        {
            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (registrations == null)
            {
                throw new ArgumentNullException(nameof(registrations));
            }

            for (int g = 0; g < registrations.Count; g++)
            {
                VpMultiCutRegistration registration = registrations[g];
                if (registration.reflected == null)
                {
                    throw new ArgumentNullException(
                        nameof(registrations), "what each geometry reflects must be said, even if it is nothing");
                }

                if (!IsFiniteNonNegative(registration.vertexEpsilon))
                {
                    throw new ArgumentOutOfRangeException(nameof(registrations), "a vertex epsilon is negative or not finite");
                }
            }

            Clear();
            _buildGeneration++;
            _invalid = VpMultiCutInvalidInput.None;
            SectionBuildCount = 0;
            if (reuseFrom == this || (reuseFrom != null && !reuseFrom.IsBuilt))
            {
                reuseFrom = null;
            }

            for (int g = 0; g < registrations.Count; g++)
            {
                VpMultiCutRegistration registration = registrations[g];
                if (!IsWithinContract(registration.localBounds)
                    || !IsPlacement(registration.geometryLocalToWorld)
                    || !IsRigid(registration.lineageToGeometryLocal))
                {
                    return Fail(Invalid(VpMultiCutInvalidInput.InputContract));
                }
            }

            // Decided with no room of this snapshot's, so that no shortage below can be what hides it.
            VpMultiCutBuildOutcome outcome = Validate(ledger, registrations);
            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                return Fail(outcome);
            }

            for (int g = 0; g < registrations.Count; g++)
            {
                VpMultiCutRegistration registration = registrations[g];
                int branchStart = _branchCount;
                int renderFragmentStart = _renderFragmentCount;
                outcome = TryCollectBranches(ledger, g, registration.root, registration.reflected);
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return Fail(outcome);
                }

                outcome = TryGroup(ledger, registration, g, branchStart, renderFragmentStart, placement);
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return Fail(outcome);
                }

                for (int r = renderFragmentStart; r < _renderFragmentCount; r++)
                {
                    outcome = TryBuildRenderFragment(ledger, registration, r, reuseFrom);
                    if (outcome != VpMultiCutBuildOutcome.Built)
                    {
                        return Fail(outcome);
                    }
                }
            }

            _registrationCount = registrations.Count;
            IsBuilt = true;
            return VpMultiCutBuildOutcome.Built;
        }

        private void Clear()
        {
            IsBuilt = false;
            _branchCount = 0;
            _candidateCount = 0;
            _renderFragmentCount = 0;
            _conditionCount = 0;
            _capCount = 0;
            _capVertexCount = 0;
            _sectionCount = 0;
        }

        private VpMultiCutBuildOutcome Fail(VpMultiCutBuildOutcome outcome)
        {
            Clear();
            if (outcome != VpMultiCutBuildOutcome.InvalidInput)
            {
                _invalid = VpMultiCutInvalidInput.None;
            }
            else if (_invalid == VpMultiCutInvalidInput.None)
            {
                _invalid = VpMultiCutInvalidInput.Lineage;
            }

            return outcome;
        }

        /// <summary>Invalid input, and why.</summary>
        private VpMultiCutBuildOutcome Invalid(VpMultiCutInvalidInput why)
        {
            _invalid = why;
            return VpMultiCutBuildOutcome.InvalidInput;
        }

        // ----- decided before any room is taken ----------------------------------------------------------------------

        /// <summary>
        /// Reads the ledger against the registrations with nothing but locals: every walk goes up a fragment's origins,
        /// bounded by the number of operations, and nothing is stored. See the class notes for what is decided here.
        /// </summary>
        private VpMultiCutBuildOutcome Validate(
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations)
        {
            int steps = ledger.OperationCount + 1;
            for (int g = 0; g < registrations.Count; g++)
            {
                VpMultiCutRegistration registration = registrations[g];
                if (!ledger.TryGetFragmentState(registration.root, out _))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                // A section of this box, placed, can be computed in float at all, and every cap vertex it gives is bounded.
                if (!VpSectionBounds.TryPlacedExtent(
                        registration.localBounds, registration.geometryLocalToWorld, registration.vertexEpsilon,
                        out _))
                {
                    return Invalid(VpMultiCutInvalidInput.ConservativeSection);
                }

                // No root on another's lineage: not the same root, and no other root above this one.
                for (int h = 0; h < registrations.Count; h++)
                {
                    if (h != g && registrations[h].root == registration.root)
                    {
                        return Invalid(VpMultiCutInvalidInput.Lineage);
                    }
                }

                LogicalFragmentId at = registration.root;
                for (int step = 0; ; step++)
                {
                    if (step > steps)
                    {
                        return Invalid(VpMultiCutInvalidInput.Lineage);
                    }

                    if (!ledger.TryGetOrigin(at, out CutOperationId origin, out float side))
                    {
                        break;
                    }

                    if (!ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                    {
                        return Invalid(VpMultiCutInvalidInput.Lineage);
                    }

                    // A boundary above the root the geometry does not reflect is a candidate of every branch below.
                    if (!Contains(registration.reflected, new VpClipBoundary(new VpCapFace(ledger, origin), side)))
                    {
                        VpMultiCutBuildOutcome planed = CheckPlane(cut.plane, registration);
                        if (planed != VpMultiCutBuildOutcome.Built)
                        {
                            return planed;
                        }
                    }

                    at = cut.source;
                    for (int h = 0; h < registrations.Count; h++)
                    {
                        if (h != g && registrations[h].root == at)
                        {
                            return Invalid(VpMultiCutInvalidInput.Lineage);
                        }
                    }
                }
            }

            for (int position = 0; ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
            {
                VpMultiCutBuildOutcome found = RegistrationOf(ledger, registrations, operation.source, steps, out int g);
                if (found != VpMultiCutBuildOutcome.Built)
                {
                    return found;
                }

                if (g < 0)
                {
                    continue;
                }

                VpMultiCutRegistration registration = registrations[g];
                switch (operation.state)
                {
                    case LogicalCutOperationState.Aborted:
                    {
                        // The source is retired: past an Ignored boundary when more of its chain is unreflected than the
                        // selection can take. Every requirement is the previous candidate, so nothing is Ignored for order.
                        VpMultiCutBuildOutcome counted = CountUnreflected(
                            ledger, operation.source, registration.reflected, steps, out int unreflected);
                        if (counted != VpMultiCutBuildOutcome.Built)
                        {
                            return counted;
                        }

                        if (unreflected > VpClipCandidates.Capacity)
                        {
                            return VpMultiCutBuildOutcome.RetiredInsideAggregate;
                        }

                        break;
                    }

                    case LogicalCutOperationState.Published:
                    case LogicalCutOperationState.Completed:
                    case LogicalCutOperationState.Terminated:
                    {
                        VpMultiCutBuildOutcome planed = CheckPlane(operation.plane, registration);
                        if (planed != VpMultiCutBuildOutcome.Built)
                        {
                            return planed;
                        }

                        break;
                    }

                    case LogicalCutOperationState.Admitted:
                    {
                        if (ledger.TryGetPreparedAnchorDistribution(operation.id, out _))
                        {
                            VpMultiCutBuildOutcome planed = CheckPlane(operation.plane, registration);
                            if (planed != VpMultiCutBuildOutcome.Built)
                            {
                                return planed;
                            }
                        }

                        break;
                    }
                }
            }

            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// One plane of the lineage: carried by the mapping and the placement as the build carries it, and, in the
        /// geometry's coordinates the section is taken in, within the section bounds of the registration's box.
        /// </summary>
        private VpMultiCutBuildOutcome CheckPlane(float4 plane, in VpMultiCutRegistration registration)
        {
            if (!VpCutPlane.TryGeometryLocalToWorld(plane, registration.lineageToGeometryLocal, out float4 local)
                || !VpCutPlane.TryGeometryLocalToWorld(local, registration.geometryLocalToWorld, out _))
            {
                return Invalid(VpMultiCutInvalidInput.PlaneNotCarried);
            }

            return VpSectionBounds.IsPlaneWithin(local, registration.localBounds)
                ? VpMultiCutBuildOutcome.Built
                : Invalid(VpMultiCutInvalidInput.ConservativeSection);
        }

        /// <summary>
        /// Which registration's lineage <paramref name="fragment"/> is on, found by going up its origins to a root, or -1
        /// when it is on none.
        /// </summary>
        private VpMultiCutBuildOutcome RegistrationOf(
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations,
            LogicalFragmentId fragment,
            int steps,
            out int registration)
        {
            registration = -1;
            LogicalFragmentId at = fragment;
            for (int step = 0; step <= steps; step++)
            {
                for (int g = 0; g < registrations.Count; g++)
                {
                    if (registrations[g].root == at)
                    {
                        registration = g;
                        return VpMultiCutBuildOutcome.Built;
                    }
                }

                if (!ledger.TryGetOrigin(at, out CutOperationId origin, out _))
                {
                    return VpMultiCutBuildOutcome.Built;
                }

                if (!ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                at = cut.source;
            }

            return Invalid(VpMultiCutInvalidInput.Lineage);
        }

        /// <summary>How many boundaries of <paramref name="fragment"/>'s whole chain the geometry does not reflect.</summary>
        private VpMultiCutBuildOutcome CountUnreflected(
            LogicalCutLedger ledger,
            LogicalFragmentId fragment,
            IReadOnlyCollection<VpClipBoundary> reflected,
            int steps,
            out int count)
        {
            count = 0;
            LogicalFragmentId at = fragment;
            for (int step = 0; step <= steps; step++)
            {
                if (!ledger.TryGetOrigin(at, out CutOperationId origin, out float side))
                {
                    return VpMultiCutBuildOutcome.Built;
                }

                if (!ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                count += Contains(reflected, new VpClipBoundary(new VpCapFace(ledger, origin), side)) ? 0 : 1;
                at = cut.source;
            }

            return Invalid(VpMultiCutInvalidInput.Lineage);
        }

        private static bool Contains(IReadOnlyCollection<VpClipBoundary> set, VpClipBoundary boundary)
        {
            foreach (VpClipBoundary item in set)
            {
                if (item == boundary)
                {
                    return true;
                }
            }

            return false;
        }

        // ----- branches ----------------------------------------------------------------------------------------------

        /// <summary>
        /// The walk from the root, positive child before negative, each branch collected and selected as it is found.
        /// A retired fragment is checked for lying past an Ignored boundary, and otherwise drawn as nothing.
        /// </summary>
        private VpMultiCutBuildOutcome TryCollectBranches(
            LogicalCutLedger ledger, int registration, LogicalFragmentId root, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            int top = 0;
            _stack[top++] = root;
            while (top > 0)
            {
                LogicalFragmentId at = _stack[--top];
                if (!ledger.TryGetFragmentState(at, out LogicalFragmentState state))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                switch (state)
                {
                    case LogicalFragmentState.Live:
                    {
                        if (ledger.TryGetActiveOperation(at, out CutOperationId pending)
                            && ledger.TryGetPreparedAnchorDistribution(pending, out _))
                        {
                            VpMultiCutBuildOutcome positive = TryAddBranch(ledger, registration, at, 1f, reflected);
                            if (positive != VpMultiCutBuildOutcome.Built)
                            {
                                return positive;
                            }

                            VpMultiCutBuildOutcome negative = TryAddBranch(ledger, registration, at, -1f, reflected);
                            if (negative != VpMultiCutBuildOutcome.Built)
                            {
                                return negative;
                            }
                        }
                        else
                        {
                            VpMultiCutBuildOutcome whole = TryAddBranch(ledger, registration, at, 0f, reflected);
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
                            return Invalid(VpMultiCutInvalidInput.Lineage);
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
            LogicalCutLedger ledger, int registration, LogicalFragmentId fragment, float pendingSide,
            IReadOnlyCollection<VpClipBoundary> reflected)
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
            _branches[_branchCount++] = new VpMultiCutBranch(
                registration, fragment, pendingSide, _candidateCount, count, selected, -1);
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
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                default:
                    return VpMultiCutBuildOutcome.CapacityExceeded;
            }
        }

        // ----- render fragments ---------------------------------------------------------------------------------------

        /// <summary>
        /// Branches that share the aggregation root and the selected prefix are one render fragment; a branch with nothing
        /// Ignored is its own. The walk puts one root's branches next to each other, so a render fragment met again after
        /// another one is a lineage this cannot read, and is refused. Each aggregation root's own chain must be the prefix.
        /// Only this registration's branches and render fragments take part: nothing is aggregated across registrations.
        /// </summary>
        private VpMultiCutBuildOutcome TryGroup(
            LogicalCutLedger ledger, in VpMultiCutRegistration registration, int registrationIndex, int branchStart,
            int renderFragmentStart, IVpFragmentPlacement placement)
        {
            IReadOnlyCollection<VpClipBoundary> reflected = registration.reflected;
            for (int b = branchStart; b < _branchCount; b++)
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
                    for (int r = renderFragmentStart; r < _renderFragmentCount; r++)
                    {
                        VpMultiCutRenderFragment existing = _renderFragments[r];
                        if (!existing.aggregated || existing.root != root || !SamePrefix(_branches[existing.branchStart], branch))
                        {
                            continue;
                        }

                        if (r != _renderFragmentCount - 1)
                        {
                            return Invalid(VpMultiCutInvalidInput.Lineage);
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

                // Where this one stands. A fragment that follows a placement of its own is drawn there, and
                // nothing is added to it; one that does not is drawn at the registration's placement, which is the
                // ordinary answer and not a fallback for a failure.
                // <para>
                // An aggregate is asked about its **first living branch in this walk**, not about its root
                // (DESIGN 5.2, D-187). The root of an aggregate is the source of the first Ignored boundary, and
                // once that boundary is published the source has been replaced and stands nowhere of its own. The
                // shape drawn is still the root's, and the root is still what groups these branches and what the
                // caps and the selected boundaries are made from: only where that one shape is put comes from this
                // branch. This branch is the group's first because a group's branches are contiguous in the walk and
                // this is the one that makes the render fragment; the ones that join it later do not ask again.
                // </para>
                LogicalFragmentId standsWhere = aggregated ? branch.fragment : root;
                if (!TryPlacementOf(placement, registration, standsWhere, out Matrix4x4 geometryLocalToWorld))
                {
                    return Invalid(VpMultiCutInvalidInput.InputContract);
                }

                _renderFragments[_renderFragmentCount] = new VpMultiCutRenderFragment(
                    registrationIndex, registration.localBounds, geometryLocalToWorld, root, rootPendingSide,
                    aggregated, b, 1, 0, 0, VpInstanceClip.None, 0, 0);
                _branches[b] = WithRenderFragment(branch, _renderFragmentCount);
                _renderFragmentCount++;
            }

            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// Where one render fragment's shape stands: the placement its fragment follows, or the registration's own
        /// when it follows nothing. Nothing is put on top of it. A placement that is not one is refused exactly as the
        /// registration's would be.
        /// </summary>
        private static bool TryPlacementOf(
            IVpFragmentPlacement placement, in VpMultiCutRegistration registration, LogicalFragmentId fragment,
            out Matrix4x4 geometryLocalToWorld)
        {
            if (placement == null)
            {
                // Nothing follows anything here: the registration's placement is what everything of it is drawn at.
                geometryLocalToWorld = registration.geometryLocalToWorld;
                return true;
            }

            VpFragmentPlacementKind kind = placement.TryGetGeometryLocalToWorld(fragment, out Matrix4x4 followed);
            if (kind == VpFragmentPlacementKind.Missing)
            {
                // It follows something and where was not said. Drawing it where it was registered would draw it where
                // it used to be, so nothing is drawn from this input at all.
                geometryLocalToWorld = default;
                return false;
            }

            Matrix4x4 baseline = kind == VpFragmentPlacementKind.Following ? followed : registration.geometryLocalToWorld;
            if (!IsPlacement(baseline))
            {
                geometryLocalToWorld = default;
                return false;
            }

            geometryLocalToWorld = baseline;
            return IsPlacement(geometryLocalToWorld);
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
                return Invalid(VpMultiCutInvalidInput.Lineage);
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
                return Invalid(VpMultiCutInvalidInput.Lineage);
            }

            for (int i = 0; i < count; i++)
            {
                if (_checkCandidates[i].boundary != _candidates[branch.candidateStart + i].boundary)
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
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
            in VpMultiCutRegistration registration,
            int index,
            VpMultiCutSnapshot reuseFrom)
        {
            LogicalFragmentId registrationRoot = registration.root;
            Matrix4x4 lineageToGeometryLocal = registration.lineageToGeometryLocal;
            float vertexEpsilon = registration.vertexEpsilon;
            VpMultiCutRenderFragment renderFragment = _renderFragments[index];

            // This render fragment's own placement, decided when it was made: the world planes of its boundaries and
            // its cap polygons are all made with that one matrix.
            Matrix4x4 geometryLocalToWorld = renderFragment.geometryLocalToWorld;
            VpMultiCutBranch representative = _branches[renderFragment.branchStart];

            // 1. The selected boundaries: the geometry's own plane, the world plane, the condition and the half-space.
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
                    return Invalid(VpMultiCutInvalidInput.PlaneNotCarried);
                }

                _localPlanes[j] = local;
                _worldPlanes[j] = world;
                _halfSpaces[j] = new VpClipHalfSpace(ToVector4(world), candidate.boundary.side);
                _conditions[_conditionCount++] = new VpCapConstraint(
                    candidate.boundary.face, candidate.boundary.side, ToVector4(world));
            }

            _selectedHalfSpaces.Set(0, selected);
            if (!VpInstanceClip.TryKeep(_selectedHalfSpaces, out VpInstanceClip clip))
            {
                return Invalid(VpMultiCutInvalidInput.ClipNotTaken);
            }

            // 3. One cap per selected boundary, cut by the other selected half-spaces.
            _selectedCandidates.Set(representative.candidateStart, selected);
            _selectedStates.Set(representative.candidateStart, selected);
            _selectedPlanes.Set(0, selected);
            int capStart = _capCount;
            for (int j = 0; j < selected; j++)
            {
                VpClipBoundary boundary = _candidates[representative.candidateStart + j].boundary;
                VpMultiCutBuildOutcome sectioned = TryTakeSection(
                    boundary.face, _localPlanes[j], registration, geometryLocalToWorld, reuseFrom, out int initial,
                    out int sectionSlot);
                if (sectioned != VpMultiCutBuildOutcome.Built)
                {
                    return sectioned;
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
                    return Invalid(VpMultiCutInvalidInput.ClipNotTaken);
                }

                for (int v = 0; v < clipped; v++)
                {
                    // Finite inputs can still overflow when added; such a vertex is refused, never dropped or emptied.
                    Vector3 placed = _clipped[v];
                    if (!IsFinite(placed))
                    {
                        return Invalid(VpMultiCutInvalidInput.DrawnCapVertex);
                    }

                    _capVertices[_capVertexCount + v] = placed;
                }

                float4 plane = _worldPlanes[j];
                var normal = new Vector3(plane.x, plane.y, plane.z);
                _capSection[_capCount] = sectionSlot;
                _caps[_capCount++] = new VpMultiCutCap(
                    index, boundary, plane, -boundary.side * normal, _capVertexCount, initial, clipped);
                _capVertexCount += clipped;
            }

            _renderFragments[index] = new VpMultiCutRenderFragment(
                renderFragment.registration, renderFragment.localBounds, renderFragment.geometryLocalToWorld,
                renderFragment.root, renderFragment.rootPendingSide, renderFragment.aggregated,
                renderFragment.branchStart, renderFragment.branchCount, conditionStart, selected, clip,
                capStart, selected);
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// The section of this face's plane through this registration's box, into <c>_initial</c> in the order it was
        /// built: taken already in this build, else taken from <paramref name="reuseFrom"/> under exactly the same key,
        /// else taken now and counted. A plane that misses the box is a section of no vertices, and is kept like any other.
        /// Asked only for a selected boundary's cap. The room cannot be short (see the class notes); if it is all the same,
        /// the build is refused and nothing is taken without being kept.
        /// </summary>
        private VpMultiCutBuildOutcome TryTakeSection(
            VpCapFace face, float4 localPlane, in VpMultiCutRegistration registration, Matrix4x4 geometryLocalToWorld,
            VpMultiCutSnapshot reuseFrom, out int vertexCount, out int sectionSlot)
        {
            vertexCount = 0;
            sectionSlot = -1;
            const int stride = VpCapBoundsPolygon.MaxVertices;
            int found = FindSection(face, localPlane, registration, geometryLocalToWorld);
            if (found >= 0)
            {
                sectionSlot = found;
                vertexCount = _sections[found].vertexCount;
                Array.Copy(_sectionVertices, found * stride, _initial, 0, vertexCount);
                return VpMultiCutBuildOutcome.Built;
            }

            if (_sectionCount >= _sections.Length)
            {
                return VpMultiCutBuildOutcome.CapacityExceeded;
            }

            int slot = _sectionCount;
            int reused = reuseFrom != null ? reuseFrom.FindSection(face, localPlane, registration, geometryLocalToWorld) : -1;
            if (reused >= 0)
            {
                vertexCount = reuseFrom._sections[reused].vertexCount;
                Array.Copy(reuseFrom._sectionVertices, reused * stride, _sectionVertices, slot * stride, vertexCount);
            }
            else
            {
                SectionBuildCount++;
                if (!_section.TryBuild(
                        registration.localBounds, localPlane, geometryLocalToWorld,
                        registration.vertexEpsilon, _sectionVertices, slot * stride, out vertexCount, out _))
                {
                    return Invalid(VpMultiCutInvalidInput.SectionNotTaken);
                }
            }

            _sections[slot] = new Section
            {
                face = face,
                localPlane = localPlane,
                bounds = registration.localBounds,
                placement = geometryLocalToWorld,
                epsilon = registration.vertexEpsilon,
                vertexCount = vertexCount,
            };
            _sectionCount++;
            sectionSlot = slot;
            Array.Copy(_sectionVertices, slot * stride, _initial, 0, vertexCount);
            return VpMultiCutBuildOutcome.Built;
        }

        private int FindSection(
            VpCapFace face, float4 localPlane, in VpMultiCutRegistration registration, Matrix4x4 geometryLocalToWorld)
        {
            for (int i = 0; i < _sectionCount; i++)
            {
                Section section = _sections[i];
                if (section.face == face
                    && Same(section.localPlane, localPlane)
                    && Same(section.bounds, registration.localBounds)
                    && Same(section.placement, geometryLocalToWorld)
                    && section.epsilon == registration.vertexEpsilon)
                {
                    return i;
                }
            }

            return -1;
        }

        // What a section was taken of is compared value by value: Unity's own equality for Vector4, Matrix4x4 and Bounds
        // is approximate, and something merely close to the face a section was taken of is a different face.
        private static bool Same(float4 a, float4 b)
        {
            return a.x == b.x && a.y == b.y && a.z == b.z && a.w == b.w;
        }

        private static bool Same(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool Same(Bounds a, Bounds b)
        {
            Vector3 aMin = a.min;
            Vector3 bMin = b.min;
            Vector3 aMax = a.max;
            Vector3 bMax = b.max;
            return aMin.x == bMin.x && aMin.y == bMin.y && aMin.z == bMin.z
                && aMax.x == bMax.x && aMax.y == bMax.y && aMax.z == bMax.z;
        }

        private static VpMultiCutRenderFragment WithBranches(in VpMultiCutRenderFragment r, int branchCount)
        {
            return new VpMultiCutRenderFragment(
                r.registration, r.localBounds, r.geometryLocalToWorld, r.root, r.rootPendingSide, r.aggregated, r.branchStart, branchCount, r.conditionStart, r.conditionCount,
                r.clip, r.capStart, r.capCount);
        }

        private static VpMultiCutBranch WithRenderFragment(in VpMultiCutBranch b, int renderFragment)
        {
            return new VpMultiCutBranch(
                b.registration, b.fragment, b.pendingSide, b.candidateStart, b.candidateCount, b.selectedCount, renderFragment);
        }

        private static Vector4 ToVector4(float4 value)
        {
            return new Vector4(value.x, value.y, value.z, value.w);
        }

        /// <summary>
        /// Whether a box, a placement and a mapping are inside the input contract a build checks first, so that a caller
        /// can refuse them where it takes them in.
        /// </summary>
        internal static bool IsWithinInputContract(
            Bounds localBounds, Matrix4x4 geometryLocalToWorld, Matrix4x4 lineageToGeometryLocal)
        {
            return IsWithinContract(localBounds) && IsPlacement(geometryLocalToWorld) && IsRigid(lineageToGeometryLocal);
        }

        /// <summary>
        /// The part of the conservative numeric check a registration settles by itself -- its box, its placement and the
        /// vertex epsilon it will be built with -- so that a caller can refuse it where it takes it in. The same function
        /// the build's own check asks.
        /// </summary>
        internal static bool IsWithinSectionBounds(Bounds localBounds, Matrix4x4 geometryLocalToWorld, float vertexEpsilon)
        {
            return VpSectionBounds.TryPlacedExtent(localBounds, geometryLocalToWorld, vertexEpsilon, out _);
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
