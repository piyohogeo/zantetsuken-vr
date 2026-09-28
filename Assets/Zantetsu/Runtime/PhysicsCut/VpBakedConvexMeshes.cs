using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// One character's bone-local convex collider meshes, made and baked once and then only read: what a crowd's slot
    /// keeps so that its next prepared cut takes them instead of baking them again. Nothing pose-dependent is in here --
    /// the pose goes into each prepared input's own B-rep copy and frames, never into these meshes.
    /// <para>
    /// Their lifetime is the existing hold count (<see cref="PhysicsShapeSource"/>): this object holds them until it is
    /// disposed, each prepared input made from them holds them through a source of its own, and whatever inherited one of
    /// them -- a published piece's collider -- holds that input's source. The meshes are destroyed when the last of these
    /// lets go, whichever it is.
    /// </para>
    /// <para>
    /// A prepared input is made from these only for the very bank and ranges they were baked from (checked value by
    /// value); any other input is refused, and the caller bakes afresh. Main thread only.
    /// </para>
    /// </summary>
    public sealed class VpBakedConvexMeshes : IDisposable
    {
        readonly Mesh[] meshes;
        readonly float3[][] bindPoints;
        readonly ConvexBrepRange[] ranges;
        readonly int[][] faceOffsets;
        readonly int[][] faceIndices;
        PhysicsShapeSource source;

        public int ConvexCount => meshes.Length;
        public int ColdCookCalls { get; }
        public bool IsDisposed => source == null;

        /// <summary>How many prepared inputs have been made from these, for measurement.</summary>
        public int Uses { get; private set; }

        /// <summary>Whether the meshes have been destroyed (every holder has let go).</summary>
        public bool IsReleased => released != null && released.IsReleased;

        readonly PhysicsShapeSource released;

        public unsafe VpBakedConvexMeshes(ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexRanges)
        {
            if (convexRanges == null || convexRanges.Count == 0) throw new ArgumentException("Validated bone-local convexes required", nameof(convexRanges));
            meshes = new Mesh[convexRanges.Count];
            bindPoints = new float3[convexRanges.Count][];
            ranges = new ConvexBrepRange[convexRanges.Count];
            faceOffsets = new int[convexRanges.Count][];
            faceIndices = new int[convexRanges.Count][];
            var owner = PhysicsShapeSource.OwnPreparedMeshes(meshes);
            try
            {
                ColdCookCalls = VpPreparedPhysicsInput.BakeConvexMeshes(bank, convexRanges, meshes, bindPoints);
                for (int c = 0; c < convexRanges.Count; c++)
                {
                    ConvexBrepRange r = ranges[c] = convexRanges[c];
                    faceOffsets[c] = new int[r.faceCount + 1];
                    for (int f = 0; f <= r.faceCount; f++) faceOffsets[c][f] = bank.faceOffsets[r.faceBase + f];
                    faceIndices[c] = new int[r.faceIndexCount];
                    for (int k = 0; k < r.faceIndexCount; k++) faceIndices[c][k] = bank.faceIndices[r.faceIndexBase + k];
                }
            }
            catch
            {
                owner.Release();
                throw;
            }

            // The constructor's hold stays: it is this object's, let go at Dispose.
            source = released = owner;
        }

        /// <summary>Whether this bank and these ranges are, value by value, the ones the meshes were baked from.</summary>
        public unsafe bool Matches(ConvexBrepBank bank, IReadOnlyList<ConvexBrepRange> convexRanges)
        {
            if (convexRanges == null || convexRanges.Count != ranges.Length) return false;
            for (int c = 0; c < ranges.Length; c++)
            {
                ConvexBrepRange a = ranges[c], b = convexRanges[c];
                if (a.vertexBase != b.vertexBase || a.vertexCount != b.vertexCount || a.faceBase != b.faceBase || a.faceCount != b.faceCount
                    || a.faceIndexBase != b.faceIndexBase || a.faceIndexCount != b.faceIndexCount) return false;
                for (int v = 0; v < a.vertexCount; v++)
                    if (!bank.vertices[a.vertexBase + v].Equals(bindPoints[c][v])) return false;
                for (int f = 0; f <= a.faceCount; f++)
                    if (bank.faceOffsets[a.faceBase + f] != faceOffsets[c][f]) return false;
                for (int k = 0; k < a.faceIndexCount; k++)
                    if (bank.faceIndices[a.faceIndexBase + k] != faceIndices[c][k]) return false;
            }

            return true;
        }

        // For a prepared input made from these: its own source, holding these meshes until its last user lets go.
        internal PhysicsShapeSource Borrow(out Mesh[] borrowedMeshes, out float3[][] borrowedBindPoints)
        {
            if (source == null) throw new ObjectDisposedException(nameof(VpBakedConvexMeshes));
            borrowedMeshes = meshes;
            borrowedBindPoints = bindPoints;
            Uses++;
            return PhysicsShapeSource.Borrowing(source);
        }

        /// <summary>Lets this object's hold go; the meshes go when every prepared input and piece made from them has let go too.</summary>
        public void Dispose()
        {
            if (source == null) return;
            PhysicsShapeSource s = source;
            source = null;
            s.Release();
        }
    }
}
