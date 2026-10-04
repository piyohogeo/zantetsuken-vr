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
    /// Which room a build answered <see cref="VpMultiCutBuildOutcome.CapacityExceeded"/> for, so that the owner of the
    /// room can tell which of its capacities to give more of -- or say which one it could not.
    /// </summary>
    public enum VpMultiCutShortage
    {
        /// <summary>The last build was not refused for room.</summary>
        None = 0,

        /// <summary>Logical branches, or the walk over them, whose room follows from the branches.</summary>
        Branches = 1,

        /// <summary>Candidates over every branch together.</summary>
        Candidates = 2,

        /// <summary>A chain longer than the chain depth, or a chain checked that does not fit the room derived from it.</summary>
        ChainDepth = 3,

        /// <summary>Render fragments.</summary>
        RenderFragments = 4,

        /// <summary>Caps, their conditions or their sections.</summary>
        Caps = 5,
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
    /// <para>
    /// A branch with a side carries the cut that side belongs to (<see cref="pendingOperation"/>) beside the side
    /// itself. The two are read from the ledger at the one moment the branch is made, so they cannot come to disagree,
    /// and whoever needs to name this side later -- to ask where it stands, for instance -- has both without asking
    /// the ledger again.
    /// </para>
    /// </summary>
    public readonly struct VpMultiCutBranch
    {
        internal VpMultiCutBranch(
            int registration, LogicalFragmentId fragment, float pendingSide, CutOperationId pendingOperation,
            int candidateStart, int candidateCount, int selectedCount, int renderFragment)
        {
            this.registration = registration;
            this.fragment = fragment;
            this.pendingSide = pendingSide;
            this.pendingOperation = pendingOperation;
            this.candidateStart = candidateStart;
            this.candidateCount = candidateCount;
            this.selectedCount = selectedCount;
            this.renderFragment = renderFragment;
        }

        /// <summary>The registration it belongs to: its index in the list the snapshot was built from.</summary>
        public readonly int registration;

        public readonly LogicalFragmentId fragment;
        public readonly float pendingSide;

        /// <summary>
        /// The accepted cut this branch is a side of, for a branch that has a side; unset for one drawn whole. It is
        /// the operation the ledger named when this branch was made, not one looked up later.
        /// </summary>
        public readonly CutOperationId pendingOperation;

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
    /// is fixed when this is made and never grows -- with one exception: the validation's two lookup tables (which
    /// registration each root is, and which registration's lineage each fragment reached is on) hold one entry per root
    /// and one per fragment the ledger's operations were cut from, unrelated lineages included, so they grow with the
    /// history once, the first time a build meets it; a build over an unchanged history allocates nothing for them.
    /// </para>
    /// </summary>
    public sealed class VpMultiCutSnapshot : IDisposable
    {
        // The room (TL, 2026-10-05). The large arrays of plain numbers are rooms (VpNumericRoom): on reserved address
        // space when the snapshot was made for a display (TryCreateOnBacking), where growing is more pages committed
        // behind the same base, and in managed arrays when it was made on its own. The arrays whose items hold a
        // reference -- a candidate's, a condition's, a cap's and a section's face names its ledger -- cannot stand on
        // native memory and stay managed arrays; they are replaced by larger ones when the room grows.
        private VpMultiCutCapacities _capacities;
        private VpNumericRoom<VpMultiCutBranch> _branches;
        private VpClipCandidate[] _candidates;
        private VpClipSelectionState[] _states;
        private VpNumericRoom<VpMultiCutRenderFragment> _renderFragments;

        /// <summary>
        /// Who each render fragment stands where, settled with the structure: the branch that puts it there, named by
        /// its fragment and -- when it is a side of an accepted cut -- by that cut and that side.
        /// <para>
        /// For an aggregate this is its **first living branch**, not its root (DESIGN 5.2, D-187): the root of an
        /// aggregate is the source of the first Ignored boundary, and once that boundary is published the source
        /// stands nowhere of its own. The shape drawn, the caps and the selected boundaries are still the root's.
        /// </para>
        /// <para>
        /// It is **not** <see cref="_sideIdentity"/>. That one records what a render fragment is, as a side, for the
        /// display, and an aggregate's is made from its root, which is a different fragment with a different side --
        /// often none at all. Asking where something stands with that record would ask about the wrong one.
        /// </para>
        /// </summary>
        private VpNumericRoom<VpMultiCutStandsAs> _standsAs;

        private VpNumericRoom<VpMultiCutSideIdentity> _sideIdentity;
        private VpNumericRoom<VpMultiCutCapIdentity> _capIdentity;
        private VpCapConstraint[] _conditions;
        private VpMultiCutCap[] _caps;

        // Validate's own tables, filled at each structural validation and read only inside it: which registration each
        // root is (so a fragment on a chain is matched by one lookup, not by comparing it with every registration), and
        // the answer already found for a fragment -- the registration its lineage is on, or none -- so that one chain is
        // walked once however many operations were cut from it (2026-09-29: the walk from every operation ever admitted,
        // compared with every registration at every step, was what grew with the history, the depth and the count).
        private readonly Dictionary<LogicalFragmentId, int> _registrationOfRoot = new Dictionary<LogicalFragmentId, int>();
        private readonly Dictionary<LogicalFragmentId, int> _lineageOf = new Dictionary<LogicalFragmentId, int>();
        private readonly HashSet<LogicalFragmentId> _rootsRegisteredTwice = new HashSet<LogicalFragmentId>();   // roots two registrations or more have: filled with the root table, read once per registration
        private readonly List<LogicalFragmentId> _lineageWalk = new List<LogicalFragmentId>();
        private VpNumericRoom<Vector3> _capVertices;

        // Work room, made once.
        private readonly VpClipBoundary[] _chain;

        // The chains read only to be checked -- an aggregation root's, a retired fragment's -- go here, never into the
        // candidates kept: a chain has no more candidates than boundaries, so the chain depth is room enough.
        private readonly VpClipCandidate[] _checkCandidates;
        private readonly VpClipSelectionState[] _checkStates;
        private VpNumericRoom<LogicalFragmentId> _stack;
        private readonly float4[] _localPlanes = new float4[VpClipCandidates.Capacity];
        private readonly float4[] _worldPlanes = new float4[VpClipCandidates.Capacity];
        private readonly VpClipHalfSpace[] _halfSpaces = new VpClipHalfSpace[VpClipCandidates.Capacity];
        private readonly Vector3[] _initial = new Vector3[VpCapBoundsPolygon.MaxVertices];
        private readonly Vector3[] _clipped = new Vector3[VpCapPolygonClip.MaxVertices];
        private readonly VpCapBoundsPolygon _section = new VpCapBoundsPolygon();
        private RangeList<VpClipCandidate> _selectedCandidates;
        private RangeList<VpClipSelectionState> _selectedStates;
        private readonly RangeList<float4> _selectedPlanes;
        private readonly RangeList<VpClipHalfSpace> _selectedHalfSpaces;

        // The sections taken in this build, one per key, and their vertices: at most one per cap.
        private Section[] _sections;
        private VpNumericRoom<Vector3> _sectionVertices;
        private readonly VpMultiCutRegistration[] _single = new VpMultiCutRegistration[1];

        // Which section each cap was built from: the slot in _sections, so that the section kept for a drawn cap can be
        // read as it is (InitialSection) without being taken again.
        private VpNumericRoom<int> _capSection;
        private bool _disposed;
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
            : this(capacities, null, capacities)
        {
        }

        /// <summary>
        /// A snapshot whose numeric rooms stand on address space reserved for <paramref name="reserve"/> and committed
        /// for <paramref name="capacities"/>, every page of the committed part written before this returns. It grows
        /// in place up to the reservation (<see cref="TryGrowTo"/>) and is disposed by whoever made it. False, holding
        /// nothing, when a reservation or a first commit is refused or the sizes do not hold together.
        /// </summary>
        internal static bool TryCreateOnBacking(
            IVpPageBacking backing, VpMultiCutCapacities capacities, VpMultiCutCapacities reserve,
            out VpMultiCutSnapshot snapshot, out string failure)
        {
            snapshot = null;
            failure = null;
            if (backing == null)
            {
                throw new ArgumentNullException(nameof(backing));
            }

            try
            {
                snapshot = new VpMultiCutSnapshot(capacities, backing, reserve);
                return true;
            }
            catch (RoomNotMadeException exception)
            {
                failure = exception.Message;
                return false;
            }
            catch (OutOfMemoryException exception)
            {
                failure = "memory could not be had: " + exception.Message;
                return false;
            }
        }

        private sealed class RoomNotMadeException : Exception
        {
            public RoomNotMadeException(string message)
                : base(message)
            {
            }
        }

        /// <summary>The numbers every room follows from: what one capacity makes of each derived size.</summary>
        internal readonly struct RoomSizes
        {
            public RoomSizes(in VpMultiCutCapacities capacities)
            {
                branches = capacities.branches;
                candidates = capacities.candidates;
                renderFragments = capacities.renderFragments;
                caps = capacities.caps;
                capVertices = (long)capacities.caps * VpCapPolygonClip.MaxVertices;
                sectionVertices = (long)capacities.caps * VpCapBoundsPolygon.MaxVertices;
                stack = ((long)capacities.branches * 2) + 2;
            }

            public readonly int branches;
            public readonly int candidates;
            public readonly int renderFragments;
            public readonly int caps;
            public readonly long capVertices;
            public readonly long sectionVertices;
            public readonly long stack;

            public bool FitsInt => capVertices <= int.MaxValue && sectionVertices <= int.MaxValue && stack <= int.MaxValue;
        }

        private VpMultiCutSnapshot(VpMultiCutCapacities capacities, IVpPageBacking backing, VpMultiCutCapacities reserve)
        {
            if (capacities.branches <= 0 || capacities.candidates < 0 || capacities.renderFragments <= 0
                || capacities.caps < 0 || capacities.chainDepth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities));
            }

            var sizes = new RoomSizes(capacities);
            var reserved = new RoomSizes(reserve);
            if (!sizes.FitsInt || !reserved.FitsInt)
            {
                throw new ArgumentOutOfRangeException(nameof(capacities), "a derived size does not fit an int");
            }

            if (reserve.branches < capacities.branches || reserve.candidates < capacities.candidates
                || reserve.renderFragments < capacities.renderFragments || reserve.caps < capacities.caps
                || reserve.chainDepth != capacities.chainDepth)
            {
                throw new ArgumentOutOfRangeException(nameof(reserve), "the reservation holds the first room, and the chain depth is not grown");
            }

            _capacities = capacities;
            try
            {
                _branches = Room<VpMultiCutBranch>(backing, reserved.branches, sizes.branches);
                _capIdentity = Room<VpMultiCutCapIdentity>(backing, reserved.candidates, sizes.candidates);
                _renderFragments = Room<VpMultiCutRenderFragment>(backing, reserved.renderFragments, sizes.renderFragments);
                _standsAs = Room<VpMultiCutStandsAs>(backing, reserved.renderFragments, sizes.renderFragments);
                _sideIdentity = Room<VpMultiCutSideIdentity>(backing, reserved.renderFragments, sizes.renderFragments);
                _capVertices = Room<Vector3>(backing, (int)reserved.capVertices, (int)sizes.capVertices);
                _sectionVertices = Room<Vector3>(backing, (int)reserved.sectionVertices, (int)sizes.sectionVertices);
                _capSection = Room<int>(backing, reserved.caps, sizes.caps);
                _stack = Room<LogicalFragmentId>(backing, (int)reserved.stack, (int)sizes.stack);
                _candidates = new VpClipCandidate[capacities.candidates];
                _states = new VpClipSelectionState[capacities.candidates];
                _conditions = new VpCapConstraint[capacities.caps];
                _caps = new VpMultiCutCap[capacities.caps];
                _sections = new Section[capacities.caps];
                _chain = new VpClipBoundary[capacities.chainDepth];
                _checkCandidates = new VpClipCandidate[capacities.chainDepth];
                _checkStates = new VpClipSelectionState[capacities.chainDepth];
            }
            catch
            {
                // Whatever was reserved before the one that failed is given back: nothing of a snapshot that was not made is held.
                DisposeRooms();
                throw;
            }

            _selectedCandidates = new RangeList<VpClipCandidate>(_candidates);
            _selectedStates = new RangeList<VpClipSelectionState>(_states);
            _selectedPlanes = new RangeList<float4>(_worldPlanes);
            _selectedHalfSpaces = new RangeList<VpClipHalfSpace>(_halfSpaces);
            if (backing != null)
            {
                PresizeForRoom(capacities.renderFragments);
            }
        }

        /// <summary>
        /// For a snapshot made for a display: the arrays kept per registration and per render fragment -- what was
        /// validated, what was placed, the reflected indexes, the segments -- are made for the room now, so that play up
        /// to the room makes none of them. They hold what the builds remember and stay managed arrays; one made on its
        /// own grows them as it meets the need, as before. Memory that cannot be had here is left to that same growth.
        /// </summary>
        private void PresizeForRoom(int count)
        {
            try
            {
                if (_validatedHas.Length < count)
                {
                    Array.Resize(ref _validatedHas, count);
                    Array.Resize(ref _validatedNow, count);
                    Array.Resize(ref _validatedBounds, count);
                    Array.Resize(ref _validatedPlacement, count);
                    Array.Resize(ref _validatedLineage, count);
                    Array.Resize(ref _validatedEpsilon, count);
                }

                if (_placedHas.Length < count)
                {
                    Array.Resize(ref _placedHas, count);
                    Array.Resize(ref _placedPassed, count);
                }

                if (_reflected.Length < count)
                {
                    var more = new VpReflectedIndex[count];
                    Array.Copy(_reflected, more, _reflected.Length);
                    for (int i = _reflected.Length; i < count; i++) more[i] = new VpReflectedIndex();
                    _reflected = more;
                }

                if (_segOf.Length < count)
                {
                    Array.Resize(ref _segOf, count);
                }
            }
            catch (OutOfMemoryException)
            {
            }
        }

        private static VpNumericRoom<T> Room<T>(IVpPageBacking backing, int reserved, int length) where T : unmanaged
        {
            if (backing == null)
            {
                return VpNumericRoom<T>.Managed(length);
            }

            if (!VpNumericRoom<T>.TryCreateNative(backing, reserved, length, out VpNumericRoom<T> room, out string failure))
            {
                throw new RoomNotMadeException(failure);
            }

            return room;
        }

        private void DisposeRooms()
        {
            _branches?.Dispose();
            _capIdentity?.Dispose();
            _renderFragments?.Dispose();
            _standsAs?.Dispose();
            _sideIdentity?.Dispose();
            _capVertices?.Dispose();
            _sectionVertices?.Dispose();
            _capSection?.Dispose();
            _stack?.Dispose();
        }

        /// <summary>
        /// Gives the numeric rooms back, once. For a snapshot on reserved address space this is its owner's to call,
        /// after the last reader of a look taken from it; one made on its own holds only managed arrays and need not be
        /// disposed. Nothing of a disposed snapshot is readable.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            IsBuilt = false;
            DisposeRooms();
        }

        /// <summary>Whether the numeric rooms stand on reserved address space.</summary>
        internal bool IsOnBacking => _branches.IsNative;

        /// <summary>
        /// Makes this snapshot's room at least <paramref name="room"/>, in place: every count that is short is taken
        /// together, in one step that either takes all of them or changes nothing a reader can see. On reserved
        /// address space the numeric rooms keep their base and what was written; the arrays that hold references are
        /// replaced by larger ones with what they held copied. What was built stays built and readable. False, with the
        /// room as it was, when a count is past the reservation, a commit is refused, memory cannot be had, or the chain
        /// depth differs (it is not grown).
        /// </summary>
        internal bool TryGrowTo(in VpMultiCutCapacities room, out string failure)
        {
            ThrowIfDisposed();
            failure = null;
            if (room.chainDepth > _capacities.chainDepth)
            {
                failure = "the chain depth is not grown";
                return false;
            }

            var target = new VpMultiCutCapacities(
                Math.Max(_capacities.branches, room.branches), Math.Max(_capacities.candidates, room.candidates),
                Math.Max(_capacities.renderFragments, room.renderFragments), Math.Max(_capacities.caps, room.caps),
                _capacities.chainDepth);
            var sizes = new RoomSizes(target);
            if (!sizes.FitsInt)
            {
                failure = "a derived size does not fit an int";
                return false;
            }

            // 1. Everything made available, nothing made usable: a refusal here leaves the snapshot as it was.
            VpClipCandidate[] candidates = _candidates;
            VpClipSelectionState[] states = _states;
            VpCapConstraint[] conditions = _conditions;
            VpMultiCutCap[] caps = _caps;
            Section[] sections = _sections;
            if (!_branches.TryPrepare(sizes.branches, out failure)
                || !_capIdentity.TryPrepare(sizes.candidates, out failure)
                || !_renderFragments.TryPrepare(sizes.renderFragments, out failure)
                || !_standsAs.TryPrepare(sizes.renderFragments, out failure)
                || !_sideIdentity.TryPrepare(sizes.renderFragments, out failure)
                || !_capVertices.TryPrepare((int)sizes.capVertices, out failure)
                || !_sectionVertices.TryPrepare((int)sizes.sectionVertices, out failure)
                || !_capSection.TryPrepare(sizes.caps, out failure)
                || !_stack.TryPrepare((int)sizes.stack, out failure))
            {
                return false;
            }

            try
            {
                if (target.candidates > _candidates.Length)
                {
                    candidates = new VpClipCandidate[target.candidates];
                    states = new VpClipSelectionState[target.candidates];
                }

                if (target.caps > _caps.Length)
                {
                    conditions = new VpCapConstraint[target.caps];
                    caps = new VpMultiCutCap[target.caps];
                    sections = new Section[target.caps];
                }
            }
            catch (OutOfMemoryException exception)
            {
                failure = "memory could not be had: " + exception.Message;
                return false;
            }

            // 2. Made usable; nothing below can fail.
            _branches.Grant(sizes.branches);
            _capIdentity.Grant(sizes.candidates);
            _renderFragments.Grant(sizes.renderFragments);
            _standsAs.Grant(sizes.renderFragments);
            _sideIdentity.Grant(sizes.renderFragments);
            _capVertices.Grant((int)sizes.capVertices);
            _sectionVertices.Grant((int)sizes.sectionVertices);
            _capSection.Grant(sizes.caps);
            _stack.Grant((int)sizes.stack);
            if (!ReferenceEquals(candidates, _candidates))
            {
                Array.Copy(_candidates, candidates, _candidateCount);
                Array.Copy(_states, states, _candidateCount);
                _candidates = candidates;
                _states = states;
                _selectedCandidates = new RangeList<VpClipCandidate>(_candidates);
                _selectedStates = new RangeList<VpClipSelectionState>(_states);
            }

            if (!ReferenceEquals(caps, _caps))
            {
                Array.Copy(_conditions, conditions, _conditionCount);
                Array.Copy(_caps, caps, _capCount);
                Array.Copy(_sections, sections, _sectionCount);
                _conditions = conditions;
                _caps = caps;
                _sections = sections;
            }

            _capacities = target;
            if (IsOnBacking)
            {
                PresizeForRoom(target.renderFragments);
            }

            return true;
        }

        /// <summary>
        /// What this snapshot's room is made of, in bytes: the managed arrays (those of the numeric rooms when they are
        /// managed, and the arrays that hold references), and the reserved and committed address space.
        /// </summary>
        internal VpRoomBytes RoomBytes()
        {
            var lines = new List<VpRoomLine>();
            DescribeRooms(lines, "snapshot");
            return VpRoomBytes.Of(lines);
        }

        /// <summary>Every array of this snapshot's room, one line each: the numeric rooms, then the arrays that hold references.</summary>
        internal void DescribeRooms(List<VpRoomLine> into, string owner)
        {
            into.Add(VpRoomLine.Of(owner + ".branches", _branches));
            into.Add(VpRoomLine.Of(owner + ".capIdentity", _capIdentity));
            into.Add(VpRoomLine.Of(owner + ".renderFragments", _renderFragments));
            into.Add(VpRoomLine.Of(owner + ".standsAs", _standsAs));
            into.Add(VpRoomLine.Of(owner + ".sideIdentity", _sideIdentity));
            into.Add(VpRoomLine.Of(owner + ".capVertices", _capVertices));
            into.Add(VpRoomLine.Of(owner + ".sectionVertices", _sectionVertices));
            into.Add(VpRoomLine.Of(owner + ".capSection", _capSection));
            into.Add(VpRoomLine.Of(owner + ".stack", _stack));
            into.Add(VpRoomLine.OfManaged(owner + ".candidates (ref)", _candidates));
            into.Add(VpRoomLine.OfManaged(owner + ".states", _states));
            into.Add(VpRoomLine.OfManaged(owner + ".conditions (ref)", _conditions));
            into.Add(VpRoomLine.OfManaged(owner + ".caps (ref)", _caps));
            into.Add(VpRoomLine.OfManaged(owner + ".sections (ref)", _sections));
            into.Add(VpRoomLine.OfManaged(owner + ".chain (ref)", _chain));
            into.Add(VpRoomLine.OfManaged(owner + ".checkCandidates (ref)", _checkCandidates));
            into.Add(VpRoomLine.OfManaged(owner + ".checkStates", _checkStates));

            // Kept per registration and per render fragment: what the builds remember (they grow with the count met).
            into.Add(VpRoomLine.OfManaged(owner + ".validated (per registration)", 2 + 24 + 64 + 64 + 4, _validatedHas.Length));
            into.Add(VpRoomLine.OfManaged(owner + ".placed (per render fragment)", 1 + 64, _placedHas.Length));
            into.Add(VpRoomLine.OfManaged(owner + ".reflected (ref)", IntPtr.Size, _reflected.Length));
            into.Add(VpRoomLine.OfManaged(owner + ".segments (per registration)", SegmentRecordBytes, _segOf.Length));
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(VpMultiCutSnapshot));
            }
        }

        private static VpArrayRange<Vector3> RangeOf(VpNumericRoom<Vector3> room, int start, int count)
        {
            return room.IsNative
                ? VpArrayRange<Vector3>.OfNative(room.BaseAddress, sizeof(float) * 3, room.Length, start, count)
                : new VpArrayRange<Vector3>(room.ManagedArray, start, count);
        }

        public VpMultiCutCapacities Capacities => _capacities;

        /// <summary>
        /// Which room the last build was short of, when it answered <see cref="VpMultiCutBuildOutcome.CapacityExceeded"/>;
        /// <see cref="VpMultiCutShortage.None"/> otherwise.
        /// </summary>
        public VpMultiCutShortage Shortage => _shortage;

        private VpMultiCutShortage _shortage;

        /// <summary>
        /// How many box-and-plane sections the last build actually took -- not reused from this build's own earlier caps
        /// or from the snapshot it was told to reuse from. Counted whether or not that build succeeded.
        /// </summary>
        public int SectionBuildCount { get; private set; }

        /// <summary>
        /// How often the structure has been settled here: the lineage walked, the candidates collected, what is
        /// Selected and Ignored decided, and the Ignored grouped. It rises once per build that reaches that work and
        /// not at all when only placements are put in place again, which is what makes the difference observable.
        /// </summary>
        public long StructureBuilds { get; private set; }

        /// <summary>
        /// How often the structural checks over the ledger have run -- the walk up every registration's origins, of
        /// which there are as many steps as the ledger has operations. It rises with <see cref="StructureBuilds"/>
        /// and, like it, not when placements alone are settled.
        /// </summary>
        public long StructureValidations { get; private set; }

        /// <summary>Root lookups the structural validation made to refuse two registrations of one root (in all; one per registration -- it was one comparison per pair of registrations), and the seconds the last structural validation took (2026-09-30: the coexistence run's Validate stage reached 185 ms).</summary>
        public long RootComparisons { get; private set; }
        public double LastStructureValidateSeconds { get; private set; }

        // Each registration's reflected boundaries, indexed once per structure build and read by its validation and
        // collection (2026-10-01: each ancestor's boundary used to be looked for from the start of the set, the square of
        // the depth per branch). Filled again by every build; nothing is carried from one build to the next.
        private VpReflectedIndex[] _reflected = System.Array.Empty<VpReflectedIndex>();
        private readonly VpReflectedIndex.Counts _reflectedCounts = new VpReflectedIndex.Counts();

        /// <summary>For tests: the reflected sets scanned from their start at every lookup, as before the index.</summary>
        internal bool reflectedByScanForTest;

        /// <summary>For tests: a display's own reflected set indexed again from its contents, as before 2026-10-01, instead of by its own lookup.</summary>
        internal bool reflectedIndexAgainForTest;

        /// <summary>The registrations' sets indexed again at a structure build, and a display's sets taken by their own lookup, over every build.</summary>
        public long ReflectedIndexesBuilt => _reflectedCounts.built;
        public long ReflectedIndexesReused => _reflectedCounts.reused;

        /// <summary>Whether any registration's set is still held after the last build (tests: nothing should be).</summary>
        internal bool HoldsReflectedForTest
        {
            get
            {
                foreach (VpReflectedIndex index in _reflected) if (index.HoldsForTest) return true;
                return false;
            }
        }

        /// <summary>Lookups of a reflected boundary, over every build of this snapshot.</summary>
        public long ReflectedLookups => _reflectedCounts.lookups;

        /// <summary>Items compared by the scan (only when <see cref="reflectedByScanForTest"/>).</summary>
        public long ReflectedComparisons => _reflectedCounts.comparisons;

        /// <summary>Boundaries entered into the indexes, over every build.</summary>
        public long ReflectedIndexEntries => _reflectedCounts.entries;

        /// <summary>The last structure build's time making the indexes (inside the validation's time).</summary>
        public double LastReflectedIndexSeconds { get; private set; }

        /// <summary>Every validation's parts, summed (never reset by a build).</summary>
        public VpValidateCounts ValidateCounts { get; } = new VpValidateCounts();

        /// <summary>The structural builds' Place passes: their counts and time, summed (never reset by a build; 2026-10-01).</summary>
        public VpPlaceCounts StructuralPlaceCounts { get; } = new VpPlaceCounts();

        /// <summary>The placement-only builds' Place passes, apart.</summary>
        public VpPlaceCounts PlacementOnlyPlaceCounts { get; } = new VpPlaceCounts();

        // The record the Place pass running now counts into.
        private VpPlaceCounts _placeInto;

        /// <summary>Tests only: a query's answer checked a second time once it is the placement (the pass before 2026-10-01's change).</summary>
        internal static bool placementCheckTwiceForTest;

        /// <summary>
        /// DIAGNOSIS ONLY, off by default (2026-10-01): each Place pass makes the same calls in three blocks, each timed whole
        /// -- every render fragment's placement query, then the checks of their answers, then everything built from them --
        /// into its record's providerSeconds, checkSeconds and restSeconds. The same outcome, the same first refusal and its
        /// reason; only on a refusal are the queries after the refused render fragment asked as well (they change nothing).
        /// </summary>
        public static bool PlacePhasedDiagnosis;

        // The diagnosis's answers, one a render fragment (made on its first use).
        private Matrix4x4[] _phasedPlacements = Array.Empty<Matrix4x4>();
        private bool[] _phasedAnswered = Array.Empty<bool>(), _phasedChecked = Array.Empty<bool>();

        /// <summary>Tests only: what render fragment <paramref name="index"/> stands as (the placement it asks for).</summary>
        internal (LogicalFragmentId fragment, CutOperationId operation, float side) StandsAsForTest(int index) => (_standsAs[index].fragment, _standsAs[index].operation, _standsAs[index].side);

        /// <summary>Tests only (a cost split): each render fragment's placement is asked and set, and nothing more is built from it.</summary>
        internal bool placeQueriesOnlyForTest;

        /// <summary>Tests only (a cost split): the planes and the clip are made, no section and no cap.</summary>
        internal bool placeNoCapsForTest;

        /// <summary>Tests only (a cost split): the sections are taken, no cap polygon is clipped.</summary>
        internal bool placeNoCapClipForTest;

        // The ledger's facts of each fragment an ancestor walk has read in this structural validation (2026-10-01): its
        // origin, side, the cut's source and plane, read once and kept for this validation only (the stamp tells this
        // validation's from an earlier one's). Each registration still makes its own checks on them.
        // Since 2026-10-01 they are the structure build's, opened by the validation and closed when the build's structure
        // part ends, and the Collect stage's chain walks (and the Group stage's root-chain checks) take what the build has
        // read from them, reading from the ledger only what it has not.
        private readonly VpLineageFacts _lineage = new VpLineageFacts();

        /// <summary>Tests only: every ancestor read from the ledger at every step (the walk before 2026-10-01), in the validation and the collection alike.</summary>
        internal bool lineageArraysOffForTest { get => _lineage.offForTest; set => _lineage.offForTest = value; }

        /// <summary>Tests only: the collection's chain walks read every ancestor from the ledger (the collection before 2026-10-01); the validation still keeps its facts.</summary>
        internal bool collectLineageOffForTest;

        // The fragment's origin (false: none) and, when it has one, whether its operation is known, with the cut's source and plane.
        private bool TryLineage(LogicalCutLedger ledger, LogicalFragmentId at, out CutOperationId origin, out float side, out bool operationKnown, out LogicalFragmentId source, out float4 plane)
        {
            return _lineage.TryGet(ledger, at, out origin, out side, out operationKnown, out source, out plane, ref ValidateCounts.ancestorReads, ref ValidateCounts.ancestorHits);
        }

        private void PrepareLineage(LogicalCutLedger ledger) => _lineage.Open(ledger);

        // Each registration root's chain as this structure build's validation walked and matched it (2026-10-01): the
        // boundaries from the root up, bottom first, the offsets of those its reflected set does not hold, whether they read
        // in admission order. Made by the validation's walk, taken in by the collections whose walk up reaches the root
        // (VpChainSegment), for this ledger, this build and that registration's own index only; the build number tells
        // this build's from an earlier one's, and the end of the structure part closes them (TryBuild's finally).
        private VpClipBoundary[] _segBoundaries = System.Array.Empty<VpClipBoundary>();
        private int[] _segUnreflected = System.Array.Empty<int>();
        private int _segCount, _segUnreflectedCount, _segBuild, _currentRegistration = -1;
        private LogicalCutLedger _segLedger;
        private (LogicalFragmentId root, int start, int length, int unreflectedStart, int unreflectedCount, bool ordered, int build)[] _segOf =
            System.Array.Empty<(LogicalFragmentId, int, int, int, int, bool, int)>();

        /// <summary>Tests only: no segment is taken in; every collection walks and matches its whole chain (the collection before 2026-10-01's second change).</summary>
        internal bool collectSegmentsOffForTest;

        // Estimated element sizes (x64: a boundary is a ledger reference, an operation id and a side, padded; a record seven
        // fields, padded): the bytes below are estimates from the element counts, which are exact.
        private const int SegmentBoundaryBytes = 24, SegmentRecordBytes = 32;

        /// <summary>The segments' element capacities now (boundaries, unreflected offsets, registrations).</summary>
        public (int boundaries, int unreflected, int registrations) SegmentCapacity => (_segBoundaries.Length, _segUnreflected.Length, _segOf.Length);

        /// <summary>The segments' arrays: their growths and the bytes those allocated, and the bytes held now (estimates, see above).</summary>
        public long SegmentGrowths { get; private set; }
        public long SegmentAllocatedBytes { get; private set; }
        public long SegmentHeldBytes => (long)_segBoundaries.Length * SegmentBoundaryBytes + (long)_segUnreflected.Length * sizeof(int)
            + (long)_segOf.Length * SegmentRecordBytes;

        private void OpenSegments(LogicalCutLedger ledger, int registrations)
        {
            _segBuild++;
            _segLedger = ledger;
            _segCount = 0;
            _segUnreflectedCount = 0;
            if (_segOf.Length < registrations)
            {
                int grown = System.Math.Max(registrations, _segOf.Length * 2);
                _segOf = new (LogicalFragmentId, int, int, int, int, bool, int)[grown];
                SegmentGrowths++;
                SegmentAllocatedBytes += (long)grown * SegmentRecordBytes;
            }
        }

        private void AddToSegment(VpClipBoundary boundary, bool reflected, int segmentStart)
        {
            if (_segCount == _segBoundaries.Length)
            {
                var more = new VpClipBoundary[System.Math.Max(256, _segBoundaries.Length * 2)];
                System.Array.Copy(_segBoundaries, more, _segCount);
                _segBoundaries = more;
                SegmentGrowths++;
                SegmentAllocatedBytes += (long)more.Length * SegmentBoundaryBytes;
            }

            if (!reflected)
            {
                if (_segUnreflectedCount == _segUnreflected.Length)
                {
                    var more = new int[System.Math.Max(64, _segUnreflected.Length * 2)];
                    System.Array.Copy(_segUnreflected, more, _segUnreflectedCount);
                    _segUnreflected = more;
                    SegmentGrowths++;
                    SegmentAllocatedBytes += (long)more.Length * sizeof(int);
                }

                _segUnreflected[_segUnreflectedCount++] = _segCount - segmentStart;
            }

            _segBoundaries[_segCount++] = boundary;
        }

        // The segment a collection of registration g may take in now, or none: this build's, for this ledger, made for g,
        // and asked with g's own index of this build.
        private VpChainSegment SegmentFor(LogicalCutLedger ledger, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            int g = _currentRegistration;
            if (collectSegmentsOffForTest || _segLedger == null || !ReferenceEquals(_segLedger, ledger) || (uint)g >= (uint)_segOf.Length
                || g >= _reflected.Length || !ReferenceEquals(reflected, _reflected[g]))
            {
                return default;
            }

            var s = _segOf[g];
            return s.build == _segBuild
                ? new VpChainSegment(s.root, _segBoundaries, s.start, s.length, _segUnreflected, s.unreflectedStart, s.unreflectedCount, s.ordered)
                : default;
        }

        // The parts of the validation running now, added to the sums when it ends (whichever way).
        private double _vIndex, _vInput, _vAncestors, _vOperations, _vContract;
        private long _vMark;

        private void Lap(ref double into)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            into += (now - _vMark) / (double)System.Diagnostics.Stopwatch.Frequency;
            _vMark = now;
        }

        /// <summary>The other stages' seconds in the last build that had them (the markers' own stages, timed here for a caller without the profiler): the branches' collection and the grouping, summed over the registrations, and the placements.</summary>
        public double LastCollectSeconds { get; private set; }
        public double LastGroupSeconds { get; private set; }
        public double LastPlaceSeconds { get; private set; }

        private static double SecondsSince(long begin) => (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>How often placements and what they decide have been settled, by either route.</summary>
        public long PlacementPasses { get; private set; }

        // The build's stages, each on a marker of its own (diagnosis: which stage a long snapshot spends in): the structural
        // checks, the branches' candidates and selection, the grouping into render fragments, and the placements with the
        // planes, clip and caps that follow from them.
        private static readonly Unity.Profiling.ProfilerMarker s_validate = new Unity.Profiling.ProfilerMarker("Zantetsu.Snapshot.Validate");
        private static readonly Unity.Profiling.ProfilerMarker s_collect = new Unity.Profiling.ProfilerMarker("Zantetsu.Snapshot.Collect");
        private static readonly Unity.Profiling.ProfilerMarker s_group = new Unity.Profiling.ProfilerMarker("Zantetsu.Snapshot.Group");
        private static readonly Unity.Profiling.ProfilerMarker s_place = new Unity.Profiling.ProfilerMarker("Zantetsu.Snapshot.Place");


        /// <summary>What made the last build <see cref="VpMultiCutBuildOutcome.InvalidInput"/>; <see cref="VpMultiCutInvalidInput.None"/> otherwise.</summary>
        public VpMultiCutInvalidInput InvalidInputReason => _invalid;

        /// <summary>
        /// This snapshot's own cap vertex array, for an upload that reads the first <see cref="CapVertexCount"/> of them
        /// by count. Not copied; good only until this snapshot is built again.
        /// </summary>
        internal VpNumericRoom<Vector3> CapVertexArray => _capVertices;

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

            return RangeOf(_capVertices, cap.vertexStart, cap.vertexCount);
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
            return RangeOf(_sectionVertices, slot * VpCapBoundsPolygon.MaxVertices, _sections[slot].vertexCount);
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

        /// <summary>
        /// What the ledger said one render fragment's side was when this structure was settled. False past the end.
        /// </summary>
        internal bool TryGetSideIdentity(int index, out VpMultiCutSideIdentity identity)
        {
            if ((uint)index >= (uint)_renderFragmentCount)
            {
                identity = default;
                return false;
            }

            identity = _sideIdentity[index];
            return true;
        }

        /// <summary>What the ledger said one candidate's cap was when this structure was settled. False past the end.</summary>
        internal bool TryGetCapIdentity(int index, out VpMultiCutCapIdentity identity)
        {
            if ((uint)index >= (uint)_candidateCount)
            {
                identity = default;
                return false;
            }

            identity = _capIdentity[index];
            return true;
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
            _shortage = VpMultiCutShortage.None;
            SectionBuildCount = 0;
            if (reuseFrom == this || (reuseFrom != null && !reuseFrom.IsBuilt))
            {
                reuseFrom = null;
            }

            // Decided with no room of this snapshot's, so that no shortage below can be what hides it.
            VpMultiCutBuildOutcome outcome;
            long structureBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                outcome = TryBuildStructure(ledger, registrations);
            }
            finally
            {
                // The registrations' sets are the build's: none is held past it (a registration retired, reordered or the
                // display ended is never looked up through a set of an earlier build). The lineage facts likewise: none is
                // read after the structure part, by placement or by a later build.
                for (int g = 0; g < _reflected.Length; g++) _reflected[g].Release();
                _lineage.Close();
                _segLedger = null;
                _currentRegistration = -1;
                ValidateCounts.structureSeconds += SecondsSince(structureBegin);
            }

            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                return Fail(outcome);
            }

            long placeBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            _placeInto = StructuralPlaceCounts;
            using (s_place.Auto())
            {
                outcome = TryApplyPlacements(ledger, registrations, placement, reuseFrom);
            }

            LastPlaceSeconds = SecondsSince(placeBegin);
            StructuralPlaceCounts.passes++;
            StructuralPlaceCounts.seconds += LastPlaceSeconds;

            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                return Fail(outcome);
            }

            _registrationCount = registrations.Count;
            IsBuilt = true;
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// Builds this snapshot by taking <paramref name="structure"/>'s settled structure as it stands and settling
        /// only what where things are decides: each render fragment's current placement, and from it the world planes,
        /// the clip and the caps. **The ledger's structure is not checked again, the lineage is not walked, the
        /// candidates are not collected again, and what is Selected or Ignored and how the Ignored were grouped are
        /// taken over unchanged.** What is copied across is copied, not recomputed.
        /// <para>
        /// The caller is the one that knows nothing of the structure has changed, and says so by calling this instead
        /// of <see cref="TryBuild(LogicalCutLedger, IReadOnlyList{VpMultiCutRegistration}, IVpFragmentPlacement)"/>.
        /// False with <see cref="VpMultiCutBuildOutcome.CapacityExceeded"/> if this snapshot cannot hold that
        /// structure, which a caller answers by building instead; a refusal otherwise leaves this snapshot unbuilt,
        /// exactly as a refused build does, so half-settled placements are never what a frame draws from.
        /// </para>
        /// </summary>
        internal VpMultiCutBuildOutcome TryBuildPlacementsFrom(
            VpMultiCutSnapshot structure,
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations,
            IVpFragmentPlacement placement)
        {
            if (structure == null)
            {
                throw new ArgumentNullException(nameof(structure));
            }

            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (registrations == null)
            {
                throw new ArgumentNullException(nameof(registrations));
            }

            if (structure == this || !structure.IsBuilt || structure._registrationCount != registrations.Count)
            {
                throw new InvalidOperationException(
                    "there is no settled structure to take, or it was settled over other registrations");
            }

            Clear();
            _buildGeneration++;
            _invalid = VpMultiCutInvalidInput.None;
            _shortage = VpMultiCutShortage.None;
            SectionBuildCount = 0;
            // The same validation the ordinary build makes, in the same order, with only the checks the settled
            // structure has already answered left out.
            VpMultiCutBuildOutcome checkedInputs;
            using (s_validate.Auto())
            {
                checkedInputs = Validate(ledger, registrations, true);
            }

            if (checkedInputs != VpMultiCutBuildOutcome.Built)
            {
                return Fail(checkedInputs);
            }

            if (structure._branchCount > _branches.Length)
            {
                return Fail(Short(VpMultiCutShortage.Branches));
            }

            if (structure._candidateCount > _candidates.Length || structure._candidateCount > _states.Length)
            {
                return Fail(Short(VpMultiCutShortage.Candidates));
            }

            if (structure._renderFragmentCount > _renderFragments.Length)
            {
                return Fail(Short(VpMultiCutShortage.RenderFragments));
            }

            _branches.CopyFrom(structure._branches, 0, 0, structure._branchCount);
            Array.Copy(structure._candidates, _candidates, structure._candidateCount);

            // What each candidate was decided to be -- Selected or Ignored -- is part of the structure, and the caps
            // are clipped by it. Leaving it behind would cut every cap by the wrong half-spaces.
            Array.Copy(structure._states, _states, structure._candidateCount);
            _capIdentity.CopyFrom(structure._capIdentity, 0, 0, structure._candidateCount);
            _renderFragments.CopyFrom(structure._renderFragments, 0, 0, structure._renderFragmentCount);
            _standsAs.CopyFrom(structure._standsAs, 0, 0, structure._renderFragmentCount);
            _sideIdentity.CopyFrom(structure._sideIdentity, 0, 0, structure._renderFragmentCount);
            _branchCount = structure._branchCount;
            _candidateCount = structure._candidateCount;
            _renderFragmentCount = structure._renderFragmentCount;

            VpMultiCutBuildOutcome placed;
            long placeBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            _placeInto = PlacementOnlyPlaceCounts;
            using (s_place.Auto())
            {
                placed = TryApplyPlacements(ledger, registrations, placement, structure);
            }

            LastPlaceSeconds = SecondsSince(placeBegin);
            PlacementOnlyPlaceCounts.passes++;
            PlacementOnlyPlaceCounts.seconds += LastPlaceSeconds;

            if (placed != VpMultiCutBuildOutcome.Built)
            {
                return Fail(placed);
            }

            _registrationCount = registrations.Count;
            IsBuilt = true;
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// Everything the ledger and the registrations' own identities decide, and nothing that where they stand
        /// does: the structural checks, the lineage walk, the candidates, what is Selected and what is Ignored, and
        /// which branches an aggregate groups together. Each render fragment is left knowing **which fragment it
        /// stands where** -- the placement itself is asked for in the other pass.
        /// </summary>
        private VpMultiCutBuildOutcome TryBuildStructure(
            LogicalCutLedger ledger, IReadOnlyList<VpMultiCutRegistration> registrations)
        {
            VpMultiCutBuildOutcome outcome;
            using (s_validate.Auto())
            {
                outcome = Validate(ledger, registrations, false);
            }

            if (outcome != VpMultiCutBuildOutcome.Built)
            {
                return outcome;
            }

            StructureBuilds++;
            LastCollectSeconds = 0.0;
            LastGroupSeconds = 0.0;
            for (int g = 0; g < registrations.Count; g++)
            {
                VpMultiCutRegistration registration = registrations[g];
                int branchStart = _branchCount;
                int candidateStart = _candidateCount;
                int renderFragmentStart = _renderFragmentCount;
                long collectBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                long chainSteps = VpClipCandidates.ChainSteps, operationReads = VpClipCandidates.OperationReads, candidatesMade = VpClipCandidates.CandidatesMade;
                long visits = VpClipCandidates.LineageVisits, reads = VpClipCandidates.LineageReads, hits = VpClipCandidates.LineageHits;
                long lookups = VpClipCandidates.ReflectedLookups, splices = VpClipCandidates.SegmentSplices, spliced = VpClipCandidates.SegmentBoundaries;
                _currentRegistration = g;
                using (s_collect.Auto())
                {
                    _inCollect = true;
                    try
                    {
                        outcome = TryCollectBranches(ledger, g, registration.root, _reflected[g]);
                    }
                    finally
                    {
                        _inCollect = false;
                    }

                    if (outcome == VpMultiCutBuildOutcome.Built)
                    {
                        long capBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                        for (int c = candidateStart; c < _candidateCount; c++)
                        {
                            _capIdentity[c] = CapIdentityOf(ledger, _candidates[c]);
                        }

                        ValidateCounts.capIdentitySeconds += SecondsSince(capBegin);
                        ValidateCounts.capIdentities += _candidateCount - candidateStart;
                    }
                }

                double collected = SecondsSince(collectBegin);
                LastCollectSeconds += collected;
                ValidateCounts.collectSeconds += collected;
                ValidateCounts.chainSteps += VpClipCandidates.ChainSteps - chainSteps;
                ValidateCounts.operationReads += VpClipCandidates.OperationReads - operationReads;
                ValidateCounts.candidatesMade += VpClipCandidates.CandidatesMade - candidatesMade;
                ValidateCounts.collectVisits += VpClipCandidates.LineageVisits - visits;
                ValidateCounts.collectReads += VpClipCandidates.LineageReads - reads;
                ValidateCounts.collectHits += VpClipCandidates.LineageHits - hits;
                ValidateCounts.collectLookups += VpClipCandidates.ReflectedLookups - lookups;
                ValidateCounts.collectSplices += VpClipCandidates.SegmentSplices - splices;
                ValidateCounts.collectSegmentBoundaries += VpClipCandidates.SegmentBoundaries - spliced;
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return outcome;
                }

                long groupBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                using (s_group.Auto())
                {
                    outcome = TryGroup(ledger, registration, g, branchStart, renderFragmentStart);
                }

                LastGroupSeconds += SecondsSince(groupBegin);
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return outcome;
                }
            }

            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// Everything where things stand decides: that each registration's own placement is one and that a section of
        /// its box there can be computed at all, then each render fragment's current placement and the world planes,
        /// clip and caps that follow from it.
        /// </summary>
        private VpMultiCutBuildOutcome TryApplyPlacements(
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations,
            IVpFragmentPlacement placement,
            VpMultiCutSnapshot reuseFrom)
        {
            PlacementPasses++;
            if (PlacePhasedDiagnosis && !placeQueriesOnlyForTest)
            {
                return TryApplyPlacementsPhased(ledger, registrations, placement, reuseFrom);
            }

            if (_placedHas.Length < _renderFragmentCount)
            {
                int room = Math.Max(_renderFragmentCount, _placedHas.Length * 2);
                Array.Resize(ref _placedHas, room);
                Array.Resize(ref _placedPassed, room);
            }

            for (int r = 0; r < _renderFragmentCount; r++)
            {
                VpMultiCutRenderFragment renderFragment = _renderFragments[r];
                VpMultiCutRegistration registration = registrations[renderFragment.registration];
                _placeInto.renderFragments++;
                if (!TryPlacementOf(
                        placement, registration, _standsAs[r], _placeInto, r, out Matrix4x4 geometryLocalToWorld))
                {
                    return Invalid(VpMultiCutInvalidInput.InputContract);
                }

                _renderFragments[r] = WithPlacement(renderFragment, geometryLocalToWorld);
                if (placeQueriesOnlyForTest) continue;
                VpMultiCutBuildOutcome outcome = TryBuildRenderFragment(ledger, registration, r, reuseFrom);
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return outcome;
                }
            }

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

        /// <summary>Room short, and which.</summary>
        private VpMultiCutBuildOutcome Short(VpMultiCutShortage which)
        {
            _shortage = which;
            return VpMultiCutBuildOutcome.CapacityExceeded;
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
            IReadOnlyList<VpMultiCutRegistration> registrations,
            bool structureAlreadySettled)
        {
            long validateBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            _vIndex = _vInput = _vAncestors = _vOperations = _vContract = 0.0;
            _vMark = validateBegin;
            try
            {
                return ValidateCore(ledger, registrations, structureAlreadySettled);
            }
            finally
            {
                if (structureAlreadySettled)
                {
                    ValidateCounts.placementOnly++;
                    ValidateCounts.placementRegistrations += registrations.Count;
                    ValidateCounts.placementInputSeconds += _vInput;
                    ValidateCounts.placementContractSeconds += _vContract;
                }
                else
                {
                    ValidateCounts.structural++;
                    ValidateCounts.registrations += registrations.Count;
                    ValidateCounts.indexSeconds += _vIndex;
                    ValidateCounts.inputSeconds += _vInput;
                    ValidateCounts.contractSeconds += _vContract;
                    ValidateCounts.ancestorSeconds += _vAncestors;
                    ValidateCounts.operationsSeconds += _vOperations;
                }

                if (!structureAlreadySettled) LastStructureValidateSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - validateBegin) / (double)System.Diagnostics.Stopwatch.Frequency;
            }
        }

        /// <summary>For tests: every registration is checked at every placement-only validation, as before 2026-10-04.</summary>
        internal static bool validateEveryRegistrationForTest;

        // What each registration passed its placement-only validation with, value for value: its bounds, placement, lineage
        // frame and epsilon. The contract checks and the section extent are functions of those values alone, so a
        // registration that comes again with the same values passes again and is not checked. Only what passed is kept.
        private bool[] _validatedHas = Array.Empty<bool>(), _validatedNow = Array.Empty<bool>();
        private Bounds[] _validatedBounds = Array.Empty<Bounds>();
        private Matrix4x4[] _validatedPlacement = Array.Empty<Matrix4x4>(), _validatedLineage = Array.Empty<Matrix4x4>();
        private float[] _validatedEpsilon = Array.Empty<float>();

        /// <summary>Observation: registrations a placement-only validation passed without checking them again.</summary>
        public long ValidationsRemembered { get; private set; }

        private void ValidatedRoom(int count)
        {
            if (_validatedHas.Length >= count) return;
            int room = Math.Max(count, _validatedHas.Length * 2);
            Array.Resize(ref _validatedHas, room);
            Array.Resize(ref _validatedNow, room);
            Array.Resize(ref _validatedBounds, room);
            Array.Resize(ref _validatedPlacement, room);
            Array.Resize(ref _validatedLineage, room);
            Array.Resize(ref _validatedEpsilon, room);
        }

        // Every element the same value (the matrix read by field, not through its indexer).
        private static bool SameValues(in Matrix4x4 a, in Matrix4x4 b) =>
            a.m00 == b.m00 && a.m01 == b.m01 && a.m02 == b.m02 && a.m03 == b.m03
            && a.m10 == b.m10 && a.m11 == b.m11 && a.m12 == b.m12 && a.m13 == b.m13
            && a.m20 == b.m20 && a.m21 == b.m21 && a.m22 == b.m22 && a.m23 == b.m23
            && a.m30 == b.m30 && a.m31 == b.m31 && a.m32 == b.m32 && a.m33 == b.m33;

        private static bool SameValues(Bounds a, Bounds b)
        {
            Vector3 ac = a.center, bc = b.center, ae = a.extents, be = b.extents;
            return ac.x == bc.x && ac.y == bc.y && ac.z == bc.z && ae.x == be.x && ae.y == be.y && ae.z == be.z;
        }

        private VpMultiCutBuildOutcome ValidateCore(
            LogicalCutLedger ledger,
            IReadOnlyList<VpMultiCutRegistration> registrations,
            bool structureAlreadySettled)
        {
            bool remember = structureAlreadySettled && !validateEveryRegistrationForTest;
            if (remember) ValidatedRoom(registrations.Count);
            for (int g = 0; g < registrations.Count; g++)
            {
                VpMultiCutRegistration registration = registrations[g];
                if (remember)
                {
                    _validatedNow[g] = _validatedHas[g]
                        && _validatedEpsilon[g] == registration.vertexEpsilon
                        && SameValues(_validatedPlacement[g], registration.geometryLocalToWorld)
                        && SameValues(_validatedLineage[g], registration.lineageToGeometryLocal)
                        && SameValues(_validatedBounds[g], registration.localBounds);
                    if (_validatedNow[g])
                    {
                        ValidationsRemembered++;
                        continue;
                    }
                }

                if (!IsWithinContract(registration.localBounds)
                    || !IsPlacement(registration.geometryLocalToWorld)
                    || !IsRigid(registration.lineageToGeometryLocal))
                {
                    return Invalid(VpMultiCutInvalidInput.InputContract);
                }
            }

            _vContract = (System.Diagnostics.Stopwatch.GetTimestamp() - _vMark) / (double)System.Diagnostics.Stopwatch.Frequency;
            Lap(ref _vInput);
            if (!structureAlreadySettled)
            {
                StructureValidations++;
                long indexBegin = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_reflected.Length < registrations.Count)
                {
                    int grown = System.Math.Max(registrations.Count, _reflected.Length * 2);
                    var more = new VpReflectedIndex[grown];
                    System.Array.Copy(_reflected, more, _reflected.Length);
                    for (int i = _reflected.Length; i < grown; i++) more[i] = new VpReflectedIndex();
                    _reflected = more;
                }

                long builtBefore = _reflectedCounts.built, reusedBefore = _reflectedCounts.reused;
                for (int g = 0; g < registrations.Count; g++)
                {
                    _reflected[g].Fill(registrations[g].reflected, reflectedByScanForTest, _reflectedCounts, reflectedIndexAgainForTest);
                }

                ValidateCounts.indexesBuilt += _reflectedCounts.built - builtBefore;
                ValidateCounts.indexesReused += _reflectedCounts.reused - reusedBefore;

                LastReflectedIndexSeconds = SecondsSince(indexBegin);
                PrepareLineage(ledger);
                OpenSegments(ledger, registrations.Count);
                Lap(ref _vIndex);
                _registrationOfRoot.Clear();
                _lineageOf.Clear();
                _rootsRegisteredTwice.Clear();
                for (int g = 0; g < registrations.Count; g++)
                {
                    // Two registrations of one root are refused below, in the order that check always had; the table
                    // notes here which roots are registered more than once, so that each registration asks once.
                    if (!_registrationOfRoot.ContainsKey(registrations[g].root)) _registrationOfRoot.Add(registrations[g].root, g);
                    else _rootsRegisteredTwice.Add(registrations[g].root);
                }
            }

            int steps = ledger.OperationCount + 1;
            for (int g = 0; g < registrations.Count; g++)
            {
                if (remember && _validatedNow[g])
                {
                    continue;   // the same values it passed with: the extent below is a function of them
                }

                VpMultiCutRegistration registration = registrations[g];
                if (!structureAlreadySettled && !ledger.TryGetFragmentState(registration.root, out _))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                // A section of this box, placed, can be computed in float at all, and every cap vertex it gives is
                // bounded. Where the box stands decides it, so it is asked again whenever that changes -- here, where
                // it has always been asked, so that which refusal comes first is what it always was.
                if (!VpSectionBounds.TryPlacedExtent(
                        registration.localBounds, registration.geometryLocalToWorld, registration.vertexEpsilon, out _))
                {
                    return Invalid(VpMultiCutInvalidInput.ConservativeSection);
                }

                if (structureAlreadySettled)
                {
                    // The rest of this is what the ledger and the lineage say, and it was settled when the structure
                    // was. Only these checks are skipped; the order of the ones that remain is untouched.
                    if (remember)
                    {
                        // It passed the contract above and the extent here: remembered with the values it passed with.
                        _validatedHas[g] = true;
                        _validatedBounds[g] = registration.localBounds;
                        _validatedPlacement[g] = registration.geometryLocalToWorld;
                        _validatedLineage[g] = registration.lineageToGeometryLocal;
                        _validatedEpsilon[g] = registration.vertexEpsilon;
                    }
                    else
                    {
                        Lap(ref _vInput);   // as before: the clock read once a registration
                    }

                    continue;
                }

                // No root on another's lineage: not the same root, and no other root above this one. Another registration
                // of this root is one the table noted (2026-09-30: each registration used to compare its root with every
                // other's, the square of the registrations; the refusal comes at the same registration as it did).
                RootComparisons++;
                if (_rootsRegisteredTwice.Contains(registration.root))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                Lap(ref _vInput);
                LogicalFragmentId at = registration.root;
                VpReflectedIndex reflectedOf = _reflected[g];
                int position = reflectedOf.Count - 1;   // a display's set holds the k-th ancestor up at the k-th from its end
                int segmentStart = _segCount, unreflectedStart = _segUnreflectedCount, below = int.MaxValue;
                bool ordered = true;
                for (int step = 0; ; step++)
                {
                    if (step > steps)
                    {
                        return Invalid(VpMultiCutInvalidInput.Lineage);
                    }

                    ValidateCounts.ancestorSteps++;
                    if (!TryLineage(ledger, at, out CutOperationId origin, out float side, out bool operationKnown, out LogicalFragmentId source, out float4 plane))
                    {
                        break;
                    }

                    if (!operationKnown)
                    {
                        return Invalid(VpMultiCutInvalidInput.Lineage);
                    }

                    // A boundary above the root the geometry does not reflect is a candidate of every branch below. This
                    // registration's own set is asked, at every ancestor.
                    ValidateCounts.ancestorLookups++;
                    var boundary = new VpClipBoundary(new VpCapFace(ledger, origin), side);
                    bool held = reflectedOf.ContainsAt(position--, boundary);
                    AddToSegment(boundary, held, segmentStart);
                    if (origin.value >= below) ordered = false;   // from the top down each must come after the one above
                    below = origin.value;
                    if (!held)
                    {
                        VpMultiCutBuildOutcome planed = CheckPlane(plane, registration);
                        if (planed != VpMultiCutBuildOutcome.Built)
                        {
                            return planed;
                        }
                    }

                    at = source;
                    if (_registrationOfRoot.TryGetValue(at, out int above) && above != g)
                    {
                        return Invalid(VpMultiCutInvalidInput.Lineage);
                    }
                }

                _segOf[g] = (registration.root, segmentStart, _segCount - segmentStart, unreflectedStart, _segUnreflectedCount - unreflectedStart, ordered, _segBuild);
                ValidateCounts.segmentEntries += _segCount - segmentStart;
                Lap(ref _vAncestors);
            }

            if (remember) Lap(ref _vInput);   // the registrations' input checks, timed once for all of them

            if (!structureAlreadySettled)
            {
                // Everything below reads the ledger and nothing else: every operation in the order it was
                // admitted, and for each one the lineage of the fragment it cut. It is as structural as the walk
                // above, and is skipped for the same reason -- a settled structure has answered it already.
                for (int position = 0; ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
                {
                    ValidateCounts.operations++;
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
                                ledger, operation.source, _reflected[g], steps, out int unreflected);
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
            }

            Lap(ref _vOperations);
            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// One plane of the lineage: carried by the mapping and the placement as the build carries it, and, in the
        /// geometry's coordinates the section is taken in, within the section bounds of the registration's box.
        /// </summary>
        private VpMultiCutBuildOutcome CheckPlane(float4 plane, in VpMultiCutRegistration registration)
        {
            ValidateCounts.planeChecks++;
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
            _lineageWalk.Clear();
            ValidateCounts.ownerLookups++;
            for (int step = 0; step <= steps; step++)
            {
                ValidateCounts.ownerSteps++;
                // Answered before, for this fragment or one below it on the same chain: the answer is the same.
                if (_lineageOf.TryGetValue(at, out int known))
                {
                    ValidateCounts.ownerCacheHits++;
                    registration = known;
                    return Remember(registration);
                }

                if (_registrationOfRoot.TryGetValue(at, out int g))
                {
                    registration = g;
                    return Remember(registration);
                }

                _lineageWalk.Add(at);
                if (!ledger.TryGetOrigin(at, out CutOperationId origin, out _))
                {
                    return Remember(-1);
                }

                if (!ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                }

                at = cut.source;
            }

            return Invalid(VpMultiCutInvalidInput.Lineage);
        }

        // Every fragment walked on the way to an answer has that answer; an invalid lineage is never remembered (it ends
        // the validation at once).
        private VpMultiCutBuildOutcome Remember(int registration)
        {
            for (int i = 0; i < _lineageWalk.Count; i++)
            {
                _lineageOf[_lineageWalk[i]] = registration;
            }

            _lineageWalk.Clear();
            return VpMultiCutBuildOutcome.Built;
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
                ValidateCounts.unreflectedSteps++;
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
            if (set is VpReflectedIndex index)
            {
                return index.Contains(boundary);
            }

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
                            // The cut these two sides belong to is the one just read. It goes with them.
                            VpMultiCutBuildOutcome positive = TryAddBranch(ledger, registration, at, 1f, pending, reflected);
                            if (positive != VpMultiCutBuildOutcome.Built)
                            {
                                return positive;
                            }

                            VpMultiCutBuildOutcome negative = TryAddBranch(ledger, registration, at, -1f, pending, reflected);
                            if (negative != VpMultiCutBuildOutcome.Built)
                            {
                                return negative;
                            }
                        }
                        else
                        {
                            VpMultiCutBuildOutcome whole = TryAddBranch(ledger, registration, at, 0f, default, reflected);
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
                            return Short(VpMultiCutShortage.Branches);
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
            CutOperationId pendingOperation, IReadOnlyCollection<VpClipBoundary> reflected)
        {
            if (_branchCount >= _branches.Length)
            {
                return Short(VpMultiCutShortage.Branches);
            }

            VpMultiCutBuildOutcome collected = Collect(
                ledger, fragment, pendingSide, true, reflected, _candidates, _candidateCount, out int count);
            if (collected != VpMultiCutBuildOutcome.Built)
            {
                return collected;
            }

            long selectBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            int selected = VpClipCandidates.Select(_candidates, _candidateCount, count, _states);
            if (_inCollect) { ValidateCounts.selectSeconds += SecondsSince(selectBegin); ValidateCounts.branches++; }
            _branches[_branchCount++] = new VpMultiCutBranch(
                registration, fragment, pendingSide, pendingOperation, _candidateCount, count, selected, -1);
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

            long selectBegin = System.Diagnostics.Stopwatch.GetTimestamp();
            int selected = VpClipCandidates.Select(_checkCandidates, 0, count, _checkStates);
            if (_inCollect) { ValidateCounts.selectSeconds += SecondsSince(selectBegin); ValidateCounts.branches++; }
            return selected < count ? VpMultiCutBuildOutcome.RetiredInsideAggregate : VpMultiCutBuildOutcome.Built;
        }

        // Whether the Collect stage is running: its chains, selections and branches are counted as its own (the Group
        // stage's root-chain checks collect too, and are not).
        private bool _inCollect;

        private VpMultiCutBuildOutcome Collect(
            LogicalCutLedger ledger, LogicalFragmentId fragment, float pendingSide, bool requireLive,
            IReadOnlyCollection<VpClipBoundary> reflected, VpClipCandidate[] into, int start, out int count)
        {
            long begin = _inCollect ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            VpClipCandidates.CollectOutcome collectedOutcome = VpClipCandidates.CollectInto(
                ledger, fragment, pendingSide, requireLive, reflected, _chain, into, start, into.Length - start, out count,
                collectLineageOffForTest ? null : _lineage, SegmentFor(ledger, reflected));
            if (_inCollect)
            {
                ValidateCounts.collectIntoSeconds += SecondsSince(begin);
                ValidateCounts.collectCalls++;
            }

            switch (collectedOutcome)
            {
                case VpClipCandidates.CollectOutcome.Collected:
                    return VpMultiCutBuildOutcome.Built;
                case VpClipCandidates.CollectOutcome.NotCollectable:
                    return Invalid(VpMultiCutInvalidInput.Lineage);
                case VpClipCandidates.CollectOutcome.CandidateOverflow when into == _candidates:
                    return Short(VpMultiCutShortage.Candidates);
                default:
                    // The chain itself, or a chain read only to be checked, whose room is the chain depth's.
                    return Short(VpMultiCutShortage.ChainDepth);
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
            int renderFragmentStart)
        {
            IReadOnlyCollection<VpClipBoundary> reflected = _reflected[registrationIndex];
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
                    return Short(VpMultiCutShortage.RenderFragments);
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
                // Which fragment this one stands where is settled here, with the rest of the structure. **Where that
                // fragment is** is asked in the other pass, and asked again whenever anything moves.
                // The branch itself, whole: which fragment, which accepted cut, which side of it. An aggregate is
                // put where its first living branch is, and that branch's side is the side that stands there -- the
                // root's side, recorded below for the display, belongs to another fragment and is not asked here.
                _standsAs[_renderFragmentCount] =
                    new VpMultiCutStandsAs(branch.fragment, branch.pendingOperation, branch.pendingSide);
                _sideIdentity[_renderFragmentCount] = SideIdentityOf(ledger, registration.root, root, rootPendingSide);
                _renderFragments[_renderFragmentCount] = new VpMultiCutRenderFragment(
                    registrationIndex, registration.localBounds, Matrix4x4.identity, root, rootPendingSide,
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
        // The diagnosis's pass (PlacePhasedDiagnosis): the queries, the checks and the builds as three blocks. The first render
        // fragment refused by its query or check is the one the ordinary pass refuses; the builds before it run as they do
        // there, so a build refused earlier is still the refusal; nothing after it is built.
        private VpMultiCutBuildOutcome TryApplyPlacementsPhased(
            LogicalCutLedger ledger, IReadOnlyList<VpMultiCutRegistration> registrations, IVpFragmentPlacement placement, VpMultiCutSnapshot reuseFrom)
        {
            int n = _renderFragmentCount;
            if (_phasedPlacements.Length < n)
            {
                _phasedPlacements = new Matrix4x4[n];
                _phasedAnswered = new bool[n];
                _phasedChecked = new bool[n];
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int refusedAt = n;
            for (int r = 0; r < n; r++)
            {
                VpMultiCutRenderFragment renderFragment = _renderFragments[r];
                _placeInto.renderFragments++;
                _phasedAnswered[r] = TryQueryPlacement(placement, registrations[renderFragment.registration], _standsAs[r], _placeInto, out _phasedPlacements[r], out _phasedChecked[r]);
                if (!_phasedAnswered[r] && refusedAt == n) refusedAt = r;
            }

            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int r = 0; r < refusedAt; r++)
            {
                if (!_phasedChecked[r]) continue;
                _placeInto.placementChecks++;
                if (!IsPlacement(_phasedPlacements[r]))
                {
                    refusedAt = r;
                    break;
                }
            }

            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            VpMultiCutBuildOutcome outcome = VpMultiCutBuildOutcome.Built;
            for (int r = 0; r < refusedAt; r++)
            {
                VpMultiCutRenderFragment renderFragment = _renderFragments[r];
                _renderFragments[r] = WithPlacement(renderFragment, _phasedPlacements[r]);
                outcome = TryBuildRenderFragment(ledger, registrations[renderFragment.registration], r, reuseFrom);
                if (outcome != VpMultiCutBuildOutcome.Built) break;
            }

            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            double f = 1.0 / System.Diagnostics.Stopwatch.Frequency;
            _placeInto.providerSeconds += (t1 - t0) * f;
            _placeInto.checkSeconds += (t2 - t1) * f;
            _placeInto.restSeconds += (t3 - t2) * f;
            if (outcome != VpMultiCutBuildOutcome.Built) return outcome;
            return refusedAt < n ? Invalid(VpMultiCutInvalidInput.InputContract) : VpMultiCutBuildOutcome.Built;
        }

        // A render fragment's placement as its query answers it, and whether that answer is to be checked: the
        // registration's own with no provider (unchecked, as TryPlacementOf has it); false when it is Missing.
        private static bool TryQueryPlacement(
            IVpFragmentPlacement placement, in VpMultiCutRegistration registration, in VpMultiCutStandsAs stands,
            VpPlaceCounts counts, out Matrix4x4 baseline, out bool check)
        {
            check = false;
            if (placement == null)
            {
                baseline = registration.geometryLocalToWorld;
                return true;
            }

            counts.queries++;
            VpFragmentPlacementKind kind = placement.TryGetGeometryLocalToWorld(
                stands.fragment, stands.operation, stands.side, out Matrix4x4 followed);
            if (kind == VpFragmentPlacementKind.Following) counts.following++;
            else if (kind == VpFragmentPlacementKind.Static) counts.staticPlacements++;
            if (kind == VpFragmentPlacementKind.Missing)
            {
                baseline = default;
                return false;
            }

            baseline = kind == VpFragmentPlacementKind.Following ? followed : registration.geometryLocalToWorld;
            check = true;
            return true;
        }

        /// <summary>For tests: a Static placement is checked again where it is placed, as before 2026-10-04.</summary>
        internal static bool placementCheckStaticForTest;

        /// <summary>For tests: a Following placement is checked at every build, as before 2026-10-04.</summary>
        internal static bool placementCheckEveryFollowingForTest;

        // The Following placement each render fragment last passed the placement check with, value for value. The check is
        // a function of the matrix alone, so the same matrix passes again and is not checked. Only what passed is kept.
        private bool[] _placedHas = Array.Empty<bool>();
        private Matrix4x4[] _placedPassed = Array.Empty<Matrix4x4>();

        /// <summary>Observation: Following placements passed without being checked again.</summary>
        public long PlacementChecksRemembered { get; private set; }

        private bool TryPlacementOf(
            IVpFragmentPlacement placement, in VpMultiCutRegistration registration, in VpMultiCutStandsAs stands,
            VpPlaceCounts counts, int r, out Matrix4x4 geometryLocalToWorld)
        {
            if (placement == null)
            {
                // Nothing follows anything here: the registration's placement is what everything of it is drawn at.
                geometryLocalToWorld = registration.geometryLocalToWorld;
                return true;
            }

            counts.queries++;
            VpFragmentPlacementKind kind = placement.TryGetGeometryLocalToWorld(
                stands.fragment, stands.operation, stands.side, out Matrix4x4 followed);
            if (kind == VpFragmentPlacementKind.Following) counts.following++;
            else if (kind == VpFragmentPlacementKind.Static) counts.staticPlacements++;
            if (kind == VpFragmentPlacementKind.Missing)
            {
                // It follows something and where was not said. Drawing it where it was registered would draw it where
                // it used to be, so nothing is drawn from this input at all.
                geometryLocalToWorld = default;
                return false;
            }

            Matrix4x4 baseline = kind == VpFragmentPlacementKind.Following ? followed : registration.geometryLocalToWorld;
            if (kind != VpFragmentPlacementKind.Following && !placementCheckStaticForTest)
            {
                // A Static placement is the registration's own matrix, and every build validates each registration's
                // placement before any render fragment is placed (ValidateCore: IsPlacement of this same matrix). It is
                // not checked a second time (2026-10-04).
                geometryLocalToWorld = baseline;
                return true;
            }

            if (!placementCheckEveryFollowingForTest && _placedHas[r] && SameValues(_placedPassed[r], baseline))
            {
                // The matrix that passed this check for this render fragment before, value for value (2026-10-04).
                PlacementChecksRemembered++;
                geometryLocalToWorld = baseline;
                return true;
            }

            counts.placementChecks++;
            if (!IsPlacement(baseline))
            {
                geometryLocalToWorld = default;
                return false;
            }

            _placedHas[r] = true;
            _placedPassed[r] = baseline;

            // The checked baseline is the placement as it is: checked once (2026-10-01; it was checked again here, the same
            // matrix, the same answer).
            geometryLocalToWorld = baseline;
            if (placementCheckTwiceForTest)
            {
                counts.placementChecks++;
                return IsPlacement(geometryLocalToWorld);
            }

            return true;
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
                return Short(VpMultiCutShortage.Caps);
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

            _placeInto.selected += selected;
            _placeInto.planeTransforms += 2 * selected;
            _selectedHalfSpaces.Set(0, selected);
            if (!VpInstanceClip.TryKeep(_selectedHalfSpaces, out VpInstanceClip clip))
            {
                return Invalid(VpMultiCutInvalidInput.ClipNotTaken);
            }

            _placeInto.clipsKept++;
            if (placeNoCapsForTest)
            {
                _renderFragments[index] = new VpMultiCutRenderFragment(
                    renderFragment.registration, renderFragment.localBounds, renderFragment.geometryLocalToWorld,
                    renderFragment.root, renderFragment.rootPendingSide, renderFragment.aggregated,
                    renderFragment.branchStart, renderFragment.branchCount, conditionStart, selected, clip, _capCount, 0);
                return VpMultiCutBuildOutcome.Built;
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
                if (initial > 0 && !placeNoCapClipForTest)
                {
                    _placeInto.capClips++;
                    _placeInto.capInputVertices += initial;
                }

                if (initial > 0 && !placeNoCapClipForTest
                    && !VpCapPolygonClip.TryClip(
                        _initial, initial, boundary, _selectedCandidates, _selectedStates, _selectedPlanes,
                        vertexEpsilon, _clipped, out clipped))
                {
                    return Invalid(VpMultiCutInvalidInput.ClipNotTaken);
                }

                _placeInto.capOutputVertices += clipped;
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
            int found = FindSection(face, localPlane, registration, geometryLocalToWorld, _placeInto);
            if (found >= 0)
            {
                _placeInto.sectionsFoundHere++;
                sectionSlot = found;
                vertexCount = _sections[found].vertexCount;
                _sectionVertices.CopyTo(found * stride, _initial, 0, vertexCount);
                return VpMultiCutBuildOutcome.Built;
            }

            if (_sectionCount >= _sections.Length)
            {
                return Short(VpMultiCutShortage.Caps);
            }

            int slot = _sectionCount;
            int reused = reuseFrom != null ? reuseFrom.FindSection(face, localPlane, registration, geometryLocalToWorld, _placeInto) : -1;
            if (reused >= 0)
            {
                _placeInto.sectionsReused++;
                vertexCount = reuseFrom._sections[reused].vertexCount;
                _sectionVertices.CopyFrom(reuseFrom._sectionVertices, reused * stride, slot * stride, vertexCount);
            }
            else
            {
                SectionBuildCount++;
                _placeInto.sectionsBuilt++;
                // Taken into the work room (at most a section's six vertices), then kept in the sections' own room.
                if (!_section.TryBuild(
                        registration.localBounds, localPlane, geometryLocalToWorld,
                        registration.vertexEpsilon, _initial, 0, out vertexCount, out _))
                {
                    return Invalid(VpMultiCutInvalidInput.SectionNotTaken);
                }

                _sectionVertices.CopyFrom(_initial, 0, slot * stride, vertexCount);
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
            _sectionVertices.CopyTo(slot * stride, _initial, 0, vertexCount);
            return VpMultiCutBuildOutcome.Built;
        }

        private int FindSection(
            VpCapFace face, float4 localPlane, in VpMultiCutRegistration registration, Matrix4x4 geometryLocalToWorld, VpPlaceCounts counts)
        {
            for (int i = 0; i < _sectionCount; i++)
            {
                counts.sectionEntriesCompared++;
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

        /// <summary>The same render fragment, standing where it now stands.</summary>
        private static VpMultiCutRenderFragment WithPlacement(in VpMultiCutRenderFragment f, Matrix4x4 geometryLocalToWorld)
        {
            return new VpMultiCutRenderFragment(
                f.registration, f.localBounds, geometryLocalToWorld, f.root, f.rootPendingSide, f.aggregated,
                f.branchStart, f.branchCount, f.conditionStart, f.conditionCount, f.clip, f.capStart, f.capCount);
        }

        /// <summary>
        /// What the ledger says one render fragment's side is. The pending case asks for the root's active operation;
        /// the published case asks which cut made that root. Both are facts about the structure, which is why they are
        /// settled once here rather than asked again every frame.
        /// </summary>
        private static VpMultiCutSideIdentity SideIdentityOf(
            LogicalCutLedger ledger, LogicalFragmentId registrationRoot, LogicalFragmentId root, float rootPendingSide)
        {
            if (rootPendingSide != 0f && ledger.TryGetActiveOperation(root, out CutOperationId pending))
            {
                return new VpMultiCutSideIdentity(
                    pending, rootPendingSide, false, IsFixedSide(ledger, pending, rootPendingSide));
            }

            if (root != registrationRoot && ledger.TryGetOrigin(root, out CutOperationId origin, out float side))
            {
                return new VpMultiCutSideIdentity(origin, side, true, IsFixedSide(ledger, origin, side));
            }

            return new VpMultiCutSideIdentity(default, 0f, false, false);
        }

        /// <summary>What the ledger says one candidate's cap is: published or not, the child it made, and fixed or not.</summary>
        private static VpMultiCutCapIdentity CapIdentityOf(LogicalCutLedger ledger, in VpClipCandidate candidate)
        {
            CutOperationId operation = candidate.boundary.face.operation;
            float side = candidate.boundary.side;
            bool published = !candidate.pending;
            LogicalFragmentId child = default;
            if (published && ledger.TryGetOperation(operation, out LogicalCutOperation cut))
            {
                child = side > 0f ? cut.positive : cut.negative;
            }

            return new VpMultiCutCapIdentity(published, child, IsFixedSide(ledger, operation, side));
        }

        /// <summary>Whether one side of one cut is fixed by the anchors its publication settled.</summary>
        private static bool IsFixedSide(LogicalCutLedger ledger, CutOperationId operation, float side)
        {
            return ledger.TryGetSettledAnchorDistribution(operation, out AnchorDistributionResult distribution)
                && FixedSupportAnchors.IsFixed(side > 0f ? distribution.positiveCount : distribution.negativeCount);
        }

        /// <summary>
        /// The cut one render fragment is a side of, as the ledger says: which operation, which of its two sides,
        /// whether that side is published, and whether it is fixed by its anchors.
        /// <para>
        /// Every part of this is a fact about the ledger and the lineage, so it is settled with the structure and
        /// carried with it. It is not settled here to save reading: asking where a fragment came from is one read of
        /// that fragment (<see cref="LogicalCutLedger.TryGetOrigin"/>). It is settled here because it is **structure**
        /// -- what a render fragment is, as a side -- and a frame that did not settle the structure again does not
        /// settle this again either.
        /// </para>
        /// </summary>
        internal readonly struct VpMultiCutSideIdentity
        {
            internal VpMultiCutSideIdentity(CutOperationId operation, float side, bool published, bool fixedByAnchors)
            {
                this.operation = operation;
                this.side = side;
                this.published = published;
                this.fixedByAnchors = fixedByAnchors;
            }

            internal readonly CutOperationId operation;
            internal readonly float side;
            internal readonly bool published;
            internal readonly bool fixedByAnchors;
        }

        /// <summary>
        /// Who a render fragment stands where: the branch that puts it there. A branch drawn whole names only its
        /// fragment; one that is a side of an accepted cut names that cut and that side as well, so that a caller
        /// which keeps something per side of an accepted cut can be asked about the right one.
        /// </summary>
        internal readonly struct VpMultiCutStandsAs
        {
            internal VpMultiCutStandsAs(LogicalFragmentId fragment, CutOperationId operation, float side)
            {
                this.fragment = fragment;
                this.operation = operation;
                this.side = side;
            }

            internal readonly LogicalFragmentId fragment;
            internal readonly CutOperationId operation;
            internal readonly float side;
        }

        /// <summary>
        /// What one candidate's cap is, as the ledger says: whether its boundary is published, which child that
        /// publication made on this side, and whether that side is fixed. Settled and carried like a side's identity,
        /// and for the same reason.
        /// </summary>
        internal readonly struct VpMultiCutCapIdentity
        {
            internal VpMultiCutCapIdentity(bool published, LogicalFragmentId child, bool fixedByAnchors)
            {
                this.published = published;
                this.child = child;
                this.fixedByAnchors = fixedByAnchors;
            }

            internal readonly bool published;
            internal readonly LogicalFragmentId child;
            internal readonly bool fixedByAnchors;
        }

        private static VpMultiCutBranch WithRenderFragment(in VpMultiCutBranch b, int renderFragment)
        {
            return new VpMultiCutBranch(
                b.registration, b.fragment, b.pendingSide, b.pendingOperation, b.candidateStart, b.candidateCount,
                b.selectedCount, renderFragment);
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

        /// <summary>Tests only: the placement check a query's answer is given.</summary>
        internal static bool IsPlacementForTest(Matrix4x4 m) => IsPlacement(m);

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

        /// <summary>For tests: the elements read through the matrix's indexer, as before 2026-10-01.</summary>
        internal static bool finiteByIndexerForTest;

        // Every one of the 16 elements neither NaN nor infinite. Read by field (2026-10-01: the indexer's reads were most
        // of the registrations' contract, which every validation checks); the same test of the same elements.
        internal static bool IsFiniteMatrix(Matrix4x4 m)
        {
            if (finiteByIndexerForTest)
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

            return math.all(math.isfinite(new float4(m.m00, m.m10, m.m20, m.m30)))
                && math.all(math.isfinite(new float4(m.m01, m.m11, m.m21, m.m31)))
                && math.all(math.isfinite(new float4(m.m02, m.m12, m.m22, m.m32)))
                && math.all(math.isfinite(new float4(m.m03, m.m13, m.m23, m.m33)));
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
