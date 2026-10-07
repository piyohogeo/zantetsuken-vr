using System;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The caps' shapes as structure (2026-10-07, DESIGN 5.6, D-208). A cap's shape -- the section of its boundary's
    /// plane through the registration's box, cut by the render fragment's other selected half-spaces -- follows from
    /// the structure alone: the selected boundaries, the lineage's mapping into the geometry's frame, the box and the
    /// vertex epsilon. It is made once, **in the geometry's own local frame**, when the structure it belongs to is
    /// made, and kept with that structure: a registration's part holds its render fragments' caps, both snapshots read
    /// them through the part, and they live as long as the part does (a part taken again is made again). Where the
    /// render fragment stands plays no part in the shape: a placement pass carries the kept planes and vertices by the
    /// placement and neither looks a section up, builds one, nor clips a polygon.
    /// </summary>
    public sealed partial class VpMultiCutSnapshot
    {
        internal sealed class LocalCapSet
        {
            internal struct Cap
            {
                internal float4 localPlane;       // the boundary's plane in the geometry's local frame
                internal VpClipBoundary boundary;  // its face and the side this cap closes
                internal int initialStart, initialCount;   // the section before the other planes cut it: 0 to 6, in the order it was built
                internal int vertexStart, vertexCount;     // the cap: 0 to 14, ordered for the side it closes (outward -side * n)
            }

            // The caps of each render fragment of the set are its representative's selected boundaries, in their order.
            internal int[] firstOf = new int[2];
            internal Cap[] caps = new Cap[4];
            internal Vector3[] vertices = new Vector3[40];   // local
            internal int capCount, vertexCount;

            internal void Clear()
            {
                capCount = 0;
                vertexCount = 0;
            }
        }

        // The shapes of a snapshot that is not made of parts (the builder that settles the structure in every build):
        // made with that build, in the placement pass, and gone with the next.
        private readonly LocalCapSet _ownShapes = new LocalCapSet();
        private int _ownShapesRegistration = -1, _ownShapesFrom;
        private RangeList<float4> _localSelectedPlanes;

        /// <summary>Observation: caps whose shape was made (the section taken, the polygon cut), and the local vertices kept for them.</summary>
        internal long CapShapesBuilt { get; private set; }
        internal long CapShapeVertices { get; private set; }

        /// <summary>
        /// The shapes of one render fragment's caps, appended to <paramref name="set"/> as its fragment
        /// <paramref name="fragment"/>: for each selected boundary of its representative branch, the plane in the
        /// geometry's local frame, the section of that plane through the box, and the section cut by the other selected
        /// half-spaces -- all in local coordinates, with the vertex epsilon in the local units it is defined in: the
        /// section's vertices and the cut polygon's neighbours are merged by their distance in this frame (D-209), so
        /// the shape does not depend on where its render fragment stands, scaled or not.
        /// </summary>
        private VpMultiCutBuildOutcome TryAppendLocalCaps(
            LocalCapSet set, int fragment, int shareFrom, in VpMultiCutRegistration registration, in VpMultiCutBranch representative,
            VpClipCandidate[] candidates, VpClipSelectionState[] states, VpPlaceCounts counts)
        {
            if (set.firstOf.Length <= fragment)
            {
                Array.Resize(ref set.firstOf, Math.Max(fragment + 1, set.firstOf.Length * 2));
            }

            set.firstOf[fragment] = set.capCount;
            int selected = representative.selectedCount;
            if (selected == 0)
            {
                return VpMultiCutBuildOutcome.Built;
            }

            if (set.caps.Length < set.capCount + selected)
            {
                Array.Resize(ref set.caps, Math.Max(set.capCount + selected, set.caps.Length * 2));
            }

            int room = set.vertexCount + selected * (VpCapBoundsPolygon.MaxVertices + _clipped.Length);
            if (set.vertices.Length < room)
            {
                Array.Resize(ref set.vertices, Math.Max(room, set.vertices.Length * 2));
            }

            for (int j = 0; j < selected; j++)
            {
                VpClipCandidate candidate = candidates[representative.candidateStart + j];
                if (!VpCutPlane.TryGeometryLocalToWorld(candidate.plane, registration.lineageToGeometryLocal, out float4 local))
                {
                    return Invalid(VpMultiCutInvalidInput.PlaneNotCarried);
                }

                _localPlanes[j] = local;
            }

            counts.planeTransforms += selected;
            _selectedCandidates.Use(candidates);
            _selectedStates.Use(states);
            _selectedCandidates.Set(representative.candidateStart, selected);
            _selectedStates.Set(representative.candidateStart, selected);
            _localSelectedPlanes ??= new RangeList<float4>(_localPlanes);
            _localSelectedPlanes.Use(_localPlanes);
            _localSelectedPlanes.Set(0, selected);
            for (int j = 0; j < selected; j++)
            {
                VpClipBoundary boundary = candidates[representative.candidateStart + j].boundary;

                // One section a face of this registration: the caps made before this one in the same box (from
                // shareFrom) that are of this face and plane share theirs -- the other side's, another render
                // fragment's. Settled here, with the shapes; nothing is looked up when things are placed.
                int initialStart = -1, initial = 0;
                for (int k = shareFrom; k < set.capCount; k++)
                {
                    counts.sectionEntriesCompared++;
                    if (set.caps[k].boundary.face == boundary.face && math.all(set.caps[k].localPlane == _localPlanes[j]))
                    {
                        initialStart = set.caps[k].initialStart;
                        initial = set.caps[k].initialCount;
                        Array.Copy(set.vertices, initialStart, _initial, 0, initial);
                        counts.sectionsFoundHere++;
                        break;
                    }
                }

                if (initialStart < 0)
                {
                    SectionBuildCount++;
                    counts.sectionsBuilt++;

                    // The section in the local frame: the placement given is the identity, so its vertices stay local
                    // and it is ordered about the local plane's own normal.
                    if (!_section.TryBuild(
                            registration.localBounds, _localPlanes[j], Matrix4x4.identity, registration.vertexEpsilon, _initial, 0,
                            out initial, out _))
                    {
                        return Invalid(VpMultiCutInvalidInput.SectionNotTaken);
                    }

                    initialStart = set.vertexCount;
                    Array.Copy(_initial, 0, set.vertices, initialStart, initial);
                    set.vertexCount += initial;
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
                    counts.capClips++;
                    counts.capInputVertices += initial;
                    if (!VpCapPolygonClip.TryClip(
                            _initial, initial, boundary, _selectedCandidates, _selectedStates, _localSelectedPlanes,
                            registration.vertexEpsilon, _clipped, out clipped))
                    {
                        return Invalid(VpMultiCutInvalidInput.ClipNotTaken);
                    }
                }

                counts.capOutputVertices += clipped;
                int vertexStart = set.vertexCount;
                for (int v = 0; v < clipped; v++)
                {
                    if (!IsFinite(_clipped[v]))
                    {
                        return Invalid(VpMultiCutInvalidInput.DrawnCapVertex);
                    }

                    set.vertices[vertexStart + v] = _clipped[v];
                }

                set.vertexCount += clipped;
                set.caps[set.capCount++] = new LocalCapSet.Cap
                {
                    localPlane = _localPlanes[j],
                    boundary = boundary,
                    initialStart = initialStart,
                    initialCount = initial,
                    vertexStart = vertexStart,
                    vertexCount = clipped,
                };
                CapShapesBuilt++;
                CapShapeVertices += initial + clipped;
            }

            return VpMultiCutBuildOutcome.Built;
        }

        /// <summary>
        /// The caps of every render fragment of a part, made with the part (the structure of its family was just made):
        /// kept in the part, read by whichever snapshot holds it.
        /// </summary>
        private VpMultiCutBuildOutcome TryBuildLocalCaps(StructurePool.Part part, in VpMultiCutRegistration registration)
        {
            part.shapes.Clear();
            for (int i = 0; i < part.rendersCount; i++)
            {
                VpMultiCutBranch representative = part.branches[part.renders[i].branch];
                VpMultiCutBuildOutcome outcome = TryAppendLocalCaps(
                    part.shapes, i, 0, registration, representative, part.candidates, part.states, StructuralPlaceCounts);
                if (outcome != VpMultiCutBuildOutcome.Built)
                {
                    return outcome;
                }
            }

            return VpMultiCutBuildOutcome.Built;
        }
    }
}
