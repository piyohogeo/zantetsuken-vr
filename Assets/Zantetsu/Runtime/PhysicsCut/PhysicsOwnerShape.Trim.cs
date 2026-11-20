using System;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    public sealed unsafe partial class PhysicsOwnerShape
    {
        /// <summary>
        /// The shape of an owner whose convexes were trimmed (CutConvexTrim): the convexes of <paramref name="source"/> in
        /// their order, each as it was or -- where <paramref name="trimmed"/> has one for it (a vertex count above zero) --
        /// the trimmed B-rep with the mesh made and baked for it.
        /// <para>
        /// **A shape of its own, in a block of its own.** Every convex is copied in, as an authored shape's are: the trimmed
        /// ones from the attempt's room, which is only read during this call, and the others from the bank they live in. So
        /// nothing here addresses the source's block, and the source can be given up as soon as its owner has this shape.
        /// A convex left as it was keeps the mesh its collider already uses and takes a hold of its own on whoever owns that
        /// mesh; a trimmed one holds <paramref name="trimmedMeshes"/>.
        /// </para>
        /// <para>
        /// The boxes are carried as everywhere: a convex left as it was has the box it had, a trimmed one the box the
        /// attempt measured from its vertices (<paramref name="boxes"/>), widened by its mesh's bounds where usable.
        /// </para>
        /// </summary>
        internal static PhysicsOwnerShape Trimmed(
            PhysicsOwnerShape source, BrepBuffer[] trimmed, Mesh[] meshes, float3x2[] boxes, PhysicsShapeSource trimmedMeshes)
        {
            if (source == null || trimmed == null || meshes == null || boxes == null || trimmedMeshes == null)
            {
                throw new ArgumentNullException(source == null ? nameof(source) : trimmed == null ? nameof(trimmed) : meshes == null ? nameof(meshes) : boxes == null ? nameof(boxes) : nameof(trimmedMeshes));
            }

            int count = source.ConvexCount;
            if (source._freed || count == 0 || trimmed.Length != count || meshes.Length != count || boxes.Length != count)
            {
                throw new ArgumentException("one entry a convex of a live source", nameof(trimmed));
            }

            var shape = new PhysicsOwnerShape(source.LocalToOwner, count);
            try
            {
                var parts = new Part[count];
                bool any = false;
                for (int c = 0; c < count; c++)
                {
                    int at = source.TableIndex(c);
                    if (source.MeshFrameOf(c).HasFrame)
                    {
                        throw new ArgumentException("convex " + c + " has a mesh frame of its own: such a shape is not trimmed", nameof(source));
                    }

                    BrepBuffer b = trimmed[c];
                    if (b.V <= 0)
                    {
                        parts[c] = new Part
                        {
                            bank = source._banks[at], range = source._convexes[at], mesh = source._meshes[at],
                            source = source.SourceOf(c), lo = source._convexLo[at], hi = source._convexHi[at],
                        };
                        continue;
                    }

                    if (meshes[c] == null || b.V < 4 || b.F < 4 || b.E < 6 || b.I != 2 * b.E)
                    {
                        throw new ArgumentException("the trimmed convex " + c + " is not a closed convex with its mesh", nameof(trimmed));
                    }

                    any = true;
                    float3 lo = boxes[c].c0, hi = boxes[c].c1;
                    Bounds box = meshes[c].bounds;
                    float3 low = box.min, high = box.max;
                    if (math.all(math.isfinite(low))) lo = math.min(lo, low);
                    if (math.all(math.isfinite(high))) hi = math.max(hi, high);
                    parts[c] = new Part
                    {
                        bank = new ConvexBrepBank { vertices = b.v, faceOffsets = b.faceOff, faceIndices = b.faceIdx, faceEdges = b.faceEdge, edges = b.edges },
                        range = new ConvexBrepRange
                        {
                            vertexCount = b.V, faceCount = b.F, faceIndexCount = b.I, edgeCount = b.E, maxFaceLoop = ConvexTrimSearch.MaxFaceLoop(in b),
                        },
                        mesh = meshes[c], source = trimmedMeshes, lo = lo, hi = hi,
                    };
                }

                if (!any)
                {
                    throw new ArgumentException("no convex was trimmed: the shape would be the source's own", nameof(trimmed));
                }

                shape.Fill(parts);
                if (source._boneOf != null)
                {
                    shape._boneOf = new int[count];
                    for (int c = 0; c < count; c++) shape._boneOf[c] = source.ConvexBone(c);
                }
            }
            catch
            {
                shape.Dispose();
                throw;
            }

            return shape;
        }
    }
}
