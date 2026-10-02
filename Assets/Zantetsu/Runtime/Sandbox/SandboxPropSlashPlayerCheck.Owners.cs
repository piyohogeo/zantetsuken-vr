using Unity.Mathematics;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // What the check reads of a fragment's owner (2026-09-30): safe for a display-only owner (the hull trial's members),
    // which has a Root but no shape and no body -- the summary and the per-frame counts once dereferenced its shape and
    // threw. Testable from the PlayMode tests.
    public static partial class SandboxPropSlashPlayerCheck
    {
        /// <summary>The convexes an owner carries: none for a display-only owner.</summary>
        internal static int ConvexCountOf(PhysicsFragmentOwner owner) => owner != null && owner.Shape != null && !owner.Shape.IsFreed ? owner.Shape.ConvexCount : 0;

        /// <summary>The centre of an owner's convexes where it stands; its Root's position for a display-only owner.</summary>
        internal static unsafe Vector3 PieceCentreOf(PhysicsFragmentOwner owner)
        {
            if (owner == null || owner.Root == null) return Vector3.zero;
            if (owner.Shape == null || owner.Shape.IsFreed) return owner.Root.transform.position;
            float4x4 toWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
            float3 sum = float3.zero;
            int count = 0;
            for (int c = 0; c < owner.Shape.ConvexCount; c++)
            {
                Zantetsu.ConvexCut.ConvexBrepRange range = owner.Shape.Convex(c);
                Zantetsu.ConvexCut.ConvexBrepBank bank = owner.Shape.BankOf(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    sum += math.transform(toWorld, bank.vertices[range.vertexBase + v]);
                    count++;
                }
            }

            return count > 0 ? (Vector3)(sum / count) : owner.Root.transform.position;
        }

        /// <summary>The lowest vertex of an owner's convexes where it stands; its Root's height for a display-only owner.</summary>
        internal static unsafe float LowestVertexYOf(PhysicsFragmentOwner owner)
        {
            if (owner == null || owner.Root == null) return float.PositiveInfinity;
            if (owner.Shape == null || owner.Shape.IsFreed) return owner.Root.transform.position.y;
            float4x4 toWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
            float lowest = float.PositiveInfinity;
            for (int c = 0; c < owner.Shape.ConvexCount; c++)
            {
                Zantetsu.ConvexCut.ConvexBrepRange range = owner.Shape.Convex(c);
                Zantetsu.ConvexCut.ConvexBrepBank bank = owner.Shape.BankOf(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    lowest = math.min(lowest, math.transform(toWorld, bank.vertices[range.vertexBase + v]).y);
                }
            }

            return lowest;
        }

        /// <summary>The lowest vertex itself; the Root's position for a display-only owner.</summary>
        internal static unsafe Vector3 LowestVertexOf(PhysicsFragmentOwner owner)
        {
            if (owner == null || owner.Root == null) return Vector3.zero;
            if (owner.Shape == null || owner.Shape.IsFreed) return owner.Root.transform.position;
            float4x4 toWorld = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
            float3 lowest = new float3(0f, float.PositiveInfinity, 0f);
            for (int c = 0; c < owner.Shape.ConvexCount; c++)
            {
                Zantetsu.ConvexCut.ConvexBrepRange range = owner.Shape.Convex(c);
                Zantetsu.ConvexCut.ConvexBrepBank bank = owner.Shape.BankOf(c);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 point = math.transform(toWorld, bank.vertices[range.vertexBase + v]);
                    if (point.y < lowest.y) lowest = point;
                }
            }

            return lowest;
        }
    }
}
